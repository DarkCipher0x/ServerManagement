using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using DcxManager.Services;
using DcxManager.Ssh;
using DcxManager.Views;
using DcxManager.Vultr;

namespace DcxManager;

// MainWindow: navigating by screen key, singletons, the cloud workspace, help (moved unchanged from MainWindow.xaml.cs in 0.2.0 phase 6½).
public partial class MainWindow
{
    public void Navigate(string key)
    {
        // 0.5.1 (ADR-067): a locked category asks first (admin approval, Windows sign-in or its password); nothing opens otherwise.
        if (Locks.CategoryOfScreen(key) is { } cat && !Locks.IsOpen("cat:" + cat))
        {
            ReleaseNavHighlight();
            _ = UnlockThenNavigateAsync(key, cat);
            return;
        }
        if (_navByKey.TryGetValue(key, out var rb) && rb.IsChecked != true) { rb.IsChecked = true; return; }
        switch (key)
        {
            case "dashboard": OpenSingleton(key, "◈ Dashboard", () => new DashboardView(), closable: false); break;
            case "hosts": OpenSingleton(key, "▣ Hosts", HostsPageForTab); break;
            case "commander": OpenSingleton(key, "⇆ File commander", () => new CommanderView()); break;
            case "notes": OpenSingleton(key, "✎ Notes", () => new NotesView()); break;
            case "docs": OpenSingleton(key, "📄 Docs", () => new DocsView()); break;
            case "sessionlogs": OpenSingleton(key, "☷ Session logs", () => new SessionLogsView()); break;
            case "incidents": OpenSingleton(key, "⚑ Incidents", () => new IncidentsView()); break;
            case "infra": OpenSingleton(key, "◫ Infrastructure", () => new InfraView()); break;
            case CloudWorkspaceView.NavKeyName: OpenCloud(); break;
            case "portals": OpenSingleton(key, "🌐 Portals", () => new PortalsView()); break;
            case "cloudpc": OpenSingleton(key, "🖥 Cloud PCs", () => new CloudPcView()); break;   // 0.6.4 (ADR-083)
            case "m365": OpenSingleton(key, "🛡 Microsoft 365 admin", () => new M365AdminView()); break;   // 0.6.6 (ADR-084)
            case "snippets": OpenSingleton(key, "⚡ Snippets", () => new SnippetsView()); break;
            case "tunnels": OpenSingleton(key, "⇄ Tunnels", () => new TunnelsView()); break;
            case "serial": OpenSingleton(key, "≋ Serial consoles", () => new SerialView()); break;
            case "lan": OpenSingleton(key, "⌕ LAN discovery", () => new LanScanView()); break;
            case "toolbox": OpenSingleton(key, "🧰 Toolbox", () => new Views.Toolbox.ToolboxView()); break;
            case "ansible": OpenSingleton(key, "⟐ Ansible", () => new AnsibleView()); break;
            case "puppet": OpenSingleton(key, "⚙ Puppet", () => new PuppetView()); break;
            case "wsl": OpenSingleton(key, "🐧 WSL", () => new WslView()); break;
            case "beyondtrust": OpenSingleton(key, "⛨ BeyondTrust", () => new BeyondTrustView()); break;
            case "bitwarden": OpenSingleton(key, "🔑 Bitwarden", () => new BitwardenView()); break;   // 0.6.14 (ADR-093)
            case "credentials": (OpenSingleton(key, "⚿ Credentials", () => new CredentialsView()).Content as CredentialsView)?.ShowSharedOnly(false); break;
            case "team:hosts": (OpenSingleton("hosts", "▣ Hosts", HostsPageForTab).Content as HostsView)?.ShowShared(); break;
            case "team:credentials": (OpenSingleton("credentials", "⚿ Credentials", () => new CredentialsView()).Content as CredentialsView)?.ShowSharedOnly(true); break;
            case "knownhosts": OpenSingleton(key, "✓ Known hosts", () => new KnownHostsView()); break;
            case "library": OpenSingleton(key, "⇄ Shared library", () => new SharedLibraryView()); break;
            case "apiexplorer": OpenSingleton(key, "{ } API explorer", () => new ApiExplorerView()); break;
            case "portal": WebWindow.Open("Vultr portal", "https://my.vultr.com/", "portal"); break;
            case "reports": OpenSingleton(key, "▤ Reports", () => new ReportView()); break;
            case "observe": OpenSingleton(key, "◉ Observability", () => new ObservabilityView()); break;
            case "help": OpenSingleton(key, "? Help", () => new HelpView()); break;
            case "privacy": OpenSingleton(key, "⛨ Privacy & network", () => new PrivacyView()); break;
            case "addons": OpenSingleton(key, "◇ Add-ons", () => new AddonsView()); break;
            case "audit": OpenSingleton(key, "☰ Audit log", () => new AuditView()); break;
            case "errorlog": OpenSingleton(key, "⚠ Error log", () => new ErrorLogView()); break;
            case "settings": OpenSingleton(key, "⚙ Settings", () => new SettingsView()); break;
            default:
                if (AppModules.FindNav(key) is { } contributed)
                {
                    // A module's item (0.2.0 phase 3): its screen as a singleton tab, or its own open action.
                    if (contributed.Open != null) { contributed.Open(); ReleaseNavHighlight(); }   // not a tab: the open screen keeps the highlight
                    else OpenSingleton(key, contributed.Header!, contributed.Screen!);
                }
                else if (key.StartsWith("local:") && Sessions.LocalShells.Find(key[6..]) is { } shell)
                {
                    OpenLocal(shell);
                    Dispatcher.BeginInvoke(() => OnActiveTab(Panes.ActiveTab));   // not a singleton: free the item for another click
                }
                else if (CloudProviders.All.Any(p => key.StartsWith(p.Id + ":")))
                {
                    // Cloud screens (ADR-020): "<provider>:<title>" or "<provider>:reference" open inside the Cloud
                    // providers workspace since 0.1.11 (ADR-028); the old keys keep working (dashboard links, scripts).
                    OpenCloud().Show(key);
                }
                break;
        }
    }

    /// <summary>Locks changed or closed again: a locked category's open screens close (nothing locked stays on show), and
    /// the sidebar redraws its padlocks. Session tabs (terminals, desktops) stay: they were opened while unlocked and closing
    /// them would lose work; the app's own lock hides everything anyway.</summary>
    private void OnLocksChanged()
    {
        foreach (var (key, tab) in _singletons.ToList())
            if (Locks.CategoryOfScreen(key) is { } cat && !Locks.IsOpen("cat:" + cat) && Panes.Contains(tab))
            {
                try { CloseTab(tab); } catch (Exception ex) { App.Log("Close locked screen", ex); }
            }
        BuildNav();
    }

    private async Task UnlockThenNavigateAsync(string key, string category)
    {
        if (await LockGate.EnsureAsync("cat:" + category, "open " + (Locks.FindCategory(category)?.Title ?? key))) Navigate(key);
        else Status($"“{Locks.FindCategory(category)?.Title ?? key}” stays locked.");
    }

    /// <summary>After a sidebar item that opens no tab: give the highlight back to the open screen (queued, after the click).</summary>
    public void ReleaseNavHighlight() => Dispatcher.BeginInvoke(() => OnActiveTab(Panes.ActiveTab));

    /// <summary>The Cloud providers workspace (one tab), opened or brought to the front.</summary>
    public CloudWorkspaceView OpenCloud()
    {
        var tab = OpenSingleton(CloudWorkspaceView.NavKeyName, "☁ Cloud providers", () => new CloudWorkspaceView());
        if (_navByKey.TryGetValue(CloudWorkspaceView.NavKeyName, out var rb) && rb.IsChecked != true) { _suppressNav = true; try { rb.IsChecked = true; } finally { _suppressNav = false; } }
        return (CloudWorkspaceView)tab.Content;
    }

    /// <summary>Opens Help & FAQ, pre-filled with the current screen (or a query).</summary>
    public void ShowHelp(string? query = null)
    {
        var screen = Panes.ActiveTab?.Header as string;
        Navigate("help");
        HelpView.Current?.Prefill(screen, query);
    }
}
