using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DcxManager.Net;
using DcxManager.Services;

namespace DcxManager.Views.Lan;

/// <summary>
/// LAN discovery → Connection (ncat-style): connect to host:port over TCP or TLS and talk to it line by line. Client only —
/// there is no listening mode, so nothing is exposed and no firewall prompt appears.
/// </summary>
public sealed class ConnectTab : DockPanel
{
    private readonly TextBox _host = new() { Width = 220, Padding = new Thickness(5, 4, 5, 4) };
    private readonly TextBox _port = new() { Width = 70, Padding = new Thickness(5, 4, 5, 4), Text = "80" };
    private readonly CheckBox _tls = new() { Content = "TLS" };
    private readonly CheckBox _inspect = new() { Content = "Accept any certificate (inspect only)", ToolTip = "Connect even when the certificate isn't trusted; it is shown and the session is marked UNTRUSTED" };
    private readonly ComboBox _ending = new() { Width = 120 };
    private readonly TextBox _output = new() { IsReadOnly = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true };
    private readonly TextBox _input = new() { Padding = new Thickness(5, 4, 5, 4) };
    private readonly Button _connect;
    private NetConnect? _conn;

    public ConnectTab()
    {
        _ending.Items.Add("CR LF (\\r\\n)"); _ending.Items.Add("LF (\\n)"); _ending.Items.Add("nothing");
        _ending.SelectedIndex = 0;
        _connect = LanUi.Button("⇄ Connect", primary: true);
        var close = LanUi.Button("✕ Close");
        _connect.Click += async (_, _) => await OpenAsync();
        close.Click += async (_, _) => await CloseAsync();
        var head = new StackPanel();
        head.Children.Add(new TextBlock { Text = "Talk to a TCP or TLS service by hand: read a banner, try SMTP or HTTP commands, check a certificate. Client only — nothing listens.", Style = (Style)FindResource("Hint"), Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap });
        head.Children.Add(LanUi.Bar(LanUi.Label("host"), _host, LanUi.Label("port"), _port, _tls, _inspect, _connect, close));
        SetDock(head, Dock.Top);
        Children.Add(head);
        var send = LanUi.Button("Send");
        send.Click += async (_, _) => await SendAsync();
        _input.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await SendAsync(); } };
        var bottom = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        SetDock(send, Dock.Right); SetDock(_ending, Dock.Right);
        bottom.Children.Add(send); bottom.Children.Add(_ending); bottom.Children.Add(_input);
        SetDock(bottom, Dock.Bottom);
        Children.Add(bottom);
        _output.Mono();
        Children.Add(_output);
    }

    public void Prefill(string host, int port, bool tls) { _host.Text = host; _port.Text = port.ToString(); _tls.IsChecked = tls; }

    private void Append(string s)
    {
        _output.AppendText(s);
        if (_output.Text.Length > 200_000) _output.Text = _output.Text[^150_000..];
        _output.ScrollToEnd();
    }

    public async Task OpenAsync()
    {
        await CloseAsync();
        if (!int.TryParse(_port.Text.Trim(), out var port) || port is < 1 or > 65535) { Dialogs.Error("Connection", "A port from 1 to 65535."); return; }
        if (LanUi.Targets(_host.Text, "Connection") is null) return;
        _connect.IsEnabled = false;
        try
        {
            _conn = await NetConnect.OpenAsync(_host.Text.Trim(), port, _tls.IsChecked == true, _inspect.IsChecked == true, CancellationToken.None);
            var c = _conn;
            c.Received += s => Dispatcher.BeginInvoke(() => Append(Redaction.Mask(s)));
            c.Closed += why => Dispatcher.BeginInvoke(() => { Append($"\n── {why} ──\n"); if (_conn == c) _conn = null; });
            Append($"── {c.Describe} ──\n");
            AuditLog.Write("you", "LAN discovery", "connection opened", $"{_host.Text.Trim()}:{port}{(_tls.IsChecked == true ? " (TLS)" : "")}");
            _input.Focus();
        }
        catch (OfflineBlockedException ex) { Offline.Tell(ex); }
        catch (Exception ex) { Append($"── could not connect: {ex.Message} ──\n"); }
        finally { _connect.IsEnabled = true; }
    }

    private async Task SendAsync()
    {
        if (_conn is not { } c) { Append("── not connected ──\n"); return; }
        var line = _input.Text;
        _input.Clear();
        var ending = _ending.SelectedIndex switch { 1 => "\n", 2 => "", _ => "\r\n" };
        try { await c.SendLineAsync(line, ending); Append("> " + line + "\n"); }
        catch (Exception ex) { Append($"── send failed: {ex.Message} ──\n"); }
    }

    public async Task CloseAsync() { if (_conn is { } c) { _conn = null; await c.DisposeAsync(); } }
    internal string OutputForTest => _output.Text;
}
