using System.IO;
using System.Xml;

namespace DcxManager.Import;

/// <summary>
/// SecureCRT (VanDyke): an XML export (Tools → Export Settings… → XML) — nested &lt;key name="…"&gt; folders under
/// "Sessions", each session's &lt;string name="Hostname"&gt;, &lt;dword name="[SSH2] Port"&gt;, "Username" and
/// "Protocol Name" — or a single session .ini from the Config\Sessions folder (S:"Hostname"=…, D:"[SSH2] Port"=00000016
/// in hex). Saved passwords are encrypted with SecureCRT's configuration passphrase and are not read: you're told which
/// sessions had one.
/// </summary>
public sealed class SecureCrtSource : IConnectionSource
{
    public string Id => "securecrt";
    public string Title => "SecureCRT";
    public string Help => "Tools → Export Settings… (XML), or pick session .ini files from %APPDATA%\\VanDyke\\Config\\Sessions. Saved passwords stay in SecureCRT: set them again here.";
    public string Filter => "SecureCRT export or session (*.xml;*.ini)|*.xml;*.ini|All files|*.*";

    public ImportResult Read(string? path, string? masterPassword = null)
    {
        var r = new ImportResult { Source = Title };
        if (path == null) throw new ArgumentNullException(nameof(path));
        if (Path.GetExtension(path).Equals(".ini", StringComparison.OrdinalIgnoreCase)) { ReadIni(path, r); return r; }
        var doc = ConnectionImport.LoadXml(ConnectionImport.ReadText(path));
        var sessions = doc.SelectSingleNode("//key[@name='Sessions']") as XmlElement ?? throw new InvalidDataException("No Sessions in this SecureCRT export.");
        Walk(sessions, "", r);
        return r;
    }

    private static void Walk(XmlElement key, string folder, ImportResult r)
    {
        foreach (var k in key.ChildNodes.OfType<XmlElement>().Where(e => e.Name == "key"))
        {
            var name = k.GetAttribute("name");
            string? Val(string n) => k.ChildNodes.OfType<XmlElement>().FirstOrDefault(e => e.Name is "string" or "dword" && e.GetAttribute("name") == n)?.InnerText;
            var host = Val("Hostname");
            if (host == null) { Walk(k, folder.Length == 0 ? name : folder + "/" + name, r); continue; }   // a folder
            if (name == "Default") continue;
            Add(r, name, folder, host, Val("Protocol Name"), Val("[SSH2] Port") ?? Val("Port"), Val("Username"), Val("Password V2") ?? Val("Password"), Val("Identity Filename V2"));
        }
    }

    private static void ReadIni(string path, ImportResult r)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in ConnectionImport.ReadText(path).Split('\n'))
        {
            var t = line.Trim();
            // S:"Name"=value   D:"Name"=0000hex   (Z: and B: blocks are skipped)
            if (t.Length < 6 || t[1] != ':' || t[2] != '"' || t[0] is not ('S' or 'D')) continue;
            int q = t.IndexOf('"', 3), eq = q < 0 ? -1 : t.IndexOf('=', q);
            if (q < 0 || eq < 0) continue;
            var key = t[3..q]; var v = t[(eq + 1)..];
            if (t[0] == 'D' && int.TryParse(v, System.Globalization.NumberStyles.HexNumber, null, out var n)) v = n.ToString();
            values[key] = v;
        }
        if (!values.TryGetValue("Hostname", out var host)) throw new InvalidDataException("No Hostname in this SecureCRT session file.");
        Add(r, Path.GetFileNameWithoutExtension(path), "", host, values.GetValueOrDefault("Protocol Name"), values.GetValueOrDefault("[SSH2] Port") ?? values.GetValueOrDefault("Port"),
            values.GetValueOrDefault("Username"), values.GetValueOrDefault("Password V2"), values.GetValueOrDefault("Identity Filename V2"));
    }

    private static void Add(ImportResult r, string name, string folder, string host, string? proto, string? port, string? user, string? sealedPassword, string? identity)
    {
        var p = (proto ?? "SSH2").ToUpperInvariant() switch { "SSH2" or "SSH1" => "ssh", "TELNET" => "telnet", "RDP" => "rdp", var x => x.ToLowerInvariant() };
        if (!string.IsNullOrEmpty(sealedPassword)) r.Warnings.Add($"“{ConnectionImport.Clean(name, 60)}”: SecureCRT keeps its saved password encrypted; set it again here.");
        ConnectionImport.Add(r, new ImportedHost
        {
            Name = name, Folder = folder, Protocol = p, Address = host, Port = ConnectionImport.Int(port), Username = user ?? "",
            KeyFile = string.IsNullOrWhiteSpace(identity) ? null : identity.Trim().TrimStart('*'),
        });
    }
}
