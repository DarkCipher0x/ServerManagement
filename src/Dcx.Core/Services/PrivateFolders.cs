using System.Collections.Concurrent;
using System.IO;

namespace DcxManager.Services;

/// <summary>
/// The folders where other programs must read plain files that this app writes (0.6.33, DarkCipher0x: "ensure that
/// everything that this application writes, is encrypted ... any of the browser data or cache etc, must be secure, there
/// must be no leaks"): the browser engine's profile and cache (WebView2), the Bitwarden CLI's data, a key handed to
/// OpenSSH, files for the Share sheet or another app, .rdp files, SCP transfer buffers, the Claude Code workspace and the
/// screenshot folder. The app's own store is sealed with its own key (<see cref="SecureStore"/>); these can't be, because
/// the program that reads them doesn't hold that key.
/// <para>So each folder is marked for Windows file encryption (EFS) before anything is written to it: every file created
/// in it is encrypted with this Windows account's key — transparent to programs running as this user, unreadable to other
/// accounts, to an offline copy of the disk and to a backup of the raw files. Files written before 0.6.33 are encrypted
/// in place by <see cref="EncryptExisting"/> (in the background; one that is open is done at a later start).</para>
/// <para>Where EFS isn't available (Windows Home, a FAT/exFAT or network drive, EFS turned off by Group Policy) the folder
/// stays readable only by this account (its profile's permissions) and <see cref="Status"/> says why — the encryption
/// audit shows it.</para>
/// </summary>
public static class PrivateFolders
{
    /// <summary>The cache folder's subfolders that hold plain files for other programs (the encryption audit walks these).</summary>
    public static readonly string[] CacheSubfolders = ["WebView2", "bitwarden-cli", "tmp", "share", "rdp", "transfer-tmp"];

    private static readonly ConcurrentDictionary<string, string?> Done = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every private folder known to this run: the cache subfolders plus any registered elsewhere (a scratch
    /// store's browser profile, the Claude Code workspace, the screenshot folder).</summary>
    public static IEnumerable<string> All =>
        CacheSubfolders.Select(s => Path.Combine(AppInfo.CacheDir, s)).Concat(Done.Keys).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>The last problem with Windows file encryption on this PC (null when it worked or wasn't tried).</summary>
    public static string? Unavailable { get; private set; }

    /// <summary>Creates <paramref name="dir"/> if needed and marks it encrypted, so files written into it are encrypted
    /// from the first byte. Cheap after the first call for a folder. Returns <paramref name="dir"/>.</summary>
    public static string Ensure(string dir)
    {
        var full = Path.GetFullPath(dir).TrimEnd('\\');
        if (Done.ContainsKey(full) && Directory.Exists(full)) return dir;
        Directory.CreateDirectory(full);
        string? why = null;
        if ((File.GetAttributes(full) & FileAttributes.Encrypted) == 0)
        {
            try { File.Encrypt(full); }   // EncryptFileW on a folder: new files in it (and in new subfolders) are encrypted
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or NotSupportedException)
            {
                why = Explain(ex);
                Unavailable = why;
                Log.Write("Windows file encryption (" + Path.GetFileName(full) + ")", ex);
            }
        }
        Done[full] = why;
        return dir;
    }

    /// <summary>Encrypts what is already in the folder (files written before it was marked). Files that are open are
    /// skipped (done at a later start). Returns how many files were encrypted now.</summary>
    public static int EncryptExisting(string dir) => EncryptExisting(dir, out _);

    private static int EncryptExisting(string dir, out int skipped)
    {
        skipped = 0;
        if (!Directory.Exists(dir) || !IsEncrypted(dir)) return 0;
        int n = 0;
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        // Folders first (new files in them are then encrypted too), then the files; the attributes come with the listing.
        foreach (var e in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", opts).OrderBy(e => e is FileInfo).ToList())
        {
            if ((e.Attributes & FileAttributes.Encrypted) != 0) continue;
            try { File.Encrypt(e.FullName); if (e is FileInfo) n++; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; }
        }
        return n;
    }

    private const string Marker = ".dcx-encrypted";

    /// <summary>The browser engine's component updater downloads public data (certificate revocation lists, origin
    /// trials, protocol lists, …) elsewhere and MOVES it into the profile, and a moved file keeps its attributes, so these
    /// arrive unencrypted while the browser runs. They hold no user data (the same files for everyone); <see
    /// cref="Prepare"/> encrypts them at the next start, and the audit names them instead of calling them a leak.</summary>
    public static readonly string[] BrowserComponentFolders =
    [
        "AutoLaunchProtocolsComponent", "CertificateRevocation", "component_crx_cache", "OriginTrials", "TrustTokenKeyCommitments",
        "Subresource Filter", "Trust Protection Lists", "ZxcvbnData", "FirstPartySetsPreloaded", "SSLErrorAssistant", "MEIPreload",
        "PKIMetadata", "Safe Browsing", "hyphen-data", "WidevineCdm", "CookieReadinessList", "PrivacySandboxAttestationsPreloaded",
        "Speech Recognition",
    ];

    /// <summary>True when <paramref name="file"/> lies in a browser component folder directly under the browser's user
    /// data folder below <paramref name="root"/>: one of <see cref="BrowserComponentFolders"/>, or any folder laid out
    /// the way the component updater installs (EBWebView\&lt;component&gt;\&lt;version&gt;\manifest.json), so a component
    /// a newer runtime adds is recognised too.</summary>
    public static bool IsBrowserComponent(string root, string file)
    {
        var parts = Path.GetRelativePath(root, file).Split('\\');
        if (parts.Length < 3 || !parts[0].Equals("EBWebView", StringComparison.OrdinalIgnoreCase)) return false;
        if (BrowserComponentFolders.Contains(parts[1], StringComparer.OrdinalIgnoreCase)) return true;
        return parts.Length > 3 && Version.TryParse(parts[2], out _)
            && File.Exists(Path.Combine(root, parts[0], parts[1], parts[2], "manifest.json"));
    }

    /// <summary>For a folder whose files another program keeps open while it runs (the browser profile): call before
    /// that program starts. Marks the folder and encrypts anything in it that isn't (files from before 0.6.33, and files
    /// the browser moved in during its last run — see <see cref="BrowserComponentFolders"/>). Every start: the listing
    /// carries the attributes, so a profile that is already encrypted costs one directory walk. The marker tells <see
    /// cref="ProtectExisting"/> to leave the folder to this (its files are open while the browser runs).</summary>
    public static void Prepare(string dir)
    {
        try
        {
            Ensure(dir);
            if (!IsEncrypted(dir)) return;
            EncryptExisting(dir, out _);
            var marker = Path.Combine(dir, Marker);
            if (!File.Exists(marker))
            {
                File.WriteAllText(marker, "encrypted by Windows for this account\n");   // (an empty file isn't encrypted)
                if (!IsEncrypted(marker)) File.Encrypt(marker);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Write("Encrypt " + Path.GetFileName(dir), ex); }
    }

    /// <summary>At start-up, off the UI thread: marks the cache subfolders that exist and encrypts their older files.</summary>
    public static int ProtectExisting()
    {
        int n = 0;
        foreach (var dir in All.ToList())
        {
            if (!Directory.Exists(dir) || File.Exists(Path.Combine(dir, Marker))) continue;   // the browser profile: done by Prepare
            try { Ensure(dir); n += EncryptExisting(dir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Write("Encrypt " + Path.GetFileName(dir), ex); }
        }
        return n;
    }

    public static bool IsEncrypted(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.Encrypted) != 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>One line for Settings → Security and the audit.</summary>
    public static string Status => Unavailable == null
        ? "Browser profile, caches and hand-off files: encrypted by Windows (EFS) for this account"
        : "Browser profile, caches and hand-off files: readable only by this Windows account — Windows file encryption isn't available (" + Unavailable + ")";

    private static string Explain(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "turned off by policy, or this Windows edition has no EFS",
        PlatformNotSupportedException or NotSupportedException => "not supported here",
        IOException io when io.HResult is unchecked((int)0x80070032) or unchecked((int)0x800706F8) => "the drive doesn't support it",
        _ => ex.Message.Trim(),
    };
}
