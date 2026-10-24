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

// SelfTest: the local API and broadcast, add-ons in the sandbox, embedded Remote Desktop (moved unchanged out of RunFullAsync in 0.2.0 phase 6½; the run's shared state is SelfTestRun).
public static partial class SelfTest
{
    private static async Task ApiAddonsRdpAsync(SelfTestRun sharedRun)
    {
        var main = sharedRun.Main; var reportPath = sharedRun.ReportPath; Action<string> Ok = sharedRun.Ok; Action<string, Exception?> Fail = sharedRun.Fail; SelfTestRun.SettleFn Settle = sharedRun.Settle;

        // ADR-011: local API + OBS broadcast over REAL loopback HTTP (raw sockets too): off by default, loopback bind,
        // token auth/scopes/expiry/revocation, Host/Origin checks, limits, lockout, writes audited; terminal broadcast
        // redaction (secrets, keys, IPs, e-mail, custom), password-prompt hiding, stop/close, incident card, indicator.
        try
        {
            var st = AppSettings.Current;
            int port;
            { var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); probe.Start(); port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port; probe.Stop(); }
            st.LocalApiPort = port; st.LocalApiEnabled = false; Api.LocalServer.Sync();
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            if (Api.LocalServer.Running) throw new Exception("listening while off");
            try { await http.GetAsync($"http://127.0.0.1:{port}/api/v1/status"); throw new Exception("something answered while off"); } catch (System.Net.Http.HttpRequestException) { }
            st.LocalApiEnabled = true; Api.LocalServer.Sync();
            if (!Api.LocalServer.Running) throw new Exception("not started: " + Api.LocalServer.LastError);
            var listeners = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Where(e => e.Port == port).ToList();
            if (listeners.Count == 0 || listeners.Any(e => !System.Net.IPAddress.IsLoopback(e.Address))) throw new Exception("not bound to loopback only: " + string.Join(",", listeners));

            var (tok, secret) = Api.ApiTokens.Create("selftest", new[] { "status.read", "hosts.read", "incidents.read", "incidents.write" }, TimeSpan.FromHours(1));
            if (JsonSerializer.Serialize(SecureStore.Read<AppSettings>("settings.dcx").ApiTokens, SecureStore.Json).Contains(secret[4..]) || tok.Hash.Length != 64) throw new Exception("token stored in readable form");
            var baseUrl = $"http://127.0.0.1:{port}";
            async Task<(int Code, string Body)> Call(string method, string path, string? bearer = null, string? json = null, Action<System.Net.Http.HttpRequestMessage>? tweak = null)
            {
                var req = new System.Net.Http.HttpRequestMessage(new System.Net.Http.HttpMethod(method), baseUrl + path);
                if (bearer != null) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearer);
                if (json != null) req.Content = new System.Net.Http.StringContent(json, Encoding.UTF8, "application/json");
                tweak?.Invoke(req);
                var resp = await http.SendAsync(req);
                return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync());
            }
            if ((await Call("GET", "/api/v1/status")).Code != 401 || (await Call("GET", "/api/v1/status", "dcx_" + new string('A', 43))).Code != 401) throw new Exception("auth required");
            var s1 = await Call("GET", "/api/v1/status", secret);
            if (s1.Code != 200 || !s1.Body.Contains(App.Version)) throw new Exception("status " + s1.Code);
            var hosts = await Call("GET", "/api/v1/hosts", secret);
            if (hosts.Code != 200 || !hosts.Body.Contains("selftest-host") || hosts.Body.Contains("s3cret-value-xyz") || hosts.Body.Contains("credential", StringComparison.OrdinalIgnoreCase)) throw new Exception("hosts: " + hosts.Body[..Math.Min(200, hosts.Body.Length)]);
            if ((await Call("POST", "/api/v1/notes", secret, "{\"title\":\"x\"}")).Code != 403) throw new Exception("missing scope accepted");
            var apiInc = Incidents.IncidentStore.Create("selftest: api incident");
            Incidents.IncidentStore.Active = apiInc;
            var ev = await Call("POST", "/api/v1/incidents/active/events", secret, "{\"title\":\"disk alert from monitoring\",\"text\":\"/var at 97%\",\"kind\":\"log\"}");
            if (ev.Code != 201 || !apiInc.Items.Any(x => x.Kind == Incidents.ItemKind.Log && x.Title.Contains("(API: selftest)"))) throw new Exception("add event " + ev.Code + " " + ev.Body);
            var one = await Call("GET", $"/api/v1/incidents/{apiInc.Id}", secret);
            if (one.Code != 200 || !one.Body.Contains("disk alert from monitoring")) throw new Exception("get incident");
            if ((await Call("GET", "/api/v1/incidents/..%2f..%2fsettings", secret)).Code is not (400 or 404)) throw new Exception("traversal id");
            if ((await Call("GET", "/api/v1/status", secret, tweak: r => r.Headers.TryAddWithoutValidation("Origin", "https://evil.example"))).Code != 403) throw new Exception("browser Origin accepted");
            if ((await Call("GET", "/api/v1/status", secret, tweak: r => r.Headers.Host = "evil.example:" + port)).Code != 403) throw new Exception("foreign Host accepted (DNS rebinding)");
            if ((await Call("POST", "/api/v1/incidents/active/events", secret, "{\"title\":\"" + new string('a', 70_000) + "\"}")).Code != 413) throw new Exception("oversize body");
            if ((await Call("POST", "/api/v1/incidents/active/events", secret, tweak: r => r.Content = new System.Net.Http.StringContent("{\"title\":\"x\"}", Encoding.UTF8, "text/plain"))).Code != 415) throw new Exception("content type");
            if ((await Call("POST", "/api/v1/incidents/active/events", secret, "{not json")).Code != 400 || (await Call("POST", "/api/v1/incidents/active/events", secret, "{\"title\":5}")).Code != 400) throw new Exception("validation");
            bool limited = false;
            for (int i = 0; i < 60 && !limited; i++) limited = (await Call("GET", "/api/v1/status", secret)).Code == 429;
            if (!limited) throw new Exception("no rate limit");
            await Task.Delay(1500);
            // raw protocol abuse
            async Task<string> Raw(string text, bool sendAll = true)
            {
                using var c = new System.Net.Sockets.TcpClient();
                await c.ConnectAsync(System.Net.IPAddress.Loopback, port);
                var ns = c.GetStream();
                if (sendAll) { var bytes = Encoding.ASCII.GetBytes(text); await ns.WriteAsync(bytes); }
                var buf = new byte[512];
                var cts = new CancellationTokenSource(9000);
                int n = await ns.ReadAsync(buf, cts.Token);
                return Encoding.ASCII.GetString(buf, 0, n);
            }
            if (!(await Raw($"POST /api/v1/notes HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nTransfer-Encoding: chunked\r\n\r\n")).Contains(" 501 ")) throw new Exception("chunked");
            if (!(await Raw($"GET /api/v1/status HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nX-Pad: {new string('p', 9000)}\r\n\r\n")).Contains(" 431 ")) throw new Exception("huge headers");
            if (!(await Raw($"DELETE /api/v1/status HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n\r\n")).Contains(" 405 ")) throw new Exception("method");
            if (!(await Raw("", sendAll: false)).Contains(" 408 ")) throw new Exception("slow request not timed out");
            // revocation, expiry, lockout
            Api.ApiTokens.Revoke(tok);
            if ((await Call("GET", "/api/v1/status", secret)).Code != 401) throw new Exception("revoked token works");
            var (_, shortSecret) = Api.ApiTokens.Create("selftest-short", new[] { "status.read" }, TimeSpan.FromMilliseconds(1));
            await Task.Delay(50);
            if ((await Call("GET", "/api/v1/status", shortSecret)).Code != 401) throw new Exception("expired token works");
            Api.LocalApi.ResetLimits();
            for (int i = 0; i < 22; i++) await Call("GET", "/api/v1/status", "dcx_wrong" + i + new string('x', 30));
            var (_, goodSecret) = Api.ApiTokens.Create("selftest-good", new[] { "status.read" }, TimeSpan.FromHours(1));
            if ((await Call("GET", "/api/v1/status", goodSecret)).Code != 429) throw new Exception("no lockout after repeated failures");
            Api.LocalApi.ResetLimits();
            if ((await Call("GET", "/api/v1/status", goodSecret)).Code != 200) throw new Exception("after lockout reset");
            if (!AuditLog.Read(DateTime.Now.ToString("yyyy-MM")).Any(a => a.Action.Contains("event added via local API"))) throw new Exception("API write not audited");
            Ok("local API: off = nothing listening, loopback-only bind, hashed tokens, auth/scopes, no credentials in /hosts, incident event write (audited), Origin + foreign Host refused, 64 KB / 8 KB / chunked / method / 5 s limits, rate limit, revocation, expiry, failure lockout");

            // terminal broadcast
            var bHost = AppData.Current.Hosts.First(h => h.Name == "selftest-host");
            main.OpenTerminal(bHost, connectOnOpen: false);
            var tv = main.Panes.AllTabs.Select(t => t.Content).OfType<TerminalView>().Last();
            for (int i = 0; i < 80 && !tv.PageReady; i++) await Settle(250);
            if (!tv.PageReady) throw new Exception("terminal page not ready");
            tv.TestFeed(Encoding.UTF8.GetBytes("\x1b[2J\x1b[Hops@web:~$ cat app.env\r\nDB_PASSWORD=hunter2xyz\r\nX-Sig: Zm9vYmFyYmF6cXV4MTIzNDU2Nzg5MEFCQ0RFRkdISUpLTE1OT1A\r\nconnect 203.0.113.9 as ops@example.com via prod-db-17\r\n-----BEGIN OPENSSH PRIVATE KEY-----\r\nb3BlbnNzaC1rZXktdjEAAAAABG5vbmUAAAAEbm9uZQAAAAAAAAABAAAAMwAAAAtzc2gtZW\r\naGVsbG8gd29ybGQgdGhpcyBpcyBhIGZha2Uga2V5IGZvciB0aGUgc2VsZnRlc3Q\r\nAKIAIOSFODNN7EXAMPLE ok\r\nops@web:~$ ")); // secret-scan: example
            await Settle(400);
            var cast = Api.Broadcasts.Start(new Api.TerminalSource(tv), "terminal", new Api.BroadcastOptions { MaskIps = true, MaskEmails = true, Patterns = { @"prod-db-\d+" } });
            // Off-screen WebView2 timers may be throttled: poll until the frame reflects the output (≤ 15 s).
            async Task TickUntil(Func<Api.Broadcast, bool> done)
            {
                for (int i = 0; i < 30; i++) { await Api.Broadcasts.TickAsync(); if (done(cast)) return; await Settle(500); }
            }
            await TickUntil(b => b.Lines.Any(l => l.Contains("cat app.env")));
            var page = await http.GetAsync(cast.Url);
            if ((int)page.StatusCode != 200 || !page.Headers.TryGetValues("Content-Security-Policy", out var csp) || !csp.First().Contains("default-src 'none'")) throw new Exception("broadcast page/CSP");
            async Task<JsonElement> Frame() { var r = await http.GetStringAsync(cast.Url + "frame"); return JsonDocument.Parse(r).RootElement.Clone(); }
            var f1 = await Frame();
            var text = string.Join("\n", f1.GetProperty("lines").EnumerateArray().Select(x => x.GetString()));
            foreach (var leak in new[] { "hunter2xyz", "Zm9vYmFyYmF6", "203.0.113.9", "ops@example.com", "prod-db-17", "b3BlbnNzaC1", "aGVsbG8gd29y", "AKIAIOSFODNN7EXAMPLE" }) // secret-scan: example
                if (text.Contains(leak)) throw new Exception($"broadcast leaked “{leak}”:\n{text}");
            if (!text.Contains("cat app.env") || !text.Contains("[REDACTED")) throw new Exception("broadcast content:\n" + text);
            if (main.BroadcastBadge.Visibility != Visibility.Visible) throw new Exception("no BROADCASTING indicator");
            var shots = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(reportPath)!, "shots")).FullName;
            Snap(main, Path.Combine(shots, "terminal-broadcast.png"));
            long seq = f1.GetProperty("seq").GetInt64();
            tv.TestFeed(Encoding.UTF8.GetBytes("sudo systemctl restart nginx\r\n[sudo] password for ops: "));
            await TickUntil(b => b.Hidden);
            var f2 = await Frame();
            if (!f2.GetProperty("hidden").GetBoolean() || f2.GetProperty("seq").GetInt64() != seq) throw new Exception("password prompt not hidden");
            tv.TestFeed(Encoding.UTF8.GetBytes("\r\nops@web:~$ "));
            await TickUntil(b => !b.Hidden);
            if ((await Frame()).GetProperty("hidden").GetBoolean()) throw new Exception("stayed hidden");
            var foreign = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, cast.Url + "frame");
            foreign.Headers.TryAddWithoutValidation("Origin", "https://evil.example");
            if ((int)(await http.SendAsync(foreign)).StatusCode != 403) throw new Exception("cross-origin frame");
            if ((int)(await http.GetAsync($"{baseUrl}/b/{new string('A', 32)}/frame")).StatusCode != 404) throw new Exception("guessed token");
            var tab = main.Panes.AllTabs.First(t => t.Content == tv);
            main.CloseTab(tab); await Settle(300);
            if (Api.Broadcasts.For(tv) != null || (int)(await http.GetAsync(cast.Url + "frame")).StatusCode != 404) throw new Exception("broadcast survived closing its tab");
            if (main.BroadcastBadge.Visibility == Visibility.Visible) throw new Exception("indicator after stop");

            // incident status card: titles only, never entry text
            var card = Api.Broadcasts.Start(new Api.IncidentSource(apiInc), "incident", new Api.BroadcastOptions());
            await Api.Broadcasts.TickAsync();
            var cardText = await http.GetStringAsync(card.Url + "frame");
            if (!cardText.Contains("selftest: api incident") || cardText.Contains("/var at 97%")) throw new Exception("incident card: " + cardText);
            if (Api.Broadcasts.StopAll("self-test") != 1 || (int)(await http.GetAsync(card.Url + "frame")).StatusCode != 404) throw new Exception("stop all");
            if (Api.BroadcastRedactor.Prompt.IsMatch("ops@web:~$ ls") || !Api.BroadcastRedactor.Prompt.IsMatch("Enter passphrase for key '/home/ops/.ssh/id_ed25519': ")) throw new Exception("prompt regex");
            try { Api.Broadcasts.Start(new Api.IncidentSource(apiInc), "incident", new Api.BroadcastOptions { Patterns = { "([unclosed" } }); throw new Exception("bad pattern accepted"); } catch (InvalidOperationException) { }

            // switch off again: nothing listening
            st.LocalApiEnabled = false; Api.LocalServer.Sync();
            if (Api.LocalServer.Running) throw new Exception("still listening after switching off");
            Api.ApiTokens.RevokeAll(); Api.ApiTokens.Purge();
            Incidents.IncidentStore.Delete(apiInc); Incidents.IncidentStore.EmptyTrash();
            Ok("broadcast: terminal screen as text over loopback with CSP page; secrets, private key (partial), key-like strings, AWS key, IPs, e-mail and custom pattern masked; [sudo] prompt freezes + hides; Origin/guessed token refused; BROADCASTING badge; stopped by closing the tab and Stop all; incident card shows titles only; bad regex refused; rendered terminal-broadcast.png");
        }
        catch (Exception ex) { Fail("local API / broadcast", ex); try { Api.Broadcasts.StopAll("self-test failure"); AppSettings.Current.LocalApiEnabled = false; Api.LocalServer.Sync(); } catch { } }

        // ADR-012: add-ons — package checks, approval grants, a REAL sandboxed run (Low integrity + job) with the bridge,
        // permission denials, user-mediated credentials, crash/hang/flood/garbage handled, tamper detection, updates that
        // can't gain permissions silently, downgrade/zip-slip refused, disable/revoke/remove; the repo sample too.
        try
        {
            var work = Path.Combine(Path.GetDirectoryName(reportPath)!, "addon-pkg");
            if (Directory.Exists(work)) Directory.Delete(work, true);
            Directory.CreateDirectory(work);
            const string script = """
                $ErrorActionPreference = 'Stop'
                # API 2 (ADR-035): every line after the handshake is an AES-256-GCM frame (samples/addons/sdk/DcxBridge.dll).
                Add-Type -Path (Join-Path $PSScriptRoot 'DcxBridge.dll')
                $script:n = 0
                function Send($o) { [DcxBridge]::Write(($o | ConvertTo-Json -Compress -Depth 8)) }
                function Call($m, $p) { $script:n++; Send @{ id = $script:n; method = $m; params = $p }; [DcxBridge]::Read() | ConvertFrom-Json }
                $inv = [DcxBridge]::Connect() | ConvertFrom-Json
                $r = [ordered]@{}
                switch ($inv.params.command) {
                  'probe' {
                    $r.info = (Call 'app.info' @{}).result.addon
                    $h = Call 'hosts.list' @{}
                    $r.hosts = @($h.result).Count
                    $r.hostsError = $h.error.code
                    $r.hostsHasSecret = (($h | ConvertTo-Json -Depth 8 -Compress) -match 's3cret-value-xyz')
                    $e = Call 'incidents.addEvent' @{ title = 'probe event'; text = 'from the sandbox' }
                    $r.event = [bool]$e.result.id
                    $r.notesError = (Call 'notes.create' @{ title = 'should fail' }).error.code
                    $r.unknown = (Call 'no.such' @{}).error.code
                    try { [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'pwn.txt'), 'x'); $r.writeInstall = 'ALLOWED' } catch { $r.writeInstall = 'denied' }
                    try { [IO.File]::WriteAllText((Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'dcx-addon-pwn.txt'), 'x'); $r.writeDocs = 'ALLOWED' } catch { $r.writeDocs = 'denied' }
                    try { [IO.File]::WriteAllText((Join-Path $env:DCX_ADDON_DATA 'ok.txt'), 'x'); $r.writeData = 'ok' } catch { $r.writeData = 'FAILED' }
                    $r.secretEnv = [bool]$env:DCX_TEST_SECRET
                    $r.context = $inv.params.context.hostName
                  }
                  'cred' { $c = Call 'credentials.request' @{ reason = 'self-test login' }; $r.code = $c.error.code; $r.secret = $c.result.secret }
                  # ADR-036: the full-control API over the same encrypted bridge.
                  'full' {
                    $c = (Call 'app.capabilities' @{}).result
                    $r.encrypted = $c.encrypted; $r.tools = @($c.tools).Count; $r.methods = @($c.methods).Count
                    $r.hasRun = [bool](@($c.tools) | Where-Object { $_.name -eq 'run_command' -and $_.permission -eq 'app.control' })
                    $r.listHosts = (Call 'app.call' @{ tool = 'list_hosts'; args = @{} }).result.result -match 'selftest-host'
                    $r.run = (Call 'app.call' @{ tool = 'run_command'; args = @{ host = 'selftest-host'; command = 'id' } }).error.code
                    $r.noTool = (Call 'app.call' @{ tool = 'no_such_tool' }).error.code
                    $add = Call 'hosts.add' @{ name = 'addon-made'; address = '192.0.2.77'; port = 2222; folder = 'Addon' }
                    $r.added = [string]$add.result.id
                    $r.updated = (Call 'hosts.update' @{ hostId = $add.result.id; tags = 'from-addon' }).result.changed
                    $r.removed = (Call 'hosts.remove' @{ hostId = $add.result.id }).result.removed
                    $r.badPort = (Call 'hosts.add' @{ name = 'x'; address = '192.0.2.78'; port = 70000 }).error.code
                    $r.uiBad = (Call 'ui.open' @{ screen = 'nowhere' }).error.code
                    $r.env = $env:DCX_ADDON_API
                  }
                  'crash' { exit 3 }
                  'hang' { Start-Sleep -Seconds 60 }
                  # A real flood: one pre-built line written as fast as possible (per-message ConvertTo-Json + flush was so slow
                  # under load that the 5 s run timeout sometimes fired first — the s26 "timed out" flake).
                  'flood' { $l = '{"jsonrpc":"2.0","method":"log","params":{"text":"flood"}}'; for ($i = 0; $i -lt 3000; $i++) { [DcxBridge]::Write($l) } }
                  # a plain-text line on the encrypted bridge: refused and the run stopped
                  'bad' { [Console]::Out.WriteLine('{"jsonrpc":"2.0","method":"done","params":{"text":"plain text"}}'); [Console]::Out.Flush(); Start-Sleep 10 }
                }
                Send @{ method = 'done'; params = @{ text = ($r | ConvertTo-Json -Compress); show = $false } }
                """;
            byte[] Sdk(string name) { using var rs = typeof(SelfTest).Assembly.GetManifestResourceStream("sdk/" + name)!; using var ms = new MemoryStream(); rs.CopyTo(ms); return ms.ToArray(); }
            string Pack(string version, string[] perms, string? evilEntry = null, string? permOverride = null, int api = 2, string? scriptOverride = null, string id = "com.example.selftest", int timeout = 5)
            {
                var path = Path.Combine(work, $"{id}-{version}{(evilEntry != null ? "-evil" : "")}.dcxaddon");
                if (File.Exists(path)) File.Delete(path);
                var manifest = JsonSerializer.Serialize(new
                {
                    api, id, name = "Self-test add-on", version, publisher = "Self-test", description = "probe",
                    entry = "test.ps1", timeoutSeconds = timeout, permissions = permOverride != null ? new[] { permOverride } : perms,
                    commands = new[] { "probe", "cred", "crash", "hang", "flood", "bad", "full" }.Select(c => new { id = c, title = c, context = c == "probe" ? "host" : "global" }),
                });
                using var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
                using (var w = new StreamWriter(zip.CreateEntry("addon.json").Open())) w.Write(manifest);
                using (var w = new StreamWriter(zip.CreateEntry("test.ps1").Open(), new UTF8Encoding(true))) w.Write(scriptOverride ?? script);
                using (var st = zip.CreateEntry("DcxBridge.dll").Open()) st.Write(Sdk("DcxBridge.dll"));
                if (evilEntry != null) using (var w = new StreamWriter(zip.CreateEntry(evilEntry).Open())) w.Write("x");
                return path;
            }
            var perms1 = new[] { "hosts.read", "incidents.write", "credentials.request" };
            var info = Addons.AddonPackages.Inspect(Pack("1.0.0", perms1));
            if (info.Refusal != null || info.NewPermissions.Count != 3 || !info.Signature.StartsWith("script")) throw new Exception("inspect: " + info.Refusal);
            var a = Addons.AddonPackages.Install(info, new[] { "hosts.read", "incidents.write" });
            if (a.Granted.Count != 2 || a.Granted.Contains("credentials.request") || a.Files.Count != 3 || a.Folder.StartsWith(SecureStore.DataDir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) throw new Exception($"install grants/files: {string.Join(",", a.Granted)} · {a.Files.Count} files · {a.Folder}");

            // a direct look at the sandbox: Low integrity (RID 0x1000) inside a job
            using (var sp = Addons.SandboxProcess.Start($"\"{Path.Combine(Environment.SystemDirectory, "cmd.exe")}\" /c ping -n 3 127.0.0.1", Environment.SystemDirectory, new Dictionary<string, string> { ["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot")! }))
            {
                var rid = sp.IntegrityRid();
                if (rid != 0x1000) throw new Exception($"sandbox integrity RID 0x{rid:X} (expected Low 0x1000)");
            }

            var aHost = AppData.Current.Hosts.First(h => h.Name == "selftest-host");
            var inc = Incidents.IncidentStore.Create("selftest: add-on incident");
            Incidents.IncidentStore.Active = inc;
            Environment.SetEnvironmentVariable("DCX_TEST_SECRET", "leak-me");
            var docsPwn = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "dcx-addon-pwn.txt");
            var openable = new List<bool>();
            Addons.AddonHost.Started = sp => openable.Add(sp.OthersCanOpen());
            var r1 = await Addons.AddonHost.RunAsync(a, "probe", new Addons.AddonContext(aHost));
            Addons.AddonHost.Started = null;
            Environment.SetEnvironmentVariable("DCX_TEST_SECRET", null);
            if (File.Exists(docsPwn)) File.Delete(docsPwn);
            if (!r1.Ok) throw new Exception($"probe run: {r1.Status} · {r1.Text} · {string.Join(" | ", r1.Log)}");
            var pr = JsonDocument.Parse(r1.Text).RootElement;
            string P(string k) => pr.TryGetProperty(k, out var v) ? v.ToString() : "";
            if (P("info") != "com.example.selftest" || P("hosts") == "0" || P("hostsHasSecret") != "False" || P("event") != "True" || P("notesError") != "-32001" || P("unknown") != "-32601") throw new Exception("bridge: " + r1.Text);
            if (P("writeInstall") != "denied" || P("writeDocs") != "denied" || P("writeData") != "ok" || P("secretEnv") != "False" || P("context") != aHost.Display) throw new Exception("sandbox: " + r1.Text);
            if (!inc.Items.Any(x => x.Title.Contains("(add-on: Self-test add-on)"))) throw new Exception("incident event from add-on");
            if (openable.Count != 1 || openable[0]) throw new Exception("another process could open the running add-on (memory read / handle duplication)");
            Ok($"add-on run: real Low-integrity sandbox (RID 0x1000) + job, which no other process can open (no memory reads, handle duplication or injection); ENCRYPTED bridge (API 2: ECDH P-256 handshake, AES-256-GCM frames) app.info/hosts.list (no secrets)/incidents.addEvent; ungranted notes.write → -32001, unknown → -32601; writes to its install folder and Documents denied, its data folder allowed; our environment secrets not inherited");

            // credentials: not granted → denied; granted → user decides each time
            var cr = await Addons.AddonHost.RunAsync(a, "cred", new Addons.AddonContext());
            if (!cr.Ok || JsonDocument.Parse(cr.Text).RootElement.GetProperty("code").ToString() != "-32001") throw new Exception("cred without permission: " + cr.Text);
            Addons.AddonPackages.SetPermission(a, "credentials.request", true);
            var promptWas = Addons.AddonHost.CredentialPrompt;
            int asked = 0;
            Addons.AddonHost.CredentialPrompt = (_, reason) => { asked++; return null; };
            var cr2 = await Addons.AddonHost.RunAsync(a, "cred", new Addons.AddonContext());
            Addons.AddonHost.CredentialPrompt = (_, _) => { asked++; return AppData.Current.Credentials.First(c => c.Name == "selftest"); };
            var cr3 = await Addons.AddonHost.RunAsync(a, "cred", new Addons.AddonContext());
            Addons.AddonHost.CredentialPrompt = promptWas;
            if (asked != 2 || JsonDocument.Parse(cr2.Text).RootElement.GetProperty("code").ToString() != "-32003" || JsonDocument.Parse(cr3.Text).RootElement.GetProperty("secret").ToString() != "s3cret-value-xyz") throw new Exception($"credential prompts: {asked} · {cr2.Text} · {cr3.Text}");
            if (!AuditLog.Read(DateTime.Now.ToString("yyyy-MM")).Any(x => x.Action == "credential request DENIED")) throw new Exception("credential denial not audited");

            // misbehaviour ends the run, never the app
            var crash = await Addons.AddonHost.RunAsync(a, "crash", new Addons.AddonContext());
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var hang = await Addons.AddonHost.RunAsync(a, "hang", new Addons.AddonContext());
            var hangMs = sw.ElapsedMilliseconds;
            // The flood gets its own copy with a 30 s limit: 3,000 encrypted lines from PowerShell can take over 5 s on a
            // busy PC (s40: "timed out" instead of "flood" in the 0.1.19 build), and the check is about the flood limit, not speed.
            var floodA = Addons.AddonPackages.Install(Addons.AddonPackages.Inspect(Pack("1.0.0", perms1, id: "com.example.selftest.flood", timeout: 30)), Array.Empty<string>());
            var flood = await Addons.AddonHost.RunAsync(floodA, "flood", new Addons.AddonContext());
            Addons.AddonPackages.Remove(floodA);
            var bad = await Addons.AddonHost.RunAsync(a, "bad", new Addons.AddonContext());
            if (crash.Ok || !crash.Status.Contains("exit code 3") || hang.Status != "timed out" || hangMs > 15000 || flood.Status != "flood" || bad.Status != "protocol error") throw new Exception($"misbehaviour: {crash.Status} / {hang.Status} {hangMs} ms / {flood.Status} / {bad.Status}");
            Ok($"add-on misbehaviour: credentials only via a per-request prompt (denied → -32003, audited; approved → that one secret, sent encrypted); crash (exit 3), hang (killed at timeout, {hangMs} ms), flood and a plain-text line on the encrypted bridge each end the run cleanly");

            // ADR-035: the plain-text bridge is gone — API 1 program add-ons are refused at install and blocked if already installed;
            // an add-on that skips the encrypted handshake is stopped before it gets anything.
            if (Addons.AddonPackages.Inspect(Pack("1.0.5", perms1, api: 1)).Refusal?.Contains("unencrypted") != true) throw new Exception("an API 1 (plain-text) program add-on was accepted");
            a.Manifest.Api = 1;
            var old = await Addons.AddonHost.RunAsync(a, "probe", new Addons.AddonContext());
            a.Manifest.Api = 2;
            if (old.Status != "blocked") throw new Exception("an installed API 1 add-on ran: " + old.Status);
            const string plainScript = """
                [Console]::In.ReadLine() | Out-Null
                [Console]::Out.WriteLine('{"jsonrpc":"2.0","id":1,"method":"hosts.list","params":{}}'); [Console]::Out.Flush()
                Start-Sleep 10
                """;
            var plainInfo = Addons.AddonPackages.Inspect(Pack("1.0.6", perms1, scriptOverride: plainScript));
            var plainA = Addons.AddonPackages.Install(plainInfo, new[] { "hosts.read" });
            var plainRun = await Addons.AddonHost.RunAsync(plainA, "probe", new Addons.AddonContext());
            if (plainRun.Status != "protocol error" || plainRun.Ok || !plainRun.Log.Any(l => l.Contains("handshake"))) throw new Exception($"a plain-text add-on: {plainRun.Status} · {string.Join(" | ", plainRun.Log)}");
            a = Addons.AddonPackages.Install(Addons.AddonPackages.Inspect(Pack("1.0.7", perms1)), new[] { "hosts.read", "incidents.write", "credentials.request" });
            Ok("add-on bridge encryption: API 1 (plain-text) program add-ons are refused at install and blocked if already installed; an add-on that skips the encrypted handshake gets nothing and is stopped");

            // ADR-036: the full-control API. Discover everything, call any app capability (reads with app.read; changes need
            // app.control AND the user's yes), manage hosts (each change approved), refuse what isn't granted.
            {
                var fullPerms = new[] { "app.read", "app.control", "hosts.write", "ui.control" };
                var fa = Addons.AddonPackages.Install(Addons.AddonPackages.Inspect(Pack("1.0.0", fullPerms, id: "com.example.selftest.full", timeout: 30)), new[] { "app.read", "hosts.write", "ui.control" });
                var approverWas = Addons.AddonApi.Approver;
                var approvals = new List<string>();
                // Host changes approved, everything else (the SSH command) declined; nothing is ever shown on screen.
                Addons.AddonApi.Approver = (_, title, _, _) => { approvals.Add(title); return Task.FromResult(title.Contains("host")); };
                try
                {
                    var hostsBefore = AppData.Current.Hosts.Count;
                    var f1 = await Addons.AddonHost.RunAsync(fa, "full", new Addons.AddonContext());
                    if (!f1.Ok) throw new Exception($"full run: {f1.Status} · {f1.Text} · {string.Join(" | ", f1.Log)}");
                    var fr = JsonDocument.Parse(f1.Text).RootElement;
                    string F(string k) => fr.TryGetProperty(k, out var v) ? v.ToString() : "";
                    if (F("encrypted") != "True" || !int.TryParse(F("tools"), out var nTools) || nTools < 30 || !int.TryParse(F("methods"), out var nMethods) || nMethods < 18 || F("hasRun") != "True" || F("env") != "2")
                        throw new Exception("capabilities: " + f1.Text);
                    if (F("listHosts") != "True" || F("run") != "-32001" || F("noTool") != "-32601") throw new Exception("app.call without app.control: " + f1.Text);
                    if (F("added").Length < 8 || F("updated") != "True" || F("removed") != "True" || F("badPort") != "-32602" || F("uiBad") != "-32602" || AppData.Current.Hosts.Count != hostsBefore)
                        throw new Exception("hosts / ui: " + f1.Text);
                    if (approvals.Count(t => t.Contains("host")) != 3) throw new Exception("host changes not each approved: " + string.Join(" | ", approvals));
                    // With app.control the change reaches the user's approval — declined here, so nothing runs.
                    Addons.AddonPackages.SetPermission(fa, "app.control", true);
                    approvals.Clear();
                    var f2 = await Addons.AddonHost.RunAsync(fa, "full", new Addons.AddonContext());
                    var fr2 = JsonDocument.Parse(f2.Text).RootElement;
                    if (!f2.Ok || fr2.GetProperty("run").ToString() != "-32003" || !approvals.Any(t => t.Contains("run a command"))) throw new Exception($"app.control: {f2.Text} · {string.Join(" | ", approvals)}");
                    if (!AuditLog.Read(DateTime.Now.ToString("yyyy-MM")).Any(x => x.Actor == "addon:com.example.selftest.full" && x.Action.Contains("run_command"))) throw new Exception("add-on app.call not audited");
                    Ok($"add-on full-control API over the encrypted bridge: app.capabilities lists {nMethods} bridge methods and {nTools} app capabilities with risk + permission; app.call runs any of them (a read with app.read; run_command refused without app.control, and with it reaches the user's approval, declined → -32003, nothing ran); hosts.add/update/remove each approved and validated; ui.open refuses unknown screens; every call audited as the add-on");
                }
                finally { Addons.AddonApi.Approver = approverWas; Addons.AddonPackages.Remove(fa); }
            }

            // revoke → denied on the next call; disable → refused
            Addons.AddonPackages.SetPermission(a, "hosts.read", false);
            var r2 = await Addons.AddonHost.RunAsync(a, "probe", new Addons.AddonContext(aHost));
            if (!r2.Ok || JsonDocument.Parse(r2.Text).RootElement.GetProperty("hostsError").ToString() != "-32001") throw new Exception("revoked permission still works: " + r2.Text);
            Addons.AddonPackages.SetEnabled(a, false);
            if ((await Addons.AddonHost.RunAsync(a, "probe", new Addons.AddonContext())).Status != "refused") throw new Exception("disabled add-on ran");
            Addons.AddonPackages.SetEnabled(a, true);

            // tamper → blocked + disabled
            File.AppendAllText(Path.Combine(a.Folder, "test.ps1"), "\n# tampered");
            var t1 = await Addons.AddonHost.RunAsync(a, "probe", new Addons.AddonContext());
            if (t1.Status != "blocked" || a.Enabled) throw new Exception("tampered add-on ran: " + t1.Status);

            // update: keeps old grants, NEW permission stays off unless approved; downgrade / same version refused
            var u1 = Addons.AddonPackages.Inspect(Pack("1.1.0", perms1.Append("notes.write").ToArray()));
            if (u1.Refusal != null || u1.NewPermissions.Count != 1 || u1.NewPermissions[0] != "notes.write") throw new Exception("update inspect: " + string.Join(",", u1.NewPermissions));
            a = Addons.AddonPackages.Install(u1, new[] { "hosts.read" });   // trying to sneak back a revoked one: ignored
            if (a.Granted.Contains("notes.write") || a.Granted.Contains("hosts.read") || !a.Granted.Contains("incidents.write") || !a.Enabled || a.Manifest.Version != "1.1.0") throw new Exception("update grants: " + string.Join(",", a.Granted));
            a = Addons.AddonPackages.Install(Addons.AddonPackages.Inspect(Pack("1.2.0", perms1.Append("notes.write").Append("notes.read").ToArray())), new[] { "notes.read" });
            if (!a.Granted.Contains("notes.read") || a.Granted.Contains("notes.write")) throw new Exception("second update grants: " + string.Join(",", a.Granted));
            if (Addons.AddonPackages.Inspect(Pack("1.0.0", perms1)).Refusal?.Contains("Downgrades") != true) throw new Exception("downgrade accepted");
            if (Addons.AddonPackages.Inspect(Pack("1.3.0", perms1, evilEntry: "../evil.ps1")).Refusal?.Contains("Unsafe path") != true) throw new Exception("zip slip accepted");
            if (Addons.AddonPackages.Inspect(Pack("1.4.0", perms1, permOverride: "vault.dump")).Refusal?.Contains("Unknown permission") != true) throw new Exception("unknown permission accepted");
            if (Directory.Exists(Path.Combine(Addons.AddonPackages.AddonsRoot, a.Id, "1.0.0"))) throw new Exception("old version folder left behind");

            // the repo's sample, if this build runs from the repository
            string? sampleDir = null;
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null && sampleDir == null; d = d.Parent)
                if (Directory.Exists(Path.Combine(d.FullName, "samples", "addons", "hello-hosts"))) sampleDir = Path.Combine(d.FullName, "samples", "addons", "hello-hosts");
            string sampleNote = "repo sample not found from this build location";
            if (sampleDir != null)
            {
                var samplePkg = Path.Combine(work, "sample.dcxaddon");
                System.IO.Compression.ZipFile.CreateFromDirectory(sampleDir, samplePkg);
                var si = Addons.AddonPackages.Inspect(samplePkg);
                if (si.Refusal != null) throw new Exception("sample refused: " + si.Refusal);
                var sa = Addons.AddonPackages.Install(si, si.Manifest.Permissions);
                var sr = await Addons.AddonHost.RunAsync(sa, "summary", new Addons.AddonContext());
                if (!sr.Ok || !sr.Text.Contains("saved host") || !inc.Items.Any(x => x.Title.StartsWith("Host inventory snapshot"))) throw new Exception($"sample run: {sr.Status} · {sr.Text} · {string.Join(" | ", sr.Log)}");
                Addons.AddonPackages.Remove(sa);
                sampleNote = "repo sample hello-hosts installed, ran (host summary → active incident) and removed";
            }

            // screen, then remove everything
            main.Navigate("addons"); await Settle(600);
            AddonsView.Current?.Refresh(); await Settle(300);
            var shots = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(reportPath)!, "shots")).FullName;
            Snap(main, Path.Combine(shots, "addons.png"));
            var folder = Path.Combine(Addons.AddonPackages.AddonsRoot, a.Id);
            Addons.AddonPackages.Remove(a);
            if (Directory.Exists(folder) || Addons.AddonPackages.Find("com.example.selftest") != null || Directory.Exists(Addons.AddonPackages.DataFolder("com.example.selftest"))) throw new Exception("remove");
            Incidents.IncidentStore.Delete(inc); Incidents.IncidentStore.EmptyTrash();
            Directory.Delete(work, true);
            Ok($"add-on lifecycle: revoke → -32001 next call, disabled → refused, tampered file → blocked + disabled, updates keep grants and NEW permissions stay off (a revoked one can't be sneaked back), downgrade / zip-slip / unknown permission refused, old version folder removed; {sampleNote}; rendered addons.png; removed cleanly");
        }
        catch (Exception ex) { Fail("add-ons", ex); }

        // ADR-013: embedded Remote Desktop — the REAL Windows RDP ActiveX in a tab, settings applied, password only in
        // memory, a refused connection reported cleanly through the event sink, pop-out/dock-back keeps the control.
        try
        {
            if (Rdp.RdpAxHost.AvailableClsid() is not { } clsid) Ok("embedded RDP: the Windows RDP control isn't registered on this PC — mstsc fallback (nothing to test)");
            else
            {
                var rc = new Credential { Name = "rdp-selftest", Kind = CredentialKind.Password, Username = "tester", Secret = "Rdp-Pa55-selftest-9f" };
                var rh = new HostEntry { Name = "rdp-selftest", Address = "127.0.0.1", Port = 1, Username = "tester", Protocol = "rdp", Auth = AuthMethod.Password, CredentialId = rc.Id };
                AppData.Current.Credentials.Add(rc); AppData.Current.Hosts.Add(rh); AppData.Save();
                main.OpenRdp(rh);
                var rv = main.Panes.AllTabs.Select(t => t.Content).OfType<RdpView>().LastOrDefault() ?? throw new Exception("no RDP tab (fell back to mstsc?)");
                async Task WaitState(string s, int seconds) { for (int i = 0; i < seconds * 4 && rv.State != s; i++) await Settle(250); }
                await WaitState("disconnected", 40);
                if (!rv.ControlCreated || rv.State != "disconnected" || !rv.Events.Contains(Rdp.RdpEvent.Disconnected)) throw new Exception($"refused connect: state {rv.State}, events {string.Join(",", rv.Events)}");
                if (File.Exists(Path.Combine(App.CacheDir, "rdp", rh.Id + ".rdp"))) throw new Exception("an .rdp file was written for the embedded session");
                var tab = main.Panes.AllTabs.First(t => t.Content == rv);
                main.Panes.PopOut(tab); await Settle(600);
                main.Panes.DockBack(tab); await Settle(600);
                if (!rv.ControlCreated) throw new Exception("control lost after pop-out / dock back");
                int before = rv.Events.Count(e => e == Rdp.RdpEvent.Disconnected);
                rv.Connect();
                for (int i = 0; i < 160 && rv.Events.Count(e => e == Rdp.RdpEvent.Disconnected) == before; i++) await Settle(250);
                if (rv.Events.Count(e => e == Rdp.RdpEvent.Disconnected) == before) throw new Exception("no second disconnect after reparenting — the control stopped working");
                main.Panes.Select(tab); await Settle(300);
                var shots = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(reportPath)!, "shots")).FullName;
                Snap(main, Path.Combine(shots, "rdp.png"));
                main.CloseTab(tab); await Settle(300);
                AppData.Current.Hosts.Remove(rh); AppData.Current.Credentials.Remove(rc); AppData.Save();
                Ok($"embedded RDP: real Windows RDP ActiveX ({clsid[..8]}…) in a tab, settings + in-memory password applied, refused connection reported via the event sink (events: {string.Join(",", rv.Events.Distinct())}), no .rdp file, pop-out + dock back keep a working control (reconnect after reparenting), closed cleanly; rendered rdp.png");
            }
        }
        catch (Exception ex) { Fail("embedded RDP", ex); }
    }
}
