using System.Net;
using System.Text.RegularExpressions;

namespace DcxManager.Mail;

/// <summary>
/// SPF in full (RFC 7208), like MxToolbox's SPF check: the record and every include / redirect as a tree, the DNS lookup
/// count against the limit of 10 and void lookups against 2, syntax, the "all" policy, deprecated ptr, overly wide ranges,
/// several SPF records — and, given a sending IP, the result a receiver would reach (pass / fail / softfail / neutral /
/// none / permerror) and which mechanism decided it. Macros (%{…}) are reported, not expanded.
/// </summary>
public static class SpfCheck
{
    public sealed record Term(string Qualifier, string Name, string Value, string Raw);
    public sealed class Node
    {
        public string Domain { get; init; } = "";
        public string Record { get; set; } = "";
        public List<Term> Terms { get; } = new();
        public List<Node> Children { get; } = new();
        public string? Error { get; set; }
    }
    public sealed record Report(Node Root, int Lookups, int VoidLookups, List<MailFinding> Findings, string? Result, string Decided);

    private static readonly string[] Mechanisms = ["all", "include", "a", "mx", "ptr", "ip4", "ip6", "exists"];

    public static async Task<Report> CheckAsync(string domain, IPAddress? sender = null, IMailDns? dns = null)
    {
        dns ??= SystemMailDns.Instance;
        domain = MailNet.CheckDomain(domain);
        var st = new State(dns);
        var root = await LoadAsync(domain, st, 0);
        var f = st.Findings;
        if (root.Error == null)
        {
            if (st.Lookups > 10) f.Add(new("bad", "SPF", $"{st.Lookups} DNS lookups — over the limit of 10, so receivers return permerror", "flatten includes you don't need, or replace a / mx with ip4 / ip6"));
            else f.Add(new(st.Lookups >= 8 ? "warn" : "good", "SPF", $"{st.Lookups} of 10 DNS lookups used"));
            if (st.Void > 2) f.Add(new("bad", "SPF", $"{st.Void} lookups returned nothing (limit 2) — permerror", "remove includes / a / mx that point to names without records"));
            var all = root.Terms.FirstOrDefault(t => t.Name == "all");
            var redirect = root.Terms.FirstOrDefault(t => t.Name == "redirect");
            if (all == null && redirect == null) f.Add(new("warn", "SPF", "no “all” at the end: anything not listed gets neutral", "end the record with -all (or ~all while testing)"));
            else if (all != null)
                f.Add(all.Qualifier switch
                {
                    "+" => new("bad", "SPF", "+all — anyone in the world passes as you", "use -all"),
                    "?" => new("warn", "SPF", "?all — neutral: SPF doesn't protect the domain", "use ~all, then -all"),
                    "~" => new("warn", "SPF", "~all — softfail: unlisted senders are only marked", "move to -all once every sender is listed"),
                    _ => new("good", "SPF", "-all — unlisted senders fail"),
                });
        }
        string? result = null; var decided = "";
        if (sender != null && root.Error == null)
        {
            var ev = new State(dns);
            (result, decided) = await EvalAsync(root, sender, ev, 0);
            if (ev.Lookups > 10) { result = "permerror"; decided = "more than 10 DNS lookups"; }
            f.Add(new(result == "pass" ? "good" : result is "fail" or "permerror" ? "bad" : "warn", "SPF result", $"{sender} → {result}{(decided.Length > 0 ? " (" + decided + ")" : "")}"));
        }
        else if (root.Error != null) result = root.Error.StartsWith("no SPF") ? "none" : "permerror";
        return new Report(root, st.Lookups, st.Void, f, result, decided);
    }

    private sealed class State(IMailDns dns)
    {
        public IMailDns Dns { get; } = dns;
        public int Lookups, Void;
        public List<MailFinding> Findings { get; } = new();
        public HashSet<string> Seen { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<Node> LoadAsync(string domain, State st, int depth)
    {
        var node = new Node { Domain = domain };
        if (depth > 10 || !st.Seen.Add(domain)) { node.Error = "include loop"; st.Findings.Add(new("bad", "SPF", $"{domain} is included more than once (a loop)")); return node; }
        var spf = (await st.Dns.TxtAsync(domain)).Where(t => Regex.IsMatch(t, @"^v=spf1(\s|$)", RegexOptions.IgnoreCase)).ToList();
        if (spf.Count == 0) { node.Error = $"no SPF record on {domain}"; st.Findings.Add(new(depth == 0 ? "bad" : "bad", "SPF", node.Error, depth == 0 ? $"add a TXT record on {domain}: v=spf1 mx -all" : "an include that has no SPF record is a permerror")); return node; }
        if (spf.Count > 1) { node.Error = $"{spf.Count} SPF records on {domain}"; st.Findings.Add(new("bad", "SPF", node.Error + " — receivers return permerror", "merge them into one record")); }
        node.Record = spf[0];
        if (node.Record.Length > 450) st.Findings.Add(new("warn", "SPF", $"{domain}: the record is {node.Record.Length} characters — long records can fail over UDP DNS"));
        if (node.Record.Contains('%')) st.Findings.Add(new("info", "SPF", $"{domain} uses macros (%{{…}}) — shown, not expanded here"));
        foreach (var raw in node.Record.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            var m = Regex.Match(raw, @"^([+\-~?]?)([A-Za-z][A-Za-z0-9-]*)(?:[:=](.*))?$");
            if (!m.Success) { st.Findings.Add(new("bad", "SPF", $"{domain}: “{raw}” isn't valid SPF syntax")); continue; }
            var q = m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : "+";
            var name = m.Groups[2].Value.ToLowerInvariant();
            var value = m.Groups[3].Value;
            if (name is "redirect" or "exp") { node.Terms.Add(new Term("", name, value, raw)); if (name == "redirect") st.Lookups++; continue; }
            if (!Mechanisms.Contains(name)) { st.Findings.Add(new("bad", "SPF", $"{domain}: unknown mechanism “{raw}”")); continue; }
            node.Terms.Add(new Term(q, name, value, raw));
            switch (name)
            {
                case "include" or "a" or "mx" or "exists": st.Lookups++; break;
                case "ptr": st.Lookups++; st.Findings.Add(new("warn", "SPF", $"{domain}: ptr is deprecated (slow and unreliable)", "replace it with ip4 / ip6 or a")); break;
                case "ip4" or "ip6":
                    var parts = value.Split('/');
                    if (!IPAddress.TryParse(parts[0], out var net) || parts.Length > 1 && !int.TryParse(parts[1], out _)) st.Findings.Add(new("bad", "SPF", $"{domain}: “{raw}” isn't a valid address"));
                    else if (parts.Length > 1 && int.Parse(parts[1]) < (net.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 16 : 32))
                        st.Findings.Add(new("warn", "SPF", $"{domain}: {raw} allows a very wide range of addresses"));
                    break;
            }
            if (name == "a" || name == "mx")
            {
                var target = TargetDomain(value, domain);
                var empty = name == "a" ? (await st.Dns.AddressesAsync(target)).Count == 0 : (await st.Dns.MxAsync(target)).Count == 0;
                if (empty) { st.Void++; st.Findings.Add(new("warn", "SPF", $"{domain}: {raw} finds no {(name == "a" ? "address" : "MX")} records")); }
            }
        }
        foreach (var t in node.Terms.Where(t => t.Name is "include" or "redirect"))
        {
            if (t.Value.Length == 0 || t.Value.Contains('%')) continue;
            var child = await LoadAsync(t.Value.ToLowerInvariant(), st, depth + 1);
            if (child.Error?.StartsWith("no SPF") == true) st.Void++;
            node.Children.Add(child);
        }
        return node;
    }

    private static string TargetDomain(string value, string domain)
    {
        var v = value.Split('/')[0];
        return v.Length == 0 ? domain : v;
    }

    private static int Cidr(string value, bool v4)
    {
        var parts = value.Split('/');
        // "a:host/24//64": the first number is the IPv4 prefix, the second the IPv6 one.
        if (v4) return parts.Length > 1 && int.TryParse(parts[1], out var p4) ? p4 : 32;
        return parts.Length > 3 && int.TryParse(parts[3], out var p6) ? p6 : parts.Length == 3 && int.TryParse(parts[2], out var q6) ? q6 : 128;
    }

    private static string Qual(string q) => q switch { "-" => "fail", "~" => "softfail", "?" => "neutral", _ => "pass" };

    private static async Task<(string Result, string Why)> EvalAsync(Node node, IPAddress ip, State st, int depth)
    {
        if (node.Error != null) return (node.Error.StartsWith("no SPF") && depth == 0 ? "none" : "permerror", node.Error);
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        bool v4 = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
        foreach (var t in node.Terms)
        {
            bool match = false;
            switch (t.Name)
            {
                case "all": match = true; break;
                case "ip4" or "ip6":
                {
                    var p = t.Value.Split('/');
                    if (IPAddress.TryParse(p[0], out var net)) match = MailNet.InCidr(ip, net, p.Length > 1 && int.TryParse(p[1], out var b) ? b : net.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128);
                    break;
                }
                case "a":
                    st.Lookups++;
                    match = (await st.Dns.AddressesAsync(TargetDomain(t.Value, node.Domain))).Any(a => MailNet.InCidr(ip, a, a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? Cidr(t.Value, true) : Cidr(t.Value, false)));
                    break;
                case "mx":
                    st.Lookups++;
                    foreach (var mx in (await st.Dns.MxAsync(TargetDomain(t.Value, node.Domain))).Take(10))
                        if ((await st.Dns.AddressesAsync(mx.Host)).Any(a => MailNet.InCidr(ip, a, a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? Cidr(t.Value, true) : Cidr(t.Value, false)))) { match = true; break; }
                    break;
                case "exists":
                    st.Lookups++;
                    match = !t.Value.Contains('%') && (await st.Dns.AddressesAsync(t.Value)).Count > 0;
                    break;
                case "include":
                {
                    st.Lookups++;
                    var child = node.Children.FirstOrDefault(c => c.Domain.Equals(t.Value, StringComparison.OrdinalIgnoreCase));
                    if (child == null) continue;
                    var (r, _) = await EvalAsync(child, ip, st, depth + 1);
                    if (r is "permerror" or "none" && child.Error != null) return ("permerror", $"include:{t.Value} — {child.Error}");
                    match = r == "pass";
                    break;
                }
                case "ptr": continue;   // deprecated; not evaluated
                default: continue;
            }
            if (match) return (Qual(t.Qualifier), $"{(depth > 0 ? node.Domain + ": " : "")}{t.Raw}");
        }
        if (node.Terms.FirstOrDefault(t => t.Name == "redirect") is { } red && node.Children.FirstOrDefault(c => c.Domain.Equals(red.Value, StringComparison.OrdinalIgnoreCase)) is { } target)
        {
            st.Lookups++;
            return await EvalAsync(target, ip, st, depth + 1);
        }
        return ("neutral", "nothing matched");
    }

    /// <summary>The tree as indented text (record, then each include / redirect below it).</summary>
    public static string Tree(Node n, int indent = 0)
    {
        var pad = new string(' ', indent * 3);
        var s = $"{pad}{(indent == 0 ? "" : "└ ")}{n.Domain}: {(n.Error != null && n.Record.Length == 0 ? "⚠ " + n.Error : n.Record)}\n";
        foreach (var c in n.Children) s += Tree(c, indent + 1);
        return s;
    }
}
