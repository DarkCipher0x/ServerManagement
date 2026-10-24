using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using DcxManager.Services;
using Renci.SshNet;
using Renci.SshNet.Security;

namespace DcxManager.Ssh;

/// <summary>Reads, generates and installs SSH keys.</summary>
public static class KeyTools
{
    /// <summary>Fills PublicKey / KeyType / Fingerprint from the private key. Throws if the key or passphrase is wrong.</summary>
    public static void Describe(Credential cred)
    {
        var pk = SshConnector.LoadKey(cred);
        var alg = pk.HostKeyAlgorithms.OfType<KeyHostAlgorithm>().FirstOrDefault()
                  ?? throw new InvalidOperationException("Unsupported key.");
        // The SSH wire blob of the public key: string name + key-specific fields.
        var blob = alg.Data;
        var name = BlobName(blob) ?? KeyName(pk.Key);
        cred.KeyType = name;
        cred.PublicKey = $"{name} {Convert.ToBase64String(blob)} {SafeComment(cred.Name)}".Trim();
        cred.Fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=');
    }

    /// <summary>The SSH public key blob starts with uint32 length + key type name.</summary>
    private static string? BlobName(byte[] blob)
    {
        if (blob.Length < 8) return null;
        int len = (blob[0] << 24) | (blob[1] << 16) | (blob[2] << 8) | blob[3];
        return len is > 0 and < 64 && len + 4 <= blob.Length ? Encoding.ASCII.GetString(blob, 4, len) : null;
    }

    private static string KeyName(Key key) => (key.ToString() ?? "") switch
    {
        var s when s.Contains("ed25519", StringComparison.OrdinalIgnoreCase) => "ssh-ed25519",
        var s when s.Contains("ecdsa-sha2-nistp256") => "ecdsa-sha2-nistp256",
        var s when s.Contains("ecdsa-sha2-nistp384") => "ecdsa-sha2-nistp384",
        var s when s.Contains("ecdsa-sha2-nistp521") => "ecdsa-sha2-nistp521",
        var s when s.Contains("rsa", StringComparison.OrdinalIgnoreCase) => "ssh-rsa",
        var s => s,
    };

    private static string SafeComment(string s) => new string(s.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '@' or '.').ToArray());

    public static string? SshKeygenPath()
    {
        var sys = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", "ssh-keygen.exe");
        if (File.Exists(sys)) return sys;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            try { var p = Path.Combine(dir.Trim(), "ssh-keygen.exe"); if (File.Exists(p)) return p; } catch { }
        }
        return null;
    }

    /// <summary>The key types the generator offers (id → label), Ed25519 first (the recommended default).</summary>
    public static readonly (string Id, string Label)[] Types =
    {
        ("ed25519", "Ed25519 (recommended)"), ("rsa4096", "RSA 4096"), ("rsa3072", "RSA 3072"),
        ("ecdsa256", "ECDSA P-256"), ("ecdsa384", "ECDSA P-384"), ("ecdsa521", "ECDSA P-521"),
    };

    /// <summary>
    /// Generates a new key pair straight into a credential (the vault is encrypted; nothing is written to disk). Every type
    /// is generated in-process since 0.3.0: Ed25519 with BouncyCastle (already vetted and shipped with SSH.NET) in the
    /// OpenSSH format, RSA and ECDSA with .NET's own cryptography as PEM. A passphrase is added when a key is exported to a
    /// file (<see cref="KeyExport"/>), where it protects something.
    /// </summary>
    public static Task<Credential> GenerateAsync(string name, string type, string username) => Task.Run(() =>
    {
        var cred = new Credential { Name = name, Kind = CredentialKind.PrivateKey, Username = username };
        switch (type)
        {
            case "ed25519":
                var gen = new Org.BouncyCastle.Crypto.Generators.Ed25519KeyPairGenerator();
                gen.Init(new Org.BouncyCastle.Crypto.Parameters.Ed25519KeyGenerationParameters(new Org.BouncyCastle.Security.SecureRandom()));
                var blob = Org.BouncyCastle.Crypto.Utilities.OpenSshPrivateKeyUtilities.EncodePrivateKey(gen.GenerateKeyPair().Private);
                cred.Secret = Pem("OPENSSH PRIVATE KEY", blob);
                CryptographicOperations.ZeroMemory(blob);
                break;
            case "rsa" or "rsa4096" or "rsa3072":
                using (var rsa = RSA.Create(type == "rsa3072" ? 3072 : 4096)) cred.Secret = rsa.ExportRSAPrivateKeyPem();
                break;
            case "ecdsa256" or "ecdsa384" or "ecdsa521":
                var curve = type switch { "ecdsa256" => ECCurve.NamedCurves.nistP256, "ecdsa384" => ECCurve.NamedCurves.nistP384, _ => ECCurve.NamedCurves.nistP521 };
                using (var ec = ECDsa.Create(curve)) cred.Secret = ec.ExportECPrivateKeyPem();
                break;
            default: throw new ArgumentException("Unknown key type: " + type);
        }
        Describe(cred);
        return cred;
    });

    private static string Pem(string label, byte[] der)
    {
        var b64 = Convert.ToBase64String(der);
        var sb = new StringBuilder($"-----BEGIN {label}-----\n");
        for (int i = 0; i < b64.Length; i += 70) sb.Append(b64, i, Math.Min(70, b64.Length - i)).Append('\n');
        return sb.Append($"-----END {label}-----\n").ToString();
    }

    internal static Task<string> ReadOnThread(StreamReader r)
    {
        var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        new Thread(() =>
        {
            try { done.TrySetResult(r.ReadToEnd()); }
            catch (Exception ex) { done.TrySetException(ex); }
        }) { IsBackground = true, Name = "ssh-keygen output" }.Start();
        return done.Task;
    }

    /// <summary>Appends the public key to ~/.ssh/authorized_keys on the host (like ssh-copy-id), without duplicating it.</summary>
    public static async Task<ExecResult> InstallPublicKeyAsync(HostEntry host, Credential key, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key.PublicKey)) Describe(key);
        var pub = key.PublicKey.Trim();
        var script =
            "umask 077; mkdir -p ~/.ssh && touch ~/.ssh/authorized_keys && chmod 700 ~/.ssh && chmod 600 ~/.ssh/authorized_keys && " +
            $"(grep -qxF {RemoteExec.Quote(pub)} ~/.ssh/authorized_keys || echo {RemoteExec.Quote(pub)} >> ~/.ssh/authorized_keys) && echo installed";
        return await SshPool.RunAsync(host, script, false, ct);
    }

    public static void ExportPrivateKey(Credential cred, string path)
    {
        // 0.6.17 (ADR-096): a key kept in Bitwarden is exported from there; here it is only ever read into memory.
        if (cred.IsExternal) throw new InvalidOperationException($"“{cred.Name}” is kept in {cred.ExternalText}: export the key from there.");
        File.WriteAllText(path, cred.Secret.Replace("\r\n", "\n"), new UTF8Encoding(false));
        // Lock the file down to the current user (OpenSSH refuses keys others can read).
        try
        {
            var fi = new FileInfo(path);
            var sec = new System.Security.AccessControl.FileSecurity();
            sec.SetAccessRuleProtection(true, false);
            var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
            sec.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(me, System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
            fi.SetAccessControl(sec);
        }
        catch { }
    }
}
