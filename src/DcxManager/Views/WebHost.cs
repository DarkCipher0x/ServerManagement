using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DcxManager.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DcxManager.Views;

/// <summary>Shared WebView2 environment and the unpacked terminal page.</summary>
public static class WebHost
{
    private static Task<CoreWebView2Environment>? _env;
    private static readonly object EnvGate = new();
    private static string? _webDir;

    /// <summary>The browser profile of this store (ADR-021): the installation keeps its usual folder (portal sign-in,
    /// cookies); a DCX_DATA_DIR store (self-test, scratch) gets its own next to it, so it never shares a browser process
    /// with the installed app.</summary>
    public static string UserDataFolder => Environment.GetEnvironmentVariable("DCX_DATA_DIR") is { Length: > 0 } scratch
        ? Path.GetFullPath(scratch).TrimEnd('\\') + "-webview2"
        : Path.Combine(App.CacheDir, "WebView2");

    public static Task<CoreWebView2Environment> EnvironmentAsync()
    {
        lock (EnvGate) return _env ??= CoreWebView2Environment.CreateAsync(null, UserDataFolder);
    }

    /// <summary>After the browser process died, the next view creates a new environment — once: only the view whose
    /// environment is still the current one resets it; the others then reuse the new one.</summary>
    public static void ResetEnvironment(CoreWebView2Environment? failed)
    {
        lock (EnvGate)
        {
            if (_env is { IsCompletedSuccessfully: true } cur && failed != null && !ReferenceEquals(cur.Result, failed)) return;
            _env = null;
            Resets++;
        }
    }

    /// <summary>How many times the environment was replaced (self-test).</summary>
    internal static int Resets { get; private set; }

    /// <summary>Extracts the embedded terminal page (xterm.js) to the local cache once per version.</summary>
    public static string WebDir()
    {
        if (_webDir != null) return _webDir;
        var dir = Path.Combine(App.CacheDir, "web-" + App.Version);
        Directory.CreateDirectory(dir);
        var asm = typeof(WebHost).Assembly;
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("web/")))
        {
            var target = Path.Combine(dir, name[4..]);
            using var s = asm.GetManifestResourceStream(name)!;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            var bytes = ms.ToArray();
            if (File.Exists(target) && File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes)) continue;
            File.WriteAllBytes(target, bytes);
        }
        return _webDir = dir;
    }

    /// <summary>
    /// Serves the terminal page for https://dcx.term/* straight from the unpacked web folder via WebResourceRequested.
    /// Replaces SetVirtualHostNameToFolderMapping, which aborted navigations (ConnectionAborted) on WebView2 runtime 155.
    /// </summary>
    public static void MapTerminalHost(CoreWebView2 core)
    {
        var dir = WebDir();
        core.AddWebResourceRequestedFilter("https://dcx.term/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) =>
        {
            try
            {
                var uri = new Uri(e.Request.Uri);
                if (!uri.Host.Equals("dcx.term", StringComparison.OrdinalIgnoreCase)) return;
                var rel = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
                var full = Path.GetFullPath(Path.Combine(dir, rel));
                if (!full.StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
                {
                    e.Response = core.Environment.CreateWebResourceResponse(null, 404, "Not Found", "");
                    return;
                }
                var mime = Path.GetExtension(full).ToLowerInvariant() switch
                {
                    ".html" => "text/html; charset=utf-8",
                    ".js" => "text/javascript; charset=utf-8",
                    ".css" => "text/css; charset=utf-8",
                    ".svg" => "image/svg+xml",
                    ".png" => "image/png",
                    ".woff2" => "font/woff2",
                    _ => "application/octet-stream",
                };
                var ms = new MemoryStream(File.ReadAllBytes(full));
                e.Response = core.Environment.CreateWebResourceResponse(ms, 200, "OK",
                    $"Content-Type: {mime}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff");
            }
            catch (Exception ex) { App.Log("Terminal resource", ex); }
        };
    }

    public static async Task InitAsync(WebView2 view, string backgroundKey = "Bg")
    {
        // The browser paints white until the page draws its own background: a white flash on every new terminal in a dark
        // theme. Paint the theme colour instead (must be set before the core is created to cover the first frame).
        Paint(view, backgroundKey);
        await view.EnsureCoreWebView2Async(await EnvironmentAsync());
        var s = view.CoreWebView2.Settings;
        s.AreDevToolsEnabled = false;
        s.IsStatusBarEnabled = false;
        s.AreBrowserAcceleratorKeysEnabled = false;
    }

    /// <summary>Offline mode (ADR-016) for a browsing view: every navigation and sub-request is checked (no DNS here: unknown
    /// names are refused). Shared by the web windows and the portal browser (ADR-032).</summary>
    public static void GuardOffline(CoreWebView2 core)
    {
        core.NavigationStarting += (_, e) =>
        {
            if (!Offline.On || !Uri.TryCreate(e.Uri, UriKind.Absolute, out var nu) || nu.Scheme is not ("http" or "https" or "ws" or "wss")) return;
            if (Offline.QuickAllowed(nu.Host)) return;
            e.Cancel = true;
            Offline.Tell(Offline.Block(nu.Host, "a web page", "outside your network or not approved"));
        };
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) =>
        {
            if (!Offline.On || !Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var ru) || ru.Scheme is not ("http" or "https" or "ws" or "wss")) return;
            if (Offline.QuickAllowed(ru.Host)) return;
            Offline.Block(ru.Host, "a web request", "outside your network or not approved");
            e.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked by offline mode", "");
        };
    }

    /// <summary>Sets the browser's own background (shown before and between page paints) to a theme colour.</summary>
    public static void Paint(WebView2 view, string backgroundKey)
    {
        try { var c = ThemeManager.Hex(backgroundKey); view.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, c.R, c.G, c.B); }
        catch (Exception ex) { App.Log("WebView2 background", ex); }
    }
}

/// <summary>A browser window for the Vultr web console (noVNC), bare-metal VNC, or the Vultr customer portal.</summary>
public sealed class WebWindow : Window
{
    private static readonly Dictionary<string, WebWindow> Open_ = new();

    public static void Open(string title, string url, string? reuseKey = null)
    {
        // Offline mode (ADR-016): checked before the window exists.
        if (Offline.On) { Offline.Run(url, "a web window (" + title + ")", () => OpenNow(title, url, reuseKey)); return; }
        OpenNow(title, url, reuseKey);
    }

    private static void OpenNow(string title, string url, string? reuseKey)
    {
        if (reuseKey != null && Open_.TryGetValue(reuseKey, out var w)) { w.Activate(); return; }
        var win = new WebWindow(title, url);
        if (reuseKey != null) { Open_[reuseKey] = win; win.Closed += (_, _) => Open_.Remove(reuseKey); }
        win.Show();
    }

    private WebWindow(string title, string url)
    {
        Title = title + " — ServerManagement";
        Typography.Size(this, 1180, 820);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SetResourceReference(BackgroundProperty, "Bg");
        Icon = Application.Current.MainWindow?.Icon;
        var view = new WebView2();
        var bar = new DockPanel { Margin = new Thickness(8, 6, 8, 6) };
        var back = new Button { Content = "←", MinWidth = 0, Width = 32, Style = (Style)FindResource("SmallButton") };
        var reload = new Button { Content = "↻", MinWidth = 0, Width = 32, Style = (Style)FindResource("SmallButton") };
        var external = new Button { Content = "Open in browser", Style = (Style)FindResource("SmallButton") };
        var addr = new TextBlock { Foreground = (System.Windows.Media.Brush)FindResource("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis }.Fs(FontStep.Caption).Mono();
        DockPanel.SetDock(external, Dock.Right);
        bar.Children.Add(external); bar.Children.Add(back); bar.Children.Add(reload); bar.Children.Add(addr);
        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar); root.Children.Add(view);
        Content = root;
        // Toolbar only (ADR-015). The page stays at 100 %: a noVNC console would be resampled, and sites keep Ctrl+wheel zoom.
        Typography.Track(this, bar);
        back.Click += (_, _) => { if (view.CanGoBack) view.GoBack(); };
        reload.Click += (_, _) => view.Reload();
        external.Click += (_, _) =>
        {
            var u = view.Source?.ToString() ?? url;
            if (!Uri.TryCreate(u, UriKind.Absolute, out var ext) || ext.Scheme is not ("http" or "https")) { Dialogs.Info("Open in browser", "Only http and https addresses can be opened in your browser."); return; }
            if (Dialogs.Confirm("Open in browser", $"Open this address in your default browser?\n\n{(u.Length > 160 ? u[..160] + "…" : u)}\n\nConsole links contain a session token — treat them like a password.", "Open"))
                Offline.Run(u, "a browser link", () => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(u) { UseShellExecute = true }));
        };
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        Loaded += async (_, _) =>
        {
            try
            {
                WebHost.Paint(view, "Bg");
                await view.EnsureCoreWebView2Async(await WebHost.EnvironmentAsync());
                view.CoreWebView2.SourceChanged += (_, _) => addr.Text = Redact(view.Source?.ToString() ?? "");
                view.CoreWebView2.NewWindowRequested += (_, e) => { e.Handled = true; view.CoreWebView2.Navigate(e.Uri); };
                WebHost.GuardOffline(view.CoreWebView2);
                view.Source = new Uri(url);
            }
            catch (Exception ex) { Dialogs.Error(title, "WebView2 runtime is needed for the console and portal.\n\n" + ex.Message); Close(); }
        };
        KeyDown += (_, e) => { if (e.Key == Key.F5) view.Reload(); };
    }

    /// <summary>Hide tokens in the address shown on screen (console URLs carry a session token).</summary>
    private static string Redact(string u)
    {
        var q = u.IndexOf('?');
        return q > 0 ? u[..q] + "?…" : u;
    }
}
