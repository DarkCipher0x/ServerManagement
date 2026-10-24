using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using DcxManager.Services;
using DcxManager.Ssh;
using DcxManager.Views;

namespace DcxManager;

// MainWindow: the header's quick connect (0.3.0): a protocol menu, the target box with a dropdown of pinned, recent and
// saved hosts (pins toggle in place), and a Connect button. Entries and parsing live in QuickTargets.
public partial class MainWindow
{
    private string _quickProto = "ssh";
    private Popup? _quickPopup;
    private ListBox? _quickList;

    private void FocusQuickConnect() { QuickConnect.Focus(); QuickConnect.SelectAll(); }

    /// <summary>The chosen protocol (self-test reads it).</summary>
    internal string QuickProto => _quickProto;

    internal void SetQuickProto(string id)
    {
        var p = QuickTargets.Find(id, AppSettings.Current.TelnetEnabled);
        _quickProto = p.Id;
        QuickMode.Content = p.Id + " ▾";
        QuickHint.Text = p.Hint;
    }

    private void OnQuickMode(object sender, RoutedEventArgs e) =>
        Menus.ShowAt(QuickMode, QuickTargets.All(AppSettings.Current.TelnetEnabled).Select(p =>
            (object?)Menus.Item((p.Id == _quickProto ? "● " : "   ") + p.Label, () => { SetQuickProto(p.Id); QuickConnect.Focus(); }, tip: p.Hint)));

    private void OnQuickText(object sender, TextChangedEventArgs e)
    {
        QuickHint.Visibility = QuickConnect.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_quickPopup?.IsOpen == true) FillQuickList();
    }

    private void OnQuickConnectKey(object sender, KeyEventArgs e)
    {
        bool open = _quickPopup?.IsOpen == true;
        switch (e.Key)
        {
            case Key.Down when !open: ShowQuickList(); e.Handled = true; break;
            case Key.Down or Key.Up when open && _quickList != null:
                MoveQuickSelection(e.Key == Key.Down ? 1 : -1); e.Handled = true; break;
            case Key.Escape when open: _quickPopup!.IsOpen = false; e.Handled = true; break;
            case Key.Enter:
                e.Handled = true;
                if (open && _quickList?.SelectedItem is ListBoxItem { Tag: QuickTargets.Entry pick }) { _quickPopup!.IsOpen = false; ConnectEntry(pick.Text); }
                else ConnectTyped();
                break;
        }
    }

    private void OnQuickGo(object sender, RoutedEventArgs e) => ConnectTyped();

    private void ConnectTyped()
    {
        var text = QuickConnect.Text.Trim();
        if (text.Length == 0 && _quickProto != "local") { ShowQuickList(); return; }
        if (QuickConnectTo(_quickProto, text)) QuickConnect.Clear();
    }

    /// <summary>A pinned / recent / saved line: its own protocol, then connect.</summary>
    private void ConnectEntry(string line)
    {
        var (proto, target) = QuickTargets.Split(line);
        SetQuickProto(proto);
        if (QuickConnectTo(proto, target)) QuickConnect.Clear();
        else QuickConnect.Text = target;
    }

    /// <summary>Opens <paramref name="target"/> with <paramref name="proto"/>; remembers it in Recent. False (with the
    /// reason shown) when the target doesn't fit the protocol.</summary>
    public bool QuickConnectTo(string proto, string target)
    {
        target = target.Trim();
        // A line typed with its own prefix ("rdp://…", "telnet …", "https://…") means that protocol whatever is selected.
        if (proto == "ssh" && QuickTargets.Split(target) is var (p2, t2) && p2 != "ssh") { proto = p2; target = t2; }
        var line = QuickTargets.Canonical(proto, target);
        switch (proto)
        {
            case "telnet":
                // Legacy devices, only while Telnet is allowed (ADR-019).
                if (ParseTelnet("telnet " + target) is not { } tn) return Bad("Telnet: host, \"host 2323\" or host:2323.");
                if (!AppSettings.Current.TelnetEnabled) { Dialogs.Error("Telnet", new Sessions.TelnetDisabledException().Message, explain: false); return false; }
                OpenTerminal(tn); break;
            case "local":
                if (Sessions.LocalShells.FromQuick(target.Length == 0 ? "local" : target) is not { } local) return Bad("Local shell: local, pwsh, powershell, cmd, wsl or \"wsl <distribution>\".");
                OpenLocal(local); break;
            case "serial":
                // Bare "COM3" reuses a saved profile on that port; explicit settings ("COM3 115200") open ad-hoc as typed.
                if (Sessions.SerialPorts.FromQuick(target) is not { } quick) return Bad("Serial: COM3, \"COM3 115200\" or \"COM3 9600 8N1\".");
                var saved = target.Contains(' ') || target.Contains(':') ? null : AppData.Current.SerialProfiles.FirstOrDefault(s => s.Port.Equals(quick.Port, StringComparison.OrdinalIgnoreCase));
                OpenSession(new Sessions.SerialTarget(saved ?? quick)); break;
            case "web":
                if (!Uri.TryCreate(line, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || uri.Host.Length == 0) return Bad("Web UI: https://host[:port][/path].");
                WebWindow.Open(uri.Host, uri.ToString(), "quick:" + uri.Authority); break;
            case "rdp":
                if (ParseQuick(target, rdp: true) is not { } rh) return Bad("Remote Desktop: user@host, host or host:3390.");
                OpenRdp(rh.Protocol == "rdp" ? rh : CopyAs(rh, "rdp")); break;
            case "sftp" or "external" or "ssh":
                if (ParseQuick(target) is not { } h) return Bad("Use user@host, user@host:port, host or the name of a saved host.");
                if (proto == "sftp") OpenFiles(h);
                else if (proto == "external") ExternalSsh.Launch(h);
                else OpenTerminal(h);
                break;
            default: return Bad("Unknown protocol.");
        }
        QuickTargets.Remember(line);
        return true;

        static bool Bad(string why) { Dialogs.Error("Quick connect", why); return false; }
        static HostEntry CopyAs(HostEntry h, string protocol) => new() { Name = h.Display, Address = h.Address, Port = h.Port == 22 ? 3389 : h.Port, Username = h.Username, Protocol = protocol, IsAdHoc = true, Auth = AuthMethod.Password };
    }

    // ---- the dropdown ----------------------------------------------------------------------------------------------

    private void OnQuickHistory(object sender, RoutedEventArgs e)
    {
        if (_quickPopup?.IsOpen == true) { _quickPopup.IsOpen = false; return; }
        ShowQuickList();
        QuickConnect.Focus();
    }

    private void ShowQuickList()
    {
        EnsureQuickPopup();
        FillQuickList();
        if (TestMode.Running) return;   // the self-test builds the list but never opens a popup
        _quickPopup!.IsOpen = true;
    }

    private void EnsureQuickPopup()
    {
        if (_quickPopup != null) return;
        _quickList = new ListBox { MaxHeight = 420, BorderThickness = new Thickness(0), Focusable = false };
        ScrollViewer.SetHorizontalScrollBarVisibility(_quickList, ScrollBarVisibility.Disabled);
        _quickList.PreviewMouseLeftButtonUp += (_, e) =>
        {
            // A click on a row connects; a click on its 📌 toggles the pin (the button handles that itself).
            if (e.OriginalSource is DependencyObject d && FindParent<Button>(d) == null && FindParent<ListBoxItem>(d) is { Tag: QuickTargets.Entry en })
            { _quickPopup!.IsOpen = false; ConnectEntry(en.Text); }
        };
        var border = new Border { Child = _quickList, BorderThickness = new Thickness(1), Padding = new Thickness(2), Width = QuickBox.Width };
        border.SetResourceReference(Border.BackgroundProperty, "Surface");
        border.SetResourceReference(Border.BorderBrushProperty, "Line");
        _quickPopup = new Popup { Child = border, PlacementTarget = QuickBox, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true };
    }

    /// <summary>Rebuilds the rows from what is typed (self-test: <see cref="QuickRowsForTest"/>).</summary>
    private void FillQuickList()
    {
        EnsureQuickPopup();
        var list = _quickList!;
        list.Items.Clear();
        string? group = null;
        foreach (var en in QuickTargets.Dropdown(QuickConnect.Text))
        {
            if (en.Group != group)
            {
                group = en.Group;
                var head = new TextBlock { Text = (group == "Pinned" ? "📌 " : group == "Recent" ? "🕘 " : "▣ ") + group.ToUpperInvariant(), Style = (Style)FindResource("Label"), Margin = new Thickness(4, 6, 0, 2) };
                list.Items.Add(new ListBoxItem { Content = head, IsEnabled = false, Focusable = false });
            }
            var row = new DockPanel();
            var pin = new Button { Content = en.Pinned ? "📌" : "📍", ToolTip = en.Pinned ? "Unpin" : "Pin to the top", MinWidth = 0, Width = 26, Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent, Opacity = en.Pinned ? 1 : 0.45 };
            pin.Click += (_, e) => { QuickTargets.TogglePin(en.Text); FillQuickList(); e.Handled = true; };
            DockPanel.SetDock(pin, Dock.Right);
            row.Children.Add(pin);
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = en.Text, TextTrimming = TextTrimming.CharacterEllipsis }.Mono());
            if (en.Detail != null) text.Children.Add(new TextBlock { Text = en.Detail, Style = (Style)FindResource("Hint"), TextTrimming = TextTrimming.CharacterEllipsis });
            row.Children.Add(text);
            list.Items.Add(new ListBoxItem { Content = row, Tag = en, ToolTip = en.Detail });
        }
        if (list.Items.Count == 0) list.Items.Add(new ListBoxItem { Content = new TextBlock { Text = "Nothing yet: connections you make appear here; 📍 pins one to the top.", Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap }, IsEnabled = false });
        list.SelectedItem = list.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag is QuickTargets.Entry);
    }

    private void MoveQuickSelection(int step)
    {
        var rows = _quickList!.Items.OfType<ListBoxItem>().Where(i => i.Tag is QuickTargets.Entry).ToList();
        if (rows.Count == 0) return;
        var i = _quickList.SelectedItem is ListBoxItem cur ? rows.IndexOf(cur) : -1;
        var next = rows[Math.Clamp(i + step, 0, rows.Count - 1)];
        _quickList.SelectedItem = next;
        next.BringIntoView();
    }

    /// <summary>Self-test: the dropdown rows (group headers included) for <paramref name="typed"/>, built without opening it.</summary>
    internal List<string> QuickRowsForTest(string typed)
    {
        QuickConnect.Text = typed;
        FillQuickList();
        var rows = _quickList!.Items.OfType<ListBoxItem>().Select(i => i.Tag is QuickTargets.Entry en ? (en.Pinned ? "📌 " : "") + en.Text : "# " + ((i.Content as TextBlock)?.Text ?? "")).ToList();
        QuickConnect.Clear();
        return rows;
    }

    private static T? FindParent<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T) d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }
}
