using System.Text;
using System.Text.Json.Nodes;
using DcxManager.Services;

namespace DcxManager.Ai;

/// <summary>The help's AI part: a short answer grounded only in the top offline search hits (<see cref="HelpKb"/>).</summary>
public static class HelpAi
{
    /// <summary>Grounded answer from the top articles. Throws if the AI is unavailable — callers fall back to the articles.</summary>
    public static async Task<string> AnswerAsync(string question, string? screen, CancellationToken ct)
    {
        var hits = HelpKb.Search(question, 4);
        var context = new StringBuilder();
        foreach (var (a, _) in hits) context.AppendLine(a.Text).AppendLine();
        var budget = Math.Max(4000, AiClient.PromptBudgetChars - 1500);
        var ctxText = context.Length > budget ? context.ToString()[..budget] : context.ToString();
        var msgs = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] =
                "You are the built-in help for the Windows app \"DarkCipher ServerManagement\". Answer ONLY from the help articles below. " +
                "Give the exact place to click (sidebar item, tab, button or right-click menu) and keyboard shortcuts when relevant. " +
                "Be brief: 2–6 sentences or a short numbered list. If the articles don't cover it, say so and name the closest feature. Never invent features.\n\n" +
                "# Help articles\n" + ctxText },
            new JsonObject { ["role"] = "user", ["content"] = (screen != null ? $"(I'm on the \"{screen}\" screen.) " : "") + question },
        };
        var reply = await AiClient.ChatAsync(msgs, null, null, ct);
        return reply["content"]?.GetValue<string>()?.Trim() ?? "";
    }
}
