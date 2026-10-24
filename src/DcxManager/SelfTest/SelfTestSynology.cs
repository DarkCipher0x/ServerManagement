using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using DcxManager.Infra;
using DcxManager.Services;

namespace DcxManager;

/// <summary>
/// 0.3.0 (§14): the Synology DSM provider against a fake DSM Web API (an HTTP handler; nothing on the network). https only;
/// the password, session id and SynoToken never appear in a URL; two-factor sign-in once with a code stores DSM's device
/// token sealed in the vault and later sign-ins use it; discovery hides packages that aren't installed; the inventory reads
/// system, storage, shares, packages, containers, VMs, Hyper Backup and users; a container stop is verified by re-reading;
/// the session is signed out after use; the built-in "admin" account is warned about.
/// </summary>
public static class SynologyTests
{
    public static async Task RunAsync(Action<string> ok, Action<string, Exception?> fail)
    {
        var cred = new Credential { Name = "selftest-dsm", Kind = CredentialKind.Password, Username = "dcx-svc", Secret = "Dsm-Secret-qx9" };
        AppData.Current.Credentials.Add(cred);
        try
        {
            if (SynologyProvider.ValidateEndpoint("http://nas.lan:5000") == null || SynologyProvider.ValidateEndpoint("https://nas.lan:5001") != null) throw new Exception("https-only check");
            var fake = new FakeDsm { RequireOtp = true };
            var c = new ProviderConfig { Kind = "synology", Name = "selftest-nas", Endpoint = "https://nas.example.test:5001", CredentialId = cred.Id, AllowChanges = true };
            var p = new SynologyProvider(c, fake) { PollEvery = TimeSpan.FromMilliseconds(20), VerifyFor = TimeSpan.FromSeconds(5) };

            // 2FA: first a sign-in object asking for the code, then the code stores the device token.
            var first = await p.InventoryAsync(CancellationToken.None);
            if (first.Count != 1 || first[0].Kind != "sign-in") throw new Exception("2FA not asked: " + string.Join(",", first.Select(o => o.Kind)));
            var r = await p.RunAsync(SynologyProvider.SignIn, first[0], new Dictionary<string, string> { ["code"] = "123456" }, CancellationToken.None);
            if (!r.Accepted || cred.Passphrase != "DEVICE-TOKEN-qx9") throw new Exception("device token not stored: " + r.Message);

            var caps = await p.DetectAsync(CancellationToken.None);
            if (!caps.Reachable || !caps.Detail.Contains("DS920+") || !caps.Notes.Any(n => n.Contains("Virtual Machine Manager"))) throw new Exception("detect: " + caps.Detail + " | " + string.Join(" | ", caps.Notes));
            var inv = await p.InventoryAsync(CancellationToken.None);
            foreach (var k in new[] { "system", "pool", "volume", "disk", "share", "package", "container", "backup", "user" })
                if (!inv.Any(o => o.Kind == k)) throw new Exception("inventory has no " + k + ": " + string.Join(",", inv.Select(o => o.Kind).Distinct()));
            if (inv.Any(o => o.Kind == "vm")) throw new Exception("VMs listed although VMM isn't installed");
            var ctr = inv.Single(o => o.Kind == "container");
            var stop = await p.RunAsync(SynologyProvider.CtrStop, ctr, CancellationToken.None);
            if (!stop.Verified) throw new Exception("container stop: " + stop.Message);

            // Security: no secret in any URL; every call after login carries the session in the body and the token header.
            if (fake.Urls.Any(u => u.Contains("Dsm-Secret") || u.Contains("_sid") || u.Contains("passwd") || u.Contains("SID-"))) throw new Exception("a secret in a URL: " + string.Join(" ", fake.Urls));
            if (!fake.Bodies.Any(b => b.Contains("device_id=DEVICE-TOKEN-qx9"))) throw new Exception("the remembered device wasn't used");
            if (fake.SessionCallsWithoutToken > 0) throw new Exception("calls without the SynoToken header: " + fake.SessionCallsWithoutToken);
            if (fake.Logouts < 3 || p.SignedInForTest) throw new Exception("sessions not signed out");

            cred.Username = "admin";
            var c2 = await new SynologyProvider(c, new FakeDsm()).DetectAsync(CancellationToken.None);
            if (!c2.Notes.Any(n => n.Contains("built-in “admin”"))) throw new Exception("no warning for the admin account");
            ok("Synology DSM: https only; two-factor sign-in once with a code stores DSM's device token sealed in the vault and later sign-ins use it; discovery hides what isn't installed (VMM); system, pools, volumes, disks, shares, packages, containers, Hyper Backup and users listed; a container stop verified by re-reading; the password, session and token never in a URL; every session signed out; the built-in admin account warned about");
        }
        catch (Exception ex) { fail("Synology DSM", ex); }
        finally { AppData.Current.Credentials.Remove(cred); AppData.Save(); }
    }

    private sealed class FakeDsm : HttpMessageHandler
    {
        public bool RequireOtp { get; set; }
        public List<string> Urls { get; } = new();
        public List<string> Bodies { get; } = new();
        public int SessionCallsWithoutToken, Logouts;
        private string _ctr = "running";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Urls.Add(req.RequestUri!.ToString());
            var body = req.Content == null ? "" : await req.Content.ReadAsStringAsync(ct);
            Bodies.Add(body);
            var f = body.Split('&').Select(kv => kv.Split('=', 2)).ToDictionary(x => Uri.UnescapeDataString(x[0]), x => Uri.UnescapeDataString(x.Length > 1 ? x[1].Replace('+', ' ') : ""));
            string api = f.GetValueOrDefault("api") ?? "", method = f.GetValueOrDefault("method") ?? "";
            JsonNode? data;
            if (api != "SYNO.API.Info" && api != "SYNO.API.Auth" && !req.Headers.Contains("X-SYNO-TOKEN")) SessionCallsWithoutToken++;
            switch (api, method)
            {
                case ("SYNO.API.Info", _):
                    data = new JsonObject();
                    foreach (var a in new[] { "SYNO.API.Auth", "SYNO.Core.System", "SYNO.Core.System.Utilization", "SYNO.Storage.CGI.Storage", "SYNO.Core.Share", "SYNO.Core.Package", "SYNO.Core.Package.Control", "SYNO.Docker.Container", "SYNO.Backup.Task", "SYNO.Core.User" })
                        data[a] = new JsonObject { ["path"] = "entry.cgi", ["minVersion"] = 1, ["maxVersion"] = 7 };
                    break;
                case ("SYNO.API.Auth", "login"):
                    if (f.GetValueOrDefault("passwd") != "Dsm-Secret-qx9") return Fail(400);
                    if (RequireOtp && f.GetValueOrDefault("otp_code") != "123456" && f.GetValueOrDefault("device_id") != "DEVICE-TOKEN-qx9") return Fail(403);
                    data = new JsonObject { ["sid"] = "SID-qx9", ["synotoken"] = "TOKEN-qx9" };
                    if (f.ContainsKey("otp_code")) data["did"] = "DEVICE-TOKEN-qx9";
                    break;
                case ("SYNO.API.Auth", "logout"): Logouts++; data = new JsonObject(); break;
                default:
                    if (f.GetValueOrDefault("_sid") != "SID-qx9") return Fail(119);
                    data = (api, method) switch
                    {
                        ("SYNO.Core.System", "info") => new JsonObject { ["model"] = "DS920+", ["firmware_ver"] = "DSM 7.2.2-72806", ["ram_size"] = 8192, ["up_time"] = "10:00:00", ["sys_temp"] = 41 },
                        ("SYNO.Core.System.Utilization", _) => new JsonObject { ["cpu"] = new JsonObject { ["user_load"] = 3, ["system_load"] = 2 }, ["memory"] = new JsonObject { ["real_usage"] = 31 } },
                        ("SYNO.Storage.CGI.Storage", _) => new JsonObject
                        {
                            ["storagePools"] = new JsonArray(new JsonObject { ["id"] = "reuse_1", ["status"] = "normal", ["size"] = new JsonObject { ["used"] = "1000000000", ["total"] = "4000000000" } }),
                            ["volumes"] = new JsonArray(new JsonObject { ["id"] = "volume_1", ["vol_path"] = "/volume1", ["status"] = "normal", ["fs_type"] = "btrfs", ["size"] = new JsonObject { ["used"] = "1", ["total"] = "2" } }),
                            ["disks"] = new JsonArray(new JsonObject { ["id"] = "sata1", ["name"] = "Drive 1", ["status"] = "normal", ["smart_status"] = "normal", ["temp"] = 35, ["model"] = "HAT5300" }),
                        },
                        ("SYNO.Core.Share", _) => new JsonObject { ["shares"] = new JsonArray(new JsonObject { ["name"] = "backups", ["additional"] = new JsonObject { ["encryption"] = 1, ["vol_path"] = "/volume1" } }) },
                        ("SYNO.Core.Package", "list") => new JsonObject { ["packages"] = new JsonArray(new JsonObject { ["id"] = "ContainerManager", ["name"] = "Container Manager", ["version"] = "24.0", ["additional"] = new JsonObject { ["status"] = "running" } }) },
                        ("SYNO.Docker.Container", "list") => new JsonObject { ["containers"] = new JsonArray(new JsonObject { ["name"] = "web", ["status"] = _ctr, ["image"] = "nginx:1.27" }) },
                        ("SYNO.Docker.Container", "stop") => Set(ref _ctr, "stopped"),
                        ("SYNO.Backup.Task", "list") => new JsonObject { ["task_list"] = new JsonArray(new JsonObject { ["task_id"] = 1, ["name"] = "Nightly", ["state"] = "idle" }) },
                        ("SYNO.Core.User", "list") => new JsonObject { ["users"] = new JsonArray(new JsonObject { ["name"] = "dcx-svc" }) },
                        _ => null,
                    };
                    if (data == null) return Fail(103);
                    break;
            }
            return Json(new JsonObject { ["success"] = true, ["data"] = data });
        }

        private static JsonNode Set(ref string field, string value) { field = value; return new JsonObject(); }
        private static HttpResponseMessage Fail(int code) => Json(new JsonObject { ["success"] = false, ["error"] = new JsonObject { ["code"] = code } });
        private static HttpResponseMessage Json(JsonNode n) => new(HttpStatusCode.OK) { Content = new StringContent(n.ToJsonString(), Encoding.UTF8, "application/json") };
    }
}
