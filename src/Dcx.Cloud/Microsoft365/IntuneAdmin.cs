using System.Net.Http;

namespace DcxManager.Microsoft365;

/// <summary>Intune (0.6.6, ADR-084): managed devices, read-only (Graph <c>GET /deviceManagement/managedDevices</c>,
/// delegated DeviceManagementManagedDevices.Read.All). Each opens on its Intune admin centre page.</summary>
public static class IntuneAdmin
{
    public const string Scopes = "DeviceManagementManagedDevices.Read.All offline_access";
    public const string Cache = "m365-intune.dcx";

    public static async Task<AdminTable> DevicesAsync(CancellationToken ct, HttpMessageHandler? handler)
    {
        var token = await GraphReader.TokenAsync(Scopes, Cache, "Intune", ct);
        var items = await GraphReader.ListAsync(token,
            "https://graph.microsoft.com/v1.0/deviceManagement/managedDevices?$select=id,deviceName,operatingSystem,osVersion,complianceState,lastSyncDateTime,userPrincipalName,model,manufacturer,isEncrypted,managementAgent&$top=200",
            M365Admin.MaxRows, "Intune", ct, handler);
        var rows = items.Select(d =>
        {
            var id = GraphReader.Id(d);
            return new AdminRow(new[]
            {
                GraphReader.S(d, "deviceName"), GraphReader.S(d, "complianceState"),
                (GraphReader.S(d, "operatingSystem") + " " + GraphReader.S(d, "osVersion")).Trim(),
                GraphReader.S(d, "userPrincipalName"), GraphReader.When(d, "lastSyncDateTime"),
                (GraphReader.S(d, "manufacturer") + " " + GraphReader.S(d, "model")).Trim(), GraphReader.S(d, "isEncrypted"),
            }, id.Length > 0 ? $"https://intune.microsoft.com/#view/Microsoft_Intune_Devices/DeviceSettingsMenuBlade/~/overview/mdmDeviceId/{id}" : null);
        }).OrderBy(r => r.Cells[0], StringComparer.OrdinalIgnoreCase).ToList();
        return new AdminTable(new[] { "Device", "Compliance", "OS", "User", "Last check-in", "Model", "Encrypted" }, rows, items.Count >= M365Admin.MaxRows);
    }
}
