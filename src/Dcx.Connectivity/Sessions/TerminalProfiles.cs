using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DcxManager.Services;

namespace DcxManager.Sessions;

/// <summary>A colour scheme for xterm.js: 16 ANSI colours plus background, foreground, cursor and selection.</summary>
public sealed record TermScheme(string Title, string Bg, string Fg, string Cursor, string Selection, string[] Ansi);

public static class TerminalProfiles
{
    static TerminalProfiles() => System.Text.Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static readonly Dictionary<string, TermScheme> Schemes = new()
    {
        ["classic-dark"] = new("Classic (xterm)", "#000000", "#e5e5e5", "#e5e5e5", "#ffffff40", new[]
            { "#000000", "#cd0000", "#00cd00", "#cdcd00", "#0000ee", "#cd00cd", "#00cdcd", "#e5e5e5", "#7f7f7f", "#ff0000", "#00ff00", "#ffff00", "#5c5cff", "#ff00ff", "#00ffff", "#ffffff" }),
        ["solarized-dark"] = new("Solarized dark", "#002b36", "#839496", "#93a1a1", "#073642", new[]
            { "#073642", "#dc322f", "#859900", "#b58900", "#268bd2", "#d33682", "#2aa198", "#eee8d5", "#002b36", "#cb4b16", "#586e75", "#657b83", "#839496", "#6c71c4", "#93a1a1", "#fdf6e3" }),
        ["solarized-light"] = new("Solarized light", "#fdf6e3", "#657b83", "#586e75", "#eee8d5", new[]
            { "#073642", "#dc322f", "#859900", "#b58900", "#268bd2", "#d33682", "#2aa198", "#eee8d5", "#002b36", "#cb4b16", "#586e75", "#657b83", "#839496", "#6c71c4", "#93a1a1", "#fdf6e3" }),
        ["green-screen"] = new("Green screen", "#001100", "#33ff66", "#33ff66", "#33ff6640", new[]
            { "#001100", "#33ff66", "#33ff66", "#33ff66", "#33ff66", "#33ff66", "#33ff66", "#33ff66", "#1a8033", "#66ff99", "#66ff99", "#66ff99", "#66ff99", "#66ff99", "#66ff99", "#ccffdd" }),
    };

    public static readonly string[] CursorStyles = { "block", "underline", "bar" };
    public static readonly (string Id, string Title)[] Encodings = { ("utf-8", "UTF-8"), ("iso-8859-1", "ISO-8859-1 (Latin-1)"), ("windows-1252", "Windows-1252"), ("ibm437", "IBM437 (DOS box drawing)") };

    public static Encoding EncodingFor(string? id) => id switch
    {
        "iso-8859-1" => System.Text.Encoding.Latin1,
        "windows-1252" => System.Text.Encoding.GetEncoding(1252),
        "ibm437" => System.Text.Encoding.GetEncoding(437),
        _ => new UTF8Encoding(false),
    };

    private static AppSettings S => AppSettings.Current;

    /// <summary>All profiles; the list always holds at least the default one.</summary>
    public static List<TerminalProfile> All
    {
        get
        {
            if (S.TerminalProfiles.Count == 0) S.TerminalProfiles.Add(new TerminalProfile { Name = "Default" });
            return S.TerminalProfiles;
        }
    }

    public static TerminalProfile Default => All.FirstOrDefault(p => p.Id == S.DefaultTerminalProfileId) ?? All[0];
    public static TerminalProfile? Find(string? id) => id == null ? null : All.FirstOrDefault(p => p.Id == id);

    /// <summary>The profile a new tab for <paramref name="host"/> uses (the host's choice, else the default).</summary>
    public static TerminalProfile For(HostEntry? host) => Find(host == null ? null : Envs.Live(host).TerminalProfileId) ?? Default;

    public static int ScrollbackFor(TerminalProfile p) => Math.Clamp(p.Scrollback > 0 ? p.Scrollback : S.Scrollback, 1000, 200_000);

    // ---- key mappings ---------------------------------------------------------------------------------------------------

    private static readonly string[] Named = { "Enter", "Tab", "Escape", "Backspace", "Space", "Insert", "Delete", "Home", "End", "PageUp", "PageDown", "Up", "Down", "Left", "Right" };

    /// <summary>"ctrl+shift+f5" → "Ctrl+Shift+F5" (modifiers in Ctrl, Alt, Shift order), or null when it isn't a chord we
    /// allow. A plain printable key needs Ctrl or Alt, so a mapping can never swallow ordinary typing.</summary>
    public static string? NormalizeKeys(string? chord)
    {
        if (string.IsNullOrWhiteSpace(chord)) return null;
        var parts = chord.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Length > 4) return null;
        bool ctrl = false, alt = false, shift = false;
        foreach (var m in parts[..^1])
        {
            switch (m.ToLowerInvariant())
            {
                case "ctrl" or "control": ctrl = true; break;
                case "alt": alt = true; break;
                case "shift": shift = true; break;
                default: return null;
            }
        }
        var k = parts[^1];
        string key;
        if (k.Length == 1 && char.IsLetterOrDigit(k[0]) && k[0] < 128) key = k.ToUpperInvariant();
        else if (Regex.IsMatch(k, "^[Ff]([1-9]|1[0-9]|2[0-4])$")) key = "F" + k[1..];
        else if (Named.FirstOrDefault(n => n.Equals(k, StringComparison.OrdinalIgnoreCase)) is { } n) key = n;
        else return null;
        bool special = key.StartsWith('F') && key.Length > 1 || Named.Contains(key) && key is not ("Space" or "Enter" or "Tab" or "Backspace");
        if (!special && !ctrl && !alt) return null;
        return (ctrl ? "Ctrl+" : "") + (alt ? "Alt+" : "") + (shift ? "Shift+" : "") + key;
    }

    /// <summary>Escapes: \e (ESC), \r, \n, \t, \\, \xHH, \uHHHH and ^X (control character). Null when malformed or over 256 characters.</summary>
    public static string? ParseSend(string? text)
    {
        if (text == null) return null;
        var sb = new StringBuilder();
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '^' && i + 1 < text.Length && text[i + 1] is >= '@' and <= '_' or >= 'a' and <= 'z')
            { sb.Append((char)(char.ToUpperInvariant(text[++i]) - '@')); continue; }
            if (c != '\\') { sb.Append(c); continue; }
            if (++i >= text.Length) return null;
            switch (text[i])
            {
                case 'e': sb.Append('\x1b'); break;
                case 'r': sb.Append('\r'); break;
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case '\\': sb.Append('\\'); break;
                case '^': sb.Append('^'); break;
                case 'x' when i + 2 < text.Length && int.TryParse(text.AsSpan(i + 1, 2), NumberStyles.HexNumber, null, out var x): sb.Append((char)x); i += 2; break;
                case 'u' when i + 4 < text.Length && int.TryParse(text.AsSpan(i + 1, 4), NumberStyles.HexNumber, null, out var u): sb.Append((char)u); i += 4; break;
                default: return null;
            }
            if (sb.Length > 256) return null;
        }
        return sb.Length is > 0 and <= 256 ? sb.ToString() : null;
    }

    /// <summary>"Ctrl+Shift+F5 = \e[15;6~" lines → mappings. Returns the problems (line numbers) instead of guessing.</summary>
    public static (List<KeyMap> Maps, List<string> Errors) ParseKeyLines(string text)
    {
        var maps = new List<KeyMap>(); var errors = new List<string>();
        var lines = text.Replace("\r", "").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var l = lines[i].Trim();
            if (l.Length == 0 || l.StartsWith('#')) continue;
            int eq = l.IndexOf('=');
            if (eq <= 0) { errors.Add($"line {i + 1}: use “keys = text”"); continue; }
            var keys = NormalizeKeys(l[..eq]);
            var send = l[(eq + 1)..].Trim();
            if (keys == null) { errors.Add($"line {i + 1}: “{l[..eq].Trim()}” isn't a key chord this allows (plain letters need Ctrl or Alt)"); continue; }
            if (ParseSend(send) == null) { errors.Add($"line {i + 1}: the text to send is empty, too long or has a bad escape"); continue; }
            if (maps.Any(m => m.Keys == keys)) { errors.Add($"line {i + 1}: {keys} is mapped twice"); continue; }
            maps.Add(new KeyMap { Keys = keys, Send = send });
        }
        return (maps, errors);
    }

    public static string KeyLines(IEnumerable<KeyMap> maps) => string.Join("\n", maps.Select(m => $"{m.Keys} = {m.Send}"));

    /// <summary>The message the terminal page applies (theme null = keep following the app theme).</summary>
    public static object PageMessage(TerminalProfile p)
    {
        Schemes.TryGetValue(p.Scheme, out var s);
        return new
        {
            t = "profile",
            scheme = s == null ? null : new { bg = s.Bg, fg = s.Fg, cursor = s.Cursor, sel = s.Selection, ansi = s.Ansi },
            cursorStyle = CursorStyles.Contains(p.CursorStyle) ? p.CursorStyle : "block",
            blink = p.CursorBlink,
            scrollback = ScrollbackFor(p),
            bs = p.BackspaceCtrlH,
            keys = p.Keys.Select(k => (NormalizeKeys(k.Keys), ParseSend(k.Send))).Where(x => x.Item1 != null && x.Item2 != null).Select(x => new { k = x.Item1, d = x.Item2 }).ToArray(),
        };
    }
}
