using System.IO;
using System.Reflection;
using Microsoft.Web.WebView2.Core;

namespace DcxManager.Chat.Calls;

/// <summary>
/// The call screen's page (0.6.38, ADR-117): CipherChat's own call page (its web\callhost page with the portal's call
/// engine, call screen and their libraries — src\Vendor\CipherChat\CallHost, vendored unchanged and checked against
/// VENDOR.json by build.ps1), embedded in this assembly and served to the call window's browser from memory on a private
/// host name. Nothing is unpacked to disk, nothing else is served, and the page's own Content-Security-Policy (no fetch,
/// no outside scripts) is also sent as a header.
/// </summary>
internal static class CallPage
{
    public const string Host = "cipherchat-call.example";
    public static readonly string Origin = "https://" + Host;
    public const string Start = "call.html";

    /// <summary>call.html's own policy, sent as a header as well.</summary>
    private const string Csp = "default-src 'none'; script-src 'self'; worker-src 'self'; style-src 'self'; img-src 'self' data: blob:; media-src blob: mediastream:; connect-src 'none'; base-uri 'none'; form-action 'none'";

    private static Dictionary<string, string>? _names;

    /// <summary>The embedded files by their path under CallHost ("app/call.js").</summary>
    public static IReadOnlyDictionary<string, string> Files
    {
        get
        {
            if (_names != null) return _names;
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var n in typeof(CallPage).Assembly.GetManifestResourceNames())
                if (n.StartsWith("callhost/", StringComparison.Ordinal)) map[n["callhost/".Length..].Replace('\\', '/')] = n;
            return _names = map;
        }
    }

    /// <summary>A file of the page by its URL path, or null (unknown paths, "..", anything not embedded).</summary>
    public static (byte[] Bytes, string Mime)? Get(string path)
    {
        path = Uri.UnescapeDataString(path).TrimStart('/');
        if (path.Length == 0 || path.Contains("..", StringComparison.Ordinal) || path.Contains('\\') || !Files.TryGetValue(path, out var name)) return null;
        using var s = typeof(CallPage).Assembly.GetManifestResourceStream(name);
        if (s == null) return null;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        var mime = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".json" => "application/json",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".woff2" => "font/woff2",
            _ => "application/octet-stream",
        };
        return (ms.ToArray(), mime);
    }

    /// <summary>Serves the page to this browser on <see cref="Origin"/> and refuses everything else it asks for: any other
    /// address the page (or anything in it) tries to load gets nothing (its CSP already forbids it).</summary>
    public static void Map(CoreWebView2 core)
    {
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) =>
        {
            try
            {
                if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var u)) { e.Response = core.Environment.CreateWebResourceResponse(null, 403, "Refused", ""); return; }
                if (u.Scheme is "blob" or "data") return;
                if (u.Scheme != "https" || !u.Host.Equals(Host, StringComparison.OrdinalIgnoreCase) || !u.IsDefaultPort)
                {
                    e.Response = core.Environment.CreateWebResourceResponse(null, 403, "Refused", "");
                    return;
                }
                if (Get(u.AbsolutePath) is not { } file) { e.Response = core.Environment.CreateWebResourceResponse(null, 404, "Not Found", ""); return; }
                e.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(file.Bytes, writable: false), 200, "OK",
                    $"Content-Type: {file.Mime}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nContent-Security-Policy: {Csp}\r\nReferrer-Policy: no-referrer");
            }
            catch (Exception ex) { Services.Log.Write("Call page", ex); }
        };
    }

    /// <summary>A browser setting for the call page: no dev tools, menus, dialogs, autofill, zoom, accelerators or host
    /// objects; only web messages to and from the app.</summary>
    public static void Lock(CoreWebView2Settings s)
    {
        s.AreDevToolsEnabled = false;
        s.AreDefaultContextMenusEnabled = false;
        s.AreDefaultScriptDialogsEnabled = false;
        s.AreHostObjectsAllowed = false;
        s.IsStatusBarEnabled = false;
        s.IsZoomControlEnabled = false;
        s.IsPasswordAutosaveEnabled = false;
        s.IsGeneralAutofillEnabled = false;
        s.AreBrowserAcceleratorKeysEnabled = false;
        s.IsWebMessageEnabled = true;
    }

    /// <summary>The call browser's profile folder: its own, encrypted by Windows for this account (PrivateFolders) before the
    /// browser starts; a self-test store gets one next to it.</summary>
    public static string ProfileFolder => Environment.GetEnvironmentVariable("DCX_DATA_DIR") is { Length: > 0 } scratch
        ? Path.GetFullPath(scratch).TrimEnd('\\') + "-webview2-calls"
        : Path.Combine(Services.AppInfo.CacheDir, "WebView2-calls");

    private static Task<CoreWebView2Environment>? _env, _shareEnv;

    /// <summary>The call browser. Live session sharing (0.6.39) uses a second one, with its own encrypted profile, that
    /// answers the page's screen-share request with the one window titled <see cref="LiveShare.WindowTitle"/> (Chromium's
    /// own source auto-select), so a live share can't pick the screen or another window; ordinary calls keep the picker.</summary>
    public static Task<CoreWebView2Environment> EnvironmentAsync(bool share = false)
    {
        // A failed start isn't kept: the next call or share tries again instead of failing until the app restarts.
        if (_shareEnv is { IsFaulted: true } or { IsCanceled: true }) _shareEnv = null;
        if (_env is { IsFaulted: true } or { IsCanceled: true }) _env = null;
        return share ? _shareEnv ??= CreateAsync(ProfileFolder + "-share", " --auto-select-desktop-capture-source=\"" + LiveShare.WindowTitle + "\"")
                     : _env ??= CreateAsync(ProfileFolder, "");
    }

    private static async Task<CoreWebView2Environment> CreateAsync(string folder, string extra)
    {
        await Task.Run(() => Services.PrivateFolders.Prepare(folder));
        return await CoreWebView2Environment.CreateAsync(null, folder, new CoreWebView2EnvironmentOptions("--disable-features=msSmartScreenProtection" + extra));
    }

    /// <summary>The WebView2 runtime is installed (calls need it).</summary>
    public static bool RuntimeAvailable()
    {
        try { CoreWebView2Environment.GetAvailableBrowserVersionString(); return true; }
        catch (WebView2RuntimeNotFoundException) { return false; }
    }
}
