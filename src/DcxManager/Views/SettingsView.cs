using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using DcxManager.Services;
using ThemeMode = DcxManager.Services.ThemeMode;   // .NET 9+ WPF has System.Windows.ThemeMode too
using DcxManager.Sessions;
using DcxManager.Ssh;
using DcxManager.Vultr;
using Microsoft.Win32;

namespace DcxManager.Views;

/// <summary>
/// Settings (0.2.1: the DarkCipher house theme's layout, shared across the DarkCipher Systems apps): the search box above a row of tabs,
/// one tab per category, each a single column of cards at most 900 px wide. A card has a title, what it applies to,
/// SMALL CAPS field labels with a muted caption under each control, 340 px dropdowns, toggle switches for on/off settings,
/// and its own Reset when its settings have safe defaults. Search looks across every tab.
/// </summary>
public sealed partial class SettingsView : UserControl, IDisposable, ISettingsKit
{
    /// <summary>Closing the tab releases the static reference (the Unloaded check alone missed it: the tab still parents
    /// the view at that moment — found by the closed-view leak check, s23).</summary>
    public void Dispose() { if (Current == this) Current = null; }

    private readonly AppSettings _s = AppSettings.Current;
    /// <summary>The settings this view edits (for cards contributed by modules, e.g. <see cref="AiSettingsCards"/>).</summary>
    internal AppSettings Settings => _s;
    private readonly ListView _accounts = new() { Height = 130 };
    private readonly TextBlock _storeStatus = new() { TextWrapping = TextWrapping.Wrap };

    /// <summary>The open Settings tab (it's a singleton), for "take me to the storage settings" links elsewhere.</summary>
    public static SettingsView? Current { get; private set; }
    private Border? _dataCard;

    /// <summary>Width of every dropdown in Settings (the house theme: one width, so the column reads cleanly).</summary>
    internal const double FieldWidth = 340;
    /// <summary>The widest a Settings page gets.</summary>
    internal const double PageWidth = 900;

    // ---- structure: tabs across the top, search across everything, one tab's cards at a time ---------------------------

    /// <summary>The app's own categories (tabs) in order: name, glyph, what belongs there. Modules add theirs (the AI module
    /// adds "AI & models" after "Cloud providers"); <see cref="Categories"/> is the merged list.</summary>
    private static readonly (string Name, string Glyph, string Blurb)[] CoreCategories =
    {
        ("Security & privacy", "⛨", "Encryption of everything stored, master password, two-factor sign-in, app lock and offline mode."),
        ("Identity & directory", "◎", "Who you are signed in to Windows as, automatic sign-in with Windows Hello, Active Directory / LDAPS / Microsoft Entra ID sign-in (view, edit, test), and single sign-on into SSH and Remote Desktop."),
        ("Cloud providers", "☁", "Accounts and API credentials for Vultr, Contabo, Hetzner Cloud, Microsoft Azure and Google Cloud. Credentials belong to one account each and are stored encrypted."),
        ("Terminals & connections", "⌨", "Behaviour of terminal tabs, reconnects, paste safety, Telnet, Remote Desktop, and terminal profiles."),
        ("Appearance", "◐", "Theme preset, accent, status colours, density, text size, zoom, corners, animation and fonts."),
        ("Navigation", "☰", "The sidebar's style, width, sections and pinned pages, the screen opened at start, and how trees and explorers open."),
        ("Data & backups", "⛁", "Encrypted backups and restores (pick what), scheduled backups, where the store lives, its snapshots, screenshots and crash recovery."),
        ("Integrations", "⇌", "The loopback-only local API, OBS broadcasts, the portal browser, syslog over TLS, log sinks (Elastic, Loki, Azure Monitor, Splunk, Graylog, webhooks), dashboards and e-mail notifications."),
        ("Import & export", "⇅", "Appearance and navigation settings as a file — no secrets, accounts or hosts — previewed before anything changes."),
        ("About", "ⓘ", "Version, the app icon, and update checks."),
    };

    /// <summary>The categories in order, the app's own and the modules' (0.2.0 phase 3).</summary>
    public static (string Name, string Glyph, string Blurb)[] Categories => AppModules.MergeSettingsCategories(CoreCategories);

    /// <summary>Card title → (category, scope) for the app's own cards. Scope says what a setting applies to. Modules'
    /// cards carry their own (<see cref="AppModules.FindSettingsCard"/>).</summary>
    private static readonly Dictionary<string, (string Category, string Scope)> Placement = new()
    {
        ["Admin protection"] = ("Security & privacy", "this Windows user on this PC · changes need admin approval"),
        ["Locks & restrictions"] = ("Security & privacy", "this Windows user · Group Policy can lock categories and turn options off"),
        ["Managed by your organisation"] = ("Security & privacy", "this PC · set by Group Policy"),
        ["Security & encryption"] = ("Security & privacy", "this PC's encrypted store"),
        ["Profiles"] = ("Identity & directory", "this PC · every profile"),
        ["This Windows account"] = ("Identity & directory", "this Windows user · read-only"),
        ["Automatic sign-in"] = ("Identity & directory", "this PC · every profile · Group Policy can set it"),
        ["Directory connections"] = ("Identity & directory", "every profile that signs in with the organisation"),
        ["Single sign-on to sessions"] = ("Identity & directory", "this Windows user · changes need admin approval"),
        ["Team features"] = ("Security & privacy", "this Windows user on this PC · changes need admin approval"),
        ["Offline mode"] = ("Security & privacy", "whole app"),
        ["AI features"] = ("Security & privacy", "whole app · applies after a restart"),
        ["Vultr accounts"] = ("Cloud providers", "per account"),
        ["Contabo accounts"] = ("Cloud providers", "per account"),
        ["Hetzner Cloud projects"] = ("Cloud providers", "per project"),
        ["Azure subscriptions"] = ("Cloud providers", "per subscription · read-only until allowed"),
        ["Google Cloud projects"] = ("Cloud providers", "per project · read-only until allowed"),
        ["Terminal & connections"] = ("Terminals & connections", "new terminal tabs"),
        ["Terminal profiles"] = ("Terminals & connections", "per profile · a host can pick its own"),
        ["Text editor"] = ("Terminals & connections", "the text and hex editors · Vim profiles"),
        ["Theme"] = ("Appearance", "whole app"),
        ["Branding & colours"] = ("Appearance", "whole app · can be set by Group Policy or a theme add-on"),
        ["Layout and text"] = ("Appearance", "whole app · terminals separate"),
        ["Typography"] = ("Appearance", "whole app · terminals separate"),
        ["Sidebar"] = ("Navigation", "whole app"),
        ["Navigation & explorers"] = ("Navigation", "whole app"),
        ["Backup & restore"] = ("Data & backups", "this Windows user · the rules need admin approval · Group Policy can set them"),
        ["Data & storage"] = ("Data & backups", "this PC's encrypted store"),
        ["Snapshots"] = ("Data & backups", "this PC's encrypted store"),
        ["Folder sync"] = ("Data & backups", "new sync jobs · this Windows user · permanent deletion needs admin approval"),
        ["Screenshots & recovery"] = ("Data & backups", "whole app"),
        ["Local API & broadcast"] = ("Integrations", "this PC only (loopback)"),
        ["IDE integration"] = ("Integrations", "this Windows user · the SSH config needs admin approval"),
        ["BeyondTrust"] = ("Integrations", "this Windows user · switching it on needs admin approval · secrets stay encrypted here"),
        ["BeyondTrust · Connection"] = ("Integrations", "this Windows user · the site needs admin approval · the secret stays encrypted here"),
        ["BeyondTrust · Password Safe"] = ("Integrations", "this Windows user · the host needs admin approval · the key stays encrypted here"),
        ["BeyondTrust · Checkout policy"] = ("Integrations", "this Windows user · allowing checkouts needs admin approval · Group Policy can set each"),
        ["BeyondTrust · Sessions"] = ("Integrations", "this Windows user · Group Policy can set each"),
        ["BeyondTrust · TLS"] = ("Integrations", "this Windows user · pins need admin approval · Group Policy can set them"),
        ["BeyondTrust · Scopes"] = ("Integrations", "what the integration may do"),
        ["BeyondTrust · Audit"] = ("Integrations", "this Windows user · needs the audit log open"),
        ["Bitwarden"] = ("Integrations", "this Windows user · switching it on needs admin approval · the master password is never stored"),
        ["Bitwarden · Server & sign-in"] = ("Integrations", "this Windows user · the server needs admin approval · the API key stays encrypted here"),
        ["Bitwarden · CLI"] = ("Integrations", "this PC · approving an unsigned build needs admin approval · Group Policy can set each"),
        ["Bitwarden · Secrets Manager"] = ("Integrations", "this Windows user · the access token stays encrypted here"),
        ["Bitwarden · Use"] = ("Integrations", "this Windows user · the uses need admin approval · Group Policy can set each"),
        ["Bitwarden · Audit"] = ("Integrations", "this Windows user · needs the audit log open"),
        ["Microsoft Teams"] = ("Integrations", "this Windows user · switching it on needs admin approval"),
        ["Pingdom"] = ("Integrations", "this Windows user · the token stays encrypted here"),
        ["Automation"] = ("Integrations", "this Windows user on this PC · changes need admin approval"),
        ["Syslog (TLS)"] = ("Integrations", "this Windows user on this PC · changes need admin approval"),
        ["Log sinks"] = ("Integrations", "this Windows user on this PC · the sink list needs admin approval · secrets stay encrypted here"),
        ["Dashboards & SIEM links"] = ("Integrations", "whole app · can be set by Group Policy"),
        ["E-mail notifications"] = ("Integrations", "this Windows user on this PC · server and recipients need admin approval"),
        ["Import & export"] = ("Import & export", "encrypted data exports (pick what) · appearance as plain JSON"),
        ["About"] = ("About", "this installation"),
        ["Updates"] = ("About", "whole app"),
    };

    /// <summary>Cards with a "Reset" (only settings whose defaults are safe to restore without asking anything else:
    /// accounts, keys, the store and security are never reset this way): card → what it restores.</summary>
    internal static readonly Dictionary<string, string> Resettable = new()
    {
        ["Theme"] = "theme mode, preset, accent, status colour intensity and following Windows high contrast",
        ["Layout and text"] = "density, text size, interface zoom, corners and animation",
        ["Typography"] = "every font and terminal-style accents",
        ["Sidebar"] = "sidebar style and width, the screen opened at start, where Hosts lives when you connect and the pinned pages",
        ["Navigation & explorers"] = "sidebar section order, hidden sections and pages, and remembering expanded sections",
        ["Terminal & connections"] = "scrollback, default user name, connect timeout, right-click mode, reconnect behaviour, paste confirmation, Telnet (off) and embedded Remote Desktop",
    };

    private readonly WrapPanel _tabs = new() { Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBox _search = Hint.Set(new TextBox { Padding = new Thickness(6, 4, 6, 4), Width = FieldWidth, ToolTip = "Search every setting in every tab (Ctrl+F)" }, "Search settings…");
    private readonly StackPanel _page = new() { Margin = new Thickness(24, 14, 18, 24), MaxWidth = PageWidth, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Dictionary<string, FrameworkElement> _catHeads = new();
    private readonly TextBlock _searchHead = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 10), TextWrapping = TextWrapping.Wrap };
    private Dictionary<Border, string>? _corpus;
    /// <summary>The tab shown last this session (Settings reopens there).</summary>
    private static string _lastCategory = CoreCategories[0].Name;

    /// <summary>Where a card lives and what its settings apply to: the app's own table, or the module that contributed it.</summary>
    private static (string Category, string? Scope)? PlacementOf(string title) =>
        Placement.TryGetValue(title, out var p) ? (p.Category, p.Scope) : AppModules.FindSettingsCard(title) is { } c ? (c.Category, c.Scope) : null;
    public string ShownCategory { get; private set; } = "";

    public SettingsView()
    {
        Current = this;
        Unloaded += (_, _) => { if (!IsLoaded && Current == this && Parent == null) Current = null; };
        // Pre-0.1.11 the collapsed cards were remembered; cards are always open now (migration, ADR-028).
        if (_s.CollapsedSettingsCards.Count > 0) { _s.CollapsedSettingsCards.Clear(); try { AppSettings.Save(); } catch { } }

        _searchHead.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _page.Children.Add(_searchHead);
        var built = new (string Category, Func<Border> Make)[]
        {
            ("Security & privacy", ProtectionCard), ("Security & privacy", LocksCard), ("Security & privacy", PolicyCard), ("Security & privacy", Security), ("Security & privacy", TeamCard), ("Security & privacy", OfflineCard), ("Security & privacy", AiFeaturesCard),
            ("Cloud providers", Vultr), ("Cloud providers", ContaboCard), ("Cloud providers", HetznerCard), ("Cloud providers", AzureCard), ("Cloud providers", GcpCard),
            ("Terminals & connections", Terminal), ("Terminals & connections", TerminalProfilesCard), ("Terminals & connections", TmuxCard), ("Terminals & connections", EditorCard),
            ("Appearance", ThemeCard), ("Appearance", BrandingCard), ("Appearance", LayoutCard), ("Appearance", TypographyCard),
            ("Navigation", SidebarCard), ("Navigation", NavigationCard),
            ("Identity & directory", IdentityAccountCard), ("Identity & directory", AutoSignInCard), ("Identity & directory", DirectoryConnectionsCard), ("Identity & directory", SsoCard), ("Identity & directory", ProfilesCard),
            ("Data & backups", BackupCard), ("Data & backups", Data), ("Data & backups", SnapshotsCard), ("Data & backups", FolderSyncCard), ("Data & backups", ScreenshotsCard),
            ("Integrations", PortalBrowserCard), ("Integrations", LocalApiCard), ("Integrations", SyslogCard), ("Integrations", LogSinksCard), ("Integrations", DashboardsCard), ("Integrations", NotificationsCard), ("Integrations", AutomationCard), ("Integrations", TeamsCard), ("Integrations", CloudPcCard), ("Integrations", M365Card), ("Integrations", PingdomCard), ("Integrations", IdeCard),
            ("Import & export", TransferCard),
            ("About", AboutCard), ("About", Updates),
        };
        built = built.Concat(BeyondTrustCards()).ToArray();   // 0.5.1 #2: Integrations → BeyondTrust is a page of its own
        built = built.Concat(BitwardenCards()).ToArray();   // 0.6.14 (ADR-093): and so is Bitwarden
        foreach (var (name, _, blurb) in Categories)
        {
            _page.Children.Add(_catHeads[name] = CategoryHead(name, blurb));
            foreach (var (_, make) in built.Where(b => b.Category == name)) _page.Children.Add(make());
            // Modules' cards (0.2.0 phase 3) come after the app's own in the same category, in registration order.
            foreach (var c in AppModules.SettingsCardsIn(name)) _page.Children.Add(Restyle(c.Make(this)));
            var tab = new RadioButton { Content = name, Tag = name, GroupName = "settings-tabs", Style = (Style)FindResource("TabStripItem") };
            System.Windows.Automation.AutomationProperties.SetName(tab, name + " settings");
            tab.Checked += (_, _) => { if (_search.Text.Length == 0) ShowCategory(name); else _search.Text = ""; };
            _tabs.Children.Add(tab);
        }
        _scroll.Content = _page;

        // Header: the title and the search box above the tabs (ServerManagement has far more settings than one screen of
        // tabs can show, so search stays first).
        var head = new StackPanel { Margin = new Thickness(24, 16, 18, 0) };
        var titleRow = new DockPanel();
        System.Windows.Automation.AutomationProperties.SetName(_search, "Search settings");
        DockPanel.SetDock(_search, Dock.Right);
        _search.HorizontalAlignment = HorizontalAlignment.Right;
        titleRow.Children.Add(_search);
        titleRow.Children.Add(new TextBlock { Text = "Settings", Style = (Style)FindResource("PageTitle"), VerticalAlignment = VerticalAlignment.Center });
        head.Children.Add(titleRow);
        if (AppModules.MenuSurfaceCount("settings-card") > 0)   // only when a module adds "Explain … with AI" to the cards
            head.Children.Add(new TextBlock { Text = "Right-click any option → ✦ Explain it with AI (sent only when you click; keys and passwords are never included).", Style = (Style)FindResource("Caption"), Margin = new Thickness(0, 4, 0, 0) });
        var strip = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Child = _tabs };
        strip.SetResourceReference(Border.BorderBrushProperty, "Line");
        head.Children.Add(strip);
        System.Windows.Automation.AutomationProperties.SetName(_tabs, "Settings categories");

        var root = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);
        root.Children.Add(_scroll);
        Content = root;

        _search.TextChanged += (_, _) => ApplySearch();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.F && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control) { _search.Focus(); _search.SelectAll(); e.Handled = true; }
            else if (e.Key == System.Windows.Input.Key.Escape && _search.IsKeyboardFocusWithin && _search.Text.Length > 0) { _search.Text = ""; e.Handled = true; }
        };
        RevealCategory(_catHeads.ContainsKey(_lastCategory) ? _lastCategory : CoreCategories[0].Name);
    }

    /// <summary>Self-test: cards shown right now, the search, the tabs, and a reset without the dialog's typing.</summary>
    internal IReadOnlyList<string> VisibleCardsForTest => _cards.Where(kv => kv.Value.Visibility == Visibility.Visible).Select(kv => kv.Key).ToList();
    internal IReadOnlyList<string> CardTitlesForTest => _cards.Keys.ToList();
    internal Border? CardForTest(string title) => _cards.TryGetValue(title, out var b) ? b : null;
    internal void SearchForTest(string q) => _search.Text = q;
    internal void ResetCardForTest(string title) => ResetCard(title);
    internal IReadOnlyList<string> TabsForTest => _tabs.Children.OfType<RadioButton>().Select(r => (string)r.Tag).ToList();
    internal string? SelectedTabForTest => _tabs.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as string;
    internal static string CategoryOfForTest(string title) => CategoryOf(title);

    private FrameworkElement CategoryHead(string name, string blurb)
    {
        var p = new StackPanel { Margin = new Thickness(0, 0, 0, 12), Tag = name };
        p.Children.Add(new TextBlock { Text = blurb, Style = (Style)FindResource("Hint") });
        if (name == "Integrations") p.Children.Add(_integrationPages);
        return p;
    }

    // ---- Integrations: one page per integration (0.5.1 #2) ------------------------------------------------------------

    /// <summary>The page selector under the Integrations blurb: All, then each integration (BeyondTrust's many cards are one page).</summary>
    private readonly WrapPanel _integrationPages = new() { Margin = new Thickness(0, 10, 0, 0) };
    private string _integrationPage = "All";

    /// <summary>The integration page a card belongs to.</summary>
    internal static string IntegrationPageOf(string cardTitle) => cardTitle.StartsWith("BeyondTrust", StringComparison.Ordinal) ? "BeyondTrust" : cardTitle.StartsWith("Bitwarden", StringComparison.Ordinal) ? "Bitwarden" : cardTitle switch
    {
        "Microsoft Teams" => "Teams", "Syslog (TLS)" => "Syslog", "Log sinks" => "Log sinks & Sentinel", "Dashboards & SIEM links" => "Dashboards & Grafana",
        "E-mail notifications" => "E-mail", "Local API & broadcast" => "Local API", "IDE integration" => "IDE", _ => cardTitle,
    };

    private void BuildIntegrationPages()
    {
        _integrationPages.Children.Clear();
        var pages = new[] { "All" }.Concat(_cards.Keys.Where(t => CategoryOf(t) == "Integrations").Select(IntegrationPageOf).Distinct()).ToList();
        foreach (var page in pages)
        {
            var b = new RadioButton { Content = page, GroupName = "integration-pages", IsChecked = page == _integrationPage, Margin = new Thickness(0, 0, 6, 6), Style = (Style)FindResource("TabStripItem") };
            System.Windows.Automation.AutomationProperties.SetName(b, page + " integration settings");
            b.Checked += (_, _) => { _integrationPage = page; if (ShownCategory == "Integrations") ShowCategory("Integrations"); };
            _integrationPages.Children.Add(b);
        }
    }

    /// <summary>Opens Settings → Integrations on one integration's page ("BeyondTrust", "Teams", …).</summary>
    public void RevealIntegration(string page)
    {
        _integrationPage = page;
        foreach (var b in _integrationPages.Children.OfType<RadioButton>()) b.IsChecked = (string)b.Content == page;
        RevealCategory("Integrations");
    }

    /// <summary>Shows one tab's cards (search cleared).</summary>
    public void RevealCategory(string name)
    {
        if (!_catHeads.ContainsKey(name)) return;
        if (_search.Text.Length > 0) _search.Text = "";
        ShowCategory(name);
    }

    private void ShowCategory(string name)
    {
        ShownCategory = _lastCategory = name;
        foreach (var tab in _tabs.Children.OfType<RadioButton>()) tab.IsChecked = (string)tab.Tag == name;
        _searchHead.Visibility = Visibility.Collapsed;
        foreach (var (cat, head) in _catHeads) head.Visibility = cat == name ? Visibility.Visible : Visibility.Collapsed;
        if (name == "Integrations" && _integrationPages.Children.Count == 0) BuildIntegrationPages();
        foreach (var (title, card) in _cards)
            card.Visibility = CategoryOf(title) == name && (name != "Integrations" || _integrationPage == "All" || IntegrationPageOf(title) == _integrationPage) ? Visibility.Visible : Visibility.Collapsed;
        _scroll.ScrollToTop();
    }

    private static string CategoryOf(string cardTitle) => PlacementOf(cardTitle)?.Category ?? "Data & backups";

    /// <summary>Text a card can be found by: its title, hint, scope and the labels of its options (never the contents of
    /// text or password boxes).</summary>
    private Dictionary<Border, string> Corpus()
    {
        if (_corpus != null) return _corpus;
        _corpus = new();
        foreach (var (title, card) in _cards)
        {
            var sb = new StringBuilder(title).Append(' ').Append(CategoryOf(title)).Append(' ');
            void Walk(DependencyObject d)
            {
                switch (d)
                {
                    case TextBox or PasswordBox: return;
                    case TextBlock t: sb.Append(t.Text).Append(' '); break;
                    case ContentControl { Content: string s }: sb.Append(s).Append(' '); break;
                }
                foreach (var c in LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>()) Walk(c);
            }
            Walk(card);
            _corpus[card] = sb.ToString();
        }
        return _corpus;
    }

    /// <summary>Search across every tab: cards whose title, description or option labels contain all the words.</summary>
    private void ApplySearch()
    {
        var q = _search.Text.Trim();
        if (q.Length == 0) { ShowCategory(_lastCategory); return; }
        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int hits = 0;
        foreach (var (card, text) in Corpus())
        {
            bool hit = words.All(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
            card.Visibility = hit ? Visibility.Visible : Visibility.Collapsed;
            if (hit) hits++;
        }
        foreach (var head in _catHeads.Values) head.Visibility = Visibility.Collapsed;
        foreach (var tab in _tabs.Children.OfType<RadioButton>()) tab.IsChecked = false;
        ShownCategory = "";
        _searchHead.Visibility = Visibility.Visible;
        _searchHead.Text = hits == 0 ? $"Nothing matches “{q}”. Try fewer or other words (e.g. “proxy”, “password”, “font”)." : $"{hits} card(s) match “{q}” in every tab — each card says which tab it is in. Esc clears the search.";
        _scroll.ScrollToTop();
    }

    /// <summary>Restores the defaults of one card's settings (after a confirmation), applies them, and reopens Settings on
    /// that card's tab. Nothing outside the card changes.</summary>
    private void ResetCard(string title, bool ask = true)
    {
        if (!Resettable.TryGetValue(title, out var list)) return;
        if (ask && !Dialogs.Confirm("Reset to defaults", $"Reset “{title}” to the defaults?\n\nThis restores: {list}.\n\nAccounts, keys, terminal profiles and every other card stay as they are.", "Reset", danger: true)) return;
        if (title == "Terminal & connections")
        {
            var d = new AppSettings();
            _s.Scrollback = d.Scrollback; _s.DefaultUsername = d.DefaultUsername; _s.ConnectTimeoutSeconds = d.ConnectTimeoutSeconds; _s.TerminalRightClick = d.TerminalRightClick;
            _s.TerminalAutoReconnect = d.TerminalAutoReconnect; _s.AutoReconnectMaxSeconds = d.AutoReconnectMaxSeconds; _s.TerminalConfirmPaste = d.TerminalConfirmPaste;
            _s.RdpEmbedded = d.RdpEmbedded;
            // Telnet is admin-protected (0.3.0 §1): off is stricter, so it applies at once; the administrator makes it stick.
            if (_s.TelnetEnabled != d.TelnetEnabled) _ = ProtectedSettings.ChangeAsync("reset Terminal & connections", "net.telnet", d.TelnetEnabled);
        }
        else ResetValues(title);
        Save();
        AuditLog.Write("you", "settings", $"reset “{title}” to defaults");
        ApplyLook();
        // Rebuild Settings so every control shows the restored values.
        var main = MainWindow.Instance;
        if (main?.Panes.AllTabs.FirstOrDefault(t => t.Content == this) is { } tab) { main.CloseTab(tab); main.Navigate("settings"); Current?.RevealCard(title); }
    }

    /// <summary>Applies every appearance and navigation setting to the running app (after a reset or an import).</summary>
    internal static void ApplyLook()
    {
        var mode = AppSettings.Current.Theme switch { "Dark" => ThemeMode.Dark, "Light" => ThemeMode.Light, _ => ThemeMode.System };
        ThemeManager.Apply(mode);
        Typography.Apply();
        NavLayout.ApplyDensity();
        MainWindow.Instance?.SyncThemeBox();
        MainWindow.Instance?.BuildNav();
    }

    /// <summary>A card: its title, what it applies to, an optional description, its controls, and its Reset when it has
    /// one. Every card is open; search and the tabs decide what shows.</summary>
    internal Border Card(string title, string hint, params UIElement[] children)
    {
        var p = new StackPanel();
        p.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("CardTitle") });
        if (PlacementOf(title) is { Scope: { } } place)
            p.Children.Add(new TextBlock { Text = $"Applies to: {place.Scope}", Style = (Style)FindResource("Caption"), Margin = new Thickness(0, 0, 0, 8), ToolTip = $"Tab: {place.Category}" });
        if (hint.Length > 0) p.Children.Add(new TextBlock { Text = hint, Style = (Style)FindResource("Hint"), Margin = new Thickness(0, 0, 0, 10) });
        foreach (var c in children) p.Children.Add(c);
        if (Resettable.ContainsKey(title))
        {
            var reset = Btn($"Reset {title.ToLowerInvariant()}", () => ResetCard(title), "SmallButton");
            reset.HorizontalAlignment = HorizontalAlignment.Left;
            reset.Margin = new Thickness(0, 12, 0, 0);
            reset.ToolTip = $"Restores the defaults of this card only (asks first): {Resettable[title]}.";
            p.Children.Add(reset);
        }
        var card = new Border { Style = (Style)FindResource("Card"), Child = p, Margin = new Thickness(0, 0, 0, 12) };
        _cards[title] = card;
        Restyle(card);
        ApplyLocks(card);
        card.IsVisibleChanged += (_, e) => { if (e.NewValue is true) ApplyLocks(card); };
        // Remember what was right-clicked so the menu can offer "Explain this option" for that row.
        DependencyObject? clicked = null;
        card.PreviewMouseRightButtonDown += (_, e) => clicked = e.OriginalSource as DependencyObject;
        Menus.Attach(card, () =>
        {
            var opt = OptionAt(clicked, card);
            clicked = null;
            // The modules' items (the AI's "Explain this option / these settings with AI"), then this card's reset.
            var ctx = new SettingsCardMenuContext(title, hint, opt?.Label, opt == null ? null : Trim(opt.Label), opt?.Value, opt?.Tip,
                () => Options(card).Select(x => x.Label).Distinct().Take(40));
            var items = AppModules.MenuItems("settings-card", ctx).ToList();
            if (Resettable.ContainsKey(title)) { if (items.Count > 0) items.Add(Menus.Sep()); items.Add(Menus.Item($"Reset {title.ToLowerInvariant()}…", () => ResetCard(title))); }
            return items;
        });
        return card;
    }

    /// <summary>The house theme on a card's controls: on/off settings become toggle switches, dropdowns one width, and a
    /// control's tooltip also shows as the muted caption under it. Lists and tables keep their own look.</summary>
    private T Restyle<T>(T root) where T : DependencyObject
    {
        var sw = (Style)FindResource("Switch");
        void Walk(DependencyObject d)
        {
            switch (d)
            {
                case ListView or ListBox or DataGrid or TreeView: return;
                // WPF gives a new control its implicit style at once, so "no style of its own" is the local value.
                case Button b when b.ReadLocalValue(HorizontalAlignmentProperty) == DependencyProperty.UnsetValue && LogicalTreeHelper.GetParent(b) is StackPanel { Orientation: Orientation.Vertical }:
                    b.HorizontalAlignment = HorizontalAlignment.Left;
                    break;
                case CheckBox c when c.ReadLocalValue(StyleProperty) == DependencyProperty.UnsetValue:
                    c.Style = sw;
                    if (c.ToolTip is string ct && LogicalTreeHelper.GetParent(c) is StackPanel { Tag: not "setting" } parent && Caption(ct) is { } cap)
                    {
                        cap.Margin = new Thickness(42, 2, 0, 4);
                        parent.Children.Insert(parent.Children.IndexOf(c) + 1, cap);
                    }
                    break;
                case ComboBox cb when !cb.IsEditable:
                    cb.Width = FieldWidth;
                    cb.HorizontalAlignment = HorizontalAlignment.Left;
                    break;
                case StackPanel { Tag: "setting" } sp when sp.Children.Count == 2 && sp.Children[1] is FrameworkElement { ToolTip: string tip } && Caption(tip) is { } c2:
                    sp.Children.Add(c2);
                    sp.Margin = new Thickness(0, 0, 0, 6);
                    break;
            }
            foreach (var child in LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>().ToList()) Walk(child);
        }
        Walk(root);
        return root;
    }

    private TextBlock? Caption(string text) => text.Length == 0 ? null : new TextBlock { Text = text, Style = (Style)FindResource("Caption"), MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left };

    private readonly Dictionary<string, Border> _cards = new();

    private static string Trim(string s) => s.Length > 48 ? s[..48] + "…" : s;

    /// <summary>A settings row as the AI sees it: its label, a non-secret current value and the tooltip.</summary>
    private sealed record SettingOption(string Label, string? Value, string? Tip);

    /// <summary>The option under the mouse: a labelled field, a checkbox or a button. Text boxes and password boxes never
    /// contribute their value (they hold keys, URLs, user names); only on/off and list choices do.</summary>
    private static SettingOption? OptionAt(DependencyObject? d, Border card)
    {
        for (var x = d; x != null && x != card; x = x is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(x) ?? LogicalTreeHelper.GetParent(x) : LogicalTreeHelper.GetParent(x))
            if (Describe(x) is { } o) return o;
        return null;
    }

    private static SettingOption? Describe(DependencyObject x) => x switch
    {
        StackPanel { Tag: "setting" } p when p.Children.Count >= 2 && p.Children[0] is TextBlock label =>
            new SettingOption(label.Text, p.Children[1] switch
            {
                ComboBox cb => cb.SelectedItem is ComboBoxItem ci ? ci.Content?.ToString() : cb.SelectedItem?.ToString(),
                CheckBox c => c.IsChecked == true ? "on" : "off",
                _ => null,
            }, (p.Children[1] as FrameworkElement)?.ToolTip as string),
        CheckBox c => new SettingOption(c.Content is TextBlock t ? t.Text : c.Content?.ToString() ?? "", c.IsChecked == true ? "on" : "off", c.ToolTip as string),
        Button b when b.Content is string s => new SettingOption("Button: " + s, null, b.ToolTip as string),
        Button { Content: TextBlock } b => new SettingOption("Button: " + AdminApproval.TextOf(b.Content), null, b.ToolTip as string),
        _ => null,
    };

    private static IEnumerable<SettingOption> Options(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (Describe(child) is { } o) { yield return o; if (child is StackPanel { Tag: "setting" }) continue; }
            foreach (var d in Options(child)) yield return d;
        }
    }

    // ISettingsKit (0.2.0 phase 6f): the card builder a module's cards get (the AI's); the helpers themselves stay internal.
    Border ISettingsKit.Card(string title, string hint, params UIElement[] children) => Card(title, hint, children);
    TextBlock ISettingsKit.L(string text) => L(text);
    WrapPanel ISettingsKit.Row(params UIElement[] items) => Row(items);
    Button ISettingsKit.Btn(string text, Action action, string? style) => Btn(text, action, style);
    StackPanel ISettingsKit.Labeled(string label, FrameworkElement element) => Labeled(label, element);
    CheckBox ISettingsKit.Check(string text, bool value, Action<bool> set) => Check(text, value, set);
    void ISettingsKit.Save() => Save();
    AppSettings ISettingsKit.Settings => Settings;

    /// <summary>A SMALL CAPS field label.</summary>
    internal TextBlock L(string t) => new() { Text = t, Style = (Style)FindResource("Label"), Margin = new Thickness(0, 8, 0, 4) };
    internal WrapPanel Row(params UIElement[] items) { var w = new WrapPanel { Margin = new Thickness(0, 4, 0, 4) }; foreach (var i in items) { if (i is FrameworkElement f) f.Margin = new Thickness(0, 0, 12, 4); if (i is Button b) b.VerticalAlignment = VerticalAlignment.Bottom; w.Children.Add(i); } return w; }
    internal Button Btn(string t, Action a, string? style = null) { var b = new Button { Content = t }; if (style != null) b.Style = (Style)FindResource(style); b.Click += (_, _) => a(); return b; }

    internal void Save()
    {
        try { AppSettings.Save(); MainWindow.Instance.Status("Settings saved (encrypted)."); }
        catch (Exception ex) { Dialogs.Error("Settings", ex.Message); }
    }

    /// <summary>An on/off setting (drawn as a toggle switch).</summary>
    /// <summary>A switch for an admin-protected setting (0.3.0 §1): clicking it asks for the change through the admin gate
    /// (Windows Hello when asked for, then UAC) and the switch shows what is in force afterwards. <paramref name="change"/>
    /// replaces the plain change where more has to happen (Hello sign-in, sandbox mode); <paramref name="current"/> what it
    /// shows, when that isn't simply the setting.</summary>
    public CheckBox ProtectedCheck(string key, string text, Func<bool, Task<bool>>? change = null, Func<bool>? current = null)
    {
        var it = ProtectedSettings.Find(key) ?? throw new ArgumentException("not a protected setting: " + key);
        bool Value() => current?.Invoke() ?? it.Get(_s)?.GetValueKind() == System.Text.Json.JsonValueKind.True;
        var c = new CheckBox
        {
            Content = new TextBlock { Text = "🛡 " + text, TextWrapping = TextWrapping.Wrap }, IsChecked = Value(), Margin = new Thickness(0, 8, 0, 0), Style = (Style)FindResource("Switch"),
            ToolTip = "🛡 Protected: changing it needs admin approval (UAC)" + (ProtectedSettings.IsProtected(key) ? "." : " — not protected yet on this PC."),
        };
        SetSetting(c, it.Setting);
        // Set by Group Policy (0.3.0 §10): shown, not changeable.
        if (PolicySettings.Get(it.Setting) is { } managed)
        {
            c.IsEnabled = false;
            c.ToolTip = $"Set by your organisation's policy ({managed.Scope} policy {it.Setting} = {managed.Shown}).";
            return c;
        }
        c.Click += async (_, _) =>
        {
            bool want = c.IsChecked == true;
            c.IsChecked = !want;
            c.IsEnabled = false;
            try { if (change != null) await change(want); else await ProtectedSettings.ChangeAsync((want ? "turn on: " : "turn off: ") + text, key, want); }
            catch (Exception ex) { Dialogs.Error("Admin protection", ex.Message); }
            finally { c.IsEnabled = true; c.IsChecked = Value(); }
        };
        return c;
    }

    internal CheckBox Check(string text, bool value, Action<bool> set, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(value))] string? valueExpr = null)
    {
        var setting = SettingFromExpression(valueExpr);
        // 0.5.0 (ADR-059): an admin-protected on/off goes through the admin gate, whichever card draws it; the card's own
        // follow-up (repaint, refresh) still runs once the change is approved.
        if (setting != null && ProtectedSettings.Items.FirstOrDefault(i => i.Setting == setting) is { } prot && !valueExpr!.TrimStart().StartsWith('!'))
            return ProtectedCheck(prot.Key, text, async want =>
            {
                if (!await ProtectedSettings.ChangeAsync((want ? "turn on: " : "turn off: ") + text, prot.Key, want)) return false;
                set(want); Save();
                return true;
            });
        var c = new CheckBox { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, IsChecked = value, Margin = new Thickness(0, 8, 0, 0), Style = (Style)FindResource("Switch") };
        if (setting != null) SetSetting(c, setting);   // 0.5.0: policy lock and 🛡 (SettingsView.Locks)
        c.Checked += (_, _) => { set(true); Save(); };
        c.Unchecked += (_, _) => { set(false); Save(); };
        return c;
    }
}
