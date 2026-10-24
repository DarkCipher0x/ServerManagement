using System.Formats.Tar;
using System.IO;
using System.IO.Compression;

namespace DcxManager.Files;

/// <summary>
/// Browsing inside archives in the File commander (Midnight Commander's archive VFS, 0.3.0 §15): .zip (.jar .war .nupkg
/// .whl), .tar, .tar.gz / .tgz. Read-only, in memory (the archive is read once, up to <see cref="MaxArchive"/>).
/// <list type="bullet">
/// <item>Entry names are normalised; one with "..", a drive letter or an absolute path is listed as unsafe and never
/// extracted (zip-slip), and extraction always joins safe segments under the chosen folder.</item>
/// <item>Bounded: at most <see cref="MaxEntries"/> entries, each read stops at <see cref="MaxEntry"/> bytes, and a zip entry
/// that inflates beyond its declared size is refused (zip bombs).</item>
/// <item>Symbolic and hard links inside archives are listed but never followed or extracted as links.</item>
/// </list>
/// </summary>
public sealed class ArchiveReader
{
    public const long MaxArchive = 512L * 1024 * 1024;
    public const long MaxEntry = 512L * 1024 * 1024;
    public const int MaxEntries = 200_000;

    public sealed record Entry(string Path, bool IsDir, long Length, DateTime Modified, int Mode, bool Unsafe, bool IsLink);

    private readonly byte[] _data;
    public string Kind { get; }
    public IReadOnlyList<Entry> Entries { get; }

    /// <summary>"zip", "tar", "tgz" or null for a file name the commander can't browse into.</summary>
    public static string? KindOf(string name)
    {
        var n = name.ToLowerInvariant();
        return n.EndsWith(".zip") || n.EndsWith(".jar") || n.EndsWith(".war") || n.EndsWith(".nupkg") || n.EndsWith(".whl") ? "zip"
             : n.EndsWith(".tar.gz") || n.EndsWith(".tgz") ? "tgz"
             : n.EndsWith(".tar") ? "tar" : null;
    }

    public ArchiveReader(byte[] data, string kind)
    {
        _data = data; Kind = kind;
        var list = new List<Entry>();
        if (kind == "zip")
        {
            using var z = new ZipArchive(new MemoryStream(data, false), ZipArchiveMode.Read);
            foreach (var e in z.Entries)
            {
                if (list.Count >= MaxEntries) throw new InvalidDataException($"more than {MaxEntries:N0} entries");
                bool dir = e.FullName.EndsWith('/') || e.FullName.EndsWith('\\');
                var (p, bad) = Normalize(e.FullName);
                int mode = (e.ExternalAttributes >> 16) & 0xFFFF;
                bool link = (mode & 0xF000) == 0xA000;
                list.Add(new Entry(p, dir, e.Length, e.LastWriteTime.LocalDateTime, mode & 0xFFF, bad, link));
            }
        }
        else
        {
            using var s = Open(kind);
            using var r = new TarReader(s);
            TarEntry? e;
            while ((e = r.GetNextEntry()) != null)
            {
                if (list.Count >= MaxEntries) throw new InvalidDataException($"more than {MaxEntries:N0} entries");
                if (e.EntryType is TarEntryType.GlobalExtendedAttributes or TarEntryType.ExtendedAttributes) continue;
                var (p, bad) = Normalize(e.Name);
                bool link = e.EntryType is TarEntryType.SymbolicLink or TarEntryType.HardLink;
                list.Add(new Entry(p, e.EntryType == TarEntryType.Directory, e.Length, e.ModificationTime.LocalDateTime, (int)e.Mode, bad, link));
            }
        }
        // Folders implied by deeper paths ("a/b/c.txt" → "a", "a/b").
        var dirs = new HashSet<string>(list.Where(x => x.IsDir).Select(x => x.Path), StringComparer.Ordinal);
        foreach (var x in list.ToList())
        {
            var parts = x.Path.Split('/');
            for (int i = 1; i < parts.Length; i++)
            {
                var d = string.Join("/", parts.Take(i));
                if (d.Length > 0 && dirs.Add(d)) list.Add(new Entry(d, true, 0, default, 0, x.Unsafe, false));
            }
        }
        Entries = list.Where(x => x.Path.Length > 0).ToList();
    }

    private Stream Open(string kind)
    {
        Stream s = new MemoryStream(_data, false);
        return kind == "tgz" ? new GZipStream(s, CompressionMode.Decompress) : s;
    }

    /// <summary>"./a\b//c" → "a/b/c"; unsafe when it climbs ("..") or is absolute / has a drive or a colon.</summary>
    public static (string Path, bool Unsafe) Normalize(string raw)
    {
        var s = raw.Replace('\\', '/');
        bool bad = s.StartsWith('/') || s.Contains(':') || s.Contains('\0');
        var all = s.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(p => p != ".").ToList();
        if (all.Any(p => p == "..")) bad = true;
        // ".." is dropped from the shown path (so it can't pose as the parent entry); the entry stays marked unsafe.
        return (string.Join("/", all.Where(p => p != "..")), bad);
    }

    /// <summary>The direct children of a folder inside the archive ("" = the top).</summary>
    public IEnumerable<Entry> Children(string folder)
    {
        var prefix = folder.Length == 0 ? "" : folder.TrimEnd('/') + "/";
        return Entries.Where(e => e.Path.StartsWith(prefix, StringComparison.Ordinal) && e.Path.Length > prefix.Length && !e.Path[prefix.Length..].Contains('/'));
    }

    /// <summary>Every file at or under a path (a file → itself).</summary>
    public IEnumerable<Entry> Under(string path) =>
        Entries.Where(e => !e.IsDir && (e.Path == path || e.Path.StartsWith(path.TrimEnd('/') + "/", StringComparison.Ordinal)));

    /// <summary>One file's bytes (bounded; unsafe names and links refused).</summary>
    public byte[] Read(string path)
    {
        // Two entries can share a shown path ("../a/b" and "a/b"): only the safe one is ever read, never the unsafe one's bytes.
        var entry = Entries.FirstOrDefault(e => e.Path == path && !e.IsDir && !e.Unsafe)
                    ?? (Entries.Any(e => e.Path == path && e.Unsafe) ? throw new InvalidDataException("unsafe name in the archive (it would land outside the folder): " + path) : throw new FileNotFoundException(path));
        if (entry.IsLink) throw new InvalidDataException("links inside archives aren't followed: " + path);
        if (entry.Length > MaxEntry) throw new InvalidDataException("too large to open from inside the archive");
        if (Kind == "zip")
        {
            using var z = new ZipArchive(new MemoryStream(_data, false), ZipArchiveMode.Read);
            var e = z.Entries.First(x => Normalize(x.FullName) is var n && n.Path == path && !n.Unsafe && !x.FullName.EndsWith('/'));
            using var s = e.Open();
            return ReadBounded(s, e.Length);
        }
        using var ts = Open(Kind);
        using var r = new TarReader(ts);
        TarEntry? t;
        while ((t = r.GetNextEntry()) != null)
            if (Normalize(t.Name) is var n && n.Path == path && !n.Unsafe && t.EntryType is not (TarEntryType.Directory or TarEntryType.SymbolicLink or TarEntryType.HardLink))
                return t.DataStream == null ? [] : ReadBounded(t.DataStream, t.Length);
        throw new FileNotFoundException(path);
    }

    private static byte[] ReadBounded(Stream s, long declared)
    {
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        int n;
        while ((n = s.Read(buf, 0, buf.Length)) > 0)
        {
            ms.Write(buf, 0, n);
            if (ms.Length > declared || ms.Length > MaxEntry) throw new InvalidDataException("the entry inflates beyond its declared size — refused");
        }
        return ms.ToArray();
    }

    /// <summary>The relative path an entry gets when extracted from <paramref name="from"/> (its parent inside the archive).</summary>
    public static string Relative(string entryPath, string from) => from.Length == 0 ? entryPath : entryPath[(from.TrimEnd('/').Length + 1)..];
}
