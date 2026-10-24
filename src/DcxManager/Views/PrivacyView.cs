using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using DcxManager.Services;
using static DcxManager.Services.PrivacyReport;
using Endpoint = DcxManager.Services.PrivacyEndpoint;
using Level = DcxManager.Services.PrivacyLevel;
using Posture = DcxManager.Services.PrivacyPosture;

namespace DcxManager.Views;

/// <summary>
/// Privacy &amp; network: the app's security posture, every destination it is configured to reach (by network class)
/// and what is listening, at a glance. Read-only presentation of <see cref="PrivacyReport"/>; refreshed on open,
/// on demand and when tunnels change.
/// </summary>
public sealed class PrivacyView : UserControl, IDisposable
{
    private readonly WrapPanel _posture = new();
    private readonly ListView _endpoints = new() { MinHeight = 120 };
    private readonly ListView _ports = new() { MinHeight = 80 };
    private readonly TextBlock _stamp = new() { Margin = new Thickness(0, 6, 0, 0) };

    public PrivacyView()
    {
        var root = new StackPanel { Margin = new Thickness(16, 12, 16, 20) };
        root.Children.Add(new TextBlock { Text = "Privacy & network", Style = (Style)FindResource("H1") });
        root.Children.Add(new TextBlock
        {
            Text = "What this app protects, where it can send data, and what it listens on. ● local · ◆ on-premises · ◇ user-configured (can't be placed) · ⚠ internet. " +
                   "SSH, SFTP, Remote Desktop and tunnels to your own hosts are operational traffic you start yourself, not telemetry.",
            Style = (Style)FindResource("Hint"), Margin = new Thickness(0, 4, 0, 10),
        });

        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        Button B(string t, Action a, string? style = null) { var b = new Button { Content = t, Margin = new Thickness(0, 0, 6, 4) }; if (style != null) b.Style = (Style)FindResource(style); b.Click += (_, _) => a(); bar.Children.Add(b); return b; }
        B("↻ Refresh", Refresh, "PrimaryButton");
        B("Security settings", () => MainWindow.Instance.Navigate("settings"));
        if (AppModules.FindSettingsCard("AI engine") != null) B("AI engine settings", () => { MainWindow.Instance.Navigate("settings"); SettingsView.Current?.RevealCard("AI engine"); });
        B("Tunnels", () => MainWindow.Instance.Navigate("tunnels"));
        root.Children.Add(bar);

        root.Children.Add(Section("SECURITY & PRIVACY POSTURE"));
        root.Children.Add(_posture);

        // 0.3.0 §3: the syslog collector at a glance (DarkCipher0x: "a box for the syslog server in Privacy & Network").
        root.Children.Add(Section("SYSLOG (TLS)"));
        root.Children.Add(SyslogBox());

        root.Children.Add(Section("NETWORK ACTIVITY (configured destinations)"));
        var gv = new GridView();
        void Col(GridView g, string h, string p, double w) => g.Columns.Add(new GridViewColumn { Header = h, Width = w, DisplayMemberBinding = new Binding(p) });
        Col(gv, "Service", "Service", 250); Col(gv, "Destination", "Destination", 260); Col(gv, "Class", "ClassText", 250); Col(gv, "State", "State", 250); Col(gv, "Purpose", "Purpose", 340);
        _endpoints.View = gv;
        root.Children.Add(_endpoints);

        root.Children.Add(Section("LISTENING SERVICES"));
        var pv = new GridView();
        Col(pv, "Service", "Service", 260); Col(pv, "Bind", "Bind", 220); Col(pv, "Who can reach it", "Reach", 330); Col(pv, "State", "State", 100);
        _ports.View = pv;
        root.Children.Add(_ports);
        _stamp.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        root.Children.Add(_stamp);

        Menus.ForList(_endpoints, this, () => _endpoints.SelectedItem is not Row r ? Array.Empty<object?>() : new object?[]
        {
            Menus.Item("Copy destination", () => Menus.Clip(r.Destination)),
            Menus.Item("What does this class mean?", () => Dialogs.Info(r.ClassText, r.Class is { } c ? NetworkPolicy.Explain(c) : "No network destination.")),
        });
        Menus.ForList(_ports, this, () => _ports.SelectedItem is not Port p ? Array.Empty<object?>() : new object?[]
        {
            Menus.Item("Copy bind address", () => Menus.Clip(p.Bind)),
            Menus.Item("Open Tunnels", () => MainWindow.Instance.Navigate("tunnels")),
        });

        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Loaded += (_, _) => { Refresh(); AppData.Changed += OnData; };
        Unloaded += (_, _) => AppData.Changed -= OnData;
    }

    /// <summary>Row shape for the endpoint list (class shown as glyph + words, never colour alone).</summary>
    public sealed record Row(string Service, string Destination, NetClass? Class, string ClassText, string State, string Purpose);

    private void OnData() => Dispatcher.BeginInvoke(Refresh);

    private readonly TextBlock _syslog = new() { TextWrapping = TextWrapping.Wrap };

    /// <summary>State, server, TLS version and certificate expiry, last sent, queue, last error, and "Send test message".</summary>
    private Border SyslogBox()
    {
        var test = new Button { Content = "Send test message", Margin = new Thickness(0, 8, 6, 0) };
        var settings = new Button { Content = "Syslog settings", Margin = new Thickness(0, 8, 0, 0) };
        test.Click += async (_, _) => { test.IsEnabled = false; var r = await Notify.SyslogForwarder.SendTestAsync(); test.IsEnabled = true; ShowSyslog(r); };
        settings.Click += (_, _) => { MainWindow.Instance.Navigate("settings"); SettingsView.Current?.RevealCard("Syslog (TLS)"); };
        var p = new StackPanel();
        p.Children.Add(_syslog);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(test); row.Children.Add(settings);
        p.Children.Add(row);
        ShowSyslog();
        void Changed() => Dispatcher.BeginInvoke(() => ShowSyslog());
        Loaded += (_, _) => Notify.SyslogForwarder.StatusChanged += Changed;
        Unloaded += (_, _) => Notify.SyslogForwarder.StatusChanged -= Changed;
        return new Border { Style = (Style)FindResource("Card"), Child = p, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 520 };
    }

    /// <summary>The box's text (self-test reads it).</summary>
    internal string SyslogText => _syslog.Text;

    private void ShowSyslog(string? note = null)
    {
        var s = AppSettings.Current;
        var st = Notify.SyslogForwarder.Status;
        string Ago(DateTime? t) => t is { } v ? $"{v:yyyy-MM-dd HH:mm:ss}" : "never";
        _syslog.Text = !s.SyslogEnabled
            ? "● Off — nothing is sent. Set up a collector in Settings → Integrations → Syslog (TLS)." + (s.SyslogHost.Length > 0 ? $"\nServer: {s.SyslogHost}:{s.SyslogPort}" : "")
            : $"State: {st.State}\nServer: {s.SyslogHost}:{s.SyslogPort} · TLS only" + (st.Tls != null ? $" ({st.Tls})" : "") +
              (s.SyslogPin.Length > 0 ? " · certificate pinned" : " · Windows certificate trust") + (s.SyslogClientCert.Length > 0 ? " · client certificate" : "") +
              (st.Certificate != null ? $"\nCertificate: {st.Certificate}" + (st.CertExpires is { } x ? $", expires {x:yyyy-MM-dd}" + (x < DateTime.Now.AddDays(30) ? " ⚠ within 30 days" : "") : "") : "") +
              $"\nLast sent: {Ago(st.LastSent)} · queued: {st.Queued}" + (st.Dropped > 0 ? $" · dropped: {st.Dropped}" : "") +
              $"\nSending: {(s.SyslogSendAudit ? "audit log" : "")}{(s.SyslogSendAudit && s.SyslogSendAppLog ? " + " : "")}{(s.SyslogSendAppLog ? "application log" : "")}" +
              (st.LastError != null ? $"\nLast error ({Ago(st.LastErrorAt)}): {st.LastError}" : "");
        if (note != null) _syslog.Text = note + "\n" + _syslog.Text;
    }

    private TextBlock Section(string t) => new() { Text = t, Style = (Style)FindResource("Label"), Margin = new Thickness(0, 14, 0, 6) };

    public void Refresh()
    {
        _posture.Children.Clear();
        foreach (var p in PostureItems()) _posture.Children.Add(Tile(p));
        _endpoints.ItemsSource = Endpoints().Select(e => new Row(e.Service, e.Destination, e.Class,
            e.Class is { } c ? $"{NetworkPolicy.Glyph(c)} {NetworkPolicy.Label(c)}" : "—", e.State, e.Purpose)).ToList();
        var ports = Ports();
        _ports.ItemsSource = ports;
        _ports.Visibility = ports.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        _stamp.Text = (ports.Count == 0 ? "Nothing is listening: no tunnels are defined and no internal forwards are open. " : "") + $"Checked {DateTime.Now:HH:mm:ss}.";
    }

    private Border Tile(Posture p)
    {
        var glyph = p.Level switch { Level.Good => "✓", Level.Warn => "⚠", _ => "•" };
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = p.Name.ToUpperInvariant(), Style = (Style)FindResource("Label"), Margin = new Thickness(0, 0, 0, 2) });
        var v = new TextBlock { Text = $"{glyph} {p.Value}", FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        v.SetResourceReference(TextBlock.ForegroundProperty, p.Level == Level.Warn ? "Warn" : p.Level == Level.Good ? "Good" : "Fg");
        sp.Children.Add(v);
        var b = new Border { Child = sp, Width = 250, MinHeight = 56, Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 0, 8, 8), CornerRadius = new CornerRadius(3),
            BorderThickness = p.Level == Level.Warn ? new Thickness(4, 1, 1, 1) : new Thickness(1), ToolTip = p.Detail };
        b.SetResourceReference(Border.BackgroundProperty, "Surface");
        b.SetResourceReference(Border.BorderBrushProperty, p.Level == Level.Warn ? "Warn" : "Line");
        return b;
    }

    public void Dispose() => AppData.Changed -= OnData;
}
