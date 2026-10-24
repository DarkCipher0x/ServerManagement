using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DcxManager.Services;
using ThemeMode = DcxManager.Services.ThemeMode;   // .NET 9+ WPF has System.Windows.ThemeMode too
using DcxManager.Ssh;
using DcxManager.Views;
using DcxManager.Vultr;
using DcxManager.Documents;

namespace DcxManager;

// SelfTest: panes, screenshots, the sidebar, the AI sidebar, typography, network classes, paste safety, privacy, renders (moved unchanged out of RunFullAsync in 0.2.0 phase 6½; the run's shared state is SelfTestRun).
public static partial class SelfTest
{
    private static async Task UiAsync(SelfTestRun sharedRun)
    {
        var main = sharedRun.Main; var reportPath = sharedRun.ReportPath; Action<string> Ok = sharedRun.Ok; Action<string, Exception?> Fail = sharedRun.Fail; SelfTestRun.SettleFn Settle = sharedRun.Settle; var testHost = sharedRun.TestHost!;

        // Panes: split, tile, merge, pop out and dock back with the live terminal inside — page must stay bridged.
        try
        {
            var term = main.Panes.AllTabs.Select(t => t.Content).OfType<TerminalView>().LastOrDefault() ?? throw new Exception("no terminal tab");
            var termTab = main.Panes.AllTabs.First(t => t.Content == term);
            int tabs = main.Panes.AllTabs.Count();
            if (!main.Panes.Split(termTab, Orientation.Horizontal)) throw new Exception("split refused");
            await Settle(300);
            if (main.Panes.PaneCount != 2) throw new Exception("pane count after split " + main.Panes.PaneCount);
            main.Panes.Tile(); await Settle(300);
            if (main.Panes.PaneCount < 2) throw new Exception("tile");
            main.Panes.PopOut(termTab); await Settle(400);
            if (!main.Panes.IsFloating(termTab)) throw new Exception("not floating");
            main.Panes.DockBack(termTab); await Settle(400);
            if (main.Panes.IsFloating(termTab)) throw new Exception("still floating");
            main.Panes.MergeAll(); await Settle(300);
            if (main.Panes.PaneCount != 1) throw new Exception("merge left " + main.Panes.PaneCount);
            if (main.Panes.AllTabs.Count() != tabs) throw new Exception($"tab count changed {tabs} → {main.Panes.AllTabs.Count()}");
            if (!term.PageReady) throw new Exception("terminal page lost after re-parenting — " + term.DiagStatus);
            Ok("panes: split / tile / pop out / dock back / merge with a live terminal, page still bridged");
        }
        catch (Exception ex) { Fail("panes", ex); }

        // Screenshots: a WPF element via PrintWindow and the terminal via WebView2 capture.
        try
        {
            main.Navigate("hosts"); await Settle(400);
            var img = Screenshot.CaptureElement((FrameworkElement)main.Content);
            var png = Screenshot.Png(img);
            if (img.PixelWidth < 200 || png.Length < 2000) throw new Exception($"element capture {img.PixelWidth}×{img.PixelHeight}, {png.Length} bytes");
            var term = main.Panes.AllTabs.Select(t => t.Content).OfType<TerminalView>().LastOrDefault() ?? throw new Exception("no terminal tab");
            main.Panes.Select(main.Panes.AllTabs.First(t => t.Content == term)); await Settle(400);
            var timg = await term.CaptureAsync();
            if (timg.PixelWidth < 100) throw new Exception("terminal capture " + timg.PixelWidth);
            var name = Screenshot.FileName("web 01/test");
            if (name.Contains('/') || name.Contains(' ') || !name.EndsWith(".png")) throw new Exception("file name " + name);
            Ok($"screenshots: element {img.PixelWidth}×{img.PixelHeight} via PrintWindow, terminal {timg.PixelWidth}×{timg.PixelHeight} via WebView2");
        }
        catch (Exception ex) { Fail("screenshots", ex); }

        // Sidebar customisation + density: order/hide logic, encrypted persistence, the live sidebar and the row-padding resources.
        try
        {
            // The pure order logic on a fixed list (TEAM only exists while a library add-on is enabled).
            var defs = new[] { "OVERVIEW", "SERVERS", "LOCAL", "VAULT", "AI", "SYSTEM" };
            var o1 = NavLayout.Order(defs, new[] { "AI", "GONE", "OVERVIEW", "AI" });
            // unknown names dropped, duplicates removed, missing sections placed after their default predecessor
            if (string.Join(",", o1) != "AI,SYSTEM,OVERVIEW,SERVERS,LOCAL,VAULT") throw new Exception("order merge: " + string.Join(",", o1));
            if (string.Join(",", NavLayout.Move(defs, "SYSTEM", -2)) != "OVERVIEW,SERVERS,LOCAL,SYSTEM,VAULT,AI") throw new Exception("move up");
            if (string.Join(",", NavLayout.Move(defs, "OVERVIEW", -1)) != string.Join(",", defs)) throw new Exception("move clamp");
            if (string.Join(",", NavLayout.MoveTo(defs, "SERVERS", "AI")) != "OVERVIEW,LOCAL,VAULT,AI,SERVERS,SYSTEM") throw new Exception("drag down");
            if (string.Join(",", NavLayout.MoveTo(defs, "AI", "SERVERS")) != "OVERVIEW,AI,SERVERS,LOCAL,VAULT,SYSTEM") throw new Exception("drag up");
            if (NavLayout.CanHide(new[] { "A", "B" }, new[] { "B" }, "A") || !NavLayout.CanHide(new[] { "A", "B" }, new string[0], "A")) throw new Exception("last visible section guard");

            var s = AppSettings.Current;
            main.MoveNavSection("SYSTEM", -MainWindow.NavSections.Length);
            main.SetNavSectionHidden("LOCAL", true);
            s.Density = "compact"; NavLayout.ApplyDensity(); AppSettings.Save();
            var disk = SecureStore.Read<AppSettings>("settings.dcx");
            if (disk.NavSectionOrder.FirstOrDefault() != "SYSTEM" || !disk.HiddenNavSections.Contains("LOCAL") || disk.Density != "compact") throw new Exception("settings round trip");
            if (Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(SecureStore.DataDir, "settings.dcx"))).Contains("HiddenNavSections")) throw new Exception("plaintext in settings.dcx");
            var nav = (StackPanel)main.FindName("Nav");
            var heads = nav.Children.OfType<TextBlock>().Select(t => t.Tag as string ?? "").ToList();
            // TEAM is a real section now (0.3.0 §22, team features built in): the live sidebar is compared with NavSections.
            if (heads.FirstOrDefault() != "SYSTEM" || heads.Contains("LOCAL") || heads.Count != MainWindow.NavSections.Length - 1) throw new Exception("sidebar headers: " + string.Join(",", heads));
            main.Navigate("vultr:Instances"); await Settle(250);   // old cloud links still open (now inside the Cloud providers workspace)
            if (Application.Current.Resources["RowPadding"] is not Thickness { Top: 1 }) throw new Exception("compact padding not applied");
            await Settle(300);
            var shotDir = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(reportPath)!, "shots")).FullName;
            Snap(main, Path.Combine(shotDir, "sidebar-compact.png"));
            main.ResetNavLayout();
            s.Density = "comfortable"; NavLayout.ApplyDensity(); AppSettings.Save();
            if (Application.Current.Resources["RowPadding"] is not Thickness { Top: 5 } || nav.Children.OfType<TextBlock>().Count() != MainWindow.NavSections.Length) throw new Exception("reset");
            Ok("sidebar: order merge/move/drag, last-section guard, hide + reorder persisted encrypted, live headers, density resources, reset");
        }
        catch (Exception ex) { Fail("sidebar customisation", ex); }

        // AI explain helpers: error text is stripped of source paths / profile / secrets; settings options expose no text-box values.
        try
        {
            Exception thrown;
            try { throw new InvalidOperationException("boom password=hunter2-secret-value " + Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + @"\x.txt"); }
            catch (Exception e) { thrown = e; }
            var et = Ai.AiActions.ErrorText("Connection failed", thrown);
            if (et.Contains("hunter2-secret-value") || et.Contains(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.OrdinalIgnoreCase)
                || System.Text.RegularExpressions.Regex.IsMatch(et, @":line \d+") || !et.Contains("InvalidOperationException")) throw new Exception("error text: " + et);
            if (Ai.AiActions.HelpFor("Settings Appearance density", 3).Count == 0) throw new Exception("no help grounding for settings");
            var sys = Ai.AiActions.HelpSystem(Ai.AiActions.HelpFor("File commander", 2));
            if (!sys.Contains("# Help articles") || sys.Contains("(none matched)")) throw new Exception("screen grounding");
            main.Navigate("settings"); await Settle(300);
            var tab = main.Panes.ActiveTab!;
            if (MainWindow.ScreenName(tab) != "Settings") throw new Exception("screen name " + MainWindow.ScreenName(tab));
            var termTab = main.Panes.AllTabs.FirstOrDefault(t => t.Content is TerminalView);
            if (termTab != null && MainWindow.ScreenName(termTab) != "Terminal") throw new Exception("host tab leaks its name");
            Ok("AI explain: error text sanitised (paths, profile, secrets), help grounding for settings/screens, screen names without host names");
        }
        catch (Exception ex) { Fail("AI explain helpers", ex); }

        // AI Engineer sidebar: one workspace; detach/reattach moves the same panel and conversation; no leaked windows or
        // handlers; closing the window docks it back collapsed; state + width persist encrypted; ✦ actions land in it.
        // The model call is replaced by a canned answer, so nothing is ever sent anywhere.
        {
            var ws = Ai.AiWorkspace.Current;
            var dock = AiModule.Dock;
            var realChat = ws.Chat;
            ws.Chat = (m, _) => Task.FromResult(new System.Text.Json.Nodes.JsonObject { ["role"] = "assistant", ["content"] = $"Demo answer ({m.Count} message(s) sent; nothing left the PC)." });
            var s = AppSettings.Current;
            bool aiOn = s.AiEnabled, filesOn = s.AiFeatureFiles, helpOn = s.AiFeatureHelper; string engine = s.AiEngine, ollama = s.OllamaUrl; string? tunnel = s.OllamaTunnelHostId;
            s.AiEnabled = true; s.AiFeatureFiles = true; s.AiFeatureHelper = true; s.AiEngine = "ollama-gpu"; s.OllamaUrl = "http://localhost:11434"; s.OllamaTunnelHostId = null;
            int AiWindows() => Application.Current.Windows.OfType<AiWindow>().Count();
            try
            {
                try
                {
                    int instances = Ai.AiWorkspace.Instances;
                    dock.Apply(AiDockState.Docked); await Settle(200);
                    var conv = ws.Active;
                    await ws.SendAsync("Which ports does a default nginx listen on?");
                    if (conv.Messages.Count != 2 || conv.Messages[1].Role != "assistant") throw new Exception("docked turn: " + conv.Messages.Count);
                    var dockHost = (Border)main.FindName("DockHost");
                    if (dockHost.Child != dock.Panel) throw new Exception("panel not in the dock");
                    int changedSubs = ws.ChangedSubscribers, focusSubs = ws.FocusSubscribers, convs = ws.Conversations.Count;

                    dock.Apply(AiDockState.Detached); await Settle(300);
                    if (dock.State != AiDockState.Detached || dock.Window == null || Window.GetWindow(dock.Panel) != dock.Window || dockHost.Child != null) throw new Exception("detach did not move the panel");
                    if (ws.Active != conv || conv.Messages.Count != 2 || dock.Window.Panel != dock.Panel) throw new Exception("detach changed the conversation");
                    await ws.SendAsync("And with TLS?");
                    if (conv.Messages.Count != 4) throw new Exception("detached turn: " + conv.Messages.Count);
                    // A change mid-session: a resize plus Reveal. Never minimise or maximise here. A window with no taskbar entry
                    // lands at the bottom-left of the screen when minimised, and a maximised one covers it; both are visible.
                    dock.Window.Width += 40; await Settle(150);
                    dock.Reveal(); await Settle(150);
                    if (dock.State != AiDockState.Detached || dock.Window.WindowState != WindowState.Normal) throw new Exception("reveal changed the detached window");
                    dock.Apply(AiDockState.Docked); await Settle(300);
                    if (dockHost.Child != dock.Panel || dock.Window != null || AiWindows() != 0) throw new Exception("reattach");
                    if (ws.Active != conv || conv.Messages.Count != 4) throw new Exception("reattach changed the conversation");

                    for (int i = 0; i < 6; i++) { dock.Apply(AiDockState.Detached); await Settle(60); dock.Apply(AiDockState.Docked); await Settle(60); }
                    if (AiWindows() != 0) throw new Exception("leaked AI windows: " + AiWindows());
                    if (ws.ChangedSubscribers != changedSubs || ws.FocusSubscribers != focusSubs) throw new Exception($"handlers leaked: {changedSubs}→{ws.ChangedSubscribers}, {focusSubs}→{ws.FocusSubscribers}");
                    if (Ai.AiWorkspace.Instances != instances || ws.Conversations.Count != convs || ws.Active != conv || conv.Messages.Count != 4) throw new Exception("a second workspace or conversation appeared");

                    // Closed by the user (title-bar ×): back in the dock, collapsed, nothing lost.
                    dock.Apply(AiDockState.Detached); await Settle(200);
                    dock.Window!.Close(); await Settle(200);
                    if (dock.State != AiDockState.Collapsed || dock.Window != null || AiWindows() != 0 || ws.Active != conv || conv.Messages.Count != 4) throw new Exception("closing the window: " + dock.State);
                    Ok($"AI sidebar: detach → reattach keeps the same conversation ({conv.Messages.Count} messages) and panel; 6 cycles leak no windows or handlers; window close → collapsed in the dock; one workspace");
                }
                catch (Exception ex) { Fail("AI sidebar detach / reattach", ex); }

                try
                {
                    var shotDir = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(reportPath)!, "shots")).FullName;
                    // Persistence (encrypted): state and width survive a reload from disk.
                    dock.Apply(AiDockState.Docked); dock.SetWidth(430);
                    var disk = SecureStore.Read<AppSettings>("settings.dcx");
                    if (disk.AiPanelState != "docked" || Math.Abs(disk.AiPanelWidth - 430) > 0.5) throw new Exception($"docked not persisted: {disk.AiPanelState} {disk.AiPanelWidth}");
                    if (Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(SecureStore.DataDir, "settings.dcx"))).Contains("AiPanel")) throw new Exception("plaintext in settings.dcx");
                    foreach (var st in new[] { AiDockState.Hidden, AiDockState.Collapsed, AiDockState.Detached })
                    {
                        dock.Apply(st); await Settle(120);
                        if (AiDock.Parse(SecureStore.Read<AppSettings>("settings.dcx").AiPanelState) != st) throw new Exception(st + " not persisted");
                        if (st == AiDockState.Collapsed) Snap(main, Path.Combine(shotDir, "ai-sidebar-collapsed.png"));
                        if (st == AiDockState.Detached) { await Settle(300); Snap(dock.Window!, Path.Combine(shotDir, "ai-sidebar-detached.png")); }
                    }
                    var col = (ColumnDefinition)main.FindName("DockCol");
                    dock.Apply(AiDockState.Hidden);
                    if (col.Width.Value != 0) throw new Exception("hidden column width " + col.Width);
                    dock.Apply(AiDockState.Docked); await Settle(200);
                    var fit = dock.Fitted();
                    if (fit > 430.5 || Math.Abs(col.Width.Value - fit) > 0.5 || Math.Abs(dock.Panel.ActualWidth - fit) > 6) throw new Exception($"docked width {col.Width} / {dock.Panel.ActualWidth} (fitted {fit})");
                    // s40: a big saved width never squeezes the open screen — at most 40 % of the window, the screen keeps 620 DIPs.
                    if (AiDock.FitWidth(750, 1430) > 1430 * AiDock.MaxShare + 0.5 || AiDock.FitWidth(750, 2560) != 750 || AiDock.FitWidth(380, 1430) != 380 || AiDock.FitWidth(750, 900) != AiDock.MinWidth)
                        throw new Exception($"fit: {AiDock.FitWidth(750, 1430)} / {AiDock.FitWidth(750, 2560)} / {AiDock.FitWidth(380, 1430)} / {AiDock.FitWidth(750, 900)}");
                    dock.SetWidth(AiDock.DefaultWidth);

                    // ✦ actions route into the workspace (context item + focus), never a window of their own.
                    dock.Apply(AiDockState.Collapsed);
                    int windows = Application.Current.Windows.Count;
                    var conv = ws.Active; int before = conv.Messages.Count;
                    await Ai.AiActions.AskAsync(Ai.AiFeature.Files, "nginx.conf on web-01 (demo)", "server {\n  listen 203.0.113.10:443 ssl;\n  server_name demo.example;\n}\n", "Explain what this configuration does.",
                        Ai.AiActions.HelpSystem(Ai.AiActions.HelpFor("File commander explain", 1)));
                    await Settle(200);
                    if (Application.Current.Windows.Count != windows) throw new Exception("an action opened a window");
                    if (dock.State != AiDockState.Docked) throw new Exception("action did not reveal the sidebar: " + dock.State);
                    if (ws.Active != conv || conv.Messages.Count != before + 2) throw new Exception("action answer not in the conversation");
                    if (ws.Context.FirstOrDefault(i => i.Title == "nginx.conf on web-01 (demo)") is not { Included: true, Kind: Ai.AiContextKind.Material }) throw new Exception("no ✓ context item for the action");
                    if (!ws.Context.Any(i => i.Kind == Ai.AiContextKind.Help && i.Included)) throw new Exception("help grounding not listed");
                    Ok("AI sidebar: state (docked/hidden/collapsed/detached) and width persist encrypted; hidden = 0 px; ✦ action → context item + revealed sidebar, no extra window");

                    // Density: the same panel in comfortable and compact spacing.
                    await Settle(300);
                    Snap(main, Path.Combine(shotDir, "ai-sidebar-docked.png"));
                    s.Density = "compact"; NavLayout.ApplyDensity(); await Settle(300);
                    var row = Menus.Descendants<TextBlock>(dock.Panel).FirstOrDefault(t => t.Text == "nginx.conf on web-01 (demo)");
                    if (row?.Padding.Top != 1) throw new Exception("compact padding not applied to the context list: " + row?.Padding);
                    Snap(main, Path.Combine(shotDir, "ai-sidebar-compact.png"));
                    s.Density = "comfortable"; NavLayout.ApplyDensity(); AppSettings.Save(); await Settle(200);
                    if (row.Padding.Top != 5) throw new Exception("comfortable padding");
                    Ok("AI sidebar renders: docked (comfortable + compact), collapsed, detached window");
                }
                catch (Exception ex) { Fail("AI sidebar persistence / routing / renders", ex); }

                // The context list never holds a secret: content is masked on entry, credentials are BLOCKED with no content.
                try
                {
                    var keep = ws.Active;
                    var c = ws.NewConversation();
                    const string pem = "-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXktdjEAAAAABG5vbmUAAAAEbm9uZQ\n-----END OPENSSH PRIVATE KEY-----"; // secret-scan: example
                    var secrets = new[] { "hunter2-demo-secret", "b3BlbnNzaC1rZXktdjEAAAAABG5vbmUAAAAEbm9uZQ", "sk-demo0123456789abcdefghijk", "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJkZW1vIn0.c2lnbmF0dXJlZGVtbw", "vault-pass-demo-77" };
                    ws.AddContext("deploy notes password=hunter2-demo-secret", $"db_password = hunter2-demo-secret\n{pem}\napi_key: sk-demo0123456789abcdefghijk\nAuthorization: Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJkZW1vIn0.c2lnbmF0dXJlZGVtbw\n");
                    var cred = ws.AddContext("vault credential demo-root", "Secret=vault-pass-demo-77", Ai.AiContextKind.Credential);
                    ws.SetIncluded(cred, true);
                    var probe = new Credential { Name = "demo-key", Kind = CredentialKind.Password, Secret = "vault-pass-demo-77" };
                    AppData.Current.Credentials.Add(probe);
                    var h = new HostEntry { Name = "web-01", Address = "203.0.113.10", Username = "alex", Auth = AuthMethod.Password, CredentialId = probe.Id };
                    ws.AddContext("host web-01", SafeMetadata.Host(h));
                    AppData.Current.Credentials.Remove(probe);
                    if (cred.Included || !cred.Blocked || cred.Content.Length != 0 || cred.StateText != "BLOCKED") throw new Exception("credential item not blocked");
                    if (!c.Context.Any(i => i.Blocked && i.Title.StartsWith("Vault credentials"))) throw new Exception("no BLOCKED vault row");
                    var sent = ws.BuildMessages(c, "review").ToJsonString();
                    foreach (var i in c.Context)
                        foreach (var x in secrets)
                            if (i.Title.Contains(x) || i.Content.Contains(x)) throw new Exception($"secret in context item “{i.Title}”");
                    foreach (var x in secrets) if (sent.Contains(x)) throw new Exception("secret in the built prompt");
                    var panelText = string.Join("\n", Menus.Descendants<TextBlock>(dock.Panel).Select(t => t.Text)
                        .Concat(Menus.Descendants<System.Windows.Documents.Run>(dock.Panel).Select(r => r.Text))
                        .Concat(Menus.Descendants<TextBox>(dock.Panel).Select(t => t.Text))
                        .Concat(Menus.Descendants<FrameworkElement>(dock.Panel).Select(e => e.ToolTip as string ?? "")));
                    foreach (var x in secrets) if (panelText.Contains(x)) throw new Exception("secret shown in the panel");
                    if (!panelText.Contains("BLOCKED")) throw new Exception("panel does not show BLOCKED");
                    ws.Clear();
                    if (c.Context.Count != 1 || !c.Context[0].Blocked || c.Messages.Count != 0) throw new Exception("clear");
                    ws.Conversations.Remove(c);
                    ws.Switch(keep);
                    Ok("AI context list: secrets masked on entry (password, private key, API key, JWT), credentials always BLOCKED and never included, nothing secret in the prompt or the panel");
                }
                catch (Exception ex) { Fail("AI context never contains secrets", ex); }
            }
            finally
            {
                ws.Chat = realChat;
                s.AiEnabled = aiOn; s.AiFeatureFiles = filesOn; s.AiFeatureHelper = helpOn; s.AiEngine = engine; s.OllamaUrl = ollama; s.OllamaTunnelHostId = tunnel;
                dock.Apply(AiDockState.Docked);
            }
        }

        // Typography: the selected UI font really reaches windows, controls, dialogs, menus and new windows; persists; falls
        // back when missing; UI and terminal fonts are independent; reset restores defaults.
        try
        {
            var s = AppSettings.Current;
            string Src(string key) => ((System.Windows.Media.FontFamily)Application.Current.Resources[key]).Source;
            var face = new[] { "Georgia", "Times New Roman", "Arial", "Verdana" }.FirstOrDefault(f => Typography.Installed.Contains(f)) ?? throw new Exception("no test face installed");
            int uiEvents = 0, termEvents = 0;
            Action onUi = () => uiEvents++, onTerm = () => termEvents++;
            Typography.Changed += onUi; Typography.TerminalChanged += onTerm;
            try
            {
                var termBefore = Typography.TerminalFamily;
                Typography.Set(uiFont: face);
                if (!Src("UiFont").StartsWith(face) || !Src("ChromeFont").StartsWith(face)) throw new Exception("resources: " + Src("UiFont"));
                if (!main.FontFamily.Source.StartsWith(face)) throw new Exception("main window font " + main.FontFamily.Source);
                // A button styled by the theme (not one that deliberately asks for MonoFont, like the quick-connect mode).
                var btn = Menus.Descendants<Button>(main).FirstOrDefault(b => DependencyPropertyHelper.GetValueSource(b, Control.FontFamilyProperty).BaseValueSource is BaseValueSource.DefaultStyle or BaseValueSource.ImplicitStyleReference or BaseValueSource.Style)
                          ?? throw new Exception("no theme-styled button in main window");
                if (!btn.FontFamily.Source.StartsWith(face)) throw new Exception("button font " + btn.FontFamily.Source);
                if (termEvents != 0 || Typography.TerminalFamily != termBefore) throw new Exception("UI font change touched the terminal");
                if (uiEvents == 0) throw new Exception("no Changed event");

                var cm = new ContextMenu { Style = (Style)Application.Current.FindResource(typeof(ContextMenu)) };
                cm.Items.Add(new MenuItem { Header = "x" });
                if (!cm.FontFamily.Source.StartsWith(face)) throw new Exception("context menu font " + cm.FontFamily.Source);
                var tip = new ToolTip { Style = (Style)Application.Current.FindResource(typeof(ToolTip)) };
                if (!tip.FontFamily.Source.StartsWith(face)) throw new Exception("tooltip font");

                // A dialog, a raw Window created in code (class handler) and a pop-out window, all off-screen and never activated.
                var dlg = Dialogs.MakeWindow("typography probe", 300);
                dlg.Content = new TextBlock { Text = "probe" };
                var raw = new Window { Content = new TextBlock { Text = "raw" }, Width = 200, Height = 100 };
                foreach (var w in new[] { dlg, raw }) { w.Owner = null; w.ShowInTaskbar = false; w.WindowStartupLocation = WindowStartupLocation.Manual; w.Left = -5000; w.Top = -5000; w.ShowActivated = false; w.Show(); }
                await Settle(150);
                // Size-to-content dialogs scroll their body (ADR-015), so the content sits inside a ScrollViewer.
                var dlgBody = dlg.Content is ScrollViewer dsv ? (TextBlock)dsv.Content : throw new Exception("dialog body not scrollable");
                if (!dlg.FontFamily.Source.StartsWith(face) || !dlgBody.FontFamily.Source.StartsWith(face)) throw new Exception("dialog font");
                if (!raw.FontFamily.Source.StartsWith(face)) throw new Exception("code-created window font " + raw.FontFamily.Source);
                Typography.Set(scale: 1.25);
                if (dlgBody.LayoutTransform is not System.Windows.Media.ScaleTransform { ScaleX: 1.25 }) throw new Exception("dialog scale");
                if (((FrameworkElement)main.Content).LayoutTransform is not System.Windows.Media.ScaleTransform { ScaleX: 1.25 }) throw new Exception("main scale");
                // Menus are built when they open (Menus.Attach), so a menu created after the change must be scaled.
                var cm2 = new ContextMenu { Style = (Style)Application.Current.FindResource(typeof(ContextMenu)) };
                if (cm2.LayoutTransform is not System.Windows.Media.ScaleTransform { ScaleX: 1.25 }) throw new Exception("context menu scale");
                // …and a menu built BEFORE the change is re-bound when it opens (the Opened class handler).
                cm.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent, cm));
                if (cm.LayoutTransform is not System.Windows.Media.ScaleTransform { ScaleX: 1.25 }) throw new Exception("reused context menu not re-bound on open");
                dlg.Close(); raw.Close();

                // Accents, editor/mono, persistence and a simulated restart (reload settings from disk, re-apply).
                Typography.Set(monoAccents: true, editorFont: face);
                if (Src("ChromeFont") != Src("MonoFont") || !Src("EditorFont").StartsWith(face)) throw new Exception("accents/editor");
                var disk = SecureStore.Read<AppSettings>("settings.dcx");
                if (disk.UiFont != face || !disk.MonoAccents || disk.EditorFont != face || Math.Abs(disk.UiScale - 1.25) > 0.001) throw new Exception("not persisted");
                if (Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(SecureStore.DataDir, "settings.dcx"))).Contains(face)) throw new Exception("plaintext font name in settings.dcx");
                // "Restart": wipe the live values and resources, then take them only from the decrypted file and re-apply.
                Application.Current.Resources["UiFont"] = new System.Windows.Media.FontFamily("Courier New");
                s.UiFont = ""; s.EditorFont = ""; s.MonoAccents = false; s.UiScale = 1;
                s.UiFont = disk.UiFont; s.EditorFont = disk.EditorFont; s.MonoAccents = disk.MonoAccents; s.UiScale = disk.UiScale;
                Typography.Apply();
                if (!Src("UiFont").StartsWith(face)) throw new Exception("not restored after reload");

                // Terminal font: own event, never the UI resources.
                uiEvents = 0;
                var uiBefore = Src("UiFont");
                Typography.SetTerminal(family: "Consolas", size: 15);
                if (termEvents != 1 || uiEvents != 0 || Src("UiFont") != uiBefore || AppSettings.Current.TerminalFontSize != 15) throw new Exception("terminal change touched UI");

                // Missing fonts fall back to the defaults (and are reported), never break.
                Typography.Set(uiFont: "DCX No Such Font 9z", monoFont: "DCX No Such Mono 9z");
                AppSettings.Current.TerminalFont = "DCX No Such Mono 9z";
                if (Src("UiFont") != Typography.DefaultUi || Src("MonoFont") != Typography.DefaultMono || Typography.TerminalFamily != "") throw new Exception("fallback: " + Src("UiFont"));
                if (Typography.MissingChoices().Count != 3) throw new Exception("missing list " + Typography.MissingChoices().Count);

                Typography.ResetDefaults();
                s = AppSettings.Current;
                if (s.UiFont != "" || s.MonoFont != "" || s.EditorFont != "" || s.TerminalFont != "" || s.MonoAccents || Math.Abs(s.UiScale - Typography.DefaultScale) > 0.001 || s.TerminalFontSize != Typography.DefaultTerminalSize)
                    throw new Exception("reset settings");
                if (Src("UiFont") != Typography.DefaultUi || Src("ChromeFont") != Typography.DefaultUi) throw new Exception("reset resources");
            }
            finally { Typography.Changed -= onUi; Typography.TerminalChanged -= onTerm; Typography.ResetDefaults(); }
            Ok($"typography: UI font ({face}) reaches main window, buttons, menus, tooltips, dialogs, code-created windows; scale on popups; accents/editor; encrypted persistence + reload; terminal independent both ways; missing-font fallback; reset");
        }
        catch (Exception ex) { Fail("typography", ex); }

        // UI font size (text only) + window sizing (ADR-015).
        try { await UiScaleTests.RunAsync(main, Path.GetDirectoryName(reportPath)!, Ok, Fail, Snap); }
        catch (Exception ex) { Fail("UI font size / window sizing", ex); }
        finally { Typography.ResetDefaults(); }

        // Network classification (one classifier for the AI gate, badges and warnings) + sandbox consistency.
        try
        {
            var cases = new (string Url, NetClass Want)[]
            {
                ("http://localhost:11434", NetClass.Local), ("http://127.0.0.1:1234/v1", NetClass.Local), ("http://[::1]:11434", NetClass.Local),
                ("http://10.0.0.5:11434", NetClass.OnPremises), ("http://192.168.1.20", NetClass.OnPremises), ("http://100.101.1.2", NetClass.OnPremises),
                ("http://ollama.lan:11434", NetClass.OnPremises), ("http://gpu-box:11434", NetClass.OnPremises), ("http://[fd00::5]:11434", NetClass.OnPremises),
                ("https://api.openai.com/v1", NetClass.External), ("https://api.anthropic.com", NetClass.External), ("https://x.openai.azure.com", NetClass.External),
                ("https://api.vultr.com/v2", NetClass.External), ("http://8.8.8.8:11434", NetClass.External), ("http://198.51.100.7", NetClass.External),
                ("https://ollama.example.org", NetClass.UserConfigured), ("not a url at all", NetClass.UserConfigured), ("", NetClass.UserConfigured),
            };
            foreach (var (u, want) in cases)
                if (NetworkPolicy.Classify(u) != want) throw new Exception($"{u}: {NetworkPolicy.Classify(u)} != {want}");
            if (Ai.AiClient.IsLocal("https://ollama.example.org")) throw new Exception("user-configured name treated as local by the AI gate");
            if (NetworkPolicy.Glyph(NetClass.External) == NetworkPolicy.Glyph(NetClass.UserConfigured)) throw new Exception("classes must differ without colour");
            // No fake NPU: a keyboard driver called "…virtual input device" was reported as an NPU before.
            if (Ai.AiHardware.IsNpu("Corsair composite virtual input device", "HIDClass") || Ai.AiHardware.IsNpu("USB Input Device", "HIDClass")) throw new Exception("input device taken for an NPU");
            if (!Ai.AiHardware.IsNpu("Intel(R) AI Boost", "System") || !Ai.AiHardware.IsNpu("AMD NPU Compute Accelerator Device", null) || !Ai.AiHardware.IsNpu("Anything", "ComputeAccelerator")) throw new Exception("real NPU missed");

            var s = AppSettings.Current;
            bool had = s.SandboxMode; int lockMin = s.AutoLockMinutes;
            s.SandboxMode = true;
            if (!SecureStore.PasswordRequiredOnStart && (!AppLock.ReconcileSandbox() || s.SandboxMode)) throw new Exception("sandbox stays on without a master password");
            s.SandboxMode = had; s.AutoLockMinutes = lockMin; AppSettings.Save();
            Ok("network classes: local / on-premises / user-configured / external (shape-coded), AI gate never treats unknown DNS names as local; sandbox flag can't claim a password that isn't there");
        }
        catch (Exception ex) { Fail("network classification / sandbox", ex); }

        // Paste safety: hidden control characters stripped, multi-line / trailing Enter / risky commands need a confirm,
        // a plain single line goes straight through, bracketed-paste mode tracked from output.
        try
        {
            var plain = PasteGuard.Check("ls -la /var/log");
            if (plain.NeedsConfirm || plain.Lines != 1) throw new Exception("plain line asked");
            var multi = PasteGuard.Check("cd /tmp\r\nls\r\n");
            if (!multi.NeedsConfirm || multi.Lines != 2 || !multi.EndsWithNewline || PasteGuard.WithoutFinalNewline(multi.Clean) != "cd /tmp\r\nls") throw new Exception("multi-line");
            var esc = PasteGuard.Check("echo hi\x1b[201~\x07rm x\x9b");
            if (esc.RemovedControls != 3 || esc.Clean.Contains('\x1b') || esc.Clean.Contains('\x9b') || !esc.NeedsConfirm) throw new Exception("control chars: " + esc.RemovedControls);
            foreach (var bad in new[] { "sudo rm -rf / ", "curl -fsSL https://example.com/i.sh | sudo bash", "dd if=/dev/zero of=/dev/sda", "systemctl stop sshd", "mkfs.ext4 /dev/vdb1", "apt upgrade -y && reboot", "sudo shutdown -h now" })
                if (PasteGuard.Check(bad).Risks.Count == 0) throw new Exception("not flagged: " + bad);
            foreach (var ok in new[] { "rm -rf ./build", "grep reboot /var/log/syslog | tail", "curl -s https://example.com -o page.html" })
                if (PasteGuard.Check(ok).Risks.Count != 0) throw new Exception("false alarm: " + ok);
            if (PasteGuard.BracketedFrom("abc\x1b[?2004hprompt$ ") != true || PasteGuard.BracketedFrom("\x1b[?2004h…\x1b[?2004l") != false || PasteGuard.BracketedFrom("plain") != null) throw new Exception("bracketed tracking");
            if (!PasteGuard.Describe(multi, false, "web-01").Contains("each line runs") || !PasteGuard.Describe(multi, true, "web-01").Contains("nothing runs until you press Enter")) throw new Exception("describe");
            Ok("paste guard: control chars stripped (incl. ESC[201~ break-out, C1 CSI), multi-line/trailing Enter/risky/huge confirm, single plain line passes, no false alarms on benign lookalikes, bracketed-paste tracked from output");
        }
        catch (Exception ex) { Fail("paste guard", ex); }

        // Privacy & network report: posture from live state, AI endpoint blocked when internet AI is off, LAN-exposed tunnel flagged.
        try
        {
            var s = AppSettings.Current;
            var (eng, url, cloud, en) = (s.AiEngine, s.OpenAiUrl, s.AiAllowCloud, s.AiEnabled);
            var tun = new TunnelDef { Name = "selftest-lan", Kind = TunnelKind.Local, BindAddress = "0.0.0.0", BindPort = 18080, DestHost = "127.0.0.1", DestPort = 80 };
            AppData.Current.Tunnels.Add(tun);
            try
            {
                s.AiEnabled = true; s.AiEngine = "openai"; s.OpenAiUrl = "https://api.openai.com/v1"; s.AiAllowCloud = false;
                var ai = PrivacyReport.Endpoints().First(e => e.Service.StartsWith("AI engine"));
                if (ai.Class != NetClass.External || !ai.State.StartsWith("BLOCKED")) throw new Exception("AI endpoint: " + ai.Class + " " + ai.State);
                s.OpenAiUrl = "http://127.0.0.1:1234/v1";
                if (PrivacyReport.Endpoints().First(e => e.Service.StartsWith("AI engine")).Class != NetClass.Local) throw new Exception("local AI endpoint");
                var post = PrivacyReport.PostureItems();
                string V(string n) => post.First(p => p.Name == n).Value;
                if (V("External AI") != "DISABLED" || V("Telemetry & analytics") != "NONE" || V("Update checks") != (Updater.Enabled ? "MANUAL (you click Check)" : "DISABLED") || V("Local API") != "OFF" || V("Broadcast (OBS)") != "none active") throw new Exception("posture");
                var port = PrivacyReport.Ports().First(p => p.Service.Contains("selftest-lan"));
                if (port.Level != PrivacyLevel.Warn || !port.Reach.Contains("ALL interfaces") || port.State != "stopped") throw new Exception("LAN tunnel: " + port.Reach);
                if (PrivacyReport.Reach("127.0.0.1").Level != PrivacyLevel.Good || PrivacyReport.Reach("192.168.1.5").Level != PrivacyLevel.Warn) throw new Exception("reach");
                if (PrivacyReport.Endpoints().Any(e => e.Service.Contains("telemetry", StringComparison.OrdinalIgnoreCase))) throw new Exception("telemetry endpoint listed");
                main.Navigate("privacy"); await Settle(500);
                var shots = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(reportPath)!, "shots")).FullName;
                Snap(main, Path.Combine(shots, "privacy.png"));
            }
            finally
            {
                AppData.Current.Tunnels.Remove(tun);
                (s.AiEngine, s.OpenAiUrl, s.AiAllowCloud, s.AiEnabled) = (eng, url, cloud, en);
                AppSettings.Save();
            }
            Ok("privacy report: posture from live state (NOT IMPLEMENTED shown as such), internet AI endpoint BLOCKED while disabled, local endpoint classed LOCAL, 0.0.0.0 tunnel flagged LAN-reachable, no telemetry endpoints; rendered privacy.png");
        }
        catch (Exception ex) { Fail("privacy report", ex); }

        // Off-screen renders of the main screens (WPF only; WebView2 content isn't captured) — demo data, no real hosts.
        try
        {
            var shots = Path.Combine(Path.GetDirectoryName(reportPath)!, "shots");
            Directory.CreateDirectory(shots);
            AppData.Current.Hosts.AddRange(new[]
            {
                new HostEntry { Name = "web-01", Group = "Production", Address = "203.0.113.10", Username = "alex", Tags = "nginx, ubuntu", Favorite = true },
                new HostEntry { Name = "db-01", Group = "Production", Address = "203.0.113.20", Username = "alex", Tags = "postgres" },
                new HostEntry { Name = "nas", Group = "LAN", Address = "192.168.1.20", Username = "sam", Auth = AuthMethod.Password },
            });
            AppData.Save();
            main.Width = 1440; main.Height = 900;
            foreach (var (key, file) in new[] { ("dashboard", "dashboard"), ("hosts", "hosts"), ("credentials", "credentials"), ("vultr:Instances", "vultr-instances"), ("vultr:Firewalls", "vultr-firewalls"), ("assistant", "assistant"), ("settings", "settings") })
            {
                main.Navigate(key);
                await Settle(500);
                Snap(main, Path.Combine(shots, file + ".png"));
            }
            main.Navigate("settings");
            SettingsView.Current?.RevealDataCard();
            await Settle(600);
            Snap(main, Path.Combine(shots, "settings-storage.png"));
            SettingsView.Current?.RevealCard("Snapshots");
            await Settle(600);
            Snap(main, Path.Combine(shots, "settings-snapshots.png"));
            SettingsView.Current?.RevealCard("Updates");
            await Settle(600);
            Snap(main, Path.Combine(shots, "settings-updates.png"));
            SettingsView.Current?.RevealCard("Theme");
            await Settle(600);
            Snap(main, Path.Combine(shots, "settings-appearance.png"));
            SettingsView.Current?.RevealCard("Sidebar");
            await Settle(600);
            Snap(main, Path.Combine(shots, "settings-navigation.png"));
            SettingsView.Current?.RevealCard("Import & export");
            await Settle(600);
            Snap(main, Path.Combine(shots, "settings-import-export.png"));
            SettingsView.Current?.RevealCard("About");
            await Settle(600);
            Snap(main, Path.Combine(shots, "settings-about.png"));
            SettingsView.Current?.RevealCard("Typography");
            await Settle(600);
            Snap(main, Path.Combine(shots, "settings-typography.png"));
            SettingsView.Current?.RevealCard("AI engine");
            await Settle(600);
            Snap(main, Path.Combine(shots, "settings-ai.png"));
            // The render shows the two folders the file tests used (a new tab normally connects to nothing, 0.5.1 #4).
            AppSettings.Current.CommanderRestoreLast = true;
            main.Navigate("commander");
            await Settle(1200);
            Snap(main, Path.Combine(shots, "commander.png"));
            AppSettings.Current.CommanderRestoreLast = false;
            main.OpenServer(testHost);
            await Settle(1500);
            Snap(main, Path.Combine(shots, "server.png"));
            ThemeManager.Apply(ThemeMode.Light);
            main.Navigate("hosts");
            await Settle(500);
            Snap(main, Path.Combine(shots, "hosts-light.png"));
            ThemeManager.Apply(ThemeMode.Dark);
            Ok("screen renders saved to " + shots);
        }
        catch (Exception ex) { Fail("screen renders", ex); }
    }
}
