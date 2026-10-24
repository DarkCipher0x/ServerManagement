using System.Net.Mail;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using DcxManager.Services;

namespace DcxManager.Notify;

/// <summary>
/// A small SMTP client for notifications (0.3.0 §4) — kept in-house instead of adding a mail library: implicit TLS
/// (port 465) or STARTTLS (587) that must succeed before anything else is said; there is no plaintext path, and the
/// password is only ever sent inside TLS (AUTH PLAIN, or LOGIN when that's all the server offers). The certificate is
/// checked by Windows (TLS 1.2 or 1.3). Messages are plain UTF-8 text with every header value stripped of CR/LF (no header
/// injection) and the body Base64-encoded.
/// </summary>
public static class SmtpSender
{
    public sealed record Server(string Host, int Port, bool ImplicitTls, string User, string Password, string From);

    /// <summary>Self-test seam: extra certificate check for a loopback test server (the real path uses Windows trust only).</summary>
    internal static RemoteCertificateValidationCallback? TestValidation;

    public static Server FromSettings(AppSettings s) => new(s.SmtpHost.Trim(), s.SmtpPort, s.SmtpImplicitTls, s.SmtpUser.Trim(), s.SmtpPassword, s.SmtpFrom.Trim());

    /// <summary>Recipients from "a@x, b@y; c@z" (invalid ones throw).</summary>
    public static List<string> Recipients(string list)
    {
        var r = list.Split(',', ';').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        foreach (var x in r) if (!MailAddress.TryCreate(x, out var a) || a.Address != x) throw new FormatException($"“{x}” isn't an e-mail address");
        return r;
    }

    /// <summary>Sends one message. Throws with the server's answer when anything fails.</summary>
    public static async Task SendAsync(Server server, IReadOnlyList<string> to, string subject, string body, CancellationToken ct = default) =>
        await SendAsync(server, to, subject, body, [], ct);

    /// <summary>With attachments (name, bytes) — e.g. a shared library's team add-on, which holds no secret.</summary>
    public static async Task SendAsync(Server server, IReadOnlyList<string> to, string subject, string body, IReadOnlyList<(string Name, byte[] Data)> attachments, CancellationToken ct = default)
    {
        if (server.Host.Length == 0) throw new InvalidOperationException("No SMTP server is set.");
        if (!MailAddress.TryCreate(server.From, out var from) || from.Address != server.From) throw new FormatException("The sender isn't an e-mail address.");
        if (to.Count == 0) throw new InvalidOperationException("No recipients are set.");
        await Offline.EnforceAsync(server.Host, "e-mail notification", ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(60));
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(server.Host, server.Port, cts.Token);
        Stream stream = tcp.GetStream();
        if (server.ImplicitTls) stream = await TlsAsync(stream, server.Host, cts.Token);
        var conn = new Conn(stream);
        await conn.ExpectAsync(220, cts.Token);
        var caps = await conn.CommandAsync("EHLO " + Helo(), 250, cts.Token);
        if (!server.ImplicitTls)
        {
            if (!caps.Any(l => l.StartsWith("STARTTLS", StringComparison.OrdinalIgnoreCase)))
                throw new AuthenticationException($"{server.Host} doesn't offer STARTTLS, so nothing is sent (no plaintext fallback).");
            await conn.CommandAsync("STARTTLS", 220, cts.Token);
            conn = new Conn(await TlsAsync(stream, server.Host, cts.Token));
            caps = await conn.CommandAsync("EHLO " + Helo(), 250, cts.Token);
        }
        if (server.User.Length > 0)
        {
            var auth = caps.FirstOrDefault(l => l.StartsWith("AUTH", StringComparison.OrdinalIgnoreCase))?.ToUpperInvariant() ?? "";
            if (auth.Contains("PLAIN"))
                await conn.CommandAsync("AUTH PLAIN " + B64("\0" + server.User + "\0" + server.Password), 235, cts.Token, secret: true);
            else if (auth.Contains("LOGIN"))
            {
                await conn.CommandAsync("AUTH LOGIN", 334, cts.Token);
                await conn.CommandAsync(B64(server.User), 334, cts.Token, secret: true);
                await conn.CommandAsync(B64(server.Password), 235, cts.Token, secret: true);
            }
            else throw new AuthenticationException($"{server.Host} offers no password sign-in (AUTH PLAIN or LOGIN).");
        }
        await conn.CommandAsync($"MAIL FROM:<{from.Address}>", 250, cts.Token);
        foreach (var r in to) await conn.CommandAsync($"RCPT TO:<{r}>", 250, cts.Token);
        await conn.CommandAsync("DATA", 354, cts.Token);
        await conn.WriteRawAsync(Message(from.Address, to, subject, body, attachments) + "\r\n.\r\n", cts.Token);
        await conn.ExpectAsync(250, cts.Token);
        try { await conn.CommandAsync("QUIT", 221, cts.Token); } catch { }
    }

    /// <summary>The message text (headers + Base64 body), dot-stuffing unnecessary since Base64 lines never start with '.'.</summary>
    internal static string Message(string from, IReadOnlyList<string> to, string subject, string body, IReadOnlyList<(string Name, byte[] Data)>? attachments = null)
    {
        static string Clean(string s) => new(s.Where(c => c is not ('\r' or '\n') && !char.IsControl(c)).ToArray());
        var host = from.Split('@').Last();
        var sb = new StringBuilder();
        sb.Append("From: DarkCipher ServerManagement <").Append(Clean(from)).Append(">\r\n");
        sb.Append("To: ").Append(string.Join(", ", to.Select(Clean))).Append("\r\n");
        sb.Append("Subject: =?utf-8?B?").Append(B64(Clean(subject))).Append("?=\r\n");
        sb.Append("Date: ").Append(DateTimeOffset.Now.ToString("ddd, dd MMM yyyy HH:mm:ss zzz", System.Globalization.CultureInfo.InvariantCulture).Remove(29, 1)).Append("\r\n");
        sb.Append("Message-ID: <").Append(Guid.NewGuid().ToString("N")).Append('@').Append(Clean(host)).Append(">\r\n");
        sb.Append("MIME-Version: 1.0\r\nAuto-Submitted: auto-generated\r\n");
        static void Lines(StringBuilder sb, string b64) { for (int i = 0; i < b64.Length; i += 76) sb.Append(b64, i, Math.Min(76, b64.Length - i)).Append("\r\n"); }
        var text = B64(body.Replace("\r\n", "\n").Replace("\n", "\r\n"));
        if (attachments is not { Count: > 0 })
        {
            sb.Append("Content-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: base64\r\n\r\n");
            Lines(sb, text);
            return sb.ToString().TrimEnd('\r', '\n');
        }
        var boundary = "dcx-" + Guid.NewGuid().ToString("N");
        sb.Append($"Content-Type: multipart/mixed; boundary=\"{boundary}\"\r\n\r\n");
        sb.Append($"--{boundary}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: base64\r\n\r\n");
        Lines(sb, text);
        foreach (var (name, data) in attachments)
        {
            var safe = new string(Clean(name).Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_').ToArray());
            sb.Append($"--{boundary}\r\nContent-Type: application/octet-stream; name=\"{safe}\"\r\nContent-Disposition: attachment; filename=\"{safe}\"\r\nContent-Transfer-Encoding: base64\r\n\r\n");
            Lines(sb, Convert.ToBase64String(data));
        }
        sb.Append($"--{boundary}--");
        return sb.ToString();
    }

    private static string Helo() => System.Text.RegularExpressions.Regex.Replace(Environment.MachineName.ToLowerInvariant(), "[^a-z0-9-]", "-") + ".local";
    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

    private static async Task<Stream> TlsAsync(Stream inner, string host, CancellationToken ct)
    {
        var ssl = new SslStream(inner, false, TestValidation);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = host, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = TestValidation != null ? System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck : System.Security.Cryptography.X509Certificates.X509RevocationMode.Online,
        }, ct);
        return ssl;
    }

    /// <summary>Line-based SMTP over a stream: commands, multi-line replies ("250-…" then "250 …").</summary>
    private sealed class Conn(Stream s)
    {
        private readonly byte[] _buf = new byte[4096];
        private readonly StringBuilder _pending = new();

        public async Task WriteRawAsync(string text, CancellationToken ct)
        {
            var b = Encoding.UTF8.GetBytes(text);
            await s.WriteAsync(b, ct);
            await s.FlushAsync(ct);
        }

        public async Task<List<string>> CommandAsync(string line, int expect, CancellationToken ct, bool secret = false)
        {
            await WriteRawAsync(line + "\r\n", ct);
            return await ExpectAsync(expect, ct, secret ? "(sign-in)" : line.Split(' ')[0]);
        }

        public async Task<List<string>> ExpectAsync(int expect, CancellationToken ct, string? what = null)
        {
            var lines = new List<string>();
            while (true)
            {
                var l = await ReadLineAsync(ct);
                if (l.Length < 3 || !int.TryParse(l[..3], out var code)) throw new IOException("Not an SMTP reply: " + Redaction.Mask(l));
                lines.Add(l.Length > 4 ? l[4..] : "");
                if (l.Length == 3 || l[3] == ' ')
                {
                    if (code != expect) throw new IOException($"{what ?? "server"}: {code} {Redaction.Mask(string.Join(" ", lines))}".Trim());
                    return lines;
                }
                if (lines.Count > 100) throw new IOException("SMTP reply too long");
            }
        }

        private async Task<string> ReadLineAsync(CancellationToken ct)
        {
            while (true)
            {
                var cur = _pending.ToString();
                var i = cur.IndexOf("\r\n", StringComparison.Ordinal);
                if (i >= 0) { _pending.Remove(0, i + 2); return cur[..i]; }
                if (_pending.Length > 8192) throw new IOException("SMTP line too long");
                var n = await s.ReadAsync(_buf, ct);
                if (n == 0) throw new IOException("the SMTP server closed the connection");
                _pending.Append(Encoding.UTF8.GetString(_buf, 0, n));
            }
        }
    }
}
