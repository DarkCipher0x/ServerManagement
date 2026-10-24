using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DcxManager.Services;

namespace DcxManager.Views;

/// <summary>
/// An extra window for another monitor (0.6.4, ADR-082; DarkCipher0x: "multi-monitor support, where multiple monitors could have
/// multiple tabs, plus an extra host pane"). Like the main window's document area it holds tabs in panes that split
/// right / down (<see cref="DocRoot"/> holds a <see cref="PaneFrame"/> or a tree of <see cref="SplitNode"/>s, managed
/// by the shared <see cref="PaneHost"/>), and on its left it has its own host pane (<see cref="SessionManagerPane"/>):
/// pinned open, or auto-collapsing to a slim "Hosts" tab at the window's edge (AppSettings.WindowHostPane, a policy).
/// Whatever you open from it, or while the window is active, opens in this window.
/// <para>A window made with New window (or restored at start) stays open when its last tab leaves (<see cref="KeepOpen"/>);
/// one made by popping a tab out closes then, as before. Closing a window that still has tabs asks whether to move
/// them to the main window or close them.</para>
/// </summary>
public sealed class FloatWindow : DetachedWindow
{
    private const double StripWidth = 22;
    private readonly PaneHost _host;
    private readonly Parts _p;
    private bool _paneOpen;
    private readonly DispatcherTimer _paneClose = new() { Interval = TimeSpan.FromMilliseconds(450) };

    /// <summary>The window's document area: one pane, or a tree of split panes.</summary>
    public Border DocRoot => _p.DocRoot;
    public SessionManagerPane HostPane { get; }
    /// <summary>Stays open (empty) when its last tab leaves: New window and restored windows.</summary>
    public bool KeepOpen { get; set; }
    /// <summary>The pane last used in this window (where new tabs go while the window is active).</summary>
    public DocPane? LastActive { get; set; }
    /// <summary>The window's first pane (the only one until it is split).</summary>
    public PaneFrame Frame => PaneHost.FramesIn(DocRoot.Child as FrameworkElement).First();
    /// <summary>"Window 2", "Window 3"… as shown in titles and menus.</summary>
    public string Label { get; set; } = "Window";

    private sealed record Parts(Grid Root, ColumnDefinition PaneCol, Border PaneHostBorder, GridSplitter Splitter, Border Strip, Border DocRoot);

    public FloatWindow(PaneHost host, Window? owner) : this(host, owner, Build(host)) { }

    private FloatWindow(PaneHost host, Window? owner, Parts p) : base(owner, p.Root, 1100, 720)
    {
        _host = host;
        _p = p;
        HostPane = new SessionManagerPane { BeforeOpen = () => _host.ActivateWindow(this) };
        _p.PaneHostBorder.Child = BuildPanel();
        BuildStrip();
        CommandBindings.Add(new CommandBinding(Commands.CloseTab, (_, e) =>
        {
            var tab = e.Parameter as TabItem ?? (LastActive ?? Frame.Pane).SelectedItem as TabItem;
            if (tab != null) host.CloseRequested?.Invoke(tab);
        }));
        Activated += (_, _) => _host.ActivateWindow(this);
        _p.Splitter.DragCompleted += (_, _) =>
        {
            if (Mode != "pinned") return;
            AppSettings.Current.WindowHostPaneWidth = Math.Round(_p.PaneCol.ActualWidth);
            try { AppSettings.Save(); } catch (Exception ex) { App.Log("Window host pane width", ex); }
        };
        _paneClose.Tick += (_, _) =>
        {
            if (_p.PaneHostBorder.IsMouseOver || _p.Strip.IsMouseOver || Mouse.Captured != null || Mouse.LeftButton == MouseButtonState.Pressed) return;
            _paneClose.Stop();
            SetPaneOpen(false);
        };
        _p.PaneHostBorder.MouseEnter += (_, _) => _paneClose.Stop();
        _p.PaneHostBorder.MouseLeave += (_, _) => { if (Mode == "auto" && _paneOpen) _paneClose.Start(); };
        _p.DocRoot.PreviewMouseDown += (_, _) => { if (Mode == "auto" && _paneOpen) SetPaneOpen(false); };
        ApplyPaneMode();
    }

    private static Parts Build(PaneHost host)
    {
        var root = new Grid();
        var paneCol = new ColumnDefinition { Width = new GridLength(250) };
        root.ColumnDefinitions.Add(paneCol);
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var strip = new Border { Cursor = Cursors.Hand, Background = Brushes.Transparent, BorderThickness = new Thickness(0, 0, 2, 0), Visibility = Visibility.Collapsed, AllowDrop = true };
        strip.SetResourceReference(Border.BorderBrushProperty, "Line");
        System.Windows.Automation.AutomationProperties.SetName(strip, "Show the host pane");
        var paneBorder = new Border();
        var splitter = new GridSplitter { Width = 4, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeBehavior = GridResizeBehavior.PreviousAndNext, Background = Brushes.Transparent, ToolTip = "Drag to resize the host pane" };
        Grid.SetColumn(splitter, 1);
        var doc = new Border { Child = host.NewFrame() };
        Grid.SetColumn(doc, 2);
        root.Children.Add(strip);
        root.Children.Add(paneBorder);
        root.Children.Add(splitter);
        root.Children.Add(doc);
        return new Parts(root, paneCol, paneBorder, splitter, strip, doc);
    }

    // ---- the host pane: pinned or auto-collapsing ------------------------------------------------------------------

    private static string Mode => AppSettings.Current.WindowHostPane == "auto" ? "auto" : "pinned";
    private static double PaneWidth => Math.Clamp(AppSettings.Current.WindowHostPaneWidth is > 0 and var w ? w : 250, 160, 520);
    internal bool HostPaneOpen => Mode == "pinned" || _paneOpen;

    private readonly Button _pin = new() { Margin = new Thickness(0, 0, 4, 0) };

    private FrameworkElement BuildPanel()
    {
        var dock = new DockPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 6, 6, 2) };
        if (Application.Current.TryFindResource("SmallButton") is Style st) _pin.Style = st;
        _pin.Click += (_, _) => SetMode(Mode == "auto" ? "pinned" : "auto");
        head.Children.Add(_pin);
        head.Children.Add(new TextBlock { Text = "▣ Hosts", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) });
        DockPanel.SetDock(head, Dock.Top);
        dock.Children.Add(head);
        dock.Children.Add(HostPane);
        return dock;
    }

    private void BuildStrip()
    {
        var icon = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 6) };
        if (Icons.For("▣") is { } c) { icon.Text = c.ToString(); icon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont"); icon.Fs(FontStep.Caption); }
        else icon.Text = "▣";
        var label = new TextBlock { Text = "Hosts", HorizontalAlignment = HorizontalAlignment.Center, LayoutTransform = new RotateTransform(-90) }.Fs(FontStep.Small);
        foreach (var t in new[] { icon, label }) t.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _p.Strip.Child = new StackPanel { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 0, 0), Children = { icon, label } };
        _p.Strip.ToolTip = "Hosts: point here to slide the host pane out";
        void Hot(bool on)
        {
            _p.Strip.SetResourceReference(Border.BorderBrushProperty, on ? "Accent" : "Line");
            foreach (var t in new[] { icon, label }) t.SetResourceReference(TextBlock.ForegroundProperty, on ? "Accent" : "Muted");
        }
        _p.Strip.MouseEnter += (_, _) => { Hot(true); SetPaneOpen(true); };
        _p.Strip.MouseLeave += (_, _) => Hot(false);
        _p.Strip.MouseLeftButtonUp += (_, _) => SetPaneOpen(true);
    }

    /// <summary>Lays the host pane out for the current mode (also when the setting changes).</summary>
    internal void ApplyPaneMode()
    {
        bool auto = Mode == "auto";
        bool managed = PolicySettings.Get(nameof(AppSettings.WindowHostPane)) != null;
        _pin.Content = auto ? "📍" : "📌";
        _pin.IsEnabled = !managed;
        _pin.ToolTip = managed ? "🔒 Set by your organisation (Group Policy: WindowHostPane)."
            : auto ? "Auto-collapse: the host pane folds to a Hosts tab at the window's edge. Click to pin it open."
                   : "Pinned: the host pane stays open. Click to auto-collapse it.";
        var b = _p.PaneHostBorder;
        _p.Strip.Visibility = auto ? Visibility.Visible : Visibility.Collapsed;
        _p.Splitter.Visibility = auto ? Visibility.Collapsed : Visibility.Visible;
        if (auto)
        {
            _p.PaneCol.Width = new GridLength(StripWidth);
            Grid.SetColumn(b, 0); Grid.SetColumnSpan(b, 3);
            b.HorizontalAlignment = HorizontalAlignment.Left;
            b.Width = PaneWidth;
            b.Margin = new Thickness(StripWidth, 0, 0, 0);
            Panel.SetZIndex(b, 10);
            // No drop shadow: an effect renders the subtree as a bitmap and blurs its text (0.6.3).
            b.SetResourceReference(Border.BackgroundProperty, "Surface");
            b.SetResourceReference(Border.BorderBrushProperty, "Line");
            b.BorderThickness = new Thickness(0, 0, 1, 0);
            b.Visibility = _paneOpen ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            _paneOpen = false; _paneClose.Stop();
            Grid.SetColumn(b, 0); Grid.SetColumnSpan(b, 1);
            b.HorizontalAlignment = HorizontalAlignment.Stretch;
            b.Width = double.NaN;
            b.Margin = new Thickness(0);
            Panel.SetZIndex(b, 0);
            b.SetResourceReference(Border.BackgroundProperty, "Surface");
            b.BorderThickness = new Thickness(0);
            b.Visibility = Visibility.Visible;
            _p.PaneCol.Width = new GridLength(PaneWidth);
        }
    }

    internal void SetPaneOpen(bool open)
    {
        if (Mode != "auto" || open == _paneOpen) return;
        _paneOpen = open;
        if (!open) _paneClose.Stop();
        // 0.6.7: the window's sessions are native windows (WebView2 terminals, ActiveX desktops) WPF can't draw over
        // ("airspace"), so the pane opens beside them (the column widens) rather than on top.
        _p.PaneCol.Width = new GridLength(open ? StripWidth + PaneWidth : StripWidth);
        _p.PaneHostBorder.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Pinned or auto-collapsing, for every extra window (refused when Group Policy sets it).</summary>
    internal void SetMode(string mode)
    {
        if (PolicySettings.Get(nameof(AppSettings.WindowHostPane)) != null) { MainWindow.Instance?.Status("🔒 The windows' host pane is set by your organisation (Group Policy: WindowHostPane)."); return; }
        AppSettings.Current.WindowHostPane = mode == "auto" ? "auto" : "pinned";
        try { AppSettings.Save(); } catch (Exception ex) { App.Log("Window host pane mode", ex); }
        foreach (var w in _host.Floats) { w._paneOpen = false; w.ApplyPaneMode(); }
    }

    /// <summary>Title: the window's label and its selected tab.</summary>
    internal void UpdateTitle(TabItem? selected) => Title = selected != null ? $"{selected.Header} — {Label} — ServerManagement" : $"{Label} — ServerManagement";

    /// <summary>Where the window is now (normal bounds), for restoring it next time.</summary>
    internal WindowPlacement Placement()
    {
        var r = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        return new WindowPlacement { Left = r.Left, Top = r.Top, Width = r.Width, Height = r.Height, Maximized = WindowState == WindowState.Maximized };
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!IsQuiet && !_host.ConfirmCloseWindow(this)) { e.Cancel = true; return; }
        base.OnClosing(e);
    }

    protected override void OnUserClosing() => _host.FloatClosing(this);
}
