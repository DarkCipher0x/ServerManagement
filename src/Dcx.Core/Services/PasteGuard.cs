using System.Text;
using System.Text.RegularExpressions;

namespace DcxManager.Services;

/// <summary>
/// Inspects text before it is pasted into a terminal. The clipboard can hold more than it shows: several lines that
/// each run as soon as they arrive, hidden control characters (an embedded ESC[201~ ends bracketed paste early and
/// lets the rest execute), or a destructive one-liner copied from a web page. Pure logic: TerminalView asks the user.
/// </summary>
public static class PasteGuard
{
    public sealed record Result(string Clean, int Lines, bool EndsWithNewline, int RemovedControls, IReadOnlyList<string> Risks)
    {
        public bool Multiline => Lines > 1;
        /// <summary>Ask when it is more than a single plain line, or anything looks risky.</summary>
        public bool NeedsConfirm => Multiline || EndsWithNewline || RemovedControls > 0 || Risks.Count > 0 || Clean.Length > LargeChars;
    }

    public const int LargeChars = 100_000;

    private static readonly (Regex Re, string Why)[] Risky =
    {
        (new(@"\brm\s+(-[a-zA-Z]*[rf][a-zA-Z]*\s+)+(/|~|\*|/\*|\$HOME)(\s|$)", RegexOptions.Compiled), "recursive delete of /, ~ or *"),
        (new(@"\bmkfs(\.\w+)?\b", RegexOptions.Compiled), "formats a file system (mkfs)"),
        (new(@"\bdd\s+[^\n]*\bof=/dev/", RegexOptions.Compiled), "writes a raw device (dd of=/dev/…)"),
        (new(@">\s*/dev/(sd|nvme|vd|hd|xvd)", RegexOptions.Compiled), "overwrites a disk device"),
        (new(@":\(\)\s*\{\s*:\|:&\s*\};:", RegexOptions.Compiled), "fork bomb"),
        (new(@"\b(curl|wget)\b[^\n|]*\|\s*(sudo\s+)?(ba|z|da)?sh\b", RegexOptions.Compiled), "downloads and runs a script (curl | sh)"),
        // Only as a command (line start, after ; & | or sudo), so "grep reboot syslog" is not flagged.
        (new(@"(^|[;&|]\s*|\bsudo\s+)\s*(shutdown|poweroff|halt|reboot)\b|\bsystemctl\s+(reboot|poweroff|halt)\b", RegexOptions.Compiled | RegexOptions.Multiline), "shuts down or reboots"),
        (new(@"\bchmod\s+-R\s+0?777\s+/(\s|$)", RegexOptions.Compiled), "chmod -R 777 /"),
        (new(@"\b(iptables\s+-F|ufw\s+disable|nft\s+flush\s+ruleset)\b", RegexOptions.Compiled), "drops firewall rules"),
        (new(@"\bsystemctl\s+(stop|disable|mask)\s+(ssh|sshd)\b", RegexOptions.Compiled), "stops SSH (you may lock yourself out)"),
    };

    public static Result Check(string text)
    {
        text ??= "";
        var sb = new StringBuilder(text.Length);
        int removed = 0;
        foreach (var ch in text)
        {
            // Keep tab, CR, LF; drop every other C0 control, DEL and the C1 range (ESC sequences, CSI 0x9B, etc.).
            if (ch is '\t' or '\r' or '\n' || (ch >= 0x20 && ch != 0x7F && !(ch >= 0x80 && ch <= 0x9F))) sb.Append(ch);
            else removed++;
        }
        var clean = sb.ToString();
        var normalized = clean.Replace("\r\n", "\n").Replace('\r', '\n');
        bool endsNl = normalized.EndsWith('\n');
        int lines = normalized.TrimEnd('\n').Split('\n').Length;
        var risks = Risky.Where(r => r.Re.IsMatch(normalized)).Select(r => r.Why).Distinct().ToList();
        return new Result(clean, normalized.Length == 0 ? 0 : lines, endsNl, removed, risks);
    }

    /// <summary>The text without its trailing line break(s), so nothing runs until the user presses Enter.</summary>
    public static string WithoutFinalNewline(string s) => s.TrimEnd('\r', '\n');

    /// <summary>Tracks bracketed-paste mode (DECSET 2004) from terminal output: the last ESC[?2004h / ESC[?2004l wins.</summary>
    public static bool? BracketedFrom(string output)
    {
        int on = output.LastIndexOf("\x1b[?2004h", StringComparison.Ordinal), off = output.LastIndexOf("\x1b[?2004l", StringComparison.Ordinal);
        if (on < 0 && off < 0) return null;
        return on > off;
    }

    /// <summary>The confirmation text: what will happen, honestly, given the shell's paste mode.</summary>
    public static string Describe(Result r, bool bracketed, string target)
    {
        var sb = new StringBuilder();
        var preview = r.Clean.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Take(8).Select(l => l.Length > 110 ? l[..110] + "…" : l);
        sb.AppendLine($"Paste {r.Lines:N0} line(s), {r.Clean.Length:N0} characters into {target}:");
        sb.AppendLine();
        foreach (var l in preview) sb.AppendLine("  " + l);
        if (r.Lines > 8) sb.AppendLine($"  … {r.Lines - 8:N0} more line(s)");
        sb.AppendLine();
        if (bracketed) sb.AppendLine("The shell has bracketed paste on: the text is inserted as one block and nothing runs until you press Enter.");
        else if (r.Multiline || r.EndsWithNewline) sb.AppendLine("⚠ The shell does not report bracketed paste: every line break acts as Enter, so each line runs as soon as it arrives.");
        if (r.RemovedControls > 0) sb.AppendLine($"⚠ {r.RemovedControls} hidden control character(s) were removed (they can hide commands or end bracketed paste early).");
        foreach (var why in r.Risks) sb.AppendLine("⚠ Looks risky: " + why + ".");
        if (r.Clean.Length > LargeChars) sb.AppendLine("⚠ Very large paste.");
        return sb.ToString().TrimEnd();
    }
}
