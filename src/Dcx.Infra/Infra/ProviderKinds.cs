using System.Net.Http;

namespace DcxManager.Infra;

/// <summary>An extra text field of a provider's add / edit form (a region, a workspace id): read from and written to the
/// configuration, validated before Save.</summary>
public sealed record ProviderField(string Label, Func<ProviderConfig, string> Get, Action<ProviderConfig, string> Set, Func<string, string?> Validate, string[]? Options = null);

/// <summary>
/// Everything the app needs to know about one kind of provider, in the provider's own file (0.6.33, DarkCipher0x: "keep
/// the organised and secure principal to the code ... efficient easy to manage code"): its title and menu entry, how to
/// build it, the object kinds its filter lists, and its add / edit form (address, vault credential, extra fields, hint,
/// validation). The Infrastructure screen draws the form from this, so a new kind is one file plus one line in
/// <see cref="ProviderKinds.All"/> — no new branches in the screen.
/// </summary>
public sealed record ProviderKind(
    string Kind,
    string Title,
    string MenuLabel,
    // the Add menu's group (kinds with the same group share a submenu once the menu is long)
    string MenuGroup,
    Func<ProviderConfig> New,
    Func<ProviderConfig, HttpMessageHandler?, IInfraProvider> Create,
    string[] ObjectKinds,
    // the address field's label, or null when the service's address is fixed
    string? EndpointLabel,
    Func<string, string?> ValidateEndpoint,
    string CredentialLabel,
    // the credential must have a user name (a client id, a BMC account) as well as a secret
    bool NeedsUser,
    string Hint,
    // the certificate is pinned on first use (a changed address forgets the pin)
    bool PinsTls,
    IReadOnlyList<ProviderField> Fields);

public static class ProviderKinds
{
    /// <summary>The kinds described this way (the older kinds still have their own branches in the screen).</summary>
    public static readonly IReadOnlyList<ProviderKind> All = [RedfishProvider.Descriptor, GreenLakeProvider.Descriptor, SophosCentralProvider.Descriptor];

    public static ProviderKind? Find(string kind) => All.FirstOrDefault(k => k.Kind == kind);

    /// <summary>Why the form's values can't be saved, or null.</summary>
    public static string? Validate(ProviderKind k, string endpoint, IReadOnlyList<string> fields)
    {
        if (k.EndpointLabel != null && k.ValidateEndpoint(endpoint.Trim()) is { } bad) return bad;
        for (int i = 0; i < k.Fields.Count && i < fields.Count; i++)
            if (k.Fields[i].Validate(fields[i].Trim()) is { } why) return why;
        return null;
    }
}
