using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using DcxManager.Vnc;

namespace DcxManager;

/// <summary>
/// 0.5.1 #19 (ADR-069): the app's own VNC client against a scripted fake RFB server on 127.0.0.1 — RFB 3.8 with the VNC
/// password, Apple's 003.889 with Apple Remote Desktop sign-in (a real Diffie-Hellman exchange; the fake decrypts and checks
/// the macOS user name and password), RFB 3.3 with no password, VeNCrypt Plain (refused on a direct connection, used
/// inside a tunnel) and VeNCrypt X.509 TLS with the certificate pinned on first trust and a changed one refused. Every
/// decoder (Raw, CopyRect, RRE, Hextile, ZRLE over one continuous zlib stream) is checked pixel by pixel, then key, pointer
/// and clipboard messages, a desktop resize, the server clipboard, and a server drawing outside the screen. Hostile sizes
/// are refused before anything is allocated. No real VNC server is contacted.
/// </summary>
public static class VncTests
{
    private const string Password = "Vnc-Pw-42";
    private const string MacUser = "macuser", MacPassword = "Mac-Selftest-Pw-1";

    public static async Task RunAsync(Action<string> ok, Action<string, Exception?> fail)
    {
        await Run("VNC: RFB 3.8, VNC password, every encoding, input, resize, clipboard, out-of-screen refused", Rfb38, ok, fail);
        await Run("VNC: Apple 003.889 + Apple Remote Desktop sign-in (DH + AES-128, macOS account checked by the fake)", AppleArd, ok, fail);
        await Run("VNC: a wrong macOS password is refused with the server's reason", AppleWrongPassword, ok, fail);
        await Run("VNC: RFB 3.3 without a password", Rfb33None, ok, fail);
        await Run("VNC: a Mac without a user name says what is missing", MacNeedsUser, ok, fail);
        await Run("VNC: VeNCrypt Plain refused direct, used inside a tunnel", VeNCryptPlain, ok, fail);
        await Run("VNC: VeNCrypt X.509 TLS, pinned on first trust, a changed certificate refused", VeNCryptX509, ok, fail);
        await Run("VNC: hostile server sizes refused before allocating", Hostile, ok, fail);
    }

    private static async Task Run(string name, Func<Task> test, Action<string> ok, Action<string, Exception?> fail)
    {
        try { await test(); ok(name); }
        catch (Exception ex) { fail(name, ex); }
    }

    // ---- scenarios ----------------------------------------------------------------------------------------------------

    private static async Task Rfb38()
    {
        var gotKey = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cut = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var updated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new SemaphoreSlim(0);
        await using var srv = new FakeRfb(s =>
        {
            W(s, "RFB 003.008\n"u8.ToArray());
            Expect(Encoding.ASCII.GetString(R(s, 12)) == "RFB 003.008\n", "client version");
            W(s, new byte[] { 1, 2 });
            Expect(R(s, 1)[0] == 2, "chose VNC auth");
            var challenge = RandomNumberGenerator.GetBytes(16);
            W(s, challenge);
            Expect(R(s, 16).SequenceEqual(VncResponse(challenge, Password)), "VNC response");
            W(s, U32(0));
            R(s, 1);
            ServerInit(s, 64, 48, "selftest-desktop");
            var pf = R(s, 20);
            Expect(pf[0] == 0 && pf[4] == 32 && pf[5] == 24 && pf[7] == 1 && pf[14] == 16 && pf[15] == 8 && pf[16] == 0, "pixel format");
            var enc = R(s, 4); int n = enc[2] << 8 | enc[3];
            var list = Enumerable.Range(0, n).Select(_ => BinaryPrimitivesRead(R(s, 4))).ToList();
            Expect(list.Contains(16) && list.Contains(5) && list.Contains(1) && list.Contains(0) && list.Contains(-223), "encodings " + string.Join(",", list));
            var req = R(s, 10);
            Expect(req[0] == 3 && req[1] == 0, "first update request is full");

            // One update with every encoding.
            using var zbuf = new MemoryStream();
            using var z = new ZLibStream(zbuf, CompressionLevel.Optimal, leaveOpen: true);
            byte[] Chunk(byte[] tiles) { long at = zbuf.Length; z.Write(tiles); z.Flush(); return zbuf.ToArray()[(int)at..]; }
            var solid = Chunk(new byte[] { 1, 255, 0, 255 });                                   // a solid magenta tile
            var packed = Chunk(new byte[] { 2, 0, 0, 0, 0, 255, 255, 0xAA, 0xAA, 0xAA, 0xAA });   // palette black / yellow, 1 bit
            var m = new MemoryStream();
            m.Write(new byte[] { 0, 0, 0, 6 });
            Rect(m, 0, 0, 4, 4, 0); for (int i = 0; i < 16; i++) m.Write(new byte[] { 0, 0, 255, 0 });   // Raw red
            Rect(m, 10, 10, 4, 4, 1); m.Write(new byte[] { 0, 0, 0, 0 });                              // CopyRect from 0,0
            Rect(m, 30, 0, 8, 8, 2); m.Write(U32(1)); m.Write(new byte[] { 0, 255, 0, 0 }); m.Write(new byte[] { 255, 0, 0, 0, 0, 2, 0, 2, 0, 2, 0, 2 });   // RRE
            Rect(m, 40, 0, 16, 16, 5); m.Write(new byte[] { 2 | 4 | 8, 255, 255, 255, 0, 0, 0, 0, 0, 1, 0x11, 0x22 });   // Hextile
            Rect(m, 0, 20, 64, 16, 16); m.Write(U32(solid.Length)); m.Write(solid);                    // ZRLE solid
            Rect(m, 0, 36, 8, 4, 16); m.Write(U32(packed.Length)); m.Write(packed);                    // ZRLE packed palette
            W(s, m.ToArray());

            // Whatever the client sends next (its incremental request, the key, pointer and clipboard), in any order.
            bool key = false, ptr = false, clip = false;
            while (!(key && ptr && clip))
            {
                int type = R(s, 1)[0];
                switch (type)
                {
                    case 3: R(s, 9); break;
                    case 4: { var k = R(s, 7); key = k[0] == 1 && k[6] == 0x61; break; }
                    case 5: { var p = R(s, 5); ptr = p[0] == 1 && (p[1] << 8 | p[2]) == 5 && (p[3] << 8 | p[4]) == 6; break; }
                    case 6: { R(s, 3); var len = BinaryPrimitivesRead(R(s, 4)); clip = Encoding.Latin1.GetString(R(s, len)) == "hi from here"; break; }
                    default: throw new Exception("unexpected client message " + type);
                }
            }
            gotKey.TrySetResult("ok");
            proceed.Wait(TimeSpan.FromSeconds(10));
            var resize = new MemoryStream(); resize.Write(new byte[] { 0, 0, 0, 1 }); Rect(resize, 0, 0, 80, 60, -223);
            W(s, resize.ToArray());
            var sc = Encoding.Latin1.GetBytes("remote-clip"); var cutMsg = new byte[8 + sc.Length]; cutMsg[0] = 3; BE(cutMsg, 4, sc.Length); sc.CopyTo(cutMsg, 8);
            W(s, cutMsg);
            proceed.Wait(TimeSpan.FromSeconds(10));
            var bad = new MemoryStream(); bad.Write(new byte[] { 0, 0, 0, 1 }); Rect(bad, 78, 58, 4, 4, 0); bad.Write(new byte[64]);
            W(s, bad.ToArray());
            Thread.Sleep(500);
        });

        var c = await Connect(srv, new RfbOptions { Password = Password });
        c.Updated += (_, _, _, _) => updated.TrySetResult();
        c.Resized += () => resized.TrySetResult();
        c.ServerCutText += t => cut.TrySetResult(t);
        c.Closed += e => closed.TrySetResult(e);
        try
        {
            if (c.Version != "3.8" || c.Security != "VNC password" || c.DesktopName != "selftest-desktop" || c.Width != 64 || c.Height != 48) throw new Exception($"init: {c.Version} {c.Security} {c.DesktopName} {c.Width}×{c.Height}");
            c.Start();
            await Wait(updated.Task, "framebuffer update");
            int P(int x, int y) { lock (c.Sync) return c.Pixels[y * c.Width + x]; }
            void Is(int x, int y, uint want, string what) { if ((uint)P(x, y) != want) throw new Exception($"{what} at {x},{y}: {(uint)P(x, y):X8}, want {want:X8}"); }
            Is(0, 0, 0xFFFF0000, "Raw red"); Is(3, 3, 0xFFFF0000, "Raw red");
            Is(10, 10, 0xFFFF0000, "CopyRect"); Is(13, 13, 0xFFFF0000, "CopyRect");
            Is(30, 0, 0xFF00FF00, "RRE background"); Is(32, 2, 0xFF0000FF, "RRE subrectangle"); Is(34, 4, 0xFF00FF00, "RRE background");
            Is(40, 0, 0xFFFFFFFF, "Hextile background"); Is(41, 1, 0xFF000000, "Hextile subrectangle"); Is(43, 3, 0xFF000000, "Hextile subrectangle"); Is(44, 4, 0xFFFFFFFF, "Hextile background");
            Is(5, 20, 0xFFFF00FF, "ZRLE solid"); Is(63, 35, 0xFFFF00FF, "ZRLE solid");
            Is(0, 36, 0xFFFFFF00, "ZRLE palette"); Is(1, 36, 0xFF000000, "ZRLE palette"); Is(6, 39, 0xFFFFFF00, "ZRLE palette");
            c.Key(0x61, true); c.Pointer(5, 6, 1); c.CutText("hi from here");
            await Wait(gotKey.Task, "key, pointer and clipboard at the server");
            proceed.Release();
            await Wait(resized.Task, "desktop resize");
            if (c.Width != 80 || c.Height != 60) throw new Exception($"resize: {c.Width}×{c.Height}");
            if (await Wait(cut.Task, "server clipboard") != "remote-clip") throw new Exception("server clipboard text");
            proceed.Release();
            var why = await Wait(closed.Task, "close on a rectangle outside the screen");
            if (why is not RfbException rex || !rex.Message.Contains("outside the screen")) throw new Exception("out-of-screen: " + why?.Message);
        }
        finally { c.Dispose(); }
    }

    private static async Task AppleArd()
    {
        await using var srv = new FakeRfb(s => ArdServer(s, MacUser, MacPassword));
        var c = await Connect(srv, new RfbOptions { Username = MacUser, Password = MacPassword });
        try
        {
            if (c.Version != "3.8" || !c.Security.Contains("Apple Remote Desktop") || c.DesktopName != "Selftest Mac" || c.Width != 32) throw new Exception($"{c.Version} {c.Security} {c.DesktopName} {c.Width}");
        }
        finally { c.Dispose(); }
        if (srv.Error is { } e) throw new Exception("fake: " + e.Message);
    }

    private static async Task AppleWrongPassword()
    {
        await using var srv = new FakeRfb(s => ArdServer(s, MacUser, MacPassword));
        try { (await Connect(srv, new RfbOptions { Username = MacUser, Password = "wrong-password" })).Dispose(); throw new Exception("a wrong password signed in"); }
        catch (RfbException ex) when (ex.Message.Contains("Sign-in refused") && ex.Message.Contains("Authentication failed")) { }
    }

    private static void ArdServer(Stream s, string user, string password)
    {
        W(s, "RFB 003.889\n"u8.ToArray());
        Expect(Encoding.ASCII.GetString(R(s, 12)) == "RFB 003.008\n", "client answers 3.8 to Apple");
        W(s, new byte[] { 4, 30, 33, 35, 36 });
        Expect(R(s, 1)[0] == 30, "chose Apple Remote Desktop");
        // RFC 2409 group 2 (1024-bit MODP), generator 2.
        var p = BigInteger.Parse("00FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD129024E088A67CC74020BBEA63B139B22514A08798E3404DDEF9519B3CD3A431B302B0A6DF25F14374FE1356D6D51C245E485B576625E7EC6F44C42E9A637ED6B0BFF5CB6F406B7EDEE386BFB5A899FA5AE9F24117C4B1FE649286651ECE65381FFFFFFFFFFFFFFFF", System.Globalization.NumberStyles.HexNumber);
        int len = 128;
        var x = new BigInteger(RandomNumberGenerator.GetBytes(64), isUnsigned: true) + 2;
        var ys = BigInteger.ModPow(2, x, p);
        var head = new byte[4]; head[1] = 2; head[2] = (byte)(len >> 8); head[3] = (byte)len;
        W(s, head.Concat(Fixed(p, len)).Concat(Fixed(ys, len)).ToArray());
        var reply = R(s, 128 + len);
        var yc = new BigInteger(reply.AsSpan(128), isUnsigned: true, isBigEndian: true);
#pragma warning disable CA5351 // the fake implements Apple's protocol, which derives the key with MD5
        var key = MD5.HashData(Fixed(BigInteger.ModPow(yc, x, p), len));
#pragma warning restore CA5351
        using var aes = Aes.Create(); aes.Key = key;
#pragma warning disable CA5358 // Apple Remote Desktop's single ECB block
        var creds = aes.DecryptEcb(reply.AsSpan(0, 128), PaddingMode.None);
#pragma warning restore CA5358
        string Str(int at) => Encoding.UTF8.GetString(creds, at, Array.IndexOf(creds, (byte)0, at) - at);
        if (Str(0) != user || Str(64) != password)
        {
            var reason = "Authentication failed"u8.ToArray();
            W(s, U32(1).Concat(U32(reason.Length)).Concat(reason).ToArray());
            return;
        }
        W(s, U32(0));
        R(s, 1);
        ServerInit(s, 32, 32, "Selftest Mac");
        R(s, 20);
        Thread.Sleep(200);
    }

    private static async Task Rfb33None()
    {
        await using var srv = new FakeRfb(s =>
        {
            W(s, "RFB 003.003\n"u8.ToArray());
            Expect(Encoding.ASCII.GetString(R(s, 12)) == "RFB 003.003\n", "client answers 3.3");
            W(s, U32(1));
            R(s, 1);
            ServerInit(s, 16, 16, "");
            R(s, 20);
            Thread.Sleep(200);
        });
        var c = await Connect(srv, new RfbOptions());
        try { if (c.Version != "3.3" || !c.Security.StartsWith("none", StringComparison.Ordinal) || c.Width != 16) throw new Exception($"{c.Version} {c.Security}"); }
        finally { c.Dispose(); }
    }

    private static async Task MacNeedsUser()
    {
        await using var srv = new FakeRfb(s => { W(s, "RFB 003.889\n"u8.ToArray()); R(s, 12); W(s, new byte[] { 1, 30 }); Thread.Sleep(300); });
        try { (await Connect(srv, new RfbOptions { Password = "x" })).Dispose(); throw new Exception("connected without a macOS user name"); }
        catch (RfbException ex) when (ex.Message.Contains("macOS user name")) { }
    }

    private static void VeNCryptStart(Stream s, int sub)
    {
        W(s, "RFB 003.008\n"u8.ToArray()); R(s, 12);
        W(s, new byte[] { 1, 19 });
        Expect(R(s, 1)[0] == 19, "chose VeNCrypt");
        W(s, new byte[] { 0, 2 });
        var v = R(s, 2); Expect(v[0] == 0 && v[1] == 2, "VeNCrypt 0.2");
        W(s, new byte[] { 0, 1 }.Concat(U32(sub)).ToArray());
    }

    private static async Task VeNCryptPlain()
    {
        await using (var srv = new FakeRfb(s => { VeNCryptStart(s, 256); Thread.Sleep(300); }))
        {
            try { (await Connect(srv, new RfbOptions { Username = "u", Password = "p" })).Dispose(); throw new Exception("Plain was used on a direct connection"); }
            catch (RfbException ex) when (ex.Message.Contains("in clear")) { }
        }
        await using var tunnel = new FakeRfb(s =>
        {
            VeNCryptStart(s, 256);
            Expect(BinaryPrimitivesRead(R(s, 4)) == 256, "chose Plain");
            int ul = BinaryPrimitivesRead(R(s, 4)), pl = BinaryPrimitivesRead(R(s, 4));
            Expect(Encoding.UTF8.GetString(R(s, ul)) == "linuxuser" && Encoding.UTF8.GetString(R(s, pl)) == "Linux-Pw-7", "Plain credentials");
            W(s, U32(0));
            R(s, 1);
            ServerInit(s, 20, 10, "tiger");
            R(s, 20);
            Thread.Sleep(200);
        });
        var c = await Connect(tunnel, new RfbOptions { Username = "linuxuser", Password = "Linux-Pw-7", TransportSecure = true });
        try { if (!c.Security.Contains("Plain") || !c.Encrypted) throw new Exception(c.Security); }
        finally { c.Dispose(); }
    }

    private static async Task VeNCryptX509()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=dcx-selftest-vnc", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var tmp = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(7));
        using var cert = X509CertificateLoader.LoadPkcs12(tmp.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
        var sha = Convert.ToHexString(SHA256.HashData(cert.GetRawCertData()));
        void Server(Stream s)
        {
            VeNCryptStart(s, 261);
            Expect(BinaryPrimitivesRead(R(s, 4)) == 261, "chose X509Vnc");
            W(s, new byte[] { 1 });
            using var tls = new SslStream(s, leaveInnerStreamOpen: true);
            try { tls.AuthenticateAsServer(cert, clientCertificateRequired: false, System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13, checkCertificateRevocation: false); }
            catch (System.Security.Authentication.AuthenticationException) { return; }   // the client refused the certificate
            var challenge = RandomNumberGenerator.GetBytes(16);
            W(tls, challenge);
            Expect(R(tls, 16).SequenceEqual(VncResponse(challenge, Password)), "VNC response inside TLS");
            W(tls, U32(0));
            R(tls, 1);
            ServerInit(tls, 24, 24, "x509");
            R(tls, 20);
            Thread.Sleep(200);
        }
        string? asked = null;
        await using (var srv = new FakeRfb(Server))
        {
            var c = await Connect(srv, new RfbOptions { Password = Password, TlsHost = "127.0.0.1", TrustCertificate = (fp, subject) => { asked = fp; return subject.Contains("dcx-selftest-vnc"); } });
            try
            {
                if (asked != sha || c.PinnedSha256 != sha) throw new Exception("pin: asked " + asked + " pinned " + c.PinnedSha256);
                if (!c.Encrypted || !c.Security.Contains("X.509")) throw new Exception(c.Security);
            }
            finally { c.Dispose(); }
        }
        await using (var srv = new FakeRfb(Server))
        {
            // Pinned: no question this time.
            var c = await Connect(srv, new RfbOptions { Password = Password, TlsHost = "127.0.0.1", TlsPin = sha, TrustCertificate = (_, _) => throw new Exception("asked although pinned") });
            c.Dispose();
        }
        await using (var srv = new FakeRfb(Server))
        {
            try { (await Connect(srv, new RfbOptions { Password = Password, TlsHost = "127.0.0.1", TlsPin = new string('0', 64), TrustCertificate = (_, _) => true })).Dispose(); throw new Exception("a changed certificate was accepted"); }
            catch (RfbException ex) when (ex.Message.Contains("CHANGED")) { }
        }
    }

    private static async Task Hostile()
    {
        await using (var srv = new FakeRfb(s => { W(s, "RFB 003.003\n"u8.ToArray()); R(s, 12); W(s, U32(1)); R(s, 1); ServerInit(s, 20000, 20000, ""); Thread.Sleep(300); }))
        {
            try { (await Connect(srv, new RfbOptions())).Dispose(); throw new Exception("a 20000×20000 desktop was accepted"); }
            catch (RfbException ex) when (ex.Message.Contains("not usable")) { }
        }
        await using (var srv = new FakeRfb(s =>
        {
            W(s, "RFB 003.003\n"u8.ToArray()); R(s, 12); W(s, U32(1)); R(s, 1);
            W(s, new byte[] { 0, 16, 0, 16 }.Concat(new byte[16]).Concat(U32(1 << 30)).ToArray());
            Thread.Sleep(300);
        }))
        {
            try { (await Connect(srv, new RfbOptions())).Dispose(); throw new Exception("a 1 GB desktop name was accepted"); }
            catch (RfbException ex) when (ex.Message.Contains("implausibly long")) { }
        }
        await using (var srv = new FakeRfb(s => { W(s, "HTTP/1.1 200\n"u8.ToArray()); Thread.Sleep(300); }))
        {
            try { (await Connect(srv, new RfbOptions())).Dispose(); throw new Exception("a non-VNC server was accepted"); }
            catch (RfbException ex) when (ex.Message.Contains("not a VNC server")) { }
        }
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------

    private static async Task<RfbClient> Connect(FakeRfb srv, RfbOptions o)
    {
        var tcp = new TcpClient { NoDelay = true, ReceiveTimeout = 10_000 };
        await tcp.ConnectAsync(IPAddress.Loopback, srv.Port);
        try { return await Task.Run(() => RfbClient.Handshake(tcp.GetStream(), o)); }
        catch { tcp.Dispose(); throw; }
    }

    private static async Task<T> Wait<T>(Task<T> t, string what)
    {
        if (await Task.WhenAny(t, Task.Delay(10_000)) != t) throw new Exception("timed out waiting for " + what);
        return await t;
    }

    private static async Task Wait(Task t, string what)
    {
        if (await Task.WhenAny(t, Task.Delay(10_000)) != t) throw new Exception("timed out waiting for " + what);
    }

    private static void Expect(bool cond, string what) { if (!cond) throw new Exception("fake server: " + what); }

    private static byte[] VncResponse(byte[] challenge, string password)
    {
        var key = new byte[8];
        var pw = Encoding.Latin1.GetBytes(password);
        for (int i = 0; i < Math.Min(8, pw.Length); i++)
        {
            // Bit reversal written differently from the client's, so the two are checked against each other.
            var bits = Convert.ToString(pw[i], 2).PadLeft(8, '0');
            key[i] = Convert.ToByte(new string(bits.Reverse().ToArray()), 2);
        }
#pragma warning disable CA5351 // the fake implements RFB's DES challenge (protocol-defined)
        using var des = DES.Create();
#pragma warning restore CA5351
        des.Key = key;
        return des.EncryptEcb(challenge, PaddingMode.None);
    }

    private static byte[] Fixed(BigInteger v, int len)
    {
        var b = v.ToByteArray(isUnsigned: true, isBigEndian: true);
        var o = new byte[len];
        b.CopyTo(o, len - b.Length);
        return o;
    }

    private static void ServerInit(Stream s, int w, int h, string name)
    {
        var n = Encoding.UTF8.GetBytes(name);
        var b = new byte[24 + n.Length];
        b[0] = (byte)(w >> 8); b[1] = (byte)w; b[2] = (byte)(h >> 8); b[3] = (byte)h;
        b[4] = 32; b[5] = 24; b[7] = 1; b[9] = 255; b[11] = 255; b[13] = 255; b[14] = 16; b[15] = 8;
        BE(b, 20, n.Length); n.CopyTo(b, 24);
        W(s, b);
    }

    private static void Rect(Stream m, int x, int y, int w, int h, int enc)
    {
        var b = new byte[12];
        b[0] = (byte)(x >> 8); b[1] = (byte)x; b[2] = (byte)(y >> 8); b[3] = (byte)y;
        b[4] = (byte)(w >> 8); b[5] = (byte)w; b[6] = (byte)(h >> 8); b[7] = (byte)h;
        BE(b, 8, enc);
        m.Write(b);
    }

    private static byte[] U32(int v) { var b = new byte[4]; BE(b, 0, v); return b; }
    private static void BE(byte[] b, int at, int v) { b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16); b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v; }
    private static int BinaryPrimitivesRead(byte[] b) => b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3];
    private static void W(Stream s, byte[] b) { s.Write(b); s.Flush(); }
    private static byte[] R(Stream s, int n) { var b = new byte[n]; s.ReadExactly(b); return b; }

    /// <summary>One scripted RFB server on 127.0.0.1 (one connection).</summary>
    private sealed class FakeRfb : IAsyncDisposable
    {
        private readonly TcpListener _l = new(IPAddress.Loopback, 0);
        private readonly Task _run;
        public int Port { get; }
        public Exception? Error { get; private set; }

        public FakeRfb(Action<Stream> script)
        {
            _l.Start();
            Port = ((IPEndPoint)_l.LocalEndpoint).Port;
            _run = Task.Run(async () =>
            {
                using var c = await _l.AcceptTcpClientAsync();
                c.ReceiveTimeout = 10_000;
                try { script(c.GetStream()); }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or EndOfStreamException) { }
                catch (Exception ex) { Error = ex; }
            });
        }

        public async ValueTask DisposeAsync()
        {
            _l.Stop();
            await Task.WhenAny(_run, Task.Delay(3000));
        }
    }
}
