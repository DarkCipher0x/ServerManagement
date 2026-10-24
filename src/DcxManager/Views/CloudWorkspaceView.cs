using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DcxManager.Services;

namespace DcxManager.Views;

/// <summary>
/// The Cloud providers workspace (ADR-028): one sidebar entry instead of a sidebar section per provider and resource.
/// The left tree lists every registered provider (<see cref="CloudProviders.All"/>) with its account, its catalog grouped as
/// the provider groups it (Compute, Storage, Network, Access, DNS …), its reference lists and provider tools — only what
/// that provider's catalog defines, so nothing appears that the provider can't do. Search filters the tree; screens the
/// user pins sit in a "★ Pinned" branch at the top. The resource screens themselves are the shared
/// <see cref="CloudResourceView"/>s, created on first use and kept while the workspace is open. Old links
/// ("vultr:Instances") open here. Tree branches start collapsed (the pinned branch open); what the user expands is
/// remembered for this session only. A click or Enter opens a screen; arrow keys only move (so walking the tree doesn't
/// load every screen); Ctrl+Enter or a middle click opens one in its own tab; right-click acts on the node under the mouse.
/// </summary>
public sealed class CloudWorkspaceView : UserControl, ICloudView, IDisposable
{
    public const string NavKeyName = "cloud";
    public const string PinnedTitle = "★ Pinned";
    public const int MaxPinned = 20;
    private const string PinnedId = "pinned";
    /// <summary>Branches the user expanded this session (cleared at restart: the tree opens clean); "!id" = a branch that
    /// starts open and was collapsed.</summary>
    private static readonly HashSet<string> SessionExpanded = new();

    private readonly TreeView _tree = new() { BorderThickness = new Thickness(0), Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBox _search = Hint.Set(new TextBox { Padding = new Thickness(5, 4, 5, 4), ToolTip = "Search providers and screens (Ctrl+F; Esc clears, ↓ goes to the tree)" }, "Search providers and screens…");
    private readonly ContentControl _host = new();
    private readonly Dictionary<string, FrameworkElement> _views = new();
    /// <summary>The id of each branch in the tree now: a provider id, "provider/group", or <see cref="PinnedId"/>.</summary>
    private readonly Dictionary<TreeViewItem, string> _branches = new();
    private TreeViewItem? _menuNode;
    private bool _menuByMouse, _building, _disposed;

    public static CloudWorkspaceView? Current { get; private set; }
    /// <summary>The nav key of the screen shown on the right ("" = the overview).</summary>
    public string Shown { get; private set; } = "";
    /// <summary>The provider of the screen shown (the header account picker follows it).</summary>
    public CloudProvider? Provider { get; private set; }
    /// <summary>The cloud screen shown on the right, if any (self-test, AI context).</summary>
    public FrameworkElement? CurrentView => _host.Content as FrameworkElement;

    public CloudWorkspaceView()
    {
        Current = this;
        Locks.Changed += OnLocksChanged;
        var root = new Grid { Margin = new Thickness(0) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        root.ColumnDefinitions.Add(new ColumnDefinition());

        var left = new DockPanel { Margin = new Thickness(12, 10, 4, 10) };
        var head = new StackPanel();
        head.Children.Add(new TextBlock { Text = "Cloud providers", Style = (Style)FindResource("H1") });
        head.Children.Add(new TextBlock { Text = "Every provider and its resources, grouped. Only what a provider's API offers is listed.", Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 8) });
        System.Windows.Automation.AutomationProperties.SetName(_search, "Search cloud providers and screens");
        head.Children.Add(_search);
        DockPanel.SetDock(head, Dock.Top);
        left.Children.Add(head);
        System.Windows.Automation.AutomationProperties.SetName(_tree, "Cloud providers and their screens");
        left.Children.Add(_tree);
        root.Children.Add(left);
        var split = new GridSplitter { Width = 6, HorizontalAlignment = HorizontalAlignment.Stretch, Background = System.Windows.Media.Brushes.Transparent };
        Grid.SetColumn(split, 1);
        root.Children.Add(split);
        Grid.SetColumn(_host, 2);
        root.Children.Add(_host);
        Content = root;

        _search.TextChanged += (_, _) => BuildTree();
        _search.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _search.Text.Length > 0) { _search.Clear(); e.Handled = true; }
            else if (e.Key == Key.Down && _tree.Items.OfType<TreeViewItem>().FirstOrDefault() is { } first) { first.Focus(); e.Handled = true; }
        };
        // A click opens at once; keyboard moves only select (Enter opens), so walking the tree doesn't load every screen.
        _tree.SelectedItemChanged += (_, _) => { if (!_building && !Moving() && _tree.SelectedItem is TreeViewItem { Tag: string key }) Show(key); };
        _tree.PreviewMouseLeftButtonUp += (_, e) => { if (ItemAt(e.OriginalSource) is { Tag: string key, IsSelected: true } && key != Shown) Show(key); };
        _tree.PreviewMouseDown += (_, e) => { if (e.ChangedButton == MouseButton.Middle && ItemAt(e.OriginalSource) is { Tag: string key }) { OpenInTab(key); e.Handled = true; } };
        _tree.PreviewMouseRightButtonDown += (_, e) => { _menuNode = ItemAt(e.OriginalSource); _menuByMouse = true; };
        _tree.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || _tree.SelectedItem is not TreeViewItem { Tag: string key }) return;
            if (Keyboard.Modifiers == ModifierKeys.Control) OpenInTab(key); else Show(key);
            e.Handled = true;
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { _search.Focus(); _search.SelectAll(); e.Handled = true; } };
        Menus.Attach(_tree, TreeMenu);
        BuildTree();
        ShowOverview();
    }

    private static bool Moving() =>
        Keyboard.IsKeyDown(Key.Up) || Keyboard.IsKeyDown(Key.Down) || Keyboard.IsKeyDown(Key.Left) || Keyboard.IsKeyDown(Key.Right) ||
        Keyboard.IsKeyDown(Key.Home) || Keyboard.IsKeyDown(Key.End) || Keyboard.IsKeyDown(Key.PageUp) || Keyboard.IsKeyDown(Key.PageDown);

    /// <summary>The tree item an event came from (its text may be a Run, which isn't in the visual tree).</summary>
    private static TreeViewItem? ItemAt(object? source)
    {
        var d = source as DependencyObject;
        while (d != null && d is not TreeViewItem)
            d = d is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as TreeViewItem;
    }

    // ---- tree ---------------------------------------------------------------------------------------------------------

    /// <summary>Every screen of a provider in its groups: (group, nav key, "glyph  text", tooltip).</summary>
    public static IEnumerable<(string Group, string Key, string Text, string? Tip)> ScreensOf(CloudProvider p)
    {
        foreach (var group in p.GroupOrder.Where(g => g != "Reference"))
            foreach (var d in p.Catalog.Where(d => d.Group == group))
                yield return (group, p.NavKey(d), $"{d.Icon}  {d.Title}", d.Description.Length > 0 ? d.Description : null);
        if (p.Catalog.Any(d => d.Group == "Reference")) yield return ("Reference", p.Id + ":reference", p.ReferenceLabel, "Read-only reference lists");
        foreach (var (key, text, tip) in p.ExtraNav) yield return ("Tools", key, text, tip);
    }

    /// <summary>Rebuilds the tree (accounts or pins changed elsewhere, e.g. in Settings).</summary>
    public void Refresh() => BuildTree();

    private void BuildTree()
    {
        _building = true;
        try
        {
            _tree.Items.Clear();
            _branches.Clear();
            var q = _search.Text.Trim();
            var pins = PinnedScreens();
            if (pins.Count > 0)
            {
                var pinNode = Node(PinnedId, PinnedTitle, "Screens you pinned: right-click a screen → Pin to top", openByDefault: true);
                foreach (var (p, s) in pins)
                    if (q.Length == 0 || p.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || Matches(s.Text, s.Tip, s.Group, q))
                        pinNode.Items.Add(Leaf(s.Key, $"{s.Text.Replace("  ", " ")}  ·  {p.Title}", s.Tip));
                if (pinNode.Items.Count > 0)
                {
                    if (q.Length > 0) pinNode.IsExpanded = true;
                    _tree.Items.Add(pinNode);
                }
            }
            foreach (var p in CloudProviders.All)
            {
                var acct = p.ActiveAccount;
                var pNode = Node(p.Id, ProviderHeader(p), acct != null ? $"{p.Title}: active account “{acct.Name}” ({p.Accounts.Count(a => !a.Disabled)} enabled). Right-click to switch account." : p.ConnectHelp);
                bool providerHit = q.Length > 0 && p.Title.Contains(q, StringComparison.OrdinalIgnoreCase);
                foreach (var g in ScreensOf(p).GroupBy(s => s.Group))
                {
                    var gNode = Node(p.Id + "/" + g.Key, g.Key, null);
                    foreach (var s in g)
                        if (q.Length == 0 || providerHit || Matches(s.Text, s.Tip, g.Key, q))
                            gNode.Items.Add(Leaf(s.Key, s.Text.Replace("  ", " "), s.Tip));
                    if (gNode.Items.Count == 0) continue;
                    if (q.Length > 0) gNode.IsExpanded = true;   // a search shows its hits; the remembered state comes back when it's cleared
                    pNode.Items.Add(gNode);
                }
                if (pNode.Items.Count == 0) continue;
                if (q.Length > 0) pNode.IsExpanded = true;
                _tree.Items.Add(pNode);
            }
            if (_tree.Items.Count == 0) _tree.Items.Add(new TreeViewItem { Header = q.Length > 0 ? $"Nothing matches “{q}”." : "No cloud providers.", IsEnabled = false });
        }
        finally { _building = false; }
    }

    /// <summary>"Vultr", "Vultr  ·  production", "Hetzner Cloud  ·  web  (2 accounts)": the account only when it adds
    /// something (an account named like its provider isn't repeated).</summary>
    public static string ProviderHeader(CloudProvider p)
    {
        var acct = p.ActiveAccount;
        if (acct == null) return $"{p.Title}  ·  {(p.Accounts.Count > 0 ? "all accounts disabled" : "no account yet")}";
        if (LockUi.Padlock(p.LockTarget(acct)) is { Length: > 0 } padlock) return padlock + " " + ProviderHeaderPlain(p, acct);
        return ProviderHeaderPlain(p, acct);
    }

    private static string ProviderHeaderPlain(CloudProvider p, ICloudAccount acct)
    {
        var name = acct.Name.Trim();
        var same = p.Title.Contains(name, StringComparison.OrdinalIgnoreCase) || name.Contains(p.Title, StringComparison.OrdinalIgnoreCase);
        var enabled = p.Accounts.Count(a => !a.Disabled);
        return p.Title + (same ? "" : "  ·  " + name) + (enabled > 1 ? $"  ({enabled} accounts)" : "");
    }

    private TreeViewItem Leaf(string key, string text, string? tip)
    {
        var item = new TreeViewItem { Header = text, Tag = key, ToolTip = tip, Padding = new Thickness(2, 1, 2, 1) };
        if (key == Shown) item.IsSelected = true;
        return item;
    }

    private static bool Matches(string text, string? tip, string group, string q) =>
        text.Contains(q, StringComparison.OrdinalIgnoreCase) || group.Contains(q, StringComparison.OrdinalIgnoreCase) || (tip?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>A branch, collapsed unless the user expanded it this session (or open unless collapsed, for
    /// <paramref name="openByDefault"/>); the state is set before it is shown, so nothing flickers.</summary>
    private TreeViewItem Node(string id, string header, string? tip, bool openByDefault = false)
    {
        var n = new TreeViewItem
        {
            Header = header, ToolTip = tip, Padding = new Thickness(2, 1, 2, 1), FontWeight = id.Contains('/') ? FontWeights.Normal : FontWeights.SemiBold,
            IsExpanded = openByDefault ? !SessionExpanded.Contains("!" + id) : SessionExpanded.Contains(id),
        };
        _branches[n] = id;
        n.Expanded += (_, e) => { if (Remembering(e, n)) { if (openByDefault) SessionExpanded.Remove("!" + id); else SessionExpanded.Add(id); } };
        n.Collapsed += (_, e) => { if (Remembering(e, n)) { if (openByDefault) SessionExpanded.Add("!" + id); else SessionExpanded.Remove(id); } };
        return n;
    }

    private bool Remembering(RoutedEventArgs e, TreeViewItem n) => !_building && _search.Text.Trim().Length == 0 && e.OriginalSource == n;

    // ---- the right-click menu: it acts on the node under the mouse (or the selected one, from the keyboard) ------------

    private IEnumerable<object?> TreeMenu()
    {
        var node = _menuByMouse ? _menuNode : _tree.SelectedItem as TreeViewItem;
        _menuByMouse = false;
        if (node is { Tag: string key })
        {
            yield return Menus.Item("Open", () => Show(key), "Enter");
            yield return Menus.Item("Open in its own tab", () => OpenInTab(key), "Ctrl+Enter", enabled: key.Contains(':'), tip: "Or middle-click it");
            var pinned = AppSettings.Current.CloudPinned.Contains(key);
            yield return Menus.Item(pinned ? "Unpin" : "★ Pin to top", () => TogglePin(key));
            yield return Menus.Sep();
        }
        else if (node != null && _branches.TryGetValue(node, out var id))
        {
            if (id == PinnedId)
                yield return Menus.Item("Unpin all", () => { AppSettings.Current.CloudPinned.Clear(); SaveAndRebuild(); });
            else if (CloudProviders.All.FirstOrDefault(x => x.Id == id) is { } p)
            {
                yield return Menus.Item($"Open {p.InstanceDef.Title.ToLowerInvariant()}", () => Show(p.NavKey(p.InstanceDef)), enabled: p.ActiveAccount != null);
                yield return Menus.Item($"Expand all of {p.Title}", () => SetProvider(p, true));
                yield return Menus.Item($"Collapse {p.Title}", () => SetProvider(p, false));
                var enabled = p.Accounts.Where(a => !a.Disabled).ToList();
                if (enabled.Count > 0)
                    yield return Menus.Sub("Switch account",
                        enabled.Select(a => (object?)Menus.Item((a.Id == p.ActiveAccount?.Id ? "✓  " : "     ") + a.Name, () => p.SetActive(a.Id))).ToArray());
                if (p.ActiveAccount is { } active) yield return LockUi.Menu(p.LockTarget(active), $"{p.Title} account {active.Name}");
                yield return Menus.Item($"{p.Title} accounts…", OpenAccountSettings);
            }
            yield return Menus.Sep();
        }
        yield return Menus.Item("Expand all", () => SetAll(true));
        yield return Menus.Item("Collapse all", () => SetAll(false));
        yield return Menus.Sep();
        yield return Menus.Item("Cloud account settings…", OpenAccountSettings);
    }

    private static void OpenAccountSettings() { MainWindow.Instance.Navigate("settings"); SettingsView.Current?.RevealCategory("Cloud providers"); }

    private void SetAll(bool open)
    {
        foreach (var p in CloudProviders.All) Remember(p, open);
        if (open) SessionExpanded.Remove("!" + PinnedId); else SessionExpanded.Add("!" + PinnedId);
        BuildTree();
    }

    private void SetProvider(CloudProvider p, bool open) { Remember(p, open); BuildTree(); }

    private static void Remember(CloudProvider p, bool open)
    {
        foreach (var id in ScreensOf(p).Select(s => p.Id + "/" + s.Group).Distinct().Prepend(p.Id))
            if (open) SessionExpanded.Add(id); else SessionExpanded.Remove(id);
    }

    // ---- showing a screen ---------------------------------------------------------------------------------------------

    /// <summary>Shows the screen for a nav key ("hetzner:Servers", "contabo:reference", or a provider tool key such as
    /// Vultr's "apiexplorer", which opens where it always did). Unknown keys are ignored.</summary>
    public void Show(string key)
    {
        if (_disposed) return;
        var p = CloudProviders.All.FirstOrDefault(x => key.StartsWith(x.Id + ":"));
        if (p == null)
        {
            // Provider tools are their own screens or windows (Vultr's API explorer, portal).
            if (CloudProviders.All.SelectMany(x => x.ExtraNav).Any(x => x.Key == key)) MainWindow.Instance.Navigate(key);
            return;
        }
        // 0.5.1 #10: a padlocked account opens nothing until it is unlocked (ApiFor refuses it anyway).
        if (p.ActiveAccount is { } acct && Locks.IsLocked(p.LockTarget(acct)) && !Locks.IsOpen(p.LockTarget(acct)))
        {
            _ = Dispatcher.InvokeAsync(async () => { if (await LockGate.EnsureAsync(p.LockTarget(acct), $"open {p.Title} account {acct.Name}")) Show(key); });
            return;
        }
        if (!_views.TryGetValue(key, out var view))
        {
            if (key == p.Id + ":reference") view = new CloudReferenceView(p);
            else if (p.Catalog.FirstOrDefault(d => p.NavKey(d) == key) is { } def) view = new CloudResourceView(p, def);
            else return;
            _views[key] = view;
        }
        _host.Content = view;
        Shown = key;
        Provider = p;
        // A pinned item stays selected where it was clicked; otherwise the screen's place in the provider tree is shown.
        if (!_building && (_tree.SelectedItem as TreeViewItem)?.Tag as string != key) SelectInTree(key);
    }

    private void SelectInTree(string key)
    {
        _building = true;
        try
        {
            foreach (var pNode in _tree.Items.OfType<TreeViewItem>())
                foreach (var g in pNode.Items.OfType<TreeViewItem>())
                    foreach (var i in g.Items.OfType<TreeViewItem>())
                        if (i.Tag as string == key) i.IsSelected = true;
        }
        finally { _building = false; }
    }

    private void OpenInTab(string key)
    {
        var p = CloudProviders.All.FirstOrDefault(x => key.StartsWith(x.Id + ":"));
        if (p == null) return;
        if (key == p.Id + ":reference") MainWindow.Instance.AddTab($"◍ {p.Title} reference", new CloudReferenceView(p));
        else if (p.Catalog.FirstOrDefault(d => p.NavKey(d) == key) is { } def) MainWindow.Instance.AddTab($"{def.Icon} {def.Title} · {p.Title}", new CloudResourceView(p, def));
    }

    private void ShowOverview()
    {
        var sp = new StackPanel { Margin = new Thickness(18, 14, 18, 14), MaxWidth = 820, HorizontalAlignment = HorizontalAlignment.Left };
        sp.Children.Add(new TextBlock { Text = "Choose a screen on the left", Style = (Style)FindResource("H2") });
        foreach (var p in CloudProviders.All)
        {
            var enabled = p.Accounts.Count(a => !a.Disabled);
            var box = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            box.Children.Add(new TextBlock { Text = p.Title, FontWeight = FontWeights.SemiBold });
            box.Children.Add(new TextBlock
            {
                Text = enabled == 0 ? p.ConnectHelp : $"{enabled} account(s) · active: {p.ActiveAccount?.Name} · {string.Join(", ", ScreensOf(p).Select(s => s.Group).Distinct())}",
                Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap,
            });
            if (enabled > 0)
            {
                var open = new Button { Content = $"Open {p.InstanceDef.Title.ToLowerInvariant()}", Style = (Style)FindResource("SmallButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
                open.Click += (_, _) => Show(p.NavKey(p.InstanceDef));
                box.Children.Add(open);
            }
            sp.Children.Add(box);
        }
        _host.Content = new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Shown = "";
        Provider = null;
    }

    // ---- pinned screens (in the encrypted settings; only what the user pins, nothing recorded on its own) -----------------

    /// <summary>Keeps only screens that still exist (a removed provider or screen never lingers), once each, at most
    /// <see cref="MaxPinned"/>, in the user's order.</summary>
    public static void PrunePinned(List<string> list)
    {
        var valid = CloudProviders.All.SelectMany(p => ScreensOf(p).Select(s => s.Key)).ToHashSet();
        var keep = list.Where(valid.Contains).Distinct().Take(MaxPinned).ToList();
        list.Clear(); list.AddRange(keep);
    }

    private static List<(CloudProvider P, (string Group, string Key, string Text, string? Tip) S)> PinnedScreens()
    {
        var list = AppSettings.Current.CloudPinned;
        PrunePinned(list);
        var all = CloudProviders.All.SelectMany(p => ScreensOf(p).Select(s => (P: p, S: s))).ToList();
        return list.Select(k => all.First(x => x.S.Key == k)).ToList();
    }

    public void TogglePin(string key)
    {
        var list = AppSettings.Current.CloudPinned;
        if (!list.Remove(key)) list.Add(key);
        SaveAndRebuild();
    }

    private void SaveAndRebuild()
    {
        PrunePinned(AppSettings.Current.CloudPinned);
        try { AppSettings.Save(); } catch (Exception ex) { App.Log("Save cloud pins", ex); }
        BuildTree();
    }

    // ---- accounts -------------------------------------------------------------------------------------------------------

    /// <summary>ICloudView: MainWindow calls <see cref="AccountChanged(CloudProvider)"/> for this view instead.</summary>
    public void AccountChanged() { }

    /// <summary>A provider's active account changed: its open screens reload, other providers' screens are untouched.</summary>
    public void AccountChanged(CloudProvider p)
    {
        foreach (var (key, v) in _views.ToList()) if (key.StartsWith(p.Id + ":") && v is ICloudView cv) cv.AccountChanged();
        BuildTree();
        if (Shown.Length == 0) ShowOverview();
    }

    /// <summary>A lock closed: drop the screens of accounts that are locked again, so nothing they loaded stays visible.</summary>
    private void OnLocksChanged() => Dispatcher.InvokeAsync(() =>
    {
        if (_disposed) return;
        foreach (var p in CloudProviders.All)
        {
            if (p.ActiveAccount is not { } a || !Locks.IsLocked(p.LockTarget(a)) || Locks.IsOpen(p.LockTarget(a))) continue;
            foreach (var key in _views.Keys.Where(k => k.StartsWith(p.Id + ":")).ToList())
            {
                if (_host.Content == _views[key]) { _host.Content = null; Shown = ""; }
                (_views[key] as IDisposable)?.Dispose();
                _views.Remove(key);
            }
        }
        BuildTree();
        if (Shown.Length == 0) ShowOverview();
    });

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Locks.Changed -= OnLocksChanged;
        foreach (var v in _views.Values) (v as IDisposable)?.Dispose();
        _views.Clear();
        if (Current == this) Current = null;
    }

    internal static void ResetSessionStateForTest() => SessionExpanded.Clear();
    internal IReadOnlyList<TreeViewItem> TreeItemsForTest => _tree.Items.OfType<TreeViewItem>().ToList();
    internal void SearchForTest(string q) => _search.Text = q;
    /// <summary>The right-click menu's item headers as if <paramref name="node"/> had been right-clicked.</summary>
    internal List<string> MenuForTest(TreeViewItem node)
    {
        _menuNode = node; _menuByMouse = true;
        return TreeMenu().OfType<MenuItem>().Select(m => m.Header?.ToString() ?? "").ToList();
    }
}
