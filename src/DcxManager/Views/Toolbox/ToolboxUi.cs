using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using DcxManager.Mail;
using DcxManager.Services;

namespace DcxManager.Views.Toolbox;

/// <summary>Pieces the toolbox tabs share: inputs, the "from this PC / from a saved server" picker, the findings list,
/// the output box, running a test with one button, and recording a run in the encrypted history.</summary>
internal static class ToolboxUi
{
    public static WrapPanel Bar(params UIElement[] items)
    {
        var w = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        foreach (var i in items) { if (i is FrameworkElement f) { f.Margin = new Thickness(0, 0, 8, 4); f.VerticalAlignment = VerticalAlignment.Center; } w.Children.Add(i); }
        return w;
    }

    public static TextBlock Label(string t) => new() { Text = t, Style = (Style)Application.Current.Resources["Label"], VerticalAlignment = VerticalAlignment.Center };
    public static TextBlock Hint(string t) => new() { Text = t, Style = (Style)Application.Current.Resources["Hint"], TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
    public static TextBox Input(double width, string text = "", string? tip = null) => new() { Width = width, Padding = new Thickness(5, 4, 5, 4), Text = text, ToolTip = tip };

    public static Button Button(string t, bool primary = false)
    {
        var b = new Button { Content = t, Margin = new Thickness(0, 0, 6, 4) };
        if (primary) b.Style = (Style)Application.Current.Resources["PrimaryButton"];
        return b;
    }

    /// <summary>A button that runs one async action, disabled while it runs; errors go to the status line.</summary>
    public static Button Run(string t, Func<Task> a, TextBlock status, bool primary = false)
    {
        var b = Button(t, primary);
        b.Click += async (_, _) =>
        {
            b.IsEnabled = false;
            try { await a(); }
            catch (OfflineBlockedException ex) { status.Text = "✕ " + ex.Message; }
            catch (OperationCanceledException) { status.Text = "Stopped."; }
            catch (Exception ex) { status.Text = "✕ " + ex.Message; }
            finally { b.IsEnabled = true; }
        };
        return b;
    }

    public static TextBlock Status() { var s = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6) }; s.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); return s; }

    public static TextBox Output()
    {
        var t = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        t.Mono();
        return t;
    }

    /// <summary>"This PC" or a saved server (the test then runs over SSH with the server's own tools).</summary>
    public static ComboBox FromPicker()
    {
        var c = new ComboBox { Width = 220, ToolTip = "Run the test from this PC, or from a saved server over SSH (dig, nc, mtr, curl, openssl on that server)" };
        void Fill()
        {
            var was = From(c)?.Id;
            c.Items.Clear();
            c.Items.Add(new ComboBoxItem { Content = "from this PC", Tag = null });
            foreach (var h in AppData.Current.Hosts.Where(h => h.Address.Length > 0 && !h.IsAdHoc).OrderBy(h => h.Group).ThenBy(h => h.Display, StringComparer.OrdinalIgnoreCase))
                c.Items.Add(new ComboBoxItem { Content = "from " + h.Display, Tag = h });
            c.SelectedItem = c.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is HostEntry h && h.Id == was) ?? c.Items[0];
        }
        Fill();
        Live.While(c, h => AppData.Changed += h, h => AppData.Changed -= h, Fill);   // 0.5.0: new hosts show at once
        return c;
    }

    public static HostEntry? From(ComboBox picker) => (picker.SelectedItem as ComboBoxItem)?.Tag as HostEntry;

    /// <summary>Runs a toolbox command on a saved server and returns its (masked) output.</summary>
    public static async Task<string> RemoteAsync(HostEntry host, string command, CancellationToken ct)
    {
        var r = await Ssh.SshPool.RunAsync(host, command, false, ct, TimeSpan.FromSeconds(90));
        return Redaction.Mask((r.Output + (r.Error.Length > 0 ? "\n" + r.Error : "")).Trim());
    }

    public static ListView Findings(ObservableCollection<MailFinding> src, FrameworkElement scope)
    {
        var list = new ListView { ItemsSource = src };
        var gv = new GridView();
        void Col(string h, string p, double w) => gv.Columns.Add(new GridViewColumn { Header = h, Width = w, DisplayMemberBinding = new Binding(p) });
        Col("", "Glyph", 30); Col("Area", "Area", 120); Col("Finding", "Text", 560); Col("What to do", "Fix", 380);
        list.View = gv;
        Menus.ForList(list, scope, () => new object?[]
        {
            list.SelectedItem is MailFinding f ? Menus.Item("Copy", () => Menus.Clip(Line(f))) : null,
            list.SelectedItem is MailFinding f2 && f2.Fix != null ? Menus.Item("Copy the fix", () => Menus.Clip(f2.Fix!)) : null,
            Menus.Item("Copy all findings", () => Menus.Clip(string.Join("\n", src.Select(Line))), enabled: src.Count > 0),
        });
        return list;
    }

    public static string Line(MailFinding f) => $"{f.Glyph} {f.Area}: {f.Text}" + (f.Fix != null ? $" — {f.Fix}" : "");
    public static string Report(IEnumerable<MailFinding> f) => string.Join("\n", f.Select(Line));

    /// <summary>A one-line verdict for a set of findings ("2 problems, 1 warning" / "all good").</summary>
    public static string Verdict(IEnumerable<MailFinding> f)
    {
        var l = f.ToList();
        int bad = l.Count(x => x.Level == "bad"), warn = l.Count(x => x.Level == "warn");
        return bad + warn == 0 ? $"all good ({l.Count} checks)" : string.Join(", ", new[] { bad > 0 ? $"{bad} problem{(bad == 1 ? "" : "s")}" : null, warn > 0 ? $"{warn} warning{(warn == 1 ? "" : "s")}" : null }.Where(s => s != null));
    }

    public static void Record(string tool, string target, string summary, string report)
    {
        ToolboxRun.Record(tool, target, summary, report);
        AuditLog.Write("you", "toolbox", tool, target);
    }
}
