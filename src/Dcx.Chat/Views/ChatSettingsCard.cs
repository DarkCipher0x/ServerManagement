using System.Windows;
using System.Windows.Controls;
using DcxManager.Services;
using DcxManager.Views;

namespace DcxManager.Chat.Views;

/// <summary>
/// Settings → CipherChat → "Chat accounts" (0.6.39, ADR-117): the chat accounts this PC is paired with, whether each is
/// locked or unlocked, and what you can do with them — open, lock, or forget this PC on that server. Contributed by the
/// chat module, so it is there only while CipherChat is on. Nothing secret is shown: no device token, no passphrase.
/// </summary>
internal static class ChatSettingsCard
{
    public static Border Accounts(ISettingsKit v)
    {
        var list = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        var lockAll = v.Btn("Lock every chat", () => ChatHub.LockAll("locked from Settings"));

        void Render()
        {
            list.Children.Clear();
            var sessions = ChatHub.Sessions.Where(s => s.Account is not null || s.Engine is not null).ToList();
            if (sessions.Count == 0)
            {
                list.Children.Add(new TextBlock { Text = "This PC isn't paired with a CipherChat account yet. Open Chats to sign in or pair it with a code from your phone or another computer.", TextWrapping = TextWrapping.Wrap, Style = (Style)v.FindResource("Hint") });
            }
            foreach (var s in sessions)
            {
                var state = s.Stage switch
                {
                    ChatSession.Stages.Ready => "unlocked",
                    ChatSession.Stages.NeedsUnlock => "locked (needs your chat passphrase)",
                    ChatSession.Stages.NeedsOwnerCheck => "waiting for the owner's safety number check",
                    ChatSession.Stages.NeedsIdentity => "signed in, no identity yet",
                    _ => s.LastError is { Length: > 0 } why ? "signed out — " + why : "signed out",
                };
                var name = new TextBlock { Text = ChatRoomView.Clean(s.Display) + "  ·  " + state, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
                var row = v.Row(name);
                if (s.Engine is not null) row.Children.Add(v.Btn("Lock", () => { s.Lock(); AuditLog.Write("you", "chat", "chat locked from Settings", s.Display); }));
                if (s.Account is { } acct)
                    row.Children.Add(v.Btn("Forget this PC…", async () => await ForgetAsync(s, acct)));
                list.Children.Add(row);
            }
            lockAll.IsEnabled = ChatHub.Sessions.Any(s => s.Engine is not null);
        }

        void Changed() { if (list.Dispatcher.CheckAccess()) Render(); else list.Dispatcher.BeginInvoke(Render); }
        v.Loaded += (_, _) => { ChatHub.Changed -= Changed; ChatHub.Changed += Changed; Render(); };
        v.Unloaded += (_, _) => ChatHub.Changed -= Changed;
        Render();

        return v.Card("Chat accounts",
            "The CipherChat accounts this PC is paired with. Each is remembered encrypted (only the device's sign-in, never your chat passphrase); locking wipes the chat keys from memory and the next unlock asks for the passphrase. Forgetting this PC signs it out of that server and removes its pairing.",
            list, v.Row(v.Btn("Open Chats", ChatModule.OpenHome, "PrimaryButton"), lockAll));
    }

    private static async Task ForgetAsync(ChatSession s, ChatAccount a)
    {
        if (!Dialogs.Confirm("Forget this PC", $"Sign this PC out of {a.Display} and remove its pairing? Your account and messages stay on the server; to use it here again you sign in or pair again.", "Forget this PC", danger: true)) return;
        try { await s.SignOutAsync(forgetPc: true); AuditLog.Write("you", "chat", "this PC forgotten", a.Display); }
        catch (Exception ex) { Dialogs.Error("Chat accounts", ex.Message); }
    }
}
