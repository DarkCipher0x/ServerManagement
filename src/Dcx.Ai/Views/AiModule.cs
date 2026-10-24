using DcxManager.Services;

namespace DcxManager.Views;

/// <summary>
/// The AI as a module (0.2.0 phase 3, first slice): what it adds to the sidebar. More of its surface (settings cards,
/// menus, the dock region, shortcuts, start-up) moves here in the following slices; in phase 5 the module becomes the
/// switch — not registered means no AI anywhere.
/// </summary>
public sealed class AiModule : IAppModule
{
    public string Id => "ai";

    /// <summary>The AI Engineer sidebar: the AI module's own right dock (see <see cref="IAppHost.RightDock"/>).</summary>
    public static AiDock Dock => (AiDock)AppModules.RightDock!;

    public void Register(IAppHost host)
    {
        // Not a tab: after the open action the main window gives the sidebar highlight back to the open screen.
        host.NavItem("AI", "aipanel", "✦  AI Engineer sidebar", "Docked AI conversation with an explicit context list; detach it to its own window (Ctrl+Shift+A)",
            open: () => Dock.Reveal(),
            menu: () =>
            {
                var dock = Dock;
                return new object?[]
                {
                    Menus.Item("Show docked", () => dock.Apply(AiDockState.Docked), enabled: dock.State != AiDockState.Docked),
                    dock.State == AiDockState.Detached
                        ? Menus.Item("Reattach to the main window", () => dock.Apply(AiDockState.Docked))
                        : Menus.Item("Detach into a window", () => dock.Apply(AiDockState.Detached)),
                    Menus.Item("Collapse", () => dock.Apply(AiDockState.Collapsed), enabled: dock.State != AiDockState.Collapsed),
                    Menus.Item("Hide", () => dock.Apply(AiDockState.Hidden), "Ctrl+Shift+A", dock.State != AiDockState.Hidden),
                };
            });
        host.NavItem("AI", "assistant", "✦  AI operator", "Chat with the local AI; it can look things up and asks before any change",
            screen: () => new AssistantView(), header: "✦ AI operator");

        // Settings: the AI's own category and its three cards (bodies still in SettingsView until phase 4).
        host.SettingsCategory("AI & models", "✦", "Where models run, which features may use AI, and what the AI may see and do (terminals, notes, approvals).", after: "Cloud providers");
        host.SettingsCard("AI & models", "AI engine", "whole app", AiSettingsCards.Engine);
        host.SettingsCard("AI & models", "AI speed, agents & memory", null, AiSettingsCards.Speed);
        host.SettingsCard("AI & models", "AI permissions & safety", "whole app · approvals are per action", AiSettingsCards.Permissions);

        // Context menus: the document tab menu's "Ask AI about this screen…" (the views' own ✦ items follow in phase 4).
        host.ContextMenu("tab", ctx => ctx is TabMenuContext t
            ? new object?[] { Menus.AsyncItem("✦ Ask AI about this screen…", () => Ai.AiActions.AskAboutScreenAsync(t.ScreenName, t.Tab.Content as System.Windows.FrameworkElement), enabled: Ai.AiClient.Blocked(Ai.AiFeature.Helper) == null) }
            : Array.Empty<object?>());

        // The assistant behind every screen's "✦ … with AI" action (the screens build the material, the AI sends it).
        host.Assistant(new Ai.AiAssistant());
        // The AI pass of the offline reports (structured findings) and the report's "Open in Claude Code".
        host.ReportAnalyst(new Ai.ReportAnalyst());
        // The host import's "✦ Identify with AI" (0.5.1 #18): names, platforms and folders from scan banners.
        Services.HostIdentify.Register(new Ai.HostImportAi());
        // Quick Start's one-click Foundry Local set-up (0.5.1 #14).
        Services.LocalAiSetup.Register(async () => await Ai.FoundryManager.EnsureReadyAsync(byUser: true)
            ? "✓ Foundry Local is ready with " + Services.AppSettings.Current.FoundryModel + "." : Ai.FoundryManager.LastStatus);
        // Screenshot menus (📷): "Analyse with AI (vision model)…".
        host.ContextMenu("screenshot", ctx => ctx is ScreenshotMenuContext s
            ? new object?[] { Menus.AsyncItem("✦ Analyse with AI (vision model)…", async () => { var img = await s.Capture(); await ScreenshotAi.AnalyseAsync(img, s.Subject, s.Owner); }, enabled: Ai.AiActions.Why(Ai.AiFeature.Files) == null) }
            : Array.Empty<object?>());
        // A Settings card's right-click menu: explain the option under the mouse, or the whole card.
        host.ContextMenu("settings-card", ctx =>
        {
            if (ctx is not SettingsCardMenuContext c) return Array.Empty<object?>();
            bool ai = Ai.AiClient.Blocked(Ai.AiFeature.Files) == null;
            return new object?[]
            {
                c.Option is { } o ? Menus.AsyncItem($"✦ Explain this option with AI — {c.OptionShort}", () => Ai.AiActions.ExplainOptionAsync(c.Card, c.Hint, o, c.Value, c.Tip), enabled: ai) : null,
                Menus.AsyncItem($"✦ Explain the “{c.Card}” settings with AI", () => Ai.AiActions.ExplainOptionAsync(c.Card, c.Hint, "(the whole card) " + string.Join(" · ", c.Options()), null, null), enabled: ai),
            };
        });

        // The error box: "Explain this error with AI" when an engine may be used. Nothing is sent unless it is clicked;
        // the answer appears in the AI sidebar; internet providers ask first.
        host.ErrorAction((title, message, ex) => Ai.AiClient.Blocked(Ai.AiFeature.Files) == null
            ? new ErrorBoxAction("✦ Explain this error with AI", "Sends this message (secrets masked, no other app data) to your AI engine. Internet providers ask first.", () => Ai.AiActions.ExplainErrorAsync(title, message, ex))
            : null);

        // The right dock column is the AI Engineer sidebar; Ctrl+Shift+A toggles it.
        host.RightDock((owner, col, splitter, dockHost) => new AiDock(owner, col, splitter, dockHost));
        host.Shortcut(System.Windows.Input.Key.A, System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift, "Ctrl+Shift+A", () => Dock.Toggle());

        // The dashboard's "AI engine" source and tile, and the AI's rows in Privacy & network.
        host.DashboardSource("ai", "AI engine", Ai.AiOverview.DashboardHost, Ai.AiOverview.CheckAsync, Ai.AiOverview.Tile);
        host.PrivacyRows(Ai.AiOverview.PrivacyPosture, Ai.AiOverview.PrivacyEndpoints);
        // The help articles tagged "module: ai" (AI engines, Foundry Local, the AI Engineer, agents, memory guard, …).
        host.HelpArticles();

        // Window ready: Foundry Local's hands-off set-up reports each step in the status line (ADR-026; background thread).
        host.WindowReady(_ => Ai.FoundryManager.Progress += t => Ui.Thread.Post(() => Ui.Window.Status("Foundry Local: " + t)));
        // Start-up (never in the self-test): the default engine (ADR-023), Foundry Local set-up (ADR-026), the memory guard (ADR-034).
        host.Startup(async () =>
        {
            Ai.AiResourceGuard.Start();
            Ai.NpuSetupOffer.Dialog = NpuSetupDialog.Ask;
            await Ai.AiDefaults.ApplyAsync();                       // first run: Foundry Local + phi-4-mini, NPU first
            if (!await Ai.NpuSetupOffer.RunAsync())                 // an NPU that isn't used: ask (or the remembered answer)
                await Ai.FoundryManager.AutoStartAsync();
        });
    }
}
