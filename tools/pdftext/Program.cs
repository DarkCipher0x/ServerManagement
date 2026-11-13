// The encoding gate's PDF reader (tools\release-check.ps1, ADR-123): extracts the text of each PDF given and looks for
// mojibake (UTF-8 read as Windows-1252: U+00C2, U+00E2 U+20AC, U+00C3 followed by U+0080..U+00BF).
// Output, one line each: "OK <file> pages=<n> chars=<m>" per PDF, "HIT <file> page=<p> <context>" per hit (non-ASCII
// written as \uXXXX so no console code page can mangle it). Exit code: 0 clean, 3 hits, 2 a PDF couldn't be read.
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;

var mojibake = new Regex("\u00C2|\u00E2\u20AC|\u00C3[\u0080-\u00BF]", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(10));
var hits = 0;
foreach (var path in args)
{
    var name = Path.GetFileName(path);
    try
    {
        using var doc = PdfDocument.Open(path);
        long chars = 0;
        var pages = 0;
        foreach (var page in doc.GetPages())
        {
            pages++;
            var text = page.Text;
            chars += text.Length;
            foreach (Match m in mojibake.Matches(text))
            {
                hits++;
                var start = Math.Max(0, m.Index - 30);
                Console.WriteLine($"HIT {name} page={page.Number} {Ascii(text.Substring(start, Math.Min(70, text.Length - start)))}");
            }
        }
        Console.WriteLine($"OK {name} pages={pages} chars={chars}");
    }
    catch (Exception e) when (e is IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or UglyToad.PdfPig.Core.PdfDocumentFormatException)
    {
        Console.WriteLine($"ERROR {name} {e.GetType().Name}: {e.Message}");
        return 2;
    }
}
return hits > 0 ? 3 : 0;

static string Ascii(string s)
{
    var sb = new StringBuilder(s.Length);
    foreach (var c in s)
    {
        if (c < ' ') sb.Append(' ');
        else if (c > '~') sb.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:X4}");
        else sb.Append(c);
    }
    return sb.ToString();
}
