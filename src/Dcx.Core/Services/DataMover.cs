using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DcxManager.Services;

/// <summary>
/// Moves the encrypted store (keyring, data, settings, reports, audit logs) to another folder or drive, safely:
/// pre-flight checks → copy → SHA-256 compare of every file → decrypt every encrypted file/line at the new place with
/// the in-memory key → switch (atomic pointer file in the standard folder) → old copy is kept until the user removes it.
/// Any failure before the switch leaves everything exactly as it was.
/// </summary>
public static class DataMover
{
    public sealed record Check(bool Ok, string Text);

    public static IEnumerable<string> StoreFiles(string dir) =>
        Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir).Where(f => Path.GetFileName(f) is var n && (n == "keyring.json" || n.EndsWith(".dcx") || n.EndsWith(".dcxlog") || n == "workspace.json"))
            : Enumerable.Empty<string>();

    /// <summary>Snapshots kept inside the store folder (the default place) — they move with the store.</summary>
    public static IEnumerable<string> SnapshotFiles(string dir) => TravelFiles(dir, Travel[0]);

    /// <summary>Encrypted session logs (ADR-019) — they move with the store too.</summary>
    public static IEnumerable<string> SessionLogFiles(string dir) => TravelFiles(dir, Travel[1]);

    /// <summary>Sub-folders of the store whose sealed files travel with it (copied + SHA-256 checked, removed with the old copy).</summary>
    private static readonly (Func<string, string> Dir, string Pattern, string What)[] Travel =
    {
        (Snapshots.DefaultDir, "snap-*" + Snapshots.Ext, "snapshots"),
        (d => Path.Combine(d, "sessionlogs"), "slog-*" + Sessions.SessionLogFiles.Ext, "session logs"),
    };

    private static IEnumerable<string> TravelFiles(string dir, (Func<string, string> Dir, string Pattern, string What) t)
    {
        var d = t.Dir(dir);
        return Directory.Exists(d) ? Directory.EnumerateFiles(d, t.Pattern) : Enumerable.Empty<string>();
    }

    private static IEnumerable<string> AllTravelFiles(string dir) => Travel.SelectMany(t => TravelFiles(dir, t));

    /// <summary>Files that may sit in a store folder without it counting as "in use": the pointer, the crash marker, our probe.</summary>
    private static bool Ignorable(string name) => name is "location.json" or "running.marker" or ".dcx-write-test";

    /// <summary>Pre-flight: everything that would make a move unsafe, before anything is written.</summary>
    public static List<Check> Preflight(string target)
    {
        var src = SecureStore.DataDir;
        var list = new List<Check>();
        void C(bool ok, string text) => list.Add(new Check(ok, text));
        string full;
        try { full = Path.GetFullPath(target); } catch (Exception ex) { C(false, "Invalid path: " + ex.Message); return list; }
        C(Path.IsPathRooted(full), "Absolute path");
        C(!full.Equals(src, StringComparison.OrdinalIgnoreCase), "Different from the current location");
        C(!full.StartsWith(src.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) && !src.StartsWith(full.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase), "Not inside (or containing) the current store");
        // location.json lives in the standard folder and running.marker (crash detection) in any store folder, so moving
        // back is allowed once the old copy is gone.
        bool existed = Directory.Exists(full);
        bool empty = !existed || !Directory.EnumerateFileSystemEntries(full).Any(e => !Ignorable(Path.GetFileName(e)));
        C(empty, empty ? "Target folder is empty or new" : "Target folder must be empty (pick or create an empty folder" + (full.Equals(SecureStore.HomeDir, StringComparison.OrdinalIgnoreCase) ? "; remove the old copy first)" : ")"));
        long need = StoreFiles(src).Concat(AllTravelFiles(src)).Sum(f => new FileInfo(f).Length);
        try
        {
            var root = Path.GetPathRoot(full)!;
            var drive = new DriveInfo(root);
            C(drive.IsReady, $"Drive {root} is ready ({drive.DriveType})");
            if (drive.IsReady) C(drive.AvailableFreeSpace > need * 3 + (50L << 20), $"Free space {drive.AvailableFreeSpace / (1 << 20):N0} MB (needs ~{Math.Max(1, need * 3 / (1 << 20)):N0} MB)");
            if (drive.DriveType is DriveType.Network) C(true, "⚠ Network location: the app needs it reachable at every start; offline = the app won't open your data");
            if (drive.DriveType is DriveType.Removable) C(true, "⚠ Removable drive: unplugged = the app won't open your data");
        }
        catch (Exception ex) { C(full.StartsWith(@"\\"), full.StartsWith(@"\\") ? "⚠ UNC path: must be reachable at every start" : "Drive check failed: " + ex.Message); }
        if (list.All(c => c.Ok))
        {
            // Only probe a target that passed the structural checks, and leave no folder behind if we had to create one.
            try
            {
                Directory.CreateDirectory(full);
                var probe = Path.Combine(full, ".dcx-write-test");
                File.WriteAllBytes(probe, RandomNumberGenerator.GetBytes(64));
                File.Delete(probe);
                C(true, "Writable");
            }
            catch (Exception ex) { C(false, "Not writable: " + ex.Message); }
            finally { if (!existed) { try { if (Directory.Exists(full) && !Directory.EnumerateFileSystemEntries(full).Any()) Directory.Delete(full); } catch { } } }
        }
        C(SecureStore.IsUnlocked, "Store is unlocked (needed to verify the copy)");
        return list;
    }

    /// <summary>Copies + verifies + switches. Returns a log; throws (with nothing switched) on any problem.</summary>
    public static string Move(string target, IProgress<string>? progress = null)
    {
        var src = SecureStore.DataDir;
        var dst = Path.GetFullPath(target);
        var log = new StringBuilder();
        void P(string s) { log.AppendLine(s); progress?.Report(s); }
        var bad = Preflight(dst).Where(c => !c.Ok).ToList();
        if (bad.Count > 0) throw new InvalidOperationException("Pre-flight failed:\n" + string.Join("\n", bad.Select(b => "✕ " + b.Text)));

        var files = StoreFiles(src).ToList();
        Directory.CreateDirectory(dst);
        try
        {
            foreach (var f in files)
            {
                var to = Path.Combine(dst, Path.GetFileName(f));
                File.Copy(f, to, overwrite: false);
                var a = SHA256.HashData(File.ReadAllBytes(f));
                var b = SHA256.HashData(File.ReadAllBytes(to));
                if (!CryptographicOperations.FixedTimeEquals(a, b)) throw new IOException($"Checksum mismatch after copying {Path.GetFileName(f)}");
                P($"✓ copied + SHA-256 verified  {Path.GetFileName(f)}  ({new FileInfo(f).Length:N0} bytes)");
            }
            // Decrypt everything at the new location with the in-memory key (proves the copy is usable, not just identical).
            foreach (var to in StoreFiles(dst))
            {
                var name = Path.GetFileName(to);
                if (name.EndsWith(".dcx")) { SecureStore.Decrypt(File.ReadAllBytes(to), name); P($"✓ decrypts  {name}"); }
                else if (name.EndsWith(".dcxlog"))
                {
                    int n = 0;
                    foreach (var line in File.ReadLines(to).Where(l => l.Length > 0)) { SecureStore.DecryptString(line.Trim(), "audit"); n++; }
                    P($"✓ decrypts  {name}  ({n} entries)");
                }
                else if (name == "keyring.json" && !SecureStore.KeyMatchesRing(to)) throw new IOException("keyring.json copy is unreadable");
            }
            if (StoreFiles(dst).Count() != files.Count) throw new IOException("File count differs after copy");
            // Snapshots and session logs in the store folder travel with it (SHA-256 checked; each is sealed, the key verifies on use).
            foreach (var t in Travel)
            {
                var list = TravelFiles(src, t).ToList();
                if (list.Count == 0) continue;
                var sd = t.Dir(dst);
                Directory.CreateDirectory(sd);
                foreach (var f in list)
                {
                    var to = Path.Combine(sd, Path.GetFileName(f));
                    File.Copy(f, to, overwrite: false);
                    if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(File.ReadAllBytes(f)), SHA256.HashData(File.ReadAllBytes(to)))) throw new IOException($"Checksum mismatch after copying {t.What}: {Path.GetFileName(f)}");
                }
                P($"✓ copied + SHA-256 verified  {list.Count} {t.What}");
            }
        }
        catch
        {
            // Roll back: remove only what we copied; the original store is untouched.
            foreach (var f in files) { try { File.Delete(Path.Combine(dst, Path.GetFileName(f))); } catch { } }
            foreach (var t in Travel)
            {
                foreach (var f in TravelFiles(src, t)) { try { File.Delete(Path.Combine(t.Dir(dst), Path.GetFileName(f))); } catch { } }
                try { var sd = t.Dir(dst); if (Directory.Exists(sd) && !Directory.EnumerateFileSystemEntries(sd).Any()) Directory.Delete(sd); } catch { }
            }
            throw;
        }

        SecureStore.SwitchDataDir(dst);
        P($"✓ switched — the app now uses {dst}");
        AuditLog.Write("you", "store", "data store moved", $"{src} → {dst}");
        P($"The previous copy is still in {src} (except location.json). Remove it from Settings → Data once you're happy.");
        return log.ToString();
    }

    /// <summary>Deletes the old store files after a move (never the pointer file, never the active store).</summary>
    public static int RemoveOldCopy(string oldDir)
    {
        if (oldDir.Equals(SecureStore.DataDir, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("That is the active store.");
        int n = 0;
        foreach (var f in StoreFiles(oldDir)) { File.Delete(f); n++; }
        // The snapshots and session logs there were copied (and verified) with the move.
        foreach (var t in Travel)
        {
            foreach (var f in TravelFiles(oldDir, t)) { File.Delete(f); n++; }
            var sd = t.Dir(oldDir);
            if (Directory.Exists(sd) && !Directory.EnumerateFileSystemEntries(sd).Any()) Directory.Delete(sd);
        }
        return n;
    }
}
