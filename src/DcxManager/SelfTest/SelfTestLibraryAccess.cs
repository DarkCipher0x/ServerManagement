using System.IO;
using DcxManager.Library;
using DcxManager.Services;
using DcxManager.Views;

namespace DcxManager;

/// <summary>
/// 0.3.0 preview.7 (§5): short approval codes and per-member permissions, against a scratch folder playing the share.
/// The 6-digit code appears on both sides only after the admin's challenge and the requester's reveal, and matches;
/// a wrong code, a reveal that doesn't match the commitment, or a forged challenge are refused; an item limited to some
/// members moves into its own folder that others can't decrypt after their next sync. Demo data only; no window.
/// </summary>
public static class LibraryAccessTests
{
    public static Task RunAsync(string reportDir, Action<string> ok, Action<string, Exception?> fail)
    {
        var share = Path.Combine(reportDir, "library-access-share");
        if (Directory.Exists(share)) Directory.Delete(share, true);
        void Mem(LibraryState _) { }
        try
        {
            var owner = LibraryClient.Create(share, "Access-team-qx8", "Owner-qx8", "General-qx8", Mem);
            var general = owner.State.Collections.Keys.Single();
            var amy = LibraryClient.Join(owner.Profile(), share, "Amy-qx8", Mem);
            var bob = LibraryClient.Join(owner.Profile(), share, "Bob-qx8", Mem);
            var reqA = owner.Requests().Single(r => r.Request.Id == amy.Me);
            if (reqA.Request.Commit.Length != 64 || owner.CodeFor(reqA) != null) throw new Exception("commitment missing, or a code before any check");

            // Before the check: no code anywhere.
            amy.Sync();
            if (amy.MyCode != null) throw new Exception("a code without a challenge");
            owner.StartCheck(amy.Me);
            if (owner.CodeFor(owner.Requests().Single(r => r.Request.Id == amy.Me)) != null) throw new Exception("a code before the reveal");
            amy.Sync();   // answers the challenge
            reqA = owner.Requests().Single(r => r.Request.Id == amy.Me);
            var code = owner.CodeFor(reqA);
            if (code == null || amy.MyCode != code || code.Length != 7 || code[3] != ' ') throw new Exception($"codes: admin {code} / requester {amy.MyCode}");

            // A wrong code is refused; a forged reveal (nonce that doesn't match the commitment) gives no code.
            try { owner.ApproveWithCode(reqA, code == "000 000" ? "111 111" : "000 000", Roles.Member, new Dictionary<string, string> { [general] = Roles.Reader }); throw new Exception("a wrong code was accepted"); }
            catch (LibraryException) { }
            owner.StartCheck(bob.Me);
            var forged = new JoinReveal { LibraryId = owner.Id, RequestId = bob.Me, Nonce = Convert.ToBase64String(new byte[16]), Time = DateTime.UtcNow.ToString("O") };
            forged.Sig = bob.Keys.Sign(forged.Canonical());
            ShareIo.Write(Path.Combine(share, "requests", bob.Me + ".reveal.json"), forged);
            if (owner.CodeFor(owner.Requests().Single(r => r.Request.Id == bob.Me)) != null) throw new Exception("a reveal that doesn't match the commitment produced a code");
            // A challenge not signed by an admin is ignored by the requester.
            var fake = new JoinChallenge { LibraryId = owner.Id, RequestId = bob.Me, AdminId = bob.Me, Nonce = Convert.ToBase64String(new byte[16]), Time = DateTime.UtcNow.ToString("O") };
            fake.Sig = bob.Keys.Sign(fake.Canonical());
            ShareIo.Write(Path.Combine(share, "requests", bob.Me + ".challenge.json"), fake);
            bob.Sync();
            if (bob.MyCode != null || !bob.State.Problems.Any(p => p.Contains("not signed by an admin"))) throw new Exception("a forged challenge was answered");

            owner.ApproveWithCode(reqA, code, Roles.Member, new Dictionary<string, string> { [general] = Roles.Reader });
            amy.Sync();
            if (amy.State.Pending || !amy.CanRead(general) || File.Exists(Path.Combine(share, "requests", amy.Me + ".reveal.json"))) throw new Exception("approval by code didn't complete or left files");
            ok("short approval codes: the commitment is in the request; no code before the admin's challenge and the requester's reveal; both sides then show the same 6-digit code; a wrong code, a reveal that doesn't match the commitment and a challenge not signed by an admin are refused; approval cleans up");

            // Per-item limit: a credential moves into its own folder; Amy (not ticked) can't read it any more.
            var cred = owner.SaveCredential(general, null, new Credential { Name = "limit-me-qx8", Kind = CredentialKind.Password, Username = "ops", Secret = "Limit-Secret-qx8" });
            amy.Sync();
            if (amy.State.Items.Values.All(i => i.Payload.Cred?.Name != "limit-me-qx8")) throw new Exception("member can't read the credential before the limit");
            var item = owner.ItemsSnapshot().Single(i => i.Item == cred);
            var cid = LibraryAccessUi.MoveTo(owner, item, "limit-me-qx8 (restricted)");
            owner.Sync(); amy.Sync();
            if (amy.State.Items.Values.Any(i => i.Payload.Cred?.Name == "limit-me-qx8") || amy.CanRead(cid)) throw new Exception("an untick member can still read the limited item");
            if (owner.ItemsSnapshot().Count(i => i.Payload.Cred?.Name == "limit-me-qx8") != 1 || owner.ItemsSnapshot().Single(i => i.Payload.Cred?.Name == "limit-me-qx8").Cid != cid) throw new Exception("the item wasn't moved");
            // Granting Amy that folder brings it back for her (the permissions grid / access window do this).
            owner.SetAccess(amy.Me, Roles.Member, new Dictionary<string, string> { [general] = Roles.Reader, [cid] = Roles.Reader });
            amy.Sync();
            if (!amy.State.Items.Values.Any(i => i.Payload.Cred?.Secret == "Limit-Secret-qx8")) throw new Exception("granted folder not readable");
            var raw = string.Concat(Directory.EnumerateFiles(share, "*", SearchOption.AllDirectories).Select(File.ReadAllText));
            if (raw.Contains("Limit-Secret-qx8") || raw.Contains("limit-me-qx8")) throw new Exception("plaintext on the share");
            ok("per-item permissions: \"Limit who can see this\" moves a credential into its own folder (its own key) that a member without access can't decrypt after their next sync; granting that folder brings it back; nothing readable on the share");
        }
        catch (Exception ex) { fail("library approval codes / permissions", ex); }
        finally { try { Directory.Delete(share, true); } catch { } }
        return Task.CompletedTask;
    }
}
