using System.Windows;
using System.Windows.Input;
using DcxManager.Services.Keys;

namespace DcxManager.Views;

/// <summary>A screen whose own keys win over the app's shortcuts while it has the keyboard (0.6.36, ADR-115): the file
/// commander keeps Midnight Commander's keys (F6 moves, Ctrl+Shift+S filters) and the Docs editor its formatting keys
/// (Ctrl+B bold, Ctrl+K link). The app's other shortcuts still work there.</summary>
public interface IOwnKeys
{
    /// <summary>Whether this screen handles <paramref name="chord"/> itself right now.</summary>
    bool OwnsKey(KeyChord chord);
}

public static class ScreenKeys
{
    /// <summary>Whether a screen around the focused element (or the event's source) keeps this key.</summary>
    public static bool Claimed(KeyEventArgs e, KeyChord chord) =>
        Keyboard.FocusedElement is DependencyObject f && Claimed(f, chord) || e.OriginalSource is DependencyObject o && Claimed(o, chord);

    public static bool Claimed(DependencyObject d, KeyChord chord)
    {
        for (var x = d; x != null; x = x is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(x) ?? LogicalTreeHelper.GetParent(x) : LogicalTreeHelper.GetParent(x))
        {
            if (x is not IOwnKeys owner) continue;
            try { if (owner.OwnsKey(chord)) return true; }
            catch (Exception ex) { Services.Log.Write("Screen keys", ex); }
        }
        return false;
    }
}
