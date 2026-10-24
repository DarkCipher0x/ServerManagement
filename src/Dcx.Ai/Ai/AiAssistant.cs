using DcxManager.Services;

namespace DcxManager.Ai;

/// <summary>The AI as the app's <see cref="IAssistant"/> (0.2.0 phase 4): the screens' "✦ … with AI" actions arrive here
/// and go through <see cref="AiActions"/> and the "Data being sent to AI" preview exactly as before.</summary>
public sealed class AiAssistant : IAssistant
{
    public static AiFeature FeatureOf(AssistUse use) => use switch
    {
        AssistUse.Helper => AiFeature.Helper,
        AssistUse.LogAnalysis => AiFeature.LogAnalysis,
        AssistUse.Reports => AiFeature.Reports,
        AssistUse.Operator => AiFeature.Operator,
        _ => AiFeature.Files,
    };

    public string? WhyNot(AssistUse use = AssistUse.Files) => AiActions.Why(FeatureOf(use));

    public Task AskAsync(string subject, string content, string task, AssistUse use = AssistUse.Files) =>
        AiActions.AskAsync(FeatureOf(use), subject, content, task);

    public async Task<AssistChoice?> PreviewAsync(string source, string what, string fullText) =>
        await Views.AiSendPreview.ShowAsync(source, what, null, fullText) is { } c ? new AssistChoice(c.Text, c.ScopeLabel) : null;

    public Task<Documents.PdfExtraction?> ReviewPdfAsync(string title, byte[] pdf, string task, Action<string>? status = null, Documents.PdfExtraction? known = null) =>
        Views.AiSendPreview.ReviewPdfAsync(title, pdf, task, status, known);

    public Task<string> AnswerHelpAsync(string question, string? screen, CancellationToken ct) => HelpAi.AnswerAsync(question, screen, ct);
    public string EngineLine => $"{AiEngines.Title(AiClient.Engine)} · {AiClient.Model}";
    public string ModelName => AiClient.Model;
    public TimeSpan LastLatency => AiClient.LastLatency;
    public string? InternetEndpoint => AiClient.Engine is "openai" or "anthropic" && NetworkPolicy.ClassifyHost(AiClient.EngineHost) == NetClass.External
        ? $"{AiClient.Engine} at {AiClient.EngineHost}" : null;

    public void AskOperator(string question)
    {
        Ui.Window.Navigate("assistant");
        Views.AssistantView.Current?.AskAbout(question);
    }
}
