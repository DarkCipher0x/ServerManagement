using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DcxManager.Views.Editors;

/// <summary>
/// Line numbers beside the editor's TextBox (0.3.0 §13): one number per logical line, placed at the vertical position
/// the TextBox reports for that line's first character, so wrapped lines take their room and scrolling stays in step.
/// Drawn directly (no items), so a 100,000-line file costs nothing.
/// </summary>
public sealed class EditorGutter : FrameworkElement
{
    private readonly TextBox _box;
    private int _digits = 2;

    public EditorGutter(TextBox box)
    {
        _box = box;
        Margin = new Thickness(0, 0, 2, 0);
        _box.TextChanged += (_, _) => { Resize(); InvalidateVisual(); };
        _box.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => InvalidateVisual()));
        _box.SizeChanged += (_, _) => InvalidateVisual();
        _box.LayoutUpdated += (_, _) => { int f; try { f = _box.GetFirstVisibleLineIndex(); } catch (Exception) { return; } if (f != _lastFirst) InvalidateVisual(); };
        Resize();
    }

    private int _lastFirst = -1;

    private void Resize()
    {
        int lines = CountLines(_box.Text);
        _digits = Math.Max(2, lines.ToString(CultureInfo.InvariantCulture).Length);
        Width = _digits * _box.FontSize * 0.65 + 14;
    }

    public static int CountLines(string text) => text.Length == 0 ? 1 : text.AsSpan().Count('\n') + 1;

    /// <summary>The 1-based logical line of a character index (counting '\n', so CRLF files count right too).</summary>
    public static int LogicalLine(string text, int charIndex) => text.AsSpan(0, Math.Clamp(charIndex, 0, text.Length)).Count('\n') + 1;

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle((Application.Current?.Resources["Surface"] as Brush) ?? Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_box.Text.Length == 0 && ActualHeight <= 0) return;
        int first, last;
        try { first = _box.GetFirstVisibleLineIndex(); last = _box.GetLastVisibleLineIndex(); } catch (Exception) { return; }
        _lastFirst = first;
        if (first < 0) return;
        var brush = (Application.Current?.Resources["Muted"] as Brush) ?? Brushes.Gray;
        var typeface = new Typeface(_box.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var text = _box.Text;
        int shownLogical = -1;
        for (int li = first; li <= last; li++)
        {
            int start;
            try { start = _box.GetCharacterIndexFromLineIndex(li); } catch (Exception) { break; }
            if (start < 0) break;
            int logical = LogicalLine(text, start);
            if (logical == shownLogical) continue;   // a wrapped continuation of the same line
            shownLogical = logical;
            Rect r;
            try { r = _box.GetRectFromCharacterIndex(start); } catch (Exception) { break; }
            if (r.IsEmpty) continue;
            var ft = new FormattedText(logical.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, _box.FontSize, brush, dpi);
            dc.DrawText(ft, new Point(ActualWidth - ft.Width - 8, r.Y));
        }
    }
}
