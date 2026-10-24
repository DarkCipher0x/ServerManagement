using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace DcxManager.Views;

/// <summary>
/// Markdown → a WPF FlowDocument (0.6.12, ADR-091): the Docs editor's preview, our own small renderer (no HTML engine,
/// no new library). Covers what runbooks use: headings, paragraphs (two spaces or \ for a line break), **bold**, *italic*,
/// ~~strike~~, `code`, fenced code blocks, &gt; quotes, nested bullet / numbered / task lists, pipe tables, rules and links.
/// Safe by construction: raw HTML is shown as text, images are never fetched (their description is shown), and only
/// https: and mailto: links are live — they go to <c>openLink</c>, which asks first. Colours and sizes follow the theme
/// and the type ramp (resource references), so the preview restyles with the app.
/// </summary>
public static class MarkdownRenderer
{
    public static FlowDocument Render(string markdown, Action<string>? openLink = null)
    {
        var doc = new FlowDocument { PagePadding = new Thickness(18, 14, 18, 14), TextAlignment = TextAlignment.Left, Background = Brushes.Transparent };
        doc.SetResourceReference(TextElement.ForegroundProperty, "Fg");
        doc.SetResourceReference(TextElement.FontFamilyProperty, "UiFont");
        doc.SetResourceReference(TextElement.FontSizeProperty, "FsBase");
        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        foreach (var b in Blocks(lines, 0, lines.Length, openLink)) doc.Blocks.Add(b);
        return doc;
    }

    private static readonly Regex Heading = new(@"^(#{1,6})\s+(.*?)\s*#*\s*$", RegexOptions.Compiled);
    private static readonly Regex Rule = new(@"^\s{0,3}([-*_])(\s*\1){2,}\s*$", RegexOptions.Compiled);
    private static readonly Regex Fence = new(@"^\s{0,3}(```+|~~~+)\s*([\w+#.-]*)", RegexOptions.Compiled);
    private static readonly Regex ListItem = new(@"^(\s*)([-*+]|\d{1,9}[.)])\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex TableSep = new(@"^\s*\|?\s*:?-{1,}:?\s*(\|\s*:?-{1,}:?\s*)*\|?\s*$", RegexOptions.Compiled);

    private static IEnumerable<Block> Blocks(string[] l, int from, int to, Action<string>? openLink)
    {
        int i = from;
        while (i < to)
        {
            var line = l[i];
            if (line.Trim().Length == 0) { i++; continue; }

            if (Fence.Match(line) is { Success: true } fm)
            {
                var marker = fm.Groups[1].Value;
                var code = new StringBuilder();
                i++;
                while (i < to && !l[i].TrimStart().StartsWith(marker, StringComparison.Ordinal)) code.AppendLine(l[i++]);
                i++;   // the closing fence (or the end)
                yield return CodeBlock(code.ToString().TrimEnd('\n', '\r'), fm.Groups[2].Value);
                continue;
            }
            if (Heading.Match(line) is { Success: true } hm)
            {
                var level = hm.Groups[1].Value.Length;
                var p = new Paragraph { Margin = new Thickness(0, level <= 2 ? 14 : 10, 0, 6), FontWeight = level <= 3 ? FontWeights.Bold : FontWeights.SemiBold };
                p.SetResourceReference(TextElement.FontSizeProperty, level switch { 1 => "FsValue", 2 => "FsH1", 3 => "FsHeading", 4 => "FsTitle", _ => "FsBase" });
                p.SetResourceReference(TextElement.ForegroundProperty, "FgStrong");
                Inline(p.Inlines, hm.Groups[2].Value, openLink);
                if (level <= 2) { p.BorderThickness = new Thickness(0, 0, 0, 1); p.SetResourceReference(Block.BorderBrushProperty, "Line"); p.Padding = new Thickness(0, 0, 0, 4); }
                yield return p;
                i++;
                continue;
            }
            if (Rule.IsMatch(line))
            {
                var line1 = new System.Windows.Controls.Border { Height = 1 };
                line1.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "LineStrong");
                yield return new BlockUIContainer(line1) { Margin = new Thickness(0, 8, 0, 8) };
                i++;
                continue;
            }
            if (line.TrimStart().StartsWith('>'))
            {
                int start = i;
                var inner = new List<string>();
                while (i < to && l[i].Trim().Length > 0 && (l[i].TrimStart().StartsWith('>') || i > start)) { var t = l[i].TrimStart(); inner.Add(t.StartsWith('>') ? t[1..].TrimStart(' ') : t); i++; }
                var q = new Section { Margin = new Thickness(0, 6, 0, 6), Padding = new Thickness(12, 2, 6, 2), BorderThickness = new Thickness(3, 0, 0, 0) };
                q.SetResourceReference(Block.BorderBrushProperty, "Accent");
                q.SetResourceReference(TextElement.ForegroundProperty, "Muted");
                var arr = inner.ToArray();
                foreach (var b in Blocks(arr, 0, arr.Length, openLink)) q.Blocks.Add(b);
                yield return q;
                continue;
            }
            if (ListItem.IsMatch(line))
            {
                yield return ListBlock(l, ref i, to, ListItem.Match(line).Groups[1].Value.Length, openLink);
                continue;
            }
            if (line.Contains('|') && i + 1 < to && TableSep.IsMatch(l[i + 1]) && l[i + 1].Contains('-'))
            {
                yield return TableBlock(l, ref i, to, openLink);
                continue;
            }

            // A paragraph: lines up to a blank line or the start of another block.
            var para = new Paragraph { Margin = new Thickness(0, 4, 0, 8) };
            bool first = true;
            while (i < to && l[i].Trim().Length > 0 && (first || !StartsBlock(l, i, to)))
            {
                var text = l[i];
                bool hard = text.EndsWith("  ") || text.EndsWith('\\');
                if (!first) para.Inlines.Add(new Run(" "));
                Inline(para.Inlines, hard ? text.TrimEnd(' ', '\\') : text.Trim(), openLink);
                if (hard) para.Inlines.Add(new LineBreak());
                first = false;
                i++;
            }
            yield return para;
        }
    }

    private static bool StartsBlock(string[] l, int i, int to) =>
        Heading.IsMatch(l[i]) || Fence.IsMatch(l[i]) || Rule.IsMatch(l[i]) || l[i].TrimStart().StartsWith('>') || ListItem.IsMatch(l[i])
        || (l[i].Contains('|') && i + 1 < to && TableSep.IsMatch(l[i + 1]));

    private static Block CodeBlock(string code, string lang)
    {
        var p = new Paragraph(new Run(code)) { Margin = new Thickness(0, 6, 0, 10), Padding = new Thickness(10, 8, 10, 8), BorderThickness = new Thickness(1) };
        p.SetResourceReference(TextElement.FontFamilyProperty, "MonoFont");
        p.SetResourceReference(TextElement.FontSizeProperty, "FsBody");
        p.SetResourceReference(Block.BackgroundProperty, "Surface2");
        p.SetResourceReference(Block.BorderBrushProperty, "Line");
        if (lang.Length > 0) p.ToolTip = lang;
        return p;
    }

    /// <summary>A list from line <paramref name="i"/> at <paramref name="indent"/>; deeper items become nested lists.</summary>
    private static List ListBlock(string[] l, ref int i, int to, int indent, Action<string>? openLink)
    {
        var first = ListItem.Match(l[i]);
        bool ordered = char.IsDigit(first.Groups[2].Value[0]);
        var list = new List { MarkerStyle = ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc, Margin = new Thickness(0, 2, 0, 8), Padding = new Thickness(22, 0, 0, 0) };
        if (ordered && int.TryParse(first.Groups[2].Value.TrimEnd('.', ')'), out var startAt) && startAt > 1) list.StartIndex = startAt;
        while (i < to)
        {
            var m = ListItem.Match(l[i]);
            if (!m.Success)
            {
                // A continuation line (indented text) belongs to the item above; anything else ends the list.
                if (l[i].Trim().Length > 0 && l[i].Length - l[i].TrimStart().Length > indent && list.ListItems.LastListItem is { } last && last.Blocks.LastBlock is Paragraph lp)
                { lp.Inlines.Add(new Run(" ")); Inline(lp.Inlines, l[i].Trim(), openLink); i++; continue; }
                break;
            }
            int ind = m.Groups[1].Value.Length;
            if (ind < indent) break;
            if (ind > indent && list.ListItems.LastListItem is { } parent) { parent.Blocks.Add(ListBlock(l, ref i, to, ind, openLink)); continue; }
            if (char.IsDigit(m.Groups[2].Value[0]) != ordered) break;
            var text = m.Groups[3].Value;
            var p = new Paragraph { Margin = new Thickness(0, 1, 0, 1) };
            var task = Regex.Match(text, @"^\[([ xX])\]\s+(.*)$");
            if (task.Success)
            {
                bool done = task.Groups[1].Value != " ";
                p.Inlines.Add(new Run(done ? "☑ " : "☐ ") { FontWeight = FontWeights.Bold });
                var span = new Span();
                if (done) { span.TextDecorations = TextDecorations.Strikethrough; span.SetResourceReference(TextElement.ForegroundProperty, "Muted"); }
                Inline(span.Inlines, task.Groups[2].Value, openLink);
                p.Inlines.Add(span);
            }
            else Inline(p.Inlines, text, openLink);
            list.ListItems.Add(new ListItem(p));
            i++;
            if (i < to && l[i].Trim().Length == 0 && i + 1 < to && ListItem.IsMatch(l[i + 1])) i++;   // a blank line between items
        }
        if (list.ListItems.Count > 0 && list.ListItems.All(li => li.Blocks.FirstBlock is Paragraph pp && pp.Inlines.FirstInline is Run r && r.Text is "☑ " or "☐ "))
            list.MarkerStyle = TextMarkerStyle.None;
        return list;
    }

    private static Table TableBlock(string[] l, ref int i, int to, Action<string>? openLink)
    {
        static List<string> Cells(string row)
        {
            var t = row.Trim();
            if (t.StartsWith('|')) t = t[1..];
            if (t.EndsWith('|') && !t.EndsWith("\\|")) t = t[..^1];
            return Regex.Split(t, @"(?<!\\)\|").Select(c => c.Trim().Replace("\\|", "|")).ToList();
        }
        var header = Cells(l[i]);
        var aligns = Cells(l[i + 1]).Select(c => c.StartsWith(':') && c.EndsWith(':') ? TextAlignment.Center : c.EndsWith(':') ? TextAlignment.Right : TextAlignment.Left).ToList();
        i += 2;
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 6, 0, 10), BorderThickness = new Thickness(1) };
        table.SetResourceReference(Block.BorderBrushProperty, "Line");
        for (int c = 0; c < header.Count; c++) table.Columns.Add(new TableColumn());
        var group = new TableRowGroup();
        table.RowGroups.Add(group);
        TableRow Row(List<string> cells, bool head)
        {
            var row = new TableRow();
            if (head) row.SetResourceReference(TableRow.BackgroundProperty, "Surface2");
            for (int c = 0; c < header.Count; c++)
            {
                var p = new Paragraph { Margin = new Thickness(0), TextAlignment = c < aligns.Count ? aligns[c] : TextAlignment.Left, FontWeight = head ? FontWeights.SemiBold : FontWeights.Normal };
                Inline(p.Inlines, c < cells.Count ? cells[c] : "", openLink);
                var cell = new TableCell(p) { Padding = new Thickness(8, 4, 8, 4), BorderThickness = new Thickness(0, 0, 1, 1) };
                cell.SetResourceReference(TableCell.BorderBrushProperty, "Line");
                row.Cells.Add(cell);
            }
            return row;
        }
        group.Rows.Add(Row(header, true));
        while (i < to && l[i].Contains('|') && l[i].Trim().Length > 0) group.Rows.Add(Row(Cells(l[i++]), false));
        return table;
    }

    // ---- inline ----------------------------------------------------------------------------------------------------

    private static readonly Regex InlineRx = new(
        @"(?<code>`+)(?<ct>.+?)\k<code>" +
        @"|!\[(?<alt>[^\]]*)\]\((?<isrc>[^)\s]*)[^)]*\)" +
        @"|\[(?<lt>[^\]]+)\]\((?<href>[^)\s]+)(?:\s+""[^""]*"")?\)" +
        @"|<(?<auto>(?:https://|mailto:)[^>\s]+)>" +
        @"|(?<b>\*\*|__)(?<bt>.+?)\k<b>" +
        @"|~~(?<st>.+?)~~" +
        @"|(?<i>[*_])(?<it>(?!\s).+?(?<!\s))\k<i>",
        RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));   // a pathological line falls back to plain text (see Inline)

    /// <summary>Inline Markdown into <paramref name="into"/>. Unknown syntax and raw HTML stay as plain text.</summary>
    private static void Inline(InlineCollection into, string text, Action<string>? openLink, int depth = 0)
    {
        List<Match> matches;
        try { matches = InlineRx.Matches(text).ToList(); }
        catch (RegexMatchTimeoutException) { into.Add(new Run(text)); return; }   // never freeze the preview on a crafted line
        int pos = 0;
        foreach (var m in matches)
        {
            if (m.Index > pos) into.Add(new Run(Unescape(text[pos..m.Index])));
            pos = m.Index + m.Length;
            if (m.Groups["code"].Success)
            {
                var r = new Run(m.Groups["ct"].Value.Trim());
                r.SetResourceReference(TextElement.FontFamilyProperty, "MonoFont");
                r.SetResourceReference(TextElement.BackgroundProperty, "Surface2");
                into.Add(r);
            }
            else if (m.Groups["alt"].Success)
            {
                var r = new Run("🖼 " + (m.Groups["alt"].Value.Length > 0 ? m.Groups["alt"].Value : "image")) { FontStyle = FontStyles.Italic, ToolTip = "Images aren't loaded (privacy): " + m.Groups["isrc"].Value };
                r.SetResourceReference(TextElement.ForegroundProperty, "Muted");
                into.Add(r);
            }
            else if (m.Groups["lt"].Success || m.Groups["auto"].Success)
            {
                var href = m.Groups["lt"].Success ? m.Groups["href"].Value : m.Groups["auto"].Value;
                var label = m.Groups["lt"].Success ? m.Groups["lt"].Value : href;
                if (LinkAllowed(href) && openLink != null)
                {
                    var link = new Hyperlink { ToolTip = href };
                    link.SetResourceReference(TextElement.ForegroundProperty, "Accent");
                    if (depth < 4) Inline(link.Inlines, label, openLink, depth + 1); else link.Inlines.Add(new Run(label));
                    var target = href;
                    link.Click += (_, _) => openLink(target);
                    into.Add(link);
                }
                else into.Add(new Run(label) { ToolTip = href, TextDecorations = TextDecorations.Underline });
            }
            else if (m.Groups["b"].Success) { var s = new Bold(); Nested(s.Inlines, m.Groups["bt"].Value); into.Add(s); }
            else if (m.Groups["st"].Success) { var s = new Span { TextDecorations = TextDecorations.Strikethrough }; Nested(s.Inlines, m.Groups["st"].Value); into.Add(s); }
            else if (m.Groups["i"].Success) { var s = new Italic(); Nested(s.Inlines, m.Groups["it"].Value); into.Add(s); }
        }
        if (pos < text.Length) into.Add(new Run(Unescape(text[pos..])));

        void Nested(InlineCollection c, string inner) { if (depth < 4) Inline(c, inner, openLink, depth + 1); else c.Add(new Run(inner)); }
    }

    /// <summary>Only https: and mailto: links are live; anything else (file:, javascript:, http:, relative) is shown as text.</summary>
    public static bool LinkAllowed(string href) =>
        Uri.TryCreate(href, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeMailto);

    private static string Unescape(string s) => Regex.Replace(s, @"\\([\\`*_{}\[\]()#+\-.!|~>])", "$1");

    /// <summary>The headings of a document (level, text, line number) for the outline.</summary>
    public static List<(int Level, string Text, int Line)> Outline(string markdown)
    {
        var o = new List<(int, string, int)>();
        bool inFence = false;
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (Fence.IsMatch(lines[i])) { inFence = !inFence; continue; }
            if (!inFence && Heading.Match(lines[i]) is { Success: true } h) o.Add((h.Groups[1].Value.Length, h.Groups[2].Value, i));
        }
        return o;
    }
}
