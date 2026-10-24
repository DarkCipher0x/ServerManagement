using System.IO;
using System.Text.RegularExpressions;
using DcxManager.Services;

namespace DcxManager.Notes;

/// <summary>An earlier version of a note (kept inside the note's own encrypted file).</summary>
public sealed class NoteRevision
{
    public DateTime At { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
}

/// <summary>An engineering note. Plain text / Markdown body; linked to hosts and/or a session key (serial, local).</summary>
public sealed class Note
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "Untitled note";
    public string Body { get; set; } = "";
    public List<string> Tags { get; set; } = new();
    /// <summary>Saved hosts this note is about (the host's notebook = notes linking it).</summary>
    public List<string> HostIds { get; set; } = new();
    /// <summary>Notebook for a non-host session: "serial:&lt;profile id&gt;", "local:pwsh", "wsl:Ubuntu".</summary>
    public string? SessionKey { get; set; }
    public bool Favorite { get; set; }
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime Updated { get; set; } = DateTime.Now;
    /// <summary>Set when moved to the Trash; files are only deleted by "Empty trash".</summary>
    public DateTime? Deleted { get; set; }
    public List<NoteRevision> Revisions { get; set; } = new();

    public string FileName => FileFor(Id);
    public static string FileFor(string id) => $"note-{id}.dcx";
}

/// <summary>
/// The notes service (ADR-005). Every note is its own encrypted file (note-&lt;id&gt;.dcx, AES-256-GCM, file name as AAD,
/// atomic writes) in the store folder; everything is loaded and indexed in memory after unlock. Views never touch files:
/// they call Create / Save / Delete / Search here. All methods are UI-thread friendly and safe to call repeatedly.
/// </summary>
public static class NoteStore
{
    public const int MaxRevisions = 30;
    public const long MaxRevisionChars = 2_000_000;
    /// <summary>A new revision is recorded when the last one is older than this (or on explicit snapshots).</summary>
    public static TimeSpan RevisionEvery { get; set; } = TimeSpan.FromMinutes(10);

    private static List<Note>? _notes;
    private static readonly object Gate = new();
    private static readonly NoteIndex Index = new();

    /// <summary>Note files that couldn't be decrypted/parsed. Never overwritten; shown in the Notes view.</summary>
    public static List<string> Damaged { get; } = new();

    /// <summary>Raised after any change (argument: the note, or null for bulk changes). UI thread when called from it.</summary>
    public static event Action<Note?>? Changed;

    private static List<Note> Loaded { get { lock (Gate) return _notes ??= LoadAll(); } }

    public static IReadOnlyList<Note> All => Loaded.Where(n => n.Deleted == null).ToList();
    public static IReadOnlyList<Note> Trash => Loaded.Where(n => n.Deleted != null).OrderByDescending(n => n.Deleted).ToList();
    public static Note? Find(string? id) => id == null ? null : Loaded.FirstOrDefault(n => n.Id == id);

    /// <summary>Forget everything in memory (lock, store move, self-test). The next access reloads from disk.</summary>
    public static void Reset() { lock (Gate) { _notes = null; Damaged.Clear(); Index.Clear(); } }

    private static List<Note> LoadAll()
    {
        var list = new List<Note>();
        Damaged.Clear();
        Index.Clear();
        if (!Directory.Exists(SecureStore.DataDir)) return list;
        foreach (var path in Directory.EnumerateFiles(SecureStore.DataDir, "note-*.dcx"))
        {
            var name = Path.GetFileName(path);
            try
            {
                var n = SecureStore.Read<Note>(name);
                if (n.FileName != name) throw new InvalidDataException("note id does not match its file name");
                list.Add(n);
            }
            catch (Exception ex) { Damaged.Add(name); Log.Write("Note load " + name, ex); }
        }
        foreach (var n in list) Index.Add(n, HostNames(n));
        return list;
    }

    private static IEnumerable<string> HostNames(Note n) =>
        n.HostIds.Select(id => AppData.Current.FindHost(id)).Where(h => h != null).SelectMany(h => new[] { h!.Display, h.Address });

    public static Note Create(string title, IEnumerable<string>? hostIds = null, string body = "", string? sessionKey = null)
    {
        var n = new Note { Title = title.Trim().Length > 0 ? title.Trim() : "Untitled note", Body = body, HostIds = hostIds?.Distinct().ToList() ?? new(), SessionKey = sessionKey };
        lock (Gate) Loaded.Add(n);
        Persist(n);
        Changed?.Invoke(n);
        return n;
    }

    /// <summary>Saves a note (autosave or explicit). Records a revision of the PREVIOUS content when the last one is
    /// older than <see cref="RevisionEvery"/>, or always when <paramref name="snapshot"/> is set.</summary>
    public static void Save(Note n, string? previousTitle = null, string? previousBody = null, bool snapshot = false)
    {
        n.Updated = DateTime.Now;
        if (previousBody != null && (previousBody != n.Body || previousTitle != n.Title))
        {
            var last = n.Revisions.LastOrDefault();
            if (snapshot || last == null || DateTime.Now - last.At >= RevisionEvery)
            {
                n.Revisions.Add(new NoteRevision { At = DateTime.Now, Title = previousTitle ?? n.Title, Body = previousBody });
                while (n.Revisions.Count > MaxRevisions || n.Revisions.Count > 1 && n.Revisions.Sum(r => (long)r.Body.Length) > MaxRevisionChars) n.Revisions.RemoveAt(0);
            }
        }
        Persist(n);
        Changed?.Invoke(n);
    }

    private static void Persist(Note n)
    {
        SecureStore.Write(n.FileName, n);
        lock (Gate) { Index.Remove(n.Id); if (n.Deleted == null) Index.Add(n, HostNames(n)); }
    }

    public static void Delete(Note n) { n.Deleted = DateTime.Now; Persist(n); Changed?.Invoke(n); AuditLog.Write("you", "notes", "note moved to trash", n.Title); }
    public static void Restore(Note n) { n.Deleted = null; Persist(n); Changed?.Invoke(n); }

    /// <summary>Permanently deletes every note in the Trash (the caller confirms with the user). Returns the count.</summary>
    public static int EmptyTrash()
    {
        var gone = Trash.ToList();
        foreach (var n in gone)
        {
            try { File.Delete(Path.Combine(SecureStore.DataDir, n.FileName)); } catch (Exception ex) { Log.Write("Note purge", ex); continue; }
            lock (Gate) { Loaded.Remove(n); Index.Remove(n.Id); }
        }
        if (gone.Count > 0) { AuditLog.Write("you", "notes", "trash emptied", $"{gone.Count} note(s)"); Changed?.Invoke(null); }
        return gone.Count;
    }

    public static IReadOnlyList<Note> ForHost(string hostId) => All.Where(n => n.HostIds.Contains(hostId)).OrderByDescending(n => n.Updated).ToList();

    /// <summary>Appends a timestamped entry to the "log" note of a host (or a session), creating it the first time.
    /// Used by "Add selection to notes" in terminals.</summary>
    public static Note AppendToLog(string title, string? hostId, string? sessionKey, string entry)
    {
        var n = All.FirstOrDefault(x => x.Tags.Contains("log") && (hostId != null ? x.HostIds.Contains(hostId) : x.SessionKey == sessionKey))
                ?? Create(title + " — log", hostId != null ? new[] { hostId } : null, "", sessionKey);
        if (!n.Tags.Contains("log")) n.Tags.Add("log");
        var before = n.Body;
        n.Body = (n.Body.TrimEnd().Length > 0 ? n.Body.TrimEnd() + "\n\n" : "") + $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] " + entry.TrimEnd() + "\n";
        Save(n, n.Title, before);
        return n;
    }

    /// <summary>Words the last search ignored because no note contains them.</summary>
    public static IReadOnlyList<string> LastIgnored { get { lock (Gate) return Index.LastIgnored.ToList(); } }

    public static IReadOnlyList<NoteIndex.Hit> Search(string query, int max = 200)
    {
        var notes = Loaded;
        lock (Gate) return Index.Search(query, notes.Where(n => n.Deleted == null).ToDictionary(n => n.Id), HostLookup, max);
    }

    /// <summary>host:&lt;text&gt; → host ids whose name or address contains it.</summary>
    private static IEnumerable<string> HostLookup(string text) =>
        AppData.Current.Hosts.Where(h => h.Display.Contains(text, StringComparison.OrdinalIgnoreCase) || h.Address.Contains(text, StringComparison.OrdinalIgnoreCase)).Select(h => h.Id);

    /// <summary>Re-indexes everything (e.g. after hosts were renamed). Cheap.</summary>
    public static void Reindex() { lock (Gate) { Index.Clear(); foreach (var n in Loaded.Where(n => n.Deleted == null)) Index.Add(n, HostNames(n)); } }

    /// <summary>For backups: deep copies of every note (including the trash).</summary>
    public static List<Note> Snapshot() => Loaded.Select(n => System.Text.Json.JsonSerializer.Deserialize<Note>(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(n, SecureStore.Json), SecureStore.Json)!).ToList();

    /// <summary>Merge from a backup: notes with a new id are added; existing ids are kept as they are.</summary>
    public static int Import(IEnumerable<Note> notes)
    {
        int added = 0;
        foreach (var n in notes.Where(n => Regex.IsMatch(n.Id ?? "", "^[0-9a-f]{32}$") && Find(n.Id) == null))
        {
            lock (Gate) Loaded.Add(n);
            Persist(n);
            added++;
        }
        if (added > 0) Changed?.Invoke(null);
        return added;
    }
}
