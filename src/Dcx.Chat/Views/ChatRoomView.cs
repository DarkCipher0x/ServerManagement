using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CipherChat.Crypto;
using CipherChat.Engine;
using DcxManager.Chat.ChatUi;
using DcxManager.Services;
using DcxManager.Views;
using Room = CipherChat.Engine.Room;

namespace DcxManager.Chat.Views;

/// <summary>
/// One CipherChat room or DM (ADR-117), drawn as CipherChat's own Windows client draws it (0.6.41): the room header with
/// its key and the call, members and settings buttons; the terminal log (time, name, message) with right-click React,
/// Reply, Edit, Copy and Delete; the compose line (replying, editing, burn, files) and the prompt with emoji, GIF, burn,
/// attach and send; slash commands (/help); and the status line. It is the chat screen's right side, a document tab
/// (pin, split, pop out) and the side panel's room. Everything is the engine's: this view only draws what it holds and
/// calls it. Engine events arrive off the UI thread and are posted here; the timeline is read under its own lock.
/// Message text is plain text only (nothing is interpreted) with control characters dropped; decrypted pictures are
/// decoded in memory and shown only when their bytes are a PNG, JPEG or GIF; files are only ever saved, never opened.
/// </summary>
public sealed class ChatRoomView : UserControl, IDisposable
{
    private static readonly int[] BurnSteps = [0, 10, 30, 60, 300];
    private const long GifInlineMax = 16L * 1048576;           // bigger GIFs show their still preview
    private const int MaxText = 8000;                         // as CipherChat's message box

    private readonly ChatSession _session;
    private readonly ChatEngine _e;
    private readonly Room _room;
    private readonly Dictionary<Entry, FrameworkElement> _rows = new();
    private readonly List<ChatEngine.OutgoingFile> _files = new();
    private ChatMessage? _reply, _edit;
    private int _burn;
    private Action<string>? _emojiPick;
    private bool _disposed, _opened, _compact;

    // header
    private readonly TextBlock _title = new() { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _topic = new() { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _keyInfo = new() { Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Panel _headButtons;
    private readonly List<TextBlock> _headLabels = new(), _sendLabel = new();
    private bool _narrow;
    private readonly StackPanel _tools = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly Button _voice, _video, _members, _settings, _pin, _asTab;
    // log
    private readonly StackPanel _log = new();
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(14, 8, 14, 8) };
    private readonly Button _older = new() { Content = "load older messages", HorizontalAlignment = HorizontalAlignment.Center, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 6) };
    // composer
    private readonly StackPanel _composeBar = new() { Margin = new Thickness(0, 0, 0, 6), Visibility = Visibility.Collapsed };
    private readonly TextBlock _prompt = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly EmojiTextBox _input = new() { MaxHeight = 180, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxLength = MaxText };
    private readonly TextBlock _placeholder = new() { Text = "message, or /help", IsHitTestVisible = false, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _emojiButton, _gifButton, _burnButton, _attach, _send;
    private readonly TextBlock _status = new() { TextTrimming = TextTrimming.CharacterEllipsis };
    // popups
    private readonly Popup _emojiPopup = new() { StaysOpen = false, Placement = PlacementMode.Top, AllowsTransparency = true };
    private readonly TextBox _emojiSearch = new() { Margin = new Thickness(0, 0, 0, 6) };
    private readonly WrapPanel _emojiGrid = new();
    private readonly Popup _gifPopup = new() { StaysOpen = false, Placement = PlacementMode.Top, AllowsTransparency = true };
    private readonly WrapPanel _gifGrid = new();
    private readonly TextBlock _gifEmpty = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6), Text = "None yet. GIFs you send, and GIFs that play in your rooms, show up here so you can send them again. You can also paste or drop a GIF into the chat." };

    /// <summary>Shown in the side panel: no Pin button (it's already pinned) and icon-only header buttons.</summary>
    public bool InPanel
    {
        get => _compact;
        init
        {
            _compact = value;
            _pin.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            Fit(ActualWidth);
        }
    }

    /// <summary>Shown on the chat screen beside the room list: offers "Open as tab".</summary>
    public bool OnChatScreen { init => _asTab.Visibility = value ? Visibility.Visible : Visibility.Collapsed; }

    /// <summary>Where a room this view switches to opens (/join, a DM from a person, a new room): the chat screen shows it
    /// in place; a tab or the panel opens it as a tab.</summary>
    public Action<Room>? OpenElsewhere { get; init; }

    /// <summary>The room shown (its id) and the chat it belongs to.</summary>
    public string RoomId => _room.Id;
    internal ChatSession Session => _session;

    /// <summary>The key a room's tab is opened with (one tab per room per account).</summary>
    public static string TabKey(ChatSession s, Room r) => "chat:" + s.Origin + "|" + s.User + "|" + r.Id;

    public ChatRoomView(ChatSession session, Room room)
    {
        _session = session;
        _e = session.Engine ?? throw new InvalidOperationException("Unlock the chat first.");
        _room = room;
        Resources.MergedDictionaries.Add(ChatStyles.Dictionary);
        SetResourceReference(BackgroundProperty, "Bg");
        Focusable = false;

        // ---- header: #room  topic / key #n · a/b hold it · age            [Voice] [Video] [Members] [Room settings] [Pin]
        _title.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        _title.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        _title.SetResourceReference(TextBlock.FontSizeProperty, "FsBase");
        _topic.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _keyInfo.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        _keyInfo.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _keyInfo.SetResourceReference(TextBlock.FontSizeProperty, "FsCaption");
        _keyInfo.ToolTip = "The room's current key (epoch). Every member it was made for is listed in its signed header.";
        _voice = HeadButton("\uE717", "Voice", "Voice call (end-to-end encrypted, post-quantum)", () => StartCall("audio"));
        _video = HeadButton("\uE714", "Video", "Video call (end-to-end encrypted, post-quantum)", () => StartCall("video"));
        _members = HeadButton("\uE716", "Members", "Who is in this room, who holds the key, and whether you've verified them", MembersDialog);
        _settings = HeadButton("\uE713", "Room settings", "Name, topic, how long messages and keys are kept, delete the room", RoomSettingsDialog);
        _pin = HeadButton("\uE718", "Pin", "Pin this room to the side panel, beside whatever tab you have open", () => ChatPanelView.Pin(_session, _room));
        _asTab = HeadButton("\uE8A7", "Open as tab", "Open this room as its own tab (pin, split, pop out)", () => ChatHomeView.OpenRoom(_session, _room));
        _asTab.Visibility = Visibility.Collapsed;
        _headButtons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        foreach (var b in new[] { _voice, _video, _members, _settings, _pin, _asTab }) _headButtons.Children.Add(b);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { _title, _topic } });
        titles.Children.Add(_keyInfo);
        var headDock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_headButtons, Dock.Right);
        headDock.Children.Add(_headButtons);
        headDock.Children.Add(titles);
        var header = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(14, 8, 10, 8), Child = headDock };
        header.SetResourceReference(Border.BorderBrushProperty, "Line");
        header.SetResourceReference(Border.BackgroundProperty, "Surface2");

        // ---- status line
        _status.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        _status.SetResourceReference(TextBlock.FontSizeProperty, "FsCaption");
        _status.SetResourceReference(TextBlock.ForegroundProperty, "HeaderMuted");
        var status = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(12, 4, 12, 4), Child = _status };
        status.SetResourceReference(Border.BorderBrushProperty, "Line");
        status.SetResourceReference(Border.BackgroundProperty, "HeaderBg");

        // ---- composer: [compose bar]  user@cipherchat:#room$ [message box]  ☺ GIF 🔥 📎 Send
        _prompt.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        _prompt.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        _prompt.SetResourceReference(TextBlock.FontSizeProperty, "FsControl");
        _input.SetResourceReference(StyleProperty, "ChatComposeBox");
        _placeholder.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        _placeholder.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _placeholder.SetResourceReference(TextBlock.FontSizeProperty, "FsControl");
        System.Windows.Automation.AutomationProperties.SetName(_input, "Message");
        _input.PreviewKeyDown += OnInputKey;
        _input.TextChanged += (_, _) => _placeholder.Visibility = _input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _emojiButton = ToolButton("\uE76E", icon: true, "Emoji", "Insert emoji", () => OpenEmoji(s => { _input.InsertAtCaret(s); _input.Focus(); }, null));
        _gifButton = ToolButton("GIF", icon: false, "Send a GIF", "Send a GIF", OpenGifs);
        _gifButton.FontWeight = FontWeights.SemiBold;
        _burnButton = ToolButton("🔥", icon: false, "Burn after reading (off)", "Burn after reading", NextBurn);
        _burnButton.Content = ColorEmoji.Content("🔥");
        _attach = ToolButton("\uE723", icon: true, "Attach files, pictures or videos (encrypted on this PC before upload)", "Attach files", AddFiles);
        _send = new Button { Margin = new Thickness(6, 0, 0, 0), Content = IconText("\uE724", "Send", _sendLabel), ToolTip = "Send (Enter). Shift+Enter is a new line." };
        _send.SetResourceReference(StyleProperty, "PrimaryButton");
        _send.MinWidth = 0;
        System.Windows.Automation.AutomationProperties.SetName(_send, "Send");
        _send.Click += async (_, _) => await SendAsync();
        var tools = _tools;
        foreach (var b in new[] { _emojiButton, _gifButton, _burnButton, _attach, _send }) tools.Children.Add(b);
        var box = new Grid();
        box.Children.Add(_input);
        box.Children.Add(_placeholder);
        var line = new DockPanel();
        DockPanel.SetDock(tools, Dock.Right);
        DockPanel.SetDock(_prompt, Dock.Left);
        line.Children.Add(tools); line.Children.Add(_prompt); line.Children.Add(box);
        var composerStack = new StackPanel();
        composerStack.Children.Add(_composeBar);
        composerStack.Children.Add(line);
        var composer = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(12, 8, 12, 8), Child = composerStack };
        composer.SetResourceReference(Border.BorderBrushProperty, "Line");
        composer.SetResourceReference(Border.BackgroundProperty, "Surface");

        // ---- the log
        _older.SetResourceReference(StyleProperty, "SmallButton");
        _older.Click += async (_, _) => await Run(async () => { await _e.LoadOlderAsync(_room); RenderHeader(); });
        var logStack = new StackPanel();
        logStack.Children.Add(_older);
        logStack.Children.Add(_log);
        _scroll.Content = logStack;
        _scroll.SetResourceReference(BackgroundProperty, "Bg");

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(status, Dock.Bottom);
        DockPanel.SetDock(composer, Dock.Bottom);
        root.Children.Add(header); root.Children.Add(status); root.Children.Add(composer); root.Children.Add(_scroll);
        var grid = new Grid();
        grid.Children.Add(root);
        grid.Children.Add(BuildEmojiPopup());
        grid.Children.Add(BuildGifPopup());
        Content = grid;

        AllowDrop = true;
        Drop += OnDrop;
        // A text box turns Paste off when the clipboard holds no text, so files and pictures are caught at the command
        CommandManager.AddPreviewCanExecuteHandler(_input, OnCanPaste);
        CommandManager.AddPreviewExecutedHandler(_input, OnPaste);

        _e.EntryAdded += OnAdded;
        _e.EntryChanged += OnChanged;
        _e.EntryRemoved += OnRemoved;
        _e.RoomsChanged += OnRoomsChanged;
        _e.OnlineChanged += OnOnline;
        _e.Notice += OnNotice;
        _e.RoomReset += OnReset;
        _session.StageChanged += OnStage;
        IsKeyboardFocusWithinChanged += (_, _) => Focused();
        SizeChanged += (_, e) => Fit(e.NewSize.Width);
        IsVisibleChanged += (_, _) => { if (IsVisible) Focused(); else if (_e.Viewing == _room) _e.Viewing = null; };
        Loaded += async (_, _) =>
        {
            if (_opened) { Focused(); return; }      // a tab switched back: everything on screen is current already
            _opened = true;
            await OpenAsync();
        };
    }

    // ------------------------------------------------------------------------------------------- narrow layouts

    /// <summary>Fits the room to its width (a split pane, the side panel, a small window): icon-only header buttons
    /// below 820 px, no prompt below 540 px (the box keeps the room), an icon-only Send below 500 px, a narrower name
    /// column below 560 px, and the composer's tools under the box below 480 px. Every button keeps its name for screen readers and its tooltip.</summary>
    private void Fit(double width)
    {
        if (width <= 0) return;
        foreach (var l in _headLabels) l.Visibility = _compact || width < 820 ? Visibility.Collapsed : Visibility.Visible;
        _prompt.Visibility = _compact || width < 540 ? Visibility.Collapsed : Visibility.Visible;
        _prompt.MaxWidth = Math.Max(140, Math.Round(width * 0.34));
        foreach (var l in _sendLabel) l.Visibility = _compact || width < 500 ? Visibility.Collapsed : Visibility.Visible;
        // below 480 px the tools go under the message box, so the box keeps the width
        var under = width < 480;
        if ((DockPanel.GetDock(_tools) == Dock.Bottom) != under)
        {
            DockPanel.SetDock(_tools, under ? Dock.Bottom : Dock.Right);
            _tools.Margin = new Thickness(0, under ? 6 : 0, 0, 0);
        }
        var narrow = _compact || width < 560;
        if (narrow == _narrow) return;
        _narrow = narrow;
        foreach (var row in _log.Children.OfType<Grid>().Where(g => g.ColumnDefinitions.Count == 3)) row.ColumnDefinitions[1].Width = new GridLength(NickWidth);
    }

    // ------------------------------------------------------------------------------------------- building blocks

    private static StackPanel IconText(string glyph, string text, List<TextBlock>? labels = null)
    {
        var icon = new TextBlock { Text = glyph, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        labels?.Add(label);
        return new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, label } };
    }

    private Button HeadButton(string glyph, string text, string tip, Action click)
    {
        var b = new Button { Content = IconText(glyph, text, _headLabels), ToolTip = tip, Margin = new Thickness(6, 2, 0, 2) };
        b.SetResourceReference(StyleProperty, "SmallButton");
        System.Windows.Automation.AutomationProperties.SetName(b, text);
        b.Click += (_, _) => click();
        return b;
    }

    private Button ToolButton(string content, bool icon, string tip, string name, Action click)
    {
        var b = new Button { Content = content, ToolTip = tip, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(6, 0, 0, 0), MinWidth = 0 };
        b.SetResourceReference(StyleProperty, "SmallButton");
        if (icon) { b.SetResourceReference(FontFamilyProperty, "IconFont"); b.FontSize = 14; }
        System.Windows.Automation.AutomationProperties.SetName(b, name);
        b.Click += (_, _) => click();
        return b;
    }

    private static TextBlock Small(string text, string brush, bool meta = false)
    {
        var t = new TextBlock { Margin = new Thickness(0, 0, 8, 0), TextWrapping = TextWrapping.Wrap };
        ColorEmoji.SetText(t, text);
        t.SetResourceReference(TextBlock.FontSizeProperty, meta ? "FsMeta" : "FsCaption");
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        t.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        return t;
    }

    private Button LinkButton(string text, Action click)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(2, 0, 2, 0) };
        b.SetResourceReference(StyleProperty, "ChatLink");
        b.Click += (_, _) => click();
        return b;
    }

    private void Ui(Action a) { if (!_disposed) Dispatcher.BeginInvoke(() => { if (!_disposed) a(); }); }

    private static async Task Run(Func<Task> step)
    {
        try { await step(); }
        catch (Exception ex) { Dialogs.Error("CipherChat", ex.Message, ex: ex); }
    }

    /// <summary>Message text as plain text: control characters (other than new lines and tabs) are dropped.</summary>
    internal static string Clean(string s) => new(s.Where(c => c == '\n' || c == '\t' || !char.IsControl(c)).Take(20_000).ToArray());

    private static string FirstLine(string s, int max) { var l = Clean(s).Split('\n')[0]; return l.Length > max ? l[..max] + "…" : l; }

    internal static string Size(long b) => b < 1024 ? b + " B" : b < 1024 * 1024 ? (b / 1024.0).ToString("0.0") + " KB" : (b / 1048576.0).ToString("0.0") + " MB";

    /// <summary>How long ago, as CipherChat's web chat writes it in the room header ("21h").</summary>
    private static string Ago(long unix)
    {
        var s = Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - unix);
        return s < 60 ? s + "s" : s < 3600 ? s / 60 + "m" : s < 86_400 ? s / 3600 + "h" : s / 86_400 + "d";
    }

    // ------------------------------------------------------------------------------------------- the room

    private async Task OpenAsync()
    {
        _files.Clear(); _reply = null; _edit = null; RenderBar();
        _log.Children.Clear(); _rows.Clear();
        List<Entry> snapshot;
        lock (_room.Timeline) snapshot = _room.Timeline.ToList();
        foreach (var en in snapshot) Insert(en, -1);
        RenderHeader(); RenderStatus();
        await Run(() => _e.OpenRoomAsync(_room));
        RenderHeader();
        await Run(() => _e.MaybeAutoRotateAsync(_room));
        Focused();
        ScrollEnd(true);
        if (IsVisible && !_compact) _input.Focus();
    }

    /// <summary>Messages in the room you're looking at aren't unread.</summary>
    private void Focused()
    {
        if (_disposed || !IsVisible || _session.Engine is null) return;
        _e.Viewing = _room;
        _e.MarkRead(_room);
    }

    private void RenderHeader()
    {
        var label = _room.Label(_e.MyUser);
        ColorEmoji.SetText(_title, Clean(label));
        ColorEmoji.SetText(_topic, Clean(_room.Topic));
        _keyInfo.Text = _room.Epoch > 0
            ? "key #" + _room.Epoch + " · " + _room.EpochRecipients.Count + "/" + _room.Members.Count + " hold it" + (_room.EpochAt > 0 ? " · " + Ago(_room.EpochAt) + " old" : "") + (_room.NeedsRotation ? " · renewal due" : "")
            : "no key yet";
        _prompt.Text = Clean(_e.MyUser) + "@cipherchat:" + Clean(label) + "$";
        _prompt.ToolTip = _prompt.Text;
        var calls = ChatPolicy.CallsBlocked() is null && _room.Members.Any(m => m.User != _e.MyUser);
        _voice.Visibility = _video.Visibility = calls ? Visibility.Visible : Visibility.Collapsed;
        _settings.Visibility = _room.Kind == "room" && (_room.MyRole == "admin" || _e.IAmOwner) ? Visibility.Visible : Visibility.Collapsed;
        _older.Visibility = _room.MoreHistory ? Visibility.Visible : Visibility.Collapsed;
        _attach.IsEnabled = _gifButton.IsEnabled = ChatPolicy.FilesBlocked() is null;
        _attach.ToolTip = ChatPolicy.FilesBlocked() ?? "Attach files, pictures or videos (encrypted on this PC before upload)";
    }

    private void RenderStatus()
    {
        if (_session.Engine is null) return;
        var me = _e.People.GetValueOrDefault(_e.MyUser);
        var mins = Math.Clamp(AppSettings.Current.ChatAutoLockMinutes, 0, 1440);
        _status.Inlines.Clear();
        var dot = new Run(_e.Online ? "● online" : "○ connecting");
        dot.SetResourceReference(TextElement.ForegroundProperty, _e.Online ? "Good" : "Warn");
        _status.Inlines.Add(dot);
        _status.Inlines.Add(new Run("   ML-KEM-1024+X25519 · ML-DSA-87+Ed25519 · AES-256-GCM · TLS X25519MLKEM768   id " + _e.Me.Fp[..16] + " "
            + (me?.Trust == TrustLevel.Ok ? "verified" : "awaiting certification") + "   auto-lock " + (mins == 0 ? "off" : mins + "m")));
        _status.ToolTip = "Post-quantum hybrid end-to-end encryption, CipherChat's own engine. Your identity fingerprint: " + Hybrid.FpGroups(_e.Me.Fp);
    }

    private void OnRoomsChanged() => Ui(() => { if (_session.Engine is not null) { RenderHeader(); RenderStatus(); } });
    private void OnOnline(bool _) => Ui(RenderStatus);
    private void OnReset(Room r) => Ui(async () => { if (r == _room) await OpenAsync(); });
    private void OnNotice(Room? r, LineKind k, string t) => Ui(() => { if (r == _room || (r is null && IsVisible)) LocalLine(k, t); });

    private void OnStage() => Ui(() =>
    {
        if (_session.Engine is not null) return;
        _input.IsEnabled = _send.IsEnabled = _attach.IsEnabled = _gifButton.IsEnabled = _emojiButton.IsEnabled = _burnButton.IsEnabled = false;
        _headButtons.IsEnabled = false;
        LocalLine(LineKind.Warn, "The chat is locked: its keys are wiped from memory. Unlock it in Chats to carry on.");
    });

    /// <summary>A line in this view only (notices, /help, command results), as CipherChat's local lines.</summary>
    private void LocalLine(LineKind k, string text)
    {
        var row = Build(new Entry { At = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Kind = k, Who = k == LineKind.Err ? "!!" : "--", Text = text });
        _log.Children.Add(row);
        ScrollEnd(true);
    }

    /// <summary>One /help row: the command in its own column and the description wrapping beside it, so a long
    /// description never runs back under the commands.</summary>
    private void HelpLine(string cmd, string text)
    {
        if (cmd.Length == 0) { LocalLine(LineKind.Sys, text); return; }
        var row = (Grid)Build(new Entry { At = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Kind = LineKind.Sys, Who = "--", Text = "" });
        foreach (var old in row.Children.OfType<UIElement>().Where(c => Grid.GetColumn(c) == 2).ToList()) row.Children.Remove(old);
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(_narrow || _compact ? 150 : 240) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var c = Small(cmd, "Accent", meta: true);
        c.TextWrapping = TextWrapping.Wrap;
        var d = Small(text, "Muted", meta: true);
        d.TextWrapping = TextWrapping.Wrap;
        d.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(d, 1);
        body.Children.Add(c);
        body.Children.Add(d);
        Grid.SetColumn(body, 2);
        row.Children.Add(body);
        _log.Children.Add(row);
        ScrollEnd(true);
    }

    private void OnAdded(Room r, Entry en) => Ui(() =>
    {
        if (r != _room) return;
        int index;
        lock (r.Timeline) index = r.Timeline.IndexOf(en);
        Insert(en, index);
        if (IsKeyboardFocusWithin || (IsVisible && Window.GetWindow(this)?.IsActive == true)) _e.MarkRead(_room);
        ScrollEnd(en.Message?.Mine == true || en.Outgoing is not null);
    });

    private void OnChanged(Room r, Entry en) => Ui(() => { if (r == _room) Rebuild(en); });

    private void Rebuild(Entry en)
    {
        if (!_rows.TryGetValue(en, out var old)) return;
        var i = _log.Children.IndexOf(old);
        var row = Build(en);
        _rows[en] = row;
        if (i < 0) { _log.Children.Add(row); return; }
        _log.Children.RemoveAt(i);
        _log.Children.Insert(i, row);
    }

    private void OnRemoved(Room r, Entry en) => Ui(() =>
    {
        if (r != _room || !_rows.Remove(en, out var row)) return;
        _log.Children.Remove(row);
    });

    /// <summary>Puts a timeline row at its place: before the row of the entry after it (older history, a late arrival),
    /// else at the end, so this view's own lines (notices, /help) stay where they happened.</summary>
    private void Insert(Entry en, int index)
    {
        var row = Build(en);
        _rows[en] = row;
        Entry? next = null;
        if (index >= 0) lock (_room.Timeline) next = index + 1 < _room.Timeline.Count ? _room.Timeline[index + 1] : null;
        if (next is not null && _rows.TryGetValue(next, out var nr) && _log.Children.IndexOf(nr) is var i and >= 0) _log.Children.Insert(i, row);
        else _log.Children.Add(row);
    }

    private void ScrollEnd(bool force)
    {
        if (force || _scroll.ScrollableHeight - _scroll.VerticalOffset < 120) Dispatcher.BeginInvoke(_scroll.ScrollToEnd, DispatcherPriority.Background);
    }

    // ------------------------------------------------------------------------------------------- one row

    private double NickWidth => _compact || _narrow ? 84 : 120;

    private FrameworkElement Build(Entry en)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(NickWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var time = Small(DateTimeOffset.FromUnixTimeSeconds(Math.Max(0, en.At)).ToLocalTime().ToString("HH:mm"), "Muted");
        time.TextWrapping = TextWrapping.NoWrap;
        grid.Children.Add(time);
        var m = en.Message;
        var nick = Small(m is null ? en.Who : m.Kind == "me" ? "*" : Clean(m.From), m is null && en.Outgoing is null ? "Muted" : m?.Mine != false ? "Accent" : "FgStrong", meta: true);
        nick.TextWrapping = TextWrapping.NoWrap;
        nick.TextTrimming = TextTrimming.CharacterEllipsis;
        if (m is not null) nick.ToolTip = Clean(m.From);
        Grid.SetColumn(nick, 1);
        grid.Children.Add(nick);
        FrameworkElement body = en.Outgoing is { } og ? PendingBody(og) : m is null ? LineBody(en) : m.Burn > 0 ? BurnBody(m) : MessageBody(m);
        Grid.SetColumn(body, 2);
        grid.Children.Add(body);
        if (m is not null && m.Burn == 0 && !m.Burned)
        {
            // built when it opens (a long room would otherwise hold a dozen menu items per message)
            var menu = new ContextMenu();
            menu.Items.Add(new MenuItem { Header = "React" });
            grid.ContextMenu = menu;
            grid.ContextMenuOpening += (_, _) => Fill(menu, m);
        }
        return grid;
    }

    /// <summary>A message of yours still on its way: waiting for the network, sending, or refused (retry / discard).</summary>
    private FrameworkElement PendingBody(Outgoing o)
    {
        var panel = new StackPanel { Opacity = o.State == Outgoing.States.Failed ? 1 : 0.7 };
        panel.Children.Add(TextView(Clean(o.Preview)));
        var status = o.State switch
        {
            Outgoing.States.Queued => "⏳ queued · sends when you're back online" + (o.Attempts > 0 && o.Note is { } n ? " (" + Clean(n) + ")" : ""),
            Outgoing.States.Sending => "↑ sending" + (o.Note is { } s ? " · " + Clean(s) : "") + "…",
            Outgoing.States.Sent => "✓ sent",
            _ => "✕ not sent: " + Clean(o.Note ?? "refused"),
        };
        var line = new WrapPanel();
        line.Children.Add(Small(status, o.State == Outgoing.States.Failed ? "Bad" : "Muted"));
        if (o.State is Outgoing.States.Failed or Outgoing.States.Queued)
        {
            if (o.State == Outgoing.States.Failed) line.Children.Add(LinkButton("retry", () => _e.Retry(o)));
            line.Children.Add(LinkButton("discard", () => _e.Discard(o)));
        }
        panel.Children.Add(line);
        return panel;
    }

    private static FrameworkElement LineBody(Entry en)
    {
        var t = (Control)TextView(Clean(en.Text));
        t.SetResourceReference(ForegroundProperty, en.Kind switch { LineKind.Err => "Bad", LineKind.Warn => "Warn", LineKind.Ok => "Good", _ => "Muted" });
        if (en.Kind == LineKind.Server) t.FontStyle = FontStyles.Italic;
        return t;
    }

    private FrameworkElement MessageBody(ChatMessage m)
    {
        var panel = new StackPanel();
        if (m.ReplyTo is not null)
        {
            var q = _e.MessageById(_room, m.ReplyTo);
            panel.Children.Add(Small("↳ " + (q is null ? "an earlier message" : Clean(q.From) + ": " + FirstLine(q.Text.Length > 0 ? q.Text : "[attachment]", 80)), "Muted"));
        }
        if (m.Text.Length > 0 || m.Kind == "me")
        {
            var text = (m.Kind == "me" ? Clean(m.From) + " " : "") + Clean(m.Text) + (m.EditedSeq > 0 ? "  (edited)" : "");
            var view = TextView(text, jumbo: m.Kind != "me" && m.EditedSeq == 0 && ColorEmoji.IsJumbo(m.Text));
            if (m.Kind == "me") ((Control)view).FontStyle = FontStyles.Italic;
            panel.Children.Add(view);
        }
        foreach (var f in m.Files) panel.Children.Add(FileView(f, keepGif: m.Burn == 0));
        if (m.Reactions.Count > 0)
        {
            var chips = new WrapPanel();
            foreach (var (emoji, users) in m.Reactions)
            {
                var mine = users.Contains(_e.MyUser);
                var c = new Button { Content = ColorEmoji.Content(emoji + " " + users.Count), ToolTip = string.Join(", ", users.Select(Clean)), Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 2, 6, 0), Tag = emoji };
                c.SetResourceReference(StyleProperty, mine ? "SmallPrimaryButton" : "SmallButton");
                System.Windows.Automation.AutomationProperties.SetName(c, emoji + " " + users.Count);
                var e = emoji;
                c.Click += async (_, _) => await Run(() => _e.ReactAsync(_room, m, e, !mine));
                chips.Children.Add(c);
            }
            panel.Children.Add(chips);
        }
        return panel;
    }

    /// <summary>Message text: selectable and read-only, never interpreted. With emoji in it a RichTextBox shows them in
    /// colour (ColorEmoji) and copying gives the emoji back as text. Emoji-only messages show larger.</summary>
    private static FrameworkElement TextView(string text, bool jumbo = false)
    {
        if (!ColorEmoji.HasEmoji(text))
        {
            var t = new TextBox { Text = text };
            t.SetResourceReference(StyleProperty, "ChatLogText");
            return t;
        }
        var p = new Paragraph { Margin = new Thickness(0) };
        p.Inlines.AddRange(ColorEmoji.Inlines(text, jumbo ? 2.8 : 1.3).ToList());
        var box = new RichTextBox { Document = new FlowDocument(p) { PagePadding = new Thickness(0) }, Tag = text };
        box.SetResourceReference(StyleProperty, "ChatLogRich");
        box.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, (_, e) => { SetClipboard(SelectedText(box)); e.Handled = true; }));
        return box;
    }

    private static string SelectedText(RichTextBox box)
    {
        var sb = new System.Text.StringBuilder();
        var end = box.Selection.End;
        for (var p = box.Selection.Start; p is not null && p.CompareTo(end) < 0; p = p.GetNextContextPosition(LogicalDirection.Forward))
        {
            var ctx = p.GetPointerContext(LogicalDirection.Forward);
            if (ctx == TextPointerContext.Text)
            {
                var run = p.GetTextInRun(LogicalDirection.Forward);
                var max = p.GetOffsetToPosition(end);
                sb.Append(run.Length > max ? run[..max] : run);
            }
            else if (ctx == TextPointerContext.EmbeddedElement && p.GetAdjacentElement(LogicalDirection.Forward) is FrameworkElement { Tag: string s }) sb.Append(s);
            else if (ctx == TextPointerContext.ElementStart && p.GetAdjacentElement(LogicalDirection.Forward) is LineBreak) sb.Append('\n');
        }
        return sb.ToString();
    }

    private static void SetClipboard(string text)
    {
        try { Clipboard.SetText(text); } catch (Exception ex) { Log.Write("Chat copy", ex); }
    }

    // Burn messages: hidden until opened; opening tells the server (it destroys the ciphertext once everyone has) and
    // starts the countdown; then the text and any decrypted pictures are dropped from this screen.
    private FrameworkElement BurnBody(ChatMessage m)
    {
        var panel = new StackPanel();
        if (m.Burned) { panel.Children.Add(Small("🔥 burned", "Muted")); return panel; }
        if (m.Mine || m.Revealed)
        {
            if (!m.Mine && !_left.ContainsKey(m)) StartCountdown(m);
            panel.Children.Add(Small(m.Mine ? "🔥 burns once read (" + m.Burn + "s)" : "🔥 " + _left.GetValueOrDefault(m, m.Burn) + "s", "Warn"));
            panel.Children.Add(MessageBody(m));
            return panel;
        }
        var reveal = new Button { Content = ColorEmoji.Content("🔥 burn message: click to reveal (visible for " + m.Burn + "s, then destroyed)"), HorizontalAlignment = HorizontalAlignment.Left };
        reveal.SetResourceReference(StyleProperty, "SmallButton");
        reveal.Click += async (_, _) => await Run(async () =>
        {
            await _e.RevealBurnAsync(_room, m);
            var en = _rows.Keys.FirstOrDefault(x => x.Message == m);
            if (en is not null) Rebuild(en);
        });
        panel.Children.Add(reveal);
        return panel;
    }

    private readonly Dictionary<ChatMessage, int> _left = new();

    /// <summary>A revealed burn message counts down once (a redrawn row shows the same count), then it burns.</summary>
    private void StartCountdown(ChatMessage m)
    {
        _left[m] = Math.Clamp(m.Burn, 1, 86_400);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            if (_disposed) { timer.Stop(); return; }
            var left = _left[m] = _left[m] - 1;
            if (left > 0) { if (_rows.Keys.FirstOrDefault(x => x.Message == m) is { } en) Rebuild(en); return; }
            timer.Stop();
            _e.BurnOut(_room, m);
        };
        timer.Start();
    }

    private void Fill(ContextMenu menu, ChatMessage m)
    {
        menu.Items.Clear();
        var react = new MenuItem { Header = "React" };
        foreach (var em in Emoji.Quick)
        {
            var mi = new MenuItem { Header = ColorEmoji.Content(em) };
            var e = em;
            mi.Click += async (_, _) => await Run(() => _e.ReactAsync(_room, m, e, !m.MyReaction(_e.MyUser, e)));
            react.Items.Add(mi);
        }
        var more = new MenuItem { Header = "More…" };
        more.Click += (_, _) => OpenEmoji(e => _ = Run(() => _e.ReactAsync(_room, m, e, !m.MyReaction(_e.MyUser, e))), this);
        react.Items.Add(more);
        menu.Items.Add(react);
        var reply = new MenuItem { Header = "Reply" };
        reply.Click += (_, _) => { _edit = null; _reply = m; RenderBar(); _input.Focus(); };
        menu.Items.Add(reply);
        if (m.Mine && m.Kind is "text" or "me")
        {
            var edit = new MenuItem { Header = "Edit" };
            edit.Click += (_, _) => { _reply = null; _edit = m; _input.Text = m.Text; RenderBar(); _input.Focus(); };
            menu.Items.Add(edit);
        }
        if (m.Text.Length > 0)
        {
            var copy = new MenuItem { Header = "Copy text" };
            copy.Click += (_, _) => SetClipboard(m.Text);
            menu.Items.Add(copy);
        }
        if (m.Mine || (_room.Kind == "room" && _room.MyRole == "admin"))
        {
            menu.Items.Add(new Separator());
            var del = new MenuItem { Header = "Delete for everyone" };
            del.Click += async (_, _) =>
            {
                if (Dialogs.Confirm("Delete message", "Delete this message for everyone? The server removes the ciphertext.", "Delete", danger: true))
                    await Run(() => _e.RedactAsync(_room, m.Seq));
            };
            menu.Items.Add(del);
        }
    }

    // ------------------------------------------------------------------------------------------- attachments (received)

    private FrameworkElement FileView(FileRef f, bool keepGif)
    {
        var label = f.SafeName + " · " + Size(f.Size);
        if (f.Kind is "image" or "video" && f.Thumb is not null)
        {
            var img = new Image { MaxWidth = 300, MaxHeight = 220, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left, Cursor = Cursors.Hand, Tag = f };
            System.Windows.Automation.AutomationProperties.SetName(img, (f.Kind == "video" ? "Video: " : "Picture: ") + f.SafeName);
            var box = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
            box.Children.Add(img);
            var gif = f.Kind == "image" && f.Mime == "image/gif" && f.Size <= GifInlineMax;
            box.Children.Add(Small((f.Kind == "video" ? "▶ " : gif ? "GIF · " : "") + label, "Muted"));
            _ = LoadThumbAsync(f.Thumb, img);
            // GIFs play inline (unless Windows' "show animations" is off: then the still preview stays)
            if (gif && SystemParameters.ClientAreaAnimation) _ = PlayGifAsync(f, img, keepGif);
            img.MouseLeftButtonUp += async (_, _) => await Run(() => f.Kind == "image" ? ViewImageAsync(f) : SaveAsync(f));
            img.ToolTip = f.Kind == "image" ? "Open (decrypted in memory on this PC)" : "Save the video (decrypted on this PC)";
            return box;
        }
        var what = f.Kind switch { "image" => "picture", "video" => "video", "audio" => "audio", _ => "file" };
        var b = new Button { Content = "⬇ " + what + ": " + label, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 4), ToolTip = "Save (decrypted on this PC; never opened or run by the app)" };
        b.SetResourceReference(StyleProperty, "SmallButton");
        b.Click += async (_, _) => await Run(() => f.Kind == "image" ? ViewImageAsync(f) : SaveAsync(f));
        return b;
    }

    private async Task LoadThumbAsync(FileRef thumb, Image img)
    {
        try
        {
            var bytes = await _e.FetchFileAsync(_room, thumb);
            if (!_disposed && img.HasAnimatedProperties == false && ChatPictures.TryDecode(bytes, 300) is { } pic) img.Source = pic;
        }
        catch (Exception ex) when (ex is CipherChat.Api.ApiException or CryptoFail or IOException or InvalidOperationException or NotSupportedException) { img.ToolTip = "Preview unavailable: " + ex.Message; }
    }

    private async Task PlayGifAsync(FileRef f, Image img, bool keep)
    {
        try
        {
            var bytes = await _e.FetchFileAsync(_room, f);
            if (_disposed || Files.Sniff(bytes) != "image/gif" || !GifPlayer.Play(img, bytes)) return;
            if (keep) RememberGif(f.SafeName, bytes);
        }
        catch (Exception ex) when (ex is CipherChat.Api.ApiException or CryptoFail or IOException or InvalidOperationException or NotSupportedException or FileFormatException) { /* the still preview stays */ }
    }

    private async Task ViewImageAsync(FileRef f)
    {
        if (TestMode.Running) { Dialogs.RefusedInSelfTest("chat picture viewer"); return; }
        var bytes = await _e.FetchFileAsync(_room, f);
        if (!ChatPictures.IsPicture(bytes)) { await SaveBytesAsync(f, bytes); return; }   // not something shown safely
        if (ChatForm.New(f.SafeName, 900) is not { } d) return;
        var img = d.Add(new Image { Source = ChatPictures.Decode(bytes, 1600), MaxHeight = 640, Stretch = Stretch.Uniform });
        if (Files.Sniff(bytes) == "image/gif" && SystemParameters.ClientAreaAnimation) GifPlayer.Play(img, bytes);
        d.Text(Size(f.Size) + " · end-to-end encrypted", "Caption");
        d.Button("Save…", () => { _ = Run(() => SaveBytesAsync(f, bytes)); return true; });
        d.Button("Close", () => true, "PrimaryButton", isDefault: true);
        d.Show();
    }

    private async Task SaveAsync(FileRef f)
    {
        if (TestMode.Running) { Dialogs.RefusedInSelfTest("save chat file"); return; }
        await SaveBytesAsync(f, await _e.FetchFileAsync(_room, f));
    }

    private async Task SaveBytesAsync(FileRef f, byte[] bytes)
    {
        if (TestMode.Running) { Dialogs.RefusedInSelfTest("save chat file"); return; }
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = f.SafeName, Title = "Save the decrypted attachment", AddExtension = false };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        await File.WriteAllBytesAsync(dlg.FileName, bytes);
        AuditLog.Write("you", "chat", "file saved from a chat", f.SafeName);
    }

    // GIFs of this session (sent, or played in a room), newest first, so sending one again is one click. In memory only,
    // per account: gone when the chat locks (the engine goes) or the app closes. A burn message's GIF is never kept.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ChatEngine, List<(string Name, byte[] Bytes)>> Gifs = new();
    private List<(string Name, byte[] Bytes)> SessionGifs => Gifs.GetOrCreateValue(_e);

    private void RememberGif(string name, byte[] bytes)
    {
        var g = SessionGifs;
        g.RemoveAll(x => x.Bytes.Length == bytes.Length && x.Name == name);
        g.Insert(0, (name, bytes));
        while (g.Count > 30 || (g.Count > 1 && g.Sum(x => (long)x.Bytes.Length) > 96L * 1048576)) g.RemoveAt(g.Count - 1);
    }

    private Popup BuildGifPopup()
    {
        var title = new TextBlock { Text = "GIFS FROM THIS SESSION", Margin = new Thickness(0, 0, 0, 6) };
        title.SetResourceReference(StyleProperty, "Label");
        _gifEmpty.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _gifEmpty.SetResourceReference(TextBlock.FontSizeProperty, "FsSmall");
        var choose = new Button { Content = "Choose a GIF…", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 4) };
        choose.SetResourceReference(StyleProperty, "SmallButton");
        choose.Click += (_, _) => { _gifPopup.IsOpen = false; PickFiles("Choose a GIF (encrypted on this PC before upload)", "GIF animations (*.gif)|*.gif"); };
        var note = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "End-to-end encrypted like any attachment. No GIF service sees what you search or send." };
        note.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        note.SetResourceReference(TextBlock.FontSizeProperty, "FsMicro");
        var stack = new StackPanel { Children = { title, _gifEmpty, new ScrollViewer { Content = _gifGrid, MaxHeight = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, choose, note } };
        var border = new Border { BorderThickness = new Thickness(1), Padding = new Thickness(8), Width = 350, Child = stack };
        border.SetResourceReference(Border.BackgroundProperty, "Surface");
        border.SetResourceReference(Border.BorderBrushProperty, "LineStrong");
        _gifPopup.Child = border;
        return _gifPopup;
    }

    private void OpenGifs()
    {
        if (ChatPolicy.FilesBlocked() is { } why) { Dialogs.Info("CipherChat", why); return; }
        _gifGrid.Children.Clear();
        var gifs = SessionGifs;
        _gifEmpty.Visibility = gifs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (name, bytes) in gifs)
        {
            var cell = new Button { Width = 104, Height = 104, Padding = new Thickness(2), Margin = new Thickness(0, 0, 4, 4), ToolTip = "Send " + Clean(name),
                                    Content = new Image { Source = GifPlayer.Still(bytes, 200), Stretch = Stretch.UniformToFill } };
            cell.SetResourceReference(StyleProperty, "SmallButton");
            var (n, b) = (name, bytes);
            cell.Click += (_, _) => { _gifPopup.IsOpen = false; AddBytes(n, b); _input.Focus(); };
            _gifGrid.Children.Add(cell);
        }
        _gifPopup.PlacementTarget = _gifButton;
        _gifPopup.IsOpen = true;
    }

    // ------------------------------------------------------------------------------------------- attachments (sending)

    private void AddFiles() => PickFiles("Attach (encrypted on this PC before upload)", null);

    private void PickFiles(string title, string? filter)
    {
        if (ChatPolicy.FilesBlocked() is { } why) { Dialogs.Info("CipherChat", why); return; }
        if (TestMode.Running) { Dialogs.RefusedInSelfTest("pick chat files"); return; }
        var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = title };
        if (filter is not null) dlg.Filter = filter;
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        foreach (var path in dlg.FileNames) AddFile(path);
    }

    /// <summary>Adds a file to the next message (also "Send to a chat" from the file commander, and dropped files).</summary>
    public void AddFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return;
        if (info.Length > ChatPolicy.MaxFileBytes) { Dialogs.Info("CipherChat", $"{info.Name} is {Size(info.Length)}; the most you can send is {Size(ChatPolicy.MaxFileBytes)}."); return; }
        AddBytes(info.Name, File.ReadAllBytes(path));
    }

    /// <summary>Adds a file a screen handed over (Send to a chat) to the next message.</summary>
    public void AddFile(string name, byte[] bytes, string? mime)
    {
        if (bytes.LongLength > ChatPolicy.MaxFileBytes) { Dialogs.Info("CipherChat", $"{name} is {Size(bytes.LongLength)}; the most you can send is {Size(ChatPolicy.MaxFileBytes)}."); return; }
        AddBytes(name, bytes, mime);
    }

    private void AddBytes(string name, byte[] bytes, string? mime = null)
    {
        if (ChatPolicy.FilesBlocked() is { } why) { Dialogs.Info("CipherChat", why); return; }
        if (_files.Count >= 10) { Dialogs.Info("CipherChat", "Up to 10 attachments per message."); return; }
        _files.Add(ChatPictures.Outgoing(name, bytes, mime));
        if (_burn == 0 && Files.Sniff(bytes) == "image/gif") RememberGif(name, bytes);
        RenderBar();
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) { e.Handled = true; foreach (var f in files.Take(10)) AddFile(f); }
    }

    // Pasted files (copied in Explorer) and pictures (screenshots, copied images) become attachments
    private static bool PastesAttachment
    {
        get { try { return Clipboard.ContainsFileDropList() || (Clipboard.ContainsImage() && !Clipboard.ContainsText()); } catch { return false; } }
    }

    private void OnCanPaste(object sender, CanExecuteRoutedEventArgs e)
    {
        if (e.Command != ApplicationCommands.Paste || !PastesAttachment) return;
        e.CanExecute = true;
        e.Handled = true;
    }

    private void OnPaste(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Command != ApplicationCommands.Paste || !PastesAttachment) return;
        e.Handled = true;
        try
        {
            if (Clipboard.ContainsFileDropList()) { foreach (var f in Clipboard.GetFileDropList().Cast<string>().Take(10)) AddFile(f); return; }
            if (Clipboard.GetImage() is not { } img) return;
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(img));
            using var ms = new MemoryStream();
            enc.Save(ms);
            AddBytes("pasted-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png", ms.ToArray(), "image/png");
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException or NotSupportedException) { Log.Write("Chat paste", ex); }
    }

    // ------------------------------------------------------------------------------------------- composing

    /// <summary>The compose line: replying, editing, burn on, and each file of the next message (a picture with its
    /// preview), each with cancel.</summary>
    private void RenderBar()
    {
        _composeBar.Children.Clear();
        void Line(string text, Action cancel, string brush = "Muted", byte[]? thumb = null)
        {
            var dock = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            var b = new Button { Content = "cancel", MinWidth = 0 };
            b.SetResourceReference(StyleProperty, "SmallButton");
            System.Windows.Automation.AutomationProperties.SetName(b, "Cancel: " + text);
            b.Click += (_, _) => { cancel(); RenderBar(); };
            DockPanel.SetDock(b, Dock.Right);
            dock.Children.Add(b);
            if (thumb is not null && ChatPictures.TryDecode(thumb, 120) is { } pic)
            {
                var img = new Image { Source = pic, MaxHeight = 48, MaxWidth = 96, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 8, 0) };
                DockPanel.SetDock(img, Dock.Left);
                dock.Children.Add(img);
            }
            var t = Small(text, brush, meta: true);
            t.VerticalAlignment = VerticalAlignment.Center;
            dock.Children.Add(t);
            _composeBar.Children.Add(dock);
        }
        if (_reply is { } r) Line("↳ replying to " + Clean(r.From) + ": " + FirstLine(r.Text.Length > 0 ? r.Text : "attachment", 60) + "   (Esc cancels)", () => _reply = null);
        if (_edit is not null) Line("✎ editing your message (Enter saves, Esc cancels)", () => { _edit = null; _input.Clear(); });
        if (_burn > 0) Line("🔥 burn after reading is on: your next messages are destroyed " + _burn + "s after each recipient opens them", () => { _burn = 0; RenderBurn(); }, "Warn");
        foreach (var f in _files.ToList())
            Line((f.Thumb is not null ? "🖼 " : "📎 ") + Clean(f.Name) + " (" + Size(f.Bytes.LongLength) + (f.W is int w && f.H is int h ? " · " + w + "×" + h : "") + ") · encrypted before upload",
                 () => _files.Remove(f), thumb: f.Thumb);
        _composeBar.Visibility = _composeBar.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RenderBurn()
    {
        _burnButton.Content = ColorEmoji.Content(_burn == 0 ? "🔥" : "🔥" + (_burn < 60 ? _burn + "s" : _burn / 60 + "m"));
        _burnButton.ToolTip = _burn == 0 ? "Burn after reading (off)" : "Burn after reading: " + _burn + "s once opened (click to change)";
        RenderBar();
    }

    private void NextBurn()
    {
        _burn = BurnSteps[(Array.IndexOf(BurnSteps, _burn) + 1) % BurnSteps.Length];
        RenderBurn();
    }

    private async void OnInputKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0) { e.Handled = true; await SendAsync(); }
        else if (e.Key == Key.Escape && (_edit is not null || _reply is not null || _files.Count > 0))
        {
            e.Handled = true;
            if (_edit is not null) _input.Clear();
            _edit = null; _reply = null; _files.Clear(); RenderBar();
        }
    }

    private async Task SendAsync()
    {
        if (_session.Engine is null) return;
        var text = _input.Text.TrimEnd();
        if (text.Trim().Length == 0 && _files.Count == 0) return;
        if (_edit is null && _files.Count == 0 && text.StartsWith('/') && !text.StartsWith("//", StringComparison.Ordinal))
        {
            _input.Clear();
            await Run(() => CommandAsync(text));
            return;
        }
        var reply = _reply?.Id; var edit = _edit; var files = _files.ToList(); var burn = _burn;
        _input.Clear(); _reply = null; _edit = null; _files.Clear(); RenderBar();
        try
        {
            if (edit is not null) { if (text.Trim().Length > 0 && text != edit.Text) await _e.EditAsync(_room, edit, text); }
            else if (files.Count > 0)
            {
                if (ChatPolicy.FilesBlocked() is { } why) { Dialogs.Info("CipherChat", why); return; }
                await _e.SendFilesAsync(_room, text, files, reply, burn, new Progress<string>(s => _status.Text = Clean(s)));
                AuditLog.Write("you", "chat", "files sent", files.Count + " file(s) to " + _room.Label(_e.MyUser));
            }
            else await _e.SendTextAsync(_room, text.StartsWith("//", StringComparison.Ordinal) ? text[1..] : text, reply, burn);
        }
        catch (Exception ex)
        {
            if (_input.Text.Length == 0) _input.Text = text;     // nothing typed is lost on a refusal
            Dialogs.Error("CipherChat", ex.Message, ex: ex);
        }
        finally { RenderStatus(); }
    }

    // ------------------------------------------------------------------------------------------- commands

    /// <summary>The /help list: a command and what it does (an empty command is a note across the row).</summary>
    private static readonly (string Cmd, string Text)[] Help =
    [
        ("/help", "this list"),
        ("/join #room | @user", "switch room or open a direct message"),
        ("/new name [user ...]", "create a room"),
        ("/me does something", "action line"),
        ("/topic text", "set the topic (admins)"),
        ("/invite user ...", "add members (admins)"),
        ("/kick user ...", "remove members (admins)"),
        ("/members", "who is here and who holds the key"),
        ("/call  /video", "a voice or video call with this room (end-to-end encrypted, post-quantum)"),
        ("/hangup", "leave the call"),
        ("/rotate", "make a new room key now"),
        ("/verify user", "safety number; the owner can certify from there"),
        ("/fp", "your identity fingerprint"),
        ("/leave", "leave this room"),
        ("/clear", "clear the screen (messages stay on the server)"),
        ("/lock", "wipe this chat's keys from memory and lock it"),
        ("", "Enter sends, Shift+Enter is a new line. // at the start sends a line that begins with /."),
        ("", "📎 attaches files, pictures, videos and GIFs (or paste / drop them); ☺ inserts emoji; GIF sends a GIF again or picks one."),
        ("", "Right-click a message to react, reply, edit (your own), copy or delete. Esc cancels a reply, an edit or the files."),
        ("", "Attachments are encrypted on this PC before upload; the server only stores ciphertext."),
    ];

    private async Task CommandAsync(string raw)
    {
        var parts = raw.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var cmd = parts[0].ToLowerInvariant();
        var args = parts.Skip(1).ToList();
        var rest = raw.Trim()[parts[0].Length..].Trim();
        switch (cmd)
        {
            case "/help": foreach (var (c, t) in Help) HelpLine(c, t); return;
            case "/me": if (rest.Length > 0) await _e.SendTextAsync(_room, rest, _reply?.Id, _burn, action: true); _reply = null; RenderBar(); return;
            case "/join":
            {
                var t = args.FirstOrDefault() ?? "";
                if (t.StartsWith('@')) { Open(await _e.OpenDmAsync(t[1..])); return; }
                var f = _e.Rooms.FirstOrDefault(x => x.Kind == "room" && x.Name == t.TrimStart('#'));
                if (f is null) LocalLine(LineKind.Err, "You're not in a room called " + Clean(t) + "."); else Open(f);
                return;
            }
            case "/new":
                if (args.Count == 0) { LocalLine(LineKind.Err, "Usage: /new name [user ...]"); return; }
                var created = await _e.CreateRoomAsync(args[0].TrimStart('#').ToLowerInvariant(), "", _e.RetentionChoices.FirstOrDefault(), args.Skip(1).Where(u => u != _e.MyUser).Distinct().ToList());
                AuditLog.Write("you", "chat", "room created", "#" + created.Name);
                Open(created);
                return;
            case "/topic": await _e.UpdateRoomAsync(_room, _room.Name, rest, _room.RetentionDays); return;
            case "/invite": await _e.ChangeMembersAsync(_room, args, []); await _e.MaybeAutoRotateAsync(_room); return;
            case "/kick": await _e.ChangeMembersAsync(_room, [], args); await _e.MaybeAutoRotateAsync(_room); return;
            case "/members": MembersDialog(); return;
            case "/call": StartCall("audio"); return;
            case "/video": StartCall("video"); return;
            case "/hangup": Calls.CallWindow.EndFor(_session); return;
            case "/rotate": await _e.RotateNowAsync(_room); return;
            case "/verify":
                if (args.FirstOrDefault() is { } u && _e.People.GetValueOrDefault(u) is { } p) PersonDialog(_session, p, Open);
                else LocalLine(LineKind.Err, "No chat user called " + Clean(args.FirstOrDefault() ?? "") + ".");
                return;
            case "/fp": LocalLine(LineKind.Sys, "Your fingerprint: " + Hybrid.FpGroups(_e.Me.Fp)); return;
            case "/leave":
                if (Dialogs.Confirm("Leave " + _room.Label(_e.MyUser), "Leave this room? You'll need to be added again to come back.", "Leave", danger: true))
                {
                    await _e.LeaveAsync(_room);
                    AuditLog.Write("you", "chat", "left a room", _room.Label(_e.MyUser));
                }
                return;
            case "/clear":
                foreach (var row in _log.Children.OfType<FrameworkElement>().ToList()) row.Visibility = Visibility.Collapsed;
                return;
            case "/lock": _session.Lock(); AuditLog.Write("you", "chat", "chat locked", _session.Display); return;
            default: LocalLine(LineKind.Err, "Unknown command " + Clean(parts[0]) + ". Type /help."); return;
        }
    }

    private void Open(Room r)
    {
        if (OpenElsewhere is { } open) open(r);
        else ChatHomeView.OpenRoom(_session, r);
    }

    private void StartCall(string media)
    {
        if (ChatPolicy.CallsBlocked() is { } why) { Dialogs.Info("CipherChat call", why); return; }
        Calls.CallWindow.Start(_session, _room, media);
    }

    // ------------------------------------------------------------------------------------------- emoji

    private Popup BuildEmojiPopup()
    {
        System.Windows.Automation.AutomationProperties.SetName(_emojiSearch, "Search emoji");
        _emojiSearch.TextChanged += (_, _) => FillEmoji(_emojiSearch.Text.Trim().ToLowerInvariant());
        var border = new Border { BorderThickness = new Thickness(1), Padding = new Thickness(8), Width = 360,
                                  Child = new StackPanel { Children = { _emojiSearch, new ScrollViewer { Content = _emojiGrid, Height = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } } } };
        border.SetResourceReference(Border.BackgroundProperty, "Surface");
        border.SetResourceReference(Border.BorderBrushProperty, "LineStrong");
        _emojiPopup.Child = border;
        return _emojiPopup;
    }

    private void OpenEmoji(Action<string> pick, UIElement? target)
    {
        _emojiPick = pick;
        _emojiPopup.PlacementTarget = target ?? _emojiButton;
        _emojiPopup.Placement = target is null ? PlacementMode.Top : PlacementMode.MousePoint;
        _emojiSearch.Text = "";
        FillEmoji("");
        _emojiPopup.IsOpen = true;
        _emojiSearch.Focus();
    }

    private void FillEmoji(string q)
    {
        _emojiGrid.Children.Clear();
        foreach (var (emoji, name, _) in Emoji.All.Where(x => q.Length == 0 || x.Name.Contains(q, StringComparison.Ordinal)))
        {
            var b = new Button { Content = ColorEmoji.Element(emoji), ToolTip = name, FontSize = 18, Width = 38, Height = 34, Padding = new Thickness(0), MinWidth = 0, Margin = new Thickness(0, 0, 2, 2) };
            b.SetResourceReference(StyleProperty, "SmallButton");
            b.Click += (_, _) => { _emojiPopup.IsOpen = false; _emojiPick?.Invoke(emoji); };
            _emojiGrid.Children.Add(b);
        }
    }

    // ------------------------------------------------------------------------------------------- dialogs

    /// <summary>A person: trust, fingerprint, the safety number to compare out of band; the owner certifies or revokes.</summary>
    internal static void PersonDialog(ChatSession s, Person p, Action<Room> open)
    {
        var e = s.Engine;
        if (e is null) return;
        var mine = p.User == e.MyUser;
        if (ChatForm.New(Clean(p.User) + (p.DisplayName.Length > 0 ? " (" + Clean(p.DisplayName) + ")" : ""), 520) is not { } d) return;
        d.Text("Trust: " + p.Trust.ToString().ToLowerInvariant() + " — " + Clean(p.Why));
        if (p.Fp is not null) d.Text("Fingerprint " + Hybrid.FpGroups(p.Fp), "Caption");
        if (p.Fp is not null && !mine)
        {
            d.Text("Safety number (compare out of band, never in this chat):", "Label");
            var sn = d.Add(new TextBlock { Text = string.Join("   ", e.SafetyNumber(p).Split(' ').Chunk(4).Select(g => string.Join(" ", g))), TextWrapping = TextWrapping.Wrap, FontSize = 18, Margin = new Thickness(0, 0, 0, 8) });
            sn.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
            if (e.IAmOwner && p.Trust == TrustLevel.Pending) d.Text("Certify only after " + Clean(p.User) + " has read you the same 60 digits on a call, in person or over a channel you already trust.", "Caption");
        }
        d.Cancel("Close");
        if (e.IAmOwner && !mine && p.Fp is not null && p.Trust != TrustLevel.Ok) d.Button("Certify", () => "certify", "PrimaryButton");
        if (e.IAmOwner && !mine && p.Trust == TrustLevel.Ok) d.Button("Revoke", () => "revoke", "DangerButton");
        if (!mine && p.Fp is not null) d.Button("Message", () => "dm");
        switch (d.Show())
        {
            case "certify": _ = Run(async () => { await e.CertifyAsync(p); AuditLog.Write("you", "chat", "person certified", p.User); }); break;
            case "revoke": _ = Run(async () => { await e.RevokeAsync(p); AuditLog.Write("you", "chat", "person's certification revoked", p.User); }); break;
            case "dm": _ = Run(async () => open(await e.OpenDmAsync(p.User))); break;
        }
    }

    private static List<CheckBox> PickList(ChatForm d, ChatEngine e, IEnumerable<string> names, IReadOnlyCollection<string>? ticked = null)
    {
        var boxes = new List<CheckBox>();
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        foreach (var n in names)
        {
            var p = e.People.GetValueOrDefault(n);
            var cb = new CheckBox { Content = Clean(n) + "  (" + (p?.Trust.ToString().ToLowerInvariant() ?? "?") + ")", Tag = n, IsChecked = ticked?.Contains(n) == true, Margin = new Thickness(0, 2, 0, 2) };
            panel.Children.Add(cb);
            boxes.Add(cb);
        }
        if (boxes.Count == 0) panel.Children.Add(new TextBlock { Text = "nobody", Opacity = 0.6 });
        d.Add(new ScrollViewer { Content = panel, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        return boxes;
    }

    /// <summary>A new private room: name, topic, history, members (only they get its key).</summary>
    internal static void NewRoomDialog(ChatSession s, Action<Room> open)
    {
        var e = s.Engine;
        if (e is null || ChatForm.New("New private room", 480) is not { } d) return;
        d.Text("Only the people you add can see this room or read it: its key is made for them alone.", "Caption");
        var name = d.Field("NAME", "", 32);
        var topic = d.Field("TOPIC (optional)", "", 200);
        d.Text("HISTORY", "Label");
        var keep = d.Add(new ComboBox { Margin = new Thickness(0, 0, 0, 10), ItemsSource = e.RetentionChoices.Select(x => x == 0 ? "keep messages until deleted" : "delete messages after " + x + " day" + (x > 1 ? "s" : "")).ToList(), SelectedIndex = 0 });
        d.Text("MEMBERS", "Label");
        var boxes = PickList(d, e, e.People.Keys.Where(u => u != e.MyUser).OrderBy(u => u, StringComparer.Ordinal));
        d.Text("Room names, topics and member lists are visible to the server. Messages aren't.", "Caption");
        d.Cancel();
        d.Button("Create", () => name.Text.Trim().Length > 0 ? true : false, "PrimaryButton", isDefault: true);
        if (d.Show(name) is not true) return;
        var retention = e.RetentionChoices[Math.Max(0, keep.SelectedIndex)];
        var members = boxes.Where(b => b.IsChecked == true).Select(b => (string)b.Tag).ToList();
        _ = Run(async () =>
        {
            var room = await e.CreateRoomAsync(name.Text.Trim().ToLowerInvariant(), topic.Text, retention, members);
            AuditLog.Write("you", "chat", "room created", "#" + room.Name + " (" + members.Count + " people)");
            open(room);
        });
    }

    /// <summary>A direct message with someone on the server.</summary>
    internal static void NewDmDialog(ChatSession s, Action<Room> open)
    {
        var e = s.Engine;
        if (e is null || ChatForm.New("Direct message", 420) is not { } d) return;
        var people = e.People.Keys.Where(u => u != e.MyUser).OrderBy(u => u, StringComparer.Ordinal).ToList();
        var sel = d.Add(new ComboBox { ItemsSource = people.Select(Clean).ToList(), SelectedIndex = people.Count > 0 ? 0 : -1, Margin = new Thickness(0, 0, 0, 8) });
        d.Cancel();
        d.Button("Open", () => sel.SelectedIndex >= 0 ? true : false, "PrimaryButton", isDefault: true);
        if (d.Show(sel) is true) _ = Run(async () => open(await e.OpenDmAsync(people[sel.SelectedIndex])));
    }

    private void MembersDialog()
    {
        var r = _room;
        var admin = r.Kind == "room" && (r.MyRole == "admin" || _e.IAmOwner);
        if (ChatForm.New("Members of " + Clean(r.Label(_e.MyUser)), 480) is not { } d) return;
        foreach (var (user, role) in r.Members)
            d.Text(Clean(user) + (role == "admin" ? " (admin)" : "") + "  ·  " + (r.EpochRecipients.Contains(user) ? "holds the key" : "no key") + "  ·  " +
                   (_e.People.GetValueOrDefault(user)?.Trust.ToString().ToLowerInvariant() ?? "?"));
        // any member may renew the key (as /rotate)
        d.Text(r.Epoch > 0 ? "Room key #" + r.Epoch + ", held by " + string.Join(", ", r.EpochRecipients.Select(Clean)) + "." : "No room key yet.", "Caption");
        List<CheckBox>? add = null, remove = null;
        if (admin)
        {
            d.Text("ADD", "Label");
            add = PickList(d, _e, _e.People.Keys.Where(u => r.Members.All(m => m.User != u)).OrderBy(u => u, StringComparer.Ordinal));
            d.Text("REMOVE", "Label");
            remove = PickList(d, _e, r.Members.Select(m => m.User).Where(u => u != _e.MyUser));
            d.Text("Any change gives the room a new key: removed members can't read anything sent afterwards.", "Caption");
            d.Button("New key now", () => "rotate");
            d.Cancel("Close");
            d.Button("Apply", () => true, "PrimaryButton");
        }
        else
        {
            d.Button("New key now", () => "rotate");
            d.Button("Close", () => null, "PrimaryButton", isDefault: true);
        }
        var res = d.Show();
        if (res is "rotate") { _ = Run(() => _e.RotateNowAsync(r)); return; }
        if (res is not true || add is null || remove is null) return;
        var a = add.Where(b => b.IsChecked == true).Select(b => (string)b.Tag).ToList();
        var rm = remove.Where(b => b.IsChecked == true).Select(b => (string)b.Tag).ToList();
        if (a.Count + rm.Count == 0) return;
        _ = Run(async () =>
        {
            await _e.ChangeMembersAsync(r, a, rm);
            AuditLog.Write("you", "chat", "room members changed", r.Label(_e.MyUser) + $" (+{a.Count} / -{rm.Count})");
        });
    }

    private void RoomSettingsDialog()
    {
        var r = _room;
        if (ChatForm.New("Settings for " + Clean(r.Label(_e.MyUser)), 460) is not { } d) return;
        var name = d.Field("NAME", r.Name, 32);
        var topic = d.Field("TOPIC", r.Topic, 200);
        d.Text("KEEP MESSAGES", "Label");
        var choices = _e.RetentionChoices.ToList();
        var keep = d.Add(new ComboBox { ItemsSource = choices.Select(x => x == 0 ? "until deleted" : x + " days").ToList(), SelectedIndex = Math.Max(0, choices.IndexOf(r.RetentionDays)), Margin = new Thickness(0, 0, 0, 10) });
        d.Text("DEVICES KEEP ROOM KEYS", "Label");
        var hist = _e.HistoryChoices.ToList();
        var window = d.Add(new ComboBox { ItemsSource = hist.Select(x => x == 0 ? "as long as messages are kept" : "for " + x + " days").ToList(), SelectedIndex = Math.Max(0, hist.IndexOf(r.HistoryDays)), Margin = new Thickness(0, 0, 0, 4) });
        d.Text("Older keys are erased from members' devices, so a device taken later can't read further back than this.", "Caption");
        var sync = d.Add(new CheckBox { Content = "Members' new devices can get older keys from their other devices", IsChecked = r.HistorySync, Margin = new Thickness(0, 6, 0, 10) });
        sync.SetResourceReference(StyleProperty, "Switch");
        d.Button("Delete room", () => "delete", "DangerButton");
        d.Button("New key now", () => "rotate");
        d.Cancel();
        d.Button("Save", () => "save", "PrimaryButton", isDefault: true);
        switch (d.Show(name))
        {
            case "save":
                _ = Run(async () =>
                {
                    await _e.UpdateRoomAsync(r, name.Text.Trim(), topic.Text, choices[Math.Max(0, keep.SelectedIndex)], hist[Math.Max(0, window.SelectedIndex)], sync.IsChecked == true);
                    AuditLog.Write("you", "chat", "room settings changed", r.Label(_e.MyUser));
                });
                break;
            case "rotate": _ = Run(() => _e.RotateNowAsync(r)); break;
            case "delete":
                if (Dialogs.Confirm("Delete " + Clean(r.Label(_e.MyUser)), "Delete the room and every message in it for everyone? This can't be undone.", "Delete", danger: true))
                    _ = Run(async () => { await _e.DeleteRoomAsync(r); AuditLog.Write("you", "chat", "room deleted", r.Label(_e.MyUser)); });
                break;
        }
    }

    // ------------------------------------------------------------------------------------------- outside callers

    /// <summary>Puts text in the composer (a share from a terminal, Toolbox or a report) without sending it.</summary>
    public void Prefill(string text)
    {
        _input.Text = _input.Text.Length == 0 ? text : _input.Text + "\n" + text;
        _input.Focus();
    }

    /// <summary>The files waiting for Send (self-test).</summary>
    internal IReadOnlyList<ChatEngine.OutgoingFile> PendingForTest => _files;

    /// <summary>Self-test (the live run): types a message in the composer and sends it as the Send button does.</summary>
    internal Task SendForTest(string text) { _input.Text = text; return SendAsync(); }

    /// <summary>Self-test: every text the room's rows show (emoji pictures as their emoji), and how many pictures they
    /// show (a decrypted thumbnail loaded).</summary>
    internal IReadOnlyList<string> RowTextsForTest => _rows.Values.SelectMany(TextsIn).ToList();
    internal int PicturesShownForTest => _rows.Values.Sum(r => ElementsIn<Image>(r).Count(i => i.Tag is FileRef && i.Source is not null));

    private static IEnumerable<string> TextsIn(DependencyObject d) =>
        ElementsIn<TextBlock>(d).Select(t => t.Text)
            .Concat(ElementsIn<TextBox>(d).Select(t => t.Text))
            .Concat(ElementsIn<RichTextBox>(d).Select(t => t.Tag as string ?? ""))
            .Concat(ElementsIn<Button>(d).Select(b => b.Tag as string ?? ""))
            .Concat(ElementsIn<ContentControl>(d).Select(c => c.Content as string ?? "")).Where(t => t.Length > 0);

    /// <summary>Self-test: every text in the log, this view's own lines (notices, /help) too.</summary>
    internal IReadOnlyList<string> LogTextsForTest => _log.Children.OfType<DependencyObject>().SelectMany(TextsIn).ToList();

    /// <summary>Self-test: the header, the prompt and the status line.</summary>
    internal IReadOnlyList<string> ChromeTextsForTest => [_title.Text, _topic.Text, _keyInfo.Text, _prompt.Text, _status.Text];

    internal static IEnumerable<T> ElementsIn<T>(DependencyObject d) where T : DependencyObject
    {
        if (d is T t) yield return t;
        foreach (var c in LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>())
            foreach (var x in ElementsIn<T>(c)) yield return x;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _e.EntryAdded -= OnAdded;
        _e.EntryChanged -= OnChanged;
        _e.EntryRemoved -= OnRemoved;
        _e.RoomsChanged -= OnRoomsChanged;
        _e.OnlineChanged -= OnOnline;
        _e.Notice -= OnNotice;
        _e.RoomReset -= OnReset;
        _session.StageChanged -= OnStage;
        _emojiPopup.IsOpen = false; _gifPopup.IsOpen = false;
        if (_e.Viewing == _room) _e.Viewing = null;
    }
}
