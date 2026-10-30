using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DcxManager.Services;

namespace DcxManager.Views;

/// <summary>A tab strip + content area. Tabs can be dragged between panes and reordered.</summary>
public sealed class DocPane : TabControl
{
    public PaneFrame Frame { get; }

    public DocPane(PaneFrame frame)
    {
        Frame = frame;
        Style = (Style)Application.Current.Resources["DocTabs"];
        AllowDrop = true;
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (GroupChipOf(e.OriginalSource) is not { } tab) return;
            e.Handled = true;   // the chip is a button of its own: no select, no drag
            if (TabGroups.GetStacked(tab)) ShowStack(tab);
            else TabGroups.SetStacked(TabGroups.GetName(tab), true);
        };
        PreviewMouseWheel += (_, e) =>
        {
            if (GroupChipOf(e.OriginalSource) is { } face && TabGroups.GetStacked(face))
            {
                // Over a stacked group's chip the wheel steps through its tabs.
                var run = TabGroups.StackOf(face);
                int i = run.ToList().IndexOf(face);
                Frame.Host.Select(run[((i + (e.Delta < 0 ? 1 : -1)) % run.Count + run.Count) % run.Count]);
                e.Handled = true;
                return;
            }
            if (e.OriginalSource is not DependencyObject d || Menus.Ancestor<TabItem>(d) == null) return;
            if (Template?.FindName("TabScroll", this) is ScrollViewer sv) { sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta); e.Handled = true; }
        };
        DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(typeof(TabItem)) ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; };
        Drop += (_, e) =>
        {
            if (e.Data.GetData(typeof(TabItem)) is not TabItem tab) return;
            var target = e.OriginalSource is DependencyObject d ? Menus.Ancestor<TabItem>(d) : null;
            Frame.Host.MoveTab(tab, this, target != null && Items.Contains(target) ? Items.IndexOf(target) : -1);
            e.Handled = true;
        };
    }

    /// <summary>A tab added, removed or moved: the group names print once per run again (0.6.29).</summary>
    protected override void OnItemsChanged(System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        base.OnItemsChanged(e);
        TabGroups.RefreshLeads(this);
    }

    /// <summary>The tab in front becomes its stacked group's face (0.6.31).</summary>
    protected override void OnSelectionChanged(SelectionChangedEventArgs e)
    {
        base.OnSelectionChanged(e);
        TabGroups.RefreshLeads(this);
    }

    /// <summary>The tab whose group chip <paramref name="source"/> is in (0.6.31), if it is.</summary>
    private static TabItem? GroupChipOf(object source)
    {
        for (var d = source as DependencyObject; d != null && d is not TabItem; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is Border { Tag: "groupchip" }) return Menus.Ancestor<TabItem>(d);
        return null;
    }

    /// <summary>A stacked group's tabs, to pick one (0.6.31): the one in front ticked, the rest by title.</summary>
    internal void ShowStack(TabItem face)
    {
        var name = TabGroups.GetName(face);
        var run = TabGroups.StackOf(face);
        var items = run.Select(t => (object?)Menus.Item((t == face ? "✓ " : "") + (t.Header as string ?? t.Header?.ToString() ?? "tab"), () => Frame.Host.Select(t)))
            .Append(Menus.Sep())
            .Append(Menus.Item($"Spread out “{name}”", () => TabGroups.SetStacked(name, false)))
            .ToArray();
        if (face.Template?.FindName("grp", face) is FrameworkElement chip) Menus.ShowBelow(chip, items);
    }

    /// <summary>Wires the tab-strip tools from the DocTabs template (they live in the template, so they're found here).</summary>
    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (GetTemplateChild("ShotBtn") is Button shot)
        {
            shot.Click += (_, _) => { if (SelectedItem is TabItem t) _ = MainWindow.Instance.ScreenshotTabAsync(t); };
            Menus.Attach(shot, () => SelectedItem is TabItem t ? MainWindow.Instance.ScreenshotMenuItems(t) : new List<object?>());
        }
        if (GetTemplateChild("LayoutBtn") is Button layout)
        {
            layout.Click += (_, _) =>
            {
                var h = Frame.Host;
                var t = SelectedItem as TabItem;
                bool many = Items.Count > 1;
                bool inWindow = h.FloatOf(this) != null;
                int panesHere = h.FloatOf(this) is { } fw ? PaneHost.PanesOf(fw).Count() : h.PaneCount;
                Menus.ShowBelow(layout, new object?[]
                {
                    Menus.Item("Split right", () => { if (t != null) h.Split(t, Orientation.Horizontal); }, "Ctrl+Alt+→", t != null && many),
                    Menus.Item("Split down", () => { if (t != null) h.Split(t, Orientation.Vertical); }, "Ctrl+Alt+↓", t != null && many),
                    Menus.Item("Move to next pane", () => { if (t != null) h.MoveToNextPane(t); }, enabled: t != null && panesHere > 1),
                    Menus.Sep(),
                    Menus.Item("Tile all tabs", h.Tile, "Ctrl+Alt+T", !inWindow),
                    Menus.Item("Merge all panes", h.MergeAll, "Ctrl+Alt+U", !inWindow && h.PaneCount > 1),
                    Menus.Sep(),
                }.Concat(h.WindowMenuItems(t)).ToArray());
            };
        }
    }
}

/// <summary>Border around a pane; shows which pane is active when there are several.</summary>
public sealed class PaneFrame : Border
{
    public DocPane Pane { get; }
    public PaneHost Host { get; }

    public PaneFrame(PaneHost host)
    {
        Host = host;
        Pane = new DocPane(this);
        Child = Pane;
        BorderThickness = new Thickness(0, 1, 0, 0);
        BorderBrush = Brushes.Transparent;
    }

    public void SetActiveLook(bool active, bool many) =>
        SetResourceReference(BorderBrushProperty, active && many ? "Accent" : "Bg");
}

/// <summary>Two panes (or nested splits) with a draggable splitter.</summary>
public sealed class SplitNode : Grid
{
    public Orientation Orientation { get; }

    public SplitNode(Orientation o, double ratio = 0.5)
    {
        Orientation = o;
        ratio = Math.Clamp(ratio, 0.1, 0.9);
        var splitter = new GridSplitter { ResizeBehavior = GridResizeBehavior.PreviousAndNext, ToolTip = "Drag to resize · double-click to even out", Focusable = false };
        if (o == Orientation.Horizontal)
        {
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ratio, GridUnitType.Star), MinWidth = 160 });
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - ratio, GridUnitType.Star), MinWidth = 160 });
            splitter.Width = 5; splitter.HorizontalAlignment = HorizontalAlignment.Stretch;
            SetColumn(splitter, 1);
        }
        else
        {
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(ratio, GridUnitType.Star), MinHeight = 90 });
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(1 - ratio, GridUnitType.Star), MinHeight = 90 });
            splitter.Height = 5; splitter.VerticalAlignment = VerticalAlignment.Stretch; splitter.HorizontalAlignment = HorizontalAlignment.Stretch;
            SetRow(splitter, 1);
        }
        splitter.MouseDoubleClick += (_, _) => Even();
        Children.Add(splitter);
    }

    public void Even()
    {
        if (Orientation == Orientation.Horizontal) { ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star); ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star); }
        else { RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star); RowDefinitions[2].Height = new GridLength(1, GridUnitType.Star); }
    }

    public FrameworkElement? First => Children.OfType<FrameworkElement>().FirstOrDefault(c => c is not GridSplitter && Slot(c) == 0);
    public FrameworkElement? Second => Children.OfType<FrameworkElement>().FirstOrDefault(c => c is not GridSplitter && Slot(c) == 2);

    private int Slot(UIElement c) => Orientation == Orientation.Horizontal ? GetColumn(c) : GetRow(c);

    public void Put(int slot, FrameworkElement el)
    {
        if (Orientation == Orientation.Horizontal) SetColumn(el, slot); else SetRow(el, slot);
        Children.Add(el);
    }

    public void Replace(FrameworkElement old, FrameworkElement @new)
    {
        int slot = Slot(old);
        Children.Remove(old);
        Put(slot, @new);
    }
}

/// <summary>
/// The document area: a tree of panes that can be split right/down, tiled into a grid, merged, and popped out into
/// separate windows. Views (terminals, files, server views…) move between panes without being recreated, so live
/// sessions survive any layout change.
/// </summary>
public sealed class PaneHost : Border
{
    private readonly List<FloatWindow> _floats = new();
    private readonly HashSet<TabItem> _wired = new();
    private bool _shuttingDown;

    public DocPane ActivePane { get; private set; }
    public event Action<TabItem?>? ActiveTabChanged;
    /// <summary>Supplies the right-click menu for a tab header.</summary>
    public Func<TabItem, IEnumerable<object?>>? TabMenu { get; set; }
    /// <summary>Called when a tab's close is requested from inside the host (middle-click, empty float window…).</summary>
    public Action<TabItem>? CloseRequested { get; set; }

    public PaneHost()
    {
        var root = NewFrame();
        Child = root;
        ActivePane = root.Pane;
        _mainLast = root.Pane;
    }

    // ---- queries --------------------------------------------------------------------------------------------------

    public IEnumerable<DocPane> MainPanes => Walk(Child as FrameworkElement);
    /// <summary>Every pane: the main window's, then each extra window's (0.6.4: a window can be split too).</summary>
    public IEnumerable<DocPane> Panes => MainPanes.Concat(_floats.SelectMany(PanesOf)).Concat(EmbeddedPanes);
    /// <summary>Every document tab (0.6.7: not the Hosts tab's own host-list tab, which is part of that page).</summary>
    public IEnumerable<TabItem> AllTabs => MainPanes.Concat(_floats.SelectMany(PanesOf)).SelectMany(p => p.Items.OfType<TabItem>())
        .Concat(EmbeddedPanes.SelectMany(p => p.Items.OfType<TabItem>().Where(t => (t.Tag as string) != "fixed"))).ToList();
    public TabItem? ActiveTab => ActivePane.SelectedItem as TabItem;
    public int PaneCount => MainPanes.Count();
    public bool Contains(TabItem t) => PaneOf(t) != null;
    public DocPane? PaneOf(TabItem t) => Panes.FirstOrDefault(p => p.Items.Contains(t));
    public IReadOnlyList<FloatWindow> Floats => _floats;
    /// <summary>The panes of one extra window.</summary>
    public static IEnumerable<DocPane> PanesOf(FloatWindow w) => Walk(w.DocRoot.Child as FrameworkElement);
    /// <summary>The extra window a pane is in, or null for the main window.</summary>
    public FloatWindow? FloatOf(DocPane? p) => p == null ? null : _floats.FirstOrDefault(f => PanesOf(f).Contains(p));
    /// <summary>The extra window a tab is in, or null.</summary>
    public FloatWindow? WindowOf(TabItem t) => FloatOf(PaneOf(t));

    private static IEnumerable<DocPane> Walk(FrameworkElement? n) => FramesIn(n).Select(f => f.Pane);

    internal static IEnumerable<PaneFrame> FramesIn(FrameworkElement? n)
    {
        if (n is PaneFrame f) yield return f;
        else if (n is SplitNode s)
        {
            foreach (var p in FramesIn(s.First)) yield return p;
            foreach (var p in FramesIn(s.Second)) yield return p;
        }
    }

    private DocPane _mainLast = null!;

    // ---- tabs -----------------------------------------------------------------------------------------------------

    /// <summary>Adds a tab to <paramref name="pane"/>, else where it belongs (0.6.7): a session (<paramref name="session"/>)
    /// goes to the Hosts tab's own panes when you opened it from there (<see cref="ArmSessionTarget"/>) or are working in
    /// them; anything else never lands inside the Hosts tab but beside it.</summary>
    public void Add(TabItem tab, DocPane? pane = null, bool session = false)
    {
        if (pane == null)
        {
            pane = (session ? TakeSessionTarget() : null) ?? ActivePane;
            if (!session && EmbeddedOf(pane) is { } e) pane = OuterPane(e) ?? MainPanes.First();
        }
        Wire(tab);
        pane.Items.Add(tab);
        pane.SelectedItem = tab;
        ShowOwner(pane);
        SetActive(pane);
    }

    // ---- 0.6.7: panes inside a tab (the Hosts tab's sessions) ----------------------------------------------------------

    /// <summary>A document area inside a tab's content (the Hosts tab: DarkCipher0x, "the terminal windows you open, should open
    /// within the tab... so that you can still pop out the hosts while viewing the terminal"). Its panes are ordinary
    /// panes of this host (split, drag, close, switch-to-open work), and <see cref="Owner"/> is the tab that holds it.</summary>
    private sealed record Embedded(Border Root, Func<TabItem?> Owner);
    private readonly List<Embedded> _embedded = new();
    private (DocPane Pane, DateTime Until)? _sessionTarget;

    public IEnumerable<DocPane> EmbeddedPanes => _embedded.SelectMany(e => Walk(e.Root.Child as FrameworkElement));
    private Embedded? EmbeddedOf(DocPane? p) => p == null ? null : _embedded.FirstOrDefault(e => Walk(e.Root.Child as FrameworkElement).Contains(p));
    /// <summary>The pane is inside a tab (the Hosts tab), not a top-level pane.</summary>
    public bool IsEmbedded(DocPane? p) => EmbeddedOf(p) != null;
    private DocPane? OuterPane(Embedded e) => e.Owner() is { } t ? PaneOf(t) : null;

    /// <summary>Makes <paramref name="root"/> a document area of its own; returns its first pane.</summary>
    public DocPane AttachEmbedded(Border root, Func<TabItem?> owner)
    {
        var f = NewFrame();
        root.Child = f;
        _embedded.Add(new Embedded(root, owner));
        return f.Pane;
    }

    /// <summary>Ends an embedded area: its sessions move to the pane holding its owner tab (or the main window); its fixed
    /// tabs (the host list) stay with their owner.</summary>
    public void DetachEmbedded(Border root)
    {
        if (_embedded.FirstOrDefault(x => x.Root == root) is not { } e) return;
        var inner = Walk(root.Child as FrameworkElement).ToList();
        _embedded.Remove(e);
        var target = OuterPane(e) is { } o && Panes.Contains(o) ? o : MainPanes.Contains(_mainLast) ? _mainLast : MainPanes.First();
        foreach (var p in inner)
            foreach (var t in p.Items.OfType<TabItem>().ToList())
            {
                p.Items.Remove(t);
                if ((t.Tag as string) == "fixed") { _wired.Remove(t); continue; }
                target.Items.Add(t);
                target.SelectedItem = t;
            }
        root.Child = null;
        if (inner.Contains(ActivePane)) SetActive(target);
        if (_sessionTarget is { } st && inner.Contains(st.Pane)) _sessionTarget = null;
    }

    /// <summary>Adds a tab to a pane without making it the active one (the Hosts tab's list tab).</summary>
    public void AddTo(TabItem tab, DocPane pane, bool activate)
    {
        Wire(tab);
        pane.Items.Add(tab);
        if (!activate) return;
        pane.SelectedItem = tab;
        SetActive(pane);
    }

    /// <summary>The next session opened (within 20 s) goes to <paramref name="pane"/>: the Hosts page arms it when you
    /// connect from it.</summary>
    public void ArmSessionTarget(DocPane pane) => _sessionTarget = (pane, DateTime.UtcNow.AddSeconds(20));

    private DocPane? TakeSessionTarget()
    {
        var t = _sessionTarget;
        _sessionTarget = null;
        return t is { } x && x.Until > DateTime.UtcNow && Panes.Contains(x.Pane) ? x.Pane : null;
    }

    /// <summary>A pane inside a tab is only visible when that tab is: select the owner tab first.</summary>
    private void ShowOwner(DocPane pane)
    {
        if (EmbeddedOf(pane) is { } e && e.Owner() is { } owner && PaneOf(owner) is { } op && op.SelectedItem != owner)
        {
            op.SelectedItem = owner;
            ShowOwner(op);
        }
    }

    public void Select(TabItem tab)
    {
        var p = PaneOf(tab);
        if (p == null) return;
        p.SelectedItem = tab;
        ShowOwner(p);   // 0.6.7: a session inside the Hosts tab: that tab comes forward too
        SetActive(p);
        if (Window.GetWindow(p) is FloatWindow fw)
        {
            if (fw.WindowState == WindowState.Minimized) fw.WindowState = WindowState.Normal;
            if (!fw.IsVisible && !TestMode.Running) fw.Show();
            BringOnScreen(fw);
            fw.Activate();
        }
    }

    /// <summary>0.6.25: a pop-out window that ended up off every monitor comes back beside the main window.</summary>
    private void BringOnScreen(FloatWindow fw)
    {
        if (TestMode.Running || fw.WindowState != WindowState.Normal) return;
        var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (vs.IntersectsWith(new Rect(fw.Left, fw.Top, Math.Min(Math.Max(fw.ActualWidth, 200), 200), 60))) return;
        var main = Window.GetWindow(this);
        fw.Left = (main?.Left ?? vs.Left) + 60; fw.Top = (main?.Top ?? vs.Top) + 60;
    }

    /// <summary>Removes a tab (the caller disposes its content). Empty panes collapse; empty pop-out windows close.</summary>
    public void Remove(TabItem tab)
    {
        var p = PaneOf(tab);
        if (p == null) return;
        p.Items.Remove(tab);
        _wired.Remove(tab);
        AfterRemoval(p);
    }

    private void AfterRemoval(DocPane p)
    {
        if (p.Items.Count > 0) return;
        if (FloatOf(p) is { } fw)
        {
            // An empty pane of a split window folds away; the window's last pane stays (empty) in a New window, while a
            // popped-out tab's window closes as before.
            if (PanesOf(fw).Count() > 1) { Collapse(p.Frame); return; }
            if (fw.KeepOpen) { fw.UpdateTitle(null); return; }
            _floats.Remove(fw); fw.CloseQuietly(); SetActive(_mainLast); return;
        }
        Collapse(p.Frame);
    }

    public void MoveTab(TabItem tab, DocPane target, int index = -1)
    {
        var from = PaneOf(tab);
        if (from == null) return;
        if (from == target)
        {
            if (index < 0 || from.Items.IndexOf(tab) == index) return;
            from.Items.Remove(tab);
            from.Items.Insert(Math.Min(index, from.Items.Count), tab);
            from.SelectedItem = tab;
            return;
        }
        from.Items.Remove(tab);
        if (index >= 0 && index <= target.Items.Count) target.Items.Insert(index, tab); else target.Items.Add(tab);
        target.SelectedItem = tab;
        SetActive(target);
        AfterRemoval(from);
    }

    public void NextTab(int dir)
    {
        var p = ActivePane;
        if (p.Items.Count < 2) return;
        int i = (p.SelectedIndex + dir + p.Items.Count) % p.Items.Count;
        p.SelectedIndex = i;
    }

    public void FocusNextPane()
    {
        var all = Panes.ToList();
        if (all.Count < 2) return;
        var next = all[(all.IndexOf(ActivePane) + 1) % all.Count];
        SetActive(next);
        if (Window.GetWindow(next) is Window w) w.Activate();
        (next.SelectedContent as UIElement)?.Focus();
        if (next.SelectedItem is TabItem { Content: IActivatable a }) a.Activated();
    }

    // ---- layout ---------------------------------------------------------------------------------------------------

    /// <summary>Moves the tab into a new pane to the right (Horizontal) or below (Vertical) of its current pane.</summary>
    public bool Split(TabItem tab, Orientation o)
    {
        var from = PaneOf(tab);
        if (from == null) return false;
        if (from.Items.Count < 2)
        {
            MainWindow.Instance?.Status("This pane has only this tab — a split needs a tab on each side (open another tab here, or drag one in).");
            return false;
        }
        var frame = from.Frame;
        var node = new SplitNode(o);
        ReplaceInParent(frame, node);
        var fresh = NewFrame();
        node.Put(0, frame);
        node.Put(2, fresh);
        from.Items.Remove(tab);
        fresh.Pane.Items.Add(tab);
        fresh.Pane.SelectedItem = tab;
        SetActive(fresh.Pane);
        return true;
    }

    public void MoveToNextPane(TabItem tab)
    {
        var here = PaneOf(tab);
        var all = (FloatOf(here) is { } w ? PanesOf(w) : EmbeddedOf(here) is { } em ? Walk(em.Root.Child as FrameworkElement) : MainPanes).ToList();   // within the tab's own window or Hosts tab
        var from = PaneOf(tab);
        if (from == null || all.Count < 2) return;
        var target = all[(Math.Max(0, all.IndexOf(from)) + 1) % all.Count];
        MoveTab(tab, target);
    }

    /// <summary>Lays every tab in the main window out as a grid, one tab per pane (up to 12 panes).</summary>
    public void Tile()
    {
        var tabs = MainPanes.SelectMany(p => p.Items.OfType<TabItem>()).ToList();
        if (tabs.Count < 2) { MainWindow.Instance?.Status("Tiling needs at least two tabs."); return; }
        var selected = ActiveTab;
        foreach (var p in MainPanes.ToList()) p.Items.Clear();
        int n = Math.Min(12, tabs.Count);
        int cols = (int)Math.Ceiling(Math.Sqrt(n)), rows = (int)Math.Ceiling(n / (double)cols);
        var frames = new List<PaneFrame>();
        for (int i = 0; i < n; i++) frames.Add(NewFrame());
        for (int i = 0; i < tabs.Count; i++)
        {
            var f = frames[Math.Min(i, n - 1)];
            f.Pane.Items.Add(tabs[i]);
            if (f.Pane.SelectedItem == null || tabs[i] == selected) f.Pane.SelectedItem = tabs[i];
        }
        var rowNodes = new List<FrameworkElement>();
        for (int r = 0; r < rows; r++)
        {
            var slice = frames.Skip(r * cols).Take(cols).Cast<FrameworkElement>().ToList();
            if (slice.Count > 0) rowNodes.Add(Chain(slice, Orientation.Horizontal));
        }
        Child = Chain(rowNodes, Orientation.Vertical);
        SetActive(frames.FirstOrDefault(f => f.Pane.Items.Contains(selected))?.Pane ?? frames[0].Pane);
    }

    private static FrameworkElement Chain(List<FrameworkElement> items, Orientation o)
    {
        if (items.Count == 1) return items[0];
        var node = new SplitNode(o, 1.0 / items.Count);
        node.Put(0, items[0]);
        node.Put(2, Chain(items.Skip(1).ToList(), o));
        return node;
    }

    /// <summary>Crash recovery (ADR-022): lays the main window out as one pane per group, in order (a grid like
    /// <see cref="Tile"/>); tabs of the main window in no group go to the first pane.</summary>
    public void Arrange(IReadOnlyList<IReadOnlyList<TabItem>> groups, TabItem? active)
    {
        var listed = groups.SelectMany(g => g).ToHashSet();
        var rest = MainPanes.SelectMany(p => p.Items.OfType<TabItem>()).Where(t => !listed.Contains(t)).ToList();
        var sets = groups.Where(g => g.Count > 0).Select(g => g.ToList()).Take(12).ToList();
        if (sets.Count == 0) return;
        foreach (var extra in groups.Where(g => g.Count > 0).Skip(12)) sets[^1].AddRange(extra);
        sets[0].InsertRange(0, rest);
        var keep = sets.SelectMany(s => s).ToHashSet();
        foreach (var p in MainPanes.ToList()) foreach (var t in p.Items.OfType<TabItem>().Where(keep.Contains).ToList()) p.Items.Remove(t);
        var frames = sets.Select(s =>
        {
            var f = NewFrame();
            foreach (var t in s) { Wire(t); f.Pane.Items.Add(t); }
            f.Pane.SelectedItem = s.Contains(active!) ? active : s[^1];
            return f;
        }).ToList();
        int n = frames.Count, cols = (int)Math.Ceiling(Math.Sqrt(n)), rows = (int)Math.Ceiling(n / (double)cols);
        var rowNodes = new List<FrameworkElement>();
        for (int r = 0; r < rows; r++)
        {
            var slice = frames.Skip(r * cols).Take(cols).Cast<FrameworkElement>().ToList();
            if (slice.Count > 0) rowNodes.Add(Chain(slice, Orientation.Horizontal));
        }
        Child = Chain(rowNodes, Orientation.Vertical);
        SetActive(frames.FirstOrDefault(f => f.Pane.Items.Contains(active))?.Pane ?? frames[0].Pane);
    }

    /// <summary>Puts every tab of the main window back into a single pane.</summary>
    public void MergeAll()
    {
        var tabs = MainPanes.SelectMany(p => p.Items.OfType<TabItem>()).ToList();
        var selected = ActiveTab;
        foreach (var p in MainPanes.ToList()) p.Items.Clear();
        var root = NewFrame();
        Child = root;
        foreach (var t in tabs) root.Pane.Items.Add(t);
        root.Pane.SelectedItem = selected ?? tabs.FirstOrDefault();
        SetActive(root.Pane);
    }

    /// <summary>Moves the tab into a window of its own (e.g. for a second monitor), at <paramref name="at"/> (screen DIPs,
    /// the window's top-left) when given. Such a window closes when its last tab leaves.</summary>
    public FloatWindow? PopOut(TabItem tab, Point? at = null)
    {
        var from = PaneOf(tab);
        if (from == null) return null;
        var win = CreateWindow(keepOpen: false, at == null ? null : new WindowPlacement { Left = at.Value.X, Top = at.Value.Y });
        from.Items.Remove(tab);
        win.Frame.Pane.Items.Add(tab);
        win.Frame.Pane.SelectedItem = tab;
        AfterRemoval(from);
        win.Show();
        SetActive(win.Frame.Pane);
        return win;
    }

    /// <summary>A new, empty window with its own host pane (0.6.4, ADR-082), on the main window's monitor unless a
    /// placement is given. It stays open when empty; whatever opens while it is active opens in it.</summary>
    public FloatWindow NewWindow(WindowPlacement? at = null)
    {
        var win = CreateWindow(keepOpen: true, at);
        win.Show();
        if (at is { Maximized: true } && !TestMode.Running) win.WindowState = WindowState.Maximized;
        SetActive(win.Frame.Pane);
        return win;
    }

    private FloatWindow CreateWindow(bool keepOpen, WindowPlacement? at)
    {
        var main = Window.GetWindow(this);
        var win = new FloatWindow(this, main) { KeepOpen = keepOpen };
        if (!TestMode.Running)
        {
            // Independent of the main window (an owned window minimises with it and always sits above it): a workspace
            // on another monitor. Without a saved place it opens a little offset from the main window, on its monitor.
            win.Owner = null;
            win.WindowStartupLocation = WindowStartupLocation.Manual;
            if (main != null) { win.Left = main.Left + 60 + 30 * _floats.Count; win.Top = main.Top + 60 + 30 * _floats.Count; }
        }
        // "Window 2", "Window 3"…: the lowest number not in use (the main window is 1).
        int n = 2;
        while (_floats.Any(f => f.Label == $"Window {n}")) n++;
        win.Label = $"Window {n}";
        win.UpdateTitle(null);
        if (at != null && !TestMode.Running)
        {
            win.WindowStartupLocation = WindowStartupLocation.Manual;
            var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            double w = Math.Clamp(at.Width, 480, Math.Max(480, vs.Width)), h = Math.Clamp(at.Height, 320, Math.Max(320, vs.Height));
            // Only where a monitor still is (a disconnected monitor's window comes back on the main one).
            if (vs.IntersectsWith(new Rect(at.Left, at.Top, Math.Min(w, 200), 60))) { win.Left = at.Left; win.Top = at.Top; win.Width = w; win.Height = h; }
            else win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        _floats.Add(win);
        return win;
    }

    /// <summary>Closes an extra window from the app (no question): its tabs move to the main window first.</summary>
    public void CloseWindow(FloatWindow w)
    {
        if (!_floats.Contains(w)) return;
        var target = MainPanes.Contains(_mainLast) ? _mainLast : MainPanes.First();
        foreach (var p in PanesOf(w).ToList())
            foreach (var t in p.Items.OfType<TabItem>().ToList()) { p.Items.Remove(t); target.Items.Add(t); target.SelectedItem = t; }
        _floats.Remove(w);
        w.CloseQuietly();
        SetActive(target);
    }

    /// <summary>Moves a tab to an extra window (the pane last used there).</summary>
    public void MoveToWindow(TabItem tab, FloatWindow w)
    {
        if (!_floats.Contains(w)) return;
        MoveTab(tab, w.LastActive is { } p && PanesOf(w).Contains(p) ? p : w.Frame.Pane);
        if (!TestMode.Running) w.Activate();
    }

    /// <summary>Returns a tab from an extra window to the main window (the pane last used there).</summary>
    public void DockBack(TabItem tab)
    {
        var from = PaneOf(tab);
        var fw = FloatOf(from);
        if (from == null || fw == null) return;
        var target = MainPanes.Contains(_mainLast) ? _mainLast : MainPanes.First();
        from.Items.Remove(tab);
        target.Items.Add(tab);
        target.SelectedItem = tab;
        SetActive(target);
        if (from.Items.Count == 0)
        {
            if (PanesOf(fw).Count() > 1) Collapse(from.Frame);
            else if (!fw.KeepOpen) { _floats.Remove(fw); fw.CloseQuietly(); }
            else fw.UpdateTitle(null);
        }
        Window.GetWindow(this)?.Activate();
    }

    public bool IsFloating(TabItem tab) => WindowOf(tab) != null;

    /// <summary>The window items of a tab's menus (0.6.4): dock back / pop out, move to another window, a new window.</summary>
    public IEnumerable<object?> WindowMenuItems(TabItem? t)
    {
        var here = t == null ? null : WindowOf(t);
        if (t != null)
            yield return here != null ? Menus.Item("Dock back into the main window", () => DockBack(t)) : Menus.Item("Pop out to a window", () => PopOut(t), "Ctrl+Alt+O");
        var others = _floats.Where(f => f != here).ToList();
        if (t != null && (others.Count > 0 || here != null))
        {
            var items = new List<object?>();
            if (here != null) items.Add(Menus.Item("Main window", () => DockBack(t)));
            items.AddRange(others.Select(f => (object?)Menus.Item(f.Label, () => MoveToWindow(t, f))));
            items.Add(Menus.Sep());
            items.Add(Menus.Item("New window", () => { var w = NewWindow(); MoveToWindow(t, w); }));
            yield return Menus.Sub("Move to window", items.ToArray());
        }
        yield return Menus.Item("New window (with a host pane, for another monitor)", () => NewWindow(), "Ctrl+Shift+N");
    }

    /// <summary>The user closes an extra window that still has tabs: move them to the main window (default), close them,
    /// or keep the window. True = go ahead.</summary>
    internal bool ConfirmCloseWindow(FloatWindow fw)
    {
        if (_shuttingDown) return true;
        var tabs = PanesOf(fw).SelectMany(p => p.Items.OfType<TabItem>()).ToList();
        if (tabs.Count == 0) return true;
        int choice = Dialogs.Choose($"Close {fw.Label}", $"{fw.Label} has {tabs.Count} tab(s). Move them to the main window, or close them?",
            new[] { "Move them to the main window", "Close them", "Cancel" }, cancelIndex: 2);
        if (choice is 2 or -1) return false;
        if (choice == 1)
            foreach (var t in tabs) if ((t.Tag as string) != "fixed") CloseRequested?.Invoke(t);
        return PanesOf(fw).SelectMany(p => p.Items.OfType<TabItem>()).All(t => (t.Tag as string) == "fixed") || choice == 0;
    }

    /// <summary>An extra window is closing by the user: its remaining tabs move to the main window (unless the app is exiting).</summary>
    internal void FloatClosing(FloatWindow fw)
    {
        if (!_floats.Remove(fw)) return;
        if (_shuttingDown) return;
        var target = MainPanes.Contains(_mainLast) ? _mainLast : MainPanes.First();
        foreach (var p in PanesOf(fw).ToList())
            foreach (var t in p.Items.OfType<TabItem>().ToList())
            {
                p.Items.Remove(t);
                target.Items.Add(t);
                target.SelectedItem = t;
            }
        SetActive(target);
    }

    /// <summary>The window became active (or its host pane is used): new tabs go to the pane last used in it.</summary>
    internal void ActivateWindow(FloatWindow w)
    {
        if (!_floats.Contains(w)) return;
        var p = w.LastActive is { } last && PanesOf(w).Contains(last) ? last : w.Frame.Pane;
        if (ActivePane != p) SetActive(p);
    }

    /// <summary>The main window became active: new tabs go to the pane last used there.</summary>
    public void ActivateMain()
    {
        var p = MainPanes.Contains(_mainLast) ? _mainLast : MainPanes.First();
        if (ActivePane != p) SetActive(p);
    }

    public void ShutDown()
    {
        _shuttingDown = true;
        foreach (var f in _floats.ToList()) f.CloseQuietly();
        _floats.Clear();
    }

    // ---- internals ------------------------------------------------------------------------------------------------

    internal PaneFrame NewFrame()
    {
        var f = new PaneFrame(this);
        var pane = f.Pane;
        pane.SelectionChanged += (_, e) =>
        {
            if (e.Source != pane) return;
            if (FloatOf(pane) is { } fw && (fw.LastActive == pane || fw.LastActive == null)) fw.UpdateTitle(pane.SelectedItem as TabItem);
            if (pane == ActivePane) ActiveTabChanged?.Invoke(pane.SelectedItem as TabItem);
        };
        // 0.6.7: panes can sit inside a tab (the Hosts tab), so only the innermost pane under the mouse / focus becomes active.
        bool Mine(object src) => src is DependencyObject d && Menus.Ancestor<DocPane>(d) == pane;
        pane.PreviewMouseDown += (_, e) => { if (Mine(e.OriginalSource)) SetActive(pane); };
        pane.AddHandler(UIElement.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((_, e) => { if (Mine(e.OriginalSource)) SetActive(pane); }), true);
        return f;
    }

    private void SetActive(DocPane pane)
    {
        bool changed = ActivePane != pane;
        ActivePane = pane;
        if (FloatOf(pane) is { } fw) { fw.LastActive = pane; fw.UpdateTitle(pane.SelectedItem as TabItem); } else _mainLast = pane;
        bool many = Panes.Count() > 1;
        foreach (var p in Panes) p.Frame.SetActiveLook(p == pane, many);
        if (changed) ActiveTabChanged?.Invoke(pane.SelectedItem as TabItem);
    }

    private void ReplaceInParent(FrameworkElement old, FrameworkElement @new)
    {
        if (old.Parent is SplitNode s) s.Replace(old, @new);
        else if (Child == old) Child = @new;
        else if (_floats.FirstOrDefault(f => f.DocRoot.Child == old) is { } w) w.DocRoot.Child = @new;   // an extra window's root
        else if (_embedded.FirstOrDefault(e => e.Root.Child == old) is { } em) em.Root.Child = @new;   // a tab's own area (Hosts)
    }

    private void Collapse(PaneFrame frame)
    {
        if (frame.Parent is not SplitNode s) return;   // the last pane stays (it can be empty)
        var window = FloatOf(frame.Pane);
        var emb = EmbeddedOf(frame.Pane);
        var sibling = s.First == frame ? s.Second : s.First;
        s.Children.Remove(frame);
        if (sibling != null) s.Children.Remove(sibling);
        if (sibling != null) ReplaceInParent(s, sibling);
        if (_mainLast == frame.Pane) _mainLast = MainPanes.First();
        if (window != null && window.LastActive == frame.Pane) window.LastActive = PanesOf(window).FirstOrDefault();
        if (ActivePane == frame.Pane) SetActive(window != null ? PanesOf(window).First() : emb != null ? Walk(emb.Root.Child as FrameworkElement).First() : MainPanes.First());
        else SetActive(ActivePane);
    }

    /// <summary>Tab header behaviours: right-click menu, middle-click close, drag to another pane.</summary>
    private void Wire(TabItem tab)
    {
        if (!_wired.Add(tab)) return;
        if (TabMenu != null) Menus.Attach(tab, () => TabMenu(tab));
        Point? down = null;
        tab.PreviewMouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Middle && (tab.Tag as string) != "fixed") { e.Handled = true; CloseRequested?.Invoke(tab); }
            // 0.6.25: only the header starts a drag. A press inside the tab's page (a window dragged in a VNC or RDP
            // session, a terminal selection) tunnels through the TabItem too, and used to tear the whole tab out.
            else if (e.ChangedButton == MouseButton.Left) down = InPage(tab, e.OriginalSource) ? null : e.GetPosition(null);
        };
        tab.PreviewMouseUp += (_, _) => down = null;
        tab.PreviewMouseMove += (_, e) =>
        {
            if (down is not { } d || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(null);
            if (Math.Abs(p.X - d.X) < SystemParameters.MinimumHorizontalDragDistance * 2 && Math.Abs(p.Y - d.Y) < SystemParameters.MinimumVerticalDragDistance * 2) return;
            if (e.OriginalSource is DependencyObject src && Menus.Ancestor<Button>(src) != null) return;   // the × button
            if ((tab.Tag as string) == "fixed" && IsEmbedded(PaneOf(tab))) return;   // the Hosts tab's host list stays put
            down = null;
            try
            {
                var from = PaneOf(tab);
                var result = DragDrop.DoDragDrop(tab, new DataObject(typeof(TabItem), tab), DragDropEffects.Move);
                // 0.6.4: dropped on the desktop (outside every window of the app): the tab gets a window of its own there.
                if (result == DragDropEffects.None && !TestMode.Running && PaneOf(tab) == from && DesktopDropPoint() is { } at) PopOut(tab, new Point(at.X - 120, at.Y - 16));
            }
            catch (Exception ex) { App.Log("Tab drag", ex); }
        };
    }

    /// <summary>True when <paramref name="source"/> sits inside the tab's content rather than its header.</summary>
    internal static bool InPage(TabItem tab, object? source)
    {
        if (tab.Content is not DependencyObject page) return false;
        for (var d = source as DependencyObject; d != null && d != tab; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d == page) return true;
        return false;
    }

    /// <summary>The mouse position (screen DIPs of the main window) when it is outside every window of the app; else null.</summary>
    private Point? DesktopDropPoint()
    {
        if (!GetCursorPos(out var px) || Window.GetWindow(this) is not { } main || PresentationSource.FromVisual(main) is not { CompositionTarget: { } ct }) return null;
        var p = ct.TransformFromDevice.Transform(new Point(px.X, px.Y));
        foreach (var w in new Window[] { main }.Concat(_floats))
        {
            if (w.WindowState == WindowState.Minimized) continue;
            var r = w.WindowState == WindowState.Maximized ? new Rect(w.PointToScreen(new Point(0, 0)), new Size(w.ActualWidth, w.ActualHeight)) : new Rect(w.Left, w.Top, w.ActualWidth, w.ActualHeight);
            if (w.WindowState == WindowState.Maximized) r = new Rect(ct.TransformFromDevice.Transform(r.TopLeft), r.Size);
            if (r.Contains(p)) return null;
        }
        return p;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
}

