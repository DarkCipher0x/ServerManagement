using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using DcxManager.Docs;
using DcxManager.Services;

namespace DcxManager.Views;

/// <summary>
/// Docs (0.6.12, ADR-091; DarkCipher0x: "a powerful but lightweight and efficient markdown editor (separate from notes), but
/// allows me to pin to servers or clusters etc, and store in organised directories"). A library tree (all, favourites,
/// pinned, your folders, trash) and a Markdown editor with a live preview (edit / split / preview), a formatting toolbar
/// and shortcuts, smart lists, an outline, revisions, import and export. Every document is its own encrypted file
/// (<see cref="DocStore"/>); autosave runs 1 s after typing stops and whenever you leave the document.
/// </summary>
public sealed class DocsView : UserControl, IDisposable
{
    public static DocsView? Current { get; private set; }

    private readonly TextBox _search = Hint.Set(new TextBox { Padding = new Thickness(6, 5, 6, 5) }, "Search documents…");
    private readonly TreeView _tree = new() { Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _count = new() { Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _title = new TextBox { FontWeight = FontWeights.SemiBold, Padding = new Thickness(6, 4, 6, 4) }.Fs(FontStep.H1);
    private readonly TextBlock _where = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
    private readonly WrapPanel _pins = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _body = new TextBox
    {
        AcceptsReturn = true, AcceptsTab = false, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(10, 8, 10, 8),
    }.Fs(FontStep.Base).Editor();
    private readonly FlowDocumentScrollViewer _preview = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, IsToolBarVisible = false };
    private readonly ComboBox _mode = new() { Width = 110, Margin = new Thickness(6, 0, 0, 0), ToolTip = "Edit, split (edit beside the preview) or preview only" };
    private readonly ComboBox _outline = new() { Width = 200, Margin = new Thickness(6, 0, 0, 0), ToolTip = "Jump to a heading" };
    private readonly ToggleButton _fav = new() { Content = "☆", Margin = new Thickness(6, 0, 0, 0), ToolTip = "Favourite" };
    private readonly TextBlock _status = new() { Margin = new Thickness(0, 4, 0, 0) };
    private readonly Grid _panes = new() { Margin = new Thickness(0, 6, 0, 0) };
    private readonly Grid _editor = new();
    private readonly TextBlock _empty = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(20), VerticalAlignment = VerticalAlignment.Top };
    private readonly DispatcherTimer _autosave, _render, _searchDelay;
    private Doc? _doc;
    private string _baseTitle = "", _baseBody = "";
    private bool _loading;
    private string _folder = "";
    private string? _pinScope;

    public DocsView()
    {
        Current = this;
        var root = new Grid { Margin = new Thickness(12, 10, 12, 10) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(290), MinWidth = 200 });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // ---- left: the library ---------------------------------------------------------------------------------------
        var left = new DockPanel();
        var head = new StackPanel();
        head.Children.Add(new TextBlock { Text = "Docs", Style = (Style)FindResource("H1") });
        head.Children.Add(new TextBlock { Text = "Markdown runbooks and references in folders, pinned to hosts, host folders and infrastructure. Encrypted like everything else.", Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6) });
        head.Children.Add(_search);
        var newRow = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        newRow.Children.Add(Btn("＋ Document", () => NewDoc(), primary: true));
        newRow.Children.Add(Btn("＋ Folder", NewFolder));
        newRow.Children.Add(Btn("Import .md…", Import));
        head.Children.Add(newRow);
        DockPanel.SetDock(head, Dock.Top);
        left.Children.Add(head);
        _count.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        DockPanel.SetDock(_count, Dock.Bottom);
        left.Children.Add(_count);
        left.Children.Add(_tree);
        root.Children.Add(left);
        var split = new GridSplitter { Width = 6, HorizontalAlignment = HorizontalAlignment.Stretch, Background = System.Windows.Media.Brushes.Transparent, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        Grid.SetColumn(split, 1);
        root.Children.Add(split);

        // ---- right: the editor -----------------------------------------------------------------------------------------
        var meta = new WrapPanel { Margin = new Thickness(0, 6, 0, 4) };
        _where.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        meta.Children.Add(_where);
        meta.Children.Add(Btn("Move…", MoveDoc, tip: "Move to another folder"));
        meta.Children.Add(Lbl("PINNED TO", 14));
        meta.Children.Add(_pins);
        meta.Children.Add(Btn("📌 Pin to…", () => ShowPinMenu(), tip: "Pin to a host, a host folder or infrastructure (a cluster, hypervisor or Docker host)"));

        var bar = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
        void T(string text, string tip, Action a) { var b = Btn(text, a, tip: tip); b.MinWidth = 30; bar.Children.Add(b); }
        T("B", "Bold (Ctrl+B)", () => Wrap("**", "**"));
        T("I", "Italic (Ctrl+I)", () => Wrap("*", "*"));
        T("S", "Strikethrough", () => Wrap("~~", "~~"));
        T("`code`", "Inline code", () => Wrap("`", "`"));
        T("H1", "Heading 1", () => LinePrefix("# "));
        T("H2", "Heading 2", () => LinePrefix("## "));
        T("H3", "Heading 3", () => LinePrefix("### "));
        T("•", "Bulleted list", () => LinePrefix("- "));
        T("1.", "Numbered list", () => LinePrefix("1. "));
        T("☐", "Task (Ctrl+Enter ticks it)", () => LinePrefix("- [ ] "));
        T("❝", "Quote", () => LinePrefix("> "));
        T("🔗", "Link (Ctrl+K)", InsertLink);
        T("{ }", "Code block (wraps the selection)", () => Wrap("```bash\n", "\n```"));
        T("▦", "Table", () => Insert("\n| Column | Column |\n|---|---|\n| | |\n"));
        T("―", "Horizontal rule", () => Insert("\n---\n"));
        T("🕒", "Timestamp", () => Insert(DateTime.Now.ToString("yyyy-MM-dd HH:mm") + " "));
        foreach (var (v, t) in new[] { ("split", "Split"), ("edit", "Edit"), ("preview", "Preview") }) _mode.Items.Add(new ComboBoxItem { Content = t, Tag = v });
        bar.Children.Add(_mode);
        bar.Children.Add(_outline);
        bar.Children.Add(_fav);
        bar.Children.Add(Btn("History…", ShowHistory, tip: "Earlier versions of this document"));
        bar.Children.Add(Btn("Export .md…", () => { if (_doc != null) { SaveNow(); Menus.SaveText(_doc.Body, SafeName(_doc.Title) + ".md", "Markdown|*.md|Text|*.txt"); } }));
        bar.Children.Add(Btn("🗑", () => TrashOrRestore(), tip: "Move to the Trash (or restore from it)", danger: true));

        var top = new StackPanel();
        top.Children.Add(_title);
        top.Children.Add(meta);
        top.Children.Add(bar);
        _panes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 120 });
        _panes.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _panes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 120 });
        _panes.Children.Add(_body);
        var paneSplit = new GridSplitter { Width = 6, HorizontalAlignment = HorizontalAlignment.Stretch, Background = System.Windows.Media.Brushes.Transparent, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        Grid.SetColumn(paneSplit, 1);
        _panes.Children.Add(paneSplit);
        var previewFrame = new Border { BorderThickness = new Thickness(1), Child = _preview };
        previewFrame.SetResourceReference(Border.BorderBrushProperty, "Line");
        previewFrame.SetResourceReference(Border.BackgroundProperty, "Surface");
        Grid.SetColumn(previewFrame, 2);
        _panes.Children.Add(previewFrame);

        var right = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        right.Children.Add(top);
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        DockPanel.SetDock(_status, Dock.Bottom);
        right.Children.Add(_status);
        right.Children.Add(_panes);
        _editor.Children.Add(right);
        _empty.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _editor.Children.Add(_empty);
        Grid.SetColumn(_editor, 2);
        root.Children.Add(_editor);
        Content = root;

        // ---- behaviour ---------------------------------------------------------------------------------------------------
        _autosave = new DispatcherTimer(TimeSpan.FromMilliseconds(1000), DispatcherPriority.Background, (_, _) => { _autosave!.Stop(); SaveNow(); }, Dispatcher) { IsEnabled = false };
        _render = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => { _render!.Stop(); RenderPreview(); }, Dispatcher) { IsEnabled = false };
        _searchDelay = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => { _searchDelay!.Stop(); Refresh(); }, Dispatcher) { IsEnabled = false };
        void Dirty() { if (!_loading && _doc != null) { _autosave.Stop(); _autosave.Start(); Status("Editing…"); } }
        _title.TextChanged += (_, _) => Dirty();
        _body.TextChanged += (_, _) => { Dirty(); if (!_loading) { _render.Stop(); _render.Start(); } };
        _title.LostFocus += (_, _) => SaveNow();
        _body.LostFocus += (_, _) => SaveNow();
        _search.TextChanged += (_, _) => { _searchDelay.Stop(); _searchDelay.Start(); };
        _fav.Click += (_, _) => { if (_doc == null) return; _doc.Favorite = _fav.IsChecked == true; _fav.Content = _doc.Favorite ? "★" : "☆"; SaveNow(); Refresh(); };
        _mode.SelectionChanged += (_, _) => ApplyMode();
        _mode.SelectedIndex = 0;
        _outline.SelectionChanged += (_, _) => { if (_outline.SelectedItem is ComboBoxItem { Tag: int line }) GoToLine(line); };
        _outline.DropDownOpened += (_, _) => FillOutline();
        _body.PreviewKeyDown += OnEditorKey;
        _tree.SelectedItemChanged += (_, _) => OnTreeSelected();
        _tree.ContextMenuOpening += (_, e) => { var items = TreeMenu().ToList(); if (items.Count == 0) { e.Handled = true; return; } _tree.ContextMenu = new ContextMenu(); Menus.Fill(_tree.ContextMenu.Items, items); };
        Action<Doc?> onChange = d => Dispatcher.BeginInvoke(() => { if (d == null || d != _doc) Refresh(); });
        Loaded += (_, _) => { DocStore.Changed -= onChange; DocStore.Changed += onChange; };
        Unloaded += (_, _) => DocStore.Changed -= onChange;
        Refresh();
        ShowEditor(false);
    }

    // ---- the library tree -------------------------------------------------------------------------------------------

    /// <summary>Rebuilds the tree (keeps the open document selected).</summary>
    public void Refresh()
    {
        var keep = _doc?.Id;
        _tree.Items.Clear();
        var q = _search.Text.Trim();
        var all = DocStore.All;
        if (q.Length > 0)
        {
            var hits = DocStore.Search(q);
            var res = Node($"🔎 Results ({hits.Count})", "results", expanded: true);
            foreach (var d in hits) res.Items.Add(Leaf(d));
            _tree.Items.Add(res);
        }
        else
        {
            var allNode = Node($"📄 All documents ({all.Count})", "all");
            foreach (var d in all.OrderByDescending(d => d.Updated).Take(50)) allNode.Items.Add(Leaf(d));
            _tree.Items.Add(allNode);
            var favs = all.Where(d => d.Favorite).ToList();
            var favNode = Node($"★ Favourites ({favs.Count})", "fav");
            foreach (var d in favs.OrderBy(d => d.Title)) favNode.Items.Add(Leaf(d));
            _tree.Items.Add(favNode);
            var pinNode = Node("📌 Pinned", "pins", expanded: _pinScope != null);
            foreach (var g in all.SelectMany(d => d.Pins.Select(p => (Pin: p, Doc: d))).GroupBy(x => x.Pin).OrderBy(g => DocStore.PinLabel(g.Key)))
            {
                var pn = Node($"{DocStore.PinLabel(g.Key)} ({g.Count()})", "pin:" + g.Key, expanded: g.Key == _pinScope);
                foreach (var x in g.OrderBy(x => x.Doc.Title)) pn.Items.Add(Leaf(x.Doc));
                pinNode.Items.Add(pn);
            }
            _tree.Items.Add(pinNode);
            // The folders, with their documents inside; top-level documents under "Unfiled".
            var map = new Dictionary<string, TreeViewItem>(StringComparer.OrdinalIgnoreCase);
            TreeViewItem FolderNode(string path)
            {
                if (map.TryGetValue(path, out var n)) return n;
                var name = path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;
                n = Node("📁 " + name, "folder:" + path, expanded: _folder.Equals(path, StringComparison.OrdinalIgnoreCase) || _folder.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase));
                map[path] = n;
                if (path.Contains('/')) FolderNode(path[..path.LastIndexOf('/')]).Items.Add(n); else _tree.Items.Add(n);
                return n;
            }
            foreach (var f in DocStore.Folders()) FolderNode(f);
            var unfiled = all.Where(d => d.Folder.Length == 0).ToList();
            if (unfiled.Count > 0) { var u = Node($"📄 Unfiled ({unfiled.Count})", "folder:"); foreach (var d in unfiled.OrderBy(d => d.Title)) u.Items.Add(Leaf(d)); _tree.Items.Add(u); }
            foreach (var d in all.Where(d => d.Folder.Length > 0).OrderBy(d => d.Title, StringComparer.OrdinalIgnoreCase)) FolderNode(d.Folder).Items.Add(Leaf(d));
            var trash = DocStore.Trash;
            if (trash.Count > 0) { var t = Node($"🗑 Trash ({trash.Count})", "trash"); foreach (var d in trash) t.Items.Add(Leaf(d)); _tree.Items.Add(t); }
            if (DocStore.Damaged.Count > 0) _tree.Items.Add(Node($"⚠ {DocStore.Damaged.Count} document file(s) couldn't be read (set aside, never overwritten)", "damaged"));
        }
        _count.Text = $"{all.Count} document(s) in {DocStore.Folders().Count} folder(s) · encrypted (AES-256-GCM)";
        if (keep != null && FindLeaf(_tree.Items, keep) is { } leaf) { _loading = true; leaf.IsSelected = true; leaf.BringIntoView(); _loading = false; }
        if (_doc != null && _doc.Deleted == null) ShowMeta();
    }

    private TreeViewItem Node(string header, string tag, bool expanded = false) => new() { Header = header, Tag = tag, IsExpanded = expanded };

    private TreeViewItem Leaf(Doc d)
    {
        var item = new TreeViewItem { Header = (d.Favorite ? "★ " : "") + d.Title + (d.Pins.Count > 0 ? "  📌" : ""), Tag = d, ToolTip = $"{d.Title}\n{(d.Folder.Length > 0 ? d.Folder : "Unfiled")} · updated {d.Updated:yyyy-MM-dd HH:mm}" + (d.Pins.Count > 0 ? "\nPinned to: " + string.Join(", ", d.Pins.Select(DocStore.PinLabel)) : "") };
        return item;
    }

    private static TreeViewItem? FindLeaf(ItemCollection items, string id)
    {
        foreach (var o in items)
            if (o is TreeViewItem i)
            {
                if (i.Tag is Doc d && d.Id == id) return i;
                if (FindLeaf(i.Items, id) is { } f) return f;
            }
        return null;
    }

    private void OnTreeSelected()
    {
        if (_loading || _tree.SelectedItem is not TreeViewItem item) return;
        switch (item.Tag)
        {
            case Doc d when d != _doc: Open(d); break;
            case string s when s.StartsWith("folder:", StringComparison.Ordinal): _folder = s[7..]; break;
            case string s when s.StartsWith("pin:", StringComparison.Ordinal): _pinScope = s[4..]; break;
        }
    }

    private IEnumerable<object?> TreeMenu()
    {
        if (_tree.SelectedItem is not TreeViewItem item) yield break;
        if (item.Tag is Doc d)
        {
            if (d.Deleted != null) { yield return Menus.Item("↶ Restore", () => { DocStore.Restore(d); Open(d); }); yield break; }
            yield return Menus.Item("Open", () => Open(d));
            yield return Menus.Item("Move to folder…", () => { Open(d); MoveDoc(); });
            yield return Menus.Item("📌 Pin to…", () => { Open(d); ShowPinMenu(); });
            yield return Menus.Item("Duplicate", () => { var c = DocStore.Create(d.Title + " (copy)", d.Folder, d.Pins, d.Body); Refresh(); Open(c); });
            yield return Menus.Item("Export .md…", () => Menus.SaveText(d.Body, SafeName(d.Title) + ".md", "Markdown|*.md|Text|*.txt"));
            yield return null;
            yield return Menus.Item("🗑 Move to Trash", () => { if (d == _doc) TrashOrRestore(); else DocStore.Delete(d); });
        }
        else if (item.Tag is string tag && tag.StartsWith("folder:", StringComparison.Ordinal))
        {
            var path = tag[7..];
            yield return Menus.Item("＋ Document here", () => NewDoc(path));
            yield return Menus.Item("＋ Folder inside…", () => NewFolder(path));
            if (path.Length == 0) yield break;
            yield return Menus.Item("Rename / move folder…", () =>
            {
                if (Ui.Dialogs.Prompt("Rename folder", "New path (use / for subfolders):", path) is { } to && to.Trim().Length > 0)
                    try { DocStore.RenameFolder(path, to); _folder = DocStore.NormalizeFolder(to); Refresh(); } catch (InvalidOperationException ex) { Ui.Dialogs.Error("Rename folder", ex.Message); }
            });
            yield return Menus.Item("Remove folder (documents move up)", () => { if (Ui.Dialogs.Confirm("Remove folder", $"Remove “{path}”? Its documents and subfolders move up one level; nothing is deleted.", "Remove")) { DocStore.RemoveFolder(path); Refresh(); } });
        }
        else if (item.Tag is "trash")
            yield return Menus.Item("Empty trash…", () =>
            {
                if (Ui.Dialogs.Confirm("Empty trash", $"Delete {DocStore.Trash.Count} document(s) in the Trash for good? This can't be undone.", "Delete", danger: true))
                    Status($"Deleted {DocStore.EmptyTrash()} document(s) for good.");
            });
    }

    // ---- documents ---------------------------------------------------------------------------------------------------

    public Doc NewDoc(string? folder = null, string? pin = null)
    {
        SaveNow();
        var d = DocStore.Create("Untitled document", folder ?? _folder, pin != null ? new[] { pin } : null, "# Untitled document\n\n");
        Refresh();
        Open(d);
        _title.Focus();
        _title.SelectAll();
        return d;
    }

    private void NewFolder() => NewFolder(_folder);

    private void NewFolder(string parent)
    {
        if (Ui.Dialogs.Prompt("New folder", "Folder name (use / for subfolders):", parent.Length > 0 ? parent + "/" : "") is not { } name || DocStore.NormalizeFolder(name).Length == 0) return;
        DocStore.CreateFolder(name);
        _folder = DocStore.NormalizeFolder(name);
        Refresh();
    }

    public void Open(Doc d)
    {
        SaveNow();
        _loading = true;
        try
        {
            _doc = d;
            _title.Text = d.Title;
            _body.Text = d.Body;
            _baseTitle = d.Title; _baseBody = d.Body;
            _fav.IsChecked = d.Favorite; _fav.Content = d.Favorite ? "★" : "☆";
            bool trashed = d.Deleted != null;
            _title.IsReadOnly = _body.IsReadOnly = trashed;
            ShowMeta();
            ShowEditor(true);
            RenderPreview();
            Status(trashed ? "In the Trash (read-only): restore it to edit." : $"Saved {d.Updated:yyyy-MM-dd HH:mm} · {Counts(d.Body)}");
            if (FindLeaf(_tree.Items, d.Id) is { } leaf && !leaf.IsSelected) leaf.IsSelected = true;
        }
        finally { _loading = false; }
    }

    /// <summary>The documents pinned to a host / folder / provider (from its menu): shows them, or offers a new one.</summary>
    public void ShowPinned(string pin, string label)
    {
        _search.Text = "";
        _pinScope = pin;
        var docs = DocStore.PinnedTo(pin);
        Refresh();
        if (docs.Count > 0) Open(docs.FirstOrDefault(d => d.Pins.Contains(pin)) ?? docs[0]);   // pinned to it directly before its folder's
        else if (Ui.Dialogs.Confirm("Docs", $"No documents are pinned to {label} yet. Create one?", "Create")) NewDoc(pin: pin);
    }

    private void ShowEditor(bool on)
    {
        _editor.Children[0].Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        _empty.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        _empty.Text = "Pick a document on the left, or ＋ Document to start one.\n\nTip: right-click a host or an infrastructure provider → 📄 Docs to see what's pinned to it.";
    }

    private void ShowMeta()
    {
        if (_doc == null) return;
        _where.Text = "📁 " + (_doc.Folder.Length > 0 ? _doc.Folder : "Unfiled");
        _pins.Children.Clear();
        if (_doc.Pins.Count == 0) _pins.Children.Add(new TextBlock { Text = "nothing", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        foreach (var p in _doc.Pins.ToList())
        {
            var pin = p;
            var chip = Btn(DocStore.PinLabel(p) + "  ✕", () => { if (_doc != null) { DocStore.Unpin(_doc, pin); ShowMeta(); } }, tip: "Unpin");
            _pins.Children.Add(chip);
        }
    }

    private void SaveNow()
    {
        _autosave.Stop();
        if (_doc == null || _loading || _doc.Deleted != null) return;
        var title = _title.Text.Trim().Length > 0 ? _title.Text.Trim() : "Untitled document";
        // Still untitled: the document's first heading names it (the title box follows; typing a title stops this).
        if (title == "Untitled document" && Regex.Match(_body.Text, @"^#\s+(.+?)\s*$", RegexOptions.Multiline) is { Success: true } h1 && h1.Groups[1].Value != title)
        {
            title = h1.Groups[1].Value.Length > 120 ? h1.Groups[1].Value[..120] : h1.Groups[1].Value;
            _loading = true; _title.Text = title; _loading = false;
        }
        if (title == _doc.Title && _body.Text == _doc.Body) return;
        _doc.Title = title;
        _doc.Body = _body.Text;
        try
        {
            DocStore.Save(_doc, _baseTitle, _baseBody);
            _baseTitle = _doc.Title; _baseBody = _doc.Body;
            Status($"Saved {DateTime.Now:HH:mm:ss} · {Counts(_doc.Body)} · encrypted");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException) { Status("⚠ Not saved: " + ex.Message); }
    }

    private void TrashOrRestore()
    {
        if (_doc == null) return;
        if (_doc.Deleted != null) { DocStore.Restore(_doc); Open(_doc); Status("Restored."); return; }
        SaveNow();
        DocStore.Delete(_doc);
        _doc = null;
        ShowEditor(false);
        Refresh();
        Status("Moved to the Trash — restore it from 🗑 Trash.");
    }

    private void MoveDoc()
    {
        if (_doc == null) return;
        var folders = new List<string> { "(Unfiled)" };
        folders.AddRange(DocStore.Folders());
        if (Ui.Dialogs.Pick("Move document", $"Move “{_doc.Title}” to which folder?", folders, f => f) is not { } to) return;
        DocStore.Move(_doc, to == "(Unfiled)" ? "" : to);
        _folder = _doc.Folder;
        Refresh();
        ShowMeta();
    }

    // ---- pins --------------------------------------------------------------------------------------------------------

    private sealed record PinChoice(string Pin, string Label);

    private void ShowPinMenu()
    {
        if (_doc == null) return;
        var menu = new ContextMenu();
        Menus.Fill(menu.Items, new object?[]
        {
            Menus.Item("🖥 A host…", () => PickPin(AppData.Current.Hosts.Where(h => !h.IsShared && !h.IsAdHoc).OrderBy(h => h.Display, StringComparer.OrdinalIgnoreCase).Select(h => new PinChoice("host:" + h.Id, $"{h.Display}  ({h.Endpoint})")), "a host")),
            Menus.Item("📁 A host folder (every host in it)…", () => PickPin(AppData.Current.Hosts.Select(h => AppData.NormalizeFolder(h.Group)).Concat(AppData.Current.Folders.Select(AppData.NormalizeFolder))
                .Where(f => f.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f).Select(f => new PinChoice("hostfolder:" + f, f)), "a host folder")),
            Menus.Item("🗄 Infrastructure (cluster, hypervisor, Docker…)…", () => PickPin(AppData.Current.InfraProviders.OrderBy(p => p.Name).Select(p => new PinChoice("infra:" + p.Id, $"{p.Name}  ({p.Kind})")), "infrastructure")),
        });
        menu.IsOpen = true;
    }

    private void PickPin(IEnumerable<PinChoice> choices, string what)
    {
        if (_doc == null) return;
        var list = choices.Where(c => !_doc.Pins.Contains(c.Pin)).ToList();
        if (list.Count == 0) { Ui.Dialogs.Info("Pin to", $"There's no {what} to pin to (or the document is pinned to all of them)."); return; }
        if (Ui.Dialogs.Pick("Pin to", $"Pin “{_doc.Title}” to {what}:", list, c => c.Label) is not { } pick) return;
        DocStore.Pin(_doc, pick.Pin);
        ShowMeta();
        Refresh();
    }

    // ---- the editor --------------------------------------------------------------------------------------------------

    private void ApplyMode()
    {
        var mode = (_mode.SelectedItem as ComboBoxItem)?.Tag as string ?? "split";
        _panes.ColumnDefinitions[0].Width = mode == "preview" ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        _panes.ColumnDefinitions[2].Width = mode == "edit" ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        _body.Visibility = mode == "preview" ? Visibility.Collapsed : Visibility.Visible;
        if (mode != "edit") RenderPreview();
    }

    private void RenderPreview()
    {
        if (_doc == null || ((_mode.SelectedItem as ComboBoxItem)?.Tag as string) == "edit") return;
        var sw = Stopwatch.StartNew();
        _preview.Document = MarkdownRenderer.Render(_body.Text, OpenLink);
        LastRenderMs = sw.Elapsed.TotalMilliseconds;
    }

    /// <summary>Self-test: how long the last preview took.</summary>
    internal double LastRenderMs { get; private set; }

    private static void OpenLink(string link)
    {
        void Go() { if (Ui.Dialogs.Confirm("Open link", $"Open this link?\n\n{link}", "Open")) Process.Start(new ProcessStartInfo(link) { UseShellExecute = true }); }
        if (link.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) Offline.Run(link, "a link in a document", Go);   // offline mode first (ADR-016)
        else if (link.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) Go();
    }

    private void FillOutline()
    {
        _outline.Items.Clear();
        foreach (var (level, text, line) in MarkdownRenderer.Outline(_body.Text))
            _outline.Items.Add(new ComboBoxItem { Content = new string(' ', (level - 1) * 3) + text, Tag = line });
        if (_outline.Items.Count == 0) _outline.Items.Add(new ComboBoxItem { Content = "(no headings)", IsEnabled = false });
    }

    /// <summary>Jumps to a logical line (the outline's line numbers count text lines, not wrapped screen lines).</summary>
    private void GoToLine(int line)
    {
        var t = _body.Text;
        int at = 0;
        for (int i = 0; i < line && at >= 0; i++) { at = t.IndexOf('\n', at); if (at >= 0) at++; }
        if (at < 0) return;
        _body.Focus();
        _body.CaretIndex = at;
        var rect = _body.GetRectFromCharacterIndex(at);
        if (!rect.IsEmpty) _body.ScrollToVerticalOffset(_body.VerticalOffset + rect.Top - 20);
    }

    /// <summary>The logical line around <paramref name="at"/>: its start and its text (without the line break). Works on the
    /// text itself, so it's right before layout and with word wrap (the TextBox line APIs count wrapped screen lines).</summary>
    private (int Start, string Text) LineAt(int at)
    {
        var t = _body.Text;
        at = Math.Clamp(at, 0, t.Length);
        int s = at == 0 ? 0 : t.LastIndexOf('\n', at - 1) + 1;
        int e = t.IndexOf('\n', at);
        if (e < 0) e = t.Length;
        return (s, t[s..e].TrimEnd('\r'));
    }

    private void OnEditorKey(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (ctrl && e.Key == Key.B) { e.Handled = true; Wrap("**", "**"); }
        else if (ctrl && e.Key == Key.I) { e.Handled = true; Wrap("*", "*"); }
        else if (ctrl && e.Key == Key.K) { e.Handled = true; InsertLink(); }
        else if (ctrl && e.Key == Key.S) { e.Handled = true; SaveSnapshot(); }
        else if (ctrl && e.Key == Key.Enter) { e.Handled = true; ToggleTask(); }
        else if (e.Key == Key.Tab) { e.Handled = true; Indent(!shift); }
        else if (e.Key == Key.Enter && !ctrl && !shift) { if (ContinueList()) e.Handled = true; }
    }

    private void SaveSnapshot()
    {
        if (_doc == null) return;
        _doc.Title = _title.Text.Trim().Length > 0 ? _title.Text.Trim() : _doc.Title;
        _doc.Body = _body.Text;
        DocStore.Save(_doc, _baseTitle, _baseBody, snapshot: true);
        _baseTitle = _doc.Title; _baseBody = _doc.Body;
        Status($"Saved with a revision {DateTime.Now:HH:mm:ss} · {Counts(_doc.Body)}");
    }

    private static readonly Regex ListLine = new(@"^(\s*)([-*+]|\d{1,9}[.)])(\s+)(\[[ xX]\]\s+)?(.*)$");

    /// <summary>Enter on a list line continues the list (numbers count up, tasks start unticked); on an empty item it ends it.</summary>
    private bool ContinueList()
    {
        var (start, line) = LineAt(_body.CaretIndex);
        if (_body.CaretIndex != start + line.Length) return false;   // only at the end of the line
        var m = ListLine.Match(line);
        if (!m.Success) return false;
        if (m.Groups[5].Value.Trim().Length == 0)
        {
            _body.Select(start, line.Length);
            _body.SelectedText = "";
            return true;
        }
        var marker = m.Groups[2].Value;
        if (char.IsDigit(marker[0]) && int.TryParse(marker[..^1], out var n)) marker = (n + 1) + marker[^1..];
        Insert(Environment.NewLine + m.Groups[1].Value + marker + m.Groups[3].Value + (m.Groups[4].Success ? "[ ] " : ""));
        return true;
    }

    private void ToggleTask()
    {
        var (start, line) = LineAt(_body.CaretIndex);
        var m = Regex.Match(line, @"\[( |x|X)\]");
        if (!m.Success) { LinePrefix("- [ ] "); return; }
        var caret = _body.CaretIndex;
        _body.Select(start + m.Index, 3);
        _body.SelectedText = m.Groups[1].Value == " " ? "[x]" : "[ ]";
        _body.CaretIndex = caret;
    }

    private void Indent(bool more)
    {
        var (start, line) = LineAt(_body.CaretIndex);
        var caret = _body.CaretIndex;
        if (more) { _body.Select(start, 0); _body.SelectedText = "  "; _body.CaretIndex = caret + 2; return; }
        int remove = line.StartsWith("  ") ? 2 : line.StartsWith(' ') || line.StartsWith('\t') ? 1 : 0;
        if (remove == 0) return;
        _body.Select(start, remove); _body.SelectedText = ""; _body.CaretIndex = Math.Max(start, caret - remove);
    }

    private void Insert(string text)
    {
        if (_doc == null || _body.IsReadOnly) return;
        var at = _body.CaretIndex;
        _body.SelectedText = text;
        _body.CaretIndex = at + text.Length;
        _body.SelectionLength = 0;
        _body.Focus();
    }

    private void Wrap(string before, string after)
    {
        if (_doc == null || _body.IsReadOnly) return;
        var sel = _body.SelectedText;
        var at = _body.SelectionStart;
        _body.SelectedText = before + sel + after;
        if (sel.Length == 0) _body.CaretIndex = at + before.Length; else _body.Select(at + before.Length, sel.Length);
        _body.Focus();
    }

    private void LinePrefix(string prefix)
    {
        if (_doc == null || _body.IsReadOnly) return;
        var (start, _) = LineAt(_body.CaretIndex);
        var caret = _body.CaretIndex;
        _body.Select(start, 0);
        _body.SelectedText = prefix;
        _body.CaretIndex = caret + prefix.Length;
        _body.Focus();
    }

    private void InsertLink()
    {
        var sel = _body.SelectedText;
        if (Ui.Dialogs.Prompt("Link", "Address (https://… or mailto:…):", "https://") is not { } url || url.Trim().Length == 0) return;
        _body.SelectedText = $"[{(sel.Length > 0 ? sel : "text")}]({url.Trim()})";
        _body.Focus();
    }

    private void ShowHistory()
    {
        if (_doc == null) return;
        SaveNow();
        if (_doc.Revisions.Count == 0) { Ui.Dialogs.Info("History", "No earlier versions yet: a version is kept every 10 minutes while you edit, and on Ctrl+S."); return; }
        var revs = Enumerable.Reverse(_doc.Revisions).ToList();
        if (Ui.Dialogs.Pick("History", $"Earlier versions of “{_doc.Title}” (newest first). Restoring keeps the current text as a version too.", revs,
                r => $"{r.At:yyyy-MM-dd HH:mm} · {r.Title} · {Counts(r.Body)}") is not { } pick) return;
        var (t, b) = (_doc.Title, _doc.Body);
        _doc.Title = pick.Title; _doc.Body = pick.Body;
        DocStore.Save(_doc, t, b, snapshot: true);
        Open(_doc);
        Status($"Restored the version from {pick.At:yyyy-MM-dd HH:mm}.");
    }

    private void Import()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Markdown|*.md;*.markdown|Text|*.txt|All files|*.*", Multiselect = true, Title = "Import Markdown files" };
        if (dlg.ShowDialog() != true) return;
        Doc? last = null;
        foreach (var path in dlg.FileNames)
        {
            try
            {
                if (new FileInfo(path).Length > DocStore.MaxBodyChars * 2L) { Ui.Dialogs.Error("Import", $"{Path.GetFileName(path)} is too large."); continue; }
                var text = File.ReadAllText(path);
                var title = Regex.Match(text, @"^#\s+(.+)$", RegexOptions.Multiline) is { Success: true } h ? h.Groups[1].Value.Trim() : Path.GetFileNameWithoutExtension(path);
                last = DocStore.Create(title, _folder, null, text);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Ui.Dialogs.Error("Import", $"{Path.GetFileName(path)}: {ex.Message}"); }
        }
        Refresh();
        if (last != null) Open(last);
    }

    // ---- small helpers -----------------------------------------------------------------------------------------------

    private static string Counts(string body) =>
        $"{Regex.Matches(body, @"\S+").Count:N0} words · {body.Length:N0} characters";

    private static string SafeName(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim() is { Length: > 0 } n ? n : "document";

    private void Status(string text) => _status.Text = text;

    private Button Btn(string text, Action a, bool primary = false, bool danger = false, string? tip = null)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 6, 6), ToolTip = tip, Style = (Style)FindResource(primary ? "PrimaryButton" : danger ? "DangerButton" : "SmallButton") };
        b.Click += (_, _) => a();
        return b;
    }

    private TextBlock Lbl(string text, double left = 0) => new() { Text = text, Style = (Style)FindResource("Label"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(left, 0, 8, 0) };

    // ---- self-test hooks ---------------------------------------------------------------------------------------------

    internal Doc? OpenDocForTest => _doc;
    internal string BodyForTest { get => _body.Text; set => _body.Text = value; }
    internal void CaretToEndForTest() => _body.CaretIndex = _body.Text.Length;
    internal bool ContinueListForTest() => ContinueList();
    internal void SaveNowForTest() => SaveNow();
    internal FlowDocumentScrollViewer PreviewForTest => _preview;
    internal void ModeForTest(string mode) => _mode.SelectedItem = _mode.Items.OfType<ComboBoxItem>().First(i => (string)i.Tag == mode);

    public void Dispose()
    {
        SaveNow();
        _autosave.Stop(); _render.Stop(); _searchDelay.Stop();
        if (Current == this) Current = null;
    }
}
