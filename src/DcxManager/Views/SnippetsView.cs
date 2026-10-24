using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using DcxManager.Services;
using DcxManager.Ssh;

namespace DcxManager.Views;

public sealed class RunResult : INotifyPropertyChanged
{
    public required HostEntry Host { get; init; }
    private string _state = "queued", _output = "";
    public string State { get => _state; set { _state = value; On(); } }
    public string Output { get => _output; set { _output = value; On(); On(nameof(FirstLine)); } }
    public string FirstLine => Output.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
    public event PropertyChangedEventHandler? PropertyChanged;
    private void On([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>Saved command snippets, and running a command on many hosts at once (in parallel) with per-host results.</summary>
public sealed class SnippetsView : UserControl, IDisposable
{
    private readonly ListBox _snippets = new() { DisplayMemberPath = "Name" };
    private readonly ListBox _hosts = new() { SelectionMode = SelectionMode.Multiple };
    private readonly TextBox _command = new() { AcceptsReturn = true, Height = 90, TextWrapping = TextWrapping.Wrap, Padding = new Thickness(6, 4, 6, 4) };
    private readonly CheckBox _sudo = new() { Content = "run with sudo", Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly ListView _results = new();
    private readonly TextBox _output = new();
    private readonly ObservableCollection<RunResult> _runs = new();
    private CancellationTokenSource? _cts;

    public SnippetsView()
    {
        _command.Mono();
        _output.Style = (Style)FindResource("Mono");

        // left: snippet library
        var left = new DockPanel { Width = 250, Margin = new Thickness(0, 0, 12, 0) };
        var lbar = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        void LB(string t, RoutedEventHandler h) { var b = new Button { Content = t, Style = (Style)FindResource("SmallButton"), Margin = new Thickness(0, 0, 4, 4) }; b.Click += h; lbar.Children.Add(b); }
        LB("Save as new", (_, _) => SaveNew());
        LB("Update", (_, _) => UpdateSel());
        LB("Delete", (_, _) => DeleteSel());
        var lt = new TextBlock { Text = "SNIPPETS", Style = (Style)FindResource("Label") };
        DockPanel.SetDock(lt, Dock.Top); DockPanel.SetDock(lbar, Dock.Bottom);
        left.Children.Add(lt); left.Children.Add(lbar); left.Children.Add(_snippets);
        _snippets.SelectionChanged += (_, _) => { if (_snippets.SelectedItem is Snippet s) { _command.Text = s.Command; _sudo.IsChecked = s.Sudo; } };
        Menus.ForList(_snippets, left, () => _snippets.SelectedItem is not Snippet s ? Array.Empty<object?>() : new object?[]
        {
            Menus.Item("▶ Run on selected hosts", () => _ = RunAsync(), enabled: _hosts.SelectedItems.Count > 0),
            Menus.Sub("⌨ Send to an open terminal",
                MainWindow.Instance.Panes.AllTabs.Select(t => t.Content).OfType<TerminalView>().Where(t => t.IsConnected)
                    .Select(t => (object?)Menus.Item(t.Host?.Display ?? "terminal", () => t.SendRaw((s.Sudo && t.Host?.Username != "root" ? "sudo " : "") + s.Command + "\r"))).ToArray()),
            Menus.Sep(),
            Menus.Item("Copy command", () => Menus.Clip(s.Command)),
            Menus.Item("Rename…", () => { if (Dialogs.Prompt("Rename snippet", "Name:", s.Name) is { Length: > 0 } n) { s.Name = n; AppData.Save(); } }),
            Menus.Item("Save as .sh…", () => Menus.SaveText(Script(s), SafeName(s) + ".sh", "Shell script|*.sh|All files|*.*")),
            Menus.Item("Share as .sh…", () => ShareService.ShareText(Window.GetWindow(this), $"Snippet — {s.Name}", Script(s), SafeName(s) + ".sh"), enabled: ShareService.Supported),
            Menus.Sep(),
            Menus.AssistItem("✦ Explain this command with AI", a => a.AskAsync("snippet " + s.Name,
                $"{(s.Sudo ? "(runs with sudo)\n" : "")}{s.Command}",
                "Explain what this shell command does step by step, what it changes (if anything), which distros it works on, and any risks or safer variants.")),
        });

        // middle: hosts
        var mid = new DockPanel { Width = 260, Margin = new Thickness(0, 0, 12, 0) };
        var mbar = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        void MB(string t, RoutedEventHandler h) { var b = new Button { Content = t, Style = (Style)FindResource("SmallButton"), Margin = new Thickness(0, 0, 4, 4) }; b.Click += h; mbar.Children.Add(b); }
        MB("All", (_, _) => _hosts.SelectAll());
        MB("None", (_, _) => _hosts.UnselectAll());
        MB("Group…", (_, _) =>
        {
            if (Dialogs.Pick("Select group", "Select every host in:", AppData.Current.Groups.ToList(), g => g) is { } g)
                foreach (ListBoxItem i in _hosts.Items) i.IsSelected = ((HostEntry)i.Tag).Group == g;
        });
        var mt = new TextBlock { Text = "TARGET HOSTS", Style = (Style)FindResource("Label") };
        DockPanel.SetDock(mt, Dock.Top); DockPanel.SetDock(mbar, Dock.Bottom);
        mid.Children.Add(mt); mid.Children.Add(mbar); mid.Children.Add(_hosts);
        Menus.ForList(_hosts, mid);

        // right: command + results
        var right = new DockPanel();
        var ct = new TextBlock { Text = "COMMAND (non-interactive; runs in parallel on each selected host)", Style = (Style)FindResource("Label") };
        var runBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 8) };
        var run = new Button { Content = "▶ Run on selected hosts", Style = (Style)FindResource("PrimaryButton") };
        var stop = new Button { Content = "■ Cancel" };
        run.Click += async (_, _) => await RunAsync();
        stop.Click += (_, _) => _cts?.Cancel();
        runBar.Children.Add(_sudo); runBar.Children.Add(run); runBar.Children.Add(stop);
        var gv = new GridView();
        void Col(string h, string p, double w) => gv.Columns.Add(new GridViewColumn { Header = h, Width = w, DisplayMemberBinding = new Binding(p) });
        Col("Host", "Host.Display", 170); Col("Result", "State", 90); Col("First line", "FirstLine", 400);
        _results.View = gv;
        _results.ItemsSource = _runs;
        _results.Height = 180;
        _results.SelectionChanged += (_, _) => _output.Text = (_results.SelectedItem as RunResult)?.Output ?? "";
        Menus.ForList(_results, runBar, () => _results.SelectedItem is not RunResult r ? Array.Empty<object?>() : new object?[]
        {
            Menus.Item("Copy output", () => Menus.Clip(r.Output), enabled: r.Output.Length > 0),
            Menus.Item("⌨ Terminal on this host", () => MainWindow.Instance.OpenTerminal(r.Host)),
            Menus.Item("▣ Manage this host", () => MainWindow.Instance.OpenServer(r.Host)),
            Menus.Sep(),
            Menus.Item("Copy all results (host, state, output)", () => Menus.Clip(AllResults()), enabled: _runs.Count > 0),
            Menus.Item("Save all results…", () => Menus.SaveText(AllResults(), $"multi-run-{DateTime.Now:yyyyMMdd-HHmm}.txt"), enabled: _runs.Count > 0),
            Menus.Item("Share all results…", () => ShareService.ShareText(Window.GetWindow(this), "Multi-host run results", AllResults(), $"multi-run-{DateTime.Now:yyyyMMdd-HHmm}.txt"), enabled: ShareService.Supported && _runs.Count > 0),
        });
        Menus.ForText(_output);
        Menus.ForText(_command);
        DockPanel.SetDock(ct, Dock.Top); DockPanel.SetDock(_command, Dock.Top); DockPanel.SetDock(runBar, Dock.Top); DockPanel.SetDock(_results, Dock.Top);
        _output.Margin = new Thickness(0, 8, 0, 0);
        right.Children.Add(ct); right.Children.Add(_command); right.Children.Add(runBar); right.Children.Add(_results); right.Children.Add(_output);

        var body = new DockPanel();
        DockPanel.SetDock(left, Dock.Left); DockPanel.SetDock(mid, Dock.Left);
        body.Children.Add(left); body.Children.Add(mid); body.Children.Add(right);
        var root = new DockPanel { Margin = new Thickness(14, 12, 14, 12) };
        var title = new TextBlock { Text = "Snippets & multi-host run", Style = (Style)FindResource("H1"), Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(title, Dock.Top);
        root.Children.Add(title); root.Children.Add(body);
        Content = root;

        AppData.Changed += Reload;
        Reload();
    }

    public void Dispose() { AppData.Changed -= Reload; _cts?.Cancel(); }

    private static string Script(Snippet s) =>
        $"#!/usr/bin/env bash\n# {s.Name}{(s.Description.Length > 0 ? " — " + s.Description : "")}\n# exported from DarkCipher ServerManagement {DateTime.Now:yyyy-MM-dd}\nset -euo pipefail\n\n{(s.Sudo ? "sudo " : "")}{s.Command}\n";

    private static string SafeName(Snippet s) => string.Concat(s.Name.Select(c => char.IsLetterOrDigit(c) ? c : '-')).Trim('-') is { Length: > 0 } n ? n : "snippet";

    private string AllResults()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var r in _runs) sb.Append("=== ").Append(r.Host.Display).Append(" — ").Append(r.State).Append('\n').Append(r.Output.TrimEnd()).Append("\n\n");
        return sb.ToString();
    }

    private void Reload()
    {
        _snippets.ItemsSource = null;
        _snippets.ItemsSource = AppData.Current.Snippets;
        var selected = _hosts.Items.OfType<ListBoxItem>().Where(i => i.IsSelected).Select(i => ((HostEntry)i.Tag).Id).ToHashSet();
        _hosts.Items.Clear();
        foreach (var h in AppData.Current.Hosts.OrderBy(h => h.Group).ThenBy(h => h.Display))
            _hosts.Items.Add(new ListBoxItem { Content = $"{h.Display}   [{h.Group}]", Tag = h, IsSelected = selected.Contains(h.Id), Padding = new Thickness(4, 2, 4, 2) });
    }

    private void SaveNew()
    {
        if (!Restrictions.Check("snippet.edit")) return;
        if (_command.Text.Trim().Length == 0) return;
        if (Dialogs.Prompt("Save snippet", "Name:") is not { Length: > 0 } name) return;
        AppData.Current.Snippets.Add(new Snippet { Name = name, Command = _command.Text.Trim(), Sudo = _sudo.IsChecked == true });
        AppData.Save();
    }

    private void UpdateSel()
    {
        if (!Restrictions.Check("snippet.edit")) return;
        if (_snippets.SelectedItem is not Snippet s) return;
        s.Command = _command.Text.Trim(); s.Sudo = _sudo.IsChecked == true;
        AppData.Save();
    }

    private void DeleteSel()
    {
        if (!Restrictions.Check("snippet.edit")) return;
        if (_snippets.SelectedItem is not Snippet s || !Dialogs.Confirm("Delete snippet", $"Delete “{s.Name}”?", "Delete", danger: true)) return;
        AppData.Current.Snippets.Remove(s);
        AppData.Save();
    }

    private async Task RunAsync()
    {
        var hosts = _hosts.Items.OfType<ListBoxItem>().Where(i => i.IsSelected).Select(i => (HostEntry)i.Tag).ToList();
        var cmd = _command.Text.Trim();
        if (hosts.Count == 0 || cmd.Length == 0) { Dialogs.Info("Run", "Choose at least one host and enter a command."); return; }
        bool sudo = _sudo.IsChecked == true;
        // Grouped by environment; any PRODUCTION host means typing PRODUCTION (ADR-017).
        if (!Envs.ConfirmMany(hosts, "Run on hosts", $"Run on {hosts.Count} host(s){(sudo ? " as root (sudo)" : "")}:\n\n{cmd}", "Run", danger: sudo)) return;
        _runs.Clear();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var results = hosts.Select(h => new RunResult { Host = h }).ToList();
        foreach (var r in results) _runs.Add(r);
        using var gate = new SemaphoreSlim(8);
        await Task.WhenAll(results.Select(async r =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(true);
            try
            {
                r.State = "running…";
                var res = await SshPool.RunAsync(r.Host, cmd, sudo, ct, TimeSpan.FromMinutes(10));
                r.Output = res.Combined;
                r.State = res.Ok ? "✓ ok" : $"✕ exit {res.ExitCode}";
                AuditLog.Write("you", r.Host.Display, "multi-run: " + (sudo ? "sudo " : "") + cmd, res.Ok ? "ok" : $"exit {res.ExitCode}");
            }
            catch (OperationCanceledException) { r.State = "cancelled"; }
            catch (Exception ex) { r.State = "✕ error"; r.Output = ex.Message; }
            finally { gate.Release(); }
        }));
    }
}
