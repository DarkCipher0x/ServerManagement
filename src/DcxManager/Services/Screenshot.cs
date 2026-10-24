using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace DcxManager.Services;

/// <summary>
/// Screenshots of terminals, Remote Desktop windows and any pane. Terminals are rendered by WebView2, which WPF's
/// RenderTargetBitmap can't see, so the browser captures itself; everything else goes through PrintWindow
/// (PW_RENDERFULLCONTENT, works even when the window is covered) with a screen copy as fallback for DirectX surfaces
/// such as mstsc. Screenshots are plain PNG files — the status bar says so every time one is written.
/// </summary>
public static class Screenshot
{
    public static string Dir
    {
        get
        {
            var d = AppSettings.Current.ScreenshotDir;
            if (string.IsNullOrWhiteSpace(d)) d = _default ??= StorageGuard.LocalDefault(Environment.SpecialFolder.MyPictures, "DarkCipher ServerManagement", "Screenshots");
            return d;
        }
    }

    /// <summary>Pictures\DarkCipher ServerManagement — unless Pictures is synchronised (OneDrive folder backup), then a
    /// local app folder, so screenshots of terminals never get uploaded just because of where Windows put Pictures.</summary>
    private static string? _default;

    // ---- capture ----------------------------------------------------------------------------------------------------

    /// <summary>PNG of the terminal page (xterm canvas) exactly as drawn.</summary>
    public static async Task<BitmapSource> CaptureWebAsync(CoreWebView2 web)
    {
        using var ms = new MemoryStream();
        await web.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
        ms.Position = 0;
        var img = new BitmapImage();
        img.BeginInit(); img.CacheOption = BitmapCacheOption.OnLoad; img.StreamSource = ms; img.EndInit(); img.Freeze();
        return img;
    }

    /// <summary>A WPF element (a whole tab, a pane) including any WebView2 inside it: the window is printed and cropped.</summary>
    public static BitmapSource CaptureElement(FrameworkElement el)
    {
        var win = Window.GetWindow(el) ?? throw new InvalidOperationException("The view isn't in a window.");
        var hwnd = new WindowInteropHelper(win).Handle;
        var full = CaptureWindow(hwnd, clientOnly: true);
        var dpi = VisualTreeHelper.GetDpi(el);
        var origin = el.PointToScreen(new Point(0, 0));
        var client = new POINT(); ClientToScreen(hwnd, ref client);
        int x = Math.Max(0, (int)Math.Round(origin.X - client.X)), y = Math.Max(0, (int)Math.Round(origin.Y - client.Y));
        int w = (int)Math.Round(el.ActualWidth * dpi.DpiScaleX), h = (int)Math.Round(el.ActualHeight * dpi.DpiScaleY);
        w = Math.Min(w, full.PixelWidth - x); h = Math.Min(h, full.PixelHeight - y);
        if (w <= 0 || h <= 0) return full;
        var crop = new CroppedBitmap(full, new Int32Rect(x, y, w, h));
        crop.Freeze();
        return crop;
    }

    /// <summary>Any top-level window by handle (ours or another process's, e.g. mstsc).</summary>
    public static BitmapSource CaptureWindow(IntPtr hwnd, bool clientOnly = false)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) throw new InvalidOperationException("That window is no longer open.");
        if (IsIconic(hwnd)) throw new InvalidOperationException("The window is minimised — restore it first, then take the screenshot.");

        RECT r;
        if (clientOnly) { GetClientRect(hwnd, out r); }
        else if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out r, Marshal.SizeOf<RECT>()) != 0) GetWindowRect(hwnd, out r);
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0) throw new InvalidOperationException("The window has no visible area.");

        Dib(w, h, out var hdc, out var hbmp, out var bits, out var old);
        try
        {
            bool ok = PrintWindow(hwnd, hdc, (clientOnly ? PW_CLIENTONLY : 0) | PW_RENDERFULLCONTENT);
            if (!ok || Uniform(bits, w, h))
            {
                // DirectX-presented content (mstsc, games) can print black: copy it off the screen instead.
                var screen = GetDC(IntPtr.Zero);
                try
                {
                    int sx = r.Left, sy = r.Top;
                    if (clientOnly) { var p = new POINT(); ClientToScreen(hwnd, ref p); sx = p.X; sy = p.Y; }
                    BitBlt(hdc, 0, 0, w, h, screen, sx, sy, SRCCOPY);
                }
                finally { ReleaseDC(IntPtr.Zero, screen); }
            }
            var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bits, w * h * 4, w * 4);
            src.Freeze();
            return src;
        }
        finally { SelectObject(hdc, old); DeleteObject(hbmp); DeleteDC(hdc); }
    }

    private static bool Uniform(IntPtr bits, int w, int h)
    {
        // Sample a grid; a real window is never a single colour.
        int first = Marshal.ReadInt32(bits);
        for (int y = 0; y < h; y += Math.Max(1, h / 16))
            for (int x = 0; x < w; x += Math.Max(1, w / 16))
                if ((Marshal.ReadInt32(bits, (y * w + x) * 4) & 0xFFFFFF) != (first & 0xFFFFFF)) return false;
        return true;
    }

    // ---- delivery -----------------------------------------------------------------------------------------------------

    public static byte[] Png(BitmapSource img)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(img));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    public static string FileName(string subject) => $"{Safe(subject)}_{DateTime.Now:yyyyMMdd-HHmmss}.png";

    /// <summary>Writes to the screenshots folder and returns the path.</summary>
    public static string Save(BitmapSource img, string subject)
    {
        Directory.CreateDirectory(Dir);
        var path = Path.Combine(Dir, FileName(subject));
        File.WriteAllBytes(path, Png(img));
        AuditLog.Write("you", subject, "saved screenshot", path);
        Ui.Window.Status($"Screenshot saved (plain PNG, not encrypted) → {path}");
        return path;
    }

    public static void SaveAs(BitmapSource img, string subject)
    {
        var dlg = new SaveFileDialog { FileName = FileName(subject), Filter = "PNG image|*.png", InitialDirectory = Directory.Exists(Dir) ? Dir : null };
        if (dlg.ShowDialog() != true) return;
        if (!StorageGuard.ConfirmDestination(Path.GetDirectoryName(dlg.FileName)!, "Save screenshot", encrypted: false)) return;
        File.WriteAllBytes(dlg.FileName, Png(img));
        AuditLog.Write("you", subject, "saved screenshot", dlg.FileName);
        Ui.Window.Status($"Screenshot saved (plain PNG, not encrypted) → {dlg.FileName}");
    }

    public static void Copy(BitmapSource img)
    {
        try { Clipboard.SetImage(img); Ui.Window.Status($"Screenshot copied to the clipboard ({img.PixelWidth}×{img.PixelHeight})"); }
        catch (Exception ex) { Ui.Dialogs.Error("Screenshot", "Couldn't put the image on the clipboard: " + ex.Message); }
    }

    public static void Share(Window? owner, BitmapSource img, string subject)
    {
        Directory.CreateDirectory(ShareService.TempDir);
        var path = Path.Combine(ShareService.TempDir, FileName(subject));
        File.WriteAllBytes(path, Png(img));
        ShareService.ShareFiles(owner, $"Screenshot — {subject}", path);
    }

    /// <summary>What the shortcut does (Settings → Terminal): "save", "clipboard" or "both".</summary>
    public static void Default(Window? owner, BitmapSource img, string subject)
    {
        var mode = AppSettings.Current.ScreenshotDefault;
        if (mode is "save" or "both") Save(img, subject);
        if (mode is "clipboard" or "both") Copy(img);
    }

    public static void OpenFolder()
    {
        Directory.CreateDirectory(Dir);
        Process.Start(new ProcessStartInfo(Dir) { UseShellExecute = true });
    }

    /// <summary>The standard "📷 Screenshot" submenu for a capture function. The capture runs when an item is clicked.</summary>
    public static System.Windows.Controls.MenuItem Menu(Func<Task<BitmapSource>> capture, string subject, Window? owner = null, string header = "📷 Screenshot") =>
        Views.Menus.Sub(header, MenuItems(capture, subject, owner).ToArray());

    /// <summary>The same items, fresh (menu items can only live in one menu), for a button's own right-click menu.</summary>
    public static List<object?> MenuItems(Func<Task<BitmapSource>> capture, string subject, Window? owner = null)
    {
        async Task Run(Action<BitmapSource> deliver) { var img = await capture(); deliver(img); }
        return new List<object?>
        {
            Views.Menus.AsyncItem("Save to screenshots folder", () => Run(i => Save(i, subject))),
            Views.Menus.AsyncItem("Save as…", () => Run(i => SaveAs(i, subject))),
            Views.Menus.AsyncItem("Copy image", () => Run(Copy)),
            Views.Menus.AsyncItem("Share…", () => Run(i => Share(owner, i, subject)), enabled: ShareService.Supported),
            Views.Menus.AsyncItem("Share to Microsoft Teams…", async () => { var img = await capture(); await Views.TeamsUi.ShareAsync(owner, "Screenshot: " + subject, "", img); }),
            Views.Menus.AsyncItem(Incidents.IncidentStore.Active is { } a ? $"⚑ Add to incident “{Views.IncidentUi.Short(a.Title)}”" : "⚑ Add to incident…", () => Run(i => Views.IncidentUi.AddScreenshot(i, subject))),
            Views.Menus.Sep(),
        }
        .Concat(Views.AppModules.MenuItems("screenshot", new Views.ScreenshotMenuContext(capture, subject, owner)))   // the AI's "Analyse with AI"
        .Concat(new object?[] { Views.Menus.Sep(), Views.Menus.Item("Open screenshots folder", OpenFolder) })
        .ToList();
    }

    // ---- window lookup (for Remote Desktop) -----------------------------------------------------------------------

    /// <summary>Top-level, visible windows whose title contains <paramref name="needle"/> (case-insensitive).</summary>
    public static List<(IntPtr Hwnd, string Title)> FindWindows(string needle, string? className = null)
    {
        var found = new List<(IntPtr, string)>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            if (className != null) { var cls = new StringBuilder(128); GetClassName(h, cls, cls.Capacity); if (!cls.ToString().Equals(className, StringComparison.OrdinalIgnoreCase)) return true; }
            int len = GetWindowTextLength(h);
            if (len == 0) return true;
            var sb = new StringBuilder(len + 1);
            GetWindowText(h, sb, sb.Capacity);
            var title = sb.ToString();
            if (title.Contains(needle, StringComparison.OrdinalIgnoreCase)) found.Add((h, title));
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static string Safe(string name)
    {
        var s = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '-' : c)).Trim('-');
        return s.Length == 0 ? "screenshot" : (s.Length > 60 ? s[..60] : s);
    }

    // ---- GDI / user32 ---------------------------------------------------------------------------------------------

    /// <summary>A top-down 32-bit DIB selected into a memory DC; the caller frees all four handles.</summary>
    private static void Dib(int w, int h, out IntPtr hdc, out IntPtr hbmp, out IntPtr bits, out IntPtr old)
    {
        hdc = CreateCompatibleDC(IntPtr.Zero);
        var bi = new BITMAPINFO { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32, biCompression = 0 };
        hbmp = CreateDIBSection(hdc, ref bi, 0, out bits, IntPtr.Zero, 0);
        if (hbmp == IntPtr.Zero) { DeleteDC(hdc); throw new InvalidOperationException("Couldn't allocate the screenshot bitmap."); }
        old = SelectObject(hdc, hbmp);
    }

    private const uint PW_CLIENTONLY = 1, PW_RENDERFULLCONTENT = 2;
    private const int SRCCOPY = 0x00CC0020, DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        public int colors;   // one RGBQUAD placeholder
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref POINT p);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT r, int size);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
}
