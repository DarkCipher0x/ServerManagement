using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using DcxManager.Bitwarden;
using DcxManager.Services;

namespace DcxManager.Views;

// SettingsView: Integrations → Bitwarden (0.6.14, ADR-093), a page of cards like BeyondTrust's: the switch, the server and
// personal API key, the CLIs (and approving a build Bitwarden didn't sign), Secrets Manager, how vault secrets may be used,
// and the audit. The switch, the server, the uses and CLI approvals are admin-protected; the API key and access token stay
// in the encrypted settings (ObserveSecrets), never in a policy; the master password is never stored.
public sealed partial class SettingsView
{
    /// <summary>The cards of the Bitwarden page, in order (Integrations → Bitwarden).</summary>
    private IEnumerable<(string Category, Func<Border> Make)> BitwardenCards() => new (string, Func<Border>)[]
    {
        ("Integrations", BitwardenOverviewCard), ("Integrations", BitwardenServerCard), ("Integrations", BitwardenCliCard),
        ("Integrations", BitwardenSecretsCard), ("Integrations", BitwardenUseCard), ("Integrations", BitwardenAuditCard),
    };

    private Border BitwardenOverviewCard()
    {
        var state = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) };
        void Show() => state.Text = (_s.BitwardenEnabled ? "● On." : "○ Off.")
            + $" Server: {BitwardenVault.ServerLabel(_s.BitwardenServer)}."
            + $" Personal API key: {(_s.ObserveSecrets.ContainsKey("bw:clientsecret") ? "stored (encrypted)" : "not set")}."
            + $" Secrets Manager token: {(BitwardenSecrets.HasToken ? "stored (encrypted)" : "not set")}."
            + $" Vault {(BitwardenVault.Unlocked ? "unlocked" : "locked")}.";
        Show();
        state.IsVisibleChanged += (_, e) => { if (e.NewValue is true) Show(); };
        return Card("Bitwarden",
            "Sign hosts in with your Bitwarden vault's logins and SSH keys, or with Secrets Manager secrets, through Bitwarden's own CLIs (bw, bws), which do the decryption. A password or key is read into memory when a session connects and is never saved here. Bitwarden cloud (US or EU), self-hosted Bitwarden and Vaultwarden (vault only). Sidebar → VAULT → Bitwarden.",
            ProtectedCheck("bw.enabled", "Bitwarden integration", async want =>
            {
                var ok = await ProtectedSettings.ChangeAsync((want ? "turn on: " : "turn off: ") + "Bitwarden integration", "bw.enabled", want);
                if (!_s.BitwardenEnabled) BitwardenVault.Lock("the integration was turned off");
                MainWindow.Instance?.BuildNav();   // VAULT → Bitwarden appears / goes at once
                Show();
                return ok;
            }),
            state,
            Row(Btn("Open Bitwarden", () => MainWindow.Instance.Navigate("bitwarden"))));
    }

    private Border BitwardenServerCard()
    {
        string[] kinds = { "bitwarden.com (US cloud)", "bitwarden.eu (EU cloud)", "Self-hosted (Bitwarden or Vaultwarden)" };
        var kind = Bound(new ComboBox { Width = 300, HorizontalAlignment = HorizontalAlignment.Left, ItemsSource = kinds, SelectedIndex = _s.BitwardenServer == "" ? 0 : _s.BitwardenServer == "eu" ? 1 : 2 }, nameof(AppSettings.BitwardenServer));
        var url = Bound(new TextBox { Text = _s.BitwardenServer is "" or "eu" ? "" : _s.BitwardenServer, Width = FieldWidth, ToolTip = "https://vault.example.com (your server's address)" }, nameof(AppSettings.BitwardenServer));
        void ShowUrl() => url.IsEnabled = kind.SelectedIndex == 2;
        ShowUrl();
        kind.SelectionChanged += (_, _) => ShowUrl();
        var cid = new PasswordBox { Width = FieldWidth, ToolTip = _s.ObserveSecrets.ContainsKey("bw:clientid") ? "A client id is stored (encrypted). Leave empty to keep it." : "client_id, e.g. user.xxxxxxxx-…" };
        var secret = new PasswordBox { Width = FieldWidth, ToolTip = _s.ObserveSecrets.ContainsKey("bw:clientsecret") ? "A client secret is stored (encrypted). Leave empty to keep it." : "client_secret" };
        var result = Result();
        async Task Apply()
        {
            var want = kind.SelectedIndex switch { 0 => "", 1 => "eu", _ => url.Text.Trim().TrimEnd('/') };
            if (ProtectedSettings.BitwardenServerCheck(want) is { } bad) { result.Text = "✕ " + bad; return; }
            if (want != _s.BitwardenServer)
            {
                if (!await ApplyProtected("change the Bitwarden server", new Dictionary<string, JsonNode?> { ["bw.server"] = want })) return;
                BitwardenVault.Lock("the server changed");
                BitwardenSessions.Forget("the Bitwarden server changed");
            }
            if (cid.Password.Trim().Length > 0) { _s.ObserveSecrets["bw:clientid"] = cid.Password.Trim(); cid.Clear(); }
            if (secret.Password.Trim().Length > 0) { _s.ObserveSecrets["bw:clientsecret"] = secret.Password.Trim(); secret.Clear(); }
            Save(); AuditLog.Write("you", "bitwarden", "Bitwarden sign-in settings changed", BitwardenVault.ServerLabel(_s.BitwardenServer));
            result.Text = "✓ Saved (the API key is encrypted). Unlock the vault on VAULT → Bitwarden: it asks for your master password.";
        }
        async Task Test()
        {
            result.Text = "Checking…";
            try { result.Text = "✓ " + (await BitwardenVault.StatusAsync()).Text; }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception or System.IO.IOException or UnauthorizedAccessException)
            { result.Text = "✕ " + ex.Message; }
        }
        return Card("Bitwarden · Server & sign-in",
            "Signing in uses your personal API key (web vault → Settings → Security → Keys → View API key), so no second-factor prompt can block it; unlocking asks for your master password each time the vault is locked and never stores it. Changing the server needs admin approval.",
            Row(Labeled("SERVER", kind), Labeled("SELF-HOSTED ADDRESS", url)),
            Row(Labeled("API KEY: CLIENT ID", cid), Labeled("API KEY: CLIENT SECRET", secret)),
            Row(AdminApproval.Button("Apply", () => _ = Apply()), Btn("Check", () => _ = Test()),
                Btn("Forget the API key", () => { foreach (var k in new[] { "bw:clientid", "bw:clientsecret" }) _s.ObserveSecrets.Remove(k); Save(); AuditLog.Write("you", "bitwarden", "Bitwarden API key removed"); result.Text = "API key removed (bw stays signed in until you sign out on the Audit card)."; })),
            result);
    }

    private Border BitwardenCliCard()
    {
        TextBox PathBox(string text, string setting, Action<string> set, string tip)
        {
            var t = Bound(new TextBox { Text = text, Width = FieldWidth, ToolTip = tip }, setting);
            t.LostFocus += (_, _) => { if (t.Text.Trim() != text) { set(t.Text.Trim()); Save(); BitwardenCli.Recheck(); } };
            return t;
        }
        var bw = PathBox(_s.BitwardenCliPath, nameof(AppSettings.BitwardenCliPath), v => _s.BitwardenCliPath = v, "Empty = found automatically (winget, Chocolatey, Scoop, Program Files, PATH). Only bw.exe, not the npm bw.cmd.");
        var bws = PathBox(_s.BitwardenSmCliPath, nameof(AppSettings.BitwardenSmCliPath), v => _s.BitwardenSmCliPath = v, "Empty = found automatically (Cargo, Program Files, PATH)");
        var lines = Bound(new StackPanel(), nameof(AppSettings.BitwardenCliPins));   // the approvals: admin-protected, Group Policy can set them
        void Show()
        {
            lines.Children.Clear();
            foreach (var tool in new[] { BitwardenCli.Tool.Bw, BitwardenCli.Tool.Bws })
            {
                var text = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 2) }.Mono();
                lines.Children.Add(text);
                if (BitwardenCli.Find(tool) is not { } path) { text.Text = $"{BitwardenCli.Name(tool)}: not found. {BitwardenCli.NotInstalled(tool)}"; continue; }
                BitwardenCli.Trust t;
                try { t = BitwardenCli.Check(path); }
                catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { text.Text = $"{BitwardenCli.Name(tool)}: {path} can't be read ({ex.Message})"; continue; }
                text.Text = $"{BitwardenCli.Name(tool)}: {path}\n{(t.Ok ? "✓ " + t.Why : "✕ " + t.Why)}";
                if (t.Ok) continue;
                var sha = t.Sha256;
                lines.Children.Add(AdminApproval.Button("Approve this build", async () =>
                {
                    if (!Dialogs.Confirm("Approve a Bitwarden CLI build", $"{path}\n\n{(t.Signer.Length > 0 ? "Signed by: " + t.Signer : "Not signed")}\nSHA-256 {BitwardenCli.Pretty(sha)}\n\nApprove it only if you got it from Bitwarden (bitwarden.com/download, or their GitHub releases) and its SHA-256 matches the one they publish. It will receive your master password, API key and access token.", "Approve", danger: true)) return;
                    var pins = string.Join(",", _s.BitwardenCliPins.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Append(sha).Distinct(StringComparer.OrdinalIgnoreCase).TakeLast(10));
                    if (await ApplyProtected("approve a Bitwarden CLI build", new Dictionary<string, JsonNode?> { ["bw.pins"] = pins })) { AuditLog.Write("you", "bitwarden", "Bitwarden CLI build approved", $"{System.IO.Path.GetFileName(path)} {BitwardenCli.Pretty(sha)}"); Show(); }
                }, "SmallButton"));
            }
            if (_s.BitwardenCliPins.Length > 0)
                lines.Children.Add(AdminApproval.Button("Forget the approved builds", async () =>
                {
                    if (await ApplyProtected("forget the approved Bitwarden CLI builds", new Dictionary<string, JsonNode?> { ["bw.pins"] = "" })) { AuditLog.Write("you", "bitwarden", "approved Bitwarden CLI builds removed"); Show(); }
                }, "SmallButton"));
        }
        Show();
        lines.IsVisibleChanged += (_, e) => { if (e.NewValue is true) Show(); };
        return Card("Bitwarden · CLI",
            "Only a CLI signed by Bitwarden (Authenticode, checked with revocation) runs; a build they didn't sign runs only after an administrator approves its SHA-256. Secrets reach it as environment variables of that one process, never on its command line. Install the vault CLI with: winget install Bitwarden.CLI",
            Row(Labeled("BW.EXE (PASSWORD MANAGER)", bw), Labeled("BWS.EXE (SECRETS MANAGER)", bws)),
            lines,
            Row(Btn("Check again", () => { BitwardenCli.Recheck(); Show(); })));
    }

    private Border BitwardenSecretsCard()
    {
        var token = new PasswordBox { Width = FieldWidth, ToolTip = BitwardenSecrets.HasToken ? "An access token is stored (encrypted). Leave empty to keep it." : "A machine account's access token (Secrets Manager → Machine accounts → Access tokens)" };
        var result = Result();
        async Task Test()
        {
            result.Text = "Checking…";
            try
            {
                var projects = await BitwardenSecrets.ProjectsAsync(token: token.Password.Trim().Length > 0 ? token.Password.Trim() : null);
                result.Text = $"✓ The machine account can read {projects.Count} project(s).";
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or TimeoutException or OfflineBlockedException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception or System.IO.IOException or UnauthorizedAccessException)
            { result.Text = "✕ " + ex.Message; }
        }
        return Card("Bitwarden · Secrets Manager",
            "A machine account's access token: read access to the projects you grant it is enough (give it no write access). Its secrets can sign hosts in (service accounts, API tokens) or be copied briefly. Not available on Vaultwarden.",
            Labeled("ACCESS TOKEN", token),
            Row(Btn("Save", () =>
            {
                if (token.Password.Trim().Length == 0) { result.Text = "Paste the access token first."; return; }
                _s.ObserveSecrets["bws:token"] = token.Password.Trim(); token.Clear(); Save();
                AuditLog.Write("you", "bitwarden", "Secrets Manager access token stored");
                result.Text = "✓ Saved (encrypted).";
            }, "PrimaryButton"), Btn("Test", () => _ = Test()),
                Btn("Forget the token", () => { _s.ObserveSecrets.Remove("bws:token"); Save(); AuditLog.Write("you", "bitwarden", "Secrets Manager access token removed"); result.Text = "Access token removed."; })),
            result);
    }

    private Border BitwardenUseCard()
    {
        int[] mins = { 5, 10, 15, 30, 60, 120, 240, 480 };
        var lockAfter = Bound(new ComboBox { Width = 90, ItemsSource = mins, SelectedItem = mins.Contains(_s.BitwardenLockMinutes) ? _s.BitwardenLockMinutes : 30, HorizontalAlignment = HorizontalAlignment.Left }, nameof(AppSettings.BitwardenLockMinutes));
        lockAfter.SelectionChanged += (_, _) => { if (lockAfter.SelectedItem is int m) { _s.BitwardenLockMinutes = m; Save(); } };
        int[] secs = { 10, 20, 30, 45, 60, 120 };
        var clear = Bound(new ComboBox { Width = 90, ItemsSource = secs, SelectedItem = secs.Contains(_s.BitwardenClipboardSeconds) ? _s.BitwardenClipboardSeconds : 30, HorizontalAlignment = HorizontalAlignment.Left }, nameof(AppSettings.BitwardenClipboardSeconds));
        clear.SelectionChanged += (_, _) => { if (clear.SelectedItem is int x) { _s.BitwardenClipboardSeconds = x; Save(); } };
        return Card("Bitwarden · Use",
            "What vault items and secrets may be used for on this PC, and how long the vault stays unlocked. It always locks when the app locks and when it closes.",
            ProtectedCheck("bw.sessions", "Vault items and secrets may sign in sessions (in memory, never saved)"),
            ProtectedCheck("bw.copy", "Passwords and secrets may be copied to the clipboard (kept out of clipboard history)"),
            Row(Labeled("LOCK THE VAULT AFTER (MINUTES UNUSED)", lockAfter), Labeled("CLEAR A COPIED SECRET AFTER (SECONDS)", clear)),
            Check("Sync the vault each time it is unlocked", _s.BitwardenSyncOnUnlock, v => _s.BitwardenSyncOnUnlock = v),
            // 0.6.20: the master password remembered on this PC (ticked at the unlock prompt).
            ProtectedCheck("bw.remember", "The master password may be remembered on this PC (encrypted; ticked at the unlock prompt)"),
            RememberedRow());
    }

    private FrameworkElement RememberedRow()
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), Style = (Style)FindResource("Caption") };
        var forget = Btn("Forget the remembered master password", () => { BitwardenSessions.Forget("forgotten by you (Settings)"); });
        void Show()
        {
            text.Text = BitwardenSessions.IsRemembered ? "● Your master password is remembered on this PC: Bitwarden unlocks without asking, also when the app starts."
                : !_s.BitwardenAllowRemember ? "○ Remembering the master password is switched off on this PC."
                : "○ No master password remembered: tick “Remember on this PC” when Bitwarden asks for it.";
            forget.IsEnabled = BitwardenSessions.IsRemembered;
        }
        Show();
        text.IsVisibleChanged += (_, e) => { if (e.NewValue is true) Show(); };
        forget.Click += (_, _) => Show();
        return Row(text, forget);
    }

    private Border BitwardenAuditCard()
    {
        var list = new TextBlock { TextWrapping = TextWrapping.Wrap, Style = (Style)FindResource("Caption") };
        var result = Result();
        void Show()
        {
            if (!Locks.IsOpen("cat:audit")) { list.Text = "🔒 The audit log is locked: open it (LOGS → Audit log) to see Bitwarden's entries here."; return; }
            var month = AuditLog.Months().FirstOrDefault();
            var entries = month == null ? new List<AuditEntry>() : AuditLog.Read(month).Where(e => e.Target.Contains("bitwarden", StringComparison.OrdinalIgnoreCase) || e.Action.Contains("Bitwarden", StringComparison.OrdinalIgnoreCase)).Take(12).ToList();
            list.Text = entries.Count == 0 ? "No Bitwarden activity this month." : string.Join("\n", entries.Select(e => $"{e.Time:dd MMM HH:mm} · {e.Actor} · {e.Target}: {e.Action}"));
        }
        Show();
        list.IsVisibleChanged += (_, e) => { if (e.NewValue is true) Show(); };
        return Card("Bitwarden · Audit",
            "What was unlocked, used for a session and copied (this month, newest first; never a secret), and signing out of Bitwarden on this PC.",
            list,
            Row(Btn("🔒 Lock the vault now", () => { BitwardenVault.Lock("locked by you"); result.Text = "Vault locked."; Show(); }),
                Btn("Sign out and forget everything", async () =>
                {
                    if (!Dialogs.Confirm("Sign out of Bitwarden", "Sign bw out, remove its cache folder of this app, and forget the API key, the Secrets Manager token and a remembered master password on this PC? Your vault on the server is untouched; hosts stay linked.", "Sign out", danger: true)) return;
                    foreach (var k in new[] { "bw:clientid", "bw:clientsecret", "bws:token" }) _s.ObserveSecrets.Remove(k);
                    BitwardenSessions.Forget("signed out of Bitwarden");
                    Save();
                    try { await BitwardenVault.SignOutAsync(); result.Text = "Signed out; every Bitwarden secret removed from this PC."; }
                    catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.IO.IOException) { result.Text = "Secrets removed; bw sign-out: " + ex.Message; }
                    Show();
                }, "DangerButton"),
                Btn("Open the audit log", () => MainWindow.Instance.Navigate("audit"))),
            result);
    }
}
