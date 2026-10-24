namespace DcxManager.Services;

/// <summary>Hosts, vault, snippets, tunnels and known host keys — one encrypted file (data.dcx).</summary>
public sealed class AppData
{
    private const string FileName = "data.dcx";

    public List<HostEntry> Hosts { get; set; } = new();
    public List<Credential> Credentials { get; set; } = new();
    public List<Snippet> Snippets { get; set; } = new();
    public List<TunnelDef> Tunnels { get; set; } = new();
    /// <summary>Saved serial / USB-serial consoles (Sessions/Serial.cs).</summary>
    public List<Sessions.SerialProfile> SerialProfiles { get; set; } = new();
    /// <summary>Infrastructure providers (Docker, Proxmox). Secrets are vault credentials referenced by id.</summary>
    public List<Infra.ProviderConfig> InfraProviders { get; set; } = new();
    public List<KnownHost> KnownHosts { get; set; } = new();
    public List<string> RecentAdHoc { get; set; } = new();
    /// <summary>Quick connect lines pinned to the top of its dropdown ("rdp://admin@host", "COM3 115200"…; QuickTargets).</summary>
    public List<string> PinnedQuick { get; set; } = new();
    /// <summary>Folder paths ("Production/EU/Web") — kept even when empty. A host's Group is its folder path.</summary>
    public List<string> Folders { get; set; } = new();
    /// <summary>Portal bookmarks and host web UIs for the in-app browser (ADR-032).</summary>
    public List<Browser.PortalBookmark> Portals { get; set; } = new();
    /// <summary>Self-signed web certificates trusted per host:port in the in-app browser (TOFU, ADR-032).</summary>
    public List<Browser.WebCertPin> WebPins { get; set; } = new();
    /// <summary>Scheduled LAN re-scans (LAN discovery → Monitoring, 0.3.0 §7).</summary>
    public List<LanMonitor> LanMonitors { get; set; } = new();
    /// <summary>File commander directory hotlist (Ctrl+\, 0.3.0 §15).</summary>
    public List<HotlistEntry> CommanderHotlist { get; set; } = new();
    /// <summary>File commander user menu (Ctrl+F2, 0.3.0 §15); null = not set up yet (the defaults are offered).</summary>
    public List<UserMenuEntry>? CommanderUserMenu { get; set; }
    /// <summary>Network &amp; mail toolbox results, newest first (0.3.0 §18; at most <see cref="ToolboxRun.Max"/>).</summary>
    public List<ToolboxRun> ToolboxHistory { get; set; } = new();
    /// <summary>The last LAN discovery result (0.3.0 §7).</summary>
    public LanLastScan? LanLastScan { get; set; }
    /// <summary>VPN tunnels (WireGuard / OpenVPN, 0.3.0 §24); their keys are vault credentials.</summary>
    public List<VpnProfile> VpnProfiles { get; set; } = new();
    /// <summary>Checks pinned to the dashboard (0.3.0 §20).</summary>
    public List<DashboardTile> DashboardTiles { get; set; } = new();
    /// <summary>Team libraries joined or created without an add-on (0.3.0 §22).</summary>
    public List<TeamLibraryRef> TeamLibraries { get; set; } = new();

    /// <summary>Every folder path, including parents implied by deeper paths and host groups.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> AllFolders
    {
        get
        {
            var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Folders.Concat(Hosts.Select(h => h.Group)))
            {
                var parts = NormalizeFolder(p).Split('/', StringSplitOptions.RemoveEmptyEntries);
                for (int i = 1; i <= parts.Length; i++) set.Add(string.Join("/", parts.Take(i)));
            }
            return set.ToList();
        }
    }

    /// <summary>Every credential folder (0.4.0), with the parents deeper paths imply, plus the host folders (one tree of
    /// names is easier to keep straight than two).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> AllCredentialFolders
    {
        get
        {
            var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Credentials.Select(c => c.Folder).Concat(AllFolders))
            {
                var parts = NormalizeFolder(p).Split('/', StringSplitOptions.RemoveEmptyEntries);
                for (int i = 1; i <= parts.Length; i++) set.Add(string.Join("/", parts.Take(i)));
            }
            return set.ToList();
        }
    }

    public static string NormalizeFolder(string? p) =>
        string.Join("/", (p ?? "").Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    public static bool InFolder(string group, string folder) =>
        group.Equals(folder, StringComparison.OrdinalIgnoreCase) || group.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);

    public static AppData Current { get; private set; } = new();

    /// <summary>Raised (on the caller's thread) after any save, so open views can refresh.</summary>
    public static event Action? Changed;

    public static void Load()
    {
        Current = SecureStore.Read<AppData>(FileName);
        if (Current.Snippets.Count == 0) Current.Snippets.AddRange(DefaultSnippets());
    }

    public static void Save()
    {
        // 0.3.0 §23: a viewer (read-only role from the directory sign-in) can look but never change what is stored.
        if (Profiles.ReadOnly) throw new InvalidOperationException("This profile is read-only (viewer role): nothing can be saved.");
        lock (Current) SecureStore.Write(FileName, Current);
        Changed?.Invoke();
    }

    /// <summary>Local first; "lib_…" ids resolve to shared library items (ADR-033), which are never part of this file.</summary>
    public HostEntry? FindHost(string? id) => id == null ? null : Hosts.FirstOrDefault(h => h.Id == id) ?? SharedHost?.Invoke(id);
    public Credential? FindCredential(string? id) => id == null ? null : Credentials.FirstOrDefault(c => c.Id == id) ?? SharedCredential?.Invoke(id);

    /// <summary>How shared library ids resolve (ADR-033): set by the shared library at start-up (0.2.0 phase 6: the store
    /// below doesn't reference the library above it).</summary>
    public static Func<string, HostEntry?>? SharedHost { get; set; }
    public static Func<string, Credential?>? SharedCredential { get; set; }

    public HostEntry? FindHostByName(string nameOrAddress) =>
        Hosts.FirstOrDefault(h => h.Name.Equals(nameOrAddress, StringComparison.OrdinalIgnoreCase))
        ?? Hosts.FirstOrDefault(h => h.Address.Equals(nameOrAddress, StringComparison.OrdinalIgnoreCase))
        ?? Hosts.FirstOrDefault(h => h.Name.Contains(nameOrAddress, StringComparison.OrdinalIgnoreCase));

    [System.Text.Json.Serialization.JsonIgnore]
    public IEnumerable<string> Groups => AllFolders;

    private static IEnumerable<Snippet> DefaultSnippets() => new[]
    {
        new Snippet { Name = "Disk usage", Command = "df -h -x tmpfs -x devtmpfs", Description = "Mounted filesystems" },
        new Snippet { Name = "Memory", Command = "free -h", Description = "RAM and swap" },
        new Snippet { Name = "Top processes (CPU)", Command = "ps -eo pid,user,%cpu,%mem,comm --sort=-%cpu | head -15" },
        new Snippet { Name = "Failed services", Command = "systemctl --failed --no-pager" },
        new Snippet { Name = "Listening ports", Command = "ss -tulpn", Sudo = true },
        new Snippet { Name = "Recent errors (journal)", Command = "journalctl -p err -n 50 --no-pager", Sudo = true },
        new Snippet { Name = "Who is logged in", Command = "w" },
        new Snippet { Name = "Last logins", Command = "last -n 20" },
        new Snippet { Name = "Reboot required?", Command = "[ -f /var/run/reboot-required ] && cat /var/run/reboot-required || echo 'No reboot required'" },
        new Snippet { Name = "Upgradable packages", Command = "if command -v apt >/dev/null; then apt list --upgradable 2>/dev/null; elif command -v dnf >/dev/null; then dnf -q check-update; fi; true" },
        new Snippet { Name = "Docker containers", Command = "docker ps -a", Sudo = true },
        new Snippet { Name = "Failed SSH logins (24h)", Command = "journalctl -u ssh -u sshd --since '24 hours ago' --no-pager | grep -iE 'failed|invalid' | tail -40", Sudo = true },
    };
}
