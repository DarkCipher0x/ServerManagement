using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DcxManager.Services;

namespace DcxManager.Infra;

/// <summary>
/// Red Hat Satellite / Foreman with Katello through their REST API (0.3.0 §17).
/// <list type="bullet">
/// <item><b>Security:</b> https only, the certificate checked by Windows or pinned on first use (ADR-007); a dedicated user
/// with a Personal Access Token from the vault (Basic authentication inside TLS — never in a URL); read-only until
/// "Allow changes"; Satellite's own roles decide what the token may do (a 403 is shown as such).</item>
/// <item><b>Reads:</b> status and version, hosts (OS, global status, errata counts, upgradable packages, lifecycle
/// environment and content view, subscription status, last report), organisations, lifecycle environments, content views,
/// repositories (last sync), subscriptions (usage, end date), running and failed tasks.</item>
/// <item><b>Changes</b> (each followed to the end of its Foreman task / job): repository sync, content view publish and
/// promote, install applicable security errata on a host, run a command on a host through remote execution
/// (PRODUCTION confirmations as everywhere).</item>
/// </list>
/// </summary>
public sealed class SatelliteProvider : IInfraProvider, ITlsPinned
{
    private readonly ProviderConfig _c;
    private readonly HttpClient _http;

    public ProviderConfig Config => _c;
    public string Where => _c.Endpoint;
    public NetClass? NetworkClass => NetworkPolicy.Classify(_c.Endpoint);
    public string LastSeenSha256 { get; private set; } = "";
    public TlsDecision LastTls { get; private set; } = TlsDecision.Accept;
    public TimeSpan VerifyFor { get; set; } = TimeSpan.FromMinutes(30);
    public TimeSpan PollEvery { get; set; } = TimeSpan.FromSeconds(3);

    public SatelliteProvider(ProviderConfig c, HttpMessageHandler? handler = null)
    {
        _c = c;
        handler ??= TlsPin.Handler(() => _c.TlsSha256, (sha, d) => { LastSeenSha256 = sha; LastTls = d; });
        // 0.6.17 (ADR-096): signed when each request is sent (the credential may be kept in Bitwarden or Password Safe).
        handler = new CredentialAuthHandler(() => _c.CredentialId, "Satellite " + c.Name, (req, cred) =>
        {
            if (cred.Username.Length > 0) req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(cred.Username + ":" + cred.Secret)));
        }, handler);
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        if (Uri.TryCreate(_c.Endpoint.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var b)) _http.BaseAddress = b;
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public static string? ValidateEndpoint(string url) =>
        !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) ? "Enter the Satellite / Foreman address, e.g. https://satellite.example.lan"
        : u.Scheme != Uri.UriSchemeHttps ? "Satellite / Foreman must be reached over https (the token would otherwise travel in clear text)."
        : null;

    private async Task<JsonNode?> Call(HttpMethod m, string path, CancellationToken ct, JsonObject? body = null)
    {
        using var req = new HttpRequestMessage(m, path);
        if (body != null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var r = await _http.SendAsync(req, ct);
        var text = await r.Content.ReadAsStringAsync(ct);
        if (r.StatusCode == HttpStatusCode.Unauthorized) throw new UnauthorizedAccessException("the user name or Personal Access Token was rejected");
        if (r.StatusCode == HttpStatusCode.Forbidden) throw new UnauthorizedAccessException("this user's roles don't allow it (403)");
        if (!r.IsSuccessStatusCode)
        {
            string? msg = null;
            try { var err = text.TrimStart().StartsWith('{') ? JsonNode.Parse(text) : null; msg = (err?["error"]?["message"] ?? err?["displayMessage"] ?? err?["error"]?["full_messages"]?[0])?.ToString(); }
            catch (System.Text.Json.JsonException) { }
            throw new InvalidOperationException($"HTTP {(int)r.StatusCode}: {Redaction.Mask(msg ?? r.ReasonPhrase ?? "")}");
        }
        return text.Length == 0 ? null : JsonNode.Parse(text);
    }

    private static string S(JsonNode? o, string k) => o?[k] is JsonValue v ? v.ToString() : "";
    private static Dictionary<string, string> Props(JsonObject o) => o.Where(kv => kv.Value is JsonValue).Take(60).ToDictionary(kv => kv.Key, kv => Redaction.Mask(kv.Value!.ToString()));
    private static IEnumerable<JsonObject> Results(JsonNode? n) => (n?["results"] as JsonArray ?? n as JsonArray ?? new JsonArray()).OfType<JsonObject>();

    public async Task<ProviderCaps> DetectAsync(CancellationToken ct)
    {
        if (ValidateEndpoint(_c.Endpoint) is { } bad) return ProviderCaps.Unreachable(bad);
        if (AppData.Current.FindCredential(_c.CredentialId) is not { Username.Length: > 0 } sc || !ExternalCredentials.HasSecret(sc)) return ProviderCaps.Unreachable("choose a vault credential: the Satellite user name and a Personal Access Token as its password");
        try
        {
            var st = await Call(HttpMethod.Get, "api/status", ct);
            var me = await Call(HttpMethod.Get, "api/v2/current_user", ct);
            bool admin = S(me, "admin") == "true";
            var notes = new List<string>();
            if (admin) notes.Add("⚠ This user is a Satellite administrator: a dedicated user with only the roles you need (Viewer, plus e.g. Remote Execution User, Content … Manager) is safer.");
            bool katello = true;
            try { await Call(HttpMethod.Get, "katello/api/organizations?per_page=1", ct); } catch (InvalidOperationException) { katello = false; notes.Add("Katello (content, errata, subscriptions) isn't installed: hosts and tasks only."); }
            catch (UnauthorizedAccessException) { notes.Add("This user can't read organizations: content views, repositories and subscriptions stay hidden (add the Viewer role)."); }
            return new ProviderCaps(true, true, true, S(st, "version"), $"{(katello ? "Satellite / Foreman + Katello" : "Foreman")} {S(st, "version")} · user {S(me, "login")}", notes);
        }
        catch (HttpRequestException ex) when (ex.InnerException is AuthenticationException || LastTls != TlsDecision.Accept)
        {
            return ProviderCaps.Unreachable(TlsPin.UntrustedMessage(LastTls, LastSeenSha256) + " (On the server: the katello-default-ca / your CA's certificate.)");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UnauthorizedAccessException or InvalidOperationException)
        {
            return ProviderCaps.Unreachable(ex is TaskCanceledException ? "timed out" : ex.Message);
        }
    }

    public async Task<IReadOnlyList<InfraObject>> InventoryAsync(CancellationToken ct)
    {
        var list = new List<InfraObject>();
        async Task Try(Func<Task> f) { try { await f(); } catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException) { } }
        await Try(async () =>
        {
            foreach (var h in Results(await Call(HttpMethod.Get, "api/v2/hosts?per_page=1000&thin=false", ct)))
            {
                var cf = h["content_facet_attributes"];
                var errata = cf?["errata_counts"];
                var p = Props(h);
                foreach (var k in new[] { "security", "bugfix", "enhancement", "total" }) if (S(errata, k) is { Length: > 0 } v) p["errata_" + k] = v;
                if (S(cf, "upgradable_package_count") is { Length: > 0 } up) p["upgradable"] = up;
                p["lifecycle"] = S(cf, "lifecycle_environment_name"); p["content_view"] = S(cf, "content_view_name");
                list.Add(new InfraObject("host:" + S(h, "id"), "host", S(h, "name"), S(h, "global_status_label").ToLowerInvariant() is { Length: > 0 } gs ? gs : "unknown", S(h, "organization_name"),
                    $"{S(h, "operatingsystem_name")} · {S(h, "ip")} · errata {S(errata, "security")} security / {S(errata, "total")} total · {S(cf, "lifecycle_environment_name")} / {S(cf, "content_view_name")}".Trim(' ', '·'), p));
            }
        });
        var orgs = new List<(string Id, string Name)>();
        await Try(async () =>
        {
            foreach (var o in Results(await Call(HttpMethod.Get, "katello/api/organizations?per_page=100", ct)))
            {
                orgs.Add((S(o, "id"), S(o, "name")));
                list.Add(new InfraObject("org:" + S(o, "id"), "organization", S(o, "name"), "active", "", S(o, "label"), Props(o)));
            }
        });
        foreach (var (oid, oname) in orgs.Take(20))
        {
            if (!Regex.IsMatch(oid, "^[0-9]{1,9}$")) continue;
            await Try(async () =>
            {
                foreach (var e in Results(await Call(HttpMethod.Get, $"katello/api/environments?organization_id={oid}&per_page=200", ct)))
                    list.Add(new InfraObject("env:" + S(e, "id"), "lifecycle", S(e, "name"), "active", oname, S(e?["prior"], "name") is { Length: > 0 } pr ? "after " + pr : "Library", Props(e!)));
            });
            await Try(async () =>
            {
                foreach (var v in Results(await Call(HttpMethod.Get, $"katello/api/content_views?organization_id={oid}&per_page=200&nondefault=true", ct)))
                {
                    var p = Props(v!);
                    p["latest_version_id"] = S(v, "latest_version_id") is { Length: > 0 } lv ? lv
                        : (v?["versions"] as JsonArray)?.OfType<JsonObject>().OrderByDescending(x => double.TryParse(S(x, "version"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0).Select(x => S(x, "id")).FirstOrDefault() ?? "";
                    list.Add(new InfraObject("cv:" + S(v, "id"), "content view", S(v, "name"), S(v, "composite") == "true" ? "composite" : "available", oname,
                        $"latest {S(v, "latest_version")} · published {S(v, "last_published")} · in {string.Join(", ", (v?["environments"] as JsonArray ?? new()).OfType<JsonObject>().Select(x => S(x, "name")))}", p));
                }
            });
            await Try(async () =>
            {
                foreach (var r in Results(await Call(HttpMethod.Get, $"katello/api/repositories?organization_id={oid}&per_page=500", ct)))
                    list.Add(new InfraObject("repo:" + S(r, "id"), "repository", S(r, "name"), S(r?["last_sync"], "result") is { Length: > 0 } res ? res : S(r?["last_sync"], "state") is { Length: > 0 } st ? st : "never synced", S(r?["product"], "name"),
                        $"{S(r, "content_type")} · last sync {S(r?["last_sync"], "ended_at")}{S(r, "last_sync_words")}".Trim(), Props(r!)));
            });
            await Try(async () =>
            {
                foreach (var s in Results(await Call(HttpMethod.Get, $"katello/api/subscriptions?organization_id={oid}&per_page=200", ct)))
                    list.Add(new InfraObject("sub:" + S(s, "id"), "subscription", S(s, "name"), DateTime.TryParse(S(s, "end_date"), out var end) && end < DateTime.Now.AddDays(30) ? "expiring" : "active", oname,
                        $"{S(s, "consumed")} of {S(s, "quantity")} used · ends {S(s, "end_date")}", Props(s!)));
            });
        }
        await Try(async () =>
        {
            foreach (var t in Results(await Call(HttpMethod.Get, "foreman_tasks/api/tasks?per_page=50&search=" + Uri.EscapeDataString("state = running or result = error or result = warning"), ct)))
                list.Add(new InfraObject("task:" + S(t, "id"), "task", S(t, "action") is { Length: > 0 } act ? act : S(t, "label"), S(t, "state") == "running" ? "running" : S(t, "result"), "tasks",
                    $"started {S(t, "started_at")} · {S(t, "result")}", Props(t!)));
        });
        return list;
    }

    // ---- actions ------------------------------------------------------------------------------------------------------

    public static readonly InfraAction Sync = new("sync", "⟳ Sync repository", true, Consequence.None, null, "Synchronises the repository from its upstream now (a Foreman task; followed to its end).");
    public static readonly InfraAction Publish = new("publish", "⇪ Publish new version…", true, Consequence.None, null, "Publishes a new version of the content view (a Foreman task; followed to its end).")
    { Inputs = new[] { new InfraInput("description", "Description", "Published from DarkCipher ServerManagement", Required: false) } };
    public static readonly InfraAction Promote = new("promote", "⇢ Promote latest version…", true, Consequence.None, null, "Promotes the content view's latest version to a lifecycle environment; hosts there then get its content.")
    { Inputs = new[] { new InfraInput("environment", "Lifecycle environment", Hint: "its name, e.g. Production") } };
    public static readonly InfraAction Errata = new("errata", "⛨ Install applicable security errata", true, Consequence.ServiceInterruption, null, "Runs remote execution on the host to install its applicable security errata (packages are updated; services may restart).");
    public static readonly InfraAction Command = new("command", "⌨ Run a command (remote execution)…", true, Consequence.ServiceInterruption, null, "Runs one command on the host through Satellite's remote execution and shows the output.")
    { Inputs = new[] { new InfraInput("command", "Command", Hint: "e.g. uptime; df -h") } };
    public static readonly InfraAction Details = new("details", "ⓘ All fields", false, Consequence.None, null, "Every field Satellite reported for this item (secrets masked).");

    public IReadOnlyList<InfraAction> ActionsFor(InfraObject o) => o.Kind switch
    {
        "repository" => new[] { Sync, Details },
        "content view" => new[] { Publish, Promote, Details },
        "host" => new[] { Errata, Command, Details },
        _ => new[] { Details },
    };

    public string? Forbidden(InfraAction a, InfraObject o, ProviderCaps caps) => a.Id switch
    {
        "errata" when o.Props.GetValueOrDefault("errata_security") is "0" => "no applicable security errata",
        _ => null,
    };

    public Task<ActionResult> RunAsync(InfraAction a, InfraObject o, CancellationToken ct) => RunAsync(a, o, new Dictionary<string, string>(), ct);

    public async Task<ActionResult> RunAsync(InfraAction a, InfraObject o, IReadOnlyDictionary<string, string> input, CancellationToken ct)
    {
        foreach (var i in a.Inputs) if (i.Validate(input.GetValueOrDefault(i.Name) ?? "") is { } bad) return new ActionResult(false, false, "refused: " + bad);
        if (a.Id == "details") return new ActionResult(true, true, "fields", string.Join("\n", o.Props.OrderBy(p => p.Key).Select(p => $"{p.Key,-32} {p.Value}")));
        var id = o.Id.Split(':').Last();
        if (!Regex.IsMatch(id, "^[0-9]{1,9}$")) return new ActionResult(false, false, "refused: unexpected id");
        try
        {
            switch (a.Id)
            {
                case "sync":
                    return await FollowTask(await Call(HttpMethod.Post, $"katello/api/repositories/{id}/sync", ct, new JsonObject()), "repository synchronised", ct);
                case "publish":
                    return await FollowTask(await Call(HttpMethod.Post, $"katello/api/content_views/{id}/publish", ct, new JsonObject { ["description"] = input.GetValueOrDefault("description") ?? "" }), "new version published", ct);
                case "promote":
                {
                    var version = o.Props.GetValueOrDefault("latest_version_id") ?? "";
                    if (!Regex.IsMatch(version, "^[0-9]{1,9}$")) return new ActionResult(false, false, "refused: the content view has no published version");
                    var org = Regex.IsMatch(o.Props.GetValueOrDefault("organization_id") ?? "", "^[0-9]+$") ? o.Props["organization_id"] : "";
                    var envs = Results(await Call(HttpMethod.Get, $"katello/api/environments?{(org.Length > 0 ? "organization_id=" + org + "&" : "")}per_page=200", ct));
                    var env = envs.FirstOrDefault(e => S(e, "name").Equals(input.GetValueOrDefault("environment"), StringComparison.OrdinalIgnoreCase));
                    if (env == null) return new ActionResult(false, false, "refused: no lifecycle environment with that name");
                    return await FollowTask(await Call(HttpMethod.Post, $"katello/api/content_view_versions/{version}/promote", ct, new JsonObject { ["environment_ids"] = new JsonArray(int.Parse(S(env, "id"))) }), $"promoted to {S(env, "name")}", ct);
                }
                case "errata" or "command":
                {
                    var host = o.Name;
                    if (!Regex.IsMatch(host, @"^[A-Za-z0-9][A-Za-z0-9.\-]{0,252}$")) return new ActionResult(false, false, "refused: unexpected host name");
                    JsonObject job;
                    if (a.Id == "errata")
                        job = new JsonObject { ["feature"] = "katello_errata_install_by_search", ["inputs"] = new JsonObject { ["Errata search query"] = "type = security" }, ["search_query"] = "name = " + host, ["targeting_type"] = "static_query" };
                    else
                    {
                        var tpl = Results(await Call(HttpMethod.Get, "api/job_templates?search=" + Uri.EscapeDataString("name = \"Run Command - Script Default\""), ct)).FirstOrDefault()
                                  ?? Results(await Call(HttpMethod.Get, "api/job_templates?search=" + Uri.EscapeDataString("name ~ \"Run Command\""), ct)).FirstOrDefault();
                        if (tpl == null) return new ActionResult(false, false, "refused: no “Run Command” job template on this Satellite");
                        job = new JsonObject { ["job_template_id"] = int.Parse(S(tpl, "id")), ["inputs"] = new JsonObject { ["command"] = input.GetValueOrDefault("command") ?? "" }, ["search_query"] = "name = " + host, ["targeting_type"] = "static_query" };
                    }
                    var inv = await Call(HttpMethod.Post, "api/job_invocations", ct, new JsonObject { ["job_invocation"] = job });
                    var jid = S(inv, "id");
                    AuditLog.Write("you", "infra:" + _c.Name, a.Id == "errata" ? "security errata install started" : "remote execution started", $"{host} · job {jid}");
                    return await FollowJob(jid, host, ct);
                }
                default: return new ActionResult(false, false, "refused: unknown action");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or UnauthorizedAccessException) { return new ActionResult(false, false, "refused: " + ex.Message); }
    }

    /// <summary>Follows a Foreman task to its end: verified only when it stopped with result "success".</summary>
    private async Task<ActionResult> FollowTask(JsonNode? started, string what, CancellationToken ct)
    {
        var tid = S(started, "id");
        if (!Regex.IsMatch(tid, "^[0-9a-f-]{8,40}$")) return new ActionResult(true, false, "requested, but no task id came back to follow");
        var until = DateTime.UtcNow + VerifyFor;
        string state = "", result = "";
        while (DateTime.UtcNow < until)
        {
            var t = await Call(HttpMethod.Get, $"foreman_tasks/api/tasks/{tid}", ct);
            state = S(t, "state"); result = S(t, "result");
            if (state is "stopped" or "paused") break;
            await Task.Delay(PollEvery, ct);
        }
        return state == "stopped" && result == "success" ? new ActionResult(true, true, $"verified: {what} (task {tid[..8]} ended with success)")
             : state == "stopped" ? new ActionResult(true, false, $"the task ended with “{result}” — see Satellite → Monitor → Tasks ({tid[..8]})")
             : new ActionResult(true, false, $"requested; the task is still “{state}” — see Satellite → Monitor → Tasks ({tid[..8]})");
    }

    /// <summary>Follows a remote-execution job and returns the host's output.</summary>
    private async Task<ActionResult> FollowJob(string jid, string host, CancellationToken ct)
    {
        if (!Regex.IsMatch(jid, "^[0-9]{1,9}$")) return new ActionResult(true, false, "requested, but no job id came back");
        var until = DateTime.UtcNow + VerifyFor;
        JsonNode? j = null;
        while (DateTime.UtcNow < until)
        {
            j = await Call(HttpMethod.Get, $"api/job_invocations/{jid}", ct);
            if (S(j, "status_label") is "succeeded" or "failed" or "cancelled" || S(j?["task"], "state") == "stopped") break;
            await Task.Delay(PollEvery, ct);
        }
        string output = "";
        try
        {
            var hid = Results(await Call(HttpMethod.Get, $"api/job_invocations/{jid}/hosts", ct)).FirstOrDefault() is { } h ? S(h, "id") : "";
            if (Regex.IsMatch(hid, "^[0-9]+$"))
                output = string.Concat((await Call(HttpMethod.Get, $"api/job_invocations/{jid}/hosts/{hid}", ct))?["output"] is JsonArray lines ? lines.OfType<JsonObject>().Select(l => S(l, "output")) : []);
        }
        catch (InvalidOperationException) { }
        var label = S(j, "status_label");
        return new ActionResult(true, label == "succeeded", label == "succeeded" ? $"verified: job {jid} succeeded on {host}" : $"job {jid} on {host}: {label}", Redaction.Mask(output));
    }
}
