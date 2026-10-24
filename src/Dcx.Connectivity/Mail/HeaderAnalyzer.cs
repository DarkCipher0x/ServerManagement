using System.Globalization;
using System.Text.RegularExpressions;

namespace DcxManager.Mail;

/// <summary>
/// Email header analyzer, like MxToolbox's: paste a message's headers and see the route it took (each Received hop with
/// the delay since the previous one), who it claims to be from, the SPF / DKIM / DMARC verdicts the receiver recorded
/// (Authentication-Results, ARC), the DKIM signatures (d= / s=), whether the visible From domain aligns with them, spam
/// scores, and the usual warning signs (Reply-To elsewhere, missing Message-ID, a Return-Path on another domain).
/// Pure parsing: the pasted text never leaves this PC and nothing is looked up.
/// </summary>
public static class HeaderAnalyzer
{
    public sealed record Hop(int Index, string From, string By, string With, DateTimeOffset? When, TimeSpan? Delay, string Raw);
    public sealed record AuthResult(string Method, string Result, string Detail);
    public sealed record Report(List<(string Name, string Value)> Headers, List<Hop> Hops, TimeSpan? Total, List<AuthResult> Auth,
        List<(string Domain, string Selector)> DkimSignatures, string FromDomain, List<MailFinding> Findings);

    public const int MaxInput = 512 * 1024;

    /// <summary>Header lines unfolded (continuation lines joined); stops at the first blank line (the body).</summary>
    public static List<(string Name, string Value)> Parse(string raw)
    {
        if (raw.Length > MaxInput) raw = raw[..MaxInput];
        var list = new List<(string, string)>();
        string? name = null; var value = new System.Text.StringBuilder();
        foreach (var line in raw.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (line.Length == 0) { if (list.Count > 0 || name != null) break; continue; }
            if ((line[0] == ' ' || line[0] == '\t') && name != null) { value.Append(' ').Append(line.Trim()); continue; }
            var colon = line.IndexOf(':');
            if (colon <= 0 || line[..colon].Any(c => c <= ' ' || c > '~')) continue;   // not a header line
            if (name != null) list.Add((name, value.ToString().Trim()));
            name = line[..colon]; value.Clear().Append(line[(colon + 1)..].Trim());
        }
        if (name != null) list.Add((name, value.ToString().Trim()));
        return list;
    }

    private static string? Get(List<(string Name, string Value)> h, string name) => h.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
    private static IEnumerable<string> All(List<(string Name, string Value)> h, string name) => h.Where(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(x => x.Value);

    /// <summary>The domain of an address in a header ("Name &lt;a@b.example&gt;" → b.example).</summary>
    public static string DomainOf(string? header)
    {
        if (string.IsNullOrEmpty(header)) return "";
        var m = Regex.Match(header, @"[A-Za-z0-9._%+\-]+@([A-Za-z0-9.\-]+\.[A-Za-z]{2,})");
        return m.Success ? m.Groups[1].Value.ToLowerInvariant().TrimEnd('.') : "";
    }

    /// <summary>Relaxed alignment (RFC 7489): same organisational domain, approximated as the last two labels (three when
    /// the second-last is a short public suffix part such as co.uk).</summary>
    public static string OrgDomain(string d)
    {
        var p = d.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length <= 2) return d;
        int take = p[^2].Length <= 3 && p[^1].Length == 2 ? 3 : 2;
        return string.Join(".", p[^Math.Min(take, p.Length)..]);
    }

    public static Report Analyze(string raw)
    {
        var h = Parse(raw);
        var f = new List<MailFinding>();

        // Route: Received headers are added on top, so the oldest hop is the last one.
        var received = All(h, "Received").Reverse().ToList();
        var hops = new List<Hop>();
        DateTimeOffset? prev = null, first = null, last = null;
        for (int i = 0; i < received.Count; i++)
        {
            var r = received[i];
            var semi = r.LastIndexOf(';');
            DateTimeOffset? when = semi >= 0 && TryDate(r[(semi + 1)..], out var d) ? d : null;
            var delay = when != null && prev != null ? when - prev : null;
            hops.Add(new Hop(i + 1, Part(r, "from"), Part(r, "by"), Part(r, "with"), when, delay, r));
            if (when != null) { first ??= when; last = when; prev = when; }
            if (delay is { } dl && dl < TimeSpan.FromMinutes(-2)) f.Add(new("warn", "Route", $"hop {i + 1} is timestamped {(-dl).TotalMinutes:0} min before the previous one (a server clock is off)"));
            else if (delay is { } slow && slow > TimeSpan.FromMinutes(10)) f.Add(new("warn", "Route", $"hop {i + 1} took {Fmt(slow)} — the message waited in a queue there"));
        }
        var total = first != null && last != null ? last - first : null;

        // What the receiver concluded.
        var auth = new List<AuthResult>();
        foreach (var ar in All(h, "Authentication-Results").Concat(All(h, "ARC-Authentication-Results")))
            foreach (Match m in Regex.Matches(ar, @"\b(spf|dkim|dmarc|arc|compauth)=([a-z]+)([^;]*)", RegexOptions.IgnoreCase))
                auth.Add(new AuthResult(m.Groups[1].Value.ToLowerInvariant(), m.Groups[2].Value.ToLowerInvariant(), m.Groups[3].Value.Trim()));
        if (Get(h, "Received-SPF") is { } rspf && !auth.Any(a => a.Method == "spf"))
            auth.Add(new AuthResult("spf", rspf.Split(' ', 2)[0].ToLowerInvariant(), rspf));
        foreach (var method in new[] { "spf", "dkim", "dmarc" })
        {
            var a = auth.FirstOrDefault(x => x.Method == method);
            f.Add(a == null ? new("info", method.ToUpperInvariant(), "no result recorded in these headers")
                : new(a.Result == "pass" ? "good" : a.Result is "fail" or "permerror" or "softfail" ? "bad" : "warn", method.ToUpperInvariant(), $"{a.Result} {a.Detail}".Trim()));
        }

        // DKIM signatures and alignment with the visible From.
        var sigs = All(h, "DKIM-Signature").Select(s => DmarcDkim.ParseTags(s)).Select(t => (t.GetValueOrDefault("d", "").ToLowerInvariant(), t.GetValueOrDefault("s", ""))).ToList();
        var from = DomainOf(Get(h, "From"));
        var returnPath = DomainOf(Get(h, "Return-Path"));
        if (from.Length == 0) f.Add(new("bad", "From", "no From address"));
        else
        {
            bool dkimAligned = sigs.Any(s => s.Item1.Length > 0 && OrgDomain(s.Item1) == OrgDomain(from));
            bool spfAligned = returnPath.Length > 0 && OrgDomain(returnPath) == OrgDomain(from);
            f.Add(new(dkimAligned || spfAligned ? "good" : "bad", "Alignment",
                $"From {from} · Return-Path {(returnPath.Length > 0 ? returnPath : "—")} ({(spfAligned ? "aligned" : "not aligned")}) · DKIM {(sigs.Count == 0 ? "none" : string.Join(", ", sigs.Select(s => s.Item1)))} ({(dkimAligned ? "aligned" : "not aligned")})",
                dkimAligned || spfAligned ? null : "DMARC needs SPF or DKIM aligned with the From domain"));
        }
        if (DomainOf(Get(h, "Reply-To")) is { Length: > 0 } rt && from.Length > 0 && OrgDomain(rt) != OrgDomain(from))
            f.Add(new("warn", "Reply-To", $"replies go to {rt}, not {from} — common in phishing (and in some newsletters)"));
        if (Get(h, "Message-ID") == null) f.Add(new("warn", "Message-ID", "no Message-ID — spam filters score this"));
        if (Get(h, "Date") is { } date && !TryDate(date, out _)) f.Add(new("warn", "Date", "the Date header isn't a valid date"));
        foreach (var name in new[] { "X-Spam-Status", "X-Spam-Score", "X-Spam-Flag", "X-MS-Exchange-Organization-SCL", "X-Forefront-Antispam-Report", "X-Rspamd-Score" })
            if (Get(h, name) is { } v) f.Add(new("info", "Spam filter", $"{name}: {Short(v)}"));
        return new Report(h, hops, total, auth, sigs, from, f);
    }

    private static string Part(string received, string word)
    {
        var m = Regex.Match(received, $@"\b{word}\s+(\S+(?:\s+\([^)]*\))?)", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : "";
    }

    private static readonly string[] DateFormats =
        ["ddd, d MMM yyyy HH:mm:ss zzz", "d MMM yyyy HH:mm:ss zzz", "ddd, d MMM yyyy HH:mm:ss", "ddd, dd MMM yyyy HH:mm:ss zzz", "ddd, d MMM yy HH:mm:ss zzz"];

    public static bool TryDate(string s, out DateTimeOffset d)
    {
        s = Regex.Replace(s.Trim(), @"\s*\([^)]*\)\s*$", "");              // "(UTC)" comments
        s = Regex.Replace(s, @"\s+(GMT|UT|UTC)$", " +0000");
        s = Regex.Replace(s, @"([+-]\d\d)(\d\d)$", "$1:$2");                 // +0100 → +01:00
        s = Regex.Replace(s, @"\s+", " ");
        return DateTimeOffset.TryParseExact(s, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out d)
            || DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out d);
    }

    public static string Fmt(TimeSpan t) => t.TotalSeconds < 1 ? "<1 s" : t.TotalMinutes < 1 ? $"{t.TotalSeconds:0} s" : t.TotalHours < 1 ? $"{t.TotalMinutes:0} min" : $"{t.TotalHours:0.#} h";
    private static string Short(string s) => s.Length > 160 ? s[..160] + "…" : s;
}
