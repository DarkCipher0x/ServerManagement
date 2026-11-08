using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using DcxManager.Services;

namespace DcxManager.Infra;

/// <summary>
/// OAuth 2.0 client-credentials sign-in for the cloud providers (0.6.33: HPE GreenLake, Sophos Central). The client id and
/// secret are a vault credential (user name = client id, password = client secret; it may be kept in Bitwarden or Password
/// Safe, ADR-096) read only when a token is needed. The access token lives in memory only, is reused until a minute before
/// it expires, and is dropped on a 401 so the next request signs in again. The token request goes to a fixed https token
/// address of the service (never one an answer names), with the secret in the POST body — never in a URL or a log.
/// </summary>
public sealed class OAuthClientCredentials : DelegatingHandler
{
    private readonly Func<string?> _credentialId;
    private readonly string _purpose;
    private readonly Func<string> _tokenUrl;
    private readonly string? _scope;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token, _tokenFor;
    private DateTime _until;

    /// <summary>How many tokens were fetched (self-test).</summary>
    internal int Fetched { get; private set; }

    public OAuthClientCredentials(Func<string?> credentialId, string purpose, Func<string> tokenUrl, HttpMessageHandler inner, string? scope = null) : base(inner)
    {
        _credentialId = credentialId; _purpose = purpose; _tokenUrl = tokenUrl; _scope = scope;
    }

    public void Forget() { _token = null; _tokenFor = null; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(ct).ConfigureAwait(false));
        var r = await base.SendAsync(request, ct).ConfigureAwait(false);
        if (r.StatusCode == System.Net.HttpStatusCode.Unauthorized) Forget();
        return r;
    }

    private async Task<string> TokenAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var cred = await ExternalCredentials.UseAsync(_credentialId(), _purpose, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("choose the vault credential with the API client id (user name) and secret (password)");
            if (cred.Username.Trim().Length == 0 || cred.Secret.Length == 0) throw new InvalidOperationException("the vault credential needs the client id as its user name and the client secret as its password");
            // A changed credential (edited, or another one chosen) never reuses the old token.
            var who = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cred.Username.Trim() + "\n" + cred.Secret)))[..16];
            if (_token != null && _tokenFor == who && DateTime.UtcNow < _until) return _token;
            var url = _tokenUrl();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) throw new InvalidOperationException("the token address must be https");
            var form = new List<KeyValuePair<string, string>>
            {
                new("grant_type", "client_credentials"), new("client_id", cred.Username.Trim()), new("client_secret", cred.Secret),
            };
            if (_scope != null) form.Add(new("scope", _scope));
            using var req = new HttpRequestMessage(HttpMethod.Post, u) { Content = new FormUrlEncodedContent(form) };
            req.Headers.Accept.ParseAdd("application/json");
            using var r = await base.SendAsync(req, ct).ConfigureAwait(false);
            var text = await InfraJson.ReadAsync(r, ct).ConfigureAwait(false);
            int code = (int)r.StatusCode;
            if (code is 400 or 401 or 403) throw new InvalidOperationException($"sign-in refused (HTTP {code}): the client id or secret was rejected, or the API client is disabled — {InfraJson.Message(text)}");
            if (code >= 300) throw new InvalidOperationException($"sign-in failed: HTTP {code} {InfraJson.Message(text)}");
            System.Text.Json.Nodes.JsonNode? j;
            try { j = InfraJson.Parse(text); } catch (System.Text.Json.JsonException) { throw new InvalidOperationException("the sign-in service didn't answer with JSON"); }
            var token = InfraJson.S(j, "access_token");
            if (token.Length is < 8 or > 16384 || token.Any(ch => ch < 0x21 || ch > 0x7E)) throw new InvalidOperationException("the sign-in service sent no usable access token");
            var life = long.TryParse(InfraJson.S(j, "expires_in"), out var s) && s is > 0 and < 86400 * 7 ? TimeSpan.FromSeconds(s) : TimeSpan.FromMinutes(15);
            _until = DateTime.UtcNow + (life > TimeSpan.FromMinutes(2) ? life - TimeSpan.FromMinutes(1) : life / 2);
            _token = token; _tokenFor = who;
            Fetched++;
            return token;
        }
        finally { _gate.Release(); }
    }
}
