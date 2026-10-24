using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DcxManager.Cloud;
using DcxManager.Services;

namespace DcxManager.Gcp;

/// <summary>
/// The Google Cloud APIs the app uses, for one project (0.2.0).
/// <list type="bullet">
/// <item>Sign-in: <see cref="GcpAuth"/> (service-account key). The bearer token never reaches a message, a log line or
/// the audit log.</item>
/// <item>Paths: full https URLs on an allow-listed Google API host (<see cref="Hosts"/>); <c>_PROJECT_</c> stands for the
/// project id. A URL that names another project, or any other host, is refused before it is sent.</item>
/// <item>Read-only (the default for a new project): changes are refused here before anything is sent, and the token only
/// carries Google's read-only scope.</item>
/// <item>Lists: the catalog's key (or Compute's aggregated <c>items</c> map, flattened) plus <c>nextPageToken</c> pages
/// (bounded). Long-running changes (Compute, GKE and Cloud SQL operations; Cloud Run's long-running operations) are
/// followed until done, bounded and cancellable.</item>
/// <item>Errors: <c>{"error": {"code", "message", "status"}}</c>, turned into one sanitised sentence.</item>
/// </list>
/// </summary>
public sealed class GcpClient : ICloudApi
{
    public const int MaxPages = 100;
    /// <summary>Stands for the project id in catalog paths (a plain token, so templates never touch it).</summary>
    public const string Project = "_PROJECT_";
    /// <summary>The Google API hosts the app talks to (and nothing else).</summary>
    public static readonly string[] Hosts =
    {
        "compute.googleapis.com", "dns.googleapis.com", "storage.googleapis.com", "container.googleapis.com", "run.googleapis.com", "sqladmin.googleapis.com",
        "cloudresourcemanager.googleapis.com",
        // 0.4.0: Cloud Functions (2nd gen), Artifact Registry, Pub/Sub. IAM stays out on purpose (identity and keys).
        "cloudfunctions.googleapis.com", "artifactregistry.googleapis.com", "pubsub.googleapis.com",
    };

    // Test seams: the self-test points the client at an in-process fake (no network, no real project).
    internal static TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);
    internal static TimeSpan OperationPoll = TimeSpan.FromSeconds(3);
    internal static TimeSpan OperationTimeout = TimeSpan.FromMinutes(20);
    private static HttpClient _http = NewDefaultClient();
    private static HttpClient NewDefaultClient() => new(Offline.Handler()) { Timeout = Timeout.InfiniteTimeSpan };
    internal static void UseHandler(HttpMessageHandler? handler)
    {
        _http = handler == null ? NewDefaultClient() : new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        GcpAuth.ForgetAll();
        lock (ProjectNumbers) ProjectNumbers.Clear();
    }

    private readonly GcpAccount _account;
    public CloudProvider Provider => GcpProvider.Instance;
    public string AccountId => _account.Id;
    public string AccountName => _account.Name;

    public GcpClient(GcpAccount account) => _account = account;

    /// <summary>The URL for a catalog path or a Google link, or an error when it would leave the allowed APIs or project.</summary>
    internal string Url(string path)
    {
        var project = Uri.EscapeDataString(_account.ProjectId);
        if (project.Length == 0) throw new CloudApiException("Google Cloud", 0, "This Google Cloud project has no project id.");
        path = path.Replace(Project, project, StringComparison.Ordinal);
        if (!Uri.TryCreate(path, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps || !Hosts.Contains(u.Host, StringComparer.OrdinalIgnoreCase) || !u.IsDefaultPort || u.UserInfo.Length > 0)
            throw new CloudApiException("Google Cloud", 0, "Refused a URL outside the Google Cloud APIs the app uses.");
        foreach (var named in ProjectSegments(u.AbsolutePath))
            if (!named.Equals(project, StringComparison.OrdinalIgnoreCase) && !(ProjectNumberOf(_account.Id) is { } number && named == number))
                throw new CloudApiException("Google Cloud", 0, "Refused a URL in another project.");
        if (System.Web.HttpUtility.ParseQueryString(u.Query)["project"] is { } qp && !qp.Equals(_account.ProjectId, StringComparison.OrdinalIgnoreCase))
            throw new CloudApiException("Google Cloud", 0, "Refused a URL in another project.");
        return u.AbsoluteUri;
    }

    public async Task<JsonElement> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct = default, string actor = "you", bool idempotent = true)
    {
        bool mutating = method != HttpMethod.Get && method != HttpMethod.Head;
        if (mutating && _account.ReadOnly)
        {
            CloudHttp.Record("Google Cloud", _account.Name, method, Display(path), null, actor, "refused locally: the project is read-only in this app");
            throw new CloudApiException("Google Cloud", 403, $"Not sent: “{_account.Name}” is read-only in this app. Allow changes for it in Settings → Cloud providers → Google Cloud projects first (the service account also needs an IAM role that allows the change).");
        }
        string? json = body switch { null => null, string s => s, JsonElement e => e.GetRawText(), _ => JsonSerializer.Serialize(body) };
        var root = await RawAsync(method, path, json, ct, actor, idempotent);
        if (mutating) await FollowOperationAsync(root, method, path, ct);
        return root;
    }

    /// <summary>Host and path only (logs and the audit log never carry the query).</summary>
    private static string Display(string path) =>
        CloudHttp.StripQuery(path.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? path[8..] : path);

    private static IEnumerable<string> ProjectSegments(string absolutePath)
    {
        var segments = absolutePath.Split('/');
        for (int i = 0; i < segments.Length - 1; i++) if (segments[i] == "projects") yield return segments[i + 1];
    }

    /// <summary>Some Google APIs name the project by its number (Cloud Run's operations). The number is read once from
    /// Resource Manager and kept in memory, so the project check can accept it without accepting any other project.</summary>
    private static readonly Dictionary<string, string> ProjectNumbers = new();
    private static string? ProjectNumberOf(string accountId) { lock (ProjectNumbers) return ProjectNumbers.TryGetValue(accountId, out var n) ? n : null; }

    private async Task EnsureProjectNumberAsync(string path, CancellationToken ct)
    {
        if (ProjectNumberOf(_account.Id) != null || !Uri.TryCreate(path, UriKind.Absolute, out var u)) return;
        if (!ProjectSegments(u.AbsolutePath).Any(s => s.Length > 0 && s.All(char.IsAsciiDigit))) return;
        var p = await RawAsync(HttpMethod.Get, $"https://cloudresourcemanager.googleapis.com/v1/projects/{Project}", null, ct, "app", true);
        if (J.Str(p, "projectNumber") is { Length: > 0 } n) lock (ProjectNumbers) ProjectNumbers[_account.Id] = n;
    }

    private async Task<JsonElement> RawAsync(HttpMethod method, string path, string? json, CancellationToken ct, string actor, bool idempotent)
    {
        await EnsureProjectNumberAsync(path, ct);
        var url = Url(path);
        CloudResponse r;
        try
        {
            var token = await GcpAuth.TokenAsync(_http, _account, RequestTimeout, ct);
            r = await CloudHttp.SendAsync(_http, () =>
            {
                var req = new HttpRequestMessage(method, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                req.Headers.UserAgent.ParseAdd($"DarkCipherServerManagement/{AppInfo.Version}");
                if (json != null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                return req;
            }, method, idempotent, RequestTimeout, "Google Cloud", ct);
        }
        catch (CloudApiException ex) { CloudHttp.Record("Google Cloud", _account.Name, method, Display(path), null, actor, ex.Message); throw; }
        if (r.Status == 401) GcpAuth.Forget(_account.Id);
        if (!r.Ok)
        {
            var msg = ErrorText(r);
            CloudHttp.Record("Google Cloud", _account.Name, method, Display(path), r, actor, msg);
            throw new CloudApiException("Google Cloud", r.Status, msg, r.RequestId);
        }
        CloudHttp.Record("Google Cloud", _account.Name, method, Display(path), r, actor);
        if (string.IsNullOrWhiteSpace(r.Text)) return default;
        try { using var doc = JsonDocument.Parse(r.Text); return doc.RootElement.Clone(); }
        catch (JsonException) { throw new CloudApiException("Google Cloud", r.Status, "Google sent a response that isn't valid JSON."); }
    }

    /// <summary>The error in one sentence: Google's status explained where it is a common cause, its message kept (first line, bounded).</summary>
    internal static string ErrorText(CloudResponse r)
    {
        string status = "", message = "", reason = "";
        try
        {
            using var d = JsonDocument.Parse(r.Text);
            if (d.RootElement.ValueKind == JsonValueKind.Object && d.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object)
            {
                status = J.Str(e, "status");
                message = CloudHttp.FirstLine(J.Str(e, "message"));
                if (e.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array && errs.GetArrayLength() > 0) reason = J.Str(errs[0], "reason");
                if (e.TryGetProperty("details", out var det) && det.ValueKind == JsonValueKind.Array)
                    foreach (var x in det.EnumerateArray()) if (J.Str(x, "reason") is { Length: > 0 } rr) { reason = rr; break; }
            }
        }
        catch (JsonException) { }
        var what = (status, reason) switch
        {
            (_, "accessNotConfigured" or "SERVICE_DISABLED") => "This API isn't enabled in the project (Google Cloud console → APIs & Services → enable it)",
            ("PERMISSION_DENIED", _) or (_, "forbidden" or "insufficientPermissions") => "The service account's IAM roles don't allow this (grant e.g. Viewer, or a role for this service)",
            ("UNAUTHENTICATED", _) => "Google refused the sign-in token — test the project again",
            ("NOT_FOUND", _) or (_, "notFound") => "Google doesn't know this item (it may have been removed)",
            ("ALREADY_EXISTS", _) or (_, "alreadyExists") => "An item with this name already exists",
            ("FAILED_PRECONDITION", _) or (_, "resourceNotReady" or "resourceInUseByAnotherResource") => "Google doesn't allow this in the item's current state",
            ("RESOURCE_EXHAUSTED", _) or (_, "quotaExceeded" or "rateLimitExceeded") => "A quota or rate limit is reached — wait a little or raise the quota",
            ("INVALID_ARGUMENT", _) or (_, "invalid" or "required") => "Google rejected a value",
            _ when r.Status == 401 => "Google refused the sign-in",
            _ when r.Status == 403 => "The service account isn't allowed to do this",
            _ when r.Status == 404 => "Google doesn't know this item",
            _ when r.Status == 429 => "Google is limiting requests — wait a little",
            _ when r.Status >= 500 => $"The Google API had a problem (HTTP {r.Status})",
            _ => $"Google answered HTTP {r.Status}",
        };
        return message.Length > 0 && !what.Contains(message, StringComparison.OrdinalIgnoreCase) ? $"{what}: {message}" : what + ".";
    }

    /// <summary>Follows a long-running change to its end: Compute / GKE / Cloud SQL operations (status PENDING / RUNNING
    /// → DONE, polled at their selfLink) and Cloud Run's long-running operations (done: false → true, polled by name).</summary>
    private async Task FollowOperationAsync(JsonElement op, HttpMethod method, string path, CancellationToken ct)
    {
        if (op.ValueKind != JsonValueKind.Object) return;
        var host = new Uri(Url(path)).Host;
        string? poll = null;
        // Compute's selfLinks name www.googleapis.com: the operation is polled on the API host the change went to.
        if (J.Str(op, "status") is "PENDING" or "RUNNING" && J.Str(op, "selfLink") is { Length: > 0 } self && Uri.TryCreate(self, UriKind.Absolute, out var selfUri)
            && J.Str(op, "kind") is var kind && (kind.EndsWith("#operation", StringComparison.Ordinal) || host.StartsWith("container.", StringComparison.Ordinal)))
            poll = $"https://{host}{Tpl.SafePath(Uri.UnescapeDataString(selfUri.AbsolutePath))}";
        else if (op.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.False && J.Str(op, "name") is { Length: > 0 } name && name.StartsWith("projects/", StringComparison.Ordinal))
            poll = $"https://{host}/v2/{Tpl.SafePath(name)}";
        if (poll == null) { ThrowIfFailed(op, method, path); return; }
        var what = $"{method.Method} {CloudHttp.StripQuery(path).Split('/').LastOrDefault()}";
        var until = DateTime.UtcNow + OperationTimeout;
        while (true)
        {
            if (DateTime.UtcNow > until)
                throw new CloudApiException("Google Cloud", 0, $"Google accepted “{what}” and it is still running after {OperationTimeout.TotalMinutes:0} minutes — refresh later to see the result.", uncertain: true);
            await CloudHttp.Sleep(OperationPoll, ct);
            var cur = await RawAsync(HttpMethod.Get, poll, null, ct, "app", true);
            var finished = J.Str(cur, "status") == "DONE" || cur.TryGetProperty("done", out var d) && d.ValueKind == JsonValueKind.True;
            if (finished) { ThrowIfFailed(cur, method, path); CloudProgress.Report("gcp", $"✓ {what} finished"); return; }
            CloudProgress.Report("gcp", $"{what}: {(J.Str(cur, "status") is { Length: > 0 } s ? s.ToLowerInvariant() : "running")}");
        }
    }

    /// <summary>A finished operation can still have failed: error.errors[] (Compute, Cloud SQL), error.message (Cloud Run)
    /// or statusMessage with an error (GKE).</summary>
    private static void ThrowIfFailed(JsonElement op, HttpMethod method, string path)
    {
        if (op.ValueKind != JsonValueKind.Object || !op.TryGetProperty("error", out var e) || e.ValueKind != JsonValueKind.Object) return;
        var message = e.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array && errs.GetArrayLength() > 0
            ? J.Str(errs[0], "message") : J.Str(e, "message");
        throw new CloudApiException("Google Cloud", 409, $"Google reports that {method.Method} {CloudHttp.StripQuery(path).Split('/').LastOrDefault()} failed: {CloudHttp.FirstLine(message.Length > 0 ? message : "no reason given")}");
    }

    /// <summary>Every page of a list (the key's array, or Compute's aggregated map flattened) with nextPageToken. A
    /// failing page fails the whole list: a partial list would make missing items look deleted.</summary>
    public async Task<List<JsonElement>> ListAsync(string path, string? key, CancellationToken ct = default)
    {
        var all = new List<JsonElement>();
        string? token = null;
        for (int page = 0; page < MaxPages; page++)
        {
            var url = token == null ? path : path + (path.Contains('?') ? "&" : "?") + "pageToken=" + Uri.EscapeDataString(token);
            var root = await RawAsync(HttpMethod.Get, url, null, ct, "you", true);
            if (root.ValueKind == JsonValueKind.Undefined) break;   // some Google lists answer {} or nothing when empty
            if (root.ValueKind != JsonValueKind.Object) throw new CloudApiException("Google Cloud", 200, "Google sent a list in an unexpected format.");
            if (root.TryGetProperty(key ?? "items", out var arr))
            {
                if (arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in arr.EnumerateArray()) all.Add(Flatten(item));
                }
                else if (arr.ValueKind == JsonValueKind.Object)   // aggregated: {"zones/europe-west2-a": {"instances": [...]}, "regions/…": {"warning": …}}
                {
                    foreach (var scope in arr.EnumerateObject())
                        if (scope.Value.ValueKind == JsonValueKind.Object)
                            foreach (var p in scope.Value.EnumerateObject())
                                if (p.Value.ValueKind == JsonValueKind.Array)
                                    foreach (var item in p.Value.EnumerateArray()) all.Add(Flatten(item));
                }
                else throw new CloudApiException("Google Cloud", 200, "Google sent a list in an unexpected format.");
            }
            token = J.Str(root, "nextPageToken") is { Length: > 0 } n ? n : null;
            if (token == null) break;
        }
        return all;
    }

    /// <summary>Adds what lists show and templates need, from Google's own fields: "_self" (the item's path on its API host,
    /// from selfLink or a relative "projects/…" name), "_zone" / "_region" / "_type" (last segment of those URLs), "_location"
    /// and, for VMs, "_ip" (first external address) and "_internal" (first internal address).</summary>
    internal static JsonElement Flatten(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return item;
        var node = JsonNode.Parse(item.GetRawText())!.AsObject();
        static string Last(string s) => s.Length == 0 ? "" : s[(s.LastIndexOf('/') + 1)..];
        var selfLink = J.Str(item, "selfLink");
        if (selfLink.Length > 0 && Uri.TryCreate(selfLink, UriKind.Absolute, out var u)) node["_self"] = Uri.UnescapeDataString(u.AbsolutePath);
        else if (J.Str(item, "name") is { } name && name.StartsWith("projects/", StringComparison.Ordinal)) node["_self"] = "/v2/" + name;
        if (J.Str(item, "zone") is { Length: > 0 } zone) node["_zone"] = Last(zone);
        if (J.Str(item, "region") is { Length: > 0 } region) node["_region"] = Last(region);
        if (J.Str(item, "machineType") is { Length: > 0 } type) node["_type"] = Last(type);
        else if (J.Str(item, "type") is { } dtype && dtype.StartsWith("https://", StringComparison.Ordinal)) node["_type"] = Last(dtype);
        if (J.Str(item, "network") is { } net && net.StartsWith("https://", StringComparison.Ordinal)) node["_network"] = Last(net);
        if (J.Str(item, "name") is { } full && full.StartsWith("projects/", StringComparison.Ordinal)) node["_short"] = Last(full);
        if (J.Get(item, "ipAddresses") is { ValueKind: JsonValueKind.Array } ips && ips.GetArrayLength() > 0)   // Cloud SQL
            node["_ip"] = ips.EnumerateArray().Where(x => J.Str(x, "type") == "PRIMARY").Select(x => J.Str(x, "ipAddress")).FirstOrDefault() ?? J.Str(ips[0], "ipAddress");
        var location = J.Str(item, "location") is { Length: > 0 } l ? l
            : System.Text.RegularExpressions.Regex.Match(J.Str(item, "name"), "/locations/([^/]+)/").Groups[1].Value;
        if (location.Length > 0) node["_location"] = location;
        else if (node["_zone"] is { } z) node["_location"] = z.GetValue<string>();
        else if (node["_region"] is { } rg) node["_location"] = rg.GetValue<string>();
        if (J.Get(item, "networkInterfaces") is { ValueKind: JsonValueKind.Array } nics && nics.GetArrayLength() > 0)
        {
            node["_internal"] = J.Str(nics[0], "networkIP");
            if (J.Get(nics[0], "accessConfigs") is { ValueKind: JsonValueKind.Array } acs && acs.GetArrayLength() > 0) node["_ip"] = J.Str(acs[0], "natIP");
        }
        using var doc = JsonDocument.Parse(node.ToJsonString());
        return doc.RootElement.Clone();
    }

    /// <summary>Google returns one item as itself.</summary>
    public JsonElement UnwrapItem(JsonElement response) => response.ValueKind == JsonValueKind.Object ? Flatten(response) : response;

    /// <summary>Test connection: signs in and reads the project; returns its name and state.</summary>
    public async Task<string> TestAsync(CancellationToken ct = default)
    {
        var r = await RawAsync(HttpMethod.Get, $"https://cloudresourcemanager.googleapis.com/v1/projects/{Project}", null, ct, "you", true);
        return $"{(J.Str(r, "name") is { Length: > 0 } n ? n : J.Str(r, "projectId"))} ({J.Str(r, "lifecycleState")})";
    }
}
