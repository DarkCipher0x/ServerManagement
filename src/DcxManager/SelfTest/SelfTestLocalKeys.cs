using System.IO;
using System.Security.Cryptography;
using DcxManager.Services;
using DcxManager.Ssh;
using DcxManager.Views;

namespace DcxManager;

/// <summary>
/// 0.6.30 (ADR-107): this PC's own SSH keys. A scratch .ssh folder of demo keys made here (never the real one): what is
/// found and in which order, Quick Start's page importing through its own button (a passphrase asked, a wrong one asked
/// again), quick connect's key, the files left as they were, and the pick-up at start (new keys once, a removed key never
/// back, a key with a passphrase only mentioned, the "adding credentials" restriction obeyed).
/// </summary>
public static class LocalKeysTests
{
    public static async Task RunAsync(string scratch, Action<string> ok, Action<string, Exception?> fail)
    {
        var s = AppSettings.Current;
        var data = AppData.Current;
        var before = data.Credentials.Select(c => c.Id).ToHashSet();
        var (defBefore, autoBefore, allowBefore, seenBefore) = (s.DefaultKeyCredentialId, s.LocalSshKeysAutoImport, s.AllowCredentialAdd, s.LocalSshKeysSeen.ToList());
        var dir = Path.Combine(scratch, "local-ssh-keys-demo");
        try
        {
            Directory.CreateDirectory(dir);
            LocalKeys.DirForTest = dir;
            s.DefaultKeyCredentialId = null;
            bool keygen = KeyTools.SshKeygenPath() != null;

            var ed = await KeyTools.GenerateAsync("demo-ed", "ed25519", "ops");
            File.WriteAllText(Path.Combine(dir, "id_ed25519"), ed.Secret);
            File.WriteAllText(Path.Combine(dir, "id_ed25519.pub"), string.Join(' ', ed.PublicKey.Split(' ').Take(2)) + " ops@demo-pc\n");
            Credential? rsa = null;
            if (keygen)
            {
                rsa = await KeyTools.GenerateAsync("demo-rsa", "rsa3072", "ops");
                await KeyExport.ExportAsync(rsa, Path.Combine(dir, "id_rsa"), "demo passphrase 1");
                File.WriteAllText(Path.Combine(dir, "id_rsa.pub"), rsa.PublicKey + "\n");
            }
            var work = await KeyTools.GenerateAsync("demo-work", "ecdsa256", "ops");
            File.WriteAllText(Path.Combine(dir, "work_key"), work.Secret);
            File.WriteAllText(Path.Combine(dir, "id_ed25519_sk"), "-----BEGIN OPENSSH PRIVATE KEY-----\nAAAA\n-----END OPENSSH PRIVATE KEY-----\n");   // secret-scan: example
            File.WriteAllText(Path.Combine(dir, "garbage"), "-----BEGIN RSA PRIVATE KEY-----\nnot a key\n-----END RSA PRIVATE KEY-----\n");   // secret-scan: example
            // Not keys: never listed.
            File.WriteAllText(Path.Combine(dir, "known_hosts"), "192.0.2.10 " + string.Join(' ', ed.PublicKey.Split(' ').Take(2)) + "\n");
            File.WriteAllText(Path.Combine(dir, "config"), "Host demo\n  HostName 192.0.2.10\n  User ops\n");
            File.WriteAllText(Path.Combine(dir, "authorized_keys"), ed.PublicKey + "\n");
            File.WriteAllText(Path.Combine(dir, "notes.txt"), "-----BEGIN OPENSSH PRIVATE KEY----- (a note, not a key)\n");   // secret-scan: example
            var hashes = Hashes(dir);

            // ---- what is found -----------------------------------------------------------------------------------------
            var found = LocalKeys.Scan(data.Credentials);
            var names = found.Select(f => f.FileName).ToList();
            var want = new List<string> { "id_ed25519" };
            if (keygen) want.Add("id_rsa");
            want.AddRange(new[] { "id_ed25519_sk", "garbage", "work_key" });
            Expect(names.SequenceEqual(want), "found, defaults first: " + string.Join(", ", names));
            var fEd = found[0];
            Expect(fEd is { IsDefault: true, State: LocalKeys.State.Ready } && fEd.Fingerprint == ed.Fingerprint && fEd.Comment == "ops@demo-pc" && fEd.KeyType == "ssh-ed25519", "id_ed25519 ready, with its .pub comment");
            if (keygen)
            {
                var fRsa = found[1];
                Expect(fRsa is { IsDefault: true, State: LocalKeys.State.NeedsPassphrase } && fRsa.Fingerprint == rsa!.Fingerprint, "id_rsa needs its passphrase; its fingerprint comes from the .pub");
                Expect(LocalKeys.IsEncrypted(File.ReadAllText(Path.Combine(dir, "id_rsa"))) && !LocalKeys.IsEncrypted(ed.Secret), "passphrase detection (OpenSSH format)");
            }
            Expect(found.Single(f => f.FileName == "id_ed25519_sk") is { State: LocalKeys.State.Unsupported, IsDefault: false } sk && sk.Why.Contains("FIDO"), "a security key is listed, not usable");
            Expect(found.Single(f => f.FileName == "garbage").State == LocalKeys.State.Unsupported, "an unreadable key is listed, not usable");
            Expect(found.Single(f => f.FileName == "work_key") is { State: LocalKeys.State.Ready, IsDefault: false }, "another key is found, not ticked by default");
            Expect(!fEd.ToString()!.Contains("PRIVATE KEY"), "a found key never prints its text");

            // ---- Quick Start's page, through its own button ------------------------------------------------------------
            var w = new SetupWindow { WindowStartupLocation = System.Windows.WindowStartupLocation.Manual, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                w.ShowForTest(w.PageTitlesForTest.ToList().IndexOf("Your SSH keys"));
                var panel = w.SshKeysForTest ?? throw new Exception("the SSH keys page has no key list");
                Expect(panel.TickedForTest.SequenceEqual(want.Select(n => n is "id_ed25519" or "id_rsa")), "default keys ticked, others not: " + string.Join(",", panel.TickedForTest));
                int asked = 0;
                panel.PassphraseForTest = (f, again) => { asked++; return again ? "demo passphrase 1" : "a wrong passphrase"; };
                await panel.ImportAsync();
                var edCred = data.Credentials.FirstOrDefault(c => !before.Contains(c.Id) && c.Fingerprint == ed.Fingerprint) ?? throw new Exception("id_ed25519 wasn't imported: " + panel.ResultForTest);
                Expect(edCred.Name == "id_ed25519" && edCred.Secret == ed.Secret && edCred.Category == "ssh" && edCred.Passphrase.Length == 0, "id_ed25519 copied as it is");
                Expect(s.DefaultKeyCredentialId == edCred.Id, "quick connect uses the first default key");
                if (keygen)
                {
                    var rsaCred = data.Credentials.FirstOrDefault(c => !before.Contains(c.Id) && c.Fingerprint == rsa!.Fingerprint) ?? throw new Exception("id_rsa wasn't imported: " + panel.ResultForTest);
                    Expect(rsaCred.Passphrase == "demo passphrase 1" && asked == 2, $"id_rsa keeps its passphrase, asked again after a wrong one (asked {asked}×)");
                }
                Expect(!data.Credentials.Any(c => !before.Contains(c.Id) && c.Fingerprint == work.Fingerprint), "a key that wasn't ticked isn't imported");
                Expect(panel.ResultForTest.Contains($"Imported {(keygen ? 2 : 1)} key(s)"), "says what was imported: " + panel.ResultForTest);
                Expect(panel.FoundForTest.First().State == LocalKeys.State.AlreadyStored, "the list then shows it as already in the store");
            }
            finally { w.Close(); }
            Expect(Hashes(dir).SequenceEqual(hashes), "the key files are unchanged");

            // ---- the pick-up at start ----------------------------------------------------------------------------------
            var said = await LocalKeyImport.PickUpAsync();
            Expect(said.Contains("work_key") && data.Credentials.Any(c => !before.Contains(c.Id) && c.Fingerprint == work.Fingerprint), "at start, a key not seen before is picked up: " + said);
            var def = s.DefaultKeyCredentialId;
            var next = await KeyTools.GenerateAsync("demo-next", "ecdsa256", "ops");
            File.WriteAllText(Path.Combine(dir, "id_ecdsa"), next.Secret);
            if (keygen)
            {
                var locked = await KeyTools.GenerateAsync("demo-locked", "ed25519", "ops");
                await KeyExport.ExportAsync(locked, Path.Combine(dir, "deploy_key"), "demo passphrase 2");
            }
            said = await LocalKeyImport.PickUpAsync();
            var nextCred = data.Credentials.FirstOrDefault(c => !before.Contains(c.Id) && c.Fingerprint == next.Fingerprint);
            Expect(nextCred != null && said.Contains("id_ecdsa"), "a new default key is picked up: " + said);
            if (keygen) Expect(said.Contains("deploy_key") && said.Contains("passphrase"), "a new key with a passphrase is only mentioned: " + said);
            Expect(s.DefaultKeyCredentialId == def, "the pick-up never changes quick connect's key");
            Expect(await LocalKeyImport.PickUpAsync() == "", "nothing new: nothing said, nothing added");
            data.Credentials.Remove(nextCred!);
            AppData.Save();
            Expect(await LocalKeyImport.PickUpAsync() == "" && !data.Credentials.Any(c => c.Fingerprint == next.Fingerprint), "a key you removed isn't brought back");

            var later = await KeyTools.GenerateAsync("demo-later", "ecdsa256", "ops");
            File.WriteAllText(Path.Combine(dir, "later_key"), later.Secret);
            s.AllowCredentialAdd = false;
            said = await LocalKeyImport.PickUpAsync();
            Expect(said.Contains("weren't picked up") && !data.Credentials.Any(c => c.Fingerprint == later.Fingerprint), "the adding-credentials restriction stops the pick-up: " + said);
            s.AllowCredentialAdd = allowBefore;
            said = await LocalKeyImport.PickUpAsync();
            Expect(said.Contains("later_key"), "and once allowed, the key is picked up: " + said);

            ok($"SSH keys on this PC (ADR-107): defaults first (ed25519, then RSA with a passphrase{(keygen ? "" : " — skipped, no ssh-keygen")}), security keys and unreadable keys listed but not usable, config / known_hosts / .pub never listed; Quick Start's page imports the ticked keys (a wrong passphrase asked again), makes the first default key quick connect's; the files are unchanged; at start new keys are picked up once, a removed key stays removed, a passphrase key is only mentioned, the restriction is obeyed");
        }
        catch (Exception ex) { fail("SSH keys on this PC", ex); }
        finally
        {
            LocalKeys.DirForTest = null;
            data.Credentials.RemoveAll(c => !before.Contains(c.Id));
            (s.DefaultKeyCredentialId, s.LocalSshKeysAutoImport, s.AllowCredentialAdd) = (defBefore, autoBefore, allowBefore);
            s.LocalSshKeysSeen = seenBefore;
            try { AppData.Save(); AppSettings.Save(); } catch { }
        }
    }

    private static List<string> Hashes(string dir) =>
        Directory.EnumerateFiles(dir).OrderBy(p => p, StringComparer.Ordinal).Select(p => Path.GetFileName(p) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))).ToList();

    private static void Expect(bool condition, string what) { if (!condition) throw new Exception(what); }
}
