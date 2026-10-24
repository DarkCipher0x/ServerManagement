using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using DcxManager.Services;

namespace DcxManager.Vnc;

/// <summary>What a VNC connection may use and how it was reached (0.5.1 #19, ADR-069).</summary>
public sealed class RfbOptions
{
    /// <summary>The user name for Apple Remote Desktop (macOS account) and VeNCrypt Plain; empty for a VNC password only.</summary>
    public string Username { get; init; } = "";
    public string Password { get; init; } = "";
    /// <summary>True when the bytes already travel encrypted (an SSH tunnel): the weaker VNC security types are then fine,
    /// and VeNCrypt Plain (a password in clear inside the stream) is allowed. False on a direct network connection.</summary>
    public bool TransportSecure { get; init; }
    /// <summary>Share the desktop with other viewers (true) or ask the server to disconnect them (false).</summary>
    public bool Shared { get; init; } = true;
    /// <summary>The server name for TLS (VeNCrypt X.509).</summary>
    public string TlsHost { get; init; } = "";
    /// <summary>The pinned SHA-256 of the server's TLS certificate (hex, empty = none yet).</summary>
    public string TlsPin { get; init; } = "";
    /// <summary>Asked when a self-signed certificate has no pin yet: (SHA-256 hex, subject) → trust it? The answer is pinned
    /// by the caller. Null = refuse unpinned certificates.</summary>
    public Func<string, string, bool>? TrustCertificate { get; init; }
    /// <summary>Upper bound on the handshake (connect, security, server init).</summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(20);
}

/// <summary>A VNC protocol error or refusal (the message is safe to show; it never contains the password).</summary>
public sealed class RfbException(string message) : Exception(message);

/// <summary>
/// A VNC (RFB) client written for this app (0.5.1 #19, ADR-069): RFB 3.3, 3.7 and 3.8 (and Apple's 003.889, treated as
/// 3.8), the security types None, VNC password, Apple Remote Desktop (type 30: macOS user name and password, Diffie-Hellman
/// + AES-128) and VeNCrypt (X.509 TLS with certificate pinning, then None, VNC or Plain), and the encodings Raw, CopyRect,
/// RRE, Hextile and ZRLE plus the DesktopSize and LastRect pseudo-encodings. The framebuffer is 32-bit BGRA kept in
/// <see cref="Pixels"/>; screens copy from it on <see cref="Updated"/>. Every length the server sends is bounded before
/// anything is allocated, and every rectangle is checked against the framebuffer.
/// </summary>
public sealed partial class RfbClient : IDisposable
{
    public const int MaxDimension = 16384;
    public const int MaxCutText = 1 << 20;

    private Stream _stream;
    private RfbReader _in;
    private readonly object _write = new();
    private Thread? _loop;
    private volatile bool _closed;

    /// <summary>"3.8", "3.7" or "3.3" (what was agreed).</summary>
    public string Version { get; private set; } = "";
    /// <summary>What protected the session, e.g. "Apple Remote Desktop (macOS account)" or "VeNCrypt X.509 + VNC password".</summary>
    public string Security { get; private set; } = "";
    /// <summary>True when the session's bytes are encrypted end to end (an SSH tunnel or VeNCrypt TLS).</summary>
    public bool Encrypted { get; private set; }
    public string DesktopName { get; private set; } = "";
    public int Width { get; private set; }
    public int Height { get; private set; }
    /// <summary>The framebuffer: Width × Height pixels, 0xAARRGGBB (BGRA in memory). Lock <see cref="Sync"/> to read it.</summary>
    public int[] Pixels { get; private set; } = Array.Empty<int>();
    public object Sync { get; } = new();

    /// <summary>Part of the framebuffer changed (x, y, w, h). Raised on the reader thread.</summary>
    public event Action<int, int, int, int>? Updated;
    /// <summary>The desktop size changed (the framebuffer was replaced). Raised on the reader thread.</summary>
    public event Action? Resized;
    public event Action? Bell;
    /// <summary>The server's clipboard text (Latin-1, at most 1 MB).</summary>
    public event Action<string>? ServerCutText;
    /// <summary>The session ended: null when closed by this side, otherwise why.</summary>
    public event Action<Exception?>? Closed;

    private RfbClient(Stream s) { _stream = s; _in = new RfbReader(s); }

    // ---- handshake ----------------------------------------------------------------------------------------------------

    /// <summary>Runs the RFB handshake on <paramref name="stream"/> (blocking; call it off the UI thread). Returns a client
    /// with the framebuffer allocated; call <see cref="Start"/> to begin receiving updates.</summary>
    public static RfbClient Handshake(Stream stream, RfbOptions o)
    {
        var c = new RfbClient(stream);
        try { c.DoHandshake(o); return c; }
        catch { c.Dispose(); throw; }
    }

    private void DoHandshake(RfbOptions o)
    {
        Encrypted = o.TransportSecure;
        var greeting = Encoding.ASCII.GetString(_in.Bytes(12));
        if (!greeting.StartsWith("RFB ", StringComparison.Ordinal) || greeting[7] != '.' || greeting[11] != '\n'
            || !int.TryParse(greeting.AsSpan(4, 3), out var major) || !int.TryParse(greeting.AsSpan(8, 3), out var minor))
            throw new RfbException("This is not a VNC server (no RFB greeting).");
        if (major != 3) throw new RfbException($"Unsupported RFB version {major}.{minor}.");
        // 3.889 is Apple's Remote Desktop server: it speaks 3.8 to a 3.8 client.
        int v = minor >= 8 ? 8 : minor >= 7 ? 7 : 3;
        Version = "3." + v;
        WriteRaw(Encoding.ASCII.GetBytes($"RFB 003.00{v}\n"));

        int type;
        if (v == 3)
        {
            type = (int)_in.U32();
            if (type == 0) throw new RfbException("The server refused the connection: " + ReasonString());
            if (type is not (1 or 2)) throw new RfbException($"The server asks for security type {type}, which RFB 3.3 doesn't define.");
        }
        else
        {
            int n = _in.U8();
            if (n == 0) throw new RfbException("The server refused the connection: " + ReasonString());
            var offered = _in.Bytes(n).Select(b => (int)b).ToList();
            type = RfbAuth.Choose(offered, o) ?? throw new RfbException(RfbAuth.NoneSuitable(offered, o));
            WriteRaw(new[] { (byte)type });
        }

        bool resultFollows = type != 1 || v == 8;
        switch (type)
        {
            case 1: Security = "none (the server has no password)"; break;
            case 2: RfbAuth.VncPassword(_in, this, o.Password); Security = "VNC password"; break;
            case 30: RfbAuth.AppleRemoteDesktop(_in, this, o.Username, o.Password); Security = "Apple Remote Desktop (macOS account)"; break;
            case 19: resultFollows = VeNCrypt(o); break;
            default: throw new RfbException($"Security type {type} is not supported.");
        }
        if (resultFollows)
        {
            var result = _in.U32();
            if (result != 0)
                throw new RfbException(v == 8 ? "Sign-in refused: " + ReasonString() : "Sign-in refused (wrong password or user name).");
        }

        WriteRaw(new[] { (byte)(o.Shared ? 1 : 0) });   // ClientInit
        int w = _in.U16(), h = _in.U16();
        _in.Bytes(16);                                  // the server's pixel format; we set our own below
        var nameLen = _in.U32();
        if (nameLen > 64 * 1024) throw new RfbException("The server's desktop name is implausibly long.");
        DesktopName = Encoding.UTF8.GetString(_in.Bytes((int)nameLen));
        Allocate(w, h);

        // 32 bits per pixel, depth 24, little-endian true colour, red 16 / green 8 / blue 0: exactly BGRA in memory.
        WriteRaw(new byte[] { 0, 0, 0, 0, 32, 24, 0, 1, 0, 255, 0, 255, 0, 255, 16, 8, 0, 0, 0, 0 });
        int[] encodings = { 1 /*CopyRect*/, 16 /*ZRLE*/, 5 /*Hextile*/, 2 /*RRE*/, 0 /*Raw*/, -223 /*DesktopSize*/, -224 /*LastRect*/ };
        var se = new byte[4 + 4 * encodings.Length];
        se[0] = 2; se[2] = 0; se[3] = (byte)encodings.Length;
        for (int i = 0; i < encodings.Length; i++) BE32(se, 4 + 4 * i, encodings[i]);
        WriteRaw(se);
    }

    /// <summary>VeNCrypt (type 19): version 0.2, a sub-type, TLS with the certificate pinned, then the inner sign-in.
    /// Returns whether a SecurityResult follows (always, for the sub-types used here).</summary>
    private bool VeNCrypt(RfbOptions o)
    {
        int maj = _in.U8(), min = _in.U8();
        if (maj != 0 || min < 2) throw new RfbException($"The server speaks VeNCrypt {maj}.{min}; version 0.2 is needed.");
        WriteRaw(new byte[] { 0, 2 });
        if (_in.U8() != 0) throw new RfbException("The server refused VeNCrypt 0.2.");
        int n = _in.U8();
        if (n == 0 || n > 64) throw new RfbException("The server offers no VeNCrypt sub-types.");
        var subs = Enumerable.Range(0, n).Select(_ => (int)_in.U32()).ToList();
        var sub = RfbAuth.ChooseVeNCrypt(subs, o) ?? throw new RfbException(RfbAuth.NoneSuitableVeNCrypt(subs, o));
        var b = new byte[4]; BE32(b, 0, sub); WriteRaw(b);
        if (sub is >= 260 and <= 262)
        {
            if (_in.U8() != 1) throw new RfbException("The server refused the TLS sub-type it offered.");
            StartTls(o);
        }
        switch (sub)
        {
            case 260: Security = "VeNCrypt X.509 TLS (no password)"; break;
            case 261: RfbAuth.VncPassword(_in, this, o.Password); Security = "VeNCrypt X.509 TLS + VNC password"; break;
            case 262: RfbAuth.Plain(this, o.Username, o.Password); Security = "VeNCrypt X.509 TLS + user name and password"; break;
            case 256: RfbAuth.Plain(this, o.Username, o.Password); Security = "VeNCrypt Plain (inside the SSH tunnel)"; break;
            case 2: RfbAuth.VncPassword(_in, this, o.Password); Security = "VNC password (VeNCrypt)"; break;
            case 1: Security = "none (VeNCrypt)"; break;
        }
        return true;
    }

    private void StartTls(RfbOptions o)
    {
        if (!_in.Empty) throw new RfbException("The server sent data before the TLS handshake.");
        string? problem = null;
        var tls = new SslStream(_stream, leaveInnerStreamOpen: false, (_, cert, _, errors) =>
        {
            if (cert == null) { problem = "the server sent no certificate"; return false; }
            var fp = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(cert.GetRawCertData()));
            if (o.TlsPin.Length > 0)
            {
                if (string.Equals(fp, o.TlsPin, StringComparison.OrdinalIgnoreCase)) { PinnedSha256 = fp; return true; }
                problem = "the server's certificate CHANGED since it was trusted (expected " + TlsPinPretty(o.TlsPin) + ", got " + TlsPinPretty(fp) + ")";
                return false;
            }
            if (errors == SslPolicyErrors.None) { PinnedSha256 = fp; return true; }   // a certificate Windows trusts
            if (o.TrustCertificate != null && o.TrustCertificate(fp, cert.Subject)) { PinnedSha256 = fp; return true; }
            problem = "the server's certificate isn't trusted (" + errors + ")";
            return false;
        });
        try
        {
            tls.AuthenticateAsClient(new SslClientAuthenticationOptions
            {
                TargetHost = o.TlsHost,
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,   // VNC servers use self-signed certificates; the pin is the check
            });
        }
        catch (System.Security.Authentication.AuthenticationException ex) { throw new RfbException("TLS failed: " + (problem ?? ex.Message)); }
        _stream = tls;
        _in = new RfbReader(tls);
        Encrypted = true;
    }

    /// <summary>The SHA-256 of the TLS certificate that was accepted (to pin it), or "".</summary>
    public string PinnedSha256 { get; private set; } = "";

    public static string TlsPinPretty(string hex) => string.Join(":", Enumerable.Range(0, Math.Min(hex.Length / 2, 32)).Select(i => hex.Substring(i * 2, 2))).ToUpperInvariant();

    private string ReasonString()
    {
        var len = _in.U32();
        if (len > 4096) return "(no readable reason)";
        return Encoding.UTF8.GetString(_in.Bytes((int)len)).Trim();
    }

    private void Allocate(int w, int h)
    {
        if (w <= 0 || h <= 0 || w > MaxDimension || h > MaxDimension) throw new RfbException($"The server's desktop size {w}×{h} is not usable.");
        lock (Sync) { Width = w; Height = h; Pixels = new int[w * h]; }
    }

    // ---- writing ------------------------------------------------------------------------------------------------------

    internal void WriteRaw(byte[] b)
    {
        lock (_write) { _stream.Write(b); _stream.Flush(); }
    }

    private void Send(byte[] b)
    {
        if (_closed) return;
        try { WriteRaw(b); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { Close(ex); }
    }

    internal static void BE16(byte[] b, int at, int v) { b[at] = (byte)(v >> 8); b[at + 1] = (byte)v; }
    internal static void BE32(byte[] b, int at, int v) { b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16); b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v; }

    /// <summary>Asks for a framebuffer update (the whole screen, or only what changed).</summary>
    public void RequestUpdate(bool incremental)
    {
        var b = new byte[10];
        b[0] = 3; b[1] = (byte)(incremental ? 1 : 0);
        BE16(b, 6, Width); BE16(b, 8, Height);
        Send(b);
    }

    /// <summary>A key press or release (an X11 keysym, see <see cref="RfbKeys"/>).</summary>
    public void Key(uint keysym, bool down)
    {
        var b = new byte[8];
        b[0] = 4; b[1] = (byte)(down ? 1 : 0);
        BE32(b, 4, (int)keysym);
        Send(b);
    }

    /// <summary>The pointer at (x, y) in framebuffer pixels with buttons (bit 0 left, 1 middle, 2 right, 3/4 wheel up/down).</summary>
    public void Pointer(int x, int y, int buttons)
    {
        var b = new byte[6];
        b[0] = 5; b[1] = (byte)buttons;
        BE16(b, 2, Math.Clamp(x, 0, Math.Max(0, Width - 1))); BE16(b, 4, Math.Clamp(y, 0, Math.Max(0, Height - 1)));
        Send(b);
    }

    /// <summary>Sends text to the server's clipboard (Latin-1: RFB's clipboard can't carry other characters).</summary>
    public void CutText(string text)
    {
        var t = Encoding.Latin1.GetBytes(text.Length > MaxCutText ? text[..MaxCutText] : text);
        var b = new byte[8 + t.Length];
        b[0] = 6; BE32(b, 4, t.Length); t.CopyTo(b, 8);
        Send(b);
    }

    // ---- reading ------------------------------------------------------------------------------------------------------

    /// <summary>Starts the reader thread and asks for the first full screen.</summary>
    public void Start()
    {
        if (_loop != null) return;
        _loop = new Thread(ReadLoop) { IsBackground = true, Name = "VNC reader" };
        _loop.Start();
        RequestUpdate(incremental: false);
    }

    private void ReadLoop()
    {
        try
        {
            while (!_closed)
            {
                int type = _in.U8();
                switch (type)
                {
                    case 0: FramebufferUpdate(); RequestUpdate(incremental: true); break;
                    case 1: _in.U8(); _in.U16(); _in.Skip(_in.U16() * 6L); break;   // colour map: not used in true colour
                    case 2: Bell?.Invoke(); break;
                    case 3:
                        _in.Bytes(3);
                        var len = _in.U32();
                        if (len > MaxCutText) { _in.Skip(len); break; }
                        ServerCutText?.Invoke(Encoding.Latin1.GetString(_in.Bytes((int)len)));
                        break;
                    default: throw new RfbException($"The server sent an unknown message ({type}).");
                }
            }
        }
        catch (Exception ex) when (!_closed) { Close(ex); }
        catch { }
    }

    private void FramebufferUpdate()
    {
        _in.U8();
        int n = _in.U16();
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = 0, y1 = 0;
        for (int i = 0; i < n; i++)
        {
            int x = _in.U16(), y = _in.U16(), w = _in.U16(), h = _in.U16();
            int enc = _in.S32();
            if (enc == -224) break;   // LastRect
            if (enc == -223) { Allocate(w, h); Resized?.Invoke(); x0 = 0; y0 = 0; x1 = w; y1 = h; continue; }
            if (w == 0 || h == 0) continue;
            if (x + w > Width || y + h > Height) throw new RfbException($"The server drew outside the screen ({x},{y} {w}×{h} on {Width}×{Height}).");
            Decode(enc, x, y, w, h);
            x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x + w); y1 = Math.Max(y1, y + h);
        }
        if (x1 > x0 && y1 > y0) Updated?.Invoke(x0, y0, x1 - x0, y1 - y0);
    }

    // ---- closing ------------------------------------------------------------------------------------------------------

    private void Close(Exception? why)
    {
        if (_closed) return;
        _closed = true;
        try { _stream.Dispose(); } catch { }
        Closed?.Invoke(why is EndOfStreamException ? new RfbException("The VNC server closed the connection.") : why);
    }

    public void Dispose() => Close(null);
}

/// <summary>Buffered big-endian reads for the RFB reader thread (synchronous: one dedicated thread per session).</summary>
internal sealed class RfbReader(Stream s)
{
    private readonly byte[] _buf = new byte[64 * 1024];
    private int _pos, _len;

    public bool Empty => _pos >= _len;

    private void Fill()
    {
        _len = s.Read(_buf, 0, _buf.Length);
        _pos = 0;
        if (_len <= 0) { _len = 0; throw new EndOfStreamException(); }
    }

    public byte U8() { if (_pos >= _len) Fill(); return _buf[_pos++]; }
    public int U16() => U8() << 8 | U8();
    public uint U32() => (uint)(U8() << 24 | U8() << 16 | U8() << 8 | U8());
    public int S32() => (int)U32();
    /// <summary>A little-endian 32-bit pixel (our pixel format).</summary>
    public int Pixel32()
    {
        int b = U8(), g = U8(), r = U8();
        U8();   // padding byte (depth 24)
        return unchecked((int)0xFF000000) | r << 16 | g << 8 | b;
    }

    /// <summary>A ZRLE compressed pixel: three bytes, blue first (our pixel format with depth 24).</summary>
    public int CPixel() { int b = U8(), g = U8(), r = U8(); return unchecked((int)0xFF000000) | r << 16 | g << 8 | b; }

    public void Read(Span<byte> dst)
    {
        while (dst.Length > 0)
        {
            if (_pos >= _len) Fill();
            int n = Math.Min(dst.Length, _len - _pos);
            _buf.AsSpan(_pos, n).CopyTo(dst);
            _pos += n;
            dst = dst[n..];
        }
    }

    public byte[] Bytes(int n) { var b = new byte[n]; Read(b); return b; }

    public void Skip(long n)
    {
        while (n > 0)
        {
            if (_pos >= _len) Fill();
            int k = (int)Math.Min(n, _len - _pos);
            _pos += k; n -= k;
        }
    }
}
