using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DcxManager.Incidents;
using DcxManager.Services;
using Microsoft.Win32;

namespace DcxManager.Views;

/// <summary>
/// "Add to incident" from anywhere (terminal selection, screenshots, AI answers, reports, hosts, notes). Captures go to
/// the ACTIVE incident without asking; with none active, the user picks an open one or starts a new one.
/// </summary>
public static class IncidentUi
{
    private const string NewChoice = "＋ Start a new incident…";

    /// <summary>Where a capture goes: the active incident, else the user's pick. Null = cancelled.</summary>
    public static Incident? Target(string what, HostEntry? host = null)
    {
        if (IncidentStore.Active is { } a) return a;
        var open = IncidentStore.All.Where(i => i.IsActive).ToList<object>();
        if (open.Count == 0) return NewIncident(host);
        var pick = Dialogs.Pick<object>("Add to incident", $"Add {what} to which incident? The one you choose becomes the active incident, so later captures go straight to it.",
            new object[] { NewChoice }.Concat(open), o => o is Incident i ? $"{i.Title}   ·   {i.Status} · {IncidentStore.SeverityText(i.Severity)} · {i.Items.Count} entries" : (string)o);
        if (pick is Incident chosen) { IncidentStore.Active = chosen; return chosen; }
        return pick is string ? NewIncident(host) : null;
    }

    /// <summary>Asks for a title and opens a new incident, which becomes the active one.</summary>
    public static Incident? NewIncident(HostEntry? host = null, string? suggested = null)
    {
        var title = Dialogs.Prompt("New incident", "What is happening? (title — you can change it later)", suggested ?? (host is { IsAdHoc: false } ? host.Display + ": " : ""));
        if (string.IsNullOrWhiteSpace(title)) return null;
        // A new incident from a host takes the host's environment (ADR-017).
        var i = IncidentStore.Create(title, host is { IsAdHoc: false } ? new[] { host.Id } : null, environment: host != null ? Envs.For(host) : "");
        IncidentStore.Active = i;
        MainWindow.Instance?.Status($"⚑ Incident “{i.Title}” opened and active — captures from terminals, screenshots and AI answers go to it.");
        return i;
    }

    public static void AddText(ItemKind kind, string title, string text, HostEntry? host = null, string? reportId = null, string? noteId = null)
    {
        if (text.Trim().Length == 0 && kind != ItemKind.Note) return;
        if (Target(IncidentStore.KindText(kind).ToLowerInvariant(), host) is not { } i) return;
        try
        {
            IncidentStore.Add(i, new IncidentItem { Kind = kind, Title = title, Text = text, HostId = host is { IsAdHoc: false } ? host.Id : null, ReportId = reportId, NoteId = noteId });
            MainWindow.Instance?.Status($"⚑ Added to incident “{i.Title}” ({IncidentStore.KindText(kind)}, encrypted)");
        }
        catch (Exception ex) { Dialogs.Error("Incident", "Couldn't add it: " + ex.Message, explain: false); }
    }

    public static void AddScreenshot(BitmapSource img, string subject, HostEntry? host = null)
    {
        if (Target("this screenshot", host) is not { } i) return;
        try
        {
            var png = Screenshot.Png(img);
            IncidentStore.Add(i, new IncidentItem { Kind = ItemKind.Screenshot, Title = $"{subject} ({img.PixelWidth}×{img.PixelHeight})", Mime = "image/png", HostId = host is { IsAdHoc: false } ? host.Id : null }, png, Screenshot.FileName(subject));
            MainWindow.Instance?.Status($"⚑ Screenshot added to incident “{i.Title}” (encrypted inside the store, not a PNG file)");
        }
        catch (Exception ex) { Dialogs.Error("Incident", "Couldn't add the screenshot: " + ex.Message, explain: false); }
    }

    public static void AttachFile(Incident i, string? path = null)
    {
        if (path == null)
        {
            var dlg = new OpenFileDialog { Title = $"Attach a file to “{i.Title}” (max {IncidentStore.MaxAttachment >> 20} MB)" };
            if (dlg.ShowDialog() != true) return;
            path = dlg.FileName;
        }
        try
        {
            var info = new FileInfo(path);
            if (info.Length > IncidentStore.MaxAttachment) { Dialogs.Error("Attach file", $"{info.Name} is {info.Length / 1048576.0:N1} MB; attachments are limited to {IncidentStore.MaxAttachment >> 20} MB.", explain: false); return; }
            IncidentStore.Add(i, new IncidentItem { Kind = ItemKind.File, Title = info.Name, Mime = MimeFor(info.Extension) }, File.ReadAllBytes(path), info.Name);
            AuditLog.Write("you", "incidents", "file attached", $"{i.Title}: {info.Name}");
        }
        catch (Exception ex) { Dialogs.Error("Attach file", ex.Message, explain: false); }
    }

    public static string MimeFor(string ext) => ext.ToLowerInvariant() switch
    {
        ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".bmp" => "image/bmp",
        ".txt" or ".log" or ".conf" or ".cfg" or ".ini" or ".yaml" or ".yml" or ".json" or ".xml" or ".md" or ".sh" or ".ps1" or ".csv" => "text/plain",
        ".pdf" => "application/pdf", ".pcap" or ".pcapng" => "application/vnd.tcpdump.pcap", _ => "application/octet-stream",
    };

    /// <summary>Menu items for a host: start an incident for it / add it to the active one / open its incidents.</summary>
    public static IEnumerable<object?> HostMenu(HostEntry h)
    {
        if (h.IsAdHoc) yield break;
        var mine = IncidentStore.ForHost(h.Id);
        yield return Menus.Item("⚑ New incident for this host…", () => { if (NewIncident(h) is { } i) MainWindow.Instance.OpenIncidents(i); });
        if (IncidentStore.Active is { } a && !a.HostIds.Contains(h.Id))
            yield return Menus.Item($"⚑ Add to active incident “{Short(a.Title)}”", () => { a.HostIds.Add(h.Id); IncidentStore.Add(a, new IncidentItem { Kind = ItemKind.Event, Title = $"{h.Display} linked", HostId = h.Id }); });
        if (mine.Count > 0) yield return Menus.Item($"⚑ Incidents for this host ({mine.Count})", () => MainWindow.Instance.OpenIncidents(mine[0]));
    }

    public static string Short(string s) => s.Length > 36 ? s[..36] + "…" : s;
}

/// <summary>
/// Incidents / work sessions (ADR-010): open incidents first, a timeline of everything gathered (terminal excerpts,
/// logs, screenshots, files, notes, diagnostics, AI analyses, connections, status changes), status/severity/environment,
/// linked hosts, a summary, ✦ AI summary of the selected entries (shown in "Data being sent to AI" first) and a Markdown
/// export. Everything is encrypted in the store; nothing is sent anywhere unless asked.
/// </summary>
public sealed class IncidentsView : UserControl, IDisposable
{
    public static IncidentsView? Current { get; private set; }

    private readonly ListBox _list = new();
    private readonly ComboBox _filter = new() { Margin = new Thickness(0, 6, 0, 6) };
    private readonly TextBlock _count = new() { Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _title = new TextBox() { FontWeight = FontWeights.SemiBold, Padding = new Thickness(6, 4, 6, 4) }.Fs(FontStep.H1);
    private readonly ComboBox _status = new() { Width = 130 }, _severity = new() { Width = 140 }, _env = new() { Width = 130 };
    private readonly ToggleButton _active = new() { Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Bottom };
    private readonly WrapPanel _hosts = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _summary = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 64, MaxHeight = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(6, 4, 6, 4) };
    private readonly ListBox _timeline = new() { SelectionMode = SelectionMode.Extended };
    private readonly TextBlock _state = new() { Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly Grid _detail = new();
    private readonly TextBlock _empty = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(20), VerticalAlignment = VerticalAlignment.Top };
    private readonly DispatcherTimer _autosave;
    private Incident? _inc;
    private bool _loading;

    public IncidentsView()
    {
        Current = this;
        _summary.Editor();
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(_timeline, ScrollBarVisibility.Disabled);
        var root = new Grid { Margin = new Thickness(12, 10, 12, 10) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300), MinWidth = 200 });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // ---- left: incidents ----------------------------------------------------------------------------------------
        var left = new DockPanel();
        var head = new StackPanel();
        head.Children.Add(new TextBlock { Text = "Incidents", Style = (Style)FindResource("H1") });
        head.Children.Add(new TextBlock { Text = "Work sessions and incidents: hosts, a timeline of what you found and did, and a summary. The ACTIVE one receives “Add to incident” from terminals, screenshots and AI answers.", Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) }.Fs(FontStep.Small));
        _filter.ItemsSource = new[] { "Open & monitoring", "All", "Resolved & closed", "Trash" };
        _filter.SelectedIndex = 0;
        head.Children.Add(_filter);
        var newRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        newRow.Children.Add(Btn("＋ New incident", () => { if (IncidentUi.NewIncident() is { } i) { Refresh(); Open(i); } }, primary: true));
        newRow.Children.Add(Btn("Empty trash", EmptyTrash, danger: true));
        head.Children.Add(newRow);
        DockPanel.SetDock(head, Dock.Top);
        left.Children.Add(head);
        _count.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        DockPanel.SetDock(_count, Dock.Bottom);
        left.Children.Add(_count);
        left.Children.Add(_list);
        root.Children.Add(left);
        var split = new GridSplitter { Width = 6, HorizontalAlignment = HorizontalAlignment.Stretch, Background = System.Windows.Media.Brushes.Transparent, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        Grid.SetColumn(split, 1);
        root.Children.Add(split);

        // ---- right: the incident ------------------------------------------------------------------------------------
        _status.ItemsSource = Enum.GetValues<IncidentStatus>();
        _severity.ItemsSource = Enum.GetValues<IncidentSeverity>().Select(IncidentStore.SeverityText).ToList();
        _env.ItemsSource = new[] { "(not stated)" }.Concat(Envs.All).ToArray();   // the app-wide vocabulary (ADR-017)
        var props = new WrapPanel { Margin = new Thickness(0, 6, 0, 2) };
        props.Children.Add(Labeled("STATUS", _status));
        props.Children.Add(Labeled("SEVERITY", _severity));
        props.Children.Add(Labeled("ENVIRONMENT", _env));
        props.Children.Add(_active);
        var hostRow = new WrapPanel { Margin = new Thickness(0, 4, 0, 2) };
        hostRow.Children.Add(Lbl("HOSTS"));
        hostRow.Children.Add(_hosts);
        hostRow.Children.Add(Btn("＋ Link host", LinkHost));
        var tools = new WrapPanel { Margin = new Thickness(0, 6, 0, 2) };
        tools.Children.Add(Btn("＋ Event…", () => AddPrompt(ItemKind.Event, "What happened? (first line = title)")));
        tools.Children.Add(Btn("＋ Log / output…", () => AddPrompt(ItemKind.Log, "Paste the log lines or command output (first line = title)")));
        tools.Children.Add(Btn("＋ Attach file…", () => { if (_inc != null) { IncidentUi.AttachFile(_inc); ShowTimeline(); } }));
        tools.Children.Add(Btn("＋ Link note…", LinkNote));
        tools.Children.Add(Btn("＋ Report…", LinkReport));
        var aiRow = new WrapPanel { Margin = new Thickness(0, 2, 0, 4) };
        if (Assist.Current != null)   // only with an assistant module (the AI)
        {
            aiRow.Children.Add(Btn("✦ Summarise with AI", () => _ = SummariseAsync(), primary: true));
            aiRow.Children.Add(Btn("✦ Root-cause hypotheses", () => _ = SummariseAsync("From this incident material, list the most likely root causes in order, each with the supporting evidence (quote timestamps) and what would confirm or rule it out. Mark anything that is a guess.")));
        }
        aiRow.Children.Add(Btn("⤓ Export Markdown…", () => { if (_inc != null) Menus.SaveText(IncidentStore.Markdown(_inc), Safe(_inc.Title) + ".md", "Markdown|*.md|Text|*.txt"); }));
        aiRow.Children.Add(Btn("Share to Teams…", () => { if (_inc != null) _ = TeamsUi.ShareAsync(Window.GetWindow(this), "Incident: " + _inc.Title, IncidentStore.Markdown(_inc), null); }));
        aiRow.Children.Add(Btn("⧉ Copy Markdown", () => { if (_inc != null) Menus.Clip(IncidentStore.Markdown(_inc)); }));
        aiRow.Children.Add(Btn("📡 Broadcast status card…", () => { if (_inc is { Deleted: null } i) BroadcastUi.Start(new Api.IncidentSource(i), "incident"); }));
        aiRow.Children.Add(Btn("Move to Trash", TrashOrRestore, danger: true));

        var top = new StackPanel();
        top.Children.Add(_title);
        top.Children.Add(props);
        top.Children.Add(hostRow);
        top.Children.Add(Lbl("SUMMARY — impact, current state, what's next"));
        top.Children.Add(_summary);
        top.Children.Add(tools);
        top.Children.Add(aiRow);
        top.Children.Add(Lbl("TIMELINE — select entries to summarise or export only those (Ctrl/Shift+click)"));
        var right = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        right.Children.Add(top);
        _state.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        DockPanel.SetDock(_state, Dock.Bottom);
        right.Children.Add(_state);
        right.Children.Add(_timeline);
        _detail.Children.Add(right);
        _empty.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _detail.Children.Add(_empty);
        Grid.SetColumn(_detail, 2);
        root.Children.Add(_detail);
        Content = root;

        // ---- behaviour ----------------------------------------------------------------------------------------------
        _autosave = new DispatcherTimer(TimeSpan.FromMilliseconds(1200), DispatcherPriority.Background, (_, _) => { _autosave!.Stop(); SaveText(); }, Dispatcher) { IsEnabled = false };
        void Dirty() { if (!_loading && _inc != null) { _autosave.Stop(); _autosave.Start(); } }
        _title.TextChanged += (_, _) => Dirty();
        _summary.TextChanged += (_, _) => Dirty();
        _title.LostFocus += (_, _) => SaveText();
        _summary.LostFocus += (_, _) => SaveText();
        _status.SelectionChanged += (_, _) => { if (!_loading && _inc != null && _status.SelectedItem is IncidentStatus s) { IncidentStore.SetStatus(_inc, s); ShowTimeline(); ShowActive(); Refresh(); } };
        _severity.SelectionChanged += (_, _) => { if (!_loading && _inc != null && _severity.SelectedIndex >= 0) { var sev = (IncidentSeverity)_severity.SelectedIndex; if (sev != _inc.Severity) { _inc.Items.Add(new IncidentItem { Kind = ItemKind.Status, Title = $"Severity {IncidentStore.SeverityText(_inc.Severity)} → {IncidentStore.SeverityText(sev)}" }); _inc.Severity = sev; IncidentStore.Save(_inc); ShowTimeline(); Refresh(); } } };
        _env.SelectionChanged += (_, _) => { if (!_loading && _inc != null) { _inc.Environment = _env.SelectedIndex <= 0 ? "" : (string)_env.SelectedItem; IncidentStore.Save(_inc); Refresh(); } };
        _active.Click += (_, _) => { if (_inc == null) return; IncidentStore.Active = _active.IsChecked == true ? _inc : null; ShowActive(); Refresh(); };
        _filter.SelectionChanged += (_, _) => Refresh();
        _list.SelectionChanged += (_, _) => { if (_list.SelectedItem is FrameworkElement { Tag: Incident i } && i != _inc) Open(i); };
        _timeline.MouseDoubleClick += (_, _) => { if (SelectedItems().FirstOrDefault() is { } it) OpenItem(it); };
        _timeline.KeyDown += (_, e) => { if (e.Key == Key.Enter && SelectedItems().FirstOrDefault() is { } it) { OpenItem(it); e.Handled = true; } };
        Menus.ForList(_timeline, this, () => SelectedItems().FirstOrDefault() is not { } it ? Array.Empty<object?>() : new object?[]
        {
            Menus.Item("Open", () => OpenItem(it)),
            Menus.Item("Copy text", () => Menus.Clip(it.Text), enabled: it.Text.Length > 0),
            it.AttachmentId != null ? Menus.Item("Save attachment as…", () => SaveAttachment(it)) : null,
            it.HostId != null && AppData.Current.FindHost(it.HostId) is { } h ? Menus.Item("⌨ Terminal on " + h.Display, () => MainWindow.Instance.OpenTerminal(h)) : null,
            Assist.Current != null ? Menus.AsyncItem($"✦ Summarise the {SelectedItems().Count} selected with AI", () => SummariseAsync()) : null,
            Menus.Sep(),
            Menus.Item(SelectedItems().Count > 1 ? $"Remove {SelectedItems().Count} entries…" : "Remove from incident…", RemoveSelected),
        });
        Menus.ForList(_list, this, () => _list.SelectedItem is not FrameworkElement { Tag: Incident i } ? Array.Empty<object?>() : new object?[]
        {
            Menus.Item(IncidentStore.Active?.Id == i.Id ? "Stop being the active incident" : "⚑ Make active", () => { IncidentStore.Active = IncidentStore.Active?.Id == i.Id ? null : i; Refresh(); ShowActive(); }, enabled: i.IsActive && i.Deleted == null),
            Menus.Item("Copy as Markdown", () => Menus.Clip(IncidentStore.Markdown(i))),
            i.Deleted == null ? Menus.Item("Move to Trash", () => { IncidentStore.Delete(i); Refresh(); }) : Menus.Item("Restore", () => { IncidentStore.Restore(i); Refresh(); }),
        });
        IncidentStore.Changed += OnChanged;
        AppData.Changed += OnHosts;
        Loaded += (_, _) => Refresh();
        Unloaded += (_, _) => SaveText();
        ShowDetail(false);
        Refresh();
    }

    private void OnChanged(Incident? i) => Dispatcher.BeginInvoke(() => { Refresh(); if (i != null && i == _inc) { ShowTimeline(); ShowActive(); } });
    private void OnHosts() => Dispatcher.BeginInvoke(() => { ShowHosts(); ShowTimeline(); });

    // ---- list ---------------------------------------------------------------------------------------------------------

    public void Refresh()
    {
        IEnumerable<Incident> rows = _filter.SelectedIndex switch
        {
            1 => IncidentStore.All,
            2 => IncidentStore.All.Where(i => !i.IsActive),
            3 => IncidentStore.Trash,
            _ => IncidentStore.All.Where(i => i.IsActive),
        };
        var list = rows.OrderByDescending(i => IncidentStore.Active?.Id == i.Id).ThenBy(i => i.IsActive ? 0 : 1).ThenBy(i => i.Severity).ThenByDescending(i => i.Updated).ToList();
        _list.ItemsSource = list.Select(Row).ToList();
        _list.SelectedItem = _list.Items.OfType<FrameworkElement>().FirstOrDefault(e => e.Tag == _inc);
        int open = IncidentStore.All.Count(i => i.IsActive);
        _count.Text = $"{list.Count} shown · {open} open · {IncidentStore.All.Count} total" + (IncidentStore.Active is { } a ? $"\n⚑ Active: {a.Title}" : "\nNo active incident: captures will ask where to go.")
            + (IncidentStore.Damaged.Count > 0 ? $"\n⚠ {IncidentStore.Damaged.Count} incident file(s) could not be read and were left untouched." : "");
        if (_inc == null) ShowDetail(false);
    }

    private FrameworkElement Row(Incident i)
    {
        var p = new StackPanel { Tag = i, Margin = new Thickness(2, 3, 2, 5) };
        bool active = IncidentStore.Active?.Id == i.Id;
        var t = new TextBlock { Text = (active ? "⚑ " : "") + i.Title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        if (i.Severity is IncidentSeverity.Sev1 && i.IsActive) t.SetResourceReference(TextBlock.ForegroundProperty, "Bad");
        p.Children.Add(t);
        var hosts = string.Join(", ", i.HostIds.Select(AppData.Current.FindHost).Where(h => h != null).Select(h => h!.Display).Take(3));
        var meta = new TextBlock
        {
            Text = $"{i.Status} · {IncidentStore.SeverityText(i.Severity)}" + (i.Environment.Length > 0 ? " · " + i.Environment : "") + $" · {i.Items.Count} entries · {i.Updated:dd MMM HH:mm}" + (hosts.Length > 0 ? " · " + hosts : ""), TextTrimming = TextTrimming.CharacterEllipsis,
        }.Fs(FontStep.Small);
        meta.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        p.Children.Add(meta);
        return p;
    }

    // ---- detail -------------------------------------------------------------------------------------------------------

    public void Open(Incident i)
    {
        SaveText();
        _loading = true;
        _inc = i;
        _title.Text = i.Title; _summary.Text = i.Summary;
        _status.SelectedItem = i.Status;
        _severity.SelectedIndex = (int)i.Severity;
        _env.SelectedItem = i.Environment.Length > 0 ? i.Environment : "(not stated)";
        _title.IsReadOnly = _summary.IsReadOnly = i.Deleted != null;
        _loading = false;
        ShowHosts(); ShowTimeline(); ShowActive();
        ShowDetail(true);
        _list.SelectedItem = _list.Items.OfType<FrameworkElement>().FirstOrDefault(e => e.Tag == i);
    }

    private void ShowDetail(bool on)
    {
        _detail.Children[0].Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        _empty.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        _empty.Text = IncidentStore.All.Count == 0
            ? "No incidents yet. ＋ New incident (or right-click a host → New incident for this host).\n\nWhile one is active, a terminal selection" + (Assist.Current != null ? ", a 📷 screenshot or an AI answer" : " or a 📷 screenshot") + " goes to its timeline with one click; connecting to one of its hosts is recorded too. Everything is encrypted in the store."
            : "Select an incident.";
    }

    private void ShowActive()
    {
        if (_inc == null) return;
        bool isActive = IncidentStore.Active?.Id == _inc.Id;
        _active.IsChecked = isActive;
        _active.Content = isActive ? "⚑ Active — captures go here" : "Make active";
        _active.IsEnabled = _inc.IsActive && _inc.Deleted == null;
    }

    private void ShowHosts()
    {
        _hosts.Children.Clear();
        if (_inc == null) return;
        foreach (var id in _inc.HostIds.ToList())
        {
            var h = AppData.Current.FindHost(id);
            var chip = Btn(h?.Display ?? "(deleted host)", () => { });
            chip.ToolTip = h?.Endpoint ?? "This host no longer exists";
            Menus.AttachDropDown(chip, () => new object?[]
            {
                h != null ? Menus.Item("⌨ Terminal", () => MainWindow.Instance.OpenTerminal(h)) : null,
                h != null ? Menus.Item("▣ Manage", () => MainWindow.Instance.OpenServer(h)) : null,
                h != null ? Menus.Item("✎ Notes", () => MainWindow.Instance.OpenNotes(h.Id)) : null,
                Menus.Item("Unlink from this incident", () => { _inc.HostIds.Remove(id); IncidentStore.Save(_inc); ShowHosts(); }),
            });
            _hosts.Children.Add(chip);
        }
        if (_inc.HostIds.Count == 0) { var none = new TextBlock { Text = "none", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) }; none.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); _hosts.Children.Add(none); }
    }

    private void ShowTimeline()
    {
        if (_inc is not { } i) return;
        var keep = SelectedItems().Select(x => x.Id).ToHashSet();
        var rows = i.Items.OrderByDescending(x => x.At).Select(TimelineRow).ToList();
        _timeline.ItemsSource = rows;
        foreach (var r in rows.Where(r => r.Tag is IncidentItem it && keep.Contains(it.Id))) _timeline.SelectedItems.Add(r);
        var span = (i.Resolved ?? DateTime.Now) - i.Created;
        _state.Text = $"{i.Items.Count} entries · opened {i.Created:yyyy-MM-dd HH:mm} · {(i.Resolved != null ? "took" : "open for")} {IncidentStore.Duration(span)} · " +
                      $"{i.Items.Count(x => x.AttachmentId != null)} attachment(s), {i.Items.Where(x => x.AttachmentId != null).Sum(x => x.Bytes) / 1048576.0:N1} MB · encrypted" +
                      (i.Deleted != null ? " · IN TRASH (Restore to edit)" : "");
    }

    private FrameworkElement TimelineRow(IncidentItem it)
    {
        var p = new StackPanel { Tag = it, Margin = new Thickness(2, 3, 2, 5) };
        var host = it.HostId != null && AppData.Current.FindHost(it.HostId) is { } h ? " · " + h.Display : "";
        p.Children.Add(new TextBlock { Text = $"{IncidentStore.Glyph(it.Kind)}  {it.At:dd MMM HH:mm:ss}   {IncidentStore.KindText(it.Kind)}{host} — {it.Title}", FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        string preview = it.AttachmentId != null ? $"{it.Mime} · {it.Bytes / 1024.0:N0} KB · double-click to open" :
            it.Kind == ItemKind.Note && Notes.NoteStore.Find(it.NoteId) is { } n ? $"note “{n.Title}” — {FirstLines(n.Body)}" : FirstLines(it.Text);
        if (preview.Length > 0)
        {
            var s = new TextBlock { Text = preview, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap }.Fs(FontStep.Small).Mono();
            s.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            p.Children.Add(s);
        }
        return p;
    }

    private static string FirstLines(string s) => string.Join("  ⏎ ", s.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && l != "```").Take(2));

    private List<IncidentItem> SelectedItems() => _timeline.SelectedItems.OfType<FrameworkElement>().Select(e => e.Tag).OfType<IncidentItem>().ToList();

    /// <summary>Saves a pending title/summary edit now (crash recovery, ADR-022); nothing when unchanged.</summary>
    public void FlushPending() => SaveText();

    private void SaveText()
    {
        _autosave.Stop();
        if (_inc is not { Deleted: null } i) return;
        var title = _title.Text.Trim().Length > 0 ? _title.Text.Trim() : "Untitled incident";
        if (title == i.Title && _summary.Text == i.Summary) return;
        i.Title = title; i.Summary = _summary.Text;
        try { IncidentStore.Save(i); }
        catch (Exception ex) { _state.Text = "✕ NOT SAVED: " + ex.Message; App.Log("Incident save", ex); }
    }

    // ---- actions ------------------------------------------------------------------------------------------------------

    private void AddPrompt(ItemKind kind, string message)
    {
        if (_inc is not { Deleted: null } i) return;
        var text = Dialogs.Prompt(IncidentStore.KindText(kind), message, "", multiline: true);
        if (string.IsNullOrWhiteSpace(text)) return;
        var lines = text.Replace("\r", "").Split('\n');
        var title = lines[0].Trim().Length > 0 ? lines[0].Trim() : IncidentStore.KindText(kind);
        if (title.Length > 120) title = title[..120] + "…";
        try { IncidentStore.Add(i, new IncidentItem { Kind = kind, Title = title, Text = lines.Length > 1 ? string.Join("\n", lines.Skip(1)).Trim('\n') : "" }); ShowTimeline(); }
        catch (Exception ex) { Dialogs.Error("Incident", ex.Message, explain: false); }
    }

    private void LinkHost()
    {
        if (_inc is not { Deleted: null } i || AppData.Current.Hosts.Count == 0) return;
        var h = Dialogs.Pick("Host", "Link which host to this incident?", AppData.Current.Hosts.Where(x => !i.HostIds.Contains(x.Id)).OrderBy(x => x.Display, StringComparer.OrdinalIgnoreCase), x => $"{x.Display}   {x.Endpoint}");
        if (h == null) return;
        i.HostIds.Add(h.Id);
        IncidentStore.Add(i, new IncidentItem { Kind = ItemKind.Event, Title = $"{h.Display} linked", HostId = h.Id });
        ShowHosts(); ShowTimeline();
    }

    private void LinkNote()
    {
        if (_inc is not { Deleted: null } i) return;
        var notes = Notes.NoteStore.All.OrderByDescending(n => n.HostIds.Any(i.HostIds.Contains)).ThenByDescending(n => n.Updated).ToList();
        if (notes.Count == 0) { Dialogs.Info("Link note", "There are no notes yet."); return; }
        var n = Dialogs.Pick("Link note", "Which note? (notes about this incident's hosts first; the note stays a note — the incident shows its current text)", notes, x => $"{x.Title}   ·   {x.Updated:dd MMM HH:mm}");
        if (n == null) return;
        IncidentStore.Add(i, new IncidentItem { Kind = ItemKind.Note, Title = n.Title, NoteId = n.Id });
        ShowTimeline();
    }

    private void LinkReport()
    {
        if (_inc is not { Deleted: null } i) return;
        var reports = Reports.ReportEngine.Load();
        if (reports.Count == 0) { Dialogs.Info("Add report", "There are no saved reports yet (DOCUMENTS → Reports)."); return; }
        var r = Dialogs.Pick("Add report", "Which diagnostics report? Its Markdown is copied into the timeline.", reports, x => $"{x.Title}   ·   {x.Health}");
        if (r == null) return;
        IncidentStore.Add(i, new IncidentItem { Kind = ItemKind.Report, Title = r.Title, Text = Reports.ReportEngine.Markdown(r), ReportId = r.Id });
        ShowTimeline();
    }

    private void OpenItem(IncidentItem it)
    {
        if (_inc is not { } i) return;
        try
        {
            if (it.Kind == ItemKind.Note && it.NoteId != null)
            {
                if (Notes.NoteStore.Find(it.NoteId) is { } n) MainWindow.Instance.OpenNotes(note: n); else Dialogs.Info("Note", "That note no longer exists.");
                return;
            }
            if (it.AttachmentId != null)
            {
                var bytes = IncidentStore.ReadAttachment(i, it);
                if (it.Mime?.StartsWith("image/") == true) { ShowImage(it.Title, bytes); return; }
                if (it.Mime == "text/plain" && bytes.Length < 4_000_000) { Dialogs.ShowText(it.Title, System.Text.Encoding.UTF8.GetString(bytes)); return; }
                SaveAttachment(it);
                return;
            }
            Dialogs.ShowText($"{IncidentStore.KindText(it.Kind)} — {it.Title}", $"{it.At:yyyy-MM-dd HH:mm:ss}\n\n{it.Text}");
        }
        catch (Exception ex) { Dialogs.Error("Incident", ex.Message, explain: false); }
    }

    private static void ShowImage(string title, byte[] png)
    {
        var img = new BitmapImage();
        using (var ms = new MemoryStream(png)) { img.BeginInit(); img.CacheOption = BitmapCacheOption.OnLoad; img.StreamSource = ms; img.EndInit(); }
        img.Freeze();
        var w = Dialogs.MakeWindow(title, Math.Min(1200, img.PixelWidth + 40), Math.Min(860, img.PixelHeight + 60));
        w.Content = new ScrollViewer { Content = new System.Windows.Controls.Image { Source = img, Stretch = System.Windows.Media.Stretch.None }, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        w.Show();
    }

    private void SaveAttachment(IncidentItem it)
    {
        if (_inc is not { } i) return;
        var dlg = new SaveFileDialog { FileName = Safe(it.Title), Filter = "All files|*.*" };
        if (dlg.ShowDialog() != true) return;
        if (!StorageGuard.ConfirmDestination(Path.GetDirectoryName(dlg.FileName)!, "Save attachment", encrypted: false)) return;
        File.WriteAllBytes(dlg.FileName, IncidentStore.ReadAttachment(i, it));
        AuditLog.Write("you", "incidents", "attachment saved out (not encrypted)", $"{i.Title}: {it.Title} → {dlg.FileName}");
        MainWindow.Instance.Status($"Saved (plain file, not encrypted) → {dlg.FileName}");
    }

    private void RemoveSelected()
    {
        if (_inc is not { Deleted: null } i) return;
        var sel = SelectedItems();
        if (sel.Count == 0) return;
        if (!Dialogs.Confirm("Remove from incident", $"Remove {sel.Count} entr{(sel.Count == 1 ? "y" : "ies")} from “{i.Title}”?" + (sel.Any(x => x.AttachmentId != null) ? " Attached screenshots/files are deleted with them." : "") +
            " Linked notes and reports themselves are not touched. (Settings → Snapshots can bring this back.)", "Remove", danger: true)) return;
        foreach (var it in sel) IncidentStore.RemoveItem(i, it);
        ShowTimeline();
    }

    private void TrashOrRestore()
    {
        if (_inc is not { } i) return;
        if (i.Deleted == null) { SaveText(); IncidentStore.Delete(i); } else IncidentStore.Restore(i);
        Open(i);
        Refresh();
    }

    private void EmptyTrash()
    {
        var c = IncidentStore.Trash.Count;
        if (c == 0) { Dialogs.Info("Trash", "The Trash is empty."); return; }
        if (!Dialogs.Confirm("Empty trash", $"Permanently delete {c} incident(s) in the Trash, with their screenshots and files? Linked notes are kept. This can't be undone here (a snapshot or encrypted backup still has them).", "Delete permanently", danger: true)) return;
        if (_inc?.Deleted != null) { _inc = null; ShowDetail(false); }
        IncidentStore.EmptyTrash();
        Refresh();
    }

    /// <summary>The incident (or the selected entries) through "Data being sent to AI", then into the AI sidebar.
    /// Screenshots and files are named, never included.</summary>
    private async Task SummariseAsync(string? task = null)
    {
        if (_inc is not { } i || Assist.Current is not { } assist) return;
        SaveText();
        var sel = SelectedItems();
        var items = sel.Count > 0 ? sel : i.Items;
        var text = SummaryMaterial(i, items);
        var attachments = items.Count(x => x.AttachmentId != null);
        var choice = await assist.PreviewAsync($"incident “{i.Title}”",
            (sel.Count > 0 ? $"{sel.Count} selected timeline entries" : $"the whole timeline ({i.Items.Count} entries)") + " + summary" + (attachments > 0 ? $"; {attachments} screenshot(s)/file(s) named only, not included" : ""), text);
        if (choice == null) return;
        await assist.AskAsync($"incident “{i.Title}” ({(sel.Count > 0 ? sel.Count + " entries" : "full timeline")})", choice.Text,
            task ?? "Write an incident summary from this material: 1) what happened (short timeline with timestamps), 2) impact, 3) likely cause(s) with the evidence, " +
                    "4) what was done, 5) current state, 6) open questions and next steps. Use only the material; mark guesses clearly.");
        AuditLog.Write("you", "incidents", "incident sent to AI", $"{i.Title} · {items.Count} entries · {choice.ScopeLabel}");
    }

    internal static string SummaryMaterial(Incident i, IEnumerable<IncidentItem> items) => IncidentStore.Markdown(i, items);

    // ---- helpers ------------------------------------------------------------------------------------------------------

    private Button Btn(string text, Action a, bool primary = false, bool danger = false)
    {
        var b = new Button { Content = text, Style = (Style)FindResource(primary ? "PrimaryButton" : danger ? "DangerButton" : "SmallButton"), Margin = new Thickness(0, 0, 4, 4) };
        if (primary || danger) { b.Padding = new Thickness(8, 3, 8, 3); b.MinWidth = 0; }
        b.Click += (_, _) => a();
        return b;
    }

    private TextBlock Lbl(string t) => new() { Text = t, Style = (Style)FindResource("Label"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 6, 4) };

    private StackPanel Labeled(string label, FrameworkElement e) { var p = new StackPanel { Margin = new Thickness(0, 0, 8, 0) }; p.Children.Add(Lbl(label)); p.Children.Add(e); return p; }

    private static string Safe(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c)).Trim();

    // test seams
    internal Incident? OpenIncident => _inc;
    internal int TimelineCount => _timeline.Items.Count;

    public void Dispose()
    {
        SaveText();
        IncidentStore.Changed -= OnChanged;
        AppData.Changed -= OnHosts;
        _autosave.Stop();
        if (Current == this) Current = null;
    }
}
