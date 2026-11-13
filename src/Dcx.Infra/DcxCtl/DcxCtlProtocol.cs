using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DcxManager.Services;

namespace DcxManager.DcxCtl;

/// <summary>
/// The DarkCipher Control protocol dcx-ctl/2 (DarkCipherSystems-Website docs/CONTROL-API.md §5; reference implementation
/// deploy/reports/dcxctl_proto.py — this is a byte-for-byte port of it). Nothing here invents cryptography: X25519 and
/// ML-KEM-768 (FIPS 203, through <see cref="Pqc"/>) come from BouncyCastle, ChaCha20-Poly1305, HMAC-SHA256 and HKDF from
/// .NET. <see cref="SelfTest"/> checks them against the RFC 5869 / 7748 / 8439 vectors and runs a whole ML-KEM exchange;
/// the client refuses to run if any fails. Every MAC / KDF input is a list of length-prefixed fields (no splicing).
/// The session keys come from the X25519 secret AND the ML-KEM secret together, salted with the pre-shared handshake
/// key, so a recorded session stays private unless both exchanges are broken. Version 1 (X25519 only) is not spoken:
/// portals refuse it since 2026-11-10, and a portal that doesn't confirm version 2 is refused here.
/// </summary>
public static class DcxCtlProtocol
{
    public static readonly byte[] Proto = "dcx-ctl/2"u8.ToArray();
    private static readonly byte[] Magic = "DCXCTL2"u8.ToArray();
    public const int Version = 2, KeyIdLen = 16, KemPub = 1184, KemCt = 1088;
    /// <summary>magic 7 | version 1 | key id 16 | X25519 32 | ML-KEM public 1184 | nonce 32 | time 8 | MAC 32 = 1,312.</summary>
    public const int HelloLen = 7 + 1 + KeyIdLen + 32 + KemPub + 32 + 8 + 32;
    /// <summary>X25519 32 | ML-KEM ciphertext 1088 | nonce 32 | MAC 32 = 1,184.</summary>
    public const int ReplyLen = 32 + KemCt + 32 + 32;
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

    /// <summary>S (32 bytes) → (K_hs, K_auth). These labels did not change with version 2, so an enrolled key works as it is.</summary>
    public static (byte[] Hs, byte[] Auth) ClientKeys(byte[] secret)
    {
        if (secret.Length != 32) throw new ProtocolException("secret must be 32 bytes");
        var prk = HkdfExtract("dcx-ctl/1 client key"u8.ToArray(), secret);
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

    /// <summary>RFC 5869, RFC 7748 and RFC 8439 vectors (the same as the Python side checks) and a whole ML-KEM-768 exchange
    /// (both sides agree, a changed ciphertext or another key never gives the same secret, the sizes fit the wire format).
    /// Throws on any mismatch.</summary>
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
        var (seed, pub) = Pqc.KemGenerate();
        var (ct, secret) = Pqc.KemEncapsulate(pub);
        var changed = (byte[])ct.Clone(); changed[0] ^= 1;
        ok = ok && pub.Length == KemPub && ct.Length == KemCt && secret.Length == 32 && secret.Any(b => b != 0)
                && Pqc.KemDecapsulate(seed, ct).SequenceEqual(secret) && !Pqc.KemDecapsulate(seed, changed).SequenceEqual(secret)
                && !Pqc.KemDecapsulate(Pqc.KemGenerate().Seed, ct).SequenceEqual(secret);
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

    /// <summary>The hello and its one-use private halves (X25519 key, ML-KEM seed); <see cref="CheckReply"/> wipes them.</summary>
    public static (byte[] Hello, byte[] Priv, byte[] KemSeed) MakeHello(byte[] keyId, byte[] kHs, long? now = null)
    {
        if (keyId.Length != KeyIdLen) throw new ProtocolException("bad key id");
        var (priv, pub) = X25519KeyPair();
        var (kemSeed, kemPub) = Pqc.KemGenerate();
        if (kemPub.Length != KemPub) throw new ProtocolException("ML-KEM-768 public key has the wrong size");
        var body = Cat(Magic, [(byte)Version], keyId, pub, kemPub, RandomNumberGenerator.GetBytes(32), BigEndian(now ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        return (Cat(body, Hmac(kHs, body)), priv, kemSeed);
    }

    public sealed record Keys(byte[] C2S, byte[] S2C, byte[] Sid, byte[] Th);

    /// <summary>Client side: the server's MAC covers our hello, its X25519 key, the ML-KEM ciphertext and its nonce, so
    /// none of them was changed on the way. Wipes <paramref name="priv"/> and <paramref name="kemSeed"/>.</summary>
    public static Keys CheckReply(byte[] hello, byte[] reply, byte[] kHs, byte[] priv, byte[] kemSeed)
    {
        try
        {
            if (reply.Length != ReplyLen) throw new ProtocolException("the portal's handshake reply has the wrong size (it must speak dcx-ctl/2)");
            var body = reply[..^32];
            if (!CryptographicOperations.FixedTimeEquals(Hmac(kHs, Cat("S"u8.ToArray(), hello, body)), reply[^32..])) throw new ProtocolException("the server didn't prove it holds the key");
            var dh = X25519(priv, body[..32]);
            var ss = Pqc.KemDecapsulate(kemSeed, body[32..(32 + KemCt)]);
            try { return Schedule(kHs, Cat(dh, ss), hello, reply); }
            finally { CryptographicOperations.ZeroMemory(dh); CryptographicOperations.ZeroMemory(ss); }
        }
        finally { CryptographicOperations.ZeroMemory(priv); CryptographicOperations.ZeroMemory(kemSeed); }
    }

    /// <summary>Server side (the self-test's fake portal only; it checks what the real gateway checks).</summary>
    internal static (byte[] Reply, Keys Keys) MakeReply(byte[] hello, byte[] kHs, long? now = null)
    {
        if (hello.Length != HelloLen || !hello.AsSpan(0, 7).SequenceEqual(Magic) || hello[7] != Version) throw new ProtocolException("not a dcx-ctl/2 hello");
        if (!CryptographicOperations.FixedTimeEquals(Hmac(kHs, hello[..^32]), hello[^32..])) throw new ProtocolException("hello MAC");
        var ts = (long)BinaryPrimitives.ReadUInt64BigEndian(hello.AsSpan(hello.Length - 40, 8));
        if (Math.Abs((now ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()) - ts) > 60) throw new ProtocolException("hello clock");
        var ec = hello[24..56];
        var kemPub = hello[56..(56 + KemPub)];
        var (priv, pub) = X25519KeyPair();
        var (ct, ss) = Pqc.KemEncapsulate(kemPub);
        var body = Cat(pub, ct, RandomNumberGenerator.GetBytes(32));
        var reply = Cat(body, Hmac(kHs, Cat("S"u8.ToArray(), hello, body)));
        return (reply, Schedule(kHs, Cat(X25519(priv, ec), ss), hello, reply));
    }

    /// <summary>HKDF-Extract(salt = K_hs, ikm = dh | ss), then one key per label bound to the transcript hash.</summary>
    private static Keys Schedule(byte[] kHs, byte[] secret, byte[] hello, byte[] reply)
    {
        var th = SHA256.HashData(Cat(hello, reply));
        var prk = HkdfExtract(kHs, secret);
        try
        {
            return new Keys(HkdfExpand(prk, Cat("dcx-ctl/2 c2s"u8.ToArray(), th), 32), HkdfExpand(prk, Cat("dcx-ctl/2 s2c"u8.ToArray(), th), 32),
                            HkdfExpand(prk, Cat("dcx-ctl/2 sid"u8.ToArray(), th), 16), th);
        }
        finally { CryptographicOperations.ZeroMemory(prk); }
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
            throw new ProtocolException("not a dcx-ctl enrolment string (it starts with dcxctl1.)");
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
