using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DcxManager.Services;
using DcxManager.Views;

namespace DcxManager;

/// <summary>
/// ADR-025 self-test robustness: (1) a screen whose background check hangs is still released the moment it closes —
/// the deterministic version of the s26 "assistant" leak flake; (2) a timer audit: after every sidebar screen has been
/// opened and closed, no DispatcherTimer is left running for a closed screen (a running timer roots its owner and
/// burns CPU forever). No window is shown.
/// </summary>
public static class RobustnessTests
{
    private static async Task Idle(int ms = 0)
    {
        if (ms > 0) await Task.Delay(ms);
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    public static async Task RunAsync(MainWindow main, Action<string> ok, Action<string, Exception?> fail)
    {
        // 1. A hanging AI engine: the operator screen's engine check must not keep the closed screen alive.
        var s = AppSettings.Current;
        string engine = s.AiEngine, url = s.OpenAiUrl;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        var held = new List<TcpClient>();
        try
        {
            listener.Start();
            _ = Task.Run(async () => { try { while (true) held.Add(await listener.AcceptTcpClientAsync()); } catch { } });   // accepts, never answers
            s.AiEngine = "openai"; s.OpenAiUrl = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/v1";
            var r = OpenAssistant(main);
            for (int i = 0; i < 40 && held.Count == 0; i++) await Idle(50);
            if (held.Count == 0) throw new Exception("the engine check never reached the (hanging) test server");
            Close(main, r);
            main.Navigate("dashboard");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            // Its check is still waiting on the hanging server (4 s limit): the closed screen must be collectable now.
            for (int i = 0; i < 8 && r.IsAlive; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); await Idle(150); }
            if (r.IsAlive) throw new Exception($"the closed AI operator screen is still in memory {sw.ElapsedMilliseconds} ms later while its engine check hangs (an awaiting method holds it)");
            ok($"closed screen with a hanging background check: the AI operator screen was released {sw.ElapsedMilliseconds} ms after closing, while its engine check was still waiting on a server that never answers (the s26 leak flake, made deterministic)");
        }
        catch (Exception ex) { fail("hanging check releases its screen", ex); }
        finally
        {
            s.AiEngine = engine; s.OpenAiUrl = url;
            listener.Stop();
            foreach (var c in held.ToList()) c.Dispose();
        }

        // 2. Timer audit: open and close every sidebar screen; no timer may keep running for a closed one.
        try
        {
            await Idle(300);
            var baseline = Timers().ToHashSet();
            // App screens only: not the dashboard (fixed), the Vultr portal (a real website), the AI dock (not a tab), local
            // shells (real processes; the endurance loops cover them) or cloud screens (they may call a provider's API).
            var keys = main.NavKeys.Where(k => !k.Contains(':') && k is not ("dashboard" or "portal" or "aipanel")).ToList();
            var opened = new List<string>();
            foreach (var key in keys)
            {
                // A screen left open by an earlier test is closed first, so every screen gets a fresh open + close here.
                main.Navigate(key);
                await Idle(100);
                if (main.Panes.ActiveTab is { } old && (old.Tag as string) != "fixed" && old.Content is not DashboardView) main.CloseTab(old);
                await Idle(50);
                var before = main.Panes.AllTabs.ToHashSet();
                main.Navigate(key);
                await Idle(300);
                var tab = main.Panes.AllTabs.FirstOrDefault(t => !before.Contains(t));
                if (tab == null) continue;   // not a tab screen
                main.CloseTab(tab);
                opened.Add(key);
                await Idle(50);
            }
            if (opened.Count < keys.Count - 2) throw new Exception($"only {opened.Count} of {keys.Count} screens could be opened fresh: {string.Join(", ", keys.Except(opened))}");
            main.Navigate("dashboard");
            // Debounced one-shot timers (workspace save, recovery journal) stop by themselves within a few seconds.
            var left = new List<string>();
            for (int i = 0; i < 12; i++)
            {
                await Idle(500);
                left = Timers().Where(t => !baseline.Contains(t) && t.IsEnabled).Select(Describe).Where(d => d != null).Select(d => d!).ToList();
                if (left.Count == 0) break;
            }
            if (left.Count > 0) throw new Exception("timers still running for closed screens: " + string.Join("; ", left.Distinct()));
            ok($"timer audit: {opened.Count} sidebar screens opened and closed ({string.Join(", ", opened)}); no DispatcherTimer left running for a closed screen");
        }
        catch (Exception ex) { fail("timer audit", ex); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference OpenAssistant(MainWindow main)
    {
        main.Navigate("assistant");
        return new WeakReference(main.Panes.ActiveTab!.Content);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Close(MainWindow main, WeakReference r)
    {
        var tab = main.Panes.AllTabs.FirstOrDefault(t => ReferenceEquals(t.Content, r.Target));
        if (tab != null) main.CloseTab(tab);
    }

    /// <summary>The dispatcher's registered timers (WPF keeps them in a private list; read-only use, self-test only).</summary>
    private static List<DispatcherTimer> Timers()
    {
        var d = Application.Current.Dispatcher;
        var f = typeof(Dispatcher).GetField("_timers", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new Exception("this WPF version doesn't expose the dispatcher's timer list; the audit can't run");
        var list = (IList)f.GetValue(d)!;
        lock (list.SyncRoot) return list.Cast<DispatcherTimer>().ToList();
    }

    /// <summary>"Type.Method" of a timer whose handler belongs to a UI element that is no longer on screen; null when it
    /// belongs to something still alive and shown (the main window, an app-wide service).</summary>
    private static string? Describe(DispatcherTimer t)
    {
        var tick = typeof(DispatcherTimer).GetField("Tick", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(t) as Delegate;
        if (tick == null) return null;
        foreach (var h in tick.GetInvocationList())
        {
            var owner = Owner(h.Target);
            if (owner is FrameworkElement fe && !fe.IsLoaded && fe is not Window) return $"{fe.GetType().Name}.{h.Method.Name}";
        }
        return null;
    }

    /// <summary>The object a handler really belongs to: itself, or the "this" captured by a lambda's closure.</summary>
    private static object? Owner(object? target)
    {
        if (target == null || target is FrameworkElement) return target;
        foreach (var f in target.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            if (f.GetValue(target) is FrameworkElement fe) return fe;
        return target;
    }
}
