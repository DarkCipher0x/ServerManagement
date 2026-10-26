using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DcxManager.Services;
using DcxManager.Views;

namespace DcxManager;

/// <summary>
/// The command palette (Ctrl+Shift+P, 0.6.29, ADR-104): one box for hosts (connect; Shift+Enter for files, manage,
/// edit), open tabs, workspaces, snippets (sent to the active terminal), every page, every settings card and the app's
/// actions. Nothing runs without Enter or a click; locks, restrictions and confirmations are those of what it opens.
/// </summary>
public static class CommandPalette
{
    /// <summary>Everything the palette can find now (the self-test searches this too).</summary>
    internal static List<PaletteSearch.Item> Items(MainWindow main)
    {
        var list = new List<PaletteSearch.Item>(AppData.Current.Hosts.Count + 200);
        foreach (var h in AppData.Current.Hosts)
        {
            var host = h;
            var proto = h.Protocol == "rdp" ? "RDP" : h.IsVnc ? "VNC" : h.IsTelnet ? "Telnet" : "SSH";
            list.Add(new PaletteSearch.Item("host:" + h.Id, "Host", h.Display, $"{proto} · {h.Username}@{h.Address} · {h.Group}{(h.Tags.Length > 0 ? " · " + h.Tags : "")}{(h.Environment.Length > 0 ? " · " + h.Environment : "")}",
                () => main.OpenTerminal(host), "⌨")
            {
                More = () => HostMore(main, host),
            });
        }
        foreach (var t in main.Panes.AllTabs)
        {
            var tab = t;
            list.Add(new PaletteSearch.Item("tab:" + (t.Content?.GetHashCode() ?? 0), "Tab", t.Header as string ?? "tab", "open tab · switch to it", () => main.Panes.Select(tab), "▭"));
        }
        foreach (var w in Workspace.AllNamed())
        {
            var name = w.Name;
            list.Add(new PaletteSearch.Item("workspace:" + name, "Workspace", name, $"workspace · {w.Entries.Count} tab(s) · opens beside the open tabs", () => main.OpenNamedWorkspace(name, connect: true), "▦")
            {
                More = () => new (string, Action)[] { ("Open disconnected", () => main.OpenNamedWorkspace(name, connect: false)) },
            });
        }
        foreach (var s in AppData.Current.Snippets)
        {
            var snip = s;
            list.Add(new PaletteSearch.Item("snippet:" + s.Id, "Snippet", s.Name, (s.Sudo ? "sudo " : "") + s.Command, () => SendSnippet(main, snip), "❯"));
        }
        foreach (var n in main.NavEntries)
        {
            var key = n.Key;
            list.Add(new PaletteSearch.Item("page:" + key, "Page", n.Title, n.Tip ?? "page", () => main.Navigate(key), n.Glyph));
        }
        foreach (var (title, category) in SettingsView.CardTitles)
        {
            var card = title;
            list.Add(new PaletteSearch.Item("setting:" + title, "Setting", title, "Settings → " + category, () => { main.Navigate("settings"); SettingsView.Current?.RevealCard(card); }, "⚙"));
        }
        void A(string id, string title, string detail, Action run, string glyph = "▸") => list.Add(new PaletteSearch.Item("action:" + id, "Action", title, detail, run, glyph));
        A("quick", "Quick connect…", "Ctrl+K · type user@host, an address, rdp://…", main.FocusQuickConnect, "⚡");
        A("lock", "Lock the app", "Ctrl+Shift+L", main.LockNow, "🔒");
        A("tile", "Tile all tabs", "Ctrl+Alt+T", main.Panes.Tile, "▦");
        A("merge", "Merge all panes", "Ctrl+Alt+U", main.Panes.MergeAll, "▭");
        A("window", "New window", "Ctrl+Shift+N · another window with a host pane", () => main.Panes.NewWindow(), "⧉");
        A("sidebar", "Show or hide the sidebar", "Ctrl+B", main.ToggleSidebar, "☰");
        A("screenshot", "Screenshot the active tab", "Ctrl+Shift+S", () => _ = main.ScreenshotActiveAsync(), "📷");
        A("saveworkspace", "Save the open tabs as a workspace…", "named workspace", main.SaveNamedWorkspace, "▦");
        A("help", "Help", "F1", () => main.ShowHelp(), "?");
        return list;
    }

    private static IEnumerable<(string, Action)> HostMore(MainWindow main, HostEntry h)
    {
        yield return ("Connect", () => main.OpenTerminal(h));
        if (h.IsSsh) { yield return ("Files (SFTP)", () => main.OpenFiles(h)); yield return ("Manage", () => main.OpenServer(h)); yield return ("New terminal (another tab)", () => main.OpenTerminal(h, forceNew: true)); }
        yield return ("Copy address", () => Menus.Clip(h.Address));
        yield return ("Show in Hosts", () => main.Navigate("hosts"));
    }

    private static void SendSnippet(MainWindow main, Snippet s)
    {
        if (main.Panes.ActiveTab?.Content is TerminalView t && t.IsConnected) _ = t.SendSnippetAsync(s, new[] { t });
        else { main.Navigate("snippets"); main.Status($"Snippet “{s.Name}”: open a connected terminal first, or run it on hosts here."); }
    }

    private static Window? _open;

    public static void Show(MainWindow main)
    {
        if (TestMode.Running) { Dialogs.RefusedInSelfTest("command palette"); return; }
        if (_open != null) { _open.Activate(); return; }
        var items = Items(main);
        var recent = AppSettings.Current.PaletteLearn ? AppSettings.Current.PaletteRecent : new List<string>();
        var w = Dialogs.MakeWindow("Command palette", 700, 500);
        w.Owner = Window.GetWindow(main.Panes.ActiveTab) ?? main;
        var box = Hint.Set(new TextBox { Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(12, 12, 12, 6) }, "Hosts, tabs, workspaces, snippets, pages, settings, actions…");
        var list = new ListBox { Margin = new Thickness(12, 0, 12, 6), BorderThickness = new Thickness(0) };
        var hint = new TextBlock { Text = "Enter: open · Shift+Enter or right-click: more · Esc: close", Margin = new Thickness(14, 0, 12, 10) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var root = new DockPanel();
        DockPanel.SetDock(box, Dock.Top); DockPanel.SetDock(hint, Dock.Bottom);
        root.Children.Add(box); root.Children.Add(hint); root.Children.Add(list);
        w.Content = root;

        void Refresh()
        {
            list.Items.Clear();
            foreach (var it in PaletteSearch.Search(items, box.Text, recent)) list.Items.Add(Row(it));
            if (list.Items.Count > 0) list.SelectedIndex = 0;
        }
        void Run(PaletteSearch.Item it, Action? action = null)
        {
            if (AppSettings.Current.PaletteLearn) { PaletteSearch.Remember(AppSettings.Current.PaletteRecent, it.Key); try { AppSettings.Save(); } catch { } }
            w.Close();
            try { (action ?? it.Run)(); } catch (Exception ex) { App.Log("Command palette", ex); Dialogs.Error("Command palette", ex.Message); }
        }
        void More(PaletteSearch.Item it)
        {
            if (it.More == null) return;
            Menus.ShowAt(list, it.More().Select(m => (object?)Menus.Item(m.Title, () => Run(it, m.Run))).ToList());
        }
        PaletteSearch.Item? Sel() => (list.SelectedItem as ListBoxItem)?.Tag as PaletteSearch.Item;
        box.TextChanged += (_, _) => Refresh();
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Down) { list.SelectedIndex = Math.Min(list.Items.Count - 1, list.SelectedIndex + 1); list.ScrollIntoView(list.SelectedItem); e.Handled = true; }
            else if (e.Key == Key.Up) { list.SelectedIndex = Math.Max(0, list.SelectedIndex - 1); list.ScrollIntoView(list.SelectedItem); e.Handled = true; }
            else if (e.Key == Key.Enter && Sel() is { } it) { if (Keyboard.Modifiers == ModifierKeys.Shift) More(it); else Run(it); e.Handled = true; }
        };
        list.MouseDoubleClick += (_, _) => { if (Sel() is { } it) Run(it); };
        list.MouseRightButtonUp += (_, e) => { if (Sel() is { } it) { More(it); e.Handled = true; } };
        w.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { w.Close(); e.Handled = true; } };
        w.Closed += (_, _) => _open = null;
        w.Loaded += (_, _) => box.Focus();
        _open = w;
        Refresh();
        w.Show();
    }

    private static ListBoxItem Row(PaletteSearch.Item it)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var glyph = new TextBlock { Text = it.Glyph, VerticalAlignment = VerticalAlignment.Center };
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = it.Title, TextTrimming = TextTrimming.CharacterEllipsis });
        var detail = new TextBlock { Text = it.Detail, TextTrimming = TextTrimming.CharacterEllipsis };
        detail.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        text.Children.Add(detail);
        var kind = new TextBlock { Text = it.Kind, Margin = new Thickness(12, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        kind.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        Grid.SetColumn(text, 1); Grid.SetColumn(kind, 2);
        g.Children.Add(glyph); g.Children.Add(text); g.Children.Add(kind);
        return new ListBoxItem { Content = g, Tag = it, Padding = new Thickness(6, 4, 6, 4) };
    }
}
