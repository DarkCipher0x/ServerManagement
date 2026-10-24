using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using DcxManager.Ssh;

namespace DcxManager.Net;

/// <summary>
/// LAN discovery → Tests (0.3.0, DarkCipher0x: "tests like telnet and stuff built in"): the everyday network checks, from this PC or
/// from a saved server over SSH.
/// <list type="bullet">
/// <item><b>TCP port</b> (the "telnet test"): resolves, connects with a timeout, says open / refused / filtered, how long it
/// took, and the banner the service sends first.</item>
/// <item><b>Traceroute</b> and an <b>mtr-style</b> live trace (loss %, best / average / worst per hop) — ICMP with rising
/// TTLs, no administrator rights needed.</item>
/// <item><b>DNS</b>: A, AAAA, CNAME, MX, TXT, NS, SOA, SRV, PTR (Windows' resolver).</item>
/// <item><b>HTTP(S)</b>: status, every redirect hop, time to headers, server / security headers, the certificate.</item>
/// <item><b>TLS certificate</b> of any port (optionally after SMTP / IMAP / POP3 / FTP STARTTLS): protocol, cipher, chain,
/// names, days left, whether Windows trusts it. Inspection only — no data is exchanged after the handshake.</item>
/// <item><b>Path MTU</b>: the largest unfragmented ping (don't-fragment bit, binary search).</item>
/// </list>
/// Every target goes through offline mode; remote commands are built from validated, shell-quoted values.
/// </summary>
public static class NetTests
{
    // ---- validation ---------------------------------------------------------------------------------------------------

    private static readonly Regex HostRx = new(@"^(?=.{1,253}$)(?:[A-Za-z0-9_](?:[A-Za-z0-9_-]{0,61}[A-Za-z0-9_])?\.)*[A-Za-z0-9_](?:[A-Za-z0-9_-]{0,61}[A-Za-z0-9_])?\.?$", RegexOptions.CultureInvariant);

    /// <summary>A host name or an IP address — nothing a shell or a URL could misread.</summary>
    public static bool ValidHost(string h) => IPAddress.TryParse(h, out _) && !h.Contains('%') || HostRx.IsMatch(h);

    public static string CheckHost(string h)
    {
        h = h.Trim();
        if (!ValidHost(h)) throw new ArgumentException("Enter a host name or an IP address.");
        return h;
    }

    public static Uri CheckUrl(string url)
    {
        var u = url.Trim();
        if (!u.Contains("://")) u = "https://" + u;
        if (!Uri.TryCreate(u, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !ValidHost(uri.IdnHost) || u.Any(char.IsControl) || u.Contains(' '))
            throw new ArgumentException("Enter an http:// or https:// address.");
        if (uri.UserInfo.Length > 0) throw new ArgumentException("Leave user names and passwords out of the address.");
        return uri;
    }

    public static readonly string[] DnsTypes = ["A", "AAAA", "CNAME", "MX", "TXT", "NS", "SOA", "SRV", "PTR"];
    public static readonly string[] StartTlsKinds = ["none", "smtp", "imap", "pop3", "ftp"];

    // ---- TCP port ("telnet test") ---------------------------------------------------------------------------------------

    public sealed record TcpResult(string State, string Address, double Ms, string Banner, string Detail);

    public static async Task<TcpResult> TcpAsync(string host, int port, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        host = CheckHost(host);
        if (port is < 1 or > 65535) throw new ArgumentException("A port from 1 to 65535.");
        Services.Offline.EnforceStatic(host, "a port test");
        await Services.Offline.EnforceAsync(host, "a port test", ct);
        IPAddress[] addrs;
        var sw = Stopwatch.StartNew();
        try { addrs = IPAddress.TryParse(host, out var ip) ? [ip] : await Dns.GetHostAddressesAsync(host, ct); }
        catch (SocketException ex) { return new TcpResult("no such name", "", 0, "", ex.Message); }
        var dnsMs = sw.Elapsed.TotalMilliseconds;
        var addr = addrs.OrderBy(a => a.AddressFamily == AddressFamily.InterNetworkV6 ? 1 : 0).First();
        using var tcp = new TcpClient(addr.AddressFamily) { NoDelay = true };
        sw.Restart();
        try
        {
            using (var t = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                t.CancelAfter(timeout ?? TimeSpan.FromSeconds(5));
                await tcp.ConnectAsync(addr, port, t.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new TcpResult("filtered (no answer)", addr.ToString(), sw.Elapsed.TotalMilliseconds, "", "nothing answered before the timeout — a firewall dropping packets, or the host is down"); }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused) { return new TcpResult("closed (refused)", addr.ToString(), sw.Elapsed.TotalMilliseconds, "", "the host answered but nothing listens on that port"); }
        catch (SocketException ex) { return new TcpResult("unreachable", addr.ToString(), sw.Elapsed.TotalMilliseconds, "", ex.Message); }
        var connectMs = sw.Elapsed.TotalMilliseconds;
        // Many services (SSH, SMTP, FTP, POP3, IMAP, MySQL…) speak first: wait briefly for a banner.
        var banner = "";
        try
        {
            using var t = CancellationTokenSource.CreateLinkedTokenSource(ct);
            t.CancelAfter(TimeSpan.FromSeconds(2));
            var buf = new byte[4096];
            var n = await tcp.GetStream().ReadAsync(buf, t.Token);
            banner = Printable(buf.AsSpan(0, n));
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException) { }
        return new TcpResult("open", addr.ToString(), connectMs, banner, $"{(addrs.Length > 1 ? $"{addrs.Length} addresses, tried {addr}; " : "")}name lookup {dnsMs:0} ms");
    }

    private static string Printable(ReadOnlySpan<byte> b)
    {
        var s = Encoding.UTF8.GetString(b);
        return Services.Redaction.Mask(new string(s.Select(c => c is '\r' or '\n' or '\t' || !char.IsControl(c) ? c : '·').ToArray()).TrimEnd());
    }

    // ---- traceroute / mtr --------------------------------------------------------------------------------------------

    public sealed record Hop(int Ttl, string Address, string Name, double? Ms, string Status);

    /// <summary>After this many hops in a row without any reply the trace stops: the destination (or a firewall before it)
    /// drops ICMP, and thirty rows of "*" at a timeout each only looked like a hang (DarkCipher0x, 0.4.0).</summary>
    public const int SilentHopsToStop = 6;

    /// <summary>Traceroute with ICMP: one line per hop as it is found. The three probes of a hop go out together (each on
    /// its own Ping — one Ping takes one request at a time), so a silent hop costs one timeout, not three; a router that
    /// answers "unreachable" / "prohibited" is shown as the last hop; <paramref name="onProbe"/> says which TTL is being
    /// probed (progress while nothing answers).</summary>
    public static async Task<List<Hop>> TraceAsync(string host, Action<Hop>? onHop = null, int maxHops = 30, int timeoutMs = 1500, bool names = true, CancellationToken ct = default, Action<int>? onProbe = null)
    {
        host = CheckHost(host);
        Services.Offline.EnforceStatic(host, "a traceroute");
        await Services.Offline.EnforceAsync(host, "a traceroute", ct);
        var target = IPAddress.TryParse(host, out var ip) ? ip : (await Dns.GetHostAddressesAsync(host, ct)).OrderBy(a => a.AddressFamily == AddressFamily.InterNetworkV6 ? 1 : 0).First();
        var hops = new List<Hop>();
        var pings = new[] { new Ping(), new Ping(), new Ping() };
        try
        {
            int silent = 0;
            for (int ttl = 1; ttl <= Math.Clamp(maxHops, 1, 64); ttl++)
            {
                ct.ThrowIfCancellationRequested();
                onProbe?.Invoke(ttl);
                var replies = await Task.WhenAll(pings.Select(p => ProbeAsync(p, target, ttl, timeoutMs, ct)));
                IPAddress? from = null; double? best = null; IPStatus last = IPStatus.TimedOut; string status = "";
                foreach (var r in replies.Where(r => r.Status is IPStatus.Success or IPStatus.TtlExpired))
                {
                    from = r.From; last = r.Status;
                    best = best == null ? r.Ms : Math.Min(best.Value, r.Ms);
                }
                if (from == null && replies.FirstOrDefault(r => r.From != null && r.Status is IPStatus.DestinationHostUnreachable or IPStatus.DestinationNetworkUnreachable or IPStatus.DestinationProhibited or IPStatus.DestinationProtocolUnreachable or IPStatus.DestinationPortUnreachable or IPStatus.DestinationUnreachable) is { } dead)
                {
                    // ICMP "destination unreachable" from a router: the end of the road, said by whom.
                    from = dead.From; best = dead.Ms; last = dead.Status; status = "unreachable: " + Describe(dead.Status);
                }
                var name = "";
                if (from != null && names) { try { using var t = CancellationTokenSource.CreateLinkedTokenSource(ct); t.CancelAfter(1500); name = (await Dns.GetHostEntryAsync(from.ToString(), t.Token)).HostName; } catch (Exception ex) when (ex is SocketException or OperationCanceledException) { } }
                var hop = new Hop(ttl, from?.ToString() ?? "*", name, best, from == null ? "no reply" : status.Length > 0 ? status : last == IPStatus.Success ? "destination" : "");
                hops.Add(hop);
                onHop?.Invoke(hop);
                if (from != null && (from.Equals(target) || status.Length > 0)) break;
                silent = from == null ? silent + 1 : 0;
                if (silent >= SilentHopsToStop && hops.Count > silent) break;   // something answered earlier; the rest of the path is silent
            }
        }
        finally { foreach (var p in pings) p.Dispose(); }
        return hops;
    }

    private sealed record Probe(IPStatus Status, IPAddress? From, double Ms);

    /// <summary>One echo with a TTL; the time is measured here (Windows reports 0 for a TTL-expired reply). A Ping that
    /// throws (no network at all) counts as a timeout.</summary>
    private static async Task<Probe> ProbeAsync(Ping ping, IPAddress target, int ttl, int timeoutMs, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var r = await ping.SendPingAsync(target, TimeSpan.FromMilliseconds(timeoutMs), new byte[32], new PingOptions(ttl, true), ct);
            var ms = r.Status == IPStatus.Success && r.RoundtripTime > 0 ? r.RoundtripTime : sw.Elapsed.TotalMilliseconds;
            var from = r.Address != null && !r.Address.Equals(IPAddress.Any) && !r.Address.Equals(IPAddress.IPv6Any) ? r.Address : null;
            return new Probe(r.Status, from, ms);
        }
        catch (OperationCanceledException) { throw; }
        catch (PingException) { return new Probe(IPStatus.TimedOut, null, sw.Elapsed.TotalMilliseconds); }
    }

    private static string Describe(IPStatus s) => s switch
    {
        IPStatus.DestinationHostUnreachable => "host unreachable",
        IPStatus.DestinationNetworkUnreachable => "network unreachable",
        IPStatus.DestinationProhibited => "administratively prohibited (a firewall)",   // same code as "protocol unreachable" in IPStatus
        IPStatus.DestinationPortUnreachable => "port unreachable",
        _ => s.ToString(),
    };

    public sealed class MtrRow
    {
        public int Ttl { get; init; }
        public string Address { get; set; } = "*";
        public int Sent { get; set; }
        public int Received { get; set; }
        public double Best { get; set; } = double.MaxValue;
        public double Worst { get; set; }
        public double Total { get; set; }
        public double Last { get; set; }
        public double LossPercent => Sent == 0 ? 0 : 100.0 * (Sent - Received) / Sent;
        public double Average => Received == 0 ? 0 : Total / Received;
        public override string ToString() => $"{Ttl,3}. {Address,-40} {LossPercent,5:0.0}%  {Sent,4}  {(Received > 0 ? $"{Last,7:0.0} {Average,7:0.0} {Best,7:0.0} {Worst,7:0.0}" : "      -       -       -       -")}";
    }

    public const string MtrHeader = "Hop  Address                                   Loss%  Sent    Last     Avg    Best   Worst";

    /// <summary>mtr-style: the route found once (<paramref name="onProbe"/> reports that progress), then every hop pinged
    /// each round — all hops of a round together, one Ping per hop — with that hop's TTL, statistics kept.</summary>
    public static async Task<List<MtrRow>> MtrAsync(string host, int rounds, Action<IReadOnlyList<MtrRow>>? onRound = null, int timeoutMs = 1000, CancellationToken ct = default, Action<int>? onProbe = null)
    {
        var route = await TraceAsync(host, null, 30, timeoutMs, names: false, ct: ct, onProbe: onProbe);
        var target = IPAddress.TryParse(host.Trim(), out var ip) ? ip : (await Dns.GetHostAddressesAsync(host.Trim(), ct)).OrderBy(a => a.AddressFamily == AddressFamily.InterNetworkV6 ? 1 : 0).First();
        var rows = route.Select(h => new MtrRow { Ttl = h.Ttl, Address = h.Address }).ToList();
        var pings = rows.Select(_ => new Ping()).ToArray();
        try
        {
            for (int round = 0; round < Math.Clamp(rounds, 1, 1000); round++)
            {
                ct.ThrowIfCancellationRequested();
                foreach (var row in rows) row.Sent++;
                var replies = await Task.WhenAll(rows.Select((row, i) => ProbeAsync(pings[i], target, row.Ttl, timeoutMs, ct)));
                for (int i = 0; i < rows.Count; i++)
                {
                    var row = rows[i]; var r = replies[i];
                    if (r.Status is not (IPStatus.Success or IPStatus.TtlExpired) || r.From == null) continue;
                    row.Received++; row.Total += r.Ms; row.Last = r.Ms; row.Best = Math.Min(row.Best, r.Ms); row.Worst = Math.Max(row.Worst, r.Ms);
                    if (row.Address == "*") row.Address = r.From.ToString();
                }
                onRound?.Invoke(rows);
                if (round + 1 < rounds) await Task.Delay(1000, ct);
            }
        }
        finally { foreach (var p in pings) p.Dispose(); }
        return rows;
    }

    // ---- DNS --------------------------------------------------------------------------------------------------------------

    public static async Task<List<string>> DnsAsync(string name, string type, CancellationToken ct = default)
    {
        name = name.Trim();
        if (type != "PTR") name = CheckHost(name); else if (!IPAddress.TryParse(name, out _)) name = CheckHost(name);
        Services.Offline.EnforceStatic(name, "a DNS lookup");
        switch (type)
        {
            case "A" or "AAAA":
            {
                var fam = type == "A" ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6;
                try { return (await Dns.GetHostAddressesAsync(name, fam, ct)).Select(a => a.ToString()).ToList(); }
                catch (SocketException) { return []; }
            }
            case "MX": return (await DnsRecords.MxAsync(name)).Select(m => $"{m.Preference,5}  {m.Host}").ToList();
            case "TXT": return await DnsRecords.TxtAsync(name);
            case "NS" or "CNAME" or "PTR": return await DnsRecords.NamesAsync(name, type);
            case "SRV": return (await DnsRecords.SrvAsync(name)).Select(s => $"priority {s.Priority}  weight {s.Weight}  port {s.Port}  {s.Target}").ToList();
            case "SOA":
                return await DnsRecords.SoaAsync(name) is { } soa
                    ? [$"primary {soa.PrimaryServer}", $"admin   {soa.Admin}", $"serial  {soa.Serial}", $"refresh {soa.Refresh}s · retry {soa.Retry}s · expire {soa.Expire}s · minimum TTL {soa.MinimumTtl}s"]
                    : [];
            default: throw new ArgumentException("record type");
        }
    }

    // ---- HTTP(S) ----------------------------------------------------------------------------------------------------------

    public sealed record HttpHop(string Url, int Status, string Reason, double Ms, string? Location);
    public sealed record HttpResult(List<HttpHop> Hops, Dictionary<string, string> Headers, CertInfo? Certificate, string? Error);

    /// <summary>Follows up to 10 redirects one by one (each shown), times each, and reports the final headers and the
    /// certificate as Windows validated it. Nothing is sent but GET; no cookies, no credentials.</summary>
    public static async Task<HttpResult> HttpAsync(string url, CancellationToken ct = default, HttpMessageHandler? handler = null)
    {
        var uri = CheckUrl(url);
        var hops = new List<HttpHop>();
        CertInfo? cert = null;
        handler ??= new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, UseProxy = true, ConnectTimeout = TimeSpan.FromSeconds(10),
            SslOptions = new SslClientAuthenticationOptions
            {
                // Validation stays Windows' decision; the callback only records what was seen.
                RemoteCertificateValidationCallback = (_, c, chain, e) => { if (c != null) cert = CertInfo.From(new X509Certificate2(c), chain, e, null, null); return e == SslPolicyErrors.None; },
            },
        };
        using var http = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DarkCipher-ServerManagement-HttpCheck/1.0");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            for (int i = 0; i < 10; i++)
            {
                Services.Offline.EnforceStatic(uri.IdnHost, "an HTTP check");
                await Services.Offline.EnforceAsync(uri.IdnHost, "an HTTP check", ct);
                var sw = Stopwatch.StartNew();
                using var r = await http.SendAsync(new HttpRequestMessage(HttpMethod.Get, uri), HttpCompletionOption.ResponseHeadersRead, ct);
                var loc = r.Headers.Location;
                var next = loc == null ? null : loc.IsAbsoluteUri ? loc : new Uri(uri, loc);
                hops.Add(new HttpHop(uri.ToString(), (int)r.StatusCode, r.ReasonPhrase ?? "", sw.Elapsed.TotalMilliseconds, next?.ToString()));
                headers.Clear();
                foreach (var h in r.Headers.Concat(r.Content.Headers)) headers[h.Key] = Services.Redaction.Mask(string.Join(", ", h.Value));
                if ((int)r.StatusCode is < 300 or > 399 || next == null) break;
                if (next.Scheme is not ("http" or "https")) break;
                uri = next;
            }
            return new HttpResult(hops, headers, cert, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or AuthenticationException)
        {
            var why = ex is TaskCanceledException ? "timed out" : ex.InnerException is AuthenticationException ae ? "certificate / TLS: " + ae.Message : ex.Message;
            return new HttpResult(hops, headers, cert, why);
        }
    }

    /// <summary>Security headers worth having on a web UI, and whether each is present.</summary>
    public static IEnumerable<(string Header, bool Present)> SecurityHeaders(IReadOnlyDictionary<string, string> h) =>
        new[] { "Strict-Transport-Security", "Content-Security-Policy", "X-Content-Type-Options", "X-Frame-Options", "Referrer-Policy" }.Select(x => (x, h.ContainsKey(x)));

    // ---- TLS certificate ---------------------------------------------------------------------------------------------

    public sealed record CertInfo(string Subject, string Issuer, IReadOnlyList<string> Names, DateTime NotBefore, DateTime NotAfter, string Sha256,
        bool Trusted, string Errors, IReadOnlyList<string> Chain, string? Protocol, string? Cipher)
    {
        public int DaysLeft => (int)Math.Floor((NotAfter - DateTime.Now).TotalDays);

        public static CertInfo From(X509Certificate2 c, X509Chain? chain, SslPolicyErrors e, string? protocol, string? cipher)
        {
            var names = new List<string>();
            foreach (var ext in c.Extensions.OfType<X509SubjectAlternativeNameExtension>()) { names.AddRange(ext.EnumerateDnsNames()); names.AddRange(ext.EnumerateIPAddresses().Select(a => a.ToString())); }
            var ch = chain?.ChainElements.Select(x => x.Certificate.GetNameInfo(X509NameType.SimpleName, false)).ToList() ?? [];
            return new CertInfo(c.Subject, c.Issuer, names, c.NotBefore, c.NotAfter, c.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256), e == SslPolicyErrors.None,
                e == SslPolicyErrors.None ? "" : e.ToString(), ch, protocol, cipher);
        }
    }

    /// <summary>The certificate a TLS service presents (after STARTTLS for mail/FTP when chosen). Inspection only: the
    /// handshake completes even for an untrusted certificate so it can be shown, then the connection is closed without
    /// sending anything — the result says plainly whether Windows trusts it.</summary>
    public static async Task<CertInfo> TlsAsync(string host, int port, string startTls = "none", string? sni = null, CancellationToken ct = default)
    {
        host = CheckHost(host);
        if (port is < 1 or > 65535) throw new ArgumentException("A port from 1 to 65535.");
        if (!StartTlsKinds.Contains(startTls)) throw new ArgumentException("STARTTLS kind");
        sni = string.IsNullOrWhiteSpace(sni) ? host : CheckHost(sni);
        Services.Offline.EnforceStatic(host, "a certificate check");
        await Services.Offline.EnforceAsync(host, "a certificate check", ct);
        using var tcp = new TcpClient();
        using (var t = CancellationTokenSource.CreateLinkedTokenSource(ct)) { t.CancelAfter(TimeSpan.FromSeconds(10)); await tcp.ConnectAsync(host, port, t.Token); }
        Stream s = tcp.GetStream();
        s.ReadTimeout = 10000; s.WriteTimeout = 10000;
        if (startTls != "none") await StartTlsAsync(s, startTls, ct);
        CertInfo? info = null;
#pragma warning disable CA5359 // inspection only: the certificate is reported (with Windows' verdict) and no data is exchanged
        // The chain Windows built is read inside the callback (it is disposed afterwards); nothing new is downloaded.
        await using var ssl = new SslStream(s, false, (_, c, chain, e) =>
        {
            if (c != null) info = CertInfo.From(new X509Certificate2(c), chain, e, null, null);
            return true;
        });
#pragma warning restore CA5359
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = sni }, ct);
        if (info == null) throw new AuthenticationException("no certificate was presented");
        return info with { Protocol = ssl.SslProtocol.ToString(), Cipher = ssl.NegotiatedCipherSuite.ToString() };
    }

    private static async Task StartTlsAsync(Stream s, string kind, CancellationToken ct)
    {
        var r = new StreamReader(s, Encoding.ASCII, false, 1024, leaveOpen: true);
        async Task<string> Line() => await r.ReadLineAsync(ct) ?? throw new IOException("the server closed the connection");
        async Task Send(string line) { var b = Encoding.ASCII.GetBytes(line + "\r\n"); await s.WriteAsync(b, ct); await s.FlushAsync(ct); }
        async Task<string> Multi() { string l; do l = await Line(); while (l.Length > 3 && l[3] == '-'); return l; }   // SMTP / FTP multi-line replies
        switch (kind)
        {
            case "smtp":
                await Multi(); await Send("EHLO dcx-tls-check.local");
                if (!(await Multi()).StartsWith("250")) throw new IOException("EHLO refused");
                await Send("STARTTLS");
                if (!(await Line()).StartsWith("220")) throw new IOException("STARTTLS not offered");
                break;
            case "imap":
                await Line(); await Send("a1 STARTTLS");
                string l; do l = await Line(); while (!l.StartsWith("a1 "));
                if (!l.StartsWith("a1 OK", StringComparison.OrdinalIgnoreCase)) throw new IOException("STARTTLS refused: " + l);
                break;
            case "pop3":
                await Line(); await Send("STLS");
                if (!(await Line()).StartsWith("+OK")) throw new IOException("STLS refused");
                break;
            case "ftp":
                await Multi(); await Send("AUTH TLS");
                if (!(await Multi()).StartsWith("234")) throw new IOException("AUTH TLS refused");
                break;
        }
    }

    // ---- path MTU ---------------------------------------------------------------------------------------------------------

    /// <summary>The largest packet that reaches the host unfragmented (ICMP with don't-fragment; binary search 68–9000).</summary>
    public static async Task<int?> PathMtuAsync(string host, CancellationToken ct = default)
    {
        host = CheckHost(host);
        Services.Offline.EnforceStatic(host, "an MTU test");
        await Services.Offline.EnforceAsync(host, "an MTU test", ct);
        var target = IPAddress.TryParse(host, out var ip) ? ip : (await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct)).First();
        using var ping = new Ping();
        async Task<bool> Fits(int payload) { var r = await ping.SendPingAsync(target, TimeSpan.FromSeconds(2), new byte[payload], new PingOptions(64, true), ct); return r.Status == IPStatus.Success; }
        if (!await Fits(0)) return null;
        int lo = 0, hi = 9000 - 28;
        while (lo < hi) { var mid = (lo + hi + 1) / 2; if (await Fits(mid)) lo = mid; else hi = mid - 1; }
        return lo + 28;   // + IPv4 (20) and ICMP (8) headers
    }

    // ---- the same tests from a saved server (SSH) ---------------------------------------------------------------------------

    /// <summary>The shell command that runs a test on a Linux server (tools tried in order; every value validated and quoted).</summary>
    public static string RemoteCommand(string test, string target, int port = 0, string dnsType = "A", string startTls = "none")
    {
        string Q(string s) => RemoteExec.Quote(s);
        switch (test)
        {
            case "tcp":
            {
                var h = CheckHost(target);
                if (port is < 1 or > 65535) throw new ArgumentException("A port from 1 to 65535.");
                return $"if command -v nc >/dev/null 2>&1; then nc -vz -w 5 {Q(h)} {port} 2>&1; " +
                       $"else timeout 5 bash -c {Q($"exec 3<>/dev/tcp/{h}/{port}")} 2>&1 && echo 'open' || echo 'closed or filtered'; fi";
            }
            case "trace":
            {
                var h = CheckHost(target);
                return $"if command -v mtr >/dev/null 2>&1; then mtr -rwn -c 3 {Q(h)}; elif command -v traceroute >/dev/null 2>&1; then traceroute -n -w 2 {Q(h)}; " +
                       $"elif command -v tracepath >/dev/null 2>&1; then tracepath -n {Q(h)}; else echo 'no mtr, traceroute or tracepath on this server'; fi";
            }
            case "dns":
            {
                if (!DnsTypes.Contains(dnsType)) throw new ArgumentException("record type");
                var n = dnsType == "PTR" && IPAddress.TryParse(target.Trim(), out _) ? target.Trim() : CheckHost(target);
                return dnsType == "PTR"
                    ? $"if command -v dig >/dev/null 2>&1; then dig +noall +answer -x {Q(n)}; else host {Q(n)}; fi"
                    : $"if command -v dig >/dev/null 2>&1; then dig +noall +answer {Q(n)} {dnsType}; elif command -v host >/dev/null 2>&1; then host -t {dnsType} {Q(n)}; else nslookup -type={dnsType} {Q(n)}; fi";
            }
            case "http":
            {
                var u = CheckUrl(target).ToString();
                return $"curl -sS -o /dev/null -L --max-redirs 10 --max-time 30 -w {Q("%{http_code} after %{num_redirects} redirect(s) · %{time_total}s · %{url_effective}\\n")} {Q(u)}";
            }
            case "tls":
            {
                var h = CheckHost(target);
                if (port is < 1 or > 65535) throw new ArgumentException("A port from 1 to 65535.");
                if (!StartTlsKinds.Contains(startTls)) throw new ArgumentException("STARTTLS kind");
                var st = startTls == "none" ? "" : $" -starttls {startTls}";
                return $"echo | timeout 15 openssl s_client -connect {Q(h + ":" + port)} -servername {Q(h)}{st} 2>/dev/null | openssl x509 -noout -subject -issuer -dates -ext subjectAltName 2>&1";
            }
            case "mtu":
            {
                var h = CheckHost(target);
                return $"if command -v tracepath >/dev/null 2>&1; then tracepath -n {Q(h)} | tail -n 3; else ping -c 1 -M do -s 1472 {Q(h)}; fi";
            }
            default: throw new ArgumentException("unknown test");
        }
    }
}
