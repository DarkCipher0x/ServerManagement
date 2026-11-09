using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using DcxManager.Services;
using DcxManager.Views;
using Microsoft.Win32;

namespace DcxManager;

/// <summary>
/// 0.6.37 (ADR-116): history. A fresh store keeps none; while it is off no history writes anything (quick connect, the
/// palette, Toolbox, "last connected", portals); on, each kind is kept, counted and cleared on its own or all at once;
/// the automatic clear runs by age and when the app closes; turning it off and a start while it is off clear what was
/// kept; clearing is audited without the entries; Group Policy sets it; Settings → History and the quick connect bar are
/// driven through their own controls (no window or pop-up opens).
/// </summary>
public static class HistoryTests
{
    /// <summary>Whether the store started with history off (read before the suite turns it on for the older sections).</summary>
    public static bool FreshStoreOff { get; set; }

    private static void Expect(bool condition, string what) { if (!condition) throw new Exception(what); }

    public static Task RunAsync(MainWindow main, Action<string> ok, Action<string, Exception?> fail)
    {
        var s = AppSettings.Current;
        var d = AppData.Current;
        var saved = (s.HistorySave, s.HistoryAutoClear, s.HistoryLastCleared, d.RecentAdHoc.ToList(), s.PaletteRecent.ToList(), d.ToolboxHistory.ToList());
        var host = new HostEntry { Name = "selftest-history", Address = "192.0.2.77", Username = "ops" };
        int cleared = 0;
        void OnCleared(string? _) => cleared++;
        ActivityHistory.Cleared += OnCleared;
        try
        {
            // Defaults: off, never cleared by itself; the fresh store started with none.
            Expect(!new AppSettings().HistorySave && new AppSettings().HistoryAutoClear == ActivityHistory.AutoClear.Never, "the defaults keep history");
            Expect(FreshStoreOff, "a fresh store started with history on");
            var ids = ActivityHistory.Sources.Select(x => x.Id).ToList();
            foreach (var want in new[] { "quick-connect", "palette", "last-connected", "toolbox", "transfers", "portals-opened", "portal-pages" })
                Expect(ids.Contains(want), "not registered: " + want);
            Expect(Throws(() => ActivityHistory.Register(new("Bad Id!", "x", "x", () => 0, () => { }))), "a malformed history id was accepted");

            // Off: nothing is written.
            ActivityHistory.SetSaving(false);
            d.RecentAdHoc.Clear(); s.PaletteRecent.Clear(); d.ToolboxHistory.Clear();
            QuickTargets.Remember("ssh selftest-off@192.0.2.78");
            ToolboxRun.Record("TCP port", "192.0.2.78:22", "open", "report");
            ActivityHistory.NoteConnected(host);
            Expect(d.RecentAdHoc.Count == 0 && d.ToolboxHistory.Count == 0 && host.LastConnected == null && !ActivityHistory.MayRecord(), "history was written while off");
            Expect(main.QuickRowsForTest("").All(r => !r.Contains("selftest-off")) && main.QuickFooterForTest().Contains("History is off"), "the quick connect bar didn't say history is off: " + main.QuickFooterForTest());

            // On: kept, counted, cleared one kind at a time (audited without the entries).
            ActivityHistory.SetSaving(true);
            QuickTargets.Remember("ssh selftest-secret-line@192.0.2.79");
            QuickTargets.Remember("rdp://192.0.2.80");
            ToolboxRun.Record("DNS", "selftest.example", "ok", "report");
            ActivityHistory.NoteConnected(host);
            Expect(d.RecentAdHoc.Count == 2 && d.ToolboxHistory.Count == 1 && host.LastConnected != null, "history wasn't kept while on");
            Expect(ActivityHistory.Find("quick-connect")!.Count() == 2 && ActivityHistory.Total >= 3, "counts: " + ActivityHistory.Total);
            Expect(main.QuickFooterForTest().Contains("Clear recent (2)"), "no Clear recent in the quick connect bar: " + main.QuickFooterForTest());
            main.QuickFooterClearForTest();
            Expect(d.RecentAdHoc.Count == 0 && d.ToolboxHistory.Count == 1, "Clear recent cleared more (or less) than the recents");
            var audit = AuditLog.Read(DateTime.Now.ToString("yyyy-MM")).Where(e => e.Target == "history").ToList();
            Expect(audit.Any(e => e.Target == "history" && e.Result.Contains("Quick connect recents (2)")), "clearing wasn't audited");
            Expect(!audit.Any(e => (e.Result + e.Action).Contains("selftest-secret-line")), "the audit holds a history entry");
            QuickTargets.Remember("ssh one@192.0.2.81"); QuickTargets.Remember("ssh two@192.0.2.82");
            QuickTargets.Forget("ssh one@192.0.2.81");
            Expect(d.RecentAdHoc.SequenceEqual(new[] { "ssh two@192.0.2.82" }), "Shift+Del forgot the wrong line");

            // Automatic clear: by age (checked whenever something is remembered) and when the app closes.
            s.HistoryAutoClear = ActivityHistory.AutoClear.Daily;
            s.HistoryLastCleared = DateTime.UtcNow.AddDays(-2);
            int before = cleared;
            QuickTargets.Remember("ssh after@192.0.2.83");
            Expect(cleared == before + 1 && d.ToolboxHistory.Count == 0 && d.RecentAdHoc.SequenceEqual(new[] { "ssh after@192.0.2.83" }), "the daily clear didn't run before remembering");
            Expect(ActivityHistory.NextClearUtc is { } due && due > DateTime.UtcNow.AddHours(23), "the next clear isn't a day away");
            s.HistoryAutoClear = ActivityHistory.AutoClear.OnExit;
            ActivityHistory.AtExit();
            Expect(d.RecentAdHoc.Count == 0, "\"when the app closes\" didn't clear");
            s.HistoryAutoClear = ActivityHistory.AutoClear.Never;

            // Off again clears; a start while off clears what another version or a restore left.
            QuickTargets.Remember("ssh left@192.0.2.84");
            ActivityHistory.SetSaving(false);
            Expect(d.RecentAdHoc.Count == 0 && !s.HistorySave, "turning history off kept it");
            d.RecentAdHoc.Add("ssh restored@192.0.2.85"); s.PaletteRecent.Add("page:hosts");
            ActivityHistory.AtStart();
            Expect(d.RecentAdHoc.Count == 0 && s.PaletteRecent.Count == 0, "a start while off kept history");

            // Group Policy: on, and the automatic clear, from the registry; a value outside the choices is refused.
            PolicySettings.SourceForTest = hive => hive == "HKLM"
                ? new Dictionary<string, (object, RegistryValueKind)> { [nameof(AppSettings.HistorySave)] = (1, RegistryValueKind.DWord), [nameof(AppSettings.HistoryAutoClear)] = ("7", RegistryValueKind.String) }
                : null;
            PolicySettings.Reload();
            AppSettings.Save();
            Expect(s.HistorySave && s.HistoryAutoClear == "7" && PolicySettings.IsManaged(nameof(AppSettings.HistorySave)), "the policy isn't in force");
            PolicySettings.SourceForTest = hive => hive == "HKLM" ? new Dictionary<string, (object, RegistryValueKind)> { [nameof(AppSettings.HistoryAutoClear)] = ("forever", RegistryValueKind.String) } : null;
            PolicySettings.Reload();
            Expect(!PolicySettings.IsManaged(nameof(AppSettings.HistoryAutoClear)) && PolicySettings.Problems.Any(p => p.Contains("HistoryAutoClear")), "a bad automatic-clear value was taken");
            PolicySettings.SourceForTest = _ => null;
            PolicySettings.Reload();

            // Settings → Security & privacy → History, through its switch and buttons.
            ActivityHistory.SetSaving(false);
            main.Navigate("settings");
            var sv = SettingsView.Current ?? throw new Exception("Settings didn't open");
            sv.RevealCard(SettingsView.HistoryCardTitle);
            var card = sv.CardForTest(SettingsView.HistoryCardTitle) ?? throw new Exception("no History card");
            var keep = Find<CheckBox>(card).First(c => (c.Content as TextBlock)?.Text.StartsWith("Keep history", StringComparison.Ordinal) == true);
            Expect(keep.IsChecked == false, "the switch shows on");
            keep.IsChecked = true;
            Expect(s.HistorySave, "the switch didn't turn history on");
            QuickTargets.Remember("ssh card@192.0.2.86");
            var clearQuick = Find<Button>(card).FirstOrDefault(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Clear Quick connect recents") ?? throw new Exception("no Clear button for quick connect");
            clearQuick.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Expect(d.RecentAdHoc.Count == 0, "the card's Clear didn't clear");
            Expect(Find<Button>(card).Count(b => (b.Content as string) == "Clear") == ActivityHistory.Sources.Count, "a history has no Clear button on the card");
            keep.IsChecked = false;
            Expect(!s.HistorySave, "the switch didn't turn history off");
            Expect(KeyCommandsHave("history.clear") && KeyCommandsHave("history.settings"), "history commands missing from the palette");
            ok($"history (0.6.37): off by default and on a fresh store, {ActivityHistory.Sources.Count} kinds registered (quick connect, palette, last connected, Toolbox, transfers, portals, portal pages); nothing written while off; kept, counted and cleared one kind or all (audited without entries); Shift+Del forgets one; cleared by age, on exit, when turned off and at a start while off; Group Policy sets it and refuses a bad value; Settings → History and the quick connect bar driven through their controls");
        }
        catch (Exception ex) { fail("history (0.6.37)", ex); }
        finally
        {
            ActivityHistory.Cleared -= OnCleared;
            PolicySettings.SourceForTest = _ => null;
            PolicySettings.Reload();
            (s.HistorySave, s.HistoryAutoClear, s.HistoryLastCleared) = (saved.HistorySave, saved.HistoryAutoClear, saved.HistoryLastCleared);
            d.RecentAdHoc = saved.Item4; s.PaletteRecent = saved.Item5; d.ToolboxHistory = saved.Item6;
            try { AppSettings.Save(); AppData.Save(); } catch { }
            if (main.Panes.AllTabs.FirstOrDefault(t => t.Content is SettingsView) is { } st) main.CloseTab(st);
        }
        return Task.CompletedTask;
    }

    private static bool KeyCommandsHave(string id) => Services.Keys.KeyCommands.Find(id) != null;

    private static bool Throws(Action a) { try { a(); return false; } catch { return true; } }

    private static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T t) yield return t;
            foreach (var x in Find<T>(child)) yield return x;
        }
    }
}
