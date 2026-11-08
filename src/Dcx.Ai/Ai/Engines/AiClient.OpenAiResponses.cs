using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DcxManager.Services;

namespace DcxManager.Ai;

// AiClient: the OpenAI API engine (0.6.33, ADR-111): OpenAI's Responses API (/v1/responses). GPT-6 models take tool
// definitions only there (the changelog: "Tool calling requires the Responses API"), so the generic OpenAI-compatible
// engine (Chat Completions) stays for LM Studio, vLLM, gateways and Azure, and OpenAI itself gets this one.
public static partial class AiClient
{
    /// <summary>The headers for OpenAI's API: the key as a bearer token (an Azure endpoint takes it as api-key).</summary>
    internal static Dictionary<string, string>? OpenAiApiHeaders(string url)
    {
        var key = S.OpenAiApiKey.Trim();
        if (key.Length == 0) return null;
        return url.Contains(".azure.com", StringComparison.OrdinalIgnoreCase)
            ? new Dictionary<string, string> { ["api-key"] = key }
            : new Dictionary<string, string> { ["Authorization"] = "Bearer " + key };
    }

    /// <summary>The Responses API request for our Ollama-shaped messages (internal so the self-test can check its shape).</summary>
    internal static JsonObject OpenAiResponsesBody(JsonArray msgs, JsonArray? tools, JsonObject? schema, string model, string reasoning)
    {
        var input = new JsonArray();
        foreach (var m in msgs.OfType<JsonObject>())
        {
            var role = m["role"]?.GetValue<string>() ?? "user";
            var content = m["content"]?.GetValue<string>() ?? "";
            switch (role)
            {
                case "tool":
                    input.Add(new JsonObject { ["type"] = "function_call_output", ["call_id"] = m["tool_call_id"]?.GetValue<string>() ?? "call_0", ["output"] = content });
                    break;
                case "assistant":
                    if (content.Length > 0) input.Add(new JsonObject { ["role"] = "assistant", ["content"] = content });
                    // Only call_id goes back: an item id ("fc_…") would make the API ask for the reasoning item that preceded
                    // it, which is not kept (store: false).
                    if (m["tool_calls"] is JsonArray calls)
                        foreach (var c in calls.OfType<JsonObject>())
                            input.Add(new JsonObject
                            {
                                ["type"] = "function_call", ["call_id"] = c["id"]?.GetValue<string>() ?? "call_0",
                                ["name"] = c["function"]?["name"]?.GetValue<string>(),
                                ["arguments"] = c["function"]?["arguments"] is JsonNode a ? (a is JsonValue v && v.TryGetValue<string>(out var str) ? str : a.ToJsonString()) : "{}",
                            });
                    break;
                default:
                    if (m["images"] is JsonArray imgs && imgs.Count > 0)
                    {
                        var parts = new JsonArray();
                        if (content.Length > 0) parts.Add(new JsonObject { ["type"] = "input_text", ["text"] = content });
                        foreach (var img in imgs) parts.Add(new JsonObject { ["type"] = "input_image", ["image_url"] = "data:image/png;base64," + img!.GetValue<string>() });
                        input.Add(new JsonObject { ["role"] = role, ["content"] = parts });
                    }
                    else input.Add(new JsonObject { ["role"] = role is "system" or "developer" or "user" ? role : "user", ["content"] = content });
                    break;
            }
        }
        // store: false — OpenAI keeps no response object for later retrieval; every turn sends the conversation itself.
        // No temperature: OpenAI's reasoning models refuse custom sampling values.
        var body = new JsonObject { ["model"] = model, ["input"] = input, ["store"] = false };
        if (reasoning is "low" or "medium" or "high") body["reasoning"] = new JsonObject { ["effort"] = reasoning };
        if (tools is { Count: > 0 })
        {
            var list = new JsonArray();
            foreach (var t in tools.OfType<JsonObject>())
                list.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["name"] = t["function"]?["name"]?.GetValue<string>(),
                    ["description"] = t["function"]?["description"]?.GetValue<string>(),
                    ["parameters"] = t["function"]?["parameters"]?.DeepClone() ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
                    // The Responses API defaults to strict schemas, which need every property required and
                    // additionalProperties false; the app's tools have optional arguments.
                    ["strict"] = false,
                });
            body["tools"] = list;
        }
        if (schema != null)
            body["text"] = new JsonObject { ["format"] = new JsonObject { ["type"] = "json_schema", ["name"] = "result", ["schema"] = schema.DeepClone(), ["strict"] = false } };
        return body;
    }

    /// <summary>A string from an answer's JSON, or null when it is missing or of another type (a server's answer is
    /// untrusted input: a number where a string belongs must not throw out of the parser).</summary>
    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>A Responses API "response" object as our Ollama-shaped assistant message.</summary>
    internal static JsonObject FromOpenAiResponse(JsonObject response)
    {
        if (response["error"] is JsonObject err && Str(err["message"]) is { } em)
            throw new AiHttpException(500, "OpenAI API: " + em);
        var text = new System.Text.StringBuilder();
        var tc = new JsonArray();
        foreach (var item in (response["output"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            switch (Str(item["type"]))
            {
                case "message":
                    foreach (var part in (item["content"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                        if (Str(part["type"]) == "output_text") text.Append(Str(part["text"]));
                        else if (Str(part["type"]) == "refusal") text.Append(Str(part["refusal"]));
                    break;
                case "function_call":
                    var argText = Str(item["arguments"]) ?? "{}";
                    JsonNode args; try { args = JsonNode.Parse(argText.Length == 0 ? "{}" : argText) as JsonObject ?? new JsonObject(); } catch { args = new JsonObject(); }
                    if (Str(item["name"]) is not { Length: > 0 } name) break;   // a call without a name can't be run
                    tc.Add(new JsonObject { ["id"] = Str(item["call_id"]) ?? Str(item["id"]) ?? "call_" + tc.Count, ["function"] = new JsonObject { ["name"] = name, ["arguments"] = args } });
                    break;
            }
        if (text.Length == 0 && tc.Count == 0 && Str(response["status"]) == "incomplete")
            throw new InvalidOperationException("OpenAI API: the answer stopped early (" + (Str(response["incomplete_details"]?["reason"]) ?? "incomplete") + ").");
        var result = new JsonObject { ["role"] = "assistant", ["content"] = text.ToString() };
        if (tc.Count > 0) result["tool_calls"] = tc;
        return result;
    }

    private static async Task<JsonObject> OpenAiResponsesAsync(string url, JsonArray msgs, JsonArray? tools, JsonObject? schema, CancellationToken ct, Action<string>? delta)
    {
        var body = OpenAiResponsesBody(msgs, tools, schema, Model, S.OpenAiApiReasoning.Trim().ToLowerInvariant());
        var headers = OpenAiApiHeaders(url);
        if (delta != null && schema == null)
        {
            body["stream"] = true;
            return await OpenAiResponsesStreamAsync(url + "/responses", body, headers, delta, ct).ConfigureAwait(false);
        }
        if (schema == null) return FromOpenAiResponse(await PostAsync(url + "/responses", body, headers, "OpenAI API", ct));
        try { return FromOpenAiResponse(await PostAsync(url + "/responses", body, headers, "OpenAI API", ct)); }
        catch (AiHttpException e) when (e.Status is 400 or 422)
        {
            // A model or gateway without JSON schema output: JSON mode, then plain instructions (the callers parse leniently).
            body["text"] = new JsonObject { ["format"] = new JsonObject { ["type"] = "json_object" } };
            try { return FromOpenAiResponse(await PostAsync(url + "/responses", body, headers, "OpenAI API", ct)); }
            catch (AiHttpException e2) when (e2.Status is 400 or 422)
            {
                body.Remove("text");
                return FromOpenAiResponse(await PostAsync(url + "/responses", body, headers, "OpenAI API", ct));
            }
        }
    }

    /// <summary>Responses API streaming: response.output_text.delta pieces are passed on; the final message comes from
    /// response.completed (or, from a stream that ends without it, the items finished so far).</summary>
    private static async Task<JsonObject> OpenAiResponsesStreamAsync(string url, JsonObject body, Dictionary<string, string>? headers, Action<string> delta, CancellationToken ct)
    {
        using var resp = await SendAsync(url, body, headers, "OpenAI API", ct).ConfigureAwait(false);
        var done = new JsonArray();
        await foreach (var ev in Events(resp, ct).ConfigureAwait(false))
        {
            switch (Str(ev["type"]))
            {
                case "response.output_text.delta" or "response.refusal.delta":
                    if (Str(ev["delta"]) is { Length: > 0 } d) delta(d);
                    break;
                case "response.output_item.done":
                    if (ev["item"] is JsonObject item) done.Add(item.DeepClone());
                    break;
                case "response.completed" or "response.incomplete":
                    if (ev["response"] is JsonObject r) return FromOpenAiResponse(r);
                    break;
                case "response.failed":
                    throw new AiHttpException(500, "OpenAI API: " + (Str(ev["response"]?["error"]?["message"]) ?? "the response failed"));
                case "error":
                    throw new AiHttpException(500, "OpenAI API: " + (Str(ev["message"]) ?? Str(ev["error"]?["message"]) ?? "stream error"));
                default:
                    // A server that ignored "stream" answers with the whole response object.
                    if (Str(ev["object"]) == "response")
                    {
                        var whole = FromOpenAiResponse(ev);
                        if (Str(whole["content"]) is { Length: > 0 } c) delta(c);
                        return whole;
                    }
                    break;
            }
        }
        return FromOpenAiResponse(new JsonObject { ["output"] = done });
    }

    /// <summary>OpenAI's model list without the models that don't chat (embeddings, images, audio, speech, moderation,
    /// realtime, legacy completions).</summary>
    internal static bool OpenAiChatModel(string id) =>
        Regex.IsMatch(id, @"^(gpt-|o\d|chatgpt-|chat-latest|codex-)", RegexOptions.IgnoreCase)
        && !Regex.IsMatch(id, @"embedding|image|dall-e|whisper|tts|transcribe|audio|realtime|moderation|search-preview|instruct", RegexOptions.IgnoreCase);

    /// <summary>OpenAI models that read images: GPT-4o, GPT-4.1, GPT-5/6 (all variants), o1/o3/o4 (not o1-mini / o3-mini).</summary>
    internal static bool OpenAiReadsImages(string model) =>
        Regex.IsMatch(model, @"^(gpt-4o|gpt-4\.1|gpt-4\.5|gpt-5|gpt-6|gpt-7|chatgpt-4o|chat-latest|o1(?!-mini)|o3(?!-mini)|o4)", RegexOptions.IgnoreCase);
}
