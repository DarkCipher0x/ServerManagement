using System.IO;
using System.Net.Sockets;
using DcxManager.Services;

namespace DcxManager.Sessions;

/// <summary>A Telnet host (legacy, unencrypted; ADR-019). Only usable while Settings → Terminal → "Allow Telnet" is on.</summary>
public sealed class TelnetTarget(HostEntry host) : SessionTarget
{
    public override string Kind => "telnet";
    public override string Title => host.Display;
    public override string Endpoint => host.Port == 23 ? $"telnet {host.Address}" : $"telnet {host.Address}:{host.Port}";
    public override string Glyph => "⌁";
    public override SessionCaps Caps => SessionCaps.AutoReconnect;
    public override HostEntry Host => host;
    public override ITerminalTransport CreateTransport() => new TelnetTransport(host.Address, host.Port);
    public override bool ShouldRetry(Exception ex) => ex is not (OfflineBlockedException or TelnetDisabledException or OperationCanceledException);
    public override SessionTarget Clone() => new TelnetTarget(host);
}

public sealed class TelnetDisabledException() : InvalidOperationException(
    "Telnet is switched off. It sends everything, passwords included, unencrypted; turn it on in Settings → Terminal & connections → \"Allow Telnet\" if this legacy device has nothing else.");

/// <summary>
/// Telnet over TCP with the minimum option negotiation a modern device expects: ECHO and SGA from the server, NAWS (window
/// size) and TTYPE ("XTERM-256COLOR") from us; every other option is refused. IAC bytes are escaped in both directions and
/// Enter is sent as CR LF (Telnet's newline). Output bytes are passed through as they come, so the terminal's encoding
/// applies.
/// </summary>
public sealed class TelnetTransport(string address, int port) : ITerminalTransport, IByteWriter
{
    internal const byte IAC = 255, DONT = 254, DO = 253, WONT = 252, WILL = 251, SB = 250, SE = 240;
    internal const byte ECHO = 1, SGA = 3, TTYPE = 24, NAWS = 31, IS = 0, SEND = 1;
    public const string TerminalType = "XTERM-256COLOR";

    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private readonly object _writeGate = new();
    private volatile bool _disposed;
    private bool _naws;
    private uint _cols = 80, _rows = 24;
    private readonly HashSet<(byte, byte)> _answered = new();

    public event Action<byte[], int>? Data;
    public event Action<string>? Closed;
    public bool IsOpen => _stream != null && !_disposed && _tcp?.Connected == true;
    public string Detail => "⚠ Telnet: unencrypted";
    /// <summary>Options negotiated (self-test).</summary>
    internal bool NawsOn => _naws;

    public async Task OpenAsync(uint cols, uint rows, CancellationToken ct = default)
    {
        if (!AppSettings.Current.TelnetEnabled) throw new TelnetDisabledException();
        _cols = cols; _rows = rows;
        var target = address;
        if (Offline.On)
        {
            var d = await Offline.DecideAsync(address, ct);
            if (!d.Allowed) throw Offline.Block(address, "Telnet", d.Reason);
            if (d.Addresses.Length > 0) target = (d.Addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? d.Addresses[0]).ToString();
        }
        var tcp = new TcpClient { NoDelay = true };
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try { await tcp.ConnectAsync(target, port, timeout.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { tcp.Dispose(); throw new IOException($"{address}:{port} did not answer within 15 s."); }
            catch { tcp.Dispose(); throw; }
        }
        if (_disposed) { tcp.Dispose(); throw new OperationCanceledException(); }
        _tcp = tcp; _stream = tcp.GetStream();
        new Thread(ReadLoop) { IsBackground = true, Name = "telnet-read " + address }.Start();
    }

    private void ReadLoop()
    {
        var buf = new byte[8192];
        var outBuf = new byte[8192];
        string reason = "connection closed by the device";
        var parser = new TelnetParser(this);
        try
        {
            var s = _stream;
            while (s != null && !_disposed)
            {
                int n = s.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                int m = parser.Feed(buf, n, outBuf);
                if (m > 0) Data?.Invoke(outBuf, m);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException or InvalidOperationException)
        {
            reason = "connection lost: " + ex.Message.TrimEnd('.');
        }
        if (!_disposed) Closed?.Invoke(reason);
    }

    /// <summary>The device asked or offered an option: answer once per (verb, option), so a chatty device can't loop us.</summary>
    internal void OnOption(byte verb, byte option)
    {
        byte? reply = (verb, option) switch
        {
            (DO, NAWS) => WILL,
            (DO, TTYPE) => WILL,
            (DO, SGA) => WILL,
            (WILL, ECHO) => DO,
            (WILL, SGA) => DO,
            (DO, _) => WONT,
            (WILL, _) => DONT,
            _ => null,   // WONT / DONT: nothing to agree to
        };
        if (reply is not { } r || !_answered.Add((verb, option))) return;
        Send(new[] { IAC, r, option });
        if (verb == DO && option == NAWS) { _naws = true; SendNaws(); }
    }

    internal void OnSubnegotiation(byte option, List<byte> data)
    {
        if (option == TTYPE && data.Count > 0 && data[0] == SEND)
        {
            var name = System.Text.Encoding.ASCII.GetBytes(TerminalType);
            Send(new[] { IAC, SB, TTYPE, IS }.Concat(name).Concat(new[] { IAC, SE }).ToArray());
        }
    }

    private void SendNaws()
    {
        var w = (ushort)Math.Min(_cols, 65535); var h = (ushort)Math.Min(_rows, 65535);
        var payload = new[] { (byte)(w >> 8), (byte)w, (byte)(h >> 8), (byte)h };
        var msg = new List<byte> { IAC, SB, NAWS };
        foreach (var b in payload) { msg.Add(b); if (b == IAC) msg.Add(IAC); }
        msg.AddRange(new[] { IAC, SE });
        Send(msg.ToArray());
    }

    private void Send(byte[] raw)
    {
        var s = _stream;
        if (s == null || _disposed) return;
        try { lock (_writeGate) { s.Write(raw, 0, raw.Length); s.Flush(); } }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    public void Write(string text) => WriteBytes(System.Text.Encoding.UTF8.GetBytes(text));

    /// <summary>Data from the keyboard: IAC doubled, a lone CR (xterm's Enter) sent as CR LF.</summary>
    public void WriteBytes(byte[] data) => Send(Escape(data));

    internal static byte[] Escape(byte[] data)
    {
        var o = new List<byte>(data.Length + 8);
        for (int i = 0; i < data.Length; i++)
        {
            var b = data[i];
            if (b == IAC) { o.Add(IAC); o.Add(IAC); }
            else if (b == '\r') { o.Add((byte)'\r'); o.Add((byte)'\n'); if (i + 1 < data.Length && data[i + 1] == '\n') i++; }
            else o.Add(b);
        }
        return o.ToArray();
    }

    public void Resize(uint cols, uint rows)
    {
        _cols = cols; _rows = rows;
        if (_naws) SendNaws();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var s = _stream; var t = _tcp;
        _stream = null; _tcp = null;
        try { s?.Dispose(); } catch { }
        try { t?.Dispose(); } catch { }
    }
}

/// <summary>The Telnet byte stream → data bytes plus option events (state survives across reads).</summary>
internal sealed class TelnetParser(TelnetTransport owner)
{
    private int _state;   // 0 data, 1 IAC, 2 verb (option byte next), 3 in SB, 4 IAC inside SB
    private byte _verb, _sbOption;
    private bool _sbHaveOption;
    private readonly List<byte> _sb = new();

    public int Feed(byte[] buf, int n, byte[] output)
    {
        int m = 0;
        for (int i = 0; i < n; i++)
        {
            byte b = buf[i];
            switch (_state)
            {
                case 0:
                    if (b == TelnetTransport.IAC) _state = 1;
                    else if (b != 0) output[m++] = b;   // NUL after CR is padding
                    break;
                case 1:
                    if (b == TelnetTransport.IAC) { output[m++] = b; _state = 0; }
                    else if (b is TelnetTransport.WILL or TelnetTransport.WONT or TelnetTransport.DO or TelnetTransport.DONT) { _verb = b; _state = 2; }
                    else if (b == TelnetTransport.SB) { _sb.Clear(); _sbHaveOption = false; _state = 3; }
                    else _state = 0;   // NOP, GA, AYT… ignored
                    break;
                case 2:
                    owner.OnOption(_verb, b);
                    _state = 0;
                    break;
                case 3:
                    if (b == TelnetTransport.IAC) _state = 4;
                    else if (!_sbHaveOption) { _sbOption = b; _sbHaveOption = true; }
                    else if (_sb.Count < 1024) _sb.Add(b);
                    break;
                case 4:
                    if (b == TelnetTransport.SE) { owner.OnSubnegotiation(_sbOption, _sb); _state = 0; }
                    else { if (_sb.Count < 1024) _sb.Add(b); _state = 3; }   // IAC IAC inside SB = a data 255
                    break;
            }
        }
        return m;
    }
}
