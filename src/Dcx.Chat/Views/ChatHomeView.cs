using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CipherChat.Engine;
using DcxManager.Services;
using DcxManager.Views;

namespace DcxManager.Chat.Views;

/// <summary>
/// Chats (ADR-117): your CipherChat accounts and their rooms, as a native screen. On the left the accounts (paired PCs
/// stay signed in; each unlocks with its chat passphrase) and the selected account's rooms with unread counts; on the
/// right whatever the selected account needs next: pair or sign in, create the identity, unlock, confirm the owner's
/// safety number, or the room actions. A room opens as its own tab (double-click or Enter).
/// The passphrase is read from the box only for the call that needs it and the box is cleared at once; it is never stored.
/// </summary>
public sealed class ChatHomeView : UserControl, IDisposable
{
    private readonly ListBox _accounts = new() { MinHeight = 60, MaxHeight = 220 };
    private readonly ListBox _rooms = new();
    private readonly ContentControl _right = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private ChatEngine? _watched;
    private bool _disposed;

    public ChatHomeView()
    {
        var left = new DockPanel { Width = 300, Margin = new Thickness(0, 0, 12, 0) };
        var head = Label("ACCOUNTS");
        DockPanel.SetDock(head, Dock.Top); left.Children.Add(head);
        DockPanel.SetDock(_accounts, Dock.Top); left.Children.Add(_accounts);
        var add = new Button { Content = "+ Add an account…", Style = (Style)FindResource("SmallButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 10) };
        add.Click += (_, _) => { _accounts.SelectedItem = null; Render(); };
        DockPanel.SetDock(add, Dock.Top); left.Children.Add(add);
        var roomsHead = Label("ROOMS");
        DockPanel.SetDock(roomsHead, Dock.Top); left.Children.Add(roomsHead);
        left.Children.Add(_rooms);
        System.Windows.Automation.AutomationProperties.SetName(_accounts, "Chat accounts");
        System.Windows.Automation.AutomationProperties.SetName(_rooms, "Rooms");

        _accounts.SelectionChanged += (_, _) => { if (!_quiet) Render(); };
        _rooms.MouseDoubleClick += (_, _) => OpenSelectedRoom();
        _rooms.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; OpenSelectedRoom(); } };
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");

        var right = new DockPanel();
        DockPanel.SetDock(_status, Dock.Bottom);
        right.Children.Add(_status);
        right.Children.Add(new ScrollViewer { Content = _right, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        var grid = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(left, Dock.Left);
        grid.Children.Add(left); grid.Children.Add(right);
        Content = grid;
        SetResourceReference(BackgroundProperty, "Bg");

        ChatHub.Changed += OnHubChanged;
        ChatAccounts.Changed += OnHubChanged;
        Loaded += (_, _) => { RenderAccounts(); Render(); };
    }

    private TextBlock Label(string text)
    {
        var t = new TextBlock { Text = text, Margin = new Thickness(0, 0, 0, 4) };
        t.SetResourceReference(StyleProperty, "Label");
        return t;
    }

    private void OnHubChanged() { if (!_disposed) Dispatcher.BeginInvoke(() => { if (!_disposed) { RenderAccounts(); RenderIfChanged(); } }); }

    private void RenderIfChanged()
    {
        var s = Selected;
        if (_shown is { } x && ReferenceEquals(x.Session, s) && x.Stage == (s?.Stage ?? default) && x.Error == (s?.LastError is not null)) { RenderRooms(s); return; }
        Render();
    }

    private void Say(string text) => _status.Text = text;

    private async Task Step(string busy, Func<Task> step)
    {
        Say(busy);
        IsEnabled = false;
        try { await step(); Say(""); }
        catch (Exception ex) { Say(""); Dialogs.Error("CipherChat", ex.Message, ex: ex); }
        finally { IsEnabled = true; RenderAccounts(); RenderIfChanged(); }
    }

    /// <summary>Asks for the authenticator code when the server wants it again (on the UI thread).</summary>
    private Task<string?> AskCode(string? error) => Dispatcher.InvokeAsync(() =>
        Dialogs.Prompt("Confirm it's you", (error is { Length: > 0 } ? error + "\n\n" : "") + "Enter the current code from your authenticator app:")).Task;

    // ------------------------------------------------------------------------------------------- accounts

    private sealed record AccountItem(ChatAccount Account, ChatSession Session)
    {
        public override string ToString()
        {
            var s = Session;
            var state = s.LastError is not null ? "can't sign in" : s.Stage switch
            {
                ChatSession.Stages.Ready => "unlocked" + (s.Engine is { } en && en.Rooms.Sum(r => r.Unread) is > 0 and var n ? " · " + n + " unread" : ""),
                ChatSession.Stages.NeedsUnlock => "locked",
                ChatSession.Stages.NeedsIdentity => "needs set-up",
                ChatSession.Stages.NeedsOwnerCheck => "check the owner",
                _ => s.Api is null ? "signed out" : "signing in…",
            };
            return Account.Display + "  —  " + state;
        }
    }

    /// <summary>Rebuilds the account list keeping the selection (or selecting <see cref="_select"/>, an account just
    /// added). The right side is rebuilt only when the selected session or its stage changed, so typing isn't lost.</summary>
    private void RenderAccounts()
    {
        var keep = _select ?? (_accounts.SelectedItem as AccountItem)?.Account.Key;
        _select = null;
        _quiet = true;
        try
        {
            _accounts.Items.Clear();
            foreach (var a in ChatAccounts.All) _accounts.Items.Add(new AccountItem(a, ChatHub.For(a)));
            // A session signed in with a password (not paired) is listed too while it lasts.
            foreach (var s in ChatHub.Sessions.Where(s => s.Account is null && s.Api is not null))
                _accounts.Items.Add(new AccountItem(new ChatAccount(s.Origin, s.User, "", "", "", DateTime.UtcNow), s));
            _accounts.SelectedItem = _accounts.Items.OfType<AccountItem>().FirstOrDefault(i => i.Account.Key == keep)
                ?? (_first && _accounts.Items.Count > 0 ? _accounts.Items[0] : null);
            _first = false;
        }
        finally { _quiet = false; }
    }
    private bool _first = true, _quiet;
    private string? _select;
    private (ChatSession? Session, ChatSession.Stages Stage, bool Error)? _shown;

    private ChatSession? Selected => (_accounts.SelectedItem as AccountItem)?.Session;

    // ------------------------------------------------------------------------------------------- the right side

    private void Render()
    {
        var s = Selected;
        _shown = (s, s?.Stage ?? default, s?.LastError is not null);
        Watch(s?.Engine);
        RenderRooms(s);
        _right.Content = s is null ? AddAccountPanel() : s.Stage switch
        {
            ChatSession.Stages.Ready => ReadyPanel(s),
            ChatSession.Stages.NeedsUnlock => UnlockPanel(s),
            ChatSession.Stages.NeedsIdentity => IdentityPanel(s),
            ChatSession.Stages.NeedsOwnerCheck => OwnerPanel(s),
            _ => SignedOutPanel(s),
        };
    }

    private void Watch(ChatEngine? e)
    {
        if (ReferenceEquals(e, _watched)) return;
        if (_watched is not null) _watched.RoomsChanged -= OnRooms;
        _watched = e;
        if (e is not null) e.RoomsChanged += OnRooms;
    }

    /// <summary>Rooms or unread counts changed: the room list and the account line (its unread count) are redrawn.</summary>
    private void OnRooms() { if (!_disposed) Dispatcher.BeginInvoke(() => { if (!_disposed) { RenderRooms(Selected); _quiet = true; try { _accounts.Items.Refresh(); } finally { _quiet = false; } } }); }

    private sealed record RoomItem(Room Room, string Me)
    {
        public override string ToString() => Room.Label(Me) + (Room.Unread > 0 ? "   (" + Room.Unread + ")" : "");
    }

    private void RenderRooms(ChatSession? s)
    {
        var keep = (_rooms.SelectedItem as RoomItem)?.Room.Id;
        _rooms.Items.Clear();
        if (s?.Engine is not { } e) return;
        foreach (var r in e.Rooms.OrderByDescending(r => r.Unread > 0).ThenBy(r => r.Kind == "dm").ThenBy(r => r.Label(e.MyUser), StringComparer.OrdinalIgnoreCase))
            _rooms.Items.Add(new RoomItem(r, e.MyUser));
        _rooms.SelectedItem = _rooms.Items.OfType<RoomItem>().FirstOrDefault(i => i.Room.Id == keep);
    }

    private void OpenSelectedRoom()
    {
        if (Selected is not { Engine: not null } s || _rooms.SelectedItem is not RoomItem item) return;
        OpenRoom(s, item.Room);
    }

    /// <summary>Opens a room as a tab (or brings its tab forward).</summary>
    public static void OpenRoom(ChatSession s, Room r) =>
        ModuleTabs.Open(ChatRoomView.TabKey(s, r), "💬 " + r.Label(s.Engine!.MyUser), () => new ChatRoomView(s, r));

    private StackPanel Panel(string title, string hint)
    {
        var p = new StackPanel { MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left };
        var t = new TextBlock { Text = title, Margin = new Thickness(0, 0, 0, 6) };
        t.SetResourceReference(StyleProperty, "PageTitle");
        p.Children.Add(t);
        var h = new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        h.SetResourceReference(StyleProperty, "Hint");
        p.Children.Add(h);
        return p;
    }

    private static TextBox Field(string name, string text = "")
    {
        var t = new TextBox { Text = text, Margin = new Thickness(0, 2, 0, 8), MinWidth = 320 };
        System.Windows.Automation.AutomationProperties.SetName(t, name);
        return t;
    }

    private static PasswordBox Secret(string name)
    {
        var p = new PasswordBox { Margin = new Thickness(0, 2, 0, 8), MinWidth = 320 };
        System.Windows.Automation.AutomationProperties.SetName(p, name);
        return p;
    }

    private Button Primary(string text, Func<Task> click)
    {
        var b = new Button { Content = text, Style = (Style)FindResource("PrimaryButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 8, 0) };
        b.Click += async (_, _) => await click();
        return b;
    }

    private Button Plain(string text, Func<Task> click)
    {
        var b = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 8, 0) };
        b.Click += async (_, _) => await click();
        return b;
    }

    private static WrapPanel Buttons(params UIElement[] items) { var w = new WrapPanel(); foreach (var i in items) w.Children.Add(i); return w; }

    private FrameworkElement AddAccountPanel()
    {
        var p = Panel("Add a CipherChat account",
            "Pair this PC as one of your CipherChat devices (recommended): in CipherChat on another device, open Settings → Pair a device (or type /pair in the web chat) and paste the code here. " +
            "This PC then stays signed in to the chat (never to administration) until you remove it, and unlocks with your chat passphrase.");
        var code = Field("Pairing code");
        p.Children.Add(Label("PAIRING CODE")); p.Children.Add(code);
        p.Children.Add(Primary("Pair this PC", () => Step("Pairing…", async () =>
        {
            var s = new ChatSession { Ask = AskCode };
            await s.PairAsync(code.Text);
            code.Clear();
            _select = s.Account?.Key;
            ChatHub.Adopt(s);
        })));

        var sep = Label("OR SIGN IN WITH YOUR PASSWORD");
        sep.Margin = new Thickness(0, 22, 0, 4);
        p.Children.Add(sep);
        var hint = new TextBlock { Text = "Nothing is remembered on this PC: you sign in again next time (pair it to stay signed in).", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        hint.SetResourceReference(StyleProperty, "Hint");
        p.Children.Add(hint);
        var server = Field("Server", AppSettings.Current.ChatAllowedServers.FirstOrDefault() ?? "https://");
        var user = Field("Username");
        var pw = Secret("Password");
        var otp = Field("Authenticator code");
        p.Children.Add(Label("SERVER")); p.Children.Add(server);
        p.Children.Add(Label("USERNAME")); p.Children.Add(user);
        p.Children.Add(Label("PASSWORD")); p.Children.Add(pw);
        p.Children.Add(Label("AUTHENTICATOR CODE")); p.Children.Add(otp);
        p.Children.Add(Plain("Sign in", () => Step("Signing in…", async () =>
        {
            var s = new ChatSession { Ask = AskCode };
            var password = pw.Password; pw.Clear();
            await s.SignInAsync(server.Text, user.Text, password, otp.Text);
            otp.Clear();
            _select = s.Origin + "|" + s.User;
            ChatHub.Adopt(s);
        })));
        return p;
    }

    private FrameworkElement SignedOutPanel(ChatSession s)
    {
        var acc = (_accounts.SelectedItem as AccountItem)!.Account;
        var p = Panel(acc.Display, s.LastError is { } e ? "This PC couldn't sign in with its pairing: " + e : "This PC is paired with this account. Sign in to see your chats.");
        if (acc.DeviceToken.Length > 0)
        {
            p.Children.Add(Buttons(
                Primary("Sign in", () => Step("Signing in…", async () => { s.Ask = AskCode; await s.ResumeAsync(acc); })),
                Plain("Remove from this PC…", () => RemoveAsync(s, acc))));
        }
        return p;
    }

    private async Task RemoveAsync(ChatSession s, ChatAccount acc)
    {
        if (!Dialogs.Confirm("Remove account", $"Remove {acc.Display} from this PC? This PC is signed out and forgets its pairing (revoke the device in CipherChat too). Your chats stay on the server and your other devices.", "Remove", danger: true)) return;
        await Step("Removing…", async () => { if (s.Api is null) s.Ask = AskCode; await s.SignOutAsync(forgetPc: true); ChatAccounts.Remove(acc); ChatHub.Forget(s); });
    }

    private FrameworkElement IdentityPanel(ChatSession s)
    {
        var p = Panel("Set up your chat identity",
            "This is the first CipherChat device for " + s.Display + ". Choose a chat passphrase: it seals your keys (on the server, encrypted) and unlocks the chat on every device. " +
            "It is never stored or sent anywhere, and nobody (not the server, not an admin) can recover it. At least 12 characters; a few words works well.");
        var a = Secret("Chat passphrase"); var b = Secret("The same passphrase again");
        p.Children.Add(Label("CHAT PASSPHRASE")); p.Children.Add(a);
        p.Children.Add(Label("AGAIN")); p.Children.Add(b);
        p.Children.Add(Primary("Create my identity", () => Step("Creating your keys (this takes a few seconds)…", async () =>
        {
            var x = a.Password; var y = b.Password; a.Clear(); b.Clear();
            if (x.Length < 12) throw new ArgumentException("Use at least 12 characters.");
            if (x != y) throw new ArgumentException("The two passphrases differ.");
            s.Ask = AskCode;
            await s.CreateIdentityAsync(x, new Progress<string>(Say));
        })));
        return p;
    }

    private FrameworkElement UnlockPanel(ChatSession s)
    {
        var p = Panel("Unlock " + s.Display, "Enter your chat passphrase. It unlocks your keys in memory only; locking the app (or this chat) wipes them again.");
        var pass = Secret("Chat passphrase");
        p.Children.Add(Label("CHAT PASSPHRASE")); p.Children.Add(pass);
        async Task Go() => await Step("Unlocking…", async () => { var x = pass.Password; pass.Clear(); s.Ask = AskCode; await s.UnlockAsync(x, new Progress<string>(Say)); });
        pass.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await Go(); } };
        p.Children.Add(Buttons(Primary("Unlock", Go), Plain("Sign out", () => Step("Signing out…", () => s.SignOutAsync(forgetPc: false)))));
        Dispatcher.BeginInvoke(() => pass.Focus());
        return p;
    }

    private FrameworkElement OwnerPanel(ChatSession s)
    {
        var p = Panel("Check the server owner's safety number",
            "Compare these 60 digits with the owner of the server, in person or over a channel you already trust. They match only if nobody is in the middle. " +
            "You do this once; after that CipherChat trusts the people the owner has verified.");
        var number = new TextBox { Text = Group(s.OwnerSafetyNumber ?? ""), IsReadOnly = true, FontSize = 18, Margin = new Thickness(0, 0, 0, 12), TextWrapping = TextWrapping.Wrap };
        number.SetResourceReference(FontFamilyProperty, "MonoFont");
        p.Children.Add(number);
        var pass = Secret("Chat passphrase");
        p.Children.Add(Label("CHAT PASSPHRASE (TO SAVE THE CHECK)")); p.Children.Add(pass);
        p.Children.Add(Buttons(
            Primary("They match", () => Step("Saving…", async () => { var x = pass.Password; pass.Clear(); await s.ConfirmOwnerAsync(x); })),
            Plain("They don't match", async () =>
            {
                Dialogs.Info("Don't use this server", "If the numbers differ, someone may be between you and the server. Nothing was saved. Sign out and tell the owner.");
                await Step("Signing out…", () => s.SignOutAsync(forgetPc: false));
            })));
        return p;
    }

    private static string Group(string digits) => string.Join(" ", Enumerable.Range(0, (digits.Length + 4) / 5).Select(i => digits.Substring(i * 5, Math.Min(5, digits.Length - i * 5))));

    private FrameworkElement ReadyPanel(ChatSession s)
    {
        var e = s.Engine!;
        var p = Panel(s.Display, (e.Online ? "Online" : "Connecting…") + " · " + e.Rooms.Count + " rooms · end-to-end encrypted (post-quantum). Double-click a room to open it as a tab.");
        var buttons = new WrapPanel();
        if (e.CanCreateRooms) buttons.Children.Add(Plain("New room…", () => NewRoomAsync(s)));
        buttons.Children.Add(Plain("Message someone…", () => NewDmAsync(s)));
        buttons.Children.Add(Plain("Open room", () => { OpenSelectedRoom(); return Task.CompletedTask; }));
        buttons.Children.Add(Plain("Lock this chat", () => { s.Lock(); return Task.CompletedTask; }));
        if (s.Account is { } acc && acc.DeviceToken.Length > 0) buttons.Children.Add(Plain("Remove from this PC…", () => RemoveAsync(s, acc)));
        else buttons.Children.Add(Plain("Sign out", () => Step("Signing out…", () => s.SignOutAsync(forgetPc: false))));
        p.Children.Add(buttons);

        var people = Label("PEOPLE");
        people.Margin = new Thickness(0, 18, 0, 4);
        p.Children.Add(people);
        foreach (var person in e.People.Values.OrderBy(x => x.User, StringComparer.OrdinalIgnoreCase).Take(200))
        {
            var row = new TextBlock
            {
                Text = ChatRoomView.Clean(person.User) + (person.DisplayName.Length > 0 ? " (" + ChatRoomView.Clean(person.DisplayName) + ")" : "") + (person.Owner ? " · owner" : "")
                    + " — " + (person.Trust == TrustLevel.Ok ? "verified" : "not verified"),
                Margin = new Thickness(0, 1, 0, 1),
            };
            row.SetResourceReference(TextBlock.ForegroundProperty, person.Trust == TrustLevel.Ok ? "Fg" : "Warn");
            p.Children.Add(row);
        }
        return p;
    }

    private async Task NewRoomAsync(ChatSession s)
    {
        var e = s.Engine!;
        if (Dialogs.Prompt("New room", "Room name:") is not { Length: > 0 } name) return;
        var others = e.People.Keys.Where(u => u != e.MyUser).OrderBy(u => u).ToList();
        var members = Dialogs.Prompt("New room", "Add people (usernames, comma-separated). Only they can read it:\n" + string.Join(", ", others.Take(40)), "") ?? "";
        var list = members.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(u => e.People.ContainsKey(u) && u != e.MyUser).Distinct().ToList();
        await Step("Creating the room…", async () =>
        {
            var room = await e.CreateRoomAsync(name.Trim(), "", e.RetentionChoices.FirstOrDefault(), list);
            AuditLog.Write("you", "chat", "room created", "#" + room.Name + " (" + list.Count + " people)");
            OpenRoom(s, room);
        });
    }

    private async Task NewDmAsync(ChatSession s)
    {
        var e = s.Engine!;
        var others = e.People.Values.Where(p => p.User != e.MyUser).OrderBy(p => p.User).ToList();
        if (TestMode.Running) { Dialogs.RefusedInSelfTest("pick: message someone"); return; }
        if (Dialogs.Pick("Message someone", "Who do you want to message?", others, p => p.User + (p.DisplayName.Length > 0 ? " (" + p.DisplayName + ")" : "")) is not { } who) return;
        await Step("Opening…", async () => OpenRoom(s, await e.OpenDmAsync(who.User)));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ChatHub.Changed -= OnHubChanged;
        ChatAccounts.Changed -= OnHubChanged;
        Watch(null);
    }
}
