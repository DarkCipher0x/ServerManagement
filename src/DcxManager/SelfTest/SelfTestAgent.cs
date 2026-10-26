using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Threading;
using DcxManager.Ai;
using DcxManager.Services;
using DcxManager.Views;
using DcxManager.Sessions;

namespace DcxManager;

/// <summary>
/// ADR-034: AI speed, agents and direct terminal control, all against <see cref="FakeAi"/> (a loopback server that speaks
/// Ollama /api/chat, the OpenAI-compatible /v1/chat/completions, Anthropic /v1/messages and Foundry Local's /v1, streamed or
/// whole, with tool calls) and a real local PowerShell session. Measures time-to-first-token and total time before
/// (whole answers, tools one at a time, timer-only terminal echo) and after (streaming, parallel read-only tools, quick
/// echo flush) and writes them to ai-timings.txt next to the report. No window, no dialog, no real model.
/// </summary>
public static class AgentTests
{
    public static string Timings { get; private set; } = "";

    public static async Task RunAsync(MainWindow main, string reportDir, Action<string> ok, Action<string, Exception?> fail, Action<Window, string> snap)
    {
        async Task Settle(int ms = 200)
        {
            await Task.Delay(ms);
            await Application.Current.Dispatcher.InvokeAsync(() => main.UpdateLayout(), DispatcherPriority.ApplicationIdle);
        }
        var shots = Directory.CreateDirectory(Path.Combine(reportDir, "shots")).FullName;
        var s = AppSettings.Current;
        var saved = (s.AiEnabled, s.AiEngine, s.OllamaUrl, s.OllamaModel, s.OllamaTunnelHostId, s.OpenAiUrl, s.OpenAiModel, s.OpenAiKey, s.AnthropicUrl, s.AnthropicModel, s.AnthropicKey,
            s.FoundryUrl, s.FoundryModel, s.AiContext, s.AiStream, s.AiMaxSteps, s.AiKeepWarmMinutes, s.AiResourceGuard, s.AiUnloadIdleFoundry, s.AiAutoRunReadOnlyTarget,
            s.AiAutoApproveReadOnly, s.AiFeatureOperator, s.AiFeatureHelper, s.AiTerminalBridge, s.AiShareCommandOutput, s.AiAllowNotes, s.AiCommandTimeoutMinutes);
        var memWas = AiResourceGuard.Memory;
        var spillWas = AiResourceGuard.OllamaSpill;
        var foundryWas = AiResourceGuard.Foundry;
        var confirmWas = TerminalBridge.Confirm;
        var report = new StringBuilder($"AI timings — fake engine (150 ms to the first token, then 15 ms per token, 40-token answers) · {DateTime.Now:yyyy-MM-dd HH:mm}\n");
        using var fake = FakeAi.Start();
        string Base() => $"http://127.0.0.1:{fake.Port}";
        void Engine(string e)
        {
            s.AiEnabled = true; s.AiEngine = e; s.AiStream = true; s.AiFeatureOperator = true; s.AiFeatureHelper = true;
            s.OllamaUrl = Base(); s.OllamaModel = "fake"; s.OllamaTunnelHostId = null;
            s.OpenAiUrl = Base() + "/v1"; s.OpenAiModel = "fake"; s.OpenAiKey = "";
            s.AnthropicUrl = Base(); s.AnthropicModel = "fake"; s.AnthropicKey = "";
            s.FoundryUrl = Base(); s.FoundryModel = "fake";
            AiClient.ForgetFoundry(); AiClient.ForgetToolSupport();
        }
        AiResourceGuard.Memory = () => (10, 100, 20);
        AiResourceGuard.OllamaSpill = _ => Task.FromResult<long?>(null);
        static JsonArray Q(string q) => new() { new JsonObject { ["role"] = "system", ["content"] = "test" }, new JsonObject { ["role"] = "user", ["content"] = q } };
        static JsonArray ToolDefs(params string[] names) => new(names.Select(n => (JsonNode)new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject { ["name"] = n, ["description"] = "test tool " + n, ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["host"] = new JsonObject { ["type"] = "string", ["description"] = "h" } }, ["required"] = new JsonArray() } },
        }).ToArray());

        try
        {
            // 1. Streaming on every engine, with tool calls put together from streamed fragments.
            try
            {
                var done = new List<string>();
                foreach (var e in new[] { "ollama-gpu", "openai", "anthropic", "npu" })
                {
                    Engine(e);
                    fake.Enqueue(new FakeAi.Reply("Checking the two things you asked about now, one moment please.", ("list_hosts", new JsonObject()), ("read_logs", new JsonObject { ["host"] = "web-01", ["lines"] = 50 })));
                    var deltas = new List<string>();
                    var msg = await AiClient.ChatAsync(Q("stream test"), ToolDefs("list_hosts", "read_logs"), null, CancellationToken.None, d => { lock (deltas) deltas.Add(d); });
                    var content = msg["content"]?.GetValue<string>() ?? "";
                    var calls = msg["tool_calls"] as JsonArray;
                    var body = fake.LastChat ?? throw new Exception(e + ": no request");
                    if (deltas.Count < 5 || string.Concat(deltas) != content || !content.StartsWith("Checking")) throw new Exception($"{e}: {deltas.Count} deltas, content [{content}]");
                    if (calls is not { Count: 2 } || calls[1]!["function"]!["name"]!.GetValue<string>() != "read_logs" || calls[1]!["function"]!["arguments"]!["host"]!.GetValue<string>() != "web-01" || calls[1]!["function"]!["arguments"]!["lines"]!.GetValue<int>() != 50)
                        throw new Exception($"{e}: tool calls {calls?.ToJsonString()}");
                    if (body["stream"]?.GetValue<bool>() != true) throw new Exception(e + ": not asked to stream");
                    if (e == "ollama-gpu" && body["keep_alive"]?.GetValue<string>() != "10m") throw new Exception("keep_alive " + body["keep_alive"]);
                    if (AiClient.LastTiming is not { Streamed: true } t || t.FirstToken >= t.Total) throw new Exception(e + ": timing " + AiClient.LastTiming);
                    done.Add($"{e} ({deltas.Count} pieces)");
                }
                ok("AI streaming: Ollama NDJSON, OpenAI-compatible SSE, Anthropic SSE and Foundry Local's /v1 stream the answer piece by piece (the pieces add up to the answer), tool calls put together from streamed fragments (arguments split across chunks), Ollama asked with keep_alive — " + string.Join(", ", done));
            }
            catch (Exception ex) { fail("AI streaming on every engine", ex); }

            // 1b. 0.6.29 (DarkCipher0x's screenshot: "Anthropic API: `temperature` is deprecated for this model"): a server
            // that refuses a sampling setting is asked again without it, streamed and whole, on every engine.
            try
            {
                var done = new List<string>();
                fake.RefuseTemperature = true;
                foreach (var e in new[] { "ollama-gpu", "openai", "anthropic", "npu" })
                    foreach (var streamed in new[] { true, false })
                    {
                        Engine(e);
                        s.AiStream = streamed;
                        int before = fake.Chats.Count;
                        var msg = await AiClient.ChatAsync(Q("temperature test"), null, null, CancellationToken.None, streamed ? _ => { } : null);
                        var content = msg["content"]?.GetValue<string>() ?? "";
                        if (content.Length == 0) throw new Exception($"{e} ({(streamed ? "streamed" : "whole")}): no answer");
                        if (fake.LastChat?["temperature"] != null)   // Ollama's own API keeps it in "options", which Ollama accepts
                            throw new Exception($"{e}: the answered request still had a temperature");
                        done.Add($"{e}{(streamed ? "" : " whole")} {fake.Chats.Count - before} request(s)");
                    }
                if (AiClient.RefusedSampling("Anthropic API: `temperature` is deprecated for this model.", new JsonObject { ["temperature"] = 0.2, ["top_p"] = 1 }) is not ["temperature"])
                    throw new Exception("RefusedSampling picked the wrong setting");
                if (AiClient.RefusedSampling("Anthropic API: max_tokens too large", new JsonObject { ["temperature"] = 0.2 }).Length != 0)
                    throw new Exception("RefusedSampling retried an unrelated error");
                ok("AI sampling settings: a server refusing `temperature` (as the Claude API does for current models) is asked again without it, streamed and whole, on every engine; unrelated errors aren't retried — " + string.Join(", ", done));
            }
            catch (Exception ex) { fail("AI sampling settings refused", ex); }
            finally { fake.RefuseTemperature = false; s.AiStream = true; }

            // 2. Before / after: whole answers vs streaming; connections reused.
            try
            {
                Engine("ollama-gpu");
                var before = new List<(double First, double Total)>();
                var after = new List<(double First, double Total)>();
                int connects0 = fake.Connections;
                for (int i = 0; i < 3; i++)
                {
                    var sw = Stopwatch.StartNew();
                    await AiClient.ChatAsync(Q("timing"), null, null, CancellationToken.None);   // before: nothing shown until the whole answer is in
                    before.Add((sw.Elapsed.TotalMilliseconds, sw.Elapsed.TotalMilliseconds));
                }
                for (int i = 0; i < 3; i++)
                {
                    await AiClient.ChatAsync(Q("timing"), null, null, CancellationToken.None, _ => { });
                    var t = AiClient.LastTiming!;
                    after.Add((t.FirstToken.TotalMilliseconds, t.Total.TotalMilliseconds));
                }
                int connects = fake.Connections - connects0;
                double Med(IEnumerable<double> x) { var l = x.OrderBy(v => v).ToList(); return l[l.Count / 2]; }
                double bF = Med(before.Select(x => x.First)), bT = Med(before.Select(x => x.Total)), aF = Med(after.Select(x => x.First)), aT = Med(after.Select(x => x.Total));
                report.AppendLine($"Answer (Ollama path): first text {bF:0} ms → {aF:0} ms; whole answer {bT:0} ms → {aT:0} ms; {connects} new TCP connection(s) for 6 questions (the pooled one from earlier calls is reused)");
                if (aF > bF / 2 || connects > 2) throw new Exception($"first token {bF:0} → {aF:0} ms, {connects} connections");
                ok($"AI speed: time to first text {bF:0} ms → {aF:0} ms (streaming), whole answer {bT:0} → {aT:0} ms, 6 questions opened {connects} new connection(s) (pooled connection reused)");
            }
            catch (Exception ex) { fail("AI speed before/after", ex); }

            // 3. The agent loop: plan → three tools → observe → answer; independent read-only tools at the same time.
            try
            {
                Engine("ollama-gpu");
                async Task<(AgentRun Run, List<AgentStep> Steps, JsonArray Msgs)> Agent(bool parallel)
                {
                    fake.Enqueue(new FakeAi.Reply("I'll look at three things.", ("slow_a", new JsonObject()), ("slow_b", new JsonObject()), ("slow_c", new JsonObject { ["host"] = "x" })));
                    fake.Enqueue(new FakeAi.Reply("All three look fine."));
                    var steps = new List<AgentStep>();
                    var msgs = Q("agent");
                    var hooks = new AgentHooks
                    {
                        RunTool = async (n, a, ct) => { await Task.Delay(300, ct); return $"{n} result"; },
                        ParallelSafe = (_, _) => parallel,
                        Step = st => { lock (steps) steps.Add(st); },
                        Delta = _ => { },
                    };
                    var (answer, run) = await AiAgent.RunAsync(msgs, ToolDefs("slow_a", "slow_b", "slow_c"), hooks, 12, CancellationToken.None);
                    if (answer != "All three look fine.") throw new Exception("answer: " + answer);
                    return (run, steps, msgs);
                }
                var seq = await Agent(false);
                var par = await Agent(true);
                var toolMsgs = par.Msgs.OfType<JsonObject>().Where(m => m["role"]?.GetValue<string>() == "tool").ToList();
                if (toolMsgs.Count != 3 || toolMsgs[0]["content"]!.GetValue<string>() != "slow_a result" || toolMsgs[2]["tool_call_id"]!.GetValue<string>() != par.Msgs.OfType<JsonObject>().First(m => m["tool_calls"] != null)["tool_calls"]![2]!["id"]!.GetValue<string>())
                    throw new Exception("tool results not in order / ids");
                if (par.Steps.Count(x => x.Kind == "tool") != 3 || par.Steps.Count(x => x.Kind == "result") != 3 || !par.Steps.Any(x => x.Kind == "plan") || par.Steps.Last().Kind != "answer") throw new Exception("step log: " + string.Join(" | ", par.Steps));
                if (par.Run.Steps != 2 || par.Run.ToolCalls != 3 || !par.Run.NativeTools) throw new Exception("run: " + par.Run);
                report.AppendLine($"Agent, 3 independent read-only tools of 300 ms: {seq.Run.Total.TotalMilliseconds:0} ms one at a time → {par.Run.Total.TotalMilliseconds:0} ms together");
                if (par.Run.Total.TotalMilliseconds > seq.Run.Total.TotalMilliseconds - 400) throw new Exception($"not parallel: {seq.Run.Total.TotalMilliseconds:0} vs {par.Run.Total.TotalMilliseconds:0} ms");
                ok($"agent loop: plan → 3 tool calls → results back in order with their ids → answer, visible step log (plan, tool, result, answer); independent read-only tools together: {seq.Run.Total.TotalMilliseconds:0} ms → {par.Run.Total.TotalMilliseconds:0} ms");
            }
            catch (Exception ex) { fail("agent loop", ex); }

            // 4. Step limit (then a summary without tools), repeated-call guard, cancel.
            try
            {
                Engine("ollama-gpu");
                fake.Always = new FakeAi.Reply("again", ("slow_a", new JsonObject()));
                int ran = 0;
                var hooks = new AgentHooks { RunTool = (_, _, _) => { Interlocked.Increment(ref ran); return Task.FromResult("same"); } };
                var (answer, run) = await AiAgent.RunAsync(Q("loop"), ToolDefs("slow_a"), hooks, 5, CancellationToken.None);
                fake.Always = null;
                if (!run.HitLimit || !answer.Contains("Stopped after 5 steps") || ran != 3 || fake.LastChat?["tools"] != null) throw new Exception($"limit: hit {run.HitLimit}, ran {ran}, last had tools {fake.LastChat?["tools"] != null}: {answer}");
                fake.TokenMs = 60;
                using var cts = new CancellationTokenSource(400);
                Exception? caught = null;
                try { await AiAgent.RunAsync(Q("cancel"), ToolDefs("slow_a"), new AgentHooks { RunTool = (_, _, _) => Task.FromResult("") }, 5, cts.Token); }
                catch (Exception ex) { caught = ex; }
                fake.TokenMs = 15;
                if (caught is not OperationCanceledException) throw new Exception("cancel: " + caught);
                ok("agent limits: stops at the step limit with a final summary call that offers no tools, the same call is refused after 3 repeats, Stop cancels a running question");
            }
            catch (Exception ex) { fake.Always = null; fake.TokenMs = 15; fail("agent limits", ex); }

            // 5. Models without native tool calling: detected (Ollama capabilities) or refused (HTTP 400) → text tool protocol.
            try
            {
                Engine("ollama-gpu");
                fake.Capabilities = new[] { "completion" };
                fake.Enqueue(new FakeAi.Reply("Let me look. <tool_call>{\"name\": \"slow_a\", \"arguments\": {\"host\": \"db-1\"}}</tool_call>"));
                fake.Enqueue(new FakeAi.Reply("db-1 is fine."));
                var got = new List<string>();
                var streamed = new StringBuilder();
                var (a1, r1) = await AiAgent.RunAsync(Q("text"), ToolDefs("slow_a"), new AgentHooks { RunTool = (n, a, _) => { got.Add(n + ":" + a["host"]); return Task.FromResult("ok"); }, Delta = d => { lock (streamed) streamed.Append(d); } }, 6, CancellationToken.None);
                fake.Capabilities = new[] { "completion", "tools" };
                var firstBody = fake.Chats[^2];
                if (r1.NativeTools || a1 != "db-1 is fine." || got.SingleOrDefault() != "slow_a:db-1" || firstBody["tools"] != null || !firstBody["messages"]!.ToJsonString().Contains("tool_call") || streamed.ToString().Contains("<tool_call"))
                    throw new Exception($"capability fallback: native {r1.NativeTools}, ran [{string.Join(",", got)}], streamed [{streamed}]");
                Engine("openai");
                fake.NextError = (400, "this model does not support tools");
                fake.Enqueue(new FakeAi.Reply("Plain answer."));
                var (a2, r2) = await AiAgent.RunAsync(Q("refused"), ToolDefs("slow_a"), new AgentHooks { RunTool = (_, _, _) => Task.FromResult("") }, 4, CancellationToken.None);
                if (a2 != "Plain answer." || r2.NativeTools || fake.LastChat?["tools"] != null) throw new Exception("refusal fallback: " + a2);
                ok("models without tool calling: Ollama capabilities without \"tools\" and an OpenAI-compatible server answering 400 \"does not support tools\" both fall back to the text tool protocol (tools described in the prompt, <tool_call> parsed, the protocol never streamed to the user)");

                // 0.3.0 (s45, DarkCipher0x's screenshot): phi-4-mini on Foundry Local takes the tools but writes the call as text,
                // terminal_run(target=7919c0e2, "df -h"). A reply that is only calls to offered tools is run; a mention isn't.
                Engine("npu");
                var termTool = new JsonArray(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = "terminal_run", ["description"] = "run",
                        ["parameters"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject { ["command"] = new JsonObject { ["type"] = "string" }, ["purpose"] = new JsonObject { ["type"] = "string" } },
                            ["required"] = new JsonArray("command", "purpose"),
                        },
                    },
                });
                var ran = new List<JsonObject>();
                var steps5 = new List<AgentStep>();
                AgentHooks H() => new() { RunTool = (n, a, _) => { lock (ran) ran.Add(a); return Task.FromResult("Filesystem  Size  Used\n/dev/vda1  23G  6.5G"); }, Step = st => { lock (steps5) steps5.Add(st); } };
                fake.Enqueue(new FakeAi.Reply("terminal_run(target=7919c0e2, \"df -h\")"));
                fake.Enqueue(new FakeAi.Reply("/ is 28% used (6.5 of 23 GB)."));
                var (a3, r3) = await AiAgent.RunAsync(Q("check the storage space"), termTool, H(), 6, CancellationToken.None);
                if (ran.Count != 1 || ran[0]["command"]?.GetValue<string>() != "df -h" || ran[0].ContainsKey("target") || r3.ToolCalls != 1 || !a3.Contains("28%")
                    || !steps5.Any(x => x.Kind == "note" && x.Text.Contains("wrote its tool call as text")) || !fake.Chats[^1]["messages"]!.ToJsonString().Contains("tool_result"))
                    throw new Exception($"text call not run: ran [{string.Join(" | ", ran.Select(x => x.ToJsonString()))}], answer [{a3}], run {r3}");
                // A bare JSON call, and command= named with a Windows path in quotes.
                ran.Clear();
                fake.Enqueue(new FakeAi.Reply("```json\n{\"name\": \"terminal_run\", \"arguments\": {\"command\": \"Get-ChildItem C:\\\\temp\", \"purpose\": \"list\"}}\n```"));
                fake.Enqueue(new FakeAi.Reply("Done."));
                await AiAgent.RunAsync(Q("json"), termTool, H(), 6, CancellationToken.None);
                if (ran.Count != 1 || ran[0]["command"]?.GetValue<string>() != "Get-ChildItem C:\\temp") throw new Exception("bare JSON call: " + string.Join(" | ", ran.Select(x => x.ToJsonString())));
                // Complicated scripts (DarkCipher0x, s45): multi-line arguments with heredocs, brackets, commas and quotes inside,
                // triple-quoted and raw strings, backslashes that are part of the script kept exactly.
                var script1 = "cat <<'EOF' > /etc/app.conf\n[main]\nhosts = (a, b), \"c\"\nEOF\ngrep -E '^host\\.(a|b)$' /etc/app.conf | awk '{print $1}'";
                var cases = new (string Reply, string Expect)[]
                {
                    ("terminal_run(command=\"\"\"\n" + script1 + "\n\"\"\", purpose=\"write config, check\")", script1 + "\n"),
                    ("```python\nterminal_run(r\"find / -name '*.log' -exec grep -l \\d{3} {} \\;\", \"find logs\")\n```", "find / -name '*.log' -exec grep -l \\d{3} {} \\;"),
                    ("terminal_run(\"for f in /var/log/*.log; do echo \\\"$f: $(wc -l < \\\"$f\\\")\\\"; done\", purpose='count lines')", "for f in /var/log/*.log; do echo \"$f: $(wc -l < \"$f\")\"; done"),
                    ("terminal_run('echo \"a, b\"; printf \"%s\\n\" x', 'p')\nterminal_run(\"uptime\", \"load\")", "echo \"a, b\"; printf \"%s\n\" x"),
                };
                foreach (var (reply, expect) in cases)
                {
                    var parsedCalls = AiAgent.SalvageTextCalls(reply, termTool) ?? throw new Exception("not parsed: " + reply);
                    var got1 = parsedCalls[0]!["function"]!["arguments"]!["command"]?.GetValue<string>();
                    if (got1 != expect) throw new Exception($"script mangled:\n[{got1}]\nexpected\n[{expect}]");
                }
                if (AiAgent.SalvageTextCalls("terminal_run('echo \"a, b\"; printf \"%s\\n\" x', 'p')\nterminal_run(\"uptime\", \"load\")", termTool) is not { Count: 2 } two || two[1]!["function"]!["arguments"]!["purpose"]?.GetValue<string>() != "load")
                    throw new Exception("two calls in one reply");
                if (AiAgent.SalvageTextCalls("terminal_run(\"never closed", termTool) != null || AiAgent.SalvageTextCalls("Sure! terminal_run(\"df -h\")", termTool) != null) throw new Exception("broken or prose-led calls accepted");
                // A call only mentioned in an explanation is NOT run; nor a tool that wasn't offered.
                ran.Clear();
                for (int i = 0; i < 3; i++) fake.Enqueue(new FakeAi.Reply("You could check it yourself with terminal_run(\"df -h\") if you like."));   // 0.6.12: asked twice to make the call; it keeps only mentioning it
                var (a4, r4) = await AiAgent.RunAsync(Q("mention"), termTool, H(), 6, CancellationToken.None);
                fake.Enqueue(new FakeAi.Reply("delete_everything(\"now\")"));
                var (a5, _) = await AiAgent.RunAsync(Q("unknown"), termTool, H(), 6, CancellationToken.None);
                if (ran.Count != 0 || r4.ToolCalls != 0 || !a4.StartsWith("You could check") || a5 != "delete_everything(\"now\")") throw new Exception("a mention or an unknown tool ran: " + string.Join(" | ", ran.Select(x => x.ToJsonString())));
                // DarkCipher0x's second transcript (0.3.0-preview.3): a native call with keys and no command, then the call written as a
                // command line inside prose. The first gets an error naming what terminal_run takes; the second runs.
                ran.Clear();
                fake.Enqueue(new FakeAi.Reply("", ("terminal_run", new JsonObject { ["keys"] = "{ENTER}", ["purpose"] = "Check CPU load over the past day using sar (sysstat)." })));
                fake.Enqueue(new FakeAi.Reply("The command to check the CPU load would be mpstat. I will execute it in the target session.\n\nCommand executed:\n```\nterminal_run --session vps-test-01 --command 'mpstat 1 24'\n```\n\nThe results will provide a detailed report."));
                fake.Enqueue(new FakeAi.Reply("CPU load has been low (under 1%)."));
                var (a6, r6) = await AiAgent.RunAsync(Q("cpu over 24h"), termTool, H(), 6, CancellationToken.None);
                var sent = fake.Chats[^1]["messages"]!.ToJsonString();
                if (!sent.Contains("needs \\u0022command\\u0022") && !sent.Contains("needs \\\"command\\\"")) throw new Exception("missing-command error not sent back: " + sent[^Math.Min(600, sent.Length)..]);
                if (ran.Count != 1 || ran[0]["command"]?.GetValue<string>() != "mpstat 1 24" || ran[0].ContainsKey("session") || !a6.Contains("under 1%"))
                    throw new Exception($"command-line call in prose not run: [{string.Join(" | ", ran.Select(x => x.ToJsonString()))}] answer [{a6}]");
                var cli = AiAgent.SalvageTextCalls("terminal_run --command \"echo \\\"hi\\\" && \\\n uname -a\" --purpose=check", termTool);
                if (cli?[0]?["function"]?["arguments"]?["command"]?.GetValue<string>() != "echo \"hi\" &&  uname -a" || cli[0]!["function"]!["arguments"]!["purpose"]?.GetValue<string>() != "check")
                    throw new Exception("command-line form: " + cli?.ToJsonString());
                // The Review & run buttons: a pseudo-call block becomes its command; another tool's call no button; plain stays.
                var names = new[] { "terminal_run", "terminal_send_keys" };
                if (AiAgent.ShellCommandOf("terminal_run --session vps-test-01 --command 'mpstat 1 24'", names) != "mpstat 1 24"
                    || AiAgent.ShellCommandOf("terminal_run(command=\"df -h\", purpose=\"disk\")", names) != "df -h"
                    || AiAgent.ShellCommandOf("terminal_send_keys --keys q", names) != null
                    || AiAgent.ShellCommandOf("sar -u -f /var/log/sysstat/sa$(date +%d)", names) != "sar -u -f /var/log/sysstat/sa$(date +%d)")
                    throw new Exception("Review & run unwrapping");
                // 0.6.10 (DarkCipher0x's screenshot, phi-4-mini on Foundry Local): the name written twice, "-- key" with a space,
                // bare tool names as headings and made-up "[No response …]" results; nothing ran. Now: the calls run (unknown
                // session / display dropped), the one without a command gets the error naming "command", the model's next
                // call runs; a reply with prose around such lines still doesn't; a reply that only talks about calls is flagged.
                var tools2 = termTool.DeepClone().AsArray();
                tools2.Add(new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = "terminal_read_screen", ["description"] = "read", ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() } } });
                const string phi = "terminal_run terminal_run -- session 95cad8f2 -- display top -- display load1.0\n\n[No response from the target terminal session 95cad8f2]\n\nterminal_read_screen\nterminal_read_screen -- session 95cad8f2 -- display load1.0\n\n[No response from the target terminal session 95cad8f2]";
                var loose = AiAgent.SalvageTextCalls(phi, tools2) ?? throw new Exception("phi-4-mini's loose calls not read");
                if (loose.Count != 2 || loose[0]!["function"]!["name"]!.GetValue<string>() != "terminal_run" || loose[1]!["function"]!["name"]!.GetValue<string>() != "terminal_read_screen"
                    || loose[0]!["function"]!["arguments"]!.AsObject().Count != 0 || loose[1]!["function"]!["arguments"]!.AsObject().Count != 0)
                    throw new Exception("loose calls: " + loose.ToJsonString());
                if (AiAgent.SalvageTextCalls("I ran it for you.\nterminal_run --command uptime\n[ok]", tools2) != null) throw new Exception("prose around loose calls accepted");
                // A lone "--" joins the word after it: the session id must never become the command.
                if (AiAgent.SalvageTextCalls("terminal_run -- session 95cad8f2 -- display top", tools2) is not { Count: 1 } lone || lone[0]!["function"]!["arguments"]!.AsObject().Count != 0)
                    throw new Exception("\"-- session x\" filled a parameter: " + AiAgent.SalvageTextCalls("terminal_run -- session 95cad8f2 -- display top", tools2)?.ToJsonString());
                ran.Clear(); steps5.Clear();
                fake.Enqueue(new FakeAi.Reply(phi));
                fake.Enqueue(new FakeAi.Reply("terminal_run -- command \"uptime\" -- purpose load"));
                fake.Enqueue(new FakeAi.Reply("Load average 0.25: the server is idle."));
                var (a7, r7) = await AiAgent.RunAsync(Q("check the server cpu load"), tools2, H(), 6, CancellationToken.None);
                var sent7 = fake.Chats[^1]["messages"]!.ToJsonString();
                if (ran.Count != 2 || ran[0].Count != 0 || ran[1]["command"]?.GetValue<string>() != "uptime" || !a7.Contains("0.25") || a7.Contains("No response")
                    || (!sent7.Contains("needs \\u0022command\\u0022") && !sent7.Contains("needs \\\"command\\\"")))
                    throw new Exception($"phi-4-mini transcript: ran [{string.Join(" | ", ran.Select(x => x.ToJsonString()))}], answer [{a7}], run {r7}");
                ran.Clear(); steps5.Clear();
                for (int i = 0; i < 3; i++) fake.Enqueue(new FakeAi.Reply("I ran terminal_run -- command uptime and the load is fine.\n[No response from the target terminal]"));   // the reply, then two corrections
                var (_, r8) = await AiAgent.RunAsync(Q("talk only"), tools2, H(), 6, CancellationToken.None);
                if (ran.Count != 0 || r8.ToolCalls != 0 || !steps5.Any(x => x.Kind == "note" && x.Text.Contains("made none, so nothing ran")))
                    throw new Exception("a reply that only talks about calls wasn't flagged: " + string.Join(" | ", steps5.Select(x => x.Text)));
                // 0.6.20 (DarkCipher0x's screenshot, qwen2.5-14b): the name, then its arguments as JSON on the next lines, no tags.
                var qwen = AiAgent.SalvageTextCalls("terminal_run\n{\n  \"command\": \"free -h\",\n  \"purpose\": \"Check the available RAM on the system.\"\n}", tools2);
                if (qwen is not { Count: 1 } || qwen[0]!["function"]!["arguments"]!["command"]?.GetValue<string>() != "free -h")
                    throw new Exception("qwen's name-then-JSON call not read: " + qwen?.ToJsonString());
                if (AiAgent.SalvageTextCalls("```\nterminal_run {\"name\": \"terminal_run\", \"arguments\": {\"command\": \"uptime\"}}\n```", tools2) is not { Count: 1 } q2 || q2[0]!["function"]!["arguments"]!["command"]?.GetValue<string>() != "uptime"
                    || AiAgent.SalvageTextCalls("Run this:\nterminal_run\n{\"command\": \"df\"}", tools2) != null || !AiAgent.MentionsToolUse("terminal_run\n{\"command\": \"df\"}", tools2))
                    throw new Exception("name-then-JSON variants");
                // A repeated key (found by the fuzzer: it threw later, outside the parser) is simply not a call.
                foreach (var dup in new[] { "terminal_run\n{\"command\": \"a\", \"command\": \"b\"}", "{\"name\": \"terminal_run\", \"arguments\": {\"command\": \"a\", \"command\": \"b\"}}", "<tool_call>{\"name\": \"terminal_run\", \"name\": \"x\"}</tool_call>", "{\"name\": 5}" })
                {
                    AiAgent.SalvageTextCalls(dup, tools2);
                    AiAgent.ParseTextCalls(dup, tools2);
                }
                if (!AiAgent.MentionsToolUse("terminal_run(\"df\")", tools2) || AiAgent.MentionsToolUse("terminal_run is the tool that types commands", tools2)
                    || !AiAgent.MentionsToolUse("(terminal_run session 79e25fd0 command: uptime)", tools2) || !AiAgent.MentionsToolUse("terminal_run command: uptime", tools2))
                    throw new Exception("MentionsToolUse");
                // 0.6.12 (DarkCipher0x's second screenshot): a pseudo-call inside prose, then "please provide approval" for a call
                // never made. The loop says nothing ran, switches the run to the <tool_call> format, and the model's real call runs.
                ran.Clear(); steps5.Clear();
                fake.Enqueue(new FakeAi.Reply("I will now check the CPU load on the target session vps-web-01 (session 79e25fd0).\n\n(terminal_run session 79e25fd0 command: top -l 1 -w 60 | head -n 1)\n\nPlease review and provide approval if the data is correct."));
                fake.Enqueue(new FakeAi.Reply("<tool_call>{\"name\": \"terminal_run\", \"arguments\": {\"command\": \"uptime\", \"purpose\": \"load\"}}</tool_call>"));
                fake.Enqueue(new FakeAi.Reply("Load average 0.05: idle."));
                int chatsBefore = fake.Chats.Count;
                var (a9, r9) = await AiAgent.RunAsync(Q("check what the cpu load has been over 24 hours"), tools2, H(), 6, CancellationToken.None);
                var retry = fake.Chats[chatsBefore + 1];
                if (ran.Count != 1 || ran[0]["command"]?.GetValue<string>() != "uptime" || !a9.Contains("0.05") || r9.ToolCalls != 1
                    || !steps5.Any(x => x.Kind == "note" && x.Text.Contains("described a tool call without making it"))
                    || retry["tools"] is JsonArray { Count: > 0 } || !retry["messages"]!.ToJsonString().Contains("tool_call"))
                    throw new Exception($"pseudo-call in prose not corrected: ran [{string.Join(" | ", ran.Select(x => x.ToJsonString()))}], answer [{a9}], run {r9}, steps [{string.Join(" | ", steps5.Select(x => x.Text))}]");
                // 0.6.17 (DarkCipher0x's fourth screenshot, phi-4-mini): "As an AI developed by Microsoft … I am unable to execute
                // commands", 0 tool calls. A refusal before any tool ran is answered with "you can" and the strict format.
                ran.Clear(); steps5.Clear();
                fake.Enqueue(new FakeAi.Reply("As an AI developed by Microsoft with restricted capabilities, I am unable to execute commands on external systems. To check the CPU load you would typically need to log in to the cloud console."));
                fake.Enqueue(new FakeAi.Reply("<tool_call>{\"name\": \"terminal_run\", \"arguments\": {\"command\": \"uptime\", \"purpose\": \"load\"}}</tool_call>"));
                fake.Enqueue(new FakeAi.Reply("Load average 0.24: the server is idle."));
                chatsBefore = fake.Chats.Count;
                var (aRef, rRef) = await AiAgent.RunAsync(Q("please check on the target server"), tools2, H(), 6, CancellationToken.None);
                if (ran.Count != 1 || ran[0]["command"]?.GetValue<string>() != "uptime" || !aRef.Contains("0.24") || rRef.ToolCalls != 1
                    || !steps5.Any(x => x.Kind == "note" && x.Text.Contains("said it can't run commands"))
                    || !fake.Chats[chatsBefore + 1]["messages"]!.ToJsonString().Contains("You CAN act here"))
                    throw new Exception($"refusal not corrected: ran [{string.Join(" | ", ran.Select(x => x.ToJsonString()))}], answer [{aRef}], run {rRef}, steps [{string.Join(" | ", steps5.Select(x => x.Text))}]");
                foreach (var no in new[] { "I'm unable to execute commands directly.", "I do not have the ability to run commands.", "As an AI language model, I can't access your server.", "I cannot directly connect to remote systems." })
                    if (!AiAgent.ClaimsNoAccess(no)) throw new Exception("refusal not recognised: " + no);
                foreach (var fine in new[] { "You are unable to connect because port 22 is filtered.", "The service cannot run without its config file.", "Load is 0.24; nothing I can't explain." })
                    if (AiAgent.ClaimsNoAccess(fine)) throw new Exception("ordinary answer read as a refusal: " + fine);
                // 0.6.15 (DarkCipher0x's third screenshot, phi-4-mini on Foundry Local): the call closed with "}>" instead of
                // "</tool_call>", both before and after the correction, so 0 tool calls ran. Every closing now counts.
                var closings = new[] { "</tool_call>", ">", "}>", "</tool_call", "", "</ tool_call >" };
                foreach (var close in closings)
                {
                    var one = AiAgent.TaggedCalls("<tool_call>{\"name\": \"terminal_run\", \"arguments\": {\"command\": \"awk '{print $1}' /proc/loadavg\", \"purpose\": \"load {1m}\"}}" + close);
                    if (one.Count != 1 || one[0].Call["function"]!["arguments"]!["command"]!.GetValue<string>() != "awk '{print $1}' /proc/loadavg")
                        throw new Exception($"<tool_call> closed with \"{close}\" not read");
                }
                if (AiAgent.TaggedCalls("<tool_call>{\"name\": \"terminal_run\", \"arguments\": {\"command\": \"uptime\"").Count != 0
                    || AiAgent.TaggedCalls("<tool_call> I would run {\"name\": \"terminal_run\"}").Count != 0)
                    throw new Exception("an unclosed or prose-led <tool_call> was read as a call");
                var (restText, twoCalls) = AiAgent.ParseTextCalls("Checking. <tool_call>{\"name\": \"terminal_run\", \"arguments\": {\"command\": \"uptime\"}}}> then <tool_call>{\"name\": \"terminal_run\", \"arguments\": {\"command\": \"free -m\"}}</tool_call> done", tools2);
                if (twoCalls.Count != 2 || restText != "Checking.  then  done") throw new Exception($"two tagged calls: {twoCalls.ToJsonString()} / [{restText}]");
                ran.Clear(); steps5.Clear();
                fake.Enqueue(new FakeAi.Reply("<tool_call>{\"name\": \"terminal_run\",\n\"arguments\": {\"command\": \"cat /proc/loadavg\", \"purpose\": \"Check the CPU load over the last 48 hours\"}}>"));
                fake.Enqueue(new FakeAi.Reply("The load averages are 0.31, 0.28 and 0.25: the server is mostly idle."));
                var (a10, r10) = await AiAgent.RunAsync(Q("what has been the CPU load over the last 48 hours"), tools2, H(), 6, CancellationToken.None);
                if (ran.Count != 1 || ran[0]["command"]?.GetValue<string>() != "cat /proc/loadavg" || r10.ToolCalls != 1 || !a10.Contains("0.31"))
                    throw new Exception($"the screenshot's call didn't run: ran [{string.Join(" | ", ran.Select(x => x.ToJsonString()))}], answer [{a10}], steps [{string.Join(" | ", steps5.Select(x => x.Text))}]");
                ok("0.6.17 (DarkCipher0x's fourth screenshot): a model that answers \"As an AI … I am unable to execute commands\" with the tools in hand, before any tool ran, is told it can act and asked for the call in the <tool_call> format, and the call runs; first-person refusals are recognised, ordinary answers (\"you are unable to connect…\") are not");
                ok($"0.6.15 (DarkCipher0x): <tool_call> blocks run however a small model closes them ({closings.Length} closings incl. \"}}>\" and none; braces inside the command kept), two in one reply with the prose kept, an unclosed or prose-led block never runs; the screenshot's exact reply (phi-4-mini, \"…}}}}>\") now runs cat /proc/loadavg in the target and the answer follows");
                ok("0.6.12 (DarkCipher0x): a reply that writes a tool call into prose (\"(terminal_run session … command: …)\", then asks for approval of a call never made) is answered with \"nothing ran, make the call\" and the run switches to the <tool_call> format; the model's real call then runs; after two tries without a call the step log says nothing ran");
                ok("0.6.10 (DarkCipher0x): phi-4-mini's loose tool calls run (\"terminal_run terminal_run -- session … -- display top\", bare tool-name headings and made-up [No response …] results dropped, unknown arguments dropped); the call without a command gets the error naming \"command\" and the corrected call runs; prose around such lines still never runs; a reply that only describes calls is flagged in the step log (nothing ran)");
                ok("s45 transcript 2: terminal_run without a command gets an error naming \"command\"; `terminal_run --session … --command '…'` in a code block inside prose runs as terminal_run {command} (session dropped); --key=value, \"…\" escapes and \\-continued lines parse; Review & run types the command, never the pseudo-call");
                ok("tool calls written as text by a model that has native tools (phi-4-mini on Foundry Local): terminal_run(target=…, \"df -h\") runs as terminal_run {command: \"df -h\"} (unknown target dropped, positional value into the first required parameter), a bare JSON call runs, results go back as text and the step log says so; complicated scripts survive intact (multi-line heredocs with brackets, commas and quotes, triple-quoted and raw strings, \\. \\d \\; kept as written, two calls in one reply); a call only mentioned in prose, an unclosed call, or a tool that wasn't offered never runs");
            }
            catch (Exception ex) { fake.Capabilities = new[] { "completion", "tools" }; fake.NextError = null; fail("tool-calling fallback", ex); }

            // 6. Per-chat model.
            try
            {
                Engine("openai");
                using (AiClient.UseModel("other-model")) await AiClient.ChatAsync(Q("m"), null, null, CancellationToken.None);
                var m1 = fake.LastChat?["model"]?.GetValue<string>();
                await AiClient.ChatAsync(Q("m"), null, null, CancellationToken.None);
                var m2 = fake.LastChat?["model"]?.GetValue<string>();
                if (m1 != "other-model" || m2 != "fake" || s.OpenAiModel != "fake") throw new Exception($"{m1} / {m2} / settings {s.OpenAiModel}");
                ok("per-chat model: a conversation's model is used for its requests only; Settings and other chats keep the default");
            }
            catch (Exception ex) { fail("per-chat model", ex); }

            // 7. Memory guard: context cap + unload under pressure, warm-up only when calm, Foundry idle unload (never a kill).
            try
            {
                Engine("ollama-gpu");
                s.AiContext = 65536; s.AiResourceGuard = true; s.AiUnloadIdleFoundry = true; s.AiKeepWarmMinutes = 10;
                AiResourceGuard.Memory = () => (90, 100, 80);
                await AiClient.ChatAsync(Q("p"), null, null, CancellationToken.None);
                var high = fake.LastChat!;
                AiResourceGuard.Memory = () => (95, 100, 90);
                await AiClient.ChatAsync(Q("p"), null, null, CancellationToken.None);
                var crit = fake.LastChat!;
                AiResourceGuard.Memory = () => (10, 100, 20);
                await AiClient.ChatAsync(Q("p"), null, null, CancellationToken.None);
                var calm = fake.LastChat!;
                if (high["options"]!["num_ctx"]!.GetValue<int>() != 8192 || high["keep_alive"]!.GetValue<string>() != "0" || crit["options"]!["num_ctx"]!.GetValue<int>() != 4096
                    || calm["options"]!["num_ctx"]!.GetValue<int>() != 65536 || calm["keep_alive"]!.GetValue<string>() != "10m") throw new Exception("context / keep_alive under pressure");
                // Warm-up when the user starts typing (calm only).
                AiClient.AllowWarmInSelfTest = true; AiClient.ResetWarm();
                int gens = fake.Generates;
                AiClient.Warm();
                for (int i = 0; i < 40 && fake.Generates == gens; i++) await Task.Delay(50);
                if (fake.Generates != gens + 1) throw new Exception("no warm-up request");
                AiClient.ResetWarm();
                AiResourceGuard.Memory = () => (95, 100, 90);
                AiClient.Warm();
                await Task.Delay(400);
                if (fake.Generates != gens + 1) throw new Exception("warmed up under memory pressure");
                AiClient.AllowWarmInSelfTest = false;
                // Foundry Local: unloaded when memory is tight and it isn't the engine, or when it is but the user went idle.
                var cli = new List<string>();
                AiResourceGuard.Foundry = (args, _, _) =>
                {
                    lock (cli) cli.Add(args);
                    return Task.FromResult(args.StartsWith("model list") ? (0, "{\"models\":[{\"alias\":\"phi-4-mini\",\"id\":\"Phi-4-mini-instruct-generic-cpu:1\",\"device\":\"CPU\",\"type\":\"chat\"}]}") : (0, "ok"));
                };
                s.FoundryUrl = "";   // the guard only manages the local Foundry Local service it discovers itself
                var t1 = await AiResourceGuard.TickAsync(CancellationToken.None);   // ollama engine, pressure → unload Foundry
                AiResourceGuard.Memory = () => (10, 100, 20);
                var t2 = await AiResourceGuard.TickAsync(CancellationToken.None);   // calm → nothing
                Engine("npu");
                s.FoundryUrl = "";
                AiResourceGuard.Memory = () => (90, 100, 80);
                AiResourceGuard.Touch();
                var t3 = await AiResourceGuard.TickAsync(CancellationToken.None);   // Foundry in use and the user active → kept
                AiResourceGuard.SetLastUse(DateTime.UtcNow.AddHours(-1));
                AiResourceGuard.Memory = () => (10, 100, 20);
                var t4 = await AiResourceGuard.TickAsync(CancellationToken.None);   // Foundry in use, idle → unload
                if (!t1.StartsWith("unloaded: Phi-4") || !t2.StartsWith("checked") || !t3.StartsWith("checked") || !t4.StartsWith("unloaded"))
                    throw new Exception($"foundry guard: {t1} | {t2} | {t3} | {t4}");
                if (cli.Count(c => c.StartsWith("model unload Phi-4-mini-instruct-generic-cpu:1")) != 2 || cli.Any(c => c.Contains("stop") || c.Contains("kill"))) throw new Exception("foundry CLI: " + string.Join(" | ", cli));
                ok("memory guard: commit ≥ 85 % → 8K context and unload after the answer (keep_alive 0), ≥ 93 % → 4K, calm → full context and kept warm 10 min; the model is warmed when the user starts typing (never under pressure); Foundry Local's models unloaded when memory is tight and it isn't the engine, or when it is and the AI went idle — kept while in use; the service is never stopped");
            }
            catch (Exception ex) { fail("AI memory guard", ex); }
            finally { AiClient.AllowWarmInSelfTest = false; AiResourceGuard.Memory = () => (10, 100, 20); AiResourceGuard.Foundry = foundryWas; s.AiContext = saved.AiContext; }

            // 8. The AI Engineer as an agent: tools, streamed answer, step log in the panel, stable system prompt.
            var ws = AiWorkspace.Current;
            var chatWas = ws.Chat;
            var approveWas = ws.Approve;
            try
            {
                Engine("ollama-gpu");
                s.AiAutoApproveReadOnly = true;
                ws.Chat = null;
                ws.Approve = (_, _, _) => Task.FromResult(false);
                var c = ws.NewConversation();
                c.Agent = true;
                int lives = 0;
                Action<AiConversation> onLive = _ => Interlocked.Increment(ref lives);
                ws.LiveChanged += onLive;
                fake.Enqueue(new FakeAi.Reply("Looking.", ("current_context", new JsonObject()), ("list_terminal_sessions", new JsonObject())));
                fake.Enqueue(new FakeAi.Reply("Everything you have open looks healthy; no terminal target is chosen."));
                await ws.SendAsync("What am I looking at?", AiFeature.Helper, consented: true);
                var sys1 = fake.Chats[^1]["messages"]![0]!["content"]!.GetValue<string>();
                fake.Enqueue(new FakeAi.Reply("Second answer."));
                await ws.SendAsync("And now?", AiFeature.Helper, consented: true);
                var sys2 = fake.LastChat!["messages"]![0]!["content"]!.GetValue<string>();
                ws.LiveChanged -= onLive;
                await Settle(400);
                List<AgentStep> steps; lock (c.Steps) steps = c.Steps.ToList();
                var panel = AiModule.Dock.Panel;
                if (c.Messages.Count(m => m.Role == "assistant") != 2 || !c.Messages.Any(m => m.Text.StartsWith("Everything you have open"))) throw new Exception("answers: " + string.Join(" | ", c.Messages.Select(m => m.Role + ":" + m.Text)));
                if (lives < 4) throw new Exception("streamed updates: " + lives);
                if (sys1 != sys2) throw new Exception("system prompt changed between questions (no prefix reuse)");
                if (c.History.Count != 4) throw new Exception("history " + c.History.Count);
                var first = fake.Chats.First(b => b["messages"]!.ToJsonString().Contains("What am I looking at?"));
                if (first["tools"] is not JsonArray { Count: > 30 }) throw new Exception("AI Engineer offered no tools");
                if (panel != null && panel.StepLinesForTest == 0) throw new Exception("no step lines in the panel");
                snap(main, Path.Combine(shots, "ai-engineer-agent.png"));
                ok($"AI Engineer agent: tools offered ({first["tools"]!.AsArray().Count}), two read-only tools run without asking (auto-approve), answer streamed ({lives} live updates, coalesced), the step log shown in the panel, the system prompt identical between questions (the engine can reuse it), history keeps question + answer only; rendered ai-engineer-agent.png");
            }
            catch (Exception ex) { fail("AI Engineer agent", ex); }
            finally { ws.Chat = chatWas; ws.Approve = approveWas; ws.NewConversation(); }

            // 9 + 10. The AI Engineer commanding its target session; terminal typing latency (same real PowerShell session).
            await TerminalAsync(main, s, ok, fail, Settle, report);

            // 11. More of the app as tools: notes, incidents, transfers, portals, libraries, infrastructure.
            try
            {
                s.AiAutoApproveReadOnly = true;
                var names = Assistant.AllTools.Select(t => t?["function"]?["name"]?.ToString()).ToList();
                foreach (var n in new[] { "terminal_read_screen", "terminal_send_keys", "terminal_interrupt", "terminal_switch_target", "terminal_open_session", "infra_actions", "cloud_action", "append_to_note", "create_incident", "add_to_incident", "transfer_status", "portal_list", "portal_open", "library_list" })
                    if (!names.Contains(n)) throw new Exception("missing tool " + n);
                if (names.Count != names.Distinct().Count()) throw new Exception("duplicate tool names");
                var yes = new CapabilityContext((_, _, _) => Task.FromResult(true));
                var asked = new List<string>();
                var no = new CapabilityContext((t, _, _) => { asked.Add(t); return Task.FromResult(false); });
                var note = Notes.NoteStore.Create("dcx-selftest agent note", null, "start");
                Incidents.Incident? inc = null;
                try
                {
                    var d1 = await Capabilities.RunAsync("append_to_note", new JsonObject { ["note_id"] = note.Id, ["text"] = "more" }, no, CancellationToken.None);
                    if (d1 != "User declined." || Notes.NoteStore.Find(note.Id)!.Body != "start") throw new Exception("declined append changed the note");
                    var a1 = await Capabilities.RunAsync("append_to_note", new JsonObject { ["note_id"] = note.Id, ["text"] = "more" }, yes, CancellationToken.None);
                    if (!a1.StartsWith("Added") || !Notes.NoteStore.Find(note.Id)!.Body.EndsWith("more")) throw new Exception("append: " + a1);
                    var ci = await Capabilities.RunAsync("create_incident", new JsonObject { ["title"] = "dcx-selftest agent incident", ["severity"] = "sev2", ["summary"] = "disk full on db" }, yes, CancellationToken.None);
                    inc = Incidents.IncidentStore.All.FirstOrDefault(i => i.Title == "dcx-selftest agent incident") ?? throw new Exception("incident not created: " + ci);
                    var ai2 = await Capabilities.RunAsync("add_to_incident", new JsonObject { ["incident_id"] = inc.Id, ["title"] = "cause", ["text"] = "logrotate stopped" }, yes, CancellationToken.None);
                    if (!ai2.StartsWith("Added") || inc.Severity != Incidents.IncidentSeverity.Sev2 || !Incidents.IncidentStore.Markdown(inc).Contains("logrotate stopped")) throw new Exception("incident entry: " + ai2);
                    var tr = await Capabilities.RunAsync("transfer_status", new JsonObject(), yes, CancellationToken.None);
                    if (!tr.Contains("Queue") || !tr.Contains("untrusted=\"true\"")) throw new Exception("transfers: " + tr);
                    var pl = await Capabilities.RunAsync("portal_list", new JsonObject(), yes, CancellationToken.None);
                    if (!pl.Contains("isn't enabled") && !pl.Contains("untrusted=\"true\"")) throw new Exception("portals: " + pl);
                    var ll = await Capabilities.RunAsync("library_list", new JsonObject(), yes, CancellationToken.None);
                    if (!ll.Contains("No shared library") && !ll.Contains("untrusted=\"true\"") || ll.Contains("Secret")) throw new Exception("libraries: " + ll);
                    Exception? noProv = null;
                    try { await Capabilities.RunAsync("infra_actions", new JsonObject { ["provider"] = "no-such-provider", ["object_id"] = "1" }, yes, CancellationToken.None); } catch (Exception ex) { noProv = ex; }
                    if (noProv?.Message.Contains("infra_list_providers") != true) throw new Exception("infra_actions: " + noProv?.Message);
                    var audit = AuditLog.Read(DateTime.Now.ToString("yyyy-MM"));
                    if (!audit.Any(x => x.Action.StartsWith("text added to a note by the AI")) || !audit.Any(x => x.Action.StartsWith("incident opened by the AI")) || !audit.Any(x => x.Action.StartsWith("entry added to an incident by the AI"))) throw new Exception("not audited");
                    ok($"more tools ({names.Count} in all, no duplicates): note append (declined → unchanged; approved → added, history kept), incident opened + timeline entry, transfer queue/history, portal bookmarks, shared libraries (no credentials), infrastructure actions per object, cloud screen actions (checked against the Hetzner fake) — every change asks and is audited, data fenced as untrusted");
                }
                finally
                {
                    Notes.NoteStore.Delete(note);
                    if (inc != null) Incidents.IncidentStore.Delete(inc);
                }
            }
            catch (Exception ex) { fail("more AI tools", ex); }
        }
        finally
        {
            (s.AiEnabled, s.AiEngine, s.OllamaUrl, s.OllamaModel, s.OllamaTunnelHostId, s.OpenAiUrl, s.OpenAiModel, s.OpenAiKey, s.AnthropicUrl, s.AnthropicModel, s.AnthropicKey,
             s.FoundryUrl, s.FoundryModel, s.AiContext, s.AiStream, s.AiMaxSteps, s.AiKeepWarmMinutes, s.AiResourceGuard, s.AiUnloadIdleFoundry, s.AiAutoRunReadOnlyTarget,
             s.AiAutoApproveReadOnly, s.AiFeatureOperator, s.AiFeatureHelper, s.AiTerminalBridge, s.AiShareCommandOutput, s.AiAllowNotes, s.AiCommandTimeoutMinutes) = saved;
            AiResourceGuard.Memory = memWas; AiResourceGuard.OllamaSpill = spillWas; AiResourceGuard.Foundry = foundryWas;
            TerminalBridge.Confirm = confirmWas;
            AiClient.ForgetFoundry(); AiClient.ForgetToolSupport();
            AppSettings.Save();
            Timings = report.ToString();
            try { File.WriteAllText(Path.Combine(reportDir, "ai-timings.txt"), Timings, new UTF8Encoding(false)); } catch { }
        }
    }

    private static async Task TerminalAsync(MainWindow main, AppSettings s, Action<string> ok, Action<string, Exception?> fail, Func<int, Task> settle, StringBuilder report)
    {
        System.Windows.Controls.TabItem? tab = null;
        HostEntry? temp = null;
        s.AiTerminalBridge = true; s.AiShareCommandOutput = true; s.AiCommandTimeoutMinutes = 1;
        try
        {
            var ps = Sessions.LocalShells.Find("powershell") ?? throw new Exception("Windows PowerShell not discovered");
            var tv = new TerminalView(new Sessions.LocalTarget(ps), null, connectOnOpen: false);
            tab = main.AddTab("❯ agent test", tv);
            for (int i = 0; i < 80 && !tv.PageReady; i++) await settle(250);
            var seen = new StringBuilder();
            tv.OutputText += t => { lock (seen) seen.Append(t); };
            await tv.ConnectAsync();
            for (int i = 0; i < 120 && !TerminalBridge.Plain(seen.ToString()).Contains("PS "); i++) await settle(250);
            if (!tv.IsConnected || tv.Shell != "powershell") throw new Exception("local PowerShell never started — " + tv.DiagStatus);
            // 0.6.11 (DarkCipher0x): the target defaults to the terminal in front and follows it; an explicit pick (or
            // "none") sticks; while an AI run holds the target nothing moves it. And the typed confirmation: case and
            // surrounding spaces don't matter (Enter after a matching name confirms; the dialog itself is never shown here).
            try
            {
                TerminalBridge.FollowFront();
                TerminalBridge.Follow(tv);
                if (!TerminalBridge.Following || TerminalBridge.Target?.SessionId != tv.SessionId) throw new Exception("the terminal in front didn't become the target");
                TerminalBridge.Choose(null);
                TerminalBridge.Follow(tv);
                if (TerminalBridge.Following || TerminalBridge.Target != null) throw new Exception("followed after you chose none");
                using (TerminalBridge.HoldTarget())
                {
                    TerminalBridge.FollowFront();
                    TerminalBridge.Follow(tv);
                    if (TerminalBridge.Target != null) throw new Exception("the target moved during an AI run");
                }
                TerminalBridge.Follow(tv);
                if (TerminalBridge.Target?.SessionId != tv.SessionId) throw new Exception("following didn't resume after the run");
                if (!Dialogs.TypedMatches("  WEB-01 ", "web-01") || Dialogs.TypedMatches("web-0", "web-01") || Dialogs.TypedMatches("", "") || !Dialogs.TypedMatches("production", "PRODUCTION"))
                    throw new Exception("typed confirmation matching");
                ok("0.6.11 (DarkCipher0x): the AI Engineer's target follows the terminal in front by default, an explicit pick or \"none\" sticks, an AI run holds the target until it ends; a typed confirmation matches regardless of case and surrounding spaces, and Enter after a matching name confirms instead of cancelling");
            }
            catch (Exception ex) { fail("AI Engineer default target / typed confirmation", ex); }
            TerminalBridge.Choose(tv);
            var t = TerminalBridge.Target!;
            var confirms = new List<(string Title, string? Type)>();
            bool answer = false;
            TerminalBridge.Confirm = (title, _, _, type, _) => { lock (confirms) confirms.Add((title, type)); return answer; };
            var yes = new CapabilityContext((_, _, _) => Task.FromResult(true));
            var no = new CapabilityContext((_, _, _) => Task.FromResult(false));
            JsonObject A(params (string K, string V)[] kv) { var o = new JsonObject(); foreach (var (k, v) in kv) o[k] = v; return o; }

            // Auto-run off: a read-only command is reviewed.
            s.AiAutoRunReadOnlyTarget = false;
            var r0 = await Capabilities.RunAsync("terminal_run", A(("command", "Get-Location"), ("purpose", "where")), yes, CancellationToken.None);
            if (confirms.Count != 1 || !r0.StartsWith("User declined")) throw new Exception("auto-run off must review: " + r0);
            // Auto-run on: read-only runs straight away; change, endless and interactive still reviewed.
            s.AiAutoRunReadOnlyTarget = true;
            var r1 = await Capabilities.RunAsync("terminal_run", A(("command", "Get-Location; Write-Output dcx-auto-ok; Write-Output 'password=Hunter2-Agent-Secret'"), ("purpose", "check")), yes, CancellationToken.None);
            if (confirms.Count != 1 || !r1.Contains("ran automatically") || !r1.Contains("dcx-auto-ok") || r1.Contains("Hunter2-Agent-Secret")) throw new Exception("auto-run: " + r1);
            var r2 = await Capabilities.RunAsync("terminal_run", A(("command", "New-Item -ItemType Directory C:\\dcx-never"), ("purpose", "change")), yes, CancellationToken.None);
            var r3 = await Capabilities.RunAsync("terminal_run", A(("command", "Start-Sleep -Seconds 120"), ("purpose", "endless")), yes, CancellationToken.None);
            var r4 = await Capabilities.RunAsync("terminal_run", A(("command", "Remove-Item C:\\dcx-never -Recurse -Force"), ("purpose", "destructive")), yes, CancellationToken.None);
            if (confirms.Count != 4 || !r2.StartsWith("User declined") || !r3.StartsWith("User declined") || confirms[3].Type != t.Title) throw new Exception("non-read-only must review: " + string.Join(" | ", confirms));
            if (TerminalBridge.WhyNotAuto(t, CommandRisk.Assess("top")) is null || TerminalBridge.WhyNotAuto(t, CommandRisk.Assess("tail -f /var/log/syslog")) is null || TerminalBridge.WhyNotAuto(t, CommandRisk.Assess("sudo ls")) is null
                || TerminalBridge.WhyNotAuto(t, CommandRisk.Assess("df -h")) is not null || TerminalBridge.WhyNotAuto(t, CommandRisk.Assess("ping -c 3 1.1.1.1")) is not null || TerminalBridge.WhyNotAuto(t, CommandRisk.Assess("sed -n 's/a/b/w out' f")) is null)
                throw new Exception("auto-run rules");
            // Read the screen (masked, fenced), send keys (always reviewed), interrupt, switch target, open session.
            var screen = await Capabilities.RunAsync("terminal_read_screen", A(("lines", "40")), yes, CancellationToken.None);
            if (!screen.Contains("dcx-auto-ok") || screen.Contains("Hunter2-Agent-Secret") || !screen.Contains("untrusted=\"true\"")) throw new Exception("screen: " + screen);
            var k0 = await Capabilities.RunAsync("terminal_send_keys", A(("keys", "Write-Output dcx-keys-sent{ENTER}"), ("purpose", "test")), yes, CancellationToken.None);
            await settle(800);
            if (k0 != "User declined." || TerminalBridge.Plain(seen.ToString()).Contains("dcx-keys-sent\r\n") || confirms.Count != 5) throw new Exception("declined keys: " + k0);
            answer = true;
            var k1 = await Capabilities.RunAsync("terminal_send_keys", A(("keys", "Write-Output dcx-keys-sent{ENTER}"), ("purpose", "test")), yes, CancellationToken.None);
            for (int i = 0; i < 40 && !TerminalBridge.Plain(seen.ToString()).Split('\n').Any(l => l.Trim() == "dcx-keys-sent"); i++) await settle(100);
            if (!k1.StartsWith("Sent") || !TerminalBridge.Plain(seen.ToString()).Split('\n').Any(l => l.Trim() == "dcx-keys-sent")) throw new Exception("keys: " + k1);
            if (TerminalBridge.Keys("{CTRL+C}", out _) != "\x03" || TerminalBridge.Keys("{BOGUS}", out var kerr) != null || kerr == null) throw new Exception("key parser");
            answer = false;
            var i0 = await Capabilities.RunAsync("terminal_interrupt", new JsonObject(), yes, CancellationToken.None);
            var sw0 = await Capabilities.RunAsync("terminal_switch_target", A(("session_id", "nosuch"), ("reason", "x")), yes, CancellationToken.None);
            var sw1 = await Capabilities.RunAsync("terminal_switch_target", A(("session_id", t.SessionId), ("reason", "x")), yes, CancellationToken.None);
            if (i0 != "User declined." || !sw0.StartsWith("ERROR") || !sw1.Contains("already the target")) throw new Exception($"interrupt/switch: {i0} | {sw0} | {sw1}");
            temp = new HostEntry { Name = "dcx agent open test", Address = "192.0.2.77", Username = "test" };
            AppData.Current.Hosts.Add(temp);
            int tabs = main.Panes.AllTabs.Count();
            var o0 = await Capabilities.RunAsync("terminal_open_session", A(("host", temp.Name), ("reason", "x")), no, CancellationToken.None);
            if (o0 != "User declined." || main.Panes.AllTabs.Count() != tabs || TerminalBridge.Target?.SessionId != t.SessionId) throw new Exception("open session declined: " + o0);
            var audit = AuditLog.Read(DateTime.Now.ToString("yyyy-MM"));
            if (!audit.Any(x => x.Action.StartsWith("AI read-only command auto-run in session " + t.SessionId)) || !audit.Any(x => x.Action.StartsWith("AI read the screen of session " + t.SessionId)) || !audit.Any(x => x.Action.StartsWith("AI sent keys to session " + t.SessionId)))
                throw new Exception("not audited");
            ok("AI Engineer commands its session: auto-run off → every command reviewed; on → read-only commands run without a review (output masked), change / endless / interactive / sudo / destructive still reviewed (destructive asks for the session name); screen read masked and fenced; keys always reviewed (declined → nothing typed), {KEY} names parsed; Ctrl+C asks unless it is the AI's own command; switching target or opening a session only on the user's yes; everything audited");

            // Typing latency: echo of a typed character until it is posted to the page, while other UI work competes.
            async Task<double> Echo(bool quick)
            {
                TerminalView.QuickFlush = quick;
                var times = new List<double>();
                for (int n = 0; n < 8; n++)
                {
                    bool load = true;
                    void Busy() { if (!load) return; Thread.Sleep(12); _ = Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, Busy); }
                    for (int b = 0; b < 3; b++) _ = Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, Busy);
                    var got = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var sw = new Stopwatch();
                    bool arrived = false;
                    Action<string> onOut = x => { if (x.Contains('q')) arrived = true; };
                    Action onFlush = () => { if (arrived) got.TrySetResult(sw.Elapsed.TotalMilliseconds); };
                    tv.OutputText += onOut; tv.Flushed += onFlush;
                    sw.Start();
                    await tv.OnInputAsync("q");
                    var done = await Task.WhenAny(got.Task, Task.Delay(3000));
                    tv.OutputText -= onOut; tv.Flushed -= onFlush;
                    load = false;
                    if (done == got.Task) times.Add(got.Task.Result);
                    await settle(60);
                }
                tv.SendRaw("\x03");
                await settle(300);
                TerminalView.QuickFlush = true;
                if (times.Count < 5) throw new Exception($"echo arrived {times.Count}/8 times");
                times.Sort();
                return times[times.Count / 2];
            }
            var before = await Echo(false);
            var after = await Echo(true);
            report.AppendLine($"Terminal echo while other UI work runs (local PowerShell): {before:0} ms → {after:0} ms (median of 8)");
            if (after > before) throw new Exception($"echo {before:0} → {after:0} ms");
            ok($"terminal typing latency: echo posted to the page {before:0} ms → {after:0} ms (median of 8, with background UI work competing); keystrokes are written off the UI thread in order");
        }
        catch (Exception ex) { fail("AI Engineer terminal control / typing latency", ex); }
        finally
        {
            TerminalView.QuickFlush = true;
            TerminalBridge.Choose(null);
            if (temp != null) AppData.Current.Hosts.Remove(temp);
            if (tab != null && main.Panes.Contains(tab)) { (tab.Content as TerminalView)?.Disconnect(); main.CloseTab(tab); }
        }
    }
}

/// <summary>A loopback AI engine for the self-test: Ollama, OpenAI-compatible, Anthropic and Foundry Local endpoints,
/// streamed (chunked NDJSON / SSE) or whole, with scripted answers and tool calls. HTTP/1.1 keep-alive, so connection
/// reuse can be counted.</summary>
internal sealed class FakeAi : IDisposable
{
    public sealed record Reply(string Text, params (string Name, JsonObject Args)[] Calls);

    private readonly TcpListener _l;
    private readonly CancellationTokenSource _cts = new();
    private readonly Queue<Reply> _script = new();
    public int Port { get; }
    public int PrefillMs = 150, TokenMs = 15;
    public string[] Capabilities = { "completion", "tools" };
    public Reply? Always;
    public (int Status, string Message)? NextError;
    public bool RefuseTemperature;
    private int _connections, _generates;
    public int Connections => Volatile.Read(ref _connections);
    public int Generates => Volatile.Read(ref _generates);
    private readonly List<JsonObject> _chats = new();
    public IReadOnlyList<JsonObject> Chats { get { lock (_chats) return _chats.ToList(); } }
    public JsonObject? LastChat { get { lock (_chats) return _chats.LastOrDefault(); } }

    private const string DefaultText = "The service is healthy and the disks have plenty of space left so nothing needs to be done right now apart from the routine updates that are already scheduled for the weekend window as usual thanks";

    private FakeAi()
    {
        _l = new TcpListener(IPAddress.Loopback, 0);
        _l.Start();
        Port = ((IPEndPoint)_l.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoop);
    }

    public static FakeAi Start() => new();
    public void Enqueue(Reply r) { lock (_script) _script.Enqueue(r); }
    private Reply Next() { lock (_script) return Always ?? (_script.Count > 0 ? _script.Dequeue() : new Reply(DefaultText)); }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await _l.AcceptTcpClientAsync(_cts.Token); } catch { return; }
            Interlocked.Increment(ref _connections);
            _ = Task.Run(() => Serve(c));
        }
    }

    private async Task Serve(TcpClient client)
    {
        using var _ = client;
        client.NoDelay = true;
        var stream = client.GetStream();
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var head = await ReadHead(stream);
                if (head == null) return;
                var lines = head.Split("\r\n");
                var parts = lines[0].Split(' ');
                string method = parts[0], path = parts.Length > 1 ? parts[1] : "/";
                int len = lines.Select(l => l.Split(':', 2)).Where(p => p.Length == 2 && p[0].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase)).Select(p => int.Parse(p[1].Trim())).FirstOrDefault();
                var buf = new byte[len];
                int got = 0;
                while (got < len) { int n = await stream.ReadAsync(buf.AsMemory(got)); if (n == 0) return; got += n; }
                var body = len > 0 ? JsonNode.Parse(Encoding.UTF8.GetString(buf)) as JsonObject ?? new JsonObject() : new JsonObject();
                await Route(stream, method, path.Split('?')[0], body);
            }
        }
        catch { }
    }

    private static async Task<string?> ReadHead(NetworkStream s)
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        while (true)
        {
            int n = await s.ReadAsync(one);
            if (n == 0) return null;
            sb.Append((char)one[0]);
            if (sb.Length >= 4 && sb[^1] == '\n' && sb[^2] == '\r' && sb[^3] == '\n' && sb[^4] == '\r') return sb.ToString(0, sb.Length - 4);
            if (sb.Length > 65536) return null;
        }
    }

    private static Task Whole(NetworkStream s, int status, string json, string type = "application/json")
    {
        var b = Encoding.UTF8.GetBytes(json);
        var h = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\nContent-Type: {type}\r\nContent-Length: {b.Length}\r\n\r\n");
        return WriteAll(s, h, b);
    }

    private static async Task WriteAll(NetworkStream s, params byte[][] parts) { foreach (var p in parts) await s.WriteAsync(p); await s.FlushAsync(); }

    private sealed class Chunked(NetworkStream s)
    {
        public async Task Start(string type) => await WriteAll(s, Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: {type}\r\nTransfer-Encoding: chunked\r\n\r\n"));
        public async Task Send(string text)
        {
            var b = Encoding.UTF8.GetBytes(text);
            await WriteAll(s, Encoding.ASCII.GetBytes(b.Length.ToString("x") + "\r\n"), b, Encoding.ASCII.GetBytes("\r\n"));
        }
        public async Task End() => await WriteAll(s, Encoding.ASCII.GetBytes("0\r\n\r\n"));
    }

    private static IEnumerable<string> Tokens(string text)
    {
        var words = text.Split(' ');
        for (int i = 0; i < words.Length; i++) yield return i < words.Length - 1 ? words[i] + " " : words[i];
    }

    private async Task Route(NetworkStream s, string method, string path, JsonObject body)
    {
        switch (path)
        {
            case "/api/show": await Whole(s, 200, new JsonObject { ["capabilities"] = new JsonArray(Capabilities.Select(c => (JsonNode)c).ToArray()), ["details"] = new JsonObject() }.ToJsonString()); return;
            case "/api/generate": Interlocked.Increment(ref _generates); await Whole(s, 200, "{\"done\":true}"); return;
            case "/api/tags": await Whole(s, 200, "{\"models\":[{\"name\":\"fake:latest\"},{\"name\":\"other-model:latest\"}]}"); return;
            case "/api/ps": await Whole(s, 200, "{\"models\":[]}"); return;
            case "/v1/models": await Whole(s, 200, "{\"data\":[{\"id\":\"fake\"},{\"id\":\"other-model\"}]}"); return;
            case "/foundry/list": await Whole(s, 200, "[]"); return;
            case "/api/chat" or "/v1/chat/completions" or "/v1/messages": break;
            default: await Whole(s, 404, "{\"error\":\"not found\"}"); return;
        }
        lock (_chats) _chats.Add((JsonObject)body.DeepClone());
        if (NextError is { } err) { NextError = null; await Whole(s, err.Status, new JsonObject { ["error"] = new JsonObject { ["message"] = err.Message } }.ToJsonString()); return; }
        // As the Claude API answers for current models (DarkCipher0x's screenshot, 2026-10-26).
        if (RefuseTemperature && body.ContainsKey("temperature")) { await Whole(s, 400, new JsonObject { ["type"] = "error", ["error"] = new JsonObject { ["type"] = "invalid_request_error", ["message"] = "`temperature` is deprecated for this model." } }.ToJsonString()); return; }
        var r = Next();
        bool stream = body["stream"]?.GetValue<bool>() == true;
        await Task.Delay(PrefillMs);
        if (!stream)
        {
            await Task.Delay(TokenMs * Tokens(r.Text).Count());
            await Whole(s, 200, path switch
            {
                "/api/chat" => new JsonObject { ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = r.Text, ["tool_calls"] = r.Calls.Length == 0 ? null : new JsonArray(r.Calls.Select(c => (JsonNode)new JsonObject { ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Args.DeepClone() } }).ToArray()) }, ["done"] = true }.ToJsonString(),
                "/v1/chat/completions" => new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = r.Text, ["tool_calls"] = r.Calls.Length == 0 ? null : new JsonArray(r.Calls.Select((c, i) => (JsonNode)new JsonObject { ["id"] = "c" + i, ["type"] = "function", ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Args.ToJsonString() } }).ToArray()) } }) }.ToJsonString(),
                _ => new JsonObject { ["content"] = new JsonArray(new[] { (JsonNode)new JsonObject { ["type"] = "text", ["text"] = r.Text } }.Concat(r.Calls.Select((c, i) => (JsonNode)new JsonObject { ["type"] = "tool_use", ["id"] = "tu" + i, ["name"] = c.Name, ["input"] = c.Args.DeepClone() })).ToArray()) }.ToJsonString(),
            });
            return;
        }
        var ch = new Chunked(s);
        switch (path)
        {
            case "/api/chat":
                await ch.Start("application/x-ndjson");
                foreach (var t in Tokens(r.Text)) { await ch.Send(new JsonObject { ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = t }, ["done"] = false }.ToJsonString() + "\n"); await Task.Delay(TokenMs); }
                if (r.Calls.Length > 0) await ch.Send(new JsonObject { ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = "", ["tool_calls"] = new JsonArray(r.Calls.Select(c => (JsonNode)new JsonObject { ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Args.DeepClone() } }).ToArray()) }, ["done"] = false }.ToJsonString() + "\n");
                await ch.Send("{\"done\":true}\n");
                break;
            case "/v1/chat/completions":
                await ch.Start("text/event-stream");
                foreach (var t in Tokens(r.Text)) { await ch.Send("data: " + new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["delta"] = new JsonObject { ["content"] = t } }) }.ToJsonString() + "\n\n"); await Task.Delay(TokenMs); }
                for (int i = 0; i < r.Calls.Length; i++)
                {
                    var args = r.Calls[i].Args.ToJsonString();
                    int half = args.Length / 2;
                    await ch.Send("data: " + new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["delta"] = new JsonObject { ["tool_calls"] = new JsonArray(new JsonObject { ["index"] = i, ["id"] = "c" + i, ["type"] = "function", ["function"] = new JsonObject { ["name"] = r.Calls[i].Name, ["arguments"] = args[..half] } }) } }) }.ToJsonString() + "\n\n");
                    await ch.Send("data: " + new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["delta"] = new JsonObject { ["tool_calls"] = new JsonArray(new JsonObject { ["index"] = i, ["function"] = new JsonObject { ["arguments"] = args[half..] } }) } }) }.ToJsonString() + "\n\n");
                }
                await ch.Send("data: [DONE]\n\n");
                break;
            default:
                await ch.Start("text/event-stream");
                await ch.Send("event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"role\":\"assistant\"}}\n\n");
                await ch.Send("event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n");
                foreach (var t in Tokens(r.Text)) { await ch.Send("event: content_block_delta\ndata: " + new JsonObject { ["type"] = "content_block_delta", ["index"] = 0, ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = t } }.ToJsonString() + "\n\n"); await Task.Delay(TokenMs); }
                for (int i = 0; i < r.Calls.Length; i++)
                {
                    var args = r.Calls[i].Args.ToJsonString();
                    int half = args.Length / 2;
                    await ch.Send("event: content_block_start\ndata: " + new JsonObject { ["type"] = "content_block_start", ["index"] = i + 1, ["content_block"] = new JsonObject { ["type"] = "tool_use", ["id"] = "tu" + i, ["name"] = r.Calls[i].Name, ["input"] = new JsonObject() } }.ToJsonString() + "\n\n");
                    await ch.Send("event: content_block_delta\ndata: " + new JsonObject { ["type"] = "content_block_delta", ["index"] = i + 1, ["delta"] = new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = args[..half] } }.ToJsonString() + "\n\n");
                    await ch.Send("event: content_block_delta\ndata: " + new JsonObject { ["type"] = "content_block_delta", ["index"] = i + 1, ["delta"] = new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = args[half..] } }.ToJsonString() + "\n\n");
                }
                await ch.Send("event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n");
                break;
        }
        await ch.End();
    }

    public void Dispose() { _cts.Cancel(); try { _l.Stop(); } catch { } }
}
