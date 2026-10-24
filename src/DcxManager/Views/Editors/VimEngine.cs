using System.Text.RegularExpressions;

namespace DcxManager.Views.Editors;

/// <summary>What the Vim layer edits: the text, the caret, the selection, undo. The editor's TextBox implements it; the
/// self-test uses a plain string buffer.</summary>
public interface IVimBuffer
{
    string Text { get; }
    int Caret { get; set; }
    void Select(int start, int length);
    /// <summary>Replaces a range (one undo step).</summary>
    void Replace(int start, int length, string text);
    void Undo();
    void Redo();
}

/// <summary>
/// A Vim key layer for the text editor (0.3.0 §13, DarkCipher0x: "integrate editor tools like vim"): normal, insert, visual,
/// visual-line and command-line modes; counts; motions h j k l w b e 0 ^ $ gg G f/t; operators d c y with motions, dd cc
/// yy, x X D C J ~ r p P, i a I A o O, u and Ctrl+R, . to repeat, / ? n N search, and ex commands :w :q :q! :wq :x
/// :&lt;line&gt; :s and :%s/old/new/[g]. Pure logic over <see cref="IVimBuffer"/>; the window maps keys to <see cref="Key"/>.
/// Keys are "a", "A", "Esc", "Enter", "Backspace", "C-r" (Ctrl+R), "C-d", "C-u".
/// </summary>
public sealed class VimEngine(IVimBuffer b)
{
    /// <summary>The text the engine works on (the window types insert-mode keys into it).</summary>
    public IVimBuffer Buffer => b;

    public enum Mode { Normal, Insert, Visual, VisualLine, Command }

    public Mode Current { get; private set; } = Mode.Normal;
    /// <summary>The ":…" or "/…" line being typed.</summary>
    public string CommandLine { get; private set; } = "";
    public string Message { get; private set; } = "";
    /// <summary>The unnamed register; <see cref="RegisterLines"/> = it holds whole lines.</summary>
    public string Register { get; private set; } = "";
    public bool RegisterLines { get; private set; }
    /// <summary>":w", ":q", ":q!", ":wq" — the window acts on them.</summary>
    public event Action<string>? Ex;

    private string _pending = "";       // keys of an unfinished command ("2d", "g", "f", "r"…)
    private int _visualAnchor;
    private string _search = "";
    private bool _searchBack;
    private List<string> _lastChange = new();
    private List<string> _recording = new();
    private bool _replaying;

    public string ModeText => Current switch
    {
        Mode.Insert => "-- INSERT --", Mode.Visual => "-- VISUAL --", Mode.VisualLine => "-- VISUAL LINE --",
        Mode.Command => CommandLine, _ => _pending.Length > 0 ? _pending : "",
    };

    // ---- text helpers -------------------------------------------------------------------------------------------------

    private string T => b.Text;
    private int LineStart(int p) { var i = T.LastIndexOf('\n', Math.Max(0, Math.Min(p, T.Length) - 1)); return p == 0 ? 0 : i + 1; }
    private int LineEnd(int p) { var i = T.IndexOf('\n', Math.Min(p, T.Length)); return i < 0 ? T.Length : i; }
    private int LineOf(int p) => T.AsSpan(0, Math.Min(p, T.Length)).Count('\n');
    private int StartOfLine(int line) { int p = 0; for (int i = 0; i < line && p < T.Length; i++) { var n = T.IndexOf('\n', p); if (n < 0) return LineStart(T.Length); p = n + 1; } return p; }
    private int Lines => T.Count(c => c == '\n') + 1;
    private int Col(int p) => p - LineStart(p);
    private int LastCol(int p) => Math.Max(LineStart(p), LineEnd(p) - (Current == Mode.Normal && LineEnd(p) > LineStart(p) ? 1 : 0));
    private static bool Word(char c) => char.IsLetterOrDigit(c) || c == '_';
    private int Clamp(int p) => Math.Clamp(p, 0, T.Length);

    private void Put(int p) { b.Caret = Clamp(p); if (Current == Mode.Normal) b.Caret = Math.Min(b.Caret, LastCol(b.Caret)); }

    // ---- keys ---------------------------------------------------------------------------------------------------------

    /// <summary>The active profile (options and mappings, <see cref="VimConfig"/>).</summary>
    public VimConfig Config { get; set; } = VimConfig.Default;
    private readonly List<string> _mapBuf = new();
    private bool _inMap;
    private string _insBuf = "";

    /// <summary>Handles one key, through the profile's normal / visual mappings. False = the editor should do its normal
    /// thing (insert-mode typing).</summary>
    public bool Key(string k)
    {
        var map = Current == Mode.Normal ? Config.Normal : Current is Mode.Visual or Mode.VisualLine ? Config.Visual : null;
        if (map == null || map.Count == 0 || _inMap || _pending.Length > 0) return KeyCore(k);
        _mapBuf.Add(k);
        var key = string.Join("\u0001", _mapBuf);
        if (map.Keys.Any(x => x.StartsWith(key + "\u0001", StringComparison.Ordinal))) return true;   // wait for the next key
        if (map.TryGetValue(key, out var rhs)) { _mapBuf.Clear(); Run(rhs); return true; }
        // No mapping continues: an exact match of what came before runs first, then this key on its own.
        var before = string.Join("\u0001", _mapBuf.Take(_mapBuf.Count - 1));
        var buf = _mapBuf.ToList(); _mapBuf.Clear();
        if (buf.Count > 1 && map.TryGetValue(before, out var prev)) { Run(prev); return Key(k); }
        _inMap = true;
        try { foreach (var x in buf) KeyCore(x); }
        finally { _inMap = false; }
        return true;
    }

    /// <summary>Runs a mapping's right-hand side (never remapped: every map behaves like noremap).</summary>
    private void Run(List<string> keys)
    {
        _inMap = true;
        try { foreach (var x in keys) { if (Current == Mode.Insert && x.Length == 1) InsertText(x); else KeyCore(x); } }
        finally { _inMap = false; }
    }

    /// <summary>Insert-mode mappings ("inoremap jk &lt;Esc&gt;"): called before the editor types <paramref name="ch"/>. True =
    /// the mapping fired (the characters of it already typed are removed, this one isn't typed).</summary>
    public bool InsertChar(char ch)
    {
        if (Current != Mode.Insert || Config.Insert.Count == 0) return false;
        static string Join(string s) => string.Join("\u0001", s.Select(c => c.ToString()));
        var cand = _insBuf + ch;
        if (Config.Insert.TryGetValue(Join(cand), out var rhs) && !Config.Insert.Keys.Any(x => x.StartsWith(Join(cand) + "\u0001", StringComparison.Ordinal)))
        {
            var typed = _insBuf.Length; _insBuf = "";
            if (typed > 0 && b.Caret >= typed) b.Replace(b.Caret - typed, typed, "");
            Run(rhs);
            return true;
        }
        _insBuf = Config.Insert.Keys.Any(x => x.StartsWith(Join(cand), StringComparison.Ordinal)) ? cand
                : Config.Insert.Keys.Any(x => x.StartsWith(ch.ToString(), StringComparison.Ordinal)) ? ch.ToString() : "";
        return false;
    }

    private bool KeyCore(string k)
    {
        Message = "";
        switch (Current)
        {
            case Mode.Insert:
                if (k == "Esc") { Current = Mode.Normal; Put(b.Caret - (Col(b.Caret) > 0 ? 1 : 0)); EndChange(); return true; }
                if (!_replaying) _recording.Add(k);
                return false;
            case Mode.Command: return CommandKey(k);
            default: return NormalKey(k);
        }
    }

    private void EndChange()
    {
        if (_replaying) return;
        if (_recording.Count > 0 && _recording.Any(x => x.Length == 1 || x is "Enter" or "Backspace")) _lastChange = _recording.ToList();
        _recording.Clear();
    }

    private bool CommandKey(string k)
    {
        if (k == "Esc") { Current = Mode.Normal; CommandLine = ""; return true; }
        if (k == "Backspace") { if (CommandLine.Length <= 1) { Current = Mode.Normal; CommandLine = ""; } else CommandLine = CommandLine[..^1]; return true; }
        if (k == "Enter")
        {
            var line = CommandLine; CommandLine = ""; Current = Mode.Normal;
            if (line.StartsWith('/') || line.StartsWith('?')) { if (line.Length > 1) _search = line[1..]; _searchBack = line[0] == '?'; Find(_searchBack, 1); }
            else RunEx(line[1..].Trim());
            return true;
        }
        if (k.Length == 1) CommandLine += k;
        return true;
    }

    private void RunEx(string cmd)
    {
        if (cmd.Length == 0) return;
        if (int.TryParse(cmd, out var line)) { Put(StartOfLine(Math.Clamp(line - 1, 0, Lines - 1))); return; }
        var m = Regex.Match(cmd, @"^(%)?s/((?:\\/|[^/])*)/((?:\\/|[^/])*)/?([gi]*)$");
        if (m.Success)
        {
            int from = m.Groups[1].Success ? 0 : LineStart(b.Caret), to = m.Groups[1].Success ? T.Length : LineEnd(b.Caret);
            var opts = (m.Groups[4].Value.Contains('i') ? RegexOptions.IgnoreCase : RegexOptions.None) | RegexOptions.Multiline;
            Regex rx;
            try { rx = new Regex(m.Groups[2].Value.Replace("\\/", "/"), opts, TimeSpan.FromSeconds(1)); } catch (ArgumentException ex) { Message = "E: " + ex.Message; return; }
            var region = T[from..to];
            int count = 0;
            var replaced = m.Groups[1].Success || m.Groups[4].Value.Contains('g')
                ? string.Join("\n", region.Split('\n').Select(l => rx.Replace(l, _ => { count++; return m.Groups[3].Value.Replace("\\/", "/"); }, m.Groups[4].Value.Contains('g') ? -1 : 1)))
                : rx.Replace(region, _ => { count++; return m.Groups[3].Value.Replace("\\/", "/"); }, 1);
            if (count > 0) { b.Replace(from, to - from, replaced); Put(from); }
            Message = count > 0 ? $"{count} substitution(s)" : "E486: Pattern not found";
            return;
        }
        if (cmd is "w" or "q" or "q!" or "wq" or "x" or "wq!" or "e!") { Ex?.Invoke(":" + cmd); return; }
        Message = "E492: Not an editor command: " + cmd;
    }

    private bool NormalKey(string k)
    {
        // ":" and "/" open the command line (in visual mode ":" too).
        if (_pending.Length == 0 && k is ":" or "/" or "?") { Current = Mode.Command; CommandLine = k; return true; }
        if (k == "Esc") { _pending = ""; if (Current is Mode.Visual or Mode.VisualLine) { Current = Mode.Normal; b.Select(b.Caret, 0); } return true; }
        // "." records each normal-mode command from its first key (count included); a change that stays in normal mode is
        // kept here, one that enters insert mode when Esc ends it (EndChange).
        bool wasNormal = Current == Mode.Normal;
        if (!_replaying) { if (_pending.Length == 0) _recording.Clear(); _recording.Add(k); }
        if (_pending.Length == 0 && k == "0") { Motion("0", 1); return true; }   // "0" alone is a motion, not a count
        _pending += k.Length == 1 ? k : $"<{k}>";
        if (_pending.Length > 24) { _pending = ""; return true; }
        var m = Regex.Match(_pending, @"^(\d*)(.*)$");
        var countText = m.Groups[1].Value;
        var rest = m.Groups[2].Value;
        if (rest.Length == 0 || (countText == "" && rest == "0" && false)) return true;
        if (countText.Length > 0 && rest.Length == 0) return true;
        int count = countText.Length > 0 ? int.Parse(countText) : 1;
        if (countText == "" && rest.StartsWith('0') && rest.Length == 1) { Done(); Motion("0", 1); return true; }
        var r = Run(rest, count, countText.Length > 0);
        if (r != Result.Pending)
        {
            _pending = "";
            if (!_replaying && wasNormal && Current == Mode.Normal && "xXDJ~rpPdc<>".Contains(rest[0]) && !Regex.IsMatch(rest, @"^<[A-Z][A-Za-z-]*>$")) { _lastChange = _recording.ToList(); _recording.Clear(); }
        }
        return true;
    }

    private enum Result { Done, Pending }
    private void Done() => _pending = "";

    private Result Run(string c, int n, bool hasCount)
    {
        bool visual = Current is Mode.Visual or Mode.VisualLine;
        switch (c)
        {
            case "i": Insert(b.Caret); return Result.Done;
            case "a": Insert(Math.Min(b.Caret + (LineEnd(b.Caret) > b.Caret ? 1 : 0), T.Length)); return Result.Done;
            case "I": Insert(FirstNonBlank(b.Caret)); return Result.Done;
            case "A": Insert(LineEnd(b.Caret)); return Result.Done;
            case "o": { var e = LineEnd(b.Caret); b.Replace(e, 0, "\n" + Indent(b.Caret)); Insert(e + 1 + Indent(b.Caret).Length, change: true); return Result.Done; }
            case "O": { var s = LineStart(b.Caret); var ind = Indent(b.Caret); b.Replace(s, 0, ind + "\n"); Insert(s + ind.Length, change: true); return Result.Done; }
            case "v": if (Current == Mode.Visual) { Current = Mode.Normal; b.Select(b.Caret, 0); } else { Current = Mode.Visual; _visualAnchor = b.Caret; ShowVisual(); } return Result.Done;
            case "V": if (Current == Mode.VisualLine) { Current = Mode.Normal; b.Select(b.Caret, 0); } else { Current = Mode.VisualLine; _visualAnchor = b.Caret; ShowVisual(); } return Result.Done;
            case "u": for (int i = 0; i < n; i++) b.Undo(); Put(b.Caret); return Result.Done;
            case "<C-r>": for (int i = 0; i < n; i++) b.Redo(); Put(b.Caret); return Result.Done;
            case "n": Find(_searchBack, n); return Result.Done;
            case "N": Find(!_searchBack, n); return Result.Done;
            case ".": Done(); Repeat(n); return Result.Done;   // the replayed keys start with an empty pending buffer
            case "p": case "P": Paste(c == "P", n); return Result.Done;
            case "J": for (int i = 0; i < Math.Max(1, n - 1 + (hasCount ? 0 : 1)); i++) Join(); return Result.Done;
            case "~": { var s = b.Caret; var e = Math.Min(LineEnd(s), s + n); if (e > s) { var t = new string(T[s..e].Select(ch => char.IsUpper(ch) ? char.ToLowerInvariant(ch) : char.ToUpperInvariant(ch)).ToArray()); b.Replace(s, e - s, t); Put(e); } return Result.Done; }
            case "x": if (visual) { VisualOp('d'); return Result.Done; } Delete(b.Caret, Math.Min(LineEnd(b.Caret), b.Caret + n), false); return Result.Done;
            case "X": { var s = Math.Max(LineStart(b.Caret), b.Caret - n); Delete(s, b.Caret, false); return Result.Done; }
            case "D": Delete(b.Caret, LineEnd(b.Caret), false); return Result.Done;
            case "C": Delete(b.Caret, LineEnd(b.Caret), false); Insert(b.Caret, change: true); return Result.Done;
            case "Y": YankLines(b.Caret, n); return Result.Done;
            case "<C-d>": Motion("j", Math.Max(1, 15 * n)); return Result.Done;
            case "<C-u>": Motion("k", Math.Max(1, 15 * n)); return Result.Done;
        }
        if (visual && c is "d" or "y" or "c" or ">" or "<") { VisualOp(c[0]); return Result.Done; }
        if (c == "r") return Result.Pending;
        if (c.Length == 2 && c[0] == 'r') { var s = b.Caret; var e = Math.Min(LineEnd(s), s + n); if (e - s == n) { b.Replace(s, n, new string(c[1], n)); Put(e - 1); } return Result.Done; }
        if (c is "g" or "f" or "F" or "t" or "T") return Result.Pending;
        // Operators: d c y > < with a motion (dd cc yy >> << for lines).
        if (c.Length >= 1 && "dcy<>".Contains(c[0]))
        {
            var op = c[0];
            var mo = c[1..];
            if (mo.Length == 0 || mo is "g" or "f" or "F" or "t" or "T" || Regex.IsMatch(mo, @"^\d+$")) return Result.Pending;
            var m2 = Regex.Match(mo, @"^(\d*)(.+)$");
            int n2 = (m2.Groups[1].Value.Length > 0 ? int.Parse(m2.Groups[1].Value) : 1) * n;
            mo = m2.Groups[2].Value;
            if (mo == op.ToString()) { LineOp(op, n2); return Result.Done; }
            var from = b.Caret;
            var to = Target(mo, n2, out var inclusive, out var linewise);
            if (to < 0) return Result.Done;
            if (linewise) { var l1 = Math.Min(LineOf(from), LineOf(to)); var l2 = Math.Max(LineOf(from), LineOf(to)); Put(StartOfLine(l1)); LineOp(op, l2 - l1 + 1); return Result.Done; }
            int s = Math.Min(from, to), e = Math.Max(from, to) + (inclusive ? 1 : 0);
            if (op == 'c' && mo is "w" or "W") { e = WordEnd(from, n2) + 1; }   // cw acts like ce
            e = Math.Min(e, T.Length);
            if (op == 'y') { Yank(T[s..e], false); Put(s); }
            else if (op is '>' or '<') LineOp(op, LineOf(e) - LineOf(s) + 1);
            else { Delete(s, e, false); if (op == 'c') Insert(s, change: true); }
            return Result.Done;
        }
        var t2 = Target(c, n, out _, out _);
        if (t2 >= 0) { Put(t2); if (visual) ShowVisual(); return Result.Done; }
        return Result.Done;   // unknown: ignore
    }

    /// <summary>Where a motion goes from the caret (−1 = not a motion).</summary>
    private int Target(string mo, int n, out bool inclusive, out bool linewise)
    {
        inclusive = false; linewise = false;
        int p = b.Caret;
        switch (mo)
        {
            case "h": case "<Left>": return Math.Max(LineStart(p), p - n);
            case "l": case "<Right>": case " ": return Math.Min(LastColAny(p), p + n);
            case "j": case "<Down>": linewise = true; return Vertical(p, n);
            case "k": case "<Up>": linewise = true; return Vertical(p, -n);
            case "0": case "<Home>": return LineStart(p);
            case "^": return FirstNonBlank(p);
            case "$": case "<End>": inclusive = true; { var q = p; for (int i = 1; i < n; i++) q = Math.Min(T.Length, LineEnd(q) + 1); return Math.Max(LineStart(q), LineEnd(q) - 1); }
            case "w": case "W": for (int i = 0; i < n; i++) p = NextWord(p, mo == "W"); return p;
            case "b": case "B": for (int i = 0; i < n; i++) p = PrevWord(p, mo == "B"); return p;
            case "e": case "E": inclusive = true; return WordEnd(p, n);
            case "gg": linewise = true; return StartOfLine(n > 1 ? n - 1 : 0) is var gg ? FirstNonBlank(gg) : 0;
            case "G": linewise = true; return FirstNonBlank(StartOfLine(_pending.Length > 0 && char.IsDigit(_pending[0]) ? n - 1 : Lines - 1));
            case "<Enter>": case "+": linewise = true; return FirstNonBlank(Vertical(p, n));
            case "-": linewise = true; return FirstNonBlank(Vertical(p, -n));
        }
        if (mo.Length == 2 && mo[0] is 'f' or 't' or 'F' or 'T')
        {
            inclusive = mo[0] is 'f' or 't';
            int q = p;
            for (int i = 0; i < n; i++)
            {
                q = mo[0] is 'f' or 't' ? T.IndexOf(mo[1], Math.Min(T.Length, q + 1 + (mo[0] == 't' && i == 0 ? 1 : 0)), Math.Max(0, LineEnd(p) - Math.Min(T.Length, q + 1 + (mo[0] == 't' && i == 0 ? 1 : 0))))
                                       : q > LineStart(p) ? T.LastIndexOf(mo[1], q - 1, q - LineStart(p)) : -1;
                if (q < 0) return -1;
            }
            return mo[0] == 't' ? q - 1 : mo[0] == 'T' ? q + 1 : q;
        }
        return -1;
    }

    private void Motion(string mo, int n) { var t = Target(mo, n, out _, out _); if (t >= 0) { Put(t); if (Current is Mode.Visual or Mode.VisualLine) ShowVisual(); } }

    private int LastColAny(int p) => Math.Max(LineStart(p), LineEnd(p) - 1);
    private int FirstNonBlank(int p) { var s = LineStart(p); var e = LineEnd(p); while (s < e && T[s] is ' ' or '\t') s++; return s; }
    private string Indent(int p) { var s = LineStart(p); return T[s..FirstNonBlank(p)]; }

    private int Vertical(int p, int d)
    {
        var line = Math.Clamp(LineOf(p) + d, 0, Lines - 1);
        var s = StartOfLine(line);
        return Math.Min(s + Col(p), Math.Max(s, LineEnd(s) - 1 < s ? s : LineEnd(s) - 1));
    }

    private int NextWord(int p, bool big)
    {
        if (p >= T.Length) return p;
        bool Cls(char c) => big ? !char.IsWhiteSpace(c) : Word(c);
        if (!char.IsWhiteSpace(T[p])) { var w = Cls(T[p]); while (p < T.Length && !char.IsWhiteSpace(T[p]) && (big || Cls(T[p]) == w)) p++; }
        while (p < T.Length && char.IsWhiteSpace(T[p])) p++;
        return p;
    }

    private int PrevWord(int p, bool big)
    {
        if (p <= 0) return 0;
        p--;
        while (p > 0 && char.IsWhiteSpace(T[p])) p--;
        bool Cls(char c) => big ? !char.IsWhiteSpace(c) : Word(c);
        var w = Cls(T[p]);
        while (p > 0 && !char.IsWhiteSpace(T[p - 1]) && (big || Cls(T[p - 1]) == w)) p--;
        return p;
    }

    private int WordEnd(int p, int n)
    {
        for (int i = 0; i < n; i++)
        {
            if (p < T.Length - 1) p++;
            while (p < T.Length - 1 && char.IsWhiteSpace(T[p])) p++;
            var w = Word(T[p]);
            while (p < T.Length - 1 && !char.IsWhiteSpace(T[p + 1]) && Word(T[p + 1]) == w) p++;
        }
        return Math.Min(p, Math.Max(0, T.Length - 1));
    }

    // ---- edits --------------------------------------------------------------------------------------------------------

    private void Insert(int at, bool change = false) { Current = Mode.Insert; b.Caret = Clamp(at); b.Select(b.Caret, 0); }

    private void Yank(string text, bool lines) { Register = text; RegisterLines = lines; }

    private void Delete(int s, int e, bool lines)
    {
        if (e <= s) return;
        Yank(T[s..e], lines);
        b.Replace(s, e - s, "");
        Put(s);
    }

    private void YankLines(int p, int n)
    {
        var s = LineStart(p); var e = LineEnd(StartOfLine(Math.Min(Lines - 1, LineOf(p) + n - 1)));
        Yank(T[s..e] + "\n", true);
        Message = $"{n} line(s) yanked";
    }

    private void LineOp(char op, int n)
    {
        var l1 = LineOf(b.Caret); var l2 = Math.Min(Lines - 1, l1 + n - 1);
        int s = StartOfLine(l1), e = LineEnd(StartOfLine(l2));
        switch (op)
        {
            case 'y': YankLines(b.Caret, l2 - l1 + 1); break;
            case 'd':
                Yank(T[s..e] + "\n", true);
                if (e < T.Length) b.Replace(s, e - s + 1, ""); else if (s > 0) b.Replace(s - 1, e - s + 1, ""); else b.Replace(0, e, "");
                Put(FirstNonBlank(Math.Min(s, T.Length)));
                break;
            case 'c': { var ind = Indent(s); Yank(T[s..e] + "\n", true); b.Replace(s, e - s, ind); Insert(s + ind.Length, change: true); break; }
            case '>': case '<':
                var unit = Config.IndentUnit;
                var lines = T[s..e].Split('\n').Select(l => op == '>' ? (l.Length > 0 ? unit + l : l) : l.StartsWith(unit) ? l[unit.Length..] : l.StartsWith('\t') ? l[1..] : l[Math.Min(l.Length - l.TrimStart(' ').Length, Config.ShiftWidth)..]).ToList();
                b.Replace(s, e - s, string.Join("\n", lines));
                Put(FirstNonBlank(s));
                break;
        }
    }

    private void Paste(bool before, int n)
    {
        if (Register.Length == 0) return;
        var text = string.Concat(Enumerable.Repeat(Register, n));
        if (RegisterLines)
        {
            int at = before ? LineStart(b.Caret) : LineEnd(b.Caret);
            if (before) b.Replace(at, 0, text);
            else { b.Replace(at, 0, "\n" + text.TrimEnd('\n')); at++; }
            Put(FirstNonBlank(at));
        }
        else
        {
            int at = before ? b.Caret : Math.Min(T.Length, b.Caret + (T.Length > 0 && b.Caret < LineEnd(b.Caret) ? 1 : 0));
            b.Replace(at, 0, text);
            Put(at + text.Length - 1);
        }
    }

    private void Join()
    {
        var e = LineEnd(b.Caret);
        if (e >= T.Length) return;
        int ns = e + 1; while (ns < T.Length && T[ns] is ' ' or '\t') ns++;
        b.Replace(e, ns - e, e > LineStart(b.Caret) && ns < T.Length && T[ns] != '\n' ? " " : "");
        Put(e);
    }

    private void VisualOp(char op)
    {
        int s = Math.Min(_visualAnchor, b.Caret), e = Math.Max(_visualAnchor, b.Caret) + 1;
        if (Current == Mode.VisualLine) { var n = LineOf(e - 1) - LineOf(s) + 1; Current = Mode.Normal; Put(LineStart(s)); LineOp(op == 'x' ? 'd' : op, n); return; }
        Current = Mode.Normal;
        e = Math.Min(e, T.Length);
        if (op == 'y') { Yank(T[s..e], false); Put(s); }
        else if (op is '>' or '<') { Put(s); LineOp(op, LineOf(e - 1) - LineOf(s) + 1); }
        else { Delete(s, e, false); if (op == 'c') Insert(s, change: true); }
        b.Select(b.Caret, 0);
    }

    private void ShowVisual()
    {
        int s = Math.Min(_visualAnchor, b.Caret), e = Math.Max(_visualAnchor, b.Caret) + 1;
        if (Current == Mode.VisualLine) { s = LineStart(s); e = Math.Min(T.Length, LineEnd(e - 1) + 1); }
        var caret = b.Caret;
        b.Select(s, Math.Min(e, T.Length) - s);
        b.Caret = caret;
    }

    private void Find(bool back, int n)
    {
        if (_search.Length == 0) { Message = "E35: No previous regular expression"; return; }
        var cmp = Config.IgnoreCase && !(Config.SmartCase && _search.Any(char.IsUpper)) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        int p = b.Caret;
        for (int i = 0; i < n; i++)
        {
            int q = back ? (p > 0 ? T.LastIndexOf(_search, p - 1, cmp) : -1) : T.IndexOf(_search, Math.Min(T.Length, p + 1), cmp);
            if (q < 0) q = back ? T.LastIndexOf(_search, cmp) : T.IndexOf(_search, cmp);   // wrap around
            if (q < 0) { Message = "E486: Pattern not found: " + _search; return; }
            p = q;
        }
        Put(p);
    }

    private void Repeat(int n)
    {
        if (_lastChange.Count == 0) return;
        _replaying = true;
        try { for (int i = 0; i < n; i++) foreach (var k in _lastChange) { if (Current == Mode.Insert && k.Length == 1) InsertText(k); else Key(k); } if (Current == Mode.Insert) Key("Esc"); }
        finally { _replaying = false; }
    }

    /// <summary>Insert-mode text when replaying "." (normally the TextBox types it).</summary>
    private void InsertText(string s) { b.Replace(b.Caret, 0, s); b.Caret += s.Length; }
}
