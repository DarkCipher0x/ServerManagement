using System.IO;

namespace DcxManager.Services;

/// <summary>
/// Local-first storage policy in one place: the start-up "your data is in a synchronised folder" warning (permanently
/// dismissible per location), warnings before writing exports/backups/screenshots into a synced or network folder, and
/// the storage facts Settings → Data shows. It only ever warns — the user decides where their data goes.
/// </summary>
public static class StorageGuard
{
    public const string SyncWarningText =
        "Your application data appears to be stored inside a synchronised folder ({0}). This may cause the encrypted " +
        "database, settings, logs, credential metadata and other application data to be uploaded or replicated by the " +
        "synchronisation provider. Everything stays encrypted, but copies leave this PC and sync conflicts can corrupt a " +
        "store that is being written.\n\nFor maximum privacy and reliability, move the data to a non-synchronised local folder.";

    /// <summary>True when the startup warning should show for the current store location.</summary>
    public static bool ShouldWarn(out StorageInfo info)
    {
        info = SyncDetector.Detect(SecureStore.DataDir);
        if (info.Kind == StorageKind.Local || info.Kind == StorageKind.Unknown) return false;
        var here = SyncDetector.Normalize(SecureStore.DataDir);
        return !AppSettings.Current.SyncWarningDismissedFor.Any(p => p.Equals(here, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>What "Change data location…" and "Review storage" open in Settings: registered by the app (the store
    /// code below the screens doesn't know them); nothing more than opening Settings by default.</summary>
    public static Action OpenDataLocation { get; set; } = () => { };
    public static Action OpenDataCard { get; set; } = () => { };

    /// <summary>Start-up check (after the main window is up). Never blocks start; the user can move, review, continue or dismiss.</summary>
    public static void CheckAtStartup()
    {
        StorageInfo info;
        try { if (!ShouldWarn(out info)) return; }
        catch (Exception ex) { Log.Write("Sync check", ex); return; }
        var where = info.Kind == StorageKind.Network ? info.Provider : info.Provider + " — " + info.Root;
        var msg = string.Format(SyncWarningText, where) + $"\n\nData folder: {SecureStore.DataDir}\nDetected by: {info.Evidence}";
        var pick = Ui.Dialogs.Choose("Data is in a synchronised folder", msg,
            new[] { "Change data location…", "Review storage", "Continue anyway", "Don't show this again" }, cancelIndex: 2, width: 640);
        switch (pick)
        {
            case 0:
                Ui.Window.Navigate("settings");
                OpenDataLocation();
                break;
            case 1:
                Ui.Window.Navigate("settings");
                OpenDataCard();
                break;
            case 3:
                AppSettings.Current.SyncWarningDismissedFor.Add(SyncDetector.Normalize(SecureStore.DataDir));
                try { AppSettings.Save(); } catch (Exception ex) { Log.Write("Save dismissal", ex); }
                AuditLog.Write("you", "store", "sync-folder warning dismissed", SecureStore.DataDir);
                break;
        }
    }

    /// <summary>
    /// Before writing a file the user asked for (backup, export, screenshot folder) into <paramref name="path"/>: if that's a
    /// synced or network folder, say so and let them go ahead. Returns false only when they choose not to.
    /// </summary>
    public static bool ConfirmDestination(string path, string what, bool encrypted)
    {
        StorageInfo info;
        try { info = SyncDetector.Detect(path); } catch { return true; }
        if (info.Kind is StorageKind.Local or StorageKind.Unknown) return true;
        var how = encrypted ? "The file is encrypted, but a copy will leave this PC." : "The file is NOT encrypted, so its contents will be readable wherever it is synchronised.";
        return Ui.Dialogs.Confirm($"{what} — synchronised folder",
            $"{path}\n\nThis folder is {info.Summary}.\n{how}\n\nDetected by: {info.Evidence}\n\nUse this location anyway?", "Use it anyway");
    }

    /// <summary>Default for plain-file output (screenshots, the Claude Code workspace): the user's folder unless it is
    /// synchronised (OneDrive "Known Folder Move" redirects Pictures/Documents), then a local per-user app folder.</summary>
    public static string LocalDefault(Environment.SpecialFolder preferred, string sub, string localSub)
    {
        var p = Path.Combine(Environment.GetFolderPath(preferred), sub);
        try { if (SyncDetector.Detect(p).Kind is StorageKind.Local) return p; } catch { }
        return Path.Combine(AppInfo.CacheDir, localSub);
    }

    /// <summary>Total size and file count of a folder (best effort; skips what it can't read).</summary>
    public static (long Bytes, int Files) Usage(string dir)
    {
        long bytes = 0; int files = 0;
        try
        {
            if (!Directory.Exists(dir)) return (0, 0);
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
            { bytes += f.Length; files++; }
        }
        catch { }
        return (bytes, files);
    }
}
