using System.IO.Ports;

namespace DcxManager.Sessions;

// Stored records (0.2.0 phase 6): kept in the encrypted store, so they live in Dcx.Core with the store; the code that uses them stays here.

/// <summary>One key mapping: a chord ("Ctrl+Shift+F5") and what it sends (escapes allowed, see <see cref="TerminalProfiles.ParseSend"/>).</summary>
public sealed class KeyMap
{
    public string Keys { get; set; } = "";
    public string Send { get; set; } = "";
}

/// <summary>A named terminal profile (ADR-019), stored in the encrypted settings. A host may name one; a tab may switch
/// to another for that session only.</summary>
public sealed class TerminalProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Default";
    /// <summary>"app" (follows the app theme) or a key of <see cref="TerminalProfiles.Schemes"/>.</summary>
    public string Scheme { get; set; } = "app";
    /// <summary>"block", "underline" or "bar".</summary>
    public string CursorStyle { get; set; } = "block";
    public bool CursorBlink { get; set; } = true;
    /// <summary>"utf-8", "iso-8859-1", "windows-1252" or "ibm437" (SSH and Telnet; local shells are always UTF-8).</summary>
    public string Encoding { get; set; } = "utf-8";
    /// <summary>Lines kept; 0 = Settings → Terminal's value.</summary>
    public int Scrollback { get; set; }
    public bool BackspaceCtrlH { get; set; }
    /// <summary>Log every connected session's output (encrypted, Sessions/SessionLog.cs).</summary>
    public bool Log { get; set; }
    public bool LogTimestamps { get; set; } = true;
    public List<KeyMap> Keys { get; set; } = new();

    public override string ToString() => Name;

    public TerminalProfile Copy(string name) => new()
    {
        Name = name, Scheme = Scheme, CursorStyle = CursorStyle, CursorBlink = CursorBlink, Encoding = Encoding, Scrollback = Scrollback,
        BackspaceCtrlH = BackspaceCtrlH, Log = Log, LogTimestamps = LogTimestamps, Keys = Keys.Select(k => new KeyMap { Keys = k.Keys, Send = k.Send }).ToList(),
    };
}

/// <summary>A saved serial / USB-serial console (stored encrypted in data.dcx with the hosts).</summary>
public sealed class SerialProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Serial console";
    /// <summary>COM port name. For USB adapters it is re-resolved from <see cref="UsbId"/> when Windows renumbers it.</summary>
    public string Port { get; set; } = "COM1";
    /// <summary>"VID_0403&amp;PID_6001\A9xxxx" (vendor, product, serial) of a USB adapter, or empty for a fixed port.</summary>
    public string UsbId { get; set; } = "";
    public int Baud { get; set; } = 9600;
    public int DataBits { get; set; } = 8;
    public Parity Parity { get; set; } = Parity.None;
    public StopBits StopBits { get; set; } = StopBits.One;
    public Handshake Handshake { get; set; } = Handshake.None;
    /// <summary>"utf-8", "latin1" or "ascii" (bytes are converted to UTF-8 for the terminal).</summary>
    public string Encoding { get; set; } = "utf-8";
    /// <summary>What Enter sends: "cr" (most network gear), "lf" or "crlf".</summary>
    public string NewLine { get; set; } = "cr";
    public bool LocalEcho { get; set; }
    public bool DtrEnable { get; set; } = true;
    public bool RtsEnable { get; set; } = true;
    /// <summary>Re-open automatically (with back-off) when the adapter is unplugged and comes back.</summary>
    public bool Reconnect { get; set; } = true;
    public string Notes { get; set; } = "";

    public string Framing => $"{DataBits}{Parity.ToString()[0]}{(StopBits == StopBits.One ? "1" : StopBits == StopBits.Two ? "2" : "1.5")}";
    public string Summary => $"{Port} · {Baud} {Framing}" + (Handshake != Handshake.None ? " · " + Handshake : "");
    public SerialProfile Copy() => (SerialProfile)MemberwiseClone();
}

/// <summary>Session log file names (the data mover lists them with the store; the logging itself is in SessionLog).</summary>
public static class SessionLogFiles
{
    public const string Ext = ".dcxslog";
}
