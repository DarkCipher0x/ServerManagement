using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using DcxManager.Services;
using DcxManager.Sessions;
using DcxManager.Ssh;
using DcxManager.Vultr;
using Microsoft.Win32;

namespace DcxManager.Views;

// SettingsView: the Terminal & connections, Screenshots and Terminal profiles cards (moved unchanged from SettingsView.cs in 0.2.0 phase 6½).
public sealed partial class SettingsView
{
    // ---- terminal ----------------------------------------------------------------------------------------------------

    private Border Terminal()
    {
        var scroll = Bound(new ComboBox { Width = 100, ItemsSource = new[] { 1000, 5000, 10000, 50000, 100000 }, SelectedItem = _s.Scrollback }, nameof(AppSettings.Scrollback));
        var user = Bound(new TextBox { Text = _s.DefaultUsername, Width = 140, Padding = new Thickness(5, 4, 5, 4) }, nameof(AppSettings.DefaultUsername));
        var timeout = Bound(new ComboBox { Width = 80, ItemsSource = new[] { 5, 10, 15, 30, 60 }, SelectedItem = _s.ConnectTimeoutSeconds }, nameof(AppSettings.ConnectTimeoutSeconds));
        var save = Btn("Save", () =>
        {
            if (scroll.SelectedItem is int s) _s.Scrollback = s;
            if (timeout.SelectedItem is int t) _s.ConnectTimeoutSeconds = t;
            _s.DefaultUsername = user.Text.Trim().Length > 0 ? user.Text.Trim() : "root";
            Save();
        }, "PrimaryButton");
        var rightClick = Bound(new ComboBox { Width = 260, ItemsSource = new[] { "Menu (Shift+right-click pastes)", "Paste, PuTTY style (Shift+right-click = menu)" }, SelectedIndex = _s.TerminalRightClick == "paste" ? 1 : 0 }, nameof(AppSettings.TerminalRightClick));
        rightClick.SelectionChanged += (_, _) => { _s.TerminalRightClick = rightClick.SelectedIndex == 1 ? "paste" : "menu"; Save(); };
        var maxBack = Bound(new ComboBox { Width = 80, ItemsSource = new[] { 5, 10, 30, 60, 120, 300 }, SelectedItem = _s.AutoReconnectMaxSeconds }, nameof(AppSettings.AutoReconnectMaxSeconds));
        maxBack.SelectionChanged += (_, _) => { if (maxBack.SelectedItem is int m) { _s.AutoReconnectMaxSeconds = m; Save(); } };
        // 0.4.0: session logs for every terminal — the profile decides (default), always, or never; a policy can force it.
        var slog = new ComboBox { Width = 330, ItemsSource = new[] { "Each terminal profile decides (default)", "Log every session (encrypted, whatever the profile says)", "Never log sessions" }, SelectedIndex = _s.SessionLogs switch { "on" => 1, "off" => 2, _ => 0 },
            ToolTip = "Session logs are written encrypted into the store (Logs → Session logs). Group Policy “Session logs” sets this for the whole organisation; Integrations → Syslog can forward them to a collector." };
        SetSetting(slog, nameof(AppSettings.SessionLogs));
        // 0.5.0: admin-protected (ADR-059): the change goes through the admin gate; a refusal puts the choice back.
        bool reverting = false;
        slog.SelectionChanged += async (_, _) =>
        {
            if (reverting) return;
            var want = slog.SelectedIndex switch { 1 => "on", 2 => "off", _ => "profile" };
            if (want == _s.SessionLogs) return;
            bool ok = false;
            try { ok = await ProtectedSettings.ChangeAsync("change terminal session logging", "terminal.sessionLogs", want); }
            catch (Exception ex) { Dialogs.Error("Session logs", ex.Message); }
            if (!ok) { reverting = true; slog.SelectedIndex = _s.SessionLogs switch { "on" => 1, "off" => 2, _ => 0 }; reverting = false; }
        };
        return Card("Terminal & connections", "Applies to new terminal tabs (right-click mode applies immediately). Terminal font and size: Appearance → Typography.",
            Row(Labeled("SCROLLBACK LINES", scroll), Labeled("DEFAULT USERNAME", user), Labeled("CONNECT TIMEOUT (s)", timeout)),
            save,
            Row(Labeled("RIGHT-CLICK IN TERMINAL", rightClick), Labeled("RECONNECT BACK-OFF CAP (s)", maxBack)),
            Labeled("SESSION LOGS (new terminal tabs)", slog),
            Check("Confirm multi-line and risky pastes (shows the lines first; hidden control characters are always removed)", _s.TerminalConfirmPaste, v => _s.TerminalConfirmPaste = v),
            Check("Reconnect terminals automatically after an unexpected drop", _s.TerminalAutoReconnect, v => _s.TerminalAutoReconnect = v),
            Check("Switch to an open tab of the same host instead of opening another (Shift-click, or right-click → New connection, still opens another)", _s.ReuseOpenTabs, v => _s.ReuseOpenTabs = v),
            Check("Keep hosts in step with their cloud servers: a changed IP address, or a server rebuilt under the same name, updates the saved host; removed servers are offered for removal", _s.CloudSyncHosts, v => _s.CloudSyncHosts = v),
            TelnetCheck(),
            Check(Rdp.RdpAxHost.AvailableClsid() != null
                    ? "Open Remote Desktop inside the app (tabs, pop-out, full screen) — off: Windows' Remote Desktop Connection window"
                    : "Open Remote Desktop inside the app — the Windows Remote Desktop control isn't available on this PC, so mstsc is used",
                _s.RdpEmbedded, v => _s.RdpEmbedded = v));
    }

    /// <summary>Saved sessions (0.6.29): what exiting does with open sessions, and whether saved ones reconnect.</summary>
    private Border SavedSessionsCard()
    {
        string[] modes = { "ask", "save", "never" };
        var onExit = Bound(new ComboBox
        {
            Width = 330, SelectedIndex = Math.Max(0, Array.IndexOf(modes, _s.SessionsOnExit)),
            ItemsSource = new[] { "Ask: Save and exit, or Exit without saving", "Always save them (no question)", "Never save them" },
        }, nameof(AppSettings.SessionsOnExit));
        onExit.SelectionChanged += (_, _) => { _s.SessionsOnExit = modes[Math.Max(0, onExit.SelectedIndex)]; Save(); };
        return Card("Saved sessions", "Your open tabs can come back at the next start: terminals (in their folder, or re-attached to their tmux session), Files, File commander, Remote Desktop, VNC, Manage and portal tabs, in the same panes, windows and groups. Quick-connect sessions are kept with their settings, never a password. Named workspaces: ▦ Workspaces in the title bar. Everything is encrypted in the store.",
            Labeled("WHEN YOU EXIT WITH TABS OPEN", onExit),
            Check("Reconnect saved sessions as they reopen (PRODUCTION hosts always reopen disconnected; locked hosts never reopen by themselves)", _s.RestoreReconnect, v => _s.RestoreReconnect = v));
    }

    /// <summary>Screenshots and crash recovery: where files land and what reopens after a crash (moved out of the terminal
    /// card in 0.1.11; they apply to RDP and every tab, not only terminals).</summary>
    private Border ScreenshotsCard()
    {
        var shotDir = new TextBox { Text = Screenshot.Dir, Width = 340, Padding = new Thickness(5, 4, 5, 4), IsReadOnly = true, ToolTip = "Screenshots of terminals, Remote Desktop windows and tabs land here as plain PNG files (Ctrl+Shift+S, or right-click → Screenshot)." };
        var shotMode = Bound(new ComboBox { Width = 200, ItemsSource = new[] { "Save and copy to clipboard", "Save to folder only", "Copy to clipboard only" }, SelectedIndex = _s.ScreenshotDefault switch { "save" => 1, "clipboard" => 2, _ => 0 } }, nameof(AppSettings.ScreenshotDefault));
        shotMode.SelectionChanged += (_, _) => { _s.ScreenshotDefault = shotMode.SelectedIndex switch { 1 => "save", 2 => "clipboard", _ => "both" }; Save(); };
        return Card("Screenshots & recovery", "Screenshots are plain PNG files outside the encrypted store. Crash recovery reopens tabs disconnected and never reconnects or runs anything by itself.",
            Row(Labeled("SCREENSHOTS FOLDER (plain PNG)", shotDir),
                Btn("Change…", () =>
                {
                    var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Screenshots folder", InitialDirectory = Directory.Exists(Screenshot.Dir) ? Screenshot.Dir : null };
                    if (dlg.ShowDialog() == true && StorageGuard.ConfirmDestination(dlg.FolderName, "Screenshots folder", encrypted: false))
                    { _s.ScreenshotDir = dlg.FolderName; Save(); shotDir.Text = Screenshot.Dir; _refreshData?.Invoke(); }
                }),
                Btn("Default", () => { _s.ScreenshotDir = ""; Save(); shotDir.Text = Screenshot.Dir; }),
                Btn("Open", Screenshot.OpenFolder),
                Labeled("CTRL+SHIFT+S DOES", shotMode)),
            Check("After a crash, offer to reopen the tabs that were open", _s.RestoreWorkspace, v => _s.RestoreWorkspace = v));
    }

    internal StackPanel Labeled(string label, FrameworkElement e) { var p = new StackPanel { Tag = "setting" }; p.Children.Add(L(label)); p.Children.Add(e); return p; }

    /// <summary>Telnet is unencrypted: turning it on asks first (ADR-019).</summary>
    private CheckBox TelnetCheck()
    {
        var c = new CheckBox { Content = new TextBlock { Text = "Allow Telnet for legacy devices (UNENCRYPTED: passwords and everything typed cross the network readable) — adds Telnet to the host editor and “telnet host” to quick connect", TextWrapping = TextWrapping.Wrap }, IsChecked = S.TelnetEnabled, Margin = new Thickness(0, 8, 0, 0) };
        SettingTag.Set(c, nameof(AppSettings.TelnetEnabled));
        c.Click += async (_, _) =>
        {
            bool want = c.IsChecked == true;
            c.IsChecked = S.TelnetEnabled;
            if (want && !Dialogs.Confirm("Allow Telnet", "Telnet sends everything — your login, password and every command — unencrypted. Anyone on the network path can read it.\n\nUse it only for legacy devices that have no SSH, ideally on an isolated management network.", "Allow Telnet", danger: true))
                return;
            // Admin-protected (0.3.0 §1): switching it off applies at once; allowing it needs admin approval.
            c.IsEnabled = false;
            try
            {
                if (await ProtectedSettings.ChangeAsync(want ? "allow Telnet" : "switch Telnet off", "net.telnet", want))
                    AuditLog.Write("you", "settings", want ? "Telnet allowed" : "Telnet switched off");
            }
            finally { c.IsEnabled = true; c.IsChecked = S.TelnetEnabled; }
        };
        return c;
    }

    // ---- terminal profiles (ADR-019) ----------------------------------------------------------------------------------

    private Border TerminalProfilesCard()
    {
        var pick = new ComboBox { Width = 220, ToolTip = "The profile to edit" };
        var name = new TextBox { Width = 200, Padding = new Thickness(5, 4, 5, 4) };
        var scheme = new ComboBox { Width = 180 };
        scheme.Items.Add(new ComboBoxItem { Content = "App theme (follows light/dark)", Tag = "app" });
        foreach (var kv in TerminalProfiles.Schemes) scheme.Items.Add(new ComboBoxItem { Content = kv.Value.Title, Tag = kv.Key });
        var cursor = new ComboBox { Width = 120, ItemsSource = TerminalProfiles.CursorStyles };
        var blink = new CheckBox { Content = "Blinking cursor", VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 6) };
        var enc = new ComboBox { Width = 200, ToolTip = "SSH and Telnet output and input. Local shells are always UTF-8; serial consoles have their own setting." };
        foreach (var (id, title) in TerminalProfiles.Encodings) enc.Items.Add(new ComboBoxItem { Content = title, Tag = id });
        var scroll = new TextBox { Width = 90, Padding = new Thickness(5, 4, 5, 4), ToolTip = "0 = the SCROLLBACK LINES setting above; 1,000–200,000" };
        var bs = new CheckBox { Content = "Backspace sends ^H (some network gear and old Unix)", Margin = new Thickness(0, 6, 0, 0) };
        var log = new CheckBox { Content = "Log every session that uses this profile (encrypted; Sidebar → Session logs)", Margin = new Thickness(0, 6, 0, 0) };
        var stamps = new CheckBox { Content = "Timestamp each logged line", Margin = new Thickness(0, 6, 0, 0) };
        var keys = new TextBox { AcceptsReturn = true, Height = 90, MinWidth = 460, Padding = new Thickness(5, 4, 5, 4), VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            ToolTip = "One per line: keys = text. Escapes: \\e (Esc) \\r \\n \\t \\xHH \\uHHHH ^C. Plain letters need Ctrl or Alt. Example: Ctrl+Shift+F5 = \\e[15;6~" }.Mono();
        var keyStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) }.Fs(FontStep.Small);
        keyStatus.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var isDefault = new TextBlock { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 6) };
        bool loading = false;
        TerminalProfile Cur() => pick.SelectedItem as TerminalProfile ?? TerminalProfiles.Default;

        void Fill(TerminalProfile? select = null)
        {
            loading = true;
            pick.ItemsSource = null;
            pick.ItemsSource = TerminalProfiles.All;
            pick.DisplayMemberPath = nameof(TerminalProfile.Name);
            pick.SelectedItem = select ?? TerminalProfiles.Default;
            loading = false;
            Show();
        }
        void Show()
        {
            var p = Cur();
            loading = true;
            name.Text = p.Name;
            scheme.SelectedItem = scheme.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == p.Scheme) ?? scheme.Items[0];
            cursor.SelectedItem = TerminalProfiles.CursorStyles.Contains(p.CursorStyle) ? p.CursorStyle : "block";
            blink.IsChecked = p.CursorBlink;
            enc.SelectedItem = enc.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == p.Encoding) ?? enc.Items[0];
            scroll.Text = p.Scrollback.ToString();
            bs.IsChecked = p.BackspaceCtrlH; log.IsChecked = p.Log; stamps.IsChecked = p.LogTimestamps;
            keys.Text = TerminalProfiles.KeyLines(p.Keys);
            keyStatus.Text = p.Keys.Count == 0 ? "No key mappings." : $"{p.Keys.Count} key mapping(s).";
            isDefault.Text = p == TerminalProfiles.Default ? "★ default profile" : "";
            loading = false;
        }
        // Saves and re-applies to open tabs that use this profile (a tab's per-session choice stays per session).
        void Changed(Action<TerminalProfile> set)
        {
            if (loading) return;
            var p = Cur();
            set(p);
            Save();
            foreach (var tv in MainWindow.Instance.Panes.AllTabs.Select(t => t.Content).OfType<TerminalView>().Where(t => t.Profile.Id == p.Id)) tv.ApplyProfile(p, tv.SessionProfile);
        }
        pick.SelectionChanged += (_, _) => { if (!loading) Show(); };
        name.LostFocus += (_, _) => { var n = name.Text.Trim(); if (n.Length > 0 && n != Cur().Name) { Changed(p => p.Name = n); Fill(Cur()); } };
        scheme.SelectionChanged += (_, _) => Changed(p => p.Scheme = (scheme.SelectedItem as ComboBoxItem)?.Tag as string ?? "app");
        cursor.SelectionChanged += (_, _) => Changed(p => p.CursorStyle = cursor.SelectedItem as string ?? "block");
        blink.Click += (_, _) => Changed(p => p.CursorBlink = blink.IsChecked == true);
        enc.SelectionChanged += (_, _) => Changed(p => p.Encoding = (enc.SelectedItem as ComboBoxItem)?.Tag as string ?? "utf-8");
        scroll.LostFocus += (_, _) =>
        {
            if (!int.TryParse(scroll.Text.Trim(), out var n) || n != 0 && (n < 1000 || n > 200_000)) { scroll.Text = Cur().Scrollback.ToString(); MainWindow.Instance.Status("Scrollback: 0 (use the setting above) or 1,000–200,000 lines."); return; }
            Changed(p => p.Scrollback = n);
        };
        bs.Click += (_, _) => Changed(p => p.BackspaceCtrlH = bs.IsChecked == true);
        log.Click += (_, _) => Changed(p => p.Log = log.IsChecked == true);
        stamps.Click += (_, _) => Changed(p => p.LogTimestamps = stamps.IsChecked == true);

        Fill();
        return Card("Terminal profiles", "Colour scheme, cursor, encoding, scrollback, key mappings and logging for terminal tabs. A host can use its own profile (host editor → TERMINAL PROFILE); any tab can switch for its session (right-click → Profile).",
            Row(Labeled("PROFILE", pick),
                Btn("＋ New", () => { var p = new TerminalProfile { Name = "Profile " + (TerminalProfiles.All.Count + 1) }; TerminalProfiles.All.Add(p); Save(); Fill(p); }),
                Btn("Duplicate", () => { var p = Cur().Copy(Cur().Name + " (copy)"); TerminalProfiles.All.Add(p); Save(); Fill(p); }),
                Btn("★ Make default", () => { S.DefaultTerminalProfileId = Cur().Id; Save(); Show(); }),
                Btn("✕ Delete", () =>
                {
                    var p = Cur();
                    if (TerminalProfiles.All.Count == 1) { Dialogs.Info("Terminal profiles", "The last profile can't be deleted."); return; }
                    int users = AppData.Current.Hosts.Count(h => h.TerminalProfileId == p.Id);
                    if (!Dialogs.Confirm("Delete profile", $"Delete “{p.Name}”?{(users > 0 ? $" {users} host(s) use it and will use the default instead." : "")}", "Delete", danger: true)) return;
                    TerminalProfiles.All.Remove(p);
                    foreach (var h in AppData.Current.Hosts.Where(h => h.TerminalProfileId == p.Id)) h.TerminalProfileId = null;
                    if (S.DefaultTerminalProfileId == p.Id) S.DefaultTerminalProfileId = "";
                    try { AppData.Save(); } catch (Exception ex) { App.Log("Save hosts", ex); }
                    Save(); Fill();
                }, "DangerButton"),
                isDefault),
            Row(Labeled("NAME", name), Labeled("COLOUR SCHEME", scheme), Labeled("CURSOR", cursor), blink),
            Row(Labeled("ENCODING", enc), Labeled("SCROLLBACK (0 = default)", scroll)),
            bs, log, stamps,
            L("KEY MAPPINGS (keys = text to send)"),
            keys,
            Row(Btn("Apply key mappings", () =>
            {
                var (maps, errors) = TerminalProfiles.ParseKeyLines(keys.Text);
                if (errors.Count > 0) { keyStatus.Text = "✕ " + string.Join(" · ", errors); return; }
                Changed(p => p.Keys = maps);
                keyStatus.Text = $"✓ {maps.Count} key mapping(s) saved and applied to open tabs using this profile.";
            }), keyStatus));
    }
}
