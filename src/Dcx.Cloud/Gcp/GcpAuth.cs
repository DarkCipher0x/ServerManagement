using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using DcxManager.Cloud;
using DcxManager.Services;

namespace DcxManager.Gcp;

/// <summary>
/// Sign-in for a Google Cloud project (0.2.0): a service account's key file and the OAuth 2.0 JWT-bearer grant
/// (RFC 7523) — a JWT signed RS256 with the key file's private key (its key id in the header) exchanged at Google's
/// token endpoint for an access token. Read-only projects ask for the read-only scope, so Google itself refuses changes
/// as well as the app. The key is parsed from the encrypted settings for each sign-in and never written anywhere; the
/// access token is kept in memory only, until a little before it expires. Nothing secret reaches a message or log line.
/// </summary>
public static class GcpAuth
{
    public const string TokenUri = "https://oauth2.googleapis.com/token";
    public const string ScopeAll = "https://www.googleapis.com/auth/cloud-platform";
    public const string ScopeRead = "https://www.googleapis.com/auth/cloud-platform.read-only";

    private static readonly Dictionary<string, (string Token, string Scope, DateTimeOffset Expires)> Tokens = new();
    internal static void Forget(string accountId) { lock (Tokens) Tokens.Remove(accountId); }
    internal static void ForgetAll() { lock (Tokens) Tokens.Clear(); }
    /// <summary>Test seam: the clock.</summary>
    internal static Func<DateTimeOffset> Clock = () => DateTimeOffset.UtcNow;

    /// <summary>What the app needs from a key file. The private key stays a PEM string only as long as a sign-in.</summary>
    public sealed record KeyFile(string ClientEmail, string PrivateKeyId, string PrivateKeyPem, string ProjectId);

    /// <summary>Reads a service-account key file (as downloaded from the Google Cloud console). Refuses anything else,
    /// including key files that name a token endpoint other than Google's (a key file can't send the app elsewhere).</summary>
    public static KeyFile ParseKeyFile(string json)
    {
        try
        {
            using var d = JsonDocument.Parse(json);
            var r = d.RootElement;
            if (r.ValueKind != JsonValueKind.Object) throw new CloudApiException("Google Cloud", 401, "That isn't a service-account key file.");
            string Str(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            if (Str("type") != "service_account") throw new CloudApiException("Google Cloud", 401, "That isn't a service-account key file (its “type” must be service_account).");
            if (Str("token_uri") is { Length: > 0 } t && t != TokenUri) throw new CloudApiException("Google Cloud", 401, "The key file names a token endpoint other than Google's — refused.");
            var key = new KeyFile(Str("client_email"), Str("private_key_id"), Str("private_key"), Str("project_id"));
            if (key.ClientEmail.Length == 0 || key.PrivateKeyPem.Length == 0) throw new CloudApiException("Google Cloud", 401, "The key file has no client_email or private_key.");
            return key;
        }
        catch (JsonException) { throw new CloudApiException("Google Cloud", 401, "The key file isn't valid JSON."); }
    }

    /// <summary>The signed grant (valid one hour, Google's maximum).</summary>
    public static string Assertion(KeyFile key, string scope, DateTimeOffset now)
    {
        using var rsa = RSA.Create();
        try { rsa.ImportFromPem(key.PrivateKeyPem); }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException) { throw new CloudApiException("Google Cloud", 401, "The key file's private key can't be read."); }
        var header = new Dictionary<string, object> { ["alg"] = "RS256", ["typ"] = "JWT", ["kid"] = key.PrivateKeyId };
        var claims = new Dictionary<string, object>
        {
            ["iss"] = key.ClientEmail, ["scope"] = scope, ["aud"] = TokenUri,
            ["iat"] = CloudJwt.Now(now), ["exp"] = CloudJwt.Now(now.AddHours(1)),
        };
        return CloudJwt.Sign(header, claims, rsa, pss: false);
    }

    public static string ScopeFor(GcpAccount a) => a.ReadOnly ? ScopeRead : ScopeAll;

    /// <summary>An access token for Google Cloud APIs (cached in memory until two minutes before it expires; a change of
    /// the read-only switch asks for a new one with the other scope).</summary>
    public static async Task<string> TokenAsync(HttpClient http, GcpAccount a, TimeSpan timeout, CancellationToken ct)
    {
        var now = Clock();
        var scope = ScopeFor(a);
        lock (Tokens) if (Tokens.TryGetValue(a.Id, out var t) && t.Scope == scope && t.Expires > now.AddMinutes(2)) return t.Token;
        if (a.ServiceAccountJson.Length == 0) throw new CloudApiException("Google Cloud", 401, "This Google Cloud project has no key file stored.");
        var assertion = Assertion(ParseKeyFile(a.ServiceAccountJson), scope, now);
        var form = new Dictionary<string, string> { ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer", ["assertion"] = assertion };
        var r = await CloudHttp.SendAsync(http, () => new HttpRequestMessage(HttpMethod.Post, TokenUri) { Content = new FormUrlEncodedContent(form) },
            HttpMethod.Post, idempotent: true, timeout, "Google Cloud", ct);
        if (!r.Ok) throw new CloudApiException("Google Cloud", r.Status, SignInError(r.Text));
        try
        {
            using var d = JsonDocument.Parse(r.Text);
            var token = d.RootElement.GetProperty("access_token").GetString() ?? throw new JsonException();
            var seconds = d.RootElement.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var s) ? s : 3599;
            lock (Tokens) Tokens[a.Id] = (token, scope, now.AddSeconds(seconds));
            return token;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        { throw new CloudApiException("Google Cloud", r.Status, "Google sent a sign-in answer the app can't read."); }
    }

    /// <summary>Google's token error in one sentence, explained where it is a common set-up mistake.</summary>
    internal static string SignInError(string body)
    {
        string error = "", description = "";
        try
        {
            using var d = JsonDocument.Parse(body);
            if (d.RootElement.ValueKind == JsonValueKind.Object)
            {
                error = d.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "";
                description = d.RootElement.TryGetProperty("error_description", out var ds) && ds.ValueKind == JsonValueKind.String ? CloudHttp.FirstLine(ds.GetString() ?? "") : "";
            }
        }
        catch (JsonException) { }
        var what = (error, description) switch
        {
            ("invalid_grant", var x) when x.Contains("Invalid JWT Signature", StringComparison.OrdinalIgnoreCase) => "Google doesn't accept this key — it was deleted or disabled in the service account (make a new key)",
            ("invalid_grant", var x) when x.Contains("Invalid JWT", StringComparison.OrdinalIgnoreCase) && x.Contains("time", StringComparison.OrdinalIgnoreCase) => "Google rejected the sign-in as outside its time window — check this PC's clock",
            ("invalid_grant", var x) when x.Contains("account not found", StringComparison.OrdinalIgnoreCase) => "Google doesn't know this service account (it may have been deleted)",
            ("invalid_scope", _) => "Google refused the requested access scope",
            ("unauthorized_client", _) => "The service account isn't allowed to sign in this way",
            ("invalid_grant", _) => "Google refused the service account's sign-in",
            _ => $"Google refused the sign-in ({(error.Length > 0 ? error : "unknown error")})",
        };
        return description.Length > 0 && !what.Contains(description, StringComparison.OrdinalIgnoreCase) ? $"{what}: {description}" : what + ".";
    }
}
