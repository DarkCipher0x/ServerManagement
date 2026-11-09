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

    /// <summary>A sidebar section of the module's own (ADR-121, 0.6.43): <paramref name="key"/> is its upper-case title
    /// ("EMAIL"), <paramref name="glyph"/> the character in front of it ("✉"), <paramref name="after"/> the section it
    /// follows by default (null = just before LOGS). Put items in it with <see cref="NavItem"/>; it shows only while it has
    /// items, and the user orders, hides, expands and pins it like any built-in section. A key that clashes with a built-in
    /// section, is already contributed or isn't 1–24 of A–Z, 0–9, space, &amp; or - is ignored (logged). Added after
    /// module API 1 without changing it: an add-on that must also run on older apps catches <see cref="MissingMethodException"/>.</summary>
    void NavSection(string key, string glyph, string? after = null);

    /// <summary>Draws <paramref name="glyph"/> (as used in the module's sidebar items, sections and Settings categories) as
    /// the Segoe Fluent Icons character <paramref name="codePoint"/> (U+E000–U+F8FF), like the app's own glyphs (ADR-121,
    /// 0.6.43). A glyph the app or another module maps already is left as it is (logged). Added without changing module
    /// API 1: on older apps catch <see cref="MissingMethodException"/>.</summary>
    void Icon(string glyph, char codePoint);

    /// <summary>A live list of sidebar items in <paramref name="section"/> (ADR-121a, 0.6.43): <paramref name="items"/> is
    /// called on the UI thread every time the sidebar is built, so it must be quick (no I/O). Each entry behaves like a
    /// <see cref="NavItem"/> (pin, hide, order, its right-click menu, one tab per key) and follows the section's NavItem
    /// items, in the order returned. An entry without a screen or open action, or whose key the app or another item uses,
    /// is skipped; a provider that throws is logged and skipped. Call <see cref="RefreshNav"/> when the list changes.</summary>
    void NavItems(string section, Func<IEnumerable<ModuleNavEntry>> items);

    /// <summary>Asks the main window to rebuild the sidebar (from any thread; several calls in a row rebuild once; nothing
    /// happens before the window exists). ADR-121a, 0.6.43.</summary>
    void RefreshNav();

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

    /// <summary>A command of the main window with its default keys (0.6.36, ADR-115): it is listed in Settings → Keyboard,
    /// where its keys can be changed, and in the command palette. <paramref name="id"/> is lower-case words joined by dots
    /// ("ai.toggle"); <paramref name="defaults"/> are key texts such as "Ctrl+Shift+A".</summary>
    void Command(string id, string title, string category, Action run, params string[] defaults);

    /// <summary>The right-hand dock column of the main window: one module may fill it. <paramref name="create"/> gets the
    /// window, the column, its splitter and the host border, and returns the controller the window talks to.</summary>
    /// <summary>A side panel (ADR-117): shown in the main window's panel column (between the documents and the right dock)
    /// with <see cref="ModulePanels.Show"/>. <paramref name="make"/> builds it each time it is shown; content that is
    /// <see cref="IDisposable"/> is disposed when the panel closes.</summary>
    void SidePanel(string id, string title, Func<FrameworkElement> make);

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

    /// <summary>Where the screens' "💬 Send to a chat…" items go (<see cref="Services.ISendTarget"/>, ADR-117). One at most.</summary>
    void SendTarget(Services.ISendTarget target);
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
public sealed record NavContribution(string Module, string Section, string Key, string Text, string? Tip, Func<FrameworkElement>? Screen, string? Header, Action? Open, Func<IEnumerable<object?>>? Menu)
{
    /// <summary>ADR-121a: a live item's count (0 = none).</summary>
    public int Badge { get; init; }
    /// <summary>ADR-121a: the item came from a module's live list (IAppHost.NavItems).</summary>
    public bool Live { get; init; }
}
/// <summary>One side panel contribution.</summary>
/// <summary>One entry of a module's live sidebar list (IAppHost.NavItems, ADR-121a): the same fields as a NavItem, plus
/// <paramref name="Badge"/>, a count drawn at the right of the item (0 = none; above 99 shows 99+).</summary>
public sealed record ModuleNavEntry(string Key, string Text, string? Tip, Func<FrameworkElement>? Screen = null, string? Header = null,
    Action? Open = null, Func<IEnumerable<object?>>? Menu = null, int Badge = 0);

/// <summary>A sidebar section a module added (ADR-121): its key (the header), glyph and the section it follows by default.</summary>
public sealed record NavSectionContribution(string Module, string Key, string Glyph, string? After);

public sealed record SidePanelContribution(string Module, string Id, string Title, Func<FrameworkElement> Make);
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
    private static readonly List<NavSectionContribution> NavSectionsAdded = new();
    private static readonly List<(string Module, string Section, Func<IEnumerable<ModuleNavEntry>> Items)> LiveNav = new();
    private static List<NavContribution> _liveSnapshot = new();

    /// <summary>ADR-121a: raised by IAppHost.RefreshNav (any thread); the main window rebuilds its sidebar.</summary>
    public static event Action? NavRefreshRequested;
    private static readonly List<SettingsCategoryContribution> SettingsCategories = new();
    private static readonly List<SettingsCardContribution> SettingsCards = new();
    private static readonly List<SidePanelContribution> Panels = new();
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

    /// <summary>How many items modules added to a menu surface; <paramref name="module"/> counts only the modules it accepts.</summary>
    public static int MenuSurfaceCount(string surface, Func<string, bool>? module = null) => Menus.Count(m => m.Surface == surface && (module == null || module(m.Module)));
    public static int ErrorActionCount => ErrorActions.Count;

    private static (string Module, Func<Window, System.Windows.Controls.ColumnDefinition, System.Windows.Controls.GridSplitter, System.Windows.Controls.Border, IDockController> Create)? _rightDock;
    private static readonly List<(string Module, Action<Window> Run)> WindowReadyHooks = new();
    private static readonly List<(string Module, Func<Task> Run)> StartupTasks = new();
    private static readonly List<(string Module, Action Run)> ShutdownHooks = new();

    /// <summary>Fills the main window's right dock column with the registered module's dock, or leaves it empty (null).</summary>
    public static IDockController? CreateRightDock(Window owner, System.Windows.Controls.ColumnDefinition col, System.Windows.Controls.GridSplitter splitter, System.Windows.Controls.Border host) =>
        RightDock = _rightDock is { } d ? d.Create(owner, col, splitter, host) : null;
    public static bool HasRightDock => _rightDock != null;
    /// <summary>The dock controller the main window created (the module that owns it reaches its dock here).</summary>
    public static IDockController? RightDock { get; private set; }

    public static void OnWindowReady(Window main) { foreach (var (_, h) in WindowReadyHooks.ToList()) { try { h(main); } catch (Exception ex) { Services.Log.Write("Window-ready hook", ex); } } }
    public static void StartBackgroundTasks() { foreach (var (_, t) in StartupTasks.ToList()) _ = Task.Run(async () => { try { await t(); } catch (Exception ex) { Services.Log.Write("Module start-up task", ex); } }); }
    public static void OnShutdown() { foreach (var (_, h) in ShutdownHooks.ToList()) { try { h(); } catch (Exception ex) { Services.Log.Write("Shutdown hook", ex); } } }
    public static (int WindowReady, int Startup, int Shutdown) HookCounts => (WindowReadyHooks.Count, StartupTasks.Count, ShutdownHooks.Count);
    /// <summary>The hooks of the modules <paramref name="module"/> accepts (the self-test counts the app's own apart from
    /// the module add-ons', ADR-120).</summary>
    public static (int WindowReady, int Startup, int Shutdown) HookCountsOf(Func<string, bool> module) =>
        (WindowReadyHooks.Count(h => module(h.Module)), StartupTasks.Count(h => module(h.Module)), ShutdownHooks.Count(h => module(h.Module)));
    public static IReadOnlyList<NavContribution> NavItems => Nav.ToList();
    public static IReadOnlyList<SettingsCardContribution> SettingsCardItems => SettingsCards.ToList();
    public static IReadOnlyList<SettingsCategoryContribution> SettingsCategoryItems => SettingsCategories.ToList();

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

    /// <summary>0.6.45: the live entries as last asked (RefreshLiveNav).</summary>
    public static IReadOnlyList<NavContribution> LiveNavSnapshot => _liveSnapshot;

    public static IEnumerable<NavContribution> NavItemsIn(string section) => Nav.Where(n => n.Section == section).Concat(_liveSnapshot.Where(n => n.Section == section));

    /// <summary>ADR-121a: asks every live list for its entries (the main window calls it as it builds the sidebar; UI
    /// thread). <paramref name="taken"/> says whether a key is used already (the app's own pages); a key used twice, an
    /// entry with nothing to open and a provider that throws are skipped (the throw logged).</summary>
    public static IReadOnlyList<NavContribution> RefreshLiveNav(Func<string, bool>? taken = null)
    {
        var list = new List<NavContribution>();
        var keys = Nav.Select(n => n.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var (module, section, items) in LiveNav.ToList())
        {
            List<ModuleNavEntry> got;
            try { got = (items() ?? Enumerable.Empty<ModuleNavEntry>()).Where(e => e != null).Take(200).ToList(); }
            catch (Exception ex) { Services.Log.Write($"Live sidebar items of module {module} ({section})", ex); continue; }
            foreach (var e in got)
            {
                if (string.IsNullOrWhiteSpace(e.Key) || string.IsNullOrWhiteSpace(e.Text) || (e.Screen == null && e.Open == null)) continue;
                if (!keys.Add(e.Key) || taken?.Invoke(e.Key) == true) continue;
                list.Add(new NavContribution(module, section, e.Key, e.Text, e.Tip, e.Screen, e.Header ?? e.Text, e.Open, e.Menu) { Badge = Math.Max(0, e.Badge), Live = true });
            }
        }
        _liveSnapshot = list;
        return list;
    }

    /// <summary>The app's own sidebar sections in their default order (the main window draws them; ADR-028, 0.6.16). A
    /// module's own section (<see cref="IAppHost.NavSection"/>) can't take one of these names, nor PINNED.</summary>
    public static readonly IReadOnlyList<string> BuiltInNavSections = new[] { "OVERVIEW", "SERVERS", "TOOLS", "DOCUMENTS", "AUTOMATION", "LOCAL", "WINDOWS", "VAULT", "TEAM", "CHAT", "STREAM", "AI", "LOGS", "SYSTEM" };

    /// <summary>The sidebar sections modules added (ADR-121), in registration order.</summary>
    public static IReadOnlyList<NavSectionContribution> NavSectionItems => NavSectionsAdded.ToList();
    public static NavSectionContribution? FindNavSection(string key) => NavSectionsAdded.FirstOrDefault(n => n.Key == key);

    /// <summary>Why a module's section key is refused, or null when it is fine (ADR-121).</summary>
    public static string? NavSectionProblem(string key, string glyph)
    {
        if (string.IsNullOrEmpty(key) || key.Length > 24 || !char.IsAsciiLetterUpper(key[0]) || key.Trim() != key
            || key.Any(c => !(char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c is ' ' or '&' or '-'))) return "the key must be 1–24 of A–Z, 0–9, space, & or -, starting with a letter";
        if (BuiltInNavSections.Contains(key) || key == "PINNED") return "the app has a section of that name";
        if (NavSectionsAdded.Any(n => n.Key == key)) return "another module added that section already";
        if (string.IsNullOrWhiteSpace(glyph) || glyph.Length > 4 || glyph.Any(char.IsControl)) return "the glyph must be one character";
        return null;
    }

    /// <summary>The default section order: <paramref name="builtIn"/> (the app's sections that exist now) with each
    /// module's section that has items inserted after the one it named, or just before LOGS (or at the end) when that one
    /// isn't there. Sections that follow another module's section are placed once that one is.</summary>
    public static string[] MergeNavSections(IReadOnlyList<string> builtIn)
    {
        var list = builtIn.ToList();
        var pending = NavSectionsAdded.Where(c => NavItemsIn(c.Key).Any()).ToList();
        void Place(NavSectionContribution c, int at)
        {
            if (at < 0) { at = list.IndexOf("LOGS"); if (at < 0) at = list.Count; } else at++;
            list.Insert(at, c.Key);
            pending.Remove(c);
        }
        // A section after another module's section waits until that one is placed; whatever can't be (a missing or
        // circular "after") goes just before LOGS.
        for (bool placed = true; placed && pending.Count > 0;)
        {
            placed = false;
            foreach (var c in pending.ToList())
                if (c.After == null || list.Contains(c.After) || pending.All(o => o.Key != c.After)) { Place(c, c.After == null ? -1 : list.IndexOf(c.After)); placed = true; }
        }
        foreach (var c in pending.ToList()) Place(c, -1);
        return list.ToArray();
    }
    public static IReadOnlyList<SidePanelContribution> SidePanels => Panels.ToList();
    public static SidePanelContribution? FindSidePanel(string id) => Panels.FirstOrDefault(p => p.Id == id);
    public static NavContribution? FindNav(string key) => Nav.FirstOrDefault(n => n.Key == key) ?? _liveSnapshot.FirstOrDefault(n => n.Key == key);

    /// <summary>Self-test: forget every module and contribution (the test re-registers what it needs).</summary>
    internal static void ResetForTest() { Modules.Clear(); Nav.Clear(); NavSectionsAdded.Clear(); LiveNav.Clear(); _liveSnapshot = new(); Panels.Clear(); SettingsCategories.Clear(); SettingsCards.Clear(); Menus.Clear(); ErrorActions.Clear(); Services.Keys.KeyCommands.ResetModulesForTest(); _rightDock = null; WindowReadyHooks.Clear(); StartupTasks.Clear(); ShutdownHooks.Clear(); Services.DashboardSources.ResetForTest(); Services.PrivacyRows.ResetForTest(); Services.HelpKb.ResetOwnersForTest(); Services.Assist.ResetForTest(); Services.HostIdentify.ResetForTest(); Services.LocalAiSetup.ResetForTest(); Reports.ReportAnalysis.ResetForTest(); Services.SendTo.ResetForTest(); }

    /// <summary>Self-test: forget one module and its sidebar sections and items (ADR-121's throwaway module).</summary>
    internal static void RemoveModuleForTest(string id)
    {
        Modules.RemoveAll(m => m.Id == id);
        Nav.RemoveAll(n => n.Module == id);
        NavSectionsAdded.RemoveAll(n => n.Module == id);
        LiveNav.RemoveAll(n => n.Module == id);
        _liveSnapshot = _liveSnapshot.Where(n => n.Module != id).ToList();
    }

    private sealed class Host(string module) : IAppHost
    {
        public void ContextMenu(string surface, Func<object?, IEnumerable<object?>> items) => Menus.Add((module, surface, items));
        public void ErrorAction(Func<string, string, Exception?, ErrorBoxAction?> provide) => ErrorActions.Add((module, provide));
        public void Command(string id, string title, string category, Action run, params string[] defaults)
        {
            Services.Keys.KeyCommands.Register(new Services.Keys.KeyCommand(id, title, category, Services.Keys.KeyScopes.App, Services.Keys.KeyCommands.Keys(defaults)) { Run = run, Module = module });
        }
        public void RightDock(Func<Window, System.Windows.Controls.ColumnDefinition, System.Windows.Controls.GridSplitter, System.Windows.Controls.Border, IDockController> create) => _rightDock ??= (module, create);
        public void WindowReady(Action<Window> run) => WindowReadyHooks.Add((module, run));
        public void SidePanel(string id, string title, Func<FrameworkElement> make)
        {
            if (Panels.All(p => p.Id != id)) Panels.Add(new SidePanelContribution(module, id, title, make));
        }
        public void Startup(Func<Task> run) => StartupTasks.Add((module, run));
        public void Shutdown(Action run) => ShutdownHooks.Add((module, run));
        public void DashboardSource(string id, string title, Func<string?> host, Func<Task<Services.CheckOutcome>> check,
            Func<Services.CheckOutcome?, string, Services.TileText> tile, double tileWidth = 170) =>
            Services.DashboardSources.Add(new Services.ModuleSource(module, id, title, host, check, tile, tileWidth));
        public void PrivacyRows(Func<IEnumerable<Services.PrivacyPosture>> posture, Func<IEnumerable<Services.PrivacyEndpoint>> endpoints) =>
            Services.PrivacyRows.Add(new Services.PrivacyContribution(module, posture, endpoints));
        public void HelpArticles() => Services.HelpKb.OwnModule(module);
        public void Assistant(Services.IAssistant assistant) => Services.Assist.Register(assistant);
        public void ReportAnalyst(Reports.IReportAnalyst analyst) => Reports.ReportAnalysis.Register(analyst);
        public void SendTarget(Services.ISendTarget target) => Services.SendTo.Register(target);

        public void SettingsCategory(string name, string glyph, string blurb, string? after = null)
        {
            if (SettingsCategories.All(c => c.Name != name)) SettingsCategories.Add(new SettingsCategoryContribution(module, name, glyph, blurb, after));
        }

        public void SettingsCard(string category, string title, string? scope, Func<ISettingsKit, System.Windows.Controls.Border> make)
        {
            if (SettingsCards.All(c => c.Title != title)) SettingsCards.Add(new SettingsCardContribution(module, category, title, scope, make));
        }

        public void NavItems(string section, Func<IEnumerable<ModuleNavEntry>> items)
        {
            ArgumentNullException.ThrowIfNull(items);
            LiveNav.Add((module, section, items));
        }

        public void RefreshNav() => NavRefreshRequested?.Invoke();

        public void Icon(string glyph, char codePoint)
        {
            if (!Services.Icons.AddForModule(glyph, codePoint)) Services.Log.Write($"Icon for \"{glyph}\" from module {module} ignored: already mapped, or not a Fluent icon code point", null);
        }

        public void NavSection(string key, string glyph, string? after = null)
        {
            if (NavSectionProblem(key, glyph) is { } why) { Services.Log.Write($"Sidebar section \"{key}\" from module {module} ignored: {why}", null); return; }
            NavSectionsAdded.Add(new NavSectionContribution(module, key, glyph, string.IsNullOrWhiteSpace(after) ? null : after));
        }

        public void NavItem(string section, string key, string text, string? tip, Func<FrameworkElement>? screen = null, string? header = null, Action? open = null, Func<IEnumerable<object?>>? menu = null)
        {
            if (screen == null && open == null) throw new ArgumentException("a sidebar item needs a screen or an open action", nameof(key));
            if (Nav.Any(n => n.Key == key)) return;
            Nav.Add(new NavContribution(module, section, key, text, tip, screen, header ?? text, open, menu));
        }
    }
}
