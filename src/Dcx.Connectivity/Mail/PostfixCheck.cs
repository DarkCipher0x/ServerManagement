using System.Text.RegularExpressions;
using DcxManager.Services;
using DcxManager.Ssh;

namespace DcxManager.Mail;

/// <summary>One check result: Good / Warn / Bad / Info, what was looked at, and what to do.</summary>
public sealed record MailFinding(string Level, string Area, string Text, string? Fix = null)
{
    public string Glyph => Level switch { "good" => "✓", "warn" => "⚠", "bad" => "✕", _ => "·" };
}

/// <summary>
/// Postfix checks over SSH (0.3.0 §8): service state, <c>postconf -n</c> against good practice (TLS in and out, no open
/// relay rules, mynetworks, VRFY, HELO, old TLS protocols), the queue (<c>postqueue -j</c>), and warnings / errors from the
/// last 24 hours of the mail log. Everything is read-only (one script, sudo when the host has it); sending a test message
/// is a separate, explicit step (<see cref="TestMailScript"/>). Results are shown, never stored.
/// </summary>
public static class PostfixCheck
{
    private const string Mark = "@@DCX@@";

    /// <summary>The read-only script: sections separated by markers.</summary>
    public static string Script =>
        $"echo {Mark}active; systemctl is-active postfix 2>/dev/null || service postfix status 2>/dev/null | head -3; " +
        $"echo {Mark}version; postconf -h mail_version 2>/dev/null; " +
        $"echo {Mark}postconf; postconf -n 2>/dev/null; " +
        $"echo {Mark}queue; postqueue -j 2>/dev/null | head -500; " +
        $"echo {Mark}log; (journalctl -u postfix -u 'postfix@*' --since '24 hours ago' -p warning --no-pager 2>/dev/null || grep -hiE 'warning|error|fatal|reject' /var/log/mail.log /var/log/maillog 2>/dev/null) | tail -80; " +
        $"echo {Mark}end";

    public sealed record Report(bool Installed, string State, string Version, Dictionary<string, string> Config, int Queued, int Deferred, List<string> LogLines, List<MailFinding> Findings);

    public static async Task<Report> RunAsync(HostEntry host, CancellationToken ct = default)
    {
        var r = await SshPool.RunAsync(host, Script, sudo: true, ct, TimeSpan.FromSeconds(60));
        return Parse(r.Output);
    }

    /// <summary>Reads the script's output (the self-test feeds it a sample).</summary>
    public static Report Parse(string output)
    {
        var sections = new Dictionary<string, string>();
        foreach (var part in output.Split(Mark).Skip(1))
        {
            var nl = part.IndexOf('\n');
            if (nl < 0) continue;
            sections[part[..nl].Trim()] = part[(nl + 1)..].Trim();
        }
        var state = sections.GetValueOrDefault("active", "").Split('\n')[0].Trim();
        var version = sections.GetValueOrDefault("version", "");
        var conf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in sections.GetValueOrDefault("postconf", "").Split('\n'))
        {
            var i = line.IndexOf('=');
            if (i > 0) conf[line[..i].Trim()] = line[(i + 1)..].Trim();
        }
        int queued = 0, deferred = 0;
        foreach (var line in sections.GetValueOrDefault("queue", "").Split('\n').Where(l => l.TrimStart().StartsWith('{')))
        {
            queued++;
            if (line.Contains("\"queue_name\": \"deferred\"") || line.Contains("\"queue_name\":\"deferred\"")) deferred++;
        }
        var log = sections.GetValueOrDefault("log", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        bool installed = version.Length > 0 || conf.Count > 0;
        var f = new List<MailFinding>();
        if (!installed) { f.Add(new("bad", "Postfix", "Postfix doesn't seem to be installed (postconf not found).")); return new(false, state, "", conf, 0, 0, log, f); }

        f.Add(state == "active" ? new("good", "Service", $"postfix is running (version {version})") : new("bad", "Service", $"postfix is {(state.Length > 0 ? state : "not running")}", "systemctl enable --now postfix; check journalctl -u postfix"));
        string C(string k) => conf.GetValueOrDefault(k, "");

        // Incoming TLS.
        var inTls = C("smtpd_tls_security_level");
        if (inTls is "may" or "encrypt") f.Add(new("good", "TLS in", $"smtpd_tls_security_level = {inTls}"));
        else f.Add(new("bad", "TLS in", "incoming TLS isn't offered (smtpd_tls_security_level unset or none)", "postconf -e smtpd_tls_security_level=may with smtpd_tls_cert_file / smtpd_tls_key_file"));
        if (C("smtpd_tls_cert_file").Length == 0 && C("smtpd_tls_chain_files").Length == 0) f.Add(new("warn", "TLS in", "no server certificate configured (smtpd_tls_cert_file / smtpd_tls_chain_files)"));
        // Outgoing TLS.
        var outTls = C("smtp_tls_security_level");
        f.Add(outTls is "may" or "encrypt" or "dane" or "verify" or "secure" ? new("good", "TLS out", $"smtp_tls_security_level = {outTls}") : new("warn", "TLS out", "outgoing mail isn't encrypted opportunistically", "postconf -e smtp_tls_security_level=may"));
        // Old protocols.
        foreach (var k in new[] { "smtpd_tls_protocols", "smtpd_tls_mandatory_protocols", "smtp_tls_protocols" })
        {
            var v = C(k);
            if (v.Length > 0 && !Regex.IsMatch(v, @"!\s*TLSv1\b") && !Regex.IsMatch(v, @">=\s*TLSv1\.[23]")) f.Add(new("warn", "TLS", $"{k} = {v} still allows TLS 1.0/1.1", $"postconf -e '{k}=>=TLSv1.2'"));
        }
        // Relay rules.
        var relay = C("smtpd_relay_restrictions") + " " + C("smtpd_recipient_restrictions");
        if (relay.Contains("reject_unauth_destination") || relay.Contains("defer_unauth_destination")) f.Add(new("good", "Relay", "relay restrictions include reject_unauth_destination"));
        else if (relay.Trim().Length == 0) f.Add(new("info", "Relay", "no relay restrictions set: Postfix's default (permit_mynetworks, permit_sasl_authenticated, defer_unauth_destination) applies"));
        else f.Add(new("bad", "Relay", "relay restrictions don't reject unauthorised destinations — this can be an OPEN RELAY", "add reject_unauth_destination to smtpd_relay_restrictions"));
        var nets = C("mynetworks");
        if (Regex.IsMatch(nets, @"(^|[\s,])0\.0\.0\.0/0|::/0")) f.Add(new("bad", "Relay", $"mynetworks = {nets} trusts the whole internet (open relay)", "limit mynetworks to 127.0.0.0/8 [::1]/128 and your own networks"));
        else if (nets.Length > 0) f.Add(new("info", "Relay", $"mynetworks = {nets}"));
        // Hygiene.
        if (C("disable_vrfy_command") != "yes") f.Add(new("warn", "Hygiene", "VRFY is enabled (addresses can be probed)", "postconf -e disable_vrfy_command=yes"));
        if (C("smtpd_helo_required") != "yes") f.Add(new("info", "Hygiene", "HELO isn't required", "postconf -e smtpd_helo_required=yes"));
        if (C("inet_interfaces") is "all" or "") f.Add(new("info", "Exposure", "listens on all interfaces (inet_interfaces = all)"));
        else f.Add(new("info", "Exposure", $"inet_interfaces = {C("inet_interfaces")}"));
        // Queue and log.
        f.Add(queued == 0 ? new("good", "Queue", "the queue is empty") : new(deferred > 20 ? "bad" : "warn", "Queue", $"{queued} message(s) queued, {deferred} deferred", "postqueue -p to see why; postqueue -f to retry"));
        var errs = log.Count(l => Regex.IsMatch(l, @"\b(error|fatal|panic)\b", RegexOptions.IgnoreCase));
        f.Add(log.Count == 0 ? new("good", "Log", "no warnings or errors in the last 24 hours") : new(errs > 0 ? "warn" : "info", "Log", $"{log.Count} warning/error line(s) in the last 24 hours ({errs} errors)"));
        return new(true, state, version, conf, queued, deferred, log, f);
    }

    /// <summary>Sends one test message through the server's own sendmail (an explicit step: it changes something).</summary>
    public static string TestMailScript(string from, string to, string subject)
    {
        foreach (var v in new[] { from, to }) if (!System.Net.Mail.MailAddress.TryCreate(v, out var a) || a.Address != v) throw new FormatException($"“{v}” isn't an e-mail address");
        var clean = new string(subject.Where(c => !char.IsControl(c)).ToArray());
        var msg = $"From: {from}\nTo: {to}\nSubject: {clean}\n\nTest message sent by DarkCipher ServerManagement on {DateTime.Now:yyyy-MM-dd HH:mm:ss}.\n";
        return $"printf '%s' {RemoteExec.Quote(msg)} | /usr/sbin/sendmail -f {RemoteExec.Quote(from)} -- {RemoteExec.Quote(to)} && echo sent";
    }
}
