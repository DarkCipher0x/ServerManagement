using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using DcxManager.Services;
using DcxManager.Ssh;
using DcxManager.Vpn;

namespace DcxManager.Views;

/// <summary>Saved SSH port forwards: local (-L), remote (-R) and SOCKS5 (-D). Auto-start ones start with the app.</summary>
public sealed class TunnelsView : UserControl, IDisposable
{
    private readonly ListView _list = new();
    private static bool _autoStarted;

    public TunnelsView()
    {
        var root = new DockPanel { Margin = new Thickness(14, 12, 14, 12) };
        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        head.Children.Add(new TextBlock { Text = "Tunnels", Style = (Style)FindResource("H1") });
        head.Children.Add(new TextBlock { Text = "Reach services that only listen on a server's localhost (databases, admin panels, Ollama), expose a local port to a server, or browse through a server with a SOCKS5 proxy.", Style = (Style)FindResource("Hint"), Margin = new Thickness(0, 4, 0, 0) });
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        void B(string t, Func<Task> h, string? style = null) { var b = new Button { Content = t, Margin = new Thickness(0, 0, 6, 4) }; if (style != null) b.Style = (Style)FindResource(style); b.Click += async (_, _) => await h(); bar.Children.Add(b); }
        B("＋ Add", () => { Edit(null); return Task.CompletedTask; }, "PrimaryButton");
        B("✎ Edit", () => { if (Sel is { } t) Edit(t); return Task.CompletedTask; });
        B("▶ Start", StartAsync);
        B("■ Stop", () => { if (Sel is { } t) TunnelManager.Stop(t); return Task.CompletedTask; });
        B("⧉ Copy address", () => { if (Sel is { } t) Clipboard.SetText($"{(t.BindAddress == "0.0.0.0" ? "127.0.0.1" : t.BindAddress)}:{t.BindPort}"); return Task.CompletedTask; });
        B("✕ Delete", () =>
        {
            if (Sel is not { } t || !Dialogs.Confirm("Delete tunnel", $"Delete “{t.Name}”?", "Delete", danger: true)) return Task.CompletedTask;
            TunnelManager.Stop(t);
            AppData.Current.Tunnels.Remove(t);
            AppData.Save();
            return Task.CompletedTask;
        }, "DangerButton");
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);

        var gv = new GridView();
        void Col(string h, string p, double w) => gv.Columns.Add(new GridViewColumn { Header = h, Width = w, DisplayMemberBinding = new Binding(p) });
        Col("Name", "Name", 180); Col("Type", "Kind", 70); Col("Via host", "HostName", 170); Col("Forward", "Summary", 320); Col("Auto", "AutoStart", 50); Col("Status", "Status", 260);
        _list.View = gv;
        _list.MouseDoubleClick += async (_, _) => await StartAsync();
        Menus.ForList(_list, this, () => Sel is not { } t ? Array.Empty<object?>() : new object?[]
        {
            Menus.Item("Copy equivalent ssh command", () => Menus.Clip(SshEquivalent(t)), tip: "The OpenSSH command line that does the same forward"),
            Menus.Item(t.AutoStart ? "Auto-start: on (turn off)" : "Auto-start: off (turn on)", () => { t.AutoStart = !t.AutoStart; AppData.Save(); }),
            Menus.Item("⌨ Terminal on the host", () => { if (AppData.Current.FindHost(t.HostId) is { } h) MainWindow.Instance.OpenTerminal(h); }, enabled: AppData.Current.FindHost(t.HostId) != null),
            Menus.AssistItem("✦ Explain this tunnel with AI", a => a.AskAsync("tunnel " + t.Name,
                SafeMetadata.Tunnel(t) + "\nEquivalent OpenSSH command: " + SshEquivalent(t),
                "Explain what this SSH tunnel does, who can reach the forwarded port (bind address exposure), typical uses, and any security concerns or better alternatives.")),
        });
        // 0.3.0 §24: VPN tunnels (WireGuard / OpenVPN) below the SSH forwards.
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(_list, 0); grid.Children.Add(_list);
        var vpnHead = new StackPanel { Margin = new Thickness(0, 14, 0, 6) };
        vpnHead.Children.Add(new TextBlock { Text = "VPN tunnels", Style = (Style)FindResource("H2") });
        vpnHead.Children.Add(new TextBlock { Text = "WireGuard and OpenVPN profiles imported from a .conf / .ovpn. Keys live in the vault; the configuration is kept without them. Routes decide what goes through the tunnel (split tunnel by default). A host's editor can name a profile to bring up before connecting. The engines need admin approval (UAC) to install a tunnel.", Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6) });
        var vbar = new WrapPanel();
        void V(string t, Func<Task> h, string? style = null) { var b = new Button { Content = t, Margin = new Thickness(0, 0, 6, 4) }; if (style != null) b.Style = (Style)FindResource(style); b.Click += async (_, _) => { try { await h(); } catch (Exception ex) { Dialogs.Error("VPN", ex.Message, explain: false); } }; vbar.Children.Add(b); }
        V("⇩ Import .conf / .ovpn…", ImportVpnAsync, "PrimaryButton");
        V("✎ Routes & options…", () => { if (SelVpn is { } p) EditVpn(p); return Task.CompletedTask; });
        V("▲ Up", async () => { if (SelVpn is { } p) await VpnManager.UpAsync(p); });
        V("▼ Down", async () => { if (SelVpn is { } p) await VpnManager.DownAsync(p); });
        V("ⓘ Status", async () => { if (SelVpn is { } p) await ShowVpnStatusAsync(p); });
        V("✕ Delete", () =>
        {
            if (SelVpn is not { } p || !Dialogs.Confirm("Delete VPN profile", $"Delete “{p.Name}” and its keys from the vault?", "Delete", danger: true)) return Task.CompletedTask;
            if (VpnManager.IsUp(p)) { Dialogs.Info("VPN", "Bring the tunnel down first."); return Task.CompletedTask; }
            // The profile's own keys (made when it was imported) go with it; the sign-in credential was picked from the vault
            // (0.6.19: perhaps a Bitwarden / Password Safe link used elsewhere too) and stays.
            foreach (var id in new[] { p.KeyCredentialId, p.PskCredentialId }) if (id != null) AppData.Current.Credentials.RemoveAll(c => c.Id == id && !c.IsExternal);
            foreach (var h in AppData.Current.Hosts.Where(h => h.VpnProfileId == p.Id)) h.VpnProfileId = null;
            AppData.Current.VpnProfiles.Remove(p); AppData.Save(); AuditLog.Write("you", "vpn", "profile deleted", p.Name);
            return Task.CompletedTask;
        }, "DangerButton");
        vpnHead.Children.Add(vbar);
        Grid.SetRow(vpnHead, 1); grid.Children.Add(vpnHead);
        var vgv = new GridView();
        void VCol(string h, string p, double w) => vgv.Columns.Add(new GridViewColumn { Header = h, Width = w, DisplayMemberBinding = new Binding(p) });
        VCol("Name", "Name", 180); VCol("Kind", "KindTitle", 90); VCol("Routes", "RoutesText", 300); VCol("Auto", "AutoStart", 50); VCol("State", "LastState", 260);
        _vpnList.View = vgv;
        Menus.ForList(_vpnList, this, () => SelVpn is not { } p ? Array.Empty<object?>() : new object?[]
        {
            Menus.Item(p.AutoStart ? "Auto-start: on (turn off)" : "Auto-start: off (turn on)", () => { p.AutoStart = !p.AutoStart; AppData.Save(); }),
            Menus.Item("Copy configuration (without keys)", () => Menus.Clip(p.Config)),
            Menus.Item("Hosts that connect through it", () => Dialogs.Info("VPN", string.Join("\n", AppData.Current.Hosts.Where(h => h.VpnProfileId == p.Id).Select(h => h.Display).DefaultIfEmpty("(none — pick it in a host's editor under VPN)")))),
        });
        Grid.SetRow(_vpnList, 2); grid.Children.Add(_vpnList);
        root.Children.Add(grid);
        Content = root;

        AppData.Changed += Reload;
        VpnManager.Changed += OnVpn;
        Reload();
    }

    private readonly ListView _vpnList = new();
    private VpnProfile? SelVpn => _vpnList.SelectedItem as VpnProfile;
    private void OnVpn(VpnProfile _) => Reload();

    private async Task ImportVpnAsync()
    {
        if (!Restrictions.Check("tunnel.add")) return;
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "VPN configuration|*.conf;*.ovpn|All files|*.*", Title = "Import a WireGuard .conf or OpenVPN .ovpn" };
        if (dlg.ShowDialog() != true) return;
        var text = await System.IO.File.ReadAllTextAsync(dlg.FileName);
        var p = VpnManager.Import(text, dlg.FileName);
        if (Dialogs.Prompt("Import VPN", "Name for the profile:", p.Name) is not { Length: > 0 } name) return;
        p.Name = name.Trim();
        if (p.Kind == "openvpn" && p.Config.Contains("auth-user-pass"))
        {
            var creds = AppData.Current.Credentials.Where(c => c.Kind == CredentialKind.Password).ToList();
            var pick = creds.Count == 0 ? null : Dialogs.Pick("Import VPN", "This OpenVPN profile needs a user name and password (auth-user-pass). Which vault credential?", creds, c => c.Display);
            if (pick == null) { Dialogs.Info("Import VPN", "Add a password credential for it in Credentials and pick it under Routes & options."); }
            else p.AuthCredentialId = pick.Id;
        }
        AppData.Current.VpnProfiles.Add(p);
        AppData.Save();
        AuditLog.Write("you", "vpn", "profile imported", $"{p.Name} ({p.KindTitle}) · keys to the vault");
        var im = VpnConfig.Import(text, dlg.FileName);
        Dialogs.Info("VPN imported", $"“{p.Name}” ({p.KindTitle}) is ready. Keys went to the vault; the configuration is stored without them.\nRoutes through the tunnel: {p.RoutesText}." + (im.Warnings.Count > 0 ? "\n\n" + string.Join("\n", im.Warnings) : "") + "\n\nIf you wish to delete the original file now that its keys are in the vault, do it yourself — the app never deletes your files.");
        EditVpn(p);
    }

    private void EditVpn(VpnProfile p)
    {
        var w = Dialogs.MakeWindow("VPN — " + p.Name, 520);
        var s = new StackPanel { Margin = new Thickness(18) };
        TextBlock L(string t) => new() { Text = t, Style = (Style)FindResource("Label"), Margin = new Thickness(0, 8, 0, 4) };
        var all = new CheckBox { Content = "Route everything through the tunnel (0.0.0.0/0) — otherwise only the routes below", IsChecked = p.RouteAll };
        var routes = new TextBox { Text = string.Join("\n", p.Routes), AcceptsReturn = true, MinHeight = 70, Padding = new Thickness(5, 4, 5, 4), ToolTip = "One per line, e.g. 10.10.0.0/16 or 192.168.50.0/24 — the networks reached through the tunnel" };
        var dns = new TextBox { Text = string.Join(", ", p.Dns), Padding = new Thickness(5, 4, 5, 4), ToolTip = "DNS servers while the tunnel is up (comma-separated), empty = unchanged" };
        var idle = new TextBox { Text = p.IdleMinutes.ToString(), Width = 80, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(5, 4, 5, 4) };
        var auto = new CheckBox { Content = "Bring it up when the app starts", IsChecked = p.AutoStart, Margin = new Thickness(0, 8, 0, 0) };
        var auth = new ComboBox { Margin = new Thickness(0, 0, 0, 4) };
        var creds = AppData.Current.Credentials.Where(c => c.Kind == CredentialKind.Password).ToList();
        auth.Items.Add("(none)"); foreach (var c in creds) auth.Items.Add(c.Display);
        auth.SelectedIndex = Math.Max(0, creds.FindIndex(c => c.Id == p.AuthCredentialId) + 1);
        s.Children.Add(all); s.Children.Add(L("ROUTES THROUGH THE TUNNEL")); s.Children.Add(routes); s.Children.Add(L("DNS (optional)")); s.Children.Add(dns);
        if (p.Kind == "openvpn")
        {
            s.Children.Add(L("USER NAME / PASSWORD CREDENTIAL (auth-user-pass)")); s.Children.Add(auth);
            // 0.6.19: 🔍 Find… searches this vault, Bitwarden, Secrets Manager and Password Safe.
            var find = new Button { Content = "🔍 Find a credential…", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 4) };
            find.Click += (_, _) =>
            {
                if (CredentialPicker.Pick(CredentialKind.Password, "the VPN “" + p.Name + "”", p.Name) is not { } picked) return;
                if (creds.FindIndex(c => c.Id == picked.Id) is var i && i < 0) { creds.Add(picked); auth.Items.Add(picked.Display); i = creds.Count - 1; }
                auth.SelectedIndex = i + 1;
            };
            s.Children.Add(find);
        }
        s.Children.Add(L("BRING DOWN AFTER MINUTES WITHOUT USE (0 = stay up)")); s.Children.Add(idle); s.Children.Add(auto);
        var ok = new Button { Content = "Save", Style = (Style)FindResource("PrimaryButton"), IsDefault = true };
        var bs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        bs.Children.Add(ok); bs.Children.Add(new Button { Content = "Cancel", IsCancel = true }); s.Children.Add(bs);
        w.Content = s;
        ok.Click += (_, _) =>
        {
            try
            {
                var list = VpnRoutes.Parse(routes.Text);
                foreach (var d in dns.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) if (!System.Net.IPAddress.TryParse(d, out _)) throw new ArgumentException("DNS: " + d);
                if (all.IsChecked == true && !Dialogs.Confirm("Route everything", "All traffic from this PC goes through the tunnel while it is up, including this app's other connections. Continue?", "Route everything", danger: true)) return;
                p.RouteAll = all.IsChecked == true; p.Routes = list; p.Dns = dns.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                p.IdleMinutes = int.TryParse(idle.Text, out var m) ? Math.Clamp(m, 0, 1440) : 0; p.AutoStart = auto.IsChecked == true;
                p.AuthCredentialId = auth.SelectedIndex > 0 ? creds[auth.SelectedIndex - 1].Id : null;
                AppData.Save(); AuditLog.Write("you", "vpn", "profile changed", $"{p.Name} · routes: {p.RoutesText}");
                w.DialogResult = true;
            }
            catch (ArgumentException ex) { Dialogs.Error("VPN", ex.Message, explain: false); }
        };
        w.ShowDialog();
    }

    private async Task ShowVpnStatusAsync(VpnProfile p)
    {
        var st = await VpnManager.StatusAsync(p);
        var routes = VpnManager.InstalledRoutes(p);
        Dialogs.ShowText("VPN — " + p.Name, $"{p.KindTitle} · {st.State}\nendpoint: {st.Endpoint}\nlast handshake: {(st.LastHandshake is { } h ? h.ToString("HH:mm:ss") : "—")}\nreceived {Fmt.Bytes(st.RxBytes)} · sent {Fmt.Bytes(st.TxBytes)}\n{st.Detail}\n\nRoutes asked for: {p.RoutesText}\nRoutes installed through the tunnel's interface ({routes.Count}):\n" + string.Join("\n", routes.Select(r => $"  {r.Prefix}/{r.Bits} via {r.NextHop} metric {r.Metric}")));
    }

    /// <summary>Called once at start-up.</summary>
    public static void AutoStartAll()
    {
        if (_autoStarted) return;
        _autoStarted = true;
        foreach (var t in AppData.Current.Tunnels.Where(t => t.AutoStart)) _ = SafeStart(t);
    }

    public void Dispose() { AppData.Changed -= Reload; VpnManager.Changed -= OnVpn; }

    private void Reload()
    {
        _list.ItemsSource = null;
        _list.ItemsSource = AppData.Current.Tunnels;
        _vpnList.ItemsSource = null;
        _vpnList.ItemsSource = AppData.Current.VpnProfiles.Select(p => { if (VpnManager.IsUp(p) && p.LastState.Length == 0) p.LastState = "up"; return p; }).ToList();
    }

    private TunnelDef? Sel => _list.SelectedItem as TunnelDef;

    private static string SshEquivalent(TunnelDef t)
    {
        var h = AppData.Current.FindHost(t.HostId);
        var via = h == null ? "user@host" : $"{(h.Port != 22 ? $"-p {h.Port} " : "")}{h.Username}@{h.Address}";
        var fwd = t.Kind switch
        {
            TunnelKind.Local => $"-L {t.BindAddress}:{t.BindPort}:{t.DestHost}:{t.DestPort}",
            TunnelKind.Remote => $"-R {t.BindAddress}:{t.BindPort}:{t.DestHost}:{t.DestPort}",
            _ => $"-D {t.BindAddress}:{t.BindPort}",
        };
        return $"ssh -N {fwd} {via}";
    }

    private async Task StartAsync() { if (Sel is { } t) await SafeStart(t); }

    private static async Task SafeStart(TunnelDef t)
    {
        try { await TunnelManager.StartAsync(t); }
        catch (Exception ex) { t.Status = "error: " + ex.Message.Split('\n')[0]; }
    }

    private void Edit(TunnelDef? existing)
    {
        if (existing == null && !Restrictions.Check("tunnel.add")) return;
        if (AppData.Current.Hosts.Count == 0) { Dialogs.Info("Tunnels", "Add a host first."); return; }
        var w = Dialogs.MakeWindow(existing == null ? "New tunnel" : "Edit tunnel", 480);
        var p = new StackPanel { Margin = new Thickness(18) };
        TextBlock L(string t) => new() { Text = t, Style = (Style)FindResource("Label") };
        TextBox T(string v) => new() { Text = v, Padding = new Thickness(5, 4, 5, 4), Margin = new Thickness(0, 0, 0, 10) };
        var name = T(existing?.Name ?? "");
        var host = new ComboBox { ItemsSource = AppData.Current.Hosts, DisplayMemberPath = "Display", Margin = new Thickness(0, 0, 0, 10) };
        host.SelectedItem = AppData.Current.FindHost(existing?.HostId) ?? AppData.Current.Hosts[0];
        var kind = new ComboBox { ItemsSource = Enum.GetValues<TunnelKind>(), SelectedItem = existing?.Kind ?? TunnelKind.Local, Margin = new Thickness(0, 0, 0, 10) };
        var bind = T(existing?.BindAddress ?? "127.0.0.1");
        var bport = T(existing?.BindPort.ToString() ?? "");
        var dhost = T(existing?.DestHost ?? "127.0.0.1");
        var dport = T(existing?.DestPort.ToString() ?? "");
        var auto = new CheckBox { Content = "Start automatically when the app opens", IsChecked = existing?.AutoStart ?? false, Margin = new Thickness(0, 4, 0, 0) };
        var hint = new TextBlock { Style = (Style)FindResource("Hint"), Margin = new Thickness(0, 0, 0, 10) };
        void Hint() => hint.Text = (TunnelKind)kind.SelectedItem switch
        {
            TunnelKind.Local => "Local: connections to bind:port on this PC go through the server to destination:port (as seen from the server).",
            TunnelKind.Remote => "Remote: the server listens on bind:port and forwards to destination:port on this PC's side.",
            _ => "Dynamic: a SOCKS5 proxy on bind:port; point a browser at it to browse as the server.",
        };
        kind.SelectionChanged += (_, _) => Hint(); Hint();
        foreach (var (l, c) in new (string, UIElement)[] { ("NAME", name), ("VIA HOST", host), ("TYPE", kind), ("", hint), ("BIND ADDRESS", bind), ("BIND PORT", bport), ("DESTINATION HOST", dhost), ("DESTINATION PORT (not used for SOCKS)", dport) })
        { if (l.Length > 0) p.Children.Add(L(l)); p.Children.Add(c); }
        p.Children.Add(auto);
        var ok = new Button { Content = "Save", Style = (Style)FindResource("PrimaryButton"), IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var bs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        bs.Children.Add(ok); bs.Children.Add(cancel); p.Children.Add(bs);
        w.Content = p;
        ok.Click += (_, _) =>
        {
            if (!int.TryParse(bport.Text, out var bp) || bp is < 1 or > 65535) { Dialogs.Error("Tunnel", "Bind port must be 1–65535."); return; }
            int.TryParse(dport.Text, out var dp);
            if ((TunnelKind)kind.SelectedItem != TunnelKind.Dynamic && dp is < 1 or > 65535) { Dialogs.Error("Tunnel", "Destination port must be 1–65535."); return; }
            if (bind.Text.Trim() == "0.0.0.0" && !Dialogs.Confirm("Open to the network", "Binding to 0.0.0.0 lets other machines on your network use this tunnel. Continue?", "Continue", danger: true)) return;
            w.DialogResult = true;
        };
        if (w.ShowDialog() != true) return;
        var t = existing ?? new TunnelDef();
        if (existing != null) TunnelManager.Stop(existing);
        t.Name = name.Text.Trim().Length > 0 ? name.Text.Trim() : $"{kind.SelectedItem} {bport.Text}";
        t.HostId = ((HostEntry)host.SelectedItem).Id; t.Kind = (TunnelKind)kind.SelectedItem;
        t.BindAddress = bind.Text.Trim(); t.BindPort = int.Parse(bport.Text);
        t.DestHost = dhost.Text.Trim(); t.DestPort = int.TryParse(dport.Text, out var d) ? d : 0; t.AutoStart = auto.IsChecked == true;
        if (existing == null) AppData.Current.Tunnels.Add(t);
        AppData.Save();
    }
}
