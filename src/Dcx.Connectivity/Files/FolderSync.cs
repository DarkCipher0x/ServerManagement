using System.IO;
using DcxManager.Services;
using DcxManager.Ssh;

namespace DcxManager.Files;

public enum DiffState { Same, LeftNewer, RightNewer, Differs, OnlyLeft, OnlyRight, TypeConflict }

/// <summary>One path in a comparison (relative to both roots, '/'-separated).</summary>
public sealed record DiffEntry(string Rel, FsItem? Left, FsItem? Right, DiffState State);

public enum SyncMode { Update, Mirror, TwoWay }
public enum SyncAction { CopyRight, CopyLeft, DeleteRight, DeleteLeft, Conflict }

public sealed class SyncStep
{
    public required DiffEntry Entry { get; init; }
    public required SyncAction Action { get; init; }
    public bool Selected { get; set; } = true;
    public string Why { get; init; } = "";
    public string ActionText => Action switch
    {
        SyncAction.CopyRight => "copy →", SyncAction.CopyLeft => "← copy", SyncAction.DeleteRight => "delete (right)", SyncAction.DeleteLeft => "delete (left)", _ => "conflict — skipped",
    };
    public long Bytes => Action switch { SyncAction.CopyRight => Entry.Left?.Length ?? 0, SyncAction.CopyLeft => Entry.Right?.Length ?? 0, _ => 0 };
}

/// <summary>
/// Folder compare and sync (ADR-014). Compare: recursive, by size + modified time (2 s tolerance, FAT/SMB) or by SHA-256.
/// Plan: Update (one way, never deletes; a newer target is a conflict), Mirror (Update + overwrite newer targets +
/// optionally delete extras), Two-way (newer side wins; both-changed = conflict). Nothing runs without the preview;
/// deletions only when asked, executed after all copies succeeded (the transfer job does that).
/// </summary>
public static class FolderSync
{
    public static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(2);
    public const int MaxEntries = 200_000;

    public static async Task<List<DiffEntry>> CompareAsync(IFileSystem left, string leftDir, IFileSystem right, string rightDir, bool checksum,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var list = new List<DiffEntry>();
        var cmp = left.IsRemote || right.IsRemote ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        async Task Walk(string l, string r, string rel)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(rel.Length == 0 ? "/" : rel);
            var li = (await left.ListAsync(l, ct)).Where(Visible).ToDictionary(i => i.Name, cmp);
            var ri = (await right.ListAsync(r, ct)).Where(Visible).ToDictionary(i => i.Name, cmp);
            foreach (var name in li.Keys.Union(ri.Keys, cmp).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                if (list.Count > MaxEntries) throw new InvalidOperationException($"More than {MaxEntries:N0} entries — compare smaller folders.");
                li.TryGetValue(name, out var a); ri.TryGetValue(name, out var b);
                var path = rel.Length == 0 ? name : rel + "/" + name;
                if (a != null && b == null) { list.Add(new(path, a, null, DiffState.OnlyLeft)); continue; }
                if (a == null && b != null) { list.Add(new(path, null, b, DiffState.OnlyRight)); continue; }
                if (a!.IsDir != b!.IsDir) { list.Add(new(path, a, b, DiffState.TypeConflict)); continue; }
                if (a.IsDir)
                {
                    if (a.IsLink || b.IsLink) continue;   // symlinked folders are not followed
                    await Walk(a.FullPath, b.FullPath, path);
                    continue;
                }
                list.Add(new(path, a, b, await StateOf(a, b)));
            }
        }
        async Task<DiffState> StateOf(FsItem a, FsItem b)
        {
            bool sameTime = (a.Modified - b.Modified).Duration() <= Tolerance;
            if (checksum)
            {
                if (a.Length == b.Length && string.Equals(await TransferQueue.HashOf(left, a.FullPath, ct), await TransferQueue.HashOf(right, b.FullPath, ct), StringComparison.OrdinalIgnoreCase)) return DiffState.Same;
            }
            else if (a.Length == b.Length && sameTime) return DiffState.Same;
            if (sameTime) return DiffState.Differs;
            return a.Modified > b.Modified ? DiffState.LeftNewer : DiffState.RightNewer;
        }
        await Walk(leftDir, rightDir, "");
        return list;
    }

    private static bool Visible(FsItem i) => !i.IsParent && !i.Name.EndsWith(TransferQueue.PartSuffix, StringComparison.Ordinal);

    /// <param name="leftToRight">Direction for Update / Mirror (ignored for Two-way).</param>
    public static List<SyncStep> Plan(IEnumerable<DiffEntry> diffs, SyncMode mode, bool leftToRight, bool deleteExtras)
    {
        var steps = new List<SyncStep>();
        SyncStep S(DiffEntry e, SyncAction a, string why, bool sel = true) => new() { Entry = e, Action = a, Why = why, Selected = sel };
        foreach (var e in diffs)
        {
            if (e.State == DiffState.Same) continue;
            if (mode == SyncMode.TwoWay)
            {
                steps.Add(e.State switch
                {
                    DiffState.OnlyLeft => S(e, SyncAction.CopyRight, "only on the left"),
                    DiffState.OnlyRight => S(e, SyncAction.CopyLeft, "only on the right"),
                    DiffState.LeftNewer => S(e, SyncAction.CopyRight, "left is newer"),
                    DiffState.RightNewer => S(e, SyncAction.CopyLeft, "right is newer"),
                    DiffState.Differs => S(e, SyncAction.Conflict, "different content, same modified time — decide yourself", false),
                    _ => S(e, SyncAction.Conflict, "a file on one side, a folder on the other", false),
                });
                continue;
            }
            var copy = leftToRight ? SyncAction.CopyRight : SyncAction.CopyLeft;
            var del = leftToRight ? SyncAction.DeleteRight : SyncAction.DeleteLeft;
            var onlySource = leftToRight ? DiffState.OnlyLeft : DiffState.OnlyRight;
            var onlyTarget = leftToRight ? DiffState.OnlyRight : DiffState.OnlyLeft;
            var sourceNewer = leftToRight ? DiffState.LeftNewer : DiffState.RightNewer;
            var targetNewer = leftToRight ? DiffState.RightNewer : DiffState.LeftNewer;
            if (e.State == onlySource) steps.Add(S(e, copy, "missing on the target"));
            else if (e.State == sourceNewer) steps.Add(S(e, copy, "source is newer"));
            else if (e.State == DiffState.Differs) steps.Add(S(e, copy, "different content"));
            else if (e.State == targetNewer)
                steps.Add(mode == SyncMode.Mirror ? S(e, copy, "⚠ target is NEWER — mirror overwrites it") : S(e, SyncAction.Conflict, "target is newer — not overwritten by Update", false));
            else if (e.State == onlyTarget)
            {
                if (mode == SyncMode.Mirror && deleteExtras) steps.Add(S(e, del, "not in the source — mirror deletes it"));
            }
            else steps.Add(S(e, SyncAction.Conflict, "a file on one side, a folder on the other", false));
        }
        return steps;
    }

    /// <summary>The transfer jobs for the selected steps: one per direction, file by file (folders expanded), with that
    /// direction's deletions attached to run after its copies.</summary>
    public static async Task<List<TransferJob>> BuildJobsAsync(IEnumerable<SyncStep> selected, FsRef leftRef, string leftDir, FsRef rightRef, string rightDir, CancellationToken ct = default)
    {
        var sel = selected.Where(s => s.Selected && s.Action != SyncAction.Conflict).ToList();
        var jobs = new List<TransferJob>();
        foreach (var toRight in new[] { true, false })
        {
            var copies = sel.Where(s => s.Action == (toRight ? SyncAction.CopyRight : SyncAction.CopyLeft)).ToList();
            var deletes = sel.Where(s => s.Action == (toRight ? SyncAction.DeleteRight : SyncAction.DeleteLeft)).Select(s => (toRight ? s.Entry.Right : s.Entry.Left)!).ToList();
            if (copies.Count == 0 && deletes.Count == 0) continue;
            var (fromRef, fromDir, toRef, toDir) = toRight ? (leftRef, leftDir, rightRef, rightDir) : (rightRef, rightDir, leftRef, leftDir);
            using var src = fromRef.Open();
            using var dst = toRef.Equals(fromRef) && fromRef.Kind == "local" ? null : toRef.Open();
            var d = dst ?? src;
            var plan = new List<(FsItem, string)>();
            async Task Add(FsItem it, string target)
            {
                plan.Add((it, target));
                if (!it.IsDir || it.IsLink) return;
                foreach (var c in await src.ListAsync(it.FullPath, ct))
                    if (Visible(c)) await Add(c, d.Join(target, c.Name));
            }
            foreach (var s in copies)
            {
                var item = (toRight ? s.Entry.Left : s.Entry.Right)!;
                var target = s.Entry.Rel.Split('/').Aggregate(toDir, (acc, part) => d.Join(acc, part));
                // Parent folders that don't exist yet (the item is inside a folder that is new on the target) come from
                // the folder's own entry; a lone file in an existing folder needs nothing more.
                await Add(item, target);
            }
            jobs.Add(new TransferJob
            {
                From = fromRef, To = toRef, DestDir = toDir, Plan = plan, DeleteOnTarget = deletes, OnConflict = Conflict.Overwrite,
                Items = copies.Select(c => (toRight ? c.Entry.Left : c.Entry.Right)!).ToList(),
                Label = $"Sync {(toRight ? "→" : "←")} {copies.Count} item(s){(deletes.Count > 0 ? $", delete {deletes.Count}" : "")}",
            });
        }
        return jobs;
    }

    public static string Totals(IEnumerable<SyncStep> steps)
    {
        var s = steps.Where(x => x.Selected).ToList();
        int r = s.Count(x => x.Action == SyncAction.CopyRight), l = s.Count(x => x.Action == SyncAction.CopyLeft);
        int dr = s.Count(x => x.Action == SyncAction.DeleteRight), dl = s.Count(x => x.Action == SyncAction.DeleteLeft);
        int c = steps.Count(x => x.Action == SyncAction.Conflict);
        return $"{r} → ({Fmt.Bytes(s.Where(x => x.Action == SyncAction.CopyRight).Sum(x => x.Bytes))}) · {l} ← ({Fmt.Bytes(s.Where(x => x.Action == SyncAction.CopyLeft).Sum(x => x.Bytes))})" +
               (dr + dl > 0 ? $" · DELETE {dr + dl}" : "") + (c > 0 ? $" · {c} conflict(s) skipped" : "");
    }
}
