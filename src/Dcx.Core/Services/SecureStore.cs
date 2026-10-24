using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DcxManager.Services;

/// <summary>
/// Everything the app writes to disk goes through here and is encrypted with AES-256-GCM under one random 256-bit data key.
/// The data key itself is never stored in the clear. It is wrapped by:
///   • Windows DPAPI (current user) — unlocks automatically when you sign in to Windows, and/or
///   • a master password (PBKDF2-SHA256, 600k iterations) — when "require on start" is on, the DPAPI copy is deleted,
///     so the files are useless without the password even to an administrator or from a backup of the disk.
/// Each file is bound to its own name (AES-GCM associated data), so encrypted files cannot be swapped for one another.
/// </summary>
public static class SecureStore
{
    private const int Pbkdf2Iterations = 600_000;
    private static readonly byte[] Magic = "DCX1"u8.ToArray();

    private static byte[]? _key;
    private static Keyring _ring = new();
    private static readonly object Gate = new();

    /// <summary>The standard store folder: %APPDATA%\DarkCipher Systems\ServerManagement (0.4.0; <see cref="AppInfo.MigrateLegacyFolders"/>
    /// brings the pre-0.4.0 folder here). Holds location.json when the store was moved.</summary>
    public static string HomeDir { get; } = AppInfo.RoamingDir;

    /// <summary>DCX_DATA_DIR (used by --selftest so tests never touch real data), else the chosen profile's folder
    /// (0.3.0 §23, <see cref="UseProfileDir"/>), else a moved location, else <see cref="HomeDir"/>. Resolved on first use.</summary>
    public static string DataDir { get => _dataDir ??= ResolveDataDir(); private set => _dataDir = value; }
    private static string? _dataDir;

    public static bool IsRelocated => !DataDir.Equals(HomeDir, StringComparison.OrdinalIgnoreCase) && Environment.GetEnvironmentVariable("DCX_DATA_DIR") is not { Length: > 0 } && _profileDir == null;
    private static string? _profileDir;

    /// <summary>Points the store at a profile's folder before anything has used it (start-up, after the profile picker).
    /// Too late once a file was read: the app restarts into another profile instead.</summary>
    public static void UseProfileDir(string dir)
    {
        if (_dataDir != null && !_dataDir.Equals(dir, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("the store is already in use; switching profiles needs a restart");
        _profileDir = dir;
        _dataDir = dir;
    }

    private static string ResolveDataDir()
    {
        if (Environment.GetEnvironmentVariable("DCX_DATA_DIR") is { Length: > 0 } d) return d;
        if (_profileDir != null) return _profileDir;
        try
        {
            var ptr = Path.Combine(HomeDir, "location.json");
            if (File.Exists(ptr) && JsonDocument.Parse(File.ReadAllText(ptr)).RootElement.TryGetProperty("DataDir", out var p) && p.GetString() is { Length: > 0 } dir && File.Exists(Path.Combine(dir, "keyring.json")))
                return dir;
        }
        catch { }
        return HomeDir;
    }

    /// <summary>Points the app at a new store folder (after DataMover has copied and verified it).</summary>
    internal static void SwitchDataDir(string dir)
    {
        // Under DCX_DATA_DIR (self-test) the pointer file must never be written: it would redirect the real app.
        if (Environment.GetEnvironmentVariable("DCX_DATA_DIR") is { Length: > 0 }) { lock (Gate) DataDir = dir; return; }
        Directory.CreateDirectory(HomeDir);
        var ptr = Path.Combine(HomeDir, "location.json");
        if (dir.Equals(HomeDir, StringComparison.OrdinalIgnoreCase)) { try { File.Delete(ptr); } catch { } }
        else
        {
            var tmp = ptr + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new { DataDir = dir, Moved = DateTime.Now }));
            File.Move(tmp, ptr, overwrite: true);
        }
        lock (Gate) DataDir = dir;
    }

    private static string RingPath => Path.Combine(DataDir, "keyring.json");

    public static bool IsUnlocked => _key != null;
    /// <summary>When the data key became available this run (entries logged before it could only be plain).</summary>
    public static DateTime? UnlockedAt { get; private set; }
    public static bool HasPassword => _ring.Password != null;
    /// <summary>True when only the master password can open the store (no DPAPI copy of the key).</summary>
    public static bool PasswordRequiredOnStart => _ring.Password != null && _ring.Dpapi == null;

    /// <summary>Loads (or creates) the key ring. Returns true if the store is open; false means the master password is needed.</summary>
    public static bool Initialize()
    {
        Directory.CreateDirectory(DataDir);
        if (!File.Exists(RingPath))
        {
            _key = RandomNumberGenerator.GetBytes(32);
            _ring = new Keyring { Dpapi = Convert.ToBase64String(ProtectedData.Protect(_key, Entropy, DataProtectionScope.CurrentUser)) };
            SaveRing();
            UnlockedAt = DateTime.Now;
            return true;
        }
        _ring = JsonSerializer.Deserialize<Keyring>(File.ReadAllText(RingPath)) ?? new Keyring();
        if (_ring.Dpapi != null)
        {
            try { _key = ProtectedData.Unprotect(Convert.FromBase64String(_ring.Dpapi), Entropy, DataProtectionScope.CurrentUser); UnlockedAt = DateTime.Now; return true; }
            catch (CryptographicException) { if (_ring.Password == null) throw new InvalidOperationException("The data key belongs to another Windows account and no master password is set."); }
        }
        return false;
    }

    public static bool Unlock(string password)
    {
        if (_ring.Password is not { } pw) return false;
        try
        {
            var kek = Derive(password, Convert.FromBase64String(pw.Salt), pw.Iterations);
            _key = Open(kek, Convert.FromBase64String(pw.Blob), "keyring");
            UnlockedAt = DateTime.Now;
            return true;
        }
        catch (CryptographicException) { return false; }
    }

    /// <summary>Sets or changes the master password. requireOnStart removes the DPAPI copy of the key.</summary>
    public static void SetPassword(string password, bool requireOnStart)
    {
        var key = _key ?? throw new InvalidOperationException("Store is locked.");
        var salt = RandomNumberGenerator.GetBytes(16);
        var kek = Derive(password, salt, Pbkdf2Iterations);
        _ring.Password = new PasswordWrap { Salt = Convert.ToBase64String(salt), Iterations = Pbkdf2Iterations, Blob = Convert.ToBase64String(Seal(kek, key, "keyring")) };
        // While Hello sign-in is on there is no Windows-account copy; remember the choice for when Hello is turned off.
        if (_ring.Hello != null) { _ring.Dpapi = null; _ring.HelloRemovedDpapi = !requireOnStart; }
        else _ring.Dpapi = requireOnStart ? null : Convert.ToBase64String(ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser));
        SaveRing();
    }

    public static void RemovePassword()
    {
        var key = _key ?? throw new InvalidOperationException("Store is locked.");
        if (_ring.Hello != null) throw new InvalidOperationException("Turn off Windows Hello sign-in first: the master password is its fallback.");
        _ring.Dpapi = Convert.ToBase64String(ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser));
        _ring.Password = null;
        SaveRing();
    }

    // ---- Windows Hello (0.3.0 §2, ADR-039) -------------------------------------------------------------------------
    // A Hello key (TPM-backed RSA, signs only after the user verifies) signs a random 32-byte challenge; RSA PKCS#1 v1.5
    // signatures are deterministic, so HKDF over the signature gives the same key-encryption key each time. The data key
    // is sealed with it. While Hello sign-in is on, the Windows-account (DPAPI) copy of the key is removed, so nothing opens
    // without Hello or the master password (which must exist first: it is the fallback).

    public static bool HasHello => _ring.Hello != null;
    public static string? HelloKeyName => _ring.Hello?.KeyName;
    public static byte[]? HelloChallenge => _ring.Hello is { } h ? Convert.FromBase64String(h.Challenge) : null;

    /// <summary>The challenge and key name to use when turning Hello on (a fresh challenge each time).</summary>
    public static (string KeyName, byte[] Challenge) NewHelloSetup() =>
        ("DarkCipher ServerManagement " + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(DataDir.ToUpperInvariant())))[..12], RandomNumberGenerator.GetBytes(32));

    /// <summary>Turns Hello sign-in on with the signature the new Hello key made over <paramref name="challenge"/>.</summary>
    public static void SetHello(string keyName, byte[] challenge, byte[] signature)
    {
        var key = _key ?? throw new InvalidOperationException("Store is locked.");
        if (_ring.Password == null) throw new InvalidOperationException("Set a master password first: it is the fallback when Windows Hello isn't available.");
        _ring.Hello = NewHelloWrap(keyName, challenge, signature, key);   // 0.6.12: hybrid (Hello signature + DPAPI secret)
        if (_ring.Dpapi != null) { _ring.HelloRemovedDpapi = true; _ring.Dpapi = null; }
        SaveRing();
    }

    /// <summary>Turns Hello sign-in off; the Windows-account copy of the key comes back if Hello had removed it.</summary>
    public static void RemoveHello()
    {
        var key = _key ?? throw new InvalidOperationException("Store is locked.");
        if (_ring.HelloRemovedDpapi) _ring.Dpapi = Convert.ToBase64String(ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser));
        _ring.Hello = null; _ring.HelloRemovedDpapi = false;
        SaveRing();
    }

    /// <summary>Opens the store with a Hello signature (start-up). False when it isn't the right signature.</summary>
    public static bool UnlockWithHello(byte[] signature)
    {
        if (_ring.Hello is not { } h || HelloKekFor(h, signature) is not { } kek) return false;
        try { _key = Open(kek, Convert.FromBase64String(h.Blob), "keyring-hello"); UnlockedAt = DateTime.Now; }
        catch (CryptographicException) { return false; }
        if (h.Pepper.Length == 0)
        {
            // A wrap from before 0.6.12: re-seal it as the hybrid now that the signature and the key are both here.
            try { _ring.Hello = NewHelloWrap(h.KeyName, Convert.FromBase64String(h.Challenge), signature, _key); SaveRing(); }
            catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException) { _ring.Hello = h; Log.Write("Windows Hello: upgrading to the post-quantum wrap", ex); }
        }
        return true;
    }

    /// <summary>Checks a Hello signature without changing anything (the lock screen).</summary>
    public static bool VerifyHello(byte[] signature)
    {
        if (_ring.Hello is not { } h || HelloKekFor(h, signature) is not { } kek) return false;
        try { Open(kek, Convert.FromBase64String(h.Blob), "keyring-hello"); return true; }
        catch (CryptographicException) { return false; }
    }

    // 0.6.12 (ADR-090, DarkCipher0x: "make sure everything that is saved is post quantum encryption"): a Hello signature is an
    // RSA / ECC signature, and a future quantum computer could rebuild the Hello private key from its public key (readable
    // from a copy of the disk) and re-create the signature. So the key-encryption key now also takes a random 32-byte
    // secret protected by DPAPI for this Windows account (symmetric: AES, quantum-safe): opening needs the Hello signature
    // AND this Windows account. A Hello wrap from before 0.6.12 (no secret) still opens and is upgraded right then.

    private static readonly byte[] HelloPepperEntropy = "dcx-hello-pepper-v2"u8.ToArray();

    /// <summary>Whether the Hello wrap is the post-quantum hybrid (Hello signature + a DPAPI-protected secret).</summary>
    public static bool HelloPostQuantum => _ring.Hello is { Pepper.Length: > 0 };

    private static byte[] HelloKek(byte[] signature, byte[] salt) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, signature, 32, salt, "dcx-hello-kek-v1"u8.ToArray());

    private static byte[] HelloKekPq(byte[] signature, byte[] pepper, byte[] salt)
    {
        var ikm = new byte[signature.Length + pepper.Length];
        signature.CopyTo(ikm, 0); pepper.CopyTo(ikm, signature.Length);
        try { return HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 32, salt, "dcx-hello-kek-v2-pq"u8.ToArray()); }
        finally { CryptographicOperations.ZeroMemory(ikm); }
    }

    /// <summary>The key-encryption key of the current Hello wrap for a signature (null when its DPAPI secret can't be opened
    /// on this Windows account: then only the master password opens the store).</summary>
    private static byte[]? HelloKekFor(HelloWrap h, byte[] signature)
    {
        var salt = Convert.FromBase64String(h.Salt);
        if (h.Pepper.Length == 0) return HelloKek(signature, salt);
        byte[] pepper;
        try { pepper = ProtectedData.Unprotect(Convert.FromBase64String(h.Pepper), HelloPepperEntropy, DataProtectionScope.CurrentUser); }
        catch (CryptographicException) { return null; }
        try { return HelloKekPq(signature, pepper, salt); }
        finally { CryptographicOperations.ZeroMemory(pepper); }
    }

    /// <summary>A new hybrid Hello wrap of <paramref name="key"/>.</summary>
    private static HelloWrap NewHelloWrap(string keyName, byte[] challenge, byte[] signature, byte[] key)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var pepper = RandomNumberGenerator.GetBytes(32);
        try
        {
            return new HelloWrap
            {
                KeyName = keyName, Challenge = Convert.ToBase64String(challenge), Salt = Convert.ToBase64String(salt),
                Pepper = Convert.ToBase64String(ProtectedData.Protect(pepper, HelloPepperEntropy, DataProtectionScope.CurrentUser)),
                Blob = Convert.ToBase64String(Seal(HelloKekPq(signature, pepper, salt), key, "keyring-hello")),
            };
        }
        finally { CryptographicOperations.ZeroMemory(pepper); }
    }

    /// <summary>Self-test: a Hello wrap in the pre-0.6.12 form (signature only), to check it still opens and is upgraded.</summary>
    internal static void SetHelloLegacyForTest(string keyName, byte[] challenge, byte[] signature)
    {
        var key = _key ?? throw new InvalidOperationException("Store is locked.");
        var salt = RandomNumberGenerator.GetBytes(16);
        _ring.Hello = new HelloWrap { KeyName = keyName, Challenge = Convert.ToBase64String(challenge), Salt = Convert.ToBase64String(salt), Blob = Convert.ToBase64String(Seal(HelloKek(signature, salt), key, "keyring-hello")) };
        SaveRing();
    }
    // ---- second factor (TOTP, RFC 6238) ------------------------------------------------------------------------
    // The authenticator secret is sealed with the data key and recovery codes are stored only as salted hashes.
    // It gates opening the app (and the lock screen); the file encryption itself rests on the key / master password.

    public static bool HasTotp => _ring.Totp != null;

    public static void SetTotp(byte[] secret, IEnumerable<string> recoveryCodes)
    {
        var key = _key ?? throw new InvalidOperationException("Store is locked.");
        _ring.Totp = Convert.ToBase64String(Seal(key, secret, "totp"));
        _ring.Recovery = recoveryCodes.Select(HashRecovery).ToList();
        _ring.TotpLastStep = 0;
        SaveRing();
    }

    public static void RemoveTotp()
    {
        _ring.Totp = null; _ring.Recovery = null; _ring.TotpLastStep = 0;
        SaveRing();
    }

    public static int RecoveryCodesLeft => _ring.Recovery?.Count ?? 0;

    /// <summary>Checks a 6-digit code (±1 step, no replay) or a one-time recovery code (consumed).</summary>
    public static bool VerifySecondFactor(string input)
    {
        if (_ring.Totp is not { } sealedSecret || _key is not { } key) return false;
        var code = new string(input.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (code.Length == 6 && code.All(char.IsDigit))
        {
            var secret = Open(key, Convert.FromBase64String(sealedSecret), "totp");
            try
            {
                long step = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
                for (long s = step - 1; s <= step + 1; s++)
                    if (s > _ring.TotpLastStep && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Totp.Code(secret, s)), Encoding.ASCII.GetBytes(code)))
                    {
                        _ring.TotpLastStep = s;
                        SaveRing();
                        return true;
                    }
                return false;
            }
            finally { CryptographicOperations.ZeroMemory(secret); }
        }
        var h = HashRecovery(code);
        if (_ring.Recovery?.Contains(h) == true) { _ring.Recovery.Remove(h); SaveRing(); return true; }
        return false;
    }

    private static string HashRecovery(string code) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes("dcx-recovery|" + new string(code.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant())));

    /// <summary>Forgets the data key from memory (used when the app closes from the lock screen).</summary>
    public static void Forget()
    {
        if (_key != null) CryptographicOperations.ZeroMemory(_key);
        _key = null;
    }

    /// <summary>Re-opens a copied keyring (used to verify a moved store with the in-memory key).</summary>
    internal static bool KeyMatchesRing(string ringPath)
    {
        try
        {
            var ring = JsonSerializer.Deserialize<Keyring>(File.ReadAllText(ringPath));
            return ring != null && (ring.Dpapi != null || ring.Password != null || ring.Hello != null);
        }
        catch { return false; }
    }

    public static bool VerifyPassword(string password)
    {
        if (_ring.Password is not { } pw) return false;
        try { Open(Derive(password, Convert.FromBase64String(pw.Salt), pw.Iterations), Convert.FromBase64String(pw.Blob), "keyring"); return true; }
        catch (CryptographicException) { return false; }
    }

    // ---- files ------------------------------------------------------------------------------------------------

    public static T Read<T>(string name) where T : new()
    {
        var path = Path.Combine(DataDir, name);
        if (!File.Exists(path)) return new T();
        var plain = Decrypt(File.ReadAllBytes(path), name);
        return JsonSerializer.Deserialize<T>(plain, Json) ?? new T();
    }

    public static void Write<T>(string name, T value)
    {
        var bytes = Encrypt(JsonSerializer.SerializeToUtf8Bytes(value, Json), name);
        var path = Path.Combine(DataDir, name);
        var tmp = path + ".tmp";
        lock (Gate)
        {
            // After a restore/migration rolled files back on disk, the in-memory state is stale: nothing may overwrite
            // the restored files before the app restarts.
            if (Frozen) { Log.Write("Store frozen", new InvalidOperationException($"write of {name} skipped (restart pending)")); return; }
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, path, overwrite: true);
        }
    }

    /// <summary>Set once files were restored underneath the running app: every later <see cref="Write{T}"/> is dropped
    /// until the restart (Services/Snapshots.cs).</summary>
    public static bool Frozen { get; internal set; }

    /// <summary>Raw (still encrypted) bytes of several store files, read under the write lock so no file is half-replaced.</summary>
    internal static Dictionary<string, byte[]> ReadRaw(string dir, IEnumerable<string> names)
    {
        var o = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        lock (Gate) foreach (var n in names) { var p = Path.Combine(dir, n); if (File.Exists(p)) o[n] = File.ReadAllBytes(p); }
        return o;
    }

    /// <summary>Runs <paramref name="a"/> holding the store's write lock (the commit of staged files).</summary>
    internal static void Locked(Action a) { lock (Gate) a(); }

    public static byte[] Encrypt(byte[] plain, string context) => Seal(_key ?? throw new InvalidOperationException("Store is locked."), plain, context);
    public static byte[] Decrypt(byte[] blob, string context) => Open(_key ?? throw new InvalidOperationException("Store is locked."), blob, context);

    /// <summary>A purpose-bound key derived from the data key (HKDF), for MACs and the like; never the data key itself.</summary>
    public static byte[] DeriveSubKey(string purpose) => HKDF.DeriveKey(HashAlgorithmName.SHA256, _key ?? throw new InvalidOperationException("Store is locked."), 32, null, Encoding.UTF8.GetBytes("dcx-subkey|" + purpose));

    public static string EncryptString(string text, string context) => Convert.ToBase64String(Encrypt(Encoding.UTF8.GetBytes(text), context));
    public static string DecryptString(string b64, string context) => Encoding.UTF8.GetString(Decrypt(Convert.FromBase64String(b64), context));

    // ---- primitives -------------------------------------------------------------------------------------------

    /// <summary>Layout: "DCX1" | nonce (12) | tag (16) | ciphertext.</summary>
    public static byte[] Seal(byte[] key, byte[] plain, string context)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var ct = new byte[plain.Length];
        using (var gcm = new AesGcm(key, 16)) gcm.Encrypt(nonce, plain, ct, tag, Encoding.UTF8.GetBytes(context));
        var o = new byte[4 + 12 + 16 + ct.Length];
        Magic.CopyTo(o, 0); nonce.CopyTo(o, 4); tag.CopyTo(o, 16); ct.CopyTo(o, 32);
        return o;
    }

    public static byte[] Open(byte[] key, byte[] blob, string context)
    {
        if (blob.Length < 32 || !blob.AsSpan(0, 4).SequenceEqual(Magic)) throw new CryptographicException("Not a DarkCipher encrypted file.");
        var plain = new byte[blob.Length - 32];
        using var gcm = new AesGcm(key, 16);
        gcm.Decrypt(blob.AsSpan(4, 12), blob.AsSpan(32), blob.AsSpan(16, 16), plain, Encoding.UTF8.GetBytes(context));
        return plain;
    }

    public static byte[] Derive(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);

    private static readonly byte[] Entropy = "darkcipher0x.ServerManagement.v1"u8.ToArray();

    private static void SaveRing()
    {
        var tmp = RingPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_ring, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, RingPath, overwrite: true);
    }

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private sealed class Keyring
    {
        public int Version { get; set; } = 1;
        public string? Dpapi { get; set; }
        public PasswordWrap? Password { get; set; }
        public string? Totp { get; set; }
        public List<string>? Recovery { get; set; }
        public long TotpLastStep { get; set; }
        public HelloWrap? Hello { get; set; }
        public bool HelloRemovedDpapi { get; set; }
    }

    private sealed class HelloWrap
    {
        public string KeyName { get; set; } = "";
        public string Challenge { get; set; } = "";
        public string Salt { get; set; } = "";
        public string Blob { get; set; } = "";
        /// <summary>0.6.12: the DPAPI-protected 32-byte secret mixed into the key (base64); empty in a pre-0.6.12 wrap.</summary>
        public string Pepper { get; set; } = "";
    }

    private sealed class PasswordWrap
    {
        public string Salt { get; set; } = "";
        public int Iterations { get; set; }
        public string Blob { get; set; } = "";
    }
}
