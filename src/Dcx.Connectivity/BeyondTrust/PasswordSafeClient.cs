using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DcxManager.Services;

namespace DcxManager.BeyondTrust;

/// <summary>A managed account Password Safe lets this user request.</summary>
public sealed record ManagedAccount(long SystemId, long AccountId, string SystemName, string AccountName, string DomainName);

/// <summary>
/// BeyondTrust Password Safe / BeyondInsight (0.5.0, plan #16): check a managed account's password out for one session and
/// back in. The public API (…/BeyondTrust/api/public/v3): POST Auth/SignAppIn with the PS-Auth header (API registration
/// key and run-as user, kept in the encrypted settings), GET ManagedAccounts, POST Requests (system, account, duration,
/// reason) → GET Credentials/{request} → PUT Requests/{request}/Checkin, POST Auth/Signout. The password is held in
/// memory only for the session it opens (never stored, never logged); every checkout is checked in when you're done, when
/// the screen closes or when the app exits. HTTPS to the configured host only; offline mode applies.
/// </summary>
public sealed class PasswordSafeClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _base;
    private readonly string _auth;
    private bool _signedIn;

    public PasswordSafeClient(string host, string apiKey, string runAs, string? runAsPassword, HttpMessageHandler? handler = null)
    {
        if (SiteProblem(host) is { } why) throw new ArgumentException(why);
        if (apiKey.Any(c => c is ';' or '\r' or '\n') || runAs.Any(c => c is ';' or '[' or ']' or '\r' or '\n') || (runAsPassword ?? "").Any(c => c is ']' or '\r' or '\n')) throw new ArgumentException("The key, user or password contains a character the PS-Auth header can't carry.");
        _base = host.Trim().TrimEnd('/') + "/BeyondTrust/api/public/v3";
        _auth = $"PS-Auth key={apiKey}; runas={runAs};" + (string.IsNullOrEmpty(runAsPassword) ? "" : $" pwd=[{runAsPassword}];");
        _http = new HttpClient(handler ?? BtTls.Handler("ps", cookies: true)) { Timeout = TimeSpan.FromSeconds(30) };
    }

    public static string? SiteProblem(string host) =>
        Uri.TryCreate(host.Trim(), UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && u.UserInfo.Length == 0 && u.AbsolutePath == "/" && u.Query.Length == 0 ? null : "the Password Safe host must be https://your-beyondinsight (no path)";

    private async Task<string> SendAsync(HttpMethod m, string path, JsonNode? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(m, _base + path);
        req.Headers.TryAddWithoutValidation("Authorization", _auth);
        if (body != null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        HttpResponseMessage sent;
        try { sent = await _http.SendAsync(req, ct); }
        catch (HttpRequestException) when (BtTls.Refusal("ps") is { } why) { throw new InvalidOperationException(why); }
        using var r = sent;
        var text = await r.Content.ReadAsStringAsync(ct);
        if ((int)r.StatusCode == 401) throw new InvalidOperationException("Password Safe refused the API key or run-as user (BeyondInsight → Configuration → API Registrations).");
        if ((int)r.StatusCode == 403) throw new InvalidOperationException("Password Safe: not allowed (" + Short(text) + ").");
        if ((int)r.StatusCode == 409) throw new InvalidOperationException("Password Safe: you already have this account checked out, or the request conflicts (" + Short(text) + ").");
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"Password Safe answered HTTP {(int)r.StatusCode} ({Short(text)}).");
        return text;
    }

    private static string Short(string s) { s = Redaction.Mask(new string(s.Where(c => !char.IsControl(c)).ToArray())); return s.Length > 160 ? s[..160] + "…" : s; }

    public async Task SignInAsync(CancellationToken ct = default) { await SendAsync(HttpMethod.Post, "/Auth/SignAppIn", null, ct); _signedIn = true; }

    public async Task<List<ManagedAccount>> AccountsAsync(CancellationToken ct = default)
    {
        if (!_signedIn) await SignInAsync(ct);
        using var d = JsonDocument.Parse(await SendAsync(HttpMethod.Get, "/ManagedAccounts", null, ct));
        var arr = d.RootElement.ValueKind == JsonValueKind.Array ? d.RootElement : d.RootElement.TryGetProperty("Data", out var data) ? data : default;
        if (arr.ValueKind != JsonValueKind.Array) return new();
        long L(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
        string S(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? new string((v.GetString() ?? "").Where(c => !char.IsControl(c)).Take(200).ToArray()) : "";
        return arr.EnumerateArray().Take(20_000).Select(e => new ManagedAccount(L(e, "SystemId"), L(e, "AccountId"), S(e, "SystemName"), S(e, "AccountName"), S(e, "DomainName"))).OrderBy(a => a.SystemName).ThenBy(a => a.AccountName).ToList();
    }

    /// <summary>Requests the account for <paramref name="minutes"/> and reads its password. Returns (request id, password).</summary>
    public async Task<(long RequestId, string Password)> CheckOutAsync(ManagedAccount a, int minutes, string reason, CancellationToken ct = default)
    {
        if (!_signedIn) await SignInAsync(ct);
        reason = new string(reason.Where(c => !char.IsControl(c)).Take(500).ToArray());
        var req = await SendAsync(HttpMethod.Post, "/Requests", new JsonObject { ["SystemID"] = a.SystemId, ["AccountID"] = a.AccountId, ["DurationMinutes"] = Math.Clamp(minutes, 1, 10080), ["Reason"] = reason }, ct);
        if (!long.TryParse(req.Trim().Trim('"'), out var id)) throw new InvalidOperationException("Password Safe didn't return a request id.");
        try
        {
            var pw = await SendAsync(HttpMethod.Get, $"/Credentials/{id}", null, ct);
            var password = JsonSerializer.Deserialize<string>(pw) ?? throw new InvalidOperationException("no credential in the answer");
            return (id, password);
        }
        catch { try { await CheckInAsync(id, "the credential couldn't be read", CancellationToken.None); } catch (Exception) { } throw; }
    }

    public Task CheckInAsync(long requestId, string reason, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Put, $"/Requests/{requestId}/Checkin", new JsonObject { ["Reason"] = new string(reason.Where(c => !char.IsControl(c)).Take(500).ToArray()) }, ct);

    public async Task SignOutAsync() { if (_signedIn) { try { await SendAsync(HttpMethod.Post, "/Auth/Signout", null, CancellationToken.None); } catch (Exception) { } _signedIn = false; } }

    public void Dispose() => _http.Dispose();
}
