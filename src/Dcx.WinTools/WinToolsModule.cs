using DcxManager.Services;
using DcxManager.Views;
using DcxManager.WinTools.Views;

namespace DcxManager.WinTools;

/// <summary>
/// Windows management as a module (0.6.39, ADR-119). Everything it adds goes through <see cref="IAppHost"/>: the
/// sidebar's WINDOWS section, its command and shortcut, and the help. With it off (Settings → Windows management, or
/// Group Policy <c>WinToolsFeatures</c>) the module isn't registered and its assembly isn't loaded. The switch and the
/// rules cards live in the app, so they can be seen and set while it is off.
/// </summary>
public sealed class WinToolsModule : IAppModule
{
    public string Id => "wintools";

    public const string HomeKey = "wintools";

    public static void Open() => ModuleTabs.Open(HomeKey, "🪟 Windows management", () => new WinToolsView());

    public void Register(IAppHost host)
    {
        host.NavItem("WINDOWS", HomeKey, "🪟  Windows management",
            "An MMC-style console: services, processes, event logs, storage, users and groups, shares, scheduled tasks, network and a WMI console (wmic commands too) — this PC or another Windows computer (Ctrl+Alt+W)",
            screen: () => new WinToolsView(), header: "🪟 Windows management");
        host.Command("wintools.open", "Open Windows management", "Windows management", Open, "Ctrl+Alt+W");
        host.HelpArticles();
    }
}
