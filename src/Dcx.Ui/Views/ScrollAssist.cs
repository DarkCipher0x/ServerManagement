using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace DcxManager.Views;

/// <summary>
/// Scrolling everywhere the app scrolls (0.3.0 §6, DarkCipher0x: "allow scrollwheel click and hold to fast scroll"):
/// <list type="bullet">
/// <item><b>Middle-click autoscroll</b>, like browsers: press and hold the wheel and move away from the mark to scroll —
/// faster the further you go — and let go to stop; or click it once to latch, move, and click (or Esc) to stop. Works on
/// every WPF scroll area (lists, tables, Settings, help, the sidebar). Terminals are web views with their own middle-click
/// (paste) and aren't touched.</item>
/// <item><b>Wheel hand-off:</b> a list or box that is already at its end passes the wheel to the page around it, so the
/// page keeps scrolling instead of the wheel going dead over a nested list.</item>
/// <item><b>Menus:</b> a menu taller than the screen scrolls (the theme's menus host a ScrollViewer limited to
/// <c>MenuMaxHeight</c>, kept here from the work area).</item>
/// </list>
/// Installed once at start-up with class handlers; nothing opens a window or popup (the mark is an adorner).
/// </summary>
public static class ScrollAssist
{
    private static bool _installed;
    private static Session? _active;

    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnWheel));
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseDownEvent, new MouseButtonEventHandler(OnMouseDown), true);
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseUpEvent, new MouseButtonEventHandler(OnMouseUp), true);
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewKeyDownEvent, new KeyEventHandler(OnKey), true);
        UpdateMenuHeight();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => Application.Current?.Dispatcher.BeginInvoke(UpdateMenuHeight);
    }

    /// <summary>Menus may be at most 90% of the work area tall (then they scroll).</summary>
    public static void UpdateMenuHeight()
    {
        if (Application.Current is { } app) app.Resources["MenuMaxHeight"] = Math.Max(240, SystemParameters.WorkArea.Height * 0.9);
    }

    // ---- wheel hand-off -----------------------------------------------------------------------------------------------

    /// <summary>Tunnels outermost first: when the scroll area under the mouse (and every one between it and this one) can't
    /// move further that way, this one scrolls instead.</summary>
    private static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not ScrollViewer outer || !CanScroll(outer, e.Delta)) return;
        var inner = Ancestors(e.OriginalSource as DependencyObject).OfType<ScrollViewer>().TakeWhile(sv => sv != outer).ToList();
        if (inner.Count == 0 || inner.Any(sv => CanScroll(sv, e.Delta))) return;
        e.Handled = true;
        outer.ScrollToVerticalOffset(outer.VerticalOffset - e.Delta * SystemParameters.WheelScrollLines * 16 / 120.0);
    }

    /// <summary>Whether a scroll area can still move vertically in the wheel's direction.</summary>
    internal static bool CanScroll(ScrollViewer sv, int delta) =>
        sv.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled && sv.ScrollableHeight > 0 &&
        (delta > 0 ? sv.VerticalOffset > 0 : sv.VerticalOffset < sv.ScrollableHeight - 0.5);

    private static IEnumerable<DependencyObject> Ancestors(DependencyObject? d)
    {
        while (d != null)
        {
            yield return d;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
    }

    // ---- middle-click autoscroll ---------------------------------------------------------------------------------------

    private static void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_active != null)
        {
            // Any click ends a latched autoscroll (and does nothing else).
            _active.Stop();
            e.Handled = true;
            return;
        }
        if (e.ChangedButton != MouseButton.Middle || sender is not Window w) return;
        var target = TargetAt(e.OriginalSource as DependencyObject);
        if (target == null) return;
        _active = new Session(w, target, e.GetPosition(target));
        e.Handled = true;
    }

    private static void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || _active is not { } s) return;
        // Held and moved: release stops it. A quick click without moving: latched until the next click or Esc.
        if (s.Moved || s.Held > TimeSpan.FromMilliseconds(350)) s.Stop();
        e.Handled = true;
    }

    private static void OnKey(object sender, KeyEventArgs e)
    {
        if (_active != null && e.Key == Key.Escape) { _active.Stop(); e.Handled = true; }
    }

    /// <summary>The innermost scroll area under the point that can scroll at all.</summary>
    internal static ScrollViewer? TargetAt(DependencyObject? source) =>
        Ancestors(source).OfType<ScrollViewer>().FirstOrDefault(sv => sv.ScrollableHeight > 0 && sv.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled
                                                                      || sv.ScrollableWidth > 0 && sv.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled);

    /// <summary>Self-test: how far one tick moves for a pointer this far from the mark (DIPs): 0 inside the dead zone,
    /// growing with the distance.</summary>
    internal static double Speed(double distance) => Math.Abs(distance) < 8 ? 0 : Math.Sign(distance) * Math.Min(120, Math.Pow((Math.Abs(distance) - 8) / 6, 1.5));

    private sealed class Session
    {
        private readonly ScrollViewer _sv;
        private readonly Point _origin;
        private readonly DispatcherTimer _timer;
        private readonly Mark _mark;
        private readonly DateTime _started = DateTime.UtcNow;
        public bool Moved { get; private set; }
        public TimeSpan Held => DateTime.UtcNow - _started;

        public Session(Window w, ScrollViewer sv, Point origin)
        {
            _sv = sv; _origin = origin;
            _mark = new Mark(sv, origin);
            AdornerLayer.GetAdornerLayer(sv)?.Add(_mark);
            sv.Cursor = Cursors.ScrollAll;
            _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Input, (_, _) => Tick(), sv.Dispatcher);
            _timer.Start();
            w.Deactivated += OnDeactivated;
            sv.Unloaded += OnUnloaded;
        }

        private void OnDeactivated(object? s, EventArgs e) => Stop();
        private void OnUnloaded(object s, RoutedEventArgs e) => Stop();

        private void Tick()
        {
            var p = Mouse.GetPosition(_sv);
            double dx = p.X - _origin.X, dy = p.Y - _origin.Y;
            if (Math.Abs(dx) > 8 || Math.Abs(dy) > 8) Moved = true;
            if (_sv.ScrollableHeight > 0) _sv.ScrollToVerticalOffset(_sv.VerticalOffset + Speed(dy));
            if (_sv.ScrollableWidth > 0 && _sv.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled) _sv.ScrollToHorizontalOffset(_sv.HorizontalOffset + Speed(dx));
        }

        public void Stop()
        {
            _timer.Stop();
            AdornerLayer.GetAdornerLayer(_sv)?.Remove(_mark);
            _sv.ClearValue(FrameworkElement.CursorProperty);
            _sv.Unloaded -= OnUnloaded;
            if (Window.GetWindow(_sv) is { } w) w.Deactivated -= OnDeactivated;
            if (_active == this) _active = null;
        }
    }

    /// <summary>The autoscroll mark: a small circle with arrows where the wheel was pressed.</summary>
    private sealed class Mark(UIElement target, Point at) : Adorner(target)
    {
        protected override void OnRender(DrawingContext dc)
        {
            var fg = (Application.Current?.TryFindResource("Accent") as Brush) ?? Brushes.SteelBlue;
            var bg = (Application.Current?.TryFindResource("Surface") as Brush) ?? Brushes.White;
            dc.DrawEllipse(bg, new Pen(fg, 1.5), at, 13, 13);
            var tri = new StreamGeometry();
            using (var g = tri.Open())
            {
                g.BeginFigure(new Point(at.X - 4, at.Y - 4), true, true); g.LineTo(new Point(at.X + 4, at.Y - 4), true, false); g.LineTo(new Point(at.X, at.Y - 9), true, false);
                g.BeginFigure(new Point(at.X - 4, at.Y + 4), true, true); g.LineTo(new Point(at.X + 4, at.Y + 4), true, false); g.LineTo(new Point(at.X, at.Y + 9), true, false);
            }
            dc.DrawGeometry(fg, null, tri);
            dc.DrawEllipse(fg, null, at, 1.5, 1.5);
        }
    }
}
