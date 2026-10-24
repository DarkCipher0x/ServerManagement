using System.Text.RegularExpressions;

namespace DcxManager.Sessions;

/// <summary>How careful the confirmation for a command must be (ADR-028). Ordered: a block takes its riskiest part.</summary>
public enum RiskLevel { ReadOnly, Change, Sensitive, Destructive }

/// <summary>The assessment shown before a command runs: the level and every reason that set it.</summary>
public sealed record RiskAssessment(RiskLevel Level, IReadOnlyList<string> Reasons, bool Interactive, bool MultiLine)
{
    /// <summary>Keeps running until stopped (tail -f, ping without a count, watch…): never run automatically (ADR-034).</summary>
    public bool Endless { get; init; }

    public string Label => Level switch
    {
        RiskLevel.ReadOnly => "read-only",
        RiskLevel.Change => "CHANGES the system",
        RiskLevel.Sensitive => "SENSITIVE (credentials, privileges or keys)",
        _ => "DESTRUCTIVE",
    };
}

/// <summary>
/// A conservative, rule-based reading of a shell command (POSIX shells and PowerShell). It errs towards caution: anything
/// not recognised as read-only counts as a change. It is a confirmation aid, not a sandbox — the person approving still
/// reads the command. Commands are split on ; && || | and new lines; each part is judged on its own.
/// </summary>
public static class CommandRisk
{
    private static readonly RegexOptions O = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static Regex R(string p) => new(p, O | RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));

    private static readonly (Regex Rx, string Why)[] Destructive =
    {
        (R(@"\brm\s+(-[a-z]*[rf][a-z]*\s+)+"), "recursive / forced delete (rm -r / -f)"),
        (R(@"\b(mkfs(\.\w+)?|wipefs|shred|blkdiscard)\b"), "wipes or formats storage"),
        (R(@"\bdd\b.*\bof="), "dd writing to a device or file"),
        (R(@"\b(fdisk|sfdisk|sgdisk|parted|gdisk)\b"), "changes partition tables"),
        (R(@">\s*/dev/(sd|nvme|vd|xvd|hd|mmcblk)"), "writes straight to a disk device"),
        (R(@":\(\)\s*\{"), "fork bomb"),
        (R(@"\b(shutdown|poweroff|halt|reboot)\b|\binit\s+[06]\b|\bsystemctl\s+(poweroff|reboot|halt|kexec)\b"), "shuts down or reboots the machine"),
        (R(@"\b(zfs|zpool)\s+(destroy|rollback)\b|\b(lvremove|vgremove|pvremove)\b"), "destroys volumes or pools"),
        (R(@"\bdrop\s+(database|table|schema)\b|\btruncate\s+table\b"), "drops database objects"),
        (R(@"\bkubectl\s+delete\b|\bhelm\s+(uninstall|delete)\b"), "deletes Kubernetes resources"),
        (R(@"\bdocker\s+(rm|rmi|system\s+prune|volume\s+(rm|prune)|network\s+rm)\b|\bpodman\s+(rm|rmi)\b"), "removes containers, images or volumes"),
        (R(@"\b(virsh\s+(destroy|undefine)|qm\s+destroy|pct\s+destroy)\b"), "destroys virtual machines"),
        (R(@"\b(iptables|ip6tables)\s+(-F|--flush)\b|\bnft\s+flush\b|\bufw\s+(reset|disable)\b"), "flushes or disables the firewall"),
        (R(@"\bchmod\s+(-R\s+)?[0-7]*777\s+/(\s|$)|\bchown\s+-R\s+\S+\s+/(\s|$)"), "changes permissions of /"),
        (R(@"\bcrontab\s+-r\b|\buserdel\b|\bgroupdel\b"), "removes users or schedules"),
        (R(@"\bgit\s+(reset\s+--hard|clean\s+-[a-z]*f)"), "discards work in a git repository"),
        (R(@"\bRemove-Item\b.*-Recurse|\b(Format-Volume|Clear-Disk|Remove-Partition|Initialize-Disk)\b"), "deletes recursively or wipes disks (PowerShell)"),
        (R(@"\b(Stop-Computer|Restart-Computer)\b"), "shuts down or restarts Windows"),
        (R(@"\b(Remove-VM|Remove-VMSnapshot|Remove-LocalUser)\b"), "removes VMs, checkpoints or users (PowerShell)"),
    };

    private static readonly (Regex Rx, string Why)[] Sensitive =
    {
        (R(@"\b(passwd|chpasswd|chage)\b"), "changes passwords"),
        (R(@"\b(useradd|usermod|adduser|groupadd|gpasswd|visudo)\b|/etc/sudoers"), "changes users, groups or sudo rights"),
        (R(@"/etc/(shadow|gshadow|passwd)\b"), "touches the account database"),
        (R(@"\bssh-keygen\b|authorized_keys|\bssh-copy-id\b"), "changes SSH keys or who may log in"),
        (R(@"\b(gpg|openssl|cryptsetup|certbot|step)\b"), "works with keys, certificates or encryption"),
        (R(@"\b(curl|wget)\b[^|]*\|\s*(sudo\s+)?(ba|z|k)?sh\b|\biwr\b.*\|\s*iex\b|\bInvoke-Expression\b"), "downloads and runs a script"),
        (R(@"\b(export|set)\s+\w*(TOKEN|SECRET|PASSW|API_?KEY|PRIVATE)\w*="), "sets a secret in the environment"),
        (R(@"^\s*(env|printenv|set)\s*$|\bcat\s+\S*(\.env|credentials|\.pem|\.key|id_rsa|id_ed25519)\b|\bGet-Credential\b"), "may print secrets (they would land in the AI context, masked)"),
        (R(@"\b(su)\b(\s|$)|\bsudo\s+(-[a-z]*[is]|su)\b"), "switches to another user's shell (the terminal's user context changes)"),
        (R(@"\bSet-ExecutionPolicy\b|\b(New-LocalUser|Add-LocalGroupMember|Set-LocalUser)\b"), "changes Windows security settings or accounts"),
        (R(@"\bsetfacl\b|\bsetcap\b|\bsetenforce\b"), "changes access control"),
    };

    private static readonly (Regex Rx, string Why)[] Change =
    {
        (R(@"\bsystemctl\s+(start|stop|restart|reload|enable|disable|mask|unmask|daemon-reload|edit|set-property)\b|\bservice\s+\S+\s+(start|stop|restart|reload)\b"), "starts, stops or reconfigures services"),
        (R(@"\b(apt|apt-get|dnf|yum|zypper|pacman|apk|snap|flatpak|pip3?|npm|gem|choco|winget|brew)\b.*\b(install|remove|purge|upgrade|update|erase|-S|-R|uninstall|dist-upgrade|autoremove)\b"), "installs, updates or removes packages"),
        (R(@"\b(firewall-cmd|ufw|iptables|ip6tables|nft)\b"), "changes the firewall"),
        (R(@"\bip\s+(addr|address|route|link|rule|neigh)\s+(add|del|delete|set|change|replace|flush)\b|\b(nmcli|netplan|ifup|ifdown|ifconfig\s+\S+\s+(up|down))\b"), "changes networking"),
        (R(@"\b(mount|umount|swapon|swapoff)\b\s+\S"), "mounts or unmounts storage"),
        (R(@"\bsed\s+(-[a-z]*i|--in-place)|\btee\b|\btruncate\b|\b(mv|cp|ln|mkdir|rmdir|touch|install)\b|\brm\b|\bchmod\b|\bchown\b|\bchgrp\b"), "creates, moves, changes or deletes files"),
        (R(@"(^|[^<>2&])>{1,2}\s*(?!/dev/null\b)[^\s&|]"), "writes to a file (redirection)"),
        (R(@"\b(kill|pkill|killall|renice)\b"), "stops or reprioritises processes"),
        (R(@"\bdocker\s+(run|start|stop|restart|exec|pull|compose|kill|update)\b|\bpodman\s+(run|start|stop|restart)\b"), "changes containers"),
        (R(@"\bkubectl\s+(apply|create|edit|patch|scale|rollout|replace|set|label|annotate|cordon|drain)\b"), "changes Kubernetes resources"),
        (R(@"\bgit\s+(push|commit|checkout|merge|rebase|pull|stash|tag|branch\s+-[dD])\b"), "changes a git repository"),
        (R(@"\b(crontab|at)\b|\b(timedatectl|hostnamectl|localectl)\s+set|\bsysctl\s+-w\b|\b(modprobe|rmmod|insmod)\b"), "changes system configuration"),
        (R(@"\bsudo\b"), "runs with root privileges (sudo)"),
        (R(@"\b(Set|New|Remove|Add|Clear|Rename|Move|Copy|Start|Stop|Restart|Suspend|Resume|Enable|Disable|Install|Uninstall|Update|Register|Unregister|Invoke|Mount|Dismount|Import|Export|Checkpoint|Restore|Save)-[A-Za-z]+"), "a PowerShell cmdlet that changes something"),
    };

    /// <summary>Programs that only read (first word of a part, after sudo/env/time/nice). Anything else counts as a change.</summary>
    private static readonly HashSet<string> ReadOnlyFirst = new(StringComparer.OrdinalIgnoreCase)
    {
        "ls", "ll", "cat", "head", "tail", "grep", "egrep", "fgrep", "rg", "find", "locate", "df", "du", "free", "uptime", "uname", "hostname", "id", "whoami", "who", "w",
        "last", "ps", "pgrep", "pstree", "ss", "netstat", "ip", "journalctl", "systemctl", "dmesg", "lsblk", "blkid", "lscpu", "lsmem", "lspci", "lsusb", "lsof", "lsmod",
        "date", "which", "type", "file", "stat", "wc", "sort", "uniq", "cut", "awk", "sed", "tr", "column", "jq", "yq", "less", "more", "echo", "printf", "pwd", "cd", "realpath",
        "readlink", "basename", "dirname", "test", "true", "false", "sleep", "docker", "podman", "kubectl", "git", "ping", "traceroute", "tracepath", "mtr", "dig", "nslookup",
        "host", "curl", "wget", "openssl", "getent", "groups", "timedatectl", "hostnamectl", "localectl", "loginctl", "vmstat", "iostat", "mpstat", "sar", "top", "htop",
        "nproc", "arch", "env", "printenv", "sha256sum", "md5sum", "cmp", "diff", "zcat", "zgrep", "xxd", "hexdump", "strings", "tree", "findmnt", "mount", "swapon",
        "lvs", "vgs", "pvs", "zpool", "zfs", "virsh", "qm", "pct", "pvesh", "ceph", "nft", "iptables", "firewall-cmd", "ufw", "sysctl", "crontab", "apt", "dnf", "yum", "rpm", "dpkg",
        "Get-ChildItem", "Get-Content", "Get-Process", "Get-Service", "Get-Item", "Get-ItemProperty", "Get-Location", "Get-Date", "Get-NetIPAddress", "Get-NetIPConfiguration",
        "Get-NetTCPConnection", "Get-WinEvent", "Get-EventLog", "Get-VM", "Get-VMSnapshot", "Get-Volume", "Get-Disk", "Get-CimInstance", "Get-ComputerInfo", "Get-HotFix",
        "Get-Command", "Get-Help", "Test-Connection", "Test-NetConnection", "Test-Path", "Resolve-DnsName", "Select-String", "Select-Object", "Where-Object", "Sort-Object",
        "Format-Table", "Format-List", "Measure-Object", "Out-String", "Write-Output", "ipconfig", "dir", "type", "tasklist", "systeminfo", "whoami", "hostname",
        "Get-WmiObject", "Get-Member", "Get-NetAdapter", "Get-NetRoute", "Get-PSDrive", "Get-Partition", "Get-PhysicalDisk", "Get-ScheduledTask", "Get-LocalUser",
        "Get-LocalGroup", "Get-LocalGroupMember", "Get-SmbShare", "Get-TimeZone", "Get-Uptime", "Get-FileHash", "Get-Acl", "Get-WindowsOptionalFeature", "Get-Counter",
        "Get-Module", "Get-Culture", "Group-Object", "Format-Wide", "ConvertTo-Json", "ConvertTo-Csv", "Get-Unique",
    };

    /// <summary>Read-only programs whose arguments can still make them change something (checked by the rules above,
    /// plus these: e.g. "systemctl restart", "docker rm", "find -delete").</summary>
    private static readonly (Regex Rx, string Why)[] ReadOnlyButNotWith =
    {
        (R(@"\bfind\b.*\s-(delete|exec|execdir|ok|fprint)\b"), "find that deletes or runs commands"),
        (R(@"\bsystemctl\s+(?!(status|is-active|is-enabled|is-failed|list-units|list-unit-files|list-timers|show|cat|--version|-a|--failed|list-dependencies)\b)\S"), "systemctl beyond read-only subcommands"),
        (R(@"\bdocker\s+(?!(ps|images|logs|inspect|stats|top|version|info|port|diff|history|network\s+ls|volume\s+ls|compose\s+(ps|logs|config))\b)\S"), "docker beyond read-only subcommands"),
        (R(@"\bkubectl\s+(?!(get|describe|logs|top|version|cluster-info|explain|api-resources|config\s+view|auth\s+can-i)\b)\S"), "kubectl beyond read-only subcommands"),
        (R(@"\bgit\s+(?!(status|log|diff|show|branch(\s|$)|remote\s+-v|rev-parse|blame|ls-files|describe|tag(\s|$))\b)\S"), "git beyond read-only subcommands"),
        (R(@"\b(curl|wget)\b.*\s(-X\s*(POST|PUT|DELETE|PATCH)|--data|-d\s|-o\s|-O\b|--output|--upload-file|-T\s)"), "curl/wget that sends data or writes files"),
        (R(@"\b(apt|dnf|yum)\s+(?!(list|search|show|info|policy|history|check-update|repolist)\b)\S"), "a package manager beyond read-only subcommands"),
        (R(@"\b(virsh|qm|pct)\s+(?!(list|status|config|dominfo|domstate|pending|snapshot-list|listsnapshot)\b)\S"), "hypervisor CLI beyond read-only subcommands"),
        (R(@"\b(zfs|zpool)\s+(?!(list|get|status|iostat|history)\b)\S"), "zfs beyond read-only subcommands"),
        (R(@"\b(nft|iptables|firewall-cmd|ufw)\b(?!.*\s(list|-L|-S|--list-all|--state|status)\b)"), "firewall tool beyond listing rules"),
        (R(@"\bsysctl\s+(-w|\S+=)"), "sysctl that writes"),
        (R(@"\bcrontab\s+(?!-l\b)\S"), "crontab beyond listing"),
        (R(@"\bopenssl\s+(genrsa|genpkey|req|ca|rsa\s+.*-out|pkcs12\s+.*-export)"), "openssl that creates keys or certificates"),
        (R(@"\bsed\b.*(['""\s;{/]w\s+\S|\s-[a-z]*i)"), "sed that writes a file"),
        (R(@"\b(awk|gawk)\b.*\bsystem\s*\("), "awk that runs commands"),
        (R(@"(\)|\$[\w:]+)\s*\.\s*(?!(ToString|ToUpper|ToLower|ToUpperInvariant|ToLowerInvariant|Trim|TrimStart|TrimEnd|Split|Replace|Substring|Contains|StartsWith|EndsWith|IndexOf|GetType|Equals|PadLeft|PadRight|Count|ToShortDateString|ToLongDateString)\s*\()[A-Za-z_]\w*\s*\("),"calls a method on an object, e.g. (Get-WmiObject …).Delete() (it may change something)"),
    };

    /// <summary>"powershell -Command "…"" (or pwsh -c) with only harmless switches: the inner command is what runs, so it
    /// is judged instead of the wrapper. -EncodedCommand, -ExecutionPolicy, -File etc. are not unwrapped (they stay a change).</summary>
    private static readonly Regex NestedPs = new(@"^\s*(&\s*)?(powershell|pwsh)(\.exe)?((\s+-(NoProfile|NoLogo|NonInteractive|nop|nol|noni))*)\s+-(Command|c)\s+(?<inner>.+)$",
        O | RegexOptions.Singleline | RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));

    /// <summary>The command inside a nested PowerShell call (outer quotes removed), or null when it isn't one.</summary>
    public static string? NestedPowerShell(string command)
    {
        Match m;
        try { m = NestedPs.Match(command.Trim()); } catch (RegexMatchTimeoutException) { return null; }
        if (!m.Success) return null;
        var inner = m.Groups["inner"].Value.Trim();
        if (inner.Length >= 2 && (inner[0] == '"' && inner[^1] == '"' || inner[0] == '\'' && inner[^1] == '\''))
        {
            var body = inner[1..^1];
            if (body.Contains(inner[0])) return null;   // nested quoting: too ambiguous to unwrap, judge the whole line
            inner = body;
        }
        return inner.Trim().Length == 0 ? null : inner.Trim();
    }

    /// <summary>Commands that don't end on their own (ADR-034: never auto-run; they would hold the session until the time limit).</summary>
    private static readonly Regex EndlessRx = R(@"\b(tail|less)\s+(-[a-zA-Z]*[fF]|--follow)|\bjournalctl\b.*\s(-f|--follow)\b|\b(docker|podman|kubectl)\s+(compose\s+)?logs\b.*\s(-f|--follow)\b|\bwatch\b|\bdmesg\b.*\s(-w|--follow)\b|\bping\b(?!.*\s-(c|n|w)\s*\d)|\btop\b(?!.*\s-(b|n))|\bGet-Content\b.*-Wait\b|\bTest-Connection\b.*-Continuous|\bsleep\s+([6-9]\d|\d{3,})\b|\bStart-Sleep\b|\bopenssl\s+s_client\b(?!.*</dev/null)");

    private static readonly Regex Interactive = R(@"(^|[;&|]\s*)(sudo\s+)?(vi|vim|nvim|nano|emacs|less|more|top|htop|watch|tmux|screen|mc|ssh(?!-)|mysql|psql|python3?|node|irb|ftp|telnet|crontab\s+-e|visudo|passwd)(\s|$)");

    public static RiskAssessment Assess(string command)
    {
        var text = command.Replace("\r\n", "\n").Trim();
        var reasons = new List<string>();
        var level = RiskLevel.ReadOnly;
        void Bump(RiskLevel l, string why) { if (!reasons.Contains(why)) reasons.Add(why); if (l > level) level = l; }

        if (text.Length == 0) return new RiskAssessment(RiskLevel.ReadOnly, new[] { "empty" }, false, false);
        if (NestedPowerShell(text) is { } inner)
        {
            var a = Assess(inner);
            return a with { Reasons = a.Reasons.Prepend("starts a second PowerShell just to run the command inside; judged by that command").ToList() };
        }
        foreach (var (rx, why) in Destructive) if (Safe(rx, text)) Bump(RiskLevel.Destructive, why);
        foreach (var (rx, why) in Sensitive) if (Safe(rx, text)) Bump(RiskLevel.Sensitive, why);
        foreach (var part in Parts(text))
        {
            foreach (var (rx, why) in Change) if (Safe(rx, part)) Bump(RiskLevel.Change, why);
            foreach (var (rx, why) in ReadOnlyButNotWith) if (Safe(rx, part)) Bump(RiskLevel.Change, why);
            var first = FirstWord(part);
            if (first.Length > 0 && !ReadOnlyFirst.Contains(first)) Bump(RiskLevel.Change, $"“{first}” is not on the read-only list");
        }
        if (text.Contains("`") || text.Contains("$(")) Bump(RiskLevel.Change, "contains command substitution (judge what it runs)");
        bool multi = text.Contains('\n');
        if (multi) reasons.Add("multi-line script: review the whole block");
        bool interactive = Safe(Interactive, text);
        if (interactive) reasons.Add("starts an interactive program — it will wait for keyboard input; prefer a non-interactive form");
        bool endless = Safe(EndlessRx, text);
        if (endless) reasons.Add("keeps running until stopped (follow / watch / ping without a count)");
        if (reasons.Count == 0) reasons.Add("only reads information");
        return new RiskAssessment(level, reasons, interactive, multi) { Endless = endless };
    }

    private static bool Safe(Regex rx, string s) { try { return rx.IsMatch(s); } catch (RegexMatchTimeoutException) { return true; } }   // a timeout counts as a match: be cautious

    /// <summary>Simple commands of a block: split on new lines, ; && || and pipes (quotes are not parsed — splitting too much
    /// only makes the judgement stricter).</summary>
    public static IEnumerable<string> Parts(string text) =>
        Regex.Split(text, @"\n|;|&&|\|\||\|").Select(p => p.Trim()).Where(p => p.Length > 0 && !p.StartsWith('#') && p is not ("{" or "}" or "do" or "done" or "then" or "fi" or "else"));

    /// <summary>The program of a simple command, skipping sudo / env assignments / time / nice / nohup / command.</summary>
    public static string FirstWord(string part)
    {
        var words = part.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        while (words.Count > 0 && (words[0] is "sudo" or "time" or "nice" or "nohup" or "command" or "exec" or "builtin" or "env" || words[0].StartsWith('-') && words.Count > 1 || Regex.IsMatch(words[0], @"^[A-Za-z_][A-Za-z0-9_]*=")))
            words.RemoveAt(0);
        if (words.Count == 0) return "";
        var w = words[0].Trim('(', ')', '{', '}', '"', '\'');
        return w.Contains('/') ? w[(w.LastIndexOf('/') + 1)..] : w;
    }
}
