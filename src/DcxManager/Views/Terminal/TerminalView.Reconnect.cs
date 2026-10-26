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

// TerminalView: an unexpected drop and the automatic reconnect (moved unchanged from TerminalView.cs in 0.2.0 phase 6½).
public sealed partial class TerminalView
{
    // ---- unexpected drop → auto-reconnect ------------------------------------------------------------------------

    /// <summary>Input modes a dead session can leave on (tmux/htop/vim mouse tracking, focus events, app keys, alt screen):
    /// left on, the next shell gets mouse clicks typed at its prompt as "0;137;11M…". Leaves the alt screen without
    /// restoring the cursor (1047, not 1049), soft-resets, then turns off every mouse protocol/encoding, focus reports,
    /// bracketed paste and app keypad. Output stays.</summary>
    internal const string ResetInputModes =
        "\x1b[?1047l\x1b[!p\x1b[?9;1000;1001;1002;1003;1004;1005;1006;1015;1016l\x1b[?2004l\x1b[?1l\x1b[?25h\x1b>";

    private void OnDrop(string reason)
    {
        if (_disposed) return;
        bool wasLive = _transport != null;
        Teardown();
        if (!wasLive) return;
        WriteLocal(ResetInputModes);
        if (_target.Has(SessionCaps.AutoReconnect) && AppSettings.Current.TerminalAutoReconnect && !_userClosed)
        {
            WriteLocal($"\r\n\x1b[33m⟫ {reason}.\x1b[0m\r\n");
            MaybeReconnect();
        }
        else
        {
            WriteLocal($"\r\n\x1b[33m⟫ {reason}. Press Enter or ↻ {(_target.Host != null ? "Reconnect" : "Reconnect for a new shell")}.\x1b[0m\r\n");
            SetStatus($"○ {_target.Endpoint} · {reason}");
        }
    }

    private void MaybeReconnect()
    {
        if (_disposed || _userClosed || !AppSettings.Current.TerminalAutoReconnect || !_target.Has(SessionCaps.AutoReconnect)) return;
        _attempt++;
        int max = Math.Max(1, AppSettings.Current.AutoReconnectMaxSeconds);
        int delay = Math.Min(max, 1 << Math.Min(5, _attempt - 1));   // 1,2,4,8,16,32 → capped
        _reconnectCts?.Cancel();
        var cts = _reconnectCts = new CancellationTokenSource();
        _ = CountdownThenConnect(delay, cts.Token);
    }

    private async Task CountdownThenConnect(int seconds, CancellationToken ct)
    {
        try
        {
            for (int s = seconds; s > 0; s--)
            {
                if (ct.IsCancellationRequested) return;
                SetStatus($"○ {_target.Endpoint} · reconnecting in {s}s (attempt {_attempt})…");
                await Task.Delay(1000, ct);
            }
            if (!ct.IsCancellationRequested && !_disposed) await ConnectAsync();
        }
        catch (OperationCanceledException) { }
    }
}
