using System.Net.Http;
using System.Text.Json;
using DcxManager.Services;

namespace DcxManager.Notify;

/// <summary>A Pingdom uptime check as the API lists it.</summary>
public sealed record PingdomCheck(long Id, string Name, string Hostname, string Type, string Status, int? LastResponseMs, DateTime? LastTest, DateTime? LastError, string[] Tags);

/// <summary>
/// Pingdom (0.5.0, plan #17; DarkCipher0x: "an API connection to Pingdom for the pinned checks"): reads the uptime checks with a
/// read-only API token (Pingdom → Settings → Pingdom API, "Read access"), kept in the encrypted settings, never a policy.
/// Only https://api.pingdom.com/api/3.1 is ever contacted (GET only); offline mode applies through the shared HTTP guard.
/// A pinned Pingdom check on the dashboard reads its state from here on the dashboard's own schedule.
/// </summary>
public static class PingdomClient
{
    public const string Base = "https://api.pingdom.com/api/3.1";
    public const string SecretKey = "pingdom";
    public const int MaxChecks = 25_000;
    internal static HttpMessageHandler? HandlerForTest { get; set; }
    internal static TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    private static HttpClient? _http;

    public static string? Token => AppSettings.Current.ObserveSecrets.GetValueOrDefault(SecretKey) is { Length: > 0 } t ? t : null;

    private static async Task<JsonDocument> GetAsync(string path, CancellationToken ct)
    {
        var token = Token ?? throw new InvalidOperationException("No Pingdom API token yet: Settings → Integrations → Pingdom.");
        var http = _http ??= new HttpClient(HandlerForTest ?? Offline.Handler()) { Timeout = Timeout.InfiniteTimeSpan };
        using var req = new HttpRequestMessage(HttpMethod.Get, Base + path);
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(RequestTimeout);
        HttpResponseMessage r;
        try { r = await http.SendAsync(req, cts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException($"no answer from Pingdom within {RequestTimeout.TotalSeconds:0} s"); }
        using (r)
        {
            if (r.Content.Headers.ContentLength is > 16_000_000) throw new InvalidOperationException("Pingdom's answer is too large");
            var body = await r.Content.ReadAsStringAsync(cts.Token);
            if ((int)r.StatusCode is 401 or 403) throw new InvalidOperationException($"Pingdom refused the token (HTTP {(int)r.StatusCode}): create a read-only API token in Pingdom → Settings → Pingdom API.");
            if ((int)r.StatusCode == 429) throw new InvalidOperationException("Pingdom's rate limit was reached: try again in a minute.");
            if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"Pingdom answered HTTP {(int)r.StatusCode}");
            try { return JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32 }); }
            catch (JsonException) { throw new InvalidOperationException("Pingdom sent an answer the app can't read"); }
        }
    }

    /// <summary>Every uptime check (GET /checks?include_tags=true).</summary>
    public static async Task<List<PingdomCheck>> ChecksAsync(CancellationToken ct = default)
    {
        using var d = await GetAsync("/checks?include_tags=true", ct);
        if (!d.RootElement.TryGetProperty("checks", out var arr) || arr.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("Pingdom sent an answer the app can't read");
        return arr.EnumerateArray().Take(MaxChecks).Select(Read).Where(c => c != null).Cast<PingdomCheck>().OrderBy(c => c.Name).ToList();
    }

    /// <summary>One check (GET /checks/{id}).</summary>
    public static async Task<PingdomCheck> CheckAsync(long id, CancellationToken ct = default)
    {
        if (id <= 0) throw new ArgumentException("check id");
        using var d = await GetAsync($"/checks/{id}", ct);
        return d.RootElement.TryGetProperty("check", out var c) && Read(c) is { } x ? x : throw new InvalidOperationException("Pingdom sent an answer the app can't read");
    }

    /// <summary>One check object (the list and the single-check answer share the fields used here).</summary>
    public static PingdomCheck? Read(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("id", out var idv) || idv.ValueKind != JsonValueKind.Number || !idv.TryGetInt64(out var id)) return null;
        string S(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? Clean(v.GetString() ?? "") : "";
        int? I(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
        DateTime? T(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var s) && s > 0 ? DateTimeOffset.FromUnixTimeSeconds(s).LocalDateTime : null;
        // "type" is a string in the list and an object ({"http": {…}}) in a single check.
        var type = e.TryGetProperty("type", out var tv) ? tv.ValueKind == JsonValueKind.String ? tv.GetString() ?? "" : tv.ValueKind == JsonValueKind.Object ? tv.EnumerateObject().FirstOrDefault().Name ?? "" : "" : "";
        var tags = e.TryGetProperty("tags", out var tg) && tg.ValueKind == JsonValueKind.Array
            ? tg.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Object && x.TryGetProperty("name", out var n) ? Clean(n.GetString() ?? "") : x.ValueKind == JsonValueKind.String ? Clean(x.GetString() ?? "") : "").Where(x => x.Length > 0).Take(20).ToArray()
            : Array.Empty<string>();
        return new PingdomCheck(id, S("name"), S("hostname"), Clean(type), S("status"), I("lastresponsetime"), T("lasttesttime"), T("lasterrortime"), tags);
    }

    /// <summary>A dashboard row's state: up → OK, down / unconfirmed_down → failing, paused / unknown → not judged.</summary>
    public static bool? Healthy(string status) => status switch { "up" => true, "down" or "unconfirmed_down" => false, _ => null };

    private static string Clean(string s)
    {
        var t = new string(s.Where(c => !char.IsControl(c)).ToArray());
        return t.Length > 200 ? t[..200] + "…" : t;
    }

    internal static void ResetForTest() { _http?.Dispose(); _http = null; }
}
