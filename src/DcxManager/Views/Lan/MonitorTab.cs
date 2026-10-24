using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using DcxManager.Net;
using DcxManager.Services;

namespace DcxManager.Views.Lan;

/// <summary>
/// LAN discovery → Monitoring: scheduled re-scans you set up and can see (no silent polling). Each run compares with the
/// last one; changes go to the audit log (and so syslog) and hosts going down / new open ports to e-mail notifications.
/// </summary>
public sealed class MonitorTab : DockPanel
{
    private readonly LanScanView _owner;
    private readonly ListView _list = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) };

    public sealed record Row(string Name, string Targets, string Ports, string Every, string State, string Last, LanMonitor Monitor);

    public MonitorTab(LanScanView owner)
    {
        _owner = owner;
        var head = new StackPanel();
        head.Children.Add(new TextBlock { Text = "Re-scans on a schedule while the app is open. Each run is compared with the last: new open ports, ports that closed, hosts that appeared or stopped answering are written to the audit log (so they reach syslog) and hosts going down or new open ports are e-mailed when notifications are on.", Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        var add = LanUi.Button("＋ New monitor…", primary: true); var edit = LanUi.Button("✎ Edit…"); var run = LanUi.Button("▶ Run now"); var del = LanUi.Button("✕ Delete");
        add.Click += (_, _) => Edit(null);
        edit.Click += (_, _) => { if (Sel is { } m) Edit(m); };
        run.Click += async (_, _) => { if (Sel is { } m) { _status.Text = $"Running {m.Name}…"; _status.Text = $"{m.Name}: " + await LanMonitors.RunAsync(m); Reload(); } };
        del.Click += (_, _) =>
        {
            if (Sel is not { } m || !Dialogs.Confirm("Monitoring", $"Delete the monitor “{m.Name}”?", "Delete", danger: true)) return;
            AppData.Current.LanMonitors.Remove(m); AppData.Save(); AuditLog.Write("you", "LAN monitor", "monitor deleted", m.Name); Reload();
        };
        head.Children.Add(LanUi.Bar(add, edit, run, del));
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        head.Children.Add(_status);
        SetDock(head, Dock.Top);
        Children.Add(head);
        var gv = new GridView();
        void Col(string h, string p, double w) => gv.Columns.Add(new GridViewColumn { Header = h, Width = w, DisplayMemberBinding = new Binding(p) });
        Col("Name", "Name", 160); Col("Targets", "Targets", 200); Col("Ports", "Ports", 130); Col("Every", "Every", 80); Col("State", "State", 70); Col("Last run", "Last", 420);
        _list.View = gv;
        _list.MouseDoubleClick += (_, _) => { if (Sel is { } m) Edit(m); };
        Children.Add(_list);
        LanMonitors.Ran += OnRan;
        Reload();
    }

    private LanMonitor? Sel => (_list.SelectedItem as Row)?.Monitor;
    private void OnRan(LanMonitor _) => Reload();
    public void Detach() => LanMonitors.Ran -= OnRan;

    private void Reload()
    {
        var sel = Sel?.Id;
        var rows = AppData.Current.LanMonitors.Select(m => new Row(m.Name, m.Targets, m.Ports, $"{m.IntervalMinutes} min", m.Enabled ? "on" : "paused",
            m.LastRun is { } t ? $"{t:yyyy-MM-dd HH:mm} · {m.LastSummary}" : "not run yet", m)).ToList();
        _list.ItemsSource = rows;
        _list.SelectedItem = rows.FirstOrDefault(r => r.Monitor.Id == sel);
    }

    /// <summary>From Port tests → "Watch these".</summary>
    public void AddFrom(string targets, string ports) { Edit(new LanMonitor { Name = "Watch " + targets.Split(',')[0].Trim(), Targets = targets, Ports = ports }, isNew: true); _owner.Show("Monitoring"); }

    private void Edit(LanMonitor? m, bool isNew = false)
    {
        isNew |= m == null;
        m ??= new LanMonitor { Targets = Ssh.LanScanner.GuessSubnet() };
        var w = Dialogs.MakeWindow(isNew ? "New monitor" : "Edit monitor", 520);
        var p = new StackPanel { Margin = new Thickness(18) };
        TextBox F(string label, string value) { p.Children.Add(LanUi.Label(label)); var t = new TextBox { Text = value, Margin = new Thickness(0, 4, 0, 8) }; p.Children.Add(t); return t; }
        var name = F("NAME", m.Name); var targets = F("TARGETS (private ranges, addresses or names)", m.Targets); var ports = F("PORTS (common, 22,443, 8000-8100…)", m.Ports);
        var every = F("EVERY (minutes, 5 or more)", m.IntervalMinutes.ToString());
        var ping = new CheckBox { Content = "Also ping (a host with no answer to ping or any port counts as down)", IsChecked = m.Ping };
        var on = new CheckBox { Content = "Enabled", IsChecked = m.Enabled, Margin = new Thickness(0, 6, 0, 0) };
        p.Children.Add(ping); p.Children.Add(on);
        var ok = new Button { Content = "Save", Style = (Style)FindResource("PrimaryButton"), IsDefault = true, Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        p.Children.Add(ok);
        w.Content = p;
        ok.Click += (_, _) =>
        {
            try { var t = NetTargets.Parse(targets.Text); if (t.Addresses.Count + t.Names.Count == 0) throw new ArgumentException("No targets."); NetTargets.Ports(ports.Text); }
            catch (ArgumentException ex) { Dialogs.Error("Monitor", ex.Message, explain: false); return; }
            if (!int.TryParse(every.Text.Trim(), out var min) || min < 5 || min > 10080) { Dialogs.Error("Monitor", "Every 5 to 10,080 minutes."); return; }
            w.DialogResult = true;
            m.Name = name.Text.Trim().Length > 0 ? name.Text.Trim() : "LAN watch"; m.IntervalMinutes = min; m.Ping = ping.IsChecked == true; m.Enabled = on.IsChecked == true;
            if (m.Targets != targets.Text.Trim() || m.Ports != ports.Text.Trim()) { m.Targets = targets.Text.Trim(); m.Ports = ports.Text.Trim(); m.LastRun = null; m.OpenPorts.Clear(); m.UpHosts.Clear(); m.LastSummary = ""; }
        };
        if (w.ShowDialog() != true) return;
        if (isNew) AppData.Current.LanMonitors.Add(m);
        AppData.Save();
        AuditLog.Write("you", "LAN monitor", isNew ? "monitor created" : "monitor changed", $"{m.Name}: {m.Targets} · {m.Ports} · every {m.IntervalMinutes} min");
        Reload();
    }
}
