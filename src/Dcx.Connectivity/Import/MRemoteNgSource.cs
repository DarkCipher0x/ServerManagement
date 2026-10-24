using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace DcxManager.Import;

/// <summary>Thrown when a file is protected with a master password other than the app's default.</summary>
public sealed class MasterPasswordNeededException(string message) : Exception(message);

/// <summary>
/// mRemoteNG's confCons.xml (%APPDATA%\mRemoteNG). Folders are Container nodes; passwords are sealed with AES-GCM (128-bit
/// nonce, the salt as associated data, PBKDF2-HMAC-SHA1 key) under the master password — "mR3m" unless the user set one —
/// or, in files from before 1.76, AES-CBC with an MD5 key. Whole-file encryption is supported the same way. Inherited user
/// names, domains and passwords are taken from the folder they inherit from.
/// </summary>
public sealed class MRemoteNgSource : IConnectionSource
{
    public string Id => "mremoteng";
    public string Title => "mRemoteNG";
    public string Help => "File → Save connections as… or the default %APPDATA%\\mRemoteNG\\confCons.xml. If you set a master password in mRemoteNG you'll be asked for it.";
    public string Filter => "mRemoteNG connections (*.xml)|*.xml|All files|*.*";

    private const string DefaultPassword = "mR3m";

    public ImportResult Read(string? path, string? masterPassword = null)
    {
        var r = new ImportResult { Source = Title };
        var doc = ConnectionImport.LoadXml(ConnectionImport.ReadText(path ?? throw new ArgumentNullException(nameof(path))));
        var root = doc.DocumentElement ?? throw new InvalidDataException("empty file");
        if (!root.LocalName.Equals("Connections", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("This isn't an mRemoteNG connections file.");
        var crypto = new Crypto(root);
        var pw = masterPassword ?? DefaultPassword;
        // "Protected" holds a known word sealed with the master password: it tells us whether ours is right.
        if (root.GetAttribute("Protected") is { Length: > 0 } prot)
        {
            string? word = null;
            try { word = crypto.Decrypt(prot, pw); } catch (CryptographicException) { }
            if (word == null) throw new MasterPasswordNeededException(masterPassword == null ? "This file is protected with an mRemoteNG master password." : "That master password is not right for this file.");
        }
        if (root.GetAttribute("FullFileEncryption").Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            string inner;
            try { inner = crypto.Decrypt(root.InnerText.Trim(), pw); }
            catch (CryptographicException) { throw new MasterPasswordNeededException("The connections are encrypted with an mRemoteNG master password."); }
            var innerDoc = ConnectionImport.LoadXml("<Connections>" + inner + "</Connections>");
            root = innerDoc.DocumentElement!;
        }
        Walk(root, "", null, r, crypto, pw);
        return r;
    }

    private sealed record Inherit(string User, string Domain, string? Password);

    private static void Walk(XmlElement parent, string folder, Inherit? from, ImportResult r, Crypto crypto, string pw)
    {
        foreach (var n in parent.ChildNodes.OfType<XmlElement>().Where(e => e.LocalName == "Node"))
        {
            string A(string name) => n.GetAttribute(name);
            bool Inh(string name) => A("Inherit" + name).Equals("true", StringComparison.OrdinalIgnoreCase);
            string? password = null;
            if (A("Password") is { Length: > 0 } sealedPw)
            {
                try { password = crypto.Decrypt(sealedPw, pw); }
                catch (CryptographicException) { r.Warnings.Add($"“{ConnectionImport.Clean(A("Name"), 60)}”: its password couldn't be decrypted and was left out."); }
            }
            var user = Inh("Username") && from != null ? from.User : A("Username");
            var domain = Inh("Domain") && from != null ? from.Domain : A("Domain");
            if (Inh("Password") && from != null) password = from.Password;
            if (A("Type").Equals("Container", StringComparison.OrdinalIgnoreCase))
            {
                var name = ConnectionImport.Clean(A("Name"), 64);
                Walk(n, folder.Length == 0 ? name : folder + "/" + name, new Inherit(user, domain, password), r, crypto, pw);
                continue;
            }
            var proto = A("Protocol").ToUpperInvariant() switch { "SSH1" or "SSH2" => "ssh", "RDP" => "rdp", "TELNET" => "telnet", var p => p.ToLowerInvariant() };
            ConnectionImport.Add(r, new ImportedHost
            {
                Name = A("Name"), Folder = folder, Protocol = proto, Address = A("Hostname"), Port = ConnectionImport.Int(A("Port")),
                Username = proto == "rdp" && domain.Length > 0 && user.Length > 0 && !user.Contains('\\') && !user.Contains('@') ? domain + "\\" + user : user,
                Password = string.IsNullOrEmpty(password) ? null : password, Notes = A("Descr"),
            });
        }
    }

    /// <summary>mRemoteNG's two schemes. GCM: base64(salt 16 ‖ nonce 16 ‖ ciphertext ‖ tag 16), AAD = salt,
    /// key = PBKDF2-SHA1(password, salt, KdfIterations, 32). Legacy CBC: base64(IV 16 ‖ ciphertext), key = MD5(password).</summary>
    public sealed class Crypto
    {
        private readonly bool _gcm;
        private readonly int _iterations;

        public Crypto(XmlElement root)
        {
            var engine = root.GetAttribute("EncryptionEngine");
            var mode = root.GetAttribute("BlockCipherMode");
            _gcm = engine.Length > 0 && mode.Equals("GCM", StringComparison.OrdinalIgnoreCase) || engine.Length > 0 && mode.Length == 0;
            if (engine.Length > 0 && !engine.Equals("AES", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"mRemoteNG's {engine} encryption isn't supported: switch it to AES in mRemoteNG (Options → Security) and save again.");
            if (mode.Length > 0 && !mode.Equals("GCM", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"mRemoteNG's {mode} mode isn't supported: switch it to GCM in mRemoteNG (Options → Security) and save again.");
            _iterations = Math.Clamp(ConnectionImport.Int(root.GetAttribute("KdfIterations"), 1000), 1000, 5_000_000);
        }

        public Crypto(bool gcm, int iterations) { _gcm = gcm; _iterations = iterations; }

        public string Decrypt(string b64, string password)
        {
            byte[] data;
            try { data = Convert.FromBase64String(b64); } catch (FormatException) { throw new CryptographicException("not base64"); }
            var pwBytes = password.Select(c => (byte)c).ToArray();   // BouncyCastle's PKCS#5 password bytes
            if (_gcm)
            {
                if (data.Length < 16 + 16 + 16) throw new CryptographicException("too short");
                var salt = data[..16]; var nonce = data[16..32]; var body = data[32..^16]; var tag = data[^16..];
                var key = Rfc2898DeriveBytes.Pbkdf2(pwBytes, salt, _iterations, HashAlgorithmName.SHA1, 32);
                try { return Encoding.UTF8.GetString(GcmAnyIv.Decrypt(key, nonce, body, tag, salt)); }
                finally { CryptographicOperations.ZeroMemory(key); }
            }
            if (data.Length < 32 || data.Length % 16 != 0) throw new CryptographicException("not a CBC block sequence");
            using var aes = Aes.Create();
            // Reading mRemoteNG's pre-1.76 format needs its MD5-derived key; nothing this app stores is protected with it.
#pragma warning disable CA5351
            aes.Key = MD5.HashData(Encoding.UTF8.GetBytes(password));
#pragma warning restore CA5351
            return Encoding.UTF8.GetString(aes.DecryptCbc(data[16..], data[..16], PaddingMode.PKCS7));
        }

        /// <summary>For the self-test: seals text exactly as mRemoteNG's GCM provider does.</summary>
        public static string EncryptGcmForTest(string text, string password, int iterations = 1000)
        {
            var salt = RandomNumberGenerator.GetBytes(16); var nonce = RandomNumberGenerator.GetBytes(16);
            var key = Rfc2898DeriveBytes.Pbkdf2(password.Select(c => (byte)c).ToArray(), salt, iterations, HashAlgorithmName.SHA1, 32);
            var (c, t) = GcmAnyIv.Encrypt(key, nonce, Encoding.UTF8.GetBytes(text), salt);
            return Convert.ToBase64String(salt.Concat(nonce).Concat(c).Concat(t).ToArray());
        }
    }
}
