namespace DcxManager.Services;

/// <summary>
/// The Windows management switch (0.6.39, ADR-119): whether the Windows management module (Dcx.WinTools: an MMC-style
/// console for services, processes, event logs, storage, users and groups, shares, scheduled tasks, network, start-up
/// programs, updates, drivers and a WMI/WQL console that also understands wmic commands) is loaded at all. On by default.
/// Decided once at start-up and applied on the next start after a change, like the AI and CipherChat switches. Order:
/// <list type="number">
/// <item>Group Policy <c>WinToolsFeatures</c> (DWORD, computer then user): 0 forces off, 1 forces on;</item>
/// <item>the environment variable <c>DCX_WINTOOLS_FEATURES</c> = 0 or 1 (the self-test, trying it out);</item>
/// <item>the setting (<see cref="AppSettings.WinToolsFeatures"/>).</item>
/// </list>
/// Off means none of the module's code is loaded: no sidebar item, menu or settings card from it.
/// </summary>
public static class WinToolsFeatures
{
    public const string PolicyValue = nameof(AppSettings.WinToolsFeatures);
    public const string EnvVar = "DCX_WINTOOLS_FEATURES";

    public enum From { Setting, Policy, Environment }

    public sealed record State(bool On, From Source)
    {
        public string Describe() => (On ? "on" : "off") + Source switch
        {
            From.Policy => " (set by your administrator)",
            From.Environment => $" ({EnvVar} for this run)",
            _ => "",
        };
    }

    /// <summary>This run's decision (off until start-up decides).</summary>
    public static State Current { get; private set; } = new(false, From.Setting);

    /// <summary>The rule, without side effects (the self-test checks every combination).</summary>
    public static State Decide(bool? policy, string? env, bool setting)
    {
        if (policy is { } p) return new(p, From.Policy);
        if (env is "0" or "1") return new(env == "1", From.Environment);
        return new(setting, From.Setting);
    }

    public static bool? ReadPolicy() => PolicySettings.Get(PolicyValue) is { Data: bool b } ? b : null;

    /// <summary>Start-up: decide, and audit whether the module is loaded.</summary>
    public static State DecideAtStartup()
    {
        Current = Decide(ReadPolicy(), Environment.GetEnvironmentVariable(EnvVar), AppSettings.Current.WinToolsFeatures);
        AuditLog.Write("app", "windows tools", Current.On ? "Windows management module loaded" : "Windows management module not loaded", Current.Describe());
        return Current;
    }

    /// <summary>The setting now differs from what this run loaded, so a restart would change it.</summary>
    public static bool RestartPending => Current.Source == From.Setting && AppSettings.Current.WinToolsFeatures != Current.On;

    /// <summary>Changes the setting. Applies on the next start.</summary>
    public static void Set(bool on)
    {
        if (PolicySettings.IsManaged(PolicyValue)) throw new InvalidOperationException("Windows management is set by your organisation's policy.");
        AppSettings.Current.WinToolsFeatures = on;
        AppSettings.Save();
        AuditLog.Write("you", "windows tools", on ? "Windows management turned on (from the next start)" : "Windows management turned off (from the next start)");
    }
}
