using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using System.Windows.Threading;
using DcxManager.Rdp;
using DcxManager.Services;
using DcxManager.Ssh;

namespace DcxManager.Views;

/// <summary>
/// Remote Desktop inside a tab (ADR-013): Windows' own RDP ActiveX in a WindowsFormsHost. Resize-to-window / scale /
/// fixed display, the control's own full screen, Ctrl+Alt+Del, reconnect, screenshot; pop-out and dock-back keep the
/// session. The password is handed to the control in memory only. Device redirection follows the host's settings with
/// conservative defaults (no drives, printers, smart cards, ports, POS, microphone, camera).
/// </summary>
public sealed class RdpView : UserControl, IHostView, IDisposable
{
    private readonly HostEntry _host;
    private readonly RdpAxHost _ax;
    private readonly WindowsFormsHost _wfh;
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly ComboBox _fit = new() { Width = 170, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly DispatcherTimer _resize;
    private readonly bool _connectOnOpen;
    private bool _created, _connected, _loggedIn, _fullScreen, _disposed, _userDisconnect;
    private string? _password;

    public HostEntry? Host => _host;
    internal string State { get; private set; } = "created";
    internal List<RdpEvent> Events { get; } = new();
    internal bool ControlCreated => _created && _ax.Created;
    internal string ClsidUsed => _ax.ClsidUsed;
    /// <summary>Signed in to the remote desktop (the dashboard counts these as connected).</summary>
    public bool IsConnected => _loggedIn && !_disposed;

    public RdpView(HostEntry host, bool connectOnOpen = true)
    {
        _host = host;
        _connectOnOpen = connectOnOpen;
        _ax = new RdpAxHost(RdpAxHost.AvailableClsid() ?? throw new InvalidOperationException("The Windows Remote Desktop control (mstscax.dll) isn't available on this PC."));
        _ax.Dock = System.Windows.Forms.DockStyle.Fill;
        _ax.Event += OnRdpEvent;
        _wfh = new WindowsFormsHost { Child = _ax };

        var bar = new DockPanel { Margin = new Thickness(8, 5, 8, 5) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(buttons, Dock.Right);
        Button B(string text, string tip, Action click)
        {
            var b = new Button { Content = text, ToolTip = tip, Style = (Style)Application.Current.Resources["SmallButton"] };
            b.Click += (_, _) => click(); buttons.Children.Add(b); return b;
        }
        _fit.Items.Add(new ComboBoxItem { Content = "Resize to window", Tag = "resize", ToolTip = "The remote desktop resolution follows this pane (Windows 8.1 / Server 2012 R2 and later)" });
        _fit.Items.Add(new ComboBoxItem { Content = "Scale to window", Tag = "scale", ToolTip = "Keep the remote resolution and scale the picture" });
        _fit.Items.Add(new ComboBoxItem { Content = $"Fixed {host.Rdp.Width}×{host.Rdp.Height}", Tag = "fixed", ToolTip = "The host's resolution, unscaled (takes effect on the next connect)" });
        _fit.SelectedItem = _fit.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == host.Rdp.Fit) ?? _fit.Items[0];
        _fit.SelectionChanged += (_, _) => FitChanged();
        buttons.Children.Add(_fit);
        B("↻ Reconnect", "Connect again", () => Connect());
        B("⛶ Full screen", "The session full screen (use the connection bar at the top to come back)", () => SetFullScreen(true));
        B("Ctrl+Alt+Del", "Send Ctrl+Alt+Del to the remote computer (or press Ctrl+Alt+End)", SendCad);
        var shot = B("📷", "Screenshot of the remote desktop · right-click for more", () =>
        {
            try { Screenshot.Default(Window.GetWindow(this), Capture(), _host.Display + "-rdp"); }
            catch (Exception ex) { Dialogs.Error("Screenshot", ex.Message); }
        });
        Menus.Attach(shot, () => Screenshot.MenuItems(() => Task.FromResult(Capture()), _host.Display + "-rdp", Window.GetWindow(this)));
        B("Open in mstsc", "Open this host in Windows' Remote Desktop Connection instead", () => RdpLauncher.Launch(_host));
        B("✕ Disconnect", "End this Remote Desktop connection (the remote session keeps running unless you sign out)", Disconnect);
        var env = EnvUi.Strip(() => _host, localWhenNoHost: false, withName: false);   // ADR-017: first, so it is never squeezed
        DockPanel.SetDock(env, Dock.Left);
        bar.Children.Add(env);
        bar.Children.Add(buttons);
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _status.Fs(FontStep.Small);
        bar.Children.Add(_status.Mono());

        // 0.5.0 (DarkCipher0x): the Remote Desktop control paints white when it has no session, and nothing WPF can draw over it
        // (airspace). So whenever there is no live session the control is collapsed and a panel in the theme's colours shows
        // what happened and what to do; the control's own background follows the theme for the frames before and after.
        var stage = new Grid();
        stage.SetResourceReference(Panel.BackgroundProperty, "Bg");
        stage.Children.Add(_wfh);
        stage.Children.Add(BuildIdlePanel());
        var border = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Child = stage };
        border.SetResourceReference(Border.BorderBrushProperty, "Line");
        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(border);
        Content = root;

        _resize = new DispatcherTimer(TimeSpan.FromMilliseconds(700), DispatcherPriority.Background, (_, _) => { _resize!.Stop(); ApplyResize(); }, Dispatcher) { IsEnabled = false };
        _wfh.SizeChanged += (_, _) => { if (_loggedIn) { _resize.Stop(); _resize.Start(); } };
        Loaded += OnLoaded;
        SetStatus($"{Endpoint} · starting the Remote Desktop control…");
    }

    private readonly TextBlock _idleTitle = new() { FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center };
    private readonly TextBlock _idleText = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 6, 0, 14) };
    private StackPanel? _idle;

    private StackPanel BuildIdlePanel()
    {
        _idleTitle.Fs(FontStep.Heading); _idleTitle.SetResourceReference(TextBlock.ForegroundProperty, "FgStrong");
        _idleText.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        var again = new Button { Content = "↻ Reconnect", Style = (Style)Application.Current.Resources["PrimaryButton"], Margin = new Thickness(0, 0, 8, 0) };
        again.Click += (_, _) => Connect();
        var mstsc = new Button { Content = "Open in mstsc" };
        mstsc.Click += (_, _) => RdpLauncher.Launch(_host);
        buttons.Children.Add(again); buttons.Children.Add(mstsc);
        _idle = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Visibility = Visibility.Collapsed, Margin = new Thickness(24) };
        _idle.Children.Add(new TextBlock { Text = "🖥", HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0.6 }.Fs(FontStep.Heading));
        _idle.Children.Add(_idleTitle); _idle.Children.Add(_idleText); _idle.Children.Add(buttons);
        return _idle;
    }

    /// <summary>No session: the white control is collapsed and the themed panel says why. Public state for the self-test.</summary>
    internal bool IdleShown => _idle?.Visibility == Visibility.Visible;

    private void ShowIdle(string title, string text)
    {
        if (_disposed || _idle == null) return;
        _idleTitle.Text = $"{title} · {_host.Display}"; _idleText.Text = text;
        _wfh.Visibility = Visibility.Collapsed;
        _idle.Visibility = Visibility.Visible;
    }

    private void ShowSession()
    {
        if (_disposed || _idle == null) return;
        _idle.Visibility = Visibility.Collapsed;
        _wfh.Visibility = Visibility.Visible;
    }

    /// <summary>The control's own background (seen while connecting and tearing down) in the theme's page colour.</summary>
    private void ThemeBackColor()
    {
        if (Application.Current.TryFindResource("Bg") is System.Windows.Media.SolidColorBrush b)
            try { _ax.BackColor = System.Drawing.Color.FromArgb(b.Color.R, b.Color.G, b.Color.B); } catch (Exception ex) { App.Log("RDP colour", ex); }
    }

    private string Endpoint => _host.Port == 3389 ? _host.Address : $"{_host.Address}:{_host.Port}";

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                ThemeBackColor();
            _ax.CreateControl();
                _created = true;
                State = "ready";
                SetStatus($"{Endpoint} · ready (Windows RDP control {Version()})");
                if (_connectOnOpen) Connect();
            }
            catch (Exception ex)
            {
                State = "failed";
                App.Log("RDP control", ex);
                SetStatus("✕ The Remote Desktop control could not start: " + ex.Message + " — use “Open in mstsc”.");
            }
        }, DispatcherPriority.Loaded);
    }

    private string Version() { try { return (string)_ax.Ocx.Version; } catch { return "?"; } }

    /// <summary>One line in the bar (Windows' disconnect descriptions are whole paragraphs); the full text is the tooltip.</summary>
    private void SetStatus(string s)
    {
        if (_disposed) return;
        var oneLine = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
        _status.Text = oneLine.Length > 160 ? oneLine[..160] + "…" : oneLine;
        _status.ToolTip = s.Trim();
    }

    /// <summary>Applies every setting and connects. Asks for a password (not saved) when the vault has none.</summary>
    public void Connect()
    {
        if (!_created || _disposed) return;
        // Offline mode (ADR-016): the server is checked before the control connects.
        if (Offline.On) { Offline.Run(_host.Address, "Remote Desktop", ConnectNow); return; }
        ConnectNow();
    }

    private void ConnectNow()
    {
        if (!_created || _disposed) return;
        try
        {
            if (_connected) { try { _ax.Ocx.Disconnect(); } catch { } }
            _userDisconnect = false;
            ShowSession();
            var r = _host.Rdp;
            if (r.Gateway.Trim().Length > 0)
            {
                SetStatus("This host uses an RD Gateway, which the embedded client doesn't handle yet — opening it in mstsc.");
                RdpLauncher.Launch(_host);
                return;
            }
            // 0.5.0 (ADR-062): single sign-on with the Windows account — no password from the vault or typed.
            var signOn = AppSettings.Current.SsoRdpAllowed ? r.SignOn : "password";
            bool sso = signOn is "windows" or "rcg" or "restricted";
            var cred = sso ? null : SshConnector.CredentialFor(_host);
            _password = cred is { Kind: CredentialKind.Password, Secret.Length: > 0 } ? cred.Secret : _password;
            if (_password == null && !sso)
            {
                _password = Dialogs.Prompt("Remote Desktop", $"Password for {(r.Domain.Length > 0 ? r.Domain + "\\" : "")}{_host.Username} on {_host.Display} (used for this connection only, not saved):", password: true);
                if (_password == null) { SetStatus($"○ {Endpoint} · not connected (no password)"); return; }
            }
            dynamic o = _ax.Ocx;
            var fit = Fit;
            var (w, h) = fit == "fixed" ? (Math.Clamp(r.Width, 640, 8192), Math.Clamp(r.Height, 480, 8192)) : PanePixels();
            Set(() => o.Server = _host.Address, "Server");
            Set(() => o.UserName = _host.Username, "UserName");
            Set(() => o.Domain = r.Domain.Trim(), "Domain");
            Set(() => o.DesktopWidth = w, "DesktopWidth");
            Set(() => o.DesktopHeight = h, "DesktopHeight");
            Set(() => o.ColorDepth = 32, "ColorDepth");
            dynamic a = o.AdvancedSettings2;
            Set(() => a.RDPPort = _host.Port, "RDPPort");
            Set(() => a.EnableCredSspSupport = true, "EnableCredSspSupport");
            Set(() => o.AdvancedSettings5.AuthenticationLevel = 2, "AuthenticationLevel");
            Set(() => a.RedirectClipboard = r.Clipboard, "RedirectClipboard");
            Set(() => a.RedirectDrives = r.Drives, "RedirectDrives");
            Set(() => a.RedirectPrinters = r.Printers, "RedirectPrinters");
            Set(() => a.RedirectSmartCards = r.SmartCards, "RedirectSmartCards");
            Set(() => a.RedirectPorts = false, "RedirectPorts");
            Set(() => o.AdvancedSettings7.AudioCaptureRedirectionMode = r.Microphone, "AudioCaptureRedirectionMode");
            Set(() => o.SecuredSettings2.AudioRedirectionMode = Math.Clamp(r.Audio, 0, 2), "AudioRedirectionMode");
            Set(() => o.SecuredSettings3.KeyboardHookMode = Math.Clamp(r.KeyboardHook, 0, 2), "KeyboardHookMode");
            Set(() => a.EnableAutoReconnect = r.AutoReconnect, "EnableAutoReconnect");
            Set(() => a.MaxReconnectAttempts = Math.Clamp(r.ReconnectRetries, 0, 200), "MaxReconnectAttempts");
            Set(() => a.SmartSizing = fit == "scale", "SmartSizing");
            Set(() => a.ConnectToAdministerServer = r.Admin, "ConnectToAdministerServer");
            Set(() => a.BitmapPersistence = 0, "BitmapPersistence");   // no screen-content cache on disk
            Set(() => a.ContainerHandledFullScreen = 0, "ContainerHandledFullScreen");
            Set(() => a.DisplayConnectionBar = true, "DisplayConnectionBar");
            Set(() => a.GrabFocusOnConnect = true, "GrabFocusOnConnect");
            if (sso)
            {
                // Remote Credential Guard sends Kerberos requests back to this PC; Restricted Admin logs on without your
                // credentials ever reaching the server. Plain single sign-on lets CredSSP use the Windows credentials (the
                // "Allow delegating default credentials" policy for TERMSRV/<server>; otherwise Windows asks once).
                try
                {
                    if (signOn == "rcg") _ax.SetExtended("RedirectedAuthentication", true);
                    if (signOn == "restricted") _ax.SetExtended("RestrictedLogon", true);
                }
                catch (Exception ex) when (ex is InvalidCastException or System.Runtime.InteropServices.COMException)
                {
                    SetStatus("✕ This Windows' Remote Desktop control can't do " + (signOn == "rcg" ? "Remote Credential Guard" : "Restricted Admin") + " — use mstsc, or single sign-on.");
                    App.Log("RDP sign-on mode", ex);
                    return;
                }
                _password = null;
            }
            else
            {
                _ax.SetPassword(_password!);
                if (cred == null) _password = null;   // a typed password is used once; reconnect asks again
            }
            State = "connecting";
            SetStatus($"{Endpoint} · connecting as {_host.Username}…");
            o.Connect();
        }
        catch (Exception ex)
        {
            State = "failed";
            App.Log("RDP connect", ex);
            SetStatus("✕ " + ex.Message);
        }
    }

    private static void Set(Action set, string what)
    {
        try { set(); } catch (Exception ex) { App.Log("RDP setting " + what, ex); }
    }

    private string Fit => (_fit.SelectedItem as ComboBoxItem)?.Tag as string ?? "resize";

    /// <summary>The pane's size in device pixels (even numbers, within the control's limits).</summary>
    private (int W, int H) PanePixels()
    {
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        int w = (int)Math.Round(Math.Max(200, _wfh.ActualWidth) * dpi.DpiScaleX), h = (int)Math.Round(Math.Max(200, _wfh.ActualHeight) * dpi.DpiScaleY);
        return (Math.Clamp(w & ~1, 640, 8192), Math.Clamp(h & ~1, 480, 8192));
    }

    private void FitChanged()
    {
        _host.Rdp.Fit = Fit;
        if (!_host.IsAdHoc) { try { AppData.Save(); } catch { } }
        if (!_loggedIn) return;
        try
        {
            dynamic o = _ax.Ocx;
            o.AdvancedSettings2.SmartSizing = Fit == "scale";
            if (Fit == "resize") ApplyResize();
            if (Fit == "fixed") SetStatus("● Fixed resolution applies on the next connect (↻ Reconnect).");
        }
        catch (Exception ex) { App.Log("RDP fit", ex); }
    }

    private void ApplyResize()
    {
        if (!_loggedIn || Fit != "resize" || _fullScreen) return;
        var (w, h) = PanePixels();
        try { _ax.Ocx.UpdateSessionDisplaySettings((uint)w, (uint)h, (uint)w, (uint)h, 0u, 100u, 100u); }
        catch (Exception ex) { App.Log("RDP resize", ex); SetStatus("● Connected — this server doesn't support live resizing; use “Scale to window”."); }
    }

    private void SetFullScreen(bool on)
    {
        if (!_loggedIn) { SetStatus("Connect first."); return; }
        try { _ax.Ocx.FullScreen = on; } catch (Exception ex) { SetStatus("✕ Full screen: " + ex.Message); }
    }

    private void SendCad()
    {
        if (!_loggedIn) { SetStatus("Connect first."); return; }
        try { _ax.Focus(); _ax.SendCtrlAltDel(); SetStatus("● Ctrl+Alt+Del sent"); }
        catch (Exception ex) { App.Log("RDP Ctrl+Alt+Del", ex); SetStatus("Couldn't send Ctrl+Alt+Del from here — click into the session and press Ctrl+Alt+End."); }
    }

    public void Disconnect()
    {
        _userDisconnect = true;
        try { if (_connected) _ax.Ocx.Disconnect(); } catch (Exception ex) { App.Log("RDP disconnect", ex); }
    }

    private System.Windows.Media.Imaging.BitmapSource Capture()
    {
        if (!_created || _ax.Handle == IntPtr.Zero) throw new InvalidOperationException("The Remote Desktop view isn't showing.");
        return Screenshot.CaptureWindow(_ax.Handle, clientOnly: true);
    }

    private void OnRdpEvent(RdpEvent e, int[] args)
    {
        if (_disposed) return;
        Events.Add(e);
        switch (e)
        {
            case RdpEvent.Connecting: State = "connecting"; SetStatus($"{Endpoint} · connecting…"); break;
            case RdpEvent.Connected: State = "connected"; _connected = true; SetStatus($"{Endpoint} · connected, signing in…"); break;
            case RdpEvent.LoginComplete:
                State = "signed in"; _loggedIn = true;
                SetStatus($"● {Endpoint} · signed in as {_host.Username}");
                _host.LastConnected = DateTime.Now;
                if (!_host.IsAdHoc) { try { AppData.Save(); } catch { } }
                AuditLog.Write("you", _host.Display, "opened Remote Desktop (embedded)", Endpoint);
                Incidents.IncidentStore.RecordConnection(_host, "Remote Desktop");
                if (Fit == "resize") { _resize.Stop(); _resize.Start(); }
                LiveSessions.Touch();
                break;
            case RdpEvent.Disconnected:
            {
                _connected = _loggedIn = false;
                State = "disconnected";
                LiveSessions.Touch();
                string why;
                try { dynamic o = _ax.Ocx; why = (string)o.GetErrorDescription((uint)args[0], (uint)o.ExtendedDisconnectReason); }
                catch { why = $"reason {args[0]}"; }
                SetStatus(_userDisconnect ? $"○ {Endpoint} · disconnected" : $"○ {Endpoint} · disconnected: {why} (code {args[0]}) — ↻ Reconnect");
                ShowIdle(_userDisconnect ? "Disconnected" : "The connection ended", _userDisconnect ? "You ended this Remote Desktop connection. The remote session keeps running unless you signed out." : $"{why} (code {args[0]})");
                break;
            }
            case RdpEvent.EnterFullScreen: _fullScreen = true; break;
            case RdpEvent.LeaveFullScreen: _fullScreen = false; if (Fit == "resize") { _resize.Stop(); _resize.Start(); } break;
            case RdpEvent.SizeChanged: SetStatus($"● {Endpoint} · signed in · {args[0]}×{args[1]}"); break;
            case RdpEvent.MinimizeRequested: if (Window.GetWindow(this) is { } win) win.WindowState = WindowState.Minimized; break;
            case RdpEvent.FatalError: State = "failed"; SetStatus($"✕ {Endpoint} · the Remote Desktop control reported a fatal error ({args[0]})"); ShowIdle("The Remote Desktop control failed", $"Fatal error {args[0]}. Reconnect, or open the host in mstsc."); App.Log("RDP fatal", new Exception("code " + args[0])); break;
            case RdpEvent.LogonError: if (args[0] < 0 || args[0] > 0xFFFF) SetStatus($"{Endpoint} · sign-in problem (code {args[0]})"); break;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _resize.Stop();
        try { if (_connected) _ax.Ocx.Disconnect(); } catch { }
        _disposed = true;
        _password = null;
        try { _ax.Event -= OnRdpEvent; _wfh.Child = null; _ax.Dispose(); _wfh.Dispose(); } catch (Exception ex) { App.Log("RDP dispose", ex); }
    }
}
