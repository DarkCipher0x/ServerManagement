using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace DcxManager.Services;

/// <summary>
/// What the uninstaller removes besides the program (0.3.0 §11, ADR-040). The MSI runs it twice on a full uninstall (not on
/// an upgrade), before the program files go:
/// <list type="bullet">
/// <item><c>--cleanup …</c> as the user who uninstalls: that user's store, cache, shared-library membership, Windows Hello
/// key and temp files (<see cref="CleanUser"/>);</item>
/// <item><c>--cleanup-machine …</c> as SYSTEM: the admin-protected records and policy values for everyone, and, when asked,
/// every other Windows user's data on this PC (<see cref="CleanMachine"/>).</item>
/// </list>
/// Options (MSI properties → arguments): REMOVE_DATA → data, REMOVE_CACHE → cache, REMOVE_LIBRARY → library,
/// REMOVE_SETTINGS → settings, REMOVE_ALLUSERS → allusers; REMOVE_EVERYTHING=1 sets them all. Nothing the user saved
/// elsewhere (backups, exported keys, %USERPROFILE%\.ssh, a shared library's share) is ever touched. Only the app's own
/// folders and keys are deleted; a moved store loses only its store files, never the folder it was moved into.
/// </summary>
public static class Uninstall
{
    public sealed record Options(bool Data, bool Cache, bool Library, bool Settings, bool AllUsers)
    {
        public static Options Parse(IEnumerable<string> args)
        {
            var o = args.Select(a => a.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0].Trim().ToLowerInvariant(), p => p[1].Trim() == "1");
            bool all = o.GetValueOrDefault("everything");
            return new(all || o.GetValueOrDefault("data"), all || o.GetValueOrDefault("cache"), all || o.GetValueOrDefault("library"),
                all || o.GetValueOrDefault("settings"), all || o.GetValueOrDefault("allusers"));
        }
    }

    /// <summary>One Windows user's folders: the store (%APPDATA%\…), the cache (%LOCALAPPDATA%\…) and their temp folder.</summary>
    public sealed record Profile(string Store, string Cache, string Temp)
    {
        /// <summary>The same folders as releases before 0.4.0 named them (vendor folder "DarkCipher"): cleaned as well, so an
        /// install that was never started after the upgrade (nothing migrated) leaves nothing behind either.</summary>
        public Profile Legacy => new(Store.Replace(@"\" + AppInfo.Company + @"\", @"\" + AppInfo.LegacyVendorFolder + @"\"), Cache.Replace(@"\" + AppInfo.Company + @"\", @"\" + AppInfo.LegacyVendorFolder + @"\"), Temp);
    }

    public static Profile CurrentProfile() => new(AppInfo.RoamingDir, AppInfo.CacheDir, Path.GetTempPath());

    /// <summary>Cleans one user's things. <paramref name="deleteHelloKey"/> removes a Windows Hello key by name (only possible
    /// in that user's own session). Returns what it did (for the log and the self-test).</summary>
    public static List<string> CleanUser(Profile p, Options o, Action<string>? deleteHelloKey)
    {
        var done = CleanUserFolders(p, o, deleteHelloKey);
        if (p.Legacy != p) done.AddRange(CleanUserFolders(p.Legacy, o, deleteHelloKey, legacy: true));
        return done;
    }

    private static List<string> CleanUserFolders(Profile p, Options o, Action<string>? deleteHelloKey, bool legacy = false)
    {
        var done = new List<string>();
        var stores = new List<string> { p.Store };
        if (MovedStore(p.Store) is { } moved) stores.Add(moved);

        // Always: the app's own temp files (nothing of the user's).
        if (!legacy)
        {
            DeleteDir(Path.Combine(p.Temp, AppInfo.RequestsTempFolder), done);
            DeleteDir(Path.Combine(p.Temp, "DarkCipher-ServerManagement-requests"), done);   // the folder's name before 0.4.0
            foreach (var f in SafeFiles(p.Temp, "dcx-addon-*.exe")) DeleteFile(f, done);
        }

        if (o.Data || o.Library)
            foreach (var s in stores)
            {
                // The Windows Hello key goes with the store it opened (its name is in the keyring, which is plain JSON).
                if (o.Data && HelloKeyName(s) is { } key) { try { deleteHelloKey?.Invoke(key); done.Add("Windows Hello key " + key); } catch (Exception ex) { done.Add($"Windows Hello key {key}: {ex.Message}"); } }
                // Shared library membership: the local copies and the member keys (the team's share is never touched).
                foreach (var f in SafeFiles(s, "library-*.dcx")) DeleteFile(f, done);
            }
        if (o.Data)
        {
            if (stores.Count > 1)
            {
                var movedDir = stores[1];
                foreach (var f in DataMover.StoreFiles(movedDir).Concat(DataMover.SnapshotFiles(movedDir)).Concat(DataMover.SessionLogFiles(movedDir))) DeleteFile(f, done);
                foreach (var sub in new[] { Snapshots.DefaultDir(movedDir), Path.Combine(movedDir, "sessionlogs"), movedDir }) DeleteIfEmpty(sub, done);
            }
            DeleteDir(p.Store, done);
        }
        if (o.Cache) DeleteDir(p.Cache, done);
        return done;
    }

    /// <summary>The registry and the other users, as SYSTEM.</summary>
    public interface IMachine
    {
        void DeleteKey(RegistryHive hive, string path);
        /// <summary>Loaded user hives (HKU\&lt;SID&gt;) — the policies of users who are signed in.</summary>
        IEnumerable<string> LoadedUserSids();
        /// <summary>Every user profile folder on this PC (from ProfileList).</summary>
        IEnumerable<string> ProfileFolders();
    }

    public static List<string> CleanMachine(Options o, IMachine m)
    {
        var done = new List<string>();
        void Key(RegistryHive h, string path) { try { m.DeleteKey(h, path); done.Add($"{h}\\{path}"); } catch (Exception ex) { done.Add($"{h}\\{path}: {ex.Message}"); } }
        // The MSI removes its own values; this makes sure the key goes even if something else wrote there (0.4.0 moved it
        // under the vendor's key; the darkcipher0x key is where releases before it wrote).
        Key(RegistryHive.LocalMachine, InstallKey);
        Key(RegistryHive.LocalMachine, @"Software\darkcipher0x\ServerManagement");
        if (o.Settings)
        {
            Key(RegistryHive.LocalMachine, ProtectedSettings.MachineKey);
            Key(RegistryHive.LocalMachine, PolicySettings.Key);
            foreach (var sid in m.LoadedUserSids()) Key(RegistryHive.Users, sid + "\\" + PolicySettings.Key);
        }
        if (o.AllUsers && (o.Data || o.Cache || o.Library))
            foreach (var home in m.ProfileFolders())
            {
                var p = new Profile(Path.Combine(home, "AppData", "Roaming", AppInfo.Company, "ServerManagement"),
                                    Path.Combine(home, "AppData", "Local", AppInfo.Company, "ServerManagement"),
                                    Path.Combine(home, "AppData", "Local", "Temp"));
                // Hello keys of other users can only be removed in their own session (Windows keeps them per user).
                done.AddRange(CleanUser(p, o, deleteHelloKey: null).Select(x => home + ": " + x));
            }
        return done;
    }

    /// <summary>Where the MSI records the installation (InstallDir and the shortcut choices); the protected records sit next to
    /// it under …\ServerManagement\Protected, so only this subkey is removed when settings are kept.</summary>
    public const string InstallKey = @"Software\DarkCipher Systems\ServerManagement\Install";

    /// <summary>The real registry and profile list.</summary>
    public sealed class RealMachine : IMachine
    {
        public void DeleteKey(RegistryHive hive, string path)
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            root.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
            // Empty parents the app created ("DarkCipher Systems\ServerManagement", "DarkCipher Systems", "darkcipher0x"), innermost first.
            for (var parent = Path.GetDirectoryName(path); parent != null; parent = Path.GetDirectoryName(parent))
            {
                var name = Path.GetFileName(parent);
                bool ours = name.Equals("DarkCipher Systems", StringComparison.OrdinalIgnoreCase) || name.Equals("darkcipher0x", StringComparison.OrdinalIgnoreCase)
                            || name.Equals("ServerManagement", StringComparison.OrdinalIgnoreCase) && parent.Contains(@"\DarkCipher Systems\", StringComparison.OrdinalIgnoreCase);
                if (!ours) break;
                using (var k = root.OpenSubKey(parent))
                    if (k == null || k.SubKeyCount > 0 || k.ValueCount > 0) break;
                root.DeleteSubKey(parent, throwOnMissingSubKey: false);
            }
        }

        public IEnumerable<string> LoadedUserSids() => Registry.Users.GetSubKeyNames().Where(s => s.StartsWith("S-1-5-21-", StringComparison.Ordinal) && !s.EndsWith("_Classes", StringComparison.Ordinal));

        public IEnumerable<string> ProfileFolders()
        {
            using var list = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
            if (list == null) yield break;
            foreach (var sid in list.GetSubKeyNames().Where(s => s.StartsWith("S-1-5-21-", StringComparison.Ordinal)))
            {
                using var k = list.OpenSubKey(sid);
                if (k?.GetValue("ProfileImagePath") is string path && Directory.Exists(Environment.ExpandEnvironmentVariables(path))) yield return Environment.ExpandEnvironmentVariables(path);
            }
        }
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------

    private static string? MovedStore(string home)
    {
        try
        {
            var ptr = Path.Combine(home, "location.json");
            if (File.Exists(ptr) && JsonDocument.Parse(File.ReadAllText(ptr)).RootElement.TryGetProperty("DataDir", out var d)
                && d.GetString() is { Length: > 0 } moved && !moved.Equals(home, StringComparison.OrdinalIgnoreCase) && Directory.Exists(moved))
                return moved;
        }
        catch { }
        return null;
    }

    private static string? HelloKeyName(string store)
    {
        try
        {
            var ring = Path.Combine(store, "keyring.json");
            if (File.Exists(ring) && JsonDocument.Parse(File.ReadAllText(ring)).RootElement.TryGetProperty("Hello", out var h)
                && h.ValueKind == JsonValueKind.Object && h.TryGetProperty("KeyName", out var n) && n.GetString() is { Length: > 0 } name)
                return name;
        }
        catch { }
        return null;
    }

    private static IEnumerable<string> SafeFiles(string dir, string pattern)
    {
        try { return Directory.Exists(dir) ? Directory.GetFiles(dir, pattern) : Array.Empty<string>(); } catch { return Array.Empty<string>(); }
    }

    private static void DeleteFile(string f, List<string> done)
    {
        try { File.Delete(f); done.Add(f); } catch (Exception ex) { done.Add($"{f}: {ex.Message}"); }
    }

    private static void DeleteDir(string dir, List<string> done)
    {
        if (!Directory.Exists(dir)) return;
        try
        {
            Directory.Delete(dir, recursive: true);
            done.Add(dir);
            var parent = Path.GetDirectoryName(dir)!;   // the vendor folder ("DarkCipher Systems", or "DarkCipher" before 0.4.0) when nothing else of ours is left in it
            if (Path.GetFileName(parent) is AppInfo.Company or AppInfo.LegacyVendorFolder && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent);
        }
        catch (Exception ex) { done.Add($"{dir}: {ex.Message}"); }
    }

    private static void DeleteIfEmpty(string dir, List<string> done)
    {
        try { if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) { Directory.Delete(dir); done.Add(dir); } } catch { }
    }
}
