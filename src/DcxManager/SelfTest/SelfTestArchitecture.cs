using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using DcxManager.Services;
using DcxManager.Views;

namespace DcxManager;

/// <summary>
/// 0.2.0 phase 1 (docs/history/PLAN-0.2.0.md): the code below the screens reaches the user and the window only through the seams
/// in <see cref="Ui"/> and logs through <see cref="Log"/>. These tests check that the WPF side is registered and forwards,
/// that the UI-thread seam behaves from both threads, and — when the source tree is next to the exe (a Debug build) —
/// that no file below the screens has gone back to calling the UI directly. No window or dialog is shown.
/// </summary>
public static class ArchitectureTests
{
    /// <summary>Folders below the screens. Everything here must compile without the UI once the projects are split (phase 6).</summary>
    private static readonly string[] LowerFolders = { "Services", "Ai", "Addons", "Api", "Cloud", "Infra", "Ssh", "Files", "Sessions", "Library", "Browser", "Notes", "Incidents", "Rdp", "Vultr", "Hetzner", "Contabo", "Documents", "Reports" };

    /// <summary>Known UI touches still below the screens: the app lock's window hooks (AppLock) and tab access (TabGroups),
    /// both in the app project. Nothing may be added here. (Environments and the terminal bridge left the list in 6b; the
    /// theme, typography, density, UI error guard and Windows Share are the Dcx.Ui kit since 6c; AddonApi opens screens
    /// through hooks since 6e.)</summary>
    private static readonly HashSet<string> Leftovers = new(StringComparer.OrdinalIgnoreCase)
    {
        "AppLock.cs", "TabGroups.cs",
    };

    /// <summary>Projects that are UI by design (WPF kit): not part of the "below the screens" scan.</summary>
    private static readonly HashSet<string> UiProjects = new(StringComparer.OrdinalIgnoreCase) { "Dcx.Ui" };

    /// <summary>The operator's built-ins (0.1.18 `Assistant`), in schema order; add-ons and models know them by these names.</summary>
    private static readonly string[] OperatorNames =
    {
        "list_hosts", "host_overview", "read_logs", "list_services", "service_action", "run_command", "vultr_list_instances", "vultr_instance_action", "vultr_deploy_server", "vultr_get",
        "cloud_list_instances", "cloud_instance_action", "list_files", "read_file", "transfer_files", "make_directory", "rename_path", "delete_path",
    };

    private static readonly Regex Forbidden = new(@"\bMainWindow\.Instance\b|(?<![\w.])Dialogs\.(Prompt|Confirm|Choose|Info|Error|ShowText|Pick)\(|\bApp\.Log\(|\bApplication\.Current\b|\bDispatcherTimer\b|\bDispatcher\.(Invoke|BeginInvoke|InvokeAsync|CheckAccess)\b", RegexOptions.Compiled);

    public static async Task RunAsync(MainWindow main, Action<string> ok, Action<string, Exception?> fail)
    {
        // 1. The WPF side is registered and the UI-thread seam behaves on and off the UI thread.
        try
        {
            if (!Ui.Thread.HasUi || !Ui.Thread.CheckAccess()) throw new Exception("UI thread seam not registered or not on the UI thread");
            if (Ui.Dialogs.GetType().Name is not "WpfDialogs" || Ui.Window.GetType().Name is not "WpfWindowShell") throw new Exception($"seams: {Ui.Dialogs.GetType().Name} / {Ui.Window.GetType().Name}");
            bool offUi = await Task.Run(() => Ui.Thread.CheckAccess());
            if (offUi) throw new Exception("CheckAccess true on a worker thread");
            int onUiInvoke = await Task.Run(() => Ui.Thread.Invoke(() => Environment.CurrentManagedThreadId));
            if (onUiInvoke != Environment.CurrentManagedThreadId) throw new Exception("Invoke did not run on the UI thread");
            var posted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            await Task.Run(() => Ui.Thread.Post(() => posted.TrySetResult(Environment.CurrentManagedThreadId)));
            if (await Task.WhenAny(posted.Task, Task.Delay(3000)) != posted.Task || posted.Task.Result != Environment.CurrentManagedThreadId) throw new Exception("Post did not run on the UI thread");
            // A background-priority UI timer: wait for three ticks (the UI thread may be busy, so no fixed timing), then it must stop on dispose.
            int ticks = 0;
            using (Ui.Thread.Every(TimeSpan.FromMilliseconds(30), () => { if (!Ui.Thread.CheckAccess()) ticks = -1000; ticks++; }))
                for (int i = 0; i < 100 && ticks < 3; i++) await Task.Delay(30);
            int after = ticks; await Task.Delay(200);
            if (ticks < 3 || ticks != after) throw new Exception($"Every: {ticks} ticks, {after} at dispose");
            if (!Ui.Thread.TryInvoke(() => { }, TimeSpan.FromSeconds(1))) throw new Exception("TryInvoke on the UI thread");
            var slow = await Task.Run(() => Ui.Thread.TryInvoke(() => Thread.Sleep(10), TimeSpan.FromSeconds(5)));
            if (!slow) throw new Exception("TryInvoke from a worker thread");

            // The dialog seam forwards to Views.Dialogs, which refuses every dialog in the self-test and records it; the
            // record is removed again here because this one was expected.
            int before; lock (Dialogs.SelfTestPrompts) before = Dialogs.SelfTestPrompts.Count;
            var answer = Ui.Dialogs.Prompt("architecture seam probe", "never shown");
            bool recorded; lock (Dialogs.SelfTestPrompts) { recorded = Dialogs.SelfTestPrompts.Count == before + 1 && Dialogs.SelfTestPrompts[^1] == "prompt: architecture seam probe"; if (recorded) Dialogs.SelfTestPrompts.RemoveAt(Dialogs.SelfTestPrompts.Count - 1); }
            if (answer != null || !recorded) throw new Exception("the dialog seam did not reach Views.Dialogs' self-test refusal");

            // The window seam reaches the real window.
            var was = main.StatusText.Text;
            Ui.Window.Status("architecture seam probe");
            bool shown = main.StatusText.Text == "architecture seam probe";
            Ui.Window.Status(was);
            if (!shown) throw new Exception("Status did not reach the main window");

            // Log.Write is the error log; App.Log forwards to it.
            int errors = Log.ErrorsThisSession;
            var path = App.Log("architecture seam probe", new InvalidOperationException("probe"));
            if (Log.ErrorsThisSession != errors + 1 || !File.Exists(path) || Path.GetDirectoryName(path) != Log.Dir || App.LogDir != Log.Dir) throw new Exception("App.Log does not forward to Log.Write");
            ok("UI seams (0.2.0 phase 1): WPF dialogs / window / UI thread registered; CheckAccess, Invoke, Post, Every and TryInvoke correct from the UI and a worker thread; a dialog through the seam is refused and recorded in the self-test; Status reaches the window; App.Log forwards to Log.Write (sealed error log)");
        }
        catch (Exception ex) { fail("UI seams", ex); }

        // 2. The capability registry (phase 2): the operator's built-ins are registry entries, first and in their old order;
        //    every 0.1.18 name is still there (the add-on fixture); risks and permissions are what add-ons were promised.
        try
        {
            var all = Capabilities.All;
            var names = all.Select(c => c.Name).ToList();
            if (!names.Take(OperatorNames.Length).SequenceEqual(OperatorNames)) throw new Exception("operator built-ins are not first / in order: " + string.Join(", ", names.Take(OperatorNames.Length)));
            if (all.Take(OperatorNames.Length).Any(c => !c.GatesItself)) throw new Exception("an operator built-in relies on the generic read gate");
            var schema = Capabilities.Schema().Select(t => t?["function"]?["name"]?.ToString()).ToList();
            if (!schema.SequenceEqual(names)) throw new Exception("schema order differs from the registry");
            if (Ai.Assistant.AllTools.Count != schema.Count) throw new Exception("Assistant.AllTools is not the registry schema");
            string? fixture = null;
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null && fixture == null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "docs", "baseline", "capabilities-0.1.18.txt"))) fixture = Path.Combine(d.FullName, "docs", "baseline", "capabilities-0.1.18.txt");
            int fixtureCount = 0;
            if (fixture != null)
            {
                var expected = File.ReadAllLines(fixture).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
                var missing = expected.Where(e => !names.Contains(e)).ToList();
                if (missing.Count > 0) throw new Exception("0.1.18 capabilities missing: " + string.Join(", ", missing));
                fixtureCount = expected.Count;
            }
            // Add-on view of the same registry: the 0.1.18 risk table for the built-ins, permissions by risk.
            var changes = new HashSet<string> { "run_command", "service_action", "vultr_instance_action", "vultr_deploy_server", "cloud_instance_action", "transfer_files", "make_directory", "rename_path", "delete_path" };
            foreach (var n in OperatorNames)
            {
                var risk = Addons.AddonApi.RiskOf(n) ?? throw new Exception("no risk for " + n);
                if ((risk == CapabilityRisk.Change) != changes.Contains(n)) throw new Exception($"risk of {n} changed: {risk}");
                if (Addons.AddonApi.PermissionFor(risk) != (risk == CapabilityRisk.Change ? "app.control" : "app.read")) throw new Exception("permission mapping for " + n);
            }
            if (Addons.AddonApi.RiskOf("terminal_read_screen") != CapabilityRisk.Private || Addons.AddonApi.RiskOf("no_such_tool") != null) throw new Exception("registry risk lookup");
            // A declined approval is reported the same way through the registry as the operator always did.
            var declined = await Capabilities.RunAsync("run_command", new System.Text.Json.Nodes.JsonObject { ["host"] = "no-such-host-7731", ["command"] = "id" }, new CapabilityContext((_, _, _) => Task.FromResult(false), "selftest"), CancellationToken.None)
                .ContinueWith(t => t.IsFaulted ? "ERR " + t.Exception!.InnerException!.Message : t.Result);
            if (!declined.StartsWith("ERR No saved host called")) throw new Exception("built-in did not run through the registry: " + declined);
            // Redaction moved to core; the AI client forwards to it.
            var sample = "password=hunter2 token: abcdef";
            if (Redaction.Mask(sample) != Ai.AiClient.Redact(sample) || Redaction.Mask(sample).Contains("hunter2") || Redaction.MaskCounted(sample).Masked != 2) throw new Exception("redaction: " + Redaction.Mask(sample));
            ok($"capability registry (0.2.0 phase 2): {all.Count} capabilities, the {OperatorNames.Length} operator built-ins first and in 0.1.18 order (each gating itself), schema = registry = Assistant.AllTools, all {fixtureCount} fixture names present, add-on risks and permissions unchanged (Private for terminal_read_screen), a built-in runs through Capabilities.RunAsync, Redaction.Mask == AiClient.Redact");
        }
        catch (Exception ex) { fail("capability registry", ex); }

        // 3. Modules (phase 3, first slice): the AI's sidebar items are contributions of the "ai" module and render as before.
        try
        {
            var mods = AppModules.All;
            if (mods.Count != 1 || mods[0].Id != "ai") throw new Exception("modules: " + string.Join(", ", mods.Select(m => m.Id)));
            var ai = AppModules.NavItemsIn("AI").ToList();
            if (ai.Select(n => n.Key).SequenceEqual(new[] { "aipanel", "assistant" }) == false) throw new Exception("AI nav items: " + string.Join(", ", ai.Select(n => n.Key)));
            if (ai[0].Open == null || ai[0].Menu == null || ai[1].Screen == null || ai[1].Header != "✦ AI operator") throw new Exception("AI nav contributions incomplete");
            if (!main.NavKeys.Contains("aipanel") || !main.NavKeys.Contains("assistant")) throw new Exception("sidebar lacks the module's items: " + string.Join(", ", main.NavKeys));
            if (ai[0].Menu!().Count() != 4) throw new Exception("AI Engineer sidebar menu");
            // The item opens its screen through the window as a singleton tab, exactly like a built-in key.
            int before = main.Panes.AllTabs.Count();
            main.Navigate("assistant");
            var tab = main.Panes.AllTabs.FirstOrDefault(t => t.Content is AssistantView) ?? throw new Exception("assistant did not open through the module");
            main.Navigate("assistant");
            if (main.Panes.AllTabs.Count() != before + 1) throw new Exception("not a singleton");
            main.CloseTab(tab);
            // Settings (slice 2): the AI's category sits after Cloud providers as before; its three cards are contributions
            // and render in Settings with their scope; CategoryOf knows them.
            var cats = SettingsView.Categories.Select(c => c.Name).ToList();
            var expectedCats = new[] { "Security & privacy", "Identity & directory", "Cloud providers", "AI & models", "Terminals & connections", "Appearance", "Navigation", "Data & backups", "Integrations", "Import & export", "About" };
            if (!cats.SequenceEqual(expectedCats)) throw new Exception("settings categories: " + string.Join(", ", cats));
            var aiCards = AppModules.SettingsCardsIn("AI & models").Select(c => c.Title).ToList();
            if (!aiCards.SequenceEqual(new[] { "AI engine", "AI speed, agents & memory", "AI permissions & safety" })) throw new Exception("AI cards: " + string.Join(", ", aiCards));
            if (SettingsView.CategoryOfForTest("AI engine") != "AI & models" || SettingsView.CategoryOfForTest("Snapshots") != "Data & backups") throw new Exception("CategoryOf");
            if (main.Panes.AllTabs.FirstOrDefault(t => t.Content is SettingsView) is { } st0) main.CloseTab(st0);
            main.Navigate("settings");
            var sv = SettingsView.Current ?? throw new Exception("no settings view");
            sv.RevealCategory("AI & models");
            if (!sv.VisibleCardsForTest.ToHashSet().SetEquals(aiCards)) throw new Exception("AI & models shows: " + string.Join(", ", sv.VisibleCardsForTest));
            if (main.Panes.AllTabs.FirstOrDefault(t => t.Content == sv) is { } st) main.CloseTab(st);
            // Context menus and the error box (slice 3): the tab menu's AI item and the error box's AI action are contributions.
            if (AppModules.MenuSurfaceCount("tab") != 1 || AppModules.ErrorActionCount != 1) throw new Exception($"menu / error contributions: {AppModules.MenuSurfaceCount("tab")} / {AppModules.ErrorActionCount}");
            var dashTab = main.Panes.AllTabs.FirstOrDefault(t => t.Content is DashboardView) ?? throw new Exception("no dashboard tab");
            var tabItems = AppModules.MenuItems("tab", new TabMenuContext(dashTab, MainWindow.ScreenName(dashTab))).OfType<System.Windows.Controls.MenuItem>().ToList();
            if (tabItems.Count != 1 || tabItems[0].Header as string != "✦ Ask AI about this screen…") throw new Exception("tab menu items: " + string.Join(", ", tabItems.Select(i => i.Header)));
            if (tabItems[0].IsEnabled != (Ai.AiClient.Blocked(Ai.AiFeature.Helper) == null)) throw new Exception("tab menu item enabled state");
            if (AppModules.MenuItems("no-such-surface", null).Any()) throw new Exception("unknown surface gave items");
            var errAction = AppModules.ErrorActionFor("probe", "probe message", null);
            bool aiAllowed = Ai.AiClient.Blocked(Ai.AiFeature.Files) == null;
            if (aiAllowed ? errAction?.Label != "✦ Explain this error with AI" : errAction != null) throw new Exception("error-box action: " + (errAction?.Label ?? "none") + " while AI " + (aiAllowed ? "allowed" : "blocked"));
            // Slice 4: the right dock, the Ctrl+Shift+A shortcut and the window / start-up / shutdown hooks are contributions.
            if (!AppModules.HasRightDock || main.RightDock is not AiDock dock) throw new Exception("the right dock is not the AI module's");
            if (AppModules.ShortcutGesture(System.Windows.Input.Key.A, System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift) != "Ctrl+Shift+A") throw new Exception("Ctrl+Shift+A not registered");
            var dockWas = dock.State;
            if (!AppModules.RunShortcut(System.Windows.Input.Key.A, System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift)) throw new Exception("shortcut did not run");
            if (dock.State == dockWas) throw new Exception("Ctrl+Shift+A did not toggle the dock");
            AppModules.RunShortcut(System.Windows.Input.Key.A, System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift);
            if (dock.State != AiDockState.Docked && dockWas == AiDockState.Docked) throw new Exception("dock not back after the second toggle: " + dock.State);
            if (AppModules.RunShortcut(System.Windows.Input.Key.A, System.Windows.Input.ModifierKeys.Control)) throw new Exception("Ctrl+A ran a module shortcut");
            var hooks = AppModules.HookCounts;
            if (hooks.WindowReady != 1 || hooks.Startup != 1) throw new Exception($"hooks: window-ready {hooks.WindowReady}, start-up {hooks.Startup}");
            // Slice 5: the dashboard's AI source + tile and the privacy report's AI rows are contributions, in their 0.1.20 places.
            var srcs = Overview.ModuleSources;
            if (srcs.Count != 1 || srcs[0].Module != "ai" || srcs[0].Id != "ai" || srcs[0].Title != "AI engine") throw new Exception("dashboard sources: " + string.Join(", ", srcs.Select(x => x.Module + "/" + x.Id)));
            bool engineOn = Ai.AiClient.Engine != "off";
            if (Overview.Sources().Any(x => x.Id == "ai") != engineOn) throw new Exception("AI source listed while the engine is " + Ai.AiClient.Engine);
            if (srcs[0].Tile(null, "w").Value != (engineOn ? Ai.AiClient.Engine : "off") || (engineOn && srcs[0].Tile(new(null, "e"), "w") is not { Value: "offline", Alert: true }) || (engineOn && srcs[0].Tile(new("t", null), "w").Value != "online"))
                throw new Exception("AI tile states");
            var dv = DashboardView.Current ?? throw new Exception("no dashboard");
            // Tile titles are drawn in capitals; compare without case.
            int At(string title) => dv.SummaryText.IndexOf(title, StringComparison.OrdinalIgnoreCase);
            if (At("AI engine") < 0 || At("Saved hosts") > At("AI engine") || At("AI engine") > At("Encrypted store"))
                throw new Exception("summary tiles: " + dv.SummaryText.Replace("\n", " | "));
            if (PrivacyReport.Contributions.Count != 1 || PrivacyReport.Contributions[0].Module != "ai") throw new Exception("privacy contributions");
            var postureNames = PrivacyReport.PostureItems().Select(p => p.Name).ToList();
            int dl = postureNames.IndexOf("Data location");
            if (dl < 0 || postureNames.ElementAtOrDefault(dl + 1) != "External AI" || postureNames.ElementAtOrDefault(dl + 2) != "Secret masking for AI" || postureNames.ElementAtOrDefault(dl + 3) != "Paste protection")
                throw new Exception("posture order: " + string.Join(", ", postureNames));
            var firstEndpoint = PrivacyReport.Endpoints().FirstOrDefault()?.Service ?? "";
            if (!firstEndpoint.StartsWith("AI engine")) throw new Exception("first endpoint: " + firstEndpoint);
            if (PrivacyReport.DashboardNote("no-such-source") != "") throw new Exception("dashboard note for an unknown source");
            // Slice 6: help articles tagged "module: ai" exist because the AI module owns them; the tag lines never reach a body.
            var inFile = HelpKb.AllInFile();
            var aiArticles = inFile.Where(a => a.Module == "ai").Select(a => a.Title).ToList();
            if (aiArticles.Count != 15 || inFile.Any(a => a.Module != null && a.Module != "ai")) throw new Exception($"tagged help articles: {aiArticles.Count} ai, others: " + string.Join(", ", inFile.Where(a => a.Module is not (null or "ai")).Select(a => a.Module)));
            if (!HelpKb.OwnedModules.Contains("ai") || HelpKb.Articles.Count != inFile.Count || !aiArticles.All(t => HelpKb.Articles.Any(a => a.Title == t))) throw new Exception($"help with the AI module: {HelpKb.Articles.Count} of {inFile.Count}");
            if (inFile.Any(a => a.Body.StartsWith("module:") || a.Body.StartsWith("keywords:") || a.Keywords.Contains("module:"))) throw new Exception("a tag line leaked into an article");
            if (HelpKb.Search("foundry local npu", 1).FirstOrDefault().Article?.Module != "ai") throw new Exception("AI article not found by search");
            var sample = HelpKb.Parse("## A\nkeywords: k\nmodule: x\nbody a\n## B\nmodule: y\nkeywords: kk\nbody b\n## C\nbody c\n", new HashSet<string> { "y" });
            if (!sample.Select(a => a.Title).SequenceEqual(new[] { "B", "C" }) || sample[0] is not { Keywords: "kk", Body: "body b", Module: "y" } || sample[1].Module != null)
                throw new Exception("help parse/filter: " + string.Join(", ", sample.Select(a => $"{a.Title}/{a.Module}/{a.Keywords}/{a.Body}")));
            // Phase 4, step 1: the screens' "✦ … with AI" actions go through the registered assistant; the screenshot and
            // Settings-card AI items are menu contributions; with no assistant the items don't exist.
            try {
            if (Assist.Current is not Ai.AiAssistant assistant) throw new Exception("assistant: " + (Assist.Current?.GetType().Name ?? "none"));
            if (assistant.WhyNot() != Ai.AiActions.Why(Ai.AiFeature.Files) || assistant.WhyNot(AssistUse.Helper) != Ai.AiActions.Why(Ai.AiFeature.Helper)) throw new Exception("assistant availability differs from the AI's");
            if (AppModules.MenuSurfaceCount("screenshot") != 1 || AppModules.MenuSurfaceCount("settings-card") != 1) throw new Exception("screenshot / settings-card contributions");
            var shotItems = Screenshot.MenuItems(() => Task.FromResult<System.Windows.Media.Imaging.BitmapSource>(null!), "probe").OfType<System.Windows.Controls.MenuItem>().Select(m => m.Header as string).ToList();
            if (!shotItems.Contains("✦ Analyse with AI (vision model)…") || shotItems.Last() != "Open screenshots folder") throw new Exception("screenshot menu: " + string.Join(" | ", shotItems));
            var cardItems = AppModules.MenuItems("settings-card", new SettingsCardMenuContext("Offline mode", "hint", "Block internet", "Block internet", "on", null, () => new[] { "a" })).OfType<System.Windows.Controls.MenuItem>().Select(m => m.Header as string).ToList();
            var cardOnly = AppModules.MenuItems("settings-card", new SettingsCardMenuContext("Offline mode", "hint", null, null, null, null, () => new[] { "a" })).Count();
            if (cardItems.Count != 2 || cardItems[0] != "✦ Explain this option with AI — Block internet" || cardItems[1] != "✦ Explain the “Offline mode” settings with AI" || cardOnly != 1) throw new Exception("settings-card menu: " + string.Join(" | ", cardItems));
            var probeItem = Menus.AssistItem("✦ probe", _ => Task.CompletedTask, enabled: true);
            if (probeItem == null || probeItem.IsEnabled != (assistant.WhyNot() == null) || Menus.AssistItem("✦ probe", _ => Task.CompletedTask, enabled: false)!.IsEnabled) throw new Exception("AssistItem enabled state");
            Assist.ResetForTest();
            try
            {
                if (Menus.AssistItem("✦ probe", _ => Task.CompletedTask) != null) throw new Exception("an AI item exists with no assistant");
            }
            finally { Assist.Register(assistant); }
            if (Assist.Current != assistant) throw new Exception("assistant not restored");
            // Step 3: the reports engine is offline code in Reports\; the AI pass is the registered analyst.
            if (Reports.ReportAnalysis.Current is not Ai.ReportAnalyst analyst || analyst.BudgetChars != Ai.AiClient.PromptBudgetChars || analyst.Actions.Count != 1 || analyst.Actions[0].MenuLabel != "Open in Claude Code (local)")
                throw new Exception("report analyst: " + (Reports.ReportAnalysis.Current?.GetType().Name ?? "none"));
            if (typeof(Reports.ReportEngine).Namespace != "DcxManager.Reports" || typeof(Reports.ReportEngine).GetMethod("AnalyzeAsync") != null) throw new Exception("ReportEngine still carries the AI pass");
            ok($"app modules (0.2.0 phase 4, step 1): the screens' \"✦ … with AI\" actions go through the registered assistant (Ai.AiAssistant, same availability as the AI's Files / Helper features); the 📷 menu's \"Analyse with AI\" and the Settings card's two \"Explain … with AI\" items are menu contributions; with no assistant registered Menus.AssistItem gives no item");
            } catch (Exception ex4) { fail("assistant (phase 4)", ex4); }
            ok($"app modules (0.2.0 phase 3, sidebar + settings + menus + error box + dock + shortcut + hooks + dashboard + privacy + help):the {aiArticles.Count} help articles tagged \"module: ai\" are in the help because the AI module owns them (all {inFile.Count} articles searchable, tag lines never in a body; an unowned module's articles are left out);the dashboard's \"AI engine\" source (listed only with an engine) and its tile (between Saved hosts and Encrypted store; off / engine / offline / online states) and the Privacy & network rows (External AI + Secret masking after Data location; the AI engine destination first) are the AI module's contributions; the AI module fills the right dock column (AiDock), registers Ctrl+Shift+A (toggles it, Ctrl+A does not), one window-ready hook (Foundry status line) and one start-up task (defaults, Foundry set-up, memory guard; never run in the self-test); " +
               "the tab menu's \"Ask AI about this screen…\" and the error box's \"Explain this error with AI\" are contributions (one each, built per context, unknown surfaces empty); " +
               "the AI Engineer sidebar item (open action + 4-item dock menu) and the AI operator (screen) render in the AI section and Navigate opens the screen as a singleton; the \"AI & models\" Settings category (after Cloud providers) and its 3 cards are contributions, rendered in Settings with their scopes");
        }
        catch (Exception ex) { fail("app modules", ex); }

        // 5. The projects (0.2.0 phase 6): each assembly references only the projects below it (arrows down), nothing but the
        // app references the AI, and the embedded files keep their names in the assembly that reads them. Checked on the
        // built assemblies, so it holds in the published single-file exe too.
        try
        {
            var below = new Dictionary<string, string[]>
            {
                ["Dcx.Core"] = Array.Empty<string>(),
                ["Dcx.Connectivity"] = new[] { "Dcx.Core" },
                ["Dcx.Ui"] = new[] { "Dcx.Core" },
                ["Dcx.Infra"] = new[] { "Dcx.Core", "Dcx.Connectivity" },
                ["Dcx.Cloud"] = new[] { "Dcx.Core", "Dcx.Connectivity", "Dcx.Ui" },
                ["Dcx.Addons"] = new[] { "Dcx.Core", "Dcx.Connectivity", "Dcx.Ui" },
                ["Dcx.Library"] = new[] { "Dcx.Core", "Dcx.Connectivity", "Dcx.Ui", "Dcx.Addons" },
                ["Dcx.Browser"] = new[] { "Dcx.Core", "Dcx.Connectivity", "Dcx.Ui", "Dcx.Infra", "Dcx.Addons" },
                ["Dcx.Notify"] = new[] { "Dcx.Core" },
                ["Dcx.Ai"] = new[] { "Dcx.Core", "Dcx.Connectivity", "Dcx.Ui", "Dcx.Infra", "Dcx.Cloud", "Dcx.Addons", "Dcx.Library", "Dcx.Browser" },
            };
            var app = typeof(App).Assembly;
            var wrong = new List<string>();
            int checkedCount = 0;
            foreach (var (name, allowed) in below)
            {
                System.Reflection.Assembly asm;
                try { asm = name == "Dcx.Ai" && !AiFeatures.Current.On ? null! : System.Reflection.Assembly.Load(name); }
                catch (FileNotFoundException) { continue; }   // a project not split out yet
                if (asm == null) continue;
                checkedCount++;
                foreach (var r in asm.GetReferencedAssemblies().Select(r => r.Name ?? "").Where(r => r.StartsWith("Dcx", StringComparison.Ordinal)))
                    if (!allowed.Contains(r)) wrong.Add($"{name} → {r}");
            }
            if (wrong.Count > 0) throw new Exception("references that don't point down: " + string.Join(", ", wrong));
            // Nothing below the app may reference the AI (only the app's composition root and its self-test do).
            var aiUsers = below.Keys.Where(k => k != "Dcx.Ai").Select(k => { try { return System.Reflection.Assembly.Load(k); } catch (FileNotFoundException) { return null; } })
                .Where(a => a != null && a.GetReferencedAssemblies().Any(r => r.Name == "Dcx.Ai")).Select(a => a!.GetName().Name).ToList();
            if (aiUsers.Count > 0) throw new Exception("these reference Dcx.Ai: " + string.Join(", ", aiUsers));
            // Embedded files: help with the help search (Core), the terminal page with the app, the add-on SDK with the app.
            string[] Res(System.Reflection.Assembly a) => a.GetManifestResourceNames();
            var help = typeof(HelpKb).Assembly;
            if (!Res(help).Contains("help/help.md")) throw new Exception($"help/help.md is not in {help.GetName().Name}");
            foreach (var n in new[] { "web/terminal.html", "web/xterm.js", "web/xterm.css", "sdk/DcxBridge.dll", "sdk/DcxAddon.ps1", "sdk/DcxBridge.cs" })
                if (!Res(app).Contains(n)) throw new Exception($"{n} is not in the app's assembly");
            ok($"projects (0.2.0 phase 6): {checkedCount} assemblies below the app reference only the projects below them; none references Dcx.Ai; help/help.md in {help.GetName().Name}, web/* and sdk/* in the app");
        }
        catch (Exception ex) { fail("projects", ex); }

        // 4. Source scan: nothing below the screens calls the UI directly any more (Debug builds, source next to the exe).
        try
        {
            // The src folder (every project in it since the phase 6 split), found from the exe in a Debug build.
            string? src = null;
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null && src == null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "DcxManager", "DcxManager.csproj"))) src = d.FullName;
            if (src == null) { ok("layering source scan: skipped (source tree not next to the exe)"); return; }
            var offenders = new List<string>();
            int scanned = 0, leftovers = 0;
            foreach (var project in Directory.GetDirectories(src).Where(p => !UiProjects.Contains(Path.GetFileName(p))))
                foreach (var folder in LowerFolders)
                {
                    var dir = Path.Combine(project, folder);
                    if (!Directory.Exists(dir)) continue;
                    foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
                    {
                        var name = Path.GetFileName(file);
                        // Views, windows and UI helpers that still live in these folders are screens; they move to the App project in phase 6.
                        if (Regex.IsMatch(name, @"View|Window|Ui\.cs$|Panel")) continue;
                        scanned++;
                        var text = File.ReadAllText(file);
                        var hits = Forbidden.Matches(text);
                        if (hits.Count == 0) continue;
                        if (Leftovers.Contains(name)) { leftovers++; continue; }
                        offenders.Add($"{Path.GetFileName(project)}\\{folder}\\{name}: {string.Join(", ", hits.Select(m => m.Value).Distinct())}");
                    }
                }
            if (offenders.Count > 0) throw new Exception("direct UI calls below the screens:\n  " + string.Join("\n  ", offenders));
            ok($"layering source scan: {scanned} files below the screens use only the Ui / Log seams ({leftovers} known leftovers for phase 6: UI services and tab access)");

            // Phase 4: outside the AI's own code, "✦ … with AI" actions go through the assistant (Assist / Menus.AssistItem),
            // never straight to the AI's actions, its send preview or its old context builder.
            var aiOwn = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "AiDock.cs", "AiPanel.cs", "AssistantView.cs", "FoundryPanel.cs", "AiSendPreview.cs", "ScreenshotAi.cs", "AiModule.cs" };
            var direct = new Regex(@"\b(AiActions|AiSendPreview|AiContext)\.|\bAssistantView\.Current\?\.AskAbout\(");
            var pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // none left
            var bypass = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + "Ai" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
                .Where(f => !Path.GetFileName(f).StartsWith("SelfTest") && !aiOwn.Contains(Path.GetFileName(f)) && !pending.Contains(Path.GetFileName(f)))
                .Where(f => direct.IsMatch(File.ReadAllText(f))).Select(Path.GetFileName).ToList();
            if (bypass.Count > 0) throw new Exception("screens calling the AI's actions directly instead of the assistant: " + string.Join(", ", bypass));
            ok("assistant source scan (0.2.0 phase 4): no screen or service calls AiActions / AiSendPreview / AiContext or the operator's AskAbout directly; they ask the registered assistant");

            // Outside the AI's own files nothing uses the DcxManager.Ai namespace (phase 4; since phase 6b without exceptions:
            // the terminal bridge and the command risk rules are DcxManager.Sessions, in Connectivity).
            var aiViews = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "AiDock.cs", "AiPanel.cs", "AssistantView.cs", "FoundryPanel.cs", "AiSendPreview.cs", "ScreenshotAi.cs", "AiModule.cs", "AiSettingsCards.cs", "AiWindow.cs" };
            var bridgeUsers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var aiNs = new Regex(@"using DcxManager\.Ai;|(?<![\w.])Ai\.[A-Z]\w*|DcxManager\.Ai\.");
            var aiUsers = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + "Ai" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
                .Where(f => !Path.GetFileName(f).StartsWith("SelfTest") && !aiViews.Contains(Path.GetFileName(f)) && !bridgeUsers.Contains(Path.GetFileName(f)))
                .Where(f => File.ReadAllLines(f).Any(line => !line.TrimStart().StartsWith("//") && aiNs.IsMatch(line))).Select(Path.GetFileName).ToList();
            if (aiUsers.Count > 0) throw new Exception("files outside the AI module that use DcxManager.Ai: " + string.Join(", ", aiUsers));
            ok($"AI module boundary (0.2.0 phase 4, no exceptions since 6b): outside Ai\\ and the {aiViews.Count} AI views nothing uses DcxManager.Ai");

            // Phase 7: every file that writes settings.dcx other than AppSettings (its own Save) and FeatureLock (the guard
            // itself) keeps what the AI features lock guards — snapshot restore, a backup's "replace everything", start-up
            // file recovery. File-level and deliberately coarse: a new writer has to name the guard to pass.
            var writes = new Regex(@"SecureStore\.(Write|Encrypt)\(|Snapshots\.Commit\(|File\.(WriteAllBytes|Move|Copy|Replace)\(");
            var guards = new Regex(@"FeatureLock\.(KeepGuarded|KeepGuardedSealed|FailClosedSealed)\(");
            var own = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "AppSettings.cs", "FeatureLock.cs" };
            var writers = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
                .Where(f => !Path.GetFileName(f).StartsWith("SelfTest") && !own.Contains(Path.GetFileName(f)))
                .Select(f => (Name: Path.GetFileName(f), Text: File.ReadAllText(f)))
                .Where(x => x.Text.Contains("\"settings.dcx\"") && writes.IsMatch(x.Text)).ToList();
            var unguarded = writers.Where(x => !guards.IsMatch(x.Text)).Select(x => x.Name).ToList();
            if (unguarded.Count > 0) throw new Exception("writes settings.dcx without FeatureLock.KeepGuarded / KeepGuardedSealed / FailClosedSealed: " + string.Join(", ", unguarded));
            var known = new[] { "Backup.cs", "Snapshots.cs", "StoreSchema.cs" };
            if (known.Any(k => writers.All(w => w.Name != k))) throw new Exception("the scan no longer finds the known writers: " + string.Join(", ", writers.Select(w => w.Name)));
            ok($"settings-writer scan (0.2.0 phase 7): {writers.Count} files besides AppSettings write settings.dcx ({string.Join(", ", writers.Select(w => w.Name))}) and each keeps what the AI features lock guards");
        }
        catch (Exception ex) { fail("layering source scan", ex); }
    }
}
