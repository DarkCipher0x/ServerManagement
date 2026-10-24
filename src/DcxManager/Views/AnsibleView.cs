using System.Windows;
using System.Windows.Controls;
using DcxManager.Ansible;
using DcxManager.Services;
using DcxManager.Sessions;

namespace DcxManager.Views;

/// <summary>
/// Ansible (0.3.0 §16): a control node (a WSL distribution on this PC, or a saved Linux host), an inventory generated
/// from the saved hosts you tick, and the everyday commands — ping, facts, ad-hoc modules, playbooks (check / diff /
/// limit / tags / extra vars / become), ansible-lint, ansible-vault, ansible-galaxy, the inventory graph, ansible-doc,
/// ansible-config and the version check. Output streams in, masked. Anything that changes servers asks first (PRODUCTION
/// hosts in the run mean typing PRODUCTION); vault and become passwords go to the control node on stdin only.
/// </summary>
public sealed class AnsibleView : UserControl, IDisposable
{
    private readonly ComboBox _node = new() { Width = 260, ToolTip = "Where ansible runs: a WSL distribution on this PC or a saved Linux host with Ansible installed" };
    private readonly ListBox _hosts = new() { Height = 150, SelectionMode = SelectionMode.Multiple };
    private readonly TextBox _pattern = Box(160, "all", "Host pattern: all, a group (env_production, folder_lab), a name, web*:!db");
    private readonly TextBox _module = Box(170, "ping", "Module for an ad-hoc run, e.g. ping, setup, shell, ansible.builtin.copy");
    private readonly TextBox _args = Box(260, "", "Module arguments, e.g. uptime  or  src=/etc/hosts dest=/tmp/hosts");
    private readonly TextBox _playbook = Box(280, "~/playbooks/site.yml", "Path on the control node");
    private readonly TextBox _limit = Box(120, "", "--limit");
    private readonly TextBox _tags = Box(120, "", "--tags");
    private readonly TextBox _extra = Box(200, "", "--extra-vars, e.g. version=1.2 or a JSON object");
    private readonly TextBox _skipTags = Box(110, "", "--skip-tags");
    private readonly TextBox _startAt = Box(170, "", "--start-at-task: the task name to begin at");
    private readonly TextBox _forks = Box(50, "", "--forks: parallel hosts (empty = Ansible's default, 5)");
    private readonly CheckBox _check = new() { Content = "--check", IsChecked = true, ToolTip = "Dry run: report what would change" };
    private readonly CheckBox _diff = new() { Content = "--diff", IsChecked = true };
    private readonly CheckBox _become = new() { Content = "become (sudo)", ToolTip = "Asks for the become password at run time (sent on stdin, never stored)" };
    private readonly CheckBox _vault = new() { Content = "vault password", ToolTip = "Asks for the vault password at run time (sent on stdin, never stored)" };
    private readonly ComboBox _verbosity = new() { Width = 70 };
    private readonly TextBox _file = Box(280, "", "File on the control node (a vault file, requirements.yml)");
    private readonly ComboBox _vaultOp = new() { Width = 110 };
    private readonly TextBox _doc = Box(170, "ansible.builtin.copy", "Module to document");
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6) };
    private readonly TextBox _out = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
    private CancellationTokenSource? _cts;
    private readonly List<Button> _buttons = new();

    /// <summary>The self-test replaces the runner so nothing real runs.</summary>
    internal Func<IAnsibleRunner?>? RunnerForTest { get; set; }
    internal Func<string, string?>? PromptForTest { get; set; }
    /// <summary>The self-test answers the PRODUCTION confirmation here instead of reaching a dialog.</summary>
    internal Func<IReadOnlyCollection<HostEntry>, bool>? ConfirmForTest { get; set; }

    private List<string> _wsl = new();

    /// <summary>The control nodes: WSL distributions (read once) and the saved Linux SSH hosts; keeps the current pick.</summary>
    private void FillNodes()
    {
        var was = (_node.SelectedItem as ComboBoxItem)?.Content as string;
        _node.Items.Clear();
        foreach (var d in _wsl) _node.Items.Add(new ComboBoxItem { Content = $"WSL · {d}", Tag = new WslAnsibleRunner(d) });
        foreach (var h in AppData.Current.Hosts.Where(h => h.Protocol == "ssh" && !h.IsAdHoc && h.Platform != "windows").OrderBy(h => h.Display, StringComparer.OrdinalIgnoreCase))
            _node.Items.Add(new ComboBoxItem { Content = "host · " + h.Display, Tag = new SshAnsibleRunner(h) });
        _node.SelectedItem = _node.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Content == was) ?? (_node.Items.Count > 0 ? _node.Items[0] : null);
    }

    /// <summary>The inventory list; a host keeps its tick across a refresh, a new host starts ticked.</summary>
    private void FillHosts()
    {
        var known = _hosts.Items.OfType<ListBoxItem>().ToDictionary(i => ((HostEntry)i.Tag).Id, i => i.IsSelected);
        _hosts.Items.Clear();
        foreach (var h in AppData.Current.Hosts.Where(h => h.Protocol == "ssh" && !h.IsAdHoc && h.Address.Length > 0).OrderBy(h => h.Group).ThenBy(h => h.Display, StringComparer.OrdinalIgnoreCase))
            _hosts.Items.Add(new ListBoxItem { Content = $"{h.Group} / {h.Display}  ·  {h.Username}:{h.Port}" + (Envs.IsProduction(h.Environment) ? "  ⚠ PRODUCTION" : ""), Tag = h, IsSelected = known.TryGetValue(h.Id, out var on) ? on : true });
    }

    public AnsibleView()
    {
        var root = new DockPanel { Margin = new Thickness(14, 12, 14, 12) };
        var head = new StackPanel();
        head.Children.Add(new TextBlock { Text = "Ansible", Style = (Style)FindResource("H1") });
        head.Children.Add(new TextBlock { Text = "Runs on a control node (Ansible doesn't run on Windows): a WSL distribution here, or a saved Linux host. The inventory is generated from the hosts you tick and written to a private temp folder on the node for the run only. Passwords travel on stdin, never on a command line or disk.", Style = (Style)FindResource("Hint"), Margin = new Thickness(0, 4, 0, 8), TextWrapping = TextWrapping.Wrap });
        _wsl = LocalShells.WslDistributions().Select(d => d.Name).ToList();
        FillNodes();
        foreach (var v in new[] { "quiet", "-v", "-vv", "-vvv" }) _verbosity.Items.Add(v);
        _verbosity.SelectedIndex = 0;
        foreach (var op in new[] { "view", "encrypt", "decrypt", "rekey", "edit-check" }) _vaultOp.Items.Add(op);
        _vaultOp.SelectedIndex = 0;
        var stop = Btn("■ Stop"); stop.Click += (_, _) => _cts?.Cancel();
        head.Children.Add(Bar(Label("control node"), _node, Run("ⓘ Version", () => Go("Version", AnsibleCommands.Version(), false, inventory: false)), stop));

        FillHosts();
        // 0.5.0: hosts added, renamed or removed elsewhere show here at once (ticks and the chosen node are kept).
        Live.While(this, h => AppData.Changed += h, h => AppData.Changed -= h, () => { FillNodes(); FillHosts(); });
        var invHead = new WrapPanel();
        invHead.Children.Add(Label("inventory hosts (select)"));
        invHead.Children.Add(Run("☷ Preview inventory", () => { _out.Text = Inventory().Yaml; _status.Text = $"{Inventory().Entries.Count} host(s) in the inventory."; return Task.CompletedTask; }));
        invHead.Children.Add(Run("⌗ Graph", () => Go("Inventory graph", AnsibleCommands.Graph(), false)));
        head.Children.Add(invHead);
        head.Children.Add(_hosts);

        head.Children.Add(Bar(Label("pattern"), _pattern, Run("◉ Ping", () => Go("Ping", AnsibleCommands.Ping(_pattern.Text), false), primary: true),
            Run("☰ Facts", () => Go("Facts", AnsibleCommands.Facts(_pattern.Text, ""), false)),
            Label("module"), _module, Label("args"), _args, _become, Run("▶ Ad-hoc", () => Go("Ad-hoc " + _module.Text.Trim(), AnsibleCommands.AdHoc(_pattern.Text, _module.Text, _args.Text, _become.IsChecked == true), changes: !ReadOnlyModule(_module.Text)))));
        head.Children.Add(Bar(Label("playbook"), _playbook, _check, _diff, Label("limit"), _limit, Label("tags"), _tags, Label("skip tags"), _skipTags, Label("extra vars"), _extra,
            Label("start at task"), _startAt, Label("forks"), _forks, _vault, _verbosity));
        head.Children.Add(Bar(
            Run("▶ Run playbook", () => Go("Playbook " + _playbook.Text.Trim(), AnsibleCommands.Playbook(_playbook.Text, PlaybookOptions(), _vault.IsChecked == true), changes: _check.IsChecked != true), primary: true),
            Run("✓ Syntax check", () => Go("Syntax check", AnsibleCommands.SyntaxCheck(_playbook.Text), false), tip: "Parses the playbook and what it includes on the control node; runs nothing"),
            Run("☰ List tasks", () => Go("List tasks", AnsibleCommands.ListTasks(_playbook.Text, _limit.Text, _tags.Text), false), tip: "The tasks the playbook would run (limit and tags respected), without running them"),
            Run("⌂ List hosts", () => Go("List hosts", AnsibleCommands.ListHosts(_playbook.Text, _limit.Text), false), tip: "Which inventory hosts each play would touch"),
            Run("✓ Lint", () => Go("Lint", AnsibleCommands.Lint(_playbook.Text), false, inventory: false))));
        head.Children.Add(Bar(Label("file"), _file, Label("vault"), _vaultOp, Run("⚿ Vault", VaultAsync),
            Run("⇩ Galaxy roles -r", () => Go("Galaxy roles", AnsibleCommands.Galaxy(_file.Text), changes: true, inventory: false, nodeChange: true), tip: "ansible-galaxy install -r <file> (roles)"),
            Run("⇩ Galaxy collections -r", () => Go("Galaxy collections", AnsibleCommands.CollectionInstall(_file.Text), changes: true, inventory: false, nodeChange: true), tip: "ansible-galaxy collection install -r <file>"),
            Run("☷ Installed", () => Go("Installed collections and roles", AnsibleCommands.Installed(), false, inventory: false)),
            Label("doc"), _doc, Run("? Doc", () => Go("Doc", AnsibleCommands.Doc(_doc.Text), false, inventory: false)), Run("⚙ Config", () => Go("Config", AnsibleCommands.Config(), false, inventory: false))));
        // The output: copy, save, or keep it with an incident (the full text, masked as shown).
        var outBar = new WrapPanel();
        outBar.Children.Add(Label("output"));
        var copy = Btn("⧉ Copy"); copy.Click += (_, _) => Menus.Clip(_out.Text); outBar.Children.Add(copy);
        var save = Btn("⤓ Save…"); save.Click += (_, _) => Menus.SaveText(_out.Text, $"ansible-{DateTime.Now:yyyyMMdd-HHmm}.log", "Log|*.log;*.txt|All files|*.*"); outBar.Children.Add(save);
        var inc = Btn("⚑ Add to incident"); inc.ToolTip = "Keeps this run's output on the active incident's timeline (encrypted)";
        inc.Click += (_, _) => { if (_out.Text.Trim().Length > 0) IncidentUi.AddText(Incidents.ItemKind.Log, _out.Text.Split('\n')[0].TrimStart('$', ' '), _out.Text); };
        outBar.Children.Add(inc);
        head.Children.Add(outBar);
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        head.Children.Add(_status);
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);
        _out.Mono();
        root.Children.Add(_out);
        Content = root;
    }

    private static TextBox Box(double w, string text, string tip) => new() { Width = w, Padding = new Thickness(5, 4, 5, 4), Text = text, ToolTip = tip };
    private static TextBlock Label(string t) => new() { Text = t, Style = (Style)Application.Current.Resources["Label"], VerticalAlignment = VerticalAlignment.Center };
    private static WrapPanel Bar(params UIElement[] items) { var w = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) }; foreach (var i in items) { if (i is FrameworkElement f) { f.Margin = new Thickness(0, 0, 8, 4); f.VerticalAlignment = VerticalAlignment.Center; } w.Children.Add(i); } return w; }
    private static Button Btn(string t) => new() { Content = t, Margin = new Thickness(0, 0, 6, 4) };
    private AnsibleCommands.PlaybookOptions PlaybookOptions() => new(_check.IsChecked == true, _diff.IsChecked == true, _limit.Text, _tags.Text, _extra.Text, _become.IsChecked == true, _verbosity.SelectedIndex,
        int.TryParse(_forks.Text.Trim(), out var f) ? f : 0, _startAt.Text, _skipTags.Text);

    private Button Run(string t, Func<Task> a, bool primary = false, string? tip = null)
    {
        var b = Btn(t);
        b.ToolTip = tip;
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

    private static bool ReadOnlyModule(string m) => m.Trim().ToLowerInvariant() is "ping" or "setup" or "gather_facts" or "ansible.builtin.ping" or "ansible.builtin.setup" or "debug" or "ansible.builtin.debug" or "stat" or "ansible.builtin.stat";

    private IAnsibleRunner Runner => RunnerForTest?.Invoke() ?? (_node.SelectedItem as ComboBoxItem)?.Tag as IAnsibleRunner ?? throw new InvalidOperationException("Pick a control node: a WSL distribution (install one with wsl --install) or a saved Linux host.");
    private List<HostEntry> Selected => _hosts.SelectedItems.OfType<ListBoxItem>().Select(i => (HostEntry)i.Tag).ToList();
    public AnsibleInventory.Inventory Inventory() => AnsibleInventory.Build(Selected);
    private string? Ask(string what) => PromptForTest != null ? PromptForTest(what) : Dialogs.Prompt("Ansible", what, password: true);

    private Task VaultAsync()
    {
        var op = _vaultOp.SelectedItem as string ?? "view";
        return Go("Vault " + op, AnsibleCommands.Vault(op, _file.Text), changes: op is "encrypt" or "decrypt" or "rekey", inventory: false, nodeChange: op is "encrypt" or "decrypt" or "rekey", needVault: true, needSecond: op == "rekey" ? "New vault password:" : null);
    }

    /// <summary>Builds the job (inventory, passwords on stdin), confirms when it changes anything, runs it and streams the output.</summary>
    private async Task Go(string title, string command, bool changes, bool inventory = true, bool nodeChange = false, bool needVault = false, string? needSecond = null)
    {
        var runner = Runner;
        var inv = inventory ? Inventory() : new AnsibleInventory.Inventory("", new());
        if (inventory && inv.Entries.Count == 0) throw new InvalidOperationException("Select at least one host for the inventory.");
        if (changes && inventory)
        {
            var hosts = inv.Entries.Select(e => e.Host).ToList();
            if (!(ConfirmForTest?.Invoke(hosts) ?? Envs.ConfirmMany(hosts, "Ansible", $"{title} will run against these hosts and may change them:", "Run"))) return;
        }
        if (nodeChange && runner is SshAnsibleRunner s && !Envs.Confirm(s.Host, "Ansible", $"{title} changes files on the control node {s.Name}.", "Run", danger: false)) return;
        var vaultPw = ""; var becomePw = "";
        if (needVault || command.Contains("\"$d/vp.sh\"")) vaultPw = Ask("Vault password:") ?? throw new OperationCanceledException();
        if (command.Contains("\"$d/bp.sh\"")) becomePw = Ask(needSecond ?? "Become (sudo) password:") ?? throw new OperationCanceledException();
        var job = AnsibleCommands.Make(title, command, inv.Yaml, vaultPw, becomePw, changes);
        _cts?.Cancel(); _cts = new CancellationTokenSource();
        _out.Text = $"$ {title}  —  on {runner.Name}\n";
        _status.Text = $"{title} is running on {runner.Name}…";
        AuditLog.Write("you", "ansible", title, $"{runner.Name}; {inv.Entries.Count} host(s)");
        var code = await runner.RunAsync(job, line => { if (Dispatcher.CheckAccess()) Append(Redaction.Mask(line)); else Dispatcher.BeginInvoke(() => Append(Redaction.Mask(line))); }, _cts.Token);
        _status.Text = code == 0 ? $"{title}: done." : code == 127 ? $"{title}: Ansible isn't installed on {runner.Name}." : $"{title}: exit code {code} — see the output.";
        AuditLog.Write("you", "ansible", title + " finished", $"exit {code}");
    }

    private void Append(string s)
    {
        _out.AppendText(s + "\n");
        if (_out.Text.Length > 400_000) _out.Text = _out.Text[^300_000..];
        _out.ScrollToEnd();
    }

    public void Dispose() => _cts?.Cancel();

    internal string OutputForTest => _out.Text;
    internal string StatusForTest => _status.Text;
    internal void SelectForTest(Func<HostEntry, bool> pick) { foreach (var i in _hosts.Items.OfType<ListBoxItem>()) i.IsSelected = pick((HostEntry)i.Tag); }
    internal Task RunForTest(string title, string command, bool changes) => Go(title, command, changes);
    internal void SetPlaybookForTest(string path, bool check) { _playbook.Text = path; _check.IsChecked = check; }
}
