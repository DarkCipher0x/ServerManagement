using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Xml;
using DcxManager.Services;

namespace DcxManager.BeyondTrust;

/// <summary>A Jump Item as the configuration API lists it (one record per kind).</summary>
public sealed record JumpItem(string Kind, long Id, string Name, string Hostname, int Port, string Username, string Group, string Tag, string Comments);

/// <summary>A row of the access-session report.</summary>
public sealed record PraSession(string Id, string Start, string End, string Jumpoint, string Target, string Users);

/// <summary>
/// BeyondTrust Privileged Remote Access / Remote Support (0.5.0, plan #16): an API account (OAuth client credentials:
/// POST /oauth2/token with the client id and secret as Basic auth; the secret kept in the encrypted settings) reads the
/// configuration API (/api/config/v1: Jump Items by kind, Jump Groups, Jumpoints, Vault account names — never a vault
/// secret) and the reporting API (/api/reporting, access sessions, XML read without DTDs). A Jump Item opens in your own
/// access console through the client-scripting URL (/api/client_script … start_jump_item_session), signed in as you there.
/// Read-only: nothing is created, changed or deleted on the appliance. HTTPS to the configured site only; offline mode applies.
/// </summary>
public sealed class PraClient(string site, string clientId, string secret, HttpMessageHandler? handler = null)
{
    public static readonly string[] JumpKinds = { "shell-jump", "remote-rdp", "remote-vnc", "protocol-tunnel-jump", "web-jump", "jump-client" };
    private readonly HttpClient _http = new(handler ?? BtTls.Handler("pra", cookies: false)) { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>Sends, turning a TLS refusal (an untrusted or changed certificate) into the reason with its fingerprint.</summary>
    private async Task<HttpResponseMessage> Send(HttpRequestMessage req, CancellationToken ct)
    {
        try { return await _http.SendAsync(req, ct); }
        catch (HttpRequestException) when (BtTls.Refusal("pra") is { } why) { throw new InvalidOperationException(why); }
    }
    private string? _token; private DateTime _expires;

    /// <summary>https://host[:port] only (no path, query or user info), or why not.</summary>
    public static string? SiteProblem(string site) =>
        Uri.TryCreate(site.Trim(), UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && u.UserInfo.Length == 0 && u.AbsolutePath == "/" && u.Query.Length == 0 && u.Fragment.Length == 0 ? null : "the site must be https://your-appliance (no path)";

    private string Base => site.Trim().TrimEnd('/');

    private async Task<string> TokenAsync(CancellationToken ct)
    {
        if (_token != null && _expires > DateTime.UtcNow.AddMinutes(1)) return _token;
        if (SiteProblem(site) is { } why) throw new InvalidOperationException("BeyondTrust: " + why);
        using var req = new HttpRequestMessage(HttpMethod.Post, Base + "/oauth2/token") { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials" }) };
        req.Headers.TryAddWithoutValidation("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(clientId + ":" + secret)));
        using var r = await Send(req, ct);
        var body = await r.Content.ReadAsStringAsync(ct);
        if ((int)r.StatusCode is 400 or 401) throw new InvalidOperationException("BeyondTrust refused the API account (check its client id and secret, and that it may use the configuration / reporting APIs).");
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"BeyondTrust answered HTTP {(int)r.StatusCode} for the token.");
        using var d = JsonDocument.Parse(body);
        _token = d.RootElement.TryGetProperty("access_token", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        _expires = DateTime.UtcNow.AddSeconds(d.RootElement.TryGetProperty("expires_in", out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var s) ? Math.Clamp(s, 60, 86400) : 3600);
        return _token ?? throw new InvalidOperationException("no access token");
    }

    private async Task<string> GetAsync(string pathAndQuery, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, Base + pathAndQuery);
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + await TokenAsync(ct));
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var r = await Send(req, ct);
        if ((int)r.StatusCode == 403) throw new InvalidOperationException("The API account isn't allowed to read that (BeyondTrust → Management → API Configuration → permissions).");
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"BeyondTrust answered HTTP {(int)r.StatusCode} for {pathAndQuery.Split('?')[0]}.");
        if (r.Content.Headers.ContentLength is > 32_000_000) throw new InvalidOperationException("the answer is too large");
        return await r.Content.ReadAsStringAsync(ct);
    }

    /// <summary>Every page of a configuration list (per_page=100, current_page=1…; stops on a short page, at most 200 pages).</summary>
    private async Task<List<JsonElement>> ListAsync(string what, CancellationToken ct)
    {
        var all = new List<JsonElement>();
        for (int page = 1; page <= 200; page++)
        {
            var text = await GetAsync($"/api/config/v1/{what}?per_page=100&current_page={page}", ct);
            using var d = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
            if (d.RootElement.ValueKind != JsonValueKind.Array) break;
            var items = d.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
            all.AddRange(items);
            if (items.Count < 100) break;
        }
        return all;
    }

    private static string S(JsonElement e, string k) => e.TryGetProperty(k, out var v) ? v.ValueKind switch { JsonValueKind.String => Clean(v.GetString()), JsonValueKind.Number => v.GetRawText(), _ => "" } : "";
    private static string Clean(string? s) { var t = new string((s ?? "").Where(c => !char.IsControl(c)).ToArray()); return t.Length > 200 ? t[..200] : t; }

    public async Task<List<JumpItem>> JumpItemsAsync(CancellationToken ct = default)
    {
        var groups = (await ListAsync("jump-group", ct)).ToDictionary(g => S(g, "id"), g => S(g, "name"));
        var list = new List<JumpItem>();
        foreach (var kind in JumpKinds)
        {
            List<JsonElement> items;
            try { items = await ListAsync("jump-item/" + kind, ct); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("HTTP 404")) { continue; }   // a kind this appliance version doesn't have
            foreach (var e in items)
                list.Add(new JumpItem(kind, long.TryParse(S(e, "id"), out var id) ? id : 0, S(e, "name"), S(e, "hostname") is { Length: > 0 } h ? h : S(e, "url"),
                    int.TryParse(S(e, "port"), out var port) ? port : 0, S(e, "username"), groups.GetValueOrDefault(S(e, "jump_group_id"), S(e, "jump_group_type")), S(e, "tag"), S(e, "comments")));
        }
        return list.OrderBy(j => j.Group).ThenBy(j => j.Name).ToList();
    }

    public async Task<List<(string Name, string Type, string Description)>> VaultAccountsAsync(CancellationToken ct = default) =>
        (await ListAsync("vault/account", ct)).Select(e => (S(e, "name"), S(e, "type"), S(e, "description"))).ToList();

    public async Task<List<(string Name, string Hostname, bool Connected)>> JumpointsAsync(CancellationToken ct = default) =>
        (await ListAsync("jumpoint", ct)).Select(e => (S(e, "name"), S(e, "appliance_hostname"), e.TryGetProperty("connected", out var c) && c.ValueKind == JsonValueKind.True)).ToList();

    /// <summary>Access sessions since <paramref name="since"/> (reporting API, XML; DTDs refused).</summary>
    public async Task<List<PraSession>> SessionsAsync(DateTime since, CancellationToken ct = default)
    {
        var xml = await GetAsync($"/api/reporting?generate_report=AccessSession&start_date={since:yyyy-MM-dd}&duration=0", ct);
        return ParseSessions(xml);
    }

    public static List<PraSession> ParseSessions(string xml)
    {
        var doc = new XmlDocument { XmlResolver = null };
        using (var xr = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 32_000_000 })) doc.Load(xr);
        if (doc.DocumentElement?.LocalName == "error") throw new InvalidOperationException("BeyondTrust: " + Clean(doc.DocumentElement.InnerText));
        var list = new List<PraSession>();
        foreach (XmlElement s in doc.GetElementsByTagName("session"))
        {
            string C(string n) => Clean(s.GetElementsByTagName(n).Cast<XmlNode>().FirstOrDefault()?.InnerText);
            var users = string.Join(", ", s.GetElementsByTagName("username").Cast<XmlNode>().Select(n => Clean(n.InnerText)).Distinct().Take(5));
            list.Add(new PraSession(s.GetAttribute("lsid"), C("start_time"), C("end_time"), C("jumpoint"), C("hostname") is { Length: > 0 } h ? h : C("name"), users));
            if (list.Count >= 5000) break;
        }
        return list;
    }

    /// <summary>The access-console link that starts a session with the Jump Item for this hostname (the console asks you to
    /// pick when several match). Opened in your browser: the appliance hands it to your access console.</summary>
    public static string JumpUrl(string site, JumpItem item)
    {
        if (SiteProblem(site) is { } why) throw new ArgumentException(why);
        var method = item.Kind switch { "shell-jump" => "shelljump", "remote-rdp" => "rdp", "remote-vnc" => "vnc", "jump-client" => "pinned", _ => "" };
        var q = new List<string> { "type=rep", "operation=generate", "action=start_jump_item_session", "client.hostname=" + Uri.EscapeDataString(item.Hostname) };
        if (method.Length > 0) q.Add("jump.method=" + method);
        return site.Trim().TrimEnd('/') + "/api/client_script?" + string.Join("&", q);
    }
}
