using System.Text.RegularExpressions;

namespace DcxManager.Sessions;

/// <summary>
/// Where a terminal's shell is, read from what it printed (0.6.29, saved sessions): nothing is ever typed into the shell
/// to ask. In order of trust:
/// <list type="bullet">
/// <item>OSC 7 (<c>ESC ] 7 ; file://host/path</c>), which many shells and prompts send on every prompt;</item>
/// <item>the last POSIX prompt in the Debian / Ubuntu shape <c>user@host:/path$ </c> (or <c>~/path</c>);</item>
/// <item>the last PowerShell prompt <c>PS C:\path&gt; </c>.</item>
/// </list>
/// A prompt that only shows the folder's name (Red Hat's <c>[user@host dir]$</c>) can't say where it is: nothing is saved.
/// Also whether a tmux session was left: tmux prints "[detached …]" or "[exited]" when you leave it.
/// </summary>
public static class ShellPlace
{
    private static readonly Regex Osc7 = new(@"\x1b\]7;file://[^/\x07\x1b]*(?<p>/[^\x07\x1b]*)(?:\x07|\x1b\\)", RegexOptions.Compiled);
    private static readonly Regex Ansi = new(@"\x1b\[[0-?]*[ -/]*[@-~]|\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)|\x1b[@-Z\\-_]", RegexOptions.Compiled);
    private static readonly Regex PosixPrompt = new(@"(?:^|[\r\n])(?:\([^)\r\n]{1,40}\) )?[A-Za-z0-9_.-]{1,64}@[A-Za-z0-9_.-]{1,253}:(?<p>~(?:/[^\r\n$#]*)?|/[^\r\n$#]*)[$#] ", RegexOptions.Compiled);
    private static readonly Regex PsPrompt = new(@"(?:^|[\r\n])PS (?<p>[A-Za-z]:\\[^\r\n>]*)> ", RegexOptions.Compiled);
    private static readonly Regex TmuxLeft = new(@"\[(?:detached(?: \(from session [^\]\r\n]*\))?|exited)\]", RegexOptions.Compiled);

    /// <summary>The working folder the output last showed, or null. <paramref name="shell"/> is "posix" or "powershell".</summary>
    public static string? FromOutput(string raw, string shell)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        string? found = null;
        if (shell == "posix")
        {
            var osc = Osc7.Matches(raw);
            if (osc.Count > 0) found = Uri.UnescapeDataString(osc[^1].Groups["p"].Value);
            else
            {
                var m = PosixPrompt.Matches(Ansi.Replace(raw, ""));
                if (m.Count > 0) found = m[^1].Groups["p"].Value.TrimEnd();
            }
        }
        else if (shell == "powershell")
        {
            var m = PsPrompt.Matches(Ansi.Replace(raw, ""));
            if (m.Count > 0) found = m[^1].Groups["p"].Value.TrimEnd();
        }
        return Usable(found) ? found : null;
    }

    private static bool Usable(string? p) => p is { Length: > 0 and <= 1024 } && !p.Any(char.IsControl);

    // A line the cursor sits on that reads like a shell prompt: "user@host:~$ ", "[root@db ~]# ", "PS C:\> ", "% ", "❯ "…
    private static readonly Regex PromptEnd = new(@"[$#%>❯»]\s?$", RegexOptions.Compiled);

    /// <summary>Whether a screen line (the cursor's) looks like a shell waiting at its prompt (0.6.29 alerts: a long command
    /// finished). Questions (y/N, password:) and empty lines don't.</summary>
    public static bool LooksLikePrompt(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        var l = Ansi.Replace(line, "").TrimEnd();
        if (l.Length is 0 or > 300) return false;
        return PromptEnd.IsMatch(l);
    }

    /// <summary>The last line of raw output as it would show (escape sequences removed, a carriage return overwrites).</summary>
    public static string LastLine(string raw)
    {
        var text = Ansi.Replace(raw ?? "", "");
        var nl = text.LastIndexOf('\n');
        var line = nl >= 0 ? text[(nl + 1)..] : text;
        var parts = line.Split('\r');
        return parts.LastOrDefault(p => p.Length > 0) ?? "";
    }

    /// <summary>True when the output shows tmux was left (detached or exited) after it was last entered.</summary>
    public static bool LeftTmux(string raw) => TmuxLeft.IsMatch(Ansi.Replace(raw, ""));

    /// <summary>The line that goes back to <paramref name="path"/>, quoted so no character of it is interpreted. POSIX lines
    /// start with a space, which keeps them out of the history where HISTCONTROL ignores spaces (the Debian default).</summary>
    public static string? ChangeDirLine(string? path, string shell)
    {
        if (!Usable(path)) return null;
        if (shell == "powershell") return $"Set-Location -LiteralPath '{path!.Replace("'", "''")}'";
        if (shell != "posix") return null;
        static string Q(string s) => "'" + s.Replace("'", "'\\''") + "'";
        if (path == "~") return " cd ~";
        if (path!.StartsWith("~/")) return " cd -- ~/" + Q(path[2..]);
        return path.StartsWith('/') ? " cd -- " + Q(path) : null;
    }
}
