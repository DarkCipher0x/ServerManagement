using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using DcxManager.Infra;
using DcxManager.Services;

namespace DcxManager.BeyondTrust;

/// <summary>
/// TLS for the BeyondTrust appliances (0.5.1 #2): the same trust-on-first-use pinning as Proxmox and vCenter (ADR-007).
/// A certificate Windows trusts is accepted while nothing is pinned; a self-signed one is refused until you compare and
/// trust it (Settings → Integrations → BeyondTrust → TLS, admin-protected); once pinned, any other certificate is refused.
/// "pra" is the PRA / Remote Support site, "ps" the Password Safe host.
/// </summary>
public static class BtTls
{
    /// <summary>What each appliance showed last (SHA-256 hex, decision), for the TLS card's "Trust certificate".</summary>
    public static readonly ConcurrentDictionary<string, (string Sha256, TlsDecision Decision)> Seen = new();

    public static string PinOf(string kind) => kind == "pra" ? AppSettings.Current.BeyondTrustPraPin : AppSettings.Current.BeyondTrustPsPin;

    public static HttpMessageHandler Handler(string kind, bool cookies)
    {
        var h = new SocketsHttpHandler
        {
            UseCookies = cookies,
            SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = TlsPin.Callback(() => PinOf(kind), (sha, d) => Seen[kind] = (sha, d)) },
        };
        if (cookies) h.CookieContainer = new CookieContainer();
        return Offline.Handler(h);
    }

    /// <summary>Why the last connection was refused at TLS, or null (the error text names the fingerprint to compare).</summary>
    public static string? Refusal(string kind) =>
        Seen.TryGetValue(kind, out var s) && s.Decision != TlsDecision.Accept ? (kind == "pra" ? "BeyondTrust PRA: " : "Password Safe: ") + TlsPin.UntrustedMessage(s.Decision, s.Sha256) : null;
}
