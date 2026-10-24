using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DcxManager.Services;

namespace DcxManager.Ai;

/// <summary>One entry of the visible step log. Kind: "plan" (the model's text before it calls tools), "tool" (a call
/// started), "result" (its outcome, shortened), "answer", "note" (limits, fallbacks), "error".</summary>
public sealed record AgentStep(int Step, string Kind, string Text, TimeSpan? Took = null)
{
    public string Glyph => Kind switch { "plan" => "◇", "tool" => "⚙", "result" => "↳", "answer" => "✦", "error" => "✕", _ => "·" };
    public override string ToString() => $"{Glyph} {(Step > 0 ? $"step {Step} · " : "")}{Text}{(Took is { } t ? $" ({t.TotalMilliseconds:0} ms)" : "")}";
}

/// <summary>What one question cost: model calls, tool calls, time to the first streamed text and in total.</summary>
public sealed record AgentRun(int Steps, int ToolCalls, TimeSpan FirstToken, TimeSpan Total, bool NativeTools, bool HitLimit);

/// <summary>Everything an AI surface plugs into the loop.</summary>
public sealed class AgentHooks
{
    /// <summary>Runs one tool (with that surface's approvals inside).</summary>
    public required Func<string, JsonObject, CancellationToken, Task<string>> RunTool { get; init; }
    /// <summary>True when the call only reads and won't ask the user, so it may run at the same time as its neighbours.</summary>
    public Func<string, JsonObject, bool> ParallelSafe { get; init; } = (_, _) => false;
    public Action<AgentStep>? Step { get; init; }
    /// <summary>Streamed answer text (any thread). A new round starts with <see cref="Round"/>.</summary>
    public Action<string>? Delta { get; init; }
    /// <summary>A new model call starts: the live text shown so far belongs to the previous round.</summary>
    public Action<int>? Round { get; init; }
}

/// <summary>
/// The multi-step tool loop both AI surfaces use (ADR-034): plan → call tools → observe → continue, up to a step limit,
/// cancellable at any point. Independent read-only calls of one round run at the same time. Models without native tool
/// calling get the same tools through a text protocol (&lt;tool_call&gt;{json}&lt;/tool_call&gt;), so an older or smaller model can
/// still act as an agent. Permissions stay in the tools: the loop never approves anything itself.
/// </summary>
public static class AiAgent
{
    public const int MaxToolOutput = 6000;

    /// <summary>The model call: AiClient (masking, internet gate, timing, streaming). The self-test may swap it.</summary>
    internal static Func<JsonArray, JsonArray?, Action<string>?, CancellationToken, Task<JsonObject>> Chat =
        (m, t, d, ct) => AiClient.ChatAsync(m, t, null, ct, d);

    public static async Task<(string Answer, AgentRun Run)> RunAsync(JsonArray messages, JsonArray? tools, AgentHooks hooks, int maxSteps, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        TimeSpan? first = null;
        int toolCalls = 0;
        maxSteps = Math.Clamp(maxSteps, 1, 50);
        bool native = tools == null || tools.Count == 0 || await AiClient.ToolsSupportedAsync(ct);
        if (!native) Note(hooks, 0, $"“{AiClient.Model}” has no native tool calling: using the text tool protocol instead.");
        int systemAddedAt = -1;
        void UseTextProtocol()
        {
            if (systemAddedAt >= 0 || tools == null) return;
            messages.Insert(1, new JsonObject { ["role"] = "system", ["content"] = TextProtocol(tools) });
            systemAddedAt = 1;
        }
        if (!native) UseTextProtocol();
        var seen = new Dictionary<string, int>();
        int corrections = 0;

        for (int step = 1; step <= maxSteps; step++)
        {
            ct.ThrowIfCancellationRequested();
            hooks.Round?.Invoke(step);
            var filter = new ToolTextFilter(native ? null : hooks.Delta);
            Action<string>? delta = hooks.Delta == null ? null : s => { first ??= sw.Elapsed; if (native) hooks.Delta(s); else filter.Push(s); };
            JsonObject msg;
            var callSw = Stopwatch.StartNew();
            try { msg = await Chat(messages, native ? tools : null, delta, ct); }
            catch (Exception ex) when (native && tools != null && ex is not OperationCanceledException && AiClient.ToolsRefused(ex.Message))
            {
                AiClient.MarkNoTools();
                native = false;
                UseTextProtocol();
                Note(hooks, step, $"“{AiClient.Model}” refused tool definitions: switching to the text tool protocol.");
                step--;
                continue;
            }
            first ??= sw.Elapsed;   // not streamed: the first text arrived with the whole answer
            var content = msg["content"]?.GetValue<string>() ?? "";
            var calls = msg["tool_calls"] as JsonArray;
            bool textCalls = !native;
            if (!native)
            {
                var (text, parsed) = ParseTextCalls(content, tools);
                content = text;
                calls = parsed.Count > 0 ? parsed : null;
                msg = new JsonObject { ["role"] = "assistant", ["content"] = msg["content"]?.GetValue<string>() ?? "" };
            }
            else if ((calls == null || calls.Count == 0) && tools is { Count: > 0 } && content.Length > 0 && SalvageTextCalls(content, tools) is { Count: > 0 } salvaged)
            {
                // Some small models (phi-4-mini on Foundry Local, s45) accept tool definitions but then write the call as
                // text, e.g. terminal_run(target=…, "df -h"). When the whole reply is such calls to real tools, run them:
                // the tools' own approvals still decide. Results go back as text, as for the text protocol.
                Note(hooks, step, $"“{AiClient.Model}” wrote its tool call as text instead of calling the tool: running it as a tool call.");
                calls = salvaged;
                content = "";
                textCalls = true;
            }
            messages.Add(msg.DeepClone());
            // 0.6.12 (DarkCipher0x's second screenshot: "(terminal_run session 79e25fd0 command: top -l 1 …)" inside prose, then
            // "please review and provide approval" for a call that was never made): instead of reading every shape a small
            // model invents, tell it nothing ran and switch this run to the strict <tool_call>{json}</tool_call> format,
            // which small models follow far more reliably. Twice at most; after that the step log says nothing ran.
            // 0.6.17 (DarkCipher0x's screenshot: phi-4-mini answered "As an AI developed by Microsoft … I am unable to
            // execute commands" with the tools in hand): a model that claims it can't act, before any tool ran, is told it
            // can and asked for the call in the same strict format.
            bool described = MentionsToolUse(content, tools ?? new JsonArray());
            bool refused = !described && toolCalls == 0 && ClaimsNoAccess(content);
            if ((calls == null || calls.Count == 0) && tools is { Count: > 0 } && corrections < 2 && (described || refused))
            {
                corrections++;
                Note(hooks, step, refused
                    ? $"“{AiClient.Model}” said it can't run commands although it has the tools: asking it to make the call in the tool-call format."
                    : $"“{AiClient.Model}” described a tool call without making it, so nothing ran: asking it to make the call in the tool-call format.");
                if (native) { native = false; UseTextProtocol(); }
                messages.Add(new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = (refused
                                      ? "You CAN act here: the tools in your list run for real on the user's systems (terminal_run types into the target terminal, " +
                                        "and the user approves anything that changes something). Don't say you can't; use a tool now. "
                                      : "Your last reply described a tool call but did not make one, so NOTHING ran and there is no result yet. ") +
                                  "Make the call now: reply with only <tool_call>{\"name\": \"terminal_run\", \"arguments\": {\"command\": \"…\", \"purpose\": \"…\"}}</tool_call> " +
                                  "(any tool from the list, one block per call), and nothing else. Use commands for the target's operating system.",
                });
                continue;
            }
            if (calls == null || calls.Count == 0)
            {
                // 0.6.10: a reply that writes about tool calls it never made (and may show "results" it made up) is
                // flagged, so nobody reads it as something that ran.
                if (tools is { Count: > 0 } && MentionsToolUse(content, tools))
                    Note(hooks, step, $"“{AiClient.Model}” described tool calls in its answer but made none, so nothing ran on the target. A larger model (e.g. qwen2.5 7B or llama3.1 8B in Ollama) calls tools reliably.");
                Step(hooks, new AgentStep(step, "answer", $"answer ready · {Fmt(sw.Elapsed)} in total"));
                return (content.Trim(), new AgentRun(step, toolCalls, first ?? sw.Elapsed, sw.Elapsed, native, false));
            }
            if (content.Trim().Length > 0) Step(hooks, new AgentStep(step, "plan", Short(content.Trim(), 400)));

            // Run the calls: consecutive parallel-safe ones together, the rest one at a time, results in the original order.
            var list = calls.OfType<JsonObject>().Select((c, i) =>
            {
                var fn = c["function"];
                var name = fn?["name"]?.GetValue<string>() ?? "";
                var args = fn?["arguments"] as JsonObject ?? ParseArgs(fn?["arguments"]);
                return (Index: i, Id: c["id"]?.GetValue<string>() ?? "call_" + i, Name: name, Args: args);
            }).ToList();
            var results = new string[list.Count];
            int k = 0;
            while (k < list.Count)
            {
                int end = k;
                while (end < list.Count && hooks.ParallelSafe(list[end].Name, list[end].Args)) end++;
                var batch = end > k ? list.GetRange(k, end - k) : list.GetRange(k, 1);
                await Task.WhenAll(batch.Select(async c =>
                {
                    var key = c.Name + c.Args.ToJsonString();
                    lock (seen) seen[key] = seen.GetValueOrDefault(key) + 1;
                    Step(hooks, new AgentStep(step, "tool", $"{c.Name}({Brief(c.Args)})"));
                    var tsw = Stopwatch.StartNew();
                    string r;
                    if (seen[key] > 3) r = "ERROR: this exact call was already made 3 times; use what you have or try something else.";
                    else if (tools != null && MissingArgs(tools, c.Name, c.Args) is { } missing) r = missing;
                    else
                        try { r = await hooks.RunTool(c.Name, c.Args, ct); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) { r = "ERROR: " + ex.Message; }
                    if (r.Length > MaxToolOutput) r = r[..MaxToolOutput] + $"\n…(truncated, {r.Length} chars total)";
                    results[c.Index] = r;
                    Step(hooks, new AgentStep(step, "result", $"{c.Name}: {Short(r.Replace('\n', ' '), 160)}", tsw.Elapsed));
                }));
                toolCalls += batch.Count;
                k += batch.Count;
            }
            if (!textCalls)
                foreach (var c in list)
                    messages.Add(new JsonObject { ["role"] = "tool", ["tool_name"] = c.Name, ["tool_call_id"] = c.Id, ["content"] = results[c.Index] });
            else
                messages.Add(new JsonObject { ["role"] = "user", ["content"] = string.Concat(list.Select(c => $"<tool_result name=\"{c.Name}\">\n{results[c.Index]}\n</tool_result>\n")) + "Continue: call more tools, or give the final answer." });
        }

        // Step limit: one last call without tools so the user still gets what was found.
        Note(hooks, maxSteps, $"Step limit ({maxSteps}) reached: asking for a summary of what was found so far.");
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = "You reached the step limit. Without calling tools, summarise what you found and what is still open." });
        hooks.Round?.Invoke(maxSteps + 1);
        var last = await Chat(messages, null, hooks.Delta, ct);
        messages.Add(last.DeepClone());
        var answer = (last["content"]?.GetValue<string>() ?? "").Trim();
        return ((answer.Length > 0 ? answer : "(Stopped at the step limit.)") + $"\n\n(Stopped after {maxSteps} steps — raise the limit in Settings → AI engine, or ask a narrower question.)",
            new AgentRun(maxSteps + 1, toolCalls, first ?? sw.Elapsed, sw.Elapsed, native, true));
    }

    private static void Step(AgentHooks h, AgentStep s) { try { h.Step?.Invoke(s); } catch (Exception ex) { Log.Write("AI step", ex); } }
    private static void Note(AgentHooks h, int step, string text) => Step(h, new AgentStep(step, "note", text));
    private static string Fmt(TimeSpan t) => t.TotalSeconds < 10 ? $"{t.TotalSeconds:0.0} s" : $"{t.TotalSeconds:0} s";

    /// <summary>A model's JSON, read whole, or null. JsonNode reads objects lazily, so a repeated key ({"command": "a",
    /// "command": "b"}) only threw later, on first use, outside any catch (0.6.20, found by the fuzzer); every object and
    /// array is opened here, inside the catch.</summary>
    internal static JsonNode? SafeParse(string json)
    {
        try
        {
            var node = JsonNode.Parse(json);
            void Open(JsonNode? n)
            {
                if (n is JsonObject o) foreach (var kv in o) Open(kv.Value);
                else if (n is JsonArray a) foreach (var x in a) Open(x);
            }
            Open(node);
            return node;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or ArgumentException or InvalidOperationException) { return null; }
    }

    internal static JsonObject ParseArgs(JsonNode? n)
    {
        if (n is JsonValue v && v.TryGetValue<string>(out var s)) { if (SafeParse(s) is JsonObject o) return o; }
        return new JsonObject();
    }

    internal static string Brief(JsonObject a) => Capabilities.BriefInline(a);

    /// <summary>A call without a required argument (s45: phi-4-mini sent terminal_run(keys={ENTER}, purpose=…) and no
    /// command): an error that says exactly what the tool takes, so the model can correct the call in its next step.
    /// Null when every required argument is there (or the tool is unknown: the registry answers that).</summary>
    internal static string? MissingArgs(JsonArray tools, string name, JsonObject args)
    {
        var def = tools.OfType<JsonObject>().Select(t => t["function"] as JsonObject).FirstOrDefault(f => f?["name"]?.GetValue<string>() == name);
        if (def?["parameters"] is not JsonObject ps) return null;
        var required = (ps["required"] as JsonArray)?.Select(x => x?.GetValue<string>()).OfType<string>().ToList() ?? new();
        // "purpose" only labels the approval box: a call without it still runs (small models often leave it out).
        var missing = required.Where(k => k != "purpose").Where(k => args[k] is null || (args[k] is JsonValue v && v.TryGetValue<string>(out var s) && s.Trim().Length == 0)).ToList();
        if (missing.Count == 0) return null;
        var props = ps["properties"] as JsonObject;
        var sb = new StringBuilder($"ERROR: {name} needs ");
        sb.Append(string.Join(", ", missing.Select(k => $"\"{k}\" ({props?[k]?["description"]?.GetValue<string>() ?? "required"})")));
        sb.Append(". You sent: ").Append(args.Count == 0 ? "no arguments" : string.Join(", ", args.Select(kv => kv.Key))).Append('.');
        var unknown = args.Select(kv => kv.Key).Where(k => props?[k] == null).ToList();
        if (unknown.Count > 0)
        {
            var owners = tools.OfType<JsonObject>().Select(t => t["function"] as JsonObject)
                .Where(f => f?["parameters"]?["properties"] is JsonObject p && unknown.Any(u => p[u] != null)).Select(f => f!["name"]!.GetValue<string>()).ToList();
            sb.Append($" {name} has no {string.Join("/", unknown)} parameter").Append(owners.Count > 0 ? $" (that belongs to {string.Join(", ", owners)})." : ".");
        }
        sb.Append($" Call {name} again with {string.Join(" and ", required.Select(k => "\"" + k + "\""))}, e.g. {name}({string.Join(", ", required.Select(k => k + "=\"…\""))}).");
        return sb.ToString();
    }
    private static string Short(string s, int n) => s.Length > n ? s[..n] + "…" : s;

    // ---- text tool protocol (models without native tool calling) -----------------------------------------------------

    /// <summary>The tools described in prose for a model that can't take tool definitions.</summary>
    internal static string TextProtocol(JsonArray tools)
    {
        var sb = new StringBuilder("You can use tools. To call one, write exactly one block per call and nothing after the blocks:\n" +
            "<tool_call>{\"name\": \"tool_name\", \"arguments\": {\"param\": \"value\"}}</tool_call>\n" +
            "Results come back in <tool_result> blocks. When you have what you need, answer without any <tool_call>.\nTools:\n");
        foreach (var t in tools.OfType<JsonObject>())
        {
            var f = t["function"];
            var ps = (f?["parameters"]?["properties"] as JsonObject)?.Select(p => $"{p.Key} ({p.Value?["type"]}): {p.Value?["description"]}") ?? Enumerable.Empty<string>();
            sb.Append("- ").Append(f?["name"]).Append(": ").Append(f?["description"]);
            var list = ps.ToList();
            if (list.Count > 0) sb.Append(" Parameters: ").Append(string.Join("; ", list));
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static readonly Regex FencedCallRx = new(@"```(?:json)?\s*(\{\s*""name""\s*:.*?\})\s*```", RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>Tool calls written as text: the remaining text and the calls in the native shape. With
    /// <paramref name="tools"/>, a reply that is nothing but calls to those tools in other shapes (a bare JSON object, or
    /// <c>name(arg=value, "value")</c>) counts too (<see cref="SalvageTextCalls"/>).</summary>
    internal static (string Text, JsonArray Calls) ParseTextCalls(string content, JsonArray? tools = null)
    {
        var calls = new JsonArray();
        var text = new StringBuilder();
        int at = 0;
        foreach (var (start, end, call) in TaggedCalls(content))
        {
            text.Append(content, at, start - at);
            at = end;
            call["id"] = "text_" + calls.Count;
            calls.Add(call);
        }
        text.Append(content, at, content.Length - at);
        var rest = text.ToString();
        foreach (Match m in FencedCallRx.Matches(rest))
            if (CallFrom(m.Groups[1].Value) is { } fenced) { fenced["id"] = "text_" + calls.Count; calls.Add(fenced); }
        rest = FencedCallRx.Replace(rest, "").Trim();
        if (calls.Count == 0 && tools != null && SalvageTextCalls(content, tools) is { Count: > 0 } other) return ("", other);
        return (rest, calls);
    }

    /// <summary>A call object ({"name", "arguments" | "parameters"}) in the native shape, or null.</summary>
    private static JsonObject? CallFrom(string json)
    {
        var o = SafeParse(json) as JsonObject;
        if (o?["name"] is not JsonValue nv || !nv.TryGetValue<string>(out var name) || name.Trim().Length == 0) return null;
        var args = o["arguments"] as JsonObject ?? (o["arguments"] is JsonValue ? ParseArgs(o["arguments"]) : null) ?? o["parameters"] as JsonObject ?? new JsonObject();
        return new JsonObject { ["function"] = new JsonObject { ["name"] = name.Trim(), ["arguments"] = args.DeepClone() } };
    }

    /// <summary>
    /// The &lt;tool_call&gt; blocks in a reply, however a small model closes them (0.6.15; DarkCipher0x's screenshot,
    /// phi-4-mini on Foundry Local wrote <c>&lt;tool_call&gt;{"name": "terminal_run", "arguments": {…}}&gt;</c>, ending in
    /// "}&gt;" instead of "&lt;/tool_call&gt;", so nothing ran even after the correction). The JSON object after the tag is read
    /// by matching its braces (quotes and escapes respected), then whatever closing follows is taken with it:
    /// "&lt;/tool_call&gt;", a cut-off "&lt;/tool_call", "&gt;", "/&gt;", stray "}" or nothing. Each block: its start, its end and
    /// the call in the native shape (no id). A block whose JSON doesn't parse, or has no name, is skipped.
    /// </summary>
    internal static List<(int Start, int End, JsonObject Call)> TaggedCalls(string content)
    {
        var found = new List<(int, int, JsonObject)>();
        int i = 0;
        while (i < content.Length && (i = content.IndexOf("<tool_call", i, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            int gt = content.IndexOf('>', i);
            int open = gt < 0 ? -1 : content.IndexOf('{', gt);
            // Only whitespace may sit between the tag and its JSON.
            if (open < 0 || content.AsSpan(gt + 1, open - gt - 1).Trim().Length > 0) { i += 10; continue; }
            int end = JsonObjectEnd(content, open);
            if (end < 0) break;   // never closed: not a call
            var closing = Regex.Match(content.Substring(end), @"^[\s}]*(?:</\s*tool_call\s*>?|/?>)?", RegexOptions.IgnoreCase);
            int stop = end + closing.Length;
            if (CallFrom(content[open..end]) is { } call) found.Add((i, stop, call));
            i = stop;
        }
        return found;
    }

    /// <summary>The index just after the '}' that closes the JSON object starting at <paramref name="open"/>, or -1.</summary>
    private static int JsonObjectEnd(string s, int open)
    {
        int depth = 0;
        bool inString = false, escaped = false;
        for (int k = open; k < s.Length; k++)
        {
            char c = s[k];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') inString = true;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return k + 1;
        }
        return -1;
    }

    /// <summary>
    /// Calls a model wrote as plain text although it was given the tools: a bare JSON object <c>{"name": …, "arguments": …}</c>,
    /// or one <c>tool_name(key=value, "positional")</c> per line. Taken only when the WHOLE reply (code fences aside) is such
    /// calls, and only for tools it was offered, so a tool merely mentioned in an explanation never runs. Named arguments the
    /// tool doesn't have are dropped (the AI Engineer's target, for one, is always the session the user chose); positional
    /// values fill the tool's required parameters in order. Returns null when the reply isn't just calls.
    /// </summary>
    internal static JsonArray? SalvageTextCalls(string content, JsonArray tools)
    {
        var defs = tools.OfType<JsonObject>().Select(t => t["function"] as JsonObject).Where(f => f?["name"] != null)
            .ToDictionary(f => f!["name"]!.GetValue<string>(), f => f!, StringComparer.Ordinal);
        // 0.6.15: <tool_call> blocks (however they're closed) in a reply that came back as text although the tools were
        // given natively: the tag is the model saying "this is a call", so prose around it doesn't stop it.
        var tagged = TaggedCalls(content).Where(t => defs.ContainsKey(t.Call["function"]!["name"]!.GetValue<string>())).ToList();
        if (tagged.Count > 0)
        {
            var fromTags = new JsonArray();
            foreach (var t in tagged) { t.Call["id"] = "text_" + fromTags.Count; fromTags.Add(t.Call); }
            return fromTags;
        }
        if (CallsOnly(Regex.Replace(content, @"```[a-zA-Z]*", "").Trim(), defs) is { } whole) return whole;

        // s45 (DarkCipher0x's screenshot): prose around a code block that is nothing but a call, e.g.
        //   Command executed: ```terminal_run --session vps-test-01 --command 'mpstat 1 24'```
        // The block is the call the model meant to make; the prose is not run. Blocks that aren't calls are ignored.
        var calls = new JsonArray();
        foreach (Match m in Regex.Matches(content, @"```[A-Za-z0-9_+-]*[ \t]*\r?\n?(.*?)```", RegexOptions.Singleline))
            if (CallsOnly(m.Groups[1].Value.Trim(), defs) is { } inBlock)
                foreach (var c in inBlock.ToList()) { inBlock.Remove(c); c!["id"] = "text_" + calls.Count; calls.Add(c); }
        if (calls.Count > 0) return calls;

        // 0.6.10 (DarkCipher0x's screenshot, phi-4-mini on Foundry Local): looser shapes, still nothing but calls once tidied.
        return CallsOnly(TidyLooseCalls(Regex.Replace(content, @"```[a-zA-Z]*", "").Trim(), defs), defs);
    }

    /// <summary>
    /// A small model's looser written calls, tidied into the shapes <see cref="CallsOnly"/> reads (0.6.10; DarkCipher0x's
    /// screenshot, phi-4-mini on Foundry Local wrote:
    /// <code>
    /// terminal_run terminal_run -- session 95cad8f2 -- display top
    /// [No response from the target terminal session 95cad8f2]
    /// terminal_read_screen
    /// terminal_read_screen -- session 95cad8f2
    /// </code>
    /// and nothing ran). A line that is only a tool's name (a heading) and a line wholly in [brackets] (a result the model
    /// made up before anything ran) are dropped; a repeated tool name and "-- key" with a space are joined. Any other prose
    /// stays, so a reply with explanation in it is still not taken as calls. What the tools then accept is theirs to say:
    /// unknown arguments are dropped and a missing command comes back as an error the model can correct.
    /// </summary>
    private static string TidyLooseCalls(string body, Dictionary<string, JsonObject> defs)
    {
        var lines = new List<string>();
        foreach (var raw in body.Split('\n'))
        {
            var l = raw.TrimEnd('\r').Trim();
            if (l.Length == 0 || defs.ContainsKey(l)) continue;
            if (l.StartsWith('[') && l.EndsWith(']')) continue;
            var twice = Regex.Match(l, @"^([A-Za-z_][A-Za-z0-9_]*)\s+\1(?=\s|\(|$)");
            if (twice.Success && defs.ContainsKey(twice.Groups[1].Value)) l = twice.Groups[1].Value + l[twice.Length..];
            l = Regex.Replace(l, @"(?<=\s)--\s+(?=[A-Za-z_])", "--");
            lines.Add(l);
        }
        return string.Join("\n", lines);
    }

    /// <summary>Whether a reply that made no call still writes one out (a tool's name with call-like arguments): the loop then
    /// asks the model to make the call, and failing that the step log says plainly that nothing ran. Call-like:
    /// <c>name(…)</c>, <c>name --key</c>, <c>(name …)</c>, <c>name key: value</c> / <c>key=value</c>, or <c>name session|command|target|keys|lines …</c>.
    /// A name mentioned in a sentence ("terminal_run is the tool that…") is not.</summary>
    internal static bool MentionsToolUse(string content, JsonArray tools) =>
        tools.OfType<JsonObject>().Select(t => t["function"]?["name"]?.GetValue<string>()).OfType<string>()
            .Any(n =>
            {
                var name = @"(?<![A-Za-z0-9_])" + Regex.Escape(n) + @"(?![A-Za-z0-9_])";
                return Regex.IsMatch(content, name + @"(\s*\(|\s*\{|\s+--?\s*[A-Za-z]|\s+[A-Za-z_]+\s*[:=]|\s+(session|command|target|keys|lines)\b)", RegexOptions.IgnoreCase)
                    || Regex.IsMatch(content, @"\(\s*" + name + @"\s");
            });

    private static readonly Regex NoAccessRx = new(
        @"\bas an (ai|a\.i\.|artificial intelligence|language model)\b" +
        @"|\b(i am|i'm|i’m)\s+(an ai|a language model|a text-based)" +
        @"|\bi(\s+am|'m|’m)?\s+(currently\s+|also\s+)?(unable|not able|not capable|can ?not|can't|can’t|incapable)\s+(of\s+)?(to\s+)?(directly\s+)?(execut|run|access|connect|interact|log ?in|perform|check|reach)\w*" +
        @"|\bi\s+(don't|don’t|do not)\s+have\s+(the\s+)?(direct\s+)?(ability|access|capabilit\w+|permission)\s+to\s+(execut|run|access|connect|interact|perform)\w*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>True for a reply that claims the model can't run anything or reach the user's systems ("As an AI … I am
    /// unable to execute commands"): small models fall back on that trained-in refusal even with tools in hand.</summary>
    internal static bool ClaimsNoAccess(string content) => content.Length > 0 && NoAccessRx.IsMatch(content);

    /// <summary>The terminal command(s) a code block in an answer really means. A block that is a written call to
    /// terminal_run (any form <see cref="SalvageTextCalls"/> reads) gives its command(s); a call to another tool gives null
    /// (nothing to type into a shell); anything else is returned unchanged. The "Review & run" buttons use this, so a
    /// pseudo-call never reaches the shell as text (s45: "terminal_run --session … --command 'mpstat 1 24'" was typed).</summary>
    public static string? ShellCommandOf(string code, IEnumerable<string> toolNames)
    {
        var first = Regex.Match(code.TrimStart(), @"^[A-Za-z_][A-Za-z0-9_]*").Value;
        if (first.Length == 0 || !toolNames.Contains(first)) return code;
        var defs = new Dictionary<string, JsonObject>(StringComparer.Ordinal)
        {
            ["terminal_run"] = new JsonObject
            {
                ["name"] = "terminal_run",
                ["parameters"] = new JsonObject { ["properties"] = new JsonObject { ["command"] = new JsonObject(), ["purpose"] = new JsonObject() }, ["required"] = new JsonArray("command", "purpose") },
            },
        };
        if (CallsOnly(code.Trim(), defs) is not { Count: > 0 } calls) return null;
        var cmds = calls.Select(c => c?["function"]?["arguments"]?["command"]?.GetValue<string>() ?? "").Where(s => s.Trim().Length > 0).ToList();
        return cmds.Count == calls.Count ? string.Join("\n", cmds) : null;
    }

    /// <summary>A body that is nothing but calls to the given tools (JSON, name(…) or name --key value), or null.</summary>
    private static JsonArray? CallsOnly(string body, Dictionary<string, JsonObject> defs)
    {
        if (body.Length == 0 || body.Length > 4000) return null;
        var calls = new JsonArray();

        // A bare JSON object (or an array of them).
        if (body.StartsWith('{') || body.StartsWith('['))
        {
            var node = SafeParse(body);
            foreach (var o in (node is JsonArray arr ? arr.OfType<JsonObject>() : node is JsonObject one ? new[] { one } : Array.Empty<JsonObject>()))
            {
                if (o["name"] is not JsonValue nv || !nv.TryGetValue<string>(out var name) || !defs.TryGetValue(name, out var def)) return null;
                var given = o["arguments"] as JsonObject ?? (o["arguments"] is JsonValue ? ParseArgs(o["arguments"]) : null) ?? o["parameters"] as JsonObject ?? new JsonObject();
                calls.Add(Call(calls.Count, name, Fit(def, given.Select(kv => (kv.Key, kv.Value?.DeepClone())), Array.Empty<JsonNode?>())));
            }
            return calls.Count > 0 ? calls : null;
        }

        // name(...) calls, one after another: scanned as a whole (not per line), so an argument may be a multi-line script
        // with heredocs, quotes, brackets and backslashes. Anything that isn't a call (prose) means "not just calls".
        int pos = 0;
        while (true)
        {
            while (pos < body.Length && (char.IsWhiteSpace(body[pos]) || body[pos] is ';' or '`')) pos++;
            if (pos >= body.Length) break;
            int nameStart = pos;
            while (pos < body.Length && (char.IsLetterOrDigit(body[pos]) || body[pos] == '_')) pos++;
            var name = body[nameStart..pos];
            if (name.Length == 0 || !defs.TryGetValue(name, out var def)) return null;
            while (pos < body.Length && body[pos] is ' ' or '\t') pos++;
            var named = new List<(string, JsonNode?)>();
            var positional = new List<JsonNode?>();
            // 0.6.20 (DarkCipher0x's screenshot, qwen2.5-14b on Foundry Local wrote the name, then its arguments as JSON on
            // the next lines, with no tags, and nothing ran):
            //   terminal_run
            //   { "command": "free -h", "purpose": "Check the available RAM" }
            int brace = pos;
            while (brace < body.Length && char.IsWhiteSpace(body[brace])) brace++;
            if (brace < body.Length && body[brace] == '{')
            {
                int close = JsonObjectEnd(body, brace);
                if (close < 0) return null;
                JsonObject? o = null;
                o = SafeParse(body[brace..close]) as JsonObject;
                if (o == null) return null;
                // {"name": same, "arguments": {…}} after the name is the same call written twice over.
                var given = o["name"] is JsonValue && (o["arguments"] ?? o["parameters"]) is { } inner
                    ? inner as JsonObject ?? ParseArgs(inner)
                    : o;
                calls.Add(Call(calls.Count, name, Fit(def, given.Select(kv => (kv.Key, kv.Value?.DeepClone())), Array.Empty<JsonNode?>())));
                pos = close;
                continue;
            }
            if (pos < body.Length && body[pos] == '-')
            {
                // Command-line form: name --key value --flag -k 'value' (to the end of the line; "\" continues it).
                var tokens = ShellTokens(body, ref pos);
                for (int i = 0; i < tokens.Count; i++)
                {
                    var tk = tokens[i];
                    // "-- session x": a lone dash pair (0.6.10, phi-4-mini) belongs to the word after it, or "x" would land in
                    // a free parameter (the session id typed as the command).
                    if (!tk.Quoted && tk.Text is "--" or "-" && i + 1 < tokens.Count && !tokens[i + 1].Quoted && !tokens[i + 1].Text.StartsWith('-')) tk = (tk.Text + tokens[++i].Text, false);
                    if (tk.Quoted || !tk.Text.StartsWith('-') || tk.Text.Length < 2) { positional.Add(JsonValue.Create(tk.Text)); continue; }
                    var key = tk.Text.TrimStart('-');
                    var eq = key.IndexOf('=');
                    if (eq > 0) { named.Add((key[..eq].Replace('-', '_'), JsonValue.Create(key[(eq + 1)..]))); continue; }
                    bool hasValue = i + 1 < tokens.Count && (tokens[i + 1].Quoted || !tokens[i + 1].Text.StartsWith('-'));
                    named.Add((key.Replace('-', '_'), hasValue ? JsonValue.Create(tokens[++i].Text) : JsonValue.Create(true)));
                }
                if (named.Count == 0 && positional.Count == 0) return null;
                calls.Add(Call(calls.Count, name, Fit(def, named, positional)));
                continue;
            }
            if (pos >= body.Length || body[pos] != '(') return null;
            if (ScanArgs(body, ref pos) is not { } parts) return null;   // pos ends after the closing ')'
            foreach (var p in parts)
            {
                var kv = Regex.Match(p, @"^([A-Za-z_][A-Za-z0-9_]*)\s*[=:]\s*(.+)$", RegexOptions.Singleline);
                if (kv.Success) named.Add((kv.Groups[1].Value, Literal(kv.Groups[2].Value)));
                else positional.Add(Literal(p));
            }
            calls.Add(Call(calls.Count, name, Fit(def, named, positional)));
        }
        return calls.Count > 0 ? calls : null;

        static JsonObject Call(int i, string name, JsonObject args) =>
            new() { ["id"] = "text_" + i, ["function"] = new JsonObject { ["name"] = name, ["arguments"] = args } };
    }

    /// <summary>The tool's own parameters from what the model wrote: known names kept, unknown dropped, positional values
    /// into the remaining parameters (required ones first, in schema order).</summary>
    private static JsonObject Fit(JsonObject def, IEnumerable<(string Key, JsonNode? Value)> named, IReadOnlyList<JsonNode?> positional)
    {
        var props = def["parameters"]?["properties"] as JsonObject;
        var known = props?.Select(p => p.Key).ToList() ?? new List<string>();
        var required = (def["parameters"]?["required"] as JsonArray)?.Select(x => x?.GetValue<string>()).OfType<string>().ToList() ?? new List<string>();
        var args = new JsonObject();
        foreach (var (k, v) in named) if (known.Contains(k)) args[k] = v;
        var free = required.Concat(known.Except(required)).Where(k => !args.ContainsKey(k)).ToList();
        for (int i = 0; i < positional.Count && i < free.Count; i++) args[free[i]] = positional[i];
        return args;
    }

    /// <summary>From the '(' at <paramref name="pos"/> to its matching ')': the arguments split at top-level commas, with
    /// strings ("…", '…', """…""", '''…''', r"…", `…`) and brackets respected across lines. Null when it never closes.</summary>
    private static List<string>? ScanArgs(string s, ref int pos)
    {
        var parts = new List<string>();
        int start = ++pos, depth = 0;
        while (pos < s.Length)
        {
            char c = s[pos];
            if (StringEnd(s, pos) is { } end) { pos = end; continue; }
            if (c is '(' or '[' or '{') depth++;
            else if (c is ']' or '}') depth--;
            else if (c == ')')
            {
                if (depth == 0)
                {
                    var last = s[start..pos].Trim();
                    if (last.Length > 0 || parts.Count > 0) parts.Add(last);
                    pos++;
                    return parts;
                }
                depth--;
            }
            else if (c == ',' && depth == 0) { parts.Add(s[start..pos].Trim()); start = pos + 1; }
            pos++;
        }
        return null;
    }

    /// <summary>Shell-style words from <paramref name="pos"/> to the end of the line (a trailing "\" joins the next line):
    /// '…' literal, "…" with \" \\ \$ \` undone, unquoted words split at blanks. Quoted words are marked (never a flag).</summary>
    private static List<(string Text, bool Quoted)> ShellTokens(string s, ref int pos)
    {
        var list = new List<(string, bool)>();
        var cur = new StringBuilder();
        bool inWord = false, quoted = false;
        void End() { if (inWord) list.Add((cur.ToString(), quoted)); cur.Clear(); inWord = quoted = false; }
        while (pos < s.Length)
        {
            char c = s[pos];
            if (c == '\\' && pos + 1 < s.Length && s[pos + 1] is '\n' or '\r') { pos += s[pos + 1] == '\r' && pos + 2 < s.Length && s[pos + 2] == '\n' ? 3 : 2; continue; }
            if (c is '\n' or '\r') break;
            if (c is ' ' or '\t') { End(); pos++; continue; }
            if (c == '\'')
            {
                int close = s.IndexOf('\'', pos + 1);
                if (close < 0) close = s.Length;
                cur.Append(s, pos + 1, close - pos - 1); inWord = quoted = true; pos = Math.Min(close + 1, s.Length); continue;
            }
            if (c == '"')
            {
                pos++;
                while (pos < s.Length && s[pos] != '"')
                {
                    if (s[pos] == '\\' && pos + 1 < s.Length && s[pos + 1] is '"' or '\\' or '$' or '`') { cur.Append(s[pos + 1]); pos += 2; continue; }
                    if (s[pos] == '\\' && pos + 1 < s.Length && s[pos + 1] is '\n' or '\r') { pos += s[pos + 1] == '\r' && pos + 2 < s.Length && s[pos + 2] == '\n' ? 3 : 2; continue; }   // as bash: joined
                    cur.Append(s[pos++]);
                }
                pos = Math.Min(pos + 1, s.Length); inWord = quoted = true; continue;
            }
            cur.Append(c); inWord = true; pos++;
        }
        End();
        return list;
    }

    /// <summary>When a string literal starts at <paramref name="i"/>: the index just after it (else null).</summary>
    private static int? StringEnd(string s, int i)
    {
        bool raw = false;
        int q = i;
        if (s[q] is 'r' or 'R' && q + 1 < s.Length && s[q + 1] is '"' or '\'' && (q == 0 || !char.IsLetterOrDigit(s[q - 1]))) { raw = true; q++; }
        if (s[q] is not ('"' or '\'' or '`')) return null;
        char quote = s[q];
        string close = q + 2 < s.Length && s[q + 1] == quote && s[q + 2] == quote ? new string(quote, 3) : quote.ToString();
        int j = q + close.Length;
        while (j < s.Length)
        {
            if (!raw && close.Length == 1 && s[j] == '\\' && j + 1 < s.Length) { j += 2; continue; }
            if (string.CompareOrdinal(s, j, close, 0, close.Length) == 0) return j + close.Length;
            j++;
        }
        return s.Length;   // unterminated: the caller then finds no ')' and gives up
    }

    /// <summary>A written value: a string literal (only \\ \" \' \n \t \r undone, so a script's other backslashes — \. \$ \d
    /// — arrive exactly as written; raw and triple-quoted strings untouched), true / false / a number, JSON, or the bare
    /// word as a string.</summary>
    private static JsonNode? Literal(string v)
    {
        v = v.Trim();
        bool raw = v.Length > 2 && v[0] is 'r' or 'R' && v[1] is '"' or '\'';
        if (raw) v = v[1..];
        foreach (var q in new[] { "\"\"\"", "'''", "\"", "'", "`" })
            if (v.Length >= 2 * q.Length && v.StartsWith(q, StringComparison.Ordinal) && v.EndsWith(q, StringComparison.Ordinal))
            {
                var inner = v[q.Length..^q.Length];
                if (q.Length == 3) return JsonValue.Create(inner.StartsWith('\n') ? inner[1..] : inner);
                return JsonValue.Create(raw || q == "`" ? inner : Unescape(inner));
            }
        if (v is "true" or "True") return JsonValue.Create(true);
        if (v is "false" or "False") return JsonValue.Create(false);
        if (long.TryParse(v, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n)) return JsonValue.Create(n);
        if (v.StartsWith('{') || v.StartsWith('[')) { if (SafeParse(v) is { } node) return node; }
        return JsonValue.Create(v);
    }

    private static string Unescape(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                char e = s[i + 1];
                string? r = e switch { '\\' => "\\", '"' => "\"", '\'' => "'", 'n' => "\n", 't' => "\t", 'r' => "\r", _ => null };
                if (r != null) { sb.Append(r); i++; continue; }
            }
            sb.Append(s[i]);
        }
        return sb.ToString();
    }

    /// <summary>Streams text to the user but holds back anything from "&lt;tool_call" on, so the protocol isn't shown.</summary>
    private sealed class ToolTextFilter(Action<string>? onText)
    {
        private readonly StringBuilder _buf = new();
        private bool _stopped;
        private const string Tag = "<tool_call";
        public void Push(string s)
        {
            if (onText == null || _stopped) return;
            _buf.Append(s);
            var all = _buf.ToString();
            int at = all.IndexOf(Tag, StringComparison.Ordinal);
            if (at >= 0) { if (at > 0) onText(all[..at]); _stopped = true; _buf.Clear(); return; }
            // Keep a tail that could be the start of the tag.
            int keep = Math.Min(Tag.Length - 1, all.Length);
            var send = all[..^keep];
            if (send.Length > 0) { onText(send); _buf.Remove(0, send.Length); }
        }
    }
}
