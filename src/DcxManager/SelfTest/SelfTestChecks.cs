using System.Net;
using System.Net.Sockets;
using System.Text;
using DcxManager.Net;
using DcxManager.Services;
using DcxManager.Views;

namespace DcxManager;

/// <summary>
/// 0.3.0 §20: the dashboard's pinned checks. Tiles validated and pinned; a TCP tile against a 127.0.0.1 service goes OK,
/// then the service stops and the same tile FAILS with an audit line for the change of state and a history entry; an
/// HTTP tile against a loopback server; HostPerf parsing; the due rule; the dashboard card draws the rows. Nothing but
/// 127.0.0.1 is contacted.
/// </summary>
public static class ChecksTests
{
    public static async Task RunAsync(Action<string> ok, Action<string, Exception?> fail)
    {
        var tilesWas = AppData.Current.DashboardTiles.ToList();
        try
        {
            try { Checks.Validate(new DashboardTile { Kind = "tcp", Target = "a;b", Port = 22 }); throw new Exception("a bad target was accepted"); } catch (ArgumentException) { }
            try { Checks.Validate(new DashboardTile { Kind = "http", Target = "https://u:p@x.test/" }); throw new Exception("credentials in a URL accepted"); } catch (ArgumentException) { }
            try { Checks.Validate(new DashboardTile { Kind = "perf", HostId = "nope" }); throw new Exception("a perf tile without a host accepted"); } catch (ArgumentException) { }
            try { Checks.Validate(new DashboardTile { Kind = "bogus" }); throw new Exception("an unknown kind accepted"); } catch (ArgumentException) { }

            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var serve = Task.Run(async () => { using var c = await listener.AcceptTcpClientAsync(); await c.GetStream().WriteAsync(Encoding.ASCII.GetBytes("220 selftest\r\n")); await Task.Delay(200); });
            var tcp = Checks.Pin(new DashboardTile { Kind = "tcp", Target = "127.0.0.1", Port = port, IntervalMinutes = 0 });
            if (tcp.Name != $"TCP port 127.0.0.1:{port}" || AppData.Current.DashboardTiles.Count != tilesWas.Count + 1) throw new Exception("pin");
            await Checks.RunAsync(tcp); await serve; listener.Stop();
            if (tcp.LastOk != true || !tcp.LastValue.StartsWith("open") || tcp.History.Count != 1 || tcp.History[0] == null) throw new Exception("tcp ok run: " + tcp.LastValue + " / " + tcp.LastDetail);
            int auditBefore = AuditLog.Read(DateTime.Now.ToString("yyyy-MM")).Count;
            await Checks.RunAsync(tcp);   // the port is closed now
            if (tcp.LastOk != false || tcp.History.Count != 2 || tcp.History[1] != null || tcp.LastChangeAt == null) throw new Exception("tcp fail run: " + tcp.LastValue);
            if (!AuditLog.Read(DateTime.Now.ToString("yyyy-MM")).Take(5).Any(a => a.Action.Contains("FAILING"))) throw new Exception("no audit line for the change of state");
            if (Checks.Due("open").Count(t => t.Id == tcp.Id) != 1 || Checks.Due("manual").Count < 1) throw new Exception("due rule");
            tcp.LastAt = DateTime.Now; tcp.IntervalMinutes = 60;
            if (Checks.Due("interval").Any(t => t.Id == tcp.Id)) throw new Exception("a tile just checked counted as due");

            var web = new TcpListener(IPAddress.Loopback, 0); web.Start();
            var wport = ((IPEndPoint)web.LocalEndpoint).Port;
            var webTask = Task.Run(async () =>
            {
                using var c = await web.AcceptTcpClientAsync();
                var s = c.GetStream(); var buf = new byte[4096]; await s.ReadAtLeastAsync(buf, 1, throwOnEndOfStream: false);
                await s.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"));
            });
            var http = Checks.Pin(new DashboardTile { Kind = "http", Target = $"http://127.0.0.1:{wport}/", Name = "selftest-http" });
            await Checks.RunAsync(http); await webTask; web.Stop();
            if (http.LastOk != true || !http.LastValue.StartsWith("200")) throw new Exception("http: " + http.LastValue);

            var perf = HostPerf.Parse("LOAD 0.42 0.30 0.25\nCORES 4\nMEM 8000000 2000000\nDISK 100000000 25000000\nUP 90000.5\n");
            if (perf.Load1 != 0.42 || perf.Cores != 4 || Math.Round(perf.MemPercent!.Value) != 75 || Math.Round(perf.DiskPercent!.Value) != 75 || Math.Round(perf.LoadPercent!.Value, 1) != 10.5 || !perf.Headline.Contains("up 1d")) throw new Exception("perf parse: " + perf.Headline);
            if (HostPerf.Parse("garbage").Headline.Length != 0) throw new Exception("perf parse of nothing");

            if (DashboardView.Current is { } dv)
            {
                dv.RedrawNow();
                var text = dv.BodyText("checks");
                if (!text.Contains("selftest-http") || !text.Contains($"127.0.0.1:{port}")) throw new Exception("the checks card doesn't list the tiles: " + text.Replace("\n", " | "));
            }
            Checks.Unpin(tcp); Checks.Unpin(http);
            if (AppData.Current.DashboardTiles.Count != tilesWas.Count) throw new Exception("unpin");
            ok("dashboard checks: targets validated (shell characters, URL credentials, missing host, unknown kind refused); a TCP tile goes OK with the 127.0.0.1 service and FAILING once it stops — audited, with history; an HTTP tile reads 200; HostPerf parses load / cores / memory / disk / uptime; the due rule respects the interval; the Pinned checks card lists the tiles; unpin removes them");
        }
        catch (Exception ex) { fail("dashboard checks", ex); }
        finally { AppData.Current.DashboardTiles = tilesWas; AppData.Save(); }
    }
}
