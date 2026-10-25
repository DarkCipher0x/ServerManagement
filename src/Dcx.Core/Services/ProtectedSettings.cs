using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace DcxManager.Services;

/// <summary>
/// Admin-protected settings (0.3.0 §1, ADR-039). Security settings — AI features, internet AI providers, offline mode,
/// the local API, Telnet, the app lock, sign-in with Windows Hello… — are kept where only administrators can write:
/// <c>HKLM\SOFTWARE\DarkCipher Systems\ServerManagement\Protected</c>, one value per Windows user (by SID), holding a small
/// JSON record. The app runs without admin rights and only reads it; a change goes through a request that the installed
/// app, started elevated (UAC), shows and writes (<see cref="ApplyRequest"/>). While a record exists its values override
/// the user's own settings file, which anything running as the user could edit. Before the first change nothing is
/// protected and the user's settings apply as before (Settings says so and offers to protect them).
/// The record's key must not be writable by non-administrators: a key with a looser ACL is ignored and reported.
/// </summary>
public static class ProtectedSettings
{
    public const string MachineKey = @"SOFTWARE\DarkCipher Systems\ServerManagement\Protected";
    public const string RequestFormat = "dcx-protected-change";
    public const int RequestVersion = 1;
    public const int MaxRequestBytes = 16 * 1024;
    public static readonly TimeSpan RequestLifetime = TimeSpan.FromMinutes(10);

    /// <summary>One protected setting: its key, how it is shown, how it is read from and written to the settings, and
    /// what values are allowed (null = fine, else why not). <paramref name="Tighten"/> says whether a new value only makes
    /// things stricter (applied for the session at once, before the admin step makes it stick).</summary>
    public sealed record Item(string Key, string Title, string Group,
        Func<AppSettings, JsonNode?> Get, Action<AppSettings, JsonNode> Set, Func<JsonNode?, string?> Check,
        Func<JsonNode?, string> Show, Func<JsonNode?, JsonNode?, bool>? Tighten = null)
    {
        /// <summary>The setting it covers (its name in the settings and in Group Policy).</summary>
        public string Setting { get; init; } = "";
    }

    private static Item Bool(string key, string setting, string title, string group, Func<AppSettings, bool> get, Action<AppSettings, bool> set, bool? safeValue = null) =>
        new(key, title, group, s => JsonValue.Create(get(s)), (s, v) => set(s, v.GetValue<bool>()),
            v => v?.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? null : "not on / off",
            v => v?.GetValueKind() == JsonValueKind.True ? "on" : "off",
            safeValue is { } sv ? (from, to) => to?.GetValueKind() == (sv ? JsonValueKind.True : JsonValueKind.False) : null) { Setting = setting };

    private static Item Int(string key, string setting, string title, string group, Func<AppSettings, int> get, Action<AppSettings, int> set, int min, int max, string unit = "") =>
        new(key, title, group, s => JsonValue.Create(get(s)), (s, v) => set(s, (int)Number(v)!.Value),
            v => Number(v) is { } d && d == Math.Floor(d) && d >= min && d <= max ? null : $"not a whole number from {min} to {max}",
            v => v == null ? "unset" : v.ToJsonString() + unit) { Setting = setting };

    private static Item Text(string key, string setting, string title, string group, Func<AppSettings, string> get, Action<AppSettings, string> set, Func<string, string?> check) =>
        new(key, title, group, s => JsonValue.Create(get(s)), (s, v) => set(s, v.GetValue<string>()),
            v => v?.GetValueKind() == JsonValueKind.String && v.GetValue<string>() is { Length: <= 1024 } t ? check(t) : "not text (up to 1,024 characters)",
            v => v?.GetValueKind() == JsonValueKind.String ? (v.GetValue<string>() is { Length: > 0 } t ? t : "(empty)") : "unset") { Setting = setting };

    /// <summary>A JSON number as a double, whatever .NET type it was created from (0.5.1 fix: a value made in code from an
    /// int threw "Int32 cannot be converted to Double" in GetValue&lt;double&gt;, so saving the backup schedule failed).</summary>
    internal static double? Number(JsonNode? v) =>
        v is JsonValue jv && jv.GetValueKind() == JsonValueKind.Number
        && double.TryParse(jv.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;

    /// <summary>A certificate pin: 64 hex digits (SHA-256), or empty.</summary>
    /// <summary>"" (bitwarden.com), "eu" (bitwarden.eu) or a self-hosted https://host[:port][/path] without a user name.</summary>
    public static string? BitwardenServerCheck(string t) =>
        t.Length == 0 || t == "eu" || Uri.TryCreate(t, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && u.UserInfo.Length == 0 && u.Query.Length == 0 && u.Fragment.Length == 0 && t.Length <= 256
            ? null : "not bitwarden.com (empty), eu, or an https:// address";

    internal static string? PinCheck(string t) => t.Length == 0 || System.Text.RegularExpressions.Regex.IsMatch(t, @"^[0-9A-Fa-f]{64}\z") ? null : "not a SHA-256 fingerprint (64 hex digits)";
    /// <summary>A host name or address (letters, digits, dots, hyphens, colons for IPv6), or empty.</summary>
    internal static string? HostCheck(string t) => t.Length == 0 || System.Text.RegularExpressions.Regex.IsMatch(t, @"^[A-Za-z0-9.\-:\[\]]{1,253}$") ? null : "not a host name or address";
    /// <summary>An absolute local or UNC folder path without control or wildcard characters, or empty.</summary>
    internal static string? PathCheck(string t) =>
        t.Length == 0 || t.Length <= 260 && !t.Any(char.IsControl) && t.IndexOfAny(new[] { '*', '?', '"', '<', '>', '|' }) < 0
            && (System.Text.RegularExpressions.Regex.IsMatch(t, @"^[A-Za-z]:\\") || t.StartsWith(@"\\")) && !t.Contains(@"\..") ? null : "not an absolute folder path";
    private static string? HexCheck(string t, int len) => t.Length == 0 || System.Text.RegularExpressions.Regex.IsMatch(t.Replace(":", ""), $"^[0-9A-Fa-f]{{{len}}}$") ? null : $"not a {len}-character hex fingerprint";
    /// <summary>The log-sink list's checker lives with the sinks (Dcx.Notify); the app sets it at start-up. Without it only an
    /// empty list is accepted (tools never store sinks they can't check).</summary>
    public static Func<string, string?> SinksCheck { get; set; } = t => t.Length == 0 ? null : "the log-sink checker isn't loaded";
    private static string? MailCheck(string t) => t.Length == 0 || t.Split(',', ';').Select(x => x.Trim()).Where(x => x.Length > 0).All(x => System.Net.Mail.MailAddress.TryCreate(x, out var a) && a.Address == x && !x.Any(char.IsControl)) ? null : "not e-mail address(es)";

    /// <summary>Everything protected, in the order Settings and the admin window list it.</summary>
    public static readonly IReadOnlyList<Item> Items = new[]
    {
        Bool("ai.features", nameof(AppSettings.AiFeatures), "AI features", "AI", s => s.AiFeatures == true, (s, v) => s.AiFeatures = v, safeValue: false),
        Bool("ai.allowCloud", nameof(AppSettings.AiAllowCloud), "Allow internet AI providers", "AI", s => s.AiAllowCloud, (s, v) => s.AiAllowCloud = v, safeValue: false),
        Bool("ai.redactSecrets", nameof(AppSettings.AiRedactSecrets), "Mask secrets before anything is sent to a model", "AI", s => s.AiRedactSecrets, (s, v) => s.AiRedactSecrets = v, safeValue: true),
        Bool("ai.autoRunReadOnly", nameof(AppSettings.AiAutoRunReadOnlyTarget), "Let the AI run read-only commands in its target terminal without asking", "AI", s => s.AiAutoRunReadOnlyTarget, (s, v) => s.AiAutoRunReadOnlyTarget = v, safeValue: false),
        Bool("net.offline", nameof(AppSettings.OfflineMode), "Offline mode", "Network", s => s.OfflineMode, (s, v) => s.OfflineMode = v, safeValue: true),
        Bool("net.pqSshRequired", nameof(AppSettings.PqSshRequired), "Require post-quantum key exchange for SSH", "Network", s => s.PqSshRequired, (s, v) => s.PqSshRequired = v, safeValue: true),
        new("net.offlineAllowed", "Approved destinations in offline mode", "Network",
            s => new JsonArray(s.OfflineAllowed.Select(x => (JsonNode)x).ToArray()),
            (s, v) => s.OfflineAllowed = v.AsArray().Select(x => x!.GetValue<string>()).ToList(),
            v => v is JsonArray a && a.Count <= 200 && a.All(x => x?.GetValueKind() == JsonValueKind.String && x.GetValue<string>() is { Length: > 0 and <= 260 } t && Offline.Validate(t) == null) ? null : "not a list of up to 200 valid destinations",
            v => v is JsonArray a ? (a.Count == 0 ? "none" : string.Join(", ", a.Select(x => x!.GetValue<string>()))) : "unset",
            (from, to) => from is JsonArray f && to is JsonArray t && t.All(x => f.Any(y => y!.GetValue<string>() == x!.GetValue<string>()))) { Setting = nameof(AppSettings.OfflineAllowed) },
        Bool("net.offlineAllowAddons", nameof(AppSettings.OfflineAllowAddons), "Add-ons may use approved destinations in offline mode", "Network", s => s.OfflineAllowAddons, (s, v) => s.OfflineAllowAddons = v, safeValue: false),
        Bool("net.localApi", nameof(AppSettings.LocalApiEnabled), "Local API (loopback only)", "Network", s => s.LocalApiEnabled, (s, v) => s.LocalApiEnabled = v, safeValue: false),
        Int("net.localApiPort", nameof(AppSettings.LocalApiPort), "Local API port", "Network", s => s.LocalApiPort, (s, v) => s.LocalApiPort = v, 1024, 65535),
        Bool("net.telnet", nameof(AppSettings.TelnetEnabled), "Allow Telnet (unencrypted)", "Network", s => s.TelnetEnabled, (s, v) => s.TelnetEnabled = v, safeValue: false),
        Bool("lock.onWindowsLock", nameof(AppSettings.LockOnWindowsLock), "Lock when Windows locks or the PC sleeps", "Sign-in and lock", s => s.LockOnWindowsLock, (s, v) => s.LockOnWindowsLock = v, safeValue: true),
        Int("lock.autoLockMinutes", nameof(AppSettings.AutoLockMinutes), "Auto-lock after minutes idle (0 = never)", "Sign-in and lock", s => s.AutoLockMinutes, (s, v) => s.AutoLockMinutes = v, 0, 480, " min"),
        Bool("lock.sandbox", nameof(AppSettings.SandboxMode), "Sandbox mode", "Sign-in and lock", s => s.SandboxMode, (s, v) => s.SandboxMode = v, safeValue: true),
        Bool("signin.hello", nameof(AppSettings.HelloUnlock), "Sign in with Windows Hello", "Sign-in and lock", s => s.HelloUnlock, (s, v) => s.HelloUnlock = v),
        Bool("signin.helloConfirm", nameof(AppSettings.HelloConfirm), "Confirm sensitive actions with Windows Hello", "Sign-in and lock", s => s.HelloConfirm, (s, v) => s.HelloConfirm = v, safeValue: true),
        // 0.3.0 §22: team features built in; switching them off is the stricter choice.
        Bool("team.features", nameof(AppSettings.TeamFeatures), "Team features (shared libraries)", "Sign-in and lock", s => s.TeamFeatures, (s, v) => s.TeamFeatures = v, safeValue: false),
        // 0.6.26 (ADR-102): folder sync deletes into the Recycle Bin or a versions folder; really deleting needs an admin.
        Bool("files.syncPermanentDelete", nameof(AppSettings.FileSyncAllowPermanentDelete), "Folder sync may delete permanently on servers", "Files", s => s.FileSyncAllowPermanentDelete, (s, v) => s.FileSyncAllowPermanentDelete = v, safeValue: false),
        // 0.3.0 §3: where the audit trail goes. Turning it on, or sending more, only ever adds a copy: "tighter".
        Bool("syslog.enabled", nameof(AppSettings.SyslogEnabled), "Send logs to a syslog server (TLS)", "Syslog", s => s.SyslogEnabled, (s, v) => s.SyslogEnabled = v, safeValue: true),
        Text("syslog.host", nameof(AppSettings.SyslogHost), "Syslog server", "Syslog", s => s.SyslogHost, (s, v) => s.SyslogHost = v.Trim(), HostCheck),
        Int("syslog.port", nameof(AppSettings.SyslogPort), "Syslog port (TLS)", "Syslog", s => s.SyslogPort, (s, v) => s.SyslogPort = v, 1, 65535),
        Text("syslog.pin", nameof(AppSettings.SyslogPin), "Syslog server certificate (SHA-256 pin)", "Syslog", s => s.SyslogPin, (s, v) => s.SyslogPin = v.Replace(":", "").Trim().ToUpperInvariant(), t => HexCheck(t, 64)),
        Text("syslog.clientCert", nameof(AppSettings.SyslogClientCert), "Syslog client certificate (thumbprint)", "Syslog", s => s.SyslogClientCert, (s, v) => s.SyslogClientCert = v.Replace(" ", "").Trim().ToUpperInvariant(), t => HexCheck(t.Replace(" ", ""), 40)),
        Bool("syslog.audit", nameof(AppSettings.SyslogSendAudit), "Send the audit log", "Syslog", s => s.SyslogSendAudit, (s, v) => s.SyslogSendAudit = v, safeValue: true),
        Bool("syslog.appLog", nameof(AppSettings.SyslogSendAppLog), "Send the application (error) log", "Syslog", s => s.SyslogSendAppLog, (s, v) => s.SyslogSendAppLog = v, safeValue: true),
        Bool("syslog.sessionLogs", nameof(AppSettings.SyslogSendSessionLogs), "Send the terminal session logs", "Syslog", s => s.SyslogSendSessionLogs, (s, v) => s.SyslogSendSessionLogs = v, safeValue: false),
        // 0.4.0 Observability: where else the audit trail goes (Elastic, Loki, Azure Monitor, Splunk, Graylog, webhooks). The
        // list carries no secrets (they stay in the encrypted settings); its JSON is checked here before anything is stored.
        Bool("observe.enabled", nameof(AppSettings.ObserveEnabled), "Ship logs to the log sinks (HTTPS)", "Log sinks", s => s.ObserveEnabled, (s, v) => s.ObserveEnabled = v, safeValue: true),
        new("observe.sinks", "Log sinks (JSON list, no secrets)", "Log sinks",
            s => JsonValue.Create(s.ObserveSinks), (s, v) => s.ObserveSinks = v.GetValue<string>(),
            v => v?.GetValueKind() == JsonValueKind.String && v.GetValue<string>() is { } t ? SinksCheck(t) : "not text",
            v => v?.GetValueKind() == JsonValueKind.String && v.GetValue<string>() is { Length: > 0 } t ? $"{t.Count(c => c == '{')} sink(s)" : "none") { Setting = nameof(AppSettings.ObserveSinks) },
        Bool("observe.audit", nameof(AppSettings.ObserveSendAudit), "Log sinks: send the audit log", "Log sinks", s => s.ObserveSendAudit, (s, v) => s.ObserveSendAudit = v, safeValue: true),
        Bool("observe.appLog", nameof(AppSettings.ObserveSendAppLog), "Log sinks: send the application (error) log", "Log sinks", s => s.ObserveSendAppLog, (s, v) => s.ObserveSendAppLog = v, safeValue: true),
        Bool("observe.sessionLogs", nameof(AppSettings.ObserveSendSessionLogs), "Log sinks: send the terminal session logs", "Log sinks", s => s.ObserveSendSessionLogs, (s, v) => s.ObserveSendSessionLogs = v, safeValue: false),
        Text("observe.grafanaUrl", nameof(AppSettings.GrafanaUrl), "Grafana address (dashboards)", "Log sinks", s => s.GrafanaUrl, (s, v) => s.GrafanaUrl = v.Trim(),
            t => t.Length == 0 || Uri.TryCreate(t, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && u.UserInfo.Length == 0 && t.Length <= 512 ? null : "not an https:// address without a user name"),
        // 0.5.0 (ADR-059): the safety nets and the record of what happened are an administrator's call too.
        Bool("safety.confirm", nameof(AppSettings.ConfirmDestructive), "Ask before every change (confirmations)", "Safety", s => s.ConfirmDestructive, (s, v) => s.ConfirmDestructive = v, safeValue: true),
        Bool("safety.confirmPaste", nameof(AppSettings.TerminalConfirmPaste), "Confirm multi-line and risky pastes", "Safety", s => s.TerminalConfirmPaste, (s, v) => s.TerminalConfirmPaste = v, safeValue: true),
        Text("terminal.sessionLogs", nameof(AppSettings.SessionLogs), "Terminal session logs (profile / on / off)", "Safety", s => s.SessionLogs, (s, v) => s.SessionLogs = v.Trim(),
            t => t is "profile" or "on" or "off" ? null : "not profile, on or off"),
        // 0.5.0 (#16): BeyondTrust — the integration, and whether vault credentials may be checked out.
        Bool("bt.enabled", nameof(AppSettings.BeyondTrustEnabled), "BeyondTrust integration (PRA / Password Safe)", "Integrations", s => s.BeyondTrustEnabled, (s, v) => s.BeyondTrustEnabled = v, safeValue: false),
        Bool("bt.checkout", nameof(AppSettings.BeyondTrustAllowCheckout), "BeyondTrust: check out Password Safe credentials for sessions", "Integrations", s => s.BeyondTrustAllowCheckout, (s, v) => s.BeyondTrustAllowCheckout = v, safeValue: false),
        Text("bt.praSite", nameof(AppSettings.BeyondTrustPraSite), "BeyondTrust PRA site", "Integrations", s => s.BeyondTrustPraSite, (s, v) => s.BeyondTrustPraSite = v.Trim(), t => t.Length == 0 || Uri.TryCreate(t, UriKind.Absolute, out var u) && u.Scheme == "https" && u.UserInfo.Length == 0 && u.AbsolutePath == "/" ? null : "not https://host"),
        Text("bt.psHost", nameof(AppSettings.BeyondTrustPsHost), "BeyondTrust Password Safe host", "Integrations", s => s.BeyondTrustPsHost, (s, v) => s.BeyondTrustPsHost = v.Trim(), t => t.Length == 0 || Uri.TryCreate(t, UriKind.Absolute, out var u) && u.Scheme == "https" && u.UserInfo.Length == 0 && u.AbsolutePath == "/" ? null : "not https://host"),
        Text("bt.praPin", nameof(AppSettings.BeyondTrustPraPin), "BeyondTrust PRA certificate pin (SHA-256)", "Integrations", s => s.BeyondTrustPraPin, (s, v) => s.BeyondTrustPraPin = v.Trim().ToUpperInvariant(), PinCheck),
        Text("bt.psPin", nameof(AppSettings.BeyondTrustPsPin), "BeyondTrust Password Safe certificate pin (SHA-256)", "Integrations", s => s.BeyondTrustPsPin, (s, v) => s.BeyondTrustPsPin = v.Trim().ToUpperInvariant(), PinCheck),
        // 0.6.14 (ADR-093): Bitwarden — the integration, the server the master password unlocks against, what vault
        // secrets may be used for, and CLI builds approved although Bitwarden didn't sign them.
        Bool("bw.enabled", nameof(AppSettings.BitwardenEnabled), "Bitwarden integration (Password Manager / Secrets Manager)", "Integrations", s => s.BitwardenEnabled, (s, v) => s.BitwardenEnabled = v, safeValue: false),
        Text("bw.server", nameof(AppSettings.BitwardenServer), "Bitwarden server", "Integrations", s => s.BitwardenServer, (s, v) => s.BitwardenServer = v.Trim(), BitwardenServerCheck),
        Bool("bw.sessions", nameof(AppSettings.BitwardenAllowSessions), "Bitwarden: vault items and secrets may sign in sessions", "Integrations", s => s.BitwardenAllowSessions, (s, v) => s.BitwardenAllowSessions = v, safeValue: false),
        Bool("bw.copy", nameof(AppSettings.BitwardenAllowCopy), "Bitwarden: passwords and secrets may be copied to the clipboard", "Integrations", s => s.BitwardenAllowCopy, (s, v) => s.BitwardenAllowCopy = v, safeValue: false),
        Bool("bw.remember", nameof(AppSettings.BitwardenAllowRemember), "Bitwarden: the master password may be remembered on this PC (encrypted)", "Integrations", s => s.BitwardenAllowRemember, (s, v) => s.BitwardenAllowRemember = v, safeValue: false),
        Text("bw.pins", nameof(AppSettings.BitwardenCliPins), "Bitwarden: approved CLI builds not signed by Bitwarden (SHA-256)", "Integrations", s => s.BitwardenCliPins, (s, v) => s.BitwardenCliPins = v.Trim().ToUpperInvariant(),
            t => t.Length == 0 || t.Split(',').Length <= 10 && t.Split(',').All(p => PinCheck(p.Trim()) == null && p.Trim().Length > 0) ? null : "not up to 10 comma-separated SHA-256 fingerprints"),
        // 0.5.0 (#13): what may be shared to Microsoft Teams, and through which tenant / app registration.
        Bool("teams.enabled", nameof(AppSettings.TeamsEnabled), "Share to Microsoft Teams", "Integrations", s => s.TeamsEnabled, (s, v) => s.TeamsEnabled = v, safeValue: false),
        Bool("teams.images", nameof(AppSettings.TeamsAllowImages), "Teams: screenshots may be shared", "Integrations", s => s.TeamsAllowImages, (s, v) => s.TeamsAllowImages = v, safeValue: false),
        Text("teams.tenant", nameof(AppSettings.TeamsTenant), "Teams: Entra tenant", "Integrations", s => s.TeamsTenant, (s, v) => s.TeamsTenant = v.Trim(), t => t.Length == 0 || System.Text.RegularExpressions.Regex.IsMatch(t, @"^[A-Za-z0-9.\-]{1,253}$") ? null : "not a tenant id or domain"),
        Text("teams.clientId", nameof(AppSettings.TeamsClientId), "Teams: app registration (client id)", "Integrations", s => s.TeamsClientId, (s, v) => s.TeamsClientId = v.Trim(), t => t.Length == 0 || Guid.TryParse(t, out _) ? null : "not a GUID"),
        Bool("cloudpc.actions", nameof(AppSettings.CloudPcAllowActions), "Cloud PCs: restart, rename and admin power actions", "Integrations", s => s.CloudPcAllowActions, (s, v) => s.CloudPcAllowActions = v, safeValue: false),
        Text("m365.tenant", nameof(AppSettings.M365Tenant), "Microsoft 365 admin: Entra tenant", "Integrations", s => s.M365Tenant, (s, v) => s.M365Tenant = v.Trim(), t => t.Length == 0 || System.Text.RegularExpressions.Regex.IsMatch(t, @"^[A-Za-z0-9.\-]{1,253}$") ? null : "not a tenant id or domain"),
        Text("m365.clientId", nameof(AppSettings.M365ClientId), "Microsoft 365 admin: app registration (client id)", "Integrations", s => s.M365ClientId, (s, v) => s.M365ClientId = v.Trim(), t => t.Length == 0 || Guid.TryParse(t, out _) ? null : "not a GUID"),
        Text("cloudpc.tenant", nameof(AppSettings.CloudPcTenant), "Cloud PCs: Entra tenant", "Integrations", s => s.CloudPcTenant, (s, v) => s.CloudPcTenant = v.Trim(), t => t.Length == 0 || System.Text.RegularExpressions.Regex.IsMatch(t, @"^[A-Za-z0-9.\-]{1,253}$") ? null : "not a tenant id or domain"),
        Text("cloudpc.clientId", nameof(AppSettings.CloudPcClientId), "Cloud PCs: app registration (client id)", "Integrations", s => s.CloudPcClientId, (s, v) => s.CloudPcClientId = v.Trim(), t => t.Length == 0 || Guid.TryParse(t, out _) ? null : "not a GUID"),
        // 0.5.0 (ADR-065): an SSH config the IDEs read, written by the app.
        Bool("ide.sshConfig", nameof(AppSettings.IdeSshConfig), "Keep an SSH config (~/.ssh/dcx_config, no secrets) for IDEs", "Integrations", s => s.IdeSshConfig, (s, v) => s.IdeSshConfig = v, safeValue: false),
        // 0.5.0 (ADR-063): Puppet stays read-only until an administrator allows changes.
        Bool("puppet.allowChanges", nameof(AppSettings.PuppetAllowChanges), "Allow Puppet changes (certificates, agent runs, deploys, Bolt)", "Automation", s => s.PuppetAllowChanges, (s, v) => s.PuppetAllowChanges = v, safeValue: false),
        // 0.5.0 (ADR-062): single sign-on into sessions with the Windows account.
        Bool("sso.kerberosSsh", nameof(AppSettings.SsoKerberosSsh), "SSH single sign-on with Kerberos", "Identity", s => s.SsoKerberosSsh, (s, v) => s.SsoKerberosSsh = v, safeValue: false),
        Bool("sso.kerberosDelegate", nameof(AppSettings.SsoKerberosDelegate), "Forward the Kerberos ticket to SSH servers", "Identity", s => s.SsoKerberosDelegate, (s, v) => s.SsoKerberosDelegate = v, safeValue: false),
        Bool("sso.rdp", nameof(AppSettings.SsoRdpAllowed), "Remote Desktop single sign-on with the Windows account", "Identity", s => s.SsoRdpAllowed, (s, v) => s.SsoRdpAllowed = v, safeValue: false),
        // 0.5.0 (ADR-060): what may leave the store, what may come in, and the scheduled backups.
        Bool("backup.allowExport", nameof(AppSettings.BackupAllowExport), "Allow encrypted exports and backups", "Data and backups", s => s.BackupAllowExport, (s, v) => s.BackupAllowExport = v, safeValue: false),
        Bool("backup.allowImport", nameof(AppSettings.BackupAllowImport), "Allow imports (backups and other apps' files)", "Data and backups", s => s.BackupAllowImport, (s, v) => s.BackupAllowImport = v, safeValue: false),
        Bool("backup.allowSecrets", nameof(AppSettings.BackupAllowSecrets), "Exports may carry credentials, keys and cloud API keys", "Data and backups", s => s.BackupAllowSecrets, (s, v) => s.BackupAllowSecrets = v, safeValue: false),
        Int("backup.minPassword", nameof(AppSettings.BackupMinPasswordLength), "Shortest export password", "Data and backups", s => s.BackupMinPasswordLength, (s, v) => s.BackupMinPasswordLength = v, 12, 128, " characters"),
        Int("backup.scheduleDays", nameof(AppSettings.BackupScheduleDays), "Scheduled backups every N days (0 = off)", "Data and backups", s => s.BackupScheduleDays, (s, v) => s.BackupScheduleDays = v, 0, 30, " day(s)"),
        Text("backup.folder", nameof(AppSettings.BackupFolder), "Scheduled backup folder", "Data and backups", s => s.BackupFolder, (s, v) => s.BackupFolder = v.Trim(), PathCheck),
        Bool("backup.scheduledSecrets", nameof(AppSettings.BackupScheduledSecrets), "Scheduled backups include credentials and keys", "Data and backups", s => s.BackupScheduledSecrets, (s, v) => s.BackupScheduledSecrets = v, safeValue: false),
        Bool("data.autoSnapshots",nameof(AppSettings.AutoSnapshots), "Take automatic snapshots", "Data and backups", s => s.AutoSnapshots, (s, v) => s.AutoSnapshots = v, safeValue: true),
        // 0.5.1 (ADR-067): locks and add-ons are an administrator's call too. Locking more, or asking for approval, is stricter.
        Bool("locks.audit", nameof(AppSettings.LockAuditLog), "The Audit log needs an unlock to open", "Locks and add-ons", s => s.LockAuditLog, (s, v) => s.LockAuditLog = v, safeValue: true),
        new("locks.policyMode", "How locked categories open (uac / signin)", "Locks and add-ons",
            s => JsonValue.Create(s.LockPolicyMode), (s, v) => s.LockPolicyMode = v.GetValue<string>(),
            v => v?.GetValueKind() == JsonValueKind.String && v.GetValue<string>() is "uac" or "signin" ? null : "not uac or signin",
            v => v?.GetValueKind() == JsonValueKind.String ? (v.GetValue<string>() == "uac" ? "Windows admin approval" : "Windows sign-in") : "unset",
            (from, to) => to?.GetValueKind() == JsonValueKind.String && to.GetValue<string>() == "uac") { Setting = nameof(AppSettings.LockPolicyMode) },
        Bool("locks.allowUser", nameof(AppSettings.AllowUserLocks), "Users may set their own locks", "Locks and add-ons", s => s.AllowUserLocks, (s, v) => s.AllowUserLocks = v),
        Bool("locks.allowItem", nameof(AppSettings.AllowItemLocks), "Users may lock single items", "Locks and add-ons", s => s.AllowItemLocks, (s, v) => s.AllowItemLocks = v),
        Int("locks.openMinutes", nameof(AppSettings.LockOpenMinutes), "An unlocked lock stays open (minutes, 0 = ask every time)", "Locks and add-ons", s => s.LockOpenMinutes, (s, v) => s.LockOpenMinutes = v, 0, 480, " min"),
        Bool("addons.needApproval", nameof(AppSettings.AddonsNeedApproval), "Installing, updating and removing add-ons needs admin approval", "Locks and add-ons", s => s.AddonsNeedApproval, (s, v) => s.AddonsNeedApproval = v, safeValue: true),
        Bool("addons.allowInstall", nameof(AppSettings.AllowAddonInstall), "Add-ons may be installed", "Locks and add-ons", s => s.AllowAddonInstall, (s, v) => s.AllowAddonInstall = v, safeValue: false),
        Bool("addons.requireSigned", nameof(AppSettings.AddonRequireSigned), "Only add-ons with a signed program", "Locks and add-ons", s => s.AddonRequireSigned, (s, v) => s.AddonRequireSigned = v, safeValue: true),
        // 0.3.0 §4: who gets told, from which server (the password stays in the encrypted settings, never here).
        Bool("smtp.enabled", nameof(AppSettings.SmtpEnabled), "E-mail notifications", "Notifications", s => s.SmtpEnabled, (s, v) => s.SmtpEnabled = v),
        Text("smtp.host", nameof(AppSettings.SmtpHost), "SMTP server", "Notifications", s => s.SmtpHost, (s, v) => s.SmtpHost = v.Trim(), HostCheck),
        Int("smtp.port", nameof(AppSettings.SmtpPort), "SMTP port", "Notifications", s => s.SmtpPort, (s, v) => s.SmtpPort = v, 1, 65535),
        Bool("smtp.implicitTls", nameof(AppSettings.SmtpImplicitTls), "SMTP: TLS from the start (465) instead of STARTTLS", "Notifications", s => s.SmtpImplicitTls, (s, v) => s.SmtpImplicitTls = v),
        Text("smtp.user", nameof(AppSettings.SmtpUser), "SMTP user name", "Notifications", s => s.SmtpUser, (s, v) => s.SmtpUser = v.Trim(), t => t.Any(char.IsControl) ? "contains control characters" : null),
        Text("smtp.from", nameof(AppSettings.SmtpFrom), "Notifications are sent from", "Notifications", s => s.SmtpFrom, (s, v) => s.SmtpFrom = v.Trim(), MailCheck),
        Text("smtp.to", nameof(AppSettings.SmtpTo), "Notifications are sent to", "Notifications", s => s.SmtpTo, (s, v) => s.SmtpTo = v.Trim(), MailCheck),
        Bool("notify.security", nameof(AppSettings.NotifySecurity), "Notify about security events", "Notifications", s => s.NotifySecurity, (s, v) => s.NotifySecurity = v, safeValue: true),
        Bool("notify.syslog", nameof(AppSettings.NotifySyslogFailing), "Notify when syslog or a log sink is failing", "Notifications", s => s.NotifySyslogFailing, (s, v) => s.NotifySyslogFailing = v, safeValue: true),
    };

    public static Item? Find(string key) => Items.FirstOrDefault(i => i.Key == key);

    // ---- the record ---------------------------------------------------------------------------------------------------

    /// <summary>The stored record for one Windows user: the values, and who changed what when (the last 20 changes).</summary>
    public sealed class Record
    {
        public int V { get; set; } = 1;
        public Dictionary<string, JsonNode?> Values { get; set; } = new();
        public List<HistoryEntry> History { get; set; } = new();
    }
    public sealed record HistoryEntry(DateTime Utc, string By, string Keys);

    /// <summary>Where records live: the registry for real, a scratch file in the self-test.</summary>
    public interface IStore
    {
        string? Read(string sid);
        void Write(string sid, string json);
        /// <summary>Null when only administrators can write the record; else what is wrong (it is then ignored).</summary>
        string? TrustProblem();
        string Describe { get; }
    }

    /// <summary>The store in use (the self-test swaps in a file store; nothing else may).</summary>
    public static IStore Store { get; internal set; } = new RegistryStore();

    public static string CurrentSid => WindowsIdentity.GetCurrent().User?.Value ?? "";

    private static Record? _record;
    private static string? _problem;

    /// <summary>This user's record (null = nothing protected yet, or the store isn't trustworthy).</summary>
    public static Record? Current => _record;
    /// <summary>Why the store was ignored (a key others could write, an unreadable record), or null.</summary>
    public static string? Problem => _problem;
    public static bool Active => _record != null;
    public static bool IsProtected(string key) => _record?.Values.ContainsKey(key) == true;

    /// <summary>Reads this user's record again. Never throws: a broken or untrusted record counts as none (and says why).</summary>
    public static void Reload()
    {
        _record = null; _problem = null;
        try
        {
            if (Store.Read(CurrentSid) is not { } json) return;
            if (Store.TrustProblem() is { } p) { _problem = p; return; }
            var r = JsonSerializer.Deserialize<Record>(json, SecureStore.Json) ?? throw new InvalidDataException("empty record");
            // Only known keys with valid values count; anything else is dropped (and reported).
            var bad = r.Values.Where(kv => Find(kv.Key) is not { } it || it.Check(kv.Value) != null).Select(kv => kv.Key).ToList();
            foreach (var k in bad) r.Values.Remove(k);
            if (bad.Count > 0) _problem = "ignored invalid protected value(s): " + string.Join(", ", bad);
            _record = r;
        }
        catch (Exception ex) { _problem = "the protected settings could not be read: " + ex.Message; Log.Write("Protected settings", ex); }
    }

    /// <summary>Puts the protected values over <paramref name="s"/> (start-up, after a restore or a protected change).
    /// Returns the keys whose value it changed.</summary>
    public static List<string> ApplyTo(AppSettings s)
    {
        var changed = new List<string>();
        if (_record == null) return changed;
        foreach (var (key, value) in _record.Values)
        {
            if (Find(key) is not { } it || value == null) continue;
            if (JsonNode.DeepEquals(it.Get(s), value)) continue;
            it.Set(s, value);
            changed.Add(key);
        }
        return changed;
    }

    // ---- changing ------------------------------------------------------------------------------------------------------

    /// <summary>The app's gate (AdminGate: Windows Hello if asked for, then UAC and the admin window). Set at start-up;
    /// without it (tools) a change goes straight to the user's settings.</summary>
    public static Func<string, IReadOnlyDictionary<string, JsonNode?>, Task<bool>>? Gate { get; set; }

    /// <summary>Changes protected settings the only way the app may: through the admin gate. True when they changed.</summary>
    public static Task<bool> ChangeAsync(string reason, IReadOnlyDictionary<string, JsonNode?> changes) =>
        Gate is { } g ? g(reason, changes) : Task.FromResult(ApplyDirect(changes));

    public static Task<bool> ChangeAsync(string reason, string key, JsonNode? value) =>
        ChangeAsync(reason, new Dictionary<string, JsonNode?> { [key] = value });

    /// <summary>Writes values to the user's own settings (no protection: before any record exists on a copy that can't
    /// elevate, or for a stricter value for this session). Returns true.</summary>
    public static bool ApplyDirect(IReadOnlyDictionary<string, JsonNode?> changes)
    {
        var s = AppSettings.Current;
        foreach (var (k, v) in changes) if (Find(k) is { } it && v != null && it.Check(v) == null) it.Set(s, v);
        AppSettings.Save();
        return true;
    }

    // ---- requests -----------------------------------------------------------------------------------------------------

    /// <summary>A change asked for by the (unelevated) app: the new values, for whom, when, and a one-time id.</summary>
    public sealed class Request
    {
        public string Format { get; set; } = RequestFormat;
        public int Version { get; set; } = RequestVersion;
        public string Id { get; set; } = "";
        public string Sid { get; set; } = "";
        public string User { get; set; } = "";
        /// <summary>The requesting profile's name (0.5.7, <see cref="AppIdentity"/>): what the history shows when the same
        /// Windows account approves. Display only: the account (<see cref="Sid"/>) is what is checked.</summary>
        public string Name { get; set; } = "";
        public DateTime RequestedUtc { get; set; }
        public string Reason { get; set; } = "";
        public Dictionary<string, JsonNode?> Values { get; set; } = new();
    }

    /// <summary>Who approved <paramref name="plan"/>, as the history records it (the elevated side): the requesting
    /// profile's name when the approving Windows account is the one that asked; otherwise — another administrator — that
    /// administrator's Windows account, because who approved is the fact.</summary>
    public static string Approver(Plan plan)
    {
        var me = WindowsIdentity.GetCurrent();
        bool same = string.Equals(me.User?.Value, plan.Request.Sid, StringComparison.OrdinalIgnoreCase);
        return same && AppIdentity.Clean(plan.Request.Name) is { Length: > 0 } n ? n : me.Name;
    }

    /// <summary>Builds a request. The first one (nothing protected yet) carries every protected setting's current value,
    /// so after it everything is protected, not just the one changed. Throws on an unknown key or a bad value.</summary>
    public static Request BuildRequest(AppSettings current, IReadOnlyDictionary<string, JsonNode?> changes, string reason)
    {
        var values = new Dictionary<string, JsonNode?>();
        if (_record == null) foreach (var it in Items) values[it.Key] = it.Get(current);
        foreach (var (k, v) in changes)
        {
            var it = Find(k) ?? throw new ArgumentException("not a protected setting: " + k);
            if (it.Check(v) is { } why) throw new ArgumentException($"{it.Title}: {why}");
            values[k] = v?.DeepClone();
        }
        return new Request
        {
            Id = Guid.NewGuid().ToString("N"), Sid = CurrentSid, User = WindowsIdentity.GetCurrent().Name, Name = AppIdentity.Name,
            RequestedUtc = DateTime.UtcNow, Reason = reason.Length > 200 ? reason[..200] : reason, Values = values,
        };
    }

    /// <summary>What a request would change, line by line (shown to the administrator before anything is written).</summary>
    public sealed record Line(string Key, string Title, string Group, string From, string To);
    public sealed record Plan(Request Request, string ForUser, List<Line> Lines);

    /// <summary>Reads and checks a request file (the elevated side). Throws <see cref="InvalidDataException"/> on anything
    /// that isn't a fresh, well-formed request for known settings with valid values.</summary>
    public static Plan ReadRequest(string path, Func<DateTime>? utcNow = null)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists) throw new InvalidDataException("the request file is missing");
        if (fi.Length > MaxRequestBytes) throw new InvalidDataException("the request is too large");
        Request r;
        try { r = JsonSerializer.Deserialize<Request>(File.ReadAllBytes(path), SecureStore.Json) ?? throw new InvalidDataException("empty request"); }
        catch (JsonException) { throw new InvalidDataException("the request is not valid JSON"); }
        if (r.Format != RequestFormat || r.Version != RequestVersion) throw new InvalidDataException("not a protected-settings request this version understands");
        var now = (utcNow ?? (() => DateTime.UtcNow))();
        if (r.RequestedUtc > now + TimeSpan.FromMinutes(1) || now - r.RequestedUtc > RequestLifetime) throw new InvalidDataException("the request has expired (ask again from the app)");
        SecurityIdentifier sid;
        try { sid = new SecurityIdentifier(r.Sid); } catch { throw new InvalidDataException("the request names no valid Windows user"); }
        if (r.Values.Count == 0 || r.Values.Count > Items.Count) throw new InvalidDataException("the request changes nothing");
        string forUser;
        try { forUser = sid.Translate(typeof(NTAccount)).Value; } catch { forUser = r.User.Length > 0 ? r.User + " (" + r.Sid + ")" : r.Sid; }

        Record? existing = null;
        try { if (Store.Read(r.Sid) is { } json) existing = JsonSerializer.Deserialize<Record>(json, SecureStore.Json); } catch { existing = null; }
        var lines = new List<Line>();
        foreach (var (k, v) in r.Values)
        {
            var it = Find(k) ?? throw new InvalidDataException("unknown setting: " + k);
            if (it.Check(v) is { } why) throw new InvalidDataException($"{it.Title}: {why}");
            JsonNode? before = existing != null && existing.Values.TryGetValue(k, out var b) ? b : null;
            if (existing != null && JsonNode.DeepEquals(before, v)) continue;
            lines.Add(new Line(k, it.Title, it.Group, existing == null || before == null ? "not protected" : it.Show(before), it.Show(v)));
        }
        return new Plan(r, forUser, lines);
    }

    /// <summary>Writes an approved plan (the elevated side, or the self-test's fake). Returns the new record.</summary>
    public static Record Commit(Plan plan, string approvedBy)
    {
        Record rec;
        try { rec = Store.Read(plan.Request.Sid) is { } json ? JsonSerializer.Deserialize<Record>(json, SecureStore.Json) ?? new() : new(); }
        catch { rec = new(); }
        foreach (var (k, v) in plan.Request.Values) rec.Values[k] = v?.DeepClone();
        rec.History.Insert(0, new HistoryEntry(DateTime.UtcNow, approvedBy, plan.Lines.Count == 0 ? "(no change)" : string.Join(", ", plan.Lines.Select(l => l.Key))));
        if (rec.History.Count > 20) rec.History.RemoveRange(20, rec.History.Count - 20);
        Store.Write(plan.Request.Sid, JsonSerializer.Serialize(rec, SecureStore.Json));
        return rec;
    }

    /// <summary>Writes a request file for the elevated helper, in the user's temp folder (deleted after use).</summary>
    public static string WriteRequestFile(Request r)
    {
        var dir = Path.Combine(Path.GetTempPath(), AppInfo.RequestsTempFolder);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, r.Id + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(r, SecureStore.Json));
        return path;
    }

    // ---- the registry store -------------------------------------------------------------------------------------------

    /// <summary>HKLM, readable by everyone, writable by administrators and SYSTEM only (set explicitly when it is created).</summary>
    public sealed class RegistryStore : IStore
    {
        public string Describe => @"HKLM\" + MachineKey;

        public string? Read(string sid)
        {
            using var k = Registry.LocalMachine.OpenSubKey(MachineKey);
            return k?.GetValue(sid) as string;
        }

        public void Write(string sid, string json)
        {
            using var k = Registry.LocalMachine.CreateSubKey(MachineKey, writable: true) ?? throw new IOException("the protected settings key could not be created");
            Lockdown(k);
            k.SetValue(sid, json, RegistryValueKind.String);
        }

        public string? TrustProblem()
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(MachineKey);
                if (k == null) return null;
                var acl = k.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
                return AclProblem(acl.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier,
                    acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<RegistryAccessRule>()
                       .Where(r => r.AccessControlType == AccessControlType.Allow)
                       .Select(r => ((SecurityIdentifier)r.IdentityReference, r.RegistryRights)));
            }
            catch (Exception ex) { return "its permissions could not be read: " + ex.Message; }
        }

        private static void Lockdown(RegistryKey k)
        {
            var acl = new RegistrySecurity();
            acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            acl.SetOwner(admins);
            var inherit = InheritanceFlags.ContainerInherit;
            acl.AddAccessRule(new RegistryAccessRule(admins, RegistryRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            acl.AddAccessRule(new RegistryAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), RegistryRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            acl.AddAccessRule(new RegistryAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), RegistryRights.ReadKey, inherit, PropagationFlags.None, AccessControlType.Allow));
            k.SetAccessControl(acl);
        }
    }

    // The bits that change something (not WriteKey / FullControl as masks: they include READ_CONTROL, which every reader has).
    private const RegistryRights WriteRights = RegistryRights.SetValue | RegistryRights.CreateSubKey | RegistryRights.CreateLink |
        RegistryRights.Delete | RegistryRights.ChangePermissions | RegistryRights.TakeOwnership;

    /// <summary>The trust rule, without the registry (the self-test checks it on made-up ACLs): the owner and everyone
    /// who may write must be administrators, SYSTEM or TrustedInstaller.</summary>
    internal static string? AclProblem(SecurityIdentifier? owner, IEnumerable<(SecurityIdentifier Who, RegistryRights Rights)> allows)
    {
        static bool Admin(SecurityIdentifier s) => s.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) || s.IsWellKnown(WellKnownSidType.LocalSystemSid)
            || s.Value.StartsWith("S-1-5-80-956008885-", StringComparison.Ordinal);   // TrustedInstaller
        if (owner == null || !Admin(owner)) return $"its owner ({owner?.Value ?? "unknown"}) is not an administrator";
        foreach (var (who, rights) in allows)
            if ((rights & WriteRights) != 0 && !Admin(who) && !who.IsWellKnown(WellKnownSidType.CreatorOwnerSid))
                return $"{Name(who)} can change it";
        return null;
    }

    private static string Name(SecurityIdentifier s) { try { return s.Translate(typeof(NTAccount)).Value; } catch { return s.Value; } }

    // ---- the self-test's store ----------------------------------------------------------------------------------------

    /// <summary>A plain file per user in a scratch folder, with a switch to pretend the ACL is loose (self-test only).</summary>
    internal sealed class FileStore(string dir) : IStore
    {
        public string? LooseAcl { get; set; }
        public string Describe => dir;
        public string? Read(string sid) { var p = Path.Combine(dir, sid + ".json"); return File.Exists(p) ? File.ReadAllText(p) : null; }
        public void Write(string sid, string json) { Directory.CreateDirectory(dir); File.WriteAllText(Path.Combine(dir, sid + ".json"), json); }
        public string? TrustProblem() => LooseAcl;
    }

    internal static void UseStoreForTest(IStore store) { Store = store; Reload(); }
}
