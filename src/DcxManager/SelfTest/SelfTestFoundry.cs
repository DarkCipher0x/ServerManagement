using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using DcxManager.Ai;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DcxManager.Services;

namespace DcxManager;

/// <summary>
/// The NPU engine against the Foundry Local CLI before and after `foundry service` became `foundry server`: discovery,
/// the fallback, the error texts, model-ID resolution and the lightweight CPU model. The CLI is a fake behind
/// <see cref="AiClient.FoundryCli"/>; the service is a real loopback HTTP server. Nothing is shown on screen.
/// </summary>
public static class FoundryTests
{
    // Shapes of real output: the current CLI lists its URLs (with colour codes); the old one printed one line.
    private const string NewStatus = "\x1B[32m●\x1B[0m Foundry Local daemon: running\n  PID: 4242   Uptime: 00:03:10\n  Local service URLs:\n    http://127.0.0.1:{0}\n    http://[::1]:{0}\n  Logs: C:\\Users\\demo\\.foundry\\logs\n";
    private const string OldStatus = "🟢 Model management service is running on http://127.0.0.1:{0}/openai/status\n";
    private const string NewNotRunning = "Foundry Local daemon: not running\nRun 'foundry server start' to start it.\n";
    private const string NewUnknown = "error: Unknown command: 'service'\n\nDid you mean 'server'?\n";
    private const string OldUnknown = "Unrecognized command or argument 'server'.\n";

    public static async Task RunAsync(MainWindow main, string reportDir, Action<string> ok, Action<string, Exception?> fail)
    {
        var s = AppSettings.Current;
        var saved = (s.AiEnabled, s.AiEngine, s.FoundryUrl, s.FoundryModel);
        var realCli = AiClient.FoundryCli;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        var sentModels = new List<string>();
        using var stop = new CancellationTokenSource();
        try
        {
            // 1. Parsing: the status output of both CLIs, the placeholder port, wildcard bindings, the unknown-verb answer.
            if (AiClient.FoundryUrlFrom(string.Format(NewStatus, 39839)) != "http://127.0.0.1:39839") throw new Exception("new status: " + AiClient.FoundryUrlFrom(string.Format(NewStatus, 39839)));
            if (AiClient.FoundryUrlFrom(string.Format(OldStatus, 5273)) != "http://127.0.0.1:5273") throw new Exception("old status");
            if (AiClient.FoundryUrlFrom("Uri: http://127.0.0.1:0/foundry/list\nservice on http://[::1]:5000") != "http://[::1]:5000") throw new Exception("port 0 / IPv6");
            if (AiClient.FoundryUrlFrom("listening on http://0.0.0.0:5272") != "http://127.0.0.1:5272") throw new Exception("wildcard binding");
            if (AiClient.FoundryUrlFrom(NewNotRunning) != null || AiClient.FoundryUrlFrom("see https://learn.microsoft.com/x and http://192.0.2.10:80") != null) throw new Exception("a URL out of nothing");
            if (!AiClient.UnknownCommand(NewUnknown) || !AiClient.UnknownCommand(OldUnknown) || AiClient.UnknownCommand(NewNotRunning)) throw new Exception("unknown-command detection");
            ok("foundry: status parsing (server + service CLIs)");

            // 2. Model IDs: exact IDs stay, aliases resolve NPU → GPU → CPU preferring what the server has.
            (string, string, string)[] catalog =
            {
                ("phi-4-mini", "Phi-4-mini-instruct-generic-cpu", "CPU"),
                ("phi-4-mini", "Phi-4-mini-instruct-openvino-npu", "NPU"),
                ("phi-4-mini", "Phi-4-mini-instruct-openvino-gpu", "GPU"),
                ("qwen2.5-0.5b", "qwen2.5-0.5b-instruct-generic-cpu", "CPU"),
            };
            void Pick(string wanted, string[] ids, (string, string, string)[] cat, string expect)
            {
                var got = AiClient.PickFoundryModel(wanted, ids, cat);
                if (got != expect) throw new Exception($"{wanted} → {got}, expected {expect}");
            }
            Pick("phi-4-mini", new[] { "Phi-4-mini-instruct-generic-cpu:4", "Phi-4-mini-instruct-openvino-npu:2" }, catalog, "Phi-4-mini-instruct-openvino-npu:2");
            Pick("phi-4-mini", new[] { "Phi-4-mini-instruct-generic-cpu:4" }, catalog, "Phi-4-mini-instruct-generic-cpu:4");
            Pick("phi-4-mini", Array.Empty<string>(), catalog, "Phi-4-mini-instruct-openvino-npu");
            Pick(AiClient.FoundryLightCpuModel, new[] { "Phi-4-mini-instruct-openvino-npu:2", "qwen2.5-0.5b-instruct-generic-cpu:3" }, catalog, "qwen2.5-0.5b-instruct-generic-cpu:3");
            Pick(AiClient.FoundryLightCpuModel, Array.Empty<string>(), Array.Empty<(string, string, string)>(), AiClient.FoundryLightCpuModel);
            Pick("phi-4-mini", new[] { "phi-4-mini-reasoning-generic-cpu:1", "Phi-4-mini-instruct-generic-cpu:4", "Phi-4-mini-instruct-qnn-npu:1" }, Array.Empty<(string, string, string)>(), "Phi-4-mini-instruct-qnn-npu:1");
            ok("foundry: model alias → model ID (NPU first, CPU pin kept)");

            // 2b. The default is Foundry Local with the best model for the hardware; never Ollama.
            {
                const long GB = 1L << 30;
                const string Npu = "Intel(R) AI Boost";
                string Default(string? npu, long ram) => Default2(npu, ram, 0);
                string Default2(string? npu, long ram, long vram) { var s = new AppSettings(); AiDefaults.Choose(s, npu, ram, vram); return s.AiEngine + " " + s.FoundryModel; }
                if (new AppSettings().AiEngine != "npu") throw new Exception("engine default");
                void Expect(string? npu, long ram, string model) { var got = Default(npu, ram); if (got != "npu " + model) throw new Exception($"{npu ?? "no NPU"}, {ram / GB} GB → {got}, expected {model}"); }
                Expect(Npu, 16 * GB, AiDefaults.NpuModel);
                Expect(Npu, 8 * GB, AiDefaults.NpuModel);
                Expect(null, 32 * GB, AiDefaults.LargeAlias);   // aliases since revision 3 (ADR-026): the PC picks the build
                Expect(null, 8 * GB, AiDefaults.LargeAlias);   // revision 4: phi-4-mini wherever it runs
                // revision 5 (0.5.1 #14): phi-4-mini on every PC; the lighter models are only suggested (Best).
                Expect(null, 6 * GB, AiDefaults.LargeAlias);
                Expect(null, 4 * GB, AiDefaults.LargeAlias);
                if (AiDefaults.Best(null, 6 * GB).Model != AiDefaults.MediumAlias || AiDefaults.Best(null, 4 * GB).Model != AiDefaults.LightAlias || AiDefaults.Best(null, 4 * GB, 4 * GB).Model != AiDefaults.LargeAlias)
                    throw new Exception("the hardware suggestion changed");
                // revision 6 (0.6.17, DarkCipher0x: "whatever is best suited for the PC"): the strongest tool-calling model it runs well.
                if (Default2(null, 32 * GB, 16 * GB) != "npu " + AiDefaults.BigGpuAlias || Default2(null, 16 * GB, 8 * GB) != "npu " + AiDefaults.GpuAlias
                    || Default2(Npu, 16 * GB, 0) != "npu " + AiDefaults.NpuModel || Default2(Npu, 32 * GB, 12 * GB) != "npu " + AiDefaults.BigGpuAlias || Default2(null, 16 * GB, 4 * GB) != "npu " + AiDefaults.LargeAlias)
                    throw new Exception("revision 6 defaults");
                var bigCpu = new AppSettings();
                if (!AiDefaults.Choose(bigCpu, null, 32 * GB, 0, 16) || bigCpu.FoundryModel != AiDefaults.GpuAlias) throw new Exception("a big CPU: " + bigCpu.FoundryModel);
                var smallCpu = new AppSettings();
                if (!AiDefaults.Choose(smallCpu, null, 4 * GB, 0, 4) || smallCpu.FoundryModel != AiDefaults.LargeAlias) throw new Exception("a small PC is never given a lighter model by default");
                var r5 = new AppSettings { AiDefaultChosen = true, AiDefaultRevision = 5, FoundryModel = AiDefaults.LargeAlias };
                if (!AiDefaults.Choose(r5, null, 32 * GB, 16 * GB) || r5.FoundryModel != AiDefaults.BigGpuAlias) throw new Exception("revision 5's phi-4-mini not upgraded on a 16 GB GPU: " + r5.FoundryModel);
                var mine = new AppSettings { AiDefaultChosen = true, AiDefaultRevision = 5, FoundryModel = "mistral-7b" };
                if (!AiDefaults.Choose(mine, null, 32 * GB, 16 * GB) || mine.FoundryModel != "mistral-7b") throw new Exception("a user's model was replaced");
                // Once per revision; a 0.1.5 default (lightweight CPU model) is re-chosen for the hardware; a user's choice is kept.
                var s15 = new AppSettings { AiDefaultChosen = true, AiDefaultRevision = 1, FoundryModel = AiClient.FoundryLightCpuModel };
                if (!AiDefaults.Choose(s15, Npu, 16 * GB) || s15.FoundryModel != AiDefaults.NpuModel) throw new Exception("0.1.5 default not upgraded: " + s15.FoundryModel);
                if (AiDefaults.Choose(s15, null, 4 * GB) || s15.FoundryModel != AiDefaults.NpuModel) throw new Exception("default applied twice");
                var oldDefault = new AppSettings { AiEngine = "ollama-gpu" };
                if (!AiDefaults.Choose(oldDefault, null, 16 * GB) || oldDefault.AiEngine != "npu" || oldDefault.FoundryModel != AiDefaults.LargeAlias) throw new Exception("untouched Ollama default kept");
                var chosen = new AppSettings { AiEngine = "ollama-gpu", OllamaModel = "qwen3:14b" };
                if (!AiDefaults.Choose(chosen, Npu, 16 * GB) || chosen.AiEngine != "ollama-gpu" || chosen.OllamaModel != "qwen3:14b") throw new Exception("a configured engine was overridden");
                var ownModel = new AppSettings { FoundryModel = "qwen2.5-coder-7b" };   // (qwen2.5-7b is a default since revision 6)
                if (!AiDefaults.Choose(ownModel, Npu, 16 * GB) || ownModel.FoundryModel != "qwen2.5-coder-7b") throw new Exception("a chosen Foundry model was overridden");
                if (AiEngines.All[0].Id != "npu") throw new Exception("Foundry Local isn't listed first");
                var rec = new HardwareProfile(32 * GB, 16, new[] { new GpuInfo("Demo GPU 16G", 16 * GB) }, null, false, false).Recommend();
                if (rec.Engine != "npu" || rec.Model != AiDefaults.BigGpuAlias || !rec.Why.Contains("Ollama")) throw new Exception("recommendation: " + rec);
            }
            // 2c. The NPU offer (DarkCipher0x, s45/s46): asked only when an NPU isn't used; "Remember my choice" sticks; no NPU = no
            //     question; never a window (the question is answered by a seam here).
            {
                var cur = AppSettings.Current;
                var keep = (cur.AiEnabled, cur.AiEngine, cur.FoundryUrl, cur.FoundryModel, cur.FoundryAutoSetup, cur.AiNpuSetupChoice);
                int asked = 0;
                try
                {
                    cur.AiEnabled = true; cur.AiNpuSetupChoice = "ask";
                    NpuSetupOffer.AskOverride = _ => { asked++; return (false, false); };
                    cur.AiEngine = "npu"; cur.FoundryUrl = ""; cur.FoundryModel = "phi-4-mini";
                    if (await NpuSetupOffer.RunAsync("Demo NPU") || asked != 0) throw new Exception("asked although the NPU is used (first-run default)");
                    cur.AiEngine = "ollama-gpu";
                    asked = 0;
                    if (await NpuSetupOffer.RunAsync("Demo NPU") || asked != 1 || cur.AiEngine != "ollama-gpu") throw new Exception("'Not now' changed the engine");
                    NpuSetupOffer.AskOverride = _ => { asked++; return (false, true); };
                    await NpuSetupOffer.RunAsync("Demo NPU");
                    if (cur.AiNpuSetupChoice != "never" || await NpuSetupOffer.RunAsync("Demo NPU") || asked != 2) throw new Exception("'never' not remembered");
                    cur.AiNpuSetupChoice = "ask";
                    NpuSetupOffer.AskOverride = _ => { asked++; return (true, true); };
                    if (!await NpuSetupOffer.RunAsync("Demo NPU") || cur.AiEngine != "npu" || cur.FoundryModel != AiDefaults.NpuModel || cur.AiNpuSetupChoice != "always") throw new Exception("yes + remember");
                    cur.AiEngine = "ollama-gpu"; asked = 0;
                    if (!await NpuSetupOffer.RunAsync("Demo NPU") || asked != 0 || cur.AiEngine != "npu") throw new Exception("'always' asked again");
                }
                finally
                {
                    NpuSetupOffer.AskOverride = null;
                    (cur.AiEnabled, cur.AiEngine, cur.FoundryUrl, cur.FoundryModel, cur.FoundryAutoSetup, cur.AiNpuSetupChoice) = keep;
                    AppSettings.Save();
                }
                ok("NPU offer: no question when the NPU is already used (the first-run default); 'Not now' changes nothing; 'Remember my choice' stores never / always; yes switches to Foundry Local + phi-4-mini on the NPU; no window");
            }
            ok("AI default: Foundry Local with the best model for the hardware (phi-4-mini on the NPU first, then a 4 GB+ GPU, then 8 GB+ RAM; lighter models below), once per revision, never over a user's choice");

            // 3. A real loopback service: /v1/models, /foundry/list and chat, recording the model each chat asked for.
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _ = Task.Run(() => ServeAsync(listener, sentModels, stop.Token));

            // The fake CLI: "new" knows only `server`, "old" only `service`; "down" = installed but not running; "none" = not installed.
            var mode = "new";
            var calls = new List<string>();
            AiClient.FoundryCli = (args, _, _) =>
            {
                lock (calls) calls.Add(args);
                var verb = args.Split(' ')[0];
                (int, string) r = mode switch
                {
                    "none" => (-1, ""),
                    _ when args == "--version" => (0, "0.11.0"),   // what the real 0.11 CLI prints
                    "new" when verb == "service" => (1, NewUnknown),
                    "old" when verb == "server" => (1, OldUnknown),
                    "down" when verb == "service" => (1, NewUnknown),
                    "down" => (args.EndsWith("status") ? 1 : 0, NewNotRunning),
                    _ when args.EndsWith("status") => (0, string.Format(mode == "old" ? OldStatus : NewStatus, port)),
                    _ => (0, "started"),
                };
                return Task.FromResult(r);
            };
            s.AiEnabled = true; s.AiEngine = "npu"; s.FoundryUrl = ""; s.FoundryModel = "phi-4-mini";

            async Task<string> Discover(string m) { mode = m; lock (calls) calls.Clear(); AiClient.ForgetFoundry(); return await AiClient.FoundryBaseAsync(); }
            async Task<string> DiscoverError(string m) { try { await Discover(m); return "(no error)"; } catch (InvalidOperationException e) { return e.Message; } }
            var expected = $"http://127.0.0.1:{port}";

            if (await Discover("new") != expected || calls.Count != 1 || calls[0] != "server status") throw new Exception("new CLI: " + string.Join(" | ", calls));
            // Chat through the discovered service: the alias goes out as the NPU model ID the server lists.
            var reply = await AiClient.ChatAsync(new JsonArray { new JsonObject { ["role"] = "user", ["content"] = "Reply with the single word OK." } }, null, null, new CancellationTokenSource(15000).Token);
            if ((reply["content"]?.GetValue<string>() ?? "") != "OK") throw new Exception("chat reply: " + reply.ToJsonString());
            if (sentModels.LastOrDefault() != "Phi-4-mini-instruct-openvino-npu:2") throw new Exception("chat model: " + sentModels.LastOrDefault());
            if (!(await AiClient.ModelsAsync()).Contains(AiClient.FoundryLightCpuModel + ":3")) throw new Exception("Load models doesn't list model IDs");
            // The lightweight CPU model is used exactly as set (never swapped for the NPU or GPU build).
            s.FoundryModel = AiClient.FoundryLightCpuModel; AiClient.ForgetFoundry();
            await AiClient.ChatAsync(new JsonArray { new JsonObject { ["role"] = "user", ["content"] = "ping" } }, null, null, new CancellationTokenSource(15000).Token);
            if (sentModels.LastOrDefault() != AiClient.FoundryLightCpuModel + ":3") throw new Exception("light CPU model: " + sentModels.LastOrDefault());
            ok("foundry: new CLI discovery (`foundry server status`) + chat with the model ID + lightweight CPU model");

            // Settings → AI engine: the Foundry Local panel (ADR-026) fills in what's true on opening (local commands only;
            // this older CLI has no JSON, so the text fallback is used).
            main.Navigate("dashboard"); await Task.Delay(200);
            main.Navigate("settings");
            Views.FoundryPanel? panel = null;
            for (int i = 0; i < 50 && panel == null; i++) { await Task.Delay(100); panel = Find<Views.FoundryPanel>(main, _ => true); }
            if (panel == null) throw new Exception("no Foundry Local panel in Settings");
            string Line(string start) => panel.Children.OfType<TextBlock>().FirstOrDefault(t => t.Text.StartsWith(start))?.Text ?? "";
            for (int i = 0; i < 60 && Line("✓ The service").Length == 0; i++) await Task.Delay(100);
            if (Line("✓ Foundry Local") != "✓ Foundry Local 0.11.0 is installed." || !Line("✓ The service").Contains($"127.0.0.1:{port}"))
                throw new Exception($"panel status: {string.Join(" / ", panel.Children.OfType<TextBlock>().Select(t => t.Text).Where(t => t.Length > 0))}");
            if (!panel.Children.OfType<WrapPanel>().SelectMany(w => w.Children.OfType<Button>()).Select(b => b.Content?.ToString()).SequenceEqual(new[] { "Set up now", "Restart service", "Stop service", "Unload model", "Show log", "Copy the commands" }))
                throw new Exception("manage buttons");
            panel.UpdateLayout();
            var shotDir = Directory.CreateDirectory(Path.Combine(reportDir, "shots")).FullName;
            Render(panel, Path.Combine(shotDir, "foundry-steps.png"));
            ok("foundry: Settings shows Foundry Local's status on opening (installed, service answering) with the manage buttons; rendered foundry-steps.png");

            if (await Discover("old") != expected || string.Join(" | ", calls) != "server status | service status") throw new Exception("old CLI: " + string.Join(" | ", calls));
            ok("foundry: falls back to `foundry service status` on an older CLI");

            var none = await DiscoverError("none");
            var down = await DiscoverError("down");
            if (!none.Contains("winget install Microsoft.FoundryLocal")) throw new Exception("not installed: " + none);
            if (!down.Contains("foundry server start") || down.Contains("service start")) throw new Exception("not running: " + down);
            mode = "old"; lock (calls) calls.Clear();
            var start = await AiClient.FoundryServerAsync("start --idle-timeout 0", "start", TimeSpan.FromSeconds(5));
            if (start.Code != 0 || start.Verb != "service" || string.Join(" | ", calls) != "server start --idle-timeout 0 | service start") throw new Exception("old start: " + string.Join(" | ", calls));
            mode = "new"; lock (calls) calls.Clear();
            start = await AiClient.FoundryServerAsync("start --idle-timeout 0", "start", TimeSpan.FromSeconds(5));
            if (start.Code != 0 || start.Verb != "server" || string.Join(" | ", calls) != "server start --idle-timeout 0") throw new Exception("new start: " + string.Join(" | ", calls));
            ok("foundry: error texts + start button (`server start --idle-timeout 0`, `service start` fallback)");
        }
        catch (Exception ex) { fail("foundry local engine", ex); }
        finally
        {
            AiClient.FoundryCli = realCli;
            (s.AiEnabled, s.AiEngine, s.FoundryUrl, s.FoundryModel) = saved;
            AiClient.ForgetFoundry();
            stop.Cancel();
            try { listener.Stop(); } catch { }
        }

        // 4. A real CLI whose child keeps the output pipe open (as a daemon started by `foundry server start` may):
        //    RunLocalAsync must return when the CLI exits, not when the child does.
        try
        {
            var sw = Stopwatch.StartNew();
            var (code, output) = await AiClient.RunLocalAsync("cmd.exe", "/d /c \"start /b ping -n 9 127.0.0.1 & echo started\"", TimeSpan.FromSeconds(30));
            if (code != 0 || !output.Contains("started")) throw new Exception($"exit {code}: {output}");
            if (sw.Elapsed > TimeSpan.FromSeconds(6)) throw new Exception($"waited {sw.Elapsed.TotalSeconds:0.0}s for the child");
            ok("local CLI runner returns when a daemon child holds the pipe");
        }
        catch (Exception ex) { fail("local CLI runner", ex); }
    }

    private static T? Find<T>(DependencyObject root, Func<T, bool> match) where T : DependencyObject
    {
        if (root is T t && match(t)) return t;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            if (Find(child, match) is { } hit) return hit;
        return null;
    }

    private static void Render(FrameworkElement e, string path)
    {
        int w = Math.Max(1, (int)Math.Ceiling(e.ActualWidth)), h = Math.Max(1, (int)Math.Ceiling(e.ActualHeight));
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        var bg = new DrawingVisual();
        using (var dc = bg.RenderOpen())
        {
            dc.DrawRectangle(Application.Current.TryFindResource("Bg") as Brush ?? Brushes.Black, null, new Rect(0, 0, w, h));
            dc.DrawRectangle(new VisualBrush(e), null, new Rect(0, 0, w, h));
        }
        rtb.Render(bg);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var f = File.Create(path);
        enc.Save(f);
    }

    private static async Task ServeAsync(TcpListener listener, List<string> sentModels, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await listener.AcceptTcpClientAsync(ct); } catch { return; }
            _ = Task.Run(async () =>
            {
                using (c)
                    try
                    {
                        var st = c.GetStream();
                        var buf = new byte[65536]; int len = 0, headerEnd = -1;
                        while (headerEnd < 0 && len < buf.Length)
                        {
                            int n = await st.ReadAsync(buf.AsMemory(len), ct); if (n == 0) return; len += n;
                            headerEnd = Encoding.ASCII.GetString(buf, 0, len).IndexOf("\r\n\r\n", StringComparison.Ordinal);
                        }
                        var head = Encoding.ASCII.GetString(buf, 0, headerEnd);
                        var cl = System.Text.RegularExpressions.Regex.Match(head, @"(?im)^content-length:\s*(\d+)");
                        int bodyLen = cl.Success ? int.Parse(cl.Groups[1].Value) : 0;
                        while (len < headerEnd + 4 + bodyLen) { int n = await st.ReadAsync(buf.AsMemory(len), ct); if (n == 0) break; len += n; }
                        var path = head.Split(' ')[1];
                        string body;
                        if (path.StartsWith("/v1/models"))
                            body = """{"object":"list","data":[{"id":"Phi-4-mini-instruct-generic-cpu:4"},{"id":"Phi-4-mini-instruct-openvino-npu:2"},{"id":"qwen2.5-0.5b-instruct-generic-cpu:3"}]}""";
                        else if (path.StartsWith("/foundry/list"))
                            body = """[{"name":"Phi-4-mini-instruct-openvino-npu","alias":"phi-4-mini","runtime":{"deviceType":"NPU"}},{"name":"Phi-4-mini-instruct-generic-cpu","alias":"phi-4-mini","runtime":{"deviceType":"CPU"}}]""";
                        else if (path.StartsWith("/v1/chat/completions"))
                        {
                            var req = JsonNode.Parse(Encoding.UTF8.GetString(buf, headerEnd + 4, Math.Min(bodyLen, len - headerEnd - 4)));
                            lock (sentModels) sentModels.Add(req?["model"]?.GetValue<string>() ?? "");
                            body = """{"choices":[{"index":0,"message":{"role":"assistant","content":"OK"},"finish_reason":"stop"}]}""";
                        }
                        else body = "{}";
                        var bytes = Encoding.UTF8.GetBytes(body);
                        var resp = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
                        await st.WriteAsync(Encoding.ASCII.GetBytes(resp), ct);
                        await st.WriteAsync(bytes, ct);
                    }
                    catch { }
            });
        }
    }
}
