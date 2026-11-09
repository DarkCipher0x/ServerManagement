namespace DcxManager.Services;

/// <summary>
/// The CipherChat switch (ADR-117): whether the chat module (Dcx.Chat, built on CipherChat's own engine) is loaded at
/// all. Off by default. Decided once at start-up and applied on the next start after a change, like the AI switch
/// (<see cref="AiFeatures"/>). Checked in this order:
/// <list type="number">
/// <item>Group Policy <c>ChatFeatures</c> (DWORD, computer then user): 0 forces off, 1 forces on; Settings then says it is
/// set by the administrator;</item>
/// <item>the environment variable <c>DCX_CHAT_FEATURES</c> = 0 or 1 (the self-test, trying it out);</item>
/// <item>the setting (<see cref="AppSettings.ChatFeatures"/>).</item>
/// </list>
/// Off means none of the chat code or CipherChat's libraries is loaded: no sidebar item, menu, widget or share target —
/// nothing to hide. The chat's accounts stay in the encrypted store untouched; turning it back on restores them.
/// </summary>
public static class ChatFeatures
{
    public const string PolicyValue = nameof(AppSettings.ChatFeatures);
    public const string EnvVar = "DCX_CHAT_FEATURES";

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
        Current = Decide(ReadPolicy(), Environment.GetEnvironmentVariable(EnvVar), AppSettings.Current.ChatFeatures);
        AuditLog.Write("app", "chat", Current.On ? "CipherChat module loaded" : "CipherChat module not loaded", Current.Describe());
        return Current;
    }

    /// <summary>The setting now differs from what this run loaded, so a restart would change it.</summary>
    public static bool RestartPending => Current.Source == From.Setting && AppSettings.Current.ChatFeatures != Current.On;

    /// <summary>Changes the setting. Applies on the next start.</summary>
    public static void Set(bool on)
    {
        if (PolicySettings.IsManaged(PolicyValue)) throw new InvalidOperationException("CipherChat is set by your organisation's policy.");
        AppSettings.Current.ChatFeatures = on;
        AppSettings.Save();
        AuditLog.Write("you", "chat", on ? "CipherChat turned on (from the next start)" : "CipherChat turned off (from the next start)");
    }
}
