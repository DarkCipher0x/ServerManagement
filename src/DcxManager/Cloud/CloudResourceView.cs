using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using DcxManager.Services;
using DcxManager.Views;

namespace DcxManager.Cloud;

/// <summary>
/// Generic cloud resource screen generated from a <see cref="ResourceDef"/> for any <see cref="CloudProvider"/> (ADR-020):
/// list, details, create, edit, delete, actions and child lists. Actions are offered only where the provider's catalog
/// defines them, enabled only in the states they work in (<see cref="ActionDef.When"/>), never twice at once on one item,
/// and watched to completion where the provider reports a state (<see cref="ActionDef.Await"/>).
/// </summary>
public sealed class CloudResourceView : UserControl, ICloudView, IDisposable
{
    private readonly CloudProvider _provider;
    private readonly ResourceDef _def;
    private readonly JsonElement? _parent;
    private readonly string _crumb;
    private readonly ListView _list = new() { SelectionMode = SelectionMode.Extended };
    private readonly TextBox _detail = new();
    private readonly TextBox _filter = DcxManager.Views.Hint.Set(new TextBox { Width = 220, Padding = new Thickness(5, 4, 5, 4), ToolTip = "Filter" }, "Filter this list…");
    private readonly TextBlock _count = new() { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(10, 0, 0, 2) };
    private readonly TextBlock _status = new TextBlock() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) }.Fs(FontStep.Small);
    private readonly List<(ActionDef Action, Button Button)> _actionButtons = new();
    private readonly List<(ActionDef Action, string Label)> _moreActions = new();
    private Button? _deleteButton, _editButton;
    private readonly HashSet<string> _busy = new();
    private readonly CancellationTokenSource _life = new();
    private List<JRow> _rows = new();
    private bool _loaded, _disposed;

    public CloudProvider? Provider => _provider;
    public ResourceDef Def => _def;

    public CloudResourceView(CloudProvider provider, ResourceDef def, JsonElement? parent = null, string? crumb = null)
    {
        _provider = provider; _def = def; _parent = parent; _crumb = crumb ?? def.Title;
        _detail.Style = (Style)FindResource("Mono");
        _count.Style = (Style)FindResource("Hint");
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");

        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(_filter, Dock.Right);
        head.Children.Add(_filter);
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(new TextBlock { Text = _crumb, Style = (Style)FindResource("H1") });
        title.Children.Add(_count);
        head.Children.Add(title);
        _filter.TextChanged += (_, _) => Apply();

        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        Button B(string text, Func<Task> click, string? style = null, string? tip = null)
        {
            var b = new Button { Content = text, ToolTip = tip, Style = (Style)FindResource(style ?? "SmallButton"), Margin = new Thickness(0, 0, 4, 4) };
            if (style is "DangerButton" or "PrimaryButton") { b.MinWidth = 0; b.Padding = new Thickness(10, 3, 10, 3); b.Fs(FontStep.Small); }
            b.Click += async (_, _) => await click();
            bar.Children.Add(b);
            return b;
        }
        B("↻ Refresh", LoadAsync);
        if (!def.ReadOnly && def.Create != null) B("＋ Create…", CreateAsync, "PrimaryButton");
        if (def.Update != null) _editButton = B("✎ Edit…", UpdateAsync);
        const int inline = 5;   // the rest goes under More ▾ (and every action is on right-click anyway)
        foreach (var (a, i) in def.Actions.Select((a, i) => (a, i)))
        {
            if (i < inline || def.Actions.Length <= inline + 1) _actionButtons.Add((a, B(a.Label, () => RunActionAsync(a), a.Danger ? "DangerButton" : null, a.Tip)));
            else _moreActions.Add((a, a.Label));
        }
        foreach (var (label, child) in def.Children) B(label + " ›", () => OpenChild(label, child));
        if (_moreActions.Count > 0 || !def.ReadOnly && !def.Single)
        {
            var mb = Menus.MoreButton(MoreItems, "More ▾", "SmallButton");   // right-click menus inline its items
            mb.Margin = new Thickness(0, 0, 4, 4);
            bar.Children.Add(mb);
        }
        foreach (var x in provider.ToolbarExtras(def)) B(x.Label, () => x.Run(Context()), x.Primary ? "PrimaryButton" : null, x.Tip);
        var docs = Menus.MoreButton(DocAiItems, Assist.Current != null ? "📄 Docs & ✦ AI ▾" : "📄 Docs ▾", "SmallButton");
        docs.Margin = new Thickness(0, 0, 4, 4);
        bar.Children.Add(docs);
        if (def.CanDelete && !def.ReadOnly) _deleteButton = B("✕ Delete", DeleteAsync, "DangerButton");
        bar.Children.Add(_status);

        var gv = new GridView();
        foreach (var c in def.Columns) gv.Columns.Add(new GridViewColumn { Header = c.Header, Width = c.Width, DisplayMemberBinding = new Binding($"[{c.Key}]") });
        _list.View = gv;
        _list.SelectionChanged += (_, _) => { ShowDetail(); UpdateStates(); };
        Menus.ForList(_list, this, RowItems, "Copy & details");   // the toolbar's actions stay at the top of the menu
        _list.MouseDoubleClick += async (_, _) =>
        {
            if (Sel == null) return;
            if (await provider.DoubleClickAsync(Context())) return;
            if (def.Actions.FirstOrDefault(a => a.Special == "ssh") is { } ssh) await RunActionAsync(ssh);
            else if (def.Children.Length > 0) await OpenChild(def.Children[0].Label, def.Children[0].Def);
        };

        var grid = new Grid();
        // Proportional: inside the Cloud providers workspace next to the AI sidebar the space is narrow, and a fixed-width
        // detail pane squeezed the list to a sliver (seen in the 0.1.11 render).
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star), MinWidth = 220 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star), MinWidth = 160, MaxWidth = 520 });
        var split = new GridSplitter { Width = 6, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent };
        Grid.SetColumn(split, 1); Grid.SetColumn(_detail, 2);
        grid.Children.Add(_list); grid.Children.Add(split); grid.Children.Add(_detail);

        var root = new DockPanel { Margin = new Thickness(14, 12, 14, 12) };
        DockPanel.SetDock(head, Dock.Top); DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(head); root.Children.Add(bar);
        if (def.Description.Length > 0)
        {
            var d = new TextBlock { Text = def.Description, Style = (Style)FindResource("Hint"), Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap };
            DockPanel.SetDock(d, Dock.Top);
            root.Children.Add(d);
        }
        root.Children.Add(grid);
        Content = root;
        Loaded += async (_, _) => { if (!_loaded) await LoadAsync(); };
        UpdateStates();
    }

    public void AccountChanged() { _rows.Clear(); _list.ItemsSource = null; _busy.Clear(); _ = LoadAsync(); }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _life.Cancel(); } catch { }
    }

    private ICloudApi? Client(bool quiet = false)
    {
        var c = _provider.Active;
        if (c == null && !quiet) _status.Text = $"No {_provider.Title} account — add one in Settings → {_provider.Title} accounts.";
        return c;
    }

    private CloudViewContext Context() => new(Client(true)!, _def, Sel, _list.SelectedItems.OfType<JRow>().ToList(), _parent, LoadAsync, s => _status.Text = s, Window.GetWindow(this), _crumb);

    private string ListPath => Tpl.Fill(_def.Path, null, _parent, url: true);

    /// <summary>The rows shown (self-test).</summary>
    internal IReadOnlyList<JRow> Rows => _rows;
    /// <summary>The rows the list control actually displays (self-test).</summary>
    internal IReadOnlyList<JRow> ShownRowsForTest => _list.Items.OfType<JRow>().ToList();
    internal string StatusText => _status.Text;
    internal bool IsBusy(JRow r) => _busy.Contains(r[_def.IdField]);
    internal bool HasDeleteButton => _deleteButton != null;
    internal Button? ButtonFor(string label) => _actionButtons.FirstOrDefault(b => b.Action.Label == label).Button;
    internal void SelectId(string id) => _list.SelectedItem = (_list.ItemsSource as IEnumerable<JRow>)?.FirstOrDefault(r => r[_def.IdField] == id);
    internal IEnumerable<object?> TestRowMenu() => RowItems();

    public async Task LoadAsync()
    {
        _loaded = true;
        if (Client() is not { } v) { ShowDetail(); return; }
        _status.Text = "loading…";
        try
        {
            if (_def.Single)
            {
                var r = await v.GetAsync(ListPath, _life.Token);
                _rows = new() { new JRow(_def.Key != null && r.ValueKind == JsonValueKind.Object && r.TryGetProperty(_def.Key, out var one) ? (one.ValueKind == JsonValueKind.Array && one.GetArrayLength() > 0 ? one[0] : one) : r) };
            }
            else _rows = (await v.ListAsync(ListPath, _def.Key, _life.Token)).Select(e => new JRow(e)).ToList();
            Apply();
            _status.Text = $"{v.AccountName} · {DateTime.Now:HH:mm:ss}" + FollowHosts(v);
            if (_def.Single && _rows.Count == 1) _list.SelectedIndex = 0;
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex) { _status.Text = "✕ " + ex.Message.Split('\n')[0]; _detail.Text = Explain(ex); }
    }

    /// <summary>Hosts reported as gone this session (asked about once; the next start asks again).</summary>
    private static readonly HashSet<string> GoneAsked = new();

    /// <summary>Saved hosts follow the account's server list (0.6.24): a new IPv4 is copied to the linked host, a host whose
    /// server was destroyed and rebuilt under the same name is relinked, and hosts whose server is gone are offered for
    /// removal (never removed without asking). Only the provider's own instance list counts: it is complete, child lists
    /// and other resource types are not. Group Policy: CloudSyncHosts. Returns a note for the status line.</summary>
    private string FollowHosts(ICloudApi v)
    {
        if (_def != _provider.InstanceDef || _parent != null || !AppSettings.Current.CloudSyncHosts) return "";
        var r = HostsView.FollowCloud(_provider, v.AccountId, _rows.Select(x => _provider.Normalize(x.Raw, v.AccountId)).OfType<CloudInstance>().ToList());
        var fresh = r.Gone.Where(h => !r.Locked.Contains(h) && GoneAsked.Add(h.Id)).ToList();
        if (fresh.Count > 0) Dispatcher.BeginInvoke(() => OfferRemoveGone(fresh, v.AccountName));
        var notes = new List<string>();
        if (r.Moved.Count > 0) notes.Add(r.Moved.Count == 1 ? $"host {r.Moved[0].Host.Display} now {r.Moved[0].Host.Address}" : $"{r.Moved.Count} hosts' addresses updated");
        if (r.Relinked.Count > 0 && r.Relinked.Count != r.Moved.Count) notes.Add($"{r.Relinked.Count} host(s) relinked");
        if (r.Gone.Count > 0) notes.Add($"⚠ {r.Gone.Count} saved host(s) without a server");
        if (r.Locked.Count > 0) notes.Add($"🔒 {r.Locked.Count} locked host(s) not updated");
        return notes.Count == 0 ? "" : " · " + string.Join(" · ", notes);
    }

    private void OfferRemoveGone(List<HostEntry> gone, string account)
    {
        if (_disposed) return;
        var names = string.Join("\n", gone.Take(15).Select(h => $"  {h.Display}  ({h.Address})")) + (gone.Count > 15 ? $"\n  … and {gone.Count - 15} more" : "");
        if (!Dialogs.Confirm("Saved hosts", $"{gone.Count} saved host(s) point at {_provider.Title} servers that no longer exist on {account}:\n\n{names}\n\n" +
                "Remove them from your saved hosts? Credentials in the vault are kept. Cancel keeps them (you're asked again next time the app starts).", "Remove hosts", danger: true)) return;
        if (!Restrictions.Check("host.delete")) return;
        var open = gone.Where(h => AppData.Current.Hosts.Contains(h) && Locks.IsOpen("host:" + h.Id)).ToList();
        HostsView.RemoveHosts(open, $"deleted host (its {_provider.Title} server no longer exists)");
        if (open.Count < gone.Count) Dialogs.Info("Saved hosts", $"{gone.Count - open.Count} padlocked host(s) were kept. Open their padlock and delete them on the Hosts page.");
    }

    /// <summary>The error with what to do about it (the provider's request id helps its support).</summary>
    private string Explain(Exception ex) => ex switch
    {
        CloudApiException { Status: 401 } c => $"{c.Message}\n\nThe sign-in for this {_provider.Title} account was refused. Check or replace its credentials in Settings → {_provider.Title} accounts." + Rid(c),
        CloudApiException { Status: 403 } c => $"{c.Message}\n\nThe account is signed in but not allowed to do this. Check the API user's role / permissions at {_provider.Title}." + Rid(c),
        CloudApiException { Status: 429 } c => $"{c.Message}\n\n{_provider.Title} is limiting requests. Wait a little and refresh." + Rid(c),
        CloudApiException { Status: >= 500 } c => $"{c.Message}\n\n{_provider.Title}'s API had a problem. Try again later." + Rid(c),
        CloudApiException { Status: 0 } c => c.Message + Rid(c),
        _ => ex.Message,
    };
    private static string Rid(CloudApiException c) => c.RequestId != null ? $"\n\nRequest id (for {c.Provider} support): {c.RequestId}" : "";

    private void Apply()
    {
        var q = _filter.Text.Trim();
        var keep = Sel?[_def.IdField];
        // Always a new list: handing the ListView the same list object again (after a status watch replaced a row) doesn't
        // redraw it, so the row kept showing its old state (found in the 0.1.11 Hetzner render).
        var shown = (q.Length == 0 ? _rows : _rows.Where(r => r.Raw.GetRawText().Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();
        _list.ItemsSource = shown;
        if (keep != null) _list.SelectedItem = shown.FirstOrDefault(r => r[_def.IdField] == keep);
        _count.Text = shown.Count == _rows.Count ? $"{_rows.Count}" : $"{shown.Count} of {_rows.Count}";
        ShowDetail();
        UpdateStates();
    }

    private JRow? Sel => _list.SelectedItem as JRow;

    private void ShowDetail()
    {
        if (Sel is { } r) _detail.Text = J.Pretty(r.Raw);
        else if (_provider.Active == null) _detail.Text = _provider.ConnectHelp;
        else if (!_detail.Text.StartsWith('✕')) _detail.Text = "";
    }

    // ---- what is possible now --------------------------------------------------------------------------------------

    /// <summary>Why <paramref name="a"/> can't run on the selection now, or null.</summary>
    internal string? WhyNot(ActionDef a)
    {
        if (a.NoRow) return null;
        if (Sel is not { } r) return "Select an item first.";
        if (_busy.Contains(r[_def.IdField])) return "An action on this item is still running.";
        if (!_def.Allows(a.When, r.Raw)) return $"Not possible while it is {r[_def.StatusField ?? "status"]}.";
        return null;
    }

    internal string? WhyNotDelete(JRow r) =>
        _busy.Contains(r[_def.IdField]) ? "An action on this item is still running." : !_def.Allows(_def.DeleteWhen, r.Raw) ? "This item can't be deleted (for example a standard image)." : null;

    private void UpdateStates()
    {
        foreach (var (a, b) in _actionButtons)
        {
            var why = a.Special is "ssh" or "addhost" && Sel != null ? null : WhyNot(a);
            b.IsEnabled = why == null;
            b.ToolTip = why ?? a.Tip;
            ToolTipService.SetShowOnDisabled(b, true);
        }
        if (_deleteButton != null)
        {
            var rows = _list.SelectedItems.OfType<JRow>().ToList();
            var why = rows.Count == 0 ? "Select an item first." : rows.Select(WhyNotDelete).FirstOrDefault(w => w != null);
            _deleteButton.IsEnabled = why == null;
            _deleteButton.ToolTip = why;
            ToolTipService.SetShowOnDisabled(_deleteButton, true);
        }
        if (_editButton != null)
        {
            _editButton.IsEnabled = Sel != null && !_busy.Contains(Sel[_def.IdField]);
            ToolTipService.SetShowOnDisabled(_editButton, true);
        }
    }

    private IEnumerable<object?> MoreItems()
    {
        foreach (var (a, label) in _moreActions)
        {
            var why = WhyNot(a);
            yield return Menus.AsyncItem(label, () => RunActionAsync(a), enabled: why == null);
        }
        if (!_def.ReadOnly && !_def.Single)
            yield return Menus.AsyncItem("{ } Create from JSON…", CreateJsonAsync);
    }

    private bool NeedRow()
    {
        if (Sel != null) return true;
        _status.Text = "Select a row first.";
        return false;
    }

    private string ItemPath(JRow row) => Tpl.Fill(_def.ItemTemplate, row.Raw, _parent, url: true);
    private string RowName(JRow row) { var n = row[_def.NameField]; return n.Length > 0 ? n : row[_def.IdField]; }

    /// <summary>The saved host linked to an instance row (for environment-aware confirmations and SSH).</summary>
    private HostEntry? LinkedHost(JRow row)
    {
        if (!_def.IsInstance || _provider.Normalize(row.Raw, "") is not { } inst) return null;
        return AppData.Current.Hosts.FirstOrDefault(h => CloudProviders.ForHost(h) is { } l && l.Provider == _provider && l.InstanceId == inst.Id)
               ?? AppData.Current.Hosts.FirstOrDefault(h => inst.Ipv4.Length > 0 && h.Address == inst.Ipv4);
    }

    /// <summary>A confirmation that follows the environment rules when the item is a known host (PRODUCTION: type its name).</summary>
    private bool Confirm(JRow? row, string title, string message, string action, bool danger, string? typeToConfirm)
    {
        if (row != null && LinkedHost(row) is { } h) return Envs.Confirm(h, title, message, action, danger, typeToConfirm);
        return Dialogs.Confirm(title, message, action, danger, typeToConfirm);
    }

    // ---- create / update / delete --------------------------------------------------------------------------------

    private async Task CreateAsync()
    {
        if (Client() is not { } v || _def.Create == null) return;
        var w = new CloudFormWindow($"Create — {_def.Title}", $"{_provider.Title} account: {v.AccountName}" + (_parent != null ? $" · {_crumb}" : ""), _def.Create, v, null, _parent, false);
        if (w.ShowDialog() != true || w.Body == null) return;
        if (_def.CreateConfirm != null && !Dialogs.Confirm($"Create — {_def.Title}", Tpl.Fill(_def.CreateConfirm, null, _parent, form: w.Body), "Create", danger: true)) return;
        var path = Tpl.Fill(_def.CreatePath ?? _def.Path.Split('?')[0], null, _parent, url: true, form: w.Body);
        foreach (var f in _def.Create.Where(f => f.InPath)) w.Body.Remove(f.Name);   // named in the path (Azure), not in the body
        // Orders are never repeated automatically (a slow answer must not become two servers).
        await SendAsync(new HttpMethod(_def.CreateMethod), path, w.Body.ToJsonString(), "created", showResult: true, idempotent: false);
    }

    private async Task CreateJsonAsync()
    {
        if (Client() is null) return;
        var path = Tpl.Fill(_def.CreatePath ?? _def.Path.Split('?')[0], null, _parent, url: true);
        var json = Dialogs.Prompt("Create from JSON", $"{_def.CreateMethod} {path}\n\nRequest body:", "{\n  \n}", multiline: true);
        if (json == null) return;
        try { JsonDocument.Parse(json); } catch (JsonException ex) { Dialogs.Error("JSON", ex.Message); return; }
        if (_def.CreateConfirm != null && !Dialogs.Confirm($"Create — {_def.Title}", Tpl.Fill(_def.CreateConfirm, null, _parent), "Create", danger: true)) return;
        await SendAsync(new HttpMethod(_def.CreateMethod), path, json, "created", showResult: true, idempotent: false);
    }

    private async Task UpdateAsync()
    {
        if (!NeedRow() || Client() is not { } v || _def.Update == null) return;
        var row = Sel!;
        var w = new CloudFormWindow($"Edit — {RowName(row)}", $"{_def.Title} · {_provider.Title} · {v.AccountName}", _def.Update, v, row.Raw, _parent, true, "Save");
        if (w.ShowDialog() != true || w.Body == null) return;
        await SendAsync(new HttpMethod(_def.UpdateMethod), ItemPath(row), w.Body.ToJsonString(), "updated");
    }

    private async Task DeleteAsync()
    {
        var rows = _list.SelectedItems.OfType<JRow>().ToList();
        if (rows.Count == 0 || Client() is not { } v) return;
        if (rows.Select(WhyNotDelete).FirstOrDefault(w => w != null) is { } why) { _status.Text = why; return; }
        var names = string.Join("\n", rows.Take(10).Select(RowName));
        if (!Confirm(rows.Count == 1 ? rows[0] : null, $"Delete {_def.Title}", $"Permanently delete {rows.Count} item(s) on {_provider.Title} account {v.AccountName}?\n\n{names}\n\nThis can't be undone.", "Delete", true,
            _def.TypeToDelete ? (rows.Count == 1 ? RowName(rows[0]) : "delete") : null)) return;
        foreach (var r in rows)
        {
            try { await v.SendAsync(HttpMethod.Delete, ItemPath(r), null, _life.Token); }
            catch (Exception ex) { Dialogs.Error("Delete", $"{RowName(r)}: {Explain(ex)}"); }
        }
        await LoadAsync();
    }

    private async Task SendAsync(HttpMethod method, string path, string? body, string verb, bool showResult = false, bool idempotent = true)
    {
        if (Client() is not { } v) return;
        _status.Text = $"{method} {path}…";
        using var progress = TrackProgress();
        try
        {
            var r = await v.SendAsync(method, path, body, _life.Token, idempotent: idempotent);
            _status.Text = $"✓ {verb}";
            if (showResult && r.ValueKind != JsonValueKind.Undefined)
            {
                // A new Vultr instance returns its one-time root password: show it once, it is not stored.
                var pw = J.Str(r, "instance.default_password");
                if (pw.Length == 0) pw = J.Str(r, "bare_metal.default_password");
                if (pw.Length == 0) pw = J.Str(r, "root_password");   // Hetzner, when no SSH key was chosen
                if (pw.Length > 0) Dialogs.ShowText("Created — note the root password", $"Root password (shown once, not stored by this app):\n\n{pw}\n\nSave it in the vault (Credentials → Add) if you need it.\n\n{J.Pretty(r)}", modal: true);
            }
            await LoadAsync();
        }
        catch (Exception ex) when (!_disposed) { _status.Text = "✕ " + ex.Message.Split('\n')[0]; Dialogs.Error(_def.Title, Explain(ex)); }
    }

    /// <summary>While a change runs, the provider's progress reports (e.g. a Hetzner action at 40 %) show in the status line.</summary>
    private IDisposable TrackProgress()
    {
        bool active = true;   // a report queued just before the change finished must not overwrite the final status
        void On(string provider, string text) { if (provider == _provider.Id) Dispatcher.BeginInvoke(() => { if (!_disposed && active) _status.Text = text; }); }
        CloudProgress.Changed += On;
        return new Unsubscribe(() => { active = false; CloudProgress.Changed -= On; });
    }
    private sealed class Unsubscribe(Action a) : IDisposable { public void Dispose() => a(); }

    // ---- actions ----------------------------------------------------------------------------------------------

    internal async Task RunActionAsync(ActionDef a)
    {
        if (Client() is not { } v) return;
        if (!a.NoRow && !NeedRow()) return;
        var row = a.NoRow ? null : Sel;
        JsonElement? raw = row?.Raw;

        if (a.Special != null) { await RunSpecialAsync(a, row); return; }
        if (WhyNot(a) is { } why) { _status.Text = why; return; }

        JsonObject? form = null;
        string? body = null;
        if (a.Fields != null || a.Fixed != null)
        {
            var obj = new JsonObject();
            if (a.Fields is { Length: > 0 })
            {
                var w = new CloudFormWindow(a.Label.TrimStart('▶', '↻', '■', '⏻', ' '), row != null ? $"{_def.Title}: {RowName(row)}" : _def.Title, a.Fields, v, raw, _parent, false, "Run");
                if (w.ShowDialog() != true || w.Body == null) return;
                obj = w.Body;
            }
            foreach (var (k, t) in a.Fixed ?? new()) J.SetPath(obj, k, Tpl.Fill(t, raw, _parent));
            form = (JsonObject)obj.DeepClone();
            foreach (var f in a.Fields?.Where(f => f.InPath) ?? Enumerable.Empty<Field>()) obj.Remove(f.Name);
            body = obj.Count > 0 || a.Fields?.Any(f => !f.InPath) == true || a.Fixed != null ? obj.ToJsonString() : null;
        }
        var path = Tpl.Fill(a.Path, raw, _parent, url: true, form: form);
        if (a.Confirm != null && !Confirm(row, a.Label.Trim(), Tpl.Fill(a.Confirm, raw, _parent, form: form) + $"\n\n{a.Method} {path}", a.Label.Trim(), a.Danger,
                a.TypeToConfirm != null ? Tpl.Fill(a.TypeToConfirm, raw, _parent) : null)) return;

        var id = row?[_def.IdField];
        if (id != null && a.Method != "GET") { _busy.Add(id); UpdateStates(); }
        _status.Text = $"{a.Method} {path}…";
        using var progress = TrackProgress();
        try
        {
            var r = await v.SendAsync(new HttpMethod(a.Method), path, body, _life.Token, idempotent: a.Idempotent);
            _status.Text = $"✓ {a.Label.Trim()}";
            if (a.ShowResult) Dialogs.ShowText($"{a.Label.Trim()} — {(row != null ? RowName(row) : _def.Title)}", J.Pretty(r));
            else if (a.Await != null && row != null) await WatchAsync(row, a.Await, a.Label.Trim());
            else if (a.Method != "GET") await LoadAsync();
        }
        catch (Exception ex) when (!_disposed) { _status.Text = "✕ " + ex.Message.Split('\n')[0]; Dialogs.Error(a.Label.Trim(), Explain(ex)); }
        finally { if (id != null) { _busy.Remove(id); if (!_disposed) UpdateStates(); } }
    }

    /// <summary>Test seam: seconds between status checks and how many (the self-test makes it fast).</summary>
    internal static TimeSpan WatchInterval = TimeSpan.FromSeconds(5);
    internal static int WatchChecks = 72;   // 6 minutes: reinstalls and orders take a while

    /// <summary>After an action: re-read the item until it reaches <paramref name="target"/> (or an error state), showing
    /// progress. Bounded, and stops when the tab closes. Nothing runs in the background after that.</summary>
    private async Task WatchAsync(JRow row, string target, string what)
    {
        if (Client() is not { } v || _def.StatusField is not { } field) { await LoadAsync(); return; }
        var id = row[_def.IdField];
        var path = ItemPath(row);
        string state = "";
        for (int i = 0; i < WatchChecks && !_disposed; i++)
        {
            try
            {
                await Task.Delay(WatchInterval, _life.Token);
                var item = await v.GetItemAsync(path, _life.Token);
                state = J.Str(item, field);
                var idx = _rows.FindIndex(r => r[_def.IdField] == id);
                if (idx >= 0) { _rows[idx] = new JRow(item); Apply(); }
                if (state.Equals(target, StringComparison.OrdinalIgnoreCase)) { _status.Text = $"✓ {what}: {RowName(row)} is {state}"; return; }
                if (state is "error" or "failed") { _status.Text = $"✕ {what}: {RowName(row)} reports {state}" + (J.Str(item, "errorMessage") is { Length: > 0 } em ? $" — {em}" : ""); return; }
                _status.Text = $"{what}: waiting for {target} (now {state}, {((i + 1) * WatchInterval).TotalSeconds:0} s)…";
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _status.Text = $"{what}: couldn't check the state ({ex.Message.Split('\n')[0]}) — ↻ Refresh later"; return; }
        }
        if (!_disposed) _status.Text = $"{what}: still {state} after {(WatchChecks * WatchInterval).TotalMinutes:0} min — ↻ Refresh later";
    }

    private async Task RunSpecialAsync(ActionDef a, JRow? row)
    {
        try
        {
            switch (a.Special)
            {
                case "ssh":
                {
                    var host = FindHost(row!) ?? AddHost(row!, ask: true);
                    if (host != null) MainWindow.Instance.OpenTerminal(host);
                    break;
                }
                case "addhost":
                    if (FindHost(row!) is { } existing) Dialogs.Info("Hosts", $"Already saved as “{existing.Display}”.");
                    else AddHost(row!, ask: false);
                    break;
                default:
                    if (!await _provider.RunSpecialAsync(a, Context())) _status.Text = "Not available.";
                    break;
            }
        }
        catch (Exception ex) { Dialogs.Error(a.Label.Trim(), Explain(ex)); }
    }

    private HostEntry? FindHost(JRow row)
    {
        if (_provider.Normalize(row.Raw, Client(true)?.AccountId ?? "") is not { } inst) return null;
        return AppData.Current.Hosts.FirstOrDefault(h => CloudProviders.ForHost(h) is { } l && l.Provider == _provider && l.InstanceId == inst.Id)
               ?? AppData.Current.Hosts.FirstOrDefault(h => inst.Ipv4.Length > 0 && h.Address == inst.Ipv4);
    }

    private HostEntry? AddHost(JRow row, bool ask)
    {
        if (_provider.Normalize(row.Raw, Client(true)?.AccountId ?? "") is not { } inst) return null;
        if (inst.Ipv4 is not { Length: > 0 } ip || ip == "0.0.0.0") { Dialogs.Info("Hosts", "This server has no public IPv4 address yet."); return null; }
        var key = AppData.Current.FindCredential(AppSettings.Current.DefaultKeyCredentialId);
        var seed = HostsView.FromCloud(inst, key);
        if (ask && !Dialogs.Confirm("Connect", $"{inst.Name} ({ip}) isn't a saved host yet. Save it now (sign-in details, key, sudo)?", "Set up host")) return null;
        var w = new HostEditWindow(seed) { Owner = Window.GetWindow(this) };
        if (w.ShowDialog() != true || w.Result is not { } h) return null;
        AppData.Current.Hosts.Add(h);
        AppData.Save();
        AuditLog.Write("you", h.Display, $"added host from {_provider.Title}");
        return h;
    }

    // ---- menus, documents + AI ---------------------------------------------------------------------------------------

    private List<JRow> Visible => (_list.ItemsSource as IEnumerable<JRow>)?.ToList() ?? new();

    /// <summary>Row-specific items at the top of the right-click menu (the toolbar is mirrored below them).</summary>
    private IEnumerable<object?> RowItems()
    {
        var sel = _list.SelectedItems.OfType<JRow>().ToList();
        var assist = Assist.Current;
        var ai = assist?.WhyNot();
        if (sel.Count == 1 && _def.IsInstance && _provider.Normalize(sel[0].Raw, "") is { } inst)
        {
            if (inst.Ipv4.Length > 0) yield return Menus.Item($"Copy IPv4 ({inst.Ipv4})", () => Menus.Clip(inst.Ipv4));
            if (inst.Ipv6.Length > 0) yield return Menus.Item("Copy IPv6", () => Menus.Clip(inst.Ipv6));
            yield return Menus.Item("Copy name", () => Menus.Clip(inst.Name));
            if (inst.Ipv4.Length > 0) yield return Menus.Item("Copy ssh command", () => Menus.Clip($"ssh {(inst.DefaultUser.Length > 0 ? inst.DefaultUser : "root")}@{inst.Ipv4}"));
            if (_busy.Contains(sel[0][_def.IdField])) yield return new MenuItem { Header = "   an action on this item is still running", IsEnabled = false };
        }
        if (sel.Count > 0) foreach (var x in _provider.RowMenuExtras(_def, Context())) yield return x;
        if (sel.Count > 0)
        {
            if (assist != null) yield return Menus.AsyncItem(sel.Count == 1 ? $"✦ Explain this {Singular} with AI" : $"✦ Explain {sel.Count} selected with AI", ExplainSelectedAsync, enabled: ai == null);
            yield return Menus.Item("Copy JSON", () => Menus.Clip(sel.Count == 1 ? J.Pretty(sel[0].Raw) : "[" + string.Join(",\n", sel.Select(r => J.Pretty(r.Raw))) + "]"));
            if (sel[0][_def.IdField].Length > 0) yield return Menus.Item("Copy id", () => Menus.Clip(string.Join("\n", sel.Select(r => r[_def.IdField]))));
        }
        if (ai != null) yield return new MenuItem { Header = "   AI: " + ai, IsEnabled = false };
    }

    private string Singular => _def.Title.EndsWith("ies") ? _def.Title[..^3].ToLowerInvariant() + "y" : _def.Title.TrimEnd('s').ToLowerInvariant();

    private IEnumerable<object?> DocAiItems()
    {
        var assist = Assist.Current;
        var ai = assist?.WhyNot();
        return new object?[]
        {
            Menus.Item("📄 View this list as a document (print / save as PDF)", ViewListDocument, enabled: Visible.Count > 0),
            assist != null ? Menus.AsyncItem("✦ Review this list with AI", ReviewListAsync, enabled: ai == null && Visible.Count > 0) : null,
            assist != null ? Menus.AsyncItem("✦ Explain the selected item with AI", ExplainSelectedAsync, enabled: ai == null && Sel != null) : null,
            ai != null ? new MenuItem { Header = "AI: " + ai, IsEnabled = false } : null,
        };
    }

    private void ViewListDocument()
    {
        if (Client() is not { } v) return;
        DocumentViewerWindow.Open(CloudDocuments.ListDocument(_provider.Title, _def, Visible, v.AccountName, _crumb));
    }

    private async Task ReviewListAsync()
    {
        if (Client() is not { } v || Assist.Current is not { } assist) return;
        var doc = CloudDocuments.ListDocument(_provider.Title, _def, Visible, v.AccountName, _crumb);
        // The list view's columns are a summary; the full JSON (masked by the assistant) gives the model the real settings.
        var json = "[" + string.Join(",\n", Visible.Take(200).Select(r => r.Raw.GetRawText())) + "]";
        await assist.AskAsync($"{_crumb} ({Visible.Count}) — {_provider.Title} {v.AccountName}", doc.AiText + "\n\nRaw JSON:\n" + json, doc.AiTask);
    }

    private async Task ExplainSelectedAsync()
    {
        var sel = _list.SelectedItems.OfType<JRow>().Take(20).ToList();
        if (sel.Count == 0) { NeedRow(); return; }
        if (Assist.Current is not { } assist) return;
        var json = sel.Count == 1 ? J.Pretty(sel[0].Raw) : "[" + string.Join(",\n", sel.Select(r => J.Pretty(r.Raw))) + "]";
        await assist.AskAsync(sel.Count == 1 ? $"{Singular} {RowName(sel[0])}" : $"{sel.Count} {_def.Title}", json,
            $"Explain this {_provider.Title} {_def.Title} resource for an infrastructure engineer: what it is and how it's configured, anything risky, wasteful or misconfigured, and what to check next.");
    }

    private Task OpenChild(string label, ResourceDef child)
    {
        if (!NeedRow()) return Task.CompletedTask;
        var row = Sel!;
        var crumb = $"{_crumb} › {RowName(row)} › {label}";
        MainWindow.Instance.AddTab($"{_def.Icon} {RowName(row)} › {label}", new CloudResourceView(_provider, child, row.Raw, crumb));
        return Task.CompletedTask;
    }
}

/// <summary>A provider's reference lists (regions, plans, images, data centers…) as tabs in one screen.</summary>
public sealed class CloudReferenceView : UserControl, ICloudView, IDisposable
{
    private readonly TabControl _tabs = new() { Margin = new Thickness(4) };
    public CloudProvider? Provider { get; }
    public CloudReferenceView(CloudProvider provider)
    {
        Provider = provider;
        foreach (var d in provider.Catalog.Where(d => d.Group == "Reference"))
            _tabs.Items.Add(new TabItem { Header = d.Title, Content = new CloudResourceView(provider, d) });
        Content = _tabs;
    }
    public void AccountChanged() { foreach (TabItem t in _tabs.Items) (t.Content as ICloudView)?.AccountChanged(); }
    public void Dispose() { foreach (TabItem t in _tabs.Items) (t.Content as IDisposable)?.Dispose(); }
}
