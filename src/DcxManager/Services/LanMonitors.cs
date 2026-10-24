using DcxManager.Net;
using DcxManager.Notify;

namespace DcxManager.Services;

/// <summary>
/// Runs the scheduled LAN re-scans you set up (LAN discovery → Monitoring, 0.3.0 §7). Nothing runs that you didn't create:
/// each monitor has its own interval (5 minutes or more), its last run and result are shown in the tab, and every change
/// it finds — a new open port, a port that closed, a host that appeared or stopped answering — goes to the audit log
/// (and so to syslog) and, for hosts going down, to e-mail notifications. Only while the app runs; offline mode applies.
/// </summary>
public static class LanMonitors
{
    private static IDisposable? _timer;
    private static readonly HashSet<string> Running = new();

    /// <summary>Raised (UI thread) after a monitor ran.</summary>
    public static event Action<LanMonitor>? Ran;

    public static void Start()
    {
        if (_timer != null) return;
        _timer = Ui.Thread.Every(TimeSpan.FromMinutes(1), () => { foreach (var m in AppData.Current.LanMonitors.Where(Due).ToList()) _ = RunAsync(m); });
    }

    public static void Stop() { _timer?.Dispose(); _timer = null; }

    private static bool Due(LanMonitor m) => m.Enabled && (m.LastRun == null || DateTime.Now - m.LastRun.Value >= TimeSpan.FromMinutes(Math.Max(5, m.IntervalMinutes)));

    /// <summary>One run: ping (if asked) and the port scan, then the differences against the last run.</summary>
    public static async Task<string> RunAsync(LanMonitor m, CancellationToken ct = default)
    {
        lock (Running) if (!Running.Add(m.Id)) return "already running";
        try
        {
            var t = NetTargets.Parse(m.Targets);
            var ports = NetTargets.Ports(m.Ports);
            var hosts = t.Addresses.Select(a => a.ToString()).Concat(t.Names).ToList();
            var open = new System.Collections.Concurrent.ConcurrentBag<OpenPort>();
            var up = new System.Collections.Concurrent.ConcurrentBag<string>();
            if (m.Ping && t.Addresses.Count > 0)
                await PingSweep.RunAsync(t.Addresses, new SyncProgress<PingResult>(r => up.Add(r.Address)), new SyncProgress<int>(_ => { }), ct);
            await PortScanner.RunAsync(hosts, ports, new SyncProgress<OpenPort>(open.Add), new SyncProgress<int>(_ => { }), ct, detect: false);
            foreach (var o in open) up.Add(o.Address);

            var nowOpen = open.Select(o => $"{o.Address}:{o.Port}").Distinct().OrderBy(x => x).ToList();
            var nowUp = up.Distinct().OrderBy(x => x).ToList();
            bool first = m.LastRun == null;
            var newPorts = first ? new List<string>() : nowOpen.Except(m.OpenPorts).ToList();
            var closed = first ? new List<string>() : m.OpenPorts.Except(nowOpen).ToList();
            var appeared = first ? new List<string>() : nowUp.Except(m.UpHosts).ToList();
            var gone = first ? new List<string>() : m.UpHosts.Except(nowUp).ToList();

            foreach (var p in newPorts) AuditLog.Write("app", "LAN monitor", $"{m.Name}: new open port {p}");
            foreach (var p in closed) AuditLog.Write("app", "LAN monitor", $"{m.Name}: port closed {p}");
            foreach (var h in appeared) AuditLog.Write("app", "LAN monitor", $"{m.Name}: host appeared {h}");
            foreach (var h in gone) AuditLog.Write("app", "LAN monitor", $"{m.Name}: host stopped answering {h}");
            if (gone.Count > 0) Notifier.Report(Notifier.Kind.HostDown, $"{gone.Count} host(s) stopped answering ({m.Name})", $"LAN monitor “{m.Name}”: no answer to ping or any watched port from:\n" + string.Join("\n", gone));
            if (newPorts.Count > 0) Notifier.Report(Notifier.Kind.Security, $"New open port(s) on your network ({m.Name})", $"LAN monitor “{m.Name}” found ports that weren't open last time:\n" + string.Join("\n", newPorts));

            m.OpenPorts = nowOpen; m.UpHosts = nowUp; m.LastRun = DateTime.Now;
            m.LastSummary = $"{nowUp.Count} host(s) up, {nowOpen.Count} open port(s)" + (first ? " · first run (baseline)" :
                newPorts.Count + closed.Count + appeared.Count + gone.Count == 0 ? " · no changes" : $" · +{newPorts.Count} port(s), −{closed.Count}, +{appeared.Count} host(s), −{gone.Count}");
            AppData.Save();
            Ui.Thread.Post(() => Ran?.Invoke(m));
            return m.LastSummary;
        }
        catch (Exception ex)
        {
            m.LastRun = DateTime.Now; m.LastSummary = "✕ " + ex.Message;
            try { AppData.Save(); } catch { }
            Ui.Thread.Post(() => Ran?.Invoke(m));
            return m.LastSummary;
        }
        finally { lock (Running) Running.Remove(m.Id); }
    }

    /// <summary>IProgress that runs the callback on the reporting thread (Progress&lt;T&gt; would post to the UI thread).</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
}
