using System.IO.Ports;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using DcxManager.Services;
using DcxManager.Sessions;

namespace DcxManager.Views;

/// <summary>
/// Serial / USB-serial consoles: saved profiles (encrypted with the hosts) and the ports Windows reports right now,
/// refreshed every 2 s while this screen is visible (no background polling otherwise). Connecting opens a terminal
/// tab through <see cref="SerialTransport"/>.
/// </summary>
public sealed class SerialView : UserControl, IDisposable
{
    private readonly ListView _profiles = new() { MinHeight = 140 };
    private readonly ListView _ports = new() { MinHeight = 100 };
    private readonly TextBlock _portsHint = new() { Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly DispatcherTimer _poll;
    private string _lastPorts = "";

    /// <summary>A profile row with whether its port (or USB adapter) is plugged in now.</summary>
    public sealed record Row(SerialProfile Profile, string Name, string Settings, string Where, string State);

    public SerialView()
    {
        var root = new StackPanel { Margin = new Thickness(14, 12, 14, 16) };
        root.Children.Add(new TextBlock { Text = "Serial / USB consoles", Style = (Style)FindResource("H1") });
        root.Children.Add(new TextBlock
        {
            Text = "Console cables and USB-serial adapters (switches, routers, firewalls, UPSes, embedded boards). Profiles remember the port, speed and framing; " +
                   "a USB adapter is followed by its identity if Windows gives it a new COM number. Unplugging it pauses the tab and it reconnects when it comes back.",
            Style = (Style)FindResource("Hint"), Margin = new Thickness(0, 4, 0, 10), TextWrapping = TextWrapping.Wrap,
        });

        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        void B(string t, Action a, string? style = null) { var b = new Button { Content = t, Margin = new Thickness(0, 0, 6, 4) }; if (style != null) b.Style = (Style)FindResource(style); b.Click += (_, _) => a(); bar.Children.Add(b); }
        B("▶ Connect", Connect, "PrimaryButton");
        B("＋ New profile", () => Edit(null, SelPort));
        B("✎ Edit", () => { if (SelProfile is { } p) Edit(p, null); });
        B("⧉ Duplicate", () => { if (SelProfile is { } p) { var c = p.Copy(); c.Id = Guid.NewGuid().ToString("N"); c.Name += " (copy)"; AppData.Current.SerialProfiles.Add(c); AppData.Save(); Reload(); } });
        B("✕ Delete", Delete, "DangerButton");
        B("↻ Refresh ports", () => { _lastPorts = ""; Reload(); });
        root.Children.Add(bar);

        root.Children.Add(new TextBlock { Text = "SAVED CONSOLES", Style = (Style)FindResource("Label"), Margin = new Thickness(0, 6, 0, 4) });
        var gv = new GridView();
        void Col(GridView g, string h, string p, double w) => g.Columns.Add(new GridViewColumn { Header = h, Width = w, DisplayMemberBinding = new Binding(p) });
        Col(gv, "Name", "Name", 200); Col(gv, "Now", "State", 150); Col(gv, "Settings", "Settings", 220); Col(gv, "Port / adapter", "Where", 330);
        _profiles.View = gv;
        _profiles.MouseDoubleClick += (_, _) => Connect();
        Menus.ForList(_profiles, this, () => SelProfile is not { } p ? Array.Empty<object?>() : new object?[]
        {
            Menus.Item("▶ Connect", Connect),
            Menus.Item("✎ Edit…", () => Edit(p, null)),
            Menus.Item("Copy settings", () => Menus.Clip($"{p.Name}: {p.Summary}, enter={p.NewLine}, encoding={p.Encoding}")),
            Menus.AssistItem("✦ Explain these serial settings with AI", a => a.AskAsync("serial console " + p.Name,
                $"Serial console profile: port {p.Port}, {p.Baud} baud, {p.Framing}, flow control {p.Handshake}, Enter sends {p.NewLine.ToUpperInvariant()}, encoding {p.Encoding}, local echo {(p.LocalEcho ? "on" : "off")}, DTR {(p.DtrEnable ? "on" : "off")}, RTS {(p.RtsEnable ? "on" : "off")}.",
                "Explain these serial console settings, what kind of device they suit, and what to change if the console shows garbage, nothing at all, or double characters.")),
        });
        root.Children.Add(_profiles);

        root.Children.Add(new TextBlock { Text = "PORTS DETECTED NOW", Style = (Style)FindResource("Label"), Margin = new Thickness(0, 14, 0, 4) });
        var pv = new GridView();
        Col(pv, "Port", "Port", 90); Col(pv, "Device", "Description", 380); Col(pv, "USB identity", "UsbId", 300);
        _ports.View = pv;
        _ports.MouseDoubleClick += (_, _) => { if (SelPort is { } sp) OpenAdHoc(sp); };
        Menus.ForList(_ports, this, () => SelPort is not { } sp ? Array.Empty<object?>() : new object?[]
        {
            Menus.Item("▶ Open at 9600 8N1 (typical console)", () => OpenAdHoc(sp, 9600)),
            Menus.Item("▶ Open at 115200 8N1", () => OpenAdHoc(sp, 115200)),
            Menus.Item("＋ Save as a profile…", () => Edit(null, sp)),
            Menus.Item("Copy port name", () => Menus.Clip(sp.Port)),
        });
        root.Children.Add(_ports);
        _portsHint.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        root.Children.Add(_portsHint);

        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _poll = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => { if (IsVisible) Reload(); }, Dispatcher) { IsEnabled = false };
        Loaded += (_, _) => { Reload(); _poll.Start(); };
        Unloaded += (_, _) => _poll.Stop();
        AppData.Changed += OnData;
    }

    private void OnData() => Dispatcher.BeginInvoke(() => { _lastPorts = ""; Reload(); });

    private SerialProfile? SelProfile => (_profiles.SelectedItem as Row)?.Profile;
    private SerialPortInfo? SelPort => _ports.SelectedItem as SerialPortInfo;

    /// <summary>Rebuilds the lists; skipped when nothing changed (keeps the selection steady while polling).</summary>
    public void Reload()
    {
        var present = SerialPorts.List();
        var key = string.Join("|", present.Select(p => p.Port + p.UsbId)) + "#" + AppData.Current.SerialProfiles.Count + string.Join(",", AppData.Current.SerialProfiles.Select(p => p.Name + p.Summary));
        if (key == _lastPorts) return;
        _lastPorts = key;
        var selP = SelProfile; var selPort = SelPort?.Port;
        var rows = AppData.Current.SerialProfiles.Select(p =>
        {
            var now = SerialPorts.Resolve(p, present);
            var where = p.UsbId.Length > 0 ? $"{p.Port} · USB {p.UsbId}" : p.Port;
            var state = now == null ? "○ not connected" : now.Equals(p.Port, StringComparison.OrdinalIgnoreCase) ? "● present" : $"● present as {now}";
            return new Row(p, p.Name, $"{p.Baud} {p.Framing}" + (p.Handshake != Handshake.None ? " · " + p.Handshake : "") + $" · Enter={p.NewLine.ToUpperInvariant()}", where, state);
        }).ToList();
        _profiles.ItemsSource = rows;
        _profiles.SelectedItem = rows.FirstOrDefault(r => r.Profile == selP);
        _ports.ItemsSource = present;
        _ports.SelectedItem = present.FirstOrDefault(p => p.Port == selPort);
        _portsHint.Text = present.Count == 0
            ? "No serial ports right now. Plug in the console cable / USB adapter (its driver must be installed — FTDI, Prolific, CP210x and CH340 are common); this list updates by itself."
            : $"{present.Count} port(s). Double-click one to open it at 9600 8N1, or right-click for 115200 / save as a profile.";
    }

    private void Connect()
    {
        if (SelProfile is { } p) { MainWindow.Instance.OpenSession(new SerialTarget(p)); return; }
        if (SelPort is { } sp) { OpenAdHoc(sp); return; }
        Dialogs.Info("Serial", "Select a saved console or a detected port first.");
    }

    private static void OpenAdHoc(SerialPortInfo sp, int baud = 9600) =>
        MainWindow.Instance.OpenSession(new SerialTarget(new SerialProfile { Name = sp.Port, Port = sp.Port, UsbId = sp.UsbId, Baud = baud }));

    private void Delete()
    {
        if (SelProfile is not { } p || !Dialogs.Confirm("Delete console", $"Delete the saved console “{p.Name}”? (Open tabs stay open.)", "Delete", danger: true)) return;
        AppData.Current.SerialProfiles.Remove(p);
        AppData.Save();
        _lastPorts = ""; Reload();
    }

    /// <summary>The profile editor. Values are validated (<see cref="SerialPorts.Validate"/>) before saving.</summary>
    private void Edit(SerialProfile? existing, SerialPortInfo? fromPort)
    {
        var p = existing?.Copy() ?? new SerialProfile { Port = fromPort?.Port ?? SerialPorts.List().FirstOrDefault()?.Port ?? "COM1", UsbId = fromPort?.UsbId ?? "", Name = fromPort != null ? $"Console on {fromPort.Port}" : "Serial console" };
        var w = Dialogs.MakeWindow(existing == null ? "New serial console" : "Edit serial console", 520);
        var panel = new StackPanel { Margin = new Thickness(18) };
        TextBlock L(string t) => new() { Text = t, Style = (Style)FindResource("Label"), Margin = new Thickness(0, 8, 0, 4) };
        ComboBox C<T>(IEnumerable<T> items, T sel, double width = 200) => new() { ItemsSource = items.ToList(), SelectedItem = sel, Width = width, HorizontalAlignment = HorizontalAlignment.Left };

        var name = new TextBox { Text = p.Name, Padding = new Thickness(5, 4, 5, 4) };
        var present = SerialPorts.List();
        var port = new ComboBox { IsEditable = true, Text = p.Port, Width = 200, HorizontalAlignment = HorizontalAlignment.Left, ItemsSource = present.Select(x => x.Port).ToList() };
        var follow = new CheckBox { Content = "Follow this USB adapter if Windows gives it a different COM number", IsChecked = p.UsbId.Length > 0, Margin = new Thickness(0, 6, 0, 0) };
        var usbInfo = new TextBlock { Style = (Style)FindResource("Hint") };
        string usbId = p.UsbId;
        void PortChanged()
        {
            var hit = present.FirstOrDefault(x => x.Port.Equals(port.Text.Trim(), StringComparison.OrdinalIgnoreCase));
            if (hit != null && hit.UsbId.Length > 0) usbId = hit.UsbId;
            follow.IsEnabled = usbId.Length > 0;
            usbInfo.Text = usbId.Length > 0 ? $"USB identity: {usbId}" + (hit != null ? $" — {hit.Description}" : "") : "Not a USB adapter (or not plugged in): the COM name is used as-is.";
        }
        port.SelectionChanged += (_, _) => Dispatcher.BeginInvoke(PortChanged);
        port.LostFocus += (_, _) => PortChanged();
        PortChanged();
        var baud = new ComboBox { IsEditable = true, Text = p.Baud.ToString(), Width = 140, HorizontalAlignment = HorizontalAlignment.Left, ItemsSource = SerialPorts.CommonBauds.Select(b => b.ToString()).ToList() };
        var data = C(new[] { 8, 7, 6, 5 }, p.DataBits, 80);
        var parity = C(Enum.GetValues<Parity>(), p.Parity, 120);
        var stop = C(new[] { StopBits.One, StopBits.OnePointFive, StopBits.Two }, p.StopBits, 140);
        var flow = new ComboBox { Width = 220, HorizontalAlignment = HorizontalAlignment.Left, ItemsSource = new[] { "None", "XON/XOFF (software)", "RTS/CTS (hardware)", "RTS/CTS + XON/XOFF" } };
        flow.SelectedIndex = p.Handshake switch { Handshake.XOnXOff => 1, Handshake.RequestToSend => 2, Handshake.RequestToSendXOnXOff => 3, _ => 0 };
        var nl = new ComboBox { Width = 220, HorizontalAlignment = HorizontalAlignment.Left, ItemsSource = new[] { "CR (most network gear)", "LF", "CR+LF" } };
        nl.SelectedIndex = p.NewLine switch { "lf" => 1, "crlf" => 2, _ => 0 };
        var enc = new ComboBox { Width = 220, HorizontalAlignment = HorizontalAlignment.Left, ItemsSource = new[] { "UTF-8", "Latin-1 (ISO-8859-1)", "ASCII" } };
        enc.SelectedIndex = p.Encoding switch { "latin1" => 1, "ascii" => 2, _ => 0 };
        var echo = new CheckBox { Content = "Local echo (only for devices that don't echo what you type)", IsChecked = p.LocalEcho, Margin = new Thickness(0, 6, 0, 0) };
        var dtr = new CheckBox { Content = "Raise DTR", IsChecked = p.DtrEnable, Margin = new Thickness(0, 6, 14, 0) };
        var rts = new CheckBox { Content = "Raise RTS", IsChecked = p.RtsEnable, Margin = new Thickness(0, 6, 0, 0) };
        var reconnect = new CheckBox { Content = "Reconnect automatically when the cable / adapter comes back", IsChecked = p.Reconnect, Margin = new Thickness(0, 6, 0, 0) };
        var notes = new TextBox { Text = p.Notes, AcceptsReturn = true, Height = 60, TextWrapping = TextWrapping.Wrap, Padding = new Thickness(5, 4, 5, 4) }.Editor();

        var frame = new WrapPanel();
        foreach (var (l, c) in new (string, FrameworkElement)[] { ("BAUD", baud), ("DATA BITS", data), ("PARITY", parity), ("STOP BITS", stop) })
        { var sp = new StackPanel { Margin = new Thickness(0, 0, 12, 0) }; sp.Children.Add(L(l)); sp.Children.Add(c); frame.Children.Add(sp); }
        var lines = new WrapPanel();
        foreach (var (l, c) in new (string, FrameworkElement)[] { ("FLOW CONTROL", flow), ("ENTER SENDS", nl), ("ENCODING", enc) })
        { var sp = new StackPanel { Margin = new Thickness(0, 0, 12, 0) }; sp.Children.Add(L(l)); sp.Children.Add(c); lines.Children.Add(sp); }
        var sig = new WrapPanel(); sig.Children.Add(dtr); sig.Children.Add(rts);
        foreach (var e in new UIElement[] { L("NAME"), name, L("PORT"), port, follow, usbInfo, frame, lines, echo, sig, reconnect, L("NOTES"), notes }) panel.Children.Add(e);
        var ok = new Button { Content = "Save", Style = (Style)FindResource("PrimaryButton"), IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var bs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        bs.Children.Add(ok); bs.Children.Add(cancel); panel.Children.Add(bs);
        w.Content = panel;
        ok.Click += (_, _) =>
        {
            p.Name = name.Text.Trim().Length > 0 ? name.Text.Trim() : port.Text.Trim().ToUpperInvariant();
            p.Port = port.Text.Trim().ToUpperInvariant();
            p.UsbId = follow.IsChecked == true ? usbId : "";
            p.Baud = int.TryParse(baud.Text.Trim(), out var b) ? b : 0;
            p.DataBits = (int)data.SelectedItem; p.Parity = (Parity)parity.SelectedItem; p.StopBits = (StopBits)stop.SelectedItem;
            p.Handshake = flow.SelectedIndex switch { 1 => Handshake.XOnXOff, 2 => Handshake.RequestToSend, 3 => Handshake.RequestToSendXOnXOff, _ => Handshake.None };
            p.NewLine = nl.SelectedIndex switch { 1 => "lf", 2 => "crlf", _ => "cr" };
            p.Encoding = enc.SelectedIndex switch { 1 => "latin1", 2 => "ascii", _ => "utf-8" };
            p.LocalEcho = echo.IsChecked == true; p.DtrEnable = dtr.IsChecked == true; p.RtsEnable = rts.IsChecked == true; p.Reconnect = reconnect.IsChecked == true;
            p.Notes = notes.Text;
            if (SerialPorts.Validate(p) is { } err) { Dialogs.Error("Serial console", err); return; }
            w.DialogResult = true;
        };
        if (w.ShowDialog() != true) return;
        if (existing == null) AppData.Current.SerialProfiles.Add(p);
        else { var i = AppData.Current.SerialProfiles.IndexOf(existing); if (i >= 0) AppData.Current.SerialProfiles[i] = p; }
        AppData.Save();
        AuditLog.Write("you", "serial:" + p.Name, existing == null ? "serial console added" : "serial console edited", p.Summary);
        _lastPorts = ""; Reload();
    }

    public void Dispose() { _poll.Stop(); AppData.Changed -= OnData; }
}
