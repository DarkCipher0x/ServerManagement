using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DcxManager.Reports;
using DcxManager.Services;
using DcxManager.Ssh;
using Microsoft.Win32;

namespace DcxManager.Views;

/// <summary>
/// Health / log reports for one server (inside Manage server) or a fleet (sidebar → Reports).
/// Offline digests + rule findings always run; the AI pass is optional and toggleable. Results are kept encrypted.
/// </summary>
public sealed class ReportView : UserControl, IDisposable
{
    private readonly HostEntry? _host;
    private readonly ComboBox _window = new() { Width = 130, ItemsSource = ReportEngine.Windows, SelectedItem = "24 hours ago" };
    private readonly WrapPanel _sources = new();
    private readonly ListBox _hosts = new() { SelectionMode = SelectionMode.Multiple, MinHeight = 120, MaxHeight = 220 };
    private readonly TextBox _files = new() { AcceptsReturn = true, MinHeight = 46, Padding = new Thickness(5, 3, 5, 3), ToolTip = "Extra log files to digest, one path per line (e.g. /var/log/nginx/error.log)" };
    private readonly TextBox _focus = new() { Padding = new Thickness(5, 3, 5, 3), ToolTip = "Optional: what should the AI look at? e.g. \"why was the site slow at 14:00\"" };
    private readonly CheckBox _useAi = new() { Content = "AI analysis", IsChecked = true, Margin = new Thickness(0, 8, 0, 0) };
    // 0.4.0: this PC (Windows) in the fleet report — its own sources, event logs and the Event Viewer exports.
    private readonly CheckBox _localPc = new() { Content = $"💻 This PC ({Environment.MachineName}, Windows)", Margin = new Thickness(0, 4, 0, 4), ToolTip = "Examine this computer: WMI, the Event Viewer, Defender, the firewall, services, disks — read-only" };
    private readonly WrapPanel _localSources = new();
    private readonly TextBox _eventLogs = new() { Padding = new Thickness(5, 3, 5, 3), Text = string.Join(", ", LocalReport.DefaultEventLogs), ToolTip = "Event logs for the \"Event Viewer: System and Application\" source and the exports, comma-separated (e.g. System, Application, Microsoft-Windows-TaskScheduler/Operational)" };
    private readonly ComboBox _eventLevels = new() { Width = 170, ItemsSource = new[] { "Critical + Error", "Critical + Error + Warning", "Everything" }, SelectedIndex = 1 };
    private readonly StackPanel _localPanel = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 4) };
    private readonly TextBlock _aiState = new TextBlock() { TextWrapping = TextWrapping.Wrap }.Fs(FontStep.Small);
    private readonly Button _run = new() { Content = "▶ Run report" };
    private readonly Button _stop = new() { Content = "■ Stop", IsEnabled = false };
    private readonly TextBlock _progress = new TextBlock() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) }.Fs(FontStep.Small);
    private readonly ComboBox _history = new() { MinWidth = 260, DisplayMemberPath = "Label" };
    private readonly StackPanel _result = new();
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private CancellationTokenSource? _cts;
    private ServerReport? _report;

    private sealed record HistoryRow(ServerReport R) { public string Label => $"{R.Created:MM-dd HH:mm} · {R.Title} · {R.Health}"; }

    public ReportView(HostEntry? host = null)
    {
        _host = host;
        _run.Style = (Style)FindResource("PrimaryButton");
        _stop.Style = (Style)FindResource("SmallButton");
        foreach (var t in new[] { _progress, _aiState }) t.SetResourceReference(TextBlock.ForegroundProperty, "Muted");

        foreach (var s in ReportEngine.Sources)
            _sources.Children.Add(new CheckBox { Content = s.Title, Tag = s.Id, IsChecked = s.Default, Margin = new Thickness(0, 3, 14, 3) });

        // ---- left: options ----
        var opts = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        opts.Children.Add(new TextBlock { Text = host == null ? "Fleet report" : "Health & log report", Style = (Style)FindResource("H2") });
        opts.Children.Add(new TextBlock { Text = "Collects read-only evidence over SSH (and from this PC's own Windows sources when ticked), digests the logs here, applies built-in checks" + (ReportAnalysis.Current != null ? ", then (optionally) asks the AI for a structured analysis." : "."), Style = (Style)FindResource("Hint"), Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap });
        if (host == null)
        {
            opts.Children.Add(Lbl("THIS PC"));
            opts.Children.Add(_localPc);
            foreach (var s in LocalReport.Sources)
                _localSources.Children.Add(new CheckBox { Content = s.Title + (s.Admin ? "  (administrator)" : ""), Tag = s.Id, IsChecked = s.Default, Margin = new Thickness(0, 3, 14, 3), ToolTip = s.Admin ? "Needs the app to run as administrator; otherwise the report says it couldn't be read" : null });
            _localPanel.Children.Add(_localSources);
            _localPanel.Children.Add(Lbl("EVENT LOGS")); _localPanel.Children.Add(_eventLogs);
            var exports = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            exports.Children.Add(_eventLevels);
            void X(string t, Func<Task> a, string tip) { var b = new Button { Content = t, Style = (Style)FindResource("SmallButton"), ToolTip = tip, Margin = new Thickness(6, 0, 0, 0) }; b.Click += async (_, _) => { b.IsEnabled = false; try { await a(); } catch (Exception ex) { Dialogs.Error("Event Viewer export", ex.Message); } finally { b.IsEnabled = true; } }; exports.Children.Add(b); }
            X("⤓ Export events (.evtx)…", ExportEvtxAsync, "One .evtx per log with the events of the window and levels above (opens in Event Viewer; a plain file)");
            X("⤓ Events as CSV…", ExportCsvAsync, "The same events as one CSV file (plain, formula-safe)");
            _localPanel.Children.Add(exports);
            _localPanel.Children.Add(new TextBlock { Text = "The Security log and BitLocker need the app to run as administrator. Exports are plain files outside the encrypted store.", Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
            opts.Children.Add(_localPanel);
            _localPc.Click += (_, _) => _localPanel.Visibility = _localPc.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            opts.Children.Add(Lbl("SERVERS"));
            foreach (var h in AppData.Current.Hosts) _hosts.Items.Add(new ListBoxItem { Content = $"{h.Display}   [{h.Group}]", Tag = h, Padding = new Thickness(4, 2, 4, 2) });
            opts.Children.Add(_hosts);
            var sel = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            var all = new Button { Content = "All", Style = (Style)FindResource("SmallButton") }; all.Click += (_, _) => _hosts.SelectAll();
            var none = new Button { Content = "None", Style = (Style)FindResource("SmallButton") }; none.Click += (_, _) => _hosts.UnselectAll();
            sel.Children.Add(all); sel.Children.Add(none);
            foreach (var g in AppData.Current.Hosts.Select(h => h.Group).Where(g => g.Length > 0).Distinct())
            {
                var gb = new Button { Content = g, Style = (Style)FindResource("SmallButton"), ToolTip = "Select this group" };
                gb.Click += (_, _) => { foreach (ListBoxItem i in _hosts.Items) i.IsSelected = ((HostEntry)i.Tag).Group == g; };
                sel.Children.Add(gb);
            }
            opts.Children.Add(sel);
        }
        opts.Children.Add(Lbl("SINCE")); opts.Children.Add(_window);
        opts.Children.Add(Lbl("EVIDENCE")); opts.Children.Add(_sources);
        opts.Children.Add(Lbl("EXTRA LOG FILES")); opts.Children.Add(_files);
        opts.Children.Add(Lbl("FOCUS (optional)")); opts.Children.Add(_focus);
        if (ReportAnalysis.Current != null) { opts.Children.Add(_useAi); opts.Children.Add(_aiState); }   // only with an analyst module (the AI)
        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(_run); buttons.Children.Add(_stop);
        opts.Children.Add(buttons);
        opts.Children.Add(_progress);
        _run.Click += async (_, _) => await RunAsync();
        _stop.Click += (_, _) => _cts?.Cancel();

        // ---- right: result ----
        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        void B(string t, Action a, string? tip = null) { var b = new Button { Content = t, Style = (Style)FindResource("SmallButton"), ToolTip = tip, Margin = new Thickness(0, 0, 6, 4) }; b.Click += (_, _) => a(); bar.Children.Add(b); }
        B("⤓ HTML…", () => Export("html"), "Self-contained HTML report (plain file — not encrypted)");
        B("⤓ Markdown…", () => Export("md"));
        B("⧉ Copy Markdown", () => { if (_report != null) Menus.Clip(ReportEngine.Markdown(_report)); });
        B("⇗ Share…", Share, "Windows Share sheet (Mail, Teams, Nearby Sharing…) with the HTML report attached — a plain copy");
        if (Assist.Current != null) B("✦ Discuss with AI operator", Discuss, "Open the AI operator with this report as context");
        foreach (var act in ReportAnalysis.Current?.Actions ?? Array.Empty<ReportAction>())
            B(act.Label, () => { if (_report != null) act.Run(_report); }, act.Tip);
        B("✕ Delete", DeleteCurrent);
        bar.Children.Add(new TextBlock { Text = "history", Style = (Style)FindResource("Label"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 6, 0) });
        bar.Children.Add(_history);
        _history.SelectionChanged += (_, _) => { if (_history.SelectedItem is HistoryRow r && r.R != _report) Show(r.R); };

        _scroll.Content = _result;
        var right = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        right.Children.Add(bar); right.Children.Add(_scroll);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330), MinWidth = 220 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 300 });
        var left = new ScrollViewer { Content = opts, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var split = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeBehavior = GridResizeBehavior.PreviousAndNext, ToolTip = "Drag to resize" };
        Grid.SetColumn(left, 0); Grid.SetColumn(split, 1); Grid.SetColumn(right, 2);
        right.Margin = new Thickness(12, 0, 0, 0);
        grid.Children.Add(left); grid.Children.Add(split); grid.Children.Add(right);
        Content = new Border { Padding = host == null ? new Thickness(14, 12, 14, 12) : new Thickness(0), Child = grid };

        Menus.Attach(_scroll, () => ReportMenu(null));
        Loaded += (_, _) => { RefreshAiState(); LoadHistory(); };
        Placeholder();
    }

    private static TextBlock Lbl(string t) => new() { Text = t, Style = (Style)Application.Current.Resources["Label"], Margin = new Thickness(0, 10, 0, 4) };

    private void RefreshAiState()
    {
        if (ReportAnalysis.Current is not { } analyst) { _useAi.IsChecked = false; return; }
        var why = analyst.WhyNot();
        _useAi.IsEnabled = why == null;
        if (why != null) _useAi.IsChecked = false;
        _aiState.Text = why ?? $"Engine: {analyst.EngineLine}. Logs are digested first and secrets masked{(AppSettings.Current.AiRedactSecrets ? "" : " (masking is OFF)")}.";
    }

    private void LoadHistory()
    {
        var all = ReportEngine.Load().Where(r => _host == null || r.Hosts.Count == 1 && r.Hosts[0] == _host.Display).Select(r => new HistoryRow(r)).ToList();
        _history.ItemsSource = all;
        if (_report == null && all.Count > 0) { _history.SelectedItem = all[0]; }
    }

    private void Placeholder()
    {
        _result.Children.Clear();
        _result.Children.Add(new TextBlock { Text = "No report yet. Pick the evidence on the left and run one — it works offline (AI off) too.", Style = (Style)FindResource("Hint"), Margin = new Thickness(0, 20, 0, 0) });
    }

    private async Task RunAsync()
    {
        if (_cts != null) return;
        var hosts = _host != null ? new List<HostEntry> { _host } : _hosts.SelectedItems.Cast<ListBoxItem>().Select(i => (HostEntry)i.Tag).ToList();
        bool local = _host == null && _localPc.IsChecked == true;
        if (hosts.Count == 0 && !local) { Dialogs.Info("Report", "Select at least one server, or tick This PC."); return; }
        var o = new ReportOptions
        {
            Window = _window.SelectedItem as string ?? "24 hours ago",
            Sources = _sources.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToHashSet(),
            LogFiles = _files.Text.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith('/')).ToList(),
            UseAi = _useAi.IsChecked == true,
            Focus = _focus.Text.Trim(),
            IncludeLocalPc = local,
            LocalSources = _localSources.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToHashSet(),
            LocalEventLogs = EventLogNames(),
        };
        _cts = new CancellationTokenSource();
        _run.IsEnabled = false; _stop.IsEnabled = true;
        var progress = new Progress<string>(s => _progress.Text = s);
        try
        {
            var r = await ReportEngine.RunAsync(hosts, o, progress, _cts.Token);
            try { ReportEngine.Save(r); } catch (Exception ex) { App.Log("Report save", ex); }
            AuditLog.Write("you", string.Join(",", r.Hosts), "report generated", r.AiUsed ? "rules+ai" : "rules");
            Show(r);
            LoadHistory();
            _progress.Text = $"done {DateTime.Now:HH:mm:ss}" + (r.AiUsed && ReportAnalysis.Current is { } a ? $" · AI {a.LastLatency.TotalSeconds:0.0}s" : "") + (r.AiNote.Length > 0 ? " · " + r.AiNote : "");
        }
        catch (OperationCanceledException) { _progress.Text = "stopped"; }
        catch (Exception ex) { _progress.Text = "✕ " + ex.Message.Split('\n')[0]; App.Log("Report", ex); }
        finally { _cts = null; _run.IsEnabled = true; _stop.IsEnabled = false; }
    }

    // ---- this PC: Event Viewer exports (0.4.0) ---------------------------------------------------------------------

    private List<string> EventLogNames() => _eventLogs.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(WindowsEvents.CheckLog).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    private int[] EventLevels() => _eventLevels.SelectedIndex switch { 0 => new[] { 1, 2 }, 1 => new[] { 1, 2, 3 }, _ => new[] { 0, 1, 2, 3, 4, 5 } };
    private string EventQuery() => WindowsEvents.Query(EventLevels(), WindowsEvents.WindowOf(_window.SelectedItem as string ?? "24 hours ago"));

    private async Task ExportEvtxAsync()
    {
        var logs = EventLogNames();
        if (logs.Count == 0) { Dialogs.Info("Event Viewer export", "Name at least one event log."); return; }
        var dlg = new OpenFolderDialog { Title = "Folder for the .evtx files" };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        if (!StorageGuard.ConfirmDestination(dlg.FolderName, "Export events", encrypted: false)) return;
        var written = new List<string>();
        foreach (var log in logs)
        {
            var file = Path.Combine(dlg.FolderName, $"{Environment.MachineName}-{string.Concat(log.Select(c => char.IsLetterOrDigit(c) ? c : '-'))}-{DateTime.Now:yyyyMMdd-HHmm}.evtx");
            _progress.Text = $"exporting {log}…";
            await WindowsEvents.ExportEvtxAsync(log, EventQuery(), file);
            written.Add(file);
        }
        AuditLog.Write("you", LocalReport.Display, "Event Viewer logs exported (.evtx, plain)", string.Join(", ", logs));
        _progress.Text = $"exported {written.Count} file(s)";
        MainWindow.Instance.Status($"Exported {written.Count} .evtx file(s) (plain, not encrypted) → {dlg.FolderName}");
    }

    private async Task ExportCsvAsync()
    {
        var logs = EventLogNames();
        if (logs.Count == 0) { Dialogs.Info("Event Viewer export", "Name at least one event log."); return; }
        var dlg = new SaveFileDialog { FileName = $"{Environment.MachineName}-events-{DateTime.Now:yyyyMMdd-HHmm}.csv", Filter = "CSV|*.csv" };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        var all = new List<WinEvent>();
        var errors = new List<string>();
        foreach (var log in logs)
        {
            _progress.Text = $"reading {log}…";
            try { all.AddRange(await WindowsEvents.ReadAsync(log, EventQuery(), 20000)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors.Add(ex.Message); }
        }
        File.WriteAllText(dlg.FileName, WindowsEvents.Csv(all), new UTF8Encoding(true));
        AuditLog.Write("you", LocalReport.Display, "Event Viewer events exported (CSV, plain)", $"{all.Count} events from {string.Join(", ", logs)}");
        _progress.Text = $"{all.Count} events → {Path.GetFileName(dlg.FileName)}" + (errors.Count > 0 ? " · " + string.Join("; ", errors) : "");
        MainWindow.Instance.Status($"Exported {all.Count} events (plain CSV, not encrypted) → {dlg.FileName}");
    }

    // ---- rendering ------------------------------------------------------------------------------------------------

    private void Show(ServerReport r)
    {
        _report = r;
        _result.Children.Clear();
        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        head.Children.Add(new TextBlock { Text = r.Title, Style = (Style)FindResource("H2") });
        head.Children.Add(new TextBlock { Text = $"{r.Created:yyyy-MM-dd HH:mm} · since {r.Window} · {(r.AiUsed ? "rules + " + r.Engine : r.Engine)}", Style = (Style)FindResource("Hint") });
        var health = new Border { CornerRadius = new CornerRadius(2), Padding = new Thickness(8, 2, 8, 2), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 6) };
        health.SetResourceReference(Border.BackgroundProperty, r.Health == "critical" ? "Bad" : r.Health == "degraded" ? "Warn" : "Good");
        health.Child = new TextBlock { Text = r.Health.ToUpperInvariant(), FontWeight = FontWeights.Bold, Foreground = Brushes.Black }.Mono();
        head.Children.Add(health);
        head.Children.Add(Sel(r.Summary, FontStep.Base));
        if (r.AiNote.Length > 0) { var n = Sel(r.AiNote, FontStep.Meta); n.SetResourceReference(TextBox.ForegroundProperty, "Warn"); head.Children.Add(n); }
        _result.Children.Add(head);

        foreach (var f in r.Findings) _result.Children.Add(Card(r, f));
        if (r.NextSteps.Count > 0)
        {
            _result.Children.Add(new TextBlock { Text = "Next steps", Style = (Style)FindResource("H2"), Margin = new Thickness(0, 12, 0, 4) });
            _result.Children.Add(Sel(string.Join("\n", r.NextSteps.Select((s, i) => $"{i + 1}. {s}")), FontStep.Control));
        }
        var ev = new Expander { Header = $"Evidence digest ({r.Evidence.Count} sections)", Margin = new Thickness(0, 12, 0, 0) };
        var evText = new TextBox { Style = (Style)FindResource("Mono"), Text = string.Join("\n\n", r.Evidence.Select(e => $"### {e.Host} · {e.Title}{(e.RawLines > 0 ? $" ({e.RawLines} lines)" : "")}{(e.Error.Length > 0 ? " — " + e.Error : "")}\n{e.Digest.TrimEnd()}")), MaxHeight = 520 };
        Menus.ForText(evText, sel => TextAiItems(sel, r.Hosts.FirstOrDefault() ?? ""));
        ev.Content = evText;
        _result.Children.Add(ev);
        _scroll.ScrollToTop();
    }

    private TextBox Sel(string text, FontStep size) => new TextBox()
    {
        Text = text, IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent, TextWrapping = TextWrapping.Wrap, Padding = new Thickness(0),
    }.Fs(size);

    private Border Card(ServerReport r, Finding f)
    {
        var p = new StackPanel();
        var title = new DockPanel();
        var pill = new Border { CornerRadius = new CornerRadius(2), Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        pill.SetResourceReference(Border.BackgroundProperty, SevBrush(f.Severity));
        pill.Child = new TextBlock { Text = f.Severity.ToUpperInvariant(), FontWeight = FontWeights.Bold, Foreground = Brushes.Black }.Fs(FontStep.Micro).Mono();
        var src = new TextBlock { Text = (r.Hosts.Count > 1 ? f.Host + " · " : "") + f.Source, Style = (Style)FindResource("Hint"), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(pill, Dock.Left); DockPanel.SetDock(src, Dock.Right);
        title.Children.Add(pill); title.Children.Add(src);
        title.Children.Add(new TextBlock { Text = f.Title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap }.Fs(FontStep.Base));
        p.Children.Add(title);
        if (f.Detail.Length > 0) p.Children.Add(Sel(f.Detail, FontStep.Body));
        if (f.Evidence.Length > 0) { var e = Sel(f.Evidence, FontStep.Small); e.Mono(); e.SetResourceReference(TextBox.ForegroundProperty, "Muted"); p.Children.Add(e); }
        if (f.Recommendation.Length > 0) { var rec = Sel("→ " + f.Recommendation, FontStep.Body); rec.Margin = new Thickness(0, 4, 0, 0); p.Children.Add(rec); }
        foreach (var c in f.Commands)
        {
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
            var copy = new Button { Content = "Copy", Style = (Style)FindResource("SmallButton") };
            var cmd = c;
            copy.Click += (_, _) => Menus.Clip(cmd);
            DockPanel.SetDock(copy, Dock.Right);
            // Server commands run over SSH after approval; this PC's suggestions are PowerShell to copy — the report never runs anything locally.
            if (!LocalReport.IsLocal(f.Host))
            {
                var run = new Button { Content = "Run…", Style = (Style)FindResource("SmallButton"), ToolTip = "Shows the exact command and runs it only if you approve" };
                run.Click += async (_, _) => await RunSuggestedAsync(f, cmd);
                DockPanel.SetDock(run, Dock.Right);
                row.Children.Add(run);
            }
            row.Children.Add(copy);
            var t = Sel("$ " + c, FontStep.Meta); t.Mono(); t.SetResourceReference(TextBox.ForegroundProperty, "Accent");
            row.Children.Add(t);
            p.Children.Add(row);
        }
        var card = new Border { Style = (Style)FindResource("Card"), Child = p, Margin = new Thickness(0, 0, 0, 8), BorderThickness = new Thickness(3, 1, 1, 1) };
        card.SetResourceReference(Border.BorderBrushProperty, SevBrush(f.Severity));
        Menus.Attach(card, () => ReportMenu(f));
        return card;
    }

    private static string SevBrush(string s) => s switch { "critical" => "Bad", "high" => "Bad", "medium" => "Warn", "low" => "Accent", _ => "Line" };

    private IEnumerable<object?> ReportMenu(Finding? f)
    {
        var host = f != null ? AppData.Current.FindHostByName(f.Host) : _host;
        if (f != null)
        {
            yield return Menus.Item("Copy finding", () => Menus.Clip($"[{f.Severity}] {f.Title}\n{f.Detail}\n{f.Recommendation}\n{string.Join("\n", f.Commands)}".Trim()));
            if (f.Commands.Count > 0)
            {
                yield return Menus.Item("Copy commands", () => Menus.Clip(string.Join("\n", f.Commands)));
                if (!LocalReport.IsLocal(f.Host)) yield return Menus.Sub("Run command (with approval)", f.Commands.Select(c => (object?)Menus.AsyncItem(c.Length > 70 ? c[..70] + "…" : c, () => RunSuggestedAsync(f, c))).ToArray());
            }
            yield return Menus.AssistItem("✦ Ask the AI operator about this", a => { a.AskOperator($"About the finding \"{f.Title}\" on {f.Host}: {f.Detail}\nRecommendation was: {f.Recommendation}\nInvestigate it with the tools and tell me exactly what to do."); return Task.CompletedTask; }, use: AssistUse.Operator);
            if (host != null)
            {
                yield return Menus.Item("Open terminal on " + host.Display, () => MainWindow.Instance.OpenTerminal(host));
                yield return Menus.Item("Manage " + host.Display, () => MainWindow.Instance.OpenServer(host));
            }
            yield return Menus.Item("Dismiss from this report", () =>
            {
                if (_report == null) return;
                _report.Findings.Remove(f);
                try { ReportEngine.Save(_report); } catch { }
                Show(_report);
            });
            yield return Menus.Sep();
        }
        yield return Menus.Item("Run report again", () => _ = RunAsync(), enabled: _cts == null);
        yield return Menus.Item("Export HTML…", () => Export("html"), enabled: _report != null);
        yield return Menus.Item("Export Markdown…", () => Export("md"), enabled: _report != null);
        yield return Menus.Item("Copy as Markdown", () => { if (_report != null) Menus.Clip(ReportEngine.Markdown(_report)); }, enabled: _report != null);
        yield return Menus.Item("⚑ Add report to incident", () =>
        {
            if (_report == null) return;
            var host = AppData.Current.Hosts.FirstOrDefault(h => _report.Title.Contains(h.Display, StringComparison.OrdinalIgnoreCase));
            IncidentUi.AddText(Incidents.ItemKind.Report, _report.Title, ReportEngine.Markdown(_report), host, reportId: _report.Id);
        }, enabled: _report != null);
        yield return Menus.Item("Share HTML report…", Share, enabled: _report != null && ShareService.Supported);
        if (Assist.Current != null) yield return Menus.Item("Discuss with AI operator", Discuss, enabled: _report != null);
        foreach (var act in ReportAnalysis.Current?.Actions ?? Array.Empty<ReportAction>())
            yield return Menus.Item(act.MenuLabel, () => { if (_report != null) act.Run(_report); }, enabled: _report != null);
    }

    private IEnumerable<object?> TextAiItems(string sel, string hostName)
    {
        var text = sel.Length > 0 ? sel : "";
        yield return Menus.AssistItem("✦ Explain selection with AI", a => { a.AskOperator($"Explain this from {hostName} and say if it needs action:\n```\n{text}\n```"); return Task.CompletedTask; }, enabled: text.Length > 0, use: AssistUse.LogAnalysis);
    }

    private static void Ask(string text) => Assist.Current?.AskOperator(text);

    private void Discuss()
    {
        if (_report == null) return;
        var md = ReportEngine.Markdown(_report);
        if (md.Length > 12000) md = md[..12000] + "\n…(truncated)";
        Ask("Here is a report generated by the app. Help me work through the findings, starting with the most important. Verify with the tools before recommending changes.\n\n" + md);
    }

    private async Task RunSuggestedAsync(Finding f, string cmd)
    {
        var host = AppData.Current.FindHostByName(f.Host) ?? _host;
        if (host == null) { Dialogs.Error("Run", "This finding isn't tied to a saved host."); return; }
        var sudo = cmd.StartsWith("sudo ");
        var text = sudo ? cmd[5..] : cmd;
        if (!Envs.Confirm(host, "Run suggested command", $"Host: {host.Display} ({host.Endpoint})\nAs: {(sudo ? "root via sudo" : host.Username)}\nSuggested by: {(f.Source == "ai" ? "AI analysis" : "built-in check")}\n\n{cmd}\n\nRun it?", "Run", danger: true)) return;
        AuditLog.Write("you", host.Display, (sudo ? "sudo " : "") + text, "from report (" + f.Source + ")");
        try
        {
            var r = await SshPool.RunAsync(host, text, sudo, timeout: TimeSpan.FromMinutes(5));
            Dialogs.ShowText($"{host.Display} — {cmd}", r.Combined.Length > 0 ? r.Combined : $"(no output, exit {r.ExitCode})");
        }
        catch (Exception ex) { Dialogs.Error("Run", ex.Message); }
    }

    private void Share()
    {
        if (_report == null) return;
        AuditLog.Write("you", string.Join(",", _report.Hosts), "report shared", _report.Title);
        ShareService.ShareText(Window.GetWindow(this), _report.Title, ReportEngine.Html(_report), $"{SafeTitle(_report)}-{_report.Created:yyyyMMdd-HHmm}.html");
    }

    private static string SafeTitle(ServerReport r) => string.Concat(r.Title.Select(c => char.IsLetterOrDigit(c) ? c : '-')).Trim('-') is { Length: > 0 } s ? s : "report";

    private void Export(string kind)
    {
        if (_report == null) return;
        var safe = SafeTitle(_report);
        var dlg = new SaveFileDialog
        {
            FileName = $"{safe}-{_report.Created:yyyyMMdd-HHmm}.{kind}",
            Filter = kind == "html" ? "HTML report|*.html" : "Markdown|*.md",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        File.WriteAllText(dlg.FileName, kind == "html" ? ReportEngine.Html(_report) : ReportEngine.Markdown(_report), new UTF8Encoding(false));
        AuditLog.Write("you", string.Join(",", _report.Hosts), "report exported", Path.GetFileName(dlg.FileName));
        MainWindow.Instance.Status("Report exported (plain file, not encrypted) → " + dlg.FileName);
    }

    private void DeleteCurrent()
    {
        if (_report == null || !Dialogs.Confirm("Delete report", $"Delete “{_report.Title}” from {_report.Created:yyyy-MM-dd HH:mm}?", "Delete", danger: true)) return;
        ReportEngine.Delete(_report.Id);
        _report = null;
        Placeholder();
        LoadHistory();
    }

    public void Dispose() => _cts?.Cancel();

    /// <summary>Shows a report produced elsewhere (e.g. Logs → Analyse) and adds it to the history.</summary>
    public void ShowReport(ServerReport r) { Show(r); LoadHistory(); }
}
