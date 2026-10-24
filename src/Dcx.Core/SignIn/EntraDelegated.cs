using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DcxManager.Services;

namespace DcxManager.SignIn;

/// <summary>
/// A delegated Microsoft Graph token for something the user does (0.5.0: sharing to Microsoft Teams; 0.6.4: listing your Cloud PCs). The same browser
/// round trip as the Entra sign-in (authorization code + PKCE, loopback redirect on 127.0.0.1, a public client — no
/// secret), asking only for the scopes given. The refresh token is kept in the encrypted store (its own file) so the next
/// share doesn't open the browser; access tokens live in memory only. "Sign out" deletes the file.
/// </summary>
public static class EntraDelegated
{
    private sealed class Cache { public string Refresh { get; set; } = ""; public string Scopes { get; set; } = ""; public string Account { get; set; } = ""; }
    private static readonly Dictionary<string, (string Token, DateTime Expires)> Access = new();
    private static readonly Regex TenantRx = new(@"^(?:[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}|[A-Za-z0-9](?:[A-Za-z0-9.-]{0,252})|organizations)$");
    private static readonly Regex ClientRx = new(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$");

    public static string? Problem(string tenant, string clientId) =>
        !TenantRx.IsMatch(tenant) ? "the tenant must be its id (GUID), a verified domain or “organizations”" : !ClientRx.IsMatch(clientId) ? "the client id must be the app registration's GUID" : null;

    public static bool SignedIn(string cacheName) => SecureStore.IsUnlocked && File.Exists(Path.Combine(SecureStore.DataDir, cacheName));
    public static string Account(string cacheName) => SignedIn(cacheName) ? SecureStore.Read<Cache>(cacheName).Account : "";

    public static void SignOut(string cacheName)
    {
        lock (Access) Access.Remove(cacheName);
        try { File.Delete(Path.Combine(SecureStore.DataDir, cacheName)); } catch (IOException) { }
    }

    /// <summary>An access token for <paramref name="scopes"/> (space-separated, e.g. "ChannelMessage.Send offline_access"):
    /// from memory, else from the stored refresh token, else through the browser.</summary>
    public static async Task<string> TokenAsync(string tenant, string clientId, string scopes, string cacheName, CancellationToken ct, string feature = "Microsoft Teams", string settingsPath = "Settings → Integrations → Microsoft Teams")
    {
        if (Problem(tenant, clientId) is { } why) throw new InvalidOperationException(feature + ": " + why + " (" + settingsPath + ").");
        lock (Access) if (Access.TryGetValue(cacheName, out var a) && a.Expires > DateTime.UtcNow.AddMinutes(2)) return a.Token;
        var tokenEp = new Uri($"https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token");
        Offline.EnforceStatic(tokenEp.IdnHost, feature + " sign-in");
        await Offline.EnforceAsync(tokenEp.IdnHost, feature + " sign-in", ct);
        using var http = new HttpClient(EntraSignIn.Handler(), disposeHandler: true) { Timeout = TimeSpan.FromSeconds(30) };
        if (SignedIn(cacheName) && SecureStore.Read<Cache>(cacheName) is { Refresh.Length: > 0 } c && c.Scopes == scopes)
        {
            var refreshed = await ExchangeAsync(http, tokenEp, new() { ["client_id"] = clientId, ["grant_type"] = "refresh_token", ["refresh_token"] = c.Refresh, ["scope"] = scopes }, cacheName, scopes, c.Account, ct);
            if (refreshed != null) return refreshed;
        }
        // The browser: PKCE + state, loopback redirect.
        var verifier = Jwt.B64Url(RandomNumberGenerator.GetBytes(48));
        var challenge = Jwt.B64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Jwt.B64Url(RandomNumberGenerator.GetBytes(24));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var redirect = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/signin";
        string code;
        try
        {
            var q = new Dictionary<string, string> { ["client_id"] = clientId, ["response_type"] = "code", ["redirect_uri"] = redirect, ["response_mode"] = "query", ["scope"] = scopes, ["state"] = state, ["code_challenge"] = challenge, ["code_challenge_method"] = "S256", ["prompt"] = "select_account" };
            var url = new Uri($"https://login.microsoftonline.com/{tenant}/oauth2/v2.0/authorize?" + string.Join("&", q.Select(kv => kv.Key + "=" + Uri.EscapeDataString(kv.Value))));
            var wait = EntraSignIn.AcceptAsync(listener, state, ct);
            EntraSignIn.OpenBrowser(url);
            using var t = CancellationTokenSource.CreateLinkedTokenSource(ct);
            t.CancelAfter(EntraSignIn.BrowserTimeout);
            code = await wait.WaitAsync(t.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("No sign-in arrived from the browser in time."); }
        finally { listener.Stop(); }
        return await ExchangeAsync(http, tokenEp, new() { ["client_id"] = clientId, ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = redirect, ["code_verifier"] = verifier, ["scope"] = scopes }, cacheName, scopes, "", ct)
            ?? throw new InvalidOperationException("Microsoft didn't issue a token for " + feature + ".");
    }

    private static async Task<string?> ExchangeAsync(HttpClient http, Uri tokenEp, Dictionary<string, string> form, string cacheName, string scopes, string account, CancellationToken ct)
    {
        using var body = new FormUrlEncodedContent(form);
        using var r = await http.PostAsync(tokenEp, body, ct);
        var text = await r.Content.ReadAsStringAsync(ct);
        if (!r.IsSuccessStatusCode) { if (form["grant_type"] == "refresh_token") return null; throw new InvalidOperationException("The token exchange failed: " + Redaction.Mask(text.Length > 300 ? text[..300] : text)); }
        using var d = JsonDocument.Parse(text);
        var at = d.RootElement.TryGetProperty("access_token", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
        if (string.IsNullOrEmpty(at)) return null;
        int secs = d.RootElement.TryGetProperty("expires_in", out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var s) ? s : 3600;
        lock (Access) Access[cacheName] = (at, DateTime.UtcNow.AddSeconds(Math.Clamp(secs, 60, 86400)));
        if (d.RootElement.TryGetProperty("refresh_token", out var rt) && rt.GetString() is { Length: > 0 } refresh)
        {
            if (account.Length == 0 && d.RootElement.TryGetProperty("id_token", out var idt) && idt.GetString() is { } jwt) account = PreferredName(jwt);
            SecureStore.Write(cacheName, new Cache { Refresh = refresh, Scopes = scopes, Account = account });
        }
        return at;
    }

    /// <summary>The signed-in name from an id_token's payload (display only; the token itself isn't trusted for anything).</summary>
    private static string PreferredName(string jwt)
    {
        try
        {
            var p = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
            p = p.PadRight(p.Length + (4 - p.Length % 4) % 4, '=');
            using var d = JsonDocument.Parse(Convert.FromBase64String(p));
            return d.RootElement.TryGetProperty("preferred_username", out var u) ? u.GetString() ?? "" : "";
        }
        catch (Exception ex) when (ex is FormatException or JsonException or IndexOutOfRangeException) { return ""; }
    }
}
