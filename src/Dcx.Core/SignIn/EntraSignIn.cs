using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DcxManager.Services;

namespace DcxManager.SignIn;

/// <summary>
/// Microsoft Entra ID sign-in (0.3.0 §23): OpenID Connect authorization code + PKCE (RFC 7636) through the system browser
/// — so MFA, Conditional Access and the user's existing session all apply — with the redirect to a loopback listener on
/// 127.0.0.1 (RFC 8252, a public client: no secret). The id_token is verified against the tenant's published keys (RS256,
/// the key named by its kid), and issuer / audience / expiry / nonce are checked. Only the claims are used (oid, upn, name,
/// groups); the tokens are discarded — nothing is stored. The authority is https only and everything goes through
/// offline mode. The self-test replaces the browser and the HTTP handler with a fake identity provider on 127.0.0.1.
/// </summary>
public sealed class EntraSignIn : IDirectorySignIn
{
    public string Kind => "entra";

    /// <summary>Opens the sign-in page: the system browser (replaced by the self-test).</summary>
    public static Action<Uri> OpenBrowser { get; set; } = u =>
    {
        if (u.Scheme != "https") throw new InvalidOperationException("the sign-in address isn't https");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(u.ToString()) { UseShellExecute = true });
    };
    /// <summary>The HTTP handler for discovery, keys and the token exchange (the self-test's fake server).</summary>
    public static Func<HttpMessageHandler> Handler { get; set; } = () => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(15) };
    public static TimeSpan BrowserTimeout { get; set; } = TimeSpan.FromMinutes(5);

    public string AuthorityFor(DirectoryBinding b) => b.Authority.Length > 0 ? b.Authority.TrimEnd('/') : $"https://login.microsoftonline.com/{b.Server}/v2.0";

    public async Task<SignInResult> SignInAsync(DirectoryBinding b, Func<string, string?> ask, CancellationToken ct)
    {
        var authority = new Uri(AuthorityFor(b));
        if (authority.Scheme != "https") return SignInResult.Fail("the authority must be https");
        Offline.EnforceStatic(authority.IdnHost, "a Microsoft Entra sign-in");
        await Offline.EnforceAsync(authority.IdnHost, "a Microsoft Entra sign-in", ct);
        using var http = new HttpClient(Handler(), disposeHandler: true) { Timeout = TimeSpan.FromSeconds(30) };

        // 1. discovery + keys
        JsonElement disco, jwks;
        try
        {
            disco = await GetJsonAsync(http, new Uri(authority + "/.well-known/openid-configuration"), ct);
            jwks = await GetJsonAsync(http, new Uri(disco.GetProperty("jwks_uri").GetString()!), ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException) { return SignInResult.Fail("the identity provider can't be reached: " + ex.Message); }
        var authorize = new Uri(disco.GetProperty("authorization_endpoint").GetString()!);
        var tokenEp = new Uri(disco.GetProperty("token_endpoint").GetString()!);
        var issuer = disco.GetProperty("issuer").GetString() ?? "";
        if (authorize.Scheme != "https" || tokenEp.Scheme != "https") return SignInResult.Fail("the identity provider's endpoints aren't https");

        // 2. the browser round trip: loopback listener, PKCE, state and nonce
        var verifier = Jwt.B64Url(RandomNumberGenerator.GetBytes(48));
        var challenge = Jwt.B64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Jwt.B64Url(RandomNumberGenerator.GetBytes(24));
        var nonce = Jwt.B64Url(RandomNumberGenerator.GetBytes(24));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var redirect = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/signin";
        string code;
        try
        {
            var q = new Dictionary<string, string>
            {
                ["client_id"] = b.ClientId, ["response_type"] = "code", ["redirect_uri"] = redirect, ["response_mode"] = "query",
                ["scope"] = "openid profile email", ["state"] = state, ["nonce"] = nonce, ["code_challenge"] = challenge, ["code_challenge_method"] = "S256",
                ["login_hint"] = b.User, ["prompt"] = "select_account",
            };
            var url = new Uri(authorize + (authorize.Query.Length > 0 ? "&" : "?") + string.Join("&", q.Select(kv => kv.Key + "=" + Uri.EscapeDataString(kv.Value))));
            var wait = AcceptAsync(listener, state, ct);
            OpenBrowser(url);
            using var t = CancellationTokenSource.CreateLinkedTokenSource(ct);
            t.CancelAfter(BrowserTimeout);
            code = await wait.WaitAsync(t.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return SignInResult.Fail("no sign-in arrived from the browser in time"); }
        catch (InvalidDataException ex) { return SignInResult.Fail(ex.Message); }
        finally { listener.Stop(); }

        // 3. code → tokens (PKCE verifier proves it's the same client), then the id_token is verified locally
        JsonElement tokens;
        try
        {
            using var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["client_id"] = b.ClientId, ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = redirect, ["code_verifier"] = verifier, ["scope"] = "openid profile email" });
            using var r = await http.PostAsync(tokenEp, form, ct);
            var body = await r.Content.ReadAsStringAsync(ct);
            if (!r.IsSuccessStatusCode) return SignInResult.Fail("the token exchange failed: " + Redaction.Mask(body.Length > 300 ? body[..300] : body));
            tokens = JsonDocument.Parse(body).RootElement.Clone();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { return SignInResult.Fail("the token exchange failed: " + ex.Message); }
        if (!tokens.TryGetProperty("id_token", out var idt) || idt.GetString() is not { Length: > 0 } idToken) return SignInResult.Fail("no id_token in the response");
        JsonElement claims;
        try { claims = Jwt.Verify(idToken, jwks); }
        catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException or KeyNotFoundException) { return SignInResult.Fail("the id_token didn't verify: " + ex.Message); }
        if (Jwt.CheckClaims(claims, issuer, b.ClientId, nonce, DateTimeOffset.UtcNow) is { } bad) return SignInResult.Fail("the id_token was refused: " + bad);
        if (claims.TryGetProperty("tid", out var tid) && Guid.TryParse(b.Server, out var tenant) && tid.GetString() != tenant.ToString()) return SignInResult.Fail("the token is from another tenant");

        var upn = Str(claims, "preferred_username") ?? Str(claims, "upn") ?? Str(claims, "email") ?? "";
        var oid = Str(claims, "oid") ?? Str(claims, "sub") ?? "";
        if (!upn.Equals(b.User, StringComparison.OrdinalIgnoreCase)) return SignInResult.Fail($"{upn} signed in, but this profile belongs to {b.User}");
        var groups = new List<string>();
        if (claims.TryGetProperty("groups", out var g) && g.ValueKind == JsonValueKind.Array) foreach (var x in g.EnumerateArray()) if (x.GetString() is { } s) groups.Add(s);
        if (claims.TryGetProperty("roles", out var roles) && roles.ValueKind == JsonValueKind.Array) foreach (var x in roles.EnumerateArray()) if (x.GetString() is { } s) groups.Add("role:" + s);
        return new SignInResult(true, oid, Str(claims, "name") ?? upn, groups, null);
    }

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal static async Task<JsonElement> GetJsonAsync(HttpClient http, Uri u, CancellationToken ct)
    {
        if (u.Scheme != "https") throw new HttpRequestException("not https: " + u.Host);
        Offline.EnforceStatic(u.IdnHost, "a Microsoft Entra sign-in");
        using var r = await http.GetAsync(u, ct);
        r.EnsureSuccessStatusCode();
        var bytes = await r.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length > 256 * 1024) throw new HttpRequestException("oversized document");
        return JsonDocument.Parse(bytes).RootElement.Clone();
    }

    /// <summary>One HTTP request on the loopback listener: the redirect with ?code=&amp;state=. Anything else is answered
    /// and ignored (a stray browser probe). The state must match or the code is thrown away.</summary>
    internal static async Task<string> AcceptAsync(TcpListener listener, string state, CancellationToken ct)
    {
        for (int i = 0; i < 8; i++)
        {
            using var c = await listener.AcceptTcpClientAsync(ct);
            var s = c.GetStream();
            var buf = new byte[8192]; int n = 0, got;
            while (n < buf.Length && (got = await s.ReadAsync(buf.AsMemory(n), ct)) > 0) { n += got; if (Encoding.ASCII.GetString(buf, 0, n).Contains("\r\n\r\n")) break; }
            var req = Encoding.ASCII.GetString(buf, 0, n);
            var line = req.Split("\r\n")[0];
            var parts = line.Split(' ');
            var query = parts.Length >= 2 && parts[1].Contains('?') ? parts[1][(parts[1].IndexOf('?') + 1)..] : "";
            var kv = query.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
            bool ok = kv.TryGetValue("code", out var code) && kv.TryGetValue("state", out var st) && st == state;
            var html = ok ? "<html><body style='font-family:sans-serif'><h2>Signed in</h2><p>You can close this tab and return to DarkCipher ServerManagement.</p></body></html>"
                          : "<html><body style='font-family:sans-serif'><h2>Sign-in not accepted</h2><p>" + (kv.TryGetValue("error_description", out var ed) ? WebUtility.HtmlEncode(ed) : "Try again from the app.") + "</p></body></html>";
            var resp = Encoding.UTF8.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {Encoding.UTF8.GetByteCount(html)}\r\nConnection: close\r\n\r\n{html}");
            await s.WriteAsync(resp, ct);
            if (ok) return code!;
            if (kv.ContainsKey("error")) throw new InvalidDataException("the identity provider reported: " + (kv.GetValueOrDefault("error_description") ?? kv["error"]));
        }
        throw new InvalidDataException("no valid sign-in redirect arrived");
    }
}
