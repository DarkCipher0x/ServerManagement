using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DcxManager.Cloud;

/// <summary>
/// Signed JSON Web Tokens for the clouds that sign in with a key instead of a token (0.2.0): an Azure app registration's
/// client assertion (PS256, certificate thumbprint in the header) and a Google service account's grant (RS256, key id in
/// the header). Only the signature is made here; the private key never leaves the process and is never written anywhere.
/// </summary>
public static class CloudJwt
{
    public static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>header.claims.signature, signed with <paramref name="key"/> (PS256 when <paramref name="pss"/>, else RS256).</summary>
    public static string Sign(IDictionary<string, object> header, IDictionary<string, object> claims, RSA key, bool pss)
    {
        var h = Base64Url(JsonSerializer.SerializeToUtf8Bytes(header));
        var c = Base64Url(JsonSerializer.SerializeToUtf8Bytes(claims));
        var signingInput = Encoding.ASCII.GetBytes(h + "." + c);
        var sig = key.SignData(signingInput, HashAlgorithmName.SHA256, pss ? RSASignaturePadding.Pss : RSASignaturePadding.Pkcs1);
        return h + "." + c + "." + Base64Url(sig);
    }

    public static long Now(DateTimeOffset t) => t.ToUnixTimeSeconds();
}
