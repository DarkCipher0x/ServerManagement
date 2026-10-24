using System.Text;
using System.Windows;
using System.Windows.Controls;
using DcxManager.Net;
using DcxManager.Services;

namespace DcxManager.Views.Toolbox;

/// <summary>
/// Toolbox → Network: the "telnet test" (TCP connect with the service's banner), traceroute, a live mtr (loss and
/// timings per hop, refreshed every round) and the path MTU — from this PC, or from a saved server over SSH using the
/// tools found there (nc, mtr / traceroute / tracepath, tracepath / ping).
/// </summary>
public sealed class NetworkTab : DockPanel
{
    private readonly TextBox _host = ToolboxUi.Input(240, "", "A host name or an IP address");
    private readonly TextBox _port = ToolboxUi.Input(70, "22");
    private readonly TextBox _rounds = ToolboxUi.Input(50, "10", "mtr rounds (one ping per hop per round)");
    private readonly ComboBox _from = ToolboxUi.FromPicker();
    private readonly TextBlock _status = ToolboxUi.Status();
    private readonly TextBox _out = ToolboxUi.Output();
    private CancellationTokenSource? _cts;

    public NetworkTab()
    {
        var head = new StackPanel();
        head.Children.Add(ToolboxUi.Hint("TCP port: does it answer, how fast, and what it says first (the banner). Traceroute and mtr use ICMP with rising TTLs — no administrator rights needed. Path MTU finds the largest unfragmented packet."));
        var stop = ToolboxUi.Button("■ Stop"); stop.Click += (_, _) => _cts?.Cancel();
        var pin = ToolboxUi.Button("📌 Pin to dashboard"); pin.ToolTip = "A TCP port (or ping) check of this host as a dashboard row (0.3.0 §20)";
        pin.Click += (_, _) => { try { PinCheckDialog.Show(Window.GetWindow(this), kind: int.TryParse(_port.Text, out var pp) && pp > 0 ? "tcp" : "ping", target: _host.Text.Trim(), port: int.TryParse(_port.Text, out var p2) ? p2 : 0); } catch (Exception ex) { _status.Text = "✕ " + ex.Message; } };
        head.Children.Add(ToolboxUi.Bar(ToolboxUi.Label("host"), _host, ToolboxUi.Label("port"), _port, _from, pin));
        head.Children.Add(ToolboxUi.Bar(
            ToolboxUi.Run("⇄ TCP port", TcpAsync, _status, primary: true), ToolboxUi.Run("⤳ Traceroute", TraceAsync, _status),
            ToolboxUi.Run("≋ mtr", MtrAsync, _status), ToolboxUi.Label("rounds"), _rounds, ToolboxUi.Run("⊟ Path MTU", MtuAsync, _status), stop));
        head.Children.Add(_status);
        SetDock(head, Dock.Top);
        Children.Add(head);
        Children.Add(_out);
    }

    private CancellationToken Begin() { _cts?.Cancel(); _cts = new CancellationTokenSource(); return _cts.Token; }
    private string Host => NetTests.CheckHost(_host.Text);
    private int Port => int.TryParse(_port.Text.Trim(), out var p) && p is >= 1 and <= 65535 ? p : throw new ArgumentException("A port from 1 to 65535.");

    private async Task<bool> RemoteAsync(string test, string tool, int port = 0)
    {
        if (ToolboxUi.From(_from) is not { } h) return false;
        var ct = Begin();
        _status.Text = $"{tool} from {h.Display}…";
        var cmd = NetTests.RemoteCommand(test, _host.Text, port);
        _out.Text = await ToolboxUi.RemoteAsync(h, cmd, ct);
        _status.Text = $"{tool} ran on {h.Display}.";
        ToolboxUi.Record(tool, $"{Host}{(port > 0 ? ":" + port : "")} from {h.Display}", _out.Text.Split('\n').FirstOrDefault() ?? "", _out.Text);
        return true;
    }

    public async Task TcpAsync()
    {
        var port = Port;
        if (await RemoteAsync("tcp", "TCP port", port)) return;
        var ct = Begin();
        _status.Text = $"Connecting to {Host}:{port}…";
        var r = await NetTests.TcpAsync(Host, port, ct: ct);
        var sb = new StringBuilder();
        sb.AppendLine($"{Host}:{port}  →  {r.State.ToUpperInvariant()}{(r.Address.Length > 0 ? $"  ({r.Address})" : "")}  {r.Ms:0.0} ms");
        if (r.Detail.Length > 0) sb.AppendLine(r.Detail);
        if (r.Banner.Length > 0) sb.AppendLine().AppendLine("Banner:").AppendLine(r.Banner);
        else if (r.State == "open") sb.AppendLine("No banner within 2 s (the service waits for the client to speak first — HTTP, TLS, DNS…).");
        _out.Text = sb.ToString();
        _status.Text = $"{Host}:{port} is {r.State}.";
        ToolboxUi.Record("TCP port", $"{Host}:{port}", $"{r.State} {r.Ms:0} ms", _out.Text);
    }

    public async Task TraceAsync()
    {
        if (await RemoteAsync("trace", "Traceroute")) return;
        var ct = Begin();
        var host = Host;
        _status.Text = $"Tracing the route to {host}…";
        _out.Text = $"traceroute to {host}\n";
        var hops = await NetTests.TraceAsync(host, h => Dispatcher.BeginInvoke(() => _out.AppendText($"{h.Ttl,3}  {h.Address,-40} {(h.Ms is { } ms ? $"{ms,7:0.0} ms" : "        *")}  {h.Name}  {h.Status}\n")), ct: ct,
            onProbe: ttl => Dispatcher.BeginInvoke(() => _status.Text = $"Tracing the route to {host}… hop {ttl}"));
        _status.Text = TraceVerdict(host, hops);
        _out.AppendText(_status.Text + "\n");
        ToolboxUi.Record("Traceroute", host, _status.Text, _out.Text);
    }

    /// <summary>One line on how the trace ended: reached, refused by a router, or silent at the end (most servers and
    /// cloud firewalls drop ICMP echo — the path up to the last router that answered is still right).</summary>
    public static string TraceVerdict(string host, IReadOnlyList<NetTests.Hop> hops)
    {
        var last = hops.LastOrDefault();
        if (last == null) return $"No hops to {host}.";
        if (last.Status == "destination") return $"Reached {host} in {hops.Count} hop(s).";
        if (last.Status.StartsWith("unreachable", StringComparison.Ordinal)) return $"{last.Address} answered \"{last.Status[13..]}\" at hop {hops.Count}: {host} can't be reached from here (a firewall or a missing route).";
        int silent = hops.Reverse().TakeWhile(h => h.Address == "*").Count();
        return silent == hops.Count
            ? $"No router on the way to {host} answered ({hops.Count} hops tried) — ICMP is blocked at this PC or its gateway."
            : $"No reply from {host}: {silent} silent hop(s) after {hops.Count - silent} that answered — the destination or a firewall on the path drops ICMP echo; the route up to hop {hops.Count - silent} is shown.";
    }

    public async Task MtrAsync()
    {
        if (await RemoteAsync("trace", "mtr")) return;
        var ct = Begin();
        var host = Host;
        int rounds = int.TryParse(_rounds.Text.Trim(), out var n) ? Math.Clamp(n, 1, 1000) : 10;
        _status.Text = $"mtr to {host}: finding the route…";
        _out.Text = $"mtr to {host} — finding the route (one hop at a time; a silent hop takes a second)\n";
        var rows = await NetTests.MtrAsync(host, rounds, r => Dispatcher.BeginInvoke(() =>
        {
            _out.Text = $"mtr to {host} — round {r[0].Sent} of {rounds}\n{NetTests.MtrHeader}\n" + string.Join("\n", r.Select(x => x.ToString()));
            _status.Text = $"mtr to {host}: round {r[0].Sent} of {rounds}…";
        }), ct: ct, onProbe: ttl => Dispatcher.BeginInvoke(() => _status.Text = $"mtr to {host}: finding the route… hop {ttl}"));
        if (rows.Count == 0) { _status.Text = $"No hops to {host}."; return; }
        var worst = rows.Where(r => r.Received > 0).OrderByDescending(r => r.LossPercent).FirstOrDefault();
        bool reached = rows[^1].Address != "*" && rows[^1].Received > 0 && NetTests.ValidHost(host) && (System.Net.IPAddress.TryParse(host, out var hip) ? rows[^1].Address == hip.ToString() : true);
        _status.Text = $"mtr done: {rows.Count} hop(s)" + (worst != null && worst.LossPercent > 0 ? $", most loss at hop {worst.Ttl} ({worst.LossPercent:0}%)" : ", no loss")
            + (reached ? "" : $" — the last hops are silent: {host} (or a firewall before it) drops ICMP echo; loss on silent hops means nothing");
        _out.AppendText("\n" + _status.Text + "\n");
        ToolboxUi.Record("mtr", host, _status.Text, _out.Text);
    }

    public async Task MtuAsync()
    {
        if (await RemoteAsync("mtu", "Path MTU")) return;
        var ct = Begin();
        var host = Host;
        _status.Text = $"Finding the path MTU to {host} (a few pings)…";
        var mtu = await NetTests.PathMtuAsync(host, ct);
        _out.Text = mtu is { } m ? $"Path MTU to {host}: {m} bytes{(m < 1500 ? "  — below Ethernet's 1500: a tunnel or PPPoE on the path; set the interface / VPN MTU to this or lower" : m == 1500 ? "  (standard Ethernet)" : "  (jumbo frames)")}\n" : $"{host} doesn't answer ping, so the MTU can't be measured.\n";
        _status.Text = mtu is { } ? $"Path MTU {mtu} bytes." : "No ping reply.";
        ToolboxUi.Record("Path MTU", host, _status.Text, _out.Text);
    }

    public void Stop() => _cts?.Cancel();
    internal void SetForTest(string host, string port) { _host.Text = host; _port.Text = port; _from.SelectedIndex = 0; }
    internal string OutputForTest => _out.Text;
}
