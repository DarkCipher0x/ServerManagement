using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DcxManager.Services;

namespace DcxManager.Views;

/// <summary>
/// "🔍 Find…" beside every credential box (0.6.19; DarkCipher0x: "it doesnt have the option for me to search for any
/// bitwarden credentials when editing a host or portal entry … make it easy to search for bitwarden/password safe creds,
/// as there could be a lot in the persons library"). One search over this vault, Bitwarden, Secrets Manager and Password
/// Safe: each source is listed once (names only), typing filters instantly, and picking an outside item makes it a vault
/// credential (ADR-096) that the editor then selects. Nothing secret is read until the credential is used.
/// </summary>
public sealed class CredentialPicker
{
    /// <summary>Self-test: the answer instead of a window (no window is ever shown in the self-test).</summary>
    internal static Func<CredentialKind?, string, Credential?>? PickForTest { get; set; }

    private static string _lastSource = CredentialSearch.Vault;

    /// <summary>Opens the picker; the chosen vault credential (an outside item is added to the vault first), or null.</summary>
    public static Credential? Pick(CredentialKind? kind, string purpose, string query = "", string? source = null)
    {
        if (TestMode.Running)
        {
            if (PickForTest != null) return PickForTest(kind, query);
            Dialogs.RefusedInSelfTest("credential picker: " + purpose);
            return null;
        }
        var p = new CredentialPicker(kind, purpose, query, source ?? _lastSource);
        return p.Window.ShowDialog() == true ? p.Result : null;
    }

    internal readonly Window Window;
    private readonly CredentialKind? _kind;
    private readonly TextBox _query;
    private readonly DataGrid _grid = new() { IsReadOnly = true, AutoGenerateColumns = false, HeadersVisibility = DataGridHeadersVisibility.Column, SelectionMode = DataGridSelectionMode.Single, MinHeight = 260 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly StackPanel _sources = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 8) };
    private readonly DispatcherTimer _typing;
    private List<CredentialHit> _all = new();
    private string _source;
    private int _loading;
    internal Credential? Result { get; private set; }

    internal CredentialPicker(CredentialKind? kind, string purpose, string query, string source)
    {
        _kind = kind;
        var sources = CredentialSearch.Sources();
        _source = sources.Contains(source) || source == "all" ? source : CredentialSearch.Vault;
        Window = Dialogs.MakeWindow("Find a credential", 900, 640);
        var root = new DockPanel { Margin = new Thickness(16) };

        var top = new StackPanel();
        var intro = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = $"For {purpose}. " + (kind == CredentialKind.PrivateKey ? "SSH keys only. " : kind == CredentialKind.Password ? "Passwords, secrets and managed accounts. " : "") +
                   "Type any part of a name, user name or address; Enter or double-click uses it. " +
                   (sources.Count > 1 ? "Items from Bitwarden or Password Safe are added to your vault as a link (nothing secret is copied) and read when used."
                                      : "Turn on Bitwarden or BeyondTrust (Settings → Integrations) to search them here too."),
        }.Fs(FontStep.Small);
        intro.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        top.Children.Add(intro);
        foreach (var s in sources.Count > 1 ? sources.Append("all") : sources)
        {
            var r = new RadioButton { Content = s == "all" ? "All sources" : CredentialSearch.Title(s), GroupName = "credsource", Margin = new Thickness(0, 0, 16, 0), Tag = s, IsChecked = s == _source };
            r.Checked += async (_, _) => { _source = (string)r.Tag; _lastSource = _source; await LoadAsync(false); };
            _sources.Children.Add(r);
        }
        top.Children.Add(_sources);
        _query = Hint.Set(new TextBox { Text = query, Padding = new Thickness(6, 5, 6, 5) }, "Search… (e.g. web-01, root, 192.0.2.10)");
        var refresh = Dialogs.Btn("↻");
        refresh.ToolTip = "Read this source again (Bitwarden syncs with its server first; other lists are kept for 5 minutes)";
        refresh.Margin = new Thickness(6, 0, 0, 0);
        refresh.Click += async (_, _) => await LoadAsync(true);
        var searchRow = new DockPanel();
        DockPanel.SetDock(refresh, Dock.Right);
        searchRow.Children.Add(refresh); searchRow.Children.Add(_query);
        top.Children.Add(searchRow);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        var use = Dialogs.Btn("Use", primary: true); use.IsDefault = true;
        var cancel = Dialogs.Btn("Cancel"); cancel.IsCancel = true; cancel.Margin = new Thickness(8, 0, 0, 0);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(use); buttons.Children.Add(cancel);
        var bottom = new StackPanel();
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        bottom.Children.Add(_status); bottom.Children.Add(buttons);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);

        void Col(string h, string p, double w) => _grid.Columns.Add(new DataGridTextColumn { Header = h, Binding = new System.Windows.Data.Binding(p), Width = w });
        Col("Name", nameof(CredentialHit.Name), 260); Col("Type", nameof(CredentialHit.KindText), 110); Col("User name", nameof(CredentialHit.User), 140);
        Col("Address / where", nameof(CredentialHit.Where), 190); Col("Source", nameof(CredentialHit.SourceText), 120); Col("In vault", nameof(CredentialHit.InVault), 60);
        _grid.Margin = new Thickness(0, 8, 0, 0);
        root.Children.Add(_grid);
        Window.Content = root;

        _typing = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Input, (_, _) => { _typing!.Stop(); Show(); }, Window.Dispatcher);
        _query.TextChanged += (_, _) => { _typing.Stop(); _typing.Start(); };
        _query.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Down && _grid.Items.Count > 0) { _grid.SelectedIndex = Math.Max(0, _grid.SelectedIndex); _grid.Focus(); e.Handled = true; }
        };
        _grid.MouseDoubleClick += (_, _) => Choose();
        use.Click += (_, _) => Choose();
        Window.Loaded += async (_, _) => { _query.Focus(); _query.CaretIndex = _query.Text.Length; await LoadAsync(false); };
        // 0.6.20: Bitwarden's list arrives or changes in the background (after the unlock, after a sync): shown as it does.
        Action changed = () => Window.Dispatcher.BeginInvoke(async () => { if (_source is CredentialSearch.Bitwarden or "all") await LoadAsync(false); });
        Bitwarden.BitwardenVault.ItemsChanged += changed;
        Window.Closed += (_, _) => Bitwarden.BitwardenVault.ItemsChanged -= changed;
    }

    /// <summary>Reads the chosen source(s); errors are shown in the status line (a missing CLI, a locked vault…).</summary>
    internal async Task LoadAsync(bool fresh)
    {
        var mine = ++_loading;
        var sources = _source == "all" ? CredentialSearch.Sources().ToList() : new List<string> { _source };
        if (fresh) foreach (var s in sources) CredentialSearch.Forget(s);
        _status.Text = "Loading " + string.Join(", ", sources.Select(CredentialSearch.Title)) + "…";
        var all = new List<CredentialHit>();
        var problems = new List<string>();
        // ↻ on Bitwarden fetches the latest vault from the server first (its list is otherwise held until it locks).
        if (fresh && sources.Contains(CredentialSearch.Bitwarden) && Bitwarden.BitwardenVault.Unlocked && !Offline.On)
        {
            _status.Text = "Syncing Bitwarden…";
            try { await Bitwarden.BitwardenVault.SyncAsync(); }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or OperationCanceledException) { problems.Add("Bitwarden sync: " + ex.Message.Split('\n')[0]); }
        }
        foreach (var s in sources)
        {
            try { all.AddRange(await CredentialSearch.LoadAsync(s)); }
            catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException or TimeoutException or System.IO.IOException or System.ComponentModel.Win32Exception or ArgumentException)
            { problems.Add($"{CredentialSearch.Title(s)}: {ex.Message.Split('\n')[0]}"); }
        }
        if (mine != _loading) return;   // another source was chosen meanwhile
        _all = all;
        Show(problems);
    }

    private void Show() => Show(null);

    private void Show(List<string>? problems)
    {
        var shown = CredentialSearch.Filter(_all, _query.Text, _kind);
        _grid.ItemsSource = shown;
        if (shown.Count > 0) _grid.SelectedIndex = 0;
        var total = _all.Count(h => _kind == null || h.Kind == _kind);
        _status.Text = (problems is { Count: > 0 } ? "⚠ " + string.Join(" · ", problems) + "\n" : "") +
                       (total == 0 ? "Nothing here yet." : shown.Count == 0 ? $"No match among {total:N0}." : $"{shown.Count:N0} of {total:N0}" + (shown.Count >= 500 ? " (type more to narrow it down)" : ""));
    }

    private void Choose()
    {
        if (_grid.SelectedItem is not CredentialHit h) return;
        try { Result = CredentialSearch.Use(h); if (Window.IsVisible) Window.DialogResult = true; }
        catch (OperationCanceledException ex) { _status.Text = "⚠ " + ex.Message; }
    }

    // ---- self-test (the window is built but never shown) ----------------------------------------------------------
    internal List<CredentialHit> ShownForTest => (_grid.ItemsSource as List<CredentialHit>) ?? new();
    internal string StatusForTest => _status.Text;
    internal void QueryForTest(string q) { _query.Text = q; Show(); }
    internal async Task SourceForTest(string source) { _source = source; await LoadAsync(false); }
    internal Credential? ChooseForTest(int index) { _grid.SelectedIndex = index; Choose(); return Result; }
}
