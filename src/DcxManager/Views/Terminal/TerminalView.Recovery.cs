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

// TerminalView: renderer recovery (ADR-021): a new display for the same session (moved unchanged from TerminalView.cs in 0.2.0 phase 6½).
public sealed partial class TerminalView
{
    // ---- renderer recovery (ADR-021): a new display, the same session ---------------------------------------------------

    private readonly Border _webBorder = new() { BorderThickness = new Thickness(0, 1, 0, 0) };
    private readonly string _tid = Guid.NewGuid().ToString("N");
    private readonly List<DateTime> _recoveries = new();
    private bool _recovering, _replay;
    private string _recoveryReason = "";
    /// <summary>Output already sent to the page (bounded), replayed into a recovered display.</summary>
    private readonly StringBuilder _history = new();
    private const int MaxHistoryChars = 512_000;
    public const int MaxRecoveries = 3;
    public static readonly TimeSpan RecoveryWindow = TimeSpan.FromMinutes(10);

    /// <summary>Recoveries done by this tab (self-test).</summary>
    internal int Recoveries { get; private set; }
    /// <summary>Identifies this tab's page (the self-test finds its renderer process by it).</summary>
    internal string PageId => _tid;
    internal string StatusText => _status.Text;
    internal System.Drawing.Color WebBackgroundForTest => _web.DefaultBackgroundColor;

    private void OnProcessFailed(CoreWebView2ProcessFailedKind kind, CoreWebView2Environment env, WebView2 web)
    {
        if (_disposed || web != _web) return;   // an older control's late event
        switch (kind)
        {
            case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                WebHost.ResetEnvironment(env);
                _ = RecoverAsync("the browser process exited");
                break;
            case CoreWebView2ProcessFailedKind.RenderProcessExited:
                _ = RecoverAsync("the display process exited");
                break;
            case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
                _ = RecoverAsync("the display stopped responding");
                break;
            // Frame, GPU and utility processes: the runtime restarts them itself; logged above.
        }
    }

    /// <summary>Replaces the WebView2 control and reloads the page; the transport is untouched, so the session goes on and
    /// the recent output is replayed. At most <see cref="MaxRecoveries"/> per <see cref="RecoveryWindow"/>.</summary>
    internal async Task<bool> RecoverAsync(string why)
    {
        if (_disposed || _recovering) return false;
        var now = DateTime.UtcNow;
        _recoveries.RemoveAll(t => now - t > RecoveryWindow);
        if (_recoveries.Count >= MaxRecoveries)
        {
            SetStatus($"✕ {_target.Endpoint} · the terminal display keeps failing ({_recoveries.Count}× in {RecoveryWindow.TotalMinutes:0} min) — reopen this tab"
                      + (IsConnected ? "; the session itself is still connected" : ""));
            return false;
        }
        _recoveries.Add(now);
        _recovering = true;
        try
        {
            Recoveries++;
            _recoveryReason = why;
            App.Log("Terminal display recovery", new Exception($"{_target.Kind} tab: {why} — recreating the display ({_recoveries.Count}/{MaxRecoveries} in {RecoveryWindow.TotalMinutes:0} min)"));
            var old = _web;
            if (old.CoreWebView2 != null) { try { old.CoreWebView2.WebMessageReceived -= OnMessage; old.CoreWebView2.ContextMenuRequested -= OnContextMenu; } catch { } }
            _pageReady = false;
            _webBorder.Child = null;
            try { old.Dispose(); } catch (Exception ex) { App.Log("Dispose failed terminal view", ex); }
            _web = new WebView2();
            _webBorder.Child = _web;
            _replay = true;
            SetStatus($"{_target.Endpoint} · restarting the terminal display ({why})…");
            await InitWebAsync();
            return true;
        }
        finally { _recovering = false; }
    }

    /// <summary>Called when the page is ready again after a recovery: the recent output, then a note.</summary>
    private void ReplayHistory()
    {
        _replay = false;
        string text;
        lock (_history) text = _history.ToString();
        if (text.Length > 0) Post(new { t = "out", d = text });
        Post(new { t = "out", d = $"\r\n\x1b[33m⟫ The terminal display was restarted ({_recoveryReason}). The session itself was not interrupted.\x1b[0m\r\n" });
        SetStatus($"{(IsConnected ? "● " : "○ ")}{_target.Endpoint} · display restarted after: {_recoveryReason}");
        Post(new { t = "focus" });
    }

    private void Remember(string text)
    {
        lock (_history)
        {
            _history.Append(text);
            // Trimmed in steps (at twice the size, back to the limit), not on every flush: cheap under an output flood.
            if (_history.Length <= MaxHistoryChars * 2) return;
            // Keep the tail, starting at a line break so no escape sequence is cut in half.
            int cut = _history.Length - MaxHistoryChars;
            var s = _history.ToString();
            int nl = s.IndexOf('\n', cut);
            _history.Remove(0, nl >= 0 && nl - cut < 4096 ? nl + 1 : cut);
        }
    }

    /// <summary>A hyperlink in terminal output (OSC 8) was clicked. The server chose it, so it is untrusted: only http(s),
    /// shown in full, opened in the default browser after an explicit yes. Never navigates inside the app.</summary>
    private void OpenLink(string? uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps)) { SetStatus("Ignored a non-web link in terminal output."); return; }
        // Offline mode (ADR-016) is checked before asking.
        Offline.Run(u.AbsoluteUri, "a link from terminal output", () =>
        {
            if (!Dialogs.Confirm("Open link from terminal output?", $"Output from {_target.Title} contains a link to:\n\n{u.AbsoluteUri}\n\nLinks in terminal output come from the server and can point anywhere. Open it in your default browser?", "Open in browser", danger: true)) return;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true }); } catch (Exception ex) { App.Log("Terminal link", ex); }
        });
    }

    /// <summary>Settings → Appearance changed the terminal font family (size stays per tab).</summary>
    private void OnFonts() => Post(new { t = "font", family = Typography.TerminalFamily });

    private void OnTheme()
    {
        string C(string k) => ThemeManager.HexString(k);
        WebHost.Paint(_web, "Input");
        Post(new { t = "theme", bg = C("Input"), fg = C("Fg"), accent = C("Accent"), sel = C("Selection"), thumb = C("ScrollThumb") });
    }

    // Every branch is guarded so a malformed message or a race during teardown can never escape to crash the app.
    private async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_disposed) return;
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var m = doc.RootElement;
            if (!m.TryGetProperty("t", out var tp)) return;
            switch (tp.GetString())
            {
                case "ready":
                    _pageReady = true;
                    Post(TerminalProfiles.PageMessage(_profile));
                    if (_replay) { ReplayHistory(); break; }
                    if (!_connectOnOpen && !_sized) SetStatus($"○ {_target.Endpoint} · press Enter to connect");
                    break;
                case "findbar":
                    ShowFind(true);
                    break;
                case "found":
                {
                    var id = m.TryGetProperty("id", out var fid) ? fid.GetInt32() : 0;
                    var r = new FindResult(m.GetProperty("i").GetInt32(), m.GetProperty("n").GetInt32(), m.TryGetProperty("err", out var fe) && fe.ValueKind == JsonValueKind.String ? fe.GetString() : null);
                    _findCount.Text = r.Error != null ? "✕ " + r.Error : _lastQuery.Length == 0 ? "" : r.Count == 0 ? "no matches" : $"{r.Index} of {r.Count}" + (r.Count >= 10000 ? "+" : "");
                    if (_waits.Remove(id, out var w)) w.TrySetResult(r);
                    break;
                }
                case "probe":
                {
                    var id = m.TryGetProperty("id", out var pid) ? pid.GetInt32() : 0;
                    if (_waits.Remove(id, out var w)) w.TrySetResult(m.Clone());
                    break;
                }
                case "resize":
                    _cols = (uint)Math.Max(10, m.GetProperty("cols").GetInt32());
                    _rows = (uint)Math.Max(4, m.GetProperty("rows").GetInt32());
                    try { _transport?.Resize(_cols, _rows); } catch { }
                    if (!_sized) { _sized = true; if (_connectOnOpen) await ConnectAsync(); }
                    break;
                case "in":
                    await OnInputAsync(m.GetProperty("d").GetString() ?? "");
                    break;
                case "copy":
                    var text = m.GetProperty("d").GetString();
                    if (!string.IsNullOrEmpty(text)) { try { Clipboard.SetText(text); } catch { } }
                    break;
                case "paste":
                    PasteClipboard();
                    break;
                case "sel":
                    _selection = m.GetProperty("d").GetString() ?? "";
                    break;
                case "link":
                    OpenLink(m.GetProperty("d").GetString());
                    break;
                case "menu":
                {
                    var sel = m.TryGetProperty("d", out var sd) ? sd.GetString() ?? "" : "";
                    bool shift = m.TryGetProperty("shift", out var sh) && sh.GetBoolean();
                    bool menuMode = AppSettings.Current.TerminalRightClick != "paste";
                    if (menuMode != shift) ShowMenu(sel);
                    else if (sel.Length > 0) { try { Clipboard.SetText(sel); } catch { } Post(new { t = "clearsel" }); }
                    else { PasteClipboard(); }
                    break;
                }
                case "screen":
                {
                    var id = m.TryGetProperty("id", out var idp) ? idp.GetInt32() : 0;
                    var lines = m.GetProperty("lines").EnumerateArray().Select(x => x.GetString() ?? "").Take(500).ToArray();
                    var cursor = m.TryGetProperty("cursor", out var cp) ? cp.GetInt32() : -1;
                    if (_screenWaits.Remove(id, out var tcs)) tcs.TrySetResult(new Api.ScreenText(lines, cursor));
                    break;
                }
                case "buffer":
                {
                    var buf = m.GetProperty("d").GetString() ?? "";
                    var purpose = m.TryGetProperty("purpose", out var pp) ? pp.GetString() : "";
                    if (purpose == "copy") Menus.Clip(buf);
                    else if (purpose == "save") Menus.SaveText(buf, $"{FileSafe(_target.Title)}-{DateTime.Now:yyyyMMdd-HHmm}.log", "Log|*.log;*.txt|All files|*.*");
                    else if (purpose == "digest") CommandText.ShowDigest(_target.Title, buf);
                    else if (purpose == "share") ShareService.ShareText(Window.GetWindow(this), $"Terminal — {_target.Title}", buf, $"{FileSafe(_target.Title)}-{DateTime.Now:yyyyMMdd-HHmm}.log");
                    else if (purpose == "ai") { Assist.Current?.AskOperator($"Here is the recent terminal output from {_target.Endpoint}. Summarise what happened and flag anything that needs action:\n```\n{(buf.Length > 8000 ? buf[^8000..] : buf)}\n```"); }
                    break;
                }
            }
        }
        catch (Exception ex) { App.Log("Terminal message", ex); }
    }

    public async Task ConnectAsync()
    {
        if (_connecting || _disposed) return;
        _connecting = true;
        _userClosed = false;
        _reconnectCts?.Cancel();     // a manual connect cancels any pending auto-reconnect
        Teardown();
        bool remote = _target.Host != null;
        WriteLocal(ResetInputModes + $"\x1b[36m⟫ {(remote ? "connecting to" : "starting")} {_target.Endpoint}…\x1b[0m\r\n");
        SetStatus($"{_target.Endpoint} · {(remote ? "connecting" : "starting")}…");
        ITerminalTransport? transport = null;
        try
        {
            transport = _target.CreateTransport();
            lock (_decodeGate) _decoder = RemoteEncoding.GetDecoder();
            _bracketed = false;   // a new shell announces its own paste mode
            transport.Data += OnBytes;
            transport.Closed += reason => Dispatcher.BeginInvoke(() => { if (_transport == transport) OnDrop(reason); });
            await transport.OpenAsync(_cols, _rows);
            if (_disposed) { transport.Dispose(); return; }
            _transport = transport;
            Epoch++;
            _attempt = 0;
            _flush.Start();
            // A program that ended during start-up raised Closed before it was the current transport.
            if (!transport.IsOpen) { OnDrop("the session ended during start-up"); return; }
            SetStatus($"● {_target.Endpoint}" + (transport.Detail.Length > 0 ? " · " + transport.Detail : ""));
            LiveSessions.Touch();
            if (_logOn) StartLog();
            AuditLog.Write("you", _target.Title, _target.Kind == "telnet" ? "opened Telnet session (unencrypted)" : remote ? "opened terminal session" : $"opened {_target.Kind} shell");
            if (_target.Host is { } ih) Incidents.IncidentStore.RecordConnection(ih, "Terminal session");
            Post(new { t = "focus" });
            if (!string.IsNullOrEmpty(_initialCommand))
            {
                await Task.Delay(400);
                if (IsConnected) _transport?.Write(_initialCommand + "\r");
            }
        }
        catch (Exception ex)
        {
            if (transport != null) { transport.Data -= OnBytes; transport.Dispose(); }
            bool willRetry = _target.Has(SessionCaps.AutoReconnect) && AppSettings.Current.TerminalAutoReconnect && !_userClosed && !_disposed && _target.ShouldRetry(ex);
            bool refused = Sessions.SessionTarget.IsAuthFailure(ex);
            WriteLocal($"\x1b[31m✕ {ex.Message.Replace("\n", "\r\n")}\x1b[0m\r\n\x1b[90m{(willRetry ? "Retrying automatically…" : refused ? "Not retrying automatically: the server refused the sign-in. Fix the credential, then press Enter to retry." : "Press Enter to retry.")}\x1b[0m\r\n");
            SetStatus($"✕ {_target.Endpoint} · {ex.Message.Split('\n')[0]}");
            // A refused sign-in is logged once (it used to be retried, and logged, every 30 s all night: s37).
            if (willRetry || (refused && _attempt <= 1)) App.Log($"{_target.Kind} connect " + (_target.Host?.Address ?? _target.Title), ex);
            _connecting = false;
            if (willRetry) MaybeReconnect();
            return;
        }
        finally { _connecting = false; }
    }
}
