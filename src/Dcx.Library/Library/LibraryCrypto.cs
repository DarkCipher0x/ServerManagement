using System.Security.Cryptography;
using System.Text;
using DcxManager.Services;

namespace DcxManager.Library;

/// <summary>
/// A key that receives wrapped secrets (ADR-033; hybrid since 0.6.12, ADR-090): P-256 ECDH, and — for keys made from 0.6.12
/// on — ML-KEM-768 alongside it. Its public form is the classical SPKI (base64), or <c>pq1:&lt;SPKI&gt;.&lt;ML-KEM public&gt;</c>
/// for a hybrid key; its private form likewise (<c>pq1:&lt;PKCS#8&gt;.&lt;ML-KEM seed&gt;</c>). Member keys and the audit
/// generations' keys are both of this kind.
/// </summary>
public sealed class KemKey : IDisposable
{
    public ECDiffieHellman Ecdh { get; }
    /// <summary>The ML-KEM-768 private seed, or null for a classical (pre-0.6.12) key.</summary>
    public byte[]? KemSeed { get; }
    public string Pub { get; }
    public bool PostQuantum => KemSeed != null;

    private KemKey(ECDiffieHellman ecdh, byte[]? kemSeed)
    {
        if (ecdh.KeySize != 256) throw new CryptographicException("keys must be P-256");
        Ecdh = ecdh; KemSeed = kemSeed;
        var spki = Convert.ToBase64String(ecdh.ExportSubjectPublicKeyInfo());
        Pub = kemSeed == null ? spki : Pqc.Join(spki, Convert.ToBase64String(Pqc.KemPublicOf(kemSeed)));
    }

    /// <summary>A new key: hybrid unless <paramref name="postQuantum"/> is false (tests of the classical path only).</summary>
    public static KemKey Create(bool postQuantum = true) =>
        new(ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256), postQuantum ? Pqc.KemGenerate().Seed : null);

    /// <summary>From its private form (either kind).</summary>
    public static KemKey Import(string priv)
    {
        var parts = Pqc.Split(priv);
        var e = ECDiffieHellman.Create();
        e.ImportPkcs8PrivateKey(Convert.FromBase64String(parts?.Classical ?? priv), out _);
        return new(e, parts is { } p ? Convert.FromBase64String(p.PostQuantum) : null);
    }

    /// <summary>From bytes that were wrapped for an admin: the private form as UTF-8 (0.6.12 on), or a raw PKCS#8 key
    /// (audit generations made before 0.6.12).</summary>
    public static KemKey ImportBytes(byte[] b)
    {
        if (b.Length > 4 && Encoding.ASCII.GetString(b, 0, 4) == Pqc.Prefix) return Import(Encoding.UTF8.GetString(b));
        var e = ECDiffieHellman.Create();
        e.ImportPkcs8PrivateKey(b, out _);
        return new(e, null);
    }

    public string ExportPrivate()
    {
        var pkcs8 = Convert.ToBase64String(Ecdh.ExportPkcs8PrivateKey());
        return KemSeed == null ? pkcs8 : Pqc.Join(pkcs8, Convert.ToBase64String(KemSeed));
    }

    public void Dispose() { Ecdh.Dispose(); if (KemSeed != null) CryptographicOperations.ZeroMemory(KemSeed); }
}

/// <summary>A key that signs (ADR-033; hybrid since 0.6.12): P-256 ECDSA, and ML-DSA-65 alongside it for keys made from
/// 0.6.12 on. A hybrid key's signature is <c>pq1:&lt;ECDSA&gt;.&lt;ML-DSA&gt;</c> and verifies only when BOTH halves do.</summary>
public sealed class SigKey : IDisposable
{
    public ECDsa Ecdsa { get; }
    public byte[]? DsaSeed { get; }
    public string Pub { get; }
    public bool PostQuantum => DsaSeed != null;

    private SigKey(ECDsa ecdsa, byte[]? dsaSeed)
    {
        if (ecdsa.KeySize != 256) throw new CryptographicException("keys must be P-256");
        Ecdsa = ecdsa; DsaSeed = dsaSeed;
        var spki = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());
        Pub = dsaSeed == null ? spki : Pqc.Join(spki, Convert.ToBase64String(Pqc.SigPublicOf(dsaSeed)));
    }

    public static SigKey Create(bool postQuantum = true) =>
        new(ECDsa.Create(ECCurve.NamedCurves.nistP256), postQuantum ? Pqc.SigGenerate().Seed : null);

    public static SigKey Import(string priv)
    {
        var parts = Pqc.Split(priv);
        var s = ECDsa.Create();
        s.ImportPkcs8PrivateKey(Convert.FromBase64String(parts?.Classical ?? priv), out _);
        return new(s, parts is { } p ? Convert.FromBase64String(p.PostQuantum) : null);
    }

    public string ExportPrivate()
    {
        var pkcs8 = Convert.ToBase64String(Ecdsa.ExportPkcs8PrivateKey());
        return DsaSeed == null ? pkcs8 : Pqc.Join(pkcs8, Convert.ToBase64String(DsaSeed));
    }

    public string Sign(string text)
    {
        var data = Encoding.UTF8.GetBytes(text);
        var ec = Convert.ToBase64String(Ecdsa.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        return DsaSeed == null ? ec : Pqc.Join(ec, Convert.ToBase64String(Pqc.Sign(DsaSeed, data)));
    }

    public void Dispose() { Ecdsa.Dispose(); if (DsaSeed != null) CryptographicOperations.ZeroMemory(DsaSeed); }
}

/// <summary>A member's keys (ADR-033): one to receive wrapped keys, one to sign what they write. The private halves live
/// only in the member's own encrypted store; the share sees the public halves. New members get hybrid post-quantum keys
/// (0.6.12); members who joined earlier keep their classical keys until they join again.</summary>
public sealed class MemberKeys : IDisposable
{
    public KemKey Enc { get; }
    public SigKey Sig { get; }
    public string EncPub => Enc.Pub;
    public string SigPub => Sig.Pub;
    public bool PostQuantum => Enc.PostQuantum && Sig.PostQuantum;

    private MemberKeys(KemKey enc, SigKey sig) { Enc = enc; Sig = sig; }

    public static MemberKeys Create(bool postQuantum = true) => new(KemKey.Create(postQuantum), SigKey.Create(postQuantum));

    public static MemberKeys Import(string encPriv, string sigPriv) => new(KemKey.Import(encPriv), SigKey.Import(sigPriv));

    public (string Enc, string Sig) ExportPrivate() => (Enc.ExportPrivate(), Sig.ExportPrivate());

    public string Fingerprint => LibraryCrypto.Fingerprint(EncPub, SigPub);
    public string Sign(string text) => Sig.Sign(text);

    public void Dispose() { Enc.Dispose(); Sig.Dispose(); }
}

/// <summary>A collection key, the metadata key or an audit private key wrapped for one recipient: ECDH-ES (an ephemeral
/// P-256 key) → HKDF-SHA256 bound to the context → AES-256-GCM, the same construction as HPKE base mode. For a hybrid
/// recipient (0.6.12) an ML-KEM-768 encapsulation (<see cref="Kem"/>) is added and both secrets go into the HKDF.</summary>
public sealed class Wrap
{
    public string Epk { get; set; } = "";
    /// <summary>The ML-KEM-768 ciphertext (base64) for a hybrid recipient; empty for a classical one.</summary>
    public string Kem { get; set; } = "";
    public string Ct { get; set; } = "";
}

public static class LibraryCrypto
{
    public const string Format = "dcxlib1";

    public static byte[] RandomKey() => RandomNumberGenerator.GetBytes(32);
    public static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>SHA-256 over both public keys (a hybrid key's post-quantum half included), as 8 groups of 4 hex characters,
    /// read aloud to check a member out of band.</summary>
    public static string Fingerprint(string encPub, string sigPub)
    {
        var h = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(encPub + "|" + sigPub)))[..32];
        return string.Join(" ", Enumerable.Range(0, 8).Select(i => h.Substring(i * 4, 4)));
    }

    public static string NormalizeFingerprint(string fp) => new string(fp.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();

    /// <summary>Whether a public key is hybrid post-quantum.</summary>
    public static bool IsPostQuantum(string pub) => Pqc.Split(pub) != null;

    /// <summary>AES-256-GCM: nonce(12) | tag(16) | ciphertext, with <paramref name="aad"/> authenticated.</summary>
    public static byte[] Seal(byte[] key, byte[] plain, string aad)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var ct = new byte[plain.Length];
        using (var g = new AesGcm(key, 16)) g.Encrypt(nonce, plain, ct, tag, Encoding.UTF8.GetBytes(aad));
        var o = new byte[28 + ct.Length];
        nonce.CopyTo(o, 0); tag.CopyTo(o, 12); ct.CopyTo(o, 28);
        return o;
    }

    public static byte[] Open(byte[] key, byte[] blob, string aad)
    {
        if (blob.Length < 28) throw new CryptographicException("sealed data is truncated");
        var plain = new byte[blob.Length - 28];
        using var g = new AesGcm(key, 16);
        g.Decrypt(blob.AsSpan(0, 12), blob.AsSpan(28), blob.AsSpan(12, 16), plain, Encoding.UTF8.GetBytes(aad));
        return plain;
    }

    public static string SealText(byte[] key, string text, string aad) => Convert.ToBase64String(Seal(key, Encoding.UTF8.GetBytes(text), aad));
    public static string OpenText(byte[] key, string b64, string aad) => Encoding.UTF8.GetString(Open(key, Convert.FromBase64String(b64), aad));

    private static byte[] WrapKey(byte[] shared, string epk, string recipientPub, string context) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, Encoding.ASCII.GetBytes(epk + "|" + recipientPub), Encoding.UTF8.GetBytes(Format + "-wrap|" + context));

    /// <summary>The hybrid wrap key: the ECDH and ML-KEM secrets together, bound to the ephemeral key, the recipient's
    /// whole public key and the ML-KEM ciphertext.</summary>
    private static byte[] HybridWrapKey(byte[] ecdh, byte[] kem, string epk, string recipientPub, string kemCt, string context) =>
        Pqc.Combine(ecdh, kem, Encoding.ASCII.GetBytes(epk + "|" + recipientPub + "|" + kemCt), Format + "-wrap-pq|" + context);

    /// <summary>Encrypts <paramref name="secret"/> to a recipient's public key: classical (SPKI, base64) or hybrid (pq1:…).
    /// A malformed key is a <see cref="CryptographicException"/> (as callers expect), never another exception type.</summary>
    public static Wrap WrapFor(string recipientEncPub, byte[] secret, string context)
    {
        try { return WrapForCore(recipientEncPub, secret, context); }
        catch (Exception ex) when (ex is FormatException or ArgumentException) { throw new CryptographicException("a malformed public key", ex); }
    }

    private static Wrap WrapForCore(string recipientEncPub, byte[] secret, string context)
    {
        var parts = Pqc.Split(recipientEncPub);
        using var eph = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var peer = ECDiffieHellman.Create();
        peer.ImportSubjectPublicKeyInfo(Convert.FromBase64String(parts?.Classical ?? recipientEncPub), out _);
        var epk = Convert.ToBase64String(eph.ExportSubjectPublicKeyInfo());
        var shared = eph.DeriveRawSecretAgreement(peer.PublicKey);
        byte[]? kemSecret = null;
        try
        {
            if (parts is not { } p) return new Wrap { Epk = epk, Ct = Convert.ToBase64String(Seal(WrapKey(shared, epk, recipientEncPub, context), secret, context)) };
            var (kemCt, ss) = Pqc.KemEncapsulate(Convert.FromBase64String(p.PostQuantum));
            kemSecret = ss;
            var kct = Convert.ToBase64String(kemCt);
            var k = HybridWrapKey(shared, ss, epk, recipientEncPub, kct, context);
            return new Wrap { Epk = epk, Kem = kct, Ct = Convert.ToBase64String(Seal(k, secret, context)) };
        }
        finally { CryptographicOperations.ZeroMemory(shared); if (kemSecret != null) CryptographicOperations.ZeroMemory(kemSecret); }
    }

    /// <summary>Opens a wrap with the recipient's key. A hybrid key only accepts a hybrid wrap: a classical wrap for a
    /// post-quantum recipient is refused (no silent downgrade). Anything malformed (a tampered share) is a
    /// <see cref="CryptographicException"/>, so callers skip that item instead of failing the whole sync.</summary>
    public static byte[] Unwrap(KemKey mine, Wrap w, string context)
    {
        try { return UnwrapCore(mine, w, context); }
        catch (Exception ex) when (ex is FormatException or ArgumentException) { throw new CryptographicException("a malformed wrap", ex); }
    }

    private static byte[] UnwrapCore(KemKey mine, Wrap w, string context)
    {
        using var peer = ECDiffieHellman.Create();
        peer.ImportSubjectPublicKeyInfo(Convert.FromBase64String(w.Epk), out _);
        var shared = mine.Ecdh.DeriveRawSecretAgreement(peer.PublicKey);
        byte[]? kemSecret = null;
        try
        {
            if (!mine.PostQuantum)
            {
                if (w.Kem.Length > 0) throw new CryptographicException("a post-quantum wrap for a classical key");
                return Open(WrapKey(shared, w.Epk, mine.Pub, context), Convert.FromBase64String(w.Ct), context);
            }
            if (w.Kem.Length == 0) throw new CryptographicException("a classical wrap for a post-quantum key (downgrade refused)");
            kemSecret = Pqc.KemDecapsulate(mine.KemSeed!, Convert.FromBase64String(w.Kem));
            return Open(HybridWrapKey(shared, kemSecret, w.Epk, mine.Pub, w.Kem, context), Convert.FromBase64String(w.Ct), context);
        }
        finally { CryptographicOperations.ZeroMemory(shared); if (kemSecret != null) CryptographicOperations.ZeroMemory(kemSecret); }
    }

    /// <summary>Verifies a signature against a signer's public key. A hybrid key needs a hybrid signature with BOTH halves
    /// valid; a classical key a classical one.</summary>
    public static bool Verify(string sigPub, string text, string sig)
    {
        try
        {
            var data = Encoding.UTF8.GetBytes(text);
            var keyParts = Pqc.Split(sigPub);
            var sigParts = Pqc.Split(sig);
            if ((keyParts == null) != (sigParts == null)) return false;   // a hybrid signer's classical-only signature is refused
            using var k = ECDsa.Create();
            k.ImportSubjectPublicKeyInfo(Convert.FromBase64String(keyParts?.Classical ?? sigPub), out _);
            if (!k.VerifyData(data, Convert.FromBase64String(sigParts?.Classical ?? sig), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) return false;
            return keyParts is not { } kp || Pqc.Verify(Convert.FromBase64String(kp.PostQuantum), data, Convert.FromBase64String(sigParts!.Value.PostQuantum));
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException) { return false; }
    }

    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
