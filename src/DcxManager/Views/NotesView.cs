using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using DcxManager.Notes;
using DcxManager.Services;

namespace DcxManager.Views;

/// <summary>
/// Notes &amp; knowledge base (ADR-005): offline search across every note (words, "phrases", tag:, host:, dates, is:fav),
/// per-host notebooks, and a plain-text/Markdown editor with timestamp and code-block helpers. Autosaves 1.2 s after
/// typing stops and whenever you leave a note; every save is an encrypted, atomic write of that note's own file.
/// AI actions only send the note (or selection) when clicked.
/// </summary>
public sealed class NotesView : UserControl, IDisposable
{
    public static NotesView? Current { get; private set; }

    private readonly TextBox _search = Hint.Set(new TextBox { Padding = new Thickness(6, 5, 6, 5) }, "Search notes…");
    private readonly ComboBox _scope = new() { Margin = new Thickness(0, 6, 0, 6) };
    private readonly ListBox _list = new();
    private readonly TextBlock _count = new() { Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _title = new TextBox() { FontWeight = FontWeights.SemiBold, Padding = new Thickness(6, 4, 6, 4) }.Fs(FontStep.H1);
    private readonly TextBox _body = new TextBox()
    {
        AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(8, 6, 8, 6),
    }.Fs(FontStep.Base);
    private readonly TextBox _tags = new() { Width = 260, Padding = new Thickness(5, 3, 5, 3), ToolTip = "Comma-separated tags (search with tag:name)" };
    private readonly ToggleButton _fav = new() { Content = "☆ Favourite", Margin = new Thickness(6, 0, 0, 0) };
    private readonly WrapPanel _hosts = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { Margin = new Thickness(0, 4, 0, 0) };
    private readonly Grid _editor = new();
    private readonly TextBlock _empty = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(20), VerticalAlignment = VerticalAlignment.Top };
    private readonly DispatcherTimer _autosave, _searchDelay;
    private readonly WrapPanel _toolbar = new();
    private Note? _note;
    private string _baseTitle = "", _baseBody = "";
    private bool _loading;
    private string? _hostScope;

    public NotesView()
    {
        Current = this;
        _body.Editor();
        var root = new Grid { Margin = new Thickness(12, 10, 12, 10) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300), MinWidth = 200 });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // ---- left: search + results -----------------------------------------------------------------------------
        var left = new DockPanel();
        var head = new StackPanel();
        head.Children.Add(new TextBlock { Text = "Notes", Style = (Style)FindResource("H1") });
        var searchHint = new TextBlock { Text = "Search: words, \"phrase\", tag:x, host:x, is:fav, today, last tuesday, after:2026-10-01", Style = (Style)FindResource("Hint"), Margin = new Thickness(0, 4, 0, 4), TextWrapping = TextWrapping.Wrap }.Fs(FontStep.Small);
        head.Children.Add(searchHint);
        head.Children.Add(_search);
        head.Children.Add(_scope);
        var newRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        newRow.Children.Add(SmallBtn("＋ New note", () => NewNote(null), primary: true));
        newRow.Children.Add(SmallBtn("＋ For a host…", () => { if (PickHost("New note for which host?") is { } h) NewNote(h); }));
        newRow.Children.Add(SmallBtn("Empty trash", EmptyTrash, danger: true));
        head.Children.Add(newRow);
        DockPanel.SetDock(head, Dock.Top);
        left.Children.Add(head);
        _count.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        DockPanel.SetDock(_count, Dock.Bottom);
        left.Children.Add(_count);
        left.Children.Add(_list);
        Grid.SetColumn(left, 0);
        root.Children.Add(left);
        var split = new GridSplitter { Width = 6, HorizontalAlignment = HorizontalAlignment.Stretch, Background = System.Windows.Media.Brushes.Transparent, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        Grid.SetColumn(split, 1);
        root.Children.Add(split);

        // ---- right: editor ---------------------------------------------------------------------------------------
        var meta = new WrapPanel { Margin = new Thickness(0, 6, 0, 4) };
        meta.Children.Add(Lbl("HOSTS"));
        meta.Children.Add(_hosts);
        meta.Children.Add(SmallBtn("＋ Link host", () => { if (_note != null && PickHost("Link this note to which host?") is { } h && !_note.HostIds.Contains(h.Id)) { _note.HostIds.Add(h.Id); SaveNow(); ShowHosts(); } }));
        meta.Children.Add(Lbl("TAGS", 14));
        meta.Children.Add(_tags);
        meta.Children.Add(_fav);
        Button T(string text, string tip, Action a) { var b = SmallBtn(text, a); b.ToolTip = tip; _toolbar.Children.Add(b); return b; }
        T("Time", "Insert [HH:mm:ss] at the cursor", () => Insert($"[{DateTime.Now:HH:mm:ss}] "));
        T("Date+time", "Insert [yyyy-MM-dd HH:mm:ss]", () => Insert($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] "));
        T("ISO", "Insert an ISO 8601 timestamp with offset", () => Insert(DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz") + " "));
        T("Host", "Insert the linked host (name and address)", () => { var h = _note?.HostIds.Select(AppData.Current.FindHost).FirstOrDefault(x => x != null); Insert(h != null ? $"{h.Display} ({h.Endpoint})" : ""); });
        T("Code block", "Wrap the selection (or an empty block) in ``` fences", () => Wrap("```\n", "\n```"));
        T("Paste as code", "Paste the clipboard as a fenced code block", () => { try { if (Clipboard.ContainsText()) Insert("```\n" + Clipboard.GetText().TrimEnd() + "\n```\n"); } catch { } });
        T("Task", "Insert a Markdown task line", () => Insert("- [ ] "));
        T("Heading", "Insert a Markdown heading", () => Insert("## "));
        T("History…", "Earlier versions of this note", ShowHistory);
        var aiRow = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
        void A(string text, string task) { if (Assist.Current != null) aiRow.Children.Add(SmallBtn(text, () => _ = AskAi(task))); }   // only with an assistant module (the AI)
        A("✦ Summarise", "Summarise this engineering note: what happened, current state, and anything unresolved.");
        A("✦ Extract actions", "Extract every action item, follow-up and open question from this note as a checklist (- [ ] …), with who/what/where when stated.");
        A("✦ Organise", "Reorganise this note into clear Markdown sections (Context, Timeline, Findings, Actions, References) without inventing facts. Keep timestamps and commands exactly as written.");
        A("✦ Ask about selection", "Explain the selected part of this note (commands, errors, config) and what it implies.");
        var delete = SmallBtn("Move to Trash", () => { if (_note == null) return; if (_note.Deleted == null) { SaveNow(); NoteStore.Delete(_note); Status("Moved to Trash — restore it from the Trash view."); } else { NoteStore.Restore(_note); Status("Restored."); } Refresh(); }, danger: true);
        aiRow.Children.Add(delete);

        var top = new StackPanel();
        top.Children.Add(_title);
        top.Children.Add(meta);
        top.Children.Add(_toolbar);
        top.Children.Add(aiRow);
        var right = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        right.Children.Add(top);
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        DockPanel.SetDock(_status, Dock.Bottom);
        right.Children.Add(_status);
        _body.Margin = new Thickness(0, 6, 0, 0);
        right.Children.Add(_body);
        _editor.Children.Add(right);
        _empty.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _editor.Children.Add(_empty);
        Grid.SetColumn(_editor, 2);
        root.Children.Add(_editor);
        Content = root;

        // ---- behaviour -------------------------------------------------------------------------------------------
        _autosave = new DispatcherTimer(TimeSpan.FromMilliseconds(1200), DispatcherPriority.Background, (_, _) => { _autosave!.Stop(); SaveNow(); }, Dispatcher) { IsEnabled = false };
        _searchDelay = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => { _searchDelay!.Stop(); Refresh(); }, Dispatcher) { IsEnabled = false };
        void Dirty() { if (!_loading && _note != null) { _autosave.Stop(); _autosave.Start(); Status("Editing…"); } }
        _title.TextChanged += (_, _) => Dirty();
        _body.TextChanged += (_, _) => Dirty();
        _tags.TextChanged += (_, _) => Dirty();
        _fav.Click += (_, _) => { if (_note == null) return; _note.Favorite = _fav.IsChecked == true; _fav.Content = _note.Favorite ? "★ Favourite" : "☆ Favourite"; SaveNow(); Refresh(); };
        _title.LostFocus += (_, _) => SaveNow();
        _body.LostFocus += (_, _) => SaveNow();
        _search.TextChanged += (_, _) => { _searchDelay.Stop(); _searchDelay.Start(); };
        _search.KeyDown += (_, e) => { if (e.Key == Key.Down && _list.Items.Count > 0) { _list.SelectedIndex = 0; (_list.ItemContainerGenerator.ContainerFromIndex(0) as UIElement)?.Focus(); } };
        _scope.SelectionChanged += (_, _) => { if (!_loading) Refresh(); };
        _list.SelectionChanged += (_, _) => { if (_list.SelectedItem is FrameworkElement { Tag: Note n } && n != _note) Open(n); };
        _body.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.T && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)) { Insert($"[{DateTime.Now:HH:mm:ss}] "); e.Handled = true; }
            else if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) { SaveNow(snapshot: true); e.Handled = true; }
        };
        Menus.ForList(_list, this, () => _list.SelectedItem is not FrameworkElement { Tag: Note n } ? Array.Empty<object?>() : Closed(n) ? new object?[]
        {
            Menus.Item("🔓 Unlock…", () => Open(n)),
            LockUi.Menu("note:" + n.Id, n.Title),
        } : new object?[]
        {
            Menus.Item(n.Favorite ? "☆ Remove favourite" : "★ Favourite", () => { n.Favorite = !n.Favorite; NoteStore.Save(n); Refresh(); }),
            Menus.Item("Copy as text", () => Menus.Clip($"# {n.Title}\n\n{n.Body}")),
            Menus.Item("Save as Markdown…", () => Menus.SaveText($"# {n.Title}\n\n{n.Body}", SafeName(n.Title) + ".md", "Markdown|*.md|Text|*.txt|All files|*.*")),
            Menus.Item("⚑ Link to incident", () => IncidentUi.AddText(Incidents.ItemKind.Note, n.Title, "", n.HostIds.Select(AppData.Current.FindHost).FirstOrDefault(h => h != null), noteId: n.Id),
                tip: "The incident's timeline shows this note's current text"),
            LockUi.Menu("note:" + n.Id, n.Title),
            Menus.Sep(),
            n.Deleted == null ? Menus.Item("Move to Trash", () => { NoteStore.Delete(n); Refresh(); }) : Menus.Item("Restore", () => { NoteStore.Restore(n); Refresh(); }),
        });
        NoteStore.Changed += OnStoreChanged;
        Locks.Changed += OnLocksChanged;
        AppData.Changed += OnStoreHosts;
        Loaded += (_, _) => { FillScopes(); Refresh(); };
        Unloaded += (_, _) => SaveNow();
        ShowEditor(false);
    }

    private static bool Closed(Note n) => Locks.IsLocked("note:" + n.Id) && !Locks.IsOpen("note:" + n.Id);

    /// <summary>A padlock closed (timer, app lock, Windows lock): the open note leaves the screen.</summary>
    private void OnLocksChanged() => Dispatcher.BeginInvoke(() =>
    {
        if (_note is { } n && Closed(n)) { SaveNow(); _note = null; _title.Text = _body.Text = ""; ShowEditor(false); }
        Refresh();
    });

    private void OnStoreChanged(Note? n) { if (n == null || n != _note) Dispatcher.BeginInvoke(Refresh); }
    private void OnStoreHosts() => Dispatcher.BeginInvoke(() => { NoteStore.Reindex(); FillScopes(); ShowHosts(); });

    // ---- scopes / list ----------------------------------------------------------------------------------------------

    private sealed record Scope(string Label, string Kind, string? HostId = null) { public override string ToString() => Label; }

    private void FillScopes()
    {
        _loading = true;
        var keep = (_scope.SelectedItem as Scope)?.Kind + (_scope.SelectedItem as Scope)?.HostId;
        var scopes = new List<Scope> { new("All notes", "all"), new("★ Favourites", "fav"), new($"Trash ({NoteStore.Trash.Count})", "trash") };
        var withNotes = NoteStore.All.SelectMany(n => n.HostIds).Distinct().Select(AppData.Current.FindHost).Where(h => h != null).OrderBy(h => h!.Display, StringComparer.OrdinalIgnoreCase);
        scopes.AddRange(withNotes.Select(h => new Scope("Host: " + h!.Display, "host", h.Id)));
        if (_hostScope != null && scopes.All(s => s.HostId != _hostScope) && AppData.Current.FindHost(_hostScope) is { } hs) scopes.Add(new Scope("Host: " + hs.Display, "host", hs.Id));
        _scope.ItemsSource = scopes;
        _scope.SelectedItem = _hostScope != null ? scopes.First(s => s.HostId == _hostScope) : scopes.FirstOrDefault(s => s.Kind + s.HostId == keep) ?? scopes[0];
        _loading = false;
    }

    public void Refresh()
    {
        var scope = _scope.SelectedItem as Scope ?? new Scope("All", "all");
        _hostScope = scope.Kind == "host" ? scope.HostId : null;
        IEnumerable<(Note Note, string Snippet)> rows;
        if (scope.Kind == "trash")
            rows = NoteStore.Trash.Select(n => (n, "deleted " + n.Deleted?.ToString("yyyy-MM-dd HH:mm")));
        else
        {
            var hits = NoteStore.Search(_search.Text);
            // 0.5.1 #10: a closed padlock keeps a note's text out of search (a match would tell what it says).
            bool searching = _search.Text.Trim().Length > 0;
            rows = hits.Where(h => scope.Kind switch { "fav" => h.Note.Favorite, "host" => h.Note.HostIds.Contains(scope.HostId!), _ => true })
                .Where(h => !searching || !Closed(h.Note))
                .Select(h => (h.Note, Closed(h.Note) ? "🔒 locked" : h.Snippet));
        }
        var list = rows.ToList();
        _list.ItemsSource = list.Select(r => Row(r.Note, r.Snippet)).ToList();
        _list.SelectedItem = _list.Items.OfType<FrameworkElement>().FirstOrDefault(e => e.Tag == _note);
        var total = NoteStore.All.Count;
        var parsed = NoteIndex.Parse(_search.Text);
        _count.Text = (scope.Kind == "trash" ? $"{list.Count} note(s) in the Trash" : $"{list.Count} of {total} note(s)")
                      + (parsed.From != null || parsed.To != null ? $" · dates {parsed.From:yyyy-MM-dd} → {parsed.To?.AddDays(-1):yyyy-MM-dd}" : "")
                      + (scope.Kind != "trash" && NoteStore.LastIgnored.Count > 0 ? $" · ignored (in no note): {string.Join(", ", NoteStore.LastIgnored)}" : "")
                      + (NoteStore.Damaged.Count > 0 ? $"\n⚠ {NoteStore.Damaged.Count} note file(s) could not be read and were left untouched: {string.Join(", ", NoteStore.Damaged.Take(3))}" : "");
        if (_note == null) ShowEditor(false);
    }

    private FrameworkElement Row(Note n, string snippet)
    {
        var p = new StackPanel { Tag = n, Margin = new Thickness(2, 3, 2, 5) };
        p.Children.Add(new TextBlock { Text = (n.Favorite ? "★ " : "") + LockUi.Decorate("note:" + n.Id, n.Title), FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        var hosts = string.Join(", ", n.HostIds.Select(AppData.Current.FindHost).Where(h => h != null).Select(h => h!.Display).Take(3));
        var meta = new TextBlock { Text = $"{n.Updated:dd MMM HH:mm}" + (hosts.Length > 0 ? " · " + hosts : "") + (n.Tags.Count > 0 ? " · #" + string.Join(" #", n.Tags.Take(4)) : ""), TextTrimming = TextTrimming.CharacterEllipsis }.Fs(FontStep.Small);
        meta.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        p.Children.Add(meta);
        if (snippet.Length > 0)
        {
            var s = new TextBlock { Text = snippet, TextTrimming = TextTrimming.CharacterEllipsis }.Fs(FontStep.Small);
            s.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            p.Children.Add(s);
        }
        return p;
    }

    // ---- editor -------------------------------------------------------------------------------------------------------

    /// <summary>Shows a host's notebook (creating nothing until the user writes).</summary>
    public void ShowHost(string hostId) { _hostScope = hostId; _search.Text = ""; FillScopes(); Refresh(); var first = NoteStore.ForHost(hostId).FirstOrDefault(); if (first != null) Open(first); else Status("No notes for this host yet — ＋ New note."); }

    public void Open(Note n)
    {
        SaveNow();
        if (Closed(n))
        {
            _note = null;
            ShowEditor(false);
            _empty.Text = $"🔒 “{n.Title}” is locked. Unlock it to read or edit it.";
            _ = Dispatcher.InvokeAsync(async () => { if (await LockGate.EnsureAsync("note:" + n.Id, "open " + n.Title)) Open(n); });
            return;
        }
        _loading = true;
        _note = n;
        _title.Text = n.Title; _body.Text = n.Body; _tags.Text = string.Join(", ", n.Tags);
        _fav.IsChecked = n.Favorite; _fav.Content = n.Favorite ? "★ Favourite" : "☆ Favourite";
        _baseTitle = n.Title; _baseBody = n.Body;
        _title.IsReadOnly = _body.IsReadOnly = n.Deleted != null;
        _loading = false;
        ShowHosts();
        ShowEditor(true);
        Status($"Last saved {n.Updated:yyyy-MM-dd HH:mm:ss} · encrypted · {n.Revisions.Count} earlier version(s)" + (n.Deleted != null ? " · IN TRASH (read-only; Restore to edit)" : ""));
        _list.SelectedItem = _list.Items.OfType<FrameworkElement>().FirstOrDefault(e => e.Tag == n);
    }

    private void ShowEditor(bool on)
    {
        _editor.Children[0].Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        _empty.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        _empty.Text = NoteStore.All.Count == 0
            ? "No notes yet. ＋ New note, or right-click a host → Notes, or select text in a terminal → Add selection to notes.\n\nNotes are encrypted like everything else and searchable offline — no AI needed."
            : "Select a note, or search. Tip: Ctrl+Shift+T inserts a timestamp while typing.";
    }

    private void ShowHosts()
    {
        _hosts.Children.Clear();
        if (_note == null) return;
        foreach (var id in _note.HostIds.ToList())
        {
            var h = AppData.Current.FindHost(id);
            var chip = SmallBtn(h?.Display ?? "(deleted host)", () => { });
            chip.ToolTip = h?.Endpoint ?? "This host no longer exists";
            Menus.AttachDropDown(chip, () => new object?[]
            {
                h != null ? Menus.Item("⌨ Terminal", () => MainWindow.Instance.OpenTerminal(h)) : null,
                h != null ? Menus.Item("All notes for this host", () => ShowHost(h.Id)) : null,
                Menus.Item("Unlink from this note", () => { _note.HostIds.Remove(id); SaveNow(); ShowHosts(); }),
            });
            _hosts.Children.Add(chip);
        }
        if (_note.HostIds.Count == 0) { var none = new TextBlock { Text = "none", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) }; none.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); _hosts.Children.Add(none); }
    }

    /// <summary>Writes the open note if anything changed (title, body, tags). Safe to call often.</summary>
    public void SaveNow(bool snapshot = false)
    {
        _autosave.Stop();
        var n = _note;
        if (n == null || n.Deleted != null) return;
        var tags = _tags.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(t => t.TrimStart('#').ToLowerInvariant()).Where(t => t.Length > 0).Distinct().ToList();
        var title = _title.Text.Trim().Length > 0 ? _title.Text.Trim() : "Untitled note";
        bool changed = title != n.Title || _body.Text != n.Body || !tags.SequenceEqual(n.Tags);
        if (!changed && !snapshot) return;
        n.Title = title; n.Body = _body.Text; n.Tags = tags;
        try
        {
            NoteStore.Save(n, _baseTitle, _baseBody, snapshot);
            if (n.Revisions.LastOrDefault() is { } r && DateTime.Now - r.At < TimeSpan.FromSeconds(2)) { _baseTitle = n.Title; _baseBody = n.Body; }
            Status($"Saved {DateTime.Now:HH:mm:ss} · encrypted · {n.Revisions.Count} earlier version(s)");
            UpdateRow(n);
        }
        catch (Exception ex) { Status("✕ NOT SAVED: " + ex.Message + " — your text is still here; try again (Ctrl+S)."); App.Log("Note save", ex); }
    }

    private void UpdateRow(Note n)
    {
        var i = _list.Items.OfType<FrameworkElement>().ToList().FindIndex(e => e.Tag == n);
        if (i < 0) return;
        var items = _list.Items.OfType<FrameworkElement>().ToList();
        items[i] = Row(n, "");
        _list.ItemsSource = items;
        _list.SelectedIndex = i;
    }

    private void NewNote(HostEntry? host)
    {
        SaveNow();
        var n = NoteStore.Create(host != null ? $"{host.Display} — {DateTime.Now:yyyy-MM-dd}" : $"Note {DateTime.Now:yyyy-MM-dd HH:mm}", host != null ? new[] { host.Id } : null, $"[{DateTime.Now:HH:mm:ss}] ");
        _search.Text = "";
        FillScopes(); Refresh();
        Open(n);
        _body.Focus(); _body.CaretIndex = _body.Text.Length;
    }

    private void Insert(string text)
    {
        if (_note == null || _body.IsReadOnly || text.Length == 0) return;
        _body.SelectedText = text;
        _body.CaretIndex = _body.SelectionStart + text.Length;
        _body.SelectionLength = 0;
        _body.Focus();
    }

    private void Wrap(string before, string after)
    {
        if (_note == null || _body.IsReadOnly) return;
        var sel = _body.SelectedText;
        Insert(before + sel + after + (sel.Length == 0 ? "" : "\n"));
    }

    private void ShowHistory()
    {
        if (_note is not { } n) return;
        SaveNow();
        if (n.Revisions.Count == 0) { Dialogs.Info("History", "No earlier versions yet. A version is kept every 10 minutes while you edit, and whenever you press Ctrl+S."); return; }
        var w = Dialogs.MakeWindow("History — " + n.Title, 900, 600);
        var grid = new Grid { Margin = new Thickness(14) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var revs = n.Revisions.AsEnumerable().Reverse().ToList();
        var list = new ListBox { ItemsSource = revs.Select(r => $"{r.At:yyyy-MM-dd HH:mm:ss}  ·  {r.Body.Length:N0} chars").ToList(), Margin = new Thickness(0, 0, 8, 0) };
        var preview = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(6) }.Editor();
        list.SelectionChanged += (_, _) => { if (list.SelectedIndex >= 0) preview.Text = $"# {revs[list.SelectedIndex].Title}\n\n{revs[list.SelectedIndex].Body}"; };
        Grid.SetColumn(preview, 1);
        grid.Children.Add(list); grid.Children.Add(preview);
        var restore = new Button { Content = "Restore this version", Style = (Style)FindResource("PrimaryButton"), Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        restore.Click += (_, _) =>
        {
            if (list.SelectedIndex < 0) return;
            var r = revs[list.SelectedIndex];
            var (bt, bb) = (n.Title, n.Body);
            n.Title = r.Title; n.Body = r.Body;
            NoteStore.Save(n, bt, bb, snapshot: true);   // the current text becomes a version too: nothing is lost
            w.Close();
            Open(n);
            Status($"Restored the version from {r.At:yyyy-MM-dd HH:mm:ss} (the previous text was kept as a version).");
        };
        Grid.SetRow(restore, 1); Grid.SetColumnSpan(restore, 2);
        grid.Children.Add(restore);
        w.Content = grid;
        list.SelectedIndex = 0;
        w.ShowDialog();
    }

    private void EmptyTrash()
    {
        var c = NoteStore.Trash.Count;
        if (c == 0) { Dialogs.Info("Trash", "The Trash is empty."); return; }
        if (!Dialogs.Confirm("Empty trash", $"Permanently delete {c} note(s) in the Trash? This removes their encrypted files and cannot be undone (an encrypted backup still has them).", "Delete permanently", danger: true)) return;
        if (_note?.Deleted != null) { _note = null; ShowEditor(false); }
        NoteStore.EmptyTrash();
        FillScopes(); Refresh();
    }

    private async Task AskAi(string task)
    {
        if (_note is not { } n || Assist.Current is not { } assist) return;
        SaveNow();
        bool selection = task.StartsWith("Explain the selected");
        var text = selection ? _body.SelectedText : n.Body;
        if (text.Trim().Length == 0) { Dialogs.Info("AI", selection ? "Select some text in the note first." : "The note is empty."); return; }
        await assist.AskAsync((selection ? "selection from note " : "note ") + n.Title, $"# {n.Title}\nTags: {string.Join(", ", n.Tags)}\n\n{text}", task);
    }

    private HostEntry? PickHost(string message) =>
        AppData.Current.Hosts.Count == 0 ? null : Dialogs.Pick("Host", message, AppData.Current.Hosts.OrderBy(h => h.Display, StringComparer.OrdinalIgnoreCase), h => $"{h.Display}   {h.Endpoint}");

    // ---- helpers ------------------------------------------------------------------------------------------------------

    private Button SmallBtn(string text, Action a, bool primary = false, bool danger = false)
    {
        var b = new Button { Content = text, Style = (Style)FindResource(primary ? "PrimaryButton" : danger ? "DangerButton" : "SmallButton"), Margin = new Thickness(0, 0, 4, 4) };
        if (primary || danger) { b.Padding = new Thickness(8, 3, 8, 3); b.MinWidth = 0; }
        b.Click += (_, _) => a();
        return b;
    }

    private TextBlock Lbl(string t, double left = 0) => new() { Text = t, Style = (Style)FindResource("Label"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(left, 0, 6, 0) };

    private void Status(string s) => _status.Text = s;

    private static string SafeName(string s) => string.Concat(s.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '-' : c)).Trim();

    // test seams
    internal Note? OpenNote => _note;
    internal void TestType(string text) { _body.Text += text; }

    public void Dispose()
    {
        SaveNow();
        NoteStore.Changed -= OnStoreChanged;
        Locks.Changed -= OnLocksChanged;
        AppData.Changed -= OnStoreHosts;
        _autosave.Stop(); _searchDelay.Stop();
        if (Current == this) Current = null;
    }
}
