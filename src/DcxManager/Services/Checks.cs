using System.Diagnostics;
using System.Net.NetworkInformation;
using DcxManager.Mail;
using DcxManager.Net;
using DcxManager.Notify;
using DcxManager.Ssh;

namespace DcxManager.Services;

/// <summary>
/// The checks engine behind the dashboard's pinned tiles (0.3.0 §20): runs a <see cref="DashboardTile"/> with the same
/// code the toolbox uses (ping, NetTests TCP / HTTP / TLS, HostPerf over SSH, the mail checks), records the result and a
/// short history in the encrypted store, and reports a change of state to the audit log (and so syslog) and to e-mail
/// notifications. It never schedules itself: the dashboard calls it by its own rule (Check now / on open / interval
/// while on screen), so there is no silent polling. Every target goes through offline mode.
/// </summary>
public static class Checks
{
    private static readonly HashSet<string> Running = new();

    /// <summary>Raised (UI thread) after a tile ran.</summary>
    public static event Action<DashboardTile>? Ran;

    public static DashboardTile? Find(string id) => AppData.Current.DashboardTiles.FirstOrDefault(t => t.Id == id);

    /// <summary>Validates and adds a tile. Throws ArgumentException with the reason.</summary>
    public static DashboardTile Pin(DashboardTile t)
    {
        Validate(t);
        if (t.Name.Trim().Length == 0) t.Name = $"{t.KindTitle} {t.TargetText}";
        AppData.Current.DashboardTiles.Add(t);
        AppData.Save();
        AuditLog.Write("you", "dashboard", "check pinned", $"{t.KindTitle} {t.TargetText}");
        return t;
    }

    public static void Unpin(DashboardTile t)
    {
        AppData.Current.DashboardTiles.Remove(t);
        AppData.Save();
        AuditLog.Write("you", "dashboard", "check removed", $"{t.KindTitle} {t.TargetText}");
    }

    public static void Validate(DashboardTile t)
    {
        if (!DashboardTile.Kinds.Contains(t.Kind)) throw new ArgumentException("check kind");
        if (t.IntervalMinutes is < 0 or > 1440) throw new ArgumentException("The interval is 0–1440 minutes.");
        switch (t.Kind)
        {
            case "ping": t.Target = NetTests.CheckHost(t.Target); break;
            case "tcp" or "tls": t.Target = NetTests.CheckHost(t.Target); if (t.Port is < 1 or > 65535) throw new ArgumentException("A port from 1 to 65535."); break;
            case "http": t.Target = NetTests.CheckUrl(t.Target).ToString(); break;
            case "mail": t.Target = MailNet.CheckDomain(t.Target); break;
            case "pingdom": if (!long.TryParse(t.Target, out var pid) || pid <= 0) throw new ArgumentException("A Pingdom check id (a number)."); break;
            case "perf": if (AppData.Current.FindHost(t.HostId) is not { Protocol: "ssh" }) throw new ArgumentException("A performance check needs a saved SSH host."); break;
        }
    }

    /// <summary>Tiles that should run for a trigger: all for "manual"; every enabled one when the dashboard opens (that
    /// happens at most once a minute); on the interval only enabled tiles with an interval of their own that is due.</summary>
    public static List<DashboardTile> Due(string trigger)
    {
        var now = DateTime.Now;
        return AppData.Current.DashboardTiles.Where(t => trigger switch
        {
            "manual" => true,
            "open" => t.Enabled,
            _ => t.Enabled && t.IntervalMinutes > 0 && (t.LastAt == null || now - t.LastAt.Value >= TimeSpan.FromMinutes(t.IntervalMinutes)),
        }).ToList();
    }

    public static async Task RunAllAsync(string trigger, CancellationToken ct = default)
    {
        var due = Due(trigger);
        if (due.Count == 0) return;
        using var quiet = trigger == "manual" ? null : ExternalCredentials.Background();   // 0.6.17 (ADR-096): scheduled checks never ask for a vault
        using var gate = new SemaphoreSlim(4);
        await Task.WhenAll(due.Select(async t => { await gate.WaitAsync(ct); try { await RunAsync(t, ct); } finally { gate.Release(); } }));
    }

    /// <summary>One run. Never throws: a failure is the tile's result.</summary>
    public static async Task RunAsync(DashboardTile t, CancellationToken ct = default)
    {
        lock (Running) if (!Running.Add(t.Id)) return;
        bool? ok; double? value; string headline, detail;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(45));
            (ok, value, headline, detail) = await CheckAsync(t, limit.Token);
        }
        catch (OfflineBlockedException ex) { (ok, value, headline, detail) = (null, null, "skipped", ex.Message); }
        catch (OperationCanceledException) { (ok, value, headline, detail) = (false, null, "timed out", "no answer within 45 s"); }
        catch (Exception ex) { (ok, value, headline, detail) = (false, null, "✕ " + Short(ex.Message), ex.Message); }
        finally { lock (Running) Running.Remove(t.Id); }

        var was = t.LastOk;
        t.LastAt = DateTime.Now; t.LastValue = headline; t.LastDetail = Redaction.Mask(detail);
        if (ok != null)
        {
            t.LastOk = ok;
            t.History.Add(ok == true ? value : null);
            if (t.History.Count > 30) t.History.RemoveRange(0, t.History.Count - 30);
            if (was != null && was != ok)
            {
                t.LastChangeAt = DateTime.Now;
                AuditLog.Write("app", "dashboard check", ok == true ? $"{t.Name}: back to OK" : $"{t.Name}: FAILING", $"{t.KindTitle} {t.TargetText} · {headline}");
                if (ok == false) Notifier.Report(Notifier.Kind.HostDown, $"Check failing: {t.Name}", $"{t.KindTitle} {t.TargetText}\n{headline}\n{t.LastDetail}");
            }
        }
        try { AppData.Save(); } catch (Exception ex) { Log.Write("dashboard check save", ex); }
        Ui.Thread.Post(() => Ran?.Invoke(t));
    }

    private static async Task<(bool? Ok, double? Value, string Headline, string Detail)> CheckAsync(DashboardTile t, CancellationToken ct)
    {
        switch (t.Kind)
        {
            case "ping":
            {
                Offline.EnforceStatic(t.Target, "a dashboard ping check");
                await Offline.EnforceAsync(t.Target, "a dashboard ping check", ct);
                using var ping = new Ping();
                var r = await ping.SendPingAsync(t.Target, TimeSpan.FromSeconds(4), new byte[32], null, ct);
                return r.Status == IPStatus.Success ? (true, r.RoundtripTime, $"{r.RoundtripTime} ms", $"reply from {r.Address}") : (false, null, r.Status.ToString(), "no reply");
            }
            case "tcp":
            {
                var r = await NetTests.TcpAsync(t.Target, t.Port, TimeSpan.FromSeconds(6), ct);
                return r.State == "open" ? (true, r.Ms, $"open · {r.Ms:0} ms", r.Banner.Length > 0 ? r.Banner.Split('\n')[0] : r.Detail) : (false, null, r.State, r.Detail);
            }
            case "http":
            {
                var sw = Stopwatch.StartNew();
                var r = await NetTests.HttpAsync(t.Target, ct);
                var last = r.Hops.LastOrDefault();
                if (r.Error != null || last == null) return (false, null, r.Error ?? "no answer", r.Error ?? "");
                bool ok = last.Status is >= 200 and < 400;
                var ms = r.Hops.Sum(h => h.Ms);
                return (ok, ms, $"{last.Status} · {ms:0} ms", $"{r.Hops.Count - 1} redirect(s)" + (r.Certificate is { } c ? $" · cert {c.DaysLeft} days" : ""));
            }
            case "tls":
            {
                var c = await NetTests.TlsAsync(t.Target, t.Port, "none", null, ct);
                bool ok = c.Trusted && c.DaysLeft >= t.WarnDays;
                return (ok, c.DaysLeft, $"{c.DaysLeft} days" + (c.Trusted ? "" : " · NOT trusted"), $"{c.Subject} · expires {c.NotAfter:yyyy-MM-dd} · {c.Protocol}");
            }
            case "perf":
            {
                var h = AppData.Current.FindHost(t.HostId) ?? throw new InvalidOperationException("the saved host is gone");
                var r = await SshPool.RunAsync(h, HostPerf.Command, false, ct, TimeSpan.FromSeconds(30));
                if (!r.Ok && r.Output.Length == 0) throw new InvalidOperationException(r.Error.Length > 0 ? r.Error.Split('\n')[0] : "no output");
                var s = HostPerf.Parse(r.Output);
                var worst = s.Worst;
                return (worst is null || worst < t.WarnPercent, worst, s.Headline, $"load {s.Load1:0.00} {s.Load5:0.00} {s.Load15:0.00} · {s.Cores} core(s) · mem {s.MemPercent:0}% · disk {s.DiskPercent:0}%");
            }
            case "mail":
            {
                var spf = await SpfCheck.CheckAsync(t.Target);
                var dmarc = await DmarcDkim.DmarcAsync(t.Target);
                var findings = spf.Findings.Concat(dmarc?.Findings ?? new List<MailFinding> { new("bad", "DMARC", "no DMARC record") }).ToList();
                int bad = findings.Count(f => f.Level == "bad"), warn = findings.Count(f => f.Level == "warn");
                return (bad == 0, bad + warn, bad + warn == 0 ? "all good" : $"{bad} problem(s), {warn} warning(s)", string.Join(" · ", findings.Where(f => f.Level is "bad" or "warn").Take(3).Select(f => f.Text)));
            }
            case "pingdom":
            {
                // 0.5.0 (#17): the state Pingdom measured, read with the read-only API token.
                var c = await Notify.PingdomClient.CheckAsync(long.Parse(t.Target), ct);
                var healthy = Notify.PingdomClient.Healthy(c.Status);
                var head = c.Status switch { "up" => c.LastResponseMs is { } ms ? $"up · {ms} ms" : "up", "down" => "DOWN", "unconfirmed_down" => "down (unconfirmed)", var s => s };
                return (healthy, c.LastResponseMs, head, $"{c.Hostname} · {c.Type}" + (c.LastTest is { } lt ? $" · tested {lt:HH:mm}" : "") + (c.LastError is { } le ? $" · last error {le:yyyy-MM-dd HH:mm}" : ""));
            }
            default: throw new InvalidOperationException("unknown check");
        }
    }

    private static string Short(string s) => s.Split('\n')[0] is { Length: > 60 } l ? l[..60] + "…" : s.Split('\n')[0];
}
