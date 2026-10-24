using DcxManager.BeyondTrust;
using DcxManager.Bitwarden;

namespace DcxManager.Services;

/// <summary>
/// Reads vault credentials kept in Bitwarden or Password Safe (0.6.17, ADR-096): the resolver behind
/// <see cref="ExternalCredentials"/>. A Bitwarden vault item asks for the master password when the vault is locked (never
/// in the background); a Secrets Manager secret uses the access token; a Password Safe account is checked out once and
/// shared until the checkout runs out (<see cref="PasswordSafeSessions.LeaseAsync"/>). What was read is held in memory for
/// <see cref="Hold"/> at most, so a dashboard's checks don't start the CLI every time, and forgotten when the vault or
/// the app locks. Every read is audited by name, never by value.
/// </summary>
public static class CredentialSources
{
    /// <summary>How long a value read from Bitwarden is reused.</summary>
    public static readonly TimeSpan Hold = TimeSpan.FromMinutes(2);

    private static readonly Dictionary<string, (Credential Value, DateTime Until)> Cache = new();
    private static readonly SemaphoreSlim UnlockGate = new(1, 1);
    private static DateTime _declinedUntil;

    /// <summary>Start-up: wires the resolver and the UI-thread wait into the core.</summary>
    public static void Install()
    {
        ExternalCredentials.Resolver = ResolveAsync;
        BitwardenVault.Changed += () => { if (!BitwardenVault.Unlocked) Forget(); };
    }

    /// <summary>Drops every value read (vault or app locked, integration turned off).</summary>
    public static void Forget() { lock (Cache) Cache.Clear(); CredentialSearch.Forget(); }   // and the picker's lists (names only)

    public static string Link(VaultItem i) => "bw:" + BitwardenSessions.Link(i);
    public static string Link(SmSecret s) => "bw:" + BitwardenSessions.Link(s);
    public static string Link(ManagedAccount a) => "ps:" + PasswordSafeSessions.Link(a);

    /// <summary>A new vault credential pointing at an external secret (ids and a label only; nothing secret is stored).</summary>
    public static Credential New(string external, string name, CredentialKind kind, string username, string category) =>
        new() { Name = name, Kind = kind, Username = username, External = external, Category = category, Notes = "Kept in " + new Credential { External = external }.ExternalText + ": read into memory each time it is used." };

    /// <summary>Adds it to the vault, or returns the vault credential already pointing at the same secret.</summary>
    public static (Credential Cred, bool Existed) AddToVault(Credential n)
    {
        if (!n.IsExternal) throw new ArgumentException("not an external credential", nameof(n));
        if (AppData.Current.Credentials.FirstOrDefault(x => x.External == n.External) is { } have) return (have, true);
        AppData.Current.Credentials.Add(n);
        AppData.Save();
        AuditLog.Write("you", "vault", $"added credential {n.Name} (kept in {n.ExternalText})", Describe(n));
        return (n, false);
    }

    /// <summary>What the credential points at, for a tooltip or the audit trail ("Bitwarden: label").</summary>
    public static string Describe(Credential c) =>
        c.External.StartsWith("bw:") ? BitwardenSessions.Describe(c.External[3..])
        : c.External.StartsWith("ps:") && PasswordSafeSessions.Parse(c.External[3..]) is { } p ? "Password Safe: " + p.Label : "";

    private static async Task<Credential> ResolveAsync(Credential c, string purpose, bool interactive, CancellationToken ct)
    {
        // The policies first: a value held in memory is never a way around one switched off since.
        var bw = c.External.StartsWith("bw:");
        if (bw)
        {
            if (!AppSettings.Current.BitwardenEnabled) throw new InvalidOperationException($"“{c.Name}” is kept in Bitwarden, and the Bitwarden integration is off (Settings → Integrations → Bitwarden).");
            if (!AppSettings.Current.BitwardenAllowSessions) throw new InvalidOperationException("Using Bitwarden items as credentials is switched off on this PC (policy).");
        }
        else if (c.External.StartsWith("ps:") && !(AppSettings.Current.BeyondTrustEnabled && AppSettings.Current.BeyondTrustAllowCheckout))
            throw new InvalidOperationException($"“{c.Name}” is kept in Password Safe, and checking out Password Safe credentials is off on this PC.");
        lock (Cache)
            if (Cache.TryGetValue(c.External, out var hit) && hit.Until > DateTime.Now) return hit.Value.Clone();
        Credential got;
        if (bw)
        {
            if (BitwardenSessions.Parse(c.External[3..]) is not { } link) throw new InvalidOperationException($"“{c.Name}”'s Bitwarden link isn't valid: add it again (VAULT → Bitwarden).");
            VaultSecret secret;
            if (link.Secrets) secret = await BitwardenSecrets.ValueAsync(link.Id, ct);
            else
            {
                if (!BitwardenVault.Unlocked)
                {
                    if (!interactive) throw new InvalidOperationException($"Bitwarden is locked, so “{c.Name}” can't be read for {purpose}. Background checks never ask: unlock it in VAULT → Bitwarden.");
                    await UnlockGate.WaitAsync(ct);
                    try
                    {
                        // One cancelled prompt answers the uses queued behind it (Files opening two connections) too.
                        if (DateTime.Now < _declinedUntil) throw new OperationCanceledException("the Bitwarden vault stayed locked");
                        if (!await BitwardenSessions.EnsureUnlockedAsync("for " + purpose, ct))
                        {
                            _declinedUntil = DateTime.Now.AddSeconds(10);
                            throw new OperationCanceledException("the Bitwarden vault stayed locked");
                        }
                    }
                    finally { UnlockGate.Release(); }
                }
                secret = await BitwardenVault.SecretAsync(link.Id, ct);
            }
            got = Filled(c, secret.Username, secret.Secret, secret.IsKey ? CredentialKind.PrivateKey : CredentialKind.Password);
            got.Totp = secret.Totp;   // 0.6.20: the item's authenticator setting, for "⧉ TOTP code" and filling a code field
        }
        else if (c.External.StartsWith("ps:"))
        {
            var (user, pw) = await PasswordSafeSessions.LeaseAsync(c.External[3..], purpose, interactive, ct);
            got = Filled(c, user, pw, CredentialKind.Password);
        }
        else throw new InvalidOperationException($"“{c.Name}” points at an unknown source.");
        lock (Cache) Cache[c.External] = (got, DateTime.Now + (c.External.StartsWith("ps:") ? TimeSpan.FromSeconds(30) : Hold));
        AuditLog.Write(interactive ? "you" : "app", "vault", "external credential used", $"{c.Name} ({Describe(c)}) for {purpose}");
        return got.Clone();
    }

    /// <summary>The in-memory copy: the stored entry with the secret filled in, no longer marked external.</summary>
    private static Credential Filled(Credential c, string user, string secret, CredentialKind kind)
    {
        var x = c.Clone();
        x.External = "";
        x.Kind = kind;
        x.Secret = secret;
        if (user.Length > 0 && (x.Username.Length == 0 || kind == CredentialKind.Password)) x.Username = user;   // a login's own username wins
        return x;
    }
}
