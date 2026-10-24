using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using DcxManager.Mail;
using DcxManager.Services;

namespace DcxManager.Views;

/// <summary>
/// Mail server checks for one saved host (0.3.0 §8): Postfix over SSH (service, settings against good practice, queue,
/// 24 hours of warnings), a test message through the server's own sendmail, and from this PC TLS on 25 / 465 / 587, an
/// open-relay probe (this host only; it never sends mail) and the domain's MX, SPF, DMARC and DKIM records. Results stay
/// on screen only (nothing is stored); the audit log records that a check ran.
/// </summary>
public sealed class MailCheckView : UserControl
{
    private readonly HostEntry _host;
    private readonly ObservableCollection<MailFinding> _findings = new();
    private readonly TextBox _domain = new() { Width = 220, Padding = new Thickness(5, 4, 5, 4) };
    private readonly TextBox _selector = new() { Width = 120, Padding = new Thickness(5, 4, 5, 4), ToolTip = "The DKIM selector your mail is signed with (e.g. default, mail, s1); empty = skip DKIM" };
    private readonly TextBox _log = new() { IsReadOnly = true, TextWrapping = TextWrapping.NoWrap, Height = 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) };

    public MailCheckView(HostEntry host)
    {
        _host = host;
        _domain.Text = host.Address.Count(c => c == '.') >= 2 && !System.Net.IPAddress.TryParse(host.Address, out _) ? string.Join('.', host.Address.Split('.')[1..]) : host.Address;
        var root = new DockPanel { Margin = new Thickness(14, 12, 14, 12) };
        var head = new StackPanel();
        head.Children.Add(new TextBlock { Text = "Mail server checks — " + host.Display, Style = (Style)FindResource("H1") });
        head.Children.Add(new TextBlock { Text = "Postfix over SSH (read-only), and from this PC: TLS on 25 / 465 / 587, an open-relay check that never sends mail, and the domain's DNS records. Results are shown, not stored.", Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 10) });
        Button B(string t, Func<Task> a, bool primary = false) { var b = new Button { Content = t, Margin = new Thickness(0, 0, 6, 4) }; if (primary) b.Style = (Style)FindResource("PrimaryButton"); b.Click += async (_, _) => { b.IsEnabled = false; try { await a(); } finally { b.IsEnabled = true; } }; return b; }
        var bar = new WrapPanel();
        bar.Children.Add(B("▶ Check Postfix (SSH)", ServerAsync, primary: true));
        bar.Children.Add(B("✉ Send a test message…", TestMailAsync));
        bar.Children.Add(B("🔒 TLS on 25 / 465 / 587", PortsAsync));
        bar.Children.Add(B("⚠ Open-relay check", RelayAsync));
        head.Children.Add(bar);
        var dns = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        dns.Children.Add(new TextBlock { Text = "domain", Style = (Style)FindResource("Label"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        dns.Children.Add(_domain);
        dns.Children.Add(new TextBlock { Text = "DKIM selector", Style = (Style)FindResource("Label"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 6, 0) });
        dns.Children.Add(_selector);
        var d = B("⌕ Check DNS (MX, SPF, DMARC, DKIM)", DnsAsync); d.Margin = new Thickness(12, 0, 0, 0);
        dns.Children.Add(d);
        head.Children.Add(dns);
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        head.Children.Add(_status);
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        var bottom = new StackPanel();
        bottom.Children.Add(new TextBlock { Text = "MAIL LOG (last 24 hours, warnings and errors)", Style = (Style)FindResource("Label"), Margin = new Thickness(0, 8, 0, 4) });
        _log.Mono();
        bottom.Children.Add(_log);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);

        var list = new ListView { ItemsSource = _findings };
        var gv = new GridView();
        void Col(string h, string p, double w) => gv.Columns.Add(new GridViewColumn { Header = h, Width = w, DisplayMemberBinding = new Binding(p) });
        Col("", "Glyph", 30); Col("Area", "Area", 110); Col("Finding", "Text", 520); Col("What to do", "Fix", 380);
        list.View = gv;
        Menus.ForList(list, this, () => list.SelectedItem is not MailFinding f ? Array.Empty<object?>() : new object?[]
        {
            Menus.Item("Copy", () => Menus.Clip($"{f.Glyph} {f.Area}: {f.Text}" + (f.Fix != null ? $" — {f.Fix}" : ""))),
            Menus.Item("Copy the fix", () => Menus.Clip(f.Fix ?? ""), enabled: f.Fix != null),
        });
        root.Children.Add(list);
        Content = root;
    }

    private void Show(IEnumerable<MailFinding> f, string area)
    {
        foreach (var x in _findings.Where(x => x.Area.StartsWith(area) || area == "*").ToList()) _findings.Remove(x);
        foreach (var x in f) _findings.Add(x);
    }

    private async Task ServerAsync()
    {
        _status.Text = "Checking Postfix over SSH…";
        try
        {
            var r = await PostfixCheck.RunAsync(_host);
            foreach (var a in new[] { "Postfix", "Service", "TLS", "Relay", "Hygiene", "Exposure", "Queue", "Log" }) Show([], a);
            foreach (var f in r.Findings) _findings.Add(f);
            _log.Text = Redaction.Mask(string.Join("\n", r.LogLines));
            if (r.Config.TryGetValue("mydomain", out var dom) && dom.Length > 0) _domain.Text = dom;
            _status.Text = r.Installed ? $"Postfix {r.Version} · {r.State} · {r.Queued} queued" : "Postfix isn't installed on this host.";
            AuditLog.Write("you", "mail checks", "Postfix checked over SSH", _host.Display);
        }
        catch (Exception ex) { _status.Text = "✕ " + ex.Message; }
    }

    private async Task TestMailAsync()
    {
        if (Dialogs.Prompt("Send a test message", "Send to (an address you own):") is not { Length: > 0 } to) return;
        var from = $"postmaster@{_domain.Text.Trim()}";
        if (Dialogs.Prompt("Send a test message", "From:", from) is not { Length: > 0 } f) return;
        if (!Envs.Confirm(_host, "Send a test message", $"Send one test e-mail through {_host.Display}'s sendmail\nfrom {f}\nto {to}?", "Send", danger: false)) return;
        try
        {
            var r = await Ssh.SshPool.RunAsync(_host, PostfixCheck.TestMailScript(f, to.Trim(), "Test from DarkCipher ServerManagement"));
            _status.Text = r.Output.Contains("sent") ? $"✓ Handed to Postfix for {to}. Check the inbox (and Check Postfix → queue / log if it doesn't arrive)." : "✕ " + (r.Error.Length > 0 ? r.Error : r.Output);
            AuditLog.Write("you", "mail checks", "test message sent", $"{_host.Display} → {to}");
        }
        catch (Exception ex) { _status.Text = "✕ " + ex.Message; }
    }

    private async Task PortsAsync()
    {
        _status.Text = "Checking TLS on 25, 465 and 587…";
        var results = await Task.WhenAll(new[] { 25, 465, 587 }.Select(p => MailProbe.PortAsync(_host.Address, p)));
        Show(results.Select(r => new MailFinding(
            !r.Open ? (r.Port == 25 ? "warn" : "info") : r.Note.StartsWith('✓') ? "good" : r.Note.StartsWith('✕') ? "bad" : "warn",
            $"TLS {r.Port}", r.Open ? $"{r.Tls} · {r.Certificate}" + (r.Expires is { } x ? $" · expires {x:yyyy-MM-dd}" : "") + $" · {r.Note}" + (r.Banner.Length > 0 ? $" · {r.Banner}" : "") : r.Note)), "TLS ");
        _status.Text = "TLS checked from this PC.";
        AuditLog.Write("you", "mail checks", "TLS on 25/465/587 checked", _host.Display);
    }

    private async Task RelayAsync()
    {
        if (!Dialogs.Confirm("Open-relay check", $"Connect to {_host.Display} on port 25 and ask it to accept mail from example.com for example.net (reserved domains, nothing is delivered: the session is reset before any message).\n\nOnly check servers you run.", "Check")) return;
        _status.Text = "Checking for an open relay…";
        try
        {
            var (open, detail) = await MailProbe.RelayAsync(_host.Address);
            Show([new MailFinding(open ? "bad" : "good", "Relay (probe)", detail, open ? "add reject_unauth_destination to smtpd_relay_restrictions and check mynetworks" : null)], "Relay (probe)");
            _status.Text = open ? "✕ The server relays mail for strangers." : "✓ Not an open relay.";
            AuditLog.Write("you", "mail checks", "open-relay check", $"{_host.Display}: {(open ? "OPEN" : "refused")}");
        }
        catch (Exception ex) { _status.Text = "✕ " + ex.Message + " (outbound port 25 may be blocked by your network)"; }
    }

    private async Task DnsAsync()
    {
        var domain = _domain.Text.Trim().TrimEnd('.');
        if (domain.Length == 0) return;
        _status.Text = $"Looking up {domain}…";
        try
        {
            var r = await MailProbe.DnsAsync(domain, _selector.Text.Trim());
            foreach (var a in new[] { "MX", "SPF", "DMARC", "DKIM" }) Show([], a);
            foreach (var f in r.Findings) _findings.Add(f);
            _status.Text = $"DNS for {domain} checked.";
        }
        catch (Exception ex) { _status.Text = "✕ " + ex.Message; }
    }

    internal IReadOnlyList<MailFinding> FindingsForTest => _findings;
}
