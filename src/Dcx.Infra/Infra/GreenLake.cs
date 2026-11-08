using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DcxManager.Services;
using static DcxManager.Infra.InfraJson;

namespace DcxManager.Infra;

/// <summary>
/// HPE GreenLake (0.6.33, DarkCipher0x: "integrate any kind of HPE greenlake api access into infrastructure"): the
/// workspace's devices (servers, switches, access points, storage) with their subscription and service assignment, the
/// subscriptions with their expiry, the newest audit-log entries and — for a chosen region — HPE Compute Ops Management's
/// servers with power state, health and their iLO.
/// <list type="bullet">
/// <item><b>Sign-in:</b> an API client made in GreenLake (Manage workspace → Personal API clients), as a vault credential
/// (user name = client id, password = client secret), through OAuth client credentials (<see cref="OAuthClientCredentials"/>):
/// HPE's common sign-in service, or the workspace's own token address when its id is given. The token lives in memory.</item>
/// <item><b>Where:</b> only https addresses under greenlake.hpe.com (the global API and the fixed list of COM regions) —
/// nothing an answer names is followed.</item>
/// <item><b>Read-only</b> by design (a fleet and licence view). A COM server's iLO can be added as a Redfish provider in
/// one step, to read its health in detail and to power it with an iLO account.</item>
/// </list>
/// The API moved from v1beta1 to v1 in 2025: each list tries v1 first and the older path when v1 isn't there.
/// </summary>
public sealed class GreenLakeProvider : IInfraProvider
{
    public const string GlobalBase = "https://global.api.greenlake.hpe.com";
    public const string CommonTokenUrl = "https://sso.common.cloud.hpe.com/as/token.oauth2";
    public static readonly string[] Regions = ["", "us-west", "eu-central", "ap-northeast"];

    public static readonly ProviderKind Descriptor = new(
        "greenlake", "HPE GreenLake (devices, subscriptions, Compute Ops Management)",
        "HPE GreenLake — devices, subscriptions, Compute Ops Management…",
        "Server hardware",
        () => new ProviderConfig { Kind = "greenlake", Name = "HPE GreenLake", Endpoint = GlobalBase },
        (c, h) => new GreenLakeProvider(c, h),
        ["All", "device", "subscription", "server", "audit"],
        null, _ => null,
        "API CLIENT (a vault credential: user name = client id, password = client secret)", NeedsUser: true,
        "Create it in HPE GreenLake: Manage workspace → Personal API clients → Create, for the “HPE GreenLake platform” (devices, subscriptions, audit log) " +
        "and, for servers, for “Compute Ops Management”. Give the API client's user an Observer (read-only) role. Copy the client id and secret into a vault credential. " +
        "Read-only: to power a server, right-click it → “Add its iLO as a Redfish provider”.",
        PinsTls: false,
        Fields:
        [
            new("COMPUTE OPS MANAGEMENT REGION (empty = don't read COM servers)", c => c.Region, (c, v) => c.Region = v,
                v => Regions.Contains(v) ? null : "Choose a Compute Ops Management region from the list (or leave it empty).", Regions),
            new("WORKSPACE ID (optional: for API clients that sign in at the workspace's own token address)", c => c.WorkspaceId, (c, v) => c.WorkspaceId = v,
                v => v.Length == 0 || Regex.IsMatch(v, "^[A-Za-z0-9-]{8,64}$") ? null : "The workspace id: letters, digits and dashes (Manage workspace shows it)."),
        ]);

    private readonly ProviderConfig _c;
    private readonly HttpClient _http;
    internal OAuthClientCredentials Auth { get; }
    public int ListCap { get; set; } = 5000;
    public int AuditEntries { get; set; } = 25;

    public ProviderConfig Config => _c;
    public string Where => GlobalBase;
    public NetClass? NetworkClass => NetworkPolicy.Classify(GlobalBase);

    public GreenLakeProvider(ProviderConfig c, HttpMessageHandler? handler = null)
    {
        _c = c;
        handler ??= TlsPin.Handler(() => "", (_, _) => { });   // a public service: Windows' certificate trust decides
        Auth = new OAuthClientCredentials(() => _c.CredentialId, "HPE GreenLake " + c.Name, TokenUrl, handler);
        _http = new HttpClient(Auth) { Timeout = TimeSpan.FromSeconds(45), MaxResponseContentBufferSize = MaxBody };
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    internal string TokenUrl() => _c.WorkspaceId.Trim().Length > 0
        ? $"{GlobalBase}/authorization/v2/oauth2/{Uri.EscapeDataString(_c.WorkspaceId.Trim())}/token"
        : CommonTokenUrl;

    /// <summary>Compute Ops Management's regional address, or null when no region is chosen.</summary>
    internal string? ComBase => Regions.Contains(_c.Region) && _c.Region.Length > 0 ? $"https://{_c.Region}.api.greenlake.hpe.com" : null;

    public sealed class GreenLakeException(int status, string message) : Exception(message) { public int Status { get; } = status; }

    private async Task<JsonObject> Get(string url, CancellationToken ct, bool retried = false)
    {
        if (!IsHttpsIn(url, "greenlake.hpe.com")) throw new GreenLakeException(0, "refused: not a GreenLake address");
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var r = await _http.SendAsync(req, ct);
        var text = await ReadAsync(r, ct);
        int code = (int)r.StatusCode;
        if (code == 429 && !retried)
        {
            var wait = r.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(3);
            await Task.Delay(wait > TimeSpan.FromSeconds(15) ? TimeSpan.FromSeconds(15) : wait, ct);
            return await Get(url, ct, true);
        }
        if (code == 401) throw new GreenLakeException(401, "GreenLake refused the token (the API client may be deleted, or made for another workspace)");
        if (code == 403) throw new GreenLakeException(403, "the API client may not read this (its user needs a role for this service)");
        if (code == 429) throw new GreenLakeException(429, "GreenLake is rate-limiting this API client: try again in a minute");
        if (code >= 300) throw new GreenLakeException(code, $"HTTP {code}: {Message(text)}");
        try { return Parse(text) as JsonObject ?? new JsonObject(); }
        catch (System.Text.Json.JsonException) { throw new GreenLakeException(code, "GreenLake didn't answer with JSON"); }
    }

    /// <summary>Every page of a list (offset / limit), trying each path in turn while the service answers 404.</summary>
    internal async Task<(List<JsonObject> Items, bool Truncated)> All(string baseUrl, string[] paths, CancellationToken ct, int limit = 200, int? cap = null)
    {
        GreenLakeException? last = null;
        foreach (var path in paths)
        {
            try
            {
                var items = new List<JsonObject>();
                int max = cap ?? ListCap;
                for (int offset = 0, pages = 0; pages < 100; pages++)
                {
                    var j = await Get($"{baseUrl}/{path}{(path.Contains('?') ? "&" : "?")}limit={limit}&offset={offset}", ct);
                    var got = Objects(j["items"]).ToList();
                    items.AddRange(got);
                    long.TryParse(S(j, "total"), out var total);
                    offset += got.Count;
                    if (items.Count >= max) return (items.Take(max).ToList(), total > max || got.Count == limit);
                    if (got.Count < limit || (total > 0 && offset >= total) || got.Count == 0) return (items, false);
                }
                return (items, true);
            }
            catch (GreenLakeException ex) when (ex.Status == 404) { last = ex; }
        }
        throw last ?? new GreenLakeException(404, "not found");
    }

    public async Task<ProviderCaps> DetectAsync(CancellationToken ct)
    {
        try
        {
            if (!ExternalCredentials.HasSecret(AppData.Current.FindCredential(_c.CredentialId))) return ProviderCaps.Unreachable("choose the vault credential with the API client id and secret");
            var (d, _) = await All(GlobalBase, ["devices/v1/devices", "devices/v1beta1/devices"], ct, limit: 1, cap: 1);
            var notes = new List<string>
            {
                "Read-only: devices, subscriptions and the audit log of this workspace" + (ComBase != null ? $", and Compute Ops Management servers in {_c.Region}." : "."),
                "To manage a server, right-click it → “Add its iLO as a Redfish provider” (an iLO account then powers it, with every change verified).",
            };
            if (ComBase == null) notes.Add("Compute Ops Management isn't read: choose its region in Edit to see servers.");
            return new ProviderCaps(true, true, false, "v1", "HPE GreenLake platform" + (ComBase != null ? " + Compute Ops Management (" + _c.Region + ")" : ""), notes);
        }
        catch (Exception ex) when (ex is GreenLakeException or HttpRequestException or TaskCanceledException or OfflineBlockedException or InvalidOperationException)
        { return ProviderCaps.Unreachable(ex is TaskCanceledException ? "timed out" : ex.Message); }
    }

    private static string Lower(string s) => s.ToLowerInvariant().Replace('_', ' ');

    public async Task<IReadOnlyList<InfraObject>> InventoryAsync(CancellationToken ct)
    {
        var list = new List<InfraObject>();
        var notes = new List<string>();
        try
        {
            var (devices, cut) = await All(GlobalBase, ["devices/v1/devices", "devices/v1beta1/devices"], ct);
            if (cut) notes.Add($"more than {ListCap} devices: the first {ListCap} are shown");
            foreach (var d in devices)
            {
                var assigned = S(d, "assignedState");
                var status = S(d, "archived") == "true" ? "archived" : assigned switch { "ASSIGNED_TO_SERVICE" => "active", "UNASSIGNED" => "unassigned", "" => "unknown", _ => Lower(assigned) };
                var subs = Objects(d["subscription"]).Select(s => S(s, "key") is { Length: > 0 } k ? k : S(s, "id")).Where(s => s.Length > 0).ToList();
                list.Add(new InfraObject("device:" + S(d, "id"), "device", S(d, "serialNumber") is { Length: > 0 } sn ? $"{S(d, "model")} {sn}".Trim() : S(d, "id"), status,
                    S(d, "location", "name") is { Length: > 0 } loc ? loc : S(d, "region"),
                    $"{Lower(S(d, "deviceType"))} · {S(d, "model")} · part {S(d, "partNumber")} · serial {S(d, "serialNumber")}" + (S(d, "macAddress") is { Length: > 0 } mac ? " · " + mac : "") +
                    (subs.Count > 0 ? " · subscription " + string.Join(", ", subs) : " · no subscription"), Flat(d)));
            }
        }
        catch (GreenLakeException ex) { notes.Add("devices: " + ex.Message); }
        try
        {
            var (subs, _) = await All(GlobalBase, ["subscriptions/v1/subscriptions", "subscriptions/v1beta1/subscriptions"], ct);
            foreach (var s in subs)
            {
                var end = DateTime.TryParse(S(s, "endTime"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal, out var e) ? e : (DateTime?)null;
                var status = end is { } x && x < DateTime.UtcNow ? "expired" : end is { } y && y < DateTime.UtcNow.AddDays(30) ? "degraded" : "active";
                list.Add(new InfraObject("subscription:" + S(s, "id"), "subscription", S(s, "key") is { Length: > 0 } k ? k : S(s, "id"), status, "",
                    $"{Lower(S(s, "subscriptionType"))} {S(s, "tier")} · {S(s, "availableQuantity")} of {S(s, "quantity")} free" + (end is { } z ? $" · ends {z:yyyy-MM-dd}" + (status == "degraded" ? " (within 30 days)" : "") : ""), Flat(s)));
            }
        }
        catch (GreenLakeException ex) { notes.Add("subscriptions: " + ex.Message); }
        try
        {
            var (logs, _) = await All(GlobalBase, ["audit-log/v1/logs"], ct, limit: AuditEntries, cap: AuditEntries);
            foreach (var l in logs)
                list.Add(new InfraObject("audit:" + S(l, "id"), "audit", Short(S(l, "description"), 90), "online", S(l, "category"),
                    $"{S(l, "createdAt")} · {S(l, "category")} · {S(l, "user", "username")} · {S(l, "description")}", Flat(l)));
        }
        catch (GreenLakeException ex) when (ex.Status is 403 or 404) { }
        catch (GreenLakeException ex) { notes.Add("audit log: " + ex.Message); }
        if (ComBase is { } com)
        {
            try
            {
                var (servers, cut) = await All(com, ["compute-ops-mgmt/v1/servers", "compute-ops-mgmt/v1beta2/servers", "compute-ops/v1beta2/servers"], ct, limit: 100);
                if (cut) notes.Add($"more than {ListCap} servers");
                foreach (var s in servers)
                {
                    var health = S(s, "hardware", "health", "summary");
                    var power = S(s, "hardware", "powerState");
                    var status = health is "CRITICAL" ? "critical" : health is "WARNING" ? "degraded" : power switch { "ON" => "running", "OFF" => "stopped", _ => S(s, "state", "connected") == "false" ? "offline" : "unknown" };
                    var props = Flat(s);
                    list.Add(new InfraObject("server:" + S(s, "id"), "server", S(s, "name") is { Length: > 0 } n ? n : S(s, "hardware", "serialNumber"), status, "Compute Ops Management",
                        $"{S(s, "hardware", "model")} · serial {S(s, "hardware", "serialNumber")} · power {power} · health {(health.Length > 0 ? health : "?")}" +
                        (S(s, "hardware", "bmc", "ip") is { Length: > 0 } ip ? " · iLO " + ip : "") + (S(s, "host", "osName") is { Length: > 0 } os ? " · " + os : "") +
                        (S(s, "state", "connected") == "false" ? " · not connected" : ""), props));
                }
            }
            catch (GreenLakeException ex) { notes.Add("Compute Ops Management: " + ex.Message); }
        }
        if (notes.Count > 0) list.Add(new InfraObject("notes", "note", "Not everything could be read", "offline", "", string.Join(" · ", notes), new Dictionary<string, string>()));
        return list;
    }

    private static string Short(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    public static readonly InfraAction Details = new("details", "ⓘ All fields", false, Consequence.None, null, "Every field GreenLake reported for this item.");
    public static readonly InfraAction AddIlo = new("add-ilo", "＋ Add its iLO as a Redfish provider", false, Consequence.None, null,
        "Adds this server's iLO to the Infrastructure list as a Redfish provider (read-only until you allow changes). Then edit it to choose the iLO account from the vault and trust its certificate.");

    public IReadOnlyList<InfraAction> ActionsFor(InfraObject o) =>
        o.Kind == "note" ? Array.Empty<InfraAction>() : o.Kind == "server" && IloAddress(o) != null ? new[] { Details, AddIlo } : new[] { Details };

    /// <summary>The iLO address COM reported, when it is a plain IP address or host name.</summary>
    internal static string? IloAddress(InfraObject o) =>
        o.Props.GetValueOrDefault("hardware.bmc.ip") is { Length: > 0 and < 254 } ip
        && (IPAddress.TryParse(ip, out _) || Regex.IsMatch(ip, @"^[A-Za-z0-9]([A-Za-z0-9-]{0,62})(\.[A-Za-z0-9]([A-Za-z0-9-]{0,62}))*$")) ? ip : null;

    public string? Forbidden(InfraAction a, InfraObject o, ProviderCaps caps) => null;
    public Task<ActionResult> RunAsync(InfraAction a, InfraObject o, CancellationToken ct) => RunAsync(a, o, new Dictionary<string, string>(), ct);

    public Task<ActionResult> RunAsync(InfraAction a, InfraObject o, IReadOnlyDictionary<string, string> input, CancellationToken ct)
    {
        if (a.Id == "details") return Task.FromResult(new ActionResult(true, true, "fields", string.Join("\n", o.Props.OrderBy(p => p.Key).Select(p => $"{p.Key,-40} {p.Value}"))));
        if (a.Id != "add-ilo" || IloAddress(o) is not { } ip) return Task.FromResult(new ActionResult(false, false, "refused: unknown action"));
        var endpoint = "https://" + (IPAddress.TryParse(ip, out var addr) && addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{ip}]" : ip);
        if (AppData.Current.InfraProviders.FirstOrDefault(p => p.Kind == "redfish" && string.Equals(p.Endpoint, endpoint, StringComparison.OrdinalIgnoreCase)) is { } existing)
            return Task.FromResult(new ActionResult(true, true, $"already in the list as “{existing.Name}”"));
        var c = new ProviderConfig { Kind = "redfish", Name = o.Name + " iLO", Endpoint = endpoint, Environment = _c.Environment };
        AppData.Current.InfraProviders.Add(c);
        AppData.Save();
        AuditLog.Write("you", "infra:" + c.Name, "provider added", "Redfish (iLO) from HPE GreenLake");
        return Task.FromResult(new ActionResult(true, true, $"added “{c.Name}” ({endpoint}) as a Redfish provider: select it, Edit to choose the iLO account, then trust its certificate"));
    }
}
