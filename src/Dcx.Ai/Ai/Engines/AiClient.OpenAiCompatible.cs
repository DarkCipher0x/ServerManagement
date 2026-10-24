using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DcxManager.Services;

namespace DcxManager.Ai;

// AiClient: OpenAI-compatible engines: LM Studio, llama.cpp, vLLM, Foundry Local, OpenAI, Azure (moved unchanged from AiClient.cs in 0.2.0 phase 6½).
public static partial class AiClient
{
    // ---- OpenAI-compatible (LM Studio, llama.cpp, vLLM, Foundry Local, OpenAI, Azure) ------------------------------

    private static async Task<JsonObject> OpenAiAsync(string url, JsonArray msgs, JsonArray? tools, JsonObject? schema, CancellationToken ct, Action<string>? delta)
    {
        var outMsgs = new JsonArray();
        foreach (var m in msgs.OfType<JsonObject>())
        {
            var role = m["role"]?.GetValue<string>() ?? "user";
            var content = m["content"]?.GetValue<string>() ?? "";
            if (role == "tool") { outMsgs.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = m["tool_call_id"]?.GetValue<string>() ?? "call_0", ["content"] = content }); continue; }
            var o = new JsonObject { ["role"] = role, ["content"] = content };
            if (m["images"] is JsonArray imgs && imgs.Count > 0)
            {
                var parts = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = content } };
                foreach (var img in imgs) parts.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64," + img!.GetValue<string>() } });
                o["content"] = parts;
            }
            if (role == "assistant" && m["tool_calls"] is JsonArray calls && calls.Count > 0)
            {
                var tc = new JsonArray();
                foreach (var c in calls.OfType<JsonObject>())
                    tc.Add(new JsonObject
                    {
                        ["id"] = c["id"]?.GetValue<string>() ?? "call_0", ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = c["function"]?["name"]?.GetValue<string>(), ["arguments"] = c["function"]?["arguments"]?.ToJsonString() ?? "{}" },
                    });
                o["tool_calls"] = tc;
            }
            outMsgs.Add(o);
        }
        var model = Engine == "npu" ? await FoundryModelIdAsync(url[..^"/v1".Length], ct) : Model;
        var body = new JsonObject { ["model"] = model, ["messages"] = outMsgs, ["temperature"] = 0.2 };
        if (tools != null) body["tools"] = tools.DeepClone();
        var key = Engine == "npu" ? "" : S.OpenAiKey;
        var headers = key.Length == 0 ? null : url.Contains(".azure.com", StringComparison.OrdinalIgnoreCase)
            ? new Dictionary<string, string> { ["api-key"] = key } : new Dictionary<string, string> { ["Authorization"] = "Bearer " + key };

        if (delta != null && schema == null)
        {
            body["stream"] = true;
            try { return await OpenAiStreamAsync(url + "/chat/completions", body, headers, delta, ct).ConfigureAwait(false); }
            // A server that can't stream refuses before anything arrived: ask again without streaming.
            catch (AiHttpException e) when (e.Status is 400 or 422 or 501 && !ToolsRefused(e.Message)) { body.Remove("stream"); }
        }
        JsonObject json;
        if (schema != null)
        {
            // Not every server has json_schema; fall back to json_object, then to plain instructions.
            body["response_format"] = new JsonObject { ["type"] = "json_schema", ["json_schema"] = new JsonObject { ["name"] = "result", ["schema"] = schema.DeepClone() } };
            try { json = await PostAsync(url + "/chat/completions", body, headers, "AI server", ct); }
            catch (AiHttpException e) when (e.Status is 400 or 422 or 500)
            {
                body["response_format"] = new JsonObject { ["type"] = "json_object" };
                try { json = await PostAsync(url + "/chat/completions", body, headers, "AI server", ct); }
                catch (AiHttpException e2) when (e2.Status is 400 or 422 or 500)
                {
                    body.Remove("response_format");
                    json = await PostAsync(url + "/chat/completions", body, headers, "AI server", ct);
                }
            }
        }
        else json = await PostAsync(url + "/chat/completions", body, headers, "AI server", ct);

        var msg = json["choices"]?[0]?["message"] as JsonObject;
        var result = new JsonObject { ["role"] = "assistant", ["content"] = msg?["content"]?.GetValue<string>() ?? "" };
        if (msg?["tool_calls"] is JsonArray oc && oc.Count > 0)
        {
            var tc = new JsonArray();
            foreach (var c in oc.OfType<JsonObject>())
            {
                var argText = c["function"]?["arguments"]?.GetValue<string>() ?? "{}";
                JsonNode args; try { args = JsonNode.Parse(argText) ?? new JsonObject(); } catch { args = new JsonObject(); }
                tc.Add(new JsonObject { ["id"] = c["id"]?.GetValue<string>(), ["function"] = new JsonObject { ["name"] = c["function"]?["name"]?.GetValue<string>(), ["arguments"] = args } });
            }
            result["tool_calls"] = tc;
        }
        return result;
    }
}
