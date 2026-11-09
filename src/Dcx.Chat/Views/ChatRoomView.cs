using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CipherChat.Engine;
using DcxManager.Services;
using DcxManager.Views;

namespace DcxManager.Chat.Views;

/// <summary>
/// One CipherChat room or DM as a document tab (ADR-117): the timeline and the composer, beside terminals and every other
/// tab (pin, split, pop out). Everything is the engine's: this view only draws what it holds and calls it. Engine events
/// arrive off the UI thread and are posted here; the timeline is read under its own lock (as CipherChat's app does).
/// Message text is shown as plain text only (no markup is interpreted); control characters are dropped before display.
/// </summary>
public sealed class ChatRoomView : UserControl, IDisposable
{
    private readonly ChatSession _session;
    private readonly ChatEngine _e;
    private readonly Room _room;
    private readonly StackPanel _log = new();
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly TextBox _input = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 44, MaxHeight = 200, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(8, 6, 8, 6) };
    private readonly TextBlock _title = new() { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _sub = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _bar = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
    /// <summary>0.6.38: the files of the next message, a picture as its preview, each with ✕ to take it out.</summary>
    private readonly WrapPanel _tray = new() { Margin = new Thickness(0, 4, 0, 0), Visibility = Visibility.Collapsed };
    private readonly Button _older = new() { Content = "Load older messages" };
    private readonly Button _pin;

    /// <summary>Shown in the side panel: no "Pin" button (it is already pinned).</summary>
    public bool InPanel { get => _pin.Visibility != Visibility.Visible; init => _pin.Visibility = value ? Visibility.Collapsed : Visibility.Visible; }
    private readonly ComboBox _burn = new() { MinWidth = 120, ToolTip = "Burn after reading: shown once for this long after it's opened, then gone everywhere" };
    private readonly Dictionary<Entry, FrameworkElement> _rows = new();
    private readonly List<ChatEngine.OutgoingFile> _files = new();
    private ChatMessage? _reply, _edit;
    private bool _disposed;

    /// <summary>The key a room's tab is opened with (one tab per room per account).</summary>
    public static string TabKey(ChatSession s, Room r) => "chat:" + s.Origin + "|" + s.User + "|" + r.Id;

    public ChatRoomView(ChatSession session, Room room)
    {
        _session = session;
        _e = session.Engine ?? throw new InvalidOperationException("Unlock the chat first.");
        _room = room;

        _older.Style = (Style)FindResource("SmallButton");
        _older.Click += async (_, _) => await Run(() => _e.LoadOlderAsync(_room));
        _sub.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _bar.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        foreach (var (label, secs) in new[] { ("No burn", 0), ("Burn: 10 s", 10), ("Burn: 1 min", 60), ("Burn: 5 min", 300) })
            _burn.Items.Add(new ComboBoxItem { Content = label, Tag = secs });
        _burn.SelectedIndex = 0;

        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(tools, Dock.Right);
        tools.Children.Add(_older);
        // 0.6.38: calls (CipherChat's own call page; follows ChatAllowCalls). Everyone in the call must be verified.
        if (ChatPolicy.CallsBlocked() is null && _room.Members.Any(m => m.User != _e.MyUser))
        {
            tools.Children.Add(SmallButton("📞 Call", () => Calls.CallWindow.Start(_session, _room, "audio"), "Voice call with this room (end-to-end encrypted)"));
            tools.Children.Add(SmallButton("🎥 Video", () => Calls.CallWindow.Start(_session, _room, "video"), "Video call with this room (end-to-end encrypted)"));
        }
        tools.Children.Add(SmallButton("Members…", () => ShowMembers(), "Who is in this room and whether you've verified them"));
        _pin = SmallButton("📌 Pin", () => ChatPanelView.Pin(_session, _room), "Pin this room to the side panel, beside whatever tab you have open");
        tools.Children.Add(_pin);
        head.Children.Add(tools);
        head.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { _title, _sub } });

        _scroll.Content = _log;
        _input.SetResourceReference(Control.BackgroundProperty, "Input");
        _input.PreviewKeyDown += OnInputKey;
        AutomationProps(_input, "Message");

        var send = new Button { Content = "Send", MinWidth = 80, Margin = new Thickness(6, 0, 0, 0), Style = (Style)FindResource("PrimaryButton") };
        send.Click += async (_, _) => await SendAsync();
        var attach = SmallButton("📎 Attach…", AddFiles, "Send files or pictures (encrypted before they leave this PC)");
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        actions.Children.Add(attach); actions.Children.Add(_burn); actions.Children.Add(send);
        var composer = new DockPanel();
        DockPanel.SetDock(actions, Dock.Bottom);
        DockPanel.SetDock(_bar, Dock.Top);
        DockPanel.SetDock(_tray, Dock.Top);
        AutomationProps(_tray, "Files to send");
        composer.Children.Add(_bar); composer.Children.Add(_tray); composer.Children.Add(actions); composer.Children.Add(_input);

        var grid = new Grid { Margin = new Thickness(12) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_scroll, 1); Grid.SetRow(composer, 2);
        composer.Margin = new Thickness(0, 8, 0, 0);
        grid.Children.Add(head); grid.Children.Add(_scroll); grid.Children.Add(composer);
        Content = grid;
        SetResourceReference(BackgroundProperty, "Bg");

        _e.EntryAdded += OnAdded;
        _e.EntryChanged += OnChanged;
        _e.EntryRemoved += OnRemoved;
        _e.RoomsChanged += OnRoomsChanged;
        _session.StageChanged += OnStage;
        IsKeyboardFocusWithinChanged += (_, _) => Focused();
        Loaded += async (_, _) => await OpenAsync();
    }

    private static void AutomationProps(FrameworkElement el, string name) => System.Windows.Automation.AutomationProperties.SetName(el, name);

    private Button SmallButton(string text, Action click, string tip)
    {
        var b = new Button { Content = text, ToolTip = tip, Style = (Style)FindResource("SmallButton"), Margin = new Thickness(0, 0, 6, 0) };
        b.Click += (_, _) => click();
        return b;
    }

    private void Ui(Action a) { if (!_disposed) Dispatcher.BeginInvoke(() => { if (!_disposed) a(); }); }

    private async Task Run(Func<Task> step)
    {
        try { await step(); }
        catch (Exception ex) { Dialogs.Error("CipherChat", ex.Message, ex: ex); }
    }

    // ------------------------------------------------------------------------------------------- the room

    private async Task OpenAsync()
    {
        RenderHeader();
        _log.Children.Clear(); _rows.Clear();
        List<Entry> snapshot;
        lock (_room.Timeline) snapshot = _room.Timeline.ToList();
        foreach (var en in snapshot) Insert(en, -1);
        await Run(() => _e.OpenRoomAsync(_room));
        RenderHeader();
        await Run(() => _e.MaybeAutoRotateAsync(_room));
        Focused();
        ScrollEnd(true);
    }

    /// <summary>Messages in the room you're looking at aren't unread.</summary>
    private void Focused()
    {
        if (!IsVisible) return;
        _e.Viewing = _room;
        _e.MarkRead(_room);
    }

    private void RenderHeader()
    {
        _title.Text = _room.Label(_e.MyUser);
        var members = _room.Members.Count;
        _sub.Text = (_room.Topic.Length > 0 ? Clean(_room.Topic) + "  ·  " : "") + members + (members == 1 ? " member" : " members")
            + (_room.RetentionDays > 0 ? "  ·  kept " + _room.RetentionDays + " days" : "") + "  ·  end-to-end encrypted";
        _older.Visibility = _room.MoreHistory ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnRoomsChanged() => Ui(RenderHeader);
    private void OnStage() => Ui(() => { if (_session.Engine is null) { _input.IsEnabled = false; _bar.Text = "The chat is locked: unlock it in Chats to carry on."; } });

    private void OnAdded(Room r, Entry en) => Ui(() =>
    {
        if (r != _room) return;
        int index;
        lock (r.Timeline) index = r.Timeline.IndexOf(en);
        Insert(en, index);
        if (IsKeyboardFocusWithin) _e.MarkRead(_room);
        ScrollEnd(en.Message?.Mine == true);
    });

    private void OnChanged(Room r, Entry en) => Ui(() =>
    {
        if (r != _room || !_rows.TryGetValue(en, out var old)) return;
        var i = _log.Children.IndexOf(old);
        var row = Build(en);
        _rows[en] = row;
        _log.Children.RemoveAt(i);
        _log.Children.Insert(i, row);
    });

    private void OnRemoved(Room r, Entry en) => Ui(() =>
    {
        if (r != _room || !_rows.Remove(en, out var row)) return;
        _log.Children.Remove(row);
    });

    private void Insert(Entry en, int index)
    {
        var row = Build(en);
        _rows[en] = row;
        if (index < 0 || index > _log.Children.Count) _log.Children.Add(row); else _log.Children.Insert(index, row);
    }

    private void ScrollEnd(bool force)
    {
        if (force || _scroll.ScrollableHeight - _scroll.VerticalOffset < 120)
            Dispatcher.BeginInvoke(_scroll.ScrollToEnd, DispatcherPriority.Background);
    }

    // ------------------------------------------------------------------------------------------- one line

    /// <summary>Message text as plain text: control characters (other than new lines and tabs) are dropped.</summary>
    internal static string Clean(string s) => new(s.Where(c => c == '\n' || c == '\t' || !char.IsControl(c)).Take(20_000).ToArray());

    private FrameworkElement Build(Entry en)
    {
        var when = DateTimeOffset.FromUnixTimeSeconds(Math.Max(0, en.At)).ToLocalTime().ToString("HH:mm");
        if (en.Message is not { } m)
        {
            var line = new TextBlock { Text = when + "  " + Clean(en.Text), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2), FontStyle = FontStyles.Italic };
            line.SetResourceReference(TextBlock.ForegroundProperty, en.Kind switch { LineKind.Err => "Bad", LineKind.Warn => "Warn", LineKind.Ok => "Good", _ => "Muted" });
            return line;
        }

        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        var who = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var name = new System.Windows.Documents.Run(m.Mine ? "you" : Clean(m.From)) { FontWeight = FontWeights.SemiBold };
        name.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, m.Mine ? "Accent" : "FgStrong");
        who.Inlines.Add(name);
        var meta = new System.Windows.Documents.Run("  " + when + (m.EditedSeq > 0 ? "  (edited)" : "") + (m.Burn > 0 ? "  🔥 " + m.Burn + " s" : ""));
        meta.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Muted");
        who.Inlines.Add(meta);
        panel.Children.Add(who);

        if (m.ReplyTo is { } rid && _e.MessageById(_room, rid) is { } orig)
        {
            var q = new TextBlock { Text = "↩ " + Clean(orig.From) + ": " + Shorten(Clean(orig.Text), 120), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 0, 0, 2) };
            q.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            panel.Children.Add(q);
        }

        if (m.Burn > 0 && !m.Revealed && !m.Mine)
        {
            if (m.Burned) panel.Children.Add(Muted("🔥 Burned."));
            else
            {
                var reveal = new Button { Content = "🔥 Show once (" + m.Burn + " s)", Style = (Style)FindResource("SmallButton"), HorizontalAlignment = HorizontalAlignment.Left };
                reveal.Click += async (_, _) => await Run(async () => { await _e.RevealBurnAsync(_room, m); StartBurn(m); });
                panel.Children.Add(reveal);
            }
        }
        else
        {
            if (m.Text.Length > 0)
            {
                // A read-only box, so text can be selected and copied (and never interpreted).
                var body = new TextBox { Text = Clean(m.Text), IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent, Padding = new Thickness(0) };
                body.SetResourceReference(Control.ForegroundProperty, "Fg");
                if (m.Kind == "me") body.FontStyle = FontStyles.Italic;
                panel.Children.Add(body);
            }
            foreach (var f in m.Files)
            {
                if (f.Kind == "image" && f.Thumb is not null) panel.Children.Add(PictureView(f));
                var file = new Button { Content = "📎 " + f.SafeName + "  (" + Size(f.Size) + ")", Style = (Style)FindResource("SmallButton"), HorizontalAlignment = HorizontalAlignment.Left, ToolTip = "Save this file (decrypted on this PC)" };
                file.Click += async (_, _) => await SaveFileAsync(f);
                panel.Children.Add(file);
            }
        }

        var reactions = m.Reactions;
        var tools = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
        foreach (var (emoji, users) in reactions)
        {
            bool mine = m.MyReaction(_e.MyUser, emoji);
            var r = new Button { Content = emoji + " " + users.Count, Style = (Style)FindResource(mine ? "SmallPrimaryButton" : "SmallButton"), ToolTip = string.Join(", ", users.Select(Clean)), Margin = new Thickness(0, 0, 4, 0) };
            r.Click += async (_, _) => await Run(() => _e.ReactAsync(_room, m, emoji, !mine));
            tools.Children.Add(r);
        }
        if (!m.Burned)
        {
            tools.Children.Add(Link("↩ Reply", () => { _reply = m; _edit = null; RenderBar(); _input.Focus(); }));
            tools.Children.Add(Link("👍", async () => await Run(() => _e.ReactAsync(_room, m, "👍", !m.MyReaction(_e.MyUser, "👍")))));
            if (m.Mine && m.Kind is "text" or "me" && m.Burn == 0) tools.Children.Add(Link("✎ Edit", () => { _edit = m; _reply = null; _input.Text = m.Text; RenderBar(); _input.Focus(); _input.CaretIndex = _input.Text.Length; }));
            if (m.Mine) tools.Children.Add(Link("🗑 Delete", async () =>
            {
                if (Dialogs.Confirm("Delete message", "Delete this message for everyone in the room?", "Delete", danger: true))
                    await Run(() => _e.RedactAsync(_room, m.Seq));
            }));
            if (m.Text.Length > 0) tools.Children.Add(Link("⧉ Copy", () => { try { Clipboard.SetText(m.Text); } catch (Exception ex) { Log.Write("Chat copy", ex); } }));
        }
        panel.Children.Add(tools);
        return panel;
    }

    /// <summary>A picture in a message (0.6.38): its encrypted thumbnail, fetched and decrypted when the message is shown,
    /// and shown only when its bytes are a PNG, JPEG or GIF. Clicking saves the full picture.</summary>
    private FrameworkElement PictureView(FileRef f)
    {
        var img = new Image { MaxWidth = 300, MaxHeight = 220, Stretch = System.Windows.Media.Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left, Cursor = Cursors.Hand, Margin = new Thickness(0, 4, 0, 2), ToolTip = "Save the picture (decrypted on this PC)" };
        AutomationProps(img, "Picture: " + f.SafeName);
        img.MouseLeftButtonUp += async (_, _) => await SaveFileAsync(f);
        _ = LoadThumbAsync(f.Thumb!, img);
        return img;
    }

    private async Task LoadThumbAsync(FileRef thumb, Image img)
    {
        try
        {
            var bytes = await _e.FetchFileAsync(_room, thumb);
            if (!_disposed && ChatPictures.TryDecode(bytes, 300) is { } pic) img.Source = pic;
        }
        catch (Exception ex) when (ex is CipherChat.Api.ApiException or CipherChat.Crypto.CryptoFail or IOException or InvalidOperationException) { img.ToolTip = "Preview unavailable: " + ex.Message; }
    }

    private TextBlock Muted(string text)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        return t;
    }

    private Button Link(string text, Action click)
    {
        var b = new Button { Content = text, Style = (Style)FindResource("SmallButton"), Margin = new Thickness(0, 0, 4, 0), Opacity = 0.75 };
        b.Click += (_, _) => click();
        return b;
    }

    internal static string Size(long b) => b < 1024 ? b + " B" : b < 1024 * 1024 ? (b / 1024.0).ToString("0.#") + " KB" : (b / 1048576.0).ToString("0.#") + " MB";

    private static string Shorten(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    private void StartBurn(ChatMessage m)
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Clamp(m.Burn, 1, 86_400)) };
        t.Tick += (_, _) => { t.Stop(); if (!_disposed) _e.BurnOut(_room, m); };
        t.Start();
    }

    private async Task SaveFileAsync(FileRef f)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = f.SafeName, Title = "Save " + f.SafeName };
        if (TestMode.Running) { Dialogs.RefusedInSelfTest("save chat file"); return; }
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        await Run(async () =>
        {
            var bytes = await _e.FetchFileAsync(_room, f);
            await File.WriteAllBytesAsync(dlg.FileName, bytes);
            AuditLog.Write("you", "chat", "file saved from a chat", f.SafeName);
        });
    }

    // ------------------------------------------------------------------------------------------- the composer

    private void RenderBar()
    {
        var parts = new List<string>();
        if (_reply is { } r) parts.Add("↩ Replying to " + Clean(r.From) + ": " + Shorten(Clean(r.Text), 80) + "   (Esc to cancel)");
        if (_edit is not null) parts.Add("✎ Editing your message   (Esc to cancel)");
        _bar.Text = string.Join("\n", parts);
        _bar.Visibility = parts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RenderTray();
    }

    /// <summary>The files of the next message: a picture as its preview (from the thumbnail it will be sent with), anything
    /// else by name, each with its size and ✕. Nothing is sent until Send.</summary>
    private void RenderTray()
    {
        _tray.Children.Clear();
        foreach (var f in _files.ToList())
        {
            var card = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(6), Margin = new Thickness(0, 0, 6, 6) };
            card.SetResourceReference(Border.BorderBrushProperty, "Line");
            card.SetResourceReference(Border.BackgroundProperty, "Surface");
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            if (f.Thumb is { } t && ChatPictures.TryDecode(t, 160) is { } pic)
            {
                var img = new Image { Source = pic, MaxHeight = 96, MaxWidth = 160, Stretch = System.Windows.Media.Stretch.Uniform, Margin = new Thickness(0, 0, 6, 0) };
                AutomationProps(img, "Preview of " + f.Name);
                row.Children.Add(img);
            }
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, MaxWidth = 220 };
            text.Children.Add(new TextBlock { Text = (f.Thumb is null ? "📎 " : "") + Clean(f.Name), TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(Muted(Size(f.Bytes.LongLength) + (f.W is int w && f.H is int h ? " · " + w + "×" + h : "")));
            row.Children.Add(text);
            var remove = new Button { Content = "✕", Style = (Style)FindResource("SmallButton"), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top, ToolTip = "Don't send this file" };
            AutomationProps(remove, "Remove " + f.Name);
            var file = f;
            remove.Click += (_, _) => { _files.Remove(file); RenderTray(); };
            row.Children.Add(remove);
            card.Child = row;
            _tray.Children.Add(card);
        }
        _tray.Visibility = _files.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_files.Count > 0) _tray.ToolTip = "Esc removes them all";
    }

    /// <summary>The files waiting for Send (self-test).</summary>
    internal IReadOnlyList<ChatEngine.OutgoingFile> PendingForTest => _files;

    /// <summary>Self-test (the live run): types a message in the composer and sends it as the Send button does.</summary>
    internal Task SendForTest(string text) { _input.Text = text; return SendAsync(); }

    /// <summary>Self-test: every text the room's rows show, and how many pictures they show (a thumbnail loaded).</summary>
    internal IReadOnlyList<string> RowTextsForTest => _rows.Values.SelectMany(TextsIn).ToList();
    internal int PicturesShownForTest => _rows.Values.Sum(r => ElementsIn<Image>(r).Count(i => i.Source is not null));

    private static IEnumerable<string> TextsIn(DependencyObject d) =>
        ElementsIn<TextBlock>(d).Select(t => t.Text).Concat(ElementsIn<TextBox>(d).Select(t => t.Text))
            .Concat(ElementsIn<ContentControl>(d).Select(c => c.Content as string ?? "")).Where(t => t.Length > 0);

    private static IEnumerable<T> ElementsIn<T>(DependencyObject d) where T : DependencyObject
    {
        if (d is T t) yield return t;
        foreach (var c in LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>())
            foreach (var x in ElementsIn<T>(c)) yield return x;
    }

    private async void OnInputKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None) { e.Handled = true; await SendAsync(); }
        else if (e.Key == Key.Escape && (_reply is not null || _edit is not null || _files.Count > 0))
        {
            e.Handled = true;
            if (_edit is not null) _input.Clear();
            _reply = null; _edit = null; _files.Clear(); RenderBar();
        }
    }

    private int BurnSeconds => _burn.SelectedItem is ComboBoxItem { Tag: int s } ? s : 0;

    private async Task SendAsync()
    {
        var text = _input.Text.TrimEnd();
        if (text.Length == 0 && _files.Count == 0) return;
        if (text.Length > 16_000) { Dialogs.Info("CipherChat", "That message is too long (16,000 characters at most)."); return; }
        var reply = _reply?.Id; var edit = _edit; var files = _files.ToList(); var burn = BurnSeconds;
        _input.Clear(); _reply = null; _edit = null; _files.Clear(); RenderBar();
        await Run(async () =>
        {
            if (edit is not null) await _e.EditAsync(_room, edit, text);
            else if (files.Count > 0)
            {
                if (ChatPolicy.FilesBlocked() is { } why) { Dialogs.Info("CipherChat", why); return; }
                await _e.SendFilesAsync(_room, text, files, reply, burn);
                AuditLog.Write("you", "chat", "files sent", files.Count + " file(s) to " + _room.Label(_e.MyUser));
            }
            else await _e.SendTextAsync(_room, text, reply, burn);
        });
    }

    private void AddFiles()
    {
        if (ChatPolicy.FilesBlocked() is { } why) { Dialogs.Info("CipherChat", why); return; }
        if (TestMode.Running) { Dialogs.RefusedInSelfTest("pick chat files"); return; }
        var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "Send files" };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        foreach (var path in dlg.FileNames) AddFile(path);
    }

    /// <summary>Adds a file to the next message (also used by "Send to chat" from the file commander).</summary>
    public void AddFile(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > ChatPolicy.MaxFileBytes) { Dialogs.Info("CipherChat", $"{info.Name} is {Size(info.Length)}; the most you can send is {Size(ChatPolicy.MaxFileBytes)}."); return; }
        _files.Add(ChatPictures.Outgoing(info.Name, File.ReadAllBytes(path)));
        RenderBar();
    }

    /// <summary>Adds a file a screen handed over (Send to a chat) to the next message.</summary>
    public void AddFile(string name, byte[] bytes, string? mime)
    {
        if (bytes.LongLength > ChatPolicy.MaxFileBytes) { Dialogs.Info("CipherChat", $"{name} is {Size(bytes.LongLength)}; the most you can send is {Size(ChatPolicy.MaxFileBytes)}."); return; }
        _files.Add(ChatPictures.Outgoing(name, bytes, mime));
        RenderBar();
    }

    /// <summary>Puts text in the composer (a share from a terminal, Toolbox or a report) without sending it.</summary>
    public void Prefill(string text)
    {
        _input.Text = _input.Text.Length == 0 ? text : _input.Text + "\n" + text;
        _input.Focus();
        _input.CaretIndex = _input.Text.Length;
    }

    private void ShowMembers()
    {
        var lines = _room.Members.Select(m =>
        {
            var p = _e.People.GetValueOrDefault(m.User);
            var trust = p is null ? "unknown" : p.Trust == TrustLevel.Ok ? "verified" : "not verified (" + Clean(p.Why) + ")";
            return Clean(m.User) + (m.Role != "member" ? " (" + m.Role + ")" : "") + " — " + trust;
        });
        Dialogs.ShowText(_room.Label(_e.MyUser) + " — members", string.Join("\n", lines));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _e.EntryAdded -= OnAdded;
        _e.EntryChanged -= OnChanged;
        _e.EntryRemoved -= OnRemoved;
        _e.RoomsChanged -= OnRoomsChanged;
        _session.StageChanged -= OnStage;
        if (_e.Viewing == _room) _e.Viewing = null;
    }
}
