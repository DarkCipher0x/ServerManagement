using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DcxManager.Services;
using DcxManager.Ssh;
using DcxManager.Views;
using DcxManager.Vultr;
using DcxManager.Documents;

namespace DcxManager;

// SelfTest: every screen, terminal stabilisation, local shells and serial consoles (moved unchanged out of RunFullAsync in 0.2.0 phase 6½; the run's shared state is SelfTestRun).
public static partial class SelfTest
{
    private static async Task ScreensAndSessionsAsync(SelfTestRun sharedRun)
    {
        var main = sharedRun.Main; var reportPath = sharedRun.ReportPath; Action<string> Ok = sharedRun.Ok; Action<string, Exception?> Fail = sharedRun.Fail; SelfTestRun.SettleFn Settle = sharedRun.Settle;

        // 5. every screen
        var keys = new List<string> { "dashboard", "hosts", "snippets", "tunnels", "lan", "toolbox", "ansible", "credentials", "knownhosts", "apiexplorer", "assistant", "audit", "settings", "cloud", "vultr:reference", "contabo:reference", "hetzner:reference" };
        foreach (var cp in CloudProviders.All) keys.AddRange(cp.Catalog.Where(d => d.Group != "Reference").Select(cp.NavKey));
        foreach (var k in keys)
        {
            try { main.Navigate(k); await Settle(250); Ok("screen " + k); }
            catch (Exception ex) { Fail("screen " + k, ex); }
        }

        var testHost = AppData.Current.Hosts.First(h => h.Name == "selftest-host");
        try { main.OpenServer(testHost); await Settle(2500); Ok("server view (connection refused handled)"); } catch (Exception ex) { Fail("server view", ex); }
        try { main.OpenFiles(testHost); await Settle(2500); Ok("sftp view (connection refused handled)"); } catch (Exception ex) { Fail("sftp view", ex); }
        try
        {
            main.OpenTerminal(testHost);
            var term = main.Panes.AllTabs.Select(t => t.Content).OfType<TerminalView>().Last();
            for (int i = 0; i < 80 && !term.PageReady; i++) await Settle(250);   // poll up to 20s for WebView2 cold start
            if (!term.PageReady) throw new Exception("xterm page never reported ready — " + term.DiagStatus);
            // The host refuses connections: stop the auto-reconnect, or it retries (and logs) for the rest of the run and lands
            // in the fuzz case's error-log check at random (the s26/s28 "1 error-log entry" flake).
            term.Disconnect();
            Ok("terminal view (WebView2 + xterm page loaded and bridged)");
        }
        catch (Exception ex) { Fail("terminal view", ex); }

        // Stabilisation: a huge output flood must stay bounded (never exhaust memory).
        try
        {
            var t = new TerminalView(testHost, connectOnOpen: false);
            var chunk = Encoding.UTF8.GetBytes(new string('x', 16384));
            for (int i = 0; i < 6000; i++) t.TestFeed(chunk);   // ~96 MB fed with no drain
            var backlog = t.PendingChars;
            if (backlog > TerminalView.MaxPending) throw new Exception($"backlog not bounded: {backlog} > {TerminalView.MaxPending}");
            t.TestFlush();
            t.Dispose();
            Ok($"output flood bounded: fed ~96 MB, backlog capped at {backlog:N0} chars (limit {TerminalView.MaxPending:N0})");
        }
        catch (Exception ex) { Fail("output flood bounding", ex); }

        // Stabilisation: rapid terminal create/destroy must leave nothing behind.
        try
        {
            int before = main.Panes.AllTabs.Count();
            for (int i = 0; i < 12; i++)
            {
                var tab = main.AddTab("churn" + i, new TerminalView(testHost, connectOnOpen: false));
                await Settle(120);
                main.CloseTab(tab);
            }
            await Settle(300);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            if (main.Panes.AllTabs.Count() != before) throw new Exception($"tab leak: {main.Panes.AllTabs.Count()} != {before}");
            Ok("rapid terminal create/destroy x12: no residual tabs");
        }
        catch (Exception ex) { Fail("terminal churn", ex); }

        // Session providers (ADR-004): local shells via ConPTY — real process, real I/O, exit code, resize, no handle leak.
        try
        {
            var shells = Sessions.LocalShells.Discover();
            var cmd = shells.FirstOrDefault(s => s.Id == "cmd") ?? throw new Exception("cmd not discovered");
            if (shells.All(s => s.Id != "powershell")) throw new Exception("Windows PowerShell not discovered");
            if (Sessions.LocalShells.FromQuick("cmd")?.Id != "cmd" || Sessions.LocalShells.FromQuick("wsl:no-such-distro") != null || Sessions.LocalShells.FromQuick("web-01") != null) throw new Exception("quick-connect words");
            if (!Sessions.LocalShells.ValidDistroName("Ubuntu-22.04") || Sessions.LocalShells.ValidDistroName("a b") || Sessions.LocalShells.ValidDistroName("x;calc") || Sessions.LocalShells.ValidDistroName("\"x")) throw new Exception("distro name validation");
            var wsl = Sessions.LocalShells.WslDistributions();   // registry only; must not throw or start WSL

            async Task<(string Output, string? Closed, int Pid)> RunOnce(string marker)
            {
                var sb = new StringBuilder(); string? closed = null;
                var tr = new Sessions.ConPtyTransport(cmd.CommandLine + " /d /q /k", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                tr.Data += (b, n) => { lock (sb) sb.Append(Encoding.UTF8.GetString(b, 0, n)); };
                tr.Closed += r => closed = r;
                await tr.OpenAsync(120, 30);
                // %COMPUTERNAME:~0,0% expands to nothing, so only cmd's OUTPUT (not the echoed input) contains the marker.
                tr.Write($"echo {marker[..4]}%COMPUTERNAME:~0,0%{marker[4..]}\r");
                for (int i = 0; i < 100 && !Contains(); i++) await Task.Delay(100);
                bool Contains() { lock (sb) return sb.ToString().Contains(marker); }
                if (!Contains()) { tr.Dispose(); throw new Exception("no output from cmd via ConPTY: " + sb.ToString()[..Math.Min(300, sb.Length)]); }
                tr.Resize(100, 40);
                tr.Write("exit 7\r");
                for (int i = 0; i < 100 && closed == null; i++) await Task.Delay(100);
                int pid = tr.ProcessId;
                tr.Dispose();
                lock (sb) return (sb.ToString(), closed, pid);
            }

            var first = await RunOnce("DCX_CONPTY_OK");
            if (first.Closed == null || !first.Closed.Contains("code 7")) throw new Exception("exit not reported: " + first.Closed);
            await Task.Delay(500);
            try { if (!System.Diagnostics.Process.GetProcessById(first.Pid).HasExited) throw new Exception("shell process still running after dispose"); } catch (ArgumentException) { }

            GC.Collect(); GC.WaitForPendingFinalizers();
            int handlesBefore = System.Diagnostics.Process.GetCurrentProcess().HandleCount;
            for (int i = 0; i < 5; i++) await RunOnce("DCX_LOOP_" + i);
            await Task.Delay(1500);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            int handleGrowth = System.Diagnostics.Process.GetCurrentProcess().HandleCount - handlesBefore;
            if (handleGrowth > 40) throw new Exception($"handle growth after 5 local sessions: +{handleGrowth}");

            // The terminal tab itself: a live local shell through WebView2 + xterm, then ended by the user.
            var tabsBefore = main.Panes.AllTabs.Count();
            main.OpenLocal(cmd);
            var tab = main.Panes.AllTabs.First(t => t.Content is TerminalView { Target: Sessions.LocalTarget });
            var tv = tab.Content as TerminalView ?? throw new Exception("local tab is not a terminal");
            for (int i = 0; i < 120 && !tv.IsConnected; i++) await Settle(250);
            if (!tv.IsConnected) throw new Exception("local terminal never started — " + tv.DiagStatus);
            if (tv.Host != null || tv.Target is not Sessions.LocalTarget || MainWindow.ScreenName(tab) != "Terminal") throw new Exception("local tab identity");
            tv.Disconnect(); await Settle(300);
            if (tv.IsConnected) throw new Exception("still connected after End shell");
            main.CloseTab(tab); await Settle(300);
            if (main.Panes.AllTabs.Count() != tabsBefore) throw new Exception("local tab not closed");
            Ok($"session providers: {shells.Count} local shell(s) discovered ({string.Join(", ", shells.Select(s => s.Id))}; WSL distros {wsl.Count}); ConPTY round trip (output, resize, exit code 7, process gone); 6 sessions → handle growth {handleGrowth:+0;-0}; live local terminal tab via xterm; quick-connect words; distro-name validation");
        }
        catch (Exception ex) { Fail("session providers (local shells)", ex); }

        // Serial / USB-serial (no hardware on the build PC): parsing, USB identity, re-enumeration, the transport over a
        // fake device stream (encoding, line endings, echo, BREAK, unplug), capabilities, encrypted persistence, a tab
        // for a missing port that fails gracefully.
        try
        {
            var q1 = Sessions.SerialPorts.FromQuick("com3:115200");
            var q2 = Sessions.SerialPorts.FromQuick("COM7 9600 7E2");
            if (q1 is not { Port: "COM3", Baud: 115200 } || q2 is not { Port: "COM7", Baud: 9600, DataBits: 7, Parity: System.IO.Ports.Parity.Even, StopBits: System.IO.Ports.StopBits.Two }
                || Sessions.SerialPorts.FromQuick("COMX") != null || Sessions.SerialPorts.FromQuick("web-01") != null) throw new Exception("quick parse");
            if (Sessions.SerialPorts.UsbIdFrom(@"USB\VID_0403&PID_6001\A9ABC") != @"VID_0403&PID_6001\A9ABC"
                || Sessions.SerialPorts.UsbIdFrom(@"FTDIBUS\VID_0403+PID_6001+A9ABCA\0000") != @"VID_0403&PID_6001\A9ABCA"
                || Sessions.SerialPorts.UsbIdFrom(@"ACPI\PNP0501\1") != "") throw new Exception("usb id");
            var prof = new Sessions.SerialProfile { Name = "core-sw console (demo)", Port = "COM3", UsbId = @"VID_0403&PID_6001\A9ABC", Baud = 9600 };
            var moved = new[] { new Sessions.SerialPortInfo("COM7", "USB Serial Port", @"VID_0403&PID_6001\A9ABC") };
            var other = new[] { new Sessions.SerialPortInfo("COM3", "USB-SERIAL CH340", @"VID_1A86&PID_7523\5") };
            var plain = new[] { new Sessions.SerialPortInfo("COM3", "Communications Port", "") };
            if (Sessions.SerialPorts.Resolve(prof, moved) != "COM7") throw new Exception("re-enumerated adapter not followed");
            if (Sessions.SerialPorts.Resolve(prof, other) != null) throw new Exception("opened a different USB device on the old COM name");
            if (Sessions.SerialPorts.Resolve(prof, plain) != "COM3" || Sessions.SerialPorts.Resolve(prof, Array.Empty<Sessions.SerialPortInfo>()) != null) throw new Exception("resolve fallback");
            if (Sessions.SerialPorts.Validate(new Sessions.SerialProfile { Port = "LPT1" }) == null || Sessions.SerialPorts.Validate(new Sessions.SerialProfile { Baud = 0 }) == null || Sessions.SerialPorts.Validate(prof) != null) throw new Exception("validate");
            if (Sessions.SerialTransport.MapNewLines("a\r", "lf") != "a\n" || Sessions.SerialTransport.MapNewLines("a\r", "crlf") != "a\r\n" || Sessions.SerialTransport.MapNewLines("x\r\n", "crlf") != "x\r\n" || Sessions.SerialTransport.MapNewLines("a\r", "cr") != "a\r") throw new Exception("newline map");

            // Transport over a fake device: Latin-1 output → UTF-8, CR→CRLF, local echo, BREAK, then the cable is pulled.
            var fake = new FakeSerialStream();
            bool broke = false; string? closed = null; var got = new StringBuilder();
            var p2 = new Sessions.SerialProfile { Name = "fake", Port = "COM9", Encoding = "latin1", NewLine = "crlf", LocalEcho = true, Reconnect = true };
            var tr = new Sessions.SerialTransport(p2, _ => (fake, "COM9", ms => broke = true, fake));
            tr.Data += (b, n) => { lock (got) got.Append(Encoding.UTF8.GetString(b, 0, n)); };
            tr.Closed += r => closed = r;
            await tr.OpenAsync(80, 24);
            fake.Feed(new byte[] { (byte)'c', (byte)'a', (byte)'f', 0xE9, (byte)'>' });
            for (int i = 0; i < 50 && !got.ToString().Contains("café>"); i++) await Task.Delay(20);
            if (!got.ToString().Contains("café>")) throw new Exception("latin-1 output: " + got);
            tr.Write("show ver\r");
            if (Encoding.Latin1.GetString(fake.Written.ToArray()) != "show ver\r\n" || !got.ToString().Contains("show ver\r\n")) throw new Exception("write/echo");
            tr.SendBreak();
            if (!broke || !tr.IsOpen || !tr.Detail.Contains("COM9 · 9600 8N1")) throw new Exception("break/detail");
            fake.Unplug();
            for (int i = 0; i < 50 && closed == null; i++) await Task.Delay(20);
            if (closed == null || !closed.StartsWith("device removed")) throw new Exception("unplug not reported: " + closed);
            tr.Dispose();

            var target = new Sessions.SerialTarget(p2);
            if (!target.Has(Sessions.SessionCaps.AutoReconnect) || target.CanDuplicate || !target.ShouldRetry(new System.IO.IOException("x")) || target.ShouldRetry(new ArgumentException("bad"))) throw new Exception("capabilities");

            // Encrypted persistence.
            AppData.Current.SerialProfiles.Add(prof);
            AppData.Save();
            if (Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(SecureStore.DataDir, "data.dcx"))).Contains("core-sw console")) throw new Exception("plaintext profile in data.dcx");
            AppData.Load();
            if (AppData.Current.SerialProfiles.All(s => s.Name != "core-sw console (demo)")) throw new Exception("profile not persisted");

            // A tab for a port that isn't there: friendly failure, no crash, no reconnect loop when the profile says no.
            var gone = new Sessions.SerialProfile { Name = "missing", Port = "COM250", Reconnect = false };
            var tabsBefore = main.Panes.AllTabs.Count();
            main.OpenSession(new Sessions.SerialTarget(gone));
            var tab = main.Panes.AllTabs.First(t => t.Content is TerminalView { Target: Sessions.SerialTarget { Profile.Port: "COM250" } });
            var tv = (TerminalView)tab.Content;
            for (int i = 0; i < 60 && !tv.DiagStatus.Contains("not present"); i++) await Settle(250);
            if (!tv.DiagStatus.Contains("not present") || tv.IsConnected) throw new Exception("missing port: " + tv.DiagStatus);
            main.CloseTab(tab); await Settle(200);
            if (main.Panes.AllTabs.Count() != tabsBefore) throw new Exception("tab not closed");

            main.Navigate("serial"); await Settle(600);
            var shots = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(reportPath)!, "shots")).FullName;
            Snap(main, Path.Combine(shots, "serial.png"));
            Ok("serial: quick parse (COM3:115200, 7E2), USB identity (USB/FTDIBUS), re-enumeration followed, different device on old COM refused, validation, line endings; transport over a fake device: Latin-1→UTF-8, CR→CRLF, local echo, BREAK, unplug → \"device removed\"; caps (reconnect, no duplicate); encrypted persistence; missing-port tab fails gracefully; rendered serial.png");
        }
        catch (Exception ex) { Fail("serial", ex); }

        sharedRun.TestHost = testHost;   // the UI area renders with it
    }
}
