using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using DcxManager.Services;
using DcxManager.Ssh;

namespace DcxManager.Reports;

/// <summary>A source of evidence collected over SSH. Commands are read-only.</summary>
public sealed record ReportSource(string Id, string Title, Func<string, string> Command, bool Sudo, bool Default, bool IsLog);

public sealed class ReportOptions
{
    public string Window { get; set; } = "24 hours ago";
    public HashSet<string> Sources { get; set; } = ReportEngine.Sources.Where(s => s.Default).Select(s => s.Id).ToHashSet();
    public List<string> LogFiles { get; set; } = new();
    public bool UseAi { get; set; } = true;
    public string Focus { get; set; } = "";
    /// <summary>0.4.0: include this PC (Windows) — <see cref="LocalReport"/> — with these of its sources and event logs.</summary>
    public bool IncludeLocalPc { get; set; }
    public HashSet<string> LocalSources { get; set; } = LocalReport.Sources.Where(s => s.Default).Select(s => s.Id).ToHashSet();
    public List<string> LocalEventLogs { get; set; } = LocalReport.DefaultEventLogs.ToList();
}

/// <summary>
/// Server/fleet reports: collect → digest (offline) → rule findings (offline) → optional analysis by the registered
/// <see cref="IReportAnalyst"/> (the AI module's, with a JSON schema) → render Markdown/HTML. Reports are kept in the
/// encrypted store (reports.dcx); exports are explicit, user-chosen files. Not AI code: 0.2.0 phase 4 moved it out of Ai\.
/// </summary>
public static class ReportEngine
{
    public static readonly ReportSource[] Sources =
    {
        new("overview", "System overview", _ => "", false, true, false),
        new("journal", "Journal errors & warnings", w => $"journalctl --no-pager -o short-iso -p warning --since {RemoteExec.Quote(w)} | tail -n 6000", true, true, true),
        new("failed", "Failed units", _ => "systemctl --failed --no-pager --no-legend --plain 2>/dev/null", false, true, false),
        new("auth", "Authentication (SSH/sudo)", w => $"(journalctl --no-pager -o short-iso --since {RemoteExec.Quote(w)} -t sshd -t sshd-session -t sudo -t su 2>/dev/null; journalctl --no-pager -o short-iso --since {RemoteExec.Quote(w)} -u ssh -u sshd 2>/dev/null) | grep -Ei 'fail|invalid|accepted|refused|disconnect|authentication|COMMAND' | tail -n 5000", true, true, true),
        new("kernel", "Kernel messages", w => $"journalctl --no-pager -o short-iso -k -p warning --since {RemoteExec.Quote(w)} | tail -n 3000", true, true, true),
        new("disk", "Disk space & inodes", _ => "df -hPT -x tmpfs -x devtmpfs -x squashfs -x overlay 2>/dev/null; echo; df -iP -x tmpfs -x devtmpfs -x squashfs -x overlay 2>/dev/null | awk 'NR==1 || $5+0 > 70'", false, true, false),
        new("ports", "Listening ports", _ => "ss -tulpnH 2>/dev/null | awk '{print $1, $5, $7}' | sort -u | head -150", true, true, false),
        new("updates", "Pending updates", _ => "if command -v apt-get >/dev/null; then apt list --upgradable 2>/dev/null | tail -n +2 | wc -l; apt list --upgradable 2>/dev/null | grep -i secur | head -40; elif command -v dnf >/dev/null; then dnf -q check-update 2>/dev/null | grep -c '^[a-zA-Z0-9]'; dnf -q updateinfo list security 2>/dev/null | head -40; fi", false, true, false),
        new("docker", "Docker containers", _ => "command -v docker >/dev/null && docker ps -a --format '{{.Names}} | {{.Image}} | {{.State}} | {{.Status}}' 2>/dev/null | head -100", true, false, false),
        new("memory", "Top memory / CPU processes", _ => "ps -eo pid,user,pcpu,pmem,rss,comm --sort=-pmem | head -15; echo; ps -eo pid,user,pcpu,pmem,comm --sort=-pcpu | head -10", false, false, false),
        new("timers", "Timers & cron", _ => "systemctl list-timers --all --no-pager --no-legend 2>/dev/null | head -40; crontab -l 2>/dev/null | grep -vE '^\\s*(#|$)'", false, false, false),
    };

    public static readonly string[] Windows = { "1 hour ago", "6 hours ago", "24 hours ago", "3 days ago", "7 days ago" };

    // ---- store (ReportStore, with the store since 0.2.0 phase 6) ----------------------------------------------------

    public static List<ServerReport> Load() => ReportStore.Load();
    public static void Save(ServerReport r) => ReportStore.Save(r);
    public static void Delete(string id) => ReportStore.Delete(id);

    // ---- collect --------------------------------------------------------------------------------------------------

    public static async Task<(List<EvidenceSection> Evidence, ServerFacts? Facts)> CollectAsync(HostEntry host, ReportOptions o, IProgress<string>? progress, CancellationToken ct)
    {
        var list = new List<EvidenceSection>();
        ServerFacts? facts = null;
        foreach (var src in Sources.Where(s => o.Sources.Contains(s.Id)))
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"{host.Display}: {src.Title.ToLowerInvariant()}…");
            var sec = new EvidenceSection { Host = host.Display, Id = src.Id, Title = src.Title };
            try
            {
                if (src.Id == "overview")
                {
                    facts = await LinuxProbe.ProbeAsync(host, ct);
                    sec.Digest = facts.Summary();
                }
                else
                {
                    sec.Command = src.Command(o.Window);
                    var r = await SshPool.RunAsync(host, sec.Command, src.Sudo, ct, TimeSpan.FromMinutes(2));
                    var text = r.Output;
                    sec.RawLines = text.Count(c => c == '\n');
                    sec.Digest = src.IsLog ? LogDigest.Render(LogDigest.Digest(text, 40), 5000) : Clip(text, 4000);
                    if (!r.Ok && text.Trim().Length == 0) sec.Error = r.Error.Split('\n')[0];
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { sec.Error = ex.Message.Split('\n')[0]; }
            list.Add(sec);
        }
        foreach (var file in o.LogFiles.Where(f => f.Trim().Length > 0))
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"{host.Display}: {file}…");
            var sec = new EvidenceSection { Host = host.Display, Id = "file", Title = "Log file " + file, Command = $"tail -n 5000 {RemoteExec.Quote(file.Trim())}" };
            try
            {
                var r = await SshPool.RunAsync(host, sec.Command, true, ct, TimeSpan.FromMinutes(2));
                sec.RawLines = r.Output.Count(c => c == '\n');
                sec.Digest = LogDigest.Render(LogDigest.Digest(r.Output, 30), 4000);
                if (!r.Ok) sec.Error = r.Error.Split('\n')[0];
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { sec.Error = ex.Message.Split('\n')[0]; }
            list.Add(sec);
        }
        return (list, facts);
    }

    // ---- offline rules --------------------------------------------------------------------------------------------

    public static List<Finding> Rules(HostEntry host, ServerFacts? f, List<EvidenceSection> ev)
    {
        var x = new List<Finding>();
        var sudo = host.Username == "root" ? "" : "sudo ";
        void Add(string sev, string title, string detail, string rec, params string[] cmds) =>
            x.Add(new Finding { Host = host.Display, Severity = sev, Title = title, Detail = detail, Recommendation = rec, Commands = cmds.ToList() });

        if (f != null)
        {
            foreach (var d in f.Disks)
                if (d.Percent >= 95) Add("critical", $"{d.Mount} is {d.Percent:0}% full", d.Text, "Free space now — full filesystems break logging, databases and package updates.", $"{sudo}du -xh --max-depth=2 {RemoteExec.Quote(d.Mount)} 2>/dev/null | sort -rh | head -20", $"{sudo}journalctl --vacuum-size=200M");
                else if (d.Percent >= 85) Add("high", $"{d.Mount} is {d.Percent:0}% full", d.Text, "Plan a clean-up or grow the volume.", $"{sudo}du -xh --max-depth=2 {RemoteExec.Quote(d.Mount)} 2>/dev/null | sort -rh | head -20");
            if (f.MemPercent >= 92) Add("high", $"Memory {f.MemPercent:0}% used", $"{Fmt.Bytes(f.MemTotal - f.MemAvailable)} of {Fmt.Bytes(f.MemTotal)}", "Check the top consumers; consider swap or more RAM.", "ps -eo pid,user,pmem,rss,comm --sort=-pmem | head -15");
            if (f.SwapTotal > 0 && f.SwapUsed * 100.0 / f.SwapTotal > 60) Add("medium", "Heavy swap use", $"{Fmt.Bytes(f.SwapUsed)} of {Fmt.Bytes(f.SwapTotal)} swap", "Sustained swapping hurts latency — find what is resident.", "vmstat 1 5");
            if (f.Cpus > 0 && double.TryParse(f.Load.Split(' ')[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var l1) && l1 > f.Cpus * 1.5)
                Add("medium", $"Load {l1:0.0} on {f.Cpus} CPUs", "Load average " + f.Load, "Check CPU and I/O wait.", "top -bn1 | head -25", "vmstat 1 5");
            if (f.RebootRequired) Add("medium", "Reboot required", "Kernel or core libraries were updated.", "Schedule a reboot window.", "cat /var/run/reboot-required.pkgs 2>/dev/null");
            foreach (var u in f.FailedUnits) Add("high", $"Unit {u} failed", "systemd reports the unit as failed.", "Inspect the status and journal, fix, then restart.", $"systemctl status {RemoteExec.Quote(u)} --no-pager -l", $"{sudo}journalctl -u {RemoteExec.Quote(u)} -n 80 --no-pager");
            if (f.Uptime.TotalDays > 180) Add("low", $"Up {f.Uptime.TotalDays:0} days", "Long uptimes usually mean an unpatched kernel.", "Check for kernel updates and reboot in a window.");
        }

        var auth = ev.FirstOrDefault(e => e.Id == "auth");
        if (auth != null && auth.Digest.Length > 0)
        {
            var fails = Regex.Matches(auth.Digest, @"\[(error|critical)\] ×(\d+)[^\n]*(Failed password|Invalid user|authentication failure|invalid user)", RegexOptions.IgnoreCase)
                .Sum(m => int.Parse(m.Groups[2].Value));
            if (fails >= 200) Add("medium", $"{fails} failed SSH logins", "Brute-force noise on the SSH port.", "Use key-only auth, fail2ban/sshguard, or restrict SSH by source IP.", "grep -E '^(PasswordAuthentication|PermitRootLogin)' /etc/ssh/sshd_config /etc/ssh/sshd_config.d/* 2>/dev/null", "fail2ban-client status sshd 2>/dev/null");
            else if (fails >= 20) Add("low", $"{fails} failed SSH logins", "Some failed authentication attempts.", "Review the sources; consider fail2ban.");
            if (Regex.IsMatch(auth.Digest, @"Accepted password for root", RegexOptions.IgnoreCase)) Add("medium", "Root logged in with a password", "Password logins as root were accepted.", "Prefer keys and PermitRootLogin prohibit-password.");
        }

        var kern = ev.FirstOrDefault(e => e.Id == "kernel");
        if (kern != null && Regex.IsMatch(kern.Digest, @"(?i)out of memory|oom-kill|killed process")) Add("high", "OOM killer fired", "The kernel killed processes for memory.", "Find the culprit and set memory limits.", $"{sudo}journalctl -k --no-pager | grep -iE 'oom|killed process' | tail -20");
        if (kern != null && Regex.IsMatch(kern.Digest, @"(?i)i/o error|ext4-fs error|xfs.*error|blk_update_request")) Add("critical", "Storage I/O errors", "The kernel logged disk or filesystem errors.", "Check SMART / the provider's block storage and back up now.", $"{sudo}journalctl -k --no-pager | grep -iE 'i/o error|ext4|xfs|blk_' | tail -30");

        foreach (var sec in ev.Where(e => e.Id is "journal" or "file"))
        {
            var crit = sec.Digest.Split('\n').Where(l => l.StartsWith("[critical]")).Take(3).ToList();
            foreach (var c in crit) Add("high", "Critical log pattern" + (sec.Id == "file" ? " in " + sec.Title[9..] : ""), c, "Investigate this pattern first.");
        }

        var upd = ev.FirstOrDefault(e => e.Id == "updates");
        if (upd != null && int.TryParse(upd.Digest.Split('\n').FirstOrDefault()?.Trim(), out var n) && n > 0)
        {
            bool security = upd.Digest.Contains("secur", StringComparison.OrdinalIgnoreCase) || upd.Digest.Split('\n').Length > 2;
            Add(security ? "medium" : "low", $"{n} package update(s) pending" + (security ? " (incl. security)" : ""), Clip(upd.Digest, 600), "Patch in a maintenance window.");
        }

        var dock = ev.FirstOrDefault(e => e.Id == "docker");
        if (dock != null)
            foreach (var line in dock.Digest.Split('\n').Where(l => l.Contains("| exited", StringComparison.OrdinalIgnoreCase) || l.Contains("restarting", StringComparison.OrdinalIgnoreCase)).Take(5))
                Add("low", "Container not running: " + line.Split('|')[0].Trim(), line.Trim(), "Check its logs; remove it if it is no longer needed.", $"{sudo}docker logs --tail 80 {RemoteExec.Quote(line.Split('|')[0].Trim())}");

        foreach (var e in ev.Where(e => e.Error.Length > 0)) x.Add(new Finding { Host = host.Display, Severity = "info", Title = $"Couldn't collect: {e.Title}", Detail = e.Error, Source = "rule" });
        return x;
    }

    public static string HealthOf(IEnumerable<Finding> f)
    {
        var list = f.ToList();
        if (list.Any(x => x.Severity == "critical")) return "critical";
        if (list.Any(x => x.Severity is "high" or "medium")) return "degraded";
        return "ok";
    }

    // ---- analysis: the registered IReportAnalyst (the AI module's; see Ai\ReportAnalyst.cs) ----------------------------

    public static int Rank(string sev) => sev switch { "critical" => 5, "high" => 4, "medium" => 3, "low" => 2, _ => 1 };

    // ---- run ------------------------------------------------------------------------------------------------------

    /// <summary>Full pipeline for one or more hosts. AI failures never lose the offline report — they're noted on it.</summary>
    public static async Task<ServerReport> RunAsync(IReadOnlyList<HostEntry> hosts, ReportOptions o, IProgress<string>? progress, CancellationToken ct)
    {
        int targets = hosts.Count + (o.IncludeLocalPc ? 1 : 0);
        var r = new ServerReport
        {
            Title = targets == 1 ? (o.IncludeLocalPc ? $"This PC report — {Environment.MachineName}" : $"Health report — {hosts[0].Display}") : $"Fleet report — {targets} systems",
            Hosts = hosts.Select(h => h.Display).ToList(), Window = o.Window, Notes = o.Focus,
        };
        if (o.IncludeLocalPc)
        {
            // This PC first (0.4.0): Windows' own sources, the same digest / rules / AI pipeline as the servers.
            r.Hosts.Insert(0, LocalReport.Display);
            try
            {
                var (ev, facts, events) = await LocalReport.CollectAsync(o, progress, ct);
                r.Evidence.AddRange(ev);
                r.Findings.AddRange(LocalReport.Rules(facts, events, ev));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { r.Findings.Add(new Finding { Host = LocalReport.Display, Severity = "high", Title = "This PC couldn't be examined", Detail = ex.Message.Split('\n')[0], Recommendation = "See the error log; WMI and wevtutil must be available." }); }
        }
        using var gate = new SemaphoreSlim(3);
        var results = await Task.WhenAll(hosts.Select(async h =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var (ev, facts) = await CollectAsync(h, o, progress, ct);
                return (ev, rules: Rules(h, facts, ev));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return (new List<EvidenceSection>(), rules: new List<Finding> { new() { Host = h.Display, Severity = "high", Title = "Host unreachable", Detail = ex.Message.Split('\n')[0], Recommendation = "Check the network path, SSH service and credentials." } });
            }
            finally { gate.Release(); }
        }));
        foreach (var (ev, rules) in results) { r.Evidence.AddRange(ev); r.Findings.AddRange(rules); }
        r.Findings = r.Findings.OrderByDescending(f => Rank(f.Severity)).ToList();
        r.Health = HealthOf(r.Findings);
        int crit = r.Findings.Count(f => f.Severity == "critical"), high = r.Findings.Count(f => f.Severity == "high");
        r.Summary = r.Findings.Count(f => f.Severity != "info") == 0
            ? $"No problems detected by the offline checks across {targets} system(s)."
            : $"Offline checks found {r.Findings.Count(f => f.Severity != "info")} issue(s){(crit + high > 0 ? $" — {crit} critical, {high} high" : "")}.";

        // No analyst registered (AI off): the offline report is the whole report, with nothing said about AI.
        if (o.UseAi && ReportAnalysis.Current is { } analyst)
        {
            if (analyst.WhyNot() is { } why) r.AiNote = "AI analysis skipped: " + why;
            else
            {
                progress?.Report("AI analysis…");
                try { await analyst.AnalyzeAsync(r, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { r.AiNote = "AI analysis failed (offline report kept): " + ex.Message.Split('\n')[0]; }
            }
        }
        return r;
    }

    // ---- render ---------------------------------------------------------------------------------------------------

    public static string Markdown(ServerReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {r.Title}");
        sb.AppendLine();
        sb.AppendLine($"- **Generated:** {r.Created:yyyy-MM-dd HH:mm} by {AppInfo.FooterLine}");
        sb.AppendLine($"- **Hosts:** {string.Join(", ", r.Hosts)}");
        sb.AppendLine($"- **Window:** since {r.Window}");
        sb.AppendLine($"- **Health:** {r.Health.ToUpperInvariant()}");
        sb.AppendLine($"- **Analysis:** {(r.AiUsed ? "rules + " + r.Engine : r.Engine)}");
        if (r.AiNote.Length > 0) sb.AppendLine($"- **Note:** {r.AiNote}");
        sb.AppendLine();
        sb.AppendLine("## Summary");
        sb.AppendLine(r.Summary);
        sb.AppendLine();
        sb.AppendLine("## Findings");
        if (r.Findings.Count == 0) sb.AppendLine("None.");
        foreach (var f in r.Findings)
        {
            sb.AppendLine($"### [{f.Severity.ToUpperInvariant()}] {f.Title}{(r.Hosts.Count > 1 ? " — " + f.Host : "")}");
            if (f.Detail.Length > 0) sb.AppendLine(f.Detail);
            if (f.Evidence.Length > 0) { sb.AppendLine(); sb.AppendLine("Evidence:"); sb.AppendLine("```"); sb.AppendLine(f.Evidence); sb.AppendLine("```"); }
            if (f.Recommendation.Length > 0) { sb.AppendLine(); sb.AppendLine("**Recommendation:** " + f.Recommendation); }
            if (f.Commands.Count > 0) { sb.AppendLine(); sb.AppendLine("```sh"); foreach (var c in f.Commands) sb.AppendLine(c); sb.AppendLine("```"); }
            sb.AppendLine($"_source: {f.Source}_");
            sb.AppendLine();
        }
        if (r.NextSteps.Count > 0)
        {
            sb.AppendLine("## Next steps");
            for (int i = 0; i < r.NextSteps.Count; i++) sb.AppendLine($"{i + 1}. {r.NextSteps[i]}");
            sb.AppendLine();
        }
        sb.AppendLine("## Evidence digest");
        foreach (var e in r.Evidence)
        {
            sb.AppendLine($"### {e.Host} · {e.Title}{(e.RawLines > 0 ? $" ({e.RawLines} lines)" : "")}");
            if (e.Error.Length > 0) sb.AppendLine("_" + e.Error + "_");
            if (e.Digest.Trim().Length > 0) { sb.AppendLine("```"); sb.AppendLine(e.Digest.TrimEnd()); sb.AppendLine("```"); }
        }
        return sb.ToString();
    }

    /// <summary>The report's accent colour (RRGGBB): the theme's live accent, set by the app at start-up; the saved accent
    /// without a UI (0.2.0 phase 6: reports don't reference the WPF theme).</summary>
    public static Func<string> AccentHex { get; set; } = () => AppSettings.Current.AccentColor;

    public static string Html(ServerReport r)
    {
        static string E(string s) => WebUtility.HtmlEncode(s);
        string Sev(string s) => s switch { "critical" => "#ff5f6d", "high" => "#ff9f43", "medium" => "#e8c547", "low" => "#4aa8ff", _ => "#8aa" };
        var accent = "#" + AccentHex();
        var sb = new StringBuilder();
        sb.Append($$"""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>{{E(r.Title)}}</title>
            <style>
            :root { --bg:#0a1112; --card:#101b1c; --fg:#bcd8d5; --muted:#7fa6a3; --line:#1d3133; --accent:{{accent}}; }
            @media (prefers-color-scheme: light) { :root { --bg:#f6fafa; --card:#fff; --fg:#0e2324; --muted:#4d6b69; --line:#d5e3e2; } }
            body { margin:0; background:var(--bg); color:var(--fg); font:14px/1.55 "Segoe UI", system-ui, sans-serif; }
            main { max-width: 980px; margin: 0 auto; padding: 28px 16px 60px; }
            h1 { font: 700 22px "Cascadia Mono", Consolas, monospace; margin: 0 0 6px; } h2 { font-size: 16px; border-bottom: 1px solid var(--line); padding-bottom: 4px; margin-top: 28px; }
            .meta { color: var(--muted); font-size: 12.5px; } .pill { display:inline-block; padding:1px 8px; border-radius:3px; font: 700 11px Consolas, monospace; color:#000; }
            .f { background: var(--card); border:1px solid var(--line); border-left:3px solid var(--sev); border-radius:3px; padding:10px 14px; margin:10px 0; }
            .f h3 { margin:0 0 4px; font-size: 14.5px; } pre { background: var(--bg); border:1px solid var(--line); padding:8px 10px; overflow-x:auto; font: 12px "Cascadia Mono", Consolas, monospace; white-space: pre-wrap; word-break: break-word; }
            .src { color: var(--muted); font-size: 11.5px; } footer { color: var(--muted); font-size: 11.5px; margin-top: 40px; } a { color: var(--accent); }
            .brand { font: 700 11px "Segoe UI", system-ui, sans-serif; letter-spacing: .14em; color: var(--accent); margin-bottom: 8px; }
            </style></head><body><main>
            """);
        sb.Append($"<div class=\"brand\">DARKCIPHER SYSTEMS</div><h1>{E(r.Title)}</h1><div class=\"meta\">{r.Created:yyyy-MM-dd HH:mm} · since {E(r.Window)} · {E(string.Join(", ", r.Hosts))}</div>");
        sb.Append($"<p><span class=\"pill\" style=\"background:{(r.Health == "critical" ? Sev("critical") : r.Health == "degraded" ? Sev("medium") : "#36d27a")}\">{E(r.Health.ToUpperInvariant())}</span> <span class=\"meta\">{E(r.AiUsed ? "rules + " + r.Engine : r.Engine)}</span></p>");
        if (r.AiNote.Length > 0) sb.Append($"<p class=\"meta\">{E(r.AiNote)}</p>");
        sb.Append($"<h2>Summary</h2><p>{E(r.Summary).Replace("\n", "<br>")}</p><h2>Findings</h2>");
        if (r.Findings.Count == 0) sb.Append("<p>None.</p>");
        foreach (var f in r.Findings)
        {
            sb.Append($"<div class=\"f\" style=\"--sev:{Sev(f.Severity)}\"><h3><span class=\"pill\" style=\"background:{Sev(f.Severity)}\">{E(f.Severity.ToUpperInvariant())}</span> {E(f.Title)}{(r.Hosts.Count > 1 ? " — " + E(f.Host) : "")}</h3>");
            if (f.Detail.Length > 0) sb.Append($"<div>{E(f.Detail).Replace("\n", "<br>")}</div>");
            if (f.Evidence.Length > 0) sb.Append($"<pre>{E(f.Evidence)}</pre>");
            if (f.Recommendation.Length > 0) sb.Append($"<p><b>Recommendation:</b> {E(f.Recommendation)}</p>");
            if (f.Commands.Count > 0) sb.Append($"<pre>{E(string.Join("\n", f.Commands))}</pre>");
            sb.Append($"<div class=\"src\">source: {E(f.Source)}</div></div>");
        }
        if (r.NextSteps.Count > 0) { sb.Append("<h2>Next steps</h2><ol>"); foreach (var s in r.NextSteps) sb.Append($"<li>{E(s)}</li>"); sb.Append("</ol>"); }
        sb.Append("<h2>Evidence digest</h2>");
        foreach (var e in r.Evidence)
            sb.Append($"<h3>{E(e.Host)} · {E(e.Title)}{(e.RawLines > 0 ? $" <span class=\"meta\">({e.RawLines} lines)</span>" : "")}</h3>{(e.Error.Length > 0 ? $"<p class=\"meta\">{E(e.Error)}</p>" : "")}{(e.Digest.Trim().Length > 0 ? $"<pre>{E(e.Digest.TrimEnd())}</pre>" : "")}");
        sb.Append($"<footer>{E(AppInfo.FooterLine)} · Contains operational data about your servers — share with care.</footer></main></body></html>");
        return sb.ToString();
    }

    private static string Clip(string s, int n) => s.Length > n ? s[..n] + "\n…(clipped)" : s;
}
