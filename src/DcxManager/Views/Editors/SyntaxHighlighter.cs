using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace DcxManager.Views.Editors;

/// <summary>
/// Syntax colours for the text editor (0.3.0 §13) with no new package: a small tokenizer for the formats an engineer
/// edits on servers (shell, YAML, JSON, INI / systemd units, nginx / Apache-style blocks, TOML, Python, Dockerfile, .env,
/// XML / HTML, Markdown) and an adorner that draws the visible lines in colour over the TextBox, whose own text is made
/// transparent while colours are on. The TextBox keeps doing everything else (caret, selection, undo, Vim). The format
/// comes from the file name; unknown names stay plain. A "colours" switch turns the overlay off at any time.
/// </summary>
public static class SyntaxHighlighter
{
    public enum Tok { Text, Comment, String, Number, Key, Keyword, Section, Variable, Tag, Punct }
    public sealed record Run(int Start, int Length, Tok Kind);

    /// <summary>The language for a file name, or null for plain text.</summary>
    public static string? LanguageFor(string path)
    {
        var name = System.IO.Path.GetFileName(path).ToLowerInvariant();
        var ext = System.IO.Path.GetExtension(name);
        if (name is "dockerfile" or "containerfile" || name.StartsWith("dockerfile.")) return "dockerfile";
        if (name is ".env" || name.StartsWith(".env.") || name.EndsWith(".env")) return "env";
        if (name is ".bashrc" or ".zshrc" or ".profile" or ".bash_profile" or "makefile") return "shell";
        if (name.EndsWith("nginx.conf") || name.Contains("sites-available") || name.Contains("sites-enabled") || name.EndsWith(".vhost")) return "nginx";
        return ext switch
        {
            ".sh" or ".bash" or ".zsh" or ".ksh" => "shell",
            ".yml" or ".yaml" => "yaml",
            ".json" or ".jsonc" => "json",
            ".ini" or ".cfg" or ".conf" or ".service" or ".timer" or ".socket" or ".mount" or ".target" or ".network" or ".netdev" or ".link" or ".desktop" or ".repo" or ".properties" or ".gitconfig" or ".wgconf" => "ini",
            ".toml" => "toml",
            ".py" => "python",
            ".xml" or ".html" or ".htm" or ".xaml" or ".svg" or ".plist" or ".csproj" => "xml",
            ".md" or ".markdown" => "markdown",
            ".ps1" or ".psm1" => "powershell",
            ".ovpn" => "ini",
            _ => null,
        };
    }

    private static readonly Dictionary<string, HashSet<string>> Keywords = new()
    {
        ["shell"] = new(StringComparer.Ordinal) { "if", "then", "else", "elif", "fi", "for", "in", "do", "done", "while", "until", "case", "esac", "function", "return", "exit", "local", "export", "readonly", "set", "unset", "source", "echo", "printf", "test", "sudo", "cd", "break", "continue", "declare", "trap", "shift", "eval", "exec" },
        ["python"] = new(StringComparer.Ordinal) { "def", "class", "return", "if", "elif", "else", "for", "while", "in", "not", "and", "or", "import", "from", "as", "with", "try", "except", "finally", "raise", "pass", "break", "continue", "lambda", "yield", "None", "True", "False", "is", "global", "nonlocal", "async", "await", "del", "assert" },
        ["powershell"] = new(StringComparer.OrdinalIgnoreCase) { "if", "else", "elseif", "foreach", "for", "while", "do", "switch", "function", "return", "param", "try", "catch", "finally", "throw", "begin", "process", "end", "in", "break", "continue", "filter", "class", "enum", "using" },
        ["dockerfile"] = new(StringComparer.OrdinalIgnoreCase) { "FROM", "RUN", "CMD", "LABEL", "EXPOSE", "ENV", "ADD", "COPY", "ENTRYPOINT", "VOLUME", "USER", "WORKDIR", "ARG", "ONBUILD", "STOPSIGNAL", "HEALTHCHECK", "SHELL", "AS" },
        ["nginx"] = new(StringComparer.Ordinal) { "server", "location", "http", "events", "upstream", "listen", "server_name", "root", "index", "proxy_pass", "return", "rewrite", "include", "ssl_certificate", "ssl_certificate_key", "access_log", "error_log", "if", "map", "try_files", "add_header", "proxy_set_header", "worker_processes", "<VirtualHost", "</VirtualHost>", "<Directory", "</Directory>", "DocumentRoot", "ServerName", "Listen", "RewriteRule", "RewriteCond", "Require", "AllowOverride", "ProxyPass", "ProxyPassReverse" },
        ["json"] = new(StringComparer.Ordinal) { "true", "false", "null" },
        ["yaml"] = new(StringComparer.Ordinal) { "true", "false", "null", "yes", "no", "on", "off", "~" },
        ["toml"] = new(StringComparer.Ordinal) { "true", "false" },
    };

    /// <summary>Tokens of one line. Multi-line constructs (heredocs, block strings) are coloured line by line, which is
    /// what a 1-line tokenizer can do honestly; the common cases look right.</summary>
    public static List<Run> Tokenize(string line, string lang)
    {
        var o = new List<Run>();
        if (line.Length == 0) return o;
        int i = 0;
        void Add(int start, int len, Tok kind) { if (len > 0) o.Add(new Run(start, len, kind)); }
        // Section headers and comments that own the whole line.
        var trimmed = line.TrimStart();
        int indent = line.Length - trimmed.Length;
        if (lang is "ini" or "toml" && trimmed.StartsWith('[') && trimmed.TrimEnd().EndsWith(']')) { Add(0, line.Length, Tok.Section); return o; }
        if (lang == "markdown")
        {
            if (trimmed.StartsWith('#')) { Add(0, line.Length, Tok.Section); return o; }
            if (trimmed.StartsWith("```")) { Add(0, line.Length, Tok.Comment); return o; }
            if (trimmed.StartsWith("- ") || trimmed.StartsWith("* ") || Regex.IsMatch(trimmed, @"^\d+\. ")) Add(indent, trimmed.IndexOf(' ') + 1, Tok.Punct);
            foreach (Match m in Regex.Matches(line, @"`[^`]+`|\*\*[^*]+\*\*|\[[^\]]+\]\([^)]+\)")) Add(m.Index, m.Length, m.Value[0] == '`' ? Tok.String : m.Value[0] == '[' ? Tok.Key : Tok.Keyword);
            return o;
        }
        if (lang == "xml")
        {
            foreach (Match m in Regex.Matches(line, @"<!--.*?-->|<!--.*$|^.*?-->|<\?.*?\?>|</?[A-Za-z][\w:.-]*|/?>|""[^""]*""|'[^']*'|\b[A-Za-z_][\w:.-]*(?==)"))
            {
                var v = m.Value;
                Add(m.Index, m.Length, v.StartsWith("<!--") || v.EndsWith("-->") ? Tok.Comment : v.StartsWith('<') || v.EndsWith('>') ? Tok.Tag : v[0] is '"' or '\'' ? Tok.String : Tok.Key);
            }
            return o;
        }
        string commentStart = lang switch { "json" => "//", "toml" or "yaml" or "shell" or "python" or "dockerfile" or "nginx" or "env" or "powershell" => "#", _ => "#" };
        bool semiComment = lang == "ini";
        // A key = value / key: value at the start of the line.
        if (lang is "ini" or "toml" or "env" && Regex.Match(line, @"^\s*([^=\s#;][^=]*?)\s*=") is { Success: true } kv) { Add(kv.Groups[1].Index, kv.Groups[1].Length, Tok.Key); i = kv.Index + kv.Length; }
        else if (lang == "yaml" && Regex.Match(line, @"^\s*(-\s+)?([""']?[\w.\-/ ]+[""']?)\s*:(\s|$)") is { Success: true } yk) { Add(yk.Groups[2].Index, yk.Groups[2].Length, Tok.Key); i = yk.Groups[2].Index + yk.Groups[2].Length + 1; }
        else if (lang == "json" && Regex.Match(line, @"^\s*(""(?:[^""\\]|\\.)*"")\s*:") is { Success: true } jk) { Add(jk.Groups[1].Index, jk.Groups[1].Length, Tok.Key); i = jk.Groups[1].Index + jk.Groups[1].Length; }
        var kws = Keywords.GetValueOrDefault(lang);
        while (i < line.Length)
        {
            char c = line[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '#' && commentStart == "#" || semiComment && c == ';' || line.AsSpan(i).StartsWith("//") && lang is "json" || line.AsSpan(i).StartsWith("<#") && lang == "powershell")
            {
                // "#" inside a shell word ("$#", "a#b") isn't a comment; a space or line start before it is.
                if (c == '#' && i > 0 && !char.IsWhiteSpace(line[i - 1]) && lang is "shell" or "yaml" or "python" or "powershell") { i++; continue; }
                Add(i, line.Length - i, Tok.Comment); break;
            }
            if (c is '"' or '\'' or '`')
            {
                int j = i + 1;
                while (j < line.Length && line[j] != c) { if (line[j] == '\\' && c != '\'') j++; j++; }
                int len = Math.Min(j + 1, line.Length) - i;
                Add(i, len, Tok.String); i += len; continue;
            }
            if (c == '$' && lang is "shell" or "env" or "dockerfile" or "nginx" or "powershell" or "python")
            {
                int j = i + 1;
                if (j < line.Length && line[j] == '{') { while (j < line.Length && line[j] != '}') j++; j++; }
                else while (j < line.Length && (char.IsLetterOrDigit(line[j]) || line[j] is '_' or '?' or '@' or '!' or '#' or '*')) j++;
                Add(i, Math.Min(j, line.Length) - i, Tok.Variable); i = Math.Min(j, line.Length); continue;
            }
            if (char.IsDigit(c) && (i == 0 || !char.IsLetterOrDigit(line[i - 1])))
            {
                int j = i; while (j < line.Length && (char.IsLetterOrDigit(line[j]) || line[j] is '.' or ':' or '/' or '-' or '_')) j++;
                if (Regex.IsMatch(line[i..j], @"^(0x[0-9a-fA-F]+|\d+(\.\d+)*[a-zA-Z%]{0,3}|\d+(\.\d+){3}(/\d+)?|[\d.:]+:\d+)$")) { Add(i, j - i, Tok.Number); i = j; continue; }
                i = j; continue;
            }
            if (char.IsLetter(c) || c == '_' || c == '<' && lang == "nginx")
            {
                int j = i + 1; while (j < line.Length && (char.IsLetterOrDigit(line[j]) || line[j] is '_' or '-' or '.' or '/' or '>')) j++;
                var word = line[i..j];
                if (lang == "nginx" && i == indent) Add(i, j - i, Tok.Keyword);                   // the directive at the start of the line
                else if (lang == "dockerfile" && i == indent && kws!.Contains(word)) Add(i, j - i, Tok.Keyword);
                else if (lang is not ("nginx" or "dockerfile") && kws != null && kws.Contains(word)) Add(i, j - i, Tok.Keyword);
                else if (lang == "shell" && i == indent && line.AsSpan(j).TrimStart().StartsWith("()")) Add(i, j - i, Tok.Key);
                i = j; continue;
            }
            if (c is '{' or '}' or '[' or ']' or '(' or ')' or ',' or ':' or ';' or '=' or '|' or '>' or '<' or '&') { Add(i, 1, Tok.Punct); i++; continue; }
            i++;
        }
        return o;
    }

    /// <summary>The brushes (theme-aware: resolved from the resources each time the overlay paints).</summary>
    public static Brush BrushFor(Tok kind, Brush fg)
    {
        string? key = kind switch
        {
            Tok.Comment => "Muted", Tok.String => "Good", Tok.Number => "Warn", Tok.Key => "Accent", Tok.Keyword => "Accent",
            Tok.Section => "Accent", Tok.Variable => "Warn", Tok.Tag => "Accent", Tok.Punct => "Muted", _ => null,
        };
        if (key != null && Application.Current?.Resources[key] is Brush b) return b;
        return fg;
    }

    /// <summary>The overlay: draws the visible lines of <paramref name="box"/> in colour; the box's own text is transparent while attached.</summary>
    public sealed class Overlay : Adorner
    {
        private readonly TextBox _box;
        private readonly string _lang;
        private Brush? _savedFg;
        private readonly Dictionary<string, List<Run>> _cache = new();

        public Overlay(TextBox box, string lang) : base(box)
        {
            _box = box; _lang = lang;
            IsHitTestVisible = false;
            TextOptions.SetTextFormattingMode(this, TextOptions.GetTextFormattingMode(box));   // the same glyph metrics as the box
            _box.TextChanged += (_, _) => { _cache.Clear(); InvalidateVisual(); };
            _box.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => InvalidateVisual()));
            _box.SizeChanged += (_, _) => InvalidateVisual();
            _box.LayoutUpdated += (_, _) => { if (_lastFirst != SafeFirst()) InvalidateVisual(); };
        }
        private int _lastFirst = -1;
        private int SafeFirst() { try { return _box.GetFirstVisibleLineIndex(); } catch (Exception) { return -1; } }

        public void Attach()
        {
            var layer = AdornerLayer.GetAdornerLayer(_box);
            if (layer == null) return;
            layer.Add(this);
            _savedFg = _box.Foreground;
            _box.Foreground = Brushes.Transparent;
        }

        public void Detach()
        {
            AdornerLayer.GetAdornerLayer(_box)?.Remove(this);
            if (_savedFg != null) _box.Foreground = _savedFg; else _box.SetResourceReference(Control.ForegroundProperty, "Fg");
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (_box.ActualWidth <= 0 || _box.Text.Length == 0) return;
            int first, last;
            try { first = _box.GetFirstVisibleLineIndex(); last = _box.GetLastVisibleLineIndex(); } catch (Exception) { return; }
            _lastFirst = first;
            if (first < 0 || last < first) return;
            var fg = (Application.Current?.Resources["Fg"] as Brush) ?? Brushes.Black;
            var typeface = new Typeface(_box.FontFamily, _box.FontStyle, _box.FontWeight, _box.FontStretch);
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            // Clip to the text area so nothing is drawn over the scroll bars or the border.
            dc.PushClip(new RectangleGeometry(new Rect(_box.Padding.Left, _box.Padding.Top, Math.Max(0, _box.ActualWidth - _box.Padding.Left - _box.Padding.Right - SystemParameters.VerticalScrollBarWidth), Math.Max(0, _box.ActualHeight - _box.Padding.Top - _box.Padding.Bottom - SystemParameters.HorizontalScrollBarHeight))));
            for (int li = first; li <= last; li++)
            {
                int start; string line;
                try { start = _box.GetCharacterIndexFromLineIndex(li); line = _box.GetLineText(li).TrimEnd('\r', '\n'); }
                catch (Exception) { break; }
                if (line.Length == 0) continue;
                Rect r;
                try { r = _box.GetRectFromCharacterIndex(start); } catch (Exception) { break; }
                if (r.IsEmpty) continue;
                var ft = new FormattedText(line, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, _box.FontSize, fg, dpi) { TextAlignment = TextAlignment.Left, Trimming = TextTrimming.None };
                if (!_cache.TryGetValue(line, out var runs)) _cache[line] = runs = Tokenize(line, _lang);
                foreach (var run in runs) ft.SetForegroundBrush(BrushFor(run.Kind, fg), run.Start, Math.Min(run.Length, line.Length - run.Start));
                dc.DrawText(ft, new Point(r.X, r.Y));
            }
            dc.Pop();
        }
    }
}
