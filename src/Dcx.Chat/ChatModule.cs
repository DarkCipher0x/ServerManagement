using System.Windows;
using DcxManager.Chat.Views;
using DcxManager.Services;
using DcxManager.Views;

namespace DcxManager.Chat;

/// <summary>
/// CipherChat as a module (ADR-117): everything chat adds to the app goes through <see cref="IAppHost"/>, so with the
/// integration off (the default; Settings → Security &amp; privacy, or Group Policy <c>ChatFeatures</c>) the module is never
/// registered and its assembly and the vendored CipherChat code are never loaded. The screens read one hub
/// (<see cref="ChatHub"/>); the rules come from <see cref="ChatPolicy"/>; the protocol is CipherChat's own pinned code.
/// </summary>
public sealed class ChatModule : IAppModule
{
    public string Id => "chat";

    public const string HomeKey = "chat", AdminKey = "chat-admin";

    public static void OpenHome() => ModuleTabs.Open(HomeKey, "💬 Chats", () => new ChatHomeView());
    public static void OpenAdmin() => ModuleTabs.Open(AdminKey, "⚑ Chat server admin", () => new ChatAdminView());

    public void Register(IAppHost host)
    {
        host.NavItem("CHAT", HomeKey, "💬  Chats", "Your CipherChat rooms and messages, end-to-end encrypted (Ctrl+Shift+M)",
            screen: () => new ChatHomeView(), header: "💬 Chats",
            menu: () => new object?[]
            {
                Menus.Item(ModulePanels.Shown() == ChatPanelView.PanelId ? "Close the chat panel" : "Show the chat panel", () => ModulePanels.Toggle(ChatPanelView.PanelId), Services.Keys.KeyCommands.Text("chat.panel")),
                Menus.Item("Lock every chat", () => ChatHub.LockAll("locked from the sidebar"), enabled: ChatHub.Sessions.Any(s => s.Engine != null)),
            });
        if (AppSettings.Current.ChatAdmin)
            host.NavItem("CHAT", AdminKey, "⚑  Chat server admin", "Manage a CipherChat server's people, requests, roles, settings and email (separate sign-in)",
                screen: () => new ChatAdminView(), header: "⚑ Chat server admin");

        host.Command("chat.open", "Open Chats", "CipherChat", OpenHome, "Ctrl+Shift+M");
        host.Command("chat.admin", "Open chat server administration", "CipherChat", () => { if (Admin.ChatAdminClient.Blocked() is null) OpenAdmin(); });
        // The side panel: one room pinned beside the documents (📌 Pin on a room tab, or this command).
        // 0.6.39: the module's own card on the app's CipherChat settings page (the switch and the rules are the app's).
        host.SettingsCard("CipherChat", "Chat accounts", "this Windows user · paired accounts", Views.ChatSettingsCard.Accounts);
        host.SidePanel(ChatPanelView.PanelId, "💬 Chat", () => new ChatPanelView());
        host.Command("chat.panel", "Show or hide the chat panel", "CipherChat", () => ModulePanels.Toggle(ChatPanelView.PanelId), "Ctrl+Shift+J");
        host.Command("chat.lock", "Lock every chat", "CipherChat", () => ChatHub.LockAll("locked by a shortcut"));

        // Saved pairings sign back in after the window is shown (never in the self-test); each chat stays locked until its
        // passphrase is entered.
        host.Startup(ChatHub.ResumeSavedAsync);
        // Locking the app or quitting wipes every chat's keys and ends the admin sign-in.
        AppLockEvents.Locking += ChatHub.LockAll;
        ChatIdleLock.Start();   // 0.6.39: ChatAutoLockMinutes
        host.Shutdown(() => ChatHub.LockAll("the app closed"));

        // Dashboard: unread messages over the unlocked chats (nothing is contacted; the count is the engines' own).
        host.DashboardSource("chat", "CipherChat",
            () => ChatHub.Sessions.Count > 0 ? "chat" : null,
            () => Task.FromResult(new CheckOutcome(UnreadText(), null)),
            (outcome, when) => new TileText(ChatHub.Unread.ToString(), Locked() is > 0 and var n ? n + " chat(s) locked" : "unread messages", ChatHub.Unread > 0));

        // 0.6.38: who you talk to — people not verified yet, and a changed key (the warning that matters) — from the unlocked
        // chats' own trust records; nothing is contacted.
        host.DashboardSource("chat-people", "Chat contacts",
            () => ChatHub.Sessions.Any(s => s.Engine != null) ? "chat" : null,
            () => Task.FromResult(new CheckOutcome(TrustText(Trust()), null)),
            (outcome, when) => TrustTile(Trust()));

        // 0.6.38: the chat server you administer — sign-up requests waiting — only while you're signed in to its
        // administration (memory only), and only under the dashboard's check rule or Check now.
        host.DashboardSource("chat-admin", "Chat server",
            () => ChatHub.Admin.SignedIn && AppSettings.Current.ChatAdmin ? ChatHub.Admin.Display : null,
            AdminCheckAsync,
            (outcome, when) => AdminTile(outcome));

        // Privacy & network: where chat traffic goes and how it is protected.
        host.PrivacyRows(
            () => new[]
            {
                new PrivacyPosture("CipherChat", "end-to-end encrypted (post-quantum hybrid); keys only in memory while unlocked", PrivacyLevel.Good,
                    "The chat passphrase is never stored or sent. Paired PCs keep only a device token, in the encrypted store."),
                new PrivacyPosture("CipherChat administration", AppSettings.Current.ChatAdmin ? "separate sign-in, memory only" : "off (policy)", AppSettings.Current.ChatAdmin ? PrivacyLevel.Info : PrivacyLevel.Good),
            },
            () => ChatAccounts.All.Select(a => new PrivacyEndpoint("CipherChat", a.Origin, NetworkPolicy.Classify(a.Origin), ChatHub.For(a).Engine != null ? "connected" : "paired", "Chat messages (encrypted), files and calls")));

        // "💬 Send to a chat…" on the screens (terminal, file commander, reports, snippets) goes here, and every 📷 menu
        // (terminal, tab strip, Remote Desktop) gets "Send screenshot to a chat…". Always reviewed in the room before sending.
        host.SendTarget(new Share.ChatSendTarget());
        host.ContextMenu("tab", Calls.LiveShare.MenuItems);   // 0.6.39: share a terminal / RDP / VNC tab live into a room
        host.ContextMenu("screenshot", ctx => ctx is ScreenshotMenuContext shot
            ? new object?[] { Menus.SendItem("💬 Send screenshot to a chat…", async () => SendItem.OfFile(shot.Subject, new SendFile(Share.ChatSendTarget.SafeName(shot.Subject) + ".png", Png(await shot.Capture()), "image/png"))) }
            : Array.Empty<object?>());

        host.HelpArticles();

        // The pairings file in the store's checks (restore, snapshots, the integrity scan): what it is and a strict parse.
        StoreSchema.Register(ChatAccounts.FileName, "CipherChat pairings (chat-accounts.dcx)",
            plain => System.Text.Json.JsonSerializer.Deserialize<List<ChatAccount>>(plain, SecureStore.Json) ?? throw new System.IO.InvalidDataException("empty"));
    }

    private static byte[] Png(System.Windows.Media.Imaging.BitmapSource image)
    {
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
        using var ms = new System.IO.MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    private static int Locked() => ChatHub.Sessions.Count(s => s.Engine == null && s.Api != null);

    /// <summary>People across the unlocked chats (yourself left out): verified, not verified yet, and a changed key.</summary>
    internal readonly record struct TrustCounts(int Verified, int Pending, int Changed);

    internal static TrustCounts Trust()
    {
        int ok = 0, pending = 0, bad = 0;
        foreach (var e in ChatHub.Sessions.Select(s => s.Engine).OfType<CipherChat.Engine.ChatEngine>())
            foreach (var p in e.People.Values.Where(p => p.User != e.MyUser))
                switch (p.Trust)
                {
                    case CipherChat.Engine.TrustLevel.Ok: ok++; break;
                    case CipherChat.Engine.TrustLevel.Bad: bad++; break;
                    case CipherChat.Engine.TrustLevel.Pending: pending++; break;
                }
        return new TrustCounts(ok, pending, bad);
    }

    internal static string TrustText(TrustCounts t) =>
        t.Changed > 0 ? t.Changed + " key change(s) to check" : t.Pending > 0 ? t.Pending + " not verified yet" : t.Verified + " verified";

    internal static TileText TrustTile(TrustCounts t) => t.Changed > 0
        ? new TileText("⚠ " + t.Changed, "key changed: check in Chats before you trust it", true)
        : t.Pending > 0 ? new TileText(t.Pending.ToString(), "not verified yet (" + t.Verified + " verified)") : new TileText(t.Verified.ToString(), "people verified");

    private static async Task<CheckOutcome> AdminCheckAsync()
    {
        try
        {
            var r = await ChatHub.Admin.RequestsAsync("new").ConfigureAwait(false);
            return new CheckOutcome(r.Requests.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), null);
        }
        catch (CipherChat.Api.ApiException e) when (e.Status == 403) { return new CheckOutcome("-", null); }
        catch (Exception e) when (e is CipherChat.Api.ApiException or InvalidOperationException or System.IO.IOException) { return new CheckOutcome(null, e.Message); }
    }

    internal static TileText AdminTile(CheckOutcome? o) => o switch
    {
        null => new TileText("…", "sign-up requests: not checked yet"),
        { Error: { } err } => new TileText("!", err, true),
        { Text: "-" } => new TileText("-", "you can't review sign-up requests here"),
        { Text: var n } when int.TryParse(n, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var c) =>
            new TileText(c.ToString(System.Globalization.CultureInfo.InvariantCulture), c == 1 ? "sign-up request waiting" : "sign-up requests waiting", c > 0),
        _ => new TileText("?", "sign-up requests"),
    };

    private static string UnreadText() => ChatHub.Unread switch { 0 => "no unread messages", 1 => "1 unread message", var n => n + " unread messages" };
}
