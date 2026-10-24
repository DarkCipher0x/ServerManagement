using System.Globalization;
using System.Text;
using System.Text.Json;
using DcxManager.Services;
using DcxManager.Views;
using DcxManager.Documents;

namespace DcxManager.Vultr;

/// <summary>
/// Turns Vultr billing data into documents for the viewer. Vultr's API v2 has invoices, their line items, billing
/// history and pending charges as JSON — but no PDF download (the official PDF exists only in the customer portal).
/// So the app renders a printable invoice from the line items (Save as PDF works from the viewer) and links the portal
/// for the official copy. Nothing here is invented: every number comes from the API response.
/// </summary>
public static class VultrDocuments
{
    public const string PortalBilling = "https://my.vultr.com/billing/";

    private static string Day(string iso) => iso.Length >= 10 ? iso[..10] : iso;
    private static decimal Num(string s) => decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0;
    private static string Money(decimal d) => d.ToString("0.00", CultureInfo.InvariantCulture);

    public sealed record Invoice(string Id, string Date, string Description, decimal Amount, decimal Balance, List<string[]> Lines, decimal LinesTotal);

    public static async Task<Invoice> LoadInvoiceAsync(VultrClient v, string id, CancellationToken ct = default)
    {
        var resp = await v.GetAsync($"/billing/invoices/{Uri.EscapeDataString(id)}", ct);
        var inv = resp.TryGetProperty("billing_invoice", out var x) ? x : resp;
        var items = await v.ListAsync($"/billing/invoices/{Uri.EscapeDataString(id)}/items", "invoice_items", ct);
        var lines = items.Select(i => new[]
        {
            J.Str(i, "description"), J.Str(i, "product"),
            $"{Day(J.Str(i, "start_date"))} → {Day(J.Str(i, "end_date"))}",
            $"{J.Str(i, "units")} {J.Str(i, "unit_type")}".Trim(),
            J.Str(i, "unit_price"), Money(Num(J.Str(i, "total"))),
        }).ToList();
        return new Invoice(id, Day(J.Str(inv, "date")), J.Str(inv, "description"), Num(J.Str(inv, "amount")), Num(J.Str(inv, "balance")), lines, lines.Sum(l => Num(l[5])));
    }

    public static string InvoiceText(Invoice i, string account)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Vultr invoice {i.Id} · account {account} · date {i.Date} · {i.Description}");
        sb.AppendLine($"Invoice amount: {Money(i.Amount)} USD · balance after: {Money(i.Balance)} · sum of line items: {Money(i.LinesTotal)}");
        sb.AppendLine("description\tproduct\tperiod\tunits\tunit price\ttotal");
        foreach (var l in i.Lines) sb.AppendLine(string.Join("\t", l));
        return sb.ToString();
    }

    public static async Task<DocumentSource> InvoiceDocumentAsync(VultrClient v, string id, CancellationToken ct = default)
    {
        var i = await LoadInvoiceAsync(v, id, ct);
        var esc = DocumentHtml.Esc;
        var mismatch = Math.Abs(i.LinesTotal - i.Amount) >= 0.01m && i.Lines.Count > 0
            ? $"<p class=\"muted\">Note: the line items add up to {Money(i.LinesTotal)}, the invoice amount is {Money(i.Amount)} (credits, tax or rounding are applied by Vultr outside the item list).</p>" : "";
        var body =
            $"<div class=\"head\"><div><h1>Invoice {esc(i.Id)}</h1><div class=\"muted\">{esc(i.Description)}</div></div>" +
            $"<div class=\"right\"><b>Vultr</b><br><span class=\"muted\">Account: {esc(v.AccountName)}<br>Date: {esc(i.Date)}</span></div></div>" +
            DocumentHtml.Table(new[] { "Description", "Product", "Period", "Units", "Unit price", "Total (USD)" }, i.Lines,
                new[] { "Invoice amount", "", "", "", "", Money(i.Amount) }) + mismatch +
            $"<p class=\"muted\">Balance after this invoice: {Money(i.Balance)} USD</p>";
        var footer = $"Generated on {DateTime.Now:yyyy-MM-dd HH:mm} from the Vultr API (GET /billing/invoices/{esc(i.Id)} and /items). " +
                     "This is a readable copy of your billing data; the official PDF invoice is in the Vultr customer portal.";
        return DocumentSource.Html($"Vultr invoice {i.Id} ({i.Date})", $"vultr-invoice-{i.Id}.html", DocumentHtml.Page($"Vultr invoice {i.Id}", body, footer)) with
        {
            AiText = InvoiceText(i, v.AccountName),
            AiTask = "Review this cloud invoice: total and what drives it (largest line items by product), anything unusual (unexpected products, partial periods, idle resources, bandwidth overage, duplicate lines), and concrete ways to reduce cost. Use only the numbers given.",
            ExternalUrl = PortalBilling,
            ExternalLabel = "Official PDF (Vultr portal)",
        };
    }

    /// <summary>Two invoices side by side for "✦ Compare with previous invoice".</summary>
    public static async Task CompareAsync(VultrClient v, string newerId, string olderId)
    {
        if (Assist.Current is not { } assist) return;
        var a = await LoadInvoiceAsync(v, newerId);
        var b = await LoadInvoiceAsync(v, olderId);
        await assist.AskAsync($"Vultr invoices {a.Id} vs {b.Id}",
            "NEWER\n" + InvoiceText(a, v.AccountName) + "\nOLDER\n" + InvoiceText(b, v.AccountName),
            "Compare these two cloud invoices: total change and its causes (which products or resources were added, removed or grew), anything unusual, and whether the change looks expected. Use only the numbers given.");
    }
}
