using System.IO;
using System.Xml;

namespace DcxManager.Import;

/// <summary>
/// Remote Desktop Connection Manager (.rdg, Sysinternals RDCMan 2.7+ and the old 2.2 format): groups become folders,
/// servers become Remote Desktop hosts with their port and logon user (DOMAIN\user). RDCMan encrypts saved passwords to
/// this Windows user or a certificate: they are not read.
/// </summary>
public sealed class RdcManSource : IConnectionSource
{
    public string Id => "rdcman";
    public string Title => "Remote Desktop Connection Manager";
    public string Help => "Pick the .rdg file. Groups become folders; saved passwords stay in RDCMan.";
    public string Filter => "RDCMan files (*.rdg)|*.rdg|All files|*.*";

    public ImportResult Read(string? path, string? masterPassword = null)
    {
        var r = new ImportResult { Source = "RDCMan" };
        var doc = ConnectionImport.LoadXml(ConnectionImport.ReadText(path ?? throw new ArgumentNullException(nameof(path))));
        if (doc.DocumentElement?.LocalName != "RDCMan") throw new InvalidDataException("This isn't an RDCMan file.");
        foreach (XmlElement s in doc.GetElementsByTagName("server"))
        {
            static string? P(XmlElement e, string name) => (e.SelectSingleNode($"properties/{name}") ?? e.SelectSingleNode(name))?.InnerText;
            var folder = new List<string>();
            for (var p = s.ParentNode as XmlElement; p != null && p.LocalName == "group"; p = p.ParentNode as XmlElement) folder.Insert(0, P(p, "name") ?? "");
            var cred = s.SelectSingleNode("logonCredentials") as XmlElement;
            var user = cred?.SelectSingleNode("userName")?.InnerText ?? cred?.SelectSingleNode("username")?.InnerText ?? "";
            var domain = cred?.SelectSingleNode("domain")?.InnerText ?? "";
            if (cred?.SelectSingleNode("password")?.InnerText is { Length: > 0 }) r.Warnings.Add($"“{ConnectionImport.Clean(P(s, "displayName") ?? P(s, "name"), 60)}”: RDCMan's saved password is not carried over.");
            ConnectionImport.Add(r, new ImportedHost
            {
                Name = P(s, "displayName") is { Length: > 0 } dn ? dn : P(s, "name") ?? "", Folder = string.Join("/", folder), Protocol = "rdp",
                Address = P(s, "name") ?? "", Port = ConnectionImport.Int(s.SelectSingleNode("connectionSettings/port")?.InnerText),
                Username = domain.Length > 0 && user.Length > 0 && !user.Contains('\\') ? domain + "\\" + user : user, Notes = P(s, "comment") ?? "",
            });
        }
        return r;
    }
}
