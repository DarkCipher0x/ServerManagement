using System.Net.Http;
using System.Text.Json;
using DcxManager.Services;

namespace DcxManager.Notify;

/// <summary>A Grafana dashboard as the search API lists it.</summary>
public sealed record GrafanaDashboard(string Uid, string Title, string Url, string Folder, string[] Tags);

/// <summary>
/// Reads the dashboard list of a Grafana server (0.4.0, Observability → Dashboards): <c>GET /api/search?type=dash-db</c>
/// with a service-account token (Bearer; kept in the encrypted settings, never a policy). HTTPS only; offline mode applies
/// through the shared HTTP guard. A dashboard opens in the app's own browser window at the server's URL plus the path
/// Grafana gave — never a URL from the answer that points anywhere else (<see cref="DashboardUrl"/>).
/// </summary>
public static class GrafanaClient
{
    public const int MaxDashboards = 500;
    internal static HttpMessageHandler? HandlerForTest { get; set; }
    internal static TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    private static HttpClient? _http;

    /// <summary>Why a Grafana / Kibana / SIEM URL is refused, or null (https, no user info; a page may keep its #fragment —
    /// the Azure portal addresses Sentinel that way).</summary>
    public static string? ValidateUrl(string url) => LogSink.UrlProblem(url, allowFragment: true);

    public static async Task<List<GrafanaDashboard>> DashboardsAsync(string baseUrl, string? token, int orgId, CancellationToken ct = default)
    {
        if (ValidateUrl(baseUrl) is { } bad) throw new InvalidOperationException("Grafana URL: " + bad);
        var http = _http ??= new HttpClient(HandlerForTest ?? Offline.Handler()) { Timeout = Timeout.InfiniteTimeSpan };
        using var req = new HttpRequestMessage(HttpMethod.Get, baseUrl.TrimEnd('/') + $"/api/search?type=dash-db&limit={MaxDashboards}");
        if (!string.IsNullOrEmpty(token)) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        if (orgId > 0) req.Headers.TryAddWithoutValidation("X-Grafana-Org-Id", orgId.ToString());
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(RequestTimeout);
        HttpResponseMessage r;
        try { r = await http.SendAsync(req, cts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException($"no answer from Grafana within {RequestTimeout.TotalSeconds:0} s"); }
        using (r)
        {
            var body = r.Content.Headers.ContentLength is > 4_000_000 ? "" : await r.Content.ReadAsStringAsync(cts.Token);
            if ((int)r.StatusCode == 401 || (int)r.StatusCode == 403) throw new InvalidOperationException("Grafana refused the token (HTTP " + (int)r.StatusCode + "): create a service account token with the Viewer role and store it in Settings → Integrations → Dashboards");
            if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"Grafana answered HTTP {(int)r.StatusCode}");
            var list = new List<GrafanaDashboard>();
            try
            {
                using var d = JsonDocument.Parse(body);
                if (d.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("Grafana sent an answer the app can't read");
                foreach (var e in d.RootElement.EnumerateArray().Take(MaxDashboards))
                {
                    string S(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? Clean(v.GetString() ?? "") : "";
                    var tags = e.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array ? t.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => Clean(x.GetString() ?? "")).Take(20).ToArray() : Array.Empty<string>();
                    if (S("uid").Length == 0 || DashboardUrl(baseUrl, S("url")) == null) continue;
                    list.Add(new GrafanaDashboard(S("uid"), S("title"), S("url"), S("folderTitle"), tags));
                }
            }
            catch (JsonException) { throw new InvalidOperationException("Grafana sent an answer the app can't read"); }
            return list.OrderBy(x => x.Folder).ThenBy(x => x.Title).ToList();
        }
    }

    /// <summary>The absolute URL of a dashboard on this server: the base plus Grafana's own path ("/d/uid/slug"). A path that
    /// isn't a plain absolute path on the same server (another host, a scheme, "//") is refused (null).</summary>
    public static string? DashboardUrl(string baseUrl, string path)
    {
        if (LogSink.UrlProblem(baseUrl) != null) return null;   // the server's base URL itself: no fragment
        if (path.Length is 0 or > 512 || !path.StartsWith('/') || path.StartsWith("//") || path.Contains('\\') || path.Any(c => char.IsControl(c) || c == '#')) return null;
        if (!Uri.TryCreate(baseUrl.TrimEnd('/') + path, UriKind.Absolute, out var u)) return null;
        var b = new Uri(baseUrl);
        return u.Scheme == Uri.UriSchemeHttps && u.Host.Equals(b.Host, StringComparison.OrdinalIgnoreCase) && u.Port == b.Port ? u.AbsoluteUri : null;
    }

    private static string Clean(string s)
    {
        var t = new string(s.Where(c => !char.IsControl(c)).ToArray());
        return t.Length > 200 ? t[..200] + "…" : t;
    }

    internal static void ResetForTest() { _http?.Dispose(); _http = null; }
}
