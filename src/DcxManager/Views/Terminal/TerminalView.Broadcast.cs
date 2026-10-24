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

// TerminalView: headless test seams and broadcast (ADR-011) (moved unchanged from TerminalView.cs in 0.2.0 phase 6½).
public sealed partial class TerminalView
{
    // ---- headless test seams (used by --selftest) ----------------------------------------------------------------

    internal static int MaxPending => MaxPendingChars;
    internal int PendingChars { get { lock (_chunks) return _pendingLen; } }
    internal string DiagStatus => $"status='{_status.Text}' web={(_web.CoreWebView2 != null)} sized={_sized} connecting={_connecting} diag='{_diag}'";
    internal void TestFeed(byte[] data) => OnBytes(data, data.Length);

    // ---- broadcast (ADR-011) ------------------------------------------------------------------------------------------

    private readonly Dictionary<int, TaskCompletionSource<Api.ScreenText?>> _screenWaits = new();
    private int _screenSeq;

    /// <summary>The rows on screen as text (UI thread). Null when the page isn't ready or doesn't answer within 3 s
    /// (a minimised/background WebView throttles its timers; the broadcast then keeps its last frame).</summary>
    internal async Task<Api.ScreenText?> CaptureScreenAsync()
    {
        if (_disposed || !_pageReady) return null;
        Flush();
        var id = ++_screenSeq;
        var tcs = new TaskCompletionSource<Api.ScreenText?>();
        _screenWaits[id] = tcs;
        Post(new { t = "screen", id });
        var done = await Task.WhenAny(tcs.Task, Task.Delay(3000));
        _screenWaits.Remove(id);
        return done == tcs.Task ? tcs.Task.Result : null;
    }

    internal bool IsDisposed => _disposed;

    /// <summary>The tail of the recent raw output (escape sequences included), at most <paramref name="maxChars"/>.</summary>
    internal string RecentOutput(int maxChars)
    {
        lock (_history) return _history.Length <= maxChars ? _history.ToString() : _history.ToString(_history.Length - maxChars, maxChars);
    }

    private Border? _castBanner;

    /// <summary>Shows/hides the red "BROADCASTING" strip on this terminal.</summary>
    internal void ShowBroadcasting(Api.Broadcast? b)
    {
        if (Content is not DockPanel root) return;
        if (b == null) { if (_castBanner != null) { root.Children.Remove(_castBanner); _castBanner = null; } return; }
        if (_castBanner == null)
        {
            var text = new TextBlock { Foreground = System.Windows.Media.Brushes.White, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            var stop = new Button { Content = "■ Stop broadcast", Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(8, 1, 8, 1) };
            stop.Click += (_, _) => Api.Broadcasts.StopFor(this, "stopped by you");
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(text); row.Children.Add(stop);
            _castBanner = new Border { Child = row, Padding = new Thickness(10, 4, 10, 4) };
            _castBanner.SetResourceReference(Border.BackgroundProperty, "Bad");
            DockPanel.SetDock(_castBanner, Dock.Top);
            root.Children.Insert(0, _castBanner);
        }
        ((TextBlock)((StackPanel)_castBanner.Child).Children[0]).Text = $"📡 BROADCASTING this screen (text, redacted) · until {b.Expires:HH:mm}" + (b.Hidden ? " · input hidden" : "");
    }
    internal void TestFlush() => Flush();
}
