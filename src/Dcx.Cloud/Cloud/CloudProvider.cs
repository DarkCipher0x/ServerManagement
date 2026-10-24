using System.Net.Http;
using System.Text.Json;
using System.Windows;
using DcxManager.Services;

namespace DcxManager.Cloud;

/// <summary>One signed-in account of a cloud provider (ADR-020). Pagination, authentication, retries and error parsing
/// are the implementation's business; callers see JSON and <see cref="CloudApiException"/>.</summary>
public interface ICloudApi
{
    CloudProvider Provider { get; }
    string AccountId { get; }
    string AccountName { get; }
    /// <summary>Any call. body may be null, a JSON string, a JsonElement or any serialisable object. idempotent = may be
    /// repeated after a rate-limit answer (false for orders).</summary>
    Task<JsonElement> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct = default, string actor = "you", bool idempotent = true);
    /// <summary>Every page of a list endpoint (bounded).</summary>
    Task<List<JsonElement>> ListAsync(string path, string? key, CancellationToken ct = default);
    /// <summary>The object inside a single-item response.</summary>
    JsonElement UnwrapItem(JsonElement response);
}

public static class CloudApiExtensions
{
    public static Task<JsonElement> GetAsync(this ICloudApi api, string path, CancellationToken ct = default) => api.SendAsync(HttpMethod.Get, path, null, ct);
    public static async Task<JsonElement> GetItemAsync(this ICloudApi api, string path, CancellationToken ct = default) => api.UnwrapItem(await api.GetAsync(path, ct));
}

/// <summary>A cloud API error with a message that is safe to show: no request, headers or credentials. RequestId is the
/// provider's support reference where it has one. Uncertain = a change timed out and may or may not have been applied.</summary>
public sealed class CloudApiException(string provider, int status, string message, string? requestId = null, bool uncertain = false) : Exception(message)
{
    public string Provider { get; } = provider;
    /// <summary>HTTP status; 0 = no answer (network, DNS, timeout).</summary>
    public int Status { get; } = status;
    public string? RequestId { get; } = requestId;
    public bool Uncertain { get; } = uncertain;
}

/// <summary>What a provider can do (shown in Settings, used by shared code such as the server view and the AI tools).
/// The screens themselves only offer what the provider's catalog defines, so nothing here can produce a dead control.</summary>
[Flags]
public enum CloudCaps
{
    None = 0, Create = 1, Delete = 2, CancelContract = 4, Start = 8, Stop = 16, Shutdown = 32, Reboot = 64, Reinstall = 128,
    Resize = 256, Rename = 512, Snapshots = 1024, Images = 2048, CustomImages = 4096, Rescue = 8192, ResetPassword = 16384,
    Console = 32768, SshKeys = 65536, PrivateNetworks = 131072, Firewalls = 262144, Dns = 524288, Backups = 1048576,
}

public enum CloudState { Running, Stopped, Busy, Error, Other }
public enum CloudPower { Start, Stop, Reboot }

/// <summary>An instance in provider-neutral terms (dashboard, host import, server view, AI tools).</summary>
public sealed record CloudInstance(string Provider, string AccountId, string Id, string Name, string Ipv4, string Ipv6, string Region,
    string Plan, string Os, CloudState State, string StatusText, int Cpu, double RamMb, string DefaultUser, JsonElement Raw);

/// <summary>Screens that follow a provider's active account (null provider = all of them, e.g. the dashboard).</summary>
public interface ICloudView
{
    CloudProvider? Provider { get; }
    void AccountChanged();
}

/// <summary>What a resource view hands to provider-specific actions.</summary>
public sealed record CloudViewContext(ICloudApi Api, ResourceDef Def, JRow? Row, IReadOnlyList<JRow> Selected, JsonElement? Parent,
    Func<Task> RefreshAsync, Action<string> Status, Window? Owner, string Crumb);

/// <summary>A provider-specific toolbar button on one of its screens (e.g. Vultr's invoice viewer).</summary>
public sealed record CloudExtra(string Label, Func<CloudViewContext, Task> Run, string? Tip = null, bool Primary = false);

/// <summary>
/// A cloud provider (ADR-020): its catalog of screens, accounts, capabilities and the few things shared code needs in
/// neutral terms. Everything provider-specific lives in the subclass; the rest of the app reads
/// <see cref="CloudProviders.All"/>.
/// </summary>
public abstract class CloudProvider
{
    public abstract string Id { get; }
    public abstract string Title { get; }
    public abstract ResourceDef[] Catalog { get; }
    public abstract string[] GroupOrder { get; }
    public abstract ResourceDef InstanceDef { get; }
    public abstract CloudCaps Caps { get; }
    /// <summary>Hosts the provider's API uses (Privacy view, offline mode explanations).</summary>
    public abstract string[] ApiHosts { get; }
    /// <summary>How to connect an account (shown where no account exists yet).</summary>
    public abstract string ConnectHelp { get; }

    public abstract IReadOnlyList<ICloudAccount> Accounts { get; }
    public abstract string? ActiveAccountId { get; set; }
    /// <summary>The API client for an account. 0.5.1 #10: refused (LockedException) while a padlock on the account is
    /// closed, so every caller (screens, dashboard, AI tools, add-ons) gets the same answer.</summary>
    public ICloudApi ApiFor(ICloudAccount account)
    {
        Locks.Demand(LockTarget(account), $"{Title} account “{account.Name}”");
        return CreateApi(account);
    }
    protected abstract ICloudApi CreateApi(ICloudAccount account);
    /// <summary>The lock target of an account ("cloud:vultr:&lt;id&gt;").</summary>
    public string LockTarget(ICloudAccount account) => $"cloud:{Id}:{account.Id}";
    public abstract CloudInstance? Normalize(JsonElement row, string accountId);
    public abstract Task PowerAsync(ICloudApi api, string instanceId, CloudPower action, CancellationToken ct = default, string actor = "you");

    public bool Has(CloudCaps c) => (Caps & c) == c;

    /// <summary>The active, enabled account (falls back to the first enabled one).</summary>
    public ICloudAccount? ActiveAccount => Accounts.FirstOrDefault(a => a.Id == ActiveAccountId && !a.Disabled) ?? Accounts.FirstOrDefault(a => !a.Disabled);
    public ICloudApi? Active => ActiveAccount is { } a ? ApiFor(a) : null;
    public ICloudApi Require() => Active ?? throw new InvalidOperationException($"No {Title} account is set up (or all are disabled). Add one in Settings → {Title} accounts.");

    /// <summary>A one-line account summary for the dashboard (e.g. balance), or null when the provider's API has none.</summary>
    public virtual Task<string?> AccountSummaryAsync(ICloudApi api, CancellationToken ct = default) => Task.FromResult<string?>(null);

    /// <summary>Out-of-band console address, or null when the provider has none.</summary>
    public virtual Task<string?> ConsoleUrlAsync(ICloudApi api, string instanceId, CancellationToken ct = default) => Task.FromResult<string?>(null);
    /// <summary>A provider-only Special action; false = not handled here.</summary>
    public virtual Task<bool> RunSpecialAsync(ActionDef a, CloudViewContext ctx) => Task.FromResult(false);
    public virtual IEnumerable<CloudExtra> ToolbarExtras(ResourceDef def) => Array.Empty<CloudExtra>();
    public virtual IEnumerable<object?> RowMenuExtras(ResourceDef def, CloudViewContext ctx) => Array.Empty<object?>();
    /// <summary>Double-click on a row: true when the provider handled it.</summary>
    public virtual Task<bool> DoubleClickAsync(CloudViewContext ctx) => Task.FromResult(false);

    public string NavKey(ResourceDef d) => $"{Id}:{d.Title}";
    /// <summary>The sidebar entry for the provider's reference lists.</summary>
    public virtual string ReferenceLabel => "◍  Reference lists";
    /// <summary>Extra sidebar entries of the provider (nav key, "glyph  text", tooltip), e.g. Vultr's API explorer and portal.</summary>
    public virtual IEnumerable<(string Key, string Text, string? Tip)> ExtraNav => Array.Empty<(string, string, string?)>();

    public async Task<List<CloudInstance>> ListInstancesAsync(ICloudApi api, CancellationToken ct = default) =>
        (await api.ListAsync(InstanceDef.Path, InstanceDef.Key, ct)).Select(e => Normalize(e, api.AccountId)).OfType<CloudInstance>().ToList();

    /// <summary>Raised when the active account of this provider changes (views reload).</summary>
    public static event Action<CloudProvider>? ActiveAccountChanged;
    public void SetActive(string accountId)
    {
        if (ActiveAccountId == accountId) return;
        ActiveAccountId = accountId;
        try { AppSettings.Save(); } catch (Exception ex) { Log.Write("Save active cloud account", ex); }
        ActiveAccountChanged?.Invoke(this);
    }

    /// <summary>Accounts were added, removed, disabled or given new credentials: open screens of this provider reload
    /// (a screen opened before the first account existed would otherwise stay empty).</summary>
    public void AccountsChanged() => ActiveAccountChanged?.Invoke(this);
}

public static class CloudProviders
{
    public static IReadOnlyList<CloudProvider> All { get; } = new CloudProvider[] { Vultr.VultrProvider.Instance, Contabo.ContaboProvider.Instance, Hetzner.HetznerProvider.Instance, Azure.AzureProvider.Instance, Gcp.GcpProvider.Instance };
    public static CloudProvider? Find(string? id) => All.FirstOrDefault(p => p.Id == id);

    /// <summary>The provider instance behind a saved host, if it came from one (Vultr's older fields included).</summary>
    public static (CloudProvider Provider, string InstanceId, string? AccountId)? ForHost(HostEntry h)
    {
        if (Find(h.CloudProvider) is { } p && h.CloudInstanceId is { Length: > 0 } id) return (p, id, h.CloudAccountId);
        if (h.VultrInstanceId is { Length: > 0 } vid) return (Vultr.VultrProvider.Instance, vid, h.VultrAccountId);
        return null;
    }

    /// <summary>The account a host's instance belongs to (its own account if still present and enabled, else the active one).</summary>
    public static ICloudApi? ApiForHost(HostEntry h)
    {
        if (ForHost(h) is not { } link) return null;
        var acct = link.Provider.Accounts.FirstOrDefault(a => a.Id == link.AccountId && !a.Disabled) ?? link.Provider.ActiveAccount;
        return acct == null ? null : link.Provider.ApiFor(acct);
    }
}
