using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DcxManager.Services;
using DcxManager.Vnc;

namespace DcxManager.Views;

/// <summary>
/// A VNC desktop in a tab (0.5.1 #19, ADR-069): Macs (Screen Sharing, signed in with the macOS account) and Linux VNC
/// servers, through an SSH tunnel by default. The app's own RFB client (no plug-in, no browser) draws into a bitmap at up
/// to 30 frames a second; keyboard and mouse go to the remote desktop while the picture has the focus. Scale to the tab or
/// 1:1, view only, Ctrl+Alt+Del, the Windows key as ⌘ / Super, clipboard to the remote side on request (from it only when
/// the host allows it), screenshot, reconnect.
/// </summary>
public sealed class VncView : UserControl, IHostView, IDisposable
{
    private readonly HostEntry _host;
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Image _image = new() { Stretch = Stretch.Uniform, SnapsToDevicePixels = true };
    private readonly Border _surface = new() { Focusable = true, FocusVisualStyle = null, Background = Brushes.Black };
    private readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly Border _overlay = new() { Padding = new Thickness(24) };
    private readonly TextBlock _overlayText = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Button _reconnect;
    private readonly CheckBox _scale = new() { Content = "Scale", ToolTip = "Fit the desktop to the tab (off: 1:1 with scroll bars)", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
    private readonly CheckBox _viewOnly = new() { Content = "View only", ToolTip = "Show the desktop without sending keys or the mouse", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
    private readonly DispatcherTimer _paint;
    private readonly Dictionary<Key, uint> _down = new();
    private VncSession? _session;
    private WriteableBitmap? _bmp;
    private CancellationTokenSource? _cts;
    private int _buttons, _dx0 = int.MaxValue, _dy0 = int.MaxValue, _dx1, _dy1;
    private readonly object _dirty = new();
    private bool _disposed, _resized;

    public HostEntry? Host => _host;
    public bool IsConnected => _session != null && !_disposed;
    internal VncSession? SessionForTest => _session;
    internal string StatusText => _status.Text;
    internal FrameworkElement ImageForTest => _image;
    /// <summary>Self-test: the drawn pixel (0x00RRGGBB), or null before the first frame.</summary>
    internal uint? PixelForTest(int x, int y)
    {
        if (_bmp == null) return null;
        var px = new int[1];
        _bmp.CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
        return (uint)px[0] & 0xFFFFFF;
    }

    public VncView(HostEntry host, bool connectOnOpen = true)
    {
        _host = host;
        var bar = new DockPanel { Margin = new Thickness(8, 5, 8, 5) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(buttons, Dock.Right);
        Button B(string text, string tip, Action click)
        {
            var b = new Button { Content = text, ToolTip = tip, Style = (Style)Application.Current.Resources["SmallButton"] };
            b.Click += (_, _) => click(); buttons.Children.Add(b); return b;
        }
        buttons.Children.Add(_scale);
        buttons.Children.Add(_viewOnly);
        _scale.IsChecked = host.Vnc.Scale;
        _viewOnly.IsChecked = host.Vnc.ViewOnly;
        _scale.Click += (_, _) => ApplyScale();
        var keys = B("Keys ▾", "Keys Windows keeps for itself", () => { });
        Menus.AttachDropDown(keys, () => new object?[]
        {
            Menus.Item("Ctrl+Alt+Del", () => Combo(RfbKeys.ControlL, RfbKeys.AltL, RfbKeys.Delete)),
            Menus.Item("⌘ / Super (the Windows key)", () => Combo(RfbKeys.SuperL)),
            Menus.Item("⌘ Space (Spotlight on a Mac)", () => Combo(RfbKeys.SuperL, ' ')),
            Menus.Item("⌘ Tab (switch apps on a Mac)", () => Combo(RfbKeys.SuperL, RfbKeys.Tab)),
            Menus.Item("Alt+Tab", () => Combo(RfbKeys.AltL, RfbKeys.Tab)),
            Menus.Item("Ctrl+Alt+F1 … (Linux consoles)", () => Combo(RfbKeys.ControlL, RfbKeys.AltL, RfbKeys.F1)),
        });
        B("📋 Send clipboard", "Put this PC's clipboard text on the remote clipboard (then paste there)", SendClipboard);
        var shot = B("📷", "Screenshot of the remote desktop · right-click for more", () =>
        {
            if (Capture() is { } img) Screenshot.Default(Window.GetWindow(this), img, _host.Display + "-vnc");
        });
        Menus.Attach(shot, () => Screenshot.MenuItems(() => Task.FromResult(Capture() ?? throw new InvalidOperationException("Not connected.")), _host.Display + "-vnc", Window.GetWindow(this)));
        _reconnect = B("↻ Reconnect", "Connect again", () => _ = ConnectAsync());
        B("✕ Disconnect", "End this VNC session", () => Disconnect("Disconnected."));
        var env = EnvUi.Strip(() => _host, localWhenNoHost: false, withName: false);   // ADR-017
        DockPanel.SetDock(env, Dock.Left);
        bar.Children.Add(env);
        bar.Children.Add(buttons);
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _status.Fs(FontStep.Small);
        bar.Children.Add(_status.Mono());

        _surface.Child = _image;
        _scroll.Content = _surface;
        _overlay.SetResourceReference(Border.BackgroundProperty, "Bg");
        _overlayText.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        _overlay.Child = _overlayText;
        var body = new Grid();
        body.Children.Add(_scroll);
        body.Children.Add(_overlay);
        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(body);
        Content = root;

        _surface.PreviewKeyDown += OnKeyDown;
        _surface.PreviewKeyUp += OnKeyUp;
        _surface.PreviewTextInput += OnText;
        _surface.LostKeyboardFocus += (_, _) => ReleaseKeys();
        _image.MouseMove += (_, e) => SendPointer(e);
        // 0.6.25: the picture keeps the mouse while a button is down, so a window dragged past the edge of the view still
        // follows and the release still reaches the server (no button left stuck down on the remote desktop).
        _image.MouseDown += (_, e) => { _surface.Focus(); _buttons |= Bit(e.ChangedButton); _image.CaptureMouse(); SendPointer(e); e.Handled = true; };
        _image.MouseUp += (_, e) => { _buttons &= ~Bit(e.ChangedButton); SendPointer(e); if (_buttons == 0) _image.ReleaseMouseCapture(); e.Handled = true; };
        _image.LostMouseCapture += (_, e) => { if (_buttons != 0) { _buttons = 0; SendPointer(e); } };
        _image.MouseWheel += (_, e) =>
        {
            if (Input() is not { } c) return;
            var (x, y) = ToFramebuffer(e.GetPosition(_image));
            int wheel = e.Delta > 0 ? 8 : 16;
            for (int i = 0; i < Math.Max(1, Math.Abs(e.Delta) / 120); i++) { c.Pointer(x, y, _buttons | wheel); c.Pointer(x, y, _buttons); }
            e.Handled = true;
        };
        _paint = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => Paint(), Dispatcher) { IsEnabled = false };
        ApplyScale();
        Overlay("Not connected.");
        if (connectOnOpen) Loaded += FirstLoad;
    }

    private void FirstLoad(object sender, RoutedEventArgs e) { Loaded -= FirstLoad; _ = ConnectAsync(); }

    // ---- connecting ---------------------------------------------------------------------------------------------------

    public async Task ConnectAsync()
    {
        if (_disposed) return;
        Disconnect(null);
        _cts = new CancellationTokenSource();
        var how = _host.Vnc.Through == "ssh" ? "through SSH" : "directly";
        Overlay($"Connecting to {_host.Display} {how}…");
        _status.Text = "connecting…";
        try
        {
            var s = await VncConnector.ConnectAsync(_host, TrustCertificate, _cts.Token);
            if (_disposed) { s.Dispose(); return; }
            _session = s;
            var c = s.Client;
            c.Updated += OnUpdated;
            c.Resized += () => Dispatcher.BeginInvoke(() => { _resized = true; });
            c.Closed += why => Dispatcher.BeginInvoke(() => { if (_session == s) Disconnect(why == null ? "Disconnected." : "The session ended: " + why.Message); });
            c.Bell += () => Dispatcher.BeginInvoke(() => System.Media.SystemSounds.Beep.Play());
            c.ServerCutText += text => Dispatcher.BeginInvoke(() => ServerClipboard(text));
            CreateBitmap();
            _overlay.Visibility = Visibility.Collapsed;
            _status.Text = $"{(c.DesktopName.Length > 0 ? c.DesktopName + " · " : "")}{c.Width}×{c.Height} · RFB {c.Version} · {c.Security} · {s.Route}" + (c.Encrypted ? " · 🔒 encrypted" : " · ⚠ NOT encrypted on the network");
            _paint.Start();
            c.Start();
            _surface.Focus();
        }
        catch (OperationCanceledException) { Overlay("Cancelled."); }
        catch (Exception ex) when (ex is RfbException or System.Net.Sockets.SocketException or System.IO.IOException or InvalidOperationException or UnauthorizedAccessException or Renci.SshNet.Common.SshException)
        {
            Overlay($"Couldn't connect to {_host.Display}: {ex.Message}");
            _status.Text = "not connected";
            if (ex is not RfbException) App.Log("VNC connect", ex);
        }
    }

    /// <summary>A VeNCrypt certificate Windows doesn't trust and nothing pinned yet: the user compares and decides (the
    /// handshake thread waits for the answer).</summary>
    private bool TrustCertificate(string sha256, string subject) => Dispatcher.Invoke(() =>
        Dialogs.Confirm("Trust this VNC server's certificate?",
            $"{_host.Display} ({_host.Address}) shows a TLS certificate Windows doesn't trust:\n\n{subject}\nSHA-256 {RfbClient.TlsPinPretty(sha256)}\n\n" +
            "Compare the fingerprint with the server (e.g. openssl x509 -noout -fingerprint -sha256 -in its certificate). If you trust it, it is pinned: any other certificate is refused later.",
            "Trust and pin", danger: true));

    private void Disconnect(string? message)
    {
        _cts?.Cancel();
        _paint.Stop();
        ReleaseKeys();
        var s = _session;
        _session = null;
        if (s != null)
        {
            s.Client.Updated -= OnUpdated;
            s.Dispose();
            AuditLog.Write("you", _host.Display, "VNC session closed", s.Route);
        }
        if (message != null) { Overlay(message); _status.Text = "not connected"; }
    }

    private void Overlay(string text)
    {
        _overlayText.Text = text + "\n\n↻ Reconnect connects again. The host's VNC settings (through SSH or direct, Mac or VNC password) are in its editor.";
        _overlay.Visibility = Visibility.Visible;
    }

    // ---- drawing ------------------------------------------------------------------------------------------------------

    private void OnUpdated(int x, int y, int w, int h)
    {
        lock (_dirty)
        {
            _dx0 = Math.Min(_dx0, x); _dy0 = Math.Min(_dy0, y);
            _dx1 = Math.Max(_dx1, x + w); _dy1 = Math.Max(_dy1, y + h);
        }
    }

    private void CreateBitmap()
    {
        if (_session?.Client is not { } c) return;
        _bmp = new WriteableBitmap(c.Width, c.Height, 96, 96, PixelFormats.Bgr32, null);
        _image.Source = _bmp;
        lock (_dirty) { _dx0 = 0; _dy0 = 0; _dx1 = c.Width; _dy1 = c.Height; }
        ApplyScale();
    }

    private void Paint()
    {
        if (_session?.Client is not { } c) return;
        if (_resized || _bmp == null || _bmp.PixelWidth != c.Width || _bmp.PixelHeight != c.Height) { _resized = false; CreateBitmap(); }
        int x0, y0, x1, y1;
        lock (_dirty) { x0 = _dx0; y0 = _dy0; x1 = _dx1; y1 = _dy1; _dx0 = _dy0 = int.MaxValue; _dx1 = _dy1 = 0; }
        if (x1 <= x0 || y1 <= y0 || _bmp == null) return;
        lock (c.Sync)
        {
            x1 = Math.Min(x1, c.Width); y1 = Math.Min(y1, c.Height);
            if (x1 <= x0 || y1 <= y0 || _bmp.PixelWidth != c.Width) return;
            _bmp.WritePixels(new Int32Rect(x0, y0, x1 - x0, y1 - y0), c.Pixels, c.Width * 4, x0, y0);
        }
    }

    private void ApplyScale()
    {
        bool fit = _scale.IsChecked == true;
        _image.Stretch = fit ? Stretch.Uniform : Stretch.None;
        _scroll.HorizontalScrollBarVisibility = fit ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        _scroll.VerticalScrollBarVisibility = fit ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        RenderOptions.SetBitmapScalingMode(_image, fit ? BitmapScalingMode.HighQuality : BitmapScalingMode.NearestNeighbor);
    }

    private BitmapSource? Capture()
    {
        if (_bmp == null || _session == null) return null;
        var copy = _bmp.Clone();
        copy.Freeze();
        return copy;
    }

    // ---- input --------------------------------------------------------------------------------------------------------

    private RfbClient? Input() => _viewOnly.IsChecked == true || _disposed ? null : _session?.Client;

    private static int Bit(MouseButton b) => b switch { MouseButton.Left => 1, MouseButton.Middle => 2, MouseButton.Right => 4, _ => 0 };

    private (int X, int Y) ToFramebuffer(Point p)
    {
        if (_session?.Client is not { } c || _image.ActualWidth <= 0 || _image.ActualHeight <= 0) return (0, 0);
        // Clamped: with the mouse captured the pointer can be outside the picture.
        return (Math.Clamp((int)(p.X * c.Width / _image.ActualWidth), 0, Math.Max(0, c.Width - 1)), Math.Clamp((int)(p.Y * c.Height / _image.ActualHeight), 0, Math.Max(0, c.Height - 1)));
    }

    private void SendPointer(MouseEventArgs e)
    {
        if (Input() is not { } c) return;
        var (x, y) = ToFramebuffer(e.GetPosition(_image));
        c.Pointer(x, y, _buttons);
    }

    private static readonly Dictionary<Key, uint> Special = new()
    {
        [Key.Back] = RfbKeys.BackSpace, [Key.Tab] = RfbKeys.Tab, [Key.Enter] = RfbKeys.Return, [Key.Escape] = RfbKeys.Escape,
        [Key.Insert] = RfbKeys.Insert, [Key.Delete] = RfbKeys.Delete, [Key.Home] = RfbKeys.Home, [Key.End] = RfbKeys.End,
        [Key.PageUp] = RfbKeys.PageUp, [Key.PageDown] = RfbKeys.PageDown, [Key.Left] = RfbKeys.Left, [Key.Up] = RfbKeys.Up,
        [Key.Right] = RfbKeys.Right, [Key.Down] = RfbKeys.Down, [Key.LeftShift] = RfbKeys.ShiftL, [Key.RightShift] = RfbKeys.ShiftR,
        [Key.LeftCtrl] = RfbKeys.ControlL, [Key.RightCtrl] = RfbKeys.ControlR, [Key.LeftAlt] = RfbKeys.AltL, [Key.RightAlt] = RfbKeys.AltR,
        [Key.LWin] = RfbKeys.SuperL, [Key.RWin] = RfbKeys.SuperR, [Key.Apps] = RfbKeys.Menu, [Key.CapsLock] = RfbKeys.CapsLock,
        [Key.NumLock] = RfbKeys.NumLock, [Key.Scroll] = RfbKeys.ScrollLock, [Key.Pause] = RfbKeys.Pause, [Key.PrintScreen] = RfbKeys.Print,
    };

    private static uint? KeysymFor(Key k, ModifierKeys mods)
    {
        if (Special.TryGetValue(k, out var s)) return s;
        if (k is >= Key.F1 and <= Key.F12) return RfbKeys.F1 + (uint)(k - Key.F1);
        // With Ctrl or Alt held (and not AltGr, which arrives as Ctrl+Alt and types text) letters and digits go as keys.
        bool chord = (mods & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0 && (mods & (ModifierKeys.Control | ModifierKeys.Alt)) != (ModifierKeys.Control | ModifierKeys.Alt);
        if (chord && k is >= Key.A and <= Key.Z) return (uint)('a' + (k - Key.A));
        if (chord && k is >= Key.D0 and <= Key.D9) return (uint)('0' + (k - Key.D0));
        return null;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (Input() is not { } c) return;
        var k = e.Key == Key.System ? e.SystemKey : e.Key;
        if (KeysymFor(k, Keyboard.Modifiers) is not { } sym) return;   // text arrives as TextInput
        _down[k] = sym;
        c.Key(sym, true);
        e.Handled = true;
    }

    private void OnKeyUp(object sender, KeyEventArgs e)
    {
        if (_session?.Client is not { } c) return;
        var k = e.Key == Key.System ? e.SystemKey : e.Key;
        if (!_down.Remove(k, out var sym)) return;
        c.Key(sym, false);
        e.Handled = true;
    }

    private void OnText(object sender, TextCompositionEventArgs e)
    {
        if (Input() is not { } c || e.Text.Length == 0) return;
        // AltGr is Ctrl+Alt on Windows: lift them on the remote side so the character arrives on its own.
        var lifted = _down.Where(d => d.Value is RfbKeys.ControlL or RfbKeys.ControlR or RfbKeys.AltL or RfbKeys.AltR).Select(d => d.Value).ToList();
        bool altGr = lifted.Any(v => v is RfbKeys.AltL or RfbKeys.AltR) && lifted.Any(v => v is RfbKeys.ControlL or RfbKeys.ControlR);
        if (altGr) foreach (var m in lifted) c.Key(m, false);
        for (int i = 0; i < e.Text.Length; i++)
        {
            int cp = char.IsSurrogatePair(e.Text, i) ? char.ConvertToUtf32(e.Text, i++) : e.Text[i];
            if (cp < 0x20 && cp is not ('\r' or '\t' or '\b')) continue;
            var sym = RfbKeys.ForChar(cp);
            c.Key(sym, true); c.Key(sym, false);
        }
        if (altGr) foreach (var m in lifted) c.Key(m, true);
        e.Handled = true;
    }

    private void ReleaseKeys()
    {
        if (_session?.Client is { } c) foreach (var sym in _down.Values) c.Key(sym, false);
        _down.Clear();
        if (_buttons != 0 && _session?.Client is { } c2) { _buttons = 0; c2.Pointer(0, 0, 0); }
    }

    /// <summary>Presses the keys in order and releases them in reverse (a character is sent as its keysym).</summary>
    private void Combo(params object[] keys)
    {
        if (Input() is not { } c) { MainWindow.Instance?.Status(_session == null ? "Not connected." : "View only: keys aren't sent."); return; }
        var syms = keys.Select(k => k is char ch ? RfbKeys.ForChar(ch) : (uint)k).ToList();
        foreach (var s in syms) c.Key(s, true);
        for (int i = syms.Count - 1; i >= 0; i--) c.Key(syms[i], false);
        _surface.Focus();
    }

    private void SendClipboard()
    {
        if (Input() is not { } c) { MainWindow.Instance?.Status("Not connected (or view only)."); return; }
        string text;
        try { text = Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
        catch (System.Runtime.InteropServices.COMException) { text = ""; }
        if (text.Length == 0) { MainWindow.Instance?.Status("The clipboard has no text."); return; }
        c.CutText(text);
        MainWindow.Instance?.Status($"Clipboard sent to {_host.Display} ({text.Length:N0} characters) — paste it there.");
        _surface.Focus();
    }

    private void ServerClipboard(string text)
    {
        if (!_host.Vnc.ClipboardFromServer) { MainWindow.Instance?.Status($"{_host.Display} copied text (taking the remote clipboard is off in the host's VNC settings)."); return; }
        try { Clipboard.SetText(text); MainWindow.Instance?.Status($"Copied from {_host.Display} ({text.Length:N0} characters)."); }
        catch (System.Runtime.InteropServices.COMException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        Disconnect(null);
        _disposed = true;
    }
}
