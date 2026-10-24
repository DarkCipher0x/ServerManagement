using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DcxManager.Services;

namespace DcxManager.Ai;

/// <summary>
/// "✦ Identify with AI" in the host import (0.5.1 #18): the model gets each address's reverse-DNS name, open ports and
/// banners (fenced as untrusted data — a banner is written by whoever runs the server) and answers in a JSON schema with a
/// short name, the platform, a folder and what the machine probably is. Every answer is checked: only addresses that were
/// scanned, names and folders cleaned and bounded, platforms from the fixed list. Nothing is imported by the AI; the
/// suggestions only fill the review grid, where the user changes and ticks them.
/// </summary>
public sealed class HostImportAi : IHostIdentifier
{
    public string? WhyNot() => AiClient.Blocked(AiFeature.Operator);

    private static readonly JsonObject Schema = (JsonObject)JsonNode.Parse("""
    {
      "type": "object",
      "properties": {
        "hosts": {
          "type": "array",
          "items": {
            "type": "object",
            "properties": {
              "address": { "type": "string" },
              "name": { "type": "string" },
              "platform": { "type": "string", "enum": ["linux", "windows", "macos", "network", "other"] },
              "folder": { "type": "string" },
              "role": { "type": "string" },
              "reason": { "type": "string" }
            },
            "required": ["address", "name", "platform", "folder", "role", "reason"]
          }
        }
      },
      "required": ["hosts"]
    }
    """)!;

    private static readonly Regex NameRx = new(@"[^\p{L}\p{N} ._\-()]", RegexOptions.CultureInvariant);

    public async Task<IReadOnlyList<HostSuggestion>> IdentifyAsync(IReadOnlyList<HostClue> clues, CancellationToken ct)
    {
        if (WhyNot() is { } why) throw new InvalidOperationException(why);
        var sb = new StringBuilder();
        foreach (var c in clues.Take(256))
        {
            sb.AppendLine($"- address {c.Address}" + (c.Name.Length > 0 ? $" · reverse DNS {c.Name}" : ""));
            foreach (var p in c.Ports) sb.AppendLine($"    port {p.Port} {p.Service}" + (p.Detail.Length > 0 ? $": {p.Detail}" : ""));
        }
        var budget = Math.Max(2000, AiClient.PromptBudgetChars - 2000);
        var data = sb.Length > budget ? sb.ToString()[..budget] + "\n(…more addresses omitted)" : sb.ToString();
        const string system =
            "You help an infrastructure engineer add machines found on their own network to a connection manager. " +
            "For each address, from the reverse-DNS name, the open ports and the banners only, suggest: a short host name (lower case, from the DNS name when there is one), " +
            "the platform (linux, windows, macos, network for switches, routers, firewalls, NAS appliances and similar, other), a folder to file it under " +
            "(e.g. Servers, Desktops, Network, Storage, Lab — at most two levels, like Servers/Linux), what it probably is (role: e.g. \"Ubuntu server\", \"Mac (Screen Sharing on)\", \"UniFi device\"), " +
            "and the evidence in a few words. Never invent an address. If unsure, say so in the reason and choose other.";
        var json = await AiClient.StructuredAsync(system, Capabilities.Untrusted("network scan", data, budget + 500), Schema, ct);
        var scanned = clues.Select(c => c.Address).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var list = new List<HostSuggestion>();
        foreach (var h in json["hosts"]?.AsArray().OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
        {
            var address = Str(h, "address", 64);
            if (!scanned.Contains(address) || list.Any(x => x.Address.Equals(address, StringComparison.OrdinalIgnoreCase))) continue;
            var platform = Str(h, "platform", 16) is var p && p is "linux" or "windows" or "macos" or "network" or "other" ? p : "other";
            var name = NameRx.Replace(Str(h, "name", 60), "").Trim();
            var folder = AppData.NormalizeFolder(string.Join('/', Str(h, "folder", 80).Split('/').Take(2).Select(s => NameRx.Replace(s, "").Trim())));
            list.Add(new HostSuggestion(address, name, platform, folder, Str(h, "role", 80), Str(h, "reason", 200)));
        }
        AuditLog.Write("you", "hosts", "host import: AI suggested names", $"{list.Count} of {clues.Count} address(es) · {AiClient.Engine}");
        return list;
    }

    private static string Str(JsonObject o, string k, int max)
    {
        var s = o[k] is JsonValue v && v.TryGetValue<string>(out var t) ? t.Trim() : "";
        s = new string(s.Where(ch => !char.IsControl(ch)).ToArray());
        return s.Length > max ? s[..max] : s;
    }
}
