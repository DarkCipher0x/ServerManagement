namespace DcxManager.Services;

/// <summary>
/// What users may add and change (0.5.1 #11, ADR-067): every kind of thing that can be added, edited or removed has a
/// setting that Group Policy can switch off (<c>Allow…</c>, category "What users may add and change"). Every place in the
/// app that adds, edits or removes that kind of thing asks here first — the screens, the AI's tools, add-ons
/// (app.call / hosts.add…), imports and the local API alike — so a switched-off action is refused everywhere, and says
/// which policy refused it. A self-test checks that every action listed here has a setting and a call site.
/// </summary>
public static class Restrictions
{
    /// <summary>Every action: its key, what it is called, and the setting that allows it.</summary>
    public static readonly IReadOnlyList<(string Action, string What, string Setting, Func<AppSettings, bool> Allowed)> All = new (string, string, string, Func<AppSettings, bool>)[]
    {
        ("host.add", "adding hosts", nameof(AppSettings.AllowHostAdd), s => s.AllowHostAdd),
        ("host.edit", "editing hosts", nameof(AppSettings.AllowHostEdit), s => s.AllowHostEdit),
        ("host.delete", "deleting hosts", nameof(AppSettings.AllowHostDelete), s => s.AllowHostDelete),
        ("folder.edit", "creating, renaming, moving and deleting host folders", nameof(AppSettings.AllowFolderEdit), s => s.AllowFolderEdit),
        ("credential.add", "adding credentials", nameof(AppSettings.AllowCredentialAdd), s => s.AllowCredentialAdd),
        ("credential.edit", "editing credentials", nameof(AppSettings.AllowCredentialEdit), s => s.AllowCredentialEdit),
        ("credential.delete", "deleting credentials", nameof(AppSettings.AllowCredentialDelete), s => s.AllowCredentialDelete),
        ("cloud.add", "adding cloud accounts", nameof(AppSettings.AllowCloudAccountAdd), s => s.AllowCloudAccountAdd),
        ("cloud.edit", "editing and removing cloud accounts", nameof(AppSettings.AllowCloudAccountEdit), s => s.AllowCloudAccountEdit),
        ("infra.add", "adding infrastructure providers", nameof(AppSettings.AllowInfraAdd), s => s.AllowInfraAdd),
        ("infra.edit", "editing infrastructure providers", nameof(AppSettings.AllowInfraEdit), s => s.AllowInfraEdit),
        ("infra.delete", "removing infrastructure providers", nameof(AppSettings.AllowInfraDelete), s => s.AllowInfraDelete),
        ("portal.add", "adding portals", nameof(AppSettings.AllowPortalAdd), s => s.AllowPortalAdd),
        ("portal.edit", "editing and removing portals", nameof(AppSettings.AllowPortalEdit), s => s.AllowPortalEdit),
        ("tunnel.add", "adding tunnels and VPN profiles", nameof(AppSettings.AllowTunnelAdd), s => s.AllowTunnelAdd),
        ("snippet.edit", "adding, editing and deleting snippets", nameof(AppSettings.AllowSnippetEdit), s => s.AllowSnippetEdit),
        ("library.create", "creating shared libraries", nameof(AppSettings.AllowLibraryCreate), s => s.AllowLibraryCreate),
        ("library.join", "joining shared libraries", nameof(AppSettings.AllowLibraryJoin), s => s.AllowLibraryJoin),
        ("integration.edit", "changing integrations", nameof(AppSettings.AllowIntegrationEdit), s => s.AllowIntegrationEdit),
        ("addon.install", "installing, updating and removing add-ons", nameof(AppSettings.AllowAddonInstall), s => s.AllowAddonInstall),
        ("network.scan", "scanning the network (ping sweeps, port scans, nmap, the SSH finder and host import)", nameof(AppSettings.AllowNetworkScan), s => s.AllowNetworkScan),
    };

    /// <summary>Why <paramref name="action"/> isn't allowed, or null when it is.</summary>
    public static string? Why(string action)
    {
        var a = All.FirstOrDefault(x => x.Action == action);
        if (a.Action == null) throw new ArgumentException("unknown restricted action: " + action);
        if (a.Allowed(AppSettings.Current)) return null;
        var by = PolicySettings.Get(a.Setting) is { } p ? $"Group Policy ({p.Scope}: {a.Setting})" : $"this PC's settings ({a.Setting})";
        return $"{Capital(a.What)} is turned off by {by}.";
    }

    public static bool Allowed(string action) => Why(action) == null;

    /// <summary>For a screen: true when allowed; otherwise says why (once, a dialog) and audits the refusal.</summary>
    public static bool Check(string action)
    {
        if (Why(action) is not { } why) return true;
        AuditLog.Write("you", "policy", "refused: " + action, why);
        Ui.Dialogs.Info("Not allowed", why + " Ask your administrator if you need it.");
        return false;
    }

    /// <summary>For the AI, add-ons and the API: throws with the reason (no dialog).</summary>
    public static void Demand(string action, string actor)
    {
        if (Why(action) is not { } why) return;
        AuditLog.Write(actor, "policy", "refused: " + action, why);
        throw new UnauthorizedAccessException(why);
    }

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
