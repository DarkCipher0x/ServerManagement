using System.Security.Cryptography;
using System.Text;
using DcxManager.Services;

namespace DcxManager.Files;

/// <summary>What both sides held after the last sync, for one path.</summary>
public sealed record SyncDbEntry(bool IsDir, long LeftBytes, long LeftTicks, long RightBytes, long RightTicks);

/// <summary>
/// The last-sync state of one folder pair (0.6.26, ADR-102), like FreeFileSync's sync.ffs_db but kept in the app's
/// encrypted store (syncdb-&lt;hash&gt;.dcx) instead of a file in the synced folders. Two-way uses it to tell "deleted on
/// one side" from "new on the other", and "changed on one side" from "changed on both". The key is the pair itself
/// (both places and folders), so a saved job and an ad-hoc compare of the same folders share it.
/// </summary>
public static class SyncDatabase
{
    public static string KeyOf(FsRef left, string leftDir, FsRef right, string rightDir) =>
        $"{left}|{leftDir.Replace('\\', '/').TrimEnd('/')}|{right}|{rightDir.Replace('\\', '/').TrimEnd('/')}";

    private static string FileOf(string key) =>
        "syncdb-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..20].ToLowerInvariant() + ".dcx";

    public static Dictionary<string, SyncDbEntry> Load(string key)
    {
        try { return SecureStore.Read<Dictionary<string, SyncDbEntry>>(FileOf(key)); }
        catch (Exception ex) { Log.Write("Sync database", ex); return new(); }
    }

    public static void Save(string key, Dictionary<string, SyncDbEntry> db)
    {
        if (SecureStore.IsUnlocked) SecureStore.Write(FileOf(key), db);
    }

    /// <summary>Forgets a pair's last sync (the next Two-way run treats every difference as new).</summary>
    public static void Forget(string key)
    {
        try { System.IO.File.Delete(System.IO.Path.Combine(SecureStore.DataDir, FileOf(key))); } catch (System.IO.IOException) { }
    }

    public static bool Exists(string key) => System.IO.File.Exists(System.IO.Path.Combine(SecureStore.DataDir, FileOf(key)));

    public static SyncDbEntry Of(FsItem l, FsItem r) => new(l.IsDir, l.Length, l.Modified.Ticks, r.Length, r.Modified.Ticks);

    /// <summary>The new state after a run: every path that is now the same on both sides, plus the old record of every
    /// path the run left alone (skipped or a conflict), so a skipped deletion isn't later mistaken for a new file.</summary>
    public static Dictionary<string, SyncDbEntry> After(IEnumerable<DiffEntry> now, Dictionary<string, SyncDbEntry> old, Func<string, bool> untouched)
    {
        var db = new Dictionary<string, SyncDbEntry>(StringComparer.Ordinal);
        foreach (var (rel, e) in old) if (untouched(rel)) db[rel] = e;
        foreach (var d in now)
            if (d.State == DiffState.Same && d.Left != null && d.Right != null) db[d.Rel] = Of(d.Left, d.Right);
        return db;
    }
}
