using System.Text.RegularExpressions;

namespace DcxManager.Services.Keys;

/// <summary>
/// The stored form of changed shortcuts (0.6.36, ADR-115): one line per command, <c>command.id=Ctrl+Shift+P;F6</c>, or
/// <c>command.id=</c> for a command with no keys. The lines live in the encrypted settings (<see cref="AppSettings.KeyBindings"/>)
/// and Group Policy can supply them (REG_MULTI_SZ), so they are read with fixed limits: at most <see cref="MaxEntries"/>
/// lines of <see cref="MaxLineLength"/> characters, a command id of the form <c>word.word</c>, and at most
/// <see cref="MaxChordsPerCommand"/> keys each, every one through <see cref="KeyChord.TryParse"/> and
/// <see cref="KeyChord.Problem"/>. A line that fails is skipped and listed, never half-applied.
/// </summary>
public static class KeyBindingList
{
    public const int MaxEntries = 400;
    public const int MaxLineLength = 200;
    public const int MaxChordsPerCommand = 3;

    private static readonly Regex IdRx = new(@"^[a-z][a-z0-9]*(\.[a-z0-9][a-z0-9-]*){1,4}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    /// <summary>Whether this is a well-formed command id (lower case words joined by dots, at most 64 characters).</summary>
    public static bool ValidId(string? id) => id is { Length: > 2 and <= 64 } && IdRx.IsMatch(id);

    /// <summary>The changed shortcuts by command id, and what was skipped (each problem names its line, clipped).</summary>
    public sealed record Parsed(IReadOnlyDictionary<string, KeyChord[]> Overrides, IReadOnlyList<string> Problems);

    public static Parsed Parse(IEnumerable<string?>? lines)
    {
        var map = new Dictionary<string, KeyChord[]>(StringComparer.Ordinal);
        var problems = new List<string>();
        if (lines == null) return new Parsed(map, problems);
        int n = 0;
        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (++n > MaxEntries) { problems.Add($"more than {MaxEntries} lines: the rest were skipped"); break; }
            var line = raw.Trim();
            if (line.Length > MaxLineLength) { problems.Add($"line {n} is longer than {MaxLineLength} characters"); continue; }
            if (line.Any(char.IsControl)) { problems.Add($"line {n} has control characters"); continue; }
            int eq = line.IndexOf('=');
            if (eq <= 0) { problems.Add($"line {n} “{Clip(line)}” is not command=keys"); continue; }
            var id = line[..eq].Trim();
            if (!ValidId(id)) { problems.Add($"line {n}: “{Clip(id)}” is not a command id"); continue; }
            if (map.ContainsKey(id)) { problems.Add($"line {n}: {id} is listed twice (the first is used)"); continue; }
            var keysText = line[(eq + 1)..].Trim();
            var chords = new List<KeyChord>();
            bool bad = false;
            foreach (var part in keysText.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!KeyChord.TryParse(part, out var c, out var err)) { problems.Add($"line {n} ({id}): “{Clip(part)}”: {err}"); bad = true; continue; }
                if (c.Problem() is { } why) { problems.Add($"line {n} ({id}): {c.Shown}: {why}"); bad = true; continue; }
                if (!chords.Contains(c)) chords.Add(c);
            }
            if (chords.Count > MaxChordsPerCommand) { problems.Add($"line {n} ({id}): more than {MaxChordsPerCommand} keys, the first {MaxChordsPerCommand} are used"); chords = chords.Take(MaxChordsPerCommand).ToList(); }
            // Text that was all wrong is skipped: it never turns a command's keys off by accident.
            if (bad && chords.Count == 0) continue;
            map[id] = chords.ToArray();
        }
        return new Parsed(map, problems);
    }

    /// <summary>One stored line.</summary>
    public static string Line(string id, IEnumerable<KeyChord> chords) => id + "=" + string.Join(";", chords.Select(c => c.Name));

    /// <summary>The stored lines for these changes, sorted by command id.</summary>
    public static List<string> Lines(IReadOnlyDictionary<string, KeyChord[]> overrides) =>
        overrides.OrderBy(o => o.Key, StringComparer.Ordinal).Select(o => Line(o.Key, o.Value)).ToList();

    private static string Clip(string s) => s.Length > 32 ? s[..32] + "…" : s;
}
