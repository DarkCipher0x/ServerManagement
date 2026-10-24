using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using DcxManager.Files;
using DcxManager.Services;
using DcxManager.Ssh;

namespace DcxManager.Views;

// CommanderView: the Midnight Commander layer (0.3.0 §15) — the F9 menu bar (Left / File / Command / Options / Right), the
// command line under the panes, MC's keys (Ctrl+X chords, Ctrl+\ hotlist, Alt+H history, Ctrl+Space sizes, Ctrl+Q quick
// view, Ctrl+O terminal here, Shift+F4 new file, Ctrl+F2 user menu) and the user menu. Commands only ever run in a visible
// terminal tab, never hidden.
public sealed partial class CommanderView
{
    private Button? _fileMenu;
    private readonly TextBox _cmd = new() { Padding = new Thickness(6, 3, 6, 3) };
    private readonly TextBlock _prompt = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
    private readonly List<string> _cmdHistory = new();
    private int _cmdIndex;
    private bool _ctrlX;
    private CancellationTokenSource? _quickCts;

    internal FilePane LeftForTest => _left;
    internal FilePane RightForTest => _right;

    // ---- menu bar -----------------------------------------------------------------------------------------------------

    private FrameworkElement BuildMenuBar()
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        Button M(string text, Func<IEnumerable<object?>> items)
        {
            var b = new Button { Content = text + " ▾", Style = (Style)FindResource("SmallButton"), Margin = new Thickness(0, 0, 4, 0), ToolTip = "F9 opens the menu bar" };
            Menus.AttachDropDown(b, items);
            bar.Children.Add(b);
            return b;
        }
        M("Left", () => PaneMenu(_left));
        _fileMenu = M("File", FileMenu);
        M("Command", CommandMenu);
        M("Options", OptionsMenu);
        M("Right", () => PaneMenu(_right));
        return bar;
    }

    internal void OpenMenuBar() => _fileMenu?.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    private IEnumerable<object?> PaneMenu(FilePane p)
    {
        string Tick(bool on) => on ? "✓ " : "   ";
        object? Sort(string key, string text) => Menus.Item(Tick(p.SortKey == key) + text, () => p.SetSort(key));
        return new object?[]
        {
            Menus.Sub("Sort order",
                Sort("Name", "Name"), Sort("Ext", "Extension"), Sort("Size", "Size"), Sort("Modified", "Modified time"), Sort("Perms", "Permissions"), Sort("Unsorted", "Unsorted"),
                Menus.Sep(), Menus.Item(Tick(p.SortDescending) + "Reverse", () => p.SetSort(p.SortKey, !p.SortDescending))),
            Menus.Sub("Listing mode", Menus.Item(Tick(!p.Brief) + "Full (name, size, time, permissions)", () => p.SetBrief(false), "Alt+1"), Menus.Item(Tick(p.Brief) + "Brief (names in columns)", () => p.SetBrief(true), "Alt+2")),
            Menus.Item("Quick search (type to jump)", p.StartQuickSearch, "Ctrl+S"),
            Menus.Item("Filter…", p.FocusFilter, "Ctrl+Shift+S"),
            Menus.Item(Tick(p.QuickView) + "Quick view (shows the other pane's file)", () => ToggleQuick(p), "Ctrl+Q"),
            Menus.Item("Info", () => { SetActive(p); p.PropertiesOfCurrent(); }, "Ctrl+X I"),
            Menus.Sep(),
            Menus.Item("Directory history…", p.HistoryMenu, "Alt+H"),
            Menus.AsyncItem("Back (previous folder)", p.BackForMenuAsync, "Alt+Y / Alt+Left"),
            Menus.Item("Directory hotlist…", p.HotlistMenu, "Ctrl+\\"),
            Menus.Item("Find file…", p.FindFromMenu, "Ctrl+Shift+F"),
            p.InArchive || p.Panel != null ? Menus.AsyncItem(p.InArchive ? "Leave the archive" : "Leave the Find results", () => p.GoAsync(p.Path, record: false)) : null,
            Menus.AsyncItem("Rescan", p.RefreshAsync, "Ctrl+R"),
        };
    }

    private IEnumerable<object?> FileMenu() => new object?[]
    {
        Menus.AsyncItem("View", () => _active.ViewAsync(), "F3"),
        Menus.AsyncItem("Edit", () => _active.EditAsync(), "F4"),
        Menus.AsyncItem("Hex editor", () => _active.HexAsync(), "Ctrl+F4"),
        Menus.AsyncItem("New file…", () => _active.NewFileAsync(), "Shift+F4"),
        Menus.Sep(),
        Menus.Group("Copy, move & delete",
            Menus.AsyncItem("Copy to the other pane", () => TransferAsync(false), "F5"),
            Menus.AsyncItem("Move to the other pane", () => TransferAsync(true), "F6"),
            Menus.AsyncItem("Rename…", () => _active.RenameAsync(), "Shift+F6"),
            Menus.AsyncItem("New folder…", () => _active.MkdirAsync(), "F7"),
            Menus.AsyncItem("Delete…", () => _active.DeleteAsync(), "F8")),
        Menus.Group("Permissions & links",
            Menus.AsyncItem("chmod…", () => _active.ChmodMarkedAsync(), "Ctrl+X C"),
            Menus.AsyncItem("chown…", () => _active.ChownAsync(), "Ctrl+X O"),
            Menus.AsyncItem("Symbolic link…", () => _active.LinkAsync(true), "Ctrl+X S"),
            Menus.AsyncItem("Hard link…", () => _active.LinkAsync(false), "Ctrl+X L")),
        Menus.Group("Marks & sizes",
            Menus.Item("Select group…", () => _active.GroupFromMenu(true), "+"),
            Menus.Item("Unselect group…", () => _active.GroupFromMenu(false), "−"),
            Menus.Item("Invert marks", _active.Invert, "*"),
            Menus.AsyncItem("Folder sizes", () => _active.MeasureAsync(), "Ctrl+Space")),
    };

    private IEnumerable<object?> CommandMenu() => new object?[]
    {
        Menus.Item("User menu…", UserMenu, "Ctrl+F2"),
        Menus.Item("Edit the user menu…", EditUserMenu),
        Menus.Item("Find file…", _active.FindFromMenu, "Ctrl+Shift+F"),
        Menus.Item("Directory hotlist…", _active.HotlistMenu, "Ctrl+\\"),
        Menus.Item("Directory history…", _active.HistoryMenu, "Alt+H"),
        Menus.Sep(),
        Menus.Item("Compare / sync folders…", CompareFolders, "Ctrl+X D"),
        Menus.Item("Swap panes", Swap, "Ctrl+U"),
        Menus.Item("Same folder in the other pane", () => _ = Other(_active).OpenAsync(_active.Ref, _active.Path), "Alt+I"),
        Menus.Item("Terminal here", TerminalHere, "Ctrl+O"),
        Menus.Item("Transfer history…", () => Dialogs.ShowText("Transfer history (last 300)", TransferHistory.Text())),
    };

    private IEnumerable<object?> OptionsMenu()
    {
        string Tick(bool on) => on ? "✓ " : "   ";
        var s = AppSettings.Current;
        return new object?[]
        {
            Menus.Item(Tick(s.CommanderShowHidden) + "Show hidden files", ToggleHidden, "Ctrl+H"),
            Menus.Item(Tick(s.EditorVimMode) + "Vim keys in the editor", () => { s.EditorVimMode = !s.EditorVimMode; SaveSettings(); }),
            Menus.Sub("Transfers",
                Menus.Item(Tick(s.TransferVerify) + "Verify every file with SHA-256", () => { s.TransferVerify = !s.TransferVerify; SaveSettings(); }),
                Menus.Item(Tick(s.TransferKeepTimes) + "Keep modified times (and permissions between servers)", () => { s.TransferKeepTimes = !s.TransferKeepTimes; SaveSettings(); }),
                Menus.Item(Tick(s.TransferRetries > 0) + "Retry failing files 3×", () => { s.TransferRetries = s.TransferRetries > 0 ? 0 : 3; SaveSettings(); })),
            Menus.Item("Help: the editor and Vim profiles (Settings → Text editor)…", () => MainWindow.Instance.ShowHelp("Text editor")),
        };
    }

    // ---- keys ---------------------------------------------------------------------------------------------------------

    private async Task<bool> McKeyAsync(KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (_ctrlX)
        {
            _ctrlX = false;
            e.Handled = true;
            switch (key)
            {
                case Key.S: await _active.LinkAsync(true); break;
                case Key.L: await _active.LinkAsync(false); break;
                case Key.O: await _active.ChownAsync(); break;
                case Key.C: await _active.ChmodMarkedAsync(); break;
                case Key.D: CompareFolders(); break;
                case Key.I: _active.PropertiesOfCurrent(); break;
                case Key.Q: ToggleQuick(Other(_active)); break;
                default: MainWindow.Instance?.Status("Ctrl+X then S symlink · L hard link · O chown · C chmod · D compare · I info · Q quick view"); break;
            }
            return true;
        }
        bool H() { e.Handled = true; return true; }
        switch (key)
        {
            case Key.X when mods == ModifierKeys.Control: _ctrlX = true; MainWindow.Instance?.Status("Ctrl+X …"); return H();
            case Key.F1: MainWindow.Instance.ShowHelp("File commander"); return H();
            case Key.F9 or Key.F10 when mods == ModifierKeys.None: OpenMenuBar(); return H();
            case Key.F4 when mods == ModifierKeys.Control: H(); await _active.HexAsync(); return true;
            case Key.F4 when mods == ModifierKeys.Shift: H(); await _active.NewFileAsync(); return true;
            case Key.F6 when mods == ModifierKeys.Shift: H(); await _active.RenameAsync(); return true;
            case Key.F2 when mods == ModifierKeys.Control: UserMenu(); return H();
            case Key.Space when mods == ModifierKeys.Control: H(); await _active.MeasureAsync(); return true;
            case Key.Oem5 or Key.Oem102 when mods == ModifierKeys.Control: _active.HotlistMenu(); return H();   // Ctrl+\ (US and UK layouts)
            case Key.H when mods == ModifierKeys.Alt: _active.HistoryMenu(); return H();
            case Key.F when mods == (ModifierKeys.Control | ModifierKeys.Shift): _active.FindFromMenu(); return H();
            case Key.OemQuestion when mods == (ModifierKeys.Alt | ModifierKeys.Shift): _active.FindFromMenu(); return H();   // Alt+?
            case Key.Q when mods == ModifierKeys.Control: ToggleQuick(Other(_active)); return H();
            case Key.O when mods == ModifierKeys.Control: TerminalHere(); return H();
            case Key.S when mods == ModifierKeys.Control: _active.StartQuickSearch(); return H();                         // MC quick search: type to jump
            case Key.S when mods == (ModifierKeys.Control | ModifierKeys.Shift): _active.FocusFilter(); return H();     // the app's filter
            case Key.Y when mods == ModifierKeys.Alt: H(); await _active.BackForMenuAsync(); return true;                  // MC Alt+Y: directory back
            case Key.D1 when mods == ModifierKeys.Alt: _active.SetBrief(false); return H();
            case Key.D2 when mods == ModifierKeys.Alt: _active.SetBrief(true); return H();
            case Key.I when mods == ModifierKeys.Alt: _ = Other(_active).OpenAsync(_active.Ref, _active.Path); return H();
            case Key.OemPeriod when mods == ModifierKeys.Alt: ToggleHidden(); return H();
            case Key.Enter when mods == ModifierKeys.Control: PutInCommandLine(false); return H();
            case Key.Enter when mods == (ModifierKeys.Control | ModifierKeys.Shift): PutInCommandLine(true); return H();
        }
        return false;
    }

    // ---- quick view ---------------------------------------------------------------------------------------------------

    private void ToggleQuick(FilePane target)
    {
        target.SetQuickView(!target.QuickView);
        if (target.QuickView) { var src = Other(target); SetActive(src); src.FocusList(); SelectionMoved(src); }
    }

    /// <summary>The cursor moved in a pane: quick view in the other pane follows it, and the command line's prompt too.</summary>
    internal void SelectionMoved(FilePane p)
    {
        UpdatePrompt();
        var o = Other(p);
        if (!o.QuickView || p.Current is not { } it) return;
        _quickCts?.Cancel();
        var cts = _quickCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(150, cts.Token);
                var text = await Dispatcher.InvokeAsync(() => p.PreviewAsync(it, cts.Token)).Task.Unwrap();
                if (!cts.IsCancellationRequested) await Dispatcher.InvokeAsync(() => o.ShowQuickText(text));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _ = Dispatcher.InvokeAsync(() => o.ShowQuickText("✕ " + ex.Message)); }
        });
    }

    // ---- command line -------------------------------------------------------------------------------------------------

    private FrameworkElement BuildCommandLine()
    {
        _cmd.Mono(); _prompt.Mono();
        _prompt.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        Hint.Set(_cmd, "Command line — Enter runs it in a terminal tab in this pane's folder (Ctrl+Enter adds the current name)");
        _cmd.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None) { e.Handled = true; RunCommandLine(); }
            else if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { e.Handled = true; PutInCommandLine(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)); }
            else if (e.Key == Key.Escape) { e.Handled = true; _cmd.Clear(); _active.FocusList(); }
            else if (e.Key is Key.Up or Key.Down && _cmdHistory.Count > 0)
            {
                e.Handled = true;
                _cmdIndex = Math.Clamp(_cmdIndex + (e.Key == Key.Up ? -1 : 1), 0, _cmdHistory.Count);
                _cmd.Text = _cmdIndex < _cmdHistory.Count ? _cmdHistory[_cmdIndex] : "";
                _cmd.CaretIndex = _cmd.Text.Length;
            }
        };
        var row = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        DockPanel.SetDock(_prompt, Dock.Left);
        row.Children.Add(_prompt); row.Children.Add(_cmd);
        UpdatePrompt();
        return row;
    }

    private void UpdatePrompt()
    {
        var p = _active;
        var who = p.Ref.Host is { } h ? $"{h.Username}{(h.Username.Length > 0 ? "@" : "")}{h.Display}" : "This PC";
        _prompt.Text = p.InArchive ? $"{who} (archive — read-only)" : $"{who}:{(p.Path.Length > 0 ? p.Path : "~")} {(p.Ref.Host != null ? "$" : ">")}";
    }

    private void PutInCommandLine(bool fullPath)
    {
        if (_active.Current is not { IsParent: false } it) return;
        var v = fullPath ? it.FullPath : it.Name;
        var q = _active.Ref.Host != null ? RemoteExec.Quote(v) : v.Contains(' ') ? "\"" + v + "\"" : v;
        _cmd.Text = (_cmd.Text.Length > 0 && !_cmd.Text.EndsWith(' ') ? _cmd.Text + " " : _cmd.Text) + q + " ";
        _cmd.Focus(); _cmd.CaretIndex = _cmd.Text.Length;
    }

    private void RunCommandLine()
    {
        var text = _cmd.Text.Trim();
        if (text.Length == 0) return;
        var p = _active;
        if (p.InArchive) { Dialogs.Info("Command line", "Leave the archive first: commands run in a real folder."); return; }
        if (text.Contains('\n')) return;
        _cmdHistory.Remove(text); _cmdHistory.Add(text); if (_cmdHistory.Count > 100) _cmdHistory.RemoveAt(0);
        _cmdIndex = _cmdHistory.Count;
        _cmd.Clear();
        if (text is "cd" or "cd ~") { _ = p.GoAsync(""); return; }
        if (text.StartsWith("cd ") && !text.Contains(';') && !text.Contains('&') && !text.Contains('|'))
        {
            // "cd" stays in the pane, like Midnight Commander's command line.
            var arg = text[3..].Trim().Trim('\'', '"');
            var dest = arg == ".." ? FsParent(p) : arg.StartsWith('/') || (arg.Length > 1 && arg[1] == ':') ? arg : (p.Path.TrimEnd('/', '\\') + (p.Ref.Host != null ? "/" : "\\") + arg);
            _ = p.GoAsync(dest);
            return;
        }
        Launch(p, text);
    }

    private static string FsParent(FilePane p) => p.Ref.Host != null ? (p.Path.TrimEnd('/') is var t && t.LastIndexOf('/') > 0 ? t[..t.LastIndexOf('/')] : "/") : System.IO.Path.GetDirectoryName(p.Path.TrimEnd('\\')) ?? p.Path;

    /// <summary>Runs a command in a visible terminal tab in the pane's folder: over SSH on a server, PowerShell on this PC.</summary>
    private static void Launch(FilePane p, string command)
    {
        if (p.Ref.Host is { } h)
        {
            MainWindow.Instance.OpenTerminal(h, p.Path.Length > 0 ? $"cd {RemoteExec.Quote(p.Path)} && {command}" : command);
            AuditLog.Write("you", h.Display, "File commander command", command);
            return;
        }
        if (LocalPowerShell() is not { } ps) { Dialogs.Error("Command line", "No PowerShell found on this PC."); return; }
        // -EncodedCommand: the command reaches PowerShell intact, whatever quotes it contains.
        var enc = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(command));
        MainWindow.Instance.OpenLocal(ps with { CommandLine = ps.CommandLine + " -NoExit -EncodedCommand " + enc, WorkingDir = p.Path.Length > 0 ? p.Path : null });
    }

    private static Sessions.LocalShellProfile? LocalPowerShell() =>
        Sessions.LocalShells.All.FirstOrDefault(x => !x.Wsl && x.CommandLine.Contains("pwsh", StringComparison.OrdinalIgnoreCase))
        ?? Sessions.LocalShells.All.FirstOrDefault(x => !x.Wsl && x.CommandLine.Contains("powershell", StringComparison.OrdinalIgnoreCase));

    private void TerminalHere()
    {
        var p = _active;
        if (p.Ref.Host is { } h) { MainWindow.Instance.OpenTerminal(h, p.Path.Length > 0 ? "cd " + RemoteExec.Quote(p.Path) : null); return; }
        if (Sessions.LocalShells.All.FirstOrDefault(x => !x.Wsl) is { } sh) MainWindow.Instance.OpenLocal(sh with { WorkingDir = p.Path.Length > 0 ? p.Path : null });
    }

    // ---- user menu ----------------------------------------------------------------------------------------------------

    private static List<UserMenuEntry> UserMenuItems => AppData.Current.CommanderUserMenu ?? UserMenuEntry.Defaults();

    private void UserMenu()
    {
        var p = _active;
        var cur = p.Current is { IsParent: false } c ? c : null;
        var items = new List<object?>();
        if (p.Ref.Host == null || p.InArchive)
            items.Add(Menus.Item("The user menu runs commands on a server — open one in this pane", () => { }, enabled: false));
        else
            foreach (var u in UserMenuItems.Where(u => u.For == "any" || u.For == "file" && cur is { IsDir: false } || u.For == "dir" && cur is { IsDir: true }))
                items.Add(Menus.Item(u.Title, () => RunUserCommand(u)));
        items.Add(Menus.Sep());
        items.Add(Menus.Item("Edit the user menu…", EditUserMenu));
        Menus.ShowAt(this, items);
    }

    private void RunUserCommand(UserMenuEntry u)
    {
        var p = _active; var o = Other(p);
        var cmd = McTools.Expand(u.Command, p.Path, p.Current is { IsParent: false } c ? c : null, p.Marked, o.Path, o.Current is { IsParent: false } oc ? oc : null, out var why);
        if (cmd == null) { Dialogs.Info(u.Title, "Can't run it here: " + why + "."); return; }
        if (!Envs.Confirm(p.Ref.Host, u.Title, $"Run on {p.Ref.Title} in {p.Path}:\n\n{cmd}", "Run in a terminal", danger: false)) return;
        Launch(p, cmd);
    }

    private void EditUserMenu() =>
        TextEditorWindow.Open("File commander user menu", McTools.FormatUserMenu(UserMenuItems), text =>
        {
            var list = McTools.ParseUserMenu(text, out var errors);
            if (errors.Count > 0) throw new InvalidOperationException("Not saved:\n" + string.Join("\n", errors.Take(10)));
            AppData.Current.CommanderUserMenu = list;
            AppData.Save();
            return Task.CompletedTask;
        });
}
