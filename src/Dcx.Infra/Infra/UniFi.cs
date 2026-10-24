using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DcxManager.Services;

namespace DcxManager.Infra;

/// <summary>
/// UniFi Network through Ubiquiti's official Network Integration API (0.6.16, ADR-094; DarkCipher0x: "integrate unifi
/// management with their API key … a very mature API integration"). Network application 9.0 or later.
/// <list type="bullet">
/// <item><b>Two ways in, one API:</b> the console's own address (<c>https://&lt;console&gt;/proxy/network/integration/v1</c>; a
/// self-hosted Network server answers without the <c>/proxy/network</c> prefix, and the path is discovered) or Ubiquiti's
/// Cloud Connector (<c>https://api.ui.com/v1/connector/consoles/{id}/…</c>, no LAN access needed). The path after
/// <c>/integration</c> is the same.</item>
/// <item><b>The API key</b> (UniFi Network → Settings → Control Plane → Integrations; for the Cloud Connector, unifi.ui.com →
/// API Keys) is a vault credential and goes only in the <c>X-API-KEY</c> header, never in a URL. https only; a console's
/// self-signed certificate is pinned on first use (ADR-007) and a changed one is refused. No redirects are followed.</item>
/// <item><b>Reads:</b> application version, sites, adopted devices with their details (ports, PoE, radios, uplink) and
/// latest statistics (uptime, CPU, memory, load, uplink rates), devices waiting for adoption, connected clients (guest
/// access), networks / VLANs, WiFi broadcasts, WAN interfaces, VPN servers, site-to-site tunnels, firewall zones and
/// policies, ACL rules, DNS policies, traffic matching lists, hotspot vouchers, LAGs, MC-LAG domains and switch stacks.
/// Lists are paged (offset / limit) up to a cap, and the cap is said, never silently cut.</item>
/// <item><b>Changes</b> (only with "Allow changes"; every one re-read to verify): restart a device (verified by its uptime
/// starting again), PoE power-cycle a port, adopt a waiting device, remove (unadopt) a device, authorise / revoke a guest,
/// generate / delete vouchers, switch a WiFi broadcast, a network or a user-defined firewall policy on or off.</item>
/// <item>Every id from the API is checked to be a GUID before it is put in a path; values typed by the user travel only in
/// JSON bodies.</item>
/// </list>
/// </summary>
public sealed class UniFiProvider : IInfraProvider, ITlsPinned
{
    public const string CloudBase = "https://api.ui.com";
    private readonly ProviderConfig _c;
    private readonly HttpClient _http;
    private string? _base;   // the working API root, discovered (ends with /integration/v1)
    private string _version = "";

    public ProviderConfig Config => _c;
    public string Where => Remote ? $"{CloudBase} (Cloud Connector, console {Short(_c.ConsoleId)})" : _c.Endpoint;
    public NetClass? NetworkClass => NetworkPolicy.Classify(Remote ? CloudBase : _c.Endpoint);
    public string LastSeenSha256 { get; private set; } = "";
    public TlsDecision LastTls { get; private set; } = TlsDecision.Accept;
    public TimeSpan VerifyFor { get; set; } = TimeSpan.FromMinutes(6);
    public TimeSpan PollEvery { get; set; } = TimeSpan.FromSeconds(5);
    /// <summary>The most objects read per list (sites × devices, clients…); more is said in the notes.</summary>
    public int ListCap { get; set; } = 2000;
    /// <summary>Devices whose details and statistics are read one by one (a big site reads the rest from the list only).</summary>
    public int DetailCap { get; set; } = 150;

    private bool Remote => _c.ConsoleId.Trim().Length > 0;

    public UniFiProvider(ProviderConfig c, HttpMessageHandler? handler = null)
    {
        _c = c;
        // The Cloud Connector is a public service with a public certificate: Windows' trust decides, nothing is pinned.
        handler ??= TlsPin.Handler(() => Remote ? "" : _c.TlsSha256, (sha, d) => { LastSeenSha256 = sha; LastTls = d; });
        // 0.6.17 (ADR-096): the key is added when each request is sent (it may be kept in Bitwarden or Password Safe).
        handler = new CredentialAuthHandler(() => _c.CredentialId, "UniFi " + c.Name, (req, cred) =>
        {
            if (cred.Secret.Length > 0) req.Headers.TryAddWithoutValidation("X-API-KEY", cred.Secret.Trim());
        }, handler);
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 32 * 1024 * 1024 };
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    private static string Short(string s) => s.Length > 12 ? s[..12] + "…" : s;

    /// <summary>https only (the API key would otherwise travel in clear text); an address and port, no path.</summary>
    public static string? ValidateEndpoint(string url) =>
        !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) ? "Enter the console's address, e.g. https://192.168.1.1 (or https://unifi.lan:8443 for a self-hosted Network server)"
        : u.Scheme != Uri.UriSchemeHttps ? "UniFi must be reached over https: the API key would otherwise travel in clear text."
        : u.UserInfo.Length > 0 || u.Query.Length > 0 ? "Only the address and port, e.g. https://192.168.1.1"
        : u.AbsolutePath.Trim('/').Length > 0 ? "Only the address and port (no path), e.g. https://192.168.1.1"
        : null;

    /// <summary>A console id as unifi.ui.com shows it (letters, digits, ':' '-' '_').</summary>
    public static string? ValidateConsoleId(string id) =>
        Regex.IsMatch(id.Trim(), @"^[A-Za-z0-9:_-]{8,160}\z") ? null : "Enter the console's id from unifi.ui.com (Site Manager → the console → its id), letters, digits and ':' only.";

    private static readonly Regex Uuid = new(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\z", RegexOptions.CultureInvariant);
    public static bool IsId(string? s) => s != null && Uuid.IsMatch(s);

    // ---- HTTP -----------------------------------------------------------------------------------------------------------

    internal sealed class UniFiException(int status, string message) : Exception(message) { public int Status { get; } = status; }

    /// <summary>The candidate API roots, most likely first: the console proxy, a self-hosted server, the Cloud Connector forms.</summary>
    internal IEnumerable<string> Roots()
    {
        if (Remote)
        {
            var id = Uri.EscapeDataString(_c.ConsoleId.Trim());
            yield return $"{CloudBase}/v1/connector/consoles/{id}/proxy/network/integration/v1";
            yield return $"{CloudBase}/v1/connector/consoles/{id}/network/integration/v1";
        }
        else
        {
            var e = _c.Endpoint.Trim().TrimEnd('/');
            yield return e + "/proxy/network/integration/v1";
            yield return e + "/integration/v1";
            yield return e + "/proxy/network/integrations/v1";
        }
    }

    private async Task<string> Root(CancellationToken ct)
    {
        if (_base != null) return _base;
        UniFiException? last = null;
        foreach (var root in Roots())
        {
            try
            {
                var info = await Send(HttpMethod.Get, root + "/info", null, ct) as JsonObject;
                _version = info?["applicationVersion"]?.ToString() ?? "";
                return _base = root;
            }
            catch (UniFiException ex) when (ex.Status is 404 or 405) { last = ex; }
        }
        throw last is { } l ? new UniFiException(404, Remote
            ? "the Cloud Connector didn't find the Network application on this console (check the console id, that the console runs UniFi OS 4.x or later, and that the key belongs to its owner)"
            : "no Network Integration API at this address (UniFi Network 9.0 or later is needed; check the address and port)")
            : new UniFiException(0, "the console didn't answer");
    }

    /// <summary>One request. Errors come back as the API's own message; 401 / 403 / 429 are explained.</summary>
    private async Task<JsonNode?> Send(HttpMethod method, string url, JsonNode? body, CancellationToken ct, bool retried = false)
    {
        using var req = new HttpRequestMessage(method, url);
        if (body != null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var r = await _http.SendAsync(req, ct);
        var text = await r.Content.ReadAsStringAsync(ct);
        int code = (int)r.StatusCode;
        if (code == 429 && !retried)
        {
            var wait = r.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2);
            await Task.Delay(wait > TimeSpan.FromSeconds(10) ? TimeSpan.FromSeconds(10) : wait, ct);
            return await Send(method, url, body, ct, retried: true);
        }
        if (code is >= 300 and < 400) throw new UniFiException(code, "the console redirected (e.g. to a sign-in page); use its https address directly");
        if (code == 401) throw new UniFiException(401, "the API key was rejected (create one under UniFi Network → Settings → Control Plane → Integrations)");
        if (code == 403) throw new UniFiException(403, "the API key isn't allowed to do this (its owner's role decides; the Cloud Connector needs the console owner's key)");
        if (code == 429) throw new UniFiException(429, "UniFi is rate-limiting this key: try again in a minute");
        if (code >= 400) throw new UniFiException(code, $"HTTP {code}: {Message(text)}");
        if (text.Trim().Length == 0) return null;
        try { return ParseJson(text); }
        catch (System.Text.Json.JsonException) { throw new UniFiException(code, "the console didn't answer with JSON (is this a UniFi Network application?)"); }
    }

    /// <summary>An answer parsed strictly (0.6.16 sweep: the fuzzer found that a duplicate key, e.g. two "message" fields,
    /// throws ArgumentException later, when the object is read; refused at parse time it is just invalid JSON).</summary>
    internal static JsonNode? ParseJson(string text) => JsonNode.Parse(text, null, new System.Text.Json.JsonDocumentOptions { AllowDuplicateProperties = false, MaxDepth = 64 });

    /// <summary>The API's error message (its JSON "message" / "statusName"), masked and short.</summary>
    internal static string Message(string text)
    {
        string msg = "";
        try { var j = ParseJson(text) as JsonObject; msg = j?["message"]?.ToString() ?? j?["statusName"]?.ToString() ?? j?["error"]?.ToString() ?? ""; }
        catch (System.Text.Json.JsonException) { msg = Regex.Replace(text, "<[^>]*>", " "); }
        msg = Redaction.Mask(Regex.Replace(msg, @"\s+", " ").Trim());
        return msg.Length > 240 ? msg[..240] + "…" : msg.Length > 0 ? msg : "(no message)";
    }

    private async Task<JsonNode?> Get(string path, CancellationToken ct) => await Send(HttpMethod.Get, await Root(ct) + path, null, ct);

    /// <summary>Every page of a list (offset / limit), up to <paramref name="cap"/>; Truncated says when there was more.</summary>
    internal async Task<(List<JsonObject> Items, bool Truncated)> All(string path, CancellationToken ct, int? cap = null)
    {
        int max = cap ?? ListCap, offset = 0;
        var items = new List<JsonObject>();
        while (true)
        {
            var sep = path.Contains('?') ? '&' : '?';
            var page = await Get($"{path}{sep}offset={offset}&limit=100", ct) as JsonObject;
            var data = (page?["data"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new();
            items.AddRange(data);
            long total = page?["totalCount"] is JsonValue tv && tv.TryGetValue<long>(out var t) ? t : items.Count;   // untrusted: a string or object counts as unknown
            offset += data.Count;
            if (data.Count == 0 || offset >= total) return (items, false);
            if (items.Count >= max) return (items.Take(max).ToList(), true);
        }
    }

    /// <summary>Safe navigation through objects only: a field that is an array or a value where an object was expected gives
    /// null instead of throwing (the API's shapes differ between list and detail answers and between versions).</summary>
    internal static JsonNode? At(JsonNode? n, params string[] path)
    {
        foreach (var p in path) n = n is JsonObject o ? o[p] : null;
        return n;
    }

    private static string S(JsonNode? n, string key) => n is JsonObject o && o[key] is JsonValue v ? v.ToString() : "";
    private static string S(JsonNode? n, string a, string b) => S(n is JsonObject o ? o[a] : null, b);

    private static Dictionary<string, string> Flat(JsonObject o, string prefix = "", Dictionary<string, string>? into = null, int depth = 0)
    {
        into ??= new();
        foreach (var (k, v) in o)
        {
            var key = prefix + k;
            if (v is JsonObject child && depth < 3) Flat(child, key + ".", into, depth + 1);
            else if (v is JsonArray arr) into[key] = arr.Count <= 8 && arr.All(x => x is JsonValue) ? string.Join(", ", arr.Select(x => x?.ToString())) : $"[{arr.Count}]";
            else into[key] = v?.ToString() ?? "";
        }
        return into;
    }

    // ---- detect and inventory -------------------------------------------------------------------------------------------

    public async Task<ProviderCaps> DetectAsync(CancellationToken ct)
    {
        try
        {
            if (!Remote && ValidateEndpoint(_c.Endpoint) is { } bad) return ProviderCaps.Unreachable(bad);
            if (Remote && ValidateConsoleId(_c.ConsoleId) is { } badId) return ProviderCaps.Unreachable(badId);
            if (!ExternalCredentials.HasSecret(AppData.Current.FindCredential(_c.CredentialId))) return ProviderCaps.Unreachable("choose the vault credential that holds the API key");
            _base = null;
            await Root(ct);
            var sites = await All("/sites", ct, 200);
            var notes = new List<string>
            {
                Remote ? "Reached through Ubiquiti's Cloud Connector (api.ui.com): no access to the console's network needed." : "Reached directly on the console's address.",
                "The API key carries its owner's role: a read-only admin's key can look but not change. Changes here still need “Allow changes”.",
            };
            if (Version.TryParse(Regex.Match(_version, @"^\d+(\.\d+){1,3}").Value, out var v) && v.Major < 9) notes.Add($"Network {_version} is older than 9.0: the Integration API may be incomplete.");
            return new ProviderCaps(true, true, true, _version, $"UniFi Network {(_version.Length > 0 ? _version : "(version not reported)")} · {sites.Items.Count} site(s)", notes);
        }
        catch (Exception ex) when (ex is UniFiException or HttpRequestException or TaskCanceledException or OfflineBlockedException or InvalidOperationException)
        {
            var tls = LastTls != TlsDecision.Accept && LastSeenSha256.Length > 0 ? TlsPin.UntrustedMessage(LastTls, LastSeenSha256) : ex.Message;
            return ProviderCaps.Unreachable(tls);
        }
    }

    public async Task<IReadOnlyList<InfraObject>> InventoryAsync(CancellationToken ct)
    {
        var list = new List<InfraObject>();
        var notes = new List<string>();
        async Task Try(string what, Func<Task> read)
        {
            try { await read(); }
            catch (UniFiException ex) when (ex.Status is 404 or 405) { }   // this Network version doesn't have it
            catch (Exception ex) when (ex is UniFiException or HttpRequestException or System.Text.Json.JsonException) { notes.Add($"{what}: {ex.Message}"); }
        }
        await Root(ct);
        list.Add(new InfraObject("app", "console", Remote ? "Console (Cloud Connector)" : _c.Endpoint, "online", "", $"UniFi Network {_version}", new Dictionary<string, string> { ["applicationVersion"] = _version, ["mode"] = Remote ? "cloud connector" : "local" }));
        var (sites, _) = await All("/sites", ct, 200);
        var deviceNames = new Dictionary<string, string>();
        foreach (var site in sites)
        {
            var sid = S(site, "id");
            if (!IsId(sid)) continue;
            var sname = S(site, "name").Length > 0 ? S(site, "name") : S(site, "internalReference");
            var p = new Dictionary<string, string> { ["siteId"] = sid };
            int devCount = 0, offline = 0, clientCount = 0;

            await Try($"{sname} devices", async () =>
            {
                var (devs, cut) = await All($"/sites/{sid}/devices", ct);
                if (cut) notes.Add($"{sname}: more than {ListCap} devices, the first {ListCap} are listed");
                devCount = devs.Count;
                foreach (var d in devs) deviceNames[S(d, "id")] = S(d, "name");
                using var gate = new SemaphoreSlim(6);
                var rows = await Task.WhenAll(devs.Select(async (d, i) =>
                {
                    var did = S(d, "id");
                    JsonObject? det = null, st = null;
                    if (IsId(did) && i < DetailCap)
                    {
                        await gate.WaitAsync(ct);
                        try
                        {
                            try { det = await Get($"/sites/{sid}/devices/{did}", ct) as JsonObject; } catch (UniFiException) { }
                            if (S(d, "state") == "ONLINE") try { st = await Get($"/sites/{sid}/devices/{did}/statistics/latest", ct) as JsonObject; } catch (UniFiException) { }
                        }
                        finally { gate.Release(); }
                    }
                    return Device(sid, sname, d, det, st);
                }));
                offline = rows.Count(r => r.Status != "online");
                list.AddRange(rows);
            });
            // Uplinks by name once every device is known.
            for (int i = 0; i < list.Count; i++)
                if (list[i].Kind == "device" && list[i].Props.GetValueOrDefault("uplink.deviceId") is { } up && deviceNames.TryGetValue(up, out var upName))
                    list[i] = list[i] with { Detail = list[i].Detail + $" · uplink {upName}" };
            await Try($"{sname} clients", async () =>
            {
                var (cl, cut) = await All($"/sites/{sid}/clients", ct);
                if (cut) notes.Add($"{sname}: more than {ListCap} clients, the first {ListCap} are listed");
                clientCount = cl.Count;
                foreach (var c in cl) list.Add(Client(sid, sname, c, deviceNames));
            });
            await Try($"{sname} networks", async () =>
            {
                foreach (var n in (await All($"/sites/{sid}/networks", ct)).Items)
                    list.Add(Obj("network", sid, sname, n, S(n, "enabled") == "true" ? "active" : "disabled",
                        $"VLAN {S(n, "vlanId")} · {S(n, "management").ToLowerInvariant()}{(S(n, "default") == "true" ? " · default" : "")} · {S(n, "metadata", "origin").ToLowerInvariant().Replace('_', ' ')}"));
            });
            await Try($"{sname} WiFi", async () =>
            {
                foreach (var w in (await All($"/sites/{sid}/wifi/broadcasts", ct)).Items)
                    list.Add(Obj("wifi", sid, sname, w, S(w, "enabled") == "true" ? "active" : "disabled",
                        $"{S(w, "securityConfiguration", "type").Replace('_', ' ')} · {S(w, "type").Replace('_', ' ').ToLowerInvariant()}"));
            });
            await Try($"{sname} WANs", async () =>
            {
                foreach (var w in (await All($"/sites/{sid}/wans", ct)).Items) list.Add(Obj("wan", sid, sname, w, "active", Summary(w, "id", "name")));
            });
            await Try($"{sname} VPN", async () =>
            {
                foreach (var v in (await All($"/sites/{sid}/vpn/servers", ct)).Items)
                    list.Add(Obj("vpn server", sid, sname, v, S(v, "enabled") == "true" ? "active" : "disabled", S(v, "type")));
                foreach (var v in (await All($"/sites/{sid}/vpn/site-to-site-tunnels", ct)).Items)
                    list.Add(Obj("site-to-site", sid, sname, v, S(v, "enabled") is "false" ? "disabled" : "active", Summary(v, "id", "name", "metadata")));
            });
            await Try($"{sname} firewall", async () =>
            {
                foreach (var z in (await All($"/sites/{sid}/firewall/zones", ct)).Items) list.Add(Obj("firewall zone", sid, sname, z, "active", S(z, "metadata", "origin").ToLowerInvariant().Replace('_', ' ')));
                foreach (var f in (await All($"/sites/{sid}/firewall/policies", ct)).Items)
                    list.Add(Obj("firewall policy", sid, sname, f, S(f, "enabled") == "false" ? "disabled" : "active",
                        $"{S(f, "action", "type")} · {S(f, "metadata", "origin").ToLowerInvariant().Replace('_', ' ')}"));
            });
            await Try($"{sname} ACL rules", async () =>
            {
                foreach (var a in (await All($"/sites/{sid}/acl-rules", ct)).Items) list.Add(Obj("acl rule", sid, sname, a, S(a, "enabled") == "false" ? "disabled" : "active", S(a, "action")));
            });
            await Try($"{sname} DNS policies", async () =>
            {
                foreach (var d in (await All($"/sites/{sid}/dns/policies", ct)).Items) list.Add(Obj("dns policy", sid, sname, d, S(d, "enabled") == "false" ? "disabled" : "active", S(d, "type")));
            });
            await Try($"{sname} traffic lists", async () =>
            {
                foreach (var t in (await All($"/sites/{sid}/traffic-matching-lists", ct)).Items) list.Add(Obj("traffic list", sid, sname, t, "active", S(t, "type")));
            });
            await Try($"{sname} vouchers", async () =>
            {
                foreach (var v in (await All($"/sites/{sid}/hotspot/vouchers", ct, 1000)).Items)
                {
                    var status = S(v, "expired") == "true" ? "expired" : S(v, "activatedAt").Length > 0 ? "active" : "available";
                    list.Add(new InfraObject("voucher:" + sid + ":" + S(v, "id"), "voucher", $"{S(v, "name")} ({S(v, "code")})", status, sname,
                        $"{S(v, "timeLimitMinutes")} min · guests {S(v, "authorizedGuestCount")}/{(S(v, "authorizedGuestLimit") is { Length: > 0 } g ? g : "∞")}" + (S(v, "expiresAt") is { Length: > 0 } e ? " · expires " + e : ""),
                        WithSite(Flat(v), sid)));
                }
            });
            await Try($"{sname} switching", async () =>
            {
                foreach (var (path, kind) in new[] { ("switching/lags", "lag"), ("switching/mc-lag-domains", "mc-lag"), ("switching/switch-stacks", "switch stack") })
                    foreach (var x in (await All($"/sites/{sid}/{path}", ct)).Items) list.Add(Obj(kind, sid, sname, x, "active", Summary(x, "id")));
            });
            list.Add(new InfraObject("site:" + sid, "site", sname, offline > 0 ? "degraded" : "online", "", $"{devCount} device(s), {offline} offline · {clientCount} client(s)", WithSite(Flat(site), sid)));
        }
        await Try("pending devices", async () =>
        {
            foreach (var d in (await All("/pending-devices", ct)).Items)
                list.Add(new InfraObject("pending:" + S(d, "macAddress"), "pending", $"{S(d, "model")} {S(d, "macAddress")}", "pending", "waiting for adoption",
                    $"{S(d, "ipAddress")} · firmware {S(d, "firmwareVersion")}", Flat(d)));
        });
        if (notes.Count > 0) list.Add(new InfraObject("notes", "note", "Not everything could be read", "offline", "", string.Join(" · ", notes.Take(6)), new Dictionary<string, string>()));
        return list;
    }

    private static Dictionary<string, string> WithSite(Dictionary<string, string> p, string sid) { p["siteId"] = sid; return p; }

    private static InfraObject Obj(string kind, string sid, string sname, JsonObject o, string status, string detail) =>
        new($"{kind.Replace(' ', '-')}:{sid}:{S(o, "id")}", kind, S(o, "name").Length > 0 ? S(o, "name") : S(o, "id"), status, sname, detail, WithSite(Flat(o), sid));

    private static string Summary(JsonObject o, params string[] skip) =>
        string.Join(" · ", o.Where(kv => !skip.Contains(kv.Key) && kv.Value is JsonValue).Take(4).Select(kv => $"{kv.Key} {kv.Value}"));

    private static InfraObject Device(string sid, string sname, JsonObject d, JsonObject? det, JsonObject? st)
    {
        var p = WithSite(Flat(d), sid);
        if (det != null) foreach (var (k, v) in Flat(det)) p.TryAdd(k, v);
        if (st != null) foreach (var (k, v) in Flat(st)) p["stats." + k] = v;
        var ports = At(det, "interfaces", "ports") as JsonArray;   // an object in the details, an array of names in the list
        if (ports != null)
        {
            p["ports.up"] = ports.Count(x => S(x, "state") == "UP").ToString();
            p["ports.count"] = ports.Count.ToString();
            p["ports.poe"] = string.Join(",", ports.OfType<JsonObject>().Where(x => x["poe"] is JsonObject poe && S(poe, "enabled") == "true").Select(x => S(x, "idx")));
        }
        var state = S(d, "state");
        var status = state switch { "ONLINE" => "online", "OFFLINE" or "CONNECTION_INTERRUPTED" or "ISOLATED" => "offline", "UPDATING" => "updating", _ => state.ToLowerInvariant().Replace('_', ' ') };
        var bits = new List<string> { S(d, "model"), S(d, "ipAddress") };
        if (S(d, "firmwareVersion") is { Length: > 0 } fw) bits.Add("firmware " + fw + (S(d, "firmwareUpdatable") == "true" ? " (update available)" : ""));
        if (st?["uptimeSec"] is JsonValue up && up.TryGetValue<long>(out var sec)) bits.Add("up " + Uptime(sec));
        if (st?["cpuUtilizationPct"] is JsonValue cpu) bits.Add($"CPU {cpu}%");
        if (st?["memoryUtilizationPct"] is JsonValue mem) bits.Add($"RAM {mem}%");
        if (ports != null) bits.Add($"{p["ports.up"]}/{p["ports.count"]} ports up");
        var feats = (d["features"] as JsonArray)?.Select(f => f?.ToString()).Where(f => f != null).ToList() ?? new();
        return new InfraObject($"device:{sid}:{S(d, "id")}", "device", S(d, "name").Length > 0 ? S(d, "name") : S(d, "macAddress"), status, sname,
            string.Join(" · ", bits.Where(b => b.Length > 0)) + (feats.Count > 0 ? $" · {string.Join("/", feats)}" : ""), p);
    }

    private static string Uptime(long sec) => sec >= 86400 ? $"{sec / 86400} d {sec % 86400 / 3600} h" : sec >= 3600 ? $"{sec / 3600} h {sec % 3600 / 60} min" : $"{sec / 60} min";

    private static InfraObject Client(string sid, string sname, JsonObject c, Dictionary<string, string> devices)
    {
        var p = WithSite(Flat(c), sid);
        var type = S(c, "type");
        var guest = S(c, "access", "type").Equals("GUEST", StringComparison.OrdinalIgnoreCase);
        var authorized = S(c, "access", "authorized");
        var status = guest ? (authorized == "true" ? "authorized" : "unauthorized") : "connected";
        var uplink = S(c, "uplinkDeviceId") is { Length: > 0 } u && devices.TryGetValue(u, out var un) ? " · via " + un : "";
        return new InfraObject($"client:{sid}:{S(c, "id")}", "client", S(c, "name").Length > 0 ? S(c, "name") : S(c, "macAddress"), status, sname,
            $"{type.ToLowerInvariant()} · {S(c, "ipAddress")}{(guest ? " · guest" : "")}{uplink}" + (S(c, "connectedAt") is { Length: > 0 } at ? " · since " + at : ""), p);
    }

    // ---- actions --------------------------------------------------------------------------------------------------------

    public static readonly InfraAction Restart = new("restart", "↻ Restart device", true, Consequence.ServiceInterruption, null,
        "Restarts the device: its clients and everything downstream lose the network until it is back (a minute or two). Verified when it is online again with its uptime started over.");
    public static readonly InfraAction PowerCycle = new("poe-cycle", "⚡ PoE power-cycle a port…", true, Consequence.ServiceInterruption, null,
        "Cuts PoE power on one port and restores it: the powered device (camera, access point, phone) restarts.")
    { Inputs = new[] { new InfraInput("port", "Port number", Hint: "a port with PoE on", Number: true) } };
    public static readonly InfraAction Unadopt = new("unadopt", "✕ Remove (unadopt) device…", true, Consequence.DataLoss, null,
        "Removes the device from this site: it forgets its configuration and must be adopted again.");
    public static readonly InfraAction Adopt = new("adopt", "＋ Adopt into a site…", true, Consequence.None, null, "Adopts the waiting device into the chosen site.")
    { Inputs = new[] { new InfraInput("site", "Site") } };
    public static readonly InfraAction Authorize = new("guest-on", "✓ Authorise guest access…", true, Consequence.None, "authorized",
        "Lets this guest client through the hotspot portal.")
    { Inputs = new[] { new InfraInput("minutes", "Time limit in minutes (empty = the portal's default)", Required: false, Number: true) } };
    public static readonly InfraAction Unauthorize = new("guest-off", "⊘ Revoke guest access", true, Consequence.ServiceInterruption, "unauthorized",
        "Ends this guest's access: it is sent back to the hotspot portal.");
    public static readonly InfraAction Vouchers = new("vouchers", "🎟 Generate guest vouchers…", true, Consequence.None, null, "Creates hotspot vouchers; the codes are shown once done.")
    {
        Inputs = new[]
        {
            new InfraInput("name", "Name (note on the vouchers)", "Guests {now}"),
            new InfraInput("count", "How many", "1", Number: true),
            new InfraInput("minutes", "Time limit (minutes)", "1440", Number: true),
            new InfraInput("guests", "Guests per voucher (empty = no limit)", "1", Required: false, Number: true),
            new InfraInput("dataMb", "Data limit in MB (empty = none)", Required: false, Number: true),
            new InfraInput("downKbps", "Download limit kbps (empty = none)", Required: false, Number: true),
            new InfraInput("upKbps", "Upload limit kbps (empty = none)", Required: false, Number: true),
        },
    };
    public static readonly InfraAction DeleteVoucher = new("voucher-delete", "✕ Delete voucher", true, Consequence.ServiceInterruption, null, "Deletes the voucher; guests using it lose access.");
    public static readonly InfraAction Enable = new("enable", "▶ Turn on", true, Consequence.None, "active", "Turns it on.");
    public static readonly InfraAction Disable = new("disable", "■ Turn off…", true, Consequence.ServiceInterruption, "disabled", "Turns it off: whoever depends on it loses that part of the network.");
    public static readonly InfraAction Details = new("details", "ⓘ All fields", false, Consequence.None, null, "Every field UniFi reported for this item.");

    public IReadOnlyList<InfraAction> ActionsFor(InfraObject o) => o.Kind switch
    {
        "device" => new[] { Details, Restart, PowerCycle, Unadopt },
        "pending" => new[] { Details, Adopt },
        "client" => new[] { Details, Authorize, Unauthorize },
        "site" => new[] { Details, Vouchers },
        "voucher" => new[] { Details, DeleteVoucher },
        "wifi" or "network" or "firewall policy" => new[] { Details, Enable, Disable },
        _ => new[] { Details },
    };

    public string? Forbidden(InfraAction a, InfraObject o, ProviderCaps caps) => a.Id switch
    {
        "restart" when o.Status != "online" => "the device is offline",
        "poe-cycle" when (o.Props.GetValueOrDefault("ports.poe") ?? "").Length == 0 => "no port with PoE on",
        "guest-on" or "guest-off" when o.Status is not ("authorized" or "unauthorized") => "not a guest-network client",
        "guest-on" when o.Status == "authorized" => "already authorised",
        "guest-off" when o.Status == "unauthorized" => "not authorised",
        "enable" when o.Status == "active" => "already on",
        "disable" when o.Status == "disabled" => "already off",
        "enable" or "disable" when o.Kind == "network" && o.Props.GetValueOrDefault("default") == "true" => "the default network can't be switched off",
        "enable" or "disable" when o.Props.GetValueOrDefault("metadata.origin") is "SYSTEM_DEFINED" or "DERIVED" or "ORCHESTRATED" => "managed by UniFi itself, not user-defined",
        _ => null,
    };

    public Task<ActionResult> RunAsync(InfraAction a, InfraObject o, CancellationToken ct) => RunAsync(a, o, new Dictionary<string, string>(), ct);

    /// <summary>"kind:siteId:itemId" → (siteId, itemId), both GUIDs, or null.</summary>
    internal static (string Site, string Item)? Ids(InfraObject o)
    {
        var parts = o.Id.Split(':');
        return parts.Length == 3 && IsId(parts[1]) && IsId(parts[2]) ? (parts[1], parts[2]) : null;
    }

    private static long? Num(IReadOnlyDictionary<string, string> input, string key, long min, long max) =>
        long.TryParse(input.GetValueOrDefault(key), out var v) && v >= min && v <= max ? v : null;

    public async Task<ActionResult> RunAsync(InfraAction a, InfraObject o, IReadOnlyDictionary<string, string> input, CancellationToken ct)
    {
        foreach (var i in a.Inputs) if (i.Validate(input.GetValueOrDefault(i.Name) ?? "") is { } bad) return new ActionResult(false, false, "refused: " + bad);
        if (a.Id == "details") return new ActionResult(true, true, "fields", string.Join("\n", o.Props.OrderBy(p => p.Key).Select(p => $"{p.Key,-34} {p.Value}")));
        try
        {
            if (a.Id == "adopt")
            {
                var mac = o.Props.GetValueOrDefault("macAddress") ?? "";
                if (!Regex.IsMatch(mac, @"^[0-9A-Fa-f]{2}([:-][0-9A-Fa-f]{2}){5}\z")) return new ActionResult(false, false, "refused: unexpected MAC address");
                var (sites, _) = await All("/sites", ct, 200);
                var want = input.GetValueOrDefault("site") ?? "";
                var site = sites.FirstOrDefault(s => S(s, "name").Equals(want, StringComparison.OrdinalIgnoreCase) || S(s, "id") == want);
                if (site == null || !IsId(S(site, "id"))) return new ActionResult(false, false, "refused: no such site (" + string.Join(", ", sites.Select(s => S(s, "name"))) + ")");
                var sid = S(site, "id");
                await Send(HttpMethod.Post, await Root(ct) + $"/sites/{sid}/devices", new JsonObject { ["macAddress"] = mac, ["ignoreDeviceLimit"] = false }, ct);
                return await Verify(async () => (await All($"/sites/{sid}/devices", ct)).Items.FirstOrDefault(d => S(d, "macAddress").Equals(mac, StringComparison.OrdinalIgnoreCase)) is { } d ? S(d, "state") : "", s => s is "ONLINE" or "ADOPTING" or "GETTING_READY" or "PROVISIONING", ct, "adopted into " + S(site, "name"));
            }
            if (a.Id == "vouchers")
            {
                var sid = o.Props.GetValueOrDefault("siteId") ?? "";
                if (!IsId(sid)) return new ActionResult(false, false, "refused: unexpected site id");
                var count = Num(input, "count", 1, 1000);
                var minutes = Num(input, "minutes", 1, 1_000_000);
                if (count == null || minutes == null) return new ActionResult(false, false, "refused: 1–1000 vouchers of 1 minute or more");
                var name = (input.GetValueOrDefault("name") ?? "").Trim();
                var body = new JsonObject { ["name"] = name.Length > 0 ? name : "Guests", ["count"] = count, ["timeLimitMinutes"] = minutes };
                if (Num(input, "guests", 1, 10_000) is { } g) body["authorizedGuestLimit"] = g;
                if (Num(input, "dataMb", 1, 10_000_000) is { } mb) body["dataUsageLimitMBytes"] = mb;
                if (Num(input, "downKbps", 1, 100_000_000) is { } dn) body["rxRateLimitKbps"] = dn;
                if (Num(input, "upKbps", 1, 100_000_000) is { } upk) body["txRateLimitKbps"] = upk;
                var before = (await All($"/sites/{sid}/hotspot/vouchers", ct, 5000)).Items.Select(v => S(v, "id")).ToHashSet();
                await Send(HttpMethod.Post, await Root(ct) + $"/sites/{sid}/hotspot/vouchers", body, ct);
                var made = (await All($"/sites/{sid}/hotspot/vouchers", ct, 5000)).Items.Where(v => !before.Contains(S(v, "id"))).ToList();
                AuditLog.Write("you", "infra:" + _c.Name, "UniFi vouchers generated", $"{made.Count} × {minutes} min on {o.Name}");
                return made.Count >= count
                    ? new ActionResult(true, true, $"verified: {made.Count} voucher(s) created", string.Join("\n", made.Select(v => $"{S(v, "code")}   {S(v, "timeLimitMinutes")} min   {S(v, "name")}")))
                    : new ActionResult(true, false, $"requested, but only {made.Count} new voucher(s) are listed");
            }
            if (Ids(o) is not { } ids) return new ActionResult(false, false, "refused: unexpected id");
            var root = await Root(ct);
            switch (a.Id)
            {
                case "restart":
                {
                    long before = await UptimeAsync(ids, ct) ?? long.MaxValue;
                    await Send(HttpMethod.Post, root + $"/sites/{ids.Site}/devices/{ids.Item}/actions", new JsonObject { ["action"] = "RESTART" }, ct);
                    AuditLog.Write("you", "infra:" + _c.Name, "UniFi device restart", o.Name);
                    var until = DateTime.UtcNow + VerifyFor;
                    while (DateTime.UtcNow < until)
                    {
                        await Task.Delay(PollEvery, ct);
                        try
                        {
                            var d = await Get($"/sites/{ids.Site}/devices/{ids.Item}", ct) as JsonObject;
                            if (S(d, "state") == "ONLINE" && await UptimeAsync(ids, ct) is { } now && now < before) return new ActionResult(true, true, $"verified: back online, up {Uptime(now)}");
                        }
                        catch (Exception ex) when (ex is UniFiException or HttpRequestException || ex is TaskCanceledException && !ct.IsCancellationRequested) { }   // rebooting: no answer yet
                    }
                    return new ActionResult(true, false, "restart requested, but NOT verified: the device didn't come back with a new uptime in time (refresh later)");
                }
                case "poe-cycle":
                {
                    var port = Num(input, "port", 1, 512);
                    var poe = (o.Props.GetValueOrDefault("ports.poe") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
                    if (port == null || !poe.Contains(port.ToString())) return new ActionResult(false, false, $"refused: port {input.GetValueOrDefault("port")} has no PoE on (PoE ports: {string.Join(", ", poe)})");
                    await Send(HttpMethod.Post, root + $"/sites/{ids.Site}/devices/{ids.Item}/interfaces/ports/{port}/actions", new JsonObject { ["action"] = "POWER_CYCLE" }, ct);
                    AuditLog.Write("you", "infra:" + _c.Name, "UniFi PoE power-cycle", $"{o.Name} port {port}");
                    return await Verify(async () => At((At(await Get($"/sites/{ids.Site}/devices/{ids.Item}", ct), "interfaces", "ports") as JsonArray)?.OfType<JsonObject>()
                        .FirstOrDefault(x => S(x, "idx") == port.ToString()), "poe", "state")?.ToString() ?? "", s => s == "UP", ct, $"port {port} has PoE power again");
                }
                case "unadopt":
                    await Send(HttpMethod.Delete, root + $"/sites/{ids.Site}/devices/{ids.Item}", null, ct);
                    AuditLog.Write("you", "infra:" + _c.Name, "UniFi device removed (unadopted)", o.Name);
                    return await Verify(async () => { try { await Get($"/sites/{ids.Site}/devices/{ids.Item}", ct); return "present"; } catch (UniFiException ex) when (ex.Status == 404) { return "gone"; } }, s => s == "gone", ct, "removed from the site");
                case "guest-on" or "guest-off":
                {
                    var body = new JsonObject { ["action"] = a.Id == "guest-on" ? "AUTHORIZE_GUEST_ACCESS" : "UNAUTHORIZE_GUEST_ACCESS" };
                    if (a.Id == "guest-on" && Num(input, "minutes", 1, 1_000_000) is { } m) body["timeLimitMinutes"] = m;
                    await Send(HttpMethod.Post, root + $"/sites/{ids.Site}/clients/{ids.Item}/actions", body, ct);
                    AuditLog.Write("you", "infra:" + _c.Name, a.Id == "guest-on" ? "UniFi guest authorised" : "UniFi guest access revoked", o.Name);
                    bool want = a.Id == "guest-on";
                    return await Verify(async () => S(At(await Get($"/sites/{ids.Site}/clients/{ids.Item}", ct), "access"), "authorized"), s => s == (want ? "true" : "false"), ct, want ? "authorised" : "revoked");
                }
                case "voucher-delete":
                    await Send(HttpMethod.Delete, root + $"/sites/{ids.Site}/hotspot/vouchers/{ids.Item}", null, ct);
                    return await Verify(async () => { try { await Get($"/sites/{ids.Site}/hotspot/vouchers/{ids.Item}", ct); return "present"; } catch (UniFiException ex) when (ex.Status == 404) { return "gone"; } }, s => s == "gone", ct, "deleted");
                case "enable" or "disable":
                {
                    bool on = a.Id == "enable";
                    var path = o.Kind switch { "wifi" => $"/sites/{ids.Site}/wifi/broadcasts/{ids.Item}", "network" => $"/sites/{ids.Site}/networks/{ids.Item}", "firewall policy" => $"/sites/{ids.Site}/firewall/policies/{ids.Item}", _ => "" };
                    if (path.Length == 0) return new ActionResult(false, false, "refused: not something that can be switched");
                    if (o.Kind == "firewall policy") await Send(HttpMethod.Patch, root + path, new JsonObject { ["enabled"] = on }, ct);
                    else
                    {
                        // The API updates the whole object (PUT): read it, change only "enabled", send it back without its read-only fields.
                        var cur = await Get(path, ct) as JsonObject ?? throw new UniFiException(0, "couldn't read it first");
                        foreach (var ro in new[] { "id", "metadata" }) cur.Remove(ro);
                        cur["enabled"] = on;
                        await Send(HttpMethod.Put, root + path, cur, ct);
                    }
                    AuditLog.Write("you", "infra:" + _c.Name, $"UniFi {o.Kind} turned {(on ? "on" : "off")}", o.Name);
                    return await Verify(async () => S(await Get(path, ct), "enabled"), s => s == (on ? "true" : "false"), ct, on ? "on" : "off");
                }
                default: return new ActionResult(false, false, "refused: unknown action");
            }
        }
        catch (Exception ex) when (ex is UniFiException or HttpRequestException or OfflineBlockedException || ex is TaskCanceledException && !ct.IsCancellationRequested) { return new ActionResult(false, false, "refused: " + ex.Message); }
    }

    private async Task<long?> UptimeAsync((string Site, string Item) ids, CancellationToken ct)
    {
        try { return At(await Get($"/sites/{ids.Site}/devices/{ids.Item}/statistics/latest", ct), "uptimeSec") is JsonValue v && v.TryGetValue<long>(out var s) ? s : null; }
        catch (UniFiException) { return null; }
    }

    /// <summary>Re-reads until the state matches (or time is up: then "not verified").</summary>
    private async Task<ActionResult> Verify(Func<Task<string>> read, Func<string, bool> done, CancellationToken ct, string what)
    {
        var until = DateTime.UtcNow + VerifyFor;
        string now = "";
        while (true)
        {
            try { now = await read(); } catch (Exception ex) when (ex is UniFiException or HttpRequestException) { now = "(" + ex.Message + ")"; }
            if (done(now)) return new ActionResult(true, true, "verified: " + what);
            if (DateTime.UtcNow >= until) return new ActionResult(true, false, $"requested, but NOT verified: UniFi reports “{now}”");
            await Task.Delay(PollEvery, ct);
        }
    }

    /// <summary>Self-test: the API root the provider settled on.</summary>
    internal string? RootForTest => _base;
}
