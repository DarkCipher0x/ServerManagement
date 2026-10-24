using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DcxManager.Sessions;

/// <summary>
/// A local console program (PowerShell, cmd, wsl.exe…) attached to a Windows pseudo console (ConPTY, Windows 10 1809+).
/// ConPTY speaks VT sequences both ways, so the same xterm.js front end works unchanged. No window is ever shown: the
/// console host runs headless and all I/O goes through two anonymous pipes. The child runs as the current user with the
/// app's privileges (never elevated).
/// </summary>
public sealed class ConPtyTransport : ITerminalTransport
{
    private readonly string _commandLine;
    private readonly string? _workingDir;
    private IntPtr _hpc, _process, _thread;
    private FileStream? _input, _output;
    private int _pid;
    private volatile bool _disposed;
    private readonly object _gate = new();

    public event Action<byte[], int>? Data;
    public event Action<string>? Closed;
    public bool IsOpen => _hpc != IntPtr.Zero && !_disposed && !HasExited;
    public string Detail => _pid == 0 ? "" : $"local process {_pid} · ConPTY";
    public int ProcessId => _pid;

    public ConPtyTransport(string commandLine, string? workingDir)
    {
        _commandLine = commandLine;
        _workingDir = workingDir is { Length: > 0 } d && Directory.Exists(d) ? d : null;
    }

    public Task OpenAsync(uint cols, uint rows, CancellationToken ct = default) => Task.Run(() => Open(cols, rows), ct);

    private void Open(uint cols, uint rows)
    {
        if (!CreatePipe(out var inRead, out var inWrite, IntPtr.Zero, 0)) throw new Win32Exception();
        if (!CreatePipe(out var outRead, out var outWrite, IntPtr.Zero, 0)) { inRead.Dispose(); inWrite.Dispose(); throw new Win32Exception(); }
        IntPtr attrs = IntPtr.Zero;
        try
        {
            int hr = CreatePseudoConsole(Size(cols, rows), inRead, outWrite, 0, out _hpc);
            if (hr != 0) throw new Win32Exception(hr, "CreatePseudoConsole failed (needs Windows 10 1809 or later)");

            IntPtr size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attrs = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attrs, 1, 0, ref size)) throw new Win32Exception();
            if (!UpdateProcThreadAttribute(attrs, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, _hpc, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception();

            var si = new STARTUPINFOEX();
            si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
            // Null std handles + STARTF_USESTDHANDLES: the child must use the pseudo console, never handles this
            // process happens to have (e.g. when started from a redirected shell).
            si.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
            si.lpAttributeList = attrs;
            var cmd = new StringBuilder(_commandLine);
            if (!CreateProcessW(null, cmd, IntPtr.Zero, IntPtr.Zero, false, EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT, IntPtr.Zero, _workingDir, ref si, out var pi))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not start " + _commandLine);
            _process = pi.hProcess; _thread = pi.hThread; _pid = pi.dwProcessId;
        }
        catch
        {
            if (_hpc != IntPtr.Zero) { ClosePseudoConsole(_hpc); _hpc = IntPtr.Zero; }
            inWrite.Dispose(); outRead.Dispose();
            throw;
        }
        finally
        {
            if (attrs != IntPtr.Zero) { DeleteProcThreadAttributeList(attrs); Marshal.FreeHGlobal(attrs); }
            // The pseudo console holds its own duplicates of these ends.
            inRead.Dispose(); outWrite.Dispose();
        }
        _input = new FileStream(inWrite, FileAccess.Write, 1, false);
        _output = new FileStream(outRead, FileAccess.Read, 1, false);
        new Thread(ReadLoop) { IsBackground = true, Name = "conpty-read " + _pid }.Start();
        new Thread(WaitExit) { IsBackground = true, Name = "conpty-exit " + _pid }.Start();
    }

    private void ReadLoop()
    {
        var buf = new byte[16384];
        try
        {
            var o = _output;
            while (o != null && !_disposed)
            {
                int n = o.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                Data?.Invoke(buf, n);
            }
        }
        catch { /* pipe closed during teardown */ }
    }

    private void WaitExit()
    {
        var p = _process;
        if (p == IntPtr.Zero) return;
        WaitForSingleObject(p, INFINITE);
        if (_disposed) return;
        GetExitCodeProcess(p, out var code);
        // Give the reader a moment to deliver the last output, then end the session.
        Thread.Sleep(150);
        if (!_disposed) Closed?.Invoke($"process exited (code {code})");
    }

    private bool HasExited => _process == IntPtr.Zero || WaitForSingleObject(_process, 0) == 0;

    public void Write(string text)
    {
        var i = _input;
        if (i == null || _disposed) return;
        var bytes = Encoding.UTF8.GetBytes(text);
        lock (_gate) { i.Write(bytes, 0, bytes.Length); i.Flush(); }
    }

    public void Resize(uint cols, uint rows)
    {
        if (_hpc != IntPtr.Zero && !_disposed) ResizePseudoConsole(_hpc, Size(cols, rows));
    }

    /// <summary>Closes the console (which ends attached programs), then makes sure the process is gone. Runs the
    /// potentially blocking ConPTY close off the UI thread.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var hpc = _hpc; var proc = _process; var thread = _thread; var input = _input; var output = _output;
        _hpc = IntPtr.Zero; _process = IntPtr.Zero; _thread = IntPtr.Zero; _input = null; _output = null;
        try { input?.Dispose(); } catch { }
        Task.Run(() =>
        {
            try { if (hpc != IntPtr.Zero) ClosePseudoConsole(hpc); } catch { }
            try { output?.Dispose(); } catch { }
            if (proc != IntPtr.Zero)
            {
                if (WaitForSingleObject(proc, 2000) != 0) { TerminateProcess(proc, 1); WaitForSingleObject(proc, 5000); }
                // Only closed once the process has ended, so the exit-watcher's wait has already returned.
                CloseHandle(proc);
            }
            if (thread != IntPtr.Zero) CloseHandle(thread);
        });
    }

    private static COORD Size(uint cols, uint rows) => new() { X = (short)Math.Clamp(cols, 10, 1000), Y = (short)Math.Clamp(rows, 4, 1000) };

    // ---- Win32 -------------------------------------------------------------------------------------------------------

    private const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000, CREATE_UNICODE_ENVIRONMENT = 0x00000400, INFINITE = 0xFFFFFFFF;
    private const int STARTF_USESTDHANDLES = 0x00000100;

    [StructLayout(LayoutKind.Sequential)] private struct COORD { public short X, Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb; public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)] private struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }
    [StructLayout(LayoutKind.Sequential)] private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attrs, int size);
    [DllImport("kernel32.dll")] private static extern int CreatePseudoConsole(COORD size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr hpc);
    [DllImport("kernel32.dll")] private static extern int ResizePseudoConsole(IntPtr hpc, COORD size);
    [DllImport("kernel32.dll")] private static extern void ClosePseudoConsole(IntPtr hpc);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attr, IntPtr value, IntPtr size, IntPtr prev, IntPtr retSize);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string? app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string? cwd, ref STARTUPINFOEX si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(IntPtr h, out uint code);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr h, uint code);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
}
