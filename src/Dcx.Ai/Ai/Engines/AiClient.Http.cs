using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DcxManager.Services;

namespace DcxManager.Ai;

// AiClient: the HTTP calls shared by the engines: streaming, retries, errors (moved unchanged from AiClient.cs in 0.2.0 phase 6½).
public static partial class AiClient
{
    // ---- http -----------------------------------------------------------------------------------------------------

    private sealed class AiHttpException : InvalidOperationException
    {
        public int Status { get; }
        public AiHttpException(int status, string message) : base(message) => Status = status;
    }

    private static async Task<JsonObject> PostAsync(string url, JsonObject body, Dictionary<string, string>? headers, string who, CancellationToken ct)
    {
        using var resp = await SendAsync(url, body, headers, who, ct);
        return JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct)) as JsonObject ?? new JsonObject();
    }

    /// <summary>Sends and returns a successful response with its body unread (streaming reads it as it arrives);
    /// errors are read and thrown as <see cref="AiHttpException"/> with the server's own message.</summary>
    private static async Task<HttpResponseMessage> SendAsync(string url, JsonObject body, Dictionary<string, string>? headers, string who, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        if (headers != null) foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, v);
        HttpResponseMessage resp;
        try { resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false); }
        catch (HttpRequestException ex) when (ex.InnerException is OfflineBlockedException ob) { throw ob; }
        catch (HttpRequestException ex) { throw new InvalidOperationException($"{who} isn't reachable at {new Uri(url).GetLeftPart(UriPartial.Authority)} — {ex.Message}"); }
        if (resp.IsSuccessStatusCode) return resp;
        using (resp)
        {
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            string err;
            try { var n = JsonNode.Parse(text); err = n?["error"] is JsonObject eo ? eo["message"]?.GetValue<string>() ?? eo.ToJsonString() : n?["error"]?.GetValue<string>() ?? text; }
            catch { err = text.Length > 300 ? text[..300] : text; }
            if (err.Contains("not found") && who == "Ollama") err = $"model “{Model}” isn't installed — run: ollama pull {Model}";
            else if (Engine == "npu" && Regex.IsMatch(err, "not (found|loaded|cached)", RegexOptions.IgnoreCase)) err += $" — run: foundry model load {Model}";
            else if (err.Contains("does not support tools")) err = $"model “{Model}” can't call tools (does not support tools). Use a tool-capable model such as qwen2.5, qwen3 or llama3.1.";
            throw new AiHttpException((int)resp.StatusCode, $"{who}: {err}");
        }
    }

    /// <summary>The response body line by line as it arrives (NDJSON and server-sent events).</summary>
    private static async IAsyncEnumerable<string> Lines(HttpResponseMessage resp, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new System.IO.StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line) yield return line;
    }

    /// <summary>Server-sent events: the JSON of each "data:" line ("[DONE]" ends the stream). A server that ignored
    /// "stream" and answered with one JSON document yields that document.</summary>
    private static async IAsyncEnumerable<JsonObject> Events(HttpResponseMessage resp, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (resp.Content.Headers.ContentType?.MediaType is "application/json")
        {
            if (JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) is JsonObject whole) yield return whole;
            yield break;
        }
        await foreach (var line in Lines(resp, ct).ConfigureAwait(false))
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].Trim();
            if (data == "[DONE]") yield break;
            JsonObject? o = null;
            try { o = JsonNode.Parse(data) as JsonObject; } catch (System.Text.Json.JsonException) { }
            if (o != null) yield return o;
        }
    }

    /// <summary>OpenAI-style streaming: delta.content pieces and delta.tool_calls fragments (by index) are put together.</summary>
    private static async Task<JsonObject> OpenAiStreamAsync(string url, JsonObject body, Dictionary<string, string>? headers, Action<string> delta, CancellationToken ct)
    {
        using var resp = await SendAsync(url, body, headers, "AI server", ct).ConfigureAwait(false);
        var text = new StringBuilder();
        var calls = new SortedDictionary<int, (string? Id, string? Name, StringBuilder Args)>();
        await foreach (var ev in Events(resp, ct).ConfigureAwait(false))
        {
            if (ev["error"] is JsonNode err) throw new AiHttpException(500, "AI server: " + (err["message"]?.ToString() ?? err.ToJsonString()));
            var choice = ev["choices"]?[0];
            var d = choice?["delta"] ?? choice?["message"];   // a whole (non-streamed) answer has "message"
            if (d == null) continue;
            if (d["content"] is JsonValue cv && cv.TryGetValue<string>(out var piece) && piece.Length > 0) { text.Append(piece); delta(piece); }
            if (d["tool_calls"] is JsonArray tcs)
                for (int i = 0; i < tcs.Count; i++)
                {
                    if (tcs[i] is not JsonObject tc) continue;
                    int idx = tc["index"] is JsonValue iv && iv.TryGetValue<int>(out var ix) ? ix : i;
                    var cur = calls.TryGetValue(idx, out var c) ? c : (null, null, new StringBuilder());
                    cur.Id ??= tc["id"]?.GetValue<string>();
                    cur.Name ??= tc["function"]?["name"]?.GetValue<string>();
                    if (tc["function"]?["arguments"] is JsonValue av && av.TryGetValue<string>(out var part)) cur.Args.Append(part);
                    else if (tc["function"]?["arguments"] is JsonObject ao) cur.Args.Append(ao.ToJsonString());
                    calls[idx] = cur;
                }
        }
        var result = new JsonObject { ["role"] = "assistant", ["content"] = text.ToString() };
        if (calls.Count > 0)
        {
            var tc = new JsonArray();
            foreach (var (i, c) in calls)
            {
                JsonNode args; try { args = JsonNode.Parse(c.Args.Length > 0 ? c.Args.ToString() : "{}") ?? new JsonObject(); } catch { args = new JsonObject(); }
                tc.Add(new JsonObject { ["id"] = c.Id ?? "call_" + i, ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = args } });
            }
            result["tool_calls"] = tc;
        }
        return result;
    }

    /// <summary>Anthropic Messages streaming: text_delta pieces; tool_use blocks with input_json_delta fragments.</summary>
    private static async Task<JsonObject> AnthropicStreamAsync(string url, JsonObject body, Dictionary<string, string> headers, Action<string> delta, CancellationToken ct)
    {
        using var resp = await SendAsync(url, body, headers, "Anthropic API", ct).ConfigureAwait(false);
        var text = new StringBuilder();
        var blocks = new SortedDictionary<int, (string Id, string Name, StringBuilder Json)>();
        await foreach (var ev in Events(resp, ct).ConfigureAwait(false))
        {
            var type = ev["type"]?.GetValue<string>();
            if (type == "error") throw new AiHttpException(500, "Anthropic API: " + (ev["error"]?["message"]?.ToString() ?? ev.ToJsonString()));
            if (type == "message_stop") break;
            if (type == "content_block_start" && ev["content_block"]?["type"]?.GetValue<string>() == "tool_use")
                blocks[ev["index"]?.GetValue<int>() ?? blocks.Count] = (ev["content_block"]!["id"]?.GetValue<string>() ?? "call_" + blocks.Count, ev["content_block"]!["name"]?.GetValue<string>() ?? "", new StringBuilder());
            else if (type == "content_block_delta")
            {
                var d = ev["delta"];
                var dt = d?["type"]?.GetValue<string>();
                if (dt == "text_delta" && d!["text"]?.GetValue<string>() is { Length: > 0 } piece) { text.Append(piece); delta(piece); }
                else if (dt == "input_json_delta" && blocks.TryGetValue(ev["index"]?.GetValue<int>() ?? -1, out var b)) b.Json.Append(d!["partial_json"]?.GetValue<string>());
            }
            else if (type == "message" && ev["content"] is JsonArray whole)   // the server answered without streaming
            {
                foreach (var blk in whole.OfType<JsonObject>())
                    if (blk["type"]?.GetValue<string>() == "text") { var t = blk["text"]?.GetValue<string>() ?? ""; text.Append(t); delta(t); }
                    else if (blk["type"]?.GetValue<string>() == "tool_use") blocks[blocks.Count] = (blk["id"]?.GetValue<string>() ?? "call_" + blocks.Count, blk["name"]?.GetValue<string>() ?? "", new StringBuilder(blk["input"]?.ToJsonString() ?? "{}"));
                break;
            }
        }
        var result = new JsonObject { ["role"] = "assistant", ["content"] = text.ToString() };
        if (blocks.Count > 0)
        {
            var tc = new JsonArray();
            foreach (var (_, b) in blocks)
            {
                JsonNode args; try { args = JsonNode.Parse(b.Json.Length > 0 ? b.Json.ToString() : "{}") ?? new JsonObject(); } catch { args = new JsonObject(); }
                tc.Add(new JsonObject { ["id"] = b.Id, ["function"] = new JsonObject { ["name"] = b.Name, ["arguments"] = args } });
            }
            result["tool_calls"] = tc;
        }
        return result;
    }
}
