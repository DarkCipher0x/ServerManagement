using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DcxManager.Files;
using DcxManager.Services;
using DcxManager.Ssh;

namespace DcxManager.Views;

// FilePane: the Midnight Commander tools (0.3.0 §15) — marking (Insert, + − *), the cursor that moves without unmarking,
// folder sizes, Find file (results shown in the pane, "panelize"), archives (read-only, in memory, zip-slip safe), quick view,
// directory history and hotlist, links, chown and new files. The pure parts are in Files/McTools.cs and Files/ArchiveReader.cs.
internal sealed partial class FilePane
{
    private ArchiveReader? _arc;
    private string _arcFile = "", _arcInner = "";
    private List<FsItem>? _panel;
    private string _panelTitle = "";
    private readonly Dictionary<string, long> _sizes = new(StringComparer.Ordinal);
    private readonly List<string> _history = new();
    private TextBox? _quick;
    private bool _marking;
    private FsItem? _cursor;

    public bool InArchive => _arc != null;
    public bool QuickView => _quick?.Visibility == Visibility.Visible;
    public IReadOnlyList<string> History => _history;
    public IReadOnlyList<FsItem>? Panel => _panel;
    /// <summary>The marked items (Midnight Commander's "tagged" files); empty when nothing is marked.</summary>
    public IReadOnlyList<FsItem> Marked => _marking ? Selected.Where(i => !i.IsParent).ToList() : [];
    /// <summary>The item under the cursor.</summary>
    public FsItem? Current => _marking && _cursor != null && _list.Items.Contains(_cursor) ? _cursor : _list.SelectedItem as FsItem;
    public IReadOnlyList<FsItem> ListingForTest => _list.Items.OfType<FsItem>().ToList();
    public void SelectForTest(string name) { _marking = false; _list.SelectedItem = _list.Items.OfType<FsItem>().FirstOrDefault(i => i.Name == name); }

    private string PlaceText => InArchive ? $"{Ref.Title} · 🗜 {System.IO.Path.GetFileName(_arcFile.Replace('\\', '/'))}{(_arcInner.Length > 0 ? "/" + _arcInner : "")} (read-only)"
                              : _panel != null ? $"{Ref.Title} · {_panelTitle}" : Ref.Title;

    private void LeaveModes() { _arc = null; _arcFile = ""; _arcInner = ""; _panel = null; _panelTitle = ""; }

    private void Visited(string path)
    {
        if (path.Length == 0) return;
        _history.Remove(path);
        _history.Insert(0, path);
        if (_history.Count > 40) _history.RemoveAt(_history.Count - 1);
    }

    private IEnumerable<FsItem> Listing()
    {
        if (_arc != null)
            return _arc.Children(_arcInner).Select(e => new FsItem
            {
                Name = e.Path.Split('/')[^1], FullPath = e.Path, IsDir = e.IsDir, IsLink = e.IsLink, Length = e.Length, Modified = e.Modified,
                Perms = e.Unsafe ? "⚠ unsafe name" : e.IsLink ? "link" : e.Mode > 0 ? Convert.ToString(e.Mode, 8) : "",
            });
        return _panel ?? _items;
    }

    private FsItem? ParentItem()
    {
        if (_arc != null) return new FsItem { Name = "..", FullPath = "", IsDir = true, IsParent = true, Display = _arcInner.Length == 0 ? ".. (leave the archive)" : ".." };
        if (_panel != null) return new FsItem { Name = "..", FullPath = _path, IsDir = true, IsParent = true, Display = ".. (back to the folder)" };
        return _path.Length > 0 && !(_fs.IsRemote && _path == "/") ? new FsItem { Name = "..", FullPath = _fs.Parent(_path), IsDir = true, IsParent = true } : null;
    }

    private bool ReadOnlyHere(string what)
    {
        if (!InArchive) return false;
        Dialogs.Info(what, "This pane is inside an archive, which is read-only. Copy files out with F5, or leave it with “..” at its top.");
        return true;
    }

    /// <summary>Enter on an archive, inside one, or in Find results. True = handled.</summary>
    private async Task<bool> OpenSpecialAsync(FsItem it)
    {
        if (_arc != null)
        {
            if (it.IsParent)
            {
                if (_arcInner.Length == 0) { await GoAsync(_path, record: false); return true; }
                var was = _arcInner;
                _arcInner = _arcInner.Contains('/') ? _arcInner[.._arcInner.LastIndexOf('/')] : "";
                Apply();
                if (_list.Items.OfType<FsItem>().FirstOrDefault(i => i.FullPath == was) is { } back) { _list.SelectedItem = back; _list.ScrollIntoView(back); }
                return true;
            }
            if (it.IsDir) { _arcInner = it.FullPath; Apply(); if (_list.Items.Count > 0) _list.SelectedIndex = 0; return true; }
            await ArchiveOpenAsync(it, hex: false);
            return true;
        }
        if (_panel != null)
        {
            if (it.IsParent) { await GoAsync(_path, record: false); return true; }
            var target = it.FullPath;
            await GoAsync(it.IsDir ? target : _fs.Parent(target));
            if (!it.IsDir && _list.Items.OfType<FsItem>().FirstOrDefault(i => i.FullPath == target) is { } hit) { _list.SelectedItem = hit; _list.ScrollIntoView(hit); }
            return true;
        }
        if (!it.IsDir && !it.IsParent && ArchiveReader.KindOf(it.Name) != null) { await EnterArchiveAsync(it); return true; }
        return false;
    }

    // ---- archives -----------------------------------------------------------------------------------------------------

    public async Task EnterArchiveAsync(FsItem it)
    {
        if (ArchiveReader.KindOf(it.Name) is not { } kind) return;
        if (it.Length > ArchiveReader.MaxArchive) { Dialogs.Info("Archive", $"{it.Name} is {Fmt.Bytes(it.Length)} — archives up to {Fmt.Bytes(ArchiveReader.MaxArchive)} open in the File commander. Copy it to this PC and use an archiver."); return; }
        _status.Text = $"reading {it.Name}…";
        try
        {
            byte[] data;
            await using (var s = await _fs.OpenReadAsync(it.FullPath)) { using var ms = new MemoryStream(); await s.CopyToAsync(ms); data = ms.ToArray(); }
            var arc = await Task.Run(() => new ArchiveReader(data, kind));
            _arc = arc; _arcFile = it.FullPath; _arcInner = ""; _panel = null; _marking = false;
            _filter.Text = "";
            Apply();
            if (_list.Items.Count > 1) _list.SelectedIndex = 1;
            var bad = arc.Entries.Count(e => e.Unsafe);
            if (bad > 0) _status.Text += $" · ⚠ {bad} entr{(bad == 1 ? "y has" : "ies have")} an unsafe name (never extracted)";
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException or FormatException or UnauthorizedAccessException)
        {
            _status.Text = "✕ " + ex.Message;
            Dialogs.Error("Archive", $"{it.Name} couldn't be read as an archive: {ex.Message}");
        }
    }

    private async Task ArchiveOpenAsync(FsItem it, bool hex)
    {
        if (_arc is not { } arc || it.IsDir) return;
        try
        {
            var bytes = await Task.Run(() => arc.Read(it.FullPath));
            var title = $"{Ref.Title}:{_arcFile} → {it.FullPath} (read-only)";
            if (hex || bytes.Take(8000).Any(b => b == 0))
                HexEditorWindow.Open(title, bytes, _ => throw new InvalidOperationException("This file is inside an archive, which is read-only. Copy it out with F5 to change it."));
            else Dialogs.ShowText(title, Encoding.UTF8.GetString(bytes));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException) { Dialogs.Error("Archive", ex.Message); }
    }

    /// <summary>F5 from inside an archive: the selected files and folders are written into the other pane's folder.</summary>
    public async Task<int> ExtractAsync(FilePane to, IReadOnlyList<FsItem> sel, bool ask = true)
    {
        if (_arc is not { } arc) return 0;
        if (to.Path.Length == 0 || to.InArchive) { Dialogs.Info("Copy", "Open a folder in the other pane first."); return 0; }
        var files = sel.SelectMany(i => arc.Under(i.FullPath)).DistinctBy(e => e.Path).ToList();
        var skipped = files.Where(e => e.Unsafe || e.IsLink).ToList();
        files = files.Except(skipped).ToList();
        if (files.Count == 0) { Dialogs.Info("Copy", skipped.Count > 0 ? "Only unsafe names or links were selected — they're never extracted." : "Nothing to copy."); return 0; }
        var arcName = System.IO.Path.GetFileName(_arcFile.Replace('\\', '/'));
        var policy = Conflict.Overwrite;
        if (ask)
        {
            var clash = files.Select(e => ArchiveReader.Relative(e.Path, _arcInner).Split('/')[0]).Distinct().Where(n => to.Items.Any(i => i.Name == n)).ToList();
            var msg = $"Copy {files.Count} file(s), {Fmt.Bytes(files.Sum(e => e.Length))}, out of {arcName}\nto {to.Ref.Title}:{to.Path}" +
                      (skipped.Count > 0 ? $"\n\n⚠ {skipped.Count} unsafe name(s) or link(s) are left out." : "") +
                      (clash.Count > 0 ? $"\n\nAlready there: {string.Join(", ", clash.Take(8))}{(clash.Count > 8 ? "…" : "")}" : "");
            var pick = clash.Count > 0 ? Dialogs.Choose("Copy out of the archive", msg, new[] { "Overwrite", "Skip existing", "Cancel" }, cancelIndex: 2)
                     : Dialogs.Confirm("Copy out of the archive", msg, "Copy") ? 0 : -1;
            if (pick is < 0 or 2) return 0;
            policy = pick == 1 ? Conflict.Skip : Conflict.Overwrite;
        }
        int done = 0;
        try
        {
            foreach (var e in files)
            {
                var rel = ArchiveReader.Relative(e.Path, _arcInner).Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (rel.Length == 0 || rel.Any(p => p is "." or ".." || p.IndexOfAny(new[] { '\\', ':', '\0' }) >= 0)) continue;   // belt and braces: never outside the folder
                var dir = to.Path;
                foreach (var seg in rel[..^1])
                {
                    dir = to._fs.Join(dir, seg);
                    if (await to._fs.StatAsync(dir) == null) await to._fs.MkdirAsync(dir);
                }
                var dest = to._fs.Join(dir, rel[^1]);
                if (policy == Conflict.Skip && await to._fs.StatAsync(dest) != null) continue;
                var bytes = await Task.Run(() => arc.Read(e.Path));
                await using (var w = await to._fs.CreateAsync(dest)) await w.WriteAsync(bytes);
                done++;
                to._status.Text = $"copying out of {arcName}: {done} of {files.Count}…";
            }
            AuditLog.Write("you", to.Ref.Title, $"copied {done} file(s) out of {Ref.Title}:{_arcFile}", to.Path);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException or Renci.SshNet.Common.SshException)
        { Dialogs.Error("Copy out of the archive", ex.Message + (ex is ArgumentException or NotSupportedException ? " (a name in the archive isn't allowed on this PC)" : "")); }
        await to.RefreshAsync();
        MainWindow.Instance?.Status($"{done} file(s) copied out of {arcName}");
        return done;
    }

    // ---- marking ------------------------------------------------------------------------------------------------------

    private bool McListKey(KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        if (e.Key == Key.Insert && mods == ModifierKeys.None || e.Key == Key.T && mods == ModifierKeys.Control) { ToggleMark(); return true; }
        if (_marking && mods == ModifierKeys.None)
        {
            int page = Math.Max(1, (int)(_list.ActualHeight / 24) - 1);
            switch (e.Key)
            {
                case Key.Down: MoveCursor(1); return true;
                case Key.Up: MoveCursor(-1); return true;
                case Key.PageDown: MoveCursor(page); return true;
                case Key.PageUp: MoveCursor(-page); return true;
                case Key.Home: MoveCursor(-_list.Items.Count); return true;
                case Key.End: MoveCursor(_list.Items.Count); return true;
                case Key.Enter: _ = OpenSelectedAsync(); return true;
            }
        }
        if (e.Key == Key.PageUp && mods == ModifierKeys.Control) { _ = Current is { IsParent: true } p ? OpenSpecialAsync(p).ContinueWith(_ => { }) : GoUpAsync(); return true; }
        if (e.Key == Key.PageDown && mods == ModifierKeys.Control) { _ = OpenSelectedAsync(); return true; }
        return false;
    }

    private Task GoUpAsync() => ParentItem() is { } up && (InArchive || _panel != null) ? OpenSpecialAsync(up) : GoAsync(_fs.Parent(_path));

    private bool McGroupKey(string text)
    {
        switch (text)
        {
            case "+": GroupUi(true); return true;
            case "-" or "\\": GroupUi(false); return true;
            case "*": Invert(); return true;
            default: return false;
        }
    }

    public void ToggleMark()
    {
        if (Current is not { } cur) return;
        if (!_marking) { _marking = true; _list.SelectedItems.Clear(); }
        if (!cur.IsParent)
        {
            if (_list.SelectedItems.Contains(cur)) _list.SelectedItems.Remove(cur); else _list.SelectedItems.Add(cur);
        }
        _cursor = cur;
        MoveCursor(1);
        UpdateMarkStatus();
    }

    private void MoveCursor(int delta)
    {
        if (_list.Items.Count == 0) return;
        var from = Current != null ? _list.Items.IndexOf(Current) : 0;
        var to = Math.Clamp(from + delta, 0, _list.Items.Count - 1);
        _cursor = (FsItem)_list.Items[to];
        _list.ScrollIntoView(_cursor);
        if (_list.ItemContainerGenerator.ContainerFromIndex(to) is ListViewItem li) li.Focus();   // focus only: the marks stay
        _owner.SelectionMoved(this);
    }

    private void UpdateMarkStatus()
    {
        var m = Marked;
        if (m.Count == 0) { _marking = false; if (_cursor != null) _list.SelectedItem = _cursor; Apply(); return; }
        MainWindow.Instance?.Status($"{m.Count} marked · {Fmt.Bytes(m.Where(i => !i.IsDir).Sum(i => i.Length))}");
    }

    private void GroupUi(bool select)
    {
        var p = Dialogs.Prompt(select ? "Select group" : "Unselect group",
            "Shell pattern, e.g. *.log or *.conf;*.cfg — end it with / to match folders instead of files:", "*")?.Trim();
        if (string.IsNullOrEmpty(p)) return;
        var n = SelectGroup(p.TrimEnd('/'), select, p.EndsWith('/'));
        MainWindow.Instance?.Status($"{n} item(s) {(select ? "marked" : "unmarked")} by {p}");
    }

    /// <summary>"+" / "−": marks or unmarks what matches a shell pattern. Returns how many matched.</summary>
    public int SelectGroup(string pattern, bool select, bool dirs = false)
    {
        var hits = McTools.Matching(_list.Items.OfType<FsItem>(), pattern, dirs).ToList();
        if (!_marking) { _cursor = Current; _marking = true; _list.SelectedItems.Clear(); }
        foreach (var h in hits)
        {
            if (select && !_list.SelectedItems.Contains(h)) _list.SelectedItems.Add(h);
            else if (!select) _list.SelectedItems.Remove(h);
        }
        if (_list.SelectedItems.Count == 0) { _marking = false; if (_cursor != null) _list.SelectedItem = _cursor; }
        return hits.Count;
    }

    /// <summary>"*": inverts the marks on files (folders stay as they are, like Midnight Commander).</summary>
    public void Invert()
    {
        if (!_marking) { _cursor = Current; _marking = true; _list.SelectedItems.Clear(); }
        foreach (var i in _list.Items.OfType<FsItem>().Where(i => !i.IsParent && !i.IsDir).ToList())
            if (_list.SelectedItems.Contains(i)) _list.SelectedItems.Remove(i); else _list.SelectedItems.Add(i);
        if (_list.SelectedItems.Count == 0) { _marking = false; if (_cursor != null) _list.SelectedItem = _cursor; }
    }

    private void ApplyKeep()
    {
        var marked = Selected.Select(i => i.FullPath).ToHashSet();
        var cur = Current?.FullPath;
        Apply();
        foreach (var i in _list.Items.OfType<FsItem>().Where(i => marked.Contains(i.FullPath))) _list.SelectedItems.Add(i);
        if (_marking && cur != null) _cursor = _list.Items.OfType<FsItem>().FirstOrDefault(i => i.FullPath == cur);
    }

    // ---- folder sizes, find, new file ---------------------------------------------------------------------------------

    /// <summary>Ctrl+Space: the size of the marked folders, the folder under the cursor, or (on "..") every folder here.</summary>
    public async Task MeasureAsync(IEnumerable<FsItem>? which = null)
    {
        if (InArchive || _path.Length == 0) return;
        IEnumerable<FsItem> pick;
        if (which != null) pick = which;
        else if (Marked.Count > 0) pick = Marked;
        else if (Current is { IsParent: true }) pick = _list.Items.OfType<FsItem>();
        else pick = Current != null ? new[] { Current } : Array.Empty<FsItem>();
        var dirs = pick.Where(i => i.IsDir && !i.IsParent && !i.IsDrive && !i.IsLink).ToList();
        if (dirs.Count == 0) return;
        _status.Text = $"measuring {dirs.Count} folder(s)…";
        try
        {
            if (_fs is HostFs h)
            {
                var r = await SshPool.RunAsync(h.Host, McTools.DuCommand(dirs.Select(d => d.FullPath)), timeout: TimeSpan.FromMinutes(5));
                foreach (var (p, n) in McTools.ParseDu(r.Output)) _sizes[p] = n;
                if (r.Output.Trim().Length == 0) throw new InvalidOperationException("du gave no answer" + (r.Error.Length > 0 ? ": " + r.Error.Trim() : ""));
            }
            else
                foreach (var d in dirs) _sizes[d.FullPath] = await Task.Run(() => McTools.DirSizeLocal(d.FullPath, CancellationToken.None));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Renci.SshNet.Common.SshException) { Dialogs.Error("Folder size", ex.Message); }
        ApplyKeep();
    }

    private void FindUi()
    {
        if (InArchive || _path.Length == 0) { Dialogs.Info("Find file", "Open a folder first (Find file searches below this pane's folder)."); return; }
        if (Dialogs.Prompt("Find file", $"Below {Ref.Title}:{_path}\n\nFile name (shell pattern, e.g. *.conf or nginx*):", "*") is not { } name) return;
        var content = Dialogs.Prompt("Find file", "Containing text (optional; plain text, case-insensitive):", "");
        if (content == null) return;
        _ = FindAsync(name, content);
    }

    /// <summary>Find file: matches below this folder become this pane's listing (Midnight Commander's "panelize").</summary>
    public async Task FindAsync(string pattern, string? content, int max = 2000)
    {
        if (InArchive || _path.Length == 0) return;
        _status.Text = "searching…";
        try
        {
            List<string> hits;
            if (_fs is HostFs h)
            {
                var r = await SshPool.RunAsync(h.Host, McTools.FindCommand(_path, pattern, content, max), timeout: TimeSpan.FromMinutes(10));
                hits = r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            }
            else hits = await Task.Run(() => McTools.FindLocal(_path, pattern, content, max, CancellationToken.None));
            var root = _path.TrimEnd('/', '\\');
            _panel = hits.Select(p =>
            {
                var rel = p.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? p[root.Length..].TrimStart('/', '\\') : p;
                if (_fs.IsRemote) return new FsItem { Name = p.Split('/')[^1], FullPath = p, Display = rel };
                var fi = new FileInfo(p);
                return new FsItem { Name = fi.Name, FullPath = p, Display = rel, Length = fi.Exists ? fi.Length : 0, Modified = fi.Exists ? fi.LastWriteTime : default };
            }).ToList();
            _panelTitle = $"🔎 {hits.Count}{(hits.Count >= max ? "+" : "")} found for “{pattern}”{(string.IsNullOrEmpty(content) ? "" : $" containing “{content}”")}";
            _arc = null; _marking = false; _filter.Text = "";
            Apply();
            if (_list.Items.Count > 1) _list.SelectedIndex = 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or Renci.SshNet.Common.SshException) { _status.Text = "✕ " + ex.Message; }
    }

    /// <summary>Shift+F4: a new empty file here, opened in the editor.</summary>
    public async Task NewFileAsync(string? name = null, bool open = true)
    {
        if (_path.Length == 0 || ReadOnlyHere("New file")) return;
        name ??= Dialogs.Prompt("New file", $"File name in {Ref.Title}:{_path}");
        if (string.IsNullOrWhiteSpace(name)) return;
        if (name.IndexOfAny(new[] { '/', '\\', '\0' }) >= 0) { Dialogs.Error("New file", "Just a name — no folders."); return; }
        var path = _fs.Join(_path, name);
        try
        {
            if (await _fs.StatAsync(path) != null) { Dialogs.Error("New file", $"{name} already exists."); return; }
            await using (await _fs.CreateAsync(path)) { }
            AuditLog.Write("you", Ref.Title, "created file " + path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Renci.SshNet.Common.SshException) { Dialogs.Error("New file", ex.Message); return; }
        await RefreshAsync();
        if (open) await EditAsync(new FsItem { Name = name, FullPath = path });
    }

    // ---- links, chown -------------------------------------------------------------------------------------------------

    /// <summary>Ctrl+X S / Ctrl+X L: a link to the current item, made in the other pane's folder (same machine).</summary>
    public async Task LinkAsync(bool symbolic)
    {
        var what = symbolic ? "Symbolic link" : "Hard link";
        var other = _owner.Other(this);
        if (One is not { } it || ReadOnlyHere(what) || other.InArchive) return;
        if (!other.Ref.Equals(Ref) || other.Path.Length == 0) { Dialogs.Info(what, "Open a folder on the same machine in the other pane: the link is made there."); return; }
        if (Dialogs.Prompt(what, $"Link to {it.FullPath}\n\nLink path:", other._fs.Join(other.Path, it.Name)) is not { Length: > 0 } link) return;
        try
        {
            if (_fs is HostFs h)
            {
                var r = await SshPool.RunAsync(h.Host, McTools.LinkCommand(it.FullPath, link, symbolic));
                if (!r.Ok) throw new InvalidOperationException(r.Combined.Trim());
            }
            else McTools.LinkLocal(it.FullPath, link, symbolic);
            AuditLog.Write("you", Ref.Title, $"{(symbolic ? "symlink" : "hard link")} {link} → {it.FullPath}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Renci.SshNet.Common.SshException)
        {
            Dialogs.Error(what, ex.Message + (symbolic && !_fs.IsRemote ? "\n\nOn Windows, symbolic links need Developer Mode (Settings → System → For developers) or an administrator." : ""));
        }
        await other.RefreshAsync();
    }

    /// <summary>Ctrl+X O: chown on a server (user, user:group or :group; recursive for folders if you choose; sudo if refused).</summary>
    public async Task ChownAsync()
    {
        var sel = Marked.Count > 0 ? Marked.ToList() : One is { } o ? [o] : [];
        if (_fs is not HostFs h || sel.Count == 0 || ReadOnlyHere("chown")) { if (!_fs.IsRemote) Dialogs.Info("chown", "chown is for files on a server."); return; }
        if (Dialogs.Prompt("chown", $"New owner for {(sel.Count == 1 ? sel[0].Name : sel.Count + " items")} (now uid:gid {sel[0].Owner}):\n\nuser, user:group or :group", "") is not { Length: > 0 } spec) return;
        if (!McTools.ValidOwner(spec)) { Dialogs.Error("chown", "That doesn't look like user, user:group or :group."); return; }
        bool recursive = false;
        if (sel.Any(i => i.IsDir))
        {
            var pick = Dialogs.Choose("chown", "Change the folders' contents too?", new[] { "Only these", "Recursively (everything inside)", "Cancel" }, cancelIndex: 2);
            if (pick is < 0 or 2) return;
            recursive = pick == 1;
        }
        if (!Envs.Confirm(h.Host, "chown", $"chown {(recursive ? "-R " : "")}{spec} on {sel.Count} item(s) on {Ref.Title}?", "Change owner")) return;
        var cmd = McTools.ChownCommand(spec, sel.Select(i => i.FullPath), recursive);
        try
        {
            var r = await SshPool.RunAsync(h.Host, cmd);
            if (!r.Ok && r.Combined.Contains("not permitted", StringComparison.OrdinalIgnoreCase)
                && Dialogs.Confirm("chown", "The server refused (only root can give files away). Run it with sudo?", "Use sudo"))
                r = await SshPool.RunAsync(h.Host, cmd, sudo: true);
            if (!r.Ok) throw new InvalidOperationException(r.Combined.Trim());
            AuditLog.Write("you", Ref.Title, $"chown {(recursive ? "-R " : "")}{spec} {string.Join(", ", sel.Select(i => i.FullPath))}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or Renci.SshNet.Common.SshException) { Dialogs.Error("chown", ex.Message); }
        await RefreshAsync();
    }

    // ---- history, hotlist ---------------------------------------------------------------------------------------------

    public void HistoryMenu()
    {
        var items = _history.Take(30).Select(p => (object?)Menus.Item(p, () => _ = GoAsync(p))).ToList();
        if (items.Count == 0) items.Add(Menus.Item("(no folders visited yet)", () => { }, enabled: false));
        Menus.ShowAt(_list, items);
    }

    public void AddToHotlist(string? name = null)
    {
        if (_path.Length == 0 || InArchive) return;
        name ??= Dialogs.Prompt("Directory hotlist", $"Name for {Ref.Title}:{_path}", System.IO.Path.GetFileName(_path.TrimEnd('/', '\\')) is { Length: > 0 } n ? n : _path)?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        var list = AppData.Current.CommanderHotlist;
        list.RemoveAll(x => x.Place == Ref.ToString() && x.Path == _path);
        list.Add(new HotlistEntry { Name = name.Length > 80 ? name[..80] : name, Place = Ref.ToString(), Path = _path });
        AppData.Save();
    }

    public void HotlistMenu()
    {
        var list = AppData.Current.CommanderHotlist;
        var items = new List<object?>();
        foreach (var h in list.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            var place = FsRef.Parse(h.Place);
            if (place.Kind == "host" && place.Host == null) continue;
            items.Add(Menus.Item($"{h.Name}   ·   {place.Title}:{h.Path}", () => _ = OpenAsync(place, h.Path)));
        }
        if (items.Count == 0) items.Add(Menus.Item("(empty — add this folder below)", () => { }, enabled: false));
        items.Add(Menus.Sep());
        items.Add(Menus.Item("＋ Add this folder…", () => AddToHotlist(), enabled: _path.Length > 0 && !InArchive));
        if (list.Count > 0)
            items.Add(Menus.Sub("Remove", list.Select(h => (object?)Menus.Item(h.Name, () => { list.Remove(h); AppData.Save(); })).ToArray()));
        Menus.ShowAt(_list, items);
    }

    // ---- sorting, quick view ------------------------------------------------------------------------------------------

    public string SortKey => _sort;
    public bool SortDescending => _desc;
    public void SetSort(string key, bool? desc = null) { _sort = key; if (desc != null) _desc = desc.Value; ApplyKeep(); }
    public void FocusFilter() { _filter.Focus(); _filter.SelectAll(); }

    // ---- listing mode (full / brief) and quick search (0.3.0 §15 review gaps) ---------------------------------------------

    private System.Windows.Controls.ViewBase? _fullView;
    public bool Brief { get; private set; }

    /// <summary>MC's brief mode: names only, flowing into as many columns as fit (full mode is the detailed list).</summary>
    public void SetBrief(bool on)
    {
        if (on == Brief) return;
        Brief = on;
        if (on)
        {
            _fullView ??= _list.View;
            _list.View = null;
            _list.ItemTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse(
                "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><TextBlock Margin=\"2,0,10,0\"><Run Text=\"{Binding Icon}\"/><Run Text=\" \"/><Run Text=\"{Binding Shown}\"/></TextBlock></DataTemplate>");
            _list.ItemsPanel = (ItemsPanelTemplate)System.Windows.Markup.XamlReader.Parse(
                "<ItemsPanelTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><WrapPanel Orientation=\"Vertical\" Height=\"{Binding ActualHeight, RelativeSource={RelativeSource AncestorType=ScrollContentPresenter}}\"/></ItemsPanelTemplate>");
            ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Auto);
            ScrollViewer.SetVerticalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        }
        else
        {
            _list.ItemTemplate = null;
            _list.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)));
            _list.View = _fullView;
            ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Auto);
            ScrollViewer.SetVerticalScrollBarVisibility(_list, ScrollBarVisibility.Auto);
        }
        ApplyKeep();
    }

    private string _quickSearch = "";
    public bool QuickSearching { get; private set; }

    /// <summary>Ctrl+S in MC: typed letters jump to the first name starting with them (the folder stays whole); Esc or a
    /// move ends it. Without it, typing filters the folder (the app's own way).</summary>
    public void StartQuickSearch()
    {
        QuickSearching = true; _quickSearch = "";
        _status.Text = "Quick search: type to jump (Esc ends) …";
        FocusList();
    }

    /// <summary>A typed character while quick-searching: true when it was used.</summary>
    public bool QuickSearchKey(string text)
    {
        if (!QuickSearching || text.Length != 1 || char.IsControl(text[0])) return false;
        _quickSearch += text;
        return QuickSearchTo(_quickSearch);
    }

    /// <summary>Selects the first item whose name starts with <paramref name="prefix"/> (then one containing it). True when found.</summary>
    public bool QuickSearchTo(string prefix)
    {
        var items = _list.Items.OfType<FsItem>().Where(i => !i.IsParent).ToList();
        var hit = items.FirstOrDefault(i => i.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) ?? items.FirstOrDefault(i => i.Name.Contains(prefix, StringComparison.OrdinalIgnoreCase));
        _status.Text = hit != null ? $"Quick search: {prefix}" : $"Quick search: {prefix} — no match";
        if (hit == null) return false;
        if (_marking) _cursor = hit; else _list.SelectedItem = hit;
        _list.ScrollIntoView(hit);
        return true;
    }

    public void EndQuickSearch() { if (!QuickSearching) return; QuickSearching = false; _quickSearch = ""; _status.Text = ""; }

    /// <summary>A left click in the list: the keyboard modes end and the list's own selection is the truth again. While
    /// marking (Insert / +, *), the cursor and the marks are kept apart from the ListView's selection; a mouse click
    /// changed that selection without telling the pane, so Current pointed at the old cursor and F3 / F4 / the buttons
    /// acted on the wrong item or on nothing (DarkCipher0x, 0.4.0: "after using the keyboard, then switching back to mouse").</summary>
    internal void MouseTakesOver()
    {
        if (_marking) { _marking = false; _cursor = null; MainWindow.Instance?.Status(""); }
        EndQuickSearch();
    }

    public Task BackForMenuAsync() => BackAsync();

    private UIElement BuildQuickView()
    {
        _quick = new TextBox { IsReadOnly = true, Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.NoWrap, AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, BorderThickness = new Thickness(0) }.Mono();
        _quick.SetResourceReference(BackgroundProperty, "Surface");
        return _quick;
    }

    /// <summary>Quick view (Ctrl+Q): this pane shows the other pane's current file instead of its own listing.</summary>
    public void SetQuickView(bool on)
    {
        if (_quick == null) return;
        _quick.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        _list.Visibility = on ? Visibility.Hidden : Visibility.Visible;
        if (!on) _quick.Text = "";
        // Say what replaced the listing: Ctrl+Q is easy to press by accident and the pane then looks "dead" to clicks.
        _status.Text = on ? "Quick view of the other pane's file — Ctrl+Q (or Left/Right → Quick view) shows the folder again" : "";
    }

    public void ShowQuickText(string text) { if (_quick != null) _quick.Text = text; }

    /// <summary>What quick view shows for an item of this pane: text (64 KB), a hex dump for binaries, an archive's contents.</summary>
    public async Task<string> PreviewAsync(FsItem it, CancellationToken ct = default)
    {
        if (it.IsParent) return "";
        if (_arc is { } arc)
        {
            if (it.IsDir) return string.Join("\n", arc.Children(it.FullPath).Take(500).Select(e => (e.IsDir ? "📁 " : "   ") + e.Path.Split('/')[^1]));
            var b = await Task.Run(() => arc.Read(it.FullPath), ct);
            return Preview(b.Length > 65536 ? b[..65536] : b, b.Length);
        }
        if (it.IsDir || it.IsDrive)
            return $"📁 {it.Name}\n\n{Ref.Title}:{it.FullPath}\n{(it.Modified != default ? "Modified " + it.Modified.ToString("yyyy-MM-dd HH:mm") + "\n" : "")}{(it.Perms.Length > 0 ? "Mode " + it.Perms + "\n" : "")}{(it.DirSize is long d ? "Size " + Fmt.Bytes(d) : "Ctrl+Space measures its size")}";
        if (ArchiveReader.KindOf(it.Name) is { } kind && it.Length <= 64L * 1024 * 1024)
        {
            await using var s = await _fs.OpenReadAsync(it.FullPath, ct);
            using var ms = new MemoryStream(); await s.CopyToAsync(ms, ct);
            var a = await Task.Run(() => new ArchiveReader(ms.ToArray(), kind), ct);
            return $"🗜 {it.Name}: {a.Entries.Count(e => !e.IsDir)} files (Enter opens it)\n\n" + string.Join("\n", a.Entries.Where(e => !e.IsDir).Take(500).Select(e => $"{(e.Unsafe ? "⚠ " : "")}{e.Path}  ({Fmt.Bytes(e.Length)})"));
        }
        await using (var s = await _fs.OpenReadAsync(it.FullPath, ct))
        {
            var buf = new byte[65536];
            int n = 0, r;
            while (n < buf.Length && (r = await s.ReadAsync(buf.AsMemory(n), ct)) > 0) n += r;
            return Preview(buf[..n], it.Length);
        }
    }

    private static string Preview(byte[] head, long total)
    {
        if (!head.Take(8000).Any(b => b == 0)) return Encoding.UTF8.GetString(head) + (total > head.Length ? $"\n\n… (first {Fmt.Bytes(head.Length)} of {Fmt.Bytes(total)})" : "");
        var sb = new StringBuilder($"binary, {Fmt.Bytes(total)} — first 1 KB (Ctrl+F4 opens the hex editor)\n\n");
        for (int o = 0; o < Math.Min(head.Length, 1024); o += 16)
        {
            var row = head.Skip(o).Take(16).ToArray();
            sb.Append($"{o:X8}  {string.Join(" ", row.Select(b => b.ToString("X2"))).PadRight(47)}  ");
            sb.AppendLine(new string(row.Select(b => b is >= 32 and < 127 ? (char)b : '.').ToArray()));
        }
        return sb.ToString();
    }

    // ---- right-click additions ----------------------------------------------------------------------------------------

    private IEnumerable<object?> McMenu(List<FsItem> sel, FsItem? one)
    {
        bool remote = _fs is HostFs;
        if (InArchive)
        {
            yield return Menus.AsyncItem("Copy out to the other pane…", () => _owner.TransferAsync(false, this), "F5", enabled: sel.Count > 0);
            yield return Menus.AsyncItem("Leave the archive", () => GoAsync(_path, record: false));
            yield break;
        }
        if (one != null && !one.IsDir && ArchiveReader.KindOf(one.Name) != null) yield return Menus.AsyncItem("🗜 Open the archive", () => EnterArchiveAsync(one), "Enter");
        yield return Menus.AsyncItem("Folder size", () => MeasureAsync(), "Ctrl+Space", enabled: sel.Any(i => i.IsDir) || Current is { IsDir: true });
        yield return Menus.AsyncItem("New file…", () => NewFileAsync(), "Shift+F4", enabled: _path.Length > 0);
        yield return Menus.AsyncItem("Symbolic link in the other pane…", () => LinkAsync(true), "Ctrl+X S", enabled: one != null);
        yield return Menus.AsyncItem("Hard link in the other pane…", () => LinkAsync(false), "Ctrl+X L", enabled: one is { IsDir: false });
        if (remote) yield return Menus.AsyncItem("chown…", ChownAsync, "Ctrl+X O", enabled: sel.Count > 0);
        yield return Menus.Item("Select group…", () => GroupUi(true), "+");
        yield return Menus.Item("Unselect group…", () => GroupUi(false), "−");
        yield return Menus.Item("Invert marks", Invert, "*");
        yield return Menus.Item("Find file…", FindUi, "Ctrl+Shift+F", enabled: _path.Length > 0);
    }

    internal void FindFromMenu() => FindUi();
    internal void GroupFromMenu(bool select) => GroupUi(select);
    internal void PropertiesOfCurrent() { if (One is { } it) Properties(it); }
    internal Task ChmodMarkedAsync() => ChmodAsync(Marked.Count > 0 ? Marked.ToList() : One is { } o ? [o] : []);
}
