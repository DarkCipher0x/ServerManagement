using System.Windows;

namespace DcxManager.Views;

/// <summary>
/// Document tabs a module opens in the main window (ADR-117: a chat room as a tab, beside terminals and everything else).
/// The main window sets <see cref="Open"/>; a module never references the window itself.
/// </summary>
public static class ModuleTabs
{
    /// <summary>Live session sharing (0.6.39, ADR-117): moves the tab into a window of its own titled
    /// <c>title</c> (so a call can capture that window and nothing else) and returns what puts the title back, or null
    /// when it can't. The main window sets it.</summary>
    public static Func<object, string, IDisposable?> Isolate { get; set; } = (_, _) => null;

    /// <summary>Opens the tab keyed <c>key</c> or brings it forward when it's already open; <c>make</c> builds the content
    /// only when it isn't. Keys are the module's own ("chat:…"). Returns the tab, or null when there is no window (tests).</summary>
    public static Func<string, string, Func<FrameworkElement>, object?> Open { get; set; } = (_, _, _) => null;
}

/// <summary>
/// Side panels (ADR-117): a module's panel pinned beside the documents (the chat's room panel), in the main window's
/// panel column between the documents and the AI dock. One panel shows at a time; a module contributes its panels with
/// <see cref="IAppHost.SidePanel"/> and shows them here. The main window sets the functions.
/// </summary>
public static class ModulePanels
{
    /// <summary>Shows the panel with this id (built once, kept while shown) and focuses it.</summary>
    public static Action<string> Show { get; set; } = _ => { };
    /// <summary>Takes the panel column down (the panel's content is disposed).</summary>
    public static Action Hide { get; set; } = () => { };
    /// <summary>The id of the panel shown now, or null.</summary>
    public static Func<string?> Shown { get; set; } = () => null;

    public static void Toggle(string id) { if (Shown() == id) Hide(); else Show(id); }
}
