using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

// The namespace stays DcxManager.Infra (where the providers first used it, ADR-007); the code moved to Core in 0.5.1 so
// the integrations below the providers (BeyondTrust) pin the same way.
namespace DcxManager.Infra;

public enum TlsDecision { Accept, NeedsTrust, Mismatch }

/// <summary>
/// Trust-on-first-use pinning shared by the providers and integrations (ADR-007). A certificate that chains to a trusted
/// root is accepted while nothing is pinned; a self-signed one needs the user to compare the SHA-256 and trust it; once
/// pinned, any other certificate is refused (possible interception), whatever its chain says.
/// </summary>
public static class TlsPin
{
    public static TlsDecision Check(string pinned, string seen, SslPolicyErrors errors) =>
        pinned.Length > 0 ? (string.Equals(pinned, seen, StringComparison.OrdinalIgnoreCase) ? TlsDecision.Accept : TlsDecision.Mismatch)
        : errors == SslPolicyErrors.None ? TlsDecision.Accept : TlsDecision.NeedsTrust;

    public static string Sha256(X509Certificate cert) => Convert.ToHexString(SHA256.HashData(cert.GetRawCertData()));

    public static string Pretty(string hex) => string.Join(":", Enumerable.Range(0, hex.Length / 2).Select(i => hex.Substring(i * 2, 2)));

    /// <summary>The validation callback that applies the pin and reports what it saw (for handlers that need other options).</summary>
    public static RemoteCertificateValidationCallback Callback(Func<string> pinned, Action<string, TlsDecision> seen) => (_, cert, _, errors) =>
    {
        if (cert == null) return false;
        var sha = Sha256(cert);
        var d = Check(pinned(), sha, errors);
        seen(sha, d);
        return d == TlsDecision.Accept;
    };

    /// <summary>A handler that applies the pin and reports what it saw, on the offline-mode guarded stack (ADR-016).</summary>
    public static HttpMessageHandler Handler(Func<string> pinned, Action<string, TlsDecision> seen) => DcxManager.Services.Offline.Handler(new SocketsHttpHandler
    {
        UseCookies = false,
        SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = Callback(pinned, seen) },
    });

    public static string UntrustedMessage(TlsDecision d, string sha) => d == TlsDecision.Mismatch
        ? $"⚠ CERTIFICATE CHANGED: the server now presents SHA-256 {Pretty(sha)}, not the one you trusted. This can mean interception. Check it on the server before trusting the new one."
        : $"certificate not trusted yet. SHA-256 {Pretty(sha)} — compare it with the certificate on the server, then click “Trust certificate”.";
}
