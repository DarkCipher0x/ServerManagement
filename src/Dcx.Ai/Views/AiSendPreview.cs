using System.Windows;
using System.Windows.Controls;
using DcxManager.Ai;
using DcxManager.Documents;
using DcxManager.Services;

namespace DcxManager.Views;

/// <summary>
/// "DATA BEING SENT TO AI": shown before a document's text goes to a model. It says exactly what will be sent — source,
/// extraction state, pages/scope, how many secrets were masked, credentials BLOCKED, the destination and its network
/// class — and shows the final (masked) text itself. Nothing is sent unless the user clicks Send.
/// </summary>
public static class AiSendPreview
{
    public sealed record Choice(string Text, string ScopeLabel);

    /// <summary>Returns the text to send (already masked when masking is on), or null when cancelled.</summary>
    public static async Task<Choice?> ShowAsync(string source, string what, PdfExtraction? pdf, string fullText)
    {
        string url;
        try { url = await AiClient.BaseUrlAsync(new CancellationTokenSource(8000).Token); }
        catch (Exception ex) { Dialogs.Error("AI", ex.Message, explain: false); return null; }
        var cls = NetworkPolicy.Classify(url);

        var w = Dialogs.MakeWindow("Data being sent to AI", 820, 720);
        var root = new DockPanel { Margin = new Thickness(16) };
        var head = new StackPanel();
        var h = new TextBlock { Text = "DATA BEING SENT TO AI", FontWeight = FontWeights.Bold }.Fs(FontStep.Title);
        h.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        head.Children.Add(h);
        var grid = new Grid { Margin = new Thickness(0, 8, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        int row = 0;
        TextBlock Row(string label, string value)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = new TextBlock { Text = label, Style = (Style)Application.Current.Resources["Label"], Margin = new Thickness(0, 2, 8, 2) };
            var v = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) };
            Grid.SetRow(l, row); Grid.SetRow(v, row); Grid.SetColumn(v, 1);
            grid.Children.Add(l); grid.Children.Add(v);
            row++;
            return v;
        }
        Row("SOURCE", source);
        Row("CONTENT", what);
        var scopeValue = Row("SCOPE", "");
        var masked = Row("REDACTION", "");
        Row("CREDENTIALS", "BLOCKED — vault passwords, keys and tokens are never included");
        Row("NOT INCLUDED", "other files, notes, terminals, Remote Desktop, screenshots, unrelated hosts");
        var dest = Row("DESTINATION", $"{AiEngines.Title(AiClient.Engine)} · {AiClient.Model} · {url}");
        head.Children.Add(grid);
        head.Children.Add(NetUi.Badge(cls, cls is NetClass.Local or NetClass.OnPremises ? "stays on your machine / network" : "leaves your network"));

        // Scope (PDF pages, or everything / the start).
        var scope = new ComboBox { Width = 260, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
        var pageFrom = new TextBox { Width = 50, Text = "1", Padding = new Thickness(4, 2, 4, 2) };
        var pageTo = new TextBox { Width = 50, Text = pdf?.PageTexts.Count.ToString() ?? "", Padding = new Thickness(4, 2, 4, 2) };
        var range = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 10, 0, 0), VerticalAlignment = VerticalAlignment.Bottom };
        range.Children.Add(new TextBlock { Text = "pages ", VerticalAlignment = VerticalAlignment.Center }); range.Children.Add(pageFrom);
        range.Children.Add(new TextBlock { Text = " to ", VerticalAlignment = VerticalAlignment.Center }); range.Children.Add(pageTo);
        if (pdf != null) scope.ItemsSource = new[] { $"All {pdf.PageTexts.Count} page(s) read", "A page range", "First page only" };
        else scope.ItemsSource = new[] { "Everything", "The first 20,000 characters" };
        scope.SelectedIndex = 0;
        var scopeRow = new WrapPanel(); scopeRow.Children.Add(scope); if (pdf != null) scopeRow.Children.Add(range);
        head.Children.Add(scopeRow);
        head.Children.Add(new TextBlock { Text = "EXACT TEXT THAT WILL BE SENT (after masking):", Style = (Style)Application.Current.Resources["Label"], Margin = new Thickness(0, 12, 0, 4) });
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        var send = new Button { Content = "Send to AI", Style = (Style)Application.Current.Resources["PrimaryButton"], IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(send); buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        var preview = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(8, 6, 8, 6) }.Mono();
        root.Children.Add(preview);

        string current = "", label = "";
        void Update()
        {
            string raw;
            if (pdf != null)
            {
                int n = pdf.PageTexts.Count;
                (int a, int b) = scope.SelectedIndex switch
                {
                    1 => (int.TryParse(pageFrom.Text, out var f) ? Math.Clamp(f, 1, n) : 1, int.TryParse(pageTo.Text, out var t) ? Math.Clamp(t, 1, n) : n),
                    2 => (1, 1),
                    _ => (1, n),
                };
                if (b < a) (a, b) = (b, a);
                raw = pdf.Text(a, b);
                label = a == 1 && b == n ? $"all {n} page(s)" : a == b ? $"page {a}" : $"pages {a}–{b}";
                range.IsEnabled = scope.SelectedIndex == 1;
            }
            else
            {
                raw = scope.SelectedIndex == 1 && fullText.Length > 20_000 ? fullText[..20_000] : fullText;
                label = scope.SelectedIndex == 1 ? "the first 20,000 characters" : "everything";
            }
            var (text, count) = AppSettings.Current.AiRedactSecrets ? AiClient.RedactCounted(raw) : (raw, 0);
            int budget = AiClient.PromptBudgetChars;
            bool cut = text.Length > budget;
            current = cut ? text[..budget] + $"\n…(truncated to {budget:N0} characters — the engine's prompt budget)" : text;
            scopeValue.Text = $"{label} · {Math.Min(text.Length, budget):N0} characters" + (cut ? $" (of {text.Length:N0}; the rest is not sent)" : "");
            masked.Text = AppSettings.Current.AiRedactSecrets ? (count == 0 ? "on — nothing secret-looking found" : $"on — {count} secret-looking value(s) masked") : "⚠ OFF — Settings → AI engine → Mask secrets";
            preview.Text = current;
        }
        scope.SelectionChanged += (_, _) => Update();
        pageFrom.TextChanged += (_, _) => Update();
        pageTo.TextChanged += (_, _) => Update();
        Update();
        send.Click += (_, _) => w.DialogResult = true;
        w.Content = root;
        return w.ShowDialog() == true ? new Choice(current, label) : null;
    }

    /// <summary>The one flow for "✦ review this PDF with AI": real extraction → honest state → preview → AI sidebar.</summary>
    public static async Task<PdfExtraction?> ReviewPdfAsync(string title, byte[] pdf, string task, Action<string>? status = null, PdfExtraction? known = null)
    {
        if (AiActions.Why(AiFeature.Files) is { } why) { Dialogs.Info("AI", why); return known; }
        status?.Invoke("extracting text…");
        var ex = known ?? await PdfText.ExtractAsync(pdf);
        status?.Invoke(ex.Label);
        if (!ex.HasText)
        {
            Dialogs.Info("Review with AI", ex.State switch
            {
                PdfTextState.NoText => $"“{title}” has no machine-readable text ({ex.Pages} page(s)): it is scanned or made of images. OCR would be required; the app does not include OCR, so nothing was sent to the AI.",
                PdfTextState.Encrypted => ex.Message + " Nothing was sent to the AI.",
                _ => $"The text of “{title}” could not be extracted: {ex.Message}. Nothing was sent to the AI.",
            });
            return ex;
        }
        var choice = await ShowAsync(title, ex.Label, ex, ex.Text());
        if (choice == null) { status?.Invoke(ex.Label + " · not sent"); return ex; }
        await AiActions.AskAsync(AiFeature.Files, $"{title} ({choice.ScopeLabel}, extracted PDF text)", choice.Text,
            task + " The material is text extracted from a PDF, with page markers; cite page numbers. If something seems missing (tables, scanned pages), say so rather than guessing.");
        status?.Invoke(ex.Label + $" · PDF CONTENT PROVIDED TO AI ({choice.ScopeLabel})");
        return ex;
    }
}
