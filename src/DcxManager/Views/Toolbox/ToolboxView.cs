using System.Windows;
using System.Windows.Controls;

namespace DcxManager.Views.Toolbox;

/// <summary>
/// Network &amp; mail toolbox (0.3.0 §18): the everyday checks in one place, each tab its own file — Network (TCP port /
/// banner, traceroute, mtr, path MTU), DNS (every record type, propagation across resolvers), Web &amp; TLS (redirect
/// chain, security headers, certificate; TLS with STARTTLS), Mail domain (MX, SPF tree, DMARC, DKIM, MTA-STS, TLS-RPT,
/// BIMI, reverse DNS), Blacklists, Headers (pasted email headers, analysed locally), WHOIS &amp; ASN, and the History of
/// runs (encrypted). Network tests run from this PC or from a saved server over SSH.
/// </summary>
public sealed class ToolboxView : UserControl, IDisposable
{
    private readonly ContentControl _body = new();
    private readonly Dictionary<string, (RadioButton Tab, FrameworkElement View)> _tabs = new();
    public NetworkTab Network { get; }
    public DnsTab Dns { get; }
    public WebTab Web { get; }
    public MailDomainTab MailDomain { get; }
    public BlacklistTab Blacklist { get; }
    public HeadersTab Headers { get; }
    public WhoisTab Whois { get; }
    public HistoryTab History { get; }

    public static readonly string[] TabNames = ["Network", "DNS", "Web & TLS", "Mail domain", "Blacklists", "Headers", "WHOIS & ASN", "History"];

    public ToolboxView()
    {
        var root = new DockPanel { Margin = new Thickness(14, 12, 14, 12) };
        var head = new StackPanel();
        head.Children.Add(new TextBlock { Text = "Network & mail toolbox", Style = (Style)FindResource("H1") });
        head.Children.Add(new TextBlock { Text = "Port, route, DNS, web, certificate and mail-domain checks — from this PC or from a saved server. Results are kept in the encrypted history; only the question leaves this PC (pasted headers never do).", Style = (Style)FindResource("Hint"), Margin = new Thickness(0, 4, 0, 8), TextWrapping = TextWrapping.Wrap });
        var strip = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        head.Children.Add(strip);
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);
        root.Children.Add(_body);
        Content = root;

        Network = new NetworkTab(); Dns = new DnsTab(); Web = new WebTab(); MailDomain = new MailDomainTab(this); Blacklist = new BlacklistTab();
        Headers = new HeadersTab(); Whois = new WhoisTab(); History = new HistoryTab();
        var views = new FrameworkElement[] { Network, Dns, Web, MailDomain, Blacklist, Headers, Whois, History };
        for (int i = 0; i < TabNames.Length; i++)
        {
            var view = views[i];
            var rb = new RadioButton { Content = TabNames[i], GroupName = "toolbox-tabs", Style = (Style)FindResource("TabStripItem") };
            rb.Checked += (_, _) => { _body.Content = view; if (view is HistoryTab h) h.Reload(); };
            strip.Children.Add(rb);
            _tabs[TabNames[i]] = (rb, view);
        }
        Show("Network");
    }

    public void Show(string tab) { if (_tabs.TryGetValue(tab, out var t)) { t.Tab.IsChecked = true; _body.Content = t.View; } }

    /// <summary>Another tab asks for a blacklist check of an address (a mail domain's MX, for instance).</summary>
    public void CheckBlacklist(string target) { Blacklist.SetTarget(target); Show("Blacklists"); }

    public void Dispose() { Network.Stop(); Dns.Stop(); MailDomain.Stop(); Blacklist.Stop(); }
}
