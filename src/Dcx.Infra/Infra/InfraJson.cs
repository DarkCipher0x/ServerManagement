using System.Net.Http;
using System.Text.Json.Nodes;

namespace DcxManager.Infra;

/// <summary>
/// Small, strict JSON helpers shared by the REST providers added in 0.6.33 (Redfish, HPE GreenLake, Sophos Central): a
/// parse with duplicate keys refused and a depth cap, type-safe reads that never throw on an unexpected shape, the fields
/// of an object flattened for "ⓘ All fields", and a bounded body read.
/// </summary>
internal static class InfraJson
{
    /// <summary>The most a provider reads from one response.</summary>
    public const int MaxBody = 16 * 1024 * 1024;

    public static JsonNode? Parse(string text) =>
        JsonNode.Parse(text, null, new System.Text.Json.JsonDocumentOptions { AllowDuplicateProperties = false, MaxDepth = 64 });

    /// <summary>A value along <paramref name="path"/> as text ("" when missing or not a value).</summary>
    public static string S(JsonNode? n, params string[] path)
    {
        foreach (var p in path) n = n is JsonObject o ? o[p] : null;
        return n is JsonValue v ? v.ToString() : "";
    }

    public static JsonNode? At(JsonNode? n, params string[] path)
    {
        foreach (var p in path) n = n is JsonObject o ? o[p] : null;
        return n;
    }

    public static IEnumerable<JsonObject> Objects(JsonNode? n) => (n as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>();

    public static IEnumerable<string> Strings(JsonNode? n) => (n as JsonArray)?.OfType<JsonValue>().Select(v => v.ToString()) ?? Enumerable.Empty<string>();

    /// <summary>Every field (three levels deep; arrays as their length), for "ⓘ All fields".</summary>
    public static Dictionary<string, string> Flat(JsonObject o, string prefix = "", Dictionary<string, string>? into = null, int depth = 0)
    {
        into ??= new();
        foreach (var (k, v) in o)
        {
            if (k.StartsWith("@odata", StringComparison.Ordinal) || into.Count >= 400) continue;
            var key = prefix + k;
            if (v is JsonObject child && depth < 3) Flat(child, key + ".", into, depth + 1);
            else if (v is JsonArray arr) into[key] = $"[{arr.Count}]";
            else into[key] = v?.ToString() ?? "";
        }
        return into;
    }

    /// <summary>The body as text, refusing more than <see cref="MaxBody"/>.</summary>
    public static async Task<string> ReadAsync(HttpResponseMessage r, CancellationToken ct)
    {
        if (r.Content.Headers.ContentLength is > MaxBody) throw new InvalidOperationException("the answer is too large");
        var text = await r.Content.ReadAsStringAsync(ct);
        return text.Length > MaxBody ? throw new InvalidOperationException("the answer is too large") : text;
    }

    /// <summary>A short error text from a JSON error body (message / error / error_description / Redfish's error.message).</summary>
    public static string Message(string text)
    {
        try
        {
            var j = Parse(text) as JsonObject;
            var m = S(j, "error", "message") is { Length: > 0 } rf ? rf
                : S(j, "error_description") is { Length: > 0 } ed ? ed
                : S(j, "message") is { Length: > 0 } msg ? msg
                : S(j, "error") is { Length: > 0 } e ? e : "";
            if (m.Length > 0) return m.Length > 300 ? m[..300] : m;
        }
        catch (System.Text.Json.JsonException) { }
        var t = text.Trim();
        return t.Length == 0 ? "(no details)" : t.Length > 200 ? t[..200] + "…" : t;
    }

    /// <summary>An https URL whose host is <paramref name="domain"/> or one of its sub-domains (an address an API hands
    /// back — a data region, a regional host — is only followed when it belongs to the service).</summary>
    public static bool IsHttpsIn(string url, string domain) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && u.UserInfo.Length == 0
        && (u.Host.Equals(domain, StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));
}
