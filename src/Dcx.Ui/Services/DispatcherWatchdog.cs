using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace DcxManager.Services;

/// <summary>
/// Keeps WPF's work queue moving (0.5.3, DarkCipher0x: "it still has the same bug with the error log right click"). WPF runs
/// everything on the UI thread — clicks, drawing, timers, closing the window — as queued operations, and wakes itself to
/// run them by posting one "DispatcherProcessQueue" message to its own hidden window. It never posts a second one while
/// it believes the first is on its way. If that message is lost, the queue stops for good: Windows still sees a
/// responding app (the thread pumps messages), but nothing in it runs again, and closing the window leaves the process
/// behind. A dump of the frozen 0.5.2 showed exactly that: the UI thread idle in GetMessage, 138 operations queued at up
/// to Send priority, and the dispatcher's "posted" flag still set.
///
/// How it was lost: a message that reaches the dispatcher's window while WPF has processing suspended (during layout
/// and rendering, e.g. the layout pass a right-click menu starts) is consumed and WPF throws "Dispatcher processing has
/// been suspended, but messages are still being processed"; <see cref="UiErrorGuard"/> logs that and carries on. When the
/// consumed message was the wake-up itself, the queue stalled. Two remedies:
/// <list type="bullet">
/// <item><see cref="KickSoon"/>: called whenever that exception is caught — the wake-up is posted again once the layout
/// pass is over;</item>
/// <item>a background heartbeat: every <see cref="Interval"/> an empty operation is queued; if it hasn't run after
/// <see cref="StallAfter"/>, the wake-up is posted again (and the stall is logged, at most once a minute). A spare wake-up
/// is harmless: WPF processes its queue and finds nothing more than it would have done anyway.</item>
/// </list>
/// The wake-up is WPF's own registered message, posted to WPF's own window; nothing else is sent. The self-test checks the
/// message number and the window against WPF's internals on every build, and recovers a real stall.
/// </summary>
public static class DispatcherWatchdog
{
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan StallAfter = TimeSpan.FromSeconds(2);

    /// <summary>WPF's wake-up message (Dispatcher registers the same name; the self-test compares the numbers).</summary>
    internal static readonly uint WakeMessage = RegisterWindowMessage("DispatcherProcessQueue");

    private static Dispatcher? _ui;
    private static IntPtr _window;
    private static Thread? _thread;
    private static int _beatQueued;
    private static long _queuedAtTicks;
    private static long _lastLoggedTicks;
    private static int _kicks;

    /// <summary>Wake-ups posted again (by the heartbeat or <see cref="KickSoon"/>), for the self-test.</summary>
    public static int Kicks => Volatile.Read(ref _kicks);
    public static bool Running => _thread != null;

    /// <summary>Starts the heartbeat for <paramref name="ui"/> (once). False when WPF's window can't be found (logged; the
    /// app runs without the watchdog).</summary>
    public static bool Start(Dispatcher ui)
    {
        if (_thread != null) return true;
        var hwnd = WindowOf(ui);
        if (hwnd == IntPtr.Zero || WakeMessage == 0)
        {
            Log.Write("Dispatcher watchdog", new InvalidOperationException("WPF's dispatcher window or wake-up message was not found; the watchdog is off"));
            return false;
        }
        _ui = ui; _window = hwnd;
        _uiThreadId = ui.CheckAccess() ? GetCurrentThreadId() : 0;
        _thread = new Thread(Loop) { IsBackground = true, Name = "dispatcher-watchdog", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        return true;
    }

    /// <summary>Posts the wake-up again in a moment, once the current layout or render pass is over (from the handler of
    /// "Dispatcher processing has been suspended…").</summary>
    public static void KickSoon()
    {
        if (_window == IntPtr.Zero) return;
        _ = Task.Delay(50).ContinueWith(_ => Kick(), TaskScheduler.Default);
    }

    /// <summary>Posts WPF's wake-up message to WPF's window now.</summary>
    public static void Kick()
    {
        if (_window == IntPtr.Zero) return;
        Interlocked.Increment(ref _kicks);
        PostMessage(_window, WakeMessage, IntPtr.Zero, IntPtr.Zero);
    }

    private static void Loop()
    {
        while (_ui is { HasShutdownStarted: false } ui)
        {
            Thread.Sleep(Interval);
            var now = DateTime.UtcNow.Ticks;
            if (Interlocked.CompareExchange(ref _beatQueued, 1, 0) == 0)
            {
                Interlocked.Exchange(ref _queuedAtTicks, now);
                try { ui.BeginInvoke(DispatcherPriority.Normal, new Action(() => Volatile.Write(ref _beatQueued, 0))); }
                catch { return; }   // the dispatcher has shut down
                continue;
            }
            var waited = TimeSpan.FromTicks(now - Interlocked.Read(ref _queuedAtTicks));
            if (waited < StallAfter) continue;
            // Busy (a long task on the UI thread): the spare wake-up waits its turn, nothing is logged. Idle in its message
            // wait with work queued: WPF lost its wake-up — that is the fault, and it is logged (at most once a minute).
            bool idle = UiThreadIdle();
            Kick();
            if (idle && now - Interlocked.Read(ref _lastLoggedTicks) > TimeSpan.FromMinutes(1).Ticks)
            {
                Interlocked.Exchange(ref _lastLoggedTicks, now);
                Log.Write("UI dispatcher watchdog", new TimeoutException($"queued UI work had not run for {waited.TotalSeconds:0.0} s while the UI thread was idle: WPF lost its wake-up message; it was posted again"));
            }
        }
    }

    private static uint _uiThreadId;

    /// <summary>True when the UI thread is waiting for a message (not running anything). Read from the OS thread list,
    /// only once a stall is suspected.</summary>
    private static bool UiThreadIdle()
    {
        if (_uiThreadId == 0) return false;
        try
        {
            using var me = System.Diagnostics.Process.GetCurrentProcess();
            foreach (System.Diagnostics.ProcessThread t in me.Threads)
                if (t.Id == _uiThreadId)
                    return t.ThreadState == System.Diagnostics.ThreadState.Wait && t.WaitReason == System.Diagnostics.ThreadWaitReason.UserRequest;
        }
        catch { }
        return false;
    }

    /// <summary>WPF's hidden window for <paramref name="d"/> (Dispatcher._window → its HwndWrapper's Handle), or zero.</summary>
    internal static IntPtr WindowOf(Dispatcher d)
    {
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        try
        {
            var holder = typeof(Dispatcher).GetField("_window", Any)?.GetValue(d);
            if (holder == null) return IntPtr.Zero;
            // SecurityCriticalData<MessageOnlyHwndWrapper>: the wrapper is its _value field (or Value property).
            var wrapper = holder.GetType().GetField("_value", Any)?.GetValue(holder) ?? holder.GetType().GetProperty("Value", Any)?.GetValue(holder) ?? holder;
            return wrapper.GetType().GetProperty("Handle", Any)?.GetValue(wrapper) is IntPtr h ? h : IntPtr.Zero;
        }
        catch { return IntPtr.Zero; }
    }

    /// <summary>The wake-up message number WPF itself registered (Dispatcher._msgProcessQueue), for the self-test.</summary>
    internal static long WpfWakeMessage() =>
        typeof(Dispatcher).GetField("_msgProcessQueue", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) is { } v ? Convert.ToInt64(v) : -1;

    /// <summary>Self-test only: puts <paramref name="d"/> into the stalled state (WPF believes a wake-up is on its way when
    /// none is), as a lost message leaves it.</summary>
    internal static bool SimulateLostWakeUp(Dispatcher d)
    {
        var f = typeof(Dispatcher).GetField("_postedProcessingType", BindingFlags.NonPublic | BindingFlags.Instance);
        if (f == null) return false;
        f.SetValue(d, 2);   // PROCESS_FOREGROUND: "a wake-up message is posted"
        return true;
    }

    [DllImport("user32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("kernel32")] private static extern uint GetCurrentThreadId();
    [DllImport("user32", SetLastError = true)] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
}
