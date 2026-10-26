using System.Text.RegularExpressions;
using DcxManager.Services;

namespace DcxManager.Sessions;

/// <summary>
/// Keyword highlighting in terminal output (0.6.29, ADR-104). Built-in packs (errors, warnings, success, network
/// addresses, HTTP status codes) plus your own rules, coloured by the terminal page with xterm.js decorations: the output
/// itself is never changed, full-screen programs (the alternate screen) are left alone, and the page spends at most a few
/// milliseconds per batch (long lines are cut at 2,000 characters, at most 30 marks a line). Patterns are checked here
/// with a time limit before they reach the page, so a pattern that backtracks badly is refused, not run.
/// </summary>
public static class Highlighting
{
    public sealed record Pack(string Id, string Title, string Example, HighlightRule[] Rules);

    private static HighlightRule R(string id, string pattern, string colour) => new() { Id = id, Name = id, Pattern = pattern, Colour = colour };

    private const string Octet = @"(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)";

    public static readonly Pack[] Packs =
    {
        new("errors", "Errors and failures", "error, failed, fatal, denied, refused, Traceback…", new[]
        {
            R("errors", @"\b(?:errors?|fail(?:ed|ure|ing)?|fatal|panic|critical|denied|refused|segfault|traceback|exception|unreachable|timed out|no such file or directory|permission denied|killed|oom-kill(?:er)?)\b", "red"),
        }),
        new("warnings", "Warnings", "warning, deprecated, retrying, degraded…", new[]
        {
            R("warnings", @"\b(?:warn(?:ing|ings)?|deprecated|retry(?:ing)?|degraded|insecure|expired?)\b", "yellow"),
        }),
        new("success", "Success", "OK, success, passed, done, active (running)…", new[]
        {
            R("success", @"\b(?:ok|success(?:ful(?:ly)?)?|succeeded|passed|done|completed?|healthy|up to date)\b|active \(running\)", "green"),
        }),
        new("network", "Network addresses", "192.0.2.10:22, 10.0.0.0/8, MAC and IPv6 addresses", new[]
        {
            R("ipv4", $@"\b{Octet}(?:\.{Octet}){{3}}(?:/\d{{1,2}})?(?::\d{{1,5}})?\b", "cyan"),
            R("mac", @"\b[0-9a-fA-F]{2}(?::[0-9a-fA-F]{2}){5}\b", "cyan"),
            R("ipv6", @"\b(?:[0-9a-fA-F]{1,4}:){3,7}[0-9a-fA-F]{1,4}\b", "cyan"),
        }),
        new("http", "HTTP status codes", "HTTP/1.1 503, \"GET /\" 404 in access logs", new[]
        {
            R("http5", @"(?<=HTTP/\d(?:\.\d)? |"" )5\d\d\b", "red"),
            R("http4", @"(?<=HTTP/\d(?:\.\d)? |"" )4\d\d\b", "yellow"),
            R("http2", @"(?<=HTTP/\d(?:\.\d)? |"" )[23]\d\d\b", "green"),
        }),
    };

    public static readonly string[] Colours = { "red", "yellow", "green", "cyan", "blue", "magenta", "grey", "white" };

    /// <summary>The terminal theme key of a colour (the page reads the scheme's own value, so it follows the profile).</summary>
    public static string ThemeKey(string colour) => colour switch
    {
        "red" => "red", "green" => "green", "cyan" => "cyan", "blue" => "blue", "magenta" => "magenta", "grey" => "brightBlack", "white" => "brightWhite", _ => "yellow",
    };

    /// <summary>Why a pattern can't be used (empty, invalid, or too slow on a long line), or null when it's fine.</summary>
    public static string? Check(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return "Enter a pattern.";
        if (pattern.Length > 500) return "Keep the pattern under 500 characters.";
        if (pattern.Contains("(?i") || pattern.Contains("(?s") || pattern.Contains("(?m") || pattern.Contains("(?x")) return "Inline options like (?i) aren't supported: use the Match case box.";
        try
        {
            var rx = new Regex(pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(150));
            if (rx.IsMatch("")) return "The pattern matches empty text: it would mark everything.";
            foreach (var probe in new[] { new string('a', 2000) + "!", string.Concat(Enumerable.Repeat("ab", 1000)) + "\u0001", new string(' ', 2000) + "x", "192.168.0.1 " + new string('1', 1990) })
                rx.Matches(probe).Count.ToString();
        }
        catch (RegexMatchTimeoutException) { return "The pattern is too slow on long lines (it backtracks): simplify it."; }
        catch (ArgumentException ex) { return "Not a valid regular expression: " + ex.Message; }
        return null;
    }

    /// <summary>The rules that are on now: the chosen packs, then your own enabled, valid rules (yours draw on top).</summary>
    public static List<HighlightRule> Active(AppSettings s)
    {
        var list = new List<HighlightRule>();
        if (!s.TerminalHighlight) return list;
        foreach (var p in Packs.Where(p => s.TerminalHighlightPacks.Contains(p.Id, StringComparer.OrdinalIgnoreCase))) list.AddRange(p.Rules);
        list.AddRange(s.TerminalHighlightRules.Where(r => r.Enabled && Check(r.Pattern) == null));
        return list;
    }

    /// <summary>The message for the terminal page.</summary>
    public static object PageMessage(AppSettings s) => new
    {
        t = "hl",
        on = s.TerminalHighlight,
        links = s.TerminalLinks,
        rules = Active(s).Select(r => new { id = r.Id, p = r.Pattern, cs = r.CaseSensitive, c = ThemeKey(r.Colour), a = r.Alert }).ToArray(),
    };

    /// <summary>A stable fingerprint of what the page was sent (a settings save resends only when it changed).</summary>
    public static string Signature(AppSettings s) =>
        $"{s.TerminalHighlight}|{s.TerminalLinks}|{string.Join(",", s.TerminalHighlightPacks)}|" + string.Join("|", s.TerminalHighlightRules.Select(r => $"{r.Id}:{r.Enabled}:{r.Pattern}:{r.Colour}:{r.CaseSensitive}:{r.Alert}"));

    // ---- clickable words (Ctrl+click) -----------------------------------------------------------------------------------

    private static readonly Regex Ipv4Only = new($@"^{Octet}(?:\.{Octet}){{3}}$", RegexOptions.Compiled);

    /// <summary>What a Ctrl+clicked word is: "ip" (with an optional :port), "path" (absolute), "url" (http/https) or null.</summary>
    public static (string Kind, string Value, int Port)? Classify(string? text)
    {
        var t = (text ?? "").Trim().TrimEnd('.', ',', ';', ')', ']', '\'', '"');
        if (t.Length is 0 or > 2048) return null;
        if (Uri.TryCreate(t, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)) return ("url", u.AbsoluteUri, 0);
        var ip = t;
        int port = 0;
        if (t.LastIndexOf(':') is > 6 and var colon && int.TryParse(t[(colon + 1)..], out var p) && p is > 0 and <= 65535) { ip = t[..colon]; port = p; }
        if (ip.Contains('/')) ip = ip[..ip.IndexOf('/')];
        if (Ipv4Only.IsMatch(ip)) return ("ip", ip, port);
        if (t.StartsWith('/') && t.Length > 1 && !t.Any(char.IsControl)) return ("path", t, 0);
        return null;
    }
}
