using System.Windows;
using System.Windows.Input;

namespace DcxManager.Views;

/// <summary>A screen that sends the keyboard to a remote program: a terminal, a VNC or Remote Desktop session (0.6.36,
/// ADR-115). While one has the keyboard, the app takes only the shortcuts that
/// <see cref="Services.Keys.KeyChord.WorksInRemoteSessions"/>.</summary>
public interface IRemoteKeyboard { }

public static class RemoteKeyboard
{
    /// <summary>Whether the key press is going to a remote program (the focused element or the event's source is inside one).</summary>
    public static bool HasKeyboard(KeyEventArgs e) =>
        Keyboard.FocusedElement is DependencyObject f && Inside(f) || e.OriginalSource is DependencyObject o && Inside(o);

    public static bool Inside(DependencyObject d)
    {
        for (var x = d; x != null; x = x is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(x) ?? LogicalTreeHelper.GetParent(x) : LogicalTreeHelper.GetParent(x))
            if (x is IRemoteKeyboard) return true;
        return false;
    }
}
