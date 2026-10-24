using System.IO;
using System.Text;

namespace DcxManager.Services;

/// <summary>
/// Proof that the whole application is encrypted at rest (0.3.0 §19, DarkCipher0x: "when people do encrypt it, the entire
/// application is encrypted"): walks the data folder (and the logs folder) and classifies every file. Sealed files start
/// with the AES-GCM magic; line files (audit, session logs, error logs) must have every entry sealed; the key ring holds
/// only wrapped keys; a few files are plain by design (named here with the reason). Anything else is reported as PLAIN
/// — the self-test fails on it, and Settings → Security shows the result on demand.
/// </summary>
public static class EncryptionAudit
{
    public sealed record Entry(string Path, string State, string Note);
    public sealed record Report(List<Entry> Entries)
    {
        public IEnumerable<Entry> Plain => Entries.Where(e => e.State == "PLAIN");
        public int Sealed => Entries.Count(e => e.State == "sealed");
        public int ByDesign => Entries.Count(e => e.State == "plain by design");
        public string Summary => Plain.Any() ? $"✕ {Plain.Count()} file(s) NOT encrypted: {string.Join(", ", Plain.Take(5).Select(p => System.IO.Path.GetFileName(p.Path)))}"
            : $"✓ {Sealed} encrypted file(s) checked, {ByDesign} plain by design (key ring, pointers, program files), nothing readable without the key";
    }

    private static readonly byte[] Magic = "DCX1"u8.ToArray();

    /// <summary>Files that are plain on purpose: name pattern → why.</summary>
    public static readonly (string Pattern, string Why)[] PlainByDesign =
    [
        ("keyring.json", "holds only wrapped copies of the data key (DPAPI / password / Hello), never the key"),
        ("location.json", "a pointer to a moved store (a path, nothing else)"),
        ("profiles.json", "profile names and kinds (no data, no secrets)"),
        ("running.marker", "the crash-detection marker (a timestamp, nothing else)"),
        ("snapshots/journal.json", "the names of files being restored (a crash journal; names only)"),
        ("protected-test/*.json", "the self-test's stand-in for the admin-protected record (settings, never secrets)"),
        ("addons/**", "installed add-on program files (code); their data files are sealed separately"),
        ("*.tmp", "a write in progress (renamed into place when complete)"),
        ("*.sealing", "an add-on data file being sealed"),
        ("*.dcxstaged", "a snapshot restore staged for the next start (already sealed)"),
    ];

    /// <summary>Walks <paramref name="dataDir"/> and, when given, the logs folder.</summary>
    public static Report Run(string dataDir, string? logsDir = null)
    {
        var o = new List<Entry>();
        if (Directory.Exists(dataDir))
            foreach (var f in Directory.EnumerateFiles(dataDir, "*", SearchOption.AllDirectories))
                o.Add(Classify(f, Path.GetRelativePath(dataDir, f).Replace('\\', '/')));
        if (logsDir != null && Directory.Exists(logsDir))
            foreach (var f in Directory.EnumerateFiles(logsDir, "errors-*.log"))
                o.Add(ClassifyLines(f, Path.GetFileName(f), line => line.StartsWith("ENC ", StringComparison.Ordinal), "error log"));
        return new Report(o);
    }

    public static Entry Classify(string path, string rel)
    {
        foreach (var (pattern, why) in PlainByDesign)
            if (Matches(rel, pattern)) return new Entry(path, "plain by design", why);
        var name = Path.GetFileName(path);
        var ext = Path.GetExtension(name).ToLowerInvariant();
        switch (ext)
        {
            case ".dcx" or ".dcxsnap" or ".dcxi" or ".dcxbackup":
                return StartsWithMagic(path) ? new Entry(path, "sealed", "AES-256-GCM") : new Entry(path, "PLAIN", "expected a sealed file");
            case ".dcxlog":
                return ClassifyLines(path, rel, line => IsBase64Sealed(line), "one sealed entry per line");
            case ".dcxslog":
                return ClassifyLines(path, rel, line => line.Length > 2 && line[1] == ' ' && IsBase64Sealed(line[2..]), "session log: one sealed chunk per line");
        }
        // Anything else is unexpected: it's only fine when it's a sealed blob anyway.
        return StartsWithMagic(path) ? new Entry(path, "sealed", "AES-256-GCM") : new Entry(path, "PLAIN", "unexpected file in the store");
    }

    private static Entry ClassifyLines(string path, string rel, Func<string, bool> sealedLine, string what)
    {
        int plain = 0, total = 0;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(fs, Encoding.UTF8);
            string? line;
            while ((line = r.ReadLine()) != null)
            {
                if (line.Trim().Length == 0) continue;
                total++;
                if (!sealedLine(line.Trim())) plain++;
            }
        }
        catch (IOException ex) { return new Entry(path, "PLAIN", "couldn't be read: " + ex.Message); }
        return plain == 0 ? new Entry(path, "sealed", $"{what} ({total})") : new Entry(path, "PLAIN", $"{plain} of {total} entries not sealed");
    }

    private static bool StartsWithMagic(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var b = new byte[4];
            return fs.Read(b, 0, 4) == 4 && b.AsSpan().SequenceEqual(Magic);
        }
        catch (IOException) { return false; }
    }

    private static bool IsBase64Sealed(string s)
    {
        if (s.Length < 44) return false;
        try { var b = Convert.FromBase64String(s.Length > 64 ? s[..64] + new string('A', (4 - s.Length % 4) % 4) : s); return b.Length >= 4 && b.AsSpan(0, 4).SequenceEqual(Magic); }
        catch (FormatException) { return false; }
    }

    /// <summary>"a/*.json", "addons/**", "*.tmp" against a relative path with forward slashes.</summary>
    public static bool Matches(string rel, string pattern)
    {
        if (pattern.EndsWith("/**")) return rel.StartsWith(pattern[..^2], StringComparison.OrdinalIgnoreCase);
        var dir = pattern.Contains('/') ? pattern[..pattern.LastIndexOf('/')] : "";
        var file = pattern.Contains('/') ? pattern[(pattern.LastIndexOf('/') + 1)..] : pattern;
        var relDir = rel.Contains('/') ? rel[..rel.LastIndexOf('/')] : "";
        var relFile = rel.Contains('/') ? rel[(rel.LastIndexOf('/') + 1)..] : rel;
        if (!relDir.Equals(dir, StringComparison.OrdinalIgnoreCase)) return false;
        if (file.StartsWith('*')) return relFile.EndsWith(file[1..], StringComparison.OrdinalIgnoreCase);
        return relFile.Equals(file, StringComparison.OrdinalIgnoreCase);
    }
}
