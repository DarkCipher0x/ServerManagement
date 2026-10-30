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

// MainWindow: the open-tab workspace and crash recovery (moved unchanged from MainWindow.xaml.cs in 0.2.0 phase 6½).
public partial class MainWindow
{
    // ---- workspace / crash recovery ------------------------------------------------------------------------------

    private System.Windows.Threading.DispatcherTimer? _workspaceSave;

    /// <summary>Persists the open saved-host tabs (and where they are) so they can be reopened after an unclean shutdown.
    /// Coalesced: a burst of tab changes is one write half a second later; nothing is written while nothing changes.</summary>
    private void SaveWorkspace()
    {
        if (!IsLoaded) return;
        _workspaceSave ??= new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(500), System.Windows.Threading.DispatcherPriority.Background,
            (_, _) => { _workspaceSave!.Stop(); SaveWorkspaceNow(); }, Dispatcher) { IsEnabled = false };
        _workspaceSave.Stop(); _workspaceSave.Start();
    }

    /// <summary>The workspace entries for the open tabs, with their pane / pop-out, active flag and group (ADR-022).</summary>
    public List<Workspace.Entry> WorkspaceEntries()
    {
        var entries = new List<Workspace.Entry>();
        var main = Panes.MainPanes.ToList();
        var windows = Panes.Floats.ToList();
        var active = Panes.ActiveTab;
        foreach (var t in Panes.AllTabs)
        {
            string? kind = null, id = null, cwd = null, tmux = null, path = null, left = null;
            HostEntry? adHoc = null;
            if (t.Content is TerminalView { Target: Sessions.LocalTarget lt } ltv) { kind = "local"; id = lt.Profile.Id; cwd = ltv.Place().Cwd; }
            else if (t.Content is TerminalView { Target: Sessions.SerialTarget st }) { if (AppData.Current.SerialProfiles.Contains(st.Profile)) { kind = "serial"; id = st.Profile.Id; } }
            else if (t.Content is CommanderView cv)
            {
                // 0.6.29: both panes, exactly where they were.
                kind = "commander"; id = cv.OpenedForHostId ?? "";
                (left, path) = cv.Places;
            }
            else if (t.Content is BrowserView bv)
            {
                if (Browser.Portals.Find(bv.Bookmark.Id) != null) { kind = "portal"; id = bv.Bookmark.Id; }   // saved portals only
            }
            else if (t.Content is IHostView { Host: { } h } && !string.IsNullOrEmpty(h.Id) && !h.IsShared)
            {
                kind = t.Content switch { TerminalView => "terminal", SftpView => "files", ServerView => "server", RdpView => "rdp", VncView => "vnc", _ => null };
                id = h.Id;
                // 0.6.29: a quick-connect host goes with its settings (never its password: that was never part of it).
                if (h.IsAdHoc) adHoc = h.Clone();
                if (t.Content is TerminalView tv) (cwd, tmux) = tv.Place();
                if (t.Content is SftpView sv && sv.CurrentPath.Length > 0) path = sv.CurrentPath;
            }
            if (kind == null || id == null) continue;
            var pane = Panes.PaneOf(t);
            var win = Panes.FloatOf(pane);
            int fi = win == null ? -1 : windows.IndexOf(win);
            var g = TabGroups.GetName(t);
            entries.Add(new Workspace.Entry(kind, id)
            {
                Floating = fi >= 0, Pane = fi >= 0 ? fi : Math.Max(0, pane == null ? 0 : main.IndexOf(pane)),
                WindowPane = win == null || pane == null ? 0 : Math.Max(0, PaneHost.PanesOf(win).ToList().IndexOf(pane)),
                Active = t == active, Group = g.Length > 0 ? g : null, GroupStacked = g.Length > 0 && TabGroups.IsStacked(g),
                Cwd = cwd, Tmux = tmux, Path = path, LeftPath = left, AdHoc = adHoc,
            });
        }
        return entries;
    }

    /// <summary>Writes the workspace at once (fatal error / session end, and the self-test).</summary>
    public void SaveWorkspaceNow()
    {
        _workspaceSave?.Stop();
        if (IsLoaded) Workspace.Save(WorkspaceEntries());
    }

    /// <summary>Saved tabs that can still be opened (their host, shell or serial profile still exists).</summary>
    public static List<Workspace.Entry> Reopenable(IEnumerable<Workspace.Entry> entries) => entries.Where(e => e.Kind switch
    {
        "local" => Sessions.LocalShells.Find(e.HostId) != null,
        "serial" => AppData.Current.SerialProfiles.Any(s => s.Id == e.HostId),
        "commander" => e.HostId.Length == 0 || AppData.Current.FindHost(e.HostId) != null,
        "portal" => Browser.Portals.Find(e.HostId) != null,
        _ => e.AdHoc != null || AppData.Current.FindHost(e.HostId) != null,
    }).ToList();

    /// <summary>After an unclean exit (or with work left in the recovery journal) one screen offers everything that
    /// can be recovered (ADR-022). After a clean exit that saved its sessions (0.6.29), they reopen straight away.
    /// Never during the self-test, which builds and renders the screen itself.</summary>
    private void OfferRestore()
    {
        if (SelfTest.Running || Environment.GetCommandLineArgs().Contains("--selftest")) return;
        var saved = Workspace.TakeSavedOnExit();   // taken either way: after a crash the crash list is the newer one
        var tabs = AppSettings.Current.RestoreWorkspace && Workspace.UncleanLastExit ? Reopenable(Workspace.Load()) : new();
        var journal = Recovery.Previous;
        if (!Workspace.UncleanLastExit && Reopenable(saved) is { Count: > 0 } back)
        {
            RestoreTabs(back, connect: true);
            Status($"Reopened the {back.Count} tab(s) saved at the last exit.");
        }
        if (tabs.Count == 0 && journal == null) return;
        RecoveryWindow.Show(this, Workspace.UncleanLastExit, tabs, journal);
    }

    /// <summary>Whether a saved tab may reopen by itself: the host, its jump host and its credential are all unlocked
    /// (a locked one never reopens by itself and never asks to be unlocked at start, ADR-067).</summary>
    private static bool MayReopen(HostEntry h) =>
        (h.IsAdHoc || Locks.IsOpen("host:" + h.Id))
        && (h.JumpHostId is not { Length: > 0 } j || Locks.IsOpen("host:" + j))
        && (h.CredentialId is not { Length: > 0 } c || Locks.IsOpen("cred:" + c));

    /// <summary>A Commander pane's saved place, or nothing when its host is gone or locked.</summary>
    private static string PanePlace(string? state)
    {
        if (string.IsNullOrEmpty(state)) return "none|";
        var r = Files.FsRef.Parse(state.Split('|', 2)[0]);
        return r.Kind == "host" && (r.Host is not { } h || !MayReopen(h)) ? "none|" : state;
    }

    /// <summary>Reopens saved tabs in their panes, pop-outs and groups. After a crash (<paramref name="connect"/> false)
    /// they come back disconnected. Saved sessions (exit, named workspaces: 0.6.29) reconnect when Settings says so,
    /// except PRODUCTION hosts, which wait for ↻; terminals go back to their folder or tmux session.</summary>
    public void RestoreTabs(IReadOnlyList<Workspace.Entry> entries, bool connect = false)
    {
        var opened = new List<(Workspace.Entry Entry, TabItem Tab)>();
        connect &= AppSettings.Current.RestoreReconnect;
        foreach (var e in entries)
        {
            var before = Panes.AllTabs.ToHashSet();
            try
            {
                if (e.Kind == "local") { if (Sessions.LocalShells.Find(e.HostId) is { } shell) OpenLocal(shell, connectOnOpen: connect, forceNew: true); }
                else if (e.Kind == "serial") { if (AppData.Current.SerialProfiles.FirstOrDefault(s => s.Id == e.HostId) is { } sp) OpenSession(new Sessions.SerialTarget(sp), connectOnOpen: connect); }
                else if (e.Kind == "portal") { if (Browser.Portals.Find(e.HostId) is { } b && PortalUi.ClosedTarget(b) == null) PortalUi.Open(b, window: false); }
                else if (e.Kind == "commander")
                {
                    var forHost = e.HostId.Length > 0 ? AppData.Current.FindHost(e.HostId) : null;
                    AddTab("⇆ " + (forHost?.Display ?? "File commander"), new CommanderView(forHost, null, (PanePlace(e.LeftPath), PanePlace(e.Path))));
                }
                else if ((e.AdHoc ?? AppData.Current.FindHost(e.HostId)) is { } h && MayReopen(h))   // a locked host never reopens by itself (ADR-067)
                {
                    if (e.AdHoc != null) h.IsAdHoc = true;   // not stored: it is what makes the copy a quick-connect host again
                    bool c = connect && !Envs.IsProduction(Envs.For(h));
                    switch (e.Kind)
                    {
                        case "terminal": OpenTerminal(h, connectOnOpen: c, forceNew: true); break;
                        case "files": OpenFiles(h, e.Path, forceNew: true); break;
                        case "server": OpenServer(h); break;
                        case "rdp":
                            if (AppSettings.Current.RdpEmbedded && h.Rdp.Gateway.Trim().Length == 0 && Rdp.RdpAxHost.AvailableClsid() != null)
                                AddTab("🖥 " + h.Display, new RdpView(h, connectOnOpen: c));
                            break;
                        case "vnc": AddTab("🖵 " + h.Display, new VncView(h, connectOnOpen: c)); break;   // otherwise it reconnects on ↻
                    }
                }
            }
            catch (Exception ex) { App.Log("Restore " + e.Kind + " tab", ex); }
            if (Panes.AllTabs.FirstOrDefault(t => !before.Contains(t)) is { } tab)
            {
                if (tab.Content is TerminalView tv) tv.RestorePlace(e.Cwd, e.Tmux);
                opened.Add((e, tab));
            }
        }
        if (opened.Count == 0) return;
        foreach (var (e, t) in opened) if (e.Group is { Length: > 0 } g) TabGroups.Assign(t, g);
        foreach (var g in opened.Where(o => o.Entry.GroupStacked && o.Entry.Group is { Length: > 0 }).Select(o => o.Entry.Group!).Distinct(StringComparer.OrdinalIgnoreCase)) TabGroups.SetStacked(g, true);
        var active = opened.FirstOrDefault(o => o.Entry.Active).Tab;
        var mainGroups = opened.Where(o => !o.Entry.Floating).GroupBy(o => o.Entry.Pane).OrderBy(g => g.Key).Select(g => (IReadOnlyList<TabItem>)g.Select(o => o.Tab).ToList()).ToList();
        if (mainGroups.Count > 1) Panes.Arrange(mainGroups, active);
        // Extra windows (0.6.4): into the windows reopened at start, in order, or new ones; a window's own panes are split
        // again one per saved pane.
        var reopened = Panes.Floats.Where(f => f.KeepOpen && !PaneHost.PanesOf(f).Any(p => p.Items.Count > 0)).ToList();
        int next = 0;
        foreach (var fg in opened.Where(o => o.Entry.Floating).GroupBy(o => o.Entry.Pane).OrderBy(g => g.Key))
        {
            var bySub = fg.GroupBy(o => o.Entry.WindowPane).OrderBy(g => g.Key).Select(g => g.Select(o => o.Tab).ToList()).ToList();
            var all = bySub.SelectMany(x => x).ToList();
            FloatWindow? w = next < reopened.Count ? reopened[next++] : null;
            if (w != null) foreach (var t in all) Panes.MoveToWindow(t, w);
            else { w = Panes.PopOut(all[0]); if (w == null) continue; w.KeepOpen = true; foreach (var t in all.Skip(1)) Panes.MoveTab(t, w.Frame.Pane); }
            foreach (var sub in bySub.Skip(1))
            {
                if (!Panes.Split(sub[0], Orientation.Horizontal)) continue;
                if (Panes.PaneOf(sub[0]) is { } sp) foreach (var t in sub.Skip(1)) Panes.MoveTab(t, sp);
            }
        }
        if (active != null) Panes.Select(active);
        SaveWorkspace();
    }

    public void SetTabHeader(FrameworkElement content, string header)
    {
        foreach (var t in Panes.AllTabs) if (t.Content == content) t.Header = header;
    }

    private void OnActiveTab(TabItem? sel)
    {
        SaveWorkspace();   // the active tab and pane moves are part of the workspace (ADR-022); coalesced
        if (sel?.Content is TerminalView front) Sessions.TerminalBridge.Follow(front);   // 0.6.10: the AI Engineer's default target
        var key = _singletons.FirstOrDefault(kv => kv.Value == sel).Key;
        if (key == null && sel != null && Menus.Ancestor<HostsView>(sel) != null) key = "hosts";   // 0.6.7: a session inside the Hosts tab
        foreach (var rb in _navByKey.Values) if (rb.IsChecked == true && (string)rb.Tag != key) rb.IsChecked = false;
        if (key != null && _navByKey.TryGetValue(key, out var nav) && nav.IsChecked != true)
        {
            // Highlight without re-navigating.
            _suppressNav = true;
            try { nav.IsChecked = true; } finally { _suppressNav = false; }
        }
        if (sel != null)
        {
            sel.BringIntoView();
            if (sel.Content is IActivatable a) a.Activated();
        }
    }

    private bool _suppressNav;
}
