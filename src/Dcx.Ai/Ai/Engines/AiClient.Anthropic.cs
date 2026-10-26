using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DcxManager.Services;

namespace DcxManager.Ai;

// AiClient: the Anthropic Messages engine: Claude API or an Anthropic-compatible endpoint (moved unchanged from AiClient.cs in 0.2.0 phase 6½).
public static partial class AiClient
{
    // ---- Anthropic Messages (Claude API, or Ollama's Anthropic-compatible endpoint) ---------------------------------

    private static async Task<JsonObject> AnthropicAsync(string url, JsonArray msgs, JsonArray? tools, JsonObject? schema, CancellationToken ct, Action<string>? delta)
    {
        var system = new StringBuilder();
        var outMsgs = new JsonArray();
        void Push(string role, JsonNode block)
        {
            if (outMsgs.Count > 0 && outMsgs[^1]!["role"]!.GetValue<string>() == role) { outMsgs[^1]!["content"]!.AsArray().Add(block); return; }
            outMsgs.Add(new JsonObject { ["role"] = role, ["content"] = new JsonArray(block) });
        }
        foreach (var m in msgs.OfType<JsonObject>())
        {
            var role = m["role"]?.GetValue<string>() ?? "user";
            var content = m["content"]?.GetValue<string>() ?? "";
            switch (role)
            {
                case "system": system.AppendLine(content); break;
                case "tool": Push("user", new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = m["tool_call_id"]?.GetValue<string>() ?? "call_0", ["content"] = content.Length > 0 ? content : "(empty)" }); break;
                case "assistant":
                    if (content.Length > 0) Push("assistant", new JsonObject { ["type"] = "text", ["text"] = content });
                    if (m["tool_calls"] is JsonArray calls)
                        foreach (var c in calls.OfType<JsonObject>())
                            Push("assistant", new JsonObject { ["type"] = "tool_use", ["id"] = c["id"]?.GetValue<string>() ?? "call_0", ["name"] = c["function"]?["name"]?.GetValue<string>(), ["input"] = c["function"]?["arguments"]?.DeepClone() ?? new JsonObject() });
                    break;
                default:
                    if (m["images"] is JsonArray imgs)
                        foreach (var img in imgs) Push("user", new JsonObject { ["type"] = "image", ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = "image/png", ["data"] = img!.GetValue<string>() } });
                    if (content.Length > 0) Push("user", new JsonObject { ["type"] = "text", ["text"] = content });
                    break;
            }
        }
        var body = new JsonObject { ["model"] = Model, ["max_tokens"] = 4096, ["messages"] = outMsgs };
        // 0.6.29: the Claude API refuses sampling settings on current models ("`temperature` is deprecated for this model"),
        // so only a local Anthropic-compatible server (Ollama) still gets one; SendAsync drops it if that refuses it too.
        if (IsLocal(url)) body["temperature"] = 0.2;
        if (system.Length > 0) body["system"] = system.ToString();
        var toolList = new JsonArray();
        if (tools != null)
            foreach (var t in tools.OfType<JsonObject>())
                toolList.Add(new JsonObject { ["name"] = t["function"]?["name"]?.GetValue<string>(), ["description"] = t["function"]?["description"]?.GetValue<string>(), ["input_schema"] = t["function"]?["parameters"]?.DeepClone() });
        if (schema != null)
        {
            // Forcing a single "result" tool is Anthropic's structured-output pattern.
            toolList.Add(new JsonObject { ["name"] = "result", ["description"] = "Return the result.", ["input_schema"] = schema.DeepClone() });
            body["tool_choice"] = new JsonObject { ["type"] = "tool", ["name"] = "result" };
        }
        if (toolList.Count > 0) body["tools"] = toolList;
        var headers = new Dictionary<string, string> { ["anthropic-version"] = "2023-06-01" };
        if (S.AnthropicKey.Length > 0) headers["x-api-key"] = S.AnthropicKey;
        else if (IsLocal(url)) headers["x-api-key"] = "ollama";

        if (delta != null && schema == null)
        {
            body["stream"] = true;
            return await AnthropicStreamAsync(url + "/v1/messages", body, headers, delta, ct).ConfigureAwait(false);
        }
        JsonObject json;
        try { json = await PostAsync(url + "/v1/messages", body, headers, "Anthropic API", ct); }
        catch (AiHttpException e) when (schema != null && e.Status is 400 or 422)
        {
            body.Remove("tool_choice");
            if (toolList.Count == 1) body.Remove("tools");
            json = await PostAsync(url + "/v1/messages", body, headers, "Anthropic API", ct);
        }

        var result = new JsonObject { ["role"] = "assistant" };
        var text = new StringBuilder();
        var tc = new JsonArray();
        foreach (var b in json["content"]?.AsArray().OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
        {
            var type = b["type"]?.GetValue<string>();
            if (type == "text") text.Append(b["text"]?.GetValue<string>());
            else if (type == "tool_use")
            {
                var name = b["name"]?.GetValue<string>();
                if (schema != null && name == "result" && b["input"] is JsonObject structured) { result["structured"] = structured.DeepClone(); continue; }
                tc.Add(new JsonObject { ["id"] = b["id"]?.GetValue<string>(), ["function"] = new JsonObject { ["name"] = name, ["arguments"] = b["input"]?.DeepClone() ?? new JsonObject() } });
            }
        }
        result["content"] = text.ToString();
        if (tc.Count > 0) result["tool_calls"] = tc;
        return result;
    }
}
