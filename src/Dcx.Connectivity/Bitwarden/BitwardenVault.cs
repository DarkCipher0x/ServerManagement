using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using DcxManager.Services;
using static DcxManager.Bitwarden.BitwardenCli;

namespace DcxManager.Bitwarden;

/// <summary>
/// The Bitwarden Password Manager vault through <c>bw</c> (0.6.14, ADR-093). Signing in uses your <b>personal API key</b>
/// (client id and secret, kept encrypted in the app's settings), so no second-factor or captcha prompt can block a
/// program; unlocking takes your <b>master password</b>, asked when needed and never stored. bw decrypts the vault itself;
/// the app holds only bw's session key, in memory, until the vault locks again: after
/// <see cref="AppSettings.BitwardenLockMinutes"/> without use, when the app locks, and at exit.
/// Item lists keep names and ids only; a password or SSH key is read one item at a time, for the session that needs it.
/// Unlocking and reading work offline from bw's encrypted cache; signing in and syncing are network calls that offline
/// mode governs.
/// </summary>
public static class BitwardenVault
{
    public sealed record Status(string State, string Email, string Server, DateTime? LastSync)
    {
        public bool IsUnlocked => State == "unlocked";
        public string Text => State switch
        {
            "unlocked" => $"● Unlocked ({Email}, {Server})" + (LastSync is { } t ? $", synced {t.ToLocalTime():dd MMM HH:mm}" : ""),
            "locked" => $"○ Locked ({Email}, {Server})",
            _ => "○ Not signed in" + (Server.Length > 0 ? $" ({Server})" : ""),
        };
    }

    private static string? _session;   // bw's session key: this process's memory only
    /// <summary>0.6.20 (DarkCipher0x: "the bitwarden search is very very slow"): every search started bw again, which
    /// decrypts the whole vault each time. The list (names, user names, addresses; never a secret) is now read once per
    /// unlock and after each sync, and searched in memory; it goes when the vault locks.</summary>
    private static volatile List<VaultItem>? _items;
    private static Task? _warm;
    private static DateTime _lastUse;
    private static Timer? _idle;
    private static readonly SemaphoreSlim Gate = new(1, 1);   // one bw at a time (it writes its cache file)
    private static readonly Regex IdRx = new(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\z", RegexOptions.CultureInvariant);

    /// <summary>The vault was unlocked or locked.</summary>
    public static event Action? Changed;
    /// <summary>The held item list was read again (after an unlock or a sync): open lists may refresh.</summary>
    public static event Action? ItemsChanged;
    public static bool Unlocked => _session != null;

    private static AppSettings S => AppSettings.Current;

    /// <summary>The server's address for a <see cref="AppSettings.BitwardenServer"/> value.</summary>
    public static string ServerUrl(string setting) => setting.Trim() switch
    {
        "" => "https://vault.bitwarden.com",
        "eu" => "https://vault.bitwarden.eu",
        var u => u.TrimEnd('/'),
    };

    public static string ServerLabel(string setting) => setting.Trim() switch { "" => "bitwarden.com", "eu" => "bitwarden.eu", var u => Offline.HostOf(u) };

    public static bool IsId(string id) => IdRx.IsMatch(id ?? "");

    internal static void Enabled()
    {
        if (!S.BitwardenEnabled) throw new InvalidOperationException("The Bitwarden integration is off (Settings → Integrations → Bitwarden, needs admin approval).");
        if (ProtectedSettings.BitwardenServerCheck(S.BitwardenServer) is { } bad) throw new InvalidOperationException("Bitwarden server: " + bad);
    }

    private static Dictionary<string, string> Session() => _session is { } k ? new() { ["BW_SESSION"] = k } : new();

    private static async Task<Result> Bw(IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? env = null, int seconds = 45, CancellationToken ct = default)
    {
        var r = await RunAsync(Tool.Bw, args, env ?? Session(), TimeSpan.FromSeconds(seconds), ct);
        if (r.ExitCode != 0) throw new InvalidOperationException(Problem(Tool.Bw, r));
        return r;
    }

    private static bool SameServer(string reported, string want)
    {
        static string N(string u) => u.Trim().TrimEnd('/').Replace("://bitwarden.com", "://vault.bitwarden.com", StringComparison.OrdinalIgnoreCase).ToLowerInvariant();
        return N(reported.Length == 0 ? "https://vault.bitwarden.com" : reported) == N(want);
    }

    /// <summary>bw's view: not signed in, locked or unlocked, which account and server, the last sync.</summary>
    public static async Task<Status> StatusAsync(CancellationToken ct = default)
    {
        Enabled();
        await Gate.WaitAsync(ct);
        try { return await StatusCoreAsync(ct); }
        finally { Gate.Release(); }
    }

    private static async Task<Status> StatusCoreAsync(CancellationToken ct)
    {
        var r = await Bw(new[] { "status" }, ct: ct);
        using var d = JsonDocument.Parse(r.Output);
        var e = d.RootElement;
        string Str(string n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        DateTime? sync = DateTime.TryParse(Str("lastSync"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t : null;
        var st = new Status(Str("status") is { Length: > 0 } s ? s : "unauthenticated", Str("userEmail"), Str("serverUrl"), sync);
        if (!st.IsUnlocked && _session != null) { _session = null; _items = null; Changed?.Invoke(); }   // bw locked itself (or was locked from a terminal)
        return st;
    }

    /// <summary>Signs in with the personal API key when needed (against the configured server), then unlocks with the
    /// master password. The password goes to bw as an environment variable of that one process and is not kept.</summary>
    public static async Task UnlockAsync(string masterPassword, CancellationToken ct = default)
    {
        Enabled();
        if (masterPassword.Length == 0) throw new ArgumentException("Enter your master password.");
        await Gate.WaitAsync(ct);
        try
        {
            var want = ServerUrl(S.BitwardenServer);
            var st = await StatusCoreAsync(ct);
            if (st.State != "unauthenticated" && !SameServer(st.Server, want))
            {
                await Bw(new[] { "logout" }, ct: ct);   // bw changes server only while signed out
                _items = null; if (Interlocked.Exchange(ref _session, null) != null) Changed?.Invoke();
                st = st with { State = "unauthenticated", Server = "" };
            }
            if (st.State == "unauthenticated")
            {
                await Offline.EnforceAsync(want, "Bitwarden sign-in", ct);
                var id = S.ObserveSecrets.GetValueOrDefault("bw:clientid") ?? "";
                var secret = S.ObserveSecrets.GetValueOrDefault("bw:clientsecret") ?? "";
                if (id.Length == 0 || secret.Length == 0) throw new InvalidOperationException("Add your personal API key first: Settings → Integrations → Bitwarden (web vault → Settings → Security → Keys → View API key).");
                if (!SameServer(st.Server, want)) await Bw(new[] { "config", "server", want }, new Dictionary<string, string>(), ct: ct);
                await Bw(new[] { "login", "--apikey" }, new Dictionary<string, string> { ["BW_CLIENTID"] = id, ["BW_CLIENTSECRET"] = secret }, 90, ct);
                AuditLog.Write("you", "bitwarden", "signed in to Bitwarden", ServerLabel(S.BitwardenServer));
            }
            var r = await Bw(new[] { "unlock", "--passwordenv", "BW_PASSWORD", "--raw" }, new Dictionary<string, string> { ["BW_PASSWORD"] = masterPassword }, 90, ct);
            var key = r.Output.Trim();
            if (key.Length is < 16 or > 512 || key.Any(char.IsWhiteSpace)) throw new InvalidOperationException("Bitwarden CLI: unlocking didn't return a session key.");
            _session = key;
            Touch();
            AuditLog.Write("you", "bitwarden", "Bitwarden vault unlocked", $"{ServerLabel(S.BitwardenServer)}, locks after {S.BitwardenLockMinutes} min unused");
        }
        finally { Gate.Release(); }
        Changed?.Invoke();
        // 0.6.20: the unlock returns now. The list is read from bw's local cache straight away (so a search is instant),
        // then the vault syncs and the list is read again, all in the background.
        var first = _firstList = Task.Run(() => ReloadItemsAsync());
        _warm = Task.Run(() => WarmAsync(first));
    }

    private static Task<List<VaultItem>>? _firstList;

    private static async Task WarmAsync(Task<List<VaultItem>> first)
    {
        try
        {
            await first;
            if (S.BitwardenSyncOnUnlock && !Offline.On) await SyncAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or OperationCanceledException or IOException or JsonException or System.ComponentModel.Win32Exception)
        { Log.Write("Bitwarden list / sync after unlock", ex); }
    }

    /// <summary>Self-test: the background read and sync after the last unlock.</summary>
    internal static Task WarmForTest => _warm ?? Task.CompletedTask;

    /// <summary>Fetches the latest vault from the server (into bw's encrypted cache).</summary>
    public static async Task SyncAsync(CancellationToken ct = default)
    {
        Enabled();
        await Offline.EnforceAsync(ServerUrl(S.BitwardenServer), "Bitwarden sync", ct);
        await Gate.WaitAsync(ct);
        try { Need(); await Bw(new[] { "sync" }, seconds: 120, ct: ct); Touch(); }
        finally { Gate.Release(); }
        await ReloadItemsAsync(ct);   // what the sync brought in shows in searches at once
    }

    private static void Need() { if (_session == null) throw new InvalidOperationException("The Bitwarden vault is locked: unlock it first."); }

    private static void Touch()
    {
        _lastUse = DateTime.UtcNow;
        _idle ??= new Timer(_ => IdleCheck(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    private static void IdleCheck()
    {
        if (_session != null && DateTime.UtcNow - _lastUse >= TimeSpan.FromMinutes(Math.Clamp(S.BitwardenLockMinutes, 1, 480))) Lock($"{S.BitwardenLockMinutes} min unused");
    }

    /// <summary>The vault's logins and SSH keys (optionally only those matching <paramref name="search"/>): names, user
    /// names and addresses only. bw's answer includes the passwords; they are dropped here, while reading it.</summary>
    public static async Task<List<VaultItem>> ItemsAsync(string search = "", CancellationToken ct = default)
    {
        Enabled();
        search = search.Trim();
        if (search.Length > 100 || search.Any(char.IsControl)) throw new ArgumentException("Search for up to 100 characters.");
        // Just unlocked: the first read is already under way, so it is waited for rather than started twice.
        if (_items == null && _firstList is { IsCompleted: false } running)
            try { await running.WaitAsync(ct); } catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or JsonException) { }
        if (_items is not { } all || _session == null) all = await ReloadItemsAsync(ct);
        else { Need(); Touch(); }
        return Search(all, search);
    }

    /// <summary>Reads the whole list from bw (its local, encrypted cache) and holds it until the vault locks.</summary>
    public static async Task<List<VaultItem>> ReloadItemsAsync(CancellationToken ct = default)
    {
        Enabled();
        List<VaultItem> list;
        await Gate.WaitAsync(ct);
        try
        {
            Need();
            var r = await Bw(new[] { "list", "items" }, seconds: 90, ct: ct);
            Touch();
            list = ParseItems(r.Output);
            if (_session != null) _items = list;   // not when it locked meanwhile
        }
        finally { Gate.Release(); }
        ItemsChanged?.Invoke();
        return list;
    }

    /// <summary>Whether the list is held (a search answers without starting bw).</summary>
    public static bool ItemsHeld => _items != null && _session != null;

    /// <summary>Every word somewhere in the name, user name or address, any order and case (what bw's own --search
    /// matched, without starting it).</summary>
    public static List<VaultItem> Search(IEnumerable<VaultItem> all, string search)
    {
        var words = search.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 0 ? all.ToList()
            : all.Where(i => words.All(w => i.Name.Contains(w, StringComparison.OrdinalIgnoreCase) || i.Username.Contains(w, StringComparison.OrdinalIgnoreCase) || i.Uri.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    internal static List<VaultItem> ParseItems(string json)
    {
        var list = new List<VaultItem>();
        using var d = JsonDocument.Parse(json);
        if (d.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("not a list of items");
        foreach (var e in d.RootElement.EnumerateArray())
            if (Item(e) is { } i) list.Add(i);
        return list.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static string Str(JsonElement e, string n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>An item's type number, or 0 (an answer is untrusted: anything that isn't an object with a number is 0).</summary>
    private static int TypeOf(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetInt32(out var n) ? n : 0;

    private static VaultItem? Item(JsonElement e)
    {
        int type = TypeOf(e);
        var id = Str(e, "id");
        if (type is not (1 or 5) || !IsId(id)) return null;   // secure notes, cards and identities are never listed
        var login = e.TryGetProperty("login", out var l) ? l : default;
        var key = e.TryGetProperty("sshKey", out var k) ? k : default;
        var uri = login.ValueKind == JsonValueKind.Object && login.TryGetProperty("uris", out var us) && us.ValueKind == JsonValueKind.Array
            ? us.EnumerateArray().Select(u => Str(u, "uri")).FirstOrDefault(u => u.Length > 0) ?? "" : "";
        return new VaultItem(id, Str(e, "name"), type, Str(login, "username"), uri, Str(e, "organizationId"),
            Str(login, "password").Length > 0, Str(key, "keyFingerprint"));
    }

    /// <summary>One item's password or SSH private key, for the use that asked (a session, a copy).</summary>
    public static async Task<VaultSecret> SecretAsync(string id, CancellationToken ct = default)
    {
        Enabled();
        if (!IsId(id)) throw new ArgumentException("Not a Bitwarden item id.");
        await Gate.WaitAsync(ct);
        try
        {
            Need();
            var r = await Bw(new[] { "get", "item", id }, ct: ct);
            Touch();
            return ParseSecret(r.Output);
        }
        finally { Gate.Release(); }
    }

    internal static VaultSecret ParseSecret(string json)
    {
        using var d = JsonDocument.Parse(json);
        var e = d.RootElement;
        int type = TypeOf(e);
        var name = Str(e, "name");
        if (type == 5 && e.TryGetProperty("sshKey", out var k) && Str(k, "privateKey") is { Length: > 0 } pk)
            return new VaultSecret(name, "", pk, true);
        if (type == 1 && e.TryGetProperty("login", out var l))
        {
            var pw = Str(l, "password");
            if (pw.Length == 0) throw new InvalidOperationException($"“{name}” has no password.");
            return new VaultSecret(name, Str(l, "username"), pw, pw.TrimStart().StartsWith("-----BEGIN", StringComparison.Ordinal), Str(l, "totp"));
        }
        throw new InvalidOperationException($"“{name}” is not a login or an SSH key.");
    }

    /// <summary>Locks the vault: bw forgets its unlocked keys and the app its session key.</summary>
    public static void Lock(string why)
    {
        var key = Interlocked.Exchange(ref _session, null); _items = null;
        if (key == null) return;
        AuditLog.Write("app", "bitwarden", "Bitwarden vault locked", why);
        Changed?.Invoke();
        _ = Task.Run(async () =>
        {
            try { await RunAsync(Tool.Bw, new[] { "lock" }, new Dictionary<string, string> { ["BW_SESSION"] = key }, TimeSpan.FromSeconds(20)); }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException or System.ComponentModel.Win32Exception) { Log.Write("Bitwarden lock", ex); }
        });
    }

    /// <summary>App exit: locked before the process ends (a few seconds at most).</summary>
    public static void LockAtExit()
    {
        var key = Interlocked.Exchange(ref _session, null); _items = null;
        if (key == null) return;
        try { Task.Run(() => RunAsync(Tool.Bw, new[] { "lock" }, new Dictionary<string, string> { ["BW_SESSION"] = key }, TimeSpan.FromSeconds(5))).Wait(TimeSpan.FromSeconds(6)); }
        catch (Exception ex) { Log.Write("Bitwarden lock at exit", ex); }
    }

    /// <summary>Signs out and removes bw's cache folder of this app (the vault on the server is untouched).</summary>
    public static async Task SignOutAsync(CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var had = Interlocked.Exchange(ref _session, null) != null; _items = null;
            try { await RunAsync(Tool.Bw, new[] { "logout" }, new Dictionary<string, string>(), TimeSpan.FromSeconds(30), ct); }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException) { Log.Write("Bitwarden sign-out", ex); }
            try { if (Directory.Exists(DataDir)) Directory.Delete(DataDir, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Write("Bitwarden cache folder", ex); }
            AuditLog.Write("you", "bitwarden", "signed out of Bitwarden", "the app's bw cache removed");
            if (had) Changed?.Invoke();
        }
        finally { Gate.Release(); }
    }

    /// <summary>Self-test: forget the session without calling bw.</summary>
    internal static void ResetForTest() { _session = null; _items = null; _warm = null; _firstList = null; _idle?.Dispose(); _idle = null; }
    /// <summary>Self-test: pretend the last use was this long ago (the idle lock).</summary>
    internal static void AgeForTest(TimeSpan ago) => _lastUse = DateTime.UtcNow - ago;
    /// <summary>Self-test: run the idle check now.</summary>
    internal static void IdleCheckForTest() => IdleCheck();
}
