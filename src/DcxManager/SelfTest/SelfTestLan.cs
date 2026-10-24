using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DcxManager.Net;
using DcxManager.Services;
using DcxManager.Views;

namespace DcxManager;

/// <summary>
/// 0.3.0 preview.6: LAN discovery tools and scrolling. Targets and ports parse with the scope guard (private ranges only,
/// a typed public address reported for confirmation); a ping of 127.0.0.1; the port scanner finds an SSH and an HTTP
/// listener on 127.0.0.1 (and identifies them) but not a closed port; the connection tool reads a banner and sends a line;
/// a monitor's second run reports a new open port and one that closed; the LAN screen's tabs; menus scroll; middle-click
/// autoscroll speeds; a list at its end hands the wheel to the page. Loopback only (no firewall prompts).
/// </summary>
public static class LanTests
{
    public static async Task RunAsync(MainWindow main, Action<string> ok, Action<string, Exception?> fail)
    {
        // 1. Targets and ports.
        try
        {
            if (NetTargets.Parse("192.168.1.0/30").Addresses.Count != 2 || NetTargets.Parse("10.0.0.5-7").Addresses.Count != 3 || NetTargets.Parse("172.16.0.10-172.16.0.12").Addresses.Count != 3) throw new Exception("ranges");
            try { NetTargets.Parse("8.8.8.0/30"); throw new Exception("a public range was accepted"); } catch (ArgumentException) { }
            try { NetTargets.Parse("8.8.8.1-3"); throw new Exception("a public dash range was accepted"); } catch (ArgumentException) { }
            var single = NetTargets.Parse("203.0.113.9, nas.lan, 127.0.0.1");
            if (single.PublicSingles.SequenceEqual(["203.0.113.9"]) == false || single.Names.Single() != "nas.lan" || single.Addresses.Count != 2) throw new Exception("single public / names");
            try { NetTargets.Parse("rm -rf;"); throw new Exception("junk accepted"); } catch (ArgumentException) { }
            var ports = NetTargets.Ports("22, 80,8000-8002,common");
            if (!ports.Contains(8001) || !ports.Contains(3389) || ports.Count != ports.Distinct().Count() || NetTargets.Ports("all").Count != 65535) throw new Exception("ports");
            try { NetTargets.Ports("70000"); throw new Exception("port 70000 accepted"); } catch (ArgumentException) { }
            if (NmapRunner.Compact([22, 80, 81, 82, 443]) != "22,80-82,443") throw new Exception("nmap port list");
            ok("LAN targets: CIDR, dash and full ranges; public ranges refused, a single typed public address flagged for confirmation; names; ports with ranges, \"common\" and \"all\"; junk refused");
        }
        catch (Exception ex) { fail("LAN targets", ex); }

        // 2. Ping, port scan with detection, connection tool — against listeners on 127.0.0.1.
        var ssh = new Listener(c => Encoding.ASCII.GetBytes("SSH-2.0-OpenSSH_9.9 selftest\r\n"));
        var http = new Listener(_ => Array.Empty<byte>(), onLine: l => l.StartsWith("HEAD") ? "HTTP/1.0 200 OK\r\nServer: dcx-selftest\r\n\r\n" : null);
        var closed = FreePort();
        try
        {
            var pings = new List<PingResult>();
            await PingSweep.RunAsync([IPAddress.Loopback], new Sync<PingResult>(pings.Add), new Sync<int>(_ => { }), CancellationToken.None);
            if (pings.Count != 1 || pings[0].Address != "127.0.0.1") throw new Exception("ping of 127.0.0.1: " + pings.Count);

            var found = new List<OpenPort>();
            await PortScanner.RunAsync(["127.0.0.1"], [ssh.Port, http.Port, closed], new Sync<OpenPort>(o => { lock (found) found.Add(o); }), new Sync<int>(_ => { }), CancellationToken.None);
            var s = found.SingleOrDefault(o => o.Port == ssh.Port); var h = found.SingleOrDefault(o => o.Port == http.Port);
            if (s?.Service != "SSH" || !s.Detail.StartsWith("SSH-2.0-OpenSSH_9.9")) throw new Exception("SSH not identified: " + s);
            if (h?.Service != "HTTP" || !h.Detail.Contains("200 OK") || !h.Detail.Contains("dcx-selftest")) throw new Exception("HTTP not identified: " + h);
            if (found.Any(o => o.Port == closed)) throw new Exception("a closed port reported open");

            await using var conn = await NetConnect.OpenAsync("127.0.0.1", http.Port, tls: false, inspectOnly: false, CancellationToken.None);
            var got = new StringBuilder();
            conn.Received += t => { lock (got) got.Append(t); };
            await conn.SendLineAsync("HEAD / HTTP/1.0");
            await Until(() => { lock (got) return got.ToString().Contains("200 OK"); }, 5000, "connection tool: no reply");
            ok("LAN tools: ping of 127.0.0.1 answers; the port scanner finds the SSH and HTTP listeners (SSH banner, HTTP status and Server header) and not the closed port; the connection tool sends a line and shows the reply");

            // 3. A monitor: baseline, then a new open port and a closed one are reported.
            var extra = new Listener(_ => Array.Empty<byte>());
            var m = new LanMonitor { Name = "selftest-watch", Targets = "127.0.0.1", Ports = $"{ssh.Port},{extra.Port},{closed}", Ping = false };
            AppData.Current.LanMonitors.Add(m);
            extra.Dispose();
            var first = await LanMonitors.RunAsync(m);
            if (!first.Contains("first run") || m.OpenPorts.Count != 1) throw new Exception("baseline: " + first + " " + string.Join(",", m.OpenPorts));
            using var reopened = new Listener(_ => Array.Empty<byte>(), port: extra.Port);
            ssh.Dispose();
            var audit = new List<AuditEntry>();
            void A(AuditEntry e) { lock (audit) audit.Add(e); }
            AuditLog.Added += A;
            try { await LanMonitors.RunAsync(m); } finally { AuditLog.Added -= A; }
            if (!audit.Any(e => e.Action.Contains($"new open port 127.0.0.1:{extra.Port}")) || !audit.Any(e => e.Action.Contains($"port closed 127.0.0.1:{ssh.Port}")) || !m.LastSummary.Contains("+1 port(s), −1"))
                throw new Exception("changes: " + m.LastSummary + " | " + string.Join(" | ", audit.Select(e => e.Action)));
            ok("LAN monitoring: the first run is the baseline; the next reports the new open port and the closed one to the audit log (and so syslog / notifications)");
        }
        catch (Exception ex) { fail("LAN tools", ex); }
        finally
        {
            ssh.Dispose(); http.Dispose();
            AppData.Current.LanMonitors.RemoveAll(x => x.Name == "selftest-watch");
            AppData.Save();
        }

        // 4. The LAN screen: five tabs, a port test from the screen.
        var http2 = new Listener(_ => Array.Empty<byte>(), onLine: l => l.StartsWith("HEAD") ? "HTTP/1.0 204 No Content\r\n\r\n" : null);
        try
        {
            using var view = new LanScanView();
            foreach (var tab in new[] { "Ping sweep", "SSH servers", "Port tests", "Connection", "Monitoring" }) view.Show(tab);
            view.Ports.SetForTest("127.0.0.1", http2.Port.ToString(), detect: true);
            await view.Ports.RunAsync();
            await Until(() => view.Ports.FoundForTest.Count == 1, 5000, "port test from the screen");
            ok("LAN discovery screen: Ping sweep, SSH servers, Port tests, Connection and Monitoring tabs; a port test from the screen finds the listener");
        }
        catch (Exception ex) { fail("LAN screen", ex); }
        finally { http2.Dispose(); }

        // 5. Scrolling: menus scroll, autoscroll speed, wheel hand-off from a list at its end to the page.
        try
        {
            var menu = new ContextMenu();
            for (int i = 0; i < 120; i++) menu.Items.Add(new MenuItem { Header = "item " + i });
            menu.ApplyTemplate();
            var sv = FindChild<ScrollViewer>(menu);
            if (sv == null || double.IsInfinity(sv.MaxHeight) || sv.MaxHeight > SystemParameters.WorkArea.Height) throw new Exception("menus don't scroll (no ScrollViewer with a height limit)");
            if (ScrollAssist.Speed(4) != 0 || ScrollAssist.Speed(40) <= ScrollAssist.Speed(20) || ScrollAssist.Speed(-40) >= 0) throw new Exception("autoscroll speed");

            // Page (outer) with a short list (inner, already at its end) in it: the wheel moves the page.
            var innerContent = new Border { Height = 40 };
            var inner = new ScrollViewer { Height = 60, Content = innerContent };
            var panel = new StackPanel(); panel.Children.Add(inner); panel.Children.Add(new Border { Height = 900 });
            var outer = new ScrollViewer { Height = 200, Width = 200, Content = panel };
            outer.Measure(new Size(200, 200)); outer.Arrange(new Rect(0, 0, 200, 200)); outer.UpdateLayout();
            innerContent.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
            outer.UpdateLayout();   // ScrollToVerticalOffset applies on the next layout pass
            if (outer.VerticalOffset <= 0) throw new Exception("the page didn't scroll when the list under the mouse couldn't");
            ok("scrolling: long menus scroll (height-limited ScrollViewer); middle-click autoscroll has a dead zone and speeds up with distance in both directions; a list at its end hands the wheel to the page");
        }
        catch (Exception ex) { fail("scrolling", ex); }
    }

    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is FrameworkElement fe) fe.ApplyTemplate();
        int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (c is T t) return t;
            if (FindChild<T>(c) is { } deeper) return deeper;
        }
        return null;
    }

    private static int FreePort() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }

    private static async Task Until(Func<bool> cond, int ms, string why)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!cond()) { if (sw.ElapsedMilliseconds > ms) throw new TimeoutException(why); await Task.Delay(30); }
    }

    private sealed class Sync<T>(Action<T> a) : IProgress<T> { public void Report(T v) => a(v); }

    /// <summary>A loopback TCP listener: an optional greeting, and an optional reply per received line.</summary>
    private sealed class Listener : IDisposable
    {
        private readonly TcpListener _l;
        private readonly CancellationTokenSource _cts = new();
        public int Port { get; }

        public Listener(Func<TcpClient, byte[]> greet, Func<string, string?>? onLine = null, int port = 0)
        {
            _l = new TcpListener(IPAddress.Loopback, port);
            _l.Start();
            Port = ((IPEndPoint)_l.LocalEndpoint).Port;
            _ = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    TcpClient c;
                    try { c = await _l.AcceptTcpClientAsync(_cts.Token); } catch { return; }
                    _ = Task.Run(async () =>
                    {
                        using var _ = c;
                        try
                        {
                            var s = c.GetStream();
                            var g = greet(c); if (g.Length > 0) await s.WriteAsync(g);
                            if (onLine == null) { await Task.Delay(2500, _cts.Token); return; }
                            using var r = new System.IO.StreamReader(s, Encoding.ASCII, false, 1024, true);
                            while (await r.ReadLineAsync() is { } line)
                                if (onLine(line) is { } reply) { await s.WriteAsync(Encoding.ASCII.GetBytes(reply)); await s.FlushAsync(); }
                        }
                        catch { }
                    });
                }
            });
        }

        public void Dispose() { _cts.Cancel(); try { _l.Stop(); } catch { } }
    }
}
