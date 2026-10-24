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
            var seen = new List<HostEntry>();
            void Walk(ItemCollection items) { foreach (var o in items) if (o is TreeViewItem { Visibility: Visibility.Visible } i) { if (i.Tag is HostEntry h && seen.All(x => x.Id != h.Id)) seen.Add(h); Walk(i.Items); } }
            Walk(Folders.Items);
            return seen;
        }
    }
    internal string TreeFilterText { get => TreeFilter.Text; set => TreeFilter.Text = value; }
    internal void RebuildForTest() => Reload();
    internal TreeViewItem? TreeLeafForTest(string id) => FindLeaf(Folders.Items, id);
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
        if (_view == "folders")
        {
            foreach (var h in local)
            {
                var folder = AppData.NormalizeFolder(h.Group);
                if (HiddenFolder(folder)) continue;   // a closed folder lock shows nothing inside it
                (map.GetValueOrDefault(folder) ?? root).Items.Add(HostLeaf(h));
            }
        }
        else
            foreach (var (key, node) in map)
                if (key.Length > 0 && !key.StartsWith("lib:"))
                    foreach (var h in local.Where(h => Matches(h, key) && !HiddenFolder(AppData.NormalizeFolder(h.Group)))) node.Items.Add(HostLeaf(h));
        foreach (var h in local.Where(h => h.Favorite && !HiddenFolder(AppData.NormalizeFolder(h.Group)))) favs.Items.Add(HostLeaf(h));
        // Shared library collections hold their hosts too (read as shared).
        foreach (var (key, node) in map.Where(kv => kv.Key.StartsWith("lib:") && kv.Key.Contains(" / ")))
            foreach (var h in Library.SharedLibraries.Hosts.Where(h => Matches(h, key) && Hit(h)).OrderBy(h => h.Display, StringComparer.OrdinalIgnoreCase)) node.Items.Add(HostLeaf(h));
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

    /// <summary>The first host node of <paramref name="id"/> (keeps the selection across a rebuild).</summary>
    private static TreeViewItem? FindLeaf(ItemCollection items, string id)
    {
        foreach (var o in items)
            if (o is TreeViewItem i)
            {
                if (i.Tag is HostEntry h && h.Id == id) return i;
                if (FindLeaf(i.Items, id) is { } found) return found;
            }
        return null;
    }

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

    /// <summary>Connects to a host from the tree, into the Hosts tab when sessions open there.</summary>
    private void ConnectFromTree(HostEntry h)
    {
        ArmSessions();
        MainWindow.Instance?.OpenTerminal(h);   // RDP, VNC and Telnet hosts open the right session by themselves
    }

    /// <summary>The right-click menu of the tree: a host's full menu (the list's), or the folder's.</summary>
    private IEnumerable<object?> TreeMenu()
    {
        if (TreeHost is not { } h) return FolderMenu(_folder);
        SelectInList(h);
        ArmSessions();
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
