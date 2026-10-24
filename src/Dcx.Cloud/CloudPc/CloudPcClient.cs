using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using DcxManager.Services;
using DcxManager.SignIn;

namespace DcxManager.CloudPc;

/// <summary>What can be done to a Cloud PC (0.6.5): restart and rename yours; troubleshoot and power a Frontline Cloud
/// PC on / off as an administrator.</summary>
public enum CloudPcAction { Restart, Rename, Troubleshoot, PowerOn, PowerOff }

/// <summary>One Cloud PC assigned to you (Microsoft Graph <c>cloudPC</c>; only what is shown is kept).</summary>
public sealed record CloudPcInfo(string Id, string Name, string Image, string Plan, string Provisioning);

/// <summary>
/// Cloud PCs (0.6.4, ADR-083; DarkCipher0x: "integrate the Windows App into ServerManagement... so i could see my Cloud PCs and
/// connect"). Microsoft's Windows App can't be embedded, so this is what can be done around it:
/// <list type="bullet">
/// <item>List your Cloud PCs: Microsoft Graph <c>GET /v1.0/me/cloudPCs</c>, delegated <c>CloudPC.Read.All</c>, signed
/// in as you through the browser (<see cref="EntraDelegated"/>: PKCE, loopback, the refresh token in the encrypted store).
/// Needs an app registration in your tenant (Settings → Integrations → Cloud PCs, admin approval).</item>
/// <item>Connect: Microsoft's documented direct launch link of the Windows App web client
/// (<c>https://windows.cloud.microsoft/webclient/ent/&lt;id&gt;</c>), in a tab of the app's browser, or the installed
/// Windows App (found in the Start menu's app list, never a guessed name).</item>
/// </list>
/// Requests carry only the token; nothing is sent until you click. Addresses are built here from checked GUIDs.
/// </summary>
public static class CloudPcClient
{
    public const string TokenCache = "cloudpc-graph.dcx";
    public const string Scopes = "CloudPC.Read.All offline_access";
    public const string WebClientHome = "https://windows.cloud.microsoft/";
    private const string ListUrl = "https://graph.microsoft.com/v1.0/me/cloudPCs";

    /// <summary>Self-test: a token without the browser, and a fake Graph.</summary>
    internal static Func<Task<string>>? TokenForTest { get; set; }

    /// <summary>A tenant and an app registration are set, so the list can be fetched.</summary>
    public static bool Configured => EntraDelegated.Problem(AppSettings.Current.CloudPcTenant, AppSettings.Current.CloudPcClientId) == null;
    public static bool SignedIn => EntraDelegated.SignedIn(TokenCache);
    public static string Account => EntraDelegated.Account(TokenCache);
    public static void SignOut() => EntraDelegated.SignOut(TokenCache);

    /// <summary>Your Cloud PCs (every page of the answer, at most 500).</summary>
    public static async Task<List<CloudPcInfo>> ListAsync(CancellationToken ct, HttpMessageHandler? handler = null)
    {
        var token = await (TokenForTest?.Invoke() ?? EntraDelegated.TokenAsync(AppSettings.Current.CloudPcTenant, AppSettings.Current.CloudPcClientId, Scopes, TokenCache, ct,
            feature: "Cloud PCs", settingsPath: "Settings → Integrations → Cloud PCs"));
        var graph = new Uri(ListUrl);
        Offline.EnforceStatic(graph.IdnHost, "Cloud PCs");
        await Offline.EnforceAsync(graph.IdnHost, "Cloud PCs", ct);
        using var http = new HttpClient(handler ?? Offline.Handler(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false }), disposeHandler: handler == null) { Timeout = TimeSpan.FromSeconds(30) };
        var list = new List<CloudPcInfo>();
        Uri? next = new(ListUrl + "?$select=id,displayName,imageDisplayName,servicePlanName,provisioningType");
        for (int page = 0; next != null && page < 10 && list.Count < 500; page++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, next);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var r = await http.SendAsync(req, ct);
            var body = await r.Content.ReadAsStringAsync(ct);
            if ((int)r.StatusCode is 401 or 403) throw new InvalidOperationException("Microsoft Graph refused the request: the app registration needs the delegated permission CloudPC.Read.All with admin consent, and you need a Windows 365 licence (HTTP " + (int)r.StatusCode + ").");
            if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"Microsoft Graph answered HTTP {(int)r.StatusCode}.");
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
                foreach (var pc in value.EnumerateArray())
                {
                    string S(string n) => pc.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                    if (!Guid.TryParse(S("id"), out _)) continue;   // only ids that can go into a link
                    list.Add(new CloudPcInfo(S("id"), Trim(S("displayName"), 120), Trim(S("imageDisplayName"), 120), Trim(S("servicePlanName"), 120), Trim(S("provisioningType"), 40)));
                }
            // The next page only on Graph itself, https.
            next = doc.RootElement.TryGetProperty("@odata.nextLink", out var nl) && Uri.TryCreate(nl.GetString(), UriKind.Absolute, out var n)
                && n.Scheme == Uri.UriSchemeHttps && n.IdnHost == graph.IdnHost ? n : null;
        }
        return list.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    // ---- actions (0.6.5) -------------------------------------------------------------------------------------------

    /// <summary>The write permission lives in its own sign-in, so the read-only list never holds it.</summary>
    public const string ActionScopes = "CloudPC.ReadWrite.All offline_access";
    public const string ActionTokenCache = "cloudpc-graph-actions.dcx";
    /// <summary>Restart / rename (yours) and troubleshoot / power on / power off (administrators) are switched on
    /// (Settings → Integrations → Cloud PCs, admin approval; Group Policy CloudPcAllowActions).</summary>
    public static bool ActionsAllowed => AppSettings.Current.CloudPcAllowActions;
    public static bool ActionsSignedIn => EntraDelegated.SignedIn(ActionTokenCache);
    public static void SignOutActions() => EntraDelegated.SignOut(ActionTokenCache);

    /// <summary>The Graph address of an action, from a checked GUID (Microsoft's documented paths; restart and rename of
    /// your own Cloud PC are Graph beta, the administrators' actions are on deviceManagement).</summary>
    public static Uri ActionUri(CloudPcAction a, string cloudPcId)
    {
        if (!Guid.TryParse(cloudPcId, out var g)) throw new ArgumentException("not a Cloud PC id");
        var id = g.ToString("D");
        return new Uri(a switch
        {
            CloudPcAction.Restart => $"https://graph.microsoft.com/beta/me/cloudPCs/{id}/reboot",
            CloudPcAction.Rename => $"https://graph.microsoft.com/beta/me/cloudPCs/{id}/rename",
            CloudPcAction.Troubleshoot => $"https://graph.microsoft.com/v1.0/deviceManagement/virtualEndpoint/cloudPCs/{id}/troubleshoot",
            CloudPcAction.PowerOn => $"https://graph.microsoft.com/beta/deviceManagement/virtualEndpoint/cloudPCs/{id}/poweron",
            CloudPcAction.PowerOff => $"https://graph.microsoft.com/beta/deviceManagement/virtualEndpoint/cloudPCs/{id}/poweroff",
            _ => throw new ArgumentOutOfRangeException(nameof(a)),
        });
    }

    /// <summary>A new display name: 1–64 printable characters.</summary>
    public static string? NameProblem(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "a name is needed" : name.Trim().Length > 64 ? "at most 64 characters" : name.Any(char.IsControl) ? "no control characters" : null;

    /// <summary>Runs an action on one Cloud PC (Graph answers 204 No Content). Throws with a readable reason otherwise.</summary>
    public static async Task RunAsync(CloudPcAction action, string cloudPcId, CancellationToken ct, string? newName = null, HttpMessageHandler? handler = null)
    {
        if (!ActionsAllowed) throw new InvalidOperationException("Cloud PC actions are off (Settings → Integrations → Cloud PCs; turning them on needs admin approval).");
        var uri = ActionUri(action, cloudPcId);
        if (action == CloudPcAction.Rename && NameProblem(newName) is { } why) throw new ArgumentException("New name: " + why + ".");
        var token = await (TokenForTest?.Invoke() ?? EntraDelegated.TokenAsync(AppSettings.Current.CloudPcTenant, AppSettings.Current.CloudPcClientId, ActionScopes, ActionTokenCache, ct,
            feature: "Cloud PC actions", settingsPath: "Settings → Integrations → Cloud PCs"));
        Offline.EnforceStatic(uri.IdnHost, "Cloud PC actions");
        await Offline.EnforceAsync(uri.IdnHost, "Cloud PC actions", ct);
        using var http = new HttpClient(handler ?? Offline.Handler(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false }), disposeHandler: handler == null) { Timeout = TimeSpan.FromSeconds(30) };
        using var req = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = action == CloudPcAction.Rename
                ? new StringContent(JsonSerializer.Serialize(new { displayName = newName!.Trim() }), System.Text.Encoding.UTF8, "application/json")
                : new ByteArrayContent(Array.Empty<byte>()),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var r = await http.SendAsync(req, ct);
        int code = (int)r.StatusCode;
        if (code is 200 or 202 or 204) return;
        bool admin = action is CloudPcAction.Troubleshoot or CloudPcAction.PowerOn or CloudPcAction.PowerOff;
        throw new InvalidOperationException(code switch
        {
            401 or 403 => admin
                ? $"Microsoft Graph refused it (HTTP {code}): this needs a Windows 365 / Intune administrator role and CloudPC.ReadWrite.All with admin consent."
                : $"Microsoft Graph refused it (HTTP {code}): the app registration needs CloudPC.ReadWrite.All with admin consent.",
            404 => "Microsoft Graph doesn't know this Cloud PC (HTTP 404).",
            400 when action is CloudPcAction.PowerOn or CloudPcAction.PowerOff => "Microsoft Graph refused it (HTTP 400): power on / off is only for Windows 365 Frontline Cloud PCs.",
            _ => $"Microsoft Graph answered HTTP {code}.",
        });
    }

    /// <summary>Microsoft's direct launch link for a Windows 365 Cloud PC in the Windows App web client, with your sign-in
    /// name as the login hint (no account picker when already signed in). Null for an id that isn't a GUID.</summary>
    public static string? WebClientLink(string cloudPcId, string? loginHint = null)
    {
        if (!Guid.TryParse(cloudPcId, out var id)) return null;
        var url = $"https://windows.cloud.microsoft/webclient/ent/{id:D}";
        return loginHint is { Length: > 3 } h && h.Contains('@') && !h.Any(char.IsWhiteSpace) ? url + "#loginHint=" + Uri.EscapeDataString(h) : url;
    }

    // ---- the installed Windows App -------------------------------------------------------------------------------

    /// <summary>The Windows App's id in the Start menu's app list (shell:AppsFolder), or null when it isn't installed.
    /// Found by its package (MicrosoftCorporationII.Windows365…) or its name, never guessed.</summary>
    public static string? WindowsAppId()
    {
        try
        {
            var t = Type.GetTypeFromProgID("Shell.Application");
            if (t == null) return null;
            dynamic shell = Activator.CreateInstance(t)!;
            dynamic folder = shell.NameSpace("shell:AppsFolder");
            foreach (dynamic item in folder.Items())
            {
                string path = item.Path ?? "", name = item.Name ?? "";
                if (path.StartsWith("MicrosoftCorporationII.Windows365_", StringComparison.OrdinalIgnoreCase) || name.Equals("Windows App", StringComparison.OrdinalIgnoreCase) && path.Contains('!'))
                    return path;
            }
        }
        catch (Exception ex) { Log.Write("Cloud PCs: find the Windows App", ex); }
        return null;
    }

    /// <summary>Starts the installed Windows App. False when it isn't installed (offer the Store).</summary>
    public static bool LaunchWindowsApp()
    {
        if (WindowsAppId() is not { } id || id.Any(c => c is '"' or '\\' or '/' or ' ')) return false;
        Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), "shell:AppsFolder\\" + id) { UseShellExecute = false });
        return true;
    }

    /// <summary>The Microsoft Store, searching for the Windows App.</summary>
    public static void OpenStore() => Process.Start(new ProcessStartInfo("ms-windows-store://search/?query=Windows%20App") { UseShellExecute = true });
}
