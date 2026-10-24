using System.IO;

namespace DcxManager.Import;

/// <summary>
/// MobaXterm: a sessions export (.mxtsessions — right-click User sessions → Export) or MobaXterm.ini. Each [Bookmarks…]
/// section has SubRep=folder\path and one line per session: name=#icon#type%host%port%user%… (type 0 SSH, 1 Telnet,
/// 4 RDP; others are reported as skipped). MobaXterm keeps passwords in its own encrypted store: not read.
/// </summary>
public sealed class MobaXtermSource : IConnectionSource
{
    public string Id => "mobaxterm";
    public string Title => "MobaXterm";
    public string Help => "Right-click “User sessions” → Export all sessions to file (.mxtsessions), or pick MobaXterm.ini. Saved passwords stay in MobaXterm.";
    public string Filter => "MobaXterm sessions (*.mxtsessions;*.ini)|*.mxtsessions;*.ini|All files|*.*";

    public ImportResult Read(string? path, string? masterPassword = null)
    {
        var r = new ImportResult { Source = Title };
        string? section = null, folder = "";
        foreach (var raw in ConnectionImport.ReadText(path ?? throw new ArgumentNullException(nameof(path))).Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1]; folder = ""; continue; }
            if (section == null || !section.StartsWith("Bookmarks", StringComparison.OrdinalIgnoreCase)) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq]; var val = line[(eq + 1)..];
            if (key.Equals("SubRep", StringComparison.OrdinalIgnoreCase)) { folder = val; continue; }
            if (key.Equals("ImgNum", StringComparison.OrdinalIgnoreCase) || !val.StartsWith('#')) continue;
            // #icon#type%host%port%user%…
            int second = val.IndexOf('#', 1);
            if (second < 0) continue;
            var parts = val[(second + 1)..].Split('%');
            if (parts.Length < 4) continue;
            var proto = parts[0] switch { "0" => "ssh", "1" => "telnet", "4" => "rdp", "5" => "vnc", "6" => "ftp", "7" => "sftp", "8" => "serial", "14" => "wsl", var t => "type " + t };
            ConnectionImport.Add(r, new ImportedHost { Name = key, Folder = folder, Protocol = proto, Address = parts[1], Port = ConnectionImport.Int(parts[2]), Username = parts[3] });
        }
        if (r.Hosts.Count == 0 && r.Warnings.Count == 0) r.Warnings.Add("No sessions were found in this file.");
        return r;
    }
}
