using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DcxManager.Cloud;
using DcxManager.Contabo;
using DcxManager.Hetzner;
using DcxManager.Infra;
using DcxManager.Services;
using DcxManager.Vultr;

namespace DcxManager;

/// <summary>
/// ADR-031 (0.1.14): the provider controls added to cover each management API (docs/PROVIDER-API-COVERAGE.md), each against
/// a fake of that API — Proxmox snapshots / pause / clone / migrate / vzdump / template and the read views; vCenter vCPU /
/// memory / delete / guest info / host disconnect; Hyper-V settings, rename, switch, delete and details through the
/// PowerShell seam; Docker pause / kill / inspect / top / stats and image / network / volume removal over the Engine API;
/// Unraid's extra mutations; and the Hetzner / Contabo / Vultr catalog additions checked against the official specs.
/// Nothing here opens a window or a dialog, and nothing reaches a real server.
/// </summary>
public static class InfraCoverageTests
{
    /// <summary>Contabo API v1 operations (api.contabo.com api-v1.yaml, 2026-10) the catalog may use, without the /v1 prefix.
    /// Users, roles, domains and troubleshooting are deliberately not in the app (docs/PROVIDER-API-COVERAGE.md).</summary>
    private const string ContaboSpec =
        "GET /tags;POST /tags;GET /tags/{id};PATCH /tags/{id};DELETE /tags/{id};GET /tags/{id}/assignments;GET /tags/{id}/assignments/{id}/{id};POST /tags/{id}/assignments/{id}/{id};" +
        "DELETE /tags/{id}/assignments/{id}/{id};GET /dns/ptrs/{id};PUT /dns/ptrs/{id};DELETE /dns/ptrs/{id};POST /dns/ptrs;GET /dns/ptrs;GET /dns/zones;POST /dns/zones;GET /dns/zones/{id};" +
        "DELETE /dns/zones/{id};GET /dns/zones/{id}/records;POST /dns/zones/{id}/records;PATCH /dns/zones/{id}/records/{id};DELETE /dns/zones/{id}/records/{id};GET /vips;GET /vips/{id};" +
        "POST /vips/{id}/{id}/{id};DELETE /vips/{id}/{id}/{id};GET /secrets;POST /secrets;GET /secrets/{id};PATCH /secrets/{id};DELETE /secrets/{id};GET /private-networks;POST /private-networks;" +
        "PATCH /private-networks/{id};GET /private-networks/{id};DELETE /private-networks/{id};POST /private-networks/{id}/instances/{id};DELETE /private-networks/{id}/instances/{id};" +
        "POST /firewalls;GET /firewalls;DELETE /firewalls/{id};GET /firewalls/{id};PATCH /firewalls/{id};PUT /firewalls/{id};POST /firewalls/{id}/instances/{id};DELETE /firewalls/{id}/instances/{id};" +
        "GET /firewalls/preset-rules;POST /compute/instances/{id}/actions/start;POST /compute/instances/{id}/actions/restart;POST /compute/instances/{id}/actions/stop;" +
        "POST /compute/instances/{id}/actions/shutdown;POST /compute/instances/{id}/actions/rescue;POST /compute/instances/{id}/actions/resetPassword;GET /compute/instances;POST /compute/instances;" +
        "GET /compute/instances/{id};PATCH /compute/instances/{id};PUT /compute/instances/{id};GET /compute/instances/{id}/products/available;POST /compute/instances/{id}/cancel;" +
        "POST /compute/instances/{id}/upgrade;GET /compute/images;POST /compute/images;GET /compute/images/{id};PATCH /compute/images/{id};DELETE /compute/images/{id};GET /compute/images/stats;" +
        "GET /compute/instances/{id}/snapshots;POST /compute/instances/{id}/snapshots;GET /compute/instances/{id}/snapshots/{id};PATCH /compute/instances/{id}/snapshots/{id};" +
        "DELETE /compute/instances/{id}/snapshots/{id};POST /compute/instances/{id}/snapshots/{id}/rollback;GET /object-storages;POST /object-storages;GET /data-centers;GET /object-storages/{id};" +
        "PATCH /object-storages/{id};POST /object-storages/{id}/resize;GET /object-storages/{id}/stats;PATCH /object-storages/{id}/cancel;GET /compute/instances/actions/audits";

    /// <summary>The Vultr v2 operations added in 0.1.14, as Vultr's official Go client (govultr, 2026-10) calls them.</summary>
    private const string VultrAdded =
        "POST /instances/{id}/ipv6/reverse;GET /instances/{id}/ipv6/reverse;DELETE /instances/{id}/ipv6/reverse/{id};POST /instances/{id}/ipv4/reverse/default;DELETE /instances/{id}/ipv4/{id};" +
        "GET /instances/{id}/iso;GET /bare-metals/{id}/ipv6;GET /bare-metals/{id}/upgrades;GET /bare-metals/{id}/vpcs;POST /bare-metals/{id}/vpcs/attach;POST /bare-metals/{id}/vpcs/detach;" +
        "GET /blocks/snapshots;POST /blocks/snapshots;PUT /blocks/snapshots/{id};DELETE /blocks/snapshots/{id};GET /blocks/snapshots/{id};GET /vfs;POST /vfs;PUT /vfs/{id};DELETE /vfs/{id};GET /vfs/{id};" +
        "PUT /vfs/{id}/attachments/{id};GET /vfs/{id}/attachments;DELETE /vfs/{id}/attachments/{id};GET /subaccounts;POST /subaccounts";

    private static string Norm(string methodAndPath) => Regex.Replace(methodAndPath.Split('?')[0], @"\{[^}]+\}", "{id}");

    public static async Task RunAsync(Action<string> ok, Action<string, Exception?> fail)
    {
        static Dictionary<string, string> In(params (string K, string V)[] kv) => kv.ToDictionary(x => x.K, x => x.V);

        // 1. Proxmox VE: snapshots, pause / resume, clone, migrate, backup, template, read views, per-action privileges.
        var token = new Credential { Name = "pve token (coverage)", Kind = CredentialKind.Password, Username = "dcx@pve!cov", Secret = "cov-token-5150" };
        AppData.Current.Credentials.Add(token);
        try
        {
            var pve = new FakePve();
            var cfg = new ProviderConfig { Kind = "proxmox", Name = "pve-cov", Endpoint = "https://pve.test:8006", CredentialId = token.Id, AllowChanges = true, Environment = "LAB" };
            var p = new ProxmoxProvider(cfg, pve) { VerifyFor = TimeSpan.FromSeconds(4), PollEvery = TimeSpan.FromMilliseconds(20) };
            var caps = await p.DetectAsync(CancellationToken.None);
            if (!caps.Reachable || !caps.CanControl || !caps.Notes.Any(n => n.Contains("VM.Migrate"))) throw new Exception("detect: " + caps.Detail + " | " + string.Join(" | ", caps.Notes));
            var inv = await p.InventoryAsync(CancellationToken.None);
            var vm = inv.First(o => o.Name == "web-01"); var ct = inv.First(o => o.Name == "dns"); var tmpl = inv.First(o => o.Status == "template");
            var node = inv.First(o => o.Kind == "node"); var store = inv.First(o => o.Kind == "storage");
            var offer = Infra.Infra.Offer(p, vm, caps);
            if (offer.First(o => o.Action.Id == "migrate").Why?.Contains("VM.Migrate") != true || !offer.First(o => o.Action.Id == "snapshot").Enabled || !offer.First(o => o.Action.Id == "clone").Enabled)
                throw new Exception("per-action privileges: " + string.Join(", ", offer.Select(o => o.Action.Id + "=" + (o.Enabled ? "on" : o.Why))));
            if (offer.First(o => o.Action.Id == "template").Why != "shut it down first") throw new Exception("template on a running VM");
            if (p.ActionsFor(tmpl).Select(a => a.Id).OrderBy(x => x).SequenceEqual(new[] { "backup", "clone" }) != true) throw new Exception("template actions");
            if (p.ActionsFor(ct).Any(a => a.Id is "suspend" or "agent") || p.ActionsFor(vm).All(a => a.Id != "suspend")) throw new Exception("VM-only actions");

            var rs = await p.RunAsync(ProxmoxProvider.SnapshotVm, vm, In(("snapname", "before-patch"), ("description", "pre"), ("vmstate", "yes")), CancellationToken.None);
            if (!rs.Verified || pve.LastForm.GetValueOrDefault("vmstate") != "1" || pve.LastForm.GetValueOrDefault("snapname") != "before-patch") throw new Exception("snapshot: " + rs.Message);
            var dup = await p.RunAsync(ProxmoxProvider.SnapshotVm, vm, In(("snapname", "before-patch"), ("vmstate", "no")), CancellationToken.None);
            var bad = await p.RunAsync(ProxmoxProvider.SnapshotVm, vm, In(("snapname", "x; rm -rf /"), ("vmstate", "no")), CancellationToken.None);
            int posts = pve.Posts;
            var missing = await p.RunAsync(ProxmoxProvider.Rollback, vm, In(("snapname", "nope-1")), CancellationToken.None);
            if (dup.Accepted || bad.Accepted || missing.Accepted || pve.Posts != posts) throw new Exception("refusals: " + dup.Message + " | " + bad.Message + " | " + missing.Message);
            var list = await p.RunAsync(ProxmoxProvider.Snapshots, vm, CancellationToken.None);
            if (list.Output?.Contains("before-patch") != true || !list.Output.Contains("RAM")) throw new Exception("snapshot list: " + list.Output);
            var rb = await p.RunAsync(ProxmoxProvider.Rollback, vm, In(("snapname", "before-patch")), CancellationToken.None);
            if (!rb.Verified) throw new Exception("rollback: " + rb.Message);
            var del = await p.RunAsync(ProxmoxProvider.DeleteSnapshot, vm, In(("snapname", "before-patch")), CancellationToken.None);
            if (!del.Verified || pve.Snaps.Contains("before-patch")) throw new Exception("delete snapshot: " + del.Message);

            var sus = await p.RunAsync(ProxmoxProvider.Suspend, vm, CancellationToken.None);
            var res = await p.RunAsync(ProxmoxProvider.Resume, vm, CancellationToken.None);
            if (!sus.Verified || !res.Verified || pve.Qmp != "running") throw new Exception("pause/resume: " + sus.Message + " | " + res.Message);

            var cl = await p.RunAsync(ProxmoxProvider.Clone, vm, In(("name", "web-02"), ("full", "yes")), CancellationToken.None);
            if (!cl.Verified || pve.LastForm.GetValueOrDefault("newid") != "105" || pve.LastForm.GetValueOrDefault("name") != "web-02") throw new Exception("clone (next id): " + cl.Message);
            var clBad = await p.RunAsync(ProxmoxProvider.Clone, vm, In(("newid", "7"), ("full", "yes")), CancellationToken.None);
            if (clBad.Accepted) throw new Exception("a VMID below 100 was sent");

            var bk = await p.RunAsync(ProxmoxProvider.Backup, vm, In(("storage", "local"), ("mode", "snapshot"), ("compress", "zstd")), CancellationToken.None);
            if (!bk.Verified || pve.LastForm.GetValueOrDefault("vmid") != "100" || !bk.Message.Contains("vzdump-qemu-100")) throw new Exception("backup: " + bk.Message);
            pve.Status = "stopped";
            var tp = await p.RunAsync(ProxmoxProvider.Template, vm with { Status = "stopped" }, CancellationToken.None);
            if (!tp.Verified) throw new Exception("template: " + tp.Message);
            pve.Status = "running";

            var tasks = await p.RunAsync(ProxmoxProvider.Tasks, node, CancellationToken.None);
            var content = await p.RunAsync(ProxmoxProvider.Content, store, CancellationToken.None);
            if (tasks.Output?.Contains("vzdump") != true || content.Output?.Contains("local:backup/") != true) throw new Exception("read views: " + tasks.Output + " | " + content.Output);
            if (pve.Calls.Any(c => c.Contains("cov-token-5150"))) throw new Exception("token in a URL");
            ok("Proxmox coverage: per-action privileges (VM.Migrate missing → migrate disabled with the reason; template only when stopped; templates offer clone/backup only; pause and guest agent VM-only), snapshot with RAM verified by re-listing, duplicate / unsafe / unknown names refused before any request, rollback verified by the current state's parent, snapshot delete verified, pause / resume verified by qmpstatus, clone with cluster/nextid verified, VMID < 100 refused, vzdump verified by the new backup file on the storage, template verified in the config, node tasks and storage contents read");
        }
        catch (Exception ex) { fail("Proxmox coverage", ex); }
        finally { AppData.Current.Credentials.Remove(token); }

        // 2. vCenter: vCPU / memory (re-read), delete (404 afterwards), guest info, host disconnect / reconnect.
        var vcCred = new Credential { Name = "vcenter (coverage)", Kind = CredentialKind.Password, Username = "svc-dcx@vsphere.local", Secret = "Vc-Cov-Pa55!" };
        AppData.Current.Credentials.Add(vcCred);
        try
        {
            var vc = new FakeVc(vcCred.Secret);
            var cfg = new ProviderConfig { Kind = "vsphere", Name = "vc-cov", Endpoint = "https://vcenter.lab.example", CredentialId = vcCred.Id, AllowChanges = true, Environment = "LAB" };
            using var p = new VSphereProvider(cfg, vc) { PollEvery = TimeSpan.FromMilliseconds(10), VerifyFor = TimeSpan.FromSeconds(3) };
            var caps = await p.DetectAsync(CancellationToken.None);
            var inv = await p.InventoryAsync(CancellationToken.None);
            var web = inv.First(o => o.Name == "web-vm"); var db = inv.First(o => o.Name == "db-vm"); var host = inv.First(o => o.Kind == "host");
            if (!caps.Reachable || host.Props.GetValueOrDefault("host") != "host-10" || host.Status != "online") throw new Exception("inventory: " + caps.Detail);
            if (Infra.Infra.Offer(p, web, caps).First(o => o.Action.Id == "delete").Enabled || Infra.Infra.Offer(p, host, caps).First(o => o.Action.Id == "host-connect").Enabled) throw new Exception("state-aware offers");
            var cpu = await p.RunAsync(VSphereProvider.Cpu, web, In(("count", "4")), CancellationToken.None);
            var mem = await p.RunAsync(VSphereProvider.Memory, web, In(("sizeMiB", "8192")), CancellationToken.None);
            if (!cpu.Verified || !mem.Verified || vc.LastPatch?["size_MiB"]?.GetValue<long>() != 8192) throw new Exception("hardware: " + cpu.Message + " | " + mem.Message);
            vc.RefuseHotAdd = true;
            var hot = await p.RunAsync(VSphereProvider.Cpu, web, In(("count", "6")), CancellationToken.None);
            if (hot.Accepted || !hot.Message.Contains("hot_add")) throw new Exception("vCenter's refusal: " + hot.Message);
            var cpuBad = await p.RunAsync(VSphereProvider.Cpu, web, In(("count", "0")), CancellationToken.None);
            if (cpuBad.Accepted) throw new Exception("0 vCPU sent");
            var info = await p.RunAsync(VSphereProvider.GuestInfo, web, CancellationToken.None);
            if (info.Output?.Contains("Ubuntu") != true || info.Output.Contains(vcCred.Secret) || !info.Output.Contains("run_state")) throw new Exception("guest info: " + info.Output);
            var d = await p.RunAsync(VSphereProvider.Delete, db, CancellationToken.None);
            if (!d.Verified || vc.Vms.ContainsKey("vm-102")) throw new Exception("delete: " + d.Message);
            var off = await p.RunAsync(VSphereProvider.HostDisconnect, host, CancellationToken.None);
            var on = await p.RunAsync(VSphereProvider.HostConnect, host with { Status = "disconnected" }, CancellationToken.None);
            if (!off.Verified || !on.Verified || vc.HostState != "CONNECTED") throw new Exception("host: " + off.Message + " | " + on.Message);
            var evil = await p.RunAsync(VSphereProvider.HostDisconnect, host with { Props = new Dictionary<string, string> { ["host"] = "host-1/../x" } }, CancellationToken.None);
            if (evil.Accepted) throw new Exception("unsafe host id sent");
            ok("vCenter coverage: vCPU and memory set with PATCH and verified by re-reading (size_MiB), vCenter's hot-add refusal shown with its reason, out-of-range values refused, guest identity / interfaces / Tools read, delete of a powered-off VM verified by a 404, delete never offered for a running VM, host disconnect / reconnect verified by connection_state, unsafe host ids refused");
        }
        catch (Exception ex) { fail("vCenter coverage", ex); }
        finally { AppData.Current.Credentials.Remove(vcCred); }

        // 3. Hyper-V: settings, rename, switch, delete and details through the PowerShell seam (values only as JSON).
        var runWas = HyperVProvider.Run;
        try
        {
            const string vmA = "6f2b4c1e-1111-4a6b-9c1d-0000000000a1", cpA = "6f2b4c1e-2222-4a6b-9c1d-0000000000a2";
            string name = "dc01", state = "Off", sw = "Internal"; int cpus = 2; long startup = 2147483648; string cpName = "before-patch";
            var scripts = new List<(string Script, JsonObject In)>();
            string? reply = null;
            string Inv() => "{\"host\":{\"name\":\"HV01\",\"cpus\":16,\"memory\":68719476736,\"vmPath\":\"D:\\\\VMs\",\"vhdPath\":\"D:\\\\VHDs\"}," +
                $"\"vms\":{{\"id\":\"{vmA}\",\"name\":{JsonSerializer.Serialize(name)},\"state\":\"{state}\",\"cpu\":{cpus},\"mem\":0,\"memStartup\":{startup},\"dynamic\":false,\"switch\":\"{sw}\",\"up\":0,\"gen\":2,\"ver\":\"10.0\",\"status\":\"Operating normally\"}}," +
                $"\"switches\":[{{\"id\":\"s1\",\"name\":\"External\",\"type\":\"External\"}},{{\"id\":\"s2\",\"name\":\"Internal\",\"type\":\"Internal\"}}],\"checkpoints\":{{\"id\":\"{cpA}\",\"name\":{JsonSerializer.Serialize(cpName)},\"vm\":\"dc01\",\"vmId\":\"{vmA}\",\"created\":\"2026-10-18 20:00\"}}}}";
            HyperVProvider.Run = (script, input, _, _) =>
            {
                var j = JsonNode.Parse(input)!.AsObject();
                scripts.Add((script, j));
                if (script.Contains("Set-VMProcessor")) cpus = int.Parse(j["arg_count"]!.ToString());
                if (script.Contains("Set-VMMemory")) startup = long.Parse(j["arg_memoryMb"]!.ToString()) * 1024 * 1024;
                if (script.Contains("Rename-VMSnapshot")) cpName = j["arg_name"]!.ToString();
                else if (script.Contains("Rename-VM ")) name = j["arg_name"]!.ToString();
                if (script.Contains("Connect-VMNetworkAdapter")) sw = j["arg_switch"]!.ToString();
                if (script.Contains("Remove-VM ")) state = "gone";
                var outText = reply ?? (state == "gone" ? Inv().Replace($"\"id\":\"{vmA}\",\"name\"", "\"id\":\"not-a-guid\",\"name\"") : Inv());
                reply = null;
                return Task.FromResult((0, outText, ""));
            };
            var hv = new HyperVProvider(new ProviderConfig { Kind = "hyperv", Name = "hv-cov", Endpoint = "", AllowChanges = true, Environment = "LAB" });
            var caps = await hv.DetectAsync(CancellationToken.None);
            var inv = await hv.InventoryAsync(CancellationToken.None);
            var vm = inv.Single(o => o.Kind == "vm"); var cp = inv.Single(o => o.Kind == "checkpoint");
            if (vm.Props.GetValueOrDefault("cpu") != "2" || vm.Props.GetValueOrDefault("switch") != "Internal") throw new Exception("inventory props");
            if (Infra.Infra.FillDefault(HyperVProvider.Cpu.Inputs[0], vm) != "2" || Infra.Infra.FillDefault(new InfraInput("x", "x", "{name}-{unknownProp}"), vm) != "dc01-") throw new Exception("defaults from props");
            if (Infra.Infra.Offer(hv, vm with { Status = "running" }, caps).First(o => o.Action.Id == "cpu").Why != "turn the VM off first") throw new Exception("cpu needs the VM off");
            var c = await hv.RunAsync(HyperVProvider.Cpu, vm, In(("count", "4")), CancellationToken.None);
            var m = await hv.RunAsync(HyperVProvider.Memory, vm, In(("memoryMb", "4096")), CancellationToken.None);
            if (!c.Verified || !m.Verified) throw new Exception("settings: " + c.Message + " | " + m.Message);
            var hostile = "web'; Remove-VM -Force; '";
            var rn = await hv.RunAsync(HyperVProvider.Rename, vm, In(("name", hostile)), CancellationToken.None);
            var renameScript = scripts.Last(s => s.Script.Contains("Rename-VM")).Script;
            if (!rn.Verified || renameScript.Contains(hostile) || name != hostile) throw new Exception("rename: " + rn.Message);
            var cs = await hv.RunAsync(HyperVProvider.ConnectSwitch, vm, In(("switch", "External")), CancellationToken.None);
            var rc = await hv.RunAsync(HyperVProvider.RenameCheckpoint, cp, In(("name", "after-patch")), CancellationToken.None);
            if (!cs.Verified || !rc.Verified) throw new Exception("switch / checkpoint rename: " + cs.Message + " | " + rc.Message);
            reply = "{\"adapters\":{\"name\":\"Network Adapter\",\"switch\":\"External\",\"mac\":\"00155D000001\",\"ips\":[\"192.0.2.10\",\"fe80::1\"],\"status\":\"Ok\"},\"disks\":[{\"controller\":\"SCSI 0:0\",\"path\":\"D:\\\\VHDs\\\\dc01.vhdx\"}],\"integration\":[{\"name\":\"Heartbeat\",\"enabled\":true,\"status\":\"OK\"}],\"automaticStart\":\"StartIfRunning\",\"automaticStop\":\"Save\"}";
            var det = await hv.RunAsync(HyperVProvider.Details, vm, CancellationToken.None);
            if (det.Output?.Contains("192.0.2.10") != true || !det.Output.Contains("dc01.vhdx") || !det.Output.Contains("Heartbeat")) throw new Exception("details: " + det.Output);
            var badCpu = await hv.RunAsync(HyperVProvider.Cpu, vm, In(("count", "0")), CancellationToken.None);
            if (badCpu.Accepted) throw new Exception("0 processors reached PowerShell");
            var rm = await hv.RunAsync(HyperVProvider.RemoveVm, vm, CancellationToken.None);
            if (!rm.Verified) throw new Exception("delete VM: " + rm.Message);
            ok("Hyper-V coverage: vCPU count and startup memory verified by re-reading, input defaults filled from the VM ({cpu}), changes needing the VM off refused while it runs, a hostile new name passed only as JSON data and verified, switch connection and checkpoint rename verified, details (adapters + IPs, disks, integration services) formatted, out-of-range values refused before PowerShell, delete VM verified gone");
        }
        catch (Exception ex) { fail("Hyper-V coverage", ex); }
        finally { HyperVProvider.Run = runWas; }

        // 4. Docker Engine API: pause / unpause / kill, inspect / top / stats, image / network / volume removal.
        try
        {
            var eng = new FakeEngine();
            var cfg = new ProviderConfig { Kind = "docker-local", Name = "docker-cov", AllowChanges = true, Environment = "LAB" };
            var p = new DockerProvider(cfg, DockerBackend.Api(new HttpClient(eng) { BaseAddress = new Uri("http://docker/") }, "fake engine", NetClass.Local)) { VerifyFor = TimeSpan.FromSeconds(2) };
            var caps = await p.DetectAsync(CancellationToken.None);
            var inv = await p.InventoryAsync(CancellationToken.None);
            var web = inv.First(o => o.Kind == "container"); var img = inv.First(o => o.Kind == "image"); var vol = inv.First(o => o.Kind == "volume");
            var bridge = inv.First(o => o.Kind == "network" && o.Name == "bridge"); var app = inv.First(o => o.Kind == "network" && o.Name == "app-net");
            if (Infra.Infra.Offer(p, bridge, caps).First().Enabled || !Infra.Infra.Offer(p, app, caps).First().Enabled || Infra.Infra.Offer(p, web, caps).First(o => o.Action.Id == "unpause").Enabled)
                throw new Exception("offers");
            var pa = await p.RunAsync(DockerProvider.Pause, web, CancellationToken.None);
            var up = await p.RunAsync(DockerProvider.Unpause, web with { Status = "paused" }, CancellationToken.None);
            if (!pa.Verified || !up.Verified) throw new Exception("pause/unpause: " + pa.Message + " | " + up.Message);
            var top = await p.RunAsync(DockerProvider.Top, web, CancellationToken.None);
            var st = await p.RunAsync(DockerProvider.Stats, web, CancellationToken.None);
            var ins = await p.RunAsync(DockerProvider.Inspect, web, CancellationToken.None);
            if (top.Output?.Contains("nginx: master") != true || !Regex.IsMatch(st.Output ?? "", @"CPU\s+50\.00 %") || !Regex.IsMatch(st.Output ?? "", @"Memory\s+95\.4 MB") || ins.Output?.Contains("\"Status\"") != true) throw new Exception("reads: " + top.Output + " | " + st.Output);
            var k = await p.RunAsync(DockerProvider.Kill, web, CancellationToken.None);
            if (!k.Verified || eng.State != "exited") throw new Exception("kill: " + k.Message);
            var ri = await p.RunAsync(DockerProvider.RemoveImage, img, CancellationToken.None);
            var rv = await p.RunAsync(DockerProvider.RemoveVolume, vol, CancellationToken.None);
            var rn = await p.RunAsync(DockerProvider.RemoveNetwork, app, CancellationToken.None);
            if (!ri.Verified || !rv.Verified || !rn.Verified || eng.Gone.Count != 3) throw new Exception("removals: " + ri.Message + " | " + rv.Message + " | " + rn.Message);
            eng.InUse = true;
            var busy = await p.RunAsync(DockerProvider.RemoveVolume, vol with { Id = "pgdata" }, CancellationToken.None);
            var evil = await p.RunAsync(DockerProvider.RemoveVolume, vol with { Id = "../../etc" }, CancellationToken.None);
            if (busy.Accepted || !busy.Message.Contains("in use") || evil.Accepted) throw new Exception("refusals: " + busy.Message + " | " + evil.Message);
            if (!DockerBackend.ValidObjectId("image", "sha256:" + new string('a', 64)) || DockerBackend.ValidObjectId("network", "abc") || DockerBackend.ValidObjectId("volume", "a b")) throw new Exception("id rules");
            ok("Docker coverage: pause / unpause / kill verified by the engine's state, processes / resource usage (docker CLI CPU formula) / inspect read, image, volume and network removal verified gone, the built-in bridge never offered, an in-use volume refused with the engine's reason, unsafe names never sent");
        }
        catch (Exception ex) { fail("Docker coverage", ex); }

        // 5. Unraid: the official schema's extra id-only mutations are discovered and offered (and only then).
        var key = new Credential { Name = "unraid key (coverage)", Kind = CredentialKind.Password, Username = "unraid-api", Secret = "unraid-cov-4242" };
        AppData.Current.Credentials.Add(key);
        try
        {
            var srv = new FakeUnraid();
            var cfg = new ProviderConfig { Kind = "unraid", Name = "tower-cov", Endpoint = "https://tower.lan", CredentialId = key.Id, AllowChanges = true, Environment = "LAB" };
            var u = new UnraidProvider(cfg, srv) { VerifyFor = TimeSpan.FromSeconds(2), PollEvery = TimeSpan.FromMilliseconds(20) };
            var caps = await u.DetectAsync(CancellationToken.None);
            var inv = await u.InventoryAsync(CancellationToken.None);
            var c = inv.First(o => o.Kind == "container"); var v = inv.First(o => o.Kind == "vm");
            var cIds = u.ActionsFor(c).Select(a => a.Id).ToList(); var vIds = u.ActionsFor(v).Select(a => a.Id).ToList();
            if (!cIds.SequenceEqual(new[] { "docker:start", "docker:stop", "docker:restart", "docker:pause", "docker:unpause" }) || !vIds.Contains("vm:reset") || !vIds.Contains("vm:pause") || vIds.Contains("vm:resume"))
                throw new Exception("discovered: " + string.Join(",", cIds) + " / " + string.Join(",", vIds));
            if (Infra.Infra.Offer(u, c, caps).First(o => o.Action.Id == "docker:unpause").Why != "not paused") throw new Exception("state gating");
            var pz = await u.RunAsync(UnraidProvider.CPause, c, CancellationToken.None);
            if (!pz.Verified || srv.Last?.Contains("docker { pause(id: $id)") != true) throw new Exception("docker pause: " + pz.Message + " · " + srv.Last);
            var rs = await u.RunAsync(UnraidProvider.VReset, v, CancellationToken.None);
            if (!rs.Verified || srv.Last?.Contains("vm { reset(id: $id) }") != true) throw new Exception("vm reset: " + rs.Message);
            ok("Unraid coverage: docker restart / pause / unpause and VM pause / reset discovered from the schema and offered, VM resume not offered when the schema lacks it, unpause gated on state, docker pause and VM reset verified by re-reading");
        }
        catch (Exception ex) { fail("Unraid coverage", ex); }
        finally { AppData.Current.Credentials.Remove(key); }

        // 6. Cloud catalogs: every Contabo operation is in its spec; Hetzner metrics; the Vultr additions as govultr calls them.
        try
        {
            var spec = ContaboSpec.Split(';').Select(Norm).ToHashSet();
            var used = new List<string>();
            void Check(HashSet<string> set, string provider, string m, string path, string what)
            {
                // A fixed value of a spec path parameter ({resourceType}: instance / instances) counts as that parameter.
                var p = Regex.Replace(path, "/assignments/(instance|image|object-storage)/", "/assignments/{resourceType}/");
                p = Regex.Replace(p, @"^(/vips/\{[^}]+\})/(instances|bare-metal)/", "$1/{resourceType}/");
                var k = Norm($"{m} {p.Replace("{parent.", "{").Replace("{form.", "{")}");
                used.Add(k);
                if (!set.Contains(k)) throw new Exception($"{what}: {k} is not in {provider}'s spec");
            }
            void Def(ResourceDef d, HashSet<string> set, string provider)
            {
                Check(set, provider, "GET", d.Path, d.Title + " list");
                if (d.Create != null) Check(set, provider, "POST", d.CreatePath ?? d.Path, d.Title + " create");
                if (d.Update != null) Check(set, provider, d.UpdateMethod, d.ItemTemplate, d.Title + " update");
                if (d.CanDelete && !d.ReadOnly) Check(set, provider, "DELETE", d.ItemTemplate, d.Title + " delete");
                foreach (var a in d.Actions.Where(a => a.Special == null && a.Path.Length > 0)) Check(set, provider, a.Method, a.Path, d.Title + " › " + a.Label);
                foreach (var (_, ch) in d.Children) Def(ch, set, provider);
            }
            foreach (var d in ContaboCatalog.All.Where(d => !d.Path.Contains('?'))) Def(d, spec, "Contabo");
            var contaboOps = used.Distinct().Count();
            foreach (var t in new[] { "Object Storage", "VIPs (additional IPs)", "Reverse DNS (PTR)", "Tags" }) if (ContaboCatalog.All.All(d => d.Title != t)) throw new Exception("Contabo screen missing: " + t);
            if (ContaboCatalog.ObjectStorage.CanDelete || !ContaboCatalog.ObjectStorage.Actions.Any(a => a.Label.StartsWith("Cancel") && a.TypeToConfirm != null)) throw new Exception("object storage: cancel, never delete");

            var vultr = VultrAdded.Split(';').Select(Norm).ToHashSet();
            var mine = new List<string>();
            foreach (var d in VultrCatalog.All)
                foreach (var a in d.Actions.Concat(d.Children.SelectMany(ch => ch.Def.Actions)).Where(a => a.Special == null && a.Path.Length > 0))
                    mine.Add(Norm($"{a.Method} {a.Path.Replace("{form.", "{")}"));
            void Screen(ResourceDef d)
            {
                mine.Add(Norm("GET " + d.Path));
                if (d.Create != null) mine.Add(Norm("POST " + (d.CreatePath ?? d.Path)));
                if (d.Update != null) mine.Add(Norm($"{d.UpdateMethod} {d.ItemTemplate}"));
                if (d.CanDelete && !d.ReadOnly) mine.Add(Norm("DELETE " + d.ItemTemplate));
                foreach (var (_, ch) in d.Children) Screen(ch);
            }
            foreach (var d in VultrCatalog.All) Screen(d);
            var vultrMissing = vultr.Where(x => !mine.Contains(x) && !x.StartsWith("GET /blocks/snapshots/{id}") && !x.StartsWith("GET /vfs/{id}")).ToList();
            if (vultrMissing.Count > 0) throw new Exception("Vultr additions not in the catalog: " + string.Join(", ", vultrMissing));

            var now = new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);
            var mp = HetznerProvider.MetricsPath("/servers/42/metrics", now);
            if (mp != "/servers/42/metrics?type=cpu&type=disk&type=network&start=2026-10-19T11%3A00%3A00Z&end=2026-10-19T12%3A00%3A00Z&step=60" || !HetznerProvider.MetricsPath("/load_balancers/7/metrics", now).Contains("type=requests_per_second"))
                throw new Exception("metrics query: " + mp);
            var sample = JsonDocument.Parse("{\"metrics\":{\"start\":\"2026-10-19T11:00:00Z\",\"end\":\"2026-10-19T12:00:00Z\",\"step\":60,\"time_series\":{\"cpu\":{\"values\":[[1760871600,\"10\"],[1760871660,\"30\"],[1760871720,\"bad\"]]},\"network.0.bandwidth.in\":{\"values\":[]}}}}").RootElement;
            var text = HetznerProvider.FormatMetrics(sample);
            if (!Regex.IsMatch(text, @"cpu\s+avg\s+20\s+peak\s+30\s+latest\s+30\s+\(2 points\)") || !text.Contains("no data") || HetznerProvider.FormatMetrics(JsonDocument.Parse("{}").RootElement) != "(no metrics returned)")
                throw new Exception("metrics text: " + text);
            ok($"cloud catalogs: {contaboOps} Contabo operations ({ContaboCatalog.All.Length} screens, new: object storage with cancel-not-delete, VIPs, PTR records, tags, add-on upgrades) all in Contabo's official spec; {vultr.Count} Vultr operations added as Vultr's official client calls them; Hetzner metrics query (repeated type, RFC 3339 window) and summary (bad points skipped)");
        }
        catch (Exception ex) { fail("cloud catalog coverage", ex); }
    }

    // ---- fakes ---------------------------------------------------------------------------------------------------------

    private static HttpResponseMessage R(int status, string json) => new((HttpStatusCode)status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>A Proxmox VE API: one node, VM 100 (running), CT 101, template 9000, storage "local", tasks that finish at once.</summary>
    private sealed class FakePve : HttpMessageHandler
    {
        public readonly List<string> Calls = new();
        public Dictionary<string, string> LastForm = new();
        public readonly List<string> Snaps = new();
        public string Status = "running", Qmp = "running";
        public int Posts;
        private string _parent = "";
        private bool _template, _cloned, _backup;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath.Replace("/api2/json/", "");
            var query = req.RequestUri.Query;
            lock (Calls) Calls.Add(req.RequestUri.ToString());
            if (req.Method != HttpMethod.Get)
            {
                Posts++;
                var body = req.Content != null ? await req.Content.ReadAsStringAsync(ct) : "";
                LastForm = body.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(kv => kv.Split('=', 2)).ToDictionary(x => Uri.UnescapeDataString(x[0]), x => Uri.UnescapeDataString(x.Length > 1 ? x[1].Replace('+', ' ') : ""));
            }
            string D(string data) => "{\"data\":" + data + "}";
            const string upid = "\"UPID:pve1:00001:00002:00003:task:100:dcx@pve!cov:\"";
            switch (req.Method.Method, path)
            {
                case ("GET", "version"): return R(200, D("{\"version\":\"8.3.2\",\"release\":\"8.3\"}"));
                case ("GET", "access/permissions"): return R(200, D("{\"/\":{\"Sys.Audit\":1,\"VM.Audit\":1},\"/vms\":{\"VM.Audit\":1,\"VM.PowerMgmt\":1,\"VM.Snapshot\":1,\"VM.Clone\":1,\"VM.Backup\":1,\"VM.Allocate\":1}}"));
                case ("GET", "cluster/nextid"): return R(200, D("\"105\""));
                case ("GET", "cluster/resources"):
                    return R(200, D("[{\"id\":\"node/pve1\",\"type\":\"node\",\"node\":\"pve1\",\"status\":\"online\",\"maxcpu\":16}," +
                        $"{{\"id\":\"qemu/100\",\"type\":\"qemu\",\"node\":\"pve1\",\"vmid\":100,\"name\":\"web-01\",\"status\":\"{Status}\",\"maxcpu\":2}}," +
                        "{\"id\":\"lxc/101\",\"type\":\"lxc\",\"node\":\"pve1\",\"vmid\":101,\"name\":\"dns\",\"status\":\"running\"}," +
                        "{\"id\":\"qemu/9000\",\"type\":\"qemu\",\"node\":\"pve1\",\"vmid\":9000,\"name\":\"tmpl\",\"status\":\"stopped\",\"template\":1}," +
                        "{\"id\":\"storage/pve1/local\",\"type\":\"storage\",\"node\":\"pve1\",\"storage\":\"local\",\"status\":\"available\",\"plugintype\":\"dir\"}]"));
                case ("GET", "nodes/pve1/qemu/100/snapshot"):
                    return R(200, D("[" + string.Concat(Snaps.Select(s => $"{{\"name\":\"{s}\",\"snaptime\":1760870000,\"vmstate\":1,\"description\":\"pre\"}},")) + $"{{\"name\":\"current\",\"parent\":\"{_parent}\",\"running\":1}}]"));
                case ("POST", "nodes/pve1/qemu/100/snapshot"): Snaps.Add(LastForm["snapname"]); _parent = LastForm["snapname"]; return R(200, D(upid));
                case ("POST", "nodes/pve1/qemu/100/status/suspend"): Qmp = "paused"; return R(200, D(upid));
                case ("POST", "nodes/pve1/qemu/100/status/resume"): Qmp = "running"; return R(200, D(upid));
                case ("GET", "nodes/pve1/qemu/100/status/current"): return R(200, D($"{{\"status\":\"{Status}\",\"qmpstatus\":\"{Qmp}\",\"vmid\":100}}"));
                case ("POST", "nodes/pve1/qemu/100/clone"): _cloned = LastForm.GetValueOrDefault("newid") == "105"; return R(200, D(upid));
                case ("GET", "nodes/pve1/qemu/105/status/current"): return _cloned ? R(200, D("{\"status\":\"stopped\",\"vmid\":105}")) : R(500, "{\"data\":null}");
                case ("POST", "nodes/pve1/vzdump"): _backup = true; return R(200, D(upid));
                case ("GET", "nodes/pve1/storage/local/content"):
                    var ctime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    return R(200, D(_backup ? $"[{{\"volid\":\"local:backup/vzdump-qemu-100-2026_10_19-12_00_00.vma.zst\",\"vmid\":100,\"ctime\":{ctime},\"content\":\"backup\",\"size\":1073741824}}]" : "[]"));
                case ("POST", "nodes/pve1/qemu/100/template"): _template = Status == "stopped"; return R(200, D("null"));
                case ("GET", "nodes/pve1/qemu/100/config"): return R(200, D(_template ? "{\"template\":1}" : "{}"));
                case ("GET", "nodes/pve1/tasks"): return R(200, D("[{\"starttime\":1760870000,\"type\":\"vzdump\",\"id\":\"100\",\"user\":\"dcx@pve!cov\",\"status\":\"OK\"}]"));
            }
            if (req.Method == HttpMethod.Get && path.StartsWith("nodes/pve1/tasks/", StringComparison.Ordinal)) return R(200, D("{\"status\":\"stopped\",\"exitstatus\":\"OK\"}"));
            var rb = Regex.Match(path, "^nodes/pve1/qemu/100/snapshot/([A-Za-z0-9_-]+)(/rollback)?$");
            if (rb.Success && req.Method == HttpMethod.Post && rb.Groups[2].Success) { _parent = rb.Groups[1].Value; return R(200, D(upid)); }
            if (rb.Success && req.Method == HttpMethod.Delete) { Snaps.Remove(rb.Groups[1].Value); if (_parent == rb.Groups[1].Value) _parent = ""; return R(200, D(upid)); }
            return R(404, "{\"data\":null,\"errors\":\"no such path\"}");
        }
    }

    /// <summary>A vSphere Automation API: two VMs, one host, hardware and guest endpoints, delete, host connect / disconnect.</summary>
    private sealed class FakeVc(string password) : HttpMessageHandler
    {
        public readonly Dictionary<string, (string Name, string Power, long Cpu, long Mem)> Vms = new() { ["vm-101"] = ("web-vm", "POWERED_ON", 2, 4096), ["vm-102"] = ("db-vm", "POWERED_OFF", 2, 4096) };
        public string HostState = "CONNECTED";
        public JsonNode? LastPatch;
        public bool RefuseHotAdd;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath; var query = req.RequestUri.Query;
            var body = req.Content != null ? await req.Content.ReadAsStringAsync(ct) : null;
            if (path == "/api/session" && req.Method == HttpMethod.Post)
                return Encoding.UTF8.GetString(Convert.FromBase64String(req.Headers.Authorization?.Parameter ?? "")) == "svc-dcx@vsphere.local:" + password ? R(201, "\"sess-1\"") : R(401, "{}");
            if (!req.Headers.TryGetValues("vmware-api-session-id", out var sid) || sid.First() != "sess-1") return R(401, "{}");
            switch (path)
            {
                case "/api/appliance/system/version": return R(200, "{\"version\":\"8.0.3.00500\",\"build\":\"24500000\"}");
                case "/api/vcenter/vm": return R(200, new JsonArray(Vms.Select(kv => (JsonNode)new JsonObject { ["vm"] = kv.Key, ["name"] = kv.Value.Name, ["power_state"] = kv.Value.Power, ["cpu_count"] = kv.Value.Cpu, ["memory_size_MiB"] = kv.Value.Mem }).ToArray()).ToJsonString());
                case "/api/vcenter/host" when req.Method == HttpMethod.Get: return R(200, $"[{{\"host\":\"host-10\",\"name\":\"esx01.lab.example\",\"connection_state\":\"{HostState}\",\"power_state\":\"POWERED_ON\"}}]");
                case "/api/vcenter/host/host-10" when req.Method == HttpMethod.Post:
                    HostState = query.Contains("action=disconnect") ? "DISCONNECTED" : "CONNECTED"; return R(204, "");
                case "/api/vcenter/cluster" or "/api/vcenter/datastore" or "/api/vcenter/network": return R(200, "[]");
            }
            var m = Regex.Match(path, @"^/api/vcenter/vm/(vm-\d+)(/.*)?$");
            if (!m.Success || !Vms.TryGetValue(m.Groups[1].Value, out var vm)) return R(404, "{\"error_type\":\"NOT_FOUND\",\"messages\":[{\"default_message\":\"not found\"}]}");
            var id = m.Groups[1].Value;
            switch (m.Groups[2].Value, req.Method.Method)
            {
                case ("", "GET"): return R(200, new JsonObject { ["name"] = vm.Name, ["power_state"] = vm.Power }.ToJsonString());
                case ("", "DELETE"):
                    if (vm.Power != "POWERED_OFF") return R(400, "{\"error_type\":\"NOT_ALLOWED_IN_CURRENT_STATE\",\"messages\":[{\"default_message\":\"powered on\"}]}");
                    Vms.Remove(id); return R(204, "");
                case ("/hardware/cpu", "GET"): return R(200, $"{{\"count\":{vm.Cpu},\"cores_per_socket\":1,\"hot_add_enabled\":false}}");
                case ("/hardware/memory", "GET"): return R(200, $"{{\"size_MiB\":{vm.Mem},\"hot_add_enabled\":false}}");
                case ("/hardware/cpu" or "/hardware/memory", "PATCH"):
                    LastPatch = JsonNode.Parse(body ?? "{}");
                    if (RefuseHotAdd) return R(400, "{\"error_type\":\"NOT_ALLOWED_IN_CURRENT_STATE\",\"messages\":[{\"default_message\":\"cannot change while powered on: hot_add_enabled is false\"}]}");
                    Vms[id] = m.Groups[2].Value.EndsWith("cpu") ? vm with { Cpu = LastPatch!["count"]!.GetValue<long>() } : vm with { Mem = LastPatch!["size_MiB"]!.GetValue<long>() };
                    return R(204, "");
                case ("/guest/identity", "GET"): return R(200, "{\"name\":\"UBUNTU_64\",\"family\":\"LINUX\",\"full_name\":{\"default_message\":\"Ubuntu Linux (64-bit)\"},\"host_name\":\"web\",\"ip_address\":\"192.0.2.20\"}");
                case ("/guest/networking/interfaces", "GET"): return R(200, "[{\"mac_address\":\"00:50:56:00:00:01\",\"nic\":\"4000\",\"ip\":{\"ip_addresses\":[{\"ip_address\":\"192.0.2.20\",\"prefix_length\":24,\"state\":\"PREFERRED\"}]}}]");
                case ("/tools", "GET"): return R(200, "{\"run_state\":\"RUNNING\",\"version_status\":\"CURRENT\",\"version\":\"12389\",\"auto_update_supported\":true}");
            }
            return R(404, "{\"error_type\":\"NOT_FOUND\",\"messages\":[]}");
        }
    }

    /// <summary>A Docker Engine API: one container, one image, two networks, one volume.</summary>
    private sealed class FakeEngine : HttpMessageHandler
    {
        private static readonly string Cid = new('c', 64), Img = "sha256:" + new string('d', 64), Net = new('e', 64);
        public string State = "running";
        public readonly HashSet<string> Gone = new();
        public bool InUse;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = Uri.UnescapeDataString(req.RequestUri!.AbsolutePath.TrimStart('/'));
            HttpResponseMessage Res() => (req.Method.Method, path) switch
            {
                ("GET", "version") => R(200, "{\"Version\":\"27.3.1\",\"ApiVersion\":\"1.47\",\"Os\":\"linux\",\"Arch\":\"amd64\"}"),
                ("GET", "containers/json") => R(200, $"[{{\"Id\":\"{Cid}\",\"Names\":[\"/web\"],\"Image\":\"nginx\",\"State\":\"{State}\",\"Status\":\"Up\",\"Ports\":[]}}]"),
                ("GET", "images/json") => R(200, Gone.Contains("image") ? "[]" : $"[{{\"Id\":\"{Img}\",\"RepoTags\":[\"nginx:latest\"],\"Size\":187000000}}]"),
                ("GET", "networks") => R(200, $"[{{\"Id\":\"{new string('b', 64)}\",\"Name\":\"bridge\",\"Driver\":\"bridge\",\"Scope\":\"local\"}},{{\"Id\":\"{Net}\",\"Name\":\"app-net\",\"Driver\":\"bridge\",\"Scope\":\"local\"}}]"),
                ("GET", "volumes") => R(200, "{\"Volumes\":[{\"Name\":\"pgdata\",\"Driver\":\"local\"}]}"),
                ("POST", var x) when x == $"containers/{Cid}/pause" => Do(() => State = "paused"),
                ("POST", var x) when x == $"containers/{Cid}/unpause" => Do(() => State = "running"),
                ("POST", var x) when x == $"containers/{Cid}/kill" => Do(() => State = "exited"),
                ("GET", var x) when x == $"containers/{Cid}/json" => R(200, $"{{\"Id\":\"{Cid}\",\"State\":{{\"Status\":\"{State}\"}}}}"),
                ("GET", var x) when x == $"containers/{Cid}/top" => R(200, "{\"Titles\":[\"UID\",\"PID\",\"CMD\"],\"Processes\":[[\"root\",\"4242\",\"nginx: master process\"]]}"),
                ("GET", var x) when x == $"containers/{Cid}/stats" => R(200, "{\"cpu_stats\":{\"cpu_usage\":{\"total_usage\":3000},\"system_cpu_usage\":20000,\"online_cpus\":4},\"precpu_stats\":{\"cpu_usage\":{\"total_usage\":1000},\"system_cpu_usage\":4000},\"memory_stats\":{\"usage\":104857600,\"limit\":1073741824,\"stats\":{\"inactive_file\":4857600}},\"networks\":{\"eth0\":{\"rx_bytes\":2048,\"tx_bytes\":1024}},\"pids_stats\":{\"current\":3}}"),
                ("DELETE", var x) when x == $"images/{Img}" => Do(() => Gone.Add("image")),
                ("DELETE", var x) when x == $"networks/{Net}" => Do(() => Gone.Add("network")),
                ("DELETE", "volumes/pgdata") => InUse ? R(409, "{\"message\":\"remove pgdata: volume is in use - [cccc]\"}") : Do(() => Gone.Add("volume")),
                ("GET", var x) when x == $"images/{Img}/json" => Gone.Contains("image") ? R(404, "{\"message\":\"No such image\"}") : R(200, "{}"),
                ("GET", var x) when x == $"networks/{Net}" => Gone.Contains("network") ? R(404, "{\"message\":\"not found\"}") : R(200, "{}"),
                ("GET", "volumes/pgdata") => Gone.Contains("volume") && !InUse ? R(404, "{\"message\":\"no such volume\"}") : R(200, "{}"),
                _ => R(404, "{\"message\":\"page not found\"}"),
            };
            HttpResponseMessage Do(Action a) { a(); return R(204, ""); }
            return Task.FromResult(Res());
        }
    }

    /// <summary>An Unraid GraphQL API with introspection: docker has every op, the VM API lacks resume.</summary>
    private sealed class FakeUnraid : HttpMessageHandler
    {
        public string? Last;
        private string _c = "RUNNING", _v = "RUNNING";

        private static JsonObject T(string kind, string? name, bool list = false, bool nonNull = false)
        {
            var t = new JsonObject { ["kind"] = kind, ["name"] = name };
            if (list) t = new JsonObject { ["kind"] = "LIST", ["name"] = null, ["ofType"] = t };
            if (nonNull) t = new JsonObject { ["kind"] = "NON_NULL", ["name"] = null, ["ofType"] = t };
            return t;
        }
        private static JsonObject F(string name, JsonObject type, bool idArg = false) => new()
        {
            ["name"] = name, ["type"] = type,
            ["args"] = idArg ? new JsonArray(new JsonObject { ["name"] = "id", ["type"] = T("SCALAR", "PrefixedID", nonNull: true) }) : new JsonArray(),
        };
        private static JsonObject Ty(string name, params JsonObject[] fields) => new() { ["name"] = name, ["kind"] = "OBJECT", ["fields"] = new JsonArray(fields) };

        private static string Schema()
        {
            var s = T("SCALAR", "String");
            var types = new JsonArray
            {
                Ty("Query", F("docker", T("OBJECT", "Docker")), F("vms", T("OBJECT", "Vms"))),
                Ty("Docker", F("containers", T("OBJECT", "DockerContainer", list: true))),
                Ty("DockerContainer", F("id", T("SCALAR", "ID")), F("names", T("SCALAR", "String", list: true)), F("image", s), F("state", T("ENUM", "ContainerState")), F("status", T("SCALAR", "String"))),
                Ty("Vms", F("domains", T("OBJECT", "VmDomain", list: true))), Ty("VmDomain", F("id", T("SCALAR", "ID")), F("name", T("SCALAR", "String")), F("state", T("ENUM", "VmState"))),
                Ty("Mutation", F("docker", T("OBJECT", "DockerMutations")), F("vm", T("OBJECT", "VmMutations"))),
                Ty("DockerMutations", UnraidProvider.DockerOps.Select(o => F(o, T("OBJECT", "DockerContainer"), true)).ToArray()),
                Ty("VmMutations", UnraidProvider.VmOps.Where(o => o != "resume").Select(o => F(o, T("SCALAR", "Boolean"), true)).ToArray()),
            };
            return new JsonObject { ["data"] = new JsonObject { ["__schema"] = new JsonObject { ["queryType"] = new JsonObject { ["name"] = "Query" }, ["mutationType"] = new JsonObject { ["name"] = "Mutation" }, ["types"] = types } } }.ToJsonString();
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var body = JsonNode.Parse(await req.Content!.ReadAsStringAsync(ct))!;
            var q = body["query"]!.ToString();
            if (q.Contains("__schema")) return R(200, Schema());
            if (q.StartsWith("mutation", StringComparison.Ordinal))
            {
                Last = q;
                if (q.Contains("docker { pause")) _c = "PAUSED";
                if (q.Contains("vm { reset")) _v = "RUNNING";
                return R(200, "{\"data\":{}}");
            }
            return R(200, "{\"data\":{\"docker\":{\"containers\":[{\"id\":\"c1:abcd\",\"names\":[\"/plex\"],\"image\":\"plexinc/pms-docker\",\"state\":\"" + _c + "\",\"status\":\"Up\"}]},\"vms\":{\"domains\":[{\"id\":\"vm:1\",\"name\":\"win11\",\"state\":\"" + _v + "\"}]}}}");
        }
    }
}
