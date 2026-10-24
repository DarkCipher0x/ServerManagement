using System.IO;
using System.IO.Compression;
using System.Xml;

namespace DcxManager.Import;

/// <summary>
/// Royal TS / Royal TSX: a document (.rtsz — a zip holding the XML object store) or a CSV export (File → Export → CSV).
/// In the document every object is an element named Royal…Connection or RoyalFolder with ID, ParentID, Name, URI, Port and
/// CredentialUsername; folders are rebuilt from the parent ids. Passwords in a document are encrypted with its own key
/// and are not read; a CSV export's password column (if you ticked it) is.
/// </summary>
public sealed class RoyalTsSource : IConnectionSource
{
    public string Id => "royalts";
    public string Title => "Royal TS / Royal TSX";
    public string Help => "Pick the document (.rtsz) or a CSV export (File → Export → CSV). Passwords come along only from a CSV export that includes them.";
    public string Filter => "Royal TS document or CSV (*.rtsz;*.csv)|*.rtsz;*.csv|All files|*.*";

    public ImportResult Read(string? path, string? masterPassword = null)
    {
        var r = new ImportResult { Source = Title };
        if (path == null) throw new ArgumentNullException(nameof(path));
        if (Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase)) { ReadCsv(ConnectionImport.ReadText(path), r); return r; }
        if (new FileInfo(path).Length > ConnectionImport.MaxFileBytes) throw new InvalidDataException("The file is too large.");
        string xml;
        using (var zip = ZipFile.OpenRead(path))
        {
            var entry = zip.Entries.Where(e => !e.FullName.EndsWith('/')).OrderByDescending(e => e.Length).FirstOrDefault() ?? throw new InvalidDataException("An empty Royal TS document.");
            if (entry.Length > ConnectionImport.MaxFileBytes) throw new InvalidDataException("The document is too large.");
            using var sr = new StreamReader(entry.Open());
            var buf = new char[ConnectionImport.MaxFileBytes / 2];
            int n = sr.ReadBlock(buf, 0, buf.Length);
            xml = new string(buf, 0, n);
        }
        var doc = ConnectionImport.LoadXml(xml);
        var objects = doc.SelectNodes("//*[starts-with(local-name(), 'Royal')]")!.OfType<XmlElement>().ToList();
        string? Child(XmlElement e, string name) => e.ChildNodes.OfType<XmlElement>().FirstOrDefault(c => c.LocalName == name)?.InnerText;
        var folders = objects.Where(o => o.LocalName is "RoyalFolder" or "RoyalDocument").ToDictionary(o => Child(o, "ID") ?? Guid.NewGuid().ToString(), o => (Name: Child(o, "Name") ?? "", Parent: Child(o, "ParentID"), IsDoc: o.LocalName == "RoyalDocument"));
        string PathOf(string? id)
        {
            var parts = new List<string>();
            for (int guard = 0; id != null && folders.TryGetValue(id, out var f) && !f.IsDoc && guard < 16; guard++) { parts.Insert(0, f.Name); id = f.Parent; }
            return string.Join("/", parts);
        }
        foreach (var o in objects.Where(o => o.LocalName.EndsWith("Connection")))
        {
            var proto = o.LocalName switch { "RoyalSSHConnection" => "ssh", "RoyalRDSConnection" => "rdp", "RoyalTelnetConnection" => "telnet", var x => x.Replace("Royal", "").Replace("Connection", "").ToLowerInvariant() };
            if (!string.IsNullOrEmpty(Child(o, "CredentialPassword"))) r.Warnings.Add($"“{ConnectionImport.Clean(Child(o, "Name"), 60)}”: the document's password is encrypted by Royal TS; set it again here (or export to CSV with passwords).");
            ConnectionImport.Add(r, new ImportedHost
            {
                Name = Child(o, "Name") ?? "", Folder = PathOf(Child(o, "ParentID")), Protocol = proto, Address = Child(o, "URI") ?? "",
                Port = ConnectionImport.Int(Child(o, "Port")), Username = Child(o, "CredentialUsername") ?? "", Notes = Child(o, "Description") ?? "",
            });
        }
        if (r.Hosts.Count == 0 && r.Warnings.Count == 0) r.Warnings.Add("No connections were found in this document.");
        return r;
    }

    private static void ReadCsv(string text, ImportResult r)
    {
        var rows = Csv.Parse(text);
        if (rows.Count < 2) throw new InvalidDataException("The CSV has no rows.");
        var head = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        int Col(params string[] names) => head.FindIndex(h => names.Contains(h));
        int name = Col("name", "display name"), host = Col("computername", "uri", "computer name", "host", "hostname"), port = Col("port"),
            user = Col("username", "credentialusername", "user name"), pw = Col("password", "credentialpassword"), folder = Col("folder", "path", "parent"),
            type = Col("type", "objecttype", "connection type"), desc = Col("description");
        if (host < 0) throw new InvalidDataException("The CSV has no ComputerName / URI column.");
        foreach (var row in rows.Skip(1))
        {
            string V(int i) => i >= 0 && i < row.Count ? row[i] : "";
            var t = V(type).ToLowerInvariant();
            var proto = t.Contains("rd") || t.Contains("remote desktop") ? "rdp" : t.Contains("telnet") ? "telnet" : "ssh";
            ConnectionImport.Add(r, new ImportedHost { Name = V(name), Folder = V(folder), Protocol = proto, Address = V(host), Port = ConnectionImport.Int(V(port)), Username = V(user), Password = V(pw) is { Length: > 0 } p ? p : null, Notes = V(desc) });
        }
    }
}

/// <summary>RFC 4180 CSV (quoted fields, doubled quotes, newlines inside quotes), comma or semicolon (whichever the header uses).</summary>
public static class Csv
{
    public static List<List<string>> Parse(string text)
    {
        var firstLine = text.Split('\n', 2)[0];
        char sep = firstLine.Count(c => c == ';') > firstLine.Count(c => c == ',') ? ';' : ',';
        var rows = new List<List<string>>(); var row = new List<string>(); var cell = new System.Text.StringBuilder();
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted) { if (c == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; } else quoted = false; } else cell.Append(c); continue; }
            if (c == '"') quoted = true;
            else if (c == sep) { row.Add(cell.ToString()); cell.Clear(); }
            else if (c == '\n') { row.Add(cell.ToString().TrimEnd('\r')); cell.Clear(); if (row.Any(x => x.Length > 0)) rows.Add(row); row = new(); if (rows.Count > ConnectionImport.MaxHosts + 1) break; }
            else cell.Append(c);
        }
        row.Add(cell.ToString().TrimEnd('\r'));
        if (row.Any(x => x.Length > 0)) rows.Add(row);
        return rows;
    }
}
