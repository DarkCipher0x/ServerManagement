using System.Windows.Controls;
using DcxManager.Chat.Share;
using DcxManager.Chat.Views;
using DcxManager.Services;
using DcxManager.Views;
using Room = CipherChat.Engine.Room;

namespace DcxManager.Chat.Calls;

/// <summary>
/// Live session sharing (0.6.39, ADR-117): a terminal, Remote Desktop or VNC tab shared into a chat room as a call in
/// which the others see that tab, live and view-only. The tab moves into a window of its own with a fixed title, and the
/// call's browser is started so that its screen share can pick only that window (Chromium's source auto-select by
/// title): never the screen, never another window, and nothing else opens into that window while it is shared. You
/// confirm every share; it's audited; ending the call stops it and gives the window its normal title back. It follows
/// ChatAllowSessionShare and ChatAllowCalls (both Group Policies), and the people in the call must be verified as for
/// any call. Only this PC's own view is sent: the others can't type or click in the session.
/// </summary>
internal static class LiveShare
{
    /// <summary>The shared window's title; the share browser captures the window with exactly this title (ASCII only).</summary>
    public const string WindowTitle = "CipherChat live share - ServerManagement";

    private static readonly HashSet<string> Shareable = ["TerminalView", "RdpView", "VncView"];

    /// <summary>The tab is a terminal, Remote Desktop or VNC session.</summary>
    public static bool CanShare(TabItem tab) => tab.Content is { } c && Shareable.Contains(c.GetType().Name);

    public static string? Blocked() =>
        !AppSettings.Current.ChatAllowSessionShare ? "Live session sharing is turned off on this PC (your organisation's policy or Settings → CipherChat)." : ChatPolicy.CallsBlocked();

    /// <summary>The tab menu's item (only for session tabs).</summary>
    public static IEnumerable<object?> MenuItems(object? ctx) =>
        ctx is TabMenuContext t && CanShare(t.Tab)
            ? [Menus.Item("📡 Share live in a chat…", () => Pick(t.Tab, t.ScreenName), enabled: Blocked() is null,
                tip: Blocked() ?? "Show this session live, view-only, to the people in a chat room (an end-to-end encrypted call)")]
            : [];

    /// <summary>Asks for the room and confirmation, then shares.</summary>
    public static void Pick(TabItem tab, string screen)
    {
        if (Blocked() is { } why) { Dialogs.Info("Live session sharing", why); return; }
        var rooms = ChatSendTarget.Rooms().Where(r => r.Room.Members.Any(m => m.User != r.Session.Engine?.MyUser)).ToList();
        if (rooms.Count == 0)
        {
            Dialogs.Info("Live session sharing", "No unlocked chat has a room with someone else in it. Open Chats, unlock your chat, then share again.");
            ChatModule.OpenHome();
            return;
        }
        if (TestMode.Running) { Dialogs.RefusedInSelfTest("pick: share a session live"); return; }
        var what = Describe(tab, screen);
        if (Dialogs.Pick("Share live in a chat", $"Which room should see “{what}” live?", rooms, r => r.Label) is not { } to) return;
        if (to.Session.Engine is null) { Dialogs.Info("Live session sharing", "That chat was locked in the meantime."); return; }
        if (!Dialogs.Confirm("Share live in a chat",
                $"Share “{what}” live with {to.Label}?\n\nThe tab moves into a window of its own and the people in the call see that window — only it — for as long as the call lasts: everything shown in it, including what you type there and any password prompt. They can't type or click in it. End the call to stop sharing.",
                "Share live")) return;
        Start(to.Session, to.Room, tab, what);
    }

    /// <summary>Isolates the tab and starts the share call (no questions: the live run and <see cref="Pick"/>).</summary>
    internal static bool Start(ChatSession s, Room room, TabItem tab, string what)
    {
        if (Blocked() is { } why) { Dialogs.Info("Live session sharing", why); return false; }
        if (CallWindow.WhyNot(s) is { } busy) { Dialogs.Info("Live session sharing", busy); return false; }
        if (ModuleTabs.Isolate(tab, WindowTitle) is not { } restore) { Dialogs.Info("Live session sharing", "This tab couldn't be moved into a window of its own."); return false; }
        AuditLog.Write("you", "chat", "live session shared", what + " → " + room.Label(s.Engine!.MyUser));
        CallWindow.StartShare(s, room, what, restore);
        return true;
    }

    internal static string Describe(TabItem tab, string screen)
    {
        var header = ChatRoomView.Clean(tab.Header as string ?? screen).Trim();
        return header.Length is > 0 and <= 80 ? header : header.Length > 80 ? header[..80] + "…" : screen;
    }
}
