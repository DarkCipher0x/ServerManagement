using DcxManager.Browser;
using DcxManager.Files;

namespace DcxManager.Services;

/// <summary>
/// Every history the app keeps, in one place (0.6.37, ADR-116). A new feature that remembers what you did adds its line
/// here and asks <see cref="ActivityHistory.MayRecord"/> before it writes, so the History switch, the automatic clear,
/// Group Policy and the "Clear" buttons cover it with no more code. Not history (kept whatever History says): the audit
/// log, saved hosts and portals themselves, pins, notes, reports, snippets and the dashboard's checks.
/// </summary>
public static class HistorySources
{
    public static void Register()
    {
        ActivityHistory.Register(new("quick-connect", "Quick connect recents", "the ↓ list of the quick connect bar (pinned lines stay)",
            () => AppData.Current.RecentAdHoc.Count, () => AppData.Current.RecentAdHoc.Clear()));
        ActivityHistory.Register(new("palette", "Command palette recents", "what the command palette (Ctrl+Shift+P) lifts to the top",
            () => AppSettings.Current.PaletteRecent.Count, () => AppSettings.Current.PaletteRecent = new()));
        ActivityHistory.Register(new("last-connected", "When each host was last connected", "the hosts list, its dashboard card and \"recent hosts\"",
            () => AppData.Current.Hosts.Count(h => h.LastConnected != null), () => { foreach (var h in AppData.Current.Hosts) h.LastConnected = null; }));
        ActivityHistory.Register(new("toolbox", "Toolbox runs", "Toolbox → History (port, DNS, web, mail and WHOIS checks with their reports)",
            () => AppData.Current.ToolboxHistory.Count, () => AppData.Current.ToolboxHistory.Clear()));
        ActivityHistory.Register(new("transfers", "Finished transfers", "the file commander's Transfer history and the dashboard's transfers",
            () => TransferHistory.Count, TransferHistory.Clear));
        ActivityHistory.Register(new("portals-opened", "When each portal was last opened", "Portals (the \"last opened\" column)",
            () => Portals.All.Count(b => b.LastOpened != null), () => { foreach (var b in Portals.All) b.LastOpened = null; }));
        // The browser's own page history can't be counted: -1 while it is kept, 0 while history is off (each run's pages
        // are cleared the first time a profile starts, PortalHistory.OnStartedAsync).
        ActivityHistory.Register(new("portal-pages", "Pages visited in portal tabs", "the portal browser's page and download history (sign-ins stay)",
            () => ActivityHistory.Saving ? -1 : 0, () => _ = Views.PortalHistory.ClearAllAsync()));
    }
}
