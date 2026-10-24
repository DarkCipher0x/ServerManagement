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

// SettingsView: the Snapshots card (moved unchanged from SettingsView.cs in 0.2.0 phase 6½).
public sealed partial class SettingsView
{
    // ---- snapshots ---------------------------------------------------------------------------------------------------

    /// <summary>Settings → Snapshots (ADR-009): rolling encrypted local snapshots, restore drill, restore, data format.</summary>
    private Border SnapshotsCard()
    {
        var list = new ListBox { Height = 170, Margin = new Thickness(0, 4, 0, 4) };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        var result = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        var format = new TextBlock { Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        var folder = new TextBox { Text = Snapshots.Dir, Width = 460, IsReadOnly = true, Padding = new Thickness(5, 4, 5, 4) }.Mono();

        void Show(Snapshots.Result r) { result.Text = r.Message; result.SetResourceReference(TextBlock.ForegroundProperty, r.Ok ? "Good" : "Warn"); }
        void Refresh()
        {
            var all = Snapshots.List();
            list.ItemsSource = all.Select(e => new ListBoxItem { Content = $"{e.Created:yyyy-MM-dd HH:mm}   {e.ReasonText,-22}  {e.Bytes / 1024.0,8:N0} KB", Tag = e }.Mono()).ToList();
            var last = all.FirstOrDefault(e => e.Reason == "auto");
            status.Text = (_s.AutoSnapshots ? $"Automatic: every {_s.SnapshotIntervalHours} h, newest {_s.SnapshotKeep} kept. " : "Automatic snapshots are OFF. ") +
                (last != null ? $"Last automatic snapshot {last.Created:yyyy-MM-dd HH:mm}. " : "No automatic snapshot yet. ") + $"{all.Count} snapshots, {all.Sum(e => e.Bytes) / 1048576.0:N1} MB." +
                (Snapshots.LastAutoResult is { Ok: false } f ? "\n⚠ Last automatic attempt failed: " + f.Message : "");
            status.SetResourceReference(TextBlock.ForegroundProperty, Snapshots.LastAutoResult is { Ok: false } || !_s.AutoSnapshots ? "Warn" : "Fg");
            folder.Text = Snapshots.Dir;
            var v = StoreSchema.VersionsOnDisk();
            format.Text = "Data format: " + string.Join(", ", StoreSchema.Kinds.Select(k => $"{k} v{v.GetValueOrDefault(k, 1)}")) +
                $" (this app: up to {string.Join(", ", StoreSchema.Kinds.Select(k => $"v{StoreSchema.Latest(k)}").Distinct())}). Upgrades run at start, only after a verified snapshot, and are verified again afterwards; a failed upgrade puts your data back.";
        }
        Snapshots.Entry? Selected() => (list.SelectedItem as ListBoxItem)?.Tag as Snapshots.Entry;

        var now = Btn("Snapshot now", async () => { result.Text = "writing…"; var r = await Task.Run(() => Snapshots.Create("manual")); Show(r); Refresh(); }, "PrimaryButton");
        var verify = Btn("Verify (restore drill)", async () =>
        {
            if (Selected() is not { } e) { result.Text = "Select a snapshot first."; return; }
            result.Text = "verifying…";
            Show(await Task.Run(() => Snapshots.Verify(e.Path)));
        });
        var restore = Btn("Restore…", () =>
        {
            if (Selected() is not { } e) { result.Text = "Select a snapshot first."; return; }
            var v = Snapshots.Verify(e.Path);
            if (!v.Ok) { Show(v); return; }
            Snapshots.Plan plan;
            try { plan = Snapshots.PlanRestore(Snapshots.Open(e.Path)); } catch (Exception ex) { result.Text = ex.Message; return; }
            if (plan.Replace.Count + plan.AddBack.Count + plan.Remove.Count == 0) { result.Text = "Nothing to restore: your data is identical to this snapshot."; return; }
            if (!Dialogs.Confirm("Restore snapshot",
                $"Put your data back to the snapshot of {e.Created:yyyy-MM-dd HH:mm} ({e.ReasonText})?\n\n{Snapshots.Describe(plan)}\n\n" +
                "A verified snapshot of your current data is taken first, so this can be undone. Open sessions end and the app restarts.", "Restore and restart", danger: true)) return;
            var r = Snapshots.Restore(e.Path);
            Show(r); Refresh();
            if (!r.Ok) return;
            Dialogs.Info("Restore snapshot", r.Message + "\n\nThe app will now restart to load the restored data.");
            App.RestartRequested = true;
            if (MainWindow.Instance is { } mw) { mw.ExitWithoutPrompt = true; mw.Close(); }
        }, "DangerButton");
        var delete = Btn("Delete…", () =>
        {
            if (Selected() is not { } e) { result.Text = "Select a snapshot first."; return; }
            if (!Dialogs.Confirm("Delete snapshot", $"Delete the snapshot of {e.Created:yyyy-MM-dd HH:mm} ({e.ReasonText})? This can't be undone.", "Delete", danger: true)) return;
            try { File.Delete(e.Path); AuditLog.Write("you", "store", "snapshot deleted", e.Name); } catch (Exception ex) { result.Text = ex.Message; }
            Refresh();
        });

        var keep = Bound(new ComboBox { Width = 90, ItemsSource = new[] { 3, 5, 10, 20, 30, 60 }, SelectedItem = new[] { 3, 5, 10, 20, 30, 60 }.Contains(_s.SnapshotKeep) ? _s.SnapshotKeep : 10 }, nameof(AppSettings.SnapshotKeep));
        keep.SelectionChanged += (_, _) => { if (keep.SelectedItem is int n) { _s.SnapshotKeep = n; Save(); Snapshots.Prune(); Refresh(); } };
        var every = Bound(new ComboBox { Width = 140 }, nameof(AppSettings.SnapshotIntervalHours));
        foreach (var (h, t) in new[] { (6, "every 6 hours"), (12, "every 12 hours"), (24, "daily"), (72, "every 3 days"), (168, "weekly") })
            every.Items.Add(new ComboBoxItem { Content = t, Tag = h, IsSelected = h == _s.SnapshotIntervalHours });
        if (every.SelectedItem == null) every.SelectedIndex = 2;
        every.SelectionChanged += (_, _) => { if (every.SelectedItem is ComboBoxItem { Tag: int h }) { _s.SnapshotIntervalHours = h; Save(); Refresh(); } };
        var auto = Check("Take automatic snapshots (local disk only; nothing leaves this PC)", _s.AutoSnapshots, v => { _s.AutoSnapshots = v; Refresh(); });

        var pick = Btn("Choose folder…", () =>
        {
            var dlg = new OpenFolderDialog { Title = "Folder for snapshots (ideally another drive)" };
            if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
            var d = dlg.FolderName;
            if (d.StartsWith(SecureStore.DataDir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) && !d.Equals(Snapshots.DefaultDir(SecureStore.DataDir), StringComparison.OrdinalIgnoreCase))
            { Dialogs.Error("Snapshots", "Pick a folder outside the store (or use the default)."); return; }
            if (SyncDetector.Detect(d) is { Kind: StorageKind.Synced } sync &&
                !Dialogs.Confirm("Snapshots", $"That folder is {sync.Summary} ({sync.Evidence}). Snapshots are encrypted, but they would leave this PC. Use it anyway?", "Use it")) return;
            _s.SnapshotDir = d; Save();
            AuditLog.Write("you", "store", "snapshot folder changed", d);
            Refresh();
        });
        var reset = Btn("Use default (inside the store)", () => { _s.SnapshotDir = ""; Save(); Refresh(); });

        Refresh();
        list.IsVisibleChanged += (_, _) => { if (list.IsVisible) Refresh(); };   // automatic snapshots happen while Settings stays open
        return Card("Snapshots", "Rolling encrypted copies of your whole store, taken automatically and before every restore, import, data upgrade and app update. " +
                "They use the store's own key, so they're as private as the store and only work with it. For another PC or a lost password, keep a password-protected export too (Security → Encrypted backup).",
            Row(Labeled("KEEP (automatic)", keep), Labeled("HOW OFTEN", every)), auto, status,
            list, Row(now, verify, restore, delete), result,
            L("FOLDER"), Row(folder, pick, reset),
            new TextBlock
            {
                Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap,
                Text = "The default folder sits on the same disk as your data and moves with it. Another drive protects against a failed disk. Damaged files found at start are offered back from the newest snapshot that has a good copy; the damaged file is always kept.",
            },
            format);
    }
}
