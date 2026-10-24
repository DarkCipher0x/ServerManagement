using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using DcxManager.Library;
using DcxManager.Services;
using ThemeMode = DcxManager.Services.ThemeMode;   // .NET 9+ WPF has System.Windows.ThemeMode too
using Microsoft.Win32;

namespace DcxManager.Views;

/// <summary>
/// First-run setup, "Quick Start" (0.3.0 §19; 0.5.1 #15: it covers everything). Short pages with Skip on every one:
/// protection, category locks, personal settings, AI (with the one-click Foundry Local set-up), organisation sign-in
/// (Active Directory, LDAPS, Entra ID), BeyondTrust (each part tested before it is kept), cloud accounts, infrastructure,
/// importing from other managers, Teams and Pingdom, log sinks, backup and the team library. Pages apply their choices
/// through the same code the Settings screen uses. Admin-protected values are collected and asked for once, as one UAC
/// prompt, at Finish; a step Group Policy already set says so and changes nothing. "Take me there when I finish" opens
/// the matching Settings page or screen afterwards.
/// </summary>
public sealed class SetupWindow : Window
{
    private readonly ContentControl _page = new();
    private readonly TextBlock _step = new() { Margin = new Thickness(0, 0, 0, 8) };
    private readonly Button _back = new() { Content = "‹ Back", MinWidth = 90 }, _next = new() { Content = "Next ›", MinWidth = 110 };
    private readonly List<(string Title, Func<FrameworkElement> Make)> _pages = new();
    private int _i;
    internal int PageCount => _pages.Count;
    internal int Page => _i;
    internal IReadOnlyList<string> PageTitlesForTest => _pages.Select(p => p.Title).ToList();
    internal void ShowForTest(int i) => Show(i);

    /// <summary>Admin-protected values chosen on the pages: asked for once, at Finish (0.5.1 #15).</summary>
    private readonly Dictionary<string, System.Text.Json.Nodes.JsonNode?> _protected = new();
    internal IReadOnlyDictionary<string, System.Text.Json.Nodes.JsonNode?> ProtectedForTest => _protected;
    /// <summary>"Take me there when I finish": what opens after the window closes, by key.</summary>
    private readonly Dictionary<string, Action> _after = new();

    public static void Run(Window owner)
    {
        var w = new SetupWindow { Owner = owner };
        w.ShowDialog();
        foreach (var a in w._after.Values) { try { a(); } catch (Exception ex) { App.Log("Quick Start: open afterwards", ex); } }
    }

    public SetupWindow()
    {
        Title = "Welcome — " + App.ProductName;
        SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        Typography.Size(this, 640);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this.Fs(FontStep.Control);
        SetResourceReference(BackgroundProperty, "Bg"); SetResourceReference(ForegroundProperty, "Fg");
        try { Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/app.ico")); } catch { }
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        _step.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var root = new DockPanel { Margin = new Thickness(26, 22, 26, 22) };
        var buttons = new DockPanel { Margin = new Thickness(0, 16, 0, 0) };
        var skip = new Button { Content = "Skip this step", MinWidth = 120 };
        skip.Click += (_, _) => Go(+1);
        _back.Click += (_, _) => Go(-1);
        _next.Click += (_, _) => Go(+1);
        _next.Style = (Style)FindResource("PrimaryButton");
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(_back); right.Children.Add(skip); right.Children.Add(_next);
        _back.Margin = skip.Margin = new Thickness(0, 0, 8, 0);
        buttons.Children.Add(right);
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(_step, Dock.Top);
        root.Children.Add(_step); root.Children.Add(buttons); root.Children.Add(_page);
        Content = root;
        ThemeManager.TrackScale(this, root);

        _pages.Add(("Welcome", Welcome));
        _pages.Add(("Protect the app", Protect));
        _pages.Add(("Lock what matters", LocksPage));
        _pages.Add(("Personal settings", Personal));
        _pages.Add(("AI features", Ai));
        _pages.Add(("Organisation sign-in", Directory));
        _pages.Add(("BeyondTrust", BeyondTrust));
        _pages.Add(("Cloud accounts", () => Pointer("Cloud accounts",
            "Vultr, Contabo, Hetzner Cloud, Microsoft Azure and Google Cloud: each account's API key or service principal is stored encrypted on this PC, and every account can be read-only. Add them in Settings → Cloud providers; Test checks each one.",
            "cloud", () => OpenSettings("Cloud providers"), "VultrAccounts", "AllowCloudAccountAdd")));
        _pages.Add(("Infrastructure", () => Pointer("Infrastructure",
            "Docker, Proxmox VE, Unraid, VMware vCenter, Hyper-V, Synology and Red Hat Satellite: inventory and health, read-only until you allow changes on each provider. ＋ Add on the Infrastructure screen; nothing is contacted until you add one.",
            "infra", () => MainWindow.Instance?.Navigate("infra"), null, "AllowInfraAdd")));
        _pages.Add(("Import connections", ImportPage));
        _pages.Add(("Teams & Pingdom", () => Pointer("Microsoft Teams and Pingdom",
            "Share screenshots, terminal text, incidents and reports to a Teams channel or chat (your Entra app registration, signed in as you), and see your Pingdom checks on the dashboard (the token stays encrypted here). Both are off until you set them up.",
            "teams", () => OpenIntegration("Teams"), "TeamsEnabled", "AllowIntegrationEdit")));
        _pages.Add(("Log sinks", () => Pointer("Log sinks and syslog",
            "Ship the audit log and the app's own errors to Elasticsearch / OpenSearch, Grafana Loki, Azure Monitor / Microsoft Sentinel, Splunk, Graylog or a webhook (HTTPS only, secrets masked), or to syslog over TLS. The sink list needs admin approval.",
            "sinks", () => OpenIntegration("Log sinks & Sentinel"), "ObserveSinks", "AllowIntegrationEdit")));
        _pages.Add(("Backup", () => Pointer("Backup",
            "Encrypted backups of the store on a schedule, to a folder you choose (a NAS or another disk); restore picks what to bring back. The rules need admin approval and can be set by Group Policy.",
            "backup", () => OpenSettings("Data & backups"), "BackupFolder")));
        _pages.Add(("Your team", Team));
        _pages.Add(("Done", Done));
        Show(0);
    }

    private async void Go(int d)
    {
        if (_i + d >= _pages.Count)
        {
            // 0.5.1 #15: the protected values chosen on the way, as one admin approval.
            if (_protected.Count > 0 && !await ProtectedSettings.ChangeAsync("Quick Start settings", _protected)) { _step.Text = "The protected settings weren't approved — the rest is saved. Change them later in Settings."; _protected.Clear(); return; }
            DialogResult = true;
            return;
        }
        Show(Math.Clamp(_i + d, 0, _pages.Count - 1));
    }

    // ---- helpers for the 0.5.1 pages -------------------------------------------------------------------------------------

    private static void OpenSettings(string category) { MainWindow.Instance?.Navigate("settings"); SettingsView.Current?.RevealCategory(category); }
    private static void OpenIntegration(string page) { MainWindow.Instance?.Navigate("settings"); SettingsView.Current?.RevealIntegration(page); }

    /// <summary>"Set by your organisation" when Group Policy set any of <paramref name="settings"/>, or null.</summary>
    private static string? Managed(params string?[] settings)
    {
        foreach (var name in settings.OfType<string>())
        {
            if (name.StartsWith("Allow", StringComparison.Ordinal) && Restrictions.All.FirstOrDefault(a => a.Setting == name) is { Action: not null } r && Restrictions.Why(r.Action) is { } why) return "🔒 " + why;
            if (PolicySettings.Get(name) is { } v) return $"🔒 Set by your organisation (Group Policy {name}) — nothing to do here.";
        }
        return null;
    }

    private CheckBox After(string key, string text, Action open)
    {
        var c = new CheckBox { Content = text, IsChecked = _after.ContainsKey(key), Margin = new Thickness(0, 8, 0, 0) };
        c.Checked += (_, _) => _after[key] = open;
        c.Unchecked += (_, _) => _after.Remove(key);
        return c;
    }

    /// <summary>A page that explains a part of the app and opens it after Finish (its set-up has its own Test buttons there).</summary>
    private FrameworkElement Pointer(string title, string text, string key, Action open, string? policySetting, string? restriction = null)
    {
        var p = new StackPanel();
        p.Children.Add(H(title));
        p.Children.Add(P(text));
        if (Managed(policySetting, restriction) is { } why) { p.Children.Add(Hint(why)); return p; }
        p.Children.Add(After(key, "Take me there when I finish", open));
        p.Children.Add(Hint("Skip this step if you don't need it now; it is always in Settings."));
        return p;
    }

    private void Show(int i)
    {
        _i = i;
        _step.Text = $"Step {i + 1} of {_pages.Count} — {_pages[i].Title}";
        _page.Content = _pages[i].Make();
        _back.IsEnabled = i > 0;
        _next.Content = i == _pages.Count - 1 ? "Finish" : "Next ›";
    }

    // ---- pages ---------------------------------------------------------------------------------------------------------

    private TextBlock H(string t) => new() { Text = t, Style = (Style)FindResource("H1"), Margin = new Thickness(0, 0, 0, 6) };
    private TextBlock P(string t) => new() { Text = t, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
    private TextBlock Hint(string t) => new() { Text = t, Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
    private TextBlock L(string t) => new() { Text = t, Style = (Style)FindResource("Label"), Margin = new Thickness(0, 8, 0, 4) };
    private Button B(string t, Action a, bool primary = false) { var b = new Button { Content = t, Margin = new Thickness(0, 0, 8, 6), HorizontalAlignment = HorizontalAlignment.Left }; if (primary) b.Style = (Style)FindResource("PrimaryButton"); b.Click += (_, _) => { try { a(); } catch (Exception ex) { Dialogs.Error("Setup", ex.Message); } }; return b; }
    private static void Save() { try { AppSettings.Save(); } catch (Exception ex) { App.Log("Setup", ex); } }

    private FrameworkElement Welcome()
    {
        var p = new StackPanel();
        // 0.5.1 #16: the company line above, the application's name in the title.
        var brand = new TextBlock { Text = "DARKCIPHER SYSTEMS", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 2) }.Fs(FontStep.Small);
        brand.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        p.Children.Add(brand);
        p.Children.Add(H("Welcome to " + AppInfo.AppName));
        p.Children.Add(P("A few optional steps set the app up the way you want: how it is protected and what is locked, your personal defaults, AI, organisation sign-in, BeyondTrust, cloud accounts, infrastructure, importing your connections, integrations, backup and your team. Every step can be skipped and changed later in Settings."));
        p.Children.Add(L("THIS PROFILE'S NAME"));
        var name = new TextBox { Text = Profiles.Current?.Name ?? Environment.UserName, Padding = new Thickness(5, 4, 5, 4) };
        name.LostFocus += (_, _) => { if (Profiles.Current is { } c && name.Text.Trim().Length > 0 && name.Text.Trim() != c.Name) { try { Profiles.Rename(c.Id, name.Text.Trim()); c.Name = name.Text.Trim(); } catch (Exception ex) when (ex is ArgumentException or System.IO.IOException) { } } };
        p.Children.Add(name);
        p.Children.Add(Hint("More people on this PC? Each can have their own profile — a separate encrypted store — or sign in with your organisation (Active Directory, LDAPS or Microsoft Entra ID). Settings → Security & privacy → Profiles."));
        return p;
    }

    private FrameworkElement Protect()
    {
        var p = new StackPanel();
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 10) };
        void Refresh() => status.Text = SecuritySetupUi.ProtectionText();
        p.Children.Add(H("Protect the app (recommended)"));
        p.Children.Add(P("Everything the app stores is already encrypted with AES-256-GCM. Right now the key is held by your Windows account, so anyone signed in as you could open the app. A master password that is required at start makes the whole application encrypted under it — the files are useless without the password, even to an administrator or from a copy of the disk."));
        p.Children.Add(status);
        p.Children.Add(B("🔒 Set a master password (required at start)…", () => { SecuritySetupUi.SetMasterPassword(this); Refresh(); }, primary: true));
        p.Children.Add(B("👤 Also sign in with Windows Hello (face, fingerprint or PIN)…", async () => { await SecuritySetupUi.TurnOnHelloAsync(); Refresh(); }));
        var autoLock = new CheckBox { Content = "Lock the app after 15 minutes without input, and when Windows locks", IsChecked = AppSettings.Current.AutoLockMinutes > 0, Margin = new Thickness(0, 6, 0, 0) };
        autoLock.Checked += (_, _) => { AppSettings.Current.AutoLockMinutes = 15; AppSettings.Current.LockOnWindowsLock = true; Save(); };
        autoLock.Unchecked += (_, _) => { AppSettings.Current.AutoLockMinutes = 0; Save(); };
        p.Children.Add(autoLock);
        p.Children.Add(Hint("Auto-lock needs a master password or 2FA to unlock with. Two-factor sign-in (authenticator app) is in Settings → Security."));
        Refresh();
        return p;
    }

    // ---- 0.5.1 #15: category locks ---------------------------------------------------------------------------------------

    private readonly HashSet<string> _lockedHere = new();

    private FrameworkElement LocksPage()
    {
        var p = new StackPanel();
        p.Children.Add(H("Lock what matters (optional)"));
        p.Children.Add(P("A locked part of the app opens only after Windows admin approval (UAC), and closes again after a while, when the app locks and when Windows locks. The audit log is locked already. You can lock single hosts, folders and credentials later from their right-click menu."));
        if (Locks.WhyNotAllowed("cat:credentials") is { } no) { p.Children.Add(Hint("🔒 " + no)); return p; }
        foreach (var key in new[] { "credentials", "knownhosts", "team", "sessionlogs", "errorlog", "settings" })
        {
            var cat = Locks.FindCategory(key)!;
            var rule = Locks.RuleOn("cat:" + key);
            var c = new CheckBox { Content = $"{cat.Title} — {cat.Guards}", IsChecked = rule != null, Margin = new Thickness(0, 6, 0, 0) };
            if (rule is { Managed: true }) { c.IsEnabled = false; c.Content = $"{cat.Title} — locked by your organisation"; }
            else if (rule != null && !_lockedHere.Contains(key)) { c.IsEnabled = false; c.ToolTip = "Locked already: remove it in Settings → Security & privacy → Locks & restrictions (it asks to unlock first)."; }
            c.Checked += (_, _) => { if (Locks.Set("cat:" + key, cat.Title, Locks.Uac) == Locks.Result.Ok) _lockedHere.Add(key); };
            c.Unchecked += (_, _) => { if (_lockedHere.Remove(key)) Locks.Remove("cat:" + key); };
            p.Children.Add(c);
        }
        return p;
    }

    // ---- 0.5.1 #15: organisation sign-in -----------------------------------------------------------------------------------

    private FrameworkElement Directory()
    {
        var p = new StackPanel();
        p.Children.Add(H("Sign in with your organisation (optional)"));
        p.Children.Add(P("A profile can sign in with your organisation instead of (or as well as) its master password: Active Directory (Windows sign-in, Kerberos), LDAPS, or Microsoft Entra ID — with a required group and roles from groups. Single sign-on also opens SSH hosts with your Kerberos ticket and Remote Desktop with your Windows account, so no password is typed or stored."));
        if (Managed("SsoKerberosSsh") is { } why) p.Children.Add(Hint(why));
        p.Children.Add(After("directory", "Take me to Settings → Identity & directory when I finish (Test sign-in is there)", () => OpenSettings("Identity & directory")));
        p.Children.Add(Hint("Nothing is contacted until you add a sign-in and press Test sign-in; no directory password is stored."));
        return p;
    }

    // ---- 0.5.1 #15: BeyondTrust, tested before it is kept -------------------------------------------------------------------

    private FrameworkElement BeyondTrust()
    {
        var s = AppSettings.Current;
        var p = new StackPanel();
        p.Children.Add(H("BeyondTrust (optional)"));
        p.Children.Add(P("Privileged Remote Access / Remote Support (read-only: Jump Items, sessions, Jumpoints) and Password Safe (a managed account checked out for one session, in memory). Each part is tested before it is kept; the sites and switching the integration on need admin approval, asked once when you finish."));
        if (Managed("BeyondTrustEnabled", "BeyondTrustPraSite", "AllowIntegrationEdit") is { } why) { p.Children.Add(Hint(why)); return p; }
        TextBox T(string text) => new() { Text = text, Padding = new Thickness(5, 4, 5, 4), Margin = new Thickness(0, 0, 0, 4) };
        var site = T(s.BeyondTrustPraSite); var cid = T(s.BeyondTrustPraClientId); var secret = new PasswordBox { Margin = new Thickness(0, 0, 0, 4) };
        var ps = T(s.BeyondTrustPsHost); var runAs = T(s.BeyondTrustPsRunAs); var key = new PasswordBox { Margin = new Thickness(0, 0, 0, 4) };
        var result = Hint("");
        p.Children.Add(L("PRA SITE (https://…) · API CLIENT ID · SECRET")); p.Children.Add(site); p.Children.Add(cid); p.Children.Add(secret);
        p.Children.Add(B("Test and keep PRA", async () =>
        {
            result.Text = "Testing…";
            try
            {
                var jp = await new BeyondTrust.PraClient(site.Text.Trim(), cid.Text.Trim(), secret.Password).JumpointsAsync();
                s.BeyondTrustPraClientId = cid.Text.Trim(); s.ObserveSecrets["bt:pra"] = secret.Password; Save();
                _protected["bt.praSite"] = site.Text.Trim(); _protected["bt.enabled"] = true;
                result.Text = $"✓ PRA answered: {jp.Count} Jumpoint(s). Kept; the site and switching on are approved when you finish.";
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Net.Http.HttpRequestException or TaskCanceledException or OfflineBlockedException) { result.Text = "✕ " + ex.Message.Split('\n')[0]; }
        }));
        p.Children.Add(L("PASSWORD SAFE HOST (https://…) · RUN AS · API KEY")); p.Children.Add(ps); p.Children.Add(runAs); p.Children.Add(key);
        p.Children.Add(B("Test and keep Password Safe", async () =>
        {
            result.Text = "Testing…";
            try
            {
                using var c = new BeyondTrust.PasswordSafeClient(ps.Text.Trim(), key.Password, runAs.Text.Trim(), null);
                await c.SignInAsync(); var n = (await c.AccountsAsync()).Count; await c.SignOutAsync();
                s.BeyondTrustPsRunAs = runAs.Text.Trim(); s.ObserveSecrets["bt:pskey"] = key.Password; Save();
                _protected["bt.psHost"] = ps.Text.Trim(); _protected["bt.enabled"] = true;
                result.Text = $"✓ Password Safe answered: {n} account(s) you can request. Kept; the host is approved when you finish.";
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.Net.Http.HttpRequestException or TaskCanceledException or OfflineBlockedException) { result.Text = "✕ " + ex.Message.Split('\n')[0]; }
        }));
        p.Children.Add(result);
        p.Children.Add(Hint("A self-signed appliance certificate is refused until you trust it: Settings → Integrations → BeyondTrust → TLS shows its fingerprint."));
        return p;
    }

    // ---- 0.5.1 #15: import from other managers ---------------------------------------------------------------------------

    private FrameworkElement ImportPage()
    {
        var p = new StackPanel();
        p.Children.Add(H("Bring your connections (optional)"));
        p.Children.Add(P("Import hosts from mRemoteNG, SecureCRT, Royal TS, MobaXterm, PuTTY / KiTTY, WinSCP, RDCMan, .rdp files or Devolutions RDM. You see every host before anything is saved; passwords go into the encrypted vault. Or let the app find SSH, Remote Desktop and VNC machines on your network."));
        if (Managed("AllowHostAdd") is { } why) { p.Children.Add(Hint(why)); return p; }
        p.Children.Add(B("⇩ Import from another manager…", () => ConnectionImportDialog.Show(this), primary: true));
        if (Restrictions.Allowed("network.scan")) p.Children.Add(B("⌕ Find hosts on the network…", () => HostImportWindow.Show(this)));
        return p;
    }

    private FrameworkElement Personal()
    {
        var s = AppSettings.Current;
        var p = new StackPanel();
        p.Children.Add(H("Personal settings"));
        p.Children.Add(L("THEME"));
        var theme = new ComboBox { Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var t in new[] { "System", "Dark", "Light" }) theme.Items.Add(t);
        theme.SelectedItem = Enum.TryParse<ThemeMode>(s.Theme, out var m) ? m.ToString() : "System";
        theme.SelectionChanged += (_, _) => { s.Theme = (string)theme.SelectedItem; ThemeManager.Apply(Enum.Parse<ThemeMode>(s.Theme)); Save(); };
        p.Children.Add(theme);
        p.Children.Add(L("DEFAULT SSH USER NAME (quick connect and new hosts)"));
        var user = new TextBox { Text = s.DefaultUsername, Width = 200, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(5, 4, 5, 4) };
        user.LostFocus += (_, _) => { if (user.Text.Trim().Length > 0) { s.DefaultUsername = user.Text.Trim(); Save(); } };
        p.Children.Add(user);
        var confirm = new CheckBox { Content = "Ask before every change on a server (confirmations) — PRODUCTION hosts always ask", IsChecked = s.ConfirmDestructive, Margin = new Thickness(0, 10, 0, 0) };
        confirm.Checked += (_, _) => { s.ConfirmDestructive = true; Save(); }; confirm.Unchecked += (_, _) => { s.ConfirmDestructive = false; Save(); };
        p.Children.Add(confirm);
        var paste = new CheckBox { Content = "Confirm multi-line pastes into terminals (stops a pasted script running by accident)", IsChecked = s.TerminalConfirmPaste, Margin = new Thickness(0, 6, 0, 0) };
        paste.Checked += (_, _) => { s.TerminalConfirmPaste = true; Save(); }; paste.Unchecked += (_, _) => { s.TerminalConfirmPaste = false; Save(); };
        p.Children.Add(paste);
        p.Children.Add(Hint("Appearance presets, fonts, density, the sidebar and terminal profiles are in Settings → Appearance / Navigation / Terminals."));
        return p;
    }

    private FrameworkElement Ai()
    {
        var s = AppSettings.Current;
        var p = new StackPanel();
        p.Children.Add(H("AI features"));
        p.Children.Add(P("Optional: an AI engineer that explains output, drafts commands (never runs them without you), analyses logs and writes reports. Off means the AI module isn't loaded at all. On, the default engine is local (Foundry Local with phi-4-mini on this PC's NPU or GPU) — nothing leaves the PC unless you choose an internet provider later."));
        var off = new RadioButton { Content = "Off — no AI anywhere (default)", GroupName = "ai", IsChecked = s.AiFeatures != true, Margin = new Thickness(0, 4, 0, 4) };
        var local = new RadioButton { Content = "On, local only — Foundry Local + phi-4-mini, set up automatically (needs ~2.5 GB; NPU when present, else GPU/CPU)", GroupName = "ai", IsChecked = s.AiFeatures == true, Margin = new Thickness(0, 4, 0, 4) };
        off.Checked += (_, _) => { if (s.AiFeatures != false) { AiFeatures.Set(false); } };
        local.Checked += (_, _) => { s.AiAllowCloud = false; s.AiEngine = "npu"; s.FoundryAutoSetup = true; AiFeatures.Set(true); };
        p.Children.Add(off); p.Children.Add(local);
        p.Children.Add(Hint("Applies after a restart. Internet providers (OpenAI-compatible, Anthropic) and Ollama on a GPU server are enabled later in Settings → AI & models; an administrator can lock AI off for a company."));
        // 0.5.1 #14: the one-click Foundry Local set-up, here as well as in Settings and the AI panel — through the AI
        // module's seam (absent when the module isn't loaded: the AI-off app never loads it).
        if (LocalAiSetup.FoundryOneClick is { } setUp)
        {
            var foundry = Hint("");
            p.Children.Add(B("⚙ Set up Foundry Local now (install, start, phi-4-mini)", async () => { foundry.Text = "Setting up Foundry Local…"; foundry.Text = await setUp(); }));
            p.Children.Add(foundry);
        }
        return p;
    }

    private FrameworkElement Team()
    {
        var p = new StackPanel();
        p.Children.Add(H("Your team (optional)"));
        p.Children.Add(P("A shared library puts hosts and team credentials on a folder or file share, end-to-end encrypted per member: only ciphertext, signatures and public keys are written there. Create one for your team, or join the one your team's admin made (they read you the owner fingerprint over a call you trust and approve you with a 6-digit code)."));
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 10) };
        void Refresh() => status.Text = SharedLibraries.Clients.Count == 0 ? "No team library on this profile yet." : "Libraries: " + string.Join(", ", SharedLibraries.Clients.Select(c => c.State.Name + (c.State.Pending ? " (waiting for approval)" : "")));
        p.Children.Add(status);
        p.Children.Add(B("＋ Create a team library…", () =>
        {
            var dlg = new OpenFolderDialog { Title = "Empty folder for the library (a file share, NAS folder or synced folder)" };
            if (dlg.ShowDialog() != true) return;
            if (Dialogs.Prompt("Create a library", "Library name:", "Team") is not { Length: > 0 } name) return;
            if (Dialogs.Prompt("Create a library", "Your name (shown to members):", AppIdentity.Name) is not { Length: > 0 } me) return;
            var c = SharedLibraries.Create(dlg.FolderName, name.Trim(), me.Trim(), "General");
            Dialogs.Info("Library created", $"“{name}” is ready with you as its owner.\n\nYour owner fingerprint — read it to people who join:\n{c.Keys.Fingerprint}\n\nBack up this profile (Settings → Security): your owner key lives only here.");
            Refresh();
        }, primary: true));
        p.Children.Add(B("⇩ Join a team library…", () =>
        {
            var dlg = new OpenFolderDialog { Title = "The library's folder (file share or synced folder)" };
            if (dlg.ShowDialog() != true) return;
            if (Dialogs.Prompt("Join a library", "The owner's fingerprint (8 groups of 4, read to you by the owner):") is not { Length: > 0 } fp) return;
            if (Dialogs.Prompt("Join a library", "Your name (shown to the admins):", AppIdentity.Name) is not { Length: > 0 } me) return;
            var c = SharedLibraries.JoinShare(dlg.FolderName, fp, me.Trim());
            Dialogs.Info("Join request sent", $"Your fingerprint: {c.Keys.Fingerprint}\n\nAn admin starts a check; press Sync in Shared library to see your 6-digit code and read it to them. Once approved, the shared hosts and credentials appear.");
            Refresh();
        }));
        p.Children.Add(Hint("Shared library is always in the sidebar under TEAM (Settings → Security → Team features switches it off). Permissions are per person and per group, enforced by the encryption."));
        Refresh();
        return p;
    }

    private FrameworkElement Done()
    {
        var p = new StackPanel();
        p.Children.Add(H("All set"));
        p.Children.Add(P(SecuritySetupUi.ProtectionText()));
        var rep = EncryptionAudit.Run(SecureStore.DataDir, Log.Dir);
        p.Children.Add(P("Encryption check: " + rep.Summary));
        if (_protected.Count > 0) p.Children.Add(P($"🛡 Finish asks for admin approval once, for: {string.Join(", ", _protected.Keys.Select(k => ProtectedSettings.Find(k)?.Title ?? k))}."));
        if (_after.Count > 0) p.Children.Add(P("After Finish: " + string.Join(", ", _after.Keys) + " open."));
        p.Children.Add(Hint("Run this setup again any time from Settings → About. Help is one keypress away (F1), and every screen has a right-click menu."));
        return p;
    }
}

