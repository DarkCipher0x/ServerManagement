using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using DcxManager.Net;
using DcxManager.Services;

namespace DcxManager.Views.Toolbox;

/// <summary>
/// Toolbox → DNS: any record type through Windows' resolver (or dig on a saved server), and propagation — the same
/// question asked directly of Cloudflare, Google, Quad9, OpenDNS and any resolver you add, side by side, with the ones
/// that disagree with the majority marked. Read-only queries; a resolver you type is an IP address.
/// </summary>
public sealed class DnsTab : DockPanel
{
    public sealed record Row(string Resolver, string Address, string Answer, string Ttl, string Ms, string Agrees);

    private readonly TextBox _name = ToolboxUi.Input(260, "", "A name (example.com), or an IP for PTR");
    private readonly ComboBox _type = new() { Width = 90 };
    private readonly ComboBox _from = ToolboxUi.FromPicker();
    private readonly TextBox _extra = ToolboxUi.Input(260, "", "Extra resolvers for the propagation check: IP addresses, comma-separated (your own DNS servers, the registrar's…)");
    private readonly TextBlock _status = ToolboxUi.Status();
    private readonly TextBox _out = ToolboxUi.Output();
    private readonly ObservableCollection<Row> _rows = new();
    private readonly ListView _list = new();
    private CancellationTokenSource? _cts;

    public DnsTab()
    {
        foreach (var t in DnsClient.TypeList) _type.Items.Add(t);
        _type.SelectedIndex = 0;
        var head = new StackPanel();
        head.Children.Add(ToolboxUi.Hint("Lookup asks the resolver this PC (or the chosen server) uses. Propagation asks each public resolver directly — after a DNS change, a resolver that still shows the old answer is simply caching it until its TTL runs out."));
        var stop = ToolboxUi.Button("■ Stop"); stop.Click += (_, _) => _cts?.Cancel();
        head.Children.Add(ToolboxUi.Bar(ToolboxUi.Label("name"), _name, ToolboxUi.Label("type"), _type, _from, ToolboxUi.Run("⌕ Lookup", LookupAsync, _status, primary: true)));
        head.Children.Add(ToolboxUi.Bar(ToolboxUi.Label("extra resolvers"), _extra, ToolboxUi.Run("🌐 Propagation", PropagationAsync, _status), stop));
        head.Children.Add(_status);
        SetDock(head, Dock.Top);
        Children.Add(head);
        var gv = new GridView();
        void Col(string h, string p, double w) => gv.Columns.Add(new GridViewColumn { Header = h, Width = w, DisplayMemberBinding = new Binding(p) });
        Col("Resolver", "Resolver", 130); Col("Address", "Address", 130); Col("Answer", "Answer", 520); Col("TTL", "Ttl", 70); Col("ms", "Ms", 60); Col("", "Agrees", 60);
        _list.View = gv; _list.ItemsSource = _rows;
        Menus.ForList(_list, this, () => new object?[]
        {
            Menus.Item("Copy all", () => Menus.Clip(string.Join("\n", _rows.Select(r => $"{r.Resolver}\t{r.Address}\t{r.Answer}\t{r.Ttl}\t{r.Ms}\t{r.Agrees}"))), enabled: _rows.Count > 0),
        });
        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(_out, 0); Grid.SetRow(_list, 1); _list.Margin = new Thickness(0, 8, 0, 0);
        body.Children.Add(_out); body.Children.Add(_list);
        Children.Add(body);
    }

    private CancellationToken Begin() { _cts?.Cancel(); _cts = new CancellationTokenSource(); return _cts.Token; }
    private string Type => _type.SelectedItem as string ?? "A";

    public async Task LookupAsync()
    {
        var name = _name.Text.Trim(); var type = Type;
        if (ToolboxUi.From(_from) is { } h)
        {
            var ct = Begin();
            _status.Text = $"{type} {name} from {h.Display}…";
            _out.Text = await ToolboxUi.RemoteAsync(h, NetTests.RemoteCommand("dns", name, 0, type), ct);
            _status.Text = $"Looked up on {h.Display}.";
            ToolboxUi.Record("DNS", $"{type} {name} from {h.Display}", _out.Text.Split('\n').FirstOrDefault() ?? "", _out.Text);
            return;
        }
        _status.Text = $"Looking up {type} {name}…";
        var records = await NetTests.DnsAsync(name, type);
        _out.Text = $"{type} {name}\n" + (records.Count == 0 ? "(no records)\n" : string.Join("\n", records) + "\n");
        _status.Text = $"{records.Count} {type} record(s) for {name}.";
        ToolboxUi.Record("DNS", $"{type} {name}", _status.Text, _out.Text);
    }

    public async Task PropagationAsync()
    {
        var ct = Begin();
        var name = _name.Text.Trim(); var type = Type;
        var resolvers = Resolvers(_extra.Text);
        _rows.Clear();
        _status.Text = $"Asking {resolvers.Count} resolvers for {type} {name}…";
        var rows = await DnsClient.PropagationAsync(name, type, resolvers, r => Dispatcher.BeginInvoke(() => _rows.Add(ToRow(r))), ct);
        int agree = rows.Count(r => r.Agrees), failed = rows.Count(r => r.Response.Error != null);
        _status.Text = agree == rows.Count ? $"All {rows.Count} resolvers agree." : $"{agree} of {rows.Count} resolvers agree{(failed > 0 ? $", {failed} didn't answer" : "")} — {rows.Count - agree - failed} differ (still caching an older answer, or a split view).";
        ToolboxUi.Record("DNS propagation", $"{type} {name}", _status.Text, string.Join("\n", rows.Select(r => $"{r.Resolver} ({r.Address}): {r.Response.Summary}{(r.Agrees ? "" : "  ≠")}")));
    }

    private static Row ToRow(DnsClient.PropagationRow r) => new(r.Resolver, r.Address, r.Response.Summary,
        r.Response.Answers.Count > 0 ? r.Response.Answers.Min(a => a.Ttl).ToString() : "", $"{r.Response.Ms:0}", r.Response.Error != null ? "—" : r.Agrees ? "✓" : "≠");

    /// <summary>The public resolvers plus the system's and any typed (validated as IP addresses).</summary>
    public static List<(string Name, string Address)> Resolvers(string extra)
    {
        var list = DnsClient.PublicResolvers.Take(4).ToList();
        foreach (var s in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                     .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
                     .SelectMany(n => n.GetIPProperties().DnsAddresses).Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).Distinct().Take(2))
            list.Add(("System", s.ToString()));
        foreach (var e in extra.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var ep = DnsClient.ParseServer(e);   // throws on anything that isn't an IP
            if (list.All(x => x.Address != e)) list.Add(("Yours", ep.Port == 53 ? ep.Address.ToString() : e));
        }
        return list;
    }

    public void Stop() => _cts?.Cancel();
    internal async Task<List<DnsClient.PropagationRow>> PropagationForTest(string name, string type, List<(string, string)> resolvers)
    {
        _rows.Clear();
        var rows = await DnsClient.PropagationAsync(name, type, resolvers, r => _rows.Add(ToRow(r)));
        return rows;
    }
    internal IReadOnlyList<Row> RowsForTest => _rows;
}
