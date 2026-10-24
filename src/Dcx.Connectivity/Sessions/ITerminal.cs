using DcxManager.Services;

namespace DcxManager.Sessions;

/// <summary>
/// A live terminal tab as the code below the screens sees it (0.2.0 phase 6): the AI Engineer's terminal bridge and the
/// add-ons' terminal capabilities work through this; the screen (<c>Views.TerminalView</c>) implements it.
/// </summary>
public interface ITerminal
{
    /// <summary>A short id for this tab's session, shown wherever the AI Engineer targets it.</summary>
    string SessionId { get; }
    /// <summary>Counts successful (re)connects: a target chosen before a reconnect is stale and refused.</summary>
    int Epoch { get; }
    SessionTarget Target { get; }
    /// <summary>The SSH host, or null for a local shell / WSL tab.</summary>
    HostEntry? Host { get; }
    /// <summary>"posix", "powershell", or "" when commands can't be wrapped reliably.</summary>
    string Shell { get; }
    bool IsConnected { get; }
    bool IsDisposed { get; }
    /// <summary>The session's environment label (PRODUCTION, LOCAL …), or "".</summary>
    string Environment { get; }
    /// <summary>Decoded output as it arrives (any thread).</summary>
    event Action<string>? OutputText;
    /// <summary>Text for the shell, exactly as typed input.</summary>
    void BridgeWrite(string text);
    /// <summary>The last output (raw, bounded).</summary>
    string RecentOutput(int maxChars);
    /// <summary>The rows the terminal shows now, or null when the page can't answer.</summary>
    Task<IReadOnlyList<string>?> ScreenLinesAsync();
}

/// <summary>The open terminal tabs (the app registers how to list them at start-up; none without a UI).</summary>
public static class Terminals
{
    public static Func<IEnumerable<ITerminal>> Open { get; set; } = () => Array.Empty<ITerminal>();
    /// <summary>The terminal tab in front (the active pane's tab), or null (0.6.10: the AI Engineer's default target).</summary>
    public static Func<ITerminal?> Front { get; set; } = () => null;
}
