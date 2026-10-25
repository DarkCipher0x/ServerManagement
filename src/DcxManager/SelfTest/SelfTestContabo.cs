using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DcxManager.Contabo;
using DcxManager.Services;
using DcxManager.Views;
using DcxManager.Vultr;

namespace DcxManager;

/// <summary>
/// ADR-020: the cloud provider layer and Contabo. Everything runs against <see cref="FakeContabo"/>, an in-process HTTP
/// handler that implements the token endpoint and the API routes from Contabo's spec — no network, no real credentials.
/// Covers sign-in, token reuse and renewal, pagination, normalisation, power actions and the status watch, orders,
/// cancellation, error parsing, rate limits, retries, timeouts, unavailability, malformed answers, permissions,
/// capabilities, the UI (screens, menus), host reconciliation, the dashboard, the AI tools, offline mode,
/// encrypted persistence and secret hygiene.
/// </summary>
public static class ContaboTests
{
    public static async Task RunAsync(MainWindow main, string reportDir, Action<string> ok, Action<string, Exception?> fail, Action<Window, string> snap)
    {
        async Task Settle(int ms = 200)
        {
            await Task.Delay(ms);
            await Application.Current.Dispatcher.InvokeAsync(() => main.UpdateLayout(), DispatcherPriority.ApplicationIdle);
        }
        var s = AppSettings.Current;
        var was = (Accounts: s.ContaboAccounts.ToList(), Active: s.ActiveContaboAccountId, Sources: s.DashboardAutoSources.ToList(), Checks: s.DashboardChecks);
        var sleepWas = CloudHttp.Sleep;
        var timeoutWas = ContaboClient.RequestTimeout;
        var watchWas = (CloudResourceView.WatchInterval, CloudResourceView.WatchChecks);
        var resolverWas = Offline.Resolver;
        bool offlineWas = Offline.On;
        var waits = new List<TimeSpan>();
        CloudHttp.Sleep = (d, ct) => { lock (waits) waits.Add(d); return Task.CompletedTask; };   // retries instant, but counted
        var fake = new FakeContabo();
        ContaboClient.UseHandler(fake);
        var a = new ContaboAccount { Name = "dcx-contabo-a", ClientId = FakeContabo.ClientA, ClientSecret = FakeContabo.SecretA, ApiUser = "ops-a@example.com", ApiPassword = FakeContabo.PasswordA };
        var b = new ContaboAccount { Name = "dcx-contabo-b", ClientId = FakeContabo.ClientB, ClientSecret = FakeContabo.SecretB, ApiUser = "ops-b@example.com", ApiPassword = FakeContabo.PasswordB };
        var addedHosts = new List<HostEntry>();
        var tabs = new List<TabItem>();
        string[] secrets = { FakeContabo.SecretA, FakeContabo.PasswordA, FakeContabo.SecretB, FakeContabo.PasswordB };
        try
        {
            s.ContaboAccounts = new() { a, b };
            s.ActiveContaboAccountId = a.Id;
            var p = ContaboProvider.Instance;
            // The fake forgets its tokens on reset; the client must too, or its first call is a (correct) 401 + sign-in.
            void FreshFake() { fake.Reset(); ContaboClient.Forget(a.Id); ContaboClient.Forget(b.Id); }

            // 1. Provider abstraction and capabilities.
            try
            {
                if (!CloudProviders.All.SequenceEqual(new CloudProvider[] { VultrProvider.Instance, p, Hetzner.HetznerProvider.Instance, Azure.AzureProvider.Instance, Gcp.GcpProvider.Instance }) || CloudProviders.Find("contabo") != p) throw new Exception("provider registry");
                if (p.Has(CloudCaps.Delete) || p.Has(CloudCaps.Resize) || p.Has(CloudCaps.Console) || !p.Has(CloudCaps.CancelContract | CloudCaps.Start | CloudCaps.Stop | CloudCaps.Reboot | CloudCaps.Rescue | CloudCaps.Snapshots | CloudCaps.Reinstall))
                    throw new Exception("Contabo capabilities");
                if (!VultrProvider.Instance.Has(CloudCaps.Delete | CloudCaps.Console)) throw new Exception("Vultr capabilities");
                if (ContaboCatalog.Instance.CanDelete || !ContaboCatalog.Instance.Actions.Any(x => x.Label.StartsWith("Cancel contract")) || ContaboCatalog.Instance.Actions.Any(x => x.Special == "console"))
                    throw new Exception("no delete / console controls on Contabo instances");
                foreach (var d in ContaboCatalog.All)
                {
                    if (!d.Path.StartsWith('/') || d.Key != "data") throw new Exception("catalog " + d.Title);
                    foreach (var x in d.Actions.Where(x => x.Special == null && x.Path.Length > 0)) if (!x.Path.StartsWith('/')) throw new Exception("action path " + x.Label);
                }
                if (ContaboProvider.StateOf("running") != CloudState.Running || ContaboProvider.StateOf("installing") != CloudState.Busy || ContaboProvider.StateOf("error") != CloudState.Error || ContaboProvider.StateOf("rescue") != CloudState.Other)
                    throw new Exception("state mapping");
                var row = JsonDocument.Parse(FakeContabo.InstanceJson(4242, "web-01", "stopped", "203.0.113.42").ToJsonString()).RootElement;
                var inst = p.Normalize(row, a.Id) ?? throw new Exception("normalize");
                if (inst is not { Id: "4242", Name: "web-01", Ipv4: "203.0.113.42", Region: "EU", Plan: "VPS 2", State: CloudState.Stopped, DefaultUser: "admin", Cpu: 4 } || inst.RamMb != 8192 || inst.Ipv6 != "2001:db8::242")
                    throw new Exception("normalized: " + inst);
                ok("cloud provider layer: Vultr + Contabo registered, capabilities declared (Contabo: no delete / resize / console; cancel contract instead), catalog paths from the spec, state mapping, instance normalisation");
            }
            catch (Exception ex) { fail("cloud provider layer", ex); }

            // 2. Sign-in, token reuse and renewal, headers, connection test, credential failures without leaks.
            try
            {
                FreshFake();
                var api = new ContaboClient(a);
                int n = await api.TestAsync();
                await api.TestAsync();
                if (n != fake.Instances(FakeContabo.ClientA).Count || fake.TokenCalls != 1) throw new Exception($"test {n} / sign-ins {fake.TokenCalls}");
                var form = fake.LastTokenForm!;
                if (form["grant_type"] != "password" || form["client_id"] != FakeContabo.ClientA || form["username"] != "ops-a@example.com" || form["client_secret"] != FakeContabo.SecretA || form["password"] != FakeContabo.PasswordA)
                    throw new Exception("token form");
                var c = fake.Calls.Last();
                if (!Regex.IsMatch(c.RequestId ?? "", "^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$") || c.Trace is not { Length: > 0 } || c.Auth != "Bearer " + fake.TokenFor(FakeContabo.ClientA))
                    throw new Exception($"headers: id {c.RequestId} trace {c.Trace} auth {c.Auth?[..10]}");
                if (fake.Calls.Select(x => x.RequestId).Distinct().Count() != fake.Calls.Count) throw new Exception("request ids reused");
                fake.RevokeTokens();   // expired / revoked: one 401, a fresh sign-in, the call repeated
                await api.TestAsync();
                if (fake.TokenCalls != 2) throw new Exception("token not renewed after 401: " + fake.TokenCalls);
                // Tokens are per account.
                await new ContaboClient(b).TestAsync();
                if (fake.TokenCalls != 3 || fake.Calls.Last().Auth != "Bearer " + fake.TokenFor(FakeContabo.ClientB)) throw new Exception("account isolation of tokens");

                var wrongPw = new ContaboAccount { Id = "x1", Name = "wrong", ClientId = FakeContabo.ClientA, ClientSecret = FakeContabo.SecretA, ApiUser = "ops-a@example.com", ApiPassword = "Wrong-Pa55word!" };
                var e1 = await Throws(() => new ContaboClient(wrongPw).TestAsync());
                var wrongClient = new ContaboAccount { Id = "x2", Name = "wrong2", ClientId = "nope", ClientSecret = "S3cret-nope!", ApiUser = "u", ApiPassword = "p" };
                var e2 = await Throws(() => new ContaboClient(wrongClient).TestAsync());
                var e3 = await Throws(() => new ContaboClient(new ContaboAccount { Id = "x3", Name = "empty" }).TestAsync());
                if (e1 is not CloudApiException { Status: 401 } || !e1.Message.Contains("API user or API password") || e2 is not CloudApiException { Status: 401 } || !e2.Message.Contains("client id or client secret")
                    || e3 is not CloudApiException { Status: 401 } || !e3.Message.Contains("missing part"))
                    throw new Exception($"credential errors: {e1?.Message} | {e2?.Message} | {e3?.Message}");
                if (new[] { e1!, e2!, e3! }.Any(e => e.Message.Contains("Wrong-Pa55word") || e.Message.Contains("S3cret-nope") || secrets.Any(x => e.Message.Contains(x)))) throw new Exception("secret in an error message");
                ok("Contabo sign-in: OAuth2 password grant with the four credentials, token reused (1 sign-in for 2 calls), renewed once after a 401, per-account tokens, UUIDv4 x-request-id per request + x-trace-id, Test connection = instance count; wrong password / client / missing values → clear 401 messages without any secret");
            }
            catch (Exception ex) { fail("Contabo sign-in", ex); }

            // 3. Pagination, rate limits, retries, timeouts, unavailable API, malformed answers, error parsing.
            try
            {
                FreshFake();
                fake.SeedMany(FakeContabo.ClientA, 250);
                var api = new ContaboClient(a);
                var all = await api.ListAsync("/compute/instances", "data");
                var pages = fake.Calls.Where(x => x.Path == "/v1/compute/instances").ToList();
                if (all.Count != 250 || pages.Count != 3 || pages.Select(x => x.Query).Count(q => q.Contains("size=100")) != 3 || pages.Select(x => x.Trace).Distinct().Count() != 1)
                    throw new Exception($"pagination: {all.Count} items in {pages.Count} pages");
                fake.SeedMany(FakeContabo.ClientA, 0);

                fake.Script("GET /v1/compute/instances", 429, "{\"message\":\"Too many requests\"}", retryAfter: 2);
                waits.Clear();
                await api.ListAsync("/compute/instances", "data");
                if (waits.Count != 1 || waits[0] != TimeSpan.FromSeconds(2)) throw new Exception("429 not retried after Retry-After: " + string.Join(",", waits));

                fake.Script("GET /v1/compute/instances", 503, "", times: 2);
                waits.Clear();
                await api.ListAsync("/compute/instances", "data");
                if (waits.Count != 2) throw new Exception("503 read not retried twice: " + waits.Count);

                fake.Script("GET /v1/compute/instances", 500, "{\"message\":\"internal\"}", times: 10);
                var e5 = await Throws(() => api.ListAsync("/compute/instances", "data"));
                if (e5 is not CloudApiException { Status: 500 } || !e5.Message.StartsWith("Contabo's API had a problem")) throw new Exception("500: " + e5?.Message);
                fake.ClearScripts();

                // A create that is rate-limited is NOT repeated (an order must never run twice).
                fake.Script("POST /v1/compute/instances", 429, "{\"message\":\"slow down\"}");
                int creates = fake.Calls.Count(x => x.Method == "POST" && x.Path == "/v1/compute/instances");
                var e6 = await Throws(() => api.SendAsync(HttpMethod.Post, "/compute/instances", new { productId = "V153", period = 1 }, idempotent: false));
                if (e6 is not CloudApiException { Status: 429 } || fake.Calls.Count(x => x.Method == "POST" && x.Path == "/v1/compute/instances") != creates + 1) throw new Exception("order repeated after 429");
                fake.ClearScripts();

                // Timeouts: reads retried, changes reported as uncertain and never repeated.
                ContaboClient.RequestTimeout = TimeSpan.FromMilliseconds(300);
                fake.DelayFor = "GET /v1/compute/instances"; fake.Delay = TimeSpan.FromSeconds(3);
                var e7 = await Throws(() => api.ListAsync("/compute/instances", "data"));
                int timedGets = fake.Calls.Count(x => x.Method == "GET" && x.Path == "/v1/compute/instances" && x.Delayed);
                fake.DelayFor = "POST /v1/compute/instances";
                var e8 = await Throws(() => api.SendAsync(HttpMethod.Post, "/compute/instances", new { productId = "V153", period = 1 }, idempotent: false));
                int timedPosts = fake.Calls.Count(x => x.Method == "POST" && x.Path == "/v1/compute/instances" && x.Delayed);
                fake.DelayFor = null;
                ContaboClient.RequestTimeout = timeoutWas;
                if (e7 is not CloudApiException { Status: 0, Uncertain: false } || timedGets != CloudHttp.MaxAttempts) throw new Exception($"read timeout: {e7?.Message} after {timedGets} attempt(s)");
                if (e8 is not CloudApiException { Status: 0, Uncertain: true } || timedPosts != 1 || !e8.Message.Contains("may or may not have been applied")) throw new Exception($"order timeout: {e8?.Message} after {timedPosts} attempt(s)");

                // Unavailable API / DNS: reads retried, then a plain message.
                fake.FailNetwork = true;
                waits.Clear();
                var e9 = await Throws(() => api.ListAsync("/compute/instances", "data"));
                fake.FailNetwork = false;
                if (e9 is not CloudApiException { Status: 0 } || !e9.Message.StartsWith("Couldn't reach Contabo") || waits.Count != CloudHttp.MaxAttempts - 1) throw new Exception("unavailable: " + e9?.Message);

                fake.Script("GET /v1/compute/instances", 200, "{ this is not json");
                var e10 = await Throws(() => api.ListAsync("/compute/instances", "data"));
                fake.Script("GET /v1/compute/instances", 200, "{\"unexpected\": true}");
                var e11 = await Throws(() => api.ListAsync("/compute/instances", "data"));
                fake.Script("POST /v1/compute/instances/1/actions/start", 403, "{\"message\":\"Missing permission compute.instances.actions.start\"}");
                var e12 = await Throws(() => api.SendAsync(HttpMethod.Post, "/compute/instances/1/actions/start", null));
                fake.Script("GET /v1/compute/instances/9", 404, "{\"errors\":[{\"message\":\"Entity not found\"}]}");
                var e13 = await Throws(() => api.SendAsync(HttpMethod.Get, "/compute/instances/9", null));
                fake.Script("PATCH /v1/compute/instances/1", 400, "{\"message\":[\"displayName must be shorter than or equal to 255 characters\"]}");
                var e14 = await Throws(() => api.SendAsync(HttpMethod.Patch, "/compute/instances/1", new { displayName = new string('x', 300) }));
                fake.ClearScripts();
                if (e10 is not CloudApiException { Message: var m10 } || !m10.Contains("isn't valid JSON")) throw new Exception("malformed: " + e10?.Message);
                if (e11 is not CloudApiException || !e11.Message.Contains("no “data” list")) throw new Exception("unexpected shape: " + e11?.Message);
                if (e12 is not CloudApiException { Status: 403, RequestId.Length: > 0 } || e12.Message != "This Contabo API user isn't allowed to do this: Missing permission compute.instances.actions.start") throw new Exception("403: " + e12?.Message);
                if (e13 is not CloudApiException { Status: 404 } || !e13.Message.EndsWith("Entity not found")) throw new Exception("404: " + e13?.Message);
                if (e14 is not CloudApiException { Status: 400 } || !e14.Message.Contains("shorter than")) throw new Exception("400: " + e14?.Message);
                ok("Contabo resilience: 250 instances over 3 pages (one trace id), 429 retried after Retry-After, 503 reads retried, persistent 500 reported, a rate-limited order NOT repeated, read timeouts retried (4 attempts) but a timed-out order reported as uncertain after 1 attempt, unreachable API retried then a plain message, malformed / unexpected answers, 400 / 403 / 404 messages parsed (message, list, errors[]), request id kept for support");
            }
            catch (Exception ex) { fail("Contabo resilience", ex); }
            finally { ContaboClient.RequestTimeout = timeoutWas; fake.DelayFor = null; fake.FailNetwork = false; fake.ClearScripts(); }

            // 4. Actions through the provider: power, create (order), rename, cancel; audit without secrets.
            try
            {
                FreshFake();
                var api = p.ApiFor(a);
                var list = await p.ListInstancesAsync(api);
                if (list.Count != 3 || list.Count(i => i.State == CloudState.Running) != 2) throw new Exception("instances: " + list.Count);
                var stopped = list.First(i => i.State == CloudState.Stopped);
                await p.PowerAsync(api, stopped.Id, CloudPower.Start);
                await p.PowerAsync(api, list[0].Id, CloudPower.Reboot);
                await p.PowerAsync(api, list[0].Id, CloudPower.Stop);
                var acts = fake.Calls.Where(x => x.Method == "POST" && x.Path.Contains("/actions/")).Select(x => x.Path).ToList();
                if (!acts.SequenceEqual(new[] { $"/v1/compute/instances/{stopped.Id}/actions/start", $"/v1/compute/instances/{list[0].Id}/actions/restart", $"/v1/compute/instances/{list[0].Id}/actions/stop" }))
                    throw new Exception("power paths: " + string.Join(", ", acts));
                var created = await api.SendAsync(HttpMethod.Post, "/compute/instances", JsonSerializer.Serialize(new { productId = "V153", region = "EU", period = 1, displayName = "dcx-new" }), idempotent: false);
                var newId = J.Str(api.UnwrapItem(created), "instanceId");
                if (newId.Length == 0 || fake.Instances(FakeContabo.ClientA).Count != 4) throw new Exception("create");
                await api.SendAsync(HttpMethod.Patch, $"/compute/instances/{newId}", new { displayName = "dcx-renamed" });
                await api.SendAsync(HttpMethod.Post, $"/compute/instances/{newId}/cancel", new { });
                var after = (await p.ListInstancesAsync(api)).First(i => i.Id == newId);
                if (after.Name != "dcx-renamed" || J.Str(after.Raw, "cancelDate").Length == 0) throw new Exception("rename / cancel");
                var audit = AuditLog.Read(DateTime.Now.ToString("yyyy-MM"));
                var mine = audit.Where(x => x.Target == "contabo:dcx-contabo-a").ToList();
                if (!mine.Any(x => x.Action == $"POST /compute/instances/{stopped.Id}/actions/start" && x.Result.Contains("request ")) || !mine.Any(x => x.Action == "POST /compute/instances" && x.Result.StartsWith("201")))
                    throw new Exception("audit trail: " + string.Join(" | ", mine.Take(5).Select(x => x.Action + " → " + x.Result)));
                ok("Contabo actions: neutral power start/restart/stop → actions/start|restart|stop, order (POST, not repeatable) → new instance, rename (PATCH), cancel contract → cancelDate; every change in the audit log with status, time and request id");
            }
            catch (Exception ex) { fail("Contabo actions", ex); }

            // 5. The UI: sidebar, screens, header picker, state-aware actions, status watch, menus, multiple accounts.
            try
            {
                FreshFake();
                main.BuildNav();
                main.Navigate("contabo:Instances");
                await Settle(800);
                var tab = main.Panes.ActiveTab ?? throw new Exception("no tab");
                tabs.Add(tab);
                // Since 0.1.11 cloud screens open inside the Cloud providers workspace (ADR-028).
                var view = (tab.Content as CloudWorkspaceView)?.CurrentView as CloudResourceView ?? throw new Exception("not a cloud view: " + tab.Content?.GetType().Name);
                await view.LoadAsync();   // the screen may have been opened earlier (every-screen test) before an account existed
                for (int i = 0; i < 40 && view.Rows.Count == 0; i++) await Settle(100);
                if (view.Rows.Count != 3 || view.Provider != p) throw new Exception("rows: " + view.Rows.Count + " " + view.StatusText);
                if (main.FindName("AccountBox") != null) throw new Exception("the header still has a cloud account picker (removed in 0.3.0)");
                if (view.HasDeleteButton) throw new Exception("a Delete button on Contabo instances");
                var stoppedRow = view.Rows.First(r => r["status"] == "stopped");
                view.SelectId(stoppedRow["instanceId"]);
                await Settle(150);
                var start = ContaboCatalog.Instance.Actions.First(x => x.Label == "▶ Start");
                var stop = ContaboCatalog.Instance.Actions.First(x => x.Label == "■ Stop");
                if (view.ButtonFor("▶ Start") is not { IsEnabled: true } || view.ButtonFor("■ Stop") is not { IsEnabled: false } sb || !(sb.ToolTip as string ?? "").Contains("stopped"))
                    throw new Exception("state-aware buttons");
                var menu = view.TestRowMenu().OfType<MenuItem>().Select(m => m.Header as string ?? "").ToList();
                if (!menu.Any(h => h.StartsWith("Copy IPv4 (203.0.113.")) || !menu.Contains("Copy ssh command")) throw new Exception("row menu: " + string.Join(" | ", menu));
                CloudResourceView.WatchInterval = TimeSpan.FromMilliseconds(50); CloudResourceView.WatchChecks = 40;
                var run = view.RunActionAsync(start);
                await Task.Delay(20);
                if (!view.IsBusy(stoppedRow)) throw new Exception("not marked busy during the action");
                await run;
                var now = view.Rows.First(r => r["instanceId"] == stoppedRow["instanceId"]);
                if (now["status"] != "running" || view.IsBusy(now) || !view.StatusText.Contains("is running")) throw new Exception("watch: " + now["status"] + " / " + view.StatusText);
                if (fake.Calls.Count(x => x.Method == "GET" && x.Path == $"/v1/compute/instances/{stoppedRow["instanceId"]}") < 2) throw new Exception("no status polling");
                var shots = Directory.CreateDirectory(Path.Combine(reportDir, "shots")).FullName;
                snap(main, Path.Combine(shots, "contabo-instances.png"));

                // Account B: switching accounts (the Cloud providers tree's Switch account does this), the screen reloads with B's instances (tenant isolation).
                p.SetActive(b.Id);
                await Settle(600);
                for (int i = 0; i < 40 && view.Rows.Count != 1; i++) await Settle(100);
                if (view.Rows.Count != 1 || view.Rows[0]["displayName"] != "b-only") throw new Exception("account B rows: " + view.Rows.Count);
                b.Disabled = true;
                if (p.ActiveAccount?.Id != a.Id) throw new Exception("disabled account still active");
                b.Disabled = false;
                p.SetActive(a.Id);
                await Settle(400);

                // Every Contabo screen builds and loads against the fake.
                foreach (var d in ContaboCatalog.All.Where(d => d.Group != "Reference"))
                {
                    main.Navigate(p.NavKey(d)); await Settle(300);
                    if (main.Panes.ActiveTab is { } t && !tabs.Contains(t)) tabs.Add(t);
                }
                main.Navigate("contabo:reference"); await Settle(300);
                if (main.Panes.ActiveTab is { } rt && !tabs.Contains(rt)) tabs.Add(rt);
                ok("Contabo UI: Instances screen inside the Cloud providers workspace loads through the shared engine, no account picker in the header (removed in 0.3.0), no Delete button, Start enabled / Stop disabled with the reason on a stopped server, copy IP / ssh menu, busy while running, status watched to running; account switch reloads with the other account's servers; disabled accounts skipped; every Contabo screen opens; rendered contabo-instances.png");
            }
            catch (Exception ex) { fail("Contabo UI", ex); }
            finally { CloudResourceView.WatchInterval = watchWas.WatchInterval; CloudResourceView.WatchChecks = watchWas.WatchChecks; }

            // 6. Hosts: discovery / reconciliation, server view, AI tools, dashboard (a failing provider stays contained).
            try
            {
                FreshFake();
                var api = p.ApiFor(a);
                var list = await p.ListInstancesAsync(api);
                var r1 = HostsView.Reconcile(p, a.Id, list, null);
                addedHosts.AddRange(AppData.Current.Hosts.Where(h => h.CloudProvider == "contabo"));
                if (r1.Added != 3 || addedHosts.Count != 3 || addedHosts.Any(h => h.CloudAccountId != a.Id || h.Username != "admin")) throw new Exception($"import: {r1.Added}");
                fake.Instances(FakeContabo.ClientA)[0]["ipConfig"]!["v4"]!["ip"] = "203.0.113.99";
                fake.Instances(FakeContabo.ClientA).RemoveAt(2);
                var r2 = HostsView.Reconcile(p, a.Id, await p.ListInstancesAsync(api), null);
                if (r2.Added != 0 || r2.Updated != 2 || r2.Gone.Count != 1 || !addedHosts.Any(h => h.Address == "203.0.113.99")) throw new Exception($"reconcile: +{r2.Added} ~{r2.Updated} gone {r2.Gone.Count}");

                // 0.6.24: a server destroyed and rebuilt under the same name (new id, new IP) moves its saved host over instead
                // of leaving it on the old address; two candidates of that name are ambiguous, so the host stays reported.
                var rebuilt = r2.Gone[0];
                var twin = FakeContabo.InstanceJson(900, rebuilt.Name, "running", "203.0.113.150");
                fake.Instances(FakeContabo.ClientA).Add(twin);
                var s1 = HostsView.SyncLinked(p, a.Id, await p.ListInstancesAsync(api));
                if (s1.Relinked.SingleOrDefault() != rebuilt || rebuilt.Address != "203.0.113.150" || rebuilt.CloudInstanceId != "900" || s1.Gone.Count != 0 || s1.Moved.Count != 1)
                    throw new Exception($"rebuilt server: relinked {s1.Relinked.Count}, {rebuilt.Address}, id {rebuilt.CloudInstanceId}, gone {s1.Gone.Count}");
                var r3 = HostsView.Reconcile(p, a.Id, await p.ListInstancesAsync(api), null);
                if (r3.Added != 0 || AppData.Current.Hosts.Count(h => h.Name == rebuilt.Name) != 1) throw new Exception("rebuilt server imported twice");
                fake.Instances(FakeContabo.ClientA).Remove(twin);
                fake.Instances(FakeContabo.ClientA).Add(FakeContabo.InstanceJson(901, rebuilt.Name, "running", "203.0.113.151"));
                fake.Instances(FakeContabo.ClientA).Add(FakeContabo.InstanceJson(902, rebuilt.Name, "running", "203.0.113.152"));
                var s2 = HostsView.SyncLinked(p, a.Id, await p.ListInstancesAsync(api));
                if (s2.Relinked.Count != 0 || s2.Gone.SingleOrDefault() != rebuilt || rebuilt.Address != "203.0.113.150") throw new Exception("two servers of the same name: relinked anyway");
                fake.Instances(FakeContabo.ClientA).RemoveAll(i => (long)i["instanceId"]! >= 901);

                var linked = addedHosts.First(h => h.Address == "203.0.113.99");
                var sv = new ServerView(linked);
                var labels = Menus.Descendants<Button>(sv).Select(x => x.Content as string ?? "").ToList();
                if (!labels.Contains("⚡ Contabo power cycle") || labels.Any(l => l.Contains("console", StringComparison.OrdinalIgnoreCase))) throw new Exception("server view buttons: " + string.Join(", ", labels));
                sv.Dispose();

                var ai = new Ai.Assistant();
                var json = await ai.TestToolAsync("cloud_list_instances", new JsonObject { ["provider"] = "contabo" });
                if (!json.Contains("\"provider\":\"contabo\"") || JsonDocument.Parse(json).RootElement.GetArrayLength() != 2) throw new Exception("AI list: " + json);
                ai.Approve = (_, _, _) => Task.FromResult(false);
                var declined = await ai.TestToolAsync("cloud_instance_action", new JsonObject { ["provider"] = "contabo", ["instance_id"] = list[0].Id, ["action"] = "reboot" });
                int restarts = fake.Calls.Count(x => x.Path.EndsWith("/actions/restart"));
                string? approvalTitle = null;
                ai.Approve = (t, _, _) => { approvalTitle = t; return Task.FromResult(true); };
                var done = await ai.TestToolAsync("cloud_instance_action", new JsonObject { ["provider"] = "contabo", ["instance_id"] = list[0].Id, ["action"] = "reboot" });
                if (declined != "User declined." || fake.Calls.Count(x => x.Path.EndsWith("/actions/restart")) != restarts + 1 || approvalTitle != "AI wants to reboot a Contabo instance" || !done.Contains("requested"))
                    throw new Exception("AI action: " + declined + " / " + done);
                var refuse = await ai.TestToolAsync("cloud_instance_action", new JsonObject { ["provider"] = "nimbus", ["instance_id"] = "1", ["action"] = "reboot" });
                if (!refuse.StartsWith("ERROR")) throw new Exception("unknown provider accepted");

                main.Navigate("dashboard"); await Settle(400);
                var dv = DashboardView.Current ?? throw new Exception("no dashboard");
                s.DashboardAutoSources = new() { "contabo" }; s.DashboardChecks = "open";
                for (int i = 0; i < 100 && dv.Checking; i++) await Task.Delay(100);   // a check started by opening the dashboard
                await dv.RunChecksAsync("manual", only: "contabo");
                if (!dv.CloudCounts.TryGetValue("contabo", out var cnt) || cnt != 2) throw new Exception("dashboard count: " + cnt);
                fake.Script("GET /v1/compute/instances", 503, "", times: 10);
                await dv.RunChecksAsync("manual", only: "contabo");
                fake.ClearScripts();
                await Settle(400);
                if (dv.CloudCounts["contabo"] != -1 || !dv.BodyText("cloud").Contains("Contabo's API had a problem") || !dv.BodyText("sessions").Any()) throw new Exception("failing provider: " + dv.BodyText("cloud"));
                ok("Contabo hosts / dashboard / AI: import adds linked hosts (Contabo's default user), reconciliation follows a changed IP and reports a removed instance (kept, not deleted), a server rebuilt under the same name takes its host over (not when the name is ambiguous), server view offers the Contabo power cycle and no console, AI cloud tools list and reboot only after approval (declined = nothing sent, unknown provider refused), dashboard counts Contabo servers and a failing Contabo API shows only in its own card");
            }
            catch (Exception ex) { fail("Contabo hosts / dashboard / AI", ex); }
            finally { s.DashboardAutoSources = was.Sources; s.DashboardChecks = was.Checks; }

            // 7. Offline mode, persistence across restart (encrypted), secret hygiene in logs.
            try
            {
                ContaboClient.UseHandler(null);   // the real, offline-checked transport
                Offline.Resolver = (h, ct) => Task.FromResult(new[] { IPAddress.Parse("203.0.113.80") });   // public: never contacted
                Offline.Set(true);
                var blocked = await Throws(() => new ContaboClient(a).TestAsync());
                if (!offlineWas) Offline.Set(false);
                if (blocked is not OfflineBlockedException) throw new Exception("offline: " + blocked?.GetType().Name + " " + blocked?.Message);
                ContaboClient.UseHandler(fake);

                AppSettings.Save();
                var raw = File.ReadAllBytes(Path.Combine(SecureStore.DataDir, "settings.dcx"));
                var text = Encoding.UTF8.GetString(raw);
                if (secrets.Any(x => text.Contains(x)) || text.Contains("dcx-contabo-a")) throw new Exception("credentials readable in settings.dcx");
                var reloaded = SecureStore.Read<AppSettings>("settings.dcx");
                if (reloaded.ContaboAccounts.FirstOrDefault(x => x.Id == a.Id) is not { ClientSecret: FakeContabo.SecretA, ApiPassword: FakeContabo.PasswordA } || reloaded.ActiveContaboAccountId != a.Id)
                    throw new Exception("accounts didn't survive a reload");
                var logs = App.ReadLogs(1);
                var audit = string.Join("\n", AuditLog.Read(DateTime.Now.ToString("yyyy-MM")).Select(x => x.Action + " " + x.Result));
                if (secrets.Any(x => logs.Contains(x) || audit.Contains(x)) || logs.Contains("Bearer ") || audit.Contains("Bearer ") || logs.Contains("tok-")) throw new Exception("a secret or token reached a log");
                ok("Contabo security: offline mode refuses Contabo before connecting (OfflineBlockedException), accounts saved encrypted and restored after a reload (no credential readable on disk), no secret, token or Authorization header in the error log or the audit log");
            }
            catch (Exception ex) { fail("Contabo security", ex); }
            finally { Offline.Resolver = resolverWas; if (!offlineWas && Offline.On) Offline.Set(false); }
        }
        finally
        {
            foreach (var t in tabs) { try { main.CloseTab(t); } catch { } }
            AppData.Current.Hosts.RemoveAll(h => addedHosts.Contains(h));
            AppData.Save();
            CloudHttp.Sleep = sleepWas;
            ContaboClient.UseHandler(null);
            ContaboClient.RequestTimeout = timeoutWas;
            s.ContaboAccounts = was.Accounts; s.ActiveContaboAccountId = was.Active;
            try { AppSettings.Save(); } catch { }
        }
    }

    private static async Task<Exception?> Throws(Func<Task> f)
    {
        try { await f(); return null; } catch (Exception ex) { return ex; }
    }

    /// <summary>
    /// An in-process Contabo: the token endpoint (OAuth2 password grant) and the API routes the app uses, per the official
    /// spec (data arrays, _pagination, instanceStatus values). Scripts inject errors, delays and network failures.
    /// Credentials are test values; nothing leaves the process.
    /// </summary>
    internal sealed class FakeContabo : HttpMessageHandler
    {
        public const string ClientA = "DCX-TEST-client-a", SecretA = "Sx-Test-secret-A-7731", PasswordA = "Pw-Test-A-7731!";
        public const string ClientB = "DCX-TEST-client-b", SecretB = "Sx-Test-secret-B-4410", PasswordB = "Pw-Test-B-4410!";
        private static readonly Dictionary<string, (string Secret, string User, string Password)> Creds = new()
        {
            [ClientA] = (SecretA, "ops-a@example.com", PasswordA),
            [ClientB] = (SecretB, "ops-b@example.com", PasswordB),
        };

        public sealed record Call(string Method, string Path, string Query, string? RequestId, string? Trace, string? Auth, bool Delayed);
        public List<Call> Calls { get; } = new();
        public int TokenCalls { get; private set; }
        public Dictionary<string, string>? LastTokenForm { get; private set; }
        public string? DelayFor { get; set; }
        public TimeSpan Delay { get; set; }
        public bool FailNetwork { get; set; }

        private readonly Dictionary<string, string> _tokens = new();   // token → client id
        private readonly Dictionary<string, List<JsonObject>> _instances = new();
        private readonly Dictionary<string, int> _pending = new();      // instance id → GETs until the target state
        private readonly Dictionary<string, string> _target = new();
        private readonly List<(string Key, int Status, string Body, int? RetryAfter, int Times)> _scripts = new();
        private int _seq, _nextId = 9000;

        public FakeContabo() => Reset();

        public void Reset()
        {
            lock (this)
            {
                Calls.Clear(); TokenCalls = 0; _tokens.Clear(); _scripts.Clear(); _pending.Clear(); _target.Clear();
                _instances[ClientA] = new() { InstanceJson(201, "web-01", "running", "203.0.113.11"), InstanceJson(202, "db-01", "running", "203.0.113.12"), InstanceJson(203, "lab-01", "stopped", "203.0.113.13") };
                _instances[ClientB] = new() { InstanceJson(301, "b-only", "running", "198.51.100.31") };
            }
        }

        public List<JsonObject> Instances(string client) => _instances[client];
        public string TokenFor(string client) { lock (this) return _tokens.Last(t => t.Value == client).Key; }
        public void RevokeTokens() { lock (this) _tokens.Clear(); }
        public void SeedMany(string client, int n)
        {
            lock (this)
            {
                if (n == 0) { Reset(); return; }
                _instances[client] = Enumerable.Range(1, n).Select(i => InstanceJson(10000 + i, $"bulk-{i:000}", "running", $"203.0.113.{i % 250 + 1}")).ToList();
            }
        }
        public void Script(string key, int status, string body, int? retryAfter = null, int times = 1) { lock (this) _scripts.Add((key, status, body, retryAfter, times)); }
        public void ClearScripts() { lock (this) _scripts.Clear(); }

        public static JsonObject InstanceJson(long id, string name, string status, string ip) => new()
        {
            ["tenantId"] = "DE", ["customerId"] = "54321", ["instanceId"] = id, ["name"] = $"vmd{id}", ["displayName"] = name, ["dataCenter"] = "European Union 1",
            ["region"] = "EU", ["regionName"] = "European Union", ["productId"] = "V92", ["productName"] = "VPS 2", ["productType"] = "ssd", ["imageId"] = "afecbb85-e2fc-46f0-9684-b46b1faf00bb",
            ["ipConfig"] = new JsonObject { ["v4"] = new JsonObject { ["ip"] = ip, ["netmaskCidr"] = 22, ["gateway"] = "203.0.113.1" }, ["v6"] = new JsonObject { ["ip"] = "2001:db8::" + (id % 1000), ["netmaskCidr"] = 64, ["gateway"] = "fe80::1" } },
            ["macAddress"] = "F2:65:50:F3:63:A1", ["ramMb"] = 8192, ["cpuCores"] = 4, ["osType"] = "Linux", ["diskMb"] = 102400, ["sshKeys"] = new JsonArray(),
            ["createdDate"] = "2026-01-02T03:04:05.000Z", ["cancelDate"] = "", ["status"] = status, ["vHostId"] = 1, ["vHostNumber"] = 1, ["vHostName"] = "m1", ["addOns"] = new JsonArray(),
            ["productType"] = "ssd", ["defaultUser"] = "admin", ["additionalIps"] = new JsonArray(), ["applicationId"] = "",
        };

        private static HttpResponseMessage Json(int status, string body, int? retryAfter = null)
        {
            var r = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (retryAfter is { } s) r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(s));
            return r;
        }

        private static string Page(IEnumerable<JsonNode?> items, int page, int size, int total) =>
            new JsonObject
            {
                ["_pagination"] = new JsonObject { ["size"] = size, ["totalElements"] = total, ["totalPages"] = Math.Max(1, (int)Math.Ceiling(total / (double)size)), ["page"] = page },
                ["data"] = new JsonArray(items.Select(i => i?.DeepClone()).ToArray()),
                ["_links"] = new JsonObject { ["self"] = "/v1/x" },
            }.ToJsonString();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var uri = req.RequestUri!;
            var key = $"{req.Method} {uri.AbsolutePath}";
            bool delayed = DelayFor == key;
            lock (this)
                Calls.Add(new Call(req.Method.Method, uri.AbsolutePath, uri.Query, req.Headers.TryGetValues("x-request-id", out var ids) ? ids.First() : null,
                    req.Headers.TryGetValues("x-trace-id", out var tr) ? tr.First() : null, req.Headers.Authorization?.ToString(), delayed));
            if (FailNetwork) throw new HttpRequestException("No such host is known. (api.contabo.com:443)", new System.Net.Sockets.SocketException(11001));
            if (delayed) await Task.Delay(Delay, ct);

            if (uri.Host == "auth.contabo.com")
            {
                var form = (await req.Content!.ReadAsStringAsync(ct)).Split('&').Select(kv => kv.Split('=', 2)).ToDictionary(kv => Uri.UnescapeDataString(kv[0]), kv => Uri.UnescapeDataString(kv.Length > 1 ? kv[1].Replace('+', ' ') : ""));
                lock (this) { TokenCalls++; LastTokenForm = form; }
                if (!Creds.TryGetValue(form.GetValueOrDefault("client_id") ?? "", out var c) || c.Secret != form.GetValueOrDefault("client_secret"))
                    return Json(401, "{\"error\":\"invalid_client\",\"error_description\":\"Invalid client credentials\"}");
                if (c.User != form.GetValueOrDefault("username") || c.Password != form.GetValueOrDefault("password"))
                    return Json(401, "{\"error\":\"invalid_grant\",\"error_description\":\"Invalid user credentials\"}");
                string token;
                lock (this) { token = $"tok-{++_seq}-{Guid.NewGuid():N}"; _tokens[token] = form["client_id"]; }
                return Json(200, $"{{\"access_token\":\"{token}\",\"expires_in\":300,\"token_type\":\"Bearer\"}}");
            }

            string? client;
            lock (this) client = req.Headers.Authorization is { Scheme: "Bearer", Parameter: { } t } && _tokens.TryGetValue(t, out var cl) ? cl : null;
            if (client == null) return Json(401, "{\"message\":\"Unauthorized\"}");
            lock (this)
            {
                var si = _scripts.FindIndex(x => x.Key == key);
                if (si >= 0)
                {
                    var sc = _scripts[si];
                    if (sc.Times <= 1) _scripts.RemoveAt(si); else _scripts[si] = sc with { Times = sc.Times - 1 };
                    return Json(sc.Status, sc.Body, sc.RetryAfter);
                }
            }
            var q = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var path = uri.AbsolutePath;
            var list = _instances[client];
            JsonObject? Find(string id) { lock (this) return list.FirstOrDefault(i => i["instanceId"]!.ToString() == id); }
            var m = Regex.Match(path, @"^/v1/compute/instances/(\d+)(?:/actions/(\w+)|/(cancel))?$");
            if (req.Method == HttpMethod.Get && path == "/v1/compute/instances")
            {
                int page = int.TryParse(q["page"], out var pg) ? pg : 1, size = int.TryParse(q["size"], out var sz) ? sz : 10;
                lock (this) return Json(200, Page(list.Skip((page - 1) * size).Take(size), page, size, list.Count));
            }
            if (req.Method == HttpMethod.Post && path == "/v1/compute/instances")
            {
                var body = JsonNode.Parse(await req.Content!.ReadAsStringAsync(ct))!.AsObject();
                JsonObject inst;
                lock (this) { inst = InstanceJson(++_nextId, body["displayName"]?.ToString() ?? "", "provisioning", "203.0.113.200"); list.Add(inst); }
                return Json(201, $"{{\"data\":[{{\"tenantId\":\"DE\",\"customerId\":\"54321\",\"instanceId\":{_nextId},\"createdDate\":\"2026-10-18T00:00:00Z\",\"imageId\":\"x\",\"productId\":\"{body["productId"]}\",\"region\":\"EU\",\"addOns\":[],\"osType\":\"Linux\",\"status\":\"provisioning\",\"sshKeys\":[]}}],\"_links\":{{\"self\":\"/v1/compute/instances/{_nextId}\"}}}}");
            }
            if (m.Success && Find(m.Groups[1].Value) is { } one)
            {
                var id = m.Groups[1].Value;
                if (req.Method == HttpMethod.Get && !m.Groups[2].Success && !m.Groups[3].Success)
                {
                    lock (this)
                        if (_pending.TryGetValue(id, out var left))
                        {
                            if (left <= 0) { one["status"] = _target[id]; _pending.Remove(id); }
                            else _pending[id] = left - 1;
                        }
                    return Json(200, Page(new[] { one }, 1, 1, 1));
                }
                if (req.Method == HttpMethod.Patch)
                {
                    var body = JsonNode.Parse(await req.Content!.ReadAsStringAsync(ct))!.AsObject();
                    lock (this) one["displayName"] = body["displayName"]?.ToString();
                    return Json(200, $"{{\"data\":[{{\"instanceId\":{id}}}],\"_links\":{{\"self\":\"x\"}}}}");
                }
                if (req.Method == HttpMethod.Post && m.Groups[3].Success)
                {
                    lock (this) one["cancelDate"] = "2026-11-30";
                    return Json(200, $"{{\"data\":[{{\"instanceId\":{id},\"cancelDate\":\"2026-11-30\"}}],\"_links\":{{\"self\":\"x\"}}}}");
                }
                if (req.Method == HttpMethod.Post && m.Groups[2].Success)
                {
                    var action = m.Groups[2].Value;
                    var target = action switch { "start" or "restart" => "running", "stop" or "shutdown" => "stopped", "rescue" => "rescue", _ => null };
                    if (target == null) return Json(404, "{\"message\":\"Unknown action\"}");
                    lock (this) { one["status"] = action == "restart" ? "running" : "provisioning"; _pending[id] = 1; _target[id] = target; }
                    return Json(201, $"{{\"data\":[{{\"tenantId\":\"DE\",\"customerId\":\"54321\",\"instanceId\":{id},\"action\":\"{action}\"}}],\"_links\":{{\"self\":\"x\"}}}}");
                }
            }
            if (req.Method == HttpMethod.Get && Regex.IsMatch(path, @"^/v1/compute/instances/\d+/snapshots$")) return Json(200, Page(Array.Empty<JsonNode>(), 1, 100, 0));
            if (req.Method == HttpMethod.Get && path is "/v1/compute/images" or "/v1/secrets" or "/v1/private-networks" or "/v1/firewalls" or "/v1/dns/zones" or "/v1/data-centers" or "/v1/compute/instances/actions/audits"
                    or "/v1/object-storages" or "/v1/vips" or "/v1/dns/ptrs" or "/v1/tags")
                return Json(200, Page(Array.Empty<JsonNode>(), 1, 100, 0));
            return Json(404, "{\"message\":\"Not found\"}");
        }
    }
}
