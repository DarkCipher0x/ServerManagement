using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DcxManager.Services;
using DcxManager.Ssh;
using DcxManager.Views;
using DcxManager.Vultr;
using DcxManager.Documents;

namespace DcxManager;

// SelfTest: infrastructure providers against fakes (Docker, Proxmox, Unraid, ...) (moved unchanged out of RunFullAsync in 0.2.0 phase 6½; the run's shared state is SelfTestRun).
public static partial class SelfTest
{
    private static async Task InfrastructureAsync(SelfTestRun sharedRun)
    {
        var main = sharedRun.Main; var reportPath = sharedRun.ReportPath; Action<string> Ok = sharedRun.Ok; Action<string, Exception?> Fail = sharedRun.Fail; SelfTestRun.SettleFn Settle = sharedRun.Settle;

        // Infrastructure providers (ADR-007), offline: Docker Engine API over a REAL named pipe served by a fake engine;
        // docker CLI JSON parsing; Proxmox through a fake HTTPS handler (token header, permissions, tasks, verification,
        // TLS pin decisions); read-only gating, confirmations, encrypted persistence, privacy listing, render.
        try
        {
            // --- Docker CLI parsing (SSH backend) + id validation + log demux ---
            const string cid = "3f2a9c1b7d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8";
            var cli = "{\"ID\":\"" + cid + "\",\"Names\":\"web\",\"Image\":\"nginx:1.27\",\"State\":\"running\",\"Status\":\"Up 2 hours\",\"Ports\":\"0.0.0.0:80->80/tcp\"}\n" +
                      "{\"ID\":\"abc; rm -rf /\",\"Names\":\"evil\",\"Image\":\"x\",\"State\":\"running\",\"Status\":\"\",\"Ports\":\"\"}\n---DCX-SECTION---\n" +
                      "{\"ID\":\"sha256:1111\",\"Repository\":\"nginx\",\"Tag\":\"1.27\",\"Size\":\"190MB\",\"CreatedSince\":\"3 weeks ago\"}\n---DCX-SECTION---\n" +
                      "{\"ID\":\"n1\",\"Name\":\"bridge\",\"Driver\":\"bridge\",\"Scope\":\"local\"}\n---DCX-SECTION---\n{\"Name\":\"pgdata\",\"Driver\":\"local\"}\n";
            var parsed = Infra.SshDocker.ParseCli(cli);
            if (parsed.Count(o => o.Kind == "container") != 1 || parsed.Any(o => o.Name == "evil") || parsed.Count != 4 || parsed.First().Status != "running") throw new Exception("cli parse: " + parsed.Count);
            if (Infra.DockerBackend.ValidId("abc; rm -rf /") || Infra.DockerBackend.ValidId("ABCDEF123456") || !Infra.DockerBackend.ValidId(cid)) throw new Exception("id validation");
            var frame = new byte[] { 1, 0, 0, 0, 0, 0, 0, 6 }.Concat(Encoding.UTF8.GetBytes("hello\n")).Concat(new byte[] { 2, 0, 0, 0, 0, 0, 0, 4 }).Concat(Encoding.UTF8.GetBytes("err\n")).ToArray();
            if (Infra.ApiDocker.Demux(frame) != "hello\nerr\n" || Infra.ApiDocker.Demux(Encoding.UTF8.GetBytes("plain tty\n")) != "plain tty\n") throw new Exception("demux");

            // --- Docker Engine API over a real named pipe (fake engine) ---
            var pipe = "dcx-selftest-docker-" + Guid.NewGuid().ToString("N")[..8];
            string state = "running"; bool removed = false, sticky = false;
            using var engineCts = new CancellationTokenSource();
            async Task Serve(System.IO.Pipes.NamedPipeServerStream s)
            {
                using (s)
                {
                    var buf = new byte[8192];
                    while (s.IsConnected && !engineCts.IsCancellationRequested)
                    {
                        var head = new StringBuilder();
                        while (!head.ToString().Contains("\r\n\r\n")) { int n = await s.ReadAsync(buf.AsMemory(0, 1), engineCts.Token); if (n == 0) return; head.Append((char)buf[0]); }
                        var lines = head.ToString().Split("\r\n");
                        var parts = lines[0].Split(' ');
                        var (method, path) = (parts[0], parts[1].TrimStart('/'));
                        var len = lines.FirstOrDefault(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) is { } cl ? int.Parse(cl.Split(':')[1].Trim()) : 0;
                        for (int got = 0; got < len;) got += await s.ReadAsync(buf.AsMemory(0, Math.Min(buf.Length, len - got)), engineCts.Token);
                        (int code, string body) = (method, path) switch
                        {
                            ("GET", "version") => (200, "{\"Version\":\"27.3.1\",\"ApiVersion\":\"1.47\",\"Os\":\"linux\",\"Arch\":\"amd64\"}"),
                            ("GET", "containers/json?all=1") => (200, removed ? "[]" : "[{\"Id\":\"" + cid + "\",\"Names\":[\"/web\"],\"Image\":\"nginx:1.27\",\"State\":\"" + state + "\",\"Status\":\"x\",\"Ports\":[{\"PrivatePort\":80,\"PublicPort\":8080,\"Type\":\"tcp\"}]}]"),
                            ("GET", "images/json") => (200, "[{\"Id\":\"sha256:1111\",\"RepoTags\":[\"nginx:1.27\"],\"Size\":190000000}]"),
                            ("GET", "networks") => (200, "[{\"Id\":\"n1\",\"Name\":\"bridge\",\"Driver\":\"bridge\",\"Scope\":\"local\"}]"),
                            ("GET", "volumes") => (200, "{\"Volumes\":[{\"Name\":\"pgdata\",\"Driver\":\"local\"}]}"),
                            ("POST", var p) when p == $"containers/{cid}/stop" => (204, Do(() => { if (!sticky) state = "exited"; })),
                            ("DELETE", var p) when p == $"containers/{cid}" => state == "running" ? (409, "{\"message\":\"cannot remove a running container\"}") : (204, Do(() => removed = true)),
                            ("GET", var p) when p == $"containers/{cid}/json" => removed ? (404, "{\"message\":\"No such container\"}") : (200, "{\"State\":{\"Status\":\"" + state + "\"}}"),
                            _ => (404, "{\"message\":\"not found\"}"),
                        };
                        var bytes = Encoding.UTF8.GetBytes(body);
                        var resp = Encoding.ASCII.GetBytes($"HTTP/1.1 {code} X\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: keep-alive\r\n\r\n");
                        await s.WriteAsync(resp, engineCts.Token); await s.WriteAsync(bytes, engineCts.Token); await s.FlushAsync(engineCts.Token);
                    }
                }
                static string Do(Action a) { a(); return ""; }
            }
            _ = Task.Run(async () =>
            {
                while (!engineCts.IsCancellationRequested)
                {
                    var server = new System.IO.Pipes.NamedPipeServerStream(pipe, System.IO.Pipes.PipeDirection.InOut, 8, System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
                    try { await server.WaitForConnectionAsync(engineCts.Token); } catch { server.Dispose(); return; }
                    _ = Task.Run(async () => { try { await Serve(server); } catch { } });
                }
            });
            var dcfg = new Infra.ProviderConfig { Kind = "docker-local", Name = "selftest docker", Endpoint = pipe, Environment = "LAB" };
            var dock = new Infra.DockerProvider(dcfg, Infra.DockerBackend.LocalPipe(pipe)) { VerifyFor = TimeSpan.FromSeconds(3) };
            var dcaps = await dock.DetectAsync(CancellationToken.None);
            if (!dcaps.Reachable || !dcaps.CanRead || dcaps.Version != "27.3.1") throw new Exception("docker detect: " + dcaps.Detail);
            var inv = await dock.InventoryAsync(CancellationToken.None);
            var web = inv.FirstOrDefault(o => o.Kind == "container") ?? throw new Exception("no container");
            if (inv.Count != 4 || web.Name != "web" || !web.Detail.Contains("8080→80/tcp")) throw new Exception("docker inventory");
            var offer = Infra.Infra.Offer(dock, web, dcaps);
            if (offer.Any(o => o.Action.Mutating && o.Enabled) || offer.First(o => o.Action.Id == "stop").Why?.Contains("read-only") != true || !offer.First(o => o.Action.Id == "logs").Enabled) throw new Exception("read-only gating");
            dcfg.AllowChanges = true;
            offer = Infra.Infra.Offer(dock, web, dcaps);
            if (!offer.First(o => o.Action.Id == "stop").Enabled || offer.First(o => o.Action.Id == "remove").Why?.Contains("stop it first") != true || offer.First(o => o.Action.Id == "start").Enabled) throw new Exception("state gating");
            var ct2 = Infra.Infra.ConfirmText(dock, Infra.DockerProvider.Remove, web);
            if (!ct2.Contains("Target:") || !ct2.Contains("Environment:  LAB") || !ct2.Contains("DATA LOSS")) throw new Exception("confirm text");
            var rStop = await dock.RunAsync(Infra.DockerProvider.Stop, web, CancellationToken.None);
            if (!rStop.Verified || state != "exited") throw new Exception("stop: " + rStop.Message);
            var stopped = (await dock.InventoryAsync(CancellationToken.None)).First(o => o.Kind == "container");
            var rRm = await dock.RunAsync(Infra.DockerProvider.Remove, stopped, CancellationToken.None);
            if (!rRm.Verified || !removed) throw new Exception("remove: " + rRm.Message);
            // A change the engine accepts but that doesn't take effect is reported as NOT verified.
            removed = false; state = "running"; sticky = true; dock.VerifyFor = TimeSpan.FromSeconds(1.2);
            var rSticky = await dock.RunAsync(Infra.DockerProvider.Stop, web, CancellationToken.None);
            if (rSticky.Verified || !rSticky.Accepted || !rSticky.Message.Contains("NOT verified")) throw new Exception("unverified path: " + rSticky.Message);
            engineCts.Cancel();
            var gone = await new Infra.DockerProvider(dcfg, Infra.DockerBackend.LocalPipe("dcx-no-such-pipe-" + Guid.NewGuid().ToString("N")[..6])).DetectAsync(CancellationToken.None);
            if (gone.Reachable || !gone.Detail.Contains("isn't reachable")) throw new Exception("missing engine: " + gone.Detail);

            // --- Proxmox via a fake HTTPS API ---
            var token = new Credential { Name = "pve token (selftest)", Kind = CredentialKind.Password, Username = "dcx@pve!infra", Secret = "s3cr3t-token-77" };
            AppData.Current.Credentials.Add(token);
            var pcfg = new Infra.ProviderConfig { Kind = "proxmox", Name = "pve-lab", Endpoint = "https://pve.test:8006", CredentialId = token.Id, Environment = "PRODUCTION", AllowChanges = true };
            string? auth = null; int taskPolls = 0; string vm100 = "running"; bool failTask = false;
            var fake = new FakeHttp(req =>
            {
                auth = req.Headers.TryGetValues("Authorization", out var v) ? v.First() : null;
                var path = req.RequestUri!.AbsolutePath.Replace("/api2/json/", "");
                string data = (req.Method.Method, path) switch
                {
                    ("GET", "version") => "{\"version\":\"8.2.7\",\"release\":\"8.2\"}",
                    ("GET", "access/permissions") => "{\"/\":{\"Sys.Audit\":1,\"VM.Audit\":1},\"/vms\":{\"VM.Audit\":1,\"VM.PowerMgmt\":1},\"/vms/101\":{\"VM.Audit\":1}}",
                    ("GET", "cluster/resources") => "[{\"id\":\"node/pve1\",\"type\":\"node\",\"node\":\"pve1\",\"status\":\"online\",\"cpu\":0.12,\"maxcpu\":16,\"mem\":8589934592,\"maxmem\":68719476736}," +
                                                   "{\"id\":\"qemu/100\",\"type\":\"qemu\",\"node\":\"pve1\",\"vmid\":100,\"name\":\"web-01\",\"status\":\"" + vm100 + "\",\"maxcpu\":2,\"maxmem\":4294967296,\"uptime\":3600}," +
                                                   "{\"id\":\"lxc/101\",\"type\":\"lxc\",\"node\":\"pve1\",\"vmid\":101,\"name\":\"dns\",\"status\":\"running\",\"maxcpu\":1,\"maxmem\":536870912}," +
                                                   "{\"id\":\"qemu/9000\",\"type\":\"qemu\",\"node\":\"pve1\",\"vmid\":9000,\"name\":\"tmpl\",\"status\":\"stopped\",\"template\":1}," +
                                                   "{\"id\":\"storage/pve1/local\",\"type\":\"storage\",\"node\":\"pve1\",\"storage\":\"local\",\"status\":\"available\",\"plugintype\":\"dir\",\"disk\":10737418240,\"maxdisk\":107374182400}]",
                    ("POST", "nodes/pve1/qemu/100/status/shutdown") => "\"UPID:pve1:0001:0002:0003:qmshutdown:100:dcx@pve!infra:\"",
                    ("GET", var t) when t.StartsWith("nodes/pve1/tasks/") => ++taskPolls < 2 ? "{\"status\":\"running\"}" : Do2(() => { if (!failTask) vm100 = "stopped"; }, failTask ? "{\"status\":\"stopped\",\"exitstatus\":\"command 'qm shutdown 100' failed: got timeout\"}" : "{\"status\":\"stopped\",\"exitstatus\":\"OK\"}"),
                    ("GET", "nodes/pve1/qemu/100/status/current") => "{\"status\":\"" + vm100 + "\"}",
                    _ => "null",
                };
                return "{\"data\":" + data + "}";
                static string Do2(Action a, string r) { a(); return r; }
            });
            var pve = new Infra.ProxmoxProvider(pcfg, fake) { VerifyFor = TimeSpan.FromSeconds(5), PollEvery = TimeSpan.FromMilliseconds(50) };
            var pcaps = await pve.DetectAsync(CancellationToken.None);
            if (!pcaps.Reachable || !pcaps.CanRead || !pcaps.CanControl || pcaps.Version != "8.2.7") throw new Exception("pve detect: " + pcaps.Detail);
            if (auth != "PVEAPIToken=dcx@pve!infra=s3cr3t-token-77") throw new Exception("token header");
            var pinv = await pve.InventoryAsync(CancellationToken.None);
            var v100 = pinv.First(o => o.Name == "web-01"); var c101 = pinv.First(o => o.Name == "dns"); var tmpl = pinv.First(o => o.Name == "tmpl");
            if (pinv.Count != 5 || v100.Kind != "vm" || c101.Kind != "lxc" || tmpl.Status != "template" || pinv.All(o => o.Kind != "storage")) throw new Exception("pve inventory");
            if (Infra.Infra.Offer(pve, c101, pcaps).First(o => o.Action.Id == "shutdown").Why?.Contains("no VM.PowerMgmt on /vms/101") != true) throw new Exception("most specific path permission");
            if (Infra.Infra.Offer(pve, tmpl, pcaps).Any(o => o.Enabled)) throw new Exception("template");
            if (!Infra.Infra.Offer(pve, v100, pcaps).First(o => o.Action.Id == "shutdown").Enabled) throw new Exception("shutdown should be offered");
            var rShut = await pve.RunAsync(Infra.ProxmoxProvider.Shutdown, v100, CancellationToken.None);
            if (!rShut.Verified || vm100 != "stopped") throw new Exception("pve shutdown: " + rShut.Message);
            vm100 = "running"; taskPolls = 0; failTask = true;
            var rFail = await pve.RunAsync(Infra.ProxmoxProvider.Shutdown, v100, CancellationToken.None);
            if (rFail.Verified || !rFail.Message.Contains("task failed")) throw new Exception("failed task: " + rFail.Message);
            if (Infra.ProxmoxProvider.TlsCheck("", "AA", System.Net.Security.SslPolicyErrors.None) != Infra.TlsDecision.Accept
                || Infra.ProxmoxProvider.TlsCheck("", "AA", System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors) != Infra.TlsDecision.NeedsTrust
                || Infra.ProxmoxProvider.TlsCheck("AA", "aa", System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors) != Infra.TlsDecision.Accept
                || Infra.ProxmoxProvider.TlsCheck("AA", "BB", System.Net.Security.SslPolicyErrors.None) != Infra.TlsDecision.Mismatch) throw new Exception("tls pinning decisions");
            if (Infra.ProxmoxProvider.ValidateEndpoint("http://pve.lan:8006") == null || Infra.ProxmoxProvider.ValidateEndpoint("https://pve.lan:8006") != null) throw new Exception("https only");

            // --- persistence, privacy, screen ---
            AppData.Current.InfraProviders.AddRange(new[] { pcfg, dcfg });
            AppData.Save();
            var raw = Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(SecureStore.DataDir, "data.dcx")));
            if (raw.Contains("pve-lab") || raw.Contains("s3cr3t-token-77")) throw new Exception("plaintext in data.dcx");
            if (System.Text.Json.JsonSerializer.Serialize(pcfg, SecureStore.Json).Contains("s3cr3t")) throw new Exception("secret inside provider config");
            if (!PrivacyReport.Endpoints().Any(e => e.Service.Contains("pve-lab") && e.Class == NetClass.UserConfigured && e.State.Contains("changes allowed"))) throw new Exception("privacy listing");
            pcfg.AllowChanges = false; vm100 = "running"; failTask = false; taskPolls = 0;
            main.Navigate("infra");
            var iv = InfraView.Current ?? throw new Exception("infra view");
            // "＋ Add ▾" opens its menu on a LEFT click (0.1.4 opened it empty): recorded, never shown, items not invoked.
            {
                var addBtn = iv.AddButtonForTest ?? throw new Exception("no Add button");
                int shown; lock (Menus.ShownForTests) shown = Menus.ShownForTests.Count;
                addBtn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                string[]? addMenu; lock (Menus.ShownForTests) addMenu = Menus.ShownForTests.Count == shown + 1 ? Menus.ShownForTests[^1] : null;
                var heads = addMenu?.ToList() ?? new();
                if (heads.Count != 11 || !heads[0].StartsWith("Docker on a saved host") || !heads[2].StartsWith("Proxmox") || !heads[3].StartsWith("Unraid") || !heads[4].StartsWith("VMware vCenter") || !heads[5].StartsWith("Hyper-V") || !heads[6].StartsWith("Synology") || !heads[7].StartsWith("Red Hat Satellite")
                    || !heads[8].StartsWith("UniFi Network") || !heads[9].StartsWith("UniFi Site Manager") || !heads[10].StartsWith("DCX-CTL"))   // 0.6.16: UniFi
                    throw new Exception("Add ▾ menu on click: " + string.Join(" | ", heads));
            }
            iv.InjectForTest(pcfg, new Infra.ProxmoxProvider(pcfg, fake));
            await Settle(400);
            iv.SelectForTest(pcfg);
            await iv.RefreshAsync();
            await Settle(300);
            iv.SelectObjectForTest("web-01");
            await Settle(400);
            var shots = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(reportPath)!, "shots")).FullName;
            Snap(main, Path.Combine(shots, "infra.png"));
            AppData.Current.InfraProviders.Remove(dcfg); AppData.Save();
            Ok("infrastructure: ＋ Add ▾ opens its menu on a left click; docker CLI JSON (injection-shaped id dropped), log demux; Docker Engine API over a real named pipe (detect, inventory, read-only gating, state gating, verified stop + remove, accepted-but-NOT-verified reported, missing engine); Proxmox token header, permissions (most specific path), template guard, task + status re-read verified, failed task not verified, TLS pin decisions, https only; encrypted persistence without secrets; privacy listing; rendered infra.png");
        }
        catch (Exception ex) { Fail("infrastructure", ex); }

        // Unraid (ADR-007 provider #3), offline: capability discovery by GraphQL introspection against a fake server.
        try
        {
            System.Text.Json.Nodes.JsonObject T(string kind, string? name, bool list = false, bool nonNull = false)
            {
                var t = new System.Text.Json.Nodes.JsonObject { ["kind"] = kind, ["name"] = name };
                if (list) t = new System.Text.Json.Nodes.JsonObject { ["kind"] = "LIST", ["name"] = null, ["ofType"] = t };
                if (nonNull) t = new System.Text.Json.Nodes.JsonObject { ["kind"] = "NON_NULL", ["name"] = null, ["ofType"] = t };
                return t;
            }
            System.Text.Json.Nodes.JsonObject Fd(string name, System.Text.Json.Nodes.JsonObject type, bool idArg = false) => new()
            {
                ["name"] = name, ["type"] = type,
                ["args"] = idArg ? new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["name"] = "id", ["type"] = T("SCALAR", "PrefixedID", nonNull: true) }) : new System.Text.Json.Nodes.JsonArray(),
            };
            System.Text.Json.Nodes.JsonObject Ty(string name, params System.Text.Json.Nodes.JsonObject[] fields) => new() { ["name"] = name, ["kind"] = "OBJECT", ["fields"] = new System.Text.Json.Nodes.JsonArray(fields) };
            System.Text.Json.Nodes.JsonObject S2(string n) => T("SCALAR", n == "id" ? "ID" : "String");
            string SchemaJson(bool mutations, bool autoStart)
            {
                var types = new System.Text.Json.Nodes.JsonArray
                {
                    Ty("Query", Fd("info", T("OBJECT", "Info")), Fd("array", T("OBJECT", "UnraidArray")), Fd("docker", T("OBJECT", "Docker")), Fd("vms", T("OBJECT", "Vms"))),
                    Ty("Info", Fd("os", T("OBJECT", "InfoOs")), Fd("cpu", T("OBJECT", "InfoCpu")), Fd("memory", T("OBJECT", "InfoMemory")), Fd("versions", T("OBJECT", "InfoVersions"))),
                    Ty("InfoOs", Fd("hostname", S2("s")), Fd("distro", S2("s")), Fd("release", S2("s"))),
                    Ty("InfoCpu", Fd("brand", S2("s")), Fd("cores", T("SCALAR", "Int")), Fd("threads", T("SCALAR", "Int"))),
                    Ty("InfoMemory", Fd("total", S2("s")), Fd("free", S2("s"))),
                    Ty("InfoVersions", Fd("core", T("OBJECT", "CoreVersions"))), Ty("CoreVersions", Fd("unraid", S2("s"))),
                    Ty("UnraidArray", Fd("state", T("ENUM", "ArrayState")), Fd("capacity", T("OBJECT", "ArrayCapacity")), Fd("disks", T("OBJECT", "ArrayDisk", list: true)), Fd("parities", T("OBJECT", "ArrayDisk", list: true))),
                    Ty("ArrayCapacity", Fd("kilobytes", T("OBJECT", "Capacity"))), Ty("Capacity", Fd("free", S2("s")), Fd("used", S2("s")), Fd("total", S2("s"))),
                    Ty("ArrayDisk", Fd("id", S2("id")), Fd("name", S2("s")), Fd("device", S2("s")), Fd("size", S2("s")), Fd("status", T("ENUM", "DiskStatus")), Fd("temp", T("SCALAR", "Int"))),
                    Ty("Docker", Fd("containers", T("OBJECT", "DockerContainer", list: true, nonNull: true))),
                    autoStart
                        ? Ty("DockerContainer", Fd("id", S2("id")), Fd("names", T("SCALAR", "String", list: true)), Fd("image", S2("s")), Fd("state", T("ENUM", "ContainerState")), Fd("status", S2("s")), Fd("autoStart", T("SCALAR", "Boolean")))
                        : Ty("DockerContainer", Fd("id", S2("id")), Fd("names", T("SCALAR", "String", list: true)), Fd("image", S2("s")), Fd("state", T("ENUM", "ContainerState")), Fd("status", S2("s"))),
                    Ty("Vms", Fd("domains", T("OBJECT", "VmDomain", list: true))), Ty("VmDomain", Fd("id", S2("id")), Fd("name", S2("s")), Fd("state", T("ENUM", "VmState"))),
                };
                if (mutations)
                {
                    types.Add(Ty("Mutation", Fd("docker", T("OBJECT", "DockerMutations")), Fd("vm", T("OBJECT", "VmMutations"))));
                    types.Add(Ty("DockerMutations", Fd("start", T("OBJECT", "DockerContainer"), true), Fd("stop", T("OBJECT", "DockerContainer"), true)));
                    types.Add(Ty("VmMutations", Fd("start", T("SCALAR", "Boolean"), true), Fd("stop", T("SCALAR", "Boolean"), true), Fd("forceStop", T("SCALAR", "Boolean"), true)));   // no reboot in this API version
                }
                return new System.Text.Json.Nodes.JsonObject { ["data"] = new System.Text.Json.Nodes.JsonObject { ["__schema"] = new System.Text.Json.Nodes.JsonObject {
                    ["queryType"] = new System.Text.Json.Nodes.JsonObject { ["name"] = "Query" }, ["mutationType"] = mutations ? new System.Text.Json.Nodes.JsonObject { ["name"] = "Mutation" } : null, ["types"] = types } } }.ToJsonString();
            }
            string plex = "RUNNING", win = "RUNNING"; bool withMutations = true, withAutoStart = true, viewer = false;
            string? lastKey = null, lastQuery = null, lastMutation = null; System.Text.Json.Nodes.JsonNode? lastVars = null;
            var server = new FakeHttp(req =>
            {
                lastKey = req.Headers.TryGetValues("x-api-key", out var k) ? k.First() : null;
                var body = System.Text.Json.Nodes.JsonNode.Parse(req.Content!.ReadAsStringAsync().Result)!;
                var q = body["query"]!.ToString(); if (!q.StartsWith("mutation")) lastQuery = q; else lastVars = body["variables"];
                if (q.Contains("__schema")) return SchemaJson(withMutations, withAutoStart);
                if (q.Contains("versions")) return "{\"data\":{\"info\":{\"versions\":{\"core\":{\"unraid\":\"7.1.4\"}}}}}";
                if (q.StartsWith("mutation"))
                {
                    lastMutation = q;
                    if (viewer) return "{\"errors\":[{\"message\":\"Forbidden resource\"}],\"data\":null}";
                    var id = body["variables"]?["id"]?.ToString();
                    if (q.Contains("docker { stop") && id == "c1:abcd") plex = "EXITED";
                    if (q.Contains("docker { start") && id == "c1:abcd") plex = "RUNNING";
                    if (q.Contains("vm { stop") && id == "vm:1") win = "SHUTOFF";
                    return "{\"data\":{}}";
                }
                return "{\"data\":{\"info\":{\"os\":{\"hostname\":\"tower\",\"distro\":\"Unraid\",\"release\":\"7.1.4\"},\"cpu\":{\"brand\":\"Ryzen\",\"cores\":8,\"threads\":16},\"memory\":{\"total\":\"68719476736\"}}," +
                       "\"array\":{\"state\":\"STARTED\",\"capacity\":{\"kilobytes\":{\"free\":\"1000000000\",\"used\":\"3000000000\",\"total\":\"4000000000\"}},\"disks\":[{\"id\":\"d1\",\"name\":\"disk1\",\"device\":\"sdb\",\"size\":\"3907018532\",\"status\":\"DISK_OK\",\"temp\":34}],\"parities\":[{\"id\":\"p1\",\"name\":\"parity\",\"device\":\"sdc\",\"size\":\"3907018532\",\"status\":\"DISK_OK\",\"temp\":36}]}," +
                       "\"docker\":{\"containers\":[{\"id\":\"c1:abcd\",\"names\":[\"/plex\"],\"image\":\"plexinc/pms-docker\",\"state\":\"" + plex + "\",\"status\":\"Up 3 days\",\"autoStart\":true}]}," +
                       "\"vms\":{\"domains\":[{\"id\":\"vm:1\",\"name\":\"win11\",\"state\":\"" + win + "\"}]}}}";
            });
            var key = new Credential { Name = "unraid key (selftest)", Kind = CredentialKind.Password, Username = "unraid-api", Secret = "unraid-key-31337" };
            AppData.Current.Credentials.Add(key);
            var ucfg = new Infra.ProviderConfig { Kind = "unraid", Name = "tower", Endpoint = "https://tower.lan", CredentialId = key.Id, Environment = "LAB", AllowChanges = true };
            var un = new Infra.UnraidProvider(ucfg, server) { VerifyFor = TimeSpan.FromSeconds(3), PollEvery = TimeSpan.FromMilliseconds(50) };
            var ucaps = await un.DetectAsync(CancellationToken.None);
            if (!ucaps.Reachable || !ucaps.CanRead || !ucaps.CanControl || ucaps.Version != "7.1.4" || lastKey != "unraid-key-31337") throw new Exception("unraid detect: " + ucaps.Detail + " key " + lastKey);
            if (!ucaps.Notes.Any(n => n.Contains("docker start") && n.Contains("vm forceStop") && !n.Contains("vm reboot"))) throw new Exception("discovered mutations: " + string.Join(" | ", ucaps.Notes));
            var uinv = await un.InventoryAsync(CancellationToken.None);
            if (lastQuery == null || !lastQuery.Contains("autoStart") || lastQuery.Contains("caches") || lastQuery.Contains("temp") == false) throw new Exception("query built from schema: " + lastQuery);
            var plexObj = uinv.First(o => o.Kind == "container"); var winObj = uinv.First(o => o.Kind == "vm");
            if (uinv.Count(o => o.Kind == "disk") != 2 || uinv.All(o => o.Kind != "array") || uinv.All(o => o.Kind != "server") || plexObj.Name != "plex" || plexObj.Status != "running" || winObj.Status != "running")
                throw new Exception("unraid inventory: " + string.Join(", ", uinv.Select(o => o.Kind + ":" + o.Name + ":" + o.Status)));
            if (un.ActionsFor(winObj).Any(a => a.Id == "vm:reboot") || !un.ActionsFor(winObj).Any(a => a.Id == "vm:forceStop")) throw new Exception("vm actions must follow the schema");
            if (un.ActionsFor(uinv.First(o => o.Kind == "array")).Count != 0 || un.ActionsFor(uinv.First(o => o.Kind == "disk")).Count != 0) throw new Exception("array/disk actions must not exist");
            var stopC = await un.RunAsync(Infra.UnraidProvider.CStop, plexObj, CancellationToken.None);
            if (!stopC.Verified || lastMutation == null || lastMutation.Contains("c1:abcd") || !lastMutation.Contains("$id: PrefixedID!")) throw new Exception("docker stop / variables: " + stopC.Message + " · " + lastMutation);
            var stopV = await un.RunAsync(Infra.UnraidProvider.VStop, winObj, CancellationToken.None);
            if (!stopV.Verified || lastVars?["id"]?.ToString() != "vm:1") throw new Exception("vm stop: " + stopV.Message);
            viewer = true;
            var denied = await un.RunAsync(Infra.UnraidProvider.CStart, (await un.InventoryAsync(CancellationToken.None)).First(o => o.Kind == "container"), CancellationToken.None);
            if (denied.Accepted || !denied.Message.Contains("role")) throw new Exception("viewer key: " + denied.Message);
            // An older API: no mutations → read-only, no buttons; a field it lacks is never requested.
            withMutations = false; withAutoStart = false; viewer = false;
            var old = new Infra.UnraidProvider(ucfg, server);
            var ocaps = await old.DetectAsync(CancellationToken.None);
            var oinv = await old.InventoryAsync(CancellationToken.None);
            if (ocaps.CanControl || old.ActionsFor(oinv.First(o => o.Kind == "container")).Count != 0 || lastQuery!.Contains("autoStart")) throw new Exception("old API version");
            // A production API with introspection OFF (DarkCipher0x's server, s33): HTTP 400 INTROSPECTION_DISABLED. Discovery falls
            // back to probing: validation errors (with their column) remove unknown fields; mutations are probed without
            // their required $id, so the probe can never run one.
            plex = "RUNNING"; win = "RUNNING";
            int probeRuns = 0; var sentWithVars = 0; string? pLastQuery = null, pLastMutation = null;
            static string Err(string msg, int col) => "{\"message\":" + System.Text.Json.JsonSerializer.Serialize(msg) + ",\"locations\":[{\"line\":1,\"column\":" + col + "}],\"extensions\":{\"code\":\"GRAPHQL_VALIDATION_FAILED\"}}";
            var prod = new FakeHttpStatus(req =>
            {
                var body = System.Text.Json.Nodes.JsonNode.Parse(req.Content!.ReadAsStringAsync().Result)!;
                var q = body["query"]!.ToString();
                if (q.Contains("__schema")) return (400, "{\"errors\":[{\"message\":\"GraphQL introspection is not allowed, but the current request is for introspection.\",\"extensions\":{\"code\":\"INTROSPECTION_DISABLED\"}}]}");
                if (q.StartsWith("mutation"))
                {
                    if (body["variables"] is System.Text.Json.Nodes.JsonObject v && v["id"] != null)
                    {
                        sentWithVars++; pLastMutation = q;
                        var id = v["id"]!.ToString();
                        if (q.Contains("docker { stop") && id == "c1:abcd") plex = "EXITED";
                        return (200, "{\"data\":{}}");
                    }
                    var m = System.Text.RegularExpressions.Regex.Match(q, @"^mutation\(\$id: (\S+)\) \{ (\w+) \{ (\w+)\(id: \$id\)(.*) \} \}$");
                    var (idT, root, op, sel) = (m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value);
                    if (op == "reboot") return (400, "{\"errors\":[" + Err($"Cannot query field \"{op}\" on type \"VmMutations\".", q.IndexOf(op) + 1) + "]}");
                    if (root == "docker" && sel.Length == 0) return (400, "{\"errors\":[" + Err($"Field \"{op}\" of type \"DockerContainer!\" must have a selection of subfields. Did you mean \"{op} {{ ... }}\"?", q.IndexOf(op) + 1) + "]}");
                    if (root == "vm" && sel.Length > 0) return (400, "{\"errors\":[" + Err($"Field \"{op}\" must not have a selection since type \"Boolean!\" has no subfields.", q.IndexOf(op) + 1) + "]}");
                    if (idT != "PrefixedID!") return (400, "{\"errors\":[" + Err($"Variable \"$id\" of type \"{idT}\" used in position expecting type \"PrefixedID!\".", 10) + "]}");
                    return (400, "{\"errors\":[{\"message\":\"Variable \\\"$id\\\" of required type \\\"PrefixedID!\\\" was not provided.\",\"extensions\":{\"code\":\"BAD_USER_INPUT\"}}]}");
                }
                if (q.Contains("docker {"))
                {
                    // Fields this API version lacks: uptime, caches and the old vms.domain list.
                    var errs = new List<string>();
                    foreach (var bad in new[] { "uptime", "caches", "domain" })
                        if (System.Text.RegularExpressions.Regex.Match(q, $@"\b{bad}\b") is { Success: true } hit) errs.Add(Err($"Cannot query field \"{bad}\" on type \"X\".", hit.Index + 1));
                    if (errs.Count > 0) return (400, "{\"errors\":[" + string.Join(",", errs) + "]}");
                    if (q.Contains("versions {")) probeRuns++; else pLastQuery = q;
                }
                if (q == "query { info { versions { core { unraid } } } }") return (200, "{\"data\":{\"info\":{\"versions\":{\"core\":{\"unraid\":\"7.2.0\"}}}}}");
                return (200, "{\"data\":{\"info\":{\"os\":{\"hostname\":\"tower\",\"distro\":\"Unraid\",\"release\":\"7.2.0\"},\"cpu\":{\"brand\":\"Ryzen\",\"cores\":8,\"threads\":16},\"memory\":{\"total\":\"68719476736\"},\"versions\":{\"core\":{\"unraid\":\"7.2.0\"}}}," +
                       "\"array\":{\"state\":\"STARTED\",\"disks\":[{\"id\":\"d1\",\"name\":\"disk1\",\"device\":\"sdb\",\"size\":\"3907018532\",\"status\":\"DISK_OK\",\"temp\":34}]}," +
                       "\"docker\":{\"containers\":[{\"id\":\"c1:abcd\",\"names\":[\"/plex\"],\"image\":\"plexinc/pms-docker\",\"state\":\"" + plex + "\",\"status\":\"Up 3 days\",\"autoStart\":true}]}," +
                       "\"vms\":{\"domains\":[{\"id\":\"vm:1\",\"name\":\"win11\",\"state\":\"" + win + "\"}]}}}");
            });
            var pun = new Infra.UnraidProvider(ucfg, prod) { VerifyFor = TimeSpan.FromSeconds(3), PollEvery = TimeSpan.FromMilliseconds(50) };
            var pcaps = await pun.DetectAsync(CancellationToken.None);
            if (!pcaps.Reachable || !pcaps.CanRead || !pcaps.CanControl || pcaps.Version != "7.2.0" || !pcaps.Notes.Any(n => n.Contains("introspection switched off")))
                throw new Exception("introspection off, detect: " + pcaps.Detail + " | " + string.Join(" | ", pcaps.Notes));
            if (!pcaps.Notes.Any(n => n.Contains("docker start") && n.Contains("vm forceStop") && !n.Contains("vm reboot"))) throw new Exception("introspection off, mutations: " + string.Join(" | ", pcaps.Notes));
            if (sentWithVars != 0 || plex != "RUNNING" || probeRuns != 1) throw new Exception($"the probe must never run a mutation (sent {sentWithVars}, probe runs {probeRuns})");
            var pinv = await pun.InventoryAsync(CancellationToken.None);
            if (pLastQuery == null || System.Text.RegularExpressions.Regex.IsMatch(pLastQuery, @"\b(uptime|caches|domain)\b") || !pLastQuery.Contains("autoStart") || !pLastQuery.Contains("domains"))
                throw new Exception("introspection off, inventory query: " + pLastQuery);
            var pplex = pinv.First(o => o.Kind == "container");
            if (pplex.Status != "running" || pinv.All(o => o.Kind != "vm") || pinv.Count(o => o.Kind == "disk") != 1) throw new Exception("introspection off, inventory: " + string.Join(", ", pinv.Select(o => o.Kind + ":" + o.Name)));
            if (pun.ActionsFor(pinv.First(o => o.Kind == "vm")).Any(a => a.Id == "vm:reboot")) throw new Exception("introspection off: reboot must not be offered");
            var pstop = await pun.RunAsync(Infra.UnraidProvider.CStop, pplex, CancellationToken.None);
            if (!pstop.Verified || pLastMutation == null || !pLastMutation.Contains("$id: PrefixedID!") || !pLastMutation.Contains("{ __typename }") || pLastMutation.Contains("c1:abcd"))
                throw new Exception("introspection off, docker stop: " + pstop.Message + " · " + pLastMutation);
            if (Infra.UnraidProvider.ValidateEndpoint("http://203.0.113.5") == null || Infra.UnraidProvider.ValidateEndpoint("http://tower.lan") != null || Infra.UnraidProvider.ValidId("x\"){ evil") || !Infra.UnraidProvider.ValidId("c1:abcd")) throw new Exception("endpoint/id rules");
            if (Infra.UnraidProvider.NormalizeState("vm", "SHUTOFF") != "stopped" || Infra.UnraidProvider.NormalizeState("container", "EXITED") != "exited") throw new Exception("state normalisation");
            Ok("unraid: introspection → capabilities (info/array/docker/VMs; docker start/stop + vm start/stop/forceStop, no reboot because the schema lacks it), inventory query built only from existing fields, server/array/disks/containers/VMs parsed, no array/disk actions, verified docker + VM stop, ids sent as GraphQL variables only, VIEWER key refused with a clear reason, older API without mutations → read-only, http only on the LAN; introspection OFF (HTTP 400 INTROSPECTION_DISABLED) → discovery by probing: unknown fields removed by validation-error column, mutations probed without $id (none ran), id type and result selection learned, verified docker stop");
        }
        catch (Exception ex) { Fail("unraid", ex); }
    }
}
