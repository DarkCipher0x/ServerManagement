using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using DcxManager.Services;
using DcxManager.Ssh;
using DcxManager.Views;
using DcxManager.Vultr;

namespace DcxManager;

// MainWindow: the sidebar: sections, order, hiding, expansion, items and their menus, resizing (moved unchanged from MainWindow.xaml.cs in 0.2.0 phase 6½).
public partial class MainWindow
{
    // ---- sidebar ------------------------------------------------------------------------------------------------

    /// <summary>Every sidebar section in its default order. Since 0.1.11 (ADR-028) cloud providers live in one "Cloud
    /// providers" workspace instead of a section each.</summary>
    /// 0.6.16 (DarkCipher0x: "separate the tools into their own section of TOOLS", "put the documents and notes into a
    /// DOCUMENTS section", "a bit better organised"): TOOLS and DOCUMENTS sections; Help moved to SYSTEM.
    private static readonly string[] AllNavSections = { "OVERVIEW", "SERVERS", "TOOLS", "DOCUMENTS", "AUTOMATION", "LOCAL", "VAULT", "TEAM", "AI", "LOGS", "SYSTEM" };

    /// <summary>The sections that exist now, in their default order; the user's order and hidden set come from settings
    /// (NavLayout). TEAM (shared library, team hosts and credentials) exists only while a library add-on is enabled; AI only
    /// while the AI module is loaded (since 0.6.16 Reports and Help live in DOCUMENTS and SYSTEM, so it would be empty).</summary>
    public static string[] NavSections => AllNavSections.Where(n => (n != "TEAM" || Library.SharedLibraries.Enabled) && (n != "AI" || AppModules.NavItemsIn("AI").Any())).ToArray();

    /// <summary>The glyph in front of a section header (the same family as the items' glyphs).</summary>
    public static string SectionGlyph(string key) => key switch { "OVERVIEW" => "◈", "SERVERS" => "▣", "TOOLS" => "🧰", "DOCUMENTS" => "📄", "AUTOMATION" => "⟐", "LOCAL" => "⌨", "VAULT" => "⚿", "TEAM" => "⇄", "LOGS" => "☷", "AI" => "✦", "SYSTEM" => "⚙", _ => "•" };

    /// <summary>A section's title (its key).</summary>
    public static string SectionTitle(string key) => key;

    /// <summary>Sidebar sections expanded right now. Every section starts collapsed (ADR-028) unless the user chose to
    /// remember expanded sections between restarts (Settings → Navigation).</summary>
    private readonly HashSet<string> _navExpanded = new();

    /// <summary>One-time tidy of saved sidebar state: names of sections that no longer exist (VULTR, CONTABO before 0.1.11)
    /// are dropped, and the pre-0.1.11 "collapsed" list is retired (sections now start collapsed).</summary>
    public static bool MigrateNavSettings(AppSettings s)
    {
        bool changed = false;
        foreach (var list in new[] { s.NavSectionOrder, s.HiddenNavSections, s.ExpandedNavSections })
            changed |= list.RemoveAll(n => !NavSections.Contains(n)) > 0;
        if (s.CollapsedNavSections.Count > 0) { s.CollapsedNavSections.Clear(); changed = true; }
        if (!s.RememberNavExpansion && s.ExpandedNavSections.Count > 0) { s.ExpandedNavSections.Clear(); changed = true; }
        return changed;
    }

    private readonly Dictionary<string, StackPanel> _navPanels = new();
    /// <summary>Self-test: each sidebar section and whether its items are shown (expanded).</summary>
    internal IReadOnlyDictionary<string, bool> NavSectionOpen => _navPanels.ToDictionary(kv => kv.Key, kv => kv.Value.Visibility == Visibility.Visible);
    /// <summary>Self-test: a fresh start's sidebar state (what the constructor does), without restarting.</summary>
    internal void ResetNavExpansionForTest() { _navExpanded.Clear(); if (AppSettings.Current.RememberNavExpansion) _navExpanded.UnionWith(AppSettings.Current.ExpandedNavSections); BuildNav(); }
    internal void SetNavExpandedForTest(string section, bool open) { if (open) _navExpanded.Add(section); else _navExpanded.Remove(section); SaveNavExpansion(); BuildNav(); }

    internal void SaveNavExpansion()
    {
        var s = AppSettings.Current;
        s.ExpandedNavSections = s.RememberNavExpansion ? NavSections.Where(_navExpanded.Contains).ToList() : new();
        SaveSettings();
    }

    /// <summary>The section order currently shown (all sections, hidden ones included).</summary>
    public List<string> NavOrder => NavLayout.Order(NavSections, AppSettings.Current.NavSectionOrder);

    private static void SaveSettings() { try { AppSettings.Save(); } catch { } }

    public void MoveNavSection(string name, int delta)
    {
        AppSettings.Current.NavSectionOrder = NavLayout.Move(NavOrder, name, delta);
        SaveSettings(); BuildNav();
    }

    public void SetNavSectionHidden(string name, bool hide)
    {
        var hidden = AppSettings.Current.HiddenNavSections;
        if (hide && !NavLayout.CanHide(NavOrder, hidden, name)) { Dialogs.Info("Sidebar", "At least one section has to stay visible."); return; }
        hidden.Remove(name);
        if (hide) hidden.Add(name);
        SaveSettings(); BuildNav();
    }

    public void ResetNavLayout()
    {
        AppSettings.Current.NavSectionOrder.Clear();
        AppSettings.Current.HiddenNavSections.Clear();
        AppSettings.Current.NavItemOrder.Clear();
        SaveSettings(); BuildNav();
    }

    // ---- 0.6.2: rearranging and pinning pages (DarkCipher0x: "the items on the left main sidebar, should be rearrangable and
    // pinable"). Drag a page within its section to reorder it; drag it onto PINNED (or a pinned page) to pin it there;
    // drag a pinned page to reorder the pins, or out of PINNED to unpin it. The 📌 on a page (shown while pointed at)
    // pins / unpins in one click; right-click has Move up / Move down. Saved in NavItemOrder / PinnedNavItems (policies).

    private const string NavItemFormat = "dcx-nav-item";
    /// <summary>Each page's section, and each section's pages in the user's order (hidden pages included).</summary>
    private readonly Dictionary<string, string> _sectionOf = new();
    private readonly Dictionary<string, List<string>> _sectionOrder = new();
    /// <summary>Self-test: a section's pages in the order shown (hidden ones included).</summary>
    internal IReadOnlyList<string> NavItemsOf(string section) => _sectionOrder.TryGetValue(section, out var l) ? l : Array.Empty<string>();

    /// <summary>Moves a page up or down among the visible pages of its section.</summary>
    public void MoveNavItem(string key, int delta)
    {
        if (!_sectionOf.TryGetValue(key, out var sec)) return;
        var hidden = AppSettings.Current.HiddenNavItems;
        var visible = _sectionOrder[sec].Where(k => !hidden.Contains(k)).ToList();
        int i = visible.IndexOf(key), j = i + delta;
        if (i < 0 || j < 0 || j >= visible.Count) return;
        PlaceNavItem(key, visible[j]);
    }

    /// <summary>Puts a page where another page of the same section is (drag and drop).</summary>
    internal void PlaceNavItem(string key, string target)
    {
        if (!_sectionOf.TryGetValue(key, out var sec) || _sectionOf.GetValueOrDefault(target) != sec || key == target) return;
        var s = AppSettings.Current;
        s.NavItemOrder = NavLayout.WithSection(s.NavItemOrder, NavLayout.MoveTo(_sectionOrder[sec], key, target));
        SaveSettings(); BuildNav();
    }

    /// <summary>Moves a pinned page up or down among the pins.</summary>
    public void MovePinned(string key, int delta)
    {
        var s = AppSettings.Current;
        if (!s.PinnedNavItems.Contains(key)) return;
        s.PinnedNavItems = NavLayout.Move(s.PinnedNavItems, key, delta);
        SaveSettings(); BuildNav();
    }

    /// <summary>Pins a page, before <paramref name="before"/> (a pinned page) or at the end.</summary>
    internal bool PinNavAt(string key, string? before)
    {
        var pins = AppSettings.Current.PinnedNavItems;
        if (!pins.Contains(key) && pins.Count >= NavLayout.MaxPinned) { Status($"At most {NavLayout.MaxPinned} pinned pages: unpin one first."); return false; }
        pins.Remove(key);
        int at = before == null ? -1 : pins.IndexOf(before);
        if (at < 0) pins.Add(key); else pins.Insert(at, key);
        SaveSettings(); BuildNav();
        return true;
    }

    private static (string Key, bool FromPinned)? ParseNavDrag(IDataObject data) =>
        data.GetData(NavItemFormat) is string v && v.Length > 2 && v[1] == ':' ? (v[2..], v[0] == 'p') : null;

    /// <summary>What a dragged page may do over <paramref name="target"/>: reorder the pins, reorder its own section, be
    /// pinned at a pinned page, or (a pinned page dragged out) be unpinned.</summary>
    private bool CanNavDrop(IDataObject data, string target, bool targetPinned)
    {
        if (ParseNavDrag(data) is not { } d || d.Key == target) return false;
        var pins = AppSettings.Current.PinnedNavItems;
        if (d.FromPinned) return true;
        if (targetPinned) return !pins.Contains(d.Key) && pins.Count < NavLayout.MaxPinned;
        return _sectionOf.TryGetValue(d.Key, out var sec) && _sectionOf.GetValueOrDefault(target) == sec;
    }

    internal void DropNavItem(string key, bool fromPinned, string target, bool targetPinned)
    {
        var s = AppSettings.Current;
        if (fromPinned && targetPinned) { s.PinnedNavItems = NavLayout.MoveTo(s.PinnedNavItems, key, target); SaveSettings(); BuildNav(); }
        else if (!fromPinned && targetPinned) PinNavAt(key, target);
        else if (!fromPinned) PlaceNavItem(key, target);
        else { SetNavPinned(key, false); Status($"“{(_allNav.TryGetValue(key, out var e) ? e.Title : key)}” unpinned."); }
    }

    /// <summary>A page is a drag source and a drop target (both sidebar styles).</summary>
    private void AttachNavDrag(RadioButton rb, NavEntry e, bool inPinned)
    {
        Point? down = null;
        rb.PreviewMouseLeftButtonDown += (_, ev) => down = ev.GetPosition(Nav);
        rb.PreviewMouseLeftButtonUp += (_, _) => down = null;
        rb.PreviewMouseMove += (_, ev) =>
        {
            if (down is not { } p0 || ev.LeftButton != MouseButtonState.Pressed) return;
            var p = ev.GetPosition(Nav);
            if (Math.Abs(p.Y - p0.Y) < SystemParameters.MinimumVerticalDragDistance && Math.Abs(p.X - p0.X) < SystemParameters.MinimumHorizontalDragDistance) return;
            down = null;
            rb.ReleaseMouseCapture();
            // Nothing pinned yet: a PINNED drop zone appears at the top for as long as the drag lasts.
            var zone = !inPinned && AppSettings.Current.PinnedNavItems.Count == 0 && AppSettings.Current.SidebarStyle != "icons" ? PinDropTarget(null) : null;
            if (zone != null) Nav.Children.Insert(0, zone);
            try { DragGhost.Run(rb, new DataObject(NavItemFormat, (inPinned ? "p:" : "s:") + e.Key), DragDropEffects.Move, ev.GetPosition(rb), label: e.Title); }
            finally { if (zone != null) Nav.Children.Remove(zone); DragGhost.Target(null); }
            ev.Handled = true;
        };
        rb.AllowDrop = true;
        rb.DragOver += (_, ev) => { bool ok = CanNavDrop(ev.Data, e.Key, inPinned); ev.Effects = ok ? DragDropEffects.Move : DragDropEffects.None; DragGhost.Target(ok ? rb : null); ev.Handled = true; };
        rb.DragLeave += (_, _) => DragGhost.Target(null);
        rb.Drop += (_, ev) =>
        {
            if (ParseNavDrag(ev.Data) is not { } d || !CanNavDrop(ev.Data, e.Key, inPinned)) return;
            ev.Handled = true;
            Dispatcher.BeginInvoke(() => DropNavItem(d.Key, d.FromPinned, e.Key, inPinned));
        };
    }

    /// <summary>The PINNED header (or, while dragging with nothing pinned, a temporary drop zone): drop a page to pin it.</summary>
    private TextBlock PinDropTarget(TextBlock? head)
    {
        if (head == null)
        {
            head = new TextBlock { Style = (Style)FindResource("NavHeader") };
            head.Inlines.Add(IconRun(Icons.Pin, "FsCaption"));
            head.Inlines.Add(new Run("  PINNED — drop here to pin"));
        }
        head.AllowDrop = true;
        head.DragOver += (_, ev) =>
        {
            bool ok = ParseNavDrag(ev.Data) is { FromPinned: false } d && !AppSettings.Current.PinnedNavItems.Contains(d.Key) && AppSettings.Current.PinnedNavItems.Count < NavLayout.MaxPinned;
            ev.Effects = ok ? DragDropEffects.Move : DragDropEffects.None; DragGhost.Target(ok ? head : null); ev.Handled = true;
        };
        head.DragLeave += (_, _) => DragGhost.Target(null);
        head.Drop += (_, ev) =>
        {
            if (ParseNavDrag(ev.Data) is not { FromPinned: false } d) return;
            ev.Handled = true;
            Dispatcher.BeginInvoke(() => PinNavAt(d.Key, null));
        };
        return head;
    }

    /// <summary>A sidebar screen: its key, glyph (drawn as a Segoe Fluent icon, <see cref="Icons"/>), title and tooltip.</summary>
    public sealed record NavEntry(string Key, string Glyph, string Title, string? Tip);
    private readonly Dictionary<string, NavEntry> _allNav = new();
    private Dictionary<string, List<string>> _sectionsForTest = new();
    /// <summary>Self-test: each section's screens in their default order (before hiding and pinning).</summary>
    internal IReadOnlyDictionary<string, List<string>> NavSectionsForTest => _sectionsForTest;
    /// <summary>Every screen the sidebar offers now (pinning, the landing page, Settings → Navigation).</summary>
    public IReadOnlyCollection<string> AllNavKeys => _allNav.Keys;
    public IReadOnlyList<NavEntry> NavEntries => _allNav.Values.ToList();

    /// <summary>Pins a screen to the PINNED section at the top of the sidebar (at most <see cref="NavLayout.MaxPinned"/>) or unpins it.</summary>
    public bool SetNavPinned(string key, bool pin)
    {
        var list = AppSettings.Current.PinnedNavItems;
        if (pin && !list.Contains(key))
        {
            if (list.Count >= NavLayout.MaxPinned) { Status($"At most {NavLayout.MaxPinned} pinned pages: unpin one first."); return false; }
            list.Add(key);
        }
        else if (!pin) list.Remove(key);
        SaveSettings(); BuildNav();
        return true;
    }

    /// <summary>Hides a page from the sidebar or shows it again (Settings can't be hidden). Hidden pages still open from
    /// links, menus and quick connect.</summary>
    public bool SetNavHidden(string key, bool hide)
    {
        if (hide && key == NavLayout.AlwaysShownPage) return false;
        var list = AppSettings.Current.HiddenNavItems;
        if (hide && !list.Contains(key)) { list.Add(key); Status($"“{(_allNav.TryGetValue(key, out var e) ? e.Title : key)}” hidden from the sidebar — Settings → Navigation → Shown pages brings it back."); }
        else if (!hide) list.Remove(key);
        SaveSettings(); BuildNav();
        return true;
    }

    /// <summary>The sidebar's width for its style: icons only = a narrow strip; otherwise the saved width.</summary>
    public void ApplySidebarWidth()
    {
        var s = AppSettings.Current;
        SideCol.Width = new GridLength(s.SidebarCollapsed ? 0 : s.SidebarStyle == "icons" ? IconRailWidth : Math.Clamp(s.SidebarWidth, 150, 480));
    }
    private const double IconRailWidth = 52;

    /// <summary>A Fluent icon (or the glyph itself when it has none) in the accent colour.</summary>
    private TextBlock NavIcon(string glyph, double width = 24)
    {
        var t = new TextBlock { Width = width, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (Icons.For(glyph) is { } c) { t.Text = c.ToString(); t.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont"); t.SetResourceReference(TextBlock.FontSizeProperty, "FsInput"); }
        else t.Text = glyph;
        t.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        return t;
    }

    private static Run IconRun(char c, string sizeKey)
    {
        var r = new Run(c.ToString());
        r.SetResourceReference(TextElement.FontFamilyProperty, "IconFont");
        r.SetResourceReference(TextElement.FontSizeProperty, sizeKey);
        return r;
    }

    public void BuildNav()
    {
        Nav.Children.Clear();
        _navByKey.Clear();
        _navPanels.Clear();
        _allNav.Clear();
        var s = AppSettings.Current;

        // 1. What the sidebar offers: sections and their screens, in the default order.
        var items = new Dictionary<string, List<NavEntry>>();
        List<NavEntry> current = new();
        void Header(string t) { current = items[t] = new(); }
        void Item(string key, string text, string? tip = null)
        {
            var parts = text.Split("  ", 2);
            var e = new NavEntry(key, parts.Length > 1 ? parts[0] : "", parts.Length > 1 ? parts[1] : text, tip);
            current.Add(e);
            _allNav[key] = e;
        }
        Header("OVERVIEW");
        Item("dashboard", "◈  Dashboard");
        Header("SERVERS");
        Item("hosts", "▣  Hosts", "Saved servers: connect, manage, files");
        Item("infra", "◫  Infrastructure", "Docker, Proxmox, Unraid, VMware vCenter and Hyper-V: inventory, health, start/stop with verified results — read-only until you allow changes");
        if (AppSettings.Current.CloudPcEnabled)
            Item("cloudpc", "🖥  Cloud PCs", "Windows 365 Cloud PCs and Azure Virtual Desktop through Microsoft's Windows App: connect in a tab or the Windows App; your Cloud PCs listed with an app registration");
        if (AppSettings.Current.M365AdminEnabled)
            Item("m365", "🛡  Microsoft 365 admin", "Intune devices, Defender incidents and alerts, Entra users, groups, devices, sign-ins and risky users — read-only, signed in as you; open any item in its admin centre");
        Item(CloudWorkspaceView.NavKeyName, "☁  Cloud providers", "Vultr, Contabo, Hetzner Cloud, Microsoft Azure and Google Cloud: servers, Kubernetes, apps, databases, storage, networks, firewalls, DNS and more, grouped per provider in one workspace");
        if (Browser.PortalBrowser.Enabled)
            Item("portals", "🌐  Portals", "Router, NAS, hypervisor and provider web UIs in tabs: a browser profile per environment, pinned self-signed certificates, no saved passwords");
        if (AppSettings.Current.BeyondTrustEnabled) Item("beyondtrust", "⛨  BeyondTrust", "Privileged Remote Access Jump Items (open in the access console, add as hosts), access sessions, Jumpoints and Vault names; Password Safe credentials checked out for a session");
        Header("TOOLS");
        Item("commander", "⇆  File commander", "Two-pane file manager: this PC and any host (SFTP), copy/move with a transfer queue");
        Item("snippets", "⚡  Snippets & multi-run", "Saved commands; run on many hosts at once");
        Item("tunnels", "⇄  Tunnels", "SSH port forwarding and SOCKS proxies");
        Item("lan", "⌕  LAN discovery", "Find SSH servers on your network");
        Item("toolbox", "🧰  Network & mail toolbox", "TCP port / banner, traceroute, mtr, DNS and propagation, HTTP and TLS certificates, SPF / DMARC / DKIM / MTA-STS, blacklists, email headers, WHOIS and ASN — from this PC or a saved server");
        Header("DOCUMENTS");
        Item("notes", "✎  Notes", "Engineering notes and per-host notebooks — encrypted, searchable offline (words, tag:, host:, dates)");
        Item("docs", "📄  Docs", "Markdown documents and runbooks in folders, pinned to hosts, host folders and infrastructure — encrypted, with a live preview");
        Item("incidents", "⚑  Incidents", "Work sessions and incidents: hosts, a timeline of terminal excerpts, screenshots, logs, notes and AI analyses, and a summary");
        Item("reports", "▤  Reports", "Health & log reports for one or many servers — offline checks" + (Reports.ReportAnalysis.Current != null ? " plus optional AI analysis" : ""));
        // 0.5.0 (DarkCipher0x): configuration management gets its own section.
        Header("AUTOMATION");
        Item("ansible", "⟐  Ansible", "Inventory from your saved hosts; ping, facts, ad-hoc modules, playbooks (check / diff / limit / tags), lint, vault, galaxy, docs and config — on a WSL distribution or a saved Linux control node");
        Item("puppet", "⚙  Puppet", "Puppet server certificates, agent runs (dry run first), enable / disable, last runs and facts, code deploys (r10k / Code Manager), Hiera lookups, PuppetDB queries and Bolt — read-only until changes are allowed");
        Header("LOCAL");
        foreach (var sh in Sessions.LocalShells.All)
            Item("local:" + sh.Id, $"{sh.Glyph}  {sh.Name}", $"{(sh.Wsl ? "WSL distribution" : "Local shell")} in a terminal tab ({sh.Detail}). Click again for another tab.");
        // 0.5.0 (ADR-064): the WSL screen, only when WSL is installed.
        if (Wsl.WslManager.Installed) Item("wsl", "🐧  WSL", "Distributions (start, stop, default, convert, export, import, unregister, update), their wsl.conf / hosts / network and ports, the global .wslconfig, and WSL Containers (wslc)");
        Item("serial", "≋  Serial / USB consoles", "COM ports and USB-serial adapters: saved console profiles, auto-detect, BREAK, reconnect on replug");
        Header("VAULT");
        Item("credentials", "⚿  Credentials", "Passwords and SSH keys (encrypted)");
        Item("knownhosts", "✓  Known hosts", "Trusted server host keys");
        // 0.6.16 (DarkCipher0x: "Bitwarden should be under the VAULT section").
        if (AppSettings.Current.BitwardenEnabled) Item("bitwarden", "🔑  Bitwarden", "Your Bitwarden vault's logins and SSH keys and Secrets Manager secrets: sign hosts in with them (read into memory per session, never saved here) or copy one briefly");
        if (Library.SharedLibraries.Enabled)
        {
            // The team's things in one place while a library add-on is enabled (ADR-033).
            Header("TEAM");
            Item("library", "⇄  Shared library", "Your team's libraries: members, join requests, collections, sync, the shared audit — end-to-end encrypted per member through a folder or file share (ADR-033)");
            Item("team:hosts", "▣  Team hosts", "Hosts shared through your libraries (Hosts, on the shared library's node)");
            Item("team:credentials", "⚿  Team credentials", "Credentials shared through your libraries (Credentials, shared only)");
        }
        // Cloud providers (ADR-020) are one workspace since 0.1.11 (ADR-028): Item "cloud" above, not a section each.
        if (AppModules.NavItemsIn("AI").Any())
        {
            Header("AI");
            // Modules contribute their items (0.2.0 phase 3): the AI module adds the AI Engineer sidebar and the AI operator.
            foreach (var c in AppModules.NavItemsIn("AI")) Item(c.Key, c.Text, c.Tip);
        }
        // 0.5.1 #8 (DarkCipher0x): every log in one section. The Audit log is locked by default (Group Policy: LockAuditLog); the
        // others can be locked from their right-click menu or Settings → Security & privacy → Locks.
        Header("LOGS");
        Item("audit", "☰  Audit log", "Everything done in the app, by you, the AI and add-ons — encrypted entry by entry; locked by default");
        Item("sessionlogs", "☷  Session logs", "Terminal output logged per session (encrypted, timestamped) — turn logging on in a terminal profile or a terminal's menu");
        Item("errorlog", "⚠  Error log", "The app's own errors (each entry encrypted on disk, secrets masked)");
        Item("observe", "◉  Observability", "Log sinks (Elasticsearch / OpenSearch, Grafana Loki, Azure Monitor / Sentinel, Splunk, Graylog, webhooks) with their state and activity, and your dashboards (Grafana, Kibana, SIEM links) in the app's browser — HTTPS only, secrets masked");
        Header("SYSTEM");
        Item("addons", "◇  Add-ons", "Sandboxed programs that extend the app — only the permissions you grant");
        Item("privacy", "⛨  Privacy & network", "Security posture, every destination by network class, and what is listening");
        Item("help", "?  Help & FAQ", "Search the built-in help (F1)" + (Assist.Current != null ? "; AI answers when an engine is on" : ""));
        Item("settings", "⚙  Settings");
        _sectionsForTest = items.ToDictionary(kv => kv.Key, kv => kv.Value.Select(e => e.Key).ToList());

        if (NavLayout.Normalize(s, _allNav.Keys).Count > 0) SaveSettings();
        // 0.6.2: the user's page order inside each section (drag a page, or right-click → Move up / down). A page never
        // leaves its section; pages the saved order doesn't know keep their default place.
        _sectionOf.Clear(); _sectionOrder.Clear();
        foreach (var name in items.Keys.ToList())
        {
            var byKey = items[name].GroupBy(e => e.Key).ToDictionary(g => g.Key, g => g.First());
            var ordered = NavLayout.Order(items[name].Select(e => e.Key).Distinct().ToList(), s.NavItemOrder);
            items[name] = ordered.Select(k => byKey[k]).ToList();
            _sectionOrder[name] = ordered;
            foreach (var k in ordered) _sectionOf[k] = name;
        }
        var order = NavOrder;
        // Hidden pages (Settings → Navigation → Shown pages) leave the sidebar but stay in _allNav: they still open from
        // links, menus and quick connect, and Settings lists them to bring them back.
        foreach (var name in items.Keys.ToList()) items[name] = items[name].Where(e => !s.HiddenNavItems.Contains(e.Key)).ToList();
        var visible = order.Where(n => !s.HiddenNavSections.Contains(n)).ToList();
        var pinned = s.PinnedNavItems.Where(k => _allNav.ContainsKey(k) && !s.HiddenNavItems.Contains(k)).Select(k => _allNav[k]).ToList();

        // 2. Drawn in the chosen style (Settings → Navigation → Sidebar).
        if (s.SidebarStyle == "icons") BuildIconRail(items, visible, pinned);
        else BuildSections(items, order, visible, pinned);
        ApplySidebarWidth();
        // Keep the open screen highlighted after a rebuild.
        if (Panes.ActiveTab is { } active) OnActiveTab(active);
    }

    private RadioButton NavButton(NavEntry e, bool iconOnly, bool inPinned = false)
    {
        UIElement content;
        TextBlock? pin = null;
        if (iconOnly) content = NavIcon(e.Glyph.Length > 0 ? e.Glyph : "•", IconRailWidth - 18);
        else
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(NavIcon(e.Glyph));
            row.Children[0].SetValue(MarginProperty, new Thickness(0, 0, 8, 0));
            row.Children.Add(new TextBlock { Text = e.Title, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            // 0.5.1 (ADR-067): a locked category shows its padlock (closed, or open while unlocked).
            if (Locks.CategoryOfScreen(e.Key) is { } cat && LockUi.Padlock("cat:" + cat) is { Length: > 0 } pad)
            {
                var p = new TextBlock { Text = pad, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = Locks.IsOpen("cat:" + cat) ? "Unlocked for now" : "Locked: opening it asks first" };
                p.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                row.Children.Add(p);
            }
            // 0.6.2: a 📌 at the right end, shown while the page is pointed at (and always, in the accent colour, on a
            // page that is pinned): one click pins or unpins it.
            var pins = AppSettings.Current.PinnedNavItems;
            bool isPinned = pins.Contains(e.Key);
            pin = new TextBlock
            {
                Text = (isPinned ? Icons.Unpin : Icons.Pin).ToString(), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 2, 0), Cursor = Cursors.Hand,
                ToolTip = isPinned ? "Unpin from the top of the sidebar" : pins.Count >= NavLayout.MaxPinned ? $"At most {NavLayout.MaxPinned} pinned pages: unpin one first" : "Pin to the top of the sidebar (or drag the page onto PINNED)",
                Visibility = isPinned && !inPinned ? Visibility.Visible : Visibility.Hidden,
            }.Fs(FontStep.Caption);
            pin.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
            pin.SetResourceReference(TextBlock.ForegroundProperty, isPinned && !inPinned ? "Accent" : "Muted");
            System.Windows.Automation.AutomationProperties.SetName(pin, isPinned ? $"Unpin {e.Title}" : $"Pin {e.Title}");
            pin.MouseLeftButtonDown += (_, ev) => ev.Handled = true;   // the page doesn't open
            pin.MouseLeftButtonUp += (_, ev) => { ev.Handled = true; Dispatcher.BeginInvoke(() => SetNavPinned(e.Key, !isPinned)); };
            var dock = new DockPanel();
            DockPanel.SetDock(pin, Dock.Right);
            dock.Children.Add(pin);
            dock.Children.Add(row);
            content = dock;
        }
        var rb = new RadioButton { Content = content, Style = (Style)FindResource("NavItem"), Tag = e.Key, ToolTip = iconOnly ? (e.Tip is { } t ? $"{e.Title} — {t}" : e.Title) : e.Tip };
        if (iconOnly) rb.Padding = new Thickness(6, 7, 6, 7);
        System.Windows.Automation.AutomationProperties.SetName(rb, e.Title);
        rb.Checked += (_, _) => { if (!_suppressNav) Navigate(e.Key); };
        Menus.Attach(rb, () => NavMenu(e.Key, e.Title, inPinned));
        if (pin != null)
        {
            var always = pin.Visibility == Visibility.Visible;
            rb.MouseEnter += (_, _) => pin.Visibility = Visibility.Visible;
            rb.MouseLeave += (_, _) => pin.Visibility = always ? Visibility.Visible : Visibility.Hidden;
        }
        AttachNavDrag(rb, e, inPinned);
        return rb;
    }

    /// <summary>Icons only: one narrow column of every visible screen (pinned first), sections separated by a rule, the
    /// names in tooltips and for screen readers.</summary>
    private void BuildIconRail(Dictionary<string, List<NavEntry>> items, List<string> visible, List<NavEntry> pinned)
    {
        void Rule() { var r = new Border { Height = 1, Margin = new Thickness(10, 6, 10, 6) }; r.SetResourceReference(Border.BackgroundProperty, "Line"); Nav.Children.Add(r); }
        foreach (var e in pinned) Nav.Children.Add(NavButton(e, iconOnly: true, inPinned: true));
        if (pinned.Count > 0) Rule();
        bool first = true;
        foreach (var name in visible)
        {
            if (!items.TryGetValue(name, out var list) || list.Count == 0) continue;
            if (!first) Rule();
            first = false;
            foreach (var e in list) { var rb = NavButton(e, iconOnly: true); Nav.Children.Add(rb); _navByKey[e.Key] = rb; }
        }
    }

    private void BuildSections(Dictionary<string, List<NavEntry>> items, List<string> order, List<string> visible, List<NavEntry> pinned)
    {
        var s = AppSettings.Current;
        // PINNED (Settings → Navigation, or right-click a screen → Pin to top): always open, at the top.
        if (pinned.Count > 0)
        {
            var head = new TextBlock { Style = (Style)FindResource("NavHeader"), Cursor = Cursors.Arrow, ToolTip = "Pinned pages: drag a page here to pin it, drag pins to reorder, drag one out (or its 📌) to unpin · Settings → Navigation" };
            head.Inlines.Add(IconRun(Icons.Pin, "FsCaption"));
            head.Inlines.Add(new Run("  PINNED"));
            Nav.Children.Add(PinDropTarget(head));
            foreach (var e in pinned) Nav.Children.Add(NavButton(e, iconOnly: false, inPinned: true));
        }
        // Each header owns a panel of items; clicking the header collapses it (remembered in settings). Headers can be
        // dragged onto each other to reorder, and their right-click menu moves / hides sections.
        var panels = new Dictionary<string, (TextBlock Head, StackPanel Panel)>();
        foreach (var t in order)
        {
            if (!items.TryGetValue(t, out var list) || list.Count == 0) continue;   // every page of it hidden: no empty header
            bool open = _navExpanded.Contains(t);
            var head = new TextBlock { Style = (Style)FindResource("NavHeader"), ToolTip = "Click to collapse / expand · drag to reorder · right-click to move or hide", AllowDrop = true, Tag = t };
            var panel = new StackPanel { Visibility = open ? Visibility.Visible : Visibility.Collapsed };
            void Paint()
            {
                head.Inlines.Clear();
                head.Inlines.Add(IconRun(panel.Visibility == Visibility.Visible ? Icons.ChevronDown : Icons.ChevronRight, "FsMicro"));
                head.Inlines.Add(new Run("  "));
                var glyph = SectionGlyph(t);
                if (Icons.For(glyph) is { } ic) head.Inlines.Add(IconRun(ic, "FsCaption")); else head.Inlines.Add(new Run(glyph));
                head.Inlines.Add(new Run("  " + SectionTitle(t)));
            }
            Paint();
            Point? down = null; bool dragged = false;
            head.MouseLeftButtonDown += (_, e) => { down = e.GetPosition(Nav); dragged = false; };
            head.MouseMove += (_, e) =>
            {
                if (down is not { } p0 || e.LeftButton != MouseButtonState.Pressed) return;
                var p = e.GetPosition(Nav);
                if (Math.Abs(p.Y - p0.Y) < SystemParameters.MinimumVerticalDragDistance && Math.Abs(p.X - p0.X) < SystemParameters.MinimumHorizontalDragDistance) return;
                dragged = true; down = null;
                DragGhost.Run(head, new DataObject("dcx-nav-section", t), DragDropEffects.Move, default, label: SectionGlyph(t) + "  " + SectionTitle(t));
            };
            head.DragOver += (_, e) => { bool can = e.Data.GetData("dcx-nav-section") is string f0 && f0 != t; e.Effects = can ? DragDropEffects.Move : DragDropEffects.None; DragGhost.Target(can ? head : null); e.Handled = true; };
            head.DragLeave += (_, _) => DragGhost.Target(null);
            head.Drop += (_, e) =>
            {
                if (e.Data.GetData("dcx-nav-section") is not string from || from == t) return;
                s.NavSectionOrder = NavLayout.MoveTo(NavOrder, from, t);
                SaveSettings();
                Dispatcher.BeginInvoke(BuildNav);
            };
            head.MouseLeftButtonUp += (_, _) =>
            {
                down = null;
                if (dragged) { dragged = false; return; }
                panel.Visibility = panel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
                if (panel.Visibility == Visibility.Visible) _navExpanded.Add(t); else _navExpanded.Remove(t);
                Paint();
                SaveNavExpansion();
            };
            Menus.Attach(head, () =>
            {
                int i = visible.IndexOf(t);
                var hidden = s.HiddenNavSections.Where(order.Contains).ToList();
                return new object?[]
                {
                    // Move past hidden neighbours too, so one click always moves it visibly.
                    Menus.Item("Move section up", () => MoveNavSection(t, i > 0 ? order.IndexOf(visible[i - 1]) - order.IndexOf(t) : -1), enabled: i > 0),
                    Menus.Item("Move section down", () => MoveNavSection(t, i < visible.Count - 1 ? order.IndexOf(visible[i + 1]) - order.IndexOf(t) : 1), enabled: i < visible.Count - 1),
                    Menus.Item($"Hide “{SectionTitle(t)}”", () => SetNavSectionHidden(t, true), enabled: visible.Count > 1),
                    Menus.Sub("Show hidden section", hidden.Select(h => (object?)Menus.Item(SectionTitle(h), () => SetNavSectionHidden(h, false))).ToArray()),
                    Menus.Item("Reset sidebar layout", ResetNavLayout, enabled: s.NavSectionOrder.Count > 0 || s.HiddenNavSections.Count > 0 || s.NavItemOrder.Count > 0, tip: "Section order, hidden sections and the order of the pages inside them"),
                    Menus.Sep(),
                    Menus.Item("Expand all sections", () => { _navExpanded.UnionWith(NavSections); SaveNavExpansion(); BuildNav(); }),
                    Menus.Item("Collapse all sections", () => { _navExpanded.Clear(); SaveNavExpansion(); BuildNav(); }),
                    Menus.Item("Sidebar settings…", () => { Navigate("settings"); SettingsView.Current?.RevealCard("Sidebar"); }),
                };
            });
            foreach (var e in list)
            {
                var rb = NavButton(e, iconOnly: false);
                panel.Children.Add(rb);
                // Items of hidden sections stay reachable (Ctrl+K, links, tab menus) but aren't highlighted in the sidebar.
                if (visible.Contains(t)) _navByKey[e.Key] = rb;
            }
            panels[t] = (head, panel);
            _navPanels[t] = panel;
        }
        foreach (var name in visible)
        {
            if (!panels.TryGetValue(name, out var p)) continue;
            Nav.Children.Add(p.Head);
            Nav.Children.Add(p.Panel);
        }
    }

    private IEnumerable<object?> NavMenu(string key, string title, bool inPinned = false)
    {
        // 0.6.2: rearrange (also by dragging): among the pins, or within the page's section.
        var pinList = AppSettings.Current.PinnedNavItems;
        if (inPinned)
        {
            int i = pinList.IndexOf(key);
            yield return Menus.Item("Move up", () => MovePinned(key, -1), enabled: i > 0);
            yield return Menus.Item("Move down", () => MovePinned(key, 1), enabled: i >= 0 && i < pinList.Count - 1);
            yield return Menus.Sep();
        }
        else if (_sectionOf.ContainsKey(key))
        {
            var hidden = AppSettings.Current.HiddenNavItems;
            var visible = _sectionOrder[_sectionOf[key]].Where(k => !hidden.Contains(k)).ToList();
            int i = visible.IndexOf(key);
            if (visible.Count > 1)
            {
                yield return Menus.Item("Move up", () => MoveNavItem(key, -1), enabled: i > 0);
                yield return Menus.Item("Move down", () => MoveNavItem(key, 1), enabled: i >= 0 && i < visible.Count - 1);
                yield return Menus.Sep();
            }
        }
        // A module's item with its own menu (the AI Engineer sidebar's dock states): that menu, then help.
        if (AppModules.FindNav(key) is { Menu: { } moduleMenu })
        {
            foreach (var m in moduleMenu()) yield return m;
            yield return Menus.Sep();
            yield return Menus.Item($"Help: {title}", () => ShowHelp(title));
            yield break;
        }
        yield return Menus.Item("Open", () => Navigate(key));
        if (key == "portal") yield break;
        yield return Menus.Item("Open in a new pane (right)", () => OpenInPane(key, Orientation.Horizontal));
        yield return Menus.Item("Open in a new pane (below)", () => OpenInPane(key, Orientation.Vertical));
        yield return Menus.Item("Open in a new window", () => { Navigate(key); if (_singletons.TryGetValue(key, out var t)) Panes.PopOut(t); });
        if (_singletons.TryGetValue(key, out var open) && Panes.Contains(open))
            yield return Menus.Item("Close", () => CloseTab(open), enabled: (open.Tag as string) != "fixed");
        if (Locks.CategoryOfScreen(key) is { } lockCat && Locks.FindCategory(lockCat) is { } lc)
            yield return LockUi.Menu("cat:" + lockCat, lc.Title);
        yield return Menus.Sep();
        var pins = AppSettings.Current.PinnedNavItems;
        yield return pins.Contains(key)
            ? Menus.Item("Unpin from the top of the sidebar", () => SetNavPinned(key, false))
            : Menus.Item($"Pin to the top of the sidebar ({pins.Count} of {NavLayout.MaxPinned})", () => SetNavPinned(key, true), enabled: pins.Count < NavLayout.MaxPinned);
        if (key != NavLayout.AlwaysShownPage)
            yield return Menus.Item($"Hide “{title}” from the sidebar", () => SetNavHidden(key, true), tip: "It still opens from links and menus; Settings → Navigation → Shown pages brings it back");
        yield return Menus.Item($"Help: {title}", () => ShowHelp(title));
        yield return Menus.Item(AppSettings.Current.SidebarCollapsed ? "Show sidebar" : "Hide sidebar", ToggleSidebar, "Ctrl+B");
    }

    private void OpenInPane(string key, Orientation o)
    {
        Navigate(key);
        if (_singletons.TryGetValue(key, out var t)) Panes.Split(t, o);
    }

    private void OnSideResized(object sender, DragCompletedEventArgs e)
    {
        var w = SideCol.ActualWidth;
        AppSettings.Current.SidebarCollapsed = w < 40;
        // Icons only keeps its narrow strip; dragging wider switches back to the expanded sidebar at that width.
        if (w >= 40 && AppSettings.Current.SidebarStyle == "icons" && w > IconRailWidth + 40) { AppSettings.Current.SidebarStyle = "expanded"; AppSettings.Current.SidebarWidth = w; BuildNav(); }
        else if (w >= 40 && AppSettings.Current.SidebarStyle != "icons") AppSettings.Current.SidebarWidth = w;
        ApplySidebarWidth();
        try { AppSettings.Save(); } catch { }
    }

    private void OnSideToggle(object sender, MouseButtonEventArgs e) => ToggleSidebar();

    public void ToggleSidebar()
    {
        var s = AppSettings.Current;
        s.SidebarCollapsed = !s.SidebarCollapsed;
        ApplySidebarWidth();
        try { AppSettings.Save(); } catch { }
    }

    // ---- motion (Settings → Appearance → Animation) ------------------------------------------------------------------

    private readonly System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames _cursorBlink = new() { Duration = TimeSpan.FromSeconds(1.1), RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever };

    /// <summary>The header cursor blinks unless animation is reduced (then it stays lit).</summary>
    private void ApplyMotion()
    {
        if (NavLayout.Reduced) { PromptCursor.BeginAnimation(OpacityProperty, null); PromptCursor.Opacity = 1; }
        else PromptCursor.BeginAnimation(OpacityProperty, _cursorBlink);
    }
}
