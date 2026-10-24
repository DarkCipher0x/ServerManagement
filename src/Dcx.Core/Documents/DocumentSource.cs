using System.Globalization;
using System.Net;
using System.Text;

namespace DcxManager.Documents;

// What the document viewer shows and the page scaffolding of generated documents (moved out of Views\DocumentViewer.cs
// in 0.2.0 phase 6: providers below the screens produce documents; only the viewer itself is UI).

/// <summary>
/// Something to show in the document viewer: a PDF, an HTML document (invoices, statements, reports) or plain text.
/// The bytes stay in memory — the viewer serves them to WebView2 directly with no-store, so nothing is cached to disk.
/// </summary>
public sealed record DocumentSource(string Title, string FileName, string Mime, byte[] Bytes)
{
    /// <summary>Plain text the AI gets for "review" (e.g. invoice lines). Null = the document text itself (text/HTML only).</summary>
    public string? AiText { get; init; }
    /// <summary>What "✦ Review with AI" asks for.</summary>
    public string AiTask { get; init; } = "Summarise this document: what it is, the key facts and numbers, and anything unusual or that needs action.";
    /// <summary>An official copy elsewhere (e.g. the invoice PDF in the Vultr portal), opened in the app's browser window.</summary>
    public string? ExternalUrl { get; init; }
    public string? ExternalLabel { get; init; }

    public bool IsPdf => Mime == "application/pdf";
    public bool IsHtml => Mime.StartsWith("text/html");

    public static DocumentSource Html(string title, string fileName, string html) => new(title, fileName, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html));
    public static DocumentSource Pdf(string title, string fileName, byte[] pdf) => new(title, fileName, "application/pdf", pdf);
    public static DocumentSource Text(string title, string fileName, string text) =>
        Html(title, fileName, DocumentHtml.Page(title, $"<pre>{WebUtility.HtmlEncode(text)}</pre>")) with { AiText = text };
}

/// <summary>Print-friendly HTML scaffolding shared by every generated document.</summary>
public static class DocumentHtml
{
    public static string Page(string title, string body, string? footer = null) => $$"""
        <!doctype html><html><head><meta charset="utf-8"><title>{{WebUtility.HtmlEncode(title)}}</title>
        <style>
          :root { color-scheme: light; }
          body { font: 14px/1.5 "Segoe UI", system-ui, sans-serif; color: #10201f; background: #fff; margin: 0; padding: 32px 40px; }
          h1 { font-size: 22px; margin: 0 0 4px; } .muted { color: #5b6f6e; } .right { text-align: right; }
          table { border-collapse: collapse; width: 100%; margin: 18px 0; font-size: 13px; }
          th, td { border-bottom: 1px solid #d5e2e1; padding: 6px 8px; text-align: left; vertical-align: top; }
          th { background: #eef5f4; font-weight: 600; } td.num, th.num { text-align: right; font-variant-numeric: tabular-nums; white-space: nowrap; }
          tfoot td { font-weight: 700; border-top: 2px solid #10201f; border-bottom: none; }
          .head { display: flex; justify-content: space-between; align-items: flex-start; border-bottom: 3px solid #36D2CB; padding-bottom: 12px; }
          .foot { margin-top: 28px; font-size: 11.5px; color: #5b6f6e; }
          .brand { font: 700 11px "Segoe UI", system-ui, sans-serif; letter-spacing: .14em; color: #1aa49e; margin-bottom: 10px; }
          pre { font: 12.5px/1.45 "Cascadia Mono", Consolas, monospace; white-space: pre-wrap; word-break: break-word; }
          @media print { body { padding: 0; } .noprint { display: none; } }
        </style></head><body><div class="brand">DARKCIPHER SYSTEMS</div>{{body}}<div class="foot">{{(footer != null ? footer + "<br>" : "")}}{{WebUtility.HtmlEncode(Services.AppInfo.FooterLine)}}</div></body></html>
        """;

    public static string Esc(string? s) => WebUtility.HtmlEncode(s ?? "");

    /// <summary>A table document from rows of (column → text). Numeric-looking columns are right-aligned.</summary>
    public static string Table(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows, IReadOnlyList<string>? footerRow = null)
    {
        var list = rows.ToList();
        bool Num(int c) => list.Count > 0 && list.All(r => c < r.Count && (r[c].Length == 0 || double.TryParse(r[c].TrimStart('$', '-'), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out _)));
        var nums = Enumerable.Range(0, headers.Count).Select(Num).ToArray();
        var sb = new StringBuilder("<table><thead><tr>");
        for (int i = 0; i < headers.Count; i++) sb.Append($"<th{(nums[i] ? " class=\"num\"" : "")}>{Esc(headers[i])}</th>");
        sb.Append("</tr></thead><tbody>");
        foreach (var r in list)
        {
            sb.Append("<tr>");
            for (int i = 0; i < headers.Count; i++) sb.Append($"<td{(nums[i] ? " class=\"num\"" : "")}>{Esc(i < r.Count ? r[i] : "")}</td>");
            sb.Append("</tr>");
        }
        sb.Append("</tbody>");
        if (footerRow != null)
        {
            sb.Append("<tfoot><tr>");
            for (int i = 0; i < headers.Count; i++) sb.Append($"<td{(nums[i] ? " class=\"num\"" : "")}>{Esc(i < footerRow.Count ? footerRow[i] : "")}</td>");
            sb.Append("</tr></tfoot>");
        }
        return sb.Append("</table>").ToString();
    }
}
