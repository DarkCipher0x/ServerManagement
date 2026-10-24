using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DcxManager.Addons;
using DcxManager.Library;
using DcxManager.Services;
using DcxManager.Views;

namespace DcxManager;

/// <summary>
/// ADR-033. The shared library add-on, end to end against a scratch folder that plays the file share. Three members run in
/// this process (owner, editor, reader), each with its own keys and cached copy; a fourth is never approved. Checks: nothing
/// readable on the share, non-members read nothing, readers can't write (even with a forged file), tamper / roll-back /
/// vanishing are detected, concurrent saves become a conflict that is resolved explicitly, the offline cache and outbox, the
/// signed and hash-chained audit, member removal re-keys and leaves a rotation checklist, local hosts and credentials are
/// never changed, the add-on package, and the screens render. Demo data and TEST-NET addresses only; no window or dialog.
/// </summary>
public static class LibraryTests
{
    public static async Task RunAsync(MainWindow main, string reportDir, Action<string> ok, Action<string, Exception?> fail)
    {
        async Task Settle(int ms = 200)
        {
            await Task.Delay(ms);
            await Application.Current.Dispatcher.InvokeAsync(() => main.UpdateLayout(), DispatcherPriority.ApplicationIdle);
        }
        var share = Path.Combine(reportDir, "library-share");
        if (Directory.Exists(share)) Directory.Delete(share, true);
        var work = Directory.CreateDirectory(Path.Combine(reportDir, "library-work")).FullName;
        SharedLibraries.WatchEnabled = false;
        var gateWas = LibraryClient.Gate;
        var localBefore = JsonSerializer.Serialize(AppData.Current, SecureStore.Json);
        void Mem(LibraryState _) { }

        const string LibName = "Selftest-team-qx7", CoreName = "Core-infra-qx7", NetName = "Network-qx7";
        const string HostName = "web-demo-qx7-01", HostAddr = "192.0.2.10", Secret = "S3cr3t-demo-pass-qx7", User = "demo-user-qx7";
        const string EdName = "Editor-Ed-qx7", RaeName = "Reader-Rae-qx7";
        LibraryClient owner = null!, ed = null!, rae = null!;
        string core = "", credItem = "", hostItem = "";

        string Share(params string[] p) => Path.Combine(new[] { share }.Concat(p).ToArray());
        string ItemsDir(string cid) => Share("collections", cid, "items");

        // 1. create, join, approve
        try
        {
            owner = LibraryClient.Create(share, LibName, "Owner-qx7", CoreName, Mem);
            core = owner.State.Collections.Keys.Single();
            var profile = owner.Profile();
            ed = LibraryClient.Join(profile, share, EdName, Mem);
            rae = LibraryClient.Join(profile, share, RaeName, Mem);
            var reqs = owner.Requests();
            if (reqs.Count != 2 || !reqs.All(r => r.Valid) || !reqs.Any(r => r.Name == EdName) || !reqs.Any(r => r.Name == RaeName)) throw new Exception("requests: " + string.Join(", ", reqs.Select(r => $"{r.Name}/{r.Valid}")));
            try { owner.Approve(ed.Me, "0000 0000 0000 0000 0000 0000 0000 0000", Roles.Member, new Dictionary<string, string> { [core] = Roles.Editor }); throw new Exception("a wrong fingerprint was accepted"); }
            catch (LibraryException) { }
            owner.Approve(ed.Me, ed.Keys.Fingerprint, Roles.Member, new Dictionary<string, string> { [core] = Roles.Editor });
            owner.Approve(rae.Me, rae.Keys.Fingerprint.Replace(" ", ""), Roles.Member, new Dictionary<string, string> { [core] = Roles.Reader });
            ed.Sync(); rae.Sync();
            if (ed.State.Pending || !ed.CanEdit(core) || rae.CanEdit(core) || !rae.CanRead(core) || !owner.IsOwner) throw new Exception($"roles: ed {ed.MyRole}/{ed.CanEdit(core)}, rae {rae.MyRole}/{rae.CanEdit(core)}");
            if (ed.NameOf(rae.Me) != RaeName || ed.State.Name != LibName) throw new Exception("sealed names not readable by members");
            ok("shared library: owner creates the library; two members join with self-signed requests whose names only admins can read; a WRONG fingerprint is refused; approval wraps the keys; editor and reader get exactly their roles");
        }
        catch (Exception ex) { fail("shared library: create / join / approve", ex); return; }

        // 2. write + read
        try
        {
            credItem = ed.SaveCredential(core, null, new Credential { Name = "demo-cred-qx7", Kind = CredentialKind.Password, Username = User, Secret = Secret });
            hostItem = ed.SaveHost(core, null, new HostEntry { Name = HostName, Address = HostAddr, Username = User, Auth = AuthMethod.Password, CredentialId = credItem, Group = "Web", Environment = "TEST" });
            rae.Sync(); owner.Sync();
            var h = rae.State.Items.GetValueOrDefault(hostItem)?.Payload.Host;
            var c = rae.State.Items.GetValueOrDefault(credItem)?.Payload.Cred;
            if (h?.Address != HostAddr || h.CredentialId != credItem || c?.Secret != Secret) throw new Exception("reader didn't get the editor's items");
            ok("shared library: an editor's host and credential reach the reader, decrypted and verified (the host refers to the credential by item id)");
        }
        catch (Exception ex) { fail("shared library: write / read", ex); }

        // 3. nothing readable on the share
        try
        {
            var needles = new[] { LibName, CoreName, HostName, HostAddr, Secret, User, EdName, RaeName, "Owner-qx7", "demo-cred-qx7" };
            var files = Directory.EnumerateFiles(share, "*", SearchOption.AllDirectories).ToList();
            var leaks = new List<string>();
            foreach (var f in files)
            {
                var text = Encoding.UTF8.GetString(File.ReadAllBytes(f));
                var asLatin = Encoding.Latin1.GetString(File.ReadAllBytes(f));
                foreach (var n in needles) if (text.Contains(n, StringComparison.OrdinalIgnoreCase) || asLatin.Contains(n, StringComparison.OrdinalIgnoreCase)) leaks.Add($"{Path.GetFileName(f)}: {n}");
            }
            if (leaks.Count > 0) throw new Exception("plaintext on the share: " + string.Join("; ", leaks));
            ok($"shared library: none of the {needles.Length} demo names, address, user or secret appears in any of the {files.Count} files on the share (ciphertext, signatures and public keys only)");
        }
        catch (Exception ex) { fail("shared library: nothing readable on the share", ex); }

        // 4. a non-member reads nothing
        try
        {
            var mallory = LibraryClient.Join(owner.Profile(), share, "Mallory-qx7", Mem);
            mallory.Sync();
            if (!mallory.State.Pending || mallory.State.Items.Count > 0 || mallory.State.Keys.Count > 0 || mallory.State.Collections.Count > 0) throw new Exception("an unapproved requester got data");
            var anyItem = Directory.EnumerateFiles(ItemsDir(core), "*.dcxi").First();
            var kf = JsonSerializer.Deserialize<KeysFile>(File.ReadAllBytes(Share("collections", core, "keys.json")), ShareIo.Json)!;
            if (kf.Epochs.Values.Any(w => w.ContainsKey(mallory.Me))) throw new Exception("a key was wrapped for a non-member");
            owner.Deny(mallory.Me);
            if (owner.Requests().Count != 0) throw new Exception("the denied request is still there");
            // A request for another library's owner key is refused at join.
            var forged = owner.Profile(); forged.OwnerFingerprint = mallory.Keys.Fingerprint;
            try { LibraryClient.Join(forged, share, "x", Mem); throw new Exception("a profile with the wrong owner fingerprint joined"); } catch (LibraryException) { }
            ok($"shared library: an unapproved member syncs and gets no keys, names or items ({Path.GetFileName(anyItem)[..8]}… stays sealed); a denied request is gone; an add-on whose owner fingerprint doesn't match the share is refused");
        }
        catch (Exception ex) { fail("shared library: non-member", ex); }

        // 5. readers can't write, even with a forged file
        ItemFile Forge(LibraryClient who, string cid, string item, int rev, string note)
        {
            var epoch = who.State.Epochs[cid];
            var f = new ItemFile { LibraryId = who.Id, Cid = cid, Item = item, Rev = rev, Author = who.Me, Epoch = epoch, Base = new(), Time = DateTime.UtcNow.ToString("O") };
            f.Body = LibraryCrypto.SealText(Convert.FromBase64String(who.State.Keys[cid][epoch]), ShareIo.ToJson(new ItemPayload { Kind = "host", Host = new HostEntry { Name = note, Address = "198.51.100.66" } }), f.Aad);
            f.Sig = who.Keys.Sign(f.Canonical());
            ShareIo.Write(Path.Combine(ItemsDir(cid), f.FileName), f);
            return f;
        }
        try
        {
            try { rae.SaveHost(core, null, new HostEntry { Name = "x", Address = "198.51.100.5" }); throw new Exception("the reader's app allowed a write"); } catch (LibraryException) { }
            var forgedItem = LibraryCrypto.NewId();
            var forged = Forge(rae, core, forgedItem, 1, "forged-by-reader");
            owner.Sync();
            if (owner.State.Items.ContainsKey(forgedItem)) throw new Exception("the forged item was accepted");
            if (!owner.State.Problems.Any(p => p.Contains("rejected"))) throw new Exception("no warning about the rejected file: " + string.Join(" | ", owner.State.Problems));
            File.Delete(Path.Combine(ItemsDir(core), forged.FileName));
            owner.Sync();
            ok("shared library: the reader's app refuses to write, and a validly encrypted file FORGED by the reader (signed with its own key) is rejected and flagged by every other member");
        }
        catch (Exception ex) { fail("shared library: reader can't write", ex); }

        // 6. tamper, vanish, roll-back
        try
        {
            var hostFile = Directory.EnumerateFiles(ItemsDir(core), hostItem + ".*.dcxi").Single();
            var good = File.ReadAllBytes(hostFile);
            var json = JsonSerializer.Deserialize<ItemFile>(good, ShareIo.Json)!;
            json.Body = json.Body[..10] + (json.Body[10] == 'A' ? 'B' : 'A') + json.Body[11..];
            File.WriteAllBytes(hostFile, JsonSerializer.SerializeToUtf8Bytes(json, ShareIo.Json));
            owner.Sync();
            var w = owner.State.Items[hostItem];
            if (w.Warning == null || !owner.State.Problems.Any(p => p.Contains("rejected"))) throw new Exception("tampered item not flagged");
            if (w.Payload.Host?.Address != HostAddr) throw new Exception("the cached copy was lost");
            File.WriteAllBytes(hostFile, good);
            owner.Sync();
            if (owner.State.Items[hostItem].Warning != null) throw new Exception("warning stayed after the good file came back");

            // roll-back: edit to r2, then put r1 back in its place
            var r1 = good;
            var r1Name = Path.GetFileName(hostFile);
            var edited = ed.State.Items[hostItem].Payload.Host!.Clone(); edited.Notes = "second revision";
            ed.Sync(); ed.SaveHost(core, hostItem, edited);
            owner.Sync();
            if (owner.State.Items[hostItem].Rev != 2) throw new Exception("owner didn't see revision 2");
            var r2File = Directory.EnumerateFiles(ItemsDir(core), hostItem + ".*.dcxi").Single();
            var r2 = File.ReadAllBytes(r2File);
            File.Delete(r2File); File.WriteAllBytes(Path.Combine(ItemsDir(core), r1Name), r1);
            owner.Sync();
            var rb = owner.State.Items[hostItem];
            if (rb.Rev != 2 || rb.Warning?.Contains("OLDER") != true) throw new Exception($"roll-back not detected (rev {rb.Rev}, {rb.Warning})");
            File.Delete(Path.Combine(ItemsDir(core), r1Name)); File.WriteAllBytes(r2File, r2);
            owner.Sync();
            if (owner.State.Items[hostItem].Warning != null) throw new Exception("roll-back warning stayed");
            ok("shared library: a changed byte in an item is rejected (the cached copy is kept and flagged); an item rolled back to an older revision is detected and the newer cached revision kept");
        }
        catch (Exception ex) { fail("shared library: tamper / roll-back", ex); }

        // 7. concurrent saves → conflict → explicit resolution
        try
        {
            ed.Sync(); owner.Sync();
            var a = ed.State.Items[hostItem].Payload.Host!.Clone(); a.Port = 2201;
            var b = owner.State.Items[hostItem].Payload.Host!.Clone(); b.Port = 2202;
            ed.SaveHost(core, hostItem, a);
            owner.SaveHost(core, hostItem, b);   // owner hasn't synced: same base
            rae.Sync();
            var ci = rae.State.Items[hostItem];
            if (ci.Conflicts.Count != 1) throw new Exception($"expected a conflict, got {ci.Conflicts.Count + 1} version(s)");
            var edRev = ci.Heads.First(h => h.EndsWith(ed.Me));
            owner.Sync();
            owner.ResolveConflict(hostItem, edRev);
            rae.Sync();
            ci = rae.State.Items[hostItem];
            if (ci.Conflicts.Count != 0 || ci.Payload.Host?.Port != 2201) throw new Exception($"after resolving: {ci.Conflicts.Count} conflicts, port {ci.Payload.Host?.Port}");
            ok("shared library: two members saving the same host from the same version produce a CONFLICT with both versions kept; choosing one writes a signed revision that supersedes both (no silent last-writer-wins)");
        }
        catch (Exception ex) { fail("shared library: conflicts", ex); }

        // 8. offline: cached copy + outbox
        try
        {
            LibraryClient.Gate = _ => "self-test: the share is offline";
            if (ed.Sync()) throw new Exception("sync claimed success while offline");
            if (!ed.State.Items.ContainsKey(hostItem)) throw new Exception("cached items gone while offline");
            var queued = ed.SaveCredential(core, null, new Credential { Name = "queued-cred-qx7", Kind = CredentialKind.Password, Username = User, Secret = "queued-" + Secret });
            if (ed.State.Outbox.Count != 1 || ed.State.Items[queued].Pending != true) throw new Exception("the edit was not queued");
            LibraryClient.Gate = gateWas;
            if (!ed.Sync() || ed.State.Outbox.Count != 0) throw new Exception("the outbox didn't flush: " + ed.State.LastError);
            rae.Sync();
            if (rae.State.Items.GetValueOrDefault(queued)?.Payload.Cred?.Secret != "queued-" + Secret) throw new Exception("the queued edit didn't reach the reader");
            ok("shared library: with the share unreachable the cached copy stays usable and an edit waits in the (sealed) outbox; it is sent and verified on the next sync");
        }
        catch (Exception ex) { fail("shared library: offline", ex); }
        finally { LibraryClient.Gate = gateWas; }

        // 9. audit
        try
        {
            var audit = owner.ReadAudit();
            var bad = audit.Where(a => a.Problem != null).ToList();
            if (bad.Count > 0) throw new Exception("audit problems: " + string.Join(" | ", bad.Take(3).Select(a => $"{a.Name}: {a.Problem}")));
            if (!audit.Any(a => a.Member == ed.Me && a.Action == "item added") || !audit.Any(a => a.Action == "member approved") || !audit.Any(a => a.Action == "conflict resolved"))
                throw new Exception("expected entries missing: " + string.Join(", ", audit.Select(a => a.Action).Distinct()));
            try { rae.ReadAudit(); throw new Exception("a member read the audit"); } catch (LibraryException) { }
            var edFile = Directory.EnumerateFiles(Share("audit"), ed.Me + "-*.dcxa").Single();
            var lines = File.ReadAllLines(edFile);
            File.WriteAllLines(edFile, lines.Where((_, i) => i != 1));
            if (!owner.ReadAudit().Any(a => a.Member == ed.Me && a.Problem?.Contains("chain") == true)) throw new Exception("a removed audit line was not detected");
            File.WriteAllLines(edFile, lines);
            ok($"shared library: the shared audit ({audit.Count} lines from {audit.Select(a => a.Member).Distinct().Count()} members) verifies (signature, sequence, hash chain) and decrypts for admins only; a removed line breaks the chain and is reported");
        }
        catch (Exception ex) { fail("shared library: audit", ex); }

        // 9b. groups (0.3.0 §22): access through a group is enforced by the keys like a direct grant
        try
        {
            var gcid = owner.CreateCollection("Groups-qx7");
            var gItem = owner.SaveCredential(gcid, null, new Credential { Name = "group-cred-qx7", Kind = CredentialKind.Password, Username = User, Secret = "group-" + Secret });
            rae.Sync(); ed.Sync();
            if (rae.CanRead(gcid) || ed.CanRead(gcid) || rae.State.Keys.ContainsKey(gcid)) throw new Exception("a new collection was readable before any grant");
            owner.SetGroup("Ops-qx7", new[] { rae.Me }, new Dictionary<string, string> { [gcid] = Roles.Reader });
            rae.Sync(); ed.Sync();
            if (!rae.CanRead(gcid) || rae.CanEdit(gcid) || rae.State.Items.GetValueOrDefault(gItem)?.Payload.Cred?.Secret != "group-" + Secret) throw new Exception("the group member can't read the collection");
            if (ed.CanRead(gcid) || ed.State.Keys.ContainsKey(gcid)) throw new Exception("a non-member of the group got the key");
            if (ed.State.Roster?.Groups.SingleOrDefault()?.Name != "Ops-qx7") throw new Exception("the roster with groups didn't verify for another member");
            var kf = JsonSerializer.Deserialize<KeysFile>(File.ReadAllBytes(Share("collections", gcid, "keys.json")), ShareIo.Json)!;
            if (!kf.Epochs[1].ContainsKey(rae.Me) || kf.Epochs[1].ContainsKey(ed.Me)) throw new Exception("key wraps don't follow the group");
            // promote the group to editor: the reader can now write there
            owner.SetGroup("Ops-qx7", new[] { rae.Me }, new Dictionary<string, string> { [gcid] = Roles.Editor });
            rae.Sync();
            if (!rae.CanEdit(gcid)) throw new Exception("editor through a group not recognised");
            rae.SaveHost(gcid, null, new HostEntry { Name = "group-host-qx7", Address = "192.0.2.44" });
            owner.Sync();
            if (!owner.State.Items.Values.Any(i => i.Cid == gcid && i.Payload.Host?.Name == "group-host-qx7")) throw new Exception("the group editor's write wasn't accepted");
            // out of the group: re-keyed, the credential goes on the rotation list, the former member reads nothing new
            var epochBefore = owner.State.Epochs[gcid];
            var tasks = owner.SetGroup("Ops-qx7", Array.Empty<string>(), new Dictionary<string, string> { [gcid] = Roles.Editor });
            if (owner.State.Epochs[gcid] != epochBefore + 1 || tasks.Count != 1 || tasks[0].Name != "group-cred-qx7") throw new Exception($"leaving the group: epoch {owner.State.Epochs[gcid]}, {tasks.Count} task(s)");
            rae.Sync();
            if (rae.CanRead(gcid) || rae.State.Keys.ContainsKey(gcid) || rae.State.Items.Values.Any(i => i.Cid == gcid)) throw new Exception("the former group member kept access");
            owner.RemoveGroup("Ops-qx7");
            ed.Sync();
            if (ed.State.Roster?.Groups.Count != 0) throw new Exception("group not removed");
            ok("shared library groups: a group's per-collection access wraps the keys for its members only (a non-member never gets the key); editor through a group can write; leaving the group re-keys the collection, lists its credential for rotation and empties the former member's copy; the signed roster carries the groups");
        }
        catch (Exception ex) { fail("shared library: groups", ex); }

        // 10. removal re-keys; the leaver reads nothing new; the rotation checklist
        try
        {
            var rosterBefore = File.ReadAllBytes(Share("roster.json"));
            var raeOldKeys = rae.State.Keys[core].ToDictionary(k => k.Key, k => k.Value);
            var epochBefore = owner.State.Epochs[core];
            var tasks = owner.Remove(rae.Me);
            if (owner.State.Epochs[core] != epochBefore + 1) throw new Exception("no new epoch");
            if (tasks.Count < 2 || !tasks.All(t => t.Collection == CoreName)) throw new Exception($"rotation checklist has {tasks.Count} entries");
            var kf = JsonSerializer.Deserialize<KeysFile>(File.ReadAllBytes(Share("collections", core, "keys.json")), ShareIo.Json)!;
            if (kf.Epochs.Values.Any(w => w.ContainsKey(rae.Me))) throw new Exception("the leaver still has a wrapped key");
            var item = JsonSerializer.Deserialize<ItemFile>(File.ReadAllBytes(Directory.EnumerateFiles(ItemsDir(core), hostItem + ".*.dcxi").Single()), ShareIo.Json)!;
            if (item.Epoch != epochBefore + 1) throw new Exception("items were not re-encrypted");
            try { LibraryCrypto.OpenText(Convert.FromBase64String(raeOldKeys[epochBefore]), item.Body, item.Aad); throw new Exception("the leaver's old key opened a re-encrypted item"); }
            catch (System.Security.Cryptography.CryptographicException) { }
            rae.Sync();
            if (!rae.State.RemovedFromLibrary || rae.State.Items.Count > 0 || rae.State.Keys.Count > 0) throw new Exception("the removed member's PC kept data");
            ed.Sync();
            if (ed.State.Items.GetValueOrDefault(hostItem)?.Payload.Host?.Address != HostAddr) throw new Exception("the remaining editor lost access");
            if (ed.NameOf(ed.Me) != EdName) throw new Exception("names unreadable after the metadata key changed");
            // the leaver's forged write after removal is rejected
            var late = Forge(new LibraryClient(new LibraryState { LibraryId = owner.Id, SharePath = share, MemberId = rae.Me, EncPriv = rae.State.EncPriv, SigPriv = rae.State.SigPriv,
                Epochs = new() { [core] = epochBefore }, Keys = new() { [core] = raeOldKeys } }, Mem), core, LibraryCrypto.NewId(), 1, "late-by-leaver");
            owner.Sync();
            if (owner.State.Items.ContainsKey(late.Item)) throw new Exception("a write by the removed member was accepted");
            File.Delete(Path.Combine(ItemsDir(core), late.FileName));
            // roster roll-back
            var rosterNow = File.ReadAllBytes(Share("roster.json"));
            File.WriteAllBytes(Share("roster.json"), rosterBefore);
            ed.Sync();
            if (!ed.State.Problems.Any(p => p.Contains("OLDER")) || ed.State.Roster!.Members.Any(m => m.Id == rae.Me)) throw new Exception("a rolled-back member list was believed");
            File.WriteAllBytes(Share("roster.json"), rosterNow);
            owner.RotationDone(owner.State.Rotation[0]);
            ok($"shared library: removing a member moves the collection to epoch {epochBefore + 1}, re-encrypts every item, drops the leaver's wraps (their old key opens nothing new), wipes the leaver's cached copy, keeps the others working, rejects the leaver's later writes, refuses a rolled-back member list, and lists {tasks.Count} credentials to rotate");
        }
        catch (Exception ex) { fail("shared library: removal and rotation", ex); }

        // 11. collections and access changes
        try
        {
            var net = owner.CreateCollection(NetName);
            ed.Sync();
            if (ed.CanRead(net)) throw new Exception("a member got a new collection without being granted");
            owner.SetAccess(ed.Me, Roles.Member, new Dictionary<string, string> { [core] = Roles.Editor, [net] = Roles.Reader });
            ed.Sync();
            if (!ed.CanRead(net) || ed.CanEdit(net) || !ed.State.Keys.ContainsKey(net)) throw new Exception("granting read access failed");
            var t = owner.SetAccess(ed.Me, Roles.Member, new Dictionary<string, string> { [core] = Roles.Editor });
            ed.Sync();
            if (ed.State.Keys.ContainsKey(net) || owner.State.Epochs[net] != 2) throw new Exception("losing access didn't re-key");
            try { ed.SetAccess(owner.Me, Roles.Member, new Dictionary<string, string>()); throw new Exception("a member changed someone's access"); } catch (LibraryException) { }
            ok("shared library: a new collection is visible only to those granted; read access gives the keys; taking access away re-keys that collection; members can't change roles");
        }
        catch (Exception ex) { fail("shared library: collections and access", ex); }

        // 11b. ownership hand-over, re-signed grants, key backup
        try
        {
            var ada = LibraryClient.Join(owner.Profile(), share, "Admin-Ada-qx7", Mem);
            owner.Approve(ada.Me, ada.Keys.Fingerprint, Roles.Admin, new Dictionary<string, string>());
            ada.Sync();
            if (!ada.IsAdmin || ada.IsOwner) throw new Exception("Ada is not an admin");
            // an offer signed by anyone but the owner is ignored
            var fake = new OwnerTransfer { Seq = 1, FromId = owner.Me, ToId = ed.Me, ToEncPub = ed.Keys.EncPub, ToSigPub = ed.Keys.SigPub, Time = DateTime.UtcNow.ToString("O") };
            fake.Sig = ed.Keys.Sign(fake.Canonical(owner.Id));
            ShareIo.Write(Share("transfer-offer.json"), fake);
            if (ed.PendingTransfer() != null || ada.PendingTransfer() != null) throw new Exception("a hand-over offer not signed by the owner was believed");
            try { ed.AcceptOwnership(); throw new Exception("a forged offer was accepted"); } catch (LibraryException) { }
            File.Delete(Share("transfer-offer.json"));
            try { owner.OfferOwnership(ed.Me); throw new Exception("ownership was offered to a non-admin"); } catch (LibraryException) { }
            owner.OfferOwnership(ada.Me);
            try { ed.AcceptOwnership(); throw new Exception("someone else accepted the offer"); } catch (LibraryException) { }
            ada.Sync();
            if (ada.PendingTransfer()?.ToId != ada.Me) throw new Exception("Ada doesn't see the offer");
            ada.AcceptOwnership();
            owner.Sync(); ed.Sync();
            if (!ada.IsOwner || owner.IsOwner || !owner.IsAdmin || ed.Entry(ed.Me) == null || ed.State.Items.GetValueOrDefault(hostItem)?.Payload.Host?.Address != HostAddr)
                throw new Exception($"after the hand-over: ada {ada.MyRole}, previous owner {owner.MyRole}, editor {(ed.Entry(ed.Me) == null ? "LOST" : ed.MyRole)}; problems: {string.Join(" | ", ed.State.Problems)}");
            // a team add-on with the new owner's fingerprint, or the original one, still matches the share; a stranger's doesn't
            var p2 = owner.Profile(); p2.OwnerFingerprint = ada.Keys.Fingerprint;
            var viaNew = LibraryClient.Join(p2, share, "Joiner-qx7", Mem);
            var p3 = owner.Profile(); p3.OwnerFingerprint = viaNew.Keys.Fingerprint;
            try { LibraryClient.Join(p3, share, "x", Mem); throw new Exception("a stranger's fingerprint matched"); } catch (LibraryException) { }
            ada.Deny(viaNew.Me);
            // a forged hand-over appended to the header is refused and the cached header kept
            var headerNow = File.ReadAllBytes(Share("library.json"));
            var h = JsonSerializer.Deserialize<LibraryHeader>(headerNow, ShareIo.Json)!;
            var forged = new OwnerTransfer { Seq = h.Transfers.Count + 1, FromId = ada.Me, ToId = ed.Me, ToEncPub = ed.Keys.EncPub, ToSigPub = ed.Keys.SigPub, Time = DateTime.UtcNow.ToString("O") };
            forged.Sig = ed.Keys.Sign(forged.Canonical(owner.Id));
            h.Transfers.Add(forged);
            ShareIo.Write(Share("library.json"), h);
            ed.Sync();
            if (ed.IsOwner || !ed.State.Problems.Any(p => p.Contains("hand-over"))) throw new Exception("a forged hand-over was believed: " + string.Join(" | ", ed.State.Problems));
            File.WriteAllBytes(Share("library.json"), headerNow);
            // members an admin approved stay valid when that admin is demoted (the owner re-signs their grants)
            var cy = LibraryClient.Join(owner.Profile(), share, "Member-Cy-qx7", Mem);
            owner.Approve(cy.Me, cy.Keys.Fingerprint, Roles.Member, new Dictionary<string, string> { [core] = Roles.Reader });
            ada.SetAccess(owner.Me, Roles.Member, new Dictionary<string, string> { [core] = Roles.Editor });
            cy.Sync(); owner.Sync();
            if (cy.State.Pending || !cy.CanRead(core) || owner.IsAdmin || !owner.CanEdit(core)) throw new Exception($"after demoting: cy {(cy.State.Pending ? "pending" : cy.MyRole)}, previous owner {owner.MyRole}");
            ed.Sync();
            if (ed.State.Items.GetValueOrDefault(hostItem) is not { Warning: null } still || still.Payload.Host?.Address != HostAddr || ed.State.Problems.Any(p => p.Contains("rejected") || p.Contains("not signed")))
                throw new Exception("files signed by the demoted admin stopped verifying: " + string.Join(" | ", ed.State.Problems));
            // key backup: sealed, wrong password refused, restores the same member
            try { ed.ExportKeys("short"); throw new Exception("a short backup password was accepted"); } catch (LibraryException) { }
            var backup = ed.ExportKeys("correct horse battery qx7");
            var btext = Encoding.UTF8.GetString(backup);
            if (btext.Contains(ed.State.SigPriv[..40]) || btext.Contains(EdName) || btext.Contains(share.Replace("\\", "\\\\"))) throw new Exception("the key backup holds plaintext");
            try { LibraryClient.ImportKeys(backup, "wrong password qx7!"); throw new Exception("a wrong password opened the backup"); } catch (LibraryException) { }
            var st = LibraryClient.ImportKeys(backup, "correct horse battery qx7");
            var ed2 = new LibraryClient(st, Mem);
            ed2.Sync();
            if (ed2.Me != ed.Me || ed2.Keys.Fingerprint != ed.Keys.Fingerprint || ed2.State.Items.GetValueOrDefault(hostItem)?.Payload.Host?.Address != HostAddr) throw new Exception("the restored keys don't read the library");
            ok("shared library: ownership is handed to an admin only by the owner's signed offer and that admin's acceptance (forged offers and forged header links are refused, others can't accept); every grant stays valid; team add-ons with the old or new owner's fingerprint still match; demoting an admin keeps the members they approved; key backups are password-sealed, refuse a wrong password and restore the same member");
        }
        catch (Exception ex) { fail("shared library: ownership hand-over and key backup", ex); }

        // 12. the app: shared items beside local ones, never overwriting them
        try
        {
            SharedLibraries.Register(ed);
            var sh = SharedLibraries.FindHost(SharedLibraries.ItemId(ed.Id, hostItem)) ?? throw new Exception("shared host not materialised");
            if (!sh.IsShared || !sh.Display.StartsWith("⇄ ") || sh.CredentialId != SharedLibraries.ItemId(ed.Id, credItem)) throw new Exception("shared host not labelled / linked: " + sh.Display);
            if (AppData.Current.FindCredential(sh.CredentialId)?.Secret != Secret || Ssh.SshConnector.CredentialFor(sh)?.Secret != Secret) throw new Exception("shared credential not resolved for connecting");
            if (AppData.Current.Hosts.Any(h => h.IsShared) || AppData.Current.Credentials.Any(c => c.IsShared)) throw new Exception("a shared item got into the local data");
            // a local host with the same name stays separate and untouched
            var local = new HostEntry { Name = HostName, Address = "203.0.113.20", Username = "local-qx7", Group = "Selftest" };
            var localCred = new Credential { Name = "local-cred-qx7", Kind = CredentialKind.Password, Username = "local-qx7", Secret = "local-secret-qx7" };
            local.CredentialId = localCred.Id;
            AppData.Current.Hosts.Add(local); AppData.Current.Credentials.Add(localCred); AppData.Save();
            var localJson = JsonSerializer.Serialize(local, SecureStore.Json);
            await SharedLibraries.SyncAsync(ed);
            if (JsonSerializer.Serialize(AppData.Current.FindHost(local.Id), SecureStore.Json) != localJson || AppData.Current.FindHost(local.Id)!.Address != "203.0.113.20") throw new Exception("sync changed a local host");
            // copy to my hosts / publish
            SharedLibraryUi.CopyHostsToMine(new() { sh });
            var copy = AppData.Current.Hosts.LastOrDefault(h => h.Name.StartsWith(HostName + " (from library)")) ?? throw new Exception("no local copy");
            if (copy.IsShared || copy.Id == sh.Id || copy.CredentialId != sh.CredentialId) throw new Exception("copy is not a new local host referring to the shared credential");
            var target = SharedLibraryUi.Writable().First(x => x.Client == ed && x.Cid == core);
            var ids = SharedLibraryUi.Publish(target, new() { local }, new() { localCred });
            var pub = ed.State.Items[ids[0]].Payload.Host!;
            if (pub.Address != "203.0.113.20" || pub.CredentialId == localCred.Id || ed.State.Items[pub.CredentialId!].Payload.Cred?.Secret != "local-secret-qx7") throw new Exception("publish didn't map the credential");
            if (JsonSerializer.Serialize(AppData.Current.FindHost(local.Id), SecureStore.Json) != localJson) throw new Exception("publishing changed the local host");
            AppData.Current.Hosts.Remove(copy); AppData.Current.Hosts.Remove(local); AppData.Current.Credentials.Remove(localCred); AppData.Save();
            ok("shared library: shared hosts and credentials appear beside local ones marked ⇄ shared, resolve for connecting, never enter data.dcx; a same-named local host is untouched by sync; Copy to my hosts makes a new local host; Publish copies a local host and its credential into a collection and leaves the local ones as they were");

            // 13. the screens
            main.Navigate("hosts"); await Settle(500);
            if (main.Panes.ActiveTab?.Content is not HostsView hv) throw new Exception("hosts screen didn't open");
            var nodes = hv.FoldersForTest.Items.OfType<TreeViewItem>().Select(i => i.Header?.ToString() ?? "").ToList();
            if (!nodes.Any(n => n.Contains("Shared · " + LibName))) throw new Exception("no Shared node in the hosts tree: " + string.Join(" | ", nodes));
            main.Navigate("credentials"); await Settle(400);
            main.Navigate("library"); await Settle(600);
            if (main.Panes.ActiveTab?.Content is not SharedLibraryView) throw new Exception("Shared library screen didn't open");
            foreach (var key in new[] { "library", "credentials", "hosts" })
                if (main.Panes.AllTabs.FirstOrDefault(t => t.Content is SharedLibraryView && key == "library" || t.Content is CredentialsView && key == "credentials" || t.Content is HostsView && key == "hosts") is { } tab) main.CloseTab(tab);
            await Settle();
            ok("shared library: the Hosts tree shows a Shared node per library with its collections; the Credentials and Shared library screens render with live data (no window, no dialog)");
        }
        catch (Exception ex) { fail("shared library: app integration", ex); }

        // 14. the add-on package
        try
        {
            var pkg = Path.Combine(work, "team.dcxaddon");
            SharedLibraries.WritePackage(owner.Profile(), "Owner-qx7", pkg);
            var info = AddonPackages.Inspect(pkg);
            if (info.Refusal != null || !info.Signature.StartsWith("shared library") || info.Manifest.Permissions.Count != 0) throw new Exception("team package: " + (info.Refusal ?? info.Signature));
            if (Encoding.UTF8.GetString(File.ReadAllBytes(pkg)).Contains(Secret)) throw new Exception("the package holds a secret");
            string Bad(string name, string libraryJson, object? extra = null)
            {
                var p = Path.Combine(work, name + ".dcxaddon");
                if (File.Exists(p)) File.Delete(p);
                using (var z = ZipFile.Open(p, ZipArchiveMode.Create))
                {
                    var m = new Dictionary<string, object> { ["api"] = 2, ["id"] = "com.selftest.library." + name, ["name"] = "x", ["version"] = "1.0.0", ["publisher"] = "Self-test", ["library"] = "library.json" };
                    if (extra is Dictionary<string, object> e) foreach (var kv in e) m[kv.Key] = kv.Value;
                    using (var w = new StreamWriter(z.CreateEntry("addon.json").Open())) w.Write(JsonSerializer.Serialize(m));
                    using (var w = new StreamWriter(z.CreateEntry("library.json").Open())) w.Write(libraryJson);
                    using (var w = new StreamWriter(z.CreateEntry("run.ps1").Open())) w.Write("exit 0");
                }
                return AddonPackages.Inspect(p).Refusal ?? "";
            }
            var fp = owner.State.OwnerFingerprint;
            var cases = new (string Name, string Json, object? Extra)[]
            {
                ("unknownfield", $"{{\"libraryId\":\"{owner.Id}\",\"name\":\"x\",\"ownerFingerprint\":\"{fp}\",\"script\":\"x\"}}", null),
                ("badid", $"{{\"libraryId\":\"XYZ\",\"name\":\"x\",\"ownerFingerprint\":\"{fp}\"}}", null),
                ("badfp", $"{{\"libraryId\":\"{owner.Id}\",\"name\":\"x\",\"ownerFingerprint\":\"1234\"}}", null),
                ("badpath", $"{{\"libraryId\":\"{owner.Id}\",\"name\":\"x\",\"ownerFingerprint\":\"{fp}\",\"path\":\"relative\\\\folder\"}}", null),
                ("withentry", $"{{\"libraryId\":\"{owner.Id}\",\"name\":\"x\",\"ownerFingerprint\":\"{fp}\"}}", new Dictionary<string, object> { ["entry"] = "run.ps1", ["commands"] = new[] { new { id = "x", title = "x", context = "global" } } }),
                ("withperms", $"{{\"libraryId\":\"{owner.Id}\",\"name\":\"x\",\"ownerFingerprint\":\"{fp}\"}}", new Dictionary<string, object> { ["permissions"] = new[] { "hosts.read" } }),
                ("notjson", "{ nope", null),
                ("idonly", $"{{\"libraryId\":\"{owner.Id}\",\"name\":\"x\"}}", null),
                ("pathonly", "{\"name\":\"x\",\"path\":\"C:\\\\x\"}", null),
                ("noname", "{}", null),
            };
            var accepted = cases.Where(c => Bad(c.Name, c.Json, c.Extra).Length == 0).Select(c => c.Name).ToList();
            if (accepted.Count > 0) throw new Exception("malformed library add-ons accepted: " + string.Join(", ", accepted));

            // the general add-on (samples/addons/shared-library): switches the Shared library screen on, names no library
            string? sample = null;
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null && sample == null; d = d.Parent)
                if (Directory.Exists(Path.Combine(d.FullName, "samples", "addons", "shared-library"))) sample = Path.Combine(d.FullName, "samples", "addons", "shared-library");
            var gpkg = Path.Combine(work, "general.dcxaddon");
            if (File.Exists(gpkg)) File.Delete(gpkg);
            if (sample != null) ZipFile.CreateFromDirectory(sample, gpkg);
            else
                using (var z = ZipFile.Open(gpkg, ZipArchiveMode.Create))
                {
                    using (var w = new StreamWriter(z.CreateEntry("addon.json").Open())) w.Write("""{"api":2,"id":"com.darkcipher.shared-library","name":"Shared library","version":"1.0.1","publisher":"DarkCipher Systems","library":"library.json"}""");
                    using (var w = new StreamWriter(z.CreateEntry("library.json").Open())) w.Write("""{"name":"Shared library"}""");
                }
            // 0.3.0 §22: team features are built in; with them switched off the add-on path still works as before.
            AppSettings.Current.TeamFeatures = false;
            if (SharedLibraries.Enabled) throw new Exception("Shared library is on before any library add-on is installed");
            var ginfo = AddonPackages.Inspect(gpkg);
            if (ginfo.Refusal != null || !ginfo.Signature.StartsWith("shared library") || ginfo.Manifest.Permissions.Count != 0 || ginfo.Manifest.Entry.Length != 0) throw new Exception("general add-on: " + (ginfo.Refusal ?? ginfo.Signature));
            var g = AddonPackages.Install(ginfo, Array.Empty<string>());
            if (!SharedLibraries.Enabled) throw new Exception("installing the general add-on didn't switch Shared library on");
            try { SharedLibraries.Join(g, share, "x"); throw new Exception("joining with the general add-on was allowed"); } catch (LibraryException) { }
            // Sidebar → TEAM (0.2.0-preview.2): the section exists while a library add-on is enabled; Team credentials shows
            // only shared ones, VAULT → Credentials everything again; Team hosts opens on a library's node.
            await Settle(300);
            if (!main.NavSectionOpen.ContainsKey("TEAM")) throw new Exception("no TEAM section with a library add-on enabled");
            main.Navigate("team:credentials"); await Settle(400);
            if (main.Panes.ActiveTab?.Content is not CredentialsView teamCreds || teamCreds.RowsForTest != SharedLibraries.Credentials.Count) throw new Exception("Team credentials isn't shared-only");
            main.Navigate("credentials"); await Settle(300);
            if (teamCreds.RowsForTest != AppData.Current.Credentials.Count + SharedLibraries.Credentials.Count) throw new Exception("VAULT → Credentials stayed shared-only");
            main.Navigate("team:hosts"); await Settle(400);
            if (main.Panes.ActiveTab?.Content is not HostsView teamHosts || (SharedLibraries.Clients.Count > 0 && !teamHosts.FolderForTest.StartsWith("lib:", StringComparison.Ordinal))) throw new Exception("Team hosts didn't open on a library");
            foreach (var t in main.Panes.AllTabs.Where(t => t.Content is CredentialsView or HostsView).ToList()) main.CloseTab(t);
            main.Navigate("addons"); await Settle(500);
            var gtext = AddonsView.Current?.ShowForTest(g) ?? throw new Exception("Add-ons screen didn't open");
            if (!gtext.Contains("switched on")) throw new Exception("Add-ons screen doesn't describe the general add-on: " + gtext.Replace("\n", " | "));
            AddonPackages.SetEnabled(g, false);
            if (SharedLibraries.Enabled) throw new Exception("disabling the general add-on left Shared library on");
            await Settle(300);
            if (main.NavSectionOpen.ContainsKey("TEAM")) throw new Exception("the TEAM section stayed after the add-on was disabled");
            AddonPackages.Remove(g);
            AppSettings.Current.TeamFeatures = true;
            if (!SharedLibraries.Enabled) throw new Exception("team features built in (TeamFeatures on) don't switch Shared library on");
            ok($"shared library: the general add-on{(sample != null ? " (repo sample)" : " (inline copy)")} installs with no program or permission, switches the Shared library screen and the sidebar's TEAM section on (Team hosts opens on a library's node, Team credentials is shared-only, VAULT → Credentials shows all again; gone when disabled), and can't be used to join a library");
            // installed + member keys in the store = a client; disabled = gone; removed = keys kept until forgotten
            SharedLibraries.Persist(owner.State);
            var a = AddonPackages.Install(info, Array.Empty<string>());
            if (SharedLibraries.Client(owner.Id) == null) throw new Exception("the installed add-on didn't load the library");
            main.Navigate("addons"); await Settle(500);
            var detail = AddonsView.Current?.ShowForTest(a) ?? throw new Exception("Add-ons screen didn't open");
            if (!detail.Contains("SHARED LIBRARY") || !detail.Contains("your role") || detail.Contains("theme ")) throw new Exception("Add-ons screen doesn't describe the library add-on: " + detail.Replace("\n", " | "));
            if (main.Panes.AllTabs.FirstOrDefault(t => t.Content is AddonsView) is { } addonsTab) main.CloseTab(addonsTab);
            await Settle();
            AddonPackages.SetEnabled(a, false);
            if (SharedLibraries.Client(owner.Id) != null) throw new Exception("a disabled library still shows");
            AddonPackages.Remove(a);
            if (!SharedLibraries.Orphans().Contains(owner.Id)) throw new Exception("keys of a removed library add-on are not listed for forgetting");
            SharedLibraries.Forget(owner.Id);
            if (SharedLibraries.Orphans().Contains(owner.Id) || File.Exists(Path.Combine(SecureStore.DataDir, SharedLibraries.StateFile(owner.Id)))) throw new Exception("Forget left the keys");
            if (AddonPackages.Permission("library.read") == null) throw new Exception("library.read permission missing");
            ok($"shared library: the team add-on is data only (no program, no permission, no secret) and installs; {cases.Length} malformed library add-ons are refused at inspection; enable/disable/remove load and unload the library; a removed library's keys stay sealed in the store until Forget");
        }
        catch (Exception ex) { fail("shared library: add-on package", ex); }
        finally
        {
            SharedLibraries.Load();   // no library add-on installed in the scratch store: no clients left
            SharedLibraries.WatchEnabled = true;
            LibraryClient.Gate = gateWas;
        }
        var localAfter = JsonSerializer.Serialize(AppData.Current, SecureStore.Json);
        if (localAfter != localBefore) fail("shared library: local data changed by the library tests", null);
    }
}
