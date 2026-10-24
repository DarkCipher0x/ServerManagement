using System.Diagnostics;
using System.IO;
using DcxManager.Services;

namespace DcxManager.Infra;

/// <summary>
/// Hyper-V on Windows Server and on Windows Pro / Enterprise / Education (0.5.1 #6): which edition this PC is, what installs
/// the Hyper-V PowerShell module on it, and the one-click install (Windows' own command, run elevated through UAC — the
/// app never installs anything silently). Windows Home has no Hyper-V and no module; that is said plainly.
/// </summary>
public static class HyperVSetup
{
    /// <summary>Win32_OperatingSystem.ProductType: 1 workstation, 2 domain controller, 3 server.</summary>
    public sealed record Edition(int ProductType, string Caption)
    {
        public bool IsServer => ProductType is 2 or 3;
        public bool IsHome => !IsServer && Caption.Contains("Home", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The fix for a missing module on this edition: the reason, and the command (null when there is none).</summary>
    public static (string Why, string? Command) ModuleFix(Edition e) =>
        e.IsHome ? ($"{e.Caption} has no Hyper-V: the Hyper-V module needs Windows Pro, Enterprise or Education, or Windows Server. Manage a Hyper-V server from a Pro or Server PC, or add the server's VMs over SSH / Remote Desktop instead.", null)
        : e.IsServer ? ($"The Hyper-V PowerShell module isn't installed on {e.Caption}. Windows Server installs it with the Hyper-V-PowerShell feature (no reboot; the Hyper-V role itself is not needed to manage another host).",
            "Install-WindowsFeature -Name Hyper-V-PowerShell")
        : ($"The Hyper-V PowerShell module isn't installed on {e.Caption}. Windows installs it with the optional feature Microsoft-Hyper-V-Management-PowerShell (Settings → Optional features → Hyper-V Management Tools), no reboot needed to manage another host.",
            "Enable-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V-Management-PowerShell -All -NoRestart");

    /// <summary>Reads "NO_MODULE|&lt;ProductType&gt;|&lt;Caption&gt;" as the prelude writes it.</summary>
    public static Edition? ParseNoModule(string err)
    {
        var line = err.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("NO_MODULE", StringComparison.Ordinal));
        if (line == null) return null;
        var parts = line.Split('|');
        return new Edition(parts.Length > 1 && int.TryParse(parts[1], out var t) ? t : 1, parts.Length > 2 ? parts[2].Trim() : "Windows");
    }

    /// <summary>What the signed-in user may do on this PC's Hyper-V, from the prelude's "who" answer.</summary>
    public static string RightsNote(bool admin, bool elevated, bool hyperVAdmin) =>
        hyperVAdmin ? "You are in this PC's “Hyper-V Administrators” group: reading and changes work without elevation."
        : admin && elevated ? "The app runs elevated as an administrator: reading and changes work."
        : admin ? "You are an administrator, but the app isn't elevated and you aren't in “Hyper-V Administrators”: Windows will refuse Hyper-V cmdlets. Add yourself to “Hyper-V Administrators” (then sign out and in) — the app doesn't need to run elevated."
        : "You aren't in this PC's “Hyper-V Administrators” group: Windows will refuse Hyper-V cmdlets. An administrator can add you (Computer Management → Local Users and Groups), then sign out and in.";

    /// <summary>Test seam: starts the elevated install; returns its exit code. Default: Windows PowerShell through UAC.</summary>
    internal static Func<string, CancellationToken, Task<int>> RunElevated = RunElevatedAsync;

    /// <summary>Installs the module with Windows' own command for this edition, through a UAC prompt (the user clicks it).
    /// Returns null on success, otherwise why not.</summary>
    public static async Task<string?> InstallModuleAsync(Edition e, CancellationToken ct)
    {
        if (ModuleFix(e).Command is not { } command) return ModuleFix(e).Why;
        AuditLog.Write("you", "hyper-v", "Hyper-V module install requested", command);
        int exit;
        try { exit = await RunElevated(command, ct); }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { return "Cancelled at the UAC prompt."; }
        AuditLog.Write("you", "hyper-v", exit == 0 ? "Hyper-V module installed" : "Hyper-V module install failed", $"exit {exit}");
        return exit == 0 ? null : $"Windows' install ended with code {exit}. Run it yourself in an elevated PowerShell to see why: {command}";
    }

    private static async Task<int> RunElevatedAsync(string command, CancellationToken ct)
    {
        if (TestMode.Running) throw new InvalidOperationException("no UAC prompt in the self-test");
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var psi = new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Normal };
        foreach (var a in new[] { "-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", "$ErrorActionPreference = 'Stop'; " + command + "; exit 0" }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Windows PowerShell didn't start");
        await p.WaitForExitAsync(ct);
        return p.ExitCode;
    }
}
