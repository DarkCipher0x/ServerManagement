namespace DcxManager.Api;

// Stored records (0.2.0 phase 6): kept in the encrypted store, so they live in Dcx.Core with the store; the code that uses them stays here.

/// <summary>A capability token for the local API (ADR-011). Only the SHA-256 of the secret is stored.</summary>
public sealed class ApiToken
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    /// <summary>Hex SHA-256 of the full token string.</summary>
    public string Hash { get; set; } = "";
    /// <summary>The first characters, shown so the user can tell tokens apart ("dcx_Ab3x…").</summary>
    public string Prefix { get; set; } = "";
    public List<string> Scopes { get; set; } = new();
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime? Expires { get; set; }
    public DateTime? LastUsed { get; set; }
    public long Uses { get; set; }
    public DateTime? Revoked { get; set; }

    public bool Valid => Revoked == null && (Expires == null || Expires > DateTime.Now);
    public string StateText => Revoked != null ? $"revoked {Revoked:yyyy-MM-dd}" : Expires is { } e && e <= DateTime.Now ? "expired" : Expires is { } e2 ? $"expires {e2:yyyy-MM-dd HH:mm}" : "never expires ⚠";
}
