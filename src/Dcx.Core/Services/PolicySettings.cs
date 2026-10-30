using System.Globalization;
using System.Reflection;
using Microsoft.Win32;

namespace DcxManager.Services;

/// <summary>
/// Group Policy for (almost) every setting (0.3.0 §10, ADR-040). Administrators set values under the standard policy keys
/// <c>HKLM\SOFTWARE\Policies\DarkCipher Systems\ServerManagement</c> (computer) and
/// <c>HKCU\SOFTWARE\Policies\DarkCipher Systems\ServerManagement</c> (user), by Group Policy (the ADMX templates every
/// build writes), Intune or a .reg file. The value name is the setting's own name (<c>OfflineMode</c>, <c>AccentColor</c>…):
/// DWORD for on/off and whole numbers, REG_SZ for text and decimals (REG_EXPAND_SZ for paths), REG_MULTI_SZ for lists.
/// <para>Order: computer policy, then user policy, then the admin-protected record, then the user's own settings. Managed
/// values are put back after every load and before every save (<see cref="AppSettings.Enforce"/>), so they can't be
/// changed or kept changed in the app.</para>
/// <para>The catalog is every setting of <see cref="AppSettings"/> with a simple type, minus <see cref="Excluded"/> (kept
/// by hand, each with its reason); every catalog entry has a category. The self-test fails when a setting is in neither
/// list or has no category, so a new setting can't be forgotten.</para>
/// </summary>
public static class PolicySettings
{
    public const string Key = AiFeatures.PolicyKey;   // SOFTWARE\Policies\DarkCipher Systems\ServerManagement

    /// <summary>Settings that are never policies, and why.</summary>
    public static readonly IReadOnlyDictionary<string, string> Excluded = new Dictionary<string, string>
    {
        [nameof(AppSettings.VultrAccounts)] = "per-user cloud accounts with their API keys (secrets never go in the registry)",
        [nameof(AppSettings.ContaboAccounts)] = "per-user cloud accounts with their credentials",
        [nameof(AppSettings.HetznerAccounts)] = "per-user cloud projects with their tokens",
        [nameof(AppSettings.AzureAccounts)] = "per-user subscriptions with their certificates",
        [nameof(AppSettings.GcpAccounts)] = "per-user projects with their key files",
        [nameof(AppSettings.ActiveVultrAccountId)] = "points at one of the user's own accounts",
        [nameof(AppSettings.ActiveContaboAccountId)] = "points at one of the user's own accounts",
        [nameof(AppSettings.ActiveHetznerAccountId)] = "points at one of the user's own accounts",
        [nameof(AppSettings.ActiveAzureAccountId)] = "points at one of the user's own accounts",
        [nameof(AppSettings.ActiveGcpAccountId)] = "points at one of the user's own accounts",
        [nameof(AppSettings.OpenAiKey)] = "an API key (a secret)",
        [nameof(AppSettings.SmtpPassword)] = "the SMTP password (a secret)",
        [nameof(AppSettings.UpdateGitHubToken)] = "a GitHub access token for updates from a private repository (a secret)",
        [nameof(AppSettings.EditorVimProfiles)] = "the user's own editor profiles (structured text, edited in Settings)",
        [nameof(AppSettings.AnthropicKey)] = "an API key (a secret)",
        [nameof(AppSettings.ApiTokens)] = "local API tokens (secrets)",
        [nameof(AppSettings.DefaultKeyCredentialId)] = "points at a credential in the user's vault",
        [nameof(AppSettings.OllamaTunnelHostId)] = "points at one of the user's hosts",
        [nameof(AppSettings.TerminalProfiles)] = "a list of records (edit profiles in the app)",
        [nameof(AppSettings.TerminalHighlightRules)] = "a list of records (the user's own highlight rules, edited in the app; Group Policy turns packs on or off with TerminalHighlightPacks)",
        [nameof(AppSettings.PaletteRecent)] = "what the command palette ran recently (state)",
        [nameof(AppSettings.DefaultTerminalProfileId)] = "points at one of the user's terminal profiles",
        [nameof(AppSettings.Addons)] = "installed add-ons and their granted permissions (managed in the app)",
        [nameof(AppSettings.ActiveIncidentId)] = "the incident open right now (state)",
        [nameof(AppSettings.AiLockSet)] = "mirrors the AI features lock file (state)",
        [nameof(AppSettings.AiDefaultChosen)] = "a one-time migration marker",
        [nameof(AppSettings.AiDefaultRevision)] = "a one-time migration marker",
        [nameof(AppSettings.CommanderLeft)] = "the last folder shown (state)",
        [nameof(AppSettings.CommanderRight)] = "the last folder shown (state)",
        [nameof(AppSettings.SidebarCollapsed)] = "whether the sidebar is hidden right now (Ctrl+B)",
        [nameof(AppSettings.AiPanelState)] = "the AI panel's dock state right now",
        [nameof(AppSettings.AiPanelWidth)] = "the AI panel's width right now",
        [nameof(AppSettings.HostsDockWidth)] = "the folder pane's width right now",
        [nameof(AppSettings.WindowHostPaneWidth)] = "the extra windows' host pane width right now",
        [nameof(AppSettings.WorkspaceWindows)] = "the extra windows open at the last exit (state)",
        [nameof(AppSettings.TmuxConfig)] = "your own tmux config (free text, often longer than a policy value)",
        [nameof(AppSettings.CollapsedNavSections)] = "replaced by ExpandedNavSections (kept to read old settings)",
        [nameof(AppSettings.ExpandedNavSections)] = "which sidebar sections are open right now",
        [nameof(AppSettings.CollapsedSettingsCards)] = "no longer used (kept to read old settings)",
        [nameof(AppSettings.LastUpdateCheck)] = "when updates were last checked (state)",
        [nameof(AppSettings.PendingUpdateVersion)] = "an update in progress (state)",
        [nameof(AppSettings.PendingUpdateFrom)] = "an update in progress (state)",
        [nameof(AppSettings.PreviousDataDir)] = "where the store was before a move (state)",
        [nameof(AppSettings.SyncWarningDismissedFor)] = "warnings the user dismissed (state)",
        [nameof(AppSettings.SetupDone)] = "whether the first-run setup was completed (state)",
        [nameof(AppSettings.LocalSshKeysSeen)] = "which of the user's own .ssh keys were already offered (state)",
        [nameof(AppSettings.BrandingLogoPng)] = "the logo image picked in Settings (too large for the registry; a policy uses BrandingLogoPath instead)",
        [nameof(AppSettings.PuppetServerHostId)] = "which saved host is the Puppet server (per user; host ids differ per store)",
        [nameof(AppSettings.PuppetDbHostId)] = "which saved host runs PuppetDB (per user; host ids differ per store)",
        [nameof(AppSettings.BackupLastUtc)] = "when the last backup was written (state)",
        [nameof(AppSettings.BackupLastError)] = "the last scheduled backup's result (state)",
        [nameof(AppSettings.ObserveSecrets)] = "the log sinks' API keys, passwords and tokens and the Grafana token (secrets never go in the registry)",
        [nameof(AppSettings.UserLocks)] = "the user's own locks with their password verifiers (secrets; Group Policy locks categories with LockedCategories instead)",
    };

    /// <summary>The category each policy is shown under (Group Policy editor, Settings, docs), by setting name.</summary>
    private static readonly (string Category, string[] Names, string[] Prefixes)[] Categories =
    {
        ("Security and sign-in", new[] { nameof(AppSettings.AutoLockMinutes), nameof(AppSettings.LockOnWindowsLock), nameof(AppSettings.SandboxMode), nameof(AppSettings.HelloUnlock), nameof(AppSettings.HelloConfirm), nameof(AppSettings.ConfirmDestructive), nameof(AppSettings.TeamFeatures) }, Array.Empty<string>()),
        ("Network and offline mode", new[] { nameof(AppSettings.TelnetEnabled), nameof(AppSettings.PortalBrowserBuiltIn), nameof(AppSettings.PortalOpenIn), nameof(AppSettings.PortalDownloadFolder) }, new[] { "Offline", "LocalApi" }),
        ("AI", Array.Empty<string>(), new[] { "Ai", "Ollama", "Foundry", "OpenAi", "Anthropic", "ClaudeCode" }),
        ("Terminals and connections", new[] { nameof(AppSettings.Scrollback), nameof(AppSettings.DefaultUsername), nameof(AppSettings.LocalSshKeysAutoImport), nameof(AppSettings.ConnectTimeoutSeconds), nameof(AppSettings.AutoReconnectMaxSeconds), nameof(AppSettings.RestoreWorkspace), nameof(AppSettings.SessionsOnExit), nameof(AppSettings.RestoreReconnect), nameof(AppSettings.RdpEmbedded), nameof(AppSettings.ReuseOpenTabs), nameof(AppSettings.CloudSyncHosts), nameof(AppSettings.EditorVimMode), nameof(AppSettings.EditorVimProfile), nameof(AppSettings.EditorLineNumbers), nameof(AppSettings.EditorSyntaxColours), nameof(AppSettings.SessionLogs) }, new[] { "Terminal", "Tmux" }),
        ("Appearance", new[] { nameof(AppSettings.Theme), nameof(AppSettings.AccentColor), nameof(AppSettings.ThemePreset), nameof(AppSettings.StatusIntensity), nameof(AppSettings.FollowWindowsHighContrast), nameof(AppSettings.Corners), nameof(AppSettings.Motion), nameof(AppSettings.Density), nameof(AppSettings.MonoAccents), nameof(AppSettings.ThemeAddon) }, new[] { "Ui", "MonoFont", "EditorFont", "Branding" }),
        ("Navigation and dashboard", new[] { nameof(AppSettings.LandingPage), nameof(AppSettings.PinnedNavItems), nameof(AppSettings.HiddenNavItems), nameof(AppSettings.NavSectionOrder), nameof(AppSettings.NavItemOrder), nameof(AppSettings.HiddenNavSections), nameof(AppSettings.RememberNavExpansion), nameof(AppSettings.CloudPinned), nameof(AppSettings.HostsView), nameof(AppSettings.HostsHiddenColumns), nameof(AppSettings.HostsDock), nameof(AppSettings.HostsOpenIn), nameof(AppSettings.WindowHostPane), nameof(AppSettings.RestoreWorkspaceWindows), nameof(AppSettings.PaletteLearn) }, new[] { "Sidebar", "Dashboard" }),
        ("Data, backups and files", new[] { nameof(AppSettings.ScreenshotDir), nameof(AppSettings.ScreenshotDefault), nameof(AppSettings.CommanderShowHidden), nameof(AppSettings.CommanderRestoreLast) }, new[] { "Snapshot", "AutoSnapshots", "Transfer", "Backup", "FileSync" }),
        ("Syslog, log sinks and notifications", Array.Empty<string>(), new[] { "Syslog", "Smtp", "Notify", "Observe", "Grafana" }),
        ("Updates", new[] { nameof(AppSettings.UpdateSource), nameof(AppSettings.UpdateEndpoint), nameof(AppSettings.AutoCheckUpdates), nameof(AppSettings.UpdateCheckInterval), nameof(AppSettings.AutoDownloadUpdates) }, Array.Empty<string>()),
        ("Identity and single sign-on", Array.Empty<string>(), new[] { "Sso" }),
        ("Post-quantum cryptography", Array.Empty<string>(), new[] { "Pq" }),
        ("Automation (Ansible and Puppet)", Array.Empty<string>(), new[] { "Puppet", "Ansible" }),
        ("IDE integration", Array.Empty<string>(), new[] { "Ide" }),
        ("Microsoft Teams", Array.Empty<string>(), new[] { "Teams" }),
        ("Cloud PCs", Array.Empty<string>(), new[] { "CloudPc" }),
        ("Microsoft 365 admin", Array.Empty<string>(), new[] { "M365" }),
        ("BeyondTrust", Array.Empty<string>(), new[] { "BeyondTrust" }),
        ("Bitwarden", Array.Empty<string>(), new[] { "Bitwarden" }),
        ("Locks and access", new[] { nameof(AppSettings.LockAuditLog), nameof(AppSettings.LockedCategories), nameof(AppSettings.LockPolicyMode), nameof(AppSettings.AllowUserLocks), nameof(AppSettings.AllowItemLocks), nameof(AppSettings.LockOpenMinutes) }, Array.Empty<string>()),
        ("Add-ons", new[] { nameof(AppSettings.AllowAddonInstall), nameof(AppSettings.AddonsNeedApproval), nameof(AppSettings.AddonAllowedIds), nameof(AppSettings.AddonBlockedIds), nameof(AppSettings.AddonRequireSigned) }, Array.Empty<string>()),
        ("What users may add and change", Array.Empty<string>(), new[] { "Allow" }),
    };

    /// <summary>Allowed values of settings that are a choice.</summary>
    private static readonly Dictionary<string, string[]> Choices = new()
    {
        [nameof(AppSettings.Theme)] = new[] { "System", "Dark", "Light" },
        [nameof(AppSettings.HostsDock)] = new[] { "pinned", "auto" },
        [nameof(AppSettings.HostsOpenIn)] = new[] { "hosts", "tab" },
        [nameof(AppSettings.SessionsOnExit)] = new[] { "ask", "save", "never" },
        [nameof(AppSettings.WindowHostPane)] = new[] { "pinned", "auto" },
        [nameof(AppSettings.UpdateSource)] = new[] { "github", "url" },
        [nameof(AppSettings.UpdateCheckInterval)] = new[] { "weekly", "daily" },
        [nameof(AppSettings.ThemePreset)] = new[] { "darkcipher", "graphite", "teal", "highcontrast", "neutral", "slate", "arctic" },
        [nameof(AppSettings.StatusIntensity)] = new[] { "subtle", "standard", "strong" },
        [nameof(AppSettings.Motion)] = new[] { "windows", "standard", "reduced" },
        [nameof(AppSettings.Density)] = new[] { "comfortable", "compact" },
        [nameof(AppSettings.SidebarStyle)] = new[] { "expanded", "compact", "icons" },
        [nameof(AppSettings.TerminalRightClick)] = new[] { "menu", "paste" },
        [nameof(AppSettings.ScreenshotDefault)] = new[] { "save", "clipboard", "both" },
        [nameof(AppSettings.DashboardChecks)] = new[] { "manual", "open", "interval" },
        [nameof(AppSettings.PortalOpenIn)] = new[] { "tab", "window" },
        [nameof(AppSettings.BeyondTrustJumpClient)] = new[] { "console", "web" },
        [nameof(AppSettings.SessionLogs)] = new[] { "profile", "on", "off" },
        [nameof(AppSettings.LockPolicyMode)] = new[] { "uac", "signin" },
        [nameof(AppSettings.FileSyncDeletion)] = new[] { "RecycleBin", "Versioning", "Permanent" },
    };

    /// <summary>Ranges of numeric settings (anything else: 0 or more).</summary>
    private static readonly Dictionary<string, (double Min, double Max)> Ranges = new()
    {
        [nameof(AppSettings.TerminalFontSize)] = (6, 40), [nameof(AppSettings.Scrollback)] = (100, 1_000_000), [nameof(AppSettings.TerminalAlertSeconds)] = (0, 86_400),
        [nameof(AppSettings.ConnectTimeoutSeconds)] = (1, 600), [nameof(AppSettings.AutoReconnectMaxSeconds)] = (1, 3600),
        [nameof(AppSettings.AiCommandTimeoutMinutes)] = (1, 120), [nameof(AppSettings.AiContext)] = (512, 1_048_576),
        [nameof(AppSettings.AiCpuThreads)] = (0, 256), [nameof(AppSettings.AiMaxSteps)] = (1, 100), [nameof(AppSettings.AiKeepWarmMinutes)] = (0, 1440),
        [nameof(AppSettings.Corners)] = (0, 8), [nameof(AppSettings.SnapshotKeep)] = (1, 1000), [nameof(AppSettings.SnapshotIntervalHours)] = (1, 720),
        [nameof(AppSettings.FileSyncVersionsKeepDays)] = (0, 3650), [nameof(AppSettings.FileSyncMaxDeletePercent)] = (1, 100),
        [nameof(AppSettings.SyslogPort)] = (1, 65535), [nameof(AppSettings.SmtpPort)] = (1, 65535), [nameof(AppSettings.NotifyMaxPerHour)] = (1, 100),
        [nameof(AppSettings.LocalApiPort)] = (1024, 65535), [nameof(AppSettings.TransferRetries)] = (0, 20), [nameof(AppSettings.DashboardCheckMinutes)] = (1, 1440),
        [nameof(AppSettings.AutoLockMinutes)] = (0, 480), [nameof(AppSettings.UiScale)] = (0.5, 3), [nameof(AppSettings.UiTextSize)] = (0.5, 3),
        [nameof(AppSettings.SidebarWidth)] = (100, 1000), [nameof(AppSettings.GrafanaOrgId)] = (0, 1_000_000),
        [nameof(AppSettings.IdeAgentMinutes)] = (5, 1440), [nameof(AppSettings.BeyondTrustCheckoutMinutes)] = (1, 480), [nameof(AppSettings.BitwardenLockMinutes)] = (1, 480), [nameof(AppSettings.BitwardenClipboardSeconds)] = (10, 300), [nameof(AppSettings.BackupMinPasswordLength)] = (12, 128), [nameof(AppSettings.BackupScheduleDays)] = (0, 30), [nameof(AppSettings.BackupKeep)] = (1, 100), [nameof(AppSettings.LockOpenMinutes)] = (0, 480),
    };

    public enum Kind { OnOff, Number, Decimal, Text, List }

    /// <summary>One policy: the setting, its registry type, category and what it accepts.</summary>
    public sealed record Entry(string Name, Kind Kind, string Category, PropertyInfo Property, (double Min, double Max)? Range, string[]? Choices)
    {
        /// <summary>"Offline mode" from "OfflineMode" (the name shown in templates and Settings).</summary>
        public string Title => ProtectedSettings.Items.FirstOrDefault(i => i.Setting == Name)?.Title
                               ?? (Restrictions.All.FirstOrDefault(a => a.Setting == Name) is { Action: not null } r ? "Allow " + r.What : null)
                               ?? Titles.GetValueOrDefault(Name) ?? Humanize(Name);
        public string RegistryType => Kind switch { Kind.OnOff or Kind.Number => "REG_DWORD", Kind.List => "REG_MULTI_SZ", _ => "REG_SZ" };
    }

    private static Kind? KindOf(Type t) =>
        t == typeof(bool) || t == typeof(bool?) ? Kind.OnOff : t == typeof(int) ? Kind.Number : t == typeof(double) ? Kind.Decimal
        : t == typeof(string) ? Kind.Text : t == typeof(List<string>) ? Kind.List : null;

    private static string? CategoryOf(string name) =>
        Categories.FirstOrDefault(c => c.Names.Contains(name) || c.Prefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal))).Category;

    /// <summary>Every policy, in category order.</summary>
    public static readonly IReadOnlyList<Entry> Catalog = typeof(AppSettings)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && p.CanWrite && !Excluded.ContainsKey(p.Name) && KindOf(p.PropertyType) != null && CategoryOf(p.Name) != null)
        .Select(p => new Entry(p.Name, KindOf(p.PropertyType)!.Value, CategoryOf(p.Name)!, p,
            Ranges.TryGetValue(p.Name, out var r) ? r : null, Choices.GetValueOrDefault(p.Name)))
        .OrderBy(e => Array.FindIndex(Categories, c => c.Category == e.Category)).ThenBy(e => e.Name, StringComparer.Ordinal)
        .ToList();

    public static IEnumerable<string> CategoryNames => Categories.Select(c => c.Category);

    /// <summary>Settings that are neither in the catalog nor excluded (the self-test wants none).</summary>
    public static IReadOnlyList<string> Unaccounted => typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && p.CanWrite && !Excluded.ContainsKey(p.Name) && Catalog.All(e => e.Name != p.Name))
        .Select(p => p.Name + (KindOf(p.PropertyType) == null ? " (type " + p.PropertyType.Name + ")" : " (no category)")).ToList();

    // ---- values -------------------------------------------------------------------------------------------------------

    /// <summary>A policy value in force: the setting, the value, and where it comes from ("computer" or "user").</summary>
    public sealed record Value(Entry Entry, object Data, string Scope)
    {
        public string Shown => Data switch { bool b => b ? "on" : "off", List<string> l => l.Count == 0 ? "(empty list)" : string.Join(", ", l), double d => d.ToString(CultureInfo.InvariantCulture), _ => Data.ToString() ?? "" };
    }

    /// <summary>Raw registry values by hive ("HKLM", "HKCU"): the self-test supplies its own instead of the registry.</summary>
    internal static Func<string, IReadOnlyDictionary<string, (object Data, RegistryValueKind Kind)>?>? SourceForTest { get; set; }

    private static Dictionary<string, Value> _values = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<string> _problems = new();

    public static IReadOnlyCollection<Value> Values => _values.Values;
    public static IReadOnlyList<string> Problems => _problems;
    public static bool IsManaged(string setting) => _values.ContainsKey(setting);
    public static Value? Get(string setting) => _values.GetValueOrDefault(setting);

    /// <summary>A computer-policy DWORD read directly (profiles are chosen before any store or settings exist, 0.3.0 §23):
    /// true / false, or null when it isn't set. The self-test's source stands in for the registry here too.</summary>
    public static bool? MachineFlag(string name)
    {
        try
        {
            var raw = SourceForTest != null ? SourceForTest("HKLM") : ReadHive(Registry.LocalMachine);
            return raw != null && raw.TryGetValue(name, out var v) && v.Kind == RegistryValueKind.DWord && v.Data is int i ? i != 0 : null;
        }
        catch (Exception ex) { Log.Write("machine policy " + name, ex); return null; }
    }

    /// <summary>A computer-policy string (REG_SZ) read directly, or null.</summary>
    public static string? MachineText(string name)
    {
        try
        {
            var raw = SourceForTest != null ? SourceForTest("HKLM") : ReadHive(Registry.LocalMachine);
            return raw != null && raw.TryGetValue(name, out var v) && v.Kind is RegistryValueKind.String && v.Data is string s && s.Length <= 2048 && !s.Any(char.IsControl) ? s : null;
        }
        catch (Exception ex) { Log.Write("machine policy " + name, ex); return null; }
    }

    /// <summary>Start-up: read the policies and have every load and save enforce them.</summary>
    public static void Install()
    {
        Reload();
        AppSettings.Enforce = ApplyTo;
    }

    /// <summary>Reads both policy keys again. Never throws; an invalid value is ignored and listed in <see cref="Problems"/>.</summary>
    public static void Reload()
    {
        var values = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase);
        _problems.Clear();
        // User first, computer second: the computer's value wins.
        foreach (var (hive, scope) in new[] { ("HKCU", "user"), ("HKLM", "computer") })
        {
            IReadOnlyDictionary<string, (object Data, RegistryValueKind Kind)>? raw;
            try { raw = SourceForTest != null ? SourceForTest(hive) : ReadHive(hive == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser); }
            catch (Exception ex) { _problems.Add($"{hive}: could not be read ({ex.Message})"); continue; }
            if (raw == null) continue;
            foreach (var (name, (data, kind)) in raw)
            {
                var entry = Catalog.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (entry == null)
                {
                    _problems.Add(Excluded.TryGetValue(name, out var why) ? $"{hive}\\{name}: can't be set by policy ({why})" : $"{hive}\\{name}: not a setting of this app");
                    continue;
                }
                if (Convert(entry, data, kind, out var value) is { } bad) { _problems.Add($"{hive}\\{name}: {bad} — ignored"); continue; }
                values[entry.Name] = new Value(entry, value!, scope);
            }
        }
        _values = values;
    }

    private static Dictionary<string, (object, RegistryValueKind)>? ReadHive(RegistryKey root)
    {
        using var k = root.OpenSubKey(Key);
        if (k == null) return null;
        var o = new Dictionary<string, (object, RegistryValueKind)>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in k.GetValueNames())
            if (n.Length > 0 && k.GetValue(n) is { } v) o[n] = (v, k.GetValueKind(n));
        return o;
    }

    /// <summary>Checks and converts one registry value. Returns why it is refused, or null.</summary>
    internal static string? Convert(Entry e, object data, RegistryValueKind kind, out object? value)
    {
        value = null;
        string? InRange(double d) => e.Range is { } r && (d < r.Min || d > r.Max) ? $"{d.ToString(CultureInfo.InvariantCulture)} is outside {r.Min.ToString(CultureInfo.InvariantCulture)}–{r.Max.ToString(CultureInfo.InvariantCulture)}" : e.Range == null && d < 0 ? "negative" : null;
        switch (e.Kind)
        {
            case Kind.OnOff:
                if (kind != RegistryValueKind.DWord || data is not int b || b is not (0 or 1)) return "must be a DWORD 0 (off) or 1 (on)";
                value = b == 1; return null;
            case Kind.Number:
                if (kind != RegistryValueKind.DWord || data is not int n) return "must be a DWORD";
                if (InRange(n) is { } r1) return r1;
                value = n; return null;
            case Kind.Decimal:
                double d;
                if (kind == RegistryValueKind.DWord && data is int i) d = i;
                else if (kind is RegistryValueKind.String && data is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p)) d = p;
                else return "must be a REG_SZ number like 1.25 (or a DWORD)";
                if (InRange(d) is { } r2) return r2;
                value = d; return null;
            case Kind.Text:
                if (kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString) || data is not string t) return "must be REG_SZ (or REG_EXPAND_SZ for a path)";
                if (t.Length > 2048 || t.Any(char.IsControl)) return "too long or contains control characters";
                if (e.Choices is { } c && !c.Contains(t, StringComparer.Ordinal)) return $"“{t}” is not one of {string.Join(", ", c)}";
                value = t; return null;
            case Kind.List:
                if (kind != RegistryValueKind.MultiString || data is not string[] items) return "must be REG_MULTI_SZ";
                var list = items.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (list.Count > 200 || list.Any(x => x.Length > 260 || x.Any(char.IsControl))) return "too many or too long entries";
                value = list; return null;
        }
        return "unsupported";
    }

    /// <summary>Puts every policy value over <paramref name="s"/>. Returns the settings it had to change.</summary>
    public static IReadOnlyList<string> ApplyTo(AppSettings s)
    {
        var changed = new List<string>();
        foreach (var v in _values.Values)
        {
            var cur = v.Entry.Property.GetValue(s);
            // A bool? with a value boxes as a bool, so Equals covers AiFeatures too.
            bool same = v.Data is List<string> l ? cur is List<string> c && c.SequenceEqual(l) : Equals(cur, v.Data);
            if (same) continue;
            v.Entry.Property.SetValue(s, v.Data is List<string> list ? list.ToList() : v.Data);
            changed.Add(v.Entry.Name);
        }
        return changed;
    }

    /// <summary>Titles where the property name doesn't read well on its own (0.5.1).</summary>
    private static readonly Dictionary<string, string> Titles = new()
    {
        [nameof(AppSettings.AddonAllowedIds)] = "Only these add-on ids may be installed (empty = any that passes the other checks)",
        [nameof(AppSettings.AddonBlockedIds)] = "Add-on ids that may never be installed or run",
        [nameof(AppSettings.LockedCategories)] = "Categories locked by your organisation (audit, credentials, hosts, …)",
        [nameof(AppSettings.CommanderRestoreLast)] = "File commander reconnects to its last places when it opens",
        [nameof(AppSettings.HostsDock)] = "The Hosts page's folder pane: pinned open or auto-collapsing (pinned or auto)",
        [nameof(AppSettings.TmuxInstall)] = "The terminal's tmux button installs tmux when it is missing (asking first)",
        [nameof(AppSettings.TmuxSession)] = "The tmux session the terminal's tmux button attaches to or creates",
        [nameof(AppSettings.M365AdminEnabled)] = "The Microsoft 365 admin page (Intune, Defender and Entra, read-only)",
        [nameof(AppSettings.CloudPcEnabled)] = "The Cloud PCs page (Windows 365 / Azure Virtual Desktop through the Windows App)",
        [nameof(AppSettings.HostsOpenIn)] = "Where sessions opened from the Hosts page go (hosts = inside the Hosts tab, tab = separate tabs)",
        [nameof(AppSettings.WindowHostPane)] = "The host pane in extra windows (other monitors): pinned open or auto-collapsing (pinned or auto)",
        [nameof(AppSettings.RestoreWorkspaceWindows)] = "Reopen the extra windows where they were (monitor, position, size) when the app starts",
        [nameof(AppSettings.SessionsOnExit)] = "Exiting with sessions open: ask (save and exit, or exit without saving), save (always reopen them next time) or never",
        [nameof(AppSettings.RestoreReconnect)] = "Saved sessions reconnect as they reopen (PRODUCTION hosts stay disconnected; locked hosts never reopen by themselves)",
        [nameof(AppSettings.LocalSshKeysAutoImport)] = "At start, copy new SSH keys from the Windows account's .ssh folder into the encrypted store (only keys without a passphrase; the files are only read)",
        [nameof(AppSettings.NavItemOrder)] = "The order of the pages inside each sidebar section (page keys; empty = the default order)",
        [nameof(AppSettings.ReuseOpenTabs)] = "Switch to an already-open tab of the same host instead of opening another",
        [nameof(AppSettings.CloudSyncHosts)] = "Saved hosts follow their cloud servers (new IP address, rebuilt server of the same name; removed servers are offered for removal)",
        [nameof(AppSettings.FileSyncScheduled)] = "Folder sync jobs may run on their schedule or in real time while the app is open (off: only when someone runs them)",
        [nameof(AppSettings.FileSyncDeletion)] = "Where new folder sync jobs send deleted and overwritten files: RecycleBin (versions on a server), Versioning or Permanent",
        [nameof(AppSettings.FileSyncVersionsKeepDays)] = "New folder sync jobs keep versions this many days (0 = forever)",
        [nameof(AppSettings.FileSyncMaxDeletePercent)] = "New folder sync jobs: a run without a preview stops when it would delete more than this percentage of a side's files",
        [nameof(AppSettings.FileSyncAllowPermanentDelete)] = "Folder sync may delete permanently on servers (admin-protected; otherwise deleted files are kept as versions)",
        [nameof(AppSettings.UpdateSource)] = "Where updates come from (github = the GitHub releases, url = the update address)",
        [nameof(AppSettings.UpdateCheckInterval)] = "How often the automatic update check runs (weekly or daily)",
        [nameof(AppSettings.AutoDownloadUpdates)] = "Download and verify a new version in the background (installing always asks)",
        [nameof(AppSettings.TmuxAlways)] = "Always-on tmux: every SSH terminal to a Linux or macOS host starts inside your tmux session (a host or folder can override; never installs tmux)",
        [nameof(AppSettings.TerminalAlerts)] = "Alerts from terminals you aren't looking at (a long command finished, a bell, a highlight alert, a dropped session)",
        [nameof(AppSettings.TerminalAlertSeconds)] = "A command running at least this many seconds alerts when it finishes (0 = never)",
        [nameof(AppSettings.TerminalAlertBell)] = "The terminal bell alerts",
        [nameof(AppSettings.TerminalAlertDrop)] = "A session that drops by itself alerts",
        [nameof(AppSettings.TerminalAlertDesktop)] = "Alerts also show as Windows notifications (off: the tab badge and the taskbar flash only)",
        [nameof(AppSettings.TerminalHighlight)] = "Colour keywords in terminal output (the highlight packs and the user's own rules)",
        [nameof(AppSettings.TerminalHighlightPacks)] = "The highlight packs that are on: errors, warnings, success, network, http",
        [nameof(AppSettings.TerminalLinks)] = "Ctrl+click an IP address, a path or a web link in terminal output for its actions",
        [nameof(AppSettings.TerminalButtonBar)] = "The snippet button bar on terminals",
        [nameof(AppSettings.PaletteLearn)] = "The command palette (Ctrl+Shift+P) puts what is used most at the top",
    };

    /// <summary>"Offline mode" from "OfflineMode", "AI allow cloud" from "AiAllowCloud".</summary>
    public static string Humanize(string name)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && (char.IsLower(name[i - 1]) || i + 1 < name.Length && char.IsLower(name[i + 1]))) sb.Append(' ');
            sb.Append(name[i]);
        }
        var words = sb.ToString().Split(' ');
        for (int i = 0; i < words.Length; i++)
            words[i] = words[i] switch
            {
                "Ai" => "AI", "Ui" => "UI", "Api" => "API", "Rdp" => "RDP", "Url" => "URL",
                _ => i == 0 ? words[i] : words[i].ToLowerInvariant(),
            };
        return string.Join(' ', words).Replace("Open AI", "OpenAI");
    }

    internal static void ResetForTest() { _values = new(StringComparer.OrdinalIgnoreCase); _problems.Clear(); }
}
