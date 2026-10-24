using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using DcxManager.Ai;
using DcxManager.Services;

namespace DcxManager.Views;

/// <summary>Where the AI Engineer sidebar is: docked on the right, collapsed to a strip, hidden, or in its own window.</summary>
public enum AiDockState { Docked, Collapsed, Hidden, Detached }

/// <summary>
/// Owns the right-hand dock column of the main window and the one <see cref="AiPanel"/>. Detach moves that same panel
/// (and so the same <see cref="AiWorkspace"/> and conversation) into an <see cref="AiWindow"/>; reattach moves it back.
/// Closing the window returns the panel to the dock collapsed. State and width persist in the encrypted settings.
/// </summary>
public sealed class AiDock : IDockController
{
    public const double DefaultWidth = 380, MinWidth = 260, MaxWidth = 900, StripWidth = 30;
    /// <summary>The docked panel never takes more than this share of the window, and always leaves the open screen at
    /// least <see cref="ContentMin"/> DIPs beside the sidebar (s40: a 750 px panel squeezed the dashboard on a smaller window).</summary>
    public const double MaxShare = 0.40, ContentMin = 620, NavWidth = 210;

    private readonly Window _owner;
    private readonly ColumnDefinition _col;
    private readonly GridSplitter _splitter;
    private readonly Border _host;
    private readonly FrameworkElement _strip;
    private bool _shuttingDown;

    public AiPanel Panel { get; }
    public AiWindow? Window { get; private set; }
    public AiDockState State { get; private set; }
    /// <summary>Docked width in DIPs (kept while collapsed, hidden or detached).</summary>
    public double Width { get; private set; }
    public event Action? StateChanged;

    public AiDock(Window owner, ColumnDefinition col, GridSplitter splitter, Border host)
    {
        _owner = owner; _col = col; _splitter = splitter; _host = host;
        _col.MinWidth = 0; _col.MaxWidth = MaxWidth;
        Panel = new AiPanel(AiWorkspace.Current);
        Panel.DetachToggleRequested += () => { if (State == AiDockState.Detached) Apply(AiDockState.Docked); else Apply(AiDockState.Detached); };
        Panel.CollapseRequested += () => Apply(AiDockState.Collapsed);
        Panel.HideRequested += () => Apply(AiDockState.Hidden);
        AiWorkspace.Current.FocusRequested += Reveal;
        _splitter.DragCompleted += OnResized;
        _splitter.MouseDoubleClick += (_, _) => Apply(AiDockState.Collapsed);
        // The window got smaller or larger: fit the panel again (the saved width is kept for a bigger window).
        _owner.SizeChanged += (_, _) => { if (State == AiDockState.Docked) _col.Width = new GridLength(Fitted()); };
        _strip = BuildStrip();

        var s = AppSettings.Current;
        Width = Math.Clamp(s.AiPanelWidth is > 0 ? s.AiPanelWidth : DefaultWidth, MinWidth, MaxWidth);
        // A detached window is reopened once the main window is on screen (RestoreDetached); dock it until then.
        Apply(Parse(s.AiPanelState) is var st && st == AiDockState.Detached ? AiDockState.Docked : st, persist: false);
    }

    public static AiDockState Parse(string? s) => Enum.TryParse<AiDockState>(s, true, out var st) ? st : AiDockState.Docked;

    /// <summary>Called when the main window has rendered: reopens the detached window if that is how the app was left.</summary>
    public void RestoreDetached()
    {
        if (Parse(AppSettings.Current.AiPanelState) == AiDockState.Detached && State != AiDockState.Detached) Apply(AiDockState.Detached);
    }

    public void Apply(AiDockState st, bool persist = true)
    {
        if (st == AiDockState.Detached) { Detach(); if (persist) Save(); return; }
        TakeBackFromWindow();
        switch (st)
        {
            case AiDockState.Docked:
                _host.Child = Panel;
                _col.Width = new GridLength(Fitted());
                _splitter.Visibility = Visibility.Visible;
                break;
            case AiDockState.Collapsed:
                _host.Child = _strip;
                _col.Width = new GridLength(StripWidth);
                _splitter.Visibility = Visibility.Collapsed;
                break;
            default:
                _host.Child = null;
                _col.Width = new GridLength(0);
                _splitter.Visibility = Visibility.Collapsed;
                break;
        }
        _host.Visibility = st == AiDockState.Hidden ? Visibility.Collapsed : Visibility.Visible;
        Panel.IsDetached = false;
        State = st;
        if (persist) Save();
        StateChanged?.Invoke();
    }

    /// <summary>Ctrl+Shift+A / sidebar item: show it when it isn't, hide it when it is docked, bring the window forward.</summary>
    public void Toggle()
    {
        if (State == AiDockState.Docked) Apply(AiDockState.Hidden);
        else Reveal();
    }

    /// <summary>An action posted into the workspace: make sure the user sees it, and put the cursor in the question box.</summary>
    public void Reveal()
    {
        if (State == AiDockState.Detached && Window != null)
        {
            if (Window.WindowState == WindowState.Minimized) Window.WindowState = WindowState.Normal;
            if (!TestMode.Running) Window.Activate();
        }
        else if (State != AiDockState.Docked) Apply(AiDockState.Docked);
        Panel.FocusInput();
    }

    public void SetWidth(double w)
    {
        Width = Math.Clamp(w, MinWidth, MaxWidth);
        if (State == AiDockState.Docked) _col.Width = new GridLength(Fitted());
        Save();
    }

    /// <summary>The docked width to use now: the saved width, limited to <see cref="MaxShare"/> of the window and to what
    /// leaves the open screen <see cref="ContentMin"/> DIPs (never below <see cref="MinWidth"/>).</summary>
    public double Fitted() => FitWidth(Width, _owner.ActualWidth > 0 ? _owner.ActualWidth : _owner.Width);

    public static double FitWidth(double saved, double window)
    {
        if (double.IsNaN(window) || window <= 0) return Math.Clamp(saved, MinWidth, DefaultWidth);
        var room = Math.Min(window * MaxShare, window - NavWidth - ContentMin);
        return Math.Clamp(Math.Min(saved, room), MinWidth, MaxWidth);
    }

    /// <summary>App exit: close the window without docking back, so "detached" is what the next start restores.</summary>
    public void ShutDown()
    {
        _shuttingDown = true;
        if (Window is { } w) { Window = null; w.Release(); w.CloseQuietly(); }
    }

    // ---- internals ------------------------------------------------------------------------------------------------

    private void Detach()
    {
        if (State == AiDockState.Detached && Window != null) { Reveal(); return; }
        _host.Child = null;   // the panel can have only one parent
        _host.Visibility = Visibility.Collapsed;
        _col.Width = new GridLength(0);
        _splitter.Visibility = Visibility.Collapsed;
        Window = new AiWindow(_owner, Panel, OnWindowClosedByUser);
        Panel.IsDetached = true;
        State = AiDockState.Detached;
        if (TestMode.Running) Window.ShowActivated = false;
        Window.Show();
        StateChanged?.Invoke();
    }

    private void TakeBackFromWindow()
    {
        if (Window is not { } w) return;
        Window = null;
        w.Release();
        w.CloseQuietly();
        if (!TestMode.Running && _owner.IsVisible) _owner.Activate();
    }

    private void OnWindowClosedByUser(AiWindow w)
    {
        if (_shuttingDown || w != Window) return;
        Window = null;
        w.Release();
        Apply(AiDockState.Collapsed);
    }

    private void OnResized(object sender, DragCompletedEventArgs e)
    {
        var w = _col.ActualWidth;
        if (w < 120) { Apply(AiDockState.Collapsed); return; }
        SetWidth(w);
    }

    private void Save()
    {
        var s = AppSettings.Current;
        s.AiPanelState = State.ToString().ToLowerInvariant();
        s.AiPanelWidth = Width;
        try { AppSettings.Save(); } catch (Exception ex) { Log.Write("AI sidebar state", ex); }
    }

    private FrameworkElement BuildStrip()
    {
        var label = new TextBlock { Text = "✦  AI Engineer", LayoutTransform = new System.Windows.Media.RotateTransform(90), Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        var b = new Button
        {
            Content = label, VerticalContentAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
            MinWidth = 0, Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent, Cursor = Cursors.Hand,
            ToolTip = "Show the AI Engineer sidebar (Ctrl+Shift+A)",
        };
        b.Click += (_, _) => Reveal();
        Menus.Attach(b, () => new object?[]
        {
            Menus.Item("Show docked", () => Apply(AiDockState.Docked)),
            Menus.Item("Detach into a window", () => Apply(AiDockState.Detached)),
            Menus.Item("Hide", () => Apply(AiDockState.Hidden)),
        });
        return b;
    }
}
