using System.IO;
using System.IO.Ports;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;
using DcxManager.Services;

namespace DcxManager.Sessions;

/// <summary>A serial port Windows currently reports, with the USB identity when it is a USB adapter.</summary>
public sealed record SerialPortInfo(string Port, string Description, string UsbId)
{
    public string Display => Description.Length > 0 ? $"{Port} — {Description}" : Port;
}

public static class SerialPorts
{
    public static readonly int[] CommonBauds = { 300, 1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600 };

    /// <summary>Present ports: names from the serial driver plus friendly names / USB ids from WMI (best effort).</summary>
    public static List<SerialPortInfo> List()
    {
        var names = new HashSet<string>(SafeNames(), StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, SerialPortInfo>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var q = new ManagementObjectSearcher("SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE PNPClass = 'Ports'");
            q.Options.Timeout = TimeSpan.FromSeconds(4);
            foreach (ManagementObject o in q.Get())
            {
                var name = o["Name"] as string ?? "";
                var m = Regex.Match(name, @"\((COM\d+)\)");
                if (!m.Success) continue;
                var port = m.Groups[1].Value;
                result[port] = new SerialPortInfo(port, name.Replace(m.Value, "").Trim(), UsbIdFrom(o["PNPDeviceID"] as string));
                names.Add(port);
            }
        }
        catch { }
        foreach (var n in names) result.TryAdd(n, new SerialPortInfo(n, "", ""));
        return result.Values.OrderBy(p => PortNumber(p.Port)).ThenBy(p => p.Port).ToList();
    }

    private static IEnumerable<string> SafeNames() { try { return SerialPort.GetPortNames(); } catch { return Array.Empty<string>(); } }

    public static int PortNumber(string port) => int.TryParse(Regex.Match(port, @"\d+$").Value, out var n) ? n : int.MaxValue;

    /// <summary>"USB\VID_0403&amp;PID_6001\A9ABC" or "FTDIBUS\VID_0403+PID_6001+A9ABCA\0000" → "VID_0403&amp;PID_6001\A9ABC".</summary>
    public static string UsbIdFrom(string? pnp)
    {
        if (string.IsNullOrEmpty(pnp)) return "";
        var m = Regex.Match(pnp, @"VID_([0-9A-F]{4})[&+]PID_([0-9A-F]{4})(?:[\\+]([^\\]+))?", RegexOptions.IgnoreCase);
        if (!m.Success) return "";
        var serial = m.Groups[3].Success ? m.Groups[3].Value : "";
        return $"VID_{m.Groups[1].Value.ToUpperInvariant()}&PID_{m.Groups[2].Value.ToUpperInvariant()}" + (serial.Length > 0 ? "\\" + serial : "");
    }

    /// <summary>The port to open now: the saved name if it is present; else, for a USB adapter, wherever the same
    /// device (VID/PID/serial) currently is. Windows renumbers adapters moved between USB sockets.</summary>
    public static string? Resolve(SerialProfile p, IReadOnlyList<SerialPortInfo> present)
    {
        var byName = present.FirstOrDefault(x => x.Port.Equals(p.Port, StringComparison.OrdinalIgnoreCase));
        if (p.UsbId.Length == 0) return byName?.Port;
        var exact = present.FirstOrDefault(x => x.UsbId.Equals(p.UsbId, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact.Port;
        // The saved COM name now belongs to a DIFFERENT USB device: never open someone else's console by accident.
        // Same VID/PID without a matching serial (adapters with no serial number), or identity unknown: accept the name.
        if (byName == null) return null;
        var vidPid = p.UsbId.Split('\\')[0];
        return byName.UsbId.Length == 0 || byName.UsbId.StartsWith(vidPid, StringComparison.OrdinalIgnoreCase) ? byName.Port : null;
    }

    /// <summary>Quick connect: "COM3", "com3:115200", "COM3 115200 8N1".</summary>
    public static SerialProfile? FromQuick(string text)
    {
        var m = Regex.Match(text.Trim(), @"^(?<p>COM\d{1,3})(?:[:\s,]+(?<b>\d{3,7}))?(?:[:\s,]+(?<f>[5-8][NEOMS][12]))?$", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        var p = new SerialProfile { Name = m.Groups["p"].Value.ToUpperInvariant(), Port = m.Groups["p"].Value.ToUpperInvariant() };
        if (m.Groups["b"].Success) p.Baud = int.Parse(m.Groups["b"].Value);
        if (m.Groups["f"].Success)
        {
            var f = m.Groups["f"].Value.ToUpperInvariant();
            p.DataBits = f[0] - '0';
            p.Parity = f[1] switch { 'E' => Parity.Even, 'O' => Parity.Odd, 'M' => Parity.Mark, 'S' => Parity.Space, _ => Parity.None };
            p.StopBits = f[2] == '2' ? StopBits.Two : StopBits.One;
        }
        return p;
    }

    /// <summary>Settings sanity check before opening (also used by the editor).</summary>
    public static string? Validate(SerialProfile p)
    {
        if (!Regex.IsMatch(p.Port ?? "", @"^COM\d{1,3}$", RegexOptions.IgnoreCase)) return "Port must look like COM3.";
        if (p.Baud is < 50 or > 4_000_000) return "Baud rate must be between 50 and 4,000,000.";
        if (p.DataBits is < 5 or > 8) return "Data bits must be 5–8.";
        if (p.StopBits == StopBits.None) return "Stop bits can't be None.";
        if (p.NewLine is not ("cr" or "lf" or "crlf")) return "Enter must send CR, LF or CR+LF.";
        return null;
    }
}

/// <summary>Device control that only serial sessions have (BREAK for ROMMON / password recovery, DTR/RTS).</summary>
public interface ISerialControl
{
    void SendBreak(int milliseconds = 300);
    string PortName { get; }
}

/// <summary>
/// A serial port as a terminal transport. Output bytes are converted to UTF-8 when the device uses another encoding;
/// Enter is mapped to the device's line ending; optional local echo. Unplugging the adapter ends the session with
/// "device removed" and, if the profile allows, TerminalView's back-off reconnect re-resolves the port (following a
/// USB adapter to its new COM number). The stream factory is injectable so the self-test can run without hardware.
/// </summary>
public sealed class SerialTransport : ITerminalTransport, ISerialControl
{
    private readonly SerialProfile _p;
    private readonly Func<SerialProfile, (Stream Stream, string Port, Action<int>? Break, IDisposable Owner)> _open;
    private Stream? _stream;
    private IDisposable? _owner;
    private Action<int>? _break;
    private volatile bool _disposed;
    private readonly System.Text.Encoding _enc;
    private readonly Decoder? _decoder;
    private readonly object _gate = new();

    public event Action<byte[], int>? Data;
    public event Action<string>? Closed;
    public bool IsOpen => _stream != null && !_disposed;
    public string Detail { get; private set; } = "";
    public string PortName { get; private set; } = "";

    public SerialTransport(SerialProfile profile, Func<SerialProfile, (Stream, string, Action<int>?, IDisposable)>? open = null)
    {
        _p = profile;
        _open = open ?? OpenReal;
        _enc = profile.Encoding switch { "latin1" => System.Text.Encoding.Latin1, "ascii" => System.Text.Encoding.ASCII, _ => System.Text.Encoding.UTF8 };
        _decoder = _enc is UTF8Encoding ? null : _enc.GetDecoder();
    }

    public Task OpenAsync(uint cols, uint rows, CancellationToken ct = default) => Task.Run(() =>
    {
        if (SerialPorts.Validate(_p) is { } bad) throw new ArgumentException(bad);
        var (stream, port, brk, owner) = _open(_p);
        if (_disposed) { owner.Dispose(); throw new OperationCanceledException(); }
        _stream = stream; _owner = owner; _break = brk; PortName = port;
        Detail = $"{port} · {_p.Baud} {_p.Framing}" + (_p.Handshake != Handshake.None ? " · " + _p.Handshake : "") + (_p.UsbId.Length > 0 ? " · USB " + _p.UsbId.Split('\\')[0] : "");
        new Thread(ReadLoop) { IsBackground = true, Name = "serial-read " + port }.Start();
    }, ct);

    private static (Stream, string, Action<int>?, IDisposable) OpenReal(SerialProfile p)
    {
        var present = SerialPorts.List();
        var port = SerialPorts.Resolve(p, present)
                   ?? throw new IOException($"{p.Port} is not present{(p.UsbId.Length > 0 ? " (the USB adapter isn't plugged in)" : "")}.");
        var sp = new SerialPort(port, p.Baud, p.Parity, p.DataBits, p.StopBits)
        {
            Handshake = p.Handshake, DtrEnable = p.DtrEnable, RtsEnable = p.RtsEnable,
            ReadTimeout = SerialPort.InfiniteTimeout, WriteTimeout = 3000, ReadBufferSize = 64 * 1024,
        };
        try { sp.Open(); }
        catch (UnauthorizedAccessException) { sp.Dispose(); throw new IOException($"{port} is in use by another program (close PuTTY/other consoles using it)."); }
        catch { sp.Dispose(); throw; }
        return (sp.BaseStream, port, ms => { sp.BreakState = true; Thread.Sleep(ms); sp.BreakState = false; }, sp);
    }

    private void ReadLoop()
    {
        var buf = new byte[8192];
        string reason = "device removed or port closed";
        try
        {
            var s = _stream;
            while (s != null && !_disposed)
            {
                int n = s.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                Deliver(buf, n);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or ObjectDisposedException or OperationCanceledException)
        {
            if (ex is IOException io && !io.Message.Contains("aborted", StringComparison.OrdinalIgnoreCase)) reason = "device removed: " + io.Message.TrimEnd('.');
        }
        if (!_disposed) Closed?.Invoke(reason);
    }

    private void Deliver(byte[] buf, int n)
    {
        if (_decoder == null) { Data?.Invoke(buf, n); return; }
        var chars = new char[_decoder.GetCharCount(buf, 0, n)];
        _decoder.GetChars(buf, 0, n, chars, 0);
        var utf8 = System.Text.Encoding.UTF8.GetBytes(chars);
        Data?.Invoke(utf8, utf8.Length);
    }

    /// <summary>xterm sends CR for Enter; the device gets its configured line ending.</summary>
    public static string MapNewLines(string text, string newLine) => newLine switch
    {
        "lf" => text.Replace("\r\n", "\n").Replace('\r', '\n'),
        "crlf" => Regex.Replace(text, "\r\n|\r|\n", "\r\n"),
        _ => text,
    };

    public void Write(string text)
    {
        var s = _stream;
        if (s == null || _disposed) return;
        var mapped = MapNewLines(text, _p.NewLine);
        var bytes = _enc.GetBytes(mapped);
        lock (_gate) { s.Write(bytes, 0, bytes.Length); s.Flush(); }
        if (_p.LocalEcho)
        {
            var echo = System.Text.Encoding.UTF8.GetBytes(Regex.Replace(mapped, "\r\n|\r|\n", "\r\n"));
            Data?.Invoke(echo, echo.Length);
        }
    }

    public void Resize(uint cols, uint rows) { /* serial has no window size */ }

    public void SendBreak(int milliseconds = 300)
    {
        var b = _break;
        if (b == null || _disposed) return;
        lock (_gate) b(Math.Clamp(milliseconds, 50, 2000));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var owner = _owner; var s = _stream;
        _owner = null; _stream = null; _break = null;
        try { s?.Dispose(); } catch { }
        try { owner?.Dispose(); } catch { }
    }
}

/// <summary>A serial console in a terminal tab. Reconnects (re-resolving the port) when the profile says so.</summary>
public sealed class SerialTarget(SerialProfile profile) : SessionTarget
{
    public SerialProfile Profile => profile;
    public override string Kind => "serial";
    public override string Title => profile.Name;
    public override string Endpoint => $"serial {profile.Summary}";
    public override string Glyph => "≋";
    public override SessionCaps Caps => profile.Reconnect ? SessionCaps.AutoReconnect : SessionCaps.None;
    public override ITerminalTransport CreateTransport() => new SerialTransport(profile);
    /// <summary>Wrong settings won't fix themselves; a missing/busy device might (replug, other app closes).</summary>
    public override bool ShouldRetry(Exception ex) => ex is not (ArgumentException or OperationCanceledException);
    public override SessionTarget Clone() => new SerialTarget(profile);
    public override bool CanDuplicate => false;
}
