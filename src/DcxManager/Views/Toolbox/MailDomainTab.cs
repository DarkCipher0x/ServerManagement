using System.Collections.ObjectModel;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using DcxManager.Mail;
using DcxManager.Services;

namespace DcxManager.Views.Toolbox;

/// <summary>
/// Toolbox → Mail domain: everything MxToolbox checks for a domain, in one run — MX hosts (with reverse DNS and a
/// blacklist shortcut), SPF as a tree with the lookup count and, given a sending IP, the result, DMARC, DKIM (a selector
/// you name or a probe of common ones), MTA-STS / TLS-RPT / BIMI. DNS only (plus the MTA-STS policy over https); nothing
/// is sent to any mail server — see a host's Mail server checks for SMTP, TLS on 25/465/587 and the open-relay probe.
/// </summary>
public sealed class MailDomainTab : DockPanel
{
    private readonly ToolboxView _owner;
    private readonly TextBox _domain = ToolboxUi.Input(240, "", "example.com (an address or URL is trimmed to its domain)");
    private readonly TextBox _sender = ToolboxUi.Input(150, "", "Optional: a sending IP to evaluate against SPF");
    private readonly TextBox _selector = ToolboxUi.Input(120, "", "DKIM selector (s= in a DKIM-Signature); empty = probe common ones");
    private readonly TextBlock _status = ToolboxUi.Status();
    private readonly ObservableCollection<MailFinding> _findings = new();
    private readonly TextBox _out = ToolboxUi.Output();
    private CancellationTokenSource? _cts;

    public MailDomainTab(ToolboxView owner)
    {
        _owner = owner;
        var head = new StackPanel();
        head.Children.Add(ToolboxUi.Hint("One run checks MX, SPF (the include tree and the 10-lookup limit), DMARC, DKIM, MTA-STS, TLS-RPT, BIMI and the MX hosts' reverse DNS. Give a sending IP to see what SPF says about it."));
        var stop = ToolboxUi.Button("■ Stop"); stop.Click += (_, _) => _cts?.Cancel();
        var bl = ToolboxUi.Button("⛔ Blacklists for the MX…"); bl.Click += (_, _) => { if (_mxIps.Count > 0) _owner.CheckBlacklist(_mxIps[0]); else _status.Text = "Run the check first to find the MX addresses."; };
        head.Children.Add(ToolboxUi.Bar(ToolboxUi.Label("domain"), _domain, ToolboxUi.Label("sending IP"), _sender, ToolboxUi.Label("DKIM selector"), _selector,
            ToolboxUi.Run("✉ Check domain", CheckAsync, _status, primary: true), bl, stop));
        head.Children.Add(_status);
        SetDock(head, Dock.Top);
        Children.Add(head);
        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
        var list = ToolboxUi.Findings(_findings, this);
        Grid.SetRow(list, 0); Grid.SetRow(_out, 1); _out.Margin = new Thickness(0, 8, 0, 0);
        body.Children.Add(list); body.Children.Add(_out);
        Children.Add(body);
    }

    private readonly List<string> _mxIps = new();

    public async Task CheckAsync()
    {
        _cts?.Cancel(); _cts = new CancellationTokenSource(); var ct = _cts.Token;
        var domain = MailNet.CheckDomain(_domain.Text);
        IPAddress? sender = null;
        if (_sender.Text.Trim().Length > 0 && !IPAddress.TryParse(_sender.Text.Trim(), out sender)) throw new ArgumentException("The sending IP isn't an IP address.");
        _findings.Clear(); _mxIps.Clear(); _out.Text = "";
        var dns = SystemMailDns.Instance;
        var text = new System.Text.StringBuilder();

        _status.Text = $"{domain}: MX…";
        var mx = await dns.MxAsync(domain);
        if (mx.Count == 0) _findings.Add(new("bad", "MX", $"{domain} has no MX records — mail to it goes to its A record, or nowhere", "add MX records for your mail provider"));
        foreach (var m in mx)
        {
            ct.ThrowIfCancellationRequested();
            var addrs = await dns.AddressesAsync(m.Host.TrimEnd('.'));
            _mxIps.AddRange(addrs.Select(a => a.ToString()));
            _findings.Add(new(addrs.Count > 0 ? "good" : "bad", "MX", $"{m.Preference} {m.Host} → {(addrs.Count > 0 ? string.Join(", ", addrs) : "no address")}"));
            foreach (var a in addrs.Take(2)) _findings.Add(await MailPolicies.FcrDnsAsync(a, dns));
        }

        _status.Text = $"{domain}: SPF…";
        var spf = await SpfCheck.CheckAsync(domain, sender, dns);
        foreach (var f in spf.Findings) _findings.Add(f);
        text.AppendLine("SPF tree:").Append(SpfCheck.Tree(spf.Root)).AppendLine($"{spf.Lookups} DNS lookups, {spf.VoidLookups} void" + (spf.Result != null ? $" · {sender?.ToString() ?? "no sender"} → {spf.Result} {spf.Decided}" : "")).AppendLine();

        _status.Text = $"{domain}: DMARC…";
        var dmarc = await DmarcDkim.DmarcAsync(domain, dns);
        if (dmarc == null) _findings.Add(new("bad", "DMARC", $"no DMARC record on _dmarc.{domain}", $"add _dmarc.{domain} TXT \"v=DMARC1; p=none; rua=mailto:dmarc@{domain}\" to start monitoring"));
        else { foreach (var f in dmarc.Findings) _findings.Add(f); text.AppendLine("DMARC: " + dmarc.Record).AppendLine(); }

        _status.Text = $"{domain}: DKIM…";
        var selector = _selector.Text.Trim();
        var keys = selector.Length > 0 ? (await DmarcDkim.DkimAsync(domain, selector, dns) is { } one ? [one] : new List<DmarcDkim.DkimKey>()) : await DmarcDkim.ProbeAsync(domain, dns, ct);
        if (keys.Count == 0) _findings.Add(new(selector.Length > 0 ? "bad" : "info", "DKIM", selector.Length > 0 ? $"no DKIM key at {selector}._domainkey.{domain}" : $"no key found under the {DmarcDkim.CommonSelectors.Length} common selectors — give the selector from a DKIM-Signature header (s=)"));
        foreach (var k in keys) { foreach (var f in k.Findings) _findings.Add(f); text.AppendLine($"DKIM {k.Selector}: {k.Record}"); }

        _status.Text = $"{domain}: MTA-STS, TLS-RPT, BIMI…";
        foreach (var f in await MailPolicies.CheckAsync(domain, dns, ct: ct)) _findings.Add(f);

        _out.Text = text.ToString();
        var verdict = ToolboxUi.Verdict(_findings);
        _status.Text = $"{domain}: {verdict}.";
        ToolboxUi.Record("Mail domain", domain, verdict, ToolboxUi.Report(_findings) + "\n\n" + _out.Text);
    }

    public void Stop() => _cts?.Cancel();
    internal IReadOnlyList<MailFinding> FindingsForTest => _findings;
}
