using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DcxManager.Browser;
using DcxManager.Infra;
using DcxManager.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DcxManager.Views;

/// <summary>
/// One portal browser tab (ADR-032): a WebView2 in the profile of the bookmark's environment (or its named profile), with
/// a clear address bar and security chip, TOFU pinning for self-signed certificates, downloads that ask first, pop-ups as
/// tabs in the same profile, no password saving or autofill, and the offline-mode guard on every request.
/// </summary>
public sealed class BrowserView : UserControl, IDisposable, IHostView
{
    /// <summary>Every open portal tab (forgetting a certificate clears their accepted-certificate cache; self-test).</summary>
    internal static readonly List<BrowserView> Live = new();
    /// <summary>host:port → SHA-256 of a pinned certificate WebView2 was told to accept in this browser session.</summary>
    private static readonly Dictionary<string, string> AcceptedThisSession = new(StringComparer.OrdinalIgnoreCase);

    private readonly WebView2 _web = new();
    private readonly TextBox _addr = new() { VerticalContentAlignment = VerticalAlignment.Center };
    private readonly Button _chip = new() { MinWidth = 0, Margin = new Thickness(6, 0, 4, 0) };
    private readonly Button _back, _fwd, _reload, _fill;
    private readonly TextBlock _profilePill = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) };
    private readonly Border _banner = new() { Padding = new Thickness(10, 6, 10, 6), BorderThickness = new Thickness(0, 0, 0, 1), Visibility = Visibility.Collapsed };
    private readonly TextBlock _bannerText = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly WrapPanel _bannerButtons = new() { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string? _initialUrl;
    private readonly List<DateTime> _popups = new();
    private string _shown = "";
    /// <summary>The last top-level address asked for: shown while the page itself is still blank (a refused first page).</summary>
    private string? _attempted;

    /// <summary>The page's address, or the one being attempted while the tab has no page yet.</summary>
    // CoreWebView2.Source is current when NavigationCompleted fires; the WPF control's Source property follows it a moment
    // later, so reading that one there showed the previous page's address (0.2.0 phase 0 baseline run).
    private string CurrentUrl => (_web.CoreWebView2?.Source ?? _web.Source?.ToString()) is { } src && src.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? src : _attempted ?? _web.Source?.ToString() ?? "";
    private bool _disposed, _loading;

    public PortalBookmark Bookmark { get; }
    /// <summary>The WebView2 profile (cookie jar) this tab runs in.</summary>
    public string ProfileName { get; }
    public string EnvironmentName => Portals.EnvironmentOf(Bookmark);
    public HostEntry? Host => AppData.Current.FindHost(Bookmark.HostId);
    /// <summary>The tab this view sits in (set by whoever adds it).</summary>
    public TabItem? Tab { get; set; }
    public WebSecurity Security { get; private set; }
    /// <summary>The untrusted certificate last refused for the page's host (what "Trust this certificate" would pin).</summary>
    public (string HostPort, string Sha256, string Subject, TlsDecision Decision, string Status)? RefusedCert { get; private set; }
    public CoreWebView2? Core => _web.CoreWebView2;
    /// <summary>Completes when the browser is initialised (or failed; see <see cref="InitError"/>).</summary>
    public Task Ready => _ready.Task;
    public string? InitError { get; private set; }
    public string AddressText => _addr.Text;
    public string BannerText => _banner.Visibility == Visibility.Visible ? _bannerText.Text : "";
    /// <summary>Raised on the UI thread after each top-level navigation finishes (success or not).</summary>
    public event Action<bool>? NavigationDone;
    internal string? LastDownloadPath { get; private set; }
    internal int DownloadsRefused { get; private set; }
    internal int Blocked { get; private set; }
    /// <summary>A pop-up tab waits for its opener to hand over the new window instead of navigating itself.</summary>
    internal bool IsPopup { get; }

    public BrowserView(PortalBookmark bookmark, string? url, string? profile = null, bool popup = false)
    {
        Bookmark = bookmark;
        ProfileName = profile ?? Portals.ProfileFor(bookmark);
        _initialUrl = url;
        IsPopup = popup;
        SetResourceReference(BackgroundProperty, "Bg");

        Button B(string text, string tip, Action click)
        {
            var b = new Button { Content = text, MinWidth = 0, Width = 32, ToolTip = tip, Style = (Style)FindResource("SmallButton"), Margin = new Thickness(0, 0, 4, 0) };
            b.Click += (_, _) => click();
            return b;
        }
        _back = B("←", "Back (Alt+←)", () => { if (_web.CanGoBack) _web.GoBack(); });
        _fwd = B("→", "Forward (Alt+→)", () => { if (_web.CanGoForward) _web.GoForward(); });
        _reload = B("↻", "Reload (F5) / stop", () => { if (_loading) Core?.Stop(); else _web.Reload(); });
        var home = B("⌂", "Back to the bookmark's address", () => Go(Bookmark.Url));
        _chip.Style = (Style)FindResource("SmallButton");
        _chip.Click += (_, _) => ShowCertificate();
        _fill = new Button { Content = "⚿ Fill sign-in", Style = (Style)FindResource("SmallButton"), Margin = new Thickness(0, 0, 4, 0),
            ToolTip = "Fills the page's sign-in form from the vault credential set on this portal (or its host). Nothing is saved by the browser." };
        _fill.Click += async (_, _) => await FillSignInAsync();
        // 0.6.20: copy the user name, password or TOTP code instead, for pages the fill can't reach.
        // Small style and its own width (0.6.20: a fixed width with the big button's padding cut the label off).
        var fillMore = Menus.MoreButton(FillMenu, "⧉ Copy ▾", "SmallButton");
        fillMore.MinWidth = 0; fillMore.Margin = new Thickness(0, 0, 4, 0);
        fillMore.ToolTip = "Fill, or copy the user name, password or TOTP code";
        var more = Menus.MoreButton(MoreMenu, "⋯", "SmallButton");
        more.MinWidth = 0; more.Margin = new Thickness(0);

        _addr.SetResourceReference(StyleProperty, "Field");
        _addr.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { if (_addr.Text != _shown) Go(Portals.Normalize(_addr.Text)); else _web.Reload(); e.Handled = true; }
            else if (e.Key == Key.Escape) { _addr.Text = _shown; e.Handled = true; }
        };
        Menus.ForText(_addr);
        // A grid, not a dock panel: the address always keeps a usable width, however narrow the tab (AI sidebar open, split panes).
        var bar = _bar = new Grid { Margin = new Thickness(8, 6, 8, 6) };
        var cells = new UIElement[] { _back, _fwd, _reload, home, _chip, _addr, _profilePill, _fill, fillMore, more };
        for (int i = 0; i < cells.Length; i++)
        {
            bar.ColumnDefinitions.Add(cells[i] == _addr ? new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 120 } : new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(cells[i], i);
            bar.Children.Add(cells[i]);
        }
        _chip.MaxWidth = 260;
        // A narrow tab (AI sidebar open, split panes): the sign-in buttons shrink to their icons, so they stay beside the
        // address instead of being cut off.
        bar.SizeChanged += (_, e) =>
        {
            bool narrow = e.NewSize.Width < 760;
            _fill.Content = narrow ? "⚿" : "⚿ Fill sign-in";
            fillMore.Content = narrow ? "⧉▾" : "⧉ Copy ▾";
            _profilePill.Visibility = e.NewSize.Width < 600 ? Visibility.Collapsed : Visibility.Visible;
        };
        _profilePill.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var label = Portals.ProfileLabel(ProfileName);
        _profilePill.Text = "⚇ " + (label.StartsWith("general") ? "general" : label);
        _profilePill.ToolTip = $"Browser profile “{label}” ({ProfileName}): cookies and sign-ins of this tab live here, separate from the other environments' profiles.";
        _profilePill.Fs(FontStep.Small);

        var bannerRow = new DockPanel();
        DockPanel.SetDock(_bannerButtons, Dock.Right);
        bannerRow.Children.Add(_bannerButtons); bannerRow.Children.Add(_bannerText);
        _banner.Child = bannerRow;
        _banner.SetResourceReference(Border.BorderBrushProperty, "Line");
        _banner.SetResourceReference(Border.BackgroundProperty, "Surface");

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top); DockPanel.SetDock(_banner, Dock.Top);
        root.Children.Add(bar); root.Children.Add(_banner); root.Children.Add(_web);
        Content = root;

        SetSecurity(WebSecurity.None);
        UpdateFill();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F5) { _web.Reload(); e.Handled = true; }
            else if (e.Key == Key.L && Keyboard.Modifiers == ModifierKeys.Control) { _addr.Focus(); _addr.SelectAll(); e.Handled = true; }
            else if (e.SystemKey == Key.Left && Keyboard.Modifiers == ModifierKeys.Alt) { if (_web.CanGoBack) _web.GoBack(); e.Handled = true; }
            else if (e.SystemKey == Key.Right && Keyboard.Modifiers == ModifierKeys.Alt) { if (_web.CanGoForward) _web.GoForward(); e.Handled = true; }
        };
        Live.Add(this);
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        if (_disposed) return;
        try
        {
            WebHost.Paint(_web, "Bg");
            var env = await WebHost.EnvironmentAsync();
            var opts = env.CreateCoreWebView2ControllerOptions();
            opts.ProfileName = ProfileName;
            opts.IsInPrivateModeEnabled = false;
            await _web.EnsureCoreWebView2Async(env, opts);
            if (_disposed) return;
            var core = _web.CoreWebView2;
            var s = core.Settings;
            // No password saving or form autofill: sign-in comes from the vault on request ("Fill sign-in"), never the browser.
            core.Profile.IsPasswordAutosaveEnabled = false;
            core.Profile.IsGeneralAutofillEnabled = false;
            s.IsPasswordAutosaveEnabled = false;
            s.IsGeneralAutofillEnabled = false;
            // No SmartScreen reputation look-ups (they send addresses and downloads' hashes to Microsoft): no unrequested traffic.
            s.IsReputationCheckingRequired = false;
            s.AreDevToolsEnabled = false;
            s.IsStatusBarEnabled = true;
            s.AreBrowserAcceleratorKeysEnabled = true;
            WebHost.GuardOffline(core);
            core.SourceChanged += (_, _) => ShowAddress();
            core.HistoryChanged += (_, _) => { _back.IsEnabled = _web.CanGoBack; _fwd.IsEnabled = _web.CanGoForward; };
            core.DocumentTitleChanged += (_, _) => { if (Tab != null) Tab.Header = "🌐 " + Short(core.DocumentTitle is { Length: > 0 } t ? t : Bookmark.Title, 36); };
            core.NavigationStarting += OnNavigationStarting;
            core.NavigationCompleted += OnNavigationCompleted;
            core.ServerCertificateErrorDetected += OnCertificateError;
            core.NewWindowRequested += OnNewWindow;
            core.WindowCloseRequested += (_, _) => Dispatcher.BeginInvoke(() => { if (IsPopup && Tab != null) MainWindow.Instance.CloseTab(Tab); });
            core.DownloadStarting += OnDownload;
            core.LaunchingExternalUriScheme += (_, a) =>
            {
                a.Cancel = true; Blocked++;
                Dispatcher.BeginInvoke(() => Banner($"⛔ The page tried to start another program ({Short(a.Uri, 80)}). The portal browser doesn't follow such links.", "Warn"));
            };
            core.ProcessFailed += (_, a) => Dispatcher.BeginInvoke(() =>
            {
                if (a.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited) WebHost.ResetEnvironment(core.Environment);
                Banner("The page's browser process stopped (" + a.ProcessFailedKind + "). Close this tab and open the portal again.", "Warn");
            });
            // Forgotten certificates must not stay accepted from earlier in this browser session (ADR-032).
            // (The chip's "pinned" state is checked against the pin itself, so the session list needs no clearing.)
            await core.ClearServerCertificateErrorActionsAsync();
            _ready.TrySetResult();
            if (!IsPopup && _initialUrl != null) Go(_initialUrl);
        }
        catch (Exception ex)
        {
            InitError = ex.Message;
            App.Log("Portal browser init", ex);
            Banner("The WebView2 runtime is needed for the portal browser: " + ex.Message, "Bad");
            _ready.TrySetResult();
        }
    }

    // ---- navigation ------------------------------------------------------------------------------------------------

    /// <summary>Opens an address in this tab (checked: http(s) only, offline mode).</summary>
    public void Go(string url)
    {
        if (Core == null) return;
        if (Portals.ValidateUrl(url) is { } why) { Banner("⛔ " + char.ToUpper(why[0]) + why[1..] + ".", "Warn"); _addr.Text = _shown; return; }
        HideBanner();
        try { Core.Navigate(url); }
        catch (ArgumentException ex) { Banner("⛔ " + ex.Message, "Warn"); }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Portals.NavigationAllowed(e.Uri, out var why)) { e.Cancel = true; Blocked++; Banner("⛔ " + char.ToUpper(why[0]) + why[1..] + ".", "Warn"); return; }
        _loading = true; _reload.Content = "✕";
        _attempted = e.Uri;
        if (RefusedCert is { } rc && Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) && Portals.HostPort(u) != rc.HostPort) { RefusedCert = null; HideBanner(); }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        _loading = false; _reload.Content = "↻";
        ShowAddress();
        if (!e.IsSuccess && RefusedCert is { } rc && Uri.TryCreate(CurrentUrl, UriKind.Absolute, out var u) && Portals.HostPort(u) == rc.HostPort)
            SetSecurity(rc.Decision == TlsDecision.Mismatch ? WebSecurity.Changed : WebSecurity.Untrusted);
        NavigationDone?.Invoke(e.IsSuccess);
    }

    private void ShowAddress()
    {
        var src = CurrentUrl;
        _shown = Portals.Display(src);
        if (!_addr.IsKeyboardFocusWithin) _addr.Text = _shown;
        _back.IsEnabled = _web.CanGoBack; _fwd.IsEnabled = _web.CanGoForward;
        if (!Uri.TryCreate(src, UriKind.Absolute, out var u)) { SetSecurity(WebSecurity.None); return; }
        var hp = Portals.HostPort(u);
        SetSecurity(u.Scheme switch
        {
            "http" => WebSecurity.NotSecure,
            "https" when RefusedCert is { } rc && rc.HostPort == hp => rc.Decision == TlsDecision.Mismatch ? WebSecurity.Changed : WebSecurity.Untrusted,
            "https" when AcceptedThisSession.TryGetValue(hp, out var sha) && Portals.Pin(hp)?.Sha256 == sha => WebSecurity.Pinned,
            "https" => WebSecurity.Secure,
            _ => WebSecurity.None,
        });
        UpdateFill();
    }

    private void SetSecurity(WebSecurity s)
    {
        Security = s;
        var host = Uri.TryCreate(CurrentUrl, UriKind.Absolute, out var u) ? u.Host : "";
        (string text, string brush, string tip) = s switch
        {
            WebSecurity.Secure => ("🔒 " + host, "Good", "HTTPS with a certificate Windows trusts."),
            WebSecurity.Pinned => ("🔒 " + host + " · pinned", "Good", "HTTPS with a self-signed certificate you trusted for this host:port (pinned by SHA-256). Click for details."),
            WebSecurity.NotSecure => ("⚠ Not secure · " + host, "Warn", "Plain HTTP: anything you type here, passwords included, crosses the network unencrypted."),
            WebSecurity.Untrusted => ("⛔ Untrusted certificate · " + host, "Bad", "The certificate isn't trusted by Windows and isn't pinned. Click to compare and trust it."),
            WebSecurity.Changed => ("⛔ CERTIFICATE CHANGED · " + host, "Bad", "This host:port now presents a different certificate from the one you trusted. This can mean interception."),
            _ => ("○", "Muted", "No page loaded."),
        };
        _chip.Content = text;
        _chip.ToolTip = tip;
        _chip.SetResourceReference(ForegroundProperty, brush);
    }

    private static string Short(string s, int n) => s.Length > n ? s[..n] + "…" : s;

    // ---- certificates (TOFU, consistent with TlsPin / ADR-007) -----------------------------------------------------

    private void OnCertificateError(object? sender, CoreWebView2ServerCertificateErrorDetectedEventArgs e)
    {
        try
        {
            var u = new Uri(e.RequestUri);
            var hp = Portals.HostPort(u);
            var sha = Portals.ShaOfPem(e.ServerCertificate.ToPemEncoding());
            var decision = Portals.Decide(hp, sha);
            if (decision == TlsDecision.Accept)
            {
                e.Action = CoreWebView2ServerCertificateErrorAction.AlwaysAllow;
                AcceptedThisSession[hp] = sha;
                if (RefusedCert?.HostPort == hp) { RefusedCert = null; HideBanner(); }
                return;
            }
            e.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
            RefusedCert = (hp, sha, e.ServerCertificate.Subject ?? "", decision, e.ErrorStatus.ToString());
            Dispatcher.BeginInvoke(() =>
            {
                SetSecurity(decision == TlsDecision.Mismatch ? WebSecurity.Changed : WebSecurity.Untrusted);
                Banner(decision == TlsDecision.Mismatch
                    ? $"⛔ CERTIFICATE CHANGED for {hp}: it now presents SHA-256 {TlsPin.Pretty(sha)}, not the one you trusted. This can mean interception — check the certificate on the device before trusting the new one."
                    : $"⛔ {hp} presents a certificate Windows doesn't trust ({e.ErrorStatus}). SHA-256 {TlsPin.Pretty(sha)} — compare it with the certificate on the device (for example its web UI's certificate page or `openssl x509 -fingerprint -sha256`), then trust it for this host:port only.",
                    "Bad", ("Trust this certificate…", () => TrustRefused()));
            });
        }
        catch (Exception ex) { e.Action = CoreWebView2ServerCertificateErrorAction.Cancel; App.Log("Portal certificate", ex); }
    }

    /// <summary>Pins the refused certificate after a confirmation (typed for a changed one), then reloads.</summary>
    public bool TrustRefused()
    {
        if (RefusedCert is not { } rc) return false;
        var changed = rc.Decision == TlsDecision.Mismatch;
        var msg = $"Trust this certificate for {rc.HostPort} only?\n\nSubject: {rc.Subject}\nSHA-256: {TlsPin.Pretty(rc.Sha256)}\nProblem: {rc.Status}\n\n" +
                  "Only trust it if the fingerprint matches the one on the device. Any other untrusted certificate on this host:port will be refused." +
                  (changed ? $"\n\nThis REPLACES the certificate you trusted before ({TlsPin.Pretty(Portals.Pin(rc.HostPort)?.Sha256 ?? "")})." : "");
        if (!Dialogs.Confirm(changed ? "Replace trusted certificate" : "Trust certificate", msg, changed ? "Replace" : "Trust", danger: changed, typeToConfirm: changed ? rc.HostPort : null)) return false;
        Portals.Trust(rc.HostPort, rc.Sha256, rc.Subject);
        RefusedCert = null;
        HideBanner();
        // A changed certificate may still be cached as refused nowhere, but the old one is cached as accepted: start clean.
        var again = CurrentUrl;   // the refused address (Reload would only reload the error page)
        _ = ClearAcceptedAsync().ContinueWith(_ => Dispatcher.BeginInvoke(() => Go(again)));
        return true;
    }

    /// <summary>After a pin is forgotten or replaced: every open portal tab forgets what this browser session accepted.</summary>
    public static async Task ClearAcceptedAsync()
    {
        foreach (var v in Live.ToList())
            if (v.Core is { } c) { try { await c.ClearServerCertificateErrorActionsAsync(); } catch (Exception ex) { App.Log("Portal certificate cache", ex); } }
    }

    private void ShowCertificate()
    {
        var src = CurrentUrl;
        if (!Uri.TryCreate(src, UriKind.Absolute, out var u)) return;
        var hp = Portals.HostPort(u);
        var pin = Portals.Pin(hp);
        var text = Security switch
        {
            WebSecurity.Secure => $"{hp}: HTTPS, certificate trusted by Windows." + (pin != null ? $"\n\nA pinned certificate also exists for this host:port ({TlsPin.Pretty(pin.Sha256)}); it is used only while the certificate is untrusted by Windows." : ""),
            WebSecurity.Pinned => $"{hp}: HTTPS with the certificate you pinned on {pin?.Trusted:yyyy-MM-dd HH:mm}.\nSubject: {pin?.Subject}\nSHA-256: {TlsPin.Pretty(pin?.Sha256 ?? "")}",
            WebSecurity.NotSecure => $"{hp}: plain HTTP. Nothing on this connection is encrypted.",
            _ => RefusedCert is { } rc ? $"{rc.HostPort}: refused certificate.\nSubject: {rc.Subject}\nSHA-256: {TlsPin.Pretty(rc.Sha256)}\nProblem: {rc.Status}" : hp,
        };
        if (Security is WebSecurity.Untrusted or WebSecurity.Changed) { TrustRefused(); return; }
        if (pin != null && Dialogs.Confirm("Certificate", text + "\n\nForget the pinned certificate? The next visit asks again.", "Forget pin")) { Portals.Forget(hp); _ = ClearAcceptedAsync(); }
        else if (pin == null) Dialogs.Info("Certificate", text);
    }

    // ---- pop-ups: tabs in the same profile ------------------------------------------------------------------------

    private void OnNewWindow(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        _popups.RemoveAll(t => t < DateTime.Now.AddMinutes(-1));
        if (_popups.Count >= 20) { Blocked++; Dispatcher.BeginInvoke(() => Banner("⛔ This page opened 20 pop-ups in a minute; further ones are blocked.", "Warn")); return; }
        if (!Portals.NavigationAllowed(e.Uri, out _) && e.Uri != "about:blank" && e.Uri.Length > 0) { Blocked++; return; }
        _popups.Add(DateTime.Now);
        var deferral = e.GetDeferral();
        Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                var popup = PortalUi.OpenPopup(this);
                await popup.Ready;
                if (popup.Core != null) e.NewWindow = popup.Core;
                else if (Portals.ValidateUrl(e.Uri) == null) Go(e.Uri);
            }
            catch (Exception ex) { App.Log("Portal pop-up", ex); }
            finally { deferral.Complete(); }
        });
    }

    // ---- downloads: a chosen folder, asked first ---------------------------------------------------------------------

    private void OnDownload(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        e.Handled = true;   // never the browser's own download flyout
        var deferral = e.GetDeferral();
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                var op = e.DownloadOperation;
                var name = Path.GetFileName(e.ResultFilePath);
                var from = Uri.TryCreate(op.Uri, UriKind.Absolute, out var du) ? du.Host : "this page";
                var folder = Portals.DownloadFolder ?? PortalUi.PickDownloadFolder();
                if (folder == null) { e.Cancel = true; DownloadsRefused++; MainWindow.Instance?.Status("⇩ Download cancelled: no folder chosen"); return; }
                var size = op.TotalBytesToReceive is ulong n && n > 0 ? $"{n / 1024.0:N0} KB" : "size unknown";
                var risky = Portals.IsRisky(name) ? "\n\n⚠ This kind of file can run code on this PC. Open it only if you expected it from this device." : "";
                if (!Dialogs.Confirm("Download", $"Save “{name}” ({size}, {op.MimeType}) from {from}\ninto {folder}?{risky}", "Save", danger: risky.Length > 0))
                { e.Cancel = true; DownloadsRefused++; return; }
                var target = Portals.SafeTarget(folder, name);
                e.ResultFilePath = target;
                op.StateChanged += (_, _) => Dispatcher.BeginInvoke(() =>
                {
                    if (op.State == CoreWebView2DownloadState.Completed)
                    {
                        LastDownloadPath = target;
                        MainWindow.Instance?.Status($"⇩ Saved {Path.GetFileName(target)} to {folder}");
                        AuditLog.Write("you", "browser", "portal download saved", $"{Path.GetFileName(target)} from {from} → {folder}");
                    }
                    else if (op.State == CoreWebView2DownloadState.Interrupted) MainWindow.Instance?.Status($"⇩ Download of {name} stopped: {op.InterruptReason}");
                });
            }
            catch (Exception ex) { e.Cancel = true; App.Log("Portal download", ex); }
            finally { deferral.Complete(); }
        });
    }

    // ---- sign-in from the vault -----------------------------------------------------------------------------------

    private void UpdateFill()
    {
        var c = Portals.SignInCredential(Bookmark);
        _fill.IsEnabled = c != null;
        _fill.ToolTip = c != null ? $"Fill the sign-in form with “{c.Name}” from the vault (only on {Bookmark.Url})." : "Set a vault password on this portal (or its host) to fill sign-in forms.";
    }

    /// <summary>Fills the page's sign-in form from the vault: only on the bookmark's own origin, not over a refused certificate,
    /// with a warning over plain HTTP. Returns the page script's answer, or why it was refused.</summary>
    public async Task<string> FillSignInAsync()
    {
        if (Core == null) return "not ready";
        if (Portals.SignInCredential(Bookmark) is not { } cred) return "no credential";
        var src = _web.Source?.ToString();
        if (Portals.SignInRefusal(Bookmark, src, Security) is { } why) { Banner("⚿ Sign-in not filled: " + why + ".", "Warn"); return "refused: " + why; }
        if (Security == WebSecurity.NotSecure && !Dialogs.Confirm("Fill sign-in", $"This page is plain HTTP: the password “{cred.Name}” would cross the network unencrypted when you sign in.\n\nFill it anyway?", "Fill", danger: true))
            return "refused: not secure";
        if (!cred.IsExternal && !await LockGate.EnsureAsync("cred:" + cred.Id, "sign in to " + Portals.Display(src))) return "locked";
        // 0.6.20: the copy buttons offered when a page can't be filled follow the Bitwarden copy policy.
        _copyAllowed = !cred.External.StartsWith("bw:") || AppSettings.Current.BitwardenAllowCopy;
        if (cred.IsExternal)   // 0.6.17 (ADR-096): read from Bitwarden / Password Safe now, for this fill only
        {
            try { cred = (await ExternalCredentials.ResolveAsync(cred, "signing in to " + Portals.Display(src)))!; }
            catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException or TimeoutException or IOException)
            { Banner("⚿ Sign-in not filled: " + ex.Message, "Warn"); return "refused: " + ex.Message; }
            if (cred.Kind != CredentialKind.Password) { Banner("⚿ Sign-in not filled: that Bitwarden item is an SSH key, not a password.", "Warn"); return "refused: not a password"; }
            // The page may have moved on while the vault was asked: the origin check again, on what is there now.
            if (Portals.SignInRefusal(Bookmark, _web.Source?.ToString(), Security) is { } again) { Banner("⚿ Sign-in not filled: " + again + ".", "Warn"); return "refused: " + again; }
        }
        var user = cred.Username.Length > 0 ? cred.Username : Host?.Username ?? "";
        _fillWatch?.Cancel();
        var watch = _fillWatch = new CancellationTokenSource();
        // 0.6.20: the page is asked what it shows first; the password only goes into a page that has a password box.
        var shows = await Script(Portals.SignInProbeScript());
        string result;
        switch (shows)
        {
            case "password":
                result = await Script(Portals.SignInScript(user, cred.Secret));
                break;
            case "user":
                // A sign-in in steps (Synology DSM, Microsoft, Google…): the user name now, the password when its box appears.
                result = await Script(Portals.SignInUserScript(user));
                if (result == "filled-user")
                    Banner($"⚿ User name filled from “{cred.Name}”. Continue on the page: the password is filled when its box appears.", "Accent", CopyActions(cred, user));
                break;
            case "code" when Totp.Parse(cred.Totp) is { } spec:
                result = await Script(Portals.SignInCodeScript(Totp.Now(spec).Code));
                break;
            default:
                result = "no-password-field";
                break;
        }
        AuditLog.Write("you", "browser", "vault credential filled into a web sign-in", $"{cred.Name} → {Portals.Display(src)} ({result})");
        if (!result.StartsWith("filled"))
        {
            Banner($"⚿ No sign-in box found on this page (it may be inside another site's frame). Copy from “{cred.Name}” instead:", "Warn", CopyActions(cred, user));
            return result;
        }
        if (result != "filled-user") HideBanner();
        MainWindow.Instance?.Status($"⚿ Sign-in filled from “{cred.Name}”" + (Totp.Parse(cred.Totp) != null ? " (the TOTP code goes in when the page asks for it)" : ""));
        if (result == "filled-user" || result.StartsWith("filled") && result != "filled-code" && Totp.Parse(cred.Totp) != null)
            _ = WatchSignInAsync(cred, user, password: result == "filled-user", watch.Token);
        return result;
    }

    private CancellationTokenSource? _fillWatch;
    private Grid? _bar;

    /// <summary>Self-test: toolbar buttons that are cut off (narrower than their content, or past the bar's edge) at the
    /// tab's current width.</summary>
    internal List<string> ClippedToolbarForTest()
    {
        var cut = new List<string>();
        if (_bar == null) return cut;
        _bar.UpdateLayout();
        foreach (var b in _bar.Children.OfType<Button>())
        {
            if (b.Visibility != Visibility.Visible) continue;
            var right = b.TransformToAncestor(_bar).Transform(new Point(b.ActualWidth, 0)).X;
            b.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            if (b.ActualWidth + 0.5 < b.DesiredSize.Width - b.Margin.Left - b.Margin.Right || right > _bar.ActualWidth + 0.5) cut.Add($"{b.Content} ({b.ActualWidth:0}/{b.DesiredSize.Width:0} px, right {right:0} of {_bar.ActualWidth:0})");
        }
        return cut;
    }
    private bool _copyAllowed = true;

    private async Task<string> Script(string js) => Core == null ? "not ready" : (await Core.ExecuteScriptAsync(js)).Trim('"');

    /// <summary>The next steps of a sign-in (90 s at most, on the bookmark's own origin only): the password once its box
    /// appears, then the TOTP code once a code box does.</summary>
    private async Task WatchSignInAsync(Credential cred, string user, bool password, CancellationToken ct)
    {
        var spec = Totp.Parse(cred.Totp);
        var until = DateTime.UtcNow.AddSeconds(90);
        try
        {
            while (DateTime.UtcNow < until && !ct.IsCancellationRequested && !_disposed && (password || spec != null))
            {
                await Task.Delay(700, ct);
                if (Core == null || Portals.SignInRefusal(Bookmark, _web.Source?.ToString(), Security) != null) return;   // left the origin
                var shows = await Script(Portals.SignInProbeScript());
                if (password && shows == "password")
                {
                    var r = await Script(Portals.SignInScript(user, cred.Secret));
                    if (!r.StartsWith("filled")) continue;
                    password = false;
                    HideBanner();
                    MainWindow.Instance?.Status($"⚿ Password filled from “{cred.Name}”");
                    AuditLog.Write("you", "browser", "vault credential filled into a web sign-in", $"{cred.Name} → {Portals.Display(_web.Source?.ToString())} (password step)");
                }
                else if (!password && spec != null && shows == "code")
                {
                    var r = await Script(Portals.SignInCodeScript(Totp.Now(spec).Code));
                    if (r != "filled-code") continue;
                    MainWindow.Instance?.Status($"⚿ TOTP code filled from “{cred.Name}”");
                    AuditLog.Write("you", "browser", "TOTP code filled into a web sign-in", $"{cred.Name} → {Portals.Display(_web.Source?.ToString())}");
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { App.Log("Fill sign-in (next step)", ex); }
    }

    /// <summary>"⧉ User name", "⧉ Password", "⧉ TOTP code": for pages the fill can't reach. Copied the password-manager way
    /// (out of clipboard history and cloud sync, cleared after the set seconds).</summary>
    private (string, Action)[] CopyActions(Credential cred, string user)
    {
        var list = new List<(string, Action)>();
        if (user.Length > 0) list.Add(("⧉ User name", () => { if (!TestMode.Running) Clipboard.SetText(user); MainWindow.Instance?.Status("⧉ User name copied."); }));
        if (!_copyAllowed) return list.ToArray();   // the policy: no Bitwarden secret on the clipboard
        list.Add(("⧉ Password", () => CopySecret(cred, cred.Secret, "password")));
        if (Totp.Parse(cred.Totp) is { } spec) list.Add(("⧉ TOTP code", () => { var (code, left) = Totp.Now(spec); CopySecret(cred, code, $"TOTP code (valid {left} s more)"); }));
        return list.ToArray();
    }

    private static void CopySecret(Credential cred, string text, string what)
    {
        var secs = AppSettings.Current.BitwardenClipboardSeconds;
        if (!SecretClipboard.Copy(text, secs)) { MainWindow.Instance?.Status("The clipboard is busy; try again."); return; }
        AuditLog.Write("you", "browser", $"vault credential's {what.Split(' ')[0]} copied for a web sign-in", cred.Name);
        MainWindow.Instance?.Status($"⧉ {char.ToUpper(what[0]) + what[1..]} of “{cred.Name}” copied: cleared in {secs} s, kept out of clipboard history.");
    }

    /// <summary>The ▾ beside Fill sign-in: the same copies, whenever filling isn't what's wanted.</summary>
    private IEnumerable<object?> FillMenu()
    {
        var c = Portals.SignInCredential(Bookmark);
        yield return Menus.Item("⚿ Fill sign-in", () => _ = FillSignInAsync(), enabled: c != null);
        yield return Menus.Sep();
        yield return Menus.Item("⧉ Copy user name", () => _ = CopyFromVaultAsync("user"), enabled: c != null);
        yield return Menus.Item("⧉ Copy password", () => _ = CopyFromVaultAsync("password"), enabled: c != null);
        yield return Menus.Item("⧉ Copy TOTP code", () => _ = CopyFromVaultAsync("totp"), enabled: c != null && (c.IsExternal || Totp.Parse(c.Totp) != null),
            tip: "From the credential's TOTP secret (or the Bitwarden item's)");
    }

    /// <summary>Reads the portal's credential (from Bitwarden / Password Safe when it lives there) and copies one part.</summary>
    internal async Task<string> CopyFromVaultAsync(string part)
    {
        if (Portals.SignInCredential(Bookmark) is not { } cred) return "no credential";
        if (!cred.IsExternal && !await LockGate.EnsureAsync("cred:" + cred.Id, "copy from " + cred.Name)) return "locked";
        if (cred.IsExternal && part != "user" && cred.External.StartsWith("bw:") && !AppSettings.Current.BitwardenAllowCopy)
        { Banner("⧉ Copying Bitwarden passwords and secrets is switched off on this PC (policy).", "Warn"); return "refused: policy"; }
        var name = cred.Name;
        if (cred.IsExternal)
        {
            try { cred = (await ExternalCredentials.ResolveAsync(cred, "copying for " + Portals.Display(CurrentUrl)))!; }
            catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException or TimeoutException or IOException)
            { Banner("⧉ Not copied: " + ex.Message, "Warn"); return "refused: " + ex.Message; }
        }
        var user = cred.Username.Length > 0 ? cred.Username : Host?.Username ?? "";
        switch (part)
        {
            case "user":
                if (user.Length == 0) { Banner($"⧉ “{name}” has no user name.", "Warn"); return "no user"; }
                if (!TestMode.Running) Clipboard.SetText(user);
                MainWindow.Instance?.Status("⧉ User name copied.");
                return "copied user";
            case "password":
                CopySecret(cred, cred.Secret, "password");
                return "copied password";
            default:
                if (Totp.Parse(cred.Totp) is not { } spec) { Banner($"⧉ “{name}” has no TOTP secret (add it in the credential, or in the Bitwarden item).", "Warn"); return "no totp"; }
                var (code, left) = Totp.Now(spec);
                CopySecret(cred, code, $"TOTP code (valid {left} s more)");
                return "copied totp";
        }
    }

    // ---- menu / banner ---------------------------------------------------------------------------------------------

    private IEnumerable<object?> MoreMenu()
    {
        var src = CurrentUrl;
        var saved = Portals.Find(Bookmark.Id) != null;
        yield return Menus.Item(saved ? "✎ Edit this portal…" : "☆ Save as a portal…", () => PortalUi.Edit(saved ? Bookmark : new PortalBookmark { Title = Core?.DocumentTitle ?? "", Url = src, Environment = Bookmark.Environment, HostId = Bookmark.HostId }));
        yield return Menus.Item("Copy address", () => Menus.Clip(_shown), tip: "As shown: token-like query values are masked");
        yield return Menus.Item("Open in your default browser…", () =>
        {
            if (Dialogs.Confirm("Open in browser", $"Open this address in your default browser?\n\n{Short(src, 200)}\n\nIt leaves the app: your browser's own profile, history and password manager apply.", "Open"))
                Offline.Run(src, "a browser link", () => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(src) { UseShellExecute = true }));
        }, enabled: src.StartsWith("http"));
        yield return Menus.Item("Certificate…", ShowCertificate, enabled: src.StartsWith("https"));
        yield return Menus.Sep();
        yield return Menus.Item($"Clear cookies and site data of profile “{Portals.ProfileLabel(ProfileName)}”…", async () =>
        {
            if (Core == null || !Dialogs.Confirm("Clear profile", $"Sign out of every portal in the “{Portals.ProfileLabel(ProfileName)}” profile (cookies, storage, cache)?", "Clear", danger: true)) return;
            await Core.Profile.ClearBrowsingDataAsync();
            AuditLog.Write("you", "browser", "browser profile cleared", ProfileName);
            _web.Reload();
        });
        if (Tab != null)
        {
            yield return Menus.Sep();
            yield return MainWindow.Instance.Panes.IsFloating(Tab)
                ? Menus.Item("Dock back into the main window", () => MainWindow.Instance.Panes.DockBack(Tab))
                : Menus.Item("Pop out to a window", () => MainWindow.Instance.Panes.PopOut(Tab), "Ctrl+Alt+O");
        }
    }

    private void Banner(string text, string brush, params (string Text, Action Click)[] actions)
    {
        _bannerText.Text = text;
        _bannerText.SetResourceReference(TextBlock.ForegroundProperty, brush);
        _bannerButtons.Children.Clear();
        foreach (var (t, a) in actions)
        {
            var b = new Button { Content = t, Style = (Style)FindResource("SmallButton"), Margin = new Thickness(0, 0, 6, 0) };
            b.Click += (_, _) => a();
            _bannerButtons.Children.Add(b);
        }
        var close = new Button { Content = "✕", MinWidth = 0, Width = 28, Style = (Style)FindResource("SmallButton") };
        close.Click += (_, _) => HideBanner();
        _bannerButtons.Children.Add(close);
        _banner.Visibility = Visibility.Visible;
    }

    private void HideBanner() => _banner.Visibility = Visibility.Collapsed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _fillWatch?.Cancel();
        Live.Remove(this);
        _ready.TrySetResult();
        try { _web.Dispose(); } catch (Exception ex) { App.Log("Portal browser close", ex); }
    }
}
