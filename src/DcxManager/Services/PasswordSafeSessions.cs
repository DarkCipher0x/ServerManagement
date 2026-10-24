using System.Text.RegularExpressions;
using DcxManager.BeyondTrust;

namespace DcxManager.Services;

/// <summary>
/// Password Safe credentials piped into connections (0.5.0, plan #16; DarkCipher0x: "credentials from BeyondTrust piped into host
/// connections securely and encrypted"). A saved host can be linked to a managed account (only the ids and a label are
/// stored). Connecting it checks the password out for the configured minutes, hands it to the SSH / Remote Desktop session
/// as an in-memory credential (HostEntry.Transient: never saved, never in a file, a log or the audit trail), and checks it
/// back in when that tab closes, when the checkout runs out, or when the app exits. The password's only copy outside
/// Password Safe is in this process's memory for the session's lifetime.
/// </summary>
public static class PasswordSafeSessions
{
    public sealed record Checkout(long RequestId, string Label, string HostId, DateTime Until) { public override string ToString() => $"{Label}  ·  until {Until:HH:mm}"; }

    private static PasswordSafeClient? _client;
    private static readonly Dictionary<object, Checkout> ByView = new(ReferenceEqualityComparer.Instance);
    private static Timer? _timer;   // a thread-pool timer: the check-in needs no UI thread
    private static readonly Regex LinkRx = new(@"^(\d{1,18}):(\d{1,18})\|([^\p{Cc}]{1,200})$", RegexOptions.CultureInvariant);   // 0.6.17: no control characters in a label

    public static IReadOnlyList<Checkout> Active { get { lock (ByView) return ByView.Values.ToList(); } }

    /// <summary>The value stored on a host for a managed account.</summary>
    public static string Link(ManagedAccount a) => $"{a.SystemId}:{a.AccountId}|{a.SystemName}\\{a.AccountName}";

    public static (long System, long Account, string Label)? Parse(string link) =>
        LinkRx.Match(link ?? "") is { Success: true } m ? (long.Parse(m.Groups[1].Value), long.Parse(m.Groups[2].Value), m.Groups[3].Value) : null;

    private static PasswordSafeClient Client()
    {
        var s = AppSettings.Current;
        if (!s.BeyondTrustEnabled) throw new InvalidOperationException("The BeyondTrust integration is off (Settings → Integrations → BeyondTrust).");
        if (!s.BeyondTrustAllowCheckout) throw new InvalidOperationException("Checking out Password Safe credentials is switched off on this PC (policy).");
        return _client ??= new PasswordSafeClient(s.BeyondTrustPsHost, s.ObserveSecrets.GetValueOrDefault("bt:pskey") ?? throw new InvalidOperationException("No Password Safe API key yet: Settings → Integrations → BeyondTrust."), s.BeyondTrustPsRunAs, s.ObserveSecrets.GetValueOrDefault("bt:pspwd"));
    }

    /// <summary>For the self-test: a client on a fake server.</summary>
    internal static void UseForTest(PasswordSafeClient? c) { _client = c; }

    /// <summary>The managed accounts this API user may request (names and ids only), for the credential picker (0.6.19).</summary>
    public static Task<List<ManagedAccount>> AccountsAsync(CancellationToken ct = default) => Client().AccountsAsync(ct);

    /// <summary>Checks the host's linked account out and returns an in-memory copy of the host carrying it (or the host
    /// itself when it isn't linked). The caller opens the session and calls <see cref="Attach"/> with its view.</summary>
    public static async Task<(HostEntry Host, Checkout? Checkout)> PrepareAsync(HostEntry host, CancellationToken ct = default)
    {
        if (Parse(host.PasswordSafeAccount) is not { } link || host.Transient != null) return (host, null);
        var s = AppSettings.Current;
        var account = new ManagedAccount(link.System, link.Account, link.Label.Split('\\')[0], link.Label.Contains('\\') ? link.Label[(link.Label.IndexOf('\\') + 1)..] : link.Label, "");
        // 0.5.1 #2: the reason, when the checkout policy asks for one (it goes to Password Safe's request and the audit log).
        var reason = $"Session to {host.Display} from ServerManagement";
        if (s.BeyondTrustAskReason)
        {
            var why = Ui.Dialogs.Prompt("Password Safe checkout", $"Why do you need {link.Label} on {host.Display}? Password Safe records the reason with the request.", "");
            if (string.IsNullOrWhiteSpace(why)) throw new OperationCanceledException("no reason given for the checkout");
            reason = Redaction.Mask(why.Trim().Length > 200 ? why.Trim()[..200] : why.Trim()) + $" ({host.Display}, ServerManagement)";
        }
        var (id, pw) = await Client().CheckOutAsync(account, s.BeyondTrustCheckoutMinutes, reason, ct);
        var h = host.Clone();
        if (h.Protocol == "ssh" || h.Username.Length == 0) h.Username = account.AccountName;
        h.Auth = AuthMethod.Password;
        h.Transient = new Credential { Name = link.Label + " (Password Safe, checked out)", Kind = CredentialKind.Password, Username = h.Username, Secret = pw };
        if (h.Protocol == "rdp") h.Rdp.SignOn = "password";
        AuditLog.Write("you", host.Display, "Password Safe credential checked out for a session", $"{link.Label} for {s.BeyondTrustCheckoutMinutes} min · {reason}");
        return (h, new Checkout(id, link.Label, host.Id, DateTime.Now.AddMinutes(s.BeyondTrustCheckoutMinutes)));
    }

    // ---- vault credentials kept in Password Safe (0.6.17, ADR-096) ---------------------------------------------------

    /// <summary>The owner of a vault credential's checkout (in place of a session view).</summary>
    private sealed class LeaseKey(string link) { public string Link { get; } = link; }
    private static readonly Dictionary<string, (LeaseKey Key, string User, string Password)> Leases = new();
    private static readonly SemaphoreSlim LeaseGate = new(1, 1);

    /// <summary>The account's password for a vault credential kept in Password Safe: one checkout shared by every use
    /// (SSH, Files, infrastructure, portals…) until it runs out, then checked in like a session's (timer, the Password
    /// Safe screen, app exit). Unattended uses (<paramref name="interactive"/> false) only reuse an open checkout.</summary>
    public static async Task<(string User, string Password)> LeaseAsync(string link, string purpose, bool interactive, CancellationToken ct = default)
    {
        var p = Parse(link) ?? throw new InvalidOperationException("The Password Safe link isn't valid: add the account again (VAULT → BeyondTrust).");
        await LeaseGate.WaitAsync(ct);
        try
        {
            lock (ByView)
                if (Leases.TryGetValue(link, out var open) && ByView.TryGetValue(open.Key, out var held) && held.Until > DateTime.Now.AddMinutes(1)) return (open.User, open.Password);
            if (!interactive)
                throw new InvalidOperationException($"No Password Safe checkout is open for {p.Label}, and background checks never start one: use it once and they reuse that checkout until it runs out.");
            var s = AppSettings.Current;
            var account = new ManagedAccount(p.System, p.Account, p.Label.Split('\\')[0], p.Label.Contains('\\') ? p.Label[(p.Label.IndexOf('\\') + 1)..] : p.Label, "");
            var reason = $"{purpose} from ServerManagement";
            if (s.BeyondTrustAskReason)
            {
                var why = Ui.Thread.Invoke(() => Ui.Dialogs.Prompt("Password Safe checkout", $"Why do you need {p.Label} ({purpose})? Password Safe records the reason with the request.", ""));
                if (string.IsNullOrWhiteSpace(why)) throw new OperationCanceledException("no reason given for the checkout");
                reason = Redaction.Mask(why.Trim().Length > 200 ? why.Trim()[..200] : why.Trim()) + $" ({purpose}, ServerManagement)";
            }
            var (id, pw) = await Client().CheckOutAsync(account, s.BeyondTrustCheckoutMinutes, reason, ct);
            var key = new LeaseKey(link);
            lock (ByView) Leases[link] = (key, account.AccountName, pw);
            Attach(key, new Checkout(id, p.Label, "", DateTime.Now.AddMinutes(s.BeyondTrustCheckoutMinutes)));
            AuditLog.Write("you", "beyondtrust", "Password Safe credential checked out for a vault credential", $"{p.Label} for {s.BeyondTrustCheckoutMinutes} min · {reason}");
            return (account.AccountName, pw);
        }
        finally { LeaseGate.Release(); }
    }

    /// <summary>App lock: the vault credentials' checkouts go back in (sessions keep theirs until their tabs close).</summary>
    public static void CheckInLeases(string why)
    {
        foreach (var (v, _) in Snapshot().Where(kv => kv.Key is LeaseKey)) _ = CheckInAsync(v, why);
    }

    /// <summary>Ties a checkout to the view that uses it (checked in when the view's tab closes).</summary>
    public static void Attach(object view, Checkout c)
    {
        lock (ByView) ByView[view] = c;
        _timer ??= new Timer(_ => { foreach (var (v, x) in Snapshot().Where(kv => kv.Value.Until <= DateTime.Now)) _ = CheckInAsync(v, "the checkout ran out"); }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    private static List<KeyValuePair<object, Checkout>> Snapshot() { lock (ByView) return ByView.ToList(); }

    /// <summary>A tab closed: if its view held a checkout, check it in.</summary>
    public static void Closed(object view) { bool has; lock (ByView) has = ByView.ContainsKey(view); if (has) _ = CheckInAsync(view, "the session closed"); }

    public static async Task CheckInAsync(object view, string why)
    {
        Checkout? c;
        lock (ByView)
        {
            if (!ByView.Remove(view, out c)) return;
            if (view is LeaseKey lk && Leases.TryGetValue(lk.Link, out var l) && ReferenceEquals(l.Key, lk)) Leases.Remove(lk.Link);   // the password goes with it
        }
        try
        {
            await Client().CheckInAsync(c.RequestId, "Done (" + why + ", ServerManagement)");
            AuditLog.Write("app", "beyondtrust", "Password Safe credential checked in", $"{c.Label}: {why}");
        }
        catch (Exception ex) { Log.Write("Password Safe check-in", ex); }
    }

    /// <summary>Checks a checkout in that never got a session (the tab didn't open, or the host opened outside the app).</summary>
    public static async Task ReleaseAsync(Checkout c, string why)
    {
        try { await Client().CheckInAsync(c.RequestId, "Done (" + why + ", ServerManagement)"); AuditLog.Write("app", "beyondtrust", "Password Safe credential checked in", $"{c.Label}: {why}"); }
        catch (Exception ex) { Log.Write("Password Safe check-in", ex); }
    }

    /// <summary>Checks in the checkout with this request id (Password Safe screen → Check in).</summary>
    public static Task CheckInRequestAsync(long requestId) =>
        Snapshot().FirstOrDefault(kv => kv.Value.RequestId == requestId) is { Key: { } v } ? CheckInAsync(v, "checked in by you") : Task.CompletedTask;

    /// <summary>App exit: every outstanding checkout back in (a few seconds at most), then sign out.</summary>
    public static void CheckInAll()
    {
        var all = Snapshot();
        var client = _client;
        if (client == null) return;
        try
        {
            Task.Run(async () =>
            {
                foreach (var (v, _) in all) await CheckInAsync(v, "the app closed");
                await client.SignOutAsync();
            }).Wait(TimeSpan.FromSeconds(8));
        }
        catch (Exception ex) { Log.Write("Password Safe exit", ex); }
    }
}
