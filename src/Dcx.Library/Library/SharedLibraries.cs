using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Windows;
using DcxManager.Addons;
using DcxManager.Services;

namespace DcxManager.Library;

/// <summary>The app's view of every shared library (ADR-033): one <see cref="LibraryClient"/> per enabled, hash-verified
/// library add-on whose member keys are on this PC. Shared hosts and credentials are materialised here as their own
/// objects — never added to <see cref="AppData"/>, so a sync can't change or overwrite a local host or credential. Their ids
/// are "lib_&lt;library&gt;_&lt;item&gt;", resolved by <see cref="AppData.FindHost"/> / <see cref="AppData.FindCredential"/>.</summary>
public static class SharedLibraries
{
    public const string IdPrefix = "lib_";
    private static readonly object Gate = new();
    private static Dictionary<string, LibraryClient> _clients = new();
    private static List<HostEntry> _hosts = new();
    private static List<Credential> _creds = new();
    private static readonly Dictionary<string, FileSystemWatcher> _watchers = new();
    private static Timer? _interval;
    private static readonly Dictionary<string, DateTime> _usedAudit = new();

    /// <summary>Raised on the UI thread after the shared items changed (sync, edit, add-on enabled or removed).</summary>
    public static event Action? Changed;
    /// <summary>Raised (on the UI thread) when a library add-on is installed, enabled, disabled or removed: the sidebar shows
    /// Shared library only while one is enabled.</summary>
    public static event Action? FeatureChanged;
    /// <summary>The Shared library screen is on: team features are built in (0.3.0 §22, the TeamFeatures setting — admin-
    /// protected and policy-managed), or a library add-on from before is enabled and verified.</summary>
    public static bool Enabled => AppSettings.Current.TeamFeatures || Addons.Any(a => a.Enabled && Profile(a, out _) != null);

    /// <summary>Every library this profile knows: the add-ons' profiles plus the ones joined or created without an add-on
    /// (<see cref="AppData.TeamLibraries"/>, 0.3.0 §22).</summary>
    public static IEnumerable<LibraryProfile> KnownProfiles()
    {
        var seen = new HashSet<string>();
        foreach (var a in Addons.Where(a => a.Enabled))
            if (Profile(a, out _) is { IsGeneral: false } p && seen.Add(p.LibraryId)) yield return p;
        foreach (var t in AppData.Current.TeamLibraries)
            if (t.LibraryId.Length == 32 && seen.Add(t.LibraryId)) yield return new LibraryProfile { LibraryId = t.LibraryId, Name = t.Name, Path = t.Path, OwnerFingerprint = t.OwnerFingerprint };
    }

    private static void Remember(LibraryProfile p)
    {
        if (AppData.Current.TeamLibraries.Any(t => t.LibraryId == p.LibraryId)) return;
        AppData.Current.TeamLibraries.Add(new TeamLibraryRef { LibraryId = p.LibraryId, Name = p.Name, Path = p.Path, OwnerFingerprint = p.OwnerFingerprint });
        AppData.Save();
    }

    /// <summary>Joins a library straight from its share (no add-on, 0.3.0 §22): the share's header names the library; the
    /// owner's fingerprint — read to you by the owner over a channel you trust — must match the key on the share.</summary>
    public static LibraryClient JoinShare(string sharePath, string ownerFingerprint, string myName)
    {
        sharePath = sharePath.Trim();
        var header = ShareIo.Read<LibraryHeader>(Path.Combine(sharePath, "library.json")) ?? throw new LibraryException($"No library was found in {sharePath}.");
        if (header.LibraryId.Length != 32) throw new LibraryException("The folder's library header is malformed.");
        var existing = ReadState(header.LibraryId);
        if (existing.MemberId.Length > 0 && !existing.RemovedFromLibrary) throw new LibraryException("This PC already has keys for that library.");
        var p = new LibraryProfile { LibraryId = header.LibraryId, Name = new DirectoryInfo(sharePath).Name, Path = sharePath, OwnerFingerprint = LibraryCrypto.NormalizeFingerprint(ownerFingerprint) };
        if (p.OwnerFingerprint.Length != 32) throw new LibraryException("The owner fingerprint is 32 hex characters (8 groups of 4).");
        var c = LibraryClient.Join(p, sharePath, myName, Persist);   // verifies the header against the fingerprint before any key is made
        Remember(p);
        Register(c);
        return c;
    }
    /// <summary>The self-test turns folder watching off so its own writes don't start background syncs mid-check.</summary>
    public static bool WatchEnabled { get; set; } = true;

    public static bool IsLibrary(AddonManifest m) => !string.IsNullOrEmpty(m.Library);
    public static string StateFile(string libraryId) => $"library-{libraryId}.dcx";
    public static string ItemId(string libraryId, string item) => IdPrefix + libraryId + "_" + item;

    public static IReadOnlyList<HostEntry> Hosts { get { lock (Gate) return _hosts; } }
    public static IReadOnlyList<Credential> Credentials { get { lock (Gate) return _creds; } }
    public static IReadOnlyList<LibraryClient> Clients { get { lock (Gate) return _clients.Values.ToList(); } }
    public static LibraryClient? Client(string libraryId) { lock (Gate) return _clients.GetValueOrDefault(libraryId); }

    public static HostEntry? FindHost(string id) { if (!id.StartsWith(IdPrefix)) return null; lock (Gate) return _hosts.FirstOrDefault(h => h.Id == id); }
    public static Credential? FindCredential(string id) { if (!id.StartsWith(IdPrefix)) return null; lock (Gate) return _creds.FirstOrDefault(c => c.Id == id); }

    /// <summary>(library, item) of a shared id, or null.</summary>
    public static (LibraryClient Client, string Item)? Resolve(string? id)
    {
        if (id is not { Length: > 0 } || !id.StartsWith(IdPrefix)) return null;
        var parts = id.Split('_');
        if (parts.Length != 3) return null;
        return Client(parts[1]) is { } c ? (c, parts[2]) : null;
    }

    /// <summary>The verified profile of an installed library add-on, or null with the reason.</summary>
    public static LibraryProfile? Profile(InstalledAddon a, out string? problem)
    {
        problem = null;
        if (!IsLibrary(a.Manifest)) { problem = "not a library add-on"; return null; }
        if (AddonPackages.UseRefusal(a) is { } policy) { problem = policy; return null; }   // 0.5.1 #7
        if (AddonPackages.VerifyFiles(a) is { } bad) { problem = bad; return null; }
        try { return LibraryProfile.Parse(AddonPackages.ReadInstalledText(a, a.Manifest.Library)); }   // sealed at rest (ADR-035)
        catch (Exception ex) when (ex is IOException or InvalidDataException) { problem = ex.Message; return null; }
    }

    public static IEnumerable<InstalledAddon> Addons => AddonPackages.All.Where(a => IsLibrary(a.Manifest));

    public static LibraryState ReadState(string libraryId)
    {
        try { return SecureStore.Read<LibraryState>(StateFile(libraryId)); }
        catch (Exception ex) { Log.Write("Shared library state " + libraryId[..8], ex); return new LibraryState(); }
    }

    public static void Persist(LibraryState s) { if (s.LibraryId.Length == 32) SecureStore.Write(StateFile(s.LibraryId), s); }

    /// <summary>Member keys kept on this PC for libraries whose add-on is no longer installed.</summary>
    public static List<string> Orphans()
    {
        var installed = KnownProfiles().Select(p => p.LibraryId).ToHashSet();
        try
        {
            return Directory.EnumerateFiles(SecureStore.DataDir, "library-*.dcx").Select(f => Path.GetFileNameWithoutExtension(f)["library-".Length..])
                .Where(id => id.Length == 32 && !installed.Contains(id)).ToList();
        }
        catch (IOException) { return new(); }
    }

    /// <summary>Deletes this PC's keys and cached copy of a library (the share is untouched).</summary>
    public static void Forget(string libraryId)
    {
        try { File.Delete(Path.Combine(SecureStore.DataDir, StateFile(libraryId))); } catch (IOException) { }
        AuditLog.Write("you", "shared library", "member keys and cache forgotten on this PC", libraryId[..8]);
        Load();
    }

    /// <summary>(Re)builds the clients from the enabled library add-ons and the cached copies. Doesn't touch the share.</summary>
    public static void Load()
    {
        var clients = new Dictionary<string, LibraryClient>();
        foreach (var a in Addons.Where(a => a.Enabled))
            if (Profile(a, out var problem) == null && problem != null) Log.Write("Shared library add-on " + a.Id, new InvalidDataException(problem));
        foreach (var p in KnownProfiles())
        {
            lock (Gate) if (_clients.TryGetValue(p.LibraryId, out var existing)) { clients[p.LibraryId] = existing; continue; }
            var st = ReadState(p.LibraryId);
            if (st.LibraryId != p.LibraryId || st.MemberId.Length == 0) continue;   // installed but not joined yet
            clients[p.LibraryId] = new LibraryClient(st, Persist);
        }
        lock (Gate) _clients = clients;
        Watch();
        Rebuild();
    }

    /// <summary>Called by <see cref="AddonPackages"/> after install, enable, disable and remove.</summary>
    public static void Notify(InstalledAddon a)
    {
        if (!IsLibrary(a.Manifest)) return;
        Load();
        SyncAllInBackground();
        if (Ui.Thread.HasUi) Ui.Thread.Post(() => { try { FeatureChanged?.Invoke(); } catch (Exception ex) { Log.Write("Shared library change", ex); } });
    }

    /// <summary>Adds a client that was just created or joined in this session.</summary>
    public static void Register(LibraryClient c)
    {
        lock (Gate) _clients[c.Id] = c;
        Watch();
        Rebuild();
    }

    public static void SyncAllInBackground() { foreach (var c in Clients) _ = SyncAsync(c); }

    public static async Task<bool> SyncAsync(LibraryClient c)
    {
        bool ok;
        try { ok = await Task.Run(c.Sync); }
        catch (Exception ex) { Log.Write("Shared library sync", ex); ok = false; }
        Rebuild();
        return ok;
    }

    /// <summary>Re-materialises the shared hosts and credentials from every client's cached copy.</summary>
    public static void Rebuild()
    {
        var hosts = new List<HostEntry>();
        var creds = new List<Credential>();
        foreach (var c in Clients)
        {
            var items = c.ItemsSnapshot();
            string Ref(string? item) => item is { Length: 32 } ? ItemId(c.Id, item) : null!;
            foreach (var i in items)
            {
                var label = $"{c.State.Name} / {c.CollectionName(i.Cid)}";
                if (i.Payload.Kind == "host" && i.Payload.Host is { } h0)
                {
                    var h = h0.Clone();
                    h.Id = ItemId(c.Id, i.Item);
                    h.CredentialId = Ref(h0.CredentialId); h.SudoCredentialId = Ref(h0.SudoCredentialId); h.JumpHostId = Ref(h0.JumpHostId);
                    // 0.6.30 sweep: a VNC / Remote Desktop tunnel goes through an SSH host of this library, never one of this PC's.
                    if (h.Vnc != null) h.Vnc.SshHostId = Ref(h0.Vnc?.SshHostId);
                    if (h.Rdp != null) h.Rdp.SshHostId = Ref(h0.Rdp?.SshHostId);
                    h.Group = "⇄ " + label; h.Favorite = false; h.SharedFrom = label;
                    h.VultrInstanceId = h.VultrAccountId = h.CloudProvider = h.CloudInstanceId = h.CloudAccountId = null;
                    // 0.6.17 (ADR-096): links into Bitwarden / Password Safe name the member's own vault; on this PC they would read
                    // this user's items and send them to a host another member chose. Never taken from a library.
                    h.BitwardenItem = ""; h.PasswordSafeAccount = "";
                    hosts.Add(h);
                }
                else if (i.Payload.Kind == "cred" && i.Payload.Cred is { } c0)
                {
                    var cr = c0.Clone();
                    cr.Id = ItemId(c.Id, i.Item);
                    cr.SharedFrom = label;
                    cr.External = "";   // 0.6.17 (ADR-096): the same, for a credential pointing into a vault
                    creds.Add(cr);
                }
            }
        }
        lock (Gate) { _hosts = hosts; _creds = creds; }
        RaiseChanged();
    }

    public static void RaiseChanged()
    {
        if (!Ui.Thread.HasUi) return;
        if (Ui.Thread.CheckAccess()) Changed?.Invoke(); else Ui.Thread.Post(() => Changed?.Invoke());
    }

    /// <summary>Local folders (a synced OneDrive / Syncthing folder) are watched for changes; network shares sync on start,
    /// on demand, and on the optional interval only (no silent polling unless the user turns it on).</summary>
    private static void Watch()
    {
        var want = Clients.Where(c => !c.State.SharePath.StartsWith(@"\\") && Directory.Exists(c.State.SharePath)).ToDictionary(c => c.Id);
        lock (Gate)
        {
            foreach (var id in _watchers.Keys.Where(k => !want.ContainsKey(k)).ToList()) { _watchers[id].Dispose(); _watchers.Remove(id); }
            foreach (var (id, c) in want.Where(w => !_watchers.ContainsKey(w.Key)))
            {
                try
                {
                    var w = new FileSystemWatcher(c.State.SharePath) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };
                    Timer? debounce = null;
                    void Kick() { debounce?.Dispose(); debounce = new Timer(_ => { if (Client(id) is { } cc) _ = SyncAsync(cc); }, null, 3000, Timeout.Infinite); }
                    w.Created += (_, _) => Kick(); w.Changed += (_, _) => Kick(); w.Deleted += (_, _) => Kick(); w.Renamed += (_, _) => Kick();
                    w.EnableRaisingEvents = WatchEnabled;
                    _watchers[id] = w;
                }
                catch (Exception ex) when (ex is IOException or ArgumentException) { }
            }
            var anyInterval = _clients.Values.Any(c => c.State.SyncMinutes > 0);
            if (anyInterval && _interval == null) _interval = new Timer(_ => Tick(), null, 60_000, 60_000);
            else if (!anyInterval && _interval != null) { _interval.Dispose(); _interval = null; }
        }
    }

    private static void Tick()
    {
        foreach (var c in Clients.Where(c => c.State.SyncMinutes > 0 && (c.State.LastSync == null || (DateTime.Now - c.State.LastSync.Value).TotalMinutes >= c.State.SyncMinutes)))
            _ = SyncAsync(c);
    }

    public static void SetInterval(LibraryClient c, int minutes)
    {
        c.State.SyncMinutes = Math.Clamp(minutes, 0, 24 * 60);
        Persist(c.State);
        Watch();
    }

    /// <summary>A shared credential was used to connect: audited locally and in the library (at most once per 10 minutes per credential).</summary>
    public static void Used(Credential c, string where)
    {
        if (c.SharedFrom == null || Resolve(c.Id) is not { } r) return;
        lock (_usedAudit)
        {
            if (_usedAudit.TryGetValue(c.Id, out var t) && (DateTime.Now - t).TotalMinutes < 10) return;
            _usedAudit[c.Id] = DateTime.Now;
        }
        Task.Run(() => r.Client.Audit("shared credential used", $"{c.Name} → {where}"));
    }

    // ---- create / join / package -----------------------------------------------------------------------------------

    /// <summary>Creates a library in <paramref name="folder"/> and remembers it in this profile (0.3.0 §22: no add-on needed);
    /// with <paramref name="packageOut"/> a team add-on is also written and installed, for members on older versions.</summary>
    public static LibraryClient Create(string folder, string name, string ownerName, string firstCollection, string? packageOut = null)
    {
        var c = LibraryClient.Create(folder, name, ownerName, firstCollection, Persist);
        Persist(c.State);
        Remember(c.Profile());
        if (packageOut != null)
        {
            WritePackage(c.Profile(), ownerName, packageOut);
            var info = AddonPackages.Inspect(packageOut);
            if (info.Refusal != null) throw new LibraryException(info.Refusal);
            AddonPackages.Install(info, Array.Empty<string>());
        }
        Register(c);
        return c;
    }

    /// <summary>The team's add-on: addon.json + library.json, no program and no secret.</summary>
    public static void WritePackage(LibraryProfile p, string publisher, string path, string version = "1.0.0")
    {
        if (File.Exists(path)) File.Delete(path);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        var manifest = new
        {
            api = 2, id = "com.darkcipher.library." + p.LibraryId[..12], name = Trim("Shared library: " + p.Name, 60), version,
            publisher = Trim(publisher.Length > 0 ? publisher : "DarkCipher ServerManagement", 80),
            description = "A shared library of hosts and team credentials. Holds no secret: an admin of the library approves each member after checking their fingerprint.",
            library = "library.json",
        };
        using (var w = new StreamWriter(zip.CreateEntry("addon.json").Open(), new UTF8Encoding(false))) w.Write(JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        using (var w = new StreamWriter(zip.CreateEntry("library.json").Open(), new UTF8Encoding(false))) w.Write(p.ToJson());
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max];

    /// <summary>Puts member keys from a backup (<see cref="LibraryClient.ImportKeys"/>) back on this PC. The library's add-on
    /// must be installed and enabled; keys already here are never overwritten.</summary>
    public static LibraryClient Restore(LibraryState st)
    {
        // The backup carries everything a profile needs (library, share, owner fingerprint): no add-on required (0.3.0 §22).
        if (KnownProfiles().All(p => p.LibraryId != st.LibraryId))
            Remember(new LibraryProfile { LibraryId = st.LibraryId, Name = st.Name, Path = st.SharePath, OwnerFingerprint = st.OwnerFingerprint });
        var existing = ReadState(st.LibraryId);
        if (existing.MemberId.Length > 0 && !existing.RemovedFromLibrary && existing.MemberId != st.MemberId)
            throw new LibraryException("This PC already has other member keys for that library. Forget them first (Shared library → Forget on this PC).");
        Persist(st);
        AuditLog.Write("you", "shared library " + st.Name, "member keys restored from a backup", st.MemberId[..8]);
        var c = new LibraryClient(st, Persist);
        Register(c);
        return c;
    }

    /// <summary>Joins the library an installed add-on names: keys are made on this PC and a request goes to the share.</summary>
    public static LibraryClient Join(InstalledAddon a, string sharePath, string myName)
    {
        var p = Profile(a, out var problem) ?? throw new LibraryException("The add-on can't be used: " + problem);
        if (p.IsGeneral) throw new LibraryException("This is the general Shared library add-on: it switches the feature on. To join a team's library, install the library add-on its owner gave you.");
        var existing = ReadState(p.LibraryId);
        if (existing.MemberId.Length > 0 && !existing.RemovedFromLibrary) throw new LibraryException("This PC already has keys for that library.");
        var c = LibraryClient.Join(p, sharePath, myName, Persist);
        Register(c);
        return c;
    }
}
