using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DcxManager.Services;
using DcxManager.Ssh;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using DcxManager.Sessions;

namespace DcxManager.Views;

// TerminalView: connect, the page bridge, input, resize, close and dispose (moved unchanged from TerminalView.cs in 0.2.0 phase 6½).
public sealed partial class TerminalView
{
    // ---- lifecycle -----------------------------------------------------------------------------------------------

    /// <summary>Tears down the live shell/session only (keeps the view and output). Safe to call repeatedly.</summary>
    private void Teardown()
    {
        var t = _transport;
        _transport = null;
        if (t != null) { t.Data -= OnBytes; try { t.Dispose(); } catch { } LiveSessions.Touch(); }
    }

    private void UserDisconnect()
    {
        _userClosed = true;
        _reconnectCts?.Cancel();
        bool wasLive = _transport != null;
        Teardown();
        if (wasLive) WriteLocal($"\r\n\x1b[33m⟫ {(_target.Host != null ? "disconnected" : "shell closed")}. Press Enter or ↻ Reconnect.\x1b[0m\r\n");
        SetStatus($"○ {_target.Endpoint} · {(_target.Host != null ? "disconnected" : "closed")}");
    }

    public void Disconnect() => UserDisconnect();

    /// <summary>The terminal as drawn (xterm canvas), captured by the browser itself.</summary>
    public async Task<System.Windows.Media.Imaging.BitmapSource> CaptureAsync()
    {
        if (_disposed || _web.CoreWebView2 == null || !_pageReady) throw new InvalidOperationException("The terminal isn't ready yet.");
        return await Screenshot.CaptureWebAsync(_web.CoreWebView2);
    }

    /// <summary>Writes straight to the shell (used by the menus and the Snippets "send to terminal" action).</summary>
    public void SendRaw(string s) => SendInput(s);

    /// <summary>Keyboard / paste data from the page (the self-test types through here too).</summary>
    internal async Task OnInputAsync(string data)
    {
        LastInput = data;
        WatchInput(data);   // 0.6.29: alerts (a long command)
        if (IsConnected) SendInput(data);
        else if (data == "\r" && !_connecting) await ConnectAsync();
        InputBroadcast.Relay(this, data);   // ADR-019: typed or pasted here → the other members too
    }

    /// <summary>Text for the session, in the profile's encoding where the transport takes bytes (SSH, Telnet).</summary>
    /// <remarks>Writes leave the UI thread (typing latency, 2026-10-19): an SSH write + flush, or a ConPTY pipe the shell
    /// isn't draining, could stall the window on every keystroke. One writer per view keeps the order.</remarks>
    internal void SendInput(string s)
    {
        var tr = _transport;
        if (tr == null || !IsConnected || s.Length == 0) return;
        lock (_writes)
        {
            _writes.Enqueue((tr, s));
            if (_writing) return;
            _writing = true;
        }
        _ = Task.Run(DrainWrites);
    }

    private readonly Queue<(ITerminalTransport Tr, string Text)> _writes = new();
    private bool _writing;

    private void DrainWrites()
    {
        while (true)
        {
            (ITerminalTransport Tr, string Text) w;
            lock (_writes)
            {
                if (_writes.Count == 0) { _writing = false; return; }
                w = _writes.Dequeue();
            }
            if (_disposed || w.Tr != _transport) continue;   // a reconnect replaced the transport: never type into the new one
            try
            {
                var enc = RemoteEncoding;
                if (w.Tr is IByteWriter bw && enc is not UTF8Encoding) bw.WriteBytes(enc.GetBytes(w.Text));
                else w.Tr.Write(w.Text);
            }
            catch (Exception ex)
            {
                var tr = w.Tr;
                try { Dispatcher.BeginInvoke(() => { if (_transport == tr) OnDrop("write failed: " + ex.Message); }); } catch { }
            }
        }
    }

    /// <summary>Self-test: waits until queued input has been written.</summary>
    internal async Task WaitWritesAsync(int ms = 3000)
    {
        for (int i = 0; i < ms / 10; i++) { lock (_writes) if (!_writing && _writes.Count == 0) return; await Task.Delay(10); }
    }

    /// <summary>Output/input encoding: the profile's for SSH and Telnet; local shells are UTF-8 and serial has its own.</summary>
    private Encoding RemoteEncoding => _target.Kind is "ssh" or "telnet" ? TerminalProfiles.EncodingFor(_profile.Encoding) : new UTF8Encoding(false);

    /// <summary>
    /// Right-click inside the browser. WebView2 owns the mouse in its own HWND, so the only reliable hook is this event:
    /// the browser's menu is suppressed and ours is shown while the request is deferred (closing the menu completes it).
    /// </summary>
    private void OnContextMenu(object? sender, CoreWebView2ContextMenuRequestedEventArgs e)
    {
        if (_disposed) return;
        e.Handled = true;
        var sel = _selection;   // xterm draws on a canvas, so the DOM selection is empty; the page reports its own
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        bool menuMode = AppSettings.Current.TerminalRightClick != "paste";
        if (menuMode != shift)
        {
            var deferral = e.GetDeferral();
            try { ShowMenu(sel, deferral); }
            catch (Exception ex) { deferral.Complete(); App.Log("Terminal menu", ex); }
        }
        else if (sel.Length > 0) { try { Clipboard.SetText(sel); } catch { } Post(new { t = "clearsel" }); }
        else { PasteClipboard(); }
    }

    /// <summary>The session's right-click menu (Shift+right-click does the other action — see Settings → Terminal).</summary>
    private void ShowMenu(string sel, CoreWebView2Deferral? deferral = null)
    {
        bool live = IsConnected;
        var assist = Assist.Current;   // no assistant module = no AI items
        bool ai = assist?.WhyNot(AssistUse.Operator) == null;
        var tab = MainWindow.Instance.Panes.AllTabs.FirstOrDefault(t => t.Content == this);
        var menuMode = AppSettings.Current.TerminalRightClick != "paste";
        // Grouped (0.2.1): the everyday actions at the top; the rest in named groups that fold into submenus.
        var menu = Menus.ShowAt(_web, new object?[]
        {
            Menus.Item("Copy", () => { Menus.Clip(sel); Post(new { t = "clearsel" }); }, "Ctrl+Shift+C", sel.Length > 0),
            Menus.Item("Paste", () => { PasteClipboard(); }, "Ctrl+Shift+V", Menus.ClipboardHasText()),
            Menus.Item("Paste selection", () => PasteText(sel), enabled: sel.Length > 0 && live),
            Menus.Item("Select all", () => Post(new { t = "selectall" })),
            Menus.Item("⌕ Find in scrollback…", () => ShowFind(true), "Ctrl+Shift+F"),
            Menus.Group("Selection & notes",
                assist != null ? Menus.Item("✦ Ask AI about the selection", AskAi, enabled: sel.Length > 0 && ai) : null,
                assist != null ? Menus.Item("✦ Summarise recent output with AI", () => Post(new { t = "buffer", purpose = "ai" }), enabled: ai) : null,
                Menus.Item("⌗ Digest selection (offline)", () => CommandText.ShowDigest(_target.Title, sel), enabled: sel.Length > 0),
                Menus.Item("✎ Add selection to notes", () => AddToNotes(sel), enabled: sel.Length > 0, tip: "Appends it as a timestamped code block to this session's log note (encrypted)"),
                Menus.AsyncItem("Share selection to Microsoft Teams…", () => TeamsUi.ShareAsync(Window.GetWindow(this), $"{_target.Title}: terminal output", sel.TrimEnd(), null), enabled: sel.Length > 0),
                Menus.Item("⚑ Add selection to incident", () => IncidentUi.AddText(ItemKindTerminal, $"{_target.Title}: {FirstLine(sel)}", $"{_target.Endpoint}\n```\n{sel.TrimEnd()}\n```", _target.Host),
                    enabled: sel.Length > 0, tip: Incidents.IncidentStore.Active is { } ai2 ? $"Goes to the active incident “{ai2.Title}”" : "Asks which incident (or starts one)"),
                Menus.Item("✎ Notes for this session", () => { if (_target.Host is { IsAdHoc: false } nh) MainWindow.Instance.OpenNotes(nh.Id); else MainWindow.Instance.OpenNotes(note: Notes.NoteStore.All.FirstOrDefault(n => n.SessionKey == NotesKey)); })),
            Menus.Group("Output & input",
                Menus.Sub("Scrollback",
                    Menus.Item("Copy all output", () => Post(new { t = "buffer", purpose = "copy" })),
                    Menus.Item("Save output as…", () => Post(new { t = "buffer", purpose = "save" })),
                    Menus.Item("Share output…", () => Post(new { t = "buffer", purpose = "share" })),
                    Menus.Item("⌗ Digest output into patterns (offline)", () => Post(new { t = "buffer", purpose = "digest" })),
                    Menus.Sep(),
                    Menus.Item("Scroll to top", () => Post(new { t = "top" })),
                    Menus.Item("Scroll to bottom", () => Post(new { t = "bottom" })),
                    Menus.Item("Clear scrollback", () => Post(new { t = "clear" })),
                    Menus.Item("Reset terminal", () => Post(new { t = "reset" }), tip: "Full reset — fixes a garbled screen after binary output")),
                Menus.Sub("Send key",
                    Menus.Item("Ctrl+C  (interrupt)", () => SendRaw("\x03"), enabled: live),
                    Menus.Item("Ctrl+D  (EOF / logout)", () => SendRaw("\x04"), enabled: live),
                    Menus.Item("Ctrl+Z  (suspend)", () => SendRaw("\x1a"), enabled: live),
                    Menus.Item("Ctrl+\\  (quit)", () => SendRaw("\x1c"), enabled: live),
                    Menus.Item("Ctrl+L  (redraw)", () => SendRaw("\x0c"), enabled: live),
                    Menus.Item("Esc", () => SendRaw("\x1b"), enabled: live),
                    Menus.Item("Ctrl+B  (tmux prefix)", () => SendRaw("\x02"), enabled: live),
                    Menus.Item("Ctrl+W", () => SendRaw("\x17"), enabled: live),
                    _transport is ISerialControl ? Menus.Item("BREAK (serial, 300 ms)", SendBreak, enabled: live, tip: "Cisco ROMMON, Juniper and most network gear use BREAK during boot for recovery") : null,
                    _target.Host != null ? Menus.Item("Ctrl+Alt+Del is not sent over SSH — use Reboot in Manage", () => { }, enabled: false) : null),
                Menus.Sub("Snippets",
                    AppData.Current.Snippets.Take(30).Select(s => (object?)Menus.Item(s.Name, () => _ = SendSnippetAsync(s, new[] { this }), enabled: live, tip: s.Command)).ToArray()),
                Menus.Sub("⌨ Broadcast input",
                    Menus.Item("To all open terminals…", () => InputBroadcast.Start(AllTerminals(), "all open terminals")),
                    tab != null && TabGroups.GetName(tab) is { Length: > 0 } g ? Menus.Item($"To the terminals in group “{g}”…", () => InputBroadcast.Start(TabGroups.Members(g).Select(t => t.Content).OfType<TerminalView>(), $"group “{g}”")) : null,
                    tab != null && MainWindow.Instance.Panes.PaneOf(tab) is { } pane ? Menus.Item("To the terminals in this pane…", () => InputBroadcast.Start(pane.Items.OfType<TabItem>().Select(t => t.Content).OfType<TerminalView>(), "this pane")) : null,
                    Menus.Item("■ Stop broadcasting input", () => InputBroadcast.Stop("stopped by you"), enabled: InputBroadcast.Members.Count > 0))),
            Menus.Group("Capture & share",
                Screenshot.Menu(CaptureAsync, _target.Title, Window.GetWindow(this)),
                BroadcastUi.TerminalItem(this),
                AddonUi.TerminalMenu(this)),
            Menus.Group("Session",
                Menus.Item("↻ Reconnect", () => _ = ConnectAsync()),
                Menus.Item("✕ Disconnect", UserDisconnect, enabled: live),
                _target.CanDuplicate ? Menus.Item("⊞ Duplicate session", () => MainWindow.Instance.OpenSession(_target.Clone(), forceNew: true)) : null,
                !_target.Has(SessionCaps.AutoReconnect) ? null : Menus.Item(AppSettings.Current.TerminalAutoReconnect ? "Auto-reconnect: on (turn off)" : "Auto-reconnect: off (turn on)", () => { AppSettings.Current.TerminalAutoReconnect = !AppSettings.Current.TerminalAutoReconnect; try { AppSettings.Save(); } catch { } }),
                !AppSettings.Current.TerminalAlerts ? null : Menus.Item(_alertsMuted ? "🔕 Alerts for this tab: off (turn on)" : "🔔 Alerts for this tab: on (turn off)", () => _alertsMuted = !_alertsMuted,
                    tip: "A long command finishing, a bell, a highlight alert or a drop, while you're looking elsewhere (Settings → Terminal → Alerts)"),
                Menus.Sep(),
                Menus.Item(_log != null ? "✓ Logging this session (turn off)" : "Log this session (encrypted)", () => SetLogging(_log == null), enabled: AppSettings.Current.SessionLogs != "on" || _log == null,
                    tip: AppSettings.Current.SessionLogs == "on" ? "Session logs are on for every session (Settings → Terminal & connections, or Group Policy)" : "Output is saved encrypted in the store's session logs, timestamped per line if the profile says so"),
                Menus.Item("Session logs…", () => MainWindow.Instance.Navigate("sessionlogs")),
                Menus.Sub("Profile (this session)", TerminalProfiles.All.Select(p => (object?)Menus.Item((p.Id == _profile.Id ? "✓ " : "    ") + p.Name, () => ApplyProfile(p, session: true),
                        tip: $"{(TerminalProfiles.Schemes.TryGetValue(p.Scheme, out var sc) ? sc.Title : "App theme")} · {p.CursorStyle} cursor · {p.Encoding}{(p.Log ? " · logging" : "")}"))
                    .Append(Menus.Sep()).Append(Menus.Item("Edit profiles…", () => { MainWindow.Instance.Navigate("settings"); SettingsView.Current?.RevealCard("Terminal profiles"); })).ToArray())),
            _target.Host is not { } host ? null : Menus.Group("Host",
                _target.Has(SessionCaps.HostTools) ? Menus.Item("⇅ Files (SFTP)", () => MainWindow.Instance.OpenFiles(host)) : null,
                _target.Has(SessionCaps.HostTools) ? Menus.Item("▣ Manage", () => MainWindow.Instance.OpenServer(host)) : null,
                Menus.Item("Copy address", () => Menus.Clip(host.Address)),
                Menus.Item("Copy user@host", () => Menus.Clip(host.Endpoint))),
            Menus.Group("View",
                Menus.Item("Larger text", () => SetFont(_fontSize + 1), "A+"),
                Menus.Item("Smaller text", () => SetFont(_fontSize - 1), "A−"),
                Menus.Item("Reset text size", () => SetFont(AppSettings.Current.TerminalFontSize)),
                Menus.Sep(),
                tab == null ? null : Menus.Item("Split right", () => MainWindow.Instance.Panes.Split(tab, Orientation.Horizontal), "Ctrl+Alt+→"),
                tab == null ? null : Menus.Item("Split down", () => MainWindow.Instance.Panes.Split(tab, Orientation.Vertical), "Ctrl+Alt+↓"),
                tab == null ? null : Menus.Item("Tile all tabs", MainWindow.Instance.Panes.Tile, "Ctrl+Alt+T"),
                tab == null ? null : MainWindow.Instance.Panes.IsFloating(tab) ? Menus.Item("Dock back", () => MainWindow.Instance.Panes.DockBack(tab)) : Menus.Item("Pop out to a window", () => MainWindow.Instance.Panes.PopOut(tab), "Ctrl+Alt+O"),
                Menus.Sep(),
                Menus.Item(menuMode ? "Right-click: menu (Shift+right-click pastes) — switch to PuTTY style" : "Right-click: PuTTY copy/paste — switch to menu", () => { AppSettings.Current.TerminalRightClick = menuMode ? "paste" : "menu"; try { AppSettings.Save(); } catch { } })),
        });
        if (deferral != null)
        {
            // In the self-test the menu is recorded, never opened, so it never closes either.
            if (menu == null || TestMode.Running) deferral.Complete();
            else menu.Closed += (_, _) => { try { deferral.Complete(); } catch { } };
        }
    }

    /// <summary>Self-test: builds the right-click menu as a right-click would (recorded by <see cref="Menus"/>, not opened).</summary>
    internal void ShowMenuForTest(string selection) => ShowMenu(selection);

    private void SetFont(int size)
    {
        _fontSize = Math.Clamp(size, 8, 32);
        Post(new { t = "font", size = _fontSize });
    }

    private void AskAi()
    {
        var sel = _selection.Trim();
        if (Assist.Current is not { } assist) return;
        if (sel.Length == 0) { Dialogs.Info("Ask AI", "Select some text in the terminal first (drag with the mouse)."); return; }
        assist.AskOperator($"In {_target.Endpoint}, I got this terminal output. Explain what it means and what to do about it:\n\n```\n{sel}\n```");
    }

    private void SetStatus(string s) { if (!_disposed) _status.Text = s; }

    /// <summary>Notebook key for sessions that have no saved host (local shells, WSL, serial, ad-hoc SSH).</summary>
    private string? NotesKey => _target switch
    {
        LocalTarget l => "local:" + l.Profile.Id,
        SerialTarget s => "serial:" + s.Profile.Id,
        _ => _target.Host is { IsAdHoc: true } a ? "adhoc:" + a.Endpoint : null,
    };

    private const Incidents.ItemKind ItemKindTerminal = Incidents.ItemKind.Terminal;
    private static string FirstLine(string s) { var l = s.Trim().Split('\n')[0].Trim(); return l.Length > 60 ? l[..60] + "…" : l; }

    private void AddToNotes(string sel)
    {
        if (sel.Trim().Length == 0) return;
        try
        {
            var hostId = _target.Host is { IsAdHoc: false } h ? h.Id : null;
            var n = Notes.NoteStore.AppendToLog(_target.Title, hostId, hostId == null ? NotesKey : null, $"{_target.Endpoint}\n```\n{sel.TrimEnd()}\n```");
            SetStatus($"● {_target.Endpoint} · added {sel.Split('\n').Length} line(s) to “{n.Title}” (Notes)");
        }
        catch (Exception ex) { Dialogs.Error("Notes", "Couldn't add to notes: " + ex.Message, explain: false); }
    }

    private void SendBreak()
    {
        if (_transport is not ISerialControl sc || !IsConnected) { SetStatus("Not connected."); return; }
        try { sc.SendBreak(); SetStatus($"● {_target.Endpoint} · BREAK sent"); AuditLog.Write("you", _target.Title, "serial BREAK sent"); }
        catch (Exception ex) { OnDrop("break failed: " + ex.Message); }
    }

    private static string FileSafe(string s) => string.Concat(s.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '-' : c));

    public void Activated()
    {
        Post(new { t = "focus" });
        SessionAlerts.Clear(this);   // 0.6.29: you're looking at it now
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _userClosed = true;
        try { Api.Broadcasts.StopFor(this, "tab closed"); } catch { }
        try { InputBroadcast.Remove(this); } catch { }
        try { var l = _log; _log = null; l?.Dispose(); } catch (Exception ex) { App.Log("Session log close", ex); }
        ThemeManager.Changed -= OnTheme;
        Typography.TerminalChanged -= OnFonts;
        try { _reconnectCts?.Cancel(); } catch { }
        try { _flush.Stop(); _quiet?.Stop(); } catch { }
        AppSettings.Saved -= OnTerminalSettings;
        AppData.Changed -= OnTerminalData;
        if (_web.CoreWebView2 != null) { try { _web.CoreWebView2.WebMessageReceived -= OnMessage; _web.CoreWebView2.ContextMenuRequested -= OnContextMenu; } catch { } }
        Teardown();
        try { _web.Dispose(); } catch { }
    }
}
