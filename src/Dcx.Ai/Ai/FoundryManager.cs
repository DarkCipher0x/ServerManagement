using System.Diagnostics;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DcxManager.Services;

namespace DcxManager.Ai;

/// <summary>A chat model Foundry Local offers on this PC (one device build).</summary>
public sealed record FoundryModel(string Alias, string Id, string Device, int SizeMb, bool Cached, bool Tools)
{
    public string Bare => Id.Split(':')[0];
    public string SizeText => SizeMb >= 1024 ? $"{SizeMb / 1024.0:0.#} GB" : $"{SizeMb} MB";
    public string DeviceText => Device.ToUpperInvariant();
}

/// <summary>What `foundry status` reports, plus a real request to the service.</summary>
public sealed record FoundryHealth(bool Installed, string? Version, bool Running, string? Url, bool Reachable, IReadOnlyList<string> Npus)
{
    public static readonly FoundryHealth Missing = new(false, null, false, null, false, Array.Empty<string>());
}

/// <summary>
/// Foundry Local, hands-off (ADR-026): installs it (winget), starts the service, checks it really answers and repairs it
/// when it doesn't (restart; stop + end the daemon + start), picks a model this PC's catalogue actually offers, downloads
/// and loads it, and retries a failed load once after a repair. Drives the CLI's JSON output (Foundry Local 0.11+).
/// Every step is reported (status bar, Settings) and the ones that install, download or repair are audited.
/// </summary>
public static class FoundryManager
{
    // ---- test seams (the self-test never runs the real CLI, winget or touches a process) -------------------------------
    internal static Func<string, TimeSpan, CancellationToken, Task<(int Code, string Output)>> Winget =
        (args, timeout, ct) => AiClient.RunLocalAsync("winget", args, timeout, ct);
    /// <summary>True when GET {url}/v1/models answers within 5 s.</summary>
    internal static Func<string, CancellationToken, Task<bool>> Probe = ProbeAsync;
    /// <summary>Ends a stuck daemon — only a process named foundrylocald.</summary>
    internal static Func<int, bool> EndDaemon = EndDaemonProcess;
    internal static Func<bool> OnBattery = () => AiHardware.OnBattery;

    /// <summary>The chat path may run the set-up during the self-test only when a test asks for it.</summary>
    internal static bool AllowInSelfTest;

    public const int BigDownloadMb = 1024;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Each step as it happens ("…" while working, "✓", "✕", "⚠"). Raised on the caller's thread.</summary>
    public static event Action<string>? Progress;
    public static string LastStatus { get; private set; } = "";
    public static bool Busy => Gate.CurrentCount == 0;
    /// <summary>The commands the last run issued (self-test, and "Show what it did").</summary>
    internal static List<string> Trace { get; } = new();

    private static void Say(string s) { LastStatus = s; try { Progress?.Invoke(s); } catch (Exception ex) { Log.Write("Foundry progress", ex); } }

    // ---- the CLI, JSON --------------------------------------------------------------------------------------------

    internal sealed record CliResult(int Code, JsonNode? Json, string Output)
    {
        public bool Missing => Code == -1;
        public string? ErrorCode => Json?["error"]?["code"]?.ToString();
        public string Error => Json?["error"] is JsonObject e
            ? $"{e["message"]}{(e["code"] is { } c ? $" ({c})" : "")}{(e["hint"] is { } h ? $" — {h}" : "")}"
            : Failure(Output, Code);
    }

    internal static async Task<CliResult> CliAsync(string args, TimeSpan timeout, CancellationToken ct)
    {
        lock (Trace) Trace.Add(args);
        var (code, output) = await AiClient.FoundryCli(args + " -o json", timeout, ct);
        return new(code, ParseJson(output), output);
    }

    /// <summary>The JSON object in the CLI's output (it may be preceded by progress text); null when there is none.</summary>
    internal static JsonNode? ParseJson(string output)
    {
        output = Regex.Replace(output ?? "", @"\x1B\[[0-9;?]*[A-Za-z]", "");
        for (int i = output.IndexOf('{'); i >= 0; i = output.IndexOf('{', i + 1))
        {
            int end = output.LastIndexOf('}');
            if (end <= i) break;
            try { return JsonNode.Parse(output[i..(end + 1)]); } catch { }
        }
        return null;
    }

    /// <summary>A failed command's own error lines (not just the closing "Hint"), or the exit code.</summary>
    internal static string Failure(string output, int code)
    {
        var lines = Regex.Replace(output ?? "", @"\x1B\[[0-9;?]*[A-Za-z]", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var errors = lines.Where(l => !l.StartsWith("Hint", StringComparison.OrdinalIgnoreCase)
            && Regex.IsMatch(l, @"error|fail|✗|×|not found|not supported|unable|cannot|can't|denied|invalid|no such", RegexOptions.IgnoreCase)).ToList();
        var shown = (errors.Count > 0 ? errors : lines.Where(l => !l.StartsWith("Hint", StringComparison.OrdinalIgnoreCase)).TakeLast(3)).Take(4).ToList();
        var hint = lines.LastOrDefault(l => l.StartsWith("Hint", StringComparison.OrdinalIgnoreCase));
        var text = string.Join(" · ", shown) + (hint != null ? (shown.Count > 0 ? " — " : "") + hint : "");
        return text.Length > 0 ? $"exit {code}: {text}" : $"exit {code}";
    }

    // ---- health ----------------------------------------------------------------------------------------------------

    private static readonly object SharedLock = new();
    private static Task<FoundryHealth>? _sharedHealth;

    /// <summary>
    /// HealthAsync for status displays: a caller that arrives while a check is running gets that check instead of
    /// starting another `foundry status` (1.8 s on DarkCipher0x's PC). Every Settings open used to start its own, and reopening
    /// it quickly stacked them up (s36c). Set-up and repair call HealthAsync directly, because they need a fresh answer
    /// after each step.
    /// </summary>
    public static Task<FoundryHealth> SharedHealthAsync()
    {
        lock (SharedLock)
        {
            if (_sharedHealth is { IsCompleted: false } running) return running;
            return _sharedHealth = HealthAsync();
        }
    }

    public static async Task<FoundryHealth> HealthAsync(CancellationToken ct = default)
    {
        var st = await CliAsync("status", TimeSpan.FromSeconds(30), ct);
        if (st.Missing) return FoundryHealth.Missing;
        if (st.Json is JsonObject j && j["service"] is JsonObject svc)
        {
            var url = (svc["webUrls"] as JsonArray)?.Select(u => u?.ToString()).FirstOrDefault(u => !string.IsNullOrEmpty(u))?.TrimEnd('/');
            bool running = svc["ready"]?.GetValue<bool>() == true || string.Equals(svc["state"]?.ToString(), "ready", StringComparison.OrdinalIgnoreCase);
            bool reach = running && url != null && j["connectivity"]?["localServiceReachable"]?.GetValue<bool>() != false && await Probe(url, ct);
            var npus = (j["system"]?["npus"] as JsonArray)?.Select(n => n is JsonObject o ? o["name"]?.ToString() : n?.ToString()).Where(n => !string.IsNullOrEmpty(n)).Select(n => n!).ToList() ?? new();
            return new(true, svc["cliVersion"]?.ToString(), running, url, reach, npus);
        }
        // An older CLI (no JSON): the version and the text status, as before.
        var (vc, ver) = await AiClient.FoundryCli("--version", TimeSpan.FromSeconds(20), ct);
        if (vc == -1) return FoundryHealth.Missing;
        var (_, output, _) = await AiClient.FoundryServerAsync("status", "status", TimeSpan.FromSeconds(15), ct);
        var u = AiClient.FoundryUrlFrom(output);
        return new(true, ver.Trim().Split('\n').LastOrDefault()?.Trim(), u != null, u, u != null && await Probe(u, ct), Array.Empty<string>());
    }

    private static async Task<bool> ProbeAsync(string url, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            using var r = await AiClient.Http.GetAsync(url + "/v1/models", cts.Token);
            return r.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException) { return false; }
    }

    private static bool EndDaemonProcess(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (!p.ProcessName.StartsWith("foundrylocald", StringComparison.OrdinalIgnoreCase)) return false;
            p.Kill(true);
            p.WaitForExit(10_000);
            return true;
        }
        catch { return false; }
    }

    // ---- catalogue -------------------------------------------------------------------------------------------------

    /// <summary>Chat models this PC can run: every device build (<paramref name="variants"/>) or the best one per model.</summary>
    public static async Task<List<FoundryModel>> CatalogAsync(bool variants, CancellationToken ct = default)
    {
        var r = await CliAsync("model list --type chat" + (variants ? " --variants" : ""), TimeSpan.FromSeconds(60), ct);
        if (r.Json == null) throw new InvalidOperationException("Foundry Local didn't list its models: " + r.Error);
        return ParseCatalog(r.Json);
    }

    public static async Task<List<FoundryModel>> LoadedAsync(CancellationToken ct = default)
    {
        var r = await CliAsync("model list --loaded", TimeSpan.FromSeconds(30), ct);
        return r.Json == null ? new() : ParseCatalog(r.Json);
    }

    internal static List<FoundryModel> ParseCatalog(JsonNode json)
    {
        var list = new List<FoundryModel>();
        foreach (var m in (json["variants"] as JsonArray ?? json["models"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            string S(string k) => m[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
            var id = S("variantId").Length > 0 ? S("variantId") : S("id");
            if (id.Length == 0) continue;
            if (S("type").Length > 0 && !S("type").Equals("chat", StringComparison.OrdinalIgnoreCase)) continue;
            int size = m["fileSizeMb"] is JsonValue sv && sv.TryGetValue<int>(out var sz) ? sz : 0;
            // supportsToolCalling is only in the per-model list; known tool-capable families count in the variant list.
            bool tools = m["supportsToolCalling"] is JsonValue tv ? tv.TryGetValue<bool>(out var t) && t : ToolFamily(S("alias"));
            list.Add(new(S("alias"), id, S("device").ToLowerInvariant(), size, m["cached"]?.GetValue<bool>() == true, tools));
        }
        return list;
    }

    private static bool ToolFamily(string alias) => Regex.IsMatch(alias, @"^(phi-4-mini|qwen2\.5|qwen3|mistral|gpt-oss)", RegexOptions.IgnoreCase);

    /// <summary>The model's base name without its runtime/device suffix ("qwen2.5-0.5b-instruct-generic-cpu" → "qwen2.5-0.5b-instruct").</summary>
    internal static string BaseName(string id) =>
        Regex.Replace(id.Split(':')[0], @"-(generic|qnn|openvino|vitis|cuda|trt|trtrtx|webgpu|dml|ov|intel|amd|qualcomm)?-?(cpu|gpu|npu)$", "", RegexOptions.IgnoreCase).ToLowerInvariant();

    /// <summary>
    /// What to load for <paramref name="wanted"/> on THIS PC: the exact build when it's offered here; an alias → its best
    /// build here; a build from another PC (an x64 CPU id on an ARM64 NPU machine) → the same model's best build here.
    /// Null when this PC doesn't offer the model at all.
    /// </summary>
    internal static FoundryModel? Resolve(string wanted, IReadOnlyList<FoundryModel> variants)
    {
        wanted = wanted.Trim();
        if (wanted.Length == 0) return null;
        var bare = wanted.Split(':')[0];
        if (variants.FirstOrDefault(v => v.Bare.Equals(bare, StringComparison.OrdinalIgnoreCase)) is { } exact) return exact;
        var sameAlias = variants.Where(v => v.Alias.Equals(wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        if (sameAlias.Count == 0)
        {
            var b = BaseName(wanted);
            var alias = variants.FirstOrDefault(v => BaseName(v.Id) == b)?.Alias;
            if (alias != null) sameAlias = variants.Where(v => v.Alias == alias).ToList();
        }
        return sameAlias.OrderBy(v => DeviceRank(v.Device)).ThenByDescending(v => v.Cached).FirstOrDefault();
    }

    private static int DeviceRank(string device) => device switch { "npu" => 0, "gpu" => 1, "cpu" => 2, _ => 3 };

    /// <summary>The recommended model for this PC from its own catalogue, and why. Only models that can use tools
    /// (the AI operator needs them).</summary>
    public static (FoundryModel Model, string Why)? Recommend(IReadOnlyList<FoundryModel> variants, long ramBytes, long vramBytes, bool hasNpu)
    {
        double ram = ramBytes / (double)(1L << 30), vram = vramBytes / (double)(1L << 30);
        var usable = variants.Where(v => v.Tools).ToList();
        FoundryModel? Pick(string device, params string[] aliases) =>
            aliases.Select(a => usable.FirstOrDefault(v => v.Device == device && v.Alias.Equals(a, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(v => v != null);
        // 0.6.17 (AiDefaults revision 6): the strongest tool-calling model the PC runs well — a big GPU first (Qwen2.5 14B /
        // 7B call tools far more reliably than phi-4-mini), then an NPU, then a smaller GPU, then the CPU.
        if (vram >= 12 && Pick("gpu", "qwen2.5-14b", "qwen2.5-7b") is { } g14)
            return (g14, $"a GPU with {vram:0} GB of video memory runs {g14.Alias}, which calls tools reliably ({g14.SizeText}).");
        if (vram >= 6 && Pick("gpu", "qwen2.5-7b") is { } g7)
            return (g7, $"a GPU with {vram:0} GB of video memory runs {g7.Alias}, which calls tools reliably ({g7.SizeText}).");
        if (hasNpu && Pick("npu", "qwen2.5-7b", "phi-4-mini", "qwen2.5-1.5b", "qwen2.5-0.5b") is { } n)
            return (n, $"this PC has an NPU: {n.Alias} runs on it at low power ({n.SizeText}).");
        if (vram >= 4 && Pick("gpu", "phi-4-mini", "qwen2.5-1.5b") is { } g2)
            return (g2, $"no NPU; a GPU with {vram:0} GB of video memory runs {g2.Alias} well ({g2.SizeText}).");
        if (ram >= 7.5 && Pick("cpu", "phi-4-mini", "qwen2.5-1.5b") is { } c1)
            return (c1, $"no NPU or large GPU; {ram:0} GB of RAM runs {c1.Alias} on the CPU ({c1.SizeText}) — best quality, slower answers.");
        if (ram >= 5.5 && Pick("cpu", "qwen2.5-1.5b", "qwen2.5-0.5b") is { } c2)
            return (c2, $"{ram:0} GB of RAM suits {c2.Alias} on the CPU ({c2.SizeText}).");
        if (Pick("cpu", "qwen2.5-0.5b") is { } c3)
            return (c3, $"the lightest model ({c3.SizeText}) — quick and small, simpler answers.");
        var any = usable.OrderBy(v => DeviceRank(v.Device)).ThenBy(v => v.SizeMb).FirstOrDefault();
        return any == null ? null : (any, $"the smallest tool-capable model this PC offers ({any.SizeText}).");
    }

    /// <summary>One short line on what a model family is good for (the Settings model guide).</summary>
    public static string GoodFor(string alias) => alias.ToLowerInvariant() switch
    {
        var a when a.StartsWith("qwen2.5-0.5b") => "Lightest. Fast on any CPU, about 1 GB of RAM; simple answers and summaries.",
        var a when a.StartsWith("qwen2.5-1.5b") => "Light and capable. Good everyday choice for CPUs and NPUs, about 2 GB of RAM.",
        var a when a.StartsWith("qwen2.5-coder") => "Code-focused: scripts, configs, explaining commands.",
        var a when a.StartsWith("qwen2.5-7b") => "Strong all-rounder. Best on a GPU with 8 GB+; slow on a CPU.",
        var a when a.StartsWith("qwen2.5-14b") => "High quality, heavy: a GPU with 12 GB+.",
        var a when a.StartsWith("phi-4-mini-reasoning") => "Step-by-step reasoning; slower, no tools for the operator.",
        var a when a.StartsWith("phi-4-mini") => "Balanced: good answers and tool use (the AI operator); NPU, GPU or a CPU with 16 GB.",
        var a when a == "phi-4" => "Higher quality than phi-4-mini; needs a big GPU or lots of RAM.",
        var a when a.StartsWith("phi-3") => "Older small model; prefer phi-4-mini.",
        var a when a.StartsWith("qwen3-0.6b") || a.StartsWith("qwen3-1.7b") => "Small Qwen3: quick, light.",
        var a when a.StartsWith("qwen3") => "Qwen3: strong reasoning and tools; size decides the hardware.",
        var a when a.StartsWith("deepseek-r1") => "Reasoning model: thinks out loud, slow; not for the operator.",
        var a when a.StartsWith("gpt-oss") => "Large open model: a GPU with 16 GB+.",
        var a when a.StartsWith("mistral") => "General chat; mid-size.",
        _ => "",
    };

    // ---- the hands-off pipeline ------------------------------------------------------------------------------------

    /// <summary>Is the configured model reachable through Foundry Local right now (cheap: status + one request)?</summary>
    public static async Task<bool> ReadyAsync(CancellationToken ct = default)
    {
        try { var h = await HealthAsync(ct); return h.Reachable; } catch { return false; }
    }

    /// <summary>
    /// Install → start → healthy (repair) → model available here → downloaded → loaded → verified. <paramref name="byUser"/>
    /// = a button press (may download on battery; asks nothing either way). False with the reason in <see cref="LastStatus"/>.
    /// </summary>
    public static async Task<bool> EnsureReadyAsync(bool byUser, CancellationToken ct = default)
    {
        if (!await Gate.WaitAsync(0, ct)) { Say("… set-up is already running"); await Gate.WaitAsync(ct); Gate.Release(); return await ReadyAsync(ct); }
        lock (Trace) Trace.Clear();
        try
        {
            var s = AppSettings.Current;
            // 1. Installed?
            Say("… checking Foundry Local");
            var h = await HealthAsync(ct);
            if (!h.Installed)
            {
                if (Offline.On) { Say("✕ Foundry Local isn't installed, and offline mode is on (installing downloads from Microsoft)."); return false; }
                Say("… installing Foundry Local from Microsoft (winget install Microsoft.FoundryLocal) — a few minutes");
                lock (Trace) Trace.Add("winget install Microsoft.FoundryLocal");
                var (wc, wo) = await Winget("install --id Microsoft.FoundryLocal -e --silent --accept-source-agreements --accept-package-agreements", TimeSpan.FromMinutes(20), ct);
                AuditLog.Write(byUser ? "you" : "app", "Foundry Local", "installed Foundry Local (winget)", wc == 0 ? "ok" : $"exit {wc}");
                if (wc == -1) { Say("✕ winget isn't available: install “App Installer” from the Microsoft Store, or Foundry Local from aka.ms/foundry-local-installer."); return false; }
                // winget returns non-zero for "already installed"/"no upgrade" too: what counts is whether foundry now answers.
                AiClient.ForgetFoundry();
                h = await HealthAsync(ct);
                if (!h.Installed) { Say($"✕ Installing Foundry Local didn't work ({Failure(wo, wc)}). If it did install, restart the app (Windows only adds new programs to PATH for programs started afterwards)."); return false; }
            }
            // 2. Running? 3. Answering?
            if (!h.Running)
            {
                Say("… starting the Foundry Local service");
                var (sc, so, _) = await StartServerAsync("start", TimeSpan.FromMinutes(3), ct);
                AiClient.ForgetFoundry();
                h = await HealthAsync(ct);
                if (!h.Running) { Say($"✕ The Foundry Local service didn't start ({Failure(so, sc)})."); return false; }
            }
            if (!h.Reachable)
            {
                h = await RepairAsync(byUser, "it says it's running but doesn't answer", ct);
                if (!h.Reachable) return false;
            }
            // 4. Which model, here?
            Say("… checking the model");
            var variants = await CatalogAsync(variants: true, ct);
            var want = s.FoundryModel.Trim();
            var model = Resolve(want, variants);
            // 0.6.17: a default meant for a GPU (Qwen2.5 7B / 14B) that this PC offers only as a CPU build would crawl.
            var gpuOnCpu = model is { Device: "cpu" } && AiDefaults.ForGpu(model.Alias) && (await AiHardware.DetectAsync()).BestVram > 0;
            if (model == null || !model.Tools || gpuOnCpu)
            {
                var hw = await AiHardware.DetectAsync();
                if (Recommend(variants, hw.RamBytes, hw.BestVram, h.Npus.Count > 0 || hw.Npu != null) is not { } rec) { Say("✕ Foundry Local offers no chat model for this PC."); return false; }
                Say($"⚠ {(want.Length == 0 ? "No model was chosen" : model == null ? $"{want} isn't offered for this PC" : gpuOnCpu ? $"{want} is offered here only for the CPU (too slow)" : $"{want} can't use tools (the AI operator needs them)")} — using {rec.Model.Alias} ({rec.Model.DeviceText}): {rec.Why}");
                model = rec.Model;
                s.FoundryModel = model.Alias;
                AppSettings.Save();
            }
            // 5. Downloaded?
            if (!model.Cached)
            {
                if (Offline.On) { Say($"✕ {model.Alias} ({model.SizeText}) isn't downloaded, and offline mode is on."); return false; }
                if (!s.AiAllowModelDownload) { Say($"✕ {model.Alias} isn't on this PC, and downloading AI models is turned off by your organisation (Group Policy: AiAllowModelDownload)."); return false; }
                if (!byUser && model.SizeMb > BigDownloadMb && OnBattery()) { Say($"⚠ {model.Alias} ({model.SizeText}) will download when the PC is on mains power — or press “Set up now”."); return false; }
                Say($"… downloading {model.Alias} ({model.DeviceText}, {model.SizeText}) — the first time only");
                var dl = await CliAsync("model download " + model.Id, TimeSpan.FromMinutes(90), ct);
                AuditLog.Write(byUser ? "you" : "app", "Foundry Local", "downloaded model " + model.Id, dl.Code == 0 ? "ok" : dl.Error);
                if (dl.Code != 0) { Say($"✕ Downloading {model.Alias} failed: {dl.Error}"); return false; }
            }
            // 6. Loaded? (a failed load gets one repair and one retry — what had to be done by hand before)
            if (!(await LoadedAsync(ct)).Any(m => m.Bare.Equals(model.Bare, StringComparison.OrdinalIgnoreCase)))
            {
                Say($"… loading {model.Alias} ({model.DeviceText}) into memory");
                var ld = await CliAsync("model load " + model.Id, TimeSpan.FromMinutes(15), ct);
                if (ld.Code != 0 && ld.ErrorCode is not ("MODEL_NOT_FOUND" or "INVALID_ARGUMENT"))
                {
                    Log.Write($"foundry model load {model.Id} (exit {ld.Code})", new InvalidOperationException(ld.Output.Trim()));
                    h = await RepairAsync(byUser, $"loading the model failed: {ld.Error}", ct);
                    if (!h.Reachable) return false;
                    Say($"… loading {model.Alias} again");
                    ld = await CliAsync("model load " + model.Id, TimeSpan.FromMinutes(15), ct);
                }
                if (ld.Code != 0) { Say($"✕ Loading {model.Alias} failed: {ld.Error}"); Log.Write($"foundry model load {model.Id} (exit {ld.Code})", new InvalidOperationException(ld.Output.Trim())); return false; }
                if (!(await LoadedAsync(ct)).Any(m => m.Bare.Equals(model.Bare, StringComparison.OrdinalIgnoreCase))) { Say($"✕ {model.Alias} reported loaded but isn't in memory."); return false; }
            }
            AiClient.ForgetFoundry();
            Say($"✓ Ready: {model.Alias} on the {model.DeviceText} ({model.SizeText}) · Foundry Local {h.Version} at {h.Url}");
            return true;
        }
        catch (OperationCanceledException) { Say("✕ Set-up was cancelled."); return false; }
        catch (Exception ex) { Say("✕ " + ex.Message.Split('\n')[0]); Log.Write("Foundry set-up", ex); return false; }
        finally { Gate.Release(); }
    }

    /// <summary>`foundry server start --idle-timeout 0` (`foundry service start` on older CLIs), recorded in the trace.</summary>
    private static Task<(int Code, string Output, string Verb)> StartServerAsync(string oldArgs, TimeSpan timeout, CancellationToken ct)
    {
        lock (Trace) Trace.Add("server start --idle-timeout 0");
        return AiClient.FoundryServerAsync("start --idle-timeout 0", oldArgs, timeout, ct);
    }

    /// <summary>restart; still not answering → stop, end the recorded daemon (only foundrylocald), start. One attempt.</summary>
    private static async Task<FoundryHealth> RepairAsync(bool byUser, string why, CancellationToken ct)
    {
        Say($"… repairing Foundry Local ({why}): restarting the service");
        AuditLog.Write(byUser ? "you" : "app", "Foundry Local", "restarted the service (repair)", why);
        await CliAsync("server restart", TimeSpan.FromMinutes(3), ct);
        AiClient.ForgetFoundry();
        var h = await HealthAsync(ct);
        if (h.Reachable) { Say("✓ Foundry Local answers again after a restart."); return h; }
        Say("… still not answering: stopping it completely and starting it fresh");
        var st = await CliAsync("server status", TimeSpan.FromSeconds(30), ct);
        await CliAsync("server stop", TimeSpan.FromMinutes(2), ct);
        if (st.Json?["pid"] is JsonValue pv && pv.TryGetValue<int>(out var pid) && pid > 0)
        {
            await Task.Delay(1500, ct);
            if (EndDaemon(pid)) AuditLog.Write(byUser ? "you" : "app", "Foundry Local", "ended a stuck Foundry Local process", "pid " + pid);
        }
        await StartServerAsync("start", TimeSpan.FromMinutes(3), ct);
        AiClient.ForgetFoundry();
        h = await HealthAsync(ct);
        Say(h.Reachable ? "✓ Foundry Local answers again after a fresh start." : "✕ Foundry Local still doesn't answer after a restart and a fresh start. “Show log” has its own messages.");
        return h;
    }

    /// <summary>At start-up, when Foundry Local is the engine: set it up in the background (no dialogs).</summary>
    public static async Task AutoStartAsync()
    {
        var s = AppSettings.Current;
        if (!s.AiEnabled || s.AiEngine != "npu" || !s.FoundryAutoSetup || s.FoundryUrl.Trim().Length > 0) return;
        if (TestMode.Running || Environment.GetCommandLineArgs().Contains("--selftest")) return;
        await Task.Delay(TimeSpan.FromSeconds(3));   // let the window settle first
        await EnsureReadyAsync(byUser: false);
    }

    /// <summary>Errors a repair can fix (service down / not answering / model not loaded) — the chat path retries once after one.</summary>
    internal static bool Repairable(Exception ex) =>
        Regex.IsMatch(ex.Message, @"isn't reachable|isn't running|not running|not loaded|must be loaded|connection|refused|timed out", RegexOptions.IgnoreCase);
}
