using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using DcxManager.Files;
using DcxManager.Services;
using DcxManager.Ssh;

namespace DcxManager.Views;

/// <summary>
/// File Commander → Sync (ADR-014; FreeFileSync-style since 0.6.26, ADR-102). One window for an ad-hoc compare of the two
/// panes or a saved job: its folder pairs (this PC or any saved host on either side), the options (compare, synchronise,
/// filter, deletion &amp; versions, schedule), and the preview — every planned action with a tick, filter buttons per kind,
/// and a right-click to change what happens to any row. Nothing runs until Synchronise, which asks first (with the
/// deletion count and where deleted files go) and runs through the transfer queue.
/// </summary>
public sealed class SyncWindow
{
    private readonly SyncJobDef _job;
    private bool _saved;
    private List<SyncPairRun> _runs = new();
    private readonly ObservableCollection<SyncStep> _rows = new();
    private bool _stale = true, _busy;
    private Window? _window;
    private CancellationTokenSource? _cts;

    // controls the self-test reads
    internal TextBox NameBox { get; } = new() { Width = 300 };
    internal StackPanel PairsPanel { get; } = new();
    internal ContentControl OptionsHost { get; } = new();
    internal ListView List { get; } = new() { SelectionMode = SelectionMode.Extended };
    internal TextBlock Status { get; } = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) };
    internal TextBlock Totals { get; } = new() { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    internal Button CompareButton { get; } = new() { Content = "Compare", MinWidth = 100 };
    internal Button SyncButton { get; } = new() { Content = "Synchronise…", MinWidth = 120, IsEnabled = false };
    private readonly ComboBox _editing = new() { Width = 260, ToolTip = "Settings of the whole job, or of one folder pair (a pair can have its own)" };
    private readonly CheckBox _ownSettings = new() { Content = "This pair has its own settings", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
    private readonly WrapPanel _kinds = new() { Margin = new Thickness(0, 0, 0, 6) };
    private readonly StackPanel _optHead = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
    private Expander? _options;
    private readonly Dictionary<string, ToggleButton> _kindButtons = new();
    private readonly HashSet<string> _hidden = new();
    private readonly List<(ToggleButton Button, SyncVariant Variant, bool LeftToRight)> _presetButtons = new();
    internal IReadOnlyList<ToggleButton> PresetButtons => _presetButtons.Select(p => p.Button).ToList();
    /// <summary>One line saying what the job's settings will do (under the "what to do" buttons).</summary>
    internal TextBlock Summary { get; } = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4) };

    /// <summary>The everyday choices as one click each; the full settings stay under "More options".</summary>
    private static readonly (string Text, SyncVariant Variant, bool LeftToRight, string Tip)[] Presets =
    {
        ("⇄  Two-way", SyncVariant.TwoWay, true, "Changes and deletions on either side go to the other. Files changed on both sides are shown for you to decide."),
        ("Update  →", SyncVariant.Update, true, "Copy new and changed files from left to right. Never deletes anything."),
        ("Mirror  →", SyncVariant.Mirror, true, "Make the right an exact copy of the left. What's only on the right is deleted (to the Recycle Bin or versions)."),
        ("←  Update", SyncVariant.Update, false, "Copy new and changed files from right to left. Never deletes anything."),
        ("←  Mirror", SyncVariant.Mirror, false, "Make the left an exact copy of the right. What's only on the left is deleted (to the Recycle Bin or versions)."),
        ("Custom…", SyncVariant.Custom, true, "Choose the action for each kind of difference yourself (opens More options)."),
    };

    public SyncWindow(SyncJobDef job, bool saved) { _job = job; _saved = saved; }

    /// <summary>The panes' two folders as a new, unsaved job with the default options (Settings → Folder sync).</summary>
    public static SyncWindow FromPanes(FsRef leftRef, string leftDir, FsRef rightRef, string rightDir) =>
        new(new SyncJobDef
        {
            Name = $"{Leaf(leftDir)} ⇄ {Leaf(rightDir)}",
            Pairs = { new SyncPairDef { Left = leftRef.ToString(), LeftPath = leftDir, Right = rightRef.ToString(), RightPath = rightDir } },
            Options = SyncOptions.Defaults(),
        }, saved: false);

    private static string Leaf(string path) { var t = path.TrimEnd('/', '\\'); int i = t.LastIndexOfAny(new[] { '/', '\\' }); return i >= 0 && i < t.Length - 1 ? t[(i + 1)..] : t.Length > 0 ? t : "/"; }

    private static Style St(string key) => (Style)Application.Current.Resources[key];

    public void Show()
    {
        if (TestMode.Running) { Dialogs.RefusedInSelfTest("window: folder sync"); return; }
        var w = Dialogs.MakeWindow(Title(), 1180, 820);
        w.Content = Build();
        w.Closed += (_, _) => _cts?.Cancel();
        w.PreviewKeyDown += (_, e) => { if (e.Key == Key.F5) { e.Handled = true; _ = CompareAsync(); } };
        _window = w;
        w.Show();
        // With both folders known the differences are the first thing to see: compare straight away (nothing changes).
        if (_job.Pairs.Count > 0 && _job.Pairs.All(p => p.LeftPath.Length > 0 && p.RightPath.Length > 0))
            w.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => _ = CompareAsync());
    }

    private string Title() => (_saved ? "Sync job: " + _job.Name : "Compare / sync folders") + " — File Commander";

    /// <summary>The window's content (the self-test builds it without a window).</summary>
    public FrameworkElement Build()
    {
        var root = new DockPanel { Margin = new Thickness(14) };

        // ---- top: name, pairs, options ----
        var top = new StackPanel();
        NameBox.Text = _job.Name;
        NameBox.TextChanged += (_, _) => _job.Name = NameBox.Text.Trim();
        var nameRow = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var nameLabel = new TextBlock { Text = "JOB", Style = St("Label"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        DockPanel.SetDock(nameLabel, Dock.Left);
        nameRow.Children.Add(nameLabel);
        var saveHint = new TextBlock { Style = St("Hint"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), Text = _saved ? "saved job" : "not saved: Save job keeps it (and lets it run on a schedule)" }.Fs(FontStep.Caption);
        var namePanel = new StackPanel { Orientation = Orientation.Horizontal };
        namePanel.Children.Add(NameBox); namePanel.Children.Add(saveHint);
        nameRow.Children.Add(namePanel);
        top.Children.Add(nameRow);

        top.Children.Add(new TextBlock { Text = "FOLDER PAIRS", Style = St("Label"), Margin = new Thickness(0, 4, 0, 4) });
        top.Children.Add(PairsPanel);
        var addPair = new Button { Content = "+ Add folder pair", Style = St("SmallButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 6) };
        addPair.Click += (_, _) => { _job.Pairs.Add(new SyncPairDef { Left = "local", Right = "local" }); FillPairs(); FillEditing(); Stale(); };
        top.Children.Add(addPair);
        FillPairs();

        // ---- what to do: one click for the usual variants ----
        top.Children.Add(new TextBlock { Text = "WHAT TO DO", Style = St("Label"), Margin = new Thickness(0, 6, 0, 4) });
        var presets = new WrapPanel();
        foreach (var (text, variant, ltr, tip) in Presets)
        {
            var b = new ToggleButton { Content = text, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 6, 4), ToolTip = tip };
            b.Click += (_, _) =>
            {
                _job.Options.Variant = variant;
                if (variant is SyncVariant.Mirror or SyncVariant.Update) _job.Options.LeftToRight = ltr;
                if (variant == SyncVariant.Custom && _options != null) _options.IsExpanded = true;
                _editing.SelectedIndex = 0;   // the presets set the whole job
                ShowOptions(); OptionsChanged();
                if (_runs.Count > 0 && !_busy) _ = CompareAsync();   // the preview follows the choice
            };
            _presetButtons.Add((b, variant, ltr));
            presets.Children.Add(b);
        }
        top.Children.Add(presets);
        top.Children.Add(Summary);

        _optHead.Children.Add(new TextBlock { Text = "SETTINGS FOR", Style = St("Label"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        _optHead.Children.Add(_editing); _optHead.Children.Add(_ownSettings);
        _editing.SelectionChanged += (_, _) => ShowOptions();
        _ownSettings.Click += (_, _) =>
        {
            if (_editing.SelectedIndex <= 0) return;
            var p = _job.Pairs[_editing.SelectedIndex - 1];
            p.Override = _ownSettings.IsChecked == true ? _job.Options.Clone() : null;
            ShowOptions(); OptionsChanged();
        };
        var options = _options = new Expander { Header = new TextBlock { Text = "More options — compare, filter, deleted files & versions, schedule", Style = St("Label") }, IsExpanded = false, Margin = new Thickness(0, 4, 0, 0) };
        var optBody = new StackPanel();
        optBody.Children.Add(_optHead); optBody.Children.Add(OptionsHost);
        options.Content = optBody;
        top.Children.Add(options);
        FillEditing();

        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        CompareButton.Style = St("PrimaryButton");
        CompareButton.ToolTip = "Compare every folder pair and plan the sync (F5). Nothing changes yet.";
        CompareButton.Click += async (_, _) => await CompareAsync();
        SyncButton.Style = St("PrimaryButton");
        SyncButton.Margin = new Thickness(8, 0, 0, 0);
        SyncButton.ToolTip = "Run the ticked actions (asks first)";
        SyncButton.Click += async (_, _) => await SynchroniseAsync();
        var save = new Button { Content = _saved ? "Save job" : "Save as job…", Margin = new Thickness(8, 0, 0, 0), ToolTip = "Keep this job (pairs, settings, schedule) in the encrypted store" };
        save.Click += (_, _) => { if (SaveJob()) { save.Content = "Save job"; saveHint.Text = "saved job"; if (_window != null) _window.Title = Title(); } };
        var jobs = new Button { Content = "Sync jobs…", Margin = new Thickness(8, 0, 0, 0), ToolTip = "All saved jobs: run, schedule, duplicate, remove" };
        jobs.Click += (_, _) => SyncJobsWindow.Show(null);
        var help = new Button { Content = "Help", Margin = new Thickness(8, 0, 0, 0) };
        help.Click += (_, _) => MainWindow.Instance?.ShowHelp("Folder sync");
        buttons.Children.Add(CompareButton); buttons.Children.Add(SyncButton); buttons.Children.Add(save); buttons.Children.Add(jobs); buttons.Children.Add(help);
        top.Children.Add(buttons);
        top.Children.Add(Status);
        top.Children.Add(_kinds);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        // ---- bottom: totals ----
        var foot = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        foot.Children.Add(Totals);
        DockPanel.SetDock(foot, Dock.Bottom);
        root.Children.Add(foot);

        // ---- the preview ----
        var gv = new GridView();
        var cb = new FrameworkElementFactory(typeof(CheckBox));
        cb.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(SyncStep.Selected)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        cb.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((_, _) => UpdateTotals()));
        gv.Columns.Add(new GridViewColumn { Header = "", Width = 34, CellTemplate = new DataTemplate { VisualTree = cb } });
        gv.Columns.Add(new GridViewColumn { Header = "Action", Width = 100, DisplayMemberBinding = new Binding(nameof(SyncStep.ActionText)) });
        gv.Columns.Add(new GridViewColumn { Header = "#", Width = 34, DisplayMemberBinding = new Binding(nameof(SyncStep.Pair)) { Converter = new PairText() } });
        gv.Columns.Add(new GridViewColumn { Header = "Path", Width = 360, DisplayMemberBinding = new Binding(".") { Converter = new PathText() } });
        gv.Columns.Add(new GridViewColumn { Header = "Left", Width = 170, DisplayMemberBinding = new Binding("Entry.Left") { Converter = new ItemText() } });
        gv.Columns.Add(new GridViewColumn { Header = "Right", Width = 170, DisplayMemberBinding = new Binding("Entry.Right") { Converter = new ItemText() } });
        gv.Columns.Add(new GridViewColumn { Header = "Why", Width = 320, DisplayMemberBinding = new Binding(nameof(SyncStep.Why)) });
        List.View = gv;
        List.ItemsSource = _rows;
        CollectionViewSource.GetDefaultView(_rows).Filter = o => o is SyncStep s && !_hidden.Contains(Kind(s));
        Menus.SelectOnRightClick(List);
        Menus.Attach(List, RowMenu);
        List.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Space || List.SelectedItems.Count == 0) return;
            e.Handled = true;
            var sel = List.SelectedItems.OfType<SyncStep>().ToList();
            bool on = !sel.All(s => s.Selected);
            foreach (var s in sel) s.Selected = on;
            UpdateTotals();
        };
        root.Children.Add(List);
        Status.Text = "Pick what to do, then Compare (F5) to see what would happen. Nothing changes until you press Synchronise.";
        return root;
    }

    // ---- pairs ---------------------------------------------------------------------------------------------------------

    private void FillPairs()
    {
        PairsPanel.Children.Clear();
        for (int i = 0; i < _job.Pairs.Count; i++)
        {
            var p = _job.Pairs[i];
            // number · place · path (stretches) · ⇄ · place · path (stretches) · swap · remove · own settings
            var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            foreach (var w in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto })
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
            void Add(UIElement e, int col) { Grid.SetColumn(e, col); row.Children.Add(e); }
            if (_job.Pairs.Count > 1) Add(new TextBlock { Text = $"{i + 1}.", Width = 22, VerticalAlignment = VerticalAlignment.Center }, 0);
            Add(Place(p.Left, v => { p.Left = v; Stale(); }), 1);
            Add(PathBox(p.LeftPath, v => { p.LeftPath = v; Stale(); }, () => p.LeftRef), 2);
            Add(new TextBlock { Text = "⇄", Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center }, 3);
            Add(Place(p.Right, v => { p.Right = v; Stale(); }), 4);
            Add(PathBox(p.RightPath, v => { p.RightPath = v; Stale(); }, () => p.RightRef), 5);
            var swap = new Button { Content = "⇆", Style = St("SmallButton"), Margin = new Thickness(6, 0, 0, 0), ToolTip = "Swap left and right" };
            swap.Click += (_, _) => { (p.Left, p.LeftPath, p.Right, p.RightPath) = (p.Right, p.RightPath, p.Left, p.LeftPath); FillPairs(); Stale(); };
            Add(swap, 6);
            if (_job.Pairs.Count > 1)
            {
                var remove = new Button { Content = "✕", Style = St("SmallButton"), Margin = new Thickness(4, 0, 0, 0), ToolTip = "Remove this folder pair from the job (no files are touched)" };
                remove.Click += (_, _) => { _job.Pairs.Remove(p); FillPairs(); FillEditing(); Stale(); };
                Add(remove, 7);
            }
            if (p.Override != null) Add(new TextBlock { Text = "own settings", Style = St("Hint"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) }.Fs(FontStep.Caption), 8);
            PairsPanel.Children.Add(row);
        }
    }

    private static ComboBox Place(string current, Action<string> set)
    {
        var c = new ComboBox { Width = 180, Margin = new Thickness(0, 0, 6, 0) };
        c.Items.Add(new ComboBoxItem { Content = "💻 This PC", Tag = "local" });
        foreach (var h in AppData.Current.Hosts.Where(h => h.Protocol != "rdp").OrderBy(h => h.Group).ThenBy(h => h.Display))
            c.Items.Add(new ComboBoxItem { Content = $"🖧 {h.Display}", Tag = "host:" + h.Id, ToolTip = $"{h.Username}@{h.Endpoint}" });
        if (current.StartsWith("host:") && !c.Items.OfType<ComboBoxItem>().Any(i => (string)i.Tag == current))
            c.Items.Add(new ComboBoxItem { Content = "(deleted host)", Tag = current });
        c.SelectedItem = c.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == current) ?? c.Items[0];
        c.SelectionChanged += (_, _) => { if (c.SelectedItem is ComboBoxItem { Tag: string t }) set(t); };
        return c;
    }

    private static DockPanel PathBox(string current, Action<string> set, Func<FsRef> where)
    {
        var p = new DockPanel();
        var t = new TextBox { Text = current, MinWidth = 120, ToolTip = "The folder: C:\\… on this PC, /srv/… on a server" }.Mono();
        t.TextChanged += (_, _) => set(t.Text.Trim());
        var browse = new Button { Content = "…", Style = St("SmallButton"), Margin = new Thickness(4, 0, 0, 0), ToolTip = "Choose a folder on this PC" };
        browse.Click += (_, _) =>
        {
            if (where().Kind != "local") { Dialogs.Info("Folder", "Type the server's folder (or open it in a File Commander pane and use Compare / sync from there)."); return; }
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Folder to sync", InitialDirectory = System.IO.Directory.Exists(t.Text) ? t.Text : null };
            if (dlg.ShowDialog() == true) t.Text = dlg.FolderName;
        };
        DockPanel.SetDock(browse, Dock.Right);
        p.Children.Add(browse); p.Children.Add(t);
        return p;
    }

    // ---- options -------------------------------------------------------------------------------------------------------

    private void FillEditing()
    {
        int keep = Math.Max(0, _editing.SelectedIndex);
        _editing.Items.Clear();
        _editing.Items.Add("The whole job (all pairs)");
        for (int i = 0; i < _job.Pairs.Count; i++) _editing.Items.Add($"Pair {i + 1}: {Leaf(_job.Pairs[i].LeftPath)} ⇄ {Leaf(_job.Pairs[i].RightPath)}");
        _editing.SelectedIndex = Math.Min(keep, _editing.Items.Count - 1);
        _optHead.Visibility = _job.Pairs.Count > 1 ? Visibility.Visible : Visibility.Collapsed;   // one pair: the job's settings are its settings
        ShowOptions();
    }

    private void ShowOptions()
    {
        int i = _editing.SelectedIndex;
        var pair = i > 0 && i <= _job.Pairs.Count ? _job.Pairs[i - 1] : null;
        _ownSettings.Visibility = pair != null ? Visibility.Visible : Visibility.Collapsed;
        _ownSettings.IsChecked = pair?.Override != null;
        if (pair != null && pair.Override == null)
        {
            OptionsHost.Content = new TextBlock { Text = "This pair uses the job's settings. Tick “This pair has its own settings” to change them for this pair only.", Style = St("Hint"), Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
            return;
        }
        OptionsHost.Content = SyncOptionsEditor.Build(pair?.Override ?? _job.Options, pair == null ? _job : null, OptionsChanged, ShowOptions);
        RefreshPresets();
    }

    private void OptionsChanged() { Stale(); RefreshPresets(); }

    /// <summary>Lights the "what to do" button that matches the job's settings and says in one line what they do.</summary>
    private void RefreshPresets()
    {
        var o = _job.Options;
        foreach (var (b, v, ltr) in _presetButtons)
            b.IsChecked = v == o.Variant && (v is not (SyncVariant.Mirror or SyncVariant.Update) || ltr == o.LeftToRight);
        var deleted = o.Deletion switch
        {
            DeletionMode.Versioning => "deleted and overwritten files are kept in a versions folder",
            DeletionMode.Permanent => "deleted files go to the Recycle Bin here and are deleted permanently on a server if allowed",
            _ => "deleted and overwritten files go to the Recycle Bin (versions folder on a server)",
        };
        var what = o.Variant switch
        {
            SyncVariant.TwoWay => "Both sides end up the same: changes and deletions go both ways, conflicts are shown for you.",
            SyncVariant.Update => o.LeftToRight ? "New and changed files are copied left → right; nothing is deleted." : "New and changed files are copied right → left; nothing is deleted.",
            SyncVariant.Mirror => (o.LeftToRight ? "The right becomes a copy of the left" : "The left becomes a copy of the right") + (o.MirrorDeletes ? " (extras are removed)." : " (extras are kept)."),
            _ => "Your own action for each kind of difference (More options → Synchronise).",
        };
        int own = _job.Pairs.Count(p => p.Override != null);
        Summary.Text = $"{what.TrimEnd('.')} · {deleted}" + (o.Filter.Include.Trim() != "*" || o.Filter.MinBytes > 0 || o.Filter.MaxBytes > 0 || o.Filter.MaxAgeDays > 0 ? " · filtered" : "")
                       + (_job.ScheduleText is { Length: > 0 } s && _job.Schedule != SyncSchedule.Off ? " · runs " + s : "")
                       + (own > 0 ? $" · {own} pair(s) use their own settings" : "");
    }

    private void Stale()
    {
        if (_stale && _rows.Count == 0) return;
        _stale = true;
        SyncButton.IsEnabled = false;
        if (_rows.Count > 0) Status.Text = "Settings or folders changed: Compare again (F5) before synchronising.";
    }

    // ---- compare and sync ----------------------------------------------------------------------------------------------

    internal async Task CompareAsync()
    {
        if (_busy) { _cts?.Cancel(); return; }
        if (_job.Pairs.Count == 0) { Status.Text = "Add a folder pair first."; return; }
        _busy = true; _cts = new CancellationTokenSource();
        CompareButton.Content = "Stop"; SyncButton.IsEnabled = false;
        _rows.Clear();
        try
        {
            var prog = new Progress<string>(s => Status.Text = "comparing " + s);
            _runs = await SyncRunner.CompareAsync(_job, prog, _cts.Token);
            foreach (var r in _runs) foreach (var s in r.Steps) _rows.Add(s);
            _stale = false;
            var lines = _runs.Select(r =>
            {
                var head = _runs.Count > 1 ? $"Pair {r.Index}: " : "";
                if (r.Error != null) return $"✕ {head}{r.Error}";
                int same = r.Diffs.Count(d => d.State == DiffState.Same && !d.IsDir), files = r.Diffs.Count(d => !d.IsDir);
                var db = r.Options.Variant == SyncVariant.TwoWay ? (r.Db is { Count: > 0 } ? " · last sync remembered" : " · first Two-way run: nothing remembered yet") : "";
                return $"{head}{files} file(s) compared, {same} identical, {r.Steps.Count} action(s) planned · {r.Options.Describe()}{db}";
            });
            Status.Text = string.Join("\n", lines);
            FillKinds();
        }
        catch (OperationCanceledException) { Status.Text = "Compare stopped."; }
        catch (Exception ex) { Status.Text = "✕ " + ex.Message; App.Log("Folder compare", ex); }
        finally { _busy = false; CompareButton.Content = "Compare"; UpdateTotals(); }   // after _busy, or Synchronise stays off
    }

    private static string Kind(SyncStep s) => s.Action switch
    {
        SyncAction.CopyRight => "→", SyncAction.CopyLeft => "←", SyncAction.DeleteLeft or SyncAction.DeleteRight => "delete", SyncAction.Conflict => "conflict", _ => "leave",
    };

    private void FillKinds()
    {
        _kinds.Children.Clear(); _kindButtons.Clear();
        foreach (var (key, text, tip) in new[] { ("→", "copy →", "Copied left to right"), ("←", "← copy", "Copied right to left"), ("delete", "delete", "Deleted (Recycle Bin / versions / permanent, as set)"), ("conflict", "conflicts", "Left alone unless you pick an action (right-click)"), ("leave", "left alone", "Rows you set to leave alone") })
        {
            var b = new ToggleButton { IsChecked = !_hidden.Contains(key), Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 6, 0), ToolTip = "Show or hide: " + tip, Tag = text };
            b.Click += (_, _) => { if (b.IsChecked == true) _hidden.Remove(key); else _hidden.Add(key); CollectionViewSource.GetDefaultView(_rows).Refresh(); };
            _kindButtons[key] = b;
            _kinds.Children.Add(b);
        }
        CountKinds();
    }

    private void CountKinds()
    {
        foreach (var (key, b) in _kindButtons) b.Content = $"{b.Tag} ({_rows.Count(s => Kind(s) == key)})";
    }

    private void UpdateTotals()
    {
        Totals.Text = _rows.Count == 0 ? "" : FolderSync.Totals(_rows);
        SyncButton.IsEnabled = !_stale && !_busy && _rows.Any(s => s.Runs) && _runs.Any(r => r.Error == null);
        CountKinds();
    }

    private IEnumerable<object?> RowMenu()
    {
        var sel = List.SelectedItems.OfType<SyncStep>().ToList();
        if (sel.Count == 0) yield break;
        bool Can(SyncStep s, SyncAction a) => a switch
        {
            SyncAction.CopyRight or SyncAction.DeleteLeft => s.Entry.Left != null && (a != SyncAction.DeleteLeft || !s.Entry.Partial) && !(a == SyncAction.CopyRight && s.Entry.State == DiffState.TypeConflict),
            SyncAction.CopyLeft or SyncAction.DeleteRight => s.Entry.Right != null && (a != SyncAction.DeleteRight || !s.Entry.Partial) && !(a == SyncAction.CopyLeft && s.Entry.State == DiffState.TypeConflict),
            _ => true,
        };
        object Set(string text, SyncAction a) => Menus.Item(text, () =>
        {
            foreach (var s in sel.Where(s => Can(s, a))) { s.Action = a; s.Selected = a != SyncAction.None; }
            CollectionViewSource.GetDefaultView(_rows).Refresh(); UpdateTotals();
        }, enabled: sel.Any(s => Can(s, a)));
        yield return Set("Copy →  (left to right)", SyncAction.CopyRight);
        yield return Set("← Copy  (right to left)", SyncAction.CopyLeft);
        yield return Set("Delete on the left", SyncAction.DeleteLeft);
        yield return Set("Delete on the right", SyncAction.DeleteRight);
        yield return Set("Leave alone", SyncAction.None);
        yield return Menus.Item("Back to the planned action", () =>
        {
            foreach (var s in sel) { s.Action = s.Planned; s.Selected = s.Planned is not (SyncAction.Conflict or SyncAction.None); }
            CollectionViewSource.GetDefaultView(_rows).Refresh(); UpdateTotals();
        });
        yield return Menus.Sep();
        yield return Menus.Item("Tick", () => { foreach (var s in sel) s.Selected = true; UpdateTotals(); });
        yield return Menus.Item("Untick", () => { foreach (var s in sel) s.Selected = false; UpdateTotals(); });
        yield return Menus.Sep();
        yield return Menus.Item(sel.Count == 1 ? $"Exclude “{sel[0].Entry.Rel}” from now on" : $"Exclude these {sel.Count} from now on", () =>
        {
            foreach (var s in sel)
            {
                var pair = s.Pair > 0 ? _job.Pairs[s.Pair - 1] : _job.Pairs[0];
                var f = _job.OptionsFor(pair).Filter;
                var line = "/" + s.Entry.Rel + (s.Entry.IsDir ? "/" : "");
                if (!f.Exclude.Split('\n').Any(x => x.Trim() == line)) f.Exclude = (f.Exclude.TrimEnd('\n') + "\n" + line).TrimStart('\n');
                _rows.Remove(s);
            }
            ShowOptions(); UpdateTotals();
            Status.Text = "Added to the exclude filter (More options → Filter)." + (_saved ? " Save job to keep it." : "");
        }, tip: "Adds the path to the exclude filter of its pair's settings; it's never compared or synced again");
    }

    private async Task SynchroniseAsync()
    {
        if (_stale || _busy) return;
        var runnable = _runs.Where(r => r.Error == null).ToList();
        var sel = runnable.SelectMany(r => r.Steps).Where(s => s.Runs).ToList();
        if (sel.Count == 0) return;
        if (Profiles.ReadOnly) { Dialogs.Info("Synchronise", "This profile is read-only (viewer): it can compare but not synchronise."); return; }
        if (!ConfirmRun(_runs, "Synchronise")) return;
        _busy = true; SyncButton.IsEnabled = false; CompareButton.IsEnabled = false;
        Status.Text = "Synchronising… (progress in File Commander → Transfers)";
        try
        {
            var result = await SyncRunner.ExecuteAsync(_runs, _saved ? _job.Id : null, "you");
            Status.Text = result.Text + "\n\nCompare again (F5) to check both sides.";
            _stale = true; _rows.Clear(); CountKinds(); Totals.Text = "";
            MainWindow.Instance?.Status((result.Ok ? "✓ " : "✕ ") + "Folder sync " + (result.Ok ? "finished" : "had errors") + ": " + _job.Name);
        }
        catch (Exception ex) { Status.Text = "✕ " + ex.Message; App.Log("Folder sync", ex); }
        finally { _busy = false; CompareButton.IsEnabled = true; }
    }

    /// <summary>Asks before the ticked steps of <paramref name="runs"/> run: the totals, where deleted and overwritten files
    /// go on each side, the deletion count typed to confirm, and the PRODUCTION banner of a changed server (ADR-017).
    /// Shared by the window and File Commander's quick syncs.</summary>
    internal static bool ConfirmRun(IReadOnlyList<SyncPairRun> runs, string title)
    {
        var runnable = runs.Where(r => r.Error == null).ToList();
        var sel = runnable.SelectMany(r => r.Steps).Where(s => s.Runs).ToList();
        if (sel.Count == 0) return false;
        int dels = sel.Count(s => s.Action is SyncAction.DeleteLeft or SyncAction.DeleteRight);
        var where = new List<string>();
        foreach (var r in runnable)
        {
            foreach (var left in new[] { true, false })
            {
                bool touches = r.Steps.Any(s => s.Runs && s.Action == (left ? SyncAction.DeleteLeft : SyncAction.DeleteRight) || s.Runs && s.Action == (left ? SyncAction.CopyLeft : SyncAction.CopyRight) && (left ? s.Entry.Left : s.Entry.Right) != null);
                if (!touches) continue;
                var fsRef = left ? r.Pair.LeftRef : r.Pair.RightRef;
                var mode = r.Options.Deletion switch
                {
                    DeletionMode.RecycleBin when fsRef.Kind == "local" => "the Recycle Bin",
                    DeletionMode.Permanent when AppSettings.Current.FileSyncAllowPermanentDelete && fsRef.Kind != "local" => "nowhere — PERMANENTLY deleted",
                    DeletionMode.Permanent when fsRef.Kind == "local" => "the Recycle Bin",
                    _ => "a versions folder (" + ((left ? r.Options.VersionsLeft : r.Options.VersionsRight) is { Length: > 0 } v ? v : SyncFilter.VersionsFolder + " in the sync folder") + ")",
                };
                where.Add($"• {fsRef.Title} ({(left ? "left" : "right")}{(r.Index > 0 ? $", pair {r.Index}" : "")}): deleted and overwritten files go to {mode}");
            }
        }
        var text = FolderSync.Totals(sel) + (where.Count > 0 ? "\n\n" + string.Join("\n", where) : "\n\nNothing is deleted or overwritten.")
                   + (runs.Any(r => r.Error != null) ? "\n\n⚠ Pairs with an error are skipped." : "");
        // The sides that change decide (ADR-017): a PRODUCTION side wins, then any labelled one.
        var touched = runnable.SelectMany(r => new[]
        {
            r.Steps.Any(s => s.Runs && s.Action is SyncAction.CopyRight or SyncAction.DeleteRight) ? r.Pair.RightRef : null,
            r.Steps.Any(s => s.Runs && s.Action is SyncAction.CopyLeft or SyncAction.DeleteLeft) ? r.Pair.LeftRef : null,
        }).Where(x => x is { Kind: not "local" } && x.Host != null).Select(x => x!.Host!).ToList();
        var target = touched.FirstOrDefault(h => Envs.IsProduction(Envs.For(h))) ?? touched.FirstOrDefault(h => Envs.For(h).Length > 0);
        return Envs.Confirm(target, title, text, dels > 0 ? $"Sync and delete {dels}" : "Synchronise", danger: dels > 0, typeToConfirm: dels > 0 ? dels.ToString() : null);
    }

    /// <summary>Checks and stores the job; false (with the reason shown) when it can't be saved yet.</summary>
    internal bool SaveJob()
    {
        if (_job.Name.Length == 0) { Status.Text = "Give the job a name first."; return false; }
        if (_job.Pairs.Count == 0 || _job.Pairs.Any(p => p.LeftPath.Length == 0 || p.RightPath.Length == 0)) { Status.Text = "Every folder pair needs a folder on both sides."; return false; }
        if (_job.Pairs.Select(p => SyncFilter.Check(_job.OptionsFor(p).Filter)).FirstOrDefault(x => x != null) is { } bad) { Status.Text = "Filter: " + bad; return false; }
        SyncJobStore.Save(_job);
        AuditLog.Write("you", "files", _saved ? "sync job changed" : "sync job saved", $"{_job.Name}: {_job.Pairs.Count} pair(s), {_job.Options.Describe()}, {_job.ScheduleText}");
        _saved = true;
        Status.Text = $"Saved “{_job.Name}” ({_job.ScheduleText}).";
        return true;
    }

    // ---- converters ----------------------------------------------------------------------------------------------------

    private sealed class ItemText : IValueConverter
    {
        public object Convert(object? v, Type t, object? p, System.Globalization.CultureInfo c) =>
            v is FsItem i ? (i.IsDir ? "folder" : Fmt.Bytes(i.Length)) + " · " + i.Modified.ToString("yyyy-MM-dd HH:mm") : "—";
        public object ConvertBack(object? v, Type t, object? p, System.Globalization.CultureInfo c) => throw new NotSupportedException();
    }

    private sealed class PathText : IValueConverter
    {
        public object Convert(object? v, Type t, object? p, System.Globalization.CultureInfo c) =>
            v is SyncStep s ? s.Entry.Rel + (s.Entry.IsDir ? "/" + (s.FolderOnly ? "" : s.FolderFiles > 0 ? $"  ({s.FolderFiles} files, {Fmt.Bytes(s.FolderBytes)})" : "  (empty)") : "") : "";
        public object ConvertBack(object? v, Type t, object? p, System.Globalization.CultureInfo c) => throw new NotSupportedException();
    }

    private sealed class PairText : IValueConverter
    {
        public object Convert(object? v, Type t, object? p, System.Globalization.CultureInfo c) => v is int n && n > 0 ? n.ToString() : "";
        public object ConvertBack(object? v, Type t, object? p, System.Globalization.CultureInfo c) => throw new NotSupportedException();
    }
}
