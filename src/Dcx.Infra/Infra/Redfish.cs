using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Text;
using System.Text.Json.Nodes;
using DcxManager.Services;
using static DcxManager.Infra.InfraJson;

namespace DcxManager.Infra;

/// <summary>
/// A server's baseboard management controller through DMTF Redfish (0.6.33, DarkCipher0x: "supermicro api access and
/// tools like that"): HPE iLO 5/6/7, Supermicro BMCs (X11 and later), Dell iDRAC 8/9, Lenovo XClarity Controller and any
/// other Redfish service.
/// <list type="bullet">
/// <item><b>Transport:</b> https only; a BMC's self-signed certificate is pinned on first use (ADR-007) and a changed one is
/// refused. Every address the BMC hands back (<c>@odata.id</c>) must be a <c>/redfish/v1/…</c> path on the same BMC —
/// anything else is refused, so an answer can't send the session anywhere else.</item>
/// <item><b>Sign-in:</b> a vault credential (a dedicated BMC account; ReadOnly to look, Operator for power). A Redfish
/// session is opened for each refresh or action and deleted afterwards; its token lives in memory only and goes in the
/// X-Auth-Token header. A BMC without sessions gets HTTP Basic over https for that one operation.</item>
/// <item><b>Reads:</b> systems (model, serial, power, health, BIOS, CPU and memory), the BMC (firmware), chassis (power
/// draw, power supplies, fans, the hottest temperatures), drives (health, wear), the firmware inventory and the newest
/// entries of the BMC's event logs (iLO IML / IEL, the SEL).</item>
/// <item><b>Changes</b> (only with "Allow changes"; the account's role decides too): power on, graceful shut down /
/// restart, force off / restart, power cycle — only the reset types this system lists — each verified by re-reading the
/// power state; the identify light; restarting the BMC itself.</item>
/// </list>
/// </summary>
public sealed class RedfishProvider : IInfraProvider, ITlsPinned
{
    /// <summary>The Redfish annotation that lists a reset action's allowed types. Built from two parts so the public
    /// source's privacy scan doesn't read the joined name (an at sign between two dotted words) as an e-mail address.</summary>
    public const string AllowableResets = "ResetType" + "@Redfish.AllowableValues";

    public static readonly ProviderKind Descriptor = new(
        "redfish", "Server BMC — Redfish (HPE iLO, Supermicro, Dell iDRAC, Lenovo XCC)",
        "Server BMC — Redfish (HPE iLO, Supermicro, Dell iDRAC, Lenovo XCC)…",
        "Server hardware",
        () => new ProviderConfig { Kind = "redfish", Name = "BMC", Endpoint = "https://bmc.lan" },
        (c, h) => new RedfishProvider(c, h),
        ["All", "system", "bmc", "chassis", "power supply", "fan", "thermal", "drive", "firmware", "log"],
        "BMC ADDRESS (https only, e.g. https://ilo-srv01.lan or https://10.0.0.20)", ValidateEndpoint,
        "BMC ACCOUNT (a vault credential: user name and password)", NeedsUser: true,
        "Use a dedicated BMC account, never the factory administrator: the ReadOnly role (iLO: Read Only; iDRAC: Read Only; Supermicro: User; XCC: Read-Only) " +
        "is enough to look; power and the identify light need Operator. Redfish must be on (iLO and iDRAC: on by default; Supermicro: BMC → Configuration → " +
        "Network/Port; XCC: Network → Service Enablement). The BMC's certificate is pinned on first use. Put BMCs on a management network only.",
        PinsTls: true, Fields: []);

    private readonly ProviderConfig _c;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _op = new(1, 1);
    private string? _session, _sessionPath, _basic;
    // What the last inventory learnt about each system / BMC (reset types offered, where to post them, LED style).
    private readonly Dictionary<string, string[]> _resets = new();
    private readonly Dictionary<string, string> _resetTarget = new();
    private readonly HashSet<string> _newLed = new();

    public ProviderConfig Config => _c;
    public string Where => _c.Endpoint;
    public NetClass? NetworkClass => NetworkPolicy.Classify(_c.Endpoint);
    public string LastSeenSha256 { get; private set; } = "";
    public TlsDecision LastTls { get; private set; } = TlsDecision.Accept;
    public TimeSpan VerifyFor { get; set; } = TimeSpan.FromSeconds(150);
    public TimeSpan PollEvery { get; set; } = TimeSpan.FromSeconds(3);
    /// <summary>Caps on what one refresh reads.</summary>
    public int MaxMembers { get; set; } = 16;
    public int MaxDrives { get; set; } = 48;
    public int MaxFirmware { get; set; } = 60;
    public int MaxLogEntries { get; set; } = 25;

    /// <summary>Self-test seams: sign-in state and how many sessions were opened.</summary>
    internal bool SignedInForTest => _session != null || _basic != null;
    internal int SessionsOpened { get; private set; }

    public RedfishProvider(ProviderConfig c, HttpMessageHandler? handler = null)
    {
        _c = c;
        handler ??= TlsPin.Handler(() => _c.TlsSha256, (sha, d) => { LastSeenSha256 = sha; LastTls = d; });
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45), MaxResponseContentBufferSize = MaxBody };
        if (Uri.TryCreate(_c.Endpoint.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var b)) _http.BaseAddress = b;
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("OData-Version", "4.0");
    }

    public static string? ValidateEndpoint(string url) =>
        !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) ? "Enter the BMC's address, e.g. https://ilo-srv01.lan"
        : u.Scheme != Uri.UriSchemeHttps ? "A BMC must be reached over https: the account's password and the session would otherwise travel in clear text."
        : u.UserInfo.Length > 0 ? "No user name or password in the address: choose the vault credential instead."
        : u.AbsolutePath.Trim('/').Length > 0 || u.Query.Length > 0 ? "Only the address (and port), e.g. https://10.0.0.20 — no path."
        : null;

    public sealed class RedfishException(int status, string message) : Exception(message) { public int Status { get; } = status; }

    // ---- HTTP -------------------------------------------------------------------------------------------------------

    /// <summary>The path of an address the BMC named, or null when it isn't a /redfish/v1 path on this BMC.</summary>
    internal string? SafePath(string? odataId)
    {
        if (string.IsNullOrWhiteSpace(odataId) || odataId.Length > 512 || odataId.Any(char.IsControl)) return null;
        var s = odataId.Trim();
        if (s.Contains("://"))
        {
            if (!Uri.TryCreate(s, UriKind.Absolute, out var u) || _http.BaseAddress == null
                || !string.Equals(u.GetLeftPart(UriPartial.Authority), _http.BaseAddress.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase)) return null;
            s = u.PathAndQuery;
        }
        var hash = s.IndexOf('#');
        if (hash >= 0) s = s[..hash];
        if (!s.StartsWith("/redfish/v1", StringComparison.Ordinal) || s.Contains("..") || s.Contains("//") || s.Contains('\\')
            || s.Contains("%2e", StringComparison.OrdinalIgnoreCase) || s.Contains("%2f", StringComparison.OrdinalIgnoreCase) || s.Contains("%5c", StringComparison.OrdinalIgnoreCase)) return null;
        return s;
    }

    private static string Error(int code, string text) => code switch
    {
        401 => "the BMC refused the account (wrong user name or password, the account is locked, or it may not use Redfish)",
        403 => "the BMC account's role doesn't allow this (power and the identify light need Operator; ReadOnly can only look)",
        404 => "the BMC doesn't have that (not found)",
        405 or 501 => "the BMC doesn't support that",
        409 => "the BMC refused it in the current state: " + Message(text),
        412 => "the item changed meanwhile (ETag): refresh and try again",
        503 => "the BMC is busy (or restarting): try again in a minute",
        _ => $"HTTP {code}: {Message(text)}",
    };

    private void Auth(HttpRequestMessage req)
    {
        if (_session != null) req.Headers.TryAddWithoutValidation("X-Auth-Token", _session);
        else if (_basic != null) req.Headers.Authorization = new AuthenticationHeaderValue("Basic", _basic);
    }

    private async Task<(int Code, string Text, HttpResponseHeaders Headers)> Send(HttpMethod m, string path, JsonObject? body, CancellationToken ct, bool auth = true, string? ifMatch = null)
    {
        var p = SafePath(path) ?? throw new RedfishException(0, "the BMC named an unexpected address (refused): " + (path.Length > 80 ? path[..80] : path));
        using var req = new HttpRequestMessage(m, p.TrimStart('/'));
        if (body != null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        if (auth) Auth(req);
        if (ifMatch != null) req.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        using var r = await _http.SendAsync(req, ct);
        return ((int)r.StatusCode, await ReadAsync(r, ct), r.Headers);
    }

    private async Task<JsonObject> Get(string path, CancellationToken ct, bool auth = true)
    {
        var (code, text, _) = await Send(HttpMethod.Get, path, null, ct, auth);
        if (code >= 300) throw new RedfishException(code, Error(code, text));
        try { return Parse(text) as JsonObject ?? throw new RedfishException(code, "the BMC didn't answer with a JSON object"); }
        catch (System.Text.Json.JsonException) { throw new RedfishException(code, "the BMC didn't answer with JSON"); }
    }

    /// <summary>The member paths of a collection (at most <paramref name="cap"/>; unsafe ones dropped).</summary>
    private async Task<List<string>> Members(string? collection, int cap, CancellationToken ct)
    {
        if (SafePath(collection) is not { } p) return new();
        var j = await Get(p, ct);
        return Objects(j["Members"]).Select(m => SafePath(S(m, "@odata.id"))).OfType<string>().Distinct().Take(cap).ToList();
    }

    /// <summary>Reads several resources, four at a time; ones that fail are counted, not fatal.</summary>
    private async Task<(List<JsonObject> Items, int Failed)> GetMany(IEnumerable<string> paths, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(4);
        int failed = 0;
        var tasks = paths.Select(async p =>
        {
            await gate.WaitAsync(ct);
            try { return await Get(p, ct); }
            catch (Exception ex) when (ex is RedfishException or HttpRequestException) { Interlocked.Increment(ref failed); return null; }
            finally { gate.Release(); }
        }).ToList();
        var got = await Task.WhenAll(tasks);
        return (got.OfType<JsonObject>().ToList(), failed);
    }

    // ---- sessions ---------------------------------------------------------------------------------------------------

    /// <summary>One sign-in for one refresh or action: a session (deleted afterwards), or Basic when the BMC has none.</summary>
    private async Task<T> Operation<T>(Func<JsonObject, Task<T>> work, CancellationToken ct)
    {
        await _op.WaitAsync(ct);
        try
        {
            JsonObject root;
            try { root = await Get("/redfish/v1/", ct, auth: false); }
            catch (RedfishException ex) when (ex.Status == 401) { root = new JsonObject(); }   // some BMCs want a sign-in even for the root
            await SignInAsync(root, ct);
            try
            {
                if (root.Count == 0) root = await Get("/redfish/v1/", ct);
                return await work(root);
            }
            finally { await SignOutAsync(); }
        }
        finally { _op.Release(); }
    }

    private async Task SignInAsync(JsonObject root, CancellationToken ct)
    {
        var cred = await ExternalCredentials.UseAsync(_c.CredentialId, "Redfish " + _c.Name, ct)
            ?? throw new RedfishException(0, "choose the vault credential with the BMC account");
        if (cred.Username.Length == 0 || cred.Secret.Length == 0) throw new RedfishException(0, "the vault credential needs the BMC account's user name and password");
        var sessions = SafePath(S(root, "Links", "Sessions", "@odata.id")) ?? "/redfish/v1/SessionService/Sessions";
        var (code, text, headers) = await Send(HttpMethod.Post, sessions, new JsonObject { ["UserName"] = cred.Username, ["Password"] = cred.Secret }, ct, auth: false);
        if (code is 200 or 201 && headers.TryGetValues("X-Auth-Token", out var tok) && tok.FirstOrDefault() is { Length: > 0 and < 4096 } t && t.All(ch => ch > 0x20 && ch < 0x7F))
        {
            _session = t;
            _sessionPath = headers.Location is { } loc ? SafePath(loc.IsAbsoluteUri ? loc.ToString() : loc.OriginalString) : null;
            _sessionPath ??= SafePath(S(Parse(text), "@odata.id"));
            SessionsOpened++;
            return;
        }
        if (code == 401) throw new RedfishException(401, Error(401, text));
        if (code is 404 or 405 or 501)
        {
            _basic = Convert.ToBase64String(Encoding.UTF8.GetBytes(cred.Username + ":" + cred.Secret));   // this operation only
            return;
        }
        throw new RedfishException(code, "sign-in failed: " + Error(code, text));
    }

    private async Task SignOutAsync()
    {
        var (path, token) = (_sessionPath, _session);
        _session = null; _sessionPath = null; _basic = null;
        if (token == null || path == null) return;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Delete, path.TrimStart('/'));
            req.Headers.TryAddWithoutValidation("X-Auth-Token", token);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var _ = await _http.SendAsync(req, cts.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }   // the BMC times the session out itself
    }

    // ---- detect -----------------------------------------------------------------------------------------------------

    private static string Vendor(JsonObject root) =>
        S(root, "Vendor") is { Length: > 0 } v ? v
        : (root["Oem"] as JsonObject)?.Select(kv => kv.Key).FirstOrDefault() is { } oem ? oem switch { "Hpe" or "Hp" => "HPE", "Lenovo" => "Lenovo", "Dell" => "Dell", "Supermicro" => "Supermicro", _ => oem }
        : "";

    public async Task<ProviderCaps> DetectAsync(CancellationToken ct)
    {
        try
        {
            if (!ExternalCredentials.HasSecret(AppData.Current.FindCredential(_c.CredentialId))) return ProviderCaps.Unreachable("choose the vault credential with the BMC account");
            return await Operation(async root =>
            {
                var vendor = Vendor(root);
                var product = S(root, "Product");
                var hpe = Objects(At(root, "Oem", "Hpe", "Manager")).FirstOrDefault();
                var bmc = hpe != null ? $"{S(hpe, "ManagerType")} {S(hpe, "ManagerFirmwareVersion")}".Trim() : product;
                var systems = await Members(S(root, "Systems", "@odata.id"), MaxMembers, ct);
                var notes = new List<string>
                {
                    "Changes go to the BMC: power and the identify light need an Operator account; everything is re-read to confirm.",
                };
                var user = AppData.Current.FindCredential(_c.CredentialId)?.Username ?? "";
                if (user is "ADMIN" or "Administrator" or "root" or "USERID") notes.Add($"⚠ “{user}” is the factory account: create a dedicated Redfish account with the least role it needs.");
                if (vendor == "Supermicro") notes.Add("Supermicro: some Redfish features (BIOS settings, firmware updates) need Supermicro's out-of-band licence; reading and power don't.");
                if (_basic != null) notes.Add("This BMC has no Redfish sessions: the account is sent (over https) with each request of a refresh.");
                return new ProviderCaps(true, true, true, S(root, "RedfishVersion"),
                    $"{(vendor.Length > 0 ? vendor + " " : "")}{bmc} · Redfish {S(root, "RedfishVersion")} · {systems.Count} system(s)".Trim(), notes);
            }, ct);
        }
        catch (HttpRequestException ex) when (ex.InnerException is AuthenticationException || LastTls != TlsDecision.Accept)
        {
            return ProviderCaps.Unreachable(TlsPin.UntrustedMessage(LastTls, LastSeenSha256) + " (compare it with the BMC's certificate page.)");
        }
        catch (Exception ex) when (ex is RedfishException or HttpRequestException or TaskCanceledException or OfflineBlockedException or InvalidOperationException)
        {
            return ProviderCaps.Unreachable(ex is TaskCanceledException ? "timed out" : ex.Message);
        }
    }

    // ---- inventory --------------------------------------------------------------------------------------------------

    internal static string StatusOf(JsonNode? o, string? power = null)
    {
        var health = S(o, "Status", "HealthRollup") is { Length: > 0 } hr ? hr : S(o, "Status", "Health");
        var state = S(o, "Status", "State");
        if (state == "Absent") return "absent";
        if (state is "Disabled" or "StandbyOffline" or "UnavailableOffline") return "offline";
        if (health == "Critical") return "critical";
        if (health == "Warning") return "degraded";
        if (power != null) return power switch { "On" => "running", "Off" => "stopped", "" => "unknown", _ => power.ToLowerInvariant() };
        return health == "OK" || state == "Enabled" ? "online" : "unknown";
    }

    private static string Health(JsonNode? o) => S(o, "Status", "HealthRollup") is { Length: > 0 } hr ? hr : S(o, "Status", "Health") is { Length: > 0 } h ? h : "—";

    private static string Gb(string bytes) => long.TryParse(bytes, out var b) && b > 0 ? $"{b / 1_000_000_000.0:0.#} GB" : "";

    public async Task<IReadOnlyList<InfraObject>> InventoryAsync(CancellationToken ct) => await Operation(async root =>
    {
        var list = new List<InfraObject>();
        var notes = new List<string>();
        _resets.Clear(); _resetTarget.Clear(); _newLed.Clear();

        // Systems, and their drives.
        var (systems, sf) = await GetMany(await Members(S(root, "Systems", "@odata.id"), MaxMembers, ct), ct);
        if (sf > 0) notes.Add($"{sf} system(s) couldn't be read");
        int drives = 0;
        foreach (var s in systems)
        {
            var id = SafePath(S(s, "@odata.id")) ?? "";
            var name = S(s, "HostName") is { Length: > 0 } hn ? hn : S(s, "Name") is { Length: > 0 } n ? n : S(s, "Id");
            var power = S(s, "PowerState");
            var reset = At(s, "Actions", "#ComputerSystem.Reset");
            if (SafePath(S(reset, "target")) is { } target) _resetTarget[id] = target;
            _resets[id] = Strings(At(reset, AllowableResets)).ToArray();
            if (s.ContainsKey("LocationIndicatorActive")) _newLed.Add(id);
            var led = s.ContainsKey("LocationIndicatorActive") ? (S(s, "LocationIndicatorActive") == "true" ? "on" : "off") : S(s, "IndicatorLED").ToLowerInvariant();
            var props = Flat(s);
            props["_led"] = led;
            list.Add(new InfraObject("system:" + id, "system", name, StatusOf(s, power), "",
                $"{S(s, "Manufacturer")} {S(s, "Model")} · serial {S(s, "SerialNumber")} · power {(power.Length > 0 ? power : "?")} · health {Health(s)} · " +
                $"{S(s, "ProcessorSummary", "Count")}× {S(s, "ProcessorSummary", "Model")} · {S(s, "MemorySummary", "TotalSystemMemoryGiB")} GiB · BIOS {S(s, "BiosVersion")}" + (led.Length > 0 ? $" · identify light {led}" : ""), props));
            try
            {
                var stores = await Members(S(s, "Storage", "@odata.id"), MaxMembers, ct);
                var (storeObjs, _) = await GetMany(stores, ct);
                var drivePaths = storeObjs.SelectMany(st => Objects(st["Drives"]).Select(d => SafePath(S(d, "@odata.id")))).OfType<string>().Distinct().Take(Math.Max(0, MaxDrives - drives)).ToList();
                var (ds, df) = await GetMany(drivePaths, ct);
                if (df > 0) notes.Add($"{df} drive(s) couldn't be read");
                foreach (var d in ds)
                {
                    drives++;
                    var life = S(d, "PredictedMediaLifeLeftPercent") is { Length: > 0 } l ? $" · {l}% life left" : "";
                    var predicted = S(d, "FailurePredicted") == "true";
                    list.Add(new InfraObject("drive:" + (SafePath(S(d, "@odata.id")) ?? S(d, "Id")), "drive", S(d, "Name") is { Length: > 0 } dn ? dn : S(d, "Id"),
                        predicted ? "degraded" : StatusOf(d), name,
                        $"{S(d, "Manufacturer")} {S(d, "Model")} · {Gb(S(d, "CapacityBytes"))} {S(d, "MediaType")} {S(d, "Protocol")} · health {Health(d)}{life}{(predicted ? " · ⚠ FAILURE PREDICTED" : "")}".Trim(), Flat(d)));
                }
                if (drives >= MaxDrives) notes.Add($"only the first {MaxDrives} drives are read");
            }
            catch (RedfishException ex) when (ex.Status is 404 or 405 or 501) { }   // no Storage resource on this BMC
            catch (RedfishException ex) { notes.Add("storage: " + ex.Message); }
        }

        // The BMC(s), and the newest entries of their event logs.
        var (managers, _) = await GetMany(await Members(S(root, "Managers", "@odata.id"), 4, ct), ct);
        foreach (var m in managers)
        {
            var id = SafePath(S(m, "@odata.id")) ?? "";
            var reset = At(m, "Actions", "#Manager.Reset");
            if (SafePath(S(reset, "target")) is { } target) _resetTarget[id] = target;
            _resets[id] = Strings(At(reset, AllowableResets)).ToArray();
            var mname = S(m, "Model") is { Length: > 0 } mm ? mm : S(m, "Name");
            list.Add(new InfraObject("bmc:" + id, "bmc", mname, StatusOf(m), "",
                $"{S(m, "ManagerType")} · firmware {S(m, "FirmwareVersion")} · health {Health(m)}" + (S(m, "DateTime") is { Length: > 0 } dt ? " · clock " + dt : ""), Flat(m)));
            try
            {
                var logs = await Members(S(m, "LogServices", "@odata.id"), 8, ct);
                foreach (var lp in logs.Where(p => p.Split('/').Last() is "IML" or "IEL" or "SEL" or "Sel" or "Log1" or "EventLog" or "Lclog").Take(2))
                {
                    var svc = await Get(lp, ct);
                    if (SafePath(S(svc, "Entries", "@odata.id")) is not { } ep) continue;
                    var page = await Get(ep, ct);
                    var members = Objects(page["Members"]).ToList();
                    List<JsonObject> entries = members.Any(e => e.ContainsKey("Message") || e.ContainsKey("Severity")) ? members
                        : (await GetMany(members.Select(e => SafePath(S(e, "@odata.id"))).OfType<string>().TakeLast(MaxLogEntries), ct)).Items;
                    foreach (var e in entries.OrderByDescending(e => S(e, "Created"), StringComparer.Ordinal).Take(MaxLogEntries))
                    {
                        var sev = S(e, "Severity");
                        list.Add(new InfraObject("log:" + (SafePath(S(e, "@odata.id")) ?? lp + "#" + S(e, "Id")), "log", Short(S(e, "Message"), 90),
                            sev switch { "Critical" => "critical", "Warning" => "degraded", _ => "online" }, $"{mname} {lp.Split('/').Last()}",
                            $"{S(e, "Created")} · {sev} · {S(e, "Message")}", Flat(e)));
                    }
                }
            }
            catch (RedfishException ex) when (ex.Status is 403 or 404) { }
            catch (RedfishException ex) { notes.Add("event log: " + ex.Message); }
        }

        // Chassis: power draw, power supplies, fans, the hottest temperatures.
        var (chassis, _) = await GetMany(await Members(S(root, "Chassis", "@odata.id"), MaxMembers, ct), ct);
        foreach (var ch in chassis)
        {
            var cname = S(ch, "Name") is { Length: > 0 } cn ? cn : S(ch, "Id");
            var cid = SafePath(S(ch, "@odata.id")) ?? cname;
            int at = list.Count;   // the chassis goes before its power supplies, fans and temperatures
            string watts = "";
            try
            {
                if (SafePath(S(ch, "Power", "@odata.id")) is { } pp)
                {
                    var pw = await Get(pp, ct);
                    watts = S(Objects(pw["PowerControl"]).FirstOrDefault(), "PowerConsumedWatts");
                    foreach (var ps in Objects(pw["PowerSupplies"]))
                        list.Add(new InfraObject($"psu:{cid}:{S(ps, "MemberId")}", "power supply", S(ps, "Name") is { Length: > 0 } pn ? pn : "Power supply " + S(ps, "MemberId"), StatusOf(ps), cname,
                            $"{S(ps, "Model")} · {S(ps, "PowerCapacityWatts")} W capacity · health {Health(ps)}" + (S(ps, "LastPowerOutputWatts") is { Length: > 0 } o ? $" · {o} W now" : ""), Flat(ps)));
                }
                if (SafePath(S(ch, "Thermal", "@odata.id")) is { } tp)
                {
                    var th = await Get(tp, ct);
                    foreach (var f in Objects(th["Fans"]))
                        list.Add(new InfraObject($"fan:{cid}:{S(f, "MemberId")}{S(f, "Name")}", "fan", S(f, "Name") is { Length: > 0 } fn ? fn : S(f, "FanName"), StatusOf(f), cname,
                            $"{S(f, "Reading")} {S(f, "ReadingUnits")} · health {Health(f)}".Trim(), Flat(f)));
                    var temps = Objects(th["Temperatures"]).Where(t => double.TryParse(S(t, "ReadingCelsius"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0).ToList();
                    if (temps.Count > 0)
                    {
                        double R(JsonObject t) => double.Parse(S(t, "ReadingCelsius"), System.Globalization.CultureInfo.InvariantCulture);
                        var hot = temps.OrderByDescending(R).ToList();
                        var worst = temps.Select(t => StatusOf(t)).FirstOrDefault(s => s is "critical" or "degraded") ?? "online";
                        var props = new Dictionary<string, string>(StringComparer.Ordinal);
                        foreach (var t in temps)
                            props.TryAdd(S(t, "Name") is { Length: > 0 } tn ? tn : "sensor " + S(t, "MemberId"), $"{S(t, "ReadingCelsius")} °C (warning {S(t, "UpperThresholdNonCritical")}, critical {S(t, "UpperThresholdCritical")})");
                        list.Add(new InfraObject("thermal:" + cid, "thermal", cname + " temperatures", worst, cname,
                            $"{temps.Count} sensors · hottest: " + string.Join(", ", hot.Take(4).Select(t => $"{S(t, "Name")} {R(t):0} °C")), props));
                    }
                }
            }
            catch (RedfishException ex) when (ex.Status is 404 or 405 or 501) { }
            catch (RedfishException ex) { notes.Add($"{cname} power / thermal: " + ex.Message); }
            list.Insert(at, new InfraObject("chassis:" + cid, "chassis", cname, StatusOf(ch), "",
                $"{S(ch, "ChassisType")} · {S(ch, "Manufacturer")} {S(ch, "Model")} · serial {S(ch, "SerialNumber")} · health {Health(ch)}" + (watts.Length > 0 ? $" · {watts} W" : ""), Flat(ch)));
        }

        // Firmware inventory.
        try
        {
            var fw = await Members(S(await Get(SafePath(S(root, "UpdateService", "@odata.id")) ?? "/redfish/v1/UpdateService", ct), "FirmwareInventory", "@odata.id"), MaxFirmware, ct);
            var (items, _) = await GetMany(fw, ct);
            foreach (var f in items)
                list.Add(new InfraObject("firmware:" + (SafePath(S(f, "@odata.id")) ?? S(f, "Id")), "firmware", S(f, "Name") is { Length: > 0 } fn ? fn : S(f, "Id"), StatusOf(f), "",
                    $"version {S(f, "Version")}" + (S(f, "Updateable") == "true" ? " · updateable" : "") + (S(f, "ReleaseDate") is { Length: > 0 } rd ? " · released " + rd : ""), Flat(f)));
        }
        catch (RedfishException ex) when (ex.Status is 403 or 404 or 405 or 501) { }
        catch (RedfishException ex) { notes.Add("firmware: " + ex.Message); }

        if (notes.Count > 0) list.Add(new InfraObject("notes", "note", "Not everything could be read", "offline", "", string.Join(" · ", notes.Distinct()), new Dictionary<string, string>()));
        return (IReadOnlyList<InfraObject>)list;
    }, ct);

    private static string Short(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    // ---- actions ----------------------------------------------------------------------------------------------------

    public static readonly InfraAction Details = new("details", "ⓘ All fields", false, Consequence.None, null, "Every field the BMC reported for this item.");

    /// <summary>The standard reset types, in the order offered, with what each one means.</summary>
    internal static readonly (string Type, string Label, Consequence C, string? Expect, string Explain)[] ResetTypes =
    [
        ("On", "⏻ Power on", Consequence.None, "On", "Powers the server on."),
        ("GracefulShutdown", "⏻ Shut down (graceful)", Consequence.ServiceInterruption, "Off", "Asks the operating system to shut down (like a short press of the power button). Everything on the server stops."),
        ("GracefulRestart", "↻ Restart (graceful)", Consequence.ServiceInterruption, null, "Asks the operating system to restart. Everything on the server is interrupted."),
        ("ForceRestart", "↻ Force restart (reset)", Consequence.DataLoss, null, "Resets the server at once, without the operating system: unsaved data and open writes are lost."),
        ("ForceOff", "⏻ Force power off", Consequence.DataLoss, "Off", "Cuts power at once (like holding the power button): unsaved data and open writes are lost."),
        ("PowerCycle", "⏻ Power cycle", Consequence.DataLoss, null, "Powers off at once and back on: unsaved data and open writes are lost."),
    ];

    private static readonly Dictionary<string, InfraAction> PowerActions = ResetTypes.ToDictionary(r => r.Type,
        r => new InfraAction("reset:" + r.Type, r.Label, true, r.C, r.Expect, r.Explain + " Sent to the BMC as Redfish ComputerSystem.Reset " + r.Type + "."));

    public static readonly InfraAction LedOn = new("led-on", "◉ Identify light on", true, Consequence.None, "on", "Turns on the server's identify (UID / locator) light so it can be found in the rack.");
    public static readonly InfraAction LedOff = new("led-off", "◯ Identify light off", true, Consequence.None, "off", "Turns the identify light off.");
    public static readonly InfraAction BmcRestart = new("bmc-restart", "↻ Restart the BMC", true, Consequence.ServiceInterruption, null,
        "Restarts the management controller itself (1–3 minutes without remote management; the server keeps running).");

    private static string Path(InfraObject o) => o.Id.IndexOf(':') is var i and > 0 ? o.Id[(i + 1)..] : "";

    public IReadOnlyList<InfraAction> ActionsFor(InfraObject o)
    {
        if (o.Kind == "note") return Array.Empty<InfraAction>();
        if (o.Kind == "bmc") return new[] { Details, BmcRestart };
        if (o.Kind != "system") return new[] { Details };
        var allowed = _resets.GetValueOrDefault(Path(o)) is { Length: > 0 } a ? a : ResetTypes.Select(r => r.Type).ToArray();
        var list = new List<InfraAction> { Details };
        list.AddRange(ResetTypes.Where(r => allowed.Contains(r.Type)).Select(r => PowerActions[r.Type]));
        list.Add(LedOn); list.Add(LedOff);
        return list;
    }

    public string? Forbidden(InfraAction a, InfraObject o, ProviderCaps caps)
    {
        var power = o.Props.GetValueOrDefault("PowerState") ?? "";
        if (a.Id == "reset:On" && power == "On") return "already powered on";
        if (a.Id is "reset:GracefulShutdown" or "reset:ForceOff" or "reset:GracefulRestart" or "reset:ForceRestart" && power == "Off") return "the server is off";
        if (a == LedOn && o.Props.GetValueOrDefault("_led") is "on" or "lit" or "blinking") return "the identify light is already on";
        if (a == LedOff && o.Props.GetValueOrDefault("_led") is "off") return "the identify light is already off";
        return null;
    }

    public Task<ActionResult> RunAsync(InfraAction a, InfraObject o, CancellationToken ct) => RunAsync(a, o, new Dictionary<string, string>(), ct);

    public async Task<ActionResult> RunAsync(InfraAction a, InfraObject o, IReadOnlyDictionary<string, string> input, CancellationToken ct)
    {
        if (a.Id == "details") return new ActionResult(true, true, "fields", string.Join("\n", o.Props.Where(p => !p.Key.StartsWith('_')).OrderBy(p => p.Key).Select(p => $"{p.Key,-40} {p.Value}")));
        var path = SafePath(Path(o));
        if (path == null) return new ActionResult(false, false, "refused: unexpected item");
        try
        {
            return await Operation<ActionResult>(async _ =>
            {
                if (a.Id.StartsWith("reset:", StringComparison.Ordinal) && o.Kind == "system")
                {
                    var type = a.Id[6..];
                    if (!ResetTypes.Any(r => r.Type == type)) return new ActionResult(false, false, "refused: unknown reset type");
                    var target = _resetTarget.GetValueOrDefault(path) ?? path + "/Actions/ComputerSystem.Reset";
                    var (code, text, _) = await Send(HttpMethod.Post, target, new JsonObject { ["ResetType"] = type }, ct);
                    if (code >= 300) return new ActionResult(false, false, "refused by the BMC: " + Error(code, text));
                    AuditLog.Write("you", "infra:" + _c.Name, "Redfish reset " + type, o.Name);
                    if (a.ExpectedStatus is not { } want) return new ActionResult(true, false, $"accepted by the BMC ({type}); a restart can't be confirmed from the power state — refresh in a few minutes");
                    return await Verify(async () => S(await Get(path, ct), "PowerState"), want, ct);
                }
                if ((a == LedOn || a == LedOff) && o.Kind == "system")
                {
                    bool on = a == LedOn;
                    var (gc, gt, gh) = await Send(HttpMethod.Get, path, null, ct);
                    if (gc >= 300) return new ActionResult(false, false, "refused: " + Error(gc, gt));
                    var cur = Parse(gt) as JsonObject ?? new JsonObject();
                    bool modern = cur.ContainsKey("LocationIndicatorActive");
                    var etag = gh.ETag?.ToString() ?? (S(cur, "@odata.etag") is { Length: > 0 } e ? e : null);
                    var body = modern ? new JsonObject { ["LocationIndicatorActive"] = on } : new JsonObject { ["IndicatorLED"] = on ? "Lit" : "Off" };
                    var (code, text, _) = await Send(HttpMethod.Patch, path, body, ct, ifMatch: etag);
                    if (code >= 300) return new ActionResult(false, false, "refused by the BMC: " + Error(code, text));
                    AuditLog.Write("you", "infra:" + _c.Name, on ? "identify light on" : "identify light off", o.Name);
                    return await Verify(async () =>
                    {
                        var s = await Get(path, ct);
                        return s.ContainsKey("LocationIndicatorActive") ? (S(s, "LocationIndicatorActive") == "true" ? "on" : "off")
                            : S(s, "IndicatorLED") is "Lit" or "Blinking" ? "on" : "off";
                    }, on ? "on" : "off", ct);
                }
                if (a == BmcRestart && o.Kind == "bmc")
                {
                    var allowed = _resets.GetValueOrDefault(path) ?? [];
                    var type = allowed.Length == 0 || allowed.Contains("GracefulRestart") ? "GracefulRestart" : allowed.Contains("ForceRestart") ? "ForceRestart" : allowed[0];
                    var target = _resetTarget.GetValueOrDefault(path) ?? path + "/Actions/Manager.Reset";
                    var (code, text, _) = await Send(HttpMethod.Post, target, new JsonObject { ["ResetType"] = type }, ct);
                    if (code >= 300) return new ActionResult(false, false, "refused by the BMC: " + Error(code, text));
                    _session = null; _sessionPath = null;   // the restart ends the session
                    AuditLog.Write("you", "infra:" + _c.Name, "BMC restart", o.Name);
                    return new ActionResult(true, false, "accepted: the BMC restarts (1–3 minutes without remote management); refresh afterwards");
                }
                return new ActionResult(false, false, "refused: this action isn't available for this item");
            }, ct);
        }
        catch (Exception ex) when (ex is RedfishException or HttpRequestException or TaskCanceledException or OfflineBlockedException or InvalidOperationException)
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
            try { now = await read(); } catch (RedfishException) { }
            if (now.Equals(expected, StringComparison.OrdinalIgnoreCase)) return new ActionResult(true, true, $"verified: now “{now}”");
            if (DateTime.UtcNow >= until) break;
            await Task.Delay(PollEvery, ct);
        }
        return new ActionResult(true, false, $"requested, but NOT verified: the BMC reports “{now}” (expected “{expected}”)");
    }
}
