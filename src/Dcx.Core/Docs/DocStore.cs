using System.IO;
using System.Text.RegularExpressions;
using DcxManager.Services;

namespace DcxManager.Docs;

/// <summary>An earlier version of a document (kept inside the document's own encrypted file).</summary>
public sealed class DocRevision
{
    public DateTime At { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
}

/// <summary>
/// A Markdown document (0.6.12, ADR-091; DarkCipher0x: "a powerful but lightweight and efficient markdown editor (separate from
/// notes), but allows me to pin to servers or clusters etc, and store in organised directories"). Lives in a folder
/// ("" = top level, "Runbooks/Linux" = nested) and can be pinned to things it is about.
/// </summary>
public sealed class Doc
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "Untitled document";
    public string Body { get; set; } = "";
    /// <summary>The folder path, '/'-separated; "" is the top level.</summary>
    public string Folder { get; set; } = "";
    /// <summary>What the document is pinned to: "host:&lt;id&gt;", "hostfolder:&lt;path&gt;", "infra:&lt;provider id&gt;",
    /// "cloud:&lt;kind&gt;:&lt;account id&gt;" (see <see cref="DocStore.PinLabel"/>).</summary>
    public List<string> Pins { get; set; } = new();
    public List<string> Tags { get; set; } = new();
    public bool Favorite { get; set; }
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime Updated { get; set; } = DateTime.Now;
    /// <summary>Set when moved to the Trash; files are only deleted by "Empty trash".</summary>
    public DateTime? Deleted { get; set; }
    public List<DocRevision> Revisions { get; set; } = new();

    public string FileName => FileFor(Id);
    public static string FileFor(string id) => $"doc-{id}.dcx";
}

/// <summary>Folders that hold no document yet (folders with documents exist through them).</summary>
public sealed class DocFolderIndex
{
    public List<string> Folders { get; set; } = new();
}

/// <summary>
/// The documents service (ADR-091), the same model as notes: every document is its own encrypted file (doc-&lt;id&gt;.dcx,
/// AES-256-GCM under the store's data key, file name as AAD, atomic writes); empty folders are kept in docs-folders.dcx.
/// Everything is loaded after unlock and searched in memory. Views never touch files.
/// </summary>
public static class DocStore
{
    public const string FoldersFile = "docs-folders.dcx";
    public const int MaxRevisions = 30;
    public const long MaxRevisionChars = 4_000_000;
    public const int MaxBodyChars = 2_000_000;
    public static TimeSpan RevisionEvery { get; set; } = TimeSpan.FromMinutes(10);

    private static List<Doc>? _docs;
    private static DocFolderIndex? _folders;
    private static readonly object Gate = new();

    /// <summary>Document files that couldn't be decrypted or parsed: never overwritten, listed in the Docs view.</summary>
    public static List<string> Damaged { get; } = new();

    /// <summary>Raised after any change (the document, or null for bulk / folder changes).</summary>
    public static event Action<Doc?>? Changed;

    /// <summary>Names for pins the app's modules know (infrastructure providers, cloud accounts); registered at start-up
    /// because Core sits below them. Returns null for an unknown pin.</summary>
    public static Func<string, string?> PinNames { get; set; } = _ => null;

    private static List<Doc> Loaded { get { lock (Gate) return _docs ??= LoadAll(); } }
    private static DocFolderIndex FolderIndex { get { lock (Gate) return _folders ??= LoadFolders(); } }

    public static IReadOnlyList<Doc> All => Loaded.Where(d => d.Deleted == null).ToList();
    public static IReadOnlyList<Doc> Trash => Loaded.Where(d => d.Deleted != null).OrderByDescending(d => d.Deleted).ToList();
    public static Doc? Find(string? id) => id == null ? null : Loaded.FirstOrDefault(d => d.Id == id);

    /// <summary>Forget everything in memory (lock, store move, self-test).</summary>
    public static void Reset() { lock (Gate) { _docs = null; _folders = null; Damaged.Clear(); } }

    private static List<Doc> LoadAll()
    {
        var list = new List<Doc>();
        Damaged.Clear();
        if (!Directory.Exists(SecureStore.DataDir)) return list;
        foreach (var path in Directory.EnumerateFiles(SecureStore.DataDir, "doc-*.dcx"))
        {
            var name = Path.GetFileName(path);
            try
            {
                var d = SecureStore.Read<Doc>(name);
                if (d.FileName != name) throw new InvalidDataException("document id does not match its file name");
                d.Folder = NormalizeFolder(d.Folder);
                list.Add(d);
            }
            catch (Exception ex) { Damaged.Add(name); Log.Write("Document load " + name, ex); }
        }
        return list;
    }

    private static DocFolderIndex LoadFolders()
    {
        try { return File.Exists(Path.Combine(SecureStore.DataDir, FoldersFile)) ? SecureStore.Read<DocFolderIndex>(FoldersFile) : new(); }
        catch (Exception ex) { Log.Write("Document folders", ex); return new(); }
    }

    private static void SaveFolders() => SecureStore.Write(FoldersFile, FolderIndex);

    // ---- folders ---------------------------------------------------------------------------------------------------

    /// <summary>A folder path cleaned up: '/'-separated, no empty or dot parts, trimmed, at most 8 levels of 60 characters.</summary>
    public static string NormalizeFolder(string? path)
    {
        var parts = (path ?? "").Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p is not ("." or "..")).Select(p => p.Length > 60 ? p[..60] : p).Take(8);
        return string.Join("/", parts);
    }

    /// <summary>Every folder (with documents or kept empty) and all their parents, sorted.</summary>
    public static IReadOnlyList<string> Folders()
    {
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string f) { for (var p = f; p.Length > 0; p = p.Contains('/') ? p[..p.LastIndexOf('/')] : "") set.Add(p); }
        foreach (var d in All) Add(d.Folder);
        foreach (var f in FolderIndex.Folders) Add(f);
        return set.ToList();
    }

    public static void CreateFolder(string path)
    {
        var f = NormalizeFolder(path);
        if (f.Length == 0) return;
        lock (Gate) if (!FolderIndex.Folders.Contains(f, StringComparer.OrdinalIgnoreCase)) FolderIndex.Folders.Add(f);
        SaveFolders();
        Changed?.Invoke(null);
    }

    /// <summary>Renames or moves a folder (and everything inside it).</summary>
    public static void RenameFolder(string from, string to)
    {
        from = NormalizeFolder(from); to = NormalizeFolder(to);
        if (from.Length == 0 || to.Length == 0 || from.Equals(to, StringComparison.OrdinalIgnoreCase)) return;
        if (Inside(to, from)) throw new InvalidOperationException("A folder can't move into itself.");
        string Map(string f) => f.Equals(from, StringComparison.OrdinalIgnoreCase) ? to : Inside(f, from) ? to + f[from.Length..] : f;
        foreach (var d in Loaded.Where(d => Inside(d.Folder, from) || d.Folder.Equals(from, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            d.Folder = Map(d.Folder);
            Persist(d);
        }
        lock (Gate) FolderIndex.Folders = FolderIndex.Folders.Select(Map).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        SaveFolders();
        AuditLog.Write("you", "docs", "folder renamed", $"{from} → {to}");
        Changed?.Invoke(null);
    }

    /// <summary>Removes a folder; its documents (and subfolders) move up to its parent. Nothing is deleted.</summary>
    public static void RemoveFolder(string path)
    {
        var f = NormalizeFolder(path);
        if (f.Length == 0) return;
        var parent = f.Contains('/') ? f[..f.LastIndexOf('/')] : "";
        foreach (var d in Loaded.Where(d => d.Folder.Equals(f, StringComparison.OrdinalIgnoreCase) || Inside(d.Folder, f)).ToList())
        {
            d.Folder = d.Folder.Equals(f, StringComparison.OrdinalIgnoreCase) ? parent : NormalizeFolder(parent + "/" + d.Folder[(f.Length + 1)..]);
            Persist(d);
        }
        lock (Gate) FolderIndex.Folders = FolderIndex.Folders
            .Select(x => x.Equals(f, StringComparison.OrdinalIgnoreCase) ? "" : Inside(x, f) ? NormalizeFolder(parent + "/" + x[(f.Length + 1)..]) : x)
            .Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        SaveFolders();
        Changed?.Invoke(null);
    }

    private static bool Inside(string folder, string parent) => folder.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase);

    // ---- documents -------------------------------------------------------------------------------------------------

    public static Doc Create(string title, string folder = "", IEnumerable<string>? pins = null, string body = "")
    {
        var d = new Doc
        {
            Title = title.Trim().Length > 0 ? title.Trim() : "Untitled document", Body = body.Length > MaxBodyChars ? body[..MaxBodyChars] : body,
            Folder = NormalizeFolder(folder), Pins = pins?.Where(ValidPin).Distinct().ToList() ?? new(),
        };
        lock (Gate) Loaded.Add(d);
        Persist(d);
        AuditLog.Write("you", "docs", "document created", d.Title);
        Changed?.Invoke(d);
        return d;
    }

    /// <summary>Saves a document (autosave or explicit), keeping a revision of the PREVIOUS content when the last one is
    /// older than <see cref="RevisionEvery"/>, or always with <paramref name="snapshot"/>.</summary>
    public static void Save(Doc d, string? previousTitle = null, string? previousBody = null, bool snapshot = false)
    {
        if (d.Body.Length > MaxBodyChars) throw new InvalidOperationException($"A document can hold up to {MaxBodyChars:N0} characters.");
        d.Updated = DateTime.Now;
        if (previousBody != null && (previousBody != d.Body || previousTitle != d.Title))
        {
            var last = d.Revisions.LastOrDefault();
            if (snapshot || last == null || DateTime.Now - last.At >= RevisionEvery)
            {
                d.Revisions.Add(new DocRevision { At = DateTime.Now, Title = previousTitle ?? d.Title, Body = previousBody });
                while (d.Revisions.Count > MaxRevisions || d.Revisions.Count > 1 && d.Revisions.Sum(r => (long)r.Body.Length) > MaxRevisionChars) d.Revisions.RemoveAt(0);
            }
        }
        Persist(d);
        Changed?.Invoke(d);
    }

    private static void Persist(Doc d) => SecureStore.Write(d.FileName, d);

    public static void Move(Doc d, string folder) { d.Folder = NormalizeFolder(folder); Persist(d); Changed?.Invoke(d); }

    public static void Delete(Doc d) { d.Deleted = DateTime.Now; Persist(d); Changed?.Invoke(d); AuditLog.Write("you", "docs", "document moved to trash", d.Title); }
    public static void Restore(Doc d) { d.Deleted = null; Persist(d); Changed?.Invoke(d); }

    /// <summary>Permanently deletes every document in the Trash (the caller confirms). Returns the count.</summary>
    public static int EmptyTrash()
    {
        var gone = Trash.ToList();
        foreach (var d in gone)
        {
            try { File.Delete(Path.Combine(SecureStore.DataDir, d.FileName)); } catch (Exception ex) { Log.Write("Document purge", ex); continue; }
            lock (Gate) Loaded.Remove(d);
        }
        if (gone.Count > 0) { AuditLog.Write("you", "docs", "trash emptied", $"{gone.Count} document(s)"); Changed?.Invoke(null); }
        return gone.Count;
    }

    // ---- pins ------------------------------------------------------------------------------------------------------

    private static readonly Regex PinRx = new(@"^(host:[0-9A-Za-z-]{1,64}|hostfolder:.{1,500}|infra:[0-9A-Za-z_.:-]{1,128}|cloud:[a-z0-9]{1,20}:[0-9A-Za-z_.:-]{1,128})$", RegexOptions.Compiled);

    public static bool ValidPin(string? pin) => pin != null && PinRx.IsMatch(pin);

    public static void Pin(Doc d, string pin)
    {
        if (!ValidPin(pin)) throw new ArgumentException("not a pin: " + pin);
        if (d.Pins.Contains(pin)) return;
        d.Pins.Add(pin);
        Persist(d);
        AuditLog.Write("you", "docs", "document pinned", $"{d.Title} → {PinLabel(pin)}");
        Changed?.Invoke(d);
    }

    public static void Unpin(Doc d, string pin) { if (d.Pins.Remove(pin)) { Persist(d); Changed?.Invoke(d); } }

    /// <summary>Documents pinned to <paramref name="pin"/>; for a host, also those pinned to its folder or a parent folder.</summary>
    public static IReadOnlyList<Doc> PinnedTo(string pin)
    {
        var keys = new HashSet<string> { pin };
        if (pin.StartsWith("host:", StringComparison.Ordinal) && AppData.Current.FindHost(pin[5..]) is { } h)
            for (var f = AppData.NormalizeFolder(h.Group); f.Length > 0; f = f.Contains('/') ? f[..f.LastIndexOf('/')] : "") keys.Add("hostfolder:" + f);
        return All.Where(d => d.Pins.Any(keys.Contains)).OrderByDescending(d => d.Favorite).ThenBy(d => d.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>A pin as people read it ("🖥 web-01", "📁 Production", the provider's or account's name).</summary>
    public static string PinLabel(string pin)
    {
        if (pin.StartsWith("host:", StringComparison.Ordinal)) return AppData.Current.FindHost(pin[5..]) is { } h ? "🖥 " + h.Display : "🖥 (removed host)";
        if (pin.StartsWith("hostfolder:", StringComparison.Ordinal)) return "📁 " + pin["hostfolder:".Length..];
        if (pin.StartsWith("infra:", StringComparison.Ordinal))
            return AppData.Current.InfraProviders.FirstOrDefault(p => p.Id == pin[6..]) is { } prov ? "🗄 " + prov.Name : "🗄 (removed provider)";
        return PinNames(pin) ?? pin;
    }

    // ---- search, backup --------------------------------------------------------------------------------------------

    /// <summary>Documents whose title, body, folder or tags contain every word of <paramref name="query"/> (any order,
    /// any case); titles first.</summary>
    public static IReadOnlyList<Doc> Search(string query, int max = 200)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return All.OrderByDescending(d => d.Updated).Take(max).ToList();
        bool In(string s, string w) => s.Contains(w, StringComparison.OrdinalIgnoreCase);
        return All.Where(d => words.All(w => In(d.Title, w) || In(d.Body, w) || In(d.Folder, w) || d.Tags.Any(t => In(t, w))))
            .OrderByDescending(d => words.Count(w => In(d.Title, w))).ThenByDescending(d => d.Updated).Take(max).ToList();
    }

    /// <summary>For backups: deep copies of every document (the trash included).</summary>
    public static List<Doc> Snapshot() => Loaded.Select(d => System.Text.Json.JsonSerializer.Deserialize<Doc>(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(d, SecureStore.Json), SecureStore.Json)!).ToList();

    /// <summary>Merge from a backup: documents with a new id are added; existing ids are kept as they are.</summary>
    public static int Import(IEnumerable<Doc> docs)
    {
        int added = 0;
        foreach (var d in docs.Where(d => Regex.IsMatch(d.Id ?? "", "^[0-9a-f]{32}$") && Find(d.Id) == null))
        {
            d.Folder = NormalizeFolder(d.Folder);
            d.Pins = d.Pins.Where(ValidPin).Distinct().ToList();
            lock (Gate) Loaded.Add(d);
            Persist(d);
            added++;
        }
        if (added > 0) Changed?.Invoke(null);
        return added;
    }
}
