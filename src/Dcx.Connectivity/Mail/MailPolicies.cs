using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace DcxManager.Mail;

/// <summary>
/// The other mail-domain records MxToolbox checks: MTA-STS (RFC 8461: the TXT record and the policy file, fetched over
/// https with the certificate validated as usual, at most 64 KB, and compared with the domain's real MX hosts), TLS-RPT
/// (RFC 8460), BIMI, and forward-confirmed reverse DNS for a sending IP.
/// </summary>
public static class MailPolicies
{
    public const int MaxPolicyBytes = 64 * 1024;

    public sealed record StsPolicy(string Mode, List<string> Mx, int MaxAge, string Raw);

    public static StsPolicy? ParseStsPolicy(string text)
    {
        var lines = text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(l => l.Split(':', 2)).Where(p => p.Length == 2).Select(p => (Key: p[0].Trim().ToLowerInvariant(), Value: p[1].Trim())).ToList();
        if (lines.FirstOrDefault(l => l.Key == "version").Value != "STSv1") return null;
        return new StsPolicy(lines.FirstOrDefault(l => l.Key == "mode").Value ?? "", lines.Where(l => l.Key == "mx").Select(l => l.Value.ToLowerInvariant()).ToList(),
            int.TryParse(lines.FirstOrDefault(l => l.Key == "max_age").Value, out var age) ? age : 0, text);
    }

    /// <summary>Does an MX host match a policy pattern ("mail.example.com" or "*.example.com", one label for the star)?</summary>
    public static bool MxMatches(string host, string pattern)
    {
        host = host.TrimEnd('.').ToLowerInvariant(); pattern = pattern.TrimEnd('.').ToLowerInvariant();
        if (pattern.StartsWith("*.")) { var rest = pattern[1..]; return host.EndsWith(rest) && !host[..^rest.Length].Contains('.') && host.Length > rest.Length; }
        return host == pattern;
    }

    /// <summary>MTA-STS, TLS-RPT and BIMI for a domain. <paramref name="fetch"/> reads the policy file (the self-test fakes it).</summary>
    public static async Task<List<MailFinding>> CheckAsync(string domain, IMailDns? dns = null, Func<Uri, CancellationToken, Task<string>>? fetch = null, CancellationToken ct = default)
    {
        dns ??= SystemMailDns.Instance;
        fetch ??= FetchPolicyAsync;
        domain = MailNet.CheckDomain(domain);
        var f = new List<MailFinding>();

        // MTA-STS
        var sts = (await dns.TxtAsync("_mta-sts." + domain)).Where(t => t.StartsWith("v=STSv1", StringComparison.OrdinalIgnoreCase)).ToList();
        if (sts.Count == 0) f.Add(new("info", "MTA-STS", "no MTA-STS record — senders may deliver without TLS if it's stripped", $"publish _mta-sts.{domain} TXT \"v=STSv1; id=1\" and a policy at https://mta-sts.{domain}/.well-known/mta-sts.txt"));
        else
        {
            if (sts.Count > 1) f.Add(new("bad", "MTA-STS", "more than one MTA-STS record"));
            if (!Regex.IsMatch(sts[0], @"\bid=[A-Za-z0-9]{1,32}\b")) f.Add(new("bad", "MTA-STS", "the record has no valid id=", "add id=<letters and digits, changed whenever the policy changes>"));
            try
            {
                var policy = ParseStsPolicy(await fetch(new Uri($"https://mta-sts.{domain}/.well-known/mta-sts.txt"), ct));
                if (policy == null) f.Add(new("bad", "MTA-STS", "the policy file isn't a valid STSv1 policy"));
                else
                {
                    f.Add(new(policy.Mode == "enforce" ? "good" : policy.Mode == "testing" ? "warn" : "bad", "MTA-STS", $"mode {policy.Mode}, max_age {policy.MaxAge}s, mx {string.Join(", ", policy.Mx)}",
                        policy.Mode == "testing" ? "switch to mode: enforce once TLS-RPT reports are clean" : policy.Mode == "none" ? "the policy is switched off" : null));
                    var mx = await dns.MxAsync(domain);
                    foreach (var m in mx.Where(m => !policy.Mx.Any(p => MxMatches(m.Host, p))))
                        f.Add(new("bad", "MTA-STS", $"the MX {m.Host} isn't covered by the policy — enforcing senders won't deliver there", "add it as an mx: line"));
                    if (policy.MaxAge < 86400) f.Add(new("warn", "MTA-STS", "max_age under a day", "use a week or more (604800)"));
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or Services.OfflineBlockedException)
            { f.Add(new("bad", "MTA-STS", "the policy file couldn't be fetched over trusted https: " + ex.Message)); }
        }

        // TLS-RPT
        var rpt = (await dns.TxtAsync("_smtp._tls." + domain)).FirstOrDefault(t => t.StartsWith("v=TLSRPTv1", StringComparison.OrdinalIgnoreCase));
        f.Add(rpt == null ? new("info", "TLS-RPT", "no TLS reporting record", $"publish _smtp._tls.{domain} TXT \"v=TLSRPTv1; rua=mailto:tls-reports@{domain}\"")
                          : new("good", "TLS-RPT", rpt));

        // BIMI
        var bimi = (await dns.TxtAsync("default._bimi." + domain)).FirstOrDefault(t => t.StartsWith("v=BIMI1", StringComparison.OrdinalIgnoreCase));
        if (bimi == null) f.Add(new("info", "BIMI", "no BIMI record (optional: shows your logo in some inboxes; needs DMARC quarantine or reject)"));
        else
        {
            var tags = DmarcDkim.ParseTags(bimi);
            f.Add(new(tags.GetValueOrDefault("l", "").StartsWith("https://") ? "good" : "warn", "BIMI", bimi, tags.ContainsKey("a") ? null : "Gmail and Apple Mail also need a VMC / CMC certificate (a=)"));
        }
        return f;
    }

    /// <summary>The policy over https: certificate validated by Windows (MTA-STS depends on it), no redirects, 64 KB at most.</summary>
    public static async Task<string> FetchPolicyAsync(Uri uri, CancellationToken ct)
    {
        Services.Offline.EnforceStatic(uri.IdnHost, "an MTA-STS policy check");
        await Services.Offline.EnforceAsync(uri.IdnHost, "an MTA-STS policy check", ct);
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(10) }) { Timeout = TimeSpan.FromSeconds(20) };
        using var r = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!r.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)r.StatusCode}");
        await using var s = await r.Content.ReadAsStreamAsync(ct);
        var buf = new byte[MaxPolicyBytes + 1];
        int n = 0, got;
        while (n < buf.Length && (got = await s.ReadAsync(buf.AsMemory(n), ct)) > 0) n += got;
        if (n > MaxPolicyBytes) throw new InvalidDataException("the policy file is larger than 64 KB");
        return System.Text.Encoding.UTF8.GetString(buf, 0, n);
    }

    /// <summary>Forward-confirmed reverse DNS: the IP's PTR name(s) must resolve back to the same IP (mail servers need it).</summary>
    public static async Task<MailFinding> FcrDnsAsync(IPAddress ip, IMailDns? dns = null)
    {
        dns ??= SystemMailDns.Instance;
        var names = await dns.PtrAsync(ip);
        if (names.Count == 0) return new("bad", "Reverse DNS", $"{ip} has no PTR record — many receivers reject mail from it", "ask whoever owns the address (your host / ISP) to set one");
        foreach (var n in names)
            if ((await dns.AddressesAsync(n.TrimEnd('.'))).Any(a => a.Equals(ip)))
                return new("good", "Reverse DNS", $"{ip} → {n} → {ip} (forward-confirmed)");
        return new("bad", "Reverse DNS", $"{ip} → {string.Join(", ", names)}, but that name doesn't resolve back to {ip}", "make the PTR name's A / AAAA record point at this address");
    }
}
