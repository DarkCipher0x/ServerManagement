using System.Xml;

namespace DcxManager.Import;

/// <summary>
/// Devolutions Remote Desktop Manager: an export (.rdm XML, File → Export → Export all entries, unencrypted or with the
/// "include credentials" option off). Connection entries have ConnectionType (RDPConfigured, SSHShell, Telnet…), Name,
/// Group (folder\sub), Url / Terminal\Host, ports and user names. Credentials in an export are encrypted and not read.
/// </summary>
public sealed class DevolutionsSource : IConnectionSource
{
    public string Id => "devolutions";
    public string Title => "Devolutions Remote Desktop Manager";
    public string Help => "File → Export → Export all entries (.rdm). Credentials are not carried over.";
    public string Filter => "RDM export (*.rdm;*.xml)|*.rdm;*.xml|All files|*.*";

    public ImportResult Read(string? path, string? masterPassword = null)
    {
        var r = new ImportResult { Source = "Remote Desktop Manager" };
        var doc = ConnectionImport.LoadXml(ConnectionImport.ReadText(path ?? throw new ArgumentNullException(nameof(path))));
        foreach (XmlElement c in doc.GetElementsByTagName("Connection"))
        {
            string? V(string xpath) => c.SelectSingleNode(xpath)?.InnerText;
            var type = V("ConnectionType") ?? "";
            if (type is "Group" or "Folder") continue;
            var proto = type switch { "RDPConfigured" or "RDP" => "rdp", "SSHShell" or "SSH" or "PuttyConnection" => "ssh", "Telnet" => "telnet", var t => t.ToLowerInvariant() };
            var host = V("Terminal/Host") ?? V("Url") ?? V("Host") ?? "";
            int port = ConnectionImport.Int(V("Terminal/HostPort") ?? V("Port"));
            if (host.Count(ch => ch == ':') == 1) { port = ConnectionImport.Int(host[(host.IndexOf(':') + 1)..], port); host = host[..host.IndexOf(':')]; }
            var user = V("Terminal/Username") ?? V("RDP/UserName") ?? V("UserName") ?? "";
            var domain = V("RDP/Domain") ?? V("Domain") ?? "";
            ConnectionImport.Add(r, new ImportedHost
            {
                Name = V("Name") ?? "", Folder = V("Group") ?? "", Protocol = proto, Address = host, Port = port,
                Username = proto == "rdp" && domain.Length > 0 && user.Length > 0 && !user.Contains('\\') ? domain + "\\" + user : user, Notes = V("Description") ?? "",
            });
        }
        if (r.Hosts.Count == 0 && r.Warnings.Count == 0) r.Warnings.Add("No connection entries were found in this export.");
        return r;
    }
}
