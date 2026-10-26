using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace DcxManager.Services;

/// <summary>
/// Alerts from sessions you aren't looking at (0.6.29, ADR-104): a long command finished, a bell, a highlight rule with an
/// alert, or the session dropped. The tab gets a dot (cleared when it's shown), the taskbar button flashes when the app
/// isn't in front, and a Windows notification says what happened; clicking it brings the tab forward. A tab you are
/// looking at never alerts. Settings → Terminal → Alerts (Group Policy too); the self-test records, never shows.
/// </summary>
public static class SessionAlerts
{
    public static readonly DependencyProperty PendingProperty = DependencyProperty.RegisterAttached("Pending", typeof(bool), typeof(SessionAlerts), new PropertyMetadata(false));
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached("Text", typeof(string), typeof(SessionAlerts), new PropertyMetadata(""));
    public static bool GetPending(DependencyObject o) => (bool)o.GetValue(PendingProperty);
    public static void SetPending(DependencyObject o, bool v) => o.SetValue(PendingProperty, v);
    public static string GetText(DependencyObject o) => (string)o.GetValue(TextProperty);
    public static void SetText(DependencyObject o, string v) => o.SetValue(TextProperty, v);

    public sealed record Alert(DateTime At, string Kind, string Title, string Text, bool Desktop);

    /// <summary>Every alert raised in this run (newest last, at most 200): the self-test reads it.</summary>
    public static List<Alert> Recent { get; } = new();

    /// <summary>Self-test: treat every tab as not looked at (the test window is never in front).</summary>
    internal static bool NobodyWatchingForTest { get; set; }

    /// <summary>Whether you are looking at this tab: selected, on screen, and its window active and not minimised.</summary>
    public static bool Watched(TabItem tab)
    {
        if (NobodyWatchingForTest) return false;
        var w = Window.GetWindow(tab);
        return tab.IsSelected && tab.IsVisible && w is { IsActive: true } && w.WindowState != WindowState.Minimized;
    }

    private static TabItem? TabOf(FrameworkElement view) => MainWindow.Instance?.Panes.AllTabs.FirstOrDefault(t => t.Content == view);

    /// <summary>Raises an alert for the tab showing <paramref name="view"/>, unless you are looking at it. False when nothing
    /// was raised (alerts off, no tab, watched).</summary>
    public static bool Raise(FrameworkElement view, string kind, string title, string text)
    {
        if (!AppSettings.Current.TerminalAlerts || TabOf(view) is not { } tab || Watched(tab)) return false;
        SetPending(tab, true);
        SetText(tab, $"{DateTime.Now:HH:mm} · {text}");
        var w = Window.GetWindow(tab);
        bool appInFront = Application.Current.Windows.OfType<Window>().Any(x => x.IsActive);
        bool desktop = AppSettings.Current.TerminalAlertDesktop && !appInFront;
        lock (Recent) { Recent.Add(new Alert(DateTime.Now, kind, title, text, desktop)); if (Recent.Count > 200) Recent.RemoveAt(0); }
        if (TestMode.Running) return true;
        // Selected but the window wasn't in front: the dot goes when you come back to it.
        if (w != null && tab.IsSelected)
        {
            void Back(object? s, EventArgs e) { w.Activated -= Back; if (tab.IsSelected) SetPending(tab, false); }
            w.Activated += Back;
        }
        if (w != null && !w.IsActive) Flash(w);
        if (desktop) ShowDesktop(title, text, tab);
        return true;
    }

    /// <summary>The tab is being looked at: its dot goes.</summary>
    public static void Clear(FrameworkElement view)
    {
        if (TabOf(view) is { } tab && GetPending(tab)) { SetPending(tab, false); SetText(tab, ""); }
    }

    // ---- the taskbar ------------------------------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags; public uint uCount; public uint dwTimeout; }
    [DllImport("user32.dll")] private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);
    private const uint FLASHW_ALL = 3, FLASHW_TIMERNOFG = 12;

    private static void Flash(Window w)
    {
        try
        {
            var h = new WindowInteropHelper(w).Handle;
            if (h == IntPtr.Zero) return;
            var fi = new FLASHWINFO { cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(), hwnd = h, dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG, uCount = 3, dwTimeout = 0 };
            FlashWindowEx(ref fi);
        }
        catch (Exception ex) { App.Log("Alert flash", ex); }
    }

    // ---- the Windows notification -------------------------------------------------------------------------------------
    // A notification-area icon that appears only while an alert is showing: Windows 10/11 show its balloon as a
    // notification. No package identity, no extra library, nothing leaves the PC.

    private static System.Windows.Forms.NotifyIcon? _icon;
    private static WeakReference<TabItem>? _lastTab;
    private static System.Windows.Threading.DispatcherTimer? _hide;

    private static void ShowDesktop(string title, string text, TabItem tab)
    {
        try
        {
            if (_icon == null)
            {
                _icon = new System.Windows.Forms.NotifyIcon();
                try { _icon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? "") ?? System.Drawing.SystemIcons.Information; }
                catch { _icon.Icon = System.Drawing.SystemIcons.Information; }
                _icon.BalloonTipClicked += (_, _) => Application.Current.Dispatcher.BeginInvoke(BringBack);
                _icon.MouseClick += (_, _) => Application.Current.Dispatcher.BeginInvoke(BringBack);
                Application.Current.Exit += (_, _) => { try { _icon.Visible = false; _icon.Dispose(); } catch { } };
                _hide = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
                _hide.Tick += (_, _) => { _hide.Stop(); if (_icon != null) _icon.Visible = false; };
            }
            var name = Application.Current.MainWindow?.Title is { Length: > 0 } t ? t : "ServerManagement";
            _icon.Text = name.Length > 63 ? name[..63] : name;
            _lastTab = new WeakReference<TabItem>(tab);
            _icon.Visible = true;
            _icon.ShowBalloonTip(8000, Trim(title, 63), Trim(text, 255), System.Windows.Forms.ToolTipIcon.Info);
            _hide!.Stop(); _hide.Start();
        }
        catch (Exception ex) { App.Log("Alert notification", ex); }
    }

    private static string Trim(string s, int max) => s.Length > max ? s[..(max - 1)] + "…" : s;

    private static void BringBack()
    {
        if (_icon != null) _icon.Visible = false;
        if (_lastTab?.TryGetTarget(out var tab) != true || tab == null || MainWindow.Instance is not { } main) return;
        if (!main.Panes.AllTabs.Contains(tab)) return;
        main.Panes.Select(tab);
        if (Window.GetWindow(tab) is { } w)
        {
            if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
            w.Activate();
        }
        if (tab.Content is IActivatable a) a.Activated();
    }
}
