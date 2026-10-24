using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace DcxManager.Net;

/// <summary>An open port and what answers on it.</summary>
public sealed record OpenPort(string Address, int Port, string Service, string Detail, long ConnectMs);

/// <summary>
/// The built-in port scanner (0.3.0 §7): TCP connect scans (no raw packets, no admin, no driver), at most
/// <c>parallel</c> connections at a time with a per-attempt timeout, then a light, read-mostly look at what answers:
/// the greeting banner (SSH, SMTP, FTP, POP3, IMAP, MySQL…), an HTTP HEAD for web ports (server and page title),
/// TLS for HTTPS-like ports (certificate subject and expiry; nothing is trusted or sent), and the well-known name of
/// the port otherwise. Nothing is ever written to a service beyond an HTTP HEAD request or a TLS handshake.
/// </summary>
public static class PortScanner
{
    private static readonly Dictionary<int, string> Known = new()
    {
        [21] = "FTP", [22] = "SSH", [23] = "Telnet", [25] = "SMTP", [53] = "DNS", [80] = "HTTP", [81] = "HTTP", [88] = "Kerberos", [110] = "POP3",
        [111] = "RPC portmapper", [123] = "NTP", [135] = "MS RPC", [139] = "NetBIOS", [143] = "IMAP", [161] = "SNMP", [389] = "LDAP", [443] = "HTTPS",
        [445] = "SMB", [465] = "SMTP (TLS)", [514] = "syslog / rsh", [587] = "SMTP submission", [631] = "IPP printing", [636] = "LDAPS", [873] = "rsync",
        [902] = "VMware", [993] = "IMAPS", [995] = "POP3S", [1080] = "SOCKS", [1194] = "OpenVPN", [1433] = "SQL Server", [1521] = "Oracle", [1883] = "MQTT",
        [2049] = "NFS", [2375] = "Docker API (plain!)", [2376] = "Docker API (TLS)", [3000] = "HTTP (app)", [3128] = "HTTP proxy", [3268] = "LDAP GC",
        [3306] = "MySQL / MariaDB", [3389] = "Remote Desktop", [4443] = "HTTPS (alt)", [5000] = "HTTP (app) / Synology", [5001] = "HTTPS / Synology",
        [5060] = "SIP", [5432] = "PostgreSQL", [5672] = "AMQP", [5900] = "VNC", [5985] = "WinRM", [5986] = "WinRM (TLS)", [6379] = "Redis",
        [6443] = "Kubernetes API", [6514] = "syslog (TLS)", [8000] = "HTTP (alt)", [8006] = "Proxmox VE", [8008] = "HTTP (alt)", [8080] = "HTTP proxy / alt",
        [8081] = "HTTP (alt)", [8443] = "HTTPS (alt)", [8888] = "HTTP (alt)", [9000] = "HTTP (app) / Portainer", [9090] = "Cockpit / Prometheus",
        [9100] = "printer (JetDirect)", [9200] = "Elasticsearch", [9443] = "HTTPS (Portainer)", [10000] = "Webmin", [11434] = "Ollama", [27017] = "MongoDB",
    };

    private static readonly HashSet<int> TlsPorts = new() { 443, 465, 636, 993, 995, 2376, 4443, 5001, 5986, 6443, 6514, 8006, 8443, 9443, 10000 };
    private static readonly HashSet<int> HttpPorts = new() { 80, 81, 3000, 5000, 8000, 8008, 8080, 8081, 8888, 9000, 9090, 11434 };

    public static string NameOf(int port) => Known.GetValueOrDefault(port, "");

    /// <summary>Scans every address × port; reports each open port (with what was detected) as it is found.</summary>
    public static async Task RunAsync(IReadOnlyList<string> hosts, IReadOnlyList<int> ports, IProgress<OpenPort> found, IProgress<int> progress,
        CancellationToken ct, int parallel = 128, int timeoutMs = 700, bool detect = true)
    {
        Services.Restrictions.Demand("network.scan", "you");   // 0.5.1: Group Policy can turn scanning off
        if (hosts.Count > 0) Services.Offline.EnforceStatic(hosts[0], "a port scan");
        var targets = Services.Offline.On ? hosts.Where(h => Services.Offline.Quick(h).Allowed != false).ToList() : hosts.ToList();
        long total = (long)targets.Count * ports.Count, done = 0;
        int lastPct = -1;
        using var gate = new SemaphoreSlim(Math.Clamp(parallel, 1, 512));
        var work = new List<Task>();
        foreach (var h in targets)
            foreach (var p in ports)
            {
                await gate.WaitAsync(ct);
                work.Add(Task.Run(async () =>
                {
                    try
                    {
                        if (await ProbeAsync(h, p, timeoutMs, detect, ct) is { } open) found.Report(open);
                    }
                    catch (OperationCanceledException) { }
                    catch { }
                    finally
                    {
                        gate.Release();
                        var pct = (int)(Interlocked.Increment(ref done) * 100 / Math.Max(1, total));
                        if (pct != Interlocked.Exchange(ref lastPct, pct)) progress.Report(pct);
                    }
                }, ct));
                if (work.Count > 4096) { work.RemoveAll(t => t.IsCompleted); }
            }
        await Task.WhenAll(work);
    }

    /// <summary>One TCP connect, then (optionally) the look at what answers. Null = closed or filtered.</summary>
    public static async Task<OpenPort?> ProbeAsync(string host, int port, int timeoutMs, bool detect, CancellationToken ct)
    {
        using var tcp = new TcpClient();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            cts.CancelAfter(timeoutMs);
            try { await tcp.ConnectAsync(host, port, cts.Token); } catch { return null; }
        }
        var ms = sw.ElapsedMilliseconds;
        var service = NameOf(port);
        if (!detect) return new OpenPort(host, port, service, "", ms);
        string detail = "";
        try { (service, detail) = await DetectAsync(tcp, host, port, service, ct); } catch { }
        return new OpenPort(host, port, service, detail, ms);
    }

    private static async Task<(string Service, string Detail)> DetectAsync(TcpClient tcp, string host, int port, string service, CancellationToken ct)
    {
        var stream = tcp.GetStream();
        if (TlsPorts.Contains(port)) return await TlsAsync(stream, host, port, service, ct);
        // Many services greet first: wait briefly for a banner.
        var banner = await ReadAsync(stream, 1200, ct);
        if (banner.Length > 0) return Classify(banner, service);
        if (HttpPorts.Contains(port) || service.Length == 0)
        {
            var req = Encoding.ASCII.GetBytes($"HEAD / HTTP/1.0\r\nHost: {host}\r\nUser-Agent: DarkCipher-ServerManagement\r\n\r\n");
            await stream.WriteAsync(req, ct);
            var resp = await ReadAsync(stream, 1500, ct);
            if (resp.StartsWith("HTTP/")) return ("HTTP", Http(resp));
        }
        return (service, "");
    }

    private static (string, string) Classify(string banner, string service)
    {
        var line = banner.Split('\n')[0].Trim();
        if (line.StartsWith("SSH-")) return ("SSH", line);
        if (Regex.IsMatch(line, @"^220[ -].*(SMTP|ESMTP|Postfix|Exim|Sendmail|mail)", RegexOptions.IgnoreCase)) return ("SMTP", line);
        if (line.StartsWith("220")) return (service.Length > 0 ? service : "FTP", line);
        if (line.StartsWith("+OK")) return ("POP3", line);
        if (line.StartsWith("* OK")) return ("IMAP", line);
        if (line.StartsWith("RFB ")) return ("VNC", line);
        if (banner.Contains("mysql_native_password") || banner.Contains("MariaDB")) return ("MySQL / MariaDB", Printable(banner));
        return (service, Printable(line));
    }

    private static string Http(string resp)
    {
        var status = resp.Split('\n')[0].Trim();
        var server = Regex.Match(resp, @"^Server:\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase).Groups[1].Value.Trim();
        return status + (server.Length > 0 ? " · " + server : "");
    }

    private static async Task<(string, string)> TlsAsync(Stream stream, string host, int port, string service, CancellationToken ct)
    {
        X509Certificate2? seen = null;
#pragma warning disable CA5359 // inspection only: the certificate is read and shown; nothing is sent over this connection
        using var ssl = new SslStream(stream, true, (_, c, _, _) => { if (c != null) seen = new X509Certificate2(c); return true; });
#pragma warning restore CA5359
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(2500);
        try
        {
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host, CertificateRevocationCheckMode = X509RevocationMode.NoCheck }, cts.Token);
        }
        catch { if (seen == null) return (service, "TLS handshake failed"); }
        var tls = ssl.IsAuthenticated ? ssl.SslProtocol.ToString().Replace("Tls", "TLS ").Replace("13", "1.3").Replace("12", "1.2") : "TLS";
        var cert = seen == null ? "" : $" · {seen.GetNameInfo(X509NameType.SimpleName, false)} · expires {seen.NotAfter:yyyy-MM-dd}" + (seen.NotAfter < DateTime.Now ? " ⚠ EXPIRED" : seen.NotAfter < DateTime.Now.AddDays(30) ? " ⚠ soon" : "") + (seen.Subject == seen.Issuer ? " · self-signed" : "");
        return (service.Length > 0 ? service : "TLS", tls + cert);
    }

    private static async Task<string> ReadAsync(Stream s, int ms, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ms);
        var buf = new byte[1024];
        try { var n = await s.ReadAsync(buf, cts.Token); return Encoding.UTF8.GetString(buf, 0, n); } catch { return ""; }
    }

    private static string Printable(string s)
    {
        var t = new string(s.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        return t.Length > 160 ? t[..160] + "…" : t;
    }
}
