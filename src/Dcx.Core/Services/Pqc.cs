using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Kems;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace DcxManager.Services;

/// <summary>
/// Post-quantum primitives (0.6.12, ADR-090; DarkCipher0x: "implement post-quantum across the entire app"): ML-KEM-768
/// (FIPS 203) for key establishment and ML-DSA-65 (FIPS 204) for signatures, from BouncyCastle 2.7.0 — the version SSH.NET
/// already brings in, so the same code runs on Windows 10 and 11 alike. Nothing here is used alone: every caller combines
/// it with the classical P-256 key it already had (hybrid), so a flaw in either half doesn't break the whole. Private keys
/// are kept as their 64- / 32-byte seeds, which the callers store in the encrypted store like every other key.
/// </summary>
public static class Pqc
{
    public const string KemName = "ML-KEM-768";
    public const string SigName = "ML-DSA-65";
    /// <summary>The marker of a hybrid key, signature or wrap in text form: <c>pq1:&lt;classical&gt;.&lt;post-quantum&gt;</c>.</summary>
    public const string Prefix = "pq1:";

    private static readonly SecureRandom Random = new();

    // ---- ML-KEM-768 ------------------------------------------------------------------------------------------------

    /// <summary>A new ML-KEM-768 key: its private seed (64 bytes) and public key (1,184 bytes).</summary>
    public static (byte[] Seed, byte[] Pub) KemGenerate()
    {
        var gen = new MLKemKeyPairGenerator();
        gen.Init(new MLKemKeyGenerationParameters(Random, MLKemParameters.ml_kem_768));
        var kp = gen.GenerateKeyPair();
        var priv = (MLKemPrivateKeyParameters)kp.Private;
        return (priv.GetSeed(), ((MLKemPublicKeyParameters)kp.Public).GetEncoded());
    }

    public static byte[] KemPublicOf(byte[] seed) => KemPrivate(seed).GetPublicKey().GetEncoded();

    /// <summary>Encapsulates to a public key: the ciphertext (1,088 bytes) and the 32-byte shared secret.</summary>
    public static (byte[] Ct, byte[] Secret) KemEncapsulate(byte[] pub)
    {
        var enc = new MLKemEncapsulator(MLKemParameters.ml_kem_768);
        enc.Init(new ParametersWithRandom(MLKemPublicKeyParameters.FromEncoding(MLKemParameters.ml_kem_768, pub), Random));
        var ct = new byte[enc.EncapsulationLength];
        var ss = new byte[enc.SecretLength];
        enc.Encapsulate(ct, 0, ct.Length, ss, 0, ss.Length);
        return (ct, ss);
    }

    /// <summary>The shared secret from a ciphertext (ML-KEM rejects implicitly: a wrong ciphertext yields a different
    /// secret, so the AEAD that follows fails).</summary>
    public static byte[] KemDecapsulate(byte[] seed, byte[] ct)
    {
        var dec = new MLKemDecapsulator(MLKemParameters.ml_kem_768);
        dec.Init(KemPrivate(seed));
        if (ct.Length != dec.EncapsulationLength) throw new CryptographicException("ML-KEM ciphertext has the wrong length");
        var ss = new byte[dec.SecretLength];
        dec.Decapsulate(ct, 0, ct.Length, ss, 0, ss.Length);
        return ss;
    }

    private static MLKemPrivateKeyParameters KemPrivate(byte[] seed) => MLKemPrivateKeyParameters.FromSeed(MLKemParameters.ml_kem_768, seed);

    // ---- ML-DSA-65 -------------------------------------------------------------------------------------------------

    /// <summary>A new ML-DSA-65 key: its private seed (32 bytes) and public key (1,952 bytes).</summary>
    public static (byte[] Seed, byte[] Pub) SigGenerate()
    {
        var gen = new MLDsaKeyPairGenerator();
        gen.Init(new MLDsaKeyGenerationParameters(Random, MLDsaParameters.ml_dsa_65));
        var kp = gen.GenerateKeyPair();
        return (((MLDsaPrivateKeyParameters)kp.Private).GetSeed(), ((MLDsaPublicKeyParameters)kp.Public).GetEncoded());
    }

    public static byte[] SigPublicOf(byte[] seed) => SigPrivate(seed).GetPublicKey().GetEncoded();

    /// <summary>An ML-DSA-65 signature (3,309 bytes, hedged: fresh randomness each time).</summary>
    public static byte[] Sign(byte[] seed, byte[] data)
    {
        var s = new MLDsaSigner(MLDsaParameters.ml_dsa_65, deterministic: false);
        s.Init(true, new ParametersWithRandom(SigPrivate(seed), Random));
        s.BlockUpdate(data, 0, data.Length);
        return s.GenerateSignature();
    }

    public static bool Verify(byte[] pub, byte[] data, byte[] sig)
    {
        try
        {
            var s = new MLDsaSigner(MLDsaParameters.ml_dsa_65, deterministic: false);
            s.Init(false, MLDsaPublicKeyParameters.FromEncoding(MLDsaParameters.ml_dsa_65, pub));
            s.BlockUpdate(data, 0, data.Length);
            return s.VerifySignature(sig);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException) { return false; }
    }

    private static MLDsaPrivateKeyParameters SigPrivate(byte[] seed) => MLDsaPrivateKeyParameters.FromSeed(MLDsaParameters.ml_dsa_65, seed);

    // ---- hybrid helpers --------------------------------------------------------------------------------------------

    /// <summary>One key from a classical and a post-quantum shared secret (HKDF-SHA256 over both, in that order, bound to
    /// <paramref name="salt"/> and <paramref name="info"/>): breaking it needs breaking both.</summary>
    public static byte[] Combine(byte[] classical, byte[] postQuantum, byte[] salt, string info)
    {
        var ikm = new byte[classical.Length + postQuantum.Length];
        classical.CopyTo(ikm, 0); postQuantum.CopyTo(ikm, classical.Length);
        try { return HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 32, salt, Encoding.UTF8.GetBytes(info)); }
        finally { CryptographicOperations.ZeroMemory(ikm); }
    }

    /// <summary>Splits <c>pq1:A.B</c> into its two base64 parts; null for anything else (a classical value).</summary>
    public static (string Classical, string PostQuantum)? Split(string? s)
    {
        if (s == null || !s.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        var body = s[Prefix.Length..];
        var dot = body.IndexOf('.');
        return dot > 0 && dot < body.Length - 1 && body.IndexOf('.', dot + 1) < 0 ? (body[..dot], body[(dot + 1)..]) : null;
    }

    public static string Join(string classical, string postQuantum) => Prefix + classical + "." + postQuantum;

    /// <summary>Self-check: a round trip of both primitives (the self-test and the About page's crypto line).</summary>
    public static bool Works()
    {
        try
        {
            var (ks, kp) = KemGenerate();
            var (ct, ss) = KemEncapsulate(kp);
            var ss2 = KemDecapsulate(ks, ct);
            var (sseed, spub) = SigGenerate();
            var msg = Encoding.UTF8.GetBytes("dcx-pqc-selfcheck");
            return ss.AsSpan().SequenceEqual(ss2) && KemPublicOf(ks).AsSpan().SequenceEqual(kp) && Verify(spub, msg, Sign(sseed, msg)) && !Verify(spub, Encoding.UTF8.GetBytes("other"), Sign(sseed, msg));
        }
        catch (Exception ex) { Log.Write("Post-quantum self-check", ex); return false; }
    }
}
