using System.Runtime.InteropServices;
using System.Windows.Threading;
using DcxManager.Services;

namespace DcxManager.Chat;

/// <summary>
/// Locks every chat after the PC has gone unused for <c>ChatAutoLockMinutes</c> (0.6.39, ADR-117; 0 = off, Group Policy
/// can set it). "Unused" is Windows' own last keyboard or mouse input, for the whole session, so a chat stays unlocked
/// while you work in another app. Locking wipes the chat keys; the PC stays paired and the next unlock asks for the
/// passphrase. Checked every 20 seconds on the UI thread; it does nothing while no chat is unlocked.
/// </summary>
internal static class ChatIdleLock
{
    private static DispatcherTimer? _timer;

    /// <summary>Starts the check (once, when the chat module loads).</summary>
    public static void Start()
    {
        if (_timer is not null) return;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(20) };
        _timer.Tick += (_, _) => Check(IdleFor());
        _timer.Start();
    }

    /// <summary>Locks the chats when the PC has been idle for at least the setting (exposed for the self-test).</summary>
    internal static bool Check(TimeSpan idle)
    {
        var mins = Math.Clamp(AppSettings.Current.ChatAutoLockMinutes, 0, 1440);
        if (mins == 0 || idle < TimeSpan.FromMinutes(mins)) return false;
        if (!ChatHub.Sessions.Any(s => s.Engine is not null)) return false;
        ChatHub.LockAll($"no keyboard or mouse use for {mins} min");
        return true;
    }

    private static TimeSpan IdleFor()
    {
        var info = new LastInput { cbSize = (uint)Marshal.SizeOf<LastInput>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
        return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.dwTime));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInput { public uint cbSize; public uint dwTime; }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInput plii);
}
