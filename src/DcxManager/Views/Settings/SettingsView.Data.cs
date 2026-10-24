using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using DcxManager.Services;
using DcxManager.Sessions;
using DcxManager.Ssh;
using DcxManager.Vultr;
using Microsoft.Win32;

namespace DcxManager.Views;

// SettingsView: the Data & storage card: location, moving the store, error log, old copies (moved unchanged from SettingsView.cs in 0.2.0 phase 6½).
public sealed partial class SettingsView
{
    // ---- data --------------------------------------------------------------------------------------------------------

    private readonly TextBlock _dataStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
    private readonly TextBlock _syncStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };

    /// <summary>Opens the folder picker for a new store location (used by the start-up sync-folder warning).</summary>
    public void PickNewDataLocation()
    {
        RevealDataCard();
        var dlg = new OpenFolderDialog { Title = "Move the encrypted store to an EMPTY, local (non-synchronised) folder" };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true) MoveStore(dlg.FolderName);
    }

    /// <summary>Opens a card's tab and scrolls the card into view.</summary>
    public void RevealCard(string title)
    {
        if (!_cards.TryGetValue(title, out var card)) return;
        if (CategoryOf(title) == "Integrations") RevealIntegration(IntegrationPageOf(title));   // its own page (0.5.1 #2)
        else RevealCategory(CategoryOf(title));
        Dispatcher.BeginInvoke(() => card.BringIntoView(), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Opens the storage card's tab and scrolls it into view.</summary>
    public void RevealDataCard()
    {
        RevealCategory("Data & backups");
        _refreshData?.Invoke();
        Dispatcher.BeginInvoke(() => _dataCard?.BringIntoView(), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static string Line(StorageInfo i) => i.Kind switch
    {
        StorageKind.Local => "✓ local disk, not synchronised",
        StorageKind.Synced => $"⚠ inside {i.Provider} ({i.Root}) — synchronised to the cloud",
        StorageKind.Network => $"⚠ {i.Provider} ({i.Root}) — data crosses the network",
        _ => "? could not be determined",
    };

    private Border Data()
    {
        var moveBack = Btn("Move back to the default folder", () => MoveStore(SecureStore.HomeDir));
        var removeOld = Btn("Remove old copy…", RemoveOldCopy, "DangerButton");
        var showWarning = Btn("Show the sync warning again", () =>
        {
            _s.SyncWarningDismissedFor.RemoveAll(p => p.Equals(SyncDetector.Normalize(SecureStore.DataDir), StringComparison.OrdinalIgnoreCase));
            Save();
            _refreshData?.Invoke();
            StorageGuard.CheckAtStartup();
        });
        void Refresh()
        {
            SyncDetector.Refresh();
            bool relocated = SecureStore.IsRelocated;
            string prev = _s.PreviousDataDir;
            int oldFiles = prev.Length > 0 && !prev.Equals(SecureStore.DataDir, StringComparison.OrdinalIgnoreCase) ? DataMover.StoreFiles(prev).Count() : 0;
            var store = SyncDetector.Detect(SecureStore.DataDir);
            var cache = SyncDetector.Detect(App.CacheDir);
            var shots = SyncDetector.Detect(Screenshot.Dir);
            var (bytes, files) = StorageGuard.Usage(SecureStore.DataDir);
            var (cBytes, cFiles) = StorageGuard.Usage(App.CacheDir);
            var wrap = SecureStore.PasswordRequiredOnStart ? "master password only (required at start; no Windows copy of the key)"
                : SecureStore.HasPassword ? "Windows DPAPI (this account) + master password" : "Windows DPAPI (this Windows account)";
            bool dismissed = _s.SyncWarningDismissedFor.Any(p => p.Equals(SyncDetector.Normalize(SecureStore.DataDir), StringComparison.OrdinalIgnoreCase));

            _dataStatus.Text =
                $"Encrypted store: {SecureStore.DataDir}" + (relocated ? $"   (moved — default is {SecureStore.HomeDir})" : "") +
                $"\n   Location: {Line(store)}" +
                $"\n   Encryption: AES-256-GCM per file, one random 256-bit data key wrapped by {wrap}" +
                $"\n   Usage: {Fmt.Bytes(bytes)} in {files} file(s)" +
                (oldFiles > 0 ? $"\nOld copy still present: {prev} ({oldFiles} files). Remove it once you're happy with the new location." : "") +
                $"\n\nLogs, browser profile and caches: {App.CacheDir}" +
                $"\n   Location: {Line(cache)} · {Fmt.Bytes(cBytes)} in {cFiles} file(s)" +
                "\n   Error logs are encrypted once the store is unlocked (only a failure before unlocking can be written in clear)." +
                "\n   Browser profile (Vultr portal sign-in): cookies are protected by Windows (DPAPI) by the browser engine." +
                "\n   Temporary files (share sheet, Remote Desktop .rdp files, key generation) are deleted on exit and at the next start." +
                $"\n\nScreenshots (plain PNG, only when you take one): {Screenshot.Dir}\n   Location: {Line(shots)}" +
                (relocated ? "\n\n⚠ Uninstall → \"delete my data\" removes the store files at the moved location too (only this app's files, not the folder)." : "");
            _syncStatus.Text = store.Kind is StorageKind.Synced or StorageKind.Network
                ? string.Format(StorageGuard.SyncWarningText, store.Provider) + $"\nDetected by: {store.Evidence}." + (dismissed ? "\nYou chose to keep it here; the start-up warning is off for this folder." : "")
                : "";
            _syncStatus.Visibility = _syncStatus.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            _syncStatus.SetResourceReference(TextBlock.ForegroundProperty, "Warn");
            moveBack.Visibility = relocated ? Visibility.Visible : Visibility.Collapsed;
            removeOld.Visibility = oldFiles > 0 ? Visibility.Visible : Visibility.Collapsed;
            showWarning.Visibility = dismissed ? Visibility.Visible : Visibility.Collapsed;
        }
        _refreshData = Refresh;
        Refresh();
        return _dataCard = Card("Data & storage", "Everything stays on this PC unless you export it. Nothing is uploaded or synchronised by the app; the checks below warn when Windows or a sync client would copy it elsewhere.",
            _syncStatus,
            _dataStatus,
            Row(Btn("Open data folder", () => Process.Start(new ProcessStartInfo(SecureStore.DataDir) { UseShellExecute = true })),
                Btn("Move data store…", () =>
                {
                    var dlg = new OpenFolderDialog { Title = "Move the encrypted store to an EMPTY folder (a local, non-synchronised one is best)" };
                    if (dlg.ShowDialog(Window.GetWindow(this)) == true) MoveStore(dlg.FolderName);
                }),
                moveBack, removeOld, Btn("Re-check", Refresh), showWarning),
            Row(Btn("View error log", ShowErrorLog),
                Btn("Clear error logs…", async () =>
                {
                    if (Locks.IsLocked("cat:errorlog") && !Locks.IsOpen("cat:errorlog") && !await LockGate.EnsureAsync("cat:errorlog", "clear the error logs")) return;
                    if (!Dialogs.Confirm("Clear error logs", $"Delete every error log in\n{App.LogDir}?", "Delete", danger: true)) return;
                    try { foreach (var f in Directory.EnumerateFiles(App.LogDir, "errors-*.log")) File.Delete(f); } catch (Exception ex) { Dialogs.Error("Clear error logs", ex.Message); }
                    Refresh();
                }),
                Btn("Open logs folder", () => { Directory.CreateDirectory(App.LogDir); Process.Start(new ProcessStartInfo(App.LogDir) { UseShellExecute = true }); })),
            Row(Btn("Sign out of Vultr portal", async () =>
            {
                try
                {
                    var env = await WebHost.EnvironmentAsync();
                    var wv = new Microsoft.Web.WebView2.Wpf.WebView2();
                    await wv.EnsureCoreWebView2Async(env);
                    await wv.CoreWebView2.Profile.ClearBrowsingDataAsync();
                    wv.Dispose();
                    Dialogs.Info("Portal", "Browser cookies and cache cleared.");
                }
                catch (Exception ex) { Dialogs.Error("Portal", ex.Message); }
            })));
    }

    private Action? _refreshData;

    /// <summary>Pre-flight checklist → confirm → copy, verify, switch (DataMover) → log. The old copy stays until removed.</summary>
    private async void MoveStore(string target)
    {
        if (!SecureStore.IsUnlocked) { Dialogs.Error("Move data store", "Unlock the store first."); return; }
        List<DataMover.Check> checks;
        try { checks = DataMover.Preflight(target); }
        catch (Exception ex) { Dialogs.Error("Move data store", ex.Message); return; }
        var list = string.Join("\n", checks.Select(c => (c.Ok ? "✓ " : "✕ ") + c.Text));
        if (checks.Any(c => !c.Ok)) { Dialogs.ShowText("Move data store — pre-flight failed", $"Nothing was changed.\n\nTarget: {target}\n\n{list}", modal: true); return; }
        if (!StorageGuard.ConfirmDestination(target, "Move data store", encrypted: true)) return;
        var from = SecureStore.DataDir;
        if (!Dialogs.Confirm("Move data store",
            $"From: {from}\nTo:   {target}\n\nPre-flight:\n{list}\n\nEvery file is copied, checksummed and decrypted at the new place before the app switches over. " +
            "Open sessions keep running. The old copy stays until you remove it.\n\nMove now?", "Move")) return;
        string log;
        try
        {
            MainWindow.Instance.Status("Moving data store…");
            var progress = new Progress<string>(s => MainWindow.Instance.Status(s));
            log = await Task.Run(() => DataMover.Move(target, progress));
        }
        catch (Exception ex)
        {
            App.Log("Move store", ex);
            Dialogs.ShowText("Move data store — failed (nothing switched)", ex.Message, modal: true);
            MainWindow.Instance.Status("Data store move failed — still using " + SecureStore.DataDir);
            return;
        }
        _s.PreviousDataDir = from;
        try { AppSettings.Save(); } catch (Exception ex) { App.Log("Save after move", ex); }   // lands in the NEW location
        _refreshData?.Invoke();
        MainWindow.Instance.Status("Data store moved → " + SecureStore.DataDir);
        Dialogs.ShowText("Move data store — done", log, modal: true);
    }

    /// <summary>0.5.1 #8: the error log is its own screen under LOGS, so its lock (and Group Policy) applies here too.</summary>
    private static void ShowErrorLog() => MainWindow.Instance?.Navigate("errorlog");

    private void RemoveOldCopy()
    {
        var old = _s.PreviousDataDir;
        if (old.Length == 0) return;
        int n = DataMover.StoreFiles(old).Count();
        if (!Dialogs.Confirm("Remove old copy", $"Delete the {n} store file(s) left in\n{old}\n\nThe active store ({SecureStore.DataDir}) is not touched. Make sure the app has opened fine from the new location first.", "Delete", danger: true, typeToConfirm: "DELETE")) return;
        try
        {
            int removed = DataMover.RemoveOldCopy(old);
            AuditLog.Write("you", "store", "old store copy removed", $"{old} ({removed} files)");
            _s.PreviousDataDir = "";
            Save();
            _refreshData?.Invoke();
        }
        catch (Exception ex) { Dialogs.Error("Remove old copy", ex.Message); }
    }
}
