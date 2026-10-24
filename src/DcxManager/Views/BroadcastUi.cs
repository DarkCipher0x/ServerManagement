using System.Windows;
using System.Windows.Controls;
using DcxManager.Api;
using DcxManager.Services;

namespace DcxManager.Views;

/// <summary>"📡 Broadcast to OBS…" (ADR-011): options → explicit start → the URL for an OBS Browser Source.</summary>
public static class BroadcastUi
{
    public static void Start(IBroadcastSource source, string kind)
    {
        if (Broadcasts.For(source.Owner) is { } running)
        {
            if (Dialogs.Confirm("Broadcast", $"“{source.Title}” is already being broadcast (until {running.Expires:HH:mm}).\n\n{running.Url}", "Stop it")) Broadcasts.Stop(running, "stopped by you");
            return;
        }
        var o = Ask(source.Title, kind);
        if (o == null) return;
        try
        {
            var b = Broadcasts.Start(source, kind, o);
            ShowUrl(b);
        }
        catch (Exception ex) { Dialogs.Error("Broadcast", ex.Message, explain: false); }
    }

    private static BroadcastOptions? Ask(string title, string kind)
    {
        var w = Dialogs.MakeWindow("Broadcast to OBS", 620);
        var p = new StackPanel { Margin = new Thickness(18) };
        var head = new TextBlock { Text = $"📡 Broadcast “{title}”", FontWeight = FontWeights.Bold }.Fs(FontStep.Title);
        head.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        p.Children.Add(head);
        p.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 10),
            Text = (kind == "terminal"
                ? "The rows on this terminal's screen are published as TEXT (no colours) to a private address on this PC (127.0.0.1) for an OBS Browser Source. "
                : "A status card (title, status, severity, duration and the latest entry titles — never their contents) is published to a private address on this PC (127.0.0.1) for an OBS Browser Source. ") +
                   "Secrets, private keys and key-like strings are always masked. Nothing is sent anywhere; OBS fetches it. A red BROADCASTING badge stays visible until you stop it, it expires, the tab closes, or the app locks.",
        });
        var theme = new ComboBox { Width = 150, ItemsSource = new[] { "dark", "light", "transparent" }, SelectedIndex = 0 };
        var font = new ComboBox { Width = 90, ItemsSource = new[] { 14, 16, 18, 22, 26, 32 }, SelectedItem = 18 };
        var rows = new ComboBox { Width = 130, ItemsSource = new[] { "all visible rows", "last 10", "last 20", "last 30", "last 40" }, SelectedIndex = 0 };
        var minutes = new ComboBox { Width = 130, ItemsSource = new[] { "30 minutes", "1 hour", "2 hours", "4 hours", "8 hours" }, SelectedIndex = 3 };
        var row1 = new WrapPanel();
        StackPanel L(string label, FrameworkElement e) { var s = new StackPanel { Margin = new Thickness(0, 0, 10, 6) }; s.Children.Add(new TextBlock { Text = label, Style = (Style)Application.Current.Resources["Label"], Margin = new Thickness(0, 0, 0, 3) }); s.Children.Add(e); return s; }
        row1.Children.Add(L("THEME", theme)); row1.Children.Add(L("TEXT SIZE (px)", font));
        if (kind == "terminal") row1.Children.Add(L("CROP", rows));
        row1.Children.Add(L("STOP AFTER", minutes));
        p.Children.Add(row1);
        var showTitle = new CheckBox { Content = "Show the title bar", IsChecked = true, Margin = new Thickness(0, 4, 0, 0) };
        var ips = new CheckBox { Content = "Mask IP addresses", IsChecked = kind == "terminal", Margin = new Thickness(0, 4, 0, 0) };
        var mails = new CheckBox { Content = "Mask e-mail addresses", IsChecked = true, Margin = new Thickness(0, 4, 0, 0) };
        var prompts = new CheckBox { Content = "Hide the screen while a password / passphrase / sudo / verification-code prompt is showing (freezes on the last safe frame)", IsChecked = true, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var c in new[] { showTitle, ips, mails, prompts }) p.Children.Add(c);
        prompts.Visibility = kind == "terminal" ? Visibility.Visible : Visibility.Collapsed;
        p.Children.Add(new TextBlock { Text = "EXTRA REDACTION PATTERNS (one regular expression per line; matches become [REDACTED])", Style = (Style)Application.Current.Resources["Label"], Margin = new Thickness(0, 10, 0, 3) });
        var patterns = new TextBox { AcceptsReturn = true, Height = 64, Padding = new Thickness(5, 3, 5, 3), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, ToolTip = "e.g. prod-db-\\d+   or   (?i)customer\\s+\\w+" }.Mono();
        p.Children.Add(patterns);
        var start = new Button { Content = "📡 Start broadcast", Style = (Style)Application.Current.Resources["PrimaryButton"], IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        var bs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        bs.Children.Add(start); bs.Children.Add(cancel);
        p.Children.Add(bs);
        w.Content = p;
        start.Click += (_, _) => w.DialogResult = true;
        if (w.ShowDialog() != true) return null;
        return new BroadcastOptions
        {
            Theme = (string)theme.SelectedItem, FontPx = (int)font.SelectedItem,
            Rows = kind == "terminal" ? new[] { 0, 10, 20, 30, 40 }[Math.Max(0, rows.SelectedIndex)] : 0,
            Minutes = new[] { 30, 60, 120, 240, 480 }[Math.Max(0, minutes.SelectedIndex)],
            ShowTitle = showTitle.IsChecked == true, MaskIps = ips.IsChecked == true, MaskEmails = mails.IsChecked == true,
            HidePasswordPrompts = prompts.IsChecked == true,
            Patterns = patterns.Text.Replace("\r", "").Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToList(),
        };
    }

    private static void ShowUrl(Broadcast b)
    {
        var w = Dialogs.MakeWindow("Broadcasting", 600);
        var p = new StackPanel { Margin = new Thickness(18) };
        p.Children.Add(new TextBlock { Text = $"📡 Broadcasting “{b.Title}” until {b.Expires:HH:mm}", FontWeight = FontWeights.Bold });
        p.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 6), Text = "In OBS: Sources → ＋ → Browser → URL below (width/height to taste). The address only works on this PC and stops working when the broadcast ends. Treat it like a password while it runs: anyone on this PC who has it can watch." });
        var url = new TextBox { Text = b.Url, IsReadOnly = true, Padding = new Thickness(5, 4, 5, 4) }.Mono();
        p.Children.Add(url);
        var bs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var copy = new Button { Content = "Copy URL", Style = (Style)Application.Current.Resources["PrimaryButton"] };
        copy.Click += (_, _) => Menus.Clip(b.Url);
        var stop = new Button { Content = "■ Stop now", Margin = new Thickness(8, 0, 0, 0) };
        stop.Click += (_, _) => { Broadcasts.Stop(b, "stopped by you"); w.Close(); };
        var ok = new Button { Content = "Close", IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        bs.Children.Add(copy); bs.Children.Add(stop); bs.Children.Add(ok);
        p.Children.Add(bs);
        w.Content = p;
        w.ShowDialog();
    }

    /// <summary>The "📡" menu item for a terminal.</summary>
    public static MenuItem TerminalItem(TerminalView tv) =>
        Broadcasts.For(tv) is { } b
            ? Menus.Item($"■ Stop broadcast (until {b.Expires:HH:mm})", () => Broadcasts.Stop(b, "stopped by you"))
            : Menus.Item("📡 Broadcast to OBS…", () => Start(new TerminalSource(tv), "terminal"), tip: "Publish this screen as redacted text for an OBS Browser Source (this PC only)");
}
