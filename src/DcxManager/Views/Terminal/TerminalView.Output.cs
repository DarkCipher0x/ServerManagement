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

// TerminalView: the bounded output pipeline, the hostile-output clamp and paste safety (moved unchanged from TerminalView.cs in 0.2.0 phase 6½).
public sealed partial class TerminalView
{
    // ---- output pipeline (bounded) -------------------------------------------------------------------------------

    /// <summary>Transport output (any thread; one producer per transport). UTF-8 split across reads is handled by the decoder.</summary>
    private void OnBytes(byte[] data, int count)
    {
        try
        {
            lock (_decodeGate)
            {
                var chars = new char[_decoder.GetCharCount(data, 0, count)];
                int n = _decoder.GetChars(data, 0, count, chars, 0);
                if (n > 0)
                {
                    var s = new string(chars, 0, n);
                    Enqueue(s);
                    RequestQuickFlush();
                    _log?.Write(s);   // the whole output, even when the screen throttles
                    OutputText?.Invoke(s);
                }
            }
        }
        catch (Exception ex) { App.Log("Terminal decode", ex); }
    }

    private void Enqueue(string s)
    {
        lock (_chunks)
        {
            _chunks.Enqueue(s);
            _pendingLen += s.Length;
            // Producer outran the UI: drop the oldest backlog rather than grow without bound.
            while (_pendingLen > MaxPendingChars && _chunks.Count > 1)
            {
                var old = _chunks.Dequeue();
                _pendingLen -= old.Length;
                _dropped = true;
            }
        }
    }

    /// <summary>Typing latency (DarkCipher0x, 2026-10-19: "terminal typing is laggy"): the 16 ms timer runs at Background priority,
    /// so under UI load an echo could wait behind layout and other work. Small, interactive output (an echo, a prompt) is
    /// flushed at once at Normal priority instead; one such request is pending at a time, and floods stay on the timer.</summary>
    private int _quickFlush;
    /// <summary>Self-test: off measures the old timer-only path (before/after timings).</summary>
    internal static bool QuickFlush = true;
    private const int QuickFlushMaxChars = 4096;
    private void RequestQuickFlush()
    {
        if (!QuickFlush || _disposed || PendingChars > QuickFlushMaxChars || Interlocked.Exchange(ref _quickFlush, 1) == 1) return;
        try
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
            {
                Interlocked.Exchange(ref _quickFlush, 0);
                if (!_disposed) Flush();
            });
        }
        catch { Interlocked.Exchange(ref _quickFlush, 0); }
    }

    private void Flush()
    {
        string text;
        lock (_chunks)
        {
            if (_pendingLen == 0) return;
            var sb = new StringBuilder(Math.Min(_pendingLen, MaxFlushChars) + 48);
            if (_dropped) { sb.Append("\r\n\x1b[33m[…output throttled: oldest lines dropped…]\x1b[0m\r\n"); _dropped = false; }
            while (_chunks.Count > 0 && sb.Length < MaxFlushChars)
            {
                var c = _chunks.Dequeue();
                _pendingLen -= c.Length;
                sb.Append(c);
            }
            text = sb.ToString();
        }
        text = _csiCarry + text;
        _csiCarry = "";
        var tail = IncompleteCsiTail(text);
        if (tail >= 0) { _csiCarry = ClampCsiDigits(text[tail..]); text = text[..tail]; }
        text = ClampCsi(text);
        if (text.Length == 0) return;
        if (PasteGuard.BracketedFrom(text) is bool bp) _bracketed = bp;
        Post(new { t = "out", d = text });
        Remember(text);
        Flushed?.Invoke();
    }

    /// <summary>Self-test: output was posted to the page (typing-latency measurement).</summary>
    internal event Action? Flushed;

    // ---- hostile output: CSI parameter clamp ------------------------------------------------------------------------
    // Found by the terminal fuzzing (s23): "ESC[99999999L" makes xterm.js insert lines one at a time — the tab freezes for
    // minutes. No real program needs a control-sequence number above 9999 (screens are far smaller), so larger ones are
    // clamped before the page sees them. A sequence split across two flushes is held back until it is complete.

    private string _csiCarry = "";
    private static readonly System.Text.RegularExpressions.Regex Csi = new(@"(?:\x1b\[|\u009b)[0-9;:?>=!<]*[ -/]*[@-~]", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex Big = new(@"\d{5,}", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex OpenCsi = new(@"(?:\x1b\[?|\u009b)[0-9;:?>=!<]*[ -/]*\z", System.Text.RegularExpressions.RegexOptions.Compiled);

    internal static string ClampCsiDigits(string seq) => Big.Replace(seq, m => int.TryParse(m.Value, out var n) && n <= 9999 ? m.Value : "9999");

    internal static string ClampCsi(string s) =>
        s.IndexOf('\x1b') < 0 && s.IndexOf('\u009b') < 0 ? s : Csi.Replace(s, m => Big.IsMatch(m.Value) ? ClampCsiDigits(m.Value) : m.Value);

    /// <summary>Index of an unfinished CSI (or a lone ESC) at the very end of <paramref name="s"/>, else -1.</summary>
    internal static int IncompleteCsiTail(string s)
    {
        int i = Math.Max(s.LastIndexOf('\x1b'), s.LastIndexOf('\u009b'));
        if (i < 0 || s.Length - i > 256) return -1;
        return OpenCsi.IsMatch(s, i) && OpenCsi.Match(s, i).Index == i ? i : -1;
    }

    // ---- paste safety ------------------------------------------------------------------------------------------------

    /// <summary>Bracketed-paste mode as last announced by the remote shell (ESC[?2004h/l). Reset on each new connection.</summary>
    private bool _bracketed;
    internal bool Bracketed => _bracketed;

    private void PasteClipboard()
    {
        string text;
        try { if (!Clipboard.ContainsText()) return; text = Clipboard.GetText(); } catch { return; }
        PasteText(text);
    }

    /// <summary>Every paste into this terminal goes through here (Ctrl+Shift+V, Ctrl+V, right-click, menu, paste
    /// selection): control characters are stripped, and multi-line, risky or huge pastes are shown first (Settings →
    /// Terminal → "Confirm multi-line and risky pastes").</summary>
    private void PasteText(string text)
    {
        var r = PasteGuard.Check(text);
        if (r.Clean.Length == 0) return;
        var send = r.Clean;
        var env = EnvUi.ForView(this);
        if (PasteNeedsConfirm(r, env))
        {
            bool offerNoEnter = r.EndsWithNewline && !_bracketed;
            var options = offerNoEnter ? new[] { "Cancel", "Paste", "Paste without the final Enter" } : new[] { "Cancel", "Paste" };
            var head = Envs.IsProduction(env) ? $"⚠ PRODUCTION — {_target.Title}\n\n" : env.Length > 0 ? $"{env} — {_target.Title}\n\n" : "";
            var choice = Dialogs.Choose("Paste into the terminal?", head + PasteGuard.Describe(r, _bracketed, _target.Title), options, cancelIndex: 0, width: 640);
            if (choice <= 0) { SetStatus($"{_target.Endpoint} · paste cancelled"); return; }
            if (choice == 2) send = PasteGuard.WithoutFinalNewline(send);
        }
        else if (r.RemovedControls > 0) SetStatus($"{_target.Endpoint} · {r.RemovedControls} hidden control character(s) removed from the paste");
        Post(new { t = "pastetext", d = send });
    }

    /// <summary>Multi-line / risky pastes are shown first when the setting is on — and always into a PRODUCTION terminal (ADR-017).</summary>
    public static bool PasteNeedsConfirm(PasteGuard.Result r, string env) =>
        r.NeedsConfirm && (AppSettings.Current.TerminalConfirmPaste || Envs.IsProduction(env));

    private void WriteLocal(string text)
    {
        Enqueue(text);
        if (!_flush.IsEnabled && !_disposed) _flush.Start();
    }

    private void Post(object message)
    {
        if (_disposed || !_pageReady) return;
        // Reading CoreWebView2 itself throws once the control is disposed (a timer tick racing a closing tab, DarkCipher0x's log
        // 2026-10-17 20:39), so the check sits inside the try as well.
        try { if (_web.CoreWebView2 is { } core) core.PostWebMessageAsJson(JsonSerializer.Serialize(message)); } catch { }
    }
}
