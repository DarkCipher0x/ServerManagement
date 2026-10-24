using System.Text.RegularExpressions;

namespace DcxManager.SignIn;

/// <summary>
/// How a profile signs in with an organisation (0.3.0 §23). One of:
/// <list type="bullet">
/// <item><b>windows</b> — the Windows session itself: the signed-in account must be the bound domain account (Kerberos /
/// Negotiate; nothing is typed); groups come from the Windows token.</item>
/// <item><b>ldaps</b> — LDAP over TLS (636) or StartTLS (389→TLS): a SASL / Negotiate bind with the Windows session, or a
/// simple bind inside TLS with a password asked each time and never stored; the certificate must validate (or match a
/// pinned SHA-256); the account must be enabled; groups from memberOf.</item>
/// <item><b>entra</b> — Microsoft Entra ID: OAuth 2.0 authorization code with PKCE through the system browser (MFA and
/// Conditional Access apply), the id_token validated against the tenant's keys; groups from the token's groups claim.</item>
/// </list>
/// Holds no secret. Group names map to the app role ("admin" / "operator" / "viewer"); a required group, when set, gates
/// the profile. Validated strictly: it is read from the plain index.
/// </summary>
public sealed class DirectoryBinding
{
    public string Kind { get; set; } = "windows";
    /// <summary>LDAPS: host name; Entra: the tenant id (GUID) or domain.</summary>
    public string Server { get; set; } = "";
    public int Port { get; set; } = 636;
    /// <summary>LDAPS: "ldaps" (TLS from the first byte) or "starttls".</summary>
    public string Transport { get; set; } = "ldaps";
    /// <summary>LDAPS: "negotiate" (the Windows session, Kerberos) or "simple" (password asked each time, only inside TLS).</summary>
    public string Bind { get; set; } = "negotiate";
    public string BaseDn { get; set; } = "";
    /// <summary>Optional SHA-256 (hex) of the server certificate: when set, only that certificate is accepted.</summary>
    public string CertPin { get; set; } = "";
    /// <summary>Who the profile belongs to: a UPN / sAMAccountName (LDAPS, Entra) — the token or the search must match it.</summary>
    public string User { get; set; } = "";
    /// <summary>Windows: the account SID that must be signed in; Entra: the object id (oid) once known.</summary>
    public string Subject { get; set; } = "";
    /// <summary>Entra: the app registration's client id (a public client with the loopback redirect).</summary>
    public string ClientId { get; set; } = "";
    /// <summary>Entra: the authority; defaults to login.microsoftonline.com/&lt;tenant&gt;/v2.0.</summary>
    public string Authority { get; set; } = "";
    /// <summary>Group → "admin" | "operator" | "viewer" (first match in this order wins; no match = "operator").</summary>
    public Dictionary<string, string> GroupRoles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>A group the account must be in, or empty.</summary>
    public string RequiredGroup { get; set; } = "";
    /// <summary>How long a cached sign-in proof lets the profile open without the directory (0 = never offline).</summary>
    public int OfflineGraceDays { get; set; } = 7;

    public static readonly string[] Kinds = ["windows", "ldaps", "entra"];
    public static readonly string[] AppRoles = ["admin", "operator", "viewer"];
    private static readonly Regex HostRx = new(@"^[A-Za-z0-9](?:[A-Za-z0-9.-]{0,252})$");
    private static readonly Regex GuidOrDomainRx = new(@"^(?:[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}|[A-Za-z0-9](?:[A-Za-z0-9.-]{0,252})|common|organizations)$");
    private static readonly Regex ClientIdRx = new(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$");
    private static readonly Regex UserRx = new(@"^[A-Za-z0-9._%+\-]+(?:@[A-Za-z0-9.-]+)?$|^[A-Za-z0-9.-]+\\[A-Za-z0-9._\-]+$");

    /// <summary>Why the binding can't be used, or null when it is sound.</summary>
    public string? Problem()
    {
        if (!Kinds.Contains(Kind)) return "unknown sign-in kind";
        if (OfflineGraceDays is < 0 or > 90) return "offline grace must be 0–90 days";
        if (GroupRoles.Values.Any(r => !AppRoles.Contains(r))) return "a group maps to an unknown role";
        switch (Kind)
        {
            case "windows":
                if (!Subject.StartsWith("S-1-5-21-", StringComparison.Ordinal)) return "a Windows profile needs the domain account's SID";
                break;
            case "ldaps":
                if (!HostRx.IsMatch(Server)) return "the LDAP server must be a host name";
                if (Port is < 1 or > 65535) return "port";
                if (Transport is not ("ldaps" or "starttls")) return "transport must be ldaps or starttls (plain LDAP is never used)";
                if (Bind is not ("negotiate" or "simple")) return "bind must be negotiate or simple";
                if (!UserRx.IsMatch(User)) return "the user must be a UPN or DOMAIN\\name";
                if (CertPin.Length > 0 && !Regex.IsMatch(CertPin, @"^[0-9a-fA-F]{64}$")) return "the certificate pin must be a SHA-256 hex fingerprint";
                if (BaseDn.Length > 512 || BaseDn.Any(char.IsControl)) return "base DN";
                break;
            case "entra":
                if (!GuidOrDomainRx.IsMatch(Server)) return "the tenant must be its id (GUID) or verified domain";
                if (!ClientIdRx.IsMatch(ClientId)) return "the client id must be the app registration's GUID";
                if (!UserRx.IsMatch(User) || !User.Contains('@')) return "the user must be a UPN (name@domain)";
                if (Authority.Length > 0 && !(Uri.TryCreate(Authority, UriKind.Absolute, out var a) && a.Scheme == "https")) return "the authority must be an https address";
                break;
        }
        return null;
    }

    public string Describe() => Kind switch
    {
        "windows" => "Windows / Active Directory (this session)",
        "ldaps" => $"LDAP over TLS · {Server} · {User}",
        "entra" => $"Microsoft Entra ID · {Server} · {User}",
        _ => Kind,
    };

    /// <summary>The app role for a set of groups: the first mapped group in the map's order, else "operator".</summary>
    public string RoleFor(IEnumerable<string> groups)
    {
        var set = groups.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (g, role) in GroupRoles) if (set.Contains(g)) return role;
        return "operator";
    }
}
