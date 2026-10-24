using System.DirectoryServices.Protocols;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DcxManager.Services;

namespace DcxManager.SignIn;

/// <summary>
/// LDAP sign-in over TLS only (0.3.0 §23): LDAPS (636) or StartTLS on 389 — never plain LDAP. The server certificate must
/// validate through Windows and match the host name, or match the pinned SHA-256; there is no "accept any". The bind is
/// SASL / Negotiate with the Windows session (Kerberos; nothing typed) or a simple bind inside TLS with a password asked
/// for this sign-in and never stored. Then the user's entry is read: it must exist, be enabled, and its memberOf gives
/// the groups (as CN). Every destination goes through offline mode.
/// </summary>
public sealed class LdapSignIn : IDirectorySignIn
{
    public string Kind => "ldaps";

    public async Task<SignInResult> SignInAsync(DirectoryBinding b, Func<string, string?> ask, CancellationToken ct)
    {
        Offline.EnforceStatic(b.Server, "an LDAP sign-in");
        await Offline.EnforceAsync(b.Server, "an LDAP sign-in", ct);
        string? password = null;
        if (b.Bind == "simple") { password = ask($"Password for {b.User} on {b.Server} (sent only inside TLS, never stored):"); if (password == null) return SignInResult.Fail("cancelled"); }
        return await Task.Run(() => Bind(b, password), ct);
    }

    private static SignInResult Bind(DirectoryBinding b, string? password)
    {
        using var conn = new LdapConnection(new LdapDirectoryIdentifier(b.Server, b.Port, false, false));
        conn.SessionOptions.ProtocolVersion = 3;
        conn.SessionOptions.VerifyServerCertificate = (_, cert) => Validate(cert, b.Server, b.CertPin);
        conn.SessionOptions.ReferralChasing = ReferralChasingOptions.None;
        conn.Timeout = TimeSpan.FromSeconds(20);
        if (b.Transport == "ldaps") conn.SessionOptions.SecureSocketLayer = true;
        else conn.SessionOptions.StartTransportLayerSecurity(null);   // StartTLS first; the bind below never goes out in clear
        if (b.Bind == "simple")
        {
            conn.AuthType = AuthType.Basic;
            conn.Credential = new NetworkCredential(b.User, password);
        }
        else conn.AuthType = AuthType.Negotiate;
        try { conn.Bind(); }
        catch (LdapException ex) { return SignInResult.Fail($"the directory refused the sign-in: {ex.Message}"); }
        catch (DirectoryOperationException ex) { return SignInResult.Fail($"the directory refused the sign-in: {ex.Message}"); }
        finally { password = null; }

        // The account: by UPN or sAMAccountName; it must be enabled (userAccountControl bit 2 clear).
        var user = b.User.Contains('\\') ? b.User[(b.User.IndexOf('\\') + 1)..] : b.User;
        var filter = $"(|(userPrincipalName={Escape(user)})(sAMAccountName={Escape(user)})(uid={Escape(user)}))";
        var req = new SearchRequest(b.BaseDn.Length > 0 ? b.BaseDn : DefaultNamingContext(conn), filter, SearchScope.Subtree, "distinguishedName", "displayName", "cn", "memberOf", "userAccountControl", "objectSid", "userPrincipalName");
        SearchResponse resp;
        try { resp = (SearchResponse)conn.SendRequest(req); }
        catch (DirectoryOperationException ex) { return SignInResult.Fail("the account couldn't be looked up: " + ex.Message); }
        if (resp.Entries.Count != 1) return SignInResult.Fail(resp.Entries.Count == 0 ? $"no account {user} in the directory" : $"{resp.Entries.Count} accounts match {user}");
        var e = resp.Entries[0];
        if (e.Attributes["userAccountControl"] is { Count: > 0 } uac && int.TryParse(uac[0]?.ToString(), out var flags) && (flags & 2) != 0) return SignInResult.Fail("the account is disabled");
        var groups = new List<string>();
        if (e.Attributes["memberOf"] is { } mo) foreach (var g in mo.GetValues(typeof(string)).OfType<string>()) groups.Add(Cn(g));
        var name = e.Attributes["displayName"] is { Count: > 0 } dn ? dn[0]?.ToString() ?? user : e.Attributes["cn"] is { Count: > 0 } cn ? cn[0]?.ToString() ?? user : user;
        var subject = e.Attributes["userPrincipalName"] is { Count: > 0 } upn ? upn[0]?.ToString() ?? e.DistinguishedName : e.DistinguishedName;
        return new SignInResult(true, subject, name, groups, null);
    }

    private static string DefaultNamingContext(LdapConnection conn)
    {
        var r = (SearchResponse)conn.SendRequest(new SearchRequest("", "(objectClass=*)", SearchScope.Base, "defaultNamingContext"));
        return r.Entries.Count > 0 && r.Entries[0].Attributes["defaultNamingContext"] is { Count: > 0 } a ? a[0]?.ToString() ?? "" : "";
    }

    /// <summary>Strict: the chain must build through Windows' store (revocation best-effort) and the name must match —
    /// or the certificate's SHA-256 equals the pin. Never "accept any".</summary>
    public static bool Validate(X509Certificate cert, string host, string pin)
    {
        try
        {
            using var c2 = new X509Certificate2(cert);
            if (pin.Length > 0) return c2.GetCertHashString(HashAlgorithmName.SHA256).Equals(pin, StringComparison.OrdinalIgnoreCase);
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
            chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.IgnoreCertificateAuthorityRevocationUnknown | X509VerificationFlags.IgnoreEndRevocationUnknown;
            return chain.Build(c2) && c2.MatchesHostname(host);
        }
        catch (CryptographicException ex) { Log.Write("LDAP certificate", ex); return false; }
    }

    /// <summary>RFC 4515 filter escaping.</summary>
    public static string Escape(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s) sb.Append(ch switch { '\\' => "\\5c", '*' => "\\2a", '(' => "\\28", ')' => "\\29", '\0' => "\\00", _ => ch.ToString() });
        return sb.ToString();
    }

    /// <summary>"CN=Ops Admins,OU=Groups,DC=example,DC=test" → "Ops Admins".</summary>
    public static string Cn(string dn)
    {
        var first = dn.Split(',')[0];
        return first.StartsWith("CN=", StringComparison.OrdinalIgnoreCase) ? first[3..].Replace("\\,", ",") : first;
    }
}
