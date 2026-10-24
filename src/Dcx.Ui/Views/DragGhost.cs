using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DcxManager.Views;

/// <summary>
/// What you drag, under the cursor (0.5.0, DarkCipher0x: "it must be animated so you can see what you are dragging"). A picture of
/// the dragged element (or a labelled pill for a selection of rows) follows the mouse on the window's adorner layer while
/// DragDrop runs; the original fades so it is clear what moves; the element under the cursor that would take the drop is
/// outlined. Everything is removed when the drag ends however it ends. The dashboard, the Hosts list and tree and the
/// sidebar sections use it.
/// </summary>
public sealed class DragGhost : Adorner
{
    private readonly Brush? _picture;
    private readonly Size _size;
    private readonly string? _label;
    private readonly Point _grab;
    private Point _at;

    private DragGhost(UIElement root, FrameworkElement? source, Point grab, string? label) : base(root)
    {
        IsHitTestVisible = false;
        _label = label;
        if (source != null && label == null && source.ActualWidth > 0)
        {
            // A snapshot (not a live VisualBrush): the source fades while dragging and mustn't fade the ghost with it.
            double scale = Math.Min(1, 360 / source.ActualWidth);
            _size = new Size(source.ActualWidth * scale, source.ActualHeight * scale);
            var dpi = VisualTreeHelper.GetDpi(source);
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(source.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(source.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bmp.Render(source);
            bmp.Freeze();
            _picture = new ImageBrush(bmp) { Opacity = 0.88 };
            _grab = new Point(grab.X * scale, grab.Y * scale);
        }
        else _grab = new Point(-14, -10);
    }

    private void MoveTo(Point p) { _at = p; InvalidateVisual(); }

    protected override void OnRender(DrawingContext dc)
    {
        var accent = TryFindResource("Accent") as Brush ?? Brushes.Teal;
        var surface = TryFindResource("Surface") as Brush ?? Brushes.Black;
        var fg = TryFindResource("FgStrong") as Brush ?? Brushes.White;
        dc.PushTransform(new TranslateTransform(_at.X - _grab.X, _at.Y - _grab.Y));
        if (_picture != null)
        {
            var r = new Rect(_size);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)), null, new Rect(6, 8, r.Width, r.Height), 8, 8);   // shadow
            dc.DrawRoundedRectangle(_picture, new Pen(accent, 2), r, 8, 8);
        }
        else
        {
            var ft = new FormattedText(_label ?? "", System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(TextElement.GetFontFamily(this), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 13, fg, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            var r = new Rect(0, 0, ft.Width + 24, ft.Height + 12);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)), null, new Rect(4, 5, r.Width, r.Height), 10, 10);
            dc.DrawRoundedRectangle(surface, new Pen(accent, 2), r, 10, 10);
            dc.DrawText(ft, new Point(12, 6));
        }
        dc.Pop();
    }

    /// <summary>Runs DragDrop.DoDragDrop with the ghost. <paramref name="grab"/> is where the mouse took hold of
    /// <paramref name="source"/>; <paramref name="label"/> draws a pill instead of a picture ("▣ 3 hosts").</summary>
    public static DragDropEffects Run(FrameworkElement source, DataObject data, DragDropEffects allowed, Point grab, string? label = null)
    {
        var root = Window.GetWindow(source)?.Content as UIElement;
        var layer = root == null ? null : AdornerLayer.GetAdornerLayer(root);
        if (root == null || layer == null) return DragDrop.DoDragDrop(source, data, allowed);
        var ghost = new DragGhost(root, source, grab, label);
        var start = Mouse.GetPosition(root);
        ghost.MoveTo(start);
        layer.Add(ghost);
        double opacity = source.Opacity;
        if (label == null) source.Opacity = Math.Min(opacity, 0.4);   // a picture of the element: it fades; a labelled pill (rows of a list) leaves the list as it is
        DragEventHandler over = (_, e) => ghost.MoveTo(e.GetPosition(root));
        root.AddHandler(UIElement.PreviewDragOverEvent, over, handledEventsToo: true);
        try { return DragDrop.DoDragDrop(source, data, allowed); }
        finally
        {
            root.RemoveHandler(UIElement.PreviewDragOverEvent, over);
            layer.Remove(ghost);
            source.Opacity = opacity;
            Target(null);
        }
    }

    // ---- the drop target's outline -------------------------------------------------------------------------------

    private sealed class Outline(UIElement e) : Adorner(e)
    {
        protected override void OnRender(DrawingContext dc)
        {
            var accent = TryFindResource("Accent") as Brush ?? Brushes.Teal;
            var r = new Rect(AdornedElement.RenderSize);
            var fill = accent.CloneCurrentValue(); fill.Opacity = 0.12;
            dc.DrawRoundedRectangle(fill, new Pen(accent, 2.5) { DashStyle = DashStyles.Dash }, r, 6, 6);
        }
    }

    private static (UIElement Element, Adorner Adorner)? _target;

    /// <summary>Outlines the element that would take the drop (null clears it). Cheap to call on every DragOver.</summary>
    public static void Target(UIElement? e)
    {
        if (_target is { } t && t.Element == e) return;
        if (_target is { } old) { AdornerLayer.GetAdornerLayer(old.Element)?.Remove(old.Adorner); _target = null; }
        if (e != null && AdornerLayer.GetAdornerLayer(e) is { } layer) { var a = new Outline(e) { IsHitTestVisible = false }; layer.Add(a); _target = (e, a); }
    }

    // ---- elements sliding into their new places ------------------------------------------------------------------

    /// <summary>Where each element is now, relative to <paramref name="root"/> (call before re-arranging).</summary>
    public static Dictionary<FrameworkElement, Point> Capture(IEnumerable<FrameworkElement> elements, Visual root)
    {
        var d = new Dictionary<FrameworkElement, Point>();
        foreach (var e in elements)
            if (e.IsVisible && e.IsDescendantOf(root)) d[e] = e.TransformToAncestor(root).Transform(new Point(0, 0));
        return d;
    }

    /// <summary>After re-arranging: each element starts at its old place and slides to its new one (about 0.2 s). Nothing
    /// moves when animation is reduced (Settings → Appearance → Animation, or Windows' own setting).</summary>
    public static void Play(Dictionary<FrameworkElement, Point> before, UIElement root, bool reduced)
    {
        if (reduced || before.Count == 0) return;
        root.UpdateLayout();
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        foreach (var (e, was) in before)
        {
            if (!e.IsVisible || !e.IsDescendantOf(root)) continue;
            var now = e.TransformToAncestor(root).Transform(new Point(0, 0));
            double dx = was.X - now.X, dy = was.Y - now.Y;
            if (Math.Abs(dx) < 1 && Math.Abs(dy) < 1) continue;
            var t = new TranslateTransform(dx, dy);
            e.RenderTransform = t;
            var dur = TimeSpan.FromMilliseconds(220);
            t.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(dx, 0, dur) { EasingFunction = ease });
            var ay = new DoubleAnimation(dy, 0, dur) { EasingFunction = ease };
            ay.Completed += (_, _) => e.RenderTransform = Transform.Identity;
            t.BeginAnimation(TranslateTransform.YProperty, ay);
        }
    }
}
