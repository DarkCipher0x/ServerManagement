using System.Net.Http;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using DcxManager.BeyondTrust;
using DcxManager.Infra;
using DcxManager.Services;

namespace DcxManager.Views;

// SettingsView: Integrations → BeyondTrust (0.5.1 #2). Its own page of cards instead of one card in Automation:
// connection (PRA / Remote Support), Password Safe, checkout policy, session behaviour, TLS pinning, what the integration
// may do, and its audit. Hosts, pins and switches are admin-protected; secrets stay in the encrypted settings
// (ObserveSecrets) and are never policies; every other field has a Group Policy and greys out when one sets it.
public sealed partial class SettingsView
{
    /// <summary>The cards of the BeyondTrust page, in order (Integrations → BeyondTrust).</summary>
    private IEnumerable<(string Category, Func<Border> Make)> BeyondTrustCards() => new (string, Func<Border>)[]
    {
        ("Integrations", BeyondTrustOverviewCard), ("Integrations", BeyondTrustConnectionCard), ("Integrations", BeyondTrustPasswordSafeCard),
        ("Integrations", BeyondTrustCheckoutCard), ("Integrations", BeyondTrustSessionsCard), ("Integrations", BeyondTrustTlsCard),
        ("Integrations", BeyondTrustScopesCard), ("Integrations", BeyondTrustAuditCard),
    };

    private TextBlock Result() => new() { Style = (Style)FindResource("Caption"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };

    private Border BeyondTrustOverviewCard()
    {
        var state = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) };
        void Show()
        {
            state.Text = (_s.BeyondTrustEnabled ? "● On." : "○ Off.")
                + $" PRA / Remote Support: {(_s.BeyondTrustPraSite.Length > 0 ? _s.BeyondTrustPraSite : "not set up")}{(_s.ObserveSecrets.ContainsKey("bt:pra") ? " (API secret stored)" : "")}."
                + $" Password Safe: {(_s.BeyondTrustPsHost.Length > 0 ? _s.BeyondTrustPsHost : "not set up")}{(_s.ObserveSecrets.ContainsKey("bt:pskey") ? " (API key stored)" : "")}."
                + $" Checkouts {(_s.BeyondTrustAllowCheckout ? $"allowed, {_s.BeyondTrustCheckoutMinutes} min" : "off")}.";
        }
        Show();
        state.IsVisibleChanged += (_, e) => { if (e.NewValue is true) Show(); };
        return Card("BeyondTrust",
            "Privileged Remote Access / Remote Support (read-only: Jump Items, sessions, Jumpoints, Vault account names; a Jump Item opens in your own access console) and Password Safe (a managed account checked out for one session, in memory, then checked in). " +
            "Secrets are kept encrypted here, never in a policy. Sidebar → SERVERS → BeyondTrust.",
            ProtectedCheck("bt.enabled", "BeyondTrust integration", async want =>
            {
                var ok = await ProtectedSettings.ChangeAsync((want ? "turn on: " : "turn off: ") + "BeyondTrust integration", "bt.enabled", want);
                MainWindow.Instance?.BuildNav();   // SERVERS → BeyondTrust appears / goes at once
                Show();
                return ok;
            }),
            state,
            Row(Btn("Open BeyondTrust", () => MainWindow.Instance.Navigate("beyondtrust"))));
    }

    private Border BeyondTrustConnectionCard()
    {
        var pra = Bound(new TextBox { Text = _s.BeyondTrustPraSite, Width = FieldWidth, ToolTip = "https://your-pra-appliance (Privileged Remote Access or Remote Support)" }, nameof(AppSettings.BeyondTrustPraSite));
        var cid = Bound(new TextBox { Text = _s.BeyondTrustPraClientId, Width = FieldWidth, ToolTip = "The API account's OAuth client id (/login → Management → API Configuration)" }, nameof(AppSettings.BeyondTrustPraClientId));
        var secret = new PasswordBox { Width = FieldWidth, ToolTip = _s.ObserveSecrets.ContainsKey("bt:pra") ? "A secret is stored (encrypted). Leave empty to keep it." : "The API account's OAuth client secret" };
        var result = Result();
        async Task Apply()
        {
            if (PraClient.SiteProblem(pra.Text) is { } bad && pra.Text.Trim().Length > 0) { result.Text = "✕ " + bad; return; }
            if (!await ApplyProtected("change the BeyondTrust PRA site", new Dictionary<string, JsonNode?> { ["bt.praSite"] = pra.Text.Trim() })) return;
            _s.BeyondTrustPraClientId = cid.Text.Trim();
            if (secret.Password.Length > 0) { _s.ObserveSecrets["bt:pra"] = secret.Password; secret.Clear(); }
            Save(); AuditLog.Write("you", "beyondtrust", "PRA connection settings changed", _s.BeyondTrustPraSite);
            result.Text = "✓ Saved (the secret is encrypted).";
        }
        async Task Test()
        {
            var key = secret.Password.Length > 0 ? secret.Password : _s.ObserveSecrets.GetValueOrDefault("bt:pra") ?? "";
            if (key.Length == 0 || cid.Text.Trim().Length == 0) { result.Text = "Enter the client id and secret first."; return; }
            result.Text = "Testing…";
            try
            {
                var jp = await new PraClient(pra.Text.Trim(), cid.Text.Trim(), key).JumpointsAsync();
                result.Text = $"✓ Signed in to {pra.Text.Trim()}: {jp.Count} Jumpoint(s), {jp.Count(j => j.Connected)} connected.";
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException or OfflineBlockedException or System.Text.Json.JsonException)
            { result.Text = "✕ " + ex.Message.Split('\n')[0] + (BtTls.Refusal("pra") != null ? " (BeyondTrust → TLS can trust it)" : ""); }
        }
        return Card("BeyondTrust · Connection",
            "The PRA or Remote Support site and its API account (Management → API Configuration: Configuration API and Reporting API, read access). HTTPS only; the site's certificate is pinned (see TLS).",
            Row(Labeled("SITE", pra), Labeled("API CLIENT ID", cid), Labeled("API CLIENT SECRET", secret)),
            Row(AdminApproval.Button("Apply", () => _ = Apply()), Btn("Test", () => _ = Test()),
                Btn("Forget the secret", () => { _s.ObserveSecrets.Remove("bt:pra"); Save(); AuditLog.Write("you", "beyondtrust", "PRA secret removed"); result.Text = "PRA secret removed."; })),
            result);
    }

    private Border BeyondTrustPasswordSafeCard()
    {
        var ps = Bound(new TextBox { Text = _s.BeyondTrustPsHost, Width = FieldWidth, ToolTip = "https://your-beyondinsight (Password Safe)" }, nameof(AppSettings.BeyondTrustPsHost));
        var runAs = Bound(new TextBox { Text = _s.BeyondTrustPsRunAs, Width = FieldWidth, ToolTip = "The Password Safe user the API registration runs as" }, nameof(AppSettings.BeyondTrustPsRunAs));
        var key = new PasswordBox { Width = FieldWidth, ToolTip = _s.ObserveSecrets.ContainsKey("bt:pskey") ? "A key is stored (encrypted). Leave empty to keep it." : "The API registration key (BeyondInsight → Configuration → API Registrations)" };
        var runPw = new PasswordBox { Width = FieldWidth, ToolTip = "The run-as user's password, only if the API registration requires it" };
        var result = Result();
        async Task Apply()
        {
            if (PasswordSafeClient.SiteProblem(ps.Text) is { } bad && ps.Text.Trim().Length > 0) { result.Text = "✕ " + bad; return; }
            if (!await ApplyProtected("change the BeyondTrust Password Safe host", new Dictionary<string, JsonNode?> { ["bt.psHost"] = ps.Text.Trim() })) return;
            _s.BeyondTrustPsRunAs = runAs.Text.Trim();
            if (key.Password.Length > 0) { _s.ObserveSecrets["bt:pskey"] = key.Password; key.Clear(); }
            if (runPw.Password.Length > 0) { _s.ObserveSecrets["bt:pspwd"] = runPw.Password; runPw.Clear(); }
            Save(); AuditLog.Write("you", "beyondtrust", "Password Safe settings changed", _s.BeyondTrustPsHost);
            result.Text = "✓ Saved (the key and password are encrypted).";
        }
        async Task Test()
        {
            var k = key.Password.Length > 0 ? key.Password : _s.ObserveSecrets.GetValueOrDefault("bt:pskey") ?? "";
            if (k.Length == 0 || runAs.Text.Trim().Length == 0) { result.Text = "Enter the run-as user and the API key first."; return; }
            result.Text = "Testing…";
            try
            {
                using var c = new PasswordSafeClient(ps.Text.Trim(), k, runAs.Text.Trim(), runPw.Password.Length > 0 ? runPw.Password : _s.ObserveSecrets.GetValueOrDefault("bt:pspwd"));
                await c.SignInAsync();
                var accounts = await c.AccountsAsync();
                await c.SignOutAsync();
                result.Text = $"✓ Signed in to Password Safe as {runAs.Text.Trim()}: {accounts.Count} managed account(s) you can request.";
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or HttpRequestException or TaskCanceledException or OfflineBlockedException or System.Text.Json.JsonException)
            { result.Text = "✕ " + ex.Message.Split('\n')[0] + (BtTls.Refusal("ps") != null ? " (BeyondTrust → TLS can trust it)" : ""); }
        }
        return Card("BeyondTrust · Password Safe",
            "BeyondInsight / Password Safe: the API registration's key and its run-as user. Checked-out passwords are kept in memory for the session only and never written anywhere.",
            Row(Labeled("HOST", ps), Labeled("RUN AS", runAs)),
            Row(Labeled("API KEY", key), Labeled("RUN-AS PASSWORD (IF REQUIRED)", runPw)),
            Row(AdminApproval.Button("Apply", () => _ = Apply()), Btn("Test", () => _ = Test()),
                Btn("Forget the key and password", () => { foreach (var k in new[] { "bt:pskey", "bt:pspwd" }) _s.ObserveSecrets.Remove(k); Save(); AuditLog.Write("you", "beyondtrust", "Password Safe secrets removed"); result.Text = "Password Safe secrets removed."; })),
            result);
    }

    private Border BeyondTrustCheckoutCard()
    {
        int[] mins = { 10, 15, 30, 60, 120, 240, 480 };
        var dur = Bound(new ComboBox { Width = 90, ItemsSource = mins, SelectedItem = mins.Contains(_s.BeyondTrustCheckoutMinutes) ? _s.BeyondTrustCheckoutMinutes : 30, HorizontalAlignment = HorizontalAlignment.Left }, nameof(AppSettings.BeyondTrustCheckoutMinutes));
        dur.SelectionChanged += (_, _) => { if (dur.SelectedItem is int m) { _s.BeyondTrustCheckoutMinutes = m; Save(); } };
        return Card("BeyondTrust · Checkout policy",
            "When a host is linked to a Password Safe managed account, connecting checks its password out for the session. It is checked in when the session's tab closes, when the time runs out and when the app closes — always.",
            ProtectedCheck("bt.checkout", "Password Safe credentials may be checked out to open sessions"),
            Labeled("CHECK OUT FOR (MINUTES; CHECKED IN EARLIER WHEN YOU'RE DONE)", dur),
            Check("Ask for a reason at every checkout (sent to Password Safe and the audit log)", _s.BeyondTrustAskReason, v => _s.BeyondTrustAskReason = v));
    }

    private Border BeyondTrustSessionsCard()
    {
        var client = Bound(new ComboBox { Width = 300, HorizontalAlignment = HorizontalAlignment.Left, ItemsSource = new[] { "My access console (client-scripting link)", "The site's web access console (portal browser)" }, SelectedIndex = _s.BeyondTrustJumpClient == "web" ? 1 : 0 }, nameof(AppSettings.BeyondTrustJumpClient));
        client.SelectionChanged += (_, _) => { _s.BeyondTrustJumpClient = client.SelectedIndex == 1 ? "web" : "console"; Save(); };
        TextBox Filter(string text, string setting, Action<string> set, string tip)
        {
            var t = Bound(new TextBox { Text = text, Width = FieldWidth, ToolTip = tip }, setting);
            t.LostFocus += (_, _) => { set(t.Text.Trim()); Save(); };
            return t;
        }
        return Card("BeyondTrust · Sessions",
            "How Jump Items open and which ones the BeyondTrust screen lists by default (comma-separated words; empty = all). The filter box on the screen narrows further.",
            Labeled("JUMP ITEMS OPEN IN", client),
            Row(Labeled("JUMP GROUPS", Filter(_s.BeyondTrustJumpGroupFilter, nameof(AppSettings.BeyondTrustJumpGroupFilter), v => _s.BeyondTrustJumpGroupFilter = v, "e.g. Linux, Datacentre — Jump Items in other groups aren't listed")),
                Labeled("JUMPOINTS", Filter(_s.BeyondTrustJumpointFilter, nameof(AppSettings.BeyondTrustJumpointFilter), v => _s.BeyondTrustJumpointFilter = v, "e.g. dc1 — sessions and Jumpoints elsewhere aren't listed"))));
    }

    private Border BeyondTrustTlsCard()
    {
        var lines = new StackPanel();
        void Show()
        {
            lines.Children.Clear();
            foreach (var (kind, title, key, pin) in new[] { ("pra", "PRA / Remote Support", "bt.praPin", _s.BeyondTrustPraPin), ("ps", "Password Safe", "bt.psPin", _s.BeyondTrustPsPin) })
            {
                var seen = BtTls.Seen.TryGetValue(kind, out var s) ? s : default;
                var text = Bound(new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 2) }.Mono(), kind == "pra" ? nameof(AppSettings.BeyondTrustPraPin) : nameof(AppSettings.BeyondTrustPsPin));
                text.Text = $"{title}: " + (pin.Length > 0 ? $"pinned SHA-256 {TlsPin.Pretty(pin)}" : "nothing pinned (a certificate Windows trusts is accepted; a self-signed one is refused)")
                    + (seen.Sha256 is { Length: > 0 } ? $"\nlast seen {TlsPin.Pretty(seen.Sha256)} — {(seen.Decision == TlsDecision.Accept ? "accepted" : seen.Decision == TlsDecision.Mismatch ? "⚠ DIFFERENT from the pin" : "not trusted yet")}" : "\nnot contacted in this session (Test on its card connects)");
                lines.Children.Add(text);
                var row = new WrapPanel();
                if (seen.Sha256 is { Length: > 0 } sha && seen.Decision != TlsDecision.Accept)
                    row.Children.Add(AdminApproval.Button("Trust the certificate shown", async () =>
                    {
                        if (!Dialogs.Confirm("Trust the certificate", $"Pin {title}'s certificate?\n\nSHA-256 {TlsPin.Pretty(sha)}\n\nCompare it with the appliance first (its /login → Management → SSL/TLS, or the certificate you installed). Any other certificate is refused afterwards.", "Trust", danger: true)) return;
                        if (await ApplyProtected($"trust the {title} certificate", new Dictionary<string, JsonNode?> { [key] = sha })) { AuditLog.Write("you", "beyondtrust", $"{title} certificate pinned", TlsPin.Pretty(sha)); Show(); }
                    }, "PrimaryButton"));
                if (pin.Length > 0)
                    row.Children.Add(AdminApproval.Button("Forget the pin", async () =>
                    {
                        if (await ApplyProtected($"forget the {title} certificate pin", new Dictionary<string, JsonNode?> { [key] = "" })) { AuditLog.Write("you", "beyondtrust", $"{title} certificate pin removed"); Show(); }
                    }, "SmallButton"));
                lines.Children.Add(row);
            }
        }
        Show();
        lines.IsVisibleChanged += (_, e) => { if (e.NewValue is true) Show(); };
        return Card("BeyondTrust · TLS",
            "Trust on first use, like Proxmox and vCenter: an appliance with a self-signed certificate is refused until you compare its fingerprint and trust it; once pinned, any other certificate is refused (possible interception). Pins need admin approval and can be set by Group Policy.",
            lines);
    }

    private Border BeyondTrustScopesCard()
    {
        var t = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        void Show() => t.Text =
            "READS (Privileged Remote Access / Remote Support, Configuration and Reporting APIs): Jump Items, access sessions, Jumpoints, Vault account names (never their secrets).\n" +
            "ACTIONS: open a Jump Item in your own access console (signed in as you there) · add Jump Items as hosts" + (Restrictions.Allowed("host.add") ? "" : " — turned off by policy") + ".\n" +
            "PASSWORD SAFE: list the managed accounts you may request · " + (_s.BeyondTrustAllowCheckout ? $"check one out for a session ({_s.BeyondTrustCheckoutMinutes} min, then checked in)" : "checkouts are OFF") + ".\n" +
            "NEVER: create, change or delete anything on the appliances; store a checked-out password; send a secret to the AI or an add-on.";
        Show();
        t.IsVisibleChanged += (_, e) => { if (e.NewValue is true) Show(); };
        return Card("BeyondTrust · Scopes", "What the integration may read and do, as it stands now.", t);
    }

    private Border BeyondTrustAuditCard()
    {
        var list = new TextBlock { TextWrapping = TextWrapping.Wrap, Style = (Style)FindResource("Caption") };
        var result = Result();
        void Show()
        {
            if (!Locks.IsOpen("cat:audit")) { list.Text = "🔒 The audit log is locked: open it (LOGS → Audit log) to see BeyondTrust's entries here."; return; }
            var month = AuditLog.Months().FirstOrDefault();
            var entries = month == null ? new List<AuditEntry>() : AuditLog.Read(month).Where(e => e.Target.Contains("beyondtrust", StringComparison.OrdinalIgnoreCase) || e.Action.Contains("Password Safe", StringComparison.OrdinalIgnoreCase)).Take(12).ToList();
            list.Text = entries.Count == 0 ? "No BeyondTrust activity this month." : string.Join("\n", entries.Select(e => $"{e.Time:dd MMM HH:mm} · {e.Actor} · {e.Target}: {e.Action}"));
        }
        Show();
        list.IsVisibleChanged += (_, e) => { if (e.NewValue is true) Show(); };
        return Card("BeyondTrust · Audit",
            "What was checked out, opened and changed through BeyondTrust (this month, newest first), and forgetting every BeyondTrust secret on this PC.",
            list,
            Row(Btn("Check in everything now", () => { PasswordSafeSessions.CheckInAll(); result.Text = "Checking in every open checkout…"; }),
                Btn("Forget every BeyondTrust secret", () =>
                {
                    if (!Dialogs.Confirm("Forget the secrets", "Remove the PRA secret, the Password Safe key and the run-as password from this PC? Checked-out sessions keep working until they close.", "Forget", danger: true)) return;
                    foreach (var k in new[] { "bt:pra", "bt:pskey", "bt:pspwd" }) _s.ObserveSecrets.Remove(k);
                    Save(); AuditLog.Write("you", "beyondtrust", "secrets removed"); result.Text = "Every BeyondTrust secret removed."; Show();
                }, "DangerButton"),
                Btn("Open the audit log", () => MainWindow.Instance.Navigate("audit"))),
            result);
    }
}
