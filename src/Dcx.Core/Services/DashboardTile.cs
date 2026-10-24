namespace DcxManager.Services;

/// <summary>
/// A check pinned to the dashboard (0.3.0 §20): an uptime check (ping, a TCP port, an HTTP address, a TLS certificate's
/// days left), a remote performance check (load, memory, disk of a saved host over SSH) or a mail-domain health check.
/// Runs by the dashboard's own rule (Check now / when it opens / on its interval while on screen) — nothing polls
/// silently — and keeps its last result and a short history here, in the encrypted store.
/// </summary>
public sealed class DashboardTile
{
    public static readonly string[] Kinds = ["ping", "tcp", "http", "tls", "perf", "mail", "pingdom"];

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    /// <summary>ping | tcp | http | tls | perf | mail.</summary>
    public string Kind { get; set; } = "tcp";
    /// <summary>A host / IP (ping, tcp, tls), an http(s) address (http) or a domain (mail).</summary>
    public string Target { get; set; } = "";
    public int Port { get; set; }
    /// <summary>perf: the saved host checked over SSH.</summary>
    public string? HostId { get; set; }
    /// <summary>tls: warn when fewer days are left.</summary>
    public int WarnDays { get; set; } = 14;
    /// <summary>perf: warn above this percent (memory / disk) or load per core × 100.</summary>
    public int WarnPercent { get; set; } = 90;
    public bool Enabled { get; set; } = true;
    /// <summary>Also check on the dashboard's interval (0 = only when it opens / Check now).</summary>
    public int IntervalMinutes { get; set; } = 15;

    public DateTime? LastAt { get; set; }
    public bool? LastOk { get; set; }
    /// <summary>The headline ("12 ms", "200 · 340 ms", "41 days", "load 0.3 · mem 62% · disk 71%").</summary>
    public string LastValue { get; set; } = "";
    public string LastDetail { get; set; } = "";
    /// <summary>The last 30 numeric results (ms, days, worst percent) for the sparkline; null = failed.</summary>
    public List<double?> History { get; set; } = new();
    public DateTime? LastChangeAt { get; set; }

    public string KindTitle => Kind switch { "ping" => "Ping", "tcp" => "TCP port", "http" => "HTTP", "tls" => "TLS certificate", "perf" => "Performance", "mail" => "Mail domain", "pingdom" => "Pingdom", _ => Kind };
    public string TargetText => Kind switch { "tcp" or "tls" => $"{Target}:{Port}", "perf" => HostId ?? "", "pingdom" => "check " + Target, _ => Target };
}
