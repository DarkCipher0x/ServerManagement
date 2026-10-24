using System.Windows;
using System.Windows.Controls;
using DcxManager.BeyondTrust;
using DcxManager.Services;

namespace DcxManager.Views;

/// <summary>
/// SERVERS → BeyondTrust (0.5.0, plan #16): Privileged Remote Access / Remote Support Jump Items (open one in your access
/// console, or add it as a saved host), recent access sessions, Jumpoints and Vault account names; and Password Safe managed
/// accounts, checked out for one session (the password goes straight into that session, in memory) and checked back in.
/// </summary>
public sealed class BeyondTrustView : UserControl, IDisposable
{
    private readonly DataGrid _items = new() { IsReadOnly = true, AutoGenerateColumns = false, Height = 260, HeadersVisibility = DataGridHeadersVisibility.Column, SelectionMode = DataGridSelectionMode.Extended };
    private readonly DataGrid _sessions = new() { IsReadOnly = true, AutoGenerateColumns = false, Height = 260, HeadersVisibility = DataGridHeadersVisibility.Column };
    private readonly ListBox _other = new() { Height = 260 };
    private readonly DataGrid _accounts = new() { IsReadOnly = true, AutoGenerateColumns = false, Height = 220, HeadersVisibility = DataGridHeadersVisibility.Column, SelectionMode = DataGridSelectionMode.Single };
    private readonly ListBox _checkouts = new() { Height = 90 };
    private readonly TextBox _filter = Hint.Set(new TextBox { Width = 220, Padding = new Thickness(5, 4, 5, 4) }, "Filter…");
    private readonly ComboBox _days = new() { Width = 90, ItemsSource = new[] { 1, 7, 30 }, SelectedIndex = 1 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6) };
    private readonly List<Button> _buttons = new();
    private List<JumpItem> _all = new();
    private PasswordSafeClient? _ps;
 

    public BeyondTrustView()
    {
        var root = new StackPanel { Margin = new Thickness(14, 12, 14, 12) };
        root.Children.Add(new TextBlock { Text = "BeyondTrust", Style = (Style)FindResource("H1") });
        root.Children.Add(new TextBlock { Text = "Privileged Remote Access / Remote Support (read-only: Jump Items, sessions, Jumpoints, Vault account names) and Password Safe (a managed account checked out for one session, then checked in). Settings → Integrations → BeyondTrust.", Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8) });
        var tabs = new TabControl();

        void Col(DataGrid g, string h, string p, double w) => g.Columns.Add(new DataGridTextColumn { Header = h, Binding = new System.Windows.Data.Binding(p), Width = w });
        Col(_items, "Name", nameof(JumpItem.Name), 200); Col(_items, "Kind", nameof(JumpItem.Kind), 110); Col(_items, "Host", nameof(JumpItem.Hostname), 200); Col(_items, "Port", nameof(JumpItem.Port), 60); Col(_items, "User", nameof(JumpItem.Username), 110); Col(_items, "Jump Group", nameof(JumpItem.Group), 160); Col(_items, "Tag", nameof(JumpItem.Tag), 100);
        _items.MouseDoubleClick += (_, _) => OpenInConsole();
        _filter.TextChanged += (_, _) => ShowItems();
        var t1 = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        t1.Children.Add(Bar(Run("↻ Load Jump Items", LoadItemsAsync, primary: true), _filter, Btn("▶ Open in access console", OpenInConsole), Btn("＋ Add as saved hosts", AddAsHosts)));
        t1.Children.Add(_items);
        tabs.Items.Add(new TabItem { Header = "Jump Items", Content = t1 });

        Col(_sessions, "Session", nameof(PraSession.Id), 120); Col(_sessions, "Start", nameof(PraSession.Start), 150); Col(_sessions, "End", nameof(PraSession.End), 150); Col(_sessions, "Target", nameof(PraSession.Target), 200); Col(_sessions, "Jumpoint", nameof(PraSession.Jumpoint), 140); Col(_sessions, "Users", nameof(PraSession.Users), 200);
        var t2 = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        t2.Children.Add(Bar(new TextBlock { Text = "last", VerticalAlignment = VerticalAlignment.Center }, _days, new TextBlock { Text = "day(s)", VerticalAlignment = VerticalAlignment.Center }, Run("↻ Access sessions", LoadSessionsAsync, primary: true)));
        t2.Children.Add(_sessions);
        tabs.Items.Add(new TabItem { Header = "Sessions", Content = t2 });

        var t3 = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        t3.Children.Add(Bar(Run("Jumpoints", LoadJumpointsAsync), Run("Vault accounts (names only)", LoadVaultAsync)));
        t3.Children.Add(_other);
        tabs.Items.Add(new TabItem { Header = "Jumpoints & Vault", Content = t3 });

        Col(_accounts, "System", nameof(ManagedAccount.SystemName), 220); Col(_accounts, "Account", nameof(ManagedAccount.AccountName), 180); Col(_accounts, "Domain", nameof(ManagedAccount.DomainName), 140);
        var t4 = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        t4.Children.Add(Bar(Run("↻ Managed accounts", LoadAccountsAsync, primary: true), Run("⚿ Check out & connect…", CheckOutAsync), Run("🔗 Link to a saved host…", LinkAsync), Run("＋ Add as a vault credential", AddCredentialAsync), Run("Unlink…", UnlinkAsync), Run("↻ Checkouts", () => { ShowCheckouts(); return Task.CompletedTask; }), Run("↩ Check in selected", CheckInSelectedAsync)));
        t4.Children.Add(_accounts);
        t4.Children.Add(new TextBlock { Text = "CHECKED OUT NOW (each is checked in when its tab closes, when it runs out, when you press Check in, or when the app exits)", Style = (Style)FindResource("Label"), Margin = new Thickness(0, 8, 0, 4) });
        t4.Children.Add(_checkouts);
        tabs.Items.Add(new TabItem { Header = "Password Safe", Content = t4 });

        root.Children.Add(tabs);
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        root.Children.Add(_status);
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Unloaded += (_, _) => { };
    }

    private static AppSettings S => AppSettings.Current;

    private static void Enabled() { if (!S.BeyondTrustEnabled) throw new InvalidOperationException("The BeyondTrust integration is off: Settings → Integrations → BeyondTrust (needs admin approval)."); }

    private static PraClient Pra()
    {
        Enabled();
        var secret = S.ObserveSecrets.GetValueOrDefault("bt:pra") ?? throw new InvalidOperationException("No PRA API account yet: Settings → Integrations → BeyondTrust.");
        return new PraClient(S.BeyondTrustPraSite, S.BeyondTrustPraClientId, secret);
    }

    private PasswordSafeClient PasswordSafe()
    {
        Enabled();
        if (!S.BeyondTrustAllowCheckout) throw new InvalidOperationException("Checking out Password Safe credentials is switched off on this PC (policy).");
        return _ps ??= new PasswordSafeClient(S.BeyondTrustPsHost, S.ObserveSecrets.GetValueOrDefault("bt:pskey") ?? throw new InvalidOperationException("No Password Safe API key yet: Settings → Integrations → BeyondTrust."), S.BeyondTrustPsRunAs, S.ObserveSecrets.GetValueOrDefault("bt:pspwd"));
    }

    private async Task LoadItemsAsync() { _status.Text = "Reading Jump Items…"; _all = await Pra().JumpItemsAsync(); ShowItems(); _status.Text = $"{_all.Count} Jump Item(s)."; }

    private void ShowItems()
    {
        var f = _filter.Text.Trim();
        var groups = Words(S.BeyondTrustJumpGroupFilter);   // 0.5.1 #2: the default Jump Group filter (Settings)
        _items.ItemsSource = _all.Where(j => groups.Length == 0 || groups.Any(g => j.Group.Contains(g, StringComparison.OrdinalIgnoreCase)))
            .Where(j => f.Length == 0 || $"{j.Name} {j.Hostname} {j.Group} {j.Tag} {j.Comments}".Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static string[] Words(string list) => list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private static bool JumpointWanted(string jumpoint) => Words(AppSettings.Current.BeyondTrustJumpointFilter) is var w && (w.Length == 0 || w.Any(x => jumpoint.Contains(x, StringComparison.OrdinalIgnoreCase)));

    private void OpenWebConsole()
    {
        // The site's web access console in the portal browser (signed in there as you; nothing is stored by the app).
        if (PraClient.SiteProblem(S.BeyondTrustPraSite) is { } bad) { _status.Text = "✕ " + bad; return; }
        PortalUi.OpenAddress(S.BeyondTrustPraSite.Trim().TrimEnd('/') + "/console", "PRODUCTION");
        AuditLog.Write("you", "beyondtrust", "opened the PRA web access console", S.BeyondTrustPraSite);
    }

    private void OpenInConsole()
    {
        if (_items.SelectedItem is not JumpItem j) return;
        if (S.BeyondTrustJumpClient == "web") { OpenWebConsole(); return; }
        try
        {
            var url = PraClient.JumpUrl(S.BeyondTrustPraSite, j);
            if (Offline.On) { _status.Text = "Offline mode is on: the access console can't be reached."; return; }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            AuditLog.Write("you", j.Hostname, "opened a BeyondTrust Jump Item in the access console", j.Name);
        }
        catch (Exception ex) when (ex is ArgumentException or System.ComponentModel.Win32Exception) { _status.Text = "✕ " + ex.Message; }
    }

    private void AddAsHosts()
    {
        if (!Restrictions.Check("host.add")) return;
        var picked = _items.SelectedItems.OfType<JumpItem>().Where(j => j.Kind is "shell-jump" or "remote-rdp" && j.Hostname.Length > 0).ToList();
        if (picked.Count == 0) { _status.Text = "Select shell-jump or RDP Jump Items first."; return; }
        int added = 0;
        foreach (var j in picked)
        {
            var rdp = j.Kind == "remote-rdp";
            int port = j.Port > 0 ? j.Port : rdp ? 3389 : 22;
            if (AppData.Current.Hosts.Any(h => h.Address.Equals(j.Hostname, StringComparison.OrdinalIgnoreCase) && h.Port == port)) continue;
            AppData.Current.Hosts.Add(new HostEntry { Name = j.Name, Address = j.Hostname, Port = port, Username = j.Username, Protocol = rdp ? "rdp" : "ssh", Platform = rdp ? "windows" : "", Auth = AuthMethod.Password,
                Group = "BeyondTrust/" + Import.ConnectionImport.CleanFolder(j.Group), Notes = $"From BeyondTrust PRA ({j.Kind}, Jump Group {j.Group}). Through PRA: Hosts → BeyondTrust → Open in access console." });
            added++;
        }
        AppData.Save();
        AuditLog.Write("you", "beyondtrust", "Jump Items added as hosts", $"{added} added");
        _status.Text = $"{added} host(s) added under BeyondTrust/.";
    }

    private async Task LoadSessionsAsync()
    {
        int days = _days.SelectedItem is int d ? d : 7;
        _sessions.ItemsSource = (await Pra().SessionsAsync(DateTime.Today.AddDays(-days))).Where(x => JumpointWanted(x.Jumpoint)).ToList();
        _status.Text = $"{(_sessions.ItemsSource as System.Collections.ICollection)?.Count ?? 0} session(s) in the last {days} day(s).";
    }

    private async Task LoadJumpointsAsync() { _other.ItemsSource = (await Pra().JumpointsAsync()).Where(j => JumpointWanted(j.Name)).Select(j => $"{(j.Connected ? "●" : "○")}  {j.Name}  ·  {j.Hostname}").ToList(); }
    private async Task LoadVaultAsync() { _other.ItemsSource = (await Pra().VaultAccountsAsync()).Select(v => $"⚿  {v.Name}  ·  {v.Type}  ·  {v.Description}").ToList(); }

    private async Task LoadAccountsAsync() { _accounts.ItemsSource = await PasswordSafe().AccountsAsync(); _status.Text = $"{(_accounts.ItemsSource as System.Collections.ICollection)?.Count ?? 0} managed account(s) you can request."; }

    private HostEntry? PickHost(ManagedAccount a, string title)
    {
        var hosts = AppData.Current.Hosts.Where(h => !h.IsAdHoc && h.Protocol is "ssh" or "rdp").OrderByDescending(h => h.Address.StartsWith(a.SystemName, StringComparison.OrdinalIgnoreCase) || h.Display.Contains(a.SystemName, StringComparison.OrdinalIgnoreCase)).ThenBy(h => h.Display).Take(40).ToList();
        if (hosts.Count == 0) throw new InvalidOperationException("No saved SSH or Remote Desktop host.");
        int i = Dialogs.Choose(title, $"Which host, with {a.SystemName}\\{a.AccountName}?", hosts.Select(h => $"{h.Display}  ·  {h.Address}").Append("Cancel").ToArray(), hosts.Count);
        return i >= 0 && i < hosts.Count ? hosts[i] : null;
    }

    /// <summary>Opens a saved host once with this account: checked out into memory for the session, checked in when its tab
    /// closes (PasswordSafeSessions).</summary>
    private Task CheckOutAsync()
    {
        if (_accounts.SelectedItem is not ManagedAccount a) throw new InvalidOperationException("Select a managed account (↻ Managed accounts).");
        if (PickHost(a, "Check out & connect") is not { } host) return Task.CompletedTask;
        var h = host.Clone();
        h.PasswordSafeAccount = PasswordSafeSessions.Link(a);
        MainWindow.Instance.OpenTerminal(h);
        _status.Text = "Opening with a Password Safe checkout…";
        return Task.CompletedTask;
    }

    /// <summary>Links the account to a saved host for good: every connection to it checks the password out (ids and a label
    /// are stored on the host; never the password).</summary>
    private Task LinkAsync()
    {
        if (_accounts.SelectedItem is not ManagedAccount a) throw new InvalidOperationException("Select a managed account (↻ Managed accounts).");
        if (PickHost(a, "Link to a saved host") is not { } host) return Task.CompletedTask;
        host.PasswordSafeAccount = PasswordSafeSessions.Link(a);
        AppData.Save();
        AuditLog.Write("you", host.Display, "linked to a Password Safe account", $"{a.SystemName}\\{a.AccountName}");
        _status.Text = $"{host.Display} now signs in with {a.SystemName}\\{a.AccountName} from Password Safe (checked out per session). Unlink it in the host's menu or here.";
        return Task.CompletedTask;
    }

    /// <summary>0.6.17 (ADR-096): a vault credential that checks this account out when used (one checkout shared by every
    /// use until it runs out), so infrastructure providers, portals, sudo and hosts can pick it like any other.</summary>
    private Task AddCredentialAsync()
    {
        if (_accounts.SelectedItem is not ManagedAccount a) throw new InvalidOperationException("Select a managed account (↻ Managed accounts).");
        if (!Restrictions.Check("credential.add")) return Task.CompletedTask;
        var (c, existed) = CredentialSources.AddToVault(CredentialSources.New(CredentialSources.Link(a), $"{a.SystemName}\\{a.AccountName}", CredentialKind.Password, a.AccountName, "service account"));
        _status.Text = existed
            ? $"“{c.Name}” is already in the vault (VAULT → Credentials), pointing at this account."
            : $"Added “{c.Name}” to the vault (VAULT → Credentials). Wherever it is picked, its password is checked out of Password Safe when used and checked back in when the checkout runs out; it is never stored here.";
        return Task.CompletedTask;
    }

    private Task UnlinkAsync()
    {
        var linked = AppData.Current.Hosts.Where(h => h.PasswordSafeAccount.Length > 0).ToList();
        if (linked.Count == 0) { _status.Text = "No host is linked to Password Safe."; return Task.CompletedTask; }
        int i = Dialogs.Choose("Unlink", "Which host?", linked.Select(h => $"{h.Display}  ·  {PasswordSafeSessions.Parse(h.PasswordSafeAccount)?.Label}").Append("Cancel").ToArray(), linked.Count);
        if (i < 0 || i >= linked.Count) return Task.CompletedTask;
        linked[i].PasswordSafeAccount = ""; AppData.Save();
        AuditLog.Write("you", linked[i].Display, "unlinked from Password Safe");
        _status.Text = "Unlinked.";
        return Task.CompletedTask;
    }

    private async Task CheckInSelectedAsync()
    {
        if (_checkouts.SelectedItem is not PasswordSafeSessions.Checkout c) throw new InvalidOperationException("Select a checkout below.");
        await PasswordSafeSessions.CheckInRequestAsync(c.RequestId);
        ShowCheckouts();
        _status.Text = "Checked in.";
    }

    private void ShowCheckouts() { _checkouts.ItemsSource = PasswordSafeSessions.Active.Select(c => c).ToList(); }

    private static WrapPanel Bar(params UIElement[] items) { var w = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) }; foreach (var i in items) { if (i is FrameworkElement f) { f.Margin = new Thickness(0, 0, 8, 4); f.VerticalAlignment = VerticalAlignment.Center; } w.Children.Add(i); } return w; }
    private static Button Btn(string t, Action a) { var b = new Button { Content = t }; b.Click += (_, _) => a(); return b; }

    private Button Run(string t, Func<Task> a, bool primary = false)
    {
        var b = new Button { Content = t };
        if (primary) b.Style = (Style)FindResource("PrimaryButton");
        b.Click += async (_, _) =>
        {
            foreach (var x in _buttons) x.IsEnabled = false;
            try { await a(); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or TimeoutException or System.Net.Http.HttpRequestException or OfflineBlockedException or System.Text.Json.JsonException or System.Xml.XmlException) { _status.Text = "✕ " + ex.Message; }
            finally { foreach (var x in _buttons) x.IsEnabled = true; }
        };
        _buttons.Add(b);
        return b;
    }

    /// <summary>The listing client is this screen's; checkouts belong to their session tabs (PasswordSafeSessions).</summary>
    public void Dispose()
    {
        var ps = _ps; _ps = null;
        if (ps == null) return;
        try { Task.Run(async () => { await ps.SignOutAsync(); ps.Dispose(); }).Wait(TimeSpan.FromSeconds(5)); }
        catch (Exception ex) { Log.Write("Password Safe close", ex); }
    }
}
