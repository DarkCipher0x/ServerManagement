using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DcxManager.Services;
using DcxManager.Ssh;

namespace DcxManager.Infra;

/// <summary>How a Docker provider reaches the engine. Two real backends: the docker CLI over SSH on a saved host, and the
/// Docker Engine API over Docker Desktop's named pipe on this PC (or any HttpClient, which the self-test uses).</summary>
public abstract class DockerBackend
{
    public abstract string Where { get; }
    public abstract NetClass? Class { get; }
    public abstract Task<ProviderCaps> VersionAsync(CancellationToken ct);
    public abstract Task<List<InfraObject>> ListAsync(CancellationToken ct);
    /// <summary>start | stop | restart | pause | unpause | kill | remove on a container id. Throws with the engine's message on failure.</summary>
    public abstract Task ActAsync(string action, string id, CancellationToken ct);
    /// <summary>inspect | top | stats of a container, as text (read only).</summary>
    public abstract Task<string> ReadAsync(string what, string id, CancellationToken ct);
    /// <summary>Removes an unused image, network or volume (the engine refuses one that is in use).</summary>
    public abstract Task RemoveAsync(string kind, string id, CancellationToken ct);
    /// <summary>Whether that image, network or volume still exists.</summary>
    public abstract Task<bool> ExistsAsync(string kind, string id, CancellationToken ct);
    /// <summary>The container's State.Status ("running", "exited"…), or null when it no longer exists.</summary>
    public abstract Task<string?> StateAsync(string id, CancellationToken ct);
    public abstract Task<string> LogsAsync(string id, CancellationToken ct);

    public static DockerBackend Ssh(ProviderConfig c) => new SshDocker(c);
    public static DockerBackend LocalPipe(string pipe) => new ApiDocker(PipeClient(string.IsNullOrWhiteSpace(pipe) ? "docker_engine" : pipe.Trim()), $@"\\.\pipe\{(string.IsNullOrWhiteSpace(pipe) ? "docker_engine" : pipe.Trim())}", NetClass.Local);
    public static DockerBackend Api(HttpClient http, string where, NetClass? cls) => new ApiDocker(http, where, cls);

    /// <summary>Container ids only (hex, 12–64): the only thing that ever reaches a command line or a URL path.</summary>
    public static bool ValidId(string? id) => id != null && Regex.IsMatch(id, "^[a-f0-9]{12,64}$");

    /// <summary>Image ids (sha256:hex or hex), network ids (hex) and volume names (Docker's own name rule): the only
    /// values that reach a command line or a URL path for removals (ADR-031).</summary>
    public static bool ValidObjectId(string kind, string? id) => id != null && kind switch
    {
        "image" => Regex.IsMatch(id, "^(sha256:)?[a-f0-9]{12,64}$"),
        "network" => Regex.IsMatch(id, "^[a-f0-9]{12,64}$"),
        "volume" => Regex.IsMatch(id, "^[A-Za-z0-9][A-Za-z0-9_.-]{0,254}$"),
        _ => false,
    };

    public static HttpClient PipeClient(string pipe)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                var p = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
                await p.ConnectAsync(3000, ct);
                return p;
            },
        };
        return new HttpClient(handler) { BaseAddress = new Uri("http://docker/"), Timeout = TimeSpan.FromSeconds(60) };
    }

    // ---- shared mapping -------------------------------------------------------------------------------------------

    internal static InfraObject Container(string id, string name, string image, string state, string status, string ports) =>
        new(id, "container", name.TrimStart('/'), state.ToLowerInvariant(), "", $"{image} · {status}" + (ports.Length > 0 ? " · " + ports : ""),
            new Dictionary<string, string> { ["image"] = image, ["status"] = status, ["ports"] = ports });
}

/// <summary>docker CLI over SSH (RemoteExec). JSON output via --format '{{json .}}'; one round trip per inventory.</summary>
internal sealed class SshDocker(ProviderConfig c) : DockerBackend
{
    private bool _sudo;
    private HostEntry Host => AppData.Current.FindHost(c.HostId) ?? throw new InvalidOperationException("The provider's host no longer exists.");
    public override string Where => AppData.Current.FindHost(c.HostId) is { } h ? $"ssh {h.Endpoint}" : "ssh (missing host)";
    public override NetClass? Class => AppData.Current.FindHost(c.HostId) is { } h ? NetworkPolicy.ClassifyHost(h.Address) : null;

    private async Task<ExecResult> Run(string cmd, CancellationToken ct, int seconds = 45)
    {
        var r = await SshPool.RunAsync(Host, cmd, _sudo, ct, TimeSpan.FromSeconds(seconds));
        if (!_sudo && (r.Error + r.Output).Contains("permission denied", StringComparison.OrdinalIgnoreCase) && (r.Error + r.Output).Contains("docker", StringComparison.OrdinalIgnoreCase))
        {
            _sudo = true;   // not in the docker group: use the host's sudo credential (asks if none is saved)
            r = await SshPool.RunAsync(Host, cmd, true, ct, TimeSpan.FromSeconds(seconds));
        }
        return r;
    }

    public override async Task<ProviderCaps> VersionAsync(CancellationToken ct)
    {
        var r = await Run("docker version --format '{{json .}}'", ct, 20);
        var line = r.Output.Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith("{"));
        if (line == null)
            return ProviderCaps.Unreachable((r.Error + r.Output).Trim() is { Length: > 0 } e ? e.Split('\n')[0] : "docker is not installed or not on PATH");
        var j = JsonNode.Parse(line);
        var server = j?["Server"];
        if (server == null || server.GetValueKind() == JsonValueKind.Null)
            return new ProviderCaps(true, false, false, j?["Client"]?["Version"]?.GetValue<string>() ?? "", "the docker CLI works but the daemon isn't reachable (is the service running?)", Array.Empty<string>());
        var notes = new List<string> { "Docker access is root-equivalent on that host (docker group or sudo)." };
        if (_sudo) notes.Add("Using sudo (this login isn't in the docker group).");
        return new ProviderCaps(true, true, true, server["Version"]?.GetValue<string>() ?? "", $"Docker {server["Version"]} · API {server["ApiVersion"]} · {server["Os"]}/{server["Arch"]}", notes);
    }

    private const string Sep = "---DCX-SECTION---";

    public override async Task<List<InfraObject>> ListAsync(CancellationToken ct)
    {
        var cmd = string.Join($"; echo '{Sep}'; ", new[]
        {
            "docker ps -a --no-trunc --format '{{json .}}'", "docker images --no-trunc --format '{{json .}}'",
            "docker network ls --no-trunc --format '{{json .}}'", "docker volume ls --format '{{json .}}'",
        });
        var r = await Run(cmd, ct, 60);
        if (r.ExitCode != 0 && !r.Output.Contains(Sep)) throw new InvalidOperationException((r.Error + r.Output).Trim().Split('\n')[0]);
        return ParseCli(r.Output);
    }

    /// <summary>Parses the four sections of CLI JSON lines (public for the self-test).</summary>
    public static List<InfraObject> ParseCli(string output)
    {
        var list = new List<InfraObject>();
        var parts = output.Split(Sep);
        // Each line becomes a flat string map inside the try: duplicate keys or non-string values (JsonObject throws on
        // first access, GetValue throws on a type mismatch) drop that line instead of the whole inventory (fuzzing, s23).
        IEnumerable<Dictionary<string, string>> Lines(int i) => i < parts.Length
            ? parts[i].Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("{")).Select(Flat).OfType<Dictionary<string, string>>()
            : Enumerable.Empty<Dictionary<string, string>>();
        static Dictionary<string, string>? Flat(string line)
        {
            try
            {
                if (JsonNode.Parse(line) is not JsonObject o) return null;
                var d = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var (k, v) in o) d[k] = v is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : v?.ToJsonString() ?? "";
                return d;
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or FormatException) { return null; }
        }
        static string S(Dictionary<string, string> o, string k) => o.TryGetValue(k, out var v) ? v : "";
        foreach (var o in Lines(0)) if (ValidId(S(o, "ID"))) list.Add(Container(S(o, "ID"), S(o, "Names"), S(o, "Image"), S(o, "State"), S(o, "Status"), S(o, "Ports")));
        foreach (var o in Lines(1))
            list.Add(new InfraObject(S(o, "ID"), "image", $"{S(o, "Repository")}:{S(o, "Tag")}", "available", "", $"{S(o, "Size")} · {S(o, "CreatedSince")}", new Dictionary<string, string> { ["size"] = S(o, "Size") }));
        foreach (var o in Lines(2))
            list.Add(new InfraObject(S(o, "ID"), "network", S(o, "Name"), "available", "", $"{S(o, "Driver")} · {S(o, "Scope")}", new Dictionary<string, string> { ["driver"] = S(o, "Driver") }));
        foreach (var o in Lines(3))
            list.Add(new InfraObject(S(o, "Name"), "volume", S(o, "Name"), "available", "", S(o, "Driver"), new Dictionary<string, string> { ["driver"] = S(o, "Driver") }));
        return list;
    }

    public override async Task ActAsync(string action, string id, CancellationToken ct)
    {
        if (!ValidId(id)) throw new ArgumentException("invalid container id");
        var verb = action switch { "start" => "start", "stop" => "stop", "restart" => "restart", "pause" => "pause", "unpause" => "unpause", "kill" => "kill", "remove" => "rm", _ => throw new ArgumentException(action) };
        var r = await Run($"docker {verb} {id}", ct, 90);
        if (r.ExitCode != 0) throw new InvalidOperationException((r.Error + r.Output).Trim().Split('\n')[0]);
    }

    public override async Task<string> ReadAsync(string what, string id, CancellationToken ct)
    {
        if (!ValidId(id)) throw new ArgumentException("invalid container id");
        var cmd = what switch
        {
            "inspect" => $"docker inspect {id}",
            "top" => $"docker top {id}",
            "stats" => $"docker stats --no-stream --no-trunc {id}",
            _ => throw new ArgumentException(what),
        };
        var r = await Run(cmd, ct, 30);
        if (r.ExitCode != 0) throw new InvalidOperationException((r.Error + r.Output).Trim().Split('\n')[0]);
        return r.Output;
    }

    public override async Task RemoveAsync(string kind, string id, CancellationToken ct)
    {
        if (!ValidObjectId(kind, id)) throw new ArgumentException("invalid " + kind + " id");
        var r = await Run($"docker {(kind == "image" ? "image rm" : kind + " rm")} {id}", ct, 90);
        if (r.ExitCode != 0) throw new InvalidOperationException((r.Error + r.Output).Trim().Split('\n')[0]);
    }

    public override async Task<bool> ExistsAsync(string kind, string id, CancellationToken ct)
    {
        if (!ValidObjectId(kind, id)) throw new ArgumentException("invalid " + kind + " id");
        var r = await Run($"docker {kind} inspect --format '{{{{.Name}}}}' {id}", ct, 20);
        if (r.ExitCode == 0) return true;
        return (r.Error + r.Output).Contains("No such", StringComparison.OrdinalIgnoreCase) || (r.Error + r.Output).Contains("not found", StringComparison.OrdinalIgnoreCase)
            ? false : throw new InvalidOperationException((r.Error + r.Output).Trim().Split('\n')[0]);
    }

    public override async Task<string?> StateAsync(string id, CancellationToken ct)
    {
        if (!ValidId(id)) throw new ArgumentException("invalid container id");
        var r = await Run($"docker inspect --format '{{{{json .State}}}}' {id}", ct, 20);
        if (r.ExitCode != 0) return (r.Error + r.Output).Contains("No such", StringComparison.OrdinalIgnoreCase) ? null : throw new InvalidOperationException((r.Error + r.Output).Trim().Split('\n')[0]);
        var line = r.Output.Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith("{"));
        return line == null ? null : JsonNode.Parse(line)?["Status"]?.GetValue<string>();
    }

    public override async Task<string> LogsAsync(string id, CancellationToken ct)
    {
        if (!ValidId(id)) throw new ArgumentException("invalid container id");
        var r = await Run($"docker logs --tail 300 -t {id} 2>&1", ct, 30);
        return r.Output;
    }
}

/// <summary>Docker Engine API (unversioned paths → the engine's own version).</summary>
internal sealed class ApiDocker(HttpClient http, string where, NetClass? cls) : DockerBackend
{
    public override string Where => where;
    public override NetClass? Class => cls;

    private async Task<JsonNode?> Get(string path, CancellationToken ct, bool allow404 = false)
    {
        using var r = await http.GetAsync(path, ct);
        if (allow404 && r.StatusCode == HttpStatusCode.NotFound) return null;
        var text = await r.Content.ReadAsStringAsync(ct);
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException(Message(text, r.StatusCode));
        return JsonNode.Parse(text);
    }

    private static string Message(string body, HttpStatusCode code)
    {
        try { if (JsonNode.Parse(body)?["message"]?.GetValue<string>() is { } m) return m; } catch { }
        return $"HTTP {(int)code}";
    }

    public override async Task<ProviderCaps> VersionAsync(CancellationToken ct)
    {
        try
        {
            var v = await Get("version", ct);
            return new ProviderCaps(true, true, true, v?["Version"]?.GetValue<string>() ?? "", $"Docker {v?["Version"]} · API {v?["ApiVersion"]} · {v?["Os"]}/{v?["Arch"]}",
                new[] { "Docker Engine access is administrator-equivalent on that machine." });
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException or OperationCanceledException { CancellationToken.IsCancellationRequested: false } or InvalidOperationException)
        {
            return ProviderCaps.Unreachable(ex is HttpRequestException or IOException or TimeoutException ? $"Docker Engine isn't reachable at {where} (is Docker Desktop running?)" : ex.Message);
        }
    }

    public override async Task<List<InfraObject>> ListAsync(CancellationToken ct)
    {
        var list = new List<InfraObject>();
        foreach (var c in (await Get("containers/json?all=1", ct))?.AsArray().OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
        {
            var ports = string.Join(", ", c["Ports"]?.AsArray().OfType<JsonObject>().Select(p => p["PublicPort"] != null ? $"{p["PublicPort"]}→{p["PrivatePort"]}/{p["Type"]}" : $"{p["PrivatePort"]}/{p["Type"]}") ?? Enumerable.Empty<string>());
            var id = c["Id"]?.GetValue<string>() ?? "";
            if (ValidId(id)) list.Add(Container(id, c["Names"]?.AsArray().FirstOrDefault()?.GetValue<string>() ?? id[..12], c["Image"]?.GetValue<string>() ?? "", c["State"]?.GetValue<string>() ?? "", c["Status"]?.GetValue<string>() ?? "", ports));
        }
        foreach (var i in (await Get("images/json", ct))?.AsArray().OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
        {
            var tag = i["RepoTags"]?.AsArray().FirstOrDefault()?.GetValue<string>() ?? "<none>:<none>";
            var size = i["Size"]?.GetValue<long>() ?? 0;
            list.Add(new InfraObject(i["Id"]?.GetValue<string>() ?? "", "image", tag, "available", "", Fmt.Bytes(size), new Dictionary<string, string> { ["size"] = size.ToString() }));
        }
        foreach (var n in (await Get("networks", ct))?.AsArray().OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
            list.Add(new InfraObject(n["Id"]?.GetValue<string>() ?? "", "network", n["Name"]?.GetValue<string>() ?? "", "available", "", $"{n["Driver"]} · {n["Scope"]}", new Dictionary<string, string> { ["driver"] = n["Driver"]?.GetValue<string>() ?? "" }));
        foreach (var v in (await Get("volumes", ct))?["Volumes"]?.AsArray().OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
            list.Add(new InfraObject(v["Name"]?.GetValue<string>() ?? "", "volume", v["Name"]?.GetValue<string>() ?? "", "available", "", v["Driver"]?.GetValue<string>() ?? "", new Dictionary<string, string>()));
        return list;
    }

    public override async Task ActAsync(string action, string id, CancellationToken ct)
    {
        if (!ValidId(id)) throw new ArgumentException("invalid container id");
        using var req = action switch
        {
            "start" or "stop" or "restart" or "pause" or "unpause" or "kill" => new HttpRequestMessage(HttpMethod.Post, $"containers/{id}/{action}"),
            "remove" => new HttpRequestMessage(HttpMethod.Delete, $"containers/{id}"),
            _ => throw new ArgumentException(action),
        };
        using var r = await http.SendAsync(req, ct);
        if (!r.IsSuccessStatusCode && r.StatusCode != HttpStatusCode.NotModified) throw new InvalidOperationException(Message(await r.Content.ReadAsStringAsync(ct), r.StatusCode));
    }

    public override async Task<string> ReadAsync(string what, string id, CancellationToken ct)
    {
        if (!ValidId(id)) throw new ArgumentException("invalid container id");
        var j = what switch
        {
            "inspect" => await Get($"containers/{id}/json", ct),
            "top" => await Get($"containers/{id}/top", ct),
            "stats" => await Get($"containers/{id}/stats?stream=false", ct),
            _ => throw new ArgumentException(what),
        };
        if (what == "top" && j?["Titles"] is JsonArray titles && j["Processes"] is JsonArray procs)
            return string.Join("\n", new[] { string.Join("  ", titles.Select(t => t?.ToString())) }.Concat(procs.OfType<JsonArray>().Select(p => string.Join("  ", p.Select(x => x?.ToString())))));
        if (what == "stats" && j != null) return FormatStats(j);
        return j?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "";
    }

    /// <summary>One stats sample → CPU %, memory, network and block I/O (the docker CLI's own formula).</summary>
    public static string FormatStats(JsonNode j)
    {
        static double D(JsonNode? n) => double.TryParse(n?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
        double cpuDelta = D(j["cpu_stats"]?["cpu_usage"]?["total_usage"]) - D(j["precpu_stats"]?["cpu_usage"]?["total_usage"]);
        double sysDelta = D(j["cpu_stats"]?["system_cpu_usage"]) - D(j["precpu_stats"]?["system_cpu_usage"]);
        double cpus = D(j["cpu_stats"]?["online_cpus"]) is > 0 and var oc ? oc : 1;
        double cpu = sysDelta > 0 && cpuDelta > 0 ? cpuDelta / sysDelta * cpus * 100 : 0;
        double mem = D(j["memory_stats"]?["usage"]) - D(j["memory_stats"]?["stats"]?["inactive_file"]), limit = D(j["memory_stats"]?["limit"]);
        double rx = 0, tx = 0;
        if (j["networks"] is JsonObject nets) foreach (var (_, n) in nets) { rx += D(n?["rx_bytes"]); tx += D(n?["tx_bytes"]); }
        double rd = 0, wr = 0;
        foreach (var e in (j["blkio_stats"]?["io_service_bytes_recursive"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
            if (e["op"]?.ToString()?.ToLowerInvariant() == "read") rd += D(e["value"]); else if (e["op"]?.ToString()?.ToLowerInvariant() == "write") wr += D(e["value"]);
        return $"CPU      {cpu:0.00} % (of {cpus:0} CPU)\nMemory   {Fmt.Bytes((long)Math.Max(0, mem))} / {Fmt.Bytes((long)limit)}" + (limit > 0 ? $" ({mem / limit * 100:0.0} %)" : "") +
               $"\nNet I/O  {Fmt.Bytes((long)rx)} received / {Fmt.Bytes((long)tx)} sent\nBlock    {Fmt.Bytes((long)rd)} read / {Fmt.Bytes((long)wr)} written\nPIDs     {j["pids_stats"]?["current"]}";
    }

    public override async Task RemoveAsync(string kind, string id, CancellationToken ct)
    {
        if (!ValidObjectId(kind, id)) throw new ArgumentException("invalid " + kind + " id");
        using var r = await http.DeleteAsync($"{kind}s/{Uri.EscapeDataString(id)}", ct);
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException(Message(await r.Content.ReadAsStringAsync(ct), r.StatusCode));
    }

    public override async Task<bool> ExistsAsync(string kind, string id, CancellationToken ct)
    {
        if (!ValidObjectId(kind, id)) throw new ArgumentException("invalid " + kind + " id");
        return await Get($"{kind}s/{Uri.EscapeDataString(id)}{(kind == "image" ? "/json" : "")}", ct, allow404: true) != null;
    }

    public override async Task<string?> StateAsync(string id, CancellationToken ct)
    {
        if (!ValidId(id)) throw new ArgumentException("invalid container id");
        var j = await Get($"containers/{id}/json", ct, allow404: true);
        return j?["State"]?["Status"]?.GetValue<string>();
    }

    public override async Task<string> LogsAsync(string id, CancellationToken ct)
    {
        if (!ValidId(id)) throw new ArgumentException("invalid container id");
        var bytes = await http.GetByteArrayAsync($"containers/{id}/logs?stdout=1&stderr=1&tail=300&timestamps=1", ct);
        return Demux(bytes);
    }

    /// <summary>Docker's multiplexed log stream (8-byte frame headers) → text; TTY containers send plain text.</summary>
    public static string Demux(byte[] b)
    {
        if (b.Length < 8 || b[0] > 2 || b[1] != 0 || b[2] != 0 || b[3] != 0) return Encoding.UTF8.GetString(b);
        var sb = new StringBuilder();
        int i = 0;
        while (i + 8 <= b.Length)
        {
            int len = (b[i + 4] << 24) | (b[i + 5] << 16) | (b[i + 6] << 8) | b[i + 7];
            if (len < 0 || i + 8 + len > b.Length) break;
            sb.Append(Encoding.UTF8.GetString(b, i + 8, len));
            i += 8 + len;
        }
        return sb.ToString();
    }
}

/// <summary>Docker as an infrastructure provider: inventory of containers, images, networks and volumes; container
/// logs, inspect, processes and a stats sample (read); start/stop/restart/pause/unpause/kill/remove of containers and
/// removal of unused images, networks and volumes (changes, ADR-031), each verified by re-reading the engine.</summary>
public sealed class DockerProvider(ProviderConfig config, DockerBackend backend) : IInfraProvider
{
    public ProviderConfig Config => config;
    public string Where => backend.Where;
    public NetClass? NetworkClass => backend.Class;
    /// <summary>How long to wait for the expected state after a change (self-test shortens it).</summary>
    public TimeSpan VerifyFor { get; set; } = TimeSpan.FromSeconds(20);

    public static readonly InfraAction Logs = new("logs", "Logs (last 300 lines)", false, Consequence.None, null, "Reads the container's recent output.");
    public static readonly InfraAction Start = new("start", "▶ Start", true, Consequence.None, "running", "Starts the container.");
    public static readonly InfraAction Stop = new("stop", "■ Stop", true, Consequence.ServiceInterruption, "exited", "Stops the container (SIGTERM, then SIGKILL after the grace period). Whatever it serves goes down.");
    public static readonly InfraAction Restart = new("restart", "↻ Restart", true, Consequence.ServiceInterruption, "running", "Stops and starts the container. Whatever it serves is briefly down.");
    public static readonly InfraAction Remove = new("remove", "✕ Remove", true, Consequence.DataLoss, null, "Deletes the container. Its writable layer (anything not in a volume) is lost. Volumes and the image are kept.");
    public static readonly InfraAction Inspect = new("inspect", "ⓘ Inspect", false, Consequence.None, null, "The container's full configuration and state (docker inspect).");
    public static readonly InfraAction Top = new("top", "☰ Processes", false, Consequence.None, null, "The processes running inside the container (docker top).");
    public static readonly InfraAction Stats = new("stats", "▤ Resource usage", false, Consequence.None, null, "One sample of CPU, memory, network and disk I/O (docker stats --no-stream).");
    public static readonly InfraAction Pause = new("pause", "‖ Pause", true, Consequence.ServiceInterruption, "paused", "Freezes every process in the container (cgroup freezer). It stops answering until unpaused.");
    public static readonly InfraAction Unpause = new("unpause", "▷ Unpause", true, Consequence.None, "running", "Resumes a paused container.");
    public static readonly InfraAction Kill = new("kill", "⚡ Kill", true, Consequence.DataLoss, "exited", "Sends SIGKILL at once: no clean shutdown, unsaved data in the container can be lost. Prefer Stop.");
    public static readonly InfraAction RemoveImage = new("remove-image", "✕ Remove image", true, Consequence.DataLoss, null, "Deletes the image from this engine (it can be pulled again). Docker refuses while a container uses it.");
    public static readonly InfraAction RemoveNetwork = new("remove-network", "✕ Remove network", true, Consequence.ServiceInterruption, null, "Deletes the network. Docker refuses while a container is attached; the built-in bridge, host and none can't be removed.");
    public static readonly InfraAction RemoveVolume = new("remove-volume", "✕ Remove volume", true, Consequence.DataLoss, null, "Deletes the volume AND ALL ITS DATA. Docker refuses while a container uses it.");

    public Task<ProviderCaps> DetectAsync(CancellationToken ct) => backend.VersionAsync(ct);

    public async Task<IReadOnlyList<InfraObject>> InventoryAsync(CancellationToken ct) => await backend.ListAsync(ct);

    public IReadOnlyList<InfraAction> ActionsFor(InfraObject o) => o.Kind switch
    {
        "container" => new[] { Logs, Inspect, Top, Stats, Start, Stop, Restart, Pause, Unpause, Kill, Remove },
        "image" => new[] { RemoveImage },
        "network" => new[] { RemoveNetwork },
        "volume" => new[] { RemoveVolume },
        _ => Array.Empty<InfraAction>(),
    };

    public string? Forbidden(InfraAction a, InfraObject o, ProviderCaps caps) => a.Id switch
    {
        "start" when o.Status == "running" => "already running",
        "stop" when o.Status != "running" && o.Status != "paused" => "not running",
        "restart" when o.Status != "running" => "not running (use Start)",
        "remove" when o.Status is "running" or "paused" or "restarting" => "stop it first (a running container is never removed)",
        "pause" or "kill" when o.Status != "running" => "not running",
        "unpause" when o.Status != "paused" => "not paused",
        "remove-network" when o.Name is "bridge" or "host" or "none" => "a built-in network can't be removed",
        _ => null,
    };

    public async Task<ActionResult> RunAsync(InfraAction a, InfraObject o, CancellationToken ct)
    {
        if (a.Id == "logs") return new ActionResult(true, true, "logs read", await backend.LogsAsync(o.Id, ct));
        if (a.Id is "inspect" or "top" or "stats")
        {
            try { return new ActionResult(true, true, a.Id + " read", await backend.ReadAsync(a.Id, o.Id, ct)); }
            catch (Exception ex) when (ex is not OperationCanceledException) { return new ActionResult(false, false, "refused: " + ex.Message); }
        }
        if (a.Id is "remove-image" or "remove-network" or "remove-volume")
        {
            var kind = a.Id[7..];
            try { await backend.RemoveAsync(kind, o.Id, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { return new ActionResult(false, false, "refused: " + ex.Message); }
            return await backend.ExistsAsync(kind, o.Id, ct) ? new ActionResult(true, false, $"the engine accepted it, but the {kind} still exists")
                : new ActionResult(true, true, $"verified: the {kind} no longer exists");
        }
        try { await backend.ActAsync(a.Id, o.Id, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return new ActionResult(false, false, "refused: " + ex.Message); }
        // Verify against the engine itself — never assume the command worked.
        var until = DateTime.UtcNow + VerifyFor;
        string? state = "?";
        while (DateTime.UtcNow < until)
        {
            state = await backend.StateAsync(o.Id, ct);
            if (a.Id == "remove" ? state == null : state == a.ExpectedStatus) return new ActionResult(true, true, a.Id == "remove" ? "verified: the container no longer exists" : $"verified: state is now “{state}”");
            await Task.Delay(500, ct);
        }
        return new ActionResult(true, false, $"requested, but NOT verified: state is “{state ?? "gone"}” after {VerifyFor.TotalSeconds:0} s (expected {(a.Id == "remove" ? "removed" : a.ExpectedStatus)})");
    }
}
