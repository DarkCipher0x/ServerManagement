using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using DcxManager.Notify;
using DcxManager.Services;

namespace DcxManager;

/// <summary>
/// 0.3.0 preview.5: syslog over TLS and e-mail notifications, against fakes on 127.0.0.1 only (no firewall prompts):
/// RFC 5424 format and escaping, masking, octet-counting frames; a TLS collector with a pinned self-signed certificate
/// receives audit lines; a wrong pin is refused and messages wait in the queue, then go once it's right; nothing ever
/// goes out unencrypted (a plain listener only ever sees a TLS handshake). SMTP: STARTTLS before AUTH, no plaintext
/// fallback, header injection stripped; notification rules, classification and the hourly limit; the protected items
/// validate their values; the Privacy &amp; network box shows the state.
/// </summary>
public static class NotifyTests
{
    public static async Task RunAsync(MainWindow main, Action<string> ok, Action<string, Exception?> fail)
    {
        var s = AppSettings.Current;
        var saved = (s.SyslogEnabled, s.SyslogHost, s.SyslogPort, s.SyslogPin, s.SyslogClientCert, s.SyslogSendAudit, s.SyslogSendAppLog,
                     s.SmtpEnabled, s.SmtpHost, s.SmtpPort, s.SmtpImplicitTls, s.SmtpUser, s.SmtpPassword, s.SmtpFrom, s.SmtpTo, s.NotifyMaxPerHour, s.NotifySecurity);

        // 1. The message format.
        try
        {
            var t = new DateTimeOffset(2026, 10, 20, 18, 0, 0, 123, TimeSpan.FromHours(1));
            var m = SyslogMessage.Build(t, SyslogMessage.Severity.Notice, "audit", [("actor", "you"), ("target", "a\"b]c\\d")], "line one\nline two password=Hunter2Secret!", host: "pc 01");
            var text = Encoding.UTF8.GetString(m);
            if (!text.StartsWith("<133>1 2026-10-20T18:00:00.123+01:00 pc01 DarkCipherServerManagement ")) throw new Exception("header: " + text[..80]);
            if (!text.Contains("[dcx@32473 actor=\"you\" target=\"a\\\"b\\]c\\\\d\"]")) throw new Exception("structured data escaping: " + text);
            if (text.Contains('\n') || text.Contains("Hunter2Secret")) throw new Exception("newline or secret in the message: " + text);
            if (m.AsSpan().IndexOf(new byte[] { 0xEF, 0xBB, 0xBF }) < 0) throw new Exception("no BOM before MSG");
            var framed = Encoding.ASCII.GetString(SyslogMessage.Frame(m)[..8]);
            if (!framed.StartsWith(m.Length + " ")) throw new Exception("octet-counting frame: " + framed);
            ok("syslog format: RFC 5424 header (PRI local0.notice = 133, ISO time with offset, no spaces in fields), [dcx@32473 …] structured data escaped, BOM before the UTF-8 text, one line, secrets masked; RFC 5425 octet-counting frame");
        }
        catch (Exception ex) { fail("syslog format", ex); }

        // 2. A TLS collector on 127.0.0.1 with a pinned self-signed certificate.
        X509Certificate2? cert = null;
        Collector? col = null;
        try
        {
            cert = TestWeb.MakeCert("127.0.0.1");
            col = new Collector(cert);
            var pin = Convert.ToHexString(SHA256.HashData(cert.RawData));
            s.SyslogEnabled = true; s.SyslogHost = "127.0.0.1"; s.SyslogPort = col.Port; s.SyslogPin = "00" + pin[2..]; s.SyslogClientCert = ""; s.SyslogSendAudit = true; s.SyslogSendAppLog = true;
            SyslogForwarder.Start();
            // Wrong pin: refused, nothing delivered, the line waits in the queue.
            AuditLog.Write("selftest", "syslog", "pinned-mismatch-check");
            await Until(() => SyslogForwarder.Status.State.StartsWith("failing"), 10_000, "a wrong pin wasn't refused");
            if (col.Received.Any(r => r.Contains("pinned-mismatch-check")) || SyslogForwarder.Status.Queued == 0) throw new Exception("delivered despite a wrong pin, or not queued");
            // Right pin: the queued line and a new one arrive, framed, over TLS 1.2+.
            s.SyslogPin = pin;
            var test = await SyslogForwarder.SendTestAsync();
            if (!test.StartsWith("✓")) throw new Exception("test message: " + test);
            AuditLog.Write("selftest", "syslog", "delivered-check");
            await Until(() => col.Received.Any(r => r.Contains("delivered-check")) && col.Received.Any(r => r.Contains("pinned-mismatch-check")), 15_000, "queued lines not delivered: " + SyslogForwarder.Status);
            var st = SyslogForwarder.Status;
            if (st.State != "connected" || st.Tls is not ("TLS 1.2" or "TLS 1.3") || st.CertExpires == null || !col.FramesOk) throw new Exception("status / framing: " + st);
            ok($"syslog over TLS: a wrong certificate pin is refused and the audit line waits in the encrypted queue; with the right pin it and the next ones arrive octet-framed over {st.Tls}; test message ✓; status shows server, TLS, certificate expiry, last sent");

            // The Privacy & network box says so.
            var pv = new Views.PrivacyView();
            pv.Refresh();
            var shown = pv.SyslogText;
            if (!shown.Contains("State: connected") || !shown.Contains("TLS only") || !shown.Contains("certificate pinned")) throw new Exception("Privacy box: " + shown);
            ok("Privacy & network: the Syslog (TLS) box shows state, server, TLS version, pin, certificate and expiry, last sent and the queue");
        }
        catch (Exception ex) { fail("syslog over TLS", ex); }
        finally
        {
            s.SyslogEnabled = false;
            col?.Dispose();
        }

        // 3. Nothing unencrypted: a plain TCP listener only ever sees a TLS handshake (0x16), never a syslog line.
        try
        {
            var plain = new TcpListener(IPAddress.Loopback, 0);
            plain.Start();
            var port = ((IPEndPoint)plain.LocalEndpoint).Port;
            s.SyslogHost = "127.0.0.1"; s.SyslogPort = port; s.SyslogPin = "";
            var accept = plain.AcceptTcpClientAsync();
            var send = SyslogForwarder.SendTestAsync();
            using (var c = await accept.WaitAsync(TimeSpan.FromSeconds(10)))
            {
                var first = new byte[1];
                var n = await c.GetStream().ReadAsync(first).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                if (n != 1 || first[0] != 0x16) throw new Exception($"first byte 0x{first[0]:X2}, not a TLS handshake");
            }
            plain.Stop();
            var r = await send;
            if (r.StartsWith("✓")) throw new Exception("a plain listener was accepted as a collector");
            ok("syslog: no plaintext path — even pointed at a plain TCP listener the first byte is a TLS handshake and the send fails");
        }
        catch (Exception ex) { fail("syslog plaintext refusal", ex); }

        // 4. SMTP: STARTTLS before anything secret, refusal without it, header injection, a STARTTLS + AUTH PLAIN delivery.
        Smtp? smtp = null;
        try
        {
            cert ??= TestWeb.MakeCert("127.0.0.1");
            var certHash = cert.GetCertHashString(HashAlgorithmName.SHA256);
            SmtpSender.TestValidation = (_, c, _, _) => c != null && new X509Certificate2(c).GetCertHashString(HashAlgorithmName.SHA256) == certHash;
            smtp = new Smtp(cert, offerStartTls: false);
            var server = new SmtpSender.Server("127.0.0.1", smtp.Port, false, "notify@example.test", "smtp-Secret-Pw-77", "notify@example.test");
            try { await SmtpSender.SendAsync(server, ["ops@example.test"], "x", "y"); throw new Exception("sent without STARTTLS"); }
            catch (System.Security.Authentication.AuthenticationException) { }
            if (smtp.Log.Any(l => l.StartsWith("AUTH") || l.StartsWith("MAIL"))) throw new Exception("said AUTH/MAIL in clear: " + string.Join(" | ", smtp.Log));
            smtp.Dispose();
            smtp = new Smtp(cert, offerStartTls: true);
            server = server with { Port = smtp.Port };
            await SmtpSender.SendAsync(server, ["ops@example.test", "lead@example.test"], "Disk full\r\nBcc: attacker@example.test", "Line 1\nLine 2 ✓");
            var log = string.Join(" | ", smtp.Log);
            if (smtp.Log.FindIndex(l => l == "STARTTLS") is var i && (i < 0 || smtp.Log.FindIndex(l => l.StartsWith("AUTH PLAIN")) < i || !smtp.AuthInTls)) throw new Exception("AUTH before TLS: " + log);
            if (smtp.Log.Count(l => l.StartsWith("RCPT TO")) != 2) throw new Exception("recipients: " + log);
            var msg = smtp.Data;
            if (msg.Contains("\r\nBcc:") || !msg.Contains("Subject: =?utf-8?B?")) throw new Exception("header injection: " + msg);
            var body = Encoding.UTF8.GetString(Convert.FromBase64String(string.Concat(msg[(msg.IndexOf("\r\n\r\n") + 4)..].Split("\r\n"))));
            if (body != "Line 1\r\nLine 2 ✓") throw new Exception("body: " + body);
            ok("SMTP: a server without STARTTLS is refused before AUTH or MAIL; with STARTTLS the password is sent only inside TLS (AUTH PLAIN), two recipients, CR/LF in the subject can't add a header, UTF-8 body Base64");
        }
        catch (Exception ex) { fail("SMTP", ex); }
        finally { smtp?.Dispose(); SmtpSender.TestValidation = null; cert?.Dispose(); }

        // 5. Notification rules: classification, switches, hourly limit with the held list in the next mail.
        try
        {
            var sent = new List<(string Subject, string Body)>();
            Notifier.SendOverride = (_, _, subj, body) => { lock (sent) sent.Add((subj, body)); return Task.CompletedTask; };
            Notifier.ResetForTest();
            var clock = DateTime.UtcNow;
            Notifier.Now = () => clock;
            s.SmtpEnabled = true; s.SmtpHost = "127.0.0.1"; s.SmtpTo = "ops@example.test"; s.SmtpFrom = "notify@example.test"; s.NotifySecurity = true; s.NotifyMaxPerHour = 2;
            Notifier.Start();
            AuditLog.Write("app", "app", "failed sign-in attempt");
            AuditLog.Write("you", "notes", "note saved");   // not a notification
            AuditLog.Write("app", "protected settings", "changed with administrator approval", "net.telnet");
            AuditLog.Write("app", "app", "failed sign-in attempt", "again");   // over the limit: held
            await Until(() => { lock (sent) return sent.Count >= 2; }, 5000, "notifications not sent");
            await Task.Delay(200);
            if (sent.Count != 2 || !sent[0].Subject.Contains("Failed sign-in") || !sent[1].Subject.Contains("Protected settings")) throw new Exception("sent: " + string.Join(" | ", sent.Select(x => x.Subject)));
            clock = clock.AddMinutes(61);
            Notifier.Report(Notifier.Kind.BackupFailed, "A snapshot of the store failed", "test");
            await Until(() => { lock (sent) return sent.Count == 3; }, 5000, "the next mail didn't go after the hour");
            if (!sent[2].Body.Contains("held back by the hourly limit") || !sent[2].Body.Contains("Failed sign-in")) throw new Exception("held list missing: " + sent[2].Body);
            s.NotifySecurity = false;
            Notifier.Report(Notifier.Kind.Security, "x", "y");
            await Task.Delay(200);
            if (sent.Count != 3) throw new Exception("a switched-off kind was sent");
            if (sent.Any(x => x.Body.Contains("smtp-Secret"))) throw new Exception("a secret in a notification");
            ok("notifications: security audit events become e-mails, ordinary ones don't; the hourly limit holds the rest and lists them in the next e-mail; a switched-off kind sends nothing");
        }
        catch (Exception ex) { fail("notification rules", ex); }
        finally { Notifier.SendOverride = null; Notifier.Now = () => DateTime.UtcNow; Notifier.ResetForTest(); }

        // 6. Protected items validate what can be stored for them (HKLM never takes junk).
        try
        {
            string? Check(string key, System.Text.Json.Nodes.JsonNode? v) => ProtectedSettings.Find(key)!.Check(v);
            if (Check("syslog.host", "logs.example.test") != null || Check("syslog.host", "evil host;rm") == null) throw new Exception("host check");
            if (Check("syslog.pin", new string('A', 64)) != null || Check("syslog.pin", "XYZ") == null) throw new Exception("pin check");
            if (Check("smtp.to", "a@example.test, b@example.test") != null || Check("smtp.to", "a@example.test\r\nBcc: x@y") == null) throw new Exception("recipient check");
            if (PolicySettings.Catalog.Any(p => p.Name == nameof(AppSettings.SmtpPassword))) throw new Exception("the SMTP password is a Group Policy");
            ok("protected syslog / SMTP items: host names, SHA-256 pins and e-mail lists validated (no CR/LF); the SMTP password is never a Group Policy");
        }
        catch (Exception ex) { fail("protected notify items", ex); }
        finally
        {
            (s.SyslogEnabled, s.SyslogHost, s.SyslogPort, s.SyslogPin, s.SyslogClientCert, s.SyslogSendAudit, s.SyslogSendAppLog,
             s.SmtpEnabled, s.SmtpHost, s.SmtpPort, s.SmtpImplicitTls, s.SmtpUser, s.SmtpPassword, s.SmtpFrom, s.SmtpTo, s.NotifyMaxPerHour, s.NotifySecurity) = saved;
            AppSettings.Save();
        }
    }

    private static async Task Until(Func<bool> cond, int ms, string why)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!cond()) { if (sw.ElapsedMilliseconds > ms) throw new TimeoutException(why); await Task.Delay(50); }
    }

    /// <summary>A TLS syslog collector (RFC 5425 framing) on 127.0.0.1.</summary>
    private sealed class Collector : IDisposable
    {
        private readonly TcpListener _l = new(IPAddress.Loopback, 0);
        private readonly X509Certificate2 _cert;
        private readonly CancellationTokenSource _cts = new();
        public int Port { get; }
        public List<string> Received { get; } = new();
        public bool FramesOk { get; private set; } = true;

        public Collector(X509Certificate2 cert)
        {
            _cert = cert;
            _l.Start();
            Port = ((IPEndPoint)_l.LocalEndpoint).Port;
            _ = Task.Run(Accept);
        }

        private async Task Accept()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient c;
                try { c = await _l.AcceptTcpClientAsync(_cts.Token); } catch { return; }
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var _ = c;
                        using var ssl = new SslStream(c.GetStream());
                        await ssl.AuthenticateAsServerAsync(_cert);
                        var buf = new List<byte>(); var chunk = new byte[4096];
                        while (true)
                        {
                            var n = await ssl.ReadAsync(chunk, _cts.Token);
                            if (n == 0) return;
                            buf.AddRange(chunk.AsSpan(0, n).ToArray());
                            while (true)   // LENGTH SP MSG
                            {
                                var sp = buf.IndexOf((byte)' ');
                                if (sp < 0) break;
                                if (!int.TryParse(Encoding.ASCII.GetString(buf.GetRange(0, sp).ToArray()), out var len)) { FramesOk = false; return; }
                                if (buf.Count < sp + 1 + len) break;
                                var msg = Encoding.UTF8.GetString(buf.GetRange(sp + 1, len).ToArray());
                                if (!msg.StartsWith('<')) FramesOk = false;
                                lock (Received) Received.Add(msg);
                                buf.RemoveRange(0, sp + 1 + len);
                            }
                        }
                    }
                    catch { }
                });
            }
        }

        public void Dispose() { _cts.Cancel(); _l.Stop(); }
    }

    /// <summary>A small SMTP server on 127.0.0.1: EHLO (with or without STARTTLS), STARTTLS, AUTH PLAIN, MAIL, RCPT, DATA.</summary>
    private sealed class Smtp : IDisposable
    {
        private readonly TcpListener _l = new(IPAddress.Loopback, 0);
        private readonly X509Certificate2 _cert;
        private readonly bool _startTls;
        public int Port { get; }
        public List<string> Log { get; } = new();
        public string Data { get; private set; } = "";
        public bool AuthInTls { get; private set; }

        public Smtp(X509Certificate2 cert, bool offerStartTls)
        {
            _cert = cert; _startTls = offerStartTls;
            _l.Start();
            Port = ((IPEndPoint)_l.LocalEndpoint).Port;
            _ = Task.Run(Serve);
        }

        private async Task Serve()
        {
            try
            {
                using var c = await _l.AcceptTcpClientAsync();
                Stream s = c.GetStream();
                bool tls = false;
                var r = new StreamReader(s, Encoding.UTF8, false, 1024, true); var w = new StreamWriter(s, new UTF8Encoding(false), 1024, true) { NewLine = "\r\n", AutoFlush = true };
                await w.WriteLineAsync("220 fake ESMTP");
                while (await r.ReadLineAsync() is { } line)
                {
                    lock (Log) Log.Add(line);
                    if (line.StartsWith("EHLO")) await w.WriteAsync("250-fake\r\n" + (_startTls && !tls ? "250-STARTTLS\r\n" : "") + "250 AUTH PLAIN LOGIN\r\n");
                    else if (line == "STARTTLS" && _startTls)
                    {
                        await w.WriteLineAsync("220 go ahead");
                        var ssl = new SslStream(s); await ssl.AuthenticateAsServerAsync(_cert); s = ssl; tls = true;
                        r = new StreamReader(s, Encoding.UTF8, false, 1024, true); w = new StreamWriter(s, new UTF8Encoding(false), 1024, true) { NewLine = "\r\n", AutoFlush = true };
                    }
                    else if (line.StartsWith("AUTH PLAIN")) { AuthInTls = tls; await w.WriteLineAsync("235 ok"); }
                    else if (line.StartsWith("MAIL") || line.StartsWith("RCPT")) await w.WriteLineAsync("250 ok");
                    else if (line == "DATA")
                    {
                        await w.WriteLineAsync("354 go");
                        var sb = new StringBuilder();
                        while (await r.ReadLineAsync() is { } d && d != ".") sb.Append(d).Append("\r\n");
                        Data = sb.ToString().TrimEnd('\r', '\n');
                        await w.WriteLineAsync("250 queued");
                    }
                    else if (line == "QUIT") { await w.WriteLineAsync("221 bye"); return; }
                    else await w.WriteLineAsync("502 no");
                }
            }
            catch { }
        }

        public void Dispose() => _l.Stop();
    }
}
