using System.Windows;
using System.Windows.Controls;
using CipherChat.Engine;
using DcxManager.Services;
using DcxManager.Views;

namespace DcxManager.Chat.Views;

/// <summary>
/// The chat side panel (ADR-117): one room pinned beside the documents, so you can talk to the team while you work in a
/// terminal or any other tab. The room is picked from every unlocked chat (any account); "📌 Pin to the side panel" on a
/// room tab puts that room here. It is the same room on the same engine as its tab (<see cref="ChatHub"/>), so messages,
/// unread counts and edits agree. Locking clears it; the panel then offers to open Chats to unlock.
/// </summary>
public sealed class ChatPanelView : UserControl, IDisposable
{
    public const string PanelId = "chat";

    /// <summary>The pinned room ("chat:origin|user|room", <see cref="ChatRoomView.TabKey"/>). 0.6.38: kept in the encrypted
    /// settings, so the panel shows the same room after a restart once that chat is unlocked.</summary>
    internal static string Pinned
    {
        get => AppSettings.Current.ChatPanelRoom ?? "";
        private set
        {
            if (AppSettings.Current.ChatPanelRoom == value) return;
            AppSettings.Current.ChatPanelRoom = value;
            try { AppSettings.Save(); } catch (Exception ex) { Log.Write("Chat panel room", ex); }
        }
    }
    private static event Action? PinChanged;

    /// <summary>Pins a room to the side panel and shows it.</summary>
    public static void Pin(ChatSession s, Room r)
    {
        Pinned = ChatRoomView.TabKey(s, r);
        PinChanged?.Invoke();
        ModulePanels.Show(PanelId);
    }

    private readonly ComboBox _pick = new() { MinWidth = 160 };
    private readonly ContentControl _body = new();
    private readonly List<ChatEngine> _watched = new();
    private ChatRoomView? _room;
    private bool _quiet, _disposed;

    private sealed record Choice(ChatSession Session, Room Room, string Key, string Text)
    {
        public override string ToString() => Text;
    }

    public ChatPanelView()
    {
        System.Windows.Automation.AutomationProperties.SetName(_pick, "Room in the panel");
        _pick.SelectionChanged += (_, _) =>
        {
            if (_quiet || _pick.SelectedItem is not Choice c) return;
            Pinned = c.Key;
            ShowRoom();
        };
        var asTab = new Button { Content = "Open as tab", Margin = new Thickness(6, 0, 0, 0), Style = (Style)FindResource("SmallButton"), ToolTip = "Open this room as a tab too" };
        asTab.Click += (_, _) => { if (_pick.SelectedItem is Choice c) ChatHomeView.OpenRoom(c.Session, c.Room); };
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(asTab, Dock.Right);
        top.Children.Add(asTab); top.Children.Add(_pick);
        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top); root.Children.Add(_body);
        Content = root;

        ChatHub.Changed += OnChanged;
        PinChanged += OnChanged;
        Loaded += (_, _) => Refresh();
    }

    private void OnChanged() { if (!_disposed) Dispatcher.BeginInvoke(() => { if (!_disposed) Refresh(); }); }

    /// <summary>Rebuilds the room list from every unlocked chat and shows the pinned room (or the first one).</summary>
    private void Refresh()
    {
        foreach (var e in _watched) e.RoomsChanged -= OnChanged;
        _watched.Clear();
        var ready = ChatHub.Sessions.Where(s => s.Engine != null).ToList();
        bool many = ready.Count > 1;
        var choices = new List<Choice>();
        foreach (var s in ready)
        {
            var e = s.Engine!;
            e.RoomsChanged += OnChanged;
            _watched.Add(e);
            foreach (var r in e.Rooms.OrderBy(r => r.Kind == "dm").ThenBy(r => r.Label(e.MyUser), StringComparer.OrdinalIgnoreCase))
                choices.Add(new Choice(s, r, ChatRoomView.TabKey(s, r),
                    ChatRoomView.Clean(r.Label(e.MyUser)) + (many ? " · " + s.Display : "") + (r.Unread > 0 ? "  (" + r.Unread + ")" : "")));
        }
        _quiet = true;
        try
        {
            _pick.Items.Clear();
            foreach (var c in choices) _pick.Items.Add(c);
            _pick.SelectedItem = choices.FirstOrDefault(c => c.Key == Pinned) ?? choices.FirstOrDefault();
            _pick.IsEnabled = choices.Count > 0;
        }
        finally { _quiet = false; }
        if (Pinned.Length == 0 && _pick.SelectedItem is Choice chosen) Pinned = chosen.Key;
        ShowRoom();
    }

    private void ShowRoom()
    {
        var c = _pick.SelectedItem as Choice;
        if (c != null && _room != null && _room.Tag as string == c.Key && c.Session.Engine != null) return;
        _room?.Dispose();
        _room = null;
        if (c == null || c.Session.Engine == null) { _body.Content = Empty(); return; }
        _room = new ChatRoomView(c.Session, c.Room) { Tag = c.Key, InPanel = true };
        _body.Content = _room;
    }

    private FrameworkElement Empty()
    {
        var p = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        var t = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
            Text = ChatHub.Sessions.Any(s => s.Engine != null)
                ? "No rooms yet. Start one in Chats (New room… or Message someone…)."
                : "No chat is unlocked. Open Chats to pair this PC or unlock your chat with its passphrase.",
        };
        t.SetResourceReference(StyleProperty, "Hint");
        p.Children.Add(t);
        var open = new Button { Content = "Open Chats", HorizontalAlignment = HorizontalAlignment.Left };
        open.Click += (_, _) => ChatModule.OpenHome();
        p.Children.Add(open);
        return p;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ChatHub.Changed -= OnChanged;
        PinChanged -= OnChanged;
        foreach (var e in _watched) e.RoomsChanged -= OnChanged;
        _watched.Clear();
        _room?.Dispose();
        _room = null;
    }
}
