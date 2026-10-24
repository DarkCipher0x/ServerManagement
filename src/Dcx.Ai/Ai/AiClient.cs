using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DcxManager.Services;

namespace DcxManager.Ai;

public enum AiFeature { Operator, LogAnalysis, Reports, Helper, ClaudeCode, Files }

/// <summary>The engines the user can pick. Every one speaks the same internal contract (chat + tools + structured JSON).</summary>
public static class AiEngines
{
    public static readonly (string Id, string Title, string Hint)[] All =
    {
        ("npu", "Foundry Local · NPU or CPU (default)", "Default. Microsoft Foundry Local on this PC: ONNX models on the NPU (Intel AI Boost, AMD XDNA, Snapdragon Hexagon) at low power, or the lightweight CPU model on PCs without one."),
        ("ollama-gpu", "Ollama · GPU", "Ollama on this PC, your LAN, or a GPU server through an SSH tunnel."),
        ("ollama-cpu", "Ollama · CPU only", "For laptops without a good GPU: forces CPU inference (num_gpu 0) and a smaller context. Use a 1.5–3B model."),
        ("openai", "OpenAI-compatible server", "LM Studio, llama.cpp server, vLLM, LocalAI, Jan — or OpenAI / Azure OpenAI / an internal gateway with a key."),
        ("anthropic", "Anthropic Messages API", "Claude via the Anthropic API with your key — or point it at Ollama (http://localhost:11434), which speaks the same API."),
        ("off", "Off — offline features only", "No model is ever called. Log digests, rule-based reports and help search keep working."),
    };

    public static string Title(string id) => All.FirstOrDefault(e => e.Id == id).Title ?? id;
}

/// <summary>
/// One front door for every AI call in the app. Messages use Ollama's shape internally
/// ({role, content, tool_calls[{id, function{name, arguments}}], tool_call_id}) and are translated per engine.
/// Secrets are masked before anything leaves the process, and non-local endpoints are refused unless allowed.
/// </summary>
public static partial class AiClient
{
    /// <summary>One pooled client for every engine (ADR-034): connections stay open for 10 minutes between questions, so
    /// a follow-up skips the TCP (and TLS) handshake.</summary>
    internal static readonly HttpClient Http = new(Offline.Handler(new SocketsHttpHandler
    {
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(30),
        MaxConnectionsPerServer = 8,
    })) { Timeout = TimeSpan.FromMinutes(15) };
    private static string? _foundryBase;

    private static AppSettings S => AppSettings.Current;
    public static string Engine => S.AiEnabled ? S.AiEngine : "off";

    // ---- per-chat model (ADR-034) ---------------------------------------------------------------------------------

    private static readonly AsyncLocal<string?> _modelOverride = new();

    /// <summary>Uses <paramref name="model"/> (of the current engine) for every call in this async flow until disposed;
    /// null or empty keeps the engine's default. A conversation's own model choice never changes Settings.</summary>
    public static IDisposable UseModel(string? model)
    {
        var was = _modelOverride.Value;
        _modelOverride.Value = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
        return new Restore(() => _modelOverride.Value = was);
    }

    private sealed class Restore(Action a) : IDisposable { public void Dispose() => a(); }

    /// <summary>The engine's configured model (ignores a per-chat choice).</summary>
    public static string DefaultModel => S.AiEngine switch
    {
        "npu" => S.FoundryModel,
        "openai" => S.OpenAiModel,
        "anthropic" => S.AnthropicModel,
        _ => S.OllamaModel,
    };

    // ---- gating ---------------------------------------------------------------------------------------------------

    public static bool FeatureOn(AiFeature f) => f switch
    {
        AiFeature.Operator => S.AiFeatureOperator,
        AiFeature.LogAnalysis => S.AiFeatureLogAnalysis,
        AiFeature.Reports => S.AiFeatureReports,
        AiFeature.Helper => S.AiFeatureHelper,
        AiFeature.ClaudeCode => S.AiFeatureClaudeCode,
        AiFeature.Files => S.AiFeatureFiles,
        _ => true,
    };

    /// <summary>Null when the feature may call a model now; otherwise a short reason. <paramref name="automatic"/> = not directly requested by the user.</summary>
    public static string? Blocked(AiFeature f, bool automatic = false)
    {
        if (!S.AiEnabled || S.AiEngine == "off") return "AI is switched off (Settings → AI engine).";
        if (!FeatureOn(f)) return $"AI for {f} is switched off (Settings → AI engine).";
        if (automatic && S.AiPauseOnBattery && AiHardware.OnBattery) return "Paused on battery power.";
        if (Offline.On && Offline.Quick(EngineHost) is { Allowed: false } off) return $"Offline mode: the AI engine at {EngineHost} is blocked ({off.Reason}).";
        return null;
    }

    /// <summary>The host the engine talks to, without contacting it (a tunnelled Ollama is the tunnel's SSH host).</summary>
    public static string EngineHost => Engine switch
    {
        "npu" => S.FoundryUrl.Trim().Length > 0 ? Offline.HostOf(S.FoundryUrl) : "localhost",
        "openai" => Offline.HostOf(S.OpenAiUrl),
        "anthropic" => Offline.HostOf(S.AnthropicUrl),
        _ => AppData.Current.FindHost(S.OllamaTunnelHostId) is { } th ? th.Address : Offline.HostOf(string.IsNullOrWhiteSpace(S.OllamaUrl) ? "http://localhost:11434" : S.OllamaUrl),
    };

    public static string Model => _modelOverride.Value ?? DefaultModel;

    /// <summary>The context budget for prompts we build ourselves (digests, reports). CPU/NPU engines get less.</summary>
    public static int PromptBudgetChars => Engine switch
    {
        "ollama-cpu" => 9_000,
        "npu" => 9_000,
        "openai" or "anthropic" => 60_000,
        _ => Math.Clamp(S.AiContext * 2, 8_000, 60_000),
    };

    public static async Task<string> BaseUrlAsync(CancellationToken ct = default) => Engine switch
    {
        "npu" => await FoundryBaseAsync(ct) + "/v1",
        "openai" => S.OpenAiUrl.Trim().TrimEnd('/'),
        "anthropic" => S.AnthropicUrl.Trim().TrimEnd('/'),
        _ => await OllamaClient.BaseUrlAsync(),
    };

    public static async Task<string> DescribeAsync()
    {
        if (Engine == "off") return "AI off — offline features only";
        string url;
        try { url = await BaseUrlAsync(new CancellationTokenSource(4000).Token); } catch (Exception ex) { url = "(" + ex.Message.Split('\n')[0] + ")"; }
        return $"{Model} · {AiEngines.Title(Engine)} · {url}";
    }

    /// <summary>Loopback, private/LAN, CGNAT/Tailscale, link-local, single-label names and our SSH tunnels count as local.</summary>
    /// <remarks>One classifier for the whole app: <see cref="NetworkPolicy"/>. DNS names it can't place are not local.</remarks>
    public static bool IsLocal(string url) => Uri.TryCreate(url, UriKind.Absolute, out _) && NetworkPolicy.IsPrivate(url);

    // ---- secrets --------------------------------------------------------------------------------------------------

    /// <summary>Secret masking lives in <see cref="Redaction"/> (0.2.0 phase 2: it is privacy, used outside the AI too);
    /// these forward so the AI code reads as before. Masking fails CLOSED.</summary>
    public static string Redact(string s) => Redaction.Mask(s);

    /// <summary>Redact and report how many secrets were masked (for the "data being sent" preview).</summary>
    public static (string Text, int Masked) RedactCounted(string s) => Redaction.MaskCounted(s);

    private static JsonArray Sanitize(JsonArray messages)
    {
        var copy = (JsonArray)messages.DeepClone();
        if (!S.AiRedactSecrets) return copy;
        foreach (var m in copy.OfType<JsonObject>())
            if (m["role"]?.GetValue<string>() is "user" or "tool" && m["content"] is JsonValue v && v.TryGetValue<string>(out var c))
                m["content"] = Redact(c);
        return copy;
    }

    // ---- chat -----------------------------------------------------------------------------------------------------

    /// <summary>One model turn. Returns an Ollama-shaped assistant message (content + optional tool_calls).</summary>
    /// <remarks><paramref name="onDelta"/>: when set (and no schema is asked for), the answer streams; each piece of text
    /// is passed on as it arrives, on a background thread — callers marshal it with BeginInvoke. The returned message is
    /// the same either way.</remarks>
    public static async Task<JsonObject> ChatAsync(JsonArray messages, JsonArray? tools, JsonObject? schema, CancellationToken ct, Action<string>? onDelta = null)
    {
        try { return await ChatOnceAsync(messages, tools, schema, ct, onDelta); }
        // Foundry Local down, not answering or without its model loaded: repair once, then ask again (ADR-026).
        catch (InvalidOperationException ex) when (Engine == "npu" && S.FoundryAutoSetup && S.FoundryUrl.Trim().Length == 0 && !ct.IsCancellationRequested
                                                   && FoundryManager.Repairable(ex) && (!TestMode.Running || FoundryManager.AllowInSelfTest))
        {
            if (!await FoundryManager.EnsureReadyAsync(byUser: false, ct))
                throw new InvalidOperationException(ex.Message.Split('\n')[0] + " — the automatic repair didn't help: " + FoundryManager.LastStatus.TrimStart('✕', '⚠', ' '));
            return await ChatOnceAsync(messages, tools, schema, ct, onDelta);
        }
    }

    /// <summary>Time to the first streamed text and to the whole answer, per model call (ADR-034).</summary>
    public sealed record AiTiming(TimeSpan FirstToken, TimeSpan Total, bool Streamed, string Engine, string Model);
    public static AiTiming? LastTiming { get; private set; }
    /// <summary>Raised after every model call (any thread).</summary>
    public static event Action<AiTiming>? Timed;

    private static async Task<JsonObject> ChatOnceAsync(JsonArray messages, JsonArray? tools, JsonObject? schema, CancellationToken ct, Action<string>? onDelta)
    {
        var sw0 = Stopwatch.StartNew();
        TimeSpan? first = null;
        bool stream = onDelta != null && schema == null && S.AiStream;
        Action<string>? delta = !stream ? null : s =>
        {
            if (s.Length == 0) return;
            first ??= sw0.Elapsed;
            try { onDelta!(s); } catch (Exception ex) { Log.Write("AI stream", ex); }
        };
        try { return await ChatCoreAsync(messages, tools, schema, ct, delta); }
        finally
        {
            var t = new AiTiming(first ?? sw0.Elapsed, sw0.Elapsed, delta != null, Engine, Model);
            LastTiming = t;
            try { Timed?.Invoke(t); } catch { }
        }
    }

    private static async Task<JsonObject> ChatCoreAsync(JsonArray messages, JsonArray? tools, JsonObject? schema, CancellationToken ct, Action<string>? delta)
    {
        if (Engine == "off") throw new InvalidOperationException("AI is switched off (Settings → AI engine).");
        var url = await BaseUrlAsync(ct);
        if (!IsLocal(url) && !S.AiAllowCloud)
            throw new InvalidOperationException($"{url} is not a local/LAN address. Allow internet AI providers in Settings → AI engine first (server data would leave your network).");
        var msgs = Sanitize(messages);
        // Images go only to a model that verifiably reads them; never pretend a text model "looked" at a screenshot.
        if (msgs.OfType<JsonObject>().Any(m => m["images"] is JsonArray { Count: > 0 }))
        {
            var v = await VisionAsync(ct);
            if (!v.Supported) throw new InvalidOperationException("Not sent: " + v.Reason);
        }
        var sw = Stopwatch.StartNew();
        try
        {
            return Engine switch
            {
                "openai" or "npu" => await OpenAiAsync(url, msgs, tools, schema, ct, delta),
                "anthropic" => await AnthropicAsync(url, msgs, tools, schema, ct, delta),
                _ => await OllamaAsync(url, msgs, tools, schema, ct, delta),
            };
        }
        finally { LastLatency = sw.Elapsed; }
    }

    public static TimeSpan LastLatency { get; private set; }

}
