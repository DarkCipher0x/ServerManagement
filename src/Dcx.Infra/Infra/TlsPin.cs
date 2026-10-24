namespace DcxManager.Infra;

/// <summary>A provider that pins a server certificate on first use (Proxmox, Unraid). The Infrastructure screen offers
/// "Trust certificate…" when <see cref="LastTls"/> isn't Accept. The pinning itself (<see cref="TlsPin"/>) is in Core
/// since 0.5.1, shared with the integrations.</summary>
public interface ITlsPinned
{
    string LastSeenSha256 { get; }
    TlsDecision LastTls { get; }
}
