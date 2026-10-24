using System.IO;
using System.Windows;
using System.Windows.Controls;
using DcxManager.Services;
using DcxManager.Sessions;
using DcxManager.Wsl;
using Microsoft.Win32;

namespace DcxManager.Views;

/// <summary>
/// LOCAL → WSL (0.5.0, ADR-064): the distributions on this PC with their state, version and disk; open a terminal or the
/// files; start, stop, shut WSL down, set the default, convert, export, import, unregister, update; each distribution's
/// /etc/wsl.conf, /etc/hosts and resolv.conf and its addresses and listening ports; the global .wslconfig as a form; and
/// WSL Containers (wslc) when installed. Changes ask first and go to the audit log; files are backed up before a write.
/// </summary>
public sealed class WslView : UserControl, IDisposable
{
    private readonly DataGrid _list = new() { IsReadOnly = true, AutoGenerateColumns = false, Height = 200, SelectionMode = DataGridSelectionMode.Single, HeadersVisibility = DataGridHeadersVisibility.Column };
    private readonly ComboBox _fileDistro = new() { Width = 200 };
    private readonly ComboBox _file = new() { Width = 180, ItemsSource = WslManager.EditableFiles, SelectedIndex = 0 };
    private readonly TextBox _editor = new() { AcceptsReturn = true, AcceptsTab = true, Height = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly StackPanel _cfgForm = new();
    private readonly Dictionary<string, TextBox> _cfgBoxes = new();
    private readonly TextBox _cfgRaw = new() { AcceptsReturn = true, Height = 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly DataGrid _containers = new() { IsReadOnly = true, AutoGenerateColumns = false, Height = 200, HeadersVisibility = DataGridHeadersVisibility.Column };
    private readonly ComboBox _cKind = new() { Width = 120, ItemsSource = new[] { "containers", "images", "volumes", "networks" }, SelectedIndex = 0 };
    private readonly TextBox _image = new() { Width = 260, Padding = new Thickness(5, 4, 5, 4), ToolTip = "Image to pull, e.g. docker.io/library/alpine:latest" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6) };
    private readonly TextBox _out = new() { IsReadOnly = true, AcceptsReturn = true, Height = 160, TextWrapping = TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly List<Button> _buttons = new();
    private CancellationTokenSource _cts = new();
    private List<WslManager.Distro> _distros = new();
    private HashSet<string> _caps = new();

    /// <summary>The self-test answers confirmations here (no dialog).</summary>
    internal Func<string, bool>? ConfirmForTest { get; set; }

    public WslView()
    {
        var root = new DockPanel { Margin = new Thickness(14, 12, 14, 12) };
        var head = new StackPanel();
        head.Children.Add(new TextBlock { Text = "WSL", Style = (Style)FindResource("H1") });
        head.Children.Add(new TextBlock { Text = "The Windows Subsystem for Linux on this PC: distributions, their configuration and network, the global .wslconfig, and WSL Containers. Everything runs wsl.exe / wslc.exe locally; nothing leaves this PC except an install or image pull you start.", Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8) });
        var tabs = new TabControl();

        // Distributions
        void Col(DataGrid g, string h, string p, double w) => g.Columns.Add(new DataGridTextColumn { Header = h, Binding = new System.Windows.Data.Binding(p), Width = w });
        Col(_list, "", nameof(Row.Default), 26); Col(_list, "Distribution", nameof(Row.Name), 200); Col(_list, "State", nameof(Row.State), 100); Col(_list, "WSL", nameof(Row.Version), 50); Col(_list, "Disk", nameof(Row.Disk), 90); Col(_list, "Location", nameof(Row.Path), 360);
        _list.MouseDoubleClick += (_, _) => OpenTerminal();
        var dTab = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        dTab.Children.Add(Bar(Run("↻ Refresh", RefreshAsync, primary: true), Btn("⌨ Open terminal", OpenTerminal), Btn("🗀 Open files", OpenFiles),
            Btn("⧉ Open in IDE", () => { if (Selected is { } d) IdeUi.OpenWsl(d); }),
            Run("▶ Start", () => OnSelected("Start", d => WslManager.StartAsync(d, _cts.Token), change: false)),
            Run("■ Terminate", () => OnSelected("Terminate", d => WslManager.TerminateAsync(d, _cts.Token), change: true)),
            Run("★ Set default", () => OnSelected("Set default", d => WslManager.SetDefaultAsync(d, _cts.Token), change: true)),
            Run("⇄ Convert v1↔v2", () => OnSelected("Convert", d => WslManager.SetVersionAsync(d, _distros.First(x => x.Name == d).Version == 2 ? 1 : 2, _cts.Token), change: true))));
        dTab.Children.Add(_list);
        dTab.Children.Add(Bar(Run("⤓ Export…", ExportAsync), Run("⤒ Import…", ImportAsync), Run("✕ Unregister…", UnregisterAsync),
            Run("⏻ Shut down WSL", () => Go("Shut down WSL", () => WslManager.ShutdownAsync(_cts.Token), change: true)),
            Run("Status / version", async () => { await Go("Status", () => WslManager.StatusAsync(_cts.Token), false); await Go("Version", () => WslManager.VersionAsync(_cts.Token), false, append: true); }),
            Run("⟳ Update WSL", () => Go("wsl --update", () => WslManager.UpdateAsync(_cts.Token), change: true, online: true)),
            Run("Available online", () => Go("wsl --list --online", () => WslManager.OnlineAsync(_cts.Token), false, online: true)),
            Run("＋ Install…", InstallAsync)));
        tabs.Items.Add(new TabItem { Header = "Distributions", Content = dTab });

        // Files & network
        _editor.Mono();
        var fTab = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        fTab.Children.Add(Bar(Label("distribution"), _fileDistro, Label("file"), _file, Run("Load", LoadFileAsync), Run("💾 Save (root, backed up)…", SaveFileAsync), Run("Network & ports", () => OnDistro(_fileDistro, "Network", d => WslManager.NetworkAsync(d, _cts.Token)))));
        fTab.Children.Add(new TextBlock { Text = "/etc/wsl.conf: [boot] systemd=true · [automount] options · [network] generateHosts / generateResolvConf / hostname · [interop] · [user] default=. Restart the distribution (Terminate) to apply.", Style = (Style)FindResource("Caption"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) });
        fTab.Children.Add(_editor);
        tabs.Items.Add(new TabItem { Header = "Config & network files", Content = fTab });

        // .wslconfig
        var cTab = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        cTab.Children.Add(new TextBlock { Text = $"{WslManager.WslConfigPath} — the [wsl2] settings for every distribution. Empty = WSL's default. Saving keeps a dated copy and applies after “Shut down WSL”.", Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap });
        foreach (var (key, title, _) in WslManager.WslConfigKeys)
        {
            var box = new TextBox { Width = 160, Padding = new Thickness(5, 3, 5, 3) };
            _cfgBoxes[key] = box;
            _cfgForm.Children.Add(Bar(new TextBlock { Text = title, Width = 260, VerticalAlignment = VerticalAlignment.Center }, box));
        }
        cTab.Children.Add(_cfgForm);
        cTab.Children.Add(Bar(Btn("↻ Reload", LoadWslConfig), Btn("💾 Save .wslconfig", SaveWslConfig)));
        _cfgRaw.Mono(); _cfgRaw.IsReadOnly = true;
        cTab.Children.Add(Label("the file as it is"));
        cTab.Children.Add(_cfgRaw);
        tabs.Items.Add(new TabItem { Header = ".wslconfig", Content = cTab });

        // Containers
        var kTab = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        kTab.Children.Add(new TextBlock { Text = WslManager.WslcExe is { } x ? $"WSL Containers: {x}" : "WSL Containers (wslc) aren't installed: “Update WSL” brings them (WSL 2.9.3 or later).", Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap });
        kTab.Children.Add(Bar(_cKind, Run("↻ List", ListContainersAsync, primary: true), Run("▶ Start", () => ContainerAsync("start")), Run("■ Stop", () => ContainerAsync("stop")),
            Run("✕ Remove…", () => ContainerAsync("remove")), Run("Logs", () => ContainerAsync("logs")), Run("Inspect", () => ContainerAsync("inspect")), Btn("⌨ Shell", ContainerShell)));
        kTab.Children.Add(Bar(Label("image"), _image, Run("⇩ Pull", PullAsync)));
        kTab.Children.Add(_containers);
        tabs.Items.Add(new TabItem { Header = "Containers", Content = kTab });

        head.Children.Add(tabs);
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        head.Children.Add(_status);
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);
        _out.Mono();
        root.Children.Add(_out);
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Loaded += async (_, _) => { if (_distros.Count == 0 && !SelfTest.Running) { try { await RefreshAsync(); } catch (Exception ex) { _status.Text = "✕ " + ex.Message; } } LoadWslConfig(); };
    }

    public sealed record Row(string Default, string Name, string State, int Version, string Disk, string Path);

    private string? Selected => (_list.SelectedItem as Row)?.Name;

    internal async Task RefreshAsync()
    {
        if (!WslManager.Installed) { _status.Text = "WSL isn't installed on this PC: run “wsl --install” in an administrator terminal (needs a restart)."; return; }
        _distros = await WslManager.ListAsync(_cts.Token);
        var sel = Selected;
        _list.ItemsSource = _distros.Select(d => new Row(d.IsDefault ? "★" : "", d.Name, d.State, d.Version, d.DiskBytes > 0 ? $"{d.DiskBytes / 1024d / 1024 / 1024:0.0} GB" : "", d.BasePath)).ToList();
        _list.SelectedItem = (_list.ItemsSource as List<Row>)?.FirstOrDefault(r => r.Name == sel) ?? (_list.ItemsSource as List<Row>)?.FirstOrDefault();
        var fsel = _fileDistro.SelectedItem as string;
        _fileDistro.ItemsSource = _distros.Select(d => d.Name).ToList();
        _fileDistro.SelectedItem = fsel != null && _distros.Any(d => d.Name == fsel) ? fsel : _distros.FirstOrDefault()?.Name;
        _status.Text = $"{_distros.Count} distribution(s), {_distros.Count(d => d.State.Equals("Running", StringComparison.OrdinalIgnoreCase))} running.";
        LocalShells.Refresh();
    }

    private bool Confirm(string title, string text, bool danger = false, string? type = null) =>
        ConfirmForTest?.Invoke(title) ?? Dialogs.Confirm(title, text, title, danger: danger, typeToConfirm: type);

    private async Task Go(string title, Func<Task<WslManager.Result>> run, bool change, bool append = false, bool online = false)
    {
        if (online && Offline.On) throw new InvalidOperationException("Offline mode is on: this needs the internet (Microsoft's WSL download servers).");
        if (change && !Confirm(title, title + "?")) return;
        _status.Text = title + "…";
        var r = await run();
        if (!append) _out.Clear();
        _out.AppendText($"$ {title}\n{Redaction.Mask(r.Combined)}\n");
        _status.Text = r.Ok ? title + ": done." : $"{title}: exit {r.ExitCode} — see the output.";
        if (change) AuditLog.Write("you", "wsl", title, $"exit {r.ExitCode}");
    }

    private Task OnSelected(string title, Func<string, Task<WslManager.Result>> run, bool change)
    {
        var d = Selected ?? throw new InvalidOperationException("Select a distribution.");
        return Go($"{title}: {d}", () => run(d), change).ContinueWith(_ => Dispatcher.InvokeAsync(RefreshAsync), TaskScheduler.FromCurrentSynchronizationContext());
    }

    private Task OnDistro(ComboBox c, string title, Func<string, Task<WslManager.Result>> run)
    {
        var d = c.SelectedItem as string ?? throw new InvalidOperationException("Pick a distribution.");
        return Go($"{title}: {d}", () => run(d), change: false);
    }

    private void OpenTerminal()
    {
        if (Selected is not { } d) return;
        LocalShells.Refresh();
        if (LocalShells.Find("wsl:" + d) is { } p) MainWindow.Instance.OpenLocal(p);
        else _status.Text = "That distribution isn't available as a shell yet (refresh).";
    }

    private void OpenFiles()
    {
        if (Selected is not { } d) return;
        MainWindow.Instance.Navigate("commander");
        _status.Text = $"File commander: open \\\\wsl.localhost\\{d}\\ in a This PC pane.";
        if (CommanderView.Current is { } cv) _ = cv.OpenLocalPathAsync($@"\\wsl.localhost\{WslManager.Name(d)}\");
    }

    private async Task ExportAsync()
    {
        var d = Selected ?? throw new InvalidOperationException("Select a distribution.");
        var dlg = new SaveFileDialog { FileName = $"{d}-{DateTime.Now:yyyyMMdd}.tar", Filter = "Tar archive (*.tar)|*.tar|Virtual disk (*.vhdx)|*.vhdx" };
        if (dlg.ShowDialog() != true) return;
        await Go($"Export {d}", () => WslManager.ExportAsync(d, dlg.FileName, dlg.FileName.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase), _cts.Token), change: false);
        AuditLog.Write("you", "wsl", "distribution exported", d);
        _status.Text += " The export is a plain copy of the distribution (not encrypted): keep it somewhere safe.";
    }

    private async Task ImportAsync()
    {
        var dlg = new OpenFileDialog { Filter = "Distribution (*.tar;*.tar.gz;*.vhdx)|*.tar;*.tar.gz;*.vhdx" };
        if (dlg.ShowDialog() != true) return;
        if (Dialogs.Prompt("Import distribution", "Name for it (letters, digits, . _ -):", Path.GetFileNameWithoutExtension(dlg.FileName).Split('-')[0]) is not { Length: > 0 } name) return;
        var folder = new OpenFolderDialog { Title = "Folder for its virtual disk" };
        if (folder.ShowDialog() != true) return;
        await Go($"Import {name}", () => WslManager.ImportAsync(WslManager.Name(name), folder.FolderName, dlg.FileName, _cts.Token), change: true);
        await RefreshAsync();
    }

    private async Task UnregisterAsync()
    {
        var d = Selected ?? throw new InvalidOperationException("Select a distribution.");
        if (!Confirm("Unregister", $"Unregister “{d}”? Its virtual disk and EVERYTHING inside it is deleted. Export it first if you may need it.", danger: true, type: d)) return;
        await Go($"Unregister {d}", () => WslManager.UnregisterAsync(d, _cts.Token), change: false);
        AuditLog.Write("you", "wsl", "distribution unregistered", d);
        await RefreshAsync();
    }

    private async Task InstallAsync()
    {
        if (Dialogs.Prompt("Install a distribution", "Name from “Available online” (e.g. Ubuntu-24.04, Debian, AlmaLinux-9):") is not { Length: > 0 } name) return;
        await Go($"Install {name}", () => WslManager.InstallAsync(WslManager.Name(name), _cts.Token), change: true, online: true);
        await RefreshAsync();
    }

    private async Task LoadFileAsync()
    {
        var d = _fileDistro.SelectedItem as string ?? throw new InvalidOperationException("Pick a distribution.");
        var r = await WslManager.ReadFileAsync(d, (string)_file.SelectedItem, _cts.Token);
        _editor.Text = r.Ok ? r.Output : "";
        _status.Text = r.Ok ? $"{_file.SelectedItem} on {d}: {r.Output.Split('\n').Length} line(s)." : $"✕ {r.Combined}";
    }

    private async Task SaveFileAsync()
    {
        var d = _fileDistro.SelectedItem as string ?? throw new InvalidOperationException("Pick a distribution.");
        var f = (string)_file.SelectedItem;
        if (!Confirm("Save", $"Write {f} in {d} as root? The current file is kept as {f}.dcx-bak.")) return;
        await Go($"Save {f} on {d}", () => WslManager.WriteFileAsync(d, f, _editor.Text, _cts.Token), change: false);
        AuditLog.Write("you", "wsl", "file saved", $"{d}:{f}");
    }

    private void LoadWslConfig()
    {
        var text = File.Exists(WslManager.WslConfigPath) ? File.ReadAllText(WslManager.WslConfigPath) : "";
        var v = WslManager.ReadWslConfig(text);
        foreach (var (k, box) in _cfgBoxes) box.Text = v.GetValueOrDefault(k, "");
        _cfgRaw.Text = text.Length > 0 ? text : "(no .wslconfig yet: WSL uses its defaults)";
    }

    private void SaveWslConfig()
    {
        try
        {
            var text = File.Exists(WslManager.WslConfigPath) ? File.ReadAllText(WslManager.WslConfigPath) : "";
            var updated = WslManager.SetWslConfig(text, _cfgBoxes.ToDictionary(kv => kv.Key, kv => kv.Value.Text.Trim()));
            if (!Confirm("Save .wslconfig", "Save .wslconfig? It applies to every distribution after “Shut down WSL”.")) return;
            var backup = WslManager.SaveWslConfig(updated);
            AuditLog.Write("you", "wsl", ".wslconfig saved", backup.Length > 0 ? "previous copy kept" : "new file");
            _status.Text = "✓ .wslconfig saved" + (backup.Length > 0 ? $" (previous copy: {Path.GetFileName(backup)})" : "") + ". Shut down WSL to apply.";
            LoadWslConfig();
        }
        catch (ArgumentException ex) { Dialogs.Error(".wslconfig", ex.Message); }
    }

    private async Task ListContainersAsync()
    {
        if (_caps.Count == 0 && WslManager.WslcExe != null) _caps = await WslManager.CapabilitiesAsync(_cts.Token);
        var kind = _cKind.SelectedItem as string ?? "containers";
        var group = kind.TrimEnd('s');
        var args = kind == "containers" ? new[] { "container", "list", "--all" } : new[] { group, "list" };
        var r = await WslManager.Wslc(_cts.Token, args);
        if (!r.Ok && kind == "containers") r = await WslManager.Wslc(_cts.Token, "container", "list");   // --all may be spelled differently
        var (cols, rows) = WslManager.ParseTable(r.Output);
        _containers.Columns.Clear();
        for (int i = 0; i < cols.Count; i++) _containers.Columns.Add(new DataGridTextColumn { Header = cols[i], Binding = new System.Windows.Data.Binding($"[{i}]") });
        _containers.ItemsSource = rows;
        _status.Text = r.Ok ? $"{rows.Count} {kind}." : $"✕ {r.Combined}";
    }

    private string SelectedContainer() => (_containers.SelectedItem as List<string>)?.FirstOrDefault() is { Length: > 0 } id ? WslManager.Id(id) : throw new InvalidOperationException("Select a container (List first).");

    private async Task ContainerAsync(string verb)
    {
        var id = SelectedContainer();
        if (verb == "remove" && !Confirm("Remove container", $"Remove the container {id}? Its writable layer is deleted.", danger: true)) return;
        if (verb is "start" or "stop" && !Confirm(verb, $"{verb} the container {id}?")) return;
        var args = verb == "logs" ? new[] { "container", "logs", id } : new[] { "container", verb, id };
        await Go($"wslc {string.Join(' ', args)}", () => WslManager.Wslc(_cts.Token, args), change: false);
        if (verb is not ("logs" or "inspect")) { AuditLog.Write("you", "wsl", "container " + verb, id); await ListContainersAsync(); }
    }

    private async Task PullAsync()
    {
        var image = WslManager.Image(_image.Text);
        await Go($"wslc image pull {image}", () => WslManager.Wslc(_cts.Token, "image", "pull", image), change: true, online: true);
    }

    private void ContainerShell()
    {
        try
        {
            var id = SelectedContainer();
            var exe = WslManager.WslcExe ?? throw new InvalidOperationException("wslc isn't installed.");
            var p = new LocalShellProfile("wslc:" + id, id, "▣", $"\"{exe}\" exec -it {id} sh", null, false, "WSL container shell");
            MainWindow.Instance.OpenLocal(p);
        }
        catch (Exception ex) { _status.Text = "✕ " + ex.Message; }
    }

    private static TextBlock Label(string t) => new() { Text = t, Style = (Style)Application.Current.Resources["Label"], VerticalAlignment = VerticalAlignment.Center };
    private static WrapPanel Bar(params UIElement[] items) { var w = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) }; foreach (var i in items) { if (i is FrameworkElement f) { f.Margin = new Thickness(0, 0, 8, 4); f.VerticalAlignment = VerticalAlignment.Center; } w.Children.Add(i); } return w; }
    private static Button Btn(string t, Action a) { var b = new Button { Content = t }; b.Click += (_, _) => a(); return b; }

    private Button Run(string t, Func<Task> a, bool primary = false)
    {
        var b = new Button { Content = t };
        if (primary) b.Style = (Style)FindResource("PrimaryButton");
        b.Click += async (_, _) =>
        {
            foreach (var x in _buttons) x.IsEnabled = false;
            try { await a(); }
            catch (OperationCanceledException) { _status.Text = "Stopped."; }
            catch (Exception ex) { _status.Text = "✕ " + ex.Message; }
            finally { foreach (var x in _buttons) x.IsEnabled = true; }
        };
        _buttons.Add(b);
        return b;
    }

    public void Dispose() { _cts.Cancel(); _cts.Dispose(); }
}
