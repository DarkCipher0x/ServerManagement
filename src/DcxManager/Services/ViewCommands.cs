using DcxManager.Services.Keys;

namespace DcxManager.Services;

/// <summary>
/// The keys of individual screens (0.6.36, ADR-115), listed in Settings → Keyboard with the app's own. Each screen asks
/// <see cref="KeyCommands.Matches"/> with one of these ids instead of naming a key. The file commander's keys follow
/// Midnight Commander and the editor's Vim mode follows Vim, so those stay as they are (see Help).
/// </summary>
public static class ViewCommands
{
    public const string PortalReload = "portal.reload", PortalAddress = "portal.address", PortalBack = "portal.back", PortalForward = "portal.forward";
    public const string NotesSaveVersion = "notes.save-version", NotesTimestamp = "notes.timestamp";
    public const string EditorSave = "editor.save", EditorFind = "editor.find", EditorReplace = "editor.replace", EditorGoTo = "editor.go-to",
        EditorFindNext = "editor.find-next", EditorFindPrevious = "editor.find-previous", EditorVim = "editor.vim";
    public const string PageFind = "page.find", PageRefresh = "page.refresh";

    public static void Register()
    {
        const string Portal = "Portal browser", Notes = "Notes", Editors = "Text and hex editors", OnAPage = "On a page";
        KeyCommands.RegisterView(PortalReload, "Reload the page (or stop loading)", Portal, KeyScopes.Portal, "F5");
        KeyCommands.RegisterView(PortalAddress, "Go to the address bar", Portal, KeyScopes.Portal, "Ctrl+L");
        KeyCommands.RegisterView(PortalBack, "Back", Portal, KeyScopes.Portal, "Alt+Left");
        KeyCommands.RegisterView(PortalForward, "Forward", Portal, KeyScopes.Portal, "Alt+Right");

        KeyCommands.RegisterView(NotesSaveVersion, "Save a version of the note", Notes, KeyScopes.Notes, "Ctrl+S");
        KeyCommands.RegisterView(NotesTimestamp, "Insert the time", Notes, KeyScopes.Notes, "Ctrl+Shift+T");

        KeyCommands.RegisterView(EditorSave, "Save", Editors, KeyScopes.Editor, "Ctrl+S");
        KeyCommands.RegisterView(EditorFind, "Find", Editors, KeyScopes.Editor, "Ctrl+F");
        KeyCommands.RegisterView(EditorReplace, "Find and replace (text editor)", Editors, KeyScopes.Editor, "Ctrl+H");
        KeyCommands.RegisterView(EditorGoTo, "Go to a line (text) or an offset (hex)", Editors, KeyScopes.Editor, "Ctrl+G");
        KeyCommands.RegisterView(EditorFindNext, "Find the next match", Editors, KeyScopes.Editor, "F3");
        KeyCommands.RegisterView(EditorFindPrevious, "Find the previous match (text editor)", Editors, KeyScopes.Editor, "Shift+F3");
        KeyCommands.RegisterView(EditorVim, "Vim mode on or off (text editor)", Editors, KeyScopes.Editor, "Ctrl+Alt+V");

        KeyCommands.RegisterView(PageFind, "Search this page (Settings, cloud consoles)", OnAPage, KeyScopes.Page, "Ctrl+F");
        KeyCommands.RegisterView(PageRefresh, "Refresh (file browser)", OnAPage, KeyScopes.Page, "F5");
    }
}
