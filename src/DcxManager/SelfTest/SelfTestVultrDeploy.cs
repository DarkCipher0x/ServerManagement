using System.Text.Json;
using System.Text.Json.Nodes;
using DcxManager.Services;
using DcxManager.Vultr;

namespace DcxManager;

/// <summary>
/// 0.6.17 (DarkCipher0x: "ask it to deploy a new server with the name and spec that i choose. If it doesnt match the spec,
/// choose the cheapest one available for the OS described. Then deploy it … and confirm"). The order worked out from a fake
/// Vultr's lists (operating systems, regions, plans with an IPv6-only, a GPU and a region-limited plan, SSH keys, the
/// account's servers), then the AI tool end to end: declined orders nothing, an approved one is created once, waited for,
/// saved as a host with the root password in the vault (never in the AI's answer) and its SSH port checked. No network.
/// </summary>
public static class VultrDeployTests
{
    private const string RootPw = "Demo-Root-Pass-qx7";

    private static List<JsonElement> Arr(string json) => JsonDocument.Parse(json).RootElement.EnumerateArray().Select(e => e.Clone()).ToList();

    private static readonly string Os = """
        [{"id":2284,"name":"Ubuntu 24.04 LTS x64","arch":"x64","family":"ubuntu"},{"id":2465,"name":"Ubuntu 25.04 x64","arch":"x64","family":"ubuntu"},
         {"id":1743,"name":"Ubuntu 22.04 LTS x64","arch":"x64","family":"ubuntu"},{"id":2136,"name":"Debian 12 x64 (bookworm)","arch":"x64","family":"debian"},
         {"id":2286,"name":"Ubuntu 24.04 LTS arm64","arch":"arm64","family":"ubuntu"},{"id":501,"name":"Windows 2022 Standard x64","arch":"x64","family":"windows"},
         {"id":159,"name":"Custom","arch":"x64","family":"iso"},{"id":186,"name":"Backup","arch":"x64","family":"backup"}]
        """;
    private static readonly string Regions = """
        [{"id":"lhr","city":"London","country":"GB"},{"id":"ams","city":"Amsterdam","country":"NL"},{"id":"ewr","city":"New Jersey","country":"US"}]
        """;
    private static readonly string Plans = """
        [{"id":"vc2-1c-0.5gb-v6","type":"vc2","vcpu_count":1,"ram":512,"disk":10,"monthly_cost":2.5,"locations":["lhr","ams"]},
         {"id":"vc2-1c-0.5gb","type":"vc2","vcpu_count":1,"ram":512,"disk":10,"monthly_cost":3.5,"locations":["lhr","ams","ewr"]},
         {"id":"vc2-1c-1gb","type":"vc2","vcpu_count":1,"ram":1024,"disk":25,"monthly_cost":5,"locations":["lhr","ams","ewr"]},
         {"id":"vc2-1c-2gb","type":"vc2","vcpu_count":1,"ram":2048,"disk":55,"monthly_cost":10,"locations":["lhr","ams","ewr"]},
         {"id":"vc2-2c-4gb","type":"vc2","vcpu_count":2,"ram":4096,"disk":80,"monthly_cost":20,"locations":["ams","ewr"]},
         {"id":"vhf-2c-4gb","type":"vhf","vcpu_count":2,"ram":4096,"disk":128,"monthly_cost":24,"locations":["lhr"]},
         {"id":"vcg-a16-2c-8g","type":"vcg","vcpu_count":2,"ram":8192,"disk":50,"monthly_cost":40,"locations":["lhr"]},
         {"id":"broken","type":"vc2","vcpu_count":"x","ram":null,"monthly_cost":"free"}]
        """;

    public static async Task RunAsync(Action<string> ok, Action<string, Exception?> fail)
    {
        // 1. Working the order out (pure).
        try
        {
            var os = Arr(Os); var regions = Arr(Regions); var plans = Arr(Plans);
            if (VultrDeploy.MatchOs(os, "ubuntu", out _) is not { Id: 2284 }) throw new Exception("“ubuntu” should be the newest LTS (24.04), not 25.04");
            if (VultrDeploy.MatchOs(os, "Ubuntu 24.04 arm", out _) is not { Id: 2286 }) throw new Exception("ARM build");
            if (VultrDeploy.MatchOs(os, "debian", out _) is not { Id: 2136, Windows: false }) throw new Exception("Debian");
            if (VultrDeploy.MatchOs(os, "Windows 2022", out _) is not { Id: 501, Windows: true }) throw new Exception("Windows");
            if (VultrDeploy.MatchOs(os, "freebsd 14", out var why) != null || !why.Contains("no Vultr operating system")) throw new Exception("an unknown OS matched");
            if (VultrDeploy.MatchOs(os, "custom", out _) != null || VultrDeploy.MatchOs(os, "backup", out _) != null) throw new Exception("an ISO / backup counted as an OS");
            if (VultrDeploy.MatchRegion(regions, "London") is not { Id: "lhr" } || VultrDeploy.MatchRegion(regions, "AMS") is not { Id: "ams" } || VultrDeploy.MatchRegion(regions, "nowhere") != null)
                throw new Exception("regions");
            if (VultrDeploy.Hostname("Web 03 — Prod!") != "web-03-prod" || VultrDeploy.Hostname("---") != "server" || VultrDeploy.Hostname(new string('a', 80)).Length != 63) throw new Exception("hostnames");
            string Pick(string? region, bool windows, int? c, double? r, int? d, string plan = "") =>
                VultrDeploy.ChoosePlan(plans, region, windows, c, r, d, plan, "Ubuntu 24.04 LTS x64", region ?? "") is { } x ? $"{x.Plan.GetProperty("id").GetString()}|{x.Matched}" : "none";
            if (Pick("lhr", false, 2, 4, null) != "vhf-2c-4gb|True") throw new Exception("2 vCPU / 4 GB in London: " + Pick("lhr", false, 2, 4, null));
            if (Pick("ams", false, 2, 4, null) != "vc2-2c-4gb|True") throw new Exception("2 vCPU / 4 GB in Amsterdam (the cheaper match)");
            if (Pick("lhr", false, 8, null, null) != "vc2-1c-0.5gb|False") throw new Exception("no match → cheapest with IPv4: " + Pick("lhr", false, 8, null, null));
            if (Pick("lhr", false, 2, 8, null) != "vc2-1c-0.5gb|False") throw new Exception("a GPU plan was used to match");
            if (Pick("lhr", true, null, null, null) != "vc2-1c-2gb|True") throw new Exception("Windows needs 2 GB: " + Pick("lhr", true, null, null, null));
            if (Pick("lhr", false, 1, 1, 20) != "vc2-1c-1gb|True" || Pick("lhr", false, 1, 1, 30) != "vc2-1c-0.5gb|False") throw new Exception("disk minimum");
            if (Pick("lhr", false, null, null, null, "vc2-2c-4gb") != "vc2-1c-0.5gb|False" || Pick("ams", false, null, null, null, "vc2-2c-4gb") != "vc2-2c-4gb|True") throw new Exception("an exact plan id");
            if (VultrDeploy.CheckName("") == null || VultrDeploy.CheckName("a\nb") == null || VultrDeploy.CheckName(new string('x', 65)) == null || VultrDeploy.CheckName("web-03") != null) throw new Exception("names");
            ok("0.6.17 Vultr deploy (order): the OS from Vultr's own list (newest LTS for “ubuntu”, ARM only when asked, ISOs / backups aren't operating systems, an unknown OS names close ones), regions by id or city, RFC 1123 host names; the cheapest plan matching the size exactly (vCPU, RAM, disk at least), in the region; no match → the cheapest plan that runs the OS, IPv6-only and GPU plans left out, Windows from 2 GB; an exact plan id when it is offered there");
        }
        catch (Exception ex) { fail("0.6.17 Vultr deploy (order)", ex); }

        // 2. The AI tool end to end against a fake Vultr.
        var s = AppSettings.Current;
        var allowWas = s.AiAllowServerDeploy;
        var created = new List<string>();
        int gets = 0;
        var probed = new List<(string, int)>();
        OperatorCapabilities.DeployPoll = TimeSpan.FromMilliseconds(20);
        OperatorCapabilities.DeployWait = TimeSpan.FromSeconds(5);
        OperatorCapabilities.DeployApi = _ => new VultrDeploy.Api
        {
            List = (path, _, _) => Task.FromResult(path switch
            {
                "/os" => Arr(Os), "/regions" => Arr(Regions), "/plans" => Arr(Plans),
                "/instances" => Arr("""[{"id":"old-1","region":"lhr"},{"id":"old-2","region":"lhr"},{"id":"old-3","region":"ewr"}]"""),
                "/ssh-keys" => Arr("""[{"id":"k-1","name":"laptop (demo)"}]"""),
                _ => new List<JsonElement>(),
            }),
            Create = (json, _) =>
            {
                lock (created) created.Add(json);
                return Task.FromResult(JsonDocument.Parse($$"""{"instance":{"id":"inst-{{created.Count}}","label":"x","status":"pending","main_ip":"0.0.0.0","default_password":"{{RootPw}}" } }""").RootElement.Clone());
            },
            Get = (path, _) =>
            {
                var n = Interlocked.Increment(ref gets);
                var id = path.Split('/').Last();
                var label = JsonNode.Parse(created[^1])!["label"]!.GetValue<string>();
                var region = JsonNode.Parse(created[^1])!["region"]!.GetValue<string>();
                var body = n % 2 == 1
                    ? $$"""{"instance":{"id":"{{id}}","label":"{{label}}","status":"pending","power_status":"stopped","server_status":"none","main_ip":"0.0.0.0","region":"{{region}}" } }"""
                    : $$"""{"instance":{"id":"{{id}}","label":"{{label}}","status":"active","power_status":"running","server_status":"ok","main_ip":"192.0.2.15{{created.Count}}","region":"{{region}}","plan":"vhf-2c-4gb","os":"Ubuntu 24.04 LTS x64","vcpu_count":2,"ram":4096 } }""";
                return Task.FromResult(JsonDocument.Parse(body).RootElement.Clone());
            },
        };
        OperatorCapabilities.DeployProbe = (h, p, _) => { lock (probed) probed.Add((h, p)); return Task.FromResult(new Net.NetTests.TcpResult("open", h, 1, "", "")); };
        try
        {
            s.AiAllowServerDeploy = true;
            var asked = new List<string>();
            bool answer = false;
            var ctx = new CapabilityContext((title, detail, _) => { asked.Add(title + "\n" + detail); return Task.FromResult(answer); });
            var args = new JsonObject { ["name"] = "web-03 (deploy demo)", ["os"] = "Ubuntu 24.04", ["region"] = "London", ["vcpus"] = 2, ["ram_gb"] = 4 };
            // Declined: nothing ordered.
            var r0 = await Capabilities.RunAsync("vultr_deploy_server", args, ctx, CancellationToken.None);
            if (!r0.Contains("declined") || created.Count != 0 || asked.Count != 1) throw new Exception("declined: " + r0);
            if (!asked[0].Contains("vhf-2c-4gb") || !asked[0].Contains("$24.00/month") || !asked[0].Contains("London") || !asked[0].Contains("Ubuntu 24.04 LTS x64") || !asked[0].Contains("bills"))
                throw new Exception("the approval doesn't show the order: " + asked[0]);
            // Approved: created once, waited for, saved, checked.
            answer = true;
            var r1 = await Capabilities.RunAsync("vultr_deploy_server", args, ctx, CancellationToken.None);
            if (created.Count != 1) throw new Exception($"{created.Count} orders");
            var body = JsonNode.Parse(created[0])!;
            if ((string?)body["region"] != "lhr" || (string?)body["plan"] != "vhf-2c-4gb" || (int?)body["os_id"] != 2284 || (string?)body["hostname"] != "web-03-deploy-demo" || (string?)body["label"] != "web-03 (deploy demo)")
                throw new Exception("order body: " + created[0]);
            if (r1.Contains(RootPw)) throw new Exception("the root password reached the AI");
            if (!r1.Contains("running at 192.0.2.151") || !r1.Contains("port 22 answers") || !r1.Contains("Saved as host") || probed.LastOrDefault() != ("192.0.2.151", 22)) throw new Exception("result: " + r1);
            var host = AppData.Current.Hosts.FirstOrDefault(h => h.Address == "192.0.2.151") ?? throw new Exception("no saved host");
            var cred = AppData.Current.FindCredential(host.CredentialId) ?? throw new Exception("no credential on the host");
            if (cred.Secret != RootPw || cred.Username != "root" || host.Auth != AuthMethod.Password) throw new Exception("root password credential");
            // No region: where the account's servers are (lhr, two of three); no size: the cheapest plan; an SSH key by name.
            var r2 = await Capabilities.RunAsync("vultr_deploy_server", new JsonObject { ["name"] = "tiny (deploy demo)", ["os"] = "debian", ["ssh_keys"] = "laptop (demo)" }, ctx, CancellationToken.None);
            var b2 = JsonNode.Parse(created[^1])!;
            if ((string?)b2["region"] != "lhr" || (string?)b2["plan"] != "vc2-1c-0.5gb" || (int?)b2["os_id"] != 2136 || b2["sshkey_id"]?[0]?.GetValue<string>() != "k-1") throw new Exception("defaults: " + created[^1]);
            // Unknown things are refused before any approval or order.
            int before = asked.Count;
            var r3 = await Capabilities.RunAsync("vultr_deploy_server", new JsonObject { ["name"] = "x", ["os"] = "plan9" }, ctx, CancellationToken.None);
            var r4 = await Capabilities.RunAsync("vultr_deploy_server", new JsonObject { ["name"] = "x", ["os"] = "debian", ["ssh_keys"] = "nobody's key" }, ctx, CancellationToken.None);
            if (!r3.StartsWith("ERROR") || !r4.StartsWith("ERROR") || asked.Count != before || created.Count != 2) throw new Exception($"refusals: {r3} | {r4}");
            s.AiAllowServerDeploy = false;
            var r5 = await Capabilities.RunAsync("vultr_deploy_server", args, ctx, CancellationToken.None);
            if (!r5.StartsWith("REFUSED") || created.Count != 2) throw new Exception("ordered while switched off");
            var month = AuditLog.Months().FirstOrDefault();
            var audit = month == null ? "" : string.Join("\n", AuditLog.Read(month).Where(e => e.Time > DateTime.Now.AddMinutes(-10)).Select(e => $"{e.Target} {e.Action} {e.Result}"));
            if (!audit.Contains("Vultr server deployed by the AI") || audit.Contains(RootPw)) throw new Exception("audit");
            ok("0.6.17 Vultr deploy (AI tool): the approval shows plan, size, region, OS and the monthly price; declined orders nothing; approved is ordered once (never retried), waited for until running, saved as a host with Vultr's root password as a vault credential (never in the AI's answer or the audit log), and its SSH port checked; no region → where the account's servers are, no size → the cheapest plan, SSH keys by name; an unknown OS or key is refused before asking; off by policy → refused");
        }
        catch (Exception ex) { fail("0.6.17 Vultr deploy (AI tool)", ex); }
        finally
        {
            s.AiAllowServerDeploy = allowWas;
            OperatorCapabilities.DeployApi = null; OperatorCapabilities.DeployProbe = null;
            OperatorCapabilities.DeployPoll = TimeSpan.FromSeconds(10); OperatorCapabilities.DeployWait = TimeSpan.FromMinutes(10);
            var ids = AppData.Current.Hosts.Where(h => h.Name.Contains("(deploy demo)") || h.Address.StartsWith("192.0.2.15")).Select(h => h.CredentialId).ToList();
            AppData.Current.Hosts.RemoveAll(h => h.Name.Contains("(deploy demo)") || h.Address.StartsWith("192.0.2.15"));
            AppData.Current.Credentials.RemoveAll(c => c.Name.Contains("(deploy demo)") || ids.Contains(c.Id) && c.Notes.Contains("AI Engineer deployed"));
            try { AppData.Save(); } catch { }
        }
    }
}
