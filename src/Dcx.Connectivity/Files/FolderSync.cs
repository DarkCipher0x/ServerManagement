using System.ComponentModel;
using System.IO;
using DcxManager.Services;
using DcxManager.Ssh;

namespace DcxManager.Files;

public enum DiffState { Same, LeftNewer, RightNewer, Differs, OnlyLeft, OnlyRight, TypeConflict }

/// <summary>One path in a comparison (relative to both roots, '/'-separated). Folders on both sides are listed as Same;
/// a folder on one side only is listed with everything inside it.</summary>
public sealed record DiffEntry(string Rel, FsItem? Left, FsItem? Right, DiffState State)
{
    /// <summary>A folder on one side only, some of whose contents the filter left out (so it's never deleted whole).</summary>
    public bool Partial { get; init; }
    public bool IsDir => (Left ?? Right)?.IsDir == true;
    public bool Under(string folderRel) => Rel.Length > folderRel.Length + 1 && Rel.StartsWith(folderRel + "/", StringComparison.Ordinal);
}

/// <summary>Old modes (the first compare window, the self-test); the sync options' variants cover them.</summary>
public enum SyncMode { Update, Mirror, TwoWay }
/// <summary>None = leave it alone (Custom variant, or set by hand in the preview).</summary>
public enum SyncAction { CopyRight, CopyLeft, DeleteRight, DeleteLeft, Conflict, None }

public sealed class SyncStep : INotifyPropertyChanged
{
    public required DiffEntry Entry { get; init; }
    private SyncAction _action;
    public required SyncAction Action { get => _action; set { _action = value; Raise(nameof(Action)); Raise(nameof(ActionText)); Raise(nameof(Bytes)); } }
    private bool _selected = true;
    public bool Selected { get => _selected; set { _selected = value; Raise(nameof(Selected)); } }
    public string Why { get; set; } = "";
    /// <summary>The folder pair this step belongs to (several pairs in one job), 1-based; 0 = the only one.</summary>
    public int Pair { get; init; }
    /// <summary>A one-sided folder copied or deleted whole: the size of everything inside it.</summary>
    public long FolderBytes { get; set; }
    /// <summary>How many files a whole-folder step covers.</summary>
    public int FolderFiles { get; set; }
    /// <summary>A copied folder whose contents have their own steps: only the folder itself is created.</summary>
    public bool FolderOnly { get; set; }
    /// <summary>The action the plan chose (the preview can change <see cref="Action"/> and go back to it).</summary>
    public SyncAction Planned { get; init; }

    public string ActionText => Action switch
    {
        SyncAction.CopyRight => "copy →", SyncAction.CopyLeft => "← copy", SyncAction.DeleteRight => "delete right", SyncAction.DeleteLeft => "delete left",
        SyncAction.None => "leave", _ => "conflict",
    };
    public long Bytes => Action switch
    {
        SyncAction.CopyRight => Entry.IsDir ? FolderBytes : Entry.Left?.Length ?? 0,
        SyncAction.CopyLeft => Entry.IsDir ? FolderBytes : Entry.Right?.Length ?? 0,
        _ => 0,
    };
    public bool Runs => Selected && Action is not (SyncAction.Conflict or SyncAction.None);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}

/// <summary>
/// Folder compare and sync (ADR-014; FreeFileSync-style since 0.6.26, ADR-102). Compare: recursive, by size + modified
/// time (with a tolerance and optional whole-hour shifts), by content (SHA-256) or by size, through the include /
/// exclude / size / age filter. Plan: Two-way (with the last-sync database: deletions carry over, changes on both sides
/// are conflicts), Mirror, Update, or Custom (an action per kind of difference). Nothing runs without a plan; deletions
/// run after all copies and go to the Recycle Bin or a versions folder unless permanent deletion is allowed.
/// </summary>
public static class FolderSync
{
    public static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(2);
    public const int MaxEntries = 200_000;

    /// <summary>The first compare's call (size + time or SHA-256, no filter).</summary>
    public static Task<List<DiffEntry>> CompareAsync(IFileSystem left, string leftDir, IFileSystem right, string rightDir, bool checksum,
        IProgress<string>? progress = null, CancellationToken ct = default) =>
        CompareAsync(left, leftDir, right, rightDir, new SyncOptions { Compare = checksum ? CompareBy.Content : CompareBy.TimeAndSize, Filter = new SyncFilterDef { Exclude = "" } }, progress, ct);

    public static async Task<List<DiffEntry>> CompareAsync(IFileSystem left, string leftDir, IFileSystem right, string rightDir, SyncOptions opt,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var list = new List<DiffEntry>();
        var filter = new SyncFilter(opt.Filter);
        var cmp = left.IsRemote || right.IsRemote ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        // Returns whether the filter left anything out below here.
        async Task<bool> Walk(string? l, string? r, string rel)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(rel.Length == 0 ? "/" : rel);
            bool skipped = false;
            var li = (l == null ? new() : await left.ListAsync(l, ct)).Where(Visible).ToDictionary(i => i.Name, cmp);
            var ri = (r == null ? new() : await right.ListAsync(r, ct)).Where(Visible).ToDictionary(i => i.Name, cmp);
            foreach (var name in li.Keys.Union(ri.Keys, cmp).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                if (list.Count > MaxEntries) throw new InvalidOperationException($"More than {MaxEntries:N0} entries — compare smaller folders or filter some out.");
                li.TryGetValue(name, out var a); ri.TryGetValue(name, out var b);
                var path = rel.Length == 0 ? name : rel + "/" + name;
                bool typeConflict = a != null && b != null && a.IsDir != b.IsDir;
                if (typeConflict) { list.Add(new(path, a, b, DiffState.TypeConflict)); continue; }   // always shown
                if ((a ?? b)!.IsDir ? !filter.FolderAllowed(path) : !filter.FileAllowed(path) || !filter.Fits(a) && !filter.Fits(b)) { skipped = true; continue; }
                if (a != null && b != null && a.IsDir)
                {
                    list.Add(new(path, a, b, DiffState.Same));
                    if (a.IsLink || b.IsLink) continue;   // symlinked folders are not followed
                    skipped |= await Walk(a.FullPath, b.FullPath, path);
                    continue;
                }
                if (a == null || b == null)
                {
                    var one = (a ?? b)!;
                    int at = list.Count;
                    list.Add(new(path, a, b, a != null ? DiffState.OnlyLeft : DiffState.OnlyRight));
                    if (one.IsDir && !one.IsLink && await Walk(a?.FullPath, b?.FullPath, path)) { list[at] = list[at] with { Partial = true }; skipped = true; }
                    continue;
                }
                list.Add(new(path, a, b, await StateOf(a, b)));
            }
            return skipped;
        }
        async Task<DiffState> StateOf(FsItem a, FsItem b)
        {
            bool sameTime = SameTime(a.Modified, b.Modified, opt);
            switch (opt.Compare)
            {
                case CompareBy.Content:
                    if (a.Length == b.Length && string.Equals(await TransferQueue.HashOf(left, a.FullPath, ct), await TransferQueue.HashOf(right, b.FullPath, ct), StringComparison.OrdinalIgnoreCase)) return DiffState.Same;
                    break;
                case CompareBy.Size:
                    if (a.Length == b.Length) return DiffState.Same;
                    break;
                default:
                    if (a.Length == b.Length && sameTime) return DiffState.Same;
                    break;
            }
            if (sameTime) return DiffState.Differs;
            return a.Modified > b.Modified ? DiffState.LeftNewer : DiffState.RightNewer;
        }
        await Walk(leftDir, rightDir, "");
        return list;
    }

    /// <summary>Within the tolerance, or a whole number of the ignored hours apart (daylight saving, FAT).</summary>
    public static bool SameTime(DateTime a, DateTime b, SyncOptions opt)
    {
        var tol = TimeSpan.FromSeconds(Math.Clamp(opt.ToleranceSeconds, 0, 3600));
        var d = (a - b).Duration();
        if (d <= tol) return true;
        foreach (var h in (opt.IgnoreHourShifts ?? "").Split(',', ';', ' '))
            if (int.TryParse(h, out var hours) && hours is > 0 and <= 24 && (d - TimeSpan.FromHours(hours)).Duration() <= tol) return true;
        return false;
    }

    private static bool Visible(FsItem i) => !i.IsParent && !i.Name.EndsWith(TransferQueue.PartSuffix, StringComparison.Ordinal);

    /// <summary>The first compare's call: the old modes as options.</summary>
    public static List<SyncStep> Plan(IEnumerable<DiffEntry> diffs, SyncMode mode, bool leftToRight, bool deleteExtras) =>
        Plan(diffs, new SyncOptions
        {
            Variant = mode switch { SyncMode.Mirror => SyncVariant.Mirror, SyncMode.Update => SyncVariant.Update, _ => SyncVariant.TwoWay },
            LeftToRight = leftToRight, MirrorDeletes = deleteExtras, Conflicts = ConflictPolicy.NewerWins,
        }, db: null);

    /// <summary>The actions for a comparison. <paramref name="db"/>: the pair's last sync (Two-way); null = none yet.</summary>
    public static List<SyncStep> Plan(IEnumerable<DiffEntry> diffs, SyncOptions opt, Dictionary<string, SyncDbEntry>? db, int pair = 0)
    {
        var all = diffs.ToList();
        var steps = new List<SyncStep>();
        SyncStep S(DiffEntry e, SyncAction a, string why) =>
            new() { Entry = e, Action = a, Planned = a, Why = why, Selected = a != SyncAction.Conflict, Pair = pair };
        // Each placeholder is kept by reference: deciding a deeper folder removes steps and shifts every index after it.
        var oneSidedFolders = new List<(DiffEntry Folder, SyncStep Place)>();
        foreach (var e in all)
        {
            if (e.State == DiffState.Same) continue;
            if (e.IsDir && e.State is DiffState.OnlyLeft or DiffState.OnlyRight)
            {
                // Decided once its contents are planned (below); a placeholder keeps the order.
                var ph = S(e, SyncAction.None, "");
                oneSidedFolders.Add((e, ph));
                steps.Add(ph);
                continue;
            }
            var s = opt.Variant switch
            {
                SyncVariant.TwoWay => TwoWay(e, opt, db, S),
                SyncVariant.Custom => opt.Custom.TryGetValue(e.State, out var act) && act != SyncAction.None ? S(e, act, CustomWhy(e.State)) : null,
                _ => OneWay(e, opt, S),
            };
            if (s != null) steps.Add(s);
        }

        // One-sided folders, deepest first: their own action, then folded into one step when everything inside agrees.
        foreach (var (folder, place) in oneSidedFolders.OrderByDescending(f => f.Folder.Rel.Count(c => c == '/')))
        {
            var inside = steps.Where(x => x != place && x.Entry.Under(folder.Rel)).ToList();
            bool left = folder.State == DiffState.OnlyLeft;
            var copy = left ? SyncAction.CopyRight : SyncAction.CopyLeft;
            var delete = left ? SyncAction.DeleteLeft : SyncAction.DeleteRight;
            SyncAction act; string why;
            switch (opt.Variant)
            {
                case SyncVariant.TwoWay:
                    bool known = db != null && db.TryGetValue(folder.Rel, out var rec) && rec.IsDir;
                    if (!known) { act = copy; why = left ? "new on the left" : "new on the right"; }
                    else if (inside.Any(x => x.Action == copy)) { act = copy; why = $"deleted on the {(left ? "right" : "left")}, but it holds new or changed files — kept"; }
                    else { act = delete; why = $"deleted on the {(left ? "right" : "left")} since the last sync"; }
                    break;
                case SyncVariant.Custom:
                    act = opt.Custom.TryGetValue(folder.State, out var c) ? c : SyncAction.None; why = CustomWhy(folder.State);
                    break;
                default:
                    var s = OneWay(folder, opt, S);
                    act = s?.Action ?? SyncAction.None; why = s?.Why ?? "";
                    break;
            }
            var step = S(folder, act, why);
            var files = all.Where(x => x.Under(folder.Rel) && !x.IsDir).ToList();
            step.FolderFiles = files.Count;
            step.FolderBytes = files.Sum(x => (x.Left ?? x.Right)!.Length);
            // Whole-folder step: every step inside does the same, every file inside has one (its own or a folded subfolder's),
            // and a delete only when the filter left nothing inside out.
            bool fold = act != SyncAction.None && inside.All(x => x.Action == act && x.Selected)
                        && files.All(f => inside.Any(x => x.Entry.Rel == f.Rel || f.Under(x.Entry.Rel)))
                        && (act == copy || !folder.Partial);
            if (fold) foreach (var x in inside) steps.Remove(x);
            else if (act == copy) { step.FolderOnly = true; step.FolderBytes = 0; step.FolderFiles = 0; step.Why += " (the folder; its contents have their own rows)"; }
            else act = SyncAction.None;   // never delete a folder whole when something inside does something else
            int at = steps.IndexOf(place);
            if (at < 0) continue;   // never happens (only descendants are removed, and they come first), but never throw
            if (act == SyncAction.None) steps.RemoveAt(at); else steps[at] = step;
        }
        return steps;
    }

    private static string CustomWhy(DiffState s) => s switch
    {
        DiffState.OnlyLeft => "only on the left", DiffState.OnlyRight => "only on the right", DiffState.LeftNewer => "left is newer",
        DiffState.RightNewer => "right is newer", DiffState.Differs => "different, same time", _ => "a file on one side, a folder on the other",
    };

    private static SyncStep? OneWay(DiffEntry e, SyncOptions opt, Func<DiffEntry, SyncAction, string, SyncStep> S)
    {
        bool ltr = opt.LeftToRight, mirror = opt.Variant == SyncVariant.Mirror;
        var copy = ltr ? SyncAction.CopyRight : SyncAction.CopyLeft;
        var del = ltr ? SyncAction.DeleteRight : SyncAction.DeleteLeft;
        var onlySource = ltr ? DiffState.OnlyLeft : DiffState.OnlyRight;
        var onlyTarget = ltr ? DiffState.OnlyRight : DiffState.OnlyLeft;
        var sourceNewer = ltr ? DiffState.LeftNewer : DiffState.RightNewer;
        var targetNewer = ltr ? DiffState.RightNewer : DiffState.LeftNewer;
        if (e.State == onlySource) return S(e, copy, "missing on the target");
        if (e.State == sourceNewer) return S(e, copy, "source is newer");
        if (e.State == DiffState.Differs) return S(e, copy, "different content");
        if (e.State == targetNewer) return mirror ? S(e, copy, "⚠ target is NEWER — mirror overwrites it") : S(e, SyncAction.Conflict, "target is newer — not overwritten by Update");
        if (e.State == onlyTarget) return mirror && opt.MirrorDeletes ? S(e, del, "not in the source — mirror deletes it") : null;
        return S(e, SyncAction.Conflict, "a file on one side, a folder on the other");
    }

    private static SyncStep TwoWay(DiffEntry e, SyncOptions opt, Dictionary<string, SyncDbEntry>? db, Func<DiffEntry, SyncAction, string, SyncStep> S)
    {
        if (e.State == DiffState.TypeConflict) return S(e, SyncAction.Conflict, "a file on one side, a folder on the other");
        SyncDbEntry? d = null;
        bool known = db != null && db.TryGetValue(e.Rel, out d) && !d.IsDir;
        bool Changed(FsItem i, long bytes, long ticks) => i.Length != bytes || !SameTime(i.Modified, new DateTime(ticks), opt);
        if (e.Left != null && e.Right == null)
            return !known ? S(e, SyncAction.CopyRight, "new on the left")
                : Changed(e.Left, d!.LeftBytes, d.LeftTicks) ? S(e, SyncAction.CopyRight, "deleted on the right, but changed on the left — kept")
                : S(e, SyncAction.DeleteLeft, "deleted on the right since the last sync");
        if (e.Right != null && e.Left == null)
            return !known ? S(e, SyncAction.CopyLeft, "new on the right")
                : Changed(e.Right, d!.RightBytes, d.RightTicks) ? S(e, SyncAction.CopyLeft, "deleted on the left, but changed on the right — kept")
                : S(e, SyncAction.DeleteRight, "deleted on the left since the last sync");
        if (known)
        {
            bool lc = Changed(e.Left!, d!.LeftBytes, d.LeftTicks), rc = Changed(e.Right!, d.RightBytes, d.RightTicks);
            if (lc && !rc) return S(e, SyncAction.CopyRight, "changed on the left since the last sync");
            if (rc && !lc) return S(e, SyncAction.CopyLeft, "changed on the right since the last sync");
        }
        string why = known ? "changed on both sides since the last sync" : "different on both sides (no earlier sync to tell which changed)";
        return opt.Conflicts switch
        {
            ConflictPolicy.LeftWins => S(e, SyncAction.CopyRight, why + " — left wins"),
            ConflictPolicy.RightWins => S(e, SyncAction.CopyLeft, why + " — right wins"),
            ConflictPolicy.NewerWins when e.State == DiffState.LeftNewer => S(e, SyncAction.CopyRight, why + " — left is newer"),
            ConflictPolicy.NewerWins when e.State == DiffState.RightNewer => S(e, SyncAction.CopyLeft, why + " — right is newer"),
            _ => S(e, SyncAction.Conflict, why + " — decide yourself"),
        };
    }

    /// <summary>The first compare's call (no filter, plain deletes).</summary>
    public static Task<List<TransferJob>> BuildJobsAsync(IEnumerable<SyncStep> selected, FsRef leftRef, string leftDir, FsRef rightRef, string rightDir, CancellationToken ct = default) =>
        BuildJobsAsync(selected, leftRef, leftDir, rightRef, rightDir, null, null, "you", ct);

    /// <summary>The transfer jobs for the steps that run: one per direction, file by file (folders expanded through the
    /// filter, missing parent folders created), with that direction's deletions attached to run after its copies. With
    /// options, overwritten and deleted files go where the deletion setting says (Recycle Bin / versions / permanent).</summary>
    public static async Task<List<TransferJob>> BuildJobsAsync(IEnumerable<SyncStep> selected, FsRef leftRef, string leftDir, FsRef rightRef, string rightDir,
        SyncOptions? opt, DateTime? stamp, string actor, CancellationToken ct = default)
    {
        var sel = selected.Where(s => s.Runs).ToList();
        var filter = opt == null ? SyncFilter.None : new SyncFilter(opt.Filter);
        var jobs = new List<TransferJob>();
        foreach (var toRight in new[] { true, false })
        {
            var copies = sel.Where(s => s.Action == (toRight ? SyncAction.CopyRight : SyncAction.CopyLeft)).ToList();
            var deletes = sel.Where(s => s.Action == (toRight ? SyncAction.DeleteRight : SyncAction.DeleteLeft)).Select(s => (toRight ? s.Entry.Right : s.Entry.Left)!)
                .OrderByDescending(i => i.FullPath.Length).ToList();   // inside before outside
            if (copies.Count == 0 && deletes.Count == 0) continue;
            var (fromRef, fromDir, toRef, toDir) = toRight ? (leftRef, leftDir, rightRef, rightDir) : (rightRef, rightDir, leftRef, leftDir);
            using var src = fromRef.Open();
            using var dst = toRef.Equals(fromRef) && fromRef.Kind == "local" ? null : toRef.Open();
            var d = dst ?? src;
            var plan = new List<(FsItem, string)>();
            var planned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Put(FsItem it, string target) { if (planned.Add(target)) plan.Add((it, target)); }
            async Task Add(FsItem it, string target, string rel)
            {
                Put(it, target);
                if (!it.IsDir || it.IsLink) return;
                foreach (var c in await src.ListAsync(it.FullPath, ct))
                {
                    if (!Visible(c)) continue;
                    var crel = rel + "/" + c.Name;
                    if (c.IsDir ? !filter.FolderAllowed(crel) : !filter.FileAllowed(crel) || !filter.Fits(c)) continue;
                    await Add(c, d.Join(target, c.Name), crel);
                }
            }
            foreach (var s in copies)
            {
                var item = (toRight ? s.Entry.Left : s.Entry.Right)!;
                var parts = s.Entry.Rel.Split('/');
                // Parent folders that don't exist on the target yet (a file inside a folder that is new there).
                string sp = fromDir, tp = toDir;
                for (int i = 0; i < parts.Length - 1; i++)
                {
                    sp = src.Join(sp, parts[i]); tp = d.Join(tp, parts[i]);
                    Put(new FsItem { Name = parts[i], FullPath = sp, IsDir = true }, tp);
                }
                if (s.FolderOnly) Put(item, d.Join(tp, parts[^1]));
                else await Add(item, d.Join(tp, parts[^1]), s.Entry.Rel);
            }
            jobs.Add(new TransferJob
            {
                From = fromRef, To = toRef, DestDir = toDir, Plan = plan, DeleteOnTarget = deletes, OnConflict = Conflict.Overwrite, Actor = actor,
                Items = copies.Select(c => (toRight ? c.Entry.Left : c.Entry.Right)!).ToList(),
                Retire = opt == null ? null : SyncVersioning.For(d, toDir, opt, leftSide: !toRight, stamp ?? DateTime.Now),
                Verify = opt?.Verify ?? AppSettings.Current.TransferVerify,
                Label = $"Sync {(toRight ? "→" : "←")} {copies.Count} item(s){(deletes.Count > 0 ? $", delete {deletes.Count}" : "")}",
            });
        }
        return jobs;
    }

    public static string Totals(IEnumerable<SyncStep> steps)
    {
        var all = steps.ToList();
        var s = all.Where(x => x.Runs).ToList();
        int r = s.Count(x => x.Action == SyncAction.CopyRight), l = s.Count(x => x.Action == SyncAction.CopyLeft);
        int dr = s.Count(x => x.Action == SyncAction.DeleteRight), dl = s.Count(x => x.Action == SyncAction.DeleteLeft);
        int c = all.Count(x => x.Action == SyncAction.Conflict);
        return $"{r} → ({Fmt.Bytes(s.Where(x => x.Action == SyncAction.CopyRight).Sum(x => x.Bytes))}) · {l} ← ({Fmt.Bytes(s.Where(x => x.Action == SyncAction.CopyLeft).Sum(x => x.Bytes))})" +
               (dr + dl > 0 ? $" · DELETE {dr + dl}" : "") + (c > 0 ? $" · {c} conflict(s) skipped" : "");
    }
}
