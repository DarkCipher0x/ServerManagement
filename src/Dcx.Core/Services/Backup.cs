using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using DcxManager.Reports;

namespace DcxManager.Services;

/// <summary>
/// Password-encrypted configuration files (.dcxbackup): PBKDF2-SHA256 (600k) → AES-256-GCM, bound to the "backup" label.
/// A file can hold everything (full backup) or a selection (e.g. one folder of hosts plus exactly the credentials, jump
/// hosts, tunnels and host keys they need). Import either merges into what's here or replaces it.
/// Version 1 files from earlier releases are read unchanged.
/// </summary>
public static partial class Backup
{
    public sealed class BackupFile
    {
        public int Version { get; set; } = 2;
        public DateTime Created { get; set; } = DateTime.Now;
        public string Scope { get; set; } = "full";          // full | selection | custom (BackupCustom.cs)
        public string Description { get; set; } = "";
        public AppData? Data { get; set; }
        public AppSettings? Settings { get; set; }
        /// <summary>False: <see cref="Settings"/> only carries picked cloud accounts, not the app's settings (a custom
        /// export without them). Null in older files = the settings are complete.</summary>
        public bool? AppSettingsIncluded { get; set; }
        public List<ServerReport>? Reports { get; set; }
        /// <summary>Engineering notes (each one re-encrypted into its own file on import).</summary>
        public List<Notes.Note>? Notes { get; set; }
        /// <summary>0.6.12: the Markdown documents (they go with notes: the same "Notes" choice).</summary>
        public List<Docs.Doc>? Docs { get; set; }
        /// <summary>Incidents / work sessions and their screenshot/file attachments (ADR-010).</summary>
        public List<Incidents.Incident>? Incidents { get; set; }
        public List<Incidents.IncidentAttachment>? IncidentFiles { get; set; }

        public string Summary =>
            $"{(Scope == "full" ? "Full backup" : Scope == "custom" ? "Custom export" : "Selection")} from {Created:yyyy-MM-dd HH:mm}" + (Description.Length > 0 ? $" — {Description}" : "") +
            $"\n{Data?.Hosts.Count ?? 0} hosts in {Data?.AllFolders.Count() ?? 0} folders, {Data?.Credentials.Count ?? 0} credentials, {Data?.Snippets.Count ?? 0} snippets, " +
            $"{Data?.Tunnels.Count ?? 0} tunnels, {Data?.SerialProfiles.Count ?? 0} serial consoles, {Data?.KnownHosts.Count ?? 0} known host keys" +
            (Data is { Portals.Count: > 0 } ? $", {Data.Portals.Count} portals" : "") + (Data is { InfraProviders.Count: > 0 } ? $", {Data.InfraProviders.Count} infrastructure providers" : "") +
            (Settings != null ? $", {(AppSettingsIncluded == false ? "cloud accounts" : "settings")} ({Settings.VultrAccounts.Count} Vultr, {Settings.ContaboAccounts.Count} Contabo, {Settings.HetznerAccounts.Count} Hetzner, {Settings.AzureAccounts.Count} Azure, {Settings.GcpAccounts.Count} Google Cloud accounts)" : "") + (Reports is { Count: > 0 } ? $", {Reports.Count} reports" : "") + (Notes is { Count: > 0 } ? $", {Notes.Count} notes" : "") + (Incidents is { Count: > 0 } ? $", {Incidents.Count} incidents" : "");
    }

    public sealed record Include(bool Credentials = true, bool Snippets = true, bool Tunnels = true, bool KnownHosts = true, bool Settings = true, bool Reports = false, bool Notes = true, bool Incidents = true);

    private static T Clone<T>(T o) => JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(o, SecureStore.Json), SecureStore.Json)!;

    /// <summary>Everything (subject to <paramref name="inc"/>).</summary>
    public static BackupFile Full(Include inc)
    {
        var d = Clone(AppData.Current);
        if (!inc.Credentials) { d.Credentials.Clear(); foreach (var h in d.Hosts) { h.CredentialId = null; h.SudoCredentialId = null; } }
        if (!inc.Snippets) d.Snippets.Clear();
        if (!inc.Tunnels) d.Tunnels.Clear();
        if (!inc.KnownHosts) d.KnownHosts.Clear();
        d.RecentAdHoc.Clear(); d.PinnedQuick.Clear();   // this PC's history, not configuration
        var (incidents, incFiles) = inc.Incidents ? DcxManager.Incidents.IncidentStore.Snapshot() : (null, null);
        return new BackupFile
        {
            Incidents = incidents, IncidentFiles = incFiles,
            Scope = "full", Data = d,
            Settings = inc.Settings ? Clone(AppSettings.Current) : null,
            Reports = inc.Reports ? ReportStore.Load() : null,
            Notes = inc.Notes ? DcxManager.Notes.NoteStore.Snapshot() : null,
            Docs = inc.Notes ? DcxManager.Docs.DocStore.Snapshot() : null,
        };
    }

    /// <summary>
    /// The given hosts plus what they depend on: their jump hosts (recursively), the credentials they use (login + sudo),
    /// tunnels over them, their known host keys, and the folders they live in.
    /// </summary>
    public static BackupFile Selection(IEnumerable<HostEntry> hosts, Include inc, string description)
    {
        var src = AppData.Current;
        var set = new Dictionary<string, HostEntry>();
        void AddHost(HostEntry h, int depth)
        {
            if (depth > 6 || !set.TryAdd(h.Id, h)) return;
            if (src.FindHost(h.JumpHostId) is { } j) AddHost(j, depth + 1);
        }
        foreach (var h in hosts) AddHost(h, 0);
        var d = new AppData { Hosts = set.Values.Select(Clone).ToList() };
        var folders = d.Hosts.Select(h => h.Group).ToHashSet(StringComparer.OrdinalIgnoreCase);
        d.Folders = src.Folders.Where(f => folders.Any(g => g.Equals(f, StringComparison.OrdinalIgnoreCase) || g.StartsWith(f + "/", StringComparison.OrdinalIgnoreCase) || f.StartsWith(g + "/", StringComparison.OrdinalIgnoreCase))).ToList();
        if (inc.Credentials)
        {
            var ids = d.Hosts.SelectMany(h => new[] { h.CredentialId, h.SudoCredentialId }).Where(i => i != null).ToHashSet();
            d.Credentials = src.Credentials.Where(c => ids.Contains(c.Id)).Select(Clone).ToList();
        }
        else foreach (var h in d.Hosts) { h.CredentialId = null; h.SudoCredentialId = null; }
        if (inc.Tunnels) d.Tunnels = src.Tunnels.Where(t => set.ContainsKey(t.HostId)).Select(Clone).ToList();
        if (inc.KnownHosts)
        {
            var keys = d.Hosts.Select(h => $"{h.Address}:{h.Port}").ToHashSet(StringComparer.OrdinalIgnoreCase);
            d.KnownHosts = src.KnownHosts.Where(k => keys.Contains(k.Key)).Select(Clone).ToList();
        }
        if (inc.Snippets) d.Snippets = src.Snippets.Select(Clone).ToList();
        // Notes about the selected hosts travel with them (and only those: no unrelated notes leak into a selection).
        var notes = inc.Notes ? DcxManager.Notes.NoteStore.Snapshot().Where(n => n.Deleted == null && n.HostIds.Any(set.ContainsKey)).ToList() : null;
        // Likewise incidents involving the selected hosts (with their attachments).
        List<Incidents.Incident>? incs = null; List<Incidents.IncidentAttachment>? incFiles = null;
        if (inc.Incidents)
        {
            var (all, files) = DcxManager.Incidents.IncidentStore.Snapshot();
            incs = all.Where(i => i.Deleted == null && i.HostIds.Any(set.ContainsKey)).ToList();
            var ids = incs.Select(i => i.Id).ToHashSet();
            incFiles = files.Where(f => ids.Contains(f.IncidentId)).ToList();
        }
        return new BackupFile { Scope = "selection", Description = description, Data = d, Notes = notes, Incidents = incs, IncidentFiles = incFiles };
    }

    /// <summary>The file without credentials (passwords, SSH and VPN keys) or the app's own secrets (AI keys, sink and Grafana
    /// tokens, the SMTP password, local API tokens, cloud API keys); every reference to a removed credential is cleared.</summary>
    public static BackupFile WithoutSecrets(BackupFile b)
    {
        if (b.Data is { } d)
        {
            d.Credentials.Clear();
            foreach (var h in d.Hosts) { h.CredentialId = null; h.SudoCredentialId = null; }
            foreach (var v in d.VpnProfiles) { v.KeyCredentialId = null; v.PskCredentialId = null; v.AuthCredentialId = null; }
            foreach (var i in d.InfraProviders) i.CredentialId = null;
            foreach (var p in d.Portals) p.CredentialId = null;
        }
        if (b.Settings is { } s) BackupPolicy.StripSettingSecrets(s);
        return b;
    }

    public static void Write(string path, string password, BackupFile b)
    {
        // 0.5.0 (ADR-060): the export rules live here, below every caller.
        if (BackupPolicy.ExportBlocked() is { } no) throw new InvalidOperationException(no);
        if (BackupPolicy.PasswordProblem(password) is { } weak) throw new ArgumentException(weak);
        if (!BackupPolicy.SecretsAllowed) b = WithoutSecrets(b);
        var plain = JsonSerializer.SerializeToUtf8Bytes(b, SecureStore.Json);
        var salt = RandomNumberGenerator.GetBytes(16);
        var blob = SecureStore.Seal(SecureStore.Derive(password, salt, 600_000), plain, "backup");
        CryptographicOperations.ZeroMemory(plain);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, salt.Concat(blob).ToArray());
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Throws CryptographicException for a wrong password or a damaged file.</summary>
    public static BackupFile Read(string path, string password)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 40) throw new InvalidDataException("Not a DarkCipher backup file.");
        var plain = SecureStore.Open(SecureStore.Derive(password, bytes[..16], 600_000), bytes[16..], "backup");
        var b = JsonSerializer.Deserialize<BackupFile>(plain, SecureStore.Json) ?? throw new InvalidDataException("empty backup");
        CryptographicOperations.ZeroMemory(plain);
        b.Data?.Hosts.ForEach(h => { if (string.IsNullOrWhiteSpace(h.Group)) h.Group = "General"; });
        return b;
    }

    public enum Conflict { Skip, Replace, KeepBoth }

    public sealed record MergeResult(int HostsAdded, int HostsReplaced, int HostsSkipped, int CredsAdded, int Other);

    /// <summary>Adds the file's items to the current store. Items are matched by id (then by address+port+user for hosts).</summary>
    public static MergeResult Merge(BackupFile b, Conflict mode, string? intoFolder = null)
    {
        if (BackupPolicy.ImportBlocked() is { } no) throw new InvalidOperationException(no);
        var cur = AppData.Current;
        var d = b.Data ?? new AppData();
        int added = 0, replaced = 0, skipped = 0, creds = 0, other = 0;
        var credMap = new Dictionary<string, string>();   // file id → id used here
        var hostMap = new Dictionary<string, string>();

        foreach (var c in d.Credentials)
        {
            var same = cur.FindCredential(c.Id) ?? cur.Credentials.FirstOrDefault(x => x.Kind == c.Kind && x.Secret == c.Secret && x.Secret.Length > 0);
            if (same == null) { cur.Credentials.Add(c); credMap[c.Id] = c.Id; creds++; }
            else if (mode == Conflict.Replace && same.Id == c.Id) { cur.Credentials[cur.Credentials.IndexOf(same)] = c; credMap[c.Id] = c.Id; }
            else if (mode == Conflict.KeepBoth && same.Secret != c.Secret) { var n = Clone(c); n.Id = Guid.NewGuid().ToString("N"); n.Name += " (imported)"; cur.Credentials.Add(n); credMap[c.Id] = n.Id; creds++; }
            else credMap[c.Id] = same.Id;
        }

        foreach (var h in d.Hosts)
        {
            if (intoFolder != null) h.Group = h.Group.Length > 0 && h.Group != "General" ? intoFolder + "/" + h.Group : intoFolder;
            var orig = h.Id;
            var same = cur.FindHost(h.Id) ?? cur.Hosts.FirstOrDefault(x => x.Address.Equals(h.Address, StringComparison.OrdinalIgnoreCase) && x.Port == h.Port && x.Username == h.Username);
            if (same != null && mode == Conflict.Skip) { hostMap[orig] = same.Id; skipped++; continue; }
            if (same != null && mode == Conflict.Replace) { h.Id = same.Id; cur.Hosts[cur.Hosts.IndexOf(same)] = h; hostMap[orig] = h.Id; replaced++; continue; }
            if (same != null || cur.FindHost(h.Id) != null) { h.Id = Guid.NewGuid().ToString("N"); h.Name = h.Display + " (imported)"; }
            hostMap[orig] = h.Id;
            cur.Hosts.Add(h);
            added++;
        }
        // Re-point references to whatever ids ended up being used here.
        foreach (var h in d.Hosts)
        {
            if (h.CredentialId != null) h.CredentialId = credMap.TryGetValue(h.CredentialId, out var ci) ? ci : cur.FindCredential(h.CredentialId)?.Id;
            if (h.SudoCredentialId != null) h.SudoCredentialId = credMap.TryGetValue(h.SudoCredentialId, out var si) ? si : cur.FindCredential(h.SudoCredentialId)?.Id;
            if (h.JumpHostId != null) h.JumpHostId = hostMap.TryGetValue(h.JumpHostId, out var ji) ? ji : cur.FindHost(h.JumpHostId)?.Id;
        }

        foreach (var f in d.Folders)
        {
            var path = intoFolder != null ? intoFolder + "/" + f : f;
            if (!cur.Folders.Contains(path, StringComparer.OrdinalIgnoreCase)) { cur.Folders.Add(path); other++; }
        }
        if (intoFolder != null && !cur.Folders.Contains(intoFolder, StringComparer.OrdinalIgnoreCase)) cur.Folders.Add(intoFolder);
        foreach (var s in d.Snippets.Where(s => !cur.Snippets.Any(x => x.Id == s.Id || x.Name == s.Name && x.Command == s.Command))) { cur.Snippets.Add(s); other++; }
        foreach (var t in d.Tunnels.Where(t => cur.Tunnels.All(x => x.Id != t.Id)))
        {
            t.HostId = hostMap.GetValueOrDefault(t.HostId, t.HostId);
            cur.Tunnels.Add(t); other++;
        }
        foreach (var v in d.VpnProfiles.Where(v => cur.VpnProfiles.All(x => x.Id != v.Id)))
        {
            string? Cred(string? id) => id == null ? null : credMap.TryGetValue(id, out var m) ? m : cur.FindCredential(id)?.Id;
            v.KeyCredentialId = Cred(v.KeyCredentialId); v.PskCredentialId = Cred(v.PskCredentialId); v.AuthCredentialId = Cred(v.AuthCredentialId);
            v.LastState = ""; v.LastUp = null;
            cur.VpnProfiles.Add(v); other++;
        }
        if (b.Notes is { Count: > 0 })
        {
            foreach (var n in b.Notes) n.HostIds = n.HostIds.Select(id => hostMap.GetValueOrDefault(id, id)).ToList();
            other += DcxManager.Notes.NoteStore.Import(b.Notes);
        }
        if (b.Docs is { Count: > 0 })
        {
            foreach (var doc in b.Docs) doc.Pins = doc.Pins.Select(p => p.StartsWith("host:", StringComparison.Ordinal) ? "host:" + hostMap.GetValueOrDefault(p[5..], p[5..]) : p).ToList();
            other += DcxManager.Docs.DocStore.Import(b.Docs);
        }
        if (b.Incidents is { Count: > 0 })
        {
            foreach (var i in b.Incidents)
            {
                i.HostIds = i.HostIds.Select(id => hostMap.GetValueOrDefault(id, id)).ToList();
                foreach (var it in i.Items.Where(x => x.HostId != null)) it.HostId = hostMap.GetValueOrDefault(it.HostId!, it.HostId!);
            }
            other += DcxManager.Incidents.IncidentStore.Import(b.Incidents, b.IncidentFiles);
        }
        foreach (var ip in d.InfraProviders.Where(x => cur.InfraProviders.All(y => y.Id != x.Id)))
        {
            if (ip.HostId != null) ip.HostId = hostMap.GetValueOrDefault(ip.HostId, ip.HostId);
            if (ip.CredentialId != null) ip.CredentialId = credMap.TryGetValue(ip.CredentialId, out var ci2) ? ci2 : cur.FindCredential(ip.CredentialId)?.Id;
            cur.InfraProviders.Add(ip); other++;
        }
        foreach (var pb in d.Portals.Where(x => cur.Portals.All(y => y.Id != x.Id)))
        {
            if (pb.HostId != null) pb.HostId = hostMap.GetValueOrDefault(pb.HostId, pb.HostId);
            if (pb.CredentialId != null) pb.CredentialId = credMap.TryGetValue(pb.CredentialId, out var ci3) ? ci3 : cur.FindCredential(pb.CredentialId)?.Id;
            cur.Portals.Add(pb); other++;
        }
        foreach (var wp in d.WebPins.Where(x => cur.WebPins.All(y => !y.HostPort.Equals(x.HostPort, StringComparison.OrdinalIgnoreCase)))) { cur.WebPins.Add(wp); other++; }
        foreach (var sp in d.SerialProfiles.Where(s => cur.SerialProfiles.All(x => x.Id != s.Id))) { cur.SerialProfiles.Add(sp); other++; }
        foreach (var lm in d.LanMonitors.Where(m => cur.LanMonitors.All(x => x.Id != m.Id))) { cur.LanMonitors.Add(lm); other++; }
        foreach (var dt in d.DashboardTiles.Where(t => cur.DashboardTiles.All(x => x.Id != t.Id)))
        {
            if (dt.HostId != null) dt.HostId = hostMap.GetValueOrDefault(dt.HostId, dt.HostId);
            cur.DashboardTiles.Add(dt); other++;
        }
        foreach (var k in d.KnownHosts.Where(k => cur.KnownHosts.All(x => !x.Key.Equals(k.Key, StringComparison.OrdinalIgnoreCase)))) { cur.KnownHosts.Add(k); other++; }
        foreach (var hl in d.CommanderHotlist)
        {
            if (hl.Place.StartsWith("host:")) hl.Place = "host:" + hostMap.GetValueOrDefault(hl.Place[5..], hl.Place[5..]);
            if (cur.CommanderHotlist.All(x => x.Place != hl.Place || x.Path != hl.Path)) { cur.CommanderHotlist.Add(hl); other++; }
        }
        foreach (var tl in d.TeamLibraries.Where(x => x.LibraryId.Length == 32 && cur.TeamLibraries.All(y => y.LibraryId != x.LibraryId))) { cur.TeamLibraries.Add(tl); other++; }
        foreach (var tr in d.ToolboxHistory.Where(r => cur.ToolboxHistory.All(x => x.Id != r.Id)).Take(ToolboxRun.Max)) { cur.ToolboxHistory.Add(tr); other++; }
        cur.ToolboxHistory = cur.ToolboxHistory.OrderByDescending(r => r.When).Take(ToolboxRun.Max).ToList();
        if (d.CommanderUserMenu is { Count: > 0 } um)
        {
            cur.CommanderUserMenu ??= UserMenuEntry.Defaults();
            foreach (var u in um.Where(u => cur.CommanderUserMenu.All(x => x.Id != u.Id && x.Command != u.Command))) { cur.CommanderUserMenu.Add(u); other++; }
        }

        if (b.Settings != null)
        {
            foreach (var a in b.Settings.VultrAccounts.Where(a => AppSettings.Current.VultrAccounts.All(x => x.Id != a.Id && x.ApiKey != a.ApiKey))) { AppSettings.Current.VultrAccounts.Add(a); other++; }
            foreach (var a in b.Settings.ContaboAccounts.Where(a => AppSettings.Current.ContaboAccounts.All(x => x.Id != a.Id && !(x.ClientId == a.ClientId && x.ApiUser == a.ApiUser)))) { AppSettings.Current.ContaboAccounts.Add(a); other++; }
            foreach (var a in b.Settings.HetznerAccounts.Where(a => AppSettings.Current.HetznerAccounts.All(x => x.Id != a.Id && x.Token != a.Token))) { AppSettings.Current.HetznerAccounts.Add(a); other++; }
            foreach (var a in b.Settings.AzureAccounts.Where(a => AppSettings.Current.AzureAccounts.All(x => x.Id != a.Id && !(x.SubscriptionId == a.SubscriptionId && x.ClientId == a.ClientId)))) { AppSettings.Current.AzureAccounts.Add(a); other++; }
            foreach (var a in b.Settings.GcpAccounts.Where(a => AppSettings.Current.GcpAccounts.All(x => x.Id != a.Id && !(x.ProjectId == a.ProjectId && x.ClientEmail == a.ClientEmail)))) { AppSettings.Current.GcpAccounts.Add(a); other++; }
            AppSettings.Save();
        }
        if (b.Reports != null) foreach (var r in b.Reports) { try { ReportStore.Save(r); other++; } catch { } }

        AppData.Save();
        return new MergeResult(added, replaced, skipped, creds, other);
    }

    /// <summary>Replaces hosts, credentials and (if present) settings with the file's content.</summary>
    public static void ReplaceAll(BackupFile b)
    {
        if (BackupPolicy.ImportBlocked() is { } no) throw new InvalidOperationException(no);
        if (b.Data != null) { SecureStore.Write("data.dcx", b.Data); AppData.Load(); }
        if (b.Settings != null && b.AppSettingsIncluded != false)
        {
            FeatureLock.KeepGuarded(b.Settings); SecureStore.Write("settings.dcx", b.Settings); AppSettings.Load();   // the lock's settings stay (0.2.0 audit)
            if (ProtectedSettings.ApplyTo(AppSettings.Current).Count > 0) AppSettings.Save();                          // and admin-protected values (0.3.0)
        }
        else if (b.Settings is { } accounts)
        {
            // A custom file with cloud accounts only: they replace this PC's accounts; the rest of the settings stay.
            var s = AppSettings.Current;
            s.VultrAccounts = accounts.VultrAccounts; s.ContaboAccounts = accounts.ContaboAccounts; s.HetznerAccounts = accounts.HetznerAccounts;
            s.AzureAccounts = accounts.AzureAccounts; s.GcpAccounts = accounts.GcpAccounts;
            AppSettings.Save();
        }
        if (b.Reports != null) foreach (var r in b.Reports) { try { ReportStore.Save(r); } catch { } }
    }
}
