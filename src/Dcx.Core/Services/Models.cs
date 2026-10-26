using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace DcxManager.Services;

public enum AuthMethod { Password, PrivateKey, KeyboardInteractive }
public enum CredentialKind { Password, PrivateKey }
public enum TunnelKind { Local, Remote, Dynamic }

/// <summary>A saved server (LAN box, VPS, Vultr instance...). Secrets live in the referenced <see cref="Credential"/>.</summary>
public sealed class HostEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Group { get; set; } = "General";
    public string Address { get; set; } = "";
    public int Port { get; set; } = 22;
    public string Username { get; set; } = "root";
    public AuthMethod Auth { get; set; } = AuthMethod.PrivateKey;
    public string? CredentialId { get; set; }
    /// <summary>Password used for sudo when it differs from the login password (optional).</summary>
    public string? SudoCredentialId { get; set; }
    /// <summary>Connect through this saved host first (bastion / jump host).</summary>
    public string? JumpHostId { get; set; }
    public string Tags { get; set; } = "";
    public string Notes { get; set; } = "";
    /// <summary>Set when the host was imported from Vultr: the instance id.</summary>
    public string? VultrInstanceId { get; set; }
    public string? VultrAccountId { get; set; }
    /// <summary>Provider-neutral link to a cloud instance (ADR-020): "contabo" (Vultr hosts may still use the fields above).</summary>
    public string? CloudProvider { get; set; }
    public string? CloudInstanceId { get; set; }
    public string? CloudAccountId { get; set; }
    public bool Favorite { get; set; }
    /// <summary>SSH keepalive interval (0 = off). Keeps NAT/firewall state alive and detects dead links on long sessions.</summary>
    public int KeepAliveSeconds { get; set; } = 30;
    public DateTime? LastConnected { get; set; }
    /// <summary>"ssh" (terminal, files, manage), "rdp" (Windows Remote Desktop via mstsc), "vnc" (Mac and Linux desktops, 0.5.1) or "telnet".</summary>
    public string Protocol { get; set; } = "ssh";
    /// <summary>SSH: sign in with the Windows account's Kerberos ticket through Windows OpenSSH (0.5.0, ADR-062).</summary>
    public bool KerberosSignOn { get; set; }
    /// <summary>A BeyondTrust Password Safe managed account ("systemId:accountId|System\Account"; ids and a label only):
    /// connecting checks its password out into memory for the session (0.5.0, plan #16).</summary>
    public string PasswordSafeAccount { get; set; } = "";
    /// <summary>A Bitwarden vault item or Secrets Manager secret ("pm:&lt;id&gt;|label" / "sm:&lt;id&gt;|label"; an id and a
    /// label only): connecting reads its password or SSH key into memory for the session (0.6.14, ADR-093).</summary>
    public string BitwardenItem { get; set; } = "";
    /// <summary>Files: "auto" (SFTP, SCP if the server has no SFTP subsystem), "sftp" or "scp" (ADR-014).</summary>
    public string FileProtocol { get; set; } = "auto";
    public RdpSettings Rdp { get; set; } = new();
    /// <summary>VNC options (Protocol "vnc", 0.5.1 #19).</summary>
    public VncSettings Vnc { get; set; } = new();
    /// <summary>Organisational category: "" (auto: windows for RDP, linux otherwise) | linux | windows | network | other.</summary>
    public string Platform { get; set; } = "";
    /// <summary>PRODUCTION | TEST | LAB | DEVELOPMENT | LOCAL | "" (not set) — shown everywhere the host is; PRODUCTION asks
    /// for the host's name before disruptive actions (Services/Environments.cs, ADR-017).</summary>
    public string Environment { get; set; } = "";
    /// <summary>Terminal profile for this host's tabs (colours, cursor, encoding, keys, logging); null = the default (ADR-019).</summary>
    public string? TerminalProfileId { get; set; }
    /// <summary>"Connect through": the VPN profile brought up before connecting (0.3.0 §24); null = none.</summary>
    public string? VpnProfileId { get; set; }
    /// <summary>Always-on tmux for this host's SSH terminals (0.6.29): "" = Settings → Terminal → tmux decides, "on" or "off".</summary>
    public string Tmux { get; set; } = "";
    /// <summary>The fields that follow the host's folder defaults (0.6.29, ADR-105): <see cref="FolderDefaults.Fields"/>. A field
    /// not listed is the host's own; one listed is rewritten whenever its folder's default changes.</summary>
    public List<string> Inherits { get; set; } = new();

    /// <summary>Files, Manage and Commander need an SSH host (not RDP or Telnet).</summary>
    [JsonIgnore] public bool IsSsh => Protocol is "ssh" or "";
    [JsonIgnore] public bool IsTelnet => Protocol == "telnet";
    [JsonIgnore] public bool IsVnc => Protocol == "vnc";

    public static readonly string[] Platforms = { "linux", "windows", "macos", "network", "other" };
    public static string PlatformLabel(string p) => p switch { "linux" => "Linux", "windows" => "Windows", "macos" => "macOS", "network" => "Network device", "other" => "Other", _ => "Auto" };
    [JsonIgnore] public string EffectivePlatform => Platform.Length > 0 ? Platform : Protocol == "rdp" ? "windows" : Protocol == "vnc" && Vnc.Kind == "mac" ? "macos" : "linux";

    /// <summary>Ad-hoc connections carry their secret here instead of in the vault; never saved.</summary>
    [JsonIgnore] public Credential? Transient { get; set; }
    [JsonIgnore] public bool IsAdHoc { get; set; }

    /// <summary>"Library / Collection" when this host comes from a shared library (ADR-033); never saved.</summary>
    [JsonIgnore] public string? SharedFrom { get; set; }
    [JsonIgnore] public bool IsShared => SharedFrom != null;
    [JsonIgnore] public string Display => (SharedFrom != null ? "⇄ " : "") + (string.IsNullOrWhiteSpace(Name) ? $"{Username}@{Address}" : Name);
    [JsonIgnore] public string Endpoint => Port == 22 ? $"{Username}@{Address}" : $"{Username}@{Address}:{Port}";
    public HostEntry Clone() { var c = (HostEntry)MemberwiseClone(); c.Rdp = Rdp.Clone(); c.Vnc = Vnc.Clone(); c.Inherits = new(Inherits ?? new()); return c; }
}

/// <summary>VNC options of a host (0.5.1 #19, ADR-069). The password (or macOS account) is the host's vault credential.</summary>
public sealed class VncSettings
{
    /// <summary>"ssh" (default: through an SSH tunnel, encrypted) or "direct" (straight to the VNC port).</summary>
    public string Through { get; set; } = "ssh";
    /// <summary>The saved SSH host the tunnel goes through (often the same machine's SSH entry); null = the first saved SSH
    /// host with the same address.</summary>
    public string? SshHostId { get; set; }
    /// <summary>Where the VNC server listens, seen from the SSH host ("127.0.0.1" when it is that machine).</summary>
    public string TunnelTarget { get; set; } = "127.0.0.1";
    /// <summary>"mac" (Apple Screen Sharing: sign in with the macOS account) or "vnc" (a VNC password, or none).</summary>
    public string Kind { get; set; } = "vnc";
    public bool ViewOnly { get; set; }
    /// <summary>Scale the desktop to the tab (true) or show it 1:1 with scroll bars (false).</summary>
    public bool Scale { get; set; } = true;
    /// <summary>Share the desktop with other viewers (false asks the server to disconnect them).</summary>
    public bool Shared { get; set; } = true;
    /// <summary>Copy the remote clipboard to Windows when it changes (off by default: a server could fill it).</summary>
    public bool ClipboardFromServer { get; set; }
    /// <summary>The pinned SHA-256 of a VeNCrypt server's TLS certificate (hex), set on first trust.</summary>
    public string TlsSha256 { get; set; } = "";

    public VncSettings Clone() => (VncSettings)MemberwiseClone();
}

/// <summary>Remote Desktop options written into a generated .rdp file (no secrets in it).</summary>
public sealed class RdpSettings
{
    public string Domain { get; set; } = "";
    public bool FullScreen { get; set; } = true;
    public int Width { get; set; } = 1600;
    public int Height { get; set; } = 900;
    public bool MultiMonitor { get; set; }
    public bool Admin { get; set; }
    public bool Clipboard { get; set; } = true;
    public bool Drives { get; set; }
    public bool Printers { get; set; }
    public bool Microphone { get; set; }
    /// <summary>0 = play on this PC, 1 = on the server, 2 = off.</summary>
    public int Audio { get; set; }
    public string Gateway { get; set; } = "";
    /// <summary>"direct" (default) or "ssh" (0.6.29): through an SSH tunnel to a saved SSH host (a bastion, or the Windows
    /// server's own OpenSSH); it opens on a random 127.0.0.1 port and closes with the tab. No RD Gateway needed.</summary>
    public string Through { get; set; } = "direct";
    /// <summary>The saved SSH host the tunnel goes through; null = a saved SSH host with the same address.</summary>
    public string? SshHostId { get; set; }
    /// <summary>The Remote Desktop server as that SSH host sees it; empty = this host's address.</summary>
    public string TunnelTarget { get; set; } = "";
    /// <summary>Client side of "keepalive": reconnect automatically when the link drops.</summary>
    public bool AutoReconnect { get; set; } = true;
    public int ReconnectRetries { get; set; } = 20;
    /// <summary>Embedded display: "resize" (follow the pane), "scale" (smart sizing) or "fixed" (Width × Height).</summary>
    public string Fit { get; set; } = "resize";
    /// <summary>Smart-card redirection (off by default).</summary>
    public bool SmartCards { get; set; }
    /// <summary>Windows key combinations: 0 = this PC, 1 = the remote computer, 2 = the remote only in full screen.</summary>
    public int KeyboardHook { get; set; } = 2;
    /// <summary>How the embedded client signs in (0.5.0, ADR-062): "password" (the vault or asked), "windows" (single
    /// sign-on with the Windows account through CredSSP), "rcg" (Remote Credential Guard: Kerberos, nothing delegated) or
    /// "restricted" (Restricted Admin: a network logon, nothing delegated; the account must be an admin on the server).</summary>
    public string SignOn { get; set; } = "password";
    public static readonly (string Id, string Title)[] SignOnChoices =
    {
        ("password", "Password (vault, or asked each time)"),
        ("windows", "Single sign-on with my Windows account"),
        ("rcg", "Remote Credential Guard (my Windows account, nothing left on the server)"),
        ("restricted", "Restricted Admin (my Windows account, network logon)"),
    };
    public RdpSettings Clone() => (RdpSettings)MemberwiseClone();
}

/// <summary>A password or private key in the vault.</summary>
public sealed class Credential
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public CredentialKind Kind { get; set; }
    public string Username { get; set; } = "";
    /// <summary>The password, or the private key text (OpenSSH / PEM / PuTTY .ppk).</summary>
    public string Secret { get; set; } = "";
    public string Passphrase { get; set; } = "";
    public string PublicKey { get; set; } = "";
    public string KeyType { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string Notes { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.Now;
    /// <summary>Folder path like a host's group ("Production/Databases"); empty = the top level (0.4.0).</summary>
    public string Folder { get; set; } = "";
    /// <summary>What it is for — one of <see cref="Categories"/> or the user's own word; empty = unsorted (0.4.0). The
    /// mechanics stay in <see cref="Kind"/> (a password or a key).</summary>
    public string Category { get; set; } = "";
    /// <summary>Where the secret really lives when it isn't stored here (0.6.17, ADR-096): "bw:pm:&lt;id&gt;|label" (a
    /// Bitwarden vault item), "bw:sm:&lt;id&gt;|label" (a Secrets Manager secret) or "ps:&lt;system&gt;:&lt;account&gt;|label" (a
    /// BeyondTrust Password Safe managed account). Ids and a label only: <see cref="Secret"/> stays empty and every use
    /// reads the secret into memory through <see cref="ExternalCredentials"/>. Empty = stored in this vault.</summary>
    public string External { get; set; } = "";
    /// <summary>The sign-in's authenticator setting (0.6.20): an otpauth:// link or a Base32 secret (<see cref="DcxManager.Services.Totp.Parse"/>);
    /// empty = none. A Bitwarden item's own TOTP is read with its password and never stored here.</summary>
    public string Totp { get; set; } = "";

    [JsonIgnore] public bool IsExternal => External.Length > 0;
    /// <summary>"Bitwarden", "Bitwarden Secrets Manager" or "Password Safe" for an external credential, else "".</summary>
    [JsonIgnore] public string ExternalText => External.StartsWith("bw:pm:") ? "Bitwarden" : External.StartsWith("bw:sm:") ? "Bitwarden Secrets Manager" : External.StartsWith("ps:") ? "Password Safe" : "";

    /// <summary>The usual types, offered in the editor (any other word is allowed).</summary>
    public static readonly string[] Categories = { "ssh", "sudo / root", "database", "api token", "web login", "vpn", "certificate", "service account", "other" };

    public Credential Clone() => (Credential)MemberwiseClone();

    /// <summary>The name with its padlock when locked (0.5.1, ADR-067).</summary>
    [JsonIgnore] public string ListName => Locks.Glyph("cred:" + Id) is { Length: > 0 } g ? g + " " + Name : Name;
    [JsonIgnore] public string KindText => Kind == CredentialKind.Password ? "password" : $"key {KeyType}".Trim();
    /// <summary>The type shown: the category, else "ssh" for a key (what keys are for) and "" for an unsorted password.</summary>
    [JsonIgnore] public string CategoryText => Category.Length > 0 ? Category : Kind == CredentialKind.PrivateKey ? "ssh" : "";
    [JsonIgnore] public string FolderText => Folder.Length > 0 ? Folder : "";
    /// <summary>"Library / Collection" when this credential comes from a shared library (ADR-033); never saved.</summary>
    [JsonIgnore] public string? SharedFrom { get; set; }
    [JsonIgnore] public bool IsShared => SharedFrom != null;
    [JsonIgnore] public string Source => SharedFrom != null ? "⇄ shared · " + SharedFrom : IsExternal ? "⛓ " + ExternalText : "this PC";
    [JsonIgnore] public string Display => $"{(SharedFrom != null ? "⇄ " : IsExternal ? "⛓ " : "")}{Name}  ({KindText}{(string.IsNullOrEmpty(Username) ? "" : ", " + Username)}{(SharedFrom != null ? " · shared: " + SharedFrom : IsExternal ? " · from " + ExternalText : "")})";
}

public sealed class Snippet
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    public bool Sudo { get; set; }
    public string Description { get; set; } = "";
    /// <summary>A button on the terminal's snippet bar (0.6.29, ADR-104).</summary>
    public bool Bar { get; set; }
    /// <summary>Where the button shows (comma separated, any one matches): host:&lt;name&gt;, folder:&lt;path&gt; (with its
    /// subfolders), tag:&lt;tag&gt;, env:&lt;ENVIRONMENT&gt;, shell:posix|powershell, local; empty = every terminal.</summary>
    public string Scope { get; set; } = "";
}

public sealed class TunnelDef : INotifyPropertyChanged
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string HostId { get; set; } = "";
    public TunnelKind Kind { get; set; } = TunnelKind.Local;
    public string BindAddress { get; set; } = "127.0.0.1";
    public int BindPort { get; set; }
    public string DestHost { get; set; } = "127.0.0.1";
    public int DestPort { get; set; }
    public bool AutoStart { get; set; }

    private string _status = "stopped";
    [JsonIgnore] public string Status { get => _status; set { _status = value; OnChanged(); } }
    [JsonIgnore] public string Summary => Kind switch
    {
        TunnelKind.Local => $"{BindAddress}:{BindPort} → {DestHost}:{DestPort}",
        TunnelKind.Remote => $"remote {BindAddress}:{BindPort} → local {DestHost}:{DestPort}",
        _ => $"SOCKS5 on {BindAddress}:{BindPort}",
    };
    [JsonIgnore] public string HostName => AppData.Current.FindHost(HostId)?.Display ?? "(missing host)";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public sealed class VultrAccount : Cloud.ICloudAccount
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Vultr";
    public string ApiKey { get; set; } = "";
    /// <summary>Kept but not used (no screen, check or tool contacts it) until enabled again.</summary>
    public bool Disabled { get; set; }
    [JsonIgnore] public string Masked => ApiKey.Length > 8 ? ApiKey[..4] + new string('•', 12) + ApiKey[^4..] : "••••";
    [JsonIgnore] public string State => Disabled ? "disabled" : "enabled";
    public override string ToString() => Name;
}

/// <summary>A Contabo API account (ADR-020). All four values come from the Contabo Customer Control Panel → API; they are
/// stored only in the encrypted settings and never shown again (only masked).</summary>
public sealed class ContaboAccount : Cloud.ICloudAccount
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Contabo";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    /// <summary>The API user (your Contabo login e-mail).</summary>
    public string ApiUser { get; set; } = "";
    public string ApiPassword { get; set; } = "";
    public bool Disabled { get; set; }
    [JsonIgnore] public string MaskedClient => ClientId.Length > 8 ? ClientId[..4] + new string('•', 8) + ClientId[^3..] : ClientId.Length > 0 ? "••••" : "";
    [JsonIgnore] public string State => Disabled ? "disabled" : "enabled";
    /// <summary>The name only: never the credentials (lists, pickers and logs show this).</summary>
    public override string ToString() => Name;
}

/// <summary>A Hetzner Cloud project (ADR-028). Hetzner issues one API token per project, either "Read" or "Read & Write"; the
/// token is stored only in the encrypted settings and never shown again (only masked). <see cref="ReadOnly"/> records what
/// the user said the token is: the app then refuses every change itself, before anything is sent.</summary>
public sealed class HetznerAccount : Cloud.ICloudAccount
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>The project's name as the user calls it (the API doesn't expose project names).</summary>
    public string Name { get; set; } = "Hetzner";
    public string Token { get; set; } = "";
    public bool ReadOnly { get; set; }
    public bool Disabled { get; set; }
    [JsonIgnore] public string MaskedToken => Token.Length > 8 ? Token[..4] + new string('•', 12) + Token[^4..] : Token.Length > 0 ? "••••" : "";
    [JsonIgnore] public string State => (Disabled ? "disabled" : "enabled") + (ReadOnly ? " · read-only" : " · read & write");
    /// <summary>The name only: never the token (lists, pickers and logs show this).</summary>
    public override string ToString() => Name;
}

/// <summary>An Azure subscription reached through a Microsoft Entra app registration that signs in with a certificate
/// (0.2.0). The certificate (PFX, with its private key) and its password live only in the encrypted settings; nothing
/// shows them again. New accounts are read-only until changes are allowed.</summary>
public sealed class AzureAccount : Cloud.ICloudAccount
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Azure";
    public string TenantId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string SubscriptionId { get; set; } = "";
    /// <summary>The app registration's certificate as a base64 PFX (private key included).</summary>
    public string CertificatePfx { get; set; } = "";
    public string CertificatePassword { get; set; } = "";
    /// <summary>SHA-256 thumbprint of the certificate (shown to compare with the app registration; not secret).</summary>
    public string CertificateSha256 { get; set; } = "";
    public bool ReadOnly { get; set; } = true;
    public bool Disabled { get; set; }
    [JsonIgnore] public string State => (Disabled ? "disabled" : "enabled") + (ReadOnly ? " · read-only" : " · changes allowed");
    /// <summary>The name only: never a key or the certificate (lists, pickers and logs show this).</summary>
    public override string ToString() => Name;
}

/// <summary>A Google Cloud project reached with a service-account key (0.2.0). The key file (JSON with its private key)
/// lives only in the encrypted settings. New accounts are read-only until changes are allowed; a read-only account also
/// asks Google only for the read-only scope.</summary>
public sealed class GcpAccount : Cloud.ICloudAccount
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Google Cloud";
    public string ProjectId { get; set; } = "";
    /// <summary>The service-account key file as downloaded from Google (JSON, private key included).</summary>
    public string ServiceAccountJson { get; set; } = "";
    /// <summary>The service account's e-mail (from the key file; not secret).</summary>
    public string ClientEmail { get; set; } = "";
    public bool ReadOnly { get; set; } = true;
    public bool Disabled { get; set; }
    [JsonIgnore] public string State => (Disabled ? "disabled" : "enabled") + (ReadOnly ? " · read-only" : " · changes allowed");
    /// <summary>The name only: never the key (lists, pickers and logs show this).</summary>
    public override string ToString() => Name;
}

public sealed class KnownHost
{
    public string Key { get; set; } = "";          // host:port
    public string Algorithm { get; set; } = "";
    public string Fingerprint { get; set; } = "";  // SHA256 base64
    public DateTime FirstSeen { get; set; } = DateTime.Now;
}
