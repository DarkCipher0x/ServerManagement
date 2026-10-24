using System.Net.Http;

namespace DcxManager.Microsoft365;

/// <summary>Microsoft Entra (0.6.6, ADR-084): users, groups, devices, sign-ins (Entra ID P1) and risky users (Entra ID
/// P2), read-only through Microsoft Graph. Users and groups open on their Entra admin centre page.</summary>
public static class EntraAdmin
{
    public const string Scopes = "User.Read.All Group.Read.All Device.Read.All AuditLog.Read.All IdentityRiskyUser.Read.All offline_access";
    public const string Cache = "m365-entra.dcx";

    private static Task<string> Token(CancellationToken ct) => GraphReader.TokenAsync(Scopes, Cache, "Entra", ct);

    public static async Task<AdminTable> UsersAsync(CancellationToken ct, HttpMessageHandler? handler)
    {
        var items = await GraphReader.ListAsync(await Token(ct),
            "https://graph.microsoft.com/v1.0/users?$select=id,displayName,userPrincipalName,accountEnabled,jobTitle,department,userType,createdDateTime&$top=200",
            M365Admin.MaxRows, "Entra", ct, handler);
        var rows = items.Select(u =>
        {
            var id = GraphReader.Id(u);
            return new AdminRow(new[]
            {
                GraphReader.S(u, "displayName"), GraphReader.S(u, "userPrincipalName"), GraphReader.S(u, "accountEnabled") == "no" ? "disabled" : "enabled",
                GraphReader.S(u, "userType"), GraphReader.S(u, "jobTitle"), GraphReader.S(u, "department"), GraphReader.When(u, "createdDateTime"),
            }, id.Length > 0 ? $"https://entra.microsoft.com/#view/Microsoft_AAD_UsersAndTenants/UserProfileMenuBlade/~/overview/userId/{id}" : null);
        }).OrderBy(r => r.Cells[0], StringComparer.OrdinalIgnoreCase).ToList();
        return new AdminTable(new[] { "Name", "Sign-in name", "Account", "Type", "Job title", "Department", "Created" }, rows, items.Count >= M365Admin.MaxRows);
    }

    public static async Task<AdminTable> GroupsAsync(CancellationToken ct, HttpMessageHandler? handler)
    {
        var items = await GraphReader.ListAsync(await Token(ct),
            "https://graph.microsoft.com/v1.0/groups?$select=id,displayName,description,securityEnabled,mailEnabled,groupTypes,membershipRule,createdDateTime&$top=200",
            M365Admin.MaxRows, "Entra", ct, handler);
        var rows = items.Select(g =>
        {
            var id = GraphReader.Id(g);
            bool m365 = g.TryGetProperty("groupTypes", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.Array && t.EnumerateArray().Any(x => x.GetString() == "Unified");
            bool dynamic = g.TryGetProperty("groupTypes", out var t2) && t2.ValueKind == System.Text.Json.JsonValueKind.Array && t2.EnumerateArray().Any(x => x.GetString() == "DynamicMembership");
            string kind = m365 ? "Microsoft 365" : GraphReader.S(g, "securityEnabled") == "yes" ? (GraphReader.S(g, "mailEnabled") == "yes" ? "Mail-enabled security" : "Security") : "Distribution";
            return new AdminRow(new[]
            {
                GraphReader.S(g, "displayName"), kind, dynamic ? "dynamic" : "assigned", GraphReader.S(g, "description"), GraphReader.When(g, "createdDateTime"),
            }, id.Length > 0 ? $"https://entra.microsoft.com/#view/Microsoft_AAD_IAM/GroupDetailsMenuBlade/~/Overview/groupId/{id}" : null);
        }).OrderBy(r => r.Cells[0], StringComparer.OrdinalIgnoreCase).ToList();
        return new AdminTable(new[] { "Group", "Type", "Membership", "Description", "Created" }, rows, items.Count >= M365Admin.MaxRows);
    }

    public static async Task<AdminTable> DevicesAsync(CancellationToken ct, HttpMessageHandler? handler)
    {
        var items = await GraphReader.ListAsync(await Token(ct),
            "https://graph.microsoft.com/v1.0/devices?$select=id,displayName,operatingSystem,operatingSystemVersion,trustType,isCompliant,isManaged,accountEnabled,approximateLastSignInDateTime&$top=200",
            M365Admin.MaxRows, "Entra", ct, handler);
        var rows = items.Select(d => new AdminRow(new[]
        {
            GraphReader.S(d, "displayName"), (GraphReader.S(d, "operatingSystem") + " " + GraphReader.S(d, "operatingSystemVersion")).Trim(),
            GraphReader.S(d, "trustType") switch { "AzureAd" => "Entra joined", "ServerAd" => "Hybrid joined", "Workplace" => "Registered", var x => x },
            GraphReader.S(d, "isCompliant"), GraphReader.S(d, "isManaged"), GraphReader.S(d, "accountEnabled") == "no" ? "disabled" : "enabled",
            GraphReader.When(d, "approximateLastSignInDateTime"),
        }, M365Admin.EntraHome)).OrderBy(r => r.Cells[0], StringComparer.OrdinalIgnoreCase).ToList();
        return new AdminTable(new[] { "Device", "OS", "Join", "Compliant", "Managed", "Account", "Last sign-in" }, rows, items.Count >= M365Admin.MaxRows);
    }

    public static async Task<AdminTable> SignInsAsync(CancellationToken ct, HttpMessageHandler? handler)
    {
        var items = await GraphReader.ListAsync(await Token(ct),
            "https://graph.microsoft.com/v1.0/auditLogs/signIns?$top=100", 500, "Entra sign-ins", ct, handler);
        var rows = items.Select(s =>
        {
            var err = GraphReader.Nested(s, "status", "errorCode");
            var place = string.Join(", ", new[] { GraphReader.Nested(s, "location", "city"), GraphReader.Nested(s, "location", "countryOrRegion") }.Where(x => x.Length > 0));
            return new AdminRow(new[]
            {
                GraphReader.When(s, "createdDateTime"), GraphReader.S(s, "userPrincipalName"), GraphReader.S(s, "appDisplayName"),
                err is "" or "0" ? "success" : "failed (" + err + ")", GraphReader.S(s, "ipAddress"), place, GraphReader.S(s, "conditionalAccessStatus"),
            }, M365Admin.EntraHome);
        }).ToList();
        return new AdminTable(new[] { "When", "User", "Application", "Result", "IP address", "Location", "Conditional Access" }, rows, items.Count >= 500);
    }

    public static async Task<AdminTable> RiskyUsersAsync(CancellationToken ct, HttpMessageHandler? handler)
    {
        var items = await GraphReader.ListAsync(await Token(ct),
            "https://graph.microsoft.com/v1.0/identityProtection/riskyUsers?$top=100", 1000, "Entra risky users", ct, handler);
        var rows = items.Select(u => new AdminRow(new[]
        {
            GraphReader.S(u, "userDisplayName"), GraphReader.S(u, "userPrincipalName"), GraphReader.S(u, "riskLevel"), GraphReader.S(u, "riskState"),
            GraphReader.S(u, "riskDetail"), GraphReader.When(u, "riskLastUpdatedDateTime"),
        }, M365Admin.EntraHome)).OrderBy(r => r.Cells[2] switch { "high" => 0, "medium" => 1, "low" => 2, _ => 3 }).ToList();
        return new AdminTable(new[] { "Name", "Sign-in name", "Risk", "State", "Detail", "Updated" }, rows, items.Count >= 1000);
    }
}
