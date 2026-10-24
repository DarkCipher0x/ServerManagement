using System.Security.Cryptography;

namespace DcxManager.Import;

/// <summary>
/// AES-GCM for any IV length (NIST SP 800-38D), decrypt-mostly. .NET's AesGcm accepts only 96-bit nonces, but mRemoteNG
/// (BouncyCastle) seals passwords with 128-bit ones. Straight from the specification: H = E(K, 0¹²⁸); J₀ = IV‖0³¹‖1 for 96
/// bits, else GHASH(IV padded ‖ [len]₆₄); CTR from inc32(J₀); tag = E(K, J₀) ⊕ GHASH(A, C). The tag is compared in
/// constant time before any plaintext is returned. The self-test checks it against .NET's AesGcm for 96-bit nonces.
/// </summary>
public static class GcmAnyIv
{
    public static byte[] Decrypt(byte[] key, byte[] iv, byte[] ciphertext, byte[] tag, byte[] aad)
    {
        if (tag.Length != 16) throw new CryptographicException("unsupported tag length");
        using var aes = Aes.Create(); aes.Key = key;
        var h = Block(aes, new byte[16]);
        var j0 = J0(h, iv);
        var expected = Tag(aes, h, j0, aad, ciphertext);
        if (!CryptographicOperations.FixedTimeEquals(expected, tag)) throw new CryptographicException("The authentication tag doesn't match (wrong password or damaged data).");
        return Ctr(aes, Inc32(j0), ciphertext);
    }

    public static (byte[] Ciphertext, byte[] Tag) Encrypt(byte[] key, byte[] iv, byte[] plaintext, byte[] aad)
    {
        using var aes = Aes.Create(); aes.Key = key;
        var h = Block(aes, new byte[16]);
        var j0 = J0(h, iv);
        var c = Ctr(aes, Inc32(j0), plaintext);
        return (c, Tag(aes, h, j0, aad, c));
    }

    private static byte[] Block(Aes aes, byte[] b) => aes.EncryptEcb(b, PaddingMode.None);

    private static byte[] J0(byte[] h, byte[] iv)
    {
        if (iv.Length == 12) { var j = new byte[16]; iv.CopyTo(j, 0); j[15] = 1; return j; }
        var y = new byte[16];
        y = GHashBlocks(h, y, iv);
        var len = new byte[16];
        WriteBits(len, 8, (ulong)iv.Length * 8);
        return Mul(Xor(y, len), h);
    }

    private static byte[] Tag(Aes aes, byte[] h, byte[] j0, byte[] aad, byte[] c)
    {
        var s = GHashBlocks(h, new byte[16], aad);
        s = GHashBlocks(h, s, c);
        var len = new byte[16];
        WriteBits(len, 0, (ulong)aad.Length * 8);
        WriteBits(len, 8, (ulong)c.Length * 8);
        s = Mul(Xor(s, len), h);
        return Xor(Block(aes, j0), s);
    }

    /// <summary>Folds <paramref name="data"/> (zero-padded to whole blocks) into the running GHASH value.</summary>
    private static byte[] GHashBlocks(byte[] h, byte[] y, byte[] data)
    {
        for (int i = 0; i < data.Length; i += 16)
        {
            var block = new byte[16];
            Array.Copy(data, i, block, 0, Math.Min(16, data.Length - i));
            y = Mul(Xor(y, block), h);
        }
        return y;
    }

    private static byte[] Ctr(Aes aes, byte[] counter, byte[] input)
    {
        var output = new byte[input.Length];
        var cb = (byte[])counter.Clone();
        for (int i = 0; i < input.Length; i += 16)
        {
            var ks = Block(aes, cb);
            for (int k = 0; k < 16 && i + k < input.Length; k++) output[i + k] = (byte)(input[i + k] ^ ks[k]);
            cb = Inc32(cb);
        }
        return output;
    }

    private static byte[] Inc32(byte[] b)
    {
        var r = (byte[])b.Clone();
        for (int i = 15; i >= 12; i--) if (++r[i] != 0) break;
        return r;
    }

    private static byte[] Xor(byte[] a, byte[] b) { var r = new byte[16]; for (int i = 0; i < 16; i++) r[i] = (byte)(a[i] ^ b[i]); return r; }

    private static void WriteBits(byte[] dst, int offset, ulong v) { for (int i = 7; i >= 0; i--) { dst[offset + i] = (byte)v; v >>= 8; } }

    /// <summary>Multiplication in GF(2¹²⁸) with the GCM polynomial (bit 0 = the most significant bit of byte 0).</summary>
    private static byte[] Mul(byte[] x, byte[] y)
    {
        var z = new byte[16];
        var v = (byte[])y.Clone();
        for (int i = 0; i < 128; i++)
        {
            if (((x[i >> 3] >> (7 - (i & 7))) & 1) != 0) for (int k = 0; k < 16; k++) z[k] ^= v[k];
            bool lsb = (v[15] & 1) != 0;
            for (int k = 15; k > 0; k--) v[k] = (byte)((v[k] >> 1) | (v[k - 1] << 7));
            v[0] >>= 1;
            if (lsb) v[0] ^= 0xE1;
        }
        return z;
    }
}
