namespace DcxManager.Services;

/// <summary>A File commander directory hotlist entry (Ctrl+\, 0.3.0 §15): a place ("local" / "host:&lt;id&gt;") and a folder.
/// Stored in the encrypted data store.</summary>
public sealed class HotlistEntry
{
    public string Name { get; set; } = "";
    public string Place { get; set; } = "local";
    public string Path { get; set; } = "";
}

/// <summary>A File commander user-menu command (F2 in Midnight Commander; here Ctrl+F2 or Command → User menu, 0.3.0 §15).
/// The command runs in a visible terminal tab, never hidden; %f %F %s %d %D %t are replaced with shell-quoted values
/// (Files/McTools.Expand). Stored in the encrypted data store.</summary>
public sealed class UserMenuEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string Command { get; set; } = "";
    /// <summary>"any", "file" or "dir": when it's offered.</summary>
    public string For { get; set; } = "any";

    public static List<UserMenuEntry> Defaults() => new()
    {
        new() { Title = "Show file type", Command = "file -- %f", For = "file" },
        new() { Title = "Disk usage of this folder", Command = "du -sh -- %d/* | sort -h | tail -n 30", For = "any" },
        new() { Title = "Tail a log (follow)", Command = "tail -n 200 -f -- %f", For = "file" },
        new() { Title = "SHA-256 of the selection", Command = "sha256sum -- %s", For = "any" },
        new() { Title = "Pack the selection into a .tar.gz", Command = "tar -czf %d/archive-$(date +%Y%m%d-%H%M%S).tar.gz -C %d -- %t", For = "any" },
        new() { Title = "Unpack this archive here", Command = "tar -xvf %f -C %d", For = "file" },
    };
}
