using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CipherChat.Engine;
using DcxManager.Chat.ChatUi;
using DcxManager.Services;
using DcxManager.Views;

namespace DcxManager.Chat.Views;

/// <summary>
/// Chats (ADR-117), laid out as CipherChat's own Windows client (0.6.41): on the left the sidebar — your accounts (paired
/// PCs stay signed in; each unlocks with its chat passphrase), then the selected account's ROOMS, DIRECT messages and
/// PEOPLE with their trust — and on the right the room itself (<see cref="ChatRoomView"/>: header, terminal log,
/// composer, status line) or whatever the account needs next: pair or sign in, create the identity, unlock, or confirm
/// the owner's safety number. Any room also opens as its own tab or in the side panel.
/// The passphrase is read from the box only for the call that needs it and the box is cleared at once; it is never stored.
/// </summary>
public sealed class ChatHomeView : UserControl, IDisposable
{
    private readonly StackPanel _accountList = new();
    private readonly StackPanel _accountActions = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 4, 0, 0) };
    private readonly StackPanel _chatLists = new();
    private readonly ContentControl _right = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24, 0, 24, 12) };
    private readonly Dictionary<ChatSession, string> _lastRoom = new();
    private ChatEngine? _watched;
    private ChatRoomView? _roomView;
    private string? _selectedKey;
    private bool _first = true, _disposed;
    private (ChatSession? Session, ChatSession.Stages Stage, bool Error)? _shown;

    public ChatHomeView()
    {
        Resources.MergedDictionaries.Add(ChatStyles.Dictionary);
        SetResourceReference(BackgroundProperty, "Bg");

        var side = new StackPanel { Margin = new Thickness(8, 10, 8, 10) };
        side.Children.Add(SideHeader("ACCOUNTS", "Add a CipherChat account (pair this PC, or sign in)", () => { _selectedKey = ""; RenderAccounts(); Render(); }));
        side.Children.Add(_accountList);
        side.Children.Add(_accountActions);
        side.Children.Add(_chatLists);
        System.Windows.Automation.AutomationProperties.SetName(_accountList, "Chat accounts");
        var sideBorder = new Border { BorderThickness = new Thickness(0, 0, 1, 0), Width = 260,
                                      Child = new ScrollViewer { Content = side, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } };
        sideBorder.SetResourceReference(Border.BackgroundProperty, "Surface");
        sideBorder.SetResourceReference(Border.BorderBrushProperty, "Line");

        _status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var right = new DockPanel();
        DockPanel.SetDock(_status, Dock.Bottom);
        right.Children.Add(_status);
        right.Children.Add(_right);

        // the sidebar gives way on a narrow screen (a split pane, a small window), as CipherChat's does
        SizeChanged += (_, e) => sideBorder.Width = Math.Clamp(Math.Round(e.NewSize.Width * 0.3), 170, 260);
        var root = new DockPanel();
        DockPanel.SetDock(sideBorder, Dock.Left);
        root.Children.Add(sideBorder);
        root.Children.Add(right);
        Content = root;

        ChatHub.Changed += OnHubChanged;
        ChatAccounts.Changed += OnHubChanged;
        Loaded += (_, _) => { RenderAccounts(); Render(); };
    }

    private FrameworkElement SideHeader(string text, string? addTip, Action? add, double top = 0)
    {
        var dock = new DockPanel { Margin = new Thickness(4, top, 0, 4) };
        if (add is not null)
        {
            var b = new Button { Content = "+", ToolTip = addTip, MinWidth = 0, Padding = new Thickness(8, 0, 8, 1) };
            b.SetResourceReference(StyleProperty, "SmallButton");
            System.Windows.Automation.AutomationProperties.SetName(b, addTip ?? text);
            b.Click += (_, _) => add();
            DockPanel.SetDock(b, Dock.Right);
            dock.Children.Add(b);
        }
        var t = new TextBlock { Text = text };
        t.SetResourceReference(StyleProperty, "ChatSideHeader");
        dock.Children.Add(t);
        return dock;
    }

    private static Button SideButton(object? content, string brush, string? tip = null)
    {
        var b = new Button { Content = content, ToolTip = tip };
        b.SetResourceReference(StyleProperty, "ChatSideItem");
        b.SetResourceReference(ForegroundProperty, brush);
        return b;
    }

    private static FrameworkElement Tagged(string text, string tag, string tagBrush)
    {
        var dock = new DockPanel();
        var t = new TextBlock { Text = tag, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        t.SetResourceReference(TextBlock.FontSizeProperty, "FsCaption");
        t.SetResourceReference(TextBlock.ForegroundProperty, tagBrush);
        DockPanel.SetDock(t, Dock.Right);
        dock.Children.Add(t);
        var name = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        ColorEmoji.SetText(name, text);
        dock.Children.Add(name);
        return dock;
    }

    private void OnHubChanged() { if (!_disposed) Dispatcher.BeginInvoke(() => { if (!_disposed) { RenderAccounts(); RenderIfChanged(); } }); }

    private void RenderIfChanged()
    {
        var s = Selected;
        if (_shown is { } x && ReferenceEquals(x.Session, s) && x.Stage == (s?.Stage ?? default) && x.Error == (s?.LastError is not null)) { RenderChatLists(); return; }
        Render();
    }

    private void Say(string text) => _status.Text = text;

    private async Task Step(string busy, Func<Task> step)
    {
        Say(busy);
        _right.IsEnabled = false;
        try { await step(); Say(""); }
        catch (Exception ex) { Say(""); Dialogs.Error("CipherChat", ex.Message, ex: ex); }
        finally { _right.IsEnabled = true; RenderAccounts(); RenderIfChanged(); }
    }

    /// <summary>Asks for the authenticator code when the server wants it again (on the UI thread).</summary>
    private Task<string?> AskCode(string? error) => Dispatcher.InvokeAsync(() =>
        Dialogs.Prompt("Confirm it's you", (error is { Length: > 0 } ? error + "\n\n" : "") + "Enter the current code from your authenticator app:")).Task;

    // ------------------------------------------------------------------------------------------- accounts

    private sealed record AccountItem(ChatAccount Account, ChatSession Session);

    private List<AccountItem> _items = new();

    private static (string Text, string Brush) StateOf(ChatSession s) => s.LastError is not null ? ("can't sign in", "Bad") : s.Stage switch
    {
        ChatSession.Stages.Ready => (s.Engine is { } en && en.Rooms.Sum(r => r.Unread) is > 0 and var n ? n + " unread" : "unlocked", "Good"),
        ChatSession.Stages.NeedsUnlock => ("locked", "Warn"),
        ChatSession.Stages.NeedsIdentity => ("set up", "Warn"),
        ChatSession.Stages.NeedsOwnerCheck => ("check owner", "Warn"),
        _ => (s.Api is null ? "signed out" : "signing in…", "Muted"),
    };

    /// <summary>Rebuilds the account rows keeping the selection ("" = adding an account; the first account at first).</summary>
    private void RenderAccounts()
    {
        _items = ChatAccounts.All.Select(a => new AccountItem(a, ChatHub.For(a))).ToList();
        // A session signed in with a password (not paired) is listed too while it lasts.
        foreach (var s in ChatHub.Sessions.Where(s => s.Account is null && s.Api is not null))
            _items.Add(new AccountItem(new ChatAccount(s.Origin, s.User, "", "", "", DateTime.UtcNow), s));
        if (_selectedKey is null || (_selectedKey.Length > 0 && _items.All(i => i.Account.Key != _selectedKey)))
            _selectedKey = _first || _selectedKey is not null ? _items.FirstOrDefault()?.Account.Key ?? "" : "";
        _first = false;
        _accountList.Children.Clear();
        foreach (var item in _items)
        {
            var (state, brush) = StateOf(item.Session);
            var b = SideButton(Tagged(ChatRoomView.Clean(item.Account.Display), state, brush), item.Account.Key == _selectedKey ? "Accent" : "Fg", item.Session.LastError);
            System.Windows.Automation.AutomationProperties.SetName(b, item.Account.Display + ", " + state);
            var key = item.Account.Key;
            b.Click += (_, _) => { if (_selectedKey == key) return; _selectedKey = key; RenderAccounts(); Render(); };
            _accountList.Children.Add(b);
        }
        if (_items.Count == 0)
        {
            var none = new TextBlock { Text = "none yet: add one with +", Margin = new Thickness(10, 2, 0, 4) };
            none.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            _accountList.Children.Add(none);
        }
        RenderAccountActions();
    }

    private AccountItem? SelectedItem => _items.FirstOrDefault(i => i.Account.Key == _selectedKey);
    private ChatSession? Selected => SelectedItem?.Session;

    private void RenderAccountActions()
    {
        _accountActions.Children.Clear();
        if (SelectedItem is not { } item) { _accountActions.Visibility = Visibility.Collapsed; return; }
        var s = item.Session;
        void Add(string text, string tip, Func<Task> click)
        {
            var b = new Button { Content = text, ToolTip = tip, MinWidth = 0 };
            b.SetResourceReference(StyleProperty, "SmallButton");
            b.Click += async (_, _) => await click();
            _accountActions.Children.Add(b);
        }
        if (s.Engine is not null) Add("🔒 Lock", "Wipe this chat's keys from memory (the PC stays paired)", () => { s.Lock(); AuditLog.Write("you", "chat", "chat locked", s.Display); return Task.CompletedTask; });
        if (item.Account.DeviceToken.Length > 0) Add("Remove…", "Forget this PC's pairing (revoke the device in CipherChat too)", () => RemoveAsync(s, item.Account));
        else if (s.Api is not null) Add("Sign out", "Sign out of this account", () => Step("Signing out…", () => s.SignOutAsync(forgetPc: false)));
        _accountActions.Visibility = _accountActions.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------------------------------- rooms, direct, people

    private void Watch(ChatEngine? e)
    {
        if (ReferenceEquals(e, _watched)) return;
        if (_watched is not null) _watched.RoomsChanged -= OnRooms;
        _watched = e;
        if (e is not null) e.RoomsChanged += OnRooms;
    }

    /// <summary>Rooms, unread counts or people changed: the lists and the account line are redrawn.</summary>
    private void OnRooms() { if (!_disposed) Dispatcher.BeginInvoke(() => { if (!_disposed) { RenderAccounts(); RenderChatLists(); KeepRoom(); } }); }

    private Room? CurrentRoom => _roomView is { } v && Selected?.Engine is { } e ? e.RoomById(v.RoomId) : null;

    private void RenderChatLists()
    {
        _chatLists.Children.Clear();
        if (Selected is not { Engine: { } e } s) return;
        var cur = _roomView?.RoomId;
        _chatLists.Children.Add(SideHeader("ROOMS", e.CanCreateRooms ? "New room" : null, e.CanCreateRooms ? () => ChatRoomView.NewRoomDialog(s, r => ShowRoom(s, r)) : null, 14));
        var rooms = new StackPanel(); var dms = new StackPanel(); var people = new StackPanel();
        foreach (var r in e.Rooms.OrderBy(r => r.Label(e.MyUser), StringComparer.Ordinal))
        {
            var b = SideButton(ColorEmoji.Content(ChatRoomView.Clean(r.Label(e.MyUser)) + (r.Unread > 0 ? "  (" + r.Unread + ")" : "")), r.Id == cur ? "Accent" : r.Unread > 0 ? "FgStrong" : "Fg");
            System.Windows.Automation.AutomationProperties.SetName(b, r.Label(e.MyUser) + (r.Unread > 0 ? ", " + r.Unread + " unread" : ""));
            if (r.Unread > 0) b.FontWeight = FontWeights.SemiBold;
            var room = r;
            b.Click += (_, _) => ShowRoom(s, room);
            b.MouseDoubleClick += (_, ev) => { ev.Handled = true; OpenRoom(s, room); };
            b.ToolTip = "Click to show here; double-click to open as a tab";
            (r.Kind == "dm" ? dms : rooms).Children.Add(b);
        }
        if (rooms.Children.Count == 0) rooms.Children.Add(Empty(e.CanCreateRooms ? "no rooms yet: + makes one" : "no rooms yet"));
        _chatLists.Children.Add(rooms);
        _chatLists.Children.Add(SideHeader("DIRECT", "New direct message", () => ChatRoomView.NewDmDialog(s, r => ShowRoom(s, r)), 12));
        if (dms.Children.Count == 0) dms.Children.Add(Empty("none yet"));
        _chatLists.Children.Add(dms);
        _chatLists.Children.Add(SideHeader("PEOPLE", null, null, 12));
        foreach (var p in e.People.Values.OrderBy(p => p.User, StringComparer.Ordinal).Take(500))
        {
            var (tag, brush) = p.Trust switch { TrustLevel.Ok => ("verified", "Good"), TrustLevel.Pending => ("pending", "Warn"), TrustLevel.None => ("no keys", "Muted"), _ => ("untrusted", "Bad") };
            var name = ChatRoomView.Clean(p.User) + (p.Owner ? " *" : "") + (AddonIdentity.IsAddon(p.User) ? "  · add-on" : "") + (p.User == e.MyUser ? " (you)" : "");
            var b = SideButton(Tagged(name, tag, brush), "Fg", ChatRoomView.Clean(p.Why));
            System.Windows.Automation.AutomationProperties.SetName(b, name + ", " + tag);
            var person = p;
            b.Click += (_, _) => ChatRoomView.PersonDialog(s, person, r => ShowRoom(s, r));
            people.Children.Add(b);
        }
        _chatLists.Children.Add(people);
    }

    private static TextBlock Empty(string text)
    {
        var t = new TextBlock { Text = text, Margin = new Thickness(10, 2, 0, 4) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        t.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        t.SetResourceReference(TextBlock.FontSizeProperty, "FsCaption");
        return t;
    }

    /// <summary>Shows a room on the right (the chat screen's own view of it).</summary>
    private void ShowRoom(ChatSession s, Room r)
    {
        if (!ReferenceEquals(Selected, s) || s.Engine is null) { OpenRoom(s, r); return; }
        _lastRoom[s] = r.Id;
        if (_roomView?.RoomId == r.Id && _right.Content == _roomView) return;
        _roomView?.Dispose();
        _roomView = new ChatRoomView(s, r) { OnChatScreen = true, OpenElsewhere = room => ShowRoom(s, room) };
        _right.Content = _roomView;
        RenderChatLists();
    }

    /// <summary>The room on screen was left or deleted: show another (or the empty screen).</summary>
    private void KeepRoom()
    {
        if (Selected is not { Engine: { } e } s || _right.Content != _roomView || _roomView is null) return;
        if (e.RoomById(_roomView.RoomId) is not null) return;
        _roomView.Dispose(); _roomView = null;
        ShowFirstRoom(s);
    }

    private void ShowFirstRoom(ChatSession s)
    {
        var e = s.Engine!;
        var room = (_lastRoom.TryGetValue(s, out var id) ? e.RoomById(id) : null)
            ?? e.Rooms.OrderBy(r => r.Kind == "room" ? 0 : 1).ThenBy(r => r.Label(e.MyUser), StringComparer.Ordinal).FirstOrDefault();
        if (room is not null) { ShowRoom(s, room); return; }
        _right.Content = NoRoomPanel(s);
        RenderChatLists();
    }

    /// <summary>Opens a room as a tab (or brings its tab forward).</summary>
    public static void OpenRoom(ChatSession s, Room r) =>
        ModuleTabs.Open(ChatRoomView.TabKey(s, r), "💬 " + r.Label(s.Engine!.MyUser), () => new ChatRoomView(s, r));

    // ------------------------------------------------------------------------------------------- the right side

    private void Render()
    {
        var s = Selected;
        _shown = (s, s?.Stage ?? default, s?.LastError is not null);
        Watch(s?.Engine);
        RenderAccountActions();
        if (s is { Stage: ChatSession.Stages.Ready, Engine: not null })
        {
            RenderChatLists();
            if (_roomView is not null && ReferenceEquals(_roomView.Session, s) && s.Engine.RoomById(_roomView.RoomId) is not null) { _right.Content = _roomView; return; }
            _roomView?.Dispose(); _roomView = null;
            ShowFirstRoom(s);
            return;
        }
        _roomView?.Dispose(); _roomView = null;
        RenderChatLists();
        _right.Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = s is null ? AddAccountPanel() : s.Stage switch
            {
                ChatSession.Stages.NeedsUnlock => UnlockPanel(s),
                ChatSession.Stages.NeedsIdentity => IdentityPanel(s),
                ChatSession.Stages.NeedsOwnerCheck => OwnerPanel(s),
                _ => SignedOutPanel(s),
            },
        };
    }

    private FrameworkElement NoRoomPanel(ChatSession s)
    {
        var e = s.Engine!;
        var p = Panel(s.Display, (e.Online ? "Online" : "Connecting…") + " · end-to-end encrypted (post-quantum). "
            + (e.CanCreateRooms ? "No rooms yet: create one with the + next to ROOMS, or message someone with the + next to DIRECT." : "No rooms yet: message someone with the + next to DIRECT, or ask an admin to add you to a room."));
        return new ScrollViewer { Content = p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private TextBlock Label(string text)
    {
        var t = new TextBlock { Text = text, Margin = new Thickness(0, 0, 0, 4) };
        t.SetResourceReference(StyleProperty, "Label");
        return t;
    }

    private StackPanel Panel(string title, string hint)
    {
        var p = new StackPanel { MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(24, 20, 24, 12) };
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
            _selectedKey = s.Account?.Key;
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
            _selectedKey = s.Origin + "|" + s.User;
            ChatHub.Adopt(s);
        })));
        return p;
    }

    private FrameworkElement SignedOutPanel(ChatSession s)
    {
        var acc = SelectedItem!.Account;
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
        // 0.6.42 (DarkCipher0x): the passphrase can be saved on this PC, encrypted in the app's store (which opens only with
        // Windows Hello or the master password); a saved one unlocks after Windows Hello (ChatPassphrases).
        var key = ChatPassphrases.KeyOf(s);
        bool saved = ChatPassphrases.IsSaved(key);
        var p = Panel("Unlock " + s.Display, saved
            ? "Your chat passphrase is saved on this PC (encrypted). Unlock with Windows Hello, or type the passphrase. Either way your keys are opened in memory only; locking the app (or this chat) wipes them again."
            : "Enter your chat passphrase. It unlocks your keys in memory only; locking the app (or this chat) wipes them again.");
        var pass = Secret("Chat passphrase");
        p.Children.Add(Label("CHAT PASSPHRASE")); p.Children.Add(pass);
        CheckBox? remember = null;
        if (!saved && ChatPassphrases.CanSave)
        {
            remember = new CheckBox { Content = "Save on this PC: encrypted in the app's store, which only opens with Windows Hello or your master password. Next time unlock with Windows Hello, and the chat unlocks by itself when you unlock the app.", Margin = new Thickness(0, 0, 0, 10) };
            if (remember.Content is string text) remember.Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
            p.Children.Add(remember);
        }
        else if (!saved && AppSettings.Current.ChatAllowRemember)
        {
            var hint = new TextBlock { Text = "To save the passphrase on this PC, turn on Windows Hello sign-in for the app first (Settings → Security & privacy): without it, anyone signed in to this Windows account could open it.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
            hint.SetResourceReference(StyleProperty, "Hint");
            p.Children.Add(hint);
        }
        async Task Go() => await Step("Unlocking…", async () =>
        {
            var x = pass.Password; pass.Clear(); s.Ask = AskCode;
            await s.UnlockAsync(x, new Progress<string>(Say));
            if (remember?.IsChecked == true) ChatPassphrases.Save(key, x, s.Display);
        });
        async Task Hello()
        {
            var result = ChatPassphrases.Result.NotSaved;
            await Step("Waiting for Windows Hello…", async () =>
            {
                s.Ask = AskCode;
                result = await ChatPassphrases.UnlockAsync(s, askHello: true, "unlocked with Windows Hello", new Progress<string>(Say));
            });
            // said in the status line (not a dialog): the passphrase field is right there
            Say(result switch
            {
                ChatPassphrases.Result.Refused => "The saved passphrase isn't right any more (changed on another device?). It was forgotten: type the passphrase.",
                ChatPassphrases.Result.NotSaved => "Windows Hello isn't available right now: type the passphrase.",
                ChatPassphrases.Result.Cancelled => "Windows Hello was cancelled; the chat stays locked.",
                _ => "",
            });
        }
        _unlockWithHello = saved ? Hello : null;
        pass.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await Go(); } };
        p.Children.Add(saved
            ? Buttons(Primary("Unlock with Windows Hello", Hello), Plain("Unlock", Go),
                Plain("Forget saved passphrase", () => { ChatPassphrases.Forget(key, s.Display, "forgotten at the unlock"); Render(); return Task.CompletedTask; }),
                Plain("Sign out", () => Step("Signing out…", () => s.SignOutAsync(forgetPc: false))))
            : Buttons(Primary("Unlock", Go), Plain("Sign out", () => Step("Signing out…", () => s.SignOutAsync(forgetPc: false)))));
        if (!saved) Dispatcher.BeginInvoke(() => pass.Focus());
        _unlockPassForTest = pass; _rememberForTest = remember; _unlockGo = Go;
        return p;
    }

    private Func<Task>? _unlockWithHello;
    private PasswordBox? _unlockPassForTest;
    private CheckBox? _rememberForTest;
    private Func<Task>? _unlockGo;

    /// <summary>Self-test: types the passphrase in the unlock panel, ticks "Save on this PC" (when offered) and unlocks, as
    /// the user does.</summary>
    internal async Task UnlockForTest(string passphrase, bool save)
    {
        if (_unlockPassForTest is null) throw new InvalidOperationException("the unlock panel isn't showing");
        if (save && _rememberForTest is null) throw new InvalidOperationException("“Save on this PC” isn't offered");
        if (_rememberForTest is not null) _rememberForTest.IsChecked = save;
        _unlockPassForTest.Password = passphrase;
        await _unlockGo!();
    }

    /// <summary>Self-test: clicks "Unlock with Windows Hello" (null when the panel doesn't offer it).</summary>
    internal Func<Task>? UnlockWithHelloForTest => _unlockWithHello;

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

    // ------------------------------------------------------------------------------------------- self-test

    /// <summary>Self-test: selects an account's session (as clicking its row does).</summary>
    internal void SelectForTest(ChatSession s)
    {
        RenderAccounts();
        _selectedKey = _items.FirstOrDefault(i => ReferenceEquals(i.Session, s))?.Account.Key ?? _selectedKey;
        RenderAccounts();
        Render();
    }

    /// <summary>Self-test: shows a room on the right (as clicking it in the sidebar does).</summary>
    internal void ShowRoomForTest(ChatSession s, Room r) => ShowRoom(s, r);

    internal ChatRoomView? RoomViewForTest => _right.Content as ChatRoomView;

    /// <summary>Self-test: every text in the sidebar.</summary>
    internal IReadOnlyList<string> SideTextsForTest =>
        new DependencyObject[] { _accountList, _chatLists }
            .SelectMany(d => ChatRoomView.ElementsIn<TextBlock>(d).Select(t => t.Text).Concat(ChatRoomView.ElementsIn<ContentControl>(d).Select(c => c.Content as string ?? "")))
            .Concat(["ACCOUNTS"]).Where(t => t.Length > 0).ToList();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ChatHub.Changed -= OnHubChanged;
        ChatAccounts.Changed -= OnHubChanged;
        Watch(null);
        _roomView?.Dispose();
    }
}
