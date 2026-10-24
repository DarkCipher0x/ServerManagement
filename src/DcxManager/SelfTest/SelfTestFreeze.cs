using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DcxManager.Services;
using DcxManager.Views;

namespace DcxManager;

/// <summary>
/// s37: DarkCipher0x's freeze. Working in terminals, then opening Cloud providers froze the app behind a blank white error box,
/// and the log filled with hundreds of identical entries. These tests cover:
/// (1) the guard's decisions: once logged, then counted, then a loop;
/// (2) a screen that throws on every layout pass: the app stays responsive, logs the failure a bounded number of times
///     and closes that screen, never a terminal;
/// (3) the real path: live terminals, then the Cloud providers workspace and every screen of every provider, measuring
///     how long the UI thread is held;
/// (4) terminals never paint the browser's white default background.
/// No window or dialog is shown.
/// </summary>
public static class FreezeTests
{
    /// <summary>Throws while being measured, every time: the shape of a drawing error that comes back on each pass.</summary>
    private sealed class LoopingElement : FrameworkElement
    {
        public int Measures;
        protected override Size MeasureOverride(Size availableSize)
        {
            Measures++;
            InvalidateMeasure();   // like a real oscillation: the next pass measures again
            throw new InvalidOperationException("s37 freeze probe: measure always fails");
        }
    }

    public static async Task RunAsync(MainWindow main, Action<string> ok, Action<string, Exception?> fail)
    {
        var d = Application.Current.Dispatcher;
        async Task Settle(int ms = 200) { await Task.Delay(ms); await d.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); }
        // How long the UI thread takes to answer: a frozen UI never answers within the limit.
        async Task<double> Responsive(int limitMs = 5000)
        {
            var sw = Stopwatch.StartNew();
            var op = d.InvokeAsync(() => { }, DispatcherPriority.Input);
            var done = await Task.WhenAny(op.Task, Task.Delay(limitMs));
            if (done != op.Task) throw new Exception($"the UI thread did not answer within {limitMs} ms (frozen)");
            return sw.Elapsed.TotalMilliseconds;
        }

        // 1. Decisions.
        try
        {
            UiErrorGuard.ResetForTest();
            var ex = new InvalidOperationException("probe");
            try { throw ex; } catch { }
            var t0 = DateTime.UtcNow;
            var seq = Enumerable.Range(0, 12).Select(i => UiErrorGuard.Classify(ex, t0.AddMilliseconds(i * 50))).ToList();
            if (seq[0] != UiErrorGuard.Verdict.LogAndShow || seq[1] != UiErrorGuard.Verdict.LogOnly) throw new Exception("first: " + string.Join(",", seq));
            if (seq.Count(v => v == UiErrorGuard.Verdict.Storm) != 1 || seq.IndexOf(UiErrorGuard.Verdict.Storm) != UiErrorGuard.StormCount) throw new Exception("storm: " + string.Join(",", seq));
            if (seq.Skip(UiErrorGuard.StormCount + 1).Any(v => v != UiErrorGuard.Verdict.Count)) throw new Exception("after the storm: " + string.Join(",", seq));
            UiErrorGuard.ResetForTest();
            var slow = new[] { 0, 2, 4 }.Select(s => UiErrorGuard.Classify(ex, t0.AddSeconds(s * 40))).ToList();   // rare repeats: shown again after a minute
            if (slow[0] != UiErrorGuard.Verdict.LogAndShow || slow[1] != UiErrorGuard.Verdict.LogAndShow || slow[2] != UiErrorGuard.Verdict.LogAndShow) throw new Exception("rare repeats: " + string.Join(",", slow));
            UiErrorGuard.ResetForTest();
            ok("UI error guard: the first failure is logged and shown, quick repeats are logged once then only counted, the 9th within 3 s is a loop, rare repeats are shown again after a minute");
        }
        catch (Exception ex) { fail("UI error guard decisions", ex); }

        // 1b. The exact error in DarkCipher0x's log (02:33, 02:42): WPF's "processing has been suspended" notice. Logged once, never a
        //     box, never a closed screen, however often it comes.
        try
        {
            UiErrorGuard.ResetForTest();
            var logBefore = LogEntries();
            int prompts; lock (Dialogs.SelfTestPrompts) prompts = Dialogs.SelfTestPrompts.Count;
            var tabsBefore = main.Panes.AllTabs.Count();
            const string notice = "Dispatcher processing has been suspended, but messages are still being processed. (s37 freeze probe)";
            if (!UiErrorGuard.IsDispatcherReentrancy(new InvalidOperationException(notice)) || UiErrorGuard.IsDispatcherReentrancy(new InvalidOperationException("other"))) throw new Exception("detection");
            for (int i = 0; i < 20; i++) _ = d.BeginInvoke(() => throw new InvalidOperationException(notice));
            await Settle(400);
            await Responsive();
            var logged = LogEntries() - logBefore;
            int promptsNow; lock (Dialogs.SelfTestPrompts) promptsNow = Dialogs.SelfTestPrompts.Count;
            if (promptsNow != prompts) throw new Exception("an error box was requested for the notice");
            if (logged != 1) throw new Exception($"{logged} log entries for 20 notices (expected 1)");
            if (main.Panes.AllTabs.Count() != tabsBefore) throw new Exception("a screen was closed for a harmless notice");
            UiErrorGuard.ResetForTest();
            ok("Dispatcher notice (DarkCipher0x's freeze): 20× “processing has been suspended” → 1 log entry, no error box, no screen closed, UI answering");
        }
        catch (Exception ex) { fail("dispatcher notice", ex); }

        // 1c. A refused SSH sign-in isn't auto-retried (DarkCipher0x's log: every 30 s all night), a network failure still is.
        try
        {
            var host = new HostEntry { Name = "demo", Address = "192.0.2.10", Port = 22, Username = "root" };
            var t = new Sessions.SshTarget(host);
            var refused = new InvalidOperationException("Authentication failed", new Renci.SshNet.Common.SshAuthenticationException("Permission denied (publickey)."));
            var down = new InvalidOperationException("Timed out", new Renci.SshNet.Common.SshOperationTimeoutException("no answer"));
            if (t.ShouldRetry(refused)) throw new Exception("a refused sign-in would be retried");
            if (!t.ShouldRetry(down)) throw new Exception("a network failure must still be retried");
            ok("SSH auto-reconnect: a refused sign-in (publickey/password) stops retrying and says why; timeouts and refused connections still retry");
        }
        catch (Exception ex) { fail("auth failure retry", ex); }

        // 2. A screen that throws on every layout pass, next to a live terminal.
        TabItem? term = null;
        try
        {
            var cmd = Sessions.LocalShells.Discover().FirstOrDefault(s => s.Id == "cmd") ?? throw new Exception("cmd not discovered");
            main.OpenLocal(cmd);
            term = main.Panes.AllTabs.Last(t => t.Content is TerminalView);
            var tv = (TerminalView)term.Content;
            for (int i = 0; i < 120 && !tv.IsConnected; i++) await Settle(250);
            if (!tv.IsConnected) throw new Exception("terminal never started — " + tv.DiagStatus);

            UiErrorGuard.ResetForTest();
            var logBefore = LogEntries();
            var probe = new LoopingElement();
            var sw = Stopwatch.StartNew();
            TabItem tab;
            // Showing it may lay it out at once (the exception then comes straight back here, as it would into a click
            // handler); the passes after that go through the dispatcher, where the guard sees them.
            try { tab = main.AddTab("✕ freeze probe", new Border { Child = probe }); main.Panes.Select(tab); }
            catch (InvalidOperationException e) when (e.Message.Contains("s37 freeze probe")) { tab = main.Panes.AllTabs.First(t => t.Header as string == "✕ freeze probe"); }
            double worst = 0;
            for (int i = 0; i < 10; i++)
            {
                await Task.Delay(150);
                try { worst = Math.Max(worst, await Responsive()); }
                catch (InvalidOperationException e) when (e.Message.Contains("s37 freeze probe")) { }
            }
            var stillOpen = main.Panes.AllTabs.Contains(tab);
            if (stillOpen) main.CloseTab(tab);
            var logged = LogEntries() - logBefore;
            lock (Dialogs.SelfTestPrompts) Dialogs.SelfTestPrompts.RemoveAll(p => p.Contains("s37 freeze probe"));
            if (stillOpen) throw new Exception($"the looping screen was not closed ({probe.Measures} measures)");
            if (!main.Panes.AllTabs.Contains(term) || !tv.IsConnected) throw new Exception("the terminal must never be closed by the guard");
            if (logged > 4) throw new Exception($"{logged} log entries for one looping failure (expected ≤ 4)");
            if (worst > 2000) throw new Exception($"the UI thread was held {worst:0} ms");
            ok($"UI error loop: a screen failing on every layout pass ({probe.Measures} measures) was closed within {sw.ElapsedMilliseconds} ms, the live terminal next to it untouched, {logged} log entries instead of hundreds, UI answering within {worst:0} ms");
        }
        catch (Exception ex) { fail("UI error loop", ex); }

        // 3. Terminals open, then the Cloud providers workspace and every screen of every provider.
        await CloudSweepAsync(main, ok, fail, "default theme", term);

        // 4. No white flash: the browser's own background is the terminal colour from the start.
        try
        {
            var tv = term?.Content as TerminalView ?? throw new Exception("no terminal");
            var want = ThemeManager.Hex("Input");
            var bg = tv.WebBackgroundForTest;
            if (bg.R != want.R || bg.G != want.G || bg.B != want.B || bg.A != 255) throw new Exception($"browser background {bg} ≠ theme Input {want}");
            var fresh = new Microsoft.Web.WebView2.Wpf.WebView2();
            WebHost.Paint(fresh, "Bg");
            var b2 = ThemeManager.Hex("Bg");
            if (fresh.DefaultBackgroundColor.R != b2.R || fresh.DefaultBackgroundColor.G != b2.G || fresh.DefaultBackgroundColor.B != b2.B) throw new Exception("Paint did not set the colour");
            fresh.Dispose();
            ok($"No white flash: a terminal's browser background is the theme's terminal colour (#{ThemeManager.HexString("Input")}) before its page draws; other web views use the window colour");
        }
        catch (Exception ex) { fail("terminal background", ex); }
        finally { if (term != null && main.Panes.AllTabs.Contains(term)) main.CloseTab(term); }
    }

    /// <summary>A live terminal in front, then the Cloud providers workspace and every screen of every provider, switching
    /// back to the terminal now and then; fails on any UI error or a UI thread held for seconds. Also run with the sample
    /// theme on (ThemeAddonTests).</summary>
    public static async Task CloudSweepAsync(MainWindow main, Action<string> ok, Action<string, Exception?> fail, string label, TabItem? term = null)
    {
        var d = Application.Current.Dispatcher;
        async Task Settle(int ms = 200) { await Task.Delay(ms); await d.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); }
        async Task<double> Responsive(int limitMs = 5000)
        {
            var sw0 = Stopwatch.StartNew();
            var op = d.InvokeAsync(() => { }, DispatcherPriority.Input);
            if (await Task.WhenAny(op.Task, Task.Delay(limitMs)) != op.Task) throw new Exception($"the UI thread did not answer within {limitMs} ms (frozen)");
            return sw0.Elapsed.TotalMilliseconds;
        }
        bool own = false;
        try
        {
            if (term == null || !main.Panes.AllTabs.Contains(term))
            {
                var cmd = Sessions.LocalShells.Discover().FirstOrDefault(s => s.Id == "cmd") ?? throw new Exception("cmd not discovered");
                main.OpenLocal(cmd);
                term = main.Panes.AllTabs.Last(t => t.Content is TerminalView);
                own = true;
                for (int i = 0; i < 120 && !((TerminalView)term.Content).IsConnected; i++) await Settle(250);
            }
            main.Panes.Select(term);
            await Settle(300);
            UiErrorGuard.ResetForTest();
            if (main.Panes.AllTabs.FirstOrDefault(t => t.Content is CloudWorkspaceView) is { } old) main.CloseTab(old);
            var sw = Stopwatch.StartNew();
            main.Navigate(CloudWorkspaceView.NavKeyName);
            var first = await Responsive();
            var ws = main.Panes.ActiveTab?.Content as CloudWorkspaceView ?? throw new Exception("the workspace did not open");
            double worst = first; string worstKey = "(open)"; int screens = 0;
            foreach (var p in CloudProviders.All)
                foreach (var s in CloudWorkspaceView.ScreensOf(p).Where(s => s.Key.Contains(':')))
                {
                    var t = Stopwatch.StartNew();
                    ws.Show(s.Key);
                    await Settle(60);
                    var ms = t.Elapsed.TotalMilliseconds + await Responsive();
                    if (ms > worst) { worst = ms; worstKey = s.Key; }
                    screens++;
                    // Back to the terminal and again to the workspace: the switch DarkCipher0x made.
                    if (screens % 5 == 0) { main.Panes.Select(term); await Settle(40); main.Navigate(CloudWorkspaceView.NavKeyName); await Responsive(); }
                }
            var errors = UiErrorGuard.SeenForTest();
            if (errors.Count > 0) throw new Exception("UI errors while showing cloud screens: " + string.Join(" | ", errors.Take(5)));
            if (worst > 3000) throw new Exception($"{worstKey} held the UI thread {worst:0} ms");
            if (!((TerminalView)term.Content).IsConnected) throw new Exception("the terminal dropped");
            ok($"Terminals → Cloud providers ({label}): opened in {first:0} ms with a live terminal, {screens} screens of {CloudProviders.All.Count} providers shown (back to the terminal every 5), no UI error, slowest {worstKey} {worst:0} ms");
        }
        catch (Exception ex) { fail($"terminals then Cloud providers ({label})", ex); }
        finally
        {
            if (main.Panes.AllTabs.FirstOrDefault(t => t.Content is CloudWorkspaceView) is { } wsTab) main.CloseTab(wsTab);
            if (own && term != null && main.Panes.AllTabs.Contains(term)) main.CloseTab(term);
        }
    }

    /// <summary>Entries in the self-test's error logs (sealed entries count as one each).</summary>
    private static int LogEntries()
    {
        try
        {
            if (!Directory.Exists(App.LogDir)) return 0;
            return Directory.EnumerateFiles(App.LogDir, "errors-*.log").Sum(f => File.ReadAllLines(f).Count(l => l.StartsWith("ENC ") || l.StartsWith("[")));
        }
        catch { return 0; }
    }
}
