namespace DcxManager.Services;

/// <summary>
/// The app is locking (ADR-117): what holds secrets in memory and lives below the app (a module such as CipherChat)
/// wipes them here. Raised by the app's lock before its windows hide; a handler that throws is logged and the rest still run.
/// </summary>
public static class AppLockEvents
{
    public static event Action<string>? Locking;
    /// <summary>0.6.42: the app was unlocked (you just verified with Windows Hello or the master password): CipherChat unlocks
    /// the chats whose passphrase is saved.</summary>
    public static event Action<string>? Unlocked;

    /// <summary>Called by the app's lock when it opens again.</summary>
    public static void RaiseUnlocked(string reason)
    {
        foreach (var h in Unlocked?.GetInvocationList() ?? Array.Empty<Delegate>())
        {
            try { ((Action<string>)h)(reason); }
            catch (Exception ex) { Log.Write("Unlock handler", ex); }
        }
    }

    /// <summary>Whether the app is locked right now (set by the app's lock): work started from outside the windows, such
    /// as the stream manager's system-wide keys (ADR-118), does nothing while it is.</summary>
    public static Func<bool> IsLocked { get; set; } = () => false;

    /// <summary>Called by the app's lock with its reason.</summary>
    public static void RaiseLocking(string reason)
    {
        foreach (var h in Locking?.GetInvocationList() ?? Array.Empty<Delegate>())
        {
            try { ((Action<string>)h)(reason); }
            catch (Exception ex) { Log.Write("Lock handler", ex); }
        }
    }
}
