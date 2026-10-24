using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using DcxManager.Views;
using Microsoft.Win32;

namespace DcxManager.Services;

/// <summary>
/// App lock: hides every window behind a sign-in screen (master password and/or authenticator code) after inactivity,
/// when Windows locks, or on demand. Live sessions, tunnels and transfers keep running while locked.
/// Checked every 20 s with GetLastInputInfo — no hooks, negligible cost.
/// </summary>
public static class AppLock
{
    private static DispatcherTimer? _timer;
    private static bool _locked;

    public static bool CanLock => SecureStore.HasPassword || SecureStore.HasTotp;
    public static bool IsLocked => _locked;

    public static void Start()
    {
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(20), DispatcherPriority.Background, (_, _) => Tick(), Application.Current.Dispatcher);
        _timer.Start();
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPower;
    }

    public static void Stop()
    {
        _timer?.Stop();
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPower;
    }

    private static void OnSessionSwitch(object? s, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock && AppSettings.Current.LockOnWindowsLock)
            Application.Current.Dispatcher.BeginInvoke(() => Lock("Windows was locked"));
    }

    private static void OnPower(object? s, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend && AppSettings.Current.LockOnWindowsLock)
            Application.Current.Dispatcher.BeginInvoke(() => Lock("the PC went to sleep"));
    }

    private static void Tick()
    {
        var m = AppSettings.Current.AutoLockMinutes;
        if (m <= 0 || _locked || !CanLock) return;
        if (IdleTime() >= TimeSpan.FromMinutes(m)) Lock($"{m} min without input");
    }

    /// <summary>Sandbox mode promises "master password at start + auto-lock". If the password requirement was removed the
    /// promise no longer holds, so the flag is switched off (and audited) rather than displayed while untrue; with sandbox
    /// on, auto-lock can't be "never". Returns true when something changed. Called at start and after key changes.</summary>
    public static bool ReconcileSandbox()
    {
        var s = AppSettings.Current;
        if (!s.SandboxMode) return false;
        if (!SecureStore.PasswordRequiredOnStart)
        {
            s.SandboxMode = false;
            AuditLog.Write("app", "app", "sandbox mode off", "the master password is no longer required at start");
        }
        else if (s.AutoLockMinutes <= 0) s.AutoLockMinutes = 15;
        else return false;
        try { AppSettings.Save(); } catch (Exception ex) { Log.Write("sandbox reconcile", ex); }
        return true;
    }

    public static TimeSpan IdleTime()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
        return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.dwTime));
    }

    /// <summary>Locks now. Returns false if no factor is configured (nothing to unlock with).</summary>
    public static bool Lock(string reason = "locked by you")
    {
        if (_locked) return true;
        if (!CanLock) return false;
        _locked = true;
        Locks.CloseAll("the app locked");   // 0.5.1 (ADR-067): every unlocked category and item closes again
        Bitwarden.BitwardenVault.Lock("the app locked");   // 0.6.14 (ADR-093): the vault's session key goes with it
        CredentialSources.Forget();   // 0.6.17 (ADR-096): and every value read from Bitwarden or Password Safe
        PasswordSafeSessions.CheckInLeases("the app locked");
        // ADR-011: nothing is broadcast or served while locked.
        try { Api.Broadcasts.StopAll("app locked"); Api.LocalServer.Sync(); } catch (Exception ex) { Log.Write("Lock: stop listener", ex); }
        var windows = Application.Current.Windows.OfType<Window>().Where(w => w.IsVisible && w is not UnlockWindow).ToList();
        foreach (var w in windows) w.Hide();
        AuditLog.Write("you", "app", "locked", reason);
        try
        {
            while (true)
            {
                var gate = new UnlockWindow(needPassword: SecureStore.HasPassword, needCode: SecureStore.HasTotp, lockScreen: true);
                var ok = gate.ShowDialog() == true;
                if (ok) break;
                if (gate.QuitRequested)
                {
                    _locked = false;
                    if (MainWindow.Instance is { } mw) { mw.ExitWithoutPrompt = true; mw.Close(); }
                    SecureStore.Forget();
                    return true;
                }
            }
        }
        finally
        {
            if (_locked)
            {
                _locked = false;
                foreach (var w in windows) { try { w.Show(); } catch { } }
                Ui.Window.Activate();
                try { Api.LocalServer.Sync(); } catch (Exception ex) { Log.Write("Unlock: listener", ex); }
                _ = BitwardenSessions.AutoUnlockAsync("the app was unlocked");   // 0.6.20: with a remembered master password only
            }
        }
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
}
