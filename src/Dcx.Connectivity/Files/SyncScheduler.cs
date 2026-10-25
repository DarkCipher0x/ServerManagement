using System.Collections.Concurrent;
using System.IO;
using DcxManager.Services;

namespace DcxManager.Files;

/// <summary>
/// Scheduled and real-time folder sync (0.6.26, ADR-102), like FreeFileSync's batch jobs and RealTimeSync, while the app
/// runs and its store is unlocked. Timed jobs (every N minutes, daily, weekly) run when due; a real-time job runs a few
/// quiet seconds after a change in one of its folders on this PC, and checks a server's folder every few minutes (a
/// server can't be watched). Every run is unattended (<see cref="SyncRunner.RunUnattendedAsync"/>): no window, conflicts
/// left alone, the deletion guard on. Settings → Folder sync (Group Policy FileSyncScheduled) turns it all off.
/// </summary>
public static class SyncScheduler
{
    private static Timer? _tick;
    private static Func<bool> _canRun = () => true;
    private static readonly ConcurrentDictionary<string, byte> Running = new();
    /// <summary>Real-time changes that came in while the job was running: it runs once more afterwards.</summary>
    private static readonly ConcurrentDictionary<string, byte> Again = new();
    private static readonly Dictionary<string, Watch> Watches = new();
    private static readonly object Gate = new();

    /// <summary>A run finished: the job, whether it went well, and its summary (raised off the UI thread).</summary>
    public static event Action<SyncJobDef, SyncRunner.Result>? RunFinished;

    public static bool IsRunning(string jobId) => Running.ContainsKey(jobId);

    /// <param name="canRun">Checked before every run (the app: unlocked, not in the self-test).</param>
    public static void Start(Func<bool> canRun)
    {
        _canRun = canRun;
        SyncJobStore.Changed += Rewatch;
        _tick ??= new Timer(_ => Tick(), null, TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(30));
        Rewatch();
    }

    public static void Stop()
    {
        SyncJobStore.Changed -= Rewatch;
        _tick?.Dispose(); _tick = null;
        lock (Gate) { foreach (var w in Watches.Values) w.Dispose(); Watches.Clear(); }
    }

    private static bool Allowed => AppSettings.Current.FileSyncScheduled && SecureStore.IsUnlocked && !Profiles.ReadOnly && _canRun();

    private static void Tick()
    {
        try
        {
            if (!Allowed) return;
            var now = DateTime.UtcNow;
            foreach (var job in SyncJobStore.All)
            {
                if (job.Schedule == SyncSchedule.RealTime)
                {
                    // A server side is polled; a job that never ran runs once to start from a known state.
                    bool remote = job.Pairs.Any(p => p.LeftRef.Kind != "local" || p.RightRef.Kind != "local");
                    if (job.LastRunUtc == null || remote && now - job.LastRunUtc >= TimeSpan.FromMinutes(Math.Clamp(job.RemotePollMinutes, 1, 1440))) Fire(job.Id, "real-time check");
                }
                else if (job.NextRunUtc(now) is { } due && due <= now) Fire(job.Id, "schedule");
            }
        }
        catch (Exception ex) { Log.Write("Sync schedule", ex); }
    }

    /// <summary>Runs a saved job now, unattended (also "Run now" in the jobs window). False if it's already running.</summary>
    public static bool Fire(string jobId, string why)
    {
        if (!Running.TryAdd(jobId, 0)) { if (why == "real-time") Again.TryAdd(jobId, 0); return false; }
        _ = Task.Run(async () =>
        {
            var job = SyncJobStore.Get(jobId);
            lock (Gate) if (Watches.TryGetValue(jobId, out var w0)) w0.Quiet(TimeSpan.FromDays(1));   // the sync's own writes, until it ends
            try
            {
                if (job == null) return;
                var result = await SyncRunner.RunUnattendedAsync(job, $"app ({why})");
                try { RunFinished?.Invoke(job, result); } catch (Exception ex) { Log.Write("Sync finished", ex); }
            }
            catch (Exception ex)
            {
                Log.Write("Sync run", ex);
                if (job != null) SyncJobStore.Ran(jobId, DateTime.UtcNow, "✕ " + ex.Message.Split('\n')[0]);
            }
            finally
            {
                lock (Gate) if (Watches.TryGetValue(jobId, out var w)) w.Quiet(TimeSpan.FromSeconds(3));   // our own changes aren't news
                Running.TryRemove(jobId, out _);
                if (Again.TryRemove(jobId, out _) && Allowed) Fire(jobId, "real-time");
            }
        });
        return true;
    }

    /// <summary>Real-time jobs watch their folders on this PC; the rest have no watcher.</summary>
    private static void Rewatch()
    {
        try
        {
            var want = SyncJobStore.All.Where(j => j.Schedule == SyncSchedule.RealTime).ToList();
            lock (Gate)
            {
                foreach (var id in Watches.Keys.Where(id => !want.Any(j => j.Id == id && Watches[id].Matches(j))).ToList()) { Watches[id].Dispose(); Watches.Remove(id); }
                foreach (var j in want.Where(j => !Watches.ContainsKey(j.Id)))
                    Watches[j.Id] = new Watch(j, () => { if (Allowed) Fire(j.Id, "real-time"); });
            }
        }
        catch (Exception ex) { Log.Write("Sync watch", ex); }
    }

    /// <summary>File system watchers on a real-time job's local folders, debounced.</summary>
    private sealed class Watch : IDisposable
    {
        private readonly List<FileSystemWatcher> _w = new();
        private readonly Timer _debounce;
        private readonly string _signature;
        private readonly TimeSpan _delay;
        private DateTime _quietUntil;

        public Watch(SyncJobDef job, Action fire)
        {
            _signature = Signature(job);
            _delay = TimeSpan.FromSeconds(Math.Clamp(job.RealTimeDelaySeconds, 1, 3600));
            _debounce = new Timer(_ => fire(), null, Timeout.Infinite, Timeout.Infinite);
            foreach (var dir in job.Pairs.SelectMany(p => new[] { (p.LeftRef, p.LeftPath), (p.RightRef, p.RightPath) }).Where(x => x.Item1.Kind == "local").Select(x => x.Item2).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    var w = new FileSystemWatcher(dir) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size, InternalBufferSize = 64 * 1024 };
                    FileSystemEventHandler on = (_, e) => Changed(e.FullPath);
                    w.Created += on; w.Changed += on; w.Deleted += on;
                    w.Renamed += (_, e) => Changed(e.FullPath);
                    w.Error += (_, _) => Kick();   // buffer overflow: just sync
                    w.EnableRaisingEvents = true;
                    _w.Add(w);
                }
                catch (Exception ex) { Log.Write("Sync watch " + dir, ex); }
            }
        }

        private static string Signature(SyncJobDef j) => string.Join("|", j.Pairs.Select(p => p.Left + p.LeftPath + p.Right + p.RightPath)) + "|" + j.RealTimeDelaySeconds;
        public bool Matches(SyncJobDef j) => Signature(j) == _signature;
        public void Quiet(TimeSpan t) => _quietUntil = DateTime.UtcNow + t;

        private void Changed(string path)
        {
            if (DateTime.UtcNow < _quietUntil) return;
            if (path.EndsWith(TransferQueue.PartSuffix, StringComparison.Ordinal) || path.Contains(Path.DirectorySeparatorChar + SyncFilter.VersionsFolder, StringComparison.OrdinalIgnoreCase)) return;
            Kick();
        }

        private void Kick() { try { _debounce.Change(_delay, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { } }

        public void Dispose()
        {
            foreach (var w in _w) { try { w.Dispose(); } catch { } }
            _debounce.Dispose();
        }
    }
}
