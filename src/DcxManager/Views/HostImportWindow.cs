using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using DcxManager.Net;
using DcxManager.Services;
using DcxManager.Ssh;
using DcxManager.Vnc;

namespace DcxManager.Views;

/// <summary>
/// Find hosts on the network and import them (0.5.1 #18): scan a private range for SSH, Remote Desktop and VNC (and
/// Telnet when it is allowed), see what answered and what each machine probably is, change names, folders, users and
/// platforms in the grid, optionally let the AI suggest them (✦ Identify with AI, when AI is on), then tick and import.
/// Addresses already saved are shown but not ticked. Nothing is signed in to; the hosts are saved without passwords (the
/// first connection asks, or pick a credential in each host's editor).
/// </summary>
public static class HostImportWindow
{
    /// <summary>One host entry the grid offers (an address can give several: SSH, Remote Desktop, VNC).</summary>
    public sealed class Row : INotifyPropertyChanged
    {
        private bool _import; private string _name = "", _folder = "", _user = "", _platform = "", _found = "";
        public required HostEntry Candidate { get; init; }
        public required DiscoveredHost From { get; init; }
        /// <summary>What follows the machine's name for this entry (" desktop", " (Remote Desktop)").</summary>
        public string Suffix { get; init; } = "";
        public bool Saved { get; init; }
        public bool Import { get => _import; set => Set(ref _import, value); }
        public string Name { get => _name; set => Set(ref _name, value); }
        public string Folder { get => _folder; set => Set(ref _folder, value); }
        public string User { get => _user; set => Set(ref _user, value); }
        public string Platform { get => _platform; set => Set(ref _platform, value); }
        public string Found { get => _found; set => Set(ref _found, value); }
        public string Address => Candidate.Address;
        public int Port => Candidate.Port;
        public string Type => Candidate.Protocol switch { "rdp" => "Remote Desktop", "vnc" => Candidate.Vnc.Kind == "mac" ? "VNC (Mac)" : "VNC", "telnet" => "Telnet ⚠", _ => "SSH" };
        public string Status => Saved ? "already saved" : "new";
        public event PropertyChangedEventHandler? PropertyChanged;
        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    public const string DefaultFolder = "Discovered";

    /// <summary>The grid rows for what a scan found.</summary>
    public static List<Row> BuildRows(IEnumerable<DiscoveredHost> found, string folder = DefaultFolder)
    {
        var rows = new List<Row>();
        foreach (var d in found)
            foreach (var c in HostDiscovery.Candidates(d, AppSettings.Current.TelnetEnabled, folder))
            {
                bool saved = HostDiscovery.AlreadySaved(c);
                var suffix = c.Name.StartsWith(d.ShortName, StringComparison.Ordinal) ? c.Name[d.ShortName.Length..] : "";
                rows.Add(new Row
                {
                    Candidate = c, From = d, Suffix = suffix, Saved = saved, Import = !saved,
                    Name = c.Name, Folder = c.Group, User = c.Username, Platform = HostEntry.PlatformLabel(c.Platform),
                    Found = string.Join(" · ", d.Ports.Select(p => $"{p.Port} {(p.Detail.Length > 0 ? p.Detail : p.Service)}")) + (d.Evidence.Length > 0 ? $" — {d.Evidence}" : ""),
                });
            }
        return rows;
    }

    /// <summary>Puts the AI's suggestions into the rows (names keep their " desktop" / " (Remote Desktop)" ending).</summary>
    public static int Apply(IEnumerable<Row> rows, IReadOnlyList<HostSuggestion> suggestions)
    {
        int n = 0;
        foreach (var r in rows)
        {
            if (suggestions.FirstOrDefault(s => s.Address.Equals(r.Address, StringComparison.OrdinalIgnoreCase)) is not { } s) continue;
            if (s.Name.Length > 0) r.Name = s.Name + r.Suffix;
            if (s.Folder.Length > 0) r.Folder = s.Folder;
            if (s.Platform.Length > 0 && !(r.Candidate.Protocol == "rdp" && s.Platform != "windows")) r.Platform = HostEntry.PlatformLabel(s.Platform);
            if (s.Role.Length > 0 && !r.Found.Contains("✦ ", StringComparison.Ordinal)) r.Found = $"✦ {s.Role}{(s.Reason.Length > 0 ? " (" + s.Reason + ")" : "")} · " + r.Found;
            n++;
        }
        return n;
    }

    /// <summary>Saves the ticked new rows as hosts (one save, one audit line each); a VNC desktop tunnels through the SSH
    /// host of the same machine (imported now or already saved). Returns the hosts added.</summary>
    public static List<HostEntry> Import(IEnumerable<Row> rows)
    {
        var added = new List<HostEntry>();
        if (!Restrictions.Check("host.add")) return added;
        foreach (var r in rows.Where(r => r.Import && !r.Saved))
        {
            var h = r.Candidate.Clone();
            h.Id = Guid.NewGuid().ToString("N");
            h.Name = r.Name.Trim().Length > 0 ? r.Name.Trim() : r.Candidate.Name;
            h.Group = AppData.NormalizeFolder(r.Folder) is { Length: > 0 } f ? f : DefaultFolder;
            h.Username = r.User.Trim();
            h.Platform = HostEntry.Platforms.FirstOrDefault(p => HostEntry.PlatformLabel(p) == r.Platform) ?? h.Platform;
            if (h.IsVnc && h.Platform == "macos") h.Vnc.Kind = "mac";
            added.Add(h);
        }
        if (added.Count == 0) return added;
        foreach (var v in added.Where(h => h.IsVnc && h.Vnc.Through == "ssh"))
        {
            var twin = added.FirstOrDefault(h => h.IsSsh && h.Address.Equals(v.Address, StringComparison.OrdinalIgnoreCase))
                       ?? AppData.Current.Hosts.FirstOrDefault(h => h.IsSsh && !h.IsAdHoc && h.Address.Equals(v.Address, StringComparison.OrdinalIgnoreCase));
            if (twin != null) v.Vnc.SshHostId = twin.Id; else v.Vnc.Through = "direct";
        }
        foreach (var f in added.Select(h => h.Group).Distinct(StringComparer.OrdinalIgnoreCase))
            if (!AppData.Current.Folders.Contains(f, StringComparer.OrdinalIgnoreCase)) AppData.Current.Folders.Add(f);
        AppData.Current.Hosts.AddRange(added);
        AppData.Save();
        foreach (var h in added) AuditLog.Write("you", h.Display, "added host", $"host import · {h.Protocol} {h.Address}:{h.Port}");
        return added;
    }

    /// <summary>Opens the window (asks nothing until Scan is pressed).</summary>
    public static void Show(Window? owner)
    {
        if (!Restrictions.Check("host.add") || !Restrictions.Check("network.scan")) return;
        var w = Dialogs.MakeWindow("Find hosts on the network", 1100, 680);
        w.Owner = owner;
        var root = new DockPanel { Margin = new Thickness(16) };
        Style S(string key) => (Style)Application.Current.Resources[key];
        TextBlock L(string t) => new() { Text = t, Style = S("Label"), Margin = new Thickness(0, 0, 0, 4) };

        var head = new StackPanel();
        head.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Style = S("Hint"), Margin = new Thickness(0, 0, 0, 10),
            Text = "Scans your own network (private addresses only) for SSH, Remote Desktop and VNC, reads what answers, and offers each machine as hosts to import. " +
                   "Nothing is signed in to. Change names, folders, users and platforms in the grid, tick what you want, then Import. Saved hosts have no password: pick a credential later or type it when connecting.",
        });
        var targets = new TextBox { Text = LanScanner.GuessSubnet(), MinWidth = 240, Style = S("Field") }.Mono();
        var ports = new TextBox { Text = string.Join(", ", HostDiscovery.PortsToScan(AppSettings.Current.TelnetEnabled)), MinWidth = 260, Style = S("Field") }.Mono();
        var scan = new Button { Content = "⌕ Scan", Style = S("PrimaryButton"), Margin = new Thickness(12, 0, 0, 10), VerticalAlignment = VerticalAlignment.Bottom };
        var stop = new Button { Content = "Stop", Margin = new Thickness(6, 0, 0, 10), VerticalAlignment = VerticalAlignment.Bottom, IsEnabled = false };
        var bar = new WrapPanel();
        var t1 = new StackPanel { Margin = new Thickness(0, 0, 12, 0) }; t1.Children.Add(L("ADDRESSES (CIDR, RANGE OR LIST)")); t1.Children.Add(targets);
        var t2 = new StackPanel(); t2.Children.Add(L("PORTS")); t2.Children.Add(ports);
        bar.Children.Add(t1); bar.Children.Add(t2); bar.Children.Add(scan); bar.Children.Add(stop);
        head.Children.Add(bar);
        var progress = new ProgressBar { Height = 4, Maximum = 100, Margin = new Thickness(0, 0, 0, 6) };
        var status = new TextBlock { Style = S("Hint"), Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap };
        head.Children.Add(progress); head.Children.Add(status);
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        var grid = new DataGrid { AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, HeadersVisibility = DataGridHeadersVisibility.Column, SelectionMode = DataGridSelectionMode.Extended };
        grid.Columns.Add(new DataGridCheckBoxColumn { Header = "Import", Binding = new Binding(nameof(Row.Import)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 60 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Name", Binding = new Binding(nameof(Row.Name)), Width = 170 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Address", Binding = new Binding(nameof(Row.Address)), IsReadOnly = true, Width = 120 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Type", Binding = new Binding(nameof(Row.Type)), IsReadOnly = true, Width = 110 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Port", Binding = new Binding(nameof(Row.Port)), IsReadOnly = true, Width = 50 });
        grid.Columns.Add(new DataGridComboBoxColumn { Header = "Platform", SelectedItemBinding = new Binding(nameof(Row.Platform)), ItemsSource = HostEntry.Platforms.Select(HostEntry.PlatformLabel).ToList(), Width = 120 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Folder", Binding = new Binding(nameof(Row.Folder)), Width = 130 });
        grid.Columns.Add(new DataGridTextColumn { Header = "User", Binding = new Binding(nameof(Row.User)), Width = 110 });
        grid.Columns.Add(new DataGridTextColumn { Header = "", Binding = new Binding(nameof(Row.Status)), IsReadOnly = true, Width = 90 });
        grid.Columns.Add(new DataGridTextColumn { Header = "What answered", Binding = new Binding(nameof(Row.Found)), IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });

        var buttons = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(right, Dock.Right);
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        Button B(Panel p, string text, Action click, string? style = null, string? tip = null)
        {
            var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0), ToolTip = tip };
            if (style != null) b.Style = S(style);
            b.Click += (_, _) => click(); p.Children.Add(b); return b;
        }
        var rows = new List<Row>();
        Button? identify = null;
        Button import = null!;
        void Refresh() { grid.ItemsSource = null; grid.ItemsSource = rows; }
        if (HostIdentify.Current is { } ai)
            identify = B(left, "✦ Identify with AI", () => _ = IdentifyAsync(ai), tip: "The AI suggests names, platforms and folders from the names, ports and banners only (no secrets). You still review everything.");
        B(left, "Tick all new", () => { foreach (var r in rows) r.Import = !r.Saved; });
        B(left, "Untick all", () => { foreach (var r in rows) r.Import = false; });
        B(left, "Tick selected", () => { foreach (var r in grid.SelectedItems.OfType<Row>()) r.Import = !r.Saved; });
        import = B(right, "Import ticked", () =>
        {
            grid.CommitEdit(DataGridEditingUnit.Row, true);
            var added = Import(rows);
            if (added.Count == 0) { status.Text = "Nothing new is ticked."; return; }
            MainWindow.Instance?.Status($"Imported {added.Count} host(s) into {string.Join(", ", added.Select(h => h.Group).Distinct())}.");
            w.DialogResult = true;
        }, "PrimaryButton");
        B(right, "Close", () => w.Close());
        buttons.Children.Add(right);
        buttons.Children.Add(left);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(grid);
        w.Content = root;
        void Enable() { if (identify != null) identify.IsEnabled = rows.Count > 0 && HostIdentify.Current is { } id && id.WhyNot() == null; import.IsEnabled = rows.Any(r => !r.Saved); }
        Enable();

        CancellationTokenSource? cts = null;
        scan.Click += async (_, _) =>
        {
            List<int> list;
            try { list = NetTargets.Ports(ports.Text); }
            catch (ArgumentException ex) { status.Text = "✕ " + ex.Message; return; }
            cts = new CancellationTokenSource();
            scan.IsEnabled = false; stop.IsEnabled = true; progress.Value = 0;
            status.Text = $"Scanning {targets.Text.Trim()} on {list.Count} port(s)…";
            try
            {
                var found = await HostDiscovery.ScanAsync(targets.Text, list, new Progress<int>(p => progress.Value = p), cts.Token);
                rows = BuildRows(found);
                Refresh();
                status.Text = found.Count == 0 ? "Nothing answered on those ports." : $"{found.Count} machine(s), {rows.Count} possible host(s), {rows.Count(r => r.Saved)} already saved.";
                AuditLog.Write("you", "hosts", "host import: network scanned", $"{targets.Text.Trim()} · {list.Count} port(s) · {found.Count} answered");
            }
            catch (OperationCanceledException) { status.Text = "Stopped."; }
            catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or InvalidOperationException) { status.Text = "✕ " + ex.Message; }
            finally { scan.IsEnabled = true; stop.IsEnabled = false; progress.Value = 100; Enable(); }
        };
        stop.Click += (_, _) => cts?.Cancel();

        async Task IdentifyAsync(IHostIdentifier ai)
        {
            if (ai.WhyNot() is { } why) { status.Text = "✦ " + why; return; }
            if (identify != null) identify.IsEnabled = false;
            status.Text = "✦ The AI is looking at what answered…";
            try
            {
                var clues = rows.Select(r => r.From).Distinct().Select(HostDiscovery.Clue).ToList();
                var n = Apply(rows, await ai.IdentifyAsync(clues, CancellationToken.None));
                status.Text = $"✦ The AI suggested names for {n} host(s). Check them before importing.";
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Net.Http.HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            { status.Text = "✦ " + ex.Message.Split('\n')[0]; }
            finally { Enable(); }
        }

        w.ShowDialog();
        cts?.Cancel();
    }
}
