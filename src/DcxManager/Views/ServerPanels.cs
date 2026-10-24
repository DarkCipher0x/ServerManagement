using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using DcxManager.Services;
using DcxManager.Ssh;

namespace DcxManager.Views;

/// <summary>An action on a selected row. Command gets the row's columns. Terminal = run it in a new terminal tab (interactive).</summary>
public sealed record RowAction(string Label, Func<string[], string> Command, bool Sudo = false, string? Confirm = null,
    bool ShowOutput = false, bool Terminal = false, bool Danger = false, bool NoRow = false);

/// <summary>A table filled from a shell command's output, with actions on the selected row.</summary>
public sealed class CommandTable : UserControl
{
    private readonly HostEntry _host;
    private readonly string _command;
    private readonly bool _sudo;
    private readonly Func<string, List<string[]>> _parse;
    private readonly ListView _list = new();
    private readonly TextBox _filter = Hint.Set(new TextBox { Width = 200, Padding = new Thickness(5, 3, 5, 3) }, "Filter rows…");
    private readonly TextBlock _status = new TextBlock() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) }.Fs(FontStep.Small);
    private List<string[]> _rows = new();
    private bool _loaded;

    public CommandTable(HostEntry host, string command, bool sudo, string[] headers, double[] widths, Func<string, List<string[]>> parse, params RowAction[] actions)
    {
        _host = host; _command = command; _sudo = sudo; _parse = parse;
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var gv = new GridView();
        for (int i = 0; i < headers.Length; i++)
            gv.Columns.Add(new GridViewColumn { Header = headers[i], Width = i < widths.Length ? widths[i] : 120, DisplayMemberBinding = new Binding($"[{i}]") });
        _list.View = gv;
        _list.SelectionMode = SelectionMode.Single;

        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        var refresh = new Button { Content = "↻ Refresh", Style = (Style)Application.Current.Resources["SmallButton"] };
        refresh.Click += async (_, _) => await LoadAsync();
        bar.Children.Add(refresh);
        bar.Children.Add(_filter);
        _filter.ToolTip = "Filter rows";
        _filter.TextChanged += (_, _) => Apply();
        bar.Children.Add(new Border { Width = 12 });
        foreach (var a in actions)
        {
            var b = new Button { Content = a.Label, Style = (Style)Application.Current.Resources[a.Danger ? "DangerButton" : "SmallButton"], Margin = new Thickness(0, 0, 4, 4) };
            if (a.Danger) { b.MinWidth = 0; b.Padding = new Thickness(8, 3, 8, 3); b.Fs(FontStep.Small); }
            b.Click += async (_, _) => await RunAction(a);
            bar.Children.Add(b);
        }
        bar.Children.Add(_status);

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(_list);
        Content = root;
        IsVisibleChanged += async (_, _) => { if (IsVisible && !_loaded) await LoadAsync(); };
        Menus.ForList(_list, this, () => _list.SelectedItem is string[] row ? new object?[]
        {
            Menus.AssistItem("✦ Ask AI about this row", a =>
            {
                a.AskOperator($"On {_host.Display}, from `{_command}`:\n```\n{string.Join("  ", row)}\n```\nWhat is this and does anything need attention?");
                return Task.CompletedTask;
            }, use: AssistUse.Operator),
        } : Array.Empty<object?>());
        if (actions.FirstOrDefault(a => !a.Danger && !a.NoRow) is { } first)
            _list.MouseDoubleClick += async (_, _) => { if (_list.SelectedItem != null) await RunAction(first); };
    }

    public async Task LoadAsync()
    {
        _loaded = true;
        _status.Text = "loading…";
        try
        {
            var r = await SshPool.RunAsync(_host, _command, _sudo);
            _rows = _parse(r.Output);
            Apply();
            _status.Text = $"{_rows.Count} rows · {DateTime.Now:HH:mm:ss}" + (r.Ok || _rows.Count > 0 ? "" : " · " + r.Error.Split('\n')[0]);
        }
        catch (Exception ex) { _status.Text = "✕ " + ex.Message.Split('\n')[0]; }
    }

    private void Apply()
    {
        var q = _filter.Text.Trim();
        _list.ItemsSource = q.Length == 0 ? _rows : _rows.Where(r => r.Any(c => c.Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    private async Task RunAction(RowAction a)
    {
        var row = _list.SelectedItem as string[];
        if (row == null && !a.NoRow) { _status.Text = "select a row first"; return; }
        var cmd = a.Command(row ?? Array.Empty<string>());
        if (string.IsNullOrWhiteSpace(cmd)) return;   // prompt cancelled
        if (a.Terminal) { MainWindow.Instance.OpenTerminal(_host, a.Sudo && _host.Username != "root" ? "sudo " + cmd : cmd); return; }
        // PRODUCTION (ADR-017): every action that changes something asks, with the host's name typed; elsewhere as before.
        bool changes = a.Confirm != null || a.Danger || (a.Sudo && !a.ShowOutput);
        var question = (a.Confirm != null ? string.Format(a.Confirm, row ?? Array.Empty<object>()) : $"{a.Label.Trim()} on {_host.Display}?") + $"\n\n{_host.Display}:  {cmd}";
        if (Envs.IsProduction(Envs.For(_host)) && changes) { if (!Envs.Confirm(_host, a.Label.Trim(), question, a.Label.Trim())) return; }
        else if (a.Confirm != null && !Envs.Confirm(_host, a.Label.Trim(), question, a.Label.Trim(), a.Danger)) return;
        _status.Text = $"running: {cmd}";
        try
        {
            var r = await SshPool.RunAsync(_host, cmd, a.Sudo, timeout: TimeSpan.FromMinutes(5));
            if (!a.ShowOutput) AuditLog.Write("you", _host.Display, (a.Sudo ? "sudo " : "") + cmd, r.Ok ? "ok" : $"exit {r.ExitCode}");
            if (a.ShowOutput || !r.Ok) Dialogs.ShowText($"{_host.Display} — {cmd}", r.Combined.Length > 0 ? r.Combined : $"(no output, exit {r.ExitCode})");
            _status.Text = r.Ok ? $"✓ {cmd}" : $"✕ exit {r.ExitCode}: {cmd}";
            if (!a.ShowOutput) await LoadAsync();
        }
        catch (Exception ex) { _status.Text = "✕ " + ex.Message.Split('\n')[0]; }
    }

    // ---- parsers -------------------------------------------------------------------------------------------------

    /// <summary>Whitespace columns; the last column takes the rest of the line.</summary>
    public static Func<string, List<string[]>> Columns(int count, int skip = 0) => text =>
        text.Split('\n').Skip(skip).Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0)
            .Select(l => l.Split((char[]?)null, count, StringSplitOptions.RemoveEmptyEntries)).Where(p => p.Length > 0)
            .Select(p => p.Length < count ? p.Concat(Enumerable.Repeat("", count - p.Length)).ToArray() : p).ToList();

    public static Func<string, List<string[]>> Split(char sep) => text =>
        text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).Select(l => l.Split(sep)).ToList();
}

/// <summary>Read-only command output with a refresh button and optional extra buttons.</summary>
public sealed class CommandText : UserControl
{
    private readonly HostEntry _host;
    private readonly Func<string> _command;
    private readonly bool _sudo;
    private readonly TextBox _text = new();
    private readonly TextBlock _status = new TextBlock() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) }.Fs(FontStep.Small);
    private bool _loaded;

    public WrapPanel Bar { get; } = new() { Margin = new Thickness(0, 0, 0, 8) };
    public string Text => _text.Text;

    public CommandText(HostEntry host, Func<string> command, bool sudo, bool autoLoad = true)
    {
        _host = host; _command = command; _sudo = sudo;
        _text.Style = (Style)Application.Current.Resources["Mono"];
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var refresh = new Button { Content = "↻ Refresh", Style = (Style)Application.Current.Resources["SmallButton"] };
        refresh.Click += async (_, _) => await LoadAsync();
        Bar.Children.Add(refresh);
        var root = new DockPanel();
        var top = new DockPanel();
        DockPanel.SetDock(_status, Dock.Right);
        top.Children.Add(_status);
        top.Children.Add(Bar);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(_text);
        Content = root;
        Menus.ForText(_text, sel => new object?[]
        {
            Menus.Item("↻ Refresh", () => _ = LoadAsync()),
            Menus.Item("⌗ Digest into patterns (offline)", () => ShowDigest(_host, sel.Length > 0 ? sel : _text.Text), enabled: _text.Text.Length > 0),
            Menus.AssistItem("✦ Explain selection with AI", a =>
            {
                a.AskOperator($"Explain this output from {_host.Display} and tell me if anything needs action:\n```\n{(sel.Length > 6000 ? sel[^6000..] : sel)}\n```");
                return Task.CompletedTask;
            }, enabled: sel.Length > 0, use: AssistUse.LogAnalysis),
        });
        if (autoLoad) IsVisibleChanged += async (_, _) => { if (IsVisible && !_loaded) await LoadAsync(); };
    }

    public Button AddButton(string label, Func<Task> click, string? tip = null)
    {
        var b = new Button { Content = label, Style = (Style)Application.Current.Resources["SmallButton"], ToolTip = tip };
        b.Click += async (_, _) => await click();
        Bar.Children.Add(b);
        return b;
    }

    public void AddControl(FrameworkElement e) { e.Margin = new Thickness(0, 0, 6, 0); e.VerticalAlignment = VerticalAlignment.Center; Bar.Children.Add(e); }

    public async Task LoadAsync()
    {
        _loaded = true;
        _status.Text = "loading…";
        try
        {
            var cmd = _command();
            var r = await SshPool.RunAsync(_host, cmd, _sudo, timeout: TimeSpan.FromMinutes(2));
            _text.Text = r.Combined;
            _text.ScrollToEnd();
            _status.Text = $"{(r.Ok ? "✓" : $"exit {r.ExitCode}")} · {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex) { _status.Text = "✕ " + ex.Message.Split('\n')[0]; }
    }

    public void SetText(string s) => _text.Text = s;

    /// <summary>Offline pattern digest of some log text, in a text window.</summary>
    public static void ShowDigest(HostEntry host, string text) => ShowDigest(host.Display, text);

    public static void ShowDigest(string title, string text)
    {
        var patterns = Reports.LogDigest.Digest(text, 80);
        int lines = text.Count(c => c == '\n');
        var head = $"{lines:N0} lines → {patterns.Count} patterns (variable parts folded: <n> numbers, <ip> addresses, <hex>/<uuid> ids). Most severe first.\n\n";
        Dialogs.ShowText($"Digest — {title}", head + Reports.LogDigest.Render(patterns, 60_000));
    }
}

/// <summary>A stat tile: label, big value, optional bar.</summary>
public sealed class StatTile : Border
{
    private readonly TextBlock _value = new TextBlock() { FontWeight = FontWeights.Bold }.Fs(FontStep.Value);
    private readonly TextBlock _sub = new TextBlock() { TextWrapping = TextWrapping.Wrap }.Fs(FontStep.Small);
    private readonly ProgressBar _bar = new() { Height = 4, Maximum = 100, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };

    public StatTile(string label, double width = 210)
    {
        Style = (Style)Application.Current.Resources["Card"];
        Width = width;
        var p = new StackPanel();
        p.Children.Add(new TextBlock { Text = label.ToUpperInvariant(), Style = (Style)Application.Current.Resources["Label"] });
        _value.Mono();
        _value.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        _sub.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        p.Children.Add(_value); p.Children.Add(_sub); p.Children.Add(_bar);
        Child = p;
        _value.Text = "—";
    }

    public void Set(string value, string sub = "", double? percent = null, bool alert = false)
    {
        _value.Text = value;
        _sub.Text = sub;
        _value.SetResourceReference(TextBlock.ForegroundProperty, alert ? "Bad" : "Fg");
        if (percent is { } pc)
        {
            _bar.Visibility = Visibility.Visible;
            _bar.Value = pc;
            _bar.SetResourceReference(ProgressBar.ForegroundProperty, pc > 90 ? "Bad" : pc > 75 ? "Warn" : "Accent");
        }
    }
}
