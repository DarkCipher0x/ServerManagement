using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace DcxManager.Documents;

/// <summary>What happened when the app tried to read a PDF's text. Displayed honestly; nothing is ever made up.</summary>
public enum PdfTextState
{
    /// <summary>Every page (within the limits) had machine-readable text.</summary>
    Extracted,
    /// <summary>Some pages had text, others none (scanned pages, images) — or the page/size limit was reached.</summary>
    Partial,
    /// <summary>No machine-readable text at all: a scanned/image-only PDF. OCR would be required.</summary>
    NoText,
    /// <summary>Password-protected; the app does not try to break or bypass it.</summary>
    Encrypted,
    /// <summary>The file could not be parsed (damaged, unsupported, timed out).</summary>
    Failed,
}

public sealed record PdfExtraction(PdfTextState State, int Pages, int PagesRead, IReadOnlyList<string> PageTexts, string Message)
{
    public int PagesWithText => PageTexts.Count(t => t.Trim().Length > 0);
    public int Chars => PageTexts.Sum(t => t.Length);
    public bool HasText => PagesWithText > 0;

    /// <summary>Text of a page range (1-based, inclusive), with page markers so the model can cite pages.</summary>
    public string Text(int from = 1, int to = int.MaxValue)
    {
        var sb = new StringBuilder();
        for (int i = Math.Max(1, from); i <= Math.Min(to, PageTexts.Count); i++)
        {
            var t = PageTexts[i - 1].Trim();
            sb.Append($"--- page {i} ---\n").Append(t.Length > 0 ? t : "(no machine-readable text on this page)").Append("\n\n");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>The status line the viewer shows: PDF DISPLAYED / PDF TEXT EXTRACTED / … (never implies more than happened).</summary>
    public string Label => State switch
    {
        PdfTextState.Extracted => $"PDF TEXT EXTRACTED · {PagesWithText} of {Pages} page(s) · {Chars:N0} characters",
        PdfTextState.Partial => $"PDF TEXT PARTLY EXTRACTED · {PagesWithText} of {Pages} page(s) have text · {Chars:N0} characters" + (PagesRead < Pages ? $" · first {PagesRead} pages read" : ""),
        PdfTextState.NoText => $"NO MACHINE-READABLE TEXT · {Pages} page(s) — scanned or image-only; OCR would be required",
        PdfTextState.Encrypted => "PASSWORD-PROTECTED PDF · text not extracted",
        _ => "TEXT EXTRACTION FAILED · " + Message,
    };
}

/// <summary>
/// Real PDF text extraction with PdfPig (Apache-2.0, managed code — no native parser). PDFs come from servers and the
/// internet, so they are untrusted: parsing runs off the UI thread with a time limit, a size limit and page/character
/// caps, and any failure becomes a <see cref="PdfTextState.Failed"/> result instead of an exception.
/// </summary>
public static class PdfText
{
    public const long MaxBytes = 100L << 20;
    public const int MaxPages = 500;
    public const int MaxChars = 2_000_000;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public static async Task<PdfExtraction> ExtractAsync(byte[] pdf, CancellationToken ct = default)
    {
        if (pdf.Length > MaxBytes) return new(PdfTextState.Failed, 0, 0, Array.Empty<string>(), $"the file is larger than {MaxBytes >> 20} MB");
        if (pdf.Length < 8 || Encoding.ASCII.GetString(pdf, 0, Math.Min(1024, pdf.Length)).IndexOf("%PDF-", StringComparison.Ordinal) < 0)
            return new(PdfTextState.Failed, 0, 0, Array.Empty<string>(), "this is not a PDF file");
        try { return await Task.Run(() => Extract(pdf, ct), ct).WaitAsync(Timeout, ct); }
        catch (TimeoutException) { return new(PdfTextState.Failed, 0, 0, Array.Empty<string>(), $"parsing took longer than {Timeout.TotalSeconds:0} s"); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new(PdfTextState.Failed, 0, 0, Array.Empty<string>(), ex.Message); }
    }

    private static PdfExtraction Extract(byte[] pdf, CancellationToken ct)
    {
        try
        {
            using var doc = PdfDocument.Open(pdf, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
            int pages = doc.NumberOfPages, read = 0, chars = 0;
            var texts = new List<string>();
            foreach (var page in doc.GetPages())
            {
                ct.ThrowIfCancellationRequested();
                if (read >= MaxPages || chars >= MaxChars) break;
                string t;
                try { t = ContentOrderTextExtractor.GetText(page, true); }
                catch { try { t = page.Text; } catch { t = ""; } }
                if (chars + t.Length > MaxChars) t = t[..Math.Max(0, MaxChars - chars)];
                texts.Add(t);
                chars += t.Length;
                read++;
            }
            int withText = texts.Count(x => x.Trim().Length > 0);
            var state = withText == 0 ? PdfTextState.NoText : withText < texts.Count || read < pages ? PdfTextState.Partial : PdfTextState.Extracted;
            var msg = state == PdfTextState.NoText ? "No machine-readable text: this looks like a scanned or image-only PDF. OCR would be required; the app does not include OCR." : "";
            return new(state, pages, read, texts, msg);
        }
        catch (UglyToad.PdfPig.Exceptions.PdfDocumentEncryptedException)
        {
            return new(PdfTextState.Encrypted, 0, 0, Array.Empty<string>(), "The PDF is password-protected. Open it in its own app with the password if you need its text.");
        }
    }
}
