using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using DcxManager.Services;
using Microsoft.Win32;

namespace DcxManager.Views;

/// <summary>Every change made by you or the AI operator, decrypted on demand.</summary>
public sealed class AuditView : UserControl, IActivatable
{
    private readonly ComboBox _month = new() { Width = 120 };
    private readonly TextBox _filter = Hint.Set(new TextBox { Width = 220, Padding = new Thickness(5, 4, 5, 4) }, "Filter entries…");
    private readonly ListView _list = new();
    private List<AuditEntry> _all = new();

    public AuditView()
    {
        var root = new DockPanel { Margin = new Thickness(14, 12, 14, 12) };
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        var export = new Button { Content = "Export CSV…", Style = (Style)FindResource("SmallButton"), Margin = new Thickness(8, 0, 0, 0) };
        export.Click += (_, _) => Export();
        right.Children.Add(_month); right.Children.Add(_filter); right.Children.Add(export);
        _filter.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(right, Dock.Right);
        head.Children.Add(right);
        var t = new StackPanel();
        t.Children.Add(new TextBlock { Text = "Audit log", Style = (Style)FindResource("H1") });
        t.Children.Add(new TextBlock { Text = "Encrypted entry by entry; a tampered entry shows as unreadable.", Style = (Style)FindResource("Hint") });
        head.Children.Add(t);
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        var gv = new GridView();
        void Col(string h, string p, double w) => gv.Columns.Add(new GridViewColumn { Header = h, Width = w, DisplayMemberBinding = new Binding(p) });
        Col("Time", "TimeText", 150); Col("Who", "Actor", 50); Col("Target", "Target", 180); Col("Action", "Action", 560); Col("Result", "Result", 160);
        _list.View = gv;
        Menus.ForList(_list, this, () => new object?[]
        {
            Menus.Item("Share visible entries (CSV)…", () => ShareService.ShareText(Window.GetWindow(this), $"Audit log {_month.SelectedItem}", Csv(Visible), $"audit-{_month.SelectedItem}.csv"), enabled: ShareService.Supported && Visible.Count > 0),
            _list.SelectedItem is AuditEntry e && e.Target.Length > 0 ? Menus.Item($"Filter by “{e.Target}”", () => _filter.Text = e.Target) : null,
            _filter.Text.Length > 0 ? Menus.Item("Clear filter", () => _filter.Text = "") : null,
            Menus.AssistItem("✦ Summarise visible entries with AI", a => a.AskAsync($"audit log {_month.SelectedItem} ({Visible.Count} entries)", Csv(Visible),
                "Summarise this audit log for an operator: what was changed, by whom (you vs ai), on which hosts/resources, any failures, and anything that looks unusual or risky."),
                enabled: Visible.Count > 0),
        });
        root.Children.Add(_list);
        Content = root;

        _month.SelectionChanged += (_, _) => Load();
        _filter.TextChanged += (_, _) => Apply();
        Activated();
    }

    public void Activated()
    {
        var months = AuditLog.Months().ToList();
        var sel = _month.SelectedItem as string;
        _month.ItemsSource = months;
        _month.SelectedItem = sel != null && months.Contains(sel) ? sel : months.FirstOrDefault();
        Load();
    }

    private void Load()
    {
        _all = _month.SelectedItem is string m ? AuditLog.Read(m) : new();
        Apply();
    }

    private void Apply()
    {
        var q = _filter.Text.Trim();
        _list.ItemsSource = q.Length == 0 ? _all : _all.Where(e => $"{e.Actor} {e.Target} {e.Action} {e.Result}".Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private List<AuditEntry> Visible => _list.ItemsSource as List<AuditEntry> ?? _all;

    private static string Csv(IEnumerable<AuditEntry> entries)
    {
        static string Q(string s) => Menus.CsvCell(s);   // quoted, and never a formula when opened in a spreadsheet
        var sb = new StringBuilder("time,actor,target,action,result\n");
        foreach (var e in entries) sb.AppendLine(string.Join(",", Q(e.TimeText), Q(e.Actor), Q(e.Target), Q(e.Action), Q(e.Result)));
        return sb.ToString();
    }

    private void Export()
    {
        var dlg = new SaveFileDialog { FileName = $"audit-{_month.SelectedItem}.csv", Filter = "CSV|*.csv" };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        File.WriteAllText(dlg.FileName, Csv(_all), new UTF8Encoding(true));
        MainWindow.Instance.Status($"Exported {_all.Count} audit entries (plain CSV, not encrypted) → {dlg.FileName}");
    }
}
