using System.Security.Cryptography;
using System.Text;

namespace DcxManager.Services;

/// <summary>
/// Locks (0.5.1 #8–#10, ADR-067): a whole category (the Audit log, Credentials, Known hosts, Settings…) or one item (a
/// host, a host folder, a credential, a cloud account, an infrastructure provider, a portal, a note) can be locked, so
/// that opening, connecting, viewing, editing, copying or exporting it first needs one of:
/// <list type="bullet">
/// <item><b>uac</b> — Windows admin approval (the installed app is started elevated and asks; no admin rights, no access);</item>
/// <item><b>signin</b> — this Windows user signs in again (Windows Hello, or the account's password);</item>
/// <item><b>password</b> — a password of its own (a salted PBKDF2-SHA256 verifier, 600,000 rounds; wrong attempts throttle).</item>
/// </list>
/// Once unlocked, a lock stays open for <see cref="AppSettings.LockOpenMinutes"/> minutes (0 = asked every time) and
/// always closes again when the app or Windows locks. Locks the user sets live in the encrypted settings
/// (<see cref="AppSettings.UserLocks"/>, never a Group Policy: they carry password verifiers); a restore never removes
/// them. Group Policy can lock categories itself (<see cref="AppSettings.LockedCategories"/>, the Audit log by default
/// through <see cref="AppSettings.LockAuditLog"/>), and decides whether users may set their own
/// (<see cref="AppSettings.AllowUserLocks"/>, <see cref="AppSettings.AllowItemLocks"/>).
/// <para>What a lock protects against is someone else at this unlocked app. The data at rest is protected by the store's
/// own encryption either way; a lock adds a second, separate check before it is shown or used (SECURITY.md).</para>
/// <para>Add-ons and the local API never reach a locked item. The AI reaches one only while it is unlocked.</para>
/// </summary>
public static class Locks
{
    public const string Uac = "uac", SignIn = "signin", Password = "password";
    public const int Rounds = 600_000;
    private const int FreeTries = 3;
    private static readonly TimeSpan FirstWait = TimeSpan.FromSeconds(30), MaxWait = TimeSpan.FromMinutes(10);

    /// <summary>One lock the user set: what it locks, how it is opened, and (password locks) the verifier and throttle.</summary>
    public sealed class Rule
    {
        /// <summary>"cat:&lt;category&gt;", "host:&lt;id&gt;", "folder:&lt;path&gt;", "cred:&lt;id&gt;", "cloud:&lt;provider&gt;:&lt;account&gt;",
        /// "infra:&lt;id&gt;", "portal:&lt;id&gt;" or "note:&lt;id&gt;".</summary>
        public string Target { get; set; } = "";
        /// <summary>What it is called (shown in prompts and lists; the target's name when it was locked).</summary>
        public string Title { get; set; } = "";
        public string Mode { get; set; } = Uac;
        public string Salt { get; set; } = "";
        public string Hash { get; set; } = "";
        public int Rounds { get; set; } = Locks.Rounds;
        public int Failures { get; set; }
        public DateTime NotBeforeUtc { get; set; }
        public DateTime SetUtc { get; set; }
    }

    /// <summary>A rule in force: the user's own, or one Group Policy sets (<see cref="Managed"/>; it can't be removed here).</summary>
    public sealed record Effective(string Target, string Title, string Mode, bool Managed, Rule? User);

    /// <summary>A category that can be locked: its key, title, the sidebar screens it covers, and what it guards.</summary>
    public sealed record Category(string Key, string Title, string[] Screens, string Guards);

    /// <summary>Every lockable category, in the order Settings lists them.</summary>
    public static readonly IReadOnlyList<Category> Categories = new Category[]
    {
        new("audit", "Audit log", new[] { "audit" }, "the record of everything done in the app"),
        new("sessionlogs", "Session logs", new[] { "sessionlogs" }, "recorded terminal output"),
        new("errorlog", "Error log", new[] { "errorlog" }, "the app's own error log"),
        new("observe", "Observability", new[] { "observe" }, "log sinks and dashboards"),
        new("credentials", "Credentials", new[] { "credentials" }, "passwords and SSH keys"),
        new("team", "Team (shared libraries)", new[] { "library", "team:hosts", "team:credentials" }, "team hosts and team credentials"),
        new("knownhosts", "Known hosts", new[] { "knownhosts" }, "trusted server host keys"),
        new("hosts", "Hosts", new[] { "hosts" }, "the saved servers list"),
        new("infra", "Infrastructure", new[] { "infra" }, "hypervisors, Docker and NAS providers"),
        new("cloud", "Cloud providers", new[] { "cloud" }, "cloud accounts and their resources"),
        new("portals", "Portals", new[] { "portals" }, "saved web UIs"),
        new("beyondtrust", "BeyondTrust", new[] { "beyondtrust" }, "Jump Items and Password Safe"),
        new("bitwarden", "Bitwarden", new[] { "bitwarden" }, "vault items and Secrets Manager"),
        new("automation", "Automation", new[] { "ansible", "puppet" }, "Ansible and Puppet"),
        new("commander", "File commander", new[] { "commander" }, "the two-pane file manager"),
        new("notes", "Notes", new[] { "notes" }, "engineering notes"),
        new("incidents", "Incidents", new[] { "incidents" }, "incidents and work sessions"),
        new("snippets", "Snippets", new[] { "snippets" }, "saved commands and multi-run"),
        new("tunnels", "Tunnels", new[] { "tunnels" }, "SSH tunnels and VPN profiles"),
        new("addons", "Add-ons", new[] { "addons" }, "installed add-ons and their permissions"),
        new("privacy", "Privacy & network", new[] { "privacy" }, "the security posture and destinations"),
        new("settings", "Settings", new[] { "settings" }, "every setting"),
    };

    public static Category? FindCategory(string key) => Categories.FirstOrDefault(c => c.Key == key);

    /// <summary>The category a sidebar screen belongs to (cloud screens "vultr:Instances" belong to "cloud"), or null.</summary>
    public static string? CategoryOfScreen(string screen)
    {
        var c = Categories.FirstOrDefault(c => c.Screens.Contains(screen, StringComparer.Ordinal));
        if (c != null) return c.Key;
        return screen.Contains(':') && !screen.StartsWith("local:", StringComparison.Ordinal) && !screen.StartsWith("team:", StringComparison.Ordinal) ? "cloud" : null;
    }

    public static string ModeTitle(string mode) => mode switch { Uac => "Windows admin approval (UAC)", SignIn => "Windows sign-in (Hello or your password)", Password => "a password", _ => mode };

    // ---- the rules in force -------------------------------------------------------------------------------------------

    private static AppSettings S => AppSettings.Current;

    /// <summary>The lock mode Group Policy uses for the categories it locks: uac (default) or signin.</summary>
    public static string PolicyMode => S.LockPolicyMode is Uac or SignIn ? S.LockPolicyMode : Uac;

    /// <summary>The rule on exactly this target, or null.</summary>
    public static Effective? RuleOn(string target)
    {
        if (target.StartsWith("cat:", StringComparison.Ordinal))
        {
            var key = target[4..];
            bool managed = S.LockedCategories.Contains(key, StringComparer.OrdinalIgnoreCase) || key == "audit" && S.LockAuditLog;
            if (managed) return new Effective(target, FindCategory(key)?.Title ?? key, PolicyMode, true, null);
        }
        var r = S.UserLocks.FirstOrDefault(x => x.Target == target);
        return r == null ? null : new Effective(target, r.Title, r.Mode, false, r);
    }

    /// <summary>Every rule that guards <paramref name="target"/>: its own, and for a host every folder it sits in.</summary>
    public static List<Effective> Governing(string target)
    {
        var list = new List<Effective>();
        if (RuleOn(target) is { } own) list.Add(own);
        if (target.StartsWith("host:", StringComparison.Ordinal) && AppData.Current.FindHost(target[5..]) is { } h)
        {
            var folder = AppData.NormalizeFolder(h.Group);
            foreach (var r in S.UserLocks.Where(r => r.Target.StartsWith("folder:", StringComparison.Ordinal)))
                if (AppData.InFolder(folder, r.Target[7..])) list.Add(new Effective(r.Target, r.Title, r.Mode, false, r));
        }
        return list;
    }

    public static bool IsLocked(string target) => Governing(target).Count > 0;
    /// <summary>The padlock to show: "🔒" closed, "🔓" open, "" not locked.</summary>
    public static string Glyph(string target) => !IsLocked(target) ? "" : IsOpen(target) ? "🔓" : "🔒";
    public static bool IsCategoryLocked(string category) => RuleOn("cat:" + category) != null;

    /// <summary>The target's own rule (not inherited), for the padlock in lists: "own", "inherited" or null.</summary>
    public static string? Padlock(string target) => RuleOn(target) != null ? "own" : Governing(target).Count > 0 ? "inherited" : null;

    // ---- open (unlocked) state ----------------------------------------------------------------------------------------

    private static readonly Dictionary<string, DateTime> Open = new();
    private static readonly object Sync = new();

    /// <summary>The clock (the self-test moves it).</summary>
    internal static Func<DateTime> UtcNow = () => DateTime.UtcNow;

    /// <summary>Raised after locks change or close again (lists redraw their padlocks; open locked screens close).</summary>
    public static event Action? Changed;
    public static void RaiseChanged() => Changed?.Invoke();

    /// <summary>True when every rule guarding the target is open now (or there is none).</summary>
    public static bool IsOpen(string target)
    {
        var rules = Governing(target);
        if (rules.Count == 0) return true;
        lock (Sync) return rules.All(r => Open.TryGetValue(r.Target, out var until) && until > UtcNow());
    }

    /// <summary>Thrown when a locked category or item is used while it is closed (or by add-ons and the local API at all).</summary>
    public sealed class LockedException(string message) : UnauthorizedAccessException(message);

    /// <summary>Refuses (throws <see cref="LockedException"/>) when a lock guarding <paramref name="target"/> is closed. For
    /// code without a person in front of it: it never asks. <paramref name="name"/> is what the message calls it.</summary>
    public static void Demand(string target, string name)
    {
        if (Strict.Value is { } caller && IsLocked(target))
        {
            AuditLog.Write(caller, "locks", "refused: locked item", $"{name} ({target})");
            throw new LockedException($"“{name}” is locked; add-ons and the local API can't use locked items.");
        }
        if (IsOpen(target)) return;
        AuditLog.Write("app", "locks", "refused: locked", $"{name} ({target})");
        throw new LockedException($"“{name}” is locked. Unlock it in the app first (right-click → 🔒 Lock → Unlock for now).");
    }

    /// <summary>Set (to the caller, "addon:&lt;id&gt;") while an add-on or the local API runs: a locked item is never
    /// theirs, open or not (<see cref="Capabilities.RunAsync"/> sets it; flows with the async call).</summary>
    public static readonly AsyncLocal<string?> Strict = new();

    /// <summary>True when the item must not be shown in detail to the current caller: its padlock is closed, or the caller
    /// is an add-on or the local API (<see cref="Strict"/>) and the item is locked at all.</summary>
    public static bool Hidden(string target) => IsLocked(target) && (Strict.Value != null || !IsOpen(target));

    /// <summary>For add-ons and the local API outside a capability: refuses any locked item, open or not.</summary>
    public static void Refuse(string target, string caller)
    {
        if (!IsLocked(target)) return;
        AuditLog.Write(caller, "locks", "refused: locked item", target);
        throw new LockedException($"“{Governing(target).FirstOrDefault()?.Title ?? target}” is locked; add-ons and the local API can't use locked items.");
    }

    /// <summary>True when this one rule (by its target) is open now.</summary>
    public static bool IsOpenRule(string ruleTarget) { lock (Sync) return Open.TryGetValue(ruleTarget, out var until) && until > UtcNow(); }

    /// <summary>Marks a rule open for the configured time (0 minutes = this one use: open for 10 seconds).</summary>
    public static void MarkOpen(string ruleTarget)
    {
        var minutes = Math.Clamp(S.LockOpenMinutes, 0, 480);
        lock (Sync) Open[ruleTarget] = UtcNow() + (minutes == 0 ? TimeSpan.FromSeconds(10) : TimeSpan.FromMinutes(minutes));
    }

    /// <summary>Closes one open lock ("🔒 Lock again now").</summary>
    public static void CloseOne(string ruleTarget)
    {
        bool was;
        lock (Sync) was = Open.Remove(ruleTarget);
        if (was) { AuditLog.Write("you", "locks", "locked again", ruleTarget); Changed?.Invoke(); }
    }

    /// <summary>Closes every open lock (the app or Windows locked, "Lock everything now", the timer).</summary>
    public static void CloseAll(string why)
    {
        bool any;
        lock (Sync) { any = Open.Count > 0; Open.Clear(); }
        if (any) { AuditLog.Write("app", "locks", "locks closed", why); Changed?.Invoke(); }
    }

    /// <summary>Closes the locks whose time is up. Returns true when any closed.</summary>
    public static bool Expire()
    {
        List<string> gone;
        lock (Sync)
        {
            var now = UtcNow();
            gone = Open.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList();
            foreach (var k in gone) Open.Remove(k);
        }
        if (gone.Count > 0) Changed?.Invoke();
        return gone.Count > 0;
    }

    // ---- setting and removing -----------------------------------------------------------------------------------------

    public enum Result { Ok, Wrong, Wait, TooShort, NotAllowed, Managed, Exists }

    /// <summary>Whether the user may set or remove their own locks of this kind (Group Policy decides).</summary>
    public static string? WhyNotAllowed(string target) =>
        target.StartsWith("cat:", StringComparison.Ordinal)
            ? (S.AllowUserLocks ? null : "Your organisation doesn't allow setting your own locks (Group Policy: AllowUserLocks).")
            : (S.AllowItemLocks && S.AllowUserLocks ? null : "Your organisation doesn't allow locking single items (Group Policy: AllowItemLocks).");

    /// <summary>Adds a lock (the caller has asked whatever the old state needed). A password lock needs the password.</summary>
    public static Result Set(string target, string title, string mode, string? password = null)
    {
        if (WhyNotAllowed(target) != null) return Result.NotAllowed;
        if (RuleOn(target) is { Managed: true }) return Result.Managed;
        if (S.UserLocks.Any(r => r.Target == target)) return Result.Exists;
        var r = new Rule { Target = target, Title = title, Mode = mode is Uac or SignIn or Password ? mode : Uac, SetUtc = UtcNow() };
        if (r.Mode == Password)
        {
            if (password is not { Length: >= FeatureLock.MinLength }) return Result.TooShort;
            var salt = RandomNumberGenerator.GetBytes(16);
            r.Salt = Convert.ToBase64String(salt);
            r.Hash = Convert.ToBase64String(Derive(password, salt, Rounds));
        }
        S.UserLocks.Add(r);
        AppSettings.Save();
        AuditLog.Write("you", "locks", "lock set", $"{title} ({target}) · {ModeTitle(r.Mode)}");
        Changed?.Invoke();
        return Result.Ok;
    }

    /// <summary>Removes the user's own lock (the caller has unlocked it first). Locks set by policy stay.</summary>
    public static Result Remove(string target)
    {
        if (RuleOn(target) is { Managed: true }) return Result.Managed;
        if (WhyNotAllowed(target) != null) return Result.NotAllowed;
        var r = S.UserLocks.FirstOrDefault(x => x.Target == target);
        if (r == null) return Result.Ok;
        S.UserLocks.Remove(r);
        lock (Sync) Open.Remove(target);
        AppSettings.Save();
        AuditLog.Write("you", "locks", "lock removed", $"{r.Title} ({target})");
        Changed?.Invoke();
        return Result.Ok;
    }

    /// <summary>The item was deleted: its own lock goes with it (no approval needed; nothing is left to protect).</summary>
    public static void Forget(string target)
    {
        if (S.UserLocks.RemoveAll(r => r.Target == target) == 0) return;
        lock (Sync) Open.Remove(target);
        AppSettings.Save();
        Changed?.Invoke();
    }

    /// <summary>Drops locks whose item no longer exists (a deleted host or credential).</summary>
    public static int Prune(Func<string, bool> exists)
    {
        int n = S.UserLocks.RemoveAll(r => !r.Target.StartsWith("cat:", StringComparison.Ordinal) && !r.Target.StartsWith("folder:", StringComparison.Ordinal) && !exists(r.Target));
        if (n > 0) { AppSettings.Save(); Changed?.Invoke(); }
        return n;
    }

    // ---- password locks -----------------------------------------------------------------------------------------------

    public static TimeSpan WaitLeft(Rule r) => r.NotBeforeUtc > UtcNow() ? r.NotBeforeUtc - UtcNow() : TimeSpan.Zero;

    /// <summary>Checks a password lock. Wrong attempts throttle (3 free, then 30 s doubling to 10 minutes, kept in the
    /// encrypted settings so a restart doesn't reset them). Every attempt is audited, never with the text.</summary>
    public static Result Verify(Rule r, string password)
    {
        lock (Sync)
        {
            var now = UtcNow();
            if (r.NotBeforeUtc > now) { AuditLog.Write("you", "locks", "attempt refused: waiting after wrong passwords", r.Title); return Result.Wait; }
            bool ok;
            try { ok = CryptographicOperations.FixedTimeEquals(Derive(password, Convert.FromBase64String(r.Salt), r.Rounds), Convert.FromBase64String(r.Hash)); }
            catch { ok = false; }
            if (ok) { r.Failures = 0; r.NotBeforeUtc = default; }
            else
            {
                r.Failures++;
                if (r.Failures >= FreeTries) r.NotBeforeUtc = now + TimeSpan.FromTicks(Math.Min(MaxWait.Ticks, FirstWait.Ticks * (1L << Math.Min(20, r.Failures - FreeTries))));
            }
            try { AppSettings.Save(); } catch (Exception ex) { Log.Write("locks", ex); }
            AuditLog.Write("you", "locks", ok ? "unlocked with its password" : "wrong lock password", ok ? r.Title : $"{r.Title} · {r.Failures} wrong in a row");
            return ok ? Result.Ok : Result.Wrong;
        }
    }

    private static byte[] Derive(string password, byte[] salt, int rounds) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, rounds, HashAlgorithmName.SHA256, 32);

    /// <summary>Self-test: forget open state.</summary>
    internal static void ResetForTest() { lock (Sync) Open.Clear(); }
}
