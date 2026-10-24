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
/// VMware vCenter through the vSphere Automation REST API (vCenter 7.0 and later, base path <c>/api</c>), ADR-028.
/// <list type="bullet">
/// <item>Sign-in: <c>POST /api/session</c> with the vault credential (user@domain + password) as Basic authentication, once;
/// afterwards only the session id travels (<c>vmware-api-session-id</c>). The session is ended when the provider is
/// replaced, and renewed once after a 401. The password never reaches a message, log line or the audit log.</item>
/// <item>TLS: pinned on first use like Proxmox (self-signed vCenter certificates are the norm); https only.</item>
/// <item>Inventory: VMs, hosts, clusters, datastores and networks. Changes: VM power (start, guest shut down / restart,
/// hard power-off, reset, suspend), clone, vCPU count, memory size and delete; host disconnect / reconnect — each verified
/// by re-reading (ADR-031). Read: guest identity, guest network interfaces and VMware Tools state.</item>
/// <item>vCenter decides what the account may do: the API has no "what may I do" call, so a refused change is reported
/// as refused, with vCenter's reason.</item>
/// <item>Not offered (not in this API, or not verified): standalone ESXi hosts (they have no Automation API), snapshots
/// (only in the SOAP Web Services API), maintenance mode (SOAP only), templates from content libraries, VM creation from
/// scratch, vMotion/relocate, console tickets (they need VMRC or WebMKS).</item>
/// </list>
/// </summary>
public sealed class VSphereProvider : IInfraProvider, ITlsPinned, IDisposable
{
    private readonly ProviderConfig _c;
    private readonly HttpClient _http;
    private string? _session;

    public ProviderConfig Config => _c;
    public string Where => _c.Endpoint;
    public NetClass? NetworkClass => NetworkPolicy.Classify(_c.Endpoint);
    public string LastSeenSha256 { get; private set; } = "";
    public TlsDecision LastTls { get; private set; } = TlsDecision.Accept;
    public TimeSpan VerifyFor { get; set; } = TimeSpan.FromSeconds(180);
    public TimeSpan PollEvery { get; set; } = TimeSpan.FromSeconds(2);

    public VSphereProvider(ProviderConfig c, HttpMessageHandler? handler = null)
    {
        _c = c;
        handler ??= TlsPin.Handler(() => _c.TlsSha256, (sha, d) => { LastSeenSha256 = sha; LastTls = d; });
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
    }

    public static string? ValidateEndpoint(string url) =>
        !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) ? "Enter the vCenter address, e.g. https://vcenter.lan"
        : u.Scheme != Uri.UriSchemeHttps ? "vCenter must be reached over https (the password and session would otherwise travel in clear text)."
        : u.AbsolutePath.Trim('/').Length > 0 ? "Enter only the vCenter address (no path), e.g. https://vcenter.lan"
        : null;

    private string Api(string path) => _c.Endpoint.Trim().TrimEnd('/') + "/api" + path;

    /// <summary>vCenter object ids look like vm-123, host-12, datastore-15, network-17, domain-c8.</summary>
    public static bool ValidId(string id) => Regex.IsMatch(id, "^[A-Za-z0-9][A-Za-z0-9-]{0,79}$");

    private async Task SignInAsync(CancellationToken ct)
    {
        if (await ExternalCredentials.UseAsync(_c.CredentialId, "vCenter " + _c.Name, ct) is not { Username.Length: > 0, Secret.Length: > 0 } cred)
            throw new UnauthorizedAccessException("choose a vault credential with the vCenter user (e.g. svc-dcx@vsphere.local) and its password");
        using var req = new HttpRequestMessage(HttpMethod.Post, Api("/session"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(cred.Username + ":" + cred.Secret)));
        using var r = await _http.SendAsync(req, ct);
        var text = await r.Content.ReadAsStringAsync(ct);
        if (r.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new UnauthorizedAccessException("vCenter refused the user name or password");
        if (r.StatusCode == HttpStatusCode.NotFound) throw new InvalidOperationException("this server has no vSphere Automation API at /api (vCenter 7.0 or later is needed; a standalone ESXi host has none)");
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"vCenter sign-in failed (HTTP {(int)r.StatusCode})");
        _session = (JsonNode.Parse(text) as JsonValue)?.GetValue<string>() ?? throw new InvalidOperationException("vCenter returned no session id");
    }

    /// <summary>One API call with the session; a 401 renews the session once (the call was refused, so repeating is safe).</summary>
    private async Task<JsonNode?> Call(HttpMethod m, string path, JsonNode? body, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            if (_session == null) await SignInAsync(ct);
            using var req = new HttpRequestMessage(m, Api(path));
            req.Headers.TryAddWithoutValidation("vmware-api-session-id", _session);
            if (body != null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var r = await _http.SendAsync(req, ct);
            var text = await r.Content.ReadAsStringAsync(ct);
            if (r.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) { _session = null; continue; }
            if (r.StatusCode == HttpStatusCode.Unauthorized) throw new UnauthorizedAccessException("vCenter ended the session and refused a new one");
            if (r.StatusCode == HttpStatusCode.Forbidden) throw new UnauthorizedAccessException("vCenter: this account isn't allowed to do this" + Reason(text));
            if (r.StatusCode == HttpStatusCode.NotFound) throw new NotFoundException("vCenter: not found" + Reason(text));
            if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"vCenter answered HTTP {(int)r.StatusCode}" + Reason(text));
            return text.Length == 0 ? null : JsonNode.Parse(text);
        }
    }

    /// <summary>HTTP 404 from the API (an object that no longer exists).</summary>
    public sealed class NotFoundException(string message) : InvalidOperationException(message);

    /// <summary>vCenter errors carry localizable messages: { "error_type", "messages": [ { "default_message" } ] }.</summary>
    internal static string Reason(string text)
    {
        try
        {
            if (JsonNode.Parse(text) is JsonObject o)
            {
                var msg = (o["messages"] as JsonArray)?.Select(x => x?["default_message"]?.ToString()).FirstOrDefault(x => x is { Length: > 0 });
                var type = o["error_type"]?.ToString();
                var s = msg ?? type;
                if (s is { Length: > 0 }) return ": " + (s.Length > 240 ? s[..240] + "…" : s.Split('\n')[0]);
            }
        }
        catch (System.Text.Json.JsonException) { }
        return "";
    }

    public async Task<ProviderCaps> DetectAsync(CancellationToken ct)
    {
        if (ValidateEndpoint(_c.Endpoint) is { } bad) return ProviderCaps.Unreachable(bad);
        try
        {
            _session = null;
            await SignInAsync(ct);
            var notes = new List<string>
            {
                "vCenter decides per action what this account may do (the API can't list privileges): a refused change is reported with vCenter's reason. Least privilege: a dedicated user with Read-only, plus Virtual machine › Interaction › Power on/off (› Provisioning › Clone, › Change configuration › CPU count / Memory, › Inventory › Remove; Host › Inventory › Modify for disconnect) where you want changes.",
                "Not offered here: snapshots and maintenance mode (not in the vSphere Automation REST API — only the SOAP API has them), content-library templates, new VMs from scratch, vMotion — use the vSphere Client for those.",
            };
            string version = "";
            try { var v = await Call(HttpMethod.Get, "/appliance/system/version", null, ct); version = $"{v?["version"]} (build {v?["build"]})"; }
            catch (UnauthorizedAccessException) { notes.Add("The appliance version isn't readable with this account (harmless)."); }
            catch (InvalidOperationException) { }
            await Call(HttpMethod.Get, "/vcenter/vm", null, ct);   // read access
            return new ProviderCaps(true, true, true, version, $"VMware vCenter {(version.Length > 0 ? version : "(version not exposed)")} · vSphere Automation API", notes);
        }
        catch (HttpRequestException ex) when (ex.InnerException is AuthenticationException || LastTls != TlsDecision.Accept)
        {
            return ProviderCaps.Unreachable(TlsPin.UntrustedMessage(LastTls, LastSeenSha256) + " (vCenter: Administration › Certificates, or the browser's certificate details.)");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException)
        {
            return ProviderCaps.Unreachable(ex is TaskCanceledException ? "timed out" : ex is System.Text.Json.JsonException ? "the server didn't answer like vCenter's API" : ex.Message);
        }
    }

    public async Task<IReadOnlyList<InfraObject>> InventoryAsync(CancellationToken ct)
    {
        var parts = new Dictionary<string, JsonArray>();
        foreach (var kind in new[] { "vm", "host", "cluster", "datastore", "network" })
            parts[kind] = await Call(HttpMethod.Get, "/vcenter/" + kind, null, ct) as JsonArray ?? new JsonArray();
        return Parse(parts);
    }

    public static string NormalizePower(string? s) => (s ?? "").ToUpperInvariant() switch
    {
        "POWERED_ON" => "running",
        "POWERED_OFF" => "stopped",
        "SUSPENDED" => "suspended",
        var x => x.Length > 0 ? x.ToLowerInvariant() : "unknown",
    };

    /// <summary>Host connection_state: CONNECTED → online, DISCONNECTED → disconnected, NOT_RESPONDING → not responding.</summary>
    public static string HostState(string? s) => (s ?? "").ToUpperInvariant() switch { "CONNECTED" => "online", "" => "unknown", var x => x.ToLowerInvariant().Replace('_', ' ') };

    public static List<InfraObject> Parse(IReadOnlyDictionary<string, JsonArray> parts)
    {
        var list = new List<InfraObject>();
        string S(JsonNode? n, string k) => n?[k]?.ToString() ?? "";
        static string Gb(string bytes) => long.TryParse(bytes, out var b) && b > 0 ? $"{b / (double)(1L << 30):0.#} GB" : "?";
        foreach (var o in parts.GetValueOrDefault("vm")?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
        {
            var id = S(o, "vm");
            if (!ValidId(id)) continue;   // untrusted answer: an id that couldn't be sent back safely is not listed
            list.Add(new InfraObject(id, "vm", S(o, "name"), NormalizePower(S(o, "power_state")), "",
                $"{S(o, "cpu_count")} vCPU · RAM {(int.TryParse(S(o, "memory_size_MiB"), out var mib) ? $"{mib / 1024.0:0.#} GB" : "?")}", new Dictionary<string, string> { ["vm"] = id }));
        }
        foreach (var o in parts.GetValueOrDefault("host")?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
        {
            if (!ValidId(S(o, "host"))) continue;
            list.Add(new InfraObject(S(o, "host"), "host", S(o, "name"), HostState(S(o, "connection_state")), "",
                $"power {S(o, "power_state").ToLowerInvariant()}", new Dictionary<string, string> { ["host"] = S(o, "host") }));
        }
        foreach (var o in parts.GetValueOrDefault("cluster")?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
            list.Add(new InfraObject(S(o, "cluster"), "cluster", S(o, "name"), "available", "", $"HA {(S(o, "ha_enabled") == "true" ? "on" : "off")} · DRS {(S(o, "drs_enabled") == "true" ? "on" : "off")}", new Dictionary<string, string>()));
        foreach (var o in parts.GetValueOrDefault("datastore")?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
            list.Add(new InfraObject(S(o, "datastore"), "datastore", S(o, "name"), "available", "", $"{S(o, "type")} · {Gb(S(o, "free_space"))} free of {Gb(S(o, "capacity"))}", new Dictionary<string, string>()));
        foreach (var o in parts.GetValueOrDefault("network")?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
            list.Add(new InfraObject(S(o, "network"), "network", S(o, "name"), "available", "", S(o, "type").Replace('_', ' ').ToLowerInvariant(), new Dictionary<string, string>()));
        return list;
    }

    // ---- actions ------------------------------------------------------------------------------------------------------

    public static readonly InfraAction Start = new("start", "▶ Power on", true, Consequence.None, "running", "Powers the VM on.");
    public static readonly InfraAction GuestShutdown = new("guest-shutdown", "⏻ Shut down guest OS", true, Consequence.ServiceInterruption, "stopped", "Asks the guest OS to shut down through VMware Tools (needs Tools running). Its services go down.");
    public static readonly InfraAction GuestReboot = new("guest-reboot", "↻ Restart guest OS", true, Consequence.ServiceInterruption, "running", "Asks the guest OS to restart through VMware Tools. Its services are briefly down.");
    public static readonly InfraAction Suspend = new("suspend", "‖ Suspend", true, Consequence.ServiceInterruption, "suspended", "Suspends the VM (memory saved to disk). It stops answering until resumed with Power on.");
    public static readonly InfraAction Stop = new("stop", "■ Power off (hard)", true, Consequence.DataLoss, "stopped", "Cuts the VM's power immediately: unsaved data inside it can be lost. Prefer Shut down guest OS.");
    public static readonly InfraAction Reset = new("reset", "⚡ Reset (hard)", true, Consequence.DataLoss, "running", "Hard reset, like the reset button: unsaved data inside the VM can be lost.");
    public static readonly InfraAction Clone = new("clone", "⧉ Clone…", true, Consequence.None, null, "Creates a copy of the VM in the same folder, resource pool and datastore (vCenter 7.0 U2 or later). The copy is left powered off; it uses as much storage as the original.")
    {
        Inputs = new[] { new InfraInput("name", "Name of the new VM", Hint: "a unique VM name") },
    };

    public static readonly InfraAction GuestInfo = new("guest-info", "ⓘ Guest & Tools info", false, Consequence.None, null, "Reads the guest OS identity, its network interfaces and the VMware Tools state (identity and interfaces need Tools running).");
    public static readonly InfraAction Cpu = new("cpu", "⚙ vCPU count…", true, Consequence.None, null, "Sets the number of virtual CPUs. While the VM runs, vCenter only allows an increase with CPU hot-add enabled; otherwise shut it down first (vCenter's reason is shown).")
    {
        Inputs = new[] { new InfraInput("count", "Number of vCPUs", Hint: "a multiple of the cores per socket", Number: true) },
    };
    public static readonly InfraAction Memory = new("memory", "⚙ Memory size…", true, Consequence.None, null, "Sets the VM's memory in MiB. While the VM runs, vCenter only allows an increase with memory hot-add enabled; otherwise shut it down first.")
    {
        Inputs = new[] { new InfraInput("sizeMiB", "Memory (MiB)", Hint: "e.g. 8192 for 8 GB", Number: true) },
    };
    public static readonly InfraAction Delete = new("delete", "✕ Delete VM", true, Consequence.DataLoss, null, "Deletes the powered-off VM from vCenter AND its files from the datastore (disks included). This can't be undone.");
    public static readonly InfraAction HostDisconnect = new("host-disconnect", "⏏ Disconnect host", true, Consequence.ServiceInterruption, "disconnected", "vCenter stops managing the host. Its VMs keep running, but HA, DRS and vMotion stop covering it until it's reconnected.");
    public static readonly InfraAction HostConnect = new("host-connect", "⇌ Reconnect host", true, Consequence.None, "online", "Reconnects a disconnected host to vCenter with the credentials vCenter already holds.");

    public IReadOnlyList<InfraAction> ActionsFor(InfraObject o) => o.Kind switch
    {
        "vm" => new[] { Start, GuestShutdown, GuestReboot, Suspend, Stop, Reset, Clone, GuestInfo, Cpu, Memory, Delete },
        "host" => new[] { HostDisconnect, HostConnect },
        _ => Array.Empty<InfraAction>(),
    };

    public string? Forbidden(InfraAction a, InfraObject o, ProviderCaps caps) => a.Id switch
    {
        "start" when o.Status == "running" => "already running",
        "guest-shutdown" or "guest-reboot" or "suspend" or "stop" or "reset" when o.Status != "running" => "not running",
        "delete" when o.Status != "stopped" => "power it off first (a running or suspended VM is never deleted)",
        "host-disconnect" when o.Status != "online" => "not connected",
        "host-connect" when o.Status == "online" => "already connected",
        _ => null,
    };

    public Task<ActionResult> RunAsync(InfraAction a, InfraObject o, CancellationToken ct) => RunAsync(a, o, new Dictionary<string, string>(), ct);

    public async Task<ActionResult> RunAsync(InfraAction a, InfraObject o, IReadOnlyDictionary<string, string> input, CancellationToken ct)
    {
        foreach (var i in a.Inputs) if (i.Validate(input.GetValueOrDefault(i.Name) ?? "") is { } bad) return new ActionResult(false, false, "refused: " + bad);
        if (o.Kind == "host") return await HostAsync(a, o, ct);
        var vm = o.Props.GetValueOrDefault("vm") ?? o.Id;
        if (o.Kind != "vm" || !ValidId(vm)) return new ActionResult(false, false, "refused: unexpected VM id");
        var id = Uri.EscapeDataString(vm);
        try
        {
            switch (a.Id)
            {
                case "guest-info":
                {
                    var sb = new StringBuilder();
                    foreach (var (what, path) in new[] { ("Guest identity", "/guest/identity"), ("Network interfaces", "/guest/networking/interfaces"), ("VMware Tools", "/tools") })
                    {
                        sb.Append(what).Append(":\n");
                        try { sb.Append(Indented(await Call(HttpMethod.Get, $"/vcenter/vm/{id}{path}", null, ct))).Append('\n'); }
                        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException) { sb.Append("  not available: ").Append(ex.Message).Append('\n'); }
                    }
                    return new ActionResult(true, true, "guest information read", sb.ToString());
                }
                case "cpu" or "memory":
                {
                    var n = long.Parse(input[a.Id == "cpu" ? "count" : "sizeMiB"]);
                    if (a.Id == "cpu" ? n is < 1 or > 768 : n is < 4 or > 25_165_824) return new ActionResult(false, false, a.Id == "cpu" ? "refused: 1 – 768 vCPUs" : "refused: 4 MiB – 24 TiB");
                    var (part, field) = a.Id == "cpu" ? ("cpu", "count") : ("memory", "size_MiB");
                    await Call(HttpMethod.Patch, $"/vcenter/vm/{id}/hardware/{part}", new JsonObject { [field] = n }, ct);
                    var now = (await Call(HttpMethod.Get, $"/vcenter/vm/{id}/hardware/{part}", null, ct))?[field]?.ToString();
                    return now == n.ToString() ? new ActionResult(true, true, $"verified: the VM now has {(a.Id == "cpu" ? $"{n} vCPU" : $"{n} MiB")}")
                        : new ActionResult(true, false, $"vCenter accepted it, but re-reading shows {field} = {now ?? "?"}");
                }
                case "delete":
                {
                    await Call(HttpMethod.Delete, $"/vcenter/vm/{id}", null, ct);
                    try { await Call(HttpMethod.Get, $"/vcenter/vm/{id}", null, ct); }
                    catch (NotFoundException) { return new ActionResult(true, true, "verified: the VM no longer exists in vCenter"); }
                    return new ActionResult(true, false, "vCenter accepted the delete, but the VM can still be read — refresh in a moment");
                }
                case "start" or "stop" or "suspend" or "reset":
                    await Call(HttpMethod.Post, $"/vcenter/vm/{id}/power?action={a.Id}", null, ct);
                    break;
                case "guest-shutdown" or "guest-reboot":
                    await Call(HttpMethod.Post, $"/vcenter/vm/{id}/guest/power?action={(a.Id == "guest-shutdown" ? "shutdown" : "reboot")}", null, ct);
                    break;
                case "clone":
                {
                    var name = input.GetValueOrDefault("name") ?? "";
                    if (Clone.Inputs[0].Validate(name) is { } bad) return new ActionResult(false, false, "refused: " + bad);
                    var created = await Call(HttpMethod.Post, "/vcenter/vm?action=clone", new JsonObject { ["source"] = vm, ["name"] = name, ["power_on"] = false }, ct);
                    var newId = (created as JsonValue)?.GetValue<string>() ?? "";
                    if (!ValidId(newId)) return new ActionResult(true, false, "vCenter accepted the clone but returned no VM id — refresh to check");
                    var made = await Call(HttpMethod.Get, $"/vcenter/vm/{Uri.EscapeDataString(newId)}", null, ct);
                    return made?["name"]?.ToString() == name
                        ? new ActionResult(true, true, $"verified: VM “{name}” ({newId}) exists, powered off")
                        : new ActionResult(true, false, $"vCenter returned {newId}, but re-reading it didn't show the name “{name}”");
                }
                default: return new ActionResult(false, false, "refused: unknown action");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return new ActionResult(false, false, "refused: " + ex.Message); }

        // Power calls return when vCenter has done them; the state is re-read until it matches (a guest shutdown takes a while).
        var until = DateTime.UtcNow + VerifyFor;
        string state = "?";
        while (DateTime.UtcNow < until)
        {
            state = NormalizePower((await Call(HttpMethod.Get, $"/vcenter/vm/{id}/power", null, ct))?["state"]?.ToString());
            if (state == a.ExpectedStatus) return new ActionResult(true, true, $"verified: the VM is now “{state}”");
            await Task.Delay(PollEvery, ct);
        }
        return new ActionResult(true, false, $"requested, but NOT verified: the VM reports “{state}” after {VerifyFor.TotalSeconds:0} s (expected “{a.ExpectedStatus}”)");
    }

    private async Task<ActionResult> HostAsync(InfraAction a, InfraObject o, CancellationToken ct)
    {
        var host = o.Props.GetValueOrDefault("host") ?? "";
        if (!ValidId(host) || a.Id is not ("host-disconnect" or "host-connect")) return new ActionResult(false, false, "refused: unexpected host id");
        try { await Call(HttpMethod.Post, $"/vcenter/host/{Uri.EscapeDataString(host)}?action={(a.Id == "host-connect" ? "connect" : "disconnect")}", null, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return new ActionResult(false, false, "refused: " + ex.Message); }
        var until = DateTime.UtcNow + VerifyFor;
        string state = "?";
        while (DateTime.UtcNow < until)
        {
            var hosts = await Call(HttpMethod.Get, "/vcenter/host", null, ct) as JsonArray ?? new JsonArray();
            state = HostState(hosts.OfType<JsonObject>().FirstOrDefault(h => h["host"]?.ToString() == host)?["connection_state"]?.ToString());
            if (state == a.ExpectedStatus) return new ActionResult(true, true, $"verified: the host is now “{state}”");
            await Task.Delay(PollEvery, ct);
        }
        return new ActionResult(true, false, $"requested, but NOT verified: the host reports “{state}” (expected “{a.ExpectedStatus}”)");
    }

    private static string Indented(JsonNode? n) =>
        string.Join("\n", (n?.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) ?? "(nothing)").Split('\n').Select(l => "  " + l.TrimEnd()));

    /// <summary>Ends the vCenter session (best effort) so it doesn't linger until vCenter's idle timeout.</summary>
    public void Dispose()
    {
        if (_session is not { } s) return;
        _session = null;
        _ = Task.Run(async () =>
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Delete, Api("/session"));
                req.Headers.TryAddWithoutValidation("vmware-api-session-id", s);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _http.SendAsync(req, cts.Token);
            }
            catch { }
        });
    }
}
