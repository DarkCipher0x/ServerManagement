using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using DcxManager.Mail;
using DcxManager.Net;
using DcxManager.Services;
using DcxManager.Views.Toolbox;

namespace DcxManager;

/// <summary>
/// 0.3.0 §18: the network & mail toolbox logic. Mail checks run against a fake DNS (nothing leaves the PC); the network
/// tests run against servers on 127.0.0.1 only (TCP banner, refused port, an HTTP redirect chain, a self-signed TLS
/// service), plus traceroute / path MTU to loopback. Pasted headers are parsed locally.
/// </summary>
public static class ToolboxTests
{
    public static async Task RunAsync(Action<string> ok, Action<string, Exception?> fail)
    {
        var dns = new FakeDns();

        try
        {
            dns.Txt["example.test"] = ["v=spf1 ip4:192.0.2.0/24 include:_spf.mail.test -all", "some-other-txt"];
            dns.Txt["_spf.mail.test"] = ["v=spf1 ip4:198.51.100.10 ~all"];
            var r = await SpfCheck.CheckAsync("example.test", IPAddress.Parse("198.51.100.10"), dns);
            if (r.Result != "pass" || r.Lookups != 1 || !r.Decided.Contains("_spf.mail.test")) throw new Exception($"include pass: {r.Result} {r.Decided} {r.Lookups}");
            if ((await SpfCheck.CheckAsync("example.test", IPAddress.Parse("203.0.113.5"), dns)).Result != "fail") throw new Exception("-all fail");
            if ((await SpfCheck.CheckAsync("example.test", IPAddress.Parse("192.0.2.77"), dns)).Result != "pass") throw new Exception("ip4 range");
            dns.Txt["many.test"] = [("v=spf1 " + string.Join(" ", Enumerable.Range(1, 11).Select(i => $"include:i{i}.test")) + " -all")];
            for (int i = 1; i <= 11; i++) dns.Txt[$"i{i}.test"] = ["v=spf1 -all"];
            var many = await SpfCheck.CheckAsync("many.test", null, dns);
            if (many.Lookups != 11 || !many.Findings.Any(f => f.Level == "bad" && f.Text.Contains("over the limit"))) throw new Exception("10-lookup limit");
            dns.Txt["open.test"] = ["v=spf1 +all"];
            if (!(await SpfCheck.CheckAsync("open.test", null, dns)).Findings.Any(f => f.Level == "bad" && f.Text.StartsWith("+all"))) throw new Exception("+all not flagged");
            dns.Txt["two.test"] = ["v=spf1 -all", "v=spf1 mx -all"];
            if ((await SpfCheck.CheckAsync("two.test", IPAddress.Parse("192.0.2.1"), dns)).Result != "permerror") throw new Exception("two records");
            dns.Txt["loop.test"] = ["v=spf1 include:loop.test -all"];
            if (!(await SpfCheck.CheckAsync("loop.test", null, dns)).Findings.Any(f => f.Text.Contains("loop"))) throw new Exception("include loop");
            ok("SPF: include tree evaluated (198.51.100.10 passes through _spf.mail.test, a stranger fails on -all, an ip4 range passes); 11 lookups flagged over the limit of 10; +all, two SPF records (permerror) and an include loop caught — fake DNS, nothing sent");
        }
        catch (Exception ex) { fail("SPF check", ex); }

        try
        {
            var d = DmarcDkim.Dmarc("v=DMARC1; p=none; pct=50; adkim=s");
            if (!d.Findings.Any(f => f.Level == "warn" && f.Text.StartsWith("p=none")) || !d.Findings.Any(f => f.Text.StartsWith("pct=50")) || !d.Findings.Any(f => f.Text.StartsWith("no rua"))) throw new Exception("DMARC findings");
            using var weak = RSA.Create(1024); using var strong = RSA.Create(2048);
            var k1 = DmarcDkim.Dkim("s1", "v=DKIM1; k=rsa; t=y; p=" + Convert.ToBase64String(weak.ExportSubjectPublicKeyInfo()));
            var k2 = DmarcDkim.Dkim("s2", "v=DKIM1; p=" + Convert.ToBase64String(strong.ExportSubjectPublicKeyInfo()));
            if (k1.Bits != 1024 || !k1.Testing || !k1.Findings.Any(f => f.Level == "warn" && f.Text.Contains("1024")) || k2.Bits != 2048 || k2.Findings[0].Level != "good") throw new Exception("DKIM key strength");
            if (!DmarcDkim.Dkim("old", "v=DKIM1; p=").Revoked) throw new Exception("revoked key");
            dns.Txt["selector1._domainkey.example.test"] = [k2.Record];
            var probe = await DmarcDkim.ProbeAsync("example.test", dns);
            if (probe.Count != 1 || probe[0].Selector != "selector1") throw new Exception("selector probe");
            try { await DmarcDkim.DkimAsync("example.test", "a;rm", dns); throw new Exception("a bad selector was accepted"); } catch (ArgumentException) { }
            ok("DMARC / DKIM: p=none, pct<100 and missing rua flagged; DKIM key strength read from the key (1024-bit warned, 2048-bit good), t=y and revoked keys spotted; the common-selector probe finds selector1; bad selectors refused");
        }
        catch (Exception ex) { fail("DMARC / DKIM", ex); }

        try
        {
            if (Blacklists.QueryName("192.0.2.10", Blacklists.IpLists[0]) != "10.2.0.192.zen.spamhaus.org") throw new Exception("query name");
            try { await Blacklists.CheckAsync("192.168.1.5", dns); throw new Exception("a private address was checked"); } catch (ArgumentException) { }
            dns.Addr["7.113.0.203.bl.spamcop.net"] = [IPAddress.Parse("127.0.0.2")];
            dns.Addr["7.113.0.203.zen.spamhaus.org"] = [IPAddress.Parse("127.255.255.254")];
            var hits = await Blacklists.CheckAsync("203.0.113.7", dns);
            if (hits.Count != Blacklists.IpLists.Length || hits[0].State != "listed" || hits[0].List.Name != "SpamCop" || hits.Single(h => h.List.Zone == "zen.spamhaus.org").State != "refused" || hits.Count(h => h.State == "clean") != hits.Count - 2)
                throw new Exception("states: " + string.Join(", ", hits.Select(h => h.List.Name + "=" + h.State)));
            ok($"blacklists: {Blacklists.IpLists.Length} IP lists asked with the reversed address; a 127.0.0.2 answer shows as listed, Spamhaus's 127.255.255.254 as refused (never as clean); private addresses refused");
        }
        catch (Exception ex) { fail("blacklists", ex); }

        try
        {
            if (!MailPolicies.MxMatches("mx1.example.test", "*.example.test") || MailPolicies.MxMatches("a.b.example.test", "*.example.test") || MailPolicies.MxMatches("example.test", "*.example.test")) throw new Exception("mx pattern");
            dns.Txt["_mta-sts.example.test"] = ["v=STSv1; id=20261020"];
            dns.Mx["example.test"] = [new DnsRecords.Mx("mx1.example.test", 10), new DnsRecords.Mx("backup.other.test", 20)];
            dns.Txt["_smtp._tls.example.test"] = ["v=TLSRPTv1; rua=mailto:tls@example.test"];
            Uri? asked = null;
            var f = await MailPolicies.CheckAsync("example.test", dns, (u, _) => { asked = u; return Task.FromResult("version: STSv1\nmode: enforce\nmx: *.example.test\nmax_age: 604800\n"); });
            if (asked?.ToString() != "https://mta-sts.example.test/.well-known/mta-sts.txt") throw new Exception("policy url " + asked);
            if (!f.Any(x => x.Level == "bad" && x.Text.Contains("backup.other.test")) || !f.Any(x => x.Area == "TLS-RPT" && x.Level == "good")) throw new Exception("MTA-STS findings: " + string.Join(" | ", f.Select(x => x.Text)));
            dns.Ptr["203.0.113.9"] = ["mail.example.test."]; dns.Addr["mail.example.test"] = [IPAddress.Parse("203.0.113.9")];
            dns.Ptr["203.0.113.10"] = ["liar.example.test"];
            if ((await MailPolicies.FcrDnsAsync(IPAddress.Parse("203.0.113.9"), dns)).Level != "good" || (await MailPolicies.FcrDnsAsync(IPAddress.Parse("203.0.113.10"), dns)).Level != "bad") throw new Exception("FCrDNS");
            ok("MTA-STS / TLS-RPT / reverse DNS: the policy is fetched from https://mta-sts.<domain>/.well-known/mta-sts.txt (fake), an MX the policy doesn't cover is flagged, TLS-RPT found; forward-confirmed reverse DNS passes only when the PTR name resolves back");
        }
        catch (Exception ex) { fail("MTA-STS / reverse DNS", ex); }

        try
        {
            const string headers = """
                Received: from mx1.example.test (mx1.example.test [192.0.2.5])
                	by inbound.receiver.test with ESMTPS id abc; Tue, 20 Oct 2026 10:05:30 +0000
                Received: from relay.example.test (relay.example.test [192.0.2.4])
                	by mx1.example.test with ESMTP id def; Tue, 20 Oct 2026 10:05:00 +0000
                Received: from laptop (unknown [198.51.100.20])
                	by relay.example.test with ESMTPSA id ghi; Tue, 20 Oct 2026 09:50:00 +0000
                Authentication-Results: inbound.receiver.test; spf=pass smtp.mailfrom=example.test;
                	dkim=pass header.d=example.test; dmarc=pass header.from=example.test
                DKIM-Signature: v=1; a=rsa-sha256; d=example.test; s=selector1; h=from:to; b=xyz
                From: Ops <ops@example.test>
                Reply-To: someone@elsewhere.test
                Return-Path: <bounce@example.test>
                Subject: test
                Date: Tue, 20 Oct 2026 09:49:58 +0000

                body line that must be ignored: X-Spam-Flag: YES
                """;
            var a = HeaderAnalyzer.Analyze(headers);
            if (a.Hops.Count != 3 || a.Hops[0].By != "relay.example.test" || a.Hops[1].Delay != TimeSpan.FromMinutes(15) || a.Total != TimeSpan.FromMinutes(15.5)) throw new Exception("route: " + string.Join(" | ", a.Hops.Select(h => $"{h.By} {h.Delay}")));
            if (a.Auth.Count(x => x.Result == "pass") != 3 || a.DkimSignatures.Single().Domain != "example.test") throw new Exception("auth results");
            if (!a.Findings.Any(x => x.Area == "Alignment" && x.Level == "good") || !a.Findings.Any(x => x.Area == "Reply-To") || !a.Findings.Any(x => x.Area == "Message-ID") || !a.Findings.Any(x => x.Text.Contains("15 min"))) throw new Exception("findings: " + string.Join(" | ", a.Findings.Select(x => x.Area + ":" + x.Text)));
            if (a.Headers.Any(h => h.Name.StartsWith("X-Spam"))) throw new Exception("the body was read as headers");
            ok("email header analyzer: 3 hops in order with a 15-minute queue delay flagged and the total time; SPF / DKIM / DMARC pass read from Authentication-Results; DKIM d=example.test aligned with From; Reply-To elsewhere and the missing Message-ID warned; the body is never parsed — all local");
        }
        catch (Exception ex) { fail("email header analyzer", ex); }

        // ---- network tests against 127.0.0.1 only ------------------------------------------------------------------
        try
        {
            if (!NetTests.ValidHost("mail.example.test") || NetTests.ValidHost("a;b") || NetTests.ValidHost("$(id)") || NetTests.ValidHost("-oProxy")) throw new Exception("host validation");
            try { NetTests.CheckUrl("https://user:pw@example.test/"); throw new Exception("credentials in a URL accepted"); } catch (ArgumentException) { }
            try { NetTests.RemoteCommand("tcp", "x;reboot", 22); throw new Exception("a bad host reached a command"); } catch (ArgumentException) { }
            var cmd = NetTests.RemoteCommand("dns", "example.test", 0, "MX");
            if (!cmd.Contains("dig +noall +answer 'example.test' MX")) throw new Exception("dns command: " + cmd);

            var banner = new TcpListener(IPAddress.Loopback, 0); banner.Start();
            var port = ((IPEndPoint)banner.LocalEndpoint).Port;
            var serve = Task.Run(async () => { using var c = await banner.AcceptTcpClientAsync(); await c.GetStream().WriteAsync(Encoding.ASCII.GetBytes("220 selftest ESMTP ready\r\n")); await Task.Delay(300); });
            var open = await NetTests.TcpAsync("127.0.0.1", port);
            await serve; banner.Stop();
            if (open.State != "open" || !open.Banner.StartsWith("220 selftest")) throw new Exception("open: " + open.State + " " + open.Banner);
            var closed = await NetTests.TcpAsync("127.0.0.1", port, TimeSpan.FromSeconds(3));
            if (!closed.State.StartsWith("closed")) throw new Exception("closed: " + closed.State);

            var web = new TcpListener(IPAddress.Loopback, 0); web.Start();
            var wport = ((IPEndPoint)web.LocalEndpoint).Port;
            var webTask = Task.Run(async () =>
            {
                for (int i = 0; i < 2; i++)
                {
                    using var c = await web.AcceptTcpClientAsync();
                    var s = c.GetStream(); var buf = new byte[4096]; await s.ReadAtLeastAsync(buf, 1, throwOnEndOfStream: false);
                    var resp = i == 0 ? "HTTP/1.1 301 Moved\r\nLocation: /final\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                                      : "HTTP/1.1 200 OK\r\nX-Content-Type-Options: nosniff\r\nServer: selftest\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok";
                    await s.WriteAsync(Encoding.ASCII.GetBytes(resp));
                }
            });
            var http = await NetTests.HttpAsync($"http://127.0.0.1:{wport}/start");
            await webTask; web.Stop();
            if (http.Error != null || http.Hops.Count != 2 || http.Hops[0].Status != 301 || http.Hops[1].Status != 200 || !http.Hops[1].Url.EndsWith("/final")) throw new Exception("http: " + http.Error + " " + string.Join(" → ", http.Hops.Select(h => h.Status)));
            if (!NetTests.SecurityHeaders(http.Headers).Any(x => x.Header == "X-Content-Type-Options" && x.Present)) throw new Exception("security headers");

            using var key = RSA.Create(2048);
            var req = new CertificateRequest("CN=selftest.local", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            // A PFX round trip gives SChannel a usable private key; it is a temporary key, removed when the certificate is disposed.
            using var cert = X509CertificateLoader.LoadPkcs12(req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(30)).Export(X509ContentType.Pfx), null);
            var tls = new TcpListener(IPAddress.Loopback, 0); tls.Start();
            var tport = ((IPEndPoint)tls.LocalEndpoint).Port;
            var tlsTask = Task.Run(async () =>
            {
                using var c = await tls.AcceptTcpClientAsync();
                await using var ssl = new SslStream(c.GetStream());
                try { await ssl.AuthenticateAsServerAsync(cert); await Task.Delay(300); } catch (Exception) { }
            });
            var info = await NetTests.TlsAsync("127.0.0.1", tport, sni: "selftest.local");
            await tlsTask; tls.Stop();
            if (info.Trusted || !info.Subject.Contains("selftest.local") || info.DaysLeft is < 28 or > 30 || info.Protocol == null) throw new Exception($"tls: {info.Subject} trusted={info.Trusted} days={info.DaysLeft}");

            var hops = await NetTests.TraceAsync("127.0.0.1", names: false);
            if (hops.Count != 1 || hops[0].Status != "destination") throw new Exception("trace: " + string.Join(",", hops.Select(h => h.Address + h.Status)));
            ok("network tests (127.0.0.1 only): TCP open with the service's banner and a refused port told apart; an HTTP 301 → 200 chain followed hop by hop with security headers; a self-signed TLS service's certificate shown as UNTRUSTED with its name and days left; traceroute reaches loopback in one hop; host names, URLs with credentials and shell characters refused before any command is built");
        }
        catch (Exception ex) { fail("network tests", ex); }

        // ---- DNS client (wire format) against a fake resolver on 127.0.0.1: UDP answer with compressed names, TC → TCP ------
        try
        {
            // The same port number over UDP and TCP (a resolver's pair). Windows reserves large blocks of the dynamic range
            // for TCP (Hyper-V / WSL excluded ports → WSAEACCES 10013 on an explicit bind), so the TCP side chooses first
            // (the OS only hands out a port it allows) and UDP binds the same number; a clash on the UDP side draws again.
            UdpClient? udp = null; TcpListener? tcp = null; int port = 0;
            // 0.5.0: both directions (TCP picks and UDP follows, then UDP picks and TCP follows), up to 64 draws: Windows'
            // excluded TCP and UDP ranges move with Hyper-V / WSL and a single direction ran out on one PC.
            for (int attempt = 0; attempt < 64 && udp == null; attempt++)
            {
                if (attempt % 2 == 0)
                {
                    var candidate = new TcpListener(IPAddress.Loopback, 0); candidate.Start();
                    port = ((IPEndPoint)candidate.LocalEndpoint).Port;
                    try { udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port)); tcp = candidate; }
                    catch (SocketException) { candidate.Stop(); }
                }
                else
                {
                    var u = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                    port = ((IPEndPoint)u.Client.LocalEndPoint!).Port;
                    var tl = new TcpListener(IPAddress.Loopback, port);
                    try { tl.Start(); tcp = tl; udp = u; }
                    catch (SocketException) { u.Dispose(); }
                }
            }
            if (udp == null || tcp == null) throw new Exception("no loopback port could be bound for both UDP and TCP");
            using var udpOwner = udp;
            static byte[] Reply(byte[] q, bool truncated, bool withAnswer)
            {
                int p = 12; while (q[p] != 0) p += q[p] + 1; p += 5;   // end of the question
                var r = new List<byte>(q[..p]);
                r[2] = (byte)(0x81 | (truncated ? 0x02 : 0)); r[3] = 0x80; r[6] = 0; r[7] = (byte)(withAnswer ? 2 : 0); r[10] = 0; r[11] = 0;
                if (withAnswer)
                {
                    r.AddRange([0xC0, 0x0C, 0, 1, 0, 1, 0, 0, 1, 44, 0, 4, 192, 0, 2, 1]);                     // A 192.0.2.1 ttl 300, name = pointer to the question
                    r.AddRange([0xC0, 0x0C, 0, 16, 0, 1, 0, 0, 0, 60, 0, 6, 5, (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o']);   // TXT "hello"
                }
                return r.ToArray();
            }
            int udpHits = 0;
            var serve = Task.Run(async () =>
            {
                for (int i = 0; i < 3; i++) { var q = await udp.ReceiveAsync(); udpHits++; await udp.SendAsync(Reply(q.Buffer, truncated: i == 1, withAnswer: i != 1), q.RemoteEndPoint); }
            });
            var tcpServe = Task.Run(async () =>
            {
                using var c = await tcp.AcceptTcpClientAsync();
                var s = c.GetStream(); var len = new byte[2]; await s.ReadExactlyAsync(len); var q = new byte[(len[0] << 8) | len[1]]; await s.ReadExactlyAsync(q);
                var r = Reply(q, false, true); await s.WriteAsync(new[] { (byte)(r.Length >> 8), (byte)r.Length }); await s.WriteAsync(r);
            });
            var a = await DnsClient.QueryAsync("www.example.test", "A", $"127.0.0.1:{port}");
            if (a.Error != null || a.Rcode != "NOERROR" || a.Answers.Count != 2 || a.Answers[0].Data != "192.0.2.1" || a.Answers[0].Name != "www.example.test" || a.Answers[0].Ttl != 300 || a.Answers[1].Data != "hello") throw new Exception("udp answer: " + a.Summary + " " + string.Join(",", a.Answers.Select(x => x.Name + "=" + x.Data)));
            var t = await DnsClient.QueryAsync("www.example.test", "A", $"127.0.0.1:{port}");   // truncated over UDP → asked again over TCP
            await tcpServe; tcp.Stop();
            if (t.Error != null || t.Answers.Count != 2) throw new Exception("tcp fallback: " + t.Summary);
            var rows = await DnsClient.PropagationAsync("www.example.test", "A", [("fake", $"127.0.0.1:{port}"), ("dead", "127.0.0.1:1")]);
            if (rows.Count != 2 || !rows[0].Agrees || rows[1].Response.Error == null || rows[1].Agrees) throw new Exception("propagation: " + string.Join(" | ", rows.Select(r => r.Resolver + " " + r.Response.Summary + " " + r.Agrees)));
            try { DnsClient.ParseServer("evil.test"); throw new Exception("a name was accepted as a resolver"); } catch (ArgumentException) { }
            var loop = DnsClient.Build(7, "a.test", 1); var bad = loop[..^11].ToList(); bad[2] = 0x81; bad[3] = 0x80; bad[7] = 1; bad[11] = 0;   // the OPT record dropped: the answer is the next record
            bad.AddRange([0xC0, (byte)bad.Count, 0, 1, 0, 1, 0, 0, 0, 1, 0, 4, 1, 2, 3, 4]);   // a pointer to itself
            try { DnsClient.Parse(bad.ToArray(), 7, "x", 0); throw new Exception("a self-pointing name was accepted"); } catch (InvalidDataException) { }
            await serve;
            ok($"DNS client: a query built on the wire and the fake resolver's answer parsed (compressed names, A + TXT with TTLs); a truncated UDP answer is re-asked over TCP; propagation marks the resolver that didn't answer as differing; resolvers must be IPs; a self-pointing name is refused ({udpHits} UDP exchanges, 127.0.0.1 only)");
        }
        catch (Exception ex) { fail("DNS client", ex); }

        // ---- WHOIS referral chain and ASN, both with fakes --------------------------------------------------------------
        try
        {
            var asked = new List<string>();
            Task<string> Raw(string server, string q, CancellationToken _)
            {
                asked.Add(server + " " + q);
                return Task.FromResult(server switch
                {
                    "whois.iana.org" => "% IANA WHOIS server\nrefer:        whois.verisign-grs.com\ndomain:       TEST\n",
                    "whois.verisign-grs.com" => "   Domain Name: EXAMPLE.TEST\n   Registrar WHOIS Server: whois.registrar.test\n   Registrar: Example Registrar\n   Name Server: NS1.EXAMPLE.TEST\n",
                    "whois.registrar.test" => "Domain Name: example.test\nRegistrar: Example Registrar\nCreation Date: 2020-01-01T00:00:00Z\nRegistrar WHOIS Server: http://evil.test/;rm\nName Server: ns1.example.test\n",
                    _ => throw new Exception("unexpected server " + server),
                });
            }
            var r = await Whois.LookupAsync("Example.TEST", Raw);
            if (r.Hops.Count != 3 || asked[1] != "whois.verisign-grs.com domain example.test" || asked[2] != "whois.registrar.test example.test") throw new Exception("chain: " + string.Join(" | ", asked));
            var sum = Whois.Summary(r.Hops[^1].Text);
            if (!sum.Any(s => s.Key == "Creation Date") || !sum.Any(s => s.Key == "Name Server")) throw new Exception("summary: " + string.Join(", ", sum.Select(s => s.Key)));
            if (Whois.Referral("whois: 192.0.2.1\n") != null || Whois.Referral("refer: whois.x.test:4444\n") != null || Whois.Referral("refer:  whois.ok.test\n") != "whois.ok.test") throw new Exception("referral parsing");
            try { await Whois.LookupAsync("10.0.0.1", Raw); throw new Exception("private IP accepted"); } catch (ArgumentException) { }
            try { await Whois.LookupAsync("a;b.test", Raw); throw new Exception("bad target accepted"); } catch (ArgumentException) { }
            var asn = await Whois.AsnAsync(IPAddress.Parse("203.0.113.9"), n => Task.FromResult(n switch
            {
                "9.113.0.203.origin.asn.cymru.com" => new List<string> { "64496 | 203.0.113.0/24 | GB | ripencc | 2026-01-01" },
                "AS64496.asn.cymru.com" => ["64496 | GB | ripencc | 2026-01-01 | DCX-TEST, GB"],
                _ => [],
            }));
            if (asn == null || asn.Asn != 64496 || asn.Prefix != "203.0.113.0/24" || asn.Name != "DCX-TEST, GB") throw new Exception("asn: " + asn);
            ok("WHOIS: IANA → Verisign (asked with 'domain ') → the registrar's server, two referrals and no more; a referral with a URL, port or IP is ignored; summary fields picked out; private and malformed targets refused. ASN: origin + AS name through Team Cymru's names (fake DNS)");
        }
        catch (Exception ex) { fail("WHOIS / ASN", ex); }

        // ---- the screen, off screen: tabs, a header analysis, a TCP test to 127.0.0.1, the run lands in History ----------
        try
        {
            var histWas = AppData.Current.ToolboxHistory.ToList();
            try
            {
                using var view = new DcxManager.Views.Toolbox.ToolboxView();
                foreach (var tab in DcxManager.Views.Toolbox.ToolboxView.TabNames) view.Show(tab);
                view.Headers.SetForTest("Received: from a.test (a.test [192.0.2.1])\n\tby b.test; Tue, 20 Oct 2026 10:00:00 +0000\nFrom: x@example.test\nMessage-ID: <1@example.test>\n\nbody");
                var rep = view.Headers.Analyze();
                if (rep.Hops.Count != 1 || view.Headers.FindingsForTest.Count == 0 || AppData.Current.ToolboxHistory.Count != histWas.Count) throw new Exception("headers tab (or it was kept in history without the tick)");
                var banner = new TcpListener(IPAddress.Loopback, 0); banner.Start();
                var port = ((IPEndPoint)banner.LocalEndpoint).Port;
                var serve = Task.Run(async () => { using var c = await banner.AcceptTcpClientAsync(); await c.GetStream().WriteAsync(Encoding.ASCII.GetBytes("SSH-2.0-selftest\r\n")); await Task.Delay(200); });
                view.Network.SetForTest("127.0.0.1", port.ToString());
                await view.Network.TcpAsync();
                await serve; banner.Stop();
                if (!view.Network.OutputForTest.Contains("OPEN") || !view.Network.OutputForTest.Contains("SSH-2.0-selftest")) throw new Exception("network tab: " + view.Network.OutputForTest);
                if (AppData.Current.ToolboxHistory.Count != histWas.Count + 1 || AppData.Current.ToolboxHistory[0].Tool != "TCP port") throw new Exception("history not recorded");
                view.Show("History"); view.History.Reload();
                if (view.History.CountForTest != AppData.Current.ToolboxHistory.Count) throw new Exception("history tab");
                if (DnsTab.Resolvers("1.1.1.1, 9.9.9.9:5353").Count(r => r.Name == "Yours") != 1) throw new Exception("extra resolvers");
                try { DnsTab.Resolvers("evil.test"); throw new Exception("a name was accepted as an extra resolver"); } catch (ArgumentException) { }
                ok("toolbox screen: 8 tabs open; the header analyser works locally and is not kept without the tick; a TCP test from the Network tab finds the 127.0.0.1 service and its banner and the run is in the encrypted History; extra resolvers are IPs only");
            }
            finally { AppData.Current.ToolboxHistory = histWas; AppData.Save(); }
        }
        catch (Exception ex) { fail("toolbox screen", ex); }
    }

    private sealed class FakeDns : IMailDns
    {
        public Dictionary<string, List<string>> Txt { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<IPAddress>> Addr { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<DnsRecords.Mx>> Mx { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<string>> Ptr { get; } = new();
        public Task<List<string>> TxtAsync(string name) => Task.FromResult(Txt.GetValueOrDefault(name) ?? []);
        public Task<List<IPAddress>> AddressesAsync(string name) => Task.FromResult(Addr.GetValueOrDefault(name) ?? []);
        public Task<List<DnsRecords.Mx>> MxAsync(string name) => Task.FromResult(Mx.GetValueOrDefault(name) ?? []);
        public Task<List<string>> PtrAsync(IPAddress ip) => Task.FromResult(Ptr.GetValueOrDefault(ip.ToString()) ?? []);
    }
}
