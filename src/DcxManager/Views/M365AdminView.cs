using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using DcxManager.Browser;
using DcxManager.Microsoft365;
using DcxManager.Services;

namespace DcxManager.Views;

/// <summary>
/// Microsoft 365 admin (0.6.6, ADR-084): Intune, Defender and Entra, read-only. Pick a list (Intune devices, Defender
/// incidents / alerts, Entra users / groups / devices / sign-ins / risky users), ↻ loads it (the first time per service,
/// the browser signs you in), the filter narrows it, double-click (or Open in portal) opens the item in the real admin
/// centre in a tab. What is read stays in memory only.
/// </summary>
public sealed class M365AdminView : UserControl
{
    private readonly ComboBox _pick = new() { Width = 260 };
    private readonly TextBox _filter = Hint.Set(new TextBox { Width = 260, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(8, 0, 0, 0) }, "Filter…");
    private readonly ListView _grid = new() { Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _state = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly Button _load;
    private AdminTable? _table;
    private readonly Dictionary<AdminList, AdminTable> _loaded = new();

    /// <summary>Self-test: the list shown and its rows after the filter.</summary>
    internal AdminTable? Table => _table;
    internal int ShownRows => _grid.Items.Count;
    internal void Pick(int i) => _pick.SelectedIndex = i;
    internal string Filter { get => _filter.Text; set => _filter.Text = value; }

    public M365AdminView()
    {
        foreach (var l in M365Admin.Lists) _pick.Items.Add(new ComboBoxItem { Content = $"{l.Service} · {l.Title}", Tag = l, ToolTip = "Needs " + l.Needs });
        _pick.SelectedIndex = 0;
        var root = new DockPanel { Margin = new Thickness(18, 14, 18, 14) };
        var head = new StackPanel();
        head.Children.Add(new TextBlock { Text = "Microsoft 365 admin", Style = (Style)FindResource("H1") });
        head.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 10),
            Text = "Intune, Defender and Entra, read-only, signed in as you through Microsoft Graph. Double-click an item to open it in the admin centre. Nothing is contacted until you load a list; nothing read is stored.",
        });
        var bar = new WrapPanel();
        _load = Btn("↻ Load", () => _ = LoadAsync(force: true), "Loads the list (the first time per service, the browser asks you to sign in)", primary: true);
        bar.Children.Add(_pick);
        bar.Children.Add(_load);
        bar.Children.Add(_filter);
        bar.Children.Add(Btn("Open in portal", () => OpenSelected(), "Opens the selected item (or the admin centre) in a tab"));
        bar.Children.Add(Menus.MoreButton(() => new object?[]
        {
            Menus.Item("Intune admin centre", () => OpenWeb(M365Admin.IntuneHome, "Intune")),
            Menus.Item("Microsoft Defender portal", () => OpenWeb(M365Admin.DefenderHome, "Defender")),
            Menus.Item("Microsoft Entra admin centre", () => OpenWeb(M365Admin.EntraHome, "Entra")),
            Menus.Sep(),
            Menus.Item("Copy the list (tab-separated)", CopyList, enabled: _table != null),
            Menus.Item("Sign out of Microsoft Graph", () => { M365Admin.SignOut(); _loaded.Clear(); _table = null; Show(); _state.Text = "Signed out."; AuditLog.Write("you", "m365", "signed out of Microsoft Graph"); }),
            Menus.Item("Set up…", () => { MainWindow.Instance?.Navigate("settings"); SettingsView.Current?.RevealCard("Microsoft 365 admin"); }),
        }, "Portals ▾", "SmallButton"));
        head.Children.Add(bar);
        head.Children.Add(_state);
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);
        root.Children.Add(_grid);
        Content = root;

        _pick.SelectionChanged += (_, _) => { _table = _pick.SelectedItem is ComboBoxItem { Tag: AdminList l } && _loaded.TryGetValue(l, out var t) ? t : null; Show(); };
        _filter.TextChanged += (_, _) => Fill();
        _grid.MouseDoubleClick += (_, _) => OpenSelected();
        Menus.Attach(_grid, () => new object?[]
        {
            Menus.Item("Open in portal", OpenSelected, enabled: _grid.SelectedItem is AdminRow { PortalUrl: not null }),
            Menus.Item("Copy row", () => { if (_grid.SelectedItem is AdminRow r) Menus.Clip(string.Join("\t", r.Cells)); }, enabled: _grid.SelectedItem != null),
        });
        Show();
    }

    private Button Btn(string text, Action click, string tip, bool primary = false)
    {
        var b = new Button { Content = text, ToolTip = tip, Margin = new Thickness(8, 0, 0, 0) };
        if (TryFindResource(primary ? "SmallPrimaryButton" : "SmallButton") is Style s) b.Style = s;
        b.Click += (_, _) => click();
        return b;
    }

    private AdminList? Current => (_pick.SelectedItem as ComboBoxItem)?.Tag as AdminList;

    private void Show()
    {
        if (!GraphReader.Configured)
            _state.Text = "Set up first: Settings → Integrations → Microsoft 365 admin (your tenant and an app registration with the read-only permissions listed there, admin consent). The Portals ▾ menu opens the admin centres meanwhile.";
        else if (_table == null && Current is { } l)
            _state.Text = $"↻ Load to read {l.Service} {l.Title.ToLowerInvariant()} (needs {l.Needs}).";
        _load.IsEnabled = GraphReader.Configured;
        var gv = new GridView();
        if (_table != null)
            for (int i = 0; i < _table.Columns.Length; i++)
                gv.Columns.Add(new GridViewColumn { Header = _table.Columns[i], DisplayMemberBinding = new Binding($"Cells[{i}]"), Width = i == 0 ? 240 : double.NaN });
        _grid.View = gv;
        Fill();
    }

    private void Fill()
    {
        _grid.Items.Clear();
        if (_table == null) return;
        var q = _filter.Text.Trim().ToLowerInvariant();
        foreach (var r in _table.Rows) if (q.Length == 0 || r.Search.Contains(q)) _grid.Items.Add(r);
    }

    /// <summary>Loads the selected list (again with ↻). The self-test passes a fake Graph.</summary>
    internal async Task LoadAsync(bool force, System.Net.Http.HttpMessageHandler? handler = null)
    {
        if (Current is not { } l) return;
        if (!force && _loaded.TryGetValue(l, out var cached)) { _table = cached; Show(); return; }
        _load.IsEnabled = false;
        _state.Text = $"Reading {l.Service} {l.Title.ToLowerInvariant()}… (the first time, the browser asks you to sign in)";
        try
        {
            var t = await l.Load(new CancellationTokenSource(TimeSpan.FromMinutes(4)).Token, handler);
            _loaded[l] = t;
            if (Current == l) _table = t;
            _state.Text = $"{t.Rows.Count} {l.Title.ToLowerInvariant()}{(t.Truncated ? " (the first ones only)" : "")}{(M365Admin.Account is { Length: > 0 } a ? " · signed in as " + a : "")}. Double-click to open in the portal.";
            AuditLog.Write("you", "m365", $"{l.Service} {l.Title.ToLowerInvariant()} listed", t.Rows.Count.ToString());
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.Net.Http.HttpRequestException or OfflineBlockedException or OperationCanceledException or System.Text.Json.JsonException or ArgumentException)
        {
            _state.Text = "✕ " + ex.Message;
        }
        finally { _load.IsEnabled = GraphReader.Configured; Show(); }
    }

    private void OpenSelected()
    {
        if (_grid.SelectedItem is AdminRow { PortalUrl: { } url } r) OpenWeb(url, r.Cells[0]);
        else if (Current is { } l) OpenWeb(l.Portal, l.Service);
    }

    private void CopyList()
    {
        if (_table == null) return;
        var rows = _grid.Items.OfType<AdminRow>().Select(r => string.Join("\t", r.Cells));
        Menus.Clip(string.Join("\n", new[] { string.Join("\t", _table.Columns) }.Concat(rows)));
    }

    /// <summary>An admin centre in a tab of the app's browser (its own "Microsoft 365 admin" profile), or the default
    /// browser while the app's browser is off.</summary>
    private static void OpenWeb(string url, string title)
    {
        if (PortalBrowser.Enabled) { PortalUi.Open(new PortalBookmark { Title = title, Url = url, Profile = "Microsoft 365 admin" }); return; }
        if (Dialogs.Confirm("Microsoft 365 admin", "The app's browser is off (Settings → Integrations → Portal browser), so this opens in your default browser.", "Open in my browser"))
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
