using System.Windows;
using System.Windows.Controls;
using DcxManager.Ansible;
using DcxManager.Puppet;
using DcxManager.Services;
using DcxManager.Sessions;
using DcxManager.Ssh;

namespace DcxManager.Views;

/// <summary>
/// Puppet (0.5.0, ADR-063; DarkCipher0x: "Puppet integration and management, fully mature"): the certificate authority on the
/// Puppet server (list, sign, revoke, clean), agents on the saved hosts (dry runs first, real runs, enable / disable with a
/// reason, last run, facts, config), code (environments, r10k / Code Manager deploys, Hiera lookups, parser validate,
/// puppet-lint), PuppetDB (nodes, failed runs, facts, reports, events — queried on the PuppetDB host with its own
/// certificate) and Bolt (commands, tasks, plans on a control node). Read-only until an administrator allows changes; every
/// change asks first (typing PRODUCTION for production hosts), is written to the audit log and shows its output masked.
/// </summary>
public sealed class PuppetView : UserControl, IDisposable
{
    private readonly ComboBox _server = new() { Width = 260, ToolTip = "The saved host that is your Puppet server (primary): certificates, environments, deploys and lookups run there over SSH" };
    private readonly ComboBox _pdb = new() { Width = 260, ToolTip = "The saved host that runs PuppetDB (often the Puppet server itself)" };
    private readonly ComboBox _node = new() { Width = 260, ToolTip = "Bolt's control node: a WSL distribution here or a saved Linux host with Bolt installed" };
    private readonly ListBox _agents = new() { Height = 140, SelectionMode = SelectionMode.Multiple };
    private readonly ListView _certs = new() { Height = 170 };
    private readonly TextBox _env = Box(140, "", "--environment (empty = the agent's own)");
    private readonly TextBox _tags = Box(200, "", "--tags: classes or resources to apply only (comma-separated)");
    private readonly CheckBox _noop = new() { Content = "Dry run (--noop)", IsChecked = true, ToolTip = "Reports what would change without changing anything. Untick for a real run (needs Puppet changes allowed)." };
    private readonly ComboBox _envs = new() { Width = 180, IsEditable = true, ToolTip = "Code environment (Get environments fills the list)" };
    private readonly TextBox _key = Box(220, "", "Hiera key, e.g. profile::web::port");
    private readonly TextBox _lookupNode = Box(220, "", "Node (certname) the lookup is for");
    private readonly TextBox _file = Box(380, "", "A manifest on the Puppet server, e.g. /etc/puppetlabs/code/environments/production/manifests/site.pp");
    private readonly TextBox _pdbNode = Box(220, "", "Node (certname) for facts, reports and events");
    private readonly ComboBox _boltKind = new() { Width = 100, ItemsSource = new[] { "command", "task", "plan" }, SelectedIndex = 0 };
    private readonly TextBox _boltWhat = Box(280, "uptime", "The command, or a task / plan name (package, facts, mymodule::deploy)");
    private readonly TextBox _boltParams = Box(260, "", "Task / plan parameters: name=value, one per line or space-separated");
    private readonly CheckBox _boltRoot = new() { Content = "--run-as root" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6) };
    private readonly TextBlock _mode = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
    private readonly TextBox _out = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly DataGrid _grid = new() { IsReadOnly = true, AutoGenerateColumns = false, Height = 220, Visibility = Visibility.Collapsed, HeadersVisibility = DataGridHeadersVisibility.Column };
    private readonly List<Button> _buttons = new();
    private CancellationTokenSource? _cts;

    /// <summary>The self-test replaces SSH: (host, command, sudo) → result.</summary>
    internal Func<HostEntry, string, bool, Task<ExecResult>>? ExecForTest { get; set; }
    /// <summary>The self-test answers confirmations here.</summary>
    internal Func<string, bool>? ConfirmForTest { get; set; }

    public PuppetView()
    {
        var root = new DockPanel { Margin = new Thickness(14, 12, 14, 12) };
        var head = new StackPanel();
        head.Children.Add(new TextBlock { Text = "Puppet", Style = (Style)FindResource("H1") });
        head.Children.Add(new TextBlock { Text = "Your Puppet server, agents, code and PuppetDB over SSH from the saved hosts, and Bolt on a control node. Sudo passwords come from the vault on stdin; PuppetDB is queried on its own host with that host's certificate, so no Puppet key is copied here.", Style = (Style)FindResource("Hint"), Margin = new Thickness(0, 4, 0, 6), TextWrapping = TextWrapping.Wrap });
        head.Children.Add(_mode);
        head.Children.Add(Bar(Label("puppet server"), _server, Label("puppetdb"), _pdb, Run("ⓘ Versions", () => OnServer("Versions", PuppetCommands.Versions(), sudo: false, change: false)), Stop()));

        var tabs = new TabControl { Margin = new Thickness(0, 4, 0, 6) };
        // Certificates
        var gv = new GridView();
        void Col(string h, string p, double w) => gv.Columns.Add(new GridViewColumn { Header = h, Width = w, DisplayMemberBinding = new System.Windows.Data.Binding(p) });
        Col("Certname", nameof(PuppetCommands.CertEntry.Certname), 260); Col("State", nameof(PuppetCommands.CertEntry.State), 90); Col("Fingerprint (SHA-256)", nameof(PuppetCommands.CertEntry.Fingerprint), 330); Col("Alt names", nameof(PuppetCommands.CertEntry.AltNames), 200);
        _certs.View = gv; _certs.SelectionMode = SelectionMode.Extended;
        var certTab = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        certTab.Children.Add(Bar(Run("↻ List certificates", ListCertsAsync, primary: true),
            Run("✓ Sign selected", () => CertAction("sign", PuppetCommands.CaSign), tip: "Compare the fingerprint with `puppet ssl verify` / the agent's output first"),
            Run("⊘ Revoke selected", () => CertAction("revoke", PuppetCommands.CaRevoke)),
            Run("✕ Clean selected…", () => CertAction("clean", PuppetCommands.CaClean), tip: "Revokes and deletes the certificate (the node must request a new one)")));
        certTab.Children.Add(_certs);
        tabs.Items.Add(new TabItem { Header = "Certificates", Content = certTab });
        // Agents
        var agentTab = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        agentTab.Children.Add(Label("agents (select the hosts)"));
        agentTab.Children.Add(_agents);
        agentTab.Children.Add(Bar(_noop, Label("environment"), _env, Label("tags"), _tags));
        agentTab.Children.Add(Bar(Run("▶ Run Puppet", AgentRunAsync, primary: true),
            Run("☷ Last run", () => OnAgents("Last run", PuppetCommands.LastRun(), change: false, summary: true)),
            Run("Facts", () => OnAgents("Facts", PuppetCommands.Facts(), change: false)),
            Run("Config", () => OnAgents("Config", PuppetCommands.Config(), change: false)),
            Run("⏸ Disable…", DisableAsync), Run("▶ Enable", () => OnAgents("Enable agent", PuppetCommands.Enable(), change: true))));
        tabs.Items.Add(new TabItem { Header = "Agents", Content = agentTab });
        // Code & Hiera
        var codeTab = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        codeTab.Children.Add(Bar(Label("environment"), _envs, Run("Get environments", EnvironmentsAsync), Run("⇪ Deploy code…", DeployAsync, tip: "r10k, or Code Manager (puppet-code) on Puppet Enterprise")));
        codeTab.Children.Add(Bar(Label("hiera key"), _key, Label("for node"), _lookupNode, Run("⌕ Lookup (--explain)", () => OnServer("Lookup", PuppetCommands.Lookup(_key.Text, _lookupNode.Text, EnvText()), sudo: true, change: false))));
        codeTab.Children.Add(Bar(Label("manifest"), _file, Run("Validate", () => OnServer("Parser validate", PuppetCommands.Validate(_file.Text), sudo: false, change: false)), Run("Lint", () => OnServer("puppet-lint", PuppetCommands.Lint(_file.Text), sudo: false, change: false))));
        tabs.Items.Add(new TabItem { Header = "Code & Hiera", Content = codeTab });
        // PuppetDB
        var pdbTab = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        pdbTab.Children.Add(Bar(Run("Nodes", () => PdbAsync("Nodes", PuppetCommands.PqlNodes()), primary: true), Run("Failed last run", () => PdbAsync("Failed nodes", PuppetCommands.PqlFailed())),
            Label("node"), _pdbNode, Run("Facts", () => PdbAsync("Facts", PuppetCommands.PqlFacts(_pdbNode.Text))), Run("Reports", () => PdbAsync("Reports", PuppetCommands.PqlReports(_pdbNode.Text))), Run("Events (latest)", () => PdbAsync("Events", PuppetCommands.PqlEvents(_pdbNode.Text)))));
        pdbTab.Children.Add(_grid);
        tabs.Items.Add(new TabItem { Header = "PuppetDB", Content = pdbTab });
        // Bolt
        var boltTab = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        boltTab.Children.Add(Bar(Label("control node"), _node));
        boltTab.Children.Add(Bar(_boltKind, _boltWhat, Label("parameters"), _boltParams, _boltRoot, Run("▶ Run with Bolt", BoltAsync, primary: true, tip: "Targets: the hosts selected on the Agents tab. Bolt connects with the control node's own SSH keys / agent and checks host keys.")));
        tabs.Items.Add(new TabItem { Header = "Bolt", Content = boltTab });
        head.Children.Add(tabs);
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        head.Children.Add(_status);
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);
        _out.Mono();
        root.Children.Add(_out);
        Content = root;

        Fill();
        _server.SelectionChanged += (_, _) => { if (Pick(_server) is { } h) { AppSettings.Current.PuppetServerHostId = h.Id; Save(); } };
        _pdb.SelectionChanged += (_, _) => { if (Pick(_pdb) is { } h) { AppSettings.Current.PuppetDbHostId = h.Id; Save(); } };
        Live.While(this, h => { AppData.Changed += h; AppSettings.Saved += h; }, h => { AppData.Changed -= h; AppSettings.Saved -= h; }, () => { Fill(); ShowMode(); });
        ShowMode();
    }

    private static void Save() { try { AppSettings.Save(); } catch (Exception ex) { App.Log("Puppet settings", ex); } }

    private void ShowMode()
    {
        bool on = AppSettings.Current.PuppetAllowChanges;
        _mode.Text = on ? "Changes are allowed: signing, runs without --noop, deploys and Bolt ask first, then act." : "🔒 Read-only: listing, dry runs, lookups, validation and PuppetDB queries. An administrator allows changes in Settings → Integrations → Automation.";
        _mode.SetResourceReference(TextBlock.ForegroundProperty, on ? "Fg" : "Warn");
    }

    /// <summary>The saved Linux SSH hosts, in the server / PuppetDB pickers, the agent list and Bolt's control nodes; keeps
    /// what was picked and ticked.</summary>
    private void Fill()
    {
        var linux = AppData.Current.Hosts.Where(h => h.Protocol == "ssh" && !h.IsAdHoc && h.Platform != "windows" && h.Address.Length > 0)
            .OrderBy(h => h.Group).ThenBy(h => h.Display, StringComparer.OrdinalIgnoreCase).ToList();
        void Combo(ComboBox c, string wantId)
        {
            var was = Pick(c)?.Id ?? wantId;
            c.Items.Clear();
            foreach (var h in linux) c.Items.Add(new ComboBoxItem { Content = h.Display + "  ·  " + h.Address, Tag = h });
            c.SelectedItem = c.Items.OfType<ComboBoxItem>().FirstOrDefault(i => ((HostEntry)i.Tag).Id == was);
        }
        Combo(_server, AppSettings.Current.PuppetServerHostId);
        Combo(_pdb, AppSettings.Current.PuppetDbHostId.Length > 0 ? AppSettings.Current.PuppetDbHostId : AppSettings.Current.PuppetServerHostId);
        var ticked = _agents.SelectedItems.OfType<ListBoxItem>().Select(i => ((HostEntry)i.Tag).Id).ToHashSet();
        _agents.Items.Clear();
        foreach (var h in linux)
            _agents.Items.Add(new ListBoxItem { Content = $"{h.Group} / {h.Display}  ·  {h.Username}@{h.Address}" + (Envs.IsProduction(h.Environment) ? "  ⚠ PRODUCTION" : ""), Tag = h, IsSelected = ticked.Contains(h.Id) });
        var node = (_node.SelectedItem as ComboBoxItem)?.Content as string;
        _node.Items.Clear();
        foreach (var d in LocalShells.WslDistributions()) _node.Items.Add(new ComboBoxItem { Content = $"WSL · {d.Name}", Tag = new WslAnsibleRunner(d.Name) });
        foreach (var h in linux) _node.Items.Add(new ComboBoxItem { Content = "host · " + h.Display, Tag = new SshAnsibleRunner(h) });
        _node.SelectedItem = _node.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Content == node) ?? (_node.Items.Count > 0 ? _node.Items[0] : null);
    }

    private static HostEntry? Pick(ComboBox c) => (c.SelectedItem as ComboBoxItem)?.Tag as HostEntry;
    private HostEntry Server => Pick(_server) ?? throw new InvalidOperationException("Pick the Puppet server (a saved Linux host) first.");
    private HostEntry PdbHost => Pick(_pdb) ?? Pick(_server) ?? throw new InvalidOperationException("Pick the PuppetDB host (or the Puppet server) first.");
    private List<HostEntry> Agents => _agents.SelectedItems.OfType<ListBoxItem>().Select(i => (HostEntry)i.Tag).ToList();
    private string EnvText() => (_envs.Text ?? "").Trim() is { Length: > 0 } e ? e : "production";

    // ---- running ------------------------------------------------------------------------------------------------------

    private Task<ExecResult> Exec(HostEntry h, string command, bool sudo, CancellationToken ct) =>
        ExecForTest != null ? ExecForTest(h, command, sudo) : SshPool.RunAsync(h, command, sudo, ct, TimeSpan.FromMinutes(30));

    private bool Confirm(string title, string text, IReadOnlyCollection<HostEntry> hosts)
    {
        if (ConfirmForTest != null) return ConfirmForTest(title);
        return Envs.ConfirmMany(hosts.ToList(), "Puppet", text, title);
    }

    private void RequireChanges(string what)
    {
        if (!AppSettings.Current.PuppetAllowChanges) throw new InvalidOperationException($"{what} changes things, and Puppet is read-only on this PC. An administrator can allow changes in Settings → Integrations → Automation.");
        if (Offline.On) { /* every target is checked by the SSH connector itself */ }
    }

    private async Task OnServer(string title, string command, bool sudo, bool change, Func<string, string>? show = null)
    {
        var h = Server;
        if (change) { RequireChanges(title); if (!Confirm(title, $"{title} on the Puppet server {h.Display}?", new[] { h })) return; }
        Begin(title, h.Display);
        var r = await Exec(h, command, sudo, _cts!.Token);
        Append(show != null ? show(r.Combined) : Redaction.Mask(r.Combined));
        End(title, r.ExitCode, change ? "on " + h.Display : null);
    }

    private async Task ListCertsAsync()
    {
        var h = Server;
        Begin("Certificates", h.Display);
        var r = await Exec(h, PuppetCommands.CaList(), sudo: true, _cts!.Token);
        var list = PuppetCommands.ParseCaList(r.Output);
        _certs.ItemsSource = list.OrderBy(c => c.State != "requested").ThenBy(c => c.Certname).ToList();
        Append(Redaction.Mask(r.Combined));
        _status.Text = r.Ok ? $"{list.Count(c => c.State == "requested")} waiting to be signed, {list.Count(c => c.State == "signed")} signed, {list.Count(c => c.State == "revoked")} revoked." : $"✕ exit {r.ExitCode}: see the output (sudo rights on the server?).";
    }

    private async Task CertAction(string verb, Func<string, string> build)
    {
        var picked = _certs.SelectedItems.OfType<PuppetCommands.CertEntry>().ToList();
        if (picked.Count == 0) throw new InvalidOperationException("Select certificates in the list first (↻ List certificates).");
        RequireChanges(verb + " certificates");
        var h = Server;
        var names = string.Join("\n", picked.Select(c => $"{c.Certname}  {c.Fingerprint}"));
        bool ok = ConfirmForTest?.Invoke(verb) ?? (verb == "clean"
            ? Dialogs.Confirm("Clean certificates", $"Revoke and delete these certificates on {h.Display}? The nodes will have to request new ones.\n\n{names}", "Clean", danger: true, typeToConfirm: picked.Count == 1 ? picked[0].Certname : "CLEAN")
            : Dialogs.Confirm($"{char.ToUpper(verb[0])}{verb[1..]} certificates", $"{char.ToUpper(verb[0])}{verb[1..]} on {h.Display}? Compare each fingerprint with the node's own (puppet ssl verify / the agent's first run):\n\n{names}", verb == "sign" ? "Sign" : "Revoke", danger: verb != "sign"));
        if (!ok) return;
        Begin(verb + " certificates", h.Display);
        foreach (var c in picked)
        {
            var r = await Exec(h, build(c.Certname), sudo: true, _cts!.Token);
            Append($"[{c.Certname}] " + Redaction.Mask(r.Combined));
            AuditLog.Write("you", h.Display, $"puppet certificate {verb}", $"{c.Certname} {c.Fingerprint} (exit {r.ExitCode})");
        }
        await ListCertsAsync();
    }

    private async Task AgentRunAsync()
    {
        bool noop = _noop.IsChecked == true;
        var cmd = PuppetCommands.AgentRun(noop, _env.Text, _tags.Text);
        await OnAgents(noop ? "Puppet dry run" : "Puppet run", cmd, change: !noop, exitMeaning: true);
    }

    private async Task DisableAsync()
    {
        var reason = ConfirmForTest != null ? "self-test maintenance" : Dialogs.Prompt("Disable Puppet", "Why? (shown to anyone who tries to run Puppet on these hosts):");
        if (reason == null) return;
        await OnAgents("Disable agent", PuppetCommands.Disable(reason), change: true);
    }

    /// <summary>Runs on every selected agent, four at a time; each line is prefixed with the host.</summary>
    private async Task OnAgents(string title, string command, bool change, bool summary = false, bool exitMeaning = false)
    {
        var hosts = Agents;
        if (hosts.Count == 0) throw new InvalidOperationException("Select the agents (hosts) first.");
        if (change) { RequireChanges(title); if (!Confirm(title, $"{title} on {hosts.Count} host(s)? This changes them.", hosts)) return; }
        Begin(title, $"{hosts.Count} host(s)");
        using var gate = new SemaphoreSlim(4);
        var ct = _cts!.Token;
        var results = await Task.WhenAll(hosts.Select(async h =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var r = await Exec(h, command, sudo: true, ct);
                var text = summary && r.Ok ? Summary(PuppetCommands.ParseLastRun(r.Output)) : Redaction.Mask(r.Combined);
                await Dispatcher.InvokeAsync(() => Append($"── {h.Display} ── " + (exitMeaning ? PuppetCommands.ExitMeaning(r.ExitCode) : $"exit {r.ExitCode}") + "\n" + text));
                if (change) AuditLog.Write("you", h.Display, "puppet: " + title, $"exit {r.ExitCode}");
                return r.ExitCode;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { await Dispatcher.InvokeAsync(() => Append($"── {h.Display} ── ✕ {ex.Message}")); return -1; }
            finally { gate.Release(); }
        }));
        _status.Text = $"{title}: {results.Count(c => c is 0 or 2)} of {hosts.Count} fine" + (results.Any(c => c is 4 or 6 or 1 or -1) ? $", {results.Count(c => c is 4 or 6 or 1 or -1)} with failures — see the output." : ".");
    }

    private static string Summary(PuppetCommands.RunSummary s) =>
        (s.DisabledReason != null ? $"⏸ disabled: {s.DisabledReason}\n" : "") +
        $"last run {s.LastRun:yyyy-MM-dd HH:mm} · {s.Total} resources · {s.Changed} changed · {s.Failed} failed · {s.OutOfSync} out of sync · config {s.ConfigVersion} · puppet {s.PuppetVersion}";

    private async Task EnvironmentsAsync()
    {
        var h = Server;
        Begin("Environments", h.Display);
        var r = await Exec(h, PuppetCommands.Environments(), sudo: false, _cts!.Token);
        var envs = r.Output.Split('\n').Select(x => x.Trim()).Where(x => System.Text.RegularExpressions.Regex.IsMatch(x, "^[a-z0-9_]{1,64}$")).ToList();
        _envs.ItemsSource = envs;
        if (envs.Count > 0 && string.IsNullOrEmpty(_envs.Text)) _envs.Text = envs.Contains("production") ? "production" : envs[0];
        Append(Redaction.Mask(r.Combined));
        End("Environments", r.ExitCode, null);
    }

    private async Task DeployAsync()
    {
        var env = PuppetCommands.Env(EnvText());
        RequireChanges("Deploying code");
        var h = Server;
        bool ok = ConfirmForTest?.Invoke("deploy") ?? Dialogs.Confirm("Deploy code", $"Deploy the “{env}” environment's code on {h.Display}? Agents in it pick it up on their next run.", "Deploy", danger: env == "production", typeToConfirm: env == "production" ? "production" : null);
        if (!ok) return;
        await OnServer("Deploy " + env, PuppetCommands.Deploy(env), sudo: true, change: false);
        AuditLog.Write("you", h.Display, "puppet code deployed", env);
    }

    private async Task PdbAsync(string title, string pql)
    {
        var h = PdbHost;
        Begin("PuppetDB: " + title, h.Display);
        var r = await Exec(h, PuppetCommands.PdbQuery(pql), sudo: true, _cts!.Token);
        if (!r.Ok) { Append(Redaction.Mask(r.Combined)); End("PuppetDB", r.ExitCode, null); _grid.Visibility = Visibility.Collapsed; return; }
        var (cols, rows) = PuppetCommands.ParseRows(r.Output);
        _grid.Columns.Clear();
        foreach (var c in cols) _grid.Columns.Add(new DataGridTextColumn { Header = c, Binding = new System.Windows.Data.Binding($"[{c}]") });
        _grid.ItemsSource = rows;
        _grid.Visibility = Visibility.Visible;
        _status.Text = $"PuppetDB {title}: {rows.Count} row(s).";
    }

    private async Task BoltAsync()
    {
        var targets = Agents;
        if (targets.Count == 0) throw new InvalidOperationException("Select the target hosts on the Agents tab first.");
        RequireChanges("A Bolt run");
        var runner = (_node.SelectedItem as ComboBoxItem)?.Tag as IAnsibleRunner ?? throw new InvalidOperationException("Pick Bolt's control node.");
        var kind = _boltKind.SelectedItem as string ?? "command";
        var cmd = PuppetCommands.Bolt(kind, _boltWhat.Text, _boltParams.Text.Split(new[] { '\n', ' ' }, StringSplitOptions.RemoveEmptyEntries), _boltRoot.IsChecked == true);
        if (!Confirm("Run", $"Bolt {kind} “{_boltWhat.Text.Trim()}” on {targets.Count} host(s)?", targets)) return;
        Begin("Bolt " + kind, runner.Name);
        var job = AnsibleCommands.Make("Bolt " + kind, cmd, PuppetCommands.BoltInventory(targets), changes: true, requireAnsible: false);
        AuditLog.Write("you", "puppet", "bolt " + kind, $"{runner.Name}; {targets.Count} target(s)");
        var code = await runner.RunAsync(job, line => Dispatcher.BeginInvoke(() => Append(Redaction.Mask(line))), _cts!.Token);
        End("Bolt " + kind, code, runner.Name);
    }

    // ---- plumbing -----------------------------------------------------------------------------------------------------

    private void Begin(string title, string where)
    {
        _cts?.Cancel(); _cts = new CancellationTokenSource();
        _out.Text = $"$ {title}  —  {where}\n";
        _status.Text = $"{title} on {where}…";
        AuditLog.Write("you", "puppet", title, where);
    }

    private void End(string title, int code, string? changed) =>
        _status.Text = code == 0 ? $"{title}: done." + (changed != null ? " (" + changed + ")" : "") : code == 127 ? $"{title}: the tool isn't installed there — see the output." : $"{title}: exit {code} — see the output.";

    private void Append(string s)
    {
        _out.AppendText(s + "\n");
        if (_out.Text.Length > 400_000) _out.Text = _out.Text[^300_000..];
        _out.ScrollToEnd();
    }

    private static TextBox Box(double w, string text, string tip) => new() { Width = w, Padding = new Thickness(5, 4, 5, 4), Text = text, ToolTip = tip };
    private static TextBlock Label(string t) => new() { Text = t, Style = (Style)Application.Current.Resources["Label"], VerticalAlignment = VerticalAlignment.Center };
    private static WrapPanel Bar(params UIElement[] items) { var w = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) }; foreach (var i in items) { if (i is FrameworkElement f) { f.Margin = new Thickness(0, 0, 8, 4); f.VerticalAlignment = VerticalAlignment.Center; } w.Children.Add(i); } return w; }

    private Button Stop() { var b = new Button { Content = "■ Stop" }; b.Click += (_, _) => _cts?.Cancel(); return b; }

    private Button Run(string t, Func<Task> a, bool primary = false, string? tip = null)
    {
        var b = new Button { Content = t, ToolTip = tip };
        if (primary) b.Style = (Style)FindResource("PrimaryButton");
        b.Click += async (_, _) =>
        {
            foreach (var x in _buttons) x.IsEnabled = false;
            try { await a(); }
            catch (OperationCanceledException) { _status.Text = "Stopped."; }
            catch (Exception ex) { _status.Text = "✕ " + ex.Message; }
            finally { foreach (var x in _buttons) x.IsEnabled = true; }
        };
        _buttons.Add(b);
        return b;
    }

    public void Dispose() => _cts?.Cancel();

    // ---- self-test seams ----------------------------------------------------------------------------------------------
    internal string OutputForTest => _out.Text;
    internal string StatusForTest => _status.Text;
    internal IReadOnlyList<PuppetCommands.CertEntry> CertsForTest => (_certs.ItemsSource as IReadOnlyList<PuppetCommands.CertEntry>) ?? Array.Empty<PuppetCommands.CertEntry>();
    internal void UseForTest(HostEntry server, IEnumerable<HostEntry> agents)
    {
        _server.SelectedItem = _server.Items.OfType<ComboBoxItem>().FirstOrDefault(i => ((HostEntry)i.Tag).Id == server.Id);
        var ids = agents.Select(a => a.Id).ToHashSet();
        foreach (var i in _agents.Items.OfType<ListBoxItem>()) i.IsSelected = ids.Contains(((HostEntry)i.Tag).Id);
    }
    internal void SelectCertsForTest(Func<PuppetCommands.CertEntry, bool> pick) { _certs.SelectedItems.Clear(); foreach (var c in CertsForTest.Where(pick)) _certs.SelectedItems.Add(c); }
    internal void SetNoopForTest(bool on) => _noop.IsChecked = on;
    internal Task ListCertsForTest() => ListCertsAsync();
    internal Task SignForTest() => CertAction("sign", PuppetCommands.CaSign);
    internal Task AgentRunForTest() => AgentRunAsync();
    internal Task LastRunForTest() => OnAgents("Last run", PuppetCommands.LastRun(), change: false, summary: true);
}
