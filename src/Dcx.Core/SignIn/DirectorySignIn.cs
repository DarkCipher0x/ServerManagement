using System.Security.Cryptography;
using System.Text;
using DcxManager.Services;

namespace DcxManager.SignIn;

/// <summary>The outcome of a directory sign-in: the subject the directory confirmed, the display name, the groups, or why not.</summary>
public sealed record SignInResult(bool Ok, string Subject, string DisplayName, IReadOnlyList<string> Groups, string? Error)
{
    public static SignInResult Fail(string why) => new(false, "", "", Array.Empty<string>(), why);
}

/// <summary>One way of signing in (one file each: WindowsAccountSignIn, LdapSignIn, EntraSignIn; the self-test has fakes).</summary>
public interface IDirectorySignIn
{
    string Kind { get; }
    /// <param name="ask">Asks the user for a secret (a simple-bind password) — never stored. Null = cancelled.</param>
    Task<SignInResult> SignInAsync(DirectoryBinding b, Func<string, string?> ask, CancellationToken ct);
}

/// <summary>
/// Opens a directory-bound profile (0.3.0 §23): the sign-in of the binding's kind must succeed, the subject must be the
/// bound one, the required group (if any) must be present, and the result's groups give the app role. A success leaves a
/// sealed proof in the profile's store (subject, time, groups, signed with a key derived from the store's data key); while
/// the directory can't be reached, a proof younger than the offline grace opens the profile. Every attempt is audited.
/// Double-audited design: nothing here stores a password or token; certificate validation can't be switched off.
/// </summary>
public static class DirectorySignIn
{
    public const string ProofFile = "directory-proof.dcx";

    public sealed class Proof
    {
        public string Subject { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public List<string> Groups { get; set; } = new();
        public DateTime When { get; set; }
        public string Mac { get; set; } = "";
        public string Canonical() => $"proof|{Subject}|{DisplayName}|{string.Join(";", Groups)}|{When:O}";
    }

    /// <summary>The providers by kind (replaced by the self-test).</summary>
    public static Dictionary<string, IDirectorySignIn> Providers { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["windows"] = new WindowsAccountSignIn(), ["ldaps"] = new LdapSignIn(), ["entra"] = new EntraSignIn(),
    };

    /// <summary>The result of this run's sign-in (null for a local profile).</summary>
    public static SignInResult? Current { get; private set; }

    /// <summary>Signs in for a directory profile. Returns null when the profile may open, else why it may not.</summary>
    public static async Task<string?> OpenAsync(DirectoryBinding b, Func<string, string?> ask, CancellationToken ct = default)
    {
        if (b.Problem() is { } bad) return "the profile's sign-in settings are invalid: " + bad;
        if (!Providers.TryGetValue(b.Kind, out var p)) return "unknown sign-in kind";
        SignInResult r;
        try { r = await p.SignInAsync(b, ask, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { r = SignInResult.Fail(ex.Message); }
        if (r.Ok)
        {
            if (Check(b, r) is { } why) { AuditLog.Write("you", "sign-in", "directory sign-in refused", why); return why; }
            Current = r;
            Profiles.Role = b.RoleFor(r.Groups);
            SaveProof(r);
            AuditLog.Write("you", "sign-in", "directory sign-in", $"{b.Describe()} · {r.Subject} · role {Profiles.Role} · {r.Groups.Count} group(s)");
            return null;
        }
        // Offline: a recent proof stands in, with the same checks against what it recorded.
        var proof = ReadProof();
        if (proof != null && b.OfflineGraceDays > 0 && DateTime.Now - proof.When <= TimeSpan.FromDays(b.OfflineGraceDays))
        {
            var cached = new SignInResult(true, proof.Subject, proof.DisplayName, proof.Groups, null);
            if (Check(b, cached) is { } why2) { AuditLog.Write("you", "sign-in", "cached directory sign-in refused", why2); return why2; }
            Current = cached;
            Profiles.Role = b.RoleFor(cached.Groups);
            AuditLog.Write("you", "sign-in", "directory sign-in from the cached proof (directory not reachable)", $"{r.Error} · proof from {proof.When:yyyy-MM-dd HH:mm}");
            return null;
        }
        AuditLog.Write("you", "sign-in", "directory sign-in failed", r.Error ?? "");
        return r.Error ?? "sign-in failed";
    }

    /// <summary>The binding's own rules over a successful sign-in.</summary>
    public static string? Check(DirectoryBinding b, SignInResult r)
    {
        if (b.Subject.Length > 0 && !r.Subject.Equals(b.Subject, StringComparison.OrdinalIgnoreCase)) return $"the account that signed in ({r.Subject}) isn't the one this profile belongs to";
        if (b.RequiredGroup.Length > 0 && !r.Groups.Contains(b.RequiredGroup, StringComparer.OrdinalIgnoreCase)) return $"the account isn't in the required group “{b.RequiredGroup}”";
        return null;
    }

    // ---- the proof (sealed in the store; its MAC is bound to the store's key so a copied proof doesn't open another store) ---

    private static void SaveProof(SignInResult r)
    {
        if (!SecureStore.IsUnlocked) return;
        var p = new Proof { Subject = r.Subject, DisplayName = r.DisplayName, Groups = r.Groups.ToList(), When = DateTime.Now };
        p.Mac = Convert.ToBase64String(HMACSHA256.HashData(ProofKeyStable(), Encoding.UTF8.GetBytes(p.Canonical())));
        try { SecureStore.Write(ProofFile, p); } catch (Exception ex) { Log.Write("directory proof", ex); }
    }

    public static Proof? ReadProof()
    {
        if (!SecureStore.IsUnlocked) return null;
        try
        {
            var p = SecureStore.Read<Proof>(ProofFile);
            if (p.Subject.Length == 0) return null;
            var mac = HMACSHA256.HashData(ProofKeyStable(), Encoding.UTF8.GetBytes(p.Canonical()));
            return CryptographicOperations.FixedTimeEquals(mac, Convert.FromBase64String(p.Mac)) ? p : null;
        }
        catch (Exception ex) { Log.Write("directory proof", ex); return null; }
    }

    /// <summary>A key derived from the store's data key (stable across runs; the encrypt-based one above is not).</summary>
    private static byte[] ProofKeyStable() => SecureStore.DeriveSubKey("directory-proof");

    public static void ForgetProof() { try { System.IO.File.Delete(System.IO.Path.Combine(SecureStore.DataDir, ProofFile)); } catch (System.IO.IOException) { } }
}
