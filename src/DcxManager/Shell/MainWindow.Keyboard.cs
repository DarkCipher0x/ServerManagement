using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DcxManager.Services;
using DcxManager.Services.Keys;
using DcxManager.Views;

namespace DcxManager;

/// <summary>
/// The main window's keyboard commands (0.6.36, ADR-115). Each is registered once with its default keys in
/// <see cref="KeyCommands"/>; Settings → Keyboard changes the keys, and every key press of this window and its pop-out
/// windows goes through <see cref="HandleShortcut"/>. A command runs exactly what its menu item or button runs, with the
/// same locks and confirmations.
/// </summary>
public sealed partial class MainWindow
{
    private void OnPreviewKey(object sender, KeyEventArgs e) => HandleShortcut(e);

    /// <summary>
    /// App shortcuts. Inside a terminal or remote desktop, plain Ctrl+letter, Alt+letter and F-keys belong to the remote
    /// program (Ctrl+W deletes a word, Ctrl+B is the tmux prefix, F1 is htop's help), so there only Ctrl+Shift / Ctrl+Alt /
    /// Ctrl+Tab / Ctrl+F-key combinations are taken (<see cref="KeyChord.WorksInRemoteSessions"/>).
    /// </summary>
    public void HandleShortcut(KeyEventArgs e)
    {
        if (e.Handled) return;
        if (KeyCommands.TryRun(e, RemoteKeyboard.HasKeyboard(e))) e.Handled = true;
    }

    /// <summary>"Clear all history…" from the palette or a key: asks, then clears every history (ADR-116).</summary>
    private void ClearAllHistoryAsked()
    {
        int n = ActivityHistory.Total;
        if (n == 0 && !ActivityHistory.Saving) { Status("No history is kept (History is off)."); return; }
        if (!Dialogs.Confirm("Clear history", $"Clear every history the app keeps{(n > 0 ? $" ({n} entries)" : "")}?\n\nPinned lines, saved hosts and portals, notes, reports and the audit log stay.", "Clear", danger: true)) return;
        ActivityHistory.ClearAll("you", "all history cleared");
        Status("History cleared.");
    }

    /// <summary>The app's own commands with their default keys (the AI module adds its own; pages follow the sidebar).</summary>
    private void RegisterCommands()
    {
        const string General = "General", Moving = "Moving around", Tabs = "Tabs", PanesWindows = "Panes and windows";
        void C(string id, string title, string category, Action run, Func<bool>? canRun, params string[] keys) =>
            KeyCommands.Register(new KeyCommand(id, title, category, KeyScopes.App, KeyCommands.Keys(keys)) { Run = run, CanRun = canRun });

        C("app.palette", "Command palette", General, () => CommandPalette.Show(this), null, "Ctrl+Shift+P");
        C("app.quick-connect", "Quick connect", General, FocusQuickConnect, null, "Ctrl+K", "Ctrl+Shift+K");
        C("app.help", "Help for this screen", General, () => ShowHelp(), null, "F1", "Ctrl+F1");
        C("app.keyboard", "Keyboard shortcuts (Settings → Keyboard)", General, ShowKeyboardSettings, null, "Ctrl+Shift+Slash");
        C("app.lock", "Lock the app", General, LockNow, () => AppLock.CanLock, "Ctrl+Shift+L");
        C("app.screenshot", "Screenshot the active tab", General, () => _ = ScreenshotActiveAsync(), () => Panes.ActiveTab != null, "Ctrl+Shift+S");
        C("workspace.save", "Save the open tabs as a workspace…", General, SaveNamedWorkspace, null);
        C("history.clear", "Clear all history…", General, ClearAllHistoryAsked, null);
        C("history.settings", "History settings (keep, clear automatically)", General, ShowHistorySettings, null);

        C("nav.sidebar", "Show or hide the sidebar", Moving, ToggleSidebar, null, "Ctrl+B", "Ctrl+Shift+B");
        C("nav.focus-sidebar", "Move to the sidebar", Moving, FocusSidebar, null, "Ctrl+Shift+E");
        C("nav.focus-tab", "Move to the active tab", Moving, FocusActiveTab, () => Panes.ActiveTab != null);

        C("tab.close", "Close the tab", Tabs, () => { if (Panes.ActiveTab is { } t && (t.Tag as string) != "fixed") CloseTab(t); }, () => Panes.ActiveTab != null, "Ctrl+W", "Ctrl+Shift+W");
        C("tab.next", "Next tab", Tabs, () => Panes.NextTab(+1), null, "Ctrl+Tab");   // not Ctrl+PgDn: the file commander opens with it
        C("tab.previous", "Previous tab", Tabs, () => Panes.NextTab(-1), null, "Ctrl+Shift+Tab");   // not Ctrl+PgUp: the file commander goes up with it
        C("tab.move-left", "Move the tab left", Tabs, () => MoveActiveTab(-1), () => Panes.ActiveTab != null, "Ctrl+Shift+PageUp");
        C("tab.move-right", "Move the tab right", Tabs, () => MoveActiveTab(+1), () => Panes.ActiveTab != null, "Ctrl+Shift+PageDown");
        for (int i = 1; i <= 8; i++)
        {
            int n = i;
            C($"tab.go-{n}", $"Go to tab {n}", Tabs, () => SelectTab(n), null, $"Ctrl+{n}");
        }
        C("tab.go-last", "Go to the last tab", Tabs, () => SelectTab(int.MaxValue), null, "Ctrl+9");
        C("tab.menu", "Open the tab's menu", Tabs, OpenTabMenu, () => Panes.ActiveTab != null, "Ctrl+Shift+F10");
        C("tab.rename", "Rename the tab…", Tabs, () => { if (Panes.ActiveTab is { } t && Dialogs.Prompt("Rename tab", "Tab title:", t.Header as string ?? "") is { Length: > 0 } n) t.Header = n; }, () => Panes.ActiveTab != null);
        C("tab.close-others", "Close the other tabs in this pane", Tabs, () => { if (Panes.ActiveTab is { } t) CloseMany(Panes.ActivePane.Items.OfType<TabItem>().Where(x => x != t)); }, () => Panes.ActiveTab != null);
        C("tab.close-all", "Close all tabs", Tabs, () => CloseMany(Panes.AllTabs), null);
        C("tab.reconnect", "Reconnect the terminal", Tabs, () => { if (Panes.ActiveTab?.Content is TerminalView tv) _ = tv.ConnectAsync(); }, () => Panes.ActiveTab?.Content is TerminalView, "Ctrl+Shift+R");

        C("pane.next", "Move to the next pane", PanesWindows, Panes.FocusNextPane, null, "F6", "Ctrl+F6");
        C("pane.split-right", "Split right", PanesWindows, () => { if (Panes.ActiveTab is { } t) Panes.Split(t, Orientation.Horizontal); }, () => Panes.ActiveTab != null, "Ctrl+Alt+Right");
        C("pane.split-down", "Split down", PanesWindows, () => { if (Panes.ActiveTab is { } t) Panes.Split(t, Orientation.Vertical); }, () => Panes.ActiveTab != null, "Ctrl+Alt+Down");
        C("pane.move-to-next", "Move the tab to the next pane", PanesWindows, () => { if (Panes.ActiveTab is { } t) Panes.MoveToNextPane(t); }, () => Panes.ActiveTab != null && Panes.PaneCount > 1);
        C("pane.tile", "Tile all tabs", PanesWindows, Panes.Tile, null, "Ctrl+Alt+T");
        C("pane.merge", "Merge all panes", PanesWindows, Panes.MergeAll, null, "Ctrl+Alt+U");
        C("pane.pop-out", "Pop the tab out, or dock it back", PanesWindows, () => { if (Panes.ActiveTab is { } p) { if (Panes.IsFloating(p)) Panes.DockBack(p); else Panes.PopOut(p); } }, () => Panes.ActiveTab != null, "Ctrl+Alt+O");
        C("window.new", "New window (with a host pane)", PanesWindows, () => Panes.NewWindow(), null, "Ctrl+Shift+N");

        ViewCommands.Register();
    }

    /// <summary>A "Go to …" command for every page the sidebar offers now (pages come and go with add-ons and
    /// features, so this follows <see cref="BuildNav"/>). Settings keeps Ctrl+, by default.</summary>
    private void RegisterPageCommands()
    {
        var keys = _allNav.Keys.Select(PageCommandId).ToHashSet(StringComparer.Ordinal);
        KeyCommands.RemoveWhere(c => c.Id.StartsWith("go.", StringComparison.Ordinal) && !keys.Contains(c.Id));
        foreach (var n in _allNav.Values)
        {
            var key = n.Key;
            var id = PageCommandId(key);
            if (!KeyBindingList.ValidId(id)) continue;
            var defaults = key == "settings" ? new[] { "Ctrl+Comma" } : Array.Empty<string>();
            KeyCommands.Register(new KeyCommand(id, "Go to " + n.Title, "Pages", KeyScopes.App, KeyCommands.Keys(defaults)) { Run = () => Navigate(key), Detail = n.Tip });
        }
    }

    /// <summary>"go." and the page key in lower case, with anything but letters, digits and "-" as "-".</summary>
    internal static string PageCommandId(string pageKey) =>
        "go." + new string(pageKey.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) || c == '-' ? c : '-').ToArray()).Trim('-');

    /// <summary>Tooltips that name a key say the one in force.</summary>
    private void ApplyKeyTips()
    {
        QuickConnect.ToolTip = "Quick connect: pick a protocol, type a target (or ↓ for pinned / recent / saved hosts), Enter" + (KeyCommands.Text("app.quick-connect") is { } k ? $" ({k})" : "");
        RefreshLockText();
    }

    // ---- what the commands do -----------------------------------------------------------------------------------

    /// <summary>Settings → Keyboard.</summary>
    internal void ShowKeyboardSettings()
    {
        Navigate("settings");
        SettingsView.Current?.RevealCategory(SettingsView.KeyboardCategory);
    }

    /// <summary>The keyboard goes to the sidebar: the open page's entry, or the first one (the sidebar opens if hidden).
    /// Esc there goes back to the active tab.</summary>
    internal void FocusSidebar()
    {
        if (AppSettings.Current.SidebarCollapsed) ToggleSidebar();
        UpdateLayout();
        var target = _navByKey.Values.FirstOrDefault(r => r.IsChecked == true && r.IsVisible) ?? _navByKey.Values.FirstOrDefault(r => r.IsVisible);
        if (target != null) Keyboard.Focus(target);
        else Nav.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    /// <summary>The keyboard goes to the active tab's content (a terminal gets its own focus).</summary>
    internal void FocusActiveTab()
    {
        var pane = Panes.ActivePane;
        if (pane.SelectedItem is TabItem { Content: IActivatable a }) { a.Activated(); return; }
        if (pane.SelectedContent is UIElement el && !el.Focus()) el.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    /// <summary>In the sidebar: ↑ / ↓ move between the pages shown, Home / End to the first / last, Enter or Space opens
    /// one (the button's own keys), Esc returns to the active tab.</summary>
    private void OnNavKey(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        if (e.Key == Key.Escape && Panes.ActiveTab != null) { FocusActiveTab(); e.Handled = true; return; }
        if (e.Key is not (Key.Up or Key.Down or Key.Home or Key.End) || Keyboard.FocusedElement is not RadioButton current) return;
        var shown = _navByKey.Values.Where(r => r.IsVisible).ToList();
        int i = shown.IndexOf(current);
        if (i < 0 || shown.Count == 0) return;
        int next = e.Key switch { Key.Up => Math.Max(0, i - 1), Key.Down => Math.Min(shown.Count - 1, i + 1), Key.Home => 0, _ => shown.Count - 1 };
        Keyboard.Focus(shown[next]);
        shown[next].BringIntoView();
        e.Handled = true;
    }

    /// <summary>Tab <paramref name="n"/> (1-based) of the active pane; past the end, the last one.</summary>
    internal void SelectTab(int n)
    {
        var tabs = Panes.ActivePane.Items.OfType<TabItem>().Where(t => t.Visibility == Visibility.Visible).ToList();
        if (tabs.Count == 0) return;
        Panes.Select(tabs[Math.Clamp(n, 1, tabs.Count) - 1]);
    }

    /// <summary>Moves the active tab one place left or right within its pane.</summary>
    internal void MoveActiveTab(int dir)
    {
        if (Panes.ActiveTab is not { } tab || Panes.PaneOf(tab) is not { } pane) return;
        int i = pane.Items.IndexOf(tab) + dir;
        if (i < 0 || i >= pane.Items.Count) return;
        Panes.MoveTab(tab, pane, i);
    }

    /// <summary>The active tab's right-click menu, opened under its header.</summary>
    internal void OpenTabMenu()
    {
        if (Panes.ActiveTab is { } tab) Menus.ShowBelow(tab, TabMenu(tab).ToList());
    }
}
