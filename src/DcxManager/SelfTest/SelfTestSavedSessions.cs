using System.IO;
using System.Text.Json;
using DcxManager.Services;
using DcxManager.Sessions;
using DcxManager.Views;

namespace DcxManager;

/// <summary>
/// 0.6.29 saved sessions: where a terminal is (OSC 7 and prompts, never by typing), the line that goes back there, leaving
/// tmux; the exit file (taken once) and named workspaces (removed ones archived); a quick-connect host kept without its
/// secret; the exit choice (Save and exit, Don't ask again, Cancel, Never); and tabs reopened with their folder, then
/// captured again.
/// </summary>
public static class SavedSessionsTests
{
    public static async Task RunAsync(Action<string> ok, Action<string, Exception?> fail)
    {
        await Run("saved sessions: working folder from OSC 7 and POSIX / PowerShell prompts (not Red Hat's short prompt), quoted cd lines, leaving tmux", Place, ok, fail);
        await Run("saved sessions: exit file taken once; named workspaces saved, renamed, replaced and archived on removal; quick-connect hosts stored without their secret", Store, ok, fail);
        await Run("saved sessions: exit asks Save and exit / Exit without saving / Cancel; Don't ask again sets the setting; Never skips saving", Exit, ok, fail);
        await Run("saved sessions: tabs reopen (a local shell with its folder, a quick-connect PRODUCTION terminal left disconnected) and are captured again", Tabs, ok, fail);
    }

    private static async Task Run(string name, Func<Task> test, Action<string> ok, Action<string, Exception?> fail)
    {
        try { await test(); ok(name); }
        catch (Exception ex) { fail(name, ex); }
    }

    private static void Expect(bool ok, string what) { if (!ok) throw new Exception(what); }

    private static Task Place()
    {
        const string E = "\x1b";
        Expect(ShellPlace.FromOutput($"x{E}]7;file://web-01/srv/my%20site{E}\\\r\nops@web-01:~$ ", "posix") == "/srv/my site", "OSC 7 (ST, percent-encoded) wins");
        Expect(ShellPlace.FromOutput($"ops@web-01:/tmp$ ls\r\n{E}[01;32mops@web-01{E}[00m:{E}[01;34m/var/log/nginx{E}[00m$ ", "posix") == "/var/log/nginx", "the last coloured Debian prompt");
        Expect(ShellPlace.FromOutput("root@db:~/backups# ", "posix") == "~/backups", "a root prompt under home");
        Expect(ShellPlace.FromOutput("(venv) ops@web-01:~$ ", "posix") == "~", "a virtualenv prefix");
        Expect(ShellPlace.FromOutput("[ops@rhel-01 nginx]$ ", "posix") == null, "Red Hat's prompt only shows the folder's name");
        Expect(ShellPlace.FromOutput("PS C:\\Users\\ops\\Projects> ", "powershell") == @"C:\Users\ops\Projects", "a PowerShell prompt");
        Expect(ShellPlace.FromOutput("ops@web-01:/srv$ ", "") == null, "no shell, no folder");
        Expect(ShellPlace.ChangeDirLine("/srv/it's here", "posix") == " cd -- '/srv/it'\\''s here'", "POSIX quoting");
        Expect(ShellPlace.ChangeDirLine("~/a b", "posix") == " cd -- ~/'a b'", "under home");
        Expect(ShellPlace.ChangeDirLine("~", "posix") == " cd ~", "home");
        Expect(ShellPlace.ChangeDirLine(@"C:\it's", "powershell") == @"Set-Location -LiteralPath 'C:\it''s'", "PowerShell quoting");
        Expect(ShellPlace.ChangeDirLine("relative/path", "posix") == null && ShellPlace.ChangeDirLine("/a\nb", "posix") == null, "only absolute paths, no control characters");
        Expect(ShellPlace.LeftTmux("[detached (from session main)]\r\n") && ShellPlace.LeftTmux($"{E}[?1049l[exited]\r\n") && !ShellPlace.LeftTmux("[ok] done"), "tmux detach / exit");
        return Task.CompletedTask;
    }

    private static Task Store()
    {
        Workspace.TakeSavedOnExit();
        Workspace.SaveOnExit(new[] { new Workspace.Entry("local", "cmd") { Cwd = @"C:\Temp" } });
        var first = Workspace.TakeSavedOnExit();
        Expect(first.Count == 1 && first[0].Cwd == @"C:\Temp", "saved at exit, read back");
        Expect(Workspace.TakeSavedOnExit().Count == 0, "taken once");

        foreach (var w in Workspace.AllNamed()) Workspace.RemoveNamed(w.Name);
        Workspace.SaveNamed("Homelab", new[] { new Workspace.Entry("local", "cmd") });
        Workspace.SaveNamed("homelab", new[] { new Workspace.Entry("local", "cmd"), new Workspace.Entry("local", "powershell") });
        Expect(Workspace.AllNamed().Count == 1 && Workspace.AllNamed()[0].Entries.Count == 2, "same name (any case) replaces");
        Workspace.RenameNamed("Homelab", "VPS");
        Expect(Workspace.AllNamed().Single().Name == "VPS", "renamed");
        Workspace.RemoveNamed("vps");
        Expect(Workspace.AllNamed().Count == 0, "removed from the menu");
        var file = SecureStore.Read<Dictionary<string, JsonElement>>("workspaces.dcx");
        var archived = file.FirstOrDefault(kv => kv.Key.Equals("Archived", StringComparison.OrdinalIgnoreCase)).Value;
        Expect(archived.ValueKind == JsonValueKind.Array && archived.GetArrayLength() == 1, "kept in the archive, not deleted");

        const string Secret = "probe-Secret-0629";
        var adHoc = new HostEntry { Name = "ops@192.0.2.29", Address = "192.0.2.29", Username = "ops", IsAdHoc = true, Auth = AuthMethod.Password, Transient = new Credential { Secret = Secret } };
        var entry = new Workspace.Entry("terminal", adHoc.Id) { AdHoc = adHoc.Clone() };
        Expect(!JsonSerializer.Serialize(new[] { entry }, SecureStore.Json).Contains(Secret), "the secret is never serialised");
        Workspace.SaveOnExit(new[] { entry });
        Expect(!File.ReadAllText(Path.Combine(SecureStore.DataDir, "workspace-exit.dcx")).Contains("192.0.2.29"), "the file is encrypted");
        var back = Workspace.TakeSavedOnExit().Single().AdHoc;
        Expect(back is { Address: "192.0.2.29", Username: "ops", Transient: null }, "the quick-connect host comes back without a secret");
        return Task.CompletedTask;
    }

    private static Task Exit()
    {
        var main = MainWindow.Instance;
        var s = AppSettings.Current;
        var (mode, prior) = (s.SessionsOnExit, Dialogs.ChooseForTests);
        var entries = new[] { new Workspace.Entry("local", "cmd") };
        try
        {
            Workspace.TakeSavedOnExit();
            s.SessionsOnExit = "ask";
            Dialogs.ChooseForTests = (2, false);
            Expect(!main.ExitDecision(entries, 1) && Workspace.TakeSavedOnExit().Count == 0, "Cancel stays and saves nothing");
            Dialogs.ChooseForTests = (1, false);
            Expect(main.ExitDecision(entries, 1) && Workspace.TakeSavedOnExit().Count == 0 && s.SessionsOnExit == "ask", "Exit without saving");
            Dialogs.ChooseForTests = (0, true);
            Expect(main.ExitDecision(entries, 1) && Workspace.TakeSavedOnExit().Count == 1, "Save and exit saves");
            Expect(s.SessionsOnExit == "save", "Don't ask again remembers Save");
            Dialogs.ChooseForTests = null;
            Expect(main.ExitDecision(entries, 1) && Workspace.TakeSavedOnExit().Count == 1, "\"save\" saves without asking");
            s.SessionsOnExit = "never";
            Expect(main.ExitDecision(entries, 0) && Workspace.TakeSavedOnExit().Count == 0, "\"never\" doesn't save");
            Expect(MainWindow.ExitMessage(entries, 1).StartsWith("1 tab is open (1 connected session). Save them?"), "the question");
        }
        finally { s.SessionsOnExit = mode; Dialogs.ChooseForTests = prior; }
        return Task.CompletedTask;
    }

    private static async Task Tabs()
    {
        var main = MainWindow.Instance;
        var ps = LocalShells.Find("powershell") ?? throw new Exception("Windows PowerShell not discovered");
        var adHoc = new HostEntry { Name = "ops@192.0.2.30", Address = "192.0.2.30", Username = "ops", Environment = "PRODUCTION", Auth = AuthMethod.Password };
        var before = main.Panes.AllTabs.ToHashSet();
        var cwd = Path.GetTempPath().TrimEnd('\\');
        main.RestoreTabs(new[]
        {
            new Workspace.Entry("local", ps.Id) { Cwd = cwd, Group = "saved-test" },
            new Workspace.Entry("terminal", adHoc.Id) { AdHoc = adHoc, Group = "saved-test" },
        }, connect: true);
        var mine = main.Panes.AllTabs.Where(t => !before.Contains(t)).ToList();
        try
        {
            await Task.Delay(300);
            var local = mine.Select(t => t.Content).OfType<TerminalView>().FirstOrDefault(v => v.Target is LocalTarget) ?? throw new Exception("the local shell didn't reopen");
            var remote = mine.Select(t => t.Content).OfType<TerminalView>().FirstOrDefault(v => v.Host?.IsAdHoc == true) ?? throw new Exception("the quick-connect terminal didn't reopen");
            Expect(!remote.IsConnected, "a PRODUCTION host waits for ↻");
            var saved = main.WorkspaceEntries().Where(e => e.Group == "saved-test").ToList();
            Expect(saved.Count == 2, $"captured again ({saved.Count})");
            Expect(saved.Any(e => e.Kind == "terminal" && e.AdHoc is { Address: "192.0.2.30", Environment: "PRODUCTION" }), "the quick-connect host is captured with its settings");
            Expect(saved.Any(e => e.Kind == "local" && (e.Cwd == cwd || local.IsConnected)), "the shell's folder is kept until it shows its own prompt");
            // Compact groups: the name is printed once per run of the group's tabs, the rest only show its colour line.
            var grouped = mine.Where(t => TabGroups.GetName(t) == "saved-test").ToList();
            Expect(grouped.Count == 2 && main.Panes.PaneOf(grouped[0]) == main.Panes.PaneOf(grouped[1]), "both in one strip");
            Expect(grouped.Count(TabGroups.GetLead) == 1, "the group's name is printed once");
        }
        finally { foreach (var t in mine) main.CloseTab(t); }
    }
}
