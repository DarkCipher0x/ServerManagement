namespace DcxManager.Infra;

// Stored records (0.2.0 phase 6): kept in the encrypted store, so they live in Dcx.Core with the store; the code that uses them stays here.

/// <summary>A saved infrastructure connection (stored encrypted in data.dcx). Secrets live in the vault and are
/// referenced by <see cref="CredentialId"/>; nothing secret is stored here.</summary>
public sealed class ProviderConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>"docker-ssh", "docker-local", "proxmox", "unraid", "vsphere" or "hyperv".</summary>
    public string Kind { get; set; } = "docker-ssh";
    public string Name { get; set; } = "";
    /// <summary>docker-ssh: the saved host to run docker on.</summary>
    public string? HostId { get; set; }
    /// <summary>proxmox: https://pve.lan:8006 · docker-local: pipe name (default docker_engine) · vsphere: https://vcenter.lan ·
    /// hyperv: the Hyper-V host's computer name ("" or localhost = this PC).</summary>
    public string Endpoint { get; set; } = "";
    /// <summary>proxmox: vault credential — Username = token id (user@realm!token), Secret = token secret.
    /// unraid: vault credential whose Secret is the API key.</summary>
    public string? CredentialId { get; set; }
    /// <summary>Read-only unless the user turns this on (separate trust decision, per provider).</summary>
    public bool AllowChanges { get; set; }
    /// <summary>Shown in every confirmation: PRODUCTION / TEST / LAB / DEVELOPMENT / LOCAL (or the user's own word).</summary>
    public string Environment { get; set; } = "LAB";
    /// <summary>proxmox: SHA-256 of the server certificate the user trusted (TOFU pinning for self-signed certs).</summary>
    public string TlsSha256 { get; set; } = "";
    /// <summary>Auto-refresh while the Infrastructure screen is open: 0 = off (default; nothing polls by itself).</summary>
    public int RefreshSeconds { get; set; }
    /// <summary>unifi (0.6.16): the console's id for the UniFi Cloud Connector (Endpoint = https://api.ui.com); empty = the
    /// console is reached directly on its own address.</summary>
    public string ConsoleId { get; set; } = "";
}
