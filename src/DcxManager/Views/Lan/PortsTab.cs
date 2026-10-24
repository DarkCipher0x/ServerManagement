using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using DcxManager.Net;
using DcxManager.Services;

namespace DcxManager.Views.Lan;

/// <summary>
/// LAN discovery → Port tests: specific ports and port ranges on chosen hosts. The built-in TCP connect scanner (service
/// detection: banners, HTTP, TLS certificates) or, when installed, Nmap. Results can become hosts, open a connection, or
/// become a monitor that re-checks them on a schedule.
/// </summary>
public sealed class PortsTab : DockPanel
{
    private readonly LanScanView _owner;
    private readonly ObservableCollection<OpenPort> _found = new();
    private readonly TextBox _targets = new() { Width = 320, Padding = new Thickness(5, 4, 5, 4) };
    private readonly TextBox _ports = new() { Width = 240, Padding = new Thickness(5, 4, 5, 4), Text = "common" };
    private readonly CheckBox _detect = new() { Content = "Identify services", IsChecked = true, ToolTip = "Read banners, HTTP headers and TLS certificates of open ports (a HEAD request or a TLS handshake; nothing else is sent)" };
    private readonly ComboBox _engine = new() { Width = 200 };
    private readonly ComboBox _speed = new() { Width = 170 };
    private readonly ProgressBar _progress = new() { Height = 4, Margin = new Thickness(0, 8, 0, 8) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ListView _list = new() { SelectionMode = SelectionMode.Extended };
    private CancellationTokenSource? _cts;

    public PortsTab(LanScanView owner)
    {
        _owner = owner;
        _engine.Items.Add("Built-in scanner");
        var nmap = NmapRunner.Find();
        _engine.Items.Add(nmap != null ? "Nmap (installed)" : "Nmap (not installed)");
        _engine.Items.Add(nmap != null ? "Nmap + versions (-sV)" : "Nmap + versions (not installed)");
        _engine.SelectedIndex = 0;
        foreach (var s in new[] { "Gentle (32 at a time)", "Normal (128)", "Fast (384)" }) _speed.Items.Add(s);
        _speed.SelectedIndex = 1;

        var head = new StackPanel();
        head.Children.Add(new TextBlock { Text = "Targets: addresses, ranges (192.168.1.0/24, 10.0.0.5-40) or host names, separated by commas. Ports: 22, 80,443, 8000-8100, \"common\" or \"all\".", Style = (Style)FindResource("Hint"), Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap });
        var saved = LanUi.Button("▣ Saved hosts…");
        saved.Click += (_, _) => PickSaved();
        head.Children.Add(LanUi.Bar(LanUi.Label("targets"), _targets, saved, LanUi.Label("ports"), _ports));
        var go = LanUi.Button("⌕ Test", primary: true); var stop = LanUi.Button("■ Stop");
        var add = LanUi.Button("＋ Add hosts"); var watch = LanUi.Button("◷ Watch these…");
        go.Click += async (_, _) => await RunAsync();
        stop.Click += (_, _) => _cts?.Cancel();
        add.Click += (_, _) => LanUi.AddToHosts(Rows().GroupBy(r => r.Address).Select(g => (g.Key, g.Any(r => r.Port == 22) ? 22 : g.First().Port, "", string.Join(", ", g.Select(r => $"{r.Port}/{r.Service}")))));
        watch.Click += (_, _) => _owner.Monitor.AddFrom(_targets.Text, _ports.Text);
        head.Children.Add(LanUi.Bar(_detect, LanUi.Label("engine"), _engine, LanUi.Label("speed"), _speed, go, stop, add, watch));
        head.Children.Add(_progress);
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        head.Children.Add(_status);
        SetDock(head, Dock.Top);
        Children.Add(head);

        var gv = new GridView();
        void Col(string h, string p, double w) => gv.Columns.Add(new GridViewColumn { Header = h, Width = w, DisplayMemberBinding = new Binding(p) });
        Col("Address", "Address", 140); Col("Port", "Port", 60); Col("Service", "Service", 170); Col("What answered", "Detail", 460); Col("ms", "ConnectMs", 50);
        _list.View = gv;
        _list.ItemsSource = _found;
        Menus.ForList(_list, this, () => _list.SelectedItem is not OpenPort o ? Array.Empty<object?>() : new object?[]
        {
            Menus.Item("⇄ Open a connection to it", () => { _owner.Connect.Prefill(o.Address, o.Port, o.Detail.StartsWith("TLS")); _owner.Show("Connection"); }),
            Menus.Item("⌨ SSH to it (ad-hoc)", () => LanUi.ConnectSsh(o.Address, o.Port), enabled: o.Service == "SSH"),
            Menus.Item("🌐 Open in the browser", () => WebWindow.Open(o.Address, $"{(o.Detail.StartsWith("TLS") || o.Service.Contains("HTTPS") ? "https" : "http")}://{o.Address}:{o.Port}/", "lan:" + o.Address + ":" + o.Port), enabled: o.Service.Contains("HTTP") || o.Detail.StartsWith("HTTP") || o.Detail.StartsWith("TLS")),
            Menus.Item("Copy", () => Menus.Clip($"{o.Address}:{o.Port} {o.Service} {o.Detail}".Trim())),
            Menus.Item("Copy all results", () => Menus.Clip(string.Join("\n", _found.Select(x => $"{x.Address}\t{x.Port}\t{x.Service}\t{x.Detail}")))),
            Menus.AssistItem("✦ Review these open ports with AI", a => a.AskAsync("port test results",
                "address\tport\tservice\tdetail\n" + string.Join("\n", _found.Select(x => $"{x.Address}\t{x.Port}\t{x.Service}\t{x.Detail}")),
                "Review these open ports on my own network: what each service probably is, anything risky to expose (plain-text protocols, admin panels, databases, Docker API without TLS, expired certificates), and what to close or protect first."),
                enabled: _found.Count > 0),
        });
        Children.Add(_list);
    }

    private List<OpenPort> Rows() { var s = _list.SelectedItems.OfType<OpenPort>().ToList(); return s.Count > 0 ? s : _found.ToList(); }

    public void SetTargets(string t) => _targets.Text = t;

    private void PickSaved()
    {
        var hosts = AppData.Current.Hosts.Where(h => h.Address.Length > 0).OrderBy(h => h.Group).ThenBy(h => h.Display).ToList();
        if (hosts.Count == 0) { Dialogs.Info("Saved hosts", "No saved hosts yet."); return; }
        var w = Dialogs.MakeWindow("Test ports on saved hosts", 480, 480);
        var list = new ListBox();
        foreach (var h in hosts) list.Items.Add(new CheckBox { Content = $"{h.Group} / {h.Display}  ·  {h.Address}", Tag = h.Address });
        var ok = new Button { Content = "Use these", Style = (Style)FindResource("PrimaryButton"), IsDefault = true, Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        ok.Click += (_, _) => w.DialogResult = true;
        var root = new DockPanel { Margin = new Thickness(14) };
        SetDock(ok, Dock.Bottom); root.Children.Add(ok); root.Children.Add(list);
        w.Content = root;
        if (w.ShowDialog() != true) return;
        var picked = list.Items.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag).Distinct().ToList();
        if (picked.Count > 0) _targets.Text = string.Join(", ", picked);
    }

    public async Task RunAsync()
    {
        if (LanUi.Targets(_targets.Text, "Port tests") is not { } t) return;
        List<int> ports;
        try { ports = NetTargets.Ports(_ports.Text); } catch (ArgumentException ex) { Dialogs.Error("Port tests", ex.Message, explain: false); return; }
        var hosts = t.Addresses.Select(a => a.ToString()).Concat(t.Names).ToList();
        long probes = (long)hosts.Count * ports.Count;
        if (probes > 2_000_000) { Dialogs.Error("Port tests", $"{probes:N0} probes is too many for one test: narrow the hosts or the ports."); return; }
        if (probes > 100_000 && !Dialogs.Confirm("Port tests", $"{hosts.Count:N0} host(s) × {ports.Count:N0} port(s) = {probes:N0} probes. This can take a while and may set off intrusion detection. Go ahead?", "Test")) return;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _found.Clear();
        _progress.Value = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var found = new Progress<OpenPort>(o => _found.Add(o));
        try
        {
            string summary;
            if (_engine.SelectedIndex > 0)
            {
                if (NmapRunner.Find() == null) { Dialogs.Info("Port tests", "Nmap isn't installed (nmap.org). The built-in scanner needs nothing extra."); return; }
                _status.Text = "Nmap is running…";
                _progress.IsIndeterminate = true;
                summary = "Nmap: " + await NmapRunner.RunAsync(hosts, ports, _engine.SelectedIndex == 2, found, _cts.Token);
            }
            else
            {
                _status.Text = $"Testing {hosts.Count} host(s) × {ports.Count} port(s)…";
                int parallel = _speed.SelectedIndex switch { 0 => 32, 2 => 384, _ => 128 };
                await PortScanner.RunAsync(hosts, ports, found, new Progress<int>(p => _progress.Value = p), _cts.Token, parallel, detect: _detect.IsChecked == true);
                summary = $"{_found.Count} open port(s) on {_found.Select(o => o.Address).Distinct().Count()} host(s)";
            }
            _status.Text = $"{summary} · {sw.Elapsed.TotalSeconds:0.0} s";
            AuditLog.Write("you", "LAN discovery", "port test", $"{hosts.Count} host(s), {ports.Count} port(s), {_found.Count} open");
        }
        catch (OfflineBlockedException ex) { Offline.Tell(ex); }
        catch (OperationCanceledException) { _status.Text = "Stopped."; }
        catch (Exception ex) { Dialogs.Error("Port tests", ex.Message); }
        finally { _progress.IsIndeterminate = false; _progress.Value = 100; }
    }

    public void Stop() => _cts?.Cancel();
    internal IReadOnlyList<OpenPort> FoundForTest => _found;
    internal void SetForTest(string targets, string ports, bool detect) { _targets.Text = targets; _ports.Text = ports; _detect.IsChecked = detect; _engine.SelectedIndex = 0; }
}
