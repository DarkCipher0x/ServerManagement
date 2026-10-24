using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace DcxManager.Services;

/// <summary>
/// "Sign in again as this Windows user" (0.5.1, ADR-067): Windows Hello when it is set up, otherwise the standard Windows
/// credential prompt (CredUI, showing only the current account), checked with LogonUser and only accepted when the
/// account that signed in is the one running the app. The password is never stored or logged; the buffers holding it are
/// cleared as soon as it has been checked. Used by locks that open with Windows sign-in, and wherever admin approval
/// can't be asked for (a portable copy can't start itself elevated).
/// </summary>
public static class WindowsSignIn
{
    public enum Outcome { Verified, Cancelled, Failed }

    /// <summary>The self-test's answer (no prompt ever shows during the self-test).</summary>
    internal static Func<string, Outcome>? FakeForTest { get; set; }

    /// <summary>Asks this Windows user to confirm who they are for <paramref name="purpose"/>.</summary>
    public static async Task<Outcome> ConfirmAsync(string purpose, Window? owner = null)
    {
        if (FakeForTest is { } fake) return fake(purpose);
        if (TestMode.Running) { Views.Dialogs.RefusedInSelfTest("Windows sign-in: " + purpose); return Outcome.Cancelled; }   // never a window in the self-test
        var hello = await WindowsHello.ConfirmAsync(purpose, owner);
        if (hello == WindowsHello.Outcome.Verified) return Outcome.Verified;
        if (hello == WindowsHello.Outcome.Cancelled) return Outcome.Cancelled;
        return PasswordPrompt(purpose, owner);   // Hello isn't set up (or failed): the account's password instead
    }

    // ---- CredUI + LogonUser ---------------------------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDUI_INFO
    {
        public int cbSize;
        public IntPtr hwndParent;
        public string pszMessageText;
        public string pszCaptionText;
        public IntPtr hbmBanner;
    }

    private const int CREDUIWIN_ENUMERATE_CURRENT_USER = 0x200, ERROR_CANCELLED = 1223, LOGON32_LOGON_INTERACTIVE = 2, LOGON32_PROVIDER_DEFAULT = 0;

    [DllImport("credui.dll", CharSet = CharSet.Unicode)]
    private static extern int CredUIPromptForWindowsCredentials(ref CREDUI_INFO info, int authError, ref uint authPackage, IntPtr inBuffer, uint inSize,
        out IntPtr outBuffer, out uint outSize, ref bool save, int flags);

    [DllImport("credui.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredUnPackAuthenticationBuffer(int flags, IntPtr buffer, uint size, StringBuilder user, ref int userLen,
        StringBuilder domain, ref int domainLen, StringBuilder password, ref int passwordLen);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LogonUser(string user, string? domain, string password, int type, int provider, out IntPtr token);

    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("ole32.dll")] private static extern void CoTaskMemFree(IntPtr p);

    private static Outcome PasswordPrompt(string purpose, Window? owner)
    {
        var info = new CREDUI_INFO
        {
            cbSize = Marshal.SizeOf<CREDUI_INFO>(),
            hwndParent = owner != null ? new WindowInteropHelper(owner).Handle : Application.Current?.MainWindow is { } mw ? new WindowInteropHelper(mw).Handle : IntPtr.Zero,
            pszCaptionText = "ServerManagement",
            pszMessageText = (purpose.Length > 250 ? purpose[..250] : purpose) + "\n\nSign in with your Windows account to continue.",   // 0.5.7: Windows shows the account itself; no PC\user here
        };
        uint package = 0; bool save = false;
        int rc = CredUIPromptForWindowsCredentials(ref info, 0, ref package, IntPtr.Zero, 0, out var buf, out var size, ref save, CREDUIWIN_ENUMERATE_CURRENT_USER);
        if (rc == ERROR_CANCELLED) return Outcome.Cancelled;
        if (rc != 0) return Outcome.Failed;
        var user = new StringBuilder(514); var domain = new StringBuilder(338); var password = new StringBuilder(256);
        int ul = user.Capacity, dl = domain.Capacity, pl = password.Capacity;
        try
        {
            if (!CredUnPackAuthenticationBuffer(0, buf, size, user, ref ul, domain, ref dl, password, ref pl)) return Outcome.Failed;
            var u = user.ToString(); var d = domain.ToString();
            if (d.Length == 0 && u.Contains('\\')) { d = u[..u.IndexOf('\\')]; u = u[(u.IndexOf('\\') + 1)..]; }
            if (!LogonUser(u, d.Length > 0 ? d : null, password.ToString(), LOGON32_LOGON_INTERACTIVE, LOGON32_PROVIDER_DEFAULT, out var token)) return Outcome.Failed;
            try
            {
                using var id = new WindowsIdentity(token);
                // Only this app's own Windows user counts: another account's password proves nothing about who is here.
                return id.User is { } who && WindowsIdentity.GetCurrent().User is { } me && who.Equals(me) ? Outcome.Verified : Outcome.Failed;
            }
            finally { CloseHandle(token); }
        }
        finally
        {
            password.Clear(); password.Append('\0', password.Capacity); password.Clear();
            if (buf != IntPtr.Zero)
            {
                for (int i = 0; i < size; i++) Marshal.WriteByte(buf, i, 0);
                CoTaskMemFree(buf);
            }
        }
    }
}
