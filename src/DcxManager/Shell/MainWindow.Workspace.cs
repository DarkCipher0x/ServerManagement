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
            string? kind = null, id = null;
            if (t.Content is TerminalView { Target: Sessions.LocalTarget lt }) { kind = "local"; id = lt.Profile.Id; }
            else if (t.Content is TerminalView { Target: Sessions.SerialTarget st }) { if (AppData.Current.SerialProfiles.Contains(st.Profile)) { kind = "serial"; id = st.Profile.Id; } }
            else if (t.Content is IHostView { Host: { IsAdHoc: false } h } && !string.IsNullOrEmpty(h.Id))
            {
                kind = t.Content switch { TerminalView => "terminal", SftpView => "files", ServerView => "server", RdpView => "rdp", VncView => "vnc", _ => null };
                id = h.Id;
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
                Active = t == active, Group = g.Length > 0 ? g : null,
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
        _ => AppData.Current.FindHost(e.HostId) != null,
    }).ToList();

    /// <summary>After an unclean exit (or with work left in the recovery journal) one screen offers everything that
    /// can be recovered (ADR-022). Never during the self-test, which builds and renders the screen itself.</summary>
    private void OfferRestore()
    {
        if (SelfTest.Running || Environment.GetCommandLineArgs().Contains("--selftest")) return;
        var tabs = AppSettings.Current.RestoreWorkspace && Workspace.UncleanLastExit ? Reopenable(Workspace.Load()) : new();
        var journal = Recovery.Previous;
        if (tabs.Count == 0 && journal == null) return;
        RecoveryWindow.Show(this, Workspace.UncleanLastExit, tabs, journal);
    }

    /// <summary>Reopens saved tabs disconnected (never auto-connected), in their panes, pop-outs and groups.</summary>
    public void RestoreTabs(IReadOnlyList<Workspace.Entry> entries)
    {
        var opened = new List<(Workspace.Entry Entry, TabItem Tab)>();
        foreach (var e in entries)
        {
            var before = Panes.AllTabs.ToHashSet();
            if (e.Kind == "local") { if (Sessions.LocalShells.Find(e.HostId) is { } shell) OpenLocal(shell, connectOnOpen: false, forceNew: true); }
            else if (e.Kind == "serial") { if (AppData.Current.SerialProfiles.FirstOrDefault(s => s.Id == e.HostId) is { } sp) OpenSession(new Sessions.SerialTarget(sp), connectOnOpen: false); }
            else if (AppData.Current.FindHost(e.HostId) is { } h && !Locks.IsLocked("host:" + h.Id))   // a locked host never reopens by itself (ADR-067)
            {
                switch (e.Kind)
                {
                    case "terminal": OpenTerminal(h, connectOnOpen: false, forceNew: true); break;
                    case "files": OpenFiles(h, forceNew: true); break;
                    case "server": OpenServer(h); break;
                    case "rdp":
                        if (AppSettings.Current.RdpEmbedded && h.Rdp.Gateway.Trim().Length == 0 && Rdp.RdpAxHost.AvailableClsid() != null)
                            try { AddTab("🖥 " + h.Display, new RdpView(h, connectOnOpen: false)); } catch (Exception ex) { App.Log("Restore RDP tab", ex); }
                        break;
                    case "vnc": AddTab("🖵 " + h.Display, new VncView(h, connectOnOpen: false)); break;   // reconnects on ↻, never by itself
                }
            }
            if (Panes.AllTabs.FirstOrDefault(t => !before.Contains(t)) is { } tab) opened.Add((e, tab));
        }
        if (opened.Count == 0) return;
        foreach (var (e, t) in opened) if (e.Group is { Length: > 0 } g) TabGroups.Assign(t, g);
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
