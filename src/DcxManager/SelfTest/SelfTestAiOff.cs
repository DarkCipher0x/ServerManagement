using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DcxManager.Services;
using DcxManager.Views;

namespace DcxManager;

/// <summary>
/// 0.2.0 phase 5 in the normal (AI on) run: the switch's rule, the first-start migration, the lock (with an injected clock,
/// so throttling is tested without waiting), the capability filter, the Settings card and the privacy row.
/// </summary>
internal static class AiSwitchTests
{
    public static async Task RunAsync(MainWindow main, Action<string> ok, Action<string, Exception?> fail)
    {
        // 1. The rule: policy beats everything, the environment beats the setting unless locked.
        try
        {
            void Expect(int? pol, string? env, bool set, bool locked, bool on, AiFeatures.From src)
            {
                var d = AiFeatures.Decide(pol, env, set, locked);
                if (d.On != on || d.Source != src) throw new Exception($"Decide({pol},{env},{set},{locked}) = {d.On}/{d.Source}, expected {on}/{src}");
            }
            Expect(0, "1", true, false, false, AiFeatures.From.Policy);
            Expect(1, "0", false, true, true, AiFeatures.From.Policy);
            Expect(null, "0", true, false, false, AiFeatures.From.Environment);
            Expect(null, "1", false, false, true, AiFeatures.From.Environment);
            Expect(null, "1", false, true, false, AiFeatures.From.Setting);    // the lock makes the environment ignored
            Expect(null, "yes", true, false, true, AiFeatures.From.Setting);
            Expect(null, null, false, false, false, AiFeatures.From.Setting);
            if (!AiFeatures.Current.On || AiFeatures.Current.Source != AiFeatures.From.Environment) throw new Exception("this run: " + AiFeatures.Current);
            // First 0.2.0 start: new install off; an existing store keeps AI only if it was in use; decided once.
            var fresh = new AppSettings();
            if (!AiFeatures.Migrate(fresh, existedBefore: false) || fresh.AiFeatures != false) throw new Exception("new install");
            var used = new AppSettings { AiEnabled = true, AiEngine = "ollama-gpu" };
            if (!AiFeatures.Migrate(used, existedBefore: true) || used.AiFeatures != true) throw new Exception("upgrade with AI in use");
            var unused = new AppSettings { AiEnabled = true, AiEngine = "off" };
            if (!AiFeatures.Migrate(unused, existedBefore: true) || unused.AiFeatures != false) throw new Exception("upgrade with the engine off");
            var disabled = new AppSettings { AiEnabled = false, AiEngine = "npu" };
            if (!AiFeatures.Migrate(disabled, existedBefore: true) || disabled.AiFeatures != false) throw new Exception("upgrade with AI disabled");
            if (AiFeatures.Migrate(used, existedBefore: false) || used.AiFeatures != true) throw new Exception("migrated twice");
            var entry = AuditLog.Read(DateTime.Now.ToString("yyyy-MM")).FirstOrDefault(e => e.Target == "ai-features" && e.Action.StartsWith("AI module"));
            if (entry?.Action != "AI module loaded") throw new Exception("start-up audit: " + (entry?.Action ?? "none"));
            ok("AI features switch (0.2.0 phase 5): machine policy beats everything, DCX_AI_FEATURES beats the setting unless the lock is set, else the setting; the first 0.2.0 start keeps AI on only for a store that used it (new installs off), once; this run loaded the module from the environment and audited it");
        }
        catch (Exception ex) { fail("AI features switch", ex); }

        // 2. The lock: verifier only, throttled with a persisted wait, every attempt audited without the text.
        var realClock = FeatureLock.UtcNow;
        var now = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
        FeatureLock.UtcNow = () => now;
        const string pw = "selftest-lock-correct-horse";
        try
        {
            FeatureLock.ResetForTest();
            if (FeatureLock.IsSet || FeatureLock.Gate("probe", () => throw new Exception("asked without a lock")) != null) throw new Exception("no lock should allow without asking");
            if (FeatureLock.Set("short") != FeatureLock.Result.TooShort || FeatureLock.IsSet) throw new Exception("short password accepted");
            if (FeatureLock.Set(pw) != FeatureLock.Result.Ok || !FeatureLock.IsSet || !AppSettings.Current.AiLockSet) throw new Exception("set");
            var raw = File.ReadAllBytes(Path.Combine(SecureStore.DataDir, FeatureLock.FileName));
            if (Encoding.UTF8.GetString(raw).Contains(pw) || Encoding.UTF8.GetString(raw).Contains("Hash")) throw new Exception("lock file not encrypted");
            if (FeatureLock.Gate("probe", () => null) != "Cancelled.") throw new Exception("cancel");
            if (FeatureLock.Gate("probe", () => pw) != null) throw new Exception("right password refused");
            for (int i = 1; i <= 2; i++) if (FeatureLock.Verify("wrong-password-" + i, "probe") != FeatureLock.Result.Wrong || FeatureLock.WaitLeft > TimeSpan.Zero) throw new Exception("wait before the 3rd wrong one");
            if (FeatureLock.Verify("wrong-password-3", "probe") != FeatureLock.Result.Wrong || FeatureLock.WaitLeft != TimeSpan.FromSeconds(30)) throw new Exception("3rd wrong: wait " + FeatureLock.WaitLeft);
            if (FeatureLock.Verify(pw, "probe") != FeatureLock.Result.Wait) throw new Exception("checked during the wait");
            now += TimeSpan.FromSeconds(31);
            if (FeatureLock.Verify("wrong-password-4", "probe") != FeatureLock.Result.Wrong || FeatureLock.WaitLeft != TimeSpan.FromSeconds(60)) throw new Exception("4th wrong: wait " + FeatureLock.WaitLeft);
            for (int i = 0; i < 8; i++) { now += TimeSpan.FromMinutes(11); FeatureLock.Verify("wrong-again-" + i, "probe"); }
            if (FeatureLock.WaitLeft != TimeSpan.FromMinutes(10)) throw new Exception("wait not capped at 10 min: " + FeatureLock.WaitLeft);
            now += TimeSpan.FromMinutes(11);
            if (FeatureLock.Verify(pw, "probe") != FeatureLock.Result.Ok || FeatureLock.WaitLeft != TimeSpan.Zero) throw new Exception("right password after the wait");
            if (FeatureLock.Verify("wrong-password-5", "probe") != FeatureLock.Result.Wrong || FeatureLock.WaitLeft != TimeSpan.Zero) throw new Exception("the count did not reset after the right password");
            if (FeatureLock.Change("nope-nope-nope", "another-password-1") != FeatureLock.Result.Wrong) throw new Exception("change with a wrong password");
            if (FeatureLock.Change(pw, "another-password-1") != FeatureLock.Result.Ok || FeatureLock.Verify(pw, "probe") != FeatureLock.Result.Wrong) throw new Exception("change");
            if (FeatureLock.Remove(pw) == FeatureLock.Result.Ok || !FeatureLock.IsSet) throw new Exception("removed with the old password");
            if (FeatureLock.Remove("another-password-1") != FeatureLock.Result.Ok || FeatureLock.IsSet || AppSettings.Current.AiLockSet) throw new Exception("remove");
            // Removed outside the app (the store file deleted): noticed and audited at the next start.
            FeatureLock.Set(pw);
            File.Delete(Path.Combine(SecureStore.DataDir, FeatureLock.FileName));
            FeatureLock.ReconcileAtStart();
            var audit = AuditLog.Read(DateTime.Now.ToString("yyyy-MM")).Where(e => e.Target == "ai-lock").ToList();
            if (!audit.Any(e => e.Action.Contains("removed outside the app")) || AppSettings.Current.AiLockSet) throw new Exception("outside removal not audited");
            if (audit.Count(e => e.Action == "wrong password") < 10 || audit.Count(e => e.Action == "correct password") < 3) throw new Exception($"attempts not audited: {audit.Count}");
            if (audit.Any(e => (e.Action + e.Result).Contains(pw) || (e.Action + e.Result).Contains("wrong-password") || (e.Action + e.Result).Contains("another-password"))) throw new Exception("a password reached the audit log");
            // 0.2.0 audit: a snapshot from before the lock, or a backup's "replace everything", must not undo the lock or
            // change what it guards.
            FeatureLock.ResetForTest();
            var snap = Snapshots.Create("before-lock");   // reasons are one or two lowercase words
            if (!snap.Ok || snap.Path == null) throw new Exception("snapshot: " + snap.Message);
            try
            {
                FeatureLock.Set(pw);
                var plan = Snapshots.PlanRestore(Snapshots.Open(snap.Path));
                if (plan.Remove.Concat(plan.Replace).Concat(plan.AddBack).Contains(FeatureLock.FileName)) throw new Exception("a restore would touch the lock file");
                var cur = AppSettings.Current;
                var incoming = new AppSettings { AiFeatures = cur.AiFeatures != true, AiAllowCloud = !cur.AiAllowCloud, AiLockSet = false };
                FeatureLock.KeepGuarded(incoming);
                if (incoming.AiFeatures != cur.AiFeatures || incoming.AiAllowCloud != cur.AiAllowCloud || !incoming.AiLockSet) throw new Exception("backup settings changed what the lock guards");
                var sealedOld = SecureStore.Encrypt(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new AppSettings { AiFeatures = cur.AiFeatures != true, AiAllowCloud = !cur.AiAllowCloud }, SecureStore.Json), "settings.dcx");
                var back = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(SecureStore.Decrypt(FeatureLock.KeepGuardedSealed(sealedOld), "settings.dcx"), SecureStore.Json)!;
                if (back.AiFeatures != cur.AiFeatures || back.AiAllowCloud != cur.AiAllowCloud) throw new Exception("snapshot settings changed what the lock guards");
                // 0.2.0 phase 7: a damaged settings.dcx restored from a snapshot at start-up comes back fail-closed.
                var onBefore = SecureStore.Encrypt(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new AppSettings { AiFeatures = true, AiAllowCloud = true, AiLockSet = false }, SecureStore.Json), "settings.dcx");
                var recovered = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(SecureStore.Decrypt(FeatureLock.FailClosedSealed(onBefore), "settings.dcx"), SecureStore.Json)!;
                if (recovered.AiFeatures != false || recovered.AiAllowCloud || !recovered.AiLockSet) throw new Exception("a recovered settings file undid the lock");
            }
            finally { try { File.Delete(snap.Path); } catch { } }
            ok("AI features lock (0.2.0 phase 5): ≥12 characters, encrypted PBKDF2-SHA256 verifier only, cancel / right / wrong; 3 wrong → 30 s, doubling, capped at 10 min (injected clock), nothing checked during a wait, reset by the right password; change and remove need the current one; a lock file deleted outside the app is audited at start; every attempt audited, never the text; a snapshot restore never touches the lock file and, like a backup's \"replace everything\", keeps the guarded settings while locked; a damaged settings file recovered from a snapshot comes back with AI and internet providers off");
        }
        catch (Exception ex) { fail("AI features lock", ex); }
        finally { FeatureLock.UtcNow = realClock; try { FeatureLock.ResetForTest(); } catch { } }

        // 3. Capabilities of a module that isn't loaded: not listed, and a call says why (same error shape as unknown).
        var loaded = Capabilities.ModuleLoaded;
        try
        {
            string[] aiOnly = { "terminal_run", "terminal_read_screen", "terminal_send_keys", "terminal_interrupt", "terminal_switch_target", "terminal_open_session" };
            if (aiOnly.Any(n => Capabilities.Find(n) == null)) throw new Exception("AI capabilities missing with the module loaded");
            Capabilities.ModuleLoaded = m => m != "ai";
            var names = Capabilities.All.Select(c => c.Name).ToList();
            if (aiOnly.Any(names.Contains) || names.Count != 39) throw new Exception($"listed while unavailable ({names.Count})");
            if (Capabilities.UnavailableBecause("terminal_run") != "ai" || Capabilities.UnavailableBecause("list_hosts") != null || Capabilities.UnavailableBecause("no_such") != null) throw new Exception("UnavailableBecause");
            var msg = await Capabilities.RunAsync("terminal_run", new System.Text.Json.Nodes.JsonObject(), new CapabilityContext((_, _, _) => Task.FromResult(false), "selftest"), CancellationToken.None)
                .ContinueWith(t => t.IsFaulted ? t.Exception!.InnerException!.Message : "ran");
            if (msg != "capability unavailable (AI off): terminal_run") throw new Exception("call: " + msg);
            if (Addons.AddonApi.RiskOf("terminal_run") != null) throw new Exception("add-on risk lookup still finds it");
            ok("module capabilities (0.2.0 phase 5): with the AI module not loaded its 6 terminal-target capabilities are not listed (38 left) and a call fails with \"capability unavailable (AI off)\"");
        }
        catch (Exception ex) { fail("module capabilities", ex); }
        finally { Capabilities.ModuleLoaded = loaded; }

        // 4. The Settings card and the privacy row.
        try
        {
            if (SettingsView.CategoryOfForTest("AI features") != "Security & privacy") throw new Exception("card placement");
            var post = PrivacyReport.PostureItems().FirstOrDefault(p => p.Name == "AI features") ?? throw new Exception("no privacy row");
            if (!post.Value.StartsWith("ON — module loaded")) throw new Exception("privacy row: " + post.Value);
            if (main.Panes.AllTabs.FirstOrDefault(t => t.Content is SettingsView) is { } st0) main.CloseTab(st0);
            main.Navigate("settings");
            await Task.Delay(300);
            var sv = SettingsView.Current ?? throw new Exception("no settings view");
            var card = sv.CardForTest("AI features") ?? throw new Exception("no AI features card");
            var texts = Menus.Descendants<TextBlock>(card).Select(t => t.Text).ToList();
            if (!texts.Any(t => t.StartsWith("This run: AI features on")) || !texts.Any(t => t.StartsWith("Not locked."))) throw new Exception("card text: " + string.Join(" | ", texts.Take(8)));
            if (main.Panes.AllTabs.FirstOrDefault(t => t.Content == sv) is { } st) main.CloseTab(st);
            ok("AI features card (Security & privacy: state, on/off, restart, lock) and the privacy row \"AI features: ON — module loaded\"");
        }
        catch (Exception ex) { fail("AI features card", ex); }
    }
}

/// <summary>
/// The second self-test run (0.2.0 phase 5): the app started with DCX_AI_FEATURES=0. The AI module was never registered,
/// so no AI screen, menu item, setting, help article, capability, tile, report option or background work may exist —
/// and the offline features still work. Writes its own report.
/// </summary>
internal static class AiOffTests
{
    private static readonly Regex AiUi = new(@"✦|\bAI Engineer\b|\bAI operator\b|\bwith AI\b|\bAI insights\b|\bAI answer\b|\bAI analysis\b|\bAsk AI\b|\bAI sidebar\b|\bAI engine\b", RegexOptions.Compiled);

    public static async Task RunAsync(MainWindow main, string reportPath)
    {
        var log = new StringBuilder();
        int failures = 0;
        void Ok(string s) => log.AppendLine("ok    " + s);
        void Fail(string s, Exception? ex) { failures++; log.AppendLine("FAIL  " + s + " :: " + ex); }
        var started = DateTime.Now;
        Application.Current.DispatcherUnhandledException += (_, e) => { Fail("unhandled UI exception", e.Exception); e.Handled = true; };
        async Task Settle(int ms = 500)
        {
            await Task.Delay(ms);
            await Application.Current.Dispatcher.InvokeAsync(() => main.UpdateLayout(), DispatcherPriority.ApplicationIdle);
        }
        Ok(SelfTest.StartupLine);
        await Settle(1500);

        // 1. Nothing of the AI module was registered.
        try
        {
            var st = AiFeatures.Current;
            if (st.On || st.Source != AiFeatures.From.Environment) throw new Exception("state: " + st);
            if (AppModules.All.Count != 0 || Assist.Current != null || Reports.ReportAnalysis.Current != null || main.RightDock != null) throw new Exception("module pieces present");
            if (AppModules.NavItems.Count != 0 || AppModules.SettingsCardItems.Count != 0 || AppModules.MenuSurfaceCount("tab") + AppModules.MenuSurfaceCount("screenshot") + AppModules.MenuSurfaceCount("settings-card") != 0 || AppModules.ErrorActionCount != 0)
                throw new Exception("contributions present");
            if (AppModules.HookCounts != (0, 0, 0) || AppModules.HasRightDock || Overview.ModuleSources.Count != 0 || PrivacyReport.Contributions.Count != 0 || HelpKb.OwnedModules.Count != 0) throw new Exception("hooks / sources present");
            var entry = AuditLog.Read(DateTime.Now.ToString("yyyy-MM")).FirstOrDefault(e => e.Target == "ai-features" && e.Action.StartsWith("AI module"));
            if (entry?.Action != "AI module not loaded" || !entry.Result.Contains("DCX_AI_FEATURES")) throw new Exception("start-up audit: " + entry?.Action + " " + entry?.Result);
            Ok("AI off: DCX_AI_FEATURES=0 decided this run, nothing of the AI module was registered (no sidebar items, settings, menus, error action, dock, shortcut, hooks, assistant, report analyst, dashboard source, privacy rows or help articles), and the start-up audit says \"AI module not loaded\"");
        }
        catch (Exception ex) { Fail("AI off: module", ex); }

        // 2. The sidebar, Settings, help, capabilities, privacy, dashboard and menus.
        try
        {
            if (main.NavKeys.Contains("aipanel") || main.NavKeys.Contains("assistant")) throw new Exception("AI sidebar items: " + string.Join(", ", main.NavKeys));
            // 0.6.16: Reports live in DOCUMENTS and Help in SYSTEM, so without the module there is no AI section at all.
            if (MainWindow.NavSections.Contains("AI") || main.NavSectionsForTest.ContainsKey("AI") || main.NavSectionsForTest.GetValueOrDefault("DOCUMENTS")?.Contains("reports") != true
                || main.NavSectionsForTest.GetValueOrDefault("SYSTEM")?.Contains("help") != true) throw new Exception("the sidebar without the AI module");
            if (SettingsView.Categories.Any(c => c.Name == "AI & models")) throw new Exception("AI & models category");
            if (SettingsView.CategoryOfForTest("AI features") != "Security & privacy") throw new Exception("no AI features card");
            var inFile = HelpKb.AllInFile();
            if (HelpKb.Articles.Any(a => a.Module != null) || HelpKb.Articles.Count != inFile.Count(a => a.Module == null) || inFile.Count(a => a.Module == "ai") != 15) throw new Exception($"help: {HelpKb.Articles.Count} of {inFile.Count}");
            var names = Capabilities.All.Select(c => c.Name).ToList();
            if (names.Count != 39 || names.Any(n => n.StartsWith("terminal_"))) throw new Exception("capabilities: " + names.Count);
            string? fixturePath = null;
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null && fixturePath == null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "docs", "baseline", "capabilities-0.1.18.txt"))) fixturePath = Path.Combine(d.FullName, "docs", "baseline", "capabilities-0.1.18.txt");
            var fixture = fixturePath == null ? new List<string>() : File.ReadAllLines(fixturePath).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
            var missing = fixture.Where(n => !names.Contains(n) && Capabilities.UnavailableBecause(n) != "ai").ToList();
            if (fixture.Count > 0 && missing.Count > 0) throw new Exception("fixture names neither listed nor AI-only: " + string.Join(", ", missing));
            var post = PrivacyReport.PostureItems();
            if (post.FirstOrDefault(p => p.Name == "AI features")?.Value != "OFF — module not loaded (DCX_AI_FEATURES)" || post.Any(p => p.Name is "External AI" or "Secret masking for AI")) throw new Exception("privacy posture: " + string.Join(", ", post.Select(p => p.Name)));
            if (PrivacyReport.Endpoints().Any(e => e.Service.StartsWith("AI engine") || e.Service.StartsWith("Foundry"))) throw new Exception("privacy endpoints list the AI");
            if (Overview.Sources().Any(s => s.Id == "ai")) throw new Exception("dashboard AI source");
            if (Menus.AssistItem("✦ probe", _ => Task.CompletedTask) != null || AppModules.ErrorActionFor("t", "m", null) != null) throw new Exception("AI menu item / error action");
            var shot = Screenshot.MenuItems(() => Task.FromResult<System.Windows.Media.Imaging.BitmapSource>(null!), "probe").OfType<MenuItem>().Select(m => m.Header as string ?? "").ToList();
            if (shot.Any(h => AiUi.IsMatch(h))) throw new Exception("screenshot menu: " + string.Join(" | ", shot));
            Ok($"AI off: sidebar without AI items (no AI section; Reports in DOCUMENTS, Help in SYSTEM), no AI & models settings, help without the 15 AI articles ({HelpKb.Articles.Count} left), {names.Count} capabilities (the 6 AI-only ones unavailable, every other 0.1.18 name kept), privacy row \"OFF — module not loaded\" and no AI rows, no dashboard AI source, no AI item in menus or the error box");
        }
        catch (Exception ex) { Fail("AI off: surfaces", ex); }

        // 3. Every sidebar screen, opened: no AI text anywhere on it (the AI features card is the one place that names the AI).
        try
        {
            var offenders = new List<string>();
            int menusBuilt = 0;
            static IEnumerable<string> Headers(IEnumerable<object?> items)
            {
                foreach (var i in items)
                {
                    if (i is MenuItem m)
                    {
                        if (m.Header is string h) yield return h;
                        foreach (var sub in Headers(m.Items.Cast<object?>())) yield return sub;
                    }
                    else if (i is IEnumerable<object?> seq && i is not string) foreach (var sub in Headers(seq)) yield return sub;
                }
            }
            var keys = main.NavKeys.ToList();
            // Two screens the sidebar doesn't open: a server's Manage view and a terminal, for a host on a refused local port
            // (nothing real is contacted).
            var probeCred = new Credential { Name = "aioff-probe", Kind = CredentialKind.Password, Secret = "not-a-real-password" };
            AppData.Current.Credentials.Add(probeCred);
            var probeHost = new HostEntry { Name = "aioff-probe", Address = "127.0.0.1", Port = 1, Username = "tester", Auth = AuthMethod.Password, CredentialId = probeCred.Id, KeepAliveSeconds = 0 };
            AppData.Current.Hosts.Add(probeHost);
            keys.Add("#server"); keys.Add("#terminal");
            foreach (var key in keys)
            {
                if (key == "#server") main.OpenServer(probeHost);
                else if (key == "#terminal") main.OpenTerminal(probeHost);
                else main.Navigate(key);
                await Settle(key is "settings" or "dashboard" or "privacy" or "#terminal" ? 900 : 350);
                if (main.Panes.ActiveTab?.Content is not FrameworkElement root) continue;
                var skip = new HashSet<DependencyObject>();
                if (root is SettingsView sv && sv.CardForTest("AI features") is { } card) foreach (var d in Menus.Descendants<DependencyObject>(card)) skip.Add(d);
                foreach (var fe in Menus.Descendants<FrameworkElement>(root))
                {
                    if (skip.Contains(fe) || fe is TextBox) continue;   // text boxes hold documents and user data
                    var text = fe switch
                    {
                        TextBlock t => t.Text,
                        ContentControl { Content: string c } => c,
                        HeaderedItemsControl { Header: string h } => h,
                        _ => null,
                    };
                    var tip = fe.ToolTip as string;
                    foreach (var s in new[] { text, tip })
                        if (s != null && AiUi.IsMatch(s)) offenders.Add($"{key}: “{(s.Length > 90 ? s[..90] + "…" : s)}”");
                    // Its right-click menu and its "More ▾" items, built the way they are when opened (no popup).
                    var builder = Menus.BuilderOf(fe) ?? (fe is Button { Tag: Func<IEnumerable<object?>> more } ? more : null);
                    if (builder == null) continue;
                    menusBuilt++;
                    List<object?> items;
                    try { items = builder().ToList(); } catch { continue; }   // a menu that needs a selection may throw without one
                    foreach (var h in Headers(items))
                        if (AiUi.IsMatch(h)) offenders.Add($"{key} (menu): “{(h.Length > 90 ? h[..90] + "…" : h)}”");
                }
            }
            if (offenders.Count > 0) throw new Exception($"{offenders.Distinct().Count()} AI text(s) on screens:\n  " + string.Join("\n  ", offenders.Distinct().Take(25)));
            if (menusBuilt < 20) throw new Exception($"only {menusBuilt} menus were built — the sweep isn't reaching them");
            Ok($"AI off: {keys.Count} screens opened (every sidebar screen, a server view, a terminal) and {menusBuilt} right-click / More ▾ menus built: no AI text, button, tab, tooltip or menu item on any of them (the AI features card excepted)");
        }
        catch (Exception ex) { Fail("AI off: screens", ex); }

        // 4. Offline features still work: the help search and an offline report digest.
        try
        {
            if (HelpKb.Search("split the screen", 1).FirstOrDefault().Article is not { } a || a.Module != null) throw new Exception("help search");
            var pats = Reports.LogDigest.Digest("2026-10-20T10:00:00Z host sshd[1]: Failed password for root from 192.0.2.7 port 22\n2026-10-20T10:00:01Z host sshd[1]: Failed password for root from 192.0.2.8 port 22\n");
            if (pats.Count != 1 || pats[0].Count != 2) throw new Exception("digest");
            Ok("AI off: offline help search and log digest still work");
        }
        catch (Exception ex) { Fail("AI off: offline features", ex); }

        // 5. No AI engine process was started by this run. Only the app's own process tree counts: an Ollama or Foundry
        //    that the user (or its own tray app / updater) runs in the background is none of the app's business with AI
        //    off — the app ignores it completely (DarkCipher0x, 2026-10-20) — and must not fail the run.
        try
        {
            var parents = new Dictionary<int, int>();
            var names = new Dictionary<int, string>();
            using (var q = new System.Management.ManagementObjectSearcher("SELECT ProcessId, ParentProcessId, Name FROM Win32_Process"))
                foreach (var o in q.Get().Cast<System.Management.ManagementObject>())
                    using (o)
                    {
                        var id = Convert.ToInt32(o["ProcessId"]);
                        parents[id] = Convert.ToInt32(o["ParentProcessId"]);
                        names[id] = o["Name"] as string ?? "";
                    }
            bool OursById(int id) { for (int hops = 0; hops < 32 && id > 4 && parents.TryGetValue(id, out var parent); hops++) { if (parent == Environment.ProcessId) return true; id = parent; } return false; }
            var engine = new Regex("(?i)foundry|ollama|inference\\.service");
            var ours = names.Where(n => engine.IsMatch(n.Value) && OursById(n.Key)).Select(n => n.Value).ToList();
            if (ours.Count > 0) throw new Exception("started by the app: " + string.Join(", ", ours));
            var elsewhere = names.Count(n => engine.IsMatch(n.Value) && !OursById(n.Key));
            Ok("AI off: no Foundry Local / Ollama process was started by the app" + (elsewhere > 0 ? $" ({elsewhere} running outside it on this PC, ignored)" : ""));
        }
        catch (Exception ex) { Fail("AI off: processes", ex); }

        // 6. After everything above opened every screen and menu: the AI assembly was never loaded (0.2.0 phase 6: with
        // AI off not one line of the AI's code is in the process, not even unused).
        try
        {
            var loaded = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name ?? "").Where(n => n.StartsWith("Dcx.", StringComparison.Ordinal)).OrderBy(n => n).ToList();
            if (loaded.Contains("Dcx.Ai")) throw new Exception("Dcx.Ai is loaded: " + string.Join(", ", loaded));
            Ok($"AI off: the AI assembly (Dcx.Ai) was never loaded; loaded: {(loaded.Count > 0 ? string.Join(", ", loaded) : "(single project)")}");
        }
        catch (Exception ex) { Fail("AI off: assembly", ex); }

        lock (Dialogs.SelfTestPrompts)
            if (Dialogs.SelfTestPrompts.Count > 0) Fail("self-test reached " + Dialogs.SelfTestPrompts.Count + " dialog(s): " + string.Join(" | ", Dialogs.SelfTestPrompts.Distinct().Take(12)), null);
            else Ok("no dialog was reached (AI-off run)");

        log.Insert(0, $"DarkCipher ServerManagement {App.DisplayVersion} self-test (AI off) {DateTime.Now:yyyy-MM-dd HH:mm:ss} — {failures} failure(s)\n\n");
        File.WriteAllText(reportPath, log.ToString());
        Application.Current.Shutdown(failures == 0 ? 0 : 1);
    }
}
