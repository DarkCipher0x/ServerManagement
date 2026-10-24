using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DcxManager.Services;

namespace DcxManager.Ai;

// AiClient: image input: model capabilities, the vision request (moved unchanged from AiClient.cs in 0.2.0 phase 6½).
public static partial class AiClient
{
    // ---- vision (image input) ---------------------------------------------------------------------------------------

    public sealed record VisionCheck(bool Supported, string Reason);

    private static (string Key, VisionCheck Result, DateTime At)? _vision;

    /// <summary>Whether the current engine + model can take images, checked against the model itself where possible
    /// (Ollama /api/show capabilities). Cached for 2 minutes per engine/model/endpoint.</summary>
    public static async Task<VisionCheck> VisionAsync(CancellationToken ct = default)
    {
        if (Engine == "off") return new(false, "AI is switched off.");
        string url;
        try { url = await BaseUrlAsync(ct); } catch (Exception ex) { return new(false, "the AI engine isn't reachable: " + ex.Message.Split('\n')[0]); }
        var key = $"{Engine}|{Model}|{url}|{S.OpenAiAcceptsImages}";
        if (_vision is { } c && c.Key == key && DateTime.Now - c.At < TimeSpan.FromMinutes(2)) return c.Result;
        VisionCheck r;
        switch (Engine)
        {
            case "npu":
                r = new(false, $"Foundry Local model “{Model}” is text-only here; switch to a vision model in Ollama (e.g. qwen2.5vl, llama3.2-vision, gemma3) to analyse images.");
                break;
            case "openai":
                r = S.OpenAiAcceptsImages
                    ? new(true, $"you marked “{Model}” as accepting images (Settings → AI engine)")
                    : new(false, $"the app can't verify that “{Model}” on this OpenAI-compatible server reads images. If it does, tick “This model accepts images” in Settings → AI engine.");
                break;
            case "anthropic" when !IsLocal(url):
                r = Model.StartsWith("claude-", StringComparison.OrdinalIgnoreCase)
                    ? new(true, $"Claude model “{Model}” accepts images")
                    : new(false, $"“{Model}” isn't a known Claude model; can't verify image support.");
                break;
            default:
                // Ollama, or Ollama behind its Anthropic-compatible endpoint.
                var model = Model;
                var ollama = Engine == "anthropic" ? url : await OllamaClient.BaseUrlAsync();
                try
                {
                    var show = await PostAsync(ollama.TrimEnd('/') + "/api/show", new JsonObject { ["model"] = model }, null, "Ollama", ct);
                    r = VisionFromOllamaShow(show) switch
                    {
                        true => new(true, $"Ollama reports that “{model}” has vision"),
                        false => new(false, $"Ollama model “{model}” can't read images. Pull a vision model (qwen2.5vl, llama3.2-vision, gemma3, llava) and select it."),
                        null => new(false, $"Ollama didn't say whether “{model}” reads images (update Ollama); not sent."),
                    };
                }
                catch (Exception ex) { r = new(false, "couldn't ask Ollama about the model: " + ex.Message.Split('\n')[0]); }
                break;
        }
        _vision = (key, r, DateTime.Now);
        return r;
    }

    /// <summary>Reads Ollama's /api/show: "capabilities" (0.6+) contains "vision"; older servers expose a projector
    /// (projector_info, or a clip/mllama family). Null when the answer doesn't say.</summary>
    public static bool? VisionFromOllamaShow(JsonObject show)
    {
        if (show["capabilities"] is JsonArray caps) return caps.Any(c => string.Equals(c?.GetValue<string>(), "vision", StringComparison.OrdinalIgnoreCase));
        if (show["projector_info"] is JsonObject { Count: > 0 }) return true;
        if (show["details"]?["families"] is JsonArray fams && fams.Any(f => f?.GetValue<string>() is "clip" or "mllama")) return true;
        if (show["details"] is JsonObject) return false;
        return null;
    }

    /// <summary>Asks for a JSON object matching <paramref name="schema"/> (native structured output where the engine has it).</summary>
    public static async Task<JsonObject> StructuredAsync(string system, string user, JsonObject schema, CancellationToken ct)
    {
        var msgs = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = system + "\nReply with a single JSON object that matches the required schema. No prose outside the JSON." },
            new JsonObject { ["role"] = "user", ["content"] = user },
        };
        var reply = await ChatAsync(msgs, null, schema, ct);
        if (reply["structured"] is JsonObject direct) return direct;
        var text = reply["content"]?.GetValue<string>() ?? "";
        return ExtractJson(text) ?? throw new InvalidOperationException("The model didn't return valid JSON. Try a larger or more capable model.\n\n" + (text.Length > 400 ? text[..400] + "…" : text));
    }

    public static JsonObject? ExtractJson(string text)
    {
        text = text.Trim();
        var fence = Regex.Match(text, @"```(?:json)?\s*([\s\S]*?)```");
        if (fence.Success) text = fence.Groups[1].Value.Trim();
        int start = text.IndexOf('{'), end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try { return JsonNode.Parse(text[start..(end + 1)]) as JsonObject; } catch { return null; }
    }
}
