using DcxManager.Services;

namespace DcxManager.WinTools;

/// <summary>
/// What Windows management may do on this PC (ADR-119), from settings that Group Policy can set (prefix WinTools). Every
/// screen and action asks here, so each rule is one line.
/// </summary>
public static class WinToolsPolicy
{
    private static AppSettings S => AppSettings.Current;

    public static string? ChangesBlocked() => S.WinToolsAllowChanges ? null
        : "Changes are turned off in Windows management on this PC (your organisation's policy or Settings → Windows management): you can look, not change.";

    public static string? RemoteBlocked() => S.WinToolsAllowRemote ? null
        : "Managing other computers is turned off in Windows management on this PC (your organisation's policy or Settings → Windows management).";

    public static string? ConsoleBlocked() => S.WinToolsAllowConsole ? null
        : "The WMI console is turned off on this PC (your organisation's policy or Settings → Windows management).";

    public static bool SecurityLogAllowed => S.WinToolsAllowSecurityLog;

    public static int EventLimit => Math.Clamp(S.WinToolsEventLimit, 50, 5000);
}
