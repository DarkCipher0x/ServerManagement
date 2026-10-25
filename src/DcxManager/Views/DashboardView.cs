using System.Collections.Specialized;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using DcxManager.Files;
using DcxManager.Incidents;
using DcxManager.Infra;
using DcxManager.Notes;
using DcxManager.Services;
using DcxManager.Ssh;
using DcxManager.Vultr;

namespace DcxManager.Views;

/// <summary>
/// Home: the engineering overview (spec §48, ADR-018). Local facts — sessions, incidents, transfers, notes, warnings,
/// environment state — come from the app's own events and never touch the network. Remote sources (Vultr, the AI engine,
/// infrastructure providers) are contacted only by the check rule the header shows, or by "Check now". Cards can be
/// moved, hidden and restored from their right-click menu.
/// </summary>
public sealed partial class DashboardView : UserControl, ICloudView, IActivatable, IDisposable
{
    private readonly StatTile _tSessions = new("Sessions", 170), _tIncidents = new("Incidents", 170), _tTransfers = new("Transfers", 170),
        _tWarnings = new("Warnings", 170), _tHosts = new("Saved hosts", 170), _tChecks = new("Checks", 170), _tStore = new("Encrypted store", 210);
    /// <summary>The modules' sources and their tiles (the AI engine), drawn after "Saved hosts" (0.2.0 phase 3).</summary>
    private readonly List<(ModuleSource Source, StatTile Tile)> _moduleTiles =
        Overview.ModuleSources.Select(m => (m, new StatTile(m.Title, m.TileWidth))).ToList();
    private readonly Dictionary<string, StackPanel> _body = new();
    private readonly Dictionary<string, Border> _cards = new();
    private readonly Grid _columns = new();
    private readonly TextBlock _status = new TextBlock().Fs(FontStep.Small);
    private readonly TextBlock _checks = new TextBlock { TextWrapping = TextWrapping.Wrap }.Fs(FontStep.Small);
    private readonly DispatcherTimer _redraw, _poll;
    private readonly Dictionary<string, string> _skip = new();
    private int _layoutCols;
    private bool _dirty = true, _disposed, _checking;
    private DateTime _lastOpenCheck = DateTime.MinValue;

    // Remote results of the modules' sources (memory only), by source id.
    private readonly Dictionary<string, (DateTime At, string By, CheckOutcome Outcome)> _moduleResults = new();

    /// <summary>The live instance (self-test and the Privacy view's description).</summary>
    public static DashboardView? Current { get; private set; }
    /// <summary>Redraws actually performed (self-test: event floods must coalesce).</summary>
    internal int Redraws { get; private set; }
    /// <summary>Remote checks attempted, by source id (self-test: "manual" must contact nothing by itself).</summary>
    internal List<string> Contacted { get; } = new();

    private static AppSettings S => AppSettings.Current;

    public DashboardView()
    {
        Current = this;
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _checks.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var root = new StackPanel { Margin = new Thickness(18, 14, 18, 14) };
        // Title and buttons share a wrapping row (the buttons drop under the title when the screen is narrow); the status
        // lines below always get the full width (s40: a docked right column squeezed them into a narrow strip).
        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        var top = new WrapPanel();
        var bar = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 0, 4) };
        var check = new Button { Content = "↻ Check now", Style = (Style)FindResource("SmallButton"), ToolTip = "Contact every remote source listed under Checks once, now" };
        check.Click += async (_, _) => await RunChecksAsync("manual");
        bar.Children.Add(check);
        bar.Children.Add(Menus.MoreButton(ChecksMenu, "Checks ▾", "SmallButton"));
        var edit = new Button { Content = "✎ Edit dashboard", Style = (Style)FindResource("SmallButton"), ToolTip = "Add cards (hosts, folders, providers, Grafana, Sentinel, Pingdom, BeyondTrust, checks…), hide built-in ones, or reset to default" };
        edit.Click += (_, _) => OpenEditor();
        bar.Children.Add(edit);
        bar.Children.Add(Menus.MoreButton(CustomiseMenu, "Customise ▾", "SmallButton"));
        _lock.Style = (Style)FindResource("SmallButton");
        _lock.Click += (_, _) => SetUnlocked(!Unlocked);
        bar.Children.Add(_lock);
        ShowLock();
        top.Children.Add(new TextBlock { Text = "Engineering overview", Style = (Style)FindResource("H1"), Margin = new Thickness(0, 0, 18, 4), VerticalAlignment = VerticalAlignment.Center });
        top.Children.Add(bar);
        head.Children.Add(top);
        _status.TextWrapping = TextWrapping.Wrap;
        _checks.TextWrapping = TextWrapping.Wrap;
        head.Children.Add(_status);
        head.Children.Add(_checks);
        root.Children.Add(head);

        foreach (var c in Overview.Cards) BuildCard(c);
        SyncAddedCards();
        root.Children.Add(_columns);
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        _redraw = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => { _redraw!.Stop(); Redraw(); }, Dispatcher) { IsEnabled = false };
        _poll = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { IsEnabled = false };
        _poll.Tick += async (_, _) => { if (IsVisible) await RunChecksAsync("interval"); };

        Loaded += OnLoaded;
        Unloaded += (_, _) => _poll.Stop();
        IsVisibleChanged += (_, _) => { if (IsVisible && _dirty) Schedule(); SetPoll(); };
        SizeChanged += (_, e) => { if (Overview.Columns(new[] { "a", "b" }, e.NewSize.Width).Count != _layoutCols) Layout(); };

        LiveSessions.Changed += Schedule;
        NoteStore.Changed += OnNote;
        IncidentStore.Changed += OnIncident;
        TransferQueue.Jobs.CollectionChanged += OnJobs;
        TransferQueue.Finished += OnJob;
        AuditLog.Added += OnAudit;
        Offline.Changed += Schedule;
        Offline.BlockedChanged += Schedule;
        Envs.Changed += Schedule;
        InfraStatus.Changed += Schedule;
        Api.Broadcasts.Changed += Schedule;
        Checks.Ran += OnCheck;
    }

    private void OnCheck(DashboardTile _) => Schedule();

    private void OnNote(Note? _) => Schedule();
    private void OnIncident(Incident? _) => Schedule();
    private void OnJobs(object? s, NotifyCollectionChangedEventArgs e) => Schedule();
    private void OnJob(TransferJob _) => Schedule();
    private void OnAudit(AuditEntry _) => Schedule();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _redraw.Stop(); _poll.Stop();
        LiveSessions.Changed -= Schedule;
        NoteStore.Changed -= OnNote;
        IncidentStore.Changed -= OnIncident;
        TransferQueue.Jobs.CollectionChanged -= OnJobs;
        TransferQueue.Finished -= OnJob;
        AuditLog.Added -= OnAudit;
        Offline.Changed -= Schedule;
        Offline.BlockedChanged -= Schedule;
        Envs.Changed -= Schedule;
        InfraStatus.Changed -= Schedule;
        Api.Broadcasts.Changed -= Schedule;
        Checks.Ran -= OnCheck;
        if (Current == this) Current = null;
    }

    public CloudProvider? Provider => null;   // follows every provider's account
    public void AccountChanged()
    {
        _cloud.Clear(); Schedule();
        _ = RunChecksAsync("open", cloudOnly: true);
    }
    public void Activated() => Schedule();

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_disposed) return;
        Current = this;
        Layout();
        Redraw();
        SetPoll();
        // "When the dashboard opens": each time it is shown, at most once a minute.
        if (DateTime.Now - _lastOpenCheck > TimeSpan.FromMinutes(1)) { _lastOpenCheck = DateTime.Now; await RunChecksAsync("open"); }
    }

    /// <summary>Coalesces redraw requests (from any thread); while hidden it only marks the view dirty.</summary>
    public void Schedule()
    {
        if (_disposed) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(Schedule); return; }
        _dirty = true;
        if (!IsVisible) return;
        if (!_redraw.IsEnabled) _redraw.Start();
    }

    /// <summary>The interval timer, when it is running (self-test).</summary>
    internal TimeSpan? PollingEvery => _poll.IsEnabled ? _poll.Interval : null;
    internal int LayoutColumns => _layoutCols;
    internal string BodyText(string card) => string.Join("\n", Menus.Descendants<TextBlock>(_body[card]).Select(t => t.Text));
    /// <summary>Self-test: redraw now even while another tab is in front.</summary>
    internal void RedrawNow() => Redraw();
    internal string SummaryText => string.Join("\n", Menus.Descendants<TextBlock>(_body["summary"]).Select(t => t.Text));

    internal void SetPoll()
    {
        _poll.Stop();
        if (_disposed || S.DashboardChecks != "interval" || !IsVisible) return;
        _poll.Interval = TimeSpan.FromMinutes(Math.Clamp(S.DashboardCheckMinutes, 5, 60));
        _poll.Start();
    }

    private static void Save() { try { AppSettings.Save(); } catch (Exception ex) { App.Log("Dashboard settings", ex); } }

    // ---- unlock and drag (0.4.0, DarkCipher0x: "unlock it and then drag and drop tiles around") -----------------------------------

    private readonly Button _lock = new() { ToolTip = "Unlock to drag the cards into the order you want; lock when done. Hidden cards come back from Customise ▾." };
    private Point? _dragStart;
    private Point _dragGrab;

    /// <summary>While unlocked every card can be dragged onto another card: it takes that card's place. The order is
    /// saved as before (DashboardOrder); locking only stops the dragging.</summary>
    public bool Unlocked { get; private set; }

    public void SetUnlocked(bool on)
    {
        if (on && DashboardCatalog.Locked(S) is { } why) { _status.Text = "🔒 " + why; return; }
        Unlocked = on;
        ShowLock();
        foreach (var b in _cards.Values) Decorate(b);
        _status.Text = on ? "Layout unlocked — drag a card onto another card to swap their places; 🔒 Lock layout when done." : "";
    }

    private void ShowLock() { _lock.Content = Unlocked ? "🔒 Lock layout" : "🔓 Unlock layout"; }

    /// <summary>The dashed accent border and the drag handlers of an unlocked card (removed again when locked).</summary>
    private void Decorate(Border card)
    {
        card.AllowDrop = Unlocked;
        if (Unlocked)
        {
            card.SetResourceReference(Border.BorderBrushProperty, "Accent");
            card.BorderThickness = new Thickness(1);
            card.Cursor = System.Windows.Input.Cursors.SizeAll;
            card.ToolTip = $"Drag “{Overview.CardById((string)card.Tag).Title}” onto another card";
        }
        else
        {
            if ((string)card.Tag == "summary") { card.BorderThickness = new Thickness(0); card.ClearValue(Border.BorderBrushProperty); }
            else card.SetResourceReference(Border.BorderBrushProperty, "Line");
            card.Cursor = null;
            card.ToolTip = null;
        }
    }

    private void WireDrag(Border card)
    {
        card.PreviewMouseLeftButtonDown += (_, e) => { if (Unlocked && e.OriginalSource is not System.Windows.Controls.Primitives.ButtonBase) { _dragStart = e.GetPosition(this); _dragGrab = e.GetPosition(card); } };
        card.PreviewMouseLeftButtonUp += (_, _) => _dragStart = null;
        card.PreviewMouseMove += (_, e) =>
        {
            if (!Unlocked || _dragStart is not { } start || e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
            var p = e.GetPosition(this);
            if (Math.Abs(p.X - start.X) < 6 && Math.Abs(p.Y - start.Y) < 6) return;
            _dragStart = null;
            // 0.5.0: a picture of the card follows the cursor, the card itself fades, the target is outlined (DragGhost).
            DragGhost.Run(card, new DataObject(DragFormat, (string)card.Tag), DragDropEffects.Move, _dragGrab);
        };
        card.DragOver += (_, e) =>
        {
            bool can = Unlocked && e.Data.GetData(DragFormat) is string from && from != (string)card.Tag;
            e.Effects = can ? DragDropEffects.Move : DragDropEffects.None;
            DragGhost.Target(can ? card : null);
            e.Handled = true;
        };
        card.DragLeave += (_, _) => DragGhost.Target(null);
        card.Drop += (_, e) =>
        {
            if (!Unlocked || e.Data.GetData(DragFormat) is not string from) return;
            e.Handled = true;
            DropCard(from, (string)card.Tag);
        };
    }

    private const string DragFormat = "dcx-dashboard-card";

    /// <summary>The dragged card takes the target's place (the target and what follows shift by one). Public for the self-test.</summary>
    public void DropCard(string dragged, string target)
    {
        if (dragged == target) return;
        var visible = Overview.Visible(S.DashboardOrder, S.DashboardHidden);
        int from = visible.IndexOf(dragged), to = visible.IndexOf(target);
        if (from < 0 || to < 0) return;
        // The cards slide from where they were to where they go (instant with reduced animation).
        var before = DragGhost.Capture(_cards.Values, _columns);
        MoveCard(dragged, to - from);
        DragGhost.Play(before, _columns, NavLayout.Reduced);
        _status.Text = $"“{Overview.CardById(dragged).Title}” moved to position {to + 1}.";
    }

    // ---- layout -------------------------------------------------------------------------------------------------------

    private void BuildCard(Overview.Card c)
    {
        var body = new StackPanel();
        _body[c.Id] = body;
        if (c.Id == "summary")
        {
            // Equal-width tiles in as many columns as fit (a WrapPanel left one tile alone on the last row).
            var tiles = new System.Windows.Controls.Primitives.UniformGrid();
            foreach (var t in new[] { _tSessions, _tIncidents, _tTransfers, _tWarnings, _tHosts, _tChecks }.Concat(_moduleTiles.Select(m => m.Tile)).Append(_tStore))
            {
                t.Width = double.NaN;
                t.MinWidth = 150;
                tiles.Children.Add(t);
            }
            tiles.SizeChanged += (_, e) =>
            {
                int n = tiles.Children.Count, fit = Math.Clamp((int)(e.NewSize.Width / 160), 1, n);
                // One column fewer when that avoids a tile alone on the last row (never down to a single column for it).
                if (fit > 2 && n % fit == 1 && n % (fit - 1) != 1) fit--;
                if (tiles.Columns != fit) tiles.Columns = fit;
            };
            body.Children.Add(tiles);
            var b0 = new Border { Child = body, Margin = new Thickness(0, 0, 0, 6), Background = System.Windows.Media.Brushes.Transparent, Tag = c.Id };
            Menus.Attach(b0, () => CardMenu(c.Id));
            WireDrag(b0);
            _cards[c.Id] = b0;
            return;
        }
        var p = new StackPanel();
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var more = Menus.MoreButton(() => CardMenu(c.Id), "⋯", "SmallButton");
        more.ToolTip = "Move, hide or change this card";
        DockPanel.SetDock(more, Dock.Right);
        head.Children.Add(more);
        head.Children.Add(new TextBlock { Text = c.Title, Style = (Style)FindResource("H2"), ToolTip = c.Explain });
        p.Children.Add(head);
        p.Children.Add(body);
        var b = new Border { Style = (Style)FindResource("Card"), Child = p, VerticalAlignment = VerticalAlignment.Top, Tag = c.Id };
        Menus.Attach(b, () => CardMenu(c.Id));
        WireDrag(b);
        _cards[c.Id] = b;
    }

    /// <summary>Places the visible cards: the summary across the top, the rest in one or two columns in order.</summary>
    private void Layout()
    {
        SyncAddedCards();
        foreach (var b in _cards.Values) (b.Parent as Panel)?.Children.Remove(b);
        _columns.Children.Clear();
        _columns.ColumnDefinitions.Clear();
        _columns.RowDefinitions.Clear();
        var visible = Overview.Visible(S.DashboardOrder, S.DashboardHidden);
        var width = ActualWidth > 0 ? ActualWidth - 36 : 1200;
        // 0.5.1 #12: full-width added cards go under the columns, across them.
        var wide = DashboardCatalog.Cards(S).Where(c => c.Wide).Select(c => c.Id).ToHashSet();
        var rest = visible.Where(id => id != "summary" && !wide.Contains(id)).ToList();
        var cols = Overview.Columns(rest, width);
        _layoutCols = cols.Count;
        _columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _columns.RowDefinitions.Add(new RowDefinition());
        for (int i = 0; i < cols.Count; i++)
        {
            _columns.ColumnDefinitions.Add(new ColumnDefinition());
            var panel = new StackPanel();
            foreach (var id in cols[i]) panel.Children.Add(_cards[id]);
            Grid.SetRow(panel, 1); Grid.SetColumn(panel, i);
            _columns.Children.Add(panel);
        }
        if (visible.Contains("summary"))
        {
            Grid.SetColumnSpan(_cards["summary"], Math.Max(1, cols.Count));
            _columns.Children.Add(_cards["summary"]);
        }
        var across = visible.Where(wide.Contains).ToList();
        if (across.Count > 0)
        {
            _columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var panel = new StackPanel();
            foreach (var id in across) panel.Children.Add(_cards[id]);
            Grid.SetRow(panel, 2); Grid.SetColumnSpan(panel, Math.Max(1, cols.Count));
            _columns.Children.Add(panel);
        }
        if (visible.Count == 0)
        {
            var none = new TextBlock { Text = "Every card is hidden — Customise ▾ → Show all cards.", Style = (Style)FindResource("Hint") };
            Grid.SetRow(none, 1);
            _columns.Children.Add(none);
        }
    }

    internal IReadOnlyList<string> VisibleOrderForTest => Overview.Visible(S.DashboardOrder, S.DashboardHidden);
    /// <summary>Self-test: the text a card shows right now (after a redraw).</summary>
    internal string TextOfCardForTest(string id)
    {
        Redraw();
        return _body.TryGetValue(id, out var b) ? string.Join(" ", Menus.Descendants<TextBlock>(b).Select(t => t.Text)) : "";
    }
    internal IReadOnlyList<string> PlacedCards => _cards.Where(kv => kv.Value.Parent != null).Select(kv => kv.Key).ToList();

    public void MoveCard(string id, int delta)
    {
        if (DashboardCatalog.Locked(S) is { } why) { _status.Text = "🔒 " + why; return; }
        var order = Overview.Order(S.DashboardOrder);
        var visible = order.Where(x => !S.DashboardHidden.Contains(x)).ToList();
        int i = visible.IndexOf(id), j = i + delta;
        if (i < 0 || j < 0 || j >= visible.Count) return;
        S.DashboardOrder = NavLayout.Move(order, id, order.IndexOf(visible[j]) - order.IndexOf(id));
        Save(); Layout();
    }

    public void SetCardHidden(string id, bool hide)
    {
        if (DashboardCatalog.Locked(S) is { } why) { _status.Text = "🔒 " + why; return; }
        S.DashboardHidden.Remove(id);
        if (hide) S.DashboardHidden.Add(id);
        Save(); Layout(); Schedule();
    }

    public void ResetLayout() { if (DashboardCatalog.Locked(S) is { } why) { _status.Text = "🔒 " + why; return; } S.DashboardOrder.Clear(); S.DashboardHidden.Clear(); Save(); Layout(); Schedule(); }

    private IEnumerable<object?> CardMenu(string id)
    {
        if (DashboardCatalog.Locked(S) is { } why)
        {
            yield return Menus.Item("🔒 " + why, () => { }, enabled: false);
            yield break;
        }
        var visible = Overview.Visible(S.DashboardOrder, S.DashboardHidden);
        int i = visible.IndexOf(id);
        yield return Menus.Item("Move up", () => MoveCard(id, -1), enabled: i > 0);
        yield return Menus.Item("Move down", () => MoveCard(id, +1), enabled: i >= 0 && i < visible.Count - 1);
        if (IsAdded(id)) foreach (var x in AddedCardMenu(id)) yield return x;
        else yield return Menus.Item($"Hide “{Overview.CardById(id).Title}”", () => SetCardHidden(id, true));
        foreach (var x in CustomiseMenu()) yield return x;
    }

    private IEnumerable<object?> CustomiseMenu()
    {
        var locked = DashboardCatalog.Locked(S);
        var hidden = Overview.Order(S.DashboardOrder).Where(S.DashboardHidden.Contains).ToList();
        yield return Menus.Sep();
        if (locked != null) { yield return Menus.Item("🔒 " + locked, () => { }, enabled: false); yield return Menus.Item("Help: Engineering overview", () => MainWindow.Instance.ShowHelp("Engineering overview")); yield break; }
        yield return Menus.Item("✎ Edit dashboard…", OpenEditor, tip: "Add cards from the catalogue, hide built-in ones, reset to default");
        yield return Menus.Item(Unlocked ? "🔒 Lock layout" : "🔓 Unlock layout (drag cards around)", () => SetUnlocked(!Unlocked));
        yield return Menus.Item("📌 Pin a check…", () => { if (PinCheckDialog.Show(Window.GetWindow(this)) != null) { SetCardHidden("checks", false); Schedule(); } }, tip: "An uptime, certificate, performance or mail-domain check as a dashboard row");
        yield return Menus.Sub("Show hidden card", hidden.Select(h => (object?)Menus.Item(Overview.CardById(h).Title, () => SetCardHidden(h, false))).ToArray());
        yield return Menus.Item("Show all cards", () => { S.DashboardHidden.Clear(); Save(); Layout(); Schedule(); }, enabled: hidden.Count > 0);
        yield return Menus.Item("Reset to default…", () => ResetToDefault(), enabled: S.DashboardOrder.Count > 0 || S.DashboardHidden.Count > 0 || S.DashboardCards.Count > 0,
            tip: "Every built-in card in its place, none hidden, the cards you added removed (asks first)");
        yield return Menus.Item("Help: Engineering overview", () => MainWindow.Instance.ShowHelp("Engineering overview"));
    }

    private IEnumerable<object?> ChecksMenu()
    {
        string Mark(bool on) => on ? "✓ " : "    ";
        void Mode(string m) { S.DashboardChecks = m; Save(); SetPoll(); ShowChecks(); }
        void Minutes(int n) { S.DashboardChecks = "interval"; S.DashboardCheckMinutes = n; Save(); SetPoll(); ShowChecks(); }
        yield return Menus.Item(Mark(S.DashboardChecks == "manual") + "Only when I press Check now", () => Mode("manual"));
        yield return Menus.Item(Mark(S.DashboardChecks == "open") + "When the dashboard opens (at most once a minute)", () => Mode("open"));
        foreach (var n in new[] { 5, 15, 60 })
            yield return Menus.Item(Mark(S.DashboardChecks == "interval" && S.DashboardCheckMinutes == n) + $"Every {n} min while the dashboard is on screen", () => Minutes(n));
        yield return Menus.Sep();
        var sources = Overview.Sources();
        if (sources.Count == 0) yield return Menus.Item("No remote sources set up", () => { }, enabled: false);
        foreach (var s in sources)
        {
            bool on = S.DashboardAutoSources.Contains(s.Id);
            yield return Menus.Item(Mark(on) + $"Include {s.Title} ({s.Host})", () => { Overview.SetAutoSource(s.Id, !on); Save(); ShowChecks(); },
                tip: "Automatic checks contact this source; it is always checked when you press Check now");
        }
        yield return Menus.Sep();
        yield return Menus.Item("Privacy & network…", () => MainWindow.Instance.Navigate("privacy"));
    }

    private void ShowChecks()
    {
        var auto = Overview.Sources().Where(s => S.DashboardAutoSources.Contains(s.Id)).Select(s => s.Title).ToList();
        _checks.Text = S.DashboardChecks == "manual" || auto.Count == 0
            ? "Remote checks: only when you press Check now. Everything else here is read from this PC."
            : $"Remote checks: {Overview.ModeText(S.DashboardChecks, S.DashboardCheckMinutes)} — {string.Join(", ", auto)}. Everything else here is read from this PC.";
    }

    // ---- remote checks ------------------------------------------------------------------------------------------------

    /// <summary>"manual" checks every source; "open" / "interval" only those the rule includes (offline-refused ones are
    /// skipped, not attempted).</summary>
    public async Task RunChecksAsync(string trigger, string? only = null, bool cloudOnly = false)
    {
        if (_checking || _disposed) return;
        _checking = true;
        try
        {
            var tasks = new List<Task>();
            foreach (var s in Overview.Sources().Where(s => (only == null || s.Id == only) && (!cloudOnly || CloudProviders.Find(s.Id) != null)))
            {
                if (trigger != "manual" && Overview.AutoCheck(s, trigger) is { } why) { _skip[s.Id] = why; continue; }
                _skip.Remove(s.Id);
                Contacted.Add(s.Id);
                var by = trigger == "manual" ? "Check now" : trigger == "open" ? "on open" : "interval";
                if (CloudProviders.Find(s.Id) is { } cp) tasks.Add(CheckCloudAsync(cp, by));
                else if (_moduleTiles.FirstOrDefault(m => m.Source.Id == s.Id).Source is { } ms) tasks.Add(CheckModuleAsync(ms, by));
                else if (AppData.Current.InfraProviders.FirstOrDefault(c => "infra:" + c.Id == s.Id) is { } c)
                    using (trigger == "manual" ? null : ExternalCredentials.Background())   // 0.6.17 (ADR-096): only Check now may ask for a vault
                        tasks.Add(InfraStatus.CheckAsync(c, "dashboard · " + by, new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token));
            }
            // 0.3.0 §20: the pinned checks follow the same rule (all on Check now; due ones on open / interval).
            if (only == null && !cloudOnly && AppData.Current.DashboardTiles.Count > 0 && (trigger == "manual" || S.DashboardChecks == trigger))
                tasks.Add(Checks.RunAllAsync(trigger));
            Schedule();
            await Task.WhenAll(tasks);
        }
        finally { _checking = false; Schedule(); }
    }

    /// <summary>One provider's last check (memory only): its instances in neutral terms, an account summary, or the error.</summary>
    private sealed record CloudResult(DateTime At, string By, List<CloudInstance>? Instances, string? Summary, string? Error);
    private readonly Dictionary<string, CloudResult> _cloud = new();
    internal bool Checking => _checking;
    internal IReadOnlyDictionary<string, int> CloudCounts => _cloud.ToDictionary(kv => kv.Key, kv => kv.Value.Instances?.Count ?? -1);

    /// <summary>A failing provider only marks its own part of the card; the others and the rest of the dashboard carry on.</summary>
    private async Task CheckCloudAsync(CloudProvider p, string by)
    {
        ICloudApi? api;
        try { api = p.Active; }
        catch (Locks.LockedException) { _cloud[p.Id] = new CloudResult(DateTime.Now, by, null, null, "🔒 the account is locked"); return; }   // 0.5.1 #10
        if (api == null) return;
        try
        {
            var list = await p.ListInstancesAsync(api);
            if (AppSettings.Current.CloudSyncHosts) HostsView.FollowCloud(p, api.AccountId, list);   // 0.6.24: hosts follow their servers
            string? summary = null;
            try { summary = await p.AccountSummaryAsync(api); } catch (Exception ex) { App.Log($"{p.Title} account summary", ex); }
            _cloud[p.Id] = new CloudResult(DateTime.Now, by, list, summary, null);
        }
        catch (Exception ex) { _cloud[p.Id] = new CloudResult(DateTime.Now, by, null, null, ex.Message.Split('\n')[0]); }
        Schedule();
    }

    /// <summary>A module's source: its check reports text or an error; a check that throws only marks its own tile.</summary>
    private async Task CheckModuleAsync(ModuleSource m, string by)
    {
        CheckOutcome r;
        try { r = await m.Check(); }
        catch (Exception ex) { App.Log($"Dashboard check ({m.Module}, {m.Id})", ex); r = new(null, ex.Message.Split('\n')[0]); }
        _moduleResults[m.Id] = (DateTime.Now, by, r);
        Schedule();
    }

    private string When(DateTime? at, string by, string id) =>
        at is { } t ? $"checked {t:HH:mm:ss} · {by}" : _skip.TryGetValue(id, out var why) ? "not checked — " + why : "not checked yet";

    // ---- drawing ------------------------------------------------------------------------------------------------------

    private void Redraw()
    {
        if (_disposed) return;
        _dirty = false;
        Redraws++;
        try
        {
            _status.Text = $"{DateTime.Now:dddd d MMMM yyyy, HH:mm}";
            ShowChecks();
            var rows = MainWindow.Instance?.Panes.AllTabs.Select(t => Overview.Classify(t.Content)).OfType<Overview.SessionRow>().ToList() ?? new();
            var warnings = Overview.Warnings(Overview.Gather());
            DrawSummary(rows, warnings);
            foreach (var id in PlacedCards)
            {
                var body = _body[id];
                if (id == "summary") continue;
                body.Children.Clear();
                switch (id)
                {
                    case "sessions": DrawSessions(body, rows); break;
                    case "warnings": DrawWarnings(body, warnings); break;
                    case "safety": DrawSafety(body, rows); break;
                    case "checks": DrawChecks(body); break;
                    case "infra": DrawInfra(body); break;
                    case "incidents": DrawIncidents(body); break;
                    case "transfers": DrawTransfers(body); break;
                    case "connections": DrawConnections(body); break;
                    case "notes": DrawNotes(body); break;
                    case "cloud": DrawCloud(body); break;
                    case "activity": DrawActivity(body); break;
                    default: if (IsAdded(id)) DrawAdded(id, body, rows); break;
                }
            }
        }
        catch (Exception ex) { App.Log("Dashboard redraw", ex); }
    }

    private TextBlock Hint(string text) => new() { Text = text, Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap };

    private static TextBlock Line(string text, FontStep step = FontStep.Meta, bool mono = false)
    {
        var t = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 0, 1) }.Fs(step);
        return mono ? t.Mono() : t;
    }

    private Button Small(string text, Action a, string? tip = null)
    {
        var b = new Button { Content = text, Style = (Style)FindResource("SmallButton"), ToolTip = tip, Margin = new Thickness(4, 0, 0, 0) };
        b.Click += (_, _) => a();
        return b;
    }

    /// <summary>A row: optional environment pill, text, and buttons on the right; clicking the text runs <paramref name="open"/>.</summary>
    private DockPanel Row(string text, string? env, Action? open, IEnumerable<Button>? buttons = null, Func<IEnumerable<object?>>? menu = null, bool mono = false, string? glyphBrush = null, string? glyph = null)
    {
        var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2), Background = System.Windows.Media.Brushes.Transparent };
        if (buttons != null)
        {
            var bar = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var b in buttons) bar.Children.Add(b);
            DockPanel.SetDock(bar, Dock.Right);
            row.Children.Add(bar);
        }
        if (glyph != null)
        {
            var g = new TextBlock { Text = glyph, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            if (glyphBrush != null) g.SetResourceReference(TextBlock.ForegroundProperty, glyphBrush);
            DockPanel.SetDock(g, Dock.Left);
            row.Children.Add(g);
        }
        if (env is { Length: > 0 })
        {
            var pill = EnvUi.Pill(env, small: true);
            pill.Margin = new Thickness(0, 0, 6, 0);
            pill.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(pill, Dock.Left);
            row.Children.Add(pill);
        }
        var t = Line(text, mono: mono);
        if (open != null) { t.Cursor = Cursors.Hand; t.MouseLeftButtonUp += (_, _) => open(); t.ToolTip = text; }
        row.Children.Add(t);
        if (menu != null) Menus.Attach(row, menu);
        return row;
    }

    private void DrawSummary(List<Overview.SessionRow> rows, List<Overview.Warning> warnings)
    {
        int open = rows.Count, live = rows.Count(r => r.Connected != false), prod = rows.Count(r => Envs.IsProduction(r.Environment));
        _tSessions.Set($"{live}/{open}", prod > 0 ? $"connected / open · {prod} PRODUCTION" : "connected / open");
        var active = IncidentStore.All.Where(i => i.IsActive).ToList();
        _tIncidents.Set(active.Count.ToString(), active.Count == 0 ? "none active" : "worst " + IncidentStore.SeverityText(active.Min(i => i.Severity)), alert: active.Any(i => i.Severity <= IncidentSeverity.Sev2));
        int running = TransferQueue.Jobs.Count(j => !j.Finished);
        _tTransfers.Set(running.ToString(), running > 0 ? "running now" : "none running");
        int serious = warnings.Count(w => w.Level != "info");
        _tWarnings.Set(serious.ToString(), $"{warnings.Count(w => w.Level == "info")} for information", alert: warnings.Any(w => w.Level == "bad"));
        _tHosts.Set(AppData.Current.Hosts.Count.ToString(), $"{AppData.Current.Credentials.Count} credentials in the vault");
        var tiles = AppData.Current.DashboardTiles;
        int failing = tiles.Count(t => t.LastOk == false);
        _tChecks.Set(tiles.Count == 0 ? "—" : failing == 0 ? "✓" : failing.ToString(), tiles.Count == 0 ? "no checks pinned" : failing == 0 ? $"{tiles.Count} check(s) OK" : $"failing of {tiles.Count}", alert: failing > 0);
        foreach (var (m, tile) in _moduleTiles)
        {
            try
            {
                var has = _moduleResults.TryGetValue(m.Id, out var r);
                var t = m.Tile(has ? r.Outcome : null, has ? When(r.At, r.By, m.Id) : When(null, "", m.Id));
                tile.Set(t.Value, t.Detail, alert: t.Alert);
            }
            catch (Exception ex) { App.Log($"Dashboard tile ({m.Module}, {m.Id})", ex); }
        }
        _tStore.Set("AES-256", SecureStore.PasswordRequiredOnStart ? "GCM · master password required" : "GCM · key protected by Windows (DPAPI)" + (SecureStore.HasPassword ? " + password" : ""));
    }

    private void DrawSessions(StackPanel body, List<Overview.SessionRow> rows)
    {
        if (rows.Count == 0) { body.Children.Add(Hint("No session tabs are open. Connect from Hosts, the quick-connect box, or LOCAL in the sidebar.")); return; }
        body.Children.Add(Hint("connected / open, by type"));
        var grid = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        foreach (var c in Overview.Count(rows))
        {
            var t = Line($"{c.Kind.ToUpperInvariant()}  {c.Connected}/{c.Open}", FontStep.Body, mono: true);
            t.ToolTip = $"{c.Connected} connected of {c.Open} open" + (c.Kind == "Server view" ? " (server views have no connection of their own)" : "");
            t.Margin = new Thickness(0, 0, 18, 2);
            grid.Children.Add(t);
        }
        body.Children.Add(grid);
        foreach (var r in rows.OrderByDescending(r => Envs.IsProduction(r.Environment)).ThenBy(r => Array.IndexOf(Overview.SessionKinds, r.Kind)).Take(14))
        {
            var (glyph, brush) = r.Connected switch { true => ("●", "Good"), false => ("○", "Muted"), _ => ("◇", "Muted") };
            body.Children.Add(Row($"{r.Kind}: {r.Title}", r.Environment, () => GoTo(r.Content), menu: () => SessionMenu(r), glyph: glyph, glyphBrush: brush));
        }
        if (rows.Count > 14) body.Children.Add(Hint($"…and {rows.Count - 14} more."));
    }

    private static TabItem? TabOf(object content) => MainWindow.Instance.Panes.AllTabs.FirstOrDefault(t => ReferenceEquals(t.Content, content));
    private static void GoTo(object content) { if (TabOf(content) is { } t) MainWindow.Instance.Panes.Select(t); }

    private IEnumerable<object?> SessionMenu(Overview.SessionRow r)
    {
        yield return Menus.Item("Go to tab", () => GoTo(r.Content));
        if (r.Content is TerminalView tv) yield return Menus.Item("✕ Disconnect", tv.Disconnect, enabled: tv.IsConnected);
        if (r.Content is RdpView rv) yield return Menus.Item("✕ Disconnect", rv.Disconnect, enabled: rv.IsConnected);
        yield return Menus.Item("Close tab", () => { if (TabOf(r.Content) is { } t) MainWindow.Instance.CloseTab(t); });
    }

    private void DrawWarnings(StackPanel body, List<Overview.Warning> warnings)
    {
        if (warnings.Count == 0) { body.Children.Add(Hint("Nothing to report.")); return; }
        foreach (var w in warnings.OrderBy(w => w.Level switch { "bad" => 0, "warn" => 1, _ => 2 }))
        {
            var (glyph, brush) = w.Level switch { "bad" => ("✕", "Bad"), "warn" => ("⚠", "Warn"), _ => ("ⓘ", "Muted") };
            var buttons = w.Go != null ? new[] { Small(w.Fix ?? "Open", () => Go(w.Go)) } : null;
            var row = Row(w.Text, null, null, buttons, glyph: glyph, glyphBrush: brush);
            if (row.Children.OfType<TextBlock>().LastOrDefault() is { } t) { t.TextWrapping = TextWrapping.Wrap; t.TextTrimming = TextTrimming.None; }
            body.Children.Add(row);
        }
    }

    /// <summary>A nav key, or "settings:&lt;card title&gt;".</summary>
    public static void Go(string where)
    {
        if (!where.StartsWith("settings:")) { MainWindow.Instance.Navigate(where); return; }
        MainWindow.Instance.Navigate("settings");
        var card = where[9..];
        if (card == "Data & storage") SettingsView.Current?.RevealDataCard(); else SettingsView.Current?.RevealCard(card);
    }

    private void DrawSafety(StackPanel body, List<Overview.SessionRow> rows)
    {
        var prod = rows.Where(r => Envs.IsProduction(r.Environment)).ToList();
        body.Children.Add(Row(Offline.On ? $"Offline mode ON · {Offline.BlockedTotal} blocked this session" : "Offline mode off — internet destinations are allowed (each is marked ⚠)",
            null, null, new[] { Small("Settings", () => Go("settings:Offline mode")) }, glyph: Offline.On ? "⛔" : "○", glyphBrush: Offline.On ? "Warn" : "Muted"));
        body.Children.Add(Row(prod.Count == 0 ? "No PRODUCTION sessions open" : $"{prod.Count} PRODUCTION session(s): " + string.Join(", ", prod.Select(p => p.Title).Distinct()),
            prod.Count > 0 ? Envs.Production : null, prod.Count > 0 ? () => GoTo(prod[0].Content) : null, glyph: prod.Count > 0 ? null : "○", glyphBrush: "Muted"));
        var envs = AppData.Current.Hosts.GroupBy(h => Envs.Normalize(h.Environment)).Where(g => g.Key.Length > 0).OrderBy(g => Array.IndexOf(Envs.All, g.Key) is var i && i < 0 ? 99 : i)
            .Select(g => $"{Envs.Short(g.Key)} {g.Count()}").ToList();
        body.Children.Add(Row("Saved hosts by environment: " + (envs.Count == 0 ? "none labelled yet" : string.Join(" · ", envs)), null, () => MainWindow.Instance.Navigate("hosts"), glyph: "▣", glyphBrush: "Muted"));
        var bc = Api.Broadcasts.All.Count;
        body.Children.Add(Row(Api.LocalServer.Running ? $"Local API listening on 127.0.0.1:{S.LocalApiPort}" + (bc > 0 ? $" · {bc} broadcast(s)" : "") : "Local API off",
            null, null, new[] { Small("Settings", () => Go("settings:Local API & broadcast")) }, glyph: Api.LocalServer.Running ? "●" : "○", glyphBrush: Api.LocalServer.Running ? "Warn" : "Muted"));
        body.Children.Add(Row(AppLock.CanLock ? "App lock available (Ctrl+Shift+L)" : "App lock needs a master password or 2FA", null, null,
            new[] { Small("Security", () => Go("settings:Security & encryption")) }, glyph: AppLock.CanLock ? "🔒" : "○", glyphBrush: "Muted"));
    }

    /// <summary>0.3.0 §20: the pinned checks — one row each with its state, headline, when, and a small history bar.</summary>
    private void DrawChecks(StackPanel body)
    {
        var tiles = AppData.Current.DashboardTiles;
        if (tiles.Count == 0) body.Children.Add(Hint("Pin an uptime check (ping, a port, an HTTP address, a certificate's days left), a saved host's performance over SSH, or a mail domain's SPF / DMARC. Customise ▾ → Pin a check…, or 📌 from the toolbox."));
        foreach (var t in tiles)
        {
            var glyph = t.LastOk switch { true => "●", false => "✕", _ => "○" };
            var brush = t.LastOk switch { true => "Good", false => "Bad", _ => "Muted" };
            var when = t.LastAt is { } at ? $"{at:HH:mm:ss}" : "not checked yet";
            var row = Row($"{t.Name}   {t.LastValue}   · {when}" + (!t.Enabled ? " · paused" : ""), null, null,
                new[] { Small("Check", () => _ = RunTileAsync(t), "Run this check once, now") }, menu: () => TileMenu(t), glyph: glyph, glyphBrush: brush);
            row.ToolTip = $"{t.KindTitle} {t.TargetText}\n{t.LastDetail}" + (t.LastChangeAt is { } ch ? $"\nstate changed {ch:yyyy-MM-dd HH:mm}" : "");
            body.Children.Add(row);
            if (t.History.Count > 1) body.Children.Add(Sparkline(t.History));
        }
        var bar = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        bar.Children.Add(Small("＋ Pin a check…", () => { if (PinCheckDialog.Show(Window.GetWindow(this)) != null) Schedule(); }));
        if (tiles.Count > 0) bar.Children.Add(Small("Check all", () => _ = RunChecksAsync("manual")));
        body.Children.Add(bar);
    }

    private IEnumerable<object?> TileMenu(DashboardTile t)
    {
        yield return Menus.Item("Check now", () => _ = RunTileAsync(t));
        yield return Menus.Item("Edit…", () => { if (PinCheckDialog.Show(Window.GetWindow(this), t) != null) Schedule(); });
        yield return Menus.Item(t.Enabled ? "Pause" : "Resume", () => { t.Enabled = !t.Enabled; Save(); AppData.Save(); Schedule(); });
        yield return Menus.Item("Open in the toolbox", () => MainWindow.Instance.Navigate("toolbox"));
        yield return Menus.Item("Copy last result", () => Menus.Clip($"{t.Name} · {t.KindTitle} {t.TargetText} · {t.LastValue} · {t.LastDetail}"));
        yield return Menus.Item("✕ Remove", () => { if (Dialogs.Confirm("Remove check", $"Remove “{t.Name}” from the dashboard?", "Remove", danger: true)) { Checks.Unpin(t); Schedule(); } });
    }

    private async Task RunTileAsync(DashboardTile t) { await Checks.RunAsync(t); Schedule(); }

    /// <summary>A row of thin bars: height from the value (relative to the series' max), red when that run failed.</summary>
    private static FrameworkElement Sparkline(IReadOnlyList<double?> values)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(22, 0, 0, 4), Height = 14 };
        var max = values.Where(v => v != null).Select(v => v!.Value).DefaultIfEmpty(1).Max();
        foreach (var v in values)
        {
            var b = new Border { Width = 4, Margin = new Thickness(0, 0, 1, 0), VerticalAlignment = VerticalAlignment.Bottom, Height = v is { } x ? Math.Max(2, 14 * (max > 0 ? x / max : 1)) : 14 };
            b.SetResourceReference(Border.BackgroundProperty, v == null ? "Bad" : "Accent");
            p.Children.Add(b);
        }
        return p;
    }

    private void DrawInfra(StackPanel body)
    {
        var list = AppData.Current.InfraProviders;
        if (list.Count == 0) { body.Children.Add(Hint("No infrastructure providers. Infrastructure → ＋ Add connects Docker, Proxmox or Unraid; nothing is contacted until you add one.")); return; }
        foreach (var c in list)
        {
            if (Locks.Hidden("infra:" + c.Id)) { body.Children.Add(Row($"🔒 {c.Name}", null, () => MainWindow.Instance.Navigate("infra"))); continue; }
            var last = InfraStatus.For(c.Id);
            var id = "infra:" + c.Id;
            var state = last?.Text ?? "○ not checked";
            var when = last != null ? $"checked {last.At:HH:mm:ss} · {last.By}" : _skip.TryGetValue(id, out var why) ? "not checked — " + why : "not checked — press Check now";
            body.Children.Add(Row($"{c.Name} · {Infra.Infra.KindTitle(c.Kind)}", c.Environment, () => MainWindow.Instance.Navigate("infra"),
                new[] { Small("Check", () => _ = RunChecksAsync("manual", only: id), "Contact this provider once, now") }, menu: () => InfraMenu(c)));
            var sub = Line($"   {state}\n   {when}", FontStep.Small);
            sub.TextTrimming = TextTrimming.None;
            sub.SetResourceReference(TextBlock.ForegroundProperty, last is { Caps.Reachable: false } ? "Bad" : "Muted");
            body.Children.Add(sub);
        }
    }

    private IEnumerable<object?> InfraMenu(ProviderConfig c)
    {
        var id = "infra:" + c.Id;
        bool on = S.DashboardAutoSources.Contains(id);
        yield return Menus.Item("Check now", () => _ = RunChecksAsync("manual", only: id));
        yield return Menus.Item((on ? "✓ " : "") + "Include in automatic dashboard checks", () => { Overview.SetAutoSource(id, !on); Save(); Schedule(); });
        yield return Menus.Item("Open in Infrastructure", () => MainWindow.Instance.Navigate("infra"));
    }

    private void DrawIncidents(StackPanel body)
    {
        var active = IncidentStore.All.Where(i => i.IsActive).OrderBy(i => i.Severity).ThenByDescending(i => i.Updated).ToList();
        if (active.Count == 0) body.Children.Add(Hint("No open incidents."));
        foreach (var i in active.Take(8))
            body.Children.Add(Row($"{IncidentStore.SeverityText(i.Severity)} · {i.Title} · {i.Status} · {IncidentStore.Duration(DateTime.Now - i.Created)}", i.Environment,
                () => MainWindow.Instance.OpenIncidents(i), glyph: "⚑", glyphBrush: i.Severity <= IncidentSeverity.Sev2 ? "Bad" : "Warn"));
        var bar = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        bar.Children.Add(Small("Incidents", () => MainWindow.Instance.Navigate("incidents")));
        body.Children.Add(bar);
    }

    private void DrawTransfers(StackPanel body)
    {
        var running = TransferQueue.Jobs.Where(j => !j.Finished).Take(6).ToList();
        foreach (var j in running)
        {
            var p = new StackPanel { Margin = new Thickness(0, 2, 0, 4) };
            p.Children.Add(Line(j.Title + " · " + j.Route));
            var st = Line("", FontStep.Small);
            st.SetBinding(TextBlock.TextProperty, new Binding(nameof(TransferJob.Status)) { Source = j });
            st.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            p.Children.Add(st);
            var bar = new ProgressBar { Height = 4, Maximum = 100 };
            bar.SetBinding(System.Windows.Controls.Primitives.RangeBase.ValueProperty, new Binding(nameof(TransferJob.Percent)) { Source = j, Mode = BindingMode.OneWay });
            p.Children.Add(bar);
            body.Children.Add(p);
        }
        var recent = TransferHistory.All.Take(5).ToList();
        if (running.Count == 0 && recent.Count == 0) body.Children.Add(Hint("No transfers yet. Use the File commander or SFTP to copy files."));
        foreach (var e in recent)
            body.Children.Add(Row($"{e.Time:dd MMM HH:mm}  {e.Title} · {e.Files} file(s), {Fmt.Bytes(e.Bytes)}" + (e.Result != "ok" ? " — " + e.Result : ""), null, null,
                glyph: e.Result == "ok" ? "✓" : "✕", glyphBrush: e.Result == "ok" ? "Good" : "Bad"));
        var b = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        b.Children.Add(Small("File commander", () => MainWindow.Instance.Navigate("commander")));
        body.Children.Add(b);
    }

    private void DrawConnections(StackPanel body)
    {
        var hosts = AppData.Current.Hosts;
        var show = hosts.Where(h => h.Favorite).Concat(hosts.Where(h => !h.Favorite && h.LastConnected != null).OrderByDescending(h => h.LastConnected)).Take(10).ToList();
        if (show.Count == 0) body.Children.Add(Hint("Favourite hosts (★) and the ones you used recently appear here."));
        foreach (var h in show)
        {
            var when = h.LastConnected is { } t ? $" · {t:dd MMM HH:mm}" : "";
            // 0.5.1 #10: a padlocked host is listed by name only (opening it asks for the unlock).
            if (Locks.Hidden("host:" + h.Id)) { body.Children.Add(Row($"{(h.Favorite ? "★ " : "")}🔒 {h.Display}", null, null, HostButtons(h), mono: true)); continue; }
            body.Children.Add(Row($"{(h.Favorite ? "★ " : "")}{h.Display}   {h.Endpoint}{when}", Envs.For(h), null, HostButtons(h), mono: true));
        }
    }

    private void DrawNotes(StackPanel body)
    {
        var notes = NoteStore.All.OrderByDescending(n => n.Updated).Take(6).ToList();
        if (notes.Count == 0) body.Children.Add(Hint("No notes yet."));
        foreach (var n in notes)
            body.Children.Add(Row($"{n.Updated:dd MMM HH:mm}  {n.Title}", null, () => MainWindow.Instance.OpenNotes(note: n), glyph: "✎", glyphBrush: "Muted"));
    }

    /// <summary>Every provider with an active account, in neutral terms (ADR-020).</summary>
    private void DrawCloud(StackPanel body)
    {
        var connected = CloudProviders.All.Where(p => p.ActiveAccount != null).ToList();
        if (connected.Count == 0) { body.Children.Add(Hint("Add a Vultr, Contabo, Hetzner, Azure or Google Cloud account in Settings to see your cloud servers here.")); return; }
        foreach (var p in connected)
        {
            var head = Line($"{p.Title} · {p.ActiveAccount!.Name}", FontStep.Body);
            head.FontWeight = FontWeights.SemiBold;
            head.Margin = new Thickness(0, body.Children.Count > 0 ? 8 : 0, 0, 0);
            body.Children.Add(head);
            _cloud.TryGetValue(p.Id, out var r);
            body.Children.Add(Hint(When(r?.At, r?.By ?? "", p.Id)));
            if (r == null) continue;
            if (r.Error != null) { var e = Hint("✕ " + r.Error); e.SetResourceReference(TextBlock.ForegroundProperty, "Bad"); body.Children.Add(e); continue; }
            var list = r.Instances ?? new();
            int up = list.Count(i => i.State == CloudState.Running);
            body.Children.Add(Line((r.Summary != null ? r.Summary + " · " : "") + $"{up}/{list.Count} running", FontStep.Body));
            foreach (var i in list.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).Take(12))
            {
                var brush = i.State switch { CloudState.Running => "Good", CloudState.Busy => "Warn", CloudState.Stopped => "Muted", _ => "Bad" };
                body.Children.Add(Row($"{i.Name,-24} {i.Ipv4,-16} {i.Region,-10} {i.Plan}  {(i.State == CloudState.Running ? "" : i.StatusText)}", null,
                    () => MainWindow.Instance.Navigate(p.NavKey(p.InstanceDef)), mono: true, glyph: "●", glyphBrush: brush));
            }
            if (list.Count == 0) body.Children.Add(Hint("No instances."));
        }
    }

    private void DrawActivity(StackPanel body)
    {
        // 0.5.1 #8: the audit log is locked by default; the dashboard shows none of it until it is opened.
        if (!Locks.IsOpen("cat:audit"))
        {
            body.Children.Add(Hint("🔒 The audit log is locked. Open it to see recent activity here."));
            var open = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            open.Children.Add(Small("🔓 Open the audit log", () => MainWindow.Instance.Navigate("audit")));
            body.Children.Add(open);
            return;
        }
        var month = AuditLog.Months().FirstOrDefault();
        var items = month == null ? new List<AuditEntry>() : AuditLog.Read(month).Take(10).ToList();
        if (items.Count == 0) body.Children.Add(Hint("Nothing yet."));
        foreach (var e in items) body.Children.Add(Line($"{e.Time:dd MMM HH:mm}  {e.Actor,-3}  {e.Target}: {e.Action}", FontStep.Small, mono: true));
        var b = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        b.Children.Add(Small("Audit log", () => MainWindow.Instance.Navigate("audit")));
        body.Children.Add(b);
    }

    /// <summary>For the Privacy view: what the dashboard contacts and when.</summary>
    public static string ChecksDescription()
    {
        var s = AppSettings.Current;
        var auto = Overview.Sources().Where(x => s.DashboardAutoSources.Contains(x.Id)).ToList();
        return s.DashboardChecks == "manual" || auto.Count == 0 ? "only when you press Check now"
            : $"{Overview.ModeText(s.DashboardChecks, s.DashboardCheckMinutes)}: " + string.Join(", ", auto.Select(x => $"{x.Title} ({x.Host})"));
    }
}
