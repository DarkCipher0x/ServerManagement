using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using DcxManager.Mail;
using DcxManager.Services;

namespace DcxManager.Views.Toolbox;

/// <summary>
/// Toolbox → Blacklists: a public IP against the IP DNSBLs, or a domain against the domain lists — one DNS query per
/// list, results as they arrive. "Refused" (the list declines queries from public resolvers) is shown as such, never
/// as clean.
/// </summary>
public sealed class BlacklistTab : DockPanel
{
    public sealed record Row(string State, string List, string Zone, string Detail);

    private readonly TextBox _target = ToolboxUi.Input(260, "", "A public IP (your mail server's) or a domain");
    private readonly TextBlock _status = ToolboxUi.Status();
    private readonly ObservableCollection<Row> _rows = new();
    private CancellationTokenSource? _cts;

    public BlacklistTab()
    {
        var head = new StackPanel();
        head.Children.Add(ToolboxUi.Hint($"{Blacklists.IpLists.Length} IP lists (Spamhaus ZEN, SpamCop, Barracuda, SORBS…) and {Blacklists.DomainLists.Length} domain lists (Spamhaus DBL, SURBL, URIBL…). A listing names the list; follow its own delisting process."));
        var stop = ToolboxUi.Button("■ Stop"); stop.Click += (_, _) => _cts?.Cancel();
        head.Children.Add(ToolboxUi.Bar(ToolboxUi.Label("IP or domain"), _target, ToolboxUi.Run("⛔ Check lists", CheckAsync, _status, primary: true), stop));
        head.Children.Add(_status);
        SetDock(head, Dock.Top);
        Children.Add(head);
        var list = new ListView { ItemsSource = _rows };
        var gv = new GridView();
        void Col(string h, string p, double w) => gv.Columns.Add(new GridViewColumn { Header = h, Width = w, DisplayMemberBinding = new Binding(p) });
        Col("State", "State", 90); Col("List", "List", 200); Col("Zone", "Zone", 220); Col("Detail", "Detail", 500);
        list.View = gv;
        Menus.ForList(list, this, () => new object?[] { Menus.Item("Copy all", () => Menus.Clip(string.Join("\n", _rows.Select(r => $"{r.State}\t{r.List}\t{r.Zone}\t{r.Detail}"))), enabled: _rows.Count > 0) });
        Children.Add(list);
    }

    public void SetTarget(string t) => _target.Text = t;

    public async Task CheckAsync()
    {
        _cts?.Cancel(); _cts = new CancellationTokenSource();
        var target = _target.Text.Trim();
        _rows.Clear();
        _status.Text = $"Asking the lists about {target}…";
        var hits = await Blacklists.CheckAsync(target, null, h => Dispatcher.BeginInvoke(() => _rows.Add(new Row(Glyph(h.State) + " " + h.State, h.List.Name, h.List.Zone, h.Detail))), _cts.Token);
        int listed = hits.Count(h => h.State == "listed"), refused = hits.Count(h => h.State == "refused");
        _status.Text = listed > 0 ? $"✕ {target} is LISTED on {listed} of {hits.Count}: {string.Join(", ", hits.Where(h => h.State == "listed").Select(h => h.List.Name))}"
                     : $"✓ {target} is on none of the {hits.Count - refused} lists that answered{(refused > 0 ? $" ({refused} refused the query — check those from your own resolver)" : "")}.";
        ToolboxUi.Record("Blacklists", target, _status.Text, string.Join("\n", hits.Select(h => $"{h.State,-8} {h.List.Name,-24} {h.List.Zone,-28} {h.Detail}")));
    }

    private static string Glyph(string s) => s switch { "listed" => "✕", "clean" => "✓", "refused" => "⚠", _ => "·" };
    public void Stop() => _cts?.Cancel();
}
