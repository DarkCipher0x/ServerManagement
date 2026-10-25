using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using DcxManager.Files;
using DcxManager.Services;

namespace DcxManager.Views;

/// <summary>
/// File Commander → Sync jobs (0.6.26, ADR-102): the saved folder sync jobs with their schedule and last result. Open
/// one to compare with a preview, run it now without one (the same rules as a scheduled run), duplicate or remove it.
/// </summary>
public sealed class SyncJobsWindow
{
    private static SyncJobsWindow? _open;
    private Window? _window;
    internal ListView List { get; } = new() { SelectionMode = SelectionMode.Single };
    internal TextBlock Status { get; } = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly (FsRef Ref, string Path)? _left, _right;

    private SyncJobsWindow((FsRef, string)? left, (FsRef, string)? right) { _left = left; _right = right; }

    /// <param name="panes">The File Commander's two folders, for "New from the panes" (null outside the Commander).</param>
    public static void Show(((FsRef Ref, string Path) Left, (FsRef Ref, string Path) Right)? panes)
    {
        if (TestMode.Running) { Dialogs.RefusedInSelfTest("window: sync jobs"); return; }
        if (_open?._window is { IsLoaded: true } w) { w.Activate(); return; }
        var v = new SyncJobsWindow(panes?.Left, panes?.Right);
        var win = Dialogs.MakeWindow("Sync jobs — File Commander", 1000, 560);
        win.Content = v.Build();
        void Changed() => win.Dispatcher.BeginInvoke(v.Fill);
        SyncJobStore.Changed += Changed;
        win.Closed += (_, _) => { SyncJobStore.Changed -= Changed; _open = null; };
        v._window = win; _open = v;
        win.Show();
    }

    private sealed record Row(SyncJobDef Job)
    {
        public string Name => Job.Name;
        public string Pairs => Job.Pairs.Count == 1 ? Job.Pairs[0].Text : $"{Job.Pairs.Count} folder pairs";
        public string Settings => Job.Options.Describe();
        public string Schedule => Job.ScheduleText + (SyncScheduler.IsRunning(Job.Id) ? " · running now" : "");
        public string Last => Job.LastRunUtc is { } t ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "never";
        public string Result => Job.LastResult;
    }

    internal FrameworkElement Build()
    {
        var root = new DockPanel { Margin = new Thickness(14) };
        var intro = new TextBlock
        {
            Text = "Saved folder sync jobs. Open one to compare with a preview; Run now runs it without one, like its schedule does (conflicts left alone, the deletion guard on). " +
                   (AppSettings.Current.FileSyncScheduled ? "" : "⚠ Scheduled and real-time runs are switched off in Settings → Folder sync."),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        };
        DockPanel.SetDock(intro, Dock.Top);
        root.Children.Add(intro);

        var bar = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        Button B(string text, Action a, string tip, string? style = null)
        {
            var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0), ToolTip = tip };
            if (style != null) b.Style = (Style)Application.Current.Resources[style];
            b.Click += (_, _) => a();
            bar.Children.Add(b);
            return b;
        }
        B("Open…", Open, "Compare with a preview, change its settings and schedule", "PrimaryButton");
        B("▶ Run now", RunNow, "Run it without a preview, as its schedule would");
        if (_left is { } l && _right is { } r && l.Path.Length > 0 && r.Path.Length > 0)
            B("+ New from the panes…", () => SyncWindow.FromPanes(l.Ref, l.Path, r.Ref, r.Path).Show(), "A new job for the two folders open in File Commander");
        B("+ New…", () => new SyncWindow(new SyncJobDef { Options = SyncOptions.Defaults(), Pairs = { new SyncPairDef() } }, saved: false).Show(), "A new, empty job");
        B("Duplicate", Duplicate, "A copy of the selected job (without its schedule)");
        B("Remove…", Remove, "Removes the job (no files are touched)", "DangerButton");
        bar.Children.Add(Status);
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar);

        var gv = new GridView();
        gv.Columns.Add(new GridViewColumn { Header = "Job", Width = 180, DisplayMemberBinding = new Binding(nameof(Row.Name)) });
        gv.Columns.Add(new GridViewColumn { Header = "Folders", Width = 280, DisplayMemberBinding = new Binding(nameof(Row.Pairs)) });
        gv.Columns.Add(new GridViewColumn { Header = "Settings", Width = 130, DisplayMemberBinding = new Binding(nameof(Row.Settings)) });
        gv.Columns.Add(new GridViewColumn { Header = "Runs", Width = 160, DisplayMemberBinding = new Binding(nameof(Row.Schedule)) });
        gv.Columns.Add(new GridViewColumn { Header = "Last run", Width = 120, DisplayMemberBinding = new Binding(nameof(Row.Last)) });
        gv.Columns.Add(new GridViewColumn { Header = "Result", Width = 300, DisplayMemberBinding = new Binding(nameof(Row.Result)) });
        List.View = gv;
        List.MouseDoubleClick += (_, e) => { if (e.OriginalSource is FrameworkElement { DataContext: Row }) Open(); };
        List.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Open(); } };
        Menus.SelectOnRightClick(List);
        Menus.Attach(List, () => Selected == null ? Array.Empty<object?>() : new object?[]
        {
            Menus.Item("Open…", Open), Menus.Item("▶ Run now", RunNow), Menus.Item("Duplicate", Duplicate), Menus.Sep(),
            Menus.Item("Forget the last sync (Two-way starts afresh)", ForgetDb), Menus.Sep(), Menus.Item("Remove…", Remove),
        });
        root.Children.Add(List);
        Fill();
        return root;
    }

    internal void Fill()
    {
        var keep = Selected?.Id;
        var rows = SyncJobStore.All.OrderBy(j => j.Name, StringComparer.CurrentCultureIgnoreCase).Select(j => new Row(j)).ToList();
        List.ItemsSource = rows;
        List.SelectedItem = rows.FirstOrDefault(r => r.Job.Id == keep) ?? rows.FirstOrDefault();
        if (rows.Count == 0) Status.Text = "No saved jobs yet: open Compare / sync folders in File Commander and use Save as job.";
    }

    private SyncJobDef? Selected => (List.SelectedItem as Row)?.Job;

    private void Open() { if (Selected is { } j) new SyncWindow(j, saved: true).Show(); }

    private void RunNow()
    {
        if (Selected is not { } j) return;
        if (Profiles.ReadOnly) { Status.Text = "This profile is read-only (viewer): jobs don't run."; return; }
        if (!Dialogs.Confirm("Run now", $"Run “{j.Name}” now, without a preview?\n\n{j.Pairs.Count} folder pair(s) · {j.Options.Describe()}\nConflicts are left alone; a pair stops if a folder is missing or it would delete more than {j.Options.MaxDeletePercent}% of a side's files.\nDeleted and overwritten files go where the job says ({j.Options.Deletion}).", "Run now")) return;
        Status.Text = SyncScheduler.Fire(j.Id, "Run now") ? $"“{j.Name}” is running — progress in File Commander → Transfers; the result appears here." : $"“{j.Name}” is already running.";
        Fill();
    }

    private void Duplicate()
    {
        if (Selected is not { } j) return;
        var copy = j.Clone();
        copy.Id = Guid.NewGuid().ToString("N"); copy.Name = j.Name + " (copy)"; copy.Schedule = SyncSchedule.Off; copy.LastRunUtc = null; copy.LastResult = "";
        SyncJobStore.Save(copy);
        AuditLog.Write("you", "files", "sync job duplicated", $"{j.Name} → {copy.Name}");
        Status.Text = $"Duplicated as “{copy.Name}” (manual until you give it a schedule).";
    }

    private void ForgetDb()
    {
        if (Selected is not { } j) return;
        if (!Dialogs.Confirm("Forget the last sync", $"Forget what “{j.Name}” remembers about its last sync?\n\nThe next Two-way run then can't tell deletions from new files: every difference is copied or shown as a conflict. No files are touched.", "Forget")) return;
        foreach (var p in j.Pairs) SyncDatabase.Forget(SyncDatabase.KeyOf(p.LeftRef, p.LeftPath, p.RightRef, p.RightPath));
        AuditLog.Write("you", "files", "sync job: last-sync state forgotten", j.Name);
        Status.Text = "Forgotten.";
    }

    private void Remove()
    {
        if (Selected is not { } j) return;
        if (!Dialogs.Confirm("Remove sync job", $"Remove the job “{j.Name}”?\n\nIts schedule stops. No files are touched, and versions it kept stay where they are.", "Remove", danger: true)) return;
        SyncJobStore.Remove(j.Id);
        AuditLog.Write("you", "files", "sync job removed", j.Name);
        Status.Text = $"Removed “{j.Name}”.";
    }
}
