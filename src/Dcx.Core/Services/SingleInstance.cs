using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DcxManager.Services;

/// <summary>
/// One running instance per store (ADR-021). The key is the store identity — <c>DCX_DATA_DIR</c> if set (self-test, scratch
/// stores), otherwise the user's installation. A second launch with the same key asks the first to come to the front and
/// exits before opening a window, the store or a browser, so two processes never write one store or share one browser.
/// </summary>
public static class SingleInstance
{
    private static Mutex? _mutex;
    private static EventWaitHandle? _activate;
    private static Thread? _listener;

    /// <summary>Activation requests received from later launches (the self-test counts them).</summary>
    public static int ActivationRequests => Volatile.Read(ref _requests);
    private static int _requests;

    /// <summary>"default" for the installation, or the full path of the DCX_DATA_DIR store.</summary>
    public static string Key => Environment.GetEnvironmentVariable("DCX_DATA_DIR") is { Length: > 0 } d
        ? Path.GetFullPath(d).TrimEnd('\\').ToLowerInvariant() : "default";

    public static string NameFor(string key) =>
        "DarkCipherServerManagement." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24];

    /// <summary>Takes the instance for <paramref name="key"/>; false when another process already holds it.
    /// An abandoned mutex (the holder crashed) counts as free.</summary>
    public static bool TryAcquire(string? key = null)
    {
        if (Take(key ?? Key) is not { } m) return false;
        _mutex = m;
        return true;
    }

    /// <summary>The owned mutex for a key, or null when another holder has it (abandoned = free). The caller keeps it alive.</summary>
    internal static Mutex? Take(string key)
    {
        var m = new Mutex(false, @"Local\" + NameFor(key));
        bool owned;
        try { owned = m.WaitOne(0); }
        catch (AbandonedMutexException) { owned = true; }
        if (owned) return m;
        m.Dispose();
        return null;
    }

    /// <summary>Gives the instance up early (a restart starts the next process before this one has exited).</summary>
    public static void Release()
    {
        try { _activate?.Dispose(); } catch { }
        try { _mutex?.ReleaseMutex(); } catch { }
        try { _mutex?.Dispose(); } catch { }
        _mutex = null; _activate = null;
    }

    /// <summary>Asks the running instance (same key) to show its window. True when one was listening.</summary>
    public static bool SignalExisting(string? key = null)
    {
        try
        {
            using var ev = EventWaitHandle.OpenExisting(@"Local\" + NameFor(key ?? Key) + ".activate");
            return ev.Set();
        }
        catch (WaitHandleCannotBeOpenedException) { return false; }
    }

    /// <summary>Starts listening for later launches; <paramref name="onActivate"/> runs on a background thread.</summary>
    public static void Listen(Action onActivate, string? key = null)
    {
        if (_listener != null) return;
        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\" + NameFor(key ?? Key) + ".activate");
        _listener = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    _activate.WaitOne();
                    Interlocked.Increment(ref _requests);
                    onActivate();
                }
                catch (ObjectDisposedException) { return; }
                catch (Exception ex) { Log.Write("Second launch", ex); }
            }
        }) { IsBackground = true, Name = "single-instance" };
        _listener.Start();
    }
}
