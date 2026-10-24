using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DcxManager.Browser;
using DcxManager.CloudPc;
using DcxManager.Services;

namespace DcxManager.Views;

/// <summary>
/// Cloud PCs (0.6.4, ADR-083): Windows 365 / Azure Virtual Desktop through Microsoft's Windows App, which can't be
/// embedded. Without any set-up: open the installed Windows App, or the Windows App web client (every Cloud PC and
/// desktop you're assigned, signed in as you) in a tab of the app's browser. With an app registration (Settings →
/// Integrations → Cloud PCs): your Cloud PCs listed here, each connecting straight into a tab (Microsoft's direct launch
/// link) or the Windows App. Nothing is contacted until you click.
/// </summary>
public sealed class CloudPcView : UserControl
{
    private readonly StackPanel _list = new();
    private readonly TextBlock _state = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 8) };
    private readonly Button _refresh;
    private readonly Button _signOut;
    private List<CloudPcInfo> _pcs = new();

    /// <summary>Self-test: the Cloud PCs shown.</summary>
    internal IReadOnlyList<CloudPcInfo> Shown => _pcs;

    public CloudPcView()
    {
        var root = new StackPanel { Margin = new Thickness(18, 14, 18, 14) };
        root.Children.Add(new TextBlock { Text = "Cloud PCs", Style = (Style)FindResource("H1") });
        root.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 12),
            Text = "Windows 365 Cloud PCs and Azure Virtual Desktop, through Microsoft's Windows App. Sessions run in Microsoft's client (it can't be embedded): in a tab of this app's browser, or in the Windows App itself. Nothing is contacted until you click.",
        });

        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        bar.Children.Add(Btn("▶ Open the Windows App", OpenWindowsApp, "Starts the installed Windows App (multi-monitor, devices and the full experience)", primary: true));
        bar.Children.Add(Btn("🌐 Windows App in a tab", () => OpenWeb(CloudPcClient.WebClientHome, "Windows App"), "Microsoft's Windows App web client in a tab here: every Cloud PC and desktop assigned to you, signed in as you"));
        root.Children.Add(bar);

        root.Children.Add(new TextBlock { Text = "YOUR CLOUD PCS", Style = (Style)FindResource("Label"), Margin = new Thickness(0, 8, 0, 4) });
        _refresh = Btn("↻ List my Cloud PCs", () => _ = LoadAsync(), "Signs in to Microsoft Graph as you (the browser opens the first time) and lists the Cloud PCs assigned to you");
        _signOut = Btn("Sign out", () => { CloudPcClient.SignOut(); CloudPcClient.SignOutActions(); AuditLog.Write("you", "cloudpc", "signed out of Microsoft Graph"); _pcs.Clear(); _state.Text = ""; Render(); }, "Forgets the Microsoft Graph sign-in for Cloud PCs");
        var setUp = Btn("Set up…", () => { MainWindow.Instance?.Navigate("settings"); SettingsView.Current?.RevealCard("Cloud PCs"); }, "The tenant and app registration for listing your Cloud PCs (Settings → Integrations → Cloud PCs)");
        root.Children.Add(new WrapPanel { Children = { _refresh, _signOut, setUp } });
        root.Children.Add(_state);
        root.Children.Add(_list);
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Render();
    }

    private Button Btn(string text, Action click, string tip, bool primary = false)
    {
        var b = new Button { Content = text, ToolTip = tip, Margin = new Thickness(0, 0, 6, 4) };
        if (TryFindResource(primary ? "SmallPrimaryButton" : "SmallButton") is Style s) b.Style = s;
        b.Click += (_, _) => click();
        return b;
    }

    private void Render()
    {
        bool configured = CloudPcClient.Configured;
        _refresh.IsEnabled = configured;
        _signOut.Visibility = CloudPcClient.SignedIn ? Visibility.Visible : Visibility.Collapsed;
        if (!configured)
            _state.Text = "To list your Cloud PCs here, add your tenant and an app registration with the delegated Graph permission CloudPC.Read.All (admin consent) in Settings → Integrations → Cloud PCs. Until then, “Windows App in a tab” shows all of them.";
        else if (_pcs.Count == 0 && _state.Text.Length == 0 || _state.Text.StartsWith("To list"))
            _state.Text = CloudPcClient.SignedIn ? $"Signed in{(CloudPcClient.Account is { Length: > 0 } a ? " as " + a : "")}. Click ↻ to list your Cloud PCs." : "Click ↻ to sign in and list your Cloud PCs.";
        _list.Children.Clear();
        foreach (var pc in _pcs) _list.Children.Add(Card(pc));
    }

    private Border Card(CloudPcInfo pc)
    {
        var name = new TextBlock { Text = "☁ " + pc.Name, FontWeight = FontWeights.SemiBold };
        var meta = new TextBlock { Text = string.Join(" · ", new[] { pc.Plan, pc.Image, pc.Provisioning }.Where(x => x.Length > 0)), TextWrapping = TextWrapping.Wrap };
        meta.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var actions = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        actions.Children.Add(Btn("⌨ Connect in a tab", () => Connect(pc), "Opens this Cloud PC straight in a tab (the Windows App web client)", primary: true));
        actions.Children.Add(Btn("▶ Windows App", OpenWindowsApp, "Opens the installed Windows App"));
        actions.Children.Add(Btn("Copy link", () => { if (CloudPcClient.WebClientLink(pc.Id) is { } l) Menus.Clip(l); }, "Copies the direct launch link"));
        // 0.6.5: power actions (Settings → Integrations → Cloud PCs → allow actions, admin approval).
        bool on = CloudPcClient.ActionsAllowed;
        string off = "Cloud PC actions are off: Settings → Integrations → Cloud PCs (admin approval)";
        var restart = Btn("⟳ Restart", () => _ = ActAsync(pc, CloudPcAction.Restart), on ? "Restarts this Cloud PC (open sessions on it end)" : off);
        var rename = Btn("✎ Rename…", () => _ = ActAsync(pc, CloudPcAction.Rename), on ? "Changes the name this Cloud PC shows" : off);
        restart.IsEnabled = rename.IsEnabled = on;
        actions.Children.Add(restart);
        actions.Children.Add(rename);
        var admin = Menus.MoreButton(() => new object?[]
        {
            Menus.Item("Troubleshoot (health check)", () => _ = ActAsync(pc, CloudPcAction.Troubleshoot), enabled: on, tip: on ? "Checks the health of the Cloud PC and its session host (Windows 365 / Intune administrators)" : off),
            Menus.Sep(),
            Menus.Item("Power on (Frontline)", () => _ = ActAsync(pc, CloudPcAction.PowerOn), enabled: on, tip: on ? "Windows 365 Frontline only, administrators: allocates the Cloud PC and assigns its licence" : off),
            Menus.Item("Power off (Frontline)…", () => _ = ActAsync(pc, CloudPcAction.PowerOff), enabled: on, tip: on ? "Windows 365 Frontline only, administrators: deallocates the Cloud PC and revokes its licence at once" : off),
        }, "Admin ▾", "SmallButton");
        admin.Margin = new Thickness(0, 0, 6, 4);
        actions.Children.Add(admin);
        var card = new Border { Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 0, 8), BorderThickness = new Thickness(1), Child = new StackPanel { Children = { name, meta, actions } } };
        card.SetResourceReference(Border.BackgroundProperty, "Surface");
        card.SetResourceReference(Border.BorderBrushProperty, "Line");
        card.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) { e.Handled = true; Connect(pc); } };
        return card;
    }

    private async Task LoadAsync()
    {
        _refresh.IsEnabled = false;
        _state.Text = "Signing in to Microsoft Graph and listing your Cloud PCs…";
        try
        {
            _pcs = await CloudPcClient.ListAsync(new CancellationTokenSource(TimeSpan.FromMinutes(3)).Token);
            _state.Text = _pcs.Count == 0 ? "No Cloud PCs are assigned to you." : $"{_pcs.Count} Cloud PC(s){(CloudPcClient.Account is { Length: > 0 } a ? " for " + a : "")}. Double-click one to connect.";
            AuditLog.Write("you", "cloudpc", "Cloud PCs listed", _pcs.Count.ToString());
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.Net.Http.HttpRequestException or OfflineBlockedException or OperationCanceledException or System.Text.Json.JsonException)
        {
            _state.Text = "✕ " + ex.Message;
        }
        finally { Render(); }
    }

    /// <summary>One action on one Cloud PC, after a confirmation that says what it does; audited either way.</summary>
    private async Task ActAsync(CloudPcInfo pc, CloudPcAction a)
    {
        string? newName = null;
        switch (a)
        {
            case CloudPcAction.Restart:
                if (!Dialogs.Confirm("Restart Cloud PC", $"Restart “{pc.Name}”? Anyone connected to it is disconnected and unsaved work there is lost.", "Restart", danger: true)) return;
                break;
            case CloudPcAction.Rename:
                newName = Dialogs.Prompt("Rename Cloud PC", $"New name for “{pc.Name}”:", pc.Name);
                if (newName == null || newName.Trim() == pc.Name) return;
                if (CloudPcClient.NameProblem(newName) is { } why) { Dialogs.Error("Rename Cloud PC", "The name: " + why + ".", explain: false); return; }
                break;
            case CloudPcAction.Troubleshoot:
                if (!Dialogs.Confirm("Troubleshoot Cloud PC", $"Run Microsoft's health check on “{pc.Name}” and its session host? (Windows 365 / Intune administrators.)", "Troubleshoot")) return;
                break;
            case CloudPcAction.PowerOn:
                if (!Dialogs.Confirm("Power on Cloud PC", $"Power on “{pc.Name}”? (Windows 365 Frontline, administrators: it is allocated and its licence assigned.)", "Power on")) return;
                break;
            case CloudPcAction.PowerOff:
                if (!Dialogs.Confirm("Power off Cloud PC", $"Power off “{pc.Name}”? A Windows 365 Frontline Cloud PC is deallocated and its licence revoked immediately; anyone on it is disconnected.", "Power off", danger: true, typeToConfirm: pc.Name)) return;
                break;
        }
        _state.Text = $"{a} “{pc.Name}”: signing in to Microsoft Graph (the browser opens the first time) and asking Microsoft…";
        try
        {
            await CloudPcClient.RunAsync(a, pc.Id, new CancellationTokenSource(TimeSpan.FromMinutes(3)).Token, newName);
            AuditLog.Write("you", "cloudpc", "Cloud PC " + a.ToString().ToLowerInvariant(), pc.Name + (newName != null ? " → " + newName.Trim() : ""));
            _state.Text = a switch
            {
                CloudPcAction.Restart => $"✓ “{pc.Name}” is restarting (a few minutes).",
                CloudPcAction.Rename => $"✓ Renamed to “{newName!.Trim()}” (↻ shows it once Microsoft has updated it).",
                CloudPcAction.Troubleshoot => $"✓ Troubleshooting “{pc.Name}” started: the result shows in the Intune admin centre.",
                CloudPcAction.PowerOn => $"✓ “{pc.Name}” is powering on.",
                _ => $"✓ “{pc.Name}” is powering off.",
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or TimeoutException or System.Net.Http.HttpRequestException or OfflineBlockedException or OperationCanceledException)
        {
            AuditLog.Write("you", "cloudpc", "Cloud PC " + a.ToString().ToLowerInvariant() + " failed", pc.Name + ": " + ex.Message);
            _state.Text = "✕ " + ex.Message;
        }
    }

    /// <summary>A Cloud PC straight into a tab (Microsoft's direct launch link), with your sign-in name as the hint.</summary>
    private void Connect(CloudPcInfo pc)
    {
        if (CloudPcClient.WebClientLink(pc.Id, CloudPcClient.Account) is not { } link) return;
        AuditLog.Write("you", "cloudpc", "Cloud PC opened", pc.Name);
        OpenWeb(link, pc.Name);
    }

    /// <summary>In a tab of the app's browser (its own "Cloud PCs" profile, so the Microsoft sign-in stays separate), or the
    /// default browser while the portal browser is off.</summary>
    private static void OpenWeb(string url, string title)
    {
        if (PortalBrowser.Enabled)
        {
            PortalUi.Open(new PortalBookmark { Title = title, Url = url, Profile = "Cloud PCs" });
            return;
        }
        if (Dialogs.Confirm("Cloud PCs", "The app's browser is off (Settings → Integrations → Portal browser), so this opens in your default browser.", "Open in my browser"))
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private static void OpenWindowsApp()
    {
        try
        {
            if (CloudPcClient.LaunchWindowsApp()) { AuditLog.Write("you", "cloudpc", "Windows App started"); return; }
            if (Dialogs.Confirm("Windows App", "The Windows App isn't installed. Get it from the Microsoft Store?", "Open the Store")) CloudPcClient.OpenStore();
        }
        catch (Exception ex) { Dialogs.Error("Windows App", "Couldn't start the Windows App: " + ex.Message, explain: false); }
    }
}
