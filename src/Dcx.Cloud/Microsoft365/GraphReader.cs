using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using DcxManager.Services;
using DcxManager.SignIn;

namespace DcxManager.Microsoft365;

/// <summary>
/// Read-only Microsoft Graph for the Microsoft 365 admin page (0.6.6, ADR-084): a GET with your delegated token, every
/// page of the answer up to a cap, following "@odata.nextLink" only on graph.microsoft.com (https). Each service (Intune,
/// Defender, Entra) has its own file, its own scopes and its own sign-in cache, so a tenant that consented to one isn't
/// asked for the others. Nothing is stored: what is read is shown and kept in memory only.
/// </summary>
public static class GraphReader
{
    public const string Host = "graph.microsoft.com";

    /// <summary>Self-test: a token without the browser.</summary>
    internal static Func<Task<string>>? TokenForTest { get; set; }

    /// <summary>A tenant and an app registration are set (Settings → Integrations → Microsoft 365 admin).</summary>
    public static bool Configured => EntraDelegated.Problem(AppSettings.Current.M365Tenant, AppSettings.Current.M365ClientId) == null;

    /// <summary>A delegated token for one service's scopes, in that service's own cache.</summary>
    public static Task<string> TokenAsync(string scopes, string cache, string feature, CancellationToken ct) =>
        TokenForTest?.Invoke() ?? EntraDelegated.TokenAsync(AppSettings.Current.M365Tenant, AppSettings.Current.M365ClientId, scopes, cache, ct,
            feature: feature, settingsPath: "Settings → Integrations → Microsoft 365 admin");

    /// <summary>Every item of a Graph collection (at most <paramref name="max"/>), as detached JSON elements.</summary>
    public static async Task<List<JsonElement>> ListAsync(string token, string url, int max, string feature, CancellationToken ct, HttpMessageHandler? handler = null, bool eventual = false)
    {
        var first = new Uri(url);
        if (first.Scheme != Uri.UriSchemeHttps || first.IdnHost != Host) throw new ArgumentException("not a Microsoft Graph address");
        Offline.EnforceStatic(Host, feature);
        await Offline.EnforceAsync(Host, feature, ct);
        using var http = new HttpClient(handler ?? Offline.Handler(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false }), disposeHandler: handler == null) { Timeout = TimeSpan.FromSeconds(40) };
        var items = new List<JsonElement>();
        Uri? next = first;
        for (int page = 0; next != null && page < 40 && items.Count < max; page++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, next);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (eventual) req.Headers.Add("ConsistencyLevel", "eventual");
            using var r = await http.SendAsync(req, ct);
            var body = await r.Content.ReadAsStringAsync(ct);
            int code = (int)r.StatusCode;
            if (code is 401 or 403) throw new InvalidOperationException(Refused(feature, code, body));
            if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"{feature}: Microsoft Graph answered HTTP {code}{GraphError(body)}.");
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
                foreach (var v in value.EnumerateArray()) { if (items.Count >= max) break; items.Add(v.Clone()); }
            next = doc.RootElement.TryGetProperty("@odata.nextLink", out var nl) && Uri.TryCreate(nl.GetString(), UriKind.Absolute, out var n)
                && n.Scheme == Uri.UriSchemeHttps && n.IdnHost == Host ? n : null;
        }
        return items;
    }

    private static string Refused(string feature, int code, string body) =>
        $"{feature}: Microsoft Graph refused it (HTTP {code}{GraphError(body)}). The app registration needs the permissions listed in Settings → Integrations → Microsoft 365 admin with admin consent, your account needs a role that can read this, and some lists need an Entra ID P1 / P2 licence (sign-ins: P1; risky users: P2).";

    private static string GraphError(string body)
    {
        try
        {
            using var d = JsonDocument.Parse(body);
            if (d.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("code", out var c) && c.GetString() is { Length: > 0 } s)
                return ": " + (s.Length > 80 ? s[..80] : s);
        }
        catch (JsonException) { }
        return "";
    }

    // ---- reading values safely ---------------------------------------------------------------------------------------

    public static string S(JsonElement e, string name, int max = 160)
    {
        var v = e.TryGetProperty(name, out var p) ? p.ValueKind switch { JsonValueKind.String => p.GetString() ?? "", JsonValueKind.True => "yes", JsonValueKind.False => "no", JsonValueKind.Number => p.GetRawText(), _ => "" } : "";
        v = new string(v.Where(ch => !char.IsControl(ch)).ToArray());
        return v.Length <= max ? v : v[..max] + "…";
    }

    public static string Nested(JsonElement e, params string[] names)
    {
        foreach (var n in names[..^1]) { if (!e.TryGetProperty(n, out e) || e.ValueKind != JsonValueKind.Object) return ""; }
        return S(e, names[^1]);
    }

    /// <summary>A date as local "yyyy-MM-dd HH:mm" (empty when missing).</summary>
    public static string When(JsonElement e, string name) =>
        DateTimeOffset.TryParse(S(e, name), out var d) && d.Year > 2000 ? d.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "";

    /// <summary>A GUID id, or "" (only checked ids go into links).</summary>
    public static string Id(JsonElement e, string name = "id") => Guid.TryParse(S(e, name), out var g) ? g.ToString("D") : "";
}
