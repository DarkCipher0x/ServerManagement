using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using DcxManager.Services;
using DcxManager.Ssh;

namespace DcxManager.Files;

public enum Conflict { Overwrite, Skip, Rename }

/// <summary>
/// One copy or move of a set of items from one place (this PC or a host) to a folder somewhere else — or an explicit
/// file-by-file plan (folder sync). Each job opens its OWN connections, so browsing stays responsive and closing a
/// Commander tab doesn't kill a running transfer.
/// </summary>
public sealed class TransferJob : INotifyPropertyChanged
{
    public required FsRef From { get; init; }
    public required FsRef To { get; init; }
    public required IReadOnlyList<FsItem> Items { get; init; }
    public required string DestDir { get; init; }
    public bool Move { get; init; }
    public Conflict OnConflict { get; init; } = Conflict.Overwrite;
    public string Actor { get; init; } = "you";
    /// <summary>Explicit (source item, target path) pairs — folder sync. When set, Items/DestDir only describe the job.</summary>
    public IReadOnlyList<(FsItem Item, string Target)>? Plan { get; init; }
    /// <summary>Deleted on the TARGET after every copy succeeded (sync "mirror", opt-in and previewed).</summary>
    public IReadOnlyList<FsItem>? DeleteOnTarget { get; init; }
    public bool Verify { get; init; } = AppSettings.Current.TransferVerify;
    public bool KeepTimes { get; init; } = AppSettings.Current.TransferKeepTimes;
    public string? Label { get; init; }
    public TransferJob? RetryOf { get; init; }
    public CancellationTokenSource Cts { get; } = new();
    public TaskCompletionSource<bool> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public DateTime Started { get; } = DateTime.Now;

    public string Title => Label ?? $"{(Move ? "Move" : "Copy")} {(Items.Count == 1 ? Items[0].Name : Items.Count + " items")}";
    public string Route => $"{From.Title} → {To.Title}:{DestDir}";

    private string _status = "queued";
    public string Status { get => _status; set { _status = value; Raise(nameof(Status)); } }
    private double _percent;
    public double Percent { get => _percent; set { _percent = value; Raise(nameof(Percent)); } }
    private string _detail = "";
    public string Detail { get => _detail; set { _detail = value; Raise(nameof(Detail)); } }
    public bool Finished { get; set; }
    public int Files { get; set; }
    public int Skipped { get; set; }
    public int Verified { get; set; }
    public int Resumed { get; set; }
    public int Retries { get; set; }
    public int Deleted { get; set; }
    public long Bytes { get; set; }
    public string? Error { get; set; }
    public List<string> Warnings { get; } = new();

    // ---- pause ----
    private TaskCompletionSource? _pause;
    public bool Paused => _pause != null;
    public void Pause() { if (!Finished && _pause == null) { _pause = new(TaskCreationOptions.RunContinuationsAsynchronously); Status = "paused"; } }
    public void Resume() { var p = Interlocked.Exchange(ref _pause, null); p?.TrySetResult(); }
    internal async Task GateAsync(CancellationToken ct) { if (_pause is { } p) await p.Task.WaitAsync(ct); }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string p) => Ui.Thread.Post(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p)));

    public string Summary => Error != null ? $"✕ {Title}: {Error}" :
        $"✓ {Title}: {Files} file(s), {Fmt.Bytes(Bytes)}{(Skipped > 0 ? $", {Skipped} skipped" : "")}{(Verified > 0 ? $", {Verified} verified (SHA-256)" : "")}{(Resumed > 0 ? $", {Resumed} resumed" : "")}{(Retries > 0 ? $", {Retries} retr{(Retries == 1 ? "y" : "ies")}" : "")}{(Deleted > 0 ? $", {Deleted} deleted on the target" : "")} → {To.Title}:{DestDir}";
}

/// <summary>
/// App-wide transfer queue (Commander panes, folder sync and the AI operator share it). Two jobs run at a time. ADR-014:
/// every file goes to "&lt;name&gt;.dcxpart" and is renamed over the target only when complete and (by default) verified
/// by SHA-256; part files are resumed; failing files are retried with back-off on fresh connections; jobs can be paused
/// and retried later (reusing part files); modified times (and Unix modes remote→remote) are kept; history is kept.
/// </summary>
public static class TransferQueue
{
    public const string PartSuffix = ".dcxpart";
    public static ObservableCollection<TransferJob> Jobs { get; } = new();
    private static readonly SemaphoreSlim Slots = new(2, 2);
    public static event Action<TransferJob>? Finished;
    /// <summary>A job was queued or finished: the crash-recovery journal listens (wired by the app; ADR-022).</summary>
    public static event Action? JobsChanged;
    private static void RaiseJobsChanged() { try { JobsChanged?.Invoke(); } catch (Exception ex) { Log.Write("transfer queue changed", ex); } }

    /// <summary>Test hooks: back-off between attempts; a fault injected while copying (file name, bytes this attempt).</summary>
    internal static TimeSpan[] Backoff = { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10) };
    internal static Func<string, long, bool>? Fault;

    public static TransferJob Enqueue(TransferJob job)
    {
        Jobs.Insert(0, job);
        while (Jobs.Count > 200 && Jobs.LastOrDefault(j => j.Finished) is { } old) Jobs.Remove(old);   // bounded list
        _ = Task.Run(() => RunAsync(job));
        RaiseJobsChanged();   // unfinished jobs are journaled for crash recovery (ADR-022)
        return job;
    }

    /// <summary>Runs a finished (failed/cancelled) job again as a new job; finished files are skipped by name only when
    /// the old job said "skip", otherwise re-copied — part files of interrupted ones are resumed.</summary>
    public static TransferJob Retry(TransferJob old) => Enqueue(new TransferJob
    {
        From = old.From, To = old.To, Items = old.Items, DestDir = old.DestDir, Move = old.Move, OnConflict = old.OnConflict,
        Actor = old.Actor, Plan = old.Plan, DeleteOnTarget = old.DeleteOnTarget, Verify = old.Verify, KeepTimes = old.KeepTimes,
        Label = old.Label, RetryOf = old,
    });

    public static void ClearFinished() { foreach (var j in Jobs.Where(j => j.Finished).ToList()) Jobs.Remove(j); }

    /// <summary>Both ends of a running job; reopened after a connection failure.</summary>
    private sealed class Ends : IDisposable
    {
        private readonly TransferJob _job;
        public IFileSystem Src = null!, Dst = null!;
        public Ends(TransferJob job) { _job = job; Open(); }
        public void Open()
        {
            Src = _job.From.Open();
            Dst = _job.From.Equals(_job.To) && _job.From.Kind == "local" ? Src : _job.To.Open();
        }
        public void Reopen() { Dispose(); Open(); }
        public void Dispose() { try { Src.Dispose(); } catch { } if (!ReferenceEquals(Src, Dst)) { try { Dst.Dispose(); } catch { } } }
    }

    private static async Task RunAsync(TransferJob job)
    {
        var ct = job.Cts.Token;
        var clock = Stopwatch.StartNew();
        try
        {
            await Slots.WaitAsync(ct);
            try
            {
                using var ends = new Ends(job);
                job.Status = "scanning";
                // Plan first: every file and folder with its size, so progress is honest and nothing half-starts on a typo.
                List<(FsItem Item, string Target)> plan;
                if (job.Plan != null) plan = job.Plan.ToList();
                else
                {
                    plan = new();
                    foreach (var it in job.Items) await PlanAsync(ends.Src, ends.Dst, it, ends.Dst.Join(job.DestDir, it.Name), plan, ct);
                }
                long total = Math.Max(1, plan.Where(p => !p.Item.IsDir).Sum(p => p.Item.Length));
                var progress = new Progress(job, total);

                // Same place + move = a rename (instant, atomic on the server).
                if (job.Plan == null && (job.Move && job.From.Equals(job.To) && job.From.Kind != "local" || job.Move && job.From.Kind == "local" && job.To.Kind == "local" && SameVolume(job.Items, job.DestDir)))
                {
                    foreach (var it in job.Items) { ct.ThrowIfCancellationRequested(); await ends.Src.RenameAsync(it.FullPath, ends.Src.Join(job.DestDir, it.Name), ct); job.Files++; job.Bytes += it.Length; }
                    job.Percent = 100;
                }
                else
                {
                    job.Status = job.Move ? "moving" : "copying";
                    foreach (var (item, target) in plan)
                    {
                        ct.ThrowIfCancellationRequested();
                        await job.GateAsync(ct);
                        if (item.IsDir) { if (await ends.Dst.StatAsync(target, ct) == null) await ends.Dst.MkdirAsync(target, ct); continue; }
                        var to = target;
                        if (await ends.Dst.StatAsync(to, ct) is { } existing)
                        {
                            if (existing.IsDir) throw new IOException($"{to} is a folder; can't overwrite it with a file.");
                            if (job.OnConflict == Conflict.Skip) { job.Skipped++; progress.Add(item.Length); continue; }
                            if (job.OnConflict == Conflict.Rename) to = await FreeNameAsync(ends.Dst, to, ct);
                        }
                        await CopyWithRetriesAsync(job, ends, item, to, progress, ct);
                        job.Files++;
                    }
                    if (job.DeleteOnTarget is { Count: > 0 } del)
                    {
                        job.Status = "deleting extras";
                        foreach (var d in del) { ct.ThrowIfCancellationRequested(); await ends.Dst.DeleteAsync(d.FullPath, d.IsDir, ct); job.Deleted++; }
                        AuditLog.Write(job.Actor, job.To.Title, "sync deleted extra items on the target", $"{del.Count}: {string.Join(", ", del.Take(10).Select(d => d.FullPath))}");
                    }
                    // A move deletes the source only after EVERY file arrived.
                    if (job.Move)
                    {
                        job.Status = "removing source";
                        foreach (var it in job.Items) await ends.Src.DeleteAsync(it.FullPath, it.IsDir, ct);
                    }
                    job.Percent = 100;
                }
                job.Status = "done";
                job.Detail = job.Summary[2..];
                AuditLog.Write(job.Actor, job.To.Title, $"{(job.Move ? "moved" : job.Label ?? "copied")} {job.Items.Count} item(s) from {job.From.Title}",
                    $"{string.Join(", ", job.Items.Take(5).Select(i => i.FullPath))} → {job.DestDir} · {job.Files} files, {job.Verified} verified, {job.Resumed} resumed, {job.Retries} retries");
            }
            finally { Slots.Release(); }
        }
        catch (OperationCanceledException) { job.Error = "cancelled"; job.Status = "cancelled"; job.Detail = "Unfinished files stay as “.dcxpart” at the destination; Retry resumes them."; }
        catch (Exception ex) { job.Error = ex.Message.Split('\n')[0]; job.Status = "failed"; job.Detail = job.Error + " — Retry resumes unfinished files."; Log.Write("Transfer", ex); }
        finally
        {
            job.Finished = true;
            RaiseJobsChanged();
            TransferHistory.Add(job, clock.Elapsed);
            job.Done.TrySetResult(job.Error == null);
            Ui.Thread.Post(() => Finished?.Invoke(job));
        }
    }

    private sealed class Progress
    {
        private readonly TransferJob _job; private readonly long _total; private long _done;
        private readonly Stopwatch _sw = Stopwatch.StartNew(); private long _last;
        public Progress(TransferJob job, long total) { _job = job; _total = total; }
        public void Add(long n, string? name = null)
        {
            _done += n;
            if (_sw.ElapsedMilliseconds - _last < 150) return;
            _last = _sw.ElapsedMilliseconds;
            _job.Percent = Math.Min(100, _done * 100.0 / _total);
            if (name != null) _job.Detail = $"{name} · {Fmt.Bytes(_done)} / {Fmt.Bytes(_total)} · {Fmt.Bytes((long)(_done / Math.Max(0.001, _sw.Elapsed.TotalSeconds)))}/s";
        }
        public void Rewind(long n) => _done = Math.Max(0, _done - n);
    }

    private static bool Transient(Exception ex) => ex is IOException or System.Net.Sockets.SocketException or ObjectDisposedException or TimeoutException
        or Renci.SshNet.Common.SshConnectionException or Renci.SshNet.Common.SshOperationTimeoutException or Renci.SshNet.Common.SshException;

    private static async Task CopyWithRetriesAsync(TransferJob job, Ends ends, FsItem item, string target, Progress progress, CancellationToken ct)
    {
        bool freshAfterMismatch = false;
        for (int attempt = 0; ; attempt++)
        {
            long sent = 0;
            try { await CopyFileAsync(job, ends, item, target, progress, b => sent += b, forceFresh: freshAfterMismatch, ct); return; }
            catch (ResumeMismatchException) when (!freshAfterMismatch)
            {
                // The resumed prefix wasn't the same data (the source changed): discard and copy this file from zero once.
                progress.Rewind(sent);
                freshAfterMismatch = true;
                job.Warnings.Add($"{item.Name}: the partial copy didn't match the source — copied again from the start");
                attempt--;
            }
            catch (Exception ex) when (Transient(ex) && attempt < Math.Min(AppSettings.Current.TransferRetries, Backoff.Length) && !ct.IsCancellationRequested)
            {
                progress.Rewind(sent);
                job.Retries++;
                var wait = Backoff[attempt];
                job.Status = $"retry {attempt + 1} in {wait.TotalSeconds:0} s";
                job.Detail = $"{item.Name}: {ex.Message.Split('\n')[0]}";
                await Task.Delay(wait, ct);
                try { ends.Reopen(); } catch (Exception rex) { Log.Write("Transfer reconnect", rex); }
                job.Status = job.Move ? "moving" : "copying";
            }
        }
    }

    private sealed class ResumeMismatchException() : IOException("resumed copy does not match the source");

    /// <summary>One file: to "&lt;target&gt;.dcxpart" (resumed if present and plausible), verify, replace, keep times/mode.</summary>
    private static async Task CopyFileAsync(TransferJob job, Ends ends, FsItem item, string target, Progress progress, Action<long> sent, bool forceFresh, CancellationToken ct)
    {
        var src = ends.Src; var dst = ends.Dst;
        var part = target + PartSuffix;
        long offset = 0;
        if (!forceFresh && dst.CanAppend && await dst.StatAsync(part, ct) is { IsDir: false } p && p.Length > 0 && p.Length <= item.Length)
        {
            offset = p.Length;
            job.Resumed++;
        }
        else if (!dst.CanAppend && await dst.StatAsync(part, ct) != null) job.Warnings.Add($"{item.Name}: SCP can't append — restarted");
        job.Detail = offset > 0 ? $"{item.Name} · resuming at {Fmt.Bytes(offset)}" : item.Name;
        progress.Add(offset);
        sent(offset);
        var buf = new byte[256 * 1024];
        long thisAttempt = 0;
        await using (var rs = await src.OpenReadAsync(item.FullPath, ct))
        {
            if (offset > 0)
            {
                if (rs.CanSeek) rs.Seek(offset, SeekOrigin.Begin);
                else { long skip = offset; while (skip > 0) { int n = await rs.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, skip)), ct); if (n == 0) break; skip -= n; } }
            }
            await using var ws = offset > 0 ? await dst.OpenAppendAsync(part, ct) : await dst.CreateAsync(part, ct);
            int read;
            while ((read = await rs.ReadAsync(buf, ct)) > 0)
            {
                await job.GateAsync(ct);
                if (Fault?.Invoke(item.Name, thisAttempt) == true) throw new IOException("injected transfer fault (self-test)");
                await ws.WriteAsync(buf.AsMemory(0, read), ct);
                thisAttempt += read; job.Bytes += read; sent(read);
                progress.Add(read, item.Name);
            }
        }
        if (job.Verify)
        {
            job.Detail = $"{item.Name} · verifying SHA-256";
            var a = await HashOf(src, item.FullPath, ct);
            var b = await HashOf(dst, part, ct);
            if (!string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            {
                try { await dst.DeleteAsync(part, false, ct); } catch { }
                if (offset > 0) throw new ResumeMismatchException();
                throw new IOException($"{item.Name}: SHA-256 differs after copying (source changed during the copy?) — the copy was discarded");
            }
            job.Verified++;
        }
        await dst.ReplaceAsync(part, target, ct);
        if (job.KeepTimes && item.Modified != default)
        {
            try { await dst.SetTimesAsync(target, item.Modified, ct); }
            catch (Exception ex) { job.Warnings.Add($"{item.Name}: modified time not kept ({ex.Message.Split('\n')[0]})"); }
            if (src.IsRemote && dst.IsRemote && item.Mode > 0)
            {
                try { await dst.SetModeAsync(target, item.Mode, ct); }
                catch (Exception ex) { job.Warnings.Add($"{item.Name}: permissions not kept ({ex.Message.Split('\n')[0]})"); }
            }
        }
    }

    /// <summary>SHA-256 where the file lives when possible (local / sha256sum), else by reading it.</summary>
    internal static async Task<string> HashOf(IFileSystem fs, string path, CancellationToken ct)
    {
        if (await fs.HashAsync(path, ct) is { } h) return h;
        await using var s = await fs.OpenReadAsync(path, ct);
        using var inc = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buf = new byte[256 * 1024]; int n;
        while ((n = await s.ReadAsync(buf, ct)) > 0) inc.AppendData(buf, 0, n);
        return Convert.ToHexString(inc.GetHashAndReset());
    }

    private static async Task PlanAsync(IFileSystem src, IFileSystem dst, FsItem item, string target, List<(FsItem, string)> plan, CancellationToken ct)
    {
        if (plan.Count > 200_000) throw new InvalidOperationException("More than 200,000 entries — use rsync/tar in a terminal for trees this large.");
        plan.Add((item, target));
        if (!item.IsDir || item.IsLink) return;   // symlinked folders are not followed (no loops, no surprises)
        foreach (var child in await src.ListAsync(item.FullPath, ct))
        {
            if (child.Name.EndsWith(PartSuffix, StringComparison.Ordinal)) continue;   // our own unfinished files never travel
            await PlanAsync(src, dst, child, dst.Join(target, child.Name), plan, ct);
        }
    }

    private static async Task<string> FreeNameAsync(IFileSystem fs, string path, CancellationToken ct)
    {
        var dir = fs.Parent(path);
        var name = path[(path.LastIndexOfAny(new[] { '/', '\\' }) + 1)..];
        var stem = Path.GetFileNameWithoutExtension(name); var ext = Path.GetExtension(name);
        for (int i = 1; i < 1000; i++)
        {
            var cand = fs.Join(dir, $"{stem} ({i}){ext}");
            if (await fs.StatAsync(cand, ct) == null) return cand;
        }
        throw new IOException("No free name for " + name);
    }

    private static bool SameVolume(IReadOnlyList<FsItem> items, string destDir) =>
        items.All(i => string.Equals(Path.GetPathRoot(i.FullPath), Path.GetPathRoot(destDir), StringComparison.OrdinalIgnoreCase));
}

/// <summary>The last 300 finished transfers (encrypted, transfers.dcx) — Commander → Transfer history.</summary>
public static class TransferHistory
{
    public sealed record Entry(DateTime Time, string Title, string Route, int Files, int Skipped, long Bytes, int Verified, int Resumed, int Retries, int Deleted, double Seconds, string Result, List<string> Warnings);

    private const string FileName = "transfers.dcx";
    private static List<Entry>? _all;
    private static readonly object Gate = new();

    public static IReadOnlyList<Entry> All { get { lock (Gate) return (_all ??= Load()).ToList(); } }

    private static List<Entry> Load()
    {
        try { return SecureStore.Read<List<Entry>>(FileName); } catch (Exception ex) { Log.Write("Transfer history", ex); return new(); }
    }

    internal static void Reset() { lock (Gate) _all = null; }

    public static void Add(TransferJob j, TimeSpan took)
    {
        try
        {
            lock (Gate)
            {
                _all ??= Load();
                _all.Insert(0, new Entry(DateTime.Now, j.Title, j.Route, j.Files, j.Skipped, j.Bytes, j.Verified, j.Resumed, j.Retries, j.Deleted, took.TotalSeconds,
                    j.Error == null ? "ok" : j.Error, j.Warnings.Take(20).ToList()));
                if (_all.Count > 300) _all.RemoveRange(300, _all.Count - 300);
                if (SecureStore.IsUnlocked) SecureStore.Write(FileName, _all);
            }
        }
        catch (Exception ex) { Log.Write("Transfer history", ex); }
    }

    public static string Text() => All.Count == 0 ? "No transfers yet." : string.Join("\n", All.Select(e =>
        $"{e.Time:yyyy-MM-dd HH:mm:ss}  {(e.Result == "ok" ? "✓" : "✕")} {e.Title} · {e.Route}\n" +
        $"    {e.Files} file(s), {Fmt.Bytes(e.Bytes)} in {e.Seconds:0.0} s" + (e.Skipped > 0 ? $", {e.Skipped} skipped" : "") + (e.Verified > 0 ? $", {e.Verified} verified" : "") +
        (e.Resumed > 0 ? $", {e.Resumed} resumed" : "") + (e.Retries > 0 ? $", {e.Retries} retries" : "") + (e.Deleted > 0 ? $", {e.Deleted} deleted" : "") + (e.Result != "ok" ? $" — {e.Result}" : "") +
        string.Concat(e.Warnings.Select(w => "\n    ⚠ " + w))));
}
