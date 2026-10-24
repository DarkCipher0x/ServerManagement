using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using DcxManager.Notify;
using DcxManager.Services;

namespace DcxManager.Views;

// SettingsView: syslog over TLS (0.3.0 §3) and e-mail notifications (§4). Every value but the SMTP password is
// admin-protected: "Apply" sends the changed ones through the admin gate (Windows Hello if on, UAC, the admin window).
public sealed partial class SettingsView
{
    private Border SyslogCard()
    {
        var host = Bound(new TextBox { Text = _s.SyslogHost, Width = FieldWidth }, nameof(AppSettings.SyslogHost));
        var port = Bound(new TextBox { Text = _s.SyslogPort.ToString(), Width = 90 }, nameof(AppSettings.SyslogPort));
        var pin = Bound(new TextBox { Text = _s.SyslogPin, Width = FieldWidth }, nameof(AppSettings.SyslogPin));
        var client = Bound(new TextBox { Text = _s.SyslogClientCert, Width = FieldWidth }, nameof(AppSettings.SyslogClientCert));
        var on = Bound(new CheckBox { Content = "Send logs to this syslog server", IsChecked = _s.SyslogEnabled }, nameof(AppSettings.SyslogEnabled));
        var audit = Bound(new CheckBox { Content = "The audit log (who did what, when)", IsChecked = _s.SyslogSendAudit }, nameof(AppSettings.SyslogSendAudit));
        var app = Bound(new CheckBox { Content = "The application (error) log", IsChecked = _s.SyslogSendAppLog }, nameof(AppSettings.SyslogSendAppLog));
        var sessions = Bound(new CheckBox { Content = "Terminal session logs (every logged session's lines, msgid “session”; which sessions are logged: Terminal & connections → Session logs)", IsChecked = _s.SyslogSendSessionLogs }, nameof(AppSettings.SyslogSendSessionLogs));
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        void Show() { var st = SyslogForwarder.Status; status.Text = $"State: {st.State}" + (st.Tls != null ? $" · {st.Tls}" : "") + (st.LastSent is { } t ? $" · last sent {t:HH:mm:ss}" : "") + (st.Queued > 0 ? $" · {st.Queued} queued" : "") + (st.LastError != null ? $"\nLast error: {st.LastError}" : ""); }
        Show();
        // Only while shown: a static event would keep a closed Settings tab alive.
        void Changed() => Dispatcher.BeginInvoke(Show);
        status.Loaded += (_, _) => { SyslogForwarder.StatusChanged -= Changed; SyslogForwarder.StatusChanged += Changed; Show(); };
        status.Unloaded += (_, _) => SyslogForwarder.StatusChanged -= Changed;

        async void Apply()
        {
            if (!int.TryParse(port.Text.Trim(), out var p) || p is < 1 or > 65535) { Dialogs.Error("Syslog", "The port is a number from 1 to 65535 (6514 is syslog over TLS)."); return; }
            var want = new Dictionary<string, JsonNode?>
            {
                ["syslog.enabled"] = on.IsChecked == true, ["syslog.host"] = host.Text.Trim(), ["syslog.port"] = p,
                ["syslog.pin"] = pin.Text.Replace(":", "").Trim().ToUpperInvariant(), ["syslog.clientCert"] = client.Text.Replace(" ", "").Trim().ToUpperInvariant(),
                ["syslog.audit"] = audit.IsChecked == true, ["syslog.appLog"] = app.IsChecked == true, ["syslog.sessionLogs"] = sessions.IsChecked == true,
            };
            if (on.IsChecked == true && host.Text.Trim().Length == 0) { Dialogs.Error("Syslog", "Enter the syslog server first."); return; }
            if (await ApplyProtected("change the syslog settings", want)) { Show(); MainWindow.Instance?.Status("Syslog settings saved."); }
        }
        async void Test()
        {
            status.Text = "Sending a test message…";
            var r = await SyslogForwarder.SendTestAsync();
            Show(); status.Text = r + "\n" + status.Text;
        }
        return Card("Syslog (TLS)",
            "Sends the audit log, the application log and (if you choose) the terminal session logs to your syslog collector as RFC 5424 messages over TLS (RFC 5425, TLS 1.2 or 1.3) — there is no UDP or unencrypted option. " +
            "Secrets are masked first. While the collector can't be reached, messages wait in an encrypted queue (up to 5,000). Changes need admin approval.",
            on,
            Row(Labeled("SERVER (name or address)", host), Labeled("PORT", port)),
            Labeled("PIN THE SERVER'S CERTIFICATE (optional SHA-256 of its certificate or CA, for self-signed collectors)", pin),
            Labeled("CLIENT CERTIFICATE (optional thumbprint from your Personal certificate store, for mutual TLS)", client),
            L("SEND"), audit, app, sessions,
            Row(AdminApproval.Button("Apply", Apply), Btn("Send test message", Test), Btn("Open Privacy & network", () => MainWindow.Instance.Navigate("privacy"))),
            status);
    }

    private Border NotificationsCard()
    {
        var host = Bound(new TextBox { Text = _s.SmtpHost, Width = FieldWidth }, nameof(AppSettings.SmtpHost));
        var port = Bound(new TextBox { Text = _s.SmtpPort.ToString(), Width = 90 }, nameof(AppSettings.SmtpPort));
        var mode = SettingTag.For(new ComboBox { Width = FieldWidth }, nameof(AppSettings.SmtpImplicitTls));
        mode.Items.Add("STARTTLS (port 587) — required, no plaintext fallback");
        mode.Items.Add("TLS from the start (port 465)");
        mode.SelectedIndex = _s.SmtpImplicitTls ? 1 : 0;
        var user = Bound(new TextBox { Text = _s.SmtpUser, Width = FieldWidth }, nameof(AppSettings.SmtpUser));
        var pass = new PasswordBox { Width = FieldWidth };
        var from = Bound(new TextBox { Text = _s.SmtpFrom, Width = FieldWidth }, nameof(AppSettings.SmtpFrom));
        var to = Bound(new TextBox { Text = _s.SmtpTo, Width = FieldWidth }, nameof(AppSettings.SmtpTo));
        var on = Bound(new CheckBox { Content = "Send e-mail notifications", IsChecked = _s.SmtpEnabled }, nameof(AppSettings.SmtpEnabled));
        var perHour = Bound(new TextBox { Text = _s.NotifyMaxPerHour.ToString(), Width = 90 }, nameof(AppSettings.NotifyMaxPerHour));
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        status.Text = _s.SmtpPassword.Length > 0 ? "A password is stored (encrypted). Leave the box empty to keep it." : "";

        async void Apply()
        {
            if (!int.TryParse(port.Text.Trim(), out var p) || p is < 1 or > 65535) { Dialogs.Error("Notifications", "The port is a number from 1 to 65535."); return; }
            try { if (to.Text.Trim().Length > 0) SmtpSender.Recipients(to.Text); } catch (FormatException ex) { Dialogs.Error("Notifications", ex.Message); return; }
            if (!int.TryParse(perHour.Text.Trim(), out var ph) || ph is < 1 or > 100) { Dialogs.Error("Notifications", "At most 1 to 100 e-mails an hour."); return; }
            if (ph != _s.NotifyMaxPerHour) { _s.NotifyMaxPerHour = ph; AppSettings.Save(); }
            var want = new Dictionary<string, JsonNode?>
            {
                ["smtp.enabled"] = on.IsChecked == true, ["smtp.host"] = host.Text.Trim(), ["smtp.port"] = p, ["smtp.implicitTls"] = mode.SelectedIndex == 1,
                ["smtp.user"] = user.Text.Trim(), ["smtp.from"] = from.Text.Trim(), ["smtp.to"] = to.Text.Trim(),
            };
            // The password is not protected (it's a secret: never in the registry); it is stored encrypted right away.
            if (pass.Password.Length > 0) { _s.SmtpPassword = pass.Password; AppSettings.Save(); pass.Clear(); AuditLog.Write("you", "notifications", "SMTP password changed"); }
            if (await ApplyProtected("change the e-mail notification settings", want)) { status.Text = "Saved."; MainWindow.Instance?.Status("Notification settings saved."); }
        }
        async void Test() { status.Text = "Sending…"; status.Text = await Notifier.SendTestAsync(); }
        return Card("E-mail notifications",
            "Sends an e-mail when something needs you: an incident is opened, a host goes down (scheduled checks), a backup or snapshot fails, a security event (wrong passwords, refused sign-ins, protected-setting changes), or syslog forwarding fails. " +
            "Only over TLS (465) or STARTTLS (587); the password is stored encrypted on this PC. At most the number per hour below; the rest are listed in the next e-mail. Server and recipients need admin approval.",
            on,
            Row(Labeled("SMTP SERVER", host), Labeled("PORT", port)),
            Labeled("SECURITY", mode),
            Row(Labeled("USER NAME", user), Labeled("PASSWORD (app password)", pass)),
            Row(Labeled("FROM", from), Labeled("TO (commas between addresses)", to)),
            L("NOTIFY ABOUT"),
            Check("Incidents opened", _s.NotifyIncidents, v => _s.NotifyIncidents = v),
            Check("Hosts going down (scheduled checks)", _s.NotifyHostDown, v => _s.NotifyHostDown = v),
            Check("Failed backups and snapshots", _s.NotifyBackupFailed, v => _s.NotifyBackupFailed = v),
            ProtectedCheck("notify.security", "Security events (sign-in failures, lock and protected-setting changes)"),
            ProtectedCheck("notify.syslog", "Syslog forwarding failing"),
            Labeled("AT MOST THIS MANY E-MAILS AN HOUR", perHour),
            Row(AdminApproval.Button("Apply", Apply), Btn("Send test e-mail", Test)),
            status);
    }

    /// <summary>Sends only the protected values that differ from now through the admin gate (nothing to do = true).</summary>
    private static async Task<bool> ApplyProtected(string reason, Dictionary<string, JsonNode?> want)
    {
        var s = AppSettings.Current;
        var changed = want.Where(kv => ProtectedSettings.Find(kv.Key) is { } it && !JsonNode.DeepEquals(it.Get(s), kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value);
        if (changed.Count == 0) return true;
        foreach (var (k, v) in changed)
            if (ProtectedSettings.Find(k)!.Check(v) is { } bad) { Dialogs.Error("Settings", $"{ProtectedSettings.Find(k)!.Title}: {bad}."); return false; }
        return await ProtectedSettings.ChangeAsync(reason, changed);
    }
}
