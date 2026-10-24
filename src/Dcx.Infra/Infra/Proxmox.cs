using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DcxManager.Services;

namespace DcxManager.Infra;

/// <summary>
/// Proxmox VE through its REST API with an API token (never a password, never root@pam's full rights by default). The
/// token's own privileges (/access/permissions) decide what can be offered: VM.Audit/Sys.Audit to read,
/// VM.PowerMgmt for power and pause, VM.Snapshot for snapshots, VM.Clone, VM.Migrate, VM.Backup (vzdump) and VM.Allocate
/// (template) for the rest (ADR-031); each action is disabled with the missing privilege named. Self-signed certificates are pinned on first use (TOFU): the user compares the SHA-256
/// shown with `pvenode cert info` on the host; a changed certificate is refused as a possible interception.
/// Changes run as Proxmox tasks; the result is reported only after the task ends AND the guest's status was re-read.
/// </summary>
public sealed class ProxmoxProvider : IInfraProvider, ITlsPinned
{
    private readonly ProviderConfig _c;
    private readonly HttpClient _http;
    private Dictionary<string, HashSet<string>> _perms = new();

    public ProviderConfig Config => _c;
    public string Where => _c.Endpoint;
    public NetClass? NetworkClass => NetworkPolicy.Classify(_c.Endpoint);
    /// <summary>SHA-256 (hex) of the certificate the server presented last time we connected.</summary>
    public string LastSeenSha256 { get; private set; } = "";
    public TlsDecision LastTls { get; private set; } = TlsDecision.Accept;
    /// <summary>Upper bound for waiting on a task + the status re-read (self-test shortens it).</summary>
    public TimeSpan VerifyFor { get; set; } = TimeSpan.FromSeconds(150);
    public TimeSpan PollEvery { get; set; } = TimeSpan.FromSeconds(1);

    public ProxmoxProvider(ProviderConfig c, HttpMessageHandler? handler = null)
    {
        _c = c;
        handler ??= TlsPin.Handler(() => _c.TlsSha256, (sha, d) => { LastSeenSha256 = sha; LastTls = d; });
        // 0.6.17 (ADR-096): signed when each request is sent (the credential may be kept in Bitwarden or Password Safe).
        handler = new CredentialAuthHandler(() => _c.CredentialId, "Proxmox " + c.Name, (req, cred) =>
        {
            if (cred.Username.Contains('!')) req.Headers.TryAddWithoutValidation("Authorization", $"PVEAPIToken={cred.Username}={cred.Secret}");
        }, handler);
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        if (Uri.TryCreate(Base, UriKind.Absolute, out var b)) _http.BaseAddress = b;
    }

    private string Base => _c.Endpoint.Trim().TrimEnd('/') + "/api2/json/";

    /// <summary>Endpoint sanity: https only (tokens never travel in clear text).</summary>
    public static string? ValidateEndpoint(string url) =>
        !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) ? "Enter the Proxmox address, e.g. https://pve.lan:8006"
        : u.Scheme != Uri.UriSchemeHttps ? "Proxmox must be reached over https (the API token would otherwise travel in clear text)."
        : null;

    public static TlsDecision TlsCheck(string pinned, string seen, SslPolicyErrors errors) => TlsPin.Check(pinned, seen, errors);

    /// <summary>Formats a hex SHA-256 like Proxmox shows it (AA:BB:…).</summary>
    public static string Pretty(string hex) => TlsPin.Pretty(hex);

    /// <summary>One API call. POST/PUT parameters travel form-encoded in the body (never in the path).</summary>
    private async Task<JsonNode?> Call(HttpMethod m, string path, CancellationToken ct, IEnumerable<KeyValuePair<string, string>>? form = null)
    {
        using var req = new HttpRequestMessage(m, path);
        if (m == HttpMethod.Post || m == HttpMethod.Put) req.Content = new FormUrlEncodedContent(form ?? Array.Empty<KeyValuePair<string, string>>());
        using var r = await _http.SendAsync(req, ct);
        var text = await r.Content.ReadAsStringAsync(ct);
        if (r.StatusCode == HttpStatusCode.Unauthorized) throw new UnauthorizedAccessException("the API token was rejected (check the token id user@realm!name and its secret)");
        if (r.StatusCode == HttpStatusCode.Forbidden) throw new UnauthorizedAccessException("permission denied for this token: " + (r.ReasonPhrase ?? "403"));
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"HTTP {(int)r.StatusCode} {r.ReasonPhrase}: {text[..Math.Min(200, text.Length)]}");
        return JsonNode.Parse(text)?["data"];
    }

    public async Task<ProviderCaps> DetectAsync(CancellationToken ct)
    {
        if (ValidateEndpoint(_c.Endpoint) is { } bad) return ProviderCaps.Unreachable(bad);
        if (AppData.Current.FindCredential(_c.CredentialId) is not { } cred || !cred.Username.Contains('!'))
            return ProviderCaps.Unreachable("choose a vault credential holding an API token (user: user@realm!tokenid, password: the token secret)");
        try
        {
            var v = await Call(HttpMethod.Get, "version", ct);
            var perms = await Call(HttpMethod.Get, "access/permissions", ct) as JsonObject ?? new JsonObject();
            _perms = ParsePermissions(perms);
            bool read = _perms.Values.Any(p => p.Contains("VM.Audit") || p.Contains("Sys.Audit"));
            bool power = _perms.Values.Any(p => ChangePrivileges.Any(p.Contains));
            var notes = new List<string>();
            if (!power) notes.Add("This token has none of " + string.Join(", ", ChangePrivileges) + " — read-only.");
            else if (ChangePrivileges.Where(x => !_perms.Values.Any(p => p.Contains(x))).ToList() is { Count: > 0 } missing)
                notes.Add("Not granted to this token (those actions stay disabled): " + string.Join(", ", missing) + ".");
            if (cred.Username.StartsWith("root@pam!", StringComparison.OrdinalIgnoreCase) && _perms.TryGetValue("/", out var root) && root.Contains("Sys.Modify"))
                notes.Add("⚠ A root@pam token with full rights: consider a dedicated user with only PVEAuditor (+ PVEVMUser for power) on /vms.");
            return new ProviderCaps(true, read, power, v?["version"]?.GetValue<string>() ?? "", $"Proxmox VE {v?["version"]} (release {v?["release"]})", notes);
        }
        catch (HttpRequestException ex) when (ex.InnerException is AuthenticationException || LastTls != TlsDecision.Accept)
        {
            return ProviderCaps.Unreachable(TlsPin.UntrustedMessage(LastTls, LastSeenSha256) + " (On the host: pvenode cert info.)");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UnauthorizedAccessException or InvalidOperationException)
        {
            return ProviderCaps.Unreachable(ex is TaskCanceledException ? "timed out" : ex.Message);
        }
    }

    /// <summary>/access/permissions → path → privileges that are granted (value 1 / true).</summary>
    public static Dictionary<string, HashSet<string>> ParsePermissions(JsonObject data) =>
        data.Where(kv => kv.Value is JsonObject).ToDictionary(kv => kv.Key,
            kv => kv.Value!.AsObject().Where(p => p.Value?.ToString() is "1" or "true").Select(p => p.Key).ToHashSet());

    /// <summary>Most specific granted path wins: /vms/&lt;id&gt; → /vms → /.</summary>
    public bool Has(string priv, string? vmid) =>
        new[] { vmid != null ? "/vms/" + vmid : null, "/vms", "/" }.Where(p => p != null).Select(p => _perms.TryGetValue(p!, out var s) ? s : null).FirstOrDefault(s => s != null) is { } set && set.Contains(priv);

    internal void SetPermissionsForTest(Dictionary<string, HashSet<string>> p) => _perms = p;

    public async Task<IReadOnlyList<InfraObject>> InventoryAsync(CancellationToken ct)
    {
        var data = await Call(HttpMethod.Get, "cluster/resources", ct) as JsonArray ?? new JsonArray();
        return ParseResources(data);
    }

    public static List<InfraObject> ParseResources(JsonArray data)
    {
        var list = new List<InfraObject>();
        string S(JsonObject o, string k) => o[k]?.ToString() ?? "";
        double D(JsonObject o, string k) => double.TryParse(S(o, k), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
        string Gb(double b) => b <= 0 ? "?" : $"{b / (1L << 30):0.#} GB";
        foreach (var o in data.OfType<JsonObject>())
        {
            var type = S(o, "type");
            var props = o.Where(kv => kv.Value is JsonValue).ToDictionary(kv => kv.Key, kv => kv.Value!.ToString());
            switch (type)
            {
                case "node":
                    list.Add(new InfraObject(S(o, "id"), "node", S(o, "node"), S(o, "status"), "", $"CPU {D(o, "cpu") * 100:0}% of {S(o, "maxcpu")} · RAM {Gb(D(o, "mem"))} / {Gb(D(o, "maxmem"))}", props));
                    break;
                case "qemu" or "lxc":
                    bool template = S(o, "template") == "1";
                    list.Add(new InfraObject(S(o, "id"), type == "qemu" ? "vm" : "lxc", S(o, "name").Length > 0 ? S(o, "name") : S(o, "vmid"), template ? "template" : S(o, "status"), S(o, "node"),
                        $"{(type == "qemu" ? "VM" : "CT")} {S(o, "vmid")} · {S(o, "maxcpu")} vCPU · RAM {Gb(D(o, "maxmem"))}" + (S(o, "status") == "running" ? $" · up {TimeSpan.FromSeconds(D(o, "uptime")):d\\.hh\\:mm}" : ""), props));
                    break;
                case "storage":
                    list.Add(new InfraObject(S(o, "id"), "storage", S(o, "storage"), S(o, "status"), S(o, "node"), $"{S(o, "plugintype")} · {Gb(D(o, "disk"))} used of {Gb(D(o, "maxdisk"))}", props));
                    break;
            }
        }
        return list;
    }

    // ---- actions (ADR-031: everything the API offers for guests that can be verified by re-reading) ------------------

    /// <summary>Privileges that allow some change; a token with none of them is read-only.</summary>
    public static readonly string[] ChangePrivileges = { "VM.PowerMgmt", "VM.Snapshot", "VM.Snapshot.Rollback", "VM.Clone", "VM.Migrate", "VM.Backup", "VM.Allocate" };

    /// <summary>Proxmox config ids (snapshot names): pve-configid, 2–40 characters.</summary>
    public static bool ValidSnapName(string s) => Regex.IsMatch(s, "^[A-Za-z][A-Za-z0-9_-]{1,39}$") && s != "current";
    /// <summary>Storage ids: pve-storage-id.</summary>
    public static bool ValidStorage(string s) => Regex.IsMatch(s, "^[A-Za-z][A-Za-z0-9._-]{0,62}$");

    public static readonly InfraAction Start = new("start", "▶ Start", true, Consequence.None, "running", "Starts the guest.");
    public static readonly InfraAction Shutdown = new("shutdown", "⏻ Shut down (graceful)", true, Consequence.ServiceInterruption, "stopped", "Asks the guest OS to shut down (ACPI / init). Its services go down.");
    public static readonly InfraAction Reboot = new("reboot", "↻ Reboot", true, Consequence.ServiceInterruption, "running", "Asks the guest OS to reboot. Its services are briefly down.");
    public static readonly InfraAction Stop = new("stop", "■ Stop (hard power-off)", true, Consequence.DataLoss, "stopped", "Cuts power immediately, like pulling the plug: unsaved data inside the guest can be lost. Prefer Shut down.");
    public static readonly InfraAction Suspend = new("suspend", "‖ Pause", true, Consequence.ServiceInterruption, "paused", "Pauses the VM in memory (QEMU suspend). It stops answering until resumed; nothing is written to disk.");
    public static readonly InfraAction Resume = new("resume", "▷ Resume", true, Consequence.None, "running", "Resumes a paused VM.");
    public static readonly InfraAction Snapshots = new("snapshots", "◷ Snapshots (list)", false, Consequence.None, null, "Lists the guest's snapshots (name, time, description, parent).");
    private static readonly InfraInput SnapNameIn = new("snapname", "Snapshot name", "before-change", "letters, digits, - and _; starts with a letter; 2–40 characters");
    public static readonly InfraAction SnapshotVm = new("snapshot", "◷ Take snapshot…", true, Consequence.None, null, "Takes a snapshot of the VM's disks (and, if chosen, its RAM so it resumes exactly there). The storage must support snapshots (ZFS, LVM-thin, Ceph, qcow2).")
    {
        Inputs = new[] { SnapNameIn, new InfraInput("description", "Description", Required: false), new InfraInput("vmstate", "Include RAM (VM state)", "no", Options: new[] { "no", "yes" }) },
    };
    public static readonly InfraAction SnapshotCt = new("snapshot", "◷ Take snapshot…", true, Consequence.None, null, "Takes a snapshot of the container's volumes. The storage must support snapshots (ZFS, LVM-thin, Ceph).")
    {
        Inputs = new[] { SnapNameIn, new InfraInput("description", "Description", Required: false) },
    };
    public static readonly InfraAction Rollback = new("rollback", "↺ Roll back to snapshot…", true, Consequence.DataLoss, null, "Returns the guest to the snapshot. Everything written since then is LOST (a running guest is stopped first).")
    {
        Inputs = new[] { SnapNameIn with { Default = null, Hint = "the exact name, as listed by Snapshots (list)" } },
    };
    public static readonly InfraAction DeleteSnapshot = new("delsnapshot", "✕ Delete snapshot…", true, Consequence.DataLoss, null, "Deletes the snapshot; it can't be rolled back to afterwards. The guest keeps running.")
    {
        Inputs = new[] { SnapNameIn with { Default = null, Hint = "the exact name, as listed by Snapshots (list)" } },
    };
    public static readonly InfraAction Clone = new("clone", "⧉ Clone…", true, Consequence.None, null, "Copies the guest to a new VMID on the same node (a full copy of every disk unless you choose a linked clone of a template). The copy is left stopped.")
    {
        Inputs = new[]
        {
            new InfraInput("newid", "New VMID", Required: false, Hint: "empty = the next free id (cluster/nextid)", Number: true),
            new InfraInput("name", "Name of the copy", Required: false, Hint: "a DNS-style name, e.g. web-02"),
            new InfraInput("full", "Full copy", "yes", "no = linked clone (templates only)", Options: new[] { "yes", "no" }),
        },
    };
    public static readonly InfraAction Migrate = new("migrate", "⇢ Migrate…", true, Consequence.ServiceInterruption, null, "Moves the guest to another node of the cluster. A running VM moves live when “online” is chosen (shared storage, or local disks copied); a container is restarted on the target.")
    {
        Inputs = new[] { new InfraInput("target", "Target node", Hint: "the node's name, as shown under Datacenter"), new InfraInput("online", "Online / restart migration", "yes", Options: new[] { "yes", "no" }) },
    };
    public static readonly InfraAction Backup = new("backup", "⛁ Back up now…", true, Consequence.ServiceInterruption, null, "Runs vzdump for this guest to a backup storage. Snapshot mode keeps it running; suspend mode pauses it briefly; stop mode shuts it down for the backup and starts it again.")
    {
        Inputs = new[]
        {
            new InfraInput("storage", "Backup storage", "local", "a storage with content type “VZDump backup file”, e.g. local or a PBS datastore"),
            new InfraInput("mode", "Mode", "snapshot", Options: new[] { "snapshot", "suspend", "stop" }),
            new InfraInput("compress", "Compression", "zstd", Options: new[] { "zstd", "gzip", "lzo", "0" }),
        },
    };
    public static readonly InfraAction Template = new("template", "◇ Convert to template…", true, Consequence.DataLoss, null, "Turns the stopped guest into a template. This can't be undone: a template can't be started, only cloned.");
    public static readonly InfraAction AgentInfo = new("agent", "ⓘ Guest agent info", false, Consequence.None, null, "Asks the QEMU guest agent for the OS, host name and network interfaces (the agent must be installed and enabled).");
    public static readonly InfraAction Tasks = new("tasks", "☰ Recent tasks", false, Consequence.None, null, "The node's last 50 tasks (who, what, when, result).");
    public static readonly InfraAction Content = new("content", "☰ Contents", false, Consequence.None, null, "What the storage holds: disk images, ISOs, templates, backups.");

    public IReadOnlyList<InfraAction> ActionsFor(InfraObject o) => o.Kind switch
    {
        "vm" or "lxc" when o.Status == "template" => new[] { Clone, Backup },
        "vm" => new[] { Start, Shutdown, Reboot, Suspend, Resume, Stop, Snapshots, SnapshotVm, Rollback, DeleteSnapshot, Clone, Migrate, Backup, Template, AgentInfo },
        "lxc" => new[] { Start, Shutdown, Reboot, Stop, Snapshots, SnapshotCt, Rollback, DeleteSnapshot, Clone, Migrate, Backup, Template },
        "node" => new[] { Tasks },
        "storage" => new[] { Content },
        _ => Array.Empty<InfraAction>(),
    };

    private static string Privilege(string action) => action switch
    {
        "snapshot" or "delsnapshot" => "VM.Snapshot",
        "clone" => "VM.Clone",
        "migrate" => "VM.Migrate",
        "backup" => "VM.Backup",
        "template" => "VM.Allocate",
        _ => "VM.PowerMgmt",
    };

    public string? Forbidden(InfraAction a, InfraObject o, ProviderCaps caps)
    {
        if (o.Kind is not ("vm" or "lxc")) return null;
        var vmid = o.Props.GetValueOrDefault("vmid");
        if (o.Status == "template" && a.Id is not ("clone" or "backup")) return "not possible on a template (clone it instead)";
        if (a.Id == "rollback" ? !Has("VM.Snapshot", vmid) && !Has("VM.Snapshot.Rollback", vmid) : !Has(Privilege(a.Id), vmid))
            return $"the token has no {(a.Id == "rollback" ? "VM.Snapshot or VM.Snapshot.Rollback" : Privilege(a.Id))} on /vms/{vmid}";
        return a.Id switch
        {
            "start" when o.Status == "running" => "already running",
            "shutdown" or "reboot" or "stop" or "suspend" or "resume" when o.Status != "running" => "not running",
            "template" when o.Status != "stopped" => "shut it down first",
            _ => null,
        };
    }

    private static bool SafeNode(string n) => Regex.IsMatch(n, "^[A-Za-z0-9][A-Za-z0-9.-]{0,62}$");

    public Task<ActionResult> RunAsync(InfraAction a, InfraObject o, CancellationToken ct) => RunAsync(a, o, new Dictionary<string, string>(), ct);

    public async Task<ActionResult> RunAsync(InfraAction a, InfraObject o, IReadOnlyDictionary<string, string> input, CancellationToken ct)
    {
        var node = o.Props.GetValueOrDefault("node") ?? "";
        if (!SafeNode(node)) return new ActionResult(false, false, "refused: unexpected node name");
        foreach (var i in a.Inputs) if (i.Validate(input.GetValueOrDefault(i.Name) ?? "") is { } bad) return new ActionResult(false, false, "refused: " + bad);
        try
        {
            switch (a.Id)
            {
                case "tasks": return new ActionResult(true, true, "tasks read", FormatTasks(await Call(HttpMethod.Get, $"nodes/{node}/tasks?limit=50", ct) as JsonArray ?? new JsonArray()));
                case "content":
                {
                    var storage = o.Props.GetValueOrDefault("storage") ?? "";
                    if (!ValidStorage(storage)) return new ActionResult(false, false, "refused: unexpected storage id");
                    return new ActionResult(true, true, "contents read", FormatContent(await Call(HttpMethod.Get, $"nodes/{node}/storage/{storage}/content", ct) as JsonArray ?? new JsonArray()));
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return new ActionResult(false, false, "refused: " + ex.Message); }

        var vmid = o.Props.GetValueOrDefault("vmid") ?? "";
        var type = o.Kind == "vm" ? "qemu" : "lxc";
        if (o.Kind is not ("vm" or "lxc") || !Regex.IsMatch(vmid, "^[0-9]{1,9}$")) return new ActionResult(false, false, "refused: unexpected node / guest id");
        var guest = $"nodes/{node}/{type}/{vmid}";
        var until = DateTime.UtcNow + VerifyFor;
        string snap = input.GetValueOrDefault("snapname") ?? "";
        if (a.Id is "snapshot" or "rollback" or "delsnapshot" && !ValidSnapName(snap)) return new ActionResult(false, false, "refused: a snapshot name has letters, digits, - and _, starts with a letter and has 2–40 characters");
        try
        {
            switch (a.Id)
            {
                case "start" or "shutdown" or "reboot" or "stop" or "suspend" or "resume":
                {
                    if (a.Id is "suspend" or "resume" && type != "qemu") return new ActionResult(false, false, "refused: pause/resume is offered for VMs only");
                    var upid = await Upid(Call(HttpMethod.Post, $"{guest}/status/{a.Id}", ct));
                    if (await WaitTask(node, upid, until, ct) is { } failed) return failed;
                    string? status = null;
                    while (DateTime.UtcNow < until)
                    {
                        var cur = await Call(HttpMethod.Get, $"{guest}/status/current", ct);
                        status = cur?["status"]?.ToString();
                        // A paused VM still reports status "running"; QEMU's own state (qmpstatus) tells paused from running.
                        if (type == "qemu" && status == "running" && cur?["qmpstatus"]?.ToString() is "paused" or "suspended") status = "paused";
                        if (status == a.ExpectedStatus) return new ActionResult(true, true, $"verified: task OK and the guest is now “{status}”");
                        await Task.Delay(PollEvery, ct);
                    }
                    return new ActionResult(true, false, $"task OK, but NOT verified: the guest reports “{status}” (expected “{a.ExpectedStatus}”)");
                }
                case "snapshots":
                    return new ActionResult(true, true, "snapshots read", FormatSnapshots(await Call(HttpMethod.Get, $"{guest}/snapshot", ct) as JsonArray ?? new JsonArray()));
                case "agent":
                {
                    if (type != "qemu") return new ActionResult(false, false, "refused: the guest agent exists for VMs only");
                    var sb = new System.Text.StringBuilder();
                    foreach (var (what, path) in new[] { ("OS", "get-osinfo"), ("Host name", "get-host-name"), ("Network interfaces", "network-get-interfaces") })
                    {
                        sb.Append(what).Append(":\n");
                        try { sb.Append(FormatAgent(await Call(HttpMethod.Get, $"{guest}/agent/{path}", ct))).Append('\n'); }
                        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException) { sb.Append("  not available: ").Append(ex.Message).Append('\n'); }
                    }
                    return new ActionResult(true, true, "guest agent read", sb.ToString());
                }
                case "snapshot":
                {
                    var form = new List<KeyValuePair<string, string>> { new("snapname", snap) };
                    if (input.GetValueOrDefault("description") is { Length: > 0 } d) form.Add(new("description", d));
                    if (type == "qemu" && input.GetValueOrDefault("vmstate") == "yes") form.Add(new("vmstate", "1"));
                    if (await SnapNames(guest, ct) is var before && before.Contains(snap)) return new ActionResult(false, false, $"refused: a snapshot “{snap}” already exists");
                    var upid = await Upid(Call(HttpMethod.Post, $"{guest}/snapshot", ct, form));
                    if (await WaitTask(node, upid, until, ct) is { } failed) return failed;
                    return (await SnapNames(guest, ct)).Contains(snap) ? new ActionResult(true, true, $"verified: task OK and snapshot “{snap}” is listed")
                        : new ActionResult(true, false, $"task OK, but NOT verified: “{snap}” isn't listed");
                }
                case "rollback" or "delsnapshot":
                {
                    if (!(await SnapNames(guest, ct)).Contains(snap)) return new ActionResult(false, false, $"refused: the guest has no snapshot “{snap}”");
                    var upid = await Upid(a.Id == "rollback"
                        ? Call(HttpMethod.Post, $"{guest}/snapshot/{Uri.EscapeDataString(snap)}/rollback", ct)
                        : Call(HttpMethod.Delete, $"{guest}/snapshot/{Uri.EscapeDataString(snap)}", ct));
                    if (await WaitTask(node, upid, until, ct) is { } failed) return failed;
                    var names = await SnapNames(guest, ct);
                    if (a.Id == "delsnapshot")
                        return !names.Contains(snap) ? new ActionResult(true, true, $"verified: task OK and snapshot “{snap}” is gone") : new ActionResult(true, false, $"task OK, but NOT verified: “{snap}” is still listed");
                    var parent = (await Call(HttpMethod.Get, $"{guest}/snapshot", ct) as JsonArray)?.OfType<JsonObject>().FirstOrDefault(s => s["name"]?.ToString() == "current")?["parent"]?.ToString();
                    var st = (await Call(HttpMethod.Get, $"{guest}/status/current", ct))?["status"]?.ToString();
                    return parent == snap ? new ActionResult(true, true, $"verified: task OK; the guest now runs from “{snap}” and is “{st}” (start it if needed)")
                        : new ActionResult(true, false, $"task OK, but NOT verified: the guest's current state follows “{parent}”, not “{snap}”");
                }
                case "clone":
                {
                    var newid = input.GetValueOrDefault("newid") ?? "";
                    if (newid.Length == 0) newid = (await Call(HttpMethod.Get, "cluster/nextid", ct))?.ToString() ?? "";
                    if (!long.TryParse(newid, out var nid) || nid is < 100 or > 999_999_999) return new ActionResult(false, false, "refused: a VMID is 100 – 999999999");
                    var form = new List<KeyValuePair<string, string>> { new("newid", newid) };
                    if (input.GetValueOrDefault("name") is { Length: > 0 } nm)
                    {
                        if (!Regex.IsMatch(nm, @"^[A-Za-z0-9]([A-Za-z0-9.-]{0,61}[A-Za-z0-9])?$")) return new ActionResult(false, false, "refused: the name must be DNS-style (letters, digits, dots, hyphens)");
                        form.Add(new(type == "qemu" ? "name" : "hostname", nm));
                    }
                    form.Add(new("full", input.GetValueOrDefault("full") == "no" ? "0" : "1"));
                    var upid = await Upid(Call(HttpMethod.Post, $"{guest}/clone", ct, form));
                    if (await WaitTask(node, upid, until, ct) is { } failed) return failed;
                    var made = await Call(HttpMethod.Get, $"nodes/{node}/{type}/{newid}/status/current", ct);
                    return made?["vmid"]?.ToString() == newid || made?["status"] != null
                        ? new ActionResult(true, true, $"verified: task OK and guest {newid} exists ({made?["status"]})")
                        : new ActionResult(true, false, $"task OK, but NOT verified: guest {newid} couldn't be read");
                }
                case "migrate":
                {
                    var target = input.GetValueOrDefault("target") ?? "";
                    if (!SafeNode(target) || target == node) return new ActionResult(false, false, "refused: choose another node of this cluster");
                    var form = new List<KeyValuePair<string, string>> { new("target", target) };
                    if (input.GetValueOrDefault("online") != "no") form.Add(new(type == "qemu" ? "online" : "restart", "1"));
                    var upid = await Upid(Call(HttpMethod.Post, $"{guest}/migrate", ct, form));
                    if (await WaitTask(node, upid, until, ct) is { } failed) return failed;
                    var now = ParseResources(await Call(HttpMethod.Get, "cluster/resources", ct) as JsonArray ?? new JsonArray()).FirstOrDefault(x => x.Props.GetValueOrDefault("vmid") == vmid && x.Kind == o.Kind);
                    return now?.Parent == target ? new ActionResult(true, true, $"verified: task OK and the guest is now on {target} (“{now.Status}”)")
                        : new ActionResult(true, false, $"task OK, but NOT verified: the cluster shows the guest on “{now?.Parent ?? "?"}”");
                }
                case "backup":
                {
                    var storage = input.GetValueOrDefault("storage") ?? "";
                    if (!ValidStorage(storage)) return new ActionResult(false, false, "refused: unexpected storage id");
                    var started = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 120;
                    var form = new List<KeyValuePair<string, string>> { new("vmid", vmid), new("storage", storage), new("mode", input.GetValueOrDefault("mode") ?? "snapshot"), new("compress", input.GetValueOrDefault("compress") ?? "zstd") };
                    var upid = await Upid(Call(HttpMethod.Post, $"nodes/{node}/vzdump", ct, form));
                    if (await WaitTask(node, upid, DateTime.UtcNow + TimeSpan.FromTicks(Math.Max(VerifyFor.Ticks, BackupFor.Ticks)), ct) is { } failed) return failed;
                    var files = await Call(HttpMethod.Get, $"nodes/{node}/storage/{storage}/content?content=backup", ct) as JsonArray ?? new JsonArray();
                    var made = files.OfType<JsonObject>().Where(f => f["vmid"]?.ToString() == vmid && long.TryParse(f["ctime"]?.ToString(), out var t) && t >= started).Select(f => f["volid"]?.ToString()).FirstOrDefault();
                    return made != null ? new ActionResult(true, true, $"verified: task OK and the backup {made} is on {storage}")
                        : new ActionResult(true, false, $"task OK, but NOT verified: no new backup of {vmid} is listed on {storage}");
                }
                case "template":
                {
                    var upid = (await Call(HttpMethod.Post, $"{guest}/template", ct))?.ToString();
                    if (upid is { Length: > 0 } && upid.StartsWith("UPID:", StringComparison.Ordinal) && await WaitTask(node, upid, until, ct) is { } failed) return failed;
                    var cfg = await Call(HttpMethod.Get, $"{guest}/config", ct);
                    return cfg?["template"]?.ToString() is "1" or "true" ? new ActionResult(true, true, "verified: the guest's configuration now says template")
                        : new ActionResult(true, false, "requested, but NOT verified: the configuration doesn't say template yet");
                }
                default: return new ActionResult(false, false, "refused: unknown action");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return new ActionResult(false, false, "refused: " + ex.Message); }
    }

    /// <summary>How long a backup may take before it's reported as NOT verified (vzdump of a big guest takes a while).</summary>
    public TimeSpan BackupFor { get; set; } = TimeSpan.FromHours(2);

    private static async Task<string> Upid(Task<JsonNode?> call) =>
        (await call)?.ToString() is { Length: > 0 } u ? u : throw new InvalidOperationException("no task id returned");

    /// <summary>Waits for a Proxmox task: null when it ended OK, otherwise the result to report.</summary>
    private async Task<ActionResult?> WaitTask(string node, string upid, DateTime until, CancellationToken ct)
    {
        string taskState = "running", exit = "";
        while (DateTime.UtcNow < until)
        {
            var t = await Call(HttpMethod.Get, $"nodes/{node}/tasks/{Uri.EscapeDataString(upid)}/status", ct);
            taskState = t?["status"]?.ToString() ?? "";
            exit = t?["exitstatus"]?.ToString() ?? "";
            if (taskState == "stopped") break;
            await Task.Delay(PollEvery, ct);
        }
        if (taskState != "stopped") return new ActionResult(true, false, $"requested (task {upid}), but NOT verified: the task was still running — see the node's Recent tasks");
        return exit == "OK" || exit.StartsWith("WARNINGS", StringComparison.Ordinal) ? null : new ActionResult(true, false, $"the Proxmox task failed: {exit}");
    }

    private async Task<HashSet<string>> SnapNames(string guest, CancellationToken ct) =>
        ((await Call(HttpMethod.Get, $"{guest}/snapshot", ct)) as JsonArray ?? new JsonArray()).OfType<JsonObject>()
            .Select(s => s["name"]?.ToString() ?? "").Where(n => n.Length > 0 && n != "current").ToHashSet();

    private static string When(JsonNode? epoch) => long.TryParse(epoch?.ToString(), out var t) && t > 0 ? DateTimeOffset.FromUnixTimeSeconds(t).LocalDateTime.ToString("yyyy-MM-dd HH:mm") : "";

    public static string FormatSnapshots(JsonArray a)
    {
        var rows = a.OfType<JsonObject>().Where(s => s["name"]?.ToString() != "current").ToList();
        return rows.Count == 0 ? "(no snapshots)" : string.Join("\n", rows.Select(s =>
            $"{s["name"],-24} {When(s["snaptime"]),-16} {(s["vmstate"]?.ToString() is "1" ? "RAM " : "    ")}parent {s["parent"]?.ToString() ?? "-",-18} {s["description"]?.ToString()?.ReplaceLineEndings(" ")}"));
    }

    public static string FormatTasks(JsonArray a) => a.Count == 0 ? "(no tasks)" : string.Join("\n", a.OfType<JsonObject>().Select(t =>
        $"{When(t["starttime"]),-16} {t["type"],-12} {t["id"],-8} {t["user"],-22} {(t["status"]?.ToString() ?? "running")}"));

    public static string FormatContent(JsonArray a) => a.Count == 0 ? "(empty)" : string.Join("\n", a.OfType<JsonObject>().OrderBy(c => c["content"]?.ToString()).Select(c =>
        $"{c["content"],-9} {c["volid"],-56} {(long.TryParse(c["size"]?.ToString(), out var b) ? DcxManager.Ssh.Fmt.Bytes(b) : ""),10} {When(c["ctime"])}"));

    /// <summary>Guest agent answers are { "result": … }; shown as indented JSON without the wrapper.</summary>
    public static string FormatAgent(JsonNode? n)
    {
        var r = n?["result"] ?? n;
        var text = r?.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) ?? "(nothing)";
        return string.Join("\n", text.Split('\n').Select(l => "  " + l.TrimEnd()));
    }
}
