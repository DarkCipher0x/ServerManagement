using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using DcxManager.Services;

namespace DcxManager.Views.Toolbox;

/// <summary>Toolbox → History: the last runs (encrypted store, newest first): open one to read its report, copy it,
/// filter by target, or clear the history.</summary>
public sealed class HistoryTab : DockPanel
{
    private readonly TextBox _filter = ToolboxUi.Input(220, "", "Filter by tool or target");
    private readonly ListView _list = new();
    private readonly TextBox _report = ToolboxUi.Output();
    private readonly TextBlock _status = ToolboxUi.Status();

    public HistoryTab()
    {
        var head = new StackPanel();
        head.Children.Add(ToolboxUi.Hint($"The last {ToolboxRun.Max} runs, kept in the encrypted store on this PC (never exported). Select one to read its report."));
        var copy = ToolboxUi.Button("Copy report"); copy.Click += (_, _) => { if (_list.SelectedItem is ToolboxRun r) Menus.Clip($"{r.Line}\n\n{r.Report}"); };
        var del = ToolboxUi.Button("✕ Delete"); del.Click += (_, _) => { if (_list.SelectedItem is ToolboxRun r) { AppData.Current.ToolboxHistory.Remove(r); AppData.Save(); Reload(); } };
        var clear = ToolboxUi.Button("Clear history"); clear.Click += (_, _) => { if (AppData.Current.ToolboxHistory.Count > 0 && Dialogs.Confirm("Toolbox history", "Delete every toolbox result?", "Delete", danger: true)) { AppData.Current.ToolboxHistory.Clear(); AppData.Save(); Reload(); } };
        _filter.TextChanged += (_, _) => Reload();
        head.Children.Add(ToolboxUi.Bar(ToolboxUi.Label("filter"), _filter, copy, del, clear));
        head.Children.Add(_status);
        SetDock(head, Dock.Top);
        Children.Add(head);
        var gv = new GridView();
        void Col(string h, string p, double w) => gv.Columns.Add(new GridViewColumn { Header = h, Width = w, DisplayMemberBinding = new Binding(p) { StringFormat = p == "When" ? "yyyy-MM-dd HH:mm" : null } });
        Col("When", "When", 130); Col("Tool", "Tool", 130); Col("Target", "Target", 260); Col("Result", "Summary", 520);
        _list.View = gv;
        _list.SelectionChanged += (_, _) => _report.Text = _list.SelectedItem is ToolboxRun r ? r.Report : "";
        Menus.ForList(_list, this, () => _list.SelectedItem is not ToolboxRun r ? Array.Empty<object?>() : new object?[]
        {
            Menus.Item("Copy report", () => Menus.Clip($"{r.Line}\n\n{r.Report}")),
            Menus.Item("Copy line", () => Menus.Clip(r.Line)),
        });
        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(_list, 0); Grid.SetRow(_report, 1); _report.Margin = new Thickness(0, 8, 0, 0);
        body.Children.Add(_list); body.Children.Add(_report);
        Children.Add(body);
        Reload();
    }

    public void Reload()
    {
        var f = _filter.Text.Trim();
        var rows = AppData.Current.ToolboxHistory.Where(r => f.Length == 0 || r.Tool.Contains(f, StringComparison.OrdinalIgnoreCase) || r.Target.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
        _list.ItemsSource = rows;
        _status.Text = $"{rows.Count} run(s){(f.Length > 0 ? " matching" : "")}.";
    }

    internal int CountForTest => _list.Items.Count;
}
