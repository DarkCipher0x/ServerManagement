namespace DcxManager.Services;

/// <summary>
/// A scheduled re-scan you set up in LAN discovery → Monitoring (0.3.0 §7): which targets and ports, how often, and what
/// it found last time, so a new open port, a port that closed, or a host that appeared or vanished can be reported (to
/// the audit log, and so to syslog and e-mail notifications). Stored in the encrypted store with everything else.
/// </summary>
public sealed class LanMonitor
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "LAN watch";
    /// <summary>Addresses, ranges or names, as in the Port tests tab (private ranges only; single public addresses allowed).</summary>
    public string Targets { get; set; } = "";
    /// <summary>"common", "22,443", "8000-8100"…</summary>
    public string Ports { get; set; } = "common";
    public int IntervalMinutes { get; set; } = 60;
    public bool Enabled { get; set; } = true;
    /// <summary>Also ping the targets: a host that stops answering both ping and every port counts as down.</summary>
    public bool Ping { get; set; } = true;
    public DateTime? LastRun { get; set; }
    public string LastSummary { get; set; } = "";
    /// <summary>"address:port" open at the last run.</summary>
    public List<string> OpenPorts { get; set; } = new();
    /// <summary>Addresses that answered (ping or any port) at the last run.</summary>
    public List<string> UpHosts { get; set; } = new();
}
