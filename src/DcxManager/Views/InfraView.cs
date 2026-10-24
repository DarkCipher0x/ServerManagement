using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using DcxManager.Infra;
using DcxManager.Services;

namespace DcxManager.Views;

/// <summary>
/// Infrastructure (ADR-007): Docker and Proxmox providers side by side. Each provider is read-only until "Allow
/// changes" is ticked for it, and even then only the actions the target itself permits are offered (others are shown
/// disabled with the reason). Every change asks with target / action / environment / consequence and is reported as
/// verified only after the object was re-read. Nothing polls unless auto-refresh is chosen, and only while this screen
/// is open.
/// </summary>
public sealed class InfraView : UserControl, IDisposable
{
    private readonly ListBox _providers = new() { MinWidth = 200 };
    private Button? _add;
    private readonly ListView _objects = new();
    private readonly TextBlock _title = new TextBlock() { FontWeight = FontWeights.SemiBold }.Fs(FontStep.Heading);
    private readonly TextBlock _caps = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4) };
    private readonly ContentControl _badge = new();
    private readonly WrapPanel _actions = new() { Margin = new Thickness(0, 6, 0, 6) };
    private readonly TextBox _filter = Hint.Set(new TextBox { Width = 160, Padding = new Thickness(5, 3, 5, 3), ToolTip = "Filter by name, status, node or image" }, "Filter objects…");
    private readonly ComboBox _kind = new() { Width = 150 };
    private readonly ComboBox _refresh = new() { Width = 150, ToolTip = "Auto-refresh only while this screen is open. Off = nothing polls." };
    private readonly CheckBox _allow = new() { Content = "Allow changes", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
    private readonly Button _trust = new() { Content = "Trust certificate…", Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, (IInfraProvider P, ProviderCaps? Caps, IReadOnlyList<InfraObject> Items, DateTime At)> _state = new();
    private CancellationTokenSource? _cts;
    private bool _busy, _loading;

    public static InfraView? Current { get; private set; }

    public InfraView()
    {
        Current = this;
        var root = new Grid { Margin = new Thickness(12, 10, 12, 10) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        root.ColumnDefinitions.Add(new ColumnDefinition());

        var left = new DockPanel();
        var lh = new StackPanel();
        lh.Children.Add(new TextBlock { Text = "Infrastructure", Style = (Style)FindResource("H1") });
        lh.Children.Add(new TextBlock { Text = "Docker, Proxmox, Unraid, VMware vCenter, Hyper-V, Synology, Red Hat Satellite / Foreman and DCX-CTL portals. Read-only until you allow changes per provider.", Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 8) });
        var add = _add = new Button { Content = "＋ Add ▾", Style = (Style)FindResource("PrimaryButton"), HorizontalAlignment = HorizontalAlignment.Left };
        Menus.AttachDropDown(add, AddItems);
        lh.Children.Add(add);
        DockPanel.SetDock(lh, Dock.Top);
        left.Children.Add(lh);
        var lb = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        lb.Children.Add(Small("✎ Edit", () => { if (Sel is { } c) Edit(c, false); }));
        lb.Children.Add(Small("✕ Remove", RemoveProvider, "DangerButton"));
        DockPanel.SetDock(lb, Dock.Bottom);
        left.Children.Add(lb);
        _providers.Margin = new Thickness(0, 8, 0, 0);
        left.Children.Add(_providers);
        root.Children.Add(left);

        var right = new DockPanel();
        var head = new StackPanel();
        var titleRow = new WrapPanel();
        titleRow.Children.Add(_title);
        titleRow.Children.Add(_allow);
        head.Children.Add(titleRow);
        head.Children.Add(_badge);
        head.Children.Add(_caps);
        head.Children.Add(_trust);
        var tools = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        tools.Children.Add(Small("↻ Refresh", () => _ = RefreshAsync(), "PrimaryButton"));
        tools.Children.Add(_filter);
        _kind.Margin = new Thickness(6, 0, 6, 0);
        tools.Children.Add(_kind);
        tools.Children.Add(_refresh);
        head.Children.Add(tools);
        head.Children.Add(_actions);
        DockPanel.SetDock(head, Dock.Top);
        right.Children.Add(head);
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        DockPanel.SetDock(_status, Dock.Bottom);
        right.Children.Add(_status);
        var gv = new GridView();
        void Col(string h, string p, double w) => gv.Columns.Add(new GridViewColumn { Header = h, Width = w, DisplayMemberBinding = new Binding(p) });
        Col("", "StatusGlyph", 24); Col("Name", "Name", 170); Col("Status", "Status", 80); Col("Kind", "Kind", 70); Col("On", "Parent", 80); Col("Details", "Detail", 360);
        _objects.View = gv;
        _objects.SelectionMode = SelectionMode.Extended;
        right.Children.Add(_objects);
        Grid.SetColumn(right, 2);
        root.Children.Add(right);
        Content = root;

        _refresh.ItemsSource = new[] { "Auto-refresh: off", "every 30 s", "every 60 s", "every 5 min" };
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) => { if (IsVisible && !_busy) using (ExternalCredentials.Background()) _ = RefreshAsync(); }, Dispatcher) { IsEnabled = false };
        _providers.SelectionChanged += (_, _) => { ShowProvider(); _ = RefreshAsync(onlyIfStale: true); };
        _objects.SelectionChanged += (_, _) => ShowActions();
        _filter.TextChanged += (_, _) => ShowObjects();
        _kind.SelectionChanged += (_, _) => { if (!_loading) ShowObjects(); };
        _refresh.SelectionChanged += (_, _) =>
        {
            if (_loading || Sel is not { } c) return;
            c.RefreshSeconds = _refresh.SelectedIndex switch { 1 => 30, 2 => 60, 3 => 300, _ => 0 };
            AppData.Save(); SetTimer(c);
        };
        _allow.Click += (_, _) => ToggleAllow();
        _trust.Click += (_, _) => Trust();
        // 0.5.1 #5: each list has its own menu. Right-clicking a provider gives that provider's commands; right-clicking an
        // object gives what can be done to it; "Add" appears only on the empty part of the provider list.
        Menus.SelectOnRightClick(_providers);
        Menus.Attach(_providers, () => ProviderUnderMouse() is { } pc ? ProviderMenu(pc) : AddItems());
        Menus.SelectOnRightClick(_objects);
        Menus.Attach(_objects, () => SelectedObjects.Count > 0 ? ObjectMenu() : Sel is { } c ? ProviderMenu(c) : Array.Empty<object?>());
        Loaded += (_, _) => { Reload(); };
        Unloaded += (_, _) => { _timer.Stop(); _cts?.Cancel(); };
        Locks.Changed += OnLocksChanged;
    }

    // ---- menus (0.5.1 #5) ---------------------------------------------------------------------------------------------

    /// <summary>"＋ Add ▾", and the right-click menu on the empty part of the provider list.</summary>
    internal IEnumerable<object?> AddItems() => !Restrictions.Allowed("infra.add")
        ? new object?[] { Menus.Item("Adding providers is turned off by policy", () => { }, enabled: false, tip: Restrictions.Why("infra.add")) }
        : new object?[]
    {
        Menus.Item("Docker on a saved host (SSH)…", () => Edit(new ProviderConfig { Kind = "docker-ssh", Name = "Docker" }, true)),
        Menus.Item("Docker on this PC (Docker Desktop)…", () => Edit(new ProviderConfig { Kind = "docker-local", Name = "Docker Desktop", Endpoint = "docker_engine", Environment = "LOCAL" }, true)),
        Menus.Item("Proxmox VE (API token)…", () => Edit(new ProviderConfig { Kind = "proxmox", Name = "Proxmox", Endpoint = "https://pve.lan:8006" }, true)),
        Menus.Item("Unraid (API key)…", () => Edit(new ProviderConfig { Kind = "unraid", Name = "Unraid", Endpoint = "https://tower.lan" }, true)),
        Menus.Item("VMware vCenter (user + password)…", () => Edit(new ProviderConfig { Kind = "vsphere", Name = "vCenter", Endpoint = "https://vcenter.lan" }, true)),
        Menus.Item("Hyper-V host (PowerShell / WinRM)…", () => Edit(new ProviderConfig { Kind = "hyperv", Name = "Hyper-V", Endpoint = "" }, true)),
        Menus.Item("Synology DSM (Web API)…", () => Edit(new ProviderConfig { Kind = "synology", Name = "Synology", Endpoint = "https://nas.lan:5001" }, true)),
        Menus.Item("Red Hat Satellite / Foreman + Katello…", () => Edit(new ProviderConfig { Kind = "satellite", Name = "Satellite", Endpoint = "https://satellite.lan" }, true)),
        // 0.6.16 (ADR-094): UniFi with Ubiquiti's API keys.
        Menus.Item("UniFi Network (API key)…", () => Edit(new ProviderConfig { Kind = "unifi", Name = "UniFi", Endpoint = "https://192.168.1.1" }, true)),
        Menus.Item("UniFi Site Manager — all consoles (api.ui.com)…", () => Edit(new ProviderConfig { Kind = "unifi-cloud", Name = "UniFi Site Manager", Endpoint = UniFiProvider.CloudBase }, true)),
        Menus.Item("DCX-CTL (dcx-ctl/1 over SSH)…", () => Edit(new ProviderConfig { Kind = "dcx-portal", Name = "DCX-CTL", Environment = "PRODUCTION" }, true)),
    };

    /// <summary>The provider row under the mouse (right-click selects it first), or null on the empty part of the list.</summary>
    private ProviderConfig? ProviderUnderMouse() =>
        _providers.InputHitTest(System.Windows.Input.Mouse.GetPosition(_providers)) is DependencyObject d && Menus.Ancestor<ListBoxItem>(d) is { } li
            ? (li.DataContext as FrameworkElement ?? li.Content as FrameworkElement)?.Tag as ProviderConfig : null;

    /// <summary>One provider's commands: only what this kind of provider can do, nothing about adding another.</summary>
    internal IEnumerable<object?> ProviderMenu(ProviderConfig c)
    {
        var s = StateFor(c);
        bool web = c.Endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        void Select() { if (Sel != c) _providers.SelectedItem = _providers.Items.OfType<FrameworkElement>().FirstOrDefault(e => e.Tag == c); }
        var refresh = new[] { (0, "Off"), (30, "Every 30 s"), (60, "Every 60 s"), (300, "Every 5 min") }
            .Select(r => (object?)Menus.Item((c.RefreshSeconds == r.Item1 ? "✓ " : "   ") + r.Item2, () =>
            {
                Select(); c.RefreshSeconds = r.Item1; AppData.Save(); SetTimer(c);
                _loading = true; _refresh.SelectedIndex = r.Item1 switch { 30 => 1, 60 => 2, 300 => 3, _ => 0 }; _loading = false;
            })).ToArray();
        if (Closed(c)) return new object?[]
        {
            Menus.Item("🔓 Unlock…", () => { Select(); _ = UnlockAsync(c); }),
            LockUi.Menu("infra:" + c.Id, c.Name),
        };
        return new object?[]
        {
            Menus.Item("↻ Refresh", () => { Select(); _state[c.Id] = s with { At = DateTime.MinValue }; _ = RefreshAsync(); }),
            Menus.Item("⚙ Test connection", () => { Select(); _ = TestAsync(c); }, tip: "Connects, checks the credential and what it may do, and says so; nothing is changed"),
            Menus.Sub("Auto-refresh", refresh),
            Menus.Sep(),
            Menus.Item(c.AllowChanges ? "🔒 Make read-only" : "🔓 Allow changes…", () => { Select(); _allow.IsChecked = !c.AllowChanges; ToggleAllow(); }),
            s.P is ITlsPinned { LastSeenSha256.Length: > 0 } ? Menus.Item("Trust certificate…", () => { Select(); Trust(); }) : null,
            s.P is ITlsPinned && c.TlsSha256.Length > 0 ? Menus.Item("Copy pinned certificate fingerprint", () => Menus.Clip(ProxmoxProvider.Pretty(c.TlsSha256))) : null,
            Menus.Sep(),
            web ? Menus.Item("🌐 Open web UI", () => PortalUi.OpenAddress(c.Endpoint, c.Environment), enabled: Browser.PortalBrowser.Enabled, tip: Browser.PortalBrowser.Enabled ? c.Endpoint : "The portal browser is off (Settings → Integrations)") : null,
            c.Endpoint.Length > 0 ? Menus.Item("Copy address", () => Menus.Clip(c.Endpoint)) : null,
            Menus.Item("✎ Edit connection…", () => { Select(); Edit(c, false); }),
            Menus.Item("⧉ Duplicate…", () => Edit(new ProviderConfig { Kind = c.Kind, Name = c.Name + " (copy)", Endpoint = c.Endpoint, Environment = c.Environment, CredentialId = c.CredentialId }, true),
                tip: "A new provider with the same address and credential (not allowed to change anything, nothing pinned)"),
            Menus.AssistItem("✦ Explain this provider with AI", _ => { Select(); return ExplainProviderAsync(c); }),
            Menus.Item($"📄 Docs ({Docs.DocStore.PinnedTo("infra:" + c.Id).Count})", () => MainWindow.Instance.OpenDocs("infra:" + c.Id, c.Name), tip: "Markdown documents pinned to this provider (runbooks for the cluster, hypervisor or Docker host)"),
            LockUi.Menu("infra:" + c.Id, c.Name),
            Menus.Sep(),
            Menus.Item("✕ Remove…", () => { Select(); RemoveProvider(); }),
        };
    }

    /// <summary>Provider menu → Test connection: what the credential reaches and may do, said in the status line.</summary>
    private async Task TestAsync(ProviderConfig c)
    {
        if (Closed(c)) return;
        var s = StateFor(c);
        _status.Text = "testing " + s.P.Where + "…";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var caps = await s.P.DetectAsync(cts.Token);
            _state[c.Id] = (s.P, caps, s.Items, s.At);
            _status.Text = caps.Reachable ? $"✓ {c.Name}: {caps.Detail} · read {(caps.CanRead ? "yes" : "no")} · change {(caps.CanControl ? "yes" : "no")}" : "✕ " + caps.Detail;
            AuditLog.Write("you", "infra:" + c.Name, "connection tested", caps.Reachable ? "reachable" : caps.Detail);
            if (Sel == c) ShowCaps();
            UpdateRow(c);
        }
        catch (Exception ex) { _status.Text = "✕ " + ex.Message.Split('\n')[0]; }
    }

    private async Task ExplainProviderAsync(ProviderConfig c)
    {
        if (Assist.Current is not { } assist) return;
        var s = StateFor(c);
        var counts = string.Join(", ", s.Items.GroupBy(o => o.Kind).Select(g => $"{g.Count()} {g.Key}"));
        await assist.AskAsync(c.Name, $"Provider: {Infra.Infra.KindTitle(c.Kind)} · environment {c.Environment} · {(c.AllowChanges ? "changes allowed" : "read-only")} · {s.Caps?.Detail ?? "not checked"} · objects: {(counts.Length > 0 ? counts : "none loaded")}",
            "Summarise this infrastructure provider's state and anything worth checking. You are advisory only.");
    }

    private Button Small(string text, Action a, string style = "SmallButton") { var b = new Button { Content = text, Style = (Style)FindResource(style), Margin = new Thickness(0, 0, 6, 4) }; if (style != "SmallButton") { b.MinWidth = 0; b.Padding = new Thickness(10, 3, 10, 3); } b.Click += (_, _) => a(); return b; }

    private ProviderConfig? Sel => (_providers.SelectedItem as FrameworkElement)?.Tag as ProviderConfig;
    /// <summary>For the AI's "current context" (names only).</summary>
    public string? SelectedProviderName => Sel?.Name;
    public IReadOnlyList<string> SelectedObjectNames => SelectedObjects.Select(o => $"{o.Kind} {o.Name}").ToList();
    private List<InfraObject> SelectedObjects => _objects.SelectedItems.OfType<InfraObject>().ToList();

    // ---- providers ----------------------------------------------------------------------------------------------------

    public void Reload()
    {
        var keep = Sel?.Id;
        _providers.ItemsSource = AppData.Current.InfraProviders.Select(Row).ToList();
        _providers.SelectedItem = _providers.Items.OfType<FrameworkElement>().FirstOrDefault(e => (e.Tag as ProviderConfig)?.Id == keep) ?? _providers.Items.OfType<FrameworkElement>().FirstOrDefault();
        if (AppData.Current.InfraProviders.Count == 0) { _title.Text = "No providers yet"; _caps.Text = "＋ Add a Docker host (over SSH or Docker Desktop on this PC) or a Proxmox VE server (API token). Nothing is contacted until you add one."; _objects.ItemsSource = null; _actions.Children.Clear(); _allow.Visibility = Visibility.Collapsed; }
    }

    private FrameworkElement Row(ProviderConfig c)
    {
        var p = new StackPanel { Tag = c, Margin = new Thickness(2, 3, 2, 5) };
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        var pill = EnvUi.Pill(c.Environment, small: true);   // ADR-017: the same label as hosts and tabs
        pill.Margin = new Thickness(0, 0, 6, 0);
        head.Children.Add(pill);
        head.Children.Add(new TextBlock { Text = LockUi.Decorate("infra:" + c.Id, c.Name), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        p.Children.Add(head);
        var s = _state.TryGetValue(c.Id, out var st) && st.Caps is { } caps ? (caps.Reachable ? (caps.CanRead ? "● " + caps.Version : "◇ no read access") : "✕ " + caps.Detail) : "○ not checked yet";
        var meta = new TextBlock { Text = $"{Infra.Infra.KindTitle(c.Kind)} · {c.Environment}{(c.AllowChanges ? " · changes allowed" : " · read-only")}\n{s}", TextWrapping = TextWrapping.Wrap }.Fs(FontStep.Small);
        meta.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        p.Children.Add(meta);
        return p;
    }

    private (IInfraProvider P, ProviderCaps? Caps, IReadOnlyList<InfraObject> Items, DateTime At) StateFor(ProviderConfig c)
    {
        if (!_state.TryGetValue(c.Id, out var s) || s.P.Config != c)
        {
            if (_state.TryGetValue(c.Id, out var old)) (old.P as IDisposable)?.Dispose();   // e.g. ends a vCenter session
            _state[c.Id] = s = (Infra.Infra.Create(c), null, Array.Empty<InfraObject>(), DateTime.MinValue);
        }
        return s;
    }

    private void ShowProvider()
    {
        if (Sel is not { } c) return;
        if (Closed(c))
        {
            // 0.5.1 #10: a locked provider shows nothing and contacts nothing until it is unlocked.
            _timer.Stop();
            _title.Text = "🔒 " + c.Name; _badge.Content = null; _allow.Visibility = Visibility.Collapsed; _trust.Visibility = Visibility.Collapsed;
            _caps.Text = "Locked. Unlock it to see and manage its objects (the button below, or right-click → Unlock).";
            _objects.ItemsSource = null; _actions.Children.Clear();
            _actions.Children.Add(Small("🔓 Unlock…", () => _ = UnlockAsync(c), "PrimaryButton"));
            return;
        }
        var s = StateFor(c);
        _loading = true;
        _title.Text = c.Name;
        _allow.Visibility = Visibility.Visible;
        _allow.IsChecked = c.AllowChanges;
        _badge.Content = s.P.NetworkClass is { } cls ? NetUi.Badge(cls, s.P.Where) : null;
        _refresh.SelectedIndex = c.RefreshSeconds switch { 30 => 1, 60 => 2, 300 => 3, _ => 0 };
        var kinds = c.Kind switch
        {
            "proxmox" => new[] { "All", "vm", "lxc", "node", "storage" },
            "unraid" => new[] { "All", "container", "vm", "disk", "array", "server" },
            "vsphere" => new[] { "All", "vm", "host", "cluster", "datastore", "network" },
            "hyperv" => new[] { "All", "vm", "checkpoint", "switch", "host", "path" },
            "synology" => new[] { "All", "system", "pool", "volume", "disk", "share", "package", "container", "vm", "backup", "user", "update", "sign-in" },
            "dcx-portal" => new[] { "All", "Portal", "System", "Stats", "Security", "Mail", "Access", "User" },
            "unifi" => new[] { "All", "site", "device", "pending", "client", "network", "wifi", "wan", "vpn server", "site-to-site", "firewall zone", "firewall policy", "acl rule", "dns policy", "traffic list", "voucher", "lag", "switch stack", "console" },
            "unifi-cloud" => new[] { "All", "console", "site", "device", "isp", "sd-wan" },
            _ => new[] { "All", "container", "image", "network", "volume" },
        };
        _kind.ItemsSource = kinds; _kind.SelectedIndex = 0;
        _loading = false;
        SetTimer(c);
        ShowCaps(); ShowObjects();
    }

    private void SetTimer(ProviderConfig c)
    {
        _timer.Stop();
        if (c.RefreshSeconds > 0) { _timer.Interval = TimeSpan.FromSeconds(c.RefreshSeconds); _timer.Start(); }
    }

    private void ShowCaps()
    {
        if (Sel is not { } c) return;
        var s = StateFor(c);
        _trust.Visibility = s.P is ITlsPinned { LastTls: not TlsDecision.Accept } ? Visibility.Visible : Visibility.Collapsed;
        if (s.Caps is not { } caps) { _caps.Text = "Not checked yet — ↻ Refresh connects and discovers what this credential may do."; return; }
        _caps.Text = (caps.Reachable ? $"{caps.Detail} · read: {(caps.CanRead ? "yes" : "no")} · change: {(caps.CanControl ? (c.AllowChanges ? "yes" : "possible, but not allowed here") : "no permission")}" : "✕ " + caps.Detail)
                     + (caps.Notes.Count > 0 ? "\n" + string.Join("\n", caps.Notes) : "");
    }

    private void ShowObjects()
    {
        if (Sel is not { } c) return;
        var s = StateFor(c);
        var kind = _kind.SelectedItem as string ?? "All";
        var f = _filter.Text.Trim();
        var items = s.Items.Where(o => kind == "All" || o.Kind == kind)
            .Where(o => f.Length == 0 || $"{o.Name} {o.Status} {o.Parent} {o.Detail}".Contains(f, StringComparison.OrdinalIgnoreCase))
            .OrderBy(o => o.Kind).ThenBy(o => o.Status == "running" ? 0 : 1).ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var sel = SelectedObjects.Select(o => o.Id).ToHashSet();
        _objects.ItemsSource = items;
        foreach (var o in items.Where(o => sel.Contains(o.Id))) _objects.SelectedItems.Add(o);
        if (s.At != DateTime.MinValue) _status.Text = $"{s.Items.Count} object(s) · refreshed {s.At:HH:mm:ss}";
        ShowActions();
    }

    // ---- refresh ------------------------------------------------------------------------------------------------------

    public async Task RefreshAsync(bool onlyIfStale = false)
    {
        if (Sel is not { } c || _busy || Closed(c)) return;
        var s = StateFor(c);
        if (onlyIfStale && s.At != DateTime.MinValue) return;
        _busy = true;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        _status.Text = "contacting " + s.P.Where + "…";
        try
        {
            var caps = await s.P.DetectAsync(cts.Token);
            IReadOnlyList<InfraObject> items = Array.Empty<InfraObject>();
            if (caps.Reachable && caps.CanRead)
            {
                try { items = await s.P.InventoryAsync(cts.Token); }
                catch (Exception ex) when (ex is not OperationCanceledException) { caps = caps with { Notes = caps.Notes.Append("Inventory failed: " + ex.Message.Split('\n')[0]).ToList() }; }
            }
            _state[c.Id] = (s.P, caps, items, DateTime.Now);
            InfraStatus.Record(c, caps, items, "Infrastructure screen");   // the dashboard shows it without contacting anything (ADR-018)
            if (Sel == c) { ShowCaps(); ShowObjects(); }
            UpdateRow(c);
        }
        catch (OperationCanceledException) { _status.Text = "cancelled / timed out"; }
        catch (Exception ex) { _status.Text = "✕ " + ex.Message.Split('\n')[0]; App.Log("Infra refresh", ex); }
        finally { _busy = false; }
    }

    private void UpdateRow(ProviderConfig c)
    {
        var rows = _providers.Items.OfType<FrameworkElement>().ToList();
        var i = rows.FindIndex(e => e.Tag == c);
        if (i < 0) return;
        rows[i] = Row(c);
        _loading = true;
        _providers.ItemsSource = rows; _providers.SelectedIndex = i;
        _loading = false;
    }

    // ---- actions ------------------------------------------------------------------------------------------------------

    private void ShowActions()
    {
        _actions.Children.Clear();
        // 0.5.1 #6: no Hyper-V module on this PC: Windows' own install for this edition, behind UAC, one click.
        if (Sel is { } hv && StateFor(hv).P is HyperVProvider { MissingModule: { } edition } && HyperVSetup.ModuleFix(edition).Command is { } command)
        {
            var fix = Small("⚙ Install the Hyper-V module (UAC)…", () => _ = InstallHyperVModuleAsync(hv, edition), "PrimaryButton");
            fix.ToolTip = "Runs, elevated: " + command;
            _actions.Children.Add(fix);
        }
        if (Sel is not { } c || SelectedObjects is not { Count: 1 } one) { if (SelectedObjects.Count > 1 && Assist.Current != null) _actions.Children.Add(Small("✦ Explain selection with AI", () => _ = ExplainAsync())); return; }
        var s = StateFor(c);
        foreach (var off in Infra.Infra.Offer(s.P, one[0], s.Caps ?? ProviderCaps.Unreachable("not checked")))
        {
            var b = Small(off.Action.Label, () => _ = RunAsync(off.Action, one[0]), off.Action.Consequence == Consequence.DataLoss ? "DangerButton" : "SmallButton");
            b.IsEnabled = off.Enabled;
            b.ToolTip = off.Enabled ? off.Action.Explain : off.Why;
            ToolTipService.SetShowOnDisabled(b, true);
            _actions.Children.Add(b);
        }
        if (Assist.Current != null) _actions.Children.Add(Small("✦ Explain with AI", () => _ = ExplainAsync()));
    }

    private IEnumerable<object?> ObjectMenu()
    {
        var sel = SelectedObjects;
        if (Sel is { } c && sel.Count == 1)
        {
            var s = StateFor(c);
            foreach (var off in Infra.Infra.Offer(s.P, sel[0], s.Caps ?? ProviderCaps.Unreachable("not checked")))
                yield return Menus.Item(off.Action.Label + (off.Enabled ? "" : $"  ({off.Why})"), () => _ = RunAsync(off.Action, sel[0]), enabled: off.Enabled);
            yield return Menus.Sep();
        }
        yield return Menus.Item("Copy names", () => Menus.Clip(string.Join("\n", sel.Select(o => o.Name))));
        yield return Menus.Item("Copy ids", () => Menus.Clip(string.Join("\n", sel.Select(o => o.Id))));
        yield return Menus.AssistItem("✦ Explain with AI", _ => ExplainAsync());
    }

    public async Task RunAsync(InfraAction a, InfraObject o)
    {
        if (Sel is not { } c) return;
        var s = StateFor(c);
        var offer = Infra.Infra.Offer(s.P, o, s.Caps ?? ProviderCaps.Unreachable("not checked")).FirstOrDefault(x => x.Action.Id == a.Id);
        if (offer is not { Enabled: true }) { Dialogs.Info(a.Label, offer?.Why ?? "not available"); return; }
        IReadOnlyDictionary<string, string> input = new Dictionary<string, string>();
        if (a.Inputs.Count > 0)
        {
            if (AskInputs(a, o) is not { } asked) return;
            input = asked;
        }
        if (a.Mutating)
        {
            bool strict = a.Consequence == Consequence.DataLoss || Envs.IsProduction(c.Environment);
            if (!Dialogs.Confirm($"{a.Label.TrimStart('▶', '■', '↻', '⏻', '✕', '＋', '⧉', '◷', '⇩', '⇧', '↺', '‖', '▷', '⇢', '⛁', '◇', '⚙', '✎', '⇄', '⏏', '⇌', '⚡', ' ')} — {o.Name}", Infra.Infra.ConfirmText(s.P, a, o, input), a.Label, danger: a.Consequence != Consequence.None, typeToConfirm: strict ? o.Name : null)) return;
        }
        _status.Text = $"{a.Label} {o.Name}… (waiting for {(a.Mutating ? "the result to be verified" : "the output")})";
        IsEnabled = false;
        try
        {
            int providersBefore = AppData.Current.InfraProviders.Count;
            var r = await s.P.RunAsync(a, o, input, CancellationToken.None);
            if (a.Mutating) AuditLog.Write("you", $"{c.Name}:{o.Name}", $"{a.Id} {o.Kind}" + (input.Count > 0 ? " (" + string.Join(", ", input.Select(kv => $"{kv.Key}={kv.Value}")) + ")" : ""), (r.Verified ? "VERIFIED · " : r.Accepted ? "NOT VERIFIED · " : "REFUSED · ") + r.Message);
            // A change's output is shown once it is verified (0.6.16: the UniFi voucher codes); a read's always.
            if (r.Output != null && (!a.Mutating || r.Verified)) Dialogs.ShowText($"{a.Label} — {o.Name}", r.Output.Length > 0 ? r.Output : "(no output)");
            if (AppData.Current.InfraProviders.Count != providersBefore) Reload();   // e.g. a UniFi console added from Site Manager
            _status.Text = $"{(r.Verified ? "✓" : r.Accepted ? "⚠" : "✕")} {a.Label} {o.Name}: {r.Message}";
            if (a.Mutating && !r.Verified) Dialogs.Info(a.Label, $"{o.Name}: {r.Message}");
        }
        catch (Exception ex) { _status.Text = "✕ " + ex.Message.Split('\n')[0]; App.Log("Infra action", ex); }
        finally { IsEnabled = true; }
        if (a.Mutating) { _state[c.Id] = s with { At = DateTime.MinValue }; await RefreshAsync(); }
    }

    /// <summary>The values an action needs, in a small form (validated per field). {name} and {now} in defaults are filled.</summary>
    private Dictionary<string, string>? AskInputs(InfraAction a, InfraObject o)
    {
        var w = Dialogs.MakeWindow($"{a.Label.Trim()} — {o.Name}", 520);
        var p = new StackPanel { Margin = new Thickness(18) };
        p.Children.Add(new TextBlock { Text = a.Explain, Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
        var boxes = new List<(InfraInput In, Control Box)>();
        foreach (var i in a.Inputs)
        {
            p.Children.Add(new TextBlock { Text = i.Label.ToUpperInvariant() + (i.Required ? "" : " (optional)"), Style = (Style)FindResource("Label"), Margin = new Thickness(0, 8, 0, 4) });
            var def = Infra.Infra.FillDefault(i, o);
            Control box = i.Options is { Length: > 0 } ? new ComboBox { ItemsSource = i.Options, SelectedItem = def.Length > 0 ? def : i.Options[0], Width = 160, HorizontalAlignment = HorizontalAlignment.Left }
                : new TextBox { Text = def, Padding = new Thickness(5, 4, 5, 4) };
            box.ToolTip = i.Hint;
            p.Children.Add(box);
            if (i.Hint != null) p.Children.Add(new TextBlock { Text = i.Hint, Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap });
            boxes.Add((i, box));
        }
        var ok = new Button { Content = "Continue", Style = (Style)FindResource("PrimaryButton"), IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var bs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        bs.Children.Add(ok); bs.Children.Add(cancel); p.Children.Add(bs);
        w.Content = p;
        Dictionary<string, string>? result = null;
        ok.Click += (_, _) =>
        {
            var values = boxes.ToDictionary(b => b.In.Name, b => (b.Box is ComboBox cb ? cb.SelectedItem as string : ((TextBox)b.Box).Text)?.Trim() ?? "");
            if (boxes.Select(b => b.In.Validate(values[b.In.Name])).FirstOrDefault(e => e != null) is { } err) { Dialogs.Error(a.Label.Trim(), err); return; }
            result = values.Where(kv => kv.Value.Length > 0).ToDictionary(kv => kv.Key, kv => kv.Value);
            w.DialogResult = true;
        };
        return w.ShowDialog() == true ? result : null;
    }

    private async Task ExplainAsync()
    {
        if (Sel is not { } c || Assist.Current is not { } assist) return;
        var sel = SelectedObjects.Take(40).ToList();
        if (sel.Count == 0) return;
        var s = StateFor(c);
        var text = $"Provider: {Infra.Infra.KindTitle(c.Kind)} · {s.Caps?.Detail} · environment {c.Environment}\n" +
                   string.Join("\n", sel.Select(o => $"- {o.Kind} “{o.Name}” · status {o.Status}{(o.Parent.Length > 0 ? " · on " + o.Parent : "")} · {o.Detail}"));
        await assist.AskAsync($"{sel.Count} {c.Name} object(s)", text,
            "Explain these infrastructure objects: what they are, their state, anything that looks unhealthy or risky, and sensible next checks. You are advisory only: suggest, never claim to have changed anything.");
    }

    // ---- provider editing ---------------------------------------------------------------------------------------------

    private void ToggleAllow()
    {
        if (Sel is not { } c) return;
        if (Closed(c) || !Restrictions.Check("infra.edit")) { _allow.IsChecked = c.AllowChanges; return; }
        if (_allow.IsChecked == true && !Dialogs.Confirm("Allow changes", $"Allow start / stop{(c.Kind.StartsWith("docker") ? " / restart / pause / kill / remove of containers, and removing images, networks and volumes" : c.Kind == "unraid" ? " / restart / pause of containers and VMs (as far as the Unraid API offers them)" : c.Kind == "vsphere" ? " / suspend / reset / clone / resize / delete of VMs, and host disconnect / reconnect" : c.Kind == "hyperv" ? " / save / reset of VMs, checkpoints, export, new VMs, import, VM settings and delete" : " / shut down / reboot / pause, snapshots, clone, migrate, backups and templates")} for “{c.Name}” ({c.Environment})?\n\nEach change still asks first, shows target, environment and consequence, and is reported only once verified. The target's own permissions still apply.", "Allow changes", danger: true))
        { _allow.IsChecked = false; return; }
        c.AllowChanges = _allow.IsChecked == true;
        AppData.Save();
        AuditLog.Write("you", "infra:" + c.Name, c.AllowChanges ? "changes allowed" : "set to read-only");
        ShowCaps(); ShowActions(); UpdateRow(c);
    }

    private void Trust()
    {
        if (Sel is not { } c || StateFor(c).P is not ITlsPinned p || p.LastSeenSha256.Length == 0) return;
        var changed = p.LastTls == TlsDecision.Mismatch;
        if (!Dialogs.Confirm(changed ? "Certificate CHANGED" : "Trust this certificate?",
            $"{c.Endpoint} presents a certificate with SHA-256:\n\n{ProxmoxProvider.Pretty(p.LastSeenSha256)}\n\n" +
            (c.Kind == "proxmox" ? "Compare it with the fingerprint on the Proxmox host (shell: pvenode cert info, or the node's Certificates page)."
             : c.Kind == "vsphere" ? "Compare it with vCenter's machine certificate (vSphere Client → Administration → Certificates → Certificate Management, or your browser's certificate details for the vCenter address)."
             : c.Kind == "unifi" ? "Compare it with the console's certificate (your browser's certificate details for the console's address, or UniFi OS → Settings → Control Plane → certificate)."
             : "Compare it with the certificate on the server (Unraid: Settings → Management Access → certificate, or openssl x509 -fingerprint -sha256 on the server).") + " Trust it only if they match.\n\n" +
            (changed ? "⚠ This is NOT the certificate you trusted before. That happens after a certificate renewal — or an interception. Be sure before continuing." : "It will be pinned: a different certificate later is refused."),
            changed ? "Trust the NEW certificate" : "Trust", danger: true, typeToConfirm: changed ? "TRUST" : null)) return;
        c.TlsSha256 = p.LastSeenSha256;
        AppData.Save();
        AuditLog.Write("you", "infra:" + c.Name, changed ? "TLS certificate re-pinned" : "TLS certificate pinned", ProxmoxProvider.Pretty(p.LastSeenSha256));
        _state.Remove(c.Id);
        _ = RefreshAsync();
    }

    private void RemoveProvider()
    {
        if (Sel is { } l && (Closed(l) || !Restrictions.Check("infra.delete"))) return;
        if (Sel is not { } c || !Dialogs.Confirm("Remove provider", $"Remove “{c.Name}” from this app? Nothing changes on the server; the vault credential stays.", "Remove", danger: true)) return;
        AppData.Current.InfraProviders.Remove(c); _state.Remove(c.Id); InfraStatus.Forget(c.Id); Locks.Forget("infra:" + c.Id); AppData.Save(); Reload();
    }

    /// <summary>The "no stored credential" choice for Hyper-V (a picker entry only; never saved or added to the vault).</summary>
    private static readonly Credential WindowsSignIn = new() { Name = "(none — use my Windows sign-in)" };

    private void Edit(ProviderConfig c, bool isNew)
    {
        if (!Restrictions.Check(isNew ? "infra.add" : "infra.edit")) return;
        if (!isNew && Closed(c)) { _ = UnlockAsync(c, () => Edit(c, false)); return; }
        var w = Dialogs.MakeWindow((isNew ? "Add " : "Edit ") + Infra.Infra.KindTitle(c.Kind), 560);
        var p = new StackPanel { Margin = new Thickness(18) };
        TextBlock L(string t) => new() { Text = t, Style = (Style)FindResource("Label"), Margin = new Thickness(0, 8, 0, 4) };
        var name = new TextBox { Text = c.Name, Padding = new Thickness(5, 4, 5, 4) };
        var env = new ComboBox { IsEditable = true, Text = c.Environment, ItemsSource = Infra.Infra.Environments, Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
        p.Children.Add(L("NAME")); p.Children.Add(name);
        p.Children.Add(L("ENVIRONMENT (shown in every confirmation; PRODUCTION asks you to type the target's name)")); p.Children.Add(env);
        ComboBox? host = null, cred = null, mode = null; TextBox? endpoint = null, consoleId = null; PasswordBox? enrol = null;
        if (c.Kind == "dcx-portal")
        {
            // 0.3.0 §9: the portal server's saved host (user dcx-ctl + its Ed25519 key), the enrolment string (sealed in the
            // vault) and the host key fingerprint dcx-admin printed (pinned in Known hosts).
            host = new ComboBox { ItemsSource = AppData.Current.Hosts.Where(h => h.IsSsh).OrderBy(h => h.Display).ToList(), DisplayMemberPath = "Display", SelectedItem = AppData.Current.FindHost(c.HostId) };
            p.Children.Add(L("PORTAL SERVER (a saved host: user dcx-ctl, the Ed25519 key you enrolled)")); p.Children.Add(host);
            enrol = new PasswordBox();
            p.Children.Add(L(c.CredentialId == null ? "ENROLMENT STRING (dcxctl1.…, shown once by dcx-admin api-key create)" : "ENROLMENT STRING (stored sealed; leave empty to keep it)")); p.Children.Add(enrol);
            endpoint = new TextBox { Text = c.Endpoint, Padding = new Thickness(5, 4, 5, 4) }.Mono();
            p.Children.Add(L("SERVER HOST KEY (SHA256:… as dcx-admin printed it; pinned — any other key is refused)")); p.Children.Add(endpoint);
            p.Children.Add(new TextBlock { Text = "Create the key on the server: sudo dcx-admin api-key create --label ServerManagement --ssh-key \"<this PC's public key>\" --scope read|admin --from <your address>. Read scope only looks; admin scope can act, and every sensitive action still asks for your authenticator code.", Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        }
        else if (c.Kind == "synology")
        {
            endpoint = new TextBox { Text = c.Endpoint, Padding = new Thickness(5, 4, 5, 4) }.Mono();
            p.Children.Add(L("NAS ADDRESS (https only, e.g. https://nas.lan:5001)")); p.Children.Add(endpoint); p.Children.Add(NetUi.LiveBadge(endpoint));
            cred = new ComboBox { ItemsSource = AppData.Current.Credentials.Where(x => x.Kind == CredentialKind.Password).OrderBy(x => x.Name).ToList(), DisplayMemberPath = "Display", SelectedItem = AppData.Current.FindCredential(c.CredentialId) };
            p.Children.Add(L("DSM ACCOUNT (a vault credential: user name and password)")); p.Children.Add(CredRow(cred));
            p.Children.Add(new TextBlock { Text = "Use a dedicated DSM account, not “admin”: a normal user sees shares, packages and its own items; an administrator account is needed for system, storage, users and power. Two-factor accounts: after saving, select “DSM sign-in” and sign in once with your authenticator code (DSM then remembers this PC; the device token is sealed in the vault). Self-signed certificates are pinned on first use.", Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        }
        else if (c.Kind == "satellite")
        {
            endpoint = new TextBox { Text = c.Endpoint, Padding = new Thickness(5, 4, 5, 4) }.Mono();
            p.Children.Add(L("SATELLITE / FOREMAN ADDRESS (https only)")); p.Children.Add(endpoint); p.Children.Add(NetUi.LiveBadge(endpoint));
            cred = new ComboBox { ItemsSource = AppData.Current.Credentials.Where(x => x.Kind == CredentialKind.Password).OrderBy(x => x.Name).ToList(), DisplayMemberPath = "Display", SelectedItem = AppData.Current.FindCredential(c.CredentialId) };
            p.Children.Add(L("ACCOUNT (a vault credential: the user name, and a Personal Access Token as its password)")); p.Children.Add(CredRow(cred));
            p.Children.Add(new TextBlock { Text = "Create the token in Satellite: your user → Personal Access Tokens (set an expiry). Least privilege: a dedicated user with the Viewer role to look; add Remote Execution User for commands and errata, and the Content View / Repository manager permissions for sync, publish and promote. The certificate is checked by Windows (install your Satellite CA) or pinned on first use.", Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        }
        else if (c.Kind == "unifi")
        {
            // 0.6.16 (ADR-094): the console's own address, or Ubiquiti's Cloud Connector with the console's id.
            mode = new ComboBox { ItemsSource = new[] { "This console's address (on your network or VPN)", "Ubiquiti Cloud Connector (api.ui.com, from anywhere)" }, SelectedIndex = c.ConsoleId.Length > 0 ? 1 : 0, HorizontalAlignment = HorizontalAlignment.Left, Width = 380 };
            p.Children.Add(L("REACH THE CONSOLE THROUGH")); p.Children.Add(mode);
            endpoint = new TextBox { Text = c.ConsoleId.Length > 0 ? "" : c.Endpoint, Padding = new Thickness(5, 4, 5, 4) }.Mono();
            var endpointLabel = L("CONSOLE ADDRESS (https, e.g. https://192.168.1.1; a self-hosted Network server: https://unifi.lan:8443)");
            p.Children.Add(endpointLabel); p.Children.Add(endpoint); var live = NetUi.LiveBadge(endpoint); p.Children.Add(live);
            consoleId = new TextBox { Text = c.ConsoleId, Padding = new Thickness(5, 4, 5, 4) }.Mono();
            var consoleLabel = L("CONSOLE ID (unifi.ui.com → the console; or add it from a UniFi Site Manager provider in one click)");
            p.Children.Add(consoleLabel); p.Children.Add(consoleId);
            void ShowMode()
            {
                bool cloud = mode.SelectedIndex == 1;
                foreach (var e in new UIElement[] { endpointLabel, endpoint, live }) e.Visibility = cloud ? Visibility.Collapsed : Visibility.Visible;
                foreach (var e in new UIElement[] { consoleLabel, consoleId }) e.Visibility = cloud ? Visibility.Visible : Visibility.Collapsed;
            }
            mode.SelectionChanged += (_, _) => ShowMode();
            ShowMode();
            cred = new ComboBox { ItemsSource = AppData.Current.Credentials.Where(x => x.Kind == CredentialKind.Password).OrderBy(x => x.Name).ToList(), DisplayMemberPath = "Display", SelectedItem = AppData.Current.FindCredential(c.CredentialId) };
            p.Children.Add(L("API KEY (a vault credential: the key is its password)")); p.Children.Add(CredRow(cred));
            p.Children.Add(new TextBlock { Text = "Create the key in UniFi Network → Settings → Control Plane → Integrations (Network 9.0 or later); for the Cloud Connector use a key from unifi.ui.com → Settings → API Keys, made by the console's owner. The key carries its creator's role: a read-only administrator's key can look but not change. Add the key under Credentials first. A console's self-signed certificate is pinned on first use; the key is only ever sent in the X-API-KEY header.", Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        }
        else if (c.Kind == "unifi-cloud")
        {
            cred = new ComboBox { ItemsSource = AppData.Current.Credentials.Where(x => x.Kind == CredentialKind.Password).OrderBy(x => x.Name).ToList(), DisplayMemberPath = "Display", SelectedItem = AppData.Current.FindCredential(c.CredentialId) };
            p.Children.Add(L("SITE MANAGER API KEY (a vault credential: the key is its password)")); p.Children.Add(CredRow(cred));
            p.Children.Add(new TextBlock { Text = "Create it at unifi.ui.com → Settings → API Keys. It shows every console, site and device your UniFi account owns or administers, with health counts, firmware status and ISP metrics — read-only. Right-click a console → “Add as a UniFi Network provider” to manage it through the Cloud Connector with the same key.", Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        }
        else if (c.Kind == "docker-ssh")
        {
            host = new ComboBox { ItemsSource = AppData.Current.Hosts.Where(h => h.Protocol != "rdp").OrderBy(h => h.Display).ToList(), DisplayMemberPath = "Display", SelectedItem = AppData.Current.FindHost(c.HostId) };
            p.Children.Add(L("HOST (docker runs there over SSH; the login needs the docker group or sudo)")); p.Children.Add(host);
        }
        else if (c.Kind == "docker-local")
        {
            endpoint = new TextBox { Text = c.Endpoint, Padding = new Thickness(5, 4, 5, 4) }.Mono();
            p.Children.Add(L("NAMED PIPE (Docker Desktop: docker_engine)")); p.Children.Add(endpoint);
        }
        else if (c.Kind == "unraid")
        {
            endpoint = new TextBox { Text = c.Endpoint, Padding = new Thickness(5, 4, 5, 4) }.Mono();
            p.Children.Add(L("UNRAID ADDRESS (https; plain http only on your own network)")); p.Children.Add(endpoint); p.Children.Add(NetUi.LiveBadge(endpoint));
            cred = new ComboBox { ItemsSource = AppData.Current.Credentials.Where(x => x.Kind == CredentialKind.Password).OrderBy(x => x.Name).ToList(), DisplayMemberPath = "Display", SelectedItem = AppData.Current.FindCredential(c.CredentialId) };
            p.Children.Add(L("API KEY (a vault credential: the key is its password)")); p.Children.Add(CredRow(cred));
            p.Children.Add(new TextBlock { Text = "Needs Unraid 7.x (or the Unraid Connect plugin) with the API enabled. Create a key under Settings → Management Access → API Keys (or `unraid-api apikey --create`). Least privilege: a VIEWER role is enough to look; start/stop needs a role that allows Docker/VM changes. Add the key under Credentials first.", Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        }
        else if (c.Kind == "vsphere")
        {
            endpoint = new TextBox { Text = c.Endpoint, Padding = new Thickness(5, 4, 5, 4) }.Mono();
            p.Children.Add(L("VCENTER ADDRESS (https, vCenter 7.0 or later)")); p.Children.Add(endpoint); p.Children.Add(NetUi.LiveBadge(endpoint));
            cred = new ComboBox { ItemsSource = AppData.Current.Credentials.Where(x => x.Kind == CredentialKind.Password).OrderBy(x => x.Name).ToList(), DisplayMemberPath = "Display", SelectedItem = AppData.Current.FindCredential(c.CredentialId) };
            p.Children.Add(L("ACCOUNT (a vault credential: user name such as svc-dcx@vsphere.local, and its password)")); p.Children.Add(CredRow(cred));
            p.Children.Add(new TextBlock { Text = "Least privilege: a dedicated vCenter user with the Read-only role to look; add Virtual machine › Interaction › Power on / Power off (and › Provisioning › Clone virtual machine) on the folders where you want changes. Standalone ESXi hosts have no Automation API — connect their vCenter. Add the account under Credentials first.", Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        }
        else if (c.Kind == "hyperv")
        {
            endpoint = new TextBox { Text = c.Endpoint, Padding = new Thickness(5, 4, 5, 4) }.Mono();
            p.Children.Add(L("HYPER-V HOST (computer name; empty = this PC)")); p.Children.Add(endpoint);
            var creds = new List<Credential> { WindowsSignIn };
            creds.AddRange(AppData.Current.Credentials.Where(x => x.Kind == CredentialKind.Password).OrderBy(x => x.Name));
            cred = new ComboBox { ItemsSource = creds, DisplayMemberPath = "Name", SelectedItem = AppData.Current.FindCredential(c.CredentialId) ?? WindowsSignIn };
            p.Children.Add(L("SIGN-IN (optional vault credential, user DOMAIN\\name)")); p.Children.Add(CredRow(cred));
            p.Children.Add(new TextBlock { Text = "Needs the Hyper-V PowerShell module on this PC (Windows features → Hyper-V Management Tools). A remote host is reached over WinRM as Windows is configured: in a domain your sign-in (Kerberos) just works; the app never enables Basic authentication, unencrypted WinRM or TrustedHosts. Changes need the host's “Hyper-V Administrators” group.", Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        }
        else
        {
            endpoint = new TextBox { Text = c.Endpoint, Padding = new Thickness(5, 4, 5, 4) }.Mono();
            p.Children.Add(L("PROXMOX ADDRESS (https)")); p.Children.Add(endpoint); p.Children.Add(NetUi.LiveBadge(endpoint));
            cred = new ComboBox { ItemsSource = AppData.Current.Credentials.Where(x => x.Kind == CredentialKind.Password).OrderBy(x => x.Name).ToList(), DisplayMemberPath = "Display", SelectedItem = AppData.Current.FindCredential(c.CredentialId) };
            p.Children.Add(L("API TOKEN (a vault credential: user = user@realm!tokenid, password = the token secret)")); p.Children.Add(CredRow(cred));
            p.Children.Add(new TextBlock { Text = "Create it in Proxmox: Datacenter → Permissions → API Tokens. Least privilege: a dedicated user with PVEAuditor on / (read), plus PVEVMUser on /vms only if you want start/stop. Add the token under Credentials first.", Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        }
        var ok = new Button { Content = "Save", Style = (Style)FindResource("PrimaryButton"), IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var bs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        bs.Children.Add(ok); bs.Children.Add(cancel); p.Children.Add(bs);
        w.Content = p;
        ok.Click += (_, _) =>
        {
            if (host != null && host.SelectedItem is not HostEntry) { Dialogs.Error("Provider", "Choose the host."); return; }
            if (c.Kind == "proxmox")
            {
                if (ProxmoxProvider.ValidateEndpoint(endpoint!.Text) is { } bad) { Dialogs.Error("Provider", bad); return; }
                if (cred!.SelectedItem is not Credential cc || !cc.Username.Contains('!')) { Dialogs.Error("Provider", "Choose a credential whose user name is the token id (user@realm!tokenid)."); return; }
            }
            if (c.Kind == "unraid")
            {
                if (UnraidProvider.ValidateEndpoint(endpoint!.Text) is { } bad) { Dialogs.Error("Provider", bad); return; }
                if (NoSecret(cred!.SelectedItem)) { Dialogs.Error("Provider", "Choose the credential that holds the API key."); return; }
            }
            if (c.Kind == "vsphere")
            {
                if (VSphereProvider.ValidateEndpoint(endpoint!.Text) is { } bad) { Dialogs.Error("Provider", bad); return; }
                if (NoUserSecret(cred!.SelectedItem)) { Dialogs.Error("Provider", "Choose the credential with the vCenter user name and password."); return; }
            }
            if (c.Kind == "synology")
            {
                if (SynologyProvider.ValidateEndpoint(endpoint!.Text) is { } bad) { Dialogs.Error("Provider", bad); return; }
                if (NoUserSecret(cred!.SelectedItem)) { Dialogs.Error("Provider", "Choose the credential with the DSM user name and password."); return; }
            }
            if (c.Kind == "satellite")
            {
                if (SatelliteProvider.ValidateEndpoint(endpoint!.Text) is { } bad) { Dialogs.Error("Provider", bad); return; }
                if (NoUserSecret(cred!.SelectedItem)) { Dialogs.Error("Provider", "Choose the credential with the Satellite user name and Personal Access Token."); return; }
            }
            if (c.Kind == "unifi")
            {
                var badUnifi = mode!.SelectedIndex == 1 ? UniFiProvider.ValidateConsoleId(consoleId!.Text) : UniFiProvider.ValidateEndpoint(endpoint!.Text);
                if (badUnifi != null) { Dialogs.Error("Provider", badUnifi); return; }
                if (NoSecret(cred!.SelectedItem)) { Dialogs.Error("Provider", "Choose the credential that holds the API key."); return; }
            }
            if (c.Kind == "unifi-cloud" && NoSecret(cred!.SelectedItem)) { Dialogs.Error("Provider", "Choose the credential that holds the Site Manager API key."); return; }
            if (c.Kind == "hyperv" && HyperVProvider.ValidateHost(endpoint!.Text) is { } badHost) { Dialogs.Error("Provider", badHost); return; }
            if (c.Kind == "dcx-portal")
            {
                if (enrol!.Password.Length > 0) try { DcxCtl.DcxCtlProtocol.ParseEnrolment(enrol.Password); } catch (DcxCtl.DcxCtlProtocol.ProtocolException ex) { Dialogs.Error("Provider", ex.Message, explain: false); return; }
                else if (c.CredentialId == null) { Dialogs.Error("Provider", "Paste the enrolment string."); return; }
                if (!System.Text.RegularExpressions.Regex.IsMatch(endpoint!.Text.Trim(), @"^SHA256:[A-Za-z0-9+/]{43}=?$")) { Dialogs.Error("Provider", "The host key fingerprint looks like SHA256:… (43 characters after the colon)."); return; }
            }
            if (c.Kind == "docker-local" && !System.Text.RegularExpressions.Regex.IsMatch(endpoint!.Text.Trim(), "^[A-Za-z0-9_.-]{1,80}$")) { Dialogs.Error("Provider", "Pipe name: letters, digits, _ . - only."); return; }
            w.DialogResult = true;
        };
        if (w.ShowDialog() != true) return;
        if (c.Kind == "unifi" && mode!.SelectedIndex == 1) { endpoint!.Text = UniFiProvider.CloudBase; c.ConsoleId = consoleId!.Text.Trim(); }
        else if (c.Kind == "unifi") c.ConsoleId = "";
        var endpointChanged = endpoint != null && endpoint.Text.Trim() != c.Endpoint;
        c.Name = name.Text.Trim().Length > 0 ? name.Text.Trim() : Infra.Infra.KindTitle(c.Kind);
        c.Environment = Envs.Normalize(env.Text) is { Length: > 0 } ne ? ne : Envs.Lab;
        if (host?.SelectedItem is HostEntry he) c.HostId = he.Id;
        if (endpoint != null) c.Endpoint = endpoint.Text.Trim();
        if (cred?.SelectedItem is Credential cr) c.CredentialId = cr == WindowsSignIn ? null : cr.Id;   // Hyper-V: "use my Windows sign-in"
        if (endpointChanged && c.Kind is "proxmox" or "unraid" or "vsphere" or "synology" or "satellite" or "unifi") c.TlsSha256 = "";
        if (c.Kind == "dcx-portal") SavePortalSecrets(c, enrol!.Password);   // a different server is never trusted by an old pin
        if (isNew) AppData.Current.InfraProviders.Add(c);
        AppData.Save();
        AuditLog.Write("you", "infra:" + c.Name, isNew ? "provider added" : "provider edited", Infra.Infra.KindTitle(c.Kind));
        _state.Remove(c.Id);
        Reload();
        _providers.SelectedItem = _providers.Items.OfType<FrameworkElement>().FirstOrDefault(e => e.Tag == c);
    }

    // 0.6.19: a vault credential kept in Bitwarden or Password Safe has no stored secret but has one to read (ADR-096): the
    // checks below accept it (before, Save refused it).
    internal static bool NoSecret(object? o) => o is not Credential c || !ExternalCredentials.HasSecret(c);
    internal static bool NoUserSecret(object? o) => o is not Credential { Username.Length: > 0 } c || !ExternalCredentials.HasSecret(c);

    /// <summary>The credential box with "🔍 Find…" beside it (0.6.19): this vault, Bitwarden, Secrets Manager and Password Safe.</summary>
    private static DockPanel CredRow(ComboBox cred)
    {
        var find = new Button { Content = "🔍 Find…", Margin = new Thickness(6, 0, 0, 0), ToolTip = "Search this vault, Bitwarden, Secrets Manager and Password Safe" };
        find.SetResourceReference(StyleProperty, "SmallButton");
        find.Click += (_, _) =>
        {
            if (CredentialPicker.Pick(CredentialKind.Password, "this infrastructure provider") is not { } picked) return;
            var items = (cred.ItemsSource as IEnumerable<Credential>)?.ToList() ?? new List<Credential>();
            if (!items.Contains(picked)) items.Add(picked);
            cred.ItemsSource = items;
            cred.SelectedItem = picked;
        };
        var row = new DockPanel();
        DockPanel.SetDock(find, Dock.Right);
        row.Children.Add(find); row.Children.Add(cred);
        return row;
    }

    /// <summary>The enrolment string into the vault (a credential of its own) and the host key pin into Known hosts, so the
    /// SSH connection refuses any other server key.</summary>
    private static void SavePortalSecrets(ProviderConfig c, string enrolment)
    {
        if (enrolment.Length > 0)
        {
            var cr = AppData.Current.Credentials.FirstOrDefault(x => x.Id == c.CredentialId) ?? new Credential { Name = c.Name + " — dcx-ctl enrolment", Kind = CredentialKind.Password, Username = "dcx-ctl" };
            cr.Secret = enrolment.Trim();
            if (!AppData.Current.Credentials.Contains(cr)) AppData.Current.Credentials.Add(cr);
            c.CredentialId = cr.Id;
        }
        if (AppData.Current.FindHost(c.HostId) is { } h)
        {
            var key = $"{h.Address}:{h.Port}";
            var fp = c.Endpoint.Replace("SHA256:", "").TrimEnd('=');
            AppData.Current.KnownHosts.RemoveAll(k => k.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            AppData.Current.KnownHosts.Add(new KnownHost { Key = key, Algorithm = "ssh-ed25519", Fingerprint = fp });
        }
    }

    /// <summary>Self-test seam: use this provider instance (e.g. with a fake HTTP handler) for its config.</summary>
    internal Button? AddButtonForTest => _add;
    internal void InjectForTest(ProviderConfig c, IInfraProvider p) => _state[c.Id] = (p, null, Array.Empty<InfraObject>(), DateTime.MinValue);
    internal void SelectForTest(ProviderConfig c) => _providers.SelectedItem = _providers.Items.OfType<FrameworkElement>().FirstOrDefault(e => e.Tag == c);
    internal void SelectObjectForTest(string name) { if (_objects.ItemsSource is IEnumerable<InfraObject> items && items.FirstOrDefault(o => o.Name == name) is { } o) _objects.SelectedItem = o; }

    private async Task InstallHyperVModuleAsync(ProviderConfig c, HyperVSetup.Edition edition)
    {
        if (!Dialogs.Confirm("Install the Hyper-V module", HyperVSetup.ModuleFix(edition).Why + "\n\nWindows asks for admin approval (UAC), then installs it. Nothing else changes.", "Install")) return;
        _status.Text = "installing the Hyper-V PowerShell module…";
        var why = await HyperVSetup.InstallModuleAsync(edition, CancellationToken.None);
        _status.Text = why == null ? "✓ Hyper-V module installed — checking again…" : "✕ " + why;
        if (why == null) { _state.Remove(c.Id); _ = RefreshAsync(); }
    }

    // ---- locks (0.5.1 #10) --------------------------------------------------------------------------------------------

    /// <summary>True while a lock covers this provider and has not been opened.</summary>
    private static bool Closed(ProviderConfig c) => Locks.IsLocked("infra:" + c.Id) && !Locks.IsOpen("infra:" + c.Id);

    private async Task UnlockAsync(ProviderConfig c, Action? then = null)
    {
        if (!await LockGate.EnsureAsync("infra:" + c.Id, "open " + c.Name)) return;
        if (then != null) then(); else if (Sel == c) { ShowProvider(); _ = RefreshAsync(onlyIfStale: true); }
    }

    private void OnLocksChanged() => Dispatcher.InvokeAsync(() =>
    {
        if (!IsLoaded) return;
        Reload();
        if (Sel is { } c && Closed(c)) ShowProvider();
    });

    public void Dispose()
    {
        Locks.Changed -= OnLocksChanged;
        _timer.Stop(); _cts?.Cancel();
        foreach (var s in _state.Values) (s.P as IDisposable)?.Dispose();
        if (Current == this) Current = null;
    }
}
