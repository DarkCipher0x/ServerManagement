using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DcxManager.Library;

// Short approval codes (0.3.0 §5, DarkCipher0x: "approvals should be easier to type out — I don't want to type a whole 16 digit
// key"). Instead of reading out the 32-character fingerprint, both PCs show the same 6-digit code, made safe against a
// forged request by commit-then-reveal through the share:
//   1. the join request carries Commit = SHA-256(Nr), Nr a secret 128-bit nonce kept on the requester's PC;
//   2. an admin starts the check: requests\<id>.challenge.json holds a fresh nonce Na, signed by that admin;
//   3. the requester's app sees a validly signed challenge and reveals Nr in requests\<id>.reveal.json, signed with the
//      request's key;
//   4. both compute code = SHA-256(library | request id | both public keys | Na | Nr) → 6 digits.
// Someone who forged or swapped the request had to commit to Nr before seeing Na, so their code matches by chance only
// (one in a million per attempt, and each attempt is a visible new request). The admin compares the code over a call and
// approves; the full fingerprint stays available and works as before.
public sealed partial class LibraryClient
{
    internal static string CommitOf(byte[] nonce) => Convert.ToHexString(SHA256.HashData(nonce)).ToLowerInvariant();

    private string ChallengePath(string requestId) => P("requests", requestId + ".challenge.json");
    private string RevealPath(string requestId) => P("requests", requestId + ".reveal.json");

    /// <summary>The 6-digit code for a request, its key pair and both nonces ("123 456").</summary>
    public static string ShortCode(string libraryId, JoinRequest r, string adminNonce, string requesterNonce)
    {
        var h = SHA256.HashData(Encoding.UTF8.GetBytes($"dcx-sas1|{libraryId}|{r.Id}|{r.EncPub}|{r.SigPub}|{adminNonce}|{requesterNonce}"));
        var n = (BitConverter.ToUInt32(h, 0) % 1_000_000).ToString("D6");
        return n[..3] + " " + n[3..];
    }

    /// <summary>Admin: start the check for a request (writes a signed challenge). Safe to call again (a new challenge).</summary>
    public void StartCheck(string requestId)
    {
        RequireAdmin();
        if (Unreachable() is { } why) throw new LibraryException(why);
        var req = Requests().FirstOrDefault(r => r.Request.Id == requestId) ?? throw new LibraryException("That request is no longer on the share.");
        if (!req.Valid) throw new LibraryException("That request is not validly signed.");
        if (req.Request.Commit.Length != 64) throw new LibraryException("This request comes from an older app without short codes: compare the full fingerprint instead.");
        var na = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var ch = new JoinChallenge { LibraryId = Id, RequestId = requestId, AdminId = Me, Nonce = na, Time = Now() };
        ch.Sig = Keys.Sign(ch.Canonical());
        try { File.Delete(RevealPath(requestId)); } catch (IOException) { }
        ShareIo.Write(ChallengePath(requestId), ch);
        lock (_gate) { State.Challenges[requestId] = na; _persist(State); }
        Audit("join check started", requestId[..8]);
    }

    /// <summary>Admin: the code for a request once the requester's app has answered; null while waiting (or if the answer
    /// isn't valid — a reveal that doesn't match the commitment or isn't signed by the request's key counts as none).</summary>
    public string? CodeFor(RequestInfo r)
    {
        if (!State.Challenges.TryGetValue(r.Request.Id, out var na) || !r.Valid || r.Request.Commit.Length != 64) return null;
        JoinReveal? rv;
        try { rv = ShareIo.Read<JoinReveal>(RevealPath(r.Request.Id)); } catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException) { return null; }
        if (rv == null || rv.LibraryId != Id || rv.RequestId != r.Request.Id || !LibraryCrypto.Verify(r.Request.SigPub, rv.Canonical(), rv.Sig)) return null;
        byte[] nr;
        try { nr = Convert.FromBase64String(rv.Nonce); } catch (FormatException) { return null; }
        if (nr.Length != 16 || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(CommitOf(nr)), Encoding.ASCII.GetBytes(r.Request.Commit))) return null;
        return ShortCode(Id, r.Request, na, rv.Nonce);
    }

    /// <summary>Requester (during sync while pending): answer a validly signed admin challenge by revealing the nonce.</summary>
    private void AnswerChallenge(Roster roster, List<string> problems)
    {
        if (State.JoinNonce.Length == 0) return;
        JoinChallenge? ch;
        try { ch = ShareIo.Read<JoinChallenge>(ChallengePath(Me)); } catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException) { problems.Add("a join challenge on the share can't be read"); return; }
        if (ch == null) { MyCode = null; return; }
        var admin = roster.Members.FirstOrDefault(m => m.Id == ch.AdminId && Roles.IsAdmin(m.Role));
        if (admin == null || ch.LibraryId != Id || ch.RequestId != Me || !LibraryCrypto.Verify(admin.SigPub, ch.Canonical(), ch.Sig))
        {
            problems.Add("a join challenge on the share is not signed by an admin: ignored");
            return;
        }
        var mine = new JoinRequest { Id = Me, EncPub = Keys.EncPub, SigPub = Keys.SigPub };
        MyCode = ShortCode(Id, mine, ch.Nonce, State.JoinNonce);
        MyCodeAdmin = NameOf(ch.AdminId);
        if (ShareIo.Read<JoinReveal>(RevealPath(Me)) is { } existing && existing.Nonce == State.JoinNonce) return;
        var rv = new JoinReveal { LibraryId = Id, RequestId = Me, Nonce = State.JoinNonce, Time = Now() };
        rv.Sig = Keys.Sign(rv.Canonical());
        ShareIo.Write(RevealPath(Me), rv);
    }

    /// <summary>Requester: the code to read to the admin (null until an admin has started the check).</summary>
    public string? MyCode { get; private set; }
    /// <summary>Requester: who started the check.</summary>
    public string? MyCodeAdmin { get; private set; }

    /// <summary>Admin: approves after the codes matched (the request's own fingerprint stands in for the typed one; the
    /// code is what was compared). Removes the challenge and reveal files.</summary>
    public void ApproveWithCode(RequestInfo r, string code, string role, IDictionary<string, string> access, string? name = null)
    {
        if (CodeFor(r) is not { } expected || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected.Replace(" ", "")), Encoding.ASCII.GetBytes(code.Replace(" ", ""))))
            throw new LibraryException("The code doesn't match this request's check. Start the check again.");
        Approve(r.Request.Id, r.Request.Fingerprint, role, access, name);
        foreach (var p in new[] { ChallengePath(r.Request.Id), RevealPath(r.Request.Id) }) try { File.Delete(p); } catch (IOException) { }
        lock (_gate) { State.Challenges.Remove(r.Request.Id); _persist(State); }
        Audit("member approved by short code", r.Request.Id[..8]);
    }
}
