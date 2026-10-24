namespace DcxManager.Services;

/// <summary>Which of the assistant's uses an action is; each can be switched off on its own (Settings → AI permissions).</summary>
public enum AssistUse { Files, Helper, LogAnalysis, Reports, Operator }

/// <summary>
/// What the screens may ask of an assistant (0.2.0 phase 4) without knowing which one, or whether one exists: "here is
/// material the user picked, do this with it". The screens build the material from their own data; the assistant decides
/// how it is sent (local or internet engine, masking, the confirmation before an internet provider) and where the answer
/// appears. The AI module registers the only implementation; with no assistant registered (AI off, phase 5)
/// <see cref="Assist.Current"/> is null and the screens offer no "✦ … with AI" item at all.
/// </summary>
public interface IAssistant
{
    /// <summary>Null when <paramref name="use"/> may run now, else why not (a disabled item's reason).</summary>
    string? WhyNot(AssistUse use = AssistUse.Files);

    /// <param name="subject">What it is, shown to the user and the model ("nginx.conf on web-01").</param>
    /// <param name="content">The material itself (bounded and masked by the assistant).</param>
    /// <param name="task">What to do with it ("Explain what this configuration does…").</param>
    Task AskAsync(string subject, string content, string task, AssistUse use = AssistUse.Files);

    /// <summary>"Data being sent" preview with a choice of scope; null when the user cancels.</summary>
    Task<AssistChoice?> PreviewAsync(string source, string what, string fullText);

    /// <summary>A PDF: extract its text, preview, then ask. Returns the extraction so the caller can reuse it.</summary>
    Task<Documents.PdfExtraction?> ReviewPdfAsync(string title, byte[] pdf, string task, Action<string>? status = null, Documents.PdfExtraction? known = null);

    /// <summary>Opens the assistant's own chat (the AI operator, which can use the app's capabilities with approval) and
    /// asks <paramref name="question"/> there.</summary>
    void AskOperator(string question);

    /// <summary>A short answer to a help question, grounded only in the built-in help articles (<see cref="HelpKb"/>).
    /// Throws when the assistant can't answer; callers fall back to the articles.</summary>
    Task<string> AnswerHelpAsync(string question, string? screen, CancellationToken ct);

    /// <summary>The engine and model as shown to the user ("Ollama · qwen2.5").</summary>
    string EngineLine { get; }
    /// <summary>The model's name for status lines.</summary>
    string ModelName { get; }
    /// <summary>How long the last answer took.</summary>
    TimeSpan LastLatency { get; }

    /// <summary>"engine at host" when the assistant sends to an internet provider, null when it stays on this PC or the
    /// local network (the dashboard warns about the former).</summary>
    string? InternetEndpoint { get; }
}

/// <summary>The text the user chose to send and a label for its scope ("everything", "pages 1–3", …).</summary>
public sealed record AssistChoice(string Text, string ScopeLabel);

/// <summary>The registered assistant, or null.</summary>
public static class Assist
{
    public static IAssistant? Current { get; private set; }
    /// <summary>Called by the app modules when a module registers its assistant (one at most; the first wins).</summary>
    public static void Register(IAssistant assistant) => Current ??= assistant;
    internal static void ResetForTest() => Current = null;
}
