using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace DcxManager.Reports;

/// <summary>One Windows event as the report sees it.</summary>
public sealed record WinEvent(DateTime Time, int Level, string Provider, int Id, string Log, string Message)
{
    public string LevelText => Level switch { 1 => "critical", 2 => "error", 3 => "warning", 4 => "information", 5 => "verbose", _ => "level " + Level };
}

/// <summary>
/// The Windows Event Viewer for the reports (0.4.0, DarkCipher0x): events are read with Windows' own <c>wevtutil.exe</c> (nothing
/// else ships), as XML, filtered by an XPath the app builds from a log name, levels and a time window — the log name is
/// validated (letters, digits and the separators event logs use), the numbers are numbers, nothing typed reaches a
/// command line. Exports go through the same tool: <c>epl</c> writes a real .evtx that opens in Event Viewer.
/// </summary>
public static class WindowsEvents
{
    /// <summary>The logs offered by default (others can be typed, e.g. "Microsoft-Windows-TaskScheduler/Operational").</summary>
    public static readonly string[] CommonLogs = { "System", "Application", "Security", "Setup", "Microsoft-Windows-Windows Defender/Operational", "Microsoft-Windows-TerminalServices-LocalSessionManager/Operational", "Microsoft-Windows-PowerShell/Operational" };

    private static readonly Regex LogRx = new(@"^[A-Za-z0-9][A-Za-z0-9 ._/\-]{0,255}$");

    /// <summary>A log name as Event Viewer spells it; anything else is refused before any command is built.</summary>
    public static string CheckLog(string log)
    {
        log = log.Trim();
        if (!LogRx.IsMatch(log) || log.Contains("..", StringComparison.Ordinal)) throw new ArgumentException($"“{log}” isn't an event log name (letters, digits, spaces, . _ / -).");
        return log;
    }

    /// <summary>The XPath for events of the given levels (1 critical … 5 verbose) within the last <paramref name="window"/>,
    /// optionally only some event ids.</summary>
    public static string Query(IEnumerable<int> levels, TimeSpan window, IEnumerable<int>? ids = null)
    {
        var lv = levels.Where(l => l is >= 0 and <= 5).Distinct().OrderBy(l => l).ToList();
        if (lv.Count == 0) throw new ArgumentException("at least one level");
        long ms = (long)Math.Clamp(window.TotalMilliseconds, 60_000, 90L * 24 * 3600 * 1000);
        var parts = new List<string> { "(" + string.Join(" or ", lv.Select(l => $"Level={l}")) + ")", $"TimeCreated[timediff(@SystemTime) <= {ms}]" };
        if (ids != null && ids.Any()) parts.Insert(1, "(" + string.Join(" or ", ids.Select(i => $"EventID={Math.Clamp(i, 0, 65535)}")) + ")");
        return "*[System[" + string.Join(" and ", parts) + "]]";
    }

    /// <summary>Test seam: what runs wevtutil (the self-test feeds XML from a string).</summary>
    internal static Func<string[], CancellationToken, Task<(int Code, string Out, string Err)>> Runner = RunAsync;

    private static async Task<(int Code, string Out, string Err)> RunAsync(string[] args, CancellationToken ct)
    {
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wevtutil.exe");
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.Unicode, StandardErrorEncoding = Encoding.Unicode };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("wevtutil.exe didn't start");
        var o = p.StandardOutput.ReadToEndAsync(ct); var e = p.StandardError.ReadToEndAsync(ct);
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch (InvalidOperationException) { } throw; }
        return (p.ExitCode, await o, await e);
    }

    /// <summary>Reads up to <paramref name="max"/> newest events of a log matching <paramref name="query"/>. A log the user
    /// may not read (Security without administrator rights) throws <see cref="UnauthorizedAccessException"/>.</summary>
    public static async Task<List<WinEvent>> ReadAsync(string log, string query, int max = 2000, CancellationToken ct = default)
    {
        log = CheckLog(log);
        var (code, outText, err) = await Runner(new[] { "qe", log, "/q:" + query, "/f:RenderedXml", "/c:" + Math.Clamp(max, 1, 20000), "/rd:true", "/uni:true" }, ct);
        if (code != 0)
        {
            var reason = err.Trim().Split('\n')[0].Trim();
            if (reason.Contains("denied", StringComparison.OrdinalIgnoreCase) || reason.Contains("privilege", StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException($"{log}: {reason} — run the app as administrator to include this log");
            throw new IOException($"{log}: {(reason.Length > 0 ? reason : "wevtutil exit " + code)}");
        }
        return Parse(outText, log);
    }

    /// <summary>Parses wevtutil's XML (a sequence of &lt;Event&gt; elements, no root). DTDs are refused; unknown bits are skipped.</summary>
    public static List<WinEvent> Parse(string xml, string log)
    {
        var list = new List<WinEvent>();
        if (xml.Trim().Length == 0) return list;
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreWhitespace = true, ConformanceLevel = ConformanceLevel.Fragment, MaxCharactersInDocument = 256L * 1024 * 1024 };
        using var r = XmlReader.Create(new StringReader(xml), settings);
        while (r.Read())
        {
            if (r.NodeType != XmlNodeType.Element || r.Name != "Event") continue;
            using var ev = r.ReadSubtree();
            if (ReadEvent(ev, log) is { } e) list.Add(e);
        }
        return list;
    }

    private static WinEvent? ReadEvent(XmlReader r, string log)
    {
        string provider = "", channel = log, message = ""; int level = 4, id = 0; DateTime time = default;
        var data = new List<string>();
        while (r.Read())
        {
            if (r.NodeType != XmlNodeType.Element) continue;
            switch (r.Name)
            {
                case "Provider": provider = r.GetAttribute("Name") ?? ""; break;
                case "EventID": int.TryParse(r.ReadElementContentAsString().Trim(), out id); break;
                case "Level": int.TryParse(r.ReadElementContentAsString().Trim(), out level); break;
                case "TimeCreated":
                    if (DateTime.TryParse(r.GetAttribute("SystemTime"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)) time = t.ToLocalTime();
                    break;
                case "Channel": channel = r.ReadElementContentAsString().Trim(); break;
                case "Message": message = r.ReadElementContentAsString().Trim(); break;
                case "Data": { var v = r.ReadElementContentAsString().Trim(); if (v.Length > 0) data.Add(v); break; }
            }
        }
        if (message.Length == 0 && data.Count > 0) message = string.Join(" · ", data.Take(6));
        if (provider.Length == 0 && id == 0) return null;
        return new WinEvent(time, level, provider, id, channel, Squash(message));
    }

    private static string Squash(string s)
    {
        var t = Regex.Replace(s.Replace("\r", ""), @"[ \t]+", " ").Trim();
        return t.Length > 2000 ? t[..2000] + "…" : t;
    }

    /// <summary>The events grouped by provider + id (the same idea as the Linux log digest): count, first and last time, one
    /// example message, ordered by severity then count. Rendered as "[severity] ×count Provider (id) first→last: example".</summary>
    public static string Digest(IEnumerable<WinEvent> events, int maxPatterns = 40)
    {
        var groups = events.GroupBy(e => (e.Provider, e.Id, e.Level))
            .Select(g => (g.Key, Count: g.Count(), First: g.Min(e => e.Time), Last: g.Max(e => e.Time), Example: g.OrderByDescending(e => e.Time).First().Message))
            .OrderBy(g => g.Key.Level).ThenByDescending(g => g.Count).Take(maxPatterns).ToList();
        var sb = new StringBuilder();
        foreach (var g in groups)
        {
            var sev = g.Key.Level switch { 1 => "critical", 2 => "error", 3 => "warning", _ => "info" };
            var when = g.Count == 1 ? g.Last.ToString("MM-dd HH:mm") : $"{g.First:MM-dd HH:mm}→{g.Last:MM-dd HH:mm}";
            var ex = g.Example.Split('\n')[0];
            sb.Append('[').Append(sev).Append("] ×").Append(g.Count).Append(' ').Append(g.Key.Provider).Append(" (").Append(g.Key.Id).Append(") ").Append(when).Append(": ").AppendLine(ex.Length > 220 ? ex[..220] + "…" : ex);
        }
        return sb.ToString();
    }

    /// <summary>Exports a log's matching events as a real .evtx (opens in Event Viewer). The file is a plain copy of the
    /// events — say so to the user.</summary>
    public static async Task ExportEvtxAsync(string log, string query, string path, CancellationToken ct = default)
    {
        log = CheckLog(log);
        if (!path.EndsWith(".evtx", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("The file must end with .evtx.");
        var (code, _, err) = await Runner(new[] { "epl", log, path, "/q:" + query, "/ow:true" }, ct);
        if (code != 0) throw new IOException($"{log}: {err.Trim().Split('\n')[0]}");
    }

    /// <summary>The events as CSV text (quoted, formula-safe): time, log, level, provider, id, message.</summary>
    public static string Csv(IEnumerable<WinEvent> events)
    {
        static string Q(string s)
        {
            if (s.Length > 0 && s[0] is '=' or '+' or '-' or '@' or '\t' or '\r') s = "'" + s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }
        var sb = new StringBuilder("time,log,level,provider,id,message\n");
        foreach (var e in events.OrderBy(e => e.Time)) sb.AppendLine(string.Join(",", Q(e.Time.ToString("yyyy-MM-dd HH:mm:ss")), Q(e.Log), Q(e.LevelText), Q(e.Provider), e.Id.ToString(), Q(e.Message)));
        return sb.ToString();
    }

    /// <summary>"24 hours ago" → 24 h (the report windows); anything unreadable = 24 h.</summary>
    public static TimeSpan WindowOf(string window)
    {
        var m = Regex.Match(window, @"(\d+)\s*(hour|day|minute)", RegexOptions.IgnoreCase);
        if (!m.Success) return TimeSpan.FromHours(24);
        int n = int.Parse(m.Groups[1].Value);
        return m.Groups[2].Value.ToLowerInvariant() switch { "day" => TimeSpan.FromDays(n), "minute" => TimeSpan.FromMinutes(n), _ => TimeSpan.FromHours(n) };
    }
}
