using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DcxManager.Services;
using DcxManager.Ssh;
using DcxManager.Views;
using DcxManager.Vultr;
using DcxManager.Documents;

namespace DcxManager;

// SelfTest: the encrypted store, the master password, keys and the parsers (moved unchanged out of RunFullAsync in 0.2.0 phase 6½; the run's shared state is SelfTestRun).
public static partial class SelfTest
{
    private static async Task StoreAndParsersAsync(SelfTestRun sharedRun)
    {
        var main = sharedRun.Main; var reportPath = sharedRun.ReportPath; Action<string> Ok = sharedRun.Ok; Action<string, Exception?> Fail = sharedRun.Fail; SelfTestRun.SettleFn Settle = sharedRun.Settle;

        // 1. encryption round trip
        try
        {
            var probe = new Credential { Name = "selftest", Kind = CredentialKind.Password, Secret = "s3cret-value-xyz" };
            AppData.Current.Credentials.Add(probe);
            var host = new HostEntry { Name = "selftest-host", Address = "127.0.0.1", Port = 1, Username = "tester", Auth = AuthMethod.Password, CredentialId = probe.Id, KeepAliveSeconds = 0 };
            AppData.Current.Hosts.Add(host);
            AppData.Save();
            var raw = File.ReadAllBytes(Path.Combine(SecureStore.DataDir, "data.dcx"));
            if (Encoding.UTF8.GetString(raw).Contains("s3cret-value-xyz") || Encoding.UTF8.GetString(raw).Contains("selftest-host")) throw new Exception("plaintext found in data.dcx");
            AppData.Load();
            if (AppData.Current.FindCredential(probe.Id)?.Secret != "s3cret-value-xyz") throw new Exception("round trip mismatch");
            raw[^1] ^= 0xFF;
            try { SecureStore.Decrypt(raw, "data.dcx"); throw new Exception("tampered file was accepted"); }
            catch (System.Security.Cryptography.CryptographicException) { }
            try { SecureStore.Decrypt(File.ReadAllBytes(Path.Combine(SecureStore.DataDir, "data.dcx")), "settings.dcx"); throw new Exception("file swap was accepted"); }
            catch (System.Security.Cryptography.CryptographicException) { }
            AuditLog.Write("selftest", "store", "audit entry");
            if (AuditLog.Read(DateTime.Now.ToString("yyyy-MM")).FirstOrDefault()?.Actor != "selftest") throw new Exception("audit round trip");
            var auditRaw = File.ReadAllText(Path.Combine(SecureStore.DataDir, $"audit-{DateTime.Now:yyyy-MM}.dcxlog"));
            if (auditRaw.Contains("selftest")) throw new Exception("plaintext in audit log");
            Ok("encrypted store: AES-GCM round trip, no plaintext on disk, tamper + swap rejected, audit log encrypted");
        }
        catch (Exception ex) { Fail("encrypted store", ex); }

        // 2. master password wrap / unwrap
        try
        {
            SecureStore.SetPassword("correct horse battery", requireOnStart: false);
            if (!SecureStore.VerifyPassword("correct horse battery") || SecureStore.VerifyPassword("wrong")) throw new Exception("verify");
            SecureStore.RemovePassword();
            Ok("master password set / verify / remove");
        }
        catch (Exception ex) { Fail("master password", ex); }

        // 3. key generation + parsing
        try
        {
            var rsa = await KeyTools.GenerateAsync("selftest-rsa", "rsa", "tester");
            if (!rsa.PublicKey.StartsWith("ssh-rsa ") || !rsa.Fingerprint.StartsWith("SHA256:")) throw new Exception("rsa describe: " + rsa.PublicKey[..20]);
            Ok($"RSA key generate + describe ({rsa.Fingerprint})");
            if (KeyTools.SshKeygenPath() != null)
            {
                var ed = await KeyTools.GenerateAsync("selftest-ed", "ed25519", "tester");
                if (!ed.PublicKey.StartsWith("ssh-ed25519 ")) throw new Exception("ed25519 describe: " + ed.PublicKey);
                Ok($"Ed25519 key generate + describe ({ed.Fingerprint})");
            }
        }
        catch (Exception ex) { Fail("key tools", ex); }

        // 4. parsers
        try
        {
            var f = LinuxProbe.Parse("##HOST\nweb1\n##OS\nUbuntu 24.04 LTS\n##UPTIME\n3600.5\n##CPUS\n4\n##STAT1\ncpu 100 0 100 800 0 0 0 0\n##STAT2\ncpu 150 0 150 900 0 0 0 0\n##MEM\nMemTotal: 1000 kB\nMemAvailable: 250 kB\n##DISK\n/dev/vda1 1000 600 400 60% /\n##END");
            if (f.Hostname != "web1" || f.Cpus != 4 || Math.Abs(f.MemPercent - 75) > 0.1 || f.Disks.Count != 1 || Math.Abs(f.CpuPercent - 50) > 0.1) throw new Exception(f.Summary());
            var tpl = Tpl.Fill("/domains/{parent.domain}/records/{id}?x={now}", System.Text.Json.JsonDocument.Parse("{\"id\":\"r 1\"}").RootElement, System.Text.Json.JsonDocument.Parse("{\"domain\":\"example.com\"}").RootElement, url: true);
            if (!tpl.StartsWith("/domains/example.com/records/r%201")) throw new Exception(tpl);
            if (MainWindow.ParseQuick("alex@10.0.0.5:2222") is not { Username: "alex", Address: "10.0.0.5", Port: 2222 }) throw new Exception("quick connect parse");
            if (RemoteExec.Quote("it's") != "'it'\\''s'") throw new Exception("shell quoting");
            Ok("probe parser, URL templates, quick-connect parser, shell quoting");
        }
        catch (Exception ex) { Fail("parsers", ex); }
    }
}
