using System.Net.Http;

namespace DcxManager.Microsoft365;

/// <summary>Microsoft Defender (0.6.6, ADR-084): incidents and alerts, read-only (Graph security API:
/// <c>/security/incidents</c> with SecurityIncident.Read.All, <c>/security/alerts_v2</c> with SecurityAlert.Read.All),
/// newest first. Each opens on its page in the Defender portal (the link Graph gives, only on security.microsoft.com).</summary>
public static class DefenderAdmin
{
    public const string Scopes = "SecurityIncident.Read.All SecurityAlert.Read.All offline_access";
    public const string Cache = "m365-defender.dcx";
    private const string Portal = "security.microsoft.com";

    public static async Task<AdminTable> IncidentsAsync(CancellationToken ct, HttpMessageHandler? handler)
    {
        var token = await GraphReader.TokenAsync(Scopes, Cache, "Defender", ct);
        var items = await GraphReader.ListAsync(token,
            "https://graph.microsoft.com/v1.0/security/incidents?$orderby=createdDateTime desc&$top=50", 500, "Defender", ct, handler);
        var rows = items.Select(i => new AdminRow(new[]
        {
            GraphReader.S(i, "displayName"), GraphReader.S(i, "severity"), GraphReader.S(i, "status"), GraphReader.S(i, "classification"),
            GraphReader.S(i, "assignedTo"), GraphReader.When(i, "createdDateTime"), GraphReader.When(i, "lastUpdateDateTime"),
        }, M365Admin.PortalLink(GraphReader.S(i, "incidentWebUrl", 2000), Portal))).ToList();
        return new AdminTable(new[] { "Incident", "Severity", "Status", "Classification", "Assigned to", "Created", "Updated" }, rows, items.Count >= 500);
    }

    public static async Task<AdminTable> AlertsAsync(CancellationToken ct, HttpMessageHandler? handler)
    {
        var token = await GraphReader.TokenAsync(Scopes, Cache, "Defender", ct);
        var items = await GraphReader.ListAsync(token,
            "https://graph.microsoft.com/v1.0/security/alerts_v2?$orderby=createdDateTime desc&$top=100", 1000, "Defender", ct, handler);
        var rows = items.Select(a => new AdminRow(new[]
        {
            GraphReader.S(a, "title"), GraphReader.S(a, "severity"), GraphReader.S(a, "status"), GraphReader.S(a, "serviceSource"),
            GraphReader.S(a, "category"), GraphReader.When(a, "createdDateTime"),
        }, M365Admin.PortalLink(GraphReader.S(a, "alertWebUrl", 2000), Portal))).ToList();
        return new AdminTable(new[] { "Alert", "Severity", "Status", "Source", "Category", "Created" }, rows, items.Count >= 1000);
    }
}
