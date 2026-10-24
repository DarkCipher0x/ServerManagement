using System.Net;
using System.Net.Http;
using System.Security.Authentication;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DcxManager.Services;

namespace DcxManager.Infra;

/// <summary>
/// Synology DSM through its Web API (0.3.0 §14, DarkCipher0x: "the fullest capabilities, focusing on security").
/// <list type="bullet">
/// <item><b>Transport:</b> HTTPS only (DSM's port 5001); the certificate is checked by Windows or pinned on first use like
/// Proxmox (ADR-007), and a changed certificate is refused.</item>
/// <item><b>Sign-in:</b> a dedicated DSM account from the vault (least privilege: a non-admin account sees only what DSM lets
/// it see; admin is needed for system, storage and users). The password, session id and SynoToken travel only in POST
/// bodies / headers, never in a URL (URLs end up in logs). Two-factor accounts: "Sign in with authenticator code" once,
/// and DSM's device token is kept sealed in the vault (the credential's passphrase field) so later sign-ins don't need the
/// code. The session lives in memory only and is signed out after use.</item>
/// <item><b>Discovery:</b> SYNO.API.Info decides what exists on this NAS (Container Manager, Virtual Machine Manager, Hyper
/// Backup are packages); actions are only offered for APIs that are there.</item>
/// <item><b>Reads:</b> system info and utilisation, storage pools, volumes and disks (S.M.A.R.T., temperature), shared folders
/// (encryption), packages, containers, virtual machines, Hyper Backup tasks, users, DSM update status.</item>
/// <item><b>Changes</b> (only with "Allow changes"; each re-read to verify): container start / stop / restart, package start /
/// stop, VM power on / shut down / power off, run a Hyper Backup task, NAS reboot / shut down (typed confirmation).</item>
/// </list>
/// </summary>
public sealed class SynologyProvider : IInfraProvider, ITlsPinned
{
    private readonly ProviderConfig _c;
    private readonly HttpClient _http;
    private Dictionary<string, (string Path, int Min, int Max)> _apis = new();
    private string? _sid, _token;
    private bool _admin;

    public ProviderConfig Config => _c;
    public string Where => _c.Endpoint;
    public NetClass? NetworkClass => NetworkPolicy.Classify(_c.Endpoint);
    public string LastSeenSha256 { get; private set; } = "";
    public TlsDecision LastTls { get; private set; } = TlsDecision.Accept;
    public TimeSpan VerifyFor { get; set; } = TimeSpan.FromSeconds(90);
    public TimeSpan PollEvery { get; set; } = TimeSpan.FromSeconds(2);

    public SynologyProvider(ProviderConfig c, HttpMessageHandler? handler = null)
    {
        _c = c;
        handler ??= TlsPin.Handler(() => _c.TlsSha256, (sha, d) => { LastSeenSha256 = sha; LastTls = d; });
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        if (Uri.TryCreate(_c.Endpoint.Trim().TrimEnd('/') + "/webapi/", UriKind.Absolute, out var b)) _http.BaseAddress = b;
    }

    /// <summary>https only: the DSM password and session would otherwise travel in clear text.</summary>
    public static string? ValidateEndpoint(string url) =>
        !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) ? "Enter the NAS address, e.g. https://nas.lan:5001"
        : u.Scheme != Uri.UriSchemeHttps ? "Synology DSM must be reached over https (port 5001 by default): the password and session would otherwise travel in clear text."
        : u.AbsolutePath.Trim('/').Length > 0 ? "Only the address and port (no path), e.g. https://nas.lan:5001"
        : null;

    private Credential? Cred => AppData.Current.FindCredential(_c.CredentialId);

    // ---- the Web API ----------------------------------------------------------------------------------------------------

    /// <summary>A DSM error code in words (SYNO.API.Auth codes first, then the common ones).</summary>
    public static string Error(int code, bool auth) => (auth, code) switch
    {
        (true, 400) => "the user name or password is wrong",
        (true, 401) => "this DSM account is disabled",
        (true, 402) => "this account may not sign in (permission denied)",
        (true, 403) => "two-factor authentication: sign in once with an authenticator code",
        (true, 404) => "the authenticator code was wrong",
        (true, 406) => "DSM requires two-factor authentication for this account: set it up in DSM, then sign in with a code",
        (true, 407) => "DSM has blocked this PC's address after failed sign-ins (Control Panel → Security → Protection)",
        (true, 408 or 409 or 410) => "the account's password has expired: change it in DSM",
        (_, 105) => "permission denied for this account",
        (_, 106) => "the session timed out",
        (_, 107) => "the session was interrupted by another sign-in",
        (_, 119) => "the session id is missing or invalid",
        (_, 102 or 103 or 104) => "this API or method isn't available on this NAS",
        _ => $"DSM error {code}",
    };

    internal sealed class DsmException(int code, string message) : Exception(message) { public int Code { get; } = code; }

    /// <summary>One API call. Everything travels form-encoded in the POST body; the SynoToken goes in its header.</summary>
    private async Task<JsonNode?> Call(string api, string method, int version, CancellationToken ct, IEnumerable<KeyValuePair<string, string>>? args = null, bool withSession = true)
    {
        var path = api == "SYNO.API.Info" ? "query.cgi" : _apis.TryGetValue(api, out var a) ? a.Path : throw new DsmException(102, $"{api} isn't available on this NAS");
        var form = new List<KeyValuePair<string, string>> { new("api", api), new("method", method), new("version", version.ToString()) };
        if (args != null) form.AddRange(args);
        if (withSession && _sid != null) form.Add(new("_sid", _sid));
        using var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(form) };
        if (withSession && _token != null) req.Headers.TryAddWithoutValidation("X-SYNO-TOKEN", _token);
        using var r = await _http.SendAsync(req, ct);
        var text = await r.Content.ReadAsStringAsync(ct);
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"HTTP {(int)r.StatusCode} {r.ReasonPhrase}");
        var j = JsonNode.Parse(text) as JsonObject ?? throw new InvalidOperationException("DSM didn't answer with JSON");
        if (j["success"]?.GetValue<bool>() != true)
        {
            var code = (int?)j["error"]?["code"]?.GetValue<long>() ?? 0;
            throw new DsmException(code, Error(code, api == "SYNO.API.Auth"));
        }
        return j["data"];
    }

    private int V(string api, int wanted) => _apis.TryGetValue(api, out var a) ? Math.Clamp(wanted, a.Min, a.Max) : wanted;
    private bool Has(string api) => _apis.ContainsKey(api);

    private async Task DiscoverAsync(CancellationToken ct)
    {
        if (_apis.Count > 0) return;
        var data = await Call("SYNO.API.Info", "query", 1, ct, new[] { new KeyValuePair<string, string>("query", "all") }, withSession: false) as JsonObject ?? new JsonObject();
        _apis = data.Where(kv => kv.Value is JsonObject).ToDictionary(kv => kv.Key, kv => (kv.Value!["path"]?.ToString() ?? "entry.cgi", (int)(kv.Value["minVersion"]?.GetValue<long>() ?? 1), (int)(kv.Value["maxVersion"]?.GetValue<long>() ?? 1)));
    }

    /// <summary>Signs in (device token if one is stored; an authenticator code when given). Keeps the session in memory.</summary>
    private async Task SignInAsync(CancellationToken ct, string? otp = null)
    {
        if (_sid != null) return;
        await DiscoverAsync(ct);
        var cred = await ExternalCredentials.ResolveAsync(Cred, "Synology " + _c.Name, ct) ?? throw new InvalidOperationException("Choose the vault credential with the DSM account (user name and password).");
        var args = new List<KeyValuePair<string, string>>
        {
            new("account", cred.Username), new("passwd", cred.Secret), new("session", "DarkCipherSM-" + _c.Id[..8]), new("format", "sid"),
            new("enable_syno_token", "yes"), new("device_name", "DarkCipher ServerManagement on " + Environment.MachineName),
        };
        if (otp is { Length: > 0 }) { args.Add(new("otp_code", otp)); args.Add(new("enable_device_token", "yes")); }
        else if (cred.Passphrase.Length > 0) args.Add(new("device_id", cred.Passphrase));   // remembered device: no code needed
        var d = await Call("SYNO.API.Auth", "login", V("SYNO.API.Auth", 7), ct, args, withSession: false);
        _sid = d?["sid"]?.ToString() ?? throw new InvalidOperationException("DSM returned no session");
        _token = d?["synotoken"]?.ToString();
        _admin = false;   // known after the first privileged read (SYNO.Core.System info)
        if (otp is { Length: > 0 } && d?["did"]?.ToString() is { Length: > 0 } did && cred.Passphrase != did)
        {
            cred.Passphrase = did;   // sealed in the vault with the credential
            AppData.Save();
            AuditLog.Write("you", "infra:" + _c.Name, "DSM device token stored (two-factor sign-in remembered)");
        }
    }

    private async Task SignOutAsync()
    {
        if (_sid == null) return;
        try { using var t = new CancellationTokenSource(5000); await Call("SYNO.API.Auth", "logout", V("SYNO.API.Auth", 6), t.Token, new[] { new KeyValuePair<string, string>("session", "DarkCipherSM-" + _c.Id[..8]) }); } catch { }
        _sid = null; _token = null;
    }

    private async Task<T> Session<T>(Func<Task<T>> work, CancellationToken ct)
    {
        await SignInAsync(ct);
        try { return await work(); }
        finally { await SignOutAsync(); }
    }

    // ---- detect / inventory -------------------------------------------------------------------------------------------

    public async Task<ProviderCaps> DetectAsync(CancellationToken ct)
    {
        if (ValidateEndpoint(_c.Endpoint) is { } bad) return ProviderCaps.Unreachable(bad);
        if (Cred is not { Username.Length: > 0 } sc || !ExternalCredentials.HasSecret(sc)) return ProviderCaps.Unreachable("choose a vault credential with the DSM account (user name and password)");
        try
        {
            return await Session(async () =>
            {
                var notes = new List<string>();
                string model = "", version = "";
                try
                {
                    var info = await Call("SYNO.Core.System", "info", V("SYNO.Core.System", 3), ct);
                    model = info?["model"]?.ToString() ?? ""; version = info?["firmware_ver"]?.ToString() ?? "";
                    _admin = true;
                }
                catch (DsmException ex) when (ex.Code == 105) { notes.Add("This account isn't an administrator: system, storage and users stay hidden (that's fine for a read-mostly account)."); }
                if (Cred!.Username.Equals("admin", StringComparison.OrdinalIgnoreCase)) notes.Add("⚠ The built-in “admin” account: create a dedicated account (and disable “admin”) — DSM's own advice.");
                foreach (var (api, what) in new[] { ("SYNO.Docker.Container", "Container Manager"), ("SYNO.Virtualization.API.Guest", "Virtual Machine Manager"), ("SYNO.Backup.Task", "Hyper Backup") })
                    if (!Has(api)) notes.Add($"{what} isn't installed (or its API isn't reachable): not shown.");
                return new ProviderCaps(true, true, true, version, $"Synology {model} · {version}".Trim(' ', '·'), notes);
            }, ct);
        }
        catch (HttpRequestException ex) when (ex.InnerException is AuthenticationException || LastTls != TlsDecision.Accept)
        {
            return ProviderCaps.Unreachable(TlsPin.UntrustedMessage(LastTls, LastSeenSha256) + " (DSM: Control Panel → Security → Certificate.)");
        }
        catch (DsmException ex) when (ex.Code is 403 or 406) { _sid = null; return ProviderCaps.Unreachable("Two-factor authentication: select “DSM sign-in” and run “Sign in with authenticator code” once."); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or DsmException or InvalidOperationException)
        {
            _sid = null;
            return ProviderCaps.Unreachable(ex is TaskCanceledException ? "timed out" : ex.Message);
        }
    }

    private static string S(JsonNode? o, string k) => o?[k] is JsonValue v ? v.ToString() : "";
    private static Dictionary<string, string> Props(JsonObject o) => o.Where(kv => kv.Value is JsonValue).Take(60).ToDictionary(kv => kv.Key, kv => Redaction.Mask(kv.Value!.ToString()));
    private static string Gb(string bytes) => long.TryParse(bytes, out var b) && b > 0 ? $"{b / (double)(1L << 30):0.#} GB" : "?";

    public async Task<IReadOnlyList<InfraObject>> InventoryAsync(CancellationToken ct)
    {
        try { return await Session(() => ReadAllAsync(ct), ct); }
        catch (DsmException ex) when (ex.Code is 403 or 406)
        {
            _sid = null;
            return new[] { SignInObject("two-factor code needed") };
        }
    }

    private InfraObject SignInObject(string status) => new("dsm-signin", "sign-in", "DSM sign-in", status, "", "Two-factor accounts: sign in once with an authenticator code; DSM's device token is then kept sealed in the vault.", new Dictionary<string, string>());

    private async Task<IReadOnlyList<InfraObject>> ReadAllAsync(CancellationToken ct)
    {
        var list = new List<InfraObject> { SignInObject("signed in") };
        async Task Try(Func<Task> f) { try { await f(); } catch (DsmException ex) when (ex.Code is 102 or 103 or 104 or 105) { } }

        await Try(async () =>
        {
            var info = await Call("SYNO.Core.System", "info", V("SYNO.Core.System", 3), ct) as JsonObject ?? new();
            var util = Has("SYNO.Core.System.Utilization") ? await Call("SYNO.Core.System.Utilization", "get", V("SYNO.Core.System.Utilization", 1), ct) : null;
            var p = Props(info);
            if (util?["cpu"] is JsonObject cpu) p["cpu_load"] = $"{(long.TryParse(S(cpu, "user_load"), out var u) ? u : 0) + (long.TryParse(S(cpu, "system_load"), out var s) ? s : 0)}%";
            if (util?["memory"] is JsonObject mem) p["memory_used"] = S(mem, "real_usage") + "%";
            list.Add(new InfraObject("system", "system", S(info, "model").Length > 0 ? S(info, "model") : "NAS", "online", "",
                $"DSM {S(info, "firmware_ver")} · RAM {S(info, "ram_size")} MB · up {S(info, "up_time")} · temp {S(info, "sys_temp")}°C" + (p.TryGetValue("cpu_load", out var cl) ? $" · CPU {cl}" : ""), p));
        });
        await Try(async () =>
        {
            if (!Has("SYNO.Core.Upgrade.Server")) return;
            var up = await Call("SYNO.Core.Upgrade.Server", "check", V("SYNO.Core.Upgrade.Server", 1), ct);
            var available = up?["update"]?["available"]?.ToString() == "true";
            list.Add(new InfraObject("update", "update", "DSM update", available ? "update available" : "up to date", "system",
                available ? $"{S(up?["update"], "version")} is available — update from DSM (Control Panel → Update & Restore)" : "no update offered", up is JsonObject uo ? Props(uo) : new()));
        });
        await Try(async () =>
        {
            var st = await Call("SYNO.Storage.CGI.Storage", "load_info", V("SYNO.Storage.CGI.Storage", 1), ct);
            foreach (var pool in (st?["storagePools"] as JsonArray ?? new()).OfType<JsonObject>())
                list.Add(new InfraObject("pool:" + S(pool, "id"), "pool", S(pool, "id"), S(pool, "status"), "storage", $"{S(pool, "device_type")} · {Gb(S(pool?["size"], "used"))} used of {Gb(S(pool?["size"], "total"))}", Props(pool!)));
            foreach (var vol in (st?["volumes"] as JsonArray ?? new()).OfType<JsonObject>())
                list.Add(new InfraObject("vol:" + S(vol, "id"), "volume", S(vol, "vol_path").Length > 0 ? S(vol, "vol_path") : S(vol, "id"), S(vol, "status"), S(vol, "pool_path"),
                    $"{S(vol, "fs_type")} · {Gb(S(vol?["size"], "used"))} used of {Gb(S(vol?["size"], "total"))}", Props(vol!)));
            foreach (var disk in (st?["disks"] as JsonArray ?? new()).OfType<JsonObject>())
                list.Add(new InfraObject("disk:" + S(disk, "id"), "disk", S(disk, "name").Length > 0 ? S(disk, "name") : S(disk, "id"), S(disk, "status"), S(disk, "used_by"),
                    $"{S(disk, "vendor")} {S(disk, "model")} · {Gb(S(disk, "size_total"))} · {S(disk, "temp")}°C · S.M.A.R.T. {S(disk, "smart_status")}".Trim(), Props(disk!)));
        });
        await Try(async () =>
        {
            var sh = await Call("SYNO.Core.Share", "list", V("SYNO.Core.Share", 1), ct, new[] { new KeyValuePair<string, string>("additional", "[\"encryption\",\"vol_path\",\"recyclebin\"]") });
            foreach (var s in (sh?["shares"] as JsonArray ?? new()).OfType<JsonObject>())
                list.Add(new InfraObject("share:" + S(s, "name"), "share", S(s, "name"), S(s?["additional"], "encryption") is "1" or "2" ? "encrypted" : "available", S(s?["additional"], "vol_path"),
                    $"{S(s, "desc")} · encryption {(S(s?["additional"], "encryption") is "1" or "2" ? "on" : "off")} · recycle bin {(S(s?["additional"], "recyclebin") == "true" ? "on" : "off")}", Props(s!)));
        });
        await Try(async () =>
        {
            var pk = await Call("SYNO.Core.Package", "list", V("SYNO.Core.Package", 2), ct, new[] { new KeyValuePair<string, string>("additional", "[\"status\",\"startable\"]") });
            foreach (var p in (pk?["packages"] as JsonArray ?? new()).OfType<JsonObject>())
                list.Add(new InfraObject("pkg:" + S(p, "id"), "package", S(p, "name"), S(p?["additional"], "status") is { Length: > 0 } ps ? ps : "installed", "packages", $"{S(p, "id")} {S(p, "version")}", Props(p!)));
        });
        await Try(async () =>
        {
            if (!Has("SYNO.Docker.Container")) return;
            var c = await Call("SYNO.Docker.Container", "list", V("SYNO.Docker.Container", 1), ct, new KeyValuePair<string, string>[] { new("limit", "-1"), new("offset", "0"), new("type", "all") });
            foreach (var x in (c?["containers"] as JsonArray ?? new()).OfType<JsonObject>())
                list.Add(new InfraObject("ctr:" + S(x, "name"), "container", S(x, "name"), S(x, "status"), "Container Manager", S(x, "image"), Props(x!)));
        });
        await Try(async () =>
        {
            if (!Has("SYNO.Virtualization.API.Guest")) return;
            var g = await Call("SYNO.Virtualization.API.Guest", "list", V("SYNO.Virtualization.API.Guest", 1), ct);
            foreach (var x in (g?["guests"] as JsonArray ?? new()).OfType<JsonObject>())
                list.Add(new InfraObject("vm:" + S(x, "guest_id"), "vm", S(x, "guest_name"), S(x, "status"), "Virtual Machine Manager", $"{S(x, "vcpu_num")} vCPU · RAM {S(x, "vram_size")} MB", Props(x!)));
        });
        await Try(async () =>
        {
            if (!Has("SYNO.Backup.Task")) return;
            var b = await Call("SYNO.Backup.Task", "list", V("SYNO.Backup.Task", 1), ct, new[] { new KeyValuePair<string, string>("additional", "[\"last_bkp_time\",\"last_bkp_result\",\"is_modified\",\"last_bkp_progress\"]") });
            foreach (var x in (b?["task_list"] as JsonArray ?? new()).OfType<JsonObject>())
                list.Add(new InfraObject("bkp:" + S(x, "task_id"), "backup", S(x, "name"), S(x, "state").Length > 0 ? S(x, "state") : "idle", "Hyper Backup",
                    $"last {S(x, "last_bkp_time")} · result {S(x, "last_bkp_result")}", Props(x!)));
        });
        await Try(async () =>
        {
            var u = await Call("SYNO.Core.User", "list", V("SYNO.Core.User", 1), ct, new KeyValuePair<string, string>[] { new("offset", "0"), new("limit", "-1"), new("additional", "[\"email\",\"expired\"]") });
            foreach (var x in (u?["users"] as JsonArray ?? new()).OfType<JsonObject>())
                list.Add(new InfraObject("user:" + S(x, "name"), "user", S(x, "name"), S(x?["additional"], "expired") is "now" or "true" ? "disabled" : "active", "users", S(x, "description"), Props(x!)));
        });
        return list;
    }

    // ---- actions ------------------------------------------------------------------------------------------------------

    public static readonly InfraAction SignIn = new("signin", "🔑 Sign in with authenticator code…", false, Consequence.None, null,
        "Signs in once with your authenticator code; DSM's device token is kept sealed in the vault so later sign-ins from this PC don't ask again.")
    { Inputs = new[] { new InfraInput("code", "Authenticator code (6 digits)", Number: true) } };
    public static readonly InfraAction CtrStart = new("ctr-start", "▶ Start", true, Consequence.None, "running", "Starts the container.");
    public static readonly InfraAction CtrStop = new("ctr-stop", "■ Stop", true, Consequence.ServiceInterruption, "stopped", "Stops the container; its service goes down.");
    public static readonly InfraAction CtrRestart = new("ctr-restart", "↻ Restart", true, Consequence.ServiceInterruption, "running", "Restarts the container.");
    public static readonly InfraAction PkgStart = new("pkg-start", "▶ Start package", true, Consequence.None, "running", "Starts the package.");
    public static readonly InfraAction PkgStop = new("pkg-stop", "■ Stop package", true, Consequence.ServiceInterruption, "stop", "Stops the package and what depends on it.");
    public static readonly InfraAction VmOn = new("vm-on", "▶ Power on", true, Consequence.None, "running", "Powers the virtual machine on.");
    public static readonly InfraAction VmShutdown = new("vm-shutdown", "⏻ Shut down (graceful)", true, Consequence.ServiceInterruption, "shutdown", "Asks the guest OS to shut down.");
    public static readonly InfraAction VmOff = new("vm-off", "■ Power off (hard)", true, Consequence.DataLoss, "shutdown", "Cuts the VM's power immediately: unsaved data inside it can be lost. Prefer Shut down.");
    public static readonly InfraAction BackupRun = new("bkp-run", "⛁ Back up now", true, Consequence.None, "backup", "Starts this Hyper Backup task now.");
    public static readonly InfraAction Reboot = new("nas-reboot", "↻ Reboot the NAS…", true, Consequence.ServiceInterruption, null, "Restarts the whole NAS: every share, container, VM and package is down until it is back.");
    public static readonly InfraAction Shutdown = new("nas-shutdown", "⏻ Shut down the NAS…", true, Consequence.ServiceInterruption, null, "Turns the NAS off. It needs someone at the NAS (or Wake-on-LAN) to come back.");
    public static readonly InfraAction Details = new("details", "ⓘ All fields", false, Consequence.None, null, "Every field DSM reported for this item (secrets masked).");

    public IReadOnlyList<InfraAction> ActionsFor(InfraObject o) => o.Kind switch
    {
        "sign-in" => new[] { SignIn },
        "container" => new[] { CtrStart, CtrStop, CtrRestart, Details },
        "package" => new[] { PkgStart, PkgStop, Details },
        "vm" => new[] { VmOn, VmShutdown, VmOff, Details },
        "backup" => new[] { BackupRun, Details },
        "system" => new[] { Details, Reboot, Shutdown },
        _ => new[] { Details },
    };

    public string? Forbidden(InfraAction a, InfraObject o, ProviderCaps caps) => a.Id switch
    {
        "ctr-start" or "vm-on" when o.Status is "running" => "already running",
        "ctr-stop" or "ctr-restart" or "vm-shutdown" or "vm-off" when o.Status is not "running" => "not running",
        "nas-reboot" or "nas-shutdown" when !_admin => "needs an administrator account",
        "pkg-start" when o.Status == "running" => "already running",
        "pkg-stop" when o.Status != "running" => "not running",
        _ => null,
    };

    public Task<ActionResult> RunAsync(InfraAction a, InfraObject o, CancellationToken ct) => RunAsync(a, o, new Dictionary<string, string>(), ct);

    private static bool SafeName(string s) => Regex.IsMatch(s, @"^[A-Za-z0-9][A-Za-z0-9 ._@+-]{0,127}$");

    public async Task<ActionResult> RunAsync(InfraAction a, InfraObject o, IReadOnlyDictionary<string, string> input, CancellationToken ct)
    {
        foreach (var i in a.Inputs) if (i.Validate(input.GetValueOrDefault(i.Name) ?? "") is { } bad) return new ActionResult(false, false, "refused: " + bad);
        if (a.Id == "details") return new ActionResult(true, true, "fields", string.Join("\n", o.Props.OrderBy(p => p.Key).Select(p => $"{p.Key,-28} {p.Value}")));
        if (a.Id == "signin")
        {
            var code = input.GetValueOrDefault("code") ?? "";
            if (!Regex.IsMatch(code, "^[0-9]{6,8}$")) return new ActionResult(false, false, "refused: a 6-digit code");
            try { _sid = null; await SignInAsync(ct, code); await SignOutAsync(); return new ActionResult(true, true, "signed in; this PC is remembered by DSM (device token sealed in the vault)"); }
            catch (Exception ex) when (ex is DsmException or HttpRequestException or InvalidOperationException) { _sid = null; return new ActionResult(false, false, "refused: " + ex.Message); }
        }
        var name = o.Name;
        if (!SafeName(name) && a.Id.StartsWith("ctr")) return new ActionResult(false, false, "refused: unexpected container name");
        try
        {
            return await Session(async () =>
            {
                switch (a.Id)
                {
                    case "ctr-start" or "ctr-stop" or "ctr-restart":
                        await Call("SYNO.Docker.Container", a.Id[4..], V("SYNO.Docker.Container", 1), ct, new[] { new KeyValuePair<string, string>("name", name) });
                        return await Verify(async () => (await Call("SYNO.Docker.Container", "list", V("SYNO.Docker.Container", 1), ct, new KeyValuePair<string, string>[] { new("limit", "-1"), new("offset", "0"), new("type", "all") }))?["containers"]?.AsArray()
                            .OfType<JsonObject>().FirstOrDefault(x => S(x, "name") == name) is { } x ? S(x, "status") : "", a.ExpectedStatus!, ct);
                    case "pkg-start" or "pkg-stop":
                    {
                        var id = o.Props.GetValueOrDefault("id") ?? "";
                        if (!Regex.IsMatch(id, "^[A-Za-z0-9._-]{1,64}$")) return new ActionResult(false, false, "refused: unexpected package id");
                        await Call("SYNO.Core.Package.Control", a.Id[4..], V("SYNO.Core.Package.Control", 1), ct, new[] { new KeyValuePair<string, string>("id", id) });
                        return await Verify(async () => (await Call("SYNO.Core.Package", "get", V("SYNO.Core.Package", 1), ct, new KeyValuePair<string, string>[] { new("id", id), new("additional", "[\"status\"]") }))?["additional"]?["status"]?.ToString() ?? "", a.ExpectedStatus!, ct);
                    }
                    case "vm-on" or "vm-shutdown" or "vm-off":
                    {
                        var gid = o.Props.GetValueOrDefault("guest_id") ?? "";
                        if (!Regex.IsMatch(gid, "^[A-Za-z0-9-]{1,64}$")) return new ActionResult(false, false, "refused: unexpected guest id");
                        await Call("SYNO.Virtualization.API.Guest.Action", a.Id switch { "vm-on" => "poweron", "vm-shutdown" => "shutdown", _ => "poweroff" }, V("SYNO.Virtualization.API.Guest.Action", 1), ct, new[] { new KeyValuePair<string, string>("guest_id", gid) });
                        return await Verify(async () => (await Call("SYNO.Virtualization.API.Guest", "get", V("SYNO.Virtualization.API.Guest", 1), ct, new[] { new KeyValuePair<string, string>("guest_id", gid) }))?["status"]?.ToString() ?? "", a.ExpectedStatus!, ct);
                    }
                    case "bkp-run":
                    {
                        var tid = o.Props.GetValueOrDefault("task_id") ?? "";
                        if (!Regex.IsMatch(tid, "^[0-9]{1,9}$")) return new ActionResult(false, false, "refused: unexpected task id");
                        await Call("SYNO.Backup.Task", "backup", V("SYNO.Backup.Task", 1), ct, new[] { new KeyValuePair<string, string>("task_id", tid) });
                        return await Verify(async () => (await Call("SYNO.Backup.Task", "status", V("SYNO.Backup.Task", 1), ct, new[] { new KeyValuePair<string, string>("task_id", tid) }))?["state"]?.ToString() ?? "", "backup", ct);
                    }
                    case "nas-reboot" or "nas-shutdown":
                        await Call("SYNO.Core.System", a.Id == "nas-reboot" ? "reboot" : "shutdown", V("SYNO.Core.System", 1), ct, new[] { new KeyValuePair<string, string>("force", "false") });
                        AuditLog.Write("you", "infra:" + _c.Name, a.Id == "nas-reboot" ? "NAS reboot requested" : "NAS shut down requested");
                        return new ActionResult(true, false, a.Id == "nas-reboot" ? "DSM accepted the reboot; the NAS is going down now (refresh in a few minutes)." : "DSM accepted the shutdown; the NAS is turning off.");
                    default: return new ActionResult(false, false, "refused: unknown action");
                }
            }, ct);
        }
        catch (Exception ex) when (ex is DsmException or HttpRequestException or InvalidOperationException) { _sid = null; return new ActionResult(false, false, "refused: " + ex.Message); }
    }

    /// <summary>Re-reads until the expected state shows (or the time is up: then "not verified").</summary>
    private async Task<ActionResult> Verify(Func<Task<string>> read, string expected, CancellationToken ct)
    {
        var until = DateTime.UtcNow + VerifyFor;
        string now = "";
        while (DateTime.UtcNow < until)
        {
            now = await read();
            if (now.Equals(expected, StringComparison.OrdinalIgnoreCase) || (expected == "backup" && now.Contains("backup", StringComparison.OrdinalIgnoreCase))) return new ActionResult(true, true, $"verified: now “{now}”");
            await Task.Delay(PollEvery, ct);
        }
        return new ActionResult(true, false, $"requested, but NOT verified: DSM reports “{now}” (expected “{expected}”)");
    }

    /// <summary>Self-test seam: the sign-in state.</summary>
    internal bool SignedInForTest => _sid != null;
}
