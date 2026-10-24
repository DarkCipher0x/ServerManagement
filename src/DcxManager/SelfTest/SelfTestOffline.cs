using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using DcxManager.Services;
using DcxManager.Ssh;
using DcxManager.Views;
using DcxManager.Vultr;

namespace DcxManager;

/// <summary>
/// Spec §53 / ADR-016: offline mode. Real loopback traffic and the real clients; name resolution is replaced by a fake
/// resolver so nothing depends on (or sends) real DNS, and every blocked destination is a TEST-NET address that is never
/// contacted. No window opens: refusals go to the status line during the self-test.
/// </summary>
public static class OfflineTests
{
    public static async Task RunAsync(MainWindow main, string reportDir, Action<string> ok, Action<string, Exception?> fail, Action<Window, string> snap)
    {
        async Task Settle(int ms = 200)
        {
            await Task.Delay(ms);
            await Application.Current.Dispatcher.InvokeAsync(() => main.UpdateLayout(), DispatcherPriority.ApplicationIdle);
        }
        var s = AppSettings.Current;
        var saved = (s.OfflineMode, s.OfflineAllowed.ToList(), s.OfflineResolvePrivateNames, s.OfflineAllowAddons);
        var realResolver = Offline.Resolver;
        int rebindCalls = 0;
        Offline.Resolver = (h, ct) => Task.FromResult(h switch
        {
            "lab.example" => new[] { IPAddress.Parse("10.0.0.5") },
            "mixed.example" => new[] { IPAddress.Parse("10.0.0.5"), IPAddress.Parse("198.51.100.9") },
            "public.example" => new[] { IPAddress.Parse("198.51.100.9") },
            "lab-loop.example" => new[] { IPAddress.Loopback },
            // First answer private (passes the request check), second public (must be caught at connect).
            "rebind.example" => new[] { Interlocked.Increment(ref rebindCalls) == 1 ? IPAddress.Loopback : IPAddress.Parse("198.51.100.7") },
            _ => throw new SocketException((int)SocketError.HostNotFound),
        });
        try
        {
            // 1. The rule.
            try
            {
                Offline.Set(false);
                Offline.SetAllowed(Array.Empty<string>(), resolvePrivateNames: true, allowAddons: false);
                if (Offline.Quick("198.51.100.7").Allowed != true) throw new Exception("off must allow everything");
                Offline.Set(true);
                var allow = new[] { "127.0.0.1", "localhost", "::1", "10.1.2.3", "192.168.1.20", "100.101.1.2", "fd00::5", "nas", "proxmox.lan", "box.home.arpa" };
                var block = new[] { "198.51.100.7", "203.0.113.50", "2001:db8::1", "api.openai.com", "api.vultr.com", "my.vultr.com", "api.anthropic.com" };
                foreach (var h in allow) if (Offline.Quick(h).Allowed != true) throw new Exception("should allow " + h);
                foreach (var h in block) if (Offline.Quick(h).Allowed != false) throw new Exception("should block " + h);
                if (!(await Offline.DecideAsync("lab.example")).Allowed) throw new Exception("private-resolving name refused");
                if ((await Offline.DecideAsync("mixed.example")).Allowed) throw new Exception("name with one public address allowed");
                if ((await Offline.DecideAsync("public.example")).Allowed) throw new Exception("public-resolving name allowed");
                if ((await Offline.DecideAsync("nx.example")).Allowed) throw new Exception("unresolvable name allowed (must fail closed)");
                if (!Offline.QuickAllowed("lab.example") || Offline.QuickAllowed("never-seen.example")) throw new Exception("quick cache");

                // Validation and the approved list.
                foreach (var good in new[] { "api.vultr.com", "*.corp.example", ".corp2.example", "203.0.113.0/24", "198.51.100.9", "2001:db8::/32", "[2001:db8::1]" })
                    if (Offline.Validate(good) is { } why) throw new Exception($"valid entry refused: {good} ({why})");
                foreach (var bad in new[] { "203.0.113.0/33", "0.0.0.0/0", "bad host!", "a*.b.example", "1.2.3.4/x", "*.", "-x.example" })
                    if (Offline.Validate(bad) == null) throw new Exception("invalid entry accepted: " + bad);
                if (Offline.SetAllowed(new[] { "api.vultr.com", "0.0.0.0/0" }) == null || s.OfflineAllowed.Contains("0.0.0.0/0")) throw new Exception("a bad list was saved");
                if (Offline.SetAllowed(new[] { "api.vultr.com", "*.corp.example", "203.0.113.0/24", "2001:db8::/32", "198.51.100.9", "# comment", "" }) is { } err) throw new Exception(err);
                if (s.OfflineAllowed.Count != 5) throw new Exception("comments/blank lines kept: " + s.OfflineAllowed.Count);
                foreach (var h in new[] { "api.vultr.com", "x.corp.example", "deep.x.corp.example", "203.0.113.50", "2001:db8::1" })
                    if (Offline.Quick(h).Allowed != true) throw new Exception("approved but refused: " + h);
                if (Offline.Quick("my.vultr.com").Allowed != false || Offline.Quick("203.0.114.1").Allowed != false) throw new Exception("approval too wide");
                if (!(await Offline.DecideAsync("public.example")).Allowed) throw new Exception("approved resolved address refused");
                Offline.SetAllowed(Array.Empty<string>(), resolvePrivateNames: false);
                if ((await Offline.DecideAsync("lab.example")).Allowed) throw new Exception("resolving off must refuse unapproved names");
                Offline.SetAllowed(Array.Empty<string>(), resolvePrivateNames: true);
                try { Offline.EnforceStatic("only-on-the-jump-host.example", "t"); } catch { throw new Exception("jump target without DNS refused"); }
                try { Offline.EnforceStatic("198.51.100.7", "t"); throw new Exception("jump target with public IP allowed"); } catch (OfflineBlockedException) { }
                ok("offline mode rule: off = unchanged; on = loopback/private/LAN names allowed, public IPs and internet services refused, DNS names allowed only when every address is private (fails closed), approved names/*.suffix/IP/CIDR (v4+v6), validation refuses /0 and junk, jump targets checked without DNS");
            }
            catch (Exception ex) { fail("offline mode rule", ex); }

            // 2. HTTP through the real handler against a loopback server.
            int hits = 0;
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var stop = new CancellationTokenSource();
            var server = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    TcpClient c;
                    try { c = await listener.AcceptTcpClientAsync(stop.Token); } catch { return; }
                    _ = Task.Run(async () =>
                    {
                        using (c)
                        {
                            Interlocked.Increment(ref hits);
                            var st = c.GetStream();
                            var buf = new byte[4096]; int n = 0;
                            while (n < buf.Length) { int r = await st.ReadAsync(buf.AsMemory(n)); if (r <= 0) break; n += r; if (Encoding.ASCII.GetString(buf, 0, n).Contains("\r\n\r\n")) break; }
                            var path = Encoding.ASCII.GetString(buf, 0, n).Split(' ').ElementAtOrDefault(1) ?? "/";
                            var resp = path == "/redirect"
                                ? "HTTP/1.1 302 Found\r\nLocation: http://198.51.100.7/\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                                : "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok";
                            await st.WriteAsync(Encoding.ASCII.GetBytes(resp));
                        }
                    });
                }
            });
            try
            {
                Offline.Set(true);
                using var http = new HttpClient(Offline.Handler()) { Timeout = TimeSpan.FromSeconds(10) };
                if (await http.GetStringAsync($"http://127.0.0.1:{port}/ok") != "ok") throw new Exception("loopback");
                // A name the fake resolver maps to 127.0.0.1: real DNS would fail, so a success proves the connect callback
                // connected to the address it checked.
                if (await http.GetStringAsync($"http://lab-loop.example:{port}/ok") != "ok") throw new Exception("vetted address");
                int connects = Offline.Connects, before = hits;
                var sw = Stopwatch.StartNew();
                try { await http.GetAsync("http://198.51.100.7/"); throw new Exception("public address reached"); }
                catch (OfflineBlockedException) { }
                if (Offline.Connects != connects) throw new Exception("a socket was opened for a blocked request");
                if (sw.ElapsedMilliseconds > 1000) throw new Exception("block took " + sw.ElapsedMilliseconds + " ms");
                var redirect = await http.GetAsync($"http://127.0.0.1:{port}/redirect");
                if ((int)redirect.StatusCode != 302) throw new Exception("redirect followed: " + (int)redirect.StatusCode);
                before = hits;
                try { await http.GetAsync($"http://rebind.example:{port}/ok"); throw new Exception("rebinding name reached a public address"); }
                catch (HttpRequestException ex) when (ex.InnerException is OfflineBlockedException) { }
                if (rebindCalls != 2) throw new Exception("rebind lookups " + rebindCalls);
                Offline.Set(false);
                if (await http.GetStringAsync($"http://127.0.0.1:{port}/ok") != "ok") throw new Exception("off: loopback");
                ok("offline HTTP: loopback and private-resolving names served (connected to the checked address); a public address refused before any socket (<1 s); redirects not followed; a DNS-rebinding answer caught at connect; off = normal");
            }
            catch (Exception ex) { fail("offline HTTP guard", ex); }
            finally { stop.Cancel(); listener.Stop(); try { await server; } catch { } }

            // 3. The real clients and launchers.
            var ai = (s.AiEnabled, s.AiEngine, s.OpenAiUrl, s.AiFeatureHelper);
            try
            {
                Offline.Set(true);
                int connects = Offline.Connects;
                s.AiEnabled = true; s.AiEngine = "openai"; s.OpenAiUrl = "https://api.openai.com/v1"; s.AiFeatureHelper = true;
                if (Ai.AiClient.Blocked(Ai.AiFeature.Helper) is not { } why || !why.Contains("Offline mode")) throw new Exception("AI gate: " + Ai.AiClient.Blocked(Ai.AiFeature.Helper));
                try { await Ai.AiClient.ModelsAsync(); throw new Exception("AI request sent"); } catch (OfflineBlockedException) { }
                s.OpenAiUrl = "http://10.0.0.5:8080/v1";
                if (Ai.AiClient.Blocked(Ai.AiFeature.Helper) != null) throw new Exception("LAN AI blocked");
                try { await new VultrClient(new VultrAccount { ApiKey = "invalid" }).GetAsync("/account"); throw new Exception("Vultr request sent"); } catch (OfflineBlockedException) { }
                var sw = Stopwatch.StartNew();
                try { await SshConnector.ConnectSshAsync(new HostEntry { Name = "offline-probe", Address = "198.51.100.7", Port = 22, Username = "t", KeepAliveSeconds = 0, IsAdHoc = true }); throw new Exception("SSH connected"); }
                catch (OfflineBlockedException) { }
                if (sw.ElapsedMilliseconds > 1000) throw new Exception("SSH block took " + sw.ElapsedMilliseconds + " ms");
                // A throwaway in-memory credential: without one the connector would PROMPT for a password (a visible window).
                var probeCred = new Credential { Name = "offline-probe", Kind = CredentialKind.Password, Secret = "not-a-real-password" };
                try { await SshConnector.ConnectSshAsync(new HostEntry { Name = "offline-probe", Address = "127.0.0.1", Port = 1, Username = "t", KeepAliveSeconds = 0, IsAdHoc = true, Auth = AuthMethod.Password, Transient = probeCred }); }
                catch (OfflineBlockedException) { throw new Exception("SSH to this PC blocked"); }
                catch { }   // refused: expected, port 1 has nothing
                if (Offline.Connects != connects) throw new Exception("a blocked client opened a socket");

                // Launchers: nothing may start (a mistake here would put mstsc / a web window on screen: the watchdog sees it).
                int webs = Application.Current.Windows.OfType<WebWindow>().Count();
                WebWindow.Open("probe", "https://my.vultr.com/");
                RdpLauncher.Launch(new HostEntry { Name = "offline-rdp", Address = "203.0.113.50", Port = 3389, Username = "t", IsAdHoc = true });
                await Settle(400);
                if (Application.Current.Windows.OfType<WebWindow>().Count() != webs) throw new Exception("web window opened");
                var recent = Offline.Blocked.Take(10).ToList();
                if (!recent.Any(b => b.Purpose.StartsWith("a web window") && b.Host == "my.vultr.com") || !recent.Any(b => b.Purpose == "Remote Desktop" && b.Host == "203.0.113.50"))
                    throw new Exception("launchers not refused: " + string.Join(" | ", recent.Select(b => b.Purpose + " " + b.Host)));
                if (Offline.AddonsBlocked() == null) throw new Exception("add-ons allowed offline by default");
                Offline.SetAllowed(Array.Empty<string>(), allowAddons: true);
                if (Offline.AddonsBlocked() != null) throw new Exception("add-on opt-in ignored");
                Offline.SetAllowed(Array.Empty<string>(), allowAddons: false);
                ok("offline clients: AI (gate reason + no request), Vultr API, SSH (refused in <1 s; this PC still reachable), web window and Remote Desktop refused before launch, add-ons refused unless allowed; no socket opened for any blocked call");
            }
            catch (Exception ex) { fail("offline clients", ex); }
            finally { (s.AiEnabled, s.AiEngine, s.OpenAiUrl, s.AiFeatureHelper) = ai; }

            // 4. What the user sees and what is kept.
            try
            {
                Offline.Set(true);
                await Settle(100);
                if (main.OfflineBadge.Visibility != Visibility.Visible || !(main.OfflineBadge.Content as string ?? "").Contains("OFFLINE MODE")) throw new Exception("badge");
                if (!(main.OfflineBadge.Content as string ?? "").Contains("blocked")) throw new Exception("badge count");
                var posture = PrivacyReport.PostureItems().First();
                if (posture.Name != "Offline mode" || !posture.Value.StartsWith("ON")) throw new Exception("privacy posture: " + posture.Value);
                if (PrivacyReport.Endpoints().First(e => e.Service == "Vultr API").State != "BLOCKED by offline mode") throw new Exception("privacy endpoint state");
                Offline.SetAllowed(new[] { "203.0.113.0/24" });
                var disk = SecureStore.Read<AppSettings>("settings.dcx");
                if (!disk.OfflineMode || !disk.OfflineAllowed.Contains("203.0.113.0/24")) throw new Exception("not persisted");
                if (Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(SecureStore.DataDir, "settings.dcx"))).Contains("203.0.113.0/24")) throw new Exception("plaintext approved list");
                var audit = AuditLog.Read(DateTime.Now.ToString("yyyy-MM"));
                if (!audit.Any(a => a.Target == "offline mode" && a.Action.StartsWith("blocked"))) throw new Exception("blocks not audited");
                main.Navigate("settings"); await Settle(300);
                SettingsView.Current?.RevealCard("Offline mode"); await Settle(300);
                var shots = Directory.CreateDirectory(Path.Combine(reportDir, "shots")).FullName;
                snap(main, Path.Combine(shots, "offline-mode.png"));
                Offline.Set(false); await Settle(100);
                if (main.OfflineBadge.Visibility == Visibility.Visible) throw new Exception("badge stayed after off");
                ok($"offline UI: OFFLINE MODE badge with the blocked count, Privacy posture + endpoint states, approved list persisted encrypted, blocks audited ({audit.Count(a => a.Target == "offline mode")} entries), Settings card rendered");
            }
            catch (Exception ex) { fail("offline UI", ex); }
        }
        finally
        {
            Offline.Resolver = realResolver;
            Offline.SetAllowed(saved.Item2, saved.Item3, saved.Item4);
            Offline.Set(saved.Item1);
        }
    }
}
