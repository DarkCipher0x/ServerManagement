using System.Windows;
using DcxManager.Services;
using DcxManager.Views;

namespace DcxManager;

// MainWindow: saved sessions (0.6.29, DarkCipher0x: "the option to save my connected sessions when i exit the app"): the
// choice at exit, and named workspaces in the title bar's ▦ Workspaces menu. What a tab keeps is WorkspaceEntries();
// reopening is RestoreTabs(connect: true).
public partial class MainWindow
{
    /// <summary>Exiting with tabs that can be reopened: save them (asked, or by the setting), or not. False = don't exit.</summary>
    internal bool ExitDecision(IReadOnlyList<Workspace.Entry> entries, int sessions)
    {
        var mode = AppSettings.Current.SessionsOnExit;
        if (entries.Count == 0 || mode == "never")
            return sessions == 0 || ExitWithoutPrompt || Dialogs.Confirm("Exit", $"{sessions} terminal session(s) are still connected. Close them and exit?", "Exit");
        // Closing for the app itself (locked by policy, a profile switch) can't ask: the sessions are kept unless "never".
        if (mode == "save" || ExitWithoutPrompt) { Workspace.SaveOnExit(entries); return true; }

        var options = new[] { "Save and exit", "Exit without saving", "Cancel" };
        var message = ExitMessage(entries, sessions);
        var (choice, again) = PolicySettings.IsManaged(nameof(AppSettings.SessionsOnExit))
            ? (Dialogs.Choose("Exit", message, options, cancelIndex: 2), false)
            : Dialogs.Choose("Exit", message, options, "Don't ask again (Settings → Terminal → Saved sessions)", cancelIndex: 2);
        if (choice is not (0 or 1)) return false;
        if (choice == 0) Workspace.SaveOnExit(entries);
        if (again)
        {
            AppSettings.Current.SessionsOnExit = choice == 0 ? "save" : "never";
            try { AppSettings.Save(); } catch (Exception ex) { App.Log("Save exit choice", ex); }
        }
        return true;
    }

    /// <summary>"3 tabs are open (2 connected sessions)…": what saving keeps and how it comes back.</summary>
    internal static string ExitMessage(IReadOnlyList<Workspace.Entry> entries, int sessions)
    {
        var tabs = entries.Count == 1 ? "1 tab is open" : $"{entries.Count} tabs are open";
        var live = sessions switch { 0 => "", 1 => " (1 connected session)", _ => $" ({sessions} connected sessions)" };
        var how = AppSettings.Current.RestoreReconnect
            ? "they reopen and reconnect next time you start ServerManagement (PRODUCTION hosts reopen disconnected)"
            : "they reopen next time you start ServerManagement, disconnected until you press ↻";
        return $"{tabs}{live}. Save them? Then {how}, in the same panes and groups, with terminals back in their folder or tmux session.";
    }

    // ---- named workspaces ----------------------------------------------------------------------------------------

    private void OnWorkspacesMenu(object sender, RoutedEventArgs e) => Menus.ShowAt((UIElement)sender, WorkspaceMenu());

    internal IEnumerable<object?> WorkspaceMenu()
    {
        var named = Workspace.AllNamed();
        yield return Menus.Item("＋ Save the open tabs as a workspace…", SaveNamedWorkspace, enabled: WorkspaceEntries().Count > 0,
            tip: "Terminals (with their folder or tmux session), files, File commander, Remote Desktop, VNC, Manage and portal tabs, in their panes and groups");
        if (named.Count > 0) yield return Menus.Sep();
        foreach (var w in named)
        {
            var name = w.Name;
            yield return Menus.Sub($"▦ {name}  ·  {w.Entries.Count} tab{(w.Entries.Count == 1 ? "" : "s")}",
                Menus.Item("▶ Open", () => OpenNamedWorkspace(name, connect: true), tip: "Open its tabs beside the ones already open (tabs already open aren't opened twice)"),
                Menus.Item("Open disconnected", () => OpenNamedWorkspace(name, connect: false)),
                Menus.Sep(),
                Menus.Item("Replace with the open tabs", () => ReplaceNamedWorkspace(name), enabled: WorkspaceEntries().Count > 0),
                Menus.Item("Rename…", () => RenameNamedWorkspace(name)),
                Menus.Item("Remove…", () => RemoveNamedWorkspace(name)));
        }
        yield return Menus.Sep();
        yield return Menus.Item("Saved sessions settings…", () => { Navigate("settings"); SettingsView.Current?.RevealCard("Saved sessions"); });
    }

    internal void SaveNamedWorkspace()
    {
        var entries = WorkspaceEntries();
        if (entries.Count == 0) { Dialogs.Info("Workspaces", "No open tab can be saved yet: open a session first."); return; }
        if (Dialogs.Prompt("Save workspace", $"A name for these {entries.Count} tab(s) (e.g. Homelab, VPS):") is not { } raw || raw.Trim().Length == 0) return;
        var name = raw.Trim();
        if (name.Length > 60) name = name[..60];
        if (Workspace.AllNamed().Any(w => w.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            && !Dialogs.Confirm("Save workspace", $"Replace the workspace “{name}” with the open tabs?", "Replace")) return;
        Workspace.SaveNamed(name, entries);
        Status($"Workspace “{name}” saved ({entries.Count} tab(s)).");
    }

    private void ReplaceNamedWorkspace(string name)
    {
        var entries = WorkspaceEntries();
        if (!Dialogs.Confirm("Workspaces", $"Replace the workspace “{name}” with the {entries.Count} tab(s) open now?", "Replace")) return;
        Workspace.SaveNamed(name, entries);
        Status($"Workspace “{name}” updated ({entries.Count} tab(s)).");
    }

    private void RenameNamedWorkspace(string name)
    {
        if (Dialogs.Prompt("Rename workspace", "New name:", name) is not { } raw || raw.Trim().Length == 0 || raw.Trim() == name) return;
        var to = raw.Trim().Length > 60 ? raw.Trim()[..60] : raw.Trim();
        if (Workspace.AllNamed().Any(w => w.Name.Equals(to, StringComparison.OrdinalIgnoreCase) && !w.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            && !Dialogs.Confirm("Rename workspace", $"A workspace called “{to}” exists. Replace it?", "Replace")) return;
        Workspace.RenameNamed(name, to);
    }

    private void RemoveNamedWorkspace(string name)
    {
        if (!Dialogs.Confirm("Workspaces", $"Remove the workspace “{name}” from the menu? (It is kept, archived, in the encrypted store.) Open tabs aren't closed.", "Remove")) return;
        Workspace.RemoveNamed(name);
        Status($"Workspace “{name}” removed.");
    }

    /// <summary>Opens a named workspace's tabs beside the open ones; a tab of the same kind and host that is already open
    /// isn't opened again.</summary>
    internal int OpenNamedWorkspace(string name, bool connect)
    {
        if (Workspace.AllNamed().FirstOrDefault(w => w.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is not { } ws) return 0;
        var open = WorkspaceEntries().Select(e => (e.Kind, e.HostId)).ToHashSet();
        var todo = Reopenable(ws.Entries).Where(e => !open.Contains((e.Kind, e.HostId))).ToList();
        if (todo.Count > 0) RestoreTabs(todo, connect);
        Status(todo.Count == 0 ? $"Workspace “{ws.Name}”: its tabs are already open." : $"Workspace “{ws.Name}”: opened {todo.Count} tab(s).");
        return todo.Count;
    }
}
