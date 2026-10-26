namespace DcxManager.Services;

/// <summary>
/// The command palette's search (0.6.29, ADR-104): every word typed must be found in an item's title or detail, as a
/// substring or (title only) as letters in order. Word starts and the title's start score highest; what you opened
/// recently is lifted (Settings → Navigation → "learn", <see cref="AppSettings.PaletteLearn"/>). Pure logic, sized for
/// 10,000 hosts: lower-cased text is kept on the item, and only the best <c>max</c> are sorted.
/// </summary>
public static class PaletteSearch
{
    public sealed class Item
    {
        public Item(string key, string kind, string title, string detail, Action run, string glyph = "")
        {
            Key = key; Kind = kind; Title = title; Detail = detail; Run = run; Glyph = glyph;
            _title = title.ToLowerInvariant(); _detail = detail.ToLowerInvariant();
        }
        /// <summary>Stable id for the recent list ("host:&lt;id&gt;", "page:hosts", "setting:Alerts").</summary>
        public string Key { get; }
        /// <summary>Host, Tab, Workspace, Snippet, Page, Setting or Action (shown, and searchable as a word).</summary>
        public string Kind { get; }
        public string Title { get; }
        public string Detail { get; }
        public string Glyph { get; }
        public Action Run { get; }
        /// <summary>More things to do with it (a host: files, manage, edit), shown with Shift+Enter or right-click.</summary>
        public Func<IEnumerable<(string Title, Action Run)>>? More { get; init; }
        internal readonly string _title, _detail;
    }

    public const int MaxRecent = 50;

    public static List<Item> Search(IReadOnlyList<Item> items, string query, IReadOnlyList<string>? recent = null, int max = 60)
    {
        var words = (query ?? "").ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
        if (recent != null) for (int i = 0; i < recent.Count && i < MaxRecent; i++) rank.TryAdd(recent[i], MaxRecent - i);
        if (words.Length == 0)
        {
            // Nothing typed: what you opened recently, then pages and actions.
            return items.Where(x => rank.ContainsKey(x.Key)).OrderByDescending(x => rank[x.Key])
                .Concat(items.Where(x => !rank.ContainsKey(x.Key) && x.Kind is "Page" or "Action")).Take(max).ToList();
        }
        var scored = new List<(Item Item, int Score)>();
        foreach (var it in items)
        {
            int score = 0;
            foreach (var w in words)
            {
                int s = Score(it, w);
                if (s == 0) { score = 0; break; }
                score += s;
            }
            if (score == 0) continue;
            if (rank.TryGetValue(it.Key, out var r)) score += 10 + r;
            scored.Add((it, score));
        }
        return scored.OrderByDescending(x => x.Score).ThenBy(x => x.Item.Title.Length).ThenBy(x => x.Item.Title, StringComparer.OrdinalIgnoreCase)
            .Take(max).Select(x => x.Item).ToList();
    }

    private static int Score(Item it, string w)
    {
        var t = it._title;
        int i = t.IndexOf(w, StringComparison.Ordinal);
        if (i == 0) return 40;
        if (i > 0) return IsWordStart(t, i) ? 30 : 18;
        if (it.Kind.Equals(w, StringComparison.OrdinalIgnoreCase)) return 6;
        int d = it._detail.IndexOf(w, StringComparison.Ordinal);
        if (d >= 0) return IsWordStart(it._detail, d) ? 10 : 6;
        return w.Length >= 2 && InOrder(t, w) ? 4 : 0;
    }

    private static bool IsWordStart(string s, int i) => i == 0 || !char.IsLetterOrDigit(s[i - 1]);

    private static bool InOrder(string s, string w)
    {
        int j = 0;
        for (int i = 0; i < s.Length && j < w.Length; i++) if (s[i] == w[j]) j++;
        return j == w.Length;
    }

    /// <summary>Puts a key at the front of the recent list (no duplicates, at most <see cref="MaxRecent"/>).</summary>
    public static void Remember(List<string> recent, string key)
    {
        recent.Remove(key);
        recent.Insert(0, key);
        if (recent.Count > MaxRecent) recent.RemoveRange(MaxRecent, recent.Count - MaxRecent);
    }
}
