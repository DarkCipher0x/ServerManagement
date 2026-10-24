using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DcxManager.Services;

namespace DcxManager.Ai;

// AiClient: the Ollama engine (/api/chat) (moved unchanged from AiClient.cs in 0.2.0 phase 6½).
public static partial class AiClient
{
    // ---- Ollama -----------------------------------------------------------------------------------------------------

    private static async Task<JsonObject> OllamaAsync(string url, JsonArray msgs, JsonArray? tools, JsonObject? schema, CancellationToken ct, Action<string>? delta)
    {
        var options = new JsonObject { ["temperature"] = 0.2 };
        int ctx = Engine == "ollama-cpu" ? Math.Min(S.AiContext, 8192) : S.AiContext;
        // Memory guard (ADR-034): under pressure a smaller context keeps the model off the GPU-to-RAM spill that froze the PC.
        ctx = AiResourceGuard.CapContext(ctx);
        options["num_ctx"] = ctx;
        if (Engine == "ollama-cpu")
        {
            options["num_gpu"] = 0;
            if (S.AiCpuThreads > 0) options["num_thread"] = S.AiCpuThreads;
        }
        // Ollama wants tool-call arguments as objects and has no tool_call_id; strip what it doesn't know.
        foreach (var m in msgs.OfType<JsonObject>()) m.Remove("tool_call_id");
        var body = new JsonObject { ["model"] = Model, ["messages"] = msgs, ["stream"] = delta != null, ["options"] = options, ["keep_alive"] = AiResourceGuard.KeepAlive() };
        if (tools != null) body["tools"] = tools.DeepClone();
        if (schema != null) body["format"] = schema.DeepClone();
        JsonObject msg;
        if (delta == null)
        {
            var json = await PostAsync(url + "/api/chat", body, null, "Ollama", ct);
            msg = (JsonObject)(json["message"]?.AsObject() ?? new JsonObject { ["role"] = "assistant", ["content"] = "" }).DeepClone();
        }
        else
        {
            // NDJSON: one {"message":{"content":"…"}} per token; tool calls arrive whole in one chunk; "done" ends it.
            var text = new StringBuilder();
            var calls = new JsonArray();
            using var resp = await SendAsync(url + "/api/chat", body, null, "Ollama", ct).ConfigureAwait(false);
            await foreach (var line in Lines(resp, ct).ConfigureAwait(false))
            {
                if (line.Length == 0) continue;
                if (JsonNode.Parse(line) is not JsonObject chunk) continue;
                if (chunk["error"] is JsonValue ev) throw new AiHttpException(500, "Ollama: " + ev.ToString());
                if (chunk["message"] is JsonObject m)
                {
                    if (m["content"] is JsonValue cv && cv.TryGetValue<string>(out var piece) && piece.Length > 0) { text.Append(piece); delta(piece); }
                    if (m["tool_calls"] is JsonArray tc) foreach (var c in tc) calls.Add(c!.DeepClone());
                }
                if (chunk["done"] is JsonValue dv && dv.TryGetValue<bool>(out var done) && done) break;
            }
            msg = new JsonObject { ["role"] = "assistant", ["content"] = text.ToString() };
            if (calls.Count > 0) msg["tool_calls"] = calls;
        }
        if (msg["tool_calls"] is JsonArray calls2)
        {
            int n = 0;
            foreach (var c in calls2.OfType<JsonObject>()) c["id"] ??= "call_" + n++;
        }
        return msg;
    }
}
