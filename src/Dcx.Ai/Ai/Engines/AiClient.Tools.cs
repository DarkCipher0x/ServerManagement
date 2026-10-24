using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DcxManager.Services;

namespace DcxManager.Ai;

// AiClient: tool support and warm-up (ADR-034) (moved unchanged from AiClient.cs in 0.2.0 phase 6½).
public static partial class AiClient
{
    // ---- tool support + warm-up (ADR-034) --------------------------------------------------------------------------

    /// <summary>An error that means "this model can't take tool definitions".</summary>
    internal static bool ToolsRefused(string message) =>
        Regex.IsMatch(message, @"does not support tools|tools? (are|is) not supported|not support(ed)? .*(tool|function)|tool[_ ]?(use|calling|choice).*(not|unsupported)|unsupported.*\btools?\b|unknown (field|parameter).*tools", RegexOptions.IgnoreCase);

    private static readonly Dictionary<string, bool> _tools = new();
    private static string ToolKey => $"{Engine}|{Model}|{S.OllamaUrl}|{S.OpenAiUrl}|{S.AnthropicUrl}";

    /// <summary>Whether the current engine + model takes native tool calls. Ollama says so itself (/api/show
    /// "capabilities"); other engines are assumed to until a call is refused (<see cref="MarkNoTools"/>). Cached.</summary>
    public static async Task<bool> ToolsSupportedAsync(CancellationToken ct = default)
    {
        var key = ToolKey;
        lock (_tools) if (_tools.TryGetValue(key, out var known)) return known;
        bool ok = true;
        if (Engine is "ollama-gpu" or "ollama-cpu")
            try
            {
                using var t = CancellationTokenSource.CreateLinkedTokenSource(ct);
                t.CancelAfter(5000);
                var show = await PostAsync((await OllamaClient.BaseUrlAsync()).TrimEnd('/') + "/api/show", new JsonObject { ["model"] = Model }, null, "Ollama", t.Token);
                if (show["capabilities"] is JsonArray caps) ok = caps.Any(c => string.Equals(c?.GetValue<string>(), "tools", StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex) when (!ct.IsCancellationRequested) { Log.Write("AI tool support check", ex); }
        lock (_tools) _tools[key] = ok;
        return ok;
    }

    /// <summary>The model refused tool definitions: use the text tool protocol for it from now on.</summary>
    public static void MarkNoTools() { lock (_tools) _tools[ToolKey] = false; }
    internal static void ForgetToolSupport() { lock (_tools) _tools.Clear(); }

    private static DateTime _warmed;
    internal static bool AllowWarmInSelfTest;

    /// <summary>The user started typing a question: load the model and open the connection now, so the answer starts
    /// sooner (Ollama: an empty /api/generate loads it with the keep-alive; Foundry: resolves the model id). At most
    /// once a minute; never under memory pressure; nothing is sent to an internet provider.</summary>
    public static void Warm()
    {
        AiResourceGuard.Touch();
        if (Engine == "off" || (TestMode.Running && !AllowWarmInSelfTest) || DateTime.UtcNow - _warmed < TimeSpan.FromMinutes(1)) return;
        if (AiResourceGuard.Check().Level != AiPressure.Ok) return;
        _warmed = DateTime.UtcNow;
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                var url = await BaseUrlAsync(cts.Token);
                if (!IsLocal(url)) return;
                switch (Engine)
                {
                    case "ollama-gpu" or "ollama-cpu":
                        await PostAsync(url + "/api/generate", new JsonObject { ["model"] = Model, ["keep_alive"] = AiResourceGuard.KeepAlive() }, null, "Ollama", cts.Token);
                        break;
                    case "npu" when AppSettings.Current.FoundryAutoSetup && AppSettings.Current.FoundryUrl.Trim().Length == 0:
                        // 0.6.19 (DarkCipher0x: Qwen "takes a while to load"): really load the model into memory now (Foundry
                        // Local otherwise loads it on the first question); quick when it is already loaded.
                        using (var load = new CancellationTokenSource(TimeSpan.FromMinutes(15))) await FoundryManager.EnsureReadyAsync(false, load.Token);
                        break;
                    case "npu":
                        await FoundryModelIdAsync(url[..^"/v1".Length], cts.Token);
                        break;
                    case "openai":
                        using (await Http.GetAsync(url + "/models", cts.Token)) { }
                        break;
                }
            }
            catch (Exception ex) { Log.Write("AI warm-up", ex); }
        });
    }

    internal static void ResetWarm() => _warmed = default;

    /// <summary>Asks Ollama to unload the app's model now (keep_alive 0). Only the model this app uses.</summary>
    public static async Task UnloadOllamaAsync(CancellationToken ct = default)
    {
        if (Engine is not ("ollama-gpu" or "ollama-cpu")) return;
        var url = await OllamaClient.BaseUrlAsync();
        await PostAsync(url + "/api/generate", new JsonObject { ["model"] = DefaultModel, ["keep_alive"] = 0 }, null, "Ollama", ct);
    }

    /// <summary>Ollama's loaded models (/api/ps): name, total size and the part in GPU memory.</summary>
    public static async Task<List<(string Name, long Size, long Vram)>> OllamaLoadedAsync(CancellationToken ct = default)
    {
        var url = await OllamaClient.BaseUrlAsync();
        using var r = await Http.GetAsync(url + "/api/ps", ct);
        var node = JsonNode.Parse(await r.Content.ReadAsStringAsync(ct));
        return (node?["models"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
            .Select(m => (m["name"]?.GetValue<string>() ?? "", m["size"]?.GetValue<long>() ?? 0, m["size_vram"]?.GetValue<long>() ?? 0)).ToList();
    }
}
