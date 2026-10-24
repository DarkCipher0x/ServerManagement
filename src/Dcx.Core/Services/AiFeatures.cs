namespace DcxManager.Services;

/// <summary>
/// The AI features switch (0.2.0 phase 5, docs/history/PLAN-0.2.0.md §3): whether the AI module is loaded at all. Decided once at
/// start-up and applied on the next start after a change. Checked in this order:
/// <list type="number">
/// <item>machine policy <c>HKLM\SOFTWARE\Policies\DarkCipher Systems\ServerManagement</c> value <c>AiFeatures</c> (DWORD):
/// 0 forces off, 1 forces on (the MSI property AIFEATURES=0 writes the 0); the Settings card then says it is set by the
/// administrator;</item>
/// <item>the environment variable <c>DCX_AI_FEATURES</c> = 0 or 1 (self-test, trying it out) — ignored while the AI
/// features lock is set, so a shortcut's environment can't bypass the lock;</item>
/// <item>the setting (<see cref="AppSettings.AiFeatures"/>).</item>
/// </list>
/// With AI off the AI's settings, chats and models stay in the encrypted store untouched; turning it back on restores them.
/// </summary>
public static class AiFeatures
{
    public const string PolicyKey = @"SOFTWARE\Policies\DarkCipher Systems\ServerManagement";
    public const string PolicyValue = "AiFeatures";
    public const string EnvVar = "DCX_AI_FEATURES";

    public enum From { Setting, Policy, Environment }

    /// <summary>The decision for this run: on or off, what decided it, and whether the lock is set.</summary>
    public sealed record State(bool On, From Source, bool Locked)
    {
        public string Describe() => (On ? "on" : "off") + Source switch
        {
            From.Policy => " (set by your administrator)",
            From.Environment => $" ({EnvVar} for this run)",
            _ => "",
        } + (Locked ? " · locked" : "");
    }

    /// <summary>This run's decision. Before start-up decides it (tools, early tests) AI counts as on.</summary>
    public static State Current { get; private set; } = new(true, From.Setting, false);

    /// <summary>The rule, without side effects (the self-test checks every combination).</summary>
    public static State Decide(int? policy, string? env, bool setting, bool locked)
    {
        if (policy is 0) return new(false, From.Policy, locked);
        if (policy is 1) return new(true, From.Policy, locked);
        if (!locked && env is "0" or "1") return new(env == "1", From.Environment, locked);
        return new(setting, From.Setting, locked);
    }

    /// <summary>The policy value (computer, else user), or null when there is none. Read through
    /// <see cref="PolicySettings"/> since 0.3.0 (the same key and value name as before).</summary>
    public static int? ReadPolicy() => PolicySettings.Get(PolicyValue) is { Data: bool b } ? (b ? 1 : 0) : null;

    /// <summary>The first 0.2.0 start decides the setting once: an existing store keeps AI on only if it was in use
    /// (enabled with an engine); a new install starts with AI off. Returns true when it decided now.</summary>
    public static bool Migrate(AppSettings s, bool existedBefore)
    {
        if (s.AiFeatures != null) return false;
        s.AiFeatures = existedBefore && s.AiEnabled && s.AiEngine != "off";
        return true;
    }

    /// <summary>Start-up: migrate, notice a lock removed outside the app, decide, and audit the module state.</summary>
    public static State DecideAtStartup()
    {
        var s = AppSettings.Current;
        if (Migrate(s, AppSettings.ExistedBeforeLoad))
        {
            try { AppSettings.Save(); } catch (Exception ex) { Log.Write("AI features migration", ex); }
            AuditLog.Write("app", "ai-features", s.AiFeatures == true ? "AI features kept on (upgrade: AI was in use)"
                : AppSettings.ExistedBeforeLoad ? "AI features off (upgrade: AI was not in use)" : "AI features off (new install)");
        }
        FeatureLock.ReconcileAtStart();
        Current = Decide(ReadPolicy(), Environment.GetEnvironmentVariable(EnvVar), s.AiFeatures == true, FeatureLock.IsSet);
        AuditLog.Write("app", "ai-features", Current.On ? "AI module loaded" : "AI module not loaded", Current.Describe());
        return Current;
    }

    /// <summary>The setting now differs from what this run loaded, so a restart would change it.</summary>
    public static bool RestartPending => Current.Source == From.Setting && (AppSettings.Current.AiFeatures == true) != Current.On;

    /// <summary>Changes the setting (the caller has passed the lock's gate). Applies on the next start.</summary>
    public static void Set(bool on)
    {
        AppSettings.Current.AiFeatures = on;
        AppSettings.Save();
        AuditLog.Write("you", "ai-features", on ? "AI features turned on (applies after a restart)" : "AI features turned off (applies after a restart)");
    }
}
