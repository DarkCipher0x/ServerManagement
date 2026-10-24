using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DcxManager.Services;

/// <summary>
/// Rolling encrypted snapshots of the store (ADR-009). A snapshot is ONE file holding the raw — already encrypted — bytes
/// of every store file plus their SHA-256, the whole payload sealed again with the data key (AAD "snapshot"). So a
/// snapshot is as private as the store itself, and useless with another data key: it is a local safety net, not a
/// portable backup (that is the password-protected .dcxbackup export).
///
/// Rules: a snapshot only counts once it was written, read back and verified; old automatic snapshots are pruned only
/// after a newer one verified; manual snapshots are never pruned; nothing is written when the disk is low.
/// Restores and migrations go through <see cref="Commit"/>: new files are staged next to their targets and verified,
/// a journal is written atomically, then the files are swapped in. A crash at any point leaves either the old files
/// (no journal yet: the staged copies are discarded at the next start) or a journal that the next start completes.
/// </summary>
public static class Snapshots
{
    public const string Ext = ".dcxsnap";
    public const string JournalFile = "pending-commit.json";
    public const string StagedSuffix = ".staged";
    public const long MaxTotalBytes = 512L << 20;
    /// <summary>Safety snapshots (pre-restore, pre-migration, pre-import, pre-upgrade) kept per reason.</summary>
    public const int SafetyKeep = 5;

    public sealed class FileItem
    {
        public string Name { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public byte[] Data { get; set; } = Array.Empty<byte>();
    }

    public sealed class Payload
    {
        public int Version { get; set; } = 1;
        public DateTime Created { get; set; }
        public string Reason { get; set; } = "";
        public string AppVersion { get; set; } = "";
        public Dictionary<string, int> Schema { get; set; } = new();
        public List<FileItem> Files { get; set; } = new();
    }

    public sealed record Entry(string Path, DateTime Created, string Reason, long Bytes)
    {
        public string Name => System.IO.Path.GetFileName(Path);
        public string ReasonText => Reason switch
        {
            "auto" => "automatic", "manual" => "manual", "pre-restore" => "before a restore", "pre-migration" => "before a data upgrade",
            "pre-import" => "before an import", "pre-upgrade" => "before an app update", _ => Reason,
        };
    }

    public sealed record Result(bool Ok, string Message, string? Path = null);

    public static string DefaultDir(string dataDir) => System.IO.Path.Combine(dataDir, "snapshots");

    /// <summary>The configured snapshot folder (Settings → Snapshots), else "snapshots" inside the store.</summary>
    public static string Dir => AppSettings.Current.SnapshotDir is { Length: > 0 } d ? d : DefaultDir(SecureStore.DataDir);

    public static bool IsDefaultDir => AppSettings.Current.SnapshotDir is not { Length: > 0 };

    /// <summary>The files a snapshot holds: exactly what the store mover moves (keyring, *.dcx, audit logs).</summary>
    public static List<string> StoreFileNames(string dataDir) => DataMover.StoreFiles(dataDir).Select(f => System.IO.Path.GetFileName(f)!).ToList();

    /// <summary>Test hook: free space for a folder (null = unknown, e.g. a UNC path).</summary>
    internal static Func<string, long?> FreeSpace = DefaultFreeSpace;

    private static long? DefaultFreeSpace(string dir)
    {
        try
        {
            var root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(dir));
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\")) return null;
            var d = new DriveInfo(root);
            return d.IsReady ? d.AvailableFreeSpace : null;
        }
        catch { return null; }
    }

    private static readonly object CreateGate = new();

    // ---- create / list / prune ---------------------------------------------------------------------------------------

    /// <summary>A snapshot could not be made: the reason it was taken, and why it failed (notifications, 0.3.0 §4).</summary>
    public static event Action<string, string>? Failed;

    public static Result Create(string reason, string? dir = null)
    {
        var r = CreateCore(reason, dir);
        if (!r.Ok) try { Failed?.Invoke(reason, r.Message); } catch (Exception ex) { Log.Write("snapshot failed event", ex); }
        return r;
    }

    private static Result CreateCore(string reason, string? dir)
    {
        lock (CreateGate)
        {
            dir ??= Dir;
            string? tmp = null;
            try
            {
                if (!SecureStore.IsUnlocked) return new(false, "The store is locked.");
                if (!Regex.IsMatch(reason, "^[a-z]+(-[a-z]+)?$")) throw new ArgumentException("bad reason");
                var raw = SecureStore.ReadRaw(SecureStore.DataDir, StoreFileNames(SecureStore.DataDir));
                long total = raw.Values.Sum(b => (long)b.Length);
                if (total > MaxTotalBytes) return new(false, $"The store is {total >> 20:N0} MB, above the {MaxTotalBytes >> 20} MB snapshot limit. Use a full .dcxbackup export instead.");
                Directory.CreateDirectory(dir);
                // Payload is base64 inside JSON (≈1.4×), plus room for the temp file and the rest of the system.
                long need = total * 3 + (50L << 20);
                if (FreeSpace(dir) is long free && free < need)
                    return new(false, $"Not enough free space in {dir} ({free >> 20:N0} MB free, needs about {need >> 20:N0} MB). No snapshot was written and none was removed.");

                var p = new Payload
                {
                    Created = DateTime.Now, Reason = reason, AppVersion = AppInfo.Version, Schema = StoreSchema.VersionsOnDisk(),
                    Files = raw.Select(kv => new FileItem { Name = kv.Key, Sha256 = Hex(kv.Value), Data = kv.Value }).ToList(),
                };
                var plain = JsonSerializer.SerializeToUtf8Bytes(p, SecureStore.Json);
                var blob = SecureStore.Encrypt(plain, "snapshot");
                CryptographicOperations.ZeroMemory(plain);

                var stem = $"snap-{p.Created:yyyyMMdd-HHmmss-fff}-{reason}";
                var path = System.IO.Path.Combine(dir, stem + Ext);
                for (int i = 2; File.Exists(path); i++) path = System.IO.Path.Combine(dir, $"{stem}~{i}{Ext}");
                tmp = path + ".tmp";
                File.WriteAllBytes(tmp, blob);
                File.Move(tmp, path);
                tmp = null;

                // Only a snapshot that reads back with every file intact counts.
                try
                {
                    var back = Open(path);
                    if (back.Files.Count != p.Files.Count || back.Files.Any(f => raw[f.Name] is var b && Hex(b) != f.Sha256)) throw new InvalidDataException("read-back differs");
                }
                catch (Exception ex)
                {
                    try { File.Delete(path); } catch { }
                    return new(false, "The snapshot did not verify after writing (" + ex.Message + "), so it was deleted. Older snapshots were kept.");
                }
                long size = new FileInfo(path).Length;
                int pruned = Prune(dir, except: path);
                if (reason != "auto") AuditLog.Write(reason == "manual" ? "you" : "app", "store", "snapshot created", $"{System.IO.Path.GetFileName(path)} · {p.Files.Count} files");
                return new(true, $"Snapshot written and verified: {p.Files.Count} files, {size / 1024.0:N0} KB" + (pruned > 0 ? $" ({pruned} older removed)" : "") + ".", path);
            }
            catch (Exception ex)
            {
                if (tmp != null) { try { File.Delete(tmp); } catch { } }
                Log.Write("Snapshot", ex);
                return new(false, "The snapshot could not be written: " + ex.Message + " Older snapshots were kept.");
            }
        }
    }

    private static readonly Regex NameRx = new(@"^snap-(\d{8}-\d{6}(?:-\d{3})?)-([a-z]+(?:-[a-z]+)?)(?:~\d+)?\.dcxsnap$", RegexOptions.CultureInvariant);

    public static List<Entry> List(string? dir = null)
    {
        dir ??= Dir;
        var list = new List<Entry>();
        if (!Directory.Exists(dir)) return list;
        foreach (var f in Directory.EnumerateFiles(dir, "snap-*" + Ext))
        {
            var m = NameRx.Match(System.IO.Path.GetFileName(f));
            if (!m.Success || !DateTime.TryParseExact(m.Groups[1].Value, new[] { "yyyyMMdd-HHmmss-fff", "yyyyMMdd-HHmmss" }, null, System.Globalization.DateTimeStyles.None, out var t)) continue;
            long len; try { len = new FileInfo(f).Length; } catch { continue; }
            list.Add(new Entry(f, t, m.Groups[2].Value, len));
        }
        return list.OrderByDescending(e => e.Created).ThenByDescending(e => e.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>Removes automatic snapshots beyond the configured count and safety snapshots beyond
    /// <see cref="SafetyKeep"/> per reason. Manual snapshots are never removed here.</summary>
    public static int Prune(string? dir = null, string? except = null)
    {
        var all = List(dir).Where(e => except == null || !e.Path.Equals(except, StringComparison.OrdinalIgnoreCase)).ToList();
        int keep = Math.Clamp(AppSettings.Current.SnapshotKeep, 1, 500);
        var victims = all.Where(e => e.Reason == "auto").Skip(except != null && List(dir).Any(e => e.Path.Equals(except, StringComparison.OrdinalIgnoreCase) && e.Reason == "auto") ? keep - 1 : keep)
            .Concat(all.Where(e => e.Reason.StartsWith("pre-")).GroupBy(e => e.Reason).SelectMany(g => g.Skip(SafetyKeep)));
        int n = 0;
        foreach (var v in victims) { try { File.Delete(v.Path); n++; } catch (Exception ex) { Log.Write("Snapshot prune", ex); } }
        return n;
    }

    public static Entry? LatestAuto(string? dir = null) => List(dir).FirstOrDefault(e => e.Reason == "auto");

    /// <summary>Result of the last automatic attempt this run (shown in Settings).</summary>
    public static Result? LastAutoResult { get; private set; }

    /// <summary>Takes an automatic snapshot when they're on and the newest is older than the interval. Local only.</summary>
    public static Result? AutoIfDue()
    {
        var s = AppSettings.Current;
        if (!s.AutoSnapshots || !SecureStore.IsUnlocked || SecureStore.Frozen) return null;
        var last = LatestAuto()?.Created ?? DateTime.MinValue;
        if (DateTime.Now - last < TimeSpan.FromHours(Math.Clamp(s.SnapshotIntervalHours, 1, 24 * 30))) return null;
        var r = Create("auto");
        if (!r.Ok) Log.Write("Automatic snapshot", new IOException(r.Message));
        return LastAutoResult = r;
    }

    // ---- open / verify -----------------------------------------------------------------------------------------------

    /// <summary>Decrypts a snapshot and checks every file's SHA-256 and name. Throws InvalidDataException.</summary>
    public static Payload Open(string path)
    {
        byte[] plain;
        try { plain = SecureStore.Decrypt(File.ReadAllBytes(path), "snapshot"); }
        catch (CryptographicException) { throw new InvalidDataException("The snapshot is damaged, or belongs to a different store (another data key)."); }
        Payload p;
        try { p = JsonSerializer.Deserialize<Payload>(plain, SecureStore.Json) ?? throw new InvalidDataException("empty snapshot"); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in p.Files)
        {
            if (!SafeName(f.Name) || !seen.Add(f.Name)) throw new InvalidDataException($"unexpected file name in snapshot: {f.Name}");
            if (Hex(f.Data) != f.Sha256) throw new InvalidDataException($"{f.Name} does not match its checksum");
        }
        return p;
    }

    /// <summary>Restore drill: everything a restore would rely on, without touching the store. Every .dcx file must decrypt
    /// under its own name and parse as what it claims to be; the format must not be newer than this app.</summary>
    public static Result Verify(string path)
    {
        try
        {
            var p = Open(path);
            int files = 0, entries = 0, badLines = 0;
            foreach (var f in p.Files)
            {
                if (f.Name.EndsWith(".dcx")) { StoreSchema.ParseCheck(f.Name, SecureStore.Decrypt(f.Data, f.Name)); files++; }
                else if (f.Name.EndsWith(".dcxlog"))
                    foreach (var line in System.Text.Encoding.UTF8.GetString(f.Data).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0))
                    {
                        try { SecureStore.DecryptString(line, "audit"); entries++; } catch { badLines++; }
                    }
            }
            if (StoreSchema.NewerThanSupported(p.Schema) is { } newer) return new(false, "✕ " + newer, path);
            return new(true, $"✓ {p.Files.Count} files intact; {files} data files decrypt and parse; {entries} audit entries" +
                (badLines > 0 ? $" (⚠ {badLines} audit lines unreadable — audit logs are only ever added back when missing)" : "") + " — restorable.", path);
        }
        catch (Exception ex) { return new(false, "✕ " + ex.Message, path); }
    }

    // ---- restore -----------------------------------------------------------------------------------------------------

    /// <summary>What a restore would change. keyring.json is never replaced (the current keyring wraps the same data key
    /// and holds the current password / 2FA); audit logs are only added back when missing (never rolled back).</summary>
    public sealed record Plan(List<string> Replace, List<string> AddBack, List<string> Remove, List<string> Unchanged);

    public static Plan PlanRestore(Payload p)
    {
        var dir = SecureStore.DataDir;
        var current = StoreFileNames(dir).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var replace = new List<string>(); var add = new List<string>(); var same = new List<string>();
        foreach (var f in p.Files)
        {
            // The keyring and the AI features lock are never rolled back (an older snapshot must not remove or change the
            // lock: 0.2.0 audit); the open-tabs list is the running app's own.
            if (f.Name == "keyring.json" || f.Name == "workspace.json" || f.Name == FeatureLock.FileName) continue;
            bool exists = current.Contains(f.Name);
            if (f.Name.EndsWith(".dcxlog")) { if (!exists) add.Add(f.Name); continue; }
            if (!exists) add.Add(f.Name);
            else if (Hex(File.ReadAllBytes(System.IO.Path.Combine(dir, f.Name))) != f.Sha256) replace.Add(f.Name);
            else same.Add(f.Name);
        }
        var inSnap = p.Files.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var remove = current.Where(n => n.EndsWith(".dcx") && n != FeatureLock.FileName && !inSnap.Contains(n)).OrderBy(n => n).ToList();
        return new(replace, add, remove, same);
    }

    public static string Describe(Plan plan)
    {
        static string Names(List<string> l) => l.Count == 0 ? "none" : string.Join(", ", l.Take(8).Select(StoreSchema.Describe)) + (l.Count > 8 ? $" and {l.Count - 8} more" : "");
        return $"Replaced: {Names(plan.Replace)}\nAdded back: {Names(plan.AddBack)}\nRemoved (they didn't exist then): {Names(plan.Remove)}\nUnchanged: {plan.Unchanged.Count} files\n" +
               "Kept as they are: your master password / 2FA (keyring), the AI features lock and the audit log." +
               (FeatureLock.IsSet ? "\nWhile the AI features lock is set, the settings it guards (AI features on/off, internet AI providers) keep their current values." : "");
    }

    /// <summary>Restores a verified snapshot. A verified "pre-restore" snapshot of the current state is taken first (unless
    /// <paramref name="safetySnapshot"/> is false — the migration rollback, which restores its own safety snapshot).
    /// With <paramref name="freeze"/> the store refuses further writes until the app restarts (in-memory state is stale).</summary>
    public static Result Restore(string path, bool safetySnapshot = true, bool freeze = true)
    {
        var v = Verify(path);
        if (!v.Ok) return new(false, "Not restored: " + v.Message.TrimStart('✕', ' '), path);
        Payload p;
        try { p = Open(path); } catch (Exception ex) { return new(false, "Not restored: " + ex.Message, path); }
        var plan = PlanRestore(p);
        string? pre = null;
        if (safetySnapshot)
        {
            var s = Create("pre-restore");
            if (!s.Ok) return new(false, "Not restored: the safety snapshot of your current data failed — " + s.Message, path);
            pre = System.IO.Path.GetFileName(s.Path);
        }
        var staged = plan.Replace.Concat(plan.AddBack).ToDictionary(n => n, n => p.Files.First(f => f.Name.Equals(n, StringComparison.OrdinalIgnoreCase)).Data, StringComparer.OrdinalIgnoreCase);
        // The settings the AI features lock guards keep their current values (0.2.0 audit).
        if (staged.TryGetValue("settings.dcx", out var settingsBytes))
        {
            try { staged["settings.dcx"] = FeatureLock.KeepGuardedSealed(settingsBytes); }
            catch (Exception ex) { return new(false, "Not restored: the snapshot's settings could not be checked — " + ex.Message, path); }
        }
        try { Commit(staged, plan.Remove, "restore of " + System.IO.Path.GetFileName(path)); }
        catch (Exception ex) { return new(false, "Not restored: " + ex.Message + " Your data was left as it was.", path); }
        if (freeze) SecureStore.Frozen = true;
        AuditLog.Write("you", "store", "snapshot restored", $"{System.IO.Path.GetFileName(path)} · {plan.Replace.Count} replaced, {plan.AddBack.Count} added back, {plan.Remove.Count} removed" + (pre != null ? $" · current state saved as {pre}" : ""));
        return new(true, $"Restored {System.IO.Path.GetFileName(path)} ({plan.Replace.Count} replaced, {plan.AddBack.Count} added back, {plan.Remove.Count} removed)." +
            (pre != null ? $" Your data from just before is in the snapshot {pre}." : ""), path);
    }

    // ---- journaled commit --------------------------------------------------------------------------------------------

    internal sealed class Journal
    {
        public DateTime Started { get; set; }
        public string What { get; set; } = "";
        public List<string> Replace { get; set; } = new();
        public List<string> Remove { get; set; } = new();
    }

    /// <summary>Test hook: called after the journal is written and before any file is swapped (simulated crash).</summary>
    internal static Action? AfterJournal;

    /// <summary>Swaps in <paramref name="files"/> (name → encrypted bytes) and deletes <paramref name="remove"/>, crash-safely.</summary>
    internal static void Commit(IReadOnlyDictionary<string, byte[]> files, IEnumerable<string> remove, string what)
    {
        var dir = SecureStore.DataDir;
        var removeList = remove.ToList();
        if (files.Keys.Concat(removeList).Any(n => !SafeName(n) || n == "keyring.json")) throw new InvalidOperationException("refusing to commit an unexpected file name");
        if (File.Exists(System.IO.Path.Combine(dir, JournalFile))) throw new InvalidOperationException("another restore/upgrade is still pending — restart the app first");
        try
        {
            foreach (var (n, b) in files)
            {
                var s = System.IO.Path.Combine(dir, n + StagedSuffix);
                File.WriteAllBytes(s, b);
                if (!File.ReadAllBytes(s).AsSpan().SequenceEqual(b)) throw new IOException($"the staged copy of {n} reads back differently");
            }
        }
        catch
        {
            foreach (var n in files.Keys) { try { File.Delete(System.IO.Path.Combine(dir, n + StagedSuffix)); } catch { } }
            throw;
        }
        var j = new Journal { Started = DateTime.Now, What = what, Replace = files.Keys.ToList(), Remove = removeList };
        var jp = System.IO.Path.Combine(dir, JournalFile);
        File.WriteAllText(jp + ".tmp", JsonSerializer.Serialize(j));
        File.Move(jp + ".tmp", jp, overwrite: true);
        AfterJournal?.Invoke();
        SecureStore.Locked(() => Apply(dir, j));
    }

    private static void Apply(string dir, Journal j)
    {
        foreach (var n in j.Replace)
        {
            var s = System.IO.Path.Combine(dir, n + StagedSuffix);
            if (File.Exists(s)) File.Move(s, System.IO.Path.Combine(dir, n), overwrite: true);
        }
        foreach (var n in j.Remove) { var t = System.IO.Path.Combine(dir, n); if (File.Exists(t)) File.Delete(t); }
        File.Delete(System.IO.Path.Combine(dir, JournalFile));
    }

    /// <summary>At start, before anything is loaded: completes an interrupted commit (its staged files were verified before
    /// the journal existed), and discards staged/temp files of operations that never reached their journal.
    /// Returns a message for the user when something was completed.</summary>
    public static string? FinishPending()
    {
        var dir = SecureStore.DataDir;
        if (!Directory.Exists(dir)) return null;
        string? msg = null;
        var jp = System.IO.Path.Combine(dir, JournalFile);
        if (File.Exists(jp))
        {
            Journal? j = null;
            try { j = JsonSerializer.Deserialize<Journal>(File.ReadAllText(jp)); } catch { }
            if (j == null || j.Replace.Concat(j.Remove).Any(n => !SafeName(n) || n == "keyring.json"))
            {
                // Written atomically, so unreadable means modified: apply nothing.
                File.Move(jp, jp + $".rejected-{DateTime.Now:yyyyMMdd-HHmmss}");
                msg = "An unreadable restore journal was found and set aside; no files were changed by it.";
            }
            else
            {
                SecureStore.Locked(() => Apply(dir, j));
                msg = $"An interrupted {j.What} from {j.Started:yyyy-MM-dd HH:mm} was completed ({j.Replace.Count} files).";
                AuditLog.Write("app", "store", "interrupted commit completed", $"{j.What} · {j.Replace.Count} files");
            }
        }
        foreach (var f in Directory.EnumerateFiles(dir, "*" + StagedSuffix).Concat(Directory.EnumerateFiles(dir, "*.dcx.tmp")))
        {
            try { File.Delete(f); } catch (Exception ex) { Log.Write("Discard staged file", ex); }
        }
        foreach (var d in new[] { Dir, DefaultDir(dir) }.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists))
            foreach (var f in Directory.EnumerateFiles(d, "*" + Ext + ".tmp")) { try { File.Delete(f); } catch { } }
        return msg;
    }

    // ---- helpers -----------------------------------------------------------------------------------------------------

    internal static bool SafeName(string n) =>
        n.Length is > 0 and < 200 && n == System.IO.Path.GetFileName(n) && n.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) < 0 && !n.StartsWith('.')
        && (n == "keyring.json" || n == "workspace.json" || n.EndsWith(".dcx", StringComparison.Ordinal) || n.EndsWith(".dcxlog", StringComparison.Ordinal));

    internal static string Hex(byte[] b) => Convert.ToHexString(SHA256.HashData(b));
}
