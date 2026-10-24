using Microsoft.Win32;

namespace DcxManager.Import;

/// <summary>
/// WinSCP sites: this user's registry (HKCU\Software\Martin Prikryl\WinSCP 2\Sessions) or a WinSCP.ini ([Sessions\…]
/// sections). Site names are URL-encoded and use '/' for folders. WinSCP's saved passwords are only obfuscated, not
/// encrypted; they are not read — set them again here, where they are encrypted.
/// </summary>
public sealed class WinScpSource : IConnectionSource
{
    public string Id => "winscp";
    public string Title => "WinSCP";
    public string Help => "Reads the sites saved in WinSCP on this PC, or a WinSCP.ini you pick. Saved passwords are not carried over (WinSCP only obfuscates them).";
    public string Filter => "WinSCP.ini (*.ini)|*.ini|All files|*.*";
    public bool FromThisPc => true;

    public ImportResult Read(string? path, string? masterPassword = null)
    {
        var r = new ImportResult { Source = Title };
        if (path == null)
        {
            using var sessions = Registry.CurrentUser.OpenSubKey(@"Software\Martin Prikryl\WinSCP 2\Sessions");
            if (sessions == null) { r.Warnings.Add("No WinSCP sites are saved in this PC's registry (WinSCP may keep them in WinSCP.ini: pick the file instead)."); return r; }
            foreach (var id in sessions.GetSubKeyNames().Take(ConnectionImport.MaxHosts + 1))
            {
                using var k = sessions.OpenSubKey(id);
                if (k == null) continue;
                Add(r, id, n => k.GetValue(n)?.ToString());
            }
            return r;
        }
        string? section = null; var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Flush() { if (section != null) { var v = values; Add(r, section, n => v.GetValueOrDefault(n)); } values = new(StringComparer.OrdinalIgnoreCase); }
        foreach (var raw in ConnectionImport.ReadText(path).Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                Flush();
                var s = line[1..^1];
                section = s.StartsWith(@"Sessions\", StringComparison.OrdinalIgnoreCase) ? s[9..] : null;
                continue;
            }
            int eq = line.IndexOf('=');
            if (section != null && eq > 0) values[line[..eq]] = line[(eq + 1)..];
        }
        Flush();
        return r;
    }

    private static void Add(ImportResult r, string id, Func<string, string?> get)
    {
        var full = Uri.UnescapeDataString(id);
        if (full.Equals("Default Settings", StringComparison.OrdinalIgnoreCase)) return;
        var folder = full.Contains('/') ? full[..full.LastIndexOf('/')] : "";
        var name = full.Contains('/') ? full[(full.LastIndexOf('/') + 1)..] : full;
        if (!string.IsNullOrEmpty(get("Password"))) r.Warnings.Add($"“{ConnectionImport.Clean(name, 60)}”: WinSCP's saved password is not carried over; set it again here.");
        var key = get("PublicKeyFile");
        ConnectionImport.Add(r, new ImportedHost
        {
            Name = name, Folder = folder, Protocol = "ssh", Address = get("HostName") ?? "", Port = ConnectionImport.Int(get("PortNumber")), Username = Uri.UnescapeDataString(get("UserName") ?? ""),
            KeyFile = string.IsNullOrWhiteSpace(key) ? null : Uri.UnescapeDataString(key),
        });
    }
}
