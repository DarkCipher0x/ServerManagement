using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using DcxManager.Files;
using DcxManager.Services;
using DcxManager.Ssh;
using Microsoft.Win32;
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace DcxManager.Views;

public sealed class RemoteItem
{
    public required string Name { get; init; }
    public required string FullName { get; init; }
    public bool IsDir { get; init; }
    public bool IsLink { get; init; }
    public long Length { get; init; }
    public DateTime Modified { get; init; }
    public string Perms { get; init; } = "";
    public string Owner { get; init; } = "";
    public string Icon => IsDir ? "📁" : IsLink ? "↪" : "📄";
    public string SizeText => IsDir ? "" : Fmt.Bytes(Length);
    public string ModifiedText => Modified.ToString("yyyy-MM-dd HH:mm");
}

/// <summary>SFTP file manager: browse, upload (incl. drag &amp; drop), download, edit text files, rename, chmod, delete.</summary>
public sealed class SftpView : UserControl, IDisposable, IHostView
{
    private readonly HostEntry _host;
    public HostEntry Host => _host;
    private SshSession<SftpClient>? _sftp;
    public bool IsConnected => _sftp is { IsConnected: true };
    private readonly TextBox _path = new() { Padding = new Thickness(6, 4, 6, 4) };
    private readonly ListView _list = new() { SelectionMode = SelectionMode.Extended, AllowDrop = true };
    private readonly TextBlock _status = new TextBlock() { VerticalAlignment = VerticalAlignment.Center }.Fs(FontStep.Small);
    private readonly ProgressBar _progress = new() { Height = 4, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
    private readonly CheckBox _hidden = new() { Content = "hidden", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    private string _cwd;

    public SftpView(HostEntry host, string? path)
    {
        _host = host;
        _cwd = path ?? "";
        _path.Mono();
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");

        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        var env = EnvUi.Strip(() => _host, localWhenNoHost: false);   // ADR-017
        env.Margin = new Thickness(0, 0, 8, 4);
        bar.Children.Add(env);
        void B(string t, RoutedEventHandler h, string? tip = null) { var b = new Button { Content = t, Style = (Style)FindResource("SmallButton"), ToolTip = tip, Margin = new Thickness(0, 0, 4, 4) }; b.Click += h; bar.Children.Add(b); }
        B("↑ Up", async (_, _) => await GoAsync(ParentDir(_cwd)));
        B("⌂ Home", async (_, _) => await GoAsync(""));
        B("↻", async (_, _) => await GoAsync(_cwd), "Refresh");
        B("⇧ Upload…", async (_, _) => await UploadDialogAsync(), "Upload files (or drag them from Explorer)");
        B("⇩ Download…", async (_, _) => await DownloadAsync());
        B("✎ Edit", async (_, _) => await EditAsync(), "Open a text file in the editor");
        B("⌨ Terminal here", (_, _) => MainWindow.Instance.OpenTerminal(_host, "cd " + RemoteExec.Quote(_cwd)));
        B("✕ Delete", async (_, _) => await DeleteAsync());
        bar.Children.Add(Menus.MoreButton(() => new object?[]
        {
            Menus.AsyncItem("＋ New folder…", MkdirAsync),
            Menus.AsyncItem("＋ New file…", NewFileAsync),
            Menus.AsyncItem("Rename…", RenameAsync, enabled: Selected.Any()),
            Menus.AsyncItem("chmod…", ChmodAsync, enabled: Selected.Any()),
            Menus.Sep(),
            Menus.Item("⧉ Copy path", () => { if (Selected.FirstOrDefault() is { } s) Clipboard.SetText(s.FullName); else Clipboard.SetText(_cwd); }, tip: "The selected item's full path, or the current folder"),
            Menus.Item("⇆ Open in File commander", () => MainWindow.Instance.OpenCommander(_host, _cwd), tip: "Two panes: this PC on the left, this folder on the right"),
        }, style: "SmallButton"));
        bar.Children.Add(_hidden);
        _hidden.Checked += async (_, _) => await GoAsync(_cwd); _hidden.Unchecked += async (_, _) => await GoAsync(_cwd);

        var pathRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var go = new Button { Content = "Go", Style = (Style)FindResource("SmallButton"), Margin = new Thickness(6, 0, 0, 0) };
        go.Click += async (_, _) => await GoAsync(_path.Text.Trim());
        DockPanel.SetDock(go, Dock.Right);
        pathRow.Children.Add(go);
        pathRow.Children.Add(_path);
        _path.KeyDown += async (_, e) => { if (e.Key == Key.Enter) await GoAsync(_path.Text.Trim()); };

        var gv = new GridView();
        void Col(string h, string p, double w) => gv.Columns.Add(new GridViewColumn { Header = h, Width = w, DisplayMemberBinding = new Binding(p) });
        Col("", "Icon", 30); Col("Name", "Name", 320); Col("Size", "SizeText", 90); Col("Modified", "ModifiedText", 140); Col("Permissions", "Perms", 100); Col("Owner", "Owner", 100);
        _list.View = gv;
        _list.MouseDoubleClick += async (_, _) => await OpenSelectedAsync();
        _list.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter) await OpenSelectedAsync();
            else if (e.Key == Key.Back) await GoAsync(ParentDir(_cwd));
            else if (e.Key == Key.Delete) await DeleteAsync();
            else if (e.Key == Key.F5) await GoAsync(_cwd);
        };
        Menus.ForList(_list, this, () =>
        {
            var sel = Selected.ToList();
            var one = sel.Count == 1 ? sel[0] : null;
            var files = sel.Where(i => !i.IsDir).ToList();
            long total = files.Sum(f => f.Length);
            return new object?[]
            {
                one is { IsDir: true } ? Menus.Item("Open folder", () => _ = GoAsync(one.FullName)) : null,
                one is { IsDir: false } ? Menus.Item("✎ Open in editor", () => _ = EditAsync()) : null,
                one is { IsDir: true } ? Menus.Item("⌨ Terminal in this folder", () => MainWindow.Instance.OpenTerminal(_host, "cd " + RemoteExec.Quote(one.FullName))) : null,
                Menus.Item("Copy name", () => Menus.Clip(string.Join("\n", sel.Select(i => i.Name))), enabled: sel.Count > 0),
                Menus.Item("Copy scp command", () => Menus.Clip(string.Join("\n", sel.Select(i => $"scp {(i.IsDir ? "-r " : "")}{(_host.Port != 22 ? $"-P {_host.Port} " : "")}{_host.Username}@{_host.Address}:{RemoteExec.Quote(i.FullName)} ."))), enabled: sel.Count > 0),
                Menus.Item(files.Count == 1 ? "Share file…" : $"Share {files.Count} files…", () => _ = ShareAsync(files),
                    enabled: ShareService.Supported && files.Count > 0 && total <= 200L * 1024 * 1024,
                    tip: total > 200L * 1024 * 1024 ? "Up to 200 MB in total — download larger files instead" : "Downloads a copy to a private temp folder and opens the Windows Share sheet"),
            };
        });
        _list.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        _list.Drop += async (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] files) await UploadAsync(files); };

        var bottom = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        bottom.Children.Add(_status);
        bottom.Children.Add(_progress);

        var root = new DockPanel { Margin = new Thickness(14, 10, 14, 10) };
        DockPanel.SetDock(bar, Dock.Top); DockPanel.SetDock(pathRow, Dock.Top); DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bar); root.Children.Add(pathRow); root.Children.Add(bottom); root.Children.Add(_list);
        Content = root;
        Loaded += async (_, _) => { if (_sftp == null) await GoAsync(_cwd); };
    }

    private IEnumerable<RemoteItem> Selected => _list.SelectedItems.OfType<RemoteItem>();

    private async Task<SftpClient> ClientAsync()
    {
        if (_sftp is { IsConnected: true }) return _sftp.Client;
        _sftp?.Dispose();
        _status.Text = "connecting…";
        _sftp = await SshConnector.ConnectSftpAsync(_host);
        LiveSessions.Touch();
        return _sftp.Client;
    }

    private static string ParentDir(string p)
    {
        if (p is "" or "/") return "/";
        var t = p.TrimEnd('/');
        var i = t.LastIndexOf('/');
        return i <= 0 ? "/" : t[..i];
    }

    private static string Join(string dir, string name) => dir.EndsWith('/') ? dir + name : dir + "/" + name;

    private async Task GoAsync(string path)
    {
        try
        {
            var c = await ClientAsync();
            if (path.Length == 0) path = c.WorkingDirectory;
            var items = new List<RemoteItem>();
            await foreach (var f in c.ListDirectoryAsync(path, CancellationToken.None))
            {
                if (f.Name is "." or "..") continue;
                if (!_hidden.IsChecked.GetValueOrDefault() && f.Name.StartsWith('.')) continue;
                items.Add(new RemoteItem
                {
                    Name = f.Name, FullName = f.FullName, IsDir = f.IsDirectory, IsLink = f.IsSymbolicLink, Length = f.Length,
                    Modified = f.LastWriteTime, Perms = PermString(f.Attributes), Owner = $"{f.UserId}:{f.GroupId}",
                });
            }
            _cwd = path;
            _path.Text = path;
            _list.ItemsSource = items.OrderByDescending(i => i.IsDir).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
            _status.Text = $"{items.Count(i => i.IsDir)} folders, {items.Count(i => !i.IsDir)} files · {Fmt.Bytes(items.Where(i => !i.IsDir).Sum(i => i.Length))}";
            MainWindow.Instance.SetTabHeader(this, $"⇅ {_host.Display}:{(path.Length > 28 ? "…" + path[^27..] : path)}");
        }
        catch (Exception ex) { _status.Text = "✕ " + ex.Message.Split('\n')[0]; }
    }

    private static string PermString(SftpFileAttributes a)
    {
        var sb = new StringBuilder(a.IsDirectory ? "d" : a.IsSymbolicLink ? "l" : "-");
        sb.Append(a.OwnerCanRead ? 'r' : '-').Append(a.OwnerCanWrite ? 'w' : '-').Append(a.OwnerCanExecute ? 'x' : '-');
        sb.Append(a.GroupCanRead ? 'r' : '-').Append(a.GroupCanWrite ? 'w' : '-').Append(a.GroupCanExecute ? 'x' : '-');
        sb.Append(a.OthersCanRead ? 'r' : '-').Append(a.OthersCanWrite ? 'w' : '-').Append(a.OthersCanExecute ? 'x' : '-');
        return sb.ToString();
    }

    private async Task OpenSelectedAsync()
    {
        if (Selected.FirstOrDefault() is not { } item) return;
        if (item.IsDir || item.IsLink && await IsDirAsync(item.FullName)) await GoAsync(item.FullName);
        else await EditAsync();
    }

    private async Task<bool> IsDirAsync(string path)
    {
        try { var c = await ClientAsync(); return await Task.Run(() => c.GetAttributes(path).IsDirectory); } catch { return false; }
    }

    private void Progress(double? value)
    {
        Dispatcher.Invoke(() =>
        {
            _progress.Visibility = value == null ? Visibility.Collapsed : Visibility.Visible;
            if (value != null) _progress.Value = value.Value;
        });
    }

    private async Task UploadDialogAsync()
    {
        var dlg = new OpenFileDialog { Multiselect = true, Title = $"Upload to {_host.Display}:{_cwd}" };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true) await UploadAsync(dlg.FileNames);
    }

    private async Task UploadAsync(string[] paths)
    {
        try
        {
            var c = await ClientAsync();
            var files = new List<(string Local, string Remote)>();
            var dirs = new List<string>();
            foreach (var p in paths)
            {
                if (Directory.Exists(p))
                {
                    var baseRemote = Join(_cwd, Path.GetFileName(p));
                    dirs.Add(baseRemote);
                    foreach (var d in Directory.GetDirectories(p, "*", SearchOption.AllDirectories)) dirs.Add(Join(baseRemote, Path.GetRelativePath(p, d).Replace('\\', '/')));
                    foreach (var f in Directory.GetFiles(p, "*", SearchOption.AllDirectories)) files.Add((f, Join(baseRemote, Path.GetRelativePath(p, f).Replace('\\', '/'))));
                }
                else if (File.Exists(p)) files.Add((p, Join(_cwd, Path.GetFileName(p))));
            }
            long total = Math.Max(1, files.Sum(f => new FileInfo(f.Local).Length)), done = 0;
            await Task.Run(() =>
            {
                foreach (var d in dirs) if (!c.Exists(d)) c.CreateDirectory(d);
                foreach (var (local, remote) in files)
                {
                    Dispatcher.Invoke(() => _status.Text = $"uploading {Path.GetFileName(local)}…");
                    using var fs = File.OpenRead(local);
                    long start = done;
                    c.UploadFile(fs, remote, true, n => Progress((start + (long)n) * 100.0 / total));
                    done += fs.Length;
                }
            });
            AuditLog.Write("you", _host.Display, $"uploaded {files.Count} file(s) to {_cwd}");
        }
        catch (Exception ex) { Dialogs.Error("Upload", ex.Message); }
        finally { Progress(null); await GoAsync(_cwd); }
    }

    private async Task DownloadAsync()
    {
        var items = Selected.ToList();
        if (items.Count == 0) return;
        try
        {
            var c = await ClientAsync();
            if (items.Count == 1 && !items[0].IsDir)
            {
                var dlg = new SaveFileDialog { FileName = items[0].Name };
                if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
                await Task.Run(() =>
                {
                    using var fs = File.Create(dlg.FileName);
                    c.DownloadFile(items[0].FullName, fs, n => Progress(n * 100.0 / Math.Max(1, items[0].Length)));
                });
            }
            else
            {
                var dlg = new OpenFolderDialog { Title = "Download to folder" };
                if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
                await Task.Run(() => { foreach (var i in items) DownloadTree(c, i.FullName, i.IsDir, Path.Combine(dlg.FolderName, LocalFs.SafeChild(i.Name))); });
            }
            _status.Text = "✓ downloaded";
        }
        catch (Exception ex) { Dialogs.Error("Download", ex.Message); }
        finally { Progress(null); }
    }

    /// <summary>Downloads the files to the share temp folder (wiped at exit) and opens the Share sheet with them.</summary>
    private async Task ShareAsync(List<RemoteItem> files)
    {
        if (files.Count == 0) return;
        try
        {
            var c = await ClientAsync();
            var dir = Path.Combine(ShareService.TempDir, Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            var local = new List<string>();
            await Task.Run(() =>
            {
                foreach (var f in files)
                {
                    var p = Path.Combine(dir, LocalFs.SafeChild(f.Name));
                    DownloadTree(c, f.FullName, false, p);
                    local.Add(p);
                }
            });
            _status.Text = $"✓ {files.Count} file(s) ready to share";
            ShareService.ShareFiles(Window.GetWindow(this), files.Count == 1 ? $"{files[0].Name} — {_host.Display}" : $"{files.Count} files — {_host.Display}", local.ToArray());
        }
        catch (Exception ex) { Dialogs.Error("Share", ex.Message); }
        finally { Progress(null); }
    }

    private void DownloadTree(SftpClient c, string remote, bool isDir, string local)
    {
        if (!isDir)
        {
            Dispatcher.Invoke(() => _status.Text = "downloading " + remote);
            using var fs = File.Create(local);
            c.DownloadFile(remote, fs);
            return;
        }
        Directory.CreateDirectory(local);
        foreach (var f in c.ListDirectory(remote))
        {
            if (f.Name is "." or "..") continue;
            if (f.IsSymbolicLink) continue;
            // The server names the entry: one path segment only, so a tree can't write outside the chosen folder.
            DownloadTree(c, f.FullName, f.IsDirectory, Path.Combine(local, LocalFs.SafeChild(f.Name)));
        }
    }

    private async Task EditAsync()
    {
        if (Selected.FirstOrDefault() is not { IsDir: false } item) return;
        if (item.Length > 4 * 1024 * 1024) { Dialogs.Info("Edit", "That file is over 4 MB; download it instead."); return; }
        try
        {
            var c = await ClientAsync();
            var bytes = await Task.Run(() => { using var ms = new MemoryStream(); c.DownloadFile(item.FullName, ms); return ms.ToArray(); });
            if (bytes.Take(8000).Any(b => b == 0))
            {
                // Binary: the hex editor instead (0.3.0 §13), in memory only.
                if (!Dialogs.Confirm("Binary file", $"{item.Name} looks like a binary file. Open it in the hex editor?", "Open in hex editor")) return;
                HexEditorWindow.Open($"{_host.Display}:{item.FullName}", bytes, async data =>
                {
                    var cl = await ClientAsync();
                    await Task.Run(() => { using var ms = new MemoryStream(data); cl.UploadFile(ms, item.FullName, true); });
                    AuditLog.Write("you", _host.Display, "edited (hex) " + item.FullName);
                }, async () => { try { var cl = await ClientAsync(); return await Task.Run(() => { using var ms = new MemoryStream(); cl.DownloadFile(item.FullName, ms); return ms.ToArray(); }); } catch { return null; } });
                return;
            }
            TextEditorWindow.Open($"{_host.Display}:{item.FullName}", Encoding.UTF8.GetString(bytes), async text =>
            {
                var cl = await ClientAsync();
                await Task.Run(() => { using var ms = new MemoryStream(new UTF8Encoding(false).GetBytes(text)); cl.UploadFile(ms, item.FullName, true); });
                AuditLog.Write("you", _host.Display, "edited " + item.FullName);
            }, _host.IsAdHoc ? null : FsRef.ForHost(_host), item.FullName);
        }
        catch (Exception ex) { Dialogs.Error("Edit", ex.Message + "\n\nIf it's a root-owned file, edit it from a terminal with sudoedit."); }
    }

    private async Task MkdirAsync()
    {
        if (Dialogs.Prompt("New folder", $"Folder name in {_cwd}:") is not { Length: > 0 } name) return;
        try { var c = await ClientAsync(); await c.CreateDirectoryAsync(Join(_cwd, name)); }
        catch (Exception ex) { Dialogs.Error("New folder", ex.Message); }
        await GoAsync(_cwd);
    }

    private async Task NewFileAsync()
    {
        if (Dialogs.Prompt("New file", $"File name in {_cwd}:") is not { Length: > 0 } name) return;
        try
        {
            var c = await ClientAsync();
            var path = Join(_cwd, name);
            await Task.Run(() => { using var ms = new MemoryStream(); c.UploadFile(ms, path, false); });
        }
        catch (Exception ex) { Dialogs.Error("New file", ex.Message); }
        await GoAsync(_cwd);
    }

    private async Task RenameAsync()
    {
        if (Selected.FirstOrDefault() is not { } item) return;
        if (Dialogs.Prompt("Rename", "New name (or a full path to move it):", item.Name) is not { Length: > 0 } name || name == item.Name) return;
        try { var c = await ClientAsync(); await c.RenameFileAsync(item.FullName, name.StartsWith('/') ? name : Join(_cwd, name), CancellationToken.None); }
        catch (Exception ex) { Dialogs.Error("Rename", ex.Message); }
        await GoAsync(_cwd);
    }

    private async Task ChmodAsync()
    {
        var items = Selected.ToList();
        if (items.Count == 0) return;
        if (Dialogs.Prompt("chmod", "Octal mode, e.g. 644, 755, 600:", "644") is not { Length: 3 or 4 } mode) return;
        try
        {
            var digits = mode.Length == 4 ? mode[1..] : mode;   // SSH.NET takes rwx digits as written (e.g. 755); special bits need a terminal
            if (!digits.All(ch => ch is >= '0' and <= '7')) { Dialogs.Error("chmod", "Use octal digits 0-7, e.g. 644."); return; }
            var c = await ClientAsync();
            await Task.Run(() => { foreach (var i in items) c.ChangePermissions(i.FullName, short.Parse(digits)); });
        }
        catch (Exception ex) { Dialogs.Error("chmod", ex.Message); }
        await GoAsync(_cwd);
    }

    private async Task DeleteAsync()
    {
        var items = Selected.ToList();
        if (items.Count == 0) return;
        var dirs = items.Count(i => i.IsDir);
        if (!Envs.Confirm(_host, "Delete", $"Permanently delete {items.Count} item(s) on {_host.Display}?\n\n{string.Join("\n", items.Take(12).Select(i => i.FullName))}{(items.Count > 12 ? "\n…" : "")}" +
            (dirs > 0 ? $"\n\n{dirs} folder(s) will be deleted with everything inside." : ""), "Delete", danger: true, typeToConfirm: dirs > 0 ? "delete" : null)) return;
        try
        {
            var c = await ClientAsync();
            foreach (var i in items)
            {
                if (i.IsDir) { var r = await SshPool.RunAsync(_host, "rm -rf -- " + RemoteExec.Quote(i.FullName)); if (!r.Ok) throw new InvalidOperationException(r.Combined); }
                else await c.DeleteAsync(i.FullName, CancellationToken.None);
            }
            AuditLog.Write("you", _host.Display, $"deleted {string.Join(", ", items.Select(i => i.FullName))}");
        }
        catch (Exception ex) { Dialogs.Error("Delete", ex.Message); }
        await GoAsync(_cwd);
    }

    public void Dispose() => _sftp?.Dispose();
}
