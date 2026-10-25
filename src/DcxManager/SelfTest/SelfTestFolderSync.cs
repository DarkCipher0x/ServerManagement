using System.IO;
using System.Text;
using System.Windows.Controls;
using DcxManager.Files;
using DcxManager.Services;
using DcxManager.Views;

namespace DcxManager;

/// <summary>
/// 0.6.26 (ADR-102): FreeFileSync-style folder sync on this PC's disk (scratch folders under the report folder): the
/// filter, the compare variants, Two-way with the last-sync database (deletions carry over both ways, changes on both
/// sides are conflicts, a whole deleted folder is one step), versioning of deleted and overwritten files, Mirror and
/// Update, pruning, the deletion guard and a missing folder, the encrypted job store and database, schedules, an
/// unattended run through the scheduler, and the windows built without being shown.
/// </summary>
public static class FolderSyncTests
{
    public static async Task RunAsync(string reportDir, Action<string> ok, Action<string, Exception?> fail)
    {
        var root = Path.Combine(reportDir, "folder-sync-test");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(root);
        LocalFs.HardDeleteForTests = true;
        try
        {
            await Run("folder sync: filter patterns (anchored, any depth, folders, includes, size/age) and partial folders", () => Filter(root), ok, fail);
            await Run("folder sync: compare by time+size, size only, content; tolerance and ignored hour shifts; Custom actions", () => Variants(root), ok, fail);
            await Run("folder sync: Two-way remembers the last sync — deletions both ways, changes, a deleted folder as one step, conflicts skipped; versions kept for deleted and overwritten files", () => TwoWay(root), ok, fail);
            await Run("folder sync: Mirror (with and without deletions), Update skips a newer target; versions per run / replace; pruning by age and count", () => OneWayAndVersions(root), ok, fail);
            await Run("folder sync: unattended runs stop on a missing folder and above the deletion limit", () => Guards(root), ok, fail);
            await Run("folder sync: jobs and last-sync state encrypted at rest; schedules (interval, daily, weekly); Run now through the scheduler", () => JobsAndSchedule(root), ok, fail);
            await Run("folder sync: the sync window (pairs, five option tabs, compare, per-row actions, one-click presets re-compare) and the jobs window, built without showing", () => Windows(root), ok, fail);
            await Run("folder sync: File Commander ⇄ Sync menu — Update → copies in one step, a refused Mirror changes nothing, Mirror → removes the extra", () => Quick(root), ok, fail);
        }
        finally { LocalFs.HardDeleteForTests = false; }
    }

    private static async Task Run(string name, Func<Task> test, Action<string> ok, Action<string, Exception?> fail)
    {
        try { await test(); ok(name); }
        catch (Exception ex) { fail(name, ex); }
    }

    private static readonly DateTime T0 = new(2024, 3, 1, 12, 0, 0);

    private static string Dir(string root, string name) { var d = Path.Combine(root, name); if (Directory.Exists(d)) Directory.Delete(d, true); Directory.CreateDirectory(d); return d; }

    private static void W(string dir, string rel, string text, DateTime t)
    {
        var p = Path.Combine(dir, rel.Replace('/', '\\'));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
        File.SetLastWriteTime(p, t);
    }

    private static void Expect(bool ok, string what) { if (!ok) throw new Exception(what); }

    private static string Steps(IEnumerable<SyncStep> s) => string.Join(", ", s.Select(x => $"{x.Entry.Rel}={x.Action}"));

    private static SyncJobDef Job(string l, string r, SyncOptions o) => new() { Name = "selftest sync", Pairs = { new SyncPairDef { LeftPath = l, RightPath = r } }, Options = o };

    // ---- filter ----

    private static async Task Filter(string root)
    {
        var f = new SyncFilter(new SyncFilterDef { Include = "*", Exclude = "*.tmp\n/build/\nnode_modules/\n" });
        Expect(!f.FileAllowed("a/b/x.tmp") && f.FileAllowed("a/b/x.txt"), "*.tmp at any depth");
        Expect(!f.FolderAllowed("build") && f.FolderAllowed("src/build"), "/build/ is anchored at the sync folder");
        Expect(!f.FolderAllowed("node_modules") && !f.FolderAllowed("web/app/node_modules"), "node_modules/ at any depth");
        Expect(!f.FolderAllowed(SyncFilter.VersionsFolder) && !f.FileAllowed("x" + TransferQueue.PartSuffix), "versions folder and part files are never synced");
        var inc = new SyncFilter(new SyncFilterDef { Include = "*.txt\ndocs/", Exclude = "" });
        Expect(inc.FileAllowed("n.txt") && inc.FileAllowed("docs/img/p.png") && !inc.FileAllowed("img/p.png"), "include: names and a folder's contents");
        var lim = new SyncFilter(new SyncFilterDef { Exclude = "", MinBytes = 10, MaxBytes = 100, MaxAgeDays = 30 }, now: T0);
        Expect(lim.Fits(new FsItem { Name = "a", FullPath = "a", Length = 50, Modified = T0.AddDays(-1) }), "within limits");
        Expect(!lim.Fits(new FsItem { Name = "a", FullPath = "a", Length = 5, Modified = T0 }) && !lim.Fits(new FsItem { Name = "a", FullPath = "a", Length = 500, Modified = T0 })
               && !lim.Fits(new FsItem { Name = "a", FullPath = "a", Length = 50, Modified = T0.AddDays(-60) }), "outside the limits");
        Expect(SyncFilter.Check(new SyncFilterDef { MinBytes = 10, MaxBytes = 5 }) != null, "min > max refused");
        Expect(SyncOptionsEditor.ParseSize("10 MB") == 10L << 20 && SyncOptionsEditor.ParseSize("1.5GB") == (long)(1.5 * (1L << 30)) && SyncOptionsEditor.ParseSize("") == 0 && SyncOptionsEditor.ParseSize("ten") == null, "size parsing");

        // A one-sided folder holding an excluded file is "partial": it may be copied, never deleted whole.
        var L = Dir(root, "f-left"); var R = Dir(root, "f-right");
        W(L, "keep.txt", "k", T0); W(R, "keep.txt", "k", T0);
        W(R, "extra/a.txt", "a", T0); W(R, "extra/junk.tmp", "j", T0);
        W(L, "build/out.bin", "o", T0); W(L, "src/build/x.txt", "x", T0);
        var o = new SyncOptions { Variant = SyncVariant.Mirror, Filter = new SyncFilterDef { Exclude = "*.tmp\n/build/" } };
        var lfs = new LocalFs();
        var diffs = await FolderSync.CompareAsync(lfs, L, lfs, R, o);
        Expect(!diffs.Any(d => d.Rel.StartsWith("build", StringComparison.Ordinal)) && diffs.Any(d => d.Rel == "src/build/x.txt"), "excluded folder not listed: " + string.Join(",", diffs.Select(d => d.Rel)));
        Expect(diffs.Single(d => d.Rel == "extra").Partial && !diffs.Any(d => d.Rel == "extra/junk.tmp"), "partial folder");
        var plan = FolderSync.Plan(diffs, o, null);
        Expect(!plan.Any(s => s.Entry.Rel == "extra" && s.Action == SyncAction.DeleteRight) && plan.Any(s => s.Entry.Rel == "extra/a.txt" && s.Action == SyncAction.DeleteRight), "partial folder not deleted whole: " + Steps(plan));
    }

    // ---- compare variants and Custom ----

    private static async Task Variants(string root)
    {
        var L = Dir(root, "v-left"); var R = Dir(root, "v-right");
        W(L, "sametime.txt", "aaaa", T0); W(R, "sametime.txt", "bbbb", T0);           // same size and time, other content
        W(L, "shifted.txt", "x", T0); W(R, "shifted.txt", "x", T0.AddHours(1));       // DST-style shift
        W(L, "close.txt", "x", T0); W(R, "close.txt", "x", T0.AddSeconds(1));         // within tolerance
        W(L, "sizeonly.txt", "same", T0); W(R, "sizeonly.txt", "SAME", T0.AddDays(3)); // same size, far apart
        var lfs = new LocalFs();
        DiffState St(List<DiffEntry> d, string rel) => d.Single(x => x.Rel == rel).State;
        var time = await FolderSync.CompareAsync(lfs, L, lfs, R, new SyncOptions { Filter = new SyncFilterDef { Exclude = "" } });
        Expect(St(time, "sametime.txt") == DiffState.Same && St(time, "close.txt") == DiffState.Same && St(time, "shifted.txt") == DiffState.RightNewer && St(time, "sizeonly.txt") == DiffState.RightNewer, "time+size");
        var shift = await FolderSync.CompareAsync(lfs, L, lfs, R, new SyncOptions { IgnoreHourShifts = "1", Filter = new SyncFilterDef { Exclude = "" } });
        Expect(St(shift, "shifted.txt") == DiffState.Same, "ignore 1-hour shift");
        var size = await FolderSync.CompareAsync(lfs, L, lfs, R, new SyncOptions { Compare = CompareBy.Size, Filter = new SyncFilterDef { Exclude = "" } });
        Expect(St(size, "sizeonly.txt") == DiffState.Same, "size only");
        var content = await FolderSync.CompareAsync(lfs, L, lfs, R, new SyncOptions { Compare = CompareBy.Content, Filter = new SyncFilterDef { Exclude = "" } });
        Expect(St(content, "sametime.txt") == DiffState.Differs && St(content, "close.txt") == DiffState.Same, "content");

        W(L, "onlyL.txt", "l", T0); W(R, "onlyR.txt", "r", T0);
        var all = await FolderSync.CompareAsync(lfs, L, lfs, R, new SyncOptions { Filter = new SyncFilterDef { Exclude = "" } });
        var custom = new SyncOptions { Variant = SyncVariant.Custom, Custom = { [DiffState.OnlyLeft] = SyncAction.DeleteLeft, [DiffState.OnlyRight] = SyncAction.None, [DiffState.RightNewer] = SyncAction.CopyRight } };
        var plan = FolderSync.Plan(all, custom, null);
        Expect(plan.Single(s => s.Entry.Rel == "onlyL.txt").Action == SyncAction.DeleteLeft && !plan.Any(s => s.Entry.Rel == "onlyR.txt")
               && plan.Single(s => s.Entry.Rel == "shifted.txt").Action == SyncAction.CopyRight, "custom: " + Steps(plan));
    }

    // ---- Two-way with the database ----

    private static async Task TwoWay(string root)
    {
        var L = Dir(root, "t-left"); var R = Dir(root, "t-right");
        foreach (var n in new[] { "a.txt", "b.txt", "c.txt", "sub/d.txt", "sub/deep/e.txt" }) { W(L, n, "v1 " + n, T0); W(R, n, "v1 " + n, T0); }
        var o = new SyncOptions { Variant = SyncVariant.TwoWay, Deletion = DeletionMode.Versioning, Naming = VersionNaming.TimestampFile, VersionsKeepDays = 0 };
        var job = Job(L, R, o);
        var key = SyncDatabase.KeyOf(FsRef.Local, L, FsRef.Local, R);
        SyncDatabase.Forget(key);
        try
        {
            // First run: identical, so the state is remembered.
            var first = await SyncRunner.CompareAsync(job);
            Expect(first[0].Error == null && first[0].Steps.Count == 0, "first compare: " + first[0].Error + Steps(first[0].Steps));
            var r1 = await SyncRunner.ExecuteAsync(first, null, "selftest");
            Expect(r1.Ok && SyncDatabase.Load(key).Count >= 5, "first run remembered: " + r1.Text);

            File.Delete(Path.Combine(L, "a.txt"));                                   // deleted on the left → delete right
            W(R, "b.txt", "v2 b from the right", T0.AddHours(1));                    // changed on the right → copy left (left's old b versioned)
            W(L, "new.txt", "new on the left", T0.AddHours(1));                      // new → copy right
            W(L, "c.txt", "v2 c left", T0.AddHours(2)); W(R, "c.txt", "v2 c right!", T0.AddHours(3));   // changed on both → conflict
            Directory.Delete(Path.Combine(L, "sub"), true);                           // whole folder deleted on the left → one step
            var second = await SyncRunner.CompareAsync(job);
            var s = second[0].Steps;
            SyncAction A(string rel) => s.Single(x => x.Entry.Rel == rel).Action;
            Expect(A("a.txt") == SyncAction.DeleteRight && A("b.txt") == SyncAction.CopyLeft && A("new.txt") == SyncAction.CopyRight && A("c.txt") == SyncAction.Conflict
                   && A("sub") == SyncAction.DeleteRight && !s.Any(x => x.Entry.Rel.StartsWith("sub/", StringComparison.Ordinal)), "plan: " + Steps(s));
            Expect(!s.Single(x => x.Entry.Rel == "c.txt").Selected && s.Single(x => x.Entry.Rel == "sub").FolderFiles == 2, "conflict unticked, folder covers 2 files");
            var r2 = await SyncRunner.ExecuteAsync(second, null, "selftest");
            Expect(r2.Ok, "run: " + r2.Text);
            Expect(!File.Exists(Path.Combine(R, "a.txt")) && !Directory.Exists(Path.Combine(R, "sub")) && File.ReadAllText(Path.Combine(L, "b.txt")) == "v2 b from the right"
                   && File.ReadAllText(Path.Combine(R, "new.txt")) == "new on the left", "files after the run");
            Expect(File.ReadAllText(Path.Combine(L, "c.txt")) == "v2 c left" && File.ReadAllText(Path.Combine(R, "c.txt")) == "v2 c right!", "conflict left alone");
            var vr = Path.Combine(R, SyncFilter.VersionsFolder); var vl = Path.Combine(L, SyncFilter.VersionsFolder);
            Expect(Directory.GetFiles(vr, "a *.txt").Length == 1 && Directory.GetDirectories(vr, "sub *").Length == 1 && File.Exists(Path.Combine(Directory.GetDirectories(vr, "sub *")[0], "deep", "e.txt")),
                "deleted file and folder kept as versions on the right");
            Expect(Directory.GetFiles(vl, "b *.txt") is [var oldB] && File.ReadAllText(oldB) == "v1 b.txt", "overwritten file kept as a version on the left");

            // Third compare: only the conflict is left, and the versions folders are never compared.
            var third = await SyncRunner.CompareAsync(job);
            Expect(third[0].Steps.Count == 1 && third[0].Steps[0].Entry.Rel == "c.txt" && !third[0].Diffs.Any(d => d.Rel.StartsWith(SyncFilter.VersionsFolder, StringComparison.Ordinal)), "after: " + Steps(third[0].Steps));

            // Deleted on one side but changed on the other: kept (copied back), never lost.
            File.Delete(Path.Combine(R, "new.txt")); W(L, "new.txt", "changed again", T0.AddHours(5));
            var fourth = await SyncRunner.CompareAsync(job);
            Expect(fourth[0].Steps.Single(x => x.Entry.Rel == "new.txt").Action == SyncAction.CopyRight, "deleted+changed kept: " + Steps(fourth[0].Steps));

            // A winner instead of skipping.
            job.Options.Conflicts = ConflictPolicy.RightWins;
            var fifth = await SyncRunner.CompareAsync(job);
            Expect(fifth[0].Steps.Single(x => x.Entry.Rel == "c.txt").Action == SyncAction.CopyLeft, "right wins");
            Expect(!Encoding.UTF8.GetString(File.ReadAllBytes(Directory.GetFiles(SecureStore.DataDir, "syncdb-*.dcx").First())).Contains("b.txt"), "last-sync state in plaintext");
        }
        finally { SyncDatabase.Forget(key); }
    }

    // ---- Mirror, Update, versions ----

    private static async Task OneWayAndVersions(string root)
    {
        var L = Dir(root, "m-left"); var R = Dir(root, "m-right");
        W(L, "x.txt", "new x", T0.AddHours(1)); W(R, "x.txt", "old x", T0);
        W(L, "y.txt", "y left", T0); W(R, "y.txt", "y right newer", T0.AddHours(2));
        W(R, "extra.txt", "only right", T0);
        var lfs = new LocalFs();
        var diffs = await FolderSync.CompareAsync(lfs, L, lfs, R, new SyncOptions());
        var upd = FolderSync.Plan(diffs, new SyncOptions { Variant = SyncVariant.Update }, null);
        Expect(upd.Single(s => s.Entry.Rel == "y.txt").Action == SyncAction.Conflict && !upd.Any(s => s.Entry.Rel == "extra.txt"), "update: " + Steps(upd));
        // 0.6.27: ← Update with a folder only on the target (left, nested) listed before a folder only on the source (right):
        // deciding the nested one removed steps and the next placeholder index ran past the end ("Index was out of range").
        var UL = Dir(root, "u-left"); var UR = Dir(root, "u-right");
        W(UL, "a/b/f.txt", "only left", T0); W(UL, "a/c.txt", "only left", T0); W(UR, "z/g.txt", "only right", T0); W(UR, "z/y/h.txt", "only right", T0);
        var udiffs = await FolderSync.CompareAsync(lfs, UL, lfs, UR, new SyncOptions());
        foreach (var variant in new[] { SyncVariant.Update, SyncVariant.Mirror })
            foreach (var ltr in new[] { false, true })
            {
                var up = FolderSync.Plan(udiffs, new SyncOptions { Variant = variant, LeftToRight = ltr }, null);
                var expect = (variant, ltr) switch
                {
                    (SyncVariant.Update, false) => "z=CopyLeft",
                    (SyncVariant.Update, true) => "a=CopyRight",
                    (SyncVariant.Mirror, false) => "a=DeleteLeft, z=CopyLeft",
                    _ => "a=CopyRight, z=DeleteRight",
                };
                Expect(Steps(up.OrderBy(s => s.Entry.Rel, StringComparer.Ordinal)) == expect, $"{variant} {(ltr ? "→" : "←")} with one-sided folders on both sides: " + Steps(up));
            }

        var keep = FolderSync.Plan(diffs, new SyncOptions { Variant = SyncVariant.Mirror, MirrorDeletes = false }, null);
        Expect(!keep.Any(s => s.Action == SyncAction.DeleteRight) && keep.Single(s => s.Entry.Rel == "y.txt").Action == SyncAction.CopyRight, "mirror without deletes: " + Steps(keep));

        var o = new SyncOptions { Variant = SyncVariant.Mirror, Deletion = DeletionMode.Versioning, Naming = VersionNaming.TimestampFolder, VersionsRight = Path.Combine(root, "m-versions"), VersionsKeepDays = 0 };
        var job = Job(L, R, o);
        var runs = await SyncRunner.CompareAsync(job);
        var r = await SyncRunner.ExecuteAsync(runs, null, "selftest");
        Expect(r.Ok && !File.Exists(Path.Combine(R, "extra.txt")) && File.ReadAllText(Path.Combine(R, "y.txt")) == "y left", "mirror run: " + r.Text);
        var runDirs = Directory.GetDirectories(o.VersionsRight);
        Expect(runDirs.Length == 1 && File.Exists(Path.Combine(runDirs[0], "extra.txt")) && File.ReadAllText(Path.Combine(runDirs[0], "y.txt")) == "y right newer" && File.ReadAllText(Path.Combine(runDirs[0], "x.txt")) == "old x",
            "one versions folder per run, with the deleted and both overwritten files");

        // Replace: only the last version.
        var rep = new SyncRetire { Mode = DeletionMode.Versioning, Root = L, VersionsDir = Path.Combine(root, "m-replace"), Naming = VersionNaming.Replace };
        W(L, "r.txt", "one", T0); await SyncVersioning.RetireAsync(lfs, rep, Path.Combine(L, "r.txt"), false, default);
        W(L, "r.txt", "two", T0); await SyncVersioning.RetireAsync(lfs, rep, Path.Combine(L, "r.txt"), false, default);
        Expect(File.ReadAllText(Path.Combine(rep.VersionsDir, "r.txt")) == "two" && Directory.GetFiles(rep.VersionsDir).Length == 1, "replace keeps the last");

        // Pruning by age and by count (time stamp per file).
        var pv = Dir(root, "m-prune");
        foreach (var d in new[] { 1, 2, 3, 40, 50 })
            W(pv, $"doc {T0.AddDays(-d).ToString(SyncVersioning.StampFormat)}.txt", "v", T0);
        W(pv, "unrelated.txt", "u", T0);
        int byAge = await SyncVersioning.PruneAsync(lfs, pv, VersionNaming.TimestampFile, 30, 0, T0, default);
        Expect(byAge == 2 && File.Exists(Path.Combine(pv, "unrelated.txt")), "pruned by age: " + byAge);
        int byCount = await SyncVersioning.PruneAsync(lfs, pv, VersionNaming.TimestampFile, 0, 1, T0, default);
        Expect(byCount == 2 && Directory.GetFiles(pv, "doc *.txt") is [var newest] && newest.Contains(T0.AddDays(-1).ToString(SyncVersioning.StampFormat)), "pruned by count: " + byCount);

        // No Recycle Bin on a server: versions; Permanent only when allowed.
        Expect(SyncVersioning.Effective(DeletionMode.RecycleBin, new NoneRemote(), false) == DeletionMode.Versioning
               && SyncVersioning.Effective(DeletionMode.Permanent, new NoneRemote(), false) == DeletionMode.Versioning
               && SyncVersioning.Effective(DeletionMode.Permanent, new NoneRemote(), true) == DeletionMode.Permanent, "effective deletion mode");
    }

    /// <summary>A stand-in "server" for the deletion-mode rule (only <see cref="IFileSystem.IsRemote"/> is read).</summary>
    private sealed class NoneRemote : IFileSystem
    {
        private readonly NoneFs _n = new();
        public FsRef Ref => _n.Ref; public string Title => "remote"; public bool IsRemote => true; public bool CanAppend => false;
        public Task<string> HomeAsync(CancellationToken ct = default) => _n.HomeAsync(ct);
        public Task<List<FsItem>> ListAsync(string path, CancellationToken ct = default) => _n.ListAsync(path, ct);
        public Task<FsItem?> StatAsync(string path, CancellationToken ct = default) => _n.StatAsync(path, ct);
        public Task MkdirAsync(string path, CancellationToken ct = default) => _n.MkdirAsync(path, ct);
        public Task DeleteAsync(string path, bool isDir, CancellationToken ct = default) => _n.DeleteAsync(path, isDir, ct);
        public Task RenameAsync(string from, string to, CancellationToken ct = default) => _n.RenameAsync(from, to, ct);
        public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default) => _n.OpenReadAsync(path, ct);
        public Task<Stream> CreateAsync(string path, CancellationToken ct = default) => _n.CreateAsync(path, ct);
        public Task<Stream> OpenAppendAsync(string path, CancellationToken ct = default) => _n.OpenAppendAsync(path, ct);
        public Task ReplaceAsync(string from, string to, CancellationToken ct = default) => _n.ReplaceAsync(from, to, ct);
        public Task SetTimesAsync(string path, DateTime modified, CancellationToken ct = default) => _n.SetTimesAsync(path, modified, ct);
        public Task SetModeAsync(string path, int mode, CancellationToken ct = default) => _n.SetModeAsync(path, mode, ct);
        public Task<string?> HashAsync(string path, CancellationToken ct = default) => _n.HashAsync(path, ct);
        public string Join(string dir, string name) => dir + "/" + name;
        public string Parent(string path) => path[..Math.Max(0, path.LastIndexOf('/'))];
        public void Dispose() { }
    }

    // ---- guards ----

    private static async Task Guards(string root)
    {
        var L = Dir(root, "g-left"); var R = Dir(root, "g-right");
        for (int i = 0; i < 20; i++) { W(L, $"f{i:00}.txt", "x", T0); W(R, $"f{i:00}.txt", "x", T0); }
        var job = Job(L, R, new SyncOptions { Variant = SyncVariant.TwoWay, Deletion = DeletionMode.Versioning, MaxDeletePercent = 50 });
        var key = SyncDatabase.KeyOf(FsRef.Local, L, FsRef.Local, R);
        SyncDatabase.Forget(key);
        try
        {
            Expect((await SyncRunner.ExecuteAsync(await SyncRunner.CompareAsync(job), null, "selftest")).Ok, "baseline");
            for (int i = 0; i < 15; i++) File.Delete(Path.Combine(L, $"f{i:00}.txt"));
            var r = await SyncRunner.RunUnattendedAsync(job, "selftest");
            Expect(!r.Ok && r.Text.Contains("15 of 20", StringComparison.Ordinal) && Directory.GetFiles(R, "f*.txt").Length == 20, "deletion guard: " + r.Text);
            var missing = Job(Path.Combine(root, "g-unplugged"), R, job.Options);
            var m = await SyncRunner.RunUnattendedAsync(missing, "selftest");
            Expect(!m.Ok && m.Text.Contains("isn't there", StringComparison.Ordinal) && Directory.GetFiles(R, "f*.txt").Length == 20, "missing folder: " + m.Text);
        }
        finally { SyncDatabase.Forget(key); }
    }

    // ---- jobs, encryption, schedules, Run now ----

    private static async Task JobsAndSchedule(string root)
    {
        var L = Dir(root, "j-left"); var R = Dir(root, "j-right");
        W(L, "hello.txt", "from the schedule", T0);
        var job = Job(L, R, new SyncOptions { Variant = SyncVariant.Mirror, Deletion = DeletionMode.Versioning });
        job.Name = "selftest-sync-job-secret-name";
        job.Schedule = SyncSchedule.Off;
        try
        {
            SyncJobStore.Save(job);
            SyncJobStore.Reset();
            var back = SyncJobStore.Get(job.Id);
            Expect(back is { Name: "selftest-sync-job-secret-name" } && back.Pairs[0].LeftPath == L && back.Options.Variant == SyncVariant.Mirror, "job round-trip");
            Expect(!Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(SecureStore.DataDir, "syncjobs.dcx"))).Contains("selftest-sync-job-secret-name"), "jobs in plaintext");

            var now = new DateTime(2026, 10, 25, 10, 0, 0, DateTimeKind.Utc);
            var t = new SyncJobDef { Schedule = SyncSchedule.Interval, IntervalMinutes = 30, LastRunUtc = now.AddMinutes(-10) };
            Expect(t.NextRunUtc(now) == now.AddMinutes(20), "interval");
            t.LastRunUtc = null; Expect(t.NextRunUtc(now) == now, "interval, never ran: now");
            var daily = new SyncJobDef { Schedule = SyncSchedule.Daily, At = "02:00", LastRunUtc = now };
            var next = daily.NextRunUtc(now)!.Value.ToLocalTime();
            Expect(next.TimeOfDay == TimeSpan.FromHours(2) && next > now.ToLocalTime() && next - now.ToLocalTime() <= TimeSpan.FromDays(1), "daily: " + next);
            var weekly = new SyncJobDef { Schedule = SyncSchedule.Weekly, At = "03:30", Days = 1 << (int)DayOfWeek.Wednesday, LastRunUtc = now };
            var wnext = weekly.NextRunUtc(now)!.Value.ToLocalTime();
            Expect(wnext.DayOfWeek == DayOfWeek.Wednesday && wnext.TimeOfDay == new TimeSpan(3, 30, 0), "weekly: " + wnext);
            Expect(new SyncJobDef { Schedule = SyncSchedule.Weekly, Days = 0 }.NextRunUtc(now) == null && new SyncJobDef().NextRunUtc(now) == null, "no days / manual");

            // Run now: the scheduler runs it unattended and notes the result on the job.
            Expect(SyncScheduler.Fire(job.Id, "selftest"), "fire");
            for (int i = 0; i < 150 && SyncJobStore.Get(job.Id)?.LastRunUtc == null; i++) await Task.Delay(100);
            var ran = SyncJobStore.Get(job.Id)!;
            Expect(ran.LastRunUtc != null && ran.LastResult.StartsWith("✓", StringComparison.Ordinal) && File.ReadAllText(Path.Combine(R, "hello.txt")) == "from the schedule", "run now: " + ran.LastResult);
            for (int i = 0; i < 50 && SyncScheduler.IsRunning(job.Id); i++) await Task.Delay(100);
        }
        finally { SyncJobStore.Remove(job.Id); }
    }

    // ---- File Commander's quick syncs (a real Commander off screen; the confirmation answered by the self-test) ----

    private static async Task Quick(string root)
    {
        var L = Dir(root, "q-left"); var R = Dir(root, "q-right");
        W(L, "a.txt", "a", T0); W(L, "sub/b.txt", "b", T0); W(R, "old.txt", "only on the right", T0);
        var s = AppSettings.Current;
        var (wasL, wasR, wasRestore) = (s.CommanderLeft, s.CommanderRight, s.CommanderRestoreLast);
        var confirmWas = Dialogs.ConfirmForTests;
        CommanderView? view = null;
        try
        {
            s.CommanderRestoreLast = true; s.CommanderLeft = "local|" + L; s.CommanderRight = "local|" + R;
            view = new CommanderView();
            await view.LeftForTest.GoAsync(L, record: false);
            await view.RightForTest.GoAsync(R, record: false);
            var menu = view.SyncMenu().OfType<MenuItem>().Select(m => m.Header as string ?? "").ToList();
            Expect(new[] { "⇄ Compare / sync folders…", "Update →", "←  Update", "Mirror →", "←  Mirror", "⇄  Two-way", "Sync jobs…" }.All(h => menu.Any(m => m.StartsWith(h, StringComparison.Ordinal))), "menu: " + string.Join(" | ", menu));

            Dialogs.ConfirmForTests = true;
            await view.QuickSyncAsync(SyncVariant.Update, true);
            Expect(File.ReadAllText(Path.Combine(R, "a.txt")) == "a" && File.ReadAllText(Path.Combine(R, "sub", "b.txt")) == "b" && File.Exists(Path.Combine(R, "old.txt")), "update →");

            Dialogs.ConfirmForTests = false;
            await view.QuickSyncAsync(SyncVariant.Mirror, true);
            Expect(File.Exists(Path.Combine(R, "old.txt")), "a refused mirror deleted");

            Dialogs.ConfirmForTests = true;
            await view.QuickSyncAsync(SyncVariant.Mirror, true);
            Expect(!File.Exists(Path.Combine(R, "old.txt")) && File.Exists(Path.Combine(R, "a.txt")), "mirror →");
        }
        finally
        {
            view?.Dispose();
            Dialogs.ConfirmForTests = confirmWas;
            s.CommanderLeft = wasL; s.CommanderRight = wasR; s.CommanderRestoreLast = wasRestore;
            try { AppSettings.Save(); } catch { }
        }
    }

    // ---- windows (built, never shown) ----

    /// <summary>Lays a view out at a window's size off screen and saves it as a PNG for a look at the layout.</summary>
    private static void Render(System.Windows.FrameworkElement view, string path)
    {
        const int w = 1100, h = 720;
        System.Windows.Documents.TextElement.SetForeground(view, (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["Fg"]);   // as the window gives it
        view.Measure(new System.Windows.Size(w, h));
        view.Arrange(new System.Windows.Rect(0, 0, w, h));
        view.UpdateLayout();
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        var bg = new System.Windows.Media.DrawingVisual();
        using (var dc = bg.RenderOpen()) dc.DrawRectangle((System.Windows.Media.Brush)System.Windows.Application.Current.Resources["Bg"], null, new System.Windows.Rect(0, 0, w, h));
        bmp.Render(bg);
        bmp.Render(view);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    private static async Task Windows(string root)
    {
        var L = Dir(root, "w-left"); var R = Dir(root, "w-right");
        W(L, "one.txt", "1", T0); W(L, "two.txt", "2", T0); W(R, "three.txt", "3", T0);
        var win = SyncWindow.FromPanes(FsRef.Local, L, FsRef.Local, R);
        var view = win.Build();
        Expect(win.PairsPanel.Children.Count == 1 && win.NameBox.Text == "w-left ⇄ w-right", "pairs and name");
        Expect(win.OptionsHost.Content is TabControl { Items.Count: 5 } tabs && tabs.Items.OfType<TabItem>().Select(t => t.Header as string).SequenceEqual(new[] { "Compare", "Synchronise", "Filter", "Deletion & versions", "Schedule & safety" }), "five option tabs");
        await win.CompareAsync();
        var rows = win.List.Items.OfType<SyncStep>().ToList();
        Expect(rows.Count == 3 && win.SyncButton.IsEnabled && win.Totals.Text.Length > 0, "compare in the window: " + Steps(rows) + " · " + win.Status.Text);
        Render(view, Path.Combine(Path.GetDirectoryName(root)!, "folder-sync-window.png"));
        var editor = SyncOptionsEditor.Build(new SyncOptions(), new SyncJobDef { Schedule = SyncSchedule.Weekly }, () => { }, () => { });
        for (int i = 1; i < editor.Items.Count; i++) { editor.SelectedIndex = i; Render(editor, Path.Combine(Path.GetDirectoryName(root)!, $"folder-sync-tab{i}.png")); }
        // A first Two-way run copies both ways (nothing to remember yet); the user can change a row.
        var three = rows.Single(s => s.Entry.Rel == "three.txt");
        Expect(three.Action == SyncAction.CopyLeft, "three.txt: " + three.Action);
        three.Action = SyncAction.DeleteRight;
        Expect(FolderSync.Totals(rows).Contains("DELETE 1", StringComparison.Ordinal), "changed row counted");

        // One click on "Mirror →" sets the job and compares again: three.txt (only on the right) is now deleted there.
        Expect(win.PresetButtons.Count == 6 && win.PresetButtons[0].IsChecked == true && win.Summary.Text.Length > 0, "presets: Two-way lit");
        var mirror = win.PresetButtons.Single(b => (string)b.Content == "Mirror  →");
        mirror.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        for (int i = 0; i < 100 && !win.List.Items.OfType<SyncStep>().Any(s => s.Entry.Rel == "three.txt" && s.Action == SyncAction.DeleteRight); i++) await Task.Delay(50);
        Expect(mirror.IsChecked == true && win.PresetButtons.Count(b => b.IsChecked == true) == 1 && win.Summary.Text.Contains("copy of the left", StringComparison.Ordinal)
               && win.List.Items.OfType<SyncStep>().Any(s => s.Entry.Rel == "three.txt" && s.Action == SyncAction.DeleteRight), "preset → preview: " + Steps(win.List.Items.OfType<SyncStep>()) + " · " + win.Summary.Text);
        Expect(!win.SaveJob() || SyncJobStore.All.Any(j => j.Name == "w-left ⇄ w-right"), "save");
        foreach (var j in SyncJobStore.All.Where(j => j.Name == "w-left ⇄ w-right")) SyncJobStore.Remove(j.Id);

        var pairOpts = new SyncOptions();
        Expect(SyncOptionsEditor.Build(pairOpts, null, () => { }, () => { }) is { Items.Count: 5 } pt && (pt.Items[4] as TabItem)?.Header as string == "Safety", "a pair's own settings: no schedule tab");
        var jobs = (SyncJobsWindow)Activator.CreateInstance(typeof(SyncJobsWindow), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null, new object?[] { null, null }, null)!;
        jobs.Build();
        Expect(jobs.List.ItemsSource != null, "jobs window");
        foreach (var k in SyncDatabase.KeyOf(FsRef.Local, L, FsRef.Local, R) is var key ? new[] { key } : Array.Empty<string>()) SyncDatabase.Forget(k);
    }
}
