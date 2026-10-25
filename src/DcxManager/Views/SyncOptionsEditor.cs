using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using DcxManager.Files;
using DcxManager.Services;

namespace DcxManager.Views;

/// <summary>
/// Folder sync options (0.6.26, ADR-102), FreeFileSync's settings in five tabs: Compare, Synchronise, Filter, Deletion &amp;
/// versions, and (for a whole job) Schedule &amp; safety. Every control writes straight into the options it was built for;
/// <c>changed</c> runs after each change (the window then knows the preview is stale).
/// </summary>
public static class SyncOptionsEditor
{
    private static Style St(string key) => (Style)Application.Current.Resources[key];
    private static TextBlock L(string t) => new() { Text = t, Style = St("Label"), Margin = new Thickness(0, 10, 0, 4) };
    private static TextBlock Hint(string t) => new TextBlock { Text = t, Style = St("Hint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) }.Fs(FontStep.Caption);
    private static StackPanel Labeled(string label, FrameworkElement e)
    {
        var p = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
        e.HorizontalAlignment = HorizontalAlignment.Left;   // a narrow box sits under the start of its label, not its middle
        p.Children.Add(L(label)); p.Children.Add(e);
        return p;
    }
    private static WrapPanel Row(params UIElement[] items) { var w = new WrapPanel(); foreach (var i in items) w.Children.Add(i); return w; }

    private static ComboBox Combo(string[] items, int index, Action<int> set, double width = 260, string? tip = null)
    {
        var c = new ComboBox { ItemsSource = items, SelectedIndex = Math.Clamp(index, 0, items.Length - 1), Width = width, ToolTip = tip };
        c.SelectionChanged += (_, _) => { if (c.SelectedIndex >= 0) set(c.SelectedIndex); };
        return c;
    }

    private static TextBox Number(int value, Action<int> set, int min, int max, double width = 90, string? tip = null)
    {
        var t = new TextBox { Text = value.ToString(CultureInfo.InvariantCulture), Width = width, ToolTip = tip };
        t.TextChanged += (_, _) => { if (int.TryParse(t.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= min && n <= max) set(n); };
        t.LostFocus += (_, _) => { if (!int.TryParse(t.Text.Trim(), out var n) || n < min || n > max) t.Text = Math.Clamp(n, min, max).ToString(CultureInfo.InvariantCulture); };
        return t;
    }

    private static CheckBox Switch(string text, bool value, Action<bool> set)
    {
        var c = new CheckBox { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, IsChecked = value, Style = St("Switch"), Margin = new Thickness(0, 10, 0, 0) };
        c.Checked += (_, _) => set(true); c.Unchecked += (_, _) => set(false);
        return c;
    }

    private static TextBox Lines(string text, Action<string> set, string tip)
    {
        var t = new TextBox { Text = text.Replace("\r\n", "\n").Replace("\n", Environment.NewLine), AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, Height = 110, Width = 300, ToolTip = tip, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }.Mono();
        t.TextChanged += (_, _) => set(t.Text.Replace("\r\n", "\n"));
        return t;
    }

    /// <summary>"10 MB", "1.5 GB", "500" (bytes) → bytes; empty or 0 → 0; null when it can't be read.</summary>
    public static long? ParseSize(string text)
    {
        var t = text.Trim().ToUpperInvariant().Replace(" ", "");
        if (t.Length == 0) return 0;
        long mult = 1;
        foreach (var (suffix, m) in new[] { ("TB", 1L << 40), ("GB", 1L << 30), ("MB", 1L << 20), ("KB", 1L << 10), ("B", 1L) })
            if (t.EndsWith(suffix, StringComparison.Ordinal)) { t = t[..^suffix.Length]; mult = m; break; }
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 0 && v * mult < long.MaxValue ? (long)(v * mult) : null;
    }

    public static string SizeText(long bytes) => bytes <= 0 ? "" : bytes % (1L << 30) == 0 ? $"{bytes >> 30} GB" : bytes % (1L << 20) == 0 ? $"{bytes >> 20} MB" : bytes % (1L << 10) == 0 ? $"{bytes >> 10} KB" : bytes.ToString(CultureInfo.InvariantCulture);

    private static TextBox Size(long value, Action<long> set, string tip)
    {
        var t = new TextBox { Text = SizeText(value), Width = 110, ToolTip = tip + " Like 10 MB, 1.5 GB or a number of bytes; empty = no limit." };
        t.TextChanged += (_, _) => { if (ParseSize(t.Text) is { } b) { set(b); t.ClearValue(Control.BorderBrushProperty); } else t.SetResourceReference(Control.BorderBrushProperty, "Danger"); };
        return t;
    }

    private static readonly SyncAction[] CustomActions = { SyncAction.CopyRight, SyncAction.CopyLeft, SyncAction.DeleteLeft, SyncAction.DeleteRight, SyncAction.None, SyncAction.Conflict };
    private static readonly string[] CustomActionText = { "copy →", "← copy", "delete left", "delete right", "do nothing", "conflict (decide yourself)" };

    /// <param name="o">The options being edited (a job's or one pair's).</param>
    /// <param name="job">The job, for the Schedule tab (null: a pair's own settings have no schedule).</param>
    /// <param name="rebuild">Rebuilds the editor after a change made in code (Default excludes).</param>
    public static TabControl Build(SyncOptions o, SyncJobDef? job, Action changed, Action rebuild)
    {
        var tabs = new TabControl { Margin = new Thickness(0, 6, 0, 0) };
        void Tab(string header, params UIElement[] content)
        {
            var body = new StackPanel { Margin = new Thickness(10, 4, 10, 10) };
            foreach (var c in content) body.Children.Add(c);
            tabs.Items.Add(new TabItem { Header = header, Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 330 } });
        }

        // ---- Compare ----
        Tab("Compare",
            Row(Labeled("COMPARE BY", Combo(new[] { "Modified time and size", "File content (SHA-256, reads every file)", "File size only" }, (int)o.Compare, i => { o.Compare = (CompareBy)i; changed(); }, 300,
                    "Time and size is fast and what FreeFileSync does by default. Content reads both copies of every file of the same size. Size only ignores times.")),
                Labeled("TIME TOLERANCE (SECONDS)", Number(o.ToleranceSeconds, v => { o.ToleranceSeconds = v; changed(); }, 0, 3600, tip: "FAT drives and some NAS shares keep times to 2 seconds.")),
                Labeled("IGNORE HOUR SHIFTS", new TextBox { Text = o.IgnoreHourShifts, Width = 90, ToolTip = "Times exactly this many hours apart count as equal: 1 for daylight saving on FAT drives, or 1,2. Empty = none." }
                    .Also(t => t.TextChanged += (_, _) => { o.IgnoreHourShifts = t.Text.Trim(); changed(); }))),
            Hint("Symbolic links to folders aren't followed (no loops, no surprises). Our own unfinished “.dcxpart” files and the versions folder are never compared."));

        // ---- Synchronise ----
        var direction = Combo(new[] { "left → right", "right → left" }, o.LeftToRight ? 0 : 1, i => { o.LeftToRight = i == 0; changed(); }, 160);
        var mirrorDeletes = Switch("Mirror deletes what isn't in the source (off: Mirror only adds and overwrites)", o.MirrorDeletes, v => { o.MirrorDeletes = v; changed(); });
        var conflicts = Combo(new[] { "Skip and show them (decide yourself)", "The newer one wins", "The left one wins", "The right one wins" }, (int)o.Conflicts, i => { o.Conflicts = (ConflictPolicy)i; changed(); }, 300);
        var custom = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        custom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        custom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var kinds = new[] { (DiffState.OnlyLeft, "Only on the left"), (DiffState.OnlyRight, "Only on the right"), (DiffState.LeftNewer, "Left is newer"), (DiffState.RightNewer, "Right is newer"), (DiffState.Differs, "Different, same time"), (DiffState.TypeConflict, "A file on one side, a folder on the other") };
        foreach (var (state, text) in kinds)
        {
            custom.RowDefinitions.Add(new RowDefinition());
            var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 8, 3) };
            int cur = Array.IndexOf(CustomActions, o.Custom.TryGetValue(state, out var a) ? a : SyncAction.None);
            var pick = Combo(CustomActionText, cur < 0 ? 4 : cur, i => { o.Custom[state] = CustomActions[i]; changed(); }, 220);
            pick.Margin = new Thickness(0, 3, 0, 3);
            Grid.SetRow(label, custom.RowDefinitions.Count - 1); Grid.SetRow(pick, custom.RowDefinitions.Count - 1); Grid.SetColumn(pick, 1);
            custom.Children.Add(label); custom.Children.Add(pick);
        }
        var variantHelp = Hint("");
        var dirBox = Labeled("DIRECTION", direction);
        var conflictBox = Labeled("WHEN BOTH SIDES CHANGED", conflicts);
        void Show()
        {
            var v = o.Variant;
            dirBox.Visibility = v is SyncVariant.Mirror or SyncVariant.Update ? Visibility.Visible : Visibility.Collapsed;
            mirrorDeletes.Visibility = v == SyncVariant.Mirror ? Visibility.Visible : Visibility.Collapsed;
            conflictBox.Visibility = v == SyncVariant.TwoWay ? Visibility.Visible : Visibility.Collapsed;
            custom.Visibility = v == SyncVariant.Custom ? Visibility.Visible : Visibility.Collapsed;
            variantHelp.Text = v switch
            {
                SyncVariant.TwoWay => "Changes on either side go to the other, deletions too. The app remembers each pair's last sync (encrypted, in its own store), so it can tell a deleted file from a new one and spots files changed on both sides. The first run has nothing to remember: files only on one side are copied, and files that differ are conflicts unless you pick a winner.",
                SyncVariant.Mirror => "Makes the target an exact copy of the source: new and changed files are copied, newer files on the target are overwritten, and (if switched on) anything not in the source is deleted.",
                SyncVariant.Update => "Copies new and newer files to the target. Never deletes, and never overwrites a newer file on the target (that's shown as a conflict).",
                _ => "Pick the action for each kind of difference yourself.",
            };
        }
        var variant = Combo(new[] { "Two-way", "Mirror", "Update", "Custom" }, (int)o.Variant, i => { o.Variant = (SyncVariant)i; Show(); changed(); }, 160);
        Show();
        Tab("Synchronise", Row(Labeled("VARIANT", variant), dirBox, conflictBox), variantHelp, mirrorDeletes, custom);

        // ---- Filter ----
        var f = o.Filter;
        Tab("Filter",
            Row(Labeled("INCLUDE", Lines(f.Include, t => { f.Include = t; changed(); }, "One pattern per line: * matches anything, ? one character. /x anchors at the sync folder; a trailing / means a folder and everything in it.")),
                Labeled("EXCLUDE", Lines(f.Exclude, t => { f.Exclude = t; changed(); }, "Excluded folders aren't even listed. Example: *.tmp, node_modules/, /build/"))),
            Row(Labeled("SMALLEST FILE", Size(f.MinBytes, b => { f.MinBytes = b; changed(); }, "Files smaller than this are left out.")),
                Labeled("LARGEST FILE", Size(f.MaxBytes, b => { f.MaxBytes = b; changed(); }, "Files larger than this are left out.")),
                Labeled("CHANGED IN THE LAST (DAYS)", Number(f.MaxAgeDays, v => { f.MaxAgeDays = v; changed(); }, 0, 36500, tip: "0 = any age."))),
            Hint("Patterns ignore case. A file passes the size and age limits when either side's copy does. Excluded items are never copied or deleted, and a folder holding excluded items is never deleted whole."),
            new Button { Content = "Default excludes", Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, ToolTip = SyncFilterDef.DefaultExclude.Replace("\n", "  ") }
                .Also(b => b.Click += (_, _) => { f.Exclude = SyncFilterDef.DefaultExclude; changed(); rebuild(); }));

        // ---- Deletion & versions ----
        bool permanent = AppSettings.Current.FileSyncAllowPermanentDelete;
        var vLeft = new TextBox { Text = o.VersionsLeft, Width = 300, ToolTip = "A folder on the left side. Empty = .dcx-versions inside the left sync folder (never synced)." };
        vLeft.TextChanged += (_, _) => { o.VersionsLeft = vLeft.Text.Trim(); changed(); };
        var vRight = new TextBox { Text = o.VersionsRight, Width = 300, ToolTip = "A folder on the right side. Empty = .dcx-versions inside the right sync folder (never synced)." };
        vRight.TextChanged += (_, _) => { o.VersionsRight = vRight.Text.Trim(); changed(); };
        Tab("Deletion & versions",
            Row(Labeled("DELETED AND OVERWRITTEN FILES", Combo(new[] { "Recycle Bin (this PC; versions on a server)", "Versioning (move to a versions folder)", permanent ? "Permanent" : "Permanent (not allowed: kept as versions)" },
                    (int)o.Deletion, i => { o.Deletion = (DeletionMode)i; changed(); }, 320,
                    "A server has no Recycle Bin, so there it keeps versions. Permanent deletion on servers needs the admin-protected setting in Settings → Folder sync.")),
                Labeled("VERSION NAMES", Combo(new[] { "Time stamp per file (name 2026-10-25 172233.ext)", "Time stamp per run (2026-10-25 172233/path)", "Replace (keep the last one only)" },
                    (int)o.Naming, i => { o.Naming = (VersionNaming)i; changed(); }, 390))),
            Row(Labeled("VERSIONS FOLDER ON THE LEFT", vLeft), Labeled("VERSIONS FOLDER ON THE RIGHT", vRight)),
            Row(Labeled("KEEP VERSIONS (DAYS, 0 = FOREVER)", Number(o.VersionsKeepDays, v => { o.VersionsKeepDays = v; changed(); }, 0, 3650)),
                Labeled("AT MOST PER FILE (0 = NO LIMIT)", Number(o.VersionsMaxCount, v => { o.VersionsMaxCount = v; changed(); }, 0, 10000))),
            Hint("Overwritten files are kept the same way, so a sync can always be undone by hand. Old versions are tidied after each sync."));

        // ---- Schedule & safety (a whole job) ----
        var verify = Combo(new[] { "As in transfer options", "Always verify (SHA-256)", "Don't verify" }, o.Verify switch { true => 1, false => 2, _ => 0 }, i => { o.Verify = i switch { 1 => true, 2 => false, _ => null }; changed(); }, 220);
        var guard = Number(o.MaxDeletePercent, v => { o.MaxDeletePercent = v; changed(); }, 1, 100, tip: "A run without a preview stops when it would delete more than this share of one side's files (and more than 10).");
        if (job == null) { Tab("Safety", Row(Labeled("UNATTENDED RUNS STOP ABOVE (% DELETED)", guard), Labeled("VERIFY COPIES", verify))); return tabs; }

        var every = Labeled("EVERY (MINUTES)", Number(job.IntervalMinutes, v => { job.IntervalMinutes = v; changed(); }, 1, 44640));
        var at = new TextBox { Text = job.At, Width = 80, ToolTip = "24-hour time, like 02:00" };
        at.TextChanged += (_, _) => { if (TimeSpan.TryParse(at.Text.Trim(), CultureInfo.InvariantCulture, out var t) && t >= TimeSpan.Zero && t < TimeSpan.FromDays(1)) { job.At = t.ToString(@"hh\:mm"); changed(); } };
        var atBox = Labeled("AT", at);
        var days = new WrapPanel();
        for (int d = 1; d <= 7; d++)
        {
            int bit = d % 7;   // Monday first, Sunday last
            var cb = new CheckBox { Content = ((DayOfWeek)bit).ToString()[..3], IsChecked = (job.Days & (1 << bit)) != 0, Margin = new Thickness(0, 4, 10, 0) };
            cb.Click += (_, _) => { job.Days = cb.IsChecked == true ? job.Days | (1 << bit) : job.Days & ~(1 << bit); changed(); };
            days.Children.Add(cb);
        }
        var daysBox = Labeled("ON", days);
        var delay = Labeled("RUN AFTER (SECONDS OF QUIET)", Number(job.RealTimeDelaySeconds, v => { job.RealTimeDelaySeconds = v; changed(); }, 1, 3600));
        var poll = Labeled("CHECK SERVER FOLDERS EVERY (MINUTES)", Number(job.RemotePollMinutes, v => { job.RemotePollMinutes = v; changed(); }, 1, 1440));
        var scheduleHint = Hint("");
        void ShowSchedule()
        {
            var k = job.Schedule;
            every.Visibility = k == SyncSchedule.Interval ? Visibility.Visible : Visibility.Collapsed;
            atBox.Visibility = k is SyncSchedule.Daily or SyncSchedule.Weekly ? Visibility.Visible : Visibility.Collapsed;
            daysBox.Visibility = k == SyncSchedule.Weekly ? Visibility.Visible : Visibility.Collapsed;
            delay.Visibility = poll.Visibility = k == SyncSchedule.RealTime ? Visibility.Visible : Visibility.Collapsed;
            scheduleHint.Text = (k == SyncSchedule.Off ? "" : "Runs while the app is open and unlocked, without a preview: conflicts are left alone, and a pair stops if a folder is missing or it would delete too much. ")
                + (AppSettings.Current.FileSyncScheduled ? "" : "⚠ Scheduled runs are switched off in Settings → Folder sync. ")
                + (k == SyncSchedule.RealTime ? "Folders on this PC are watched; a server's folder is checked every few minutes." : "");
        }
        var kind = Combo(new[] { "Manual (only when you run it)", "Every N minutes", "Daily", "Weekly", "Real-time (when a folder changes)" }, (int)job.Schedule, i => { job.Schedule = (SyncSchedule)i; ShowSchedule(); changed(); }, 260);
        ShowSchedule();
        Tab("Schedule & safety", Row(Labeled("RUN", kind), every, atBox, delay, poll), daysBox, scheduleHint,
            Row(Labeled("UNATTENDED RUNS STOP ABOVE (% DELETED)", guard), Labeled("VERIFY COPIES", verify)));
        return tabs;
    }


    private static T Also<T>(this T x, Action<T> a) { a(x); return x; }
}
