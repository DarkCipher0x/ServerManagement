using System.Net;
using System.Net.Sockets;
using DcxManager.Net;

namespace DcxManager.Mail;

/// <summary>
/// Blacklist (DNSBL / RBL) check, like MxToolbox's: is a sending IP or a domain listed? Each list is asked with one DNS
/// query (the reversed address under the list's zone) — nothing else is sent. A 127.0.0.x answer means listed; the
/// 127.255.255.x answers some lists give when they refuse a query (Spamhaus through public resolvers) are reported as
/// "refused", never as listed or clean.
/// </summary>
public static class Blacklists
{
    public sealed record ListInfo(string Zone, string Name, bool Domains);
    public sealed record Hit(ListInfo List, string State, string Detail);

    public static readonly ListInfo[] IpLists =
    [
        new("zen.spamhaus.org", "Spamhaus ZEN", false), new("bl.spamcop.net", "SpamCop", false), new("b.barracudacentral.org", "Barracuda", false),
        new("dnsbl.sorbs.net", "SORBS", false), new("bl.mailspike.net", "Mailspike", false), new("psbl.surriel.com", "PSBL", false),
        new("dnsbl-1.uceprotect.net", "UCEPROTECT level 1", false), new("ix.dnsbl.manitu.net", "NiX Spam", false), new("truncate.gbudb.net", "GBUdb Truncate", false),
        new("dnsbl.dronebl.org", "DroneBL", false), new("all.s5h.net", "s5h.net", false), new("db.wpbl.info", "WPBL", false),
        new("spam.dnsbl.anonmails.de", "anonmails.de", false), new("bl.blocklist.de", "blocklist.de", false), new("cbl.abuseat.org", "CBL (via Spamhaus)", false),
        new("dnsbl.spfbl.net", "SPFBL", false), new("rbl.interserver.net", "InterServer", false), new("bl.nszones.com", "NSZones", false),
    ];

    public static readonly ListInfo[] DomainLists =
    [
        new("dbl.spamhaus.org", "Spamhaus DBL", true), new("multi.surbl.org", "SURBL", true), new("multi.uribl.com", "URIBL", true),
        new("dbl.nszones.com", "NSZones DBL", true), new("uribl.spameatingmonkey.net", "SEM URIBL", true),
    ];

    /// <summary>The DNS name asked for one list: "10.2.0.192.zen.spamhaus.org" for 192.0.2.10, "example.com.dbl…" for a domain.</summary>
    public static string QueryName(string target, ListInfo list)
    {
        if (IPAddress.TryParse(target, out var ip))
        {
            var rev = DnsRecords.ReverseName(ip);
            return rev[..rev.LastIndexOf(ip.AddressFamily == AddressFamily.InterNetwork ? ".in-addr.arpa" : ".ip6.arpa", StringComparison.Ordinal)] + "." + list.Zone;
        }
        return MailNet.CheckDomain(target) + "." + list.Zone;
    }

    /// <summary>Checks an IP (against the IP lists) or a domain (against the domain lists), a few lists at a time.</summary>
    public static async Task<List<Hit>> CheckAsync(string target, IMailDns? dns = null, Action<Hit>? onHit = null, CancellationToken ct = default)
    {
        dns ??= SystemMailDns.Instance;
        target = target.Trim();
        bool isIp = IPAddress.TryParse(target, out var ip);
        if (isIp && (IPAddress.IsLoopback(ip!) || NetTargets.IsPrivate(ip!))) throw new ArgumentException("That's a private address — blacklists list public sending addresses only.");
        var lists = isIp ? IpLists : DomainLists;
        var hits = new List<Hit>();
        using var gate = new SemaphoreSlim(6);
        var tasks = lists.Select(async l =>
        {
            await gate.WaitAsync(ct);
            try
            {
                Hit h;
                try
                {
                    var answers = await dns.AddressesAsync(QueryName(target, l));
                    var v4 = answers.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.GetAddressBytes()).ToList();
                    if (v4.Count == 0) h = new Hit(l, "clean", "not listed");
                    else if (v4.All(b => b[0] == 127 && b[1] == 255 && b[2] == 255)) h = new Hit(l, "refused", "the list refused the query (often: public DNS resolvers are blocked) — ask from your own resolver");
                    else if (v4.Any(b => b[0] == 127)) h = new Hit(l, "listed", string.Join(", ", answers.Select(a => a.ToString())));
                    else h = new Hit(l, "unknown", "unexpected answer " + string.Join(", ", answers));
                }
                catch (Exception ex) when (ex is InvalidOperationException or SocketException) { h = new Hit(l, "error", ex.Message); }
                lock (hits) hits.Add(h);
                onHit?.Invoke(h);
            }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks);
        return hits.OrderBy(h => h.State switch { "listed" => 0, "refused" => 1, "error" or "unknown" => 2, _ => 3 }).ThenBy(h => h.List.Name).ToList();
    }
}
