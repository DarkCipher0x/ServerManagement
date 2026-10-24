using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace DcxManager.DcxCtl;

/// <summary>
/// The DarkCipher Control protocol dcx-ctl/1 (DarkCipherSystems-Website docs/CONTROL-API.md §5; reference implementation
/// deploy/reports/dcxctl_proto.py — this is a byte-for-byte port of it). Nothing here invents cryptography: X25519 comes
/// from BouncyCastle (already shipped with SSH.NET), ChaCha20-Poly1305, HMAC-SHA256 and HKDF from .NET. <see cref="SelfTest"/>
/// checks all of them against the RFC 5869 / 7748 / 8439 vectors and the client refuses to run if any fails.
/// Every MAC / KDF input is a list of length-prefixed fields (no splicing).
/// </summary>
public static class DcxCtlProtocol
{
    public static readonly byte[] Proto = "dcx-ctl/1"u8.ToArray();
    private static readonly byte[] Magic = "DCXCTL1"u8.ToArray();
    public const int Version = 1, KeyIdLen = 16, HelloLen = 7 + 1 + 16 + 32 + 32 + 8 + 32, ReplyLen = 96;
    public const int MaxFrame = 1024 * 1024 + 64, MaxPlain = MaxFrame - 8 - 16;
    public const long MaxMessages = 1L << 20;
    private const byte DirC2S = 1, DirS2C = 2;

    public sealed class ProtocolException(string message) : Exception(message);

    // ---- primitives ---------------------------------------------------------------------------------------------------

    /// <summary>Length-prefixed fields: byte[] as is, string as UTF-8, long as 8-byte big-endian.</summary>
    public static byte[] Fields(params object[] parts)
    {
        using var ms = new MemoryStream();
        Span<byte> len = stackalloc byte[4];
        foreach (var p in parts)
        {
            var b = p switch
            {
                byte[] x => x,
                string s => Encoding.UTF8.GetBytes(s),
                long n => BigEndian(n),
                int n => BigEndian(n),
                _ => throw new ArgumentException("field type"),
            };
            BinaryPrimitives.WriteUInt32BigEndian(len, (uint)b.Length);
            ms.Write(len); ms.Write(b);
        }
        return ms.ToArray();
    }

    private static byte[] BigEndian(long n) { var b = new byte[8]; BinaryPrimitives.WriteUInt64BigEndian(b, (ulong)n); return b; }
    public static byte[] Hmac(byte[] key, byte[] data) => HMACSHA256.HashData(key, data);
    public static byte[] HkdfExtract(byte[] salt, byte[] ikm) => Hmac(salt, ikm);
    public static byte[] HkdfExpand(byte[] prk, byte[] info, int length) => HKDF.Expand(HashAlgorithmName.SHA256, prk, length, info);
    private static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    /// <summary>S (32 bytes) → (K_hs, K_auth).</summary>
    public static (byte[] Hs, byte[] Auth) ClientKeys(byte[] secret)
    {
        if (secret.Length != 32) throw new ProtocolException("secret must be 32 bytes");
        var prk = HkdfExtract(Cat(Proto, " client key"u8.ToArray()), secret);
        return (HkdfExpand(prk, "dcx-ctl/1 handshake"u8.ToArray(), 32), HkdfExpand(prk, "dcx-ctl/1 e2e"u8.ToArray(), 32));
    }

    public static (byte[] Priv, byte[] Pub) X25519KeyPair()
    {
        var priv = RandomNumberGenerator.GetBytes(32);
        return (priv, X25519Base(priv));
    }

    public static byte[] X25519Base(byte[] priv)
    {
        var k = new Org.BouncyCastle.Crypto.Parameters.X25519PrivateKeyParameters(priv, 0);
        return k.GeneratePublicKey().GetEncoded();
    }

    public static byte[] X25519(byte[] priv, byte[] pub)
    {
        if (pub.Length != 32) throw new ProtocolException("bad public key");
        var shared = new byte[32];
        new Org.BouncyCastle.Crypto.Parameters.X25519PrivateKeyParameters(priv, 0).GenerateSecret(new Org.BouncyCastle.Crypto.Parameters.X25519PublicKeyParameters(pub, 0), shared, 0);
        if (CryptographicOperations.FixedTimeEquals(shared, new byte[32])) throw new ProtocolException("low-order public key");
        return shared;
    }

    public static byte[] Seal(byte[] key, byte[] nonce, byte[] aad, byte[] plain)
    {
        var ct = new byte[plain.Length]; var tag = new byte[16];
        using var c = new ChaCha20Poly1305(key);
        c.Encrypt(nonce, plain, ct, tag, aad);
        return Cat(ct, tag);
    }

    public static byte[] Open(byte[] key, byte[] nonce, byte[] aad, byte[] sealedData)
    {
        if (sealedData.Length < 16) throw new ProtocolException("short");
        var plain = new byte[sealedData.Length - 16];
        using var c = new ChaCha20Poly1305(key);
        try { c.Decrypt(nonce, sealedData.AsSpan(0, plain.Length), sealedData.AsSpan(plain.Length), plain, aad); }
        catch (CryptographicException) { throw new ProtocolException("authentication failed"); }
        return plain;
    }

    /// <summary>RFC 5869, RFC 7748 and RFC 8439 vectors (the same as the Python side checks). Throws on any mismatch.</summary>
    public static void SelfTest()
    {
        static byte[] H(string s) => Convert.FromHexString(s);
        var prk = HkdfExtract(H("000102030405060708090a0b0c"), Enumerable.Repeat((byte)0x0b, 22).ToArray());
        var okm = HkdfExpand(prk, H("f0f1f2f3f4f5f6f7f8f9"), 42);
        bool ok = prk.SequenceEqual(H("077709362c2e32df0ddc3f0dc47bba6390b6c73bb50f9c3122ec844ad7c2b3e5")) &&
                  okm.SequenceEqual(H("3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865"));
        var aPriv = H("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a");
        var bPub = H("de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f");
        ok = ok && X25519Base(aPriv).SequenceEqual(H("8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a"));
        ok = ok && X25519(aPriv, bPub).SequenceEqual(H("4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742"));
        var msg = Encoding.ASCII.GetBytes("Ladies and Gentlemen of the class of '99: If I could offer you only one tip for the future, sunscreen would be it.");
        var sealedMsg = Seal(Enumerable.Range(0x80, 32).Select(i => (byte)i).ToArray(), H("070000004041424344454647"), H("50515253c0c1c2c3c4c5c6c7"), msg);
        ok = ok && sealedMsg[^16..].SequenceEqual(H("1ae10b594f09e26a7e902ecbd0600691"));
        if (!ok) throw new ProtocolException("cryptographic self-test failed");
    }

    // ---- framing ------------------------------------------------------------------------------------------------------

    public static async Task<byte[]> ReadFrameAsync(Stream s, CancellationToken ct)
    {
        var len = await ReadExactAsync(s, 4, ct);
        var n = BinaryPrimitives.ReadUInt32BigEndian(len);
        if (n == 0 || n > MaxFrame) throw new ProtocolException("bad frame length");
        return await ReadExactAsync(s, (int)n, ct);
    }

    public static async Task WriteFrameAsync(Stream s, byte[] payload, CancellationToken ct)
    {
        if (payload.Length == 0 || payload.Length > MaxFrame) throw new ProtocolException("bad frame length");
        var len = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(len, (uint)payload.Length);
        await s.WriteAsync(Cat(len, payload), ct);
        await s.FlushAsync(ct);
    }

    private static async Task<byte[]> ReadExactAsync(Stream s, int n, CancellationToken ct)
    {
        var buf = new byte[n]; int got = 0;
        while (got < n)
        {
            var r = await s.ReadAsync(buf.AsMemory(got, n - got), ct);
            if (r == 0) throw new ProtocolException("connection closed");
            got += r;
        }
        return buf;
    }

    // ---- handshake ----------------------------------------------------------------------------------------------------

    public static (byte[] Hello, byte[] Priv) MakeHello(byte[] keyId, byte[] kHs, long? now = null)
    {
        if (keyId.Length != KeyIdLen) throw new ProtocolException("bad key id");
        var (priv, pub) = X25519KeyPair();
        var body = Cat(Magic, [Version], keyId, pub, RandomNumberGenerator.GetBytes(32), BigEndian(now ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        return (Cat(body, Hmac(kHs, body)), priv);
    }

    public sealed record Keys(byte[] C2S, byte[] S2C, byte[] Sid, byte[] Th);

    public static Keys CheckReply(byte[] hello, byte[] reply, byte[] kHs, byte[] priv)
    {
        if (reply.Length != ReplyLen) throw new ProtocolException("bad reply");
        var es = reply[..32]; var ns = reply[32..64]; var mac = reply[64..];
        if (!CryptographicOperations.FixedTimeEquals(Hmac(kHs, Cat("S"u8.ToArray(), hello, es, ns)), mac)) throw new ProtocolException("the server didn't prove it holds the key");
        return Schedule(kHs, X25519(priv, es), hello, reply);
    }

    /// <summary>Server side (the self-test's fake portal only).</summary>
    internal static (byte[] Reply, Keys Keys) MakeReply(byte[] hello, byte[] kHs)
    {
        var ec = hello[(7 + 1 + 16)..(7 + 1 + 16 + 32)];
        if (!CryptographicOperations.FixedTimeEquals(Hmac(kHs, hello[..^32]), hello[^32..])) throw new ProtocolException("hello MAC");
        var (priv, pub) = X25519KeyPair();
        var ns = RandomNumberGenerator.GetBytes(32);
        var reply = Cat(pub, ns, Hmac(kHs, Cat("S"u8.ToArray(), hello, pub, ns)));
        return (reply, Schedule(kHs, X25519(priv, ec), hello, reply));
    }

    private static Keys Schedule(byte[] kHs, byte[] dh, byte[] hello, byte[] reply)
    {
        var th = SHA256.HashData(Cat(hello, reply));
        var prk = HkdfExtract(kHs, dh);
        return new Keys(HkdfExpand(prk, Cat("dcx-ctl/1 c2s"u8.ToArray(), th), 32), HkdfExpand(prk, Cat("dcx-ctl/1 s2c"u8.ToArray(), th), 32),
                        HkdfExpand(prk, Cat("dcx-ctl/1 sid"u8.ToArray(), th), 16), th);
    }

    // ---- sealed channel -----------------------------------------------------------------------------------------------

    /// <summary>Sealed JSON messages over frames, strict counters from 0, limits per §5.</summary>
    public sealed class Channel(Stream s, Keys keys, bool server)
    {
        private readonly byte[] _kOut = server ? keys.S2C : keys.C2S, _kIn = server ? keys.C2S : keys.S2C;
        private readonly byte _dOut = server ? DirS2C : DirC2S, _dIn = server ? DirC2S : DirS2C;
        private long _nOut, _nIn;
        private readonly DateTime _started = DateTime.UtcNow;

        private byte[] Aad(byte d, long n) => Cat(Proto, [d], keys.Sid, BigEndian(n));
        private static byte[] Nonce(long n) => Cat(new byte[4], BigEndian(n));

        private void Live() { if (DateTime.UtcNow - _started > TimeSpan.FromMinutes(30)) throw new ProtocolException("connection too old"); }

        public async Task SendAsync(JsonObject o, CancellationToken ct)
        {
            Live();
            if (_nOut >= MaxMessages) throw new ProtocolException("message limit");
            var plain = Encoding.UTF8.GetBytes(o.ToJsonString());
            if (plain.Length > MaxPlain) throw new ProtocolException("message too large");
            var n = _nOut++;
            await WriteFrameAsync(s, Cat(BigEndian(n), Seal(_kOut, Nonce(n), Aad(_dOut, n), plain)), ct);
        }

        public async Task<JsonObject> RecvAsync(CancellationToken ct)
        {
            Live();
            var frame = await ReadFrameAsync(s, ct);
            if (frame.Length < 24) throw new ProtocolException("short frame");
            var n = (long)BinaryPrimitives.ReadUInt64BigEndian(frame);
            if (n != _nIn || n >= MaxMessages) throw new ProtocolException("counter");
            var plain = Open(_kIn, Nonce(n), Aad(_dIn, n), frame[8..]);
            _nIn++;
            try { return JsonNode.Parse(plain) as JsonObject ?? throw new ProtocolException("not an object"); }
            catch (System.Text.Json.JsonException) { throw new ProtocolException("not JSON"); }
        }
    }

    // ---- end-to-end MACs ----------------------------------------------------------------------------------------------

    public static string OpenProof(byte[] kAuth, byte[] keyId, byte[] sid, byte[] th) => Convert.ToHexString(Hmac(kAuth, Fields("open"u8.ToArray(), keyId, sid, th))).ToLowerInvariant();
    public static string RequestMac(byte[] kAuth, byte[] sid, long seq, string verb, string path, string bodySha256) => Convert.ToHexString(Hmac(kAuth, Fields("req"u8.ToArray(), sid, seq, verb, path, bodySha256))).ToLowerInvariant();
    public static string ResponseMac(byte[] kAuth, byte[] sid, long seq, long status, string bodySha256) => Convert.ToHexString(Hmac(kAuth, Fields("resp"u8.ToArray(), sid, seq, status, bodySha256))).ToLowerInvariant();

    // ---- enrolment string ---------------------------------------------------------------------------------------------

    /// <summary>"dcxctl1.&lt;portal&gt;.&lt;key id hex&gt;.&lt;S base64url&gt;.&lt;check&gt;" → (portal, key id, secret).</summary>
    public static (string Portal, byte[] KeyId, byte[] Secret) ParseEnrolment(string text)
    {
        var parts = (text ?? "").Trim().Split('.');
        if (parts.Length != 5 || parts[0] != "dcxctl1" || !System.Text.RegularExpressions.Regex.IsMatch(parts[1], "^[a-z0-9-]{2,32}$") || !System.Text.RegularExpressions.Regex.IsMatch(parts[2], "^[0-9a-f]{32}$"))
            throw new ProtocolException("not a dcx-ctl/1 enrolment string");
        var body = string.Join(".", parts[..4]);
        var check = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant()[..8];
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(check), Encoding.ASCII.GetBytes(parts[4]))) throw new ProtocolException("the enrolment string's check digits are wrong (copy it again)");
        byte[] secret;
        try { var b = parts[3].Replace('-', '+').Replace('_', '/'); secret = Convert.FromBase64String(b + new string('=', (4 - b.Length % 4) % 4)); }
        catch (FormatException) { throw new ProtocolException("enrolment string secret"); }
        if (secret.Length != 32) throw new ProtocolException("enrolment string secret");
        return (parts[1], Convert.FromHexString(parts[2]), secret);
    }

    /// <summary>Self-test: makes an enrolment string the way dcx-admin does.</summary>
    internal static string MakeEnrolment(string portal, byte[] keyId, byte[] secret)
    {
        var body = $"dcxctl1.{portal}.{Convert.ToHexString(keyId).ToLowerInvariant()}.{Convert.ToBase64String(secret).TrimEnd('=').Replace('+', '-').Replace('/', '_')}";
        return body + "." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant()[..8];
    }
}
