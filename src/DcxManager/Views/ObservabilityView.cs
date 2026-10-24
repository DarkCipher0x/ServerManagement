using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using DcxManager.Notify;
using DcxManager.Services;

namespace DcxManager.Views;

/// <summary>
/// Observability (0.4.0): the log sinks' state and activity (events shipped per minute over the last hour), a test button,
/// and the dashboards — Grafana's list read through its API and opened in the app's own browser window, plus the links the
/// organisation set (Kibana, OpenSearch Dashboards, Microsoft Sentinel, Splunk…). Everything here is read-only towards the
/// providers; the only writes are the events the shipper sends.
/// </summary>
public sealed class ObservabilityView : UserControl, IDisposable
{
    private readonly StackPanel _sinks = new();
    private readonly TextBlock _sinkSummary = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly UniformGrid _bars = new() { Rows = 1, Columns = 60, Height = 70, Margin = new Thickness(0, 6, 0, 2) };
    private readonly TextBlock _barsText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ListBox _dash = new() { MinHeight = 90, MaxHeight = 260 };
    private readonly TextBlock _dashState = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly WrapPanel _links = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(30) };
    private List<GrafanaDashboard> _dashboards = new();

    private sealed record DashRow(GrafanaDashboard D) { public override string ToString() => (D.Folder.Length > 0 ? D.Folder + " / " : "") + D.Title + (D.Tags.Length > 0 ? "   [" + string.Join(", ", D.Tags) + "]" : ""); }

    public ObservabilityView()
    {
        foreach (var t in new[] { _sinkSummary, _barsText, _dashState }) t.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var page = new StackPanel { Margin = new Thickness(24, 16, 18, 24), MaxWidth = 980, HorizontalAlignment = HorizontalAlignment.Left };
        page.Children.Add(new TextBlock { Text = "Observability", Style = (Style)FindResource("H2") });
        page.Children.Add(new TextBlock
        {
            Text = "Where this app's own events go (log sinks) and where you look at everything else (dashboards). Events are the audit log, the application log and, when switched on, terminal session logs — masked before they leave, HTTPS only.",
            Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
        });

        // ---- sinks ----
        var sinkButtons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        sinkButtons.Children.Add(Small("Send test to every sink", async b => { b.IsEnabled = false; _sinkSummary.Text = "Sending…"; try { _sinkSummary.Text = await LogShipper.SendTestAsync(); } finally { b.IsEnabled = true; RefreshSinks(); } }, "One test event to each enabled sink, now"));
        sinkButtons.Children.Add(Small("Refresh", _ => RefreshSinks()));
        sinkButtons.Children.Add(Small("Settings…", _ => { MainWindow.Instance.Navigate("settings"); SettingsView.Current?.RevealCard("Log sinks"); }, "Add or change sinks (admin approval)"));
        page.Children.Add(Card("Log sinks", _sinks, _sinkSummary, sinkButtons));

        // ---- activity ----
        var act = new StackPanel();
        act.Children.Add(_bars);
        act.Children.Add(_barsText);
        page.Children.Add(Card("Activity — events shipped per minute, last hour", act));

        // ---- dashboards ----
        _dash.MouseDoubleClick += (_, _) => OpenSelected();
        var dashButtons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        dashButtons.Children.Add(Small("↻ Refresh list", async b => { b.IsEnabled = false; try { await LoadDashboardsAsync(); } finally { b.IsEnabled = true; } }, "Ask Grafana for its dashboards (read-only)"));
        dashButtons.Children.Add(Small("Open dashboard", _ => OpenSelected(), "Open the selected dashboard in the app's browser window"));
        dashButtons.Children.Add(Small("Open Grafana", _ => OpenLink("Grafana", AppSettings.Current.GrafanaUrl)));
        dashButtons.Children.Add(Small("Settings…", _ => { MainWindow.Instance.Navigate("settings"); SettingsView.Current?.RevealCard("Dashboards & SIEM links"); }));
        var dashPanel = new StackPanel();
        dashPanel.Children.Add(Lbl("GRAFANA"));
        dashPanel.Children.Add(_dash);
        dashPanel.Children.Add(_dashState);
        dashPanel.Children.Add(dashButtons);
        dashPanel.Children.Add(Lbl("LINKS"));
        dashPanel.Children.Add(_links);
        page.Children.Add(Card("Dashboards", dashPanel));

        Content = new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Menus.Attach(_dash, () => _dash.SelectedItem is DashRow r ? new object?[]
        {
            Menus.Item("Open in the app's browser", OpenSelected),
            Menus.Item("Copy link", () => { if (GrafanaClient.DashboardUrl(AppSettings.Current.GrafanaUrl, r.D.Url) is { } u) Menus.Clip(u); }),
        } : Array.Empty<object?>());

        _timer.Tick += (_, _) => { RefreshSinks(); DrawBars(); };
        Loaded += (_, _) => { LogShipper.StatusChanged += OnStatus; _timer.Start(); RefreshSinks(); DrawBars(); RefreshLinks(); if (_dashboards.Count == 0 && AppSettings.Current.GrafanaUrl.Length > 0) _ = LoadDashboardsAsync(); else RefreshDashState(); };
        Unloaded += (_, _) => { LogShipper.StatusChanged -= OnStatus; _timer.Stop(); };
        RefreshSinks(); DrawBars(); RefreshLinks(); RefreshDashState();
    }

    private void OnStatus() => Dispatcher.BeginInvoke(() => { RefreshSinks(); DrawBars(); });

    private void RefreshSinks()
    {
        _sinks.Children.Clear();
        var s = AppSettings.Current;
        var st = LogShipper.Statuses;
        if (st.Count == 0)
        {
            _sinks.Children.Add(new TextBlock { Text = "No log sink is set up. Settings → Integrations → Log sinks adds Elasticsearch / OpenSearch, Grafana Loki, Azure Monitor / Sentinel, Splunk, Graylog or a webhook.", TextWrapping = TextWrapping.Wrap });
        }
        foreach (var x in st)
        {
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var dot = new TextBlock { Text = "●", VerticalAlignment = VerticalAlignment.Center };
            dot.SetResourceReference(TextBlock.ForegroundProperty, x.State switch { "connected" => "Good", "failing" => "Bad", "disabled" => "Muted", _ => "Warn" });
            var nm = new TextBlock { Text = x.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, ToolTip = LogSink.TitleOf(x.Kind) };
            var kind = new TextBlock { Text = LogSink.TitleOf(x.Kind), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            kind.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            var detail = new TextBlock { Text = $"{x.State}" + (x.LastSent is { } t ? $" · last sent {t:HH:mm:ss}" : "") + (x.Sent > 0 ? $" · {x.Sent:N0} sent" : "") + (x.Queued > 0 ? $" · {x.Queued} queued" : "") + (x.LastError != null ? $"\n{x.LastError}" : ""), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(dot, 0); Grid.SetColumn(nm, 1); Grid.SetColumn(kind, 2); Grid.SetColumn(detail, 3);
            row.Children.Add(dot); row.Children.Add(nm); row.Children.Add(kind); row.Children.Add(detail);
            _sinks.Children.Add(row);
        }
        var what = new List<string>();
        if (s.ObserveSendAudit) what.Add("audit log"); if (s.ObserveSendAppLog) what.Add("application log"); if (s.ObserveSendSessionLogs) what.Add("session logs");
        _sinkSummary.Text = st.Count == 0 ? "" : (s.ObserveEnabled ? $"Shipping: {string.Join(" + ", what)}." : "● Shipping is OFF (Settings → Integrations → Log sinks).") +
            (LogShipper.QueuedTotal > 0 ? $" Queued: {LogShipper.QueuedTotal}." : "") + (LogShipper.Dropped > 0 ? $" Dropped while unreachable: {LogShipper.Dropped}." : "") +
            (Offline.On ? " Offline mode: only approved destinations are contacted." : "");
    }

    private void DrawBars()
    {
        var m = LogShipper.LastHour();
        int max = Math.Max(1, m.Max());
        _bars.Children.Clear();
        for (int i = 0; i < m.Length; i++)
        {
            var r = new Rectangle { VerticalAlignment = VerticalAlignment.Bottom, Height = Math.Max(m[i] > 0 ? 3 : 1, 66.0 * m[i] / max), Margin = new Thickness(1, 0, 1, 0), RadiusX = 1, RadiusY = 1, ToolTip = $"{m[i]} event(s), {59 - i} min ago" };
            r.SetResourceReference(Shape.FillProperty, m[i] > 0 ? "Accent" : "Line");
            _bars.Children.Add(r);
        }
        int total = m.Sum();
        _barsText.Text = total == 0 ? "Nothing shipped in the last hour." : $"{total:N0} event(s) in the last hour · peak {max:N0} per minute.";
    }

    private void RefreshLinks()
    {
        _links.Children.Clear();
        var s = AppSettings.Current;
        if (s.GrafanaUrl.Length > 0) _links.Children.Add(Small("Grafana", _ => OpenLink("Grafana", s.GrafanaUrl), s.GrafanaUrl));
        foreach (var raw in s.ObserveLinks)
        {
            var (label, link) = ParseLink(raw);
            if (label == null || GrafanaClient.ValidateUrl(link) != null) continue;
            _links.Children.Add(Small(label, _ => OpenLink(label, link), link));
        }
        if (_links.Children.Count == 0) _links.Children.Add(new TextBlock { Text = "No links yet — Settings → Integrations → Dashboards & SIEM links (Kibana, OpenSearch Dashboards, Microsoft Sentinel, Splunk…).", TextWrapping = TextWrapping.Wrap });
    }

    private void RefreshDashState()
    {
        var s = AppSettings.Current;
        _dashState.Text = s.GrafanaUrl.Length == 0 ? "No Grafana server is set (Settings → Integrations → Dashboards & SIEM links)."
            : _dashboards.Count == 0 ? $"Grafana: {s.GrafanaUrl}" : $"{_dashboards.Count} dashboard(s) from {s.GrafanaUrl}" + (s.GrafanaOrgId > 0 ? $" (org {s.GrafanaOrgId})" : "") + ". Double-click to open in the app's browser window.";
    }

    private async Task LoadDashboardsAsync()
    {
        var s = AppSettings.Current;
        if (s.GrafanaUrl.Length == 0) { RefreshDashState(); return; }
        _dashState.Text = "Asking Grafana…";
        try
        {
            _dashboards = await GrafanaClient.DashboardsAsync(s.GrafanaUrl, s.ObserveSecrets.GetValueOrDefault("grafana"), s.GrafanaOrgId);
            _dash.ItemsSource = _dashboards.Select(d => new DashRow(d)).ToList();
            RefreshDashState();
        }
        catch (Exception ex) { _dashState.Text = "✕ " + Redaction.Mask(ex.Message); }
    }

    private void OpenSelected()
    {
        if (_dash.SelectedItem is not DashRow r) return;
        if (GrafanaClient.DashboardUrl(AppSettings.Current.GrafanaUrl, r.D.Url) is { } u) WebWindow.Open(r.D.Title, u, "grafana:" + r.D.Uid);
    }

    private static void OpenLink(string label, string url)
    {
        if (GrafanaClient.ValidateUrl(url) is { } why) { Dialogs.Error("Dashboards", $"{label}: {why}."); return; }
        WebWindow.Open(label, url, "observe:" + label);
    }

    /// <summary>"Label=https://…" → (label, url); (null, "") when it isn't that shape.</summary>
    public static (string? Label, string Url) ParseLink(string raw)
    {
        int eq = raw.IndexOf('=');
        if (eq <= 0 || eq >= raw.Length - 1) return (null, "");
        var label = raw[..eq].Trim(); var url = raw[(eq + 1)..].Trim();
        return label.Length is 0 or > 40 || label.Any(char.IsControl) ? (null, "") : (label, url);
    }

    private Border Card(string title, params UIElement[] children)
    {
        var p = new StackPanel();
        p.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("CardTitle") });
        foreach (var c in children) p.Children.Add(c);
        return new Border { Style = (Style)FindResource("Card"), Child = p, Margin = new Thickness(0, 0, 0, 12) };
    }

    private Button Small(string text, Func<Button, Task> click, string? tip = null)
    {
        var b = new Button { Content = text, Style = (Style)FindResource("SmallButton"), ToolTip = tip, Margin = new Thickness(0, 0, 6, 4) };
        b.Click += async (_, _) => { try { await click(b); } catch (Exception ex) { Dialogs.Error("Observability", ex.Message); } };
        return b;
    }
    private Button Small(string text, Action<Button> click, string? tip = null) => Small(text, b => { click(b); return Task.CompletedTask; }, tip);
    private static TextBlock Lbl(string t) => new() { Text = t, Style = (Style)Application.Current.Resources["Label"], Margin = new Thickness(0, 10, 0, 4) };

    // ---- self-test seams ----
    internal string SinksTextForTest => string.Join("\n", _sinks.Children.OfType<Grid>().Select(g => string.Join(" | ", g.Children.OfType<TextBlock>().Select(t => t.Text)))) + "\n" + _sinkSummary.Text;
    internal int BarCountForTest => _bars.Children.Count;
    internal string BarsTextForTest => _barsText.Text;
    internal int LinkButtonsForTest => _links.Children.OfType<Button>().Count();
    internal Task LoadDashboardsForTestAsync() => LoadDashboardsAsync();
    internal int DashboardCountForTest => _dashboards.Count;

    public void Dispose() { _timer.Stop(); LogShipper.StatusChanged -= OnStatus; }
}
