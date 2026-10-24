using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Windows.Security.Credentials;
using Windows.Security.Credentials.UI;
using Windows.Security.Cryptography;

namespace DcxManager.Services;

/// <summary>
/// Windows Hello (0.3.0 §2, ADR-039): a consent check (face, fingerprint or PIN) for sensitive actions, and a Hello key —
/// a TPM-backed RSA key that only signs after the user verifies — whose signature over a fixed challenge unwraps the data
/// key at sign-in (<see cref="SecureStore"/>).
/// <para><b>In front:</b> a desktop app's Hello prompt opens behind its window unless Windows is told which window asks.
/// Consent goes through <c>IUserConsentVerifierInterop::RequestVerificationForWindowAsync(hwnd)</c> (Windows 11; called
/// through its vtable, as .NET 8 has no built-in WinRT COM interop). For key signing (no window-aware call in this SDK)
/// the app lets the Windows Security broker take the foreground (<c>AllowSetForegroundWindow</c>) and brings its dialog
/// forward when it appears.</para>
/// <para>The self-test never shows Hello: <see cref="Fake"/> answers instead (deterministic signatures).</para>
/// </summary>
public static class WindowsHello
{
    public enum Outcome { Verified, Cancelled, NotAvailable, Failed }

    /// <summary>The self-test's stand-in: never a real prompt. Set by the self-test only.</summary>
    internal static FakeHello? Fake { get; set; }

    internal sealed class FakeHello
    {
        public bool Available = true;
        public Outcome Answer = Outcome.Verified;
        public byte[] DeviceSecret = RandomNumberGenerator.GetBytes(32);
        public readonly HashSet<string> Keys = new();
        public int Prompts;
    }

    private static bool Faked => Fake != null || TestMode.Running;
    private static FakeHello F => Fake ??= new FakeHello();

    /// <summary>Hello is set up for this user (a PIN at least) and can sign with keys.</summary>
    public static async Task<bool> IsAvailableAsync()
    {
        if (Faked) return F.Available;
        try
        {
            return await UserConsentVerifier.CheckAvailabilityAsync() == UserConsentVerifierAvailability.Available
                && await KeyCredentialManager.IsSupportedAsync();
        }
        catch (Exception ex) { Log.Write("Windows Hello availability", ex); return false; }
    }

    /// <summary>Asks the user to verify (consent), in front of <paramref name="owner"/> (the main window when null).</summary>
    public static async Task<Outcome> ConfirmAsync(string message, Window? owner = null)
    {
        if (Faked) { F.Prompts++; return F.Available ? F.Answer : Outcome.NotAvailable; }
        var hwnd = Handle(owner);
        try
        {
            UserConsentVerificationResult r;
            if (hwnd != IntPtr.Zero && Environment.OSVersion.Version.Build >= 22000)
            {
                try { r = await ForWindow(hwnd, message); }
                catch (Exception ex) { Log.Write("Windows Hello (window-aware)", ex); r = await Fallback(message); }
            }
            else r = await Fallback(message);
            return r switch
            {
                UserConsentVerificationResult.Verified => Outcome.Verified,
                UserConsentVerificationResult.Canceled => Outcome.Cancelled,
                UserConsentVerificationResult.DeviceNotPresent or UserConsentVerificationResult.NotConfiguredForUser or UserConsentVerificationResult.DisabledByPolicy => Outcome.NotAvailable,
                _ => Outcome.Failed,
            };
        }
        catch (Exception ex) { Log.Write("Windows Hello", ex); return Outcome.Failed; }
    }

    /// <summary>Waits for <see cref="ConfirmAsync"/> on the UI thread while keeping it pumping (for synchronous gates).</summary>
    public static Outcome Confirm(string message, Window? owner = null)
    {
        var t = ConfirmAsync(message, owner);
        if (!t.IsCompleted && Application.Current?.Dispatcher.CheckAccess() == true)
        {
            var frame = new DispatcherFrame();
            t.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }
        return t.GetAwaiter().GetResult();
    }

    /// <summary>Creates (or replaces) the Hello key <paramref name="name"/> and returns its signature over
    /// <paramref name="challenge"/>, or null when the user cancelled or Hello can't.</summary>
    public static async Task<byte[]?> CreateAndSignAsync(string name, byte[] challenge)
    {
        if (Faked) { F.Prompts++; if (!F.Available || F.Answer != Outcome.Verified) return null; F.Keys.Add(name); return FakeSign(name, challenge); }
        try
        {
            using var front = BringBrokerForward();
            var created = await KeyCredentialManager.RequestCreateAsync(name, KeyCredentialCreationOption.ReplaceExisting);
            if (created.Status != KeyCredentialStatus.Success) { Log.Write("Windows Hello key", new InvalidOperationException("create: " + created.Status)); return null; }
            return await SignWith(created.Credential, challenge);
        }
        catch (Exception ex) { Log.Write("Windows Hello key", ex); return null; }
    }

    /// <summary>Signs <paramref name="challenge"/> with the existing Hello key (the user verifies first). Null when
    /// cancelled, missing or failed — the caller falls back to the master password.</summary>
    public static async Task<byte[]?> SignAsync(string name, byte[] challenge)
    {
        if (Faked) { F.Prompts++; return F.Available && F.Answer == Outcome.Verified && F.Keys.Contains(name) ? FakeSign(name, challenge) : null; }
        try
        {
            var opened = await KeyCredentialManager.OpenAsync(name);
            if (opened.Status != KeyCredentialStatus.Success) { Log.Write("Windows Hello key", new InvalidOperationException("open: " + opened.Status)); return null; }
            using var front = BringBrokerForward();
            return await SignWith(opened.Credential, challenge);
        }
        catch (Exception ex) { Log.Write("Windows Hello key", ex); return null; }
    }

    /// <summary>Deletes the Hello key (turning Hello sign-in off). Missing is fine.</summary>
    public static async Task DeleteAsync(string name)
    {
        if (Faked) { F.Keys.Remove(name); return; }
        try { await KeyCredentialManager.DeleteAsync(name); } catch (Exception ex) { Log.Write("Windows Hello key delete", ex); }
    }

    private static async Task<byte[]?> SignWith(KeyCredential key, byte[] challenge)
    {
        var r = await key.RequestSignAsync(CryptographicBuffer.CreateFromByteArray(challenge));
        if (r.Status != KeyCredentialStatus.Success) { Log.Write("Windows Hello sign", new InvalidOperationException(r.Status.ToString())); return null; }
        CryptographicBuffer.CopyToByteArray(r.Result, out var sig);
        return sig;
    }

    private static byte[] FakeSign(string name, byte[] challenge) =>
        HMACSHA256.HashData(F.DeviceSecret, System.Text.Encoding.UTF8.GetBytes(name).Concat(challenge).ToArray());

    // ---- in front -----------------------------------------------------------------------------------------------------

    private static IntPtr Handle(Window? owner)
    {
        try
        {
            var w = owner ?? Application.Current?.Windows.OfType<Window>().FirstOrDefault(x => x.IsActive) ?? Application.Current?.MainWindow;
            return w == null ? IntPtr.Zero : new WindowInteropHelper(w).Handle;
        }
        catch { return IntPtr.Zero; }
    }

    private static async Task<UserConsentVerificationResult> Fallback(string message)
    {
        using var front = BringBrokerForward();
        return await UserConsentVerifier.RequestVerificationAsync(message);
    }

    private static readonly Guid IidInterop = new("39E050C3-4E74-441A-8DC0-B81104DF949C");

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int RequestForWindow(IntPtr self, IntPtr hwnd, IntPtr message, ref Guid riid, out IntPtr asyncOp);

    /// <summary>IUserConsentVerifierInterop::RequestVerificationForWindowAsync, called through its vtable (slot 6, after
    /// IUnknown's 3 and IInspectable's 3 methods).</summary>
    private static async Task<UserConsentVerificationResult> ForWindow(IntPtr hwnd, string message)
    {
        IntPtr factory = IntPtr.Zero, cls = IntPtr.Zero, msg = IntPtr.Zero, op = IntPtr.Zero;
        try
        {
            const string ClassName = "Windows.Security.Credentials.UI.UserConsentVerifier";
            Check(WindowsCreateString(ClassName, ClassName.Length, out cls));
            var iid = IidInterop;
            Check(RoGetActivationFactory(cls, ref iid, out factory));
            Check(WindowsCreateString(message, message.Length, out msg));
            var vtbl = Marshal.ReadIntPtr(factory);
            var call = Marshal.GetDelegateForFunctionPointer<RequestForWindow>(Marshal.ReadIntPtr(vtbl, 6 * IntPtr.Size));
            var riid = WinRT.GuidGenerator.CreateIID(typeof(global::Windows.Foundation.IAsyncOperation<UserConsentVerificationResult>));
            Check(call(factory, hwnd, msg, ref riid, out op));
            var operation = WinRT.MarshalInterface<global::Windows.Foundation.IAsyncOperation<UserConsentVerificationResult>>.FromAbi(op);
            return await operation;
        }
        finally
        {
            if (op != IntPtr.Zero) Marshal.Release(op);
            if (factory != IntPtr.Zero) Marshal.Release(factory);
            if (msg != IntPtr.Zero) WindowsDeleteString(msg);
            if (cls != IntPtr.Zero) WindowsDeleteString(cls);
        }
    }

    private static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }

    /// <summary>Lets the Windows Security broker take the foreground, and for a few seconds brings its dialog forward if
    /// it opened behind. Disposing stops the watch.</summary>
    private static IDisposable BringBrokerForward()
    {
        try { AllowSetForegroundWindow(-1); } catch { }
        var cts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 40 && !cts.IsCancellationRequested; i++)
            {
                await Task.Delay(100);
                var w = FindWindow("Credential Dialog Xaml Host", null);
                if (w == IntPtr.Zero) continue;
                if (GetForegroundWindow() != w) SetForegroundWindow(w);
                return;
            }
        });
        return cts;
    }

    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string src, int length, out IntPtr hstring);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(IntPtr hstring);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? cls, string? title);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
}
