using System.Windows;
using System.Windows.Controls;
using DcxManager.Services;

namespace DcxManager.Views;

/// <summary>"Pin a check…" (0.3.0 §20): kind, target, port, warning threshold and interval for a dashboard tile. Also
/// edits an existing tile. Returns the saved tile or null.</summary>
public static class PinCheckDialog
{
    public static DashboardTile? Show(Window? owner, DashboardTile? existing = null, string? kind = null, string? target = null, int port = 0)
    {
        var w = Dialogs.MakeWindow(existing == null ? "Pin a check to the dashboard" : "Edit check", 480);
        if (owner != null) w.Owner = owner;
        var p = new StackPanel { Margin = new Thickness(18) };
        TextBlock L(string t) => new() { Text = t, Style = (Style)Application.Current.Resources["Label"], Margin = new Thickness(0, 8, 0, 4) };
        TextBox T(string v, string? tip = null) => new() { Text = v, Padding = new Thickness(5, 4, 5, 4), ToolTip = tip };
        var kinds = new ComboBox();
        foreach (var k in DashboardTile.Kinds) kinds.Items.Add(k switch { "ping" => "Ping (ICMP)", "tcp" => "TCP port (open?)", "http" => "HTTP(S) address (status + time)", "tls" => "TLS certificate (days left)", "perf" => "Performance of a saved host (SSH)", _ => "Mail domain (SPF + DMARC)" });
        kinds.SelectedIndex = Math.Max(0, Array.IndexOf(DashboardTile.Kinds, existing?.Kind ?? kind ?? "tcp"));
        var name = T(existing?.Name ?? "", "Empty = made from the kind and target");
        var tgt = T(existing?.Target ?? target ?? "", "Host / IP, an http(s) address, or a domain");
        var prt = T(existing?.Port is > 0 ? existing.Port.ToString() : port > 0 ? port.ToString() : "443");
        var host = new ComboBox { ItemsSource = AppData.Current.Hosts.Where(h => h.Protocol == "ssh").ToList(), DisplayMemberPath = "Display" };
        host.SelectedItem = AppData.Current.FindHost(existing?.HostId);
        var warn = T(existing != null ? (existing.Kind == "tls" ? existing.WarnDays : existing.WarnPercent).ToString() : "14", "tls: days left to warn at · perf: percent (memory, disk, load per core) to warn at");
        var every = new ComboBox();
        foreach (var m in new[] { 0, 5, 15, 30, 60, 240 }) every.Items.Add(m == 0 ? "only when the dashboard opens / Check now" : $"every {m} min while the dashboard is on screen");
        every.SelectedIndex = Math.Max(0, Array.IndexOf(new[] { 0, 5, 15, 30, 60, 240 }, existing?.IntervalMinutes ?? 15));
        var portRow = new StackPanel(); portRow.Children.Add(L("PORT")); portRow.Children.Add(prt);
        var hostRow = new StackPanel(); hostRow.Children.Add(L("SAVED HOST")); hostRow.Children.Add(host);
        var tgtRow = new StackPanel(); tgtRow.Children.Add(L("TARGET")); tgtRow.Children.Add(tgt);
        var warnRow = new StackPanel(); warnRow.Children.Add(L("WARN AT")); warnRow.Children.Add(warn);
        void Show()
        {
            var k = DashboardTile.Kinds[kinds.SelectedIndex];
            portRow.Visibility = k is "tcp" or "tls" ? Visibility.Visible : Visibility.Collapsed;
            hostRow.Visibility = k == "perf" ? Visibility.Visible : Visibility.Collapsed;
            tgtRow.Visibility = k == "perf" ? Visibility.Collapsed : Visibility.Visible;
            warnRow.Visibility = k is "tls" or "perf" ? Visibility.Visible : Visibility.Collapsed;
        }
        kinds.SelectionChanged += (_, _) => Show(); Show();
        p.Children.Add(L("KIND")); p.Children.Add(kinds);
        p.Children.Add(L("NAME (optional)")); p.Children.Add(name);
        p.Children.Add(tgtRow); p.Children.Add(portRow); p.Children.Add(hostRow); p.Children.Add(warnRow);
        p.Children.Add(L("HOW OFTEN")); p.Children.Add(every);
        p.Children.Add(new TextBlock { Text = "Checks run by the dashboard's rule (Checks ▾) — never in the background without it. A change of state goes to the audit log and e-mail notifications.", Style = (Style)Application.Current.Resources["Hint"], TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
        var ok = new Button { Content = existing == null ? "Pin" : "Save", Style = (Style)Application.Current.Resources["PrimaryButton"], IsDefault = true };
        var bs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        bs.Children.Add(ok); bs.Children.Add(new Button { Content = "Cancel", IsCancel = true });
        p.Children.Add(bs);
        w.Content = p;
        DashboardTile? result = null;
        ok.Click += (_, _) =>
        {
            var t = existing ?? new DashboardTile();
            t.Kind = DashboardTile.Kinds[kinds.SelectedIndex]; t.Name = name.Text.Trim(); t.Target = tgt.Text.Trim();
            t.Port = int.TryParse(prt.Text.Trim(), out var pn) ? pn : 0; t.HostId = (host.SelectedItem as HostEntry)?.Id;
            if (int.TryParse(warn.Text.Trim(), out var wn)) { if (t.Kind == "tls") t.WarnDays = wn; else t.WarnPercent = wn; }
            t.IntervalMinutes = new[] { 0, 5, 15, 30, 60, 240 }[Math.Max(0, every.SelectedIndex)];
            try { Checks.Validate(t); }
            catch (ArgumentException ex) { Dialogs.Error("Pin a check", ex.Message, explain: false); return; }
            result = existing == null ? Checks.Pin(t) : t;
            if (existing != null) { if (t.Name.Length == 0) t.Name = $"{t.KindTitle} {t.TargetText}"; AppData.Save(); }
            w.DialogResult = true;
        };
        return w.ShowDialog() == true ? result : null;
    }
}
