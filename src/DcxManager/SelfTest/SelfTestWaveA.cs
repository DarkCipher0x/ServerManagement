using System.Diagnostics;
using System.Windows.Controls;
using DcxManager.Rdp;
using DcxManager.Services;
using DcxManager.Sessions;
using DcxManager.Views;

namespace DcxManager;

/// <summary>
/// 0.6.29 Wave A (ADR-104, ADR-105): folder defaults, keyword highlighting and Ctrl+click words, alerts from tabs you
/// aren't looking at, the snippet button bar and its variables, the command palette, always-on tmux, RDP through SSH, and
/// a 10,000-host scale budget. Demo data only (TEST-NET addresses), in the self-test's own store; nothing is shown.
/// </summary>
public static class WaveATests
{
    public static async Task RunAsync(Action<string> ok, Action<string, Exception?> fail)
    {
        await Run("folder defaults (ADR-105): nearest folder wins, hosts follow only what they adopted, a changed value becomes the host's own, SSH-only fields skip RDP, a key never goes to RDP, renames and deletes carry the defaults", Folders, ok, fail);
        await Run("highlighting: packs and rules checked (empty, inline options and backtracking refused, fast patterns kept), page message from the settings, Ctrl+click words classified (URL, IP:port, path)", Highlight, ok, fail);
        await Run("snippet variables: host values and answers quoted for POSIX and PowerShell, line breaks dropped, {{ }} literal, cancel stops, scopes (host, folder with subfolders, tag, env, shell, local) and bad scopes refused", Snippets, ok, fail);
        await Run("command palette: every word must match, title starts first, recent items lifted (at most 50 kept), pages, settings cards and hosts all found", Palette, ok, fail);
        await Run("always-on tmux and RDP through SSH: Settings / host on / off decide, Windows and RDP hosts never; the tunnel's SSH host, target and port", TmuxAndRdp, ok, fail);
        await Run("terminal (local cmd): highlighting reaches the page and marks an error line; the bell and a long command alert a tab nobody is looking at (once per 10 s, never when muted); the button bar shows scoped snippets and sends them quoted", Terminal, ok, fail);
        await Run("scale budget: 10,000 demo hosts in 200 folders saved encrypted, the Hosts screen and the host pane rebuilt (closed folders make their nodes when opened) and filtered, hosts found by id, the palette built and searched, folder defaults applied, all within budget", Scale, n => ok(n + " (" + ScaleTimes + ")"), fail);
        await Run("performance pass: an idle terminal's flush timer stops (no ticks) and output re-arms it; last-connected saves wait and coalesce into one; the CSI clamp leaves ordinary output untouched; a hidden terminal asks for low memory", Performance, ok, fail);
    }

    private static async Task Performance()
    {
        var main = MainWindow.Instance;
        // Deferred saves: three requests, nothing at once, one save (one Changed) a moment later.
        int changed = 0;
        Action count = () => changed++;
        AppData.Changed += count;
        try
        {
            AppData.SaveSoon(); AppData.SaveSoon(); AppData.SaveSoon();
            Expect(changed == 0, "a deferred save waits");
            for (int i = 0; i < 40 && changed == 0; i++) await Task.Delay(100);
            await Task.Delay(300);
            Expect(changed == 1, $"three deferred saves made {changed} save(s), not one");
        }
        finally { AppData.Changed -= count; }

        var plain = "\x1b[31mred\x1b[0m and \x1b[1;32mgreen\x1b[0m";
        Expect(ReferenceEquals(TerminalView.ClampCsi(plain), plain), "ordinary output is passed through as it is");
        Expect(TerminalView.ClampCsi("a\x1b[99999Lb") == "a\x1b[9999Lb", "a huge CSI number is still clamped");

        var tv = new TerminalView(new HostEntry { Name = "perf-idle-demo", Address = "192.0.2.50", Username = "ops" }, connectOnOpen: false);
        var tab = main.AddTab("perf idle", tv);
        try
        {
            for (int i = 0; i < 50 && tv.MemoryTargetForTest.Length == 0; i++) await Task.Delay(100);
            Expect(tv.MemoryTargetForTest == (tv.IsVisible ? "Normal" : "Low"), $"the memory target follows whether the tab shows ({tv.MemoryTargetForTest}, visible {tv.IsVisible})");
            // Another tab in front hides it (the self-test window is off-screen but showing, so tabs do become visible).
            var front = main.AddTab("perf front", new TerminalView(new HostEntry { Name = "perf-front-demo", Address = "192.0.2.51", Username = "ops" }, connectOnOpen: false));
            try
            {
                for (int i = 0; i < 30 && tv.IsVisible; i++) await Task.Delay(100);
                if (!tv.IsVisible) Expect(tv.MemoryTargetForTest == "Low", $"a terminal behind another tab asks for low memory ({tv.MemoryTargetForTest})");
            }
            finally { main.CloseTab(front); }
            tv.TestFeed(System.Text.Encoding.UTF8.GetBytes("ops@demo:~$ echo hi\r\nhi\r\n"));
            await Task.Delay(400);
            Expect(tv.PendingChars == 0, "the output was flushed");
            int t0 = tv.FlushTicks;
            await Task.Delay(1500);
            Expect(tv.FlushTicks == t0, $"an idle terminal's timer still ticked ({tv.FlushTicks - t0} times in 1.5 s)");
            tv.TestFeed(System.Text.Encoding.UTF8.GetBytes(new string('x', 20_000)));   // above the quick-flush size: the timer
            for (int i = 0; i < 20 && tv.PendingChars > 0; i++) await Task.Delay(50);
            Expect(tv.PendingChars == 0 && tv.FlushTicks > t0, "new output re-armed the timer and drained");
        }
        finally { main.CloseTab(tab); }
    }

    private static string ScaleTimes = "";

    private static async Task Run(string name, Func<Task> test, Action<string> ok, Action<string, Exception?> fail)
    {
        try { await test(); ok(name); }
        catch (Exception ex) { fail(name, ex); }
    }

    private static void Expect(bool ok, string what) { if (!ok) throw new Exception(what); }

    private static Task Folders()
    {
        var saved = FolderDefaults.All.ToList();
        var key = new Credential { Name = "wave-a key", Kind = CredentialKind.PrivateKey };
        var pass = new Credential { Name = "wave-a password", Kind = CredentialKind.Password };
        AppData.Current.Credentials.Add(key); AppData.Current.Credentials.Add(pass);
        try
        {
            FolderDefaults.All.Clear();
            var web = new HostEntry { Name = "web-01", Address = "192.0.2.41", Group = "WaveA/Contoso/Web", Username = "root" };
            var rdp = new HostEntry { Name = "ws-01", Address = "192.0.2.42", Group = "WaveA/Contoso", Protocol = "rdp", Username = "admin" };
            var mine = new HostEntry { Name = "db-01", Address = "192.0.2.43", Group = "WaveA/Contoso/Web", Username = "dba" };
            var hosts = new List<HostEntry> { web, rdp, mine };
            FolderDefaults.All.Add(new FolderSettings { Path = "WaveA/Contoso", Username = "ops", CredentialId = key.Id, Tmux = "on", Environment = "PRODUCTION" });
            FolderDefaults.All.Add(new FolderSettings { Path = "WaveA/Contoso/Web", Username = "deploy" });
            Expect(FolderDefaults.Effective("WaveA/Contoso/Web", "user") is { Value: "deploy", Folder: "WaveA/Contoso/Web" }, "the nearest folder wins");
            Expect(FolderDefaults.Effective("WaveA/Contoso/Web", "tmux") is { Value: "on", Folder: "WaveA/Contoso" }, "a parent fills what the child doesn't set");
            Expect(FolderDefaults.Apply(hosts) == 0 && web.Username == "root", "existing hosts don't follow until they adopt");
            FolderDefaults.Adopt(new[] { web, rdp }, FolderDefaults.Fields);
            Expect(web.Username == "deploy" && web.CredentialId == key.Id && web.Auth == AuthMethod.PrivateKey && web.Tmux == "on" && web.Environment == "PRODUCTION", "adopted: every field the folder sets");
            Expect(rdp.Username == "ops" && rdp.CredentialId == null && rdp.Tmux == "" && !rdp.Inherits.Contains("tmux") && !rdp.Inherits.Contains("credential"), "RDP: no tmux, never a key");
            Expect(mine.Username == "dba" && mine.Inherits.Count == 0, "a host that didn't adopt keeps its own");
            FolderDefaults.All.Single(f => f.Path == "WaveA/Contoso").CredentialId = pass.Id;
            FolderDefaults.Apply(hosts);
            Expect(web.CredentialId == pass.Id && web.Auth == AuthMethod.Password, "a folder change reaches its followers");
            Expect(rdp.CredentialId == null, "RDP didn't follow the credential (a key didn't fit), so it isn't changed");
            FolderDefaults.Adopt(new[] { rdp }, new[] { "credential" });
            Expect(rdp.CredentialId == pass.Id && rdp.Inherits.Contains("credential"), "a password fits RDP once adopted");
            web.Username = "someone";
            FolderDefaults.Decide(web);
            Expect(!web.Inherits.Contains("user") && web.Inherits.Contains("credential"), "a value changed in the editor becomes the host's own");
            FolderDefaults.Apply(hosts);
            Expect(web.Username == "someone", "and stays");
            FolderDefaults.All.Single(f => f.Path == "WaveA/Contoso").Tmux = null;
            FolderDefaults.Apply(hosts);
            Expect(web.Tmux == "on" && !web.Inherits.Contains("tmux"), "a field no folder sets any more stays as the host's own value");
            var jump = new HostEntry { Name = "bastion", Address = "192.0.2.44", Group = "WaveA/Contoso" };
            Expect(!FolderDefaults.Fits(jump, "jump", jump.Id) && FolderDefaults.Fits(web, "jump", jump.Id) && !FolderDefaults.Fits(rdp, "jump", jump.Id), "a jump host: SSH only, never itself");
            FolderDefaults.Renamed("WaveA/Contoso", "WaveA/Fabrikam");
            Expect(FolderDefaults.For("WaveA/Fabrikam/Web")?.Username == "deploy" && FolderDefaults.For("WaveA/Contoso") == null, "a rename carries the defaults and the sub-folders'");
            Expect(FolderDefaults.Describe("WaveA/Fabrikam/Web", (f, v) => v).Contains("(from WaveA/Fabrikam)"), "the editor says where a value comes from");
            FolderDefaults.Deleted("WaveA/Fabrikam");
            Expect(FolderDefaults.All.Count == 0, "a delete removes them");
        }
        finally
        {
            FolderDefaults.All.Clear(); FolderDefaults.All.AddRange(saved);
            AppData.Current.Credentials.Remove(key); AppData.Current.Credentials.Remove(pass);
        }
        return Task.CompletedTask;
    }

    private static Task Highlight()
    {
        foreach (var p in Highlighting.Packs) foreach (var r in p.Rules) Expect(Highlighting.Check(r.Pattern) == null, "pack pattern refused: " + r.Id);
        Expect(Highlighting.Check("") != null && Highlighting.Check("a*") != null, "empty / empty-matching refused");
        Expect(Highlighting.Check("(?i)error") != null, "inline options refused");
        Expect(Highlighting.Check("(a+)+$") != null, "catastrophic backtracking refused");
        Expect(Highlighting.Check("([") != null, "invalid refused");
        Expect(Highlighting.Check(@"\bdisk (?:full|quota)\b") == null, "a normal pattern is fine");
        var ip = Highlighting.Packs.Single(p => p.Id == "network").Rules.Single(r => r.Id == "ipv4");
        var rx = new System.Text.RegularExpressions.Regex(ip.Pattern);
        Expect(rx.IsMatch("listening on 198.51.100.7:8443") && !rx.IsMatch("version 1.2.3"), "the IPv4 pack");
        var http = new System.Text.RegularExpressions.Regex(Highlighting.Packs.Single(p => p.Id == "http").Rules.Single(r => r.Id == "http5").Pattern);
        Expect(http.IsMatch("HTTP/1.1 503 Service Unavailable") && http.IsMatch("\"GET / HTTP/2\" 502 0") && !http.IsMatch("took 503 ms"), "HTTP 5xx only after HTTP/x or a log's quote");

        var s = new AppSettings { TerminalHighlight = true, TerminalHighlightPacks = new() { "errors" }, TerminalLinks = true };
        s.TerminalHighlightRules.Add(new HighlightRule { Name = "mine", Pattern = "wave-a", Colour = "magenta", Alert = true });
        s.TerminalHighlightRules.Add(new HighlightRule { Name = "bad", Pattern = "(a+)+$" });
        s.TerminalHighlightRules.Add(new HighlightRule { Name = "off", Pattern = "off", Enabled = false });
        var active = Highlighting.Active(s);
        Expect(active.Count == 2 && active[1].Name == "mine", "the chosen packs, then your enabled valid rules");
        var json = System.Text.Json.JsonSerializer.Serialize(Highlighting.PageMessage(s));
        Expect(json.Contains("\"t\":\"hl\"") && json.Contains("\"c\":\"magenta\"") && json.Contains("\"a\":true"), "the page message");
        var sig = Highlighting.Signature(s);
        s.TerminalHighlightRules[0].Colour = "red";
        Expect(Highlighting.Signature(s) != sig, "the signature changes with a rule");
        Expect(Highlighting.PageMessage(new AppSettings { TerminalHighlight = false }) is { } off && System.Text.Json.JsonSerializer.Serialize(off).Contains("\"rules\":[]"), "off sends no rules");

        Expect(Highlighting.Classify("https://example.com/a?b=1).") is { Kind: "url", Value: "https://example.com/a?b=1" }, "a URL (trailing punctuation dropped)");
        Expect(Highlighting.Classify("192.0.2.10:2222") is { Kind: "ip", Value: "192.0.2.10", Port: 2222 }, "an IP with a port");
        Expect(Highlighting.Classify("10.0.0.0/8") is { Kind: "ip", Value: "10.0.0.0" }, "a CIDR's address");
        Expect(Highlighting.Classify("/var/log/nginx/error.log") is { Kind: "path" }, "an absolute path");
        Expect(Highlighting.Classify("javascript:alert(1)") == null && Highlighting.Classify("999.1.1.1") == null && Highlighting.Classify("file:///etc/passwd") == null, "nothing else");
        return Task.CompletedTask;
    }

    private static Task Snippets()
    {
        var h = new HostEntry { Name = "web 01", Address = "192.0.2.51", Username = "ops", Port = 2222, Group = "WaveA/Contoso/Web", Tags = "nginx, edge", Environment = "PRODUCTION" };
        var posix = new SnippetVariables.Context(h, "posix", "PRODUCTION", false);
        var qs = SnippetVariables.Questions("tail -n {ask:Lines=50} {pick:Log|ls /var/log} {ask:lines}");
        Expect(qs.Count == 2 && qs[0] is { Label: "Lines", Default: "50", PickCommand: null } && qs[1] is { Label: "Log", PickCommand: "ls /var/log" }, "questions, once per label");
        var x = SnippetVariables.Expand("ssh {user}@{address} -p {port} # {host} {{lit}} {ask:Why}", posix, _ => "it's; rm -rf /\nwhoami");
        Expect(x == "ssh ops@192.0.2.51 -p 2222 # 'web 01' {lit} 'it'\\''s; rm -rf /whoami'", "POSIX: " + x);
        var ps = SnippetVariables.Expand("Get-Content {ask:Path}", posix with { Shell = "powershell" }, _ => @"C:\it's here");
        Expect(ps == @"Get-Content 'C:\it''s here'", "PowerShell: " + ps);
        Expect(SnippetVariables.Expand("echo {ask:A}", posix, _ => null) == null, "a cancelled question stops it");
        Expect(SnippetVariables.Quote("a\u001b[31mb\r\n", "") == "a[31mb", "control characters dropped even unquoted");
        Expect(!SnippetVariables.HasVariables("echo {{x}}") && SnippetVariables.HasVariables("echo {host}"), "literal braces aren't variables");

        Snippet S(string scope) => new() { Name = "s", Command = "uptime", Bar = true, Scope = scope };
        Expect(SnippetVariables.InScope(S(""), posix), "empty = everywhere");
        Expect(SnippetVariables.InScope(S("host:web 01"), posix) && SnippetVariables.InScope(S("host:192.0.2.51"), posix), "host by name or address");
        Expect(SnippetVariables.InScope(S("folder:WaveA/Contoso"), posix) && !SnippetVariables.InScope(S("folder:WaveA/Cont"), posix), "folder with subfolders (whole names)");
        Expect(SnippetVariables.InScope(S("tag:edge"), posix) && !SnippetVariables.InScope(S("tag:ed"), posix), "tags");
        Expect(SnippetVariables.InScope(S("env:production"), posix) && !SnippetVariables.InScope(S("env:TEST"), posix), "environment");
        Expect(SnippetVariables.InScope(S("tag:none, shell:posix"), posix) && !SnippetVariables.InScope(S("local"), posix), "any one token; local only for local shells");
        Expect(SnippetVariables.InScope(S("local"), new(null, "", "LOCAL", true)), "local");
        Expect(SnippetVariables.CheckScope("host:a, local, env:TEST") == null && SnippetVariables.CheckScope("hots:a") != null && SnippetVariables.CheckScope("host:") != null, "scope check");
        return Task.CompletedTask;
    }

    private static Task Palette()
    {
        var items = new List<PaletteSearch.Item>();
        void I(string key, string kind, string title, string detail = "") => items.Add(new PaletteSearch.Item(key, kind, title, detail, () => { }));
        I("host:1", "Host", "web-01", "SSH · ops@192.0.2.61 · Contoso/Web");
        I("host:2", "Host", "db-web-02", "SSH · ops@192.0.2.62 · Contoso/Db");
        I("host:3", "Host", "mail", "SSH · ops@192.0.2.63 · Fabrikam");
        I("page:hosts", "Page", "Hosts");
        I("setting:Alerts", "Setting", "Alerts", "Settings → Terminals & connections");
        var r = PaletteSearch.Search(items, "web");
        Expect(r.Count == 2 && r[0].Key == "host:1", "title start first: " + string.Join(",", r.Select(i => i.Title)));
        Expect(PaletteSearch.Search(items, "web contoso db").Single().Key == "host:2", "every word must match (title or detail)");
        Expect(PaletteSearch.Search(items, "wb01").Single().Key == "host:1", "letters in order");
        Expect(PaletteSearch.Search(items, "192.0.2.63").Single().Key == "host:3", "an address in the detail");
        Expect(PaletteSearch.Search(items, "web", new[] { "host:2" })[0].Key == "host:2", "recent lifted");
        Expect(PaletteSearch.Search(items, "", new[] { "host:3" }).First().Key == "host:3", "nothing typed: recent first");
        var recent = new List<string>();
        for (int i = 0; i < 60; i++) PaletteSearch.Remember(recent, "k" + i);
        PaletteSearch.Remember(recent, "k30");
        Expect(recent.Count == PaletteSearch.MaxRecent && recent[0] == "k30" && recent.Count(k => k == "k30") == 1, "recent list bounded, no duplicates");

        var all = CommandPalette.Items(MainWindow.Instance);
        Expect(all.Any(i => i.Kind == "Page" && i.Key == "page:hosts") && all.Any(i => i.Key == "setting:Alerts") && all.Any(i => i.Key == "setting:Highlighting & button bar"), "pages and the new settings cards");
        Expect(all.Any(i => i.Key == "action:lock") && PaletteSearch.Search(all, "lock app").FirstOrDefault()?.Key == "action:lock", "actions");
        Expect(AppData.Current.Hosts.Count == 0 || all.Count(i => i.Kind == "Host") == AppData.Current.Hosts.Count, "every host");
        return Task.CompletedTask;
    }

    private static Task TmuxAndRdp()
    {
        var s = AppSettings.Current;
        var prior = s.TmuxAlways;
        var linux = new HostEntry { Name = "tmux-01", Address = "192.0.2.71", Platform = "linux" };
        var win = new HostEntry { Name = "win-01", Address = "192.0.2.72", Platform = "windows" };
        var views = new List<TerminalView>();
        TerminalView V(HostEntry h) { var v = new TerminalView(h, connectOnOpen: false); views.Add(v); return v; }
        try
        {
            s.TmuxAlways = false;
            Expect(!V(linux).AutoTmux, "off by default");
            s.TmuxAlways = true;
            Expect(V(linux).AutoTmux && !V(win).AutoTmux, "on for Linux, never Windows");
            linux.Tmux = "off";
            Expect(!V(linux).AutoTmux, "a host can say off");
            s.TmuxAlways = false; linux.Tmux = "on";
            Expect(V(linux).AutoTmux, "a host can say on");
        }
        finally { s.TmuxAlways = prior; foreach (var v in views) v.Dispose(); }

        var bastion = new HostEntry { Name = "bastion", Address = "198.51.100.10" };
        var rdp = new HostEntry { Name = "ws", Address = "10.20.0.5", Protocol = "rdp", Port = 0 };
        Expect(!RdpTunnel.Wanted(rdp), "direct by default");
        rdp.Rdp.Through = "ssh"; rdp.Rdp.SshHostId = bastion.Id;
        AppData.Current.Hosts.Add(bastion);
        try
        {
            Expect(RdpTunnel.Wanted(rdp) && RdpTunnel.SshHostFor(rdp)?.Id == bastion.Id, "through the chosen SSH host");
            Expect(RdpTunnel.TargetFor(rdp) == "10.20.0.5" && RdpTunnel.PortFor(rdp) == 3389, "the host's own address, port 3389 by default");
            rdp.Rdp.TunnelTarget = "10.20.0.6"; rdp.Port = 3390;
            Expect(RdpTunnel.TargetFor(rdp) == "10.20.0.6" && RdpTunnel.PortFor(rdp) == 3390, "a target as the SSH host sees it");
            Expect(RdpTunnel.NewKey(rdp) != RdpTunnel.NewKey(rdp), "each session its own tunnel");
        }
        finally { AppData.Current.Hosts.Remove(bastion); }
        return Task.CompletedTask;
    }

    private static async Task<bool> WaitScreen(TerminalView tv, string text, int ms = 15000)
    {
        for (int i = 0; i < ms / 250; i++)
        {
            if ((await tv.CaptureScreenAsync())?.Lines.Any(l => l.Contains(text)) == true) return true;
            await Task.Delay(250);
        }
        return false;
    }

    private static async Task Terminal()
    {
        var main = MainWindow.Instance;
        var s = AppSettings.Current;
        var prior = (s.TerminalAlerts, s.TerminalAlertSeconds, s.TerminalAlertBell, s.TerminalHighlight, s.TerminalButtonBar, Packs: s.TerminalHighlightPacks.ToList());
        var cmd = LocalShells.Discover().FirstOrDefault(p => p.Id == "cmd") ?? throw new Exception("cmd not discovered");
        var bar = new Snippet { Name = "wave-a echo", Command = "echo WAVE{ask:Word}A", Bar = true, Scope = "local" };
        var elsewhere = new Snippet { Name = "wave-a hidden", Command = "ver", Bar = true, Scope = "host:none-such" };
        TabItem? tab = null;
        try
        {
            s.TerminalAlerts = true; s.TerminalAlertSeconds = 30; s.TerminalAlertBell = true; s.TerminalHighlight = true; s.TerminalButtonBar = true;
            s.TerminalHighlightPacks = new() { "errors" };
            AppData.Current.Snippets.Add(bar); AppData.Current.Snippets.Add(elsewhere);
            var tv = new TerminalView(new LocalTarget(cmd), null, connectOnOpen: true);
            tab = main.AddTab("❯ wave-a", tv);
            main.Panes.Select(tab);
            for (int i = 0; i < 80 && !tv.IsConnected; i++) await Task.Delay(250);
            Expect(tv.IsConnected, "the shell didn't start");

            // Highlighting reaches the page and marks a finished error line.
            var probe = await tv.HighlightProbeAsync();
            Expect(probe is { On: true, Rules: 1 }, $"the page has the rules ({probe})");
            await tv.OnInputAsync("echo boot%COMPUTERNAME:~0,0% failed: error 42\r");
            Expect(await WaitScreen(tv, "boot failed: error 42"), "no output");
            int marks = 0;
            for (int i = 0; i < 20 && marks == 0; i++) { await Task.Delay(150); marks = (await tv.HighlightProbeAsync())?.Marks ?? 0; }
            Expect(marks >= 2, $"the error line is marked ({marks})");

            // Alerts when nobody is looking.
            SessionAlerts.NobodyWatchingForTest = true;
            lock (SessionAlerts.Recent) SessionAlerts.Recent.Clear();
            Expect(tv.Alert("bell", "Bell · test", "bell"), "the bell alerts");
            Expect(!tv.Alert("bell", "Bell · test", "bell"), "once per 10 s");
            Expect(SessionAlerts.GetPending(tab), "the tab's dot");
            tv.Activated();
            Expect(!SessionAlerts.GetPending(tab), "looking at it clears the dot");
            tv.AlertsMuted = true;
            Expect(!tv.Alert("drop", "Drop · test", "drop"), "a muted tab never alerts");
            tv.AlertsMuted = false;
            tv.CommandStartedForTest = DateTime.UtcNow.AddMinutes(-2);
            await tv.OnQuietAsync();
            Expect(tv.CommandStartedForTest == null, "the prompt ends the command");
            lock (SessionAlerts.Recent) Expect(SessionAlerts.Recent.Any(a => a.Kind == "done" && a.Text.Contains("2 min")), "a long command alerts when it finishes");
            tv.CommandStartedForTest = DateTime.UtcNow.AddSeconds(-5);
            await tv.OnQuietAsync();
            lock (SessionAlerts.Recent) Expect(SessionAlerts.Recent.Count(a => a.Kind == "done") == 1, "a short one doesn't");

            // The button bar: scoped, and sent quoted for the shell (cmd: one line, no quoting).
            tv.BuildButtonBar();
            Expect(tv.BarSnippets.Contains(bar) && !tv.BarSnippets.Contains(elsewhere), "the bar shows scoped snippets only");
            TerminalView.AnswerForTest = _ => "-\r\nX";
            Expect(await tv.SendSnippetAsync(bar, new[] { tv }) == 1 && tv.LastSnippetSent == "echo WAVE-XA", "sent: " + tv.LastSnippetSent);
            Expect(await WaitScreen(tv, "WAVE-XA"), "the shell ran it");
            s.TerminalButtonBar = false;
            tv.BuildButtonBar();
            Expect(tv.BarSnippets.Count == 0, "the setting hides the bar");
        }
        finally
        {
            TerminalView.AnswerForTest = null;
            SessionAlerts.NobodyWatchingForTest = false;
            AppData.Current.Snippets.Remove(bar); AppData.Current.Snippets.Remove(elsewhere);
            (s.TerminalAlerts, s.TerminalAlertSeconds, s.TerminalAlertBell, s.TerminalHighlight, s.TerminalButtonBar) = (prior.TerminalAlerts, prior.TerminalAlertSeconds, prior.TerminalAlertBell, prior.TerminalHighlight, prior.TerminalButtonBar);
            s.TerminalHighlightPacks = prior.Packs;
            if (tab != null) main.CloseTab(tab);
        }
    }

    /// <summary>Budgets are generous for a busy build PC; the report says the real times.</summary>
    private static async Task Scale()
    {
        var main = MainWindow.Instance;
        var data = AppData.Current;
        var nets = new[] { "192.0.2", "198.51.100", "203.0.113" };
        var added = new List<HostEntry>(10_000);
        for (int i = 0; i < 10_000; i++)
            added.Add(new HostEntry
            {
                Name = $"scale-{i:00000}", Address = $"{nets[i % 3]}.{1 + i % 254}", Username = "ops", Group = $"Scale/Region-{i % 20:00}/Site-{i % 200:000}",
                Tags = i % 7 == 0 ? "edge" : "", Inherits = i % 2 == 0 ? new() { "user" } : new(),
            });
        var savedFolders = FolderDefaults.All.ToList();
        var times = new List<string>();
        long T(string what, long budgetMs, Action a)
        {
            var sw = Stopwatch.StartNew(); a(); sw.Stop();
            times.Add($"{what} {sw.ElapsedMilliseconds} ms");
            if (sw.ElapsedMilliseconds > budgetMs) throw new Exception($"{what} took {sw.ElapsedMilliseconds} ms (budget {budgetMs} ms)");
            return sw.ElapsedMilliseconds;
        }
        try
        {
            data.Hosts.AddRange(added);
            FolderDefaults.All.Add(new FolderSettings { Path = "Scale", Username = "scale-ops" });
            FolderDefaults.All.Add(new FolderSettings { Path = "Scale/Region-04", Username = "region-ops" });
            T("folder defaults applied to 10,000", 1500, () => FolderDefaults.Apply(data.Hosts));
            Expect(added[3].Username == "ops" && added[2].Username == "scale-ops" && added[0].Username == "scale-ops" && added.First(h => h.Group.StartsWith("Scale/Region-04/") && h.Inherits.Count > 0).Username == "region-ops", "inheritance at scale");
            // 0.6.29 performance pass: budgets tightened from 8 s / 8 s / 2 s once the quadratic folder counts, eager tree
            // nodes and per-save reloads were gone (still roomy for a busy build PC; the report has the real times).
            T("encrypted save", 3000, AppData.Save);
            main.Navigate("hosts");
            await Task.Delay(200);
            var hv = main.Panes.AllTabs.Select(t => t.Content).OfType<HostsView>().FirstOrDefault() ?? throw new Exception("no Hosts screen");
            HostsView.ResetExpandedForTest();
            T("Hosts screen rebuilt", 1500, hv.RebuildForTest);
            int made = hv.TreeLeafCountForTest();
            Expect(made < 1000, $"closed folders make their host nodes when opened ({made} of 10,000 made)");
            Expect(hv.TreeLeafForTest(added[9999].Id) != null, "a host inside a closed folder is still found (made on demand)");
            T("encrypted save, Hosts screen open", 4000, AppData.Save);
            int shown = 0;
            T("Hosts list filtered", 1000, () => shown = hv.FilterListForTest("scale-0999"));
            Expect(shown == 10, $"filter result ({shown})");
            hv.FilterListForTest("");
            var pane = new SessionManagerPane();
            T("host pane rebuilt", 1500, pane.Build);
            T("host pane filtered", 1000, () => pane.Filter = "scale-0999");
            Expect(pane.ShownHosts.Count == 10, $"host pane filter result ({pane.ShownHosts.Count})");
            T("10,000 hosts found by id", 200, () => { foreach (var h in added) if (data.FindHost(h.Id) != h) throw new Exception("FindHost missed " + h.Name); });
            var gone = added[5000];
            data.Hosts.Remove(gone);
            Expect(data.FindHost(gone.Id) == null && data.FindHost(added[5001].Id) == added[5001], "the id index stays exact after a removal (no save in between)");
            data.Hosts.Add(gone);
            List<PaletteSearch.Item> items = new();
            T("palette built", 2000, () => items = CommandPalette.Items(main));
            List<PaletteSearch.Item> hits = new();
            T("palette searched", 1000, () => hits = PaletteSearch.Search(items, "scale 09999"));
            Expect(hits.FirstOrDefault()?.Title == "scale-09999", "palette finds the host");
            T("palette searched by address", 1000, () => hits = PaletteSearch.Search(items, "203.0.113.2 site-"));
            Expect(hits.Count > 0, "palette finds by address");
        }
        finally
        {
            var ids = added.Select(h => h.Id).ToHashSet();
            data.Hosts.RemoveAll(h => ids.Contains(h.Id));
            FolderDefaults.All.Clear(); FolderDefaults.All.AddRange(savedFolders);
            AppData.Save();
        }
        ScaleTimes = string.Join(" · ", times);

    }
}
