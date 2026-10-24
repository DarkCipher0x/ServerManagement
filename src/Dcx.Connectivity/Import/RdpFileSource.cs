using System.IO;

namespace DcxManager.Import;

/// <summary>
/// Remote Desktop .rdp files (pick several): full address (host[:port]), server port, username (DOMAIN\user) and the
/// gateway, one host per file named after the file. A saved password blob (DPAPI, this user only) is not read.
/// </summary>
public sealed class RdpFileSource : IConnectionSource
{
    public string Id => "rdp";
    public string Title => "Remote Desktop files (.rdp)";
    public string Help => "Pick one or more .rdp files; each becomes a Remote Desktop host.";
    public string Filter => "Remote Desktop files (*.rdp)|*.rdp|All files|*.*";
    public bool MultipleFiles => true;

    public ImportResult Read(string? path, string? masterPassword = null)
    {
        var r = new ImportResult { Source = Title };
        var v = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in ConnectionImport.ReadText(path ?? throw new ArgumentNullException(nameof(path))).Split('\n'))
        {
            var parts = raw.Trim().Split(':', 3);   // name:type:value
            if (parts.Length == 3) v[parts[0].Trim()] = parts[2].Trim();
        }
        var addr = v.GetValueOrDefault("full address") ?? v.GetValueOrDefault("alternate full address") ?? "";
        int port = ConnectionImport.Int(v.GetValueOrDefault("server port"));
        // host:port, [v6]:port
        if (addr.StartsWith('[') && addr.IndexOf("]:", StringComparison.Ordinal) is > 0 and var b) { port = ConnectionImport.Int(addr[(b + 2)..], port); addr = addr[1..b]; }
        else if (addr.Count(c => c == ':') == 1) { port = ConnectionImport.Int(addr[(addr.IndexOf(':') + 1)..], port); addr = addr[..addr.IndexOf(':')]; }
        var gw = v.GetValueOrDefault("gatewayhostname");
        ConnectionImport.Add(r, new ImportedHost
        {
            Name = Path.GetFileNameWithoutExtension(path), Protocol = "rdp", Address = addr, Port = port, Username = v.GetValueOrDefault("username") ?? "",
            Notes = string.IsNullOrEmpty(gw) ? "" : "Remote Desktop gateway in the .rdp file: " + gw,
        });
        return r;
    }
}
