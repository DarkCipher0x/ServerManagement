using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DcxManager.Services;
using DcxManager.Views;

namespace DcxManager.Ai;

/// <summary>
/// The one-click "✦ … with AI" actions views offer (explain a file, review a document, explain an error…).
/// Nothing is sent until the user clicks; an internet provider gets an explicit "send this?" first; secrets are masked by
/// AiClient. The material lands as a context item in the AI Engineer sidebar (<see cref="AiWorkspace"/>) and the answer
/// appears there, so follow-ups and every shared item stay in one visible place.
/// </summary>
public static class AiActions
{
    /// <summary>Text for menu items: the feature's state, so a disabled action says why.</summary>
    public static string? Why(AiFeature f) => AiClient.Blocked(f);

    public static object?[] MenuItems(AiFeature f, string label, Func<Task> run) =>
        new object?[] { Menus.AsyncItem("✦ " + label, run, enabled: Why(f) == null) };

    /// <param name="subject">What it is, shown to the user and the model ("nginx.conf on web-01").</param>
    /// <param name="content">The material itself (truncated to the engine's budget).</param>
    /// <param name="task">What to do with it ("Explain what this configuration does…").</param>
    public static async Task AskAsync(AiFeature f, string subject, string content, string task, string? system = null)
    {
        if (AiClient.Blocked(f) is { } why) { Ui.Dialogs.Info("AI", why); return; }
        string url;
        try { url = await AiClient.BaseUrlAsync(new CancellationTokenSource(8000).Token); }
        catch (Exception ex) { Ui.Dialogs.Error("AI", ex.Message, explain: false); return; }
        if (!AiClient.IsLocal(url))
        {
            if (!AppSettings.Current.AiAllowCloud) { Ui.Dialogs.Error("AI", $"{url} is not a local/LAN address and internet AI providers are not allowed (Settings → AI engine).", explain: false); return; }
            if (!ConfirmInternet(url, $"“{subject}” ({content.Length:N0} characters)")) return;
        }
        // Into the one AI workspace: the material becomes a visible context item (✓ included), the sidebar (or its
        // detached window) comes forward, and the task is asked in the active conversation. No per-action windows.
        var ws = AiWorkspace.Current;
        ws.AddContext(subject, content);
        if (system != null) ws.AddContext("help articles for " + subject, system, AiContextKind.Help);
        ws.RequestFocus();
        AuditLog.Write("you", "ai", "asked AI about " + subject, AiClient.Model);
        await ws.SendAsync($"{task}\n\n(About the context item “{AiClient.Redact(subject)}”.)", f, consented: true);
    }

    /// <summary>The one-time "send to the internet?" question before material leaves for a non-local provider.</summary>
    public static bool ConfirmInternet(string url, string what) =>
        Ui.Dialogs.Confirm("Send to an internet AI provider?",
            $"{what} will be sent to {(Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url)} for this answer.\n" +
            (AppSettings.Current.AiRedactSecrets ? "Keys, passwords and tokens are masked first." : "⚠ Secret masking is OFF in Settings.") +
            "\n\nOnly the context items marked ✓ in the AI sidebar are included; nothing else from the app.", "Send");

    // ---- explain this error / option / screen ----------------------------------------------------------------------

    /// <summary>Error text that is safe to hand to a model: type + message (+ inner) + stack frames without source paths,
    /// the Windows profile folder and user name replaced, secrets masked.</summary>
    public static string ErrorText(string message, Exception? ex = null)
    {
        var sb = new StringBuilder(message.Trim());
        for (var e = ex; e != null; e = e.InnerException)
            sb.Append("\n\n").Append(e.GetType().FullName).Append(": ").Append(e.Message);
        if (ex?.StackTrace is { Length: > 0 } st)
            sb.Append("\n\nStack (app frames):\n").Append(Regex.Replace(st, @" in [A-Za-z]:\\[^\r\n]*?:line \d+", ""));
        var text = sb.ToString();
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (profile.Length > 3) text = text.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        if (Environment.UserName is { Length: > 2 } user) text = Regex.Replace(text, $@"\b{Regex.Escape(user)}\b", "%USERNAME%", RegexOptions.IgnoreCase);
        return AiClient.Redact(text);
    }

    /// <summary>"✦ Explain this error with AI" (error dialogs and the unhandled-error dialog). Only on click.</summary>
    public static Task ExplainErrorAsync(string title, string message, Exception? ex = null) =>
        AskAsync(AiFeature.Files, "error: " + title, ErrorText(message, ex),
            "This error was shown by the Windows app DarkCipher ServerManagement. Explain in plain terms what most likely caused it " +
            "and what to check or do next (numbered steps). If it comes from a remote server, SSH, the network or the Vultr API, say which. " +
            "Don't guess beyond the text.", HelpSystem(HelpFor(title + " " + message, 3)));

    /// <summary>"✦ Explain this option with AI" in Settings: the option's label, hint and (non-secret) current value,
    /// grounded in the matching help articles.</summary>
    public static Task ExplainOptionAsync(string card, string cardHint, string option, string? value, string? tooltip)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Settings card: {card}");
        if (cardHint.Length > 0) sb.AppendLine($"Card description: {cardHint}");
        sb.AppendLine($"Option: {option}");
        if (!string.IsNullOrWhiteSpace(tooltip)) sb.AppendLine($"Tooltip: {tooltip}");
        if (!string.IsNullOrWhiteSpace(value)) sb.AppendLine($"Current value: {value}");
        return AskAsync(AiFeature.Files, $"setting “{option}”", AiClient.Redact(sb.ToString()),
            "Explain what this setting of DarkCipher ServerManagement does, when to change it, and any security or performance trade-off. " +
            "Be brief (3–6 sentences).", HelpSystem(HelpFor(card + " " + option, 3)));
    }

    /// <summary>"✦ Ask AI about this screen" (tab right-click): the user's question, grounded in the help articles for
    /// that screen plus the screen's visible button labels (UI text only — no list contents, hosts or secrets).</summary>
    public static async Task AskAboutScreenAsync(string screen, FrameworkElement? content)
    {
        if (AiClient.Blocked(AiFeature.Helper) is { } why) { Ui.Dialogs.Info("AI", why); return; }
        if (Ui.Dialogs.Prompt("✦ Ask AI about this screen", $"Your question about the “{screen}” screen:", "What can I do on this screen, and how?") is not { Length: > 0 } q) return;
        var buttons = content == null ? new List<string>() : Menus.Descendants<System.Windows.Controls.Primitives.ButtonBase>(content)
            .Select(b => b.Content as string ?? (b.Content as TextBlock)?.Text ?? b.ToolTip as string)
            .Where(t => !string.IsNullOrWhiteSpace(t) && t!.Trim().Length > 1).Select(t => t!.Trim()).Distinct().Take(60).ToList();
        var material = $"Screen: {screen}\n" + (buttons.Count > 0 ? "Buttons and options on it: " + string.Join(" · ", buttons) + "\n" : "");
        await AskAsync(AiFeature.Helper, $"the “{screen}” screen", AiClient.Redact(material), q, HelpSystem(HelpFor(screen + " " + q, 4)));
    }

    /// <summary>Help articles for grounding (BM25 over the built-in help).</summary>
    public static List<HelpArticle> HelpFor(string query, int take) => HelpKb.Search(query, take).Select(h => h.Article).ToList();

    public static string HelpSystem(IEnumerable<HelpArticle> articles)
    {
        var ctx = string.Join("\n\n", articles.Select(a => a.Text));
        var budget = Math.Max(3000, AiClient.PromptBudgetChars / 2);
        if (ctx.Length > budget) ctx = ctx[..budget];
        return "You are the built-in help of the Windows app \"DarkCipher ServerManagement\" and an expert Linux, Windows, networking and cloud engineer. " +
               "Use the help articles below for anything about the app: give the exact place to click (sidebar item, tab, button or right-click menu). " +
               "Never invent app features, menus or settings that the articles or the material don't mention; say so when they don't cover it. Be concise.\n\n" +
               "# Help articles\n" + (ctx.Length > 0 ? ctx : "(none matched)");
    }
}
