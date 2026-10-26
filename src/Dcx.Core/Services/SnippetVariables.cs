using System.Text;
using System.Text.RegularExpressions;

namespace DcxManager.Services;

/// <summary>
/// Snippet variables and scope (0.6.29, ADR-104). A snippet may hold {host}, {name}, {address}, {user}, {port},
/// {ask:Label} / {ask:Label=default} (asked when it's sent) and {pick:Label|command} (a choice from a command's output
/// lines on the host); {{ and }} are literal braces. Every value is quoted for the shell it lands in, and line breaks and
/// control characters are dropped from values, so an answer can never run a second command. Pure logic: the terminal asks.
/// </summary>
public static class SnippetVariables
{
    /// <summary>Where a snippet is being sent: the host (null for a local shell), the shell ("posix", "powershell" or ""
    /// when it can't be quoted reliably), and the tab's environment label.</summary>
    public sealed record Context(HostEntry? Host, string Shell, string Environment, bool Local);

    /// <summary>A question the snippet asks: free text with a default, or a pick from a command's output.</summary>
    public sealed record Question(string Label, string Default, string? PickCommand);

    private static readonly Regex Token = new(@"\{\{|\}\}|\{(host|name|address|user|port)\}|\{ask:([^}=]{1,60})(?:=([^}]{0,200}))?\}|\{pick:([^}|]{1,60})\|([^}]{1,400})\}", RegexOptions.Compiled);

    /// <summary>The questions a snippet asks, in order, once each (the same label asked twice is one question).</summary>
    public static IReadOnlyList<Question> Questions(string command)
    {
        var list = new List<Question>();
        foreach (Match m in Token.Matches(command ?? ""))
        {
            Question? q = m.Groups[2].Success ? new(m.Groups[2].Value.Trim(), m.Groups[3].Value, null)
                : m.Groups[4].Success ? new(m.Groups[4].Value.Trim(), "", m.Groups[5].Value) : null;
            if (q != null && !list.Any(x => x.Label.Equals(q.Label, StringComparison.OrdinalIgnoreCase))) list.Add(q);
        }
        return list;
    }

    public static bool HasVariables(string command) => Token.Matches(command ?? "").Any(m => m.Value is not ("{{" or "}}"));

    /// <summary>The command with every variable filled in and quoted, or null when a question was cancelled (answer
    /// returned null). <paramref name="answer"/> is called once per <see cref="Questions"/> entry.</summary>
    public static string? Expand(string command, Context c, Func<Question, string?> answer)
    {
        var answers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var q in Questions(command))
        {
            var a = answer(q);
            if (a == null) return null;
            answers[q.Label] = a;
        }
        var h = c.Host;
        return Token.Replace(command ?? "", m =>
        {
            if (m.Value == "{{") return "{";
            if (m.Value == "}}") return "}";
            if (m.Groups[1].Success)
            {
                var v = m.Groups[1].Value switch
                {
                    "host" or "name" => h?.Name ?? System.Environment.MachineName,
                    "address" => h?.Address ?? "127.0.0.1",
                    "user" => h?.Username ?? System.Environment.UserName,
                    _ => (h?.Port ?? 0).ToString(),
                };
                return Quote(v, c.Shell);
            }
            var label = (m.Groups[2].Success ? m.Groups[2].Value : m.Groups[4].Value).Trim();
            return Quote(answers.TryGetValue(label, out var s) ? s : "", c.Shell);
        });
    }

    /// <summary>A value as one word for the shell: POSIX single quotes ('\'' inside), PowerShell single quotes ('' inside);
    /// plain words are left as they are. Line breaks and control characters are always dropped.</summary>
    public static string Quote(string value, string shell)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value ?? "") if (ch >= 0x20 && ch != 0x7F && !(ch >= 0x80 && ch <= 0x9F)) sb.Append(ch);
        var v = sb.ToString();
        if (v.Length > 0 && v.All(ch => char.IsAsciiLetterOrDigit(ch) || "_@%+=:,./-".Contains(ch))) return v;
        return shell switch
        {
            "posix" => "'" + v.Replace("'", "'\\''") + "'",
            "powershell" => "'" + v.Replace("'", "''") + "'",
            _ => v,   // cmd, network gear, serial: no quoting can be relied on; the value still can't break the line
        };
    }

    /// <summary>The choices from a pick command's output: trimmed non-empty lines, first 500.</summary>
    public static List<string> Choices(string output) =>
        (output ?? "").Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && l.Length <= 300).Distinct().Take(500).ToList();

    /// <summary>Whether a snippet's button shows on this terminal: empty scope = everywhere, else any one token matches
    /// (host:name, folder:path with its subfolders, tag:tag, env:ENVIRONMENT, shell:posix|powershell, local).</summary>
    public static bool InScope(Snippet s, Context c)
    {
        var tokens = (s.Scope ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0) return true;
        var h = c.Host;
        foreach (var t in tokens)
        {
            int i = t.IndexOf(':');
            var kind = (i < 0 ? t : t[..i]).Trim().ToLowerInvariant();
            var val = i < 0 ? "" : t[(i + 1)..].Trim();
            bool hit = kind switch
            {
                "local" => c.Local,
                "host" => h != null && (h.Name.Equals(val, StringComparison.OrdinalIgnoreCase) || h.Address.Equals(val, StringComparison.OrdinalIgnoreCase)),
                "folder" => h != null && val.Length > 0 && (h.Group.Equals(val.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) || h.Group.StartsWith(val.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase)),
                "tag" => h != null && h.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains(val, StringComparer.OrdinalIgnoreCase),
                "env" => val.Length > 0 && Envs.Normalize(c.Environment) == Envs.Normalize(val),
                "shell" => c.Shell.Length > 0 && c.Shell.Equals(val, StringComparison.OrdinalIgnoreCase),
                _ => false,
            };
            if (hit) return true;
        }
        return false;
    }

    /// <summary>Why a scope can't be used, or null.</summary>
    public static string? CheckScope(string? scope)
    {
        foreach (var t in (scope ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int i = t.IndexOf(':');
            var kind = (i < 0 ? t : t[..i]).Trim().ToLowerInvariant();
            if (kind == "local" && i < 0) continue;
            if (kind is not ("host" or "folder" or "tag" or "env" or "shell") || i < 0 || t[(i + 1)..].Trim().Length == 0)
                return $"\"{t}\" isn't a scope: use host:<name>, folder:<path>, tag:<tag>, env:<ENVIRONMENT>, shell:posix|powershell or local, separated by commas.";
        }
        return null;
    }
}
