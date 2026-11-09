using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DcxManager.Services;
using DcxManager.Views;

namespace DcxManager.Chat.Views;

/// <summary>
/// A small dialog for the chat screen (0.6.41), the same shape as CipherChat's own (Dialogs.New in CipherChat.Ui): text,
/// labelled fields, any control, and buttons that each close it with a value (a button returning <c>false</c> keeps it
/// open, so a form can refuse an empty name). It is the app's own window (theme, scaling, owner). In the self-test it is
/// never shown: <see cref="New"/> records the refusal and returns null, as every dialog in the app does.
/// </summary>
internal sealed class ChatForm
{
    private readonly Window _w;
    private readonly StackPanel _body = new() { Margin = new Thickness(18, 16, 18, 0) };
    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(18, 14, 18, 16) };
    private object? _result;

    private ChatForm(string title, double width)
    {
        _w = Dialogs.MakeWindow(title, width);
        var root = new DockPanel();
        DockPanel.SetDock(_buttons, Dock.Bottom);
        root.Children.Add(_buttons);
        root.Children.Add(_body);
        _w.Content = root;
        _w.KeyDown += (_, e) => { if (e.Key == Key.Escape) { _result = null; _w.Close(); } };
    }

    /// <summary>A new dialog, or null in the self-test (which never shows a window).</summary>
    public static ChatForm? New(string title, double width = 480)
    {
        if (TestMode.Running) { Dialogs.RefusedInSelfTest("chat dialog: " + title); return null; }
        return new ChatForm(title, width);
    }

    public TextBlock Text(string text, string? style = null)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        if (style is not null) t.SetResourceReference(FrameworkElement.StyleProperty, style);
        if (style == "Label") t.Margin = new Thickness(0, 4, 0, 4);
        _body.Children.Add(t);
        return t;
    }

    public TextBox Field(string label, string value, int max)
    {
        Text(label, "Label");
        var t = new TextBox { Text = value, MaxLength = max, Margin = new Thickness(0, 0, 0, 10) };
        System.Windows.Automation.AutomationProperties.SetName(t, label);
        _body.Children.Add(t);
        return t;
    }

    public T Add<T>(T el) where T : UIElement { _body.Children.Add(el); return el; }

    /// <summary>A button: its function's value closes the dialog with it, except <c>false</c>, which keeps it open.</summary>
    public Button Button(string text, Func<object?> click, string? style = null, bool isDefault = false)
    {
        var b = new Button { Content = text, MinWidth = 90, Margin = new Thickness(6, 0, 0, 0), IsDefault = isDefault };
        if (style is not null) b.SetResourceReference(FrameworkElement.StyleProperty, style);
        b.Click += (_, _) =>
        {
            var v = click();
            if (v is false) return;
            _result = v;
            _w.Close();
        };
        _buttons.Children.Add(b);
        return b;
    }

    public Button Cancel(string text = "Cancel")
    {
        var b = Button(text, () => null);
        b.IsCancel = true;
        return b;
    }

    /// <summary>Shows it (modal) and returns the value of the button that closed it (null: cancelled or closed).</summary>
    public object? Show(IInputElement? focus = null)
    {
        if (focus is not null) _w.Loaded += (_, _) => focus.Focus();
        _w.ShowDialog();
        return _result;
    }

    // ------------------------------------------------------------------------------------------- shared dialogs

    /// <summary>One of your other devices asks for the room keys this PC holds (CipherChat's history sync): approve only
    /// when it's yours and shows the same code. Null = not now. Never shown in the self-test (null).</summary>
    public static bool? ConfirmSync(string name, DateTimeOffset added, string code)
    {
        if (New("Send history to a new device?") is not { } d) return null;
        d.Text("\"" + ChatRoomView.Clean(name) + "\" (added " + added.ToLocalTime().ToString("g") + ") is asking for the room keys this PC holds, so it can read older messages.");
        d.Text("Approve only if it's your own device and it shows this code:", "Caption");
        var c = d.Text(ChatRoomView.Clean(code));
        c.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        c.FontSize = 18;
        d.Text("Rooms whose admins turned history sync off aren't included, and nothing older than each room's history window.", "Caption");
        d.Button("Decline", () => "no", "DangerButton");
        d.Cancel("Not now");
        d.Button("Approve", () => "yes", "PrimaryButton");
        var r = d.Show() switch { "yes" => (bool?)true, "no" => false, _ => null };
        if (r is not null) AuditLog.Write("you", "chat", r == true ? "history sent to a new device" : "history request declined", ChatRoomView.Clean(name));
        return r;
    }
}
