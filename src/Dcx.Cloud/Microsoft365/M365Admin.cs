using System.Net.Http;
using DcxManager.Services;
using DcxManager.SignIn;

namespace DcxManager.Microsoft365;

/// <summary>One row of an admin list: its cells (in the list's column order), a portal link, and text to filter on.</summary>
public sealed record AdminRow(string[] Cells, string? PortalUrl)
{
    public string Search { get; } = string.Join(" ", Cells).ToLowerInvariant();
}

/// <summary>A loaded admin list.</summary>
public sealed record AdminTable(string[] Columns, List<AdminRow> Rows, bool Truncated);

/// <summary>One list the page offers: where it comes from, what it needs, how it loads.</summary>
public sealed record AdminList(string Service, string Title, string Needs, string Portal, Func<CancellationToken, HttpMessageHandler?, Task<AdminTable>> Load);

/// <summary>
/// Microsoft 365 admin (0.6.6, ADR-084; DarkCipher0x: "Integration into Microsofts Defender portal/intune etc ... for the admin
/// management ... same with entra"). Read-only in this release: Intune devices (<see cref="IntuneAdmin"/>), Defender
/// incidents and alerts (<see cref="DefenderAdmin"/>), Entra users, groups, devices, sign-ins and risky users
/// (<see cref="EntraAdmin"/>), each with "Open in portal" for the real admin centre. Signed in as you (an administrator)
/// through Microsoft Graph with an app registration in your tenant (Settings → Integrations → Microsoft 365 admin, admin
/// approval); one sign-in per service, asked for the first time you load one of its lists.
/// </summary>
public static class M365Admin
{
    public const int MaxRows = 2000;
    public const string IntuneHome = "https://intune.microsoft.com/";
    public const string DefenderHome = "https://security.microsoft.com/";
    public const string EntraHome = "https://entra.microsoft.com/";

    public static readonly string[] Caches = { IntuneAdmin.Cache, DefenderAdmin.Cache, EntraAdmin.Cache };

    /// <summary>Every list, in page order.</summary>
    public static IReadOnlyList<AdminList> Lists { get; } = new[]
    {
        new AdminList("Intune", "Devices", "DeviceManagementManagedDevices.Read.All", IntuneHome, IntuneAdmin.DevicesAsync),
        new AdminList("Defender", "Incidents", "SecurityIncident.Read.All", DefenderHome, DefenderAdmin.IncidentsAsync),
        new AdminList("Defender", "Alerts", "SecurityAlert.Read.All", DefenderHome, DefenderAdmin.AlertsAsync),
        new AdminList("Entra", "Users", "User.Read.All", EntraHome, EntraAdmin.UsersAsync),
        new AdminList("Entra", "Groups", "Group.Read.All", EntraHome, EntraAdmin.GroupsAsync),
        new AdminList("Entra", "Devices", "Device.Read.All", EntraHome, EntraAdmin.DevicesAsync),
        new AdminList("Entra", "Sign-ins", "AuditLog.Read.All (Entra ID P1)", EntraHome, EntraAdmin.SignInsAsync),
        new AdminList("Entra", "Risky users", "IdentityRiskyUser.Read.All (Entra ID P2)", EntraHome, EntraAdmin.RiskyUsersAsync),
    };

    /// <summary>The delegated Graph permissions the app registration needs (all read-only).</summary>
    public static readonly string[] Permissions =
    {
        "DeviceManagementManagedDevices.Read.All", "SecurityIncident.Read.All", "SecurityAlert.Read.All",
        "User.Read.All", "Group.Read.All", "Device.Read.All", "AuditLog.Read.All", "IdentityRiskyUser.Read.All",
    };

    public static bool SignedIn => Caches.Any(EntraDelegated.SignedIn);
    public static string Account => Caches.Select(EntraDelegated.Account).FirstOrDefault(a => a.Length > 0) ?? "";
    public static void SignOut() { foreach (var c in Caches) EntraDelegated.SignOut(c); }

    /// <summary>A portal link from Graph is used only on Microsoft's admin centre of that service (https).</summary>
    public static string? PortalLink(string? url, string host) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && u.IdnHost.Equals(host, StringComparison.OrdinalIgnoreCase) ? u.AbsoluteUri : null;
}
