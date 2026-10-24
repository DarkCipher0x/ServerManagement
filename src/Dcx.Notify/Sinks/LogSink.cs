using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DcxManager.Notify;

/// <summary>
/// One destination for the app's events (0.4.0, Observability): Elasticsearch / OpenSearch (bulk API), Grafana Loki (push
/// API), Azure Monitor / Microsoft Sentinel (Logs Ingestion API through a data collection rule), Splunk (HTTP Event
/// Collector), Graylog (GELF over HTTP) or any HTTPS webhook taking a JSON array. HTTPS only — there is no plain-HTTP option.
/// The list of sinks is kept as JSON text in the settings (<c>ObserveSinks</c>, admin-protected and a Group Policy) WITHOUT
/// their secrets; each sink's API key, password or token lives in the encrypted settings only (<c>ObserveSecrets</c>, by
/// sink id) and is never a policy. Azure Monitor signs in with one of the Azure subscriptions' app registrations
/// (certificate credential) for the <c>https://monitor.azure.com/.default</c> scope.
/// </summary>
public sealed class LogSink
{
    public const int MaxSinks = 12;
    public const int MaxJsonBytes = 8192;
    public static readonly string[] Kinds = { "elastic", "loki", "azuremonitor", "splunk", "gelf", "webhook" };
    public static readonly string[] Auths = { "none", "apikey", "basic", "bearer", "entra" };

    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = "";
    /// <summary>One of <see cref="Kinds"/>.</summary>
    public string Kind { get; set; } = "elastic";
    /// <summary>The base URL (https): the Elasticsearch / OpenSearch / Loki / Splunk / Graylog server, the Azure data
    /// collection endpoint (…ingest.monitor.azure.com), or the whole webhook URL.</summary>
    public string Url { get; set; } = "";
    /// <summary>Elastic: the index (or data stream). Azure Monitor: the stream name (Custom-…_CL). Splunk: the index
    /// (optional). Loki: the "job" label (optional).</summary>
    public string Index { get; set; } = "";
    /// <summary>Loki: X-Scope-OrgID (multi-tenant). Azure Monitor: the data collection rule's immutable id (dcr-…).</summary>
    public string Tenant { get; set; } = "";
    /// <summary>One of <see cref="Auths"/>: how the secret is sent. "entra" = Azure Monitor only.</summary>
    public string Auth { get; set; } = "apikey";
    /// <summary>Basic auth user name (Grafana Cloud: the numeric instance id).</summary>
    public string User { get; set; } = "";
    /// <summary>Azure Monitor: which Azure subscription's app registration signs in.</summary>
    public string AzureAccountId { get; set; } = "";
    /// <summary>Optional SHA-256 (hex) of the server's certificate or its CA: when set, only that is accepted.</summary>
    public string Pin { get; set; } = "";
    public bool Enabled { get; set; } = true;

    [System.Text.Json.Serialization.JsonIgnore] public string KindTitle => TitleOf(Kind);
    public static string TitleOf(string kind) => kind switch
    {
        "elastic" => "Elasticsearch / OpenSearch", "loki" => "Grafana Loki", "azuremonitor" => "Azure Monitor / Sentinel", "splunk" => "Splunk HEC",
        "gelf" => "Graylog (GELF HTTP)", "webhook" => "HTTPS webhook (JSON)", _ => kind,
    };
    [System.Text.Json.Serialization.JsonIgnore] public string Host => Uri.TryCreate(Url, UriKind.Absolute, out var u) ? u.Host : Url;

    private static readonly Regex NameRx = new(@"^[^\p{C}]{1,60}$"), HexRx = new("^[0-9A-Fa-f]{64}$"), IndexRx = new(@"^[A-Za-z0-9._\-]{1,200}$"), TenantRx = new(@"^[A-Za-z0-9._\-]{1,128}$");

    /// <summary>Why this sink is not acceptable, or null.</summary>
    public string? Validate()
    {
        if (!Regex.IsMatch(Id, "^[A-Za-z0-9]{6,32}$")) return "the id must be 6–32 letters or digits";
        if (!NameRx.IsMatch(Name.Trim())) return "the name is 1–60 characters";
        if (!Kinds.Contains(Kind)) return $"the kind is one of {string.Join(", ", Kinds)}";
        if (!Auths.Contains(Auth)) return $"the authentication is one of {string.Join(", ", Auths)}";
        if (UrlProblem(Url) is { } up) return up;
        if (Pin.Length > 0 && !HexRx.IsMatch(Pin.Replace(":", ""))) return "the certificate pin is a 64-character SHA-256 hex fingerprint";
        if (Index.Length > 0 && !IndexRx.IsMatch(Index)) return "the index / stream is letters, digits, . _ -";
        if (Tenant.Length > 0 && !TenantRx.IsMatch(Tenant)) return "the tenant / rule id is letters, digits, . _ -";
        if (User.Length > 128 || User.Any(char.IsControl)) return "the user name is up to 128 characters";
        if (AzureAccountId.Length > 64 || AzureAccountId.Any(char.IsControl)) return "the Azure subscription id is damaged";
        switch (Kind)
        {
            case "elastic" when Index.Length == 0: return "Elasticsearch / OpenSearch needs an index";
            case "azuremonitor":
                if (Auth != "entra") return "Azure Monitor signs in with an Azure subscription's app registration (authentication “entra”)";
                if (AzureAccountId.Length == 0) return "Azure Monitor needs an Azure subscription (Settings → Cloud providers) to sign in with";
                if (!Tenant.StartsWith("dcr-", StringComparison.OrdinalIgnoreCase)) return "Azure Monitor needs the data collection rule's immutable id (dcr-…)";
                if (!Index.StartsWith("Custom-", StringComparison.Ordinal)) return "Azure Monitor needs the stream name (Custom-…)";
                break;
            default:
                if (Auth == "entra") return "only Azure Monitor signs in with an Azure subscription";
                break;
        }
        return null;
    }

    /// <summary>HTTPS, a host, no user info, no fragment (a sink is POSTed to; a page link may keep its #fragment); or why not.</summary>
    public static string? UrlProblem(string url, bool allowFragment = false)
    {
        if (url.Length is 0 or > 512 || url.Any(char.IsControl)) return "the URL is 1–512 characters";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return "not a URL";
        if (u.Scheme != Uri.UriSchemeHttps) return "only https:// — the app never sends logs unencrypted";
        if (u.UserInfo.Length > 0) return "no user name or password in the URL (use the authentication fields)";
        if (u.Fragment.Length > 0 && !allowFragment) return "the URL can't have a #fragment";
        if (u.Host.Length == 0) return "the URL has no host";
        return null;
    }

    // ---- the list in the settings -----------------------------------------------------------------------------------

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The sinks in <c>ObserveSinks</c>; a damaged list is empty (and <see cref="Check"/> says why).</summary>
    public static List<LogSink> ParseList(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            var list = JsonSerializer.Deserialize<List<LogSink>>(json, Json) ?? new();
            return list.Where(s => s.Validate() == null).ToList();
        }
        catch (JsonException) { return new(); }
    }

    public static string ToJson(IEnumerable<LogSink> sinks) => JsonSerializer.Serialize(sinks.ToList(), Json);

    /// <summary>Why a sinks list (JSON text, from the settings, the admin gate or a policy) is refused, or null.</summary>
    public static string? Check(string json)
    {
        if (json.Length == 0) return null;
        if (Encoding.UTF8.GetByteCount(json) > MaxJsonBytes) return $"more than {MaxJsonBytes / 1024} KB of sinks";
        List<LogSink>? list;
        try { list = JsonSerializer.Deserialize<List<LogSink>>(json, Json); }
        catch (JsonException ex) { return "not a JSON list of sinks (" + ex.Message.Split('.')[0] + ")"; }
        if (list == null) return "not a JSON list of sinks";
        if (list.Count > MaxSinks) return $"more than {MaxSinks} sinks";
        if (list.Select(s => s.Id).Distinct().Count() != list.Count) return "two sinks share an id";
        foreach (var s in list) if (s.Validate() is { } why) return $"sink “{s.Name}”: {why}";
        return null;
    }

    // ---- payloads ---------------------------------------------------------------------------------------------------------

    /// <summary>One HTTP request for a sink: where (relative to the sink's URL, or absolute), what, and its headers.</summary>
    public sealed record Payload(string Path, string ContentType, string Body, IReadOnlyList<(string Name, string Value)> Headers);

    /// <summary>The requests that carry <paramref name="events"/> to this sink (one, except GELF: one per event). The
    /// secret is only ever put in a header here; it never appears in the body or the path.</summary>
    public IReadOnlyList<Payload> Payloads(IReadOnlyList<LogEvent> events, string? secret, string? entraToken = null)
    {
        var headers = new List<(string, string)>();
        switch (Auth)
        {
            case "apikey" when secret is { Length: > 0 }:
                headers.Add(Kind switch { "elastic" => ("Authorization", "ApiKey " + secret), "splunk" => ("Authorization", "Splunk " + secret), _ => ("X-API-Key", secret) });
                break;
            case "basic" when secret is { Length: > 0 }: headers.Add(("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(User + ":" + secret)))); break;
            case "bearer" when secret is { Length: > 0 }: headers.Add(("Authorization", "Bearer " + secret)); break;
            case "entra" when entraToken is { Length: > 0 }: headers.Add(("Authorization", "Bearer " + entraToken)); break;
        }
        switch (Kind)
        {
            case "elastic":
            {
                var sb = new StringBuilder();
                var meta = new JsonObject { ["create"] = new JsonObject { ["_index"] = Index } }.ToJsonString(LogEvent.Compact);
                foreach (var e in events) sb.Append(meta).Append('\n').Append(e.ToJsonString()).Append('\n');
                return new[] { new Payload("/_bulk", "application/x-ndjson", sb.ToString(), headers) };
            }
            case "loki":
            {
                if (Tenant.Length > 0) headers.Add(("X-Scope-OrgID", Tenant));
                var streams = new JsonArray();
                foreach (var g in events.GroupBy(e => e.Kind))
                {
                    var labels = new JsonObject { ["app"] = "dcx-servermanagement", ["host"] = Environment.MachineName, ["kind"] = g.Key };
                    if (Index.Length > 0) labels["job"] = Index;
                    var values = new JsonArray();
                    foreach (var e in g.OrderBy(e => e.Time)) values.Add(new JsonArray((e.Time.ToUnixTimeMilliseconds() * 1_000_000L).ToString(), e.ToJsonString()));
                    streams.Add(new JsonObject { ["stream"] = labels, ["values"] = values });
                }
                return new[] { new Payload("/loki/api/v1/push", "application/json", new JsonObject { ["streams"] = streams }.ToJsonString(LogEvent.Compact), headers) };
            }
            case "azuremonitor":
            {
                var arr = new JsonArray(events.Select(e => (JsonNode)e.ToAzureJson()).ToArray());
                return new[] { new Payload($"/dataCollectionRules/{Uri.EscapeDataString(Tenant)}/streams/{Uri.EscapeDataString(Index)}?api-version=2023-01-01", "application/json", arr.ToJsonString(LogEvent.Compact), headers) };
            }
            case "splunk":
            {
                var sb = new StringBuilder();
                foreach (var e in events)
                {
                    var o = new JsonObject { ["time"] = e.Time.ToUnixTimeMilliseconds() / 1000.0, ["host"] = Environment.MachineName, ["source"] = "dcx-servermanagement", ["sourcetype"] = "dcx:servermanagement", ["event"] = e.ToJson() };
                    if (Index.Length > 0) o["index"] = Index;
                    sb.Append(o.ToJsonString(LogEvent.Compact)).Append('\n');
                }
                return new[] { new Payload("/services/collector/event", "application/json", sb.ToString(), headers) };
            }
            case "gelf":
                return events.Select(e =>
                {
                    var o = new JsonObject
                    {
                        ["version"] = "1.1", ["host"] = Environment.MachineName, ["short_message"] = e.Message.Length > 0 ? e.Message : e.Kind,
                        ["timestamp"] = e.Time.ToUnixTimeMilliseconds() / 1000.0, ["level"] = e.Level,
                        ["_app"] = LogEvent.App, ["_kind"] = e.Kind, ["_actor"] = e.Actor, ["_target"] = e.Target, ["_result"] = e.Result, ["_session"] = e.Session, ["_user"] = Environment.UserName,
                    };
                    return new Payload("/gelf", "application/json", o.ToJsonString(LogEvent.Compact), headers);
                }).ToList();
            default:   // webhook: the URL as given, a JSON array
                return new[] { new Payload("", "application/json", new JsonArray(events.Select(e => (JsonNode)e.ToJson()).ToArray()).ToJsonString(LogEvent.Compact), headers) };
        }
    }

    /// <summary>The absolute request URL for a payload (the sink's URL plus the path; a webhook is sent to its URL as is).</summary>
    public string RequestUrl(Payload p)
    {
        if (p.Path.Length == 0) return Url;
        var b = Url.TrimEnd('/');
        return b + p.Path;
    }

    /// <summary>Whether the server said yes (Elasticsearch answers 200 with per-item errors; the rest by status).</summary>
    public static string? ResponseProblem(string kind, int status, string body)
    {
        if (status is < 200 or > 299) return $"HTTP {status}" + (body.Length > 0 ? ": " + Services.Redaction.Mask(FirstLine(body)) : "");
        if (kind == "elastic")
        {
            try
            {
                using var d = JsonDocument.Parse(body);
                if (d.RootElement.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.True)
                {
                    string reason = "an item was refused";
                    if (d.RootElement.TryGetProperty("items", out var items))
                        foreach (var it in items.EnumerateArray())
                            foreach (var op in it.EnumerateObject())
                                if (op.Value.TryGetProperty("error", out var er) && er.TryGetProperty("reason", out var rs)) { reason = rs.GetString() ?? reason; goto done; }
                    done:
                    return "Elasticsearch refused: " + Services.Redaction.Mask(reason);
                }
            }
            catch (JsonException) { }
        }
        if (kind == "splunk" && body.Contains("\"code\":") && !body.Contains("\"code\":0")) return "Splunk refused: " + Services.Redaction.Mask(FirstLine(body));
        return null;
    }

    private static string FirstLine(string s)
    {
        var t = new string(s.Where(c => !char.IsControl(c) || c == ' ').ToArray()).Trim();
        return t.Length > 200 ? t[..200] + "…" : t;
    }
}
