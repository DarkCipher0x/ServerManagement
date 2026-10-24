using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DcxManager.Services;
using DcxManager.Ssh;

namespace DcxManager.Puppet;

/// <summary>
/// Puppet over SSH (0.5.0, ADR-063): what runs on the Puppet server (certificate authority, environments, code deploy, Hiera
/// lookups, validation), on agents (runs with --noop first, enable / disable with a reason, last-run summary, facts, config)
/// and on the PuppetDB host (PQL queries with the host's own agent certificate, so no certificate or key is ever copied to
/// this PC). Every value that reaches a command is checked against Puppet's own rules or shell-quoted; sudo goes through
/// the app's usual stdin path (RemoteExec), never the command line. Builders and parsers only: the screen runs them.
/// </summary>
public static class PuppetCommands
{
    private static readonly Regex CertRx = new(@"^[a-z0-9][a-z0-9._\-]{0,252}$", RegexOptions.CultureInvariant);
    private static readonly Regex EnvRx = new(@"^[a-z0-9_]{1,64}$", RegexOptions.CultureInvariant);
    private static readonly Regex TagsRx = new(@"^[A-Za-z0-9_:.,\-]{1,300}$", RegexOptions.CultureInvariant);
    private static readonly Regex KeyRx = new(@"^[A-Za-z0-9_:.\-]{1,200}$", RegexOptions.CultureInvariant);

    private static string Q(string s) => RemoteExec.Quote(s);

    public static string Cert(string c) { c = c.Trim().ToLowerInvariant(); if (!CertRx.IsMatch(c)) throw new ArgumentException("A certname is lower-case letters, digits, . _ - (like web01.example.com)."); return c; }
    public static string Env(string e) { e = e.Trim(); if (!EnvRx.IsMatch(e)) throw new ArgumentException("An environment name is lower-case letters, digits and _ (like production)."); return e; }
    public static string Tags(string t) { t = t.Trim(); if (t.Length > 0 && !TagsRx.IsMatch(t)) throw new ArgumentException("Tags are class or resource names (letters, digits, _ : . -) separated by commas."); return t; }
    public static string Key(string k) { k = k.Trim(); if (!KeyRx.IsMatch(k)) throw new ArgumentException("A Hiera key is like profile::web::port."); return k; }
    public static string FilePath(string p)
    {
        p = p.Trim();
        if (p.Length == 0 || p.Length > 1024 || p.Any(char.IsControl)) throw new ArgumentException("Enter a path on the server (e.g. /etc/puppetlabs/code/environments/production/manifests/site.pp).");
        return p;
    }

    /// <summary>The PATH Puppet's packages install to, ahead of the usual one (sudo resets PATH).</summary>
    private const string Path = "PATH=/opt/puppetlabs/bin:/opt/puppetlabs/puppet/bin:/opt/puppetlabs/server/bin:$PATH; export PATH; ";

    // ---- the Puppet server -------------------------------------------------------------------------------------------

    public static string Versions() => Path + "echo \"puppet $(puppet --version 2>/dev/null || echo 'not installed')\"; " +
        "echo \"puppetserver $(puppetserver --version 2>/dev/null | head -n 1 || echo '-')\"; " +
        "(r10k version 2>/dev/null | head -n 1) || true; command -v puppet-code >/dev/null 2>&1 && echo 'puppet-code: present (Puppet Enterprise Code Manager)'; " +
        "command -v puppetdb >/dev/null 2>&1 && echo 'puppetdb: present' || true";

    public static string CaList() => Path + "puppetserver ca list --all";
    public static string CaSign(string certname) => Path + "puppetserver ca sign --certname " + Q(Cert(certname));
    public static string CaRevoke(string certname) => Path + "puppetserver ca revoke --certname " + Q(Cert(certname));
    public static string CaClean(string certname) => Path + "puppetserver ca clean --certname " + Q(Cert(certname));

    public static string Environments() => Path + "ls -1 \"$(puppet config print environmentpath --section server | cut -d: -f1)\"";

    /// <summary>Deploys an environment's code: Code Manager (puppet-code, PE) when present, else r10k.</summary>
    public static string Deploy(string env) => Path +
        $"if command -v puppet-code >/dev/null 2>&1; then puppet-code deploy {Q(Env(env))} --wait; " +
        $"elif command -v r10k >/dev/null 2>&1; then r10k deploy environment {Q(Env(env))} --modules --verbose info; " +
        "else echo 'Neither Code Manager (puppet-code) nor r10k is installed on this server.'; exit 127; fi";

    public static string Lookup(string key, string node, string env) => Path +
        $"puppet lookup {Q(Key(key))} --node {Q(Cert(node))} --environment {Q(Env(env))} --explain --render-as yaml";

    public static string Validate(string path) => Path + "puppet parser validate " + Q(FilePath(path));
    public static string Lint(string path) => Path + "if command -v puppet-lint >/dev/null 2>&1; then puppet-lint " + Q(FilePath(path)) + "; else echo 'puppet-lint is not installed (gem install puppet-lint, or the PDK)'; exit 127; fi";

    // ---- agents ------------------------------------------------------------------------------------------------------

    /// <summary>A run with --detailed-exitcodes (0 nothing to do, 2 changes, 4 failures, 6 both, 1 the run failed).</summary>
    public static string AgentRun(bool noop, string env, string tags)
    {
        var sb = new StringBuilder(Path + "puppet agent --test --detailed-exitcodes --color=false");
        if (noop) sb.Append(" --noop");
        if (env.Trim().Length > 0) sb.Append(" --environment ").Append(Q(Env(env)));
        if (Tags(tags) is { Length: > 0 } t) sb.Append(" --tags ").Append(Q(t));
        return sb.ToString();
    }

    public static string ExitMeaning(int code) => code switch
    {
        0 => "nothing to change",
        2 => "changes applied" ,
        4 => "failures",
        6 => "changes and failures",
        1 => "the run failed (or another run is in progress / the agent is disabled)",
        _ => "exit " + code,
    };

    public static string Disable(string reason)
    {
        reason = reason.Trim();
        if (reason.Length == 0 || reason.Length > 200 || reason.Any(char.IsControl)) throw new ArgumentException("Give a reason (one line, up to 200 characters): it is shown to everyone who tries to run Puppet there.");
        return Path + "puppet agent --disable " + Q(reason);
    }

    public static string Enable() => Path + "puppet agent --enable";
    public static string LastRun() => Path + "f=\"$(puppet config print lastrunfile --section agent)\"; d=\"$(puppet config print agent_disabled_lockfile --section agent)\"; " +
        "[ -f \"$d\" ] && echo \"disabled: $(cat \"$d\")\"; cat \"$f\"";
    public static string Facts() => Path + "puppet facts show --render-as json 2>/dev/null || facter -j";
    public static string Config() => Path + "puppet config print server ca_server certname environment runinterval noop --section agent";

    /// <summary>The last run, from last_run_summary.yaml (two-level YAML: section, then key: value).</summary>
    public sealed record RunSummary(DateTime? LastRun, int Total, int Changed, int Failed, int OutOfSync, string ConfigVersion, string PuppetVersion, string? DisabledReason);

    public static RunSummary ParseLastRun(string text)
    {
        string? section = null; var v = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? disabled = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("disabled: ", StringComparison.Ordinal)) { disabled = DisabledReason(line[10..]); continue; }
            if (line.Length == 0 || line.StartsWith("---")) continue;
            var m = Regex.Match(line, @"^(\s*)([A-Za-z_]+):\s*(.*)$");
            if (!m.Success) continue;
            if (m.Groups[1].Value.Length == 0) { section = m.Groups[2].Value; if (m.Groups[3].Value.Length > 0) v[section] = m.Groups[3].Value.Trim('"', '\''); continue; }
            if (section != null) v[section + "." + m.Groups[2].Value] = m.Groups[3].Value.Trim('"', '\'');
        }
        int I(string k) => int.TryParse(v.GetValueOrDefault(k), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
        DateTime? last = long.TryParse(v.GetValueOrDefault("time.last_run"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch) ? DateTimeOffset.FromUnixTimeSeconds(epoch).LocalDateTime : null;
        return new RunSummary(last, I("resources.total"), I("resources.changed"), I("resources.failed"), I("resources.out_of_sync"),
            v.GetValueOrDefault("version.config") ?? "", v.GetValueOrDefault("version.puppet") ?? "", disabled);
    }

    /// <summary>The agent's lock file is JSON: {"disabled_message":"…"}.</summary>
    private static string DisabledReason(string json)
    {
        try { return JsonDocument.Parse(json).RootElement.GetProperty("disabled_message").GetString() ?? "(no reason)"; }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { return json.Trim(); }
    }

    // ---- the certificate authority -----------------------------------------------------------------------------------

    public sealed record CertEntry(string Certname, string State, string Fingerprint, string AltNames);

    /// <summary>puppetserver ca list --all: sections "Requested Certificates:", "Signed Certificates:", "Revoked
    /// Certificates:", then one indented line per certificate: name, (SHA256) fingerprint, optional alt names.</summary>
    public static List<CertEntry> ParseCaList(string text)
    {
        var list = new List<CertEntry>();
        string state = "";
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var head = Regex.Match(line, @"^\s*(Requested|Signed|Revoked) Certificates:", RegexOptions.IgnoreCase);
            if (head.Success) { state = head.Groups[1].Value.ToLowerInvariant(); continue; }
            if (state.Length == 0 || line.Trim().Length == 0) continue;
            var m = Regex.Match(line, @"^\s+(\S+)\s+\((?:SHA256|SHA1)\)\s+([0-9A-Fa-f:]+)(?:\s+alt names:\s*(.*))?");
            if (m.Success && CertRx.IsMatch(m.Groups[1].Value)) list.Add(new CertEntry(m.Groups[1].Value, state, m.Groups[2].Value, m.Groups[3].Value.Trim().Trim('"')));
        }
        return list;
    }

    // ---- PuppetDB ----------------------------------------------------------------------------------------------------

    /// <summary>A PQL query against PuppetDB on that host (localhost:8081) with the host's own agent certificate. The query
    /// is URL-encoded by curl and shell-quoted here; node names inside it are validated certnames.</summary>
    public static string PdbQuery(string pql) => Path +
        "curl -sS --fail-with-body --max-time 60 --cacert \"$(puppet config print localcacert)\" --cert \"$(puppet config print hostcert)\" --key \"$(puppet config print hostprivkey)\" " +
        "-G https://localhost:8081/pdb/query/v4 --data-urlencode " + Q("query=" + pql);

    public static string PqlNodes() => "nodes[certname, report_timestamp, latest_report_status, latest_report_noop, catalog_environment] { order by certname }";
    public static string PqlFailed() => "nodes[certname, report_timestamp, catalog_environment] { latest_report_status = \"failed\" order by certname }";
    public static string PqlFacts(string node) => $"facts[name, value] {{ certname = \"{Cert(node)}\" order by name }}";
    public static string PqlReports(string node) => $"reports[receive_time, status, noop, environment, configuration_version] {{ certname = \"{Cert(node)}\" order by receive_time desc limit 20 }}";
    public static string PqlEvents(string node) => $"events[timestamp, resource_type, resource_title, property, status, message] {{ certname = \"{Cert(node)}\" and latest_report? = true order by timestamp }}";

    /// <summary>A PuppetDB answer (a JSON array of objects) as rows of column → text, for a grid. Values that are objects or
    /// arrays are shown as compact JSON, cut at 400 characters.</summary>
    public static (List<string> Columns, List<Dictionary<string, string>> Rows) ParseRows(string json, int max = 5000)
    {
        var cols = new List<string>(); var rows = new List<Dictionary<string, string>>();
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("PuppetDB didn't answer with a list: " + Cut(json, 200));
        foreach (var o in doc.RootElement.EnumerateArray().Take(max))
        {
            if (o.ValueKind != JsonValueKind.Object) continue;
            var row = new Dictionary<string, string>();
            foreach (var p in o.EnumerateObject())
            {
                if (!cols.Contains(p.Name)) cols.Add(p.Name);
                row[p.Name] = p.Value.ValueKind switch
                {
                    JsonValueKind.String => p.Value.GetString() ?? "",
                    JsonValueKind.Null => "",
                    JsonValueKind.Object or JsonValueKind.Array => Cut(p.Value.GetRawText(), 400),
                    _ => p.Value.GetRawText(),
                };
            }
            rows.Add(row);
        }
        return (cols, rows);
    }

    private static string Cut(string s, int n) => s.Length > n ? s[..n] + "…" : s;

    // ---- Bolt (on a control node, like Ansible) -------------------------------------------------------------------------

    private static readonly Regex TaskRx = new(@"^[a-z][a-z0-9_]*(::[a-z][a-z0-9_]*)*$", RegexOptions.CultureInvariant);
    private static readonly Regex ParamRx = new(@"^[a-z][a-z0-9_]{0,63}=[^\r\n]{0,500}$", RegexOptions.CultureInvariant);

    public static string BoltTask(string t) { t = t.Trim(); if (!TaskRx.IsMatch(t)) throw new ArgumentException("A task or plan name is like package or facts::info."); return t; }

    /// <summary>The Bolt inventory for the ticked hosts: SSH targets with their user and port; host keys are checked.</summary>
    public static string BoltInventory(IEnumerable<HostEntry> hosts)
    {
        var sb = new StringBuilder("targets:\n");
        foreach (var h in hosts)
        {
            if (!Regex.IsMatch(h.Address, @"^[A-Za-z0-9.\-:\[\]]{1,253}$")) continue;
            sb.Append("  - name: ").Append(Q(h.Display)).Append('\n');
            sb.Append("    uri: ").Append(Q($"ssh://{h.Address}:{h.Port}")).Append('\n');
            sb.Append("    config:\n      ssh:\n        user: ").Append(Q(h.Username)).Append("\n        host-key-check: true\n");
        }
        return sb.ToString();
    }

    /// <summary>bolt command / task / plan run over the inventory in $d (the Ansible bootstrap writes it there).</summary>
    public static string Bolt(string kind, string what, IEnumerable<string> parameters, bool runAsRoot)
    {
        var sb = new StringBuilder("if ! command -v bolt >/dev/null 2>&1; then echo 'Puppet Bolt is not installed on this control node (https://www.puppet.com/docs/bolt)'; exit 127; fi; ");
        sb.Append("bolt --inventoryfile \"$d/inventory.yml\" --no-color --format human ");
        switch (kind)
        {
            case "command":
                if (what.Trim().Length == 0 || what.Length > 2000 || what.Any(c => c is '\r' or '\n')) throw new ArgumentException("A command is one line.");
                sb.Append("command run ").Append(Q(what.Trim())).Append(" --targets all");
                break;
            case "task": sb.Append("task run ").Append(BoltTask(what)).Append(" --targets all"); break;
            case "plan": sb.Append("plan run ").Append(BoltTask(what)).Append(" targets=all"); break;
            default: throw new ArgumentException("kind");
        }
        foreach (var p in parameters.Select(x => x.Trim()).Where(x => x.Length > 0))
        {
            if (!ParamRx.IsMatch(p)) throw new ArgumentException($"A parameter is name=value (lower-case name): “{p}”.");
            sb.Append(' ').Append(Q(p));
        }
        if (runAsRoot) sb.Append(" --run-as root");
        return sb.ToString();
    }
}
