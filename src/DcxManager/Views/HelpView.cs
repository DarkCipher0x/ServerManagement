using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DcxManager.Services;

namespace DcxManager.Views;

/// <summary>Help & FAQ: instant offline search over the built-in articles, plus an optional grounded AI answer.</summary>
public sealed class HelpView : UserControl, IDisposable
{
    public static HelpView? Current { get; private set; }

    private readonly TextBox _q = Hint.Set(new TextBox() { Padding = new Thickness(8, 6, 8, 6) }.Fs(FontStep.Input), "Search the help — e.g. “split the screen”…");
    private readonly Button _ask = new() { Content = "Ask", MinWidth = 80 };
    private readonly CheckBox _useAi = new() { Content = "AI answer", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
    private readonly ListBox _results = new();
    private readonly TextBox _answer = new TextBox() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = Brushes.Transparent }.Fs(FontStep.Base);
    private readonly TextBox _article = new TextBox() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = Brushes.Transparent, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }.Fs(FontStep.Control);
    private readonly TextBlock _status = new TextBlock() { TextWrapping = TextWrapping.Wrap }.Fs(FontStep.Small);
    private readonly Border _answerCard;
    private CancellationTokenSource? _cts;
    private string? _screen;

    public HelpView()
    {
        Current = this;
        _ask.Style = (Style)FindResource("PrimaryButton");
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");

        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        head.Children.Add(new TextBlock { Text = "Help & FAQ", Style = (Style)FindResource("H1") });
        bool assistant = Assist.Current != null;   // the AI answer exists only with an assistant module
        head.Children.Add(new TextBlock { Text = "Search works offline on this PC." + (assistant ? " With an AI engine on, you also get a short answer grounded in these articles." : ""), Style = (Style)FindResource("Hint") });
        var row = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(_useAi, Dock.Right); DockPanel.SetDock(_ask, Dock.Right);
        _ask.Margin = new Thickness(8, 0, 0, 0);
        if (assistant) row.Children.Add(_useAi);
        else _useAi.IsChecked = false;
        row.Children.Add(_ask); row.Children.Add(_q);
        head.Children.Add(row);
        head.Children.Add(_status);
        var chips = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        var questions = assistant
            ? new[] { "How do I split the screen?", "Which model fits my laptop?", "How do reports work without AI?", "Use the NPU on a Copilot+ PC", "Run Claude Code locally", "What is sent to the AI?" }
            : new[] { "How do I split the screen?", "How do reports work?", "Offline mode", "Back up my data" };
        foreach (var s in questions)
        {
            var b = new Button { Content = s, Style = (Style)FindResource("SmallButton"), Margin = new Thickness(0, 0, 6, 6) };
            b.Click += (_, _) => { _q.Text = s; _ = AskAsync(); };
            chips.Children.Add(b);
        }
        head.Children.Add(chips);

        var answerPanel = new StackPanel();
        answerPanel.Children.Add(new TextBlock { Text = "✦ ANSWER", Style = (Style)FindResource("Label") });
        answerPanel.Children.Add(_answer);
        _answerCard = new Border { Style = (Style)FindResource("Card"), Child = answerPanel, Margin = new Thickness(0, 0, 0, 10), Visibility = Visibility.Collapsed };

        _results.DisplayMemberPath = "Title";
        _results.SelectionChanged += (_, _) => { if (_results.SelectedItem is HelpArticle a) _article.Text = a.Title.ToUpperInvariant() + "\n\n" + a.Body; };
        Menus.SelectOnRightClick(_results);
        Menus.Attach(_results, () => _results.SelectedItem is HelpArticle a ? new object?[]
        {
            Menus.Item("Copy article", () => Menus.Clip(a.Title + "\n\n" + a.Body)),
            Menus.Item("Copy title", () => Menus.Clip(a.Title)),
            Menus.AssistItem("✦ Ask AI to explain this further", _ => { _q.Text = "Explain in more detail: " + a.Title; return AskAsync(); }, use: AssistUse.Helper),
        } : Array.Empty<object?>());
        Menus.ForText(_answer);
        Menus.ForText(_article);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280), MinWidth = 180 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var right = new DockPanel { Margin = new Thickness(12, 0, 0, 0) };
        DockPanel.SetDock(_answerCard, Dock.Top);
        if (assistant) right.Children.Add(_answerCard);   // the AI answer card exists only with an assistant
        var art = new Border { Style = (Style)FindResource("Card"), Child = _article };
        right.Children.Add(art);
        var split = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        Grid.SetColumn(_results, 0); Grid.SetColumn(split, 1); Grid.SetColumn(right, 2);
        grid.Children.Add(_results); grid.Children.Add(split); grid.Children.Add(right);

        var root = new DockPanel { Margin = new Thickness(14, 12, 14, 12) };
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head); root.Children.Add(grid);
        Content = root;

        _q.TextChanged += (_, _) => Search();
        _q.PreviewKeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await AskAsync(); } };
        _ask.Click += async (_, _) => await AskAsync();
        Loaded += (_, _) => { RefreshAi(); _q.Focus(); };
        Search();
    }

    /// <summary>Opens help pre-filled (F1 passes the current screen's title).</summary>
    public void Prefill(string? screen, string? query = null)
    {
        _screen = screen;
        if (query != null) { _q.Text = query; _q.CaretIndex = query.Length; }
        else if (screen != null && _q.Text.Length == 0) { _q.Text = screen.TrimStart('◈', '▣', '⌨', '⇅', '⚡', '⇄', '⌕', '⚿', '✓', '✦', '☰', '⚙', '◍', ' ', '{', '}', '⧉', '?'); }
        RefreshAi();
        _q.Focus();
        _q.SelectAll();
    }

    private void RefreshAi()
    {
        if (Assist.Current is not { } assist) return;
        var why = assist.WhyNot(AssistUse.Helper);
        _useAi.IsEnabled = why == null;
        if (why != null) _useAi.IsChecked = false;
        _useAi.ToolTip = why ?? assist.EngineLine;
    }

    private void Search()
    {
        var hits = HelpKb.Search(_q.Text, 12);
        _results.ItemsSource = hits.Select(h => h.Article).ToList();
        if (_results.Items.Count > 0) _results.SelectedIndex = 0;
        _status.Text = _q.Text.Trim().Length == 0 ? $"{HelpKb.Articles.Count} articles" : $"{hits.Count} matching article(s) · offline search";
    }

    private async Task AskAsync()
    {
        Search();
        if (Assist.Current is not { } assist || _useAi.IsChecked != true || assist.WhyNot(AssistUse.Helper) != null || _q.Text.Trim().Length == 0) { _answerCard.Visibility = Visibility.Collapsed; return; }
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        _answerCard.Visibility = Visibility.Visible;
        _answer.Text = "…thinking";
        try
        {
            var a = await assist.AnswerHelpAsync(_q.Text.Trim(), _screen, cts.Token);
            if (cts.IsCancellationRequested) return;
            _answer.Text = a.Length > 0 ? a : "(no answer — see the articles below)";
            _status.Text = $"answer by {assist.ModelName} in {assist.LastLatency.TotalSeconds:0.0}s · grounded in the top articles";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _answer.Text = "AI unavailable — the matching articles are below.\n" + ex.Message.Split('\n')[0]; }
    }

    public void Dispose() { _cts?.Cancel(); if (Current == this) Current = null; }
}
