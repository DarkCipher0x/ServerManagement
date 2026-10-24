using System.IO;
using System.Security.Cryptography;
using System.Text;
using DcxManager.Services;

namespace DcxManager.Library;

/// <summary>This PC's side of one shared library (ADR-033): the member's keys and the last good decrypted copy. Saved as
/// <c>library-&lt;id&gt;.dcx</c> in the member's own store, sealed like every other store file; never written to the share.</summary>
public sealed class LibraryState
{
    public string LibraryId { get; set; } = "";
    public string SharePath { get; set; } = "";
    public string OwnerFingerprint { get; set; } = "";
    public string MemberId { get; set; } = "";
    public string MyName { get; set; } = "";
    public string EncPriv { get; set; } = "";
    public string SigPriv { get; set; } = "";
    /// <summary>A join request is on the share and no admin has approved it yet.</summary>
    public bool Pending { get; set; }
    /// <summary>The join request's secret nonce (base64): committed to in the request, revealed only after an admin's
    /// challenge, so the short approval code can't be ground in advance (LibraryClient.Approval.cs).</summary>
    public string JoinNonce { get; set; } = "";
    /// <summary>Admins: request id → the challenge nonce this PC sent for it (base64).</summary>
    public Dictionary<string, string> Challenges { get; set; } = new();
    public bool RemovedFromLibrary { get; set; }

    public string Name { get; set; } = "";
    public LibraryHeader? Header { get; set; }
    public Roster? Roster { get; set; }
    public long MetaSeq { get; set; }
    /// <summary>The current audit public key, so audit lines can be sealed while the share is away.</summary>
    public string AuditPub { get; set; } = "";
    public int AuditGen { get; set; }
    public SortedDictionary<string, string> Collections { get; set; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, string> MemberNames { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Collection id → epoch → collection key (base64). Needed to write while the share is away.</summary>
    public Dictionary<string, Dictionary<int, string>> Keys { get; set; } = new();
    public Dictionary<string, int> Epochs { get; set; } = new();
    public Dictionary<string, CachedItem> Items { get; set; } = new();
    /// <summary>Signed, sealed revisions written while the share was unreachable.</summary>
    public List<ItemFile> Outbox { get; set; } = new();
    public List<QueuedAuditLine> AuditOutbox { get; set; } = new();
    public long AuditSeq { get; set; }
    public string AuditPrev { get; set; } = "";
    public string AuditMonth { get; set; } = "";
    public List<RotationTask> Rotation { get; set; } = new();
    public DateTime? LastSync { get; set; }
    public string? LastError { get; set; }
    public List<string> Problems { get; set; } = new();
    /// <summary>Optional sync interval in minutes (0 = off: no polling unless asked).</summary>
    public int SyncMinutes { get; set; }
}

public sealed class CachedItem
{
    public string Cid { get; set; } = "";
    public string Item { get; set; } = "";
    public int Rev { get; set; }
    public string RevId { get; set; } = "";
    public string Author { get; set; } = "";
    public string Time { get; set; } = "";
    public ItemPayload Payload { get; set; } = new();
    /// <summary>Other heads when two people saved the same item from the same base. Never resolved silently.</summary>
    public List<CachedItem> Conflicts { get; set; } = new();
    public string? Warning { get; set; }
    public bool Pending { get; set; }
    public IEnumerable<string> Heads => Conflicts.Select(c => c.RevId).Prepend(RevId);
}

public sealed class QueuedAuditLine
{
    public string File { get; set; } = "";
    public string Line { get; set; } = "";
}

public sealed class RotationTask
{
    public string Item { get; set; } = "";
    public string Name { get; set; } = "";
    public string Collection { get; set; } = "";
    public string Because { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime? Done { get; set; }
}

public sealed record AuditEntry(string Member, string Name, long Seq, string Time, string Action, string Target, string? Problem);
public sealed record RequestInfo(JoinRequest Request, string Name, bool Valid);

public sealed class LibraryException(string message) : Exception(message);

/// <summary>The engine of one shared library for one member: create, join, sync, write, members, rotation, audit. Crypto,
/// verification and merging run here, in the app, never in an add-on process (decrypted team secrets must not cross the
/// Low-IL bridge). The share is untrusted: everything read from it is verified before it is believed.</summary>
public sealed partial class LibraryClient
{
    public LibraryState State { get; }
    private readonly Action<LibraryState> _persist;
    private readonly object _gate = new();
    private MemberKeys? _keys;

    public LibraryClient(LibraryState state, Action<LibraryState> persist) { State = state; _persist = persist; }

    public MemberKeys Keys => _keys ??= MemberKeys.Import(State.EncPriv, State.SigPriv);
    public List<CachedItem> ItemsSnapshot() { lock (_gate) return State.Items.Values.ToList(); }
    public string Id => State.LibraryId;
    public string Me => State.MemberId;
    private string P(params string[] parts) => Path.Combine(new[] { State.SharePath }.Concat(parts).ToArray());
    private static string Now() => DateTime.UtcNow.ToString("O");

    /// <summary>Test seam and Offline gate: may the share be touched now? null = yes, else the reason it can't.</summary>
    public static Func<string, string?> Gate = DefaultGate;

    public static string? DefaultGate(string path)
    {
        if (path.StartsWith(@"\\") && Offline.On)
        {
            var host = path.TrimStart('\\').Split('\\')[0];
            var (allowed, reason) = Offline.Quick(host);
            if (allowed != true) return "Offline mode: " + (reason.Length > 0 ? reason : $"{host} is not on the allowed list");
        }
        return null;
    }

    /// <summary>null when the share can be used; otherwise why the cached copy is being used.</summary>
    public string? Unreachable()
    {
        if (Gate(State.SharePath) is { } why) return why;
        try { return File.Exists(P("library.json")) ? null : $"{State.SharePath} can't be reached or holds no library"; }
        catch (Exception ex) { return ex.Message; }
    }

    // ---- roles ------------------------------------------------------------------------------------------------------

    public RosterEntry? Entry(string id) => State.Roster?.Members.FirstOrDefault(m => m.Id == id);
    public string MyRole => Entry(Me)?.Role ?? "";
    public bool IsAdmin => Roles.IsAdmin(MyRole);
    public bool IsOwner => MyRole == Roles.Owner;
    public bool CanRead(string cid) => IsAdmin || (State.Roster is { } r && Entry(Me) is { } me && r.Readable(me, cid));
    public bool CanEdit(string cid) => IsAdmin || (State.Roster is { } r && Entry(Me) is { } me && r.Editable(me, cid));
    public string NameOf(string member) => State.MemberNames.GetValueOrDefault(member) is { Length: > 0 } n ? n : "member " + member[..Math.Min(8, member.Length)];
    public string CollectionName(string cid) => State.Collections.GetValueOrDefault(cid) ?? "collection " + cid[..Math.Min(8, cid.Length)];

    // Access is the roster's to decide (own grant or a group, 0.3.0 §22): see Roster.Readable / Editable.

    // ---- create / join ----------------------------------------------------------------------------------------------

    /// <summary>Creates a new library in an empty folder, with this PC as the owner and one collection.</summary>
    public static LibraryClient Create(string sharePath, string name, string ownerName, string firstCollection, Action<LibraryState> persist)
    {
        if (Gate(sharePath) is { } why) throw new LibraryException(why);
        Directory.CreateDirectory(sharePath);
        if (File.Exists(Path.Combine(sharePath, "library.json"))) throw new LibraryException("That folder already holds a library. Join it instead, or pick an empty folder.");
        var keys = MemberKeys.Create();
        var (ep, sp) = keys.ExportPrivate();
        var lib = LibraryCrypto.NewId();
        var me = LibraryCrypto.NewId();
        var st = new LibraryState { LibraryId = lib, SharePath = sharePath, MemberId = me, MyName = ownerName, EncPriv = ep, SigPriv = sp, OwnerFingerprint = keys.Fingerprint, Name = name };
        var c = new LibraryClient(st, persist) { _keys = keys };
        var header = new LibraryHeader { LibraryId = lib, OwnerId = me, OwnerEncPub = keys.EncPub, OwnerSigPub = keys.SigPub, Created = Now() };
        header.Sig = keys.Sign(header.Canonical());
        var owner = new RosterEntry { Id = me, EncPub = keys.EncPub, SigPub = keys.SigPub, Role = Roles.Owner, GrantedBy = me, Granted = Now() };
        owner.GrantSig = keys.Sign(owner.Canonical(lib));
        var roster = new Roster { LibraryId = lib, Seq = 1, Members = { owner } };
        var cid = LibraryCrypto.NewId();
        var info = new MetaInfo { Name = name, Collections = { [cid] = firstCollection }, Members = { [me] = ownerName } };
        var meta = new Meta { LibraryId = lib, Seq = 1 };
        c.SealMeta(meta, info, LibraryCrypto.RandomKey(), new[] { owner });
        using (var audit = KemKey.Create())
            meta.Audit.Add(c.NewAuditGen(1, audit, new[] { owner }));
        var keysFile = new KeysFile { LibraryId = lib, Cid = cid, Epoch = 1 };
        var ck = LibraryCrypto.RandomKey();
        keysFile.Epochs[1] = new() { [me] = LibraryCrypto.WrapFor(keys.EncPub, ck, c.CkContext(cid, 1, me)) };
        c.Sign(roster); c.Sign(meta); c.Sign(keysFile);
        ShareIo.Write(c.P("library.json"), header);
        ShareIo.Write(c.P("roster.json"), roster);
        ShareIo.Write(c.P("meta.json"), meta);
        ShareIo.Write(c.P("collections", cid, "keys.json"), keysFile);
        Directory.CreateDirectory(c.P("collections", cid, "items"));
        Directory.CreateDirectory(c.P("requests"));
        Directory.CreateDirectory(c.P("audit"));
        c.Sync();
        c.Audit("library created", name);
        return c;
    }

    public LibraryProfile Profile() => new() { LibraryId = Id, Name = State.Name, Path = State.SharePath, OwnerFingerprint = State.OwnerFingerprint };

    /// <summary>Generates this PC's keys and writes a signed join request. An admin must check the fingerprint and approve.</summary>
    public static LibraryClient Join(LibraryProfile profile, string sharePath, string myName, Action<LibraryState> persist)
    {
        if (Gate(sharePath) is { } why) throw new LibraryException(why);
        var header = ShareIo.Read<LibraryHeader>(Path.Combine(sharePath, "library.json")) ?? throw new LibraryException($"No library was found in {sharePath}.");
        VerifyHeader(header, profile.LibraryId, profile.OwnerFingerprint);
        var keys = MemberKeys.Create();
        var (ep, sp) = keys.ExportPrivate();
        var st = new LibraryState { LibraryId = profile.LibraryId, SharePath = sharePath, MemberId = LibraryCrypto.NewId(), MyName = myName, EncPriv = ep, SigPriv = sp,
                                    OwnerFingerprint = profile.OwnerFingerprint, Name = profile.Name, Pending = true, Header = header };
        var c = new LibraryClient(st, persist) { _keys = keys };
        var roster = c.ReadRoster() ?? throw new LibraryException("The library's member list on the share is missing or not validly signed.");
        var meta = c.ReadMeta(roster) ?? throw new LibraryException("The library's metadata on the share is missing or not validly signed.");
        var gen = meta.CurrentAudit;
        var nonce = RandomNumberGenerator.GetBytes(16);
        st.JoinNonce = Convert.ToBase64String(nonce);
        var req = new JoinRequest { LibraryId = c.Id, Id = c.Me, EncPub = keys.EncPub, SigPub = keys.SigPub, Gen = gen.Gen, Time = Now(), Commit = CommitOf(nonce),
                                    Name = LibraryCrypto.WrapFor(gen.Pub, Encoding.UTF8.GetBytes(myName), c.RequestContext(c.Me)) };
        req.Sig = keys.Sign(req.Canonical());
        ShareIo.Write(c.P("requests", c.Me + ".json"), req);
        persist(st);
        AuditLog.Write("you", "shared library " + profile.Name, "join requested", "fingerprint " + keys.Fingerprint);
        return c;
    }

    /// <summary>Checks the header against the fingerprint in the add-on and walks the ownership chain. The fingerprint may be
    /// the first owner's or any later owner's (a team add-on exported after a hand-over). Returns the current owner.</summary>
    private static OwnerKeys VerifyHeader(LibraryHeader h, string libraryId, string ownerFingerprint)
    {
        if (h.LibraryId != libraryId) throw new LibraryException("The folder holds a different library than this add-on names.");
        if (!LibraryCrypto.Verify(h.OwnerSigPub, h.Canonical(), h.Sig)) throw new LibraryException("The library header on the share is not validly signed.");
        var cur = new OwnerKeys(h.OwnerId, h.OwnerEncPub, h.OwnerSigPub);
        var fps = new List<string> { LibraryCrypto.NormalizeFingerprint(LibraryCrypto.Fingerprint(cur.EncPub, cur.SigPub)) };
        int seq = 0;
        foreach (var t in h.Transfers ?? new())
        {
            if (t.Seq != ++seq || t.FromId != cur.Id || !LibraryCrypto.Verify(cur.SigPub, t.Canonical(libraryId), t.Sig))
                throw new LibraryException("An ownership hand-over in the library header is not validly signed by the owner before it. Do not use this share; tell an admin.");
            cur = new OwnerKeys(t.ToId, t.ToEncPub, t.ToSigPub);
            fps.Add(LibraryCrypto.NormalizeFingerprint(LibraryCrypto.Fingerprint(cur.EncPub, cur.SigPub)));
        }
        if (!fps.Contains(LibraryCrypto.NormalizeFingerprint(ownerFingerprint)))
            throw new LibraryException("The owner key on the share does NOT match the fingerprint in the add-on. Do not use this share; tell the library's owner.");
        return cur;
    }

    /// <summary>The current owner, from the verified header chain.</summary>
    public static OwnerKeys CurrentOwner(LibraryHeader h)
    {
        var cur = new OwnerKeys(h.OwnerId, h.OwnerEncPub, h.OwnerSigPub);
        foreach (var t in h.Transfers ?? new())
        {
            if (t.FromId != cur.Id || !LibraryCrypto.Verify(cur.SigPub, t.Canonical(h.LibraryId), t.Sig)) break;
            cur = new OwnerKeys(t.ToId, t.ToEncPub, t.ToSigPub);
        }
        return cur;
    }

    // ---- signing helpers --------------------------------------------------------------------------------------------

    private void Sign(Roster r) { r.Signer = Me; r.Time = Now(); r.Sig = Keys.Sign(r.Canonical()); }
    private void Sign(Meta m) { m.Signer = Me; m.Time = Now(); m.Sig = Keys.Sign(m.Canonical()); }
    private void Sign(KeysFile k) { k.Signer = Me; k.Time = Now(); k.Sig = Keys.Sign(k.Canonical()); }
    private string CkContext(string cid, int epoch, string member) => $"ck|{Id}|{cid}|{epoch}|{member}";
    private string MkContext(int epoch, string member) => $"mk|{Id}|{epoch}|{member}";
    private string AuditContext(int gen, string member) => $"auditkey|{Id}|{gen}|{member}";
    private string RequestContext(string id) => $"request|{Id}|{id}";
    private string AuditLineContext(string member, long seq) => $"auditline|{Id}|{member}|{seq}";
    private string InfoAad(int epoch) => $"{LibraryCrypto.Format}|info|{Id}|{epoch}";

    private void SealMeta(Meta meta, MetaInfo info, byte[] mk, IEnumerable<RosterEntry> members)
    {
        meta.Mk = new(StringComparer.Ordinal);
        foreach (var m in members) meta.Mk[m.Id] = LibraryCrypto.WrapFor(m.EncPub, mk, MkContext(meta.MkEpoch, m.Id));
        meta.Info = LibraryCrypto.SealText(mk, ShareIo.ToJson(info), InfoAad(meta.MkEpoch));
    }

    private AuditGen NewAuditGen(int gen, KemKey key, IEnumerable<RosterEntry> admins)
    {
        var g = new AuditGen { Gen = gen, Pub = key.Pub };
        var priv = Encoding.UTF8.GetBytes(key.ExportPrivate());   // 0.6.12: the private form (hybrid); KemKey.ImportBytes also reads older raw PKCS#8
        foreach (var a in admins.Where(a => Roles.IsAdmin(a.Role))) g.Wraps[a.Id] = LibraryCrypto.WrapFor(a.EncPub, priv, AuditContext(gen, a.Id));
        return g;
    }

    // ---- verification -----------------------------------------------------------------------------------------------

    /// <summary>The roster entries whose grant chain leads back to the owner. Admin roles must be granted by the owner.</summary>
    public static Dictionary<string, RosterEntry> ValidMembers(Roster r, LibraryHeader h, List<string>? problems = null)
    {
        var all = r.Members.GroupBy(m => m.Id).ToDictionary(g => g.Key, g => g.First());
        var ok = new Dictionary<string, RosterEntry>();
        var owner = CurrentOwner(h);
        bool Check(RosterEntry e, HashSet<string> seen)
        {
            if (ok.ContainsKey(e.Id)) return true;
            if (!seen.Add(e.Id)) return false;
            bool valid;
            if (e.Id == owner.Id)
                valid = e.Role == Roles.Owner && e.GrantedBy == e.Id && e.EncPub == owner.EncPub && e.SigPub == owner.SigPub && LibraryCrypto.Verify(owner.SigPub, e.Canonical(r.LibraryId), e.GrantSig);
            else
                valid = e.Role != Roles.Owner && all.TryGetValue(e.GrantedBy, out var g) && g.Id != e.Id && Check(g, seen) && Roles.IsAdmin(g.Role)
                        && (e.Role != Roles.Admin || g.Role == Roles.Owner) && LibraryCrypto.Verify(g.SigPub, e.Canonical(r.LibraryId), e.GrantSig);
            if (valid) ok[e.Id] = e; else problems?.Add($"member entry {e.Id[..Math.Min(8, e.Id.Length)]} has no valid grant and is ignored");
            return valid;
        }
        foreach (var e in all.Values) Check(e, new HashSet<string>());
        return ok;
    }

    private Roster? ReadRoster(List<string>? problems = null)
    {
        // The header is re-read every time: an ownership hand-over is appended to it.
        var h = ShareIo.Read<LibraryHeader>(P("library.json")) ?? State.Header;
        if (h == null) return null;
        try { VerifyHeader(h, Id, State.OwnerFingerprint); }
        catch (LibraryException ex) when (State.Header != null && !ReferenceEquals(h, State.Header))
        { problems?.Add(ex.Message + " The cached header is used."); h = State.Header; }
        if (State.Header != null && (h.Transfers?.Count ?? 0) < (State.Header.Transfers?.Count ?? 0))
        { problems?.Add("the library header on the share has FEWER ownership hand-overs than one already seen: rolled back? The cached header is used"); h = State.Header; }
        State.Header = h;
        var r = ShareIo.Read<Roster>(P("roster.json"));
        if (r == null || r.LibraryId != Id) { problems?.Add("the member list is missing from the share"); return null; }
        var valid = ValidMembers(r, h, problems);
        if (!valid.TryGetValue(r.Signer, out var signer) || !Roles.IsAdmin(signer.Role) || !LibraryCrypto.Verify(signer.SigPub, r.Canonical(), r.Sig))
        { problems?.Add("the member list on the share is not signed by an admin — ignored, the cached list is used"); return null; }
        if (State.Roster != null && r.Seq < State.Roster.Seq)
        { problems?.Add($"the member list on the share (version {r.Seq}) is OLDER than one already seen ({State.Roster.Seq}): rolled back? The newer cached list is used"); return null; }
        r.Members = r.Members.Where(m => valid.ContainsKey(m.Id)).ToList();
        return r;
    }

    private Meta? ReadMeta(Roster roster, List<string>? problems = null)
    {
        var m = ShareIo.Read<Meta>(P("meta.json"));
        if (m == null || m.LibraryId != Id || m.Audit.Count == 0) { problems?.Add("the library metadata is missing from the share"); return null; }
        if (roster.Members.FirstOrDefault(x => x.Id == m.Signer) is not { } s || !Roles.IsAdmin(s.Role) || !LibraryCrypto.Verify(s.SigPub, m.Canonical(), m.Sig))
        { problems?.Add("the library metadata is not signed by an admin — ignored"); return null; }
        if (m.Seq < State.MetaSeq) { problems?.Add($"the library metadata (version {m.Seq}) is older than one already seen ({State.MetaSeq}): rolled back? Ignored"); return null; }
        return m;
    }

    private KeysFile? ReadKeys(string cid, Roster roster, List<string>? problems = null)
    {
        var k = ShareIo.Read<KeysFile>(P("collections", cid, "keys.json"));
        if (k == null || k.LibraryId != Id || k.Cid != cid) return null;
        if (roster.Members.FirstOrDefault(x => x.Id == k.Signer) is not { } s || !Roles.IsAdmin(s.Role) || !LibraryCrypto.Verify(s.SigPub, k.Canonical(), k.Sig))
        { problems?.Add($"the keys of {CollectionName(cid)} are not signed by an admin — ignored"); return null; }
        return k;
    }

    /// <summary>Who may have written <paramref name="f"/>: a current editor or admin, or a removed member for epochs they still had.</summary>
    private string? SignerKey(ItemFile f, Roster r)
    {
        if (r.Members.FirstOrDefault(m => m.Id == f.Author) is { } e)
            return r.Editable(e, f.Cid) ? e.SigPub : null;
        if (r.Removed.FirstOrDefault(m => m.Id == f.Author) is { } x && (Roles.IsAdmin(x.Role) || x.Access.GetValueOrDefault(f.Cid) == Roles.Editor)
            && f.Epoch < x.Epochs.GetValueOrDefault(f.Cid, int.MaxValue)) return x.SigPub;
        return null;
    }

    private ItemPayload? Decrypt(ItemFile f)
    {
        if (!State.Keys.TryGetValue(f.Cid, out var byEpoch) || !byEpoch.TryGetValue(f.Epoch, out var k)) return null;
        try { return ShareIo.FromJson<ItemPayload>(LibraryCrypto.OpenText(Convert.FromBase64String(k), f.Body, f.Aad)); }
        catch (Exception ex) when (ex is CryptographicException or System.Text.Json.JsonException or FormatException) { return null; }
    }

    // ---- sync -------------------------------------------------------------------------------------------------------

    /// <summary>Reads the share, verifies everything, merges into the cached copy and saves it. Returns false when the share
    /// couldn't be used (the cached copy stays in use, labelled with its time).</summary>
    public bool Sync()
    {
        lock (_gate)
        {
            if (Unreachable() is { } why) { State.LastError = why; _persist(State); return false; }
            var problems = new List<string>();
            try
            {
                FlushOutbox(problems);
                var roster = ReadRoster(problems);
                if (roster != null) State.Roster = roster;
                roster = State.Roster;
                if (roster == null) throw new LibraryException(problems.FirstOrDefault() ?? "the member list can't be read");
                var me = roster.Members.FirstOrDefault(m => m.Id == Me);
                if (me == null)
                {
                    // Removed (or no longer listed after being a member): forget everything this PC could read. A pending
                    // request just waits.
                    if (!State.Pending || roster.Removed.Any(x => x.Id == Me))
                    {
                        State.RemovedFromLibrary = true; State.Pending = false;
                        State.Items.Clear(); State.Keys.Clear(); State.Epochs.Clear(); State.Outbox.Clear(); State.Collections.Clear(); State.Rotation.Clear();
                    }
                    else AnswerChallenge(roster, problems);   // 0.3.0 §5: an admin started the short-code check

                    State.Problems = problems; State.LastError = null; State.LastSync = DateTime.Now;
                    _persist(State);
                    return true;
                }
                State.Pending = false; State.RemovedFromLibrary = false; State.JoinNonce = "";

                if (ReadMeta(roster, problems) is { } meta && meta.Mk.TryGetValue(Me, out var mkw))
                {
                    var mk = LibraryCrypto.Unwrap(Keys.Enc, mkw, MkContext(meta.MkEpoch, Me));
                    var info = ShareIo.FromJson<MetaInfo>(LibraryCrypto.OpenText(mk, meta.Info, InfoAad(meta.MkEpoch))) ?? new();
                    State.Name = info.Name; State.Collections = info.Collections; State.MemberNames = info.Members; State.MetaSeq = meta.Seq;
                    State.AuditPub = meta.CurrentAudit.Pub; State.AuditGen = meta.CurrentAudit.Gen;
                }

                foreach (var cid in State.Collections.Keys.ToList())
                {
                    if (!roster.Readable(me, cid)) { Forget(cid); continue; }
                    if (ReadKeys(cid, roster, problems) is not { } kf) continue;
                    var mine = new Dictionary<int, string>();
                    foreach (var (epoch, wraps) in kf.Epochs)
                        if (wraps.TryGetValue(Me, out var w))
                            try { mine[epoch] = Convert.ToBase64String(LibraryCrypto.Unwrap(Keys.Enc, w, CkContext(cid, epoch, Me))); }
                            catch (CryptographicException) { problems.Add($"a key of {CollectionName(cid)} (epoch {epoch}) could not be opened"); }
                    State.Keys[cid] = mine;
                    State.Epochs[cid] = kf.Epoch;
                    SyncItems(cid, roster, problems);
                }
                foreach (var gone in State.Keys.Keys.Where(c => !State.Collections.ContainsKey(c)).ToList()) Forget(gone);
                FlushAudit();
                State.LastError = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or LibraryException or CryptographicException or System.Text.Json.JsonException)
            {
                State.LastError = ex.Message;
            }
            State.Problems = problems.Distinct().Take(50).ToList();
            State.LastSync = DateTime.Now;
            _persist(State);
            return State.LastError == null;
        }
    }

    private void Forget(string cid)
    {
        State.Keys.Remove(cid); State.Epochs.Remove(cid);
        foreach (var k in State.Items.Where(i => i.Value.Cid == cid).Select(i => i.Key).ToList()) State.Items.Remove(k);
    }

    private void SyncItems(string cid, Roster roster, List<string> problems)
    {
        var dir = P("collections", cid, "items");
        var files = Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*.dcxi").Take(50_000).ToList() : new();
        var revs = new Dictionary<string, List<(ItemFile F, ItemPayload P)>>();
        int rejected = 0;
        foreach (var path in files)
        {
            ItemFile? f;
            try { f = ShareIo.Read<ItemFile>(path); } catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException) { rejected++; continue; }
            if (f == null || f.LibraryId != Id || f.Cid != cid || f.Item.Length != 32) { rejected++; continue; }
            if (SignerKey(f, roster) is not { } pub || !LibraryCrypto.Verify(pub, f.Canonical(), f.Sig)) { rejected++; continue; }
            if (Decrypt(f) is not { } payload) { rejected++; continue; }
            var list = revs.TryGetValue(f.Item, out var l) ? l : revs[f.Item] = new();
            if (!list.Any(x => x.F.RevId == f.RevId)) list.Add((f, payload));
        }
        if (rejected > 0) problems.Add($"{rejected} file(s) in {CollectionName(cid)} were rejected: not signed by an editor of the collection, damaged, or not readable with your keys");

        foreach (var (item, list) in revs)
        {
            var based = list.SelectMany(x => x.F.Base).ToHashSet();
            var heads = list.Where(x => !based.Contains(x.F.RevId)).OrderByDescending(x => x.F.Rev).ThenByDescending(x => x.F.Time, StringComparer.Ordinal).ToList();
            if (heads.Count == 0) continue;
            State.Items.TryGetValue(item, out var cached);
            if (cached is { Pending: true } && State.Outbox.Any(o => o.Item == item)) continue;   // our unsent edit wins locally until it is sent
            if (heads.Count == 1 && heads[0].P.Kind == "tomb") { State.Items.Remove(item); continue; }
            var top = heads[0];
            if (cached != null && cached.Rev > top.F.Rev && !list.Any(x => x.F.RevId == cached.RevId))
            {
                cached.Warning = $"The share holds an OLDER version (revision {top.F.Rev}) than one already seen ({cached.Rev}): rolled back? The newer cached version is kept.";
                problems.Add($"“{Title(cached)}” was rolled back on the share");
                continue;
            }
            var ci = ToCached(top.F, top.P);
            ci.Conflicts = heads.Skip(1).Where(h => h.P.Kind != "tomb").Select(h => ToCached(h.F, h.P)).ToList();
            if (ci.Conflicts.Count > 0) problems.Add($"“{Title(ci)}” has {ci.Conflicts.Count + 1} versions saved at the same time: pick one in Shared library");
            State.Items[item] = ci;
        }
        foreach (var lost in State.Items.Values.Where(i => i.Cid == cid && !i.Pending && !revs.ContainsKey(i.Item)).ToList())
        {
            lost.Warning = "This item VANISHED from the share without a signed delete. The cached copy is kept; an admin can accept the removal.";
            problems.Add($"“{Title(lost)}” vanished from the share");
        }
    }

    private CachedItem ToCached(ItemFile f, ItemPayload p) => new() { Cid = f.Cid, Item = f.Item, Rev = f.Rev, RevId = f.RevId, Author = f.Author, Time = f.Time, Payload = p };

    public static string Title(CachedItem i) => i.Payload.Kind == "cred" ? i.Payload.Cred?.Name ?? "credential" : i.Payload.Host?.Display ?? "host";

    // ---- writing items ----------------------------------------------------------------------------------------------

    /// <summary>Saves a host or credential (new when <paramref name="item"/> is null) into a collection. All current
    /// versions (a conflict's heads) become its base, so saving also resolves a conflict.</summary>
    public string SaveItem(string cid, string? item, ItemPayload payload)
    {
        lock (_gate)
        {
            if (!CanEdit(cid)) throw new LibraryException($"You can't change {CollectionName(cid)}: your role there is {(CanRead(cid) ? "reader" : "none")}.");
            if (!State.Epochs.TryGetValue(cid, out var epoch) || !State.Keys.TryGetValue(cid, out var ks) || !ks.TryGetValue(epoch, out var key))
                throw new LibraryException("This PC doesn't hold the key of that collection yet. Sync first.");
            item ??= LibraryCrypto.NewId();
            State.Items.TryGetValue(item, out var cur);
            if (cur != null && cur.Cid != cid) throw new LibraryException("That item belongs to another collection.");
            payload.EditedBy = NameOf(Me);
            var f = new ItemFile { LibraryId = Id, Cid = cid, Item = item, Rev = (cur == null ? 0 : cur.Conflicts.Select(c => c.Rev).Append(cur.Rev).Max()) + 1,
                                   Author = Me, Epoch = epoch, Base = cur?.Heads.ToList() ?? new(), Time = Now() };
            f.Body = LibraryCrypto.SealText(Convert.FromBase64String(key), ShareIo.ToJson(payload), f.Aad);
            f.Sig = Keys.Sign(f.Canonical());
            if (payload.Kind == "tomb") State.Items.Remove(item);
            else State.Items[item] = ToCached(f, payload);
            if (Unreachable() == null)
            {
                try { Publish(f); }
                catch (IOException) { Queue(f); }
            }
            else Queue(f);
            _persist(State);
            Audit(payload.Kind == "tomb" ? "item deleted" : cur == null ? "item added" : cur.Conflicts.Count > 0 ? "conflict resolved" : "item changed",
                  $"{CollectionName(cid)} / {(payload.Kind == "tomb" ? cur != null ? Title(cur) : item : Title(State.Items[item]))}");
            return item;
        }
    }

    private void Queue(ItemFile f)
    {
        State.Outbox.Add(f);
        if (State.Items.TryGetValue(f.Item, out var c)) c.Pending = true;
    }

    private void Publish(ItemFile f)
    {
        var dir = P("collections", f.Cid, "items");
        var path = Path.Combine(dir, f.FileName);
        if (!File.Exists(path)) ShareIo.Write(path, f);
        // Superseded revisions this writer knew about; a sync tool's copies or a concurrent writer's files stay as heads.
        foreach (var b in f.Base)
        {
            var parts = b.Split('.', 2);
            if (parts.Length == 2 && int.TryParse(parts[0], out _))
                try { File.Delete(Path.Combine(dir, $"{f.Item}.{parts[0]}.{(parts[1].Length >= 8 ? parts[1][..8] : parts[1])}.dcxi")); } catch (IOException) { }
        }
        if (State.Items.TryGetValue(f.Item, out var c)) c.Pending = false;
    }

    private void FlushOutbox(List<string> problems)
    {
        foreach (var f in State.Outbox.ToList())
        {
            try { Publish(f); State.Outbox.Remove(f); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { problems.Add("an edit made while away couldn't be sent yet: " + ex.Message); break; }
        }
    }

    public string SaveHost(string cid, string? item, HostEntry host) => SaveItem(cid, item, new ItemPayload { Kind = "host", Host = host });
    public string SaveCredential(string cid, string? item, Credential cred) => SaveItem(cid, item, new ItemPayload { Kind = "cred", Cred = cred });
    public void DeleteItem(string item)
    {
        if (!State.Items.TryGetValue(item, out var cur)) return;
        SaveItem(cur.Cid, item, new ItemPayload { Kind = "tomb" });
    }

    /// <summary>Keeps one of a conflict's versions (by revision id); the others are superseded by a new signed revision.</summary>
    public void ResolveConflict(string item, string keepRevId)
    {
        if (!State.Items.TryGetValue(item, out var cur)) return;
        var keep = cur.RevId == keepRevId ? cur : cur.Conflicts.FirstOrDefault(c => c.RevId == keepRevId) ?? throw new LibraryException("That version is no longer there. Sync first.");
        SaveItem(cur.Cid, item, keep.Payload);
    }

    /// <summary>Admin: accept that an item vanished from the share (drops the cached copy).</summary>
    public void AcceptVanished(string item)
    {
        if (!IsAdmin) throw new LibraryException("Only an admin can accept a removal.");
        if (State.Items.Remove(item)) { _persist(State); Audit("vanished item accepted", item); }
    }

    // ---- admin: members, collections, rotation ----------------------------------------------------------------------

    private void RequireAdmin() { if (!IsAdmin) throw new LibraryException("Only the owner or an admin can do this."); }

    /// <summary>Fresh, verified copies of roster and meta for an admin change; refuses when another admin changed them since the last sync.</summary>
    private (Roster R, Meta M, byte[] Mk, MetaInfo Info) Begin()
    {
        RequireAdmin();
        if (Unreachable() is { } why) throw new LibraryException("Member and collection changes need the share: " + why);
        // Start from the share's current state, so a re-key covers items other members added since this PC last looked.
        if (!Sync()) throw new LibraryException("The share couldn't be synced: " + State.LastError);
        RequireAdmin();
        var problems = new List<string>();
        var r = ReadRoster(problems) ?? throw new LibraryException(problems.FirstOrDefault() ?? "the member list can't be read");
        if (State.Roster != null && r.Seq != State.Roster.Seq) throw new LibraryException("Another admin changed the member list since your last sync. Sync, then try again.");
        var m = ReadMeta(r, problems) ?? throw new LibraryException(problems.FirstOrDefault() ?? "the metadata can't be read");
        var mk = LibraryCrypto.Unwrap(Keys.Enc, m.Mk[Me], MkContext(m.MkEpoch, Me));
        var info = ShareIo.FromJson<MetaInfo>(LibraryCrypto.OpenText(mk, m.Info, InfoAad(m.MkEpoch))) ?? new();
        return (r, m, mk, info);
    }

    private void Commit(Roster r, Meta m)
    {
        r.Seq++; m.Seq++;
        Sign(r); Sign(m);
        ShareIo.Write(P("roster.json"), r);
        ShareIo.Write(P("meta.json"), m);
        State.Roster = ValidRoster(r);
    }

    private Roster ValidRoster(Roster r) { var v = ValidMembers(r, State.Header!); r.Members = r.Members.Where(x => v.ContainsKey(x.Id)).ToList(); return r; }

    private RosterEntry Grant(RosterEntry e) { e.GrantedBy = Me; e.Granted = Now(); e.GrantSig = Keys.Sign(e.Canonical(Id)); return e; }

    private byte[] AuditPrivate(Meta m, int gen)
    {
        var g = m.Audit.First(a => a.Gen == gen);
        if (!g.Wraps.TryGetValue(Me, out var w)) throw new LibraryException("You don't hold the audit key.");
        return LibraryCrypto.Unwrap(Keys.Enc, w, AuditContext(gen, Me));
    }

    public List<RequestInfo> Requests()
    {
        var o = new List<RequestInfo>();
        if (Unreachable() != null || !Directory.Exists(P("requests"))) return o;
        Meta? m = IsAdmin && State.Roster != null ? ReadMeta(State.Roster) : null;
        foreach (var path in Directory.EnumerateFiles(P("requests"), "*.json").Take(500))
        {
            JoinRequest? q;
            try { q = ShareIo.Read<JoinRequest>(path); } catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException) { continue; }
            if (q == null || q.LibraryId != Id || q.Id.Length != 32 || Path.GetFileNameWithoutExtension(path) != q.Id) continue;
            var valid = LibraryCrypto.Verify(q.SigPub, q.Canonical(), q.Sig);
            var name = "(name sealed for admins)";
            if (m != null && valid)
                try
                {
                    using var ak = KemKey.ImportBytes(AuditPrivate(m, q.Gen));
                    name = Encoding.UTF8.GetString(LibraryCrypto.Unwrap(ak, q.Name, RequestContext(q.Id)));
                }
                catch (Exception ex) when (ex is CryptographicException or LibraryException or InvalidOperationException) { name = "(name can't be read)"; }
            o.Add(new RequestInfo(q, name, valid));
        }
        return o;
    }

    /// <summary>Approves a join request after the fingerprint was checked out of band.</summary>
    public void Approve(string requestId, string fingerprintChecked, string role, IDictionary<string, string> access, string? name = null)
    {
        lock (_gate)
        {
            var req = Requests().FirstOrDefault(r => r.Request.Id == requestId) ?? throw new LibraryException("That request is no longer on the share.");
            if (!req.Valid) throw new LibraryException("That request is not validly signed.");
            if (LibraryCrypto.NormalizeFingerprint(fingerprintChecked) != LibraryCrypto.NormalizeFingerprint(req.Request.Fingerprint))
                throw new LibraryException("The fingerprint you checked does not match the request. Do not approve it.");
            if (role == Roles.Admin && !IsOwner) throw new LibraryException("Only the owner can appoint admins.");
            if (role == Roles.Owner) throw new LibraryException("A library has one owner.");
            var (r, m, mk, info) = Begin();
            if (r.Members.Any(x => x.Id == requestId)) throw new LibraryException("That member is already in the library.");
            var e = Grant(new RosterEntry { Id = requestId, EncPub = req.Request.EncPub, SigPub = req.Request.SigPub, Role = role,
                                           Access = new(access.Where(a => info.Collections.ContainsKey(a.Key) && a.Value is Roles.Editor or Roles.Reader).ToDictionary(a => a.Key, a => a.Value), StringComparer.Ordinal) });
            r.Members.Add(e);
            info.Members[e.Id] = (name ?? req.Name).Trim() is { Length: > 0 } n && !n.StartsWith("(") ? n : "member " + e.Id[..8];
            m.Mk[e.Id] = LibraryCrypto.WrapFor(e.EncPub, mk, MkContext(m.MkEpoch, e.Id));
            m.Info = LibraryCrypto.SealText(mk, ShareIo.ToJson(info), InfoAad(m.MkEpoch));
            if (Roles.IsAdmin(role)) ShareAuditKeys(m, e);
            foreach (var cid in info.Collections.Keys.Where(c => r.Readable(e, c))) AddReader(cid, r, e);
            Commit(r, m);
            try { File.Delete(P("requests", requestId + ".json")); } catch (IOException) { }
            Sync();
            Audit("member approved", $"{info.Members[e.Id]} as {role} · fingerprint {req.Request.Fingerprint}");
        }
    }

    public void Deny(string requestId)
    {
        RequireAdmin();
        foreach (var f in new[] { requestId + ".json", requestId + ".challenge.json", requestId + ".reveal.json" })
            try { File.Delete(P("requests", f)); } catch (IOException) { }
        lock (_gate) { if (State.Challenges.Remove(requestId)) _persist(State); }
        Audit("join request denied", requestId);
    }

    private void ShareAuditKeys(Meta m, RosterEntry admin)
    {
        foreach (var g in m.Audit) g.Wraps[admin.Id] = LibraryCrypto.WrapFor(admin.EncPub, AuditPrivate(m, g.Gen), AuditContext(g.Gen, admin.Id));
    }

    /// <summary>Wraps every epoch's key of a collection for a member who gains read access.</summary>
    private void AddReader(string cid, Roster r, RosterEntry e)
    {
        var kf = ReadKeys(cid, r) ?? throw new LibraryException($"The keys of {CollectionName(cid)} can't be read.");
        foreach (var (epoch, wraps) in kf.Epochs)
        {
            if (!wraps.TryGetValue(Me, out var mine)) continue;
            var ck = LibraryCrypto.Unwrap(Keys.Enc, mine, CkContext(cid, epoch, Me));
            wraps[e.Id] = LibraryCrypto.WrapFor(e.EncPub, ck, CkContext(cid, epoch, e.Id));
        }
        Sign(kf);
        ShareIo.Write(P("collections", cid, "keys.json"), kf);
    }

    public string CreateCollection(string name)
    {
        lock (_gate)
        {
            var (r, m, mk, info) = Begin();
            if (name.Trim().Length is 0 or > 60) throw new LibraryException("A collection name of 1–60 characters is needed.");
            var cid = LibraryCrypto.NewId();
            var ck = LibraryCrypto.RandomKey();
            var kf = new KeysFile { LibraryId = Id, Cid = cid, Epoch = 1 };
            kf.Epochs[1] = new(StringComparer.Ordinal);
            foreach (var a in r.Members.Where(x => Roles.IsAdmin(x.Role))) kf.Epochs[1][a.Id] = LibraryCrypto.WrapFor(a.EncPub, ck, CkContext(cid, 1, a.Id));
            Sign(kf);
            ShareIo.Write(P("collections", cid, "keys.json"), kf);
            Directory.CreateDirectory(P("collections", cid, "items"));
            info.Collections[cid] = name.Trim();
            m.Info = LibraryCrypto.SealText(mk, ShareIo.ToJson(info), InfoAad(m.MkEpoch));
            Commit(r, m);
            Sync();
            Audit("collection created", name.Trim());
            return cid;
        }
    }

    /// <summary>Changes a member's role and collection access. Losing read access to a collection rotates its key; losing
    /// admin rotates the audit key. Returns the credentials that member could read and should now be changed.</summary>
    public List<RotationTask> SetAccess(string memberId, string role, IDictionary<string, string> access) => Change(memberId, remove: false, role, access);

    /// <summary>Removes a member: every collection they could read moves to a new key epoch and is re-encrypted, the
    /// metadata key is replaced, and the credentials they could see go on the rotation checklist.</summary>
    public List<RotationTask> Remove(string memberId) => Change(memberId, remove: true, Roles.Member, new Dictionary<string, string>());

    private List<RotationTask> Change(string memberId, bool remove, string role, IDictionary<string, string> access)
    {
        lock (_gate)
        {
            var (r, m, mk, info) = Begin();
            var old = r.Members.FirstOrDefault(x => x.Id == memberId) ?? throw new LibraryException("That member is not in the library.");
            if (old.Role == Roles.Owner) throw new LibraryException("The owner can't be removed or changed.");
            if ((old.Role == Roles.Admin || role == Roles.Admin) && !IsOwner) throw new LibraryException("Only the owner can appoint, change or remove admins.");
            var name = info.Members.GetValueOrDefault(memberId) ?? memberId[..8];
            var updated = old.Copy();
            updated.Role = role;
            updated.Access = new(access.Where(a => info.Collections.ContainsKey(a.Key) && a.Value is Roles.Editor or Roles.Reader).ToDictionary(a => a.Key, a => a.Value), StringComparer.Ordinal);
            // Group access counts (0.3.0 §22): a removed member leaves every group; a changed one keeps their groups.
            var before = new Roster { LibraryId = r.LibraryId, Seq = r.Seq, Members = r.Members.ToList(), Removed = r.Removed.ToList(), Groups = r.Groups.Select(g => g.Copy()).ToList() };
            if (remove) foreach (var g in r.Groups) g.Members.Remove(memberId);
            var lost = info.Collections.Keys.Where(c => before.Readable(old, c) && (remove || !r.Readable(updated, c))).ToList();
            var gained = remove ? new List<string>() : info.Collections.Keys.Where(c => !before.Readable(old, c) && r.Readable(updated, c)).ToList();
            bool lostAdmin = Roles.IsAdmin(old.Role) && (remove || !Roles.IsAdmin(role));
            // Files this admin signed (key files) and revisions they wrote must keep verifying once they are no longer allowed
            // to sign them: found now, against the roster as it was, and signed again by this admin after the change.
            var keysByThem = lostAdmin ? info.Collections.Keys.Where(c => ReadKeys(c, before)?.Signer == memberId).ToList() : new List<string>();
            var lostEdit = remove ? new List<string>() : info.Collections.Keys.Where(c => !lost.Contains(c) && before.Editable(old, c) && !r.Editable(updated, c)).ToList();

            r.Members.Remove(old);
            if (remove)
            {
                // Members' names are sealed under the metadata key: a removed member must not read later changes.
                r.Removed.Add(new RemovedEntry { Id = old.Id, EncPub = old.EncPub, SigPub = old.SigPub, Role = old.Role, Access = before.EffectiveAccess(old), Removed = Now(),
                                                 Epochs = new(lost.ToDictionary(c => c, c => (ReadKeys(c, r)?.Epoch ?? State.Epochs.GetValueOrDefault(c, 1)) + 1), StringComparer.Ordinal) });
                m.MkEpoch++;
                mk = LibraryCrypto.RandomKey();
                SealMeta(m, info, mk, r.Members);
            }
            else
            {
                r.Members.Add(Grant(updated));
                if (Roles.IsAdmin(role) && !Roles.IsAdmin(old.Role)) ShareAuditKeys(m, updated);
            }
            // Members this one approved keep a valid grant chain: the owner re-signs their grants.
            if (lostAdmin)
                for (int i = 0; i < r.Members.Count; i++)
                    if (r.Members[i].GrantedBy == memberId && r.Members[i].Id != memberId) r.Members[i] = Grant(r.Members[i].Copy());
            if (lostAdmin)
            {
                using var ak = KemKey.Create();
                foreach (var g in m.Audit) g.Wraps.Remove(memberId);
                m.Audit.Add(NewAuditGen(m.Audit.Max(a => a.Gen) + 1, ak, r.Members));
            }
            Commit(r, m);
            foreach (var cid in keysByThem)
                if (ReadKeys(cid, before) is { } kf) { Sign(kf); ShareIo.Write(P("collections", cid, "keys.json"), kf); }
            foreach (var cid in lostEdit) Reissue(cid, memberId);
            foreach (var cid in gained) AddReader(cid, r, updated);
            var tasks = new List<RotationTask>();
            foreach (var cid in lost)
            {
                Rotate(cid, r, memberId);
                tasks.AddRange(State.Items.Values.Where(i => i.Cid == cid && i.Payload.Kind == "cred")
                    .Select(i => new RotationTask { Item = i.Item, Name = Title(i), Collection = info.Collections[cid], Because = remove ? $"{name} was removed" : $"{name} lost access" }));
            }
            State.Rotation.AddRange(tasks);
            Sync();
            Audit(remove ? "member removed" : "member access changed",
                  $"{name}{(remove ? "" : $" → {role}")} · {lost.Count} collection(s) re-keyed · {tasks.Count} credential(s) to rotate");
            return tasks;
        }
    }

    /// <summary>A new epoch for a collection: a new key wrapped for everyone who still reads it, the leaver's wraps removed
    /// from older epochs, and every current item re-encrypted under the new key.</summary>
    /// <summary>Groups (0.3.0 §22): creates or changes a group — its members and per-collection access. Members who gain
    /// read access get the keys; members who lose it move that collection to a new key epoch (same mechanics as a direct
    /// change). Returns the credentials that became unreadable to someone and should be rotated.</summary>
    public List<RotationTask> SetGroup(string name, IEnumerable<string> members, IDictionary<string, string> access) =>
        ChangeGroups(r =>
        {
            name = name.Trim();
            if (name.Length is 0 or > 60 || name.Any(char.IsControl)) throw new LibraryException("A group name of 1–60 characters is needed.");
            var g = r.Groups.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? new RosterGroup { Name = name };
            r.Groups.Remove(g);
            g.Members = members.Where(m => r.Members.Any(x => x.Id == m)).Distinct().ToList();
            g.Access = new(access.Where(a => State.Collections.ContainsKey(a.Key) && a.Value is Roles.Editor or Roles.Reader).ToDictionary(a => a.Key, a => a.Value), StringComparer.Ordinal);
            r.Groups.Add(g);
        }, "group set", name);

    public List<RotationTask> RemoveGroup(string name) =>
        ChangeGroups(r => { if (r.Groups.RemoveAll(g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) == 0) throw new LibraryException("No such group."); }, "group removed", name);

    private List<RotationTask> ChangeGroups(Action<Roster> mutate, string what, string name)
    {
        lock (_gate)
        {
            var (r, m, _, info) = Begin();
            var before = new Roster { LibraryId = r.LibraryId, Seq = r.Seq, Members = r.Members.ToList(), Removed = r.Removed.ToList(), Groups = r.Groups.Select(g => g.Copy()).ToList() };
            mutate(r);
            var cids = info.Collections.Keys.ToList();
            var others = r.Members.Where(e => !Roles.IsAdmin(e.Role)).ToList();
            var lost = others.SelectMany(e => cids.Where(c => before.Readable(e, c) && !r.Readable(e, c)).Select(c => (Member: e, Cid: c))).ToList();
            var gained = others.SelectMany(e => cids.Where(c => !before.Readable(e, c) && r.Readable(e, c)).Select(c => (Member: e, Cid: c))).ToList();
            var lostEdit = others.SelectMany(e => cids.Where(c => r.Readable(e, c) && before.Editable(e, c) && !r.Editable(e, c)).Select(c => (Member: e, Cid: c))).ToList();
            Commit(r, m);
            foreach (var (e, cid) in lostEdit) Reissue(cid, e.Id);
            foreach (var (e, cid) in gained) AddReader(cid, r, e);
            var tasks = new List<RotationTask>();
            foreach (var g in lost.GroupBy(x => x.Cid))
            {
                Rotate(g.Key, r, g.Select(x => x.Member.Id));
                tasks.AddRange(State.Items.Values.Where(i => i.Cid == g.Key && i.Payload.Kind == "cred")
                    .Select(i => new RotationTask { Item = i.Item, Name = Title(i), Collection = info.Collections[g.Key], Because = $"{string.Join(", ", g.Select(x => NameOf(x.Member.Id)))} lost access (group “{name}”)" }));
            }
            State.Rotation.AddRange(tasks);
            Sync();
            Audit(what, $"{name} · {gained.Count} reader(s) added · {lost.GroupBy(x => x.Cid).Count()} collection(s) re-keyed · {tasks.Count} credential(s) to rotate");
            return tasks;
        }
    }

    private void Rotate(string cid, Roster r, string leaver) => Rotate(cid, r, new[] { leaver });

    private void Rotate(string cid, Roster r, IEnumerable<string> leavers)
    {
        var kf = ReadKeys(cid, r) ?? throw new LibraryException($"The keys of {CollectionName(cid)} can't be read.");
        foreach (var w in kf.Epochs.Values) foreach (var leaver in leavers) w.Remove(leaver);
        var epoch = kf.Epoch + 1;
        var ck = LibraryCrypto.RandomKey();
        kf.Epochs[epoch] = new(StringComparer.Ordinal);
        foreach (var e in r.Members.Where(x => r.Readable(x, cid))) kf.Epochs[epoch][e.Id] = LibraryCrypto.WrapFor(e.EncPub, ck, CkContext(cid, epoch, e.Id));
        kf.Epoch = epoch;
        Sign(kf);
        ShareIo.Write(P("collections", cid, "keys.json"), kf);
        if (!State.Keys.TryGetValue(cid, out var ks)) State.Keys[cid] = ks = new();
        ks[epoch] = Convert.ToBase64String(ck);
        State.Epochs[cid] = epoch;
        foreach (var it in State.Items.Values.Where(i => i.Cid == cid).ToList())
            foreach (var head in it.Conflicts.Prepend(it).ToList())
                Rewrite(head, epoch, ck, "re-encrypted after a member change");
    }

    /// <summary>One head written again as a new revision signed by this admin (same content), superseding only that head.</summary>
    private void Rewrite(CachedItem head, int epoch, byte[] ck, string note)
    {
        var f = new ItemFile { LibraryId = Id, Cid = head.Cid, Item = head.Item, Rev = head.Rev + 1, Author = Me, Epoch = epoch, Base = new() { head.RevId }, Time = Now() };
        var payload = head.Payload; payload.Note = note;
        f.Body = LibraryCrypto.SealText(ck, ShareIo.ToJson(payload), f.Aad);
        f.Sig = Keys.Sign(f.Canonical());
        Publish(f);
    }

    /// <summary>Someone lost the right to edit a collection but still reads it: the revisions they wrote would stop verifying,
    /// so this admin signs them again (same key epoch, same content).</summary>
    private void Reissue(string cid, string author)
    {
        if (!State.Epochs.TryGetValue(cid, out var epoch) || !State.Keys.TryGetValue(cid, out var ks) || !ks.TryGetValue(epoch, out var k)) return;
        var ck = Convert.FromBase64String(k);
        foreach (var it in State.Items.Values.Where(i => i.Cid == cid).ToList())
            foreach (var head in it.Conflicts.Prepend(it).Where(h => h.Author == author).ToList())
                Rewrite(head, epoch, ck, "signed again after a role change");
    }

    public void RotationDone(RotationTask t) { t.Done = DateTime.Now; _persist(State); Audit("credential rotated", $"{t.Collection} / {t.Name}"); }

    // ---- ownership hand-over ----------------------------------------------------------------------------------------

    private string OfferPath => P("transfer-offer.json");

    /// <summary>A valid, unaccepted hand-over offered by the current owner, or null.</summary>
    public OwnerTransfer? PendingTransfer()
    {
        if (Unreachable() != null || State.Header is not { } h) return null;
        OwnerTransfer? t;
        try { t = ShareIo.Read<OwnerTransfer>(OfferPath); } catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException) { return null; }
        var owner = CurrentOwner(h);
        return t != null && t.Seq == (h.Transfers?.Count ?? 0) + 1 && t.FromId == owner.Id && LibraryCrypto.Verify(owner.SigPub, t.Canonical(Id), t.Sig) ? t : null;
    }

    /// <summary>Owner: offers the library to an admin. Nothing changes until that admin accepts on their PC.</summary>
    public void OfferOwnership(string adminId)
    {
        lock (_gate)
        {
            if (!IsOwner) throw new LibraryException("Only the owner can hand the library over.");
            var (r, _, _, _) = Begin();
            var to = r.Members.FirstOrDefault(m => m.Id == adminId) ?? throw new LibraryException("That member is not in the library.");
            if (to.Role != Roles.Admin) throw new LibraryException("Make them an admin first: ownership goes to an admin.");
            var t = new OwnerTransfer { Seq = (State.Header!.Transfers?.Count ?? 0) + 1, FromId = Me, ToId = to.Id, ToEncPub = to.EncPub, ToSigPub = to.SigPub, Time = Now() };
            t.Sig = Keys.Sign(t.Canonical(Id));
            ShareIo.Write(OfferPath, t);
            Audit("ownership offered", NameOf(to.Id));
        }
    }

    public void WithdrawOwnershipOffer()
    {
        if (!IsOwner) throw new LibraryException("Only the owner can withdraw the offer.");
        try { File.Delete(OfferPath); } catch (IOException) { }
        Audit("ownership offer withdrawn", "");
    }

    /// <summary>The admin named in the offer becomes the owner: the signed offer goes into the header, their roster entry
    /// becomes the owner's, the previous owner stays an admin, and every grant the previous owner made is re-signed.</summary>
    public void AcceptOwnership()
    {
        lock (_gate)
        {
            var (r, m, _, _) = Begin();
            var t = PendingTransfer() ?? throw new LibraryException("There is no valid ownership offer on the share.");
            if (t.ToId != Me || t.ToEncPub != Keys.EncPub || t.ToSigPub != Keys.SigPub) throw new LibraryException("The offer is for someone else.");
            var header = ShareIo.Read<LibraryHeader>(P("library.json")) ?? throw new LibraryException("The library header is missing.");
            VerifyHeader(header, Id, State.OwnerFingerprint);
            header.Transfers ??= new();
            header.Transfers.Add(t);
            var previous = t.FromId;
            State.Header = header;
            for (int i = 0; i < r.Members.Count; i++)
            {
                var e = r.Members[i].Copy();
                if (e.Id == Me) { e.Role = Roles.Owner; e.Access = new(StringComparer.Ordinal); }
                else if (e.Id == previous) e.Role = Roles.Admin;
                else if (e.GrantedBy != previous) continue;
                r.Members[i] = Grant(e);
            }
            // Roster first (signed by the new owner), then the header that makes it the owner: a client reading in between
            // keeps its cached list for one sync.
            Commit(r, m);
            ShareIo.Write(P("library.json"), header);
            try { File.Delete(OfferPath); } catch (IOException) { }
            Sync();
            Audit("ownership accepted", $"from {NameOf(previous)}");
        }
    }

    // ---- key backup -------------------------------------------------------------------------------------------------

    private sealed class KeyBackupBody
    {
        public string LibraryId { get; set; } = "";
        public string MemberId { get; set; } = "";
        public string MyName { get; set; } = "";
        public string EncPriv { get; set; } = "";
        public string SigPriv { get; set; } = "";
        public string SharePath { get; set; } = "";
        public string OwnerFingerprint { get; set; } = "";
        public string Name { get; set; } = "";
    }

    /// <summary>This member's keys sealed under a password, for safekeeping (the owner's especially) or another PC.</summary>
    public byte[] ExportKeys(string password)
    {
        if (password.Length < 12) throw new LibraryException("Use a password of at least 12 characters: this file holds your library keys.");
        var b = new KeyBackup { LibraryId = Id, Salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)) };
        var key = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(b.Salt), b.Iterations, HashAlgorithmName.SHA256, 32);
        var body = new KeyBackupBody { LibraryId = Id, MemberId = Me, MyName = State.MyName, EncPriv = State.EncPriv, SigPriv = State.SigPriv,
                                       SharePath = State.SharePath, OwnerFingerprint = State.OwnerFingerprint, Name = State.Name };
        b.Blob = LibraryCrypto.SealText(key, ShareIo.ToJson(body), $"{b.Format}|{Id}");
        CryptographicOperations.ZeroMemory(key);
        AuditLog.Write("you", "shared library " + State.Name, "member keys exported (password-sealed)", "");
        return System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(b, ShareIo.Json);
    }

    /// <summary>Opens a key backup. Throws <see cref="LibraryException"/> for a wrong password or a damaged file.</summary>
    public static LibraryState ImportKeys(byte[] file, string password)
    {
        KeyBackup? b;
        try { b = System.Text.Json.JsonSerializer.Deserialize<KeyBackup>(file, ShareIo.Json); }
        catch (System.Text.Json.JsonException) { b = null; }
        if (b is not { Format: "dcxlibkey1" } || b.LibraryId.Length != 32 || b.Iterations is < 100_000 or > 10_000_000) throw new LibraryException("That is not a library key backup.");
        var key = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(b.Salt), b.Iterations, HashAlgorithmName.SHA256, 32);
        try
        {
            var body = ShareIo.FromJson<KeyBackupBody>(LibraryCrypto.OpenText(key, b.Blob, $"{b.Format}|{b.LibraryId}")) ?? throw new LibraryException("The backup is empty.");
            if (body.LibraryId != b.LibraryId) throw new LibraryException("The backup is damaged.");
            using var probe = MemberKeys.Import(body.EncPriv, body.SigPriv);
            return new LibraryState { LibraryId = body.LibraryId, MemberId = body.MemberId, MyName = body.MyName, EncPriv = body.EncPriv, SigPriv = body.SigPriv,
                                      SharePath = body.SharePath, OwnerFingerprint = body.OwnerFingerprint, Name = body.Name };
        }
        catch (CryptographicException) { throw new LibraryException("Wrong password, or the backup was changed."); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    // ---- audit ------------------------------------------------------------------------------------------------------

    /// <summary>Appends a sealed, signed, hash-chained line to this member's audit file on the share (queued while away),
    /// and mirrors it into this PC's own encrypted audit log.</summary>
    public void Audit(string action, string target)
    {
        AuditLog.Write("you", "shared library " + State.Name, action, target);
        if (State.Roster?.Members.Any(m => m.Id == Me) != true) return;
        lock (_gate)
        {
            try
            {
                // Sealed to the audit key learnt at the last verified sync: works while the share is away (the line is queued).
                if (State.AuditPub.Length == 0) return;
                var gen = new AuditGen { Gen = State.AuditGen, Pub = State.AuditPub };
                var month = DateTime.UtcNow.ToString("yyyy-MM");
                if (State.AuditMonth != month) { State.AuditMonth = month; State.AuditPrev = ""; }
                var line = new AuditLine { Seq = ++State.AuditSeq, Prev = State.AuditPrev, Time = Now(), Gen = gen.Gen,
                                           Body = LibraryCrypto.WrapFor(gen.Pub, Encoding.UTF8.GetBytes(ShareIo.ToJson(new[] { action, target })), AuditLineContext(Me, State.AuditSeq + 0)) };
                line.Sig = Keys.Sign(line.Canonical(Me));
                var text = System.Text.Json.JsonSerializer.Serialize(line, ShareIo.Json);
                State.AuditPrev = LibraryCrypto.Hash(text);
                State.AuditOutbox.Add(new QueuedAuditLine { File = $"{Me}-{month}.dcxa", Line = text });
                FlushAudit();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
            _persist(State);
        }
    }

    private void FlushAudit()
    {
        if (State.AuditOutbox.Count == 0 || Unreachable() != null) return;
        Directory.CreateDirectory(P("audit"));
        foreach (var q in State.AuditOutbox.ToList())
        {
            if (q.File.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { State.AuditOutbox.Remove(q); continue; }
            File.AppendAllText(P("audit", q.File), q.Line + "\n", new UTF8Encoding(false));
            State.AuditOutbox.Remove(q);
        }
    }

    /// <summary>Admin: every audit line on the share, verified (signature, sequence, hash chain) and decrypted.</summary>
    public List<AuditEntry> ReadAudit()
    {
        RequireAdmin();
        var o = new List<AuditEntry>();
        if (Unreachable() is { } why) throw new LibraryException(why);
        var r = State.Roster!;
        var m = ReadMeta(r) ?? throw new LibraryException("the metadata can't be read");
        var privs = new Dictionary<int, KemKey>();
        try
        {
            foreach (var g in m.Audit.Where(g => g.Wraps.ContainsKey(Me))) privs[g.Gen] = KemKey.ImportBytes(AuditPrivate(m, g.Gen));
            foreach (var path in Directory.Exists(P("audit")) ? Directory.EnumerateFiles(P("audit"), "*.dcxa") : Enumerable.Empty<string>())
            {
                var member = Path.GetFileNameWithoutExtension(path).Split('-')[0];
                var pub = r.Members.FirstOrDefault(x => x.Id == member)?.SigPub ?? r.Removed.FirstOrDefault(x => x.Id == member)?.SigPub;
                var prev = ""; long seq = -1;
                foreach (var text in File.ReadLines(path).Where(l => l.Length > 0).Take(100_000))
                {
                    string? problem = null; string action = "?", target = "";
                    AuditLine? line = null;
                    try { line = System.Text.Json.JsonSerializer.Deserialize<AuditLine>(text, ShareIo.Json); } catch (System.Text.Json.JsonException) { }
                    if (line == null) { o.Add(new(member, NameOf(member), 0, "", "?", "", "unreadable line")); continue; }
                    if (pub == null || !LibraryCrypto.Verify(pub, line.Canonical(member), line.Sig)) problem = "signature invalid";
                    else if (line.Prev != prev) problem = "hash chain broken (lines removed or changed before this one)";
                    else if (seq >= 0 && line.Seq != seq + 1) problem = $"sequence jumps {seq} → {line.Seq} (lines missing)";
                    if (privs.TryGetValue(line.Gen, out var pk))
                        try
                        {
                            var pair = ShareIo.FromJson<string[]>(Encoding.UTF8.GetString(LibraryCrypto.Unwrap(pk, line.Body, AuditLineContext(member, line.Seq))));
                            if (pair is { Length: 2 }) { action = pair[0]; target = pair[1]; }
                        }
                        catch (CryptographicException) { problem ??= "can't be decrypted"; }
                    else problem ??= "sealed with an audit key you don't hold";
                    o.Add(new(member, NameOf(member), line.Seq, line.Time, action, target, problem));
                    prev = LibraryCrypto.Hash(text); seq = line.Seq;
                }
            }
        }
        finally { foreach (var k in privs.Values) k.Dispose(); }
        return o.OrderBy(a => a.Time, StringComparer.Ordinal).ToList();
    }
}
