using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DcxManager.Vultr;

/// <summary>What the user asked for: a name and an OS, optionally a region, a size (vCPU / RAM / disk) or an exact plan, and
/// SSH keys (Vultr key names) to install.</summary>
public sealed record DeployRequest(string Name, string Os, string Region = "", int? Vcpus = null, double? RamGb = null, int? DiskGb = null, string Plan = "", IReadOnlyList<string>? SshKeys = null);

/// <summary>The order worked out from the request and Vultr's own lists, shown to the user before anything is created.</summary>
public sealed record DeployPlan(string Label, string Hostname, int OsId, string OsName, bool Windows, string RegionId, string RegionText,
    string PlanId, int Vcpus, int RamMb, int DiskGb, double MonthlyCost, bool SpecMatched, string SpecNote, IReadOnlyList<(string Id, string Name)> SshKeys)
{
    public string Size => $"{Vcpus} vCPU · {RamMb / 1024.0:0.##} GB RAM · {DiskGb} GB disk";
    public string Cost => MonthlyCost.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>The approval text: everything that will be ordered, what it costs, and why this plan.</summary>
    public string Describe(string account) =>
        $"Vultr account: {account}\nName: {Label} (hostname {Hostname})\nOS: {OsName}\nRegion: {RegionText} ({RegionId})\n" +
        $"Plan: {PlanId} · {Size} · ${Cost}/month\n{SpecNote}\n" +
        (SshKeys.Count > 0 ? "SSH keys: " + string.Join(", ", SshKeys.Select(k => k.Name)) : "No SSH key: Vultr's generated root password goes into your vault (never shown to the AI).") +
        "\n\nThis orders a new server that Vultr bills to the account.";
}

/// <summary>
/// The AI's "deploy a server" on Vultr (0.6.17, DarkCipher0x: "ask it to deploy a new server with the name and spec that i
/// choose. If it doesnt match the spec, choose the cheapest one available for the OS described. Then deploy it … and
/// confirm"). Works the order out from Vultr's own lists (operating systems, regions, plans, SSH keys), never from guesses:
/// the cheapest plan that matches the requested size exactly, else the cheapest plan offered in the region that can run the
/// OS (IPv6-only and GPU plans left out). Creating is never retried (a slow answer must not become two servers); readiness
/// is read back from the instance.
/// </summary>
public static class VultrDeploy
{
    /// <summary>The calls it needs (a VultrClient's, or a fake in the self-test).</summary>
    public sealed class Api
    {
        public required Func<string, string, CancellationToken, Task<List<JsonElement>>> List { get; init; }
        public required Func<string, CancellationToken, Task<JsonElement>> Get { get; init; }
        /// <summary>POST /instances with the body; never retried.</summary>
        public required Func<string, CancellationToken, Task<JsonElement>> Create { get; init; }

        public static Api For(VultrClient v, string actor) => new()
        {
            List = (path, key, ct) => v.ListAsync(path, key, ct),
            Get = (path, ct) => v.GetAsync(path, ct),
            Create = (json, ct) => v.SendAsync(HttpMethod.Post, "/instances", json, ct, actor, idempotent: false),
        };
    }

    /// <summary>The least memory a plan needs for the OS (Vultr refuses Windows below 2 GB).</summary>
    public static int MinRamMb(bool windows) => windows ? 2048 : 512;

    /// <summary>Label rules: 1–64 printable characters.</summary>
    public static string? CheckName(string name) =>
        name.Trim().Length is 0 or > 64 || name.Any(char.IsControl) ? "the name must be 1–64 characters without control characters" : null;

    /// <summary>A host name from the label (RFC 1123: a–z, 0–9 and hyphens, at most 63).</summary>
    public static string Hostname(string name)
    {
        var h = Regex.Replace(name.Trim().ToLowerInvariant(), "[^a-z0-9-]+", "-");
        h = Regex.Replace(h, "-{2,}", "-").Trim('-');
        if (h.Length > 63) h = h[..63].TrimEnd('-');
        return h.Length == 0 ? "server" : h;
    }

    private static string S(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ValueKind == JsonValueKind.Number ? v.GetRawText() : "" : "";
    private static double D(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : double.NaN;
    private static IEnumerable<string> Strings(JsonElement e, string k) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!) : Enumerable.Empty<string>();

    private static string[] Words(string s) => Regex.Split(s.ToLowerInvariant(), @"[^a-z0-9.]+").Where(w => w.Length > 0).ToArray();

    private static double Version(string name) => Regex.Match(name, @"\d+(\.\d+)?") is { Success: true } m && double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    /// <summary>The operating system meant by <paramref name="query"/> ("ubuntu 24.04", "Debian 12", "windows 2022"): every word
    /// of the query in the image's name; x64 unless ARM is asked for; an LTS release first, then the newest. Apps,
    /// snapshots, backups, ISOs and iPXE aren't operating systems. Null with the reason (and close names) when none fits.</summary>
    public static (int Id, string Name, bool Windows)? MatchOs(IEnumerable<JsonElement> os, string query, out string why)
    {
        why = "";
        var words = Words(query);
        if (words.Length == 0) { why = "say which operating system (e.g. Ubuntu 24.04, Debian 12, Windows 2022)"; return null; }
        bool arm = words.Any(w => w is "arm" or "arm64" or "aarch64");
        var images = os.Where(o => D(o, "id") is > 0 && S(o, "name").Length > 0 && S(o, "family") is not ("application" or "snapshot" or "backup" or "iso" or "ipxe" or "iPXE" or "custom")).ToList();
        var hits = images.Where(o => { var n = Words(S(o, "name")); return words.Where(w => w is not ("arm" or "arm64" or "aarch64" or "x64" or "lts")).All(w => n.Any(x => x == w || x.StartsWith(w))); })
            .Where(o => arm == (S(o, "arch") is "arm64" or "aarch64" || S(o, "name").Contains("arm", StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(o => S(o, "name").Contains("LTS")).ThenByDescending(o => Version(S(o, "name"))).ToList();   // a server wants an LTS release
        if (hits.Count == 0)
        {
            var close = images.Where(o => words.Any(w => S(o, "name").Contains(w, StringComparison.OrdinalIgnoreCase))).Select(o => S(o, "name")).Distinct().Take(8).ToList();
            why = $"no Vultr operating system matches “{query}”" + (close.Count > 0 ? ": close ones are " + string.Join(", ", close) : "");
            return null;
        }
        var best = hits[0];
        return ((int)D(best, "id"), S(best, "name"), S(best, "family").Equals("windows", StringComparison.OrdinalIgnoreCase) || S(best, "name").Contains("Windows", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The region meant by <paramref name="query"/>: its id ("lhr"), city ("London") or country ("GB").</summary>
    public static (string Id, string Text)? MatchRegion(IEnumerable<JsonElement> regions, string query)
    {
        var q = query.Trim();
        if (q.Length == 0) return null;
        var list = regions.Where(r => S(r, "id").Length > 0).ToList();
        Func<JsonElement, bool>[] tries =
        {
            r => S(r, "id").Equals(q, StringComparison.OrdinalIgnoreCase),
            r => S(r, "city").Equals(q, StringComparison.OrdinalIgnoreCase),
            r => S(r, "city").Contains(q, StringComparison.OrdinalIgnoreCase),
            r => S(r, "country").Equals(q, StringComparison.OrdinalIgnoreCase),
        };
        foreach (var t in tries)
            if (list.FirstOrDefault(t) is { ValueKind: JsonValueKind.Object } hit) return (S(hit, "id"), $"{S(hit, "city")}, {S(hit, "country")}".Trim(' ', ','));
        return null;
    }

    /// <summary>Plans that can run the OS: not IPv6-only ("-v6"), not GPU, enough memory, priced, offered somewhere (or in
    /// <paramref name="region"/>).</summary>
    public static IEnumerable<JsonElement> Usable(IEnumerable<JsonElement> plans, string? region, bool windows) =>
        plans.Where(p => S(p, "id").Length > 0 && !S(p, "id").EndsWith("-v6", StringComparison.OrdinalIgnoreCase)
                         && !S(p, "type").Equals("vcg", StringComparison.OrdinalIgnoreCase) && !S(p, "id").Contains("-gpu-", StringComparison.OrdinalIgnoreCase)
                         && D(p, "ram") >= MinRamMb(windows) && D(p, "monthly_cost") > 0 && D(p, "vcpu_count") > 0
                         && (region == null ? Strings(p, "locations").Any() : Strings(p, "locations").Contains(region, StringComparer.OrdinalIgnoreCase)));

    /// <summary>The plan: an exact plan id when given (and offered); else the cheapest one matching every size given
    /// (vCPU and RAM exactly, disk at least); else the cheapest usable one. Ties go to the smaller id (stable).</summary>
    public static (JsonElement Plan, bool Matched, string Note)? ChoosePlan(IEnumerable<JsonElement> plans, string? region, bool windows, int? vcpus, double? ramGb, int? diskGb, string planId, string osName, string regionText)
    {
        var usable = Usable(plans, region, windows).OrderBy(p => D(p, "monthly_cost")).ThenBy(p => S(p, "id"), StringComparer.Ordinal).ToList();
        if (usable.Count == 0) return null;
        var where = region == null ? "" : $" in {regionText}";
        if (planId.Trim().Length > 0)
        {
            var exact = usable.FirstOrDefault(p => S(p, "id").Equals(planId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (exact.ValueKind != JsonValueKind.Undefined) return (exact, true, $"The plan you asked for ({S(exact, "id")}).");
            var cheapest = usable[0];
            return (cheapest, false, $"Plan {planId.Trim()} isn't offered{where} for {osName}: the cheapest plan for it is used instead.");
        }
        bool any = vcpus != null || ramGb != null || diskGb != null;
        if (!any) return (usable[0], true, $"No size given: the cheapest plan{where} for {osName}.");
        var asked = string.Join(" / ", new[] { vcpus is { } c ? $"{c} vCPU" : null, ramGb is { } r ? $"{r:0.##} GB RAM" : null, diskGb is { } d ? $"{d} GB+ disk" : null }.Where(x => x != null));
        var match = usable.FirstOrDefault(p =>
            (vcpus == null || (int)D(p, "vcpu_count") == vcpus) &&
            (ramGb == null || Math.Abs(D(p, "ram") - ramGb.Value * 1024) <= 64) &&
            (diskGb == null || D(p, "disk") >= diskGb));
        if (match.ValueKind != JsonValueKind.Undefined) return (match, true, $"Matches your spec ({asked}): the cheapest such plan{where}.");
        return (usable[0], false, $"No plan matches your spec ({asked}){where}, so the cheapest plan for {osName} is used instead.");
    }

    /// <summary>Works the order out from Vultr's lists. Throws with a plain reason when it can't (unknown OS, region, key…).</summary>
    public static async Task<DeployPlan> PlanAsync(Api api, DeployRequest r, CancellationToken ct)
    {
        if (CheckName(r.Name) is { } bad) throw new InvalidOperationException(bad);
        if (r.Vcpus is < 1 or > 128 || r.RamGb is <= 0 or > 2048 || r.DiskGb is < 1 or > 100_000) throw new InvalidOperationException("the size is out of range (vCPU 1–128, RAM up to 2048 GB, disk 1–100000 GB)");
        var os = MatchOs(await api.List("/os", "os", ct), r.Os, out var osWhy) ?? throw new InvalidOperationException(osWhy);
        var regions = await api.List("/regions", "regions", ct);
        (string Id, string Text)? region = null;
        if (r.Region.Trim().Length > 0)
            region = MatchRegion(regions, r.Region) ?? throw new InvalidOperationException($"no Vultr region matches “{r.Region}” (try its id, e.g. lhr, ams, ewr, or a city)");
        var plans = await api.List("/plans", "plans", ct);
        string? regionId = region?.Id;
        if (regionId == null)
        {
            // No region asked for: where the account's servers already are (the most common), else the chosen plan's first location.
            var mine = (await api.List("/instances", "instances", ct)).Select(i => S(i, "region")).Where(x => x.Length > 0).GroupBy(x => x).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
            if (mine != null && Usable(plans, mine, os.Windows).Any()) regionId = mine;
        }
        var regionText = regionId == null ? "" : region?.Text ?? MatchRegion(regions, regionId)?.Text ?? regionId;
        var chosen = ChoosePlan(plans, regionId, os.Windows, r.Vcpus, r.RamGb, r.DiskGb, r.Plan, os.Name, regionText)
                     ?? throw new InvalidOperationException($"no Vultr plan{(regionId == null ? "" : " in " + regionText)} can run {os.Name}");
        if (regionId == null)
        {
            regionId = Strings(chosen.Plan, "locations").OrderBy(x => x, StringComparer.Ordinal).First();
            regionText = MatchRegion(regions, regionId)?.Text ?? regionId;
        }
        var keys = new List<(string, string)>();
        if (r.SshKeys is { Count: > 0 })
        {
            var all = await api.List("/ssh-keys", "ssh_keys", ct);
            foreach (var k in r.SshKeys.Where(k => k.Trim().Length > 0))
            {
                var hit = all.FirstOrDefault(x => S(x, "name").Equals(k.Trim(), StringComparison.OrdinalIgnoreCase) || S(x, "id") == k.Trim());
                if (hit.ValueKind == JsonValueKind.Undefined) throw new InvalidOperationException($"no Vultr SSH key named “{k.Trim()}” (Vultr → Account → SSH Keys)");
                keys.Add((S(hit, "id"), S(hit, "name")));
            }
        }
        var p = chosen.Plan;
        return new DeployPlan(r.Name.Trim(), Hostname(r.Name), os.Id, os.Name, os.Windows, regionId, regionText, S(p, "id"),
            (int)D(p, "vcpu_count"), (int)D(p, "ram"), (int)D(p, "disk"), D(p, "monthly_cost"), chosen.Matched, chosen.Note, keys);
    }

    /// <summary>The create request (POST /instances).</summary>
    public static JsonObject Body(DeployPlan p)
    {
        var b = new JsonObject
        {
            ["region"] = p.RegionId, ["plan"] = p.PlanId, ["os_id"] = p.OsId, ["label"] = p.Label, ["hostname"] = p.Hostname,
            ["enable_ipv6"] = true, ["backups"] = "disabled", ["tags"] = new JsonArray("ServerManagement"),
        };
        if (p.SshKeys.Count > 0) b["sshkey_id"] = new JsonArray(p.SshKeys.Select(k => (JsonNode?)k.Id).ToArray());
        return b;
    }

    /// <summary>Ready = active, running, the server finished installing, and an IPv4 address assigned.</summary>
    public static bool Ready(JsonElement inst) =>
        S(inst, "status") == "active" && S(inst, "power_status") == "running" && S(inst, "server_status") == "ok" && S(inst, "main_ip") is { Length: > 0 } ip && ip != "0.0.0.0";

    /// <summary>Polls the instance until it is <see cref="Ready"/> or <paramref name="limit"/> passes; returns the last answer.</summary>
    public static async Task<(JsonElement Instance, bool Ready)> WaitReadyAsync(Api api, string id, TimeSpan limit, TimeSpan every, Action<string>? progress, CancellationToken ct)
    {
        var until = DateTime.UtcNow + limit;
        JsonElement last = default;
        while (true)
        {
            var got = await api.Get("/instances/" + Uri.EscapeDataString(id), ct);
            last = got.ValueKind == JsonValueKind.Object && got.TryGetProperty("instance", out var i) ? i : got;
            if (Ready(last)) return (last, true);
            progress?.Invoke($"{S(last, "status")} · {S(last, "power_status")} · {S(last, "server_status")}");
            if (DateTime.UtcNow >= until) return (last, false);
            await Task.Delay(every, ct);
        }
    }
}
