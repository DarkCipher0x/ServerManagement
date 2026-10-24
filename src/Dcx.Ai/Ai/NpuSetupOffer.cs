using DcxManager.Services;

namespace DcxManager.Ai;

/// <summary>
/// When the AI Engineer loads on a PC with an NPU (a Copilot+ PC) that isn't using it, offer to set up the best options
/// for it: Foundry Local, phi-4-mini on the NPU, the hands-off set-up (DarkCipher0x, s45/s46). It asks first, with a "Remember my
/// choice" tick box (<see cref="AppSettings.AiNpuSetupChoice"/>: ask / always / never); nothing changes without the yes
/// or a remembered "always". A first run needs no question: the default (<see cref="AiDefaults"/>) already is Foundry Local
/// with phi-4-mini, NPU first, and <see cref="FoundryManager.AutoStartAsync"/> sets it up. Not every PC has an NPU: then
/// nothing is offered.
/// </summary>
public static class NpuSetupOffer
{
    /// <summary>The question (self-test seam: answers without a window). Returns (set it up, remember).</summary>
    internal static Func<string, (bool Yes, bool Remember)>? AskOverride;

    /// <summary>The real question (a window with the "Remember my choice" tick box), registered by the AI module's screens
    /// (Views/NpuSetupDialog.cs): this class stays below the UI.</summary>
    public static Func<string, (bool Yes, bool Remember)>? Dialog { get; set; }

    /// <summary>Whether the settings already run on the NPU: Foundry Local on this PC with phi-4-mini (its alias resolves to
    /// the NPU build here, <see cref="FoundryManager.Resolve"/>) or an explicit NPU build.</summary>
    public static bool UsesNpu(AppSettings s) =>
        s.AiEngine == "npu" && s.FoundryUrl.Trim().Length == 0
        && (s.FoundryModel.Equals(AiDefaults.NpuModel, StringComparison.OrdinalIgnoreCase) || s.FoundryModel.Contains("npu", StringComparison.OrdinalIgnoreCase));

    /// <summary>Checks, asks (or uses the remembered answer) and, on yes, switches and starts the set-up. True when switched.</summary>
    public static async Task<bool> RunAsync(string? npuOverride = null)
    {
        var s = AppSettings.Current;
        if (!s.AiEnabled || s.AiNpuSetupChoice == "never") return false;
        bool selfTest = TestMode.Running || Environment.GetCommandLineArgs().Contains("--selftest");
        var ask = AskOverride ?? (selfTest ? null : Dialog);   // never a window in the self-test
        if (ask == null) return false;
        var npu = npuOverride ?? (await AiHardware.DetectAsync()).Npu;
        if (npu == null || UsesNpu(s)) return false;

        bool yes;
        if (s.AiNpuSetupChoice == "always") yes = true;
        else
        {
            var (y, remember) = AskOverride != null ? AskOverride(npu) : await Ui.Thread.InvokeAsync(() => ask(npu));
            yes = y;
            if (remember) { s.AiNpuSetupChoice = y ? "always" : "never"; AppSettings.Save(); }
        }
        if (!yes) return false;
        s.AiEngine = "npu"; s.FoundryUrl = ""; s.FoundryModel = AiDefaults.NpuModel; s.FoundryAutoSetup = true;
        AppSettings.Save();
        AuditLog.Write("you", "AI", "switched to Foundry Local on the NPU", npu);
        if (!selfTest) _ = FoundryManager.EnsureReadyAsync(byUser: true);
        return true;
    }
}
