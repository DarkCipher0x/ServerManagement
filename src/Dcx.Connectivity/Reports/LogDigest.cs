using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DcxManager.Reports;

/// <summary>A cluster of log lines that differ only in variable parts (numbers, IPs, ids, paths under /proc …).</summary>
public sealed class LogPattern
{
    public string Template { get; init; } = "";
    public string Example { get; set; } = "";
    public string Source { get; set; } = "";
    public int Count { get; set; }
    public string Severity { get; set; } = "info";
    public DateTime? First { get; set; }
    public DateTime? Last { get; set; }
    public HashSet<string> Ips { get; } = new();
}

/// <summary>
/// Deterministic, CPU-cheap log reduction — no model needed. Thousands of journal lines become a short ranked list of
/// patterns with counts, severities and time spans. This is the offline "AI off" analysis, and it is what we send to a
/// model instead of raw logs (far fewer tokens, so small CPU/NPU models can still do useful work).
/// </summary>
public static class LogDigest
{
    private static readonly Regex IsoTs = new(@"^(\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:[.,]\d+)?(?:Z|[+-]\d{2}:?\d{2})?)\s+", RegexOptions.Compiled);
    private static readonly Regex SyslogTs = new(@"^([A-Z][a-z]{2}\s+\d{1,2}\s\d{2}:\d{2}:\d{2})\s+", RegexOptions.Compiled);
    private static readonly Regex HostProc = new(@"^(\S+)\s+([\w\-./@]+?)(?:\[\d+\])?:\s", RegexOptions.Compiled);
    private static readonly Regex Ipv4 = new(@"\b(?:\d{1,3}\.){3}\d{1,3}\b", RegexOptions.Compiled);
    private static readonly Regex Ipv6 = new(@"\b(?:[0-9a-fA-F]{1,4}:){3,7}[0-9a-fA-F]{1,4}\b", RegexOptions.Compiled);
    private static readonly Regex Uuid = new(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b", RegexOptions.Compiled);
    private static readonly Regex Hex = new(@"\b0x[0-9a-fA-F]+\b|\b[0-9a-fA-F]{12,}\b", RegexOptions.Compiled);
    private static readonly Regex Num = new(@"(?<![A-Za-z])\d+(?:\.\d+)?", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s{2,}", RegexOptions.Compiled);

    private static readonly (Regex Re, string Sev)[] Severity =
    {
        (new Regex(@"(?i)\b(panic|emerg|fatal|kernel bug|oops|out of memory|oom-kill|killed process|segfault|i/o error|corrupt|filesystem read-only|ext4-fs error|xfs.*(error|corrupt))", RegexOptions.Compiled), "critical"),
        (new Regex(@"(?i)\b(error|err|fail(ed|ure)?|denied|refused|unable|cannot|can't|timed? ?out|unreachable|exception|traceback|invalid user|authentication failure|break-in)", RegexOptions.Compiled), "error"),
        (new Regex(@"(?i)\b(warn(ing)?|deprecated|retry|retrying|degraded|throttl|slow|high)", RegexOptions.Compiled), "warning"),
    };

    public static int SeverityRank(string s) => s switch { "critical" => 4, "error" => 3, "warning" => 2, "notice" => 1, _ => 0 };

    public static List<LogPattern> Digest(string text, int maxPatterns = 40)
    {
        var map = new Dictionary<string, LogPattern>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith("-- ")) continue;   // journalctl "-- No entries --" / boot markers
            DateTime? ts = null;
            var rest = line;
            var m = IsoTs.Match(rest);
            if (m.Success) { ts = ParseTs(m.Groups[1].Value); rest = rest[m.Length..]; }
            else if ((m = SyslogTs.Match(rest)).Success) { ts = ParseSyslog(m.Groups[1].Value); rest = rest[m.Length..]; }
            string source = "";
            var hp = HostProc.Match(rest);
            if (hp.Success && ts != null) { source = hp.Groups[2].Value; rest = rest[hp.Length..]; }

            var ips = Ipv4.Matches(rest).Select(x => x.Value).ToList();
            var tpl = Uuid.Replace(rest, "<uuid>");
            tpl = Ipv6.Replace(tpl, "<ip6>");
            tpl = Ipv4.Replace(tpl, "<ip>");
            tpl = Hex.Replace(tpl, "<hex>");
            tpl = Num.Replace(tpl, "<n>");
            tpl = Spaces.Replace(tpl, " ").Trim();
            if (tpl.Length > 240) tpl = tpl[..240] + "…";
            var key = source + "|" + tpl;
            if (!map.TryGetValue(key, out var p))
            {
                p = map[key] = new LogPattern { Template = tpl, Example = line.Length > 400 ? line[..400] + "…" : line, Source = source, Severity = Classify(rest) };
            }
            p.Count++;
            foreach (var ip in ips) if (p.Ips.Count < 50) p.Ips.Add(ip);
            if (ts != null)
            {
                if (p.First == null || ts < p.First) p.First = ts;
                if (p.Last == null || ts > p.Last) { p.Last = ts; p.Example = line.Length > 400 ? line[..400] + "…" : line; }
            }
        }
        // Rank: severity first, then volume.
        return map.Values.OrderByDescending(p => SeverityRank(p.Severity)).ThenByDescending(p => p.Count).Take(maxPatterns).ToList();
    }

    public static string Classify(string line)
    {
        foreach (var (re, sev) in Severity) if (re.IsMatch(line)) return sev;
        return "info";
    }

    /// <summary>Compact text form for prompts and reports.</summary>
    public static string Render(IEnumerable<LogPattern> patterns, int maxChars = 6000)
    {
        var sb = new StringBuilder();
        foreach (var p in patterns)
        {
            var span = p.First != null ? $" {p.First:MM-dd HH:mm}→{p.Last:MM-dd HH:mm}" : "";
            var ips = p.Ips.Count > 0 ? $" ips:{string.Join(",", p.Ips.Take(5))}{(p.Ips.Count > 5 ? $"(+{p.Ips.Count - 5})" : "")}" : "";
            var line = $"[{p.Severity}] ×{p.Count}{span} {(p.Source.Length > 0 ? p.Source + ": " : "")}{p.Template}{ips}";
            if (sb.Length + line.Length > maxChars) { sb.AppendLine("…(more patterns omitted)"); break; }
            sb.AppendLine(line);
        }
        return sb.ToString();
    }

    private static DateTime? ParseTs(string s) =>
        DateTimeOffset.TryParse(s.Replace(',', '.'), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d) ? d.LocalDateTime
        : DateTimeOffset.TryParseExact(s, "yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out d) ? d.LocalDateTime
        : DateTime.TryParseExact(s[..Math.Min(19, s.Length)].Replace('T', ' '), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? dt : null;

    private static DateTime? ParseSyslog(string s) =>
        DateTime.TryParseExact(Regex.Replace(s, @"\s+", " "), "MMM d HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var d) ? d : null;
}
