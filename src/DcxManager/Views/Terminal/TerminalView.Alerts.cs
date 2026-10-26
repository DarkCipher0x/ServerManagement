using System.Windows.Threading;
using DcxManager.Services;
using DcxManager.Sessions;

namespace DcxManager.Views;

// TerminalView: alerts from a terminal you aren't looking at (0.6.29, ADR-104).
public sealed partial class TerminalView
{
    /// <summary>When Enter was last pressed (a command may have started), until the screen shows a prompt again.</summary>
    private DateTime? _cmdStarted;
    private DispatcherTimer? _quiet;
    private int _quietKick;
    private bool _alertsMuted;
    private readonly Dictionary<string, DateTime> _lastAlert = new();
    /// <summary>Self-test: the clock a command started on (a test moves it back instead of waiting).</summary>
    internal DateTime? CommandStartedForTest { get => _cmdStarted; set => _cmdStarted = value; }
    internal bool AlertsMuted { get => _alertsMuted; set => _alertsMuted = value; }

    /// <summary>Input watch: Enter may start a command.</summary>
    private void WatchInput(string data)
    {
        if (IsConnected && data.Contains('\r')) _cmdStarted = DateTime.UtcNow;
    }

    /// <summary>Output watch (any thread): while a command may be running, each burst of output restarts a short quiet timer.</summary>
    private void WatchOutputForAlerts()
    {
        if (_cmdStarted == null || _disposed || Interlocked.Exchange(ref _quietKick, 1) == 1) return;
        try
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                Interlocked.Exchange(ref _quietKick, 0);
                if (_disposed) return;
                _quiet ??= new DispatcherTimer(TimeSpan.FromMilliseconds(900), DispatcherPriority.Background, async (_, _) => await OnQuietAsync(), Dispatcher);
                _quiet.Stop(); _quiet.Start();
            });
        }
        catch { Interlocked.Exchange(ref _quietKick, 0); }
    }

    /// <summary>Output stopped for a moment: if the cursor's line is a prompt again, the command finished. Read from the
    /// screen, so it works inside tmux too; a question (y/N, a password) isn't a prompt and keeps waiting.</summary>
    internal async Task OnQuietAsync()
    {
        _quiet?.Stop();
        if (_cmdStarted is not { } start || !IsConnected) return;
        // A hidden tab's page may be too throttled to answer: then the last line of the output itself (not inside tmux).
        var screen = await CaptureScreenAsync();
        var line = screen is { CursorLine: >= 0 } sc && sc.CursorLine < sc.Lines.Length ? sc.Lines[sc.CursorLine] : ShellPlace.LastLine(RecentOutput(4000));
        if (!ShellPlace.LooksLikePrompt(line)) return;
        _cmdStarted = null;
        var took = DateTime.UtcNow - start;
        int secs = AppSettings.Current.TerminalAlertSeconds;
        if (secs > 0 && took.TotalSeconds >= secs) Alert("done", $"Command finished · {_target.Title}", $"A command finished after {Took(took)} on {_target.Endpoint}.");
    }

    private static string Took(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min {t.Seconds} s" : $"{(int)t.TotalSeconds} s";

    /// <summary>One alert of a kind per tab every 10 seconds at most (a bell loop can't flood you).</summary>
    internal bool Alert(string kind, string title, string text)
    {
        var s = AppSettings.Current;
        if (_alertsMuted || !s.TerminalAlerts || _disposed) return false;
        if (kind == "bell" && !s.TerminalAlertBell || kind == "drop" && !s.TerminalAlertDrop) return false;
        var now = DateTime.UtcNow;
        if (_lastAlert.TryGetValue(kind, out var last) && now - last < TimeSpan.FromSeconds(10)) return false;
        if (!SessionAlerts.Raise(this, kind, title, text)) return false;
        _lastAlert[kind] = now;
        return true;
    }
}
