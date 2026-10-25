using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using DcxManager.Services;

namespace DcxManager.Files;

/// <summary>How one side of a sync gets rid of a file it deletes or overwrites (0.6.26, ADR-102).</summary>
public sealed class SyncRetire
{
    public required DeletionMode Mode { get; init; }
    /// <summary>The sync folder on this side (paths are made relative to it for the versions folder).</summary>
    public required string Root { get; init; }
    /// <summary>The versions folder on this side (absolute).</summary>
    public required string VersionsDir { get; init; }
    public VersionNaming Naming { get; init; } = VersionNaming.TimestampFile;
    /// <summary>One stamp per run, so a run's versions belong together.</summary>
    public DateTime Stamp { get; init; } = DateTime.Now;
    /// <summary>Group Policy / Settings allow permanent deletion (else it falls back to versioning).</summary>
    public bool PermanentAllowed { get; init; }
}

/// <summary>
/// Versioning for folder sync, FreeFileSync style: a deleted or overwritten file is moved into a versions folder on the
/// same side instead of being lost. "Time stamp (file)" keeps <c>&lt;folder&gt;/name 2026-10-25 172233.ext</c>, "Time stamp
/// (folder)" keeps <c>2026-10-25 172233/&lt;path&gt;</c>, "Replace" keeps only the last one. The Recycle Bin is this PC's;
/// a server has none, so there it falls back to versioning. Permanent deletion needs the admin-protected setting.
/// </summary>
public static class SyncVersioning
{
    public const string StampFormat = "yyyy-MM-dd HHmmss";
    private static readonly Regex StampInName = new(@" (\d{4}-\d{2}-\d{2} \d{6})(?=\.[^./\\]*$|$)", RegexOptions.CultureInvariant);

    /// <summary>The mode actually used on a side (no Recycle Bin on a server; no permanent deletes unless allowed).</summary>
    public static DeletionMode Effective(DeletionMode wanted, IFileSystem fs, bool permanentAllowed) => wanted switch
    {
        DeletionMode.RecycleBin when fs.IsRemote => DeletionMode.Versioning,
        DeletionMode.Permanent when !permanentAllowed => DeletionMode.Versioning,
        _ => wanted,
    };

    public static string DefaultVersionsDir(IFileSystem fs, string root) => fs.Join(root, SyncFilter.VersionsFolder);

    public static SyncRetire For(IFileSystem fs, string root, SyncOptions o, bool leftSide, DateTime stamp)
    {
        var custom = (leftSide ? o.VersionsLeft : o.VersionsRight).Trim();
        return new SyncRetire
        {
            Mode = o.Deletion, Root = root, VersionsDir = custom.Length > 0 ? custom : DefaultVersionsDir(fs, root), Naming = o.Naming, Stamp = stamp,
            PermanentAllowed = AppSettings.Current.FileSyncAllowPermanentDelete,
        };
    }

    /// <summary>Deletes (or versions) a file or folder at <paramref name="path"/> on <paramref name="fs"/>.</summary>
    public static async Task<string> RetireAsync(IFileSystem fs, SyncRetire r, string path, bool isDir, CancellationToken ct)
    {
        var mode = Effective(r.Mode, fs, r.PermanentAllowed);
        if (mode != DeletionMode.Versioning)
        {
            await fs.DeleteAsync(path, isDir, ct);   // this PC: always the Recycle Bin (LocalFs never hard-deletes)
            return mode == DeletionMode.RecycleBin || !fs.IsRemote ? "Recycle Bin" : "deleted";
        }
        var parts = RelParts(r.Root, path);
        if (parts.Count == 0) throw new IOException($"{path} isn't inside {r.Root}; not versioned.");
        string stamp = r.Stamp.ToString(StampFormat, CultureInfo.InvariantCulture);
        string dest;
        switch (r.Naming)
        {
            case VersionNaming.TimestampFolder:
                dest = Join(fs, fs.Join(r.VersionsDir, stamp), parts);
                break;
            case VersionNaming.Replace:
                dest = Join(fs, r.VersionsDir, parts);
                if (await fs.StatAsync(dest, ct) is { } old) await fs.DeleteAsync(dest, old.IsDir, ct);
                break;
            default:
                var name = parts[^1];
                var ext = isDir ? "" : Path.GetExtension(name);
                parts[^1] = (ext.Length > 0 ? name[..^ext.Length] : name) + " " + stamp + ext;
                dest = Join(fs, r.VersionsDir, parts);
                break;
        }
        await EnsureDirAsync(fs, fs.Parent(dest), ct);
        for (int i = 2; await fs.StatAsync(dest, ct) != null; i++) dest = Suffix(dest, i);
        await MoveAsync(fs, path, dest, isDir, ct);
        return "versions";
    }

    /// <summary>"C:\sync" + "C:\sync\a\b.txt" → ["a", "b.txt"]; a path outside the root → empty.</summary>
    internal static List<string> RelParts(string root, string path)
    {
        string N(string p) => p.Replace('\\', '/').TrimEnd('/');
        var r = N(root); var p = N(path);
        var cmp = path.Contains('\\') || path.Length > 1 && path[1] == ':' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (r.Length == 0 || !p.StartsWith(r + "/", cmp)) return new();
        return p[(r.Length + 1)..].Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    private static string Join(IFileSystem fs, string dir, IEnumerable<string> parts) => parts.Aggregate(dir, fs.Join);

    /// <summary>"…/a.txt" → "…/a (2).txt" (the last segment only, '/' or '\').</summary>
    private static string Suffix(string path, int n)
    {
        int cut = path.LastIndexOfAny(new[] { '/', '\\' }) + 1;
        var name = path[cut..];
        int dot = name.LastIndexOf('.');
        return path[..cut] + (dot > 0 ? name[..dot] + $" ({n})" + name[dot..] : name + $" ({n})");
    }

    public static async Task EnsureDirAsync(IFileSystem fs, string dir, CancellationToken ct)
    {
        if (dir.Length == 0 || await fs.StatAsync(dir, ct) is { IsDir: true }) return;
        await EnsureDirAsync(fs, fs.Parent(dir), ct);
        if (await fs.StatAsync(dir, ct) == null) await fs.MkdirAsync(dir, ct);
    }

    /// <summary>A rename; on this PC a folder that can't be renamed across drives is moved file by file.</summary>
    private static async Task MoveAsync(IFileSystem fs, string from, string to, bool isDir, CancellationToken ct)
    {
        try { await fs.RenameAsync(from, to, ct); }
        catch (IOException) when (isDir && !fs.IsRemote)
        {
            await fs.MkdirAsync(to, ct);
            foreach (var c in await fs.ListAsync(from, ct))
                if (!c.IsParent) await MoveAsync(fs, c.FullPath, fs.Join(to, c.Name), c.IsDir, ct);
            Directory.Delete(from);   // empty now
        }
    }

    /// <summary>Removes versions older than the limit and beyond the per-file count. Returns how many went.</summary>
    public static async Task<int> PruneAsync(IFileSystem fs, string versionsDir, VersionNaming naming, int keepDays, int maxCount, DateTime now, CancellationToken ct)
    {
        if (keepDays <= 0 && maxCount <= 0 || naming == VersionNaming.Replace) return 0;
        if (await fs.StatAsync(versionsDir, ct) is not { IsDir: true }) return 0;
        var cutoff = keepDays > 0 ? now.AddDays(-keepDays) : DateTime.MinValue;
        int removed = 0;
        if (naming == VersionNaming.TimestampFolder)
        {
            var runs = (await fs.ListAsync(versionsDir, ct)).Where(i => i.IsDir && !i.IsParent)
                .Select(i => (Item: i, Stamp: Parse(i.Name))).Where(x => x.Stamp != null).OrderByDescending(x => x.Stamp).ToList();
            for (int i = 0; i < runs.Count; i++)
                if (runs[i].Stamp < cutoff || maxCount > 0 && i >= maxCount) { await fs.DeleteAsync(runs[i].Item.FullPath, true, ct); removed++; }
            return removed;
        }
        async Task Walk(string dir)
        {
            var items = (await fs.ListAsync(dir, ct)).Where(i => !i.IsParent).ToList();
            foreach (var sub in items.Where(i => i.IsDir && Parse(i.Name) == null)) await Walk(sub.FullPath);
            var versions = items.Select(i => (Item: i, Stamp: Parse(i.Name), Key: StampInName.Replace(i.Name, ""))).Where(x => x.Stamp != null);
            foreach (var group in versions.GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                int n = 0;
                foreach (var v in group.OrderByDescending(x => x.Stamp))
                {
                    if (v.Stamp < cutoff || maxCount > 0 && n >= maxCount) { await fs.DeleteAsync(v.Item.FullPath, v.Item.IsDir, ct); removed++; }
                    n++;
                }
            }
        }
        await Walk(versionsDir);
        return removed;
    }

    private static DateTime? Parse(string name)
    {
        var m = name.Length == StampFormat.Length ? Regex.Match(" " + name, @" (\d{4}-\d{2}-\d{2} \d{6})$") : StampInName.Match(name);
        return m.Success && DateTime.TryParseExact(m.Groups[1].Value, StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }
}
