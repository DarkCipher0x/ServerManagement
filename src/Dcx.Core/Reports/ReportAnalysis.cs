namespace DcxManager.Reports;

/// <summary>
/// What adds an analysis pass to the offline reports (0.2.0 phase 4). The AI module registers the only one
/// (<c>Ai\ReportAnalyst.cs</c>); with none registered, reports are the offline digest and rule findings only and the
/// screens show no AI option.
/// </summary>
public interface IReportAnalyst
{
    /// <summary>Null when the analysis for <paramref name="use"/> (reports, or log analysis) may run now, else why not.</summary>
    string? WhyNot(Services.AssistUse use = Services.AssistUse.Reports);
    /// <summary>Adds the analysis to <paramref name="r"/> (summary, health, findings, next steps; AiUsed, Engine).</summary>
    Task AnalyzeAsync(ServerReport r, CancellationToken ct);
    /// <summary>How much digest text fits in one analysis (characters).</summary>
    int BudgetChars { get; }
    /// <summary>The engine and model as shown to the user ("Ollama · qwen2.5").</summary>
    string EngineLine { get; }
    /// <summary>The model's name for status lines.</summary>
    string ModelName { get; }
    /// <summary>How long the last analysis took.</summary>
    TimeSpan LastLatency { get; }
    /// <summary>Extra actions on a report (label, tooltip, run) — e.g. "Open in Claude Code".</summary>
    IReadOnlyList<ReportAction> Actions { get; }
}

/// <summary>A report action: its toolbar label, its menu label, tooltip and what it does with the shown report.</summary>
public sealed record ReportAction(string Label, string MenuLabel, string Tip, Action<ServerReport> Run);

/// <summary>The registered analyst, or null.</summary>
public static class ReportAnalysis
{
    public static IReportAnalyst? Current { get; private set; }
    public static void Register(IReportAnalyst analyst) => Current ??= analyst;
    internal static void ResetForTest() => Current = null;
}
