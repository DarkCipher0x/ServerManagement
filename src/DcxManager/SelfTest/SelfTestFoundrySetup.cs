using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DcxManager.Ai;
using DcxManager.Services;
using DcxManager.Views;

namespace DcxManager;

/// <summary>
/// ADR-026 Foundry Local hands-off set-up. The CLI, winget, the health probe and "end a process" are fakes (the self-test
/// never touches the real Foundry Local); the JSON they answer with is copied from a real Foundry Local 0.11 run. Covers:
/// parsing, choosing a build this PC offers, the recommendation per hardware, the full pipeline from "not installed" with
/// a sick service and a model load that fails like DarkCipher0x's (exit 4) — repaired automatically — the offline / battery gates,
/// the chat path repairing itself once, and the Settings model guide (rendered). Nothing is shown on screen.
/// </summary>
public static class FoundrySetupTests
{
    // Real Foundry Local 0.11 output (this PC, 2026-10-18), trimmed to a few models.
    private const string RealVariants = """{"variants":[{"alias":"phi-4-mini","variantName":"Phi-4-mini-instruct-generic-gpu","variantId":"Phi-4-mini-instruct-generic-gpu:5","type":"Chat","device":"Gpu","executionProvider":"WebGPUExecutionProvider","fileSizeMb":3809,"cached":true,"license":"MIT"},{"alias":"phi-4-mini","variantName":"Phi-4-mini-instruct-generic-cpu","variantId":"Phi-4-mini-instruct-generic-cpu:5","type":"Chat","device":"Cpu","executionProvider":"CPUExecutionProvider","fileSizeMb":4915,"cached":false,"license":"MIT"},{"alias":"qwen2.5-0.5b","variantName":"qwen2.5-0.5b-instruct-generic-cpu","variantId":"qwen2.5-0.5b-instruct-generic-cpu:4","type":"Chat","device":"Cpu","executionProvider":"CPUExecutionProvider","fileSizeMb":822,"cached":true,"license":"apache-2.0"},{"alias":"qwen2.5-7b","variantName":"qwen2.5-7b-instruct-generic-gpu","variantId":"qwen2.5-7b-instruct-generic-gpu:4","type":"Chat","device":"Gpu","executionProvider":"WebGPUExecutionProvider","fileSizeMb":5324,"cached":false,"license":"apache-2.0"},{"alias":"qwen2.5-1.5b","variantName":"qwen2.5-1.5b-instruct-generic-cpu","variantId":"qwen2.5-1.5b-instruct-generic-cpu:4","type":"Chat","device":"Cpu","executionProvider":"CPUExecutionProvider","fileSizeMb":1822,"cached":false,"license":"apache-2.0"},{"alias":"deepseek-r1-7b","variantName":"deepseek-r1-distill-qwen-7b-generic-gpu","variantId":"deepseek-r1-distill-qwen-7b-generic-gpu:4","type":"Chat","device":"Gpu","executionProvider":"WebGPUExecutionProvider","fileSizeMb":5713,"cached":false,"license":"MIT"}]}""";
    private const string RealLoaded = """{"models":[{"alias":"qwen2.5-0.5b","id":"qwen2.5-0.5b-instruct-generic-cpu:4","displayName":"qwen2.5-0.5b-instruct-generic-cpu","type":"Chat","device":"Cpu","fileSizeMb":822,"cached":true,"license":"apache-2.0","supportsToolCalling":true}]}""";
    private const string RealError = """{"error":{"code":"MODEL_NOT_FOUND","message":"No model matching 'no-such-model-xyz' was found in the catalog.","hint":"Run 'foundry model list' to see available models."}}""";
    // A synthetic Copilot+ (ARM64, NPU) catalogue: the same models, other builds — the x64 CPU ids are NOT offered.
    private const string NpuVariants = """{"variants":[{"alias":"phi-4-mini","variantId":"Phi-4-mini-instruct-qnn-npu:1","type":"Chat","device":"Npu","fileSizeMb":2700,"cached":false},{"alias":"phi-4-mini","variantId":"Phi-4-mini-instruct-generic-cpu-arm64:1","type":"Chat","device":"Cpu","fileSizeMb":4900,"cached":false},{"alias":"qwen2.5-0.5b","variantId":"qwen2.5-0.5b-instruct-qnn-npu:1","type":"Chat","device":"Npu","fileSizeMb":600,"cached":true},{"alias":"qwen2.5-1.5b","variantId":"qwen2.5-1.5b-instruct-qnn-npu:1","type":"Chat","device":"Npu","fileSizeMb":1500,"cached":false}]}""";

    public static async Task RunAsync(MainWindow main, string reportDir, Action<string> ok, Action<string, Exception?> fail)
    {
        const long GB = 1L << 30;
        var s = AppSettings.Current;
        var saved = (s.AiEnabled, s.AiEngine, s.FoundryUrl, s.FoundryModel, s.FoundryAutoSetup, s.OfflineMode);
        var (realCli, realWinget, realProbe, realEnd, realBattery) = (AiClient.FoundryCli, FoundryManager.Winget, FoundryManager.Probe, FoundryManager.EndDaemon, FoundryManager.OnBattery);

        // 1. Parsing real output; errors say what went wrong, not just the hint.
        try
        {
            var v = FoundryManager.ParseCatalog(JsonNode.Parse(RealVariants)!);
            if (v.Count != 6 || v.Count(m => m.Device == "gpu") != 3 || !v.Single(m => m.Id == "qwen2.5-0.5b-instruct-generic-cpu:4").Cached || v.Single(m => m.Alias == "qwen2.5-7b").SizeMb != 5324) throw new Exception("variants");
            if (!v.Single(m => m.Id.StartsWith("Phi-4-mini-instruct-generic-gpu")).Tools || v.Single(m => m.Alias == "deepseek-r1-7b").Tools) throw new Exception("tool-capable families");
            var loaded = FoundryManager.ParseCatalog(JsonNode.Parse(RealLoaded)!);
            if (loaded.Single().Bare != "qwen2.5-0.5b-instruct-generic-cpu" || !loaded[0].Tools) throw new Exception("loaded list");
            var withNoise = "■ Loading model (x, 822 MB)\n\x1B[32m●\x1B[0m " + RealError;
            var r = new FoundryManager.CliResult(3, FoundryManager.ParseJson(withNoise), withNoise);
            if (r.ErrorCode != "MODEL_NOT_FOUND" || !r.Error.Contains("No model matching") || !r.Error.Contains("foundry model list")) throw new Exception("error: " + r.Error);
            var text = FoundryManager.Failure("Loading…\nerror: the daemon did not respond\nHint: Run 'foundry server status' to inspect, or 'foundry server logs' for details.", 4);
            if (!text.Contains("daemon did not respond") || !text.Contains("Hint")) throw new Exception("text failure: " + text);
            if (FoundryManager.BaseName("qwen2.5-0.5b-instruct-generic-cpu:4") != "qwen2.5-0.5b-instruct" || FoundryManager.BaseName("Phi-4-mini-instruct-qnn-npu:1") != "phi-4-mini-instruct") throw new Exception("base names");
            ok("foundry set-up: real Foundry Local 0.11 JSON parsed (catalogue builds per device, sizes, downloaded, tools, loaded); errors show Foundry's message + code + hint (not just the hint), text output keeps its error lines");
        }
        catch (Exception ex) { fail("foundry set-up parsing", ex); }

        // 2. Which build, on which PC; the recommendation per hardware.
        try
        {
            var x64 = FoundryManager.ParseCatalog(JsonNode.Parse(RealVariants)!);
            var arm = FoundryManager.ParseCatalog(JsonNode.Parse(NpuVariants)!);
            void Is(string wanted, IReadOnlyList<FoundryModel> cat, string? expect) { var got = FoundryManager.Resolve(wanted, cat)?.Bare; if (got != expect) throw new Exception($"{wanted} → {got ?? "none"}, expected {expect ?? "none"}"); }
            Is("Phi-4-mini-instruct-generic-cpu", x64, "Phi-4-mini-instruct-generic-cpu");             // an exact build stays (a deliberate CPU pick)
            Is("phi-4-mini", x64, "Phi-4-mini-instruct-generic-gpu");                                // alias → GPU before CPU here
            Is("qwen2.5-0.5b-instruct-generic-cpu", arm, "qwen2.5-0.5b-instruct-qnn-npu");           // an x64 id from another PC → this PC's build
            Is("Phi-4-mini-instruct-generic-cpu", arm, "Phi-4-mini-instruct-qnn-npu");
            Is("phi-4-mini", arm, "Phi-4-mini-instruct-qnn-npu");
            Is("no-such-model", x64, null);
            string Rec(IReadOnlyList<FoundryModel> cat, long ram, long vram, bool npu) => FoundryManager.Recommend(cat, ram, vram, npu)?.Model.Bare ?? "none";
            if (Rec(arm, 16 * GB, 0, true) != "Phi-4-mini-instruct-qnn-npu") throw new Exception("NPU PC: " + Rec(arm, 16 * GB, 0, true));
            if (Rec(x64, 32 * GB, 16 * GB, false) != "qwen2.5-7b-instruct-generic-gpu") throw new Exception("16 GB GPU (no 14B in this catalogue → 7B): " + Rec(x64, 32 * GB, 16 * GB, false));
            if (Rec(x64, 32 * GB, 8 * GB, false) != "qwen2.5-7b-instruct-generic-gpu") throw new Exception("8 GB GPU");
            if (Rec(x64, 32 * GB, 4 * GB, false) != "Phi-4-mini-instruct-generic-gpu") throw new Exception("4 GB GPU");
            if (Rec(x64, 16 * GB, 0, false) != "Phi-4-mini-instruct-generic-cpu") throw new Exception("16 GB CPU");
            if (Rec(x64, 8 * GB, 0, false) != "Phi-4-mini-instruct-generic-cpu") throw new Exception("8 GB CPU");
            if (Rec(x64, 6 * GB, 0, false) != "qwen2.5-1.5b-instruct-generic-cpu") throw new Exception("6 GB CPU");
            if (Rec(x64, 4 * GB, 0, false) != "qwen2.5-0.5b-instruct-generic-cpu") throw new Exception("4 GB CPU");
            if (FoundryManager.Recommend(x64, 32 * GB, 16 * GB, false)!.Value.Model.Alias == "deepseek-r1-7b") throw new Exception("a model without tools recommended");
            // The default is now an alias, so it means the right build on any PC.
            string Default(string? npu, long ram) { var d = new AppSettings(); AiDefaults.Choose(d, npu, ram); return d.FoundryModel; }
            if (Default("Intel(R) AI Boost", 16 * GB) != "phi-4-mini" || Default(null, 32 * GB) != "phi-4-mini" || Default(null, 8 * GB) != "phi-4-mini" || Default(null, 6 * GB) != "phi-4-mini" || Default(null, 4 * GB) != "phi-4-mini") throw new Exception("defaults");
            var r2 = new AppSettings { AiDefaultChosen = true, AiDefaultRevision = 2, FoundryModel = AiDefaults.CpuLargeModel };
            if (!AiDefaults.Choose(r2, null, 32 * GB) || r2.FoundryModel != "phi-4-mini") throw new Exception("0.1.6 id default not moved to the alias: " + r2.FoundryModel);
            ok("foundry models: a build this PC offers is chosen (an x64 CPU id from another PC loads the NPU build on a Copilot+ PC; an exact CPU pick is kept); recommendation per hardware — a 6 GB+ GPU → Qwen2.5 7B (14B from 12 GB when offered), NPU → phi-4-mini on the NPU, a 4 GB GPU → phi-4-mini, CPU by RAM (8 GB+ phi-4-mini / 6 / 4 GB) — never a model without tools; defaults are aliases");
        }
        catch (Exception ex) { fail("foundry model choice", ex); }

        // 3. The hands-off pipeline, from nothing: install → start → sick service repaired (restart doesn't help → stop + end
        //    the daemon + start) → x64 model id resolved to this PC's NPU build → download → load fails like DarkCipher0x's exit 4 →
        //    repaired → loaded → verified.
        var st = new Fake();
        try
        {
            s.AiEnabled = true; s.AiEngine = "npu"; s.FoundryUrl = ""; s.FoundryAutoSetup = true; s.OfflineMode = false;
            s.FoundryModel = "Phi-4-mini-instruct-generic-cpu";   // what 0.1.6 chose on an x64 PC, then copied to a Copilot+ PC
            st.WorstCase();
            var since = DateTime.Now.AddSeconds(-1);
            var lines = new List<string>();
            void P(string l) { lock (lines) lines.Add(l); }
            FoundryManager.Progress += P;
            bool okRun;
            try { okRun = await FoundryManager.EnsureReadyAsync(byUser: false); }
            finally { FoundryManager.Progress -= P; }
            var trace = string.Join(" | ", FoundryManager.Trace);
            if (!okRun) throw new Exception("not ready: " + FoundryManager.LastStatus + " — " + trace);
            if (!FoundryManager.LastStatus.StartsWith("✓ Ready") || !FoundryManager.LastStatus.Contains("phi-4-mini") || !FoundryManager.LastStatus.Contains("NPU")) throw new Exception("status: " + FoundryManager.LastStatus);
            if (!st.Installed || st.WingetCalls != 1) throw new Exception("install");
            if (st.Ended != 4242) throw new Exception("the stuck daemon wasn't ended (pid " + st.Ended + ") — " + trace);
            if (!trace.Contains("server restart") || !trace.Contains("server stop")) throw new Exception("repair steps: " + trace);
            if (!st.Downloads.SequenceEqual(new[] { "Phi-4-mini-instruct-qnn-npu:1" })) throw new Exception("download: " + string.Join(",", st.Downloads));
            if (st.Loads.Count != 2 || st.Loads.Any(l => l != "Phi-4-mini-instruct-qnn-npu:1")) throw new Exception("loads: " + string.Join(",", st.Loads));
            if (!st.Loaded) throw new Exception("not loaded at the end");
            if (s.FoundryModel != "Phi-4-mini-instruct-generic-cpu") throw new Exception("the setting was rewritten although the model resolved: " + s.FoundryModel);
            if (!lines.Any(l => l.Contains("repairing")) || !lines.Any(l => l.Contains("downloading") && l.Contains("2.6 GB"))) throw new Exception("progress lines: " + string.Join(" / ", lines));
            var audit = AuditLog.Read(DateTime.Now.ToString("yyyy-MM")).Where(a => a.Time >= since && a.Target == "Foundry Local").Select(a => a.Action).ToList();
            if (!audit.Any(a => a.Contains("installed Foundry Local")) || !audit.Any(a => a.Contains("downloaded model")) || !audit.Any(a => a.Contains("restarted the service")) || !audit.Any(a => a.Contains("ended a stuck")))
                throw new Exception("audit: " + string.Join(", ", audit));
            ok($"foundry hands-off set-up: from not installed → winget install → started → a service that said Ready but didn't answer was repaired (restart, then stop + end the stuck daemon + start) → the x64 model id resolved to this PC's NPU build → downloaded (2.6 GB) → a load that failed with exit 4 was repaired and retried → loaded and verified; every install/download/repair audited ({FoundryManager.Trace.Count} commands)");
        }
        catch (Exception ex) { fail("foundry hands-off set-up", ex); }

        // 4. Gates: offline mode never installs or downloads; on battery an automatic run doesn't start a big download;
        //    a model this PC doesn't offer is replaced by the recommendation (and the setting says so).
        try
        {
            st.Reset(installed: false);
            s.OfflineMode = true;
            if (await FoundryManager.EnsureReadyAsync(byUser: true) || st.WingetCalls != 0 || !FoundryManager.LastStatus.Contains("offline")) throw new Exception("offline install: " + FoundryManager.LastStatus);
            st.Reset(installed: true); st.Running = true; st.Sick = false;
            if (await FoundryManager.EnsureReadyAsync(byUser: true) || st.Downloads.Count != 0 || !FoundryManager.LastStatus.Contains("offline")) throw new Exception("offline download: " + FoundryManager.LastStatus);
            s.OfflineMode = false;
            FoundryManager.OnBattery = () => true;
            st.Reset(installed: true); st.Running = true; st.Sick = false;
            if (await FoundryManager.EnsureReadyAsync(byUser: false) || st.Downloads.Count != 0 || !FoundryManager.LastStatus.Contains("mains power")) throw new Exception("battery: " + FoundryManager.LastStatus);
            st.Reset(installed: true); st.Running = true; st.Sick = false;
            if (!await FoundryManager.EnsureReadyAsync(byUser: true) || st.Downloads.Count != 1) throw new Exception("a button press on battery should download: " + FoundryManager.LastStatus);
            FoundryManager.OnBattery = realBattery;
            st.Reset(installed: true); st.Running = true; st.Sick = false;
            s.FoundryModel = "llama-9000";
            var said = new List<string>();
            void P2(string l) => said.Add(l);
            FoundryManager.Progress += P2;
            try { if (!await FoundryManager.EnsureReadyAsync(byUser: true)) throw new Exception("unknown model: " + FoundryManager.LastStatus); }
            finally { FoundryManager.Progress -= P2; }
            if (s.FoundryModel != "phi-4-mini" || !said.Any(l => l.Contains("llama-9000 isn't offered for this PC"))) throw new Exception("not replaced/explained: " + s.FoundryModel + " · " + string.Join(" / ", said));
            ok("foundry gates: offline mode never installs or downloads (it says why); on battery the automatic set-up waits for mains before a big download (a button press doesn't); a model this PC doesn't offer is replaced by the recommendation and the status says so");
        }
        catch (Exception ex) { fail("foundry set-up gates", ex); }
        finally { s.OfflineMode = saved.OfflineMode; FoundryManager.OnBattery = realBattery; }

        // 5. The chat path repairs itself once: the service is down when a question is asked.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        using var stop = new CancellationTokenSource();
        try
        {
            listener.Start();
            st.Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _ = Task.Run(() => Serve(listener, stop.Token));
            FoundryManager.Probe = realProbe;   // a real request to the loopback service
            st.Reset(installed: true); st.Running = false; st.Sick = false; st.Loaded = true; st.CachedAll = true;
            s.FoundryModel = "phi-4-mini";
            AiClient.ForgetFoundry();
            FoundryManager.AllowInSelfTest = true;
            var reply = await AiClient.ChatAsync(new JsonArray { new JsonObject { ["role"] = "user", ["content"] = "Reply with the single word OK." } }, null, null, new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token);
            if ((reply["content"]?.GetValue<string>() ?? "").Trim() != "OK") throw new Exception("reply: " + reply.ToJsonString());
            if (!FoundryManager.Trace.Any(t => t.StartsWith("server start"))) throw new Exception("not repaired: " + string.Join(" | ", FoundryManager.Trace));
            // Switched off: the error comes back as it is, nothing is started.
            s.FoundryAutoSetup = false; st.Running = false; AiClient.ForgetFoundry(); lock (FoundryManager.Trace) FoundryManager.Trace.Clear();
            try { await AiClient.ChatAsync(new JsonArray { new JsonObject { ["role"] = "user", ["content"] = "x" } }, null, null, new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token); throw new Exception("answered with the service down"); }
            catch (InvalidOperationException e) when (e.Message.Contains("isn't running")) { }
            if (FoundryManager.Trace.Count != 0) throw new Exception("set-up ran although switched off");
            ok("foundry chat path: a question asked while the service is down repairs it once (start, verify) and is answered from a real loopback service; with automatic set-up off the error is returned untouched and nothing is started");
        }
        catch (Exception ex) { fail("foundry chat repair", ex); }
        finally { FoundryManager.AllowInSelfTest = false; stop.Cancel(); listener.Stop(); }

        // 6. The Settings model guide, from a catalogue (rendered, no window).
        try
        {
            string model = "phi-4-mini";
            var panel = new FoundryPanel(() => model, m => model = m);
            var cat = FoundryManager.ParseCatalog(JsonNode.Parse(RealVariants)!);
            panel.ShowModels(cat, FoundryManager.Recommend(cat, 32 * GB, 16 * GB, false));
            var host = new System.Windows.Controls.Border { Child = panel, Width = 860 };
            host.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "Bg");
            host.SetResourceReference(TextElement.ForegroundProperty, "Fg");
            host.Measure(new Size(860, double.PositiveInfinity)); host.Arrange(new Rect(host.DesiredSize)); host.UpdateLayout();
            var bmp = new RenderTargetBitmap((int)host.ActualWidth, (int)host.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(host);
            Directory.CreateDirectory(Path.Combine(reportDir, "shots"));
            var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp));
            using (var f = File.Create(Path.Combine(reportDir, "shots", "foundry-models.png"))) enc.Save(f);
            var all = string.Join("\n", Texts(panel));
            if (!all.Contains("★ Recommended for this PC") || !all.Contains("qwen2.5-7b · GPU · 5.2 GB") || !all.Contains("CPU — runs anywhere") || !all.Contains("no tools") || !all.Contains("Lightest"))
                throw new Exception("guide text: " + all);
            host.Child = null;
            ok("foundry model guide: built from this PC's catalogue — recommendation on top with the reason, grouped NPU / GPU / CPU, size, downloaded, tool use and what each is good for (shots\\foundry-models.png)");
        }
        catch (Exception ex) { fail("foundry model guide", ex); }
        finally
        {
            (AiClient.FoundryCli, FoundryManager.Winget, FoundryManager.Probe, FoundryManager.EndDaemon, FoundryManager.OnBattery) = (realCli, realWinget, realProbe, realEnd, realBattery);
            (s.AiEnabled, s.AiEngine, s.FoundryUrl, s.FoundryModel, s.FoundryAutoSetup, s.OfflineMode) = saved;
            AiClient.ForgetFoundry();
        }
    }

    private static IEnumerable<string> Texts(DependencyObject o)
    {
        if (o is System.Windows.Controls.TextBlock t) yield return t.Text;
        if (o is System.Windows.Controls.ContentControl { Content: DependencyObject c }) foreach (var x in Texts(c)) yield return x;
        if (o is System.Windows.Controls.Panel p) foreach (UIElement ch in p.Children) foreach (var x in Texts(ch)) yield return x;
    }

    /// <summary>A loopback stand-in for the service: /v1/models and chat.</summary>
    private static async Task Serve(TcpListener l, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await l.AcceptTcpClientAsync(ct); } catch { return; }
            _ = Task.Run(async () =>
            {
                using (c)
                {
                    var ns = c.GetStream();
                    var buf = new byte[65536]; int n = await ns.ReadAsync(buf, ct);
                    var req = Encoding.UTF8.GetString(buf, 0, n);
                    var path = req.Split(' ').ElementAtOrDefault(1) ?? "";
                    string body = path.StartsWith("/v1/models") ? """{"data":[{"id":"Phi-4-mini-instruct-qnn-npu:1"}]}"""
                        : path.StartsWith("/v1/chat") ? """{"choices":[{"message":{"role":"assistant","content":"OK"}}]}"""
                        : path.StartsWith("/foundry/list") ? "[]" : "{}";
                    var bytes = Encoding.UTF8.GetBytes(body);
                    await ns.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"), ct);
                    await ns.WriteAsync(bytes, ct);
                }
            }, ct);
        }
    }

    /// <summary>A scripted Foundry Local: installed / running / sick / cached / loaded, answering like the 0.11 CLI.</summary>
    private sealed class Fake
    {
        public bool Installed, Running, Sick, Loaded, CachedAll, RestartFixes, LoadFailsOnce;
        public int WingetCalls, Ended, Port = 1;
        public List<string> Downloads = new(), Loads = new();
        private readonly HashSet<string> _cached = new();

        public void Reset(bool installed)
        {
            Installed = installed; Running = false; Sick = false; Loaded = false; CachedAll = false; RestartFixes = true; LoadFailsOnce = false;
            WingetCalls = 0; Ended = 0; Downloads.Clear(); Loads.Clear(); _cached.Clear();
            Hook();
        }

        /// <summary>The worst case: not installed; once started the service doesn't answer and a restart doesn't fix it;
        /// the first model load fails like exit 4.</summary>
        public void WorstCase()
        {
            Reset(installed: false);
            Sick = true; RestartFixes = false; LoadFailsOnce = true;
        }

        private void Hook()
        {
            FoundryManager.Winget = (_, _, _) => { WingetCalls++; Installed = true; return Task.FromResult((0, "Successfully installed")); };
            FoundryManager.Probe = (_, _) => Task.FromResult(Running && !Sick);
            FoundryManager.EndDaemon = pid => { Ended = pid; Running = false; Sick = false; return true; };
            AiClient.FoundryCli = (args, _, _) => Task.FromResult(Answer(args));
        }

        private string Url => $"http://127.0.0.1:{Port}";
        private string Variants()
        {
            var j = JsonNode.Parse(NpuVariants)!;
            foreach (var v in j["variants"]!.AsArray().OfType<JsonObject>())
                if (CachedAll || _cached.Contains(v["variantId"]!.ToString())) v["cached"] = true;
            return j.ToJsonString();
        }

        private (int, string) Answer(string args)
        {
            if (!Installed) return (-1, "");
            var a = args.Replace(" -o json", "");
            switch (a)
            {
                case "--version": return (0, "0.11.0");
                case "status":
                    return (0, new JsonObject
                    {
                        ["system"] = new JsonObject { ["architecture"] = "Arm64", ["npus"] = new JsonArray { new JsonObject { ["name"] = "Snapdragon(R) X Elite - Hexagon NPU" } } },
                        ["service"] = new JsonObject { ["state"] = Running ? "ready" : "stopped", ["ready"] = Running, ["webUrls"] = Running ? new JsonArray { Url } : new JsonArray(), ["cliVersion"] = "0.11.0" },
                        ["connectivity"] = new JsonObject { ["localServiceReachable"] = Running && !Sick },
                    }.ToJsonString());
                case "server status": return Running ? (0, $$"""{"running":true,"state":"ready","pid":4242,"webUrls":["{{Url}}"]}""") : (0, "Foundry Local daemon: not running\n");
                case "server start --idle-timeout 0": Running = true; return (0, "● success: started");
                case "server restart": Running = true; if (RestartFixes) Sick = false; return (0, """{"success":true}""");
                case "server stop": Running = false; return (0, """{"success":true}""");
                case "model list --type chat --variants": return (0, Variants());
                case "model list --loaded":
                    return (0, Loaded ? """{"models":[{"alias":"phi-4-mini","id":"Phi-4-mini-instruct-qnn-npu:1","type":"Chat","device":"Npu","fileSizeMb":2700,"cached":true,"supportsToolCalling":true}]}""" : """{"models":[]}""");
            }
            if (a.StartsWith("model download ")) { var id = a[15..]; Downloads.Add(id); _cached.Add(id); return (0, """{"success":true}"""); }
            if (a.StartsWith("model load "))
            {
                var id = a[11..]; Loads.Add(id);
                if (LoadFailsOnce) { LoadFailsOnce = false; Sick = true; RestartFixes = true; return (4, """{"error":{"code":"SERVICE_ERROR","message":"The Foundry Local service did not respond to the load request.","hint":"Run 'foundry server status' to inspect, or 'foundry server logs' for details."}}"""); }
                Loaded = true; return (0, """{"success":true,"message":"Loaded."}""");
            }
            if (a.StartsWith("server status")) return (0, "");
            return (1, "error: Unknown command: '" + a + "'");
        }
    }
}
