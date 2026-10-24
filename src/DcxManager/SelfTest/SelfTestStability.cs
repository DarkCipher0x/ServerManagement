using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DcxManager.Services;
using DcxManager.Sessions;
using DcxManager.Views;
using Microsoft.Web.WebView2.Core;

namespace DcxManager;

/// <summary>
/// ADR-021: the single-instance guard (real second processes, per-store keys, abandoned holders, a per-store browser
/// profile) and terminal renderer recovery (the real renderer process of a live cmd session is killed; the session must
/// survive, the output come back, and input still work). No window is shown: second processes exit before any UI.
/// </summary>
public static class StabilityTests
{
    public static async Task RunAsync(MainWindow main, string reportDir, Action<string> ok, Action<string, Exception?> fail)
    {
        async Task Settle(int ms = 200)
        {
            await Task.Delay(ms);
            await Application.Current.Dispatcher.InvokeAsync(() => main.UpdateLayout(), DispatcherPriority.ApplicationIdle);
        }

        // 1. Single instance: the guard, per-store keys, abandoned holders, the browser profile.
        var otherStore = Path.Combine(reportDir, "second-store-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var key = SingleInstance.Key;
            if (key == "default" || SingleInstance.NameFor(key) == SingleInstance.NameFor("default") || SingleInstance.NameFor(key) != SingleInstance.NameFor(key.ToUpperInvariant().ToLowerInvariant()))
                throw new Exception("instance key");
            // Held by this process (another thread can't take it).
            var taken = await Task.Run(() => SingleInstance.Take(key));
            if (taken != null) { taken.Dispose(); throw new Exception("this store's instance mutex isn't held"); }
            // An abandoned holder (a crashed instance) counts as free.
            var probeKey = "selftest-abandoned-" + Guid.NewGuid().ToString("N");
            var t = new Thread(() => SingleInstance.Take(probeKey)) { IsBackground = true };   // takes it and ends without releasing
            t.Start(); t.Join();
            using (var again = SingleInstance.Take(probeKey))
                if (again == null) throw new Exception("an abandoned instance blocked the next one");

            // A real second launch of the same store: it must hand over and exit without any UI.
            int before = SingleInstance.ActivationRequests;
            var exe = Environment.ProcessPath ?? throw new Exception("no process path");
            int same = await RunProbeAsync(exe, Environment.GetEnvironmentVariable("DCX_DATA_DIR")!);
            for (int i = 0; i < 40 && SingleInstance.ActivationRequests == before; i++) await Task.Delay(100);
            if (same != 0) throw new Exception($"second launch of the same store exited with {same} (9 = the guard let it through)");
            if (SingleInstance.ActivationRequests != before + 1) throw new Exception("the running instance wasn't asked to come forward");
            // Another store is another instance: the probe gets its own guard (and exits 9 by design, before any UI).
            Directory.CreateDirectory(otherStore);
            int other = await RunProbeAsync(exe, otherStore);
            if (other != 9) throw new Exception($"a different store was blocked (exit {other})");

            // The browser profile belongs to the store: never the installed app's.
            var installed = Path.Combine(App.CacheDir, "WebView2");
            var env = await WebHost.EnvironmentAsync();
            if (WebHost.UserDataFolder.Equals(installed, StringComparison.OrdinalIgnoreCase) || !env.UserDataFolder.TrimEnd('\\').Equals(WebHost.UserDataFolder, StringComparison.OrdinalIgnoreCase)
                || !WebHost.UserDataFolder.EndsWith("-webview2"))
                throw new Exception($"browser profile: {env.UserDataFolder}");
            ok($"single instance: this store's guard is held; an abandoned holder counts as free; a real second launch of the same store hands over (the running instance is asked forward) and exits without UI; another store gets its own instance; the self-test's browser profile is its own ({Path.GetFileName(WebHost.UserDataFolder)}), never the installed app's");
        }
        catch (Exception ex) { fail("single instance", ex); }
        finally { try { if (Directory.Exists(otherStore)) Directory.Delete(otherStore, true); } catch { } }

        // 2. Renderer recovery: kill the real renderer of a live terminal; the session must carry on.
        TabItem? tab = null;
        try
        {
            var cmd = LocalShells.Discover().FirstOrDefault(p => p.Id == "cmd") ?? throw new Exception("cmd not discovered");
            var tv = new TerminalView(new LocalTarget(cmd), null, connectOnOpen: true);
            tab = main.AddTab("❯ recovery", tv);
            main.Panes.Select(tab);
            for (int i = 0; i < 80 && !tv.IsConnected; i++) await Settle(250);
            if (!tv.IsConnected) throw new Exception("shell didn't start — " + tv.DiagStatus);
            await tv.OnInputAsync("echo BEFORE%COMPUTERNAME:~0,0%_CRASH\r");
            if (!await WaitScreen(tv, "BEFORE_CRASH")) throw new Exception("no output before the crash");

            int? pid = await RendererPidAsync(tv);
            string how;
            if (pid is { } p)
            {
                using var proc = Process.GetProcessById(p);
                // Only the WebView2 runtime's renderer of THIS test profile (found by this tab's page id) is ever killed.
                if (!proc.ProcessName.StartsWith("msedgewebview2", StringComparison.OrdinalIgnoreCase)) throw new Exception("unexpected renderer process " + proc.ProcessName);
                proc.Kill();
                how = "the real renderer process was killed";
            }
            else
            {
                await tv.RecoverAsync("self-test (renderer process not identifiable)");
                how = "the renderer process couldn't be identified on this runtime, so recovery was triggered directly";
            }
            for (int i = 0; i < 120 && (tv.Recoveries == 0 || !tv.PageReady); i++) await Settle(250);
            if (tv.Recoveries != 1 || !tv.PageReady) throw new Exception($"no recovery: {tv.Recoveries} · {tv.DiagStatus}");
            if (!await WaitScreen(tv, "BEFORE_CRASH")) throw new Exception("output not replayed after recovery");
            // The note is also written into the terminal, but a local console (ConPTY) repaints its own screen when the new
            // display reports its size, so the status line is the place that always says it.
            if (!tv.StatusText.Contains("display restarted after")) throw new Exception("no note about the restart: " + tv.StatusText);
            if (!tv.IsConnected) throw new Exception("the session was lost");
            await tv.OnInputAsync("echo AFTER%COMPUTERNAME:~0,0%_RECOVERY\r");
            if (!await WaitScreen(tv, "AFTER_RECOVERY")) throw new Exception("input doesn't reach the shell after recovery");

            // Bounded: 3 per 10 minutes, then a clear message instead of a crash loop.
            if (!await tv.RecoverAsync("self-test 2") ) throw new Exception("second recovery refused");
            for (int i = 0; i < 80 && !tv.PageReady; i++) await Settle(250);
            if (!await tv.RecoverAsync("self-test 3")) throw new Exception("third recovery refused");
            for (int i = 0; i < 80 && !tv.PageReady; i++) await Settle(250);
            if (await tv.RecoverAsync("self-test 4") || !tv.StatusText.Contains("keeps failing") || !tv.StatusText.Contains("still connected"))
                throw new Exception("a fourth recovery inside 10 minutes: " + tv.StatusText);
            if (!tv.IsConnected) throw new Exception("the session was lost during repeated recoveries");

            // Browser-process exit: only the view whose environment is current resets it (once for all tabs).
            int resets = WebHost.Resets;
            var foreign = await CoreWebView2Environment.CreateAsync(null, Path.Combine(reportDir, "foreign-webview2"));
            WebHost.ResetEnvironment(foreign);
            if (WebHost.Resets != resets) throw new Exception("an old environment's failure reset the current one");
            ok($"terminal renderer recovery: {how}; a new display replayed the output, said it restarted, the cmd session stayed connected and took input; capped at {TerminalView.MaxRecoveries} per {TerminalView.RecoveryWindow.TotalMinutes:0} min with a clear message; a browser-process exit resets the shared environment only once");
        }
        catch (Exception ex) { fail("terminal renderer recovery", ex); }
        finally { if (tab != null) { try { main.CloseTab(tab); } catch { } } }
    }

    /// <summary>Runs this exe as a second instance of <paramref name="store"/> with the probe argument; its exit code.</summary>
    private static async Task<int> RunProbeAsync(string exe, string store)
    {
        var psi = new ProcessStartInfo(exe, "--selftest-second") { UseShellExecute = false, CreateNoWindow = true };
        psi.Environment["DCX_DATA_DIR"] = store;
        using var p = Process.Start(psi) ?? throw new Exception("couldn't start the probe");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { p.Kill(); } catch { } throw new Exception("the probe didn't exit (it may be showing UI)"); }
        return p.ExitCode;
    }

    /// <summary>The renderer process showing this terminal's page (its frame source carries the page id), or null.</summary>
    private static async Task<int?> RendererPidAsync(TerminalView tv)
    {
        try
        {
            var env = await WebHost.EnvironmentAsync();
            foreach (var info in await env.GetProcessExtendedInfosAsync())
                if (info.ProcessInfo.Kind == CoreWebView2ProcessKind.Renderer && info.AssociatedFrameInfos.Any(f => f.Source.Contains(tv.PageId)))
                    return info.ProcessInfo.ProcessId;
        }
        catch (Exception ex) { App.Log("Find renderer process", ex); }
        return null;
    }

    private static async Task<bool> WaitScreen(TerminalView tv, string text, int seconds = 20)
    {
        for (int i = 0; i < seconds * 4; i++)
        {
            if ((await tv.CaptureScreenAsync())?.Lines.Any(l => l.Contains(text)) == true) return true;
            await Task.Delay(250);
        }
        return false;
    }
}
