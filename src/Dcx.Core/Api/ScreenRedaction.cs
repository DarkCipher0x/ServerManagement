using System.Text.RegularExpressions;
using DcxManager.Services;

namespace DcxManager.Api;

// Screen text and how it is made safe to show elsewhere (a broadcast, an add-on): moved out of Broadcasts.cs in 0.2.0
// phase 6, since add-ons below the app use it too; the broadcast sources and server stay in the app.

/// <summary>Turns a screen into broadcastable text: crop, then always mask secrets / private keys / long key-like
/// strings, optionally IPs, e-mail and custom patterns, and detect password prompts on the cursor row.</summary>
public static class BroadcastRedactor
{
    private static readonly Regex KeyLike = new(@"(?<![\w/.\-])[A-Za-z0-9+/=_\-]{40,}(?![\w/.\-])", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Ipv4 = new(@"\b(?:25[0-5]|2[0-4]\d|1?\d?\d)(?:\.(?:25[0-5]|2[0-4]\d|1?\d?\d)){3}\b", RegexOptions.Compiled);
    private static readonly Regex Ipv6 = new(@"(?<![\w:])(?:[0-9A-Fa-f]{1,4}:){2,7}[0-9A-Fa-f]{0,4}(?![\w:])|(?<![\w:])::(?:[0-9A-Fa-f]{1,4}:){0,6}[0-9A-Fa-f]{1,4}(?![\w:])", RegexOptions.Compiled);
    private static readonly Regex Email = new(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.Compiled);
    public static readonly Regex Prompt = new(@"(?i)((password|passphrase|passcode|pin|verification code|one-time code|otp)[^:\n]{0,40}:\s*$|\[sudo\]|enter pin|token:\s*$)", RegexOptions.Compiled);

    public static (string[] Lines, bool Hidden) Apply(ScreenText s, BroadcastOptions o, Regex[] custom)
    {
        var src = s.Lines;
        int cursor = s.CursorLine;
        int start = 0;
        if (o.Rows > 0)
        {
            // Crop to the last N rows that hold anything (or end at the cursor).
            int last = Math.Max(cursor, Array.FindLastIndex(src, l => l.Trim().Length > 0));
            last = Math.Clamp(last, 0, Math.Max(0, src.Length - 1));
            start = Math.Max(0, last - o.Rows + 1);
            src = src.Skip(start).Take(last - start + 1).ToArray();
        }
        var lines = src.Select(l => l.TrimEnd()).ToArray();
        bool inKey = false;
        for (int i = 0; i < lines.Length; i++)
        {
            var l = lines[i];
            if (l.Contains("-----BEGIN") && l.Contains("PRIVATE KEY")) { inKey = !l.Contains("-----END"); lines[i] = "[REDACTED PRIVATE KEY]"; continue; }
            if (inKey) { if (l.Contains("-----END")) inKey = false; lines[i] = "[REDACTED PRIVATE KEY]"; continue; }
            l = Redaction.Mask(l);
            l = KeyLike.Replace(l, m => LooksLikeKey(m.Value) ? "[REDACTED]" : m.Value);
            if (o.MaskIps) { l = Ipv4.Replace(l, "•.•.•.•"); l = Ipv6.Replace(l, m => m.Value.Count(c => c == ':') >= 2 && m.Value.Any(char.IsLetterOrDigit) ? "•:•:•" : m.Value); }
            if (o.MaskEmails) l = Email.Replace(l, "•@•");
            foreach (var rx in custom)
            {
                try { l = rx.Replace(l, "[REDACTED]"); }
                catch (RegexMatchTimeoutException) { l = "[REDACTED — pattern timed out]"; }
            }
            lines[i] = l;
        }
        bool hidden = false;
        if (o.HidePasswordPrompts)
        {
            var row = cursor >= 0 && cursor < s.Lines.Length ? s.Lines[cursor] : s.Lines.LastOrDefault(l => l.Trim().Length > 0) ?? "";
            hidden = Prompt.IsMatch(row.TrimEnd());
        }
        return (lines, hidden);
    }

    /// <summary>Long base64/hex-ish runs with mixed character classes: keys, tokens, hashes. Paths and plain words aren't.</summary>
    private static bool LooksLikeKey(string v) => v.Count(c => c == '/') < 3 && v.Any(char.IsDigit) && v.Any(char.IsUpper) && v.Any(char.IsLower);
}

/// <summary>Visible screen text from a source: rows top to bottom, and the cursor row (-1 = unknown).</summary>
public sealed record ScreenText(string[] Lines, int CursorLine);

public sealed class BroadcastOptions
{
    /// <summary>dark | light | transparent</summary>
    public string Theme { get; set; } = "dark";
    public int FontPx { get; set; } = 18;
    /// <summary>Only the last N rows (0 = all visible rows).</summary>
    public int Rows { get; set; }
    public bool ShowTitle { get; set; } = true;
    public bool MaskIps { get; set; }
    public bool MaskEmails { get; set; }
    public List<string> Patterns { get; set; } = new();
    public bool HidePasswordPrompts { get; set; } = true;
    public int Minutes { get; set; } = 240;
}
