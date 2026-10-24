namespace DcxManager.Services;

/// <summary>
/// Environment labels (spec §55, ADR-017): one vocabulary for hosts, sessions, infrastructure providers and incidents, a
/// colour + text label wherever a host appears, and production safeguards. Colour is never the only signal. This is the
/// UI-free part (0.2.0 phase 6); the pills, strips and tab labels are <c>Views.EnvUi</c>.
/// </summary>
public static class Envs
{
    public const string Production = "PRODUCTION", Test = "TEST", Lab = "LAB", Development = "DEVELOPMENT", Local = "LOCAL";
    public static readonly string[] All = { Production, Test, Lab, Development, Local };

    /// <summary>A host's environment changed (tabs, strips and lists re-read it).</summary>
    public static event Action? Changed;
    public static void RaiseChanged() { try { Changed?.Invoke(); } catch (Exception ex) { Log.Write("environment changed", ex); } }
    public static int ChangedSubscribers => Changed?.GetInvocationList().Length ?? 0;

    /// <summary>Upper-case, common spellings folded; anything else stays as a custom label; empty = not set.</summary>
    public static string Normalize(string? env)
    {
        var e = (env ?? "").Trim().ToUpperInvariant();
        return e switch
        {
            "" or "(NOT SET)" or "NONE" => "",
            "PROD" or "PRD" or "LIVE" => Production,
            "DEV" => Development,
            "QA" or "UAT" or "STAGING" or "STAGE" or "TST" => Test,
            _ => e.Length > 24 ? e[..24] : e,
        };
    }

    public static bool IsProduction(string? env) => Normalize(env) == Production;

    /// <summary>A host's environment; local shells (no host) are LOCAL. Saved hosts are looked up by id, because editing a
    /// host replaces its object while open tabs still hold the old one.</summary>
    public static string For(HostEntry? h) => h == null ? Local : Normalize(Live(h).Environment);

    public static HostEntry Live(HostEntry h) => h.IsAdHoc ? h : AppData.Current.FindHost(h.Id) ?? h;

    public static string Short(string? env) => Normalize(env) switch
    {
        Production => "PROD",
        Development => "DEV",
        "" => "",
        var e => e.Length > 6 ? e[..6] : e,
    };

    /// <summary>The theme resource key of an environment's colour (the screens look it up).</summary>
    public static string BrushKey(string? env) => Normalize(env) switch
    {
        Production => "EnvProd",
        Test => "EnvTest",
        Lab => "EnvLab",
        Development => "EnvDev",
        Local => "EnvLocal",
        _ => "EnvOther",
    };

    /// <summary>
    /// A confirmation that knows the host's environment: a coloured banner for any labelled host; for PRODUCTION always a
    /// danger confirmation that needs the host's name typed (even with confirmations switched off).
    /// </summary>
    public static bool Confirm(HostEntry? host, string title, string message, string action = "Continue", bool danger = true, string? typeToConfirm = null)
    {
        var live = host == null ? null : Live(host);
        var env = live == null ? "" : Normalize(live.Environment);
        if (IsProduction(env)) { danger = true; typeToConfirm = live!.Display; }   // always the host's name on PRODUCTION
        return Ui.Dialogs.Confirm(title, message, action, danger, typeToConfirm, env.Length > 0 ? (env, live!.Display) : null);
    }

    /// <summary>Several hosts at once (multi-run, broadcast input): grouped by environment; any PRODUCTION host means typing PRODUCTION.</summary>
    public static bool ConfirmMany(IReadOnlyCollection<HostEntry> hosts, string title, string message, string action = "Run", bool danger = false)
    {
        var groups = hosts.GroupBy(For).OrderBy(g => Array.IndexOf(All, g.Key) is var i && i < 0 ? 99 : i)
            .Select(g => $"{(g.Key.Length > 0 ? g.Key : "(no environment)")}: {string.Join(", ", g.Select(h => h.Display))}");
        bool prod = hosts.Any(h => IsProduction(For(h)));
        var text = message + "\n\n" + string.Join("\n", groups);
        return Ui.Dialogs.Confirm(title, text, action, danger: prod || danger, typeToConfirm: prod ? Production : null,
            banner: prod ? (Production, $"{hosts.Count(h => IsProduction(For(h)))} of {hosts.Count} hosts") : null);
    }
}
