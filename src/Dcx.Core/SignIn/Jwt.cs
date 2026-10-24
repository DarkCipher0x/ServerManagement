using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DcxManager.SignIn;

/// <summary>
/// Just enough JWT (RFC 7519) for an OpenID Connect id_token: a compact RS256 token verified against a JSON Web Key Set
/// (RFC 7517, "n" / "e" of the key whose "kid" the header names), then the claims checked by the caller. No other
/// algorithm is accepted ("none", HS*, ES* are refused); the header's own "alg" never picks the verification.
/// </summary>
public static class Jwt
{
    public sealed record Parsed(JsonElement Header, JsonElement Payload, string Kid);

    public static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static byte[] FromB64Url(string s) => Convert.FromBase64String(s.Replace('-', '+').Replace('_', '/') + new string('=', (4 - s.Length % 4) % 4));

    /// <summary>Verifies the signature with the matching JWK and returns the claims; throws on anything wrong.</summary>
    public static JsonElement Verify(string token, JsonElement jwks)
    {
        if (token.Length > 16 * 1024) throw new CryptographicException("token too long");
        var parts = token.Split('.');
        if (parts.Length != 3) throw new CryptographicException("not a compact JWS");
        using var header = JsonDocument.Parse(FromB64Url(parts[0]));
        if (header.RootElement.TryGetProperty("alg", out var alg) is false || alg.GetString() != "RS256") throw new CryptographicException("only RS256 id_tokens are accepted");
        var kid = header.RootElement.TryGetProperty("kid", out var k) ? k.GetString() ?? "" : "";
        if (!jwks.TryGetProperty("keys", out var keys)) throw new CryptographicException("no keys in the JWKS");
        JsonElement? key = null;
        foreach (var jk in keys.EnumerateArray())
            if (jk.TryGetProperty("kty", out var kty) && kty.GetString() == "RSA" && (kid.Length == 0 || jk.TryGetProperty("kid", out var jkid) && jkid.GetString() == kid)) { key = jk; break; }
        if (key == null) throw new CryptographicException("the token's signing key isn't in the tenant's key set");
        using var rsa = RSA.Create(new RSAParameters { Modulus = FromB64Url(key.Value.GetProperty("n").GetString()!), Exponent = FromB64Url(key.Value.GetProperty("e").GetString()!) });
        if (rsa.KeySize < 2048) throw new CryptographicException("signing key under 2048 bits");
        var data = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
        if (!rsa.VerifyData(data, FromB64Url(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) throw new CryptographicException("the token's signature doesn't verify");
        return JsonDocument.Parse(FromB64Url(parts[1])).RootElement.Clone();
    }

    /// <summary>The standard OIDC checks: issuer, audience, expiry (with a little skew), not-before, nonce.</summary>
    public static string? CheckClaims(JsonElement c, string issuer, string audience, string nonce, DateTimeOffset now)
    {
        if (!c.TryGetProperty("iss", out var iss) || iss.GetString() != issuer) return "issuer mismatch";
        if (!c.TryGetProperty("aud", out var aud) || !(aud.ValueKind == JsonValueKind.String ? aud.GetString() == audience : aud.EnumerateArray().Any(a => a.GetString() == audience))) return "audience mismatch";
        if (!c.TryGetProperty("exp", out var exp) || DateTimeOffset.FromUnixTimeSeconds(exp.GetInt64()) < now.AddMinutes(-2)) return "the token has expired";
        if (c.TryGetProperty("nbf", out var nbf) && DateTimeOffset.FromUnixTimeSeconds(nbf.GetInt64()) > now.AddMinutes(2)) return "the token isn't valid yet";
        if (nonce.Length > 0 && (!c.TryGetProperty("nonce", out var n) || n.GetString() != nonce)) return "nonce mismatch (replayed or forged response)";
        return null;
    }

    /// <summary>Makes a signed token (the self-test's fake identity provider).</summary>
    public static string Sign(RSA key, string kid, object claims)
    {
        var h = B64Url(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { alg = "RS256", typ = "JWT", kid })));
        var p = B64Url(JsonSerializer.SerializeToUtf8Bytes(claims));
        var sig = key.SignData(Encoding.ASCII.GetBytes(h + "." + p), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return h + "." + p + "." + B64Url(sig);
    }

    /// <summary>The public half of a key as a JWK (the self-test's fake JWKS).</summary>
    public static object Jwk(RSA key, string kid)
    {
        var p = key.ExportParameters(false);
        return new { kty = "RSA", use = "sig", kid, n = B64Url(p.Modulus!), e = B64Url(p.Exponent!) };
    }
}
