using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DcxManager.Services;
using DcxManager.SignIn;

namespace DcxManager.Notify;

/// <summary>Where a share goes: a Teams Workflows webhook (its URL is a secret, kept in the encrypted settings), or a channel
/// / chat reached through Microsoft Graph with your own sign-in (only ids are kept here).</summary>
public sealed class TeamsDestination
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    /// <summary>"webhook", "channel" or "chat".</summary>
    public string Kind { get; set; } = "channel";
    public string TeamId { get; set; } = "";
    public string ChannelId { get; set; } = "";
    public string ChatId { get; set; } = "";
    public string KindTitle => Kind switch { "webhook" => "Workflows webhook (text)", "channel" => "channel (Graph)", "chat" => "chat (Graph)", _ => Kind };
    public override string ToString() => $"{Name} — {KindTitle}";
}

/// <summary>
/// Sharing to Microsoft Teams (0.5.0, plan #13; DarkCipher0x: "Teams integration for sharing screenshots etc."). Text is masked
/// (Redaction) before it leaves; the caller shows a preview of exactly what is sent. Graph: POST a chatMessage to
/// /teams/{team}/channels/{channel}/messages or /chats/{chat}/messages with the screenshot as a hosted content (4 MB at
/// most), signed in as you (delegated; ChannelMessage.Send / ChatMessage.Send). Webhook: an Adaptive Card to a Teams
/// Workflows URL on Microsoft's own hosts only; screenshots don't fit a webhook (Teams' 28 KB limit), so it carries text.
/// Offline mode, the on/off switch and the images switch (admin-protected, Group Policy) are checked here.
/// </summary>
public static class TeamsShare
{
    public const string Scopes = "openid profile offline_access ChannelMessage.Send ChatMessage.Send Team.ReadBasic.All Channel.ReadBasic.All Chat.ReadBasic";
    public const string TokenCache = "teams-token.dcx";
    public const int MaxImageBytes = 3_500_000;
    internal static HttpMessageHandler? HandlerForTest { get; set; }
    internal static Func<Task<string>>? TokenForTest { get; set; }
    private static HttpClient? _http;

    private static HttpClient Http => _http ??= new HttpClient(HandlerForTest ?? Offline.Handler()) { Timeout = TimeSpan.FromSeconds(60) };
    internal static void ResetForTest() { _http?.Dispose(); _http = null; }

    public static List<TeamsDestination> Destinations()
    {
        try { return JsonSerializer.Deserialize<List<TeamsDestination>>(AppSettings.Current.TeamsDestinations.Length > 0 ? AppSettings.Current.TeamsDestinations : "[]") ?? new(); }
        catch (JsonException) { return new(); }
    }

    public static void SaveDestinations(List<TeamsDestination> list) { AppSettings.Current.TeamsDestinations = JsonSerializer.Serialize(list); AppSettings.Save(); }

    /// <summary>A Workflows / Power Automate webhook URL on Microsoft's hosts, https, no user info — or why not.</summary>
    public static string? WebhookProblem(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps || u.UserInfo.Length > 0) return "not an https address";
        var h = u.IdnHost.ToLowerInvariant();
        bool microsoft = h.EndsWith(".logic.azure.com") || h.EndsWith(".environment.api.powerplatform.com") || h.EndsWith(".api.powerplatform.com") || h.EndsWith(".webhook.office.com");
        return microsoft ? null : "not a Teams Workflows / Power Automate address (…logic.azure.com or …powerplatform.com)";
    }

    public static void SetWebhookUrl(TeamsDestination d, string url)
    {
        if (WebhookProblem(url) is { } why) throw new ArgumentException(why);
        AppSettings.Current.ObserveSecrets["teams:" + d.Id] = url.Trim();
    }

    private static void Allowed(bool image)
    {
        if (!AppSettings.Current.TeamsEnabled) throw new InvalidOperationException("Sharing to Microsoft Teams is switched off on this PC (Settings → Integrations → Microsoft Teams, or your organisation's policy).");
        if (image && !AppSettings.Current.TeamsAllowImages) throw new InvalidOperationException("Sharing images to Teams is switched off on this PC (policy).");
    }

    private static Task<string> TokenAsync(CancellationToken ct) => TokenForTest?.Invoke() ?? EntraDelegated.TokenAsync(AppSettings.Current.TeamsTenant, AppSettings.Current.TeamsClientId, Scopes, TokenCache, ct);

    /// <summary>The HTML a Graph message carries (title, masked text as preformatted lines, the image).</summary>
    public static string Html(string title, string text, bool image) =>
        $"<h3>{WebUtility.HtmlEncode(title)}</h3>" + (text.Length > 0 ? $"<pre>{WebUtility.HtmlEncode(Redaction.Mask(text))}</pre>" : "") + (image ? "<img src=\"../hostedContents/1/$value\" alt=\"screenshot\">" : "") +
        "<p><em>Shared from DarkCipher ServerManagement</em></p>";

    /// <summary>The Adaptive Card a webhook carries (masked text, no image).</summary>
    public static string Card(string title, string text) => new JsonObject
    {
        ["type"] = "message",
        ["attachments"] = new JsonArray(new JsonObject
        {
            ["contentType"] = "application/vnd.microsoft.card.adaptive",
            ["content"] = new JsonObject
            {
                ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json", ["type"] = "AdaptiveCard", ["version"] = "1.4",
                ["body"] = new JsonArray(
                    new JsonObject { ["type"] = "TextBlock", ["text"] = title, ["weight"] = "Bolder", ["size"] = "Medium", ["wrap"] = true },
                    new JsonObject { ["type"] = "TextBlock", ["text"] = Redaction.Mask(text.Length > 20_000 ? text[..20_000] + "…" : text), ["wrap"] = true, ["fontType"] = "Monospace" },
                    new JsonObject { ["type"] = "TextBlock", ["text"] = "Shared from DarkCipher ServerManagement", ["isSubtle"] = true, ["size"] = "Small" }),
            },
        }),
    }.ToJsonString();

    /// <summary>Sends to a destination. <paramref name="png"/> only for Graph destinations (refused for a webhook).</summary>
    public static async Task SendAsync(TeamsDestination d, string title, string text, byte[]? png, CancellationToken ct = default)
    {
        Allowed(png != null);
        title = title.Length > 200 ? title[..200] : title;
        if (d.Kind == "webhook")
        {
            if (png != null) throw new InvalidOperationException("A webhook can't carry a screenshot (Teams limits webhook messages to 28 KB): add a channel or chat destination (Graph) for images.");
            var url = AppSettings.Current.ObserveSecrets.GetValueOrDefault("teams:" + d.Id) ?? throw new InvalidOperationException("This webhook's address isn't stored any more: add it again.");
            if (WebhookProblem(url) is { } why) throw new InvalidOperationException(why);
            using var body = new StringContent(Card(title, text), Encoding.UTF8, "application/json");
            using var r = await Http.PostAsync(url, body, ct);
            if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"Teams answered HTTP {(int)r.StatusCode}.");
            return;
        }
        if (png != null && png.Length > MaxImageBytes) throw new InvalidOperationException("The image is larger than Teams accepts in a message (about 4 MB).");
        var path = d.Kind == "chat" ? $"/chats/{Uri.EscapeDataString(d.ChatId)}/messages" : $"/teams/{Uri.EscapeDataString(d.TeamId)}/channels/{Uri.EscapeDataString(d.ChannelId)}/messages";
        var msg = new JsonObject { ["body"] = new JsonObject { ["contentType"] = "html", ["content"] = Html(title, text, png != null) } };
        if (png != null) msg["hostedContents"] = new JsonArray(new JsonObject { ["@microsoft.graph.temporaryId"] = "1", ["contentBytes"] = Convert.ToBase64String(png), ["contentType"] = "image/png" });
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://graph.microsoft.com/v1.0" + path) { Content = new StringContent(msg.ToJsonString(), Encoding.UTF8, "application/json") };
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + await TokenAsync(ct));
        using var resp = await Http.SendAsync(req, ct);
        if ((int)resp.StatusCode is 401 or 403) throw new InvalidOperationException($"Teams refused the message (HTTP {(int)resp.StatusCode}): the app registration needs the delegated permissions ChannelMessage.Send / ChatMessage.Send, and you must be a member of that channel or chat.");
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"Teams answered HTTP {(int)resp.StatusCode}.");
    }

    public sealed record Pick(string Id, string Name, string ParentId = "");

    private static async Task<List<Pick>> ListAsync(string path, Func<JsonElement, Pick?> read, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0" + path);
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + await TokenAsync(ct));
        using var r = await Http.SendAsync(req, ct);
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"Microsoft Graph answered HTTP {(int)r.StatusCode}.");
        using var d = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
        return d.RootElement.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Take(500).Select(read).Where(x => x != null).Cast<Pick>().ToList() : new();
    }

    private static string S(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    public static Task<List<Pick>> TeamsAsync(CancellationToken ct = default) { Allowed(false); return ListAsync("/me/joinedTeams?$select=id,displayName", e => new Pick(S(e, "id"), S(e, "displayName")), ct); }
    public static Task<List<Pick>> ChannelsAsync(string teamId, CancellationToken ct = default) => ListAsync($"/teams/{Uri.EscapeDataString(teamId)}/channels?$select=id,displayName", e => new Pick(S(e, "id"), S(e, "displayName"), teamId), ct);
    public static Task<List<Pick>> ChatsAsync(CancellationToken ct = default) { Allowed(false); return ListAsync("/me/chats?$top=50&$select=id,topic,chatType", e => new Pick(S(e, "id"), S(e, "topic") is { Length: > 0 } t ? t : $"({S(e, "chatType")} chat)"), ct); }
}
