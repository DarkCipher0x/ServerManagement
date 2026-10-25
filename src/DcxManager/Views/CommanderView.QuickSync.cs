using DcxManager.Files;
using DcxManager.Services;

namespace DcxManager.Views;

// CommanderView: the "⇄ Sync ▾" button (0.6.26, ADR-102) — the two panes' folders synced in one step with one
// confirmation and no window, for the everyday "push my changes up" / "pull the server's copy down"; the full sync window
// and the saved jobs are one click away. Same engine, default settings (Settings → Folder sync), versioning and
// confirmation as the sync window.
public sealed partial class CommanderView
{
    internal IEnumerable<object?> SyncMenu()
    {
        bool ready = _left.Path.Length > 0 && _right.Path.Length > 0;
        yield return Menus.Item("⇄ Compare / sync folders…", CompareFolders, "Ctrl+X D", tip: "See every difference first, choose what happens to each, then sync");
        yield return Menus.Sep();
        yield return Menus.Item("Update →   copy new and changed files to the right", () => _ = QuickSyncAsync(SyncVariant.Update, true), enabled: ready,
            tip: "One step: compares, shows what will be copied, asks once. Never deletes anything.");
        yield return Menus.Item("←  Update   copy new and changed files to the left", () => _ = QuickSyncAsync(SyncVariant.Update, false), enabled: ready,
            tip: "One step: compares, shows what will be copied, asks once. Never deletes anything.");
        yield return Menus.Item("Mirror →   make the right a copy of the left", () => _ = QuickSyncAsync(SyncVariant.Mirror, true), enabled: ready,
            tip: "Copies and overwrites, and removes what's only on the right (to the Recycle Bin or a versions folder). Asks first.");
        yield return Menus.Item("←  Mirror   make the left a copy of the right", () => _ = QuickSyncAsync(SyncVariant.Mirror, false), enabled: ready,
            tip: "Copies and overwrites, and removes what's only on the left (to the Recycle Bin or a versions folder). Asks first.");
        yield return Menus.Item("⇄  Two-way   both sides get each other's changes", () => _ = QuickSyncAsync(SyncVariant.TwoWay, true), enabled: ready,
            tip: "Changes and deletions go both ways; files changed on both sides are left alone (open Compare / sync to decide).");
        var jobs = SyncJobStore.All.OrderBy(j => j.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (jobs.Count > 0)
        {
            yield return Menus.Sep();
            foreach (var j in jobs.Take(12))
                yield return Menus.Item("▶ " + j.Name, () => new SyncWindow(j, saved: true).Show(), tip: "Opens the job and compares it; Synchronise runs it");
        }
        yield return Menus.Sep();
        yield return Menus.Item("Sync jobs…", OpenSyncJobs, tip: "Saved jobs: schedules, real-time sync, run now");
        yield return Menus.Item("Folder sync help", () => MainWindow.Instance.ShowHelp("Folder sync"));
    }

    /// <summary>Compares the two panes' folders with the default settings and the chosen variant, asks once (what will be
    /// copied and deleted, and where deleted files go), then runs it through the transfer queue.</summary>
    internal async Task QuickSyncAsync(SyncVariant variant, bool leftToRight)
    {
        if (_left.Path.Length == 0 || _right.Path.Length == 0) { Dialogs.Info("Sync", "Open a folder in both panes first."); return; }
        if (_left.Ref.Equals(_right.Ref) && SamePath(_left.Path, _right.Path)) { Dialogs.Info("Sync", "Both panes show the same folder."); return; }
        if (Profiles.ReadOnly) { Dialogs.Info("Sync", "This profile is read-only (viewer): it can compare but not synchronise."); return; }
        var opt = SyncOptions.Defaults();
        opt.Variant = variant;
        opt.LeftToRight = leftToRight;
        var name = variant switch
        {
            SyncVariant.Update => leftToRight ? "Update →" : "← Update",
            SyncVariant.Mirror => leftToRight ? "Mirror →" : "← Mirror",
            _ => "Two-way",
        };
        var job = new SyncJobDef
        {
            Name = name,
            Pairs = { new SyncPairDef { Left = _left.Ref.ToString(), LeftPath = _left.Path, Right = _right.Ref.ToString(), RightPath = _right.Path } },
            Options = opt,
        };
        var main = MainWindow.Instance;
        main.Status($"{name}: comparing {_left.Path} and {_right.Path}…");
        List<SyncPairRun> runs;
        try { runs = await SyncRunner.CompareAsync(job); }
        catch (Exception ex) { Dialogs.Error("Sync", ex.Message, ex: ex); return; }
        var run = runs[0];
        if (run.Error != null) { Dialogs.Info("Sync", "Couldn't compare the folders: " + run.Error); main.Status(""); return; }
        int conflicts = run.Steps.Count(s => s.Action == SyncAction.Conflict);
        if (!run.Steps.Any(s => s.Runs))
        {
            main.Status(conflicts == 0 ? $"{name}: already in sync — nothing to do." : $"{name}: nothing to copy; {conflicts} conflict(s) to decide in Compare / sync folders.");
            return;
        }
        if (!SyncWindow.ConfirmRun(runs, "Sync — " + name)) { main.Status(""); return; }
        var result = await SyncRunner.ExecuteAsync(runs, null, "you");
        main.Status((result.Ok ? "✓ " : "✕ ") + name + ": " + result.Text.Split('\n')[0] + (conflicts > 0 ? $" · {conflicts} conflict(s) left alone" : ""));
        _ = _left.RefreshAsync(); _ = _right.RefreshAsync();
    }
}
