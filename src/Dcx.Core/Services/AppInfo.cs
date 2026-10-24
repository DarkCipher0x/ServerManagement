using System.IO;
using System.Reflection;

namespace DcxManager.Services;

/// <summary>
/// What the app is called, which version runs and where its folders live. Moved from <c>App</c> (0.2.0 phase 6) so code
/// below the screens doesn't need the WPF application; <c>App</c>'s members forward here. The version is the exe's (the
/// entry assembly): the csproj's numeric <c>Version</c> and its <c>ReleaseLabel</c>.
/// Folders and registry keys are named after the vendor and the product (0.4.0, DarkCipher0x: "labelled correctly"):
/// <c>…\DarkCipher Systems\ServerManagement</c>. Until 0.3.1 the per-user folders sat under <c>DarkCipher</c>;
/// <see cref="MigrateLegacyFolders"/> moves them once, at start-up, before anything opens the store.
/// </summary>
public static class AppInfo
{
    public const string ProductName = "DarkCipher ServerManagement";
    /// <summary>The name on screen (window titles, headings, installer), like the logo (0.6.1, DarkCipher0x: "just ServerManagement
    /// like the logo.. not DarkCipher ServerManagement"). <see cref="ProductName"/> stays the identifier in exports and APIs.</summary>
    public const string DisplayName = "ServerManagement";
    /// <summary>The vendor, as folders and registry keys spell it.</summary>
    public const string Company = "DarkCipher Systems";
    /// <summary>The application's own name: the big title under the company line on covers and title blocks (0.5.1 #16).</summary>
    public const string AppName = "ServerManagement";
    /// <summary>The footer of every generated document and report (0.5.1 #16, BUILD.md §7): "ServerManagement 0.5.1 – DarkCipher Systems".</summary>
    public static string FooterLine => $"{AppName} {DisplayVersion} – {Company}";
    /// <summary>The vendor folder of 0.1.0–0.3.1 (moved to <see cref="Company"/> on first start of 0.4.0).</summary>
    public const string LegacyVendorFolder = "DarkCipher";
    /// <summary>The temp folder that carries a protected-settings request to the elevated copy.</summary>
    public const string RequestsTempFolder = "DarkCipher Systems-ServerManagement-requests";

    /// <summary>%APPDATA%\DarkCipher Systems\ServerManagement — the store's home (SecureStore.HomeDir, the profiles).</summary>
    public static string RoamingDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Company, "ServerManagement");

    /// <summary>%LOCALAPPDATA%\DarkCipher Systems\ServerManagement — logs, browser profile, caches, VPN files.</summary>
    public static string CacheDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Company, "ServerManagement");

    /// <summary>%USERPROFILE%\AppData\LocalLow\DarkCipher Systems — the Low-integrity sandbox folders of add-ons.</summary>
    public static string LocalLowVendorDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", Company);

    /// <summary>Short-lived files handed to Windows Share or an installer (cleared by the app).</summary>
    public static string ShareDir => Path.Combine(CacheDir, "share");

    /// <summary>The per-user folders of releases before 0.4.0 and where each one lives now (old, new).</summary>
    public static IReadOnlyList<(string Old, string New)> LegacyFolders
    {
        get
        {
            string F(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
            return new[]
            {
                (Path.Combine(F(Environment.SpecialFolder.ApplicationData), LegacyVendorFolder, "ServerManagement"), RoamingDir),
                (Path.Combine(F(Environment.SpecialFolder.LocalApplicationData), LegacyVendorFolder, "ServerManagement"), CacheDir),
                (Path.Combine(F(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", LegacyVendorFolder, "ServerManagement"), Path.Combine(LocalLowVendorDir, "ServerManagement")),
                (Path.Combine(F(Environment.SpecialFolder.MyDocuments), LegacyVendorFolder, "claude-workspace"), Path.Combine(F(Environment.SpecialFolder.MyDocuments), Company, "claude-workspace")),
            };
        }
    }

    /// <summary>Moves each pre-0.4.0 folder to its new name when the new one doesn't exist yet (a rename on the same
    /// volume: the files are untouched, so a relocated store's pointer, the profiles and the keyring come along).
    /// Returns what was moved. The store's move is the one that must not fail quietly: an app that started with an empty
    /// new folder next to a full old one would look like lost data, so that failure is thrown to the caller.</summary>
    public static List<string> MigrateLegacyFolders()
    {
        var done = new List<string>();
        foreach (var (old, @new) in LegacyFolders)
        {
            if (!Directory.Exists(old) || Directory.Exists(@new)) continue;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(@new)!);
                Directory.Move(old, @new);
                done.Add($"{old} → {@new}");
                // The old vendor folder goes when nothing else is left in it.
                var parent = Path.GetDirectoryName(old)!;
                if (Path.GetFileName(parent) == LegacyVendorFolder && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (@new == RoamingDir) throw new IOException($"The data folder could not be moved from {old} to {@new}: {ex.Message}", ex);
                done.Add($"{old}: not moved ({ex.Message})");
            }
        }
        return done;
    }

    private static Assembly Exe => Assembly.GetEntryAssembly() ?? typeof(AppInfo).Assembly;

    /// <summary>The numeric version (update checks, store metadata, cache folders): always x.y.z.</summary>
    public static string Version => Exe.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "";

    /// <summary>What the release is called, shown to people (e.g. "0.2.0-preview.1"; the csproj's ReleaseLabel), or the
    /// numeric version when there is no label.</summary>
    public static string DisplayVersion =>
        Exe.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion is { Length: > 0 } iv ? iv.Split('+')[0] : Version;
}

/// <summary>The self-test is running (set by it, read everywhere): no window, no dialog, no real network. Moved below
/// the screens in 0.2.0 phase 6; <c>SelfTest.Running</c> forwards here.</summary>
public static class TestMode
{
    public static bool Running { get; set; }
}
