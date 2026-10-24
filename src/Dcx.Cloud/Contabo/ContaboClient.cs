using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DcxManager.Services;

namespace DcxManager.Contabo;

/// <summary>
/// Contabo API v1 (https://api.contabo.com, official spec api-v1.yaml) for one account (ADR-020).
/// <list type="bullet">
/// <item>Sign-in: OAuth2 password grant at auth.contabo.com with client id, client secret, API user and API password.
/// The access token is kept in memory only, per account, renewed a minute before it expires and once on a 401.</item>
/// <item>Every request: a fresh UUIDv4 x-request-id (shown in errors as the support reference) and an x-trace-id that groups
/// the requests of one operation (all pages of a list).</item>
/// <item>Lists: page / size with <c>_pagination.totalPages</c>; every response's payload is the <c>data</c> array.</item>
/// <item>Retries, timeouts, sanitised errors and the audit trail: <see cref="CloudHttp"/>. Credentials and tokens never
/// reach a message, a log line or the audit log.</item>
/// </list>
/// </summary>
public sealed class ContaboClient : ICloudApi
{
    public const string DefaultApiBase = "https://api.contabo.com/v1";
    public const string DefaultTokenUrl = "https://auth.contabo.com/auth/realms/contabo/protocol/openid-connect/token";
    public const int PageSize = 100;
    public const int MaxPages = 100;

    // Test seams: the self-test points the client at an in-process fake Contabo (no network, no real credentials).
    internal static string ApiBase = DefaultApiBase;
    internal static string TokenUrl = DefaultTokenUrl;
    internal static TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);
    internal static TimeSpan SignInTimeout = TimeSpan.FromSeconds(30);
    private static HttpClient _http = NewDefaultClient();
    private static HttpClient NewDefaultClient() => new(Offline.Handler()) { Timeout = Timeout.InfiniteTimeSpan };   // per-attempt timeouts in CloudHttp
    internal static void UseHandler(HttpMessageHandler? handler)
    {
        _http = handler == null ? NewDefaultClient() : new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        ApiBase = DefaultApiBase; TokenUrl = DefaultTokenUrl;
        lock (Tokens) Tokens.Clear();
    }

    private sealed record Token(string Value, DateTime Expires, string Fingerprint);
    private static readonly Dictionary<string, Token> Tokens = new();
    private static readonly SemaphoreSlim SignInGate = new(1, 1);

    private readonly ContaboAccount _account;
    public CloudProvider Provider => ContaboProvider.Instance;
    public string AccountId => _account.Id;
    public string AccountName => _account.Name;

    public ContaboClient(ContaboAccount account) => _account = account;

    /// <summary>Forgets an account's token (credentials replaced or account removed).</summary>
    public static void Forget(string accountId) { lock (Tokens) Tokens.Remove(accountId); }

    /// <summary>A hash of the credentials, so a token is never used after they were replaced.</summary>
    private string Fingerprint => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{_account.ClientId}\n{_account.ClientSecret}\n{_account.ApiUser}\n{_account.ApiPassword}")))[..16];

    private async Task<string> TokenAsync(bool renew, CancellationToken ct)
    {
        var fp = Fingerprint;
        lock (Tokens)
            if (!renew && Tokens.TryGetValue(_account.Id, out var t) && t.Fingerprint == fp && t.Expires > DateTime.UtcNow.AddSeconds(60)) return t.Value;
        await SignInGate.WaitAsync(ct);
        try
        {
            lock (Tokens)
                if (!renew && Tokens.TryGetValue(_account.Id, out var t2) && t2.Fingerprint == fp && t2.Expires > DateTime.UtcNow.AddSeconds(60)) return t2.Value;
            if (_account.ClientId.Length == 0 || _account.ClientSecret.Length == 0 || _account.ApiUser.Length == 0 || _account.ApiPassword.Length == 0)
                throw new CloudApiException("Contabo", 401, "This Contabo account is missing part of its credentials (client id, client secret, API user, API password).");
            CloudResponse r;
            try
            {
                r = await CloudHttp.SendAsync(_http, () =>
                {
                    var req = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
                    {
                        Content = new FormUrlEncodedContent(new Dictionary<string, string>
                        {
                            ["grant_type"] = "password", ["client_id"] = _account.ClientId, ["client_secret"] = _account.ClientSecret,
                            ["username"] = _account.ApiUser, ["password"] = _account.ApiPassword,
                        }),
                    };
                    req.Headers.UserAgent.ParseAdd($"DarkCipherServerManagement/{AppInfo.Version}");
                    return req;
                }, HttpMethod.Post, idempotent: true, SignInTimeout, "Contabo", ct);
            }
            catch (CloudApiException ex) { CloudHttp.Record("Contabo", _account.Name, HttpMethod.Get, "/sign-in", null, "app", ex.Message); throw; }
            if (!r.Ok)
            {
                var msg = SignInError(r);
                CloudHttp.Record("Contabo", _account.Name, HttpMethod.Get, "/sign-in", r, "app", msg);
                throw new CloudApiException("Contabo", r.Status is 400 or 401 ? 401 : r.Status, msg);
            }
            string token; int seconds;
            try
            {
                using var d = JsonDocument.Parse(r.Text);
                token = d.RootElement.GetProperty("access_token").GetString() ?? throw new FormatException();
                seconds = d.RootElement.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var s) ? s : 300;
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or FormatException or InvalidOperationException)
            {
                throw new CloudApiException("Contabo", r.Status, "Contabo's sign-in service sent an answer without an access token.");
            }
            lock (Tokens) Tokens[_account.Id] = new Token(token, DateTime.UtcNow.AddSeconds(Math.Clamp(seconds, 30, 86400)), fp);
            return token;
        }
        finally { SignInGate.Release(); }
    }

    /// <summary>The sign-in service's error code in plain words (its own text is not echoed: it can quote what was sent).</summary>
    private static string SignInError(CloudResponse r)
    {
        string code = "";
        try { using var d = JsonDocument.Parse(r.Text); code = d.RootElement.TryGetProperty("error", out var e) ? e.GetString() ?? "" : ""; } catch { }
        return code switch
        {
            "invalid_client" or "unauthorized_client" => "Contabo sign-in failed: the client id or client secret was refused.",
            "invalid_grant" => "Contabo sign-in failed: the API user or API password was refused (the API password is set separately in the Customer Control Panel → API).",
            _ when r.Status == 429 => "Contabo's sign-in service is limiting requests. Wait a little and try again.",
            _ when r.Status >= 500 => $"Contabo's sign-in service had a problem (HTTP {r.Status}). Try again later.",
            _ => $"Contabo sign-in failed (HTTP {r.Status}{(code.Length > 0 ? ", " + code : "")}).",
        };
    }

    public Task<JsonElement> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct = default, string actor = "you", bool idempotent = true) =>
        SendAsync(method, path, body, ct, actor, idempotent, trace: null);

    /// <summary>trace = the x-trace-id grouping requests of one operation (every page of a list); null = a new one.</summary>
    private async Task<JsonElement> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct, string actor, bool idempotent, string? trace)
    {
        if (!path.StartsWith('/')) path = "/" + path;
        string? json = body switch
        {
            null => null,
            string s => s,
            JsonElement e => e.GetRawText(),
            _ => JsonSerializer.Serialize(body),
        };
        trace ??= Guid.NewGuid().ToString("N");
        CloudResponse r;
        for (int auth = 0; ; auth++)
        {
            string token;
            try { token = await TokenAsync(renew: auth > 0, ct); }
            catch (CloudApiException) { throw; }
            try
            {
                r = await CloudHttp.SendAsync(_http, () =>
                {
                    var req = new HttpRequestMessage(method, ApiBase + path);
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    req.Headers.Add("x-request-id", Guid.NewGuid().ToString("D"));
                    req.Headers.Add("x-trace-id", trace);
                    req.Headers.UserAgent.ParseAdd($"DarkCipherServerManagement/{AppInfo.Version}");
                    if (json != null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                    return req;
                }, method, idempotent, RequestTimeout, "Contabo", ct);
            }
            catch (CloudApiException ex) { CloudHttp.Record("Contabo", _account.Name, method, path, null, actor, ex.Message, ex.RequestId); throw; }
            // An expired or revoked token: sign in again once (the request itself was refused, so repeating it is safe).
            if (r.Status == 401 && auth == 0) continue;
            break;
        }

        if (!r.Ok)
        {
            var msg = ErrorText(r);
            CloudHttp.Record("Contabo", _account.Name, method, path, r, actor, msg);
            throw new CloudApiException("Contabo", r.Status, msg, r.RequestId);
        }
        CloudHttp.Record("Contabo", _account.Name, method, path, r, actor);
        if (string.IsNullOrWhiteSpace(r.Text)) return default;
        try
        {
            using var doc = JsonDocument.Parse(r.Text);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            CloudHttp.Record("Contabo", _account.Name, HttpMethod.Get, path, r, "app", "malformed JSON in the answer");
            throw new CloudApiException("Contabo", r.Status, "Contabo sent a response that isn't valid JSON.", r.RequestId);
        }
    }

    /// <summary>The API's error in a sentence: Contabo answers with a JSON message (or an errors list), never echoed whole.</summary>
    internal static string ErrorText(CloudResponse r)
    {
        string? detail = null;
        try
        {
            using var d = JsonDocument.Parse(r.Text);
            var e = d.RootElement;
            if (e.ValueKind == JsonValueKind.Object)
            {
                if (e.TryGetProperty("message", out var m)) detail = m.ValueKind == JsonValueKind.String ? m.GetString() : m.ValueKind == JsonValueKind.Array ? string.Join("; ", m.EnumerateArray().Select(x => x.ToString())) : null;
                else if (e.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array)
                    detail = string.Join("; ", errs.EnumerateArray().Take(5).Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.TryGetProperty("message", out var xm) ? xm.ToString() : x.GetRawText()));
                else if (e.TryGetProperty("error", out var er)) detail = er.ToString();
            }
        }
        catch (JsonException) { }
        detail = detail is { Length: > 0 } ? CloudHttp.FirstLine(detail) : null;
        var what = r.Status switch
        {
            400 => "Contabo rejected the request as invalid",
            401 => "Contabo refused the sign-in (the access token was not accepted)",
            402 => "Contabo needs a payment before it can do this",
            403 => "This Contabo API user isn't allowed to do this",
            404 => "Contabo doesn't know this item (it may have been removed)",
            409 => "Contabo reports a conflict with the current state",
            415 => "Contabo can't use this file type",
            429 => "Contabo is limiting requests — wait a little and try again",
            >= 500 => $"Contabo's API had a problem (HTTP {r.Status})",
            _ => $"Contabo answered HTTP {r.Status}",
        };
        return detail != null ? $"{what}: {detail}" : what + ".";
    }

    /// <summary>Every page of a list (page/size), with one trace id. A failing page fails the whole list: a partial list
    /// would make missing items look deleted.</summary>
    public async Task<List<JsonElement>> ListAsync(string path, string? key, CancellationToken ct = default)
    {
        var all = new List<JsonElement>();
        key ??= "data";
        var trace = Guid.NewGuid().ToString("N");
        for (int page = 1; page <= MaxPages; page++)
        {
            var sep = path.Contains('?') ? '&' : '?';
            var root = await SendAsync(HttpMethod.Get, $"{path}{sep}page={page}&size={PageSize}", null, ct, "you", true, trace);
            if (root.ValueKind != JsonValueKind.Object) throw new CloudApiException("Contabo", 200, "Contabo sent a list in an unexpected format.");
            if (root.TryGetProperty(key, out var arr) && arr.ValueKind == JsonValueKind.Array) all.AddRange(arr.EnumerateArray());
            else throw new CloudApiException("Contabo", 200, $"Contabo's answer has no “{key}” list.");
            int pages = root.TryGetProperty("_pagination", out var p) && p.TryGetProperty("totalPages", out var tp) && tp.TryGetDouble(out var tpd) ? (int)tpd : 1;
            if (page >= pages) break;
        }
        return all;
    }

    /// <summary>Contabo returns one item as data[0].</summary>
    public JsonElement UnwrapItem(JsonElement response) =>
        response.ValueKind == JsonValueKind.Object && response.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array && d.GetArrayLength() > 0 ? d[0] : response;

    /// <summary>Test connection: sign in and read one instance. Reports only the instance count.</summary>
    public async Task<int> TestAsync(CancellationToken ct = default)
    {
        var r = await SendAsync(HttpMethod.Get, "/compute/instances?page=1&size=1", null, ct);
        return r.TryGetProperty("_pagination", out var p) && p.TryGetProperty("totalElements", out var t) && t.TryGetDouble(out var n) ? (int)n : 0;
    }
}
