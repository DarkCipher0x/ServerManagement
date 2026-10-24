using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DcxManager.Services;
using DcxManager.Sessions;
using DcxManager.Views;

namespace DcxManager;

/// <summary>
/// Spec §65 / ADR-019: terminal profiles, scrollback search, key mappings, encrypted session logging, broadcast input,
/// tab groups and Telnet. Telnet talks to a loopback fake device; local sessions are real cmd.exe shells (ConPTY); no
/// dialog is reached (confirmations are answered by <see cref="Dialogs.ConfirmForTests"/>).
/// </summary>
public static class TerminalTests
{
    public static async Task RunAsync(MainWindow main, string reportDir, Action<string> ok, Action<string, Exception?> fail, Action<Window, string> snap)
    {
        async Task Settle(int ms = 200)
        {
            await Task.Delay(ms);
            await Application.Current.Dispatcher.InvokeAsync(() => main.UpdateLayout(), DispatcherPriority.ApplicationIdle);
        }
        async Task<string> Screen(TerminalView tv) => string.Join("\n", (await tv.CaptureScreenAsync())?.Lines ?? Array.Empty<string>());
        async Task<bool> WaitScreen(TerminalView tv, string text, int seconds = 15)
        {
            for (int i = 0; i < seconds * 4; i++) { if ((await Screen(tv)).Contains(text)) return true; await Task.Delay(250); }
            return false;
        }
        async Task WaitReady(TerminalView tv) { for (int i = 0; i < 80 && !tv.PageReady; i++) await Settle(250); if (!tv.PageReady) throw new Exception("terminal page never ready — " + tv.DiagStatus); }

        var s = AppSettings.Current;
        var was = (Profiles: s.TerminalProfiles.ToList(), Default: s.DefaultTerminalProfileId, Telnet: s.TelnetEnabled);
        var tabs = new List<TabItem>();
        var shots = Directory.CreateDirectory(Path.Combine(reportDir, "shots")).FullName;
        var cmd = LocalShells.Discover().FirstOrDefault(p => p.Id == "cmd");
        var logs = new List<string>();
        TabItem Add(string header, TerminalView tv) { var t = main.AddTab(header, tv); tabs.Add(t); return t; }
        try
        {
            // 1. Profiles: key chords, escapes, schemes, encodings, resolution, encrypted settings.
            try
            {
                var chords = new (string In, string? Out)[] { ("ctrl+shift+f5", "Ctrl+Shift+F5"), ("Shift+Ctrl+a", "Ctrl+Shift+A"), ("alt+x", "Alt+X"), ("F12", "F12"), ("pageup", "PageUp"),
                    ("x", null), ("shift+x", null), ("ctrl+", null), ("hyper+a", null), ("ctrl+shift+alt+meta+a", null), ("Enter", null), ("ctrl+enter", "Ctrl+Enter") };
                foreach (var (i, o) in chords) if (TerminalProfiles.NormalizeKeys(i) != o) throw new Exception($"chord '{i}' → '{TerminalProfiles.NormalizeKeys(i)}'");
                if (TerminalProfiles.ParseSend(@"\e[15;6~") != "\x1b[15;6~" || TerminalProfiles.ParseSend("^C") != "\x03" || TerminalProfiles.ParseSend(@"a\x41\u00e9\r\n\t\\") != "aAé\r\n\t\\"
                    || TerminalProfiles.ParseSend(@"\q") != null || TerminalProfiles.ParseSend("") != null || TerminalProfiles.ParseSend(new string('a', 300)) != null) throw new Exception("escapes");
                var (maps, errs) = TerminalProfiles.ParseKeyLines("# comment\nCtrl+Shift+F5 = \\e[15;6~\nctrl+shift+f5 = x\nq = nope\nAlt+K\nF2 = \\z");
                if (maps.Count != 1 || errs.Count != 4) throw new Exception($"key lines: {maps.Count} maps, errors: {string.Join(" | ", errs)}");
                foreach (var sc in TerminalProfiles.Schemes.Values)
                    if (sc.Ansi.Length != 16 || sc.Ansi.Append(sc.Bg).Append(sc.Fg).Any(c => !System.Text.RegularExpressions.Regex.IsMatch(c, "^#[0-9a-fA-F]{6}$"))) throw new Exception("scheme " + sc.Title);
                if (TerminalProfiles.EncodingFor("iso-8859-1").GetString(new byte[] { 0xE9 }) != "é" || TerminalProfiles.EncodingFor("ibm437").GetString(new byte[] { 0xC4 }) != "─"
                    || TerminalProfiles.EncodingFor("windows-1252").GetString(new byte[] { 0x80 }) != "€" || TerminalProfiles.EncodingFor("nonsense").WebName != "utf-8") throw new Exception("encodings");

                s.TerminalProfiles = new();
                var def = TerminalProfiles.Default;
                var solar = new TerminalProfile { Name = "dcx-solar", Scheme = "solarized-dark", CursorStyle = "underline", CursorBlink = false, Scrollback = 50_000, BackspaceCtrlH = true,
                    Keys = new() { new KeyMap { Keys = "Ctrl+Shift+F5", Send = @"\e[15;6~" } } };
                TerminalProfiles.All.Add(solar);
                var host = new HostEntry { Name = "prof-host", Address = "192.0.2.51", TerminalProfileId = solar.Id };
                if (TerminalProfiles.For(null) != def || TerminalProfiles.For(host) != solar || TerminalProfiles.For(new HostEntry { TerminalProfileId = "gone" }) != def) throw new Exception("resolution");
                if (TerminalProfiles.ScrollbackFor(new TerminalProfile { Scrollback = 5 }) != 1000 || TerminalProfiles.ScrollbackFor(solar) != 50_000) throw new Exception("scrollback clamp");
                AppSettings.Save();
                var raw = Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(SecureStore.DataDir, "settings.dcx")));
                if (raw.Contains("dcx-solar") || raw.Contains("solarized")) throw new Exception("profiles stored in plaintext");
                if (SecureStore.Read<AppSettings>("settings.dcx").TerminalProfiles.All(p => p.Name != "dcx-solar")) throw new Exception("profile not saved");
                ok("terminal profiles: key chords normalised (plain letters need Ctrl/Alt), escapes \\e \\xHH \\uHHHH ^X, key-map lines with per-line errors, 4 colour schemes complete, encodings UTF-8 / Latin-1 / 1252 / IBM437, host → profile → default resolution, scrollback clamp, saved encrypted");
            }
            catch (Exception ex) { fail("terminal profiles", ex); }

            // 2. Telnet protocol against a loopback fake device.
            try
            {
                s.TelnetEnabled = false;
                try { await new TelnetTransport("127.0.0.1", 9).OpenAsync(80, 24); throw new Exception("connected while Telnet is switched off"); }
                catch (TelnetDisabledException) { }
                s.TelnetEnabled = true;
                var parse = new[] { ("telnet 192.0.2.60", "192.0.2.60", 23), ("telnet 192.0.2.60 2323", "192.0.2.60", 2323), ("TELNET switch-01:2601", "switch-01", 2601), ("telnet://192.0.2.61:24/", "192.0.2.61", 24) };
                foreach (var (t, a, p) in parse) if (MainWindow.ParseTelnet(t) is not { IsTelnet: true } h || h.Address != a || h.Port != p) throw new Exception("quick connect: " + t);
                if (MainWindow.ParseTelnet("telnet") != null || MainWindow.ParseTelnet("user@host") != null || MainWindow.ParseTelnet("telnet a 70000") != null) throw new Exception("quick connect accepted junk");

                var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                var got = new List<byte>();
                var server = Task.Run(async () =>
                {
                    using var c = await listener.AcceptTcpClientAsync();
                    var st = c.GetStream();
                    byte I = 255;
                    await st.WriteAsync(new byte[] { I, 253, 31, I, 253, 24, I, 251, 1, I, 251, 3, I, 253, 153, I, 253, 31 });   // DO NAWS, DO TTYPE, WILL ECHO, WILL SGA, DO 153, DO NAWS again
                    await st.WriteAsync(Encoding.ASCII.GetBytes("hello"));
                    await st.WriteAsync(new byte[] { I, I, (byte)'!', 0, I, 250, 24, 1, I, 240 });   // data 255, NUL padding, SB TTYPE SEND
                    var buf = new byte[4096];
                    var until = DateTime.UtcNow.AddSeconds(8);
                    Task<int>? read = null;   // one read at a time: an abandoned read would swallow the next bytes
                    while (DateTime.UtcNow < until)
                    {
                        read ??= st.ReadAsync(buf, 0, buf.Length);
                        if (await Task.WhenAny(read, Task.Delay(300)) != read) { lock (got) if (got.Count >= 59) break; continue; }
                        int n = read.Result; read = null;
                        if (n <= 0) break;
                        lock (got) got.AddRange(buf.Take(n));
                    }
                });
                var data = new List<byte>();
                var tr = new TelnetTransport("127.0.0.1", port);
                tr.Data += (b, n) => { lock (data) data.AddRange(b.Take(n)); };
                await tr.OpenAsync(100, 30);
                for (int i = 0; i < 40; i++) { lock (data) if (data.Count >= 7) break; await Task.Delay(100); }
                tr.Resize(120, 40);
                tr.WriteBytes(new byte[] { (byte)'a', (byte)'b', (byte)'\r', 255 });
                await server;
                tr.Dispose(); listener.Stop();
                byte[] d; lock (data) d = data.ToArray();
                if (!d.SequenceEqual(Encoding.ASCII.GetBytes("hello").Concat(new byte[] { 255, (byte)'!' }))) throw new Exception("data: " + BitConverter.ToString(d));
                byte[] g; lock (got) g = got.ToArray();
                bool Has(params byte[] seq) { for (int i = 0; i + seq.Length <= g.Length; i++) if (g.Skip(i).Take(seq.Length).SequenceEqual(seq)) return true; return false; }
                int Count(params byte[] seq) { int n = 0; for (int i = 0; i + seq.Length <= g.Length; i++) if (g.Skip(i).Take(seq.Length).SequenceEqual(seq)) n++; return n; }
                var checks = new (string What, byte[] Seq)[]
                {
                    ("WILL NAWS", new byte[] { 255, 251, 31 }), ("NAWS 100x30", new byte[] { 255, 250, 31, 0, 100, 0, 30, 255, 240 }), ("NAWS 120x40 after resize", new byte[] { 255, 250, 31, 0, 120, 0, 40, 255, 240 }),
                    ("WILL TTYPE", new byte[] { 255, 251, 24 }), ("DO ECHO", new byte[] { 255, 253, 1 }), ("DO SGA", new byte[] { 255, 253, 3 }), ("WONT 153", new byte[] { 255, 252, 153 }),
                    ("TTYPE IS", new byte[] { 255, 250, 24, 0 }.Concat(Encoding.ASCII.GetBytes("XTERM-256COLOR")).Concat(new byte[] { 255, 240 }).ToArray()),
                    ("CR → CR LF and IAC doubled", new byte[] { (byte)'a', (byte)'b', 13, 10, 255, 255 }),
                };
                foreach (var (what, seq) in checks) if (!Has(seq)) throw new Exception("client didn't send " + what + ": " + BitConverter.ToString(g));
                if (Count(255, 251, 31) != 1) throw new Exception("answered the repeated DO NAWS again (negotiation loop risk)");
                ok("Telnet: off by default and refused while off; quick-connect forms; loopback device: NAWS (and again after a resize), TTYPE XTERM-256COLOR, ECHO/SGA agreed, unknown options refused, a repeated request not re-answered, IAC escaped both ways, NUL padding dropped, Enter = CR LF");
            }
            catch (Exception ex) { fail("Telnet protocol", ex); }

            // 3. A Telnet tab: Latin-1 profile, output decoded, input sent, host tools refused, environment and dashboard kind.
            try
            {
                s.TelnetEnabled = true;
                var latin = new TerminalProfile { Name = "dcx-latin1", Encoding = "iso-8859-1" };
                TerminalProfiles.All.Add(latin);
                var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                var typed = new List<byte>();
                var server = Task.Run(async () =>
                {
                    using var c = await listener.AcceptTcpClientAsync();
                    var st = c.GetStream();
                    await st.WriteAsync(Encoding.Latin1.GetBytes("Welcome to dcx-switch\r\ncaf\u00e9 login: "));
                    var buf = new byte[1024];
                    // Generous: under load the page can take a while before the test types (s28 endurance run). It ends as soon as the line arrives.
                    var until = DateTime.UtcNow.AddSeconds(60);
                    Task<int>? read = null;   // one read at a time: an abandoned read would swallow the typed line (the s28 flake)
                    while (DateTime.UtcNow < until)
                    {
                        read ??= st.ReadAsync(buf, 0, buf.Length);
                        if (await Task.WhenAny(read, Task.Delay(300)) != read) { lock (typed) if (Encoding.Latin1.GetString(typed.ToArray()).Contains("\r\n")) break; continue; }
                        int n = read.Result; read = null;
                        if (n <= 0) break;
                        lock (typed) typed.AddRange(buf.Take(n));
                        lock (typed) if (Encoding.Latin1.GetString(typed.ToArray()).Contains("\r\n")) break;
                    }
                });
                var th = new HostEntry { Name = "dcx-switch", Address = "127.0.0.1", Port = port, Protocol = "telnet", Environment = "LAB", TerminalProfileId = latin.Id, IsAdHoc = true };
                var tv = new TerminalView(new TelnetTarget(th), null, connectOnOpen: true);
                var tab = Add("⌁ dcx-switch", tv);
                main.Panes.Select(tab);
                await WaitReady(tv);
                for (int i = 0; i < 40 && !tv.IsConnected; i++) await Settle(250);
                if (!tv.IsConnected) throw new Exception("Telnet tab didn't connect — " + tv.DiagStatus);
                if (!await WaitScreen(tv, "café login:")) throw new Exception("Latin-1 output not decoded: " + await Screen(tv));
                if (!tv.DiagStatus.Contains("unencrypted")) throw new Exception("status doesn't say unencrypted: " + tv.DiagStatus);
                await tv.OnInputAsync("ops\u00e9\r");
                await server; listener.Stop();
                byte[] t2; lock (typed) t2 = typed.ToArray();
                if (!t2.SequenceEqual(Encoding.Latin1.GetBytes("ops\u00e9\r\n"))) throw new Exception("input not Latin-1 / CR LF: " + BitConverter.ToString(t2));
                if (Overview.Classify(tv)?.Kind != "Telnet" || EnvTab.GetLabel(tab) != "LAB") throw new Exception("dashboard kind / environment label");
                int before = main.Panes.AllTabs.Count();
                main.OpenFiles(th); main.OpenServer(th); main.OpenCommander(th);
                if (main.Panes.AllTabs.Count() != before) throw new Exception("files / manage / commander opened for a Telnet host");
                var audit = AuditLog.Read(DateTime.Now.ToString("yyyy-MM"));
                if (!audit.Any(a => a.Target == "dcx-switch" && a.Action == "opened Telnet session (unencrypted)")) throw new Exception("Telnet session not audited");
                main.CloseTab(tab); tabs.Remove(tab);
                ok("Telnet tab: connects to the loopback device, Latin-1 profile decodes output (café) and encodes input, Enter = CR LF, status says unencrypted, LAB label, dashboard counts it as Telnet, Files / Manage / Commander refused, audited");
            }
            catch (Exception ex) { fail("Telnet tab", ex); }

            // 4. Page features: profile applied, key mappings, Backspace ^H, scrollback search (wrapped lines, case, regex, errors).
            TerminalView? featureTerm = null; TabItem? featureTab = null;
            try
            {
                if (cmd == null) throw new Exception("cmd not discovered");
                var solar = TerminalProfiles.All.First(p => p.Name == "dcx-solar");
                featureTerm = new TerminalView(new LocalTarget(cmd), null, connectOnOpen: false);
                featureTab = Add("❯ features", featureTerm);
                main.Panes.Select(featureTab);
                await WaitReady(featureTerm);
                featureTerm.ApplyProfile(solar, session: true);
                await Settle(300);
                var probe = await featureTerm.ProbeAsync() ?? throw new Exception("page didn't answer the probe");
                if (probe.GetProperty("cursorStyle").GetString() != "underline" || probe.GetProperty("blink").GetBoolean() || probe.GetProperty("scrollback").GetInt32() != 50_000
                    || probe.GetProperty("bg").GetString() != "#002b36" || probe.GetProperty("red").GetString() != "#dc322f" || !probe.GetProperty("bs").GetBoolean()
                    || !probe.GetProperty("keys").EnumerateArray().Any(k => k.GetString() == "Ctrl+Shift+F5")) throw new Exception("profile not applied: " + probe);
                if (!featureTerm.SessionProfile || featureTerm.Profile != solar) throw new Exception("session profile flag");
                featureTerm.PressKey("F5", "F5", ctrl: true, shift: true);
                for (int i = 0; i < 20 && featureTerm.LastInput != "\x1b[15;6~"; i++) await Task.Delay(100);
                if (featureTerm.LastInput != "\x1b[15;6~") throw new Exception("key mapping sent: " + Escape(featureTerm.LastInput));
                featureTerm.PressKey("Backspace", "Backspace");
                for (int i = 0; i < 20 && featureTerm.LastInput != "\x08"; i++) await Task.Delay(100);
                if (featureTerm.LastInput != "\x08") throw new Exception("Backspace = ^H: " + Escape(featureTerm.LastInput));

                var sb = new StringBuilder();
                for (int i = 1; i <= 400; i++) sb.Append(i switch { 50 or 150 or 250 => $"line {i} needle here", 260 => $"line {i} NEEDLE upper", _ => $"line {i}" }).Append("\r\n");
                sb.Append(new string('x', 700)).Append("wrapword").Append(new string('y', 300)).Append("\r\n");
                featureTerm.TestFeed(Encoding.UTF8.GetBytes(sb.ToString()));
                featureTerm.TestFlush();
                await Settle(800);
                var r = await featureTerm.FindAsync("needle", "prev", false, false) ?? throw new Exception("no find answer");
                if (r is not { Count: 4, Index: 4 }) throw new Exception($"find: {r}");
                r = (await featureTerm.FindAsync("needle", "prev", false, false))!;
                if (r.Index != 3) throw new Exception("previous: " + r);
                r = (await featureTerm.FindAsync("needle", "next", false, false))!;
                if (r.Index != 4) throw new Exception("next: " + r);
                var probe2 = await featureTerm.ProbeAsync();
                if (probe2?.GetProperty("sel").GetString()?.ToLowerInvariant() != "needle") throw new Exception("match not selected: " + probe2);
                if ((await featureTerm.FindAsync("needle", "prev", true, false))!.Count != 3) throw new Exception("match case");
                if ((await featureTerm.FindAsync("ne+dle", "prev", false, true))!.Count != 4) throw new Exception("regex");
                if ((await featureTerm.FindAsync("wrapword", "prev", false, false))!.Count != 1) throw new Exception("a word on a wrapped line wasn't found");
                if ((await featureTerm.FindAsync("(", "prev", false, true))?.Error is not { Length: > 0 }) throw new Exception("bad regex not reported");
                if ((await featureTerm.FindAsync("zzz-not-there", "prev", false, false))!.Count != 0) throw new Exception("no-match");
                featureTerm.ShowFind(true);
                await featureTerm.FindAsync("needle", "prev", false, false);
                await Settle(300);
                if (!featureTerm.FindVisible || featureTerm.FindCountText != "4 of 4") throw new Exception("find bar: " + featureTerm.FindCountText);
                ok("terminal page: profile applied (solarized colours, underline steady cursor, 50,000 lines, keys), key mapping and Backspace ^H through the page's own key handler, scrollback search over 400 lines (4 matches, newest first, previous/next, match case, regex, wrapped line, bad regex reported, no match), find bar shows “4 of 4”");
            }
            catch (Exception ex) { fail("terminal page features", ex); }

            // 5. Session logging: the logger, encryption at rest, a real logged cmd session, the Session logs screen.
            try
            {
                using (var lg = new SessionLogger("unit · test", timestamps: true) { Clock = () => new DateTime(2026, 10, 18, 9, 30, 0) })
                {
                    logs.Add(lg.Path);
                    lg.Write("\x1b[31mred\x1b[0m text\r\nprogress 10%\rprogress 100%\r\n\x1b]0;title\u0007after osc\r\nsplit \x1b[3");
                    lg.Write("2mgreen\x1b[0m end\r\nab\bc\r\nrow one\x1b[7;1Hrow two\r\nsecret-free tail");
                }
                var text = SessionLog.Read(logs[0]);
                string[] want = { "[2026-10-18 09:30:00] red text", "progress 100%", "after osc", "split green end", "ac", "] row one\n", "] row two\n", "secret-free tail" };
                foreach (var w in want) if (!text.Contains(w)) throw new Exception($"log missing “{w}”: {text}");
                if (text.Contains('\x1b') || text.Contains("10%\r") || text.Contains("title")) throw new Exception("escape / overwrite leftovers: " + text);
                var raw = File.ReadAllText(logs[0]);
                if (raw.Contains("red text") || raw.Contains("unit · test")) throw new Exception("log readable on disk");
                if (Path.GetFileName(logs[0]).Contains("unit")) throw new Exception("session title in the file name");
                if (SessionLog.List().FirstOrDefault(e => e.Path == logs[0]) is not { Title: "unit · test" }) throw new Exception("list header");

                if (cmd == null) throw new Exception("cmd not discovered");
                var logged = new TerminalProfile { Name = "dcx-logged", Log = true, LogTimestamps = true };
                TerminalProfiles.All.Add(logged);
                var lt = new TerminalView(new LocalTarget(cmd), "echo DCX_LOG%COMPUTERNAME:~0,0%_MARK", connectOnOpen: false);
                var ltab = Add("❯ logged", lt);
                main.Panes.Select(ltab);
                await WaitReady(lt);
                lt.ApplyProfile(logged, session: true);
                lt.SetLogging(true);
                await lt.ConnectAsync();
                if (!await WaitScreen(lt, "DCX_LOG_MARK")) throw new Exception("cmd output never arrived");
                var path = lt.LogPath ?? throw new Exception("no log started");
                logs.Add(path);
                main.CloseTab(ltab); tabs.Remove(ltab);
                await Settle(300);
                var live = SessionLog.Read(path);
                // The marker is a line of its own (ConPTY moves the cursor instead of printing CR LF; that must still break the line).
                if (!System.Text.RegularExpressions.Regex.IsMatch(live, @"^\[\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\] DCX_LOG_MARK\s*$", System.Text.RegularExpressions.RegexOptions.Multiline))
                    throw new Exception("live log: " + live[..Math.Min(400, live.Length)].Replace("\n", " ⏎ "));
                if (File.ReadAllText(path).Contains("DCX_LOG_MARK")) throw new Exception("live log readable on disk");
                if (!AuditLog.Read(DateTime.Now.ToString("yyyy-MM")).Any(a => a.Action == "session logging started")) throw new Exception("logging start not audited");

                main.Navigate("sessionlogs");
                await Settle(400);
                var view = SessionLogsView.Current ?? throw new Exception("no Session logs screen");
                view.Select(path);
                await Settle(300);
                if (view.Count < 2 || !view.ShownText.Contains("DCX_LOG_MARK")) throw new Exception("Session logs screen: " + view.Count);
                snap(main, Path.Combine(shots, "session-logs.png"));
                SessionLog.Delete(logs[0]);
                try { SessionLog.Delete(Path.Combine(SecureStore.DataDir, "settings.dcx")); throw new Exception("deleted a non-log file"); } catch (ArgumentException) { }
                view.Reload();
                if (SessionLog.List().Any(e => e.Path == logs[0])) throw new Exception("delete");
                ok("session logging: escape sequences and CR overwrites resolved, split sequences joined, backspace applied, per-line timestamps, encrypted on disk (text and title), no host name in the file name; a real cmd session logged from its profile; logging audited; Session logs screen lists and decrypts it; delete limited to log files; rendered session-logs.png");
            }
            catch (Exception ex) { fail("session logging", ex); }

            // 6. Broadcast input between two real shells; production confirmation; stop; a closed member ends it.
            try
            {
                if (cmd == null) throw new Exception("cmd not discovered");
                var a = new TerminalView(new LocalTarget(cmd), null, connectOnOpen: false);
                var b = new TerminalView(new LocalTarget(cmd), null, connectOnOpen: false);
                var ta = Add("❯ bcast-a", a); var tb = Add("❯ bcast-b", b);
                main.Panes.Select(ta); await WaitReady(a);
                main.Panes.Select(tb); await WaitReady(b);
                await a.ConnectAsync(); await b.ConnectAsync();
                for (int i = 0; i < 60 && !(a.IsConnected && b.IsConnected); i++) await Settle(250);
                if (!a.IsConnected || !b.IsConnected) throw new Exception("shells didn't start");

                Dialogs.ConfirmForTests = false;
                if (InputBroadcast.Start(new[] { a, b }, "test") || InputBroadcast.Members.Count != 0) throw new Exception("started without consent");
                Dialogs.ConfirmForTests = true;
                Dialogs.SelfTestConfirms.Clear();
                if (!InputBroadcast.Start(new[] { a, b }, "test")) throw new Exception("refused");
                if (!a.InputBannerShown || !b.InputBannerShown) throw new Exception("member strip missing");
                await a.OnInputAsync("echo BCAST%COMPUTERNAME:~0,0%_OK\r");
                if (!await WaitScreen(a, "BCAST_OK") || !await WaitScreen(b, "BCAST_OK")) throw new Exception("typed text didn't reach both shells");
                InputBroadcast.Stop("test");
                if (a.InputBannerShown || b.InputBannerShown) throw new Exception("strip left after stop");
                await a.OnInputAsync("echo ONLY%COMPUTERNAME:~0,0%_A\r");
                await WaitScreen(a, "ONLY_A");
                await Task.Delay(800);
                if ((await Screen(b)).Contains("ONLY_A")) throw new Exception("input still broadcast after stop");

                var prodHost = new HostEntry { Name = "bcast-prod", Address = "192.0.2.70", Username = "ops", Environment = "PRODUCTION", IsAdHoc = true };
                var prodTerm = new TerminalView(prodHost, connectOnOpen: false);
                var tp = Add("⌨ bcast-prod", prodTerm);
                Dialogs.SelfTestConfirms.Clear();
                InputBroadcast.Start(new[] { a, prodTerm }, "test");
                if (Dialogs.SelfTestConfirms.LastOrDefault() is not { TypeToConfirm: "PRODUCTION", Danger: true }) throw new Exception("production set didn't ask for PRODUCTION");
                main.CloseTab(tp); tabs.Remove(tp);
                if (InputBroadcast.Members.Count != 0 || a.InputBannerShown) throw new Exception("closing a member left a one-terminal broadcast");

                // Tab groups on the same tabs.
                TabGroups.Assign(ta, "web");
                TabGroups.Assign(tb, "WEB");
                if (TabGroups.Members("web").Count != 2 || TabGroups.GetName(tb) != "web" || TabGroups.GetBrush(ta) == null || !TabGroups.GetShown(ta)) throw new Exception("group membership");
                if (TabGroups.Validate("") == null || TabGroups.Validate(new string('g', 30)) == null) throw new Exception("group name validation");
                main.Panes.Select(ta);
                await Settle(300);
                if (ta.Template?.FindName("grp", ta) is not Border { Visibility: Visibility.Visible }) throw new Exception("group label not shown on the tab");
                Dialogs.SelfTestConfirms.Clear();
                if (!InputBroadcast.Start(TabGroups.Members("web").Select(t => t.Content).OfType<TerminalView>(), "group “web”") || InputBroadcast.Members.Count != 2) throw new Exception("group broadcast");
                if (featureTab != null) TabGroups.Assign(featureTab, "web");
                main.Panes.Select(ta);   // a member: its strip shows in the render
                await Settle(400);
                if (!a.InputBannerShown) throw new Exception("member strip missing (group)");
                snap(main, Path.Combine(shots, "terminal-features.png"));
                InputBroadcast.Stop("test");
                TabGroups.Remove(tb);
                if (TabGroups.Members("web").Count != (featureTab != null ? 2 : 1) || TabGroups.GetShown(tb)) throw new Exception("remove from group");
                ok("broadcast input: asks first (refusal starts nothing), both real shells receive what is typed in one, every member shows the strip, Stop ends it, a PRODUCTION member means typing PRODUCTION, a closed member ends a two-terminal broadcast; tab groups: case-insensitive names, colour + printed label on the tab, broadcast to a group, remove; rendered terminal-features.png");
            }
            catch (Exception ex) { fail("broadcast input / tab groups", ex); }
            finally { Dialogs.ConfirmForTests = null; Dialogs.SelfTestConfirms.Clear(); InputBroadcast.Stop(null); }
        }
        finally
        {
            Dialogs.ConfirmForTests = null;
            InputBroadcast.Stop(null);
            foreach (var t in tabs.ToList()) { try { TabGroups.Remove(t); main.CloseTab(t); } catch { } }
            foreach (var l in logs) { try { if (File.Exists(l)) File.Delete(l); } catch { } }
            s.TerminalProfiles = was.Profiles; s.DefaultTerminalProfileId = was.Default; s.TelnetEnabled = was.Telnet;
            try { AppSettings.Save(); } catch { }
        }
    }

    private static string Escape(string s) => string.Concat(s.Select(c => c < 32 ? $"\\x{(int)c:X2}" : c.ToString()));
}
