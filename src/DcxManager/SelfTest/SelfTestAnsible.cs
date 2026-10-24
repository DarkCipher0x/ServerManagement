using System.Text;
using DcxManager.Ansible;
using DcxManager.Services;
using DcxManager.Views;

namespace DcxManager;

/// <summary>
/// 0.3.0 §16: Ansible tooling. The inventory generated from saved hosts (groups from folders / environments, names made
/// safe and unique, odd hosts left out), the command lines (patterns, tags and modules validated; paths and arguments
/// quoted), the job (bootstrap on the command line, script + passwords on stdin, passwords never in the script), and
/// the screen with a fake runner (a PRODUCTION host in a changing run needs the confirmation, which the test refuses).
/// </summary>
public static class AnsibleTests
{
    public static async Task RunAsync(Action<string> ok, Action<string, Exception?> fail)
    {
        try
        {
            var hosts = new List<HostEntry>
            {
                new() { Name = "Web 01", Group = "Production/EU", Address = "203.0.113.10", Username = "deploy", Environment = "PRODUCTION" },
                new() { Name = "web-01", Group = "Lab", Address = "192.0.2.5", Port = 2222, Username = "root", Environment = "LAB" },
                new() { Name = "bad; rm -rf /", Group = "Lab", Address = "192.0.2.6", Username = "root" },
                new() { Name = "evil", Group = "Lab", Address = "$(reboot)", Username = "root" },
                new() { Name = "win", Group = "Lab", Address = "192.0.2.7", Protocol = "rdp" },
            };
            var inv = AnsibleInventory.Build(hosts);
            if (inv.Entries.Count != 3 || inv.Entries[0].Name != "web-01" || inv.Entries[1].Name != "web-01-2" || inv.Entries[2].Name != "bad-rm-rf") throw new Exception("names: " + string.Join(", ", inv.Entries.Select(e => e.Name)));
            if (!inv.Yaml.Contains("folder_production:") || !inv.Yaml.Contains("folder_production_eu:") || !inv.Yaml.Contains("env_production:") || !inv.Yaml.Contains("ansible_port: 2222") || inv.Yaml.Contains("reboot") || !inv.Yaml.Contains("ansible_host: \"203.0.113.10\"")) throw new Exception("yaml:\n" + inv.Yaml);
            ok("Ansible inventory: 3 of 5 hosts (an RDP host and an address with shell characters left out), names made safe and unique (web-01, web-01-2, bad-rm-rf), groups per folder level, environment and platform, port only when not 22, values quoted");
        }
        catch (Exception ex) { fail("Ansible inventory", ex); }

        try
        {
            var pb = AnsibleCommands.Playbook("~/play books/site'x.yml", new AnsibleCommands.PlaybookOptions(Check: true, Diff: true, Limit: "web*:!db", Tags: "nginx,tls", ExtraVars: "v=1 it's", Become: true, Verbosity: 2), vault: true);
            if (!pb.Contains("\"$HOME\"/'play books/site'\\''x.yml'") || !pb.Contains("--check --diff --limit 'web*:!db' --tags 'nginx,tls' -e 'v=1 it'\\''s' --become --become-password-file \"$d/bp.sh\" --vault-password-file \"$d/vp.sh\" -vv")) throw new Exception("playbook: " + pb);
            // 0.4.0: skip tags, start at a task (any text, quoted), forks (clamped); syntax check, list tasks / hosts, collections.
            var pb2 = AnsibleCommands.Playbook("site.yml", new AnsibleCommands.PlaybookOptions(SkipTags: "slow", StartAtTask: "Install nginx; it's", Forks: 9999), vault: false);
            if (!pb2.Contains("--skip-tags 'slow' --start-at-task 'Install nginx; it'\\''s' --forks 500")) throw new Exception("playbook 0.4.0 options: " + pb2);
            if (!AnsibleCommands.SyntaxCheck("site.yml").EndsWith("'site.yml' --syntax-check") || !AnsibleCommands.ListTasks("site.yml", "web*", "tls").EndsWith("--list-tasks --limit 'web*' --tags 'tls'") || !AnsibleCommands.ListHosts("site.yml", "").EndsWith("--list-hosts")
                || !AnsibleCommands.CollectionInstall("~/req.yml").StartsWith("ansible-galaxy collection install -r \"$HOME\"/'req.yml'")) throw new Exception("syntax / list / collections");
            foreach (var bad in new Action[] { () => AnsibleCommands.Ping("all; reboot"), () => AnsibleCommands.CheckTags("a b"), () => AnsibleCommands.Doc("copy && id"), () => AnsibleCommands.Vault("drop", "x"), () => AnsibleCommands.CheckPath("a\nb"), () => AnsibleCommands.CheckTaskName("a\tb") })
            {
                try { bad(); throw new Exception("accepted: " + bad.Method.Name); } catch (ArgumentException) { }
            }
            var job = AnsibleCommands.Make("Ping", AnsibleCommands.Ping("all"), "all:\n  hosts: {}\n", "V4ult-pw", "Bec0me-pw");
            if (job.Command != AnsibleCommands.Bootstrap || job.Command.Contains("V4ult") || job.Command.Contains("inventory")) throw new Exception("command: " + job.Command);
            var lines = job.Stdin.Split('\n');
            var script = Encoding.UTF8.GetString(Convert.FromBase64String(lines[0]));
            if (lines[1] != "V4ult-pw" || lines[2] != "Bec0me-pw" || script.Contains("V4ult") || script.Contains("Bec0me")) throw new Exception("stdin layout / a password in the script");
            if (!script.Contains("mktemp -d") || !script.Contains("chmod 700") || !script.Contains("trap 'rm -rf \"$d\"' EXIT") || !script.Contains("read -r DCX_VP") || !script.Contains("vp.sh") || !script.Contains("ansible -i \"$d/inventory.yml\" 'all' -m ping </dev/null")) throw new Exception("script: " + script);
            try { AnsibleCommands.Make("x", "true", "", "a\nb"); throw new Exception("a password with a line break was accepted"); } catch (ArgumentException) { }
            ok("Ansible commands: playbook line with every option (0.4.0: skip tags, start at a task, forks clamped to 500), quoted paths (~/ expanded, quotes escaped) and extra vars; syntax check, list tasks / hosts and collection install built read-only; patterns, tags, module names, task names, vault operations and paths validated; the job is a fixed bootstrap on the command line, the script (base64) and the two passwords on the next stdin lines — never in the script or the command");
        }
        catch (Exception ex) { fail("Ansible commands", ex); }

        try
        {
            var prod = new HostEntry { Name = "selftest-ansible-prod", Group = "Selftest", Address = "203.0.113.20", Username = "ops", Environment = "PRODUCTION" };
            AppData.Current.Hosts.Add(prod);
            try
            {
                var fake = new FakeRunner();
                IReadOnlyCollection<HostEntry>? confirmedFor = null;
                var view = new AnsibleView { RunnerForTest = () => fake, PromptForTest = _ => "pw-from-prompt", ConfirmForTest = hosts => { confirmedFor = hosts; return false; } };
                view.SelectForTest(h => h.Name == "selftest-ansible-prod" || h.Name == "selftest-host");
                if (!view.Inventory().Yaml.Contains("selftest-ansible-prod")) throw new Exception("inventory from the screen");
                await view.RunForTest("Ping", AnsibleCommands.Ping("all"), false);
                if (fake.Jobs.Count != 1 || !view.OutputForTest.Contains("fake: ok") || !view.StatusForTest.Contains("done")) throw new Exception("ping run: " + view.StatusForTest + " / " + view.OutputForTest);
                var script0 = Encoding.UTF8.GetString(Convert.FromBase64String(fake.Jobs[0].Stdin.Split('\n')[0]));
                var invB64 = System.Text.RegularExpressions.Regex.Match(script0, @"printf %s '([A-Za-z0-9+/=]+)' \| base64 -d > ""\$d/inventory\.yml""").Groups[1].Value;
                if (invB64.Length == 0 || !Encoding.UTF8.GetString(Convert.FromBase64String(invB64)).Contains("selftest-ansible-prod")) throw new Exception("the inventory didn't reach the job");
                await view.RunForTest("Ad-hoc shell", AnsibleCommands.AdHoc("all", "shell", "reboot", false), true);   // PRODUCTION in the run → the confirmation (refused in the self-test)
                if (fake.Jobs.Count != 1 || confirmedFor == null || !confirmedFor.Any(h => h.Name == "selftest-ansible-prod")) throw new Exception("a changing run on a PRODUCTION host went ahead without the confirmation");
                await view.RunForTest("Playbook", AnsibleCommands.Playbook("site.yml", new AnsibleCommands.PlaybookOptions(Become: true), vault: true), false);
                var stdin = fake.Jobs[1].Stdin.Split('\n');
                if (stdin[1] != "pw-from-prompt" || stdin[2] != "pw-from-prompt" || view.OutputForTest.Contains("pw-from-prompt")) throw new Exception("prompted passwords on stdin only");
                view.Dispose();
                ok("Ansible screen: inventory from the ticked hosts reaches the job; a ping runs through the (fake) control node and streams its output; a changing ad-hoc run with a PRODUCTION host stops at the confirmation; vault / become passwords prompted and sent on stdin only, never shown");
            }
            finally { AppData.Current.Hosts.Remove(prod); }
        }
        catch (Exception ex) { fail("Ansible screen", ex); }
    }

    private sealed class FakeRunner : IAnsibleRunner
    {
        public List<AnsibleCommands.Job> Jobs { get; } = new();
        public string Name => "fake-node";
        public Task<int> RunAsync(AnsibleCommands.Job job, Action<string> onLine, CancellationToken ct)
        {
            Jobs.Add(job);
            onLine("fake: ok");
            return Task.FromResult(0);
        }
    }
}
