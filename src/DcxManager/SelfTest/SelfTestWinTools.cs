using System.Windows.Controls;
using DcxManager.Services;
using DcxManager.Views;
using DcxManager.WinTools;
using DcxManager.WinTools.Views;
using DcxManager.WinTools.Wmi;

namespace DcxManager;

/// <summary>
/// ADR-119 (Windows management built in). The switch (its rule, protection and policies), the settings page, the wmic
/// translation and what the console refuses, and, against this PC through the real screen, the snap-ins (overview,
/// services, processes, volumes, the System event log, the WMI console). A change is only ever asked for, never made:
/// Stop on a service reaches the confirmation, which the self-test refuses, and the service is still running afterwards.
/// No other computer, window or dialog.
/// </summary>
internal static class WinToolsTests
{
    public static async Task RunAsync(MainWindow main, Action<string> ok, Action<string, Exception?> fail)
    {
        try
        {
            var d = WinToolsFeatures.Decide(false, "1", true);
            if (d.On || d.Source != WinToolsFeatures.From.Policy) throw new Exception("policy doesn't win");
            if (!WinToolsFeatures.Decide(null, "1", false).On || WinToolsFeatures.Decide(null, "0", true).On || !WinToolsFeatures.Decide(null, null, true).On) throw new Exception("env / setting order");
            if (!new AppSettings().WinToolsFeatures || !new AppSettings().WinToolsAllowChanges) throw new Exception("a new install doesn't have Windows management on");
            foreach (var key in new[] { "wintools.features", "wintools.changes" })
                if (ProtectedSettings.Items.FirstOrDefault(i => i.Key == key) is not { Setting: not null }) throw new Exception("not admin-protected: " + key);
            foreach (var p in new[] { nameof(AppSettings.WinToolsFeatures), nameof(AppSettings.WinToolsAllowChanges), nameof(AppSettings.WinToolsAllowRemote), nameof(AppSettings.WinToolsAllowConsole), nameof(AppSettings.WinToolsAllowSecurityLog), nameof(AppSettings.WinToolsEventLimit) })
                if (!PolicySettings.Catalog.Any(e => e.Name == p)) throw new Exception("not a policy: " + p);
            if (!SettingsView.Categories.Any(c => c.Name == "Windows management")) throw new Exception("no Windows management settings page");
            foreach (var card in new[] { "Windows management", "Windows management rules" })
                if (SettingsView.CategoryOfForTest(card) != "Windows management") throw new Exception("card not on its page: " + card);
            bool on = WinToolsFeatures.Current.On;
            bool loaded = AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "Dcx.WinTools");
            if (!on && loaded) throw new Exception("Dcx.WinTools loaded with Windows management off");
            if (on != AppModules.NavItemsIn("WINDOWS").Any()) throw new Exception("the WINDOWS section doesn't follow the switch");
            ok($"Windows management switch (ADR-119): machine policy beats DCX_WINTOOLS_FEATURES beats the setting (new installs on); on/off and changes are admin-protected; 6 policies; its own Settings page; this run {(on ? "loaded" : "didn't load")} it");
            if (!on) return;
        }
        catch (Exception ex) { fail("Windows management switch", ex); return; }

        try { ok("Windows management wmic commands (ADR-119): " + Wmic()); }
        catch (Exception ex) { fail("Windows management wmic commands", ex); }

        try { ok("Windows management console on this PC (ADR-119): " + await Live(main)); }
        catch (Exception ex) { fail("Windows management console on this PC", ex); }
    }

    private static string Wmic()
    {
        void Is(string line, string wql, string ns = @"root\cimv2")
        {
            var p = WmicCommand.Parse(line);
            if (p.Error is not null || p.Wql != wql || p.Namespace != ns) throw new Exception($"“{line}” → {p.Error ?? p.Wql} ({p.Namespace})");
        }
        void Refused(string line, string why)
        {
            var p = WmicCommand.Parse(line);
            if (p.Error is null) throw new Exception($"“{line}” wasn't refused ({why}): {p.Wql}");
        }
        Is("os get caption,version", "SELECT caption, version FROM Win32_OperatingSystem");
        Is("wmic service where state=\"running\" get name,startname", "SELECT name, startname FROM Win32_Service WHERE state=\"running\"");
        Is("process where (name='svchost.exe') get processid", "SELECT processid FROM Win32_Process WHERE name='svchost.exe'");
        Is("logicaldisk list brief", "SELECT DeviceID, DriveType, FileSystem, FreeSpace, Size, VolumeName FROM Win32_LogicalDisk");
        Is("path Win32_BIOS", "SELECT * FROM Win32_BIOS");
        Is(@"/namespace:\\root\wmi path MSStorageDriver_FailurePredictStatus get active", "SELECT active FROM MSStorageDriver_FailurePredictStatus", @"root\wmi");
        Is("SELECT Name FROM Win32_Service WHERE StartMode = 'Auto'", "SELECT Name FROM Win32_Service WHERE StartMode = 'Auto'");
        Refused("service where name='spooler' call stopservice", "call");
        Refused("useraccount where name='x' set disabled=true", "set");
        Refused("process delete", "delete");
        Refused("/node:server1 os get caption", "node");
        Refused("/user:admin os", "credentials");
        Refused("SELECT * FROM Win32_Process; SELECT * FROM Win32_Service", "two statements");
        Refused("os get caption;version", "bad property");
        Refused("nosuchalias get x", "unknown alias");
        foreach (var bad in new[] { "DELETE FROM Win32_Process", "", "  ", "select * from x\0" })
            if (WmicCommand.IsReadOnlyWql(bad)) throw new Exception("read-only WQL accepted: " + bad);
        foreach (var bad in new[] { "a'b", "a\\b", "a\"b", "" }) if (Snapins.SafeKey(bad)) throw new Exception("unsafe key accepted: " + bad);
        if (WinTarget.ValidateComputer("server-01.corp.example") is not null || WinTarget.ValidateComputer("a b") is null || WinTarget.ValidateComputer("\\\\x") is null) throw new Exception("computer names");
        return $"{WmicCommand.Aliases.Count} aliases; get / where / list brief / path / namespace become WQL; call, set, delete, /node:, /user:, two statements and unknown aliases are refused; only reading WQL runs; quotes and backslashes never reach a WMI path";
    }

    private static async Task<string> Live(MainWindow main)
    {
        WinToolsModule.Open();
        var tab = main.Panes.AllTabs.FirstOrDefault(t => t.Content is WinToolsView) ?? throw new Exception("Windows management didn't open as a tab");
        var v = (WinToolsView)tab.Content;
        try
        {
            if (!v.TargetForTest.IsLocal) throw new Exception("it doesn't start on this PC");
            var tree = v.TreeForTest;
            foreach (var id in new[] { "overview", "services", "processes", "startup", "drivers", "updates", "ev-system", "ev-application", "disks", "volumes", "shares", "users", "groups", "network", "tasks", Snapins.ConsoleId, Snapins.ClassicId })
                if (!tree.Contains(id)) throw new Exception("the tree has no " + id);

            async Task<WmiTable> Show(string id, int atLeast)
            {
                var (t, status) = await v.ShowForTest(id);
                if (t.Rows.Count < atLeast) throw new Exception($"{id}: {t.Rows.Count} row(s) — {status}");
                return t;
            }
            var ov = await Show("overview", 4);
            if (!ov.Rows.Any(r => r[0] == "Windows" && r[1].Contains("Windows", StringComparison.OrdinalIgnoreCase))) throw new Exception("the overview has no Windows version");
            var svc = await Show("services", 20);
            int iName = svc.Col("Name"), iState = svc.Col("State");
            if (!svc.Rows.Any(r => r[iName].Equals("Winmgmt", StringComparison.OrdinalIgnoreCase) && r[iState] == "Running")) throw new Exception("no running Winmgmt service");
            var proc = await Show("processes", 5);
            if (!proc.Rows.Any(r => r[proc.Col("ProcessId")] == Environment.ProcessId.ToString())) throw new Exception("this process isn't listed");
            var vol = await Show("volumes", 1);
            var ev = await Show("ev-system", 1);
            await Show("network", 0);
            var tasks = await Show("tasks", 0);

            // The filter narrows the grid.
            await v.ShowForTest("services");
            v.FilterForTest("Winmgmt");
            if (v.RowsShownForTest is 0 or > 3) throw new Exception("the filter shows " + v.RowsShownForTest);

            // A change is asked for and refused: Stop on Winmgmt reaches the confirmation, nothing is stopped.
            if (!v.ActionsForTest.Any(a => a.Label.Contains("Stop") && a.Enabled)) throw new Exception("Stop isn't offered with changes allowed");
            v.SelectRowForTest(0);
            int before; lock (Dialogs.SelfTestPrompts) before = Dialogs.SelfTestPrompts.Count;
            await v.RunActionForTest("stop");
            lock (Dialogs.SelfTestPrompts)
            {
                if (Dialogs.SelfTestPrompts.Count != before + 1 || !Dialogs.SelfTestPrompts[^1].StartsWith("confirmation: Services", StringComparison.Ordinal)) throw new Exception("Stop didn't ask first");
                Dialogs.SelfTestPrompts.RemoveAt(Dialogs.SelfTestPrompts.Count - 1);   // expected here
            }
            var again = await v.TargetForTest.QueryAsync("SELECT State FROM Win32_Service WHERE Name = 'Winmgmt'");
            if (again.Rows.FirstOrDefault()?[0] != "Running") throw new Exception("Winmgmt isn't running after a refused Stop");
            var s = AppSettings.Current;
            var was = s.WinToolsAllowChanges;
            try
            {
                s.WinToolsAllowChanges = false;
                await v.ShowForTest("services");
                if (v.ActionsForTest.Any(a => a.Enabled && !a.Label.Contains("Open"))) throw new Exception("changes offered with WinToolsAllowChanges off");
            }
            finally { s.WinToolsAllowChanges = was; }

            // The WMI console: wmic and WQL run; what changes is refused before anything runs.
            var (os, wql) = await v.ConsoleForTest("os get caption,version");
            if (os.Rows.Count != 1 || !wql.Contains("SELECT caption, version FROM Win32_OperatingSystem")) throw new Exception("console: " + wql);
            var (q2, _) = await v.ConsoleForTest("SELECT Name, State FROM Win32_Service WHERE Name = 'Winmgmt'");
            if (q2.Rows.Count != 1) throw new Exception("console WQL rows: " + q2.Rows.Count);
            var (q3, w3) = await v.ConsoleForTest("service where name='winmgmt' call stopservice");
            if (q3.Rows.Count != 0 || !w3.Contains("only reads")) throw new Exception("console didn't refuse call: " + w3);
            var (_, w4) = await v.ConsoleForTest("path Win32_NoSuchClass");
            if (!w4.Contains("doesn't exist") && !w4.Contains("not valid")) throw new Exception("an unknown class: " + w4);

            // Remote targets follow the policy.
            var r = s.WinToolsAllowRemote;
            try
            {
                s.WinToolsAllowRemote = false;
                try { WinTarget.ForComputer("server-01.corp.example"); throw new Exception("a remote target with WinToolsAllowRemote off"); }
                catch (InvalidOperationException) { }
            }
            finally { s.WinToolsAllowRemote = r; }

            return $"opened as a tab on this PC; {tree.Count} snap-ins; overview ({ov.Rows.Count} facts), {svc.Rows.Count} services (Winmgmt running), {proc.Rows.Count} processes (this one listed), {vol.Rows.Count} volume(s), {ev.Rows.Count} System events, {tasks.Rows.Count} scheduled tasks; the filter narrows; Stop asks first (refused here, Winmgmt still running) and isn't offered when changes are off; the console ran wmic and WQL, refused call and explained an unknown class; remote follows its policy";
        }
        finally { main.CloseTab(tab); }
    }
}
