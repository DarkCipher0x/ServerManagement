using System.Windows;
using System.Windows.Controls;
using DcxManager.Services;

namespace DcxManager.Views;

// SettingsView: Settings → CipherChat (ADR-117; a page of its own since 0.6.39). These cards live in the app, not the chat
// module, because with chat off the module isn't loaded at all — the switch, the PC's rules and the notification and
// locking choices can still be seen and set (and are all Group Policies). The module adds "Chat accounts" when loaded.
public sealed partial class SettingsView
{
    internal const string ChatCategory = "CipherChat";

    /// <summary>CipherChat built in (on/off, admin-protected, Group Policy <c>ChatFeatures</c>), like AI features.</summary>
    private Border ChatFeaturesCard()
    {
        bool policy = ChatFeatures.Current.Source == ChatFeatures.From.Policy;
        var state = new TextBlock { Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
        var on = new RadioButton { Content = new TextBlock { Text = "On — load CipherChat: Chats in the sidebar, rooms as tabs, calls, live session sharing, sharing into chats, the dashboard tiles and (if allowed) server administration", TextWrapping = TextWrapping.Wrap }, GroupName = "chatfeatures", Margin = new Thickness(0, 2, 0, 2) };
        var off = new RadioButton { Content = new TextBlock { Text = "Off — no chat code is loaded: no chat screens, menus, connections or background work (paired accounts stay in the encrypted store)", TextWrapping = TextWrapping.Wrap }, GroupName = "chatfeatures", Margin = new Thickness(0, 2, 0, 2) };
        SettingTag.Set(on, nameof(AppSettings.ChatFeatures)); SettingTag.Set(off, nameof(AppSettings.ChatFeatures));
        var restart = Btn("Restart now", RestartForChat, "PrimaryButton");

        void Refresh()
        {
            var cur = ChatFeatures.Current;
            state.Text = $"This run: CipherChat {cur.Describe()} — the chat module {(cur.On ? "is loaded" : "is not loaded")}."
                + (policy ? "\nSet by your administrator (machine policy): it can't be changed here." : "")
                + (cur.Source == ChatFeatures.From.Environment ? $"\n{ChatFeatures.EnvVar} decides this run; the setting below applies when it isn't set." : "")
                + (ChatFeatures.RestartPending ? $"\nAfter a restart: {(S.ChatFeatures ? "on" : "off")}." : "");
            on.IsChecked = S.ChatFeatures; off.IsChecked = !S.ChatFeatures;
            on.IsEnabled = off.IsEnabled = !policy;
            restart.Visibility = ChatFeatures.RestartPending ? Visibility.Visible : Visibility.Collapsed;
        }
        async Task Choose(bool v)
        {
            if (S.ChatFeatures == v) return;
            try
            {
                if (await ProtectedSettings.ChangeAsync(v ? "turn CipherChat on" : "turn CipherChat off", "chat.features", v))
                    AuditLog.Write("you", "chat", v ? "CipherChat turned on (applies after a restart)" : "CipherChat turned off (applies after a restart)");
            }
            catch (Exception ex) { Dialogs.Error("CipherChat", ex.Message); }
            Refresh();
        }
        on.Click += async (_, _) => await Choose(true);
        off.Click += async (_, _) => await Choose(false);
        Refresh();

        return Card("CipherChat",
            "End-to-end encrypted chat and calls (post-quantum), built on CipherChat's own engine. This PC pairs as one of your CipherChat devices; your chat passphrase is never stored or sent, and locking the app wipes the chat keys. " +
            "Like AI features, it is off unless turned on, and an administrator can force it: HKLM\\SOFTWARE\\Policies\\DarkCipher Systems\\ServerManagement → ChatFeatures (DWORD 0 = off, 1 = on). Every option on this page has a policy of the same name.",
            state, on, off, Row(restart));
    }

    /// <summary>What chats may do on this PC: servers, files, calls, live sharing, sharing from screens, administration.</summary>
    private Border ChatRulesCard()
    {
        var size = new TextBox { Text = S.ChatMaxFileMB.ToString(), Width = 70 };
        SettingTag.Set(size, nameof(AppSettings.ChatMaxFileMB));
        size.LostFocus += (_, _) =>
        {
            if (int.TryParse(size.Text, out var mb) && mb is >= 1 and <= 100) { if (S.ChatMaxFileMB != mb) { S.ChatMaxFileMB = mb; Save(); } }
            else size.Text = S.ChatMaxFileMB.ToString();
        };

        // The servers this PC may use (empty = any https:// server you add); an organisation sets its own with the policy.
        var servers = new TextBox { Text = string.Join("\n", S.ChatAllowedServers), AcceptsReturn = true, MinHeight = 54, Width = FieldWidth, TextWrapping = TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        SettingTag.Set(servers, nameof(AppSettings.ChatAllowedServers));
        var serversNote = new TextBlock { Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        void ShowServers() => serversNote.Text = S.ChatAllowedServers.Count == 0 ? "Any https:// CipherChat server you add can be used." : "Only these servers can be used: " + string.Join(", ", S.ChatAllowedServers) + ".";
        servers.LostFocus += (_, _) =>
        {
            var list = new List<string>();
            foreach (var line in servers.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!Uri.TryCreate(line, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps || u.UserInfo.Length > 0 || u.Host.Length == 0)
                {
                    serversNote.Text = "“" + (line.Length > 60 ? line[..60] + "…" : line) + "” isn't an https:// server address — nothing was changed.";
                    return;
                }
                var origin = u.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
                if (!list.Contains(origin)) list.Add(origin);
            }
            if (list.Count > 200) { serversNote.Text = "200 servers at most — nothing was changed."; return; }
            if (!list.SequenceEqual(S.ChatAllowedServers)) { S.ChatAllowedServers = list; Save(); AuditLog.Write("you", "chat", "allowed CipherChat servers changed", list.Count == 0 ? "any" : string.Join(", ", list)); }
            servers.Text = string.Join("\n", list);
            ShowServers();
        };
        ShowServers();

        return Card("Chat rules on this PC",
            "What chats may do here. Each one is also a Group Policy (Settings → Security & privacy → Managed by your organisation shows the ones set); a rule set by policy shows its lock.",
            L("SERVERS"),
            Labeled("Allowed CipherChat servers (one https:// address per line; empty = any)", servers), serversNote,
            L("FILES"),
            Check("Files and pictures in chats", _s.ChatAllowFiles, v => _s.ChatAllowFiles = v),
            Row(Labeled("Largest file to send (MB, 1–100; the server may allow less)", size)),
            L("CALLS AND LIVE SHARING"),
            Check("Voice and video calls", _s.ChatAllowCalls, v => _s.ChatAllowCalls = v),
            Check("Live session sharing: share a terminal, Remote Desktop or VNC tab into a room (a call that shows only that tab's window; asked every time)", _s.ChatAllowSessionShare, v => _s.ChatAllowSessionShare = v),
            L("CHAT PASSPHRASE"),
            Check("The chat passphrase may be saved on this PC (ticked at the unlock; encrypted, only while the app opens with Windows Hello or a master password; a locked chat then unlocks with Windows Hello)", _s.ChatAllowRemember, v => _s.ChatAllowRemember = v),
            L("SHARING FROM THE APP"),
            Check("“Send to a chat” from the app's screens (screenshots, output, notes — always shown before it's sent)", _s.ChatShareFromScreens, v => _s.ChatShareFromScreens = v),
            L("ADMINISTRATION"),
            Check("CipherChat server administration (separate sign-in, memory only)", _s.ChatAdmin, v => _s.ChatAdmin = v));
    }

    /// <summary>How chats tell you about messages and calls, and when they lock themselves.</summary>
    private Border ChatNotifyCard()
    {
        var mins = new TextBox { Text = S.ChatAutoLockMinutes.ToString(), Width = 70 };
        SettingTag.Set(mins, nameof(AppSettings.ChatAutoLockMinutes));
        mins.LostFocus += (_, _) =>
        {
            if (int.TryParse(mins.Text, out var m) && m is >= 0 and <= 1440) { if (S.ChatAutoLockMinutes != m) { S.ChatAutoLockMinutes = m; Save(); } }
            else mins.Text = S.ChatAutoLockMinutes.ToString();
        };
        return Card("Chat notifications & locking",
            "A notification never contains a message unless you allow it below. Locking wipes the chat keys from memory; the PC stays paired and the next unlock asks for your chat passphrase.",
            Check("Windows notifications for new messages (when their room isn't in front)", _s.ChatNotify, v => _s.ChatNotify = v),
            Check("Show message text in notifications (off: only who wrote and where)", _s.ChatToastPreviews, v => _s.ChatToastPreviews = v),
            Check("Ringtone for incoming calls (the ring window and the taskbar flash show either way)", _s.ChatRingSound, v => _s.ChatRingSound = v),
            Row(Labeled("Lock every chat after this many minutes without using the PC (0 = only with the app lock and at exit)", mins)));
    }

    /// <summary>Restarts the app so the CipherChat setting applies.</summary>
    private static void RestartForChat()
    {
        if (!Dialogs.Confirm("Restart now", "Restart the app now so the CipherChat setting applies? Open terminals and sessions close; the workspace reopens them disconnected.", "Restart")) return;
        App.RestartRequested = true;
        MainWindow.Instance.Close();
        if (MainWindow.Instance.IsVisible) App.RestartRequested = false;
    }
}
