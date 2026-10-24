using DcxManager.Ssh;
using Contribution = DcxManager.Services.PrivacyContribution;
using Endpoint = DcxManager.Services.PrivacyEndpoint;
using Level = DcxManager.Services.PrivacyLevel;
using Posture = DcxManager.Services.PrivacyPosture;

namespace DcxManager.Services;

/// <summary>
/// The facts behind Privacy &amp; network: security posture, every destination the app is configured to reach (with its
/// <see cref="NetClass"/>), and what is listening. Built from live state, never from assumptions. Features that don't
/// exist are reported as NOT IMPLEMENTED rather than "disabled". No UI here (Views/PrivacyView.cs presents it).
/// </summary>
public static class PrivacyReport
{
    // The row types (PrivacyLevel, PrivacyPosture, PrivacyEndpoint) and the modules' rows (PrivacyRows) live with the module
    // contracts in the UI kit since 0.2.0 phase 6f (Dcx.Ui, ModuleContributions.cs).
    public sealed record Port(string Service, string Bind, string Reach, Level Level, string State);
    public static IReadOnlyList<Contribution> Contributions => PrivacyRows.All;

    private static IEnumerable<T> Rows<T>(Func<Contribution, IEnumerable<T>> pick)
    {
        var all = new List<T>();
        foreach (var c in PrivacyRows.All)
        {
            try { all.AddRange(pick(c)); }
            catch (Exception ex) { Log.Write($"Privacy report rows ({c.Module})", ex); }
        }
        return all;
    }

    /// <summary>" · dashboard checks …" when the dashboard's check rule (ADR-018) contacts source <paramref name="id"/>
    /// without a click, else "" — the only thing that contacts sources without a click.</summary>
    public static string DashboardNote(string id)
    {
        var s = AppSettings.Current;
        return s.DashboardChecks != "manual" && s.DashboardAutoSources.Contains(id) ? " · dashboard checks " + Overview.ModeText(s.DashboardChecks, s.DashboardCheckMinutes) : "";
    }

    public static List<Posture> PostureItems()
    {
        var s = AppSettings.Current;
        var list = new List<Posture>
        {
            new("Offline mode", Offline.On ? $"ON — only this PC, your network and {s.OfflineAllowed.Count} approved destination(s) · {Offline.BlockedTotal} blocked this session" : "off",
                Offline.On ? Level.Good : Level.Info, Offline.On ? "Checked before every connection; blocks are listed in Settings → Offline mode and audited." : "Settings → Offline mode blocks everything outside your network except what you approve."),
            new("Store encryption", "AES-256-GCM · always on", Level.Good, "Every file in the data folder; there is no plaintext mode."),
            new("Key protection", SecureStore.PasswordRequiredOnStart ? "master password (required at start)" : SecureStore.HasPassword ? "Windows account (DPAPI) + master password" : "Windows account (DPAPI)",
                SecureStore.PasswordRequiredOnStart ? Level.Good : Level.Info, SecureStore.PasswordRequiredOnStart ? null : "Anyone signed in to Windows as you can open the app."),
            new("Two-factor (MFA)", SecureStore.HasTotp ? "ENABLED" : "DISABLED", SecureStore.HasTotp ? Level.Good : Level.Info),
            new("App lock", s.AutoLockMinutes > 0 ? $"after {s.AutoLockMinutes} min idle" + (s.LockOnWindowsLock ? " · with Windows" : "") : s.LockOnWindowsLock ? "with Windows lock / sleep" : "manual only",
                s.AutoLockMinutes > 0 ? Level.Good : Level.Info, AppLock.CanLock ? null : "Needs a master password or 2FA to lock."),
            new("Sandbox mode", s.SandboxMode ? "ON" : "off", s.SandboxMode ? Level.Good : Level.Info),
            // 0.2.0 phase 5: the AI module is loaded or not at all (Settings → Security & privacy → AI features).
            new("AI features", (AiFeatures.Current.On ? "ON — module loaded" : "OFF — module not loaded")
                    + (AiFeatures.Current.Source == AiFeatures.From.Policy ? " (set by your administrator)" : AiFeatures.Current.Source == AiFeatures.From.Environment ? $" ({AiFeatures.EnvVar})" : "")
                    + (AiFeatures.Current.Locked ? " · locked" : ""),
                AiFeatures.Current.On ? Level.Info : Level.Good,
                AiFeatures.Current.On ? "AI engines, actions and background work exist; what they may send is listed below." : "No AI code runs: no AI screens, model downloads, engine checks or connections."),
        };
        var store = SyncDetector.Detect(SecureStore.DataDir);
        list.Add(new("Data location", store.Kind switch
        {
            StorageKind.Local => "local disk, not synchronised",
            StorageKind.Synced => $"⚠ synchronised by {store.Provider}",
            StorageKind.Network => $"⚠ network location ({store.Provider})",
            _ => "could not be determined",
        }, store.Kind == StorageKind.Local ? Level.Good : Level.Warn, "Files are encrypted either way; a synced folder copies them to other devices."));
        list.AddRange(Rows(c => c.Posture()));   // the modules' rows (AI: external providers, secret masking)
        list.Add(new("Paste protection", s.TerminalConfirmPaste ? "confirm multi-line / risky" : "control characters stripped only", s.TerminalConfirmPaste ? Level.Good : Level.Info));
        list.Add(new("Telemetry & analytics", "NONE", Level.Good, "Not built into the app: no usage data, crash uploads or remote feature flags."));
        if (!Updater.Enabled)
            list.Add(new("Update checks", "DISABLED", Level.Good, "No release key is pinned in this build; the app never contacts the update service."));
        else
            list.Add(new("Update checks", s.AutoCheckUpdates ? $"AUTOMATIC {s.UpdateCheckInterval} (opt-in)" + (s.AutoDownloadUpdates ? ", downloads in the background" : "") : "MANUAL (you click Check)", s.AutoCheckUpdates ? Level.Warn : Level.Good,
                (Updater.FromGitHub ? "From the GitHub releases. " : "") + "Plain download of the update information; no identifiers sent. Installs need the DarkCipher release key's signature; installing always asks."));
        list.Add(new("Cloud sync", "NONE", Level.Good, "The app has no sync service of its own."));
        int tokens = s.ApiTokens.Count(t => t.Valid);
        list.Add(new("Local API", s.LocalApiEnabled ? $"ON — 127.0.0.1:{s.LocalApiPort} only, {tokens} valid token(s)" : "OFF", s.LocalApiEnabled ? Level.Info : Level.Good,
            "Loopback only (never the network); scoped, expiring, revocable tokens; no access to credentials, terminals or files."));
        int casts = Api.Broadcasts.All.Count;
        list.Add(new("Broadcast (OBS)", casts > 0 ? $"BROADCASTING {casts}" : "none active", casts > 0 ? Level.Warn : Level.Good,
            "Text only, redacted, 127.0.0.1 only, expires; stopped on lock."));
        var addons = s.Addons;
        list.Add(new("Add-ons", addons.Count == 0 ? "none installed" : $"{addons.Count(a => a.Enabled)} enabled of {addons.Count}", addons.Any(a => a.Enabled && a.Granted.Contains("credentials.request")) ? Level.Warn : addons.Count > 0 ? Level.Info : Level.Good,
            "Out of process, Low-integrity sandbox, only granted permissions, files verified before each run; nothing loads into the app."));
        if (Browser.PortalBrowser.Enabled || Browser.Portals.All.Count > 0)
            list.Add(new("Portal browser", Browser.PortalBrowser.Enabled ? $"ON — {Browser.Portals.All.Count} portal(s), {AppData.Current.WebPins.Count} pinned certificate(s)" : "off (Settings → Integrations)", Level.Info,
                "Passwords and form autofill are never saved; SmartScreen look-ups are off; cookies live per environment profile in the WebView2 data folder (Windows DPAPI), not in the app's store; downloads ask first."));
        var ports = Ports();
        int live = ports.Count(p => p.State == "running");
        list.Add(new("Listening services", live == 0 ? "none" : $"{live} active", ports.Any(p => p.State == "running" && p.Level == Level.Warn) ? Level.Warn : Level.Good));
        return list;
    }

    public static List<Endpoint> Endpoints()
    {
        var s = AppSettings.Current;
        var list = new List<Endpoint>();

        // The modules' destinations first (AI: the engine, Foundry Local's own set-up).
        list.AddRange(Rows(c => c.Endpoints()));

        // Update service (vendor communication — only when you click Check, or weekly if you opted in)
        list.Add(new("Update service", Updater.Endpoint, NetworkPolicy.Classify(Updater.Endpoint),
            !Updater.Enabled ? (Packaging.IsPackaged ? "DISABLED: Microsoft Store install, the Store handles updates; not contacted" : "DISABLED in this build (no signed releases yet): not contacted") :
            (s.AutoCheckUpdates ? $"{s.UpdateCheckInterval} automatic check (opt-in)" : "only when you click Check for updates") + (s.LastUpdateCheck > DateTime.MinValue ? $" · last {s.LastUpdateCheck:yyyy-MM-dd HH:mm}" : " · never contacted"),
            "Update information + the installer; sends no identifiers"));

        // Syslog over TLS and e-mail notifications (0.3.0 §3/§4): your own servers, only when set up.
        var sys = Notify.SyslogForwarder.Status;
        list.Add(new("Syslog (TLS)", s.SyslogHost.Length > 0 ? $"{s.SyslogHost}:{s.SyslogPort}" : "—", s.SyslogHost.Length > 0 ? NetworkPolicy.ClassifyHost(s.SyslogHost) : null,
            !s.SyslogEnabled ? "off · nothing sent" : $"{sys.State}" + (sys.Queued > 0 ? $" · {sys.Queued} queued" : ""),
            "The audit log and error log (masked), RFC 5424 over TLS only"));
        list.Add(new("E-mail notifications (SMTP)", s.SmtpHost.Length > 0 ? $"{s.SmtpHost}:{s.SmtpPort}" : "—", s.SmtpHost.Length > 0 ? NetworkPolicy.ClassifyHost(s.SmtpHost) : null,
            !s.SmtpEnabled ? "off · nothing sent" : "only when an event you chose happens" + (Notify.Notifier.LastError is { } ne ? " · last error: " + ne : ""),
            "Short event messages (masked) to the addresses you set, over TLS only"));

        // Vultr
        int accounts = s.VultrAccounts.Count;
        static string Dash(string id) => DashboardNote(id);
        list.Add(new("Vultr API", "api.vultr.com", NetClass.External, accounts == 0 ? "not configured · nothing sent" : $"{accounts} account(s) · on demand" + Dash("vultr"),
            "Cloud inventory and actions you open; no background polling unless you choose a dashboard interval"));
        list.Add(new("Vultr portal / web console", "my.vultr.com", NetClass.External, "only when you open it", "Vultr's own site in an app window"));

        // Contabo (ADR-020): sign-in and API, only with an enabled account.
        int contabo = s.ContaboAccounts.Count(a => !a.Disabled);
        list.Add(new("Contabo API", "api.contabo.com", NetClass.External, contabo == 0 ? "not configured · nothing sent" : $"{contabo} account(s) · on demand" + Dash("contabo"),
            "Instances, images, secrets, networks, firewalls and DNS you open; no background polling unless you choose a dashboard interval"));
        list.Add(new("Contabo sign-in", "auth.contabo.com", NetClass.External, contabo == 0 ? "not configured · nothing sent" : "before Contabo API calls (token kept in memory only)",
            "OAuth2 sign-in with your client id/secret and API user/password"));

        // Hetzner Cloud (ADR-028): API only, with an enabled project.
        int hetzner = s.HetznerAccounts.Count(a => !a.Disabled);
        list.Add(new("Hetzner Cloud API", "api.hetzner.cloud", NetClass.External, hetzner == 0 ? "not configured · nothing sent" : $"{hetzner} project(s) · on demand" + Dash("hetzner"),
            "Servers, volumes, networks, firewalls, load balancers, images and DNS you open; the project token as a Bearer header; no background polling unless you choose a dashboard interval"));

        // Microsoft Azure (0.2.0): Entra sign-in with the certificate, then Resource Manager, only with an enabled subscription.
        int azure = s.AzureAccounts.Count(a => !a.Disabled);
        list.Add(new("Azure Resource Manager", "management.azure.com", NetClass.External, azure == 0 ? "not configured · nothing sent" : $"{azure} subscription(s) · on demand" + Dash("azure"),
            "VMs, AKS, App Service, SQL, storage and network settings, DNS and resources you open; never secrets or keys; no background polling unless you choose a dashboard interval"));
        list.Add(new("Microsoft Entra sign-in", "login.microsoftonline.com", NetClass.External, azure == 0 ? "not configured · nothing sent" : "before Azure calls (token kept in memory only)",
            "Client-credentials sign-in with a short-lived assertion signed by your certificate; the certificate itself never leaves this PC"));

        static string HostOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "—";
        // 0.5.0: Microsoft Teams, Pingdom and BeyondTrust — each only when switched on and configured.
        list.Add(new("Microsoft Teams (Graph)", "graph.microsoft.com, login.microsoftonline.com", NetClass.External, !s.TeamsEnabled ? "off · nothing sent" : "only when you share (preview first)",
            "What you choose to share: a title, masked text and optionally a screenshot, posted as you to a channel or chat; the refresh token is kept encrypted, access tokens in memory"));
        list.Add(new("Microsoft Teams (webhook)", "*.logic.azure.com / *.powerplatform.com", NetClass.External, !s.TeamsEnabled || Notify.TeamsShare.Destinations().All(d => d.Kind != "webhook") ? "none · nothing sent" : "only when you share (preview first)",
            "Masked text as an Adaptive Card to a Teams Workflows address you added (kept encrypted); never images"));
        list.Add(new("Pingdom API", "api.pingdom.com", NetClass.External, s.ObserveSecrets.ContainsKey(Notify.PingdomClient.SecretKey) ? $"{AppData.Current.DashboardTiles.Count(t => t.Kind == "pingdom")} pinned check(s) · dashboard schedule" : "not configured · nothing sent",
            "Read-only GET of your checks' state with a read-only token; nothing about your hosts is sent"));
        list.Add(new("BeyondTrust PRA / Remote Support", HostOf(s.BeyondTrustPraSite), HostOf(s.BeyondTrustPraSite) is "—" ? null : NetworkPolicy.ClassifyHost(HostOf(s.BeyondTrustPraSite)), s.BeyondTrustEnabled && s.BeyondTrustPraSite.Length > 0 ? "on demand (read-only)" : "off · nothing sent",
            "Jump Items, Jump Groups, Jumpoints, Vault account names and the access-session report with your API account; nothing is changed there"));
        list.Add(new("BeyondTrust Password Safe", HostOf(s.BeyondTrustPsHost), HostOf(s.BeyondTrustPsHost) is "—" ? null : NetworkPolicy.ClassifyHost(HostOf(s.BeyondTrustPsHost)), s.BeyondTrustEnabled && s.BeyondTrustAllowCheckout && s.BeyondTrustPsHost.Length > 0 ? "when you connect a linked host" : "off · nothing sent",
            "Checks a managed account out for one session and back in; the password lives in memory for that session only"));

        // Google Cloud (0.2.0): service-account sign-in, then the allow-listed APIs, only with an enabled project.
        int gcp = s.GcpAccounts.Count(a => !a.Disabled);
        list.Add(new("Google Cloud APIs", "compute, container, run, sqladmin, dns, storage, cloudresourcemanager .googleapis.com", NetClass.External, gcp == 0 ? "not configured · nothing sent" : $"{gcp} project(s) · on demand" + Dash("gcp"),
            "VMs, GKE, Cloud Run, Cloud SQL, buckets (settings only), networks, firewalls and DNS you open; no background polling unless you choose a dashboard interval"));
        list.Add(new("Google sign-in", "oauth2.googleapis.com", NetClass.External, gcp == 0 ? "not configured · nothing sent" : "before Google Cloud calls (token kept in memory only)",
            "Service-account sign-in with a short-lived grant signed by the key; read-only scope while the project is read-only; the key itself never leaves this PC"));

        // Infrastructure providers: only when you open the Infrastructure screen (or its auto-refresh, which you turn on).
        foreach (var ip in AppData.Current.InfraProviders)
        {
            NetClass? cls; string dest;
            if (ip.Kind == "docker-ssh") { var h = AppData.Current.FindHost(ip.HostId); dest = h != null ? "ssh " + h.Endpoint : "(missing host)"; cls = h != null ? NetworkPolicy.ClassifyHost(h.Address) : null; }
            else if (ip.Kind == "docker-local") { dest = "local pipe (Docker Desktop)"; cls = NetClass.Local; }
            else if (ip.Kind == "hyperv") { var h = ip.Endpoint.Trim(); dest = h is "" or "." or "localhost" ? "this PC (Hyper-V)" : "WinRM " + h; cls = h is "" or "." or "localhost" ? NetClass.Local : NetworkPolicy.ClassifyHost(h); }
            else { dest = ip.Endpoint; cls = NetworkPolicy.Classify(ip.Endpoint); }
            list.Add(new($"{Infra.Infra.KindTitle(ip.Kind)} · {ip.Name}", dest, cls,
                (ip.RefreshSeconds > 0 ? $"auto-refresh every {ip.RefreshSeconds}s while Infrastructure is open" : "on demand") + Dash("infra:" + ip.Id) + (ip.AllowChanges ? " · changes allowed" : " · read-only"),
                "Inventory you open; changes only when allowed and confirmed"));
        }

        // Saved hosts: operational traffic, summarised by class (connections you open yourself, not telemetry).
        foreach (var g in AppData.Current.Hosts.GroupBy(h => NetworkPolicy.ClassifyHost(h.Address)).OrderBy(g => g.Key))
            list.Add(new("Saved hosts (SSH / SFTP / RDP)", $"{g.Count()} host(s)", g.Key, "when you connect", "Operational traffic you start: terminals, files, Remote Desktop, tunnels"));
        // Portal browser (ADR-032): saved portals by class; only the pages you open, no SmartScreen look-ups.
        foreach (var g in Browser.Portals.All.GroupBy(b => NetworkPolicy.ClassifyHost(Offline.HostOf(b.Url))).OrderBy(g => g.Key))
            list.Add(new("Portal browser · saved portals", $"{g.Count()} portal(s)", g.Key, Browser.PortalBrowser.Enabled ? "when you open one" : "switched off · nothing opened",
                "Web UIs you open in a tab, and whatever those pages load"));
        return Offline.On ? list.Select(OfflineState).ToList() : list;
    }

    /// <summary>What offline mode does to an endpoint (ADR-016), decided without contacting anything.</summary>
    private static Endpoint OfflineState(Endpoint e)
    {
        if (e.Class is null or NetClass.Local or NetClass.OnPremises) return e;
        if (e.Service.StartsWith("Saved hosts"))
            return e with { State = e.Class == NetClass.External ? "BLOCKED by offline mode unless approved" : "allowed only if approved or resolving to private addresses" };
        var host = Offline.HostOf(e.Destination.Split(' ')[0]);
        return Offline.Quick(host).Allowed switch
        {
            false => e with { State = "BLOCKED by offline mode" },
            null => e with { State = "allowed only if it resolves to private addresses · " + e.State },
            _ => e with { State = "approved · " + e.State },
        };
    }

    public static List<Port> Ports()
    {
        var list = new List<Port>();
        var running = TunnelManager.Listeners();
        foreach (var t in AppData.Current.Tunnels)
        {
            var live = running.FirstOrDefault(l => l.Key == t.Id);
            if (t.Kind == TunnelKind.Remote)
            {
                var host = AppData.Current.FindHost(t.HostId)?.Display ?? "server";
                list.Add(new($"Tunnel “{t.Name}” (remote -R)", $"{t.BindAddress}:{t.BindPort} on {host}", "listens on the server, not on this PC", Level.Info, live != null ? "running" : "stopped"));
                continue;
            }
            var (reach, level) = Reach(t.BindAddress);
            list.Add(new($"Tunnel “{t.Name}” ({(t.Kind == TunnelKind.Dynamic ? "SOCKS5" : "local -L")})", $"{t.BindAddress}:{(live?.Port ?? t.BindPort)}", reach, level, live != null ? "running" : "stopped"));
        }
        foreach (var l in running.Where(l => AppData.Current.Tunnels.All(t => t.Id != l.Key)))
        {
            var (reach, level) = Reach(l.BindHost);
            list.Add(new(l.Key == "ollama-tunnel" ? "Ollama over SSH (internal)" : $"Internal {l.Kind}", $"{l.BindHost}:{l.Port}", reach, level, "running"));
        }
        if (AppSettings.Current.LocalApiEnabled || Api.LocalServer.Running)
            list.Add(new("Local API / broadcast listener", $"127.0.0.1:{(Api.LocalServer.Running ? Api.LocalServer.Port : AppSettings.Current.LocalApiPort)}", "this PC only (loopback)", Level.Good,
                Api.LocalServer.Running ? "running" : "stopped"));
        int relays = SshConnector.ActiveRelays;
        if (relays > 0) list.Add(new("Jump-host relays (internal)", $"127.0.0.1 × {relays}", "this PC only, one per hopped session", Level.Good, "running"));
        return list;
    }

    /// <summary>Who can reach a local bind address.</summary>
    public static (string Reach, Level Level) Reach(string bind)
    {
        var b = (bind ?? "").Trim().Trim('[', ']');
        if (b is "" or "0.0.0.0" or "::" or "*") return ("⚠ ALL interfaces: reachable from your network", Level.Warn);
        if (b is "127.0.0.1" or "::1" or "localhost" || b.StartsWith("127.")) return ("this PC only (loopback)", Level.Good);
        return (NetworkPolicy.ClassifyHost(b) == NetClass.External ? $"⚠ public interface {b}" : $"interface {b} (your network)", Level.Warn);
    }
}
