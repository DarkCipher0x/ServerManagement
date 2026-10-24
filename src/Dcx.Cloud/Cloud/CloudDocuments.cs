using System.Globalization;
using System.Text;
using DcxManager.Views;
using DcxManager.Documents;

namespace DcxManager.Cloud;

/// <summary>Any cloud list (instances, billing history, images…) as a printable table document with an AI review text.</summary>
public static class CloudDocuments
{
    private static string Day(string iso) => iso.Length >= 10 ? iso[..10] : iso;
    private static decimal Num(string s) => decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0;
    private static string Money(decimal d) => d.ToString("0.00", CultureInfo.InvariantCulture);

    public static DocumentSource ListDocument(string provider, ResourceDef def, IReadOnlyList<JRow> rows, string account, string crumb)
    {
        var headers = def.Columns.Select(c => c.Header).ToList();
        var data = rows.Select(r => (IReadOnlyList<string>)def.Columns.Select(c => c.Key.Contains("date", StringComparison.OrdinalIgnoreCase) ? Day(r[c.Key]) : r[c.Key]).ToList()).ToList();
        string[]? footer = null;
        var totalCol = def.Columns.Select((c, n) => (c, n)).FirstOrDefault(t => t.c.Key is "total" or "amount");
        if (totalCol.c != null)
        {
            footer = new string[headers.Count];
            footer[0] = "Total";
            footer[totalCol.n] = Money(rows.Sum(r => Num(r[totalCol.c.Key])));
        }
        var esc = DocumentHtml.Esc;
        var body = $"<div class=\"head\"><div><h1>{esc(crumb)}</h1><div class=\"muted\">{rows.Count} row(s)</div></div>" +
                   $"<div class=\"right\"><b>{esc(provider)}</b><br><span class=\"muted\">Account: {esc(account)}<br>{DateTime.Now:yyyy-MM-dd HH:mm}</span></div></div>" +
                   DocumentHtml.Table(headers, data, footer);
        var text = new StringBuilder($"{crumb} — {provider} account {account}\n{string.Join("\t", headers)}\n");
        foreach (var r in data) text.AppendLine(string.Join("\t", r));
        var slug = string.Concat(crumb.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-')).Trim('-');
        return DocumentSource.Html($"{crumb} — {account}", $"{provider.ToLowerInvariant()}-{slug}-{DateTime.Now:yyyyMMdd}.html",
            DocumentHtml.Page(crumb, body, $"Generated from the {provider} API.")) with
        {
            AiText = text.ToString(),
            AiTask = def.Group == "Account"
                ? "Review this billing data: totals, the biggest cost items, trends or spikes, anything unusual, and concrete ways to reduce cost."
                : $"Review this list of {provider} {def.Title}: what is there, anything misconfigured, idle, risky or costly, and suggested next steps.",
        };
    }
}
