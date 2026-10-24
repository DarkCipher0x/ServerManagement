using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DcxManager.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;
using DcxManager.Documents;

namespace DcxManager.Views;

/// <summary>
/// The in-app document viewer: PDF (WebView2's own viewer: pages, zoom, search), HTML documents and text.
/// Toolbar: zoom, print, save as PDF / save a copy, open externally, the official copy (if any) and ✦ review with AI.
/// </summary>
public sealed class DocumentViewerWindow : Window
{
    private readonly DocumentSource _doc;
    private readonly WebView2 _view = new();
    private readonly TextBlock _zoom = new() { VerticalAlignment = VerticalAlignment.Center, MinWidth = 46, TextAlignment = TextAlignment.Center };
    private readonly string _base = "https://dcx.doc/" + Guid.NewGuid().ToString("N")[..12] + "/";
    /// <summary>PDF DISPLAYED → PDF TEXT EXTRACTED / NO MACHINE-READABLE TEXT → PDF CONTENT PROVIDED TO AI: each step only once it happened.</summary>
    private readonly TextBlock _state = new TextBlock() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis }.Fs(FontStep.Small);
    private Documents.PdfExtraction? _pdfText;

    public static void Open(DocumentSource doc) => new DocumentViewerWindow(doc).Show();

    private DocumentViewerWindow(DocumentSource doc)
    {
        _doc = doc;
        Title = doc.Title + " — ServerManagement";
        Typography.Size(this, 1000, 860);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SetResourceReference(BackgroundProperty, "Bg");
        SetResourceReference(ForegroundProperty, "Fg");
        Icon = Application.Current.MainWindow?.Icon;
        this.Fs(FontStep.Base);

        var bar = new WrapPanel { Margin = new Thickness(8, 6, 8, 6) };
        Button B(string text, string tip, Action click, string style = "SmallButton")
        {
            var b = new Button { Content = text, ToolTip = tip, Style = (Style)FindResource(style), Margin = new Thickness(0, 0, 4, 0) };
            b.Click += (_, _) => click();
            bar.Children.Add(b);
            return b;
        }
        B("−", "Zoom out (Ctrl+−)", () => Zoom(-0.1));
        bar.Children.Add(_zoom);
        B("+", "Zoom in (Ctrl++)", () => Zoom(+0.1));
        B("100%", "Actual size (Ctrl+0)", () => SetZoom(1));
        B("🖶 Print…", "Print (Ctrl+P)", Print);
        if (!doc.IsPdf) B("⇩ Save as PDF…", "Writes a PDF of this document (plain file)", () => _ = SaveAsPdfAsync());
        B("⇩ Save a copy…", "Save the document as a file (plain, not encrypted)", SaveCopy);
        B("↗ Open externally", "Open in your default app (a temporary plain copy, deleted when the app exits)", OpenExternally);
        if (doc.ExternalUrl != null) B(doc.ExternalLabel ?? "Official copy", doc.ExternalUrl, () => WebWindow.Open(doc.ExternalLabel ?? doc.Title, doc.ExternalUrl, "doc-external"));
        if (Assist.Current is { } assist)   // only with an assistant module (the AI)
        {
            var ai = B("✦ Review with AI", "Send this document to your AI engine for a summary and questions", () => _ = ReviewAsync(assist), "SmallButton");
            if (assist.WhyNot() is { } why) { ai.IsEnabled = false; ai.ToolTip = why; ToolTipService.SetShowOnDisabled(ai, true); }
        }
        _state.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _state.Text = doc.IsPdf ? "PDF DISPLAYED · text not read yet" : "";
        bar.Children.Add(_state);

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar); root.Children.Add(_view);
        Content = root;
        Typography.Track(this, bar);   // the toolbar only: a transform above the WebView2 (HwndHost) would misplace it
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        Loaded += async (_, _) => await LoadAsync();
        PreviewKeyDown += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) { if (e.Key == Key.Escape) Close(); return; }
            if (e.Key is Key.OemPlus or Key.Add) { Zoom(0.1); e.Handled = true; }
            else if (e.Key is Key.OemMinus or Key.Subtract) { Zoom(-0.1); e.Handled = true; }
            else if (e.Key is Key.D0 or Key.NumPad0) { SetZoom(1); e.Handled = true; }
            else if (e.Key == Key.P) { Print(); e.Handled = true; }
        };
        Closed += (_, _) => _view.Dispose();
        AuditLog.Write("you", "document", "opened " + doc.Title);
    }

    private async Task LoadAsync()
    {
        try
        {
            WebHost.Paint(_view, "Bg");
            await _view.EnsureCoreWebView2Async(await WebHost.EnvironmentAsync());
            var core = _view.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.AddWebResourceRequestedFilter(_base + "*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) =>
            {
                if (!e.Request.Uri.StartsWith(_base, StringComparison.OrdinalIgnoreCase)) return;
                e.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(_doc.Bytes), 200, "OK",
                    $"Content-Type: {_doc.Mime}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff" +
                    // Generated HTML runs no script and loads nothing external. (Not on PDFs: the built-in PDF viewer needs its own resources.)
                    (_doc.IsHtml ? "\r\nContent-Security-Policy: default-src 'none'; style-src 'unsafe-inline'; img-src data:" : ""));
            };
            // Documents never navigate anywhere else; links open in the user's browser after a confirmation.
            core.NavigationStarting += (_, e) =>
            {
                if (e.Uri.StartsWith(_base, StringComparison.OrdinalIgnoreCase) || e.Uri.StartsWith("about:")) return;
                e.Cancel = true;
                var link = e.Uri;
                if (link.StartsWith("https://"))
                    Offline.Run(link, "a browser link", () =>   // offline mode is checked before asking (ADR-016)
                    {
                        if (Dialogs.Confirm("Open link", $"Open this link in your browser?\n\n{link}", "Open"))
                            Process.Start(new ProcessStartInfo(link) { UseShellExecute = true });
                    });
            };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.DownloadStarting += (_, e) => e.Cancel = true;   // "Save a copy" is the only way out, with its warnings
            _view.ZoomFactorChanged += (_, _) => _zoom.Text = $"{_view.ZoomFactor * 100:0}%";
            // The page can't take a WPF transform (HwndHost), so it starts at the UI's own size instead (ADR-015); − / + / 100 % as before.
            SetZoom(StartZoom);
            core.Navigate(_base + Uri.EscapeDataString(_doc.FileName));
            // Read the PDF's text in the background so the state line is honest about what the AI could get.
            if (_doc.IsPdf) { _pdfText = await Documents.PdfText.ExtractAsync(_doc.Bytes); _state.Text = "PDF DISPLAYED · " + _pdfText.Label; }
        }
        catch (Exception ex) { Dialogs.Error("Document", "The WebView2 runtime is needed to show documents.\n\n" + ex.Message); Close(); }
    }

    /// <summary>The document's starting zoom: UI scale × UI font size, so a document reads at the same size as the interface.</summary>
    public static double StartZoom => Math.Round(Math.Clamp(Typography.WindowFactor, 0.3, 4), 2);

    private void Zoom(double d) => SetZoom(Math.Clamp(_view.ZoomFactor + d, 0.3, 4));
    private void SetZoom(double z) { _view.ZoomFactor = z; _zoom.Text = $"{z * 100:0}%"; }

    private void Print()
    {
        try { _view.CoreWebView2?.ShowPrintUI(CoreWebView2PrintDialogKind.Browser); }
        catch (Exception ex) { Dialogs.Error("Print", ex.Message); }
    }

    private async Task SaveAsPdfAsync()
    {
        var dlg = new SaveFileDialog { FileName = Path.ChangeExtension(_doc.FileName, ".pdf"), Filter = "PDF|*.pdf" };
        if (dlg.ShowDialog(this) != true || !StorageGuard.ConfirmDestination(Path.GetDirectoryName(dlg.FileName)!, "Save as PDF", encrypted: false)) return;
        try
        {
            var ok = await _view.CoreWebView2.PrintToPdfAsync(dlg.FileName);
            if (!ok) throw new IOException("WebView2 could not write the PDF.");
            AuditLog.Write("you", "document", "saved PDF of " + _doc.Title, dlg.FileName);
            MainWindow.Instance?.Status($"Saved (plain PDF, not encrypted) → {dlg.FileName}");
        }
        catch (Exception ex) { Dialogs.Error("Save as PDF", ex.Message); }
    }

    private void SaveCopy()
    {
        var ext = Path.GetExtension(_doc.FileName);
        var dlg = new SaveFileDialog { FileName = _doc.FileName, Filter = $"{(ext.Length > 0 ? ext.TrimStart('.').ToUpperInvariant() : "File")}|*{ext}|All files|*.*" };
        if (dlg.ShowDialog(this) != true || !StorageGuard.ConfirmDestination(Path.GetDirectoryName(dlg.FileName)!, "Save a copy", encrypted: false)) return;
        try
        {
            File.WriteAllBytes(dlg.FileName, _doc.Bytes);
            AuditLog.Write("you", "document", "saved a copy of " + _doc.Title, dlg.FileName);
            MainWindow.Instance?.Status($"Saved (plain file, not encrypted) → {dlg.FileName}");
        }
        catch (Exception ex) { Dialogs.Error("Save a copy", ex.Message); }
    }

    private void OpenExternally()
    {
        if (!Dialogs.Confirm("Open externally", $"Your default app for {Path.GetExtension(_doc.FileName)} files will open a temporary plain copy of “{_doc.Title}”.\n\nThe copy is in the app's private temp folder and is deleted when the app exits (and at the next start).", "Open")) return;
        try
        {
            var dir = Path.Combine(ShareService.TempDir, Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, string.Concat(_doc.FileName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c)));
            File.WriteAllBytes(path, _doc.Bytes);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { Dialogs.Error("Open externally", ex.Message); }
    }

    private async Task ReviewAsync(IAssistant assist)
    {
        if (_doc.IsPdf && _doc.AiText == null)
        {
            _pdfText = await assist.ReviewPdfAsync(_doc.Title, _doc.Bytes, _doc.AiTask, s => _state.Text = "PDF DISPLAYED · " + s, _pdfText);
            return;
        }
        string? text = _doc.AiText;
        if (text == null && !_doc.IsPdf)
        {
            // Visible text of the rendered page (tables come out tab-separated, which models read well).
            try { text = System.Text.Json.JsonSerializer.Deserialize<string>(await _view.CoreWebView2.ExecuteScriptAsync("document.body.innerText")); } catch { }
        }
        if (string.IsNullOrWhiteSpace(text)) { Dialogs.Info("Review with AI", "This document has no text to review."); return; }
        if (await assist.PreviewAsync(_doc.Title, _doc.IsHtml ? "document text" : "text", text) is not { } choice) return;
        await assist.AskAsync(_doc.Title + (choice.ScopeLabel == "everything" ? "" : $" ({choice.ScopeLabel})"), choice.Text, _doc.AiTask);
    }
}
