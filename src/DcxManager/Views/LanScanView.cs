using System.Windows;
using System.Windows.Controls;
using DcxManager.Views.Lan;

namespace DcxManager.Views;

/// <summary>
/// LAN discovery (0.3.0 §7): tabs for an ICMP ping sweep, SSH servers (the original discovery), port tests on chosen hosts
/// and port ranges (built-in TCP scanner with service detection, or Nmap when installed), a connection tool (ncat-style,
/// client only) and scheduled monitoring. Each tab is its own file in Views/Lan. Private ranges only; a single public
/// address must be typed and confirmed. Only scan networks you manage.
/// </summary>
public sealed class LanScanView : UserControl, IDisposable
{
    private readonly ContentControl _body = new();
    private readonly Dictionary<string, (RadioButton Tab, FrameworkElement View)> _tabs = new();
    public PingTab Ping { get; }
    public SshTab Ssh { get; }
    public PortsTab Ports { get; }
    public ConnectTab Connect { get; }
    public MonitorTab Monitor { get; }

    public LanScanView()
    {
        var root = new DockPanel { Margin = new Thickness(14, 12, 14, 12) };
        var head = new StackPanel();
        head.Children.Add(new TextBlock { Text = "LAN discovery", Style = (Style)FindResource("H1") });
        head.Children.Add(new TextBlock { Text = "Find what is on your network and what it answers on. Private ranges only (a single public address can be tested when you type it). Only scan networks you manage.", Style = (Style)FindResource("Hint"), Margin = new Thickness(0, 4, 0, 8), TextWrapping = TextWrapping.Wrap });
        var strip = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        head.Children.Add(strip);
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);
        root.Children.Add(_body);
        Content = root;

        Ping = new PingTab(this); Ssh = new SshTab(); Ports = new PortsTab(this); Connect = new ConnectTab(); Monitor = new MonitorTab(this);
        foreach (var (name, view) in new (string, FrameworkElement)[] { ("Ping sweep", Ping), ("SSH servers", Ssh), ("Port tests", Ports), ("Connection", Connect), ("Monitoring", Monitor) })
        {
            var rb = new RadioButton { Content = name, GroupName = "lan-tabs", Style = (Style)FindResource("TabStripItem") };
            rb.Checked += (_, _) => _body.Content = view;
            strip.Children.Add(rb);
            _tabs[name] = (rb, view);
        }
        Show("Ping sweep");
    }

    /// <summary>Opens a tab by name ("Port tests"…).</summary>
    public void Show(string tab) { if (_tabs.TryGetValue(tab, out var t)) { t.Tab.IsChecked = true; _body.Content = t.View; } }

    /// <summary>"Test ports on these" from another tab.</summary>
    public void TestPorts(IEnumerable<string> hosts) { Ports.SetTargets(string.Join(", ", hosts)); Show("Port tests"); }

    public void Dispose() { Ping.Stop(); Ssh.Stop(); Ports.Stop(); _ = Connect.CloseAsync(); Monitor.Detach(); }
}
