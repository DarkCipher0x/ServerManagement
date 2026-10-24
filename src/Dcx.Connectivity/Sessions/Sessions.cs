using DcxManager.Services;
using DcxManager.Ssh;

namespace DcxManager.Sessions;

/// <summary>
/// The byte pipe behind a terminal tab (ADR-004). <see cref="Views.TerminalView"/> owns everything the user sees —
/// xterm.js, the bounded output pipeline, paste safety, links, menus, reconnect — and talks to a transport only through
/// this interface, so a new connection type is one class, not another terminal view.
/// Events may fire on any thread; the view marshals.
/// </summary>
public interface ITerminalTransport : IDisposable
{
    /// <summary>Raw output (UTF-8). The buffer is only valid during the call.</summary>
    event Action<byte[], int>? Data;
    /// <summary>The session ended on its own (network loss, server closed it, process exited). Not raised by Dispose.</summary>
    event Action<string>? Closed;
    bool IsOpen { get; }
    /// <summary>Shown in the status line once open (cipher and server version, process id…).</summary>
    string Detail { get; }
    Task OpenAsync(uint cols, uint rows, CancellationToken ct = default);
    void Write(string text);
    void Resize(uint cols, uint rows);
}

/// <summary>A transport that takes encoded bytes (SSH, Telnet), so a terminal profile's encoding applies to input too.</summary>
public interface IByteWriter { void WriteBytes(byte[] data); }

/// <summary>What a session type supports, so menus and buttons only offer what works (no fake controls).</summary>
[Flags]
public enum SessionCaps
{
    None = 0,
    /// <summary>Files (SFTP), Manage, File commander and host copy actions — a saved/ad-hoc SSH host.</summary>
    HostTools = 1,
    /// <summary>Reconnect automatically with back-off after an unexpected drop.</summary>
    AutoReconnect = 2,
    /// <summary>Snippets marked "sudo" get the prefix when the login isn't root.</summary>
    Sudo = 4,
}

/// <summary>What a terminal tab connects to. Creates a fresh transport for every (re)connect.</summary>
public abstract class SessionTarget
{
    /// <summary>"ssh", "local" or "wsl".</summary>
    public abstract string Kind { get; }
    /// <summary>Tab and window titles.</summary>
    public abstract string Title { get; }
    /// <summary>Status line ("user@host:22", "this PC · PowerShell 7", "WSL · Ubuntu").</summary>
    public abstract string Endpoint { get; }
    public abstract string Glyph { get; }
    public abstract SessionCaps Caps { get; }
    public virtual HostEntry? Host => null;
    public abstract ITerminalTransport CreateTransport();
    /// <summary>Whether an automatic retry makes sense after this connect error.</summary>
    public virtual bool ShouldRetry(Exception ex) => true;
    /// <summary>The server refused the sign-in (wrong key or password): retrying the same credential can't succeed.</summary>
    public static bool IsAuthFailure(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException) if (e is Renci.SshNet.Common.SshAuthenticationException) return true;
        return false;
    }
    /// <summary>A copy for "Duplicate session".</summary>
    public abstract SessionTarget Clone();
    /// <summary>False when a second session to the same target can't work (a serial port opens once).</summary>
    public virtual bool CanDuplicate => true;
    public bool Has(SessionCaps c) => (Caps & c) == c;
}

public sealed class SshTarget(HostEntry host) : SessionTarget
{
    public override string Kind => "ssh";
    public override string Title => host.Display;
    public override string Endpoint => host.Endpoint;
    public override string Glyph => "⌨";
    public override SessionCaps Caps => SessionCaps.HostTools | SessionCaps.AutoReconnect | SessionCaps.Sudo;
    public override HostEntry Host => host;
    public override ITerminalTransport CreateTransport() => new SshTransport(host);
    // A refused sign-in isn't retried: the same key or password fails the same way (s37: 167 retries overnight).
    public override bool ShouldRetry(Exception ex) => ex is not (HostKeyRejectedException or OperationCanceledException) && !IsAuthFailure(ex);
    public override SessionTarget Clone() => new SshTarget(host);
}

/// <summary>An SSH shell channel (SSH.NET) — the logic that used to live inside TerminalView.</summary>
public sealed class SshTransport(HostEntry host) : ITerminalTransport, IByteWriter
{
    private SshSession<Renci.SshNet.SshClient>? _session;
    private Renci.SshNet.ShellStream? _shell;
    private volatile bool _disposed;

    public event Action<byte[], int>? Data;
    public event Action<string>? Closed;
    public bool IsOpen => _session?.IsConnected == true && _shell != null && !_disposed;
    public string Detail { get; private set; } = "";

    public async Task OpenAsync(uint cols, uint rows, CancellationToken ct = default)
    {
        var session = await SshConnector.ConnectSshAsync(host, ct);
        if (_disposed) { session.Dispose(); throw new OperationCanceledException(); }
        var modes = new Dictionary<Renci.SshNet.Common.TerminalModes, uint> { [Renci.SshNet.Common.TerminalModes.ECHO] = 1 };
        var shell = session.Client.CreateShellStream("xterm-256color", cols, rows, 0, 0, 1 << 16, modes);
        shell.DataReceived += (_, e) => { if (!_disposed) Data?.Invoke(e.Data, e.Data.Length); };
        shell.Closed += (_, _) => { if (!_disposed) Closed?.Invoke("session closed"); };
        session.Client.ErrorOccurred += (_, e) => { if (!_disposed) Closed?.Invoke("connection lost: " + e.Exception.Message); };
        _session = session; _shell = shell;
        var info = session.Client.ConnectionInfo;
        Detail = $"{Ssh.SshPq.Describe(info.CurrentKeyExchangeAlgorithm)} · {info.CurrentServerEncryption} · {info.ServerVersion}";
    }

    public void Write(string text) { var s = _shell; if (s == null) return; s.Write(text); s.Flush(); }
    public void WriteBytes(byte[] data) { var s = _shell; if (s == null) return; s.Write(data, 0, data.Length); s.Flush(); }

    public void Resize(uint cols, uint rows) { try { _shell?.ChangeWindowSize(cols, rows, 0, 0); } catch { } }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var shell = _shell; var session = _session;
        _shell = null; _session = null;
        if (shell != null) { try { shell.Dispose(); } catch { } }
        if (session != null) { try { session.Dispose(); } catch { } }
    }
}
