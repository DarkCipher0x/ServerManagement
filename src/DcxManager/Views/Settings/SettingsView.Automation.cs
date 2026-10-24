using System.Windows;
using System.Windows.Controls;
using DcxManager.Services;

namespace DcxManager.Views;

// SettingsView: the Automation card (0.5.0, ADR-063) — what Ansible and Puppet may change.
public sealed partial class SettingsView
{
    private Border AutomationCard() => Card("Automation",
        "Ansible and Puppet (sidebar → AUTOMATION). Puppet is read-only until this allows changes: listing certificates, dry runs (--noop), lookups, validation and PuppetDB queries always work. " +
        "Every change asks first (typing PRODUCTION for production hosts) and is written to the audit log. Ansible runs that change hosts always ask first.",
        ProtectedCheck("puppet.allowChanges", "Allow Puppet changes: sign / revoke / clean certificates, real agent runs, enable / disable agents, code deploys and Bolt runs"),
        Row(Btn("Open Puppet", () => MainWindow.Instance.Navigate("puppet")), Btn("Open Ansible", () => MainWindow.Instance.Navigate("ansible"))));

    /// <summary>Microsoft Teams (0.5.0, #13): on/off and images (admin), the app registration, sign-in, destinations.</summary>
    private Border TeamsCard()
    {
        var tenant = Bound(new TextBox { Text = _s.TeamsTenant, Width = FieldWidth, ToolTip = "Your Entra tenant id (GUID) or verified domain (contoso.com)" }, nameof(AppSettings.TeamsTenant));
        var client = Bound(new TextBox { Text = _s.TeamsClientId, Width = FieldWidth, ToolTip = "An app registration (public client, redirect http://127.0.0.1) with the delegated Graph permissions ChannelMessage.Send, ChatMessage.Send, Team.ReadBasic.All, Channel.ReadBasic.All, Chat.ReadBasic" }, nameof(AppSettings.TeamsClientId));
        var list = new ListBox { Height = 110, Width = 560, HorizontalAlignment = HorizontalAlignment.Left };
        var result = new TextBlock { Style = (Style)FindResource("Caption"), TextWrapping = TextWrapping.Wrap };
        void Fill()
        {
            list.Items.Clear();
            foreach (var d in Notify.TeamsShare.Destinations()) list.Items.Add(new ListBoxItem { Content = d.ToString(), Tag = d });
            var who = SignIn.EntraDelegated.Account(Notify.TeamsShare.TokenCache);
            result.Text = (SignIn.EntraDelegated.SignedIn(Notify.TeamsShare.TokenCache) ? $"Signed in to Microsoft Graph{(who.Length > 0 ? " as " + who : "")}. " : "Not signed in to Microsoft Graph (asked when you add a channel or chat, or share). ")
                        + (_s.TeamsEnabled ? "" : "Sharing is OFF.");
        }
        void Add(Notify.TeamsDestination d) { var all = Notify.TeamsShare.Destinations(); all.Add(d); Notify.TeamsShare.SaveDestinations(all); AuditLog.Write("you", "teams", "destination added", $"{d.Name} ({d.KindTitle})"); Fill(); }
        async Task AddChannel()
        {
            try
            {
                var teams = await Notify.TeamsShare.TeamsAsync();
                if (teams.Count == 0) { Dialogs.Info("Teams", "You aren't a member of any team."); return; }
                int ti = Dialogs.Choose("Add a channel", "Which team?", teams.Select(x => x.Name).Append("Cancel").ToArray(), teams.Count);
                if (ti < 0 || ti >= teams.Count) return;
                var chans = await Notify.TeamsShare.ChannelsAsync(teams[ti].Id);
                int ci = Dialogs.Choose("Add a channel", $"Which channel in {teams[ti].Name}?", chans.Select(x => x.Name).Append("Cancel").ToArray(), chans.Count);
                if (ci < 0 || ci >= chans.Count) return;
                Add(new Notify.TeamsDestination { Name = $"{teams[ti].Name} › {chans[ci].Name}", Kind = "channel", TeamId = teams[ti].Id, ChannelId = chans[ci].Id });
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.Net.Http.HttpRequestException or OfflineBlockedException) { result.Text = "✕ " + ex.Message; }
        }
        async Task AddChat()
        {
            try
            {
                var chats = await Notify.TeamsShare.ChatsAsync();
                int i = Dialogs.Choose("Add a chat", "Which chat?", chats.Select(x => x.Name).Append("Cancel").ToArray(), chats.Count);
                if (i < 0 || i >= chats.Count) return;
                Add(new Notify.TeamsDestination { Name = chats[i].Name, Kind = "chat", ChatId = chats[i].Id });
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.Net.Http.HttpRequestException or OfflineBlockedException) { result.Text = "✕ " + ex.Message; }
        }
        void AddWebhook()
        {
            if (Dialogs.Prompt("Add a webhook", "Name (e.g. Ops channel):") is not { Length: > 0 } name) return;
            if (Dialogs.Prompt("Add a webhook", "The Teams Workflows webhook address (Teams → channel → ⋯ → Workflows → “Post to a channel when a webhook request is received”). It is kept encrypted:", password: true) is not { Length: > 0 } url) return;
            var d = new Notify.TeamsDestination { Name = name.Trim(), Kind = "webhook" };
            try { Notify.TeamsShare.SetWebhookUrl(d, url); Add(d); Save(); }
            catch (ArgumentException ex) { Dialogs.Error("Webhook", ex.Message); }
        }
        Fill();
        return Card("Microsoft Teams",
            "Share screenshots, terminal output, incidents and reports to a Teams channel or chat (📷 → Share to Microsoft Teams…, a terminal's right-click menu, Incidents). You see exactly what is sent; secrets are masked first. Channels and chats go through Microsoft Graph signed in as you (screenshots included); a Workflows webhook carries text only. “Teams” in the sidebar is the app's own shared libraries — this is Microsoft Teams.",
            ProtectedCheck("teams.enabled", "Share to Microsoft Teams"),
            ProtectedCheck("teams.images", "Screenshots may be shared (off: text only)"),
            Row(Labeled("ENTRA TENANT", tenant), Labeled("APP REGISTRATION (CLIENT ID)", client),
                AdminApproval.Button("Apply", async () =>
                {
                    var want = new Dictionary<string, System.Text.Json.Nodes.JsonNode?> { ["teams.tenant"] = tenant.Text.Trim(), ["teams.clientId"] = client.Text.Trim() };
                    if (await ApplyProtected("change the Microsoft Teams app registration", want)) { SignIn.EntraDelegated.SignOut(Notify.TeamsShare.TokenCache); Fill(); }
                })),
            result, L("DESTINATIONS"), list,
            Row(Btn("＋ Channel…", () => _ = AddChannel()), Btn("＋ Chat…", () => _ = AddChat()), Btn("＋ Webhook…", AddWebhook),
                Btn("Remove", () =>
                {
                    if ((list.SelectedItem as ListBoxItem)?.Tag is not Notify.TeamsDestination d) return;
                    var all = Notify.TeamsShare.Destinations(); all.RemoveAll(x => x.Id == d.Id); Notify.TeamsShare.SaveDestinations(all);
                    _s.ObserveSecrets.Remove("teams:" + d.Id); Save(); AuditLog.Write("you", "teams", "destination removed", d.Name); Fill();
                }),
                Btn("Send a test…", () => _ = TeamsUi.ShareAsync(Window.GetWindow(this), "Test from ServerManagement", "If you can read this, sharing to Teams works.", null)),
                Btn("Sign out of Graph", () => { SignIn.EntraDelegated.SignOut(Notify.TeamsShare.TokenCache); AuditLog.Write("you", "teams", "signed out of Microsoft Graph"); Fill(); })));
    }

    /// <summary>Cloud PCs (0.6.4, ADR-083): the page on / off, and the tenant + app registration (admin approval) that let
    /// the page list your Cloud PCs through Microsoft Graph.</summary>
    private Border CloudPcCard()
    {
        var tenant = Bound(new TextBox { Text = _s.CloudPcTenant, Width = FieldWidth, ToolTip = "Your Entra tenant id (GUID) or verified domain (contoso.com)" }, nameof(AppSettings.CloudPcTenant));
        var client = Bound(new TextBox { Text = _s.CloudPcClientId, Width = FieldWidth, ToolTip = "An app registration (public client, redirect http://127.0.0.1) with the delegated Microsoft Graph permission CloudPC.Read.All, admin consent granted" }, nameof(AppSettings.CloudPcClientId));
        var result = new TextBlock { Style = (Style)FindResource("Caption"), TextWrapping = TextWrapping.Wrap };
        void Fill()
        {
            var who = CloudPc.CloudPcClient.Account;
            result.Text = !CloudPc.CloudPcClient.Configured ? "No app registration: the Cloud PCs page offers the Windows App and its web client (every Cloud PC you have), without a list here."
                : CloudPc.CloudPcClient.SignedIn ? $"Signed in to Microsoft Graph{(who.Length > 0 ? " as " + who : "")}." : "Not signed in yet (the browser opens the first time you list your Cloud PCs).";
        }
        var enabled = Bound(new CheckBox { Content = "Show the Cloud PCs page (Windows 365 / Azure Virtual Desktop through the Windows App)", IsChecked = _s.CloudPcEnabled }, nameof(AppSettings.CloudPcEnabled));
        enabled.Click += (_, _) => { _s.CloudPcEnabled = enabled.IsChecked == true; Save(); MainWindow.Instance?.BuildNav(); };
        Fill();
        return Card("Cloud PCs",
            "Windows 365 Cloud PCs and Azure Virtual Desktop through Microsoft's Windows App (it can't be embedded): connect in a tab of the app's browser or in the Windows App. Optionally, list your Cloud PCs here: Microsoft Graph, signed in as you, needs an app registration with the delegated permission CloudPC.Read.All (an administrator grants consent).",
            enabled,
            ProtectedCheck("cloudpc.actions", "Allow restart and rename of my Cloud PCs, and administrators' troubleshoot / power on / power off (asks Microsoft for CloudPC.ReadWrite.All, a separate sign-in)"),
            Row(Labeled("ENTRA TENANT", tenant), Labeled("APP REGISTRATION (CLIENT ID)", client),
                AdminApproval.Button("Apply", async () =>
                {
                    var want = new Dictionary<string, System.Text.Json.Nodes.JsonNode?> { ["cloudpc.tenant"] = tenant.Text.Trim(), ["cloudpc.clientId"] = client.Text.Trim() };
                    if (await ApplyProtected("change the Cloud PCs app registration", want)) { CloudPc.CloudPcClient.SignOut(); Fill(); }
                })),
            result,
            Row(Btn("Open Cloud PCs", () => MainWindow.Instance?.Navigate("cloudpc")),
                Btn("Sign out of Graph", () => { CloudPc.CloudPcClient.SignOut(); CloudPc.CloudPcClient.SignOutActions(); AuditLog.Write("you", "cloudpc", "signed out of Microsoft Graph"); Fill(); })));
    }

    /// <summary>Microsoft 365 admin (0.6.6, ADR-084): the page on / off, the tenant + app registration (admin approval),
    /// the read-only permissions it needs, sign-out.</summary>
    private Border M365Card()
    {
        var tenant = Bound(new TextBox { Text = _s.M365Tenant, Width = FieldWidth, ToolTip = "Your Entra tenant id (GUID) or verified domain (contoso.com)" }, nameof(AppSettings.M365Tenant));
        var client = Bound(new TextBox { Text = _s.M365ClientId, Width = FieldWidth, ToolTip = "An app registration (public client, redirect http://127.0.0.1) with the delegated Graph permissions below, admin consent granted" }, nameof(AppSettings.M365ClientId));
        var result = new TextBlock { Style = (Style)FindResource("Caption"), TextWrapping = TextWrapping.Wrap };
        void Fill()
        {
            var who = Microsoft365.M365Admin.Account;
            result.Text = !Microsoft365.GraphReader.Configured ? "No app registration yet: the page offers the admin centres (Portals ▾) without the lists."
                : Microsoft365.M365Admin.SignedIn ? $"Signed in to Microsoft Graph{(who.Length > 0 ? " as " + who : "")}." : "Not signed in yet (the browser asks the first time you load a list of each service).";
        }
        var enabled = Bound(new CheckBox { Content = "Show the Microsoft 365 admin page (Intune, Defender, Entra; read-only)", IsChecked = _s.M365AdminEnabled }, nameof(AppSettings.M365AdminEnabled));
        enabled.Click += (_, _) => { _s.M365AdminEnabled = enabled.IsChecked == true; Save(); MainWindow.Instance?.BuildNav(); };
        var perms = new TextBox { Text = string.Join("\n", Microsoft365.M365Admin.Permissions), IsReadOnly = true, Height = 128, Width = FieldWidth + 120, HorizontalAlignment = HorizontalAlignment.Left, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }.Mono();
        Menus.ForText(perms);
        Fill();
        return Card("Microsoft 365 admin",
            "Intune devices, Defender incidents and alerts, and Entra users, groups, devices, sign-ins and risky users, read-only, signed in as you through Microsoft Graph. Set up once: in the Entra admin centre → App registrations → New registration (a public client with the redirect http://127.0.0.1), add the delegated Microsoft Graph permissions below, and an administrator grants consent. Your account still needs a role that can read each (e.g. Intune / Security / Global Reader); sign-ins need Entra ID P1, risky users P2.",
            enabled,
            Row(Labeled("ENTRA TENANT", tenant), Labeled("APP REGISTRATION (CLIENT ID)", client),
                AdminApproval.Button("Apply", async () =>
                {
                    var want = new Dictionary<string, System.Text.Json.Nodes.JsonNode?> { ["m365.tenant"] = tenant.Text.Trim(), ["m365.clientId"] = client.Text.Trim() };
                    if (await ApplyProtected("change the Microsoft 365 admin app registration", want)) { Microsoft365.M365Admin.SignOut(); Fill(); }
                })),
            L("DELEGATED GRAPH PERMISSIONS (ALL READ-ONLY)"), perms,
            result,
            Row(Btn("Open Microsoft 365 admin", () => MainWindow.Instance?.Navigate("m365")),
                Btn("Sign out of Graph", () => { Microsoft365.M365Admin.SignOut(); AuditLog.Write("you", "m365", "signed out of Microsoft Graph"); Fill(); })));
    }

    /// <summary>Pingdom (0.5.0, #17): the read-only API token and pinning its checks to the dashboard.</summary>
    private Border PingdomCard()
    {
        var token = new PasswordBox { Width = FieldWidth, ToolTip = Notify.PingdomClient.Token != null ? "A token is stored (encrypted). Leave empty to keep it." : "A read-only API token: Pingdom → Settings → Pingdom API → Add API token (Read access)." };
        var list = new ListBox { Height = 160, SelectionMode = SelectionMode.Extended, Visibility = Visibility.Collapsed };
        var result = new TextBlock { Style = (Style)FindResource("Caption"), TextWrapping = TextWrapping.Wrap };
        async Task Load()
        {
            result.Text = "Reading Pingdom's checks…";
            try
            {
                var checks = await Notify.PingdomClient.ChecksAsync();
                list.Items.Clear();
                foreach (var c in checks) list.Items.Add(new ListBoxItem { Content = $"{(c.Status == "up" ? "●" : c.Status is "down" or "unconfirmed_down" ? "✕" : "○")}  {c.Name}  ·  {c.Hostname}  ·  {c.Type}  ·  {c.Status}" + (c.LastResponseMs is { } ms ? $" · {ms} ms" : ""), Tag = c });
                list.Visibility = Visibility.Visible;
                result.Text = $"{checks.Count} check(s): {checks.Count(c => c.Status == "up")} up, {checks.Count(c => c.Status is "down" or "unconfirmed_down")} down. Select some and pin them to the dashboard.";
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.Net.Http.HttpRequestException or OfflineBlockedException) { result.Text = "✕ " + ex.Message; }
        }
        return Card("Pingdom",
            "Pingdom's uptime checks next to the app's own on the dashboard: a read-only API token (kept encrypted, never in a policy) and only https://api.pingdom.com is contacted, on the dashboard's check schedule.",
            Row(Labeled("API TOKEN (READ ACCESS)", token),
                Btn("Save token", () => { if (token.Password.Length == 0) return; _s.ObserveSecrets[Notify.PingdomClient.SecretKey] = token.Password.Trim(); token.Clear(); Save(); AuditLog.Write("you", "pingdom", "API token changed"); result.Text = "✓ Token saved (encrypted)."; }),
                Btn("Load checks", () => _ = Load()),
                Btn("Forget token", () => { _s.ObserveSecrets.Remove(Notify.PingdomClient.SecretKey); Save(); AuditLog.Write("you", "pingdom", "API token removed"); result.Text = "Token removed."; })),
            result, list,
            Row(Btn("📌 Pin selected to the dashboard", () =>
            {
                int n = 0;
                foreach (var c in list.SelectedItems.OfType<ListBoxItem>().Select(i => (Notify.PingdomCheck)i.Tag))
                {
                    if (AppData.Current.DashboardTiles.Any(t => t.Kind == "pingdom" && t.Target == c.Id.ToString())) continue;
                    Checks.Pin(new DashboardTile { Kind = "pingdom", Name = c.Name.Length > 0 ? c.Name : c.Hostname, Target = c.Id.ToString() });
                    n++;
                }
                result.Text = $"{n} Pingdom check(s) pinned to the dashboard.";
            })));
    }

    /// <summary>IDE integration (0.5.0, ADR-065): which editor, the managed SSH config, how long keys stay in ssh-agent.</summary>
    private Border IdeCard()
    {
        var found = Ide.IdeIntegration.Installed();
        var pick = Bound(new ComboBox { Width = 220, ToolTip = "Hosts, WSL distributions and folders open in this editor" }, nameof(AppSettings.IdePreferred));
        pick.Items.Add(new ComboBoxItem { Content = "(the first one found)", Tag = "" });
        foreach (var e in found) pick.Items.Add(new ComboBoxItem { Content = e.Title, Tag = e.Id });
        pick.SelectedItem = pick.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == _s.IdePreferred) ?? pick.Items[0];
        pick.SelectionChanged += (_, _) => { _s.IdePreferred = (pick.SelectedItem as ComboBoxItem)?.Tag as string ?? ""; Save(); };
        int[] mins = { 15, 30, 60, 120, 240, 480 };
        var life = Bound(new ComboBox { Width = 90, ItemsSource = mins, SelectedItem = mins.Contains(_s.IdeAgentMinutes) ? _s.IdeAgentMinutes : 60, ToolTip = "How long a host's key stays in ssh-agent after opening it in an IDE" }, nameof(AppSettings.IdeAgentMinutes));
        life.SelectionChanged += (_, _) => { if (life.SelectedItem is int m) { _s.IdeAgentMinutes = m; Save(); } };
        var state = new TextBlock { Style = (Style)FindResource("Caption"), TextWrapping = TextWrapping.Wrap,
            Text = (found.Count == 0 ? "No supported editor found (VS Code, VS Code Insiders, Cursor, VSCodium, Windsurf, JetBrains Gateway). " : $"Found: {string.Join(", ", found.Select(e => e.Title))}. ") +
                   (Ide.IdeIntegration.IncludePresent() ? "~/.ssh/config includes dcx_config." : "~/.ssh/config doesn't include dcx_config yet (asked the first time you open a host).") };
        return Card("IDE integration",
            "Open saved hosts (Remote-SSH), WSL distributions and folders in VS Code, Cursor, VSCodium, Windsurf or JetBrains Gateway: Hosts → ⧉ Open in …, File commander's menu, LOCAL → WSL. The editor opens through its own link handler; a host's vault key goes into ssh-agent for a while and is never written to disk.",
            state,
            Row(Labeled("EDITOR", pick), Labeled("KEYS STAY IN SSH-AGENT (MIN)", life)),
            ProtectedCheck("ide.sshConfig", "Keep ~/.ssh/dcx_config (Host entries, no secrets) and its Include line for Remote-SSH"),
            Row(Btn("Remove dcx_config and the Include line", () => { Ide.IdeIntegration.Remove(); AuditLog.Write("you", "ide", "SSH config for IDEs removed"); state.Text = "Removed."; })));
    }
}
