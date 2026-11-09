using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using DcxManager.Services;
using DcxManager.Views;
using DcxManager.WinTools.Wmi;

namespace DcxManager.WinTools.Views;

/// <summary>
/// Windows management (0.6.39, ADR-119): an MMC-style console in a tab. On the left is the tree of snap-ins (System,
/// Event Viewer, Storage, Local users and groups, Network, Scheduled tasks, Tools); on the right is what the selected
/// one shows, with a filter, its actions and a grid. The toolbar picks the computer: this PC, a saved host (with its
/// saved credential) or any computer by name (your Windows sign-in). Reading is free; every change is confirmed,
/// audited and follows WinToolsAllowChanges. Loads run off the UI thread and can be cancelled by picking another node.
/// </summary>
public sealed class WinToolsView : UserControl, IDisposable
{
    private readonly TreeView _tree = new() { MinWidth = 220 };
    private readonly ComboBox _computer = new() { Width = 300, DisplayMemberPath = nameof(Choice.Label) };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _title = new();
    private readonly TextBlock _about = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 6) };
    private readonly TextBox _filter = new() { Width = 260, ToolTip = "Shows only the rows containing this text" };
    private readonly CheckBox _problems = new() { Content = "Errors and warnings only", Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly WrapPanel _actions = new() { Margin = new Thickness(0, 6, 0, 6) };
    private readonly DataGrid _grid = new() { IsReadOnly = true, AutoGenerateColumns = false, HeadersVisibility = DataGridHeadersVisibility.Column, SelectionMode = DataGridSelectionMode.Single, CanUserSortColumns = true, EnableRowVirtualization = true, EnableColumnVirtualization = true };
    private readonly TextBlock _count = new() { Margin = new Thickness(0, 4, 0, 0) };
    private readonly StackPanel _consoleBar = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 4) };
    private readonly TextBox _command = new() { MinWidth = 420, ToolTip = "A WQL query (SELECT …) or a wmic command (os get caption,version)" };
    private readonly TextBox _namespace = new() { Text = @"root\cimv2", Width = 160, ToolTip = "The WMI namespace" };
    private readonly TextBlock _wql = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };

    private WinTarget _target = WinTarget.Local();
    private Snapin? _snap;
    private WmiTable _table = new();
    private CancellationTokenSource? _load;
    private bool _disposed;

    public sealed record Choice(string Label, HostEntry? Host, string? Computer);

    public WinToolsView()
    {
        foreach (var t in new[] { _title }) t.SetResourceReference(StyleProperty, "H2");
        foreach (var t in new[] { _about, _status, _count, _wql }) t.SetResourceReference(StyleProperty, "Hint");
        _filter.SetResourceReference(BackgroundProperty, "Input");
        _command.SetResourceReference(BackgroundProperty, "Input");
        _namespace.SetResourceReference(BackgroundProperty, "Input");
        System.Windows.Automation.AutomationProperties.SetName(_filter, "Filter");
        System.Windows.Automation.AutomationProperties.SetName(_command, "WMI command");
        System.Windows.Automation.AutomationProperties.SetName(_computer, "Computer");

        // Header + toolbar
        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        var h1 = new TextBlock { Text = "Windows management" };
        h1.SetResourceReference(StyleProperty, "H1");
        var blurb = new TextBlock { Text = "Services, processes, event logs, storage, users and groups, shares, scheduled tasks, network and WMI — for this PC or another Windows computer. Changes are confirmed and audited.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 8) };
        blurb.SetResourceReference(StyleProperty, "Hint");
        var bar = new WrapPanel();
        bar.Children.Add(new TextBlock { Text = "Computer:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        bar.Children.Add(_computer);
        bar.Children.Add(Btn("Connect to…", async () => await ConnectToAsync(), "Manage another Windows computer by name, with your Windows sign-in"));
        bar.Children.Add(Btn("↻ Refresh", async () => await ReloadAsync(), "Load the selected view again (F5)"));
        bar.Children.Add(_status);
        head.Children.Add(h1); head.Children.Add(blurb); head.Children.Add(bar);

        // Tree
        string? group = null;
        TreeViewItem? groupItem = null;
        foreach (var s in Snapins.All)
        {
            if (s.Id == "ev-security" && !WinToolsPolicy.SecurityLogAllowed) continue;
            if (s.Id == Snapins.ConsoleId && WinToolsPolicy.ConsoleBlocked() is not null) continue;
            if (s.Group != group)
            {
                group = s.Group;
                groupItem = new TreeViewItem { Header = group, IsExpanded = true, Focusable = false, FontWeight = FontWeights.SemiBold };
                _tree.Items.Add(groupItem);
            }
            groupItem!.Items.Add(new TreeViewItem { Header = s.Glyph + "  " + s.Title, Tag = s, ToolTip = s.About, FontWeight = FontWeights.Normal });
        }
        _tree.SelectedItemChanged += async (_, e) => { if (e.NewValue is TreeViewItem { Tag: Snapin s }) await ShowAsync(s); };

        // Right side
        var filterRow = new WrapPanel();
        filterRow.Children.Add(new TextBlock { Text = "Filter:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        filterRow.Children.Add(_filter);
        filterRow.Children.Add(_problems);
        var typing = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        typing.Tick += (_, _) => { typing.Stop(); Render(); };
        _filter.TextChanged += (_, _) => { typing.Stop(); typing.Start(); };
        _problems.Click += async (_, _) => await ReloadAsync();

        var cmdRow = new WrapPanel();
        cmdRow.Children.Add(_command);
        cmdRow.Children.Add(new TextBlock { Text = "namespace", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) });
        cmdRow.Children.Add(_namespace);
        cmdRow.Children.Add(Btn("Run", async () => await RunConsoleAsync(_command.Text), "Run the query (Enter)", "PrimaryButton"));
        _command.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await RunConsoleAsync(_command.Text); } };
        _consoleBar.Children.Add(cmdRow);
        _consoleBar.Children.Add(_wql);

        _grid.SetResourceReference(BackgroundProperty, "Bg");
        _grid.MouseDoubleClick += async (_, _) => { if (_snap?.Actions.FirstOrDefault(a => !a.Changes) is { } open) await RunActionAsync(open); };
        var right = new DockPanel { Margin = new Thickness(10, 0, 0, 0) };
        var top = new StackPanel();
        top.Children.Add(_title); top.Children.Add(_about); top.Children.Add(_consoleBar); top.Children.Add(filterRow); top.Children.Add(_actions);
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(_count, Dock.Bottom);
        right.Children.Add(top); right.Children.Add(_count); right.Children.Add(_grid);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250), MinWidth = 180 });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var split = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch, Background = System.Windows.Media.Brushes.Transparent };
        Grid.SetColumn(split, 1); Grid.SetColumn(right, 2);
        body.Children.Add(_tree); body.Children.Add(split); body.Children.Add(right);

        var root = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head); root.Children.Add(body);
        Content = root;
        SetResourceReference(BackgroundProperty, "Bg");

        FillComputers();
        _computer.SelectionChanged += async (_, _) => { if (_computer.SelectedItem is Choice c) await UseAsync(c); };
        PreviewKeyDown += async (_, e) => { if (e.Key == Key.F5) { e.Handled = true; await ReloadAsync(); } };
        Loaded += async (_, _) =>
        {
            if (_snap is null && _tree.Items.Count > 0 && _tree.Items[0] is TreeViewItem g && g.Items.Count > 0 && g.Items[0] is TreeViewItem first) first.IsSelected = true;
            await Task.CompletedTask;
        };
        Unloaded += (_, _) => _load?.Cancel();
    }

    private static Button Btn(string text, Action click, string tip, string style = "SmallButton")
    {
        var b = new Button { Content = text, Margin = new Thickness(6, 0, 0, 0), ToolTip = tip };
        b.SetResourceReference(StyleProperty, style);
        b.Click += (_, _) => click();
        return b;
    }

    // ------------------------------------------------------------------------------------------- computers
    private void FillComputers()
    {
        var items = new List<Choice> { new("This PC (" + Environment.MachineName + ")", null, ".") };
        if (WinToolsPolicy.RemoteBlocked() is null)
            items.AddRange(AppData.Current.Hosts.Where(IsWindows).OrderBy(h => h.Name, StringComparer.OrdinalIgnoreCase)
                .Select(h => new Choice(WinTarget.Flat(h.Name.Length > 0 ? h.Name : h.Address, 60) + "  ·  " + WinTarget.Flat(h.Address, 60), h, null)));
        _computer.ItemsSource = items;
        _computer.SelectedIndex = 0;
    }

    /// <summary>Hosts that are Windows: Remote Desktop ones, or ones marked Windows.</summary>
    internal static bool IsWindows(HostEntry h) =>
        h.Address.Trim().Length > 0 && (h.Protocol.Equals("rdp", StringComparison.OrdinalIgnoreCase) || h.Platform.Contains("windows", StringComparison.OrdinalIgnoreCase));

    private async Task UseAsync(Choice c)
    {
        try
        {
            var next = c.Host is { } h ? await WinTarget.ForHostAsync(h) : WinTarget.ForComputer(c.Computer ?? ".");
            _target.Dispose();
            _target = next;
            _status.Text = "Managing " + _target.Display;
            await ReloadAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { _status.Text = ex.Message; }
    }

    private async Task ConnectToAsync()
    {
        if (WinToolsPolicy.RemoteBlocked() is { } no) { Dialogs.Info("Windows management", no); return; }
        if (Dialogs.Prompt("Connect to a computer", "The Windows computer's name or address. It is managed with your Windows sign-in (for a saved credential, add it as a host and pick it in the list).") is not { Length: > 0 } name) return;
        if (WinTarget.ValidateComputer(name) is { } bad) { Dialogs.Info("Connect to a computer", bad); return; }
        var list = ((IEnumerable<Choice>)_computer.ItemsSource).ToList();
        var c = new Choice(WinTarget.Flat(name.Trim(), 60) + " (your Windows sign-in)", null, name.Trim());
        list.Add(c);
        _computer.ItemsSource = list;
        _computer.SelectedItem = c;
    }

    /// <summary>The computer being managed (the self-test reads it).</summary>
    internal WinTarget TargetForTest => _target;

    // ------------------------------------------------------------------------------------------- showing
    private async Task ShowAsync(Snapin s)
    {
        _snap = s;
        _title.Text = s.Title;
        _about.Text = s.About;
        _problems.Visibility = s.EventLog is null ? Visibility.Collapsed : Visibility.Visible;
        _consoleBar.Visibility = s.Id == Snapins.ConsoleId ? Visibility.Visible : Visibility.Collapsed;
        _actions.Children.Clear();
        foreach (var a in s.Actions)
        {
            var act = a;
            var b = Btn(a.Label, async () => await RunActionAsync(act), a.Changes ? (WinToolsPolicy.ChangesBlocked() ?? a.Warning) : "Open it");
            b.Margin = new Thickness(0, 0, 6, 0);
            if (a.Changes && WinToolsPolicy.ChangesBlocked() is not null) b.IsEnabled = false;
            _actions.Children.Add(b);
        }
        if (s.Id == Snapins.ConsoleId) { SetTable(new WmiTable()); _command.Focus(); return; }
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        if (_snap is not { } s || s.Id == Snapins.ConsoleId) return;
        _load?.Cancel();
        var cts = _load = new CancellationTokenSource();
        _status.Text = "Loading " + s.Title + " from " + _target.Display + "…";
        try
        {
            var table = s.EventLog is { } log && _problems.IsChecked == true
                ? await _target.EventsAsync(log, WinToolsPolicy.EventLimit, 3, cts.Token)
                : await s.Load(_target, cts.Token);
            if (cts.IsCancellationRequested || _snap != s) return;
            SetTable(table);
            _status.Text = "Managing " + _target.Display;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (Expected(ex))
        {
            if (_snap != s) return;
            SetTable(new WmiTable());
            _status.Text = s.Title + ": " + Explain(ex);
        }
    }

    internal static bool Expected(Exception ex) => ex is System.Management.ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException
        or System.Diagnostics.Eventing.Reader.EventLogException or InvalidOperationException or ArgumentException or TimeoutException or System.IO.IOException;

    private const string Denied = "access denied (that needs administrator rights on the computer, or a credential that has them).";

    internal static string Explain(Exception ex) => ex switch
    {
        UnauthorizedAccessException => Denied,
        System.Diagnostics.Eventing.Reader.EventLogException el when el.Message.Contains("denied", StringComparison.OrdinalIgnoreCase) => Denied,
        System.Runtime.InteropServices.COMException { HResult: unchecked((int)0x800706BA) } => "the computer can't be reached (it's off, the name is wrong, or its firewall blocks remote management: WMI and RPC).",
        System.Management.ManagementException me => me.ErrorCode switch
        {
            System.Management.ManagementStatus.InvalidClass => "that class doesn't exist on this computer.",
            System.Management.ManagementStatus.InvalidNamespace => "that namespace doesn't exist on this computer.",
            System.Management.ManagementStatus.InvalidQuery => "that query isn't valid WQL.",
            System.Management.ManagementStatus.AccessDenied => "access denied (that needs administrator rights on the computer).",
            _ => WinTarget.Flat(me.Message, 300),
        },
        _ => WinTarget.Flat(ex.Message, 300),
    };

    private void SetTable(WmiTable t)
    {
        _table = t;
        _grid.Columns.Clear();
        for (int i = 0; i < t.Columns.Count; i++)
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = t.Columns[i], Binding = new Binding("[" + i + "]") { Mode = BindingMode.OneTime },
                Width = t.Columns[i] is "Message" or "CommandLine" or "PathName" or "Description" or "Command" or "What it manages" ? new DataGridLength(1, DataGridLengthUnitType.Star) : DataGridLength.Auto,
                MaxWidth = 700,
            });
        Render();
    }

    private void Render()
    {
        var q = _filter.Text.Trim();
        var rows = q.Length == 0 ? _table.Rows : _table.Rows.Where(r => r.Any(c => c.Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();
        _grid.ItemsSource = rows;
        _count.Text = rows.Count == _table.Rows.Count ? $"{rows.Count} item(s)" + (_table.Truncated ? " — the first ones only (see Settings → Windows management)" : "")
            : $"{rows.Count} of {_table.Rows.Count} item(s) match “{q}”";
    }

    // ------------------------------------------------------------------------------------------- console + actions
    private async Task RunConsoleAsync(string line)
    {
        if (WinToolsPolicy.ConsoleBlocked() is { } no) { _wql.Text = no; return; }
        var p = WmicCommand.Parse(line, _namespace.Text.Trim());
        if (p.Error is { } err) { _wql.Text = err; SetTable(new WmiTable()); return; }
        _wql.Text = (p.FromWmic ? "Runs as WQL: " : "WQL: ") + p.Wql + "   (namespace " + p.Namespace + ")";
        _load?.Cancel();
        var cts = _load = new CancellationTokenSource();
        try { SetTable(await _target.QueryAsync(p.Wql, p.Namespace, 5000, ct: cts.Token)); }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (Expected(ex)) { SetTable(new WmiTable()); _wql.Text += "\n" + Explain(ex); }
    }

    private async Task RunActionAsync(SnapAction a)
    {
        if (_snap is not { } s || _grid.SelectedItem is not string[] row) { _status.Text = "Select a row first."; return; }
        if (a.Changes)
        {
            if (WinToolsPolicy.ChangesBlocked() is { } no) { Dialogs.Info("Windows management", no); return; }
            var what = WinTarget.Flat(row.FirstOrDefault(c => c.Length > 0) ?? "", 80);
            if (_table.Col("Name") is >= 0 and var ni) what = row[ni];
            if (!Dialogs.Confirm(s.Title + ": " + a.Label.TrimStart('▶', '■', '↻', '✕', '✓', '⊘', ' '),
                    $"{a.Label.TrimStart('▶', '■', '↻', '✕', '✓', '⊘', ' ')} “{what}” on {_target.Display}?\n\n{a.Warning}", a.Label.TrimStart('▶', '■', '↻', '✕', '✓', '⊘', ' '), danger: a.Id is "stop" or "end" or "disabled" or "disable")) return;
        }
        _status.Text = a.Label + "…";
        try
        {
            var result = await a.Run(_target, _table, row);
            _status.Text = result;
            if (a.Changes)
            {
                AuditLog.Write("you", "windows tools", s.Title + " · " + a.Label.TrimStart('▶', '■', '↻', '✕', '✓', '⊘', ' '), result + " · " + _target.Display);
                await ReloadAsync();
                _status.Text = result;
            }
        }
        catch (Exception ex) when (Expected(ex)) { _status.Text = a.Label + ": " + Explain(ex); }
    }

    // ------------------------------------------------------------------------------------------- self-test
    /// <summary>Self-test: shows a snap-in (as clicking it in the tree does) and returns what it loaded.</summary>
    internal async Task<(WmiTable Table, string Status)> ShowForTest(string id)
    {
        var item = _tree.Items.OfType<TreeViewItem>().SelectMany(g => g.Items.OfType<TreeViewItem>()).FirstOrDefault(i => i.Tag is Snapin s && s.Id == id)
            ?? throw new InvalidOperationException("no such node in the tree: " + id);
        _snap = null;
        item.IsSelected = true;
        if (_snap?.Id != id) await ShowAsync((Snapin)item.Tag);
        for (int i = 0; i < 600 && _status.Text.StartsWith("Loading", StringComparison.Ordinal); i++) await Task.Delay(50);
        return (_table, _status.Text);
    }

    internal async Task<(WmiTable Table, string Wql)> ConsoleForTest(string line)
    {
        await ShowForTest(Snapins.ConsoleId);
        await RunConsoleAsync(line);
        return (_table, _wql.Text);
    }

    internal IReadOnlyList<string> TreeForTest => _tree.Items.OfType<TreeViewItem>().SelectMany(g => g.Items.OfType<TreeViewItem>()).Select(i => ((Snapin)i.Tag).Id).ToList();
    internal IReadOnlyList<(string Label, bool Enabled)> ActionsForTest => _actions.Children.OfType<Button>().Select(b => (b.Content as string ?? "", b.IsEnabled)).ToList();
    internal int RowsShownForTest => (_grid.ItemsSource as System.Collections.ICollection)?.Count ?? 0;
    internal void FilterForTest(string text) { _filter.Text = text; Render(); }
    internal void SelectRowForTest(int i) => _grid.SelectedItem = (_grid.ItemsSource as IList<string[]>)?[i];
    internal Task RunActionForTest(string id) => _snap?.Actions.FirstOrDefault(a => a.Id == id) is { } a ? RunActionAsync(a) : Task.CompletedTask;
    internal string StatusForTest => _status.Text;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _load?.Cancel();
        _target.Dispose();
    }
}
