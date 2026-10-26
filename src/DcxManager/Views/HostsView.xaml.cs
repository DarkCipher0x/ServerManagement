using System.ComponentModel;
using System.IO;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using DcxManager.Services;
using DcxManager.Ssh;
using DcxManager.Vnc;
using DcxManager.Vultr;

namespace DcxManager.Views;

public sealed class HostRow(HostEntry host) : INotifyPropertyChanged
{
    public HostEntry Host { get; } = host;
    /// <summary>The name with its padlock when the host (or its folder) is locked (0.5.1, ADR-067).</summary>
    public string Name => LockUi.Decorate("host:" + Host.Id, Host.Display);
    public string Star => Host.IsShared ? "⇄" : Host.Favorite ? "★" : "";
    public string Proto => Host.Protocol == "rdp" ? "RDP" : Host.IsVnc ? (Host.Vnc.Kind == "mac" ? "VNC (Mac)" : "VNC") : Host.IsTelnet ? "Telnet ⚠" : "SSH";
    public string PlatformText => HostEntry.PlatformLabel(Host.EffectivePlatform);
    /// <summary>Environment label (ADR-017): the text is always shown; the brush is the theme's colour for it.</summary>
    public string EnvText => Envs.Normalize(Host.Environment);
    public System.Windows.Media.Brush? EnvBrush => EnvText.Length > 0 ? EnvUi.BrushFor(EnvText) : null;
    public string AuthText
    {
        get
        {
            var c = AppData.Current.FindCredential(Host.CredentialId);
            return Host.Auth switch
            {
                AuthMethod.PrivateKey => c != null ? "key · " + c.Name : "key (none chosen!)",
                AuthMethod.KeyboardInteractive => "interactive" + (c != null ? " · " + c.Name : ""),
                _ => c != null ? "password · " + c.Name : "password (ask)",
            };
        }
    }
    public string JumpText => AppData.Current.FindHost(Host.JumpHostId)?.Display ?? "";
    public string LastText => Host.LastConnected?.ToString("yyyy-MM-dd HH:mm") ?? "";
    private string _reach = "";
    public string Reach { get => _reach; set { _reach = value; Changed(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public partial class HostsView : UserControl, IDisposable
{
    private List<HostRow> _rows = new();
    private string _folder = "";                         // "" = all hosts
    /// <summary>Folders the user expanded this session ("" = the "All hosts" root). Everything starts collapsed (ADR-028);
    /// kept while the app runs (closing and reopening the Hosts tab keeps it), never saved, pruned to existing folders.</summary>
    private static readonly HashSet<string> _expanded = new(StringComparer.OrdinalIgnoreCase);
    internal static IReadOnlyCollection<string> ExpandedForTest => _expanded;
    internal static void ResetExpandedForTest() => _expanded.Clear();
    internal static void ExpandForTest(string path) => _expanded.Add(path);
    internal TreeView FoldersForTest => Folders;
    private bool _building;

    private string _view = "folders";                    // folders | platform | protocol | tags | jump
    private List<GridViewColumn> _allColumns = new();

    public HostsView()
    {
        InitializeComponent();
        InitFolderPane();   // 0.6.2: the folder pane pins or auto-collapses inside this page (HostsView.FolderPane.cs)
        InitSessions();     // 0.6.7: sessions opened from here open inside this page (HostsView.Sessions.cs)
        AppData.Changed += OnStoreChanged;
        Locks.Changed += OnLocksChanged;   // padlocks redraw (ADR-067)
        Library.SharedLibraries.Changed += OnStoreChanged;
        IsVisibleChanged += (_, _) => { if (IsVisible && _stale) { _stale = false; Reload(); } };
        _view = AppSettings.Current.HostsView;
        foreach (ComboBoxItem i in ViewBox.Items) if ((string)i.Tag == _view) ViewBox.SelectedItem = i;
        MoreSlot.Content = Menus.MoreButton(MoreMenu, style: "SmallButton");
        if (List.View is GridView gv0) { _allColumns = gv0.Columns.ToList(); ApplyColumns(); }
        Menus.ForList(List, this, HostMenu);
        Menus.SelectOnRightClick(List);
        Folders.PreviewMouseRightButtonDown += (_, e) => { if (e.OriginalSource is DependencyObject d && Menus.Ancestor<TreeViewItem>(d) is { } i) { i.IsSelected = true; i.Focus(); } };
        Menus.Attach(Folders, TreeMenu);   // 0.6.7: a host node gets the host's full menu (HostsView.Tree.cs)
        InitTree();
        // 0.5.0 (DarkCipher0x: "drag and drop hosts into different folders"): hosts from the list and whole folders inside the tree,
        // with a picture of what moves under the cursor, the target folder outlined, and a closed folder opening when you
        // hover over it.
        Folders.DragOver += OnFolderDragOver;
        Folders.DragLeave += (_, _) => { DragGhost.Target(null); _hoverOpen.Stop(); };
        Folders.Drop += OnFolderDrop;
        _hoverOpen.Tick += (_, _) => { _hoverOpen.Stop(); if (_hoverItem is { IsExpanded: false } hi) hi.IsExpanded = true; };
        Point? folderDown = null; TreeViewItem? folderItem = null;
        Folders.PreviewMouseLeftButtonDown += (_, e) => { folderDown = e.GetPosition(null); folderItem = e.OriginalSource is DependencyObject d ? Menus.Ancestor<TreeViewItem>(d) : null; };
        Folders.PreviewMouseMove += (_, e) =>
        {
            if (folderDown is { } h0 && folderItem is { Tag: HostEntry dragged } hostItem && e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
            {
                var hp = e.GetPosition(null);
                if (Math.Abs(hp.X - h0.X) < 8 && Math.Abs(hp.Y - h0.Y) < 8) return;
                folderDown = null;   // 0.6.7: a host dragged from the tree onto a folder moves there
                DragGhost.Run(hostItem, new DataObject(typeof(HostEntry[]), new[] { dragged }), DragDropEffects.Move, default, label: "▣ " + dragged.Display);
                return;
            }
            if (folderDown is not { } d0 || folderItem is not { Tag: string path } item || e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
            var p = e.GetPosition(null);
            if (Math.Abs(p.X - d0.X) < 8 && Math.Abs(p.Y - d0.Y) < 8) return;
            folderDown = null;
            if (path.Length == 0 || path == "★" || !IsFolderKey(path)) return;   // the root, favourites and categories stay put
            DragGhost.Run(item, new DataObject(FolderFormat, path), DragDropEffects.Move, default, label: "📁 " + path);
        };
        Point? down = null;
        List.PreviewMouseLeftButtonDown += (_, e) => down = e.GetPosition(null);
        List.PreviewMouseMove += (_, e) =>
        {
            if (down is not { } d || e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
            var p = e.GetPosition(null);
            if (Math.Abs(p.X - d.X) < 8 && Math.Abs(p.Y - d.Y) < 8) return;
            if (e.OriginalSource is DependencyObject src && Menus.Ancestor<ListViewItem>(src) == null) return;
            down = null;
            var hosts = SelectedAll.ToArray();
            if (hosts.Length > 0) DragGhost.Run(List, new DataObject(typeof(HostEntry[]), hosts), DragDropEffects.Move, default, label: hosts.Length == 1 ? "▣ " + hosts[0].Display : $"▣ {hosts.Length} hosts");
        };
        Reload();
    }

    public void Dispose() { EndSessionArea(); AppData.Changed -= OnStoreChanged; Locks.Changed -= OnLocksChanged; Library.SharedLibraries.Changed -= OnStoreChanged; }

    private bool _stale;
    /// <summary>A Hosts tab behind other tabs rebuilds when it comes to the front, not on every save (0.6.29 performance
    /// pass: 10,000 hosts took 2 s per save). While the main window isn't showing (start-up, the self-test) it reloads at once.</summary>
    private void OnStoreChanged()
    {
        if (!IsVisible && DcxManager.MainWindow.Instance is { IsVisible: true }) { _stale = true; return; }
        _stale = false;
        Reload();
    }

    /// <summary>Secondary actions — also folded into the list's right-click menu by Menus.MirrorButtons.</summary>
    private IEnumerable<object?> MoreMenu()
    {
        var sel = SelectedAll.ToList();
        var one = Selected;
        var ev = new RoutedEventArgs();
        yield return Menus.Item("＋ Add Windows host (Remote Desktop)…", () => AddHost(_view == "folders" && _folder is not ("" or "★") ? _folder : null, "rdp"));
        yield return Menus.Item("＋ Add Mac or Linux desktop (VNC)…", () => AddHost(_view == "folders" && _folder is not ("" or "★") ? _folder : null, "vnc"));
        yield return Menus.Item("⌕ Find hosts on the network…", () => HostImportWindow.Show(Window.GetWindow(this)),
            enabled: Restrictions.Allowed("host.add") && Restrictions.Allowed("network.scan"),
            tip: "Scan your network for SSH, Remote Desktop and VNC, review what answered (✦ the AI can suggest names), tick and import");
        // 0.6.2: the folder pane, pinned or auto-collapsing inside this page (also its own 📌 / 📍).
        bool paneManaged = PolicySettings.Get(nameof(AppSettings.HostsDock)) != null;
        yield return FolderMode == "auto"
            ? Menus.Item("📌 Pin the folder pane open", () => SetFolderMode("pinned"), enabled: !paneManaged, tip: paneManaged ? "🔒 Set by your organisation (Group Policy: HostsDock)." : null)
            : Menus.Item("📍 Auto-collapse the folder pane", () => SetFolderMode("auto"), enabled: !paneManaged, tip: paneManaged ? "🔒 Set by your organisation (Group Policy: HostsDock)." : "Folds to a strip on the left of this page; point at it to slide the folders out");
        // 0.6.7 (ADR-086): sessions inside this tab, or separate tabs.
        bool openManaged = PolicySettings.Get(nameof(AppSettings.HostsOpenIn)) != null;
        yield return OpenIn == "hosts"
            ? Menus.Item("⧉ Open sessions as separate tabs", () => SetOpenIn("tab"), enabled: !openManaged, tip: openManaged ? "🔒 Set by your organisation (Group Policy: HostsOpenIn)." : "Terminals and desktops you open from Hosts get their own tabs, as before")
            : Menus.Item("▣ Open sessions inside the Hosts tab", () => SetOpenIn("hosts"), enabled: !openManaged, tip: openManaged ? "🔒 Set by your organisation (Group Policy: HostsOpenIn)." : "Terminals and desktops you open from Hosts open as tabs inside it, beside the folder pane");
        yield return Menus.Item("⧉ Duplicate", () => OnDuplicate(this, ev), enabled: one != null);
        yield return Menus.Item(one is { Favorite: true } ? "☆ Remove from favourites" : "★ Add to favourites", () => OnFavourite(this, ev), enabled: sel.Count > 0);
        yield return Menus.Item("Copy address", () => OnCopyAddress(this, ev), enabled: one != null);
        yield return Menus.Sep();
        yield return Menus.Item("⧉ Open in Windows Terminal (OpenSSH)", () => OnExternal(this, ev), enabled: one is { Protocol: not "rdp" });
        yield return Menus.AsyncItem($"⧉ Open in {IdeUi.EditorTitle ?? "an IDE (VS Code…)"}", async () => { if (one != null) await IdeUi.OpenHostAsync(one); }, enabled: one is { Protocol: "ssh" });
        yield return Menus.Item("🖥 Remote Desktop", () => { if (one != null) MainWindow.Instance.OpenRdp(one); }, enabled: one is { Protocol: "rdp" }, tip: "Windows hosts only — set the type in the host editor");
        yield return Menus.Item("⚿ Install SSH key…", () => OnInstallKey(this, ev), enabled: one is { Protocol: not "rdp" }, tip: "Copy a vault public key into the server's authorized_keys (ssh-copy-id)");
        yield return Menus.Item("◉ Check reachability", () => OnPing(this, ev), tip: "Tests the SSH/RDP port of every host listed");
        yield return Menus.Sep();
        yield return Menus.Sub("Columns", _allColumns.Select(c => (object?)ColumnToggle(c)).ToArray());
        yield return Menus.Sep();
        foreach (var p in CloudProviders.All)
            yield return Menus.AsyncItem($"⇩ Import from {p.Title}…", () => ImportFromCloudAsync(p), enabled: p.ActiveAccount != null);
        yield return Menus.Item("⇩ Import from another app…", () => ConnectionImportDialog.Show(Window.GetWindow(this), _folder is "" or "★" || !IsFolderKey(_folder) ? null : _folder), tip: "mRemoteNG, SecureCRT, Royal TS, MobaXterm, PuTTY / KiTTY, WinSCP, RDCMan, .rdp files, Devolutions RDM");
        yield return Menus.Item("⇩ Import ~/.ssh/config…", () => OnImportSshConfig(this, ev));
        yield return Menus.Item("⇪ Export (encrypted)…", () => OnExport(this, ev), tip: "Password-encrypted file with the selected hosts / folder and their credentials");
        yield return Menus.Item("⇩ Import .dcxbackup…", () => OnImport(this, ev), tip: "Merge into your hosts or replace everything");
    }

    private MenuItem ColumnToggle(GridViewColumn c)
    {
        var name = c.Header?.ToString() ?? "";
        var hidden = AppSettings.Current.HostsHiddenColumns;
        bool shown = !hidden.Contains(name);
        var m = Menus.Item(name.Length == 0 ? "★ (favourite)" : name, () =>
        {
            if (shown) hidden.Add(name); else hidden.Remove(name);
            try { AppSettings.Save(); } catch { }
            ApplyColumns();
        }, enabled: name != "Name");
        m.IsCheckable = true; m.IsChecked = shown;
        return m;
    }

    /// <summary>Shows the columns the user wants, in their original order (GridView can't hide a column, so rebuild).</summary>
    private void ApplyColumns()
    {
        if (List.View is not GridView gv) return;
        var hidden = AppSettings.Current.HostsHiddenColumns;
        gv.Columns.Clear();
        foreach (var c in _allColumns) if (!hidden.Contains(c.Header?.ToString() ?? "")) gv.Columns.Add(c);
    }

    private void OnViewChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewBox.SelectedItem is not ComboBoxItem i || List == null) return;
        _view = (string)i.Tag;
        _folder = "";
        AppSettings.Current.HostsView = _view;
        try { AppSettings.Save(); } catch { }
        BuildTree();
        Apply();
    }

    /// <summary>Does a host belong under the selected tree node (a folder path, "★", or a prefixed category key)?</summary>
    private static bool Matches(HostEntry h, string key)
    {
        if (key.Length == 0) return true;
        // Shared library hosts (ADR-033) appear under "All hosts" and their own Shared node only, never in local folders.
        if (key.StartsWith("lib:")) return h.IsShared && ("lib:" + h.SharedFrom == key || ("lib:" + h.SharedFrom).StartsWith(key + " / ", StringComparison.Ordinal));
        if (h.IsShared) return false;
        if (key == "★") return h.Favorite;
        if (key.StartsWith("p:")) return h.EffectivePlatform == key[2..];
        if (key.StartsWith("proto:")) return h.Protocol == key[6..];
        if (key.StartsWith("t:")) return key.Length == 2 ? h.Tags.Trim().Length == 0 : TagsOf(h).Contains(key[2..], StringComparer.OrdinalIgnoreCase);
        if (key.StartsWith("j:")) return key.Length == 2 ? h.JumpHostId == null : h.JumpHostId == key[2..];
        return AppData.InFolder(AppData.NormalizeFolder(h.Group), key);
    }

    private static bool IsFolderKey(string key) => key.Length == 0 || key == "★" || !(key.StartsWith("p:") || key.StartsWith("proto:") || key.StartsWith("t:") || key.StartsWith("j:") || key.StartsWith("lib:"));

    private static IEnumerable<string> TagsOf(HostEntry h) => h.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private void Reload()
    {
        var reach = _rows.ToDictionary(r => r.Host.Id, r => r.Reach);
        _rows = AppData.Current.Hosts.OrderByDescending(h => h.Favorite).ThenBy(h => h.Group).ThenBy(h => h.Display)
            .Concat(Library.SharedLibraries.Hosts.OrderBy(h => h.Group).ThenBy(h => h.Display))
            .Select(h => new HostRow(h) { Reach = reach.GetValueOrDefault(h.Id, "") }).ToList();
        BuildTree();
        Apply();
    }

    // ---- folder tree ----------------------------------------------------------------------------------------------

    private void BuildTree()
    {
        var keepHost = TreeHost?.Id;
        _building = true;
        try
        {
            Folders.Items.Clear();
            var hosts = AppData.Current.Hosts;
            var root = Node("All hosts", "", hosts.Count + Library.SharedLibraries.Hosts.Count, "◈");
            root.IsExpanded = _expanded.Contains("");
            // Removed folders don't keep a remembered state (no stale entries accumulate).
            var folders = AppData.Current.AllFolders;   // worked out once per build (0.6.29 performance pass)
            var known = new HashSet<string>(folders, StringComparer.OrdinalIgnoreCase);
            _expanded.RemoveWhere(p => p.Length > 0 && !known.Contains(p));
            Folders.Items.Add(root);
            var map = new Dictionary<string, TreeViewItem>(StringComparer.OrdinalIgnoreCase) { [""] = root };
            void Cat(string label, string key, string glyph) { var n = Node(label, key, hosts.Count(h => Matches(h, key)), glyph); root.Items.Add(n); map[key] = n; }
            switch (_view)
            {
                case "platform":
                    foreach (var p in HostEntry.Platforms) Cat(HostEntry.PlatformLabel(p), "p:" + p, p switch { "linux" => "🐧", "windows" => "⊞", "macos" => "⌘", "network" => "⇄", _ => "▫" });
                    break;
                case "protocol":
                    Cat("SSH", "proto:ssh", "⌨"); Cat("Remote Desktop", "proto:rdp", "🖥");
                    break;
                case "tags":
                    foreach (var t in hosts.SelectMany(TagsOf).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t, StringComparer.OrdinalIgnoreCase)) Cat(t, "t:" + t, "#");
                    Cat("(no tags)", "t:", "·");
                    break;
                case "jump":
                    var jumps = hosts.Select(x => x.JumpHostId).Where(id => id != null).ToHashSet();
                    foreach (var j in hosts.Where(h => jumps.Contains(h.Id)).OrderBy(h => h.Display)) Cat("via " + j.Display, "j:" + j.Id, "⇢");
                    Cat("(direct)", "j:", "·");
                    break;
                default:
                    var counts = AppData.FolderCounts(hosts);   // one pass, not one pass per folder
                    int Count(string path) => counts.GetValueOrDefault(path);
                    foreach (var path in folders)
                    {
                        var parent = path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
                        var name = path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;
                        var node = Node(name, path, Count(path), "▸");
                        node.IsExpanded = _expanded.Contains(path);
                        (map.GetValueOrDefault(parent) ?? root).Items.Add(node);
                        map[path] = node;
                    }
                    break;
            }
            NewFolderBtn.Visibility = _view == "folders" ? Visibility.Visible : Visibility.Collapsed;
            var favs = Node("Favourites", "★", hosts.Count(h => h.Favorite), "★");
            Folders.Items.Add(favs);
            // One node per shared library, a child per collection (ADR-033).
            foreach (var c in Library.SharedLibraries.Clients.OrderBy(c => c.State.Name))
            {
                var shared = Library.SharedLibraries.Hosts;
                var libKey = "lib:" + c.State.Name;
                var lib = Node("Shared · " + c.State.Name + (c.State.LastError != null ? " (cached)" : c.State.Pending ? " (awaiting approval)" : ""), libKey, shared.Count(h => Matches(h, libKey)), "⇄");
                lib.IsExpanded = _expanded.Contains(libKey);
                lib.ToolTip = c.State.LastError != null ? $"The share can't be reached ({c.State.LastError}); showing the copy cached {c.State.LastSync:yyyy-MM-dd HH:mm}" : $"Shared library, synced {c.State.LastSync:yyyy-MM-dd HH:mm}";
                foreach (var (cid, cname) in c.State.Collections.OrderBy(x => x.Value))
                {
                    var key = $"lib:{c.State.Name} / {cname}";
                    var n = Node(cname + (c.CanEdit(cid) ? "" : " (read-only)"), key, shared.Count(h => Matches(h, key)), "▸");
                    lib.Items.Add(n); map[key] = n;
                }
                Folders.Items.Add(lib); map[libKey] = lib;
            }
            AddHostLeaves(root, map, favs);   // 0.6.7: hosts inside their folders (HostsView.Tree.cs)
            if (keepHost != null && FindLeaf(keepHost) is { } leaf) { leaf.IsSelected = true; for (var p = leaf.Parent as TreeViewItem; p != null; p = p.Parent as TreeViewItem) p.IsExpanded = true; }
            else if (map.TryGetValue(_folder, out var sel)) { sel.IsSelected = true; for (var p = sel.Parent as TreeViewItem; p != null; p = p.Parent as TreeViewItem) p.IsExpanded = true; }
            else if (_folder == "★") favs.IsSelected = true;
            else { _folder = ""; root.IsSelected = true; }
        }
        finally { _building = false; }
    }

    private TreeViewItem Node(string name, string path, int count, string glyph)
    {
        var item = new TreeViewItem { Header = $"{glyph} {name}  ({count})" + (path.Length > 0 && path != "★" && LockUi.Padlock("folder:" + path) is { Length: > 0 } pad ? "  " + pad : ""), Tag = path, AllowDrop = true, Padding = new Thickness(2, 1, 2, 1) };
        item.Expanded += (_, e) => { if (!_building && path != "★") { _expanded.Add(path); UnlockOnExpand(item, path); } e.Handled = true; };
        item.Collapsed += (_, e) => { if (!_building) _expanded.Remove(path); e.Handled = true; };
        item.MouseDoubleClick += (_, e) => { if (item.IsSelected && path.Length > 0 && path != "★") { e.Handled = true; } };
        return item;
    }

    /// <summary>Sidebar → TEAM → Team hosts: the first shared library's node (any others are right beside it).</summary>
    public void ShowShared()
    {
        if (Library.SharedLibraries.Clients.OrderBy(c => c.State.Name).FirstOrDefault() is not { } c) { MainWindow.Instance.Status("No shared library on this PC yet: Shared library → Create or Join."); return; }
        _folder = "lib:" + c.State.Name;
        if (_stale) { _stale = false; Reload(); }   // opened from behind other tabs: the rows first
        else { BuildTree(); Apply(); }
    }

    internal string FolderForTest => _folder;

    private void OnFolderSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_building) return;
        if (Folders.SelectedItem is TreeViewItem { Tag: HostEntry h } hi)   // 0.6.7: a host: its folder in the list, the host selected
        {
            var key = ParentKey(hi);
            if (key != _folder) { _folder = key; Apply(); }
            SelectInList(h);
            return;
        }
        _folder = (Folders.SelectedItem as TreeViewItem)?.Tag as string ?? "";
        Apply();
    }

    private const string FolderFormat = "dcx-host-folder";
    private readonly System.Windows.Threading.DispatcherTimer _hoverOpen = new() { Interval = TimeSpan.FromMilliseconds(650) };
    private TreeViewItem? _hoverItem;

    /// <summary>The folder a drag would land in, or null (favourites, categories, a folder dropped on itself or inside itself).</summary>
    private static string? DropTarget(DragEventArgs e, out TreeViewItem? item)
    {
        item = e.OriginalSource is DependencyObject d ? Menus.Ancestor<TreeViewItem>(d) : null;
        var target = item?.Tag switch { string s => s, HostEntry when item != null => ParentKey(item), _ => null };   // 0.6.7: onto a host = into its folder
        if (target == null || target == "★" || !IsFolderKey(target)) return null;
        if (e.Data.GetData(FolderFormat) is string moving && (target.Equals(moving, StringComparison.OrdinalIgnoreCase) || AppData.InFolder(target, moving)
            || (moving.Contains('/') ? moving[..moving.LastIndexOf('/')] : "").Equals(target, StringComparison.OrdinalIgnoreCase))) return null;
        return e.Data.GetDataPresent(typeof(HostEntry[])) || e.Data.GetDataPresent(FolderFormat) ? target : null;
    }

    private void OnFolderDragOver(object sender, DragEventArgs e)
    {
        var target = DropTarget(e, out var item);
        e.Effects = target != null ? DragDropEffects.Move : DragDropEffects.None;
        DragGhost.Target(target != null ? item : null);
        if (item != _hoverItem) { _hoverItem = item; _hoverOpen.Stop(); if (item is { IsExpanded: false, HasItems: true }) _hoverOpen.Start(); }
        e.Handled = true;
    }

    private void OnFolderDrop(object sender, DragEventArgs e)
    {
        DragGhost.Target(null); _hoverOpen.Stop();
        if (DropTarget(e, out _) is not { } target) return;
        e.Handled = true;
        if (e.Data.GetData(FolderFormat) is string moving)
        {
            var leaf = moving.Contains('/') ? moving[(moving.LastIndexOf('/') + 1)..] : moving;
            MoveFolder(moving, target.Length == 0 ? leaf : target + "/" + leaf);
            return;
        }
        if (e.Data.GetData(typeof(HostEntry[])) is HostEntry[] hosts) MoveHosts(hosts, target.Length == 0 ? "General" : target);
    }

    private void MoveHosts(IEnumerable<HostEntry> hosts, string folder)
    {
        var list = hosts.Where(h => !h.IsShared).ToList();
        if (list.Count == 0) return;
        if (!Restrictions.Check("host.edit")) return;
        if (!list.All(h => Locks.IsOpen("host:" + h.Id))) { AfterUnlock(list, "move", () => MoveHosts(list, folder)); return; }
        foreach (var h in list) h.Group = folder;
        // 0.6.29 (ADR-105): a folder with defaults offers them to the hosts moved in (what they already follow comes along).
        bool adopt = Services.FolderDefaults.Describe(folder, FolderDefaultsWindow.Show) is { Length: > 0 } d && !TestMode.Running
                     && Dialogs.Confirm("Folder defaults", $"“{folder}” has defaults: {d}.\n\nShould the {list.Count} host(s) you moved follow them? Their own values for these fields are replaced.", "Follow the folder's defaults");
        if (adopt) Services.FolderDefaults.Adopt(list, Services.FolderDefaults.Fields);
        AppData.Save();
        AuditLog.Write("you", folder, $"moved {list.Count} host(s) into folder" + (adopt ? " (they follow its defaults)" : ""));
        MainWindow.Instance.Status($"Moved {list.Count} host(s) to {folder}");
    }

    private void OnNewFolder(object sender, RoutedEventArgs e) => NewFolder(_folder is "★" || !IsFolderKey(_folder) ? "" : _folder);

    private void NewFolder(string parent) => HostActions.NewFolder(parent, path => { _expanded.Add(parent); _folder = path; });

    private void RenameFolder(string path)
    {
        if (!Restrictions.Check("folder.edit")) return;
        if (!Locks.IsOpen("folder:" + path)) { _ = Dispatcher.InvokeAsync(async () => { if (await LockGate.EnsureAsync("folder:" + path, "rename the folder")) RenameFolder(path); }); return; }
        var leaf = path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;
        if (Dialogs.Prompt("Rename / move folder", "New name — or a full path with / to move it (e.g. Archive/Old):", leaf) is not { Length: > 0 } input) return;
        var parent = path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
        var target = AppData.NormalizeFolder(input.Contains('/') ? input : (parent.Length > 0 ? parent + "/" + input : input));
        if (target.Length == 0 || target.Equals(path, StringComparison.OrdinalIgnoreCase)) return;
        if (AppData.InFolder(target, path)) { Dialogs.Error("Folder", "A folder can't be moved inside itself."); return; }
        MoveFolder(path, target);
    }

    /// <summary>Moves (or renames) a folder with its sub-folders and hosts to <paramref name="target"/>. Refuses a move into
    /// itself. Used by Rename / move… and by dragging a folder onto another (0.5.0).</summary>
    internal bool MoveFolder(string path, string target)
    {
        path = AppData.NormalizeFolder(path); target = AppData.NormalizeFolder(target);
        if (path.Length == 0 || target.Length == 0 || target.Equals(path, StringComparison.OrdinalIgnoreCase) || AppData.InFolder(target, path)) return false;
        if (!Restrictions.Check("folder.edit")) return false;
        if (!Locks.IsOpen("folder:" + path)) { Dialogs.Info("Folder", $"“{path}” is locked. Unlock it first (right-click → 🔒 Lock → Unlock for now)."); return false; }
        string Re(string g) => AppData.InFolder(g, path) ? target + g[path.Length..] : g;
        foreach (var h in AppData.Current.Hosts.Where(h => !h.IsShared)) h.Group = Re(AppData.NormalizeFolder(h.Group));
        AppData.Current.Folders = AppData.Current.Folders.Select(f => Re(AppData.NormalizeFolder(f))).Append(target).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Services.FolderDefaults.Renamed(path, target);   // 0.6.29: its defaults move with it
        if (_expanded.Remove(path)) _expanded.Add(target);
        _folder = target;
        AppData.Save();
        AuditLog.Write("you", path, "folder moved to " + target);
        MainWindow.Instance?.Status($"Moved the folder “{path}” to “{target}”.");
        return true;
    }

    private void DeleteFolder(string path)
    {
        if (!Restrictions.Check("folder.edit")) return;
        if (!Locks.IsOpen("folder:" + path)) { _ = Dispatcher.InvokeAsync(async () => { if (await LockGate.EnsureAsync("folder:" + path, "delete the folder")) DeleteFolder(path); }); return; }
        var inside = AppData.Current.Hosts.Where(h => AppData.InFolder(AppData.NormalizeFolder(h.Group), path)).ToList();
        var parent = path.Contains('/') ? path[..path.LastIndexOf('/')] : "General";
        if (!Dialogs.Confirm("Delete folder", $"Delete the folder “{path}” and its sub-folders?" + (inside.Count > 0 ? $"\n\nIts {inside.Count} host(s) are kept and moved to “{parent}”." : ""), "Delete folder", danger: true)) return;
        foreach (var h in inside) h.Group = parent;
        AppData.Current.Folders.RemoveAll(f => AppData.InFolder(AppData.NormalizeFolder(f), path));
        Services.FolderDefaults.Deleted(path);
        _folder = parent == "General" ? "" : parent;
        AppData.Save();
        AuditLog.Write("you", path, "folder deleted", $"{inside.Count} host(s) moved to {parent}");
    }

    private List<HostEntry> HostsIn(string key) => AppData.Current.Hosts.Where(h => Matches(h, key)).ToList();

    private static string KeyLabel(string key) =>
        key.Length == 0 ? "all hosts" : key == "★" ? "favourites" : key.StartsWith("lib:") ? "shared " + key[4..]
        : key.StartsWith("p:") ? HostEntry.PlatformLabel(key[2..]) : key.StartsWith("proto:") ? key[6..].ToUpperInvariant()
        : key.StartsWith("t:") ? (key.Length == 2 ? "untagged" : "#" + key[2..]) : key.StartsWith("j:") ? "jump group" : key;

    private IEnumerable<object?> FolderMenu(string folder)
    {
        var hosts = HostsIn(folder);
        if (folder.StartsWith("lib:"))
        {
            yield return Menus.Item("⇄ Open Shared library", () => MainWindow.Instance.Navigate("library"));
            yield return Menus.AsyncItem("⟳ Sync now", async () => { foreach (var c in Library.SharedLibraries.Clients.Where(c => folder == "lib:" + c.State.Name || folder.StartsWith("lib:" + c.State.Name + " / "))) await Library.SharedLibraries.SyncAsync(c); });
            yield return Menus.Item($"⌨ Open terminals for {hosts.Count} host(s)", () => { foreach (var h in hosts.Where(h => h.IsSsh).Take(8)) MainWindow.Instance.OpenTerminal(h); }, enabled: hosts.Count > 0);
            yield return Menus.Item("◉ Check reachability", () => { _folder = folder; Apply(); OnPing(this, new RoutedEventArgs()); }, enabled: hosts.Count > 0);
            yield break;
        }
        bool real = folder.Length > 0 && folder != "★" && IsFolderKey(folder);   // a real folder (not a category node)
        var label = real ? $"“{folder}”" : KeyLabel(folder);
        if (_view == "folders")
        {
            yield return Menus.Item(real ? "New sub-folder…" : "New folder…", () => NewFolder(real ? folder : ""));
            if (real)
            {
                yield return Menus.Item("Rename / move…", () => RenameFolder(folder));
                yield return Menus.Item("Delete folder (keeps hosts)…", () => DeleteFolder(folder));
                yield return Menus.Item("⚙ Folder defaults…", () => FolderDefaultsWindow.Edit(folder), tip: Services.FolderDefaults.Describe(folder, FolderDefaultsWindow.Show) is { Length: > 0 } d ? "Set here or above: " + d : "User, credential, jump host, environment, terminal profile, VPN and tmux for the hosts in this folder and its sub-folders");
                // 0.5.1 (ADR-067): a folder lock guards every host in it and its sub-folders.
                yield return LockUi.Menu("folder:" + folder, "folder " + folder);
            }
        }
        yield return Menus.Item("＋ Add host here…", () => AddHost(real ? folder : null));
        yield return Menus.Item("＋ Add Windows host (Remote Desktop) here…", () => AddHost(real ? folder : null, "rdp"));
        yield return Menus.Item("＋ Add Mac or Linux desktop (VNC) here…", () => AddHost(real ? folder : null, "vnc"));
        yield return Menus.Sep();
        yield return Menus.Item($"⌨ Open terminals for {hosts.Count} host(s)", () =>
        {
            var ssh = hosts.Where(h => h.Protocol != "rdp").ToList();
            if (ssh.Count > 8 && !Dialogs.Confirm("Open terminals", $"Open {ssh.Count} terminal tabs?", "Open")) return;
            foreach (var h in ssh) MainWindow.Instance.OpenTerminal(h);
            if (ssh.Count > 1) MainWindow.Instance.Panes.Tile();
        }, enabled: hosts.Count > 0);
        yield return Menus.Item("◉ Check reachability", () => { _folder = folder; Apply(); OnPing(this, new RoutedEventArgs()); }, enabled: hosts.Count > 0);
        yield return Menus.Item("▤ Health report for this folder", () => MainWindow.Instance.Navigate("reports"), enabled: hosts.Count > 0);
        yield return Menus.Sep();
        yield return Menus.Item($"⇪ Export {label} (encrypted)…", () => ExportHosts(hosts, label.Trim('“', '”')), enabled: hosts.Count > 0);
        yield return Menus.Item(real ? $"⇩ Import into {label}…" : "⇩ Import…", () => ImportFile(real ? folder : null));
        yield return Menus.Sep();
        if (_view == "folders")
        {
            yield return Menus.Item("Expand all", () => SetExpanded(true));
            yield return Menus.Item("Collapse all", () => SetExpanded(false));
        }
        yield return Menus.Sub("View by",
            new[] { ("folders", "Folders"), ("platform", "Platform"), ("protocol", "Protocol"), ("tags", "Tags"), ("jump", "Jump host") }
                .Select(v => { var m = Menus.Item(v.Item2, () => { foreach (ComboBoxItem i in ViewBox.Items) if ((string)i.Tag == v.Item1) ViewBox.SelectedItem = i; }); m.IsCheckable = true; m.IsChecked = _view == v.Item1; return (object?)m; }).ToArray());
    }

    private void SetExpanded(bool on)
    {
        foreach (var f in AppData.Current.AllFolders.Append("")) { if (on) _expanded.Add(f); else _expanded.Remove(f); }
        BuildTree();
    }

    /// <summary>A host's right-click menu (0.2.1: grouped): connect / files / manage first, then notes and incidents,
    /// organising, and copy / export as groups that fold into submenus when the menu is long.</summary>
    private IEnumerable<object?> HostMenu()
    {
        var sel = SelectedAll.ToList();
        if (sel.Count == 0) yield break;
        var first = sel[0];
        var ev = new RoutedEventArgs();
        // Primary: what the toolbar's first buttons do, for the host under the mouse.
        if (first.Protocol == "rdp")
        {
            yield return Menus.Item("🖥 Connect (Remote Desktop)", () => MainWindow.Instance.OpenRdp(first));
            yield return Menus.Item("🖥 Open in mstsc (separate window)", () => MainWindow.Instance.OpenRdp(first, external: true));
        }
        else if (first.IsVnc)
        {
            yield return Menus.Item(first.Vnc.Kind == "mac" ? "🖵 Connect (Mac Screen Sharing)" : "🖵 Connect (VNC desktop)", () => MainWindow.Instance.OpenVnc(first),
                tip: first.Vnc.Through == "ssh" ? "Through an SSH tunnel (encrypted)" : "Directly to the VNC port");
            if (VncConnector.SshHostFor(first) is { } sshTwin) yield return Menus.Item($"⌨ Terminal on {sshTwin.Display}", () => MainWindow.Instance.OpenTerminal(sshTwin));
        }
        else if (first.IsTelnet) yield return Menus.Item("⌁ Connect (Telnet, unencrypted)", () => MainWindow.Instance.OpenTerminal(first));
        // 0.5.9: connecting switches to an open tab of this host; this opens another on purpose (Shift-click does the same).
        if (AppSettings.Current.ReuseOpenTabs && MainWindow.Instance.HasOpenSession(first))
            yield return Menus.Item("⧉ New connection (another tab)", () =>
            {
                if (first.Protocol == "rdp") MainWindow.Instance.OpenRdp(first, forceNew: true);
                else if (first.IsVnc) MainWindow.Instance.OpenVnc(first, forceNew: true);
                else MainWindow.Instance.OpenTerminal(first, forceNew: true);
            }, tip: $"{first.Display} is already open in a tab; Connect switches to it. This opens a second session.");
        else
        {
            yield return Menus.Item("⌨ Connect", () => OnTerminal(this, ev), tip: "SSH terminal (double-click does the same)");
            yield return Menus.Item("⇅ Files", () => OnFiles(this, ev), tip: "SFTP file browser");
            yield return Menus.Item("▣ Manage", () => OnManage(this, ev), tip: "Dashboard, services, processes, logs, packages, Docker, users, firewall, insights");
            yield return Menus.Item("⇆ File commander (this PC ⇄ host)", () => MainWindow.Instance.OpenCommander(first), tip: "Two-pane file manager: copy/move between this PC and the host (SFTP)");
            if (sel.Count == 1) yield return Menus.Group("More tools",
                Menus.Item("✉ Mail server checks (Postfix)…", () => MainWindow.Instance.AddTab("✉ " + first.Display, new MailCheckView(first)), tip: "Postfix status, settings, queue and log over SSH; TLS on 25/465/587, open relay and DNS (MX, SPF, DMARC, DKIM) from this PC"),
                !first.IsAdHoc ? Menus.Item("🖵 Add its desktop (VNC)…", () => AddVncTwin(first), tip: "A VNC host for this machine's desktop, tunnelled through this SSH host") : null);
        }
        if (sel.Count == 1 && PortalUi.HostMenu(first) is { } webUi) yield return webUi;
        yield return Menus.Item("✎ Edit…", () => OnEdit(this, ev), enabled: sel.Count == 1 && !first.IsShared);
        if (sel.Count == 1 && !first.IsShared && !first.IsAdHoc) yield return LockUi.Menu("host:" + first.Id, first.Display);
        // 0.6.14 (ADR-093): sign in from a Bitwarden vault item or Secrets Manager secret.
        if (sel.Count == 1 && !first.IsShared && !first.IsAdHoc && AppSettings.Current.BitwardenEnabled)
            yield return first.BitwardenItem.Length > 0
                ? (object)Menus.Group("Bitwarden",
                    Menus.Item("🔑 Change the Bitwarden sign-in…", () => BitwardenView.LinkFor(first), tip: BitwardenSessions.Describe(first.BitwardenItem)),
                    Menus.Item("Unlink Bitwarden", () => { first.BitwardenItem = ""; AppData.Save(); AuditLog.Write("you", first.Display, "unlinked from Bitwarden"); }, tip: "The host signs in with its own credential again"))
                : Menus.Item("🔑 Sign in with Bitwarden…", () => BitwardenView.LinkFor(first), tip: "Pick a vault login, SSH key or Secrets Manager secret: it's read into memory each time you connect, never saved here");
        yield return Menus.Sep();
        yield return Menus.Group("Notes & incidents",
            sel.Count == 1 && !first.IsAdHoc ? Menus.Item($"✎ Notes for this host ({Notes.NoteStore.ForHost(first.Id).Count})", () => MainWindow.Instance.OpenNotes(first.Id), tip: "The host's notebook: encrypted, searchable, with timestamps and terminal excerpts") : null,
            sel.Count == 1 && !first.IsAdHoc ? Menus.Item($"📄 Docs for this host ({Docs.DocStore.PinnedTo("host:" + first.Id).Count})", () => MainWindow.Instance.OpenDocs("host:" + first.Id, first.Display), tip: "Markdown documents pinned to this host (or its folder)") : null,
            sel.Count == 1 ? IncidentUi.HostMenu(first).ToArray() : null,
            sel.Count > 1 && sel.All(h => !h.IsAdHoc) ? Menus.Item($"⚑ New incident for these {sel.Count} hosts…", () =>
            {
                if (IncidentUi.NewIncident(first, $"{sel.Count} hosts: ") is not { } inc) return;
                foreach (var h in sel.Skip(1)) if (!inc.HostIds.Contains(h.Id)) inc.HostIds.Add(h.Id);
                Incidents.IncidentStore.Save(inc);
                MainWindow.Instance.OpenIncidents(inc);
            }) : null,
            sel.Count == 1 ? AddonUi.HostMenu(first) : null,
            Menus.AssistItem(sel.Count == 1 ? "✦ Review this host's setup with AI" : $"✦ Review {sel.Count} hosts' setup with AI",
                a => a.AskAsync(sel.Count == 1 ? $"host {first.Display}" : $"{sel.Count} hosts",
                    string.Join("\n", sel.Take(30).Select(SafeMetadata.Host)),
                    "Review these saved connection settings (no secrets are included): security of the authentication setup, jump hosts, keep-alive, organisation (folders/tags/platform), and anything inconsistent or risky. Suggest concrete improvements.")));
        // Folders, environment and export belong to the library's copy of a shared host, not this PC.
        bool local = sel.All(h => !h.IsShared);
        yield return Menus.Group("Organise",
            local ? Menus.Sub("Move to folder",
                AppData.Current.AllFolders.Select(f => (object?)Menus.Item(f, () => MoveHosts(sel, f), enabled: sel.Any(h => h.Group != f)))
                    .Append(Menus.Sep()).Append(Menus.Item("New folder…", () =>
                    {
                        if (Dialogs.Prompt("Move to new folder", "Folder path (use / for nested):", _folder is "" or "★" ? "" : _folder + "/") is { Length: > 0 } p && AppData.NormalizeFolder(p) is { Length: > 0 } np)
                        { if (!AppData.Current.Folders.Contains(np, StringComparer.OrdinalIgnoreCase)) AppData.Current.Folders.Add(np); MoveHosts(sel, np); }
                    })).ToArray()) : null,
            local ? Menus.Sub("Environment",
                new[] { "" }.Concat(Envs.All).Select(e => (object?)Menus.Item(e.Length > 0 ? e : "(not set)", () => SetEnvironment(sel, e),
                    enabled: sel.Any(h => Envs.Normalize(h.Environment) != e))).ToArray()) : null,
            Menus.Item(first.Favorite ? "☆ Remove from favourites" : "★ Add to favourites", () => OnFavourite(this, ev)),
            SharedLibraryUi.HostMenu(sel, Window.GetWindow(this)).ToArray());
        yield return Menus.Group("Copy & export",
            Menus.Item("Copy user@host", () => Menus.Clip(string.Join("\n", sel.Select(h => $"{h.Username}@{h.Address}{(h.Port != 22 ? ":" + h.Port : "")}")))),
            Menus.Item("Copy ssh command", () => Menus.Clip(string.Join("\n", sel.Select(h => $"ssh {(h.Port != 22 ? $"-p {h.Port} " : "")}{h.Username}@{h.Address}"))), enabled: first.IsSsh),
            local ? Menus.Item($"⇪ Export {sel.Count} host(s) (encrypted)…", () => ExportHosts(sel, sel.Count == 1 ? first.Display : $"{sel.Count} hosts")) : null);
        if (first.Protocol == "rdp")
        {
            var shot = RdpLauncher.ScreenshotMenu(first, Window.GetWindow(this));
            if (!RdpLauncher.IsOpen(first)) { shot.IsEnabled = false; shot.ToolTip = "Connect first — the Remote Desktop window must be open (and not minimised)."; }
            yield return Menus.Group("Remote Desktop", Menus.Item("RDP server keepalive commands…", () => RdpLauncher.ShowServerKeepAlive(first)), shot);
        }
    }

    /// <summary>0.5.1 #19: a VNC host for an SSH host's desktop — same address, through that SSH host, editor opens to check it.</summary>
    private void AddVncTwin(HostEntry ssh)
    {
        if (!Restrictions.Check("host.add")) return;
        var mac = ssh.EffectivePlatform == "macos";
        var seed = new HostEntry
        {
            Name = ssh.Display + " desktop", Address = ssh.Address, Port = VncConnector.DefaultPort, Protocol = "vnc", Group = ssh.Group,
            Environment = ssh.Environment, Username = mac ? ssh.Username : "", CredentialId = mac ? ssh.CredentialId : null, Auth = AuthMethod.Password,
            Vnc = new VncSettings { Through = "ssh", SshHostId = ssh.Id, Kind = mac ? "mac" : "vnc" },
        };
        var w = new HostEditWindow(seed, seedIsNew: true) { Owner = Window.GetWindow(this) };
        if (w.ShowDialog() != true || w.Result is not { } h) return;
        AppData.Current.Hosts.Add(h);
        AppData.Save();
        AuditLog.Write("you", h.Display, "added host", "VNC desktop of " + ssh.Display);
    }

    private void OnFilter(object sender, RoutedEventArgs e) => Apply();

    private void Apply()
    {
        if (List == null) return;
        var q = Search.Text.Trim();
        var shown = _rows.Where(r => Matches(r.Host, _folder) &&
            (q.Length == 0 || $"{r.Host.Display} {r.Host.Address} {r.Host.Username} {r.Host.Tags} {r.Host.Notes} {r.Host.Group}".Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();
        List.ItemsSource = shown;
        CountText.Text = (shown.Count == _rows.Count ? $"{_rows.Count} saved" : $"{shown.Count} of {_rows.Count}") + (_folder.Length > 0 && _folder != "★" ? $" · {KeyLabel(_folder)}" : "");
    }

    // ---- export / import ------------------------------------------------------------------------------------------

    private void OnExport(object sender, RoutedEventArgs e)
    {
        var sel = SelectedAll.ToList();
        if (sel.Count > 1) ExportHosts(sel, $"{sel.Count} hosts");
        else ExportHosts(HostsIn(_folder), KeyLabel(_folder));
    }

    private void OnImport(object sender, RoutedEventArgs e) => ImportFile(_folder is "" or "★" || !IsFolderKey(_folder) ? null : _folder);

    private void ExportHosts(List<HostEntry> hosts, string what) => AfterUnlock(hosts, "export", () => BackupDialogs.ExportSelection(Window.GetWindow(this), hosts, what));

    private void ImportFile(string? intoFolder) { if (Restrictions.Check("host.add")) BackupDialogs.Import(Window.GetWindow(this), intoFolder); }

    private void OnLocksChanged() => Dispatcher.BeginInvoke(Reload);

    /// <summary>Opens the locks guarding every host in <paramref name="hosts"/> (asking for each closed one). True when all
    /// are open now (0.5.1, ADR-067: export, copy, delete and edit of locked hosts).</summary>
    private static async Task<bool> UnlockAllAsync(IEnumerable<HostEntry> hosts, string what)
    {
        foreach (var h in hosts.Where(h => !h.IsAdHoc && !h.IsShared))
            if (!await LockGate.EnsureAsync("host:" + h.Id, what + " " + h.Display)) return false;
        return true;
    }

    /// <summary>Runs <paramref name="action"/> once every host in the list is unlocked (at once when none is locked).</summary>
    private void AfterUnlock(IReadOnlyList<HostEntry> hosts, string what, Action action)
    {
        if (hosts.All(h => h.IsAdHoc || h.IsShared || Locks.IsOpen("host:" + h.Id))) { action(); return; }
        _ = Dispatcher.InvokeAsync(async () => { if (await UnlockAllAsync(hosts, what)) action(); });
    }

    private void AddHost(string? folder, string protocol = "ssh") => HostActions.Add(Window.GetWindow(this), folder, protocol);

    private HostEntry? Selected => (List.SelectedItem as HostRow)?.Host;
    private IEnumerable<HostEntry> SelectedAll => List.SelectedItems.OfType<HostRow>().Select(r => r.Host);

    private HostEntry? Need()
    {
        if (Selected is { } h) return h;
        Dialogs.Info("Hosts", "Select a host first.");
        return null;
    }

    private void OnAdd(object sender, RoutedEventArgs e) => AddHost(_folder is "" or "★" || !IsFolderKey(_folder) ? null : _folder);

    private void OnEdit(object sender, RoutedEventArgs e)
    {
        if (Need() is not { } h) return;
        HostActions.Edit(Window.GetWindow(this), h);
    }

    private void OnDuplicate(object sender, RoutedEventArgs e)
    {
        if (Need() is not { } h) return;
        if (h.IsShared) { SharedLibraryUi.CopyHostsToMine(new() { h }); return; }
        if (!Restrictions.Check("host.add")) return;
        if (!Locks.IsOpen("host:" + h.Id)) { AfterUnlock(new[] { h }, "duplicate", () => OnDuplicate(sender, e)); return; }
        var copy = h.Clone();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = h.Display + " (copy)";
        copy.LastConnected = null;
        Unlink(copy);   // a copy is not that cloud instance
        AppData.Current.Hosts.Add(copy);
        AppData.Save();
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        var hosts = SelectedAll.ToList();
        if (hosts.Count == 0) return;
        if (hosts.Any(h => h.IsShared))
        {
            if (hosts.All(h => h.IsShared)) SharedLibraryUi.DeleteShared(hosts.Select(h => h.Id).ToList(), "host");
            else Dialogs.Info("Delete hosts", "The selection mixes your own hosts and shared library hosts. Delete them separately: shared hosts are deleted for every member of the library.");
            return;
        }
        if (!Restrictions.Check("host.delete")) return;
        if (!hosts.All(h => Locks.IsOpen("host:" + h.Id))) { AfterUnlock(hosts, "delete", () => OnDelete(sender, e)); return; }
        if (!Dialogs.Confirm("Delete hosts", $"Remove {string.Join(", ", hosts.Select(h => h.Display))} from your saved hosts?\n\nCredentials in the vault are kept.", "Delete", danger: true)) return;
        RemoveHosts(hosts, "deleted host");
    }

    /// <summary>Removes saved hosts (already confirmed): their locks and pooled connections go, jump-host links are cleared.</summary>
    public static void RemoveHosts(IEnumerable<HostEntry> hosts, string audit)
    {
        foreach (var h in hosts.ToList())
        {
            AppData.Current.Hosts.Remove(h); Locks.Forget("host:" + h.Id);
            SshPool.Drop(h.Id);
            foreach (var other in AppData.Current.Hosts.Where(o => o.JumpHostId == h.Id)) other.JumpHostId = null;
            AuditLog.Write("you", h.Display, audit);
        }
        AppData.Save();
    }

    private void OnTerminal(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && sender == List && Menus.Ancestor<ListViewItem>(d) == null) return;   // double-click on empty space
        foreach (var h in SelectedAll.Take(8))
            if (h.Protocol == "rdp") MainWindow.Instance.OpenRdp(h); else MainWindow.Instance.OpenTerminal(h);
    }
    private void OnFiles(object sender, RoutedEventArgs e) { if (Need() is { } h) MainWindow.Instance.OpenFiles(h); }
    private void OnManage(object sender, RoutedEventArgs e) { if (Need() is { } h) MainWindow.Instance.OpenServer(h); }
    private void OnExternal(object sender, RoutedEventArgs e) { if (Need() is { } h && MainWindow.Instance.HostGate(h, "connect to", () => ExternalSsh.Launch(h))) ExternalSsh.Launch(h); }

    /// <summary>Sets the environment of several hosts at once (ADR-017). Taking PRODUCTION away asks first: it removes the safeguards.</summary>
    public static bool SetEnvironment(IReadOnlyCollection<HostEntry> hosts, string env)
    {
        env = Envs.Normalize(env);
        var changing = hosts.Where(h => Envs.Normalize(h.Environment) != env).ToList();
        if (changing.Count == 0) return false;
        if (!Restrictions.Check("host.edit")) return false;
        if (changing.FirstOrDefault(h => !h.IsAdHoc && !Locks.IsOpen("host:" + h.Id)) is { } locked)
        {
            Dialogs.Info("Environment", $"“{locked.Display}” is locked. Unlock it first (right-click → 🔒 Lock → Unlock for now).");
            return false;
        }
        var losing = changing.Where(h => Envs.IsProduction(h.Environment)).ToList();
        if (losing.Count > 0 && !Dialogs.Confirm("Remove PRODUCTION label", $"{string.Join(", ", losing.Select(h => h.Display))} will no longer be marked PRODUCTION, so reboots, deletes and multi-host runs stop asking for the host's name. Continue?", "Change", danger: true))
            return false;
        foreach (var h in changing)
        {
            AuditLog.Write("you", h.Display, "environment", $"{(h.Environment.Length > 0 ? h.Environment : "(not set)")} → {(env.Length > 0 ? env : "(not set)")}");
            h.Environment = env;
        }
        AppData.Save();
        Envs.RaiseChanged();
        return true;
    }

    private void OnFavourite(object sender, RoutedEventArgs e)
    {
        foreach (var h in SelectedAll.Where(h => !h.IsShared)) h.Favorite = !h.Favorite;
        AppData.Save();
    }

    private void OnCopyAddress(object sender, RoutedEventArgs e) { if (Selected is { } h) Clipboard.SetText(h.Port == 22 ? h.Address : $"{h.Address}:{h.Port}"); }

    private async void OnInstallKey(object sender, RoutedEventArgs e)
    {
        if (Need() is not { } h) return;
        var keys = AppData.Current.Credentials.Where(c => c.Kind == CredentialKind.PrivateKey).ToList();
        if (keys.Count == 0) { Dialogs.Info("Install key", "There are no keys in the vault. Generate or import one under Credentials."); return; }
        var key = Dialogs.Pick("Install key", $"Add which public key to {h.Endpoint}'s ~/.ssh/authorized_keys?\n(Connects with the host's current sign-in.)", keys, k => $"{k.Name}   {k.KeyType}   {k.Fingerprint}");
        if (key == null) return;
        try
        {
            var r = await KeyTools.InstallPublicKeyAsync(h, key);
            if (!r.Ok) { Dialogs.Error("Install key", r.Combined); return; }
            AuditLog.Write("you", h.Display, $"installed public key {key.Name} {key.Fingerprint}");
            if (h.Auth != AuthMethod.PrivateKey && Dialogs.Confirm("Key installed", $"The key is in authorized_keys.\n\nSwitch {h.Display} to sign in with “{key.Name}” from now on?", "Switch to key"))
            {
                h.Auth = AuthMethod.PrivateKey;
                if (h.CredentialId != null && AppData.Current.FindCredential(h.CredentialId) is { Kind: CredentialKind.Password } pw) h.SudoCredentialId ??= pw.Id;
                h.CredentialId = key.Id;
                SshPool.Drop(h.Id);
                AppData.Save();
            }
            else Dialogs.Info("Install key", "Key installed.");
        }
        catch (Exception ex) { Dialogs.Error("Install key", ex.Message); }
    }

    private async void OnPing(object sender, RoutedEventArgs e)
    {
        var rows = (List.ItemsSource as IEnumerable<HostRow>)?.ToList() ?? new();
        await Task.WhenAll(rows.Select(async r =>
        {
            r.Reach = "checking…";
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (Offline.On && !(await Offline.DecideAsync(r.Host.Address)).Allowed) { r.Reach = "⛔ offline mode"; return; }
                using var tcp = new TcpClient();
                using var cts = new CancellationTokenSource(3000);
                await tcp.ConnectAsync(r.Host.Address, r.Host.Port, cts.Token);
                r.Reach = $"● open  {sw.ElapsedMilliseconds} ms";
            }
            catch { r.Reach = r.Host.JumpHostId != null ? "via jump host" : "✕ unreachable"; }
        }));
    }

    private async Task ImportFromCloudAsync(CloudProvider p)
    {
        var title = $"Import from {p.Title}";
        ICloudApi? api;
        try { api = p.Active; }
        catch (Locks.LockedException ex) { Dialogs.Info(title, ex.Message); return; }   // 0.5.1 #10: a padlocked account
        if (api == null) { Dialogs.Info(title, $"Add a {p.Title} account in Settings → {p.Title} accounts first."); return; }
        try
        {
            var list = await p.ListInstancesAsync(api);
            var keys = AppData.Current.Credentials.Where(c => c.Kind == CredentialKind.PrivateKey).ToList();
            Credential? key = keys.Count == 0 ? null
                : Dialogs.Pick(title, "Which vault key should the imported hosts use? (Cancel = ask for password)", keys, k => $"{k.Name}   {k.KeyType}");
            var r = Reconcile(p, api.AccountId, list, key);
            AppData.Save();
            AuditLog.Write("you", $"{p.Id}:{api.AccountName}", $"imported {r.Added} instance(s) as hosts", $"{r.Updated} updated, {r.Gone.Count} linked host(s) without an instance");
            var msg = $"{r.Added} new host(s) added, {r.Updated} existing host(s) updated; {list.Count - r.Added - r.Updated} without a public IPv4.";
            if (r.Gone.Count > 0)
            {
                msg += $"\n\n{r.Gone.Count} saved host(s) are linked to {p.Title} instances that no longer exist on {api.AccountName}:\n" + string.Join("\n", r.Gone.Take(15).Select(h => "  " + h.Display));
                if (Dialogs.Confirm(title, msg + "\n\nRemove the link from these hosts? (The hosts themselves are kept.)", "Remove the links"))
                {
                    foreach (var h in r.Gone) Unlink(h);
                    AppData.Save();
                    AuditLog.Write("you", $"{p.Id}:{api.AccountName}", $"unlinked {r.Gone.Count} host(s) from removed instances");
                }
                return;
            }
            Dialogs.Info(title, msg);
        }
        catch (Exception ex) { Dialogs.Error(title, ex.Message); }
    }

    public sealed record ReconcileResult(int Added, int Updated, List<HostEntry> Gone);

    /// <summary>Discovery against the saved hosts (ADR-020): new instances become hosts, linked hosts follow a changed
    /// address, and hosts linked to an instance of this account that no longer exists are reported (never deleted).</summary>
    public static ReconcileResult Reconcile(CloudProvider p, string accountId, IReadOnlyList<CloudInstance> list, Credential? key)
    {
        var sync = SyncLinked(p, accountId, list);
        int added = 0, updated = sync.Followed + sync.Relinked.Count;
        var hosts = AppData.Current.Hosts;
        foreach (var i in list)
        {
            if (!HasAddress(i) || hosts.Any(h => CloudProviders.ForHost(h) is { } l && l.Provider == p && l.InstanceId == i.Id)) continue;
            var existing = hosts.FirstOrDefault(h => CloudProviders.ForHost(h) == null && h.Address == i.Ipv4);
            if (existing != null) { Link(existing, i); updated++; continue; }
            hosts.Add(FromCloud(i, key));
            added++;
        }
        return new ReconcileResult(added, updated, sync.Gone);
    }

    /// <summary>What <see cref="SyncLinked"/> changed: hosts that followed a new address (with the old one), hosts relinked
    /// to a replacement server of the same name, hosts whose server is gone, and hosts left alone because they are locked.</summary>
    public sealed record CloudSyncResult(int Followed, List<(HostEntry Host, string Old)> Moved, List<HostEntry> Relinked, List<HostEntry> Gone, List<HostEntry> Locked)
    {
        public bool Changed => Moved.Count > 0 || Relinked.Count > 0;
    }

    /// <summary>Linked hosts follow their cloud servers (0.6.24): a changed public IPv4 is copied to the host, and a host whose
    /// server was destroyed is relinked to the one new, unlinked server of the same name (a rebuilt server). Hosts whose server
    /// is gone stay saved and are reported. <paramref name="list"/> must be the account's complete instance list. Shared
    /// library hosts, padlocked hosts and a policy that forbids host edits are never changed.</summary>
    public static CloudSyncResult SyncLinked(CloudProvider p, string accountId, IReadOnlyList<CloudInstance> list)
    {
        var hosts = AppData.Current.Hosts;
        var byId = list.GroupBy(i => i.Id).ToDictionary(g => g.Key, g => g.First());
        var linked = hosts.Select(CloudProviders.ForHost).Where(l => l is { } x && x.Provider == p).Select(l => l!.Value.InstanceId).ToHashSet();
        var mine = hosts.Where(h => !h.IsShared && CloudProviders.ForHost(h) is { } l && l.Provider == p && (l.AccountId == null || l.AccountId == accountId)).ToList();
        var r = new CloudSyncResult(0, new(), new(), new(), new());
        var canEdit = Restrictions.Allowed("host.edit");
        int followed = 0;
        foreach (var h in mine)
        {
            var id = CloudProviders.ForHost(h)!.Value.InstanceId;
            if (byId.TryGetValue(id, out var i))
            {
                followed++;
                if (!HasAddress(i) || h.Address == i.Ipv4) continue;
                if (!canEdit || !Locks.IsOpen("host:" + h.Id)) { r.Locked.Add(h); continue; }
                r.Moved.Add((h, h.Address)); h.Address = i.Ipv4; SshPool.Drop(h.Id);
                continue;
            }
            var twins = list.Where(x => !linked.Contains(x.Id) && x.Name.Length > 0 && string.Equals(x.Name, h.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (twins.Count != 1) { r.Gone.Add(h); continue; }
            if (!canEdit || !Locks.IsOpen("host:" + h.Id)) { r.Locked.Add(h); r.Gone.Add(h); continue; }
            var twin = twins[0];
            var old = h.Address;
            Link(h, twin); linked.Add(twin.Id);
            if (HasAddress(twin) && h.Address != twin.Ipv4) { h.Address = twin.Ipv4; SshPool.Drop(h.Id); }
            r.Relinked.Add(h);
            if (h.Address != old) r.Moved.Add((h, old));
        }
        return r with { Followed = followed };
    }

    /// <summary><see cref="SyncLinked"/>, saved and written to the audit log (the cloud screens and the dashboard's cloud card
    /// call it whenever they load an account's complete instance list).</summary>
    public static CloudSyncResult FollowCloud(CloudProvider p, string accountId, IReadOnlyList<CloudInstance> list)
    {
        if (Profiles.ReadOnly) return new CloudSyncResult(0, new(), new(), new(), new());   // a viewer can't save hosts
        var r = SyncLinked(p, accountId, list);
        if (!r.Changed) return r;
        AppData.Save();
        foreach (var (h, old) in r.Moved) AuditLog.Write("app", h.Display, $"host address followed {p.Title}", $"{old} → {h.Address}");
        foreach (var h in r.Relinked) AuditLog.Write("app", h.Display, $"host relinked to a rebuilt {p.Title} server of the same name", h.Address);
        return r;
    }

    /// <summary>A usable public IPv4 (Vultr shows 0.0.0.0 while a server is still being built).</summary>
    private static bool HasAddress(CloudInstance i) => i.Ipv4.Length > 0 && i.Ipv4 != "0.0.0.0";

    private static void Link(HostEntry h, CloudInstance i)
    {
        h.CloudProvider = i.Provider; h.CloudInstanceId = i.Id; h.CloudAccountId = i.AccountId;
        if (i.Provider == "vultr") { h.VultrInstanceId = null; h.VultrAccountId = null; }   // the neutral link replaces the old Vultr-only one
    }

    public static void Unlink(HostEntry h) { h.CloudProvider = h.CloudInstanceId = h.CloudAccountId = null; h.VultrInstanceId = h.VultrAccountId = null; }

    /// <summary>A saved host for a cloud instance (sign-in user from the provider: root, or Contabo's default user).</summary>
    public static HostEntry FromCloud(CloudInstance i, Credential? key)
    {
        var h = new HostEntry
        {
            Name = i.Name.Length > 0 ? i.Name : i.Ipv4,
            Group = $"{CloudProviders.Find(i.Provider)?.Title ?? i.Provider} {i.Region.ToUpperInvariant()}".Trim(),
            Address = i.Ipv4, Username = i.DefaultUser is { Length: > 0 } u && u != "administrator" ? u : "root",
            Auth = key != null ? AuthMethod.PrivateKey : AuthMethod.Password, CredentialId = key?.Id,
            Tags = J.Str(i.Raw, "tags"),
            Notes = $"{i.Plan} · {i.Os}".Trim(' ', '·'),
        };
        Link(h, i);
        return h;
    }

    /// <summary>Reads Host blocks from %USERPROFILE%\.ssh\config (HostName, User, Port, IdentityFile, ProxyJump).</summary>
    private void OnImportSshConfig(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config");
        if (!File.Exists(path)) { Dialogs.Info("Import", "No ~/.ssh/config file was found."); return; }
        var entries = new List<(HostEntry Host, string? Identity, string? Jump)>();
        HostEntry? cur = null; string? identity = null, jump = null;
        void Commit() { if (cur != null && !cur.Name.Contains('*')) entries.Add((cur, identity, jump)); }
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var parts = line.Split(new[] { ' ', '\t', '=' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            var k = parts[0].ToLowerInvariant(); var v = parts[1].Trim().Trim('"');
            switch (k)
            {
                case "host": Commit(); cur = new HostEntry { Name = v.Split(' ')[0], Address = v.Split(' ')[0], Group = "ssh config", Username = Environment.UserName.ToLowerInvariant() }; identity = jump = null; break;
                case "hostname" when cur != null: cur.Address = v; break;
                case "user" when cur != null: cur.Username = v; break;
                case "port" when cur != null && int.TryParse(v, out var p): cur.Port = p; break;
                case "identityfile" when cur != null: identity = v.Replace("~", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)); break;
                case "proxyjump" when cur != null: jump = v; break;
            }
        }
        Commit();
        int added = 0, keys = 0;
        foreach (var (h, id, j) in entries)
        {
            if (AppData.Current.Hosts.Any(x => x.Address == h.Address && x.Port == h.Port && x.Username == h.Username)) continue;
            h.Auth = AuthMethod.Password;
            if (id != null && File.Exists(id))
            {
                var text = File.ReadAllText(id);
                var cred = AppData.Current.Credentials.FirstOrDefault(c => c.Secret.Trim() == text.Trim());
                if (cred == null)
                {
                    cred = new Credential { Name = Path.GetFileName(id), Kind = CredentialKind.PrivateKey, Secret = text, Notes = "imported from " + id };
                    try { KeyTools.Describe(cred); } catch { cred.Notes += " (passphrase needed — edit to add it)"; }
                    AppData.Current.Credentials.Add(cred);
                    keys++;
                }
                h.Auth = AuthMethod.PrivateKey; h.CredentialId = cred.Id;
            }
            if (j != null) h.Notes = "ProxyJump " + j;
            AppData.Current.Hosts.Add(h);
            added++;
        }
        foreach (var (h, _, j) in entries)
            if (j != null && AppData.Current.FindHostByName(j.Split('@').Last().Split(':')[0]) is { } jh && jh.Id != h.Id) h.JumpHostId = jh.Id;
        AppData.Save();
        Dialogs.Info("Import ~/.ssh/config", $"{added} host(s) and {keys} key(s) imported into the encrypted store." + (keys > 0 ? "\n\nThe key files on disk are untouched; you can delete them if you only want the encrypted copies." : ""));
    }
}
