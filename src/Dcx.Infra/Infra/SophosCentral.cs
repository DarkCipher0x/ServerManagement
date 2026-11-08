using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DcxManager.Services;
using static DcxManager.Infra.InfraJson;

namespace DcxManager.Infra;

/// <summary>
/// Sophos Central (0.6.33, DarkCipher0x: "security solutions api access would be good too, like Sophos Central ... for
/// Sophos Firewall management and devices"): the firewalls Central manages (connection, firmware, group, cluster) and their
/// groups, the protected computers and servers (health, threats, services, tamper protection, isolation) and the open alerts
/// — for one tenant, or every tenant of a partner or organization account.
/// <list type="bullet">
/// <item><b>Sign-in:</b> a Central API credential (Global Settings → API Credentials Management; partner / organization:
/// Settings → API credentials) as a vault credential (user name = client id, password = client secret), through OAuth
/// client credentials at id.sophos.com; the token lives in memory. whoami says which kind of account it is and where its
/// data lives.</item>
/// <item><b>Where:</b> only https addresses under sophos.com: a data-region address from whoami or a tenant list is refused
/// unless it is one, so the token and tenant id never go anywhere else.</item>
/// <item><b>Changes</b> (only with "Allow changes"; the API credential's role decides too — Service Principal Read-Only can
/// only look): check for and schedule a firewall firmware upgrade, start a scan or an update check on a computer, isolate it
/// from the network or release it (verified by re-reading), acknowledge an alert.</item>
/// </list>
/// </summary>
public sealed class SophosCentralProvider : IInfraProvider
{
    public const string TokenUrl = "https://id.sophos.com/api/v2/oauth2/token";
    public const string GlobalBase = "https://api.central.sophos.com";

    public static readonly ProviderKind Descriptor = new(
        "sophos-central", "Sophos Central (firewalls, endpoints, alerts)",
        "Sophos Central — firewalls, endpoints, alerts…",
        "Security",
        () => new ProviderConfig { Kind = "sophos-central", Name = "Sophos Central", Endpoint = GlobalBase },
        (c, h) => new SophosCentralProvider(c, h),
        ["All", "firewall", "firewall group", "computer", "server", "alert", "tenant"],
        null, _ => null,
        "API CREDENTIAL (a vault credential: user name = client id, password = client secret)", NeedsUser: true,
        "Create it in Sophos Central: Global Settings → API Credentials Management → Add Credential (a partner or organization: Settings & policies → API credentials). " +
        "Least privilege: the “Service Principal Read-Only” role to look; “Service Principal Super Admin” only if you want firmware upgrades, scans and isolation from here. " +
        "Copy the client id and secret into a vault credential. One credential of a partner or organization account shows every tenant.",
        PinsTls: false, Fields: []);

    private readonly ProviderConfig _c;
    private readonly HttpClient _http;
    internal OAuthClientCredentials Auth { get; }
    // The tenants of the last refresh: id → (name, data-region address).
    private readonly Dictionary<string, (string Name, string Host)> _tenants = new();
    private string _accountKind = "";
    public int ListCap { get; set; } = 5000;
    public int MaxTenants { get; set; } = 25;
    public int MaxAlerts { get; set; } = 100;
    public TimeSpan VerifyFor { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan PollEvery { get; set; } = TimeSpan.FromSeconds(4);

    public ProviderConfig Config => _c;
    public string Where => GlobalBase;
    public NetClass? NetworkClass => NetworkPolicy.Classify(GlobalBase);

    public SophosCentralProvider(ProviderConfig c, HttpMessageHandler? handler = null)
    {
        _c = c;
        handler ??= TlsPin.Handler(() => "", (_, _) => { });   // a public service: Windows' certificate trust decides
        Auth = new OAuthClientCredentials(() => _c.CredentialId, "Sophos Central " + c.Name, () => TokenUrl, handler, scope: "token");
        _http = new HttpClient(Auth) { Timeout = TimeSpan.FromSeconds(45), MaxResponseContentBufferSize = MaxBody };
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public sealed class SophosException(int status, string message) : Exception(message) { public int Status { get; } = status; }

    internal static bool IsId(string s) => Regex.IsMatch(s, "^[A-Za-z0-9-]{8,64}$");

    /// <summary>One call. The tenant (or partner / organization) id goes in its header; the address must be Sophos's.</summary>
    private async Task<JsonObject> Send(HttpMethod m, string url, CancellationToken ct, (string Name, string Value)? scope = null, JsonObject? body = null, bool retried = false)
    {
        if (!IsHttpsIn(url, "sophos.com")) throw new SophosException(0, "refused: not a Sophos Central address");
        using var req = new HttpRequestMessage(m, url);
        if (scope is { } s) req.Headers.TryAddWithoutValidation(s.Name, s.Value);
        if (body != null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var r = await _http.SendAsync(req, ct);
        var text = await ReadAsync(r, ct);
        int code = (int)r.StatusCode;
        if (code == 429 && !retried)
        {
            var wait = r.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(3);
            await Task.Delay(wait > TimeSpan.FromSeconds(15) ? TimeSpan.FromSeconds(15) : wait, ct);
            return await Send(m, url, ct, scope, body, true);
        }
        if (code == 401) throw new SophosException(401, "Sophos Central refused the token (the API credential may be deleted or expired)");
        if (code == 403) throw new SophosException(403, "the API credential's role doesn't allow this (Service Principal Read-Only can only look)");
        if (code == 429) throw new SophosException(429, "Sophos Central is rate-limiting this credential: try again in a minute");
        if (code >= 300) throw new SophosException(code, $"HTTP {code}: {Message(text)}");
        if (text.Trim().Length == 0) return new JsonObject();
        try { return Parse(text) as JsonObject ?? new JsonObject(); }
        catch (System.Text.Json.JsonException) { throw new SophosException(code, "Sophos Central didn't answer with JSON"); }
    }

    private static (string, string) Tenant(string id) => ("X-Tenant-ID", id);

    /// <summary>whoami, then the tenants this credential may read (itself, or a partner's / organization's).</summary>
    private async Task<List<(string Id, string Name, string Host)>> TenantsAsync(CancellationToken ct)
    {
        var who = await Send(HttpMethod.Get, GlobalBase + "/whoami/v1", ct);
        var id = S(who, "id");
        _accountKind = S(who, "idType");
        if (!IsId(id)) throw new SophosException(0, "whoami gave no usable id");
        var global = S(who, "apiHosts", "global") is { Length: > 0 } g && IsHttpsIn(g, "sophos.com") ? g.TrimEnd('/') : GlobalBase;
        if (_accountKind == "tenant")
        {
            var host = S(who, "apiHosts", "dataRegion").TrimEnd('/');
            if (!IsHttpsIn(host, "sophos.com")) throw new SophosException(0, "whoami named a data region outside sophos.com (refused)");
            return [(id, _c.Name, host)];
        }
        if (_accountKind is not ("partner" or "organization")) throw new SophosException(0, "unexpected account kind: " + _accountKind);
        var header = _accountKind == "partner" ? "X-Partner-ID" : "X-Organization-ID";
        var list = new List<(string, string, string)>();
        for (int page = 1; page <= 50 && list.Count < MaxTenants; page++)
        {
            var j = await Send(HttpMethod.Get, $"{global}/{_accountKind}/v1/tenants?pageTotal=true&pageSize=100&page={page}", ct, (header, id));
            foreach (var t in Objects(j["items"]))
                if (IsId(S(t, "id")) && S(t, "apiHost").TrimEnd('/') is var h && IsHttpsIn(h, "sophos.com") && list.Count < MaxTenants)
                    list.Add((S(t, "id"), S(t, "showAs") is { Length: > 0 } sa ? sa : S(t, "name"), h));
            if (!int.TryParse(S(j, "pages", "total"), out var total) || page >= total) break;
        }
        return list;
    }

    /// <summary>A keyed list (pageFromKey / nextKey), or a numbered one (page / pages.total), up to the cap.</summary>
    private async Task<List<JsonObject>> List(string url, string tenant, CancellationToken ct, bool keyed, int? cap = null)
    {
        var items = new List<JsonObject>();
        int max = cap ?? ListCap;
        string? key = null;
        for (int page = 1; page <= 200; page++)
        {
            var sep = url.Contains('?') ? "&" : "?";
            var u = keyed ? url + sep + "pageSize=500" + (key != null ? "&pageFromKey=" + Uri.EscapeDataString(key) : "")
                          : url + sep + $"pageTotal=true&pageSize=100&page={page}";
            var j = await Send(HttpMethod.Get, u, ct, Tenant(tenant));
            items.AddRange(Objects(j["items"]));
            if (items.Count >= max) return items.Take(max).ToList();
            if (keyed) { key = S(j, "pages", "nextKey"); if (string.IsNullOrEmpty(key)) break; }
            else if (!int.TryParse(S(j, "pages", "total"), out var total) || page >= total) break;
        }
        return items;
    }

    public async Task<ProviderCaps> DetectAsync(CancellationToken ct)
    {
        try
        {
            if (!ExternalCredentials.HasSecret(AppData.Current.FindCredential(_c.CredentialId))) return ProviderCaps.Unreachable("choose the vault credential with the API client id and secret");
            var tenants = await TenantsAsync(ct);
            var notes = new List<string>
            {
                "Changes (firmware upgrades, scans, isolation, acknowledging alerts) need “Allow changes” here and a Super Admin API credential; a Read-Only one can only look.",
            };
            if (tenants.Count >= MaxTenants) notes.Add($"Only the first {MaxTenants} tenants are read.");
            return new ProviderCaps(true, true, true, "v1", $"Sophos Central · {_accountKind} account · {tenants.Count} tenant(s)", notes);
        }
        catch (Exception ex) when (ex is SophosException or HttpRequestException or TaskCanceledException or OfflineBlockedException or InvalidOperationException)
        { return ProviderCaps.Unreachable(ex is TaskCanceledException ? "timed out" : ex.Message); }
    }

    public async Task<IReadOnlyList<InfraObject>> InventoryAsync(CancellationToken ct)
    {
        var list = new List<InfraObject>();
        var notes = new List<string>();
        var tenants = await TenantsAsync(ct);
        _tenants.Clear();
        foreach (var (tid, tname, host) in tenants)
        {
            _tenants[tid] = (tname, host);
            string where = tenants.Count > 1 ? tname : "";
            if (tenants.Count > 1) list.Add(new InfraObject("tenant:" + tid, "tenant", tname, "online", "", host, new Dictionary<string, string> { ["id"] = tid, ["apiHost"] = host }));
            try
            {
                foreach (var f in await List(host + "/firewall/v1/firewalls", tid, ct, keyed: false))
                {
                    bool connected = S(f, "status", "connected") == "true", suspended = S(f, "status", "suspended") == "true";
                    var status = suspended ? "suspended" : connected ? "online" : "offline";
                    var ext = string.Join(", ", Strings(f["externalIpv4Addresses"]).Take(3));
                    list.Add(new InfraObject($"firewall:{tid}:{S(f, "id")}", "firewall", S(f, "name") is { Length: > 0 } n ? n : S(f, "hostname"), status, where.Length > 0 ? where : S(f, "group", "name"),
                        $"{S(f, "model")} · {S(f, "firmwareVersion")} · serial {S(f, "serialNumber")} · {(connected ? "connected" : "NOT connected")}" +
                        (S(f, "status", "managing") is { Length: > 0 } mg ? " · managing " + mg : "") + (S(f, "group", "name") is { Length: > 0 } gn ? " · group " + gn : "") +
                        (S(f, "cluster", "mode") is { Length: > 0 } cm ? " · cluster " + cm : "") + (ext.Length > 0 ? " · " + ext : ""), Flat(f)));
                }
                foreach (var g in await List(host + "/firewall/v1/firewall-groups", tid, ct, keyed: false))
                    list.Add(new InfraObject($"fwgroup:{tid}:{S(g, "id")}", "firewall group", S(g, "name"), "active", where,
                        $"{S(g, "firewalls", "total")} firewall(s)" + (S(g, "lockedByManagingAccount") == "true" ? " · locked by the managing account" : ""), Flat(g)));
            }
            catch (SophosException ex) when (ex.Status is 403 or 404) { notes.Add($"{tname}: firewalls aren't readable with this credential ({ex.Message})"); }
            catch (SophosException ex) { notes.Add($"{tname} firewalls: " + ex.Message); }
            try
            {
                foreach (var e in await List(host + "/endpoint/v1/endpoints", tid, ct, keyed: true))
                {
                    var overall = S(e, "health", "overall");
                    var isolated = S(e, "isolation", "status") == "isolated";
                    var status = isolated ? "isolated" : overall switch { "good" => "online", "suspicious" => "degraded", "bad" => "critical", _ => "unknown" };
                    var kind = S(e, "type") == "server" ? "server" : "computer";
                    list.Add(new InfraObject($"endpoint:{tid}:{S(e, "id")}", kind, S(e, "hostname"), status, where,
                        $"{S(e, "os", "name")} · health {(overall.Length > 0 ? overall : "?")} (threats {S(e, "health", "threats", "status")}, services {S(e, "health", "services", "status")})" +
                        $" · tamper protection {(S(e, "tamperProtectionEnabled") == "true" ? "on" : "OFF")}" + (isolated ? " · ISOLATED" : "") +
                        (S(e, "lastSeenAt") is { Length: > 0 } seen ? " · seen " + seen : "") + (Strings(e["ipv4Addresses"]).FirstOrDefault() is { } ip ? " · " + ip : ""), Flat(e)));
                }
            }
            catch (SophosException ex) when (ex.Status is 403 or 404) { notes.Add($"{tname}: endpoints aren't readable with this credential"); }
            catch (SophosException ex) { notes.Add($"{tname} endpoints: " + ex.Message); }
            try
            {
                foreach (var a in await List(host + "/common/v1/alerts", tid, ct, keyed: true, cap: MaxAlerts))
                {
                    var sev = S(a, "severity");
                    list.Add(new InfraObject($"alert:{tid}:{S(a, "id")}", "alert", Short(S(a, "description"), 90), sev switch { "high" => "critical", "medium" => "degraded", _ => "online" }, where.Length > 0 ? where : S(a, "category"),
                        $"{S(a, "raisedAt")} · {sev} · {S(a, "category")} · {S(a, "product")} · {S(a, "description")}", Flat(a)) );
                    _alertActions[$"{tid}:{S(a, "id")}"] = Strings(a["allowedActions"]).ToArray();
                }
            }
            catch (SophosException ex) when (ex.Status is 403 or 404) { }
            catch (SophosException ex) { notes.Add($"{tname} alerts: " + ex.Message); }
        }
        if (notes.Count > 0) list.Add(new InfraObject("notes", "note", "Not everything could be read", "offline", "", string.Join(" · ", notes), new Dictionary<string, string>()));
        return list;
    }

    private readonly Dictionary<string, string[]> _alertActions = new();

    private static string Short(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    // ---- actions ----------------------------------------------------------------------------------------------------

    public static readonly InfraAction Details = new("details", "ⓘ All fields", false, Consequence.None, null, "Every field Sophos Central reported for this item.");
    public static readonly InfraAction FirmwareCheck = new("fw-check", "⟳ Check for firmware upgrades", false, Consequence.None, null,
        "Asks Sophos Central which firmware versions this firewall can be upgraded to (nothing changes).");
    public static readonly InfraAction FirmwareUpgrade = new("fw-upgrade", "⬆ Upgrade the firmware…", true, Consequence.ServiceInterruption, null,
        "Schedules the firmware upgrade through Sophos Central: the firewall downloads it, installs it and RESTARTS — traffic through it stops for several minutes. Take a configuration backup first.")
    {
        Inputs = [new InfraInput("version", "Firmware version (from “Check for firmware upgrades”)", Hint: "e.g. 21.0.1.272")],
    };
    public static readonly InfraAction Scan = new("scan", "🔍 Start a scan", true, Consequence.None, null, "Starts a scan on this computer through Sophos Central.");
    public static readonly InfraAction UpdateCheck = new("update-check", "⟳ Check for updates", true, Consequence.None, null, "Tells the Sophos agent on this computer to check for updates now.");
    public static readonly InfraAction Isolate = new("isolate", "⛔ Isolate from the network…", true, Consequence.ServiceInterruption, "on",
        "Cuts this computer off from the network except Sophos Central (it keeps protecting it): everything else it does over the network stops until it is released.")
    {
        Inputs = [new InfraInput("comment", "Reason (kept in Sophos Central)", Default: "Isolated from DarkCipher ServerManagement", Required: false)],
    };
    public static readonly InfraAction Release = new("release", "✓ Release from isolation", true, Consequence.None, "off", "Lets this computer use the network again.");
    public static readonly InfraAction Acknowledge = new("acknowledge", "✓ Acknowledge the alert", true, Consequence.None, null, "Marks the alert as dealt with in Sophos Central (it leaves the list).");

    private static (string Tenant, string Id)? Ids(InfraObject o)
    {
        var p = o.Id.Split(':');
        return p.Length == 3 && IsId(p[1]) && IsId(p[2]) ? (p[1], p[2]) : null;
    }

    public IReadOnlyList<InfraAction> ActionsFor(InfraObject o) => o.Kind switch
    {
        "firewall" => new[] { Details, FirmwareCheck, FirmwareUpgrade },
        "computer" or "server" => o.Status == "isolated" ? new[] { Details, Scan, UpdateCheck, Release } : new[] { Details, Scan, UpdateCheck, Isolate },
        "alert" => Ids(o) is { } ids && _alertActions.GetValueOrDefault($"{ids.Tenant}:{ids.Id}") is { } acts && acts.Contains("acknowledge") ? new[] { Details, Acknowledge } : new[] { Details },
        "note" => Array.Empty<InfraAction>(),
        _ => new[] { Details },
    };

    public string? Forbidden(InfraAction a, InfraObject o, ProviderCaps caps) =>
        a == FirmwareUpgrade && o.Status != "online" ? "the firewall isn't connected to Sophos Central" : null;

    public Task<ActionResult> RunAsync(InfraAction a, InfraObject o, CancellationToken ct) => RunAsync(a, o, new Dictionary<string, string>(), ct);

    public async Task<ActionResult> RunAsync(InfraAction a, InfraObject o, IReadOnlyDictionary<string, string> input, CancellationToken ct)
    {
        if (a.Id == "details") return new ActionResult(true, true, "fields", string.Join("\n", o.Props.OrderBy(p => p.Key).Select(p => $"{p.Key,-40} {p.Value}")));
        if (Ids(o) is not { } ids || !_tenants.TryGetValue(ids.Tenant, out var t)) return new ActionResult(false, false, "refused: refresh first (unknown tenant or item)");
        var host = t.Host;
        try
        {
            switch (a.Id)
            {
                case "fw-check":
                {
                    var j = await Send(HttpMethod.Post, host + "/firewall/v1/firewalls/actions/firmware-upgrade-check", ct, Tenant(ids.Tenant), new JsonObject { ["firewalls"] = new JsonArray(ids.Id) });
                    var mine = Objects(j["firewalls"]).FirstOrDefault(f => S(f, "id") == ids.Id);
                    var versions = Strings(mine?["upgradeToVersion"]).ToList();
                    var info = Objects(j["firmwareVersions"]).Where(v => versions.Contains(S(v, "version"))).Select(v => $"{S(v, "version")}  {Short(S(v, "news").Replace('\n', ' '), 120)}");
                    return new ActionResult(true, true, versions.Count == 0 ? "no upgrade available: the firewall runs the newest firmware Central offers" : $"{versions.Count} upgrade(s) available: {string.Join(", ", versions)}",
                        versions.Count == 0 ? null : string.Join("\n", info.DefaultIfEmpty(string.Join("\n", versions))));
                }
                case "fw-upgrade":
                {
                    var version = input.GetValueOrDefault("version", "").Trim();
                    if (!Regex.IsMatch(version, @"^[0-9A-Za-z][0-9A-Za-z._-]{0,40}$")) return new ActionResult(false, false, "refused: enter a firmware version from “Check for firmware upgrades”");
                    var check = await Send(HttpMethod.Post, host + "/firewall/v1/firewalls/actions/firmware-upgrade-check", ct, Tenant(ids.Tenant), new JsonObject { ["firewalls"] = new JsonArray(ids.Id) });
                    var offered = Strings(Objects(check["firewalls"]).FirstOrDefault(f => S(f, "id") == ids.Id)?["upgradeToVersion"]).ToList();
                    if (!offered.Contains(version)) return new ActionResult(false, false, $"refused: {version} isn't offered for this firewall" + (offered.Count > 0 ? " (offered: " + string.Join(", ", offered) + ")" : " (no upgrade is offered)"));
                    await Send(HttpMethod.Post, host + "/firewall/v1/firewalls/actions/firmware-upgrade", ct, Tenant(ids.Tenant),
                        new JsonObject { ["firewalls"] = new JsonArray(new JsonObject { ["id"] = ids.Id, ["upgradeToVersion"] = version }) });
                    AuditLog.Write("you", "infra:" + _c.Name, "firewall firmware upgrade scheduled", $"{o.Name} → {version}");
                    return new ActionResult(true, false, $"accepted: Sophos Central upgrades “{o.Name}” to {version} (the firewall restarts); refresh later to see the new version");
                }
                case "scan" or "update-check":
                {
                    var path = a.Id == "scan" ? "scans" : "update-checks";
                    await Send(HttpMethod.Post, $"{host}/endpoint/v1/endpoints/{ids.Id}/{path}", ct, Tenant(ids.Tenant), new JsonObject());
                    AuditLog.Write("you", "infra:" + _c.Name, a.Id == "scan" ? "endpoint scan started" : "endpoint update check", o.Name);
                    return new ActionResult(true, false, a.Id == "scan" ? "accepted: the scan starts when the computer next checks in (results appear as alerts)" : "accepted: the agent checks for updates when it next checks in");
                }
                case "isolate" or "release":
                {
                    bool on = a.Id == "isolate";
                    var body = new JsonObject { ["enabled"] = on, ["ids"] = new JsonArray(ids.Id) };
                    if (on && input.GetValueOrDefault("comment") is { Length: > 0 } comment) body["comment"] = comment.Length > 200 ? comment[..200] : comment;
                    await Send(HttpMethod.Post, host + "/endpoint/v1/endpoints/isolation", ct, Tenant(ids.Tenant), body);
                    AuditLog.Write("you", "infra:" + _c.Name, on ? "endpoint isolated" : "endpoint released from isolation", o.Name);
                    return await Verify(async () => S(await Send(HttpMethod.Get, $"{host}/endpoint/v1/endpoints/{ids.Id}/isolation", ct, Tenant(ids.Tenant)), "enabled") == "true" ? "on" : "off", on ? "on" : "off", ct);
                }
                case "acknowledge":
                {
                    await Send(HttpMethod.Post, $"{host}/common/v1/alerts/{ids.Id}/actions", ct, Tenant(ids.Tenant), new JsonObject { ["action"] = "acknowledge" });
                    AuditLog.Write("you", "infra:" + _c.Name, "alert acknowledged", o.Name);
                    try { await Send(HttpMethod.Get, $"{host}/common/v1/alerts/{ids.Id}", ct, Tenant(ids.Tenant)); return new ActionResult(true, false, "accepted; the alert is still listed (Central may take a moment)"); }
                    catch (SophosException ex) when (ex.Status == 404) { return new ActionResult(true, true, "verified: the alert is gone"); }
                }
            }
            return new ActionResult(false, false, "refused: unknown action");
        }
        catch (Exception ex) when (ex is SophosException or HttpRequestException or TaskCanceledException or OfflineBlockedException or InvalidOperationException)
        {
            return new ActionResult(false, false, "refused: " + (ex is TaskCanceledException ? "timed out" : ex.Message));
        }
    }

    private async Task<ActionResult> Verify(Func<Task<string>> read, string expected, CancellationToken ct)
    {
        var until = DateTime.UtcNow + VerifyFor;
        string now = "";
        while (true)
        {
            try { now = await read(); } catch (SophosException) { }
            if (now == expected) return new ActionResult(true, true, $"verified: isolation now {now}");
            if (DateTime.UtcNow >= until) break;
            await Task.Delay(PollEvery, ct);
        }
        return new ActionResult(true, false, $"requested, but NOT verified yet: isolation reads “{now}” (it applies when the computer next checks in)");
    }
}
