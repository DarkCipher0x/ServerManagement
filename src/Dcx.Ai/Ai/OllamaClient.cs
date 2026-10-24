using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DcxManager.Services;
using DcxManager.Ssh;

namespace DcxManager.Ai;

/// <summary>
/// Talks to Ollama (https://ollama.com). The server can be this PC, another machine on the LAN, or a GPU server reached
/// through an SSH tunnel to a saved host (Settings → Assistant) — so prompts never leave machines you control.
/// </summary>
public static class OllamaClient
{
    private static readonly HttpClient Http = new(Offline.Handler()) { Timeout = TimeSpan.FromMinutes(10) };

    public static async Task<string> BaseUrlAsync()
    {
        var s = AppSettings.Current;
        if (s.OllamaTunnelHostId != null && AppData.Current.FindHost(s.OllamaTunnelHostId) is { } host)
        {
            var port = await TunnelManager.EnsureEphemeralAsync("ollama-tunnel", host, "127.0.0.1", 11434);
            return $"http://127.0.0.1:{port}";
        }
        return (string.IsNullOrWhiteSpace(s.OllamaUrl) ? "http://localhost:11434" : s.OllamaUrl.Trim()).TrimEnd('/');
    }

    public static async Task<List<string>> ModelsAsync(CancellationToken ct = default)
    {
        var url = await BaseUrlAsync();
        var json = await Http.GetStringAsync(url + "/api/tags", ct);
        var node = JsonNode.Parse(json);
        return node?["models"]?.AsArray().Select(m => m?["name"]?.GetValue<string>() ?? "").Where(n => n.Length > 0).OrderBy(n => n).ToList() ?? new();
    }

    public static async Task<string> VersionAsync(CancellationToken ct = default)
    {
        var url = await BaseUrlAsync();
        var json = await Http.GetStringAsync(url + "/api/version", ct);
        return JsonNode.Parse(json)?["version"]?.GetValue<string>() ?? "?";
    }

    /// <summary>One /api/chat call (non-streaming). Returns the assistant message object.</summary>
    public static async Task<JsonObject> ChatAsync(JsonArray messages, JsonArray? tools, CancellationToken ct)
    {
        var s = AppSettings.Current;
        var url = await BaseUrlAsync();
        var body = new JsonObject
        {
            ["model"] = s.OllamaModel,
            ["messages"] = messages.DeepClone(),
            ["stream"] = false,
            ["options"] = new JsonObject { ["num_ctx"] = s.AiContext, ["temperature"] = 0.2 },
        };
        if (tools != null) body["tools"] = tools.DeepClone();

        HttpResponseMessage resp;
        try { resp = await Http.PostAsync(url + "/api/chat", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct); }
        catch (HttpRequestException ex) when (ex.InnerException is OfflineBlockedException ob) { throw ob; }
        catch (HttpRequestException) { throw new InvalidOperationException($"Ollama isn't reachable at {url}. Is it running? (ollama serve)"); }
        using (resp)
        {
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                var err = TryErr(text) ?? resp.ReasonPhrase ?? "error";
                if (err.Contains("not found")) err = $"model “{s.OllamaModel}” isn't installed — run: ollama pull {s.OllamaModel}";
                else if (err.Contains("does not support tools")) err = $"model “{s.OllamaModel}” can't call tools. Use a tool-capable model such as qwen2.5, qwen3 or llama3.1.";
                throw new InvalidOperationException("Ollama: " + err);
            }
            return JsonNode.Parse(text)?["message"]?.AsObject() ?? new JsonObject { ["role"] = "assistant", ["content"] = "" };
        }
    }

    private static string? TryErr(string json)
    {
        try { return JsonNode.Parse(json)?["error"]?.GetValue<string>(); } catch { return null; }
    }
}
