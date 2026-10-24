using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace DcxManager.Net;

/// <summary>
/// WHOIS (RFC 3912) for a domain or a public IP, starting at IANA and following at most two referrals (refer: / whois: /
/// "Registrar WHOIS Server:"), each referral host validated before it is used; and the ASN behind an IP through Team
/// Cymru's DNS zones (one TXT lookup for the origin, one for the AS name). Plain-text answers are capped at 256 KB and
/// made printable. Private addresses are refused — WHOIS covers public allocations only.
/// </summary>
public static class Whois
{
    public const int MaxBytes = 256 * 1024;
    public const string Iana = "whois.iana.org";

    public sealed record Result(List<(string Server, string Text)> Hops);

    /// <summary>One raw WHOIS exchange: connect to server:43, send the query line, read until the server closes.</summary>
    public static async Task<string> RawAsync(string server, string query, int port = 43, CancellationToken ct = default)
    {
        server = NetTests.CheckHost(server);
        Services.Offline.EnforceStatic(server, "a WHOIS lookup");
        await Services.Offline.EnforceAsync(server, "a WHOIS lookup", ct);
        using var tcp = new TcpClient();
        using (var t = CancellationTokenSource.CreateLinkedTokenSource(ct)) { t.CancelAfter(TimeSpan.FromSeconds(10)); await tcp.ConnectAsync(server, port, t.Token); }
        var s = tcp.GetStream();
        await s.WriteAsync(Encoding.ASCII.GetBytes(query + "\r\n"), ct);
        var buf = new byte[8192]; var all = new MemoryStream();
        using var rt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        rt.CancelAfter(TimeSpan.FromSeconds(20));
        int n;
        while ((n = await s.ReadAsync(buf, rt.Token)) > 0) { all.Write(buf, 0, n); if (all.Length > MaxBytes) break; }
        return Printable(Encoding.UTF8.GetString(all.GetBuffer(), 0, (int)Math.Min(all.Length, MaxBytes)));
    }

    /// <summary>Looks a domain or IP up, following referrals. <paramref name="raw"/> replaces the network in tests.</summary>
    public static async Task<Result> LookupAsync(string target, Func<string, string, CancellationToken, Task<string>>? raw = null, string start = Iana, CancellationToken ct = default)
    {
        raw ??= (srv, q, c) => RawAsync(srv, q, 43, c);
        target = target.Trim().TrimEnd('.').ToLowerInvariant();
        bool isIp = IPAddress.TryParse(target, out var ip);
        if (isIp && NetTargets.IsPrivate(ip!)) throw new ArgumentException("That's a private address — WHOIS covers public allocations only.");
        if (!isIp && (!NetTests.ValidHost(target) || !target.Contains('.'))) throw new ArgumentException("Enter a domain (example.com) or a public IP address.");
        var hops = new List<(string, string)>();
        var server = start; var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int hop = 0; hop < 3 && seen.Add(server); hop++)
        {
            // Verisign (com / net) returns every "example.com" match unless told it's a domain; IANA wants the bare name.
            var q = !isIp && server.EndsWith("verisign-grs.com", StringComparison.OrdinalIgnoreCase) ? "domain " + target : target;
            var text = await raw(server, q, ct);
            hops.Add((server, text));
            var next = Referral(text);
            if (next == null || next.Equals(server, StringComparison.OrdinalIgnoreCase)) break;
            server = next;
        }
        return new Result(hops);
    }

    /// <summary>The next server a WHOIS answer points at, or null. Only a plain host name is accepted (no URLs, ports or paths).</summary>
    public static string? Referral(string text)
    {
        var m = Regex.Match(text, @"^\s*(?:refer|whois|ReferralServer|Registrar WHOIS Server)\s*:\s*(?:r?whois://)?([A-Za-z0-9.-]+)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        var h = m.Groups[1].Value.TrimEnd('.');
        return NetTests.ValidHost(h) && h.Contains('.') && !IPAddress.TryParse(h, out _) ? h : null;
    }

    /// <summary>The fields worth a summary line (registrar, dates, name servers, status; or the netname / org / country of an IP block).</summary>
    public static List<(string Key, string Value)> Summary(string text)
    {
        var keys = new[] { "Domain Name", "Registrar", "Registrar WHOIS Server", "Creation Date", "Registry Expiry Date", "Registrar Registration Expiration Date", "Updated Date", "Name Server", "Domain Status", "DNSSEC",
                           "NetName", "NetRange", "CIDR", "inetnum", "inet6num", "netname", "OrgName", "org-name", "Organization", "descr", "country", "Country", "OriginAS", "origin", "abuse-mailbox", "OrgAbuseEmail" };
        var found = new List<(string, string)>();
        foreach (var line in text.Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var k = line[..colon].Trim(); var v = line[(colon + 1)..].Trim();
            if (v.Length == 0 || k.StartsWith('%') || k.StartsWith('#')) continue;
            if (keys.Any(x => x.Equals(k, StringComparison.OrdinalIgnoreCase)) && !found.Any(f => f.Item1.Equals(k, StringComparison.OrdinalIgnoreCase) && f.Item2 == v)) found.Add((k, v));
            if (found.Count >= 40) break;
        }
        return found;
    }

    // ---- ASN (Team Cymru) ------------------------------------------------------------------------------------------------

    public sealed record AsnInfo(int Asn, string Prefix, string Country, string Registry, string Allocated, string Name);

    /// <summary>"13335 | 104.16.0.0/12 | US | arin | 2014-03-28" + "13335 | US | arin | 2010-07-14 | CLOUDFLARENET, US".</summary>
    public static async Task<AsnInfo?> AsnAsync(IPAddress ip, Func<string, Task<List<string>>>? txt = null, CancellationToken ct = default)
    {
        txt ??= DnsRecords.TxtAsync;
        if (NetTargets.IsPrivate(ip)) throw new ArgumentException("That's a private address — it has no public ASN.");
        var rev = DnsRecords.ReverseName(ip);
        var zone = ip.AddressFamily == AddressFamily.InterNetworkV6 ? ".ip6.arpa" : ".in-addr.arpa";
        var name = rev[..rev.LastIndexOf(zone, StringComparison.Ordinal)] + (ip.AddressFamily == AddressFamily.InterNetworkV6 ? ".origin6.asn.cymru.com" : ".origin.asn.cymru.com");
        var origin = (await txt(name)).Select(Fields).FirstOrDefault(f => f.Length >= 5 && int.TryParse(f[0].Split(' ')[0], out _));
        ct.ThrowIfCancellationRequested();
        if (origin == null) return null;
        var asn = int.Parse(origin[0].Split(' ')[0]);   // several origins ("13335 15169") → the first
        var detail = (await txt($"AS{asn}.asn.cymru.com")).Select(Fields).FirstOrDefault(f => f.Length >= 5);
        return new AsnInfo(asn, origin[1], origin[2], origin[3], origin[4], detail?[4] ?? "");
    }

    private static string[] Fields(string s) => s.Split('|').Select(x => x.Trim()).ToArray();

    private static string Printable(string s) => new(s.Where(c => c is '\r' or '\n' or '\t' || !char.IsControl(c)).ToArray());
}
