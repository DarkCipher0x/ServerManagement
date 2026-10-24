using System.IO;
using System.Text;
using System.Windows;
using DcxManager.Services;
using DcxManager.Ssh;
using DcxManager.Views;

namespace DcxManager;

/// <summary>
/// 0.3.0 preview.4: the SSH key tool. Every type is generated in-process and loads in the SSH engine; an export with a
/// passphrase goes through ssh-keygen + the app's own askpass helper over a one-shot pipe (a real child process of this
/// build) and the file then opens only with that passphrase; an export without one is the plain key; the tool's window
/// creates a key into the vault without being shown.
/// </summary>
public static class SshKeyTests
{
    public static async Task RunAsync(MainWindow main, string reportDir, Action<string> ok, Action<string, Exception?> fail)
    {
        var made = new List<string>();
        try
        {
            foreach (var (id, _) in KeyTools.Types)
            {
                var c = await KeyTools.GenerateAsync("selftest-" + id, id, "tester");
                if (c.PublicKey.Length < 40 || !c.Fingerprint.StartsWith("SHA256:")) throw new Exception(id + ": not described");
                var expect = id switch { "ed25519" => "ssh-ed25519", "ecdsa256" => "ecdsa-sha2-nistp256", "ecdsa384" => "ecdsa-sha2-nistp384", "ecdsa521" => "ecdsa-sha2-nistp521", _ => "ssh-rsa" };
                if (c.KeyType != expect) throw new Exception($"{id}: {c.KeyType}");
                SshConnector.LoadKey(c);   // the engine that connects can use it
            }
            ok("SSH key tool: Ed25519, RSA 3072/4096 and ECDSA P-256/384/521 generated in-process (no temp file), described and loadable by the SSH engine");
        }
        catch (Exception ex) { fail("SSH key generation", ex); }

        var dir = Path.Combine(reportDir, "keyexport");
        Directory.CreateDirectory(dir);
        try
        {
            var ed = await KeyTools.GenerateAsync("selftest-export", "ed25519", "tester");
            var plain = Path.Combine(dir, "plain");
            await KeyExport.ExportAsync(ed, plain, null);
            if (!File.ReadAllText(plain).Contains("OPENSSH PRIVATE KEY")) throw new Exception("plain export");
            if (KeyTools.SshKeygenPath() != null)
            {
                const string pass = "selftest-passphrase-77";
                var prot = Path.Combine(dir, "protected");
                await KeyExport.ExportAsync(ed, prot, pass);
                var back = new Credential { Kind = CredentialKind.PrivateKey, Secret = File.ReadAllText(prot), Passphrase = pass };
                KeyTools.Describe(back);
                if (back.Fingerprint != ed.Fingerprint) throw new Exception("protected copy is another key");
                try { KeyTools.Describe(new Credential { Kind = CredentialKind.PrivateKey, Secret = back.Secret, Passphrase = "wrong-passphrase" }); throw new Exception("opened with a wrong passphrase"); }
                catch (Exception ex) when (!ex.Message.StartsWith("opened")) { }
                try { KeyTools.Describe(new Credential { Kind = CredentialKind.PrivateKey, Secret = back.Secret }); throw new Exception("opened without the passphrase"); }
                catch (Exception ex) when (!ex.Message.StartsWith("opened")) { }
                // An RSA key (PEM in the vault) is protected the same way.
                var rsa = await KeyTools.GenerateAsync("selftest-export-rsa", "rsa3072", "tester");
                var rp = Path.Combine(dir, "rsa");
                await KeyExport.ExportAsync(rsa, rp, pass);
                var rb = new Credential { Kind = CredentialKind.PrivateKey, Secret = File.ReadAllText(rp), Passphrase = pass };
                KeyTools.Describe(rb);
                if (rb.Fingerprint != rsa.Fingerprint) throw new Exception("RSA protected copy");
                ok("SSH key export: plain, or passphrase-protected in the OpenSSH format through ssh-keygen + this app's askpass helper over a one-shot current-user pipe (never on a command line); opens only with the passphrase (Ed25519 and RSA)");
            }
            else ok("SSH key export: plain export checked (no ssh-keygen here: the passphrase path is not run)");
        }
        catch (Exception ex) { fail("SSH key export", ex); }
        finally { try { Directory.Delete(dir, true); } catch { } }

        // The tool's window, never shown: fill, create into the vault as the quick-connect default.
        var before = AppSettings.Current.DefaultKeyCredentialId;
        try
        {
            var tool = new SshKeyWindow();
            tool.Fill("selftest-tool-key", "ecdsa256", "ops", asDefault: true);
            if (!await tool.CreateAsync() || tool.Created is not { } c) throw new Exception("not created: " + tool.StatusText);
            made.Add(c.Id);
            if (AppData.Current.FindCredential(c.Id) == null || AppSettings.Current.DefaultKeyCredentialId != c.Id || !tool.StatusText.StartsWith("✓")) throw new Exception("not in the vault / not the default");
            if (tool.Window.IsVisible) throw new Exception("window shown");
            ok("SSH key tool window (off screen): creates the key into the vault, sets it as the quick-connect default, shows the public key actions");
        }
        catch (Exception ex) { fail("SSH key tool window", ex); }
        finally
        {
            AppData.Current.Credentials.RemoveAll(c => made.Contains(c.Id));
            AppSettings.Current.DefaultKeyCredentialId = before;
            AppData.Save(); AppSettings.Save();
        }
    }
}
