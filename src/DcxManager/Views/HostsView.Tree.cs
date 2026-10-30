using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DcxManager.Services;

namespace DcxManager.Views;

/// <summary>
/// The Hosts page's tree as a full host pane (0.6.7, DarkCipher0x: "in this view you cant expand the folders ... it needs to be a
/// fully working hosts pane"). Hosts sit inside their folders (and under the category nodes of the other views, Favourites
/// and shared collections): double-click or Enter connects (into the Hosts tab when sessions open there); right-click has
/// the host's full menu (the same as in the list); drag a host onto a folder to move it; the filter narrows hosts and
/// shows the folders that hold them; ＋ Host adds one to the selected folder. A closed folder lock hides what's inside, a
/// locked host shows its padlock and no address. Everything the list does stays available on the All hosts tab.
/// </summary>
public partial class HostsView
{
    /// <summary>The host selected in the tree, if a host node is.</summary>
    private HostEntry? TreeHost => (Folders.SelectedItem as TreeViewItem)?.Tag as HostEntry;

    /// <summary>Self-test: the hosts the tree shows (each once).</summary>
    internal IReadOnlyList<HostEntry> TreeHostsForTest
    {
        get
        {
            Leaves.MakeAll();
            var seen = new List<HostEntry>();
            void Walk(ItemCollection items) { foreach (var o in items) if (o is TreeViewItem { Visibility: Visibility.Visible } i) { if (i.Tag is HostEntry h && seen.All(x => x.Id != h.Id)) seen.Add(h); Walk(i.Items); } }
            Walk(Folders.Items);
            return seen;
        }
    }
    internal string TreeFilterText { get => TreeFilter.Text; set => TreeFilter.Text = value; }
    internal void RebuildForTest() => Reload();
    /// <summary>Self-test (0.6.29 scale budget): types into the list filter and returns how many rows show.</summary>
    internal int FilterListForTest(string q) { Search.Text = q; return (List.ItemsSource as System.Collections.ICollection)?.Count ?? -1; }
    internal TreeViewItem? TreeLeafForTest(string id) => FindLeaf(id);
    /// <summary>Self-test: host nodes made so far (closed folders make theirs when opened).</summary>
    internal int TreeLeafCountForTest() { int n = 0; void W(ItemCollection items) { foreach (var o in items) if (o is TreeViewItem i) { if (i.Tag is HostEntry) n++; W(i.Items); } } W(Folders.Items); return n; }
    internal string ParentKeyForTest(TreeViewItem item) => ParentKey(item);

    private void InitTree()
    {
        TreeFilter.TextChanged += (_, _) => BuildTree();
        Menus.ForText(TreeFilter);
        Folders.KeyDown += (_, e) => { if (e.Key == Key.Enter && TreeHost is { } h) { e.Handled = true; ConnectFromTree(h); } };
    }

    /// <summary>Folder, category, Favourites and shared nodes get their hosts as children (after the nodes exist). With a
    /// filter, only matching hosts stay, and nodes without any are hidden (All hosts and Favourites stay).</summary>
    private void AddHostLeaves(TreeViewItem root, Dictionary<string, TreeViewItem> map, TreeViewItem favs)
    {
        var q = TreeFilter.Text.Trim();
        bool filtering = q.Length > 0;
        bool Hit(HostEntry h) => !filtering || $"{h.Display} {h.Address} {h.Username} {h.Tags} {h.Group}".Contains(q, StringComparison.OrdinalIgnoreCase);
        var local = AppData.Current.Hosts.Where(h => !h.IsShared && Hit(h)).OrderBy(h => h.Display, StringComparer.OrdinalIgnoreCase).ToList();
        Leaves.Clear();
        // A closed folder lock shows nothing inside it; each distinct folder is checked once.
        var hidden = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        string FolderOf(HostEntry h) => AppData.NormalizeFolder(h.Group);
        bool Hidden(string folder) => hidden.TryGetValue(folder, out var v) ? v : hidden[folder] = HiddenFolder(folder);
        var shown = local.Where(h => !Hidden(FolderOf(h))).ToList();
        if (_view == "folders")
        {
            foreach (var g in shown.GroupBy(FolderOf, StringComparer.OrdinalIgnoreCase))
                AddLeaves(map.GetValueOrDefault(g.Key) ?? root, g.ToList(), filtering);
        }
        else
            foreach (var (key, node) in map)
                if (key.Length > 0 && !key.StartsWith("lib:"))
                    AddLeaves(node, shown.Where(h => Matches(h, key)).ToList(), filtering);
        AddLeaves(favs, shown.Where(h => h.Favorite).ToList(), filtering);
        // Shared library collections hold their hosts too (read as shared).
        foreach (var (key, node) in map.Where(kv => kv.Key.StartsWith("lib:") && kv.Key.Contains(" / ")))
            AddLeaves(node, Library.SharedLibraries.Hosts.Where(h => Matches(h, key) && Hit(h)).OrderBy(h => h.Display, StringComparer.OrdinalIgnoreCase).ToList(), filtering);
        // Closed folder locks: an "unlock to see" child so they can still be opened.
        foreach (var (path, node) in map)
            if (_view == "folders" && path.Length > 0 && HiddenFolder(path) && node.Items.Count == 0)
                node.Items.Add(new TreeViewItem { Header = "🔒 locked — expand to unlock", IsEnabled = false });
        if (filtering)
        {
            bool Show(TreeViewItem n)
            {
                bool any = n.Tag is HostEntry;
                foreach (var c in n.Items.OfType<TreeViewItem>().ToList()) any |= Show(c);
                n.Visibility = any || n == root || n == favs ? Visibility.Visible : Visibility.Collapsed;
                if (any) n.IsExpanded = true;
                return any;
            }
            foreach (var top in Folders.Items.OfType<TreeViewItem>()) Show(top);
        }
    }

    // Host nodes are made when their folder opens (0.6.29 performance pass, LazyTreeLeaves).
    private LazyTreeLeaves? _leavesLazy;
    private LazyTreeLeaves Leaves => _leavesLazy ??= new LazyTreeLeaves(HostLeaf);
    private void AddLeaves(TreeViewItem node, List<HostEntry> hosts, bool now) => Leaves.Add(node, hosts, now);

    /// <summary>The first host node of <paramref name="id"/> (keeps the selection across a rebuild), made if need be.</summary>
    private TreeViewItem? FindLeaf(string id) => Leaves.Find(Folders.Items, id);

    /// <summary>Expanding a folder behind a closed lock asks to unlock it first, then shows its hosts.</summary>
    private void UnlockOnExpand(TreeViewItem item, string path)
    {
        if (_view != "folders" || path.Length == 0 || path == "★" || !IsFolderKey(path) || !HiddenFolder(path)) return;
        item.IsExpanded = false;
        _ = Dispatcher.InvokeAsync(async () => { if (await LockGate.EnsureAsync("folder:" + path, "open the folder " + path)) { _expanded.Add(path); BuildTree(); } });
    }

    private static bool HiddenFolder(string folder)
    {
        for (var f = folder; f.Length > 0; f = f.Contains('/') ? f[..f.LastIndexOf('/')] : "")
            if (Locks.IsLocked("folder:" + f) && !Locks.IsOpen("folder:" + f)) return true;
        return false;
    }

    private TreeViewItem HostLeaf(HostEntry h)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(new TextBlock { Text = LockUi.Decorate("host:" + h.Id, h.Display) });
        var proto = new TextBlock { Text = "  " + (h.Protocol == "rdp" ? "RDP" : h.IsVnc ? "VNC" : h.IsTelnet ? "Telnet" : "SSH"), VerticalAlignment = VerticalAlignment.Center }.Fs(FontStep.Small);
        proto.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        line.Children.Add(proto);
        var item = new TreeViewItem
        {
            Header = line, Tag = h, Padding = new Thickness(2, 1, 2, 1), AllowDrop = true,
            ToolTip = $"{h.Display}\n{(Locks.Hidden("host:" + h.Id) ? "🔒 locked" : h.Endpoint)}\nDouble-click to connect · right-click for more",
        };
        item.MouseDoubleClick += (_, e) => { if (item.IsSelected) { e.Handled = true; ConnectFromTree(h); } };
        return item;
    }

    /// <summary>Connects to a host from the tree: a tab in the main window's tab bar (0.6.31: never a tab inside a tab).</summary>
    private void ConnectFromTree(HostEntry h)
    {
        if (PaneInWindow) MainWindow.Instance?.Panes.ActivateMain();
        MainWindow.Instance?.OpenTerminal(h);   // RDP, VNC and Telnet hosts open the right session by themselves
    }

    /// <summary>The right-click menu of the tree: a host's full menu (the list's), or the folder's.</summary>
    private IEnumerable<object?> TreeMenu()
    {
        if (TreeHost is not { } h) return FolderMenu(_folder);
        SelectInList(h);
        return HostMenu();
    }

    /// <summary>Selecting a host in the tree selects it in the list (which shows its folder), so every list action applies.</summary>
    private void SelectInList(HostEntry h)
    {
        var row = (List.ItemsSource as IEnumerable<HostRow>)?.FirstOrDefault(r => r.Host.Id == h.Id);
        if (row == null && Search.Text.Length > 0) { Search.Text = ""; row = (List.ItemsSource as IEnumerable<HostRow>)?.FirstOrDefault(r => r.Host.Id == h.Id); }
        if (row == null) return;
        List.SelectedItems.Clear();
        List.SelectedItem = row;
        List.ScrollIntoView(row);
    }

    /// <summary>The key of the nearest folder / category node above a tree item (a host's folder).</summary>
    private static string ParentKey(TreeViewItem item)
    {
        for (var p = item.Parent as TreeViewItem; p != null; p = p.Parent as TreeViewItem)
            if (p.Tag is string key) return key;
        return "";
    }

    private void OnNewHostInTree(object sender, RoutedEventArgs e)
    {
        var folder = TreeHost is { } h ? AppData.NormalizeFolder(h.Group) : _view == "folders" && _folder is not ("" or "★") && IsFolderKey(_folder) ? _folder : null;
        AddHost(folder);
    }
}
