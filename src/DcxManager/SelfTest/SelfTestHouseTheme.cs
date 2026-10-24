using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DcxManager.Services;
using ThemeMode = DcxManager.Services.ThemeMode;   // .NET 9+ WPF has System.Windows.ThemeMode too
using DcxManager.Views;

namespace DcxManager;

/// <summary>
/// 0.2.1: the DarkCipher house theme (shared across the DarkCipher Systems apps) — presets and their contrast rules, the accent rule, the
/// extra tokens, corners and motion, Segoe Fluent Icons in the sidebar, the sidebar styles and pinned pages, the tabbed
/// Settings layout (switches, 340 px dropdowns, captions, resets per card) and appearance import / export. Renders the
/// new screens in dark, light and high contrast. No window or dialog is shown.
/// </summary>
public static class HouseThemeTests
{
    public static async Task RunAsync(MainWindow main, string reportDir, Action<string> ok, Action<string, Exception?> fail, Action<Window, string> snap)
    {
        async Task Settle(int ms = 200)
        {
            await Task.Delay(ms);
            await Application.Current.Dispatcher.InvokeAsync(() => main.UpdateLayout(), DispatcherPriority.ApplicationIdle);
        }
        var shots = Directory.CreateDirectory(Path.Combine(reportDir, "shots")).FullName;
        var s = AppSettings.Current;
        var was = AppearanceTransfer.Export(s);
        var wasPins = s.PinnedNavItems.ToList();
        var res = Application.Current.Resources;
        Color C(string key) => ((SolidColorBrush)res[key]).Color;
        try
        {
            // 1. Presets: the contrast rules of the house theme, on every preset and the light palette.
            try
            {
                var checks = 0;
                void Need(string what, Color a, Color b, double min)
                {
                    checks++;
                    var r = ThemeManager.Contrast(a, b);
                    if (r < min) throw new Exception($"{what}: {r:0.00}:1 < {min}:1");
                }
                // 0.6.9: every theme pack, dark and light (a pack without a light palette is checked as DarkCipher Light,
                // what light mode paints), its own accent as painted, and every swatch as painted.
                foreach (var pack in ThemeManager.Presets)
                    foreach (var dark in pack.AlwaysDark ? new[] { true } : new[] { true, false })
                    {
                        var id = pack.Id;
                        var tag = $"{id} {(dark ? "dark" : "light")}";
                        Color P(string k) => ThemeManager.PresetColour(id, k, dark);
                        Need($"{tag} body text on background", P("Fg"), P("Bg"), 7); Need($"{tag} body text on cards", P("Fg"), P("Surface"), 7);
                        Need($"{tag} strong text", P("FgStrong"), P("Bg"), 7); Need($"{tag} header text", P("HeaderFg"), P("HeaderBg"), 7);
                        Need($"{tag} secondary text", P("Muted"), P("Bg"), 4.5); Need($"{tag} secondary text on cards", P("Muted"), P("Surface"), 4.5);
                        foreach (var k in new[] { "Good", "Warn", "Bad", "Info", "Critical" }) Need($"{tag} {k}", P(k), P("Bg"), 3);
                        Need($"{tag} own accent", ThemeManager.AccentFor(ThemeManager.ParseHex(pack.Accent)!.Value, dark, id), P("Bg"), 3);
                        foreach (var (name, hex) in ThemeManager.AccentSwatches) Need($"{tag} accent {name}", ThemeManager.AccentFor(ThemeManager.ParseHex(hex)!.Value, dark, id), P("Bg"), 3);
                        // Environment labels are a safety signal: the same under every pack.
                        foreach (var k in new[] { "EnvProd", "EnvTest", "EnvLab", "EnvDev" })
                            if (P(k) != ThemeManager.PresetColour("darkcipher", k, dark)) throw new Exception($"{tag}: {k} changed");
                    }

                // Applying a pack paints it in both modes, with the house theme's extra keys and derived colours.
                foreach (var pack in ThemeManager.Presets)
                    foreach (var m in new[] { ThemeMode.Dark, ThemeMode.Light })
                    {
                        var id = pack.Id;
                        s.ThemePreset = id; s.FollowWindowsHighContrast = false;
                        ThemeManager.Apply(m);
                        var dark = m == ThemeMode.Dark || pack.AlwaysDark;
                        if (ThemeManager.IsDark != dark || C("Bg") != ThemeManager.PresetColour(id, "Bg", dark) || ThemeManager.EffectivePreset != id) throw new Exception($"pack not applied: {id} {m}");
                        foreach (var k in new[] { "Surface2", "FgStrong", "LineStrong", "AccentDim", "HeaderMuted", "FgDisabled", "AltRow", "DangerLine", "Info", "Critical", "Unknown" })
                            if (res[k] is not SolidColorBrush) throw new Exception($"{id}: no {k}");
                    }
                // A pack's own accent is painted as its light accent in light mode; any other accent follows the shared rule.
                s.ThemePreset = "neutral"; s.AccentColor = "4CC2FF"; ThemeManager.Apply(ThemeMode.Light);
                if (ThemeManager.HexString("Accent") != "005FB8") throw new Exception("neutral light accent: " + ThemeManager.HexString("Accent"));
                ThemeManager.Apply(ThemeMode.Dark);
                if (ThemeManager.HexString("Accent") != "4CC2FF") throw new Exception("neutral dark accent: " + ThemeManager.HexString("Accent"));
                s.AccentColor = AppSettings.DefaultAccent;
                if (new AppSettings().ThemePreset != "darkcipher" || ThemeManager.Preset("nonsense").Id != "darkcipher") throw new Exception("the default pack is not DarkCipher");
                if (ThemeManager.Presets.Count(p => p.HasLight && !p.Id.StartsWith("dark", StringComparison.Ordinal)) < 3) throw new Exception("fewer than three everyday dark + light packs");
                s.ThemePreset = "darkcipher";
                ThemeManager.Apply(ThemeMode.Dark);
                var standardGood = C("Good");
                s.StatusIntensity = "subtle"; ThemeManager.Apply(ThemeMode.Dark);
                var subtleGood = C("Good");
                s.StatusIntensity = "standard"; ThemeManager.Apply(ThemeMode.Dark);
                if (subtleGood == standardGood) throw new Exception("status intensity had no effect");
                if (!ThemeManager.CheckAccent("36D2CB").Ok || ThemeManager.CheckAccent("0B1314").Ok || ThemeManager.CheckAccent("nonsense").Ok) throw new Exception("accent rule");
                var probe = new AppSettings { ThemePreset = "neon", Corners = 7, Motion = "fast", StatusIntensity = "loud", Theme = "Purple", AccentColor = "zz" };
                if (ThemeManager.Normalize(probe).Count != 6 || probe is not { ThemePreset: "darkcipher", Corners: 2, Motion: "windows", StatusIntensity: "standard", Theme: "System", AccentColor: AppSettings.DefaultAccent })
                    throw new Exception("normalize");
                ok($"house theme presets (0.6.9 theme packs): {ThemeManager.Presets.Count} packs ({string.Join(", ", ThemeManager.Presets.Select(p => $"{p.Name} {p.Modes}"))}) meet the house theme's contrast rules in dark and light ({checks} checks: body, strong and header text 7:1, secondary 4.5:1 on background and cards, status 3:1, each pack's own accent and every accent swatch 3:1 as painted, environment labels unchanged), DarkCipher stays the default, a pack's own accent paints its light accent in light mode, each pack paints its colours in both modes with the extra keys (Surface2, FgStrong, LineStrong, AccentDim, Info, Critical…), status intensity changes the status colours, a custom accent under 3:1 is refused, out-of-range stored values are corrected");
            }
            catch (Exception ex) { fail("house theme presets", ex); }

            // 2. Tokens and styles: corners, icons, motion.
            try
            {
                foreach (var r in new[] { 0, 4, 2 })
                {
                    s.Corners = r; NavLayout.ApplyCorners();
                    if (res["Radius"] is not CornerRadius cr || cr.TopLeft != r) throw new Exception("corners " + r);
                }
                if (res["IconFont"] is not FontFamily icons || !icons.Source.StartsWith("Segoe Fluent Icons", StringComparison.Ordinal)) throw new Exception("IconFont");
                foreach (var k in new[] { "Switch", "CardTitle", "Caption", "PageTitle", "TabStripItem" }) if (main.TryFindResource(k) is not Style) throw new Exception("style " + k);
                s.Motion = "reduced"; NavLayout.ApplyMotion();
                if (!NavLayout.Reduced || main.PromptCursor.HasAnimatedProperties) throw new Exception("reduced motion still animates the cursor");
                s.Motion = "standard"; NavLayout.ApplyMotion();
                if (!main.PromptCursor.HasAnimatedProperties) throw new Exception("standard motion doesn't blink the cursor");
                s.Motion = "windows"; NavLayout.ApplyMotion();
                ok("house theme tokens: corners 0 / 4 / 2 px reach the Radius token, IconFont is Segoe Fluent Icons (Segoe MDL2 Assets fallback), the Switch / CardTitle / Caption / PageTitle / TabStripItem styles exist, reduced animation stops the header cursor's blink and standard brings it back");
            }
            catch (Exception ex) { fail("house theme tokens", ex); }

            // 3. Sidebar: Fluent icons, icons-only, compact, pinned pages (max 6), landing page.
            try
            {
                s.SidebarStyle = "expanded"; s.PinnedNavItems = new(); main.BuildNav();
                bool HasFluent(DependencyObject root) => Menus.Descendants<TextBlock>(root).Any(t => t.FontFamily?.Source.StartsWith("Segoe Fluent Icons", StringComparison.Ordinal) == true);
                var navItems = main.Nav.Children.OfType<FrameworkElement>().SelectMany(Menus.Descendants<RadioButton>).ToList();
                if (!navItems.Any() || !HasFluent(main.Nav)) throw new Exception("sidebar items without Fluent icons");
                var keys = main.AllNavKeys.Where(k => !k.StartsWith("local:", StringComparison.Ordinal)).Take(7).ToList();
                int pinnedOk = keys.Count(k => main.SetNavPinned(k, true));
                if (pinnedOk != NavLayout.MaxPinned || s.PinnedNavItems.Count != NavLayout.MaxPinned) throw new Exception($"pinned {pinnedOk}");
                await Settle(200);
                var head = main.Nav.Children.OfType<TextBlock>().FirstOrDefault();
                if (head == null || !new System.Windows.Documents.TextRange(head.ContentStart, head.ContentEnd).Text.Contains("PINNED")) throw new Exception("no PINNED section at the top");
                snap(main, Path.Combine(shots, "sidebar-pinned.png"));
                s.SidebarStyle = "icons"; main.BuildNav(); await Settle(250);
                if (main.Nav.Children.OfType<TextBlock>().Any() || Math.Abs(main.SideCol.Width.Value - 52) > 0.5) throw new Exception("icons-only still has headers or width " + main.SideCol.Width);
                var rail = main.Nav.Children.OfType<RadioButton>().ToList();
                if (rail.Count < 10 || rail.Any(r => r.ToolTip is not string || System.Windows.Automation.AutomationProperties.GetName(r).Length == 0)) throw new Exception("icon rail without names");
                snap(main, Path.Combine(shots, "sidebar-icons.png"));
                s.SidebarStyle = "compact"; NavLayout.ApplyDensity(); main.BuildNav();
                if (res["NavItemPadding"] is not Thickness pad || pad.Top > 2) throw new Exception("compact padding");
                s.SidebarStyle = "expanded"; NavLayout.ApplyDensity();
                foreach (var k in keys) main.SetNavPinned(k, false);
                // Shown pages: a hidden page leaves the sidebar (and its pin), Settings can't be hidden, and it comes back.
                bool InNav(string k) => Menus.Descendants<RadioButton>(main.Nav).Any(r => r.Tag as string == k);
                main.SetNavPinned("snippets", true);
                main.SetNavHidden("snippets", true);
                if (InNav("snippets") || !s.HiddenNavItems.Contains("snippets")) throw new Exception("a hidden page is still in the sidebar");
                if (main.SetNavHidden(NavLayout.AlwaysShownPage, true) || s.HiddenNavItems.Contains(NavLayout.AlwaysShownPage) || !InNav(NavLayout.AlwaysShownPage)) throw new Exception("Settings could be hidden");
                main.SetNavHidden("snippets", false); main.SetNavPinned("snippets", false);
                if (!InNav("snippets") || s.HiddenNavItems.Count != 0) throw new Exception("a shown-again page didn't come back");
                var hp = new AppSettings { HiddenNavItems = new() { "settings", "notes", "notes", " " } };
                if (!NavLayout.Normalize(hp, main.AllNavKeys).Contains("hidden pages") || !hp.HiddenNavItems.SequenceEqual(new[] { "notes" })) throw new Exception("hidden pages normalize");
                var probe = new AppSettings { LandingPage = "no-such-page", SidebarStyle = "wide", PinnedNavItems = new() { "hosts", "hosts", "ghost" } };
                if (NavLayout.Normalize(probe, main.AllNavKeys).Count != 3 || probe.LandingPage != "" || probe.SidebarStyle != "expanded" || !probe.PinnedNavItems.SequenceEqual(new[] { "hosts" })) throw new Exception("navigation normalize");
                ok($"sidebar: every page drawn with a Segoe Fluent icon, pinning stops at {NavLayout.MaxPinned} and shows a PINNED section first, a hidden page leaves the sidebar and its pins and comes back (Settings can't be hidden), icons only = a 52 px strip without headers where every page keeps its name (tooltip + screen-reader name), compact tightens rows, unknown landing page / style / pins corrected; rendered sidebar-pinned.png and sidebar-icons.png");
            }
            catch (Exception ex) { fail("sidebar styles and pins", ex); }
            finally { s.SidebarStyle = "expanded"; NavLayout.ApplyDensity(); main.BuildNav(); }

            // 4. The tabbed Settings layout.
            try
            {
                if (main.Panes.AllTabs.FirstOrDefault(t => t.Content is SettingsView) is { } st0) main.CloseTab(st0);
                main.Navigate("settings");
                await Settle(500);
                var sv = SettingsView.Current ?? throw new Exception("no settings view");
                sv.RevealCategory("Appearance");
                await Settle(200);
                if (!sv.VisibleCardsForTest.SequenceEqual(new[] { "Theme", "Branding & colours", "Layout and text", "Typography" })) throw new Exception("Appearance tab: " + string.Join(", ", sv.VisibleCardsForTest));
                sv.RevealCategory("Navigation");
                if (!sv.VisibleCardsForTest.SequenceEqual(new[] { "Sidebar", "Navigation & explorers" })) throw new Exception("Navigation tab: " + string.Join(", ", sv.VisibleCardsForTest));
                int combos = 0, switches = 0, captions = 0, resets = 0;
                foreach (var title in sv.CardTitlesForTest)
                {
                    var card = sv.CardForTest(title)!;
                    foreach (var cb in Menus.Descendants<ComboBox>(card).Where(c => !c.IsEditable && Menus.Descendants<ListView>(card).All(l => !l.IsAncestorOf(c))))
                    {
                        combos++;
                        if (Math.Abs(cb.Width - SettingsView.FieldWidth) > 0.5) throw new Exception($"{title}: a dropdown is {cb.Width} px");
                    }
                    switches += Menus.Descendants<CheckBox>(card).Count(c => c.Style == (Style)Application.Current.FindResource("Switch"));
                    captions += Menus.Descendants<TextBlock>(card).Count(t => t.Style == (Style)Application.Current.FindResource("Caption"));
                    resets += Menus.Descendants<Button>(card).Count(b => (b.Content as string)?.StartsWith("Reset ", StringComparison.Ordinal) == true);
                }
                var sw = (Style)Application.Current.FindResource("Switch");
                var notSwitch = Menus.Descendants<CheckBox>(sv.CardForTest("Terminal & connections")!).Where(c => c.Style != sw).ToList();
                if (notSwitch.Count > 0) throw new Exception("on/off settings that aren't switches: " + string.Join("; ", notSwitch.Select(c => $"{(c.Content as TextBlock)?.Text ?? c.Content?.ToString()} (style: {(c.Style == null ? "none" : c.Style.TargetType.Name + (c.Style.BasedOn != null ? " based on another" : ""))})")));
                if (resets < SettingsView.Resettable.Count) throw new Exception($"resets {resets}");
                foreach (var cat in new[] { "Appearance", "Navigation" })
                {
                    sv.RevealCategory(cat); await Settle(400);
                    snap(main, Path.Combine(shots, $"settings-{cat.ToLowerInvariant()}-tab.png"));
                }
                ok($"settings layout: {sv.TabsForTest.Count} tabs across the top under the search box, Appearance = Theme + Layout and text + Typography, Navigation = Sidebar + Navigation & explorers, {combos} dropdowns all {SettingsView.FieldWidth} px, {switches} on/off settings as toggle switches, {captions} captions, {resets} per-card resets; rendered the Appearance and Navigation tabs");
            }
            catch (Exception ex) { fail("settings layout", ex); }

            // 5. Import & export.
            try
            {
                var json = AppearanceTransfer.Export(s);
                var o = JsonNode.Parse(json)!.AsObject();
                var keys = o.Select(kv => kv.Key).ToList();
                foreach (var forbidden in new[] { "key", "token", "password", "secret", "host", "credential", "path", "dir" })
                    if (keys.Any(k => k.Contains(forbidden, StringComparison.OrdinalIgnoreCase))) throw new Exception("export holds a " + forbidden + " field");
                var before = (s.ThemePreset, s.Corners, s.SidebarStyle, s.AccentColor);
                var changed = new AppSettings { ThemePreset = "graphite", Corners = 4, SidebarStyle = "icons", AccentColor = "3FB6FF", PinnedNavItems = new() { "hosts" } };
                var plan = AppearanceTransfer.Read(AppearanceTransfer.Export(changed), s, main.AllNavKeys, MainWindow.NavSections);
                if (plan.Changes.Count < 5 || !AppearanceTransfer.Describe(plan).Contains("Theme preset: darkcipher → graphite")) throw new Exception("plan: " + AppearanceTransfer.Describe(plan));
                if (s.ThemePreset != before.ThemePreset) throw new Exception("reading a file changed settings");
                var tampered = JsonNode.Parse(AppearanceTransfer.Export(changed))!.AsObject();
                tampered["zoom"] = 9; tampered["corners"] = 13; tampered["accent"] = "red"; tampered["pinnedPages"] = new JsonArray("hosts", "ghost-page"); tampered["hiddenSections"] = new JsonArray(MainWindow.NavSections.Select(x => (JsonNode)x).ToArray());
                var p2 = AppearanceTransfer.Read(tampered.ToJsonString(), s, main.AllNavKeys, MainWindow.NavSections);
                if (p2.Skipped.Count < 5 || p2.Changes.Any(c => c.Name is "Interface zoom" or "Corners" or "Accent" or "Hidden sections")) throw new Exception("tampered values accepted: " + AppearanceTransfer.Describe(p2));
                Exception? Bad(string text) { try { AppearanceTransfer.Read(text, s); return null; } catch (Exception ex) { return ex; } }
                if (Bad("not json") is not InvalidDataException || Bad("{\"format\":\"other\"}") is not InvalidDataException || Bad("{\"format\":\"dcx-appearance\",\"version\":99}") is not InvalidDataException || Bad(new string(' ', AppearanceTransfer.MaxBytes + 1)) is not InvalidDataException)
                    throw new Exception("a non-export file was accepted");
                var sv = SettingsView.Current ?? throw new Exception("no settings view");
                sv.ApplyImport(plan);
                await Settle(300);
                if (s is not { ThemePreset: "graphite", Corners: 4, SidebarStyle: "icons", AccentColor: "3FB6FF" } || C("Bg") != ThemeManager.PresetColour("graphite", "Bg", true) && ThemeManager.IsDark)
                    throw new Exception("import not applied");
                if (!AuditLog.Read(DateTime.Now.ToString("yyyy-MM")).Any(a => a.Action == "appearance imported")) throw new Exception("import not audited");
                ok($"appearance import / export: the export holds only appearance and navigation ({keys.Count} fields, no key / token / password / host / path), reading a file changes nothing, the preview lists each change (“Theme preset: darkcipher → graphite”), out-of-range zoom / corners / accent, an unknown page and hiding every section are left out and listed, non-export / newer / oversized files refused, applying repaints and is audited");
            }
            catch (Exception ex) { fail("appearance import / export", ex); }

            // 6. Right-click menus organised into groups: short menus flat, long ones fold their groups into submenus.
            try
            {
                static int Top(ContextMenu m) => m.Items.OfType<MenuItem>().Count();
                ContextMenu Build(IEnumerable<object?> items) { var m = new ContextMenu(); Menus.Fill(m.Items, items); return m; }
                MenuItem I(string h) => Menus.Item(h, () => { });
                var shortMenu = Build(new object?[] { I("a"), I("b"), Menus.Group("G", I("c"), I("d")), Menus.Group("Empty", null, null) });
                if (Top(shortMenu) != 4 || shortMenu.Items.OfType<MenuItem>().Any(m => m.HasItems)) throw new Exception("a short menu folded");
                var longMenu = Build(Enumerable.Range(0, 9).Select(i => (object?)I("item " + i))
                    .Append(Menus.Group("First group", I("x1"), I("x2"), I("x3"))).Append(Menus.Group("Second group", I("y1"), Menus.Sep(), I("y2"))).Append(Menus.Group("Single", I("z1"))));
                var heads = longMenu.Items.OfType<MenuItem>().Select(m => m.Header as string).ToList();
                if (Top(longMenu) != 12 || !heads.Contains("First group") || !heads.Contains("Second group") || !heads.Contains("z1")
                    || longMenu.Items.OfType<MenuItem>().First(m => (string)m.Header == "First group").Items.Count != 3 || longMenu.Items[^1] is Separator)
                    throw new Exception("long menu: " + string.Join(" | ", heads));
                // Every attached menu on the main screens, built as when it opens (with the first row selected where there is a list).
                foreach (var key in new[] { "dashboard", "hosts", "credentials", "commander", "infra", "notes", "incidents", "snippets", "cloud" }) { main.Navigate(key); await Settle(250); }
                int built = 0, biggest = 0; string biggestName = "";
                foreach (var tab in main.Panes.AllTabs.ToList())
                {
                    if (tab.Content is not DependencyObject content) continue;
                    foreach (var fe in new[] { (FrameworkElement)tab }.Concat(Menus.Descendants<FrameworkElement>(content)))
                    {
                        if (fe is ListView { Items.Count: > 0 } lv && lv.SelectedIndex < 0) lv.SelectedIndex = 0;
                        if (Menus.BuilderOf(fe) is not { } builder) continue;
                        List<object?> items;
                        try { items = builder().ToList(); } catch { continue; }
                        var m = Build(items);
                        if (Top(m) == 0) continue;
                        built++;
                        if (Top(m) > biggest) { biggest = Top(m); biggestName = $"{MainWindow.ScreenName(tab)} › {fe.GetType().Name}"; }
                    }
                }
                // The terminal's menu opens through Menus.ShowAt (WebView2 has no attached menu): recorded, never shown.
                var term = main.Panes.AllTabs.Select(t => t.Content).OfType<TerminalView>().FirstOrDefault();
                if (term != null)
                {
                    int before; lock (Menus.ShownForTests) before = Menus.ShownForTests.Count;
                    term.ShowMenuForTest("sample selection");
                    string[] termMenu; lock (Menus.ShownForTests) termMenu = Menus.ShownForTests.Count == before + 1 ? Menus.ShownForTests[^1] : throw new Exception("the terminal menu wasn't built");
                    if (termMenu.Length > Menus.GroupThreshold || !termMenu.Contains("Selection & notes") || !termMenu.Contains("Copy")) throw new Exception("terminal menu: " + string.Join(" | ", termMenu));
                    built++;
                    if (termMenu.Length > biggest) { biggest = termMenu.Length; biggestName = "terminal"; }
                }
                if (built < 15) throw new Exception($"only {built} menus built");
                if (biggest > Menus.GroupThreshold) throw new Exception($"a menu still has {biggest} top-level rows: {biggestName}");
                ok($"right-click menus: grouped — a short menu stays flat, a long one folds each group into a submenu under its name (one-item groups stay inline, empty groups vanish); {built} menus on the main screens built with a row selected{(term != null ? " plus the terminal's (recorded, not opened)" : "")}, the longest top level {biggest} rows ({biggestName})");
            }
            catch (Exception ex) { fail("menu groups", ex); }

            // 7. Renders in light mode and high contrast.
            try
            {
                var back = AppearanceTransfer.Read(was, s, null, null);
                AppearanceTransfer.Apply(back, s);
                s.PinnedNavItems = wasPins;
                SettingsView.ApplyLook();
                main.Navigate("settings");
                SettingsView.Current?.RevealCategory("Appearance");
                ThemeManager.Apply(ThemeMode.Light); await Settle(500);
                // 0.6.9: the theme pack tiles, one per pack, the chosen one outlined.
                var tiles = SettingsView.PresetTilesForTest(main);
                if (tiles.Count != ThemeManager.Presets.Count || tiles.Count(t => t.Chosen) != 1 || tiles.Single(t => t.Chosen).Name != ThemeManager.Preset(s.ThemePreset).Name)
                    throw new Exception("theme pack tiles: " + string.Join(", ", tiles.Select(t => t.Name + (t.Chosen ? "*" : ""))));
                snap(main, Path.Combine(shots, "settings-light.png"));
                var packShots = new List<string>();
                foreach (var (id, m) in new[] { ("neutral", ThemeMode.Light), ("neutral", ThemeMode.Dark), ("slate", ThemeMode.Light), ("slate", ThemeMode.Dark), ("arctic", ThemeMode.Light), ("arctic", ThemeMode.Dark) })
                {
                    s.ThemePreset = id; s.AccentColor = ThemeManager.Preset(id).Accent;   // as choosing the pack in Settings does
                    ThemeManager.Apply(m); await Settle(400);
                    if (C("Bg") != ThemeManager.PresetColour(id, "Bg", m == ThemeMode.Dark)) throw new Exception($"{id} {m} background");
                    var file = $"theme-{id}-{m.ToString().ToLowerInvariant()}.png";
                    snap(main, Path.Combine(shots, file));
                    packShots.Add(file);
                }
                s.ThemePreset = "highcontrast"; s.AccentColor = AppSettings.DefaultAccent; ThemeManager.Apply(ThemeMode.Dark); await Settle(500);
                if (C("Bg") != Colors.Black) throw new Exception("high contrast background");
                snap(main, Path.Combine(shots, "settings-highcontrast.png"));
                ok($"house theme renders: Settings → Appearance with {tiles.Count} theme pack tiles (the chosen one outlined) in DarkCipher Light (settings-light.png), the everyday packs in both modes ({string.Join(", ", packShots)}) and DarkCipher High Contrast (settings-highcontrast.png)");
            }
            catch (Exception ex) { fail("house theme renders", ex); }
        }
        finally
        {
            try { AppearanceTransfer.Apply(AppearanceTransfer.Read(was, s, null, null), s); } catch { }
            s.PinnedNavItems = wasPins;
            AppSettings.Save();
            SettingsView.ApplyLook();
        }
    }
}
