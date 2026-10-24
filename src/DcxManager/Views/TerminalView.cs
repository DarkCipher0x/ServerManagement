using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DcxManager.Services;
using DcxManager.Ssh;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using DcxManager.Sessions;

namespace DcxManager.Views;

/// <summary>
/// An interactive terminal: xterm.js (full VT/xterm-256color, mouse, alt-screen apps like htop/vim/tmux) in WebView2,
/// bridged to a session transport (ADR-004): SSH (SSH.NET shell channel), a local shell or WSL (ConPTY). Ctrl+Shift+C /
/// Ctrl+Shift+V or right-click to copy/paste. What the session supports (<see cref="SessionCaps"/>) decides which
/// buttons and menu items appear.
///
/// Stabilisation (Phase 1): output is drained through a bounded chunk queue so enormous command output can never
/// exhaust memory or block the UI; an unexpected drop (network loss, server restart) triggers auto-reconnect with
/// exponential back-off where the session type allows it; teardown is deterministic (timer, handlers, transport,
/// reconnect task all released); and every backend callback is isolated so one session failing never affects the app
/// or other tabs.
/// </summary>
public sealed partial class TerminalView : UserControl, IDisposable, IActivatable, IHostView, ITerminal
{
    // Output pipeline bounds. SSH data arrives in small chunks (≤ max packet), so a flush is a handful of chunks.
    private const int MaxFlushChars = 200_000;     // cap per message posted to the page (bounds JSON cost)
    private const int MaxPendingChars = 6_000_000; // hard cap on undrained backlog (scrollback is capped anyway)

    private readonly SessionTarget _target;
    private readonly string? _initialCommand;
    private readonly bool _connectOnOpen;
    private WebView2 _web = new();   // replaced after a renderer failure (ADR-021)
    private readonly TextBlock _status = new();
    private readonly Queue<string> _chunks = new();
    private int _pendingLen;
    private bool _dropped;
    private Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly object _decodeGate = new();
    private readonly DispatcherTimer _flush;

    // Terminal features (ADR-019): profile (per host, switchable per session), find bar, session log.
    private TerminalProfile _profile;
    private bool _sessionProfile;
    private SessionLogger? _log;
    private bool _logOn;
    private readonly Border _findBar = new() { Visibility = Visibility.Collapsed, Padding = new Thickness(8, 4, 8, 4), BorderThickness = new Thickness(0, 1, 0, 0) };
    private readonly TextBox _findBox = Hint.Set(new TextBox { Width = 260, Padding = new Thickness(5, 2, 5, 2), ToolTip = "Search the scrollback · Enter = older match, Shift+Enter = newer, Esc closes" }, "Find in scrollback…");
    private readonly CheckBox _findCase = new() { Content = "Aa", ToolTip = "Match case", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    private readonly CheckBox _findRegex = new() { Content = ".*", ToolTip = "Regular expression", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
    private readonly TextBlock _findCount = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };

    private ITerminalTransport? _transport;
    private CancellationTokenSource? _reconnectCts;
    private int _attempt;
    private uint _cols = 120, _rows = 32;
    private bool _pageReady, _sized, _connecting, _disposed, _userClosed;
    private string _diag = "";
    private string _selection = "";
    private int _fontSize = AppSettings.Current.TerminalFontSize;

    /// <summary>The SSH host, or null for a local shell / WSL tab.</summary>
    public HostEntry? Host => _target.Host;
    public SessionTarget Target => _target;
    public bool PageReady => _pageReady;
    public bool IsConnected => _transport?.IsOpen == true;

    // ---- AI terminal targeting (ADR-028) ------------------------------------------------------------------------------
    /// <summary>A short id for this tab's session, shown wherever the AI Engineer targets it.</summary>
    public string SessionId { get; } = Guid.NewGuid().ToString("N")[..8];
    /// <summary>Counts successful (re)connects: a target chosen before a reconnect is stale and refused.</summary>
    public int Epoch { get; private set; }
    /// <summary>Decoded output as it arrives (any thread). Only the terminal bridge listens, and only while a command it
    /// sent is running.</summary>
    internal event Action<string>? OutputText;
    /// <summary>The shell the bridge can talk to: "posix" (SSH to Linux/Unix, WSL), "powershell" (local Windows
    /// PowerShell / PowerShell 7), or "" when commands can't be wrapped reliably (cmd, Windows/network SSH hosts, serial, Telnet).</summary>
    public string Shell => _target switch
    {
        SshTarget s => s.Host.EffectivePlatform is "windows" or "network" ? "" : "posix",
        LocalTarget l when l.Profile.Wsl => "posix",
        LocalTarget l when l.Profile.Id is "powershell" or "pwsh" => "powershell",
        _ => "",
    };
    /// <summary>Text for the shell, exactly as typed input (the bridge's only way in).</summary>
    internal void BridgeWrite(string s) => SendInput(s);

    // ITerminal (0.2.0 phase 6): the terminal bridge below the screens reaches the tab only through these.
    string ITerminal.Environment => EnvUi.ForView(this);
    event Action<string>? ITerminal.OutputText { add => OutputText += value; remove => OutputText -= value; }
    void ITerminal.BridgeWrite(string text) => BridgeWrite(text);
    string ITerminal.RecentOutput(int maxChars) => RecentOutput(maxChars);
    bool ITerminal.IsDisposed => IsDisposed;
    async Task<IReadOnlyList<string>?> ITerminal.ScreenLinesAsync() => (await CaptureScreenAsync())?.Lines;

    public TerminalView(HostEntry host, string? initialCommand = null, bool connectOnOpen = true)
        : this(new SshTarget(host), initialCommand, connectOnOpen) { }

    public TerminalView(SessionTarget target, string? initialCommand = null, bool connectOnOpen = true)
    {
        _target = target;
        _initialCommand = initialCommand;
        _connectOnOpen = connectOnOpen;
        _profile = TerminalProfiles.For(_target.Host);
        _logOn = SessionLog.Wanted(_profile.Log);   // 0.4.0: Settings / policy can force logging on or off for every session

        var bar = new DockPanel { Margin = new Thickness(8, 5, 8, 5) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(buttons, Dock.Right);
        Button B(string text, string tip, RoutedEventHandler click)
        {
            var b = new Button { Content = text, ToolTip = tip, Style = (Style)Application.Current.Resources["SmallButton"] };
            b.Click += click; buttons.Children.Add(b); return b;
        }
        B("↻ Reconnect", _target.Host != null ? "Reconnect this session" : "Start a new shell in this tab", async (_, _) => await ConnectAsync());
        if (_target.Has(SessionCaps.HostTools) && _target.Host is { } h)
        {
            B("⇅ Files", "Browse files on this server (SFTP)", (_, _) => MainWindow.Instance.OpenFiles(h));
            B("▣ Manage", "Services, processes, logs, packages, Docker…", (_, _) => MainWindow.Instance.OpenServer(h));
        }
        // 0.6.6 (ADR-085): into tmux on this host (installed when missing, after asking; your config from Settings → Terminal).
        if (TmuxApplies) B("⧉ tmux", "Start or reattach your tmux session on this host (installs tmux if it's missing, asking first; your config: Settings → Terminal → tmux)", async (_, _) => await StartTmuxAsync());
        if (_target.CanDuplicate) B("⊞ Duplicate", "Open another session of the same kind", (_, _) => MainWindow.Instance.OpenSession(_target.Clone(), forceNew: true));
        if (_target is SerialTarget) B("⎍ Break", "Send a serial BREAK (ROMMON / password recovery on network gear)", (_, _) => SendBreak());
        if (Assist.Current != null) B("✦ Ask AI", "Send the selected text to the AI operator for an explanation", (_, _) => AskAi());
        B("A−", "Smaller text", (_, _) => SetFont(_fontSize - 1));
        B("A+", "Larger text", (_, _) => SetFont(_fontSize + 1));
        B("⌕", "Search the scrollback (Ctrl+Shift+F)", (_, _) => ShowFind(_findBar.Visibility != Visibility.Visible));
        B("⌫ Clear", "Clear the scrollback", (_, _) => Post(new { t = "clear" }));
        // Camera: click = what Settings → Terminal says (save / clipboard / both); right-click = all the options.
        var shot = B("📷", "Screenshot this terminal (Ctrl+Shift+S) · right-click for save as / copy / share", async (_, _) =>
        {
            try { Screenshot.Default(Window.GetWindow(this), await CaptureAsync(), _target.Title); }
            catch (Exception ex) { Dialogs.Error("Screenshot", ex.Message); }
        });
        Menus.Attach(shot, () => Screenshot.MenuItems(CaptureAsync, _target.Title, Window.GetWindow(this)));
        B("✕ Disconnect", _target.Host != null ? "Close the SSH connection (stops auto-reconnect)" : "End the shell (closes the program running in it)", (_, _) => UserDisconnect());
        _status.Mono();
        _status.Fs(FontStep.Small);
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        // Environment label (ADR-017): the host's, LOCAL for local shells / WSL, none for a serial device. Added first so a
        // narrow pane squeezes the buttons, never the label; the host's name is already on the tab.
        var env = EnvUi.Strip(() => _target.Host, localWhenNoHost: _target.Kind is "local" or "wsl", withName: false);
        DockPanel.SetDock(env, Dock.Left);
        bar.Children.Add(env);
        bar.Children.Add(buttons);
        bar.Children.Add(_status);

        var border = _webBorder;
        border.Child = _web;
        border.SetResourceReference(Border.BorderBrushProperty, "Line");
        border.SetResourceReference(Border.BackgroundProperty, "Input");
        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        BuildFindBar();
        DockPanel.SetDock(_findBar, Dock.Bottom);
        root.Children.Add(_findBar);
        root.Children.Add(border);
        Content = root;

        _flush = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Background, (_, _) => Flush(), Dispatcher);
        Loaded += OnLoaded;
        ThemeManager.Changed += OnTheme;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        if (_disposed) return;
        SetStatus($"{_target.Endpoint} · starting terminal…");
        Typography.TerminalChanged += OnFonts;
        await InitWebAsync();
    }

    /// <summary>Sets up the current WebView2 control and loads the terminal page (at start, and again after a renderer
    /// failure on a fresh control — ADR-021).</summary>
    private async Task InitWebAsync()
    {
        var web = _web;
        try
        {
            await WebHost.InitAsync(web, "Input");
            if (_disposed || web != _web) return;
            var core = web.CoreWebView2;
            WebHost.MapTerminalHost(core);
            core.WebMessageReceived += OnMessage;
            core.ContextMenuRequested += OnContextMenu;
            // Remote output is untrusted: the terminal page can never navigate away or open windows (OSC 8 links go
            // through OpenLink, which shows the full URL and asks first), and page script dialogs are suppressed.
            core.NewWindowRequested += (_, a) => a.Handled = true;
            core.NavigationStarting += (_, a) => { if (!a.Uri.StartsWith("https://dcx.term/", StringComparison.OrdinalIgnoreCase)) a.Cancel = true; };
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.NavigationCompleted += (_, a) => { if (!a.IsSuccess) { _diag = "nav failed: " + a.WebErrorStatus; App.Log("Terminal nav", new Exception(_diag)); } };
            core.ProcessFailed += (_, a) =>
            {
                _diag = "renderer failed: " + a.ProcessFailedKind;
                App.Log("Terminal WebView2 process", new Exception(_diag));
                var env = core.Environment;
                Dispatcher.BeginInvoke(() => OnProcessFailed(a.ProcessFailedKind, env, web));
            };
            string C(string k) => ThemeManager.HexString(k);
            core.Navigate($"https://dcx.term/terminal.html?bg={C("Input")}&fg={C("Fg")}&accent={C("Accent")}&sel={C("Selection")}&thumb={C("ScrollThumb")}&size={_fontSize}&font={Uri.EscapeDataString(Typography.TerminalFamily)}&scrollback={AppSettings.Current.Scrollback}&tid={_tid}");
        }
        catch (Exception ex)
        {
            SetStatus("WebView2 runtime missing: " + ex.Message);
            App.Log("Terminal WebView2 init", ex);
        }
    }

}
