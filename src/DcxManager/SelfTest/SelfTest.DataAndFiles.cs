using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DcxManager.Services;
using DcxManager.Ssh;
using DcxManager.Views;
using DcxManager.Vultr;
using DcxManager.Documents;

namespace DcxManager;

// SelfTest: snapshots, the store move, sync folders, the error log, the file commander and transfers (moved unchanged out of RunFullAsync in 0.2.0 phase 6½; the run's shared state is SelfTestRun).
public static partial class SelfTest
{
    private static async Task DataAndFilesAsync(SelfTestRun sharedRun)
    {
        var main = sharedRun.Main; var reportPath = sharedRun.ReportPath; Action<string> Ok = sharedRun.Ok; Action<string, Exception?> Fail = sharedRun.Fail; SelfTestRun.SettleFn Settle = sharedRun.Settle;

        // ADR-009: snapshots, restore, crash-safe commit, damaged-file recovery, schema migrations; updater switched off.
        try
        {
            var dir = SecureStore.DataDir;
            var snapDir = Snapshots.DefaultDir(dir);
            var st = AppSettings.Current;
            int keepWas = st.SnapshotKeep;

            // 0.6.1 (ADR-079): the pinned release key switches the updater on (outside the Store); checks stay manual by
            // default: nothing contacts the update source until you click, or opt into the automatic check.
            if (Updater.Enabled != (Updater.ReleaseKeyPinned && !Packaging.IsPackaged)) throw new Exception("updater should be on exactly when the release key is pinned (outside the Store)");
            var autoWas = st.AutoCheckUpdates; st.AutoCheckUpdates = false;
            try
            {
                var posture = PrivacyReport.PostureItems().FirstOrDefault(p => p.Name == "Update checks")?.Value;
                if (posture != (Updater.Enabled ? "MANUAL (you click Check)" : "DISABLED")) throw new Exception("privacy posture for updates: " + posture);
                if (!PrivacyReport.Endpoints().Any(e => e.Service == "Update service" && e.State.StartsWith(Updater.Enabled ? "only when you click" : "DISABLED"))) throw new Exception("update endpoint listing");
            }
            finally { st.AutoCheckUpdates = autoWas; }

            // start-up recorded the baseline format
            if (!File.Exists(Path.Combine(dir, StoreSchema.MetaFile)) || StoreSchema.VersionsOnDisk().Any(kv => kv.Value != 1)) throw new Exception("store.dcx baseline not recorded at start");

            // create + verify; encrypted at rest
            SecureStore.Write("reports.dcx", new List<DcxManager.Reports.ServerReport>());
            var s1 = Snapshots.Create("manual");
            if (!s1.Ok || s1.Path == null || !File.Exists(s1.Path) || !s1.Path.StartsWith(snapDir)) throw new Exception("create: " + s1.Message);
            var rawSnap = File.ReadAllBytes(s1.Path);
            if (Encoding.UTF8.GetString(rawSnap).Contains("selftest-host") || Encoding.UTF8.GetString(rawSnap).Contains("data.dcx")) throw new Exception("snapshot not sealed (plaintext names/content)");
            if (!Snapshots.Verify(s1.Path).Ok) throw new Exception("verify: " + Snapshots.Verify(s1.Path).Message);
            if (Snapshots.List().FirstOrDefault()?.Reason != "manual") throw new Exception("list");
            Ok("snapshots: sealed single file, read-back verified, restore drill passes, listed");

            // restore round trip (+ safety snapshot, freeze, plan)
            AppData.Current.Hosts.Add(new HostEntry { Name = "snap-probe", Address = "192.0.2.77", Username = "x" });
            AppData.Save();
            var plan = Snapshots.PlanRestore(Snapshots.Open(s1.Path));
            if (!plan.Replace.Contains("data.dcx") || plan.Replace.Contains("keyring.json")) throw new Exception("plan: " + Snapshots.Describe(plan));
            var rr = Snapshots.Restore(s1.Path);
            if (!rr.Ok || !SecureStore.Frozen) throw new Exception("restore: " + rr.Message);
            AppData.Save();   // frozen: must not overwrite the restored file
            if (SecureStore.Read<AppData>("data.dcx").Hosts.Any(h => h.Name == "snap-probe")) throw new Exception("restored data overwritten while frozen / not restored");
            if (!Snapshots.List().Any(e => e.Reason == "pre-restore")) throw new Exception("no pre-restore snapshot");
            SecureStore.Frozen = false;
            AppData.Load(); AppSettings.Load(); st = AppSettings.Current; Notes.NoteStore.Reset();
            if (AppData.Current.Hosts.Any(h => h.Name == "snap-probe") || AppData.Current.Hosts.All(h => h.Name != "selftest-host")) throw new Exception("data after restore");
            Ok("snapshot restore: plan (keyring never replaced), verified pre-restore snapshot, store frozen until restart, data back");

            // pruning: autos beyond the limit go; manual + safety ones stay
            st.SnapshotKeep = 2;
            for (int i = 0; i < 4; i++)
            {
                var ac = Snapshots.Create("auto");
                if (!ac.Ok || !File.Exists(ac.Path)) throw new Exception($"auto create {i}: {ac.Message} · keep {AppSettings.Current.SnapshotKeep} · " + string.Join(", ", Snapshots.List().Select(e => e.Name)));
            }
            if (Snapshots.List().Count(e => e.Reason == "auto") != 2 || !Snapshots.List().Any(e => e.Reason == "manual")) throw new Exception("prune: " + string.Join(",", Snapshots.List().Select(e => e.Reason)));
            st.SnapshotKeep = keepWas;
            if (Snapshots.AutoIfDue() != null) throw new Exception("auto snapshot taken although one is fresh");

            // low disk / failed write / damaged / foreign snapshots
            int count = Snapshots.List().Count;
            Snapshots.FreeSpace = _ => 1024;
            var low = Snapshots.Create("manual");
            Snapshots.FreeSpace = d => { try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(d))!).AvailableFreeSpace; } catch { return null; } };
            if (low.Ok || !low.Message.Contains("Not enough free space") || Snapshots.List().Count != count) throw new Exception("low disk: " + low.Message);
            var blocker = Path.Combine(Path.GetDirectoryName(reportPath)!, "snap-blocker");
            File.WriteAllText(blocker, "a file, not a folder");
            var failed = Snapshots.Create("manual", blocker);
            File.Delete(blocker);
            if (failed.Ok || Snapshots.List().Count != count) throw new Exception("failed write: " + failed.Message);
            var bad = Path.Combine(snapDir, "snap-20200101-000000-manual" + Snapshots.Ext);
            var b2 = File.ReadAllBytes(s1.Path); b2[^5] ^= 0x5A; File.WriteAllBytes(bad, b2);
            var dataBefore = File.ReadAllBytes(Path.Combine(dir, "data.dcx"));
            if (Snapshots.Verify(bad).Ok || Snapshots.Restore(bad).Ok || !File.ReadAllBytes(Path.Combine(dir, "data.dcx")).AsSpan().SequenceEqual(dataBefore)) throw new Exception("damaged snapshot accepted");
            File.WriteAllBytes(bad, SecureStore.Seal(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32), Encoding.UTF8.GetBytes("{}"), "snapshot"));
            if (!Snapshots.Verify(bad).Message.Contains("different store")) throw new Exception("foreign snapshot");
            File.Delete(bad);
            Ok("snapshots: low disk writes nothing, failed write keeps older ones, damaged and foreign-key snapshots refused with data untouched");

            // crash-safe commit: staged without journal → discarded; journal written then crash → completed at next start
            var settingsPath = Path.Combine(dir, "settings.dcx");
            var liveSettings = File.ReadAllBytes(settingsPath);
            File.WriteAllBytes(settingsPath + Snapshots.StagedSuffix, new byte[] { 1, 2, 3 });
            Snapshots.FinishPending();
            if (File.Exists(settingsPath + Snapshots.StagedSuffix) || !File.ReadAllBytes(settingsPath).AsSpan().SequenceEqual(liveSettings)) throw new Exception("orphan staged file");
            var probeSettings = SecureStore.Read<AppSettings>("settings.dcx");
            probeSettings.Scrollback = 4242;
            var newBytes = SecureStore.Encrypt(JsonSerializer.SerializeToUtf8Bytes(probeSettings, SecureStore.Json), "settings.dcx");
            Snapshots.AfterJournal = () => throw new IOException("simulated crash");
            try { Snapshots.Commit(new Dictionary<string, byte[]> { ["settings.dcx"] = newBytes }, Array.Empty<string>(), "self-test commit"); throw new Exception("crash hook did not fire"); }
            catch (IOException) { }
            finally { Snapshots.AfterJournal = null; }
            if (!File.ReadAllBytes(settingsPath).AsSpan().SequenceEqual(liveSettings) || !File.Exists(Path.Combine(dir, Snapshots.JournalFile))) throw new Exception("crash state");
            var done = Snapshots.FinishPending();
            if (done == null || SecureStore.Read<AppSettings>("settings.dcx").Scrollback != 4242 || File.Exists(Path.Combine(dir, Snapshots.JournalFile))) throw new Exception("roll-forward: " + done);
            File.WriteAllText(Path.Combine(dir, Snapshots.JournalFile), "{\"Replace\":[\"..\\\\evil.dcx\"]}");
            if (Snapshots.FinishPending()?.Contains("set aside") != true || File.Exists(Path.Combine(dir, Snapshots.JournalFile))) throw new Exception("hostile journal");
            foreach (var f in Directory.EnumerateFiles(dir, Snapshots.JournalFile + ".rejected-*")) File.Delete(f);
            SecureStore.Write("settings.dcx", st);
            Ok("journaled commit: orphan staged files discarded, interrupted commit completed at start, hostile journal set aside");

            // damaged store files at start: restore one file from the newest good snapshot, or set it aside
            var rep = Path.Combine(dir, "reports.dcx");
            var rb = File.ReadAllBytes(rep); rb[^1] ^= 0xFF; File.WriteAllBytes(rep, rb);
            var dmg = StoreRecovery.Check();
            if (dmg.Count != 1 || dmg[0].Name != "reports.dcx" || dmg[0].GoodCopy == null) throw new Exception("damage check: " + string.Join(";", dmg.Select(d => d.Name + " " + d.GoodCopy)));
            StoreRecovery.RestoreFile("reports.dcx", dmg[0].GoodCopy!);
            if (StoreRecovery.Check().Count != 0 || !Directory.EnumerateFiles(dir, "reports.dcx.damaged-*").Any()) throw new Exception("file recovery");
            SecureStore.Write("selftest-extra.dcx", new { a = 1 });
            var xb = File.ReadAllBytes(Path.Combine(dir, "selftest-extra.dcx")); xb[^1] ^= 0xFF; File.WriteAllBytes(Path.Combine(dir, "selftest-extra.dcx"), xb);
            var asked = new List<string>();
            if (!StoreRecovery.RunAtStart((t, m, o) => { asked.Add(t); return Array.IndexOf(o, "Start without it"); }) || asked.Count != 1 || File.Exists(Path.Combine(dir, "selftest-extra.dcx"))) throw new Exception("set aside via start-up flow: " + string.Join(",", asked));
            foreach (var f in Directory.EnumerateFiles(dir, "*.damaged-*")) File.Delete(f);
            st = AppSettings.Current;
            Ok("damaged files at start: restored from the newest snapshot with a good copy (damaged file kept), else set aside on request");

            // migrations: verified pre-migration snapshot, transform, verify; failure before commit changes nothing; newer store refused
            int scroll = st.Scrollback;
            StoreSchema.AddTestMigration(new("settings", 1, "self-test: scrollback", n => { n["Scrollback"] = 12345; return n; }));
            var m1 = StoreSchema.Migrate();
            if (m1.Outcome != StoreSchema.Outcome.Migrated || SecureStore.Read<AppSettings>("settings.dcx").Scrollback != 12345 || StoreSchema.VersionsOnDisk()["settings"] != 2) throw new Exception("migrate: " + m1.Message);
            var pre = Snapshots.List().FirstOrDefault(e => e.Reason == "pre-migration");
            if (pre == null || !Snapshots.Verify(pre.Path).Ok || Snapshots.Open(pre.Path).Schema["settings"] != 1) throw new Exception("pre-migration snapshot");
            if (StoreSchema.Migrate().Outcome != StoreSchema.Outcome.UpToDate) throw new Exception("second run not up to date");
            StoreSchema.AddTestMigration(new("settings", 2, "self-test: broken", n => { n["Scrollback"] = "not a number"; return n; }));
            var m2 = StoreSchema.Migrate();
            if (m2.Outcome != StoreSchema.Outcome.Failed || !m2.Message.Contains("Nothing was changed") || StoreSchema.VersionsOnDisk()["settings"] != 2 || SecureStore.Read<AppSettings>("settings.dcx").Scrollback != 12345) throw new Exception("failed migration: " + m2.Message);
            StoreSchema.ClearTestMigrations();
            if (StoreSchema.Migrate().Outcome != StoreSchema.Outcome.NewerStore) throw new Exception("newer store not refused");
            var askedNewer = new List<string>();
            if (StoreRecovery.RunAtStart((t, _, _) => { askedNewer.Add(t); return 0; }) || !askedNewer.Contains("Newer data format")) throw new Exception("start-up did not refuse a newer store");
            var newerSnap = Snapshots.Create("manual");
            if (newerSnap.Ok && Snapshots.Verify(newerSnap.Path!).Ok) throw new Exception("snapshot of a newer format offered for restore");
            if (newerSnap.Path != null) File.Delete(newerSnap.Path);
            // back to the baseline for the rest of the run
            SecureStore.Write(StoreSchema.MetaFile, new StoreSchema.Meta { Versions = StoreSchema.LatestAll(), WrittenBy = App.Version, Updated = DateTime.Now });
            AppSettings.Load(); AppSettings.Current.Scrollback = scroll; AppSettings.Save();
            if (StoreSchema.Migrate().Outcome != StoreSchema.Outcome.UpToDate) throw new Exception("baseline reset");
            // the test-only "settings v2" pre-migration snapshots are (correctly) unrestorable now: remove them
            foreach (var e in Snapshots.List().Where(e => e.Reason == "pre-migration")) File.Delete(e.Path);
            if (Snapshots.List().Any(e => !Snapshots.Verify(e.Path).Ok)) throw new Exception("leftover unverifiable snapshot");
            Ok("migrations: verified pre-migration snapshot, JSON transform + verify, idempotent, broken step changes nothing, newer format refused at start and for restore");
        }
        catch (Exception ex) { Fail("snapshots / migrations", ex); SecureStore.Frozen = false; Snapshots.AfterJournal = null; StoreSchema.ClearTestMigrations(); }

        // Data store move: pre-flight, copy+verify+switch into a scratch folder, then back.
        try
        {
            var orig = SecureStore.DataDir;
            var moved = Path.Combine(Path.GetDirectoryName(reportPath)!, "moved-store");
            if (Directory.Exists(moved)) Directory.Delete(moved, true);
            var bad = DataMover.Preflight(Path.Combine(orig, "inside")).Where(c => !c.Ok).ToList();
            if (bad.Count == 0) throw new Exception("pre-flight accepted a nested target");
            var pre = DataMover.Preflight(moved);
            if (pre.Any(c => !c.Ok)) throw new Exception("pre-flight: " + string.Join("; ", pre.Where(c => !c.Ok).Select(c => c.Text)));
            int snaps = DataMover.SnapshotFiles(orig).Count();
            if (snaps == 0) throw new Exception("expected snapshots from the snapshot tests");
            // A session log (ADR-019) must travel too.
            string slog;
            using (var lg = new Sessions.SessionLogger("move · test", timestamps: false)) { lg.Write("travels with the store\r\n"); slog = Path.GetFileName(lg.Path); }
            DataMover.Move(moved);
            if (Sessions.SessionLog.List().FirstOrDefault(e => Path.GetFileName(e.Path) == slog) is not { } movedLog || !Sessions.SessionLog.Read(movedLog.Path).Contains("travels with the store"))
                throw new Exception("session log did not move with the store");
            if (!SecureStore.DataDir.Equals(moved, StringComparison.OrdinalIgnoreCase)) throw new Exception("DataDir not switched");
            AppData.Load();
            if (AppData.Current.Hosts.All(h => h.Name != "selftest-host")) throw new Exception("data unreadable after move");
            if (DataMover.SnapshotFiles(moved).Count() != snaps || Snapshots.List().Count != snaps || !Snapshots.Verify(Snapshots.List()[0].Path).Ok) throw new Exception("snapshots did not move with the store");
            if (DataMover.RemoveOldCopy(orig) == 0) throw new Exception("old copy had nothing to remove");
            if (Directory.Exists(Snapshots.DefaultDir(orig))) throw new Exception("old snapshots left behind");
            if (DataMover.SessionLogFiles(orig).Any()) throw new Exception("old session logs left behind");
            DataMover.Move(orig);
            if (!SecureStore.DataDir.Equals(orig, StringComparison.OrdinalIgnoreCase)) throw new Exception("DataDir not switched back");
            DataMover.RemoveOldCopy(moved);
            if (DataMover.SnapshotFiles(orig).Count() != snaps) throw new Exception("snapshots did not move back");
            var back = Sessions.SessionLog.List().FirstOrDefault(e => Path.GetFileName(e.Path) == slog) ?? throw new Exception("session log did not move back");
            Sessions.SessionLog.Delete(back.Path);
            AppData.Load();
            if (File.Exists(Path.Combine(SecureStore.HomeDir, "location.json")) && Environment.GetEnvironmentVariable("DCX_DATA_DIR") is { Length: > 0 })
            {
                var ptr = File.ReadAllText(Path.Combine(SecureStore.HomeDir, "location.json"));
                if (ptr.Contains(moved, StringComparison.OrdinalIgnoreCase) || ptr.Contains(orig, StringComparison.OrdinalIgnoreCase)) throw new Exception("self-test wrote the REAL location.json pointer");
            }
            Ok("data store move: pre-flight rejects nested target, move + verify + switch (snapshots and session logs travel along), move back, pointer untouched");
        }
        catch (Exception ex) { Fail("data store move", ex); }

        // RDP file: never a password, sane fields.
        try
        {
            var h = new HostEntry { Name = "win-01", Address = "203.0.113.30", Port = 3390, Username = "admin", Protocol = "rdp", Rdp = new RdpSettings { FullScreen = false, Width = 1280, Height = 800, Domain = "CORP" } };
            var rdp = RdpLauncher.RdpFile(h);
            if (!rdp.Contains("full address:s:203.0.113.30:3390") || !rdp.Contains("username:s:CORP\\admin") || !rdp.Contains("desktopwidth:i:1280") || !rdp.Contains("autoreconnection enabled:i:1")) throw new Exception("fields");
            if (rdp.Contains("password", StringComparison.OrdinalIgnoreCase)) throw new Exception("password field present");
            Ok("RDP file: address/port/domain user/resolution/auto-reconnect, no password");
        }
        catch (Exception ex) { Fail("RDP file", ex); }

        // Sync-folder detection: explicit roots, nesting, siblings with a shared prefix, junctions into a root, UNC paths.
        try
        {
            var baseDir = Path.Combine(Path.GetDirectoryName(reportPath)!, "sync-test");
            if (Directory.Exists(baseDir)) { foreach (var d in Directory.GetDirectories(baseDir)) if (new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint)) Directory.Delete(d); Directory.Delete(baseDir, true); }
            var fakeRoot = Path.Combine(baseDir, "OneDrive - Fake");
            Directory.CreateDirectory(Path.Combine(fakeRoot, "Apps"));
            Directory.CreateDirectory(Path.Combine(baseDir, "OneDrive - Fake2"));   // shares the prefix, is NOT inside the root
            var roots = new[] { new SyncDetector.Root(SyncDetector.Normalize(fakeRoot), "OneDrive (Personal)", "selftest") };
            var inside = SyncDetector.Detect(Path.Combine(fakeRoot, "Apps", "not-created-yet"), roots);
            if (inside.Kind != StorageKind.Synced || inside.Provider != "OneDrive (Personal)") throw new Exception("nested path not detected: " + inside.Kind);
            if (SyncDetector.Detect(Path.Combine(baseDir, "OneDrive - Fake2"), roots).Kind != StorageKind.Local) throw new Exception("prefix sibling flagged as synced");
            if (SyncDetector.Detect(Path.Combine(baseDir, "plain"), roots).Kind != StorageKind.Local) throw new Exception("plain folder flagged");
            if (SyncDetector.Detect(@"\\files.example.test\share\dcx", roots) is not { Kind: StorageKind.Network, Root: @"\\files.example.test\share" }) throw new Exception("UNC not reported as network");
            // A junction elsewhere that points INTO the sync root must still count (Documents junctions, symlinked AppData…).
            var link = Path.Combine(baseDir, "local-looking");
            var mk = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{Path.Combine(fakeRoot, "Apps")}\"") { CreateNoWindow = true, UseShellExecute = false });
            mk?.WaitForExit(5000);
            string junction = "junction not created (skipped)";
            if (Directory.Exists(link))
            {
                if (SyncDetector.Detect(Path.Combine(link, "store"), roots).Kind != StorageKind.Synced) throw new Exception("junction into sync root not followed");
                Directory.Delete(link);
                junction = "junction followed";
            }
            Directory.Delete(baseDir, true);
            var real = SyncDetector.KnownRoots();
            var here = SyncDetector.Detect(SecureStore.DataDir);
            Ok($"sync detection: nested/prefix/UNC correct, {junction}; this PC has {real.Count} sync root(s) [{string.Join(", ", real.Select(r => r.Provider).Distinct())}]; scratch store is {here.Kind}");
        }
        catch (Exception ex) { Fail("sync detection", ex); }

        // Error log: sealed on disk once unlocked, readable through the in-app viewer, kept inside the scratch folder.
        try
        {
            if (!App.LogDir.StartsWith(Environment.GetEnvironmentVariable("DCX_DATA_DIR")!, StringComparison.OrdinalIgnoreCase)) throw new Exception("log dir escaped the scratch folder: " + App.LogDir);
            var marker = "selftest-log-marker-" + Guid.NewGuid().ToString("N")[..8];
            var file = App.Log("selftest", new InvalidOperationException(marker + " host 203.0.113.77"));
            if (File.ReadAllText(file).Contains(marker)) throw new Exception("error log written in clear");
            if (!App.ReadLogs(1).Contains(marker)) throw new Exception("viewer can't decrypt the entry");
            Ok("error log: entry sealed on disk, decrypted by the viewer, stays in the scratch folder");
        }
        catch (Exception ex) { Fail("error log", ex); }

        // File commander back end: local copy of a tree with checksums, conflict policies, same-volume move, secret guard.
        try
        {
            var root = Path.Combine(Path.GetDirectoryName(reportPath)!, "commander-test");
            if (Directory.Exists(root)) Directory.Delete(root, true);
            var src = Path.Combine(root, "src"); var dst = Path.Combine(root, "dst");
            Directory.CreateDirectory(Path.Combine(src, "tree", "sub")); Directory.CreateDirectory(dst);
            File.WriteAllText(Path.Combine(src, "tree", "a.txt"), "hello commander");
            var blob = System.Security.Cryptography.RandomNumberGenerator.GetBytes(3 * 1024 * 1024 + 17);   // spans many 256 KB buffers
            File.WriteAllBytes(Path.Combine(src, "tree", "sub", "b.bin"), blob);
            using var lfs = new Files.LocalFs();
            var tree = (await lfs.ListAsync(src)).Single(i => i.Name == "tree");
            async Task<Files.TransferJob> Run(Files.TransferJob j) { Files.TransferQueue.Enqueue(j); await j.Done.Task.WaitAsync(TimeSpan.FromSeconds(60)); return j; }
            var j1 = await Run(new Files.TransferJob { From = Files.FsRef.Local, To = Files.FsRef.Local, Items = new[] { tree }, DestDir = dst });
            if (j1.Error != null) throw new Exception("copy: " + j1.Error);
            var copied = File.ReadAllBytes(Path.Combine(dst, "tree", "sub", "b.bin"));
            if (!copied.AsSpan().SequenceEqual(blob) || File.ReadAllText(Path.Combine(dst, "tree", "a.txt")) != "hello commander") throw new Exception("copied content differs");
            var j2 = await Run(new Files.TransferJob { From = Files.FsRef.Local, To = Files.FsRef.Local, Items = new[] { tree }, DestDir = dst, OnConflict = Files.Conflict.Skip });
            if (j2.Skipped != 2 || j2.Files != 0) throw new Exception($"skip policy: {j2.Files} copied, {j2.Skipped} skipped");
            var j3 = await Run(new Files.TransferJob { From = Files.FsRef.Local, To = Files.FsRef.Local, Items = new[] { tree }, DestDir = dst, OnConflict = Files.Conflict.Rename });
            if (!File.Exists(Path.Combine(dst, "tree", "a (1).txt"))) throw new Exception("rename policy didn't keep both");
            var a = (await lfs.ListAsync(Path.Combine(src, "tree"))).Single(i => i.Name == "a.txt");
            var j4 = await Run(new Files.TransferJob { From = Files.FsRef.Local, To = Files.FsRef.Local, Items = new[] { a }, DestDir = Path.Combine(dst, "tree", "sub"), Move = true });
            if (j4.Error != null || File.Exists(a.FullPath) || !File.Exists(Path.Combine(dst, "tree", "sub", "a.txt"))) throw new Exception("move didn't move");
            if (lfs.Parent(@"C:\") != "" || lfs.Parent(Path.Combine(src, "tree")) != src) throw new Exception("local Parent()");
            var sftpPaths = new Files.SftpFs(new HostEntry { Name = "x", Address = "203.0.113.9" });
            if (sftpPaths.Parent("/var/log/") != "/var" || sftpPaths.Parent("/var") != "/" || sftpPaths.Join("/", "etc") != "/etc") throw new Exception("sftp path helpers");
            foreach (var p in new[] { "/home/a/.ssh/id_ed25519", "/etc/shadow", @"C:\Users\x\.aws\credentials", "/srv/app/.env", "/etc/ssl/private/site.key", "/root/.kube/config" })
                if (!Ai.Assistant.LooksSecret(p)) throw new Exception("secret path not caught: " + p);
            foreach (var p in new[] { "/var/log/nginx/error.log", "/etc/nginx/nginx.conf", @"C:\logs\app.txt", "/home/a/keys-notes.md" })
                if (Ai.Assistant.LooksSecret(p)) throw new Exception("false positive: " + p);
            AppSettings.Current.CommanderLeft = "local|" + src; AppSettings.Current.CommanderRight = "local|" + dst;
            Ok($"file commander: tree copy byte-exact ({Fmt.Bytes(blob.Length)}), skip/keep-both policies, move, path helpers, AI secret-path guard");
        }
        catch (Exception ex) { Fail("file commander", ex); }

        // ADR-014 transfers: part file + atomic replace, SHA-256 verify, resume after an injected fault, retries, retry-later,
        // stale partial detected, pause, kept times, encrypted history, part files never travel; folder compare/sync plans
        // and a real mirror run; SCP stat parser; SFTP→SCP fallback detection.
        var backoffWas = Files.TransferQueue.Backoff;
        try
        {
            var root = Path.Combine(Path.GetDirectoryName(reportPath)!, "transfer-test");
            if (Directory.Exists(root)) Directory.Delete(root, true);
            string D(string n) { var d = Path.Combine(root, n); Directory.CreateDirectory(d); return d; }
            var src = D("src");
            var bigBytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(6 * 1024 * 1024 + 123);
            var bigPath = Path.Combine(src, "big.bin");
            File.WriteAllBytes(bigPath, bigBytes);
            var stamp = new DateTime(2020, 5, 5, 10, 0, 0);
            File.SetLastWriteTime(bigPath, stamp);
            File.WriteAllText(Path.Combine(src, "stray.bin" + Files.TransferQueue.PartSuffix), "never copied");
            using var lfs = new Files.LocalFs();
            var big = (await lfs.ListAsync(src)).Single(i => i.Name == "big.bin");
            Files.TransferQueue.Backoff = new[] { TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(150) };
            async Task<Files.TransferJob> Run(Files.TransferJob j) { Files.TransferQueue.Enqueue(j); await j.Done.Task.WaitAsync(TimeSpan.FromSeconds(90)); return j; }
            bool Same(string p) => File.Exists(p) && File.ReadAllBytes(p).AsSpan().SequenceEqual(bigBytes);

            // 1) one fault mid-file → retry on a fresh connection resumes the part file → verified
            int faults = 0;
            Files.TransferQueue.Fault = (name, n) => name == "big.bin" && n > 2 * 1024 * 1024 && faults++ == 0;
            var d1 = D("d1");
            var j1 = await Run(new Files.TransferJob { From = Files.FsRef.Local, To = Files.FsRef.Local, Items = new[] { big }, DestDir = d1, Verify = true, KeepTimes = true });
            var t1 = Path.Combine(d1, "big.bin");
            if (j1.Error != null || j1.Retries != 1 || j1.Resumed < 1 || j1.Verified != 1 || !Same(t1) || File.Exists(t1 + Files.TransferQueue.PartSuffix)) throw new Exception($"resume after fault: {j1.Summary}");
            if ((File.GetLastWriteTime(t1) - stamp).Duration() > TimeSpan.FromSeconds(2)) throw new Exception("modified time not kept");

            // 2) permanent fault → fails after the retries with the part file kept (never the real name) → Retry resumes it
            Files.TransferQueue.Fault = (name, n) => name == "big.bin" && n > 1024 * 1024;
            var d2 = D("d2");
            var j2 = await Run(new Files.TransferJob { From = Files.FsRef.Local, To = Files.FsRef.Local, Items = new[] { big }, DestDir = d2, Verify = true });
            var t2 = Path.Combine(d2, "big.bin");
            if (j2.Error == null || j2.Retries != 3 || File.Exists(t2) || !File.Exists(t2 + Files.TransferQueue.PartSuffix)) throw new Exception($"permanent fault: {j2.Summary} · target exists {File.Exists(t2)}");
            Files.TransferQueue.Fault = null;
            var j2b = Files.TransferQueue.Retry(j2);
            await j2b.Done.Task.WaitAsync(TimeSpan.FromSeconds(90));
            if (j2b.Error != null || j2b.Resumed < 1 || !Same(t2) || j2b.RetryOf != j2) throw new Exception($"retry later: {j2b.Summary}");

            // 3) a stale part file (different bytes) is detected by SHA-256 and the file is copied again from zero
            var d3 = D("d3");
            File.WriteAllBytes(Path.Combine(d3, "big.bin" + Files.TransferQueue.PartSuffix), System.Security.Cryptography.RandomNumberGenerator.GetBytes(1024 * 1024));
            var j3 = await Run(new Files.TransferJob { From = Files.FsRef.Local, To = Files.FsRef.Local, Items = new[] { big }, DestDir = d3, Verify = true });
            if (j3.Error != null || !Same(Path.Combine(d3, "big.bin")) || !j3.Warnings.Any(w => w.Contains("didn't match"))) throw new Exception($"stale partial: {j3.Summary} · {string.Join(";", j3.Warnings)}");

            // 4) pause holds the job before any byte moves; resume finishes it
            var d4 = D("d4");
            var j4 = new Files.TransferJob { From = Files.FsRef.Local, To = Files.FsRef.Local, Items = new[] { big }, DestDir = d4 };
            j4.Pause();
            Files.TransferQueue.Enqueue(j4);
            await Task.Delay(700);
            if (j4.Finished || j4.Bytes != 0 || File.Exists(Path.Combine(d4, "big.bin"))) throw new Exception("pause did not hold");
            j4.Resume();
            await j4.Done.Task.WaitAsync(TimeSpan.FromSeconds(90));
            if (j4.Error != null || !Same(Path.Combine(d4, "big.bin"))) throw new Exception("after resume: " + j4.Summary);

            // 5) part files never travel; history is kept, encrypted
            var srcDir = (await lfs.ListAsync(root)).Single(i => i.Name == "src");
            var d5 = D("d5");
            var j5 = await Run(new Files.TransferJob { From = Files.FsRef.Local, To = Files.FsRef.Local, Items = new[] { srcDir }, DestDir = d5 });
            if (j5.Error != null || File.Exists(Path.Combine(d5, "src", "stray.bin" + Files.TransferQueue.PartSuffix))) throw new Exception("a .dcxpart file was copied");
            Files.TransferHistory.Reset();
            var hist = Files.TransferHistory.All;
            if (!hist.Any(h => h.Retries == 3 && h.Result != "ok") || !hist.Any(h => h.Resumed > 0 && h.Result == "ok")) throw new Exception("history: " + hist.Count);
            if (Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(SecureStore.DataDir, "transfers.dcx"))).Contains("big.bin")) throw new Exception("history in plaintext");
            Ok($"transfers: .dcxpart + atomic replace, SHA-256 verified, injected fault → retry resumes ({Fmt.Bytes(bigBytes.Length)}), permanent fault → failed with part kept → Retry resumes, stale part detected and recopied, pause holds/resumes, modified time kept, part files never copied, encrypted history");

            // 6) compare / sync
            var L = D("left"); var R = D("right");
            void W(string dir, string rel, string text, DateTime t) { var p = Path.Combine(dir, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, text); File.SetLastWriteTime(p, t); }
            var t0 = new DateTime(2024, 1, 1, 12, 0, 0);
            W(L, "same.txt", "same", t0); W(R, "same.txt", "same", t0);
            W(L, "changed.txt", "new content", t0.AddHours(2)); W(R, "changed.txt", "old", t0);
            W(L, "onlyL.txt", "left", t0); W(L, Path.Combine("dirL", "inner.txt"), "inner", t0);
            W(R, "onlyR.txt", "right", t0);
            W(L, "tc", "file here", t0); W(R, Path.Combine("tc", "x"), "folder there", t0);
            var diffs = await Files.FolderSync.CompareAsync(lfs, L, lfs, R, checksum: false);
            Files.DiffState St(string rel) => diffs.Single(d => d.Rel == rel).State;
            if (St("same.txt") != Files.DiffState.Same || St("changed.txt") != Files.DiffState.LeftNewer || St("onlyL.txt") != Files.DiffState.OnlyLeft || St("dirL") != Files.DiffState.OnlyLeft || St("onlyR.txt") != Files.DiffState.OnlyRight || St("tc") != Files.DiffState.TypeConflict)
                throw new Exception("compare: " + string.Join(", ", diffs.Select(d => d.Rel + "=" + d.State)));
            var byHash = await Files.FolderSync.CompareAsync(lfs, L, lfs, R, checksum: true);
            if (byHash.Single(d => d.Rel == "same.txt").State != Files.DiffState.Same) throw new Exception("checksum compare");
            var upd = Files.FolderSync.Plan(diffs, Files.SyncMode.Update, true, deleteExtras: true);
            if (upd.Any(s => s.Action is Files.SyncAction.DeleteRight or Files.SyncAction.DeleteLeft) || upd.Count(s => s.Action == Files.SyncAction.CopyRight) != 3 || upd.Single(s => s.Entry.Rel == "tc").Selected) throw new Exception("update plan");
            var two = Files.FolderSync.Plan(diffs, Files.SyncMode.TwoWay, true, deleteExtras: true);
            if (two.Single(s => s.Entry.Rel == "onlyR.txt").Action != Files.SyncAction.CopyLeft || two.Any(s => s.Action is Files.SyncAction.DeleteRight or Files.SyncAction.DeleteLeft)) throw new Exception("two-way plan");
            var noDel = Files.FolderSync.Plan(diffs, Files.SyncMode.Mirror, true, deleteExtras: false);
            if (noDel.Any(s => s.Action == Files.SyncAction.DeleteRight)) throw new Exception("mirror deleted without the box ticked");
            var mirror = Files.FolderSync.Plan(diffs, Files.SyncMode.Mirror, true, deleteExtras: true);
            if (mirror.Single(s => s.Entry.Rel == "onlyR.txt").Action != Files.SyncAction.DeleteRight) throw new Exception("mirror plan");
            Files.LocalFs.HardDeleteForTests = true;
            try
            {
                var jobs = await Files.FolderSync.BuildJobsAsync(mirror, Files.FsRef.Local, L, Files.FsRef.Local, R);
                if (jobs.Count != 1) throw new Exception("jobs " + jobs.Count);
                var sj = await Run(jobs[0]);
                if (sj.Error != null || sj.Deleted != 1) throw new Exception("sync run: " + sj.Summary);
            }
            finally { Files.LocalFs.HardDeleteForTests = false; }
            var after = await Files.FolderSync.CompareAsync(lfs, L, lfs, R, checksum: true);
            if (after.Any(d => d.State != Files.DiffState.Same && d.Rel != "tc") || File.Exists(Path.Combine(R, "onlyR.txt")) || File.ReadAllText(Path.Combine(R, "dirL", "inner.txt")) != "inner")
                throw new Exception("after mirror: " + string.Join(", ", after.Where(d => d.State != Files.DiffState.Same).Select(d => d.Rel + "=" + d.State)));
            Ok("folder sync: compare by size+time and by SHA-256 (same / newer / only-left / only-right / type conflict), Update never deletes and skips a newer target, Two-way copies both ways, Mirror deletes only with the box ticked, real mirror run (copies then 1 delete) leaves both sides identical except the type conflict");

            // 7) SCP: stat parsing (GNU/BusyBox lines, '|' in names, bad lines) and the SFTP → SCP switch condition
            var g = Files.ScpFs.ParseStat("regular file|12|1700000000|644|1000|1000|a|b.txt", "/srv");
            var dd = Files.ScpFs.ParseStat("directory|4096|1700000000|755|0|0|logs", "/var/");
            var ln = Files.ScpFs.ParseStat("symbolic link|7|1700000000|777|0|0|current", "/opt");
            var em = Files.ScpFs.ParseStat("regular empty file|0|1700000000|600|33|33|.env", "/app");
            if (g is not { Name: "a|b.txt", Length: 12, Mode: 0x1A4, FullPath: "/srv/a|b.txt" } || dd is not { IsDir: true, FullPath: "/var/logs", Length: 0 } || ln is not { IsLink: true } || em is not { Name: ".env", Mode: 0x180 } || g.Perms != "-rw-r--r--")
                throw new Exception("scp stat parse");
            if (Files.ScpFs.ParseStat("garbage", "/") != null || Files.ScpFs.ParseStat("regular file|x|y|644|0|0|n", "/") != null) throw new Exception("scp stat garbage accepted");
            if (!Files.HostFs.NoSftp(new Renci.SshNet.Common.SshException("Subsystem 'sftp' could not be executed.")) || Files.HostFs.NoSftp(new IOException("Permission denied"))) throw new Exception("sftp fallback detection");
            if (new Files.HostFs(new HostEntry { Name = "p", Address = "203.0.113.5", FileProtocol = "scp" }).Title != "p · SCP") throw new Exception("scp host title");
            Ok("SCP fallback: stat -c lines parsed (GNU/BusyBox, '|' in names, modes, links, empty files), garbage refused; 'subsystem' failure switches SFTP → SCP; SCP hosts labelled");
            Directory.Delete(root, true);
        }
        catch (Exception ex) { Fail("file transfer", ex); }
        finally { Files.TransferQueue.Backoff = backoffWas; Files.TransferQueue.Fault = null; Files.LocalFs.HardDeleteForTests = false; }
    }
}
