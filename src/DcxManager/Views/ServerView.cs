using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DcxManager.Services;
using DcxManager.Ssh;
using DcxManager.Vultr;

namespace DcxManager.Views;

/// <summary>Everything about one Linux server: live overview, services, processes, logs, packages, Docker, network, firewall, users, cron, shell.</summary>
public sealed class ServerView : UserControl, IDisposable, IHostView
{
    private readonly HostEntry _host;
    public HostEntry Host => _host;
    private readonly StatTile _cpu = new("CPU"), _mem = new("Memory"), _disk = new("Root disk"), _up = new("Uptime"), _load = new("Load"), _swap = new("Swap");
    private readonly TextBlock _facts = new TextBlock() { TextWrapping = TextWrapping.Wrap, LineHeight = 20 }.Fs(FontStep.Body);
    private readonly TextBlock _alerts = new TextBlock() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) }.Fs(FontStep.Body);
    private readonly WrapPanel _disks = new();
    private readonly TextBlock _status = new TextBlock() { VerticalAlignment = VerticalAlignment.Center }.Fs(FontStep.Small);
    private readonly DispatcherTimer _timer;
    private ServerFacts? _last;
    private bool _busy;
    private readonly ReportView _insights;
    private readonly TabItem _insightsTab;
    private readonly TabControl _tabs;

    private string Sudo => _host.Username == "root" ? "" : "sudo ";

    public ServerView(HostEntry host)
    {
        _host = host;
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _facts.Mono();
        _alerts.SetResourceReference(TextBlock.ForegroundProperty, "Warn");

        var tabs = new TabControl { Margin = new Thickness(14, 10, 14, 12) };
        TabItem T(string header, UIElement content) { var t = new TabItem { Header = header, Content = new Border { Padding = new Thickness(0, 10, 0, 0), Child = content } }; tabs.Items.Add(t); return t; }

        T("Overview", BuildOverview());
        T("Services", Services());
        T("Processes", Processes());
        T("Logs", Logs());
        T("Packages", Packages());
        T("Docker", Docker());
        T("Network", Network());
        T("Firewall", Firewall());
        T("Users", Users());
        T("Cron", Cron());
        T("Shell", Shell());
        _insights = new ReportView(host);
        _insightsTab = T(Reports.ReportAnalysis.Current != null ? "✦ AI insights" : "Insights", _insights);   // the reports work offline too
        Content = tabs;
        _tabs = tabs;

        _timer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, async (_, _) => { Task t; using (ExternalCredentials.Background()) t = RefreshAsync(); await t; }, Dispatcher) { IsEnabled = false };
        Loaded += async (_, _) => { if (_last == null) await RefreshAsync(); };
    }

    // ---- overview ------------------------------------------------------------------------------------------------

    private UIElement BuildOverview()
    {
        // Toolbar: everyday actions on the left (they wrap when narrow); power actions grouped at the right, apart from the
        // rest so a reboot is never one slip away from "Files".
        var bar = new WrapPanel();
        var power = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        var toolbar = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(power, Dock.Right);
        toolbar.Children.Add(power); toolbar.Children.Add(bar);
        var env = EnvUi.Strip(() => _host, localWhenNoHost: false);   // ADR-017
        env.Margin = new Thickness(0, 0, 8, 4);
        bar.Children.Add(env);
        Button B(string t, Func<Task> click, string? style = null, string? tip = null)
        {
            bool danger = style == "DangerButton";
            var b = new Button { Content = t, Style = (Style)FindResource(danger ? "SmallDangerButton" : style ?? "SmallButton"), ToolTip = tip, Margin = new Thickness(danger ? 6 : 0, 0, danger ? 0 : 6, 4) };
            b.Click += async (_, _) => await click();
            (danger ? (Panel)power : bar).Children.Add(b);
            return b;
        }
        B("↻ Refresh", RefreshAsync);
        var auto = new CheckBox { Content = "auto 5s", Margin = new Thickness(4, 0, 12, 4), VerticalAlignment = VerticalAlignment.Center };
        auto.Checked += (_, _) => _timer.Start(); auto.Unchecked += (_, _) => _timer.Stop();
        bar.Children.Add(auto);
        B("⌨ Terminal", () => { MainWindow.Instance.OpenTerminal(_host); return Task.CompletedTask; });
        B("⇅ Files", () => { MainWindow.Instance.OpenFiles(_host); return Task.CompletedTask; });
        B("⌨ htop", () => { MainWindow.Instance.OpenTerminal(_host, "htop || top"); return Task.CompletedTask; });
        if (Assist.Current != null) B("✦ Diagnose with AI", DiagnoseAsync, tip: "Send this overview, failed units and recent errors to the local AI for a health review");
        // Out-of-band controls from the host's cloud provider (ADR-020): only what that provider offers.
        if (CloudProviders.ForHost(_host) is { } cloud)
        {
            if (cloud.Provider.Has(CloudCaps.Console)) B($"🖥 {cloud.Provider.Title} console", OpenCloudConsoleAsync, tip: "Out-of-band console — works when SSH or networking is broken");
            if (cloud.Provider.Has(CloudCaps.Reboot)) B($"⚡ {cloud.Provider.Title} power cycle", CloudRebootAsync, "DangerButton", $"Hard restart through the {cloud.Provider.Title} API");
        }
        B("↻ Reboot", () => PowerAsync("reboot"), "DangerButton");
        B("⏻ Shut down", () => PowerAsync("poweroff"), "DangerButton");
        bar.Children.Add(_status);

        // Six equal tiles: three or two per row (or six on a wide screen), never one left alone.
        var tiles = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3 };
        foreach (var t in new[] { _cpu, _mem, _swap, _disk, _load, _up }) { t.Width = double.NaN; t.MinWidth = 140; tiles.Children.Add(t); }
        tiles.SizeChanged += (_, e) => { var c = e.NewSize.Width >= 1100 ? 6 : e.NewSize.Width >= 480 ? 3 : 2; if (tiles.Columns != c) tiles.Columns = c; };

        var info = new Border { Style = (Style)FindResource("Card"), Margin = new Thickness(0, 0, 10, 10) };
        var ip = new StackPanel();
        ip.Children.Add(new TextBlock { Text = _host.Display, Style = (Style)FindResource("H2") });
        ip.Children.Add(_facts);
        ip.Children.Add(_alerts);
        info.Child = ip;

        var disksCard = new Border { Style = (Style)FindResource("Card"), Margin = new Thickness(0, 0, 10, 10) };
        var dp = new StackPanel();
        dp.Children.Add(new TextBlock { Text = "Filesystems", Style = (Style)FindResource("H2") });
        dp.Children.Add(_disks);
        disksCard.Child = dp;

        var root = new StackPanel();
        root.Children.Add(toolbar); root.Children.Add(tiles); root.Children.Add(info); root.Children.Add(disksCard);
        return new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        _status.Text = "probing…";
        try
        {
            var f = await LinuxProbe.ProbeAsync(_host);
            _last = f;
            _cpu.Set($"{f.CpuPercent:0}%", $"{f.Cpus} × {f.CpuModel.Trim()}", f.CpuPercent);
            _mem.Set($"{f.MemPercent:0}%", $"{Fmt.Bytes(f.MemTotal - f.MemAvailable)} of {Fmt.Bytes(f.MemTotal)}", f.MemPercent);
            _swap.Set(f.SwapTotal > 0 ? $"{f.SwapUsed * 100.0 / f.SwapTotal:0}%" : "none", f.SwapTotal > 0 ? $"{Fmt.Bytes(f.SwapUsed)} of {Fmt.Bytes(f.SwapTotal)}" : "", f.SwapTotal > 0 ? f.SwapUsed * 100.0 / f.SwapTotal : null);
            var root = f.Disks.FirstOrDefault(d => d.Mount == "/") ?? f.Disks.FirstOrDefault();
            if (root != null) _disk.Set($"{root.Percent:0}%", $"{Fmt.Bytes(root.Used)} of {Fmt.Bytes(root.Size)}", root.Percent);
            _load.Set(f.Load.Split(' ').FirstOrDefault() ?? "", "1 · 5 · 15 min: " + f.Load, alert: f.Cpus > 0 && double.TryParse(f.Load.Split(' ')[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var l1) && l1 > f.Cpus * 1.5);
            _up.Set(Fmt.Duration(f.Uptime), $"{f.LoggedIn} session(s) logged in");
            _facts.Text = $"{f.Os}\nkernel {f.Kernel} · {f.Arch}{(f.Virtualization.Length > 0 ? " · " + f.Virtualization : "")}\nhost {f.Hostname} · packages: {f.PackageManager} · docker: {(f.HasDocker ? "yes" : "no")}\n{string.Join("\n", f.Addresses)}";
            var alerts = new List<string>();
            if (f.RebootRequired) alerts.Add("⚠ A reboot is required (kernel or library updates).");
            if (f.FailedUnits.Count > 0) alerts.Add("⚠ Failed units: " + string.Join(", ", f.FailedUnits));
            foreach (var d in f.Disks.Where(d => d.Percent > 85)) alerts.Add($"⚠ {d.Mount} is {d.Percent:0}% full.");
            _alerts.Text = string.Join("\n", alerts);
            _disks.Children.Clear();
            foreach (var d in f.Disks)
            {
                var t = new StatTile(d.Mount, 190);
                t.Set($"{d.Percent:0}%", $"{Fmt.Bytes(d.Used)} / {Fmt.Bytes(d.Size)}", d.Percent);
                _disks.Children.Add(t);
            }
            _status.Text = $"updated {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex) { _status.Text = "✕ " + ex.Message.Split('\n')[0]; _timer.Stop(); }
        finally { _busy = false; }
    }

    private async Task PowerAsync(string what)
    {
        if (!Envs.Confirm(_host, what == "reboot" ? "Reboot" : "Shut down", $"{(what == "reboot" ? "Reboot" : "Shut down")} {_host.Display} ({_host.Address}) now?" + (what == "poweroff" && CloudProviders.ForHost(_host) == null ? "\n\nYou'll need physical/console access to turn it back on." : ""), what == "reboot" ? "Reboot" : "Shut down", danger: true, typeToConfirm: what == "poweroff" ? _host.Display : null)) return;
        try
        {
            await SshPool.RunAsync(_host, $"systemctl {what} || {what}", true, timeout: TimeSpan.FromSeconds(15));
        }
        catch { /* the connection drops as the machine goes down */ }
        SshPool.Drop(_host.Id);
        AuditLog.Write("you", _host.Display, what);
        _status.Text = what == "reboot" ? "reboot sent — reconnect in a minute" : "shutdown sent";
    }

    private async Task DiagnoseAsync()
    {
        if (Assist.Current is not { } assist) return;
        _status.Text = "collecting…";
        string errors = "";
        try { errors = (await SshPool.RunAsync(_host, "journalctl -p err -n 60 --no-pager -o short-iso --since '24 hours ago'", true)).Combined; } catch { }
        _last ??= await LinuxProbe.ProbeAsync(_host);
        _status.Text = "";
        assist.AskOperator($"Do a health review of the server {_host.Display}. Point out anything wrong or risky, most important first, with the commands to fix it. Use the tools if you need more detail.\n\n{_last.Summary()}\n\nErrors in the journal (24h):\n```\n{(errors.Length > 4000 ? errors[^4000..] : errors)}\n```");
    }

    private async Task OpenCloudConsoleAsync()
    {
        if (CloudProviders.ForHost(_host) is not { } cloud) return;
        try
        {
            var api = CloudProviders.ApiForHost(_host) ?? cloud.Provider.Require();
            if (await cloud.Provider.ConsoleUrlAsync(api, cloud.InstanceId) is not { } url) { Dialogs.Info("Console", $"{cloud.Provider.Title} didn't return a console address for this instance."); return; }
            AuditLog.Write("you", $"{cloud.Provider.Id}:{api.AccountName}", $"opened web console for {_host.Display}");
            WebWindow.Open($"Console — {_host.Display}", url);
        }
        catch (Exception ex) { Dialogs.Error($"{cloud.Provider.Title} console", ex.Message); }
    }

    private async Task CloudRebootAsync()
    {
        if (CloudProviders.ForHost(_host) is not { } cloud) return;
        if (!Envs.Confirm(_host, $"{cloud.Provider.Title} power cycle", $"Hard-restart {_host.Display} through the {cloud.Provider.Title} API? Unsaved data in memory is lost.", "Restart", danger: true)) return;
        try
        {
            var api = CloudProviders.ApiForHost(_host) ?? cloud.Provider.Require();
            await cloud.Provider.PowerAsync(api, cloud.InstanceId, CloudPower.Reboot);
            SshPool.Drop(_host.Id);
            _status.Text = $"restart requested via {cloud.Provider.Title}";
        }
        catch (Exception ex) { Dialogs.Error(cloud.Provider.Title, ex.Message); }
    }

    // ---- tabs ----------------------------------------------------------------------------------------------------

    private UIElement Services() => new CommandTable(_host,
        "systemctl list-units --type=service --all --no-pager --no-legend --plain", false,
        new[] { "Unit", "Load", "Active", "Sub", "Description" }, new double[] { 260, 70, 80, 90, 420 }, CommandTable.Columns(5),
        new RowAction("Status", r => $"systemctl status {Q(r[0])} --no-pager -l", ShowOutput: true),
        new RowAction("Logs", r => $"journalctl -u {Q(r[0])} -n 300 --no-pager -o short-iso", Sudo: true, ShowOutput: true),
        new RowAction("Follow logs", r => $"journalctl -fu {Q(r[0])}", Sudo: true, Terminal: true),
        new RowAction("▶ Start", r => $"systemctl start {Q(r[0])}", Sudo: true),
        new RowAction("↻ Restart", r => $"systemctl restart {Q(r[0])}", Sudo: true, Confirm: "Restart {0}?"),
        new RowAction("⟳ Reload", r => $"systemctl reload {Q(r[0])}", Sudo: true),
        new RowAction("Enable", r => $"systemctl enable {Q(r[0])}", Sudo: true),
        new RowAction("Disable", r => $"systemctl disable {Q(r[0])}", Sudo: true, Confirm: "Disable {0} at boot?"),
        new RowAction("■ Stop", r => $"systemctl stop {Q(r[0])}", Sudo: true, Confirm: "Stop {0}?", Danger: true));

    private UIElement Processes() => new CommandTable(_host,
        "ps -eo pid,user,pcpu,pmem,rss,etime,args --sort=-pcpu | head -400", false,
        new[] { "PID", "User", "CPU %", "Mem %", "RSS KB", "Elapsed", "Command" }, new double[] { 70, 90, 60, 60, 80, 90, 560 }, CommandTable.Columns(7, skip: 1),
        new RowAction("Details", r => $"ps -o pid,ppid,user,lstart,etime,stat,nlwp,rss,vsz,args -p {Q(r[0])}; echo; ls -l /proc/{Q(r[0])}/cwd 2>/dev/null; cat /proc/{Q(r[0])}/status 2>/dev/null | head -20", Sudo: true, ShowOutput: true),
        new RowAction("Open files", r => $"ls -l /proc/{Q(r[0])}/fd 2>&1 | head -200", Sudo: true, ShowOutput: true),
        new RowAction("Kill (TERM)", r => $"kill {Q(r[0])}", Sudo: true, Confirm: "Send SIGTERM to PID {0} ({6})?", Danger: true),
        new RowAction("Kill -9", r => $"kill -9 {Q(r[0])}", Sudo: true, Confirm: "Force-kill PID {0} ({6})? It gets no chance to clean up.", Danger: true));

    private UIElement Logs()
    {
        var unit = new TextBox { Width = 180, Padding = new Thickness(5, 3, 5, 3), ToolTip = "systemd unit (empty = everything)" };
        var prio = new ComboBox { Width = 100, ItemsSource = new[] { "all", "emerg", "alert", "crit", "err", "warning", "notice", "info", "debug" }, SelectedIndex = 0 };
        var lines = new ComboBox { Width = 80, ItemsSource = new[] { "200", "500", "1000", "5000" }, SelectedIndex = 1 };
        var since = new TextBox { Width = 140, Padding = new Thickness(5, 3, 5, 3), ToolTip = "e.g. 1 hour ago, today, 2026-10-01" };
        var grep = new TextBox { Width = 140, Padding = new Thickness(5, 3, 5, 3), ToolTip = "only lines containing (grep -i)" };
        string Cmd()
        {
            var c = $"journalctl --no-pager -o short-iso -n {lines.SelectedItem}";
            if (unit.Text.Trim().Length > 0) c += " -u " + Q(unit.Text.Trim());
            if (prio.SelectedItem is string p && p != "all") c += " -p " + p;
            if (since.Text.Trim().Length > 0) c += " --since " + Q(since.Text.Trim());
            if (grep.Text.Trim().Length > 0) c += " -g " + Q(grep.Text.Trim()) + " --case-sensitive=false";
            return c;
        }
        var view = new CommandText(_host, Cmd, true);
        view.AddControl(Lbl("unit")); view.AddControl(unit);
        view.AddControl(Lbl("priority")); view.AddControl(prio);
        view.AddControl(Lbl("lines")); view.AddControl(lines);
        view.AddControl(Lbl("since")); view.AddControl(since);
        view.AddControl(Lbl("grep")); view.AddControl(grep);
        view.AddButton("Follow (terminal)", () => { MainWindow.Instance.OpenTerminal(_host, Sudo + Cmd().Replace(" -n ", " -f -n ")); return Task.CompletedTask; });
        view.AddButton("auth.log", () => { MainWindow.Instance.OpenTerminal(_host, Sudo + "less +G /var/log/auth.log 2>/dev/null || " + Sudo + "less +G /var/log/secure"); return Task.CompletedTask; });
        view.AddButton("⌗ Digest", () => { CommandText.ShowDigest(_host, view.Text); return Task.CompletedTask; }, "Group the lines into ranked patterns with counts — offline, no AI");
        if (Reports.ReportAnalysis.Current != null)
            view.AddButton("✦ Analyse with AI", () => AnalyseLogsAsync(view.Text, Cmd()), "Structured analysis of the digested lines (findings, severity, commands) — saved under AI insights");
        if (Assist.Current is { } assist)
            view.AddButton("✦ Explain", () => { assist.AskOperator($"Explain these log lines from {_host.Display} and tell me if anything needs action:\n```\n{Tail(view.Text, 5000)}\n```"); return Task.CompletedTask; });
        return view;
    }

    private async Task AnalyseLogsAsync(string text, string cmd)
    {
        if (text.Trim().Length == 0) { Dialogs.Info("Analyse", "Load some log lines first (↻ Refresh)."); return; }
        if (Reports.ReportAnalysis.Current is not { } analyst) return;
        if (analyst.WhyNot(AssistUse.LogAnalysis) is { } why) { Dialogs.Info("Analyse with AI", why + "\n\nThe offline ⌗ Digest still works."); return; }
        var patterns = Reports.LogDigest.Digest(text, 60);
        var r = new Reports.ServerReport
        {
            Title = $"Log analysis — {_host.Display}", Hosts = new() { _host.Display }, Window = "(current Logs view)",
            Evidence = new() { new Reports.EvidenceSection { Host = _host.Display, Id = "journal", Title = "Logs: " + cmd, Command = cmd, Digest = Reports.LogDigest.Render(patterns, analyst.BudgetChars - 2000), RawLines = text.Count(c => c == '\n') } },
        };
        r.Health = patterns.Any(p => p.Severity == "critical") ? "critical" : patterns.Any(p => p.Severity == "error") ? "degraded" : "ok";
        MainWindow.Instance.Status($"Analysing {r.Evidence[0].RawLines:N0} log lines ({patterns.Count} patterns) with {analyst.ModelName}…");
        try { await analyst.AnalyzeAsync(r, CancellationToken.None); }
        catch (Exception ex) { r.AiNote = "AI analysis failed: " + ex.Message.Split('\n')[0]; r.Summary = "Offline digest only."; }
        try { Reports.ReportEngine.Save(r); } catch (Exception ex) { App.Log("Report save", ex); }
        _insights.ShowReport(r);
        _tabs.SelectedItem = _insightsTab;
        MainWindow.Instance.Status(r.AiUsed ? $"Log analysis done in {analyst.LastLatency.TotalSeconds:0.0}s" : r.AiNote);
    }

    private const string UpgradableCmd =
        "if command -v apt-get >/dev/null 2>&1; then apt list --upgradable 2>/dev/null | tail -n +2 | sed 's#/# #'; " +
        "elif command -v dnf >/dev/null 2>&1; then dnf -q check-update 2>/dev/null | grep -v '^$'; " +
        "elif command -v yum >/dev/null 2>&1; then yum -q check-update 2>/dev/null | grep -v '^$'; " +
        "elif command -v zypper >/dev/null 2>&1; then zypper -q lu | tail -n +3 | awk -F'|' '{print $3, $5, $2}'; " +
        "elif command -v pacman >/dev/null 2>&1; then pacman -Qu; " +
        "elif command -v apk >/dev/null 2>&1; then apk version -l '<' | tail -n +2; fi; true";

    private string PkgCmd(string apt, string dnf, string zypper, string pacman, string apk) =>
        $"if command -v apt-get >/dev/null 2>&1; then {apt}; elif command -v dnf >/dev/null 2>&1; then {dnf}; elif command -v yum >/dev/null 2>&1; then {dnf.Replace("dnf", "yum")}; " +
        $"elif command -v zypper >/dev/null 2>&1; then {zypper}; elif command -v pacman >/dev/null 2>&1; then {pacman}; elif command -v apk >/dev/null 2>&1; then {apk}; fi";

    private UIElement Packages()
    {
        var s = Sudo;
        return new CommandTable(_host, UpgradableCmd, false,
            new[] { "Package", "Source / version", "Details" }, new double[] { 260, 260, 420 }, CommandTable.Columns(3),
            new RowAction("Refresh index", _ => PkgCmd("apt-get update", "dnf -q makecache", "zypper -q refresh", "pacman -Sy", "apk update"), Sudo: true, ShowOutput: true, NoRow: true),
            new RowAction("Upgrade all (terminal)", _ => PkgCmd($"{s}apt-get update && {s}apt-get upgrade", $"{s}dnf upgrade", $"{s}zypper update", $"{s}pacman -Syu", $"{s}apk upgrade"), Terminal: true, NoRow: true),
            new RowAction("Upgrade selected (terminal)", r => PkgCmd($"{s}apt-get install --only-upgrade {Q(r[0])}", $"{s}dnf upgrade {Q(r[0])}", $"{s}zypper update {Q(r[0])}", $"{s}pacman -S {Q(r[0])}", $"{s}apk add -u {Q(r[0])}"), Terminal: true),
            new RowAction("Install…", _ => Dialogs.Prompt("Install package", "Package name(s):") is { Length: > 0 } p ? PkgCmd($"{s}apt-get install {p}", $"{s}dnf install {p}", $"{s}zypper install {p}", $"{s}pacman -S {p}", $"{s}apk add {p}") : "", Terminal: true, NoRow: true),
            new RowAction("Search…", _ => Dialogs.Prompt("Search packages", "Search for:") is { Length: > 0 } p ? PkgCmd($"apt-cache search {Q(p)} | head -100", $"dnf -q search {Q(p)} | head -100", $"zypper -q search {Q(p)} | head -100", $"pacman -Ss {Q(p)} | head -100", $"apk search {Q(p)} | head -100") : "", ShowOutput: true, NoRow: true),
            new RowAction("Auto-updates status", _ => "if command -v unattended-upgrade >/dev/null; then systemctl status unattended-upgrades --no-pager; tail -30 /var/log/unattended-upgrades/unattended-upgrades.log 2>/dev/null; elif command -v dnf >/dev/null; then systemctl status dnf-automatic.timer --no-pager; else echo 'No automatic update service found'; fi", Sudo: true, ShowOutput: true, NoRow: true));
    }

    private UIElement Docker()
    {
        var tabs = new TabControl();
        var containers = new CommandTable(_host,
            "docker ps -a --format '{{.ID}}|{{.Names}}|{{.Image}}|{{.State}}|{{.Status}}|{{.Ports}}'", true,
            new[] { "Id", "Name", "Image", "State", "Status", "Ports" }, new double[] { 110, 180, 230, 70, 170, 320 }, CommandTable.Split('|'),
            new RowAction("Logs", r => $"docker logs --tail 400 -t {Q(r[1])} 2>&1", Sudo: true, ShowOutput: true),
            new RowAction("Follow logs", r => $"docker logs -f --tail 100 {Q(r[1])}", Sudo: true, Terminal: true),
            new RowAction("Shell", r => $"docker exec -it {Q(r[1])} sh -c 'command -v bash >/dev/null && exec bash || exec sh'", Sudo: true, Terminal: true),
            new RowAction("Inspect", r => $"docker inspect {Q(r[1])}", Sudo: true, ShowOutput: true),
            new RowAction("Stats", _ => "docker stats --no-stream", Sudo: true, ShowOutput: true, NoRow: true),
            new RowAction("▶ Start", r => $"docker start {Q(r[1])}", Sudo: true),
            new RowAction("↻ Restart", r => $"docker restart {Q(r[1])}", Sudo: true, Confirm: "Restart container {1}?"),
            new RowAction("■ Stop", r => $"docker stop {Q(r[1])}", Sudo: true, Confirm: "Stop container {1}?"),
            new RowAction("✕ Remove", r => $"docker rm {Q(r[1])}", Sudo: true, Confirm: "Remove container {1}? (must be stopped; volumes are kept)", Danger: true));
        var images = new CommandTable(_host,
            "docker images --format '{{.Repository}}|{{.Tag}}|{{.ID}}|{{.Size}}|{{.CreatedSince}}'", true,
            new[] { "Repository", "Tag", "Id", "Size", "Created" }, new double[] { 280, 120, 130, 90, 140 }, CommandTable.Split('|'),
            new RowAction("Pull latest", r => $"docker pull {Q(r[0] + ":" + r[1])}", Sudo: true, ShowOutput: true),
            new RowAction("Disk usage", _ => "docker system df -v", Sudo: true, ShowOutput: true, NoRow: true),
            new RowAction("✕ Remove", r => $"docker rmi {Q(r[2])}", Sudo: true, Confirm: "Remove image {0}:{1}?", Danger: true),
            new RowAction("Prune unused", _ => "docker image prune -af", Sudo: true, Confirm: "Delete ALL images not used by a container?", Danger: true, ShowOutput: true, NoRow: true));
        var compose = new CommandTable(_host,
            "docker compose ls -a --format json 2>/dev/null | tr -d '[]' | sed 's/},{/}\\n{/g' | sed -E 's/.*\"Name\":\"([^\"]*)\".*\"Status\":\"([^\"]*)\".*\"ConfigFiles\":\"([^\"]*)\".*/\\1|\\2|\\3/'", true,
            new[] { "Project", "Status", "Compose file" }, new double[] { 180, 180, 460 }, CommandTable.Split('|'),
            new RowAction("ps", r => $"docker compose -f {Q(r[2])} ps", Sudo: true, ShowOutput: true),
            new RowAction("Logs", r => $"docker compose -f {Q(r[2])} logs --tail 200", Sudo: true, ShowOutput: true),
            new RowAction("Pull + up -d", r => $"docker compose -f {Q(r[2])} pull && docker compose -f {Q(r[2])} up -d", Sudo: true, Terminal: true),
            new RowAction("↻ Restart", r => $"docker compose -f {Q(r[2])} restart", Sudo: true, Confirm: "Restart project {0}?"),
            new RowAction("■ Down", r => $"docker compose -f {Q(r[2])} down", Sudo: true, Confirm: "Stop and remove the containers of {0}?", Danger: true));
        foreach (var (h, c) in new (string, UIElement)[] { ("Containers", containers), ("Images", images), ("Compose", compose) })
            tabs.Items.Add(new TabItem { Header = h, Content = new Border { Padding = new Thickness(0, 8, 0, 0), Child = c } });
        return tabs;
    }

    private UIElement Network()
    {
        var tabs = new TabControl();
        var ports = new CommandTable(_host, "ss -tulpnH", true,
            new[] { "Proto", "State", "Recv-Q", "Send-Q", "Local", "Peer", "Process" }, new double[] { 55, 70, 60, 60, 200, 160, 420 }, CommandTable.Columns(7),
            new RowAction("Who owns it", r => $"ss -tulpn | grep -F {Q(r[4])}", Sudo: true, ShowOutput: true));
        var conns = new CommandTable(_host, "ss -tnpH state established", true,
            new[] { "Recv-Q", "Send-Q", "Local", "Peer", "Process" }, new double[] { 60, 60, 200, 200, 420 }, CommandTable.Columns(5));
        var ifaces = new CommandText(_host, () => "ip -br -c=never addr; echo; ip route; echo; ip -6 route 2>/dev/null; echo; cat /etc/resolv.conf 2>/dev/null | grep -v '^#'", false);
        ifaces.AddButton("Ping…", async () => { if (Dialogs.Prompt("Ping", "Host to ping from the server:") is { Length: > 0 } t) Dialogs.ShowText("ping " + t, (await SshPool.RunAsync(_host, $"ping -c 4 -W 2 {Q(t)}")).Combined); });
        ifaces.AddButton("Trace…", async () => { if (Dialogs.Prompt("Traceroute", "Host:") is { Length: > 0 } t) Dialogs.ShowText("tracepath " + t, (await SshPool.RunAsync(_host, $"(tracepath -n {Q(t)} || traceroute -n {Q(t)}) 2>&1 | head -40", timeout: TimeSpan.FromMinutes(2))).Combined); });
        ifaces.AddButton("DNS lookup…", async () => { if (Dialogs.Prompt("DNS", "Name:") is { Length: > 0 } t) Dialogs.ShowText("lookup " + t, (await SshPool.RunAsync(_host, $"getent ahosts {Q(t)}; command -v dig >/dev/null && dig +short {Q(t)} ANY")).Combined); });
        foreach (var (h, c) in new (string, UIElement)[] { ("Listening", ports), ("Connections", conns), ("Interfaces & routes", ifaces) })
            tabs.Items.Add(new TabItem { Header = h, Content = new Border { Padding = new Thickness(0, 8, 0, 0), Child = c } });
        return tabs;
    }

    private UIElement Firewall()
    {
        const string show = "if command -v ufw >/dev/null 2>&1; then ufw status verbose; echo; ufw status numbered; " +
                            "elif command -v firewall-cmd >/dev/null 2>&1; then firewall-cmd --state; firewall-cmd --list-all; " +
                            "else nft list ruleset 2>/dev/null || iptables -S; fi; echo; echo '# fail2ban'; fail2ban-client status 2>/dev/null || echo 'not installed'";
        var view = new CommandText(_host, () => show, true);
        async Task Run(string title, string cmd)
        {
            var r = await SshPool.RunAsync(_host, cmd, true);
            AuditLog.Write("you", _host.Display, "firewall: " + cmd, r.Ok ? "ok" : "exit " + r.ExitCode);
            if (!r.Ok) Dialogs.ShowText(title, r.Combined);
            await view.LoadAsync();
        }
        view.AddButton("Allow port…", async () =>
        {
            if (Dialogs.Prompt("Allow port", "Port or port/proto (e.g. 443, 51820/udp):") is not { Length: > 0 } p) return;
            var proto = p.Contains('/') ? "" : "/tcp";
            await Run("Allow", $"if command -v ufw >/dev/null; then ufw allow {Q(p)}; else firewall-cmd --permanent --add-port={Q(p + proto)} && firewall-cmd --reload; fi");
        });
        view.AddButton("Remove port…", async () =>
        {
            if (Dialogs.Prompt("Remove rule", "Port or port/proto to stop allowing:") is not { Length: > 0 } p) return;
            var proto = p.Contains('/') ? "" : "/tcp";
            if (!Envs.Confirm(_host, "Remove rule", $"Stop allowing {p} on {_host.Display}? Don't remove your SSH port.", "Remove", danger: true)) return;
            await Run("Remove", $"if command -v ufw >/dev/null; then ufw delete allow {Q(p)}; else firewall-cmd --permanent --remove-port={Q(p + proto)} && firewall-cmd --reload; fi");
        });
        view.AddButton("Enable ufw", async () =>
        {
            if (!Envs.Confirm(_host, "Enable firewall", $"Enable ufw on {_host.Display}? Port {_host.Port}/tcp (SSH) is allowed first so you don't lock yourself out.", "Enable", danger: true)) return;
            await Run("ufw", $"ufw allow {_host.Port}/tcp && ufw --force enable");
        });
        view.AddButton("fail2ban jails", async () => Dialogs.ShowText("fail2ban", (await SshPool.RunAsync(_host, "for j in $(fail2ban-client status | sed -n 's/.*Jail list:\\s*//p' | tr ',' ' '); do fail2ban-client status $j; echo; done", true)).Combined));
        view.AddButton("Edit in terminal", () => { MainWindow.Instance.OpenTerminal(_host, Sudo + "ufw status numbered 2>/dev/null || " + Sudo + "firewall-cmd --list-all"); return Task.CompletedTask; });
        return view;
    }

    private UIElement Users()
    {
        var tabs = new TabControl();
        var users = new CommandTable(_host,
            "getent passwd | awk -F: '$3==0 || ($3>=1000 && $3<65534) {print $1\":\"$3\":\"$5\":\"$6\":\"$7}' | while IFS=: read u id g h s; do grp=$(id -nG \"$u\" 2>/dev/null | tr ' ' ','); lk=$(passwd -S \"$u\" 2>/dev/null | awk '{print $2}'); echo \"$u|$id|$h|$s|$grp|$lk\"; done", true,
            new[] { "User", "UID", "Home", "Shell", "Groups", "Pw" }, new double[] { 120, 60, 180, 130, 300, 50 }, CommandTable.Split('|'),
            new RowAction("Add user…", _ => Dialogs.Prompt("Add user", "New username:") is { Length: > 0 } u ? $"useradd -m -s /bin/bash {Q(u)} && echo created {Q(u)} && echo 'Set a password from a terminal: passwd {u}'" : "", Sudo: true, ShowOutput: true, NoRow: true),
            new RowAction("Grant sudo", r => $"usermod -aG $(getent group sudo >/dev/null && echo sudo || echo wheel) {Q(r[0])}", Sudo: true, Confirm: "Give {0} full sudo rights?"),
            new RowAction("Set password (terminal)", r => $"passwd {Q(r[0])}", Sudo: true, Terminal: true),
            new RowAction("authorized_keys", r => $"cat {Q(r[2])}/.ssh/authorized_keys 2>&1", Sudo: true, ShowOutput: true),
            new RowAction("Lock", r => $"usermod -L {Q(r[0])}", Sudo: true, Confirm: "Lock the password of {0}? (SSH keys still work)"),
            new RowAction("Unlock", r => $"usermod -U {Q(r[0])}", Sudo: true),
            new RowAction("✕ Delete", r => $"userdel -r {Q(r[0])}", Sudo: true, Confirm: "Delete user {0} AND their home directory {2}?", Danger: true));
        var sessions = new CommandText(_host, () => "echo '# logged in now'; w; echo; echo '# recent logins'; last -n 30 -a 2>/dev/null; echo; echo '# failed logins'; lastb -n 20 -a 2>/dev/null || echo '(needs root)'", true);
        var sudoers = new CommandText(_host, () => "grep -vE '^\\s*(#|$)' /etc/sudoers /etc/sudoers.d/* 2>/dev/null", true);
        foreach (var (h, c) in new (string, UIElement)[] { ("Accounts", users), ("Sessions & logins", sessions), ("sudoers", sudoers) })
            tabs.Items.Add(new TabItem { Header = h, Content = new Border { Padding = new Thickness(0, 8, 0, 0), Child = c } });
        return tabs;
    }

    private UIElement Cron()
    {
        var view = new CommandText(_host, () =>
            "echo \"# crontab of $(id -un)\"; crontab -l 2>/dev/null || echo '(none)'; echo; echo '# /etc/crontab'; grep -vE '^\\s*(#|$)' /etc/crontab 2>/dev/null; " +
            "echo; echo '# /etc/cron.d'; for f in /etc/cron.d/*; do [ -f \"$f\" ] && { echo \"## $f\"; grep -vE '^\\s*(#|$)' \"$f\"; }; done; echo; echo '# systemd timers'; systemctl list-timers --all --no-pager", false);
        view.AddButton("Edit my crontab (terminal)", () => { MainWindow.Instance.OpenTerminal(_host, "crontab -e"); return Task.CompletedTask; });
        view.AddButton("Edit root crontab (terminal)", () => { MainWindow.Instance.OpenTerminal(_host, Sudo + "crontab -e"); return Task.CompletedTask; });
        return view;
    }

    private UIElement Shell()
    {
        var cmd = new TextBox { Padding = new Thickness(6, 5, 6, 5), ToolTip = "Non-interactive command; Enter to run. For interactive programs use the Terminal." }.Mono();
        var sudo = new CheckBox { Content = "sudo", Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        var snippets = new ComboBox { Width = 200, DisplayMemberPath = "Name", ItemsSource = AppData.Current.Snippets, ToolTip = "Saved snippets" };
        var run = new Button { Content = "Run", Style = (Style)FindResource("PrimaryButton") };
        var output = new TextBox { Style = (Style)FindResource("Mono") };
        snippets.SelectionChanged += (_, _) => { if (snippets.SelectedItem is Snippet s) { cmd.Text = s.Command; sudo.IsChecked = s.Sudo; } };
        async Task Go()
        {
            var c = cmd.Text.Trim();
            if (c.Length == 0) return;
            run.IsEnabled = false;
            output.AppendText($"$ {(sudo.IsChecked == true ? "sudo " : "")}{c}\n");
            try
            {
                var r = await SshPool.RunAsync(_host, c, sudo.IsChecked == true, timeout: TimeSpan.FromMinutes(10));
                output.AppendText(r.Combined.TrimEnd() + (r.Ok ? "" : $"\n[exit {r.ExitCode}]") + "\n\n");
                AuditLog.Write("you", _host.Display, (sudo.IsChecked == true ? "sudo " : "") + c, r.Ok ? "ok" : $"exit {r.ExitCode}");
            }
            catch (Exception ex) { output.AppendText("✕ " + ex.Message + "\n\n"); }
            finally { run.IsEnabled = true; output.ScrollToEnd(); }
        }
        run.Click += async (_, _) => await Go();
        cmd.KeyDown += async (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) await Go(); };
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        foreach (var el in new FrameworkElement[] { run, sudo, snippets }) { DockPanel.SetDock(el, Dock.Right); top.Children.Add(el); }
        top.Children.Add(cmd);
        var clear = new Button { Content = "Clear", Style = (Style)FindResource("SmallButton"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
        clear.Click += (_, _) => output.Clear();
        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top); DockPanel.SetDock(clear, Dock.Bottom);
        root.Children.Add(top); root.Children.Add(clear); root.Children.Add(output);
        return root;
    }

    private static string Q(string s) => RemoteExec.Quote(s);
    private static TextBlock Lbl(string t) => new() { Text = t, Style = (Style)Application.Current.Resources["Label"], Margin = new Thickness(6, 0, 4, 0) };
    private static string Tail(string s, int n) => s.Length > n ? s[^n..] : s;

    public void Dispose() { _timer.Stop(); _insights.Dispose(); }
}
