using System.Runtime.CompilerServices;

namespace DcxManager.Services;

/// <summary>WinForms is in the process only for the embedded Remote Desktop control (an AxHost, ADR-013) and the tray
/// balloon (NotifyIcon). Its own "unhandled exception" box must never show: errors go to ours. That mode used to be set in
/// OnStartup, which loaded System.Windows.Forms (with System.Configuration and System.Private.Xml: 26 MB of images) in every
/// start; since 0.6.32 (ADR-110) the first WinForms user calls <see cref="Ensure"/> instead. The mode can be set at any time
/// before a WinForms message loop runs, and a WPF app never runs one.</summary>
internal static class WinFormsSetup
{
    private static bool _done;

    /// <summary>Never inlined: compiling a caller must not pull the WinForms assembly in.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Ensure()
    {
        if (_done) return;
        _done = true;
        System.Windows.Forms.Application.SetUnhandledExceptionMode(System.Windows.Forms.UnhandledExceptionMode.ThrowException);
    }
}
