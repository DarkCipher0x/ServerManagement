using System.Windows;

namespace DcxManager.Views;

/// <summary>
/// Extension points of the app (0.2.0 phase 3, docs/history/PLAN-0.2.0.md §3). A module contributes what it adds to the screens
/// through <see cref="IAppHost"/> when it is registered; the screens render the contributions and never name a module.
/// With a module not registered (AI off, later), nothing of it exists: no item to hide, no menu to grey out.
/// Phase 3 adds the points one at a time; this first slice is the sidebar.
/// </summary>
public interface IAppModule
{
    string Id { get; }
    void Register(IAppHost host);
}

/// <summary>What a module can add. Contributions are data; the window reads them when it builds.</summary>
public interface IAppHost
{
    /// <summary>A sidebar item in <paramref name="section"/> (an existing header such as "AI"). Either a screen (a
    /// singleton tab with <paramref name="header"/>) or an <paramref name="open"/> action; an optional right-click menu.</summary>
    void NavItem(string section, string key, string text, string? tip, Func<FrameworkElement>? screen = null, string? header = null, Action? open = null, Func<IEnumerable<object?>>? menu = null);

    /// <summary>A Settings category of its own, placed after an existing one (<paramref name="after"/> null = last).</summary>
    void SettingsCategory(string name, string glyph, string blurb, string? after = null);

    /// <summary>A Settings card in <paramref name="category"/> (its own or an existing one). <paramref name="scope"/> is
    /// the "applies to" text in the card header. The card is built by <paramref name="make"/> when Settings opens; the factory gets the Settings kit (ISettingsKit).</summary>
    void SettingsCard(string category, string title, string? scope, Func<ISettingsKit, System.Windows.Controls.Border> make);

    /// <summary>Items appended to a context menu. <paramref name="surface"/> names the menu ("tab": the document tab menu,
    /// context = the <c>TabItem</c>; more surfaces as the screens adopt the point); <paramref name="items"/> builds the
    /// items for that context each time the menu opens (null entries are skipped).</summary>
    void ContextMenu(string surface, Func<object?, IEnumerable<object?>> items);

    /// <summary>An extra button in the error box (<see cref="Dialogs.Error"/>): given the title, message and exception,
    /// return the action or null when it doesn't apply right now (then the box is a plain warning).</summary>
    void ErrorAction(Func<string, string, Exception?, ErrorBoxAction?> provide);

    /// <summary>A global keyboard shortcut of the main window (<paramref name="gesture"/> is the text shown in menus).</summary>
    void Shortcut(System.Windows.Input.Key key, System.Windows.Input.ModifierKeys modifiers, string gesture, Action run);

    /// <summary>The right-hand dock column of the main window: one module may fill it. <paramref name="create"/> gets the
    /// window, the column, its splitter and the host border, and returns the controller the window talks to.</summary>
    void RightDock(Func<Window, System.Windows.Controls.ColumnDefinition, System.Windows.Controls.GridSplitter, System.Windows.Controls.Border, IDockController> create);

    /// <summary>Runs once the main window has rendered (restore a detached window, hook status lines).</summary>
    void WindowReady(Action<Window> run);

    /// <summary>A background task started after the main window is shown (not in the self-test): e.g. Foundry Local's
    /// hands-off set-up.</summary>
    void Startup(Func<Task> run);

    /// <summary>Runs when the main window closes, after the documents are disposed.</summary>
    void Shutdown(Action run);

    /// <summary>A remote source of the dashboard and its tile on the summary row (after "Saved hosts"). <paramref name="host"/>
    /// is the host a check contacts, or null when there is nothing to check now (the source is then not listed);
    /// <paramref name="check"/> runs only under the dashboard's check rule or "Check now"; <paramref name="tile"/> draws the
    /// tile from the last outcome (memory only) and the "checked … / not checked — …" text.</summary>
    void DashboardSource(string id, string title, Func<string?> host, Func<Task<Services.CheckOutcome>> check,
        Func<Services.CheckOutcome?, string, Services.TileText> tile, double tileWidth = 170);

    /// <summary>Rows of the Privacy &amp; network report: posture rows (after "Data location") and destinations (first).</summary>
    void PrivacyRows(Func<IEnumerable<Services.PrivacyPosture>> posture, Func<IEnumerable<Services.PrivacyEndpoint>> endpoints);

    /// <summary>The built-in help's articles tagged <c>module: &lt;this module's id&gt;</c> exist (search, Help &amp; FAQ, the
    /// AI's grounding). Without this call — or with the module not registered — they are left out of the help.</summary>
    void HelpArticles();

    /// <summary>The app's assistant (<see cref="Services.IAssistant"/>): the screens' "✦ … with AI" actions send the
    /// material they build to it. One at most.</summary>
    void Assistant(Services.IAssistant assistant);

    /// <summary>The analysis pass of the offline reports (<see cref="Reports.IReportAnalyst"/>). One at most.</summary>
    void ReportAnalyst(Reports.IReportAnalyst analyst);
}

/// <summary>Context of the "screenshot" menu surface (every 📷 menu): the capture to run on click, its subject and owner window.</summary>
public sealed record ScreenshotMenuContext(Func<Task<System.Windows.Media.Imaging.BitmapSource>> Capture, string Subject, Window? Owner);

/// <summary>Context of the "settings-card" menu surface (a Settings card's right-click menu): the card, its hint, the
/// option under the mouse (label, short label, non-secret value, tooltip) if any, and the card's option labels (built on use).</summary>
public sealed record SettingsCardMenuContext(string Card, string Hint, string? Option, string? OptionShort, string? Value, string? Tip, Func<IEnumerable<string>> Options);

/// <summary>What the main window needs from whatever fills its right dock column.</summary>
public interface IDockController
{
    /// <summary>The window has rendered: reopen a detached window if that is how the app was left.</summary>
    void RestoreDetached();
    /// <summary>App exit: close a detached window without docking back.</summary>
    void ShutDown();
}

/// <summary>One sidebar contribution.</summary>
public sealed record NavContribution(string Module, string Section, string Key, string Text, string? Tip, Func<FrameworkElement>? Screen, string? Header, Action? Open, Func<IEnumerable<object?>>? Menu);
/// <summary>One Settings category contribution.</summary>
public sealed record SettingsCategoryContribution(string Module, string Name, string Glyph, string Blurb, string? After);
/// <summary>One Settings card contribution.</summary>
public sealed record SettingsCardContribution(string Module, string Category, string Title, string? Scope, Func<ISettingsKit, System.Windows.Controls.Border> Make);

/// <summary>The registered modules and everything they contributed. Modules are added at start-up (the composition root
/// in <c>App</c>); the main window reads the lists whenever it builds.</summary>
public static class AppModules
{
    private static readonly List<IAppModule> Modules = new();
    private static readonly List<NavContribution> Nav = new();
    private static readonly List<SettingsCategoryContribution> SettingsCategories = new();
    private static readonly List<SettingsCardContribution> SettingsCards = new();
    private static readonly List<(string Module, string Surface, Func<object?, IEnumerable<object?>> Items)> Menus = new();
    private static readonly List<(string Module, Func<string, string, Exception?, ErrorBoxAction?> Provide)> ErrorActions = new();

    public static IReadOnlyList<IAppModule> All => Modules.ToList();

    /// <summary>Whether a module with this id is registered (no allocation: the capability registry asks on every lookup).</summary>
    public static bool IsLoaded(string id)
    {
        foreach (var m in Modules) if (m.Id == id) return true;
        return false;
    }

    /// <summary>The contributed items of one context menu, in registration order (a failing builder is logged and skipped).</summary>
    public static IEnumerable<object?> MenuItems(string surface, object? context)
    {
        foreach (var (module, s, build) in Menus.ToList())
        {
            if (s != surface) continue;
            IEnumerable<object?> items;
            try { items = build(context).ToList(); }
            catch (Exception ex) { Services.Log.Write($"Menu contribution ({module}, {surface})", ex); continue; }
            foreach (var i in items) if (i != null) yield return i;
        }
    }

    /// <summary>The first error-box action that applies to this error, or null.</summary>
    public static ErrorBoxAction? ErrorActionFor(string title, string message, Exception? ex)
    {
        foreach (var (module, provide) in ErrorActions.ToList())
        {
            try { if (provide(title, message, ex) is { } a) return a; }
            catch (Exception e2) { Services.Log.Write($"Error-box contribution ({module})", e2); }
        }
        return null;
    }

    public static int MenuSurfaceCount(string surface) => Menus.Count(m => m.Surface == surface);
    public static int ErrorActionCount => ErrorActions.Count;

    private static readonly List<(string Module, System.Windows.Input.Key Key, System.Windows.Input.ModifierKeys Mods, string Gesture, Action Run)> Shortcuts = new();
    private static (string Module, Func<Window, System.Windows.Controls.ColumnDefinition, System.Windows.Controls.GridSplitter, System.Windows.Controls.Border, IDockController> Create)? _rightDock;
    private static readonly List<Action<Window>> WindowReadyHooks = new();
    private static readonly List<Func<Task>> StartupTasks = new();
    private static readonly List<Action> ShutdownHooks = new();

    /// <summary>Runs the module shortcut bound to this key and modifiers, if any.</summary>
    public static bool RunShortcut(System.Windows.Input.Key key, System.Windows.Input.ModifierKeys mods)
    {
        var s = Shortcuts.FirstOrDefault(x => x.Key == key && x.Mods == mods);
        if (s.Run == null) return false;
        s.Run();
        return true;
    }
    public static string? ShortcutGesture(System.Windows.Input.Key key, System.Windows.Input.ModifierKeys mods) => Shortcuts.FirstOrDefault(x => x.Key == key && x.Mods == mods).Gesture;

    /// <summary>Fills the main window's right dock column with the registered module's dock, or leaves it empty (null).</summary>
    public static IDockController? CreateRightDock(Window owner, System.Windows.Controls.ColumnDefinition col, System.Windows.Controls.GridSplitter splitter, System.Windows.Controls.Border host) =>
        RightDock = _rightDock is { } d ? d.Create(owner, col, splitter, host) : null;
    public static bool HasRightDock => _rightDock != null;
    /// <summary>The dock controller the main window created (the module that owns it reaches its dock here).</summary>
    public static IDockController? RightDock { get; private set; }

    public static void OnWindowReady(Window main) { foreach (var h in WindowReadyHooks.ToList()) { try { h(main); } catch (Exception ex) { Services.Log.Write("Window-ready hook", ex); } } }
    public static void StartBackgroundTasks() { foreach (var t in StartupTasks.ToList()) _ = Task.Run(async () => { try { await t(); } catch (Exception ex) { Services.Log.Write("Module start-up task", ex); } }); }
    public static void OnShutdown() { foreach (var h in ShutdownHooks.ToList()) { try { h(); } catch (Exception ex) { Services.Log.Write("Shutdown hook", ex); } } }
    public static (int WindowReady, int Startup, int Shutdown) HookCounts => (WindowReadyHooks.Count, StartupTasks.Count, ShutdownHooks.Count);
    public static IReadOnlyList<NavContribution> NavItems => Nav.ToList();
    public static IReadOnlyList<SettingsCardContribution> SettingsCardItems => SettingsCards.ToList();

    /// <summary>The Settings categories: the app's own in their order, with each contributed one inserted after the
    /// category it named (or at the end).</summary>
    public static (string Name, string Glyph, string Blurb)[] MergeSettingsCategories((string Name, string Glyph, string Blurb)[] core)
    {
        var list = core.ToList();
        foreach (var c in SettingsCategories)
        {
            if (list.Any(x => x.Name == c.Name)) continue;
            int at = c.After != null ? list.FindIndex(x => x.Name == c.After) : -1;
            list.Insert(at >= 0 ? at + 1 : list.Count, (c.Name, c.Glyph, c.Blurb));
        }
        return list.ToArray();
    }

    public static IEnumerable<SettingsCardContribution> SettingsCardsIn(string category) => SettingsCards.Where(c => c.Category == category);
    public static SettingsCardContribution? FindSettingsCard(string title) => SettingsCards.FirstOrDefault(c => c.Title == title);

    /// <summary>Registers a module once (by id) and collects its contributions.</summary>
    public static void Add(IAppModule module)
    {
        if (Modules.Any(m => m.Id == module.Id)) return;
        Modules.Add(module);
        module.Register(new Host(module.Id));
    }

    public static IEnumerable<NavContribution> NavItemsIn(string section) => Nav.Where(n => n.Section == section);
    public static NavContribution? FindNav(string key) => Nav.FirstOrDefault(n => n.Key == key);

    /// <summary>Self-test: forget every module and contribution (the test re-registers what it needs).</summary>
    internal static void ResetForTest() { Modules.Clear(); Nav.Clear(); SettingsCategories.Clear(); SettingsCards.Clear(); Menus.Clear(); ErrorActions.Clear(); Shortcuts.Clear(); _rightDock = null; WindowReadyHooks.Clear(); StartupTasks.Clear(); ShutdownHooks.Clear(); Services.DashboardSources.ResetForTest(); Services.PrivacyRows.ResetForTest(); Services.HelpKb.ResetOwnersForTest(); Services.Assist.ResetForTest(); Services.HostIdentify.ResetForTest(); Services.LocalAiSetup.ResetForTest(); Reports.ReportAnalysis.ResetForTest(); }

    private sealed class Host(string module) : IAppHost
    {
        public void ContextMenu(string surface, Func<object?, IEnumerable<object?>> items) => Menus.Add((module, surface, items));
        public void ErrorAction(Func<string, string, Exception?, ErrorBoxAction?> provide) => ErrorActions.Add((module, provide));
        public void Shortcut(System.Windows.Input.Key key, System.Windows.Input.ModifierKeys modifiers, string gesture, Action run)
        {
            if (Shortcuts.All(s => s.Key != key || s.Mods != modifiers)) Shortcuts.Add((module, key, modifiers, gesture, run));
        }
        public void RightDock(Func<Window, System.Windows.Controls.ColumnDefinition, System.Windows.Controls.GridSplitter, System.Windows.Controls.Border, IDockController> create) => _rightDock ??= (module, create);
        public void WindowReady(Action<Window> run) => WindowReadyHooks.Add(run);
        public void Startup(Func<Task> run) => StartupTasks.Add(run);
        public void Shutdown(Action run) => ShutdownHooks.Add(run);
        public void DashboardSource(string id, string title, Func<string?> host, Func<Task<Services.CheckOutcome>> check,
            Func<Services.CheckOutcome?, string, Services.TileText> tile, double tileWidth = 170) =>
            Services.DashboardSources.Add(new Services.ModuleSource(module, id, title, host, check, tile, tileWidth));
        public void PrivacyRows(Func<IEnumerable<Services.PrivacyPosture>> posture, Func<IEnumerable<Services.PrivacyEndpoint>> endpoints) =>
            Services.PrivacyRows.Add(new Services.PrivacyContribution(module, posture, endpoints));
        public void HelpArticles() => Services.HelpKb.OwnModule(module);
        public void Assistant(Services.IAssistant assistant) => Services.Assist.Register(assistant);
        public void ReportAnalyst(Reports.IReportAnalyst analyst) => Reports.ReportAnalysis.Register(analyst);

        public void SettingsCategory(string name, string glyph, string blurb, string? after = null)
        {
            if (SettingsCategories.All(c => c.Name != name)) SettingsCategories.Add(new SettingsCategoryContribution(module, name, glyph, blurb, after));
        }

        public void SettingsCard(string category, string title, string? scope, Func<ISettingsKit, System.Windows.Controls.Border> make)
        {
            if (SettingsCards.All(c => c.Title != title)) SettingsCards.Add(new SettingsCardContribution(module, category, title, scope, make));
        }

        public void NavItem(string section, string key, string text, string? tip, Func<FrameworkElement>? screen = null, string? header = null, Action? open = null, Func<IEnumerable<object?>>? menu = null)
        {
            if (screen == null && open == null) throw new ArgumentException("a sidebar item needs a screen or an open action", nameof(key));
            if (Nav.Any(n => n.Key == key)) return;
            Nav.Add(new NavContribution(module, section, key, text, tip, screen, header ?? text, open, menu));
        }
    }
}
