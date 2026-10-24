using System.Text.Json;
using DcxManager.Reports;

namespace DcxManager.Services;

/// <summary>
/// Item-by-item encrypted export and import (0.3.0 §11): the user ticks exactly which hosts, credentials, cloud accounts,
/// notes, incidents, snippets, tunnels, serial consoles, infrastructure providers and portals go into a file, and on
/// import ticks which of the file's items come in. Same <see cref="Backup.BackupFile"/> format and encryption as the
/// other exports, so <see cref="Backup.Read"/>, <see cref="Backup.Merge"/> and <see cref="Backup.ReplaceAll"/> work
/// unchanged. Dependencies are kept honest: a picked host brings its jump hosts; a credential that isn't picked is
/// removed from everything that used it (and the summary says so).
/// </summary>
public static partial class Backup
{
    /// <summary>Ids of the picked items per kind, plus the all-or-nothing groups.</summary>
    public sealed class Choice
    {
        public HashSet<string> Hosts { get; } = new();
        public HashSet<string> Credentials { get; } = new();
        public HashSet<string> Snippets { get; } = new();
        public HashSet<string> Tunnels { get; } = new();
        public HashSet<string> Serial { get; } = new();
        public HashSet<string> Infra { get; } = new();
        public HashSet<string> Portals { get; } = new();
        public HashSet<string> Notes { get; } = new();
        public HashSet<string> Incidents { get; } = new();
        /// <summary>Cloud accounts by provider id ("vultr", "contabo", "hetzner", "azure", "gcp") → account ids.</summary>
        public Dictionary<string, HashSet<string>> Accounts { get; } = new() { ["vultr"] = new(), ["contabo"] = new(), ["hetzner"] = new(), ["azure"] = new(), ["gcp"] = new() };
        public bool KnownHosts { get; set; }
        public bool WebPins { get; set; }
        /// <summary>Scheduled LAN re-scans (all or none).</summary>
        public bool LanMonitors { get; set; }
        /// <summary>The app's own settings (terminal profiles, appearance, AI engines…), not the cloud accounts.</summary>
        public bool AppSettings { get; set; }
        public bool Reports { get; set; }

        public int Count => Hosts.Count + Credentials.Count + Snippets.Count + Tunnels.Count + Serial.Count + Infra.Count + Portals.Count
                            + Notes.Count + Incidents.Count + Accounts.Values.Sum(a => a.Count)
                            + (KnownHosts ? 1 : 0) + (WebPins ? 1 : 0) + (LanMonitors ? 1 : 0) + (AppSettings ? 1 : 0) + (Reports ? 1 : 0);

        /// <summary>Everything in this store (reports off: they can be large and are rarely wanted elsewhere).</summary>
        public static Choice Everything()
        {
            var d = AppData.Current; var s = DcxManager.Services.AppSettings.Current;
            var c = new Choice { KnownHosts = true, WebPins = true, LanMonitors = true, AppSettings = true };
            c.Fill(d, s, DcxManager.Notes.NoteStore.Snapshot().Where(n => n.Deleted == null).Select(n => n.Id),
                   DcxManager.Incidents.IncidentStore.Snapshot().Incidents.Where(i => i.Deleted == null).Select(i => i.Id));
            return c;
        }

        /// <summary>Everything a file holds (what the import tree starts with).</summary>
        public static Choice From(BackupFile b)
        {
            var c = new Choice
            {
                KnownHosts = b.Data?.KnownHosts.Count > 0, WebPins = b.Data?.WebPins.Count > 0, LanMonitors = b.Data?.LanMonitors.Count > 0,
                AppSettings = b.Settings != null && b.AppSettingsIncluded != false, Reports = b.Reports is { Count: > 0 },
            };
            c.Fill(b.Data ?? new AppData(), b.Settings, b.Notes?.Select(n => n.Id) ?? [], b.Incidents?.Select(i => i.Id) ?? []);
            return c;
        }

        private void Fill(AppData d, AppSettings? s, IEnumerable<string> notes, IEnumerable<string> incidents)
        {
            Hosts.UnionWith(d.Hosts.Select(h => h.Id)); Credentials.UnionWith(d.Credentials.Select(x => x.Id));
            Snippets.UnionWith(d.Snippets.Select(x => x.Id)); Tunnels.UnionWith(d.Tunnels.Select(x => x.Id));
            Serial.UnionWith(d.SerialProfiles.Select(x => x.Id)); Infra.UnionWith(d.InfraProviders.Select(x => x.Id));
            Portals.UnionWith(d.Portals.Select(x => x.Id)); Notes.UnionWith(notes); Incidents.UnionWith(incidents);
            if (s == null) return;
            Accounts["vultr"].UnionWith(s.VultrAccounts.Select(a => a.Id)); Accounts["contabo"].UnionWith(s.ContaboAccounts.Select(a => a.Id));
            Accounts["hetzner"].UnionWith(s.HetznerAccounts.Select(a => a.Id)); Accounts["azure"].UnionWith(s.AzureAccounts.Select(a => a.Id));
            Accounts["gcp"].UnionWith(s.GcpAccounts.Select(a => a.Id));
        }
    }

    /// <summary>What a custom export or import filter did beyond the ticks (shown to the user before anything is written).</summary>
    public sealed record CustomResult(BackupFile File, IReadOnlyList<string> AddedJumpHosts, IReadOnlyList<string> StrippedCredentials)
    {
        /// <summary>How many secrets the file carries: credentials plus cloud accounts (never the secrets themselves).</summary>
        public (int Credentials, int CloudKeys) Secrets => (File.Data?.Credentials.Count ?? 0,
            File.Settings is { } s ? s.VultrAccounts.Count + s.ContaboAccounts.Count + s.HetznerAccounts.Count + s.AzureAccounts.Count + s.GcpAccounts.Count : 0);
    }

    /// <summary>A file holding exactly the picked items from this store.</summary>
    public static CustomResult Custom(Choice c, string description = "")
    {
        var (notes, (incs, files)) = (DcxManager.Notes.NoteStore.Snapshot(), DcxManager.Incidents.IncidentStore.Snapshot());
        var b = new BackupFile
        {
            Scope = "custom", Description = description, Data = Clone(AppData.Current), Settings = Clone(AppSettings.Current),
            Reports = c.Reports ? ReportStore.Load() : null, Notes = notes, Incidents = incs, IncidentFiles = files,
        };
        b.Data.RecentAdHoc.Clear(); b.Data.PinnedQuick.Clear();
        return Narrow(b, c, pullJumpHosts: true);
    }

    /// <summary>The part of a read file the user ticked on import (the same rules as an export).</summary>
    public static CustomResult Filter(BackupFile b, Choice c) => Narrow(b, c, pullJumpHosts: true);

    private static CustomResult Narrow(BackupFile b, Choice c, bool pullJumpHosts)
    {
        var d = b.Data ?? new AppData();
        var byId = d.Hosts.ToDictionary(h => h.Id);
        var hosts = new HashSet<string>(c.Hosts.Where(byId.ContainsKey));
        var jumps = new List<string>();
        if (pullJumpHosts)
        {
            // A host is useless without the jump hosts it connects through: they come along (recursively, like Selection).
            var queue = new Queue<string>(hosts);
            while (queue.Count > 0)
                if (byId.GetValueOrDefault(queue.Dequeue())?.JumpHostId is { } j && byId.TryGetValue(j, out var jh) && hosts.Add(j)) { jumps.Add(jh.Display); queue.Enqueue(j); }
        }
        d.Hosts = d.Hosts.Where(h => hosts.Contains(h.Id)).ToList();
        d.Credentials = d.Credentials.Where(x => c.Credentials.Contains(x.Id)).ToList();
        d.Snippets = d.Snippets.Where(x => c.Snippets.Contains(x.Id)).ToList();
        d.Tunnels = d.Tunnels.Where(x => c.Tunnels.Contains(x.Id) && hosts.Contains(x.HostId)).ToList();   // a tunnel needs its host
        d.SerialProfiles = d.SerialProfiles.Where(x => c.Serial.Contains(x.Id)).ToList();
        d.InfraProviders = d.InfraProviders.Where(x => c.Infra.Contains(x.Id)).ToList();
        d.Portals = d.Portals.Where(x => c.Portals.Contains(x.Id)).ToList();
        if (!c.KnownHosts) d.KnownHosts.Clear();
        if (!c.WebPins) d.WebPins.Clear();
        if (!c.LanMonitors) { d.LanMonitors.Clear(); d.DashboardTiles.Clear(); }   // scheduled checks travel together
        d.DashboardTiles = d.DashboardTiles.Where(t => t.HostId == null || hosts.Contains(t.HostId)).ToList();   // a performance tile needs its host
        // File commander hotlist / user menu travel with the app settings; a hotlist entry needs its host.
        if (!c.AppSettings) { d.CommanderHotlist.Clear(); d.CommanderUserMenu = null; }
        d.ToolboxHistory.Clear();   // results of checks are this PC's working notes, never exported
        d.LanLastScan = null;
        // VPN profiles (0.3.0 §24) travel with the tunnels choice; their keys are credentials and follow the credential rules below.
        if (c.Tunnels.Count == 0) d.VpnProfiles.Clear();
        if (!c.AppSettings) d.TeamLibraries.Clear();   // which team libraries this profile knows (no secret) travels with the settings
        d.CommanderHotlist = d.CommanderHotlist.Where(x => x.Place == "local" || x.Place.StartsWith("host:") && hosts.Contains(x.Place[5..])).ToList();
        var folders = d.Hosts.Select(h => h.Group).Concat(d.Portals.Select(p => p.Folder)).Where(f => f.Length > 0).ToList();
        d.Folders = d.Folders.Where(f => folders.Any(g => AppData.InFolder(g, f) || AppData.InFolder(f, g))).ToList();

        // A credential that wasn't picked never travels hidden inside something that was: its references are removed.
        var kept = d.Credentials.Select(x => x.Id).ToHashSet();
        var stripped = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        string? Keep(string? id, string what)
        {
            if (id == null || kept.Contains(id)) return id;
            stripped.Add(what);
            return null;
        }
        foreach (var h in d.Hosts) { h.CredentialId = Keep(h.CredentialId, h.Display); h.SudoCredentialId = Keep(h.SudoCredentialId, h.Display + " (sudo)"); if (h.VpnProfileId != null && d.VpnProfiles.All(v => v.Id != h.VpnProfileId)) h.VpnProfileId = null; }
        foreach (var v in d.VpnProfiles) { v.KeyCredentialId = Keep(v.KeyCredentialId, v.Name + " (VPN key)"); v.PskCredentialId = Keep(v.PskCredentialId, v.Name + " (VPN pre-shared keys)"); v.AuthCredentialId = Keep(v.AuthCredentialId, v.Name + " (VPN password)"); }
        foreach (var ip in d.InfraProviders)
        {
            ip.CredentialId = Keep(ip.CredentialId, ip.Name);
            if (ip.HostId != null && !hosts.Contains(ip.HostId) && !ip.HostId.StartsWith("lib_")) ip.HostId = null;
        }
        foreach (var p in d.Portals)
        {
            p.CredentialId = Keep(p.CredentialId, p.Title);
            if (p.HostId != null && !hosts.Contains(p.HostId)) p.HostId = null;
        }

        if (b.Settings is { } s)
        {
            // The app's own settings only when ticked; the cloud accounts are filtered one by one either way.
            var accounts = Clone(s);
            var target = c.AppSettings ? s : new AppSettings();
            target.VultrAccounts = accounts.VultrAccounts.Where(a => c.Accounts["vultr"].Contains(a.Id)).ToList();
            target.ContaboAccounts = accounts.ContaboAccounts.Where(a => c.Accounts["contabo"].Contains(a.Id)).ToList();
            target.HetznerAccounts = accounts.HetznerAccounts.Where(a => c.Accounts["hetzner"].Contains(a.Id)).ToList();
            target.AzureAccounts = accounts.AzureAccounts.Where(a => c.Accounts["azure"].Contains(a.Id)).ToList();
            target.GcpAccounts = accounts.GcpAccounts.Where(a => c.Accounts["gcp"].Contains(a.Id)).ToList();
            var anyAccount = c.Accounts.Values.Any(a => a.Count > 0);
            b.Settings = c.AppSettings || anyAccount ? target : null;
            b.AppSettingsIncluded = c.AppSettings && b.AppSettingsIncluded != false;
        }
        b.Notes = b.Notes?.Where(n => c.Notes.Contains(n.Id)).ToList() is { Count: > 0 } ns ? ns : null;
        if (b.Notes != null) foreach (var n in b.Notes) n.HostIds = n.HostIds.Where(hosts.Contains).ToList();
        b.Incidents = b.Incidents?.Where(i => c.Incidents.Contains(i.Id)).ToList() is { Count: > 0 } incs ? incs : null;
        var incIds = b.Incidents?.Select(i => i.Id).ToHashSet() ?? new();
        b.IncidentFiles = b.IncidentFiles?.Where(f => incIds.Contains(f.IncidentId)).ToList() is { Count: > 0 } fs ? fs : null;
        if (!c.Reports) b.Reports = null;
        return new CustomResult(b, jumps, stripped.ToList());
    }

    /// <summary>Puts a file's app settings in place of this PC's, keeping what must stay: the cloud account lists
    /// (merged separately), the AI lock's guarded values, admin-protected values and Group Policy values.</summary>
    public static void ApplySettings(AppSettings incoming)
    {
        var s = Clone(incoming);
        var cur = AppSettings.Current;
        s.VultrAccounts = cur.VultrAccounts; s.ContaboAccounts = cur.ContaboAccounts; s.HetznerAccounts = cur.HetznerAccounts;
        s.AzureAccounts = cur.AzureAccounts; s.GcpAccounts = cur.GcpAccounts; s.ActiveVultrAccountId = cur.ActiveVultrAccountId;
        s.PreviousDataDir = cur.PreviousDataDir; s.SyncWarningDismissedFor = cur.SyncWarningDismissedFor;   // this PC's store, not the file's
        FeatureLock.KeepGuarded(s);
        SecureStore.Write("settings.dcx", s);
        AppSettings.Load();
        if (ProtectedSettings.ApplyTo(AppSettings.Current).Count > 0) AppSettings.Save();
    }
}
