using System.Windows;
using System.Windows.Controls;
using DcxManager.Files;
using DcxManager.Services;

namespace DcxManager.Views;

// SettingsView: Folder sync (0.6.26, ADR-102) — the defaults new sync jobs start from, whether jobs run by themselves,
// and the admin-protected permanent deletion.
public sealed partial class SettingsView
{
    private Border FolderSyncCard()
    {
        var deletion = Bound(new ComboBox
        {
            Width = 300, ItemsSource = new[] { "Recycle Bin (versions on a server)", "Versioning (a versions folder)", "Permanent (only if allowed below)" },
            SelectedIndex = Enum.TryParse<DeletionMode>(_s.FileSyncDeletion, true, out var d) ? (int)d : 0,
            ToolTip = "Where new jobs send the files a sync deletes or overwrites. Each job can change it.",
        }, nameof(AppSettings.FileSyncDeletion));
        deletion.SelectionChanged += (_, _) => { _s.FileSyncDeletion = ((DeletionMode)Math.Max(0, deletion.SelectedIndex)).ToString(); Save(); };
        int[] keepChoices = { 0, 7, 14, 30, 60, 90, 180, 365 };
        var keep = Bound(new ComboBox
        {
            Width = 160, ItemsSource = keepChoices.Select(k => k == 0 ? "forever" : $"{k} days").ToList(),
            SelectedIndex = Math.Max(0, Array.IndexOf(keepChoices, _s.FileSyncVersionsKeepDays)), ToolTip = "How long new jobs keep versions of deleted and overwritten files.",
        }, nameof(AppSettings.FileSyncVersionsKeepDays));
        if (!keepChoices.Contains(_s.FileSyncVersionsKeepDays)) keep.SelectedIndex = Array.IndexOf(keepChoices, 30);
        keep.SelectionChanged += (_, _) => { _s.FileSyncVersionsKeepDays = keepChoices[Math.Max(0, keep.SelectedIndex)]; Save(); };
        int[] guardChoices = { 10, 25, 50, 75, 100 };
        var guard = Bound(new ComboBox
        {
            Width = 160, ItemsSource = guardChoices.Select(g => g == 100 ? "no limit" : $"{g}%").ToList(),
            SelectedIndex = Math.Max(0, Array.IndexOf(guardChoices, _s.FileSyncMaxDeletePercent)),
            ToolTip = "A run without a preview (schedule, real-time, Run now) stops when it would delete more than this share of one side's files (and more than 10). An unplugged drive must never wipe the other side.",
        }, nameof(AppSettings.FileSyncMaxDeletePercent));
        if (!guardChoices.Contains(_s.FileSyncMaxDeletePercent)) guard.SelectedIndex = Array.IndexOf(guardChoices, 50);
        guard.SelectionChanged += (_, _) => { _s.FileSyncMaxDeletePercent = guardChoices[Math.Max(0, guard.SelectedIndex)]; Save(); };
        var jobs = SyncJobStore.All;
        int scheduled = jobs.Count(j => j.Schedule != SyncSchedule.Off);
        return Card("Folder sync",
            "File Commander → Sync: compare and synchronise folders on this PC and your servers, FreeFileSync-style — Two-way (remembers the last sync), Mirror, Update or Custom, with filters, versioning and saved jobs. " +
            $"{jobs.Count} saved job(s), {scheduled} with a schedule. These are the defaults a new job starts from; every job can change them.",
            Check("Let sync jobs run on their schedule or in real time while the app is open (off: only when you run them)", _s.FileSyncScheduled, v => _s.FileSyncScheduled = v),
            Row(Labeled("DELETED AND OVERWRITTEN FILES GO TO", deletion), Labeled("KEEP VERSIONS", keep), Labeled("UNATTENDED RUNS STOP ABOVE", guard)),
            Check("Allow permanent deletion on servers (a server has no Recycle Bin; otherwise \"Permanent\" keeps versions) — needs admin approval", _s.FileSyncAllowPermanentDelete, v => _s.FileSyncAllowPermanentDelete = v),
            Row(Btn("⇄ Sync jobs…", () => SyncJobsWindow.Show(null)), Btn("Help", () => MainWindow.Instance.ShowHelp("Folder sync"))));
    }
}
