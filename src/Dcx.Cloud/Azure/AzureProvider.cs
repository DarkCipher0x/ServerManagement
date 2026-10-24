using System.Net.Http;
using System.Text.Json;
using DcxManager.Services;

namespace DcxManager.Azure;

/// <summary>Microsoft Azure as a <see cref="CloudProvider"/> (0.2.0). One account = one subscription, reached through
/// an Entra app registration that signs in with a certificate (<see cref="AzureAuth"/>). New subscriptions are read-only
/// until changes are allowed in Settings. Instance ids are Azure resource ids. No console (the serial console is a
/// WebSocket protocol the app has no client for); VM list rows carry no IP addresses (those live on network interfaces),
/// so SSH and "add to hosts" ask for the address.</summary>
public sealed class AzureProvider : CloudProvider
{
    public static AzureProvider Instance { get; } = new();
    private AzureProvider() { }

    private const string ComputeVersion = "2024-07-01";

    public override string Id => "azure";
    public override string Title => "Microsoft Azure";
    public override ResourceDef[] Catalog => AzureCatalog.All;
    public override string[] GroupOrder => AzureCatalog.GroupOrder;
    public override ResourceDef InstanceDef => AzureCatalog.VirtualMachine;
    public override CloudCaps Caps => CloudCaps.Create | CloudCaps.Delete | CloudCaps.Start | CloudCaps.Stop | CloudCaps.Shutdown | CloudCaps.Reboot
        | CloudCaps.Resize | CloudCaps.Snapshots | CloudCaps.PrivateNetworks | CloudCaps.Firewalls | CloudCaps.Dns;
    public override string[] ApiHosts => new[] { "management.azure.com", "login.microsoftonline.com" };
    public override string ConnectHelp =>
        "Connect an Azure subscription:\n\n1. Entra admin center → App registrations → New registration (single tenant)\n" +
        "2. Make a certificate (PFX with its private key), upload its public half under Certificates & secrets → Certificates\n" +
        "3. Subscription → Access control (IAM) → give the app “Reader” to only look, or e.g. “Contributor” to make changes\n" +
        "4. Settings → Cloud providers → Azure subscriptions → ＋ Add subscription: tenant id, client id, subscription id and the PFX, then Test\n\n" +
        "The certificate is stored in the encrypted store and never shown again; client secrets are not accepted. " +
        "A new subscription is read-only in this app until you allow changes for it.";

    public override string ReferenceLabel => "◍  Locations";
    public override IReadOnlyList<ICloudAccount> Accounts => AppSettings.Current.AzureAccounts;
    public override string? ActiveAccountId { get => AppSettings.Current.ActiveAzureAccountId; set => AppSettings.Current.ActiveAzureAccountId = value; }
    protected override ICloudApi CreateApi(ICloudAccount account) => new AzureClient((AzureAccount)account);

    /// <summary>Azure VM power states (instance view "PowerState/…") in neutral terms.</summary>
    public static CloudState StateOf(string power) => power switch
    {
        "running" => CloudState.Running,
        "deallocated" or "stopped" => CloudState.Stopped,
        "starting" or "stopping" or "deallocating" => CloudState.Busy,
        _ => CloudState.Other,
    };

    public override CloudInstance? Normalize(JsonElement r, string accountId)
    {
        var id = J.Str(r, "id");
        if (id.Length == 0) return null;
        var power = J.Str(r, "_power");
        var os = J.Str(r, "properties.storageProfile.imageReference.offer") is { Length: > 0 } offer
            ? $"{offer} {J.Str(r, "properties.storageProfile.imageReference.sku")}".Trim() : J.Str(r, "properties.storageProfile.osDisk.osType");
        var user = J.Str(r, "properties.osProfile.adminUsername") is { Length: > 0 } u ? u : "azureuser";
        return new CloudInstance("azure", accountId, id, J.Str(r, "name"), "", "", J.Str(r, "location"), J.Str(r, "properties.hardwareProfile.vmSize"), os,
            StateOf(power), power.Length > 0 ? power : J.Str(r, "properties.provisioningState"), 0, 0, user, r);
    }

    /// <summary>Stop = deallocate (billing stops), the only stop that makes sense from a management tool.</summary>
    public override Task PowerAsync(ICloudApi api, string instanceId, CloudPower action, CancellationToken ct = default, string actor = "you")
    {
        var path = Tpl.SafePath(instanceId);
        if (path.Length == 0) throw new CloudApiException("Azure", 0, "That isn't an Azure resource id.");
        var verb = action switch { CloudPower.Start => "start", CloudPower.Stop => "deallocate", _ => "restart" };
        return api.SendAsync(HttpMethod.Post, $"{path}/{verb}?api-version={ComputeVersion}", null, ct, actor);
    }

    public override async Task<string?> AccountSummaryAsync(ICloudApi api, CancellationToken ct = default)
    {
        var list = await ListInstancesAsync(api, ct);
        var ro = api is AzureClient && AppSettings.Current.AzureAccounts.FirstOrDefault(a => a.Id == api.AccountId) is { ReadOnly: true } ? " · read-only" : "";
        return $"{list.Count} VM(s), {list.Count(i => i.State == CloudState.Running)} running{ro}";
    }

    public override async Task<bool> RunSpecialAsync(ActionDef a, CloudViewContext c)
    {
        switch (a.Special)
        {
            case "resize":
                if (c.Row is not { } row) { Ui.Dialogs.Info("Resize", "Select a virtual machine first."); return true; }
                var id = Tpl.SafePath(J.Str(row.Raw, "id"));
                if (id.Length == 0) return true;
                c.Status("Reading the sizes this VM can move to…");
                var sizes = await c.Api.ListAsync($"{id}/vmSizes?api-version={ComputeVersion}", "value");
                var current = J.Str(row.Raw, "properties.hardwareProfile.vmSize");
                var labels = sizes.Select(s => (Name: J.Str(s, "name"), Text: $"{J.Str(s, "name")}   {J.Str(s, "numberOfCores")} vCPU   {J.Str(s, "memoryInMB")} MB"))
                    .Where(s => s.Name.Length > 0 && s.Name != current).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
                c.Status("");
                if (labels.Count == 0) { Ui.Dialogs.Info("Resize", "Azure offers no other size for this VM where it is now (deallocate it to see every size of the region)."); return true; }
                if (Ui.Dialogs.Pick("Resize " + row[c.Def.NameField], $"Current size: {current}. Move to:", labels.Select(l => l.Text).ToList(), t => t) is not { } pick) return true;
                var size = labels.First(l => l.Text == pick).Name;
                if (!Ui.Dialogs.Confirm("Resize", $"Resize {row[c.Def.NameField]} from {current} to {size}? A running VM restarts.", "Resize", danger: true)) return true;
                c.Status($"Resizing to {size}…");
                await c.Api.SendAsync(HttpMethod.Patch, $"{id}?api-version={ComputeVersion}", new { properties = new { hardwareProfile = new { vmSize = size } } });
                c.Status($"Resized to {size}.");
                await c.RefreshAsync();
                return true;
            case "explore":
                if (Ui.Dialogs.Prompt("Azure API explorer", "GET which Resource Manager path? (_SUB_ = this subscription; include the api-version)",
                        $"/subscriptions/{AzureClient.Sub}/providers?api-version=2021-04-01") is not { Length: > 0 } path) return true;
                c.Status("Reading…");
                var j = await c.Api.GetAsync(path.Trim());
                Ui.Dialogs.ShowText("GET " + CloudHttp.StripQuery(path.Trim()), J.Pretty(j));
                c.Status("Read.");
                return true;
            default:
                return false;
        }
    }
}
