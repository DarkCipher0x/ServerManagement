using System.Windows.Threading;

namespace DcxManager.Services;

/// <summary>
/// Stops one recurring UI exception from taking the app down (s37). An exception thrown while WPF measures or renders an
/// element comes back on every layout pass. The old handler logged each one and opened an error box in a nested message
/// loop, so that box never got to paint: a blank white window, a frozen app, and hundreds of identical sealed log entries
/// (DarkCipher0x's freeze on opening Cloud providers, 2026-10-19). Now:
/// <list type="bullet">
/// <item>the same failure (type, message, first frame) is logged once, then counted, never logged again per pass;</item>
/// <item>more than <see cref="StormCount"/> repeats within <see cref="StormWindow"/> is a loop: the screen in front is closed
/// unless it is a live session (the action is decided by <see cref="Classify"/>, the caller does it);</item>
/// <item>the error box is never opened from inside the exception handler. It is queued at idle priority, at most one
/// for each failure, so it can always paint.</item>
/// </list>
/// </summary>
public static class UiErrorGuard
{
    public const int StormCount = 8;
    public static readonly TimeSpan StormWindow = TimeSpan.FromSeconds(3);
    /// <summary>The same failure is reported again (box) at most this often.</summary>
    public static readonly TimeSpan ReshowAfter = TimeSpan.FromMinutes(1);

    public enum Verdict { LogAndShow, LogOnly, Count, Storm }

    private sealed class Seen { public DateTime First, LastShown; public readonly Queue<DateTime> Recent = new(); public int Total; public bool Stormed; }
    private static readonly Dictionary<string, Seen> _seen = new();
    private static readonly object Gate = new();

    /// <summary>WPF's "Dispatcher processing has been suspended, but messages are still being processed": a window message
    /// arrived while WPF was drawing. Embedded browser windows (WebView2 terminals) send them when their tab is hidden. WPF
    /// drops that one message and carries on, so it is logged once and never answered with a box: opening a modal box at
    /// that moment is what froze the app behind a blank window (DarkCipher0x's log, 2026-10-19 02:33).</summary>
    public static bool IsDispatcherReentrancy(Exception ex) =>
        ex is InvalidOperationException && (ex.Message.Contains("Dispatcher processing has been suspended", StringComparison.Ordinal)
                                            || (ex.StackTrace?.Contains("Dispatcher.WndProcHook", StringComparison.Ordinal) ?? false));

    /// <summary>What identifies "the same failure": type, message and the first stack frame.</summary>
    public static string Signature(Exception ex)
    {
        var frame = ex.StackTrace?.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        return $"{ex.GetType().FullName}|{ex.Message}|{frame}";
    }

    /// <summary>Records one occurrence and says what to do with it. <paramref name="now"/> is for the self-test.</summary>
    public static Verdict Classify(Exception ex, DateTime? now = null)
    {
        var t = now ?? DateTime.UtcNow;
        var sig = Signature(ex);
        lock (Gate)
        {
            if (!_seen.TryGetValue(sig, out var s))
            {
                if (_seen.Count > 200) _seen.Clear();
                _seen[sig] = s = new Seen { First = t, LastShown = t };
                s.Recent.Enqueue(t); s.Total = 1;
                return Verdict.LogAndShow;
            }
            s.Total++;
            s.Recent.Enqueue(t);
            while (s.Recent.Count > 0 && t - s.Recent.Peek() > StormWindow) s.Recent.Dequeue();
            if (s.Recent.Count > StormCount && !s.Stormed) { s.Stormed = true; return Verdict.Storm; }
            if (s.Stormed || s.Recent.Count > 2) return Verdict.Count;
            if (t - s.LastShown >= ReshowAfter) { s.LastShown = t; return Verdict.LogAndShow; }
            return Verdict.LogOnly;
        }
    }

    /// <summary>How often a failure has happened (self-test, the summary line).</summary>
    public static int CountOf(Exception ex) { lock (Gate) return _seen.TryGetValue(Signature(ex), out var s) ? s.Total : 0; }

    internal static void ResetForTest() { lock (Gate) _seen.Clear(); }
    /// <summary>Distinct UI failures seen since the last reset, as "message ×count" (self-test).</summary>
    internal static List<string> SeenForTest() { lock (Gate) return _seen.Select(kv => $"{kv.Key.Split('|')[1]} ×{kv.Value.Total}").ToList(); }

    /// <summary>Queues work at idle priority, so it runs only once the window has rendered.</summary>
    public static void WhenIdle(Dispatcher d, Action a) => d.BeginInvoke(DispatcherPriority.ApplicationIdle, a);
}
