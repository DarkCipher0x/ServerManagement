using DcxManager.BeyondTrust;
using DcxManager.Bitwarden;

namespace DcxManager.Services;

/// <summary>One row of the credential picker: a vault credential, or a Bitwarden item / Secrets Manager secret / Password
/// Safe account that becomes one when picked. Names, user names and where it is from only: never a secret.</summary>
public sealed record CredentialHit(string Source, string Name, string User, string Where, CredentialKind Kind, string External, Credential? Existing)
{
    public string SourceText => CredentialSearch.Title(Source);
    public string KindText => Kind == CredentialKind.PrivateKey ? "SSH key" : Source == "sm" ? "secret" : Source == "ps" ? "managed account" : "password";
    /// <summary>"✓" when it is already a vault credential (picking it again reuses that one).</summary>
    public string InVault => Existing != null ? "✓" : "";
}

/// <summary>
/// The search behind the credential picker (0.6.19; DarkCipher0x: "it doesnt have the option for me to search for any
/// bitwarden credentials when editing a host or portal entry … make it easy to search for bitwarden/password safe creds,
/// as there could be a lot in the persons library"). Each source is listed once (names only) and held for a few minutes, so
/// typing filters thousands of items instantly; picking an outside item adds it to the vault as an external credential
/// (ADR-096), or reuses the one already there.
/// </summary>
public static class CredentialSearch
{
    public const string Vault = "vault", Bitwarden = "bw", Secrets = "sm", PasswordSafe = "ps";

    /// <summary>How long a source's list is reused before it is read again (↻ reads it now).</summary>
    public static readonly TimeSpan Hold = TimeSpan.FromMinutes(5);
    private static readonly Dictionary<string, (List<CredentialHit> Hits, DateTime Until)> Lists = new();

    public static string Title(string source) => source switch
    {
        Vault => "This vault", Bitwarden => "Bitwarden", Secrets => "Secrets Manager", PasswordSafe => "Password Safe", _ => source,
    };

    /// <summary>The sources switched on here: this vault always; Bitwarden with the Bitwarden integration (and its use as
    /// credentials allowed), Secrets Manager once an access token is set; Password Safe with BeyondTrust and checkouts allowed.</summary>
    public static IReadOnlyList<string> Sources()
    {
        var s = AppSettings.Current;
        var list = new List<string> { Vault };
        if (s.BitwardenEnabled && s.BitwardenAllowSessions) { list.Add(Bitwarden); if (BitwardenSecrets.HasToken) list.Add(Secrets); }
        if (s.BeyondTrustEnabled && s.BeyondTrustAllowCheckout) list.Add(PasswordSafe);
        return list;
    }

    /// <summary>Drops the held lists (a source's ↻, the vault or the app locking).</summary>
    public static void Forget(string? source = null) { lock (Lists) { if (source == null) Lists.Clear(); else Lists.Remove(source); } }

    /// <summary>Every item of a source (the vault is always read fresh). Bitwarden asks for the master password when the
    /// vault is locked: the picker is something the user opened.</summary>
    public static async Task<List<CredentialHit>> LoadAsync(string source, CancellationToken ct = default)
    {
        if (source == Vault) return VaultHits();
        lock (Lists)
            if (source != Bitwarden && Lists.TryGetValue(source, out var held) && held.Until > DateTime.Now) return WithExisting(held.Hits);   // 0.6.20: Bitwarden holds its own list until it locks
        List<CredentialHit> hits;
        switch (source)
        {
            case Bitwarden:
                if (!await BitwardenSessions.EnsureUnlockedAsync("to search it", ct)) throw new OperationCanceledException("the Bitwarden vault stayed locked");
                hits = (await BitwardenVault.ItemsAsync("", ct)).Where(i => BitwardenVault.IsId(i.Id))
                    .Select(i => new CredentialHit(Bitwarden, i.Name, i.Username, i.Type == 5 ? i.Fingerprint : i.Uri, i.Type == 5 ? CredentialKind.PrivateKey : CredentialKind.Password, CredentialSources.Link(i), null)).ToList();
                break;
            case Secrets:
                if (!BitwardenSecrets.HasToken) throw new InvalidOperationException("No Secrets Manager access token yet: Settings → Integrations → Bitwarden → Secrets Manager.");
                hits = (await BitwardenSecrets.SecretsAsync(null, ct)).Where(x => BitwardenVault.IsId(x.Id))
                    .Select(x => new CredentialHit(Secrets, x.Key, "", x.Note, CredentialKind.Password, CredentialSources.Link(x), null)).ToList();
                break;
            case PasswordSafe:
                hits = (await PasswordSafeSessions.AccountsAsync(ct))
                    .Select(a => new CredentialHit(PasswordSafe, $"{a.SystemName}\\{a.AccountName}", a.AccountName, a.DomainName.Length > 0 ? a.DomainName : a.SystemName, CredentialKind.Password, CredentialSources.Link(a), null)).ToList();
                break;
            default: throw new ArgumentException("unknown source " + source);
        }
        if (source != Bitwarden) lock (Lists) Lists[source] = (hits, DateTime.Now + Hold);
        return WithExisting(hits);
    }

    private static List<CredentialHit> VaultHits() =>
        AppData.Current.Credentials.Select(c => new CredentialHit(Vault, c.Name, c.Username, c.IsExternal ? "⛓ " + CredentialSources.Describe(c) : c.FolderText, c.Kind, c.External, c)).ToList();

    /// <summary>Marks the items that are already vault credentials (that may have changed since the list was read).</summary>
    private static List<CredentialHit> WithExisting(List<CredentialHit> hits)
    {
        var byLink = AppData.Current.Credentials.Where(c => c.IsExternal).GroupBy(c => c.External).ToDictionary(g => g.Key, g => g.First());
        return hits.Select(h => h with { Existing = byLink.GetValueOrDefault(h.External) }).ToList();
    }

    /// <summary>Every word of <paramref name="query"/> somewhere in the name, user name or where it's from (any order, any
    /// case); only the kind asked for (a key for key sign-in, a password otherwise). Best first: a name starting with the
    /// query, then a name containing it, then the rest, each by name. At most <paramref name="max"/>.</summary>
    public static List<CredentialHit> Filter(IEnumerable<CredentialHit> all, string query, CredentialKind? kind, int max = 500)
    {
        var words = query.Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var q = query.Trim();
        return all.Where(h => kind == null || h.Kind == kind)
            .Where(h => words.All(w => h.Name.Contains(w, StringComparison.OrdinalIgnoreCase) || h.User.Contains(w, StringComparison.OrdinalIgnoreCase) || h.Where.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(h => q.Length == 0 ? 0 : h.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 0 : h.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .ThenBy(h => h.Name, StringComparer.OrdinalIgnoreCase)
            .Take(max).ToList();
    }

    /// <summary>The vault credential for a picked row: itself, the external credential already there, or a new one.</summary>
    public static Credential Use(CredentialHit h)
    {
        if (h.Existing != null) return h.Existing;
        if (h.External.Length == 0) throw new InvalidOperationException("nothing to add");
        if (!Restrictions.Check("credential.add")) throw new OperationCanceledException("adding credentials is turned off on this PC");
        var category = h.Kind == CredentialKind.PrivateKey ? "ssh" : h.Source == Secrets ? "api token" : h.Source == PasswordSafe ? "service account" : "";
        return CredentialSources.AddToVault(CredentialSources.New(h.External, h.Name, h.Kind, h.User, category)).Cred;
    }
}
