namespace DcxManager.Services;

/// <summary>
/// A VPN tunnel the app can bring up (0.3.0 §24, DarkCipher0x: "use VPNs in the app and route traffic down it for certain
/// routes — WireGuard and OpenVPN"). The configuration is kept here with its secrets removed: the WireGuard private key /
/// pre-shared keys and the OpenVPN private key / user password live in the vault as credentials and are joined back in
/// memory only when the tunnel starts. Routes decide what goes through the tunnel (split tunnel by default). A saved host
/// can name the profile it needs; the app brings it up before connecting.
/// </summary>
public sealed class VpnProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    /// <summary>"wireguard" or "openvpn".</summary>
    public string Kind { get; set; } = "wireguard";
    /// <summary>The configuration text with secrets removed (WireGuard: no PrivateKey / PresharedKey lines; OpenVPN: no &lt;key&gt; block).</summary>
    public string Config { get; set; } = "";
    /// <summary>Vault credential holding the WireGuard private key or the OpenVPN private key (PEM).</summary>
    public string? KeyCredentialId { get; set; }
    /// <summary>WireGuard: vault credential whose secret holds the peers' pre-shared keys, one "publickey psk" per line.</summary>
    public string? PskCredentialId { get; set; }
    /// <summary>OpenVPN: a password credential for auth-user-pass (sent over the management channel, never written).</summary>
    public string? AuthCredentialId { get; set; }
    /// <summary>Routes (CIDRs) that go through the tunnel; empty with <see cref="RouteAll"/> false = only the tunnel's own network.</summary>
    public List<string> Routes { get; set; } = new();
    /// <summary>Everything through the tunnel (WireGuard 0.0.0.0/0, OpenVPN redirect-gateway). Off = split tunnel.</summary>
    public bool RouteAll { get; set; }
    /// <summary>DNS servers to use while the tunnel is up (empty = leave as is).</summary>
    public List<string> Dns { get; set; } = new();
    /// <summary>Bring it up when the app starts.</summary>
    public bool AutoStart { get; set; }
    /// <summary>Bring it down again when no host that needs it has been used for this long (0 = stay up).</summary>
    public int IdleMinutes { get; set; } = 0;
    public DateTime? LastUp { get; set; }
    public string LastState { get; set; } = "";

    public string KindTitle => Kind == "openvpn" ? "OpenVPN" : "WireGuard";
    public string RoutesText => RouteAll ? "everything" : Routes.Count == 0 ? "the tunnel's network only" : string.Join(", ", Routes);
}
