using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace DcxManager.Services;

/// <summary>A help article. <paramref name="Module"/> is the module the article belongs to ("ai"), or null for the app's own.</summary>
public sealed record HelpArticle(string Title, string Keywords, string Body, string? Module = null)
{
    public string Text => $"## {Title}\n{Body}";
}

/// <summary>
/// The built-in help: articles embedded in the exe (Assets/help/help.md), ranked with BM25 on this PC — instant and
/// fully offline. An article may carry a <c>module: &lt;id&gt;</c> line after its title (and <c>keywords:</c> line); it is
/// shown only while a registered module owns that id (0.2.0 phase 3: with AI off, the AI's articles don't exist). The AI's
/// grounded answer on top of the search lives with the AI (<c>Ai\HelpAi.cs</c>); nothing here uses an AI engine.
/// </summary>
public static class HelpKb
{
    private static readonly object Gate = new();
    private static readonly HashSet<string> Owned = new(StringComparer.OrdinalIgnoreCase);
    private static List<HelpArticle>? _articles;
    private static List<Dictionary<string, int>> _tf = new();
    private static Dictionary<string, int> _df = new();
    private static double _avgLen;

    public static IReadOnlyList<HelpArticle> Articles { get { lock (Gate) return _articles ??= Load(); } }

    /// <summary>A module owns the articles tagged with <paramref name="module"/> (called when it registers); the index is rebuilt on next use.</summary>
    public static void OwnModule(string module) { lock (Gate) { if (Owned.Add(module)) _articles = null; } }
    public static IReadOnlyCollection<string> OwnedModules { get { lock (Gate) return Owned.ToList(); } }
    internal static void ResetOwnersForTest() { lock (Gate) { Owned.Clear(); _articles = null; } }

    /// <summary>Every article in the help file, with no module filter (self-test).</summary>
    internal static List<HelpArticle> AllInFile() => Parse(ReadHelp(), null);

    private static string ReadHelp()
    {
        using var s = typeof(HelpKb).Assembly.GetManifestResourceStream("help/help.md");
        return s == null ? "" : new StreamReader(s, Encoding.UTF8).ReadToEnd();
    }

    /// <summary>Splits the help file into articles: <c>## Title</c>, then optional <c>keywords:</c> and <c>module:</c> lines
    /// (either order), then the body. With <paramref name="owned"/> given, articles of modules not in it are left out.</summary>
    internal static List<HelpArticle> Parse(string md, ISet<string>? owned)
    {
        var list = new List<HelpArticle>();
        foreach (var block in Regex.Split(md, @"^## ", RegexOptions.Multiline).Skip(1))
        {
            var lines = block.Replace("\r", "").Split('\n').ToList();
            var title = lines[0].Trim();
            string kw = "", module = "";
            int i = 1;
            for (; i < lines.Count && i <= 2; i++)
            {
                if (lines[i].StartsWith("keywords:")) kw = lines[i][9..].Trim();
                else if (lines[i].StartsWith("module:")) module = lines[i][7..].Trim();
                else break;
            }
            var body = string.Join("\n", lines.Skip(i)).Trim();
            if (module.Length > 0 && owned != null && !owned.Contains(module)) continue;
            list.Add(new HelpArticle(title, kw, body, module.Length > 0 ? module : null));
        }
        return list;
    }

    private static List<HelpArticle> Load()
    {
        var list = Parse(ReadHelp(), Owned);
        // Index: title ×3, keywords ×3, body ×1.
        _tf = list.Select(a =>
        {
            var d = new Dictionary<string, int>();
            void Add(string text, int w) { foreach (var t in Tokens(text)) d[t] = d.GetValueOrDefault(t) + w; }
            Add(a.Title, 3); Add(a.Keywords, 3); Add(a.Body, 1);
            return d;
        }).ToList();
        _df = new();
        foreach (var d in _tf) foreach (var k in d.Keys) _df[k] = _df.GetValueOrDefault(k) + 1;
        _avgLen = _tf.Count == 0 ? 1 : _tf.Average(d => d.Values.Sum());
        return list;
    }

    private static readonly HashSet<string> Stop = new("the a an and or to of in on for is it how do i can my me with what where why does this that be are from use using".Split(' '));

    public static IEnumerable<string> Tokens(string text)
    {
        foreach (Match m in Regex.Matches(text.ToLowerInvariant(), @"[a-z0-9][a-z0-9+#.\-]*[a-z0-9+#]|[a-z0-9]"))
        {
            var w = m.Value;
            if (w.Length < 2 || Stop.Contains(w)) continue;
            yield return Stem(w);
        }
    }

    private static string Stem(string w)
    {
        if (w.Length > 5 && w.EndsWith("ing")) return w[..^3];
        if (w.Length > 4 && w.EndsWith("ed")) return w[..^2];
        if (w.Length > 4 && w.EndsWith("es")) return w[..^2];
        if (w.Length > 3 && w.EndsWith('s') && !w.EndsWith("ss")) return w[..^1];
        return w;
    }

    public static List<(HelpArticle Article, double Score)> Search(string query, int take = 6)
    {
        List<HelpArticle> arts; List<Dictionary<string, int>> tfs; Dictionary<string, int> dfs; double avg;
        lock (Gate) { arts = (_articles ??= Load()); tfs = _tf; dfs = _df; avg = _avgLen; }
        var q = Tokens(query).Distinct().ToList();
        if (q.Count == 0) return arts.Take(take).Select(a => (a, 0.0)).ToList();
        const double k1 = 1.4, b = 0.75;
        int n = arts.Count;
        var scored = new List<(HelpArticle, double)>();
        for (int i = 0; i < n; i++)
        {
            var d = tfs[i]; double len = d.Values.Sum(), score = 0;
            foreach (var t in q)
            {
                // prefix match lets "reconn" find "reconnect"
                int tf = d.TryGetValue(t, out var exact) ? exact : d.Where(kv => kv.Key.StartsWith(t) && t.Length >= 4).Sum(kv => kv.Value) / 2;
                if (tf == 0) continue;
                int df = dfs.TryGetValue(t, out var x) ? x : 1;
                double idf = Math.Log(1 + (n - df + 0.5) / (df + 0.5));
                score += idf * tf * (k1 + 1) / (tf + k1 * (1 - b + b * len / avg));
            }
            if (score > 0) scored.Add((arts[i], score));
        }
        return scored.OrderByDescending(s => s.Item2).Take(take).ToList();
    }
}
