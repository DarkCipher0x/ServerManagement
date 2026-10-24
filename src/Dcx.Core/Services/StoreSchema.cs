using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DcxManager.Services;

/// <summary>
/// Store schema versions and migrations (ADR-009). store.dcx (encrypted) records the schema version of each kind of
/// store file. A store without it predates versioning and is version 1 — the baseline — for every kind.
///
/// Start-up flow (<see cref="Migrate"/>): newer than this app → refuse to open (never "downgrade" data);
/// migrations due → verified pre-migration snapshot → transform each file in memory → parse-check → stage → journaled
/// commit → re-read and verify → on any failure after the commit, restore the pre-migration snapshot.
/// Before the commit a failure changes nothing at all.
/// </summary>
public static class StoreSchema
{
    public const string MetaFile = "store.dcx";

    public sealed class Meta
    {
        public int Format { get; set; } = 1;
        public Dictionary<string, int> Versions { get; set; } = new();
        public string WrittenBy { get; set; } = "";
        public DateTime Updated { get; set; }
        public List<string> History { get; set; } = new();
    }

    public static readonly string[] Kinds = { "data", "settings", "reports", "workspace", "notes", "incidents", "incident-files" };

    public static string? KindOf(string name) => name switch
    {
        "data.dcx" => "data",
        "settings.dcx" => "settings",
        "reports.dcx" => "reports",
        "workspace.dcx" => "workspace",
        _ when name.StartsWith("note-", StringComparison.Ordinal) && name.EndsWith(".dcx", StringComparison.Ordinal) => "notes",
        _ when name.StartsWith("incident-", StringComparison.Ordinal) && name.EndsWith(".dcx", StringComparison.Ordinal) => "incidents",
        _ when name.StartsWith("incfile-", StringComparison.Ordinal) && name.EndsWith(".dcx", StringComparison.Ordinal) => "incident-files",
        _ => null,
    };

    public static string Describe(string name) => name switch
    {
        "data.dcx" => "hosts, vault & snippets (data.dcx)",
        "settings.dcx" => "settings (settings.dcx)",
        "reports.dcx" => "reports (reports.dcx)",
        "workspace.dcx" => "open tabs (workspace.dcx)",
        _ when Owned.TryGetValue(name, out var owned) => owned.Description,
        MetaFile => "format record (store.dcx)",
        FeatureLock.FileName => "AI features lock (ai-lock.dcx)",
        _ when name.StartsWith("note-") => "a note",
        _ when name.StartsWith("incident-") => "an incident",
        _ when name.StartsWith("incfile-") => "an incident attachment",
        _ when name.StartsWith("audit-") => "audit log " + name,
        _ => name,
    };

    /// <summary>Store files owned by code above the store (the crash-recovery journal): what they hold and a strict parse,
    /// registered at start-up (0.2.0 phase 6: the store doesn't reference the screens' recovery code).</summary>
    private static readonly Dictionary<string, (string Description, Func<byte[], object> Parse)> Owned = new();
    public static void Register(string fileName, string description, Func<byte[], object> parse) => Owned[fileName] = (description, parse);

    /// <summary>One schema step: upgrades a <see cref="Kind"/> file from version <see cref="From"/> to From+1, on the JSON.</summary>
    public sealed record Migration(string Kind, int From, string Description, Func<JsonNode, JsonNode> Apply);

    /// <summary>Every schema change adds one entry here. Empty today: version 1 of every kind is the baseline.</summary>
    private static readonly List<Migration> Registry = new();
    private static readonly List<Migration> TestRegistry = new();
    internal static void AddTestMigration(Migration m) => TestRegistry.Add(m);
    internal static void ClearTestMigrations() => TestRegistry.Clear();
    private static IEnumerable<Migration> All => Registry.Concat(TestRegistry);

    public static int Latest(string kind) => 1 + All.Count(m => m.Kind == kind);
    public static Dictionary<string, int> LatestAll() => Kinds.ToDictionary(k => k, Latest);
    private static Dictionary<string, int> Baseline() => Kinds.ToDictionary(k => k, _ => 1);

    /// <summary>store.dcx, or null when there is none. Throws when it exists but can't be read.</summary>
    public static Meta? ReadMeta() =>
        File.Exists(Path.Combine(SecureStore.DataDir, MetaFile)) ? SecureStore.Read<Meta>(MetaFile) : null;

    /// <summary>Versions on disk (baseline when unrecorded or unreadable) — what a snapshot records.</summary>
    public static Dictionary<string, int> VersionsOnDisk()
    {
        var v = Baseline();
        try { if (ReadMeta() is { } m) foreach (var (k, n) in m.Versions) v[k] = n; } catch { }
        return v;
    }

    public static string? NewerThanSupported(Dictionary<string, int> versions)
    {
        var newer = versions.Where(kv => kv.Value > Latest(kv.Key)).Select(kv => $"{kv.Key} v{kv.Value} (this app knows up to v{Latest(kv.Key)})").ToList();
        return newer.Count == 0 ? null : "This data was written by a newer version of the app: " + string.Join(", ", newer) + ".";
    }

    /// <summary>Checks that decrypted bytes are what the file claims to be. Throws on anything else.</summary>
    public static void ParseCheck(string name, byte[] plain)
    {
        var o = SecureStore.Json;
        _ = name switch
        {
            "data.dcx" => JsonSerializer.Deserialize<AppData>(plain, o) ?? throw new InvalidDataException("empty"),
            "settings.dcx" => JsonSerializer.Deserialize<AppSettings>(plain, o) ?? throw new InvalidDataException("empty"),
            MetaFile => JsonSerializer.Deserialize<Meta>(plain, o) ?? throw new InvalidDataException("empty"),
            "reports.dcx" => JsonSerializer.Deserialize<List<DcxManager.Reports.ServerReport>>(plain, o) ?? throw new InvalidDataException("empty"),
            "workspace.dcx" => JsonSerializer.Deserialize<List<Workspace.Entry>>(plain, o) ?? throw new InvalidDataException("empty"),
            _ when Owned.TryGetValue(name, out var owned) => owned.Parse(plain),
            _ when KindOf(name) == "notes" => (object)(JsonSerializer.Deserialize<DcxManager.Notes.Note>(plain, o) is { } n && n.FileName == name ? n : throw new InvalidDataException("note id does not match its file name")),
            _ when KindOf(name) == "incidents" => (object)(JsonSerializer.Deserialize<DcxManager.Incidents.Incident>(plain, o) is { } inc && inc.FileName == name ? inc : throw new InvalidDataException("incident id does not match its file name")),
            _ when KindOf(name) == "incident-files" => (object)(JsonSerializer.Deserialize<DcxManager.Incidents.IncidentAttachment>(plain, o) is { } att && DcxManager.Incidents.IncidentAttachment.FileFor(att.Id) == name ? att : throw new InvalidDataException("attachment id does not match its file name")),
            _ => JsonDocument.Parse(plain),
        };
    }

    public enum Outcome { UpToDate, Initialized, Migrated, NewerStore, Failed }
    public sealed record RunResult(Outcome Outcome, string Message);

    public static RunResult Migrate()
    {
        Meta? meta;
        try { meta = ReadMeta(); }
        catch (Exception ex) { return new(Outcome.Failed, "The format record (store.dcx) can't be read: " + ex.Message); }
        var have = Baseline();
        if (meta != null) foreach (var (k, n) in meta.Versions) have[k] = n;
        if (NewerThanSupported(have) is { } newer) return new(Outcome.NewerStore, newer);

        var pending = Kinds.Where(k => have.GetValueOrDefault(k, 1) < Latest(k)).ToList();
        var dir = SecureStore.DataDir;
        var targets = Snapshots.StoreFileNames(dir).Where(n => KindOf(n) is { } k && pending.Contains(k)).ToList();
        var steps = pending.SelectMany(k => Enumerable.Range(have[k], Latest(k) - have[k]).Select(v => All.Single(m => m.Kind == k && m.From == v))).ToList();
        var newMeta = new Meta
        {
            Versions = LatestAll(), WrittenBy = AppInfo.Version, Updated = DateTime.Now,
            History = (meta?.History ?? new()).Append($"{DateTime.Now:yyyy-MM-dd HH:mm} {AppInfo.Version}: " +
                (steps.Count > 0 ? string.Join("; ", steps.Select(s => $"{s.Kind} v{s.From}→v{s.From + 1} {s.Description}")) : meta == null ? "versions recorded (baseline)" : "versions updated")).TakeLast(50).ToList(),
        };

        if (targets.Count == 0)
        {
            // Nothing to transform (no files of the changed kinds, or nothing changed): just record the versions.
            if (meta != null && meta.Versions.Count == newMeta.Versions.Count && meta.Versions.All(kv => newMeta.Versions.GetValueOrDefault(kv.Key) == kv.Value))
                return new(Outcome.UpToDate, "Data format is current.");
            SecureStore.Write(MetaFile, newMeta);
            return new(Outcome.Initialized, "Data format versions recorded.");
        }

        // 1. a verified safety snapshot, or nothing happens
        var snap = Snapshots.Create("pre-migration");
        if (!snap.Ok) return new(Outcome.Failed, "Your data needs a format upgrade, but the safety snapshot failed: " + snap.Message + " Nothing was changed.");
        var check = Snapshots.Verify(snap.Path!);
        if (!check.Ok) return new(Outcome.Failed, "Your data needs a format upgrade, but the safety snapshot did not verify: " + check.Message + " Nothing was changed.");
        var snapName = Path.GetFileName(snap.Path!);

        // 2. transform in memory and parse-check every result
        var staged = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var name in targets)
            {
                var kind = KindOf(name)!;
                var plain = SecureStore.Decrypt(File.ReadAllBytes(Path.Combine(dir, name)), name);
                JsonNode node = JsonNode.Parse(plain) ?? throw new InvalidDataException($"{name} is empty");
                for (int v = have[kind]; v < Latest(kind); v++) node = All.Single(m => m.Kind == kind && m.From == v).Apply(node);
                var bytes = Encoding.UTF8.GetBytes(node.ToJsonString());
                ParseCheck(name, bytes);
                staged[name] = SecureStore.Encrypt(bytes, name);
            }
            staged[MetaFile] = SecureStore.Encrypt(JsonSerializer.SerializeToUtf8Bytes(newMeta, SecureStore.Json), MetaFile);
        }
        catch (Exception ex)
        {
            Log.Write("Store migration (prepare)", ex);
            return new(Outcome.Failed, $"The data format upgrade could not be prepared: {ex.Message}. Nothing was changed; the snapshot {snapName} was kept.");
        }

        // 3. journaled commit, 4. verify what is now on disk; roll back from the snapshot if anything is off
        try
        {
            Snapshots.Commit(staged, Array.Empty<string>(), "data format upgrade");
            foreach (var name in staged.Keys) ParseCheck(name, SecureStore.Decrypt(File.ReadAllBytes(Path.Combine(dir, name)), name));
            if (ReadMeta() is not { } m2 || NewerThanSupported(m2.Versions) != null || Kinds.Any(k => m2.Versions.GetValueOrDefault(k) != Latest(k)))
                throw new InvalidDataException("the format record did not update");
        }
        catch (Exception ex)
        {
            Log.Write("Store migration (commit)", ex);
            var rb = Snapshots.Restore(snap.Path!, safetySnapshot: false, freeze: false);
            return new(Outcome.Failed, $"The data format upgrade did not verify ({ex.Message}). " +
                (rb.Ok ? $"Your data was put back from the snapshot {snapName}." : $"Putting it back from {snapName} failed too: {rb.Message} Restore it from Settings → Snapshots, or import a .dcxbackup."));
        }
        AuditLog.Write("app", "store", "data format upgraded", string.Join("; ", steps.Select(s => $"{s.Kind} v{s.From}→v{s.From + 1}")) + $" · {targets.Count} files · snapshot {snapName}");
        return new(Outcome.Migrated, $"Your data was upgraded to the current format ({targets.Count} files, verified). The previous version is kept in the snapshot {snapName}.");
    }
}

/// <summary>
/// Start-up recovery (ADR-009), before anything is loaded: finish an interrupted restore/upgrade, find damaged store
/// files and offer the newest snapshot copy that verifies (the damaged file is renamed aside, never overwritten or
/// deleted), then run <see cref="StoreSchema.Migrate"/>. Returns false when the app must not open the store.
/// </summary>
public static class StoreRecovery
{
    public sealed record Damage(string Name, string Error, Snapshots.Entry? GoodCopy);

    /// <summary>Messages for the status bar once the window is up (completed commits, upgrades, recoveries).</summary>
    public static List<string> Notices { get; } = new();

    /// <summary>Every non-note .dcx file must decrypt under its own name and parse (notes: NoteStore lists damaged ones).</summary>
    public static List<Damage> Check()
    {
        var list = new List<Damage>();
        var dir = SecureStore.DataDir;
        foreach (var name in Snapshots.StoreFileNames(dir).Where(n => n.EndsWith(".dcx") && StoreSchema.KindOf(n) != "notes"))
        {
            try { StoreSchema.ParseCheck(name, SecureStore.Decrypt(File.ReadAllBytes(Path.Combine(dir, name)), name)); }
            catch (Exception ex) { list.Add(new(name, ex is System.Security.Cryptography.CryptographicException ? "it doesn't decrypt (damaged or modified)" : ex.Message, FindGoodCopy(name))); }
        }
        return list;
    }

    /// <summary>The snapshot folder the settings name (read raw: settings may not be loaded yet, or be the damaged file).</summary>
    private static IEnumerable<string> CandidateDirs()
    {
        var dirs = new List<string>();
        try
        {
            var plain = SecureStore.Decrypt(File.ReadAllBytes(Path.Combine(SecureStore.DataDir, "settings.dcx")), "settings.dcx");
            if (JsonNode.Parse(plain)?["SnapshotDir"]?.GetValue<string>() is { Length: > 0 } d) dirs.Add(d);
        }
        catch { }
        dirs.Add(Snapshots.DefaultDir(SecureStore.DataDir));
        return dirs.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    public static Snapshots.Entry? FindGoodCopy(string name)
    {
        foreach (var e in CandidateDirs().SelectMany(d => Snapshots.List(d)).OrderByDescending(e => e.Created).Take(40))
        {
            try
            {
                var f = Snapshots.Open(e.Path).Files.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (f == null) continue;
                StoreSchema.ParseCheck(name, SecureStore.Decrypt(f.Data, name));
                return e;
            }
            catch { }
        }
        return null;
    }

    private static string SetAsideName(string name) => $"{name}.damaged-{DateTime.Now:yyyyMMdd-HHmmss}";

    /// <summary>Renames the damaged file aside (kept for inspection) and puts the snapshot's copy in its place.</summary>
    public static string RestoreFile(string name, Snapshots.Entry from)
    {
        var f = Snapshots.Open(from.Path).Files.First(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        StoreSchema.ParseCheck(name, SecureStore.Decrypt(f.Data, name));
        // The settings come back with what the AI features lock guards switched off while it is set (FeatureLock).
        var data = name.Equals("settings.dcx", StringComparison.OrdinalIgnoreCase) ? FeatureLock.FailClosedSealed(f.Data) : f.Data;
        var aside = SetAsideName(name);
        File.Move(Path.Combine(SecureStore.DataDir, name), Path.Combine(SecureStore.DataDir, aside));
        Snapshots.Commit(new Dictionary<string, byte[]> { [name] = data }, Array.Empty<string>(), "file recovery");
        AuditLog.Write("you", "store", "damaged file restored from snapshot", $"{name} ← {from.Name} · damaged copy kept as {aside}");
        return $"{StoreSchema.Describe(name)} was restored from the snapshot of {from.Created:yyyy-MM-dd HH:mm}; the damaged file is kept as {aside}.";
    }

    /// <summary>Renames the damaged file aside so the app starts without it (nothing deleted).</summary>
    public static string SetAside(string name)
    {
        var aside = SetAsideName(name);
        File.Move(Path.Combine(SecureStore.DataDir, name), Path.Combine(SecureStore.DataDir, aside));
        AuditLog.Write("you", "store", "damaged file set aside", $"{name} → {aside}");
        return $"{StoreSchema.Describe(name)} could not be read and was set aside as {aside}; the app started without it. Import a .dcxbackup to bring the data back.";
    }

    /// <summary>The whole start-up sequence. <paramref name="ask"/>(title, text, options) returns the chosen index (-1 = closed).</summary>
    public static bool RunAtStart(Func<string, string, string[], int> ask)
    {
        try { if (Snapshots.FinishPending() is { } m) Notices.Add(m); }
        catch (Exception ex)
        {
            Log.Write("Finish pending commit", ex);
            ask("Data store", "An interrupted restore or upgrade could not be completed: " + ex.Message + "\n\nNothing else was changed. The app will close; if this repeats, see Help → Snapshots.", new[] { "Close" });
            return false;
        }

        foreach (var d in Check())
        {
            if (d.GoodCopy is { } g)
            {
                int c = ask("Damaged data file",
                    $"{StoreSchema.Describe(d.Name)} can't be read: {d.Error}.\n\nThe snapshot from {g.Created:yyyy-MM-dd HH:mm} ({g.ReasonText}) has a good copy. " +
                    "Restore just this file from it? The damaged file is kept next to it for inspection; nothing is deleted.",
                    new[] { "Restore this file", "Close the app" });
                if (c != 0) return false;
                try { Notices.Add(RestoreFile(d.Name, g)); }
                catch (Exception ex) { Log.Write("Restore damaged file", ex); ask("Damaged data file", "Restoring it failed: " + ex.Message, new[] { "Close" }); return false; }
            }
            else
            {
                int c = ask("Damaged data file",
                    $"{StoreSchema.Describe(d.Name)} can't be read: {d.Error}.\n\nNo snapshot has a readable copy. You can start without it (the damaged file is kept aside, nothing is deleted) " +
                    "and import a .dcxbackup afterwards, or close the app.",
                    new[] { "Close the app", "Start without it" });
                if (c != 1) return false;
                try { Notices.Add(SetAside(d.Name)); }
                catch (Exception ex) { Log.Write("Set damaged file aside", ex); ask("Damaged data file", "Setting it aside failed: " + ex.Message, new[] { "Close" }); return false; }
            }
        }

        try { AppSettings.Load(); } catch (Exception ex) { Log.Write("Settings load (recovery)", ex); }
        var r = StoreSchema.Migrate();
        switch (r.Outcome)
        {
            case StoreSchema.Outcome.NewerStore:
                ask("Newer data format", r.Message + "\n\nOpening it with this older version could damage it, so the app will close. Install the newer version again " +
                    "(or restore an older snapshot from within it).", new[] { "Close" });
                return false;
            case StoreSchema.Outcome.Failed:
                ask("Data format upgrade", r.Message, new[] { "Close" });
                return false;
            case StoreSchema.Outcome.Migrated:
                Notices.Add(r.Message);
                break;
        }
        return true;
    }
}
