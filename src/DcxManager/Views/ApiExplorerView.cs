using DcxManager.Services;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DcxManager.Vultr;

namespace DcxManager.Views;

/// <summary>Call any Vultr API v2 endpoint — covers anything new or niche that has no dedicated screen.</summary>
public sealed class ApiExplorerView : UserControl
{
    private readonly ComboBox _method = new() { Width = 90, ItemsSource = new[] { "GET", "POST", "PATCH", "PUT", "DELETE", "OPTIONS" }, SelectedIndex = 0 };
    private readonly ComboBox _path = new() { IsEditable = true, Padding = new Thickness(5, 4, 5, 4) };
    private readonly TextBox _body = new() { AcceptsReturn = true, AcceptsTab = true, Padding = new Thickness(6, 4, 6, 4), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBox _result = new();
    private readonly TextBlock _status = new TextBlock() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) }.Fs(FontStep.Small);

    private static readonly string[] Examples =
    {
        "/account", "/account/bandwidth", "/instances", "/instances/{id}", "/instances/{id}/bandwidth", "/bare-metals", "/blocks", "/snapshots",
        "/backups", "/firewalls", "/firewalls/{id}/rules", "/domains", "/domains/{domain}/records", "/reserved-ips", "/vpcs", "/load-balancers",
        "/kubernetes/clusters", "/kubernetes/versions", "/databases", "/databases/plans", "/object-storage", "/object-storage/clusters", "/registries",
        "/ssh-keys", "/startup-scripts", "/users", "/billing/history", "/billing/invoices", "/billing/pending-charges", "/regions", "/plans", "/plans-metal",
        "/os", "/applications", "/iso", "/iso-public", "/cdns/pull-zones", "/cdns/push-zones", "/inference", "/marketplace/apps/{image_id}/variables",
    };

    public ApiExplorerView()
    {
        _path.Mono(); _body.Mono();
        _path.ItemsSource = Examples;
        _path.Text = "/account";
        _result.Style = (Style)FindResource("Mono");
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");

        var send = new Button { Content = "Send", Style = (Style)FindResource("PrimaryButton"), Margin = new Thickness(6, 0, 0, 0) };
        send.Click += async (_, _) => await SendAsync();
        var line = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(_method, Dock.Left); DockPanel.SetDock(send, Dock.Right);
        _method.Margin = new Thickness(0, 0, 6, 0);
        line.Children.Add(_method); line.Children.Add(send); line.Children.Add(_path);
        _path.KeyDown += async (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) await SendAsync(); };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(380) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var bodyPanel = new DockPanel();
        var bl = new TextBlock { Text = "REQUEST BODY (JSON)", Style = (Style)FindResource("Label") };
        DockPanel.SetDock(bl, Dock.Top);
        bodyPanel.Children.Add(bl); bodyPanel.Children.Add(_body);
        var resPanel = new DockPanel();
        var rl = new StackPanel { Orientation = Orientation.Horizontal };
        rl.Children.Add(new TextBlock { Text = "RESPONSE", Style = (Style)FindResource("Label") });
        rl.Children.Add(_status);
        DockPanel.SetDock(rl, Dock.Top);
        resPanel.Children.Add(rl); resPanel.Children.Add(_result);
        Grid.SetColumn(resPanel, 2);
        grid.Children.Add(bodyPanel); grid.Children.Add(resPanel);

        var root = new DockPanel { Margin = new Thickness(14, 12, 14, 12) };
        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        head.Children.Add(new TextBlock { Text = "Vultr API explorer", Style = (Style)FindResource("H1") });
        head.Children.Add(new TextBlock { Text = "Base URL https://api.vultr.com/v2 · your API key is added automatically · lists return all pages for GET. Reference: https://www.vultr.com/api/", Style = (Style)FindResource("Hint"), Margin = new Thickness(0, 4, 0, 0) });
        DockPanel.SetDock(head, Dock.Top); DockPanel.SetDock(line, Dock.Top);
        root.Children.Add(head); root.Children.Add(line); root.Children.Add(grid);
        Content = root;
    }

    private async Task SendAsync()
    {
        var v = VultrClient.Active;
        if (v == null) { _status.Text = "No Vultr account — Settings → Vultr accounts."; return; }
        var method = (string)_method.SelectedItem;
        var path = _path.Text.Trim();
        if (path.Contains('{')) { _status.Text = "Replace the {placeholders} in the path first."; return; }
        string? body = _body.Text.Trim().Length > 0 && method != "GET" ? _body.Text : null;
        if (body != null) { try { JsonDocument.Parse(body); } catch (JsonException ex) { _status.Text = "Body isn't valid JSON: " + ex.Message; return; } }
        if (method != "GET" && !Dialogs.Confirm("Send request", $"{method} {path} on {v.AccountName}?\n\nThis can change or delete real resources.", "Send", danger: method == "DELETE")) return;
        _status.Text = "…";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var r = await v.SendAsync(new HttpMethod(method), path, body);
            _result.Text = J.Pretty(r);
            _status.Text = $"✓ {sw.ElapsedMilliseconds} ms";
        }
        catch (CloudApiException ex) { _result.Text = ex.Message + (ex.RequestId != null ? $"\n\nRequest id: {ex.RequestId}" : ""); _status.Text = ex.Status > 0 ? $"✕ HTTP {ex.Status}" : "✕ no answer"; }
        catch (Exception ex) { _result.Text = ex.Message; _status.Text = "✕ error"; }
    }
}
