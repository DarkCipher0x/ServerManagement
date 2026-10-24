using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using DcxManager.Services;
using DcxManager.Sessions;

namespace DcxManager.Views;

/// <summary>
/// Session logs (ADR-019): the encrypted terminal logs, decrypted in memory to read, search, export or delete. Logging is
/// switched on per terminal profile or from a terminal's right-click menu.
/// </summary>
public sealed class SessionLogsView : UserControl, IActivatable
{
    public sealed record Row(SessionLog.Entry Entry)
    {
        public string When => Entry.Started.ToString("yyyy-MM-dd HH:mm");
        public string Title => Entry.Title;
        public string Size => Entry.Bytes < 1024 * 1024 ? $"{Entry.Bytes / 1024.0:0.#} KB" : $"{Entry.Bytes / 1048576.0:0.#} MB";
    }

    private readonly ListView _list = new() { SelectionMode = SelectionMode.Single };
    private readonly TextBox _filter = Hint.Set(new TextBox { Padding = new Thickness(5, 3, 5, 3), ToolTip = "Filter by title" }, "Filter logs…");
    private readonly TextBox _text = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBox _find = Hint.Set(new TextBox { Width = 200, Padding = new Thickness(5, 3, 5, 3), ToolTip = "Find in this log (Enter = next)" }, "Find in this log…");
    private readonly TextBlock _status = new TextBlock { TextWrapping = TextWrapping.Wrap }.Fs(FontStep.Small);
    private List<Row> _rows = new();

    public static SessionLogsView? Current { get; private set; }

    public SessionLogsView()
    {
        Current = this;
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var root = new Grid { Margin = new Thickness(14, 10, 14, 10) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(380) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        root.ColumnDefinitions.Add(new ColumnDefinition());

        var left = new DockPanel();
        var head = new StackPanel();
        head.Children.Add(new TextBlock { Text = "Session logs", Style = (Style)FindResource("H1") });
        head.Children.Add(new TextBlock { Text = "Terminal output saved per session, encrypted like everything else. Turn logging on in a terminal profile (Settings → Terminal profiles) or from a terminal's right-click → Session log.", Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 8) });
        head.Children.Add(_filter);
        DockPanel.SetDock(head, Dock.Top);
        left.Children.Add(head);
        var gv = new GridView();
        void Col(string h, string p, double w) => gv.Columns.Add(new GridViewColumn { Header = h, Width = w, DisplayMemberBinding = new Binding(p) });
        Col("Started", nameof(Row.When), 120); Col("Session", nameof(Row.Title), 180); Col("Size", nameof(Row.Size), 60);
        _list.View = gv;
        _list.Margin = new Thickness(0, 6, 0, 0);
        left.Children.Add(_list);
        root.Children.Add(left);

        var right = new DockPanel();
        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        Button B(string t, Action a, string? tip = null, string style = "SmallButton") { var b = new Button { Content = t, ToolTip = tip, Style = (Style)FindResource(style), Margin = new Thickness(0, 0, 6, 4) }; b.Click += (_, _) => a(); bar.Children.Add(b); return b; }
        B("↻ Refresh", Reload);
        B("⇩ Export as text…", Export, "Saves a readable, NOT encrypted copy");
        B("✕ Delete", Delete, "Delete this log for good", "DangerButton");
        bar.Children.Add(_find);
        B("Find next", FindNext);
        DockPanel.SetDock(bar, Dock.Top);
        right.Children.Add(bar);
        DockPanel.SetDock(_status, Dock.Bottom);
        right.Children.Add(_status);
        _text.Mono();
        right.Children.Add(_text);
        Grid.SetColumn(right, 2);
        root.Children.Add(right);
        Content = root;

        _filter.TextChanged += (_, _) => Apply();
        _list.SelectionChanged += (_, _) => Show();
        _find.KeyDown += (_, e) => { if (e.Key == Key.Enter) { FindNext(); e.Handled = true; } };
        Menus.ForList(_list, this, () => Selected == null ? Array.Empty<object?>() : new object?[]
        {
            Menus.Item("⇩ Export as text…", Export),
            Menus.Item("✕ Delete…", Delete),
            Menus.Item("Copy session title", () => Menus.Clip(Selected!.Title)),
        });
        Menus.ForText(_text);
        Menus.ForText(_filter);
        Loaded += (_, _) => Reload();
    }

    public void Activated() => Reload();

    private Row? Selected => _list.SelectedItem as Row;
    internal int Count => _rows.Count;
    internal string ShownText => _text.Text;

    public void Reload()
    {
        var keep = Selected?.Entry.Path;
        try { _rows = SessionLog.List().Select(e => new Row(e)).ToList(); }
        catch (Exception ex) { _rows = new(); _status.Text = "✕ " + ex.Message; App.Log("Session logs list", ex); }
        Apply();
        if (keep != null) _list.SelectedItem = _list.Items.OfType<Row>().FirstOrDefault(r => r.Entry.Path == keep);
        if (_rows.Count == 0) _status.Text = "No session logs yet.";
    }

    private void Apply()
    {
        var f = _filter.Text.Trim();
        _list.ItemsSource = _rows.Where(r => f.Length == 0 || r.Title.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public void Select(string path) { Reload(); _list.SelectedItem = _list.Items.OfType<Row>().FirstOrDefault(r => r.Entry.Path == path); }

    private void Show()
    {
        if (Selected is not { } r) { _text.Text = ""; return; }
        try
        {
            _text.Text = SessionLog.Read(r.Entry.Path, 5_000_000);
            _status.Text = $"{r.Title} · started {r.Entry.Started:yyyy-MM-dd HH:mm:ss} · {r.Size} encrypted · {_text.LineCount} line(s) shown";
            _text.ScrollToEnd();
        }
        catch (Exception ex) { _text.Text = ""; _status.Text = "✕ Couldn't read this log: " + ex.Message; }
    }

    private void FindNext()
    {
        var q = _find.Text;
        if (q.Length == 0 || _text.Text.Length == 0) return;
        int from = _text.SelectionStart + _text.SelectionLength;
        int i = _text.Text.IndexOf(q, from, StringComparison.OrdinalIgnoreCase);
        if (i < 0) i = _text.Text.IndexOf(q, StringComparison.OrdinalIgnoreCase);
        if (i < 0) { _status.Text = $"“{q}” not found."; return; }
        _text.Focus(); _text.Select(i, q.Length);
        _text.ScrollToLine(_text.GetLineIndexFromCharacterIndex(i));
    }

    private void Export()
    {
        if (Selected is not { } r) return;
        if (!Dialogs.Confirm("Export session log", "The exported file is plain text — NOT encrypted. Anyone who can open the file can read the session, including anything secret that was printed.\n\nExport it?", "Choose where to save…")) return;
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = $"session-{r.Entry.Started:yyyyMMdd-HHmm}.log", Filter = "Log|*.log;*.txt|All files|*.*" };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        if (!StorageGuard.ConfirmDestination(dlg.FileName, "Session log", encrypted: false)) return;
        try
        {
            File.WriteAllText(dlg.FileName, SessionLog.Read(r.Entry.Path, int.MaxValue));
            AuditLog.Write("you", "session logs", "exported a session log as plain text", Path.GetFileName(dlg.FileName));
            _status.Text = "Exported to " + dlg.FileName;
        }
        catch (Exception ex) { Dialogs.Error("Export session log", ex.Message, explain: false); }
    }

    private void Delete()
    {
        if (Selected is not { } r) return;
        if (!Dialogs.Confirm("Delete session log", $"Delete the log “{r.Title}” from {r.When}? This can't be undone.", "Delete", danger: true)) return;
        try { SessionLog.Delete(r.Entry.Path); Reload(); }
        catch (Exception ex) { Dialogs.Error("Delete session log", ex.Message, explain: false); }
    }
}
