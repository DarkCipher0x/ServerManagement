using System.Security.Cryptography;
using System.Text;

namespace DcxManager.Services;

/// <summary>RFC 6238 TOTP (HMAC-SHA1, 6 digits, 30 s) — works with Microsoft/Google Authenticator, Authy, 1Password, Bitwarden…</summary>
public static class Totp
{
    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(20);

    public static string Code(byte[] secret, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        for (int i = 7; i >= 0; i--) { counter[i] = (byte)(step & 0xFF); step >>= 8; }
#pragma warning disable CA5350 // RFC 6238 and every authenticator app use HMAC-SHA1; HMAC (not plain SHA-1) is not affected by SHA-1 collisions.
        var hash = HMACSHA1.HashData(secret, counter);
#pragma warning restore CA5350
        int o = hash[^1] & 0x0F;
        int bin = (hash[o] & 0x7F) << 24 | hash[o + 1] << 16 | hash[o + 2] << 8 | hash[o + 3];
        return (bin % 1_000_000).ToString("D6");
    }

    public static string Now(byte[] secret) => Code(secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);

    /// <summary>A sign-in's authenticator setting as password managers store it (0.6.20): an <c>otpauth://totp/…</c> link
    /// (secret, digits, period, SHA1/SHA256/SHA512), a bare Base32 secret (spaces and dashes allowed) or Bitwarden's
    /// <c>steam://SECRET</c>.</summary>
    public sealed record Spec(byte[] Secret, int Digits, int Period, string Algorithm, bool Steam)
    {
        /// <summary>Never the secret.</summary>
        public override string ToString() => $"TOTP {Algorithm} {Digits} digits / {Period} s";
    }

    /// <summary>The setting read, or null when it isn't one of the forms above (or the secret is too short).</summary>
    public static Spec? Parse(string? text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0 || t.Length > 2048) return null;
        if (t.StartsWith("steam://", StringComparison.OrdinalIgnoreCase))
            return FromBase32(t[8..]) is { Length: >= 10 } s ? new Spec(s, 5, 30, "SHA1", true) : null;
        if (!t.StartsWith("otpauth://", StringComparison.OrdinalIgnoreCase))
            return FromBase32(t) is { Length: >= 10 } raw ? new Spec(raw, 6, 30, "SHA1", false) : null;
        if (!System.Uri.TryCreate(t, UriKind.Absolute, out var u) || !u.Host.Equals("totp", StringComparison.OrdinalIgnoreCase)) return null;
        var q = u.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2)).Where(p => p.Length == 2)
            .GroupBy(p => System.Uri.UnescapeDataString(p[0]).ToLowerInvariant()).ToDictionary(g => g.Key, g => System.Uri.UnescapeDataString(g.First()[1]));
        if (FromBase32(q.GetValueOrDefault("secret")) is not { Length: >= 10 } secret) return null;
        int digits = int.TryParse(q.GetValueOrDefault("digits"), out var d) && d is >= 6 and <= 10 ? d : 6;
        int period = int.TryParse(q.GetValueOrDefault("period"), out var p) && p is >= 10 and <= 300 ? p : 30;
        var algo = (q.GetValueOrDefault("algorithm") ?? "SHA1").ToUpperInvariant() switch { "SHA256" => "SHA256", "SHA512" => "SHA512", _ => "SHA1" };
        return new Spec(secret, digits, period, algo, false);
    }

    /// <summary>The code for this moment, and the seconds it stays valid.</summary>
    public static (string Code, int SecondsLeft) Now(Spec s, DateTimeOffset? at = null)
    {
        var now = (at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        return (Code(s, now / s.Period), (int)(s.Period - now % s.Period));
    }

    public static string Code(Spec s, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        for (int i = 7; i >= 0; i--) { counter[i] = (byte)(step & 0xFF); step >>= 8; }
#pragma warning disable CA5350 // RFC 6238: SHA1 is what most sites use; HMAC (not plain SHA-1) is not affected by SHA-1 collisions.
        var hash = s.Algorithm switch { "SHA256" => HMACSHA256.HashData(s.Secret, counter), "SHA512" => HMACSHA512.HashData(s.Secret, counter), _ => HMACSHA1.HashData(s.Secret, counter) };
#pragma warning restore CA5350
        int o = hash[^1] & 0x0F;
        int bin = (hash[o] & 0x7F) << 24 | hash[o + 1] << 16 | hash[o + 2] << 8 | hash[o + 3];
        if (s.Steam)
        {
            const string SteamChars = "23456789BCDFGHJKMNPQRTVWXY";
            var sb = new StringBuilder();
            for (int i = 0; i < 5; i++) { sb.Append(SteamChars[bin % SteamChars.Length]); bin /= SteamChars.Length; }
            return sb.ToString();
        }
        return ((long)bin % (long)Math.Pow(10, s.Digits)).ToString("D" + s.Digits);
    }

    /// <summary>Base32 (RFC 4648) to bytes; spaces, dashes and padding ignored, any case. Null when it isn't Base32.</summary>
    public static byte[]? FromBase32(string? text)
    {
        const string A = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>();
        int buffer = 0, bits = 0, n = 0;
        foreach (var raw in text ?? "")
        {
            if (raw is ' ' or '-' or '=' or '\t') continue;
            int v = A.IndexOf(char.ToUpperInvariant(raw));
            if (v < 0) return null;
            buffer = (buffer << 5 | v) & 0xFFFF; bits += 5; n++;
            if (bits >= 8) { bytes.Add((byte)(buffer >> (bits - 8))); bits -= 8; }
        }
        return n == 0 ? null : bytes.ToArray();
    }

    public static string Base32(byte[] data)
    {
        const string A = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var sb = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = buffer << 8 | b; bits += 8;
            while (bits >= 5) { sb.Append(A[(buffer >> (bits - 5)) & 31]); bits -= 5; }
        }
        if (bits > 0) sb.Append(A[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    public static string Uri(byte[] secret, string account) =>
        $"otpauth://totp/{System.Uri.EscapeDataString("DarkCipher ServerManagement:" + account)}?secret={Base32(secret)}&issuer={System.Uri.EscapeDataString("DarkCipher Systems")}&algorithm=SHA1&digits=6&period=30";

    /// <summary>Ten one-time recovery codes like 7KQ2-M9XD-4TPR.</summary>
    public static List<string> RecoveryCodes()
    {
        const string A = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
        return Enumerable.Range(0, 10).Select(_ =>
        {
            var b = RandomNumberGenerator.GetBytes(12);
            var s = new string(b.Select(x => A[x % A.Length]).ToArray());
            return $"{s[..4]}-{s[4..8]}-{s[8..12]}";
        }).ToList();
    }
}
