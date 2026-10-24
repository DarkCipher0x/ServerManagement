using DcxManager.Ide;
using DcxManager.Services;
using DcxManager.Ssh;

namespace DcxManager.Views;

/// <summary>The screens' side of IDE integration (0.5.0, ADR-065): picks the editor, asks once before the SSH config is
/// touched, loads the host's key into ssh-agent for a while, and opens the editor's link.</summary>
public static class IdeUi
{
    public static string? EditorTitle => IdeIntegration.Preferred(IdeIntegration.Installed())?.Title;

    private static IdeIntegration.Editor? Editor()
    {
        var e = IdeIntegration.Preferred(IdeIntegration.Installed());
        if (e == null) Dialogs.Info("Open in an IDE", "No supported editor was found on this PC: Visual Studio Code, VS Code Insiders, Cursor, VSCodium, Windsurf or JetBrains Gateway (they register a link handler when installed).");
        return e;
    }

    public static async Task OpenHostAsync(HostEntry h, string path = "")
    {
        if (Editor() is not { } e) return;
        if (IdeIntegration.Problem(h) is { } why) { Dialogs.Error("Open in " + e.Title, $"{h.Display}: {why}."); return; }
        try
        {
            if (e.Id != "gateway")
            {
                bool include = IdeIntegration.IncludePresent() || Dialogs.Confirm("Let the IDE reach your hosts",
                    $"{e.Title}'s Remote-SSH reads ~/.ssh/config. The app keeps its own file, ~/.ssh/dcx_config, with a Host entry per saved SSH host (address, user, port — no password, key or key path), and adds one “Include dcx_config” line to the top of ~/.ssh/config (a copy of the old file is kept as config.dcx-bak).\n\nSettings → Integrations → IDE integration removes both.", "Allow");
                if (!include) return;
                IdeIntegration.WriteConfig(AppData.Current.Hosts.Where(x => x.Protocol == "ssh" && !x.IsAdHoc), mayAddInclude: true);
            }
            if (SshConnector.CredentialFor(h) is { Kind: CredentialKind.PrivateKey } key)
            {
                try { MainWindow.Instance?.Status(await IdeIntegration.AgentAddAsync(key, AppSettings.Current.IdeAgentMinutes)); }
                catch (Exception ex) when (ex is InvalidOperationException or TimeoutException) { Dialogs.Info("ssh-agent", ex.Message + "\n\nThe editor opens anyway; it will ask for a password or use your own keys."); }
            }
            IdeIntegration.Open(IdeIntegration.SshUri(e, h, path));
            AuditLog.Write("you", h.Display, "opened in " + e.Title, path.Length > 0 ? "folder chosen" : "");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.IO.IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { Dialogs.Error("Open in " + e.Title, ex.Message); }
    }

    public static void OpenWsl(string distro, string path = "")
    {
        if (Editor() is not { } e) return;
        try { IdeIntegration.Open(IdeIntegration.WslUri(e, distro, path)); AuditLog.Write("you", "wsl", "opened in " + e.Title, distro); }
        catch (Exception ex) when (ex is ArgumentException or System.ComponentModel.Win32Exception) { Dialogs.Error("Open in " + e.Title, ex.Message); }
    }

    public static void OpenLocal(string folder)
    {
        if (Editor() is not { } e) return;
        try { IdeIntegration.Open(IdeIntegration.LocalUri(e, folder)); }
        catch (Exception ex) when (ex is ArgumentException or System.ComponentModel.Win32Exception) { Dialogs.Error("Open in " + e.Title, ex.Message); }
    }
}
