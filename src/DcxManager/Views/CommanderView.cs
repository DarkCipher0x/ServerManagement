using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using DcxManager.Files;
using DcxManager.Services;
using DcxManager.Ssh;
using DcxManager.Documents;

namespace DcxManager.Views;

/// <summary>
/// Commander-style dual-pane file manager: each pane is this PC or any saved host (SFTP), so local⇄remote and
/// remote⇄remote both work. F-key bar like Total Commander (F2 rename, F3 view, F4 edit, F5 copy, F6 move, F7 folder,
/// F8 delete), Tab switches panes, drag between panes or from Explorer, and every transfer goes through the shared
/// background queue (visible below, also used by the AI operator).
/// </summary>
public sealed partial class CommanderView : UserControl, IDisposable
{
    private readonly FilePane _left, _right;
    private FilePane _active;
    private readonly ListView _jobs = new() { MinHeight = 90, MaxHeight = 220 };
    private readonly Expander _jobsBox;

    /// <summary>The Commander screen on show (WSL → Open files uses it). Weak: a closed Commander must be collectable.</summary>
    public static CommanderView? Current => _current != null && _current.TryGetTarget(out var c) ? c : null;
    private static WeakReference<CommanderView>? _current;

    /// <summary>Opens a folder of this PC (e.g. \\wsl.localhost\Ubuntu\) in the active pane.</summary>
    public Task OpenLocalPathAsync(string path) => _active.OpenAsync(FsRef.Local, path);

    /// <summary>The host this commander was opened for (its right pane), for switching to it instead of opening another (0.5.9).</summary>
    internal string? OpenedForHostId { get; }

    /// <summary>Both panes' places ("local|C:\x", "host:id|/path"), for saved sessions (0.6.29).</summary>
    internal (string Left, string Right) Places => (_left.State, _right.State);

    /// <param name="places">A saved session's two panes (0.6.29): they reopen exactly there.</param>
    public CommanderView(HostEntry? rightHost = null, string? rightPath = null, (string Left, string Right)? places = null)
    {
        OpenedForHostId = rightHost?.Id;
        _current = new WeakReference<CommanderView>(this);
        Loaded += (_, _) => _current = new WeakReference<CommanderView>(this);
        var s = AppSettings.Current;
        // 0.5.1 #4: a new Commander tab connects to nothing. Only a host you opened it for (a host's "Files in Commander")
        // is connected; the last places come back only when CommanderRestoreLast is on (off by default, Group Policy can set
        // it), or with one click (More ▾ → Reconnect the last places).
        bool restore = s.CommanderRestoreLast;
        _left = new FilePane(this, "left", places?.Left ?? (restore && s.CommanderLeft.Length > 0 ? s.CommanderLeft : "none|"));
        _right = new FilePane(this, "right", places?.Right ?? (rightHost != null ? $"host:{rightHost.Id}|{rightPath ?? ""}" : restore && s.CommanderRight.Length > 0 ? s.CommanderRight : "none|"));
        _active = _left;

        var panes = new Grid();
        panes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 220 });
        panes.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        panes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 220 });
        var split = new GridSplitter { Width = 6, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        Grid.SetColumn(_left, 0); Grid.SetColumn(split, 1); Grid.SetColumn(_right, 2);
        panes.Children.Add(_left); panes.Children.Add(split); panes.Children.Add(_right);

        // F-key bar: the primary actions stay visible, everything else is on right-click / More.
        var keys = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        void K(string key, string text, Func<Task> run, string tip, string? style = null)
        {
            var b = new Button { Content = $"{key} {text}", Style = (Style)FindResource(style ?? "SmallButton"), Margin = new Thickness(0, 0, 4, 4), ToolTip = tip };
            b.Click += async (_, _) => await run();
            keys.Children.Add(b);
        }
        K("F1", "Help", () => { MainWindow.Instance.ShowHelp("File commander"); return Task.CompletedTask; }, "Help for the File commander");
        K("F2", "Rename", () => _active.RenameAsync(), "Rename the selected item");
        K("F3", "View", () => _active.ViewAsync(), "Read-only view of a text file");
        K("F4", "Edit", () => _active.EditAsync(), "Edit a text file (saved straight back)");
        K("F5", "Copy →", () => TransferAsync(false), "Copy the selection to the other pane's folder");
        K("F6", "Move →", () => TransferAsync(true), "Move the selection to the other pane's folder");
        K("F7", "New folder", () => _active.MkdirAsync(), "Create a folder");
        K("F8", "Delete", () => _active.DeleteAsync(), "Delete (local: Recycle Bin; remote: permanent, asks first)", "SmallButton");
        K("F9", "Menu", () => { OpenMenuBar(); return Task.CompletedTask; }, "The menu bar: Left, File, Command, Options, Right (Midnight Commander style)");
        var sync = Menus.MoreButton(SyncMenu, "⇄ Sync ▾", "SmallButton");
        sync.ToolTip = "Sync the two panes' folders: Update, Mirror or Two-way in one step, the compare window, saved jobs";
        keys.Children.Add(sync);
        keys.Children.Add(Menus.MoreButton(() => new object?[]
        {
            Menus.Item("↺ Reconnect the last places", ReconnectLast, enabled: AppSettings.Current.CommanderLeft.Length + AppSettings.Current.CommanderRight.Length > 0,
                tip: "Opens the places these panes showed last time (a new Commander tab never connects by itself)"),
            Menus.Item("⇄ Swap panes", Swap, "Ctrl+U"),
            Menus.Item("= Same folder in the other pane", () => _ = Other(_active).OpenAsync(_active.Ref, _active.Path), tip: "Opens this pane's place and folder in the other pane"),
            Menus.Item("↻ Refresh both", () => { _ = _left.RefreshAsync(); _ = _right.RefreshAsync(); }, "Ctrl+R"),
            Menus.Sep(),
            Menus.Item(AppSettings.Current.CommanderShowHidden ? "Hide hidden files" : "Show hidden files", ToggleHidden, "Ctrl+H"),
            Menus.Sep(),
            Menus.Item("⇄ Compare / sync folders…", CompareFolders, tip: "Compare the two panes' folders (FreeFileSync-style: Two-way, Mirror, Update or Custom, filters, versioning) and sync after a preview"),
            Menus.Item("⇄ Sync jobs…", OpenSyncJobs, tip: "Saved sync jobs: run now, schedules and real-time sync"),
            Menus.Item("Transfer history…", () => Dialogs.ShowText("Transfer history (last 300)", TransferHistory.Text())),
            Menus.Sub("Transfer options",
                Menus.Item((AppSettings.Current.TransferVerify ? "✓ " : "   ") + "Verify every file with SHA-256", () => { AppSettings.Current.TransferVerify = !AppSettings.Current.TransferVerify; SaveSettings(); }),
                Menus.Item((AppSettings.Current.TransferKeepTimes ? "✓ " : "   ") + "Keep modified times (and permissions between servers)", () => { AppSettings.Current.TransferKeepTimes = !AppSettings.Current.TransferKeepTimes; SaveSettings(); }),
                Menus.Item((AppSettings.Current.TransferRetries > 0 ? "✓ " : "   ") + "Retry failing files 3× (resume, fresh connection)", () => { AppSettings.Current.TransferRetries = AppSettings.Current.TransferRetries > 0 ? 0 : 3; SaveSettings(); })),
            _active.Ref.Host is not { } ph ? null : Menus.Sub($"Files for {ph.Display} over…",
                new[] { ("auto", "Auto — SFTP, or SCP if the server has no SFTP"), ("sftp", "SFTP only"), ("scp", "SCP (shell commands + scp)") }
                    .Select(o => (object?)Menus.Item((ph.FileProtocol == o.Item1 ? "✓ " : "   ") + o.Item2, () =>
                    {
                        ph.FileProtocol = o.Item1;
                        if (!ph.IsAdHoc) { try { AppData.Save(); } catch { } }
                        _ = _active.OpenAsync(_active.Ref, _active.Path);
                    })).ToArray()),
            Menus.Item("Clear finished transfers", TransferQueue.ClearFinished),
        }, style: "SmallButton"));

        // Transfers: the shared queue, newest first.
        var gv = new GridView();
        gv.Columns.Add(new GridViewColumn { Header = "Transfer", Width = 220, DisplayMemberBinding = new Binding(nameof(TransferJob.Title)) });
        gv.Columns.Add(new GridViewColumn { Header = "Route", Width = 300, DisplayMemberBinding = new Binding(nameof(TransferJob.Route)) });
        var bar = new FrameworkElementFactory(typeof(ProgressBar));
        bar.SetBinding(RangeBase.ValueProperty, new Binding(nameof(TransferJob.Percent)));
        bar.SetValue(FrameworkElement.HeightProperty, 8.0); bar.SetValue(FrameworkElement.WidthProperty, 120.0);
        gv.Columns.Add(new GridViewColumn { Header = "Progress", Width = 136, CellTemplate = new DataTemplate { VisualTree = bar } });
        gv.Columns.Add(new GridViewColumn { Header = "Status", Width = 90, DisplayMemberBinding = new Binding(nameof(TransferJob.Status)) });
        gv.Columns.Add(new GridViewColumn { Header = "Detail", Width = 360, DisplayMemberBinding = new Binding(nameof(TransferJob.Detail)) });
        _jobs.View = gv;
        _jobs.ItemsSource = TransferQueue.Jobs;
        Menus.SelectOnRightClick(_jobs);
        Menus.Attach(_jobs, () =>
        {
            var j = _jobs.SelectedItem as TransferJob;
            return new object?[]
            {
                j is { Finished: false, Paused: true } ? Menus.Item("▶ Resume", () => j.Resume()) : Menus.Item("⏸ Pause", () => j?.Pause(), enabled: j is { Finished: false }),
                Menus.Item("Cancel", () => { j?.Resume(); j?.Cts.Cancel(); }, enabled: j is { Finished: false }),
                Menus.Item("↻ Retry (resumes unfinished files)", () => { if (j != null) TransferQueue.Retry(j); }, enabled: j is { Finished: true, Error: not null }),
                Menus.Item("Details…", () => { if (j != null) Dialogs.ShowText(j.Title, j.Summary + "\n" + j.Route + (j.Warnings.Count > 0 ? "\n\n" + string.Join("\n", j.Warnings.Select(x => "⚠ " + x)) : "")); }, enabled: j != null),
                Menus.Item("Show destination", () => { if (j != null) _ = Other(_active).OpenAsync(j.To, j.DestDir); }, enabled: j != null),
                Menus.Item("Copy summary", () => Menus.Clip(j?.Summary), enabled: j is { Finished: true }),
                Menus.Sep(),
                Menus.Item("Clear finished", TransferQueue.ClearFinished),
            };
        });
        _jobsBox = new Expander { Content = _jobs, IsExpanded = TransferQueue.Jobs.Count > 0, Margin = new Thickness(0, 6, 0, 0) };
        UpdateJobsHeader();
        TransferQueue.Jobs.CollectionChanged += OnJobsChanged;
        TransferQueue.Finished += OnJobFinished;

        var root = new DockPanel { Margin = new Thickness(12, 10, 12, 10) };
        var menuBar = BuildMenuBar();
        var cmdLine = BuildCommandLine();
        DockPanel.SetDock(menuBar, Dock.Top);
        DockPanel.SetDock(_jobsBox, Dock.Bottom); DockPanel.SetDock(keys, Dock.Bottom); DockPanel.SetDock(cmdLine, Dock.Bottom);
        root.Children.Add(menuBar); root.Children.Add(_jobsBox); root.Children.Add(keys); root.Children.Add(cmdLine); root.Children.Add(panes);
        Content = root;

        PreviewKeyDown += OnKey;
        Loaded += (_, _) => { _left.FocusList(); SetActive(_left); };
    }

    /// <summary>More ▾ → Reconnect the last places: the places the panes showed last (0.5.1 #4, one deliberate click).</summary>
    private void ReconnectLast()
    {
        var s = AppSettings.Current;
        foreach (var (pane, state) in new[] { (_left, s.CommanderLeft), (_right, s.CommanderRight) })
        {
            if (state.Length == 0) continue;
            var parts = state.Split('|', 2);
            var r = FsRef.Parse(parts[0]);
            if (r.Kind == "host" && r.Host == null) continue;
            _ = pane.OpenAsync(r, parts.Length > 1 ? parts[1] : "");
        }
    }

    internal FilePane Other(FilePane p) => p == _left ? _right : _left;
    internal FilePane Active => _active;

    internal void SetActive(FilePane p)
    {
        _active = p;
        _left.SetHighlight(p == _left);
        _right.SetHighlight(p == _right);
    }

    internal void Remember()
    {
        // A pane that isn't connected keeps the last real place (that is what "Reconnect the last places" opens).
        if (!_left.Ref.IsNone) AppSettings.Current.CommanderLeft = _left.State;
        if (!_right.Ref.IsNone) AppSettings.Current.CommanderRight = _right.State;
        try { AppSettings.Save(); } catch { }
    }

    private void Swap()
    {
        var (lr, lp) = (_left.Ref, _left.Path);
        _ = _left.OpenAsync(_right.Ref, _right.Path);
        _ = _right.OpenAsync(lr, lp);
    }

    private static void SaveSettings() { try { AppSettings.Save(); } catch { } }

    /// <summary>Left pane folder vs right pane folder → the compare/sync window.</summary>
    private void CompareFolders()
    {
        if (_left.Path.Length == 0 || _right.Path.Length == 0) { Dialogs.Info("Compare", "Open a folder in both panes first."); return; }
        if (_left.Ref.Equals(_right.Ref) && SamePath(_left.Path, _right.Path)) { Dialogs.Info("Compare", "Both panes show the same folder."); return; }
        SyncWindow.FromPanes(_left.Ref, _left.Path, _right.Ref, _right.Path).Show();
    }

    /// <summary>The saved sync jobs (0.6.26), offering a new job for the two panes' folders.</summary>
    private void OpenSyncJobs() => SyncJobsWindow.Show(((_left.Ref, _left.Path), (_right.Ref, _right.Path)));

    private void ToggleHidden()
    {
        AppSettings.Current.CommanderShowHidden = !AppSettings.Current.CommanderShowHidden;
        try { AppSettings.Save(); } catch { }
        _ = _left.RefreshAsync(); _ = _right.RefreshAsync();
    }

    private void OnJobsChanged(object? s, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        UpdateJobsHeader();
        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add) _jobsBox.IsExpanded = true;
    }

    private void OnJobFinished(TransferJob j)
    {
        UpdateJobsHeader();
        foreach (var p in new[] { _left, _right })
            if (p.Ref.Equals(j.To) && SamePath(p.Path, j.DestDir) || j.Move && p.Ref.Equals(j.From)) _ = p.RefreshAsync();
        MainWindow.Instance.Status(j.Summary);
    }

    private static bool SamePath(string a, string b) => a.TrimEnd('/', '\\').Equals(b.TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase);

    private void UpdateJobsHeader()
    {
        int running = TransferQueue.Jobs.Count(j => !j.Finished);
        _jobsBox.Header = new TextBlock { Text = running > 0 ? $"Transfers — {running} running" : $"Transfers ({TransferQueue.Jobs.Count})", Style = (Style)FindResource("Label") };
    }

    private async void OnKey(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox) { if (e.Key == Key.Tab) { e.Handled = true; var o = Other(_active); SetActive(o); o.FocusList(); } return; }
        if (await McKeyAsync(e)) return;
        var mods = Keyboard.Modifiers;
        switch (e.Key)
        {
            case Key.Tab: e.Handled = true; { var o = Other(_active); SetActive(o); o.FocusList(); } break;
            case Key.F2: e.Handled = true; await _active.RenameAsync(); break;
            case Key.F3: e.Handled = true; await _active.ViewAsync(); break;
            case Key.F4: e.Handled = true; await _active.EditAsync(); break;
            case Key.F5: e.Handled = true; await TransferAsync(false); break;
            case Key.F6: e.Handled = true; await TransferAsync(true); break;
            case Key.F7: e.Handled = true; await _active.MkdirAsync(); break;
            case Key.F8: case Key.Delete: e.Handled = true; await _active.DeleteAsync(); break;
            case Key.R when mods == ModifierKeys.Control: e.Handled = true; await _active.RefreshAsync(); break;
            case Key.H when mods == ModifierKeys.Control: e.Handled = true; ToggleHidden(); break;
            case Key.U when mods == ModifierKeys.Control: e.Handled = true; Swap(); break;
        }
    }

    /// <summary>F5/F6 and drag-drop: copy or move the active pane's selection into the other pane's folder.</summary>
    internal Task TransferAsync(bool move, FilePane? from = null, IReadOnlyList<FsItem>? items = null, FilePane? to = null)
    {
        Transfer(move, from, items, to);
        return Task.CompletedTask;
    }

    private void Transfer(bool move, FilePane? from, IReadOnlyList<FsItem>? items, FilePane? to)
    {
        from ??= _active; to ??= Other(from);
        var sel = (items ?? from.Selected).Where(i => !i.IsParent && !i.IsDrive).ToList();
        if (sel.Count == 0) { MainWindow.Instance.Status("Select files or folders first."); return; }
        if (to.InArchive) { Dialogs.Info(move ? "Move" : "Copy", "The other pane is inside an archive, which is read-only. Leave it (.. at its top) first."); return; }
        if (from.InArchive)
        {
            if (move) { Dialogs.Info("Move", "Files can't be moved out of an archive (it's read-only) — F5 copies them out."); return; }
            _ = from.ExtractAsync(to, sel);
            return;
        }
        if (to.Path.Length == 0) { Dialogs.Info(move ? "Move" : "Copy", "Open a folder in the other pane first."); return; }
        if (from.Ref.Equals(to.Ref) && sel.Any(i => SamePath(i.FullPath, to.Path) || i.IsDir && (to.Path + "/").Replace('\\', '/').StartsWith(i.FullPath.Replace('\\', '/').TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase)))
        { Dialogs.Error(move ? "Move" : "Copy", "Can't copy or move a folder into itself."); return; }
        if (from.Ref.Equals(to.Ref) && SamePath(from.Path, to.Path)) { Dialogs.Info(move ? "Move" : "Copy", "Both panes show the same folder."); return; }

        // Conflicts are decided up front, once, against what's visible in the target folder.
        var existing = to.Items.Where(i => !i.IsParent).Select(i => i.Name).ToHashSet(StringComparer.Ordinal);
        var clash = sel.Where(i => existing.Contains(i.Name)).ToList();
        var policy = Conflict.Overwrite;
        if (clash.Count > 0)
        {
            var pick = Dialogs.Choose(move ? "Move — names already exist" : "Copy — names already exist",
                $"{clash.Count} item(s) already exist in {to.Ref.Title}:{to.Path}\n\n{string.Join("\n", clash.Take(10).Select(c => c.Name))}{(clash.Count > 10 ? "\n…" : "")}\n\nInside folders, the same choice applies to every file.",
                new[] { "Overwrite", "Skip existing", "Keep both (rename)", "Cancel" }, cancelIndex: 3);
            if (pick is < 0 or 3) return;
            policy = (Conflict)pick;
        }
        else if (!Dialogs.Confirm(move ? "Move" : "Copy",
                     $"{(move ? "Move" : "Copy")} {(sel.Count == 1 ? sel[0].Name : sel.Count + " items")}\nfrom {from.Ref.Title}:{from.Path}\nto     {to.Ref.Title}:{to.Path}" +
                     (move ? "\n\nThe originals are deleted only after everything has arrived." : ""), move ? "Move" : "Copy"))
            return;

        TransferQueue.Enqueue(new TransferJob { From = from.Ref, To = to.Ref, Items = sel, DestDir = to.Path, Move = move, OnConflict = policy });
    }

    public void Dispose()
    {
        TransferQueue.Jobs.CollectionChanged -= OnJobsChanged;
        TransferQueue.Finished -= OnJobFinished;
        _left.Dispose(); _right.Dispose();
    }
}

/// <summary>One side of the Commander: a place (this PC / a host), a folder, a filterable sortable list.</summary>
internal sealed partial class FilePane : Border, IDisposable
{
    private readonly CommanderView _owner;
    private readonly string _side;
    private IFileSystem _fs;
    private string _path = "";
    private readonly Stack<(FsRef, string)> _back = new(), _fwd = new();
    private readonly ComboBox _source = new() { MinWidth = 170, Margin = new Thickness(0, 0, 6, 0) };
    private readonly TextBox _pathBox = new() { Padding = new Thickness(6, 4, 6, 4) };
    private readonly TextBox _filter = Hint.Set(new TextBox { Padding = new Thickness(6, 3, 6, 3), Width = 150, ToolTip = "Filter this folder (name contains; * and ? wildcards)" }, "Filter…");
    private readonly ListView _list = new() { SelectionMode = SelectionMode.Extended, AllowDrop = true };
    private readonly TextBlock _status = new TextBlock() { Margin = new Thickness(2, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis }.Fs(FontStep.Meta);
    private List<FsItem> _items = new();
    private string _sort = "Name"; private bool _desc;
    private bool _loading, _suppressSource;
    private CancellationTokenSource? _listCts;

    public FsRef Ref => _fs.Ref;
    public string Path => _path;
    public IReadOnlyList<FsItem> Items => _items;
    public IEnumerable<FsItem> Selected => _list.SelectedItems.OfType<FsItem>();
    public string State => $"{Ref}|{_path}";

    public FilePane(CommanderView owner, string side, string state)
    {
        _owner = owner; _side = side;
        var parts = state.Split('|', 2);
        var r = FsRef.Parse(parts[0]);
        if (r.Kind == "host" && r.Host == null) r = FsRef.None;   // a deleted host: not connected, never somewhere else
        _fs = r.Open();
        _path = parts.Length > 1 ? parts[1] : "";

        BorderThickness = new Thickness(1); CornerRadius = new CornerRadius(6); Padding = new Thickness(8);
        SetResourceReference(BorderBrushProperty, "Line");
        SetResourceReference(BackgroundProperty, "Surface");
        _pathBox.Mono();
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");

        FillSources();
        _source.SelectionChanged += async (_, _) =>
        {
            if (_suppressSource || _source.SelectedItem is not ComboBoxItem { Tag: FsRef target } || target.Equals(Ref)) return;
            await OpenAsync(target, "");
        };

        var nav = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 6, 0) };
        Button N(string t, string tip, Func<Task> a) { var b = new Button { Content = t, ToolTip = tip, Style = (Style)FindResource("SmallButton"), Margin = new Thickness(0, 0, 3, 0), MinWidth = 30 }; b.Click += async (_, _) => await a(); nav.Children.Add(b); return b; }
        N("◀", "Back (Alt+Left)", BackAsync);
        N("▶", "Forward (Alt+Right)", ForwardAsync);
        N("↑", "Up (Backspace)", () => GoAsync(_fs.Parent(_path)));
        N("⌂", "Home folder", async () => await GoAsync(await _fs.HomeAsync()));
        N("↻", "Refresh (Ctrl+R)", RefreshAsync);

        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(_source, Dock.Left); DockPanel.SetDock(nav, Dock.Left);
        top.Children.Add(_source); top.Children.Add(nav); top.Children.Add(_pathBox);
        _pathBox.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await GoAsync(_pathBox.Text.Trim()); FocusList(); } };

        var filterRow = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var filterLbl = new TextBlock { Text = "FILTER", Style = (Style)FindResource("Label"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        DockPanel.SetDock(filterLbl, Dock.Left); DockPanel.SetDock(_filter, Dock.Left);
        filterRow.Children.Add(filterLbl); filterRow.Children.Add(_filter); filterRow.Children.Add(new FrameworkElement());
        _filter.TextChanged += (_, _) => Apply();
        _filter.KeyDown += (_, e) => { if (e.Key == Key.Escape) { _filter.Clear(); FocusList(); } else if (e.Key == Key.Down) FocusList(); };

        var gv = new GridView();
        void Col(string header, string prop, double w, string sortKey)
        {
            var h = new GridViewColumnHeader { Content = header, Tag = sortKey };
            h.Click += (_, _) => { if (_sort == sortKey) _desc = !_desc; else { _sort = sortKey; _desc = false; } Apply(); };
            gv.Columns.Add(new GridViewColumn { Header = h, Width = w, DisplayMemberBinding = new Binding(prop) });
        }
        Col("", nameof(FsItem.Icon), 30, "Type");
        Col("Name", nameof(FsItem.Shown), 260, "Name");
        Col("Size", nameof(FsItem.SizeText), 90, "Size");
        Col("Modified", nameof(FsItem.ModifiedText), 130, "Modified");
        Col("Perms", nameof(FsItem.Perms), 90, "Perms");
        _list.View = gv;
        _list.GotKeyboardFocus += (_, _) => _owner.SetActive(this);
        _list.PreviewMouseDown += (_, _) => _owner.SetActive(this);
        _list.PreviewMouseLeftButtonDown += (_, _) => MouseTakesOver();   // keyboard marking / quick search end; the click's selection counts
        _list.MouseDoubleClick += async (_, e) => { if (e.OriginalSource is FrameworkElement { DataContext: FsItem }) await OpenSelectedAsync(); };
        _list.SelectionChanged += (_, _) => _owner.SelectionMoved(this);
        _list.PreviewKeyDown += (_, e) => { if (McListKey(e)) e.Handled = true; };   // marking keys, before the list's own handling
        _list.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; await OpenSelectedAsync(); }
            else if (e.Key == Key.Back) { e.Handled = true; await GoAsync(_fs.Parent(_path)); }
            else if (e.Key == Key.Left && Keyboard.Modifiers == ModifierKeys.Alt) { e.Handled = true; await BackAsync(); }
            else if (e.Key == Key.Right && Keyboard.Modifiers == ModifierKeys.Alt) { e.Handled = true; await ForwardAsync(); }
        };
        _list.TextInput += (_, e) =>
        {
            if (QuickSearchKey(e.Text)) { e.Handled = true; return; }   // MC quick search (Ctrl+S): jump, don't filter
            if (_filter.Text.Length == 0 && McGroupKey(e.Text)) { e.Handled = true; return; }
            if (e.Text.Length == 1 && !char.IsControl(e.Text[0])) { _filter.Focus(); _filter.Text += e.Text; _filter.CaretIndex = _filter.Text.Length; e.Handled = true; }
        };
        _list.PreviewKeyDown += (_, e) => { if (QuickSearching && e.Key is Key.Escape or Key.Enter or Key.Up or Key.Down or Key.Left or Key.Right or Key.Back) { EndQuickSearch(); if (e.Key == Key.Escape) e.Handled = true; } };
        Menus.SelectOnRightClick(_list);
        Menus.Attach(_list, BuildMenu);
        SetupDragDrop();

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top); DockPanel.SetDock(filterRow, Dock.Top); DockPanel.SetDock(_status, Dock.Bottom);
        var body = new Grid();
        body.Children.Add(_list); body.Children.Add(BuildQuickView());
        root.Children.Add(top); root.Children.Add(filterRow); root.Children.Add(_status); root.Children.Add(body);
        Child = root;
        Loaded += async (_, _) => { if (_items.Count == 0 && !_loading) await GoAsync(_path, record: false); };
        // 0.5.0: hosts added or renamed elsewhere appear in the source list at once; a local folder follows changes on disk.
        Live.While(this, h => AppData.Changed += h, h => AppData.Changed -= h, FillSources);
        Unloaded += (_, _) => StopWatch();
        Loaded += (_, _) => Watch();
    }

    // ---- live local folders (0.5.0) ----------------------------------------------------------------------------------

    private FileSystemWatcher? _watch;
    private System.Windows.Threading.DispatcherTimer? _watchDebounce;

    /// <summary>Watches the shown local folder (not its subfolders) and re-reads it, keeping the selection, half a second after
    /// the last change: files copied in from Explorer, a download landing, an editor saving. Remote panes refresh after every
    /// operation the app does; they are not polled.</summary>
    private void Watch()
    {
        StopWatch();
        if (_fs.IsRemote || InArchive || _panel != null || _path.Length == 0 || !IsLoaded) return;
        try
        {
            var w = new FileSystemWatcher(_path) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite, InternalBufferSize = 16384 };
            _watchDebounce ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _watchDebounce.Tick -= OnWatchTick; _watchDebounce.Tick += OnWatchTick;
            void Changed(object? s, EventArgs e) => Dispatcher.BeginInvoke(() => { _watchDebounce.Stop(); _watchDebounce.Start(); });
            w.Created += Changed; w.Deleted += Changed; w.Changed += Changed; w.Renamed += Changed; w.Error += Changed;
            w.EnableRaisingEvents = true;
            _watch = w;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException) { }   // a drive list, a vanished folder: nothing to watch
    }

    private void OnWatchTick(object? s, EventArgs e) { _watchDebounce?.Stop(); if (IsLoaded && !_loading) _ = RefreshAsync(); }

    private void StopWatch() { _watchDebounce?.Stop(); if (_watch is { } w) { _watch = null; w.EnableRaisingEvents = false; w.Dispose(); } }

    private void FillSources()
    {
        _suppressSource = true;
        _source.Items.Clear();
        _source.Items.Add(new ComboBoxItem { Content = "○ Not connected", Tag = FsRef.None, ToolTip = "Disconnect this pane (choose a place when you need one)" });
        _source.Items.Add(new ComboBoxItem { Content = "💻 This PC", Tag = FsRef.Local });
        foreach (var h in AppData.Current.Hosts.Where(h => h.Protocol != "rdp").OrderBy(h => h.Group).ThenBy(h => h.Display))
            _source.Items.Add(new ComboBoxItem { Content = $"🖧 {h.Display}", Tag = FsRef.ForHost(h), ToolTip = $"{h.Username}@{h.Endpoint}  ·  {(h.Group.Length > 0 ? h.Group : "no folder")}" });
        _source.SelectedItem = _source.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, Ref));
        _suppressSource = false;
    }

    public void SetHighlight(bool on) { if (on) SetResourceReference(BorderBrushProperty, "Accent"); else SetResourceReference(BorderBrushProperty, "Line"); }
    public void FocusList()
    {
        _list.Focus();
        if (_list.SelectedIndex < 0 && _list.Items.Count > 0) _list.SelectedIndex = 0;
        if (_list.ItemContainerGenerator.ContainerFromIndex(Math.Max(0, _list.SelectedIndex)) is ListViewItem li) li.Focus();
    }

    // ---- navigation ----------------------------------------------------------------------------------------------

    public async Task OpenAsync(FsRef target, string path)
    {
        if (!target.Equals(Ref))
        {
            _back.Push((Ref, _path)); _fwd.Clear();
            var old = _fs;
            try { _fs = target.Open(); } catch (Exception ex) { Dialogs.Error("Open", ex.Message); return; }
            old.Dispose();
            _path = "";
            _suppressSource = true;
            _source.SelectedItem = _source.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, target));
            _suppressSource = false;
            await GoAsync(path, record: false);
        }
        else await GoAsync(path);
    }

    public Task RefreshAsync()
    {
        if (InArchive || _panel != null) { Apply(); return Task.CompletedTask; }   // a fixed listing: nothing to re-read
        return GoAsync(_path, record: false, keepSelection: true);
    }

    private async Task BackAsync()
    {
        if (_back.Count == 0) return;
        var (r, p) = _back.Pop();
        _fwd.Push((Ref, _path));
        await Jump(r, p);
    }

    private async Task ForwardAsync()
    {
        if (_fwd.Count == 0) return;
        var (r, p) = _fwd.Pop();
        _back.Push((Ref, _path));
        await Jump(r, p);
    }

    private async Task Jump(FsRef r, string p)
    {
        if (!r.Equals(Ref)) { var old = _fs; _fs = r.Open(); old.Dispose(); _suppressSource = true; _source.SelectedItem = _source.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, r)); _suppressSource = false; }
        await GoAsync(p, record: false);
    }

    public async Task GoAsync(string path, bool record = true, bool keepSelection = false)
    {
        _listCts?.Cancel();
        var cts = _listCts = new CancellationTokenSource();
        var keep = keepSelection ? Selected.Select(i => i.Name).ToHashSet() : null;
        var from = _path;
        var cameFromPath = _arc != null ? _arcFile : _path;   // leaving an archive selects the archive file
        LeaveModes();
        if (!keepSelection) { _marking = false; _cursor = null; }
        if (Ref.IsNone)
        {
            // 0.5.1 #4: a pane that isn't connected shows nothing and says how to start.
            _items = new(); _path = ""; _pathBox.Text = ""; _list.ItemsSource = Array.Empty<FsItem>(); _loading = false;
            _status.Text = "○ Not connected — choose This PC or a host in the list on the left.";
            return;
        }
        _loading = true;
        _status.Text = (_fs.IsRemote ? "connecting / " : "") + "listing…";
        try
        {
            if (path.Length == 0 && _fs.IsRemote) path = await _fs.HomeAsync(cts.Token);
            var items = await _fs.ListAsync(path, cts.Token);
            if (cts.IsCancellationRequested) return;
            if (record && !path.Equals(from, StringComparison.Ordinal)) { _back.Push((Ref, from)); _fwd.Clear(); }
            if (!path.Equals(_path, StringComparison.Ordinal)) _sizes.Clear();
            _path = path;
            _items = items;
            Visited(path);
            Watch();
            _pathBox.Text = path.Length == 0 ? "(drives)" : path;
            if (!keepSelection) _filter.Text = "";
            Apply();
            if (keep != null) foreach (var i in _list.Items.OfType<FsItem>().Where(i => keep.Contains(i.Name))) _list.SelectedItems.Add(i);
            else if (!cameFromPath.Equals(path) && _list.Items.OfType<FsItem>().FirstOrDefault(i => i.FullPath.TrimEnd('/', '\\').Equals(cameFromPath.TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase)) is { } cameFrom)
            { _list.SelectedItem = cameFrom; _list.ScrollIntoView(cameFrom); }
            _owner.Remember();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _status.Text = "✕ " + ex.Message.Split('\n')[0];
            if (_items.Count == 0 && path.Length > 0 && !_fs.IsRemote) { _path = ""; }
        }
        finally { if (_listCts == cts) _loading = false; }
    }

    private void Apply()
    {
        bool hidden = AppSettings.Current.CommanderShowHidden;
        var f = _filter.Text.Trim();
        System.Text.RegularExpressions.Regex? wild = f.Contains('*') || f.Contains('?')
            ? new System.Text.RegularExpressions.Regex("^" + System.Text.RegularExpressions.Regex.Escape(f).Replace("\\*", ".*").Replace("\\?", ".") + "$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            : null;
        var source = Listing().Select(i => i.IsDir && _sizes.TryGetValue(i.FullPath, out var n) ? i.WithDirSize(n) : i).ToList();
        IEnumerable<FsItem> q = source.Where(i => hidden || !(i.Name.StartsWith('.') || i.Perms.StartsWith('h')));
        if (f.Length > 0) q = q.Where(i => wild?.IsMatch(i.Name) ?? i.Name.Contains(f, StringComparison.OrdinalIgnoreCase));
        Func<FsItem, object> key = _sort switch
        {
            "Size" => i => i.DirSize ?? i.Length, "Modified" => i => i.Modified, "Perms" => i => i.Perms, "Type" => i => i.Ext,
            "Unsorted" => i => 0, "Ext" => i => i.Ext + "\0" + i.Name.ToLowerInvariant(),
            _ => i => i.Shown.ToLowerInvariant(),
        };
        var sorted = (_desc ? q.OrderByDescending(key) : q.OrderBy(key)).OrderByDescending(i => i.IsDir).ToList();
        if (ParentItem() is { } up) sorted.Insert(0, up);
        _list.ItemsSource = sorted;
        int dirs = source.Count(i => i.IsDir), files = source.Count - dirs;
        _status.Text = $"{PlaceText} ·{dirs} folders, {files} files · {Fmt.Bytes(source.Where(i => !i.IsDir).Sum(i => i.Length))}" +
                       (_sizes.Count > 0 ? $" · measured folders {Fmt.Bytes(_sizes.Values.Sum())}" : "") +
                       (f.Length > 0 ? $" · filter “{f}”: {sorted.Count(i => !i.IsParent)} shown" : "") + (hidden ? "" : " · hidden files off (Ctrl+H)");
    }

    private async Task OpenSelectedAsync()
    {
        if (Current is not FsItem it) return;
        if (await OpenSpecialAsync(it)) return;   // archives, find results, the ".." of either
        if (it.IsDir || it.IsLink && await _fs.StatAsync(it.FullPath) is { IsDir: true }) { await GoAsync(it.FullPath); return; }
        await ViewAsync();
    }

    // ---- file operations ------------------------------------------------------------------------------------------

    // View / edit / rename act on the current item (the cursor, also while items are marked), like Midnight Commander.
    private FsItem? One => Current is { IsParent: false, IsDrive: false } c ? c : Selected.FirstOrDefault(i => !i.IsParent && !i.IsDrive);

    private async Task<string?> ReadTextAsync(FsItem it, int max = 4 * 1024 * 1024)
    {
        if (it.IsDir) return null;
        if (it.Length > max) { Dialogs.Info("View", $"{it.Name} is {Fmt.Bytes(it.Length)} — over {Fmt.Bytes(max)}. Copy it to this PC and open it there."); return null; }
        await using var s = await _fs.OpenReadAsync(it.FullPath);
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms);
        var bytes = ms.ToArray();
        if (bytes.Take(8000).Any(b => b == 0))
        {
            // Binary: the hex editor instead (0.3.0 §13).
            if (Dialogs.Confirm("Binary file", $"{it.Name} looks like a binary file. Open it in the hex editor?", "Open in hex editor")) await HexAsync(it);
            return null;
        }
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>The hex editor for a file of this pane (in memory; Save writes it back, with the changed-since-opened check).</summary>
    public async Task HexAsync(FsItem? item = null)
    {
        if ((item ?? One) is not { IsDir: false } it) return;
        if (InArchive) { await ArchiveOpenAsync(it, hex: true); return; }
        if (it.Length > Editors.HexDocument.MaxBytes) { Dialogs.Info("Hex editor", $"{it.Name} is {Fmt.Bytes(it.Length)} — the hex editor opens files up to {Fmt.Bytes(Editors.HexDocument.MaxBytes)}."); return; }
        try
        {
            var fs = _fs; var path = it.FullPath;
            async Task<byte[]?> Read() { try { await using var s = await fs.OpenReadAsync(path); using var ms = new MemoryStream(); await s.CopyToAsync(ms); return ms.ToArray(); } catch { return null; } }
            var data = await Read() ?? throw new IOException("the file could not be read");
            HexEditorWindow.Open($"{Ref.Title}:{path}", data, async bytes =>
            {
                await using (var w = await fs.CreateAsync(path)) await w.WriteAsync(bytes);
                AuditLog.Write("you", Ref.Title, "edited (hex) " + path);
                _ = RefreshAsync();
            }, Read);
        }
        catch (Exception ex) { Dialogs.Error("Hex editor", ex.Message); }
    }

    public async Task ViewAsync()
    {
        if (One is not { IsDir: false } it) return;
        if (InArchive) { await ArchiveOpenAsync(it, hex: false); return; }
        try
        {
            if (it.Ext == "pdf")
            {
                // PDFs open in the in-app document viewer, read into memory (never cached to disk).
                if (it.Length > 64L * 1024 * 1024) { Dialogs.Info("View", $"{it.Name} is {Fmt.Bytes(it.Length)} — copy it to this PC and open it there."); return; }
                await using var s = await _fs.OpenReadAsync(it.FullPath);
                using var ms = new MemoryStream();
                await s.CopyToAsync(ms);
                DocumentViewerWindow.Open(DocumentSource.Pdf($"{it.Name} — {Ref.Title}", it.Name, ms.ToArray()));
                return;
            }
            if (await ReadTextAsync(it) is { } text) Dialogs.ShowText($"{Ref.Title}:{it.FullPath}", text);
        }
        catch (Exception ex) { Dialogs.Error("View", ex.Message); }
    }

    public async Task EditAsync(FsItem? item = null)
    {
        if ((item ?? One) is not { IsDir: false } it || ReadOnlyHere("Edit")) return;
        try
        {
            if (await ReadTextAsync(it) is not { } text) return;
            var fs = _fs;
            TextEditorWindow.Open($"{Ref.Title}:{it.FullPath}", text, async edited =>
            {
                await using (var w = await fs.CreateAsync(it.FullPath))
                    await w.WriteAsync(new UTF8Encoding(false).GetBytes(edited));
                AuditLog.Write("you", Ref.Title, "edited " + it.FullPath);
                _ = RefreshAsync();
            }, Ref.Kind == "local" || Ref.Host is { IsAdHoc: false } ? Ref : null, it.FullPath);   // ad-hoc hosts can't be found again
        }
        catch (Exception ex) { Dialogs.Error("Edit", ex.Message + (_fs.IsRemote ? "\n\nRoot-owned files: edit them from a terminal with sudoedit." : "")); }
    }

    public async Task MkdirAsync()
    {
        if (_path.Length == 0 || ReadOnlyHere("New folder")) return;
        if (Dialogs.Prompt("New folder", $"Folder name in {Ref.Title}:{_path}") is not { Length: > 0 } name) return;
        try { await _fs.MkdirAsync(_fs.Join(_path, name)); AuditLog.Write("you", Ref.Title, "created folder " + _fs.Join(_path, name)); }
        catch (Exception ex) { Dialogs.Error("New folder", ex.Message); }
        await RefreshAsync();
    }

    public async Task RenameAsync()
    {
        if (One is not { } it || ReadOnlyHere("Rename")) return;
        if (Dialogs.Prompt("Rename", $"New name for {it.Name}:", it.Name) is not { Length: > 0 } name || name == it.Name) return;
        if (name.IndexOfAny(new[] { '/', '\\' }) >= 0) { Dialogs.Error("Rename", "Use F6 (Move) to put it in another folder."); return; }
        try { await _fs.RenameAsync(it.FullPath, _fs.Join(_path, name)); AuditLog.Write("you", Ref.Title, $"renamed {it.FullPath} → {name}"); }
        catch (Exception ex) { Dialogs.Error("Rename", ex.Message); }
        await RefreshAsync();
    }

    public async Task DeleteAsync()
    {
        var sel = Selected.Where(i => !i.IsParent && !i.IsDrive).ToList();
        if (sel.Count == 0 || ReadOnlyHere("Delete")) return;
        int dirs = sel.Count(i => i.IsDir);
        var where = _fs.IsRemote ? $"on {Ref.Title} — permanently (SFTP has no recycle bin)" : "to the Recycle Bin";
        if (!Envs.Confirm(_fs.IsRemote ? Ref.Host : null, "Delete", $"Delete {sel.Count} item(s) {where}?\n\n{string.Join("\n", sel.Take(12).Select(i => i.FullPath))}{(sel.Count > 12 ? "\n…" : "")}" +
                (dirs > 0 ? $"\n\n{dirs} folder(s) with everything inside." : ""), "Delete", danger: true, typeToConfirm: _fs.IsRemote && dirs > 0 ? "delete" : null)) return;
        try
        {
            _status.Text = "deleting…";
            foreach (var i in sel) await _fs.DeleteAsync(i.FullPath, i.IsDir && !i.IsLink);
            AuditLog.Write("you", Ref.Title, $"deleted {string.Join(", ", sel.Select(i => i.FullPath))}", _fs.IsRemote ? "permanent" : "recycle bin");
        }
        catch (Exception ex) { Dialogs.Error("Delete", ex.Message); }
        await RefreshAsync();
    }

    private void Properties(FsItem it)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Name:      {it.Name}");
        sb.AppendLine($"Where:     {Ref.Title}");
        sb.AppendLine($"Path:      {it.FullPath}");
        sb.AppendLine($"Type:      {(it.IsDrive ? "drive" : it.IsDir ? "folder" : it.IsLink ? "symbolic link" : "file")}");
        if (!it.IsDir) sb.AppendLine($"Size:      {Fmt.Bytes(it.Length)} ({it.Length:N0} bytes)");
        if (it.Modified != default) sb.AppendLine($"Modified:  {it.Modified:yyyy-MM-dd HH:mm:ss}");
        if (it.Perms.Length > 0) sb.AppendLine($"{(_fs.IsRemote ? "Mode" : "Attributes")}:  {it.Perms}");
        if (_fs.IsRemote && it.Owner.Length > 0) sb.AppendLine($"Owner:     uid:gid {it.Owner}");
        Dialogs.ShowText("Properties — " + it.Name, sb.ToString());
    }

    private async Task ChmodAsync(List<FsItem> items)
    {
        if (_fs is not HostFs sftp || items.Count == 0 || ReadOnlyHere("chmod")) return;
        if (Dialogs.Prompt("chmod", "Octal mode, e.g. 644, 755, 600:", "644") is not { Length: 3 } mode || !mode.All(c => c is >= '0' and <= '7')) return;
        try
        {
            var r = await SshPool.RunAsync(sftp.Host, "chmod " + mode + " -- " + string.Join(" ", items.Select(i => RemoteExec.Quote(i.FullPath))));
            if (!r.Ok) throw new InvalidOperationException(r.Combined);
            AuditLog.Write("you", Ref.Title, $"chmod {mode} {string.Join(", ", items.Select(i => i.FullPath))}");
        }
        catch (Exception ex) { Dialogs.Error("chmod", ex.Message); }
        await RefreshAsync();
    }

    private async Task ExplainAsync(IAssistant assist, FsItem it)
    {
        try
        {
            if (it.Ext == "pdf")
            {
                // Real extraction (PdfPig) + the "data being sent" preview; a scanned PDF says OCR is needed instead of pretending.
                if (it.Length > Documents.PdfText.MaxBytes) { Dialogs.Info("AI", $"{it.Name} is {Fmt.Bytes(it.Length)} — too large to extract here."); return; }
                await using var s = await _fs.OpenReadAsync(it.FullPath);
                using var ms = new MemoryStream();
                await s.CopyToAsync(ms);
                await assist.ReviewPdfAsync($"{it.Name} on {Ref.Title}", ms.ToArray(), "Summarise this PDF: what it is, the key facts and numbers, and anything unusual or that needs action.", st => MainWindow.Instance?.Status($"{it.Name}: {st}"));
                return;
            }
            if (await ReadTextAsync(it, 512 * 1024) is not { } text) return;
            await assist.AskAsync($"{it.Name} on {Ref.Title}", text,
                $"Explain this file ({it.FullPath}): what it is, what it configures or contains, and anything that looks wrong, insecure or unusual. If it is a log, summarise the important events and errors.");
        }
        catch (Exception ex) { Dialogs.Error("AI", ex.Message); }
    }

    private IEnumerable<object?> BuildMenu()
    {
        var sel = Selected.Where(i => !i.IsParent).ToList();
        var one = sel.Count == 1 ? sel[0] : null;
        var other = _owner.Other(this);
        bool any = sel.Any(i => !i.IsDrive);
        var target = $"{other.Ref.Title}:{(other.Path.Length > 0 ? other.Path : "(choose a folder)")}";
        var assist = Assist.Current;
        var ai = assist?.WhyNot();
        return new object?[]
        {
            one is { IsDir: true } ? Menus.Item("Open", () => _ = GoAsync(one.FullPath), "Enter") : null,
            // 0.5.0 (ADR-065): the folder (or this one) in VS Code and similar, locally or through Remote-SSH.
            Menus.AsyncItem($"⧉ Open {(one is { IsDir: true } ? "folder" : "this folder")} in {IdeUi.EditorTitle ?? "an IDE"}", async () =>
            {
                var folder = one is { IsDir: true } ? one.FullPath : _path;
                if (Ref.Kind == "host" && Ref.Host is { } h) await IdeUi.OpenHostAsync(h, folder);
                else if (folder.Length > 0) IdeUi.OpenLocal(folder);
            }, enabled: !InArchive && (_path.Length > 0 || one is { IsDir: true })),
            one is { IsDir: false } ? Menus.AsyncItem("View", ViewAsync, "F3") : null,
            one is { IsDir: false } ? Menus.AsyncItem("Edit", () => EditAsync(), "F4") : null,
            one is { IsDir: false } ? Menus.AsyncItem("Hex editor", () => HexAsync(), "Ctrl+F4") : null,
            one is { IsDir: false } && !_fs.IsRemote ? Menus.Item("Open with default app", () => Process.Start(new ProcessStartInfo(one.FullPath) { UseShellExecute = true })) : null,
            Menus.Sep(),
            Menus.AsyncItem($"Copy to {target}", () => _owner.TransferAsync(false, this), "F5", enabled: any),
            Menus.AsyncItem($"Move to {target}", () => _owner.TransferAsync(true, this), "F6", enabled: any),
            Menus.AsyncItem("Rename…", RenameAsync, "F2", enabled: one != null && !one.IsDrive),
            Menus.AsyncItem("New folder…", MkdirAsync, "F7", enabled: _path.Length > 0),
            _fs.IsRemote ? Menus.AsyncItem("chmod…", () => ChmodAsync(sel), enabled: any) : null,
            Menus.Sep(),
            Menus.Group("Copy & open elsewhere",
            Menus.Item("Copy path", () => Menus.Clip(sel.Count > 0 ? string.Join("\n", sel.Select(i => i.FullPath)) : _path)),
            Menus.Item("Copy name", () => Menus.Clip(string.Join("\n", sel.Select(i => i.Name))), enabled: sel.Count > 0),
            _fs is HostFs s1 && any ? Menus.Item("Copy scp command", () => Menus.Clip(string.Join("\n", sel.Select(i =>
                $"scp {(i.IsDir ? "-r " : "")}{(s1.Host.Port != 22 ? $"-P {s1.Host.Port} " : "")}{s1.Host.Username}@{s1.Host.Address}:{RemoteExec.Quote(i.FullPath)} .")))) : null,
            _fs is SftpFs s2 ? Menus.Item("⌨ Terminal here", () => MainWindow.Instance.OpenTerminal(s2.Host, "cd " + RemoteExec.Quote(one is { IsDir: true } ? one.FullPath : _path))) : null,
            !_fs.IsRemote && _path.Length > 0 ? Menus.Item("Open in File Explorer", () => Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { one != null ? "/select," + one.FullPath : _path } })) : null,
            Menus.Item("Open this folder in the other pane", () => _ = other.OpenAsync(Ref, one is { IsDir: true } ? one.FullPath : _path))),
            Menus.Group("More file tools", McMenu(sel, one).ToArray()),
            one != null ? Menus.Item("Properties", () => Properties(one), "Alt+Enter") : null,
            Menus.Sep(),
            one is { IsDir: false } && assist != null ? Menus.AsyncItem("✦ Explain with AI", () => ExplainAsync(assist, one), enabled: ai == null) : null,
            ai != null && one is { IsDir: false } ? new MenuItem { Header = "   " + ai, IsEnabled = false } : null,
            Menus.Sep(),
            Menus.AsyncItem(_fs.IsRemote ? "Delete permanently…" : "Delete (Recycle Bin)…", DeleteAsync, "F8", enabled: any),
        };
    }

    // ---- drag & drop ----------------------------------------------------------------------------------------------

    private Point _dragStart;
    private const string DragFormat = "DcxCommanderItems";

    private void SetupDragDrop()
    {
        _list.PreviewMouseLeftButtonDown += (_, e) => _dragStart = e.GetPosition(null);
        _list.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            var d = e.GetPosition(null) - _dragStart;
            if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            if (e.OriginalSource is not DependencyObject src || Menus.Ancestor<ListViewItem>(src) == null) return;
            var sel = Selected.Where(i => !i.IsParent && !i.IsDrive).ToList();
            if (sel.Count == 0) return;
            var data = new DataObject(DragFormat, new DragPayload(this, sel));
            DragDrop.DoDragDrop(_list, data, DragDropEffects.Copy | DragDropEffects.Move);
        };
        _list.DragOver += (_, e) =>
        {
            bool ours = e.Data.GetData(DragFormat) is DragPayload p && p.From != this;
            bool files = e.Data.GetDataPresent(DataFormats.FileDrop);
            e.Effects = ours || files ? (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? DragDropEffects.Move : DragDropEffects.Copy) : DragDropEffects.None;
            e.Handled = true;
        };
        _list.Drop += async (_, e) =>
        {
            bool move = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            if (ReadOnlyHere("Drop")) return;
            if (e.Data.GetData(DragFormat) is DragPayload p && p.From != this) { await _owner.TransferAsync(move, p.From, p.Items, this); return; }
            if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
            {
                // From Windows Explorer: always a copy from this PC.
                var items = paths.Select(x => Directory.Exists(x)
                    ? new FsItem { Name = System.IO.Path.GetFileName(x.TrimEnd('\\')), FullPath = x, IsDir = true }
                    : new FsItem { Name = System.IO.Path.GetFileName(x), FullPath = x, Length = new FileInfo(x).Length }).ToList();
                if (_path.Length == 0) { Dialogs.Info("Copy", "Open a folder first."); return; }
                if (!Dialogs.Confirm("Copy", $"Copy {items.Count} item(s) from this PC to {Ref.Title}:{_path}?", "Copy")) return;
                TransferQueue.Enqueue(new TransferJob { From = FsRef.Local, To = Ref, Items = items, DestDir = _path });
            }
        };
    }

    private sealed record DragPayload(FilePane From, List<FsItem> Items);

    public void Dispose() { _listCts?.Cancel(); StopWatch(); _fs.Dispose(); }
}
