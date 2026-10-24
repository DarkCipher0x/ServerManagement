using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DcxManager.Mail;
using DcxManager.Services;
using DcxManager.Views;

namespace DcxManager;

/// <summary>
/// 0.3.0 preview.7 (§8): Postfix checks. The SSH script's output is parsed into findings (a risky sample: open mynetworks,
/// no reject_unauth_destination, no incoming TLS, TLS 1.0 allowed, VRFY on, a deferred queue, log errors; and a good
/// one); the test-mail script refuses addresses that could inject; the open-relay probe calls a loopback server that
/// relays "open" and one that refuses "closed", and never sends DATA; the screen builds. No real mail server is touched.
/// </summary>
public static class MailTests
{
    private const string M = "@@DCX@@";

    public static async Task RunAsync(MainWindow main, Action<string> ok, Action<string, Exception?> fail)
    {
        try
        {
            var bad = PostfixCheck.Parse($"{M}active\ninactive\n{M}version\n3.8.6\n{M}postconf\nmynetworks = 0.0.0.0/0\nsmtpd_relay_restrictions = permit_mynetworks permit_sasl_authenticated\nsmtpd_tls_protocols = !SSLv2, !SSLv3\n" +
                $"{M}queue\n{{\"queue_name\": \"deferred\", \"queue_id\": \"A1\"}}\n{{\"queue_name\": \"active\", \"queue_id\": \"B2\"}}\n{M}log\nOct 20 postfix/smtp[1]: error: connect to mx.example.test: timed out\n{M}end\n");
            string L(string area) => string.Join(" ", bad.Findings.Where(f => f.Area == area).Select(f => f.Level));
            if (!bad.Installed || !L("Service").Contains("bad") || !L("TLS in").Contains("bad") || !bad.Findings.Any(f => f.Area == "Relay" && f.Level == "bad" && f.Text.Contains("OPEN RELAY")) ||
                !bad.Findings.Any(f => f.Text.Contains("0.0.0.0/0")) || !L("TLS").Contains("warn") || !L("Hygiene").Contains("warn") || bad.Queued != 2 || bad.Deferred != 1 || !L("Log").Contains("warn"))
                throw new Exception("risky sample: " + string.Join(" | ", bad.Findings.Select(f => $"{f.Level} {f.Area}: {f.Text}")));
            var good = PostfixCheck.Parse($"{M}active\nactive\n{M}version\n3.8.6\n{M}postconf\nsmtpd_tls_security_level = may\nsmtpd_tls_chain_files = /etc/x.pem\nsmtp_tls_security_level = may\nsmtpd_tls_protocols = >=TLSv1.2\nsmtpd_relay_restrictions = permit_mynetworks permit_sasl_authenticated reject_unauth_destination\nmynetworks = 127.0.0.0/8\ndisable_vrfy_command = yes\nsmtpd_helo_required = yes\n{M}queue\n{M}log\n{M}end\n");
            if (good.Findings.Any(f => f.Level is "bad" or "warn")) throw new Exception("good sample flagged: " + string.Join(" | ", good.Findings.Where(f => f.Level is "bad" or "warn").Select(f => f.Text)));
            if (!PostfixCheck.Parse($"{M}active\n\n{M}version\n\n{M}postconf\n\n{M}end\n").Findings.Any(f => f.Text.Contains("isn't be installed") || f.Text.Contains("doesn't seem to be installed"))) throw new Exception("not installed");
            try { PostfixCheck.TestMailScript("a@example.test", "b@example.test\nBcc: x@y", "s"); throw new Exception("an injected recipient was accepted"); } catch (FormatException) { }
            var script = PostfixCheck.TestMailScript("postmaster@example.test", "ops@example.test", "Hi\r\nBcc: x@y");
            if (script.Contains("\r\nBcc") || script.Contains("\nBcc: x@y")) throw new Exception("subject injection");
            ok("Postfix checks: a risky configuration is flagged (stopped, no incoming TLS, open-relay rules and mynetworks 0.0.0.0/0, TLS 1.0/1.1, VRFY, deferred queue, log errors) and a good one isn't; a missing Postfix is said; test-mail addresses and subject can't inject");
        }
        catch (Exception ex) { fail("Postfix checks", ex); }

        var open = new FakeSmtp(relay: true); var shut = new FakeSmtp(relay: false);
        try
        {
            var (o, od) = await MailProbe.RelayAsync("127.0.0.1", open.Port);
            var (c, cd) = await MailProbe.RelayAsync("127.0.0.1", shut.Port);
            if (!o || !od.Contains("OPEN RELAY") || c || !cd.StartsWith("✓")) throw new Exception($"relay results: {od} / {cd}");
            if (open.Log.Any(l => l.StartsWith("DATA")) || !open.Log.Contains("RSET")) throw new Exception("the probe went further than RCPT: " + string.Join(" | ", open.Log));
            var view = new MailCheckView(new HostEntry { Name = "mail-selftest", Address = "mail.example.test" });
            ok("open-relay probe: a loopback server that accepts mail for example.net is reported OPEN, one that refuses is fine, and the probe resets before DATA (nothing is ever sent); the Mail server checks screen builds");
        }
        catch (Exception ex) { fail("open-relay probe", ex); }
        finally { open.Dispose(); shut.Dispose(); }
    }

    private sealed class FakeSmtp : IDisposable
    {
        private readonly TcpListener _l = new(IPAddress.Loopback, 0);
        public int Port { get; }
        public List<string> Log { get; } = new();

        public FakeSmtp(bool relay)
        {
            _l.Start();
            Port = ((IPEndPoint)_l.LocalEndpoint).Port;
            _ = Task.Run(async () =>
            {
                try
                {
                    using var c = await _l.AcceptTcpClientAsync();
                    var s = c.GetStream();
                    var r = new StreamReader(s, Encoding.ASCII); var w = new StreamWriter(s, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
                    await w.WriteLineAsync("220 fake");
                    while (await r.ReadLineAsync() is { } line)
                    {
                        lock (Log) Log.Add(line);
                        if (line.StartsWith("EHLO")) await w.WriteLineAsync("250 fake");
                        else if (line.StartsWith("MAIL")) await w.WriteLineAsync("250 ok");
                        else if (line.StartsWith("RCPT")) await w.WriteLineAsync(relay ? "250 ok" : "554 5.7.1 Relay access denied");
                        else if (line == "RSET") await w.WriteLineAsync("250 reset");
                        else if (line == "QUIT") { await w.WriteLineAsync("221 bye"); return; }
                        else await w.WriteLineAsync("502 no");
                    }
                }
                catch { }
            });
        }

        public void Dispose() => _l.Stop();
    }
}
