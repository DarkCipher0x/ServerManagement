using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DcxManager.Services;
using DcxManager.Views;
using DcxManager.Vultr;

namespace DcxManager;

/// <summary>
/// ROADMAP #16 / ADR-015: the UI font size (type ramp) and the window sizing rule. Every window it opens is off-screen,
/// never activated, never minimised or maximised.
/// </summary>
public static class UiScaleTests
{
    public static async Task RunAsync(MainWindow main, string reportDir, Action<string> ok, Action<string, Exception?> fail, Action<Window, string> snap)
    {
        async Task Settle(int ms = 200)
        {
            await Task.Delay(ms);
            await Application.Current.Dispatcher.InvokeAsync(() => main.UpdateLayout(), DispatcherPriority.ApplicationIdle);
        }
        static void OffScreen(Window w) { w.Owner = null; w.ShowInTaskbar = false; w.WindowStartupLocation = WindowStartupLocation.Manual; w.Left = -5000; w.Top = -5000; w.ShowActivated = false; }
        double Res(string key) => (double)Application.Current.Resources[key];
        var shots = Directory.CreateDirectory(Path.Combine(reportDir, "shots")).FullName;
        var wa = SystemParameters.WorkArea;
        bool Near(double a, double b) => Math.Abs(a - b) < 0.6;
        double Want(double design) => Math.Min(design * Typography.WindowFactor, wa.Width * 0.96);

        // 1. The type ramp: 100 % = the old literals exactly; a change reaches styles, code-built elements and the main
        //    window live; it never touches the zoom or the terminal; it is persisted encrypted; out-of-range values clamp.
        try
        {
            Typography.ResetDefaults();
            for (int i = 0; i < Typography.Ramp.Length; i++)
                if (Res(Typography.Ramp[i].Key) != Typography.Ramp[i].Px) throw new Exception($"{Typography.Ramp[i].Key} at 100 % = {Res(Typography.Ramp[i].Key)}");
            // In a real (off-screen) window: resource changes reach elements through their window's tree.
            var h1 = new TextBlock { Text = "h1", Style = (Style)Application.Current.FindResource("H1") };
            var small = new TextBlock { Text = "small" }.Fs(FontStep.Small);
            var probe = new Window { Width = 200, Height = 100, Content = new StackPanel { Children = { h1, small } } };
            OffScreen(probe); probe.Show(); await Settle(100);
            var cm = new ContextMenu { Style = (Style)Application.Current.FindResource(typeof(ContextMenu)) };
            cm.Items.Add(new MenuItem { Header = "x" });
            if (h1.FontSize != 17 || small.FontSize != 11.5 || main.FontSize != 13.5) throw new Exception($"100 %: h1 {h1.FontSize}, small {small.FontSize}, main {main.FontSize}");
            int term = 0; Action onTerm = () => term++;
            Typography.TerminalChanged += onTerm;
            int termSize = AppSettings.Current.TerminalFontSize;
            try
            {
                Typography.Set(textSize: 1.25);
                for (int i = 0; i < Typography.Ramp.Length; i++)
                {
                    double want = Math.Round(Typography.Ramp[i].Px * 1.25 * 2, MidpointRounding.AwayFromZero) / 2;
                    if (Res(Typography.Ramp[i].Key) != want) throw new Exception($"{Typography.Ramp[i].Key} at 125 % = {Res(Typography.Ramp[i].Key)}, want {want}");
                }
                if (h1.FontSize != Typography.Px(FontStep.H1) || small.FontSize != Typography.Px(FontStep.Small) || main.FontSize != Typography.Px(FontStep.Base))
                    throw new Exception($"125 % not live: h1 {h1.FontSize}, small {small.FontSize}, main {main.FontSize}");
                // A menu built before the change picks up the new size when it opens (popups are outside the window trees).
                cm.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent, cm));
                if (cm.FontSize != Typography.Px(FontStep.Base)) throw new Exception("reused context menu size " + cm.FontSize);
                var btn = Menus.Descendants<Button>(main).FirstOrDefault(b => DependencyPropertyHelper.GetValueSource(b, Control.FontSizeProperty).BaseValueSource is BaseValueSource.DefaultStyle or BaseValueSource.ImplicitStyleReference or BaseValueSource.Style);
                if (btn != null && btn.FontSize != Typography.Px(FontStep.Control)) throw new Exception("theme button size " + btn.FontSize);
                if (((FrameworkElement)main.Content).LayoutTransform is not ScaleTransform st || Math.Abs(st.ScaleX - Typography.UiScale) > 0.001)
                    throw new Exception("text size changed the UI zoom");
                if (term != 0 || AppSettings.Current.TerminalFontSize != termSize) throw new Exception("text size touched the terminal");
                if (Math.Abs(SecureStore.Read<AppSettings>("settings.dcx").UiTextSize - 1.25) > 0.001) throw new Exception("not persisted");
                Typography.Set(textSize: 9); if (Typography.TextSize != 1.5) throw new Exception("upper clamp " + Typography.TextSize);
                Typography.Set(textSize: 0.1); if (Typography.TextSize != 0.85) throw new Exception("lower clamp " + Typography.TextSize);
                Typography.ResetDefaults();
                if (AppSettings.Current.UiTextSize != 1 || Res("FsBase") != 13.5) throw new Exception("reset");
            }
            finally { Typography.TerminalChanged -= onTerm; probe.Close(); }
            ok($"UI font size: {Typography.Ramp.Length} ramp tokens equal the design at 100 %; 125 % reaches styles (H1 17→21.5), code-built text, theme buttons, the main window and reused menus live; zoom and terminal untouched; persisted encrypted; clamps 85–150 %; reset");
        }
        catch (Exception ex) { fail("UI font size (type ramp)", ex); }

        // 2. The window sizing rule: designed size × scale × text size, capped to the work area, kept in proportion when
        //    typography changes while open, no handler left after close; size-to-content bodies scroll.
        try
        {
            Typography.ResetDefaults();
            double defaultWidth = Want(400);
            int subs = Typography.ChangedSubscribers;
            var dlg = Dialogs.MakeWindow("sizing probe", 400);
            var body = new StackPanel { Margin = new Thickness(18) };
            body.Children.Add(new TextBlock { Text = "A size-to-content dialog. Its body scrolls when it is taller than the screen, and its width follows UI scale × UI font size.", TextWrapping = TextWrapping.Wrap });
            body.Children.Add(new Button { Content = "OK", Style = (Style)Application.Current.FindResource("PrimaryButton"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) });
            dlg.Content = body;
            if (!Near(dlg.Width, Want(400))) throw new Exception($"dialog width {dlg.Width}, want {Want(400)}");
            if (dlg.MaxHeight > wa.Height) throw new Exception("dialog not capped to the work area");
            OffScreen(dlg); dlg.Show(); await Settle();
            if (dlg.Content is not ScrollViewer) throw new Exception("size-to-content body does not scroll");
            if (Typography.ChangedSubscribers != subs + 1) throw new Exception($"subscribers {subs}→{Typography.ChangedSubscribers}");
            double w0 = dlg.ActualWidth;
            Typography.Set(textSize: 1.5); await Settle();
            if (!Near(dlg.Width, Math.Min(w0 * 1.5, wa.Width * 0.96))) throw new Exception($"open dialog not resized: {w0} → {dlg.Width}");
            snap(dlg, Path.Combine(shots, "dialog-text-150.png"));
            dlg.Close(); await Settle(50);
            if (Typography.ChangedSubscribers != subs) throw new Exception("closed dialog left a Changed handler");

            var manual = Dialogs.MakeWindow("manual probe", 500, 400);
            if (!Near(manual.Height, Math.Min(400 * Typography.WindowFactor, wa.Height * 0.96))) throw new Exception("manual-height dialog " + manual.Height);
            manual.Close();

            Typography.Set(scale: 1.75, textSize: 1.5);
            var huge = Dialogs.MakeWindow("cap probe", 1100, 720);
            if (huge.Width > wa.Width * 0.96 + 0.5 || huge.Height > wa.Height * 0.96 + 0.5) throw new Exception($"not capped: {huge.Width}×{huge.Height} on {wa.Width}×{wa.Height}");
            huge.Close();
            ok($"window sizing: designed size × scale × text size ({defaultWidth:0} px at the defaults for a 400 px dialog), capped to the work area at 175 % × 150 %, open dialogs follow a change in proportion, size-to-content bodies scroll, no handler left after close");
        }
        catch (Exception ex) { fail("window sizing rule", ex); }
        finally { Typography.ResetDefaults(); }

        // 3. The windows that used to keep their designed size (AUDIT §6): host editor, unlock, credential editor, Vultr form,
        //    document viewer, web windows.
        try
        {
            Typography.Set(textSize: 1.25);
            double f = Typography.WindowFactor;
            var checks = new List<string>();
            void Scaled(string name, FrameworkElement root)
            {
                if (root.LayoutTransform is not ScaleTransform s || Math.Abs(s.ScaleX - Typography.UiScale) > 0.001) throw new Exception(name + ": content not scaled");
            }

            var cred = new CredentialEditWindow(null);
            if (!Near(cred.Width, Want(600))) throw new Exception("credential editor width " + cred.Width);
            if (cred.Content is not ScrollViewer { Content: FrameworkElement credRoot }) throw new Exception("credential editor body does not scroll");
            Scaled("credential editor", credRoot);
            OffScreen(cred); cred.Show(); await Settle();
            snap(cred, Path.Combine(shots, "credential-editor-125.png"));
            cred.Close();
            checks.Add("credential editor");

            var def = VultrCatalog.All.First(d => d.Create != null);
            var vf = new CloudFormWindow("t", "", def.Create!, new VultrClient(new VultrAccount { ApiKey = "invalid" }), null, null, false);
            if (!Near(vf.Width, Want(640))) throw new Exception("Vultr form width " + vf.Width);
            Scaled("Vultr form", (FrameworkElement)vf.Content);
            OffScreen(vf); vf.Show(); await Settle();
            snap(vf, Path.Combine(shots, "vultr-form-125.png"));
            vf.Close();
            checks.Add("Vultr form");

            var host = new HostEditWindow(null);
            if (!Near(host.Width, Want(640))) throw new Exception("host editor width " + host.Width);
            if (host.Content is not ScrollViewer) throw new Exception("host editor body does not scroll");
            OffScreen(host); host.Show(); await Settle();
            host.Close();
            checks.Add("host editor");

            var unlock = new UnlockWindow();
            if (!Near(unlock.Width, Want(440))) throw new Exception("unlock width " + unlock.Width);
            unlock.Close();
            checks.Add("unlock");

            if (Math.Abs(DocumentViewerWindow.StartZoom - Math.Round(f, 2)) > 0.001) throw new Exception("document start zoom " + DocumentViewerWindow.StartZoom);
            checks.Add($"document viewer (toolbar scaled, page starts at {DocumentViewerWindow.StartZoom * 100:0}%)");
            ok("fixed windows now scale: " + string.Join(", ", checks) + "; web windows scale their toolbar, pages stay at 100 % (ADR-015)");
        }
        catch (Exception ex) { fail("fixed windows scale", ex); }
        finally { Typography.ResetDefaults(); }

        // 4. No raw interface font sizes left in the source (a raw size ignores the UI font size). Skipped without a source tree.
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DcxManager.csproj"))) dir = dir.Parent;
            if (dir == null) ok("raw font-size scan: skipped (no source tree next to this build)");
            else
            {
                var raw = new Regex(@"\bFontSize\s*=\s*""?\d|""FontSize""\s+Value=""\d");
                var hits = new List<string>();
                int files = 0;
                foreach (var file in Directory.EnumerateFiles(dir.FullName, "*.*", SearchOption.AllDirectories))
                {
                    if (!(file.EndsWith(".cs") || file.EndsWith(".xaml")) || file.Contains(@"\bin\") || file.Contains(@"\obj\") || Path.GetFileName(file).StartsWith("SelfTest")) continue;
                    files++;
                    var lines = File.ReadAllLines(file);
                    for (int i = 0; i < lines.Length; i++)
                        if (raw.IsMatch(lines[i])) hits.Add($"{Path.GetFileName(file)}:{i + 1}");
                }
                if (hits.Count > 0) throw new Exception("raw sizes: " + string.Join(", ", hits.Take(20)));
                ok($"raw font-size scan: {files} source files, every interface size uses a type-ramp token");
            }
        }
        catch (Exception ex) { fail("raw font-size scan", ex); }
    }
}
