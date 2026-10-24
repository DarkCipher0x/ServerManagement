using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DcxManager.Services;

namespace DcxManager.Hetzner;

/// <summary>
/// Hetzner Cloud API v1 (https://api.hetzner.cloud/v1, official spec cloud.spec.json) for one project (ADR-028).
/// <list type="bullet">
/// <item>Authentication: the project's API token as a Bearer header. It never reaches a message, a log line or the audit
/// log. A token marked read-only is never sent with a change: the app refuses it locally first.</item>
/// <item>Lists: <c>page</c> / <c>per_page=50</c> until <c>meta.pagination.next_page</c> is null; payload under the
/// resource's key (servers, volumes …).</item>
/// <item>Rate limits: 3600 requests per hour per project. A 429 is waited out using RateLimit-Reset (reads and
/// idempotent changes only, <see cref="CloudHttp"/>); the latest RateLimit-Remaining is kept for display.</item>
/// <item>Long-running work: most changes answer with an <c>action</c> (and <c>next_actions</c>). The client follows each
/// action through <c>GET /actions/{id}</c> until success or error, reporting its progress, so a change is only reported
/// done when Hetzner says so. Bounded and cancellable; a timeout is reported as "still running".</item>
/// <item>Errors: <c>{"error": {"code", "message", "details"}}</c>, turned into one sanitised sentence.</item>
/// </list>
/// </summary>
public sealed class HetznerClient : ICloudApi
{
    public const string DefaultApiBase = "https://api.hetzner.cloud/v1";
    public const int PageSize = 50;
    public const int MaxPages = 200;

    // Test seams: the self-test points the client at an in-process fake Hetzner (no network, no real token).
    internal static string ApiBase = DefaultApiBase;
    internal static TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);
    internal static TimeSpan ActionPoll = TimeSpan.FromSeconds(2);
    internal static TimeSpan ActionTimeout = TimeSpan.FromMinutes(10);
    private static HttpClient _http = NewDefaultClient();
    private static HttpClient NewDefaultClient() => new(Offline.Handler()) { Timeout = Timeout.InfiniteTimeSpan };
    internal static void UseHandler(HttpMessageHandler? handler)
    {
        _http = handler == null ? NewDefaultClient() : new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        ApiBase = DefaultApiBase;
        lock (Limits) Limits.Clear();
    }

    /// <summary>The last rate-limit headers seen per project: (limit, remaining, reset time).</summary>
    private static readonly Dictionary<string, (int Limit, int Remaining, DateTimeOffset Reset)> Limits = new();
    public static (int Limit, int Remaining, DateTimeOffset Reset)? RateLimitOf(string accountId) { lock (Limits) return Limits.TryGetValue(accountId, out var l) ? l : null; }

    private readonly HetznerAccount _account;
    public CloudProvider Provider => HetznerProvider.Instance;
    public string AccountId => _account.Id;
    public string AccountName => _account.Name;

    public HetznerClient(HetznerAccount account) => _account = account;

    public async Task<JsonElement> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct = default, string actor = "you", bool idempotent = true)
    {
        if (!path.StartsWith('/')) path = "/" + path;
        bool mutating = method != HttpMethod.Get && method != HttpMethod.Head;
        if (_account.Token.Length == 0) throw new CloudApiException("Hetzner", 401, "This Hetzner project has no API token stored.");
        if (mutating && _account.ReadOnly)
        {
            CloudHttp.Record("Hetzner", _account.Name, method, path, null, actor, "refused locally: the project's token is read-only");
            throw new CloudApiException("Hetzner", 403, $"Not sent: the API token of “{_account.Name}” is set up as read-only in this app. Replace it with a Read & Write token (Settings → Cloud providers → Hetzner) to make changes.");
        }
        string? json = body switch { null => null, string s => s, JsonElement e => e.GetRawText(), _ => JsonSerializer.Serialize(body) };
        var root = await RawAsync(method, path, json, ct, actor, idempotent);
        if (mutating && root.ValueKind == JsonValueKind.Object) await FollowActionsAsync(root, ct);
        return root;
    }

    private async Task<JsonElement> RawAsync(HttpMethod method, string path, string? json, CancellationToken ct, string actor, bool idempotent)
    {
        CloudResponse r;
        try
        {
            r = await CloudHttp.SendAsync(_http, () =>
            {
                var req = new HttpRequestMessage(method, ApiBase + path);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _account.Token);
                req.Headers.UserAgent.ParseAdd($"DarkCipherServerManagement/{AppInfo.Version}");
                if (json != null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                return req;
            }, method, idempotent, RequestTimeout, "Hetzner", ct);
        }
        catch (CloudApiException ex) { CloudHttp.Record("Hetzner", _account.Name, method, path, null, actor, ex.Message); throw; }
        RememberLimits(r);
        if (!r.Ok)
        {
            var msg = ErrorText(r);
            CloudHttp.Record("Hetzner", _account.Name, method, path, r, actor, msg);
            throw new CloudApiException("Hetzner", r.Status, msg);
        }
        CloudHttp.Record("Hetzner", _account.Name, method, path, r, actor);
        if (string.IsNullOrWhiteSpace(r.Text)) return default;
        try { using var doc = JsonDocument.Parse(r.Text); return doc.RootElement.Clone(); }
        catch (JsonException) { throw new CloudApiException("Hetzner", r.Status, "Hetzner sent a response that isn't valid JSON."); }
    }

    private void RememberLimits(CloudResponse r)
    {
        if (r.Headers is not { } h) return;
        int Int(string n) => h.TryGetValues(n, out var v) && int.TryParse(v.FirstOrDefault(), out var i) ? i : -1;
        long reset = h.TryGetValues("RateLimit-Reset", out var rv) && long.TryParse(rv.FirstOrDefault(), out var u) ? u : 0;
        int limit = Int("RateLimit-Limit"), left = Int("RateLimit-Remaining");
        if (limit < 0 || left < 0) return;
        lock (Limits) Limits[_account.Id] = (limit, left, reset > 0 ? DateTimeOffset.FromUnixTimeSeconds(reset) : DateTimeOffset.UtcNow);
    }

    /// <summary>The API's error in one sentence. Codes from the spec; Hetzner's own message is kept (first line, bounded),
    /// invalid-input details name the fields.</summary>
    internal static string ErrorText(CloudResponse r)
    {
        string code = "", message = "", fields = "";
        try
        {
            using var d = JsonDocument.Parse(r.Text);
            if (d.RootElement.ValueKind == JsonValueKind.Object && d.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object)
            {
                code = e.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "";
                message = e.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? CloudHttp.FirstLine(m.GetString() ?? "") : "";
                if (e.TryGetProperty("details", out var det) && det.ValueKind == JsonValueKind.Object && det.TryGetProperty("fields", out var fs) && fs.ValueKind == JsonValueKind.Array)
                    fields = string.Join("; ", fs.EnumerateArray().Take(5).Select(f => $"{J.Str(f, "name")}: {J.Str(f, "messages")}"));
            }
        }
        catch (JsonException) { }
        var what = code switch
        {
            "unauthorized" => "Hetzner refused the API token (it may have been deleted or mistyped)",
            "token_readonly" => "This API token is read-only — create a Read & Write token in the Hetzner Console to make changes",
            "forbidden" => "This API token isn't allowed to do this",
            "not_found" => "Hetzner doesn't know this item (it may have been removed)",
            "protected" => "The item is protected — turn its protection off first (Protection…)",
            "locked" => "Another action is still running on this item — wait for it to finish",
            "rate_limit_exceeded" => "Hetzner is limiting requests (3600 per hour per project) — wait a little",
            "resource_limit_exceeded" => "The project's limit for this resource type is reached (Hetzner can raise it on request)",
            "resource_unavailable" => "That resource isn't available right now (e.g. the server type in this location)",
            "uniqueness_error" => "Something with that name or value already exists",
            "invalid_input" => "Hetzner rejected the input",
            "json_error" => "Hetzner couldn't read the request",
            "conflict" => "The item changed in the meantime — refresh and try again",
            "maintenance" => "Hetzner is in maintenance for this operation",
            "unsupported_error" => "This operation isn't supported for this item",
            "service_error" or "unavailable" or "server_error" => $"Hetzner's API had a problem (HTTP {r.Status})",
            _ when r.Status == 401 => "Hetzner refused the API token",
            _ when r.Status == 403 => "This API token isn't allowed to do this",
            _ when r.Status == 404 => "Hetzner doesn't know this item",
            _ when r.Status == 429 => "Hetzner is limiting requests — wait a little",
            _ when r.Status >= 500 => $"Hetzner's API had a problem (HTTP {r.Status})",
            _ => $"Hetzner answered HTTP {r.Status}",
        };
        var detail = fields.Length > 0 ? fields : message;
        return detail.Length > 0 && !what.Contains(detail, StringComparison.OrdinalIgnoreCase) ? $"{what}: {detail}" : what + ".";
    }

    /// <summary>Waits for the action(s) a change started: "action", "actions" and "next_actions". Throws when one fails.</summary>
    private async Task FollowActionsAsync(JsonElement root, CancellationToken ct)
    {
        var ids = new List<(long Id, string Command)>();
        void Add(JsonElement a)
        {
            if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty("id", out var id) && id.TryGetInt64(out var n) && J.Str(a, "status") != "success")
                ids.Add((n, J.Str(a, "command")));
        }
        if (root.TryGetProperty("action", out var one)) Add(one);
        foreach (var k in new[] { "actions", "next_actions" })
            if (root.TryGetProperty(k, out var many) && many.ValueKind == JsonValueKind.Array) foreach (var a in many.EnumerateArray()) Add(a);
        foreach (var (id, command) in ids)
        {
            var until = DateTime.UtcNow + ActionTimeout;
            while (true)
            {
                var a = await RawAsync(HttpMethod.Get, $"/actions/{id}", null, ct, "app", true);
                var act = J.Get(a, "action");
                var status = J.Str(act, "status");
                if (status == "success") { CloudProgress.Report("hetzner", $"✓ {command} finished"); break; }
                if (status == "error")
                    throw new CloudApiException("Hetzner", 409, $"Hetzner reports that “{command}” failed: {CloudHttp.FirstLine(J.Str(act, "error.message") is { Length: > 0 } em ? em : J.Str(act, "error.code"))}");
                CloudProgress.Report("hetzner", $"{command}: {J.Str(act, "progress")} %");
                if (DateTime.UtcNow > until)
                    throw new CloudApiException("Hetzner", 0, $"“{command}” was accepted and is still running after {ActionTimeout.TotalMinutes:0} minutes — refresh later to see the result.", uncertain: true);
                await CloudHttp.Sleep(ActionPoll, ct);
            }
        }
    }

    /// <summary>Every page of a list (page / per_page, until next_page is null). A failing page fails the whole list:
    /// a partial list would make missing items look deleted.</summary>
    public async Task<List<JsonElement>> ListAsync(string path, string? key, CancellationToken ct = default)
    {
        var all = new List<JsonElement>();
        if (!path.StartsWith('/')) path = "/" + path;
        key ??= CloudHttp.StripQuery(path).Trim('/').Split('/')[^1];
        for (int page = 1; page <= MaxPages; page++)
        {
            var sep = path.Contains('?') ? '&' : '?';
            var root = await RawAsync(HttpMethod.Get, $"{path}{sep}page={page}&per_page={PageSize}", null, ct, "you", true);
            if (root.ValueKind != JsonValueKind.Object) throw new CloudApiException("Hetzner", 200, "Hetzner sent a list in an unexpected format.");
            if (root.TryGetProperty(key, out var arr) && arr.ValueKind == JsonValueKind.Array) all.AddRange(arr.EnumerateArray());
            else throw new CloudApiException("Hetzner", 200, $"Hetzner's answer has no “{key}” list.");
            var next = J.Get(root, "meta.pagination.next_page");
            if (next.ValueKind != JsonValueKind.Number) break;   // null / missing = last page (or a list without pages)
        }
        return all;
    }

    /// <summary>Hetzner returns one item as {"server": {...}} (plus "action" on changes): the single object property.</summary>
    public JsonElement UnwrapItem(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object) return response;
        var objs = response.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Object && p.Name is not ("meta" or "action")).ToList();
        return objs.Count == 1 ? objs[0].Value : response;
    }

    /// <summary>Test connection: one server read. Reports the server count only. A token's write permission can't be
    /// read through the API; it shows on the first change.</summary>
    public async Task<int> TestAsync(CancellationToken ct = default)
    {
        var r = await RawAsync(HttpMethod.Get, "/servers?page=1&per_page=1", null, ct, "you", true);
        return J.Get(r, "meta.pagination.total_entries") is { ValueKind: JsonValueKind.Number } t && t.TryGetInt32(out var n) ? n
            : r.TryGetProperty("servers", out var s) && s.ValueKind == JsonValueKind.Array ? s.GetArrayLength() : 0;
    }
}
