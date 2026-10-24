using System.Runtime.InteropServices;
using System.Text;

namespace DcxManager.Services;

/// <summary>
/// ADR-029: the same binaries ship as the MSI/zip (website) and as an MSIX for the Microsoft Store, which signs the
/// package for free. A Store install runs with package identity: Windows owns updates (the in-app updater stays off),
/// the app's identity (AUMID) comes from the manifest, and a restart goes through the package, not the exe path.
/// </summary>
public static class Packaging
{
    private const int AppModelErrorNoPackage = 15700;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFamilyName(ref int length, StringBuilder? name);

    private static readonly Lazy<string?> _family = new(() =>
    {
        try
        {
            int len = 0;
            if (GetCurrentPackageFamilyName(ref len, null) == AppModelErrorNoPackage || len <= 0) return null;
            var sb = new StringBuilder(len);
            return GetCurrentPackageFamilyName(ref len, sb) == 0 ? sb.ToString() : null;
        }
        catch { return null; }   // older Windows without the API: never packaged
    });

    /// <summary>The package family name when installed from an MSIX (Store or sideloaded), else null.</summary>
    public static string? FamilyName => _family.Value;

    public static bool IsPackaged => FamilyName != null;

    /// <summary>The manifest's Application Id (installer\msix\AppxManifest.xml).</summary>
    public const string ApplicationId = "App";

    /// <summary>What to start to relaunch this install: the package's app entry when packaged, else the exe.</summary>
    public static System.Diagnostics.ProcessStartInfo? RelaunchInfo(string? exePath) =>
        FamilyName is { } pfn ? new System.Diagnostics.ProcessStartInfo($@"shell:AppsFolder\{pfn}!{ApplicationId}") { UseShellExecute = true }
        : exePath is { } exe ? new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false }
        : null;
}
