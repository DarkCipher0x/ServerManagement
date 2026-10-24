using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using DcxManager.Services;

namespace DcxManager.Azure;

/// <summary>
/// Sign-in for an Azure subscription (0.2.0): the OAuth 2.0 client-credentials grant of Microsoft Entra ID with a
/// certificate credential — a client assertion (JWT, PS256, the certificate's SHA-256 thumbprint as <c>x5t#S256</c>)
/// signed with the app registration's private key — for the Azure Resource Manager scope. The certificate is loaded
/// from the encrypted settings for each sign-in with an ephemeral key set (never written to the Windows key store); the
/// access token is kept in memory only, until a little before it expires. Nothing secret reaches a message or log line.
/// </summary>
public static class AzureAuth
{
    public const string DefaultLogin = "https://login.microsoftonline.com";
    public const string Scope = "https://management.azure.com/.default";
    /// <summary>Test seam: the fake identity provider of the self-test.</summary>
    internal static string LoginBase = DefaultLogin;

    private static readonly Dictionary<string, (string Token, DateTimeOffset Expires)> Tokens = new();
    internal static void Forget(string accountId) { lock (Tokens) Tokens.Remove(accountId); }
    internal static void ForgetAll() { lock (Tokens) Tokens.Clear(); }
    /// <summary>Test seam: the clock.</summary>
    internal static Func<DateTimeOffset> Clock = () => DateTimeOffset.UtcNow;

    /// <summary>The certificate with its private key, ephemeral (only in this process's memory).</summary>
    public static X509Certificate2 LoadCertificate(AzureAccount a)
    {
        if (a.CertificatePfx.Length == 0) throw new Cloud.CloudApiException("Azure", 401, "This Azure subscription has no certificate stored.");
        byte[] pfx;
        try { pfx = Convert.FromBase64String(a.CertificatePfx); }
        catch (FormatException) { throw new Cloud.CloudApiException("Azure", 401, "The stored certificate is damaged — add it again."); }
        try
        {
            var cert = X509CertificateLoader.LoadPkcs12(pfx, a.CertificatePassword, X509KeyStorageFlags.EphemeralKeySet);
            if (!cert.HasPrivateKey) { cert.Dispose(); throw new Cloud.CloudApiException("Azure", 401, "The certificate has no private key — export it as a .pfx with its key."); }
            return cert;
        }
        catch (CryptographicException) { throw new Cloud.CloudApiException("Azure", 401, "The certificate can't be opened (wrong password, or not a .pfx file)."); }
    }

    /// <summary>The certificate's SHA-256 thumbprint as hex (what the Azure portal shows for the app registration).</summary>
    public static string Sha256Hex(X509Certificate2 cert) => Convert.ToHexString(cert.GetCertHash(HashAlgorithmName.SHA256));

    /// <summary>The client assertion for <paramref name="tokenUrl"/> (valid ten minutes, single use).</summary>
    public static string ClientAssertion(X509Certificate2 cert, string clientId, string tokenUrl, DateTimeOffset now)
    {
        using var key = cert.GetRSAPrivateKey() ?? throw new Cloud.CloudApiException("Azure", 401, "The certificate's key isn't an RSA key (Entra ID needs RSA).");
        var header = new Dictionary<string, object> { ["alg"] = "PS256", ["typ"] = "JWT", ["x5t#S256"] = Cloud.CloudJwt.Base64Url(cert.GetCertHash(HashAlgorithmName.SHA256)) };
        var claims = new Dictionary<string, object>
        {
            ["aud"] = tokenUrl, ["iss"] = clientId, ["sub"] = clientId, ["jti"] = Guid.NewGuid().ToString(),
            ["nbf"] = Cloud.CloudJwt.Now(now), ["iat"] = Cloud.CloudJwt.Now(now), ["exp"] = Cloud.CloudJwt.Now(now.AddMinutes(10)),
        };
        return Cloud.CloudJwt.Sign(header, claims, key, pss: true);
    }

    public static string TokenUrl(string tenantId) => $"{LoginBase}/{Uri.EscapeDataString(tenantId)}/oauth2/v2.0/token";

    /// <summary>The Logs Ingestion API of Azure Monitor (log sinks, 0.4.0): a second scope of the same app registration.</summary>
    public const string MonitorScope = "https://monitor.azure.com/.default";

    /// <summary>An access token for Azure Resource Manager — or another <paramref name="scope"/> of the same app registration
    /// (cached in memory per account and scope until two minutes before it expires).</summary>
    public static async Task<string> TokenAsync(HttpClient http, AzureAccount a, TimeSpan timeout, CancellationToken ct, string scope = Scope)
    {
        var now = Clock();
        var cacheKey = scope == Scope ? a.Id : a.Id + "|" + scope;
        lock (Tokens) if (Tokens.TryGetValue(cacheKey, out var t) && t.Expires > now.AddMinutes(2)) return t.Token;
        if (a.TenantId.Length == 0 || a.ClientId.Length == 0) throw new Cloud.CloudApiException("Azure", 401, "The tenant id and the application (client) id are needed.");
        var url = TokenUrl(a.TenantId);
        string assertion;
        using (var cert = LoadCertificate(a)) assertion = ClientAssertion(cert, a.ClientId, url, now);
        var form = new Dictionary<string, string>
        {
            ["client_id"] = a.ClientId, ["scope"] = scope, ["grant_type"] = "client_credentials",
            ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer", ["client_assertion"] = assertion,
        };
        var r = await Cloud.CloudHttp.SendAsync(http, () => new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) },
            HttpMethod.Post, idempotent: true, timeout, "Azure", ct);
        if (!r.Ok) throw new Cloud.CloudApiException("Azure", r.Status, SignInError(r.Text));
        try
        {
            using var d = JsonDocument.Parse(r.Text);
            var token = d.RootElement.GetProperty("access_token").GetString() ?? throw new JsonException();
            var seconds = d.RootElement.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var s) ? s : 3599;
            lock (Tokens) Tokens[cacheKey] = (token, now.AddSeconds(seconds));
            return token;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        { throw new Cloud.CloudApiException("Azure", r.Status, "Microsoft Entra ID sent a sign-in answer the app can't read."); }
    }

    /// <summary>Entra ID's error in one sentence: its AADSTS code explained where it is a common set-up mistake.</summary>
    internal static string SignInError(string body)
    {
        string error = "", description = "";
        try
        {
            using var d = JsonDocument.Parse(body);
            if (d.RootElement.ValueKind == JsonValueKind.Object)
            {
                error = d.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "";
                description = d.RootElement.TryGetProperty("error_description", out var ds) && ds.ValueKind == JsonValueKind.String ? Cloud.CloudHttp.FirstLine(ds.GetString() ?? "") : "";
            }
        }
        catch (JsonException) { }
        var code = System.Text.RegularExpressions.Regex.Match(description, @"AADSTS\d+").Value;
        var what = code switch
        {
            "AADSTS700027" => "Entra ID doesn't recognise the certificate — upload this certificate (.cer) to the app registration's Certificates & secrets",
            "AADSTS700016" => "Entra ID doesn't know this application (client) id in this tenant",
            "AADSTS90002" or "AADSTS900023" => "Entra ID doesn't know this tenant id",
            "AADSTS7000215" or "AADSTS7000222" => "The app registration's credential is invalid or expired",
            "AADSTS700024" => "The sign-in was rejected as outside its time window — check this PC's clock",
            "AADSTS50020" or "AADSTS50034" => "The account isn't in this tenant",
            _ => error == "invalid_client" ? "Entra ID refused the app registration's credential" : $"Microsoft Entra ID refused the sign-in ({(error.Length > 0 ? error : "unknown error")})",
        };
        return code.Length > 0 ? $"{what} ({code})." : what + ".";
    }
}
