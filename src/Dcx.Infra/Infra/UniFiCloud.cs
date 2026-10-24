using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DcxManager.Services;

namespace DcxManager.Infra;

/// <summary>
/// The whole UniFi estate through Ubiquiti's Site Manager API (0.6.16, ADR-094): <c>https://api.ui.com/v1</c> with an API key
/// from unifi.ui.com → API Keys in the <c>X-API-Key</c> header. Read-only by design (the Site Manager API is a fleet view):
/// every console (host) the account owns or administers with its state and version, every site with its health counts,
/// every device with its firmware status, the ISP metrics of the last hours (latency, packet loss, throughput, uptime) and
/// SD-WAN configurations. Lists follow <c>nextToken</c> pages; a 429 is waited out once. A console can be added as a
/// UniFi Network provider through the Cloud Connector in one step, with the same key.
/// </summary>
public sealed class UniFiCloudProvider : IInfraProvider
{
    private readonly ProviderConfig _c;
    private readonly HttpClient _http;

    public ProviderConfig Config => _c;
    public string Where => UniFiProvider.CloudBase;
    public NetClass? NetworkClass => NetworkPolicy.Classify(UniFiProvider.CloudBase);
    /// <summary>The most entries read per list.</summary>
    public int ListCap { get; set; } = 5000;

    public UniFiCloudProvider(ProviderConfig c, HttpMessageHandler? handler = null)
    {
        _c = c;
        handler ??= TlsPin.Handler(() => "", (_, _) => { });   // a public service: Windows' certificate trust decides
        // 0.6.17 (ADR-096): the key is added when each request is sent (it may be kept in Bitwarden or Password Safe).
        handler = new CredentialAuthHandler(() => _c.CredentialId, "UniFi Site Manager " + c.Name, (req, cred) =>
        {
            if (cred.Secret.Length > 0) req.Headers.TryAddWithoutValidation("X-API-Key", cred.Secret.Trim());
        }, handler);
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 32 * 1024 * 1024, BaseAddress = new Uri(UniFiProvider.CloudBase + "/") };
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    private async Task<JsonObject> Send(HttpMethod method, string path, CancellationToken ct, bool retried = false)
    {
        using var req = new HttpRequestMessage(method, path);
        using var r = await _http.SendAsync(req, ct);
        var text = await r.Content.ReadAsStringAsync(ct);
        int code = (int)r.StatusCode;
        if (code == 429 && !retried)
        {
            var wait = r.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2);
            await Task.Delay(wait > TimeSpan.FromSeconds(10) ? TimeSpan.FromSeconds(10) : wait, ct);
            return await Send(method, path, ct, true);
        }
        if (code == 401) throw new UniFiProvider.UniFiException(401, "the API key was rejected (create one at unifi.ui.com → Settings → API Keys)");
        if (code == 403) throw new UniFiProvider.UniFiException(403, "this account may not read that (owner or super admin needed)");
        if (code == 429) throw new UniFiProvider.UniFiException(429, "Site Manager is rate-limiting this key: try again in a minute");
        if (code >= 300) throw new UniFiProvider.UniFiException(code, $"HTTP {code}: {UniFiProvider.Message(text)}");
        try { return UniFiProvider.ParseJson(text) as JsonObject ?? new JsonObject(); }
        catch (System.Text.Json.JsonException) { throw new UniFiProvider.UniFiException(code, "Site Manager didn't answer with JSON"); }
    }

    /// <summary>Every page (pageSize / nextToken) up to the cap.</summary>
    internal async Task<(List<JsonObject> Items, bool Truncated)> All(string path, CancellationToken ct)
    {
        var items = new List<JsonObject>();
        string? token = null;
        for (int pages = 0; pages < 200; pages++)
        {
            var url = path + (path.Contains('?') ? "&" : "?") + "pageSize=500" + (token != null ? "&nextToken=" + Uri.EscapeDataString(token) : "");
            var j = await Send(HttpMethod.Get, url, ct);
            items.AddRange((j["data"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>());
            token = j["nextToken"]?.ToString();
            if (string.IsNullOrEmpty(token)) return (items, false);
            if (items.Count >= ListCap) return (items.Take(ListCap).ToList(), true);
        }
        return (items, true);
    }

    private static string S(JsonNode? n, params string[] path)
    {
        foreach (var p in path) n = n is JsonObject o ? o[p] : null;
        return n is JsonValue v ? v.ToString() : "";
    }

    private static Dictionary<string, string> Flat(JsonObject o, string prefix = "", Dictionary<string, string>? into = null, int depth = 0)
    {
        into ??= new();
        foreach (var (k, v) in o)
        {
            var key = prefix + k;
            if (v is JsonObject child && depth < 3) Flat(child, key + ".", into, depth + 1);
            else if (v is JsonArray arr) into[key] = $"[{arr.Count}]";
            else into[key] = v?.ToString() ?? "";
        }
        return into;
    }

    public async Task<ProviderCaps> DetectAsync(CancellationToken ct)
    {
        try
        {
            if (!ExternalCredentials.HasSecret(AppData.Current.FindCredential(_c.CredentialId))) return ProviderCaps.Unreachable("choose the vault credential that holds the Site Manager API key");
            var hosts = await Send(HttpMethod.Get, "v1/hosts?pageSize=1", ct);
            return new ProviderCaps(true, true, false, "v1", "UniFi Site Manager (api.ui.com)", new[]
            {
                "Read-only: a fleet view of every console, site and device this account owns or administers.",
                "To manage a console, add it as a UniFi Network provider (right-click the console → “Add as a UniFi Network provider”): it then goes through the Cloud Connector with this key.",
            });
        }
        catch (Exception ex) when (ex is UniFiProvider.UniFiException or HttpRequestException or TaskCanceledException or OfflineBlockedException or InvalidOperationException)
        { return ProviderCaps.Unreachable(ex.Message); }
    }

    public async Task<IReadOnlyList<InfraObject>> InventoryAsync(CancellationToken ct)
    {
        var list = new List<InfraObject>();
        var notes = new List<string>();
        var hostNames = new Dictionary<string, string>();
        var (hosts, hcut) = await All("v1/hosts", ct);
        if (hcut) notes.Add($"more than {ListCap} consoles");
        foreach (var h in hosts)
        {
            var id = S(h, "id");
            var name = S(h, "reportedState", "hostname") is { Length: > 0 } hn ? hn : S(h, "reportedState", "name") is { Length: > 0 } n2 ? n2 : id;
            hostNames[id] = name;
            var state = S(h, "reportedState", "state");
            var status = S(h, "isBlocked") == "true" ? "blocked" : state is "" or "connected" ? "online" : state.ToLowerInvariant();
            list.Add(new InfraObject("host:" + id, "console", name, status, "", $"{S(h, "type")} · {S(h, "ipAddress")} · version {S(h, "reportedState", "version")}" + (S(h, "isOwner") == "false" ? " · not owner" : ""), Flat(h)));
        }
        try
        {
            var (sites, _) = await All("v1/sites", ct);
            foreach (var s in sites)
            {
                var c = UniFiProvider.At(s, "statistics", "counts");
                int.TryParse(S(c, "offlineDevice"), out var off);
                var host = hostNames.GetValueOrDefault(S(s, "hostId"), S(s, "hostId"));
                list.Add(new InfraObject("site:" + S(s, "siteId"), "site", S(s, "meta", "desc") is { Length: > 0 } d ? d : S(s, "meta", "name"), off > 0 ? "degraded" : "online", host,
                    $"{S(c, "totalDevice")} device(s), {S(c, "offlineDevice")} offline · {S(c, "wifiClient")} WiFi / {S(c, "wiredClient")} wired client(s)" + (S(c, "guestClient") is { Length: > 0 } g ? $" · {g} guest(s)" : ""), Flat(s)));
            }
        }
        catch (UniFiProvider.UniFiException ex) { notes.Add("sites: " + ex.Message); }
        try
        {
            var (groups, dcut) = await All("v1/devices", ct);
            if (dcut) notes.Add($"more than {ListCap} device groups");
            foreach (var g in groups)
                foreach (var d in (g["devices"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
                {
                    var upd = S(d, "updateAvailable") is { Length: > 0 } u && u != "null" ? " (update " + u + ")" : S(d, "firmwareStatus") == "updateAvailable" ? " (update available)" : "";
                    list.Add(new InfraObject("device:" + S(d, "id"), "device", S(d, "name").Length > 0 ? S(d, "name") : S(d, "mac"), S(d, "status").ToLowerInvariant() is { Length: > 0 } st ? st : "unknown",
                        S(g, "hostName").Length > 0 ? S(g, "hostName") : hostNames.GetValueOrDefault(S(g, "hostId"), ""),
                        $"{S(d, "model")} · {S(d, "ip")} · {S(d, "productLine")} · {S(d, "version")}{upd}", Flat(d)));
                }
        }
        catch (UniFiProvider.UniFiException ex) { notes.Add("devices: " + ex.Message); }
        try
        {
            var j = await Send(HttpMethod.Get, "v1/isp-metrics/5m?duration=24h", ct);
            foreach (var m in (j["data"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
            {
                var last = (m["periods"] as JsonArray)?.OfType<JsonObject>().LastOrDefault();
                var wan = UniFiProvider.At(last, "data", "wan") as JsonObject;
                if (wan == null) continue;
                var site = list.FirstOrDefault(o => o.Kind == "site" && o.Id == "site:" + S(m, "siteId"))?.Name ?? S(m, "siteId");
                string F(string k) => S(wan, k);
                list.Add(new InfraObject($"isp:{S(m, "hostId")}:{S(m, "siteId")}", "isp", $"{site} internet", (F("packetLoss") is { Length: > 0 } pl && double.TryParse(pl, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var loss) && loss > 2) ? "degraded" : "online",
                    hostNames.GetValueOrDefault(S(m, "hostId"), ""),
                    $"{F("ispName")} · latency {F("avgLatency")} ms (max {F("maxLatency")}) · loss {F("packetLoss")}% · ↓ {F("download_kbps")} / ↑ {F("upload_kbps")} kbps · {S(last, "metricTime")}", Flat(wan)));
            }
        }
        catch (UniFiProvider.UniFiException ex) when (ex.Status != 404) { notes.Add("ISP metrics: " + ex.Message); }
        catch (UniFiProvider.UniFiException) { }
        try
        {
            var j = await Send(HttpMethod.Get, "v1/sd-wan-configs", ct);
            foreach (var s in (j["data"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
                list.Add(new InfraObject("sdwan:" + S(s, "id"), "sd-wan", S(s, "name"), "active", "", S(s, "type"), Flat(s)));
        }
        catch (UniFiProvider.UniFiException) { }   // only on accounts with SD-WAN
        if (notes.Count > 0) list.Add(new InfraObject("notes", "note", "Not everything could be read", "offline", "", string.Join(" · ", notes), new Dictionary<string, string>()));
        return list;
    }

    public static readonly InfraAction AddConsole = new("add-console", "＋ Add as a UniFi Network provider (Cloud Connector)", false, Consequence.None, null,
        "Adds this console to the Infrastructure list as a UniFi Network provider reached through Ubiquiti's Cloud Connector with the same API key: devices, clients, WiFi, networks, firewall and vouchers, read-only until you allow changes.");
    public static readonly InfraAction Details = new("details", "ⓘ All fields", false, Consequence.None, null, "Every field Site Manager reported for this item.");

    public IReadOnlyList<InfraAction> ActionsFor(InfraObject o) => o.Kind == "console" ? new[] { Details, AddConsole } : new[] { Details };
    public string? Forbidden(InfraAction a, InfraObject o, ProviderCaps caps) => null;
    public Task<ActionResult> RunAsync(InfraAction a, InfraObject o, CancellationToken ct) => RunAsync(a, o, new Dictionary<string, string>(), ct);

    public Task<ActionResult> RunAsync(InfraAction a, InfraObject o, IReadOnlyDictionary<string, string> input, CancellationToken ct)
    {
        if (a.Id == "details") return Task.FromResult(new ActionResult(true, true, "fields", string.Join("\n", o.Props.OrderBy(p => p.Key).Select(p => $"{p.Key,-34} {p.Value}"))));
        if (a.Id != "add-console") return Task.FromResult(new ActionResult(false, false, "refused: unknown action"));
        var id = o.Id.StartsWith("host:", StringComparison.Ordinal) ? o.Id[5..] : "";
        if (UniFiProvider.ValidateConsoleId(id) != null) return Task.FromResult(new ActionResult(false, false, "refused: unexpected console id"));
        if (AppData.Current.InfraProviders.FirstOrDefault(p => p.Kind == "unifi" && p.ConsoleId == id) is { } existing)
            return Task.FromResult(new ActionResult(true, true, $"already in the list as “{existing.Name}”"));
        var c = new ProviderConfig { Kind = "unifi", Name = o.Name, Endpoint = UniFiProvider.CloudBase, ConsoleId = id, CredentialId = _c.CredentialId, Environment = _c.Environment };
        AppData.Current.InfraProviders.Add(c);
        AppData.Save();
        AuditLog.Write("you", "infra:" + c.Name, "provider added", "UniFi Network through the Cloud Connector");
        return Task.FromResult(new ActionResult(true, true, $"added “{c.Name}” as a UniFi Network provider (Cloud Connector); select it in the list"));
    }
}
