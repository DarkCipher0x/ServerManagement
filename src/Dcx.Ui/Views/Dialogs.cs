using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DcxManager.Services;

namespace DcxManager.Views;

/// <summary>Small themed dialogs built in code: text prompt, confirmation, read-only text viewer.</summary>
public static class Dialogs
{
    public static Window Owner => Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current.MainWindow!;

    public static Window MakeWindow(string title, double width, double? height = null)
    {
        var w = new Window
        {
            Title = title, SizeToContent = height == null ? SizeToContent.Height : SizeToContent.Manual,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = height == null ? ResizeMode.NoResize : ResizeMode.CanResize, ShowInTaskbar = false,
        }.Fs(FontStep.Base);
        Typography.Size(w, width, height);
        w.Loaded += (_, _) =>
        {
            if (w.Content is not FrameworkElement fe) return;
            // Size-to-content dialogs are capped to the screen (Typography.Size), so their body scrolls instead of being cut off.
            if (height == null && fe is not ScrollViewer) { w.Content = null; w.Content = new ScrollViewer { Content = fe, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; }
            ThemeManager.TrackScale(w, fe);
        };
        w.SetResourceReference(Control.BackgroundProperty, "Bg");
        w.SetResourceReference(Control.ForegroundProperty, "Fg");
        try { w.Owner = Owner; } catch { }
        if (w.Owner == null) w.WindowStartupLocation = WindowStartupLocation.CenterScreen;   // before the main window exists (start-up)
        w.SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(w);
        return w;
    }

    private static StackPanel Buttons(params Button[] buttons)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        foreach (var b in buttons) p.Children.Add(b);
        return p;
    }

    internal static Button Btn(string text, bool primary = false, bool danger = false)
    {
        var b = new Button { Content = text, MinWidth = 90 };
        if (primary) b.Style = (Style)Application.Current.Resources["PrimaryButton"];
        if (danger) b.Style = (Style)Application.Current.Resources["DangerButton"];
        return b;
    }

    /// <summary>Prompts refused during the self-test (it must never put a window on screen); the report fails on any.</summary>
    public static List<string> SelfTestPrompts { get; } = new();

    /// <summary>Records a dialog the self-test refused to show; the report fails on any (see SelfTest).</summary>
    public static void RefusedInSelfTest(string what) { lock (SelfTestPrompts) SelfTestPrompts.Add(what); }

    public static string? Prompt(string title, string message, string initial = "", bool password = false, bool multiline = false)
    {
        if (TestMode.Running) { RefusedInSelfTest("prompt: " + title); return null; }
        var w = MakeWindow(title, multiline ? 620 : 440);
        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        Control input;
        Func<string> get;
        if (password)
        {
            var pb = new PasswordBox { Password = initial };
            input = pb; get = () => pb.Password;
        }
        else
        {
            var tb = new TextBox { Text = initial, Padding = new Thickness(5, 4, 5, 4) };
            if (multiline)
            {
                tb.AcceptsReturn = true; tb.Height = 220; tb.TextWrapping = TextWrapping.Wrap;
                tb.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                tb.Mono();
            }
            input = tb; get = () => tb.Text;
        }
        root.Children.Add(input);
        var ok = Btn("OK", primary: true); ok.IsDefault = true;
        var cancel = Btn("Cancel"); cancel.IsCancel = true;
        ok.Click += (_, _) => w.DialogResult = true;
        root.Children.Add(Buttons(ok, cancel));
        w.Content = root;
        w.Loaded += (_, _) => { input.Focus(); if (input is TextBox t) t.SelectAll(); };
        return w.ShowDialog() == true ? get() : null;
    }

    /// <summary>A password box with one tick box under it (0.6.20: Bitwarden's "Remember on this PC"). Null text = cancelled.</summary>
    public static (string? Text, bool Option) PromptPassword(string title, string message, string option, bool optionDefault = false)
    {
        if (TestMode.Running) { RefusedInSelfTest("prompt: " + title); return (null, false); }
        var w = MakeWindow(title, 480);
        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        var pb = new PasswordBox();
        root.Children.Add(pb);
        var tick = new CheckBox { Content = new TextBlock { Text = option, TextWrapping = TextWrapping.Wrap }, IsChecked = optionDefault, Margin = new Thickness(0, 10, 0, 0) };
        root.Children.Add(tick);
        var ok = Btn("OK", primary: true); ok.IsDefault = true;
        var cancel = Btn("Cancel"); cancel.IsCancel = true;
        ok.Click += (_, _) => w.DialogResult = true;
        root.Children.Add(Buttons(ok, cancel));
        w.Content = root;
        w.Loaded += (_, _) => pb.Focus();
        return w.ShowDialog() == true ? (pb.Password, tick.IsChecked == true) : (null, false);
    }

    /// <summary>Self-test only: when set, <see cref="Confirm"/> records the question instead of showing it and returns this
    /// answer (the self-test never opens windows on the user's screen).</summary>
    public static bool? ConfirmForTests { get; set; }
    public static List<(string Title, string Message, bool Danger, string? TypeToConfirm, string? Env)> SelfTestConfirms { get; } = new();

    /// <summary>Whether what was typed confirms <paramref name="expected"/> (a host name, PRODUCTION, RUN): spaces around it
    /// and letter case don't matter, as they don't for host names (0.6.10).</summary>
    public static bool TypedMatches(string typed, string expected) =>
        expected.Trim().Length > 0 && string.Equals(typed.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Asks before something changes. Typed confirmation (typeToConfirm) for the really destructive ones.</summary>
    public static bool Confirm(string title, string message, string action = "Continue", bool danger = false, string? typeToConfirm = null, (string Env, string Target)? banner = null)
    {
        if (!danger && !AppSettings.Current.ConfirmDestructive && typeToConfirm == null) return true;
        if (TestMode.Running && ConfirmForTests is { } answer) { lock (SelfTestConfirms) SelfTestConfirms.Add((title, message, danger, typeToConfirm, banner?.Env)); return answer; }
        if (TestMode.Running) { RefusedInSelfTest("confirmation: " + title); return false; }
        var w = MakeWindow(title, 480);
        var root = new StackPanel { Margin = new Thickness(18) };
        // Environment banner (ADR-017): the colour AND the words, so it reads without colour too.
        if (banner is { } bn)
        {
            var pill = EnvUi.Pill(bn.Env, bn.Target);
            pill.HorizontalAlignment = HorizontalAlignment.Left;
            pill.Margin = new Thickness(0, 0, 0, 10);
            root.Children.Add(pill);
        }
        root.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        TextBox? typed = null;
        var ok = Btn(action, primary: !danger, danger: danger);
        if (typeToConfirm != null)
        {
            root.Children.Add(new TextBlock { Text = $"Type \"{typeToConfirm}\" to confirm, then press Enter or {action}:", Margin = new Thickness(0, 12, 0, 4), Foreground = (Brush)Application.Current.Resources["Muted"] });
            typed = new TextBox { Padding = new Thickness(5, 4, 5, 4) };
            var hint = new TextBlock { Margin = new Thickness(0, 4, 0, 0) }.Fs(FontStep.Small);
            hint.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            typed.TextChanged += (_, _) =>
            {
                ok.IsEnabled = TypedMatches(typed.Text, typeToConfirm);
                hint.Text = typed.Text.Trim().Length == 0 ? "" : ok.IsEnabled ? "✓ matches" : "doesn't match yet";
            };
            // 0.6.10 (DarkCipher0x: "when i put in the name of the server like it asks and approve... it says i declined"):
            // Enter in the box used to hit the default button, Cancel. Now Enter confirms once the name matches (typing it
            // is the safeguard) and otherwise does nothing; it never cancels. Esc and Cancel still cancel.
            typed.PreviewKeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                if (ok.IsEnabled) w.DialogResult = true;
            };
            ok.IsEnabled = false;
            root.Children.Add(typed);
            root.Children.Add(hint);
        }
        var cancel = Btn("Cancel"); cancel.IsCancel = true; cancel.IsDefault = danger && typeToConfirm == null;
        ok.IsDefault = !danger;
        ok.Click += (_, _) => w.DialogResult = true;
        root.Children.Add(Buttons(ok, cancel));
        w.Content = root;
        w.Loaded += (_, _) => (typed ?? (Control)cancel).Focus();
        return w.ShowDialog() == true;
    }

    /// <summary>A message with several labelled choices. Returns the chosen index, or -1 when the window is closed.
    /// The first option is the primary (highlighted) one; Esc picks <paramref name="cancelIndex"/> when given.</summary>
    public static int Choose(string title, string message, string[] options, int cancelIndex = -1, double width = 560)
    {
        if (TestMode.Running) { RefusedInSelfTest("choice: " + title); return cancelIndex; }
        var w = MakeWindow(title, width);
        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        int result = -1;
        var p = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        for (int i = 0; i < options.Length; i++)
        {
            int idx = i;
            var b = Btn(options[i], primary: i == 0);
            b.Margin = new Thickness(6, 4, 0, 0);
            if (i == 0) b.IsDefault = true;
            b.Click += (_, _) => { result = idx; w.Close(); };
            p.Children.Add(b);
        }
        w.KeyDown += (_, e) => { if (e.Key == Key.Escape) { result = cancelIndex; w.Close(); } };
        root.Children.Add(p);
        w.Content = root;
        w.ShowDialog();
        return result;
    }

    public static void Info(string title, string message)
    {
        var app = Application.Current;
        if (app == null) return;
        if (!app.Dispatcher.CheckAccess()) { app.Dispatcher.BeginInvoke(() => Info(title, message)); return; }
        if (TestMode.Running) { RefusedInSelfTest("info: " + title + " — " + message); return; }
        Box(title, message, "ⓘ", "Accent", null);
    }

    /// <summary>The error box's extra button, if a module offers one for this error: the app registers how to ask the
    /// modules (<c>AppModules.ErrorActionFor</c>); none by default.</summary>
    public static Func<string, string, Exception?, ErrorBoxAction?> ErrorAction { get; set; } = (_, _, _) => null;

    /// <summary>A warning with OK and — when a module offers one (the AI's "✦ Explain this error with AI") — an extra
    /// action button. Nothing happens unless that button is clicked (then the dialog closes and the action runs).</summary>
    public static void Error(string title, string message, bool explain = true, Exception? ex = null)
    {
        var app = Application.Current;
        if (app == null) return;
        // Never block a background thread on the UI (a busy or looping UI thread would deadlock it): queue the box instead.
        if (!app.Dispatcher.CheckAccess()) { app.Dispatcher.BeginInvoke(() => Error(title, message, explain, ex)); return; }
        if (TestMode.Running) { RefusedInSelfTest("error: " + title + " — " + message); return; }
        var action = explain && SecureStore.IsUnlocked && app.MainWindow is { IsLoaded: true } ? ErrorAction(title, message, ex) : null;
        Box(title, message, "⚠", "Warn", action);
    }

    /// <summary>The themed message box behind <see cref="Info"/> and <see cref="Error"/> (0.4.0): the system MessageBox
    /// followed Windows' light theme and flashed white over the dark app; this one is the app's own window in its colours,
    /// cleared to the theme background before its first paint (ThemeManager.ApplyTitleBar). Only when the application has
    /// no resources yet (nothing themed can be drawn) does the system box remain.</summary>
    private static void Box(string title, string message, string glyph, string glyphBrush, ErrorBoxAction? action)
    {
        var app = Application.Current;
        if (app?.Resources["PrimaryButton"] == null)
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, glyph == "⚠" ? MessageBoxImage.Warning : MessageBoxImage.Information);
            return;
        }
        var w = MakeWindow(title, 520);
        var root = new StackPanel { Margin = new Thickness(18) };
        var body = new DockPanel();
        var icon = new TextBlock { Text = glyph, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Top }.Fs(FontStep.Icon);
        icon.SetResourceReference(TextBlock.ForegroundProperty, glyphBrush);
        DockPanel.SetDock(icon, Dock.Left);
        body.Children.Add(icon);
        var text = new TextBox { Text = message, IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent, TextWrapping = TextWrapping.Wrap, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        text.SetResourceReference(Control.ForegroundProperty, "Fg");
        body.Children.Add(text);
        root.Children.Add(body);
        var ok = Btn("OK", primary: true); ok.IsDefault = true; ok.IsCancel = true;
        ok.Click += (_, _) => w.Close();
        if (action != null)
        {
            var ai = Btn(action.Label);
            ai.ToolTip = action.Tip;
            ai.Margin = new Thickness(0, 0, 8, 0);
            ai.Click += (_, _) =>
            {
                w.Close();
                app.Dispatcher.BeginInvoke(async () =>
                {
                    try { await action.Run(); }
                    catch (Exception e2) { Log.Write("Error-box action", e2); }
                });
            };
            root.Children.Add(Buttons(ai, ok));
        }
        else root.Children.Add(Buttons(ok));
        w.Content = root;
        w.KeyDown += (_, e) => { if (e.Key == Key.Escape) w.Close(); };
        w.Loaded += (_, _) => ok.Focus();
        w.ShowDialog();
    }

    public static void ShowText(string title, string text, bool modal = false)
    {
        var w = MakeWindow(title, 900, 600);
        w.ShowInTaskbar = true;
        var tb = new TextBox { Text = text, Style = (Style)Application.Current.Resources["Mono"], Margin = new Thickness(10) };
        w.Content = tb;
        w.KeyDown += (_, e) => { if (e.Key == Key.Escape) w.Close(); };
        if (modal) w.ShowDialog(); else w.Show();
    }

    /// <summary>Pick one item from a list (e.g. a host, a backup, an ISO).</summary>
    public static T? Pick<T>(string title, string message, IEnumerable<T> items, Func<T, string> label) where T : class
    {
        var w = MakeWindow(title, 520);
        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        var list = new ListBox { Height = 280 };
        foreach (var i in items) list.Items.Add(new ListBoxItem { Content = label(i), Tag = i, Padding = new Thickness(4, 3, 4, 3) });
        if (list.Items.Count > 0) list.SelectedIndex = 0;
        root.Children.Add(list);
        var ok = Btn("Select", primary: true); ok.IsDefault = true;
        var cancel = Btn("Cancel"); cancel.IsCancel = true;
        ok.Click += (_, _) => w.DialogResult = list.SelectedItem != null;
        list.MouseDoubleClick += (_, _) => { if (list.SelectedItem != null) w.DialogResult = true; };
        root.Children.Add(Buttons(ok, cancel));
        w.Content = root;
        return w.ShowDialog() == true ? (list.SelectedItem as ListBoxItem)?.Tag as T : null;
    }
}

/// <summary>A button in the error box: its label, tooltip and what it does after the box closes.</summary>
public sealed record ErrorBoxAction(string Label, string Tip, Func<Task> Run);
