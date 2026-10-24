using System.Runtime.InteropServices;
using Microsoft.Win32;
using DcxManager.Services;

namespace DcxManager.Rdp;

/// <summary>Windows' RDP ActiveX (mstscax.dll) is late-bound: properties via IDispatch, the password via
/// IMsTscNonScriptable, events via the IMsTscAxEvents connection point (ADR-013).</summary>
[ComImport, Guid("C1E6743A-41C1-4A74-832A-0DD06C1C7A0E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMsTscNonScriptable
{
    // First vtable slot after IUnknown: put_ClearTextPassword. Nothing after it is declared because nothing else is used.
    void PutClearTextPassword([MarshalAs(UnmanagedType.BStr)] string password);
}

[ComImport, Guid("2F079C4C-87B2-4AFD-97AB-20CDB43038AE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMsRdpClientNonScriptable
{
    // IMsTscNonScriptable (10 slots), then NotifyRedirectDeviceChange, then SendKeys.
    void PutClearTextPassword([MarshalAs(UnmanagedType.BStr)] string password);
    void PutPortablePassword([MarshalAs(UnmanagedType.BStr)] string v);
    [return: MarshalAs(UnmanagedType.BStr)] string GetPortablePassword();
    void PutPortableSalt([MarshalAs(UnmanagedType.BStr)] string v);
    [return: MarshalAs(UnmanagedType.BStr)] string GetPortableSalt();
    void PutBinaryPassword([MarshalAs(UnmanagedType.BStr)] string v);
    [return: MarshalAs(UnmanagedType.BStr)] string GetBinaryPassword();
    void PutBinarySalt([MarshalAs(UnmanagedType.BStr)] string v);
    [return: MarshalAs(UnmanagedType.BStr)] string GetBinarySalt();
    void ResetPassword();
    void NotifyRedirectDeviceChange(UIntPtr wParam, IntPtr lParam);
    void SendKeys(int numKeys, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] short[] keyUp, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] int[] keyData);
}

/// <summary>Named connection properties (0.5.0, ADR-062): "RedirectedAuthentication" = Remote Credential Guard,
/// "RestrictedLogon" = Restricted Admin mode. IUnknown-based; put_Property is its first slot.</summary>
[ComImport, Guid("302D8188-0052-4807-806A-362B628F9AC5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMsRdpExtendedSettings
{
    void PutProperty([MarshalAs(UnmanagedType.BStr)] string name, [In, MarshalAs(UnmanagedType.Struct)] ref object value);
}

/// <summary>The control's events (dispinterface IMsTscAxEvents). Only the DISPIDs used here are declared.</summary>
[ComImport, Guid("336D5562-EFA8-482E-8CB3-C5C0FC7A7DB6"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
public interface IMsTscAxEvents
{
    [DispId(1)] void OnConnecting();
    [DispId(2)] void OnConnected();
    [DispId(3)] void OnLoginComplete();
    [DispId(4)] void OnDisconnected(int discReason);
    [DispId(5)] void OnEnterFullScreenMode();
    [DispId(6)] void OnLeaveFullScreenMode();
    [DispId(10)] void OnFatalError(int errorCode);
    [DispId(11)] void OnWarning(int warningCode);
    [DispId(12)] void OnRemoteDesktopSizeChange(int width, int height);
    [DispId(14)] void OnRequestContainerMinimize();
    [DispId(22)] void OnLogonError(int error);
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class RdpEventSink : IMsTscAxEvents
{
    private readonly RdpAxHost _owner;
    internal RdpEventSink(RdpAxHost owner) { _owner = owner; }
    public void OnConnecting() => _owner.Raise(RdpEvent.Connecting);
    public void OnConnected() => _owner.Raise(RdpEvent.Connected);
    public void OnLoginComplete() => _owner.Raise(RdpEvent.LoginComplete);
    public void OnDisconnected(int discReason) => _owner.Raise(RdpEvent.Disconnected, discReason);
    public void OnEnterFullScreenMode() => _owner.Raise(RdpEvent.EnterFullScreen);
    public void OnLeaveFullScreenMode() => _owner.Raise(RdpEvent.LeaveFullScreen);
    public void OnFatalError(int errorCode) => _owner.Raise(RdpEvent.FatalError, errorCode);
    public void OnWarning(int warningCode) => _owner.Raise(RdpEvent.Warning, warningCode);
    public void OnRemoteDesktopSizeChange(int width, int height) => _owner.Raise(RdpEvent.SizeChanged, width, height);
    public void OnRequestContainerMinimize() => _owner.Raise(RdpEvent.MinimizeRequested);
    public void OnLogonError(int error) => _owner.Raise(RdpEvent.LogonError, error);
}

public enum RdpEvent { Connecting, Connected, LoginComplete, Disconnected, EnterFullScreen, LeaveFullScreen, FatalError, Warning, SizeChanged, MinimizeRequested, LogonError }

/// <summary>The ActiveX inside a WinForms AxHost. Creates the newest registered MsRdpClient version.</summary>
public sealed class RdpAxHost : System.Windows.Forms.AxHost
{
    /// <summary>MsTscAx 13 → 9 (MsRdpClient*NotSafeForScripting), newest first.</summary>
    private static readonly string[] Clsids =
    {
        "3F859AA3-C2D4-4FAA-B0E4-FD0C9C4E5E3A", "1DF7C823-B2D4-4B54-975A-F2AC5D7CF8B8", "A0C63C30-F08D-4AB4-907C-34905D770C7D",
        "8B918B82-7985-4C24-89DF-C33AD2BBFBCD", "A3BC03A0-041D-42E3-AD22-882B7865C9C5",
    };

    private static string? _clsid;
    private static bool _probed;

    /// <summary>The newest RDP control that can actually be CREATED on this PC (a registered CLSID isn't enough: some
    /// Windows builds register MsTscAx 13 without a class factory for it), or null. Probed once, on the UI thread.</summary>
    public static string? AvailableClsid()
    {
        if (_probed) return _clsid;
        _probed = true;
        foreach (var c in Clsids)
        {
            using (var k = Registry.ClassesRoot.OpenSubKey($@"CLSID\{{{c}}}\InprocServer32")) if (k == null) continue;
            object? o = null;
            try
            {
                var t = Type.GetTypeFromCLSID(new Guid(c), throwOnError: false);
                if (t == null) continue;
                o = Activator.CreateInstance(t);
                if (o != null) { _clsid = c; break; }
            }
            catch (Exception ex) { Log.Write($"RDP control {c} not creatable", ex); }
            finally { if (o != null && Marshal.IsComObject(o)) Marshal.FinalReleaseComObject(o); }
        }
        return _clsid;
    }

    private ConnectionPointCookie? _cookie;
    public event Action<RdpEvent, int[]>? Event;
    public string ClsidUsed { get; }

    public RdpAxHost(string clsid) : base(clsid) { ClsidUsed = clsid; }

    protected override void CreateSink()
    {
        try { _cookie = new ConnectionPointCookie(GetOcx()!, new RdpEventSink(this), typeof(IMsTscAxEvents)); }
        catch (Exception ex) { Log.Write("RDP event sink", ex); }
    }

    protected override void DetachSink()
    {
        try { _cookie?.Disconnect(); } catch { }
        _cookie = null;
    }

    internal void Raise(RdpEvent e, params int[] args) => Event?.Invoke(e, args);

    /// <summary>The control's IDispatch for late-bound calls.</summary>
    public dynamic Ocx => GetOcx() ?? throw new InvalidOperationException("The Remote Desktop control isn't created yet.");

    public void SetPassword(string password) => ((IMsTscNonScriptable)GetOcx()!).PutClearTextPassword(password);

    /// <summary>Sets a named connection property (IMsRdpExtendedSettings). Throws when the control doesn't support it.</summary>
    public void SetExtended(string name, object value) { var v = value; ((IMsRdpExtendedSettings)GetOcx()!).PutProperty(name, ref v); }

    /// <summary>Ctrl+Alt+Del by scan codes (key down ×3, key up ×3). Throws when the control doesn't support it.</summary>
    public void SendCtrlAltDel()
    {
        var ns = (IMsRdpClientNonScriptable)GetOcx()!;
        // Ctrl 0x1D, Alt 0x38, Del 0x53 (extended: the control expects the scan code; extended flag in bit 8)
        var data = new[] { 0x1D, 0x38, 0x153, 0x153, 0x38, 0x1D };
        var up = new short[] { 0, 0, 0, -1, -1, -1 };
        ns.SendKeys(data.Length, up, data);
    }
}
