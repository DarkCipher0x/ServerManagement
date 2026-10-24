using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DcxManager.Services;

namespace DcxManager.Library;

// The files of a shared library on the share (ADR-033). Everything here is public: ids, public keys, wrapped keys,
// ciphertext and signatures. Names, addresses, users, notes and secrets only ever appear inside sealed fields.
//
//   <share>\library.json                  LibraryHeader   — the owner's public keys, signed by the owner (the trust anchor)
//   <share>\roster.json                   Roster          — members, roles, access per collection; signed by an admin
//   <share>\meta.json                     Meta            — the metadata key wrapped per member, sealed names, audit keys
//   <share>\collections\<cid>\keys.json   KeysFile        — the collection key of every epoch, wrapped per reader
//   <share>\collections\<cid>\items\<item>.<rev>.<author8>.dcxi   ItemFile — one revision of one host or credential
//   <share>\requests\<id>.json            JoinRequest     — a would-be member's public keys, self-signed
//   <share>\audit\<member>-yyyy-MM.dcxa   AuditLine per line — sealed to the audit key, signed, hash-chained

public static class Roles
{
    public const string Owner = "owner", Admin = "admin", Member = "member";
    public const string Editor = "editor", Reader = "reader";
    public static bool IsAdmin(string? role) => role is Owner or Admin;
    public static string Label(string role) => role switch { Owner => "Owner", Admin => "Admin", _ => "Member" };
}

public sealed class LibraryHeader
{
    public int Format { get; set; } = 1;
    public string LibraryId { get; set; } = "";
    public string OwnerId { get; set; } = "";
    public string OwnerEncPub { get; set; } = "";
    public string OwnerSigPub { get; set; } = "";
    public string Created { get; set; } = "";
    public string Sig { get; set; } = "";
    /// <summary>Ownership hand-overs, in order. Each is signed by the owner before it; the header's own signature (by the
    /// first owner) does not cover them, so the chain is checked link by link from the first owner.</summary>
    public List<OwnerTransfer> Transfers { get; set; } = new();
    public string Canonical() => $"header|{Format}|{LibraryId}|{OwnerId}|{OwnerEncPub}|{OwnerSigPub}|{Created}";
}

/// <summary>The owner hands the library to an admin: signed by the current owner, accepted (written into the header) by the new one.</summary>
public sealed class OwnerTransfer
{
    public int Seq { get; set; }
    public string FromId { get; set; } = "";
    public string ToId { get; set; } = "";
    public string ToEncPub { get; set; } = "";
    public string ToSigPub { get; set; } = "";
    public string Time { get; set; } = "";
    public string Sig { get; set; } = "";
    public string Canonical(string lib) => $"transfer|{lib}|{Seq}|{FromId}|{ToId}|{ToEncPub}|{ToSigPub}|{Time}";
}

/// <summary>A member's keys exported for safekeeping or another PC: sealed with a key derived from a password
/// (PBKDF2-SHA256, 600,000 iterations) — never plain.</summary>
public sealed class KeyBackup
{
    public string Format { get; set; } = "dcxlibkey1";
    public string LibraryId { get; set; } = "";
    public string Salt { get; set; } = "";
    public int Iterations { get; set; } = 600_000;
    public string Blob { get; set; } = "";
}

public sealed record OwnerKeys(string Id, string EncPub, string SigPub);

public sealed class RosterEntry
{
    public string Id { get; set; } = "";
    public string EncPub { get; set; } = "";
    public string SigPub { get; set; } = "";
    public string Role { get; set; } = Roles.Member;
    /// <summary>Collection id → "editor" | "reader". Owner and admins can read and edit every collection.</summary>
    public SortedDictionary<string, string> Access { get; set; } = new(StringComparer.Ordinal);
    public string GrantedBy { get; set; } = "";
    public string Granted { get; set; } = "";
    public string GrantSig { get; set; } = "";
    public string Canonical(string lib) => $"grant|{lib}|{Id}|{EncPub}|{SigPub}|{Role}|{string.Join(";", Access.OrderBy(a => a.Key, StringComparer.Ordinal).Select(a => a.Key + "=" + a.Value))}|{GrantedBy}|{Granted}";
    public RosterEntry Copy() { var c = (RosterEntry)MemberwiseClone(); c.Access = new(Access, StringComparer.Ordinal); return c; }
}

/// <summary>A removed member: kept so items they wrote before the removal still verify, and so their old epochs are known.</summary>
public sealed class RemovedEntry
{
    public string Id { get; set; } = "";
    public string EncPub { get; set; } = "";
    public string SigPub { get; set; } = "";
    public string Role { get; set; } = Roles.Member;
    public SortedDictionary<string, string> Access { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Collection id → the first epoch the member could NOT read. Items they wrote below it still count.</summary>
    public SortedDictionary<string, int> Epochs { get; set; } = new(StringComparer.Ordinal);
    public string Removed { get; set; } = "";
    public string Canonical() => $"{Id}:{EncPub}:{SigPub}:{Role}:{string.Join(",", Access.OrderBy(a => a.Key, StringComparer.Ordinal).Select(a => a.Key + "=" + a.Value))}:{string.Join(",", Epochs.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => e.Key + "=" + e.Value))}:{Removed}";
}

/// <summary>A named group of members with its own per-collection access (0.3.0 §22: group-based permissions). A member's
/// effective access is their own entry's plus every group they are in; the keys are wrapped accordingly, so a group is
/// enforced by the encryption like a direct grant. Part of the signed roster.</summary>
public sealed class RosterGroup
{
    public string Name { get; set; } = "";
    public List<string> Members { get; set; } = new();
    /// <summary>Collection id → "editor" | "reader".</summary>
    public SortedDictionary<string, string> Access { get; set; } = new(StringComparer.Ordinal);
    public string Canonical() => $"{Name}={string.Join("+", Members.OrderBy(m => m, StringComparer.Ordinal))}={string.Join(";", Access.OrderBy(a => a.Key, StringComparer.Ordinal).Select(a => a.Key + "=" + a.Value))}";
    public RosterGroup Copy() => new() { Name = Name, Members = Members.ToList(), Access = new(Access, StringComparer.Ordinal) };
}

public sealed class Roster
{
    public string LibraryId { get; set; } = "";
    public long Seq { get; set; }
    public List<RosterEntry> Members { get; set; } = new();
    public List<RemovedEntry> Removed { get; set; } = new();
    /// <summary>Groups (0.3.0 §22); older rosters have none and keep their signature.</summary>
    public List<RosterGroup> Groups { get; set; } = new();
    public string Signer { get; set; } = "";
    public string Time { get; set; } = "";
    public string Sig { get; set; } = "";
    public string Canonical() => $"roster|{LibraryId}|{Seq}|{Time}|{Signer}|{string.Join(",", Members.Select(m => m.Id + ":" + m.GrantSig))}|{string.Join(",", Removed.Select(r => r.Canonical()))}"
                                 + (Groups.Count > 0 ? "|groups|" + string.Join(",", Groups.OrderBy(g => g.Name, StringComparer.Ordinal).Select(g => g.Canonical())) : "");

    /// <summary>Own access plus every group's (editor wins over reader). Admins read and edit everything.</summary>
    public SortedDictionary<string, string> EffectiveAccess(RosterEntry e)
    {
        var o = new SortedDictionary<string, string>(e.Access, StringComparer.Ordinal);
        foreach (var g in Groups.Where(g => g.Members.Contains(e.Id)))
            foreach (var (cid, level) in g.Access)
                if (!o.TryGetValue(cid, out var cur) || (cur != Roles.Editor && level == Roles.Editor)) o[cid] = level;
        return o;
    }

    public bool Readable(RosterEntry e, string cid) => Roles.IsAdmin(e.Role) || e.Access.ContainsKey(cid) || Groups.Any(g => g.Members.Contains(e.Id) && g.Access.ContainsKey(cid));
    public bool Editable(RosterEntry e, string cid) => Roles.IsAdmin(e.Role) || e.Access.GetValueOrDefault(cid) == Roles.Editor || Groups.Any(g => g.Members.Contains(e.Id) && g.Access.GetValueOrDefault(cid) == Roles.Editor);
}

public sealed class AuditGen
{
    public int Gen { get; set; }
    public string Pub { get; set; } = "";
    /// <summary>Admin id → the audit private key (PKCS#8) wrapped for that admin.</summary>
    public SortedDictionary<string, Wrap> Wraps { get; set; } = new(StringComparer.Ordinal);
    public string Canonical() => $"{Gen}:{Pub}:{Wraps.Canon()}";
}

public sealed class Meta
{
    public string LibraryId { get; set; } = "";
    public long Seq { get; set; }
    public int MkEpoch { get; set; } = 1;
    /// <summary>Member id → the metadata key wrapped for them.</summary>
    public SortedDictionary<string, Wrap> Mk { get; set; } = new(StringComparer.Ordinal);
    /// <summary><see cref="MetaInfo"/> sealed under the metadata key.</summary>
    public string Info { get; set; } = "";
    public List<AuditGen> Audit { get; set; } = new();
    public string Signer { get; set; } = "";
    public string Time { get; set; } = "";
    public string Sig { get; set; } = "";
    public string Canonical() => $"meta|{LibraryId}|{Seq}|{MkEpoch}|{Mk.Canon()}|{Info}|{string.Join(",", Audit.Select(a => a.Canonical()))}|{Signer}|{Time}";
    public AuditGen CurrentAudit => Audit.OrderBy(a => a.Gen).Last();
}

public sealed class MetaInfo
{
    public string Name { get; set; } = "";
    public SortedDictionary<string, string> Collections { get; set; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, string> Members { get; set; } = new(StringComparer.Ordinal);
}

public sealed class KeysFile
{
    public string LibraryId { get; set; } = "";
    public string Cid { get; set; } = "";
    public int Epoch { get; set; } = 1;
    /// <summary>Epoch → member id → the collection key of that epoch wrapped for the member.</summary>
    public SortedDictionary<int, SortedDictionary<string, Wrap>> Epochs { get; set; } = new();
    public string Signer { get; set; } = "";
    public string Time { get; set; } = "";
    public string Sig { get; set; } = "";
    public string Canonical() => $"keys|{LibraryId}|{Cid}|{Epoch}|{string.Join(";", Epochs.OrderBy(e => e.Key).Select(e => e.Key + "=" + e.Value.Canon()))}|{Signer}|{Time}";
}

public sealed class ItemFile
{
    public string LibraryId { get; set; } = "";
    public string Cid { get; set; } = "";
    public string Item { get; set; } = "";
    public int Rev { get; set; }
    public string Author { get; set; } = "";
    public int Epoch { get; set; }
    /// <summary>The revisions this one replaces ("rev.author"); two revisions with the same base are a conflict.</summary>
    public List<string> Base { get; set; } = new();
    public string Time { get; set; } = "";
    /// <summary><see cref="ItemPayload"/> sealed under the collection key of <see cref="Epoch"/>.</summary>
    public string Body { get; set; } = "";
    public string Sig { get; set; } = "";
    [JsonIgnore] public string RevId => $"{Rev}.{Author}";
    public string FileName => $"{Item}.{Rev}.{(Author.Length >= 8 ? Author[..8] : Author)}.dcxi";
    public string Canonical() => $"item|{LibraryId}|{Cid}|{Item}|{Rev}|{Author}|{Epoch}|{string.Join(",", Base)}|{Time}|{Body}";
    public string Aad => $"{LibraryCrypto.Format}|item|{LibraryId}|{Cid}|{Item}|{RevId}|{Epoch}";
}

/// <summary>What an item holds. Ids inside <see cref="Host"/> (credential, sudo credential, jump host) are item ids of the
/// same library, never local ids.</summary>
public sealed class ItemPayload
{
    public string Kind { get; set; } = "host";   // host | cred | tomb
    public HostEntry? Host { get; set; }
    public Credential? Cred { get; set; }
    public string EditedBy { get; set; } = "";
    public string Note { get; set; } = "";
}

public sealed class JoinRequest
{
    public string LibraryId { get; set; } = "";
    public string Id { get; set; } = "";
    public string EncPub { get; set; } = "";
    public string SigPub { get; set; } = "";
    /// <summary>The display name sealed to the audit key of <see cref="Gen"/> (only admins can read it).</summary>
    public Wrap Name { get; set; } = new();
    public int Gen { get; set; }
    public string Time { get; set; } = "";
    /// <summary>SHA-256 (hex) of the requester's secret nonce (0.3.0 §5: short approval codes). Empty in older requests.</summary>
    public string Commit { get; set; } = "";
    public string Sig { get; set; } = "";
    public string Canonical() => $"request|{LibraryId}|{Id}|{EncPub}|{SigPub}|{Name.Epk}:{Name.Ct}|{Gen}|{Time}" + (Commit.Length > 0 ? "|" + Commit : "");
    [JsonIgnore] public string Fingerprint => LibraryCrypto.Fingerprint(EncPub, SigPub);
}

/// <summary>An admin's answer to a join request (0.3.0 §5): a fresh nonce, signed by that admin.</summary>
public sealed class JoinChallenge
{
    public string LibraryId { get; set; } = "";
    public string RequestId { get; set; } = "";
    public string AdminId { get; set; } = "";
    public string Nonce { get; set; } = "";
    public string Time { get; set; } = "";
    public string Sig { get; set; } = "";
    public string Canonical() => $"challenge|{LibraryId}|{RequestId}|{AdminId}|{Nonce}|{Time}";
}

/// <summary>The requester reveals the nonce it committed to, signed with its request key (0.3.0 §5).</summary>
public sealed class JoinReveal
{
    public string LibraryId { get; set; } = "";
    public string RequestId { get; set; } = "";
    public string Nonce { get; set; } = "";
    public string Time { get; set; } = "";
    public string Sig { get; set; } = "";
    public string Canonical() => $"reveal|{LibraryId}|{RequestId}|{Nonce}|{Time}";
}

public sealed class AuditLine
{
    public long Seq { get; set; }
    public string Prev { get; set; } = "";
    public string Time { get; set; } = "";
    public int Gen { get; set; }
    public Wrap Body { get; set; } = new();
    public string Sig { get; set; } = "";
    public string Canonical(string member) => $"audit|{member}|{Seq}|{Prev}|{Time}|{Gen}|{Body.Epk}:{Body.Ct}";
}

internal static class FormatExt
{
    public static string Canon(this SortedDictionary<string, Wrap> d) => string.Join(",", d.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Value.Kem.Length == 0 ? $"{kv.Key}:{kv.Value.Epk}:{kv.Value.Ct}" : $"{kv.Key}:{kv.Value.Epk}:{kv.Value.Ct}:{kv.Value.Kem}"));
}

/// <summary>Reads and writes the share's files. Every write is a temporary file renamed into place; every read is bounded.</summary>
public static class ShareIo
{
    public const long MaxFile = 4L << 20;
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = false, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 32 };

    public static T? Read<T>(string path) where T : class
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) return null;
                if (fi.Length > MaxFile) throw new InvalidDataException($"{fi.Name} is larger than 4 MB");
                return JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), Json);
            }
            catch (IOException) when (attempt < 3) { Thread.Sleep(60); }
        }
    }

    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp-" + LibraryCrypto.NewId()[..8];
        File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(value, Json));
        File.Move(tmp, path, overwrite: true);
    }

    public static string ToJson<T>(T value) => JsonSerializer.Serialize(value, SecureStore.Json);
    public static T? FromJson<T>(string json) => JsonSerializer.Deserialize<T>(json, SecureStore.Json);
}

/// <summary>The data file of a library add-on (`library.json` in the package): which library, where, and whose key to
/// trust. Parsed strictly like a theme (ADR-030); it holds no secret, so the package can be handed to the whole team.</summary>
public sealed class LibraryProfile
{
    public string LibraryId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string OwnerFingerprint { get; set; } = "";

    public const int MaxBytes = 16 * 1024;

    /// <summary>The general Shared library add-on: switches the Shared library screen on; it names no library.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool IsGeneral => LibraryId.Length == 0;

    public static LibraryProfile Parse(string json)
    {
        if (json.Length > MaxBytes) throw new InvalidDataException("library file is larger than 16 KB");
        JsonNode? root;
        try { root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { MaxDepth = 4, CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (JsonException ex) { throw new InvalidDataException("library file is not valid JSON: " + ex.Message); }
        if (root is not JsonObject o) throw new InvalidDataException("library file must be a JSON object");
        var p = new LibraryProfile();
        string Text(JsonNode? v, string prop, int max)
        {
            string s;
            try { s = v?.GetValue<string>() ?? ""; } catch (InvalidOperationException) { throw new InvalidDataException($"\"{prop}\" must be text"); }
            if (s.Length > max || s.Any(char.IsControl)) throw new InvalidDataException($"\"{prop}\" must be plain text of at most {max} characters");
            return s.Trim();
        }
        foreach (var (prop, value) in o)
        {
            switch (prop)
            {
                case "libraryId": p.LibraryId = Text(value, prop, 32); break;
                case "name": p.Name = Text(value, prop, 60); break;
                case "path": p.Path = Text(value, prop, 260); break;
                case "ownerFingerprint": p.OwnerFingerprint = Text(value, prop, 64); break;
                case "notes": Text(value, prop, 1000); break;
                default: throw new InvalidDataException($"unknown field “{prop}” (allowed: libraryId, name, path, ownerFingerprint, notes)");
            }
        }
        if (p.Name.Length == 0) throw new InvalidDataException("\"name\" is required");
        // No library id and no fingerprint: the general Shared library add-on, which only switches the feature on.
        if (p.LibraryId.Length == 0 && p.OwnerFingerprint.Length == 0 && p.Path.Length == 0) return p;
        if (p.LibraryId.Length != 32 || !p.LibraryId.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')) throw new InvalidDataException("\"libraryId\" must be 32 lowercase hex characters");
        if (LibraryCrypto.NormalizeFingerprint(p.OwnerFingerprint).Length != 32) throw new InvalidDataException("\"ownerFingerprint\" must be 32 hex characters (8 groups of 4)");
        if (p.Path.Length > 0 && (p.Path.IndexOfAny(System.IO.Path.GetInvalidPathChars()) >= 0 || !(p.Path.StartsWith(@"\\") || (p.Path.Length > 2 && p.Path[1] == ':'))))
            throw new InvalidDataException("\"path\" must be a full folder path (C:\\… or \\\\server\\share\\…)");
        return p;
    }

    public string ToJson() => JsonSerializer.Serialize(new { libraryId = LibraryId, name = Name, path = Path, ownerFingerprint = OwnerFingerprint,
        notes = "A DarkCipher ServerManagement shared library. This file holds no secret: membership is approved by an admin." }, new JsonSerializerOptions { WriteIndented = true });
}
