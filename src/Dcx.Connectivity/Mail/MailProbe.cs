using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using DcxManager.Net;

namespace DcxManager.Mail;

/// <summary>
/// Mail server checks from this PC (0.3.0 §8): TLS on 25 (STARTTLS), 465 (implicit TLS) and 587 (STARTTLS) with the
/// certificate's name, issuer, expiry and whether Windows trusts it; an open-relay probe (only against your own saved
/// hosts, and it never sends mail: MAIL FROM / RCPT TO to the reserved example.com / example.net, then RSET); and DNS:
/// MX, SPF, DMARC and a DKIM selector you name. Outbound port 25 is often blocked by ISPs; the result says so.
/// </summary>
public static class MailProbe
{
    public sealed record PortResult(int Port, bool Open, string Banner, string Tls, string Certificate, DateTime? Expires, bool Trusted, string Note);

    public static async Task<PortResult> PortAsync(string host, int port, CancellationToken ct = default)
    {
        await Services.Offline.EnforceAsync(host, "a mail server check", ct);
        using var tcp = new TcpClient();
        using (var t = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            t.CancelAfter(8000);
            try { await tcp.ConnectAsync(host, port, t.Token); }
            catch
            {
                return new(port, false, "", "", "", null, false, port == 25 ? "no answer — closed, or outbound port 25 is blocked by your ISP / network (common); test from the server or another network" : "no answer (closed or filtered)");
            }
        }
        Stream s = tcp.GetStream();
        var info = new TlsInfo();
        try
        {
            if (port == 465) s = await TlsAsync(s, host, info, ct);
            var c = new Lines(s);
            var banner = (await c.ReplyAsync(ct)).Text;
            var caps = await c.CommandAsync("EHLO dcx-probe.invalid", ct);
            if (port != 465)
            {
                if (!caps.Text.Contains("STARTTLS", StringComparison.OrdinalIgnoreCase)) return new(port, true, banner, "none", "", null, false, "⚠ STARTTLS isn't offered: mail to or through this port travels unencrypted");
                var st = await c.CommandAsync("STARTTLS", ct);
                if (st.Code != 220) return new(port, true, banner, "refused", "", null, false, "STARTTLS refused: " + st.Text);
                s = await TlsAsync(s, host, info, ct);
                c = new Lines(s);
                await c.CommandAsync("EHLO dcx-probe.invalid", ct);
            }
            try { await c.CommandAsync("QUIT", ct); } catch { }
            var note = info.Expires is { } x && x < DateTime.Now ? "✕ certificate EXPIRED" : info.Expires is { } y && y < DateTime.Now.AddDays(21) ? "⚠ certificate expires within 3 weeks" : !info.Trusted ? $"⚠ certificate not trusted ({info.Errors})" : "✓";
            return new(port, true, banner, info.Protocol, info.Subject + (info.Issuer.Length > 0 ? " · issued by " + info.Issuer : ""), info.Expires, info.Trusted, note);
        }
        catch (Exception ex) { return new(port, true, "", info.Protocol, info.Subject, info.Expires, info.Trusted, "✕ " + ex.Message); }
    }

    /// <summary>Would the server relay mail from a stranger to a stranger? Never sends: RSET after RCPT.</summary>
    public static async Task<(bool Open, string Detail)> RelayAsync(string host, int port = 25, CancellationToken ct = default)
    {
        await Services.Offline.EnforceAsync(host, "an open-relay check", ct);
        using var tcp = new TcpClient();
        using (var t = CancellationTokenSource.CreateLinkedTokenSource(ct)) { t.CancelAfter(8000); await tcp.ConnectAsync(host, port, t.Token); }
        Stream s = tcp.GetStream();
        var c = new Lines(s);
        await c.ReplyAsync(ct);
        var caps = await c.CommandAsync("EHLO dcx-relay-probe.invalid", ct);
        if (caps.Text.Contains("STARTTLS", StringComparison.OrdinalIgnoreCase) && (await c.CommandAsync("STARTTLS", ct)).Code == 220)
        {
            s = await TlsAsync(s, host, new TlsInfo(), ct);
            c = new Lines(s);
            await c.CommandAsync("EHLO dcx-relay-probe.invalid", ct);
        }
        var from = await c.CommandAsync("MAIL FROM:<relay-probe@example.com>", ct);
        if (from.Code != 250) { await Quit(c, ct); return (false, "refused the sender: " + from.Text); }
        var rcpt = await c.CommandAsync("RCPT TO:<relay-probe@example.net>", ct);
        await c.CommandAsync("RSET", ct);
        await Quit(c, ct);
        return rcpt.Code is 250 or 251 ? (true, $"✕ OPEN RELAY: accepted mail for an outside domain without authentication ({rcpt.Code} {rcpt.Text})") : (false, $"✓ relaying refused ({rcpt.Code} {rcpt.Text})");
    }

    public sealed record DnsResult(List<DnsRecords.Mx> Mx, string? Spf, string? Dmarc, string? Dkim, List<MailFinding> Findings);

    public static async Task<DnsResult> DnsAsync(string domain, string? dkimSelector)
    {
        var f = new List<MailFinding>();
        var mx = await DnsRecords.MxAsync(domain);
        f.Add(mx.Count > 0 ? new("good", "MX", string.Join(", ", mx.Select(m => $"{m.Host} ({m.Preference})"))) : new("bad", "MX", $"no MX record for {domain}"));
        var spf = (await DnsRecords.TxtAsync(domain)).FirstOrDefault(t => t.StartsWith("v=spf1", StringComparison.OrdinalIgnoreCase));
        if (spf == null) f.Add(new("bad", "SPF", "no SPF record", $"add a TXT record on {domain}: v=spf1 mx -all (or ~all while testing)"));
        else f.Add(new(spf.Contains("-all") ? "good" : spf.Contains("~all") ? "warn" : "bad", "SPF", spf, spf.Contains("+all") ? "+all lets anyone send as you: use -all" : null));
        var dmarc = (await DnsRecords.TxtAsync("_dmarc." + domain)).FirstOrDefault(t => t.StartsWith("v=DMARC1", StringComparison.OrdinalIgnoreCase));
        if (dmarc == null) f.Add(new("bad", "DMARC", "no DMARC record", $"add a TXT record on _dmarc.{domain}: v=DMARC1; p=quarantine; rua=mailto:postmaster@{domain}"));
        else
        {
            var p = Regex.Match(dmarc, @"\bp=(\w+)", RegexOptions.IgnoreCase).Groups[1].Value.ToLowerInvariant();
            f.Add(new(p is "reject" or "quarantine" ? "good" : "warn", "DMARC", dmarc, p == "none" ? "p=none only reports: move to quarantine or reject once reports look clean" : null));
        }
        string? dkim = null;
        if (dkimSelector is { Length: > 0 } sel)
        {
            dkim = (await DnsRecords.TxtAsync($"{sel}._domainkey.{domain}")).FirstOrDefault();
            f.Add(dkim != null && dkim.Contains("p=") ? new("good", "DKIM", $"{sel}: {Short(dkim)}") : new("bad", "DKIM", $"no DKIM key at {sel}._domainkey.{domain}"));
        }
        return new(mx, spf, dmarc, dkim, f);
    }

    private static string Short(string s) => s.Length > 90 ? s[..90] + "…" : s;
    private static async Task Quit(Lines c, CancellationToken ct) { try { await c.CommandAsync("QUIT", ct); } catch { } }

    private sealed class TlsInfo { public string Protocol = "", Subject = "", Issuer = "", Errors = ""; public DateTime? Expires; public bool Trusted; }

    private static async Task<Stream> TlsAsync(Stream inner, string host, TlsInfo info, CancellationToken ct)
    {
#pragma warning disable CA5359 // a probe: the certificate is examined and reported (trusted or not), nothing secret is sent
        var ssl = new SslStream(inner, false, (_, cert, _, errors) =>
        {
            if (cert is { } c) { var x = new X509Certificate2(c); info.Subject = x.GetNameInfo(X509NameType.SimpleName, false); info.Issuer = x.GetNameInfo(X509NameType.SimpleName, true); info.Expires = x.NotAfter; }
            info.Trusted = errors == SslPolicyErrors.None; info.Errors = errors.ToString();
            return true;
        });
#pragma warning restore CA5359
        using var t = CancellationTokenSource.CreateLinkedTokenSource(ct);
        t.CancelAfter(8000);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host, CertificateRevocationCheckMode = X509RevocationMode.NoCheck }, t.Token);
        info.Protocol = ssl.SslProtocol switch { System.Security.Authentication.SslProtocols.Tls13 => "TLS 1.3", System.Security.Authentication.SslProtocols.Tls12 => "TLS 1.2", var p => p.ToString() + " (old)" };
        return ssl;
    }

    /// <summary>SMTP replies over a stream (multi-line "250-…" until "250 …").</summary>
    private sealed class Lines(Stream s)
    {
        private readonly StringBuilder _buf = new();
        private readonly byte[] _chunk = new byte[2048];

        public async Task<(int Code, string Text)> CommandAsync(string line, CancellationToken ct)
        {
            await s.WriteAsync(Encoding.ASCII.GetBytes(line + "\r\n"), ct);
            await s.FlushAsync(ct);
            return await ReplyAsync(ct);
        }

        public async Task<(int Code, string Text)> ReplyAsync(CancellationToken ct)
        {
            var text = new List<string>();
            using var t = CancellationTokenSource.CreateLinkedTokenSource(ct);
            t.CancelAfter(10000);
            while (true)
            {
                var cur = _buf.ToString();
                var i = cur.IndexOf("\r\n", StringComparison.Ordinal);
                if (i < 0)
                {
                    if (_buf.Length > 16384) throw new IOException("reply too long");
                    var n = await s.ReadAsync(_chunk, t.Token);
                    if (n == 0) throw new IOException("the server closed the connection");
                    _buf.Append(Encoding.UTF8.GetString(_chunk, 0, n));
                    continue;
                }
                _buf.Remove(0, i + 2);
                var l = cur[..i];
                if (l.Length < 3 || !int.TryParse(l[..3], out var code)) throw new IOException("not an SMTP reply");
                text.Add(l.Length > 4 ? l[4..] : "");
                if (l.Length == 3 || l[3] == ' ') return (code, string.Join(" ", text));
            }
        }
    }
}
