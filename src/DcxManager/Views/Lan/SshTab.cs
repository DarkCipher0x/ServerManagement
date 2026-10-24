using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using DcxManager.Services;
using DcxManager.Ssh;

namespace DcxManager.Views.Lan;

/// <summary>LAN discovery → SSH servers: sweeps a subnet for machines answering on the SSH port and reads their banner
/// (the original LAN discovery, unchanged), then adds them to Hosts.</summary>
public sealed class SshTab : DockPanel
{
    private readonly ObservableCollection<LanHost> _found = new();
    private readonly TextBox _cidr = new() { Width = 180, Padding = new Thickness(5, 4, 5, 4) };
    private readonly TextBox _port = new() { Width = 60, Padding = new Thickness(5, 4, 5, 4), Text = "22" };
    private readonly ProgressBar _progress = new() { Height = 4, Margin = new Thickness(0, 8, 0, 8) };
    private readonly ListView _list = new() { SelectionMode = SelectionMode.Extended };
    private CancellationTokenSource? _cts;

    public SshTab()
    {
        _cidr.Text = LanScanner.GuessSubnet();
        var head = new StackPanel();
        head.Children.Add(new TextBlock { Text = "Machines answering on the SSH port in a subnet, with their SSH banner.", Style = (Style)FindResource("Hint"), Margin = new Thickness(0, 0, 0, 8) });
        var bar = LanUi.Bar(LanUi.Label("subnet"), _cidr, LanUi.Label("port"), _port);
        var scan = LanUi.Button("⌕ Scan", primary: true); var stop = LanUi.Button("■ Stop"); var add = LanUi.Button("＋ Add selected to hosts");
        scan.Click += async (_, _) => await ScanAsync();
        stop.Click += (_, _) => _cts?.Cancel();
        add.Click += (_, _) => LanUi.AddToHosts(_list.SelectedItems.OfType<LanHost>().Select(h => (h.Address, h.Port, h.Name, h.Banner)));
        bar.Children.Add(scan); bar.Children.Add(stop); bar.Children.Add(add);
        head.Children.Add(bar);
        head.Children.Add(_progress);
        SetDock(head, Dock.Top);
        Children.Add(head);
        var gv = new GridView();
        void Col(string h, string p, double w) => gv.Columns.Add(new GridViewColumn { Header = h, Width = w, DisplayMemberBinding = new Binding(p) });
        Col("Address", "Address", 140); Col("Port", "Port", 50); Col("Name", "Name", 220); Col("SSH banner", "Banner", 420);
        _list.View = gv;
        _list.ItemsSource = _found;
        Menus.ForList(_list, this, () => _list.SelectedItem is not LanHost h ? Array.Empty<object?>() : new object?[]
        {
            Menus.Item("⌨ Connect now (ad-hoc)", () => LanUi.ConnectSsh(h.Address, h.Port)),
            Menus.Item("Copy address", () => Menus.Clip(h.Address)),
            Menus.Item("Copy ssh command", () => Menus.Clip($"ssh {(h.Port != 22 ? $"-p {h.Port} " : "")}{AppSettings.Current.DefaultUsername}@{h.Address}")),
            Menus.AssistItem("✦ Identify these devices with AI", a => a.AskAsync($"LAN scan {_cidr.Text}",
                "address\tport\tname\tSSH banner\n" + string.Join("\n", _found.Select(x => $"{x.Address}\t{x.Port}\t{x.Name}\t{x.Banner}")),
                "Identify what these SSH-speaking devices probably are (OS/distro, appliance, router, NAS, hypervisor) from their banners and names, flag outdated or vulnerable-looking SSH versions, and suggest which to add as hosts."),
                enabled: _found.Count > 0),
        });
        Children.Add(_list);
        // The last result comes back from the encrypted store (0.3.0 §7 "saved encrypted").
        if (AppData.Current.LanLastScan is { Found.Count: > 0 } last)
        {
            _cidr.Text = last.Subnet; _port.Text = last.Port.ToString();
            foreach (var f in last.Found) _found.Add(new LanHost(f.Address, f.Port, f.Banner, f.Name));
            _progress.Value = 100;
            MainWindow.Instance?.Status($"LAN scan: {last.Found.Count} SSH server(s) from {last.When:yyyy-MM-dd HH:mm} (encrypted store) — Scan for a fresh result");
        }
    }

    private async Task ScanAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _found.Clear();
        if (!int.TryParse(_port.Text, out var port)) port = 22;
        try
        {
            await LanScanner.ScanAsync(_cidr.Text.Trim(), port, new Progress<LanHost>(h => _found.Add(h)), new Progress<int>(p => _progress.Value = p), _cts.Token);
            MainWindow.Instance.Status($"LAN scan: {_found.Count} SSH server(s) found on {_cidr.Text}");
            AppData.Current.LanLastScan = new LanLastScan { Subnet = _cidr.Text.Trim(), Port = port, When = DateTime.Now, Found = _found.Select(h => new LanFound { Address = h.Address, Port = h.Port, Name = h.Name, Banner = h.Banner }).Take(2000).ToList() };
            AppData.Save();
        }
        catch (Exception ex) { Dialogs.Error("Scan", ex.Message); }
    }

    internal int FoundForTest => _found.Count;

    public void Stop() => _cts?.Cancel();
}
