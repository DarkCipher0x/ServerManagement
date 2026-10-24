using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace DcxManager.Services;

/// <summary>
/// The mouse is never left captured once every button is up (0.5.4, DarkCipher0x: "the right-click is getting stuck in the
/// window ... its as if its locked the mouse into that pane. It should be immediately releasing the right-click and not
/// holding onto it"). After a right-click in LOGS → Error log, something kept WPF's mouse capture on that pane in subtree
/// mode: the pane's own buttons still worked (a button takes the capture and gives it back), everything else — the
/// sidebar, other screens — got no clicks. The app's own code never captures the mouse there, so the capture came from
/// inside WPF; whatever holds it, this guard lets it go.
///
/// Rule: when no mouse button is pressed, a capture is only legitimate while a menu, a context menu, an open drop-down
/// or a popup holds it (they keep the mouse to close themselves on an outside click). Anything else is released — after
/// every button-up and on the next mouse move — and logged once per element type with its ancestry, so the error log
/// names the culprit (LOGS → Error log: "Mouse capture released").
/// </summary>
public static class CaptureGuard
{
    private static bool _installed;
    private static readonly HashSet<string> Logged = new();
    private static int _releases;

    /// <summary>Captures released so far (self-test).</summary>
    public static int Releases => Volatile.Read(ref _releases);

    /// <summary>Class handlers on every window (once, at start-up).</summary>
    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseUpEvent, new MouseButtonEventHandler((s, _) => AfterInput(s)), true);
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseMoveEvent, new MouseEventHandler((_, _) => { if (Mouse.Captured != null) Sweep(); }), true);
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.ClosedEvent, new RoutedEventHandler((s, _) => AfterInput(s)), true);
    }

    /// <summary>Checks once the current input has been handled (the menu has opened or the button's click has run).</summary>
    private static void AfterInput(object sender)
    {
        if (sender is DispatcherObject d) d.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => Sweep()));
    }

    /// <summary>Releases a capture nothing legitimate holds while every button is up. True when one was released.</summary>
    public static bool Sweep()
    {
        if (Mouse.Captured is not DependencyObject held) return false;
        // The physical buttons, not WPF's idea of them (0.5.5): a left-click in the log field that closes its context menu
        // goes down on the text box (which captures the mouse to select text) but comes up on the closing menu's popup
        // window. The text box never sees the button-up, so WPF still reports the left button pressed and 0.5.4 left the
        // capture alone. Windows' own state can't miss a release.
        if (AnyButtonDown()) return false;
        if (Legitimate(held)) return false;
        var what = Describe(held);
        Mouse.Capture(null);
        Interlocked.Increment(ref _releases);
        lock (Logged)
            if (Logged.Add(held.GetType().FullName ?? "?"))
                Log.Write("Mouse capture released", new InvalidOperationException($"{what} still held the mouse after every button was up; released (CaptureGuard)"));
        return true;
    }

    /// <summary>Whether any mouse button is physically down right now (GetAsyncKeyState checks the physical buttons, so a
    /// swapped left/right setting makes no difference when all five are checked). The self-test replaces it.</summary>
    internal static Func<bool> AnyButtonDown = () =>
        Down(0x01) || Down(0x02) || Down(0x04) || Down(0x05) || Down(0x06);   // VK_LBUTTON, RBUTTON, MBUTTON, XBUTTON1, XBUTTON2

    private static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    [System.Runtime.InteropServices.DllImport("user32")] private static extern short GetAsyncKeyState(int vk);

    /// <summary>True for the holders that keep the mouse on purpose with no button pressed: menus, context menus,
    /// an open drop-down, anything inside a popup.</summary>
    internal static bool Legitimate(DependencyObject held)
    {
        for (DependencyObject? d = held; d != null; d = Parent(d))
        {
            switch (d)
            {
                case MenuBase or MenuItem or ContextMenu or ToolTip or Popup: return true;
                case ComboBox { IsDropDownOpen: true }: return true;
            }
            if (d is Visual v && PresentationSource.FromVisual(v)?.RootVisual is { } root && root.GetType().Name == "PopupRoot") return true;
        }
        return false;
    }

    private static DependencyObject? Parent(DependencyObject d) =>
        (d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : null)
        ?? LogicalTreeHelper.GetParent(d)
        ?? (d as FrameworkElement)?.TemplatedParent;

    /// <summary>"TextBox in ScrollViewer in ErrorLogView in …" (types only, never text).</summary>
    internal static string Describe(DependencyObject held)
    {
        var chain = new List<string>();
        for (DependencyObject? d = held; d != null && chain.Count < 10; d = Parent(d))
        {
            var name = d.GetType().Name;
            if (d is FrameworkElement { Name.Length: > 0 } fe) name += "#" + fe.Name;
            chain.Add(name);
        }
        return string.Join(" in ", chain);
    }
}
