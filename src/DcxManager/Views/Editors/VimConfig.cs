using System.Text.RegularExpressions;

namespace DcxManager.Views.Editors;

/// <summary>
/// A Vim profile's settings (0.3.0 §13, DarkCipher0x: "vim profiles and customizability"), read from a small vimrc:
/// <code>
/// " comments start with a double quote
/// set tabstop=4 shiftwidth=4 expandtab      (ts, sw, et / noexpandtab)
/// set ignorecase smartcase                   (ic, scs)
/// set wrap / nowrap
/// let mapleader = ","
/// inoremap jk &lt;Esc&gt;                        (insert-mode mapping)
/// nnoremap ; :                               (normal mode)   nmap / nnoremap / map / noremap
/// vnoremap &lt;Leader&gt;y y                      (visual mode)   vmap / vnoremap / xnoremap
/// </code>
/// Key names: &lt;Esc&gt; &lt;CR&gt; &lt;Enter&gt; &lt;BS&gt; &lt;Space&gt; &lt;Tab&gt; &lt;Leader&gt; &lt;C-x&gt;. Mappings never recurse (every map
/// behaves like noremap: safer and predictable). Anything not understood is listed in <see cref="Errors"/> and ignored —
/// a profile can't run commands, read files or reach the network; it only changes keys and a few options.
/// </summary>
public sealed class VimConfig
{
    public int TabStop { get; private set; } = 4;
    public int ShiftWidth { get; private set; } = 4;
    public bool ExpandTab { get; private set; } = true;
    public bool IgnoreCase { get; private set; }
    public bool SmartCase { get; private set; }
    public bool? Wrap { get; private set; }
    public string Leader { get; private set; } = "\\";
    /// <summary>Left-hand side (engine keys) → right-hand side (engine keys), per mode.</summary>
    public Dictionary<string, List<string>> Normal { get; } = new();
    public Dictionary<string, List<string>> Insert { get; } = new();
    public Dictionary<string, List<string>> Visual { get; } = new();
    public List<string> Errors { get; } = new();

    public static readonly VimConfig Default = Parse("");

    /// <summary>The indent one level of &gt; / &lt; or a Tab press inserts.</summary>
    public string IndentUnit => ExpandTab ? new string(' ', ShiftWidth) : "\t";
    public string TabText => ExpandTab ? new string(' ', TabStop) : "\t";

    public static VimConfig Parse(string rc)
    {
        var c = new VimConfig();
        int n = 0;
        foreach (var raw in (rc ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            n++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('"')) continue;
            if (n > 500) { c.Errors.Add("only the first 500 lines are read"); break; }
            try { c.Line(line); }
            catch (FormatException ex) { c.Errors.Add($"line {n}: {ex.Message}"); }
        }
        return c;
    }

    private void Line(string line)
    {
        var m = Regex.Match(line, @"^(\w+)\s*(.*)$");
        var cmd = m.Groups[1].Value; var rest = m.Groups[2].Value.Trim();
        switch (cmd)
        {
            case "set" or "se":
                foreach (var opt in rest.Split(' ', StringSplitOptions.RemoveEmptyEntries)) Set(opt);
                break;
            case "let":
            {
                var l = Regex.Match(rest, "^(?:g:)?mapleader\\s*=\\s*([\"'])(.{1,8})\\1$");
                if (!l.Success) throw new FormatException("only “let mapleader = \",\"” is supported");
                Leader = l.Groups[2].Value == "\\<Space>" || l.Groups[2].Value == " " ? " " : l.Groups[2].Value;
                break;
            }
            case "map" or "noremap" or "nmap" or "nnoremap" or "nn" or "nno":
                Map(Normal, rest); break;
            case "imap" or "inoremap" or "ino":
                Map(Insert, rest); break;
            case "vmap" or "vnoremap" or "xmap" or "xnoremap" or "vn" or "xn":
                Map(Visual, rest); break;
            default: throw new FormatException($"“{cmd}” isn't supported (set, let mapleader, and the map commands are)");
        }
    }

    private void Set(string opt)
    {
        var kv = opt.Split('=', 2);
        var name = kv[0];
        int Num() => kv.Length == 2 && int.TryParse(kv[1], out var v) && v is >= 1 and <= 16 ? v : throw new FormatException($"{name} needs a number from 1 to 16");
        switch (name)
        {
            case "tabstop" or "ts": TabStop = Num(); break;
            case "shiftwidth" or "sw": ShiftWidth = Num(); break;
            case "softtabstop" or "sts": break;   // accepted, same as tabstop here
            case "expandtab" or "et": ExpandTab = true; break;
            case "noexpandtab" or "noet": ExpandTab = false; break;
            case "ignorecase" or "ic": IgnoreCase = true; break;
            case "noignorecase" or "noic": IgnoreCase = false; break;
            case "smartcase" or "scs": SmartCase = true; break;
            case "nosmartcase" or "noscs": SmartCase = false; break;
            case "wrap": Wrap = true; break;
            case "nowrap": Wrap = false; break;
            case "number" or "nu" or "nonumber" or "nonu" or "relativenumber" or "rnu" or "hlsearch" or "hls" or "incsearch" or "is" or "nocompatible" or "nocp": break;   // harmless: accepted
            default: throw new FormatException($"option “{name}” isn't supported");
        }
    }

    private void Map(Dictionary<string, List<string>> into, string rest)
    {
        rest = Regex.Replace(rest, @"^(<silent>|<buffer>|<nowait>)\s*", "", RegexOptions.IgnoreCase);
        var parts = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) throw new FormatException("a mapping needs two sides: map {lhs} {rhs}");
        var lhs = Keys(parts[0]); var rhs = Keys(parts[1].Trim());
        if (lhs.Count == 0 || lhs.Count > 8 || rhs.Count > 64) throw new FormatException("mapping too long");
        into[string.Join("\u0001", lhs)] = rhs;
    }

    /// <summary>"jk", "&lt;Leader&gt;w", "&lt;C-r&gt;" → engine keys ("j","k" / leader,"w" / "C-r").</summary>
    public List<string> Keys(string s)
    {
        var list = new List<string>();
        for (int i = 0; i < s.Length;)
        {
            if (s[i] == '<' && s.IndexOf('>', i) is var e && e > i + 1)
            {
                var name = s[(i + 1)..e];
                string? k = name.ToLowerInvariant() switch
                {
                    "esc" => "Esc", "cr" or "enter" or "return" => "Enter", "bs" or "backspace" => "Backspace", "space" => " ", "tab" => "\t",
                    "leader" => null, "lt" => "<", "bar" => "|", "bslash" => "\\",
                    _ when Regex.IsMatch(name, "^[Cc]-[A-Za-z]$") => "C-" + char.ToLowerInvariant(name[2]),
                    _ => "",
                };
                if (k == null) { list.AddRange(Leader.Select(ch => ch.ToString())); i = e + 1; continue; }
                if (k.Length == 0) throw new FormatException($"key “<{name}>” isn't known");
                list.Add(k); i = e + 1; continue;
            }
            list.Add(s[i].ToString()); i++;
        }
        return list;
    }
}
