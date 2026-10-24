using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using DcxManager.Ai;
using DcxManager.Azure;
using DcxManager.Gcp;
using DcxManager.Services;
using DcxManager.Views;

namespace DcxManager;

/// <summary>
/// 0.2.0: Microsoft Azure and Google Cloud against in-process fakes of their sign-in endpoints and APIs (Entra ID's
/// token endpoint verifying the certificate assertion; Resource Manager with nextLink pages and async operations;
/// Google's token endpoint verifying the RS256 grant and its scope; Compute aggregated lists and operations; Cloud Run
/// long-running operations by project number; Resource Manager). The certificate and the key are made in memory for the
/// run. Nothing contacts the network; no window or dialog is shown.
/// </summary>
public static class AzureGcpTests
{
    private static readonly string[] SecretPaths = { "listkeys", "secrets", "retrievebootdiagnostics", "publishxml", "listcredentials", "connectionstrings", "listsecrets", "serialconsole" };

    public static async Task RunAsync(MainWindow main, string reportDir, Action<string> ok, Action<string, Exception?> fail, Action<Window, string> snap)
    {
        async Task Settle(int ms = 200)
        {
            await Task.Delay(ms);
            await Application.Current.Dispatcher.InvokeAsync(() => main.UpdateLayout(), DispatcherPriority.ApplicationIdle);
        }
        static async Task<Exception?> Throws(Func<Task> f) { try { await f(); return null; } catch (Exception ex) { return ex; } }
        var shots = Directory.CreateDirectory(Path.Combine(reportDir, "shots")).FullName;
        var s = AppSettings.Current;
        var sleepWas = CloudHttp.Sleep;
        var azureWas = (s.AzureAccounts.ToList(), s.ActiveAzureAccountId);
        var gcpWas = (s.GcpAccounts.ToList(), s.ActiveGcpAccountId);
        var pollWas = (AzureClient.OperationPoll, GcpClient.OperationPoll);
        CloudHttp.Sleep = (_, _) => Task.CompletedTask;
        AzureClient.OperationPoll = TimeSpan.FromMilliseconds(1);
        GcpClient.OperationPoll = TimeSpan.FromMilliseconds(1);
        var tabs = new List<System.Windows.Controls.TabItem>();

        // ---- Azure ------------------------------------------------------------------------------------------------------
        using var rsa = RSA.Create(2048);
        using var cert = new CertificateRequest("CN=dcx-selftest-azure", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        using var otherRsa = RSA.Create(2048);
        using var otherCert = new CertificateRequest("CN=dcx-selftest-other", otherRsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        const string pfxPassword = "Pfx-Selftest-Pw-0042";
        var pfx = Convert.ToBase64String(cert.Export(X509ContentType.Pfx, pfxPassword));
        var fakeAz = new FakeAzure(cert);
        AzureClient.UseHandler(fakeAz);
        var az = new AzureAccount { Name = "dcx-azure-lab", TenantId = FakeAzure.Tenant, ClientId = FakeAzure.Client, SubscriptionId = FakeAzure.Sub, CertificatePfx = pfx, CertificatePassword = pfxPassword, CertificateSha256 = AzureAuth.Sha256Hex(cert), ReadOnly = false };
        var azRo = new AzureAccount { Name = "dcx-azure-readonly", TenantId = FakeAzure.Tenant, ClientId = FakeAzure.Client, SubscriptionId = FakeAzure.Sub, CertificatePfx = pfx, CertificatePassword = pfxPassword, ReadOnly = true };
        try
        {
            var p = AzureProvider.Instance;
            s.AzureAccounts = new() { az, azRo };
            s.ActiveAzureAccountId = az.Id;

            // 1. Registration, capabilities, catalog rules, normalisation, templates.
            try
            {
                if (CloudProviders.Find("azure") != p || p.Has(CloudCaps.Console) || !p.Has(CloudCaps.Start | CloudCaps.Stop | CloudCaps.Reboot | CloudCaps.Resize | CloudCaps.Firewalls | CloudCaps.Dns)) throw new Exception("registration / capabilities");
                int ops = 0;
                void Def(ResourceDef d, bool child)
                {
                    var paths = new List<string> { d.Path };
                    // The item path is only used for deletes, updates and state watches (read-only lists like "All resources" have none).
                    if (d.CanDelete && !d.ReadOnly || d.Update != null || d.StatusField != null) paths.Add(d.ItemTemplate);
                    if (d.CreatePath != null) paths.Add(d.CreatePath);
                    paths.AddRange(d.Actions.Where(a => a.Special == null).Select(a => a.Path));
                    foreach (var x in paths)
                    {
                        ops++;
                        if (!x.Contains("api-version=")) throw new Exception($"{d.Title}: no api-version in {x}");
                        if (SecretPaths.Any(sp => x.Contains(sp, StringComparison.OrdinalIgnoreCase))) throw new Exception($"{d.Title}: a secret-reading path {x}");
                        if (!(x.StartsWith("/subscriptions/" + AzureClient.Sub + "/") || x.StartsWith("{path:id}") || child && x.StartsWith("{path:parent.id}"))) throw new Exception($"{d.Title}: path outside the subscription: {x}");
                    }
                    if (child != d.IsChild) throw new Exception($"{d.Title}: child flag");
                    foreach (var (_, c) in d.Children) Def(c, true);
                }
                foreach (var d in AzureCatalog.All) Def(d, false);
                var row = AzureClient.Flatten(JsonDocument.Parse(FakeAzure.Vm("vm-9", "deallocated").ToJsonString()).RootElement);
                var inst = p.Normalize(row, az.Id) ?? throw new Exception("normalize");
                if (inst is not { Name: "vm-9", Region: "uksouth", Plan: "Standard_B2s", State: CloudState.Stopped, DefaultUser: "dcxadmin" } || J.Str(row, "_rg") != "rg-web" || !inst.Id.EndsWith("/virtualMachines/vm-9"))
                    throw new Exception("normalized: " + inst);
                if (AzureProvider.StateOf("deallocating") != CloudState.Busy || AzureProvider.StateOf("running") != CloudState.Running) throw new Exception("state mapping");
                if (Tpl.SafePath("/subscriptions/x/../y") != "" || Tpl.SafePath("/a//b") != "" || Tpl.SafePath("/a/b?c#d") != "/a/b%3Fc%23d") throw new Exception("SafePath");
                var tplRow = JsonDocument.Parse("{\"id\":\"/subscriptions/s/resourceGroups/rg 1/x\"}").RootElement;
                if (Tpl.Fill("{path:id}/start?api-version=1", tplRow, null, url: true) != "/subscriptions/s/resourceGroups/rg%201/x/start?api-version=1") throw new Exception("{path:id}");
                var form = new JsonObject();
                J.SetPath(form, "properties.access", "Allow"); J.SetPath(form, "name", "r1");
                if (form.ToJsonString() != "{\"properties\":{\"access\":\"Allow\"},\"name\":\"r1\"}" || Tpl.Fill("{form.properties.access} {form.name}", null, null, form: form) != "Allow r1") throw new Exception("nested form: " + form.ToJsonString());
                ok($"Azure provider: registered, capabilities (no console), {ops} catalog paths ({AzureCatalog.All.Length} screens) all in the subscription with an api-version and none reading secrets, child screens flagged, VM normalisation (_rg, _power from the instance view), path templates segment-escaped (. / .. / empty segments refused), nested form bodies");
            }
            catch (Exception ex) { fail("Azure provider", ex); }

            // 2. Sign-in and client: certificate assertion verified by the fake, token cached, pages, guards, operations, errors, secrets.
            try
            {
                fakeAz.Reset();
                var api = new AzureClient(az);
                if (await api.TestAsync() != "DCX lab (Enabled)" || fakeAz.TokenCalls != 1 || fakeAz.AssertionProblem != null) throw new Exception("sign-in: " + fakeAz.AssertionProblem);
                var vms = await api.ListAsync(AzureCatalog.VirtualMachine.Path, "value");
                if (vms.Count != 3 || fakeAz.Calls.Count(c => c.Path.EndsWith("/virtualMachines")) != 2 || fakeAz.TokenCalls != 1) throw new Exception($"pages {vms.Count} / token calls {fakeAz.TokenCalls}");
                if (vms.Any(v => J.Str(v, "_rg") != "rg-web") || J.Str(vms[1], "_power") != "deallocated") throw new Exception("flatten");
                if (fakeAz.Calls.Where(c => c.Host == "management.azure.com").Any(c => c.Auth != "Bearer " + FakeAzure.Token)) throw new Exception("bearer");

                int before = fakeAz.Calls.Count;
                var g1 = await Throws(() => api.GetAsync("https://evil.example/subscriptions/x"));
                var g2 = await Throws(() => api.GetAsync("/subscriptions/00000000-0000-4000-8000-00000000dead/resourcegroups?api-version=2021-04-01"));
                var g3 = await Throws(() => api.GetAsync("/tenants?api-version=2022-12-01"));
                if (g1 is not CloudApiException || g2 is not CloudApiException || !g2.Message.Contains("another subscription") || g3 is not CloudApiException || fakeAz.Calls.Count != before) throw new Exception("guards: " + g1?.Message + " / " + g2?.Message + " / " + g3?.Message);

                var progress = new List<string>();
                void OnProgress(string prov, string text) { if (prov == "azure") lock (progress) progress.Add(text); }
                CloudProgress.Changed += OnProgress;
                try { await p.PowerAsync(api, J.Str(vms[1], "id"), CloudPower.Start); }
                finally { CloudProgress.Changed -= OnProgress; }
                if (fakeAz.Power["vm-2"] != "running" || fakeAz.OperationPolls < 2 || !progress.Any(x => x.StartsWith("✓"))) throw new Exception($"start: {fakeAz.Power["vm-2"]} polls {fakeAz.OperationPolls} progress {string.Join("|", progress)}");
                var broken = await Throws(() => p.PowerAsync(api, J.Str(vms[2], "id"), CloudPower.Reboot));
                if (broken is not CloudApiException || !broken.Message.Contains("maintenance")) throw new Exception("failed operation: " + broken?.Message);
                before = fakeAz.Calls.Count;
                var roErr = await Throws(() => p.PowerAsync(new AzureClient(azRo), J.Str(vms[0], "id"), CloudPower.Stop));
                if (roErr is not CloudApiException { Status: 403 } || !roErr.Message.StartsWith("Not sent") || fakeAz.Calls.Count != before) throw new Exception("read-only: " + roErr?.Message);

                fakeAz.Script($"GET /subscriptions/{FakeAzure.Sub}/resourcegroups", 403, "{\"error\":{\"code\":\"AuthorizationFailed\",\"message\":\"The client does not have authorization to perform action.\"}}");
                var e403 = await Throws(() => api.ListAsync(AzureCatalog.ResourceGroup.Path, "value"));
                var wrongCert = new AzureAccount { Id = "selftest-wrong-cert", Name = "wrong", TenantId = FakeAzure.Tenant, ClientId = FakeAzure.Client, SubscriptionId = FakeAzure.Sub, CertificatePfx = Convert.ToBase64String(otherCert.Export(X509ContentType.Pfx, "x")), CertificatePassword = "x" };
                var eCert = await Throws(() => new AzureClient(wrongCert).TestAsync());
                var eTenant = await Throws(() => new AzureClient(new AzureAccount { Id = "selftest-wrong-tenant", Name = "t", TenantId = "00000000-0000-4000-8000-0000000000ff", ClientId = FakeAzure.Client, SubscriptionId = FakeAzure.Sub, CertificatePfx = pfx, CertificatePassword = pfxPassword }).TestAsync());
                var ePw = await Throws(() => Task.Run(() => AzureAuth.LoadCertificate(new AzureAccount { CertificatePfx = pfx, CertificatePassword = "wrong" }).Dispose()));
                if (e403 is not CloudApiException { Status: 403 } || !e403.Message.Contains("role")) throw new Exception("AuthorizationFailed: " + e403?.Message);
                if (eCert is not CloudApiException || !eCert.Message.Contains("AADSTS700027") || !eCert.Message.Contains("certificate")) throw new Exception("wrong certificate: " + eCert?.Message);
                if (eTenant is not CloudApiException || !eTenant.Message.Contains("tenant")) throw new Exception("wrong tenant: " + eTenant?.Message);
                if (ePw is not CloudApiException || !ePw.Message.Contains("can't be opened")) throw new Exception("wrong password: " + ePw?.Message);
                // 0.4.0: the Logs Ingestion token (log sinks) is the same sign-in for another scope, cached apart from the ARM token.
                int tokenCallsBefore = fakeAz.TokenCalls;
                var mon1 = await AzureClient.MonitorTokenAsync(az.Id, default);
                var mon2 = await AzureClient.MonitorTokenAsync(az.Id, default);
                if (mon1 != FakeAzure.MonitorToken || mon2 != mon1 || fakeAz.TokenCalls != tokenCallsBefore + 1 || await Throws(() => AzureClient.MonitorTokenAsync("no-such-account", default)) is not CloudApiException)
                    throw new Exception($"monitor token: {mon1} calls {fakeAz.TokenCalls - tokenCallsBefore}");
                var audit = AuditLog.Read(DateTime.Now.ToString("yyyy-MM"));
                var secrets = new[] { FakeAzure.Token, FakeAzure.MonitorToken, pfx[..40], pfxPassword };
                if (audit.Any(a => secrets.Any(x => (a.Target + a.Action + a.Result).Contains(x))) || new[] { g1, g2, broken, roErr, e403, eCert, eTenant, ePw }.Any(e => secrets.Any(x => e!.Message.Contains(x))))
                    throw new Exception("a secret reached the audit log or a message");
                ok("Azure client: certificate sign-in (PS256 assertion with x5t#S256, audience, issuer, ten-minute life and signature verified by the fake Entra ID), token cached across calls, 3 VMs over 2 nextLink pages with Bearer only to Resource Manager, links off Resource Manager / other subscriptions / tenant paths refused before sending, start followed through Azure-AsyncOperation to Succeeded with progress, a failed operation reported with Azure's reason, a read-only subscription refused locally, AuthorizationFailed / wrong certificate (AADSTS700027) / unknown tenant / wrong .pfx password explained, the Azure Monitor (Logs Ingestion) token from the same sign-in cached per scope, no token, certificate or password in messages or the audit log");
            }
            catch (Exception ex) { fail("Azure client", ex); }

            // 3. Screen and AI tools.
            try
            {
                fakeAz.Reset();
                if (main.Panes.AllTabs.FirstOrDefault(t => t.Content is CloudWorkspaceView) is { } oldWs) main.CloseTab(oldWs);
                main.Navigate(p.NavKey(AzureCatalog.VirtualMachine));
                await Settle(600);
                var tab = main.Panes.ActiveTab ?? throw new Exception("no tab");
                tabs.Add(tab);
                var view = (tab.Content as CloudWorkspaceView)?.CurrentView as CloudResourceView ?? throw new Exception("no resource view");
                await view.LoadAsync();
                if (view.Rows.Count != 3) throw new Exception($"rows {view.Rows.Count}");
                var dealloc = view.Rows.First(r => r["_power"] == "running");
                view.SelectId(dealloc["id"]);
                await Settle(150);
                if (view.ButtonFor("▶ Start") is not { IsEnabled: false } || view.ButtonFor("■ Stop (deallocate)") is not { IsEnabled: true }) throw new Exception("state-aware buttons");
                snap(main, Path.Combine(shots, "azure-vms.png"));
                var ai = new Assistant { Approve = (_, _, _) => Task.FromResult(true) };
                var list = await ai.TestToolAsync("cloud_list_instances", new JsonObject { ["provider"] = "azure" });
                if (JsonDocument.Parse(list).RootElement.GetArrayLength() != 3) throw new Exception("AI list: " + list);
                var vmsText = await ai.TestToolAsync("cloud_list", new JsonObject { ["provider"] = "azure", ["resource"] = "Virtual machines" });
                if (!vmsText.Contains("vm-1") || !vmsText.Contains("untrusted=\"true\"")) throw new Exception("cloud_list: " + vmsText);
                var acts = await ai.TestToolAsync("cloud_action", new JsonObject { ["provider"] = "azure", ["resource"] = "Virtual machines", ["action"] = "Spaceship" });
                if (!acts.Contains("Restart") || acts.Contains("Resize")) throw new Exception("cloud_action list: " + acts);
                ai.Approve = (_, _, _) => Task.FromResult(false);
                var declined = await ai.TestToolAsync("cloud_instance_action", new JsonObject { ["provider"] = "azure", ["instance_id"] = dealloc["id"], ["action"] = "stop" });
                if (declined != "User declined." || fakeAz.Calls.Any(c => c.Method == "POST")) throw new Exception("declined: " + declined);
                ok("Azure UI + AI: azure:Virtual machines opens in the Cloud providers workspace with 3 VMs, Start disabled / Stop (deallocate) enabled on a running VM, rendered azure-vms.png; AI cloud_list_instances, cloud_list (fenced as untrusted), cloud_action lists the screen's own actions (no Special ones), a declined stop never sent");
            }
            catch (Exception ex) { fail("Azure UI + AI", ex); }
        }
        finally
        {
            foreach (var t in tabs.Where(main.Panes.Contains).ToList()) main.CloseTab(t);
            tabs.Clear();
            AzureClient.UseHandler(null);
            s.AzureAccounts = azureWas.Item1; s.ActiveAzureAccountId = azureWas.ActiveAzureAccountId;
        }

        // ---- Google Cloud -----------------------------------------------------------------------------------------------
        using var gKey = RSA.Create(2048);
        using var otherKey = RSA.Create(2048);
        string KeyFile(RSA k, string tokenUri = GcpAuth.TokenUri, string type = "service_account") => new JsonObject
        {
            ["type"] = type, ["project_id"] = FakeGcp.Project, ["private_key_id"] = FakeGcp.KeyId, ["private_key"] = k.ExportPkcs8PrivateKeyPem(),
            ["client_email"] = FakeGcp.Email, ["client_id"] = "100000000000000000001", ["token_uri"] = tokenUri,
        }.ToJsonString();
        var fakeG = new FakeGcp(gKey);
        GcpClient.UseHandler(fakeG);
        var gc = new GcpAccount { Name = "dcx-gcp-lab", ProjectId = FakeGcp.Project, ServiceAccountJson = KeyFile(gKey), ClientEmail = FakeGcp.Email, ReadOnly = false };
        var gcRo = new GcpAccount { Name = "dcx-gcp-readonly", ProjectId = FakeGcp.Project, ServiceAccountJson = KeyFile(gKey), ClientEmail = FakeGcp.Email, ReadOnly = true };
        try
        {
            var p = GcpProvider.Instance;
            s.GcpAccounts = new() { gc, gcRo };
            s.ActiveGcpAccountId = gc.Id;

            // 4. Registration, catalog rules, key files, normalisation.
            try
            {
                if (CloudProviders.Find("gcp") != p || p.Has(CloudCaps.Console) || !p.Has(CloudCaps.Start | CloudCaps.Stop | CloudCaps.Reboot | CloudCaps.Resize | CloudCaps.Firewalls | CloudCaps.Dns)) throw new Exception("registration / capabilities");
                int ops = 0;
                void Def(ResourceDef d)
                {
                    var paths = new List<string> { d.Path, d.ItemTemplate };
                    if (d.CreatePath != null) paths.Add(d.CreatePath);
                    paths.AddRange(d.Actions.Where(a => a.Special == null).Select(a => a.Path));
                    foreach (var x in paths)
                    {
                        ops++;
                        var host = Regex.Match(x, @"^https://([a-z0-9.]+)[/{]").Groups[1].Value;
                        if (!GcpClient.Hosts.Contains(host)) throw new Exception($"{d.Title}: not on an allowed host: {x}");
                        if (SecretPaths.Any(sp => x.Contains(sp, StringComparison.OrdinalIgnoreCase))) throw new Exception($"{d.Title}: a secret-reading path {x}");
                    }
                    foreach (var (_, c) in d.Children) { if (!c.IsChild) throw new Exception(c.Title + ": child flag"); Def(c); }
                }
                foreach (var d in GcpCatalog.All) Def(d);
                var k1 = await Throws(() => Task.Run(() => GcpAuth.ParseKeyFile(KeyFile(gKey, tokenUri: "https://evil.example/token"))));
                var k2 = await Throws(() => Task.Run(() => GcpAuth.ParseKeyFile(KeyFile(gKey, type: "authorized_user"))));
                var k3 = await Throws(() => Task.Run(() => GcpAuth.ParseKeyFile("{not json")));
                if (k1 is not CloudApiException || !k1.Message.Contains("token endpoint") || k2 is not CloudApiException || k3 is not CloudApiException) throw new Exception("key file checks");
                var row = GcpClient.Flatten(JsonDocument.Parse(FakeGcp.Vm("vm-9", "TERMINATED", "europe-west2-a", 9).ToJsonString()).RootElement);
                var inst = p.Normalize(row, gc.Id) ?? throw new Exception("normalize");
                if (inst is not { Name: "vm-9", Region: "europe-west2-a", Plan: "e2-small", Ipv4: "203.0.113.109", State: CloudState.Stopped, Os: "ubuntu-2404-lts" } || inst.Id != $"/compute/v1/projects/{FakeGcp.Project}/zones/europe-west2-a/instances/vm-9")
                    throw new Exception("normalized: " + inst);
                if (GcpProvider.StateOf("STOPPING") != CloudState.Busy || GcpProvider.StateOf("RUNNING") != CloudState.Running) throw new Exception("state mapping");
                ok($"Google Cloud provider: registered, capabilities (no console), {ops} catalog paths ({GcpCatalog.All.Length} screens) all on the allowed API hosts and none reading secrets, key files checked (service_account only, Google's token endpoint only), VM normalisation (_self, _zone, _type, _ip, boot licence)");
            }
            catch (Exception ex) { fail("Google Cloud provider", ex); }

            // 5. Sign-in and client.
            try
            {
                fakeG.Reset();
                if (await new GcpClient(gcRo).TestAsync() != "DCX lab (ACTIVE)" || fakeG.AssertionProblem != null || fakeG.Scopes.LastOrDefault() != GcpAuth.ScopeRead) throw new Exception("read-only sign-in: " + fakeG.AssertionProblem + " " + string.Join(",", fakeG.Scopes));
                var api = new GcpClient(gc);
                var vms = await api.ListAsync(GcpCatalog.Instance.Path, "items");
                if (fakeG.Scopes.LastOrDefault() != GcpAuth.ScopeAll) throw new Exception("full scope for a project that allows changes");
                if (vms.Count != 3 || fakeG.Calls.Count(c => c.Path.EndsWith("/aggregated/instances")) != 2 || !fakeG.Calls.Any(c => c.Query.Contains("pageToken=p2"))) throw new Exception($"pages {vms.Count}");
                if (J.Str(vms[0], "_self") != $"/compute/v1/projects/{FakeGcp.Project}/zones/europe-west2-a/instances/vm-1" || J.Str(vms[0], "_zone") != "europe-west2-a" || J.Str(vms[0], "_ip") != "203.0.113.101") throw new Exception("flatten: " + vms[0]);
                if (fakeG.Calls.Where(c => c.Host != "oauth2.googleapis.com").Any(c => c.Auth != "Bearer " + FakeGcp.Token)) throw new Exception("bearer");

                int before = fakeG.Calls.Count;
                var g1 = await Throws(() => api.GetAsync($"https://compute.googleapis.com/compute/v1/projects/someone-else/zones"));
                var g2 = await Throws(() => api.GetAsync($"https://compute.googleapis.com.evil.example/compute/v1/projects/{FakeGcp.Project}/zones"));
                var g3 = await Throws(() => api.GetAsync($"http://compute.googleapis.com/compute/v1/projects/{FakeGcp.Project}/zones"));
                var g4 = await Throws(() => api.GetAsync("https://storage.googleapis.com/storage/v1/b?project=someone-else"));
                var g5 = await Throws(() => api.GetAsync("https://iam.googleapis.com/v1/projects/" + FakeGcp.Project + "/serviceAccounts"));
                if (new[] { g1, g2, g3, g4, g5 }.Any(e => e is not CloudApiException) || fakeG.Calls.Count != before) throw new Exception("guards");

                var stopped = vms.First(v => J.Str(v, "status") == "TERMINATED");
                await p.PowerAsync(api, J.Str(stopped, "_self"), CloudPower.Start);
                if (fakeG.Status[J.Str(stopped, "name")] != "RUNNING" || fakeG.OperationPolls < 2) throw new Exception("start not followed to DONE");
                var broken = await Throws(() => p.PowerAsync(api, J.Str(vms.First(v => J.Str(v, "name") == "vm-broken"), "_self"), CloudPower.Stop));
                if (broken is not CloudApiException || !broken.Message.Contains("maintenance")) throw new Exception("failed operation: " + broken?.Message);

                var run = await api.ListAsync(GcpCatalog.RunService.Path, "services");
                if (run.Count != 1 || J.Str(run[0], "_short") != "api" || J.Str(run[0], "_location") != "europe-west2") throw new Exception("run list");
                await api.SendAsync(HttpMethod.Delete, "https://run.googleapis.com" + Tpl.SafePath(J.Str(run[0], "_self")), null);
                if (!fakeG.RunDeleted || fakeG.ProjectReads != 2) throw new Exception($"run delete by project number: deleted {fakeG.RunDeleted}, project reads {fakeG.ProjectReads}");
                var gNum = await Throws(() => api.GetAsync("https://run.googleapis.com/v2/projects/999999999999/locations/x/services"));
                if (gNum is not CloudApiException || !gNum.Message.Contains("another project")) throw new Exception("another project's number: " + gNum?.Message);

                before = fakeG.Calls.Count;
                var roErr = await Throws(() => p.PowerAsync(new GcpClient(gcRo), J.Str(vms[0], "_self"), CloudPower.Stop));
                if (roErr is not CloudApiException { Status: 403 } || !roErr.Message.StartsWith("Not sent") || fakeG.Calls.Count != before) throw new Exception("read-only: " + roErr?.Message);
                fakeG.Script($"GET /dns/v1/projects/{FakeGcp.Project}/managedZones", 403, "{\"error\":{\"code\":403,\"message\":\"Cloud DNS API has not been used in project.\",\"status\":\"PERMISSION_DENIED\",\"details\":[{\"@type\":\"type.googleapis.com/google.rpc.ErrorInfo\",\"reason\":\"SERVICE_DISABLED\"}]}}");
                var eApi = await Throws(() => api.ListAsync(GcpCatalog.DnsZone.Path, "managedZones"));
                var wrongKey = new GcpAccount { Id = "selftest-wrong-key", Name = "wrong", ProjectId = FakeGcp.Project, ServiceAccountJson = KeyFile(otherKey), ReadOnly = true };
                var eKey = await Throws(() => new GcpClient(wrongKey).TestAsync());
                if (eApi is not CloudApiException || !eApi.Message.Contains("isn't enabled")) throw new Exception("SERVICE_DISABLED: " + eApi?.Message);
                if (eKey is not CloudApiException || !eKey.Message.Contains("deleted or disabled")) throw new Exception("wrong key: " + eKey?.Message);
                var audit = AuditLog.Read(DateTime.Now.ToString("yyyy-MM"));
                var pemLine = gKey.ExportPkcs8PrivateKeyPem().Split('\n')[1].Trim();
                var secrets = new[] { FakeGcp.Token, pemLine };
                if (audit.Any(a => secrets.Any(x => (a.Target + a.Action + a.Result).Contains(x))) || new[] { g1, g2, g3, g4, g5, broken, gNum, roErr, eApi, eKey }.Any(e => secrets.Any(x => e!.Message.Contains(x))))
                    throw new Exception("a secret reached the audit log or a message");
                ok("Google Cloud client: RS256 grant (key id, issuer, audience, one-hour life, signature verified by the fake), read-only scope while the project is read-only and the full scope once changes are allowed, 3 VMs from aggregated pages (warning scopes skipped, pageToken), other projects / hosts / plain http / other IAM APIs refused before sending, Compute start followed to DONE, a failed operation reported with Google's reason, Cloud Run delete by project number (read once from Resource Manager) followed to done and another project's number refused, read-only refused locally, SERVICE_DISABLED / revoked key explained, no token or key in messages or the audit log");
            }
            catch (Exception ex) { fail("Google Cloud client", ex); }

            // 6. Screen and AI tools.
            try
            {
                fakeG.Reset();
                if (main.Panes.AllTabs.FirstOrDefault(t => t.Content is CloudWorkspaceView) is { } oldWs) main.CloseTab(oldWs);
                main.Navigate(p.NavKey(GcpCatalog.Instance));
                await Settle(600);
                var tab = main.Panes.ActiveTab ?? throw new Exception("no tab");
                tabs.Add(tab);
                var view = (tab.Content as CloudWorkspaceView)?.CurrentView as CloudResourceView ?? throw new Exception("no resource view");
                await view.LoadAsync();
                if (view.Rows.Count != 3) throw new Exception($"rows {view.Rows.Count}");
                var term = view.Rows.First(r => r["status"] == "TERMINATED");
                view.SelectId(term["id"]);
                await Settle(150);
                if (view.ButtonFor("▶ Start") is not { IsEnabled: true } || view.ButtonFor("■ Stop") is not { IsEnabled: false }) throw new Exception("state-aware buttons");
                snap(main, Path.Combine(shots, "gcp-vms.png"));
                var ai = new Assistant { Approve = (_, _, _) => Task.FromResult(true) };
                var list = await ai.TestToolAsync("cloud_list_instances", new JsonObject { ["provider"] = "gcp" });
                if (JsonDocument.Parse(list).RootElement.GetArrayLength() != 3) throw new Exception("AI list: " + list);
                var text = await ai.TestToolAsync("cloud_list", new JsonObject { ["provider"] = "gcp", ["resource"] = "VM instances" });
                if (!text.Contains("vm-1") || !text.Contains("europe-west2-a")) throw new Exception("cloud_list: " + text);
                ok("Google Cloud UI + AI: gcp:VM instances opens in the Cloud providers workspace with 3 VMs from every zone, Start enabled / Stop disabled on a stopped VM, rendered gcp-vms.png; AI cloud_list_instances and cloud_list");
            }
            catch (Exception ex) { fail("Google Cloud UI + AI", ex); }
        }
        finally
        {
            foreach (var t in tabs.Where(main.Panes.Contains).ToList()) main.CloseTab(t);
            GcpClient.UseHandler(null);
            CloudHttp.Sleep = sleepWas;
            AzureClient.OperationPoll = pollWas.Item1; GcpClient.OperationPoll = pollWas.Item2;
            s.GcpAccounts = gcpWas.Item1; s.ActiveGcpAccountId = gcpWas.ActiveGcpAccountId;
            AppSettings.Save();
        }
    }
}

/// <summary>Records calls and plays back scripted answers, for the Azure and Google fakes.</summary>
internal abstract class FakeCloudBase : HttpMessageHandler
{
    public sealed record Call(string Method, string Host, string Path, string Query, string? Auth, string? Body);
    public List<Call> Calls { get; } = new();
    private readonly List<(string Key, int Status, string Body)> _scripts = new();
    public void Script(string key, int status, string body) { lock (Calls) _scripts.Add((key, status, body)); }
    protected void ClearBase() { Calls.Clear(); _scripts.Clear(); }

    protected sealed record Answer(int Status, string Body, Dictionary<string, string>? Headers = null);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        await Task.Yield();
        var u = req.RequestUri!;
        var body = req.Content != null ? await req.Content.ReadAsStringAsync(ct) : null;
        Answer a;
        lock (Calls)
        {
            Calls.Add(new Call(req.Method.Method, u.Host, u.AbsolutePath, u.Query, req.Headers.Authorization?.ToString(), body));
            var key = $"{req.Method.Method} {u.AbsolutePath}";
            var script = _scripts.FirstOrDefault(x => x.Key == key);
            if (script.Key != null) { _scripts.Remove(script); a = new Answer(script.Status, script.Body); }
            else a = Route(req.Method.Method, u.Host, u.AbsolutePath, u.Query, req.Headers.Authorization?.ToString(), body);
        }
        var r = new HttpResponseMessage((HttpStatusCode)a.Status) { Content = new StringContent(a.Body, Encoding.UTF8, "application/json") };
        foreach (var (k, v) in a.Headers ?? new()) r.Headers.TryAddWithoutValidation(k, v);
        return r;
    }

    protected abstract Answer Route(string method, string host, string path, string query, string? auth, string? body);

    protected static string? JwtProblem(string jwt, Func<JsonElement, JsonElement, string?> check, Func<byte[], byte[], bool> verify)
    {
        var parts = jwt.Split('.');
        if (parts.Length != 3) return "not a JWT";
        static byte[] B64(string s) { s = s.Replace('-', '+').Replace('_', '/'); return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=')); }
        try
        {
            using var h = JsonDocument.Parse(B64(parts[0]));
            using var c = JsonDocument.Parse(B64(parts[1]));
            if (check(h.RootElement, c.RootElement) is { } problem) return problem;
            return verify(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), B64(parts[2])) ? null : "bad signature";
        }
        catch (Exception ex) when (ex is FormatException or JsonException) { return "malformed"; }
    }
}

/// <summary>Microsoft Entra ID's token endpoint and Azure Resource Manager, as far as the self-test needs them.</summary>
internal sealed class FakeAzure(X509Certificate2 cert) : FakeCloudBase
{
    public const string Tenant = "00000000-0000-4000-8000-0000000000a1", Client = "00000000-0000-4000-8000-0000000000b1", Sub = "00000000-0000-4000-8000-0000000000c1";
    public const string Token = "fake-azure-access-token-selftest-0001", MonitorToken = "fake-azure-monitor-token-selftest-0002";
    public int TokenCalls, OperationPolls;
    public string? AssertionProblem;
    public Dictionary<string, string> Power { get; } = new();
    private readonly Dictionary<string, (string Vm, string Then, bool Fail, int Polls)> _ops = new();
    private int _nextOp;

    public void Reset() { lock (Calls) { ClearBase(); TokenCalls = 0; OperationPolls = 0; AssertionProblem = null; _ops.Clear(); Power.Clear(); Power["vm-1"] = "running"; Power["vm-2"] = "deallocated"; Power["vm-broken"] = "running"; } }

    public static JsonObject Vm(string name, string power) => new()
    {
        ["id"] = $"/subscriptions/{Sub}/resourceGroups/rg-web/providers/Microsoft.Compute/virtualMachines/{name}", ["name"] = name, ["location"] = "uksouth", ["type"] = "Microsoft.Compute/virtualMachines",
        ["properties"] = new JsonObject
        {
            ["hardwareProfile"] = new JsonObject { ["vmSize"] = "Standard_B2s" },
            ["storageProfile"] = new JsonObject { ["osDisk"] = new JsonObject { ["osType"] = "Linux" }, ["imageReference"] = new JsonObject { ["offer"] = "ubuntu-24_04-lts", ["sku"] = "server" } },
            ["osProfile"] = new JsonObject { ["adminUsername"] = "dcxadmin", ["computerName"] = name },
            ["provisioningState"] = "Succeeded",
            ["instanceView"] = new JsonObject { ["statuses"] = new JsonArray(new JsonObject { ["code"] = "ProvisioningState/succeeded" }, new JsonObject { ["code"] = "PowerState/" + power }) },
        },
    };

    private static Answer Json(JsonNode n, int status = 200) => new(status, n.ToJsonString());
    private static Answer Error(int status, string code, string message) => Json(new JsonObject { ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }, status);

    protected override Answer Route(string method, string host, string path, string query, string? auth, string? body)
    {
        if (host == "login.microsoftonline.com")
        {
            if (method != "POST" || path != $"/{Tenant}/oauth2/v2.0/token")
                return new Answer(400, "{\"error\":\"invalid_request\",\"error_description\":\"AADSTS90002: Tenant '…' not found.\"}");
            TokenCalls++;
            var form = System.Web.HttpUtility.ParseQueryString(body ?? "");
            if (form["grant_type"] != "client_credentials" || form["client_id"] != Client || form["scope"] is not (AzureAuth.Scope or AzureAuth.MonitorScope) || form["client_assertion_type"] != "urn:ietf:params:oauth:client-assertion-type:jwt-bearer")
                return new Answer(400, "{\"error\":\"invalid_request\",\"error_description\":\"AADSTS900144: missing parameter.\"}");
            bool monitor = form["scope"] == AzureAuth.MonitorScope;
            var aud = $"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/token";
            var problem = JwtProblem(form["client_assertion"] ?? "", (h, c) =>
                h.GetProperty("alg").GetString() != "PS256" ? "alg" :
                h.GetProperty("x5t#S256").GetString() != CloudJwt.Base64Url(cert.GetCertHash(HashAlgorithmName.SHA256)) ? "thumbprint" :
                c.GetProperty("aud").GetString() != aud ? "aud" : c.GetProperty("iss").GetString() != Client || c.GetProperty("sub").GetString() != Client ? "iss/sub" :
                c.GetProperty("exp").GetInt64() - c.GetProperty("nbf").GetInt64() != 600 || string.IsNullOrEmpty(c.GetProperty("jti").GetString()) ? "lifetime/jti" : null,
                (data, sig) => { using var k = cert.GetRSAPublicKey()!; return k.VerifyData(data, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pss); });
            if (problem != null)
            {
                // A different certificate has a different thumbprint: Entra answers AADSTS700027 for both.
                if (problem is not ("thumbprint" or "bad signature")) AssertionProblem = problem;
                return new Answer(401, "{\"error\":\"invalid_client\",\"error_description\":\"AADSTS700027: Client assertion failed signature validation.\\r\\nTrace ID: x\"}");
            }
            return Json(new JsonObject { ["token_type"] = "Bearer", ["expires_in"] = 3599, ["access_token"] = monitor ? MonitorToken : Token });
        }
        if (host != "management.azure.com") return Error(404, "NotFound", "unknown host");
        if (auth != "Bearer " + Token) return Error(401, "InvalidAuthenticationToken", "The access token is invalid.");
        var subPath = $"/subscriptions/{Sub}";
        if (method == "GET" && path == subPath) return Json(new JsonObject { ["subscriptionId"] = Sub, ["displayName"] = "DCX lab", ["state"] = "Enabled" });
        if (method == "GET" && path == subPath + "/providers/Microsoft.Compute/virtualMachines")
        {
            if (query.Contains("%24skiptoken=2") || query.Contains("$skiptoken=2")) return Json(new JsonObject { ["value"] = new JsonArray(Vm("vm-broken", Power["vm-broken"])) });
            return Json(new JsonObject
            {
                ["value"] = new JsonArray(Vm("vm-1", Power["vm-1"]), Vm("vm-2", Power["vm-2"])),
                ["nextLink"] = $"https://management.azure.com{subPath}/providers/Microsoft.Compute/virtualMachines?api-version=2024-07-01&statusOnly=true&%24skiptoken=2",
            });
        }
        if (method == "GET" && path == subPath + "/resourcegroups") return Json(new JsonObject { ["value"] = new JsonArray(new JsonObject { ["id"] = subPath + "/resourceGroups/rg-web", ["name"] = "rg-web", ["location"] = "uksouth" }) });
        var m = Regex.Match(path, $"^{Regex.Escape(subPath)}/resourceGroups/rg-web/providers/Microsoft.Compute/virtualMachines/([^/]+)/(start|deallocate|restart|powerOff)$");
        if (method == "POST" && m.Success)
        {
            var vm = m.Groups[1].Value;
            var then = m.Groups[2].Value switch { "start" or "restart" => "running", "deallocate" => "deallocated", _ => "stopped" };
            var id = "op-" + ++_nextOp;
            _ops[id] = (vm, then, vm == "vm-broken", 0);
            return new Answer(202, "", new() { ["Azure-AsyncOperation"] = $"https://management.azure.com{subPath}/providers/Microsoft.Compute/locations/uksouth/operations/{id}?api-version=2024-07-01" });
        }
        var op = Regex.Match(path, $"^{Regex.Escape(subPath)}/providers/Microsoft.Compute/locations/uksouth/operations/(op-\\d+)$");
        if (method == "GET" && op.Success && _ops.TryGetValue(op.Groups[1].Value, out var o))
        {
            OperationPolls++;
            _ops[op.Groups[1].Value] = o with { Polls = o.Polls + 1 };
            if (o.Polls == 0) return Json(new JsonObject { ["status"] = "InProgress" });
            if (o.Fail) return Json(new JsonObject { ["status"] = "Failed", ["error"] = new JsonObject { ["code"] = "VMAgentStatusCommunicationError", ["message"] = "The host is in maintenance; try again later." } });
            Power[o.Vm] = o.Then;
            return Json(new JsonObject { ["status"] = "Succeeded" });
        }
        return Error(404, "ResourceNotFound", "The resource was not found.");
    }
}

/// <summary>Google's token endpoint, Compute Engine, Cloud Run and Resource Manager, as far as the self-test needs them.</summary>
internal sealed class FakeGcp(RSA key) : FakeCloudBase
{
    public const string Project = "dcx-lab-1", Number = "123456789012", KeyId = "kid-selftest-1", Email = "dcx-selftest@dcx-lab-1.iam.gserviceaccount.com";
    public const string Token = "fake-google-access-token-selftest-0001";
    public string? AssertionProblem;
    public List<string> Scopes { get; } = new();
    public int OperationPolls, ProjectReads;
    public bool RunDeleted;
    public Dictionary<string, string> Status { get; } = new();
    private readonly Dictionary<string, (string Vm, string Then, bool Fail, int Polls)> _ops = new();
    private int _nextOp, _runPolls;

    public void Reset() { lock (Calls) { ClearBase(); AssertionProblem = null; Scopes.Clear(); OperationPolls = 0; ProjectReads = 0; RunDeleted = false; _runPolls = 0; _ops.Clear(); Status.Clear(); Status["vm-1"] = "RUNNING"; Status["vm-2"] = "TERMINATED"; Status["vm-broken"] = "RUNNING"; } }

    public static JsonObject Vm(string name, string status, string zone, int n) => new()
    {
        ["kind"] = "compute#instance", ["id"] = (7000000000000000000L + n).ToString(), ["name"] = name, ["status"] = status,
        ["zone"] = $"https://www.googleapis.com/compute/v1/projects/{Project}/zones/{zone}",
        ["machineType"] = $"https://www.googleapis.com/compute/v1/projects/{Project}/zones/{zone}/machineTypes/e2-small",
        ["selfLink"] = $"https://www.googleapis.com/compute/v1/projects/{Project}/zones/{zone}/instances/{name}",
        ["networkInterfaces"] = new JsonArray(new JsonObject { ["networkIP"] = $"10.154.0.{n + 1}", ["accessConfigs"] = new JsonArray(new JsonObject { ["natIP"] = $"203.0.113.{100 + n}" }) }),
        ["disks"] = new JsonArray(new JsonObject { ["boot"] = true, ["licenses"] = new JsonArray($"https://www.googleapis.com/compute/v1/projects/ubuntu-os-cloud/global/licenses/ubuntu-2404-lts") }),
        ["creationTimestamp"] = "2026-10-01T10:00:00.000-07:00",
    };

    private static Answer Json(JsonNode n, int status = 200) => new(status, n.ToJsonString());
    private static Answer Error(int status, string s, string message) => Json(new JsonObject { ["error"] = new JsonObject { ["code"] = status, ["message"] = message, ["status"] = s } }, status);

    protected override Answer Route(string method, string host, string path, string query, string? auth, string? body)
    {
        if (host == "oauth2.googleapis.com")
        {
            var form = System.Web.HttpUtility.ParseQueryString(body ?? "");
            if (method != "POST" || path != "/token" || form["grant_type"] != "urn:ietf:params:oauth:grant-type:jwt-bearer") return new Answer(400, "{\"error\":\"unsupported_grant_type\"}");
            string? scope = null;
            var problem = JwtProblem(form["assertion"] ?? "", (h, c) =>
            {
                scope = c.GetProperty("scope").GetString();
                return h.GetProperty("alg").GetString() != "RS256" ? "alg" : h.GetProperty("kid").GetString() != KeyId ? "kid" :
                    c.GetProperty("iss").GetString() != Email ? "iss" : c.GetProperty("aud").GetString() != GcpAuth.TokenUri ? "aud" :
                    c.GetProperty("exp").GetInt64() - c.GetProperty("iat").GetInt64() != 3600 ? "lifetime" : null;
            }, (data, sig) => key.VerifyData(data, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
            if (problem != null)
            {
                if (problem != "bad signature") AssertionProblem = problem;
                return new Answer(400, "{\"error\":\"invalid_grant\",\"error_description\":\"Invalid JWT Signature.\"}");
            }
            Scopes.Add(scope ?? "");
            return Json(new JsonObject { ["access_token"] = Token, ["expires_in"] = 3599, ["token_type"] = "Bearer" });
        }
        if (auth != "Bearer " + Token) return Error(401, "UNAUTHENTICATED", "Request had invalid authentication credentials.");
        if (host == "cloudresourcemanager.googleapis.com" && method == "GET" && path == $"/v1/projects/{Project}")
        {
            ProjectReads++;
            return Json(new JsonObject { ["projectId"] = Project, ["projectNumber"] = Number, ["name"] = "DCX lab", ["lifecycleState"] = "ACTIVE" });
        }
        if (host == "compute.googleapis.com")
        {
            if (method == "GET" && path == $"/compute/v1/projects/{Project}/aggregated/instances")
            {
                if (query.Contains("pageToken=p2"))
                    return Json(new JsonObject { ["items"] = new JsonObject { ["zones/us-east1-b"] = new JsonObject { ["instances"] = new JsonArray(Vm("vm-broken", Status["vm-broken"], "us-east1-b", 3)) } } });
                return Json(new JsonObject
                {
                    ["items"] = new JsonObject
                    {
                        ["zones/europe-west2-a"] = new JsonObject { ["instances"] = new JsonArray(Vm("vm-1", Status["vm-1"], "europe-west2-a", 1), Vm("vm-2", Status["vm-2"], "europe-west2-a", 2)) },
                        ["zones/asia-east1-a"] = new JsonObject { ["warning"] = new JsonObject { ["code"] = "NO_RESULTS_ON_PAGE", ["message"] = "There are no results for scope 'zones/asia-east1-a' on this page." } },
                    },
                    ["nextPageToken"] = "p2",
                });
            }
            var m = Regex.Match(path, $"^/compute/v1/projects/{Project}/zones/([a-z0-9-]+)/instances/([^/]+)/(start|stop|reset)$");
            if (method == "POST" && m.Success)
            {
                var id = "operation-" + ++_nextOp;
                _ops[id] = (m.Groups[2].Value, m.Groups[3].Value == "stop" ? "TERMINATED" : "RUNNING", m.Groups[2].Value == "vm-broken", 0);
                return Json(new JsonObject { ["kind"] = "compute#operation", ["name"] = id, ["status"] = "RUNNING", ["selfLink"] = $"https://www.googleapis.com/compute/v1/projects/{Project}/zones/{m.Groups[1].Value}/operations/{id}" });
            }
            var op = Regex.Match(path, $"^/compute/v1/projects/{Project}/zones/[a-z0-9-]+/operations/(operation-\\d+)$");
            if (method == "GET" && op.Success && _ops.TryGetValue(op.Groups[1].Value, out var o))
            {
                OperationPolls++;
                _ops[op.Groups[1].Value] = o with { Polls = o.Polls + 1 };
                var r = new JsonObject { ["kind"] = "compute#operation", ["name"] = op.Groups[1].Value, ["status"] = o.Polls == 0 ? "RUNNING" : "DONE" };
                if (o.Polls > 0 && o.Fail) r["error"] = new JsonObject { ["errors"] = new JsonArray(new JsonObject { ["code"] = "RESOURCE_OPERATION_RATE_EXCEEDED", ["message"] = "The zone is in maintenance." }) };
                else if (o.Polls > 0) Status[o.Vm] = o.Then;
                return Json(r);
            }
        }
        if (host == "run.googleapis.com")
        {
            if (method == "GET" && path == $"/v2/projects/{Project}/locations/-/services")
                return Json(new JsonObject { ["services"] = new JsonArray(new JsonObject { ["name"] = $"projects/{Number}/locations/europe-west2/services/api", ["uid"] = "u-1", ["uri"] = "https://api-selftest.a.run.app", ["terminalCondition"] = new JsonObject { ["state"] = "CONDITION_SUCCEEDED" } }) });
            if (method == "DELETE" && path == $"/v2/projects/{Number}/locations/europe-west2/services/api")
                return Json(new JsonObject { ["name"] = $"projects/{Number}/locations/europe-west2/operations/op-run", ["done"] = false });
            if (method == "GET" && path == $"/v2/projects/{Number}/locations/europe-west2/operations/op-run")
            {
                if (++_runPolls < 2) return Json(new JsonObject { ["name"] = "op-run", ["done"] = false });
                RunDeleted = true;
                return Json(new JsonObject { ["name"] = "op-run", ["done"] = true, ["response"] = new JsonObject() });
            }
        }
        return Error(404, "NOT_FOUND", "The resource was not found.");
    }
}
