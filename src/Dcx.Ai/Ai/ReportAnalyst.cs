using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DcxManager.Reports;
using DcxManager.Services;

namespace DcxManager.Ai;

/// <summary>
/// The AI's analysis pass over an offline report (0.2.0 phase 4: moved out of ReportEngine unchanged). The AI module
/// registers it as the app's <see cref="IReportAnalyst"/>; the model answers in a JSON schema, its findings are added to
/// the deterministic rule findings (restatements dropped) and ranked.
/// </summary>
public sealed class ReportAnalyst : IReportAnalyst
{
    public string? WhyNot(AssistUse use = AssistUse.Reports) => AiClient.Blocked(AiAssistant.FeatureOf(use));
    public int BudgetChars => AiClient.PromptBudgetChars;
    public string EngineLine => $"{AiEngines.Title(AiClient.Engine)} · {AiClient.Model}";
    public string ModelName => AiClient.Model;
    public TimeSpan LastLatency => AiClient.LastLatency;
    public IReadOnlyList<ReportAction> Actions { get; } = new[]
    {
        new ReportAction("⌁ Open in Claude Code", "Open in Claude Code (local)", "Claude Code on this PC, powered by your local Ollama model", ClaudeCode.OpenWithReport),
    };

    public static readonly JsonObject Schema = (JsonObject)JsonNode.Parse("""
    {
      "type": "object",
      "properties": {
        "health": { "type": "string", "enum": ["ok", "degraded", "critical"] },
        "summary": { "type": "string" },
        "findings": {
          "type": "array",
          "items": {
            "type": "object",
            "properties": {
              "host": { "type": "string" },
              "severity": { "type": "string", "enum": ["critical", "high", "medium", "low", "info"] },
              "title": { "type": "string" },
              "detail": { "type": "string" },
              "evidence": { "type": "string" },
              "recommendation": { "type": "string" },
              "commands": { "type": "array", "items": { "type": "string" } }
            },
            "required": ["severity", "title", "detail", "recommendation"]
          }
        },
        "next_steps": { "type": "array", "items": { "type": "string" } }
      },
      "required": ["health", "summary", "findings", "next_steps"]
    }
    """)!;

    public async Task AnalyzeAsync(ServerReport r, CancellationToken ct)
    {
        int budget = AiClient.PromptBudgetChars;
        var sb = new StringBuilder();
        sb.AppendLine($"Report window: since {r.Window}. Hosts: {string.Join(", ", r.Hosts)}.");
        if (r.Notes.Length > 0) sb.AppendLine("Operator focus: " + r.Notes);
        sb.AppendLine("\n## Rule-based findings already detected (confirm, refine or dismiss them)");
        foreach (var f in r.Findings.Where(f => f.Source == "rule")) sb.AppendLine($"- [{f.Severity}] {f.Host}: {f.Title} — {f.Detail}");
        sb.AppendLine("\n## Evidence (logs are pre-digested: [severity] ×count first→last template)");
        foreach (var e in r.Evidence)
        {
            var block = $"\n### {e.Host} · {e.Title}{(e.Error.Length > 0 ? " (collection error: " + e.Error + ")" : "")}\n{e.Digest}\n";
            if (sb.Length + block.Length > budget) { sb.AppendLine($"\n(…{r.Evidence.Count - r.Evidence.IndexOf(e)} more sections omitted to fit the model's context)"); break; }
            sb.Append(block);
        }
        const string system =
            "You are a senior Linux and Windows SRE writing an operations report for an experienced infrastructure engineer. " +
            "Use only the evidence given; never invent hosts, numbers or log lines. Rank findings by impact. " +
            "Give exact, safe, read-only diagnostic commands first (shell for Linux hosts, PowerShell for a host called \"This PC\" — Windows Event Viewer, Defender, firewall, services); mark anything that changes state clearly in the recommendation. " +
            "Keep each finding tight: what is wrong, why it matters, what to do. Merge duplicates. If everything is healthy, say so.";
        var json = await AiClient.StructuredAsync(system, sb.ToString(), Schema, ct);
        r.AiUsed = true;
        r.Engine = await AiClient.DescribeAsync();
        r.Summary = json["summary"]?.GetValue<string>() ?? r.Summary;
        if (json["health"]?.GetValue<string>() is { } h && h is "ok" or "degraded" or "critical") r.Health = h;
        var aiFindings = new List<Finding>();
        foreach (var f in json["findings"]?.AsArray().OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
        {
            aiFindings.Add(new Finding
            {
                Host = Str(f, "host") is { Length: > 0 } hh ? hh : r.Hosts.FirstOrDefault() ?? "",
                Severity = Str(f, "severity") is { Length: > 0 } s ? s : "info",
                Title = Str(f, "title"), Detail = Str(f, "detail"), Evidence = Str(f, "evidence"), Recommendation = Str(f, "recommendation"),
                Commands = f["commands"]?.AsArray().Select(c => c?.ToString() ?? "").Where(c => c.Length > 0).ToList() ?? new(),
                Source = "ai",
            });
        }
        // Keep rule findings (deterministic, auditable) and add the AI's that aren't restatements of them.
        foreach (var a in aiFindings)
            if (!r.Findings.Any(x => Similar(x.Title, a.Title))) r.Findings.Add(a);
        r.Findings = r.Findings.OrderByDescending(f => ReportEngine.Rank(f.Severity)).ToList();
        r.NextSteps = json["next_steps"]?.AsArray().Select(c => c?.ToString() ?? "").Where(c => c.Length > 0).ToList() ?? new();
    }

    private static bool Similar(string a, string b)
    {
        static HashSet<string> W(string s) => Regex.Matches(s.ToLowerInvariant(), "[a-z0-9/]{3,}").Select(m => m.Value).ToHashSet();
        var x = W(a); var y = W(b);
        if (x.Count == 0 || y.Count == 0) return false;
        return x.Intersect(y).Count() * 2.0 / (x.Count + y.Count) > 0.6;
    }

    private static string Str(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : o[k]?.ToString() ?? "";
}