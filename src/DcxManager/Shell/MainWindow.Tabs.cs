using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using DcxManager.Services;
using DcxManager.Ssh;
using DcxManager.Views;
using DcxManager.Vultr;

namespace DcxManager;

// MainWindow: documents: tabs, the tab menu, screenshots of tabs (moved unchanged from MainWindow.xaml.cs in 0.2.0 phase 6½).
public partial class MainWindow
{
    // ---- documents ----------------------------------------------------------------------------------------------

    public TabItem OpenSingleton(string key, string header, Func<FrameworkElement> factory, bool closable = true)
    {
        if (_singletons.TryGetValue(key, out var existing) && Panes.Contains(existing)) { Panes.Select(existing); return existing; }
        var tab = AddTab(header, factory(), closable);
        _singletons[key] = tab;
        tab.Name = "";
        return tab;
    }

    public TabItem AddTab(string header, FrameworkElement content, bool closable = true)
    {
        var tab = new TabItem { Header = header, Content = content, Style = (Style)FindResource("DocTab"), Tag = closable ? null : "fixed" };
        EnvTab.Apply(tab);   // environment label on host tabs (ADR-017)
        // 0.6.7 (ADR-086): sessions opened from the Hosts page open inside it (when set so); other screens beside it.
        Panes.Add(tab, session: content is TerminalView or SftpView or ServerView or RdpView or VncView or CommanderView);
        SaveWorkspace();
        LiveSessions.Touch();
        return tab;
    }

    private void OnCloseTab(object sender, ExecutedRoutedEventArgs e)
    {
        var tab = e.Parameter as TabItem ?? Panes.ActiveTab;
        if (tab == null || (tab.Tag as string) == "fixed") return;
        CloseTab(tab);
    }

    public void CloseTab(TabItem tab)
    {
        if (tab.Content is ICloseable c && !c.CanClose()) return;
        Panes.Remove(tab);
        if (tab.Content is IDisposable d) { try { d.Dispose(); } catch (Exception ex) { App.Log("Close tab", ex); } }
        if (tab.Content is { } content) PasswordSafeSessions.Closed(content);   // 0.5.0: a Password Safe checkout goes back in
        foreach (var kv in _singletons.Where(kv => kv.Value == tab).ToList()) _singletons.Remove(kv.Key);
        SaveWorkspace();
        LiveSessions.Touch();
    }

    private void CloseMany(IEnumerable<TabItem> tabs)
    {
        var list = tabs.Where(t => (t.Tag as string) != "fixed").ToList();
        int live = list.Count(t => t.Content is TerminalView { IsConnected: true });
        if (live > 0 && !Dialogs.Confirm("Close tabs", $"Close {list.Count} tab(s)? {live} terminal session(s) are connected.", "Close")) return;
        foreach (var t in list) CloseTab(t);
    }

    /// <summary>Right-click menu of a tab header (0.2.1: grouped — Close and Rename first, the rest in groups that fold
    /// into submenus when the menu is long).</summary>
    private IEnumerable<object?> TabMenu(TabItem tab)
    {
        bool fixedTab = (tab.Tag as string) == "fixed";
        var pane = Panes.PaneOf(tab);
        var siblings = pane?.Items.OfType<TabItem>().ToList() ?? new();
        bool floating = Panes.IsFloating(tab);

        yield return Menus.Item("Close", () => CloseTab(tab), "Ctrl+W", !fixedTab);
        yield return Menus.Item("Rename tab…", () => { if (Dialogs.Prompt("Rename tab", "Tab title:", tab.Header as string ?? "") is { Length: > 0 } n) tab.Header = n; });
        yield return Menus.Group("Close tabs",
            Menus.Item("Close other tabs in this pane", () => CloseMany(siblings.Where(t => t != tab)), enabled: siblings.Count > 1),
            Menus.Item("Close tabs to the right", () => CloseMany(siblings.Skip(siblings.IndexOf(tab) + 1)), enabled: siblings.IndexOf(tab) < siblings.Count - 1),
            Menus.Item("Close all tabs", () => CloseMany(Panes.AllTabs)));
        // Tab groups (ADR-019): a printed name in the group's colour; group actions below.
        var group = TabGroups.GetName(tab);
        var members = group.Length > 0 ? TabGroups.Members(group).ToList() : new();
        var terms = members.Select(t => t.Content).OfType<TerminalView>().ToList();
        yield return Menus.Group("Tab group",
            Menus.Sub(group.Length > 0 ? $"In group “{group}” — change" : "Add to a group", TabGroups.Names.Select(n => (object?)Menus.Item((n == group ? "✓ " : "") + n, () => TabGroups.Assign(tab, n), enabled: n != group))
                .Append(Menus.Sep())
                .Append(Menus.Item("New group…", () =>
                {
                    if (Dialogs.Prompt("New tab group", "Group name (for example “web tier” or “change 4711”):", "") is not { } n) return;
                    if (TabGroups.Validate(n) is { } err) Dialogs.Info("Tab group", err); else TabGroups.Assign(tab, n);
                }))
                .Append(Menus.Item("Remove from group", () => TabGroups.Remove(tab), enabled: group.Length > 0)).ToArray()),
            group.Length == 0 ? null : new object?[]
            {
                Menus.Item($"Next tab in “{group}”", () => { var i = members.IndexOf(tab); Panes.Select(members[(i + 1) % members.Count]); }, enabled: members.Count > 1),
                Menus.Item("⌨ Broadcast input to the group's terminals…", () => InputBroadcast.Start(terms, $"group “{group}”"), enabled: terms.Count > 1),
                Menus.Item("↻ Reconnect the group's terminals", () => { foreach (var t in terms.Where(t => !t.IsConnected)) _ = t.ConnectAsync(); }, enabled: terms.Any(t => !t.IsConnected)),
                Menus.Item("✕ Disconnect the group's terminals", () => { foreach (var t in terms) t.Disconnect(); }, enabled: terms.Any(t => t.IsConnected)),
                Menus.Item("Close the group's tabs", () => CloseMany(members)),
            });
        int panesHere = Panes.WindowOf(tab) is { } win ? PaneHost.PanesOf(win).Count() : Panes.PaneCount;
        yield return Menus.Group("Layout", new object?[]
        {
            Menus.Item("Split right", () => Panes.Split(tab, Orientation.Horizontal), "Ctrl+Alt+→", siblings.Count > 1),
            Menus.Item("Split down", () => Panes.Split(tab, Orientation.Vertical), "Ctrl+Alt+↓", siblings.Count > 1),
            Menus.Item("Move to next pane", () => Panes.MoveToNextPane(tab), enabled: panesHere > 1),
            Menus.Item("Tile all tabs", Panes.Tile, "Ctrl+Alt+T", !floating),
            Menus.Item("Merge all panes", Panes.MergeAll, "Ctrl+Alt+U", !floating && Panes.PaneCount > 1),
        }.Concat(Panes.WindowMenuItems(tab)).ToArray());

        if (tab.Content is TerminalView { Host: null } local)
        {
            bool serial = local.Target is Sessions.SerialTarget;
            yield return Menus.Group("Session",
                Menus.Item(serial ? "↻ Reconnect" : "↻ New shell in this tab", () => _ = local.ConnectAsync()),
                Menus.Item(serial ? "✕ Disconnect (release the port)" : "✕ End shell", local.Disconnect, enabled: local.IsConnected),
                local.Target.CanDuplicate ? Menus.Item("⊞ Duplicate session", () => OpenSession(local.Target.Clone(), forceNew: true)) : null);
        }
        if (tab.Content is IHostView { Host: { } h })
        {
            var tv = tab.Content as TerminalView;
            yield return Menus.Group("Host",
                tv != null ? Menus.Item("↻ Reconnect", () => _ = tv.ConnectAsync()) : Menus.Item("⌨ Terminal", () => OpenTerminal(h)),
                tv != null ? Menus.Item("✕ Disconnect", tv.Disconnect, enabled: tv.IsConnected) : null,
                tv != null ? Menus.Item("⊞ Duplicate session", () => OpenTerminal(h, forceNew: true)) : null,
                tab.Content is not SftpView ? Menus.Item("⇅ Files", () => OpenFiles(h)) : null,
                Menus.Item("⇆ File commander", () => OpenCommander(h)),
                tab.Content is not ServerView ? Menus.Item("▣ Manage", () => OpenServer(h)) : null,
                Menus.Sep(),
                Menus.Item($"✎ Notes ({Notes.NoteStore.ForHost(h.Id).Count})", () => OpenNotes(h.Id)),
                IncidentUi.HostMenu(h).ToArray(),
                AddonUi.HostMenu(h),
                Menus.Sep(),
                Menus.Item("Copy address", () => Menus.Clip(h.Address)),
                Menus.Item("Copy user@host", () => Menus.Clip($"{h.Username}@{h.Address}{(h.Port != 22 ? ":" + h.Port : "")}")));
        }
        // Modules' items for a tab (0.2.0 phase 3): the AI module adds "Ask AI about this screen…".
        yield return Menus.Group("Screenshot & help",
            ScreenshotMenu(tab),
            Menus.Item("Help for this screen", () => { Panes.Select(tab); ShowHelp(); }, "F1"),
            AppModules.MenuItems("tab", new TabMenuContext(tab, ScreenName(tab))).ToArray());
    }

    /// <summary>What kind of screen a tab is, for help/AI grounding. Host tabs give their kind, not the host's name.</summary>
    public static string ScreenName(TabItem tab) => tab.Content switch
    {
        TerminalView => "Terminal",
        SftpView => "SFTP file browser",
        ServerView => "Server management (Manage)",
        CommanderView { } when tab.Header is string h && !h.StartsWith("⇆ File commander") => "File commander",
        _ => Regex.Replace(tab.Header as string ?? "this screen", @"^\W+\s*", "").Trim() is { Length: > 0 } n ? n : "this screen",
    };

    /// <summary>A tab's content as an image: terminals via the browser, everything else by printing the window.</summary>
    private static Task<System.Windows.Media.Imaging.BitmapSource> CaptureTabAsync(TabItem tab)
    {
        if (tab.Content is TerminalView tv) return tv.CaptureAsync();
        if (tab.Content is FrameworkElement fe && fe.IsVisible) return Task.FromResult(Screenshot.CaptureElement(fe));
        throw new InvalidOperationException("Select the tab first so it is on screen, then take the screenshot.");
    }

    /// <summary>Ctrl+Shift+S: the active tab, delivered as Settings → Terminal says (save, clipboard or both).</summary>
    private Task ScreenshotActiveAsync() => Panes.ActiveTab is { } tab ? ScreenshotTabAsync(tab) : Task.CompletedTask;

    /// <summary>One tab, default delivery (used by the 📷 button in each pane's tab strip).</summary>
    public async Task ScreenshotTabAsync(TabItem tab)
    {
        try { Panes.Select(tab); Screenshot.Default(Window.GetWindow(tab) ?? this, await CaptureTabAsync(tab), tab.Header as string ?? "tab"); }
        catch (Exception ex) { Dialogs.Error("Screenshot", ex.Message); }
    }

    public MenuItem ScreenshotMenu(TabItem tab) =>
        Screenshot.Menu(() => CaptureTabAsync(tab), tab.Header as string ?? "tab", Window.GetWindow(tab) ?? this, "📷 Screenshot this tab");

    public List<object?> ScreenshotMenuItems(TabItem tab) =>
        Screenshot.MenuItems(() => CaptureTabAsync(tab), tab.Header as string ?? "tab", Window.GetWindow(tab) ?? this);
}
