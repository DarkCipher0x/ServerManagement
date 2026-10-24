using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace DcxManager.Services;

/// <summary>What a folder is: a plain local disk, inside a cloud-synchronised folder, or on a network share.</summary>
public enum StorageKind { Local, Synced, Network, Unknown }

/// <param name="Provider">"OneDrive (Personal)", "Dropbox", "Network share"… — empty for local.</param>
/// <param name="Root">The sync root / share that contains the path.</param>
/// <param name="Evidence">How it was detected (shown in Settings, so the user can judge it).</param>
public sealed record StorageInfo(string Path, StorageKind Kind, string Provider, string Root, string Evidence)
{
    public bool IsLocal => Kind == StorageKind.Local;
    public string Summary => Kind switch
    {
        StorageKind.Local => "local disk (not synchronised)",
        StorageKind.Synced => $"inside {Provider} — synchronised to the cloud",
        StorageKind.Network => $"on a network location ({Provider})",
        _ => "unknown",
    };
}

/// <summary>
/// Detects whether a folder lives inside OneDrive or another cloud-sync provider, or on a network share.
/// It never just looks for "OneDrive" in the path. Sources, strongest first:
///   1. The Windows Cloud Files sync-root registry (SyncRootManager) — every modern provider registers its roots there
///      (OneDrive personal/business/SharePoint libraries, Dropbox, Google Drive, iCloud, Box, Nextcloud…).
///   2. OneDrive's own account registry (UserFolder + synced SharePoint mount points) and the OneDrive* environment variables.
///   3. Dropbox's info.json (older Dropbox clients don't use Cloud Files).
///   4. Cloud Files placeholder attributes (pinned / unpinned / recall-on-access) on the folder or a parent.
///   5. UNC paths and mapped network drives (reported as Network, not Synced).
/// Junctions and symlinks are resolved first, so a local-looking path that points into OneDrive is still caught.
/// New providers can be added to <see cref="KnownRoots"/> without touching the callers.
/// </summary>
public static class SyncDetector
{
    public sealed record Root(string Path, string Provider, string Evidence);

    private static List<Root>? _roots;
    private static DateTime _rootsAt;

    /// <summary>All sync roots this user has, cached for a minute (registry reads are cheap but not free).</summary>
    public static IReadOnlyList<Root> KnownRoots()
    {
        if (_roots != null && DateTime.UtcNow - _rootsAt < TimeSpan.FromMinutes(1)) return _roots;
        var list = new List<Root>();
        void Add(string? p, string provider, string evidence)
        {
            if (string.IsNullOrWhiteSpace(p)) return;
            try { p = Normalize(Environment.ExpandEnvironmentVariables(p)); } catch { return; }
            if (p.Length < 3 || list.Any(r => r.Path.Equals(p, StringComparison.OrdinalIgnoreCase))) return;
            list.Add(new Root(p, provider, evidence));
        }

        // 1. Cloud Files sync roots: HKLM\...\Explorer\SyncRootManager\<Provider>!<SID>!<Account>\UserSyncRoots\<SID> = path
        try
        {
            var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? "";
            using var mgr = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager");
            foreach (var name in mgr?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                using var k = mgr!.OpenSubKey(name);
                using var roots = k?.OpenSubKey("UserSyncRoots");
                if (roots == null) continue;
                var provider = ProviderName(name, k?.GetValue("DisplayNameResource") as string);
                foreach (var v in roots.GetValueNames())
                    if (sid.Length == 0 || v.Equals(sid, StringComparison.OrdinalIgnoreCase))
                        Add(roots.GetValue(v) as string, provider, "Windows sync-root registry (SyncRootManager\\" + name.Split('!')[0] + ")");
            }
        }
        catch { }

        // 2. OneDrive accounts and synced SharePoint/Teams libraries
        try
        {
            using var acc = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\OneDrive\Accounts");
            foreach (var name in acc?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                using var k = acc!.OpenSubKey(name);
                var label = name.Equals("Personal", StringComparison.OrdinalIgnoreCase) ? "OneDrive (Personal)" : $"OneDrive ({k?.GetValue("DisplayName") as string ?? name})";
                Add(k?.GetValue("UserFolder") as string, label, $"OneDrive account registry ({name}\\UserFolder)");
                using var mounts = k?.OpenSubKey("ScopeIdToMountPointPathCache");
                foreach (var v in mounts?.GetValueNames() ?? Array.Empty<string>())
                    Add(mounts!.GetValue(v) as string, label + " · SharePoint library", "OneDrive synced library (ScopeIdToMountPointPathCache)");
            }
        }
        catch { }
        Add(Environment.GetEnvironmentVariable("OneDriveConsumer"), "OneDrive (Personal)", "%OneDriveConsumer%");
        Add(Environment.GetEnvironmentVariable("OneDriveCommercial"), "OneDrive (work or school)", "%OneDriveCommercial%");
        Add(Environment.GetEnvironmentVariable("OneDrive"), "OneDrive", "%OneDrive%");

        // 3. Dropbox (info.json lists personal and business folders)
        foreach (var baseDir in new[] { Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData })
        {
            try
            {
                var info = System.IO.Path.Combine(Environment.GetFolderPath(baseDir), "Dropbox", "info.json");
                if (!File.Exists(info)) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(info));
                foreach (var p in doc.RootElement.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.Object && p.Value.TryGetProperty("path", out var path))
                        Add(path.GetString(), "Dropbox" + (p.Name == "business" ? " (business)" : ""), "Dropbox info.json");
            }
            catch { }
        }

        _roots = list;
        _rootsAt = DateTime.UtcNow;
        return list;
    }

    public static void Refresh() => _roots = null;

    private static string ProviderName(string key, string? display)
    {
        var id = key.Split('!')[0];
        if (id.StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase))
            return key.Contains("!Personal", StringComparison.OrdinalIgnoreCase) ? "OneDrive (Personal)" : key.Contains("!Business", StringComparison.OrdinalIgnoreCase) ? "OneDrive (work or school)" : "OneDrive";
        if (id.Contains("Dropbox", StringComparison.OrdinalIgnoreCase)) return "Dropbox";
        if (id.Contains("Google", StringComparison.OrdinalIgnoreCase) || id.Contains("DriveFS", StringComparison.OrdinalIgnoreCase)) return "Google Drive";
        if (id.Contains("iCloud", StringComparison.OrdinalIgnoreCase)) return "iCloud Drive";
        if (id.StartsWith("Box", StringComparison.OrdinalIgnoreCase)) return "Box";
        if (id.Contains("Nextcloud", StringComparison.OrdinalIgnoreCase)) return "Nextcloud";
        if (id.Contains("pCloud", StringComparison.OrdinalIgnoreCase)) return "pCloud";
        if (id.Contains("MEGA", StringComparison.OrdinalIgnoreCase)) return "MEGA";
        return display is { Length: > 0 } && !display.StartsWith('@') ? display : id;   // "@C:\…dll,-123" resource refs aren't readable
    }

    /// <summary>Classifies <paramref name="path"/> (need not exist yet — the nearest existing parent is checked).</summary>
    public static StorageInfo Detect(string path) => Detect(path, KnownRoots());

    /// <summary>Testable core: same as <see cref="Detect(string)"/> against an explicit root list.</summary>
    public static StorageInfo Detect(string path, IReadOnlyList<Root> roots)
    {
        string full;
        try { full = Normalize(path); } catch { return new StorageInfo(path, StorageKind.Unknown, "", "", "path could not be resolved"); }

        // Resolve junctions/symlinks on the nearest existing ancestor (e.g. a Documents junction into OneDrive).
        var resolved = Resolve(full);
        foreach (var candidate in resolved.Equals(full, StringComparison.OrdinalIgnoreCase) ? new[] { full } : new[] { full, resolved })
            foreach (var r in roots.OrderByDescending(r => r.Path.Length))
                if (Under(candidate, r.Path))
                    return new StorageInfo(path, StorageKind.Synced, r.Provider, r.Path, r.Evidence + (candidate != full ? $" — via link to {resolved}" : ""));

        if (CloudAttributes(resolved) is { } attr)
            return new StorageInfo(path, StorageKind.Synced, "a cloud-files provider", attr.Folder, $"Cloud Files placeholder attribute {attr.What} on {attr.Folder}");

        if (resolved.StartsWith(@"\\", StringComparison.Ordinal))
            return new StorageInfo(path, StorageKind.Network, "network share", ShareRoot(resolved), "UNC path");
        try
        {
            var root = System.IO.Path.GetPathRoot(resolved);
            if (root is { Length: > 0 })
            {
                var d = new DriveInfo(root);
                if (d.DriveType == DriveType.Network) return new StorageInfo(path, StorageKind.Network, "mapped network drive " + root.TrimEnd('\\'), root, "drive type Network");
                if (d.IsReady && d.VolumeLabel.Contains("Google Drive", StringComparison.OrdinalIgnoreCase))
                    return new StorageInfo(path, StorageKind.Synced, "Google Drive", root, "volume label " + d.VolumeLabel);
            }
        }
        catch { }
        return new StorageInfo(path, StorageKind.Local, "", "", "no sync root, cloud attribute or network drive found");
    }

    public static string Normalize(string p)
    {
        var f = System.IO.Path.GetFullPath(p.Trim().Trim('"'));
        return f.Length > 3 ? f.TrimEnd('\\', '/') : f;
    }

    private static bool Under(string path, string root) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    private static string ShareRoot(string unc)
    {
        var parts = unc.TrimStart('\\').Split('\\');
        return parts.Length >= 2 ? $@"\\{parts[0]}\{parts[1]}" : unc;
    }

    private static string NearestExisting(string p)
    {
        var cur = p;
        while (!string.IsNullOrEmpty(cur) && !Directory.Exists(cur) && !File.Exists(cur)) cur = System.IO.Path.GetDirectoryName(cur);
        return cur ?? p;
    }

    // FILE_ATTRIBUTE_RECALL_ON_OPEN 0x40000, PINNED 0x80000, UNPINNED 0x100000, RECALL_ON_DATA_ACCESS 0x400000
    private static (string Folder, string What)? CloudAttributes(string p)
    {
        var cur = NearestExisting(p);
        for (int depth = 0; depth < 32 && !string.IsNullOrEmpty(cur); depth++)
        {
            try
            {
                var a = (int)File.GetAttributes(cur);
                if ((a & 0x80000) != 0) return (cur, "PINNED");
                if ((a & 0x100000) != 0) return (cur, "UNPINNED");
                if ((a & 0x400000) != 0) return (cur, "RECALL_ON_DATA_ACCESS");
                if ((a & 0x40000) != 0) return (cur, "RECALL_ON_OPEN");
            }
            catch { }
            cur = System.IO.Path.GetDirectoryName(cur);
        }
        return null;
    }

    /// <summary>The final path of the nearest existing ancestor (junctions and symlinks followed) + the missing tail.</summary>
    private static string Resolve(string full)
    {
        var existing = NearestExisting(full);
        if (string.IsNullOrEmpty(existing) || existing.StartsWith(@"\\", StringComparison.Ordinal)) return full;
        try
        {
            using var h = CreateFileW(existing, 0, 7, IntPtr.Zero, 3, 0x02000000 /* BACKUP_SEMANTICS: opens folders */, IntPtr.Zero);
            if (h.IsInvalid) return full;
            var buf = new char[1024];
            var n = GetFinalPathNameByHandleW(h, buf, (uint)buf.Length, 0);
            if (n == 0 || n >= buf.Length) return full;
            var fin = new string(buf, 0, (int)n);
            if (fin.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) fin = @"\\" + fin[8..];
            else if (fin.StartsWith(@"\\?\", StringComparison.Ordinal)) fin = fin[4..];
            var tail = full.Length > existing.Length ? full[existing.Length..] : "";
            return Normalize(fin + tail);
        }
        catch { return full; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle h, [Out] char[] buf, uint size, uint flags);
}
