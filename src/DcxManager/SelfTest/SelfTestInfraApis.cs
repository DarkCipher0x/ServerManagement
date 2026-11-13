using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using DcxManager.Infra;
using DcxManager.Services;

namespace DcxManager;

/// <summary>
/// 0.6.33: the Redfish (BMC), HPE GreenLake and Sophos Central providers against fakes (an HTTP handler; nothing on the
/// network). Redfish: https only; addresses the BMC names are followed only when they are /redfish/v1 paths on the same
/// BMC; one session per refresh or action, deleted afterwards, the token in its header and the password never in a URL;
/// Basic only for a BMC without sessions; drives, power supplies, fans, temperatures, firmware and the event log read;
/// only the reset types the system lists offered; power and the identify light verified by re-reading. GreenLake: OAuth
/// client credentials once (the token reused), the v1beta1 fallback, subscriptions about to expire flagged, Compute Ops
/// Management servers and their iLO added as a Redfish provider. Sophos Central: a partner account's tenants (one outside
/// sophos.com refused), the tenant id only to its own data region, keyed and numbered pages, firmware check / upgrade (only
/// an offered version), isolation verified, an alert acknowledged.
/// </summary>
public static class InfraApiTests
{
    /// <summary>A fake web API: every request is recorded (method, address, headers, body) and answered by the function.</summary>
    private sealed class FakeApi(Func<HttpRequestMessage, string, (int Code, string Body, Dictionary<string, string>? Headers)> respond) : HttpMessageHandler
    {
        public readonly List<(string Method, Uri Url, Dictionary<string, string> Headers, string Body)> Seen = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
            lock (Seen) Seen.Add((request.Method.Method, request.RequestUri!, headers, body));
            var (code, text, extra) = respond(request, body);
            var m = new HttpResponseMessage((HttpStatusCode)code) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
            foreach (var (k, v) in extra ?? new())
                if (k == "Location") m.Headers.Location = new Uri(v, UriKind.RelativeOrAbsolute); else m.Headers.TryAddWithoutValidation(k, v);
            return m;
        }
    }

    private static (int, string, Dictionary<string, string>?) J(int code, JsonNode body, Dictionary<string, string>? h = null) => (code, body.ToJsonString(), h);
    private static JsonObject Ref(string path) => new() { ["@odata.id"] = path };
    private static JsonObject Members(params string[] paths) => new() { ["Members"] = new JsonArray(paths.Select(p => (JsonNode)Ref(p)).ToArray()) };
    private static void Expect(bool condition, string what) { if (!condition) throw new Exception(what); }

    public static async Task RunAsync(Action<string> ok, Action<string, Exception?> fail)
    {
        try { await RedfishAsync(ok); } catch (Exception ex) { fail("Redfish BMC provider", ex); }
        try { await GreenLakeAsync(ok); } catch (Exception ex) { fail("HPE GreenLake provider", ex); }
        try { await SophosAsync(ok); } catch (Exception ex) { fail("Sophos Central provider", ex); }
    }

    // ---- Redfish --------------------------------------------------------------------------------------------------------

    private static FakeApi FakeBmc(Func<string> power, Action<string> setPower, Func<bool> led, Action<bool> setLed, List<string> deleted, bool sessions = true)
    {
        const string Token = "RF-TOKEN-qx9";
        return new FakeApi((r, body) =>
        {
            var path = r.RequestUri!.AbsolutePath.TrimEnd('/');
            if (r.RequestUri.Host != "bmc.example.test") return J(599, new JsonObject { ["error"] = "wrong host" });
            if (path == "/redfish/v1" && r.Method == HttpMethod.Get)
                return J(200, new JsonObject
                {
                    ["RedfishVersion"] = "1.17.0", ["Vendor"] = "HPE", ["Product"] = "ProLiant DL380 Gen11",
                    ["Oem"] = new JsonObject { ["Hpe"] = new JsonObject { ["Manager"] = new JsonArray(new JsonObject { ["ManagerType"] = "iLO 6", ["ManagerFirmwareVersion"] = "1.62" }) } },
                    ["Systems"] = Ref("/redfish/v1/Systems"), ["Chassis"] = Ref("/redfish/v1/Chassis"), ["Managers"] = Ref("/redfish/v1/Managers"),
                    ["UpdateService"] = Ref("/redfish/v1/UpdateService"),
                    ["Links"] = new JsonObject { ["Sessions"] = Ref("/redfish/v1/SessionService/Sessions") },
                });
            if (path == "/redfish/v1/SessionService/Sessions" && r.Method == HttpMethod.Post)
            {
                if (!sessions) return J(405, new JsonObject());
                var b = JsonNode.Parse(body);
                if (b?["UserName"]?.ToString() != "dcx-ro" || b["Password"]?.ToString() != "Bmc-Secret-qx9") return J(401, new JsonObject { ["error"] = new JsonObject { ["message"] = "bad" } });
                return J(201, new JsonObject { ["@odata.id"] = "/redfish/v1/SessionService/Sessions/77" }, new() { ["X-Auth-Token"] = Token, ["Location"] = "/redfish/v1/SessionService/Sessions/77" });
            }
            bool authed = sessions ? r.Headers.TryGetValues("X-Auth-Token", out var t) && t.First() == Token
                : r.Headers.Authorization is { Scheme: "Basic" } a && Encoding.UTF8.GetString(Convert.FromBase64String(a.Parameter!)) == "dcx-ro:Bmc-Secret-qx9";
            if (!authed) return J(401, new JsonObject());
            if (path == "/redfish/v1/SessionService/Sessions/77" && r.Method == HttpMethod.Delete) { lock (deleted) deleted.Add(path); return J(204, new JsonObject()); }
            var resetTarget = "/redfish/v1/Systems/1/Actions/ComputerSystem.Reset";
            if (path == resetTarget && r.Method == HttpMethod.Post)
            {
                var type = JsonNode.Parse(body)?["ResetType"]?.ToString();
                if (type is "ForceOff" or "GracefulShutdown") setPower("Off"); else if (type == "On") setPower("On"); else return J(400, new JsonObject());
                return J(204, new JsonObject());
            }
            if (path == "/redfish/v1/Systems/1" && r.Method == HttpMethod.Patch)
            {
                if (JsonNode.Parse(body)?["LocationIndicatorActive"] is JsonValue v) setLed(v.GetValue<bool>()); else return J(400, new JsonObject());
                return J(204, new JsonObject());
            }
            if (r.Method != HttpMethod.Get) return J(405, new JsonObject());
            return path switch
            {
                "/redfish/v1/Systems" => J(200, new JsonObject { ["Members"] = new JsonArray(Ref("/redfish/v1/Systems/1"), Ref("https://evil.example.test/redfish/v1/Systems/2"), Ref("/redfish/v1/Systems/../../etc"), Ref("/redfish/v1/Systems/%2e%2e")) }),
                "/redfish/v1/Systems/1" => J(200, new JsonObject
                {
                    ["@odata.id"] = "/redfish/v1/Systems/1", ["Id"] = "1", ["Name"] = "Computer System", ["HostName"] = "srv-demo-01", ["Manufacturer"] = "HPE", ["Model"] = "ProLiant DL380 Gen11",
                    ["SerialNumber"] = "CZDEMO0001", ["PowerState"] = power(), ["BiosVersion"] = "U46 v2.30", ["LocationIndicatorActive"] = led(),
                    ["Status"] = new JsonObject { ["Health"] = "OK", ["HealthRollup"] = "OK", ["State"] = "Enabled" },
                    ["ProcessorSummary"] = new JsonObject { ["Count"] = 2, ["Model"] = "Xeon Gold 6430" }, ["MemorySummary"] = new JsonObject { ["TotalSystemMemoryGiB"] = 256 },
                    ["Storage"] = Ref("/redfish/v1/Systems/1/Storage"),
                    ["Actions"] = new JsonObject { ["#ComputerSystem.Reset"] = new JsonObject { ["target"] = resetTarget, [RedfishProvider.AllowableResets] = new JsonArray("On", "ForceOff", "GracefulShutdown", "GracefulRestart") } },
                }),
                "/redfish/v1/Systems/1/Storage" => J(200, Members("/redfish/v1/Systems/1/Storage/DE00")),
                "/redfish/v1/Systems/1/Storage/DE00" => J(200, new JsonObject { ["Drives"] = new JsonArray(Ref("/redfish/v1/Systems/1/Storage/DE00/Drives/0")) }),
                "/redfish/v1/Systems/1/Storage/DE00/Drives/0" => J(200, new JsonObject
                {
                    ["@odata.id"] = "/redfish/v1/Systems/1/Storage/DE00/Drives/0", ["Name"] = "Drive 0", ["Model"] = "MZ7L3960", ["CapacityBytes"] = 960197124096, ["MediaType"] = "SSD",
                    ["Status"] = new JsonObject { ["Health"] = "Warning", ["State"] = "Enabled" }, ["PredictedMediaLifeLeftPercent"] = 7, ["FailurePredicted"] = true,
                }),
                "/redfish/v1/Managers" => J(200, Members("/redfish/v1/Managers/1")),
                "/redfish/v1/Managers/1" => J(200, new JsonObject
                {
                    ["@odata.id"] = "/redfish/v1/Managers/1", ["Model"] = "iLO 6", ["FirmwareVersion"] = "1.62", ["ManagerType"] = "BMC", ["Status"] = new JsonObject { ["Health"] = "OK" },
                    ["LogServices"] = Ref("/redfish/v1/Managers/1/LogServices"),
                    ["Actions"] = new JsonObject { ["#Manager.Reset"] = new JsonObject { ["target"] = "/redfish/v1/Managers/1/Actions/Manager.Reset", [RedfishProvider.AllowableResets] = new JsonArray("GracefulRestart") } },
                }),
                "/redfish/v1/Managers/1/LogServices" => J(200, Members("/redfish/v1/Managers/1/LogServices/IEL", "/redfish/v1/Managers/1/LogServices/Other")),
                "/redfish/v1/Managers/1/LogServices/IEL" => J(200, new JsonObject { ["Entries"] = Ref("/redfish/v1/Managers/1/LogServices/IEL/Entries") }),
                "/redfish/v1/Managers/1/LogServices/IEL/Entries" => J(200, new JsonObject
                {
                    ["Members"] = new JsonArray(
                        new JsonObject { ["@odata.id"] = "/redfish/v1/Managers/1/LogServices/IEL/Entries/1", ["Id"] = "1", ["Created"] = "2026-11-07T10:00:00Z", ["Severity"] = "Warning", ["Message"] = "Power supply 2 lost input" },
                        new JsonObject { ["@odata.id"] = "/redfish/v1/Managers/1/LogServices/IEL/Entries/2", ["Id"] = "2", ["Created"] = "2026-11-08T09:00:00Z", ["Severity"] = "Critical", ["Message"] = "Fan 3 failed" }),
                }),
                "/redfish/v1/Chassis" => J(200, Members("/redfish/v1/Chassis/1")),
                "/redfish/v1/Chassis/1" => J(200, new JsonObject
                {
                    ["@odata.id"] = "/redfish/v1/Chassis/1", ["Name"] = "Computer System Chassis", ["ChassisType"] = "RackMount", ["Status"] = new JsonObject { ["Health"] = "OK" },
                    ["Power"] = Ref("/redfish/v1/Chassis/1/Power"), ["Thermal"] = Ref("/redfish/v1/Chassis/1/Thermal"),
                }),
                "/redfish/v1/Chassis/1/Power" => J(200, new JsonObject
                {
                    ["PowerControl"] = new JsonArray(new JsonObject { ["PowerConsumedWatts"] = 212 }),
                    ["PowerSupplies"] = new JsonArray(
                        new JsonObject { ["MemberId"] = "0", ["Name"] = "PSU 1", ["PowerCapacityWatts"] = 800, ["Status"] = new JsonObject { ["Health"] = "OK", ["State"] = "Enabled" } },
                        new JsonObject { ["MemberId"] = "1", ["Name"] = "PSU 2", ["PowerCapacityWatts"] = 800, ["Status"] = new JsonObject { ["Health"] = "Critical", ["State"] = "Enabled" } }),
                }),
                "/redfish/v1/Chassis/1/Thermal" => J(200, new JsonObject
                {
                    ["Fans"] = new JsonArray(new JsonObject { ["MemberId"] = "0", ["Name"] = "Fan 1", ["Reading"] = 32, ["ReadingUnits"] = "Percent", ["Status"] = new JsonObject { ["Health"] = "OK" } }),
                    ["Temperatures"] = new JsonArray(
                        new JsonObject { ["MemberId"] = "0", ["Name"] = "01-Inlet Ambient", ["ReadingCelsius"] = 22, ["Status"] = new JsonObject { ["Health"] = "OK" } },
                        new JsonObject { ["MemberId"] = "1", ["Name"] = "02-CPU 1", ["ReadingCelsius"] = 61, ["Status"] = new JsonObject { ["Health"] = "OK" } },
                        new JsonObject { ["MemberId"] = "2", ["Name"] = "02-CPU 1", ["ReadingCelsius"] = 60, ["Status"] = new JsonObject { ["Health"] = "OK" } }),
                }),
                "/redfish/v1/UpdateService" => J(200, new JsonObject { ["FirmwareInventory"] = Ref("/redfish/v1/UpdateService/FirmwareInventory") }),
                "/redfish/v1/UpdateService/FirmwareInventory" => J(200, Members("/redfish/v1/UpdateService/FirmwareInventory/1")),
                "/redfish/v1/UpdateService/FirmwareInventory/1" => J(200, new JsonObject { ["@odata.id"] = "/redfish/v1/UpdateService/FirmwareInventory/1", ["Name"] = "iLO 6", ["Version"] = "1.62", ["Updateable"] = true, ["Status"] = new JsonObject { ["Health"] = "OK" } }),
                _ => J(404, new JsonObject { ["error"] = new JsonObject { ["message"] = "not here" } }),
            };
        });
    }

    private static async Task RedfishAsync(Action<string> ok)
    {
        var cred = new Credential { Name = "selftest-bmc", Kind = CredentialKind.Password, Username = "dcx-ro", Secret = "Bmc-Secret-qx9" };
        AppData.Current.Credentials.Add(cred);
        try
        {
            Expect(RedfishProvider.ValidateEndpoint("http://bmc.lan") != null && RedfishProvider.ValidateEndpoint("https://bmc.lan/redfish") != null
                && RedfishProvider.ValidateEndpoint("https://u:p@bmc.lan") != null && RedfishProvider.ValidateEndpoint("https://192.0.2.20:8443") == null, "https-only address check");
            string power = "On"; bool led = false; var deleted = new List<string>();
            var fake = FakeBmc(() => power, v => power = v, () => led, v => led = v, deleted);
            var c = new ProviderConfig { Kind = "redfish", Name = "selftest-bmc", Endpoint = "https://bmc.example.test", CredentialId = cred.Id, AllowChanges = true };
            var p = new RedfishProvider(c, fake) { PollEvery = TimeSpan.FromMilliseconds(20), VerifyFor = TimeSpan.FromSeconds(5) };
            Expect(p.SafePath("https://evil.example.test/redfish/v1/x") == null && p.SafePath("/redfish/v1/../x") == null && p.SafePath("/redfish/v1/%2E%2E/x") == null
                && p.SafePath("/etc/passwd") == null && p.SafePath("https://bmc.example.test/redfish/v1/Systems/1#/x") == "/redfish/v1/Systems/1", "safe paths");

            var caps = await p.DetectAsync(CancellationToken.None);
            Expect(caps.Reachable && caps.Detail.Contains("iLO 6") && caps.Detail.Contains("HPE"), "detect: " + caps.Detail);
            var inv = await p.InventoryAsync(CancellationToken.None);
            foreach (var k in new[] { "system", "drive", "bmc", "log", "chassis", "power supply", "fan", "thermal", "firmware" })
                Expect(inv.Any(o => o.Kind == k), "inventory has no " + k + ": " + string.Join(",", inv.Select(o => o.Kind).Distinct()));
            Expect(inv.Count(o => o.Kind == "system") == 1, "a member outside the BMC (or a .. path) was followed");
            Expect(inv.Single(o => o.Kind == "drive").Status == "degraded" && inv.Single(o => o.Kind == "drive").Detail.Contains("FAILURE PREDICTED"), "drive wear not flagged");
            Expect(inv.Single(o => o.Name == "PSU 2").Status == "critical", "failed power supply not flagged");
            Expect(inv.Count(o => o.Kind == "log") == 2 && inv.First(o => o.Kind == "log").Name.Contains("Fan 3"), "event log: newest first");
            Expect(inv.Single(o => o.Kind == "chassis").Detail.Contains("212 W"), "power draw");
            Expect(fake.Seen.All(s => s.Url.Host == "bmc.example.test") && !fake.Seen.Any(s => s.Url.AbsolutePath.Contains("..") || s.Url.AbsolutePath.Contains("Other")), "requests outside the BMC's own paths");

            var sys = inv.Single(o => o.Kind == "system");
            var acts = p.ActionsFor(sys).Select(a => a.Id).ToList();
            Expect(acts.Contains("reset:ForceOff") && acts.Contains("reset:GracefulShutdown") && !acts.Contains("reset:ForceRestart") && !acts.Contains("reset:PowerCycle"), "offered reset types: " + string.Join(",", acts));
            Expect(p.Forbidden(p.ActionsFor(sys).First(a => a.Id == "reset:On"), sys, caps) != null, "power on offered while on");
            var off = await p.RunAsync(p.ActionsFor(sys).First(a => a.Id == "reset:ForceOff"), sys, CancellationToken.None);
            Expect(off.Verified && power == "Off", "force off: " + off.Message);
            var on = await p.RunAsync(p.ActionsFor(sys).First(a => a.Id == "reset:On"), sys, CancellationToken.None);
            Expect(on.Verified && power == "On", "power on: " + on.Message);
            var ledOn = await p.RunAsync(RedfishProvider.LedOn, sys, CancellationToken.None);
            Expect(ledOn.Verified && led, "identify light: " + ledOn.Message);

            // Sessions: each operation opened one and deleted it; the password only in the session request's body.
            Expect(p.SessionsOpened >= 5 && deleted.Count == p.SessionsOpened && !p.SignedInForTest, $"sessions: opened {p.SessionsOpened}, deleted {deleted.Count}");
            Expect(!fake.Seen.Any(s => s.Url.ToString().Contains("Bmc-Secret") || s.Url.ToString().Contains("RF-TOKEN")), "a secret in a URL");
            Expect(fake.Seen.Where(s => s.Body.Contains("Bmc-Secret")).All(s => s.Url.AbsolutePath.EndsWith("/Sessions")), "the password sent outside the sign-in");
            Expect(fake.Seen.Where(s => !s.Url.AbsolutePath.EndsWith("/Sessions") && s.Url.AbsolutePath.TrimEnd('/') != "/redfish/v1").All(s => s.Headers.ContainsKey("X-Auth-Token")), "a request without the session");

            // A BMC without sessions: Basic for that operation only.
            var basicFake = FakeBmc(() => power, v => power = v, () => led, v => led = v, new List<string>(), sessions: false);
            var pb = new RedfishProvider(c, basicFake);
            var binv = await pb.InventoryAsync(CancellationToken.None);
            Expect(binv.Any(o => o.Kind == "system") && !pb.SignedInForTest, "Basic fallback");
            // A wrong password is refused, not retried as Basic.
            cred.Secret = "wrong";
            var bad = await new RedfishProvider(c, FakeBmc(() => power, v => power = v, () => led, v => led = v, new List<string>())).DetectAsync(CancellationToken.None);
            Expect(!bad.Reachable && bad.Detail.Contains("refused the account"), "wrong password: " + bad.Detail);
            ok($"Redfish BMC (fake iLO 6): https only; only /redfish/v1 paths on the same BMC are followed (another host, .. and %2e refused); one session per refresh or action, deleted afterwards ({p.SessionsOpened}), the password only in the sign-in body; Basic only for a BMC without sessions; system, drive wear (failure predicted), BMC, event log newest first, chassis power draw, a failed power supply, fans, temperatures and firmware read; only the listed reset types offered; force off and power on verified by re-reading; the identify light verified; a wrong password refused");
        }
        finally { AppData.Current.Credentials.Remove(cred); AppData.Save(); }
    }

    // ---- HPE GreenLake --------------------------------------------------------------------------------------------------

    private static async Task GreenLakeAsync(Action<string> ok)
    {
        var cred = new Credential { Name = "selftest-greenlake", Kind = CredentialKind.Password, Username = "gl-client-qx9", Secret = "Gl-Secret-qx9" };
        AppData.Current.Credentials.Add(cred);
        var providersBefore = AppData.Current.InfraProviders.Select(x => x.Id).ToHashSet();
        try
        {
            const string Token = "GLTOKEN-qx9-abcdef";
            var now = DateTime.UtcNow;
            JsonObject Page(Uri u, JsonObject[] all)
            {
                var q = System.Web.HttpUtility.ParseQueryString(u.Query);
                int limit = int.Parse(q["limit"] ?? "200"), offset = int.Parse(q["offset"] ?? "0");
                var items = all.Skip(offset).Take(limit).Select(i => (JsonNode)i.DeepClone()).ToArray();
                return new JsonObject { ["items"] = new JsonArray(items), ["count"] = items.Length, ["offset"] = offset, ["total"] = all.Length };
            }
            var devices = Enumerable.Range(1, 3).Select(i => new JsonObject
            {
                ["id"] = $"dev-{i}", ["serialNumber"] = $"SNDEMO{i}", ["model"] = "ProLiant DL360 Gen11", ["deviceType"] = "COMPUTE", ["partNumber"] = "P52499-B21",
                ["assignedState"] = i == 3 ? "UNASSIGNED" : "ASSIGNED_TO_SERVICE", ["subscription"] = i == 3 ? new JsonArray() : new JsonArray(new JsonObject { ["key"] = "SUBKEY-1" }),
            }).ToArray();
            var subs = new[]
            {
                new JsonObject { ["id"] = "sub-1", ["key"] = "SUBKEY-1", ["subscriptionType"] = "CENTRAL_COMPUTE", ["tier"] = "ENHANCED", ["quantity"] = 10, ["availableQuantity"] = 8, ["endTime"] = now.AddDays(10).ToString("o") },
                new JsonObject { ["id"] = "sub-2", ["key"] = "SUBKEY-2", ["subscriptionType"] = "CENTRAL_COMPUTE", ["tier"] = "STANDARD", ["quantity"] = 5, ["availableQuantity"] = 5, ["endTime"] = now.AddDays(-3).ToString("o") },
            };
            var fake = new FakeApi((r, body) =>
            {
                var u = r.RequestUri!;
                if (u.Host == "sso.common.cloud.hpe.com" && u.AbsolutePath == "/as/token.oauth2" && r.Method == HttpMethod.Post)
                    return body.Contains("client_id=gl-client-qx9") && body.Contains("client_secret=Gl-Secret-qx9") && body.Contains("grant_type=client_credentials")
                        ? J(200, new JsonObject { ["access_token"] = Token, ["token_type"] = "Bearer", ["expires_in"] = 7200 })
                        : J(401, new JsonObject { ["error"] = "invalid_client", ["error_description"] = "bad client" });
                if (r.Headers.Authorization?.ToString() != "Bearer " + Token) return J(401, new JsonObject());
                if (u.Host == "global.api.greenlake.hpe.com")
                    return u.AbsolutePath switch
                    {
                        "/devices/v1/devices" => J(404, new JsonObject { ["message"] = "no v1 here" }),
                        "/devices/v1beta1/devices" => J(200, Page(u, devices)),
                        "/subscriptions/v1/subscriptions" => J(200, Page(u, subs)),
                        "/audit-log/v1/logs" => J(200, Page(u, [new JsonObject { ["id"] = "log-1", ["category"] = "User Management", ["description"] = "API client created", ["createdAt"] = now.ToString("o") }])),
                        _ => J(404, new JsonObject()),
                    };
                if (u.Host == "us-west.api.greenlake.hpe.com" && u.AbsolutePath == "/compute-ops-mgmt/v1/servers")
                    return J(200, Page(u, [new JsonObject
                    {
                        ["id"] = "srv-1", ["name"] = "srv-demo-02",
                        ["hardware"] = new JsonObject { ["model"] = "ProLiant DL360 Gen11", ["serialNumber"] = "SNDEMO1", ["powerState"] = "ON", ["health"] = new JsonObject { ["summary"] = "OK" }, ["bmc"] = new JsonObject { ["ip"] = "192.0.2.50" } },
                        ["state"] = new JsonObject { ["connected"] = true, ["managed"] = true },
                    }]));
                return J(404, new JsonObject());
            });
            var c = new ProviderConfig { Kind = "greenlake", Name = "selftest-greenlake", Endpoint = GreenLakeProvider.GlobalBase, CredentialId = cred.Id, Region = "us-west", Environment = "LAB" };
            var p = new GreenLakeProvider(c, fake);
            var caps = await p.DetectAsync(CancellationToken.None);
            Expect(caps.Reachable && !caps.CanControl && caps.Detail.Contains("Compute Ops Management"), "detect: " + caps.Detail);
            var inv = await p.InventoryAsync(CancellationToken.None);
            Expect(inv.Count(o => o.Kind == "device") == 3 && inv.Any(o => o.Kind == "device" && o.Status == "unassigned" && o.Detail.Contains("no subscription")), "devices (v1beta1 fallback): " + string.Join(",", inv.Select(o => o.Kind + "/" + o.Status)));
            Expect(inv.Single(o => o.Name == "SUBKEY-1").Status == "degraded" && inv.Single(o => o.Name == "SUBKEY-2").Status == "expired", "subscription expiry");
            Expect(inv.Any(o => o.Kind == "audit") && !inv.Any(o => o.Kind == "note"), "audit log / notes: " + string.Join(" | ", inv.Where(o => o.Kind == "note").Select(o => o.Detail)));
            var srv = inv.Single(o => o.Kind == "server");
            Expect(srv.Status == "running" && srv.Detail.Contains("iLO 192.0.2.50"), "COM server: " + srv.Detail);
            Expect(p.Auth.Fetched == 1, "the token wasn't reused: " + p.Auth.Fetched);
            Expect(!fake.Seen.Any(s => s.Url.ToString().Contains("Gl-Secret") || s.Url.ToString().Contains(Token)), "a secret in a URL");
            Expect(fake.Seen.Where(s => s.Body.Contains("Gl-Secret")).All(s => s.Url.Host == "sso.common.cloud.hpe.com"), "the secret sent elsewhere");
            // The iLO as a Redfish provider, once.
            var add = await p.RunAsync(GreenLakeProvider.AddIlo, srv, CancellationToken.None);
            var added = AppData.Current.InfraProviders.FirstOrDefault(x => !providersBefore.Contains(x.Id));
            Expect(add.Accepted && added is { Kind: "redfish", Endpoint: "https://192.0.2.50", AllowChanges: false }, "iLO added: " + add.Message);
            var again = await p.RunAsync(GreenLakeProvider.AddIlo, srv, CancellationToken.None);
            Expect(again.Message.Contains("already") && AppData.Current.InfraProviders.Count(x => !providersBefore.Contains(x.Id)) == 1, "iLO added twice");
            // Form checks and the workspace's own token address; a refused client.
            var region = GreenLakeProvider.Descriptor.Fields[0];
            Expect(region.Validate("evil.example") != null && region.Validate("eu-central") == null, "region check");
            c.WorkspaceId = "ws-0123456789";
            Expect(p.TokenUrl() == "https://global.api.greenlake.hpe.com/authorization/v2/oauth2/ws-0123456789/token", "workspace token address: " + p.TokenUrl());
            c.WorkspaceId = "";
            cred.Secret = "wrong";
            var bad = await new GreenLakeProvider(c, fake).DetectAsync(CancellationToken.None);
            Expect(!bad.Reachable && bad.Detail.Contains("sign-in refused"), "a refused client: " + bad.Detail);
            ok("HPE GreenLake (fake): OAuth client credentials at HPE's sign-in, the token reused (1 for detect + refresh) and never in a URL, the secret only to the sign-in; devices through the v1beta1 fallback (an unassigned one without subscription flagged), subscriptions expiring within 30 days and expired flagged, the audit log, Compute Ops Management servers in the chosen region; a server's iLO added once as a read-only Redfish provider; the region list and the workspace's own token address; a refused client reported");
        }
        finally
        {
            AppData.Current.Credentials.Remove(cred);
            AppData.Current.InfraProviders.RemoveAll(x => !providersBefore.Contains(x.Id));
            AppData.Save();
        }
    }

    // ---- Sophos Central -------------------------------------------------------------------------------------------------

    private static async Task SophosAsync(Action<string> ok)
    {
        var cred = new Credential { Name = "selftest-sophos", Kind = CredentialKind.Password, Username = "sophos-client-qx9", Secret = "Sophos-Secret-qx9" };   // secret-scan: example (synthetic fixture)
        AppData.Current.Credentials.Add(cred);
        try
        {
            const string Token = "SOPHOS-TOKEN-qx9", Partner = "11111111-aaaa-bbbb-cccc-000000000001", TenantA = "22222222-aaaa-bbbb-cccc-000000000002", TenantEvil = "33333333-aaaa-bbbb-cccc-000000000003";
            const string Fw1 = "44444444-aaaa-bbbb-cccc-000000000004", Fw2 = "55555555-aaaa-bbbb-cccc-000000000005", Ep1 = "66666666-aaaa-bbbb-cccc-000000000006", Ep2 = "77777777-aaaa-bbbb-cccc-000000000007", Al1 = "88888888-aaaa-bbbb-cccc-000000000008";
            const string Region = "api-eu01.central.sophos.com";
            bool isolated = false, acked = false; string? upgradeBody = null;
            string whoKind = "partner", dataRegion = "https://" + Region;
            var fake = new FakeApi((r, body) =>
            {
                var u = r.RequestUri!;
                if (u.Host == "id.sophos.com" && u.AbsolutePath == "/api/v2/oauth2/token")
                    return body.Contains("client_id=sophos-client-qx9") && body.Contains("client_secret=Sophos-Secret-qx9") && body.Contains("scope=token")   // secret-scan: example (synthetic fixture)
                        ? J(200, new JsonObject { ["access_token"] = Token, ["expires_in"] = 3600 }) : J(400, new JsonObject { ["errorCode"] = "oauth.invalid_client" });
                if (r.Headers.Authorization?.ToString() != "Bearer " + Token) return J(401, new JsonObject());
                if (u.Host == "api.central.sophos.com" && u.AbsolutePath == "/whoami/v1")
                    return J(200, new JsonObject { ["id"] = whoKind == "partner" ? Partner : TenantA, ["idType"] = whoKind, ["apiHosts"] = new JsonObject { ["global"] = "https://api.central.sophos.com", ["dataRegion"] = dataRegion } });
                if (u.Host == "api.central.sophos.com" && u.AbsolutePath == "/partner/v1/tenants")
                    return r.Headers.TryGetValues("X-Partner-ID", out var pid) && pid.First() == Partner
                        ? J(200, new JsonObject
                        {
                            ["items"] = new JsonArray(
                                new JsonObject { ["id"] = TenantA, ["name"] = "Demo Customer A", ["apiHost"] = "https://" + Region },
                                new JsonObject { ["id"] = TenantEvil, ["name"] = "Evil", ["apiHost"] = "https://central.sophos.com.evil.example" }),
                            ["pages"] = new JsonObject { ["current"] = 1, ["total"] = 1 },
                        }) : J(403, new JsonObject());
                if (u.Host != Region) return J(404, new JsonObject());
                if (!r.Headers.TryGetValues("X-Tenant-ID", out var tid) || tid.First() != TenantA) return J(403, new JsonObject { ["message"] = "wrong tenant" });
                var q = System.Web.HttpUtility.ParseQueryString(u.Query);
                switch (u.AbsolutePath)
                {
                    case "/firewall/v1/firewalls":
                        return J(200, new JsonObject
                        {
                            ["items"] = new JsonArray(
                                new JsonObject { ["id"] = Fw1, ["name"] = "fw-demo-hq", ["model"] = "XGS 2300", ["firmwareVersion"] = "SFOS 21.0.0 GA-Build169", ["serialNumber"] = "X2300DEMO", ["status"] = new JsonObject { ["connected"] = true, ["managing"] = "approved" }, ["group"] = new JsonObject { ["name"] = "HQ" }, ["externalIpv4Addresses"] = new JsonArray("192.0.2.1") },
                                new JsonObject { ["id"] = Fw2, ["name"] = "fw-demo-branch", ["model"] = "XGS 116", ["firmwareVersion"] = "SFOS 20.0.2", ["status"] = new JsonObject { ["connected"] = false } }),
                            ["pages"] = new JsonObject { ["current"] = 1, ["total"] = 1 },
                        });
                    case "/firewall/v1/firewall-groups":
                        return J(200, new JsonObject { ["items"] = new JsonArray(new JsonObject { ["id"] = "99999999-aaaa-bbbb-cccc-000000000009", ["name"] = "HQ", ["firewalls"] = new JsonObject { ["total"] = 1 } }), ["pages"] = new JsonObject { ["total"] = 1 } });
                    case "/endpoint/v1/endpoints":
                        return q["pageFromKey"] == null
                            ? J(200, new JsonObject { ["items"] = new JsonArray(new JsonObject { ["id"] = Ep1, ["type"] = "computer", ["hostname"] = "pc-demo-01", ["health"] = new JsonObject { ["overall"] = "good" }, ["tamperProtectionEnabled"] = true, ["isolation"] = new JsonObject { ["status"] = isolated ? "isolated" : "notIsolated" } }), ["pages"] = new JsonObject { ["nextKey"] = "KEY-2" } })
                            : J(200, new JsonObject { ["items"] = new JsonArray(new JsonObject { ["id"] = Ep2, ["type"] = "server", ["hostname"] = "srv-demo-03", ["health"] = new JsonObject { ["overall"] = "bad", ["threats"] = new JsonObject { ["status"] = "bad" } }, ["tamperProtectionEnabled"] = false }), ["pages"] = new JsonObject() });
                    case "/common/v1/alerts":
                        return J(200, new JsonObject { ["items"] = acked ? new JsonArray() : new JsonArray(new JsonObject { ["id"] = Al1, ["severity"] = "high", ["category"] = "malware", ["description"] = "Malware detected: EICAR-AV-Test", ["raisedAt"] = "2026-11-08T08:00:00Z", ["allowedActions"] = new JsonArray("acknowledge") }), ["pages"] = new JsonObject() });
                    case "/firewall/v1/firewalls/actions/firmware-upgrade-check":
                        return J(200, new JsonObject { ["firewalls"] = new JsonArray(new JsonObject { ["id"] = Fw1, ["upgradeToVersion"] = new JsonArray("21.5.1.261") }), ["firmwareVersions"] = new JsonArray(new JsonObject { ["version"] = "21.5.1.261", ["news"] = "Maintenance release" }) });
                    case "/firewall/v1/firewalls/actions/firmware-upgrade":
                        upgradeBody = body; return J(201, new JsonObject());
                    case "/endpoint/v1/endpoints/isolation":
                        isolated = JsonNode.Parse(body)?["enabled"]?.GetValue<bool>() == true && body.Contains(Ep1); return J(202, new JsonObject());
                    case var s when s == $"/endpoint/v1/endpoints/{Ep1}/isolation":
                        return J(200, new JsonObject { ["enabled"] = isolated });
                    case var s when s == $"/endpoint/v1/endpoints/{Ep1}/scans":
                        return J(201, new JsonObject());
                    case var s when s == $"/common/v1/alerts/{Al1}/actions":
                        acked = body.Contains("acknowledge"); return J(201, new JsonObject());
                    case var s when s == $"/common/v1/alerts/{Al1}":
                        return acked ? J(404, new JsonObject()) : J(200, new JsonObject { ["id"] = Al1 });
                }
                return J(404, new JsonObject());
            });
            var c = new ProviderConfig { Kind = "sophos-central", Name = "selftest-sophos", Endpoint = SophosCentralProvider.GlobalBase, CredentialId = cred.Id, AllowChanges = true };
            var p = new SophosCentralProvider(c, fake) { PollEvery = TimeSpan.FromMilliseconds(20), VerifyFor = TimeSpan.FromSeconds(5) };
            var caps = await p.DetectAsync(CancellationToken.None);
            Expect(caps.Reachable && caps.Detail.Contains("partner") && caps.Detail.Contains("1 tenant"), "detect (the tenant outside sophos.com refused): " + caps.Detail);
            var inv = await p.InventoryAsync(CancellationToken.None);
            Expect(inv.Count(o => o.Kind == "firewall") == 2 && inv.Single(o => o.Name == "fw-demo-branch").Status == "offline", "firewalls: " + string.Join(",", inv.Select(o => o.Kind + "/" + o.Status)));
            Expect(inv.Any(o => o.Kind == "firewall group") && inv.Single(o => o.Kind == "computer").Status == "online" && inv.Single(o => o.Kind == "server").Status == "critical", "groups / endpoints over two keyed pages");
            Expect(inv.Single(o => o.Kind == "server").Detail.Contains("tamper protection OFF"), "tamper protection off not shown");
            Expect(!inv.Any(o => o.Kind == "note"), "notes: " + string.Join(" | ", inv.Where(o => o.Kind == "note").Select(o => o.Detail)));
            Expect(fake.Seen.All(s => s.Url.Host.EndsWith("sophos.com")), "a request outside sophos.com: " + string.Join(", ", fake.Seen.Select(s => s.Url.Host).Distinct()));
            Expect(fake.Seen.Where(s => s.Url.Host == Region).All(s => s.Headers.GetValueOrDefault("X-Tenant-ID") == TenantA), "the tenant id not on every tenant call");
            Expect(!fake.Seen.Any(s => s.Url.Host != Region && s.Headers.ContainsKey("X-Tenant-ID")), "the tenant id sent to another host");
            Expect(p.Auth.Fetched == 1 && !fake.Seen.Any(s => s.Url.ToString().Contains("Sophos-Secret") || s.Url.ToString().Contains(Token)), "token reuse / a secret in a URL");

            var fw1 = inv.Single(o => o.Name == "fw-demo-hq");
            var check = await p.RunAsync(SophosCentralProvider.FirmwareCheck, fw1, CancellationToken.None);
            Expect(check.Accepted && check.Message.Contains("21.5.1.261"), "firmware check: " + check.Message);
            var wrong = await p.RunAsync(SophosCentralProvider.FirmwareUpgrade, fw1, new Dictionary<string, string> { ["version"] = "22.0.0.1" }, CancellationToken.None);
            Expect(!wrong.Accepted && upgradeBody == null, "an upgrade to a version not offered: " + wrong.Message);
            var up = await p.RunAsync(SophosCentralProvider.FirmwareUpgrade, fw1, new Dictionary<string, string> { ["version"] = "21.5.1.261" }, CancellationToken.None);
            Expect(up.Accepted && upgradeBody != null && upgradeBody.Contains(Fw1) && upgradeBody.Contains("21.5.1.261"), "upgrade: " + up.Message);
            Expect(p.Forbidden(SophosCentralProvider.FirmwareUpgrade, inv.Single(o => o.Name == "fw-demo-branch"), caps) != null, "an upgrade offered for a disconnected firewall");
            var pc = inv.Single(o => o.Kind == "computer");
            var iso = await p.RunAsync(SophosCentralProvider.Isolate, pc, new Dictionary<string, string> { ["comment"] = "selftest" }, CancellationToken.None);
            Expect(iso.Verified && isolated, "isolate: " + iso.Message);
            var rel = await p.RunAsync(SophosCentralProvider.Release, pc, CancellationToken.None);
            Expect(rel.Verified && !isolated, "release: " + rel.Message);
            var alert = inv.Single(o => o.Kind == "alert");
            Expect(p.ActionsFor(alert).Contains(SophosCentralProvider.Acknowledge), "acknowledge not offered");
            var ack = await p.RunAsync(SophosCentralProvider.Acknowledge, alert, CancellationToken.None);
            Expect(ack.Verified && acked, "acknowledge: " + ack.Message);

            // A tenant account whose data region isn't Sophos's is refused.
            whoKind = "tenant"; dataRegion = "https://api.example.test";
            var badRegion = await new SophosCentralProvider(c, fake).DetectAsync(CancellationToken.None);
            Expect(!badRegion.Reachable && badRegion.Detail.Contains("outside sophos.com"), "data region outside sophos.com: " + badRegion.Detail);
            ok("Sophos Central (fake): OAuth client credentials at id.sophos.com (scope token), the token reused and never in a URL; a partner account's tenants (one outside sophos.com refused), the tenant id only to its own data region; firewalls (a disconnected one flagged), groups, computers and servers over two keyed pages (tamper protection off shown), alerts; firmware check, an upgrade only to an offered version (not for a disconnected firewall), isolation and release verified by re-reading, an alert acknowledged and verified gone; a tenant whose data region isn't Sophos's refused");
        }
        finally { AppData.Current.Credentials.Remove(cred); AppData.Save(); }
    }
}

/// <summary>For the fuzz stress (0.6.33): every request gets <paramref name="respond"/>'s text with 200, and a session
/// token header (so a Redfish BMC's sign-in "works" and the mutated answers reach the inventory).</summary>
internal sealed class FuzzHttp(Func<HttpRequestMessage, string> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var m = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(respond(request), Encoding.UTF8, "application/json") };
        m.Headers.TryAddWithoutValidation("X-Auth-Token", "FUZZ-TOKEN-1");
        m.Headers.Location = new Uri("/redfish/v1/SessionService/Sessions/1", UriKind.Relative);
        return Task.FromResult(m);
    }
}
