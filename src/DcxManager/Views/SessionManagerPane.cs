using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DcxManager.Services;

namespace DcxManager.Views;

/// <summary>
/// The host pane of a workspace window (0.6.4, ADR-082; DarkCipher0x: "multiple monitors could have multiple tabs, plus an extra
/// host pane"; first written for 0.6.0's dock, removed in 0.6.2, back here). SecureCRT's Session Manager, in the house
/// style: a filter, a small toolbar (Connect, ＋ Host, Properties), the tree — All hosts → folders → hosts, and
/// ★ Favourites — and ＋ Folder at the bottom. Double-click or Enter connects (to the open tab when there is one,
/// ADR-076), in the window the pane belongs to (<see cref="BeforeOpen"/>); right-click has the host's or folder's
/// actions. The main window has its own: the Hosts page's folder pane as the hosts panel beside every tab (0.6.31, Shell/MainWindow.HostPane.cs).
/// Locks (ADR-067): a closed folder lock hides what's inside until it is opened; a locked host shows its padlock and
/// asks before anything opens. Shared-library hosts stay on the Hosts page.
/// </summary>
public sealed class SessionManagerPane : UserControl
{
    private readonly TreeView _tree = new() { BorderThickness = new Thickness(0), Padding = new Thickness(0, 4, 0, 4) };
    private readonly TextBox _filter = Hint.Set(new TextBox { Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(8, 4, 8, 6) }, "Filter hosts…");
    /// <summary>Folders opened in this session ("" = All hosts); kept across redraws and app restarts of the pane.</summary>
    private static readonly HashSet<string> Expanded = new(StringComparer.OrdinalIgnoreCase) { "" };
    private bool _building;
    private readonly LazyTreeLeaves _leaves;
    /// <summary>Typing in the filter redraws once the typing pauses (150 ms), not on every key (0.6.29 performance pass).</summary>
    private readonly System.Windows.Threading.DispatcherTimer _filterPause;

    public SessionManagerPane()
    {
        _leaves = new LazyTreeLeaves(HostNode);
        _filterPause = new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(150), System.Windows.Threading.DispatcherPriority.Input, (_, _) => { _filterPause!.Stop(); Build(); }, Dispatcher) { IsEnabled = false };
        var root = new DockPanel();
        var bar = new WrapPanel { Margin = new Thickness(8, 2, 8, 0) };
        bar.Children.Add(Small("⌨ Connect", "Connect to the selected host (Enter or double-click)", () => { if (SelectedHost is { } h) Connect(h); }));
        bar.Children.Add(Small("＋ Host", "Add a host (in the selected folder)", () => HostActions.Add(Window.GetWindow(this), SelectedFolder is { Length: > 0 } f ? f : null)));
        bar.Children.Add(Small("✎ Properties", "Edit the selected host", () => { if (SelectedHost is { } h) HostActions.Edit(Window.GetWindow(this), h); }));
        DockPanel.SetDock(bar, Dock.Top); root.Children.Add(bar);
        DockPanel.SetDock(_filter, Dock.Top); root.Children.Add(_filter);
        var newFolder = Small("＋ Folder", "New folder inside the selected one", () => HostActions.NewFolder(SelectedFolder ?? "", p => { Expanded.Add(SelectedFolder ?? ""); }));
        newFolder.Margin = new Thickness(8, 6, 8, 8); newFolder.HorizontalAlignment = HorizontalAlignment.Left;
        DockPanel.SetDock(newFolder, Dock.Bottom); root.Children.Add(newFolder);
        root.Children.Add(_tree);
        Content = root;

        _filter.TextChanged += (_, _) => { _filterPause.Stop(); _filterPause.Start(); };
        _tree.KeyDown += (_, e) => { if (e.Key == Key.Enter && SelectedHost is { } h) { e.Handled = true; Connect(h); } };
        _tree.PreviewMouseRightButtonDown += (_, e) => { if (e.OriginalSource is DependencyObject d && Menus.Ancestor<TreeViewItem>(d) is { } i) { i.IsSelected = true; i.Focus(); } };
        Menus.Attach(_tree, ContextItems);
        Live.While(this, h => { AppData.Changed += h; Locks.Changed += h; }, h => { AppData.Changed -= h; Locks.Changed -= h; }, () => Dispatcher.BeginInvoke(new Action(Build)));
        Build();
    }

    private Button Small(string text, string tip, Action click)
    {
        var b = new Button { Content = text, ToolTip = tip, Margin = new Thickness(0, 0, 4, 2) };
        if (TryFindResource("SmallButton") is Style s) b.Style = s;
        b.Click += (_, _) => click();
        return b;
    }

    // ---- the tree ---------------------------------------------------------------------------------------------------

    /// <summary>Redraws the tree from the saved hosts (filtered), keeping what was open and what was selected.</summary>
    internal void Build()
    {
        _building = true;
        try
        {
            var keep = (_tree.SelectedItem as TreeViewItem)?.Tag;
            _tree.Items.Clear();
            var all = AppData.Current.Hosts.Where(h => !h.IsShared).ToList();
            var q = _filter.Text.Trim();
            bool filtering = q.Length > 0;
            bool Match(HostEntry h) => !filtering || h.Display.Contains(q, StringComparison.OrdinalIgnoreCase) || h.Address.Contains(q, StringComparison.OrdinalIgnoreCase)
                                       || h.Group.Contains(q, StringComparison.OrdinalIgnoreCase) || (h.Tags ?? "").Contains(q, StringComparison.OrdinalIgnoreCase);
            var hosts = all.Where(Match).ToList();

            // 0.6.29 performance pass: folder counts in one pass, a closed lock checked once per folder, open state set
            // before the hosts go in, and host nodes made only when their folder is opened (LazyTreeLeaves).
            _leaves.Clear();
            var hidden = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            bool IsHidden(string folder) => hidden.TryGetValue(folder, out var v) ? v : hidden[folder] = Hidden(folder);
            var counts = AppData.FolderCounts(hosts);
            var root = Folder("All hosts", "", hosts.Count, "◈");
            _tree.Items.Add(root);
            var map = new Dictionary<string, TreeViewItem>(StringComparer.OrdinalIgnoreCase) { [""] = root };
            foreach (var path in AppData.Current.AllFolders)
            {
                int count = counts.GetValueOrDefault(path);
                if (filtering && count == 0) continue;
                var parent = path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
                var node = Folder(path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path, path, count, "▸");
                (map.GetValueOrDefault(parent) ?? root).Items.Add(node);
                map[path] = node;
            }
            foreach (var (path, node) in map) node.IsExpanded = filtering || Expanded.Contains(path) && !IsHidden(path);
            var sorted = hosts.OrderBy(h => h.Display, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var g in sorted.GroupBy(h => AppData.NormalizeFolder(h.Group), StringComparer.OrdinalIgnoreCase))
                if (!IsHidden(g.Key)) _leaves.Add(map.GetValueOrDefault(g.Key) ?? root, g.ToList(), filtering);   // a closed folder lock shows nothing inside it
            // Folders that are locked and closed get an "unlock to see" child, so they can still be expanded.
            foreach (var (path, node) in map)
                if (path.Length > 0 && IsHidden(path) && node.Items.Count == 0) node.Items.Add(new TreeViewItem { Header = "🔒 locked — expand to unlock", IsEnabled = false });
            var favs = sorted.Where(h => h.Favorite && !IsHidden(AppData.NormalizeFolder(h.Group))).ToList();
            var fav = Folder("Favourites", "★", favs.Count, "★");
            fav.IsExpanded = filtering || Expanded.Contains("★");
            _leaves.Add(fav, favs, filtering);
            _tree.Items.Add(fav);
            if (keep is HostEntry kh) _leaves.Find(_tree.Items, kh.Id);   // its folder's nodes exist before reselecting
            if (keep != null) Reselect(_tree.Items, keep);
        }
        finally { _building = false; }
    }

    private static bool Hidden(string folder)
    {
        for (var f = folder; f.Length > 0; f = f.Contains('/') ? f[..f.LastIndexOf('/')] : "")
            if (Locks.IsLocked("folder:" + f) && !Locks.IsOpen("folder:" + f)) return true;
        return false;
    }

    private TreeViewItem Folder(string name, string path, int count, string glyph)
    {
        var pad = path.Length > 0 && path != "★" ? LockUi.Padlock("folder:" + path) : "";
        var item = new TreeViewItem { Header = $"{glyph} {name}  ({count})" + (pad.Length > 0 ? "  " + pad : ""), Tag = path };
        item.Expanded += (_, e) =>
        {
            e.Handled = true;
            if (_building) return;
            Expanded.Add(path);
            if (path.Length > 0 && path != "★" && Hidden(path))
            {
                item.IsExpanded = false;
                _ = Dispatcher.InvokeAsync(async () => { if (await LockGate.EnsureAsync("folder:" + path, "open the folder " + name)) { Expanded.Add(path); Build(); } });
            }
        };
        item.Collapsed += (_, e) => { e.Handled = true; if (!_building) Expanded.Remove(path); };
        return item;
    }

    private TreeViewItem HostNode(HostEntry h)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(new TextBlock { Text = LockUi.Decorate("host:" + h.Id, h.Display) });
        var proto = new TextBlock { Text = "  " + (h.Protocol == "rdp" ? "RDP" : h.IsVnc ? "VNC" : h.IsTelnet ? "Telnet" : "SSH"), VerticalAlignment = VerticalAlignment.Center }.Fs(FontStep.Small);   // the type ramp, never a raw size
        proto.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        line.Children.Add(proto);
        var item = new TreeViewItem { Header = line, Tag = h, ToolTip = $"{h.Display}\n{(Locks.Hidden("host:" + h.Id) ? "🔒 locked" : h.Endpoint)}" };   // a closed lock shows no address
        item.MouseDoubleClick += (_, e) => { if (item.IsSelected) { e.Handled = true; Connect(h); } };
        return item;
    }

    private static bool Reselect(ItemCollection items, object tag)
    {
        foreach (var o in items)
            if (o is TreeViewItem i)
            {
                if (Equals(i.Tag, tag) || i.Tag is HostEntry a && tag is HostEntry b && a.Id == b.Id) { i.IsSelected = true; return true; }
                if (Reselect(i.Items, tag)) return true;
            }
        return false;
    }

    // ---- actions ----------------------------------------------------------------------------------------------------

    /// <summary>Self-test: the filter text, and the hosts the tree shows (each once, Favourites not counted twice).</summary>
    internal string Filter { get => _filter.Text; set { _filter.Text = value; _filterPause.Stop(); Build(); } }   // the test doesn't wait out the typing pause
    internal IReadOnlyList<HostEntry> ShownHosts
    {
        get
        {
            _leaves.MakeAll();
            var seen = new List<HostEntry>();
            var ids = new HashSet<string>();
            void Walk(ItemCollection items) { foreach (var o in items) if (o is TreeViewItem i) { if (i.Tag is HostEntry h && ids.Add(h.Id)) seen.Add(h); Walk(i.Items); } }
            Walk(_tree.Items);
            return seen;
        }
    }

    internal HostEntry? SelectedHost =>(_tree.SelectedItem as TreeViewItem)?.Tag as HostEntry;
    /// <summary>The selected folder, or the folder of the selected host; null on Favourites.</summary>
    private string? SelectedFolder => (_tree.SelectedItem as TreeViewItem)?.Tag switch
    {
        string s when s != "★" => s,
        HostEntry h => AppData.NormalizeFolder(h.Group),
        _ => null,
    };

    /// <summary>Runs before anything opens from this pane (0.6.4: a workspace window makes itself the target, so the
    /// session opens in the window whose pane you used).</summary>
    public Action? BeforeOpen { get; set; }

    private void Connect(HostEntry h) { BeforeOpen?.Invoke(); MainWindow.Instance?.OpenTerminal(h); }

    private IEnumerable<object?> ContextItems()
    {
        var mw = MainWindow.Instance;
        if (SelectedHost is { } h)
        {
            yield return Menus.Item("⌨ Connect", () => Connect(h), "Enter");
            yield return Menus.Item("⧉ New connection (another tab)", () =>
            {
                BeforeOpen?.Invoke();
                if (h.Protocol == "rdp") mw?.OpenRdp(h, forceNew: true);
                else if (h.IsVnc) mw?.OpenVnc(h, forceNew: true);
                else mw?.OpenTerminal(h, forceNew: true);
            }, enabled: mw?.HasOpenSession(h) == true);
            if (h.IsSsh)
            {
                yield return Menus.Item("⇅ Files", () => { BeforeOpen?.Invoke(); mw?.OpenFiles(h); });
                yield return Menus.Item("▣ Manage", () => { BeforeOpen?.Invoke(); mw?.OpenServer(h); });
            }
            yield return Menus.Sep();
            yield return Menus.Item("✎ Properties…", () => HostActions.Edit(Window.GetWindow(this), h));
            yield return Menus.Item(h.Favorite ? "☆ Remove from favourites" : "★ Add to favourites", () => { h.Favorite = !h.Favorite; AppData.Save(); });
            yield return Menus.Item("Copy address", () => Menus.Clip(h.Port is 22 or 0 ? h.Address : $"{h.Address}:{h.Port}"));
            yield return Menus.Item("Open the Hosts page", () => mw?.Navigate("hosts"));
        }
        else
        {
            var folder = SelectedFolder ?? "";
            yield return Menus.Item("＋ Add host here…", () => HostActions.Add(Window.GetWindow(this), folder.Length > 0 ? folder : null));
            yield return Menus.Item(folder.Length > 0 ? "New sub-folder…" : "New folder…", () => HostActions.NewFolder(folder, _ => Expanded.Add(folder)));
            yield return Menus.Item("Open the Hosts page", () => mw?.Navigate("hosts"));
        }
    }
}
