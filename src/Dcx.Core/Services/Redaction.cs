using System.Text.RegularExpressions;

namespace DcxManager.Services;

/// <summary>
/// Secret masking for anything that leaves the app's own screens: text sent to a model, audit details, broadcast text,
/// add-on call logs. Moved here from <c>AiClient</c> in 0.2.0 phase 2 because it is privacy, not AI (<c>AiClient.Redact</c>
/// forwards). Masking fails CLOSED: text that can't be scanned within the budget is withheld, never passed on.
/// </summary>
public static class Redaction
{
    private static readonly Regex SecretPath = new(
        @"(^|[/\\])(\.ssh[/\\]|\.gnupg[/\\]|\.aws[/\\]|\.azure[/\\]|\.kube[/\\]config|\.docker[/\\]config\.json|id_(rsa|dsa|ecdsa|ed25519)(\.|$)|shadow-?$|gshadow-?$|\.env(\.|$)|\.netrc$|\.pgpass$|credentials(\.json)?$|.*\.(pem|key|pfx|p12|kdbx|ppk|jks|keystore)$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Paths that look like keys, credentials or password files: the operator never reads them, transfers flag
    /// them, the AI is never given them.</summary>
    public static bool LooksSecretPath(string path) => SecretPath.IsMatch(path.Trim());

    /// <summary>Per-pattern time limit.</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(500);
    public const string Unscannable = "[REDACTED — this text could not be checked for secrets in time, so it was withheld]";

    private static readonly (Regex Re, string With)[] Secrets =
    {
        // Linear-time engine: thousands of BEGIN lines without an END must not rescan the rest of the text each time (fuzzing, s23).
        (new Regex(@"-----BEGIN [A-Z0-9 ]{0,40}PRIVATE KEY-----[\s\S]*?-----END [A-Z0-9 ]{0,40}PRIVATE KEY-----", RegexOptions.NonBacktracking, Budget), "[REDACTED PRIVATE KEY]"),
        // JSON: "default_password": "…", "api_key":"…", "client_secret" : "…" (quoted keys, as in API responses and configs)
        (new Regex(@"(?i)(""[A-Za-z0-9_\-]{0,40}(password|passwd|passphrase|secret|token|api[_-]?key|apikey|access[_-]?key|private[_-]?key)[A-Za-z0-9_\-]{0,40}""\s*:\s*)""(?:[^""\\]|\\.)*""", RegexOptions.Compiled, Budget), "$1\"[REDACTED]\""),
        // key = value / key: value, including prefixed names (db_password, AWS_SECRET, csrf_token, x-api-key)
        (new Regex(@"(?i)(?<![A-Za-z0-9])([A-Za-z0-9_\-]{0,40}?(?:password|passwd|pwd|passphrase|secret|token|api[_-]?key|access[_-]?key|client[_-]?secret))(\s*[:=]\s*)(""[^""]*""|'[^']*'|\S+)", RegexOptions.Compiled, Budget), "$1$2[REDACTED]"),
        (new Regex(@"(?i)\b(bearer|basic)\s+[A-Za-z0-9\-._~+/]{12,}=*", RegexOptions.Compiled, Budget), "$1 [REDACTED]"),
        (new Regex(@"(://[^\s:/@]+:)[^\s@/]+@", RegexOptions.Compiled, Budget), "$1[REDACTED]@"),
        (new Regex(@"\b(AKIA|ASIA)[0-9A-Z]{16}\b", RegexOptions.Compiled, Budget), "[REDACTED AWS KEY]"),
        (new Regex(@"\b(ghp|gho|ghs|github_pat|glpat|xox[abpr])[-_][A-Za-z0-9_\-]{16,}", RegexOptions.Compiled, Budget), "[REDACTED TOKEN]"),
        (new Regex(@"\bsk-[A-Za-z0-9_\-]{20,}", RegexOptions.Compiled, Budget), "[REDACTED KEY]"),
        (new Regex(@"\beyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}", RegexOptions.Compiled, Budget), "[REDACTED JWT]"),
    };

    /// <summary>Masks secrets in <paramref name="s"/>; returns <see cref="Unscannable"/> when a pattern runs out of time.</summary>
    public static string Mask(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        try { foreach (var (re, with) in Secrets) s = re.Replace(s, with); }
        catch (RegexMatchTimeoutException) { return Unscannable; }
        return s;
    }

    /// <summary>Mask and report how many secrets were masked (for the "data being sent" preview).</summary>
    public static (string Text, int Masked) MaskCounted(string s)
    {
        if (string.IsNullOrEmpty(s)) return (s, 0);
        int n = 0;
        try { foreach (var (re, with) in Secrets) s = re.Replace(s, m => { n++; return m.Result(with); }); }
        catch (RegexMatchTimeoutException) { return (Unscannable, n + 1); }
        return (s, n);
    }
}
