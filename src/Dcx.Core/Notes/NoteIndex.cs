using System.Globalization;
using System.Text.RegularExpressions;

namespace DcxManager.Notes;

/// <summary>
/// Deterministic, offline full-text index for notes (ADR-005). Lives only in memory. Tokens: lower-case words, plus
/// whole "dotted/dashed" terms (prod-web-04, nginx.conf, 192.0.2.10) and their parts. Matching is by prefix (≥ 2
/// characters), all terms must match (AND). Query syntax:
/// <c>word "exact phrase" tag:x host:x is:fav before:2026-10-01 after:… on:… today yesterday "last tuesday" this week last week</c>.
/// Filler words ("notes", "about", "everything I wrote") are ignored so natural questions work without AI.
/// </summary>
public sealed class NoteIndex
{
    public sealed record Hit(Note Note, double Score, string Snippet);

    private readonly Dictionary<string, HashSet<string>> _postings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _byNote = new();
    private readonly Dictionary<string, Dictionary<string, int>> _weights = new();   // note → token → best field weight
    private string[]? _sorted;

    public int Count => _byNote.Count;

    /// <summary>Words of the last search that appear in no note at all and were therefore ignored (shown to the user),
    /// so "all incidents involving docker-01" still finds the docker-01 notes. Never ignores the only criterion.</summary>
    public List<string> LastIgnored { get; } = new();

    public void Clear() { _postings.Clear(); _byNote.Clear(); _weights.Clear(); _sorted = null; }

    public static IEnumerable<string> Tokens(string? text)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        foreach (Match m in Regex.Matches(text.ToLowerInvariant(), @"[\p{L}\p{N}]+(?:[._:/@-][\p{L}\p{N}]+)*"))
        {
            var t = m.Value;
            if (t.Length <= 64) yield return t;
            if (t.IndexOfAny(new[] { '.', '-', '_', ':', '/', '@' }) >= 0)
                foreach (var part in Regex.Split(t, @"[._:/@-]+")) if (part.Length > 0) yield return part;
        }
    }

    public void Add(Note n, IEnumerable<string> hostNames)
    {
        var w = new Dictionary<string, int>();
        void Field(string? text, int weight) { foreach (var t in Tokens(text)) if (!w.TryGetValue(t, out var cur) || cur < weight) w[t] = weight; }
        Field(n.Body, 1);
        Field(string.Join(" ", hostNames), 2);
        Field(n.Title, 3);
        Field(string.Join(" ", n.Tags), 3);
        foreach (var t in w.Keys)
        {
            if (!_postings.TryGetValue(t, out var set)) _postings[t] = set = new HashSet<string>();
            set.Add(n.Id);
        }
        _byNote[n.Id] = w.Keys.ToHashSet();
        _weights[n.Id] = w;
        _sorted = null;
    }

    public void Remove(string id)
    {
        if (!_byNote.Remove(id, out var tokens)) return;
        _weights.Remove(id);
        foreach (var t in tokens) if (_postings.TryGetValue(t, out var s)) { s.Remove(id); if (s.Count == 0) _postings.Remove(t); }
        _sorted = null;
    }

    private static readonly HashSet<string> Filler = new(StringComparer.OrdinalIgnoreCase)
    {
        "notes", "note", "about", "everything", "anything", "i", "wrote", "written", "write", "the", "a", "an", "all", "any", "from",
        "of", "on", "in", "for", "to", "and", "with", "involving", "regarding", "re", "my", "me", "show", "find", "what", "did", "issue", "issues",
    };

    /// <summary>A parsed query (exposed for tests and the UI's "searching for …" line).</summary>
    public sealed record Query(List<string> Terms, List<string> Phrases, List<string> Tags, List<string> Hosts, bool FavOnly, DateTime? From, DateTime? To);

    public static Query Parse(string text, DateTime? now = null)
    {
        var today = (now ?? DateTime.Now).Date;
        var q = new Query(new(), new(), new(), new(), false, null, null);
        DateTime? from = null, to = null; bool fav = false;
        var s = text ?? "";
        foreach (Match m in Regex.Matches(s, "\"([^\"]+)\"")) q.Phrases.Add(m.Groups[1].Value.ToLowerInvariant());
        s = Regex.Replace(s, "\"[^\"]*\"", " ");
        // Natural date words first ("last tuesday", "this week", …).
        var dow = Regex.Match(s, @"\blast\s+(monday|tuesday|wednesday|thursday|friday|saturday|sunday)\b", RegexOptions.IgnoreCase);
        if (dow.Success)
        {
            var target = Enum.Parse<DayOfWeek>(dow.Groups[1].Value, true);
            int back = ((int)today.DayOfWeek - (int)target + 7) % 7; if (back == 0) back = 7;
            from = today.AddDays(-back); to = from.Value.AddDays(1);
            s = s.Remove(dow.Index, dow.Length);
        }
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        if (Regex.IsMatch(s, @"\bthis\s+week\b", RegexOptions.IgnoreCase)) { from = monday; to = today.AddDays(1); s = Regex.Replace(s, @"\bthis\s+week\b", " ", RegexOptions.IgnoreCase); }
        if (Regex.IsMatch(s, @"\blast\s+week\b", RegexOptions.IgnoreCase)) { from = monday.AddDays(-7); to = monday; s = Regex.Replace(s, @"\blast\s+week\b", " ", RegexOptions.IgnoreCase); }
        foreach (var raw in s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var w = raw.Trim(',', ';', '?', '!');
            var lw = w.ToLowerInvariant();
            if (lw == "today") { from = today; to = today.AddDays(1); continue; }
            if (lw == "yesterday") { from = today.AddDays(-1); to = today; continue; }
            if (lw is "is:fav" or "is:favourite" or "is:favorite") { fav = true; continue; }
            var kv = Regex.Match(w, @"^(tag|host|before|after|on):(.+)$", RegexOptions.IgnoreCase);
            if (kv.Success)
            {
                var v = kv.Groups[2].Value;
                switch (kv.Groups[1].Value.ToLowerInvariant())
                {
                    case "tag": q.Tags.Add(v.ToLowerInvariant()); break;
                    case "host": q.Hosts.Add(v); break;
                    default:
                        if (DateTime.TryParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                        {
                            var key = kv.Groups[1].Value.ToLowerInvariant();
                            if (key == "before") to = d;
                            else if (key == "after") from = d.AddDays(1);
                            else { from = d; to = d.AddDays(1); }
                        }
                        break;
                }
                continue;
            }
            if (Filler.Contains(lw) || lw.Length < 2) continue;
            q.Terms.AddRange(Tokens(w).Where(t => t.Length >= 2).Take(1));   // the whole term (dashed/dotted kept intact)
        }
        return q with { FavOnly = fav, From = from, To = to };
    }

    private IEnumerable<string> PrefixKeys(string prefix)
    {
        _sorted ??= _postings.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
        int lo = 0, hi = _sorted.Length;
        while (lo < hi) { int mid = (lo + hi) / 2; if (string.CompareOrdinal(_sorted[mid], prefix) < 0) lo = mid + 1; else hi = mid; }
        for (int i = lo; i < _sorted.Length && _sorted[i].StartsWith(prefix, StringComparison.Ordinal); i++) yield return _sorted[i];
    }

    public List<Hit> Search(string text, IReadOnlyDictionary<string, Note> notes, Func<string, IEnumerable<string>> hostIdsFor, int max = 200)
    {
        var q = Parse(text);
        IEnumerable<string> ids = notes.Keys;
        var scores = notes.Keys.ToDictionary(k => k, _ => 0.0);
        LastIgnored.Clear();
        bool otherCriteria(string t) => q.Terms.Any(x => x != t && PrefixKeys(x).Any()) || q.Phrases.Count + q.Tags.Count + q.Hosts.Count > 0 || q.FavOnly || q.From != null || q.To != null;
        foreach (var term in q.Terms)
        {
            var keys = PrefixKeys(term).ToList();
            if (keys.Count == 0 && otherCriteria(term)) { LastIgnored.Add(term); continue; }
            var match = new HashSet<string>();
            foreach (var k in keys)
                foreach (var id in _postings[k])
                {
                    match.Add(id);
                    if (scores.ContainsKey(id) && _weights.TryGetValue(id, out var w) && w.TryGetValue(k, out var weight))
                        scores[id] += weight * (k == term ? 1.0 : 0.6);   // exact token beats a prefix
                }
            ids = ids.Where(match.Contains).ToList();
        }
        foreach (var tag in q.Tags) ids = ids.Where(id => notes[id].Tags.Any(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase)));
        foreach (var h in q.Hosts) { var hs = hostIdsFor(h).ToHashSet(); ids = ids.Where(id => notes[id].HostIds.Any(hs.Contains)); }
        if (q.FavOnly) ids = ids.Where(id => notes[id].Favorite);
        // A date range matches notes created, edited or revised within it ("notes from last Tuesday").
        if (q.From != null || q.To != null)
        {
            DateTime from = q.From ?? DateTime.MinValue, to = q.To ?? DateTime.MaxValue;
            bool In(DateTime d) => d >= from && d < to;
            ids = ids.Where(id => In(notes[id].Created) || In(notes[id].Updated) || notes[id].Revisions.Any(r => In(r.At)));
        }
        foreach (var p in q.Phrases)
            ids = ids.Where(id => (notes[id].Title + "\n" + notes[id].Body).Contains(p, StringComparison.OrdinalIgnoreCase)).Select(id => { scores[id] += 5; return id; }).ToList();
        return ids.Select(id => new Hit(notes[id], scores[id] + (notes[id].Favorite ? 0.5 : 0), Snippet(notes[id], q)))
            .OrderByDescending(h => h.Score).ThenByDescending(h => h.Note.Updated).Take(max).ToList();
    }

    /// <summary>The first body line containing a search term or phrase (else the first non-empty line), shortened.</summary>
    public static string Snippet(Note n, Query q)
    {
        var lines = n.Body.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var needles = q.Phrases.Concat(q.Terms).ToList();
        var line = lines.FirstOrDefault(l => needles.Any(t => l.Contains(t, StringComparison.OrdinalIgnoreCase))) ?? lines.FirstOrDefault() ?? "";
        return line.Length > 140 ? line[..140] + "…" : line;
    }
}
