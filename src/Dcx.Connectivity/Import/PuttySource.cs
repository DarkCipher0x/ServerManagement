using Microsoft.Win32;

namespace DcxManager.Import;

/// <summary>
/// PuTTY and KiTTY saved sessions on this PC (read-only from this user's registry: HKCU\Software\SimonTatham\PuTTY\Sessions
/// or HKCU\Software\9bis.com\KiTTY\Sessions). Names are URL-encoded there; HostName may carry user@host; PublicKeyFile
/// points at a .ppk (imported as a key when the file exists and is readable). Passwords are never stored by PuTTY.
/// </summary>
public sealed class PuttySource(string flavour) : IConnectionSource
{
    public string Id => flavour;
    public string Title => flavour == "kitty" ? "KiTTY" : "PuTTY";
    public string Help => $"Reads the sessions saved in {Title} on this PC (nothing is changed there). Key files (.ppk) are imported into the vault when they can be read.";
    public string Filter => "";
    public bool FromThisPc => true;

    private string KeyPath => flavour == "kitty" ? @"Software\9bis.com\KiTTY\Sessions" : @"Software\SimonTatham\PuTTY\Sessions";

    public ImportResult Read(string? path, string? masterPassword = null)
    {
        var r = new ImportResult { Source = Title };
        using var sessions = Registry.CurrentUser.OpenSubKey(KeyPath);
        if (sessions == null) { r.Warnings.Add($"No {Title} sessions are saved on this PC."); return r; }
        foreach (var id in sessions.GetSubKeyNames().Take(ConnectionImport.MaxHosts + 1))
        {
            using var k = sessions.OpenSubKey(id);
            if (k == null) continue;
            var name = Uri.UnescapeDataString(id);
            if (name.Equals("Default Settings", StringComparison.OrdinalIgnoreCase)) continue;
            var host = k.GetValue("HostName") as string ?? "";
            var user = k.GetValue("UserName") as string ?? "";
            if (host.Contains('@')) { user = host[..host.LastIndexOf('@')]; host = host[(host.LastIndexOf('@') + 1)..]; }
            var proto = (k.GetValue("Protocol") as string ?? "ssh").ToLowerInvariant();
            var key = k.GetValue("PublicKeyFile") as string;
            ConnectionImport.Add(r, new ImportedHost
            {
                Name = name, Folder = "", Protocol = proto, Address = host, Port = k.GetValue("PortNumber") is int p ? p : 0, Username = user,
                KeyFile = string.IsNullOrWhiteSpace(key) ? null : key,
            });
        }
        return r;
    }
}
