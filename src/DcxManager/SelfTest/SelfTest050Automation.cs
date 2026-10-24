using System.IO;
using System.Windows;
using System.Windows.Threading;
using DcxManager.Puppet;
using DcxManager.Services;
using DcxManager.Ssh;
using DcxManager.Views;

namespace DcxManager;

/// <summary>
/// 0.5.0 automation tests (#9): Puppet's command builders refuse anything that isn't a valid certname, environment, tag,
/// Hiera key or Bolt name; the parsers read real tool output; the Puppet screen runs against a fake SSH executor (nothing
/// real is contacted), stays read-only until changes are allowed, and records what it would have run.
/// </summary>
public static class Release050AutomationTests
{
    public static async Task RunAsync(MainWindow main, Action<string> ok, Action<string, Exception?> fail)
    {
        async Task Settle(int ms = 200)
        {
            await Task.Delay(ms);
            await Application.Current.Dispatcher.InvokeAsync(() => main.UpdateLayout(), DispatcherPriority.ApplicationIdle);
        }

        try
        {
            if (PuppetCommands.Cert(" WEB01.Example.com ") != "web01.example.com") throw new Exception("certname normalising");
            foreach (var bad in new[] { "a;b", "x y", "-rf", "web01'", "$(id)", "" })
                try { PuppetCommands.Cert(bad); throw new Exception("accepted certname " + bad); } catch (ArgumentException) { }
            foreach (var bad in new[] { "Prod", "prod-1", "a b", "x;y" })
                try { PuppetCommands.Env(bad); throw new Exception("accepted environment " + bad); } catch (ArgumentException) { }
            try { PuppetCommands.Tags("a b"); throw new Exception("tags with a space"); } catch (ArgumentException) { }
            try { PuppetCommands.Disable(""); throw new Exception("disable without a reason"); } catch (ArgumentException) { }
            try { PuppetCommands.Bolt("task", "package; rm", Array.Empty<string>(), false); throw new Exception("bolt task name"); } catch (ArgumentException) { }
            try { PuppetCommands.Bolt("command", "a\nb", Array.Empty<string>(), false); throw new Exception("bolt multi-line command"); } catch (ArgumentException) { }
            var run = PuppetCommands.AgentRun(true, "production", "profile::web,nginx");
            if (!run.Contains("--noop") || !run.Contains("--environment 'production'") || !run.Contains("--tags 'profile::web,nginx'") || !run.Contains("--detailed-exitcodes")) throw new Exception(run);
            if (!PuppetCommands.Disable("patching tonight; don't run").Contains("'patching tonight; don'\\''t run'")) throw new Exception("disable reason quoting");
            if (!PuppetCommands.PqlFacts("web01.example.com").Contains("certname = \"web01.example.com\"")) throw new Exception("PQL");

            var ca = PuppetCommands.ParseCaList("Requested Certificates:\n    web03.example.com       (SHA256)  AA:BB:CC:DD\nSigned Certificates:\n    puppet.example.com      (SHA256)  11:22:33:44\talt names: [\"DNS:puppet\", \"DNS:puppet.example.com\"]\n    web01.example.com       (SHA256)  55:66:77:88\nRevoked Certificates:\n    old.example.com         (SHA256)  99:AA:BB:CC\n");
            if (ca.Count != 4 || ca.Count(c => c.State == "requested") != 1 || ca.Single(c => c.Certname == "puppet.example.com").AltNames.Length == 0) throw new Exception("CA list parsing");
            var lr = PuppetCommands.ParseLastRun("disabled: {\"disabled_message\":\"patching\"}\n---\nversion:\n  config: \"abc123\"\n  puppet: 8.10.0\nresources:\n  changed: 3\n  failed: 1\n  total: 412\n  out_of_sync: 4\ntime:\n  last_run: 1761040000\nevents:\n  failure: 1\n");
            if (lr is not { Changed: 3, Failed: 1, Total: 412, OutOfSync: 4, ConfigVersion: "abc123", PuppetVersion: "8.10.0", DisabledReason: "patching" } || lr.LastRun == null) throw new Exception("last run parsing");
            var (cols, rows) = PuppetCommands.ParseRows("[{\"certname\":\"web01\",\"latest_report_status\":\"failed\",\"facts\":{\"a\":1}},{\"certname\":\"web02\",\"latest_report_status\":null}]");
            if (cols.Count != 3 || rows.Count != 2 || rows[1]["latest_report_status"] != "" || !rows[0]["facts"].StartsWith("{")) throw new Exception("PuppetDB rows");
            try { PuppetCommands.ParseRows("{\"error\":\"x\"}"); throw new Exception("a non-list answer was accepted"); } catch (InvalidDataException) { }
            ok("puppet commands: certnames, environments, tags, Hiera keys, reasons and Bolt names are checked or shell-quoted before they reach SSH (hostile ones refused); `puppetserver ca list`, last_run_summary.yaml and PuppetDB answers are parsed into rows");
        }
        catch (Exception ex) { fail("puppet commands", ex); }

        // WSL (#14): parsing and editing only — nothing is started, stopped or written on this PC.
        try
        {
            var l = Wsl.WslManager.ParseList("  NAME            STATE           VERSION\n* Ubuntu-24.04    Running         2\n  Debian          Stopped         2\n  docker-desktop  Stopped         2\n  bad;name        Stopped         2\n");
            if (l.Count != 3 || !l[0].IsDefault || l[0].State != "Running" || l.Any(d => d.Name.Contains(';'))) throw new Exception("wsl --list --verbose parsing");
            var cfg = "# mine\r\n[wsl2]\r\nmemory=4GB\r\nkernelCommandLine = quiet\r\n\r\n[experimental]\r\nsparseVhd=true\r\n";
            var upd = Wsl.WslManager.SetWslConfig(cfg, new Dictionary<string, string> { ["memory"] = "8GB", ["networkingMode"] = "mirrored", ["swap"] = "" });
            var v = Wsl.WslManager.ReadWslConfig(upd);
            if (v["memory"] != "8GB" || v["networkingMode"] != "mirrored" || !upd.Contains("# mine") || !upd.Contains("kernelCommandLine = quiet") || !upd.Contains("[experimental]") || !upd.Contains("sparseVhd=true")) throw new Exception(".wslconfig edit lost something: " + upd);
            if (upd.IndexOf("networkingMode", StringComparison.Ordinal) > upd.IndexOf("[experimental]", StringComparison.Ordinal)) throw new Exception("key written outside [wsl2]");
            try { Wsl.WslManager.SetWslConfig(cfg, new Dictionary<string, string> { ["memory"] = "lots" }); throw new Exception("bad memory value accepted"); } catch (ArgumentException) { }
            try { Wsl.WslManager.SetWslConfig(cfg, new Dictionary<string, string> { ["kernel"] = "x" }); throw new Exception("unknown key accepted"); } catch (ArgumentException) { }
            var (cols, rows) = Wsl.WslManager.ParseTable("CONTAINER ID   IMAGE          STATUS          NAMES\nab12cd34ef56   nginx:latest   Up 2 minutes    web\n");
            if (cols.Count != 4 || rows.Single()[3] != "web" || rows[0][1] != "nginx:latest") throw new Exception("wslc table parsing");
            foreach (var bad in new[] { "a b", "x;y", "-x", "" }) try { Wsl.WslManager.Name(bad); throw new Exception("accepted distro " + bad); } catch (ArgumentException) { }
            try { Wsl.WslManager.Id("a;b"); throw new Exception("container id"); } catch (ArgumentException) { }
            try { Wsl.WslManager.Image("Nginx Latest"); throw new Exception("image name"); } catch (ArgumentException) { }
            try { _ = Wsl.WslManager.ReadFileAsync("Ubuntu", "/etc/shadow", CancellationToken.None); throw new Exception("read a file outside the editable list"); } catch (ArgumentException) { }
            ok($"WSL (#14): distributions read from wsl --list --verbose (default, state, version; odd names dropped), .wslconfig edited in [wsl2] only with every other line, comment and section kept and values checked, wslc tables read by their header, only /etc/wsl.conf, /etc/hosts and resolv.conf can be read or written, names / ids / images checked before wsl.exe or wslc.exe sees them; WSL installed here: {(Wsl.WslManager.Installed ? "yes" : "no")}, WSL Containers: {(Wsl.WslManager.WslcExe != null ? "yes" : "no")}");
        }
        catch (Exception ex) { fail("WSL (#14)", ex); }

        // IDE integration (#15): links and the SSH config text only — ~/.ssh is never touched by the self-test.
        try
        {
            var code = new Ide.IdeIntegration.Editor("vscode", "Visual Studio Code", "vscode");
            var gw = new Ide.IdeIntegration.Editor("gateway", "JetBrains Gateway", "jetbrains-gateway");
            var jump = new HostEntry { Id = "aaaaaa11", Name = "Bastion EU", Address = "203.0.113.5", Username = "jump", Port = 2222 };
            var web = new HostEntry { Id = "bbbbbb22", Name = "Web 01 (prod)", Address = "web01.example.com", Username = "deploy", Port = 22, JumpHostId = jump.Id, KerberosSignOn = true };
            if (Ide.IdeIntegration.Alias(web) != "dcx-web-01-prod-bbbbbb") throw new Exception("alias " + Ide.IdeIntegration.Alias(web));
            var u = Ide.IdeIntegration.SshUri(code, web, "/srv/my app");
            if (u != "vscode://vscode-remote/ssh-remote+dcx-web-01-prod-bbbbbb/srv/my%20app") throw new Exception(u);
            if (!Ide.IdeIntegration.SshUri(gw, web).StartsWith("jetbrains-gateway://connect#type=ssh&deploy=false&host=web01.example.com&port=22&user=deploy")) throw new Exception("gateway link");
            if (Ide.IdeIntegration.WslUri(code, "Ubuntu-24.04", "/home/jo") != "vscode://vscode-remote/wsl+Ubuntu-24.04/home/jo") throw new Exception("wsl link");
            if (Ide.IdeIntegration.LocalUri(code, @"C:\Repos\my app") != "vscode://file/C:/Repos/my%20app") throw new Exception("local link " + Ide.IdeIntegration.LocalUri(code, @"C:\Repos\my app"));
            var cfg = Ide.IdeIntegration.Render(new[] { jump, web, new HostEntry { Name = "evil", Address = "x\nProxyCommand calc", Username = "u" } });
            if (!cfg.Contains("ProxyJump " + Ide.IdeIntegration.Alias(jump)) || !cfg.Contains("GSSAPIAuthentication yes") || !cfg.Contains("Port 2222") || cfg.Contains("ProxyCommand") || cfg.Contains("IdentityFile")) throw new Exception("config text:\n" + cfg);
            try { Ide.IdeIntegration.SshUri(code, new HostEntry { Address = "a b", Username = "u" }); throw new Exception("a hostile address made a link"); } catch (ArgumentException) { }
            try { Ide.IdeIntegration.Open("https://example.com/"); throw new Exception("a non-IDE link would be opened"); } catch (ArgumentException) { }
            ok($"IDE integration (#15): Remote-SSH, WSL, local-folder and JetBrains Gateway links built with escaped paths and stable aliases; dcx_config holds address, user, port, Kerberos and ProxyJump only (no key, path or secret; a host with a line break in its address is left out); only IDE link schemes are ever opened; editors found here: {(Ide.IdeIntegration.Installed() is { Count: > 0 } f ? string.Join(", ", f.Select(e => e.Title)) : "none")}");
        }
        catch (Exception ex) { fail("IDE integration (#15)", ex); }

        var s = AppSettings.Current;
        bool allowWas = s.PuppetAllowChanges;
        var hosts = new List<HostEntry>
        {
            new() { Name = "pt-puppet", Address = "192.0.2.180", Username = "ops", Group = "pttest" },
            new() { Name = "pt-web01", Address = "192.0.2.181", Username = "ops", Group = "pttest" },
            new() { Name = "pt-web02", Address = "192.0.2.182", Username = "ops", Group = "pttest" },
        };
        try
        {
            AppData.Current.Hosts.AddRange(hosts); AppData.Save();
            main.Navigate("puppet"); await Settle(400);
            var pv = main.Panes.AllTabs.Select(t => t.Content).OfType<PuppetView>().FirstOrDefault() ?? throw new Exception("no Puppet screen (sidebar → AUTOMATION → Puppet)");
            var ran = new List<(string Host, string Cmd, bool Sudo)>();
            pv.ExecForTest = (h, cmd, sudo) =>
            {
                lock (ran) ran.Add((h.Name, cmd, sudo));
                var outText = cmd.Contains("ca list") ? "Requested Certificates:\n    web03.example.com   (SHA256)  AA:BB\nSigned Certificates:\n    web01.example.com   (SHA256)  11:22\n"
                    : cmd.Contains("lastrunfile") ? "---\nresources:\n  changed: 2\n  failed: 0\n  total: 50\ntime:\n  last_run: 1761040000\n"
                    : "Notice: Applied catalog in 1.2 seconds";
                return Task.FromResult(new ExecResult(cmd.Contains("agent --test") ? 2 : 0, outText, ""));
            };
            pv.ConfirmForTest = _ => true;
            pv.UseForTest(hosts[0], hosts.Skip(1));
            s.PuppetAllowChanges = false;
            await pv.ListCertsForTest();
            if (pv.CertsForTest.Count != 2 || pv.CertsForTest[0].State != "requested") throw new Exception("certificates not listed");
            pv.SelectCertsForTest(c => c.Certname == "web03.example.com");
            try { await pv.SignForTest(); throw new Exception("signed while read-only"); } catch (InvalidOperationException) { }
            if (ran.Any(r => r.Cmd.Contains("ca sign"))) throw new Exception("a sign command ran while read-only");

            pv.SetNoopForTest(true);
            await pv.AgentRunForTest();
            if (ran.Count(r => r.Cmd.Contains("agent --test") && r.Cmd.Contains("--noop") && r.Sudo) != 2) throw new Exception("dry run on both agents with sudo");
            pv.SetNoopForTest(false);
            try { await pv.AgentRunForTest(); throw new Exception("a real run while read-only"); } catch (InvalidOperationException) { }

            s.PuppetAllowChanges = true;
            await pv.SignForTest();
            if (!ran.Any(r => r.Host == "pt-puppet" && r.Cmd.Contains("puppetserver ca sign --certname 'web03.example.com'") && r.Sudo)) throw new Exception("sign command");
            await pv.AgentRunForTest();
            if (ran.Count(r => r.Cmd.Contains("agent --test") && !r.Cmd.Contains("--noop")) != 2) throw new Exception("real run");
            await pv.LastRunForTest();
            if (!pv.OutputForTest.Contains("2 changed")) throw new Exception("last run summary: " + pv.OutputForTest);
            ok("puppet screen (AUTOMATION → Puppet): certificates listed from the server (requested first), signing and real runs refused while read-only (nothing sent), dry runs with --noop always allowed, sign / run on every selected agent with sudo once changes are allowed, last-run summaries shown per host — all against a fake SSH executor");
        }
        catch (Exception ex) { fail("puppet screen", ex); }
        finally
        {
            s.PuppetAllowChanges = allowWas; AppSettings.Save();
            foreach (var h in hosts) AppData.Current.Hosts.Remove(h);
            AppData.Save();
            if (main.Panes.AllTabs.FirstOrDefault(t => t.Content is PuppetView) is { } tab) main.CloseTab(tab);
        }
    }
}
