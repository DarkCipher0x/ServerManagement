namespace DcxManager.Browser;

// Stored records (0.2.0 phase 6): kept in the encrypted store, so they live in Dcx.Core with the store; the code that uses them stays here.

/// <summary>A saved portal (ADR-032): global, or a host's web UI when <see cref="HostId"/> is set. Stored in data.dcx.</summary>
public sealed class PortalBookmark
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    /// <summary>Folder path in the Portals list ("Network/Core"); ignored for host web UIs, which list under their host.</summary>
    public string Folder { get; set; } = "";
    /// <summary>Set: this is that host's web UI ("Open web UI" in the host's menu).</summary>
    public string? HostId { get; set; }
    /// <summary>PRODUCTION | TEST | … | "" = the host's environment (or none). Picks the browser profile and the tab label.</summary>
    public string Environment { get; set; } = "";
    /// <summary>A named profile that overrides the environment's ("" = by environment).</summary>
    public string Profile { get; set; } = "";
    /// <summary>A vault password credential for "Fill sign-in" (never filled automatically); null = the host's, if a password.</summary>
    public string? CredentialId { get; set; }
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime? LastOpened { get; set; }
}

/// <summary>A self-signed (or otherwise untrusted) certificate the user compared and trusted for one host:port (TOFU, like
/// <see cref="TlsPin"/>). Any other untrusted certificate on that host:port is refused as a possible interception.</summary>
public sealed class WebCertPin
{
    public string HostPort { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string Subject { get; set; } = "";
    public DateTime Trusted { get; set; } = DateTime.Now;
}
