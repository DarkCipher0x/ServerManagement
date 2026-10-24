using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using DcxManager.Net;
using DcxManager.Services;
using DcxManager.Ssh;

namespace DcxManager.Views.Lan;

/// <summary>LAN discovery → Ping sweep: which addresses answer an ICMP echo (round trip, TTL with an OS hint, name).</summary>
public sealed class PingTab : DockPanel
{
    private readonly LanScanView _owner;
    private readonly ObservableCollection<PingResult> _found = new();
    private readonly TextBox _targets = new() { Width = 320, Padding = new Thickness(5, 4, 5, 4) };
    private readonly ProgressBar _progress = new() { Height = 4, Margin = new Thickness(0, 8, 0, 8) };
    private readonly TextBlock _status = new();
    private readonly ListView _list = new() { SelectionMode = SelectionMode.Extended };
    private CancellationTokenSource? _cts;

    public PingTab(LanScanView owner)
    {
        _owner = owner;
        _targets.Text = LanScanner.GuessSubnet();
        var head = new StackPanel();
        head.Children.Add(new TextBlock { Text = "Which addresses answer a ping. Hosts that drop ICMP (the Windows firewall does by default) won't show here: Port tests finds those.", Style = (Style)FindResource("Hint"), Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap });
        var go = LanUi.Button("⌕ Ping", primary: true); var stop = LanUi.Button("■ Stop");
        var ports = LanUi.Button("⇢ Test ports on selected"); var add = LanUi.Button("＋ Add selected to hosts");
        go.Click += async (_, _) => await RunAsync();
        stop.Click += (_, _) => _cts?.Cancel();
        ports.Click += (_, _) => _owner.TestPorts(Selected().Select(r => r.Address));
        add.Click += (_, _) => LanUi.AddToHosts(Selected().Select(r => (r.Address, 22, r.Name, "found by ping" + (r.OsHint.Length > 0 ? " · " + r.OsHint : ""))));
        head.Children.Add(LanUi.Bar(LanUi.Label("targets"), _targets, go, stop, ports, add));
        head.Children.Add(_progress);
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        head.Children.Add(_status);
        SetDock(head, Dock.Top);
        Children.Add(head);
        var gv = new GridView();
        void Col(string h, string p, double w) => gv.Columns.Add(new GridViewColumn { Header = h, Width = w, DisplayMemberBinding = new Binding(p) });
        Col("Address", "Address", 140); Col("Name", "Name", 240); Col("Round trip (ms)", "RoundTripMs", 110); Col("TTL", "Ttl", 60); Col("Probably", "OsHint", 180);
        _list.View = gv;
        _list.ItemsSource = _found;
        Menus.ForList(_list, this, () => _list.SelectedItem is not PingResult r ? Array.Empty<object?>() : new object?[]
        {
            Menus.Item("⇢ Test ports", () => _owner.TestPorts(Selected().Select(x => x.Address))),
            Menus.Item("⌨ SSH to it (ad-hoc)", () => LanUi.ConnectSsh(r.Address, 22)),
            Menus.Item("Copy address", () => Menus.Clip(r.Address)),
        });
        Children.Add(_list);
    }

    /// <summary>The selected rows, or every row when none is selected.</summary>
    private List<PingResult> Selected() { var s = _list.SelectedItems.OfType<PingResult>().ToList(); return s.Count > 0 ? s : _found.ToList(); }

    public async Task RunAsync()
    {
        if (LanUi.Targets(_targets.Text, "Ping sweep") is not { } t) return;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _found.Clear();
        var addrs = t.Addresses.ToList();
        foreach (var n in t.Names) try { addrs.AddRange((await System.Net.Dns.GetHostAddressesAsync(n)).Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).Take(1)); } catch { }
        _status.Text = $"Pinging {addrs.Count} address(es)…";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await PingSweep.RunAsync(addrs, new Progress<PingResult>(r => _found.Add(r)), new Progress<int>(p => _progress.Value = p), _cts.Token);
            _status.Text = $"{_found.Count} of {addrs.Count} answered · {sw.Elapsed.TotalSeconds:0.0} s";
            AuditLog.Write("you", "LAN discovery", "ping sweep", $"{addrs.Count} addresses, {_found.Count} answered");
        }
        catch (OfflineBlockedException ex) { Offline.Tell(ex); }
        catch (Exception ex) { Dialogs.Error("Ping sweep", ex.Message); }
    }

    public void Stop() => _cts?.Cancel();
    internal int FoundForTest => _found.Count;
    internal void SetTargetsForTest(string t) => _targets.Text = t;
}
