using System.Net.Http;
using System.Text.Json;
using DcxManager.Services;
using DcxManager.Views;

namespace DcxManager.Hetzner;

/// <summary>Hetzner Cloud as a <see cref="CloudProvider"/> (ADR-028). One account = one Hetzner project (one API token).
/// Capabilities are what the Cloud API offers and the app implements: no web console (Hetzner's console is VNC over a
/// WebSocket, which the app has no client for), so <see cref="CloudCaps.Console"/> is not claimed.</summary>
public sealed class HetznerProvider : CloudProvider
{
    public static HetznerProvider Instance { get; } = new();
    private HetznerProvider() { }

    public override string Id => "hetzner";
    public override string Title => "Hetzner Cloud";
    public override ResourceDef[] Catalog => HetznerCatalog.All;
    public override string[] GroupOrder => HetznerCatalog.GroupOrder;
    public override ResourceDef InstanceDef => HetznerCatalog.Server;
    public override CloudCaps Caps => CloudCaps.Create | CloudCaps.Delete | CloudCaps.Start | CloudCaps.Stop | CloudCaps.Shutdown | CloudCaps.Reboot
        | CloudCaps.Reinstall | CloudCaps.Resize | CloudCaps.Rename | CloudCaps.Snapshots | CloudCaps.Images | CloudCaps.Rescue | CloudCaps.ResetPassword
        | CloudCaps.SshKeys | CloudCaps.PrivateNetworks | CloudCaps.Firewalls | CloudCaps.Dns | CloudCaps.Backups;
    public override string[] ApiHosts => new[] { "api.hetzner.cloud" };
    public override string ConnectHelp =>
        "Connect a Hetzner Cloud project:\n\n1. Hetzner Console → your project → Security → API tokens → Generate API token\n" +
        "2. Choose “Read” to only look, or “Read & Write” to make changes\n" +
        "3. Settings → Cloud providers → Hetzner Cloud projects → ＋ Add project, paste the token, then Test\n\n" +
        "The token is stored in the encrypted store and never shown again. Each project needs its own token.";

    public override string ReferenceLabel => "◍  Locations, types & pricing";
    public override IReadOnlyList<ICloudAccount> Accounts => AppSettings.Current.HetznerAccounts;
    public override string? ActiveAccountId { get => AppSettings.Current.ActiveHetznerAccountId; set => AppSettings.Current.ActiveHetznerAccountId = value; }
    protected override ICloudApi CreateApi(ICloudAccount account) => new HetznerClient((HetznerAccount)account);

    /// <summary>Hetzner server states (spec) in neutral terms.</summary>
    public static CloudState StateOf(string status) => status switch
    {
        "running" => CloudState.Running,
        "off" => CloudState.Stopped,
        "initializing" or "starting" or "stopping" or "deleting" or "migrating" or "rebuilding" => CloudState.Busy,
        _ => CloudState.Other,   // unknown
    };

    public override CloudInstance? Normalize(JsonElement r, string accountId)
    {
        var id = J.Str(r, "id");
        if (id.Length == 0) return null;
        var status = J.Str(r, "status");
        int.TryParse(J.Str(r, "server_type.cores"), out var cpu);
        double.TryParse(J.Str(r, "server_type.memory"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ramGb);
        var os = J.Str(r, "image.description") is { Length: > 0 } d ? d : J.Str(r, "image.name");
        return new CloudInstance("hetzner", accountId, id, J.Str(r, "name"), J.Str(r, "public_net.ipv4.ip"), J.Str(r, "public_net.ipv6.ip"), J.Str(r, "location.name"),
            J.Str(r, "server_type.name"), os, StateOf(status), status, cpu, ramGb * 1024, "root", r);
    }

    public override Task PowerAsync(ICloudApi api, string instanceId, CloudPower action, CancellationToken ct = default, string actor = "you") =>
        api.SendAsync(HttpMethod.Post, $"/servers/{Uri.EscapeDataString(instanceId)}/actions/{action switch { CloudPower.Start => "poweron", CloudPower.Stop => "poweroff", _ => "reboot" }}", null, ct, actor);

    public override async Task<string?> AccountSummaryAsync(ICloudApi api, CancellationToken ct = default)
    {
        var list = await ListInstancesAsync(api, ct);
        var rl = HetznerClient.RateLimitOf(api.AccountId);
        return $"{list.Count} server(s), {list.Count(i => i.State == CloudState.Running)} running" + (rl is { } l ? $" · API budget {l.Remaining}/{l.Limit} this hour" : "");
    }

    public override async Task<bool> RunSpecialAsync(ActionDef a, CloudViewContext c)
    {
        if (a.Special == "metrics")
        {
            if (c.Row is not { } row) { Ui.Dialogs.Info(a.Label.Trim(), "Select a row first."); return true; }
            c.Status("Reading metrics…");
            var j = await c.Api.GetAsync(MetricsPath(Tpl.Fill(a.Path, row.Raw, c.Parent, url: true), DateTime.UtcNow));
            Ui.Dialogs.ShowText($"Metrics — {row[c.Def.NameField]}", FormatMetrics(j));
            c.Status("Metrics read (last hour).");
            return true;
        }
        if (a.Special != "uploadkey") return false;
        var keys = AppData.Current.Credentials.Where(k => k.Kind == CredentialKind.PrivateKey).ToList();
        if (keys.Count == 0) { Ui.Dialogs.Info("Upload", "No keys in the vault yet (Credentials → Add)."); return true; }
        if (Ui.Dialogs.Pick("Upload SSH key", "Which key's public half should be added to this Hetzner project?", keys, k => $"{k.Name}   {k.KeyType}   {k.Fingerprint}") is not { } key) return true;
        await UploadPublicKeyAsync(c.Api, key);
        await c.RefreshAsync();
        return true;
    }

    /// <summary>The metrics query for the last hour at one-minute steps (spec: type is a repeated array parameter,
    /// start / end are RFC 3339). Servers: cpu, disk, network; load balancers: connections, requests, bandwidth.</summary>
    public static string MetricsPath(string itemMetricsPath, DateTime utcNow)
    {
        var types = itemMetricsPath.StartsWith("/servers/", StringComparison.Ordinal) ? new[] { "cpu", "disk", "network" } : new[] { "open_connections", "connections_per_second", "requests_per_second", "bandwidth" };
        string T(DateTime t) => Uri.EscapeDataString(t.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture));
        return itemMetricsPath + "?" + string.Join("&", types.Select(t => "type=" + t)) + $"&start={T(utcNow.AddHours(-1))}&end={T(utcNow)}&step=60";
    }

    /// <summary>metrics.time_series → one line per series: average, peak and latest value (untrusted: bad points are skipped).</summary>
    public static string FormatMetrics(JsonElement response)
    {
        var series = J.Get(response, "metrics.time_series");
        if (series.ValueKind != JsonValueKind.Object) return "(no metrics returned)";
        var sb = new System.Text.StringBuilder($"{J.Str(response, "metrics.start")} → {J.Str(response, "metrics.end")} · step {J.Str(response, "metrics.step")} s\n\n");
        foreach (var s in series.EnumerateObject().OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            var vals = new List<double>();
            if (s.Value.TryGetProperty("values", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var p in arr.EnumerateArray())
                    if (p.ValueKind == JsonValueKind.Array && p.GetArrayLength() == 2 && double.TryParse(p[1].ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && double.IsFinite(v))
                        vals.Add(v);
            sb.Append($"{s.Name,-34} ").Append(vals.Count == 0 ? "no data" : $"avg {vals.Average(),12:0.###}   peak {vals.Max(),12:0.###}   latest {vals[^1],12:0.###}   ({vals.Count} points)").Append('\n');
        }
        return sb.Append("\nUnits as Hetzner reports them: CPU in %, disk in IOPS and bytes/s, network in packets and bytes/s.").ToString();
    }

    /// <summary>Adds the public half of a vault key to the project (the private key never leaves the vault).</summary>
    public static async Task UploadPublicKeyAsync(ICloudApi api, Credential key, string actor = "you")
    {
        var pub = key.PublicKey;
        if (string.IsNullOrWhiteSpace(pub)) throw new InvalidOperationException("This key has no public half stored (re-import it in Credentials).");
        await api.SendAsync(HttpMethod.Post, "/ssh_keys", new { name = key.Name.Length > 64 ? key.Name[..64] : key.Name, public_key = pub.Trim() }, actor: actor, idempotent: false);
    }
}
