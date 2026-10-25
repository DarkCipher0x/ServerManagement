using DcxManager.Services;
using DcxManager.Ssh;

namespace DcxManager.Files;

/// <summary>One folder pair of a run: what was compared and what is planned.</summary>
public sealed class SyncPairRun
{
    public required SyncPairDef Pair { get; init; }
    public required SyncOptions Options { get; init; }
    public int Index { get; init; }
    public List<DiffEntry> Diffs { get; set; } = new();
    public List<SyncStep> Steps { get; set; } = new();
    public string DbKey => SyncDatabase.KeyOf(Pair.LeftRef, Pair.LeftPath, Pair.RightRef, Pair.RightPath);
    public Dictionary<string, SyncDbEntry>? Db { get; set; }
    /// <summary>Why this pair can't run (a folder is missing, the deletion guard tripped…), or null.</summary>
    public string? Error { get; set; }
}

/// <summary>
/// Runs a sync job (0.6.26, ADR-102): compare every pair, plan it, run the steps through the transfer queue, then
/// record the last-sync state (Two-way), tidy the versions folders and note the result on the job. The compare
/// window uses the two halves separately (the preview in between); a schedule or "Run now" runs it unattended, where
/// conflicts are skipped and a pair stops if a folder is missing or it would delete more than the job allows.
/// </summary>
public static class SyncRunner
{
    public static async Task<List<SyncPairRun>> CompareAsync(SyncJobDef job, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var runs = new List<SyncPairRun>();
        int n = 0;
        foreach (var pair in job.Pairs)
        {
            var run = new SyncPairRun { Pair = pair, Options = job.OptionsFor(pair), Index = job.Pairs.Count > 1 ? ++n : 0 };
            runs.Add(run);
            try
            {
                if (pair.LeftPath.Trim().Length == 0 || pair.RightPath.Trim().Length == 0) throw new InvalidOperationException("choose a folder on both sides");
                using var l = pair.LeftRef.Open();
                using var r = pair.RightRef.Equals(pair.LeftRef) && pair.LeftRef.Kind == "local" ? null : pair.RightRef.Open();
                var rf = r ?? l;
                // A missing folder (an unplugged drive, an unmounted share) must never read as "everything was deleted".
                if (await l.StatAsync(pair.LeftPath, ct) is not { IsDir: true }) throw new InvalidOperationException($"the left folder isn't there ({pair.LeftRef.Title}:{pair.LeftPath})");
                if (await rf.StatAsync(pair.RightPath, ct) is not { IsDir: true }) throw new InvalidOperationException($"the right folder isn't there ({pair.RightRef.Title}:{pair.RightPath})");
                if (SyncFilter.Check(run.Options.Filter) is { } bad) throw new InvalidOperationException("filter: " + bad);
                var p = progress == null ? null : new Progress<string>(s => progress.Report((run.Index > 0 ? $"pair {run.Index}: " : "") + s));
                run.Diffs = await FolderSync.CompareAsync(l, pair.LeftPath, rf, pair.RightPath, run.Options, p, ct);
                run.Db = run.Options.Variant == SyncVariant.TwoWay ? SyncDatabase.Load(run.DbKey) : null;
                run.Steps = FolderSync.Plan(run.Diffs, run.Options, run.Db, run.Index);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { run.Error = ex.Message.Split('\n')[0]; Log.Write("Sync compare", ex); }
        }
        return runs;
    }

    /// <summary>For a run nobody previews: the share of one side's files the steps would delete, or null when that's within
    /// the job's limit (more than 10 files and more than <see cref="SyncOptions.MaxDeletePercent"/>% stops it).</summary>
    public static string? DeletionGuard(SyncPairRun run)
    {
        foreach (var left in new[] { true, false })
        {
            var del = left ? SyncAction.DeleteLeft : SyncAction.DeleteRight;
            int files = run.Diffs.Count(d => !d.IsDir && (left ? d.Left : d.Right) != null);
            int gone = run.Steps.Where(s => s.Runs && s.Action == del).Sum(s => s.Entry.IsDir ? s.FolderFiles : 1);
            int limit = Math.Clamp(run.Options.MaxDeletePercent, 1, 100);
            if (gone > 10 && gone * 100L > (long)limit * Math.Max(1, files))
                return $"it would delete {gone} of {files} files on the {(left ? "left" : "right")} (more than {limit}%); run it with a preview to check";
        }
        return null;
    }

    public sealed record Result(bool Ok, string Text);

    /// <summary>Runs the steps of every pair without an error and waits for them. <paramref name="jobId"/>: the saved job to
    /// note the result on (null for an ad-hoc compare).</summary>
    public static async Task<Result> ExecuteAsync(IReadOnlyList<SyncPairRun> runs, string? jobId, string actor, CancellationToken ct = default)
    {
        var stamp = DateTime.Now;
        var lines = new List<string>();
        bool ok = true;
        foreach (var run in runs)
        {
            string label = run.Index > 0 ? $"pair {run.Index}: " : "";
            if (run.Error != null) { lines.Add($"✕ {label}{run.Error}"); ok = false; continue; }
            var steps = run.Steps.Where(s => s.Runs).ToList();
            if (steps.Count == 0) { lines.Add($"✓ {label}already in sync" + (run.Steps.Count > 0 ? $" ({run.Steps.Count} left alone)" : "")); await RememberAsync(run, steps, ct); continue; }
            try
            {
                var jobs = await FolderSync.BuildJobsAsync(steps, run.Pair.LeftRef, run.Pair.LeftPath, run.Pair.RightRef, run.Pair.RightPath, run.Options, stamp, actor, ct);
                foreach (var j in jobs) TransferQueue.Enqueue(j);
                var done = await Task.WhenAll(jobs.Select(j => j.Done.Task.WaitAsync(ct)));
                if (done.All(x => x))
                {
                    await RememberAsync(run, steps, ct);
                    int pruned = await PruneAsync(run, stamp, ct);
                    lines.Add($"✓ {label}{FolderSync.Totals(run.Steps)}" + (pruned > 0 ? $" · {pruned} old version(s) removed" : ""));
                }
                else
                {
                    ok = false;
                    lines.Add($"✕ {label}" + string.Join("; ", jobs.Where(j => j.Error != null).Select(j => j.Summary)) + " — Retry it from Transfers; the last-sync state is unchanged");
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { ok = false; lines.Add($"✕ {label}{ex.Message.Split('\n')[0]}"); Log.Write("Sync run", ex); }
        }
        var text = string.Join("\n", lines);
        if (jobId != null) SyncJobStore.Ran(jobId, DateTime.UtcNow, (ok ? "✓ " : "✕ ") + string.Join(" | ", lines.Select(l => l.TrimStart('✓', '✕', ' '))));
        AuditLog.Write(actor, "files", "folder sync " + (ok ? "finished" : "failed"), (jobId != null ? (SyncJobStore.Get(jobId)?.Name ?? "job") + ": " : "") + text.Replace('\n', ' '));
        return new Result(ok, text);
    }

    /// <summary>A whole job without a preview (schedule, real-time, "Run now"): conflicts are left alone, and a pair whose
    /// deletions exceed its limit doesn't run.</summary>
    public static async Task<Result> RunUnattendedAsync(SyncJobDef job, string actor, CancellationToken ct = default)
    {
        if (Profiles.ReadOnly) return new Result(false, "This profile is read-only (viewer): sync jobs don't run.");
        var runs = await CompareAsync(job, null, ct);
        foreach (var run in runs.Where(r => r.Error == null))
        {
            foreach (var s in run.Steps.Where(s => s.Action == SyncAction.Conflict)) s.Selected = false;
            run.Error = DeletionGuard(run);
        }
        return await ExecuteAsync(runs, job.Id, actor, ct);
    }

    /// <summary>Two-way: what both sides hold now becomes the last-sync state (paths the run left alone keep their old record).</summary>
    private static async Task RememberAsync(SyncPairRun run, List<SyncStep> ran, CancellationToken ct)
    {
        if (run.Options.Variant != SyncVariant.TwoWay) return;
        using var l = run.Pair.LeftRef.Open();
        using var r = run.Pair.RightRef.Equals(run.Pair.LeftRef) && run.Pair.LeftRef.Kind == "local" ? null : run.Pair.RightRef.Open();
        var now = await FolderSync.CompareAsync(l, run.Pair.LeftPath, r ?? l, run.Pair.RightPath, run.Options, null, ct);
        var done = ran.Select(s => s.Entry.Rel).ToList();
        bool Untouched(string rel) => !done.Any(d => rel == d || rel.StartsWith(d + "/", StringComparison.Ordinal));
        SyncDatabase.Save(run.DbKey, SyncDatabase.After(now, run.Db ?? new(), Untouched));
    }

    private static async Task<int> PruneAsync(SyncPairRun run, DateTime stamp, CancellationToken ct)
    {
        var o = run.Options;
        if (o.Deletion == DeletionMode.Permanent && AppSettings.Current.FileSyncAllowPermanentDelete) return 0;
        int n = 0;
        foreach (var left in new[] { true, false })
        {
            var (fsRef, root) = left ? (run.Pair.LeftRef, run.Pair.LeftPath) : (run.Pair.RightRef, run.Pair.RightPath);
            using var fs = fsRef.Open();
            if (SyncVersioning.Effective(o.Deletion, fs, AppSettings.Current.FileSyncAllowPermanentDelete) != DeletionMode.Versioning) continue;
            var r = SyncVersioning.For(fs, root, o, left, stamp);
            try { n += await SyncVersioning.PruneAsync(fs, r.VersionsDir, o.Naming, o.VersionsKeepDays, o.VersionsMaxCount, stamp, ct); }
            catch (Exception ex) { Log.Write("Sync versions cleanup", ex); }
        }
        return n;
    }
}
