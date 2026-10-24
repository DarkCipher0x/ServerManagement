using System.Text;
using System.Text.RegularExpressions;
using DcxManager.Ssh;

namespace DcxManager.Ansible;

/// <summary>
/// Builds what runs on the control node (0.3.0 §16). Every job is one shell command plus text on stdin:
/// <list type="bullet">
/// <item>the command is a fixed bootstrap: <c>sh -c 'IFS= read -r s &amp;&amp; eval "$(printf %s "$s" | base64 -d)"'</c>
/// — it reads the first stdin line (the script, base64) and runs it; nothing of the job is on the command line;</item>
/// <item>the script makes a private temp dir (mode 700, removed on exit), reads the next two stdin lines into
/// DCX_VP / DCX_BP (vault and become passwords, empty when not used), writes the inventory and two tiny scripts
/// (vp.sh / bp.sh print those variables — Ansible reads passwords through --vault-password-file /
/// --become-password-file), then runs the ansible command with stdin from /dev/null.</item>
/// </list>
/// Passwords therefore never touch argv, a file or the audit log. Every user value is validated (patterns, tags,
/// module names) or shell-quoted (paths, arguments, extra vars).
/// </summary>
public static class AnsibleCommands
{
    /// <summary>What the bootstrap shell runs: the first stdin line, base64-decoded.</summary>
    public const string BootstrapScript = "IFS= read -r s && eval \"$(printf %s \"$s\" | base64 -d)\"";
    public static readonly string Bootstrap = "sh -c " + RemoteExec.Quote(BootstrapScript);

    public sealed record Job(string Title, string Command, string Stdin, bool Changes);

    /// <param name="Forks">Parallel hosts (0 = Ansible's default of 5).</param>
    /// <param name="StartAtTask">--start-at-task: the task name to begin at (empty = the start).</param>
    /// <param name="SkipTags">--skip-tags.</param>
    public sealed record PlaybookOptions(bool Check = false, bool Diff = false, string Limit = "", string Tags = "", string ExtraVars = "", bool Become = false, int Verbosity = 0,
        int Forks = 0, string StartAtTask = "", string SkipTags = "");

    private static readonly Regex PatternRx = new(@"^[A-Za-z0-9_.:*!&,\-~\[\]]{1,200}$");
    private static readonly Regex TagsRx = new(@"^[A-Za-z0-9_,.\-]{1,200}$");
    private static readonly Regex ModuleRx = new(@"^[A-Za-z0-9_.]{1,100}$");
    private static readonly Regex OpRx = new(@"^(encrypt|decrypt|view|rekey|edit-check)$");

    private static string Q(string s) => RemoteExec.Quote(s);

    public static string CheckPattern(string p) { p = p.Trim(); if (p.Length == 0) return "all"; if (!PatternRx.IsMatch(p)) throw new ArgumentException("A host pattern is names, groups, * ! & : , only."); return p; }
    public static string CheckTags(string t) { t = t.Trim(); if (t.Length > 0 && !TagsRx.IsMatch(t)) throw new ArgumentException("Tags are letters, digits, _ . - separated by commas."); return t; }
    public static string CheckModule(string m) { m = m.Trim(); if (!ModuleRx.IsMatch(m)) throw new ArgumentException("A module name is like ping, setup, ansible.builtin.copy."); return m; }
    public static string CheckPath(string p)
    {
        p = p.Trim();
        if (p.Length == 0 || p.Length > 1024 || p.Any(char.IsControl)) throw new ArgumentException("Enter a path on the control node (e.g. ~/playbooks/site.yml).");
        return p;
    }
    /// <summary>A task name for --start-at-task: any text without control characters, quoted.</summary>
    public static string CheckTaskName(string t)
    {
        t = t.Trim();
        if (t.Length > 200 || t.Any(char.IsControl)) throw new ArgumentException("A task name is one line of up to 200 characters.");
        return t;
    }
    /// <summary>"~/x" is expanded by the script (quoted paths aren't); anything else stays literal.</summary>
    private static string P(string path) => path.StartsWith("~/") ? "\"$HOME\"/" + Q(path[2..]) : Q(path);

    // ---- the ansible command lines (inside the script, $d is the temp dir) ---------------------------------------------

    public static string Version() => "ansible --version && (ansible-lint --version 2>/dev/null || echo 'ansible-lint: not installed') && (ansible-galaxy --version | head -n 1)";
    public static string Ping(string pattern) => $"ansible -i \"$d/inventory.yml\" {Q(CheckPattern(pattern))} -m ping";
    public static string Facts(string pattern, string filter) => $"ansible -i \"$d/inventory.yml\" {Q(CheckPattern(pattern))} -m setup" + (filter.Trim().Length > 0 ? $" -a {Q("filter=" + filter.Trim())}" : "");
    public static string AdHoc(string pattern, string module, string args, bool become) =>
        $"ansible -i \"$d/inventory.yml\" {Q(CheckPattern(pattern))} -m {Q(CheckModule(module))}" + (args.Trim().Length > 0 ? $" -a {Q(args.Trim())}" : "") + (become ? " --become --become-password-file \"$d/bp.sh\"" : "");
    public static string Playbook(string path, PlaybookOptions o, bool vault)
    {
        var sb = new StringBuilder($"ansible-playbook -i \"$d/inventory.yml\" {P(CheckPath(path))}");
        if (o.Check) sb.Append(" --check");
        if (o.Diff) sb.Append(" --diff");
        if (o.Limit.Trim().Length > 0) sb.Append(" --limit ").Append(Q(CheckPattern(o.Limit)));
        if (CheckTags(o.Tags) is { Length: > 0 } t) sb.Append(" --tags ").Append(Q(t));
        if (CheckTags(o.SkipTags) is { Length: > 0 } st) sb.Append(" --skip-tags ").Append(Q(st));
        if (o.ExtraVars.Trim().Length > 0) sb.Append(" -e ").Append(Q(o.ExtraVars.Trim()));
        if (CheckTaskName(o.StartAtTask) is { Length: > 0 } task) sb.Append(" --start-at-task ").Append(Q(task));
        if (o.Forks > 0) sb.Append(" --forks ").Append(Math.Clamp(o.Forks, 1, 500));
        if (o.Become) sb.Append(" --become --become-password-file \"$d/bp.sh\"");
        if (vault) sb.Append(" --vault-password-file \"$d/vp.sh\"");
        if (o.Verbosity > 0) sb.Append(" -").Append(new string('v', Math.Clamp(o.Verbosity, 1, 4)));
        return sb.ToString();
    }
    /// <summary>--syntax-check: parses the playbook (and its roles / includes) on the control node, runs nothing.</summary>
    public static string SyntaxCheck(string path) => $"ansible-playbook -i \"$d/inventory.yml\" {P(CheckPath(path))} --syntax-check";
    /// <summary>--list-tasks / --list-hosts: what the playbook would do, and to whom (limit and tags respected), without running it.</summary>
    public static string ListTasks(string path, string limit, string tags) =>
        $"ansible-playbook -i \"$d/inventory.yml\" {P(CheckPath(path))} --list-tasks" + (limit.Trim().Length > 0 ? " --limit " + Q(CheckPattern(limit)) : "") + (CheckTags(tags) is { Length: > 0 } t ? " --tags " + Q(t) : "");
    public static string ListHosts(string path, string limit) =>
        $"ansible-playbook -i \"$d/inventory.yml\" {P(CheckPath(path))} --list-hosts" + (limit.Trim().Length > 0 ? " --limit " + Q(CheckPattern(limit)) : "");
    public static string Lint(string path) => $"ansible-lint {P(CheckPath(path))}";
    /// <summary>ansible-galaxy collection install -r requirements.yml (collections only; <see cref="Galaxy"/> installs roles).</summary>
    public static string CollectionInstall(string requirements) => $"ansible-galaxy collection install -r {P(CheckPath(requirements))}";
    /// <summary>What is installed on the control node: collections and roles.</summary>
    public static string Installed() => "ansible-galaxy collection list 2>/dev/null; echo; ansible-galaxy role list 2>/dev/null";
    public static string Vault(string op, string path)
    {
        if (!OpRx.IsMatch(op)) throw new ArgumentException("vault operation");
        return op switch
        {
            "rekey" => $"ansible-vault rekey --vault-password-file \"$d/vp.sh\" --new-vault-password-file \"$d/bp.sh\" {P(CheckPath(path))}",
            "edit-check" => $"ansible-vault view --vault-password-file \"$d/vp.sh\" {P(CheckPath(path))} >/dev/null && echo 'the vault password opens this file'",
            _ => $"ansible-vault {op} --vault-password-file \"$d/vp.sh\" {P(CheckPath(path))}",
        };
    }
    public static string Galaxy(string requirements) => $"ansible-galaxy install -r {P(CheckPath(requirements))}";
    public static string Graph() => "ansible-inventory -i \"$d/inventory.yml\" --graph";
    public static string Doc(string module) => $"ansible-doc {Q(CheckModule(module))}";
    public static string Config() => "ansible-config dump --only-changed";

    // ---- the job: script + stdin -----------------------------------------------------------------------------------------

    /// <summary>Wraps an ansible command line into the bootstrap + script + stdin. The passwords are the 2nd and 3rd
    /// stdin lines only; the script never contains them.</summary>
    public static Job Make(string title, string ansibleCommand, string inventoryYaml, string vaultPassword = "", string becomePassword = "", bool changes = false, bool requireAnsible = true)
    {
        if (vaultPassword.Contains('\n') || vaultPassword.Contains('\r') || becomePassword.Contains('\n') || becomePassword.Contains('\r')) throw new ArgumentException("a password can't contain a line break");
        var inv = Convert.ToBase64String(Encoding.UTF8.GetBytes(inventoryYaml));
        var script = new StringBuilder();
        script.Append("set -e; d=$(mktemp -d) || exit 1; chmod 700 \"$d\"; trap 'rm -rf \"$d\"' EXIT INT TERM; ");
        script.Append("IFS= read -r DCX_VP || DCX_VP=; IFS= read -r DCX_BP || DCX_BP=; export DCX_VP DCX_BP; ");
        script.Append($"printf %s '{inv}' | base64 -d > \"$d/inventory.yml\"; ");
        script.Append("printf '#!/bin/sh\\nprintf %%s \"$DCX_VP\"\\n' > \"$d/vp.sh\"; printf '#!/bin/sh\\nprintf %%s \"$DCX_BP\"\\n' > \"$d/bp.sh\"; chmod 700 \"$d/vp.sh\" \"$d/bp.sh\"; ");
        script.Append("export ANSIBLE_FORCE_COLOR=0 ANSIBLE_NOCOLOR=1; ");
        if (requireAnsible) script.Append("if ! command -v ansible >/dev/null 2>&1; then echo 'ansible is not installed on this control node (pip install ansible, or apt/dnf install ansible)'; exit 127; fi; ");
        script.Append(ansibleCommand).Append(" </dev/null");
        var stdin = Convert.ToBase64String(Encoding.UTF8.GetBytes(script.ToString())) + "\n" + vaultPassword + "\n" + becomePassword + "\n";
        return new Job(title, Bootstrap, stdin, changes);
    }
}
