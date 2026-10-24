using DcxManager.Services;

namespace DcxManager.Ai;

/// <summary>
/// The AI's rows in the app's overviews (0.2.0 phase 3): the dashboard's "AI engine" source and tile, and the Privacy
/// &amp; network report's posture rows and destinations. The AI module contributes them; the dashboard and the report
/// render whatever modules contribute and never name the AI. Behaviour is exactly that of 0.1.20.
/// </summary>
public static class AiOverview
{
    // ---- dashboard ----------------------------------------------------------------------------------------------------

    /// <summary>The host the dashboard would contact, or null with the engine off (then there is no source to check).</summary>
    public static string? DashboardHost() => AiClient.Engine != "off" ? AiClient.EngineHost : null;

    /// <summary>One check: Ollama is asked for its version (3 s); other engines are not contacted from here.</summary>
    public static async Task<CheckOutcome> CheckAsync()
    {
        try
        {
            if (AiClient.Engine.StartsWith("ollama"))
            {
                var ver = await OllamaClient.VersionAsync(new CancellationTokenSource(3000).Token);
                return new($"Ollama {ver} · {AppSettings.Current.OllamaModel}", null);
            }
            return new($"{AiClient.Engine} · {AiClient.Model} (not contacted from here)", null);
        }
        catch (Exception ex) { return new(null, ex is OfflineBlockedException ? ex.Message : "not reachable — Settings → AI engine"); }
    }

    /// <summary>The summary tile from the last check (null = none yet; <paramref name="when"/> says why).</summary>
    public static TileText Tile(CheckOutcome? last, string when) =>
        AiClient.Engine == "off" ? new("off", "Settings → AI engine")
        : last?.Error != null ? new("offline", last.Error, Alert: true)
        : last?.Text != null ? new("online", last.Text)
        : new(AiClient.Engine, when);

    // ---- Privacy & network --------------------------------------------------------------------------------------------

    public static IEnumerable<PrivacyPosture> PrivacyPosture()
    {
        var s = AppSettings.Current;
        yield return new("External AI", s.AiAllowCloud ? "ENABLED" : "DISABLED", s.AiAllowCloud ? PrivacyLevel.Warn : PrivacyLevel.Good,
            s.AiAllowCloud ? "Internet AI providers may receive the text you choose to send." : "Only local / on-premises AI endpoints are used.");
        yield return new("Secret masking for AI", s.AiRedactSecrets ? "on" : "OFF", s.AiRedactSecrets ? PrivacyLevel.Good : PrivacyLevel.Warn);
    }

    public static IEnumerable<PrivacyEndpoint> PrivacyEndpoints()
    {
        var s = AppSettings.Current;
        var list = new List<PrivacyEndpoint>();
        string engine = s.AiEnabled ? s.AiEngine : "off";
        if (engine == "off") list.Add(new("AI engine", "—", null, "off", "AI features switched off"));
        else
        {
            string dest; NetClass cls;
            if (engine.StartsWith("ollama") && AppData.Current.FindHost(s.OllamaTunnelHostId) is { } th)
            { dest = $"SSH tunnel to {th.Display} ({th.Address})"; cls = NetworkPolicy.ClassifyHost(th.Address); }
            else
            {
                dest = engine switch
                {
                    "npu" => string.IsNullOrWhiteSpace(s.FoundryUrl) ? "Foundry Local on this PC (auto-discovered)" : s.FoundryUrl,
                    "openai" => s.OpenAiUrl,
                    "anthropic" => s.AnthropicUrl,
                    _ => s.OllamaUrl,
                };
                cls = engine == "npu" && string.IsNullOrWhiteSpace(s.FoundryUrl) ? NetClass.Local : NetworkPolicy.Classify(dest);
            }
            bool isPrivate = cls is NetClass.Local or NetClass.OnPremises;
            var dash = PrivacyRows.DashboardNote("ai");
            var state = (isPrivate || s.AiAllowCloud ? "used only when you ask" : "BLOCKED: internet AI providers are disabled")
                        + (engine.StartsWith("ollama") && dash.Length > 0 ? dash + " (version only)" : "");
            list.Add(new("AI engine · " + AiEngines.Title(engine), dest, cls, state, "Model requests: the text you choose to send"));
        }
        // Foundry Local's own set-up (ADR-026): Microsoft's installer and model downloads, run by Foundry Local / winget.
        if (engine == "npu" && string.IsNullOrWhiteSpace(s.FoundryUrl))
            list.Add(new("Foundry Local set-up (Microsoft)", "winget + Foundry Local's model catalogue", NetClass.External,
                s.FoundryAutoSetup ? "automatic: installs Foundry Local and downloads the chosen model when missing (not in offline mode; big downloads wait for mains power)" : "only when you press Set up now",
                "Microsoft's package and model files come down; none of your data goes up. Foundry Local itself may check its catalogue."));
        return list;
    }
}
