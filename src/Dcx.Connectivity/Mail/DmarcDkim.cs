using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace DcxManager.Mail;

/// <summary>
/// DMARC (RFC 7489) and DKIM (RFC 6376) record checks, like MxToolbox's: every tag parsed and explained, the policy and its
/// weak spots; for DKIM the key type and its real strength (the RSA modulus is read from the published key), testing mode,
/// revoked keys — and, when no selector is known, a probe of the selectors common providers use.
/// </summary>
public static class DmarcDkim
{
    public sealed record Tags(string Record, Dictionary<string, string> Values, List<MailFinding> Findings);

    /// <summary>"v=DMARC1; p=reject; rua=…" → tag → value (keys lower-case, values trimmed).</summary>
    public static Dictionary<string, string> ParseTags(string record) =>
        record.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
              .Select(p => p.Split('=', 2))
              .Where(p => p.Length == 2)
              .GroupBy(p => p[0].Trim().ToLowerInvariant())
              .ToDictionary(g => g.Key, g => g.First()[1].Trim());

    // ---- DMARC --------------------------------------------------------------------------------------------------------

    public static async Task<Tags?> DmarcAsync(string domain, IMailDns? dns = null)
    {
        dns ??= SystemMailDns.Instance;
        domain = MailNet.CheckDomain(domain);
        var all = (await dns.TxtAsync("_dmarc." + domain)).Where(t => t.TrimStart().StartsWith("v=DMARC1", StringComparison.OrdinalIgnoreCase)).ToList();
        if (all.Count == 0) return null;
        var r = Dmarc(all[0]);
        if (all.Count > 1) r.Findings.Insert(0, new("bad", "DMARC", $"{all.Count} DMARC records — receivers ignore them all", "keep exactly one"));
        return r;
    }

    public static Tags Dmarc(string record)
    {
        var t = ParseTags(record);
        var f = new List<MailFinding>();
        var p = t.GetValueOrDefault("p", "").ToLowerInvariant();
        f.Add(p switch
        {
            "reject" => new("good", "DMARC policy", "p=reject — mail failing SPF and DKIM alignment is refused"),
            "quarantine" => new("good", "DMARC policy", "p=quarantine — failing mail goes to spam", "p=reject once reports are clean"),
            "none" => new("warn", "DMARC policy", "p=none — monitoring only, nothing is blocked", "move to quarantine, then reject"),
            _ => new("bad", "DMARC policy", "no valid p= tag — the record is ignored", "add p=none to start monitoring"),
        });
        if (t.TryGetValue("sp", out var sp)) f.Add(new(sp.Equals("none", StringComparison.OrdinalIgnoreCase) && p != "none" ? "warn" : "info", "DMARC subdomains", $"sp={sp}"));
        if (t.TryGetValue("pct", out var pct) && int.TryParse(pct, out var n) && n < 100) f.Add(new("warn", "DMARC", $"pct={n} — the policy applies to only {n}% of failing mail", "raise to 100"));
        if (!t.ContainsKey("rua")) f.Add(new("warn", "DMARC reports", "no rua= — you get no aggregate reports", "add rua=mailto:dmarc@your-domain"));
        else f.Add(new("good", "DMARC reports", "aggregate reports to " + t["rua"]));
        if (t.TryGetValue("ruf", out var ruf)) f.Add(new("info", "DMARC reports", "forensic reports to " + ruf + " (few receivers send them)"));
        f.Add(new("info", "DMARC alignment", $"DKIM {(t.GetValueOrDefault("adkim", "r") == "s" ? "strict" : "relaxed")}, SPF {(t.GetValueOrDefault("aspf", "r") == "s" ? "strict" : "relaxed")}"));
        foreach (var k in t.Keys.Where(k => k is not ("v" or "p" or "sp" or "pct" or "rua" or "ruf" or "adkim" or "aspf" or "fo" or "rf" or "ri" or "np" or "psd" or "t")))
            f.Add(new("warn", "DMARC", $"unknown tag “{k}”"));
        return new Tags(record, t, f);
    }

    // ---- DKIM ---------------------------------------------------------------------------------------------------------

    /// <summary>Selectors well-known providers publish (Microsoft 365, Google, Fastmail, Mailchimp, SendGrid, Amazon SES…).</summary>
    public static readonly string[] CommonSelectors =
        ["default", "selector1", "selector2", "google", "k1", "k2", "k3", "s1", "s2", "fm1", "fm2", "fm3", "dkim", "mail", "smtp", "mx", "mandrill", "everlytickey1", "everlytickey2", "zoho", "protonmail", "protonmail2", "protonmail3", "mxvault", "sig1", "key1", "20230601", "20221208", "20210112"];

    public sealed record DkimKey(string Selector, string Record, string KeyType, int Bits, bool Testing, bool Revoked, List<MailFinding> Findings);

    public static async Task<DkimKey?> DkimAsync(string domain, string selector, IMailDns? dns = null)
    {
        dns ??= SystemMailDns.Instance;
        domain = MailNet.CheckDomain(domain);
        if (!Regex.IsMatch(selector, @"^[A-Za-z0-9](?:[A-Za-z0-9._-]{0,62})$")) throw new ArgumentException("A selector is letters, digits, dots, dashes and underscores.");
        var rec = (await dns.TxtAsync($"{selector}._domainkey.{domain}")).FirstOrDefault(r => r.Contains("p=", StringComparison.OrdinalIgnoreCase));
        return rec == null ? null : Dkim(selector, rec);
    }

    /// <summary>Tries the common selectors (one TXT lookup each) and returns the keys found.</summary>
    public static async Task<List<DkimKey>> ProbeAsync(string domain, IMailDns? dns = null, CancellationToken ct = default)
    {
        var found = new List<DkimKey>();
        foreach (var s in CommonSelectors)
        {
            ct.ThrowIfCancellationRequested();
            if (await DkimAsync(domain, s, dns) is { } k) found.Add(k);
        }
        return found;
    }

    public static DkimKey Dkim(string selector, string record)
    {
        var t = ParseTags(record);
        var f = new List<MailFinding>();
        var k = t.GetValueOrDefault("k", "rsa").ToLowerInvariant();
        var p = Regex.Replace(t.GetValueOrDefault("p", ""), @"\s", "");
        var testing = t.GetValueOrDefault("t", "").Split(':').Contains("y");
        int bits = 0;
        if (p.Length == 0) f.Add(new("warn", "DKIM", $"{selector}: the key is empty — revoked"));
        else if (k == "ed25519") { bits = 256; f.Add(new("good", "DKIM", $"{selector}: Ed25519 key (many receivers still need an RSA key alongside)")); }
        else
        {
            try
            {
                using var rsa = RSA.Create();
                rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(p), out _);
                bits = rsa.KeySize;
                f.Add(bits >= 2048 ? new("good", "DKIM", $"{selector}: RSA {bits}-bit key")
                    : bits >= 1024 ? new("warn", "DKIM", $"{selector}: RSA {bits}-bit key — weak by today's standard", "rotate to a 2048-bit key")
                    : new("bad", "DKIM", $"{selector}: RSA {bits}-bit key — breakable", "rotate to a 2048-bit key now"));
            }
            catch (Exception ex) when (ex is FormatException or CryptographicException) { f.Add(new("bad", "DKIM", $"{selector}: the p= key isn't a valid public key")); }
        }
        if (testing) f.Add(new("warn", "DKIM", $"{selector}: t=y — testing mode, receivers may ignore failures", "remove t=y once signing works"));
        if (t.TryGetValue("h", out var h) && h.Contains("sha1", StringComparison.OrdinalIgnoreCase) && !h.Contains("sha256", StringComparison.OrdinalIgnoreCase))
            f.Add(new("bad", "DKIM", $"{selector}: h={h} — SHA-1 only", "allow sha256"));
        return new DkimKey(selector, record, k, bits, testing, p.Length == 0, f);
    }
}
