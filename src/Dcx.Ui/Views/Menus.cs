using System.Collections;
using System.IO;
using System.Text;
using DcxManager.Services;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace DcxManager.Views;

/// <summary>
/// Right-click menus everywhere. Menus are built lazily when they open, so they always reflect the current selection
/// and state. Lists get a menu that mirrors every toolbar button of their view (so anything you can click you can
/// right-click), plus copy/export of rows and cells.
/// </summary>
public static class Menus
{
    public static MenuItem Item(string header, Action click, string? gesture = null, bool enabled = true, string? tip = null)
    {
        var m = new MenuItem { Header = header, InputGestureText = gesture ?? "", IsEnabled = enabled, ToolTip = tip };
        m.Click += (_, _) =>
        {
            try { click(); }
            catch (Exception ex) { Log.Write("Menu " + header, ex); Dialogs.Error(header, ex.Message); }
        };
        return m;
    }

    public static MenuItem AsyncItem(string header, Func<Task> click, string? gesture = null, bool enabled = true) =>
        Item(header, () => _ = Guard(header, click), gesture, enabled);

    /// <summary>An "✦ … with AI" item (0.2.0 phase 4): only when an assistant is registered — otherwise null, so the menu
    /// has no such item at all — and enabled when <paramref name="enabled"/> and the assistant may be used now.</summary>
    public static MenuItem? AssistItem(string header, Func<Services.IAssistant, Task> run, bool enabled = true, Services.AssistUse use = Services.AssistUse.Files) =>
        Services.Assist.Current is { } a ? AsyncItem(header, () => run(a), enabled: enabled && a.WhyNot(use) == null) : null;

    private static async Task Guard(string header, Func<Task> f)
    {
        try { await f(); }
        catch (Exception ex) { Log.Write("Menu " + header, ex); Dialogs.Error(header, ex.Message); }
    }

    public static MenuItem Sub(string header, params object?[] items)
    {
        var m = new MenuItem { Header = header };
        Fill(m.Items, items);
        if (m.Items.Count == 0) m.IsEnabled = false;
        return m;
    }

    public static Separator Sep() => new();

    /// <summary>
    /// A named group of related items (0.2.1). In a short menu its items show inline between separators; once a menu has
    /// more than <see cref="GroupThreshold"/> rows, every group with two or more items becomes an expandable submenu under
    /// its name, so the top level keeps the main actions and a few headings. It enumerates its items, so code that walks a
    /// menu's items (the self-test's sweeps) sees inside it.
    /// </summary>
    public sealed class MenuGroup(string name, object?[] items) : IEnumerable<object?>
    {
        public string Name { get; } = name;
        public object?[] Items { get; } = items;
        public IEnumerator<object?> GetEnumerator() => ((IEnumerable<object?>)Items).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public static MenuGroup Group(string name, params object?[] items) => new(name, items);

    /// <summary>Rows above which a menu folds its groups into submenus.</summary>
    public const int GroupThreshold = 12;

    /// <summary>Adds items, skipping nulls, flattening sequences, folding long menus' groups into submenus, and collapsing
    /// leading/trailing/double separators.</summary>
    public static void Fill(ItemCollection target, IEnumerable<object?> items)
    {
        var list = Flat(items);
        bool fold = Rows(list) > GroupThreshold;
        FillInto(target, list, fold);
        while (target.Count > 0 && target[^1] is Separator) target.RemoveAt(target.Count - 1);
    }

    /// <summary>The items once, with inline sequences spliced in and each group's items made concrete (a builder's lazy
    /// sequences are enumerated exactly once).</summary>
    private static List<object?> Flat(IEnumerable<object?> items)
    {
        var list = new List<object?>();
        foreach (var i in items)
        {
            if (i == null) continue;
            if (i is MenuGroup g) list.Add(new MenuGroup(g.Name, Flat(g.Items).ToArray()));
            else if (i is IEnumerable<object?> seq && i is not string) list.AddRange(Flat(seq));
            else list.Add(i);
        }
        return list;
    }

    private static void FillInto(ItemCollection target, IEnumerable<object?> items, bool fold)
    {
        foreach (var i in items)
        {
            if (i == null) continue;
            if (i is MenuGroup g)
            {
                var present = g.Items.Where(x => x != null).ToList();
                if (present.All(x => x is Separator)) continue;
                if (fold && present.Count(x => x is not Separator) >= 2) { Add(target, Sub(g.Name, present.ToArray())); continue; }
                Add(target, Sep()); FillInto(target, present, fold); Add(target, Sep());
                continue;
            }
            Add(target, i);
        }
    }

    private static void Add(ItemCollection target, object item)
    {
        if (item is Separator && (target.Count == 0 || target[^1] is Separator)) return;
        target.Add(item);
    }

    /// <summary>Rows a menu would show without folding: items, inline sequences and groups counted item by item.</summary>
    private static int Rows(IEnumerable<object?> items)
    {
        int n = 0;
        foreach (var i in items)
        {
            if (i == null || i is Separator) continue;
            n += i is MenuGroup g ? Rows(g.Items) : 1;
        }
        return n;
    }

    /// <summary>The builder <see cref="Attach"/> gave an element, so the self-test can build every right-click menu of a
    /// screen without opening a popup (0.2.0 audit: the AI-off sweep checks their items too).</summary>
    internal static readonly DependencyProperty BuilderProperty =
        DependencyProperty.RegisterAttached("Builder", typeof(Func<IEnumerable<object?>>), typeof(Menus));
    internal static Func<IEnumerable<object?>>? BuilderOf(DependencyObject d) => d.GetValue(BuilderProperty) as Func<IEnumerable<object?>>;

    /// <summary>Attaches a menu built on every open. Return no items to suppress it.</summary>
    public static void Attach(FrameworkElement el, Func<IEnumerable<object?>> build)
    {
        var menu = new ContextMenu();
        el.ContextMenu = menu;
        el.SetValue(BuilderProperty, build);
        el.ContextMenuOpening += (_, e) =>
        {
            menu.Items.Clear();
            try { Fill(menu.Items, build()); }
            catch (Exception ex) { Log.Write("Context menu", ex); }
            if (menu.Items.Count == 0) e.Handled = true;
        };
    }

    /// <summary>A button whose left click opens the same menu as its right click, below it.</summary>
    /// <remarks>Never open an <see cref="Attach"/>ed menu with <c>ContextMenu.IsOpen = true</c>: that skips
    /// ContextMenuOpening, so the menu opens empty (the Infrastructure "＋ Add ▾" button did nothing until 0.1.5).</remarks>
    public static void AttachDropDown(Button button, Func<IEnumerable<object?>> build)
    {
        Attach(button, build);
        button.Click += (_, _) =>
        {
            IEnumerable<object?> items;
            try { items = build().ToList(); }
            catch (Exception ex) { Log.Write("Menu", ex); return; }
            ShowBelow(button, items);
        };
    }

    /// <summary>Shows a menu right now at the mouse (used where the native menu can't be attached, e.g. inside WebView2).</summary>
    public static ContextMenu? ShowAt(UIElement target, IEnumerable<object?> items)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.MousePoint };
        Fill(menu.Items, items);
        if (menu.Items.Count == 0) return null;
        // A popup is a window on the screen: the self-test records the menu instead of opening it.
        if (TestMode.Running) { lock (ShownForTests) ShownForTests.Add(menu.Items.OfType<MenuItem>().Select(m => m.Header?.ToString() ?? "").ToArray()); return menu; }
        menu.IsOpen = true;
        return menu;
    }

    /// <summary>Menus <see cref="ShowBelow"/> would have opened during the self-test.</summary>
    internal static readonly List<string[]> ShownForTests = new();

    /// <summary>Opens a menu under a button (the "More ▾" pattern).</summary>
    public static void ShowBelow(FrameworkElement button, IEnumerable<object?> items)
    {
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        Fill(menu.Items, items);
        // A popup is a window on the screen: the self-test records the menu instead of opening it.
        if (TestMode.Running) { lock (ShownForTests) ShownForTests.Add(menu.Items.OfType<MenuItem>().Select(m => m.Header?.ToString() ?? "").ToArray()); return; }
        if (menu.Items.Count > 0) menu.IsOpen = true;
    }

    /// <summary>
    /// A toolbar "More ▾" button. Its items are built when it opens, and <see cref="MirrorButtons"/> folds them into the
    /// list's right-click menu too, so nothing is reachable only through the dropdown.
    /// </summary>
    public static Button MoreButton(Func<IEnumerable<object?>> items, string label = "More ▾", string? style = null)
    {
        var b = new Button { Content = label, Tag = items, ToolTip = "More actions" };
        if (style != null && Application.Current.TryFindResource(style) is Style s) b.Style = s;
        b.Click += (_, _) => ShowBelow(b, items());
        return b;
    }

    // ---- lists ----------------------------------------------------------------------------------------------------

    /// <summary>Right-click on a row selects it first (keeping a multi-selection if the row is part of it).</summary>
    public static void SelectOnRightClick(Selector list)
    {
        list.PreviewMouseRightButtonDown += (_, e) =>
        {
            if (e.OriginalSource is not DependencyObject d) return;
            var container = Ancestor<ListBoxItem>(d);
            if (container == null) return;
            if (!container.IsSelected)
            {
                if (list is ListBox lb && lb.SelectionMode != SelectionMode.Single && (Keyboard.Modifiers & ModifierKeys.Control) != 0) container.IsSelected = true;
                else list.SelectedItem = list.ItemContainerGenerator.ItemFromContainer(container);
            }
            container.Focus();
        };
    }

    /// <summary>
    /// Gives a list a menu: <paramref name="extra"/> (row-specific items) first, then every visible toolbar button found in
    /// <paramref name="scope"/> (outside the list), then copy/export. One line per view.
    /// </summary>
    /// <remarks>0.2.1: the menu is grouped. By default the row items come first and the screen's toolbar and the copy /
    /// export items are groups, so a long menu folds them into "Screen actions ▸" and "Copy &amp; export ▸". Screens whose
    /// toolbar holds the row's real actions (cloud and infrastructure lists) pass <paramref name="extraGroup"/>: the
    /// toolbar's actions stay at the top and the row items become that group.</remarks>
    public static void ForList(Selector list, FrameworkElement scope, Func<IEnumerable<object?>>? extra = null, string? extraGroup = null)
    {
        SelectOnRightClick(list);
        Attach(list, () =>
        {
            var items = new List<object?>();
            if (extraGroup != null)
            {
                items.AddRange(MirrorButtons(scope, list));
                items.Add(Sep());
                if (extra != null) items.Add(Group(extraGroup, extra().ToArray()));
            }
            else
            {
                if (extra != null) { items.AddRange(extra()); items.Add(Sep()); }
                items.Add(Group("Screen actions", MirrorButtons(scope, list).ToArray()));
            }
            items.Add(Group("Copy & export", CopyItems(list).ToArray()));
            return items;
        });
    }

    /// <summary>Menu items that click the view's buttons. Danger buttons go last, after a separator.</summary>
    public static IEnumerable<object?> MirrorButtons(FrameworkElement scope, DependencyObject? exclude = null)
    {
        var normal = new List<object?>(); var danger = new List<object?>();
        Panel? lastPanel = null;
        foreach (var b in Descendants<ButtonBase>(scope))
        {
            if (b is ToggleButton and not CheckBox and not RadioButton) continue;
            if (b is CheckBox or RadioButton || !b.IsVisible || (exclude != null && IsInside(b, exclude))) continue;
            if (b.Tag is Func<IEnumerable<object?>> more)
            {
                // A "More ▾" dropdown: inline its items instead of an item that would just open the dropdown.
                try { normal.Add(Sep()); normal.AddRange(more()); normal.Add(Sep()); } catch (Exception ex) { Log.Write("More menu", ex); }
                continue;
            }
            var header = Label(b);
            if (header == null) continue;
            bool isDanger = b.Style != null && Application.Current.TryFindResource("DangerButton") is Style ds && b.Style == ds;
            var target = isDanger ? danger : normal;
            if (!isDanger && b.Parent is Panel p && lastPanel != null && p != lastPanel) target.Add(Sep());
            if (!isDanger && b.Parent is Panel p2) lastPanel = p2;
            var btn = b;
            target.Add(Item(header, () => btn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, btn)), enabled: btn.IsEnabled, tip: btn.ToolTip as string));
        }
        if (danger.Count > 0) { normal.Add(Sep()); normal.AddRange(danger); }
        return normal;
    }

    private static string? Label(ButtonBase b)
    {
        var text = b.Content switch
        {
            string s => s,
            TextBlock t => t.Text,
            _ => b.ToolTip as string,
        };
        if (string.IsNullOrWhiteSpace(text) || text.Trim() is "×" or "⋯" or "…") return b.ToolTip as string ?? null;
        // Short glyph-only buttons ("A+", "⚙") read better with their tooltip.
        if (text.Trim().Length <= 2 && b.ToolTip is string tip) return $"{text.Trim()}  {tip}";
        return text.Replace("_", "__");
    }

    public static IEnumerable<object?> CopyItems(Selector list)
    {
        var selected = SelectedItems(list);
        var columns = (list as ListView)?.View is GridView gv ? gv.Columns.ToList() : new List<GridViewColumn>();
        var row = selected.FirstOrDefault();
        yield return Item(selected.Count > 1 ? $"Copy {selected.Count} rows" : "Copy row", () => Clip(string.Join("\n", selected.Select(i => RowText(i, columns)))), "Ctrl+C", selected.Count > 0);
        if (row != null && columns.Count > 1)
            yield return Sub("Copy cell", columns.Select(c => (object?)Item(Header(c) + ":  " + Short(CellText(row, c)), () => Clip(CellText(row, c)))).ToArray());
        yield return Item("Copy all rows (TSV)", () => Clip(AllText(list, columns, "\t")));
        yield return Item("Export as CSV…", () => ExportCsv(list, columns));
        yield return Item("Share as CSV…", () => Services.ShareService.ShareText(Window.GetWindow(list), "Table export", Csv(list, columns), $"export-{DateTime.Now:yyyyMMdd-HHmm}.csv"), enabled: Services.ShareService.Supported && list.Items.Count > 0);
        yield return Sep();
        yield return Item("Select all", () => { if (list is ListBox lb && lb.SelectionMode != SelectionMode.Single) lb.SelectAll(); }, "Ctrl+A", list is ListBox { SelectionMode: not SelectionMode.Single });
    }

    public static List<object> SelectedItems(Selector list) =>
        list is ListBox lb && lb.SelectionMode != SelectionMode.Single ? lb.SelectedItems.Cast<object>().ToList()
        : list.SelectedItem is { } s ? new List<object> { s } : new List<object>();

    private static string Header(GridViewColumn c) => c.Header?.ToString() ?? "";

    public static string CellText(object item, GridViewColumn c)
    {
        if (c.DisplayMemberBinding is not Binding b || b.Path?.Path is not { } path) return "";
        try
        {
            if (path.StartsWith('[') && item is IList list && int.TryParse(path.Trim('[', ']'), out var idx)) return idx < list.Count ? list[idx]?.ToString() ?? "" : "";
            object? cur = item;
            foreach (var part in path.Split('.'))
            {
                if (cur == null) break;
                cur = cur.GetType().GetProperty(part)?.GetValue(cur);
            }
            return b.StringFormat != null && cur != null ? string.Format(b.StringFormat, cur) : cur?.ToString() ?? "";
        }
        catch { return ""; }
    }

    private static string RowText(object item, List<GridViewColumn> cols) =>
        cols.Count == 0 ? item.ToString() ?? "" : string.Join("\t", cols.Select(c => CellText(item, c)));

    private static string AllText(Selector list, List<GridViewColumn> cols, string sep)
    {
        var sb = new StringBuilder();
        if (cols.Count > 0) sb.AppendLine(string.Join(sep, cols.Select(Header)));
        foreach (var i in list.Items) sb.AppendLine(cols.Count == 0 ? i?.ToString() : string.Join(sep, cols.Select(c => CellText(i!, c))));
        return sb.ToString();
    }

    private static string Csv(Selector list, List<GridViewColumn> cols)
    {
        var sb = new StringBuilder();
        if (cols.Count > 0) sb.AppendLine(string.Join(",", cols.Select(c => CsvCell(Header(c)))));
        foreach (var i in list.Items) sb.AppendLine(cols.Count == 0 ? CsvCell(i?.ToString() ?? "") : string.Join(",", cols.Select(c => CsvCell(CellText(i!, c)))));
        return sb.ToString();
    }

    /// <summary>One CSV cell. Quoted when it needs it, and a cell that a spreadsheet would read as a formula ("=", "+", "-",
    /// "@", tab, CR first) gets a leading apostrophe: exported text came from servers and logs, and must open as text.</summary>
    public static string CsvCell(string s)
    {
        if (s.Length > 0 && s[0] is '=' or '+' or '-' or '@' or '\t' or '\r') s = "'" + s;
        return s.IndexOfAny(new[] { ',', '"', '\n', '\r', '\'' }) >= 0 || s.Length == 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    private static void ExportCsv(Selector list, List<GridViewColumn> cols)
    {
        var dlg = new SaveFileDialog { FileName = $"export-{DateTime.Now:yyyyMMdd-HHmm}.csv", Filter = "CSV|*.csv" };
        if (dlg.ShowDialog() != true) return;
        if (!Services.StorageGuard.ConfirmDestination(Path.GetDirectoryName(dlg.FileName)!, "Export CSV", encrypted: false)) return;
        File.WriteAllText(dlg.FileName, Csv(list, cols), new UTF8Encoding(true));
        Ui.Window.Status($"Exported {list.Items.Count} rows (plain text, not encrypted) → {dlg.FileName}");
    }

    // ---- text -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Every text box gets the app's own menu (0.5.2, DarkCipher0x: "make it the apps own code"). A right-click in a 0.5.1 LOGS
    /// screen opened WPF's built-in text box menu inside the main window, and afterwards nothing in the app took a click
    /// until it was killed. Installed once at start-up: a text box without a menu of its own gets the editing menu
    /// (<see cref="EnsureTextMenu"/>) the first time it is right-clicked (this class handler runs before WPF's TextBoxBase one, so the built-in menu
    /// never opens). Boxes with their own menu (<see cref="Attach"/>, or a ContextMenu set by the caller) are left alone.
    /// </summary>
    public static void InstallTextMenus()
    {
        if (_textMenus) return;
        _textMenus = true;
        EventManager.RegisterClassHandler(typeof(TextBox), FrameworkElement.ContextMenuOpeningEvent, new ContextMenuEventHandler((s, _) => { if (s is TextBox tb) EnsureTextMenu(tb); }));
    }

    private static bool _textMenus;

    /// <summary>Gives <paramref name="box"/> the app's editing menu, filled now, unless it already has a menu. Editing
    /// commands only — no Save as or Share: a box given the menu automatically may hold a secret (a key typed into a plain
    /// field, recovery codes), so nothing here writes or sends its text anywhere WPF's own menu didn't.</summary>
    internal static void EnsureTextMenu(TextBox box)
    {
        if (box.ContextMenu != null || BuilderOf(box) != null) return;
        box.IsInactiveSelectionHighlightEnabled = true;
        Attach(box, () =>
        {
            var items = EditItems(box);
            if (box.IsReadOnly) { items.Add(Sep()); items.Add(Item("Find…", () => Find(box))); }
            return items;
        });
        var menu = box.ContextMenu!;
        menu.Items.Clear();
        try { Fill(menu.Items, BuilderOf(box)!()); }
        catch (Exception ex) { Log.Write("Context menu", ex); }
    }

    /// <summary>Copy (the highlighted text), Cut, Paste, Copy all, Select all — what every text menu starts with.</summary>
    private static List<object?> EditItems(TextBox box)
    {
        var sel = box.SelectedText;
        return new List<object?>
        {
            Item("Copy", () => Clip(sel), "Ctrl+C", sel.Length > 0),
            box.IsReadOnly ? null : Item("Cut", () => box.Cut(), "Ctrl+X", sel.Length > 0),
            box.IsReadOnly ? null : Item("Paste", () => box.Paste(), "Ctrl+V", ClipboardHasText()),
            Item("Copy all", () => Clip(box.Text), enabled: box.Text.Length > 0),
            Item("Select all", () => { box.Focus(); box.SelectAll(); }, "Ctrl+A"),
        };
    }

    /// <summary>A read-only/plain text box menu: copy, select all, save, find, plus caller items (e.g. AI on the selection).</summary>
    public static void ForText(TextBox box, Func<string, IEnumerable<object?>>? extra = null)
    {
        // Highlight, right-click, Copy (DarkCipher0x, 0.5.2): the highlight stays visible while the menu has the focus.
        box.IsInactiveSelectionHighlightEnabled = true;
        Attach(box, () =>
        {
            var sel = box.SelectedText;
            var items = EditItems(box);
            items.AddRange(new object?[]
            {
                Sep(),
                Item("Find…", () => Find(box), "Ctrl+F"),
                Item("Save as…", () => SaveText(box.Text, "output.txt")),
                Item("Share…", () => Services.ShareService.ShareText(Window.GetWindow(box), "Output", sel.Length > 0 ? sel : box.Text, $"output-{DateTime.Now:yyyyMMdd-HHmm}.txt"), enabled: Services.ShareService.Supported && box.Text.Length > 0, tip: "Shares the selection, or everything when nothing is selected"),
                Item("Wrap lines", () => box.TextWrapping = box.TextWrapping == TextWrapping.Wrap ? TextWrapping.NoWrap : TextWrapping.Wrap),
            });
            if (extra != null) { items.Add(Sep()); items.AddRange(extra(sel)); }
            return items;
        });
        box.PreviewKeyDown += (_, e) => { if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; Find(box); } };
    }

    private static string _lastFind = "";

    public static void Find(TextBox box)
    {
        if (Dialogs.Prompt("Find", "Find text (case-insensitive; repeats from the cursor):", _lastFind) is not { Length: > 0 } q) return;
        _lastFind = q;
        int from = Math.Min(box.Text.Length, box.SelectionStart + box.SelectionLength);
        int i = box.Text.IndexOf(q, from, StringComparison.OrdinalIgnoreCase);
        if (i < 0) i = box.Text.IndexOf(q, StringComparison.OrdinalIgnoreCase);
        if (i < 0) { Ui.Window.Status($"“{q}” not found"); return; }
        box.Focus();
        box.Select(i, q.Length);
        var line = box.GetLineIndexFromCharacterIndex(i);
        if (line >= 0) box.ScrollToLine(line);
    }

    public static void SaveText(string text, string name, string filter = "Text|*.txt|All files|*.*")
    {
        var dlg = new SaveFileDialog { FileName = name, Filter = filter };
        if (dlg.ShowDialog() != true) return;
        if (!Services.StorageGuard.ConfirmDestination(Path.GetDirectoryName(dlg.FileName)!, "Save as", encrypted: false)) return;
        File.WriteAllText(dlg.FileName, text, new UTF8Encoding(false));
        Ui.Window.Status($"Saved (plain text, not encrypted) → {dlg.FileName}");
    }

    /// <summary>Whether the clipboard holds text; false (never an exception) while another program has it open.</summary>
    public static bool ClipboardHasText() { try { return Clipboard.ContainsText(); } catch { return false; } }

    public static void Clip(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); Ui.Window.Status($"Copied {text.Length:N0} characters"); } catch { }
    }

    // ---- tree helpers ---------------------------------------------------------------------------------------------

    public static T? Ancestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T) d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    private static bool IsInside(DependencyObject d, DependencyObject container)
    {
        for (var x = d; x != null; x = x is Visual ? VisualTreeHelper.GetParent(x) ?? LogicalTreeHelper.GetParent(x) : LogicalTreeHelper.GetParent(x))
            if (x == container) return true;
        return false;
    }

    /// <summary>Logical-tree walk (works for content that isn't currently rendered, e.g. other tabs).</summary>
    public static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T t) yield return t;
            if (child is ContextMenu) continue;
            foreach (var d in Descendants<T>(child)) yield return d;
        }
    }

    private static string Short(string s) => s.Length > 40 ? s[..40] + "…" : s;
}
