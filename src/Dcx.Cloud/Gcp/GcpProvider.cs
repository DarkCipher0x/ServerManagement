using System.Net.Http;
using System.Text.Json;
using DcxManager.Cloud;
using DcxManager.Services;

namespace DcxManager.Gcp;

/// <summary>Google Cloud as a <see cref="CloudProvider"/> (0.2.0). One account = one project, reached through a service
/// account's key file (<see cref="GcpAuth"/>). New projects are read-only until changes are allowed in Settings (and
/// then the token carries the full scope). Instance ids are the VM's own path (from selfLink). No console (the serial
/// console is interactive over a WebSocket, which the app has no client for).</summary>
public sealed class GcpProvider : CloudProvider
{
    public static GcpProvider Instance { get; } = new();
    private GcpProvider() { }

    public override string Id => "gcp";
    public override string Title => "Google Cloud";
    public override ResourceDef[] Catalog => GcpCatalog.All;
    public override string[] GroupOrder => GcpCatalog.GroupOrder;
    public override ResourceDef InstanceDef => GcpCatalog.Instance;
    public override CloudCaps Caps => CloudCaps.Create | CloudCaps.Delete | CloudCaps.Start | CloudCaps.Stop | CloudCaps.Shutdown | CloudCaps.Reboot
        | CloudCaps.Resize | CloudCaps.Snapshots | CloudCaps.CustomImages | CloudCaps.PrivateNetworks | CloudCaps.Firewalls | CloudCaps.Dns;
    public override string[] ApiHosts => GcpClient.Hosts.Append("oauth2.googleapis.com").ToArray();
    public override string ConnectHelp =>
        "Connect a Google Cloud project:\n\n1. Google Cloud console → IAM & Admin → Service accounts → Create service account\n" +
        "2. Grant it “Viewer” to only look, or roles for the changes you want (e.g. Compute Admin)\n" +
        "3. The service account → Keys → Add key → JSON, and save the file\n" +
        "4. Settings → Cloud providers → Google Cloud projects → ＋ Add project, choose the key file, then Test\n\n" +
        "The key is stored in the encrypted store and never shown again (delete the downloaded file afterwards). " +
        "A new project is read-only in this app until you allow changes for it. The APIs you use must be enabled in the project.";

    public override string ReferenceLabel => "◍  Regions & API explorer";
    public override IReadOnlyList<ICloudAccount> Accounts => AppSettings.Current.GcpAccounts;
    public override string? ActiveAccountId { get => AppSettings.Current.ActiveGcpAccountId; set => AppSettings.Current.ActiveGcpAccountId = value; }
    protected override ICloudApi CreateApi(ICloudAccount account) => new GcpClient((GcpAccount)account);

    /// <summary>Compute Engine VM states in neutral terms.</summary>
    public static CloudState StateOf(string status) => status switch
    {
        "RUNNING" => CloudState.Running,
        "TERMINATED" or "SUSPENDED" => CloudState.Stopped,
        "PROVISIONING" or "STAGING" or "STOPPING" or "SUSPENDING" or "REPAIRING" => CloudState.Busy,
        _ => CloudState.Other,
    };

    public override CloudInstance? Normalize(JsonElement r, string accountId)
    {
        var self = J.Str(r, "_self");
        if (self.Length == 0) return null;
        var status = J.Str(r, "status");
        string os = "";
        if (J.Get(r, "disks") is { ValueKind: JsonValueKind.Array } disks)
            foreach (var d in disks.EnumerateArray())
                if (J.Str(d, "boot") == "yes" && J.Get(d, "licenses") is { ValueKind: JsonValueKind.Array } lic && lic.GetArrayLength() > 0)
                { var l = lic[0].ValueKind == JsonValueKind.String ? lic[0].GetString() ?? "" : ""; os = l[(l.LastIndexOf('/') + 1)..]; break; }
        return new CloudInstance("gcp", accountId, self, J.Str(r, "name"), J.Str(r, "_ip"), "", J.Str(r, "_zone"), J.Str(r, "_type"), os,
            StateOf(status), status, 0, 0, "", r);
    }

    /// <summary>Stop = Compute Engine's stop (a clean shutdown; billing for CPU and memory stops); reboot = reset.</summary>
    public override Task PowerAsync(ICloudApi api, string instanceId, CloudPower action, CancellationToken ct = default, string actor = "you")
    {
        var path = Tpl.SafePath(instanceId);
        if (path.Length == 0 || !path.StartsWith("/compute/v1/projects/", StringComparison.Ordinal)) throw new CloudApiException("Google Cloud", 0, "That isn't a Compute Engine VM path.");
        var verb = action switch { CloudPower.Start => "start", CloudPower.Stop => "stop", _ => "reset" };
        return api.SendAsync(HttpMethod.Post, $"https://compute.googleapis.com{path}/{verb}", null, ct, actor);
    }

    public override async Task<string?> AccountSummaryAsync(ICloudApi api, CancellationToken ct = default)
    {
        var list = await ListInstancesAsync(api, ct);
        var ro = AppSettings.Current.GcpAccounts.FirstOrDefault(a => a.Id == api.AccountId) is { ReadOnly: true } ? " · read-only" : "";
        return $"{list.Count} VM(s), {list.Count(i => i.State == CloudState.Running)} running{ro}";
    }

    public override async Task<bool> RunSpecialAsync(ActionDef a, CloudViewContext c)
    {
        switch (a.Special)
        {
            case "resize":
                if (c.Row is not { } row) { Ui.Dialogs.Info("Change machine type", "Select a VM first."); return true; }
                var self = Tpl.SafePath(J.Str(row.Raw, "_self"));
                var zone = J.Str(row.Raw, "_zone");
                if (self.Length == 0 || zone.Length == 0) return true;
                if (J.Str(row.Raw, "status") != "TERMINATED") { Ui.Dialogs.Info("Change machine type", "Stop the VM first: Google only changes the machine type of a stopped VM."); return true; }
                c.Status("Reading the machine types of " + zone + "…");
                var types = await c.Api.ListAsync($"https://compute.googleapis.com/compute/v1/projects/{GcpClient.Project}/zones/{Uri.EscapeDataString(zone)}/machineTypes", "items");
                var current = J.Str(row.Raw, "_type");
                var labels = types.Select(t => (Name: J.Str(t, "name"), Text: $"{J.Str(t, "name")}   {J.Str(t, "guestCpus")} vCPU   {J.Str(t, "memoryMb")} MB"))
                    .Where(t => t.Name.Length > 0 && t.Name != current).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
                c.Status("");
                if (labels.Count == 0) { Ui.Dialogs.Info("Change machine type", "Google offers no other machine type in this zone."); return true; }
                if (Ui.Dialogs.Pick("Machine type of " + row[c.Def.NameField], $"Current: {current}. Change to:", labels.Select(l => l.Text).ToList(), t => t) is not { } pick) return true;
                var type = labels.First(l => l.Text == pick).Name;
                if (!Ui.Dialogs.Confirm("Change machine type", $"Change {row[c.Def.NameField]} from {current} to {type}? Start it again afterwards.", "Change", danger: true)) return true;
                c.Status($"Changing to {type}…");
                await c.Api.SendAsync(HttpMethod.Post, $"https://compute.googleapis.com{self}/setMachineType", new { machineType = $"zones/{zone}/machineTypes/{type}" });
                c.Status($"Machine type is now {type}.");
                await c.RefreshAsync();
                return true;
            case "explore":
                if (Ui.Dialogs.Prompt("Google Cloud API explorer", $"GET which URL? (_PROJECT_ = this project; hosts: {string.Join(", ", GcpClient.Hosts)})",
                        $"https://compute.googleapis.com/compute/v1/projects/{GcpClient.Project}/zones") is not { Length: > 0 } url) return true;
                c.Status("Reading…");
                var j = await c.Api.GetAsync(url.Trim());
                Ui.Dialogs.ShowText("GET " + CloudHttp.StripQuery(url.Trim()), J.Pretty(j));
                c.Status("Read.");
                return true;
            default:
                return false;
        }
    }
}
