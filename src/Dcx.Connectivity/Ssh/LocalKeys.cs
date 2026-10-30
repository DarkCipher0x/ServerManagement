using System.IO;
using System.Security.Cryptography;
using System.Text;
using DcxManager.Services;

namespace DcxManager.Ssh;

/// <summary>
/// SSH keys already on this PC (0.6.30, ADR-107; DarkCipher0x: "pick up the hosts desktops default ssh key and add it
/// automatically to the credential store"). Looks in the Windows account's .ssh folder: OpenSSH's default identities first,
/// in the order ssh tries them, then any other private key file there. A key chosen is copied into the encrypted store; the
/// files are only ever read, never changed, moved or removed. A key with a passphrase keeps it (asked when it's imported).
/// A FIDO security key (<c>…_sk</c>) is signed by the hardware, which the SSH library can't drive: it is listed, not imported.
/// </summary>
public static class LocalKeys
{
    /// <summary>OpenSSH's default identities, in the order ssh tries them (DSA is gone from OpenSSH and isn't looked for).</summary>
    public static readonly string[] DefaultNames = { "id_ed25519", "id_ecdsa", "id_rsa" };
    private static readonly string[] SecurityKeyNames = { "id_ed25519_sk", "id_ecdsa_sk" };
    /// <summary>Files in .ssh that are never private keys.</summary>
    private static readonly string[] NotKeys = { "config", "known_hosts", "known_hosts.old", "authorized_keys", "authorized_keys2", "dcx_config", "environment", "rc" };
    private const int MaxKeyBytes = 64 * 1024;

    public static string DefaultDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");

    /// <summary>Self-test: the folder to look in instead.</summary>
    internal static string? DirForTest { get; set; }

    /// <summary>Where keys are looked for. The self-test never reads the real folder: a scratch one, or one that doesn't exist.</summary>
    public static string Dir => DirForTest ?? (TestMode.Running ? Path.Combine(SecureStore.DataDir, "no-ssh-folder-in-self-test") : DefaultDir);

    public enum State { Ready, NeedsPassphrase, AlreadyStored, Unsupported }

    /// <summary>A key file found. Not a record: its text is a private key and must never end up in a ToString().</summary>
    public sealed class Found
    {
        public required string Path { get; init; }
        public required string FileName { get; init; }
        /// <summary>One of OpenSSH's default identities (ticked by default; the first can become quick connect's key).</summary>
        public bool IsDefault { get; init; }
        public State State { get; internal set; }
        public string KeyType { get; init; } = "";
        public string Fingerprint { get; init; } = "";
        /// <summary>The comment of the matching .pub file (often user@pc), shown to tell keys apart.</summary>
        public string Comment { get; init; } = "";
        /// <summary>Why it can't be imported (Unsupported), else "".</summary>
        public string Why { get; init; } = "";
        internal string Text { get; init; } = "";
        /// <summary>What it is known by in the "already offered" list: its fingerprint, else a hash of the file.</summary>
        public string Id => Fingerprint.Length > 0 ? Fingerprint : "file:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Text.Trim())))[..32];
        public string Label => $"{FileName}{(KeyType.Length > 0 ? "  ·  " + KeyType : "")}{(Comment.Length > 0 ? "  ·  " + Comment : "")}";
    }

    /// <summary>The keys in <paramref name="dir"/> (default <see cref="Dir"/>): defaults first, then the rest by name. Each
    /// says whether it is ready, needs its passphrase, is already in <paramref name="stored"/>, or can't be used.</summary>
    public static List<Found> Scan(IEnumerable<Credential> stored, string? dir = null)
    {
        dir ??= Dir;
        var list = new List<Found>();
        if (!Directory.Exists(dir)) return list;
        var keys = stored.Where(c => c.Kind == CredentialKind.PrivateKey && !c.IsExternal).ToList();
        var files = new List<string>();
        foreach (var n in DefaultNames.Concat(SecurityKeyNames)) { var p = System.IO.Path.Combine(dir, n); if (File.Exists(p)) files.Add(p); }
        try
        {
            files.AddRange(Directory.EnumerateFiles(dir).Where(p => !files.Contains(p, StringComparer.OrdinalIgnoreCase) && MaybeKeyFile(p))
                .OrderBy(p => System.IO.Path.GetFileName(p), StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        foreach (var path in files)
            if (Read(path) is { } f)
            {
                if (f.State != State.Unsupported && keys.Any(c => f.Fingerprint.Length > 0 && c.Fingerprint == f.Fingerprint || c.Secret.Trim() == f.Text.Trim()))
                    f.State = State.AlreadyStored;
                list.Add(f);
            }
        return list;
    }

    private static bool MaybeKeyFile(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        if (name.StartsWith('.') || NotKeys.Contains(name, StringComparer.OrdinalIgnoreCase)) return false;
        var ext = System.IO.Path.GetExtension(name).ToLowerInvariant();
        if (ext is ".pub" or ".old" or ".bak" or ".dcx-bak" or ".txt" or ".log") return false;
        try
        {
            if (new FileInfo(path).Length is 0 or > MaxKeyBytes) return false;
            using var r = new StreamReader(path);
            var head = new char[200];
            var text = new string(head, 0, r.Read(head, 0, head.Length));
            return text.Contains("PRIVATE KEY-----") || text.StartsWith("PuTTY-User-Key-File-");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>At most <see cref="MaxKeyBytes"/> of the file, else null (0.6.30 sweep: a symlinked "key" reports the
    /// link's size, so the read itself is bounded, not the size check).</summary>
    private static string? ReadBounded(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
        var buf = new byte[MaxKeyBytes + 1];
        int n = 0, r;
        while (n < buf.Length && (r = fs.Read(buf, n, buf.Length - n)) > 0) n += r;
        return n > MaxKeyBytes ? null : Encoding.UTF8.GetString(buf, 0, n);
    }

    /// <summary>Text for the screen: no control or Unicode format characters (a bidi override can't make one name look like another).</summary>
    private static string Shown(string s, int max) =>
        new string(s.Where(c => !char.IsControl(c) && char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format).Take(max).ToArray());

    /// <summary>More key-derivation rounds than this and a passphrase check would freeze the app for minutes (ssh-keygen's
    /// default is 16; -a 100 is a careful choice).</summary>
    public const int MaxKdfRounds = 1000;

    private static Found? Read(string path)
    {
        string? text;
        try { text = ReadBounded(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        if (text == null) return null;
        var name = Shown(System.IO.Path.GetFileName(path), 120);
        var (pubType, pubFp, comment) = FromPub(path + ".pub");
        bool isDefault = DefaultNames.Contains(name, StringComparer.OrdinalIgnoreCase);
        if (KdfRounds(text) is > MaxKdfRounds and var rounds)
            return new Found { Path = path, FileName = name, IsDefault = false, State = State.Unsupported, KeyType = pubType, Fingerprint = pubFp, Comment = comment, Text = text,
                Why = $"its passphrase is stretched {rounds:N0} times (more than {MaxKdfRounds:N0}): checking it would freeze the app" };
        if (SecurityKeyNames.Contains(name, StringComparer.OrdinalIgnoreCase) || pubType.StartsWith("sk-", StringComparison.Ordinal))
            return new Found { Path = path, FileName = name, IsDefault = false, State = State.Unsupported, KeyType = pubType, Fingerprint = pubFp, Comment = comment, Text = text,
                Why = "a FIDO security key: the key on the hardware signs, and the SSH library here can't use it" };
        var probe = new Credential { Name = name, Kind = CredentialKind.PrivateKey, Secret = text };
        try
        {
            KeyTools.Describe(probe);
            return new Found { Path = path, FileName = name, IsDefault = isDefault, State = State.Ready, KeyType = probe.KeyType, Fingerprint = probe.Fingerprint, Comment = comment, Text = text };
        }
        catch (Exception ex)
        {
            if (IsEncrypted(text))
                return new Found { Path = path, FileName = name, IsDefault = isDefault, State = State.NeedsPassphrase, KeyType = pubType, Fingerprint = pubFp, Comment = comment, Text = text };
            return new Found { Path = path, FileName = name, IsDefault = false, State = State.Unsupported, KeyType = pubType, Fingerprint = pubFp, Comment = comment, Text = text,
                Why = "not a key the SSH library can read (" + ex.Message.Split('\n')[0].TrimEnd('.') + ")" };
        }
    }

    /// <summary>Type, fingerprint and comment from a .pub file next to the key ("", "", "" when there is none).</summary>
    private static (string Type, string Fingerprint, string Comment) FromPub(string pubPath)
    {
        try
        {
            if (!File.Exists(pubPath) || ReadBounded(pubPath) is not { } pub) return ("", "", "");
            var parts = pub.Trim().Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return ("", "", "");
            var blob = Convert.FromBase64String(parts[1]);
            var comment = parts.Length > 2 ? Shown(parts[2], 80) : "";
            return (Shown(parts[0], 40), "SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('='), comment);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException) { return ("", "", ""); }
    }

    /// <summary>Whether the key text is protected by a passphrase: OpenSSH's own format with a cipher, an encrypted PEM or
    /// PKCS#8 key, or a PuTTY key with encryption.</summary>
    public static bool IsEncrypted(string text)
    {
        if (text.Contains("Proc-Type: 4,ENCRYPTED") || text.Contains("BEGIN ENCRYPTED PRIVATE KEY")) return true;
        if (text.StartsWith("PuTTY-User-Key-File-"))
            return text.Split('\n').FirstOrDefault(l => l.StartsWith("Encryption:")) is { } e && e["Encryption:".Length..].Trim() != "none";
        return OpenSsh(text) is { } k && k.Cipher != "none";
    }

    /// <summary>The bcrypt rounds an OpenSSH-format key asks for (0 when it has none or isn't that format).</summary>
    public static long KdfRounds(string text) => OpenSsh(text) is { Kdf: "bcrypt" } k ? k.Rounds : 0;

    /// <summary>The cipher, KDF and KDF rounds of an OpenSSH-format private key, read with every length checked; null when
    /// it isn't one.</summary>
    private static (string Cipher, string Kdf, long Rounds)? OpenSsh(string text)
    {
        // Split so the public source's privacy scan doesn't read the header as a key.
        const string begin = "-----BEGIN OPENSSH " + "PRIVATE KEY-----", end = "-----END OPENSSH " + "PRIVATE KEY-----";
        int a = text.IndexOf(begin, StringComparison.Ordinal), b = a < 0 ? -1 : text.IndexOf(end, a, StringComparison.Ordinal);
        if (a < 0 || b < a) return null;
        byte[] blob;
        try { blob = Convert.FromBase64String(string.Concat(text[(a + begin.Length)..b].Where(c => !char.IsWhiteSpace(c)))); }
        catch (FormatException) { return null; }
        var magic = "openssh-key-v1\0"u8;
        if (blob.Length < magic.Length || !blob.AsSpan(0, magic.Length).SequenceEqual(magic)) return null;
        int at = magic.Length;
        long U32() { if (at + 4 > blob.Length) throw new FormatException(); long v = ((long)blob[at] << 24) | ((long)blob[at + 1] << 16) | ((long)blob[at + 2] << 8) | blob[at + 3]; at += 4; return v; }
        byte[] Str(int max) { var len = U32(); if (len > max || at + len > blob.Length) throw new FormatException(); var s = blob.AsSpan(at, (int)len).ToArray(); at += (int)len; return s; }
        try
        {
            var cipher = Encoding.ASCII.GetString(Str(64));
            var kdf = Encoding.ASCII.GetString(Str(64));
            long rounds = 0;
            if (kdf == "bcrypt")
            {
                var opts = Str(1024);
                // kdfoptions: string salt, uint32 rounds.
                if (opts.Length >= 8)
                {
                    long salt = ((long)opts[0] << 24) | ((long)opts[1] << 16) | ((long)opts[2] << 8) | opts[3];
                    if (salt + 8 <= opts.Length) { int r = 4 + (int)salt; rounds = ((long)opts[r] << 24) | ((long)opts[r + 1] << 16) | ((long)opts[r + 2] << 8) | opts[r + 3]; }
                }
            }
            return cipher.Length > 0 ? (cipher, kdf, rounds) : null;
        }
        catch (FormatException) { return null; }
    }

    /// <summary>The credential for a found key (its file text, its passphrase if any), named after the file. Throws when
    /// the passphrase is wrong or the key can't be read.</summary>
    public static Credential ToCredential(Found f, IEnumerable<Credential> stored, string passphrase = "")
    {
        if (f.State == State.Unsupported) throw new InvalidOperationException($"{f.FileName}: {f.Why}.");
        var name = f.FileName;
        if (stored.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) name = $"{f.FileName} ({Environment.MachineName})";
        var c = new Credential
        {
            Name = name, Kind = CredentialKind.PrivateKey, Category = "ssh", Secret = f.Text, Passphrase = passphrase,
            Notes = $"Copied from ~/.ssh/{f.FileName} on {DateTime.Now:yyyy-MM-dd} (the file itself is unchanged).",
        };
        KeyTools.Describe(c);   // the passphrase is checked here
        return c;
    }
}
