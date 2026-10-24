using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using DcxManager.Ai;
using DcxManager.Services;

namespace DcxManager.Views;

/// <summary>
/// "✦ Analyse screenshot with AI": preview → optional redaction (drag boxes; they are burned into the pixels, not just
/// drawn on top) → task → AI Engineer sidebar. Sent only to a model whose image support was verified
/// (<see cref="AiClient.VisionAsync"/>); otherwise the window says why and Send stays disabled. Screenshots are treated as
/// sensitive: nothing is stored, the image lives only in the conversation's context (memory) until it is cleared.
/// </summary>
public static class ScreenshotAi
{
    public static readonly (string Label, string Task)[] Tasks =
    {
        ("Analyse the screenshot", "Analyse this screenshot: what is shown, what state the system is in, and anything that looks wrong or needs attention."),
        ("Explain the error shown", "Explain the error or warning visible in this screenshot: likely cause and what to check or do next (numbered steps). Quote the exact message you read."),
        ("Read the visible message(s)", "Transcribe the text messages visible in this screenshot exactly, then explain them briefly. Say clearly if something is unreadable."),
        ("Summarise the screen", "Summarise what this screen shows in a few bullet points."),
    };

    /// <summary>Largest side sent to the model (bigger screenshots are scaled down; the original isn't kept).</summary>
    public const int MaxSide = 1920;

    public static async Task AnalyseAsync(BitmapSource shot, string source, Window? owner = null)
    {
        if (AiActions.Why(AiFeature.Files) is { } why) { Dialogs.Info("AI", why); return; }
        var w = Dialogs.MakeWindow("Analyse screenshot with AI", 1040, 800);
        if (owner != null) { try { w.Owner = owner; } catch { } }
        var root = new DockPanel { Margin = new Thickness(14) };

        var head = new StackPanel();
        var h = new TextBlock { Text = "SCREENSHOT → AI", FontWeight = FontWeights.Bold }.Fs(FontStep.Title);
        h.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        head.Children.Add(h);
        head.Children.Add(new TextBlock { Text = $"Source: {source} · {shot.PixelWidth}×{shot.PixelHeight}. Drag on the image to black out anything that shouldn't be sent (names, addresses, tokens on screen). Black boxes are burned into the image before it leaves this window.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6) });
        var cap = new TextBlock { Text = "Checking whether the selected model can read images…", TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
        head.Children.Add(cap);
        var dest = new ContentControl { Margin = new Thickness(0, 4, 0, 4) };
        head.Children.Add(dest);
        var row = new WrapPanel { Margin = new Thickness(0, 6, 0, 6) };
        var task = new ComboBox { Width = 260, ItemsSource = Tasks.Select(t => t.Label).ToList(), SelectedIndex = 0 };
        var extra = new TextBox { Width = 420, Padding = new Thickness(5, 3, 5, 3), Margin = new Thickness(8, 0, 8, 0), ToolTip = "Optional: your own question about the screenshot" };
        var undo = new Button { Content = "Undo box", Style = (Style)Application.Current.Resources["SmallButton"] };
        var clear = new Button { Content = "Clear boxes", Style = (Style)Application.Current.Resources["SmallButton"] };
        row.Children.Add(task); row.Children.Add(extra); row.Children.Add(undo); row.Children.Add(clear);
        head.Children.Add(row);
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        var send = new Button { Content = "Send to AI", Style = (Style)Application.Current.Resources["PrimaryButton"], IsEnabled = false };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var count = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        count.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var bottom = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var bs = new StackPanel { Orientation = Orientation.Horizontal };
        bs.Children.Add(send); bs.Children.Add(cancel);
        DockPanel.SetDock(bs, Dock.Right);
        bottom.Children.Add(bs); bottom.Children.Add(count);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);

        // Image + redaction canvas in image pixel coordinates (the Viewbox scales both together).
        var canvas = new Canvas { Width = shot.PixelWidth, Height = shot.PixelHeight, Background = Brushes.Transparent, Cursor = Cursors.Cross };
        var surface = new Grid { Width = shot.PixelWidth, Height = shot.PixelHeight };
        surface.Children.Add(new Image { Source = shot, Stretch = Stretch.Fill });
        surface.Children.Add(canvas);
        var frame = new Border { BorderThickness = new Thickness(1), Child = new Viewbox { Child = surface, Stretch = Stretch.Uniform } };
        frame.SetResourceReference(Border.BorderBrushProperty, "Line");
        root.Children.Add(frame);

        var boxes = new List<Rect>();
        Point? start = null; Rectangle? live = null;
        void Count() => count.Text = boxes.Count == 0 ? "No areas blacked out." : $"{boxes.Count} area(s) will be blacked out in the sent image.";
        canvas.MouseLeftButtonDown += (_, e) => { start = e.GetPosition(canvas); live = new Rectangle { Fill = Brushes.Black, Opacity = 0.85 }; canvas.Children.Add(live); canvas.CaptureMouse(); };
        canvas.MouseMove += (_, e) =>
        {
            if (start is not { } s || live == null) return;
            var p = e.GetPosition(canvas);
            var r = new Rect(s, p);
            Canvas.SetLeft(live, r.X); Canvas.SetTop(live, r.Y); live.Width = r.Width; live.Height = r.Height;
        };
        canvas.MouseLeftButtonUp += (_, e) =>
        {
            canvas.ReleaseMouseCapture();
            if (start is { } s && live != null)
            {
                var r = new Rect(s, e.GetPosition(canvas));
                r.Intersect(new Rect(0, 0, shot.PixelWidth, shot.PixelHeight));
                if (!r.IsEmpty && r.Width >= 3 && r.Height >= 3) boxes.Add(r); else canvas.Children.Remove(live);
            }
            start = null; live = null; Count();
        };
        undo.Click += (_, _) => { if (boxes.Count == 0) return; boxes.RemoveAt(boxes.Count - 1); canvas.Children.RemoveAt(canvas.Children.Count - 1); Count(); };
        clear.Click += (_, _) => { boxes.Clear(); canvas.Children.Clear(); Count(); };
        Count();

        string url = "";
        w.Loaded += async (_, _) =>
        {
            try { url = await AiClient.BaseUrlAsync(new CancellationTokenSource(8000).Token); dest.Content = NetUi.Badge(NetworkPolicy.Classify(url), $"{AiEngines.Title(AiClient.Engine)} · {AiClient.Model}"); }
            catch (Exception ex) { cap.Text = "✕ " + ex.Message.Split('\n')[0]; return; }
            var v = await AiClient.VisionAsync();
            cap.Text = (v.Supported ? "✓ " : "✕ Not available: ") + v.Reason + (v.Supported ? "" : ". Nothing will be sent.");
            cap.SetResourceReference(TextBlock.ForegroundProperty, v.Supported ? "Good" : "Warn");
            send.IsEnabled = v.Supported;
        };
        bool go = false;
        send.Click += (_, _) => { go = true; w.Close(); };
        w.Content = root;
        w.ShowDialog();
        if (!go) return;

        if (!AiClient.IsLocal(url))
        {
            if (!AppSettings.Current.AiAllowCloud) { Dialogs.Error("AI", $"{url} is not a local/LAN address and internet AI providers are not allowed.", explain: false); return; }
            if (!AiActions.ConfirmInternet(url, $"a screenshot of “{source}” ({shot.PixelWidth}×{shot.PixelHeight}, {boxes.Count} area(s) blacked out)")) return;
        }
        var (png, width, height) = Render(shot, boxes);
        var label = Tasks[Math.Max(0, task.SelectedIndex)];
        var question = label.Task + (extra.Text.Trim().Length > 0 ? "\n\nMy question: " + extra.Text.Trim() : "");
        var ws = AiWorkspace.Current;
        ws.AddImage($"screenshot — {source} ({DateTime.Now:HH:mm:ss})", png, width, height);
        ws.RequestFocus();
        AuditLog.Write("you", "ai", "screenshot sent to AI", $"{source} · {width}×{height} · {boxes.Count} redaction(s) · {AiClient.Model}");
        await ws.SendAsync(question, AiFeature.Files, consented: true);
    }

    /// <summary>The image as sent: redaction boxes drawn opaque into the pixels, scaled so the longest side is ≤ <see cref="MaxSide"/>.</summary>
    public static (byte[] Png, int Width, int Height) Render(BitmapSource shot, IReadOnlyList<Rect> boxes)
    {
        double scale = Math.Min(1.0, (double)MaxSide / Math.Max(shot.PixelWidth, shot.PixelHeight));
        int w = Math.Max(1, (int)Math.Round(shot.PixelWidth * scale)), h = Math.Max(1, (int)Math.Round(shot.PixelHeight * scale));
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawImage(shot, new Rect(0, 0, w, h));
            foreach (var b in boxes) dc.DrawRectangle(Brushes.Black, null, new Rect(b.X * scale, b.Y * scale, b.Width * scale, b.Height * scale));
        }
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return (ms.ToArray(), w, h);
    }
}
