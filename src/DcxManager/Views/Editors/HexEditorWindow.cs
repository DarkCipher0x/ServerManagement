using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using DcxManager.Services;
using DcxManager.Views.Editors;

namespace DcxManager.Views;

/// <summary>
/// The hex editor (0.3.0 §13, DarkCipher0x: "hex editors"): offset | 16 bytes in hex | the same as text, for local or remote files
/// (File commander, Files) in memory only. Keys: arrows / Page / Home / End move (Shift selects), hex digits edit the hex
/// column, typing edits the text column (Tab switches), Insert toggles insert / overwrite, Delete / Backspace remove in
/// insert mode, Ctrl+Z / Ctrl+Y undo / redo, Ctrl+G go to offset, Ctrl+F find bytes ("DE AD BE EF") or text, F3 next,
/// Ctrl+S save (with the same changed-since-opened check as the text editor).
/// </summary>
public static class HexEditorWindow
{
    internal static readonly List<(Window Window, HexView View, Func<Task> Save)> Open_ = new();

    public static Window Open(string title, byte[] data, Func<byte[], Task> save, Func<Task<byte[]?>>? reread = null)
    {
        var doc = new HexDocument(data);
        var w = Dialogs.MakeWindow(title + "  (hex)", 980, 700);
        w.ShowInTaskbar = true; w.Owner = null; w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (SelfTest.Running) { w.WindowStartupLocation = WindowStartupLocation.Manual; w.Left = -10000; w.Top = -10000; w.ShowInTaskbar = false; w.ShowActivated = false; }
        var view = new HexView(doc);
        var scroll = new ScrollBar { Orientation = Orientation.Vertical, SmallChange = 1, LargeChange = 16 };
        var status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis }.Fs(FontStep.Small);
        status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var info = new TextBlock { Margin = new Thickness(10, 4, 10, 6) }.Fs(FontStep.Small).Mono();
        info.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var saveBtn = new Button { Content = "Save  (Ctrl+S)", Style = (Style)Application.Current.Resources["PrimaryButton"] };
        var original = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data));
        byte[]? lastFind = null;

        void Sync()
        {
            scroll.Maximum = Math.Max(0, view.Rows - view.VisibleRows);
            scroll.ViewportSize = view.VisibleRows;
            scroll.Value = view.TopRow;
            info.Text = doc.Describe(view.SelStart, view.SelLength) + $"   ·   {(view.InsertMode ? "INSERT" : "OVERWRITE")} · {(view.InText ? "text" : "hex")} column";
            status.Text = (doc.Dirty ? "modified · " : "") + $"{doc.Length:N0} bytes";
        }
        view.Moved += Sync;
        doc.Changed += Sync;
        scroll.Scroll += (_, _) => { view.TopRow = (int)scroll.Value; view.InvalidateVisual(); };
        view.SizeChanged += (_, _) => Sync();

        async Task DoSave()
        {
            try
            {
                saveBtn.IsEnabled = false;
                if (reread != null && await reread() is { } now && Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(now)) != original
                    && !Dialogs.Confirm("The file changed", "The file has changed since you opened it. Overwrite those changes?", "Overwrite", danger: true)) return;
                var bytes = doc.ToArray();
                await save(bytes);
                original = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
                status.Text = $"saved {DateTime.Now:HH:mm:ss} · {bytes.Length:N0} bytes";
            }
            catch (Exception ex) { Dialogs.Error("Save", ex.Message); }
            finally { saveBtn.IsEnabled = true; }
        }
        void Find(bool again)
        {
            if (!again || lastFind == null)
            {
                if (Dialogs.Prompt("Find", "Bytes in hex (e.g. DE AD BE EF), or text in quotes (\"PNG\"):") is not { Length: > 0 } q) return;
                var text = q.Length >= 2 && q[0] == '"' && q[^1] == '"';
                lastFind = HexDocument.Pattern(text ? q[1..^1] : q, asHex: !text);
                if (lastFind == null) { status.Text = "not hex bytes (or put text in quotes)"; return; }
            }
            var at = doc.Find(lastFind, view.Caret + (again ? 1 : 0));
            if (at < 0) { status.Text = "not found"; return; }
            view.Go(at, lastFind.Length);
        }
        saveBtn.Click += async (_, _) => await DoSave();
        w.PreviewKeyDown += async (_, e) =>
        {
            var mods = Keyboard.Modifiers;
            if (e.Key == Key.S && mods == ModifierKeys.Control) { e.Handled = true; await DoSave(); }
            else if (e.Key == Key.F && mods == ModifierKeys.Control) { e.Handled = true; Find(false); }
            else if (e.Key == Key.F3) { e.Handled = true; Find(true); }
            else if (e.Key == Key.G && mods == ModifierKeys.Control)
            {
                e.Handled = true;
                if (Dialogs.Prompt("Go to offset", "Offset (decimal, or hex with 0x):") is { } s
                    && (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? int.TryParse(s[2..], NumberStyles.HexNumber, null, out var o) : int.TryParse(s, out o)))
                    view.Go(Math.Clamp(o, 0, doc.Length), 0);
            }
        };
        w.Closing += (_, e) => { if (doc.Dirty && Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(doc.ToArray())) != original && !Dialogs.Confirm("Unsaved changes", "Close without saving?", "Discard", danger: true)) e.Cancel = true; };
        w.Closed += (_, _) => { lock (Open_) Open_.RemoveAll(o => o.Window == w); };

        var bar = new DockPanel { Margin = new Thickness(10, 8, 10, 8) };
        DockPanel.SetDock(saveBtn, Dock.Right);
        bar.Children.Add(saveBtn); bar.Children.Add(status);
        var body = new DockPanel();
        DockPanel.SetDock(scroll, Dock.Right);
        body.Children.Add(scroll); body.Children.Add(view);
        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top); DockPanel.SetDock(info, Dock.Bottom);
        root.Children.Add(bar); root.Children.Add(info); root.Children.Add(body);
        w.Content = root;
        lock (Open_) Open_.Add((w, view, DoSave));
        w.Show();
        Sync();
        if (!SelfTest.Running) view.Focus();
        return w;
    }
}

/// <summary>The hex grid, drawn directly (only the visible rows): fast for files of any size up to the cap.</summary>
public sealed class HexView : FrameworkElement
{
    public const int PerRow = 16;
    private readonly HexDocument _doc;
    private double _lineH = 18, _charW = 8;
    public int Caret { get; private set; }
    public int Anchor { get; private set; }
    public bool Low { get; private set; }          // the second (low) nibble of the caret byte
    public bool InText { get; private set; }
    public bool InsertMode { get; private set; }
    public int TopRow { get; set; }
    public event Action? Moved;

    public HexView(HexDocument doc)
    {
        _doc = doc;
        Focusable = true; FocusVisualStyle = null;
        doc.Changed += InvalidateVisual;
        ClipToBounds = true;
    }

    public int Rows => _doc.Length / PerRow + 1;
    public int VisibleRows => Math.Max(1, (int)(ActualHeight / _lineH));
    public int SelStart => Math.Min(Anchor, Caret);
    public int SelLength => Anchor == Caret ? 0 : Math.Abs(Anchor - Caret) + 1;

    public void Go(int at, int length)
    {
        Anchor = at; Caret = Math.Max(at, at + length - 1); Low = false;
        Reveal();
    }

    private void Move(int to, bool select)
    {
        Caret = Math.Clamp(to, 0, Math.Max(0, InsertMode ? _doc.Length : _doc.Length - 1));
        if (!select) Anchor = Caret;
        Low = false;
        Reveal();
    }

    private void Reveal()
    {
        var row = Caret / PerRow;
        if (row < TopRow) TopRow = row;
        if (row >= TopRow + VisibleRows) TopRow = row - VisibleRows + 1;
        InvalidateVisual();
        Moved?.Invoke();
    }

    private Typeface Face => new((Application.Current.TryFindResource("MonoFont") as FontFamily) ?? new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private double Size => Application.Current.TryFindResource("FsBase") is double d ? d : 13;
    private Brush Res(string k, Brush fallback) => Application.Current.TryFindResource(k) as Brush ?? fallback;

    protected override void OnRender(DrawingContext dc)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var probe = new FormattedText("0", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, Size, Brushes.Black, dpi);
        _charW = probe.WidthIncludingTrailingWhitespace; _lineH = Math.Ceiling(probe.Height * 1.25);
        dc.DrawRectangle(Res("Input", Brushes.White), null, new Rect(RenderSize));
        Brush fg = Res("Fg", Brushes.Black), muted = Res("Muted", Brushes.Gray), sel = Res("Selection", Brushes.LightBlue), accent = Res("Accent", Brushes.Teal);
        double hexX = _charW * 10, txtX = hexX + _charW * (PerRow * 3 + 2);
        for (int r = 0; r < VisibleRows + 1; r++)
        {
            int row = TopRow + r, start = row * PerRow;
            if (start > _doc.Length) break;
            double y = r * _lineH;
            dc.DrawText(new FormattedText($"{start:X8}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, Size, muted, dpi), new Point(4, y));
            for (int i = 0; i < PerRow; i++)
            {
                int at = start + i;
                if (at >= _doc.Length && !(at == _doc.Length && InsertMode && at == Caret)) break;
                double hx = hexX + i * 3 * _charW + (i >= 8 ? _charW : 0), tx = txtX + i * _charW;
                bool inSel = SelLength > 0 && at >= SelStart && at < SelStart + SelLength;
                if (inSel) { dc.DrawRectangle(sel, null, new Rect(hx - 1, y, _charW * 2 + 2, _lineH)); dc.DrawRectangle(sel, null, new Rect(tx, y, _charW, _lineH)); }
                if (at == Caret)
                {
                    var pen = new Pen(accent, 1.5);
                    dc.DrawRectangle(null, InText ? new Pen(muted, 1) : pen, new Rect(hx - 1 + (Low && !InText ? _charW : 0), y + 1, InText ? _charW * 2 + 2 : _charW + 1, _lineH - 2));
                    dc.DrawRectangle(null, InText ? pen : new Pen(muted, 1), new Rect(tx, y + 1, _charW, _lineH - 2));
                }
                if (at >= _doc.Length) continue;
                var b = _doc[at];
                dc.DrawText(new FormattedText(b.ToString("X2"), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, Size, b == 0 ? muted : fg, dpi), new Point(hx, y));
                var ch = b is >= 32 and < 127 ? ((char)b).ToString() : "·";
                dc.DrawText(new FormattedText(ch, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, Size, b is >= 32 and < 127 ? fg : muted, dpi), new Point(tx, y));
            }
        }
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        Focus();
        var p = e.GetPosition(this);
        double hexX = _charW * 10, txtX = hexX + _charW * (PerRow * 3 + 2);
        int row = TopRow + (int)(p.Y / _lineH);
        int col; bool text = p.X >= txtX;
        if (text) col = (int)((p.X - txtX) / _charW);
        else { var x = p.X - hexX; if (x > 8 * 3 * _charW) x -= _charW; col = (int)(x / (3 * _charW)); }
        if (col is < 0 or >= PerRow) return;
        InText = text;
        Move(row * PerRow + col, Keyboard.Modifiers == ModifierKeys.Shift);
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        TopRow = Math.Clamp(TopRow - Math.Sign(e.Delta) * 3, 0, Math.Max(0, Rows - VisibleRows));
        InvalidateVisual(); Moved?.Invoke();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift), ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        switch (e.Key)
        {
            case Key.Left: Move(Caret - 1, shift); break;
            case Key.Right: Move(Caret + 1, shift); break;
            case Key.Up: Move(Caret - PerRow, shift); break;
            case Key.Down: Move(Caret + PerRow, shift); break;
            case Key.PageUp: Move(Caret - PerRow * VisibleRows, shift); break;
            case Key.PageDown: Move(Caret + PerRow * VisibleRows, shift); break;
            case Key.Home: Move(ctrl ? 0 : Caret - Caret % PerRow, shift); break;
            case Key.End: Move(ctrl ? _doc.Length : Caret - Caret % PerRow + PerRow - 1, shift); break;
            case Key.Tab: InText = !InText; Low = false; InvalidateVisual(); Moved?.Invoke(); break;
            case Key.Insert: InsertMode = !InsertMode; Moved?.Invoke(); InvalidateVisual(); break;
            case Key.Z when ctrl: _doc.Undo(); Move(Math.Min(Caret, Math.Max(0, _doc.Length - 1)), false); break;
            case Key.Y when ctrl: _doc.Redo(); Move(Caret, false); break;
            case Key.Delete when InsertMode:
                if (SelLength > 0) { _doc.Delete(SelStart, SelLength); Move(SelStart, false); } else _doc.Delete(Caret, 1);
                break;
            case Key.Back when InsertMode && Caret > 0: _doc.Delete(Caret - 1, 1); Move(Caret - 1, false); break;
            default: return;
        }
        e.Handled = true;
    }

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        foreach (var ch in e.Text) TypeChar(ch);
        e.Handled = true;
    }

    /// <summary>One typed character (also the self-test's way in).</summary>
    public void TypeChar(char ch)
    {
        if (InText)
        {
            if (ch > 0xFF || char.IsControl(ch)) return;
            if (InsertMode) _doc.Insert(Caret, [(byte)ch]); else _doc.Overwrite(Caret, (byte)ch);
            Move(Caret + 1, false);
            return;
        }
        if (!Uri.IsHexDigit(ch)) return;
        var v = Convert.ToByte(ch.ToString(), 16);
        if (!Low)
        {
            if (InsertMode || Caret >= _doc.Length) _doc.Insert(Caret, [(byte)(v << 4)]);
            else _doc.Overwrite(Caret, (byte)((v << 4) | (_doc[Caret] & 0x0F)));
            Low = true; Anchor = Caret; InvalidateVisual(); Moved?.Invoke();
        }
        else
        {
            _doc.Overwrite(Caret, (byte)((_doc[Caret] & 0xF0) | v));
            Move(Caret + 1, false);
        }
    }

    internal void SetModeForTest(bool text, bool insert) { InText = text; InsertMode = insert; }
}
