using System.Text;
using System.Windows;
using System.Windows.Controls;
using DcxManager.Net;
using DcxManager.Services;

namespace DcxManager.Views.Toolbox;

/// <summary>
/// Toolbox → Web &amp; TLS: an HTTP(S) check (every redirect hop with its status and time, the final headers, which
/// security headers are missing, the certificate as Windows validated it) and a TLS certificate check of any port —
/// after STARTTLS for SMTP / IMAP / POP3 / FTP when chosen — showing names, days left, chain, protocol and cipher, and
/// plainly whether Windows trusts it. Inspection only: nothing but a GET or a handshake is sent; no cookies, no logins.
/// </summary>
public sealed class WebTab : DockPanel
{
    private readonly TextBox _url = ToolboxUi.Input(360, "", "http(s)://host/path — no user names or passwords");
    private readonly TextBox _host = ToolboxUi.Input(220, "", "Host name or IP");
    private readonly TextBox _port = ToolboxUi.Input(60, "443");
    private readonly TextBox _sni = ToolboxUi.Input(180, "", "Server name sent in the handshake (SNI); empty = the host");
    private readonly ComboBox _starttls = new() { Width = 90, ToolTip = "Start plain and upgrade: smtp (25 / 587), imap (143), pop3 (110), ftp (21)" };
    private readonly ComboBox _from = ToolboxUi.FromPicker();
    private readonly TextBlock _status = ToolboxUi.Status();
    private readonly TextBox _out = ToolboxUi.Output();

    public WebTab()
    {
        foreach (var k in NetTests.StartTlsKinds) _starttls.Items.Add(k);
        _starttls.SelectedIndex = 0;
        var head = new StackPanel();
        head.Children.Add(ToolboxUi.Hint("HTTP: follows each redirect by hand (so you see every hop), reports time to headers, the server and security headers, and the certificate. TLS: the certificate any port presents — expiry, names, chain, protocol and cipher — even an untrusted one, which is said so."));
        var pinHttp = ToolboxUi.Button("📌 Pin"); pinHttp.ToolTip = "Pin an HTTP check of this address to the dashboard";
        pinHttp.Click += (_, _) => PinCheckDialog.Show(Window.GetWindow(this), kind: "http", target: _url.Text.Trim());
        var pinTls = ToolboxUi.Button("📌 Pin"); pinTls.ToolTip = "Pin a certificate days-left check to the dashboard";
        pinTls.Click += (_, _) => PinCheckDialog.Show(Window.GetWindow(this), kind: "tls", target: _host.Text.Trim(), port: int.TryParse(_port.Text, out var pp) ? pp : 443);
        head.Children.Add(ToolboxUi.Bar(ToolboxUi.Label("URL"), _url, _from, ToolboxUi.Run("🌐 HTTP check", HttpAsync, _status, primary: true), pinHttp));
        head.Children.Add(ToolboxUi.Bar(ToolboxUi.Label("host"), _host, ToolboxUi.Label("port"), _port, ToolboxUi.Label("STARTTLS"), _starttls, ToolboxUi.Label("SNI"), _sni, ToolboxUi.Run("🔒 TLS certificate", TlsAsync, _status), pinTls));
        head.Children.Add(_status);
        SetDock(head, Dock.Top);
        Children.Add(head);
        Children.Add(_out);
    }

    public async Task HttpAsync()
    {
        var url = NetTests.CheckUrl(_url.Text).ToString();
        if (ToolboxUi.From(_from) is { } h)
        {
            _status.Text = $"curl from {h.Display}…";
            _out.Text = await ToolboxUi.RemoteAsync(h, NetTests.RemoteCommand("http", url), CancellationToken.None);
            _status.Text = $"Checked from {h.Display}.";
            ToolboxUi.Record("HTTP", $"{url} from {h.Display}", _out.Text.Split('\n').FirstOrDefault() ?? "", _out.Text);
            return;
        }
        _status.Text = $"GET {url}…";
        var r = await NetTests.HttpAsync(url);
        var sb = new StringBuilder();
        foreach (var hop in r.Hops) sb.AppendLine($"{hop.Status} {hop.Reason,-20} {hop.Ms,7:0} ms  {hop.Url}{(hop.Location != null ? "  →  " + hop.Location : "")}");
        if (r.Error != null) sb.AppendLine("✕ " + r.Error);
        if (r.Headers.Count > 0)
        {
            sb.AppendLine().AppendLine("Security headers:");
            foreach (var (name, present) in NetTests.SecurityHeaders(r.Headers)) sb.AppendLine($"  {(present ? "✓" : "✕")} {name}{(present ? ": " + Short(r.Headers[name]) : "  (missing)")}");
            sb.AppendLine().AppendLine("Headers:");
            foreach (var kv in r.Headers.OrderBy(k => k.Key)) sb.AppendLine($"  {kv.Key}: {Short(kv.Value)}");
        }
        if (r.Certificate is { } c) sb.AppendLine().Append(Describe(c));
        _out.Text = sb.ToString();
        var last = r.Hops.LastOrDefault();
        _status.Text = r.Error != null ? "✕ " + r.Error : $"{last?.Status} after {r.Hops.Count - 1} redirect(s); {NetTests.SecurityHeaders(r.Headers).Count(x => !x.Present)} security header(s) missing.";
        ToolboxUi.Record("HTTP", url, _status.Text, _out.Text);
    }

    public async Task TlsAsync()
    {
        var host = NetTests.CheckHost(_host.Text);
        var port = int.TryParse(_port.Text.Trim(), out var p) && p is >= 1 and <= 65535 ? p : throw new ArgumentException("A port from 1 to 65535.");
        var kind = _starttls.SelectedItem as string ?? "none";
        if (ToolboxUi.From(_from) is { } h)
        {
            _status.Text = $"openssl s_client from {h.Display}…";
            _out.Text = await ToolboxUi.RemoteAsync(h, NetTests.RemoteCommand("tls", host, port, "A", kind), CancellationToken.None);
            _status.Text = $"Checked from {h.Display}.";
            ToolboxUi.Record("TLS certificate", $"{host}:{port} from {h.Display}", _out.Text.Split('\n').FirstOrDefault() ?? "", _out.Text);
            return;
        }
        _status.Text = $"TLS handshake with {host}:{port}{(kind != "none" ? " after " + kind.ToUpperInvariant() + " STARTTLS" : "")}…";
        var c = await NetTests.TlsAsync(host, port, kind, _sni.Text.Trim().Length > 0 ? _sni.Text.Trim() : null);
        _out.Text = Describe(c);
        _status.Text = $"{(c.Trusted ? "✓ trusted" : "✕ NOT trusted (" + c.Errors + ")")} · {c.DaysLeft} days left · {c.Protocol} · {c.Cipher}";
        ToolboxUi.Record("TLS certificate", $"{host}:{port}", _status.Text, _out.Text);
    }

    public static string Describe(NetTests.CertInfo c)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Certificate:");
        sb.AppendLine($"  subject   {c.Subject}");
        sb.AppendLine($"  issuer    {c.Issuer}");
        sb.AppendLine($"  valid     {c.NotBefore:yyyy-MM-dd} → {c.NotAfter:yyyy-MM-dd}  ({(c.DaysLeft < 0 ? "EXPIRED " + -c.DaysLeft + " days ago" : c.DaysLeft + " days left" + (c.DaysLeft < 14 ? "  ⚠ renew now" : c.DaysLeft < 30 ? "  ⚠ renew soon" : ""))})");
        sb.AppendLine($"  names     {(c.Names.Count > 0 ? string.Join(", ", c.Names) : "(none)")}");
        sb.AppendLine($"  sha256    {c.Sha256}");
        sb.AppendLine($"  trusted   {(c.Trusted ? "yes (Windows)" : "NO — " + c.Errors)}");
        if (c.Chain.Count > 0) sb.AppendLine($"  chain     {string.Join("  ←  ", c.Chain)}");
        if (c.Protocol != null) sb.AppendLine($"  protocol  {c.Protocol} · {c.Cipher}");
        return sb.ToString();
    }

    private static string Short(string s) => s.Length > 200 ? s[..200] + "…" : s;
    internal void SetForTest(string url, string host, string port) { _url.Text = url; _host.Text = host; _port.Text = port; _from.SelectedIndex = 0; _sni.Text = "selftest.local"; }
    internal string OutputForTest => _out.Text;
}
