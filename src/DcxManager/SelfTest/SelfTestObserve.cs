using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using DcxManager.Notify;
using DcxManager.Services;
using DcxManager.Views;

namespace DcxManager;

/// <summary>
/// 0.4.0 Observability: log sinks and dashboards, against fakes on 127.0.0.1 only. The event document (masked, one line);
/// the sink rules (https only, kinds, pins, Azure Monitor's ids); every kind's payload shape (Elastic bulk NDJSON, Loki
/// streams with nanosecond stamps, Azure Monitor's DCR path and PascalCase columns, Splunk HEC, GELF, webhook array) and
/// where the secret goes (a header, never the body); live delivery to a TLS fake with a pinned self-signed certificate
/// (API key, Basic and Entra bearer headers seen), a wrong pin refused and queued then delivered; the Grafana list through
/// a fake and dashboard URLs kept on the server; the policies and protected items; the Observability screen off screen.
/// </summary>
public static class ObserveTests
{
    public static async Task RunAsync(MainWindow main, Action<string> ok, Action<string, Exception?> fail)
    {
        var s = AppSettings.Current;
        var saved = (s.ObserveEnabled, s.ObserveSinks, new Dictionary<string, string>(s.ObserveSecrets), s.ObserveSendAudit, s.ObserveSendAppLog, s.ObserveSendSessionLogs, s.GrafanaUrl, s.GrafanaOrgId, s.ObserveLinks.ToList());
        var tokenWas = LogShipper.AzureToken;
        const string apiKey = "elastic-ApiKey-Secret-9981", lokiPw = "loki-Password-Secret-5512", monitorToken = "fake-monitor-bearer-token-0042";

        // 1. The event document.
        try
        {
            var e = LogEvent.Audit(new AuditEntry(new DateTime(2026, 10, 21, 9, 30, 0, DateTimeKind.Utc), "you", "host-1", "ran ssh root@h password=Hunter2Secret!\r\nline2", "ok"));
            var json = e.ToJsonString();
            using var d = JsonDocument.Parse(json);
            var r = d.RootElement;
            if (r.GetProperty("@timestamp").GetString() != "2026-10-21T09:30:00.000Z" || r.GetProperty("kind").GetString() != "audit" || r.GetProperty("actor").GetString() != "you" || r.GetProperty("app").GetString() != LogEvent.App) throw new Exception("fields: " + json);
            if (json.Contains("Hunter2Secret") || json.Contains("\\r") || json.Contains("\\n") || !r.GetProperty("message").GetString()!.Contains("[REDACTED]")) throw new Exception("not masked / not one line: " + json);
            var back = LogEvent.FromJson(json) ?? throw new Exception("round trip");
            if (back.Kind != "audit" || back.Message != e.Message || back.Time != e.Time) throw new Exception("round trip values");
            var az = LogEvent.SessionLine(DateTimeOffset.Now, "ssh web-1", "ls -la").ToAzureJson();
            if (az["TimeGenerated"] == null || az["Kind"]?.GetValue<string>() != "session" || az["Session"]?.GetValue<string>() != "ssh web-1") throw new Exception("azure columns: " + az.ToJsonString());
            if (LogEvent.FromJson("{not json") != null || LogEvent.FromJson("[1]") != null) throw new Exception("damaged spool lines must be skipped");
            if (LogEvent.AppLog(DateTimeOffset.Now, "x", new string('a', 5000)).Message.Length > 4001) throw new Exception("message not bounded");
            ok("log event: ECS-like JSON document (@timestamp UTC, app, host, user, kind, severity, actor, target, result, session, message), secrets masked and CR/LF removed at creation, bounded, round-trips through the spool, damaged spool lines skipped, Azure Monitor columns PascalCase with TimeGenerated");
        }
        catch (Exception ex) { fail("log event", ex); }

        // 2. Sink rules and the list.
        try
        {
            var good = new LogSink { Name = "es", Kind = "elastic", Url = "https://es.example.test:9200", Index = "dcx-logs", Auth = "apikey" };
            if (good.Validate() != null) throw new Exception("good refused: " + good.Validate());
            string? Why(Action<LogSink> mutate) { var c = new LogSink { Id = good.Id, Name = good.Name, Kind = good.Kind, Url = good.Url, Index = good.Index, Auth = good.Auth }; mutate(c); return c.Validate(); }
            if (Why(c => c.Url = "http://es.example.test:9200") is not { } p1 || !p1.Contains("https")) throw new Exception("plain http accepted");
            if (Why(c => c.Url = "https://user:pw@es.example.test") == null) throw new Exception("user info in the URL accepted");
            if (Why(c => c.Url = "https://es.example.test/#x") == null) throw new Exception("fragment accepted");
            if (Why(c => c.Kind = "udp") == null || Why(c => c.Auth = "magic") == null) throw new Exception("unknown kind / auth accepted");
            if (Why(c => c.Index = "") == null) throw new Exception("elastic without an index accepted");
            if (Why(c => c.Pin = "ABC") == null || Why(c => c.Pin = new string('A', 64)) != null) throw new Exception("pin rule");
            if (Why(c => c.Name = "a\tb") == null || Why(c => c.Name = new string('n', 61)) == null) throw new Exception("name rule");
            if (Why(c => c.Auth = "entra") == null) throw new Exception("entra on a non-Azure sink accepted");
            var azm = new LogSink { Name = "mon", Kind = "azuremonitor", Url = "https://dce-1.uksouth-1.ingest.monitor.azure.com", Auth = "entra", AzureAccountId = "acct1", Tenant = "dcr-0123abcd", Index = "Custom-Dcx_CL" };
            if (azm.Validate() != null) throw new Exception("azure monitor refused: " + azm.Validate());
            if (new LogSink { Name = "mon", Kind = "azuremonitor", Url = azm.Url, Auth = "entra", AzureAccountId = "acct1", Tenant = "notadcr", Index = "Custom-Dcx_CL" }.Validate() == null) throw new Exception("DCR id rule");
            if (new LogSink { Name = "mon", Kind = "azuremonitor", Url = azm.Url, Auth = "apikey", AzureAccountId = "acct1", Tenant = "dcr-1", Index = "Custom-Dcx_CL" }.Validate() == null) throw new Exception("azure monitor with an api key accepted");
            var list = new List<LogSink> { good, azm };
            var text = LogSink.ToJson(list);
            if (text.Contains("Secret") || text.Contains("KindTitle") || text.Contains("\"Host\"")) throw new Exception("computed fields / secrets in the list: " + text);
            var parsed = LogSink.ParseList(text);
            if (parsed.Count != 2 || parsed[0].Id != good.Id || parsed[1].Tenant != "dcr-0123abcd") throw new Exception("round trip: " + text);
            if (LogSink.Check(text) != null || LogSink.Check("") != null) throw new Exception("good list refused: " + LogSink.Check(text));
            if (LogSink.Check("{not a list") == null || LogSink.Check(LogSink.ToJson(new[] { good, good })) is not { } dup || !dup.Contains("share an id")) throw new Exception("damaged / duplicate list accepted");
            var plain = LogSink.Check(text.Replace("https://es", "http://es"));
            if (plain == null || !plain.Contains("https")) throw new Exception("http in the list accepted: " + plain);
            if (LogSink.Check(LogSink.ToJson(Enumerable.Range(0, 13).Select(i => new LogSink { Id = "sink" + i.ToString("00000"), Name = "s" + i, Kind = "webhook", Url = "https://h.test/" + i, Auth = "none" }))) is not { } many || !many.Contains("more than")) throw new Exception("13 sinks accepted");
            if (LogSink.ParseList(text.Replace("https://es", "http://es")).Count != 1) throw new Exception("ParseList must drop an invalid sink, keep the rest");
            if (ProtectedSettings.Find("observe.sinks")!.Check(JsonValue.Create(text)) != null || ProtectedSettings.Find("observe.sinks")!.Check(JsonValue.Create(text.Replace("https://es", "http://es"))) == null) throw new Exception("the protected item doesn't use the sink rules");
            if (!PolicySettings.Catalog.Any(p => p.Name == nameof(AppSettings.ObserveSinks)) || !PolicySettings.Catalog.Any(p => p.Name == nameof(AppSettings.GrafanaUrl)) || !PolicySettings.Catalog.Any(p => p.Name == nameof(AppSettings.ObserveLinks) && p.Kind == PolicySettings.Kind.List)) throw new Exception("sinks / Grafana / links are not policies");
            if (PolicySettings.Catalog.Any(p => p.Name == nameof(AppSettings.ObserveSecrets)) || !PolicySettings.Excluded.ContainsKey(nameof(AppSettings.ObserveSecrets))) throw new Exception("the sink secrets are a policy");
            ok("log sinks: https only (no user info, no fragment), known kinds and authentications, Elastic needs an index, Azure Monitor needs the Entra sign-in + a dcr- rule id + a Custom- stream, 64-hex pins, names bounded; the list round-trips without secrets, duplicates / damage / plain http / more than 12 refused by the protected item and ParseList drops only the bad one; ObserveSinks, GrafanaUrl and ObserveLinks are Group Policies, the secrets never");
        }
        catch (Exception ex) { fail("log sink rules", ex); }

        // 3. Payload shapes per kind; the secret is a header, never in the body or path.
        try
        {
            var evs = new[] { LogEvent.Audit(new AuditEntry(DateTime.UtcNow, "you", "t1", "first", "ok")), LogEvent.AppLog(DateTimeOffset.Now, "where", "second") };
            LogSink Mk(string kind, string auth, string index = "idx", string tenant = "") => new() { Name = kind, Kind = kind, Url = "https://sink.example.test:9999/base", Index = index, Tenant = tenant, Auth = auth, User = "u1", AzureAccountId = "a" };
            var es = Mk("elastic", "apikey").Payloads(evs, "KEY1").Single();
            var lines = es.Body.TrimEnd('\n').Split('\n');
            if (es.Path != "/_bulk" || es.ContentType != "application/x-ndjson" || lines.Length != 4 || lines[0] != "{\"create\":{\"_index\":\"idx\"}}" || !lines[1].Contains("\"first\"") || !es.Headers.Contains(("Authorization", "ApiKey KEY1"))) throw new Exception("elastic: " + es.Path + " " + es.Body);
            var loki = Mk("loki", "basic", tenant: "team-a").Payloads(evs, "pw").Single();
            using (var d = JsonDocument.Parse(loki.Body))
            {
                var streams = d.RootElement.GetProperty("streams");
                if (loki.Path != "/loki/api/v1/push" || streams.GetArrayLength() != 2 || !loki.Headers.Contains(("X-Scope-OrgID", "team-a")) || !loki.Headers.Contains(("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("u1:pw"))))) throw new Exception("loki: " + loki.Body);
                var v = streams[0].GetProperty("values")[0];
                if (!long.TryParse(v[0].GetString(), out var ns) || ns < 1_000_000_000_000_000_000L || streams[0].GetProperty("stream").GetProperty("app").GetString() != "dcx-servermanagement") throw new Exception("loki stamp / labels: " + loki.Body);
            }
            var azs = Mk("azuremonitor", "entra", "Custom-Dcx_CL", "dcr-abc");
            var az = azs.Payloads(evs, null, "TOK").Single();
            if (az.Path != "/dataCollectionRules/dcr-abc/streams/Custom-Dcx_CL?api-version=2023-01-01" || !az.Headers.Contains(("Authorization", "Bearer TOK")) || !az.Body.StartsWith("[{\"TimeGenerated\"") || azs.RequestUrl(az) != "https://sink.example.test:9999/base" + az.Path) throw new Exception("azure: " + az.Path + " " + az.Body[..60]);
            var sp = Mk("splunk", "apikey").Payloads(evs, "HEC").Single();
            if (sp.Path != "/services/collector/event" || !sp.Headers.Contains(("Authorization", "Splunk HEC")) || sp.Body.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length != 2 || !sp.Body.Contains("\"sourcetype\":\"dcx:servermanagement\"") || !sp.Body.Contains("\"index\":\"idx\"")) throw new Exception("splunk: " + sp.Body);
            var gelf = Mk("gelf", "none").Payloads(evs, null);
            if (gelf.Count != 2 || gelf[0].Path != "/gelf" || !gelf[0].Body.Contains("\"version\":\"1.1\"") || !gelf[0].Body.Contains("\"short_message\":\"first\"") || !gelf[1].Body.Contains("\"level\":3") || gelf[0].Headers.Count != 0) throw new Exception("gelf: " + gelf[0].Body);
            var wh = new LogSink { Name = "w", Kind = "webhook", Url = "https://hooks.example.test/in?src=dcx", Auth = "bearer" };
            var w = wh.Payloads(evs, "BT").Single();
            if (w.Path != "" || wh.RequestUrl(w) != wh.Url || !w.Headers.Contains(("Authorization", "Bearer BT")) || !w.Body.StartsWith("[{\"@timestamp\"")) throw new Exception("webhook: " + w.Body[..40]);
            var wk = new LogSink { Name = "w", Kind = "webhook", Url = "https://hooks.example.test/in", Auth = "apikey" }.Payloads(evs, "XK").Single();
            if (!wk.Headers.Contains(("X-API-Key", "XK"))) throw new Exception("webhook api key header");
            foreach (var (p, sec) in new[] { (es, "KEY1"), (loki, "pw"), (az, "TOK"), (sp, "HEC"), (w, "BT") })
                if (p.Body.Contains(sec) || p.Path.Contains(sec)) throw new Exception("a secret in a body or path");
            if (LogSink.ResponseProblem("elastic", 200, "{\"errors\":true,\"items\":[{\"create\":{\"status\":400,\"error\":{\"type\":\"x\",\"reason\":\"mapper parsing failed\"}}}]}") is not { } er || !er.Contains("mapper parsing")) throw new Exception("elastic item errors not seen");
            if (LogSink.ResponseProblem("elastic", 200, "{\"errors\":false}") != null || LogSink.ResponseProblem("loki", 204, "") != null || LogSink.ResponseProblem("loki", 401, "unauthorised token=abc") is not { } e401 || !e401.StartsWith("HTTP 401")) throw new Exception("status handling");
            if (LogSink.ResponseProblem("splunk", 200, "{\"text\":\"Invalid token\",\"code\":4}") == null || LogSink.ResponseProblem("splunk", 200, "{\"text\":\"Success\",\"code\":0}") != null) throw new Exception("splunk codes");
            ok("payloads: Elastic /_bulk NDJSON (create+index, ApiKey header, per-item errors read), Loki /loki/api/v1/push streams per kind with ns stamps + X-Scope-OrgID + Basic, Azure Monitor /dataCollectionRules/<dcr>/streams/<stream>?api-version=2023-01-01 with a bearer and PascalCase rows, Splunk HEC events with Splunk header + sourcetype + index, GELF 1.1 one message per event, webhook JSON array with Bearer / X-API-Key; no secret in any body or path; Splunk codes and HTTP statuses explained");
        }
        catch (Exception ex) { fail("payloads", ex); }

        // 4. Live delivery to a TLS fake on 127.0.0.1 (pinned self-signed certificate): three sinks, three authentications.
        FakeSinkServer? fake = null;
        try
        {
            using var cert = TestWeb.MakeCert("127.0.0.1");
            fake = new FakeSinkServer(cert);
            var pin = Convert.ToHexString(SHA256.HashData(cert.RawData));
            var es = new LogSink { Id = "selftstes001", Name = "selftest-elastic", Kind = "elastic", Url = $"https://127.0.0.1:{fake.Port}", Index = "dcx-selftest", Auth = "apikey", Pin = pin };
            var loki = new LogSink { Id = "selftstlk001", Name = "selftest-loki", Kind = "loki", Url = $"https://127.0.0.1:{fake.Port}", Tenant = "tenant-7", Auth = "basic", User = "12345", Pin = pin };
            var mon = new LogSink { Id = "selftstaz001", Name = "selftest-monitor", Kind = "azuremonitor", Url = $"https://127.0.0.1:{fake.Port}", Tenant = "dcr-selftest", Index = "Custom-Dcx_CL", Auth = "entra", AzureAccountId = "selftest-acct", Pin = pin };
            var wrong = new LogSink { Id = "selftstwp001", Name = "selftest-wrongpin", Kind = "webhook", Url = $"https://127.0.0.1:{fake.Port}/hook", Auth = "none", Pin = "00" + pin[2..] };
            LogShipper.AzureToken = (acct, _) => acct == "selftest-acct" ? Task.FromResult(monitorToken) : throw new InvalidOperationException("unknown account " + acct);
            s.ObserveSinks = LogSink.ToJson(new[] { es, loki, mon, wrong });
            s.ObserveSecrets[es.Id] = apiKey; s.ObserveSecrets[loki.Id] = lokiPw;
            s.ObserveEnabled = true; s.ObserveSendAudit = true; s.ObserveSendAppLog = true; s.ObserveSendSessionLogs = false;
            LogShipper.ResetForTest();
            LogShipper.Start();
            AuditLog.Write("selftest", "observe", "delivery-check-4411");
            await Until(() => new[] { "/_bulk", "/loki/api/v1/push", "/dataCollectionRules/dcr-selftest/streams/Custom-Dcx_CL" }.All(p => fake.Requests.Any(r => r.Path.StartsWith(p) && r.Body.Contains("delivery-check-4411"))), 20_000, "not delivered to the three sinks: " + fake.Summary + " | " + string.Join("; ", LogShipper.Statuses.Select(x => x.Name + "=" + x.State + " " + x.LastError)));
            var bulk = fake.Requests.First(r => r.Path == "/_bulk");
            var push = fake.Requests.First(r => r.Path == "/loki/api/v1/push");
            var dcr = fake.Requests.First(r => r.Path.StartsWith("/dataCollectionRules/"));
            if (bulk.Headers.GetValueOrDefault("authorization") != "ApiKey " + apiKey || bulk.Headers.GetValueOrDefault("content-type")?.StartsWith("application/x-ndjson") != true) throw new Exception("elastic headers: " + string.Join(", ", bulk.Headers));
            if (push.Headers.GetValueOrDefault("authorization") != "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("12345:" + lokiPw)) || push.Headers.GetValueOrDefault("x-scope-orgid") != "tenant-7") throw new Exception("loki headers: " + string.Join(", ", push.Headers));
            if (dcr.Headers.GetValueOrDefault("authorization") != "Bearer " + monitorToken || !dcr.Path.Contains("api-version=2023-01-01")) throw new Exception("azure headers / path: " + dcr.Path + " " + string.Join(", ", dcr.Headers));
            if (bulk.Body.Contains("Hunter2") || !bulk.Body.Contains("\"actor\":\"selftest\"")) throw new Exception("bulk body: " + bulk.Body);
            // The wrong pin: TLS refused, nothing delivered, the event waits; the right pin delivers it.
            await Until(() => LogShipper.Statuses.First(x => x.Id == wrong.Id).State == "failing", 20_000, "a wrong pin wasn't refused");
            if (fake.Requests.Any(r => r.Path == "/hook")) throw new Exception("delivered despite a wrong pin");
            var stWrong = LogShipper.Statuses.First(x => x.Id == wrong.Id);
            if (stWrong.Queued == 0 || stWrong.LastError == null) throw new Exception("not queued / no error: " + stWrong);
            var okSinks = LogShipper.Statuses.Where(x => x.Id != wrong.Id).ToList();
            if (okSinks.Any(x => x.State != "connected" || x.Sent < 1 || x.LastSent == null)) throw new Exception("statuses: " + string.Join("; ", okSinks));
            var test = await LogShipper.SendTestAsync(es.Id);
            if (!test.StartsWith("✓") || !fake.Requests.Any(r => r.Path == "/_bulk" && r.Body.Contains("Test event"))) throw new Exception("test event: " + test);
            wrong.Pin = pin;
            s.ObserveSinks = LogSink.ToJson(new[] { es, loki, mon, wrong });
            AuditLog.Write("selftest", "observe", "after-pin-fix-4412");
            await Until(() => fake.Requests.Any(r => r.Path == "/hook" && r.Body.Contains("delivery-check-4411")) && fake.Requests.Any(r => r.Path == "/hook" && r.Body.Contains("after-pin-fix-4412")), 20_000, "queued events not delivered after the pin was fixed: " + fake.Summary);
            // No secret anywhere it could be read.
            var statusText = string.Join(" ", LogShipper.Statuses.Select(x => x.ToString()));
            var audit = AuditLog.Read(DateTime.Now.ToString("yyyy-MM"));
            var secrets = new[] { apiKey, lokiPw, monitorToken };
            if (secrets.Any(x => statusText.Contains(x) || s.ObserveSinks.Contains(x) || test.Contains(x)) || audit.Any(a => secrets.Any(x => (a.Target + a.Action + a.Result).Contains(x)))) throw new Exception("a secret reached a status, the sink list, a message or the audit log");
            if (fake.Requests.Any(r => !r.Tls)) throw new Exception("a request arrived without TLS");
            var bars = LogShipper.LastHour();
            if (bars.Length != 60 || bars.Sum() < 3) throw new Exception("activity counters: " + bars.Sum());
            ok($"log shipping: an audit line reached an Elastic, a Loki and an Azure Monitor sink over TLS 1.2+ to 127.0.0.1 with a pinned self-signed certificate (ApiKey / Basic+X-Scope-OrgID / Entra bearer headers seen, bodies masked), a wrong pin is refused (TLS) and its event waits in the queue until the pin is right, test event ✓, per-sink state / last sent / sent count, {bars.Sum()} events counted for the activity bars, no secret in statuses, the sink list, messages or the audit log");
        }
        catch (Exception ex) { fail("log shipping", ex); }

        // 5. The Observability screen, off screen (the main window is at -10000).
        try
        {
            // Grafana through a fake: the list, the Bearer header, dashboard URLs kept on the server. The settings go in first:
            // the screen reads them when it is built (and again when it is shown; it is never shown here).
            var fg = new FakeGrafana();
            GrafanaClient.HandlerForTest = fg; GrafanaClient.ResetForTest();
            s.GrafanaUrl = "https://grafana.example.test"; s.GrafanaOrgId = 3; s.ObserveSecrets["grafana"] = "glsa_selftest_token_77";
            s.ObserveLinks = new() { "Kibana=https://kibana.example.test/app/discover", "Sentinel=https://portal.azure.com/#view/Microsoft_Azure_Security_Insights", "bad=http://plain.example.test", "nonsense" };
            var view = new ObservabilityView { Width = 900, Height = 700 };
            view.Measure(new Size(900, 700)); view.Arrange(new Rect(0, 0, 900, 700));
            var text = view.SinksTextForTest;
            if (!text.Contains("selftest-elastic") || !text.Contains("selftest-loki") || !text.Contains("Azure Monitor / Sentinel") || !text.Contains("Shipping: audit log + application log")) throw new Exception("sinks text: " + text);
            if (view.BarCountForTest != 60 || !view.BarsTextForTest.Contains("event(s) in the last hour")) throw new Exception("bars: " + view.BarCountForTest + " " + view.BarsTextForTest);
            var dash = await GrafanaClient.DashboardsAsync(s.GrafanaUrl, s.ObserveSecrets["grafana"], s.GrafanaOrgId);
            var hosts = dash.FirstOrDefault(d => d.Uid == "abc");
            if (dash.Count != 2 || hosts is not { Title: "Hosts", Folder: "Infra" } || hosts.Tags.SingleOrDefault() != "linux" || dash.Any(d => d.Uid == "evil") || fg.LastAuth != "Bearer glsa_selftest_token_77" || fg.LastOrg != "3" || fg.LastPath?.StartsWith("/api/search?type=dash-db") != true) throw new Exception($"grafana list: {dash.Count} {fg.LastAuth} {fg.LastOrg} {fg.LastPath}");
            if (GrafanaClient.DashboardUrl(s.GrafanaUrl, "/d/abc/hosts") != "https://grafana.example.test/d/abc/hosts" || GrafanaClient.DashboardUrl(s.GrafanaUrl, "//evil.test/d") != null || GrafanaClient.DashboardUrl(s.GrafanaUrl, "https://evil.test/d") != null || GrafanaClient.DashboardUrl(s.GrafanaUrl, "/d/x#frag") != null || GrafanaClient.DashboardUrl("http://grafana.example.test", "/d/x") != null) throw new Exception("dashboard URL rules");
            if (GrafanaClient.ValidateUrl("https://grafana.example.test") != null || GrafanaClient.ValidateUrl("http://grafana.example.test") == null || GrafanaClient.ValidateUrl("javascript:alert(1)") == null) throw new Exception("URL rules");
            fg.Status = 401;
            var e401 = await Throws(() => GrafanaClient.DashboardsAsync(s.GrafanaUrl, "bad", 0));
            if (e401 == null || !e401.Message.Contains("refused the token") || e401.Message.Contains("bad")) throw new Exception("401: " + e401?.Message);
            fg.Status = 200;
            await view.LoadDashboardsForTestAsync();
            view.Measure(new Size(900, 700)); view.Arrange(new Rect(0, 0, 900, 700));
            if (view.DashboardCountForTest != 2 || view.LinkButtonsForTest != 3) throw new Exception($"screen: {view.DashboardCountForTest} dashboards, {view.LinkButtonsForTest} link buttons (Grafana + 2 valid links; the http and nonsense ones dropped)");
            var (l1, u1) = ObservabilityView.ParseLink("Kibana=https://k.test/x=y");
            if (l1 != "Kibana" || u1 != "https://k.test/x=y" || ObservabilityView.ParseLink("nolink").Label != null || ObservabilityView.ParseLink("=https://x").Label != null) throw new Exception("link parsing");
            view.Dispose();
            if (!main.NavKeys.Contains("observe")) throw new Exception("no sidebar entry");
            ok("Observability screen: sinks with state, kind and summary, 60 activity bars, Grafana's dashboards listed through its API (Bearer token, X-Grafana-Org-Id, read-only search), a refused token explained without echoing it, dashboard URLs only ever on the Grafana server (other hosts, //, https: and #fragments refused), org links parsed as Label=https://… with plain-http ones dropped; sidebar entry “observe”");
        }
        catch (Exception ex) { fail("observability screen", ex); }
        finally
        {
            GrafanaClient.HandlerForTest = null; GrafanaClient.ResetForTest();
            try { LogShipper.Stop(); } catch { }
            LogShipper.ResetForTest();
            LogShipper.AzureToken = tokenWas;
            fake?.Dispose();
            (s.ObserveEnabled, s.ObserveSinks, _, s.ObserveSendAudit, s.ObserveSendAppLog, s.ObserveSendSessionLogs, s.GrafanaUrl, s.GrafanaOrgId, _) = saved;
            s.ObserveSecrets = saved.Item3; s.ObserveLinks = saved.Item9;
            AppSettings.Save();
            LogShipper.Start();
        }
    }

    private static async Task<Exception?> Throws(Func<Task> f) { try { await f(); return null; } catch (Exception ex) { return ex; } }

    private static async Task Until(Func<bool> cond, int ms, string why)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!cond()) { if (sw.ElapsedMilliseconds > ms) throw new TimeoutException(why); await Task.Delay(50); }
    }

    /// <summary>A Grafana search API in process.</summary>
    private sealed class FakeGrafana : HttpMessageHandler
    {
        public string? LastAuth, LastOrg, LastPath;
        public int Status = 200;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            LastAuth = req.Headers.Authorization?.ToString(); LastOrg = req.Headers.TryGetValues("X-Grafana-Org-Id", out var o) ? o.FirstOrDefault() : null; LastPath = req.RequestUri!.PathAndQuery;
            var body = Status == 200
                ? "[{\"uid\":\"abc\",\"title\":\"Hosts\",\"url\":\"/d/abc/hosts\",\"folderTitle\":\"Infra\",\"tags\":[\"linux\"]},{\"uid\":\"def\",\"title\":\"Mail\",\"url\":\"/d/def/mail\",\"tags\":[]},{\"uid\":\"evil\",\"title\":\"Evil\",\"url\":\"https://evil.test/d/x\"}]"
                : "{\"message\":\"invalid API key\"}";
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)Status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    /// <summary>A TLS HTTP server on 127.0.0.1 that records every POST (method, path, headers, body) and answers as each
    /// sink kind would: Elastic 200 {"errors":false}, Loki / Azure Monitor 204, Splunk 200 code 0, GELF 202, else 200.</summary>
    private sealed class FakeSinkServer : IDisposable
    {
        public sealed record Request(string Method, string Path, Dictionary<string, string> Headers, string Body, bool Tls);
        private readonly TcpListener _l = new(IPAddress.Loopback, 0);
        private readonly X509Certificate2 _cert;
        private readonly CancellationTokenSource _cts = new();
        private readonly List<Request> _requests = new();
        public int Port { get; }
        public IReadOnlyList<Request> Requests { get { lock (_requests) return _requests.ToList(); } }
        public string Summary { get { lock (_requests) return _requests.Count + " request(s): " + string.Join(", ", _requests.Select(r => r.Path)); } }

        public FakeSinkServer(X509Certificate2 cert)
        {
            _cert = cert;
            _l.Start();
            Port = ((IPEndPoint)_l.LocalEndpoint).Port;
            _ = Task.Run(Accept);
        }

        private async Task Accept()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient c;
                try { c = await _l.AcceptTcpClientAsync(_cts.Token); } catch { return; }
                _ = Task.Run(() => Serve(c));
            }
        }

        private async Task Serve(TcpClient c)
        {
            using (c)
            {
                try
                {
                    using var ssl = new SslStream(c.GetStream(), false);
                    await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = _cert, EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13 }, _cts.Token);
                    while (!_cts.IsCancellationRequested)
                    {
                        var head = new MemoryStream(); var one = new byte[1]; int match = 0;
                        while (match < 4)
                        {
                            int n = await ssl.ReadAsync(one, _cts.Token);
                            if (n == 0) return;
                            head.WriteByte(one[0]);
                            match = one[0] == (match % 2 == 0 ? '\r' : '\n') ? match + 1 : one[0] == '\r' ? 1 : 0;
                        }
                        var lines = Encoding.ASCII.GetString(head.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                        var first = lines[0].Split(' ');
                        var headers = lines.Skip(1).Select(l => l.Split(':', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0].Trim().ToLowerInvariant(), p => p[1].Trim());
                        var len = headers.TryGetValue("content-length", out var cl) && int.TryParse(cl, out var li) ? li : 0;
                        var body = new byte[len]; int got = 0;
                        while (got < len) { int n = await ssl.ReadAsync(body.AsMemory(got), _cts.Token); if (n == 0) return; got += n; }
                        var path = first.Length > 1 ? first[1] : "/";
                        lock (_requests) _requests.Add(new Request(first[0], path, headers, Encoding.UTF8.GetString(body), true));
                        var (status, answer) = path.Split('?')[0] switch
                        {
                            "/_bulk" => (200, "{\"took\":1,\"errors\":false,\"items\":[]}"),
                            "/loki/api/v1/push" => (204, ""),
                            var p when p.StartsWith("/dataCollectionRules/") => (204, ""),
                            "/services/collector/event" => (200, "{\"text\":\"Success\",\"code\":0}"),
                            "/gelf" => (202, ""),
                            _ => (200, "{}"),
                        };
                        var bytes = Encoding.UTF8.GetBytes(answer);
                        var reason = status switch { 200 => "OK", 202 => "Accepted", 204 => "No Content", _ => "x" };
                        await ssl.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {reason}\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\n\r\n"), _cts.Token);
                        if (bytes.Length > 0) await ssl.WriteAsync(bytes, _cts.Token);
                        await ssl.FlushAsync(_cts.Token);
                    }
                }
                catch { /* refused handshakes (the wrong pin) and closed connections are expected */ }
            }
        }

        public void Dispose() { _cts.Cancel(); try { _l.Stop(); } catch { } }
    }
}
