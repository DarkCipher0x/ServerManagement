using DcxManager.Services;

namespace DcxManager.Notify;

/// <summary>
/// Which events send an e-mail (0.3.0 §4) and how often. Kinds: incidents (opened), host down (scheduled checks), failed
/// backups and snapshots, security events (wrong passwords and refused sign-ins, AI lock changes, protected-setting
/// changes, the local API locking itself, Windows Hello refusals) and syslog failing. Each kind has its own switch in
/// Settings. At most <see cref="AppSettings.NotifyMaxPerHour"/> e-mails an hour: the rest are counted and listed in the
/// next one. Bodies carry what happened, when and on which PC — masked, never a secret. Sending happens in the
/// background; a failure is kept in <see cref="LastError"/> (never written to the app's log, which could loop).
/// </summary>
public static class Notifier
{
    public enum Kind { Incident, HostDown, BackupFailed, Security, SyslogFailing }

    private static readonly object Gate = new();
    private static readonly Queue<DateTime> Sent = new();
    private static readonly List<string> Held = new();
    private static bool _started;

    public static string? LastError { get; private set; }
    public static DateTime? LastSent { get; private set; }

    /// <summary>Self-test seam: replaces the real send (server, to, subject, body).</summary>
    internal static Func<SmtpSender.Server, IReadOnlyList<string>, string, string, Task>? SendOverride;
    /// <summary>Self-test: the clock.</summary>
    internal static Func<DateTime> Now = () => DateTime.UtcNow;

    public static void Start()
    {
        lock (Gate)
        {
            if (_started) return;
            _started = true;
        }
        AuditLog.Added += OnAudit;
        Snapshots.Failed += (reason, why) => Report(Kind.BackupFailed, "A snapshot of the store failed", $"Snapshot ({reason}) failed: {why}");
        SyslogForwarder.Failing += why => Report(Kind.SyslogFailing, "Syslog forwarding is failing", $"The syslog server {AppSettings.Current.SyslogHost}:{AppSettings.Current.SyslogPort} can't be reached: {why}\nMessages are kept in the encrypted spool and sent when it's back.");
        // 0.4.0 Observability: a log sink that had worked and now fails is told the same way (the same switch).
        LogShipper.Failing += (name, why) => Report(Kind.SyslogFailing, $"The log sink “{name}” is failing", $"The log sink “{name}” can't be reached: {why}\nEvents for it are kept in the encrypted spool and sent when it's back.");
    }

    /// <summary>Audit entries that are notifications: classified by where they come from.</summary>
    internal static (Kind Kind, string Subject)? Classify(AuditEntry e)
    {
        var a = e.Action;
        bool Has(string s) => a.Contains(s, StringComparison.OrdinalIgnoreCase);
        return e.Target switch
        {
            "incidents" when Has("incident opened") => (Kind.Incident, "Incident opened"),
            "app" when Has("failed sign-in") => (Kind.Security, "Failed sign-in attempt"),
            "ai-lock" when Has("wrong password") || Has("refused") || Has("removed") || Has("set") => (Kind.Security, "AI features lock: " + a),
            "protected settings" => (Kind.Security, "Protected settings: " + a),
            "api" when Has("locked") => (Kind.Security, "Local API locked after failed sign-ins"),
            "store" when Has("master password removed") || Has("restored") => (Kind.Security, "Store: " + a),
            "confirm" when Has("not confirmed") => (Kind.Security, "A sensitive action wasn't confirmed"),
            _ => null,
        };
    }

    private static void OnAudit(AuditEntry e)
    {
        if (e.Target == "notifications") return;   // never about itself
        if (Classify(e) is { } c) Report(c.Kind, c.Subject, $"{e.Time:yyyy-MM-dd HH:mm:ss}  {e.Actor} · {e.Target} · {e.Action}" + (e.Result is { Length: > 0 } and not "ok" ? $" · {e.Result}" : ""));
    }

    private static bool Enabled(Kind k, AppSettings s) => s.SmtpEnabled && k switch
    {
        Kind.Incident => s.NotifyIncidents,
        Kind.HostDown => s.NotifyHostDown,
        Kind.BackupFailed => s.NotifyBackupFailed,
        Kind.Security => s.NotifySecurity,
        Kind.SyslogFailing => s.NotifySyslogFailing,
        _ => false,
    };

    /// <summary>Sends (in the background) when that kind is on and the hourly limit allows; otherwise holds it for the next one.</summary>
    public static void Report(Kind kind, string subject, string body)
    {
        var s = AppSettings.Current;
        if (!Enabled(kind, s)) return;
        string? summary = null;
        lock (Gate)
        {
            var now = Now();
            while (Sent.Count > 0 && now - Sent.Peek() > TimeSpan.FromHours(1)) Sent.Dequeue();
            if (Sent.Count >= Math.Max(1, s.NotifyMaxPerHour)) { if (Held.Count < 200) Held.Add($"{now.ToLocalTime():HH:mm} {subject}"); return; }
            Sent.Enqueue(now);
            if (Held.Count > 0) { summary = $"\n\nAlso since the last e-mail ({Held.Count} held back by the hourly limit):\n" + string.Join("\n", Held.Take(50)); Held.Clear(); }
        }
        var text = Redaction.Mask($"{body}{summary}\n\n— DarkCipher ServerManagement on {Environment.MachineName}, {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
        _ = SendAsync($"[{Environment.MachineName}] {subject}", text);
    }

    /// <summary>Settings → Notifications → Send test.</summary>
    public static async Task<string> SendTestAsync()
    {
        try
        {
            var s = AppSettings.Current;
            await Send(SmtpSender.FromSettings(s), SmtpSender.Recipients(s.SmtpTo), $"[{Environment.MachineName}] Test notification", "This is a test e-mail from DarkCipher ServerManagement. Notifications reach you here.");
            AuditLog.Write("you", "notifications", "test e-mail sent", s.SmtpHost);
            return "✓ Sent. Check the inbox of " + s.SmtpTo + ".";
        }
        catch (Exception ex) { LastError = Redaction.Mask(ex.Message); return "✕ " + LastError; }
    }

    private static async Task SendAsync(string subject, string body)
    {
        try
        {
            var s = AppSettings.Current;
            await Send(SmtpSender.FromSettings(s), SmtpSender.Recipients(s.SmtpTo), subject, body);
            LastSent = DateTime.Now; LastError = null;
        }
        catch (Exception ex) { LastError = Redaction.Mask(ex.Message); }
    }

    private static Task Send(SmtpSender.Server server, IReadOnlyList<string> to, string subject, string body) =>
        SendOverride != null ? SendOverride(server, to, subject, body) : SmtpSender.SendAsync(server, to, subject, body);

    /// <summary>Self-test: forget the hourly window and anything held.</summary>
    internal static void ResetForTest() { lock (Gate) { Sent.Clear(); Held.Clear(); } LastError = null; LastSent = null; }
}
