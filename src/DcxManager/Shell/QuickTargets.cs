using System.Text.RegularExpressions;
using DcxManager.Services;

namespace DcxManager;

/// <summary>
/// Quick connect's protocols and history (0.3.0, DarkCipher0x: "more quick connect protocols… an easy navigable drop down… a
/// connect button… pin history in the dropdowns"). An entry is one canonical line that says its protocol by itself —
/// <c>user@host</c> (SSH), <c>sftp://user@host</c>, <c>rdp://user@host</c>, <c>telnet host 23</c>, <c>COM3 115200</c>,
/// <c>wsl Ubuntu</c>, <c>https://nas.lan</c>, <c>ext://user@host</c> (external SSH client) — so a pinned or recent line
/// reopens exactly what it was. Pins live in the encrypted store (<see cref="AppData.PinnedQuick"/>).
/// </summary>
public static class QuickTargets
{
    public sealed record Proto(string Id, string Label, string Hint);

    public const int MaxPinned = 30, MaxRecent = 15;

    /// <summary>The protocol menu, in order. Telnet only while it is allowed (ADR-019).</summary>
    public static IReadOnlyList<Proto> All(bool telnet)
    {
        var list = new List<Proto>
        {
            new("ssh", "SSH terminal", "user@host[:port] or a saved host"),
            new("sftp", "Files (SFTP)", "user@host[:port] — file browser"),
            new("rdp", "Remote Desktop", "user@windows-host[:port]"),
            new("serial", "Serial console", "COM3 or \"COM3 115200 8N1\""),
            new("local", "Local shell", "pwsh, cmd, wsl or \"wsl Ubuntu\""),
            new("web", "Web UI", "https://host[:port] — in-app browser"),
            new("external", "External SSH client", "user@host — opens your own ssh client"),
        };
        if (telnet) list.Insert(3, new("telnet", "Telnet (legacy)", "host [port]"));
        return list;
    }

    public static Proto Find(string id, bool telnet) => All(telnet).FirstOrDefault(p => p.Id == id) ?? All(telnet)[0];

    /// <summary>An entry (typed, pinned or recent) → its protocol and the target as typed for that protocol.</summary>
    public static (string Proto, string Target) Split(string entry)
    {
        var t = entry.Trim();
        string? After(string prefix) => t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? t[prefix.Length..].Trim() : null;
        if (After("sftp://") is { } s) return ("sftp", s);
        if (After("rdp://") is { } r) return ("rdp", r);
        if (After("mstsc ") is { } m) return ("rdp", m);
        if (After("ext://") is { } x) return ("external", x);
        if (After("ssh://") is { } h) return ("ssh", h);
        if (After("telnet://") is { } tu) return ("telnet", tu.TrimEnd('/'));
        if (After("telnet ") is { } tn) return ("telnet", tn);
        if (t.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || t.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return ("web", t);
        if (Regex.IsMatch(t, @"^COM\d{1,3}\b", RegexOptions.IgnoreCase)) return ("serial", t);
        if (!AppData.Current.Hosts.Any(hh => hh.Name.Equals(t, StringComparison.OrdinalIgnoreCase)) && Sessions.LocalShells.FromQuick(t) != null) return ("local", t);
        return ("ssh", t);
    }

    /// <summary>The one line that reopens <paramref name="target"/> with <paramref name="proto"/> (what history stores).</summary>
    public static string Canonical(string proto, string target)
    {
        target = target.Trim();
        // Typed with its own prefix ("rdp://…" while SSH is selected): the prefix wins.
        if (Split(target) is var (p, t) && p != "ssh" && p != "local" && proto is "ssh") { proto = p; target = t; }
        return proto switch
        {
            "sftp" => "sftp://" + target,
            "rdp" => "rdp://" + target,
            "external" => "ext://" + target,
            "telnet" => "telnet " + target,
            "web" => target.Contains("://") ? target : "https://" + target,
            _ => target,
        };
    }

    public sealed record Entry(string Text, string Group, bool Pinned, string? Detail = null);

    /// <summary>The dropdown: Pinned, then Recent (not pinned), then saved hosts — all filtered by what is typed.</summary>
    public static List<Entry> Dropdown(string filter, int maxHosts = 25)
    {
        var d = AppData.Current;
        bool Hit(string s) => filter.Trim().Length == 0 || s.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase);
        var list = new List<Entry>();
        list.AddRange(d.PinnedQuick.Where(Hit).Select(p => new Entry(p, "Pinned", true)));
        list.AddRange(d.RecentAdHoc.Where(r => !d.PinnedQuick.Contains(r, StringComparer.OrdinalIgnoreCase) && Hit(r)).Select(r => new Entry(r, "Recent", false)));
        foreach (var h in d.Hosts.Where(h => Hit(h.Name) || Hit(h.Address) || Hit(h.Group)).OrderBy(h => h.Group, StringComparer.OrdinalIgnoreCase).ThenBy(h => h.Display).Take(maxHosts))
        {
            var line = h.Protocol == "rdp" ? "rdp://" + h.Name : h.IsTelnet ? h.Name : h.Name;
            if (list.Any(e => e.Text.Equals(line, StringComparison.OrdinalIgnoreCase))) continue;
            list.Add(new Entry(line, "Saved hosts", d.PinnedQuick.Contains(line, StringComparer.OrdinalIgnoreCase), $"{h.Group} · {h.Username}{(h.Username.Length > 0 ? "@" : "")}{h.Address}"));
        }
        return list;
    }

    /// <summary>Pins or unpins a line (newest pin first, at most <see cref="MaxPinned"/>). True = pinned now.</summary>
    public static bool TogglePin(string line)
    {
        var p = AppData.Current.PinnedQuick;
        var i = p.FindIndex(x => x.Equals(line, StringComparison.OrdinalIgnoreCase));
        bool pinned;
        if (i >= 0) { p.RemoveAt(i); pinned = false; }
        else { p.Insert(0, line.Trim()); if (p.Count > MaxPinned) p.RemoveRange(MaxPinned, p.Count - MaxPinned); pinned = true; }
        AppData.Save();
        return pinned;
    }

    /// <summary>Adds a line to Recent (most recent first, at most <see cref="MaxRecent"/>).</summary>
    public static void Remember(string line)
    {
        var r = AppData.Current.RecentAdHoc;
        r.RemoveAll(x => x.Equals(line, StringComparison.OrdinalIgnoreCase));
        r.Insert(0, line);
        if (r.Count > MaxRecent) r.RemoveRange(MaxRecent, r.Count - MaxRecent);
        try { AppData.Save(); } catch (Exception ex) { Log.Write("Quick connect history", ex); }
    }
}
