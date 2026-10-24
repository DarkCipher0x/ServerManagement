using System.Windows;
using System.Windows.Controls;
using DcxManager.Bitwarden;
using DcxManager.Services;

namespace DcxManager.Views;

/// <summary>
/// VAULT → Bitwarden (0.6.14, ADR-093): the vault's logins and SSH keys and the Secrets Manager secrets a machine account
/// may read. A host signs in with one (linked for good, or for one connection): the password or key is read into memory
/// when it connects and never saved here. Lists show names only; a password, key or secret value is read one at a time,
/// for a session or a copy that is cleared again and kept out of clipboard history.
/// </summary>
public sealed class BitwardenView : UserControl
{
    private readonly DataGrid _items = new() { IsReadOnly = true, AutoGenerateColumns = false, Height = 280, HeadersVisibility = DataGridHeadersVisibility.Column, SelectionMode = DataGridSelectionMode.Single };
    private readonly DataGrid _secrets = new() { IsReadOnly = true, AutoGenerateColumns = false, Height = 260, HeadersVisibility = DataGridHeadersVisibility.Column, SelectionMode = DataGridSelectionMode.Single };
    private readonly ListBox _linked = new() { Height = 200 };
    private readonly ComboBox _projects = new() { Width = 220 };
    private readonly TextBox _search = Hint.Set(new TextBox { Width = 240, Padding = new Thickness(5, 4, 5, 4) }, "Search the vault…");
    private readonly TextBlock _state = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6) };
    /// <summary>0.6.16: what's still needed, step by step (DarkCipher0x tried it without the CLI and nothing said so).</summary>
    private readonly StackPanel _setup = new() { Margin = new Thickness(0, 2, 0, 8) };
    private readonly TextBlock _forHost = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6), Visibility = Visibility.Collapsed };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6) };
    private readonly List<Button> _buttons = new();
    private List<SmProject> _projectList = new();

    /// <summary>The screen that is open (one at a time).</summary>
    internal static BitwardenView? Current { get; private set; }
    /// <summary>The saved host the next "Link" is for (set from the host's menu).</summary>
    private static string? _pendingHost;

    public BitwardenView()
    {
        Current = this;
        var root = new StackPanel { Margin = new Thickness(14, 12, 14, 12) };
        root.Children.Add(new TextBlock { Text = "Bitwarden", Style = (Style)FindResource("H1") });
        root.Children.Add(new TextBlock { Text = "Sign hosts in with your vault's logins and SSH keys or with Secrets Manager secrets: read into memory each time a session connects, never saved here. Lists show names only. Settings → Integrations → Bitwarden.", Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8) });
        root.Children.Add(_setup);
        root.Children.Add(_state);
        root.Children.Add(Bar(Run("🔓 Unlock…", UnlockAsync, primary: true), Btn("🔒 Lock", () => { BitwardenVault.Lock("locked by you"); ShowState(); }), Run("↻ Sync", async () => { await BitwardenVault.SyncAsync(); _status.Text = "✓ Synced."; await ShowStateAsync(); }),
            Btn("⚙ Settings", () => { MainWindow.Instance.Navigate("settings"); SettingsView.Current?.RevealIntegration("Bitwarden"); })));
        _forHost.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        root.Children.Add(_forHost);
        var tabs = new TabControl();

        void Col(DataGrid g, string h, string p, double w) => g.Columns.Add(new DataGridTextColumn { Header = h, Binding = new System.Windows.Data.Binding(p), Width = w });
        Col(_items, "Name", nameof(VaultItem.Name), 220); Col(_items, "Type", nameof(VaultItem.TypeText), 80); Col(_items, "User name", nameof(VaultItem.Username), 160); Col(_items, "Address", nameof(VaultItem.Uri), 220); Col(_items, "Vault", nameof(VaultItem.Owner), 100); Col(_items, "Key fingerprint", nameof(VaultItem.Fingerprint), 200);
        _items.MouseDoubleClick += async (_, _) => { if (_pendingHost != null) await Guard(LinkItemAsync); };
        _search.KeyDown += async (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) await Guard(LoadItemsAsync); };
        // 0.6.20: the list is held in memory once the vault is unlocked, so typing filters it at once (no bw per search).
        var typing = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        typing.Tick += async (_, _) => { typing.Stop(); if (BitwardenVault.ItemsHeld) await Guard(LoadItemsAsync); };
        _search.TextChanged += (_, _) => { typing.Stop(); typing.Start(); };
        Action changed = () => Dispatcher.BeginInvoke(async () => { if (IsLoaded && BitwardenVault.ItemsHeld) await Guard(LoadItemsAsync); });
        Loaded += (_, _) => BitwardenVault.ItemsChanged += changed;
        Unloaded += (_, _) => BitwardenVault.ItemsChanged -= changed;
        var t1 = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        t1.Children.Add(Bar(_search, Run("↻ Search", LoadItemsAsync, primary: true)));
        t1.Children.Add(_items);
        t1.Children.Add(Bar(Run("⚿ Connect a host with it…", ConnectItemAsync), Run("🔗 Link to a saved host…", LinkItemAsync), Run("＋ Add as a vault credential", AddItemCredentialAsync), Run("⧉ Copy user name", CopyUserAsync), Run("⧉ Copy password / key", CopyItemAsync)));
        tabs.Items.Add(new TabItem { Header = "Vault", Content = t1 });

        Col(_secrets, "Key", nameof(SmSecret.Key), 240); Col(_secrets, "Note", nameof(SmSecret.Note), 300); Col(_secrets, "Id", nameof(SmSecret.Id), 260);
        var t2 = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        t2.Children.Add(Bar(new TextBlock { Text = "Project", VerticalAlignment = VerticalAlignment.Center }, _projects, Run("↻ Projects", LoadProjectsAsync), Run("↻ Secrets", LoadSecretsAsync, primary: true)));
        t2.Children.Add(_secrets);
        t2.Children.Add(Bar(Run("⚿ Connect a host with it…", ConnectSecretAsync), Run("🔗 Link to a saved host…", LinkSecretAsync), Run("＋ Add as a vault credential", AddSecretCredentialAsync), Run("⧉ Copy value", CopySecretAsync)));
        tabs.Items.Add(new TabItem { Header = "Secrets Manager", Content = t2 });

        var t3 = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        t3.Children.Add(new TextBlock { Text = "HOSTS THAT SIGN IN WITH BITWARDEN (only the item's id and name are stored on the host)", Style = (Style)FindResource("Label"), Margin = new Thickness(0, 0, 0, 4) });
        t3.Children.Add(_linked);
        t3.Children.Add(Bar(Btn("Unlink selected", Unlink), Btn("↻ Refresh", ShowLinked)));
        tabs.Items.Add(new TabItem { Header = "Linked hosts", Content = t3 });

        root.Children.Add(tabs);
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        root.Children.Add(_status);
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Loaded += (_, _) => { BitwardenVault.Changed += OnChanged; ShowState(); ShowLinked(); ShowPending(); };
        Unloaded += (_, _) => BitwardenVault.Changed -= OnChanged;
    }

    private static AppSettings S => AppSettings.Current;

    /// <summary>From a host's menu: opens this screen ready to link an item to that host.</summary>
    public static void LinkFor(HostEntry host)
    {
        _pendingHost = host.Id;
        MainWindow.Instance.Navigate("bitwarden");
        Current?.ShowPending();
    }

    private HostEntry? Pending => _pendingHost is { } id ? AppData.Current.Hosts.FirstOrDefault(h => h.Id == id) : null;

    private void ShowPending()
    {
        if (Pending is { } h)
        {
            _forHost.Text = $"Choosing the Bitwarden sign-in for {h.Display}: pick a login, SSH key or secret, then 🔗 Link (or double-click it).";
            _forHost.Visibility = Visibility.Visible;
        }
        else _forHost.Visibility = Visibility.Collapsed;
    }

    private void OnChanged() => Dispatcher.BeginInvoke(ShowState);

    /// <summary>The set-up checklist: the CLI (install it from here), the API key, the vault, Secrets Manager. Hidden once
    /// everything needed is in place and the vault has been unlocked.</summary>
    private void ShowSetup()
    {
        _setup.Children.Clear();
        var rows = new List<(bool Ok, string Title, string Detail, Button? Action)>();
        string? bw = Fake ? "(scripted)" : BitwardenCli.Find(BitwardenCli.Tool.Bw);
        BitwardenCli.Trust? trust = null;
        if (bw != null && !Fake) try { trust = BitwardenCli.Check(bw); } catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { }
        if (bw == null)
            rows.Add((false, "Bitwarden CLI", "Not installed. ServerManagement uses Bitwarden's own command-line tool (bw.exe) to read your vault; the desktop app doesn't include it.",
                Run("⬇ Install Bitwarden CLI", InstallCliAsync, primary: true)));
        else if (trust is { Ok: false })
            rows.Add((false, "Bitwarden CLI", trust.Why, Btn("Open CLI settings", OpenCliSettings)));
        else rows.Add((true, "Bitwarden CLI", Fake ? "ready" : $"{bw} · {trust?.Why}", null));
        bool key = (S.ObserveSecrets.GetValueOrDefault("bw:clientsecret") ?? "").Length > 0;
        rows.Add((key, "Personal API key", key ? "stored (encrypted)" : "Needed to sign in: web vault → Settings → Security → Keys → View API key, then Settings → Integrations → Bitwarden → Server & sign-in.",
            key ? null : Btn("Add the API key", OpenCliSettings)));
        rows.Add((BitwardenVault.Unlocked, "Vault", BitwardenVault.Unlocked ? "unlocked" : "locked: 🔓 Unlock asks for your master password (never stored)", null));
        bool sm = BitwardenSecrets.HasToken, bws = Fake || BitwardenCli.Find(BitwardenCli.Tool.Bws) != null;
        rows.Add((sm && bws, "Secrets Manager (optional)", sm && bws ? "access token stored, bws.exe found"
            : (sm ? "" : "no machine-account access token yet. ") + (bws ? "" : "bws.exe isn't installed (Bitwarden's sdk-sm releases on GitHub)."), null));
        if (rows.Take(3).All(r => r.Ok)) return;   // ready: the checklist steps aside
        var card = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = new Thickness(14, 10, 14, 10) };
        card.SetResourceReference(Border.BorderBrushProperty, "Line");
        var list = new StackPanel();
        list.Children.Add(new TextBlock { Text = "SET UP BITWARDEN", Style = (Style)FindResource("Label"), Margin = new Thickness(0, 0, 0, 6) });
        foreach (var (ok, title, detail, action) in rows)
        {
            var line = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            var mark = new TextBlock { Text = ok ? "✓" : "○", Width = 22, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Top };
            mark.SetResourceReference(TextBlock.ForegroundProperty, ok ? "Accent" : "Muted");
            DockPanel.SetDock(mark, Dock.Left); line.Children.Add(mark);
            if (action != null) { action.Margin = new Thickness(12, 0, 0, 0); action.VerticalAlignment = VerticalAlignment.Center; DockPanel.SetDock(action, Dock.Right); line.Children.Add(action); }
            var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
            text.Inlines.Add(new System.Windows.Documents.Run(title + "  ") { FontWeight = FontWeights.SemiBold });
            var d = new System.Windows.Documents.Run(detail);
            d.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Muted");
            text.Inlines.Add(d);
            line.Children.Add(text);
            list.Children.Add(line);
        }
        card.Child = list;
        _setup.Children.Add(card);
    }

    private static bool Fake => BitwardenCli.Fake != null;

    private static void OpenCliSettings() { MainWindow.Instance.Navigate("settings"); SettingsView.Current?.RevealIntegration("Bitwarden"); }

    private async Task InstallCliAsync()
    {
        if (!Dialogs.Confirm("Install the Bitwarden CLI", "Install Bitwarden's command-line tool (bw.exe, from Bitwarden Inc.) for your Windows account with winget?\n\nwinget install --id Bitwarden.CLI\n\nIt takes a minute; no administrator approval is needed.", "Install")) return;
        _status.Text = "Installing the Bitwarden CLI with winget… (a minute or so)";
        var path = await BitwardenCli.InstallBwAsync();
        var t = BitwardenCli.Check(path);
        _status.Text = t.Ok ? $"✓ Bitwarden CLI installed: {path} ({t.Why})." : "Installed, but: " + t.Why;
        ShowSetup();
    }

    private void ShowState()
    {
        _state.Text = !S.BitwardenEnabled ? "The Bitwarden integration is off (Settings → Integrations → Bitwarden)."
            : (BitwardenVault.Unlocked ? $"● Vault unlocked ({BitwardenVault.ServerLabel(S.BitwardenServer)}); it locks after {S.BitwardenLockMinutes} min unused, with the app and at exit." : $"○ Vault locked ({BitwardenVault.ServerLabel(S.BitwardenServer)}).")
              + (BitwardenSecrets.HasToken ? " Secrets Manager: access token stored." : " Secrets Manager: no access token.");
        if (S.BitwardenEnabled) ShowSetup(); else _setup.Children.Clear();
    }

    private async Task ShowStateAsync()
    {
        ShowState();
        try { var st = await BitwardenVault.StatusAsync(); _state.Text = st.Text + (BitwardenSecrets.HasToken ? " · Secrets Manager: access token stored." : ""); }
        catch (Exception ex) when (Expected(ex)) { }
    }

    private async Task UnlockAsync()
    {
        if (BitwardenVault.Unlocked) { _status.Text = "The vault is already unlocked."; return; }
        _status.Text = "Unlocking…";
        if (!await BitwardenSessions.EnsureUnlockedAsync("to use it in ServerManagement")) { _status.Text = "Not unlocked."; return; }
        _status.Text = "✓ Unlocked.";
        await ShowStateAsync();
        await LoadItemsAsync();
    }

    private async Task LoadItemsAsync()
    {
        if (!await BitwardenSessions.EnsureUnlockedAsync("to list its items")) { _status.Text = "The vault stayed locked."; return; }
        if (!BitwardenVault.ItemsHeld) _status.Text = "Reading the vault… (once per unlock; searches after that are instant)";
        var list = await BitwardenVault.ItemsAsync(_search.Text);
        _items.ItemsSource = list;
        _status.Text = $"{list.Count} login(s) and SSH key(s){(_search.Text.Trim().Length > 0 ? " matching" : "")}.";
    }

    private async Task LoadProjectsAsync()
    {
        _projectList = await BitwardenSecrets.ProjectsAsync();
        _projects.ItemsSource = new object[] { "All projects" }.Concat(_projectList).ToList();
        _projects.SelectedIndex = 0;
        _status.Text = $"{_projectList.Count} project(s).";
    }

    private async Task LoadSecretsAsync()
    {
        var pid = _projects.SelectedItem is SmProject p ? p.Id : null;
        var list = await BitwardenSecrets.SecretsAsync(pid);
        _secrets.ItemsSource = list;
        _status.Text = $"{list.Count} secret(s).";
    }

    private VaultItem SelItem => _items.SelectedItem as VaultItem ?? throw new InvalidOperationException("Select a login or SSH key (↻ Search).");
    private SmSecret SelSecret => _secrets.SelectedItem as SmSecret ?? throw new InvalidOperationException("Select a secret (↻ Secrets).");

    /// <summary>The host to use: the one the host menu asked for, else one picked (hosts whose address appears in the item first).</summary>
    private HostEntry? PickHost(string name, string hint, string title)
    {
        if (Pending is { } p) return p;
        var hosts = AppData.Current.Hosts.Where(h => !h.IsAdHoc && !h.IsShared && h.Protocol is "ssh" or "rdp" or "vnc")
            .OrderByDescending(h => hint.Contains(h.Address, StringComparison.OrdinalIgnoreCase) || name.Contains(h.Display, StringComparison.OrdinalIgnoreCase)).ThenBy(h => h.Display).Take(40).ToList();
        if (hosts.Count == 0) throw new InvalidOperationException("No saved SSH, Remote Desktop or VNC host.");
        int i = Dialogs.Choose(title, $"Which host signs in with “{name}”?", hosts.Select(h => $"{h.Display}  ·  {h.Address}").Append("Cancel").ToArray(), hosts.Count);
        return i >= 0 && i < hosts.Count ? hosts[i] : null;
    }

    private void Link(HostEntry host, string link)
    {
        host.BitwardenItem = link;
        AppData.Save();
        AuditLog.Write("you", host.Display, "linked to Bitwarden", BitwardenSessions.Describe(link));
        _status.Text = $"{host.Display} now signs in with {BitwardenSessions.Describe(link)} (read into memory each time it connects). Unlink it in the host's menu or under Linked hosts.";
        _pendingHost = null; ShowPending(); ShowLinked();
    }

    private Task LinkItemAsync() { var i = SelItem; if (PickHost(i.Name, i.Uri, "Link to a saved host") is { } h) Link(h, BitwardenSessions.Link(i)); return Task.CompletedTask; }
    private Task LinkSecretAsync() { var s = SelSecret; if (PickHost(s.Key, s.Note, "Link to a saved host") is { } h) Link(h, BitwardenSessions.Link(s)); return Task.CompletedTask; }

    // 0.6.17 (ADR-096): a vault credential that reads this item when used, so hosts, cloud hosts, infrastructure, portals,
    // sudo and VPN keys can all pick it like any other credential.
    private Task AddItemCredentialAsync()
    {
        var i = SelItem;
        return AddCredential(CredentialSources.New(CredentialSources.Link(i), i.Name, i.Type == 5 ? CredentialKind.PrivateKey : CredentialKind.Password, i.Username, i.Type == 5 ? "ssh" : ""));
    }

    private Task AddSecretCredentialAsync()
    {
        var s = SelSecret;
        return AddCredential(CredentialSources.New(CredentialSources.Link(s), s.Key, CredentialKind.Password, "", "api token"));
    }

    private Task AddCredential(Credential n)
    {
        if (!Restrictions.Check("credential.add")) return Task.CompletedTask;
        var (c, existed) = CredentialSources.AddToVault(n);
        _status.Text = existed
            ? $"“{c.Name}” is already in the vault (VAULT → Credentials), pointing at this item."
            : $"Added “{c.Name}” to the vault (VAULT → Credentials). Pick it for a host, an infrastructure provider, a portal, sudo or a VPN key: its {(n.Kind == CredentialKind.PrivateKey ? "key" : "password")} is read from Bitwarden into memory each time it is used, never stored here.";
        return Task.CompletedTask;
    }

    private void ConnectOnce(HostEntry host, string link)
    {
        var h = host.Clone();
        h.BitwardenItem = link;
        h.PasswordSafeAccount = "";
        _pendingHost = null; ShowPending();
        MainWindow.Instance.OpenTerminal(h);
        _status.Text = $"Connecting {host.Display} with {BitwardenSessions.Describe(link)}…";
    }

    private Task ConnectItemAsync() { var i = SelItem; if (PickHost(i.Name, i.Uri, "Connect a host") is { } h) ConnectOnce(h, BitwardenSessions.Link(i)); return Task.CompletedTask; }
    private Task ConnectSecretAsync() { var s = SelSecret; if (PickHost(s.Key, s.Note, "Connect a host") is { } h) ConnectOnce(h, BitwardenSessions.Link(s)); return Task.CompletedTask; }

    private static void CanCopy() { if (!S.BitwardenAllowCopy) throw new InvalidOperationException("Copying Bitwarden passwords and secrets is switched off on this PC (policy)."); }

    private void Copied(string what, string label)
    {
        AuditLog.Write("you", "bitwarden", $"Bitwarden {what} copied", label);
        _status.Text = $"⧉ {what} of “{label}” copied: cleared from the clipboard in {S.BitwardenClipboardSeconds} s and kept out of clipboard history.";
    }

    private Task CopyUserAsync()
    {
        var i = SelItem;
        if (i.Username.Length == 0) throw new InvalidOperationException($"“{i.Name}” has no user name.");
        Clipboard.SetText(i.Username);
        _status.Text = $"⧉ User name of “{i.Name}” copied.";
        return Task.CompletedTask;
    }

    private async Task CopyItemAsync()
    {
        CanCopy();
        var i = SelItem;
        if (!await BitwardenSessions.EnsureUnlockedAsync("to copy " + i.Name)) return;
        var s = await BitwardenVault.SecretAsync(i.Id);
        if (!SecretClipboard.Copy(s.Secret, S.BitwardenClipboardSeconds)) throw new InvalidOperationException("The clipboard is busy; try again.");
        Copied(s.IsKey ? "SSH key" : "password", i.Name);
    }

    private async Task CopySecretAsync()
    {
        CanCopy();
        var x = SelSecret;
        var s = await BitwardenSecrets.ValueAsync(x.Id);
        if (!SecretClipboard.Copy(s.Secret, S.BitwardenClipboardSeconds)) throw new InvalidOperationException("The clipboard is busy; try again.");
        Copied("secret", x.Key);
    }

    private void ShowLinked()
    {
        _linked.ItemsSource = AppData.Current.Hosts.Where(h => h.BitwardenItem.Length > 0)
            .Select(h => new ListBoxItem { Content = $"{h.Display}  ·  {BitwardenSessions.Describe(h.BitwardenItem)}", Tag = h }).ToList();
    }

    private void Unlink()
    {
        if (_linked.SelectedItem is not ListBoxItem { Tag: HostEntry h }) { _status.Text = "Select a host above."; return; }
        h.BitwardenItem = ""; AppData.Save();
        AuditLog.Write("you", h.Display, "unlinked from Bitwarden");
        _status.Text = $"{h.Display} unlinked.";
        ShowLinked();
    }

    /// <summary>Self-test: search the vault as ↻ Search does; the rows listed.</summary>
    internal async Task<int> SearchForTest(string text) { _search.Text = text; await LoadItemsAsync(); return (_items.ItemsSource as System.Collections.ICollection)?.Count ?? 0; }
    /// <summary>Self-test: the Secrets Manager tab's ↻ Secrets.</summary>
    internal async Task<int> SecretsForTest() { await LoadSecretsAsync(); return (_secrets.ItemsSource as System.Collections.ICollection)?.Count ?? 0; }

    private static bool Expected(Exception ex) => ex is InvalidOperationException or ArgumentException or TimeoutException or OfflineBlockedException or OperationCanceledException
        or System.Text.Json.JsonException or System.ComponentModel.Win32Exception or System.IO.IOException or UnauthorizedAccessException;

    private async Task Guard(Func<Task> a)
    {
        foreach (var x in _buttons) x.IsEnabled = false;
        try { await a(); }
        catch (Exception ex) when (Expected(ex)) { _status.Text = "✕ " + ex.Message; }
        finally { foreach (var x in _buttons) x.IsEnabled = true; }
    }

    private static WrapPanel Bar(params UIElement[] items) { var w = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) }; foreach (var i in items) { if (i is FrameworkElement f) { f.Margin = new Thickness(0, 4, 8, 4); f.VerticalAlignment = VerticalAlignment.Center; } w.Children.Add(i); } return w; }
    private static Button Btn(string t, Action a) { var b = new Button { Content = t }; b.Click += (_, _) => a(); return b; }

    private Button Run(string t, Func<Task> a, bool primary = false)
    {
        var b = new Button { Content = t };
        if (primary) b.Style = (Style)FindResource("PrimaryButton");
        b.Click += async (_, _) => await Guard(a);
        _buttons.Add(b);
        return b;
    }
}
