using System.Net.Http;
using System.Text.Json;
using DcxManager.Services;
using DcxManager.Views;

namespace DcxManager.Contabo;

/// <summary>Contabo as a <see cref="CloudProvider"/> (ADR-020). Capabilities are what Contabo's API offers — nothing more:
/// no delete (contracts are cancelled), no resize (no product change), no web console.</summary>
public sealed class ContaboProvider : CloudProvider
{
    public static ContaboProvider Instance { get; } = new();
    private ContaboProvider() { }

    public override string Id => "contabo";
    public override string Title => "Contabo";
    public override ResourceDef[] Catalog => ContaboCatalog.All;
    public override string[] GroupOrder => ContaboCatalog.GroupOrder;
    public override ResourceDef InstanceDef => ContaboCatalog.Instance;
    public override CloudCaps Caps => CloudCaps.Create | CloudCaps.CancelContract | CloudCaps.Start | CloudCaps.Stop | CloudCaps.Shutdown | CloudCaps.Reboot
        | CloudCaps.Reinstall | CloudCaps.Rename | CloudCaps.Snapshots | CloudCaps.Images | CloudCaps.CustomImages | CloudCaps.Rescue | CloudCaps.ResetPassword
        | CloudCaps.SshKeys | CloudCaps.PrivateNetworks | CloudCaps.Firewalls | CloudCaps.Dns;
    public override string[] ApiHosts => new[] { "api.contabo.com", "auth.contabo.com" };
    public override string ConnectHelp =>
        "Connect a Contabo account:\n\n1. Contabo Customer Control Panel → API (my.contabo.com/api/details)\n" +
        "2. Note the Client ID and Client Secret, and set an API password there (your API user is your Contabo login e-mail)\n" +
        "3. Settings → Contabo accounts → ＋ Add account, then Test\n\nAll four values are stored in the encrypted store and are never shown again.";

    public override string ReferenceLabel => "◍  Data centers, images & history";
    public override IReadOnlyList<ICloudAccount> Accounts => AppSettings.Current.ContaboAccounts;
    public override string? ActiveAccountId { get => AppSettings.Current.ActiveContaboAccountId; set => AppSettings.Current.ActiveContaboAccountId = value; }
    protected override ICloudApi CreateApi(ICloudAccount account) => new ContaboClient((ContaboAccount)account);

    /// <summary>Contabo states (spec instanceStatus) in neutral terms.</summary>
    public static CloudState StateOf(string status) => status switch
    {
        "running" => CloudState.Running,
        "stopped" => CloudState.Stopped,
        "provisioning" or "installing" or "manual_provisioning" or "reset_password" or "pending_payment" or "verification_required" => CloudState.Busy,
        "error" or "product_not_available" => CloudState.Error,
        _ => CloudState.Other,   // uninstalled, rescue, unknown, other
    };

    public override CloudInstance? Normalize(JsonElement r, string accountId)
    {
        var id = J.Str(r, "instanceId");
        if (id.Length == 0) return null;
        var status = J.Str(r, "status");
        double.TryParse(J.Str(r, "ramMb"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ram);
        int.TryParse(J.Str(r, "cpuCores"), out var cpu);
        var name = J.Str(r, "displayName") is { Length: > 0 } dn ? dn : J.Str(r, "name");
        return new CloudInstance("contabo", accountId, id, name, J.Str(r, "ipConfig.v4.ip"), J.Str(r, "ipConfig.v6.ip"), J.Str(r, "region"),
            J.Str(r, "productName") is { Length: > 0 } pn ? pn : J.Str(r, "productId"), J.Str(r, "osType"), StateOf(status), status, cpu, ram, J.Str(r, "defaultUser"), r);
    }

    public override Task PowerAsync(ICloudApi api, string instanceId, CloudPower action, CancellationToken ct = default, string actor = "you") =>
        api.SendAsync(HttpMethod.Post, $"/compute/instances/{Uri.EscapeDataString(instanceId)}/actions/{action switch { CloudPower.Start => "start", CloudPower.Stop => "stop", _ => "restart" }}", null, ct, actor);

    public override async Task<bool> RunSpecialAsync(ActionDef a, CloudViewContext c)
    {
        if (a.Special != "uploadkey") return false;
        var keys = AppData.Current.Credentials.Where(k => k.Kind == CredentialKind.PrivateKey).ToList();
        if (keys.Count == 0) { Ui.Dialogs.Info("Upload", "No keys in the vault yet (Credentials → Add)."); return true; }
        if (Ui.Dialogs.Pick("Upload SSH key", "Which key's public half should be added to Contabo Secrets?", keys, k => $"{k.Name}   {k.KeyType}   {k.Fingerprint}") is not { } key) return true;
        await UploadPublicKeyAsync(c.Api, key);
        await c.RefreshAsync();
        return true;
    }

    /// <summary>Adds the public half of a vault key as an "ssh" secret (the private key never leaves the vault).</summary>
    public static async Task UploadPublicKeyAsync(ICloudApi api, Credential key, string actor = "you")
    {
        var pub = key.PublicKey;
        if (string.IsNullOrWhiteSpace(pub)) throw new InvalidOperationException("This key has no public half stored (re-import it in Credentials).");
        await api.SendAsync(HttpMethod.Post, "/secrets", new { name = key.Name.Length > 255 ? key.Name[..255] : key.Name, value = pub, type = "ssh" }, actor: actor, idempotent: false);
    }
}
