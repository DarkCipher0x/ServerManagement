using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DcxManager.Chat.Admin;
using DcxManager.Services;
using DcxManager.Views;

namespace DcxManager.Chat.Views;

/// <summary>
/// CipherChat server administration (ADR-117 §7a), the same as CipherChat for Windows' admin pages: people (invite, role,
/// permissions, suspend, sign out, reset links, delete), requests for access, roles, the audit log, server settings and
/// email. It has its own sign-in (password + authenticator code), kept in memory only: never the chat's device pairing,
/// never resumed, and ended when the app locks or quits. Tabs and actions appear only for what the server says this admin
/// may do; the server checks everything again. One-time links are shown once to copy and never written anywhere.
/// </summary>
public sealed class ChatAdminView : UserControl, IDisposable
{
    private static ChatAdminClient Client => ChatHub.Admin;
    private readonly ContentControl _body = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private bool _disposed;

    public ChatAdminView()
    {
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var root = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(_status);
        root.Children.Add(_body);
        Content = root;
        SetResourceReference(BackgroundProperty, "Bg");
        Client.SignedOut += OnSignedOut;
        Loaded += (_, _) => Render();
    }

    private void OnSignedOut() { if (!_disposed) Dispatcher.BeginInvoke(() => { if (!_disposed) Render(); }); }

    private void Say(string text) => _status.Text = text;

    private async Task Step(string busy, Func<Task> step)
    {
        Say(busy);
        IsEnabled = false;
        try { await step(); Say(""); }
        catch (Exception ex) { Say(""); Dialogs.Error("CipherChat administration", ex.Message, ex: ex); }
        finally { IsEnabled = true; }
    }

    private void Render() => _body.Content = ChatAdminClient.Blocked() is { } why ? Note(why) : Client.SignedIn ? SignedInPanel() : SignInPanel();

    private FrameworkElement Note(string text)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left };
        t.SetResourceReference(StyleProperty, "Hint");
        return t;
    }

    private TextBlock Label(string text, double top = 0)
    {
        var t = new TextBlock { Text = text, Margin = new Thickness(0, top, 0, 4) };
        t.SetResourceReference(StyleProperty, "Label");
        return t;
    }

    private Button Btn(string text, Func<Task> click, string? style = null)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 6, 6) };
        if (style != null) b.Style = (Style)FindResource(style);
        b.Click += async (_, _) => await click();
        return b;
    }

    // ------------------------------------------------------------------------------------------- sign-in

    private FrameworkElement SignInPanel()
    {
        var p = new StackPanel { MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Left };
        var title = new TextBlock { Text = "Sign in to administration", Margin = new Thickness(0, 0, 0, 6) };
        title.SetResourceReference(StyleProperty, "PageTitle");
        p.Children.Add(title);
        p.Children.Add(Note("A separate sign-in for managing a CipherChat server (people, requests, roles, settings, email). It isn't this PC's chat pairing: " +
            "it lasts until you sign out, lock the app or quit, and nothing of it is saved. Your account needs an admin role on the server."));
        var server = new TextBox { Text = AppSettings.Current.ChatAllowedServers.FirstOrDefault() ?? ChatHub.Sessions.FirstOrDefault()?.Origin ?? "https://", Margin = new Thickness(0, 2, 0, 8) };
        var user = new TextBox { Text = ChatHub.Sessions.FirstOrDefault()?.User ?? "", Margin = new Thickness(0, 2, 0, 8) };
        var pw = new PasswordBox { Margin = new Thickness(0, 2, 0, 8) };
        var code = new TextBox { Margin = new Thickness(0, 2, 0, 8) };
        System.Windows.Automation.AutomationProperties.SetName(server, "Server");
        System.Windows.Automation.AutomationProperties.SetName(user, "Username");
        System.Windows.Automation.AutomationProperties.SetName(pw, "Password");
        System.Windows.Automation.AutomationProperties.SetName(code, "Authenticator code");
        p.Children.Add(Label("SERVER", 12)); p.Children.Add(server);
        p.Children.Add(Label("USERNAME")); p.Children.Add(user);
        p.Children.Add(Label("PASSWORD")); p.Children.Add(pw);
        p.Children.Add(Label("AUTHENTICATOR CODE")); p.Children.Add(code);
        async Task Go() => await Step("Signing in…", async () =>
        {
            var password = pw.Password; pw.Clear();
            var otp = code.Text; code.Clear();
            Client.Ask = AskCode;
            await Client.SignInAsync(server.Text, user.Text, password, otp);
            Render();
        });
        code.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await Go(); } };
        p.Children.Add(Btn("Sign in", Go, "PrimaryButton"));
        return p;
    }

    private Task<string?> AskCode(string? error) => Dispatcher.InvokeAsync(() =>
        Dialogs.Prompt("Confirm it's you", (error is { Length: > 0 } ? error + "\n\n" : "") + "Enter the current code from your authenticator app:")).Task;

    // ------------------------------------------------------------------------------------------- signed in

    private FrameworkElement SignedInPanel()
    {
        var dock = new DockPanel();
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var signOut = Btn("Sign out of administration", () => Step("Signing out…", async () => { await Client.SignOutAsync(); Render(); }));
        DockPanel.SetDock(signOut, Dock.Right);
        head.Children.Add(signOut);
        var who = new TextBlock { Text = "Administering " + Client.Display, VerticalAlignment = VerticalAlignment.Center };
        who.SetResourceReference(StyleProperty, "PageTitle");
        head.Children.Add(who);
        DockPanel.SetDock(head, Dock.Top);
        dock.Children.Add(head);

        var tabs = new ContentControl { Content = Note("Loading what you may manage…") };
        dock.Children.Add(tabs);
        _ = FillTabsAsync(tabs);
        return dock;
    }

    /// <summary>The tabs this admin's permissions allow (from the server's roles answer); the server checks every call again.</summary>
    private async Task FillTabsAsync(ContentControl host)
    {
        IReadOnlyList<string>? mine = null;
        try { mine = (await Client.RolesAsync()).Mine; }
        catch (Exception ex) when (ex is CipherChat.Api.ApiException or InvalidOperationException) { Log.Write("Chat admin permissions", ex); }
        if (_disposed || !Client.SignedIn) { Render(); return; }
        bool Can(params string[] any) => mine is null || any.Any(mine.Contains);
        var tabs = new TabControl();
        if (Can("admin.users.view", "admin.users.create")) tabs.Items.Add(new TabItem { Header = "People", Content = new PeopleTab(this) });
        if (Can("admin.requests.review")) tabs.Items.Add(new TabItem { Header = "Requests", Content = new RequestsTab(this) });
        tabs.Items.Add(new TabItem { Header = "Roles", Content = new RolesTab(this) });
        if (Can("admin.settings.manage", "admin.mail.manage")) tabs.Items.Add(new TabItem { Header = "Server settings & email", Content = new SettingsTab(this) });
        if (Can("admin.audit.view")) tabs.Items.Add(new TabItem { Header = "Audit log", Content = new AuditTab(this) });
        tabs.SelectedIndex = 0;
        host.Content = tabs;
    }

    /// <summary>Shows a one-time link once, to copy (it never goes to a log or a file).</summary>
    private static void ShowOnce(AdminResult r, string what)
    {
        var lines = new List<string>();
        if (r.Emailed is { Length: > 0 } to) lines.Add("Emailed to " + to + ".");
        if (r.SessionsEnded is { } n) lines.Add(n + " sign-in(s) ended.");
        if (r.Link is not { } link) { Dialogs.Info(what, lines.Count > 0 ? string.Join("\n", lines) : "Done."); return; }
        if (TestMode.Running) { Dialogs.RefusedInSelfTest("one-time link: " + what); return; }
        var w = Dialogs.MakeWindow(what, 620);
        var p = new StackPanel { Margin = new Thickness(18) };
        p.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
            Text = (lines.Count > 0 ? string.Join("\n", lines) + "\n\n" : "") + $"Give this one-time link to {r.Username} over a channel you trust. It works once and expires in {link.ExpiresHours} hours. It is shown only now.",
        });
        var box = new TextBox { Text = link.Url, IsReadOnly = true, TextWrapping = TextWrapping.Wrap };
        box.SetResourceReference(StyleProperty, "Mono");
        p.Children.Add(box);
        var copy = new Button { Content = "Copy", Margin = new Thickness(0, 10, 6, 0), HorizontalAlignment = HorizontalAlignment.Left };
        copy.Click += (_, _) => { try { Clipboard.SetText(link.Url); copy.Content = "Copied"; } catch (System.Runtime.InteropServices.ExternalException) { } };
        p.Children.Add(copy);
        w.Content = p;
        w.KeyDown += (_, e) => { if (e.Key == Key.Escape) w.Close(); };
        w.ShowDialog();
        box.Text = "";
    }

    private static bool AskSendEmail(string what, string user) =>
        Dialogs.Choose(what, $"Email the link to {user} from the server, or show it here once to pass on yourself?", ["Email it", "Show it here"], cancelIndex: -1) == 0;

    // ------------------------------------------------------------------------------------------- tabs

    /// <summary>A tab with a toolbar, a list and a details/actions area; loads on first show and on Refresh.</summary>
    private abstract class AdminTab : DockPanel
    {
        protected readonly ChatAdminView View;
        protected readonly WrapPanel Tools = new() { Margin = new Thickness(0, 8, 0, 6) };
        protected readonly ListBox List = new() { MinHeight = 200 };
        protected readonly StackPanel Details = new() { Margin = new Thickness(12, 0, 0, 0), Width = 380 };
        private bool _loaded;

        protected AdminTab(ChatAdminView view)
        {
            View = view;
            Tools.Children.Add(view.Btn("Refresh", Reload));
            SetDock(Tools, System.Windows.Controls.Dock.Top);
            Children.Add(Tools);
            var right = new ScrollViewer { Content = Details, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            SetDock(right, System.Windows.Controls.Dock.Right);
            Children.Add(right);
            Children.Add(List);
            List.SelectionChanged += (_, _) => { Details.Children.Clear(); if (List.SelectedItem is ListBoxItem { Tag: { } t }) ShowDetails(t); };
            IsVisibleChanged += async (_, _) => { if (IsVisible && !_loaded) { _loaded = true; await Reload(); } };
        }

        protected Task Reload() => View.Step("Loading…", LoadAsync);
        protected abstract Task LoadAsync();
        protected abstract void ShowDetails(object item);

        protected void Fill<T>(IEnumerable<T> items, Func<T, string> label, Func<T, string>? key = null) where T : notnull
        {
            var keep = (List.SelectedItem as ListBoxItem)?.Tag is T old && key != null ? key(old) : null;
            List.Items.Clear();
            foreach (var i in items) List.Items.Add(new ListBoxItem { Content = ChatRoomView.Clean(label(i)), Tag = i });
            if (keep != null) List.SelectedItem = List.Items.OfType<ListBoxItem>().FirstOrDefault(x => x.Tag is T t && key!(t) == keep);
        }

        protected void Line(string label, string value)
        {
            var t = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) };
            t.Inlines.Add(new System.Windows.Documents.Run(label + ": ") { FontWeight = FontWeights.SemiBold });
            t.Inlines.Add(new System.Windows.Documents.Run(ChatRoomView.Clean(value)));
            Details.Children.Add(t);
        }

        protected void Act(string text, Func<Task> run, bool danger = false) =>
            Details.Children.Add(View.Btn(text, () => View.Step("Working…", async () => { await run(); await LoadAsync(); }), danger ? "DangerButton" : null));
    }

    private sealed class PeopleTab : AdminTab
    {
        private AdminPeople? _people;

        public PeopleTab(ChatAdminView view) : base(view)
        {
            Tools.Children.Add(view.Btn("Invite someone…", InviteAsync));
        }

        protected override async Task LoadAsync()
        {
            _people = await Client.PeopleAsync();
            Fill(_people.Users.OrderBy(u => u.Username, StringComparer.OrdinalIgnoreCase),
                u => u.Username + (u.DisplayName.Length > 0 ? " (" + u.DisplayName + ")" : "") + " · " + u.Role + (u.Status != "active" ? " · " + u.Status : "") + (u.Locked ? " · locked" : ""),
                u => u.Username);
        }

        private async Task InviteAsync()
        {
            if (_people is { } p && !p.Can("admin.users.create")) { Dialogs.Info("Invite", "Your role can't invite people on this server."); return; }
            if (Dialogs.Prompt("Invite someone", "Username (letters, digits, . _ -):") is not { Length: > 0 } user) return;
            var name = Dialogs.Prompt("Invite someone", "Display name (optional):") ?? "";
            var email = Dialogs.Prompt("Invite someone", "Email address (optional; needed to email the invite):") ?? "";
            var roles = _people?.Roles ?? ["member"];
            int pick = roles.Count > 1 ? Dialogs.Choose("Invite someone", "Role:", roles.ToArray(), cancelIndex: -1) : 0;
            if (pick < 0) return;
            bool send = email.Length > 0 && AskSendEmail("Invite", user);
            await View.Step("Inviting…", async () =>
            {
                ShowOnce(await Client.InviteAsync(user.Trim(), name.Trim(), email.Trim(), roles[pick], send), "Invite for " + user.Trim());
                await LoadAsync();
            });
        }

        protected override void ShowDetails(object item)
        {
            var u = (AdminUser)item;
            var p = _people!;
            Line("Username", u.Username);
            if (u.DisplayName.Length > 0) Line("Name", u.DisplayName);
            if (u.Email.Length > 0) Line("Email", u.Email);
            Line("Role", u.Role + (u.Username == p.Owner ? " (server owner)" : ""));
            Line("Status", u.Status + (u.Locked ? ", locked after failed sign-ins" : ""));
            Line("Last sign-in", u.LastLogin.Length > 0 ? u.LastLogin : "never");
            Line("Chat identity", u.ChatIdentity ? (u.Certified ? "set up, verified by the owner" : "set up, not verified yet") : "not set up");
            Line("Signed in on", u.Sessions + " device(s)/session(s)");
            if (u.Grants.Count > 0) Line("Extra permissions", string.Join(", ", u.Grants));
            if (u.Denies.Count > 0) Line("Removed permissions", string.Join(", ", u.Denies));
            if (!u.Manageable) { Details.Children.Add(new TextBlock { Text = "You can't change this account (the owner, yourself, or an equal role).", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) }); return; }

            Details.Children.Add(View.Label("ACTIONS", 12));
            if (p.Can("admin.users.roles") && p.Roles.Count > 0)
                Act("Change role…", async () =>
                {
                    int i = Dialogs.Choose("Change role", "New role for " + u.Username + ":", p.Roles.ToArray(), cancelIndex: -1);
                    if (i >= 0 && p.Roles[i] != u.Role) await Client.SetRoleAsync(u.Username, p.Roles[i]);
                });
            if (p.Can("admin.users.roles"))
                Act("Extra / removed permissions…", async () =>
                {
                    var g = Dialogs.Prompt("Permissions", "Extra permissions for " + u.Username + " (comma-separated ids):", string.Join(", ", u.Grants));
                    if (g is null) return;
                    var d = Dialogs.Prompt("Permissions", "Permissions removed from their role (comma-separated ids):", string.Join(", ", u.Denies));
                    if (d is null) return;
                    static IEnumerable<string> Ids(string s) => s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    await Client.SetPermissionsAsync(u.Username, Ids(g), Ids(d));
                });
            if (p.Can("admin.users.update"))
            {
                if (u.Suspended) Act("Reactivate", () => Shown(Client.ActivateAsync(u.Username), "Reactivated " + u.Username));
                else Act("Suspend…", async () => { if (Dialogs.Confirm("Suspend", $"Suspend {u.Username}? They are signed out everywhere and can't sign in until reactivated.", "Suspend", danger: true)) ShowOnce(await Client.SuspendAsync(u.Username), "Suspended " + u.Username); });
                Act("Sign out everywhere", () => Shown(Client.SignOutUserAsync(u.Username), "Signed out " + u.Username));
            }
            if (p.Can("admin.users.update"))
            {
                if (u.Pending) Act("New invite link…", async () => ShowOnce(await Client.NewInviteAsync(u.Username, u.Email.Length > 0 && AskSendEmail("New invite", u.Username)), "Invite for " + u.Username));
                else
                {
                    Act("Reset password…", async () => { if (Dialogs.Confirm("Reset password", $"Make a password reset link for {u.Username}? Their current password stops working when they use it.", "Make link")) ShowOnce(await Client.ResetPasswordAsync(u.Username, u.Email.Length > 0 && AskSendEmail("Reset password", u.Username)), "Password reset for " + u.Username); });
                    Act("Reset authenticator…", async () => { if (Dialogs.Confirm("Reset authenticator", $"Make a link for {u.Username} to set up a new authenticator?", "Make link")) ShowOnce(await Client.ResetMfaAsync(u.Username, u.Email.Length > 0 && AskSendEmail("Reset authenticator", u.Username)), "Authenticator reset for " + u.Username); });
                }
            }
            if (p.Can("admin.users.delete"))
                Act("Delete account…", async () =>
                {
                    if (Dialogs.Confirm("Delete account", $"Delete {u.Username}'s account? They lose access to every room. This can't be undone.", "Delete", danger: true, typeToConfirm: u.Username))
                        ShowOnce(await Client.DeleteAsync(u.Username), "Deleted " + u.Username);
                }, danger: true);
        }

        private static async Task Shown(Task<AdminResult> run, string what) => ShowOnce(await run, what);
    }

    private sealed class RequestsTab : AdminTab
    {
        private readonly ComboBox _which = new() { Width = 140, Margin = new Thickness(0, 0, 6, 6) };

        public RequestsTab(ChatAdminView view) : base(view)
        {
            foreach (var s in new[] { "new", "approved", "declined" }) _which.Items.Add(s);
            _which.SelectedIndex = 0;
            _which.SelectionChanged += async (_, _) => await Reload();
            Tools.Children.Insert(0, _which);
        }

        protected override async Task LoadAsync()
        {
            var r = await Client.RequestsAsync(_which.SelectedItem as string ?? "new");
            Fill(r.Requests, x => x.Created + " · " + x.Name + (x.Email.Length > 0 ? " <" + x.Email + ">" : ""), x => x.Id.ToString());
        }

        protected override void ShowDetails(object item)
        {
            var r = (AccessRequest)item;
            Line("Name", r.Name);
            Line("Email", r.Email);
            Line("Sent", r.Created);
            Line("Status", r.Status + (r.DecidedBy.Length > 0 ? " by " + r.DecidedBy : ""));
            if (r.Message.Length > 0) Line("Message", r.Message);
            if (r.Status != "new") return;
            Details.Children.Add(View.Label("ACTIONS", 12));
            Act("Approve and invite…", async () =>
            {
                if (Dialogs.Prompt("Approve", "Username for " + r.Name + ":", Suggest(r)) is not { Length: > 0 } user) return;
                ShowOnce(await Client.ApproveRequestAsync(r.Id, user.Trim(), r.Email.Length > 0 && AskSendEmail("Approve", user.Trim())), "Invite for " + user.Trim());
            });
            Act("Decline", async () => { if (Dialogs.Confirm("Decline", "Decline " + r.Name + "'s request?", "Decline")) await Client.DecideRequestAsync(r.Id, "decline"); });
            Act("Delete…", async () => { if (Dialogs.Confirm("Delete request", "Delete this request and what it says? This can't be undone.", "Delete", danger: true)) await Client.DecideRequestAsync(r.Id, "delete"); }, danger: true);
        }

        private static string Suggest(AccessRequest r) =>
            new string(r.Name.ToLowerInvariant().Replace(' ', '.').Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-').Take(32).ToArray());
    }

    private sealed class RolesTab : AdminTab
    {
        private AdminRoles? _roles;

        public RolesTab(ChatAdminView view) : base(view)
        {
            Tools.Children.Add(view.Btn("New role…", () => EditAsync(null)));
        }

        protected override async Task LoadAsync()
        {
            _roles = await Client.RolesAsync();
            Fill(_roles.Roles, r => r.Label + " (" + r.Name + ") · " + r.People + " people" + (r.Builtin ? " · built in" : ""), r => r.Name);
        }

        protected override void ShowDetails(object item)
        {
            var r = (AdminRole)item;
            Line("Role", r.Label + " (" + r.Name + ")");
            Line("People", r.People.ToString());
            if (r.Updated.Length > 0) Line("Changed", r.Updated + (r.UpdatedBy.Length > 0 ? " by " + r.UpdatedBy : ""));
            Details.Children.Add(View.Label("PERMISSIONS", 10));
            foreach (var g in (_roles?.Permissions ?? []).GroupBy(x => x.Group))
                foreach (var perm in g)
                    Details.Children.Add(new TextBlock { Text = (r.Perms.Contains(perm.Id) ? "✓ " : "· ") + g.Key + ": " + ChatRoomView.Clean(perm.Label), TextWrapping = TextWrapping.Wrap, Opacity = r.Perms.Contains(perm.Id) ? 1 : 0.55 });
            if (_roles?.CanManage != true || r.Builtin) return;
            Details.Children.Add(View.Label("ACTIONS", 12));
            Act("Edit…", () => EditAsync(r));
            Act("Delete…", async () => { if (Dialogs.Confirm("Delete role", $"Delete the role {r.Label}?" + (r.People > 0 ? $" {r.People} people have it; the server refuses until they have another role." : ""), "Delete", danger: true)) await Client.DeleteRoleAsync(r.Name); }, danger: true);
        }

        private async Task EditAsync(AdminRole? r)
        {
            if (_roles is not { CanManage: true } roles) { Dialogs.Info("Roles", "Your role can't manage roles on this server."); return; }
            var name = r?.Name ?? Dialogs.Prompt("New role", "Role id (lower-case letters, digits and -, starting with a letter):");
            if (name is not { Length: > 0 }) return;
            if (Dialogs.Prompt(r is null ? "New role" : "Edit role", "Label:", r?.Label ?? name) is not { Length: > 0 } label) return;
            var ids = string.Join(", ", roles.Permissions.Select(x => x.Id));
            if (Dialogs.Prompt(r is null ? "New role" : "Edit role", "Permissions (comma-separated). You can give only what you hold yourself.\n\nAvailable: " + ids, string.Join(", ", r?.Perms ?? []), multiline: true) is not { } perms) return;
            var list = perms.Split([',', '\n', '\r', ' '], StringSplitOptions.RemoveEmptyEntries).ToList();
            await View.Step("Saving…", async () =>
            {
                if (r is null) await Client.CreateRoleAsync(name.Trim(), label.Trim(), list); else await Client.UpdateRoleAsync(r.Name, label.Trim(), list);
                await LoadAsync();
            });
        }
    }

    private sealed class AuditTab : AdminTab
    {
        public AuditTab(ChatAdminView view) : base(view) { }

        protected override async Task LoadAsync() =>
            Fill(await Client.AuditAsync(), e => e.At + " · " + e.Actor + " · " + e.Action + (e.TargetId.Length > 0 ? " · " + e.TargetId : "") + (e.Outcome is { Length: > 0 } o && o != "ok" ? " · " + o : ""), e => e.Seq.ToString());

        protected override void ShowDetails(object item)
        {
            var e = (AdminAuditEvent)item;
            Line("When", e.At); Line("Who", e.Actor); Line("What", e.Action);
            if (e.TargetId.Length > 0) Line("On", e.TargetType + " " + e.TargetId);
            Line("Outcome", e.Outcome);
            if (e.Ip.Length > 0) Line("From", e.Ip);
        }
    }

    /// <summary>Server settings from the server's own schema (so new settings appear without a code change), then email.</summary>
    private sealed class SettingsTab : AdminTab
    {
        private AdminServerSettings? _s;

        public SettingsTab(ChatAdminView view) : base(view)
        {
            Tools.Children.Add(view.Btn("Change a setting…", ChangeAsync));
            Tools.Children.Add(view.Btn("Email set-up…", MailAsync));
            Tools.Children.Add(view.Btn("Send a test email…", async () =>
            {
                if (Dialogs.Prompt("Test email", "Send to (empty = your own address):") is not { } to) return;
                await View.Step("Sending…", async () => Dialogs.Info("Test email", await Client.TestMailAsync(to)));
            }));
        }

        protected override async Task LoadAsync()
        {
            _s = await Client.SettingsAsync();
            var rows = _s.Schema.Select(x => (Key: x.Key, Text: x.Key + " = " + (_s.Settings[x.Key]?.ToJsonString() ?? "(default " + (x.Default?.ToJsonString() ?? "none") + ")"))).ToList();
            if (_s.CanMail) rows.Add(("mail", "email: " + (Read(_s.Mail, "host") is { Length: > 0 } h ? h + " · from " + Read(_s.Mail, "from") : "not set up") + " · " + _s.MailQueue + " queued"));
            Fill(rows, r => r.Text, r => r.Key);
            if (!_s.CanSettings && !_s.CanMail) Fill(new[] { ("", "Your role can't change server settings or email.") }, r => r.Item2);
        }

        private static string Read(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue(out string? s) ? s : "";

        protected override void ShowDetails(object item)
        {
            if (_s is null || item is not ValueTuple<string, string> row) return;
            if (row.Item1 == "mail")
            {
                Line("Server", Read(_s.Mail, "host") + ":" + (_s.Mail["port"]?.ToJsonString() ?? ""));
                Line("Security", Read(_s.Mail, "security"));
                Line("From", Read(_s.Mail, "from_name") + " <" + Read(_s.Mail, "from") + ">");
                Line("Queued", _s.MailQueue.ToString());
                return;
            }
            if (_s.Schema.FirstOrDefault(x => x.Key == row.Item1) is not { } spec) return;
            Line("Setting", spec.Key);
            Line("Kind", spec.Kind + (spec.Kind == "int" ? $" ({spec.Min}–{spec.Max})" : spec.Kind == "str" ? $" (up to {spec.Max} characters)" : ""));
            Line("Value", _s.Settings[spec.Key]?.ToJsonString() ?? "(default)");
            if (_s.CanSettings) Act("Change…", () => ChangeOneAsync(spec));
        }

        private async Task ChangeAsync()
        {
            if (_s is not { CanSettings: true } s) { Dialogs.Info("Server settings", "Your role can't change server settings."); return; }
            if (TestMode.Running) { Dialogs.RefusedInSelfTest("pick: server setting"); return; }
            if (Dialogs.Pick("Server settings", "Which setting?", s.Schema, x => x.Key + " (" + x.Kind + ")") is { } spec) await View.Step("Saving…", () => ChangeOneAsync(spec));
        }

        private async Task ChangeOneAsync(AdminSettingSpec spec)
        {
            var now = _s!.Settings[spec.Key];
            if (Dialogs.Prompt("Change " + spec.Key, spec.Kind switch { "int" => $"A number from {spec.Min} to {spec.Max}:", "bool" => "on or off:", _ => $"Text (up to {spec.Max} characters):" },
                    now is JsonValue v ? (spec.Kind == "str" && v.TryGetValue(out string? t) ? t : now.ToJsonString()) : "") is not { } typed) return;
            JsonNode value = spec.Kind switch
            {
                "int" => long.TryParse(typed.Trim(), out var n) ? JsonValue.Create(n) : throw new ArgumentException("Enter a whole number."),
                "bool" => typed.Trim().ToLowerInvariant() switch { "on" or "true" or "yes" or "1" => JsonValue.Create(true), "off" or "false" or "no" or "0" => JsonValue.Create(false), _ => throw new ArgumentException("Enter on or off.") },
                _ => JsonValue.Create(typed),
            };
            await Client.SaveSettingsAsync(_s.Schema, new Dictionary<string, JsonNode> { [spec.Key] = value });
            await LoadAsync();
        }

        private async Task MailAsync()
        {
            if (_s is not { CanMail: true } s) { Dialogs.Info("Email", "Your role can't change the email set-up."); return; }
            var m = s.Mail;
            if (Dialogs.Prompt("Email set-up", "SMTP server:", Read(m, "host")) is not { } host) return;
            if (Dialogs.Prompt("Email set-up", "Port:", m["port"]?.ToJsonString() ?? "587") is not { } portText || !int.TryParse(portText, out var port)) return;
            int sec = Dialogs.Choose("Email set-up", "Security (plain SMTP isn't allowed; certificates are always checked):", ["STARTTLS", "TLS"], cancelIndex: -1);
            if (sec < 0) return;
            if (Dialogs.Prompt("Email set-up", "SMTP username:", Read(m, "user")) is not { } user) return;
            var (pw, clear) = TestMode.Running ? (null, false) : Dialogs.PromptPassword("Email set-up", "New SMTP password (empty = keep the saved one). The server seals it and never shows it again.", "Remove the saved password");
            if (Dialogs.Prompt("Email set-up", "From address:", Read(m, "from")) is not { } from) return;
            var fromName = Dialogs.Prompt("Email set-up", "From name:", Read(m, "from_name")) ?? "";
            var replyTo = Dialogs.Prompt("Email set-up", "Reply-to (optional):", Read(m, "reply_to")) ?? "";
            await View.Step("Saving…", async () =>
            {
                await Client.SaveMailAsync(host.Trim(), port, sec == 0 ? "starttls" : "tls", user.Trim(), pw, clear, from.Trim(), fromName.Trim(), replyTo.Trim());
                await LoadAsync();
            });
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Client.SignedOut -= OnSignedOut;
    }
}
