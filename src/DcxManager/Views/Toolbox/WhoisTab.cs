using System.Net;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using DcxManager.Net;
using DcxManager.Services;

namespace DcxManager.Views.Toolbox;

/// <summary>
/// Toolbox → WHOIS &amp; ASN: who registered a domain (registrar, dates, name servers, status) or holds an IP block
/// (netname, organisation, country, abuse contact), following IANA's referral to the right registry; and the autonomous
/// system an IP belongs to (number, name, prefix, registry) through Team Cymru's DNS zones.
/// </summary>
public sealed class WhoisTab : DockPanel
{
    private readonly TextBox _target = ToolboxUi.Input(260, "", "A domain or a public IP");
    private readonly TextBlock _status = ToolboxUi.Status();
    private readonly TextBox _out = ToolboxUi.Output();

    public WhoisTab()
    {
        var head = new StackPanel();
        head.Children.Add(ToolboxUi.Hint("WHOIS goes to port 43 of IANA, then the registry or registrar it names (at most two referrals). ASN is two DNS TXT lookups. Both are public data; private addresses are refused."));
        head.Children.Add(ToolboxUi.Bar(ToolboxUi.Label("domain or IP"), _target, ToolboxUi.Run("⌕ WHOIS", WhoisAsync, _status, primary: true), ToolboxUi.Run("AS  ASN", AsnAsync, _status)));
        head.Children.Add(_status);
        SetDock(head, Dock.Top);
        Children.Add(head);
        Children.Add(_out);
    }

    public async Task WhoisAsync()
    {
        var target = _target.Text.Trim();
        _status.Text = $"WHOIS {target}…";
        var r = await Whois.LookupAsync(target);
        var sb = new StringBuilder();
        var last = r.Hops[^1];
        var summary = Whois.Summary(last.Text);
        if (summary.Count > 0) { sb.AppendLine("Summary:"); foreach (var (k, v) in summary) sb.AppendLine($"  {k,-40} {v}"); sb.AppendLine(); }
        foreach (var (server, text) in r.Hops) sb.AppendLine($"───── {server} ─────").AppendLine(text.Trim()).AppendLine();
        _out.Text = sb.ToString();
        _status.Text = $"{r.Hops.Count} server(s) asked, last {last.Server}.";
        ToolboxUi.Record("WHOIS", target, summary.Take(3).Select(s => $"{s.Key}: {s.Value}").DefaultIfEmpty(_status.Text).Aggregate((a, b) => a + " · " + b), _out.Text);
    }

    public async Task AsnAsync()
    {
        var target = _target.Text.Trim();
        if (!IPAddress.TryParse(target, out var ip))
        {
            var addrs = await Dns.GetHostAddressesAsync(NetTests.CheckHost(target));
            ip = addrs.FirstOrDefault() ?? throw new ArgumentException("That name has no address.");
        }
        _status.Text = $"ASN of {ip}…";
        var a = await Whois.AsnAsync(ip);
        _out.Text = a == null ? $"{ip}: no ASN found (unrouted, or not in Team Cymru's data)\n"
            : $"{ip}\n  AS{a.Asn}  {a.Name}\n  prefix    {a.Prefix}\n  country   {a.Country}\n  registry  {a.Registry}\n  allocated {a.Allocated}\n";
        _status.Text = a == null ? "No ASN found." : $"AS{a.Asn} {a.Name} ({a.Prefix})";
        ToolboxUi.Record("ASN", ip.ToString(), _status.Text, _out.Text);
    }
}
