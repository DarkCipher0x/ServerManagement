using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using DcxManager.Infra;
using DcxManager.Services;

namespace DcxManager;

/// <summary>
/// ADR-094 (0.6.16, DarkCipher0x: "integrate unifi management with their API key … a very mature API integration"). The
/// UniFi Network provider against a fake console speaking the Network Integration API (paths discovered: a self-hosted
/// server without /proxy/network; the Cloud Connector's second form), and the Site Manager provider against a fake
/// api.ui.com. Inventory with paging and caps, every change verified by re-reading (restart by a new uptime, PoE back on,
/// adoption, guest access, vouchers, WiFi / network PUT without read-only fields, firewall PATCH), and the guards: https only,
/// the key only in its header, ids checked before any request, redirects refused, 429 waited out once. Demo data, no network.
/// </summary>
public static class UniFiTests
{
    private const string Key = "unifi-key-fake-qx7";
    private const string Site = "11111111-aaaa-4bbb-8ccc-000000000001";
    private const string Sw = "22222222-aaaa-4bbb-8ccc-000000000001", Ap = "22222222-aaaa-4bbb-8ccc-000000000002", Gw = "22222222-aaaa-4bbb-8ccc-000000000003";
    private const string Guest = "33333333-aaaa-4bbb-8ccc-000000000001";
    private const string Vlan = "44444444-aaaa-4bbb-8ccc-000000000002", DefNet = "44444444-aaaa-4bbb-8ccc-000000000001";
    private const string Wifi = "55555555-aaaa-4bbb-8ccc-000000000001";
    private const string FwUser = "66666666-aaaa-4bbb-8ccc-000000000001", FwSys = "66666666-aaaa-4bbb-8ccc-000000000002";

    /// <summary>A UniFi console (Network Integration API) with state; answers only under <see cref="Root"/>.</summary>
    private sealed class FakeConsole : HttpMessageHandler
    {
        public string Root = "/integration/v1";
        public readonly List<string> Urls = new();
        public int WithoutKey, Requests;
        public long Uptime = 864000;
        public bool Restarted;
        public string PoeState = "UP";
        public bool GuestAuthorized;
        public bool WifiOn = true, VlanOn = true, FwOn = true;
        public JsonObject? LastPut;
        public readonly List<JsonObject> Vouchers = new();
        public readonly List<string> Adopted = new();
        public bool SwPresent = true;
        public bool Redirect;
        public int ClientCount = 230;

        static HttpResponseMessage J(int code, JsonNode body) => new((HttpStatusCode)code) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        static JsonObject Page(IEnumerable<JsonNode> all, string query)
        {
            var q = System.Web.HttpUtility.ParseQueryString(query);
            int off = int.TryParse(q["offset"], out var o) ? o : 0, lim = int.TryParse(q["limit"], out var l) ? l : 25;
            var list = all.ToList();
            var page = list.Skip(off).Take(lim).Select(x => x.DeepClone()).ToArray();
            return new JsonObject { ["offset"] = off, ["limit"] = lim, ["count"] = page.Length, ["totalCount"] = list.Count, ["data"] = new JsonArray(page) };
        }
        static JsonObject Dev(string id, string name, string model, string state, string[] feats) => new()
        {
            ["id"] = id, ["name"] = name, ["model"] = model, ["macAddress"] = "f4:e2:c6:00:00:" + id[^2..], ["ipAddress"] = "192.0.2." + (10 + id[^1] - '0'), ["state"] = state,
            ["firmwareVersion"] = "7.1.66", ["firmwareUpdatable"] = id == Ap, ["supported"] = true, ["features"] = new JsonArray(feats.Select(f => (JsonNode)f).ToArray()), ["interfaces"] = new JsonArray(),
        };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => Answer(r, ct);

        public async Task<HttpResponseMessage> Answer(HttpRequestMessage r, CancellationToken ct)
        {
            Requests++;
            Urls.Add(r.RequestUri!.AbsoluteUri);
            if (!r.Headers.TryGetValues("X-API-KEY", out var k) || k.Single() != Key) { WithoutKey++; return J(401, new JsonObject { ["statusCode"] = 401, ["statusName"] = "UNAUTHORIZED", ["message"] = "Missing API key" }); }
            if (Redirect) { var m = new HttpResponseMessage(HttpStatusCode.Found); m.Headers.Location = new Uri("https://unifi.example.test/login"); return m; }
            var path = r.RequestUri.AbsolutePath;
            if (!path.StartsWith(Root, StringComparison.Ordinal)) return J(404, new JsonObject { ["message"] = "Not Found" });
            path = path[Root.Length..];
            var q = r.RequestUri.Query;
            var body = r.Content == null ? null : JsonNode.Parse(await r.Content.ReadAsStringAsync(ct)) as JsonObject;
            var m2 = r.Method.Method;
            string S = $"/sites/{Site}";
            switch (m2, path)
            {
                case ("GET", "/info"): return J(200, new JsonObject { ["applicationVersion"] = "10.0.162" });
                case ("GET", "/sites"): return J(200, Page(new[] { new JsonObject { ["id"] = Site, ["internalReference"] = "default", ["name"] = "Lab (demo)" } }, q));
                case ("GET", var p) when p == S + "/devices":
                    var devs = new List<JsonNode> { Dev(Ap, "ap-lounge (demo)", "U7PRO", "ONLINE", new[] { "accessPoint" }), Dev(Gw, "gateway (demo)", "UCGMAX", "ONLINE", new[] { "gateway", "switching" }) };
                    if (SwPresent) devs.Insert(0, Dev(Sw, "switch-core (demo)", "USWPRO24POE", "ONLINE", new[] { "switching" }));
                    devs.AddRange(Adopted.Select(mac => (JsonNode)new JsonObject { ["id"] = "22222222-aaaa-4bbb-8ccc-0000000000f9", ["name"] = "new-ap", ["model"] = "U6LR", ["macAddress"] = mac, ["ipAddress"] = "192.0.2.99", ["state"] = "ONLINE", ["firmwareUpdatable"] = false, ["supported"] = true, ["features"] = new JsonArray("accessPoint"), ["interfaces"] = new JsonArray() }));
                    return J(200, Page(devs, q));
                case ("GET", var p) when p == S + $"/devices/{Sw}":
                    if (!SwPresent) return J(404, new JsonObject { ["message"] = "device not found" });
                    var sw = Dev(Sw, "switch-core (demo)", "USWPRO24POE", Restarted && Uptime > 600 ? "OFFLINE" : "ONLINE", new[] { "switching" });
                    sw["interfaces"] = new JsonObject
                    {
                        ["ports"] = new JsonArray(Enumerable.Range(1, 6).Select(i => (JsonNode)new JsonObject
                        {
                            ["idx"] = i, ["connector"] = "RJ45", ["maxSpeedMbps"] = 1000, ["speedMbps"] = 1000, ["state"] = i <= 4 ? "UP" : "DOWN",
                            ["poe"] = i is 3 or 5 ? new JsonObject { ["enabled"] = true, ["standard"] = "802.3at", ["state"] = i == 3 ? PoeState : "UP", ["type"] = 2 } : null,
                        }).ToArray()),
                    };
                    sw["uplink"] = new JsonObject { ["deviceId"] = Gw };
                    if (PoeState == "DOWN") PoeState = "UP";   // power returns on the next read
                    if (Restarted) Uptime = Uptime > 600 ? 30 : Uptime;   // comes back with a new uptime
                    return J(200, sw);
                case ("GET", var p) when p.StartsWith(S + "/devices/") && p.EndsWith("/statistics/latest"):
                    return J(200, new JsonObject { ["uptimeSec"] = p.Contains(Sw) ? Uptime : 3600, ["cpuUtilizationPct"] = 12.5, ["memoryUtilizationPct"] = 41.0, ["loadAverage1Min"] = 0.2, ["interfaces"] = new JsonObject() });
                case ("GET", var p) when p.StartsWith(S + "/devices/"): return J(200, Dev(p[^36..], "dev", "X", "ONLINE", Array.Empty<string>()));
                case ("POST", var p) when p == S + $"/devices/{Sw}/actions":
                    if (body?["action"]?.ToString() != "RESTART") return J(400, new JsonObject { ["statusName"] = "BAD_REQUEST", ["message"] = "unknown action" });
                    Restarted = true; return J(200, new JsonObject());
                case ("POST", var p) when p == S + $"/devices/{Sw}/interfaces/ports/3/actions":
                    if (body?["action"]?.ToString() != "POWER_CYCLE") return J(400, new JsonObject { ["message"] = "unknown action" });
                    PoeState = "DOWN"; return J(200, new JsonObject());
                case ("DELETE", var p) when p == S + $"/devices/{Sw}": SwPresent = false; return J(200, new JsonObject());
                case ("GET", "/pending-devices"): return J(200, Page(Adopted.Count > 0 ? Array.Empty<JsonNode>() : new JsonNode[] { new JsonObject { ["macAddress"] = "f4:e2:c6:00:00:f9", ["model"] = "U6LR", ["ipAddress"] = "192.0.2.99", ["state"] = "PENDING_ADOPTION", ["firmwareVersion"] = "6.6.0", ["firmwareUpdatable"] = true, ["supported"] = true, ["features"] = new JsonArray("accessPoint"), ["adoptionTargetSiteIds"] = new JsonArray(Site) } }, q));
                case ("POST", var p) when p == S + "/devices":
                    if (body?["macAddress"]?.ToString() is not { } mac || body["ignoreDeviceLimit"] is null) return J(400, new JsonObject { ["message"] = "macAddress and ignoreDeviceLimit are required" });
                    Adopted.Add(mac); return J(200, new JsonObject());
                case ("GET", var p) when p == S + "/clients":
                    var clients = Enumerable.Range(0, ClientCount).Select(i => (JsonNode)new JsonObject
                    {
                        ["id"] = i == 0 ? Guest : $"33333333-aaaa-4bbb-8ccc-{i + 1:D12}", ["name"] = i == 0 ? "guest-phone (demo)" : $"client-{i:D3}", ["type"] = i % 2 == 0 ? "WIRELESS" : "WIRED",
                        ["ipAddress"] = $"198.51.100.{i % 250 + 1}", ["macAddress"] = $"02:00:00:00:{i / 256:X2}:{i % 256:X2}", ["uplinkDeviceId"] = Ap, ["connectedAt"] = "2026-10-24T07:00:00Z",
                        ["access"] = i == 0 ? new JsonObject { ["type"] = "GUEST", ["authorized"] = GuestAuthorized } : new JsonObject { ["type"] = "DEFAULT" },
                    });
                    return J(200, Page(clients, q));
                case ("GET", var p) when p == S + $"/clients/{Guest}":
                    return J(200, new JsonObject { ["id"] = Guest, ["name"] = "guest-phone (demo)", ["type"] = "WIRELESS", ["access"] = new JsonObject { ["type"] = "GUEST", ["authorized"] = GuestAuthorized } });
                case ("POST", var p) when p == S + $"/clients/{Guest}/actions":
                    GuestAuthorized = body?["action"]?.ToString() == "AUTHORIZE_GUEST_ACCESS"; return J(200, new JsonObject());
                case ("GET", var p) when p == S + "/networks":
                    return J(200, Page(new JsonNode[]
                    {
                        new JsonObject { ["id"] = DefNet, ["name"] = "Default", ["vlanId"] = 1, ["enabled"] = true, ["default"] = true, ["management"] = "GATEWAY", ["metadata"] = new JsonObject { ["origin"] = "SYSTEM_DEFINED" } },
                        new JsonObject { ["id"] = Vlan, ["name"] = "IoT (demo)", ["vlanId"] = 20, ["enabled"] = VlanOn, ["default"] = false, ["management"] = "GATEWAY", ["metadata"] = new JsonObject { ["origin"] = "USER_DEFINED" } },
                    }, q));
                case ("GET", var p) when p == S + $"/networks/{Vlan}":
                    return J(200, new JsonObject { ["id"] = Vlan, ["name"] = "IoT (demo)", ["vlanId"] = 20, ["enabled"] = VlanOn, ["management"] = "GATEWAY", ["metadata"] = new JsonObject { ["origin"] = "USER_DEFINED" } });
                case ("PUT", var p) when p == S + $"/networks/{Vlan}":
                    LastPut = body; if (body?["id"] != null || body?["metadata"] != null) return J(400, new JsonObject { ["message"] = "read-only field in body" });
                    VlanOn = body?["enabled"]?.GetValue<bool>() == true; return J(200, body!.DeepClone());
                case ("GET", var p) when p == S + "/wifi/broadcasts":
                    return J(200, Page(new JsonNode[] { new JsonObject { ["id"] = Wifi, ["name"] = "Lab-WiFi (demo)", ["type"] = "STANDARD", ["enabled"] = WifiOn, ["metadata"] = new JsonObject { ["origin"] = "USER_DEFINED" }, ["securityConfiguration"] = new JsonObject { ["type"] = "WPA2_WPA3_PERSONAL" } } }, q));
                case ("GET", var p) when p == S + $"/wifi/broadcasts/{Wifi}":
                    return J(200, new JsonObject { ["id"] = Wifi, ["name"] = "Lab-WiFi (demo)", ["type"] = "STANDARD", ["enabled"] = WifiOn, ["metadata"] = new JsonObject { ["origin"] = "USER_DEFINED" }, ["securityConfiguration"] = new JsonObject { ["type"] = "WPA2_WPA3_PERSONAL" } });
                case ("PUT", var p) when p == S + $"/wifi/broadcasts/{Wifi}":
                    LastPut = body; WifiOn = body?["enabled"]?.GetValue<bool>() == true; return J(200, body!.DeepClone());
                case ("GET", var p) when p == S + "/firewall/policies":
                    return J(200, Page(new JsonNode[]
                    {
                        new JsonObject { ["id"] = FwUser, ["name"] = "Block IoT to LAN (demo)", ["enabled"] = FwOn, ["action"] = new JsonObject { ["type"] = "BLOCK" }, ["metadata"] = new JsonObject { ["origin"] = "USER_DEFINED" } },
                        new JsonObject { ["id"] = FwSys, ["name"] = "Allow return traffic", ["enabled"] = true, ["action"] = new JsonObject { ["type"] = "ALLOW" }, ["metadata"] = new JsonObject { ["origin"] = "SYSTEM_DEFINED" } },
                    }, q));
                case ("GET", var p) when p == S + $"/firewall/policies/{FwUser}": return J(200, new JsonObject { ["id"] = FwUser, ["enabled"] = FwOn });
                case ("PATCH", var p) when p == S + $"/firewall/policies/{FwUser}": FwOn = body?["enabled"]?.GetValue<bool>() == true; return J(200, new JsonObject { ["id"] = FwUser, ["enabled"] = FwOn });
                case ("GET", var p) when p == S + "/firewall/zones": return J(200, Page(new JsonNode[] { new JsonObject { ["id"] = "77777777-aaaa-4bbb-8ccc-000000000001", ["name"] = "Internal", ["metadata"] = new JsonObject { ["origin"] = "SYSTEM_DEFINED" } } }, q));
                case ("GET", var p) when p == S + "/hotspot/vouchers": return J(200, Page(Vouchers, q));
                case ("POST", var p) when p == S + "/hotspot/vouchers":
                    if (body?["name"] == null || body["timeLimitMinutes"] == null) return J(400, new JsonObject { ["statusCode"] = 400, ["statusName"] = "BAD_REQUEST", ["message"] = "name and timeLimitMinutes are required" });
                    for (int i = 0; i < body["count"]!.GetValue<long>(); i++)
                        Vouchers.Add(new JsonObject { ["id"] = $"88888888-aaaa-4bbb-8ccc-{Vouchers.Count + 1:D12}", ["code"] = $"{12345 + Vouchers.Count:D5}{67890 + Vouchers.Count:D5}", ["name"] = body["name"]!.ToString(), ["timeLimitMinutes"] = body["timeLimitMinutes"]!.GetValue<long>(), ["authorizedGuestCount"] = 0, ["authorizedGuestLimit"] = body["authorizedGuestLimit"]?.GetValue<long>(), ["createdAt"] = "2026-10-24T08:00:00Z", ["expired"] = false });
                    return J(201, new JsonObject());
                case ("GET", var p) when p.StartsWith(S + "/hotspot/vouchers/"):
                    return Vouchers.Any(v => v["id"]!.ToString() == p[^36..]) ? J(200, Vouchers.First(v => v["id"]!.ToString() == p[^36..]).DeepClone()) : J(404, new JsonObject { ["message"] = "voucher not found" });
                case ("DELETE", var p) when p.StartsWith(S + "/hotspot/vouchers/"): Vouchers.RemoveAll(v => v["id"]!.ToString() == p[^36..]); return J(200, new JsonObject());
            }
            return J(404, new JsonObject { ["statusCode"] = 404, ["statusName"] = "NOT_FOUND", ["message"] = "No route" });
        }
    }

    /// <summary>api.ui.com: Site Manager (paged hosts, a 429 once) and the Cloud Connector (its second path form).</summary>
    private sealed class FakeCloud : HttpMessageHandler
    {
        public readonly List<string> Urls = new();
        public int WithoutKey;
        public bool Limited = true;
        public FakeConsole? Console;
        public string ConsoleId = "";
        static HttpResponseMessage J(int code, JsonNode body) => new((HttpStatusCode)code) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Urls.Add(r.RequestUri!.AbsoluteUri);
            if (r.RequestUri.Host != "api.ui.com") return J(404, new JsonObject());
            var keyOk = (r.Headers.TryGetValues("X-API-Key", out var k) || r.Headers.TryGetValues("X-API-KEY", out k)) && k.Single() == Key;
            if (!keyOk) { WithoutKey++; return J(401, new JsonObject { ["message"] = "unauthorized" }); }
            var path = r.RequestUri.AbsolutePath;
            var connector = $"/v1/connector/consoles/{Uri.EscapeDataString(ConsoleId)}/network";
            if (Console != null && path.StartsWith(connector, StringComparison.Ordinal))
            {
                var inner = new HttpRequestMessage(r.Method, "https://console.internal" + path[connector.Length..] + r.RequestUri.Query) { Content = r.Content };
                inner.Headers.TryAddWithoutValidation("X-API-KEY", Key);
                return await Console.Answer(inner, ct);
            }
            var q = System.Web.HttpUtility.ParseQueryString(r.RequestUri.Query);
            switch (path)
            {
                case "/v1/hosts":
                    if (q["nextToken"] == null)
                        return J(200, new JsonObject { ["data"] = new JsonArray(new JsonObject { ["id"] = "A1B2C3D4E5F60718293A4B5C6D7E8F90:1234567", ["type"] = "console", ["ipAddress"] = "203.0.113.20", ["isBlocked"] = false, ["reportedState"] = new JsonObject { ["hostname"] = "UDM-Pro Lab (demo)", ["version"] = "4.3.6", ["state"] = "connected" } }), ["httpStatusCode"] = 200, ["traceId"] = "t1", ["nextToken"] = "page2" });
                    return J(200, new JsonObject { ["data"] = new JsonArray(new JsonObject { ["id"] = "FFEEDDCCBBAA00998877665544332211:7654321", ["type"] = "console", ["ipAddress"] = "203.0.113.21", ["isBlocked"] = false, ["reportedState"] = new JsonObject { ["hostname"] = "UCG Branch (demo)", ["version"] = "4.2.9", ["state"] = "disconnected" } }), ["httpStatusCode"] = 200, ["traceId"] = "t2" });
                case "/v1/sites":
                    if (Limited) { Limited = false; var m = J(429, new JsonObject { ["message"] = "rate limited" }); m.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero); return m; }
                    return J(200, new JsonObject { ["data"] = new JsonArray(new JsonObject { ["siteId"] = "site-1", ["hostId"] = "A1B2C3D4E5F60718293A4B5C6D7E8F90:1234567", ["meta"] = new JsonObject { ["name"] = "default", ["desc"] = "Lab (demo)" }, ["statistics"] = new JsonObject { ["counts"] = new JsonObject { ["totalDevice"] = 3, ["offlineDevice"] = 1, ["wifiClient"] = 12, ["wiredClient"] = 5, ["guestClient"] = 1 } } }), ["httpStatusCode"] = 200 });
                case "/v1/devices":
                    return J(200, new JsonObject { ["data"] = new JsonArray(new JsonObject { ["hostId"] = "A1B2C3D4E5F60718293A4B5C6D7E8F90:1234567", ["hostName"] = "UDM-Pro Lab (demo)", ["devices"] = new JsonArray(
                        new JsonObject { ["id"] = "d1", ["mac"] = "F4E2C6000001", ["name"] = "U7 Pro lounge (demo)", ["model"] = "U7 Pro", ["ip"] = "192.0.2.11", ["status"] = "online", ["version"] = "7.1.66", ["firmwareStatus"] = "updateAvailable", ["productLine"] = "network" },
                        new JsonObject { ["id"] = "d2", ["mac"] = "F4E2C6000002", ["name"] = "USW Flex (demo)", ["model"] = "USW Flex", ["ip"] = "192.0.2.12", ["status"] = "offline", ["version"] = "7.1.26", ["firmwareStatus"] = "upToDate", ["productLine"] = "network" }) }), ["httpStatusCode"] = 200 });
                case "/v1/isp-metrics/5m":
                    return J(200, new JsonObject { ["data"] = new JsonArray(new JsonObject { ["metricType"] = "5m", ["hostId"] = "A1B2C3D4E5F60718293A4B5C6D7E8F90:1234567", ["siteId"] = "site-1", ["periods"] = new JsonArray(new JsonObject { ["metricTime"] = "2026-10-24T07:55:00Z", ["data"] = new JsonObject { ["wan"] = new JsonObject { ["avgLatency"] = 9, ["maxLatency"] = 31, ["packetLoss"] = 0, ["download_kbps"] = 820000, ["upload_kbps"] = 110000, ["ispName"] = "Example ISP" } } }) }) });
            }
            return J(404, new JsonObject { ["message"] = "not found" });
        }
    }

    public static async Task RunAsync(Action<string> ok, Action<string, Exception?> fail)
    {
        var cred = new Credential { Name = "selftest-unifi", Kind = CredentialKind.Password, Username = "api", Secret = Key };
        AppData.Current.Credentials.Add(cred);
        var before = AppData.Current.InfraProviders.ToList();
        try
        {
            // 1. Address checks and the local console (a self-hosted server: the API answers without /proxy/network).
            try
            {
                foreach (var bad in new[] { "http://192.168.1.1", "https://user@unifi.example.test", "https://unifi.example.test/network", "unifi.example.test" })
                    if (UniFiProvider.ValidateEndpoint(bad) == null) throw new Exception("accepted " + bad);
                if (UniFiProvider.ValidateEndpoint("https://192.168.1.1") != null || UniFiProvider.ValidateEndpoint("https://unifi.example.test:8443") != null) throw new Exception("a good address was refused");
                if (UniFiProvider.ValidateConsoleId("../../v1/hosts") == null || UniFiProvider.ValidateConsoleId("A1B2C3D4E5F60718293A4B5C6D7E8F90:1234567") != null) throw new Exception("console id check");
                var fake = new FakeConsole();
                var c = new ProviderConfig { Kind = "unifi", Name = "selftest-unifi", Endpoint = "https://unifi.example.test:8443", CredentialId = cred.Id, AllowChanges = true };
                var p = new UniFiProvider(c, fake) { PollEvery = TimeSpan.FromMilliseconds(10), VerifyFor = TimeSpan.FromSeconds(5), ListCap = 200 };
                var caps = await p.DetectAsync(CancellationToken.None);
                if (!caps.Reachable || caps.Version != "10.0.162" || p.RootForTest != "https://unifi.example.test:8443/integration/v1") throw new Exception($"detect: {caps.Detail} via {p.RootForTest}");
                var inv = await p.InventoryAsync(CancellationToken.None);
                foreach (var kind in new[] { "console", "site", "device", "pending", "client", "network", "wifi", "firewall zone", "firewall policy", "voucher" }.Where(k => k != "voucher"))
                    if (!inv.Any(o => o.Kind == kind)) throw new Exception("inventory has no " + kind + ": " + string.Join(",", inv.Select(o => o.Kind).Distinct()));
                var sw = inv.Single(o => o.Kind == "device" && o.Name == "switch-core (demo)");
                if (sw.Status != "online" || !sw.Detail.Contains("4/6 ports up") || !sw.Detail.Contains("up 10 d") || !sw.Detail.Contains("uplink gateway (demo)") || sw.Props.GetValueOrDefault("ports.poe") != "3,5")
                    throw new Exception("switch: " + sw.Detail + " | poe " + sw.Props.GetValueOrDefault("ports.poe"));
                if (!inv.Single(o => o.Name == "ap-lounge (demo)").Detail.Contains("update available")) throw new Exception("firmware update not shown");
                var clients = inv.Count(o => o.Kind == "client");
                if (clients != 200 || !inv.Any(o => o.Kind == "note" && o.Detail.Contains("more than 200 clients"))) throw new Exception($"paging / cap: {clients} clients");
                var site = inv.Single(o => o.Kind == "site");
                if (!site.Detail.Contains("3 device(s), 0 offline") || !site.Detail.Contains("200 client(s)")) throw new Exception("site summary: " + site.Detail);

                // Changes, each verified by re-reading.
                var r1 = await p.RunAsync(UniFiProvider.Restart, sw, CancellationToken.None);
                if (!r1.Verified || !fake.Restarted) throw new Exception("restart: " + r1.Message);
                var noPoe = await p.RunAsync(UniFiProvider.PowerCycle, sw, new Dictionary<string, string> { ["port"] = "4" }, CancellationToken.None);
                var poe = await p.RunAsync(UniFiProvider.PowerCycle, sw, new Dictionary<string, string> { ["port"] = "3" }, CancellationToken.None);
                if (noPoe.Accepted || !poe.Verified) throw new Exception($"PoE: port 4 {noPoe.Message} / port 3 {poe.Message}");
                var guest = inv.Single(o => o.Kind == "client" && o.Name == "guest-phone (demo)");
                if (guest.Status != "unauthorized" || p.Forbidden(UniFiProvider.Authorize, inv.First(o => o.Kind == "client" && o.Name == "client-001"), caps) == null) throw new Exception("guest detection");
                var g1 = await p.RunAsync(UniFiProvider.Authorize, guest, new Dictionary<string, string> { ["minutes"] = "60" }, CancellationToken.None);
                var g2 = await p.RunAsync(UniFiProvider.Unauthorize, guest with { Status = "authorized" }, CancellationToken.None);
                if (!g1.Verified || !g2.Verified || fake.GuestAuthorized) throw new Exception($"guest: {g1.Message} / {g2.Message}");
                var v = await p.RunAsync(UniFiProvider.Vouchers, site, new Dictionary<string, string> { ["name"] = "Visitors", ["count"] = "3", ["minutes"] = "480", ["guests"] = "1" }, CancellationToken.None);
                if (!v.Verified || fake.Vouchers.Count != 3 || v.Output?.Split('\n').Length != 3) throw new Exception("vouchers: " + v.Message);
                var inv2 = await p.InventoryAsync(CancellationToken.None);
                var del = await p.RunAsync(UniFiProvider.DeleteVoucher, inv2.First(o => o.Kind == "voucher"), CancellationToken.None);
                if (!del.Verified || fake.Vouchers.Count != 2) throw new Exception("voucher delete: " + del.Message);
                var wifi = inv.Single(o => o.Kind == "wifi");
                var w = await p.RunAsync(UniFiProvider.Disable, wifi, CancellationToken.None);
                if (!w.Verified || fake.WifiOn || fake.LastPut?["id"] != null || fake.LastPut?["metadata"] != null || fake.LastPut?["securityConfiguration"] == null) throw new Exception("WiFi off (PUT round trip): " + w.Message);
                var net = inv.Single(o => o.Kind == "network" && o.Name == "IoT (demo)");
                var n1 = await p.RunAsync(UniFiProvider.Disable, net, CancellationToken.None);
                if (!n1.Verified || fake.VlanOn) throw new Exception("network off: " + n1.Message);
                if (p.Forbidden(UniFiProvider.Disable, inv.Single(o => o.Kind == "network" && o.Name == "Default"), caps) == null) throw new Exception("the default network could be switched off");
                var fw = await p.RunAsync(UniFiProvider.Disable, inv.Single(o => o.Kind == "firewall policy" && o.Name.StartsWith("Block")), CancellationToken.None);
                if (!fw.Verified || fake.FwOn) throw new Exception("firewall PATCH: " + fw.Message);
                if (p.Forbidden(UniFiProvider.Disable, inv.Single(o => o.Kind == "firewall policy" && o.Name.StartsWith("Allow")), caps) == null) throw new Exception("a system-defined policy could be switched off");
                var adopt = await p.RunAsync(UniFiProvider.Adopt, inv.Single(o => o.Kind == "pending"), new Dictionary<string, string> { ["site"] = "Lab (demo)" }, CancellationToken.None);
                if (!adopt.Verified || fake.Adopted.Count != 1) throw new Exception("adopt: " + adopt.Message);
                var un = await p.RunAsync(UniFiProvider.Unadopt, sw, CancellationToken.None);
                if (!un.Verified || fake.SwPresent) throw new Exception("unadopt: " + un.Message);

                // Guards: ids before any request; the key only in its header; redirects refused; API errors in words.
                int reqs = fake.Requests;
                foreach (var tampered in new[] { sw with { Id = $"device:{Site}:../../sites" }, sw with { Id = "device:x:y" }, sw with { Id = $"device:{Site}:{Sw}:extra" } })
                    if ((await p.RunAsync(UniFiProvider.Restart, tampered, CancellationToken.None)).Accepted) throw new Exception("a tampered id was used: " + tampered.Id);
                if (fake.Requests != reqs) throw new Exception("a refused id still sent a request");
                if (fake.Urls.Any(u => u.Contains(Key))) throw new Exception("the key in a URL");
                if (fake.WithoutKey > 0) throw new Exception("requests without the key: " + fake.WithoutKey);
                if (!UniFiProvider.Message("{\"statusCode\":400,\"statusName\":\"BAD_REQUEST\",\"message\":\"vlanId must be at least 2\"}").Contains("vlanId must be at least 2")) throw new Exception("error message");
                fake.Redirect = true;
                var redir = await new UniFiProvider(c, fake).DetectAsync(CancellationToken.None);
                if (redir.Reachable || !redir.Detail.Contains("redirected")) throw new Exception("redirect: " + redir.Detail);
                ok("0.6.16 UniFi Network (Integration API, fake console): https only, an address without path or user; the API root discovered (a self-hosted server without /proxy/network); sites, devices with details and statistics (ports up, PoE ports, uptime, uplink by name, firmware update), devices waiting for adoption, 230 clients paged 100 at a time and capped at 200 (said in a note), networks, WiFi, firewall zones and policies; restart verified by a new uptime, PoE power-cycle only on a PoE port and verified, guest access granted and revoked, 3 vouchers generated (codes shown) and one deleted, WiFi and a VLAN switched off through a GET → PUT without read-only fields, a user firewall policy through PATCH (the default network and system policies refused), a waiting device adopted, a device removed; tampered ids refused before any request, the key only in X-API-KEY, a redirect refused, API errors in words");
            }
            catch (Exception ex) { fail("UniFi Network", ex); }

            // 2. Site Manager (api.ui.com) and the Cloud Connector.
            try
            {
                var cloud = new FakeCloud();
                var cc = new ProviderConfig { Kind = "unifi-cloud", Name = "selftest-unifi-cloud", Endpoint = UniFiProvider.CloudBase, CredentialId = cred.Id, Environment = "LAB" };
                var sm = new UniFiCloudProvider(cc, cloud);
                var caps = await sm.DetectAsync(CancellationToken.None);
                if (!caps.Reachable || caps.CanControl) throw new Exception("detect: " + caps.Detail);
                var inv = await sm.InventoryAsync(CancellationToken.None);
                if (inv.Count(o => o.Kind == "console") != 2) throw new Exception("hosts across nextToken pages: " + inv.Count(o => o.Kind == "console"));
                var siteRow = inv.Single(o => o.Kind == "site");
                if (siteRow.Status != "degraded" || !siteRow.Detail.Contains("12 WiFi / 5 wired") || siteRow.Parent != "UDM-Pro Lab (demo)") throw new Exception("site (after one 429, waited out): " + siteRow.Detail);
                if (inv.Count(o => o.Kind == "device") != 2 || !inv.Single(o => o.Name == "U7 Pro lounge (demo)").Detail.Contains("update available")) throw new Exception("devices");
                var isp = inv.Single(o => o.Kind == "isp");
                if (!isp.Detail.Contains("latency 9 ms") || !isp.Detail.Contains("Example ISP")) throw new Exception("ISP metrics: " + isp.Detail);
                if (inv.Single(o => o.Name == "UCG Branch (demo)").Status != "disconnected") throw new Exception("a disconnected console shows online");
                // One click: a console as a UniFi Network provider through the Cloud Connector, then used.
                var host = inv.Single(o => o.Name == "UDM-Pro Lab (demo)");
                var added = await sm.RunAsync(UniFiCloudProvider.AddConsole, host, CancellationToken.None);
                var pc = AppData.Current.InfraProviders.SingleOrDefault(x => x.Kind == "unifi" && x.ConsoleId == host.Id[5..]);
                if (!added.Verified || pc == null || pc.Endpoint != UniFiProvider.CloudBase || pc.CredentialId != cred.Id) throw new Exception("add console: " + added.Message);
                if (!(await sm.RunAsync(UniFiCloudProvider.AddConsole, host, CancellationToken.None)).Message.Contains("already")) throw new Exception("the console was added twice");
                cloud.Console = new FakeConsole();
                cloud.ConsoleId = pc.ConsoleId;
                var remote = new UniFiProvider(pc, cloud) { PollEvery = TimeSpan.FromMilliseconds(10), VerifyFor = TimeSpan.FromSeconds(5) };
                var rc = await remote.DetectAsync(CancellationToken.None);
                if (!rc.Reachable || remote.RootForTest?.Contains("/v1/connector/consoles/A1B2C3D4E5F60718293A4B5C6D7E8F90%3A1234567/network/integration/v1") != true || !remote.Where.Contains("Cloud Connector"))
                    throw new Exception($"Cloud Connector: {rc.Detail} via {remote.RootForTest}");
                if ((await remote.InventoryAsync(CancellationToken.None)).Count(o => o.Kind == "device") != 3) throw new Exception("inventory through the Cloud Connector");
                if (cloud.Urls.Any(u => u.Contains(Key)) || cloud.WithoutKey > 0) throw new Exception("the key outside its header");
                ok("0.6.16 UniFi Site Manager (fake api.ui.com): read-only fleet view: consoles across nextToken pages (a disconnected one shown so), a site with its health counts after a 429 waited out once, devices with firmware status, the last ISP metrics (latency, loss, throughput); “Add as a UniFi Network provider” adds the console once (Cloud Connector, same key, same environment), and that provider finds the connector's path (the console id escaped) and reads the console's inventory through it; the key only in its header");
            }
            catch (Exception ex) { fail("UniFi Site Manager", ex); }
        }
        finally
        {
            AppData.Current.Credentials.Remove(cred);
            AppData.Current.InfraProviders.RemoveAll(x => !before.Contains(x));
        }
    }
}
