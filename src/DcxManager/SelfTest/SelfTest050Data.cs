using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DcxManager.Import;
using DcxManager.Services;

namespace DcxManager;

/// <summary>
/// 0.5.0 data tests (docs/PLAN-0.5.0.md #6, #7): imports from other connection managers (every parser on a made-up file,
/// mRemoteNG's encryption both ways, hostile input) and the backup rules (exports off, secrets left out, short passwords,
/// imports off, scheduled backups with pruning). Files live in a scratch folder; nothing on this PC is read or changed
/// outside the throwaway store (PuTTY / WinSCP registry reads are not exercised here).
/// </summary>
public static class Release050DataTests
{
    public static Task RunAsync(Action<string> ok, Action<string, Exception?> fail)
    {
        var dir = Path.Combine(Path.GetTempPath(), "dcx-st-import-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            Gcm(ok, fail);
            Parsers(dir, ok, fail);
            Apply(dir, ok, fail);
            Backups(dir, ok, fail);
            Identity(ok, fail);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
        return Task.CompletedTask;
    }

    private static string W(string dir, string name, string text) { var p = Path.Combine(dir, name); File.WriteAllText(p, text, new UTF8Encoding(false)); return p; }

    // GCM with any nonce length: equal to .NET's AesGcm for 96-bit nonces, round trip and tamper detection for 128-bit ones.
    private static void Gcm(Action<string> ok, Action<string, Exception?> fail)
    {
        try
        {
            for (int i = 0; i < 20; i++)
            {
                var key = RandomNumberGenerator.GetBytes(32); var nonce = RandomNumberGenerator.GetBytes(12);
                var plain = RandomNumberGenerator.GetBytes(i * 7); var aad = RandomNumberGenerator.GetBytes(i % 5 * 9);
                var (c, t) = GcmAnyIv.Encrypt(key, nonce, plain, aad);
                var c2 = new byte[plain.Length]; var t2 = new byte[16];
                using (var g = new AesGcm(key, 16)) g.Encrypt(nonce, plain, c2, t2, aad);
                if (!c.SequenceEqual(c2) || !t.SequenceEqual(t2)) throw new Exception($"differs from AesGcm at length {plain.Length}");
                var n16 = RandomNumberGenerator.GetBytes(16);
                var (c3, t3) = GcmAnyIv.Encrypt(key, n16, plain, aad);
                if (!GcmAnyIv.Decrypt(key, n16, c3, t3, aad).SequenceEqual(plain)) throw new Exception("128-bit nonce round trip");
                if (c3.Length > 0) { c3[0] ^= 1; try { GcmAnyIv.Decrypt(key, n16, c3, t3, aad); throw new Exception("a changed ciphertext was accepted"); } catch (CryptographicException) { } }
            }
            ok("import crypto: AES-GCM for any nonce length matches .NET's AesGcm on 20 random 96-bit cases, round-trips 128-bit nonces (mRemoteNG's) and refuses a changed byte before returning any plaintext");
        }
        catch (Exception ex) { fail("import crypto (GCM)", ex); }
    }

    private static void Parsers(string dir, Action<string> ok, Action<string, Exception?> fail)
    {
        // mRemoteNG, default master password, inherited credentials, a hostile host and an unsupported protocol.
        try
        {
            string Seal(string t, string pw) => MRemoteNgSource.Crypto.EncryptGcmForTest(t, pw);
            string File1(string pw, string prot) =>
                $"<?xml version=\"1.0\" encoding=\"utf-8\"?><mrng:Connections xmlns:mrng=\"http://mremoteng.org\" Name=\"Connections\" EncryptionEngine=\"AES\" BlockCipherMode=\"GCM\" KdfIterations=\"1000\" FullFileEncryption=\"false\" Protected=\"{Seal(prot, pw)}\" ConfVersion=\"2.6\">" +
                $"<Node Name=\"Lab\" Type=\"Container\" Username=\"labuser\" Domain=\"\" Password=\"{Seal("s3cret-pass", pw)}\">" +
                "<Node Name=\"web-01\" Type=\"Connection\" Hostname=\"192.0.2.21\" Port=\"22\" Protocol=\"SSH2\" InheritUsername=\"true\" InheritPassword=\"true\" />" +
                "<Node Name=\"dc-01\" Type=\"Connection\" Hostname=\"192.0.2.22\" Port=\"3389\" Protocol=\"RDP\" Username=\"admin\" Domain=\"CORP\" />" +
                "<Node Name=\"evil\" Type=\"Connection\" Hostname=\"x;rm -rf /\" Protocol=\"SSH2\" />" +
                "<Node Name=\"vnc\" Type=\"Connection\" Hostname=\"192.0.2.23\" Protocol=\"VNC\" /></Node></mrng:Connections>";
            var src = new MRemoteNgSource();
            var r = src.Read(W(dir, "confCons.xml", File1("mR3m", "ThisIsNotProtected")));
            var web = r.Hosts.SingleOrDefault(h => h.Name == "web-01") ?? throw new Exception("web-01 missing: " + string.Join("; ", r.Warnings));
            var dc = r.Hosts.Single(h => h.Name == "dc-01");
            if (r.Hosts.Count != 2 || web.Folder != "Lab" || web.Username != "labuser" || web.Password != "s3cret-pass" || web.Port != 22) throw new Exception("inheritance or decryption");
            if (dc.Protocol != "rdp" || dc.Username != @"CORP\admin" || dc.Password != null) throw new Exception("RDP user/domain");
            if (r.Warnings.Count < 2) throw new Exception("the hostile host and VNC weren't reported");

            var custom = W(dir, "protected.xml", File1("Correct-Horse-1", "ThisIsProtected"));
            try { src.Read(custom); throw new Exception("a protected file opened without its password"); } catch (MasterPasswordNeededException) { }
            try { src.Read(custom, "wrong-password"); throw new Exception("a wrong master password was accepted"); } catch (MasterPasswordNeededException) { }
            if (src.Read(custom, "Correct-Horse-1").Hosts.Single(h => h.Name == "web-01").Password != "s3cret-pass") throw new Exception("custom master password");

            var inner = "<Node Name=\"ff-01\" Type=\"Connection\" Hostname=\"192.0.2.24\" Port=\"2200\" Protocol=\"SSH2\" Username=\"ff\" />";
            var full = W(dir, "full.xml", $"<mrng:Connections xmlns:mrng=\"http://mremoteng.org\" EncryptionEngine=\"AES\" BlockCipherMode=\"GCM\" KdfIterations=\"1000\" FullFileEncryption=\"true\" Protected=\"{Seal("ThisIsNotProtected", "mR3m")}\">{Seal(inner, "mR3m")}</mrng:Connections>");
            if (src.Read(full).Hosts.Single().Port != 2200) throw new Exception("whole-file encryption");

            // Pre-1.76 files: AES-CBC with an MD5 key (read-only support).
            using var aes = Aes.Create();
#pragma warning disable CA5351
            aes.Key = MD5.HashData(Encoding.UTF8.GetBytes("mR3m"));
#pragma warning restore CA5351
            var iv = RandomNumberGenerator.GetBytes(16);
            var legacyPw = Convert.ToBase64String(iv.Concat(aes.EncryptCbc(Encoding.UTF8.GetBytes("old-pass"), iv)).ToArray());
            var legacy = W(dir, "legacy.xml", $"<Connections Name=\"Connections\" ConfVersion=\"2.4\"><Node Name=\"old\" Type=\"Connection\" Hostname=\"192.0.2.25\" Protocol=\"SSH2\" Username=\"o\" Password=\"{legacyPw}\" /></Connections>");
            if (src.Read(legacy).Hosts.Single().Password != "old-pass") throw new Exception("legacy CBC");

            // XML with a DTD (XXE / entity expansion) is refused outright.
            try { src.Read(W(dir, "xxe.xml", "<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY e SYSTEM \"file:///c:/windows/win.ini\">]><Connections>&e;</Connections>")); throw new Exception("a DTD was processed"); }
            catch (System.Xml.XmlException) { }
            ok("import mRemoteNG: folders, inherited user and password, DOMAIN\\user for RDP, passwords decrypted with the default key, a custom master password asked for (wrong one refused), whole-file encryption, the pre-1.76 CBC format; a hostile host name and VNC skipped with a note; a DTD (XXE) refused");
        }
        catch (Exception ex) { fail("import mRemoteNG", ex); }

        // Every other file format on a small made-up file.
        try
        {
            var crt = new SecureCrtSource().Read(W(dir, "crt.xml", "<VanDyke version=\"3.0\"><key name=\"Sessions\"><key name=\"Core\"><key name=\"sw-01\"><string name=\"Hostname\">192.0.2.31</string><dword name=\"[SSH2] Port\">2222</dword><string name=\"Username\">netops</string><string name=\"Protocol Name\">SSH2</string><string name=\"Password V2\">02:abcdef</string></key></key></key></VanDyke>"));
            if (crt.Hosts.Single() is not { Folder: "Core", Port: 2222, Username: "netops", Password: null } || crt.Warnings.Count != 1) throw new Exception("SecureCRT XML");
            var crtIni = new SecureCrtSource().Read(W(dir, "sw-02.ini", "S:\"Hostname\"=192.0.2.32\nD:\"[SSH2] Port\"=00000016\nS:\"Username\"=root\nS:\"Protocol Name\"=SSH2\n"));
            if (crtIni.Hosts.Single() is not { Name: "sw-02", Port: 22, Username: "root" }) throw new Exception("SecureCRT .ini");

            var csv = new RoyalTsSource().Read(W(dir, "royal.csv", "Name,ComputerName,Port,Username,Password,Folder,Type\nfs-01,192.0.2.41,22,ops,\"p,w\"\"1\",Storage/NAS,SSH Connection\n"));
            if (csv.Hosts.Single() is not { Folder: "Storage/NAS", Password: "p,w\"1", Username: "ops" }) throw new Exception("Royal CSV");
            var rtsz = Path.Combine(dir, "doc.rtsz");
            using (var z = ZipFile.Open(rtsz, ZipArchiveMode.Create))
            using (var s = new StreamWriter(z.CreateEntry("doc.xml").Open()))
                s.Write("<RTSZ><RoyalDocument><ID>d1</ID><Name>Doc</Name></RoyalDocument><RoyalFolder><ID>f1</ID><ParentID>d1</ParentID><Name>Servers</Name></RoyalFolder><RoyalSSHConnection><ID>c1</ID><ParentID>f1</ParentID><Name>app-01</Name><URI>192.0.2.42</URI><Port>22</Port><CredentialUsername>deploy</CredentialUsername></RoyalSSHConnection></RTSZ>");
            if (new RoyalTsSource().Read(rtsz).Hosts.Single() is not { Folder: "Servers", Name: "app-01", Username: "deploy" }) throw new Exception("Royal document");

            var moba = new MobaXtermSource().Read(W(dir, "s.mxtsessions", "[Bookmarks]\nSubRep=\nImgNum=42\n[Bookmarks_1]\nSubRep=Lab\\Linux\nImgNum=41\nmx-01=#109#0%192.0.2.51%22%moba%%-1%-1\nmx-rdp=#91#4%192.0.2.52%3389%Administrator%\n"));
            if (moba.Hosts.Count != 2 || moba.Hosts.Any(h => h.Folder != "Lab/Linux") || moba.Hosts.Single(h => h.Name == "mx-rdp").Protocol != "rdp") throw new Exception("MobaXterm");

            var scp = new WinScpSource().Read(W(dir, "WinSCP.ini", "[Configuration]\nx=1\n[Sessions\\Prod/sftp-01]\nHostName=192.0.2.61\nPortNumber=2022\nUserName=sftp%20user\nPassword=A35C7A\n"));
            if (scp.Hosts.Single() is not { Folder: "Prod", Name: "sftp-01", Port: 2022, Username: "sftp user", Password: null } || scp.Warnings.Count != 1) throw new Exception("WinSCP");

            var rdg = new RdcManSource().Read(W(dir, "f.rdg", "<RDCMan programVersion=\"2.83\" schemaVersion=\"3\"><file><properties><name>f</name></properties><group><properties><name>DCs</name></properties><server><properties><name>192.0.2.71</name><displayName>dc-71</displayName></properties><logonCredentials inherit=\"None\"><userName>adm</userName><domain>CORP</domain><password>AQAAAN</password></logonCredentials><connectionSettings inherit=\"None\"><port>3390</port></connectionSettings></server></group></file></RDCMan>"));
            if (rdg.Hosts.Single() is not { Name: "dc-71", Folder: "DCs", Port: 3390, Username: @"CORP\adm", Protocol: "rdp" }) throw new Exception("RDCMan");

            var rdp = new RdpFileSource().Read(W(dir, "jump.rdp", "full address:s:192.0.2.81:3391\r\nusername:s:CORP\\jo\r\ngatewayhostname:s:gw.example.test\r\n"));
            if (rdp.Hosts.Single() is not { Name: "jump", Port: 3391, Username: @"CORP\jo" } || !rdp.Hosts[0].Notes.Contains("gw.example.test")) throw new Exception(".rdp");

            var rdm = new DevolutionsSource().Read(W(dir, "e.rdm", "<ArrayOfConnection><Connection><ConnectionType>SSHShell</ConnectionType><Name>rdm-ssh</Name><Group>Ops\\Linux</Group><Terminal><Host>192.0.2.91</Host><HostPort>22</HostPort><Username>ops</Username></Terminal></Connection><Connection><ConnectionType>RDPConfigured</ConnectionType><Name>rdm-rdp</Name><Url>192.0.2.92:3389</Url><UserName>adm</UserName><Domain>CORP</Domain></Connection></ArrayOfConnection>"));
            if (rdm.Hosts.Count != 2 || rdm.Hosts.Single(h => h.Name == "rdm-ssh").Folder != "Ops/Linux" || rdm.Hosts.Single(h => h.Name == "rdm-rdp").Username != @"CORP\adm") throw new Exception("Devolutions");

            if (ConnectionImport.CleanFolder("../../etc/\u0007x/./") != "etc/x") throw new Exception("folder cleaning");
            ok($"import formats: SecureCRT (XML export and session .ini), Royal TS (document and CSV with quoted fields), MobaXterm, WinSCP.ini, RDCMan .rdg, .rdp files and Devolutions RDM read with their folders, ports and DOMAIN\\user; passwords those apps encrypt are reported, never guessed; folder paths can't climb (“../”) or carry control characters; {ConnectionImport.All.Count} sources in all");
        }
        catch (Exception ex) { fail("import formats", ex); }
    }

    private static void Apply(string dir, Action<string> ok, Action<string, Exception?> fail)
    {
        var s = AppSettings.Current;
        try
        {
            var hosts = new List<ImportedHost>
            {
                new() { Name = "imp-ssh", Folder = "Lab", Protocol = "ssh", Address = "192.0.2.201", Port = 22, Username = "u1", Password = "imported-pw-1" },
                new() { Name = "imp-rdp", Folder = "", Protocol = "rdp", Address = "192.0.2.202", Port = 3389, Username = @"CORP\a" },
            };
            var o = ConnectionImportApply.Apply(hosts, "selftest", "Imported/selftest", passwords: true);
            var h1 = AppData.Current.Hosts.Single(h => h.Address == "192.0.2.201");
            if (o.Added != 2 || h1.Group != "Imported/selftest/Lab" || AppData.Current.FindCredential(h1.CredentialId)?.Secret != "imported-pw-1") throw new Exception("hosts or vault credential");
            if (AppData.Current.Hosts.Single(h => h.Address == "192.0.2.202").Platform != "windows") throw new Exception("RDP host platform");
            if (ConnectionImportApply.Apply(hosts, "selftest", "Imported/selftest", true).Skipped != 2) throw new Exception("duplicates not skipped");
            var noPw = ConnectionImportApply.Apply(new[] { new ImportedHost { Name = "imp-nopw", Protocol = "ssh", Address = "192.0.2.203", Port = 22, Username = "u3", Password = "x" } }, "selftest", "Imported/selftest", passwords: false);
            if (noPw.Passwords != 0 || AppData.Current.Hosts.Single(h => h.Address == "192.0.2.203").CredentialId != null) throw new Exception("a password came in with the option off");
            s.BackupAllowImport = false;
            try { ConnectionImportApply.Apply(hosts, "selftest", "x", true); throw new Exception("imported while imports are off"); } catch (InvalidOperationException) { }
            ok("import apply: ticked connections land under the chosen folder with their own folders below, a saved password goes into the encrypted vault (only when asked), RDP hosts are Windows hosts, a host already here is skipped, and nothing comes in while imports are switched off (policy)");
        }
        catch (Exception ex) { fail("import apply", ex); }
        finally
        {
            s.BackupAllowImport = true;
            var mine = AppData.Current.Hosts.Where(h => h.Group.StartsWith("Imported/selftest")).ToList();
            foreach (var h in mine) { AppData.Current.Hosts.Remove(h); if (h.CredentialId != null) AppData.Current.Credentials.RemoveAll(c => c.Id == h.CredentialId); }
            AppData.Save();
        }
    }

    private static void Identity(Action<string> ok, Action<string, Exception?> fail)
    {
        var s = AppSettings.Current;
        var saved = (s.SsoKerberosSsh, s.SsoKerberosDelegate);
        try
        {
            var j = SignIn.WindowsAccount.ParseJoin("+----+\n| Device State |\n+----+\n             AzureAdJoined : YES\n          EnterpriseJoined : NO\n              DomainJoined : YES\n                DomainName : CORP\n                TenantName : Contoso\n               AzureAdPrt : YES\n");
            if (!(j.AdJoined && j.EntraJoined && j.PrimaryRefreshToken && j.Tenant == "Contoso" && j.DomainName == "CORP")) throw new Exception("dsregcmd parsing");
            var t = SignIn.WindowsAccount.ParseKlist("Current LogonId is 0:0x1\n\nCached Tickets: (2)\n\n#0>     Client: jo @ CORP.EXAMPLE\n        Server: krbtgt/CORP.EXAMPLE @ CORP.EXAMPLE\n        End Time: 10/21/2026 18:02:11 (local)\n\n#1>     Client: jo @ CORP.EXAMPLE\n        Server: host/web01.corp.example @ CORP.EXAMPLE\n        End Time: 10/21/2026 18:02:11 (local)\n");
            if (t.Count != 2 || !t.HasTgt || t.Realm != "CORP.EXAMPLE" || t.TgtExpires == null) throw new Exception("klist parsing");

            var me = new SignIn.WindowsAccount.Account(@"CORP\jo", "jo@corp.example", "S-1-5-21-1-2-3-1104", true, "CORP");
            var winB = new SignIn.DirectoryBinding { Kind = "windows", Subject = me.Sid };
            var ldapB = new SignIn.DirectoryBinding { Kind = "ldaps", Server = "dc01.corp.example", User = "JO@corp.example" };
            if (!SignIn.WindowsAccount.Matches(winB, me) || !SignIn.WindowsAccount.Matches(ldapB, me) || SignIn.WindowsAccount.Matches(new SignIn.DirectoryBinding { Kind = "windows", Subject = "S-1-5-21-9-9-9-500" }, me)) throw new Exception("account matching");
            var ix = new ProfileIndex();
            ix.Profiles.Add(new ProfileInfo { Id = "default", Name = "Local", Kind = "local" });
            ix.Profiles.Add(new ProfileInfo { Id = "p-jo", Name = "Work", Kind = "directory", Directory = winB });
            if (Profiles.ForWindowsAccount(ix, me)?.Id != "p-jo") throw new Exception("the bound profile wasn't picked");
            ix.AutoOpenForWindowsAccount = false;
            if (Profiles.ForWindowsAccount(ix, me) != null) throw new Exception("picked although switched off");
            ix.AutoOpenForWindowsAccount = true;
            PolicySettings.SourceForTest = h => h == "HKLM" ? new Dictionary<string, (object, Microsoft.Win32.RegistryValueKind)> { ["AutoProfileForWindowsAccount"] = (0, Microsoft.Win32.RegistryValueKind.DWord) } : null;
            try { if (Profiles.ForWindowsAccount(ix, me) != null) throw new Exception("the computer policy didn't win"); }
            finally { PolicySettings.SourceForTest = _ => null; PolicySettings.Reload(); }
            ix.Profiles.Add(new ProfileInfo { Id = "p-jo2", Name = "Work 2", Kind = "directory", Directory = ldapB });
            if (Profiles.ForWindowsAccount(ix, me) != null) throw new Exception("two matches must show the picker");

            // Kerberos SSH: only checked values reach OpenSSH's command line; the ticket is forwarded only when allowed.
            var h = new HostEntry { Name = "k", Address = "web01.corp.example", Port = 22, Username = "jo", KerberosSignOn = true };
            s.SsoKerberosSsh = true; s.SsoKerberosDelegate = false;
            if (Ssh.KerberosSsh.SshExe.Length > 0)
            {
                var p = Ssh.KerberosSsh.Profile(h, out var why) ?? throw new Exception("no profile: " + why);
                if (!p.CommandLine.Contains("GSSAPIAuthentication=yes") || !p.CommandLine.Contains("GSSAPIDelegateCredentials=no") || !p.CommandLine.EndsWith("jo@web01.corp.example")) throw new Exception(p.CommandLine);
                foreach (var bad in new[] { "jo -oProxyCommand=calc", "jo;calc", "\"jo\"" }) { h.Username = bad; if (Ssh.KerberosSsh.Profile(h, out _) != null) throw new Exception("accepted user " + bad); }
                h.Username = "jo"; h.Address = "web01 -oProxyCommand=calc";
                if (Ssh.KerberosSsh.Profile(h, out _) != null) throw new Exception("accepted a hostile address");
                h.Address = "web01.corp.example"; s.SsoKerberosSsh = false;
                if (Ssh.KerberosSsh.Profile(h, out _) != null) throw new Exception("ran while switched off");
            }
            if (new RdpSettings().SignOn != "password" || RdpSettings.SignOnChoices.Length != 4) throw new Exception("RDP sign-on choices");
            ok("identity (#8): the PC's join state (AD / Entra / hybrid, PRT) and Kerberos tickets are read from dsregcmd / klist (no ticket content kept); a profile bound to the signed-in account (SID, UPN or DOMAIN\\name) opens without the picker unless switched off or the computer policy says no, and two matches show the picker; Kerberos SSH puts only checked host / user values on OpenSSH's command line, forwards the ticket only when allowed, and is refused when switched off; RDP sign-on offers password, single sign-on, Remote Credential Guard and Restricted Admin");
        }
        catch (Exception ex) { fail("identity (#8)", ex); }
        finally { (s.SsoKerberosSsh, s.SsoKerberosDelegate) = saved; }
    }

    private static void Backups(string dir, Action<string> ok, Action<string, Exception?> fail)
    {
        var s = AppSettings.Current;
        var saved = (s.BackupAllowExport, s.BackupAllowSecrets, s.BackupMinPasswordLength, s.BackupFolder, s.BackupKeep, s.BackupScheduleDays, s.BackupLastUtc, s.OpenAiKey);
        var cred = new Credential { Name = "backup-test", Kind = CredentialKind.Password, Username = "u", Secret = "do-not-export-1" };
        AppData.Current.Credentials.Add(cred);
        try
        {
            var path = Path.Combine(dir, "t.dcxbackup");
            var file = Backup.Custom(Backup.Choice.Everything()).File;
            try { Backup.Write(path, "short", file); throw new Exception("a short password was accepted"); } catch (ArgumentException) { }
            s.BackupMinPasswordLength = 20;
            try { Backup.Write(path, "sixteen-chars-pw", file); throw new Exception("the policy minimum wasn't applied"); } catch (ArgumentException) { }
            s.BackupMinPasswordLength = 12;
            s.BackupAllowExport = false;
            try { Backup.Write(path, "long-enough-password", file); throw new Exception("an export was written while exports are off"); } catch (InvalidOperationException) { }
            s.BackupAllowExport = true;

            s.OpenAiKey = "sk-selftest-not-exported";
            s.BackupAllowSecrets = false;
            Backup.Write(path, "long-enough-password", Backup.Custom(Backup.Choice.Everything()).File);
            var back = Backup.Read(path, "long-enough-password");
            if (back.Data?.Credentials.Count != 0 || back.Data.Hosts.Any(h => h.CredentialId != null) || back.Settings?.OpenAiKey.Length != 0) throw new Exception("secrets left in a no-secrets export");
            if (File.ReadAllText(path).Contains("do-not-export")) throw new Exception("plaintext in the file");
            s.BackupAllowSecrets = true;

            // Scheduled backups: folder + password, verified, newest N kept, due only after N days.
            var bdir = Path.Combine(dir, "scheduled");
            s.BackupFolder = bdir; s.BackupKeep = 2; s.BackupScheduleDays = 7; s.BackupLastUtc = null;
            ScheduledBackup.SetPassword("scheduled-backup-pw-1");
            for (int i = 0; i < 3; i++) { var r = ScheduledBackup.Run("selftest"); if (!r.Ok) throw new Exception(r.Message); Thread.Sleep(1100); }
            var list = ScheduledBackup.List(bdir);
            if (list.Count != 2) throw new Exception($"{list.Count} backups kept, expected 2");
            if (Backup.Read(list[0].FullName, "scheduled-backup-pw-1").Data?.Credentials.Any(c => c.Name == "backup-test") != true) throw new Exception("scheduled backup content");
            if (ScheduledBackup.RunIfDue() != null) throw new Exception("ran although one is fresh");
            if (ScheduledBackup.RunIfDue(() => DateTime.UtcNow.AddDays(8)) is not { Ok: true }) throw new Exception("didn't run when due");
            ok("backups: exports refuse passwords shorter than the policy minimum (12 by default), nothing is written while exports are switched off, a no-secrets policy strips credentials, keys and the app's own tokens from the file (none in its plaintext either); scheduled backups are encrypted with the stored backup password, read back, the newest N kept, and run only when due");
        }
        catch (Exception ex) { fail("backups (#7)", ex); }
        finally
        {
            (s.BackupAllowExport, s.BackupAllowSecrets, s.BackupMinPasswordLength, s.BackupFolder, s.BackupKeep, s.BackupScheduleDays, s.BackupLastUtc, s.OpenAiKey) = saved;
            s.BackupLastError = "";
            ScheduledBackup.ForgetPassword();
            AppData.Current.Credentials.Remove(cred);
            AppData.Save(); AppSettings.Save();
        }
    }
}
