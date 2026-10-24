using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using DcxManager.Services;

namespace DcxManager.Views;

/// <summary>
/// LOGS → Error log (0.5.1 #8): the app's own error log for the last days, decrypted for viewing only (each entry is sealed
/// on disk), with a filter, refresh, the folder and "Explain with AI". It sits in LOGS beside the Audit log and Session logs
/// and can be locked like them (Settings → Security & privacy → Locks; Group Policy LockedCategories "errorlog").
/// </summary>
public sealed class ErrorLogView : UserControl, IActivatable
{
    private readonly TextBox _text = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBox _filter = Hint.Set(new TextBox { Width = 220, Padding = new Thickness(5, 4, 5, 4) }, "Filter lines…");
    private readonly ComboBox _days = new() { Width = 120, ItemsSource = new[] { "Last day", "Last 7 days", "Last 14 days", "Last 30 days" }, SelectedIndex = 2 };
    private readonly TextBlock _count = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
    private string _all = "";

    public ErrorLogView()
    {
        var root = new DockPanel { Margin = new Thickness(14, 12, 14, 12) };
        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        head.Children.Add(new TextBlock { Text = "Error log", Style = (Style)FindResource("H1") });
        head.Children.Add(new TextBlock { Text = "What went wrong inside the app. Each entry is encrypted on disk and decrypted here only for viewing; secrets are masked before anything is written.", Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap });
        var bar = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        Button B(string t, Action a, string? style = "SmallButton") { var b = new Button { Content = t, Style = (Style)FindResource(style ?? "SmallButton"), Margin = new Thickness(0, 0, 6, 0) }; b.Click += (_, _) => a(); return b; }
        bar.Children.Add(_days); _days.Margin = new Thickness(0, 0, 6, 0);
        bar.Children.Add(_filter); _filter.Margin = new Thickness(0, 0, 6, 0);
        bar.Children.Add(B("↻ Refresh", Load, "PrimaryButton"));
        bar.Children.Add(B("🗀 Open folder", () => { Directory.CreateDirectory(App.LogDir); Process.Start(new ProcessStartInfo(App.LogDir) { UseShellExecute = true }); }));
        bar.Children.Add(B("Copy", () => Menus.Clip(_text.Text)));
        if (Assist.Current != null) bar.Children.Add(B("✦ Explain with AI", () => _ = Assist.Current?.AskAsync("Error log", Last(6000), "Explain these application errors: likely causes, whether they matter, and what to check. Advisory only.")));
        _count.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        bar.Children.Add(_count);
        head.Children.Add(bar);
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);
        _text.Mono();
        root.Children.Add(_text);
        Content = root;
        _days.SelectionChanged += (_, _) => Load();
        _filter.TextChanged += (_, _) => Apply();
        Loaded += (_, _) => Load();
    }

    public void Activated() => Load();

    private int Days => _days.SelectedIndex switch { 0 => 1, 1 => 7, 3 => 30, _ => 14 };

    private void Load()
    {
        _all = App.ReadLogs(days: Days);
        Apply();
    }

    private void Apply()
    {
        var f = _filter.Text.Trim();
        var lines = _all.Split('\n');
        var shown = f.Length == 0 ? lines : lines.Where(l => l.Contains(f, StringComparison.OrdinalIgnoreCase)).ToArray();
        _text.Text = shown.Length == 0 || _all.Trim().Length == 0 ? "No errors recorded. 👍" : string.Join("\n", shown);
        _count.Text = $"{lines.Count(l => l.StartsWith('['))} entr(ies) in {Days} day(s)" + (f.Length > 0 ? $" · {shown.Length} line(s) match" : "");
        _text.ScrollToEnd();
    }

    private string Last(int chars) => _text.Text.Length <= chars ? _text.Text : _text.Text[^chars..];
}
