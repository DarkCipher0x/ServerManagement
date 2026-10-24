using System.IO;
using System.Text.RegularExpressions;
using System.Xml;

namespace DcxManager.Import;

/// <summary>One connection read from another app's file: what the preview shows and what becomes a host. Passwords are
/// held only until the import writes them into the encrypted vault (or drops them when you untick "passwords").</summary>
public sealed class ImportedHost
{
    public string Name { get; set; } = "";
    /// <summary>Folder path inside the source ("Datacentre/Core"), '/'-separated, already cleaned.</summary>
    public string Folder { get; set; } = "";
    /// <summary>"ssh", "rdp" or "telnet" (others are reported as skipped).</summary>
    public string Protocol { get; set; } = "ssh";
    public string Address { get; set; } = "";
    public int Port { get; set; }
    public string Username { get; set; } = "";
    public string? Password { get; set; }
    /// <summary>A private key file the source points at (read only if it exists when importing).</summary>
    public string? KeyFile { get; set; }
    public string Notes { get; set; } = "";
    /// <summary>A jump / gateway host by name, resolved after import when a host of that name exists.</summary>
    public string? Jump { get; set; }
    public override string ToString() => $"{Name} ({Protocol} {Address}:{Port})";
}

public sealed class ImportResult
{
    public required string Source { get; init; }
    public List<ImportedHost> Hosts { get; } = new();
    /// <summary>What was skipped or couldn't be read, in words (never a secret).</summary>
    public List<string> Warnings { get; } = new();
    public bool HasPasswords => Hosts.Any(h => !string.IsNullOrEmpty(h.Password));
}

/// <summary>An app whose connection list can be read. File-based sources take a path; registry ones read this user's
/// keys (read-only). <see cref="ConnectionImport.All"/> lists them.</summary>
public interface IConnectionSource
{
    string Id { get; }
    string Title { get; }
    /// <summary>Where the file usually is and how to get it, shown before picking.</summary>
    string Help { get; }
    /// <summary>Open-file filter ("" = no file: it reads this PC only).</summary>
    string Filter { get; }
    /// <summary>Can read this user's saved sessions on this PC (the registry, read-only) — Read(null).</summary>
    bool FromThisPc => false;
    /// <summary>Several files at once (one connection per file, like .rdp).</summary>
    bool MultipleFiles => false;
    /// <summary>Reads the connections. <paramref name="masterPassword"/> only for sources that encrypt with one.</summary>
    ImportResult Read(string? path, string? masterPassword = null);
}

/// <summary>
/// Imports from other connection managers (0.5.0, ADR-061). Parse-only: nothing is executed or fetched, every file is
/// size-capped, XML is read with DTDs prohibited and no resolver (no XXE, no entity expansion), and every value is checked
/// before it can become a host (addresses, ports, names, folders). The caller previews the result with ticks.
/// </summary>
public static class ConnectionImport
{
    public const long MaxFileBytes = 32L * 1024 * 1024;
    public const int MaxHosts = 20_000;

    public static IReadOnlyList<IConnectionSource> All { get; } = new IConnectionSource[]
    {
        new MRemoteNgSource(), new SecureCrtSource(), new RoyalTsSource(), new MobaXtermSource(), new PuttySource("putty"),
        new PuttySource("kitty"), new WinScpSource(), new RdcManSource(), new RdpFileSource(), new DevolutionsSource(),
    };

    public static IConnectionSource? Find(string id) => All.FirstOrDefault(s => s.Id == id);

    /// <summary>Reads a file as text after the size check (UTF-8 / UTF-16 by BOM).</summary>
    public static string ReadText(string path)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists) throw new FileNotFoundException("The file isn't there.", path);
        if (fi.Length > MaxFileBytes) throw new InvalidDataException($"The file is larger than {MaxFileBytes / 1024 / 1024} MB.");
        return File.ReadAllText(path);
    }

    /// <summary>XML with DTDs prohibited, no external resolver, a size cap and no entity expansion.</summary>
    public static XmlDocument LoadXml(string text)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxFileBytes, MaxCharactersFromEntities = 0, IgnoreComments = true };
        var doc = new XmlDocument { XmlResolver = null };
        using var sr = new StringReader(text);
        using var xr = XmlReader.Create(sr, settings);
        doc.Load(xr);
        return doc;
    }

    private static readonly Regex HostRx = new(@"^[A-Za-z0-9._\-:\[\]%]{1,253}$", RegexOptions.CultureInvariant);

    /// <summary>Cleans one entry; null with a reason when it can't be a host. Adds it to the result when fine.</summary>
    public static void Add(ImportResult r, ImportedHost h)
    {
        if (r.Hosts.Count >= MaxHosts) { if (r.Hosts.Count == MaxHosts) r.Warnings.Add($"More than {MaxHosts:N0} connections: the rest were left out."); return; }
        h.Name = Clean(h.Name, 128);
        h.Address = Clean(h.Address, 253).Trim();
        h.Username = Clean(h.Username, 128);
        h.Folder = CleanFolder(h.Folder);
        h.Notes = Clean(h.Notes, 2000);
        h.Protocol = h.Protocol.ToLowerInvariant();
        if (h.Address.Length == 0) { r.Warnings.Add($"“{Shown(h.Name)}”: no address, skipped."); return; }
        if (!HostRx.IsMatch(h.Address)) { r.Warnings.Add($"“{Shown(h.Name)}”: “{Shown(h.Address)}” isn't a host name or address, skipped."); return; }
        if (h.Protocol is not ("ssh" or "rdp" or "telnet")) { r.Warnings.Add($"“{Shown(h.Name)}”: {h.Protocol} connections aren't imported, skipped."); return; }
        if (h.Port is < 1 or > 65535) h.Port = h.Protocol switch { "rdp" => 3389, "telnet" => 23, _ => 22 };
        if (h.Name.Length == 0) h.Name = h.Address;
        if (h.Password is { } pw && (pw.Length > 1024 || pw.Any(c => c == '\0'))) { h.Password = null; r.Warnings.Add($"“{Shown(h.Name)}”: the stored password looked damaged and was left out."); }
        if (h.KeyFile is { } kf && (kf.Length > 260 || kf.Any(char.IsControl))) h.KeyFile = null;
        if (h.Jump is { } j) { h.Jump = Clean(j, 253); if (h.Jump.Length == 0) h.Jump = null; }
        r.Hosts.Add(h);
    }

    private static string Shown(string s) => s.Length > 60 ? s[..60] + "…" : s;

    /// <summary>Control characters removed, trimmed, capped.</summary>
    public static string Clean(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var t = new string(s.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return t.Length > max ? t[..max] : t;
    }

    /// <summary>A folder path with '/' separators, no empty, "." or ".." parts, at most 8 levels of 64 characters.</summary>
    public static string CleanFolder(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var parts = s.Replace('\\', '/').Split('/').Select(p => Clean(p, 64)).Where(p => p.Length > 0 && p != "." && p != "..").Take(8);
        return string.Join("/", parts);
    }

    public static int Int(string? s, int fallback = 0) => int.TryParse(s?.Trim(), out var n) ? n : fallback;
}
