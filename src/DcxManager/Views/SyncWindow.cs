using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using DcxManager.Files;
using DcxManager.Services;
using DcxManager.Ssh;

namespace DcxManager.Views;

/// <summary>
/// Commander → Compare / sync folders (ADR-014): compare the two panes' folders, pick a mode, review every action
/// (each with a checkbox), then run it through the transfer queue. Deletions only in Mirror with the box ticked,
/// confirmed with their count, and executed after the copies succeeded.
/// </summary>
public sealed class SyncWindow
{
    private readonly FsRef _lRef, _rRef;
    private readonly string _lDir, _rDir;
    private List<DiffEntry> _diffs = new();
    private List<SyncStep> _steps = new();

    public SyncWindow(FsRef leftRef, string leftDir, FsRef rightRef, string rightDir) { _lRef = leftRef; _lDir = leftDir; _rRef = rightRef; _rDir = rightDir; }

    public void Show()
    {
        var w = Dialogs.MakeWindow("Compare / sync folders", 1100, 720);
        var root = new DockPanel { Margin = new Thickness(14) };
        var head = new StackPanel();
        head.Children.Add(new TextBlock { Text = $"LEFT   {_lRef.Title}:{_lDir}\nRIGHT  {_rRef.Title}:{_rDir}", Margin = new Thickness(0, 0, 0, 8) }.Mono());
        var by = new ComboBox { Width = 230, ItemsSource = new[] { "size + modified time", "SHA-256 (reads every file)" }, SelectedIndex = 0, Margin = new Thickness(0, 0, 8, 0) };
        var mode = new ComboBox { Width = 200, ItemsSource = new[] { "Update →  (left to right)", "Update ←  (right to left)", "Mirror →  (make right = left)", "Mirror ←  (make left = right)", "Two-way (newer wins)" }, SelectedIndex = 0, Margin = new Thickness(0, 0, 8, 0) };
        var del = new CheckBox { Content = "Delete files that exist only on the target (Mirror)", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        var compare = new Button { Content = "Compare", Style = (Style)Application.Current.Resources["PrimaryButton"] };
        var row = new WrapPanel();
        row.Children.Add(by); row.Children.Add(mode); row.Children.Add(del); row.Children.Add(compare);
        head.Children.Add(row);
        var status = new TextBlock { Margin = new Thickness(0, 6, 0, 6), TextWrapping = TextWrapping.Wrap };
        head.Children.Add(status);
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        var totals = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        var run = new Button { Content = "Run selected…", Style = (Style)Application.Current.Resources["PrimaryButton"], IsEnabled = false, Margin = new Thickness(8, 0, 0, 0) };
        var close = new Button { Content = "Close", IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        var foot = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var fb = new StackPanel { Orientation = Orientation.Horizontal };
        fb.Children.Add(run); fb.Children.Add(close);
        DockPanel.SetDock(fb, Dock.Right);
        foot.Children.Add(fb); foot.Children.Add(totals);
        DockPanel.SetDock(foot, Dock.Bottom);
        root.Children.Add(foot);

        var list = new ListView();
        var gv = new GridView();
        var cb = new FrameworkElementFactory(typeof(CheckBox));
        cb.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new Binding(nameof(SyncStep.Selected)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        cb.AddHandler(System.Windows.Controls.Primitives.ToggleButton.ClickEvent, new RoutedEventHandler((_, _) => totals.Text = FolderSync.Totals(_steps)));
        gv.Columns.Add(new GridViewColumn { Header = "", Width = 34, CellTemplate = new DataTemplate { VisualTree = cb } });
        gv.Columns.Add(new GridViewColumn { Header = "Action", Width = 120, DisplayMemberBinding = new Binding(nameof(SyncStep.ActionText)) });
        gv.Columns.Add(new GridViewColumn { Header = "Path", Width = 380, DisplayMemberBinding = new Binding("Entry.Rel") });
        gv.Columns.Add(new GridViewColumn { Header = "Left", Width = 170, DisplayMemberBinding = new Binding("Entry.Left") { Converter = new ItemText() } });
        gv.Columns.Add(new GridViewColumn { Header = "Right", Width = 170, DisplayMemberBinding = new Binding("Entry.Right") { Converter = new ItemText() } });
        gv.Columns.Add(new GridViewColumn { Header = "Why", Width = 260, DisplayMemberBinding = new Binding(nameof(SyncStep.Why)) });
        list.View = gv;
        root.Children.Add(list);
        w.Content = root;

        (SyncMode Mode, bool LeftToRight) Mode() => mode.SelectedIndex switch
        {
            0 => (SyncMode.Update, true), 1 => (SyncMode.Update, false), 2 => (SyncMode.Mirror, true), 3 => (SyncMode.Mirror, false), _ => (SyncMode.TwoWay, true),
        };
        void Replan()
        {
            var (m, ltr) = Mode();
            del.IsEnabled = m == SyncMode.Mirror;
            _steps = FolderSync.Plan(_diffs, m, ltr, del.IsChecked == true && m == SyncMode.Mirror);
            list.ItemsSource = _steps;
            totals.Text = _diffs.Count == 0 ? "" : FolderSync.Totals(_steps);
            run.IsEnabled = _steps.Any(s => s.Action != SyncAction.Conflict);
        }
        mode.SelectionChanged += (_, _) => Replan();
        del.Click += (_, _) => Replan();
        compare.Click += async (_, _) =>
        {
            compare.IsEnabled = false; run.IsEnabled = false;
            using var l = _lRef.Open();
            using var r = _rRef.Equals(_lRef) && _lRef.Kind == "local" ? null : _rRef.Open();
            try
            {
                var prog = new Progress<string>(s => status.Text = "comparing " + s);
                _diffs = await FolderSync.CompareAsync(l, _lDir, r ?? l, _rDir, by.SelectedIndex == 1, prog);
                int same = _diffs.Count(d => d.State == DiffState.Same);
                status.Text = $"{_diffs.Count} entries compared: {same} identical, {_diffs.Count - same} different or on one side only.";
                Replan();
            }
            catch (Exception ex) { status.Text = "✕ " + ex.Message; App.Log("Folder compare", ex); }
            finally { compare.IsEnabled = true; }
        };
        run.Click += async (_, _) =>
        {
            var sel = _steps.Where(s => s.Selected && s.Action != SyncAction.Conflict).ToList();
            if (sel.Count == 0) return;
            int dels = sel.Count(s => s.Action is SyncAction.DeleteLeft or SyncAction.DeleteRight);
            var where = sel.Any(s => s.Action == SyncAction.DeleteRight) ? _rRef : _lRef;
            var text = FolderSync.Totals(_steps) + (dels > 0
                ? $"\n\n⚠ {dels} item(s) will be DELETED on {where.Title} after all copies succeed — " + (where.Kind == "local" ? "to the Recycle Bin." : "permanently (a server has no Recycle Bin).")
                : "\n\nNothing will be deleted.");
            // The sides that change decide (ADR-017): a PRODUCTION side wins, then any labelled one.
            var touched = new[]
            {
                sel.Any(s => s.Action is SyncAction.CopyRight or SyncAction.DeleteRight) ? _rRef : null,
                sel.Any(s => s.Action is SyncAction.CopyLeft or SyncAction.DeleteLeft) ? _lRef : null,
            }.Where(r => r is { Kind: not "local" } && r.Host != null).Select(r => r!.Host!).ToList();
            var target = touched.FirstOrDefault(h => Envs.IsProduction(Envs.For(h))) ?? touched.FirstOrDefault(h => Envs.For(h).Length > 0);
            if (!Envs.Confirm(target, "Run sync", text, dels > 0 ? $"Copy and delete {dels}" : "Run", danger: dels > 0, typeToConfirm: dels > 0 ? dels.ToString() : null)) return;
            try
            {
                var jobs = await FolderSync.BuildJobsAsync(sel, _lRef, _lDir, _rRef, _rDir);
                foreach (var j in jobs) TransferQueue.Enqueue(j);
                AuditLog.Write("you", "files", "folder sync started", $"{_lRef.Title}:{_lDir} ⇄ {_rRef.Title}:{_rDir} · {FolderSync.Totals(_steps)}");
                MainWindow.Instance?.Status($"Sync queued: {jobs.Count} transfer job(s) — see Transfers.");
                w.Close();
            }
            catch (Exception ex) { status.Text = "✕ " + ex.Message; }
        };
        w.Show();
    }

    private sealed class ItemText : IValueConverter
    {
        public object Convert(object? v, Type t, object? p, System.Globalization.CultureInfo c) =>
            v is FsItem i ? (i.IsDir ? "folder" : Fmt.Bytes(i.Length)) + " · " + i.Modified.ToString("yyyy-MM-dd HH:mm") : "—";
        public object ConvertBack(object? v, Type t, object? p, System.Globalization.CultureInfo c) => throw new NotSupportedException();
    }
}
