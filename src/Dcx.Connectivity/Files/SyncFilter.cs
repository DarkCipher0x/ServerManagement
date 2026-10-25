using System.Text.RegularExpressions;

namespace DcxManager.Files;

/// <summary>
/// Folder sync filter (0.6.26, ADR-102), FreeFileSync style. Patterns go one per line (or separated by ';'), with '/' or
/// '\' as separators: <c>*</c> matches anything (separators too), <c>?</c> one character. A pattern starting with a
/// separator is anchored at the sync folder (<c>/build/</c>); otherwise it matches at any depth (<c>*.tmp</c>,
/// <c>node_modules/</c>). A trailing separator means a folder, and everything inside it. Matching ignores case.
/// Include applies to files (a folder pattern includes the files inside it); exclude applies to files and folders (an
/// excluded folder isn't even listed). Size and age limits apply to files: a file passes when either side's copy does.
/// </summary>
public sealed class SyncFilterDef
{
    public const string DefaultExclude = "/System Volume Information/\n/$Recycle.Bin/\n/RECYCLE?/\n/Recovery/\n*/thumbs.db";

    public string Include { get; set; } = "*";
    public string Exclude { get; set; } = DefaultExclude;
    /// <summary>Smallest file in bytes (0 = no limit).</summary>
    public long MinBytes { get; set; }
    /// <summary>Largest file in bytes (0 = no limit).</summary>
    public long MaxBytes { get; set; }
    /// <summary>Only files modified in the last N days (0 = any age).</summary>
    public int MaxAgeDays { get; set; }

    public SyncFilterDef Clone() => (SyncFilterDef)MemberwiseClone();
}

public sealed class SyncFilter
{
    /// <summary>Where versions go by default (inside each sync folder); never compared or synced.</summary>
    public const string VersionsFolder = ".dcx-versions";

    private readonly List<(Regex Re, bool Dir)> _inc, _exc;
    private readonly bool _all;
    private readonly long _min, _max;
    private readonly DateTime _since;

    public static readonly SyncFilter None = new(new SyncFilterDef { Exclude = "" });

    public SyncFilter(SyncFilterDef def, DateTime? now = null)
    {
        _inc = Parse(def.Include);
        _exc = Parse(def.Exclude);
        _all = _inc.Count == 0 || Lines(def.Include).Any(l => l is "*" or "/*" or "*/*");
        _min = Math.Max(0, def.MinBytes);
        _max = Math.Max(0, def.MaxBytes);
        _since = def.MaxAgeDays > 0 ? (now ?? DateTime.Now).AddDays(-def.MaxAgeDays) : DateTime.MinValue;
    }

    /// <summary>Problems with the patterns (for the editor), or null.</summary>
    public static string? Check(SyncFilterDef def)
    {
        foreach (var line in Lines(def.Include).Concat(Lines(def.Exclude)))
            if (line.Length > 512) return "a pattern is longer than 512 characters";
        if (def.MinBytes < 0 || def.MaxBytes < 0 || def.MaxAgeDays < 0) return "limits can't be negative";
        if (def.MaxBytes > 0 && def.MinBytes > def.MaxBytes) return "the smallest size is larger than the largest";
        return null;
    }

    private static IEnumerable<string> Lines(string? text) =>
        (text ?? "").Split(new[] { '\n', '\r', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static List<(Regex, bool)> Parse(string? text) => Lines(text).Select(l => { var re = Compile(l, out bool dir); return (re, dir); }).ToList();

    private static Regex Compile(string pattern, out bool dir)
    {
        var p = pattern.Replace('\\', '/');
        dir = p.EndsWith('/');
        p = p.TrimEnd('/');
        bool anchored = p.StartsWith('/');
        p = p.TrimStart('/');
        var body = string.Concat(p.Select(c => c switch { '*' => ".*", '?' => ".", _ => Regex.Escape(c.ToString()) }));
        return new Regex((anchored ? "^/" : "^(?:.*/)?") + body + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
    }

    /// <summary>"/a/b" for "a/b": what the patterns are matched against.</summary>
    private static string Key(string rel) => "/" + rel.Replace('\\', '/').Trim('/');

    /// <summary>Whether a folder is listed at all.</summary>
    public bool FolderAllowed(string rel)
    {
        var k = Key(rel);
        if (k.Equals("/" + VersionsFolder, StringComparison.OrdinalIgnoreCase)) return false;
        return !_exc.Any(p => p.Re.IsMatch(k));
    }

    /// <summary>Whether a file takes part (name patterns only).</summary>
    public bool FileAllowed(string rel)
    {
        var k = Key(rel);
        if (k.EndsWith(TransferQueue.PartSuffix, StringComparison.Ordinal)) return false;
        if (_exc.Any(p => !p.Dir && p.Re.IsMatch(k))) return false;
        if (_all) return true;
        if (_inc.Any(p => !p.Dir && p.Re.IsMatch(k))) return true;
        // A folder pattern includes every file below a matching folder.
        var parts = k.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = parts.Length - 1; i >= 1; i--)
        {
            var folder = "/" + string.Join('/', parts.Take(i));
            if (_inc.Any(p => p.Dir && p.Re.IsMatch(folder))) return true;
        }
        return false;
    }

    /// <summary>Size and age limits for one copy of a file.</summary>
    public bool Fits(FsItem? i) =>
        i != null && (i.IsDir || (_min == 0 || i.Length >= _min) && (_max == 0 || i.Length <= _max) && (_since == DateTime.MinValue || i.Modified >= _since));

    public bool HasLimits => _min > 0 || _max > 0 || _since != DateTime.MinValue;
}
