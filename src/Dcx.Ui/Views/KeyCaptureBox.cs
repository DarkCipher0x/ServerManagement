using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using DcxManager.Services.Keys;

namespace DcxManager.Views;

/// <summary>
/// A box that records the next key you press as a shortcut (Settings → Keyboard, 0.6.36, ADR-115). While it has the
/// keyboard the app's own shortcuts are not run (<see cref="KeyCommands.TryRun(KeyEventArgs, bool)"/>), so any key can be
/// recorded. Tab and Shift+Tab still move on (so it never traps the keyboard) and Esc cancels.
/// </summary>
public sealed class KeyCaptureBox : Border
{
    private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };

    /// <summary>The last key recorded (null until one is).</summary>
    public KeyChord? Chord { get; private set; }

    /// <summary>A key was recorded (it may still be one that can't be a shortcut: ask <see cref="KeyChord.Problem"/>).</summary>
    public event Action<KeyChord>? Captured;
    /// <summary>Esc was pressed.</summary>
    public event Action? Cancelled;

    public KeyCaptureBox()
    {
        Focusable = true;
        Cursor = Cursors.IBeam;
        MinWidth = 240;
        MinHeight = 30;
        Padding = new Thickness(10, 4, 10, 4);
        BorderThickness = new Thickness(1);
        SetResourceReference(BorderBrushProperty, "Accent");
        SetResourceReference(BackgroundProperty, "Surface");
        _text.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        Child = _text;
        AutomationProperties.SetName(this, "Press the new shortcut keys");
        ToolTip = "Press the keys for this command. Esc cancels; Tab moves on.";
        Prompt();
        PreviewKeyDown += OnKey;
        MouseLeftButtonDown += (_, e) => { Focus(); e.Handled = true; };
        GotKeyboardFocus += (_, _) => { if (Chord == null) Prompt(); };
    }

    private void Prompt() => _text.Text = "Press the keys…";

    private void OnKey(object sender, KeyEventArgs e)
    {
        var key = e.Key switch { Key.System => e.SystemKey, Key.ImeProcessed => e.ImeProcessedKey, Key.DeadCharProcessed => e.DeadCharProcessedKey, _ => e.Key };
        var mods = Keyboard.Modifiers;
        // Tab and Shift+Tab leave the box; Esc cancels. Anything else is recorded (and not passed on).
        if (key == Key.Tab && (mods & (ModifierKeys.Control | ModifierKeys.Alt)) == 0) return;
        e.Handled = true;
        if (key == Key.Escape && mods == ModifierKeys.None) { Cancelled?.Invoke(); return; }
        Press(key, mods);
    }

    /// <summary>What a key press does (the self-test presses keys through here).</summary>
    public void Press(Key key, ModifierKeys mods)
    {
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            var held = new KeyChord(Key.None, mods & KeyChord.Supported);
            _text.Text = (held.Ctrl ? "Ctrl+" : "") + (held.Alt ? "Alt+" : "") + (held.Shift ? "Shift+" : "") + "…";
            return;
        }
        if (KeyChord.From(key, mods) is not { } chord)
        {
            _text.Text = (mods & ModifierKeys.Windows) != 0 ? "The Windows key belongs to Windows" : "That key can't be a shortcut";
            return;
        }
        Chord = chord;
        _text.Text = chord.Shown;
        Captured?.Invoke(chord);
    }

    /// <summary>Empties the box for another try.</summary>
    public void Clear() { Chord = null; Prompt(); }
}
