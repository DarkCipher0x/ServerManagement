using System.Security.Principal;

namespace DcxManager.SignIn;

/// <summary>
/// Active Directory through the Windows session (0.3.0 §23): the account signed in to Windows is the proof — Kerberos /
/// Negotiate already happened at the Windows logon, so nothing is typed and no password passes through the app. The
/// profile is bound to the account's SID; a different account (or a local, non-domain one) can't open it. Groups are the
/// token's groups (domain and local), as names.
/// </summary>
public sealed class WindowsAccountSignIn : IDirectorySignIn
{
    public string Kind => "windows";

    /// <summary>What the current Windows session is (the self-test substitutes its own).</summary>
    public static Func<(string Sid, string Name, string AuthType, bool Authenticated, List<string> Groups)> Identity { get; set; } = Read;

    public static (string Sid, string Name, string AuthType, bool Authenticated, List<string> Groups) Read()
    {
        using var id = WindowsIdentity.GetCurrent();
        var groups = new List<string>();
        foreach (var g in id.Groups ?? new IdentityReferenceCollection())
        {
            try { groups.Add(g.Translate(typeof(NTAccount)).Value); }
            catch (IdentityNotMappedException) { groups.Add(g.Value); }
            catch (SystemException) { groups.Add(g.Value); }
        }
        return (id.User?.Value ?? "", id.Name, id.AuthenticationType ?? "", id.IsAuthenticated, groups);
    }

    public Task<SignInResult> SignInAsync(DirectoryBinding b, Func<string, string?> ask, CancellationToken ct)
    {
        var (sid, name, auth, authenticated, groups) = Identity();
        if (!authenticated || sid.Length == 0) return Task.FromResult(SignInResult.Fail("the Windows session isn't an authenticated account"));
        if (!sid.StartsWith("S-1-5-21-", StringComparison.Ordinal)) return Task.FromResult(SignInResult.Fail("the Windows account isn't a domain or local account with a SID"));
        // A domain account signed in with Kerberos / Negotiate; a cached logon still shows the domain SID and groups.
        if (auth.Length > 0 && auth is not ("Kerberos" or "Negotiate" or "NTLM" or "CloudAP")) return Task.FromResult(SignInResult.Fail($"unexpected Windows authentication type {auth}"));
        return Task.FromResult(new SignInResult(true, sid, name, groups, null));
    }

    /// <summary>The binding for the account signed in right now (used when a profile is created "with this Windows account").</summary>
    public static DirectoryBinding BindCurrent()
    {
        var (sid, name, _, _, _) = Identity();
        return new DirectoryBinding { Kind = "windows", Subject = sid, User = name };
    }
}
