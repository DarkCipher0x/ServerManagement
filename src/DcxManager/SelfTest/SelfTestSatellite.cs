using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using DcxManager.Infra;
using DcxManager.Services;

namespace DcxManager;

/// <summary>
/// 0.3.0 (§17): the Red Hat Satellite / Foreman + Katello provider against a fake REST API (an HTTP handler; nothing on the
/// network). https only; the user and Personal Access Token go only in the Authorization header (never a URL); an
/// administrator account is warned about; the inventory reads hosts (errata, lifecycle, content view), organizations,
/// lifecycle environments, content views, repositories, subscriptions (expiring ones flagged) and failed tasks; repository sync
/// and content-view promote are followed to the end of their Foreman task; remote execution returns the host's output;
/// errata install is refused when nothing applies; unexpected ids and unknown environments are refused.
/// </summary>
public static class SatelliteTests
{
    public static async Task RunAsync(Action<string> ok, Action<string, Exception?> fail)
    {
        var cred = new Credential { Name = "selftest-satellite", Kind = CredentialKind.Password, Username = "dcx-svc", Secret = "Sat-PAT-qx9" };
        AppData.Current.Credentials.Add(cred);
        try
        {
            if (SatelliteProvider.ValidateEndpoint("http://satellite.example.test") == null || SatelliteProvider.ValidateEndpoint("https://satellite.example.test") != null) throw new Exception("https-only check");
            var fake = new FakeSatellite();
            var c = new ProviderConfig { Kind = "satellite", Name = "selftest-satellite", Endpoint = "https://satellite.example.test", CredentialId = cred.Id, AllowChanges = true };
            var p = new SatelliteProvider(c, fake) { PollEvery = TimeSpan.FromMilliseconds(20), VerifyFor = TimeSpan.FromSeconds(5) };

            var caps = await p.DetectAsync(CancellationToken.None);
            if (!caps.Reachable || !caps.Detail.Contains("6.16") || !caps.Detail.Contains("Katello") || caps.Notes.Any(n => n.Contains("administrator"))) throw new Exception("detect: " + caps.Detail + " | " + string.Join(" | ", caps.Notes));
            var inv = await p.InventoryAsync(CancellationToken.None);
            foreach (var k in new[] { "host", "organization", "lifecycle", "content view", "repository", "subscription", "task" })
                if (!inv.Any(o => o.Kind == k)) throw new Exception("inventory has no " + k + ": " + string.Join(",", inv.Select(o => o.Kind).Distinct()));
            var web = inv.Single(o => o.Kind == "host" && o.Name == "web01.example.test");
            if (web.Props.GetValueOrDefault("errata_security") != "3" || !web.Detail.Contains("Production")) throw new Exception("host errata/lifecycle: " + web.Detail);
            if (inv.Single(o => o.Kind == "subscription").Status != "expiring") throw new Exception("an expiring subscription wasn't flagged");

            var sync = await p.RunAsync(SatelliteProvider.Sync, inv.Single(o => o.Kind == "repository"), CancellationToken.None);
            if (!sync.Verified || fake.Synced != 1) throw new Exception("sync: " + sync.Message);
            var cv = inv.Single(o => o.Kind == "content view");
            var promote = await p.RunAsync(SatelliteProvider.Promote, cv, new Dictionary<string, string> { ["environment"] = "production" }, CancellationToken.None);
            if (!promote.Verified || fake.PromotedTo != 3) throw new Exception("promote: " + promote.Message);
            var nowhere = await p.RunAsync(SatelliteProvider.Promote, cv, new Dictionary<string, string> { ["environment"] = "Nowhere" }, CancellationToken.None);
            if (nowhere.Accepted) throw new Exception("an unknown environment was accepted");
            var cmd = await p.RunAsync(SatelliteProvider.Command, web, new Dictionary<string, string> { ["command"] = "uptime" }, CancellationToken.None);
            if (!cmd.Verified || cmd.Output?.Contains("load average") != true || fake.LastCommand != "uptime") throw new Exception("remote execution: " + cmd.Message);
            var clean = inv.Single(o => o.Kind == "host" && o.Name == "db01.example.test");
            if (p.Forbidden(SatelliteProvider.Errata, clean, caps) == null) throw new Exception("errata install offered with no applicable errata");
            var bad = await p.RunAsync(SatelliteProvider.Sync, web with { Id = "repo:1;drop" }, CancellationToken.None);
            if (bad.Accepted) throw new Exception("an unexpected id was accepted");

            // Security: the token is only ever in the Authorization header.
            if (fake.Urls.Any(u => u.Contains("Sat-PAT") || u.Contains("dcx-svc"))) throw new Exception("a secret in a URL");
            if (fake.WithoutAuth > 0) throw new Exception("requests without the Authorization header: " + fake.WithoutAuth);

            fake.Admin = true;
            var c2 = await new SatelliteProvider(c, fake).DetectAsync(CancellationToken.None);
            if (!c2.Notes.Any(n => n.Contains("administrator"))) throw new Exception("no warning for an administrator account");
            cred.Secret = "wrong";
            var c3 = await new SatelliteProvider(c, fake).DetectAsync(CancellationToken.None);
            if (c3.Reachable || !c3.Detail.Contains("rejected")) throw new Exception("a wrong token: " + c3.Detail);
            ok("Red Hat Satellite / Foreman + Katello: https only; user + Personal Access Token only in the Authorization header; hosts (errata, lifecycle, content view), organizations, lifecycle environments, content views, repositories, subscriptions (expiring flagged) and failed tasks listed; repository sync and content-view promote followed to their task's end; remote execution returns the output; errata install refused when nothing applies; unknown environments and unexpected ids refused; an administrator account warned about; a wrong token reported");
        }
        catch (Exception ex) { fail("Red Hat Satellite", ex); }
        finally { AppData.Current.Credentials.Remove(cred); AppData.Save(); }
    }

    private sealed class FakeSatellite : HttpMessageHandler
    {
        public List<string> Urls { get; } = new();
        public int WithoutAuth, Synced, PromotedTo;
        public bool Admin;
        public string LastCommand = "";
        private int _taskPolls;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var url = req.RequestUri!.ToString();
            Urls.Add(url);
            var auth = req.Headers.Authorization;
            if (auth == null) { WithoutAuth++; return Status(HttpStatusCode.Unauthorized); }
            if (Encoding.UTF8.GetString(Convert.FromBase64String(auth.Parameter ?? "")) != "dcx-svc:Sat-PAT-qx9") return Status(HttpStatusCode.Unauthorized);
            var body = req.Content == null ? null : JsonNode.Parse(await req.Content.ReadAsStringAsync(ct));
            var path = req.RequestUri.AbsolutePath;
            bool post = req.Method == HttpMethod.Post;
            JsonNode? r = path switch
            {
                "/api/status" => new JsonObject { ["version"] = "6.16.1" },
                "/api/v2/current_user" => new JsonObject { ["login"] = "dcx-svc", ["admin"] = Admin },
                "/katello/api/organizations" => Page(new JsonObject { ["id"] = 1, ["name"] = "Example", ["label"] = "Example" }),
                "/api/v2/hosts" => Page(
                    Host(10, "web01.example.test", "Warning", 3, 7, "Production"),
                    Host(11, "db01.example.test", "OK", 0, 0, "Production")),
                "/katello/api/environments" => Page(
                    new JsonObject { ["id"] = 1, ["name"] = "Library" },
                    new JsonObject { ["id"] = 2, ["name"] = "Test", ["prior"] = new JsonObject { ["name"] = "Library" } },
                    new JsonObject { ["id"] = 3, ["name"] = "Production", ["prior"] = new JsonObject { ["name"] = "Test" } }),
                "/katello/api/content_views" => Page(new JsonObject { ["id"] = 5, ["name"] = "RHEL9-Base", ["organization_id"] = 1, ["latest_version"] = "4.0", ["latest_version_id"] = 44, ["environments"] = new JsonArray(new JsonObject { ["name"] = "Library" }) }),
                "/katello/api/repositories" => Page(new JsonObject { ["id"] = 7, ["name"] = "BaseOS x86_64", ["content_type"] = "yum", ["product"] = new JsonObject { ["name"] = "RHEL" }, ["last_sync"] = new JsonObject { ["result"] = "success", ["ended_at"] = "2026-10-19" } }),
                "/katello/api/subscriptions" => Page(new JsonObject { ["id"] = 9, ["name"] = "Red Hat Enterprise Linux Server", ["quantity"] = 10, ["consumed"] = 2, ["end_date"] = DateTime.Now.AddDays(10).ToString("yyyy-MM-dd") }),
                "/foreman_tasks/api/tasks" => Page(new JsonObject { ["id"] = "aaaaaaaa-0000-0000-0000-000000000001", ["action"] = "Synchronize repository 'AppStream'", ["state"] = "stopped", ["result"] = "error" }),
                "/katello/api/repositories/7/sync" when post => Started(() => Synced++),
                "/katello/api/content_view_versions/44/promote" when post => Started(() => PromotedTo = (int)body!["environment_ids"]![0]!),
                "/api/job_templates" => Page(new JsonObject { ["id"] = 70, ["name"] = "Run Command - Script Default" }),
                "/api/job_invocations" when post => Job(body!),
                "/api/job_invocations/88" => new JsonObject { ["id"] = 88, ["status_label"] = "succeeded" },
                "/api/job_invocations/88/hosts" => Page(new JsonObject { ["id"] = 10, ["name"] = "web01.example.test" }),
                "/api/job_invocations/88/hosts/10" => new JsonObject { ["output"] = new JsonArray(new JsonObject { ["output"] = " 10:00:00 up 3 days,  load average: 0.01, 0.02, 0.00\n" }) },
                _ when path.StartsWith("/foreman_tasks/api/tasks/") => new JsonObject { ["id"] = path[^36..], ["state"] = ++_taskPolls % 2 == 0 ? "stopped" : "running", ["result"] = _taskPolls % 2 == 0 ? "success" : "pending" },
                _ => null,
            };
            if (r == null) return Status(HttpStatusCode.NotFound);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(r.ToJsonString(), Encoding.UTF8, "application/json") };
        }

        private JsonNode Job(JsonNode body)
        {
            LastCommand = body["job_invocation"]?["inputs"]?["command"]?.ToString() ?? "";
            return new JsonObject { ["id"] = 88 };
        }

        private static JsonNode Started(Action a) { a(); return new JsonObject { ["id"] = "bbbbbbbb-0000-0000-0000-" + Guid.NewGuid().ToString("N")[..12] }; }

        private static JsonObject Host(int id, string name, string status, int security, int total, string env) => new()
        {
            ["id"] = id, ["name"] = name, ["ip"] = "192.0.2." + id, ["operatingsystem_name"] = "RHEL 9.4", ["global_status_label"] = status, ["organization_name"] = "Example",
            ["content_facet_attributes"] = new JsonObject
            {
                ["errata_counts"] = new JsonObject { ["security"] = security, ["bugfix"] = 0, ["enhancement"] = 0, ["total"] = total },
                ["upgradable_package_count"] = total * 2, ["lifecycle_environment_name"] = env, ["content_view_name"] = "RHEL9-Base",
            },
        };

        private static JsonNode Page(params JsonObject[] items) => new JsonObject { ["total"] = items.Length, ["results"] = new JsonArray(items) };
        private static HttpResponseMessage Status(HttpStatusCode s) => new(s) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
    }
}
