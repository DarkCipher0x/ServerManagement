using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using DcxManager.Services;
using DcxManager.Views;

namespace DcxManager.Ssh;

/// <summary>
/// Remote Desktop through Windows' own client (mstsc.exe) — no embedded ActiveX. A .rdp file is generated per launch
/// (address, display, redirection, auto-reconnect; never a password) and removed shortly after mstsc reads it.
/// A vault password is handed over through the Windows credential store (CredWrite, TERMSRV/host) only for the moment
/// mstsc needs it, then deleted — it never appears on a command line or in a file.
/// </summary>
public static class RdpLauncher
{
    public static void Launch(HostEntry h)
    {
        // 0.6.29: through SSH — a forward on a random 127.0.0.1 port, open while mstsc runs (an RD Gateway wins).
        if (Rdp.RdpTunnel.Wanted(h) && h.Rdp.Gateway.Trim().Length == 0) { _ = LaunchThroughSshAsync(h); return; }
        // Offline mode (ADR-016): the server and any RD Gateway are checked before mstsc starts.
        if (Offline.On)
        {
            var gw = h.Rdp.Gateway.Trim();
            Offline.Run(h.Address, "Remote Desktop", () => { if (gw.Length > 0) Offline.Run(gw, "Remote Desktop (RD Gateway)", () => LaunchNow(h)); else LaunchNow(h); });
            return;
        }
        LaunchNow(h);
    }

    private static async Task LaunchThroughSshAsync(HostEntry h)
    {
        var key = Rdp.RdpTunnel.NewKey(h);
        try
        {
            var (port, route) = await Rdp.RdpTunnel.OpenAsync(h, key);
            var via = h.Clone();
            via.Address = "127.0.0.1"; via.Port = port; via.Rdp.Through = "direct";
            Process? p = null;
            LaunchNow(via, started => p = started);
            AuditLog.Write("you", h.Display, "Remote Desktop (mstsc) " + route);
            if (p == null) { Rdp.RdpTunnel.Close(key); return; }
            await p.WaitForExitAsync();
        }
        catch (Exception ex) { Ui.Dialogs.Error("Remote Desktop", "Couldn't open the SSH tunnel: " + ex.Message, explain: false); }
        finally { Rdp.RdpTunnel.Close(key); }
    }

    private static void LaunchNow(HostEntry h, Action<Process?>? started = null)
    {
        try
        {
            var dir = Path.Combine(AppInfo.CacheDir, "rdp");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"{h.Id}.rdp");
            File.WriteAllText(file, RdpFile(h), new UnicodeEncoding(false, true));

            string target = "TERMSRV/" + h.Address;
            bool wroteCred = false;
            var user = UserName(h);
            if (SshConnector.CredentialFor(h) is { Kind: CredentialKind.Password, Secret.Length: > 0 } c)
                wroteCred = CredWriteDomain(target, user, c.Secret);

            var proc = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "mstsc.exe"), $"\"{file}\"") { UseShellExecute = false });
            started?.Invoke(proc);
            h.LastConnected = DateTime.Now;
            if (!h.IsAdHoc) { try { AppData.SaveSoon(); } catch { } }
            AuditLog.Write("you", h.Display, "opened Remote Desktop", h.Port == 3389 ? h.Address : $"{h.Address}:{h.Port}");
            Ui.Window.Status($"Remote Desktop → {h.Display}{(wroteCred ? " (vault password handed to Windows for this sign-in)" : "")}");

            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(25));
                if (wroteCred) CredDelete(target, CRED_TYPE_DOMAIN_PASSWORD, 0);
                try { File.Delete(file); } catch { }
            });
        }
        catch (Exception ex) { Ui.Dialogs.Error("Remote Desktop", ex.Message); Log.Write("RDP launch", ex); }
    }

    /// <summary>
    /// The open Remote Desktop window(s) for a host. mstsc titles its window "address - Remote Desktop Connection"
    /// (class TscShellContainerClass), so this also finds sessions that were opened outside the app.
    /// </summary>
    public static List<(IntPtr Hwnd, string Title)> Windows(HostEntry h)
    {
        var list = Screenshot.FindWindows(h.Address, "TscShellContainerClass");
        if (list.Count == 0) list = Screenshot.FindWindows(h.Address + " - ");
        return list;
    }

    public static bool IsOpen(HostEntry h) => Windows(h).Count > 0;

    /// <summary>Screenshot submenu for an RDP host; the window is found when an item is clicked.</summary>
    public static System.Windows.Controls.MenuItem ScreenshotMenu(HostEntry h, System.Windows.Window? owner = null) =>
        Screenshot.Menu(() =>
        {
            var wins = Windows(h);
            if (wins.Count == 0) throw new InvalidOperationException($"No Remote Desktop window for {h.Address} is open.");
            // Full-screen sessions have no frame to crop; windowed ones lose only the title bar.
            return Task.FromResult(Screenshot.CaptureWindow(wins[0].Hwnd, clientOnly: true));
        }, h.Display + "-rdp", owner, "📷 Screenshot Remote Desktop window");

    private static string UserName(HostEntry h) => h.Rdp.Domain.Trim().Length > 0 ? $"{h.Rdp.Domain.Trim()}\\{h.Username}" : h.Username;

    public static string RdpFile(HostEntry h)
    {
        var r = h.Rdp;
        var sb = new StringBuilder();
        void L(string s) => sb.Append(s).Append("\r\n");
        L($"full address:s:{h.Address}{(h.Port != 3389 ? ":" + h.Port : "")}");
        L($"username:s:{UserName(h)}");
        L($"screen mode id:i:{(r.FullScreen ? 2 : 1)}");
        if (!r.FullScreen) { L($"desktopwidth:i:{Math.Clamp(r.Width, 640, 8192)}"); L($"desktopheight:i:{Math.Clamp(r.Height, 480, 8192)}"); L("smart sizing:i:1"); L("dynamic resolution:i:1"); }
        L($"use multimon:i:{(r.MultiMonitor ? 1 : 0)}");
        L($"administrative session:i:{(r.Admin ? 1 : 0)}");
        L($"redirectclipboard:i:{(r.Clipboard ? 1 : 0)}");
        L($"redirectprinters:i:{(r.Printers ? 1 : 0)}");
        L($"drivestoredirect:s:{(r.Drives ? "*" : "")}");
        L($"audiomode:i:{Math.Clamp(r.Audio, 0, 2)}");
        L($"audiocapturemode:i:{(r.Microphone ? 1 : 0)}");
        L($"autoreconnection enabled:i:{(r.AutoReconnect ? 1 : 0)}");
        L($"autoreconnect max retries:i:{Math.Clamp(r.ReconnectRetries, 1, 200)}");
        L("networkautodetect:i:1");
        L("bandwidthautodetect:i:1");
        L("connection type:i:7");
        L("authentication level:i:2");   // warn if the server can't be authenticated
        L("prompt for credentials:i:0");
        L("enablecredsspsupport:i:1");
        if (r.Gateway.Trim().Length > 0) { L($"gatewayhostname:s:{r.Gateway.Trim()}"); L("gatewayusagemethod:i:1"); L("gatewaycredentialssource:i:0"); L("gatewayprofileusagemethod:i:1"); }
        else L("gatewayusagemethod:i:4");
        return sb.ToString();
    }

    /// <summary>RDP keepalive is a server-side policy; these commands enable it on the Windows host (run there as admin).</summary>
    public static void ShowServerKeepAlive(HostEntry h) => Ui.Dialogs.ShowText($"RDP keepalive — {h.Display}",
        "Remote Desktop keepalive is configured on the Windows SERVER (the client side is auto-reconnect, set in the host).\n" +
        "Run these in an elevated PowerShell on the server — equivalent to the Group Policy\n" +
        "Computer Configuration → Administrative Templates → Windows Components → Remote Desktop Services →\n" +
        "Remote Desktop Session Host → Connections → \"Configure keep-alive connection interval\":\n\n" +
        "reg add \"HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows NT\\Terminal Services\" /v KeepAliveEnable /t REG_DWORD /d 1 /f\n" +
        "reg add \"HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows NT\\Terminal Services\" /v KeepAliveInterval /t REG_DWORD /d 1 /f\n\n" +
        "KeepAliveInterval is in minutes (1 = every minute). Optional — keep disconnected sessions for 8 hours instead of ending them:\n" +
        "reg add \"HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows NT\\Terminal Services\" /v MaxDisconnectionTime /t REG_DWORD /d 28800000 /f\n\n" +
        "Then: gpupdate /force   (new sessions pick it up).");

    // ---- Windows credential store (advapi32) ---------------------------------------------------------------------

    private const int CRED_TYPE_DOMAIN_PASSWORD = 2;
    private const int CRED_PERSIST_SESSION = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public int Flags, Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist, AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref CREDENTIAL cred, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);

    private static bool CredWriteDomain(string target, string user, string password)
    {
        var blob = Encoding.Unicode.GetBytes(password);
        var ptr = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, ptr, blob.Length);
            var c = new CREDENTIAL
            {
                Type = CRED_TYPE_DOMAIN_PASSWORD, TargetName = target, UserName = user,
                CredentialBlob = ptr, CredentialBlobSize = blob.Length, Persist = CRED_PERSIST_SESSION,
                Comment = "DarkCipher ServerManagement (temporary)",
            };
            return CredWrite(ref c, 0);
        }
        finally
        {
            for (int i = 0; i < blob.Length; i++) Marshal.WriteByte(ptr, i, 0);
            Array.Clear(blob);
            Marshal.FreeHGlobal(ptr);
        }
    }
}
