using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DcxManager.Services;

namespace DcxManager.Azure;

/// <summary>
/// Azure Resource Manager (https://management.azure.com) for one subscription (0.2.0).
/// <list type="bullet">
/// <item>Sign-in: <see cref="AzureAuth"/> (certificate, never a client secret). The bearer token never reaches a
/// message, a log line or the audit log.</item>
/// <item>Paths: below <c>/subscriptions/_SUB_</c> (the client fills in the subscription) or <c>/providers</c>; every
/// path carries its <c>api-version</c>. Links from Azure (nextLink, operation status) are only followed on
/// management.azure.com.</item>
/// <item>Read-only (the default for a new subscription): every change is refused here before anything is sent.</item>
/// <item>Lists: <c>value</c> plus <c>nextLink</c> pages (bounded). Long-running changes (201/202 with
/// Azure-AsyncOperation or Location) are followed until Azure reports Succeeded / Failed / Canceled, bounded and
/// cancellable; a timeout is reported as "still running".</item>
/// <item>Errors: <c>{"error": {"code", "message"}}</c>, turned into one sanitised sentence.</item>
/// </list>
/// </summary>
public sealed class AzureClient : ICloudApi
{
    public const string DefaultApiBase = "https://management.azure.com";
    public const int MaxPages = 100;
    /// <summary>Stands for the subscription id in catalog paths (a plain token, so templates never touch it).</summary>
    public const string Sub = "_SUB_";

    // Test seams: the self-test points the client at an in-process fake (no network, no real tenant).
    internal static string ApiBase = DefaultApiBase;
    internal static TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);
    internal static TimeSpan OperationPoll = TimeSpan.FromSeconds(5);
    internal static TimeSpan OperationTimeout = TimeSpan.FromMinutes(15);
    private static HttpClient _http = NewDefaultClient();
    private static HttpClient NewDefaultClient() => new(Offline.Handler()) { Timeout = Timeout.InfiniteTimeSpan };
    internal static void UseHandler(HttpMessageHandler? handler)
    {
        _http = handler == null ? NewDefaultClient() : new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        ApiBase = DefaultApiBase;
        AzureAuth.LoginBase = handler == null ? AzureAuth.DefaultLogin : "https://login.microsoftonline.com";
        AzureAuth.ForgetAll();
    }

    /// <summary>An access token for Azure Monitor's Logs Ingestion API (log sinks, 0.4.0) from one of the subscriptions' app
    /// registrations — the same certificate sign-in, a different scope. The log shipper calls this; the token never leaves
    /// the request header.</summary>
    public static async Task<string> MonitorTokenAsync(string accountId, CancellationToken ct)
    {
        var a = AppSettings.Current.AzureAccounts.FirstOrDefault(x => x.Id == accountId && !x.Disabled)
            ?? throw new CloudApiException("Azure", 401, "The Azure subscription this log sink signs in with isn't set up (Settings → Cloud providers → Azure subscriptions).");
        return await AzureAuth.TokenAsync(_http, a, RequestTimeout, ct, AzureAuth.MonitorScope);
    }

    /// <summary>Reads that Azure only offers as POST, allowed on a read-only subscription. Empty on purpose: the obvious
    /// one (retrieveBootDiagnosticsData) answers with SAS URLs, which are secrets the app never fetches.</summary>
    private static readonly string[] PostReads = Array.Empty<string>();

    private readonly AzureAccount _account;
    public CloudProvider Provider => AzureProvider.Instance;
    public string AccountId => _account.Id;
    public string AccountName => _account.Name;

    public AzureClient(AzureAccount account) => _account = account;

    /// <summary>The full URL for a catalog path or an Azure link, or an error when it would leave Resource Manager.</summary>
    internal string Url(string path)
    {
        if (path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(path, UriKind.Absolute, out var u) || !u.Host.Equals(new Uri(ApiBase).Host, StringComparison.OrdinalIgnoreCase))
                throw new CloudApiException("Azure", 0, "Refused a link outside Azure Resource Manager.");
            return path;
        }
        if (!path.StartsWith('/')) path = "/" + path;
        path = path.Replace(Sub, Uri.EscapeDataString(_account.SubscriptionId), StringComparison.Ordinal);
        if (!path.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase) && !path.StartsWith("/providers", StringComparison.OrdinalIgnoreCase))
            throw new CloudApiException("Azure", 0, "Refused a path outside the subscription.");
        if (!path.StartsWith($"/subscriptions/{Uri.EscapeDataString(_account.SubscriptionId)}", StringComparison.OrdinalIgnoreCase) && path.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase))
            throw new CloudApiException("Azure", 0, "Refused a path in another subscription.");
        return ApiBase + path;
    }

    public async Task<JsonElement> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct = default, string actor = "you", bool idempotent = true)
    {
        bool mutating = method != HttpMethod.Get && method != HttpMethod.Head;
        if (mutating && _account.ReadOnly && !(method == HttpMethod.Post && PostReads.Any(p => Path(path).EndsWith(p, StringComparison.OrdinalIgnoreCase))))
        {
            CloudHttp.Record("Azure", _account.Name, method, path, null, actor, "refused locally: the subscription is read-only in this app");
            throw new CloudApiException("Azure", 403, $"Not sent: “{_account.Name}” is read-only in this app. Allow changes for it in Settings → Cloud providers → Azure subscriptions first (the app registration also needs an Azure role that allows the change).");
        }
        string? json = body switch { null => null, string s => s, JsonElement e => e.GetRawText(), _ => JsonSerializer.Serialize(body) };
        var (root, r) = await RawAsync(method, path, json, ct, actor, idempotent);
        if (mutating && r.Status is 201 or 202) await FollowOperationAsync(r, method, path, ct);
        return root;
    }

    private static string Path(string p) => CloudHttp.StripQuery(p);

    private async Task<(JsonElement Root, CloudResponse Response)> RawAsync(HttpMethod method, string path, string? json, CancellationToken ct, string actor, bool idempotent)
    {
        var url = Url(path);
        CloudResponse r;
        try
        {
            var token = await AzureAuth.TokenAsync(_http, _account, RequestTimeout, ct);
            r = await CloudHttp.SendAsync(_http, () =>
            {
                var req = new HttpRequestMessage(method, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                req.Headers.UserAgent.ParseAdd($"DarkCipherServerManagement/{AppInfo.Version}");
                if (json != null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                return req;
            }, method, idempotent, RequestTimeout, "Azure", ct);
        }
        catch (CloudApiException ex) { CloudHttp.Record("Azure", _account.Name, method, path, null, actor, ex.Message); throw; }
        if (r.Status == 401) AzureAuth.Forget(_account.Id);
        if (!r.Ok)
        {
            var msg = ErrorText(r);
            CloudHttp.Record("Azure", _account.Name, method, path, r, actor, msg);
            throw new CloudApiException("Azure", r.Status, msg, RequestIdOf(r));
        }
        CloudHttp.Record("Azure", _account.Name, method, path, r, actor);
        if (string.IsNullOrWhiteSpace(r.Text)) return (default, r);
        try { using var doc = JsonDocument.Parse(r.Text); return (doc.RootElement.Clone(), r); }
        catch (JsonException) { throw new CloudApiException("Azure", r.Status, "Azure sent a response that isn't valid JSON."); }
    }

    private static string? RequestIdOf(CloudResponse r) =>
        r.RequestId ?? (r.Headers is { } h && h.TryGetValues("x-ms-correlation-request-id", out var v) ? v.FirstOrDefault() : null);

    /// <summary>The error in one sentence: Azure's code explained where it is a common cause, its message kept (first line, bounded).</summary>
    internal static string ErrorText(CloudResponse r)
    {
        string code = "", message = "";
        try
        {
            using var d = JsonDocument.Parse(r.Text);
            if (d.RootElement.ValueKind == JsonValueKind.Object && d.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object)
            {
                code = J.Str(e, "code");
                message = CloudHttp.FirstLine(J.Str(e, "message"));
            }
        }
        catch (JsonException) { }
        var what = code switch
        {
            "AuthorizationFailed" => "The app registration's Azure role doesn't allow this (grant a role such as Reader or Contributor on the subscription or resource group)",
            "InvalidAuthenticationToken" or "ExpiredAuthenticationToken" => "Azure refused the sign-in token — test the subscription again",
            "SubscriptionNotFound" => "Azure doesn't know this subscription id (or the app registration has no role on it)",
            "ResourceNotFound" or "ResourceGroupNotFound" or "NotFound" => "Azure doesn't know this item (it may have been removed)",
            "Conflict" or "OperationNotAllowed" => "Azure doesn't allow this in the item's current state",
            "ScopeLocked" => "The item is locked (a management lock) — remove the lock first",
            "QuotaExceeded" => "The subscription's quota for this is reached",
            "TooManyRequests" or "SubscriptionRequestsThrottled" => "Azure is limiting requests — wait a little",
            "MissingSubscriptionRegistration" => "The subscription isn't registered for this resource provider (Azure portal → Subscription → Resource providers)",
            _ when r.Status == 401 => "Azure refused the sign-in",
            _ when r.Status == 403 => "The app registration isn't allowed to do this",
            _ when r.Status == 404 => "Azure doesn't know this item",
            _ when r.Status == 429 => "Azure is limiting requests — wait a little",
            _ when r.Status >= 500 => $"Azure Resource Manager had a problem (HTTP {r.Status})",
            _ => $"Azure answered HTTP {r.Status}",
        };
        return message.Length > 0 && !what.Contains(message, StringComparison.OrdinalIgnoreCase) ? $"{what}: {message}" : what + ".";
    }

    /// <summary>Follows a long-running change: Azure-AsyncOperation (status in the body) or Location (202 until done).</summary>
    private async Task FollowOperationAsync(CloudResponse first, HttpMethod method, string path, CancellationToken ct)
    {
        var h = first.Headers;
        string? async = h != null && h.TryGetValues("Azure-AsyncOperation", out var av) ? av.FirstOrDefault() : null;
        string? location = h?.Location?.ToString();
        if (async == null && location == null) return;
        var what = $"{method.Method} {Path(path).Split('/').LastOrDefault()}";
        var until = DateTime.UtcNow + OperationTimeout;
        while (true)
        {
            if (DateTime.UtcNow > until)
                throw new CloudApiException("Azure", 0, $"Azure accepted “{what}” and it is still running after {OperationTimeout.TotalMinutes:0} minutes — refresh later to see the result.", uncertain: true);
            await CloudHttp.Sleep(OperationPoll, ct);
            var (root, r) = await RawAsync(HttpMethod.Get, async ?? location!, null, ct, "app", true);
            if (async != null)
            {
                var status = J.Str(root, "status");
                if (status.Equals("Succeeded", StringComparison.OrdinalIgnoreCase)) { CloudProgress.Report("azure", $"✓ {what} finished"); return; }
                if (status is "Failed" or "Canceled")
                    throw new CloudApiException("Azure", 409, $"Azure reports that “{what}” {status.ToLowerInvariant()}: {CloudHttp.FirstLine(J.Str(root, "error.message") is { Length: > 0 } m ? m : J.Str(root, "error.code"))}");
                CloudProgress.Report("azure", $"{what}: {status}");
            }
            else if (r.Status != 202) { CloudProgress.Report("azure", $"✓ {what} finished"); return; }
        }
    }

    /// <summary>Every page of a list (value + nextLink). A failing page fails the whole list: a partial list would make
    /// missing items look deleted.</summary>
    public async Task<List<JsonElement>> ListAsync(string path, string? key, CancellationToken ct = default)
    {
        var all = new List<JsonElement>();
        string? next = path;
        for (int page = 0; next != null && page < MaxPages; page++)
        {
            var (root, _) = await RawAsync(HttpMethod.Get, next, null, ct, "you", true);
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(key ?? "value", out var arr) || arr.ValueKind != JsonValueKind.Array)
                throw new CloudApiException("Azure", 200, "Azure sent a list in an unexpected format.");
            foreach (var item in arr.EnumerateArray()) all.Add(Flatten(item));
            next = J.Str(root, "nextLink") is { Length: > 0 } n ? n : null;
        }
        return all;
    }

    /// <summary>Adds the resource group (from the id) as "_rg" and a VM's power state (instance view "PowerState/…") as
    /// "_power" so lists can show them; everything else is Azure's own JSON.</summary>
    internal static JsonElement Flatten(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return item;
        var id = J.Str(item, "id");
        var m = System.Text.RegularExpressions.Regex.Match(id, "/resourceGroups/([^/]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        string? power = null;
        if (J.Get(item, "properties.instanceView.statuses") is { ValueKind: JsonValueKind.Array } statuses)
            foreach (var s in statuses.EnumerateArray())
                if (J.Str(s, "code") is { } code && code.StartsWith("PowerState/", StringComparison.OrdinalIgnoreCase)) power = code[11..].ToLowerInvariant();
        if (!m.Success && power == null) return item;
        var node = System.Text.Json.Nodes.JsonNode.Parse(item.GetRawText())!.AsObject();
        if (m.Success) node["_rg"] = m.Groups[1].Value;
        if (power != null) node["_power"] = power;
        using var doc = JsonDocument.Parse(node.ToJsonString());
        return doc.RootElement.Clone();
    }

    /// <summary>Azure returns one item as itself.</summary>
    public JsonElement UnwrapItem(JsonElement response) => response;

    /// <summary>Test connection: signs in and reads the subscription; returns its display name and state.</summary>
    public async Task<string> TestAsync(CancellationToken ct = default)
    {
        var (r, _) = await RawAsync(HttpMethod.Get, $"/subscriptions/{Sub}?api-version=2022-12-01", null, ct, "you", true);
        return $"{J.Str(r, "displayName")} ({J.Str(r, "state")})";
    }
}
