using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DcxManager.Services;

namespace DcxManager.Vultr;

/// <summary>
/// Vultr API v2 (https://api.vultr.com/v2) with a Personal Access Token (ADR-020: one <see cref="ICloudApi"/> of the
/// Vultr provider). Cursor pagination; retries, timeouts, sanitised errors and the audit trail come from
/// <see cref="CloudHttp"/>. Vultr offers no OAuth for third-party apps — the API key is the sign-in.
/// </summary>
public sealed class VultrClient : ICloudApi
{
    public const string BaseUrl = "https://api.vultr.com/v2";
    private static readonly HttpClient Http = new(Offline.Handler()) { Timeout = Timeout.InfiniteTimeSpan };   // per-attempt timeouts in CloudHttp
    internal static TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

    private readonly VultrAccount _account;
    public CloudProvider Provider => VultrProvider.Instance;
    public string AccountName => _account.Name;
    public string AccountId => _account.Id;

    public VultrClient(VultrAccount account) => _account = account;

    public static VultrClient? Active => VultrProvider.Instance.Active as VultrClient;

    public static VultrClient Require() =>
        Active ?? throw new InvalidOperationException("No Vultr account is set up (or all are disabled). Add your API key in Settings → Vultr accounts.");

    public Task<JsonElement> GetAsync(string path, CancellationToken ct = default) => SendAsync(HttpMethod.Get, path, null, ct);
    public Task<JsonElement> PostAsync(string path, object? body = null, CancellationToken ct = default, string actor = "you") => SendAsync(HttpMethod.Post, path, body, ct, actor);
    public Task<JsonElement> PatchAsync(string path, object? body, CancellationToken ct = default, string actor = "you") => SendAsync(HttpMethod.Patch, path, body, ct, actor);
    public Task<JsonElement> PutAsync(string path, object? body, CancellationToken ct = default, string actor = "you") => SendAsync(HttpMethod.Put, path, body, ct, actor);
    public Task<JsonElement> DeleteAsync(string path, CancellationToken ct = default, string actor = "you") => SendAsync(HttpMethod.Delete, path, null, ct, actor);

    /// <summary>Any call. body may be null, a JSON string, a JsonElement or any serialisable object.</summary>
    public async Task<JsonElement> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct = default, string actor = "you", bool idempotent = true)
    {
        if (!path.StartsWith('/')) path = "/" + path;
        string? json = body switch
        {
            null => null,
            string s => s,
            JsonElement e => e.GetRawText(),
            _ => JsonSerializer.Serialize(body),
        };
        CloudResponse r;
        try
        {
            r = await CloudHttp.SendAsync(Http, () =>
            {
                var req = new HttpRequestMessage(method, BaseUrl + path);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _account.ApiKey);
                req.Headers.UserAgent.ParseAdd($"DarkCipherServerManagement/{AppInfo.Version}");
                if (json != null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                return req;
            }, method, idempotent, RequestTimeout, "Vultr", ct);
        }
        catch (CloudApiException ex) { CloudHttp.Record("Vultr", _account.Name, method, path, null, actor, ex.Message); throw; }

        if (!r.Ok)
        {
            var msg = ErrorText(r.Text) ?? $"HTTP {r.Status}";
            if (r.Status == 401) msg += "\n\nCheck the API key, and that this PC's public IP is allowed under Vultr → Account → API → Access Control.";
            CloudHttp.Record("Vultr", _account.Name, method, path, r, actor, CloudHttp.FirstLine(msg));
            throw new CloudApiException("Vultr", r.Status, msg);
        }
        CloudHttp.Record("Vultr", _account.Name, method, path, r, actor);
        if (string.IsNullOrWhiteSpace(r.Text)) return default;
        try
        {
            using var doc = JsonDocument.Parse(r.Text);
            return doc.RootElement.Clone();
        }
        catch (JsonException) { throw new CloudApiException("Vultr", r.Status, "Vultr sent a response that isn't valid JSON."); }
    }

    /// <summary>Reads every page of a list endpoint. key = the array property ("instances"); if it's missing, the first array is used.</summary>
    public async Task<List<JsonElement>> ListAsync(string path, string? key, CancellationToken ct = default)
    {
        var all = new List<JsonElement>();
        string? cursor = null;
        for (int page = 0; page < 200; page++)
        {
            var sep = path.Contains('?') ? '&' : '?';
            var url = $"{path}{sep}per_page=500" + (cursor != null ? "&cursor=" + Uri.EscapeDataString(cursor) : "");
            var root = await GetAsync(url, ct);
            if (root.ValueKind != JsonValueKind.Object) break;

            JsonElement arr = default;
            if (key != null && root.TryGetProperty(key, out var k)) arr = k;
            else foreach (var p in root.EnumerateObject()) if (p.Value.ValueKind == JsonValueKind.Array) { arr = p.Value; break; }

            if (arr.ValueKind == JsonValueKind.Array) all.AddRange(arr.EnumerateArray());
            else if (arr.ValueKind == JsonValueKind.Object) { all.Add(arr); break; }   // single-object responses (e.g. pending charges)

            cursor = root.TryGetProperty("meta", out var meta) && meta.TryGetProperty("links", out var links)
                     && links.TryGetProperty("next", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
            if (string.IsNullOrEmpty(cursor)) break;
        }
        return all;
    }

    /// <summary>Vultr wraps one item in a single property ({"instance": {…}}).</summary>
    public JsonElement UnwrapItem(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object) return response;
        var props = response.EnumerateObject().ToList();
        return props.Count == 1 && props[0].Value.ValueKind == JsonValueKind.Object ? props[0].Value : response;
    }

    public async Task<JsonElement> AccountAsync(CancellationToken ct = default)
    {
        var r = await GetAsync("/account", ct);
        return r.TryGetProperty("account", out var a) ? a : r;
    }

    private static string? ErrorText(string text)
    {
        try
        {
            using var d = JsonDocument.Parse(text);
            if (d.RootElement.TryGetProperty("error", out var e)) return CloudHttp.FirstLine(e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.GetRawText());
        }
        catch { }
        return string.IsNullOrWhiteSpace(text) ? null : CloudHttp.FirstLine(text);
    }
}
