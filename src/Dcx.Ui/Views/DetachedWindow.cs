using System.Windows;
using System.Windows.Input;
using DcxManager.Services;

namespace DcxManager.Views;

/// <summary>
/// Shared chrome for windows that hold content moved out of the main window without recreating it (pane pop-outs, the
/// detached AI sidebar): owner, theme, title bar, UI text size on <paramref name="root"/>, app shortcuts, and a quiet close
/// for programmatic closes. A close by the user calls <see cref="OnUserClosing"/> so the owner can take the content back.
/// </summary>
public abstract class DetachedWindow : Window
{
    /// <summary>The app's keyboard shortcuts, so they work in these windows too (registered by the app: the main window's).</summary>
    public static Action<KeyEventArgs>? AppShortcuts { get; set; }

    private bool _quiet;

    protected DetachedWindow(Window? owner, FrameworkElement root, double width, double height)
    {
        Width = width; Height = height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Owner = owner;
        ShowInTaskbar = true;
        SetResourceReference(BackgroundProperty, "Bg");
        SetResourceReference(ForegroundProperty, "Fg");
        this.Fs(FontStep.Base);
        Icon = owner?.Icon;
        Content = root;
        ThemeManager.TrackScale(this, root);
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        PreviewKeyDown += (_, e) => AppShortcuts?.Invoke(e);
        Closing += (_, _) => { if (!_quiet) OnUserClosing(); };
        // The self-test drives pop-outs and the detached AI window: like the main window, they stay off-screen there
        // (CenterOwner of an off-screen owner still landed at 0,0).
        if (TestMode.Running)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -10000; Top = -10000;
            ShowInTaskbar = false; ShowActivated = false;
            // Belt and braces: nothing in a self-test may maximise one of these onto the user's screen.
            StateChanged += (_, _) => { if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal; };
        }
    }

    /// <summary>The user closed the window (title bar ×, Alt+F4, taskbar): hand the content back.</summary>
    protected abstract void OnUserClosing();

    public void CloseQuietly() { _quiet = true; try { Close(); } catch { } }

    /// <summary>The window is being closed by the app (<see cref="CloseQuietly"/>), not by the user.</summary>
    protected bool IsQuiet => _quiet;
}
