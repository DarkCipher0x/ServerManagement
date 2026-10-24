using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;

// Usage: Guide <repo root> <manifest> <version> <out.html>
// Manifest lines (see docs\guide\guide.txt):
//   TITLE <text> / SUBTITLE <text> / COVERS <text>   the cover page (0.4.0: one manifest per PDF of the release bundle;
//                                        defaults: "Complete guide", the version line, the parts list)
//   PART <title>                         a part divider (H1, new page)
//   DOC <path> [title=..] [dropkeywords] [cut=<heading line>] [take=<heading line>]   (dropkeywords also drops help.md's "module:" tags)
// Every document's headings move down one level (its H1 becomes a chapter). {VERSION} is replaced everywhere.
if (args.Length != 4) { Console.Error.WriteLine("usage: Guide <root> <manifest> <version> <out.html>"); return 2; }
var (root, manifest, version, outFile) = (args[0], args[1], args[2], args[3]);
var pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
var body = new StringBuilder();
var toc = new List<(int Level, string Id, string Text)>();
int n = 0;
string coverTitle = "Complete guide", coverSubtitle = "Complete guide — version {VERSION}", coverCovers = "Overview · User guide · Administration · Security · Developer reference · Release notes · Known limitations";

foreach (var raw in File.ReadAllLines(manifest, Encoding.UTF8))
{
    var line = raw.Trim();
    if (line.Length == 0 || line.StartsWith('#')) continue;
    if (line.StartsWith("TITLE ")) { coverTitle = line[6..]; continue; }
    if (line.StartsWith("SUBTITLE ")) { coverSubtitle = line[9..]; continue; }
    if (line.StartsWith("COVERS ")) { coverCovers = line[7..]; continue; }
    if (line.StartsWith("PART "))
    {
        var title = line[5..].Replace("{VERSION}", version);
        var id = "part" + (++n);
        toc.Add((1, id, title));
        body.Append($"<h1 class=\"part\" id=\"{id}\">{WebUtility.HtmlEncode(title)}</h1>\n");
        continue;
    }
    if (!line.StartsWith("DOC ")) throw new Exception("bad manifest line: " + line);
    var opts = Regex.Matches(line[4..], "(\\w+)=\"([^\"]*)\"|(\\w+)=(\\S+)|(\\S+)").Select(m =>
        m.Groups[1].Success ? (m.Groups[1].Value, m.Groups[2].Value) : m.Groups[3].Success ? (m.Groups[3].Value, m.Groups[4].Value) : ("", m.Groups[5].Value)).ToList();
    var path = Path.Combine(root, opts.First(o => o.Item1 == "").Item2);
    string? Opt(string k) => opts.FirstOrDefault(o => o.Item1 == k).Item2;
    bool Flag(string k) => opts.Any(o => o.Item1 == "" && o.Item2 == k);

    var lines = File.ReadAllLines(path, Encoding.UTF8).ToList();
    if (Opt("take") is { } take) lines = Section(lines, take, keep: true);
    if (Opt("cut") is { } cut) lines = Section(lines, cut, keep: false);
    if (Flag("dropkeywords")) lines = lines.Where(l => !l.StartsWith("keywords:") && !l.StartsWith("module:")).ToList();   // search keywords + module tags
    if (Opt("title") is { } chapter && lines.FindIndex(l => Regex.IsMatch(l, "^#{1,6} ")) is >= 0 and var hi)
        lines[hi] = new string('#', lines[hi].TakeWhile(c => c == '#').Count()) + " " + chapter;
    var md = string.Join("\n", lines).Replace("{VERSION}", version);

    var doc = Markdown.Parse(md, pipeline);
    int d = ++n;
    var heads = doc.Descendants<HeadingBlock>().ToList();
    // top=N: move the document's first heading (and the rest with it) to level N, e.g. a section taken out of a file.
    int delta = Opt("top") is { } top && heads.Count > 0 ? int.Parse(top) - heads[0].Level : 0;
    foreach (var h in heads)
    {
        h.Level = Math.Clamp(h.Level + delta, 1, 6);
        h.Level = Math.Min(6, h.Level + 1);
        var text = Plain(h);
        var id = $"d{d}-{Slug(text)}";
        h.GetAttributes().Id = id;
        if (h.Level <= (Opt("toc") is { } depth ? int.Parse(depth) : 3)) toc.Add((Math.Min(h.Level, 3), id, text));
    }
    var sw = new StringWriter();
    var r = new HtmlRenderer(sw);
    pipeline.Setup(r);
    r.Render(doc);
    var html = sw.ToString();
    // Links to repository files mean nothing in a PDF: keep the text, drop the link.
    html = Regex.Replace(html, "<a href=\"(?!https?:|#)[^\"]*\">(.*?)</a>", "$1", RegexOptions.Singleline);
    body.Append($"<section class=\"doc\">\n{html}\n</section>\n");
}

var tocHtml = new StringBuilder("<nav class=\"toc\"><h1 class=\"toc-title\">Contents</h1>\n");
foreach (var (lvl, id, text) in toc)
    tocHtml.Append($"<div class=\"t{lvl}\"><a href=\"#{id}\">{WebUtility.HtmlEncode(text)}</a></div>\n");
tocHtml.Append("</nav>\n");

var template = File.ReadAllText(Path.Combine(Path.GetDirectoryName(manifest)!, "guide.html"), Encoding.UTF8);
var logo = Path.Combine(root, "src", "DcxManager", "Assets", "logo.png");
var logoUri = File.Exists(logo) ? "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(logo)) : "";
File.WriteAllText(outFile, template.Replace("{TITLE}", WebUtility.HtmlEncode(coverTitle)).Replace("{SUBTITLE}", WebUtility.HtmlEncode(coverSubtitle)).Replace("{COVERS}", WebUtility.HtmlEncode(coverCovers))
    .Replace("{VERSION}", version).Replace("{DATE}", DateTime.Now.ToString("d MMMM yyyy"))
    .Replace("{LOGO}", logoUri).Replace("{TOC}", tocHtml.ToString()).Replace("{BODY}", body.ToString()), new UTF8Encoding(false));
Console.WriteLine($"{toc.Count} headings, {body.Length / 1024} KB → {outFile}");
return 0;

// The lines of the section that starts with <heading> (keep) or everything except it (cut); a section runs to the
// next heading of the same or a higher level.
static List<string> Section(List<string> lines, string heading, bool keep)
{
    int start = lines.FindIndex(l => l.TrimEnd() == heading || l.StartsWith(heading + " ") || l.StartsWith(heading));
    if (start < 0) throw new Exception("heading not found: " + heading);
    int level = heading.TakeWhile(c => c == '#').Count();
    int end = lines.FindIndex(start + 1, l => Regex.IsMatch(l, "^#{1," + level + "} "));
    if (end < 0) end = lines.Count;
    return keep ? lines.GetRange(start, end - start) : lines.Take(start).Concat(lines.Skip(end)).ToList();
}

static string Plain(HeadingBlock h)
{
    var sb = new StringBuilder();
    foreach (var i in h.Inline?.Descendants<Markdig.Syntax.Inlines.Inline>() ?? Enumerable.Empty<Markdig.Syntax.Inlines.Inline>())
        if (i is Markdig.Syntax.Inlines.LiteralInline l) sb.Append(l.Content);
        else if (i is Markdig.Syntax.Inlines.CodeInline c) sb.Append(c.Content);
    return sb.ToString().Trim();
}

static string Slug(string s)
{
    var t = Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
    return t.Length == 0 ? "h" : t.Length > 60 ? t[..60] : t;
}
