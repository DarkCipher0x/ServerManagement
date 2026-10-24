using System.IO;
using DcxManager.Infra;
using DcxManager.Sessions;
using DcxManager.Views;

namespace DcxManager.Services;

/// <summary>Raised when a session tab opens or closes, or a terminal / Remote Desktop connects or drops (ADR-018). The
/// dashboard listens instead of polling. May be raised on any thread.</summary>
public static class LiveSessions
{
    public static event Action? Changed;
    internal static int Subscribers => Changed?.GetInvocationList().Length ?? 0;
    public static void Touch() { try { Changed?.Invoke(); } catch (Exception ex) { Log.Write("live sessions changed", ex); } }
}

/// <summary>
/// The engineering dashboard's model (spec §48, ADR-018), kept free of WPF so the self-test can drive it directly:
/// sessions by type, local warnings, card layout, and the rule for remote checks. Local facts never touch the network;
/// remote checks happen only by <see cref="AutoCheck"/> or an explicit "Check now".
/// </summary>
public static class Overview
{
    // ---- cards ---------------------------------------------------------------------------------------------------------

    public sealed record Card(string Id, string Title, string Explain);

    public static readonly Card[] Cards =
    {
        new("summary", "Summary", "Tiles: sessions, incidents, transfers, warnings, hosts, AI engine, store"),
        new("sessions", "Active sessions", "Open session tabs by type, connected or not; PRODUCTION counted separately"),
        new("warnings", "Local application warnings", "Problems this app found in itself: store, snapshots, errors, exposure"),
        new("safety", "Environment & safety", "Offline mode, blocked attempts, production sessions, local API, broadcasts, app lock"),
        new("checks", "Pinned checks", "Uptime, certificate, performance and mail-domain checks you pinned, run by the dashboard's check rule"),
        new("infra", "Infrastructure", "Your Docker, Proxmox and Unraid providers with their last-known state"),
        new("incidents", "Active incidents", "Open and monitoring incidents"),
        new("transfers", "Transfers", "Running and recent file transfers"),
        new("connections", "Recent connections", "Favourite hosts first, then the ones used most recently"),
        new("notes", "Recent notes", "Notes changed most recently"),
        new("cloud", "Cloud servers", "Instances of every connected cloud account (Vultr, Contabo, Hetzner, Azure, Google Cloud), with Vultr's balance"),
        new("activity", "Recent activity", "The newest audit log entries"),
    };

    /// <summary>The built-in cards, then the added ones (0.5.1 #12, <see cref="DashboardCatalog"/>).</summary>
    public static IReadOnlyList<string> CardIds => Cards.Select(c => c.Id).Concat(DashboardCatalog.Cards(AppSettings.Current).Select(c => c.Id)).ToList();
    public static Card CardById(string id) =>
        Cards.FirstOrDefault(c => c.Id == id)
        ?? (DashboardCatalog.Cards(AppSettings.Current).FirstOrDefault(c => c.Id == id) is { } x ? new Card(x.Id, x.Title, DashboardCatalog.Find(x.Type)?.Explain ?? "") : new Card(id, id, ""));

    /// <summary>Saved order with unknown ids dropped and new cards in their default place.</summary>
    public static List<string> Order(IEnumerable<string> saved) => NavLayout.Order(CardIds, saved);

    public static List<string> Visible(IEnumerable<string> saved, IEnumerable<string> hidden)
    {
        var h = hidden.ToHashSet();
        return Order(saved).Where(id => !h.Contains(id)).ToList();
    }

    /// <summary>Cards in reading order split into columns (left, right, left…), or one column when narrow.</summary>
    public static List<List<string>> Columns(IReadOnlyList<string> visible, double width)
    {
        int n = width >= 980 ? 2 : 1;
        var cols = Enumerable.Range(0, n).Select(_ => new List<string>()).ToList();
        for (int i = 0; i < visible.Count; i++) cols[i % n].Add(visible[i]);
        return cols;
    }

    // ---- sessions ------------------------------------------------------------------------------------------------------

    /// <summary>One open session tab. <paramref name="Connected"/> is null for views without a live connection state.</summary>
    public sealed record SessionRow(string Kind, string Title, string Environment, bool? Connected, object Content);

    public static readonly string[] SessionKinds = { "SSH", "Remote Desktop", "VNC", "Telnet", "Serial", "Local shell", "WSL", "Files (SFTP)", "Server view" };

    public static SessionRow? Classify(object? content) => content switch
    {
        TerminalView tv => new SessionRow(tv.Target switch { SerialTarget => "Serial", LocalTarget { Kind: "wsl" } => "WSL", LocalTarget => "Local shell", TelnetTarget => "Telnet", _ => "SSH" },
            tv.Target.Title, EnvUi.ForView(tv), tv.IsConnected, tv),
        RdpView rv => new SessionRow("Remote Desktop", rv.Host?.Display ?? "", EnvUi.ForView(rv), rv.IsConnected, rv),
        VncView vv => new SessionRow("VNC", vv.Host?.Display ?? "", EnvUi.ForView(vv), vv.IsConnected, vv),
        SftpView sv => new SessionRow("Files (SFTP)", sv.Host?.Display ?? "", EnvUi.ForView(sv), sv.IsConnected, sv),
        ServerView v => new SessionRow("Server view", v.Host?.Display ?? "", EnvUi.ForView(v), null, v),
        _ => null,
    };

    public sealed record SessionCount(string Kind, int Open, int Connected);

    /// <summary>Counts per kind in <see cref="SessionKinds"/> order; kinds with nothing open are left out.</summary>
    public static List<SessionCount> Count(IEnumerable<SessionRow> rows)
    {
        var list = rows.ToList();
        return SessionKinds.Select(k => new SessionCount(k, list.Count(r => r.Kind == k), list.Count(r => r.Kind == k && r.Connected != false)))
            .Where(c => c.Open > 0).ToList();
    }

    // ---- local warnings ------------------------------------------------------------------------------------------------

    /// <summary>"bad", "warn" or "info". <paramref name="Go"/> is where the fix is: a nav key, or "settings:&lt;card&gt;".</summary>
    public sealed record Warning(string Level, string Text, string? Fix = null, string? Go = null);

    /// <summary>Everything the warnings are computed from (gathered locally; the self-test builds its own).</summary>
    public sealed record Facts(
        IReadOnlyList<string> RecoveryNotices, int DamagedNotes, int DamagedIncidents,
        bool AutoSnapshots, int SnapshotIntervalHours, DateTime? LastAutoSnapshot, string? SnapshotFailure,
        string? SyncedFolder, int ErrorsThisSession, string? InternetAi, bool LocalApi, int AddonsBlockedOffline,
        int FailedTransfers24h, bool UpdatesOff, DateTime Now);

    private static (string Dir, string? Result)? _syncCache;

    public static Facts Gather()
    {
        var s = AppSettings.Current;
        DateTime? last = null;
        try { last = Snapshots.LatestAuto()?.Created; } catch { }
        string? synced = null;
        try
        {
            var dir = SecureStore.DataDir;
            if (_syncCache is not { } c || c.Dir != dir)
            {
                var info = SyncDetector.Detect(dir);
                _syncCache = (dir, info.Kind is StorageKind.Local or StorageKind.Unknown ? null : info.Summary);
            }
            synced = _syncCache.Value.Result;
        }
        catch { }
        string? internetAi = null;
        try { internetAi = Assist.Current?.InternetEndpoint; } catch (Exception ex) { Log.Write("Overview: assistant endpoint", ex); }
        var now = DateTime.Now;
        return new Facts(StoreRecovery.Notices.ToList(), Notes.NoteStore.Damaged.Count, Incidents.IncidentStore.Damaged.Count,
            s.AutoSnapshots, s.SnapshotIntervalHours, last, Snapshots.LastAutoResult is { Ok: false } r ? r.Message : null,
            synced, App.ErrorsThisSession, internetAi, s.LocalApiEnabled,
            Offline.AddonsBlocked() != null ? s.Addons.Count : 0,
            Files.TransferHistory.All.Count(e => e.Result != "ok" && now - e.Time < TimeSpan.FromHours(24)),
            !Updater.Enabled, now);
    }

    public static List<Warning> Warnings(Facts f)
    {
        var w = new List<Warning>();
        foreach (var n in f.RecoveryNotices) w.Add(new("bad", "Store recovery: " + n, "Snapshots", "settings:Snapshots"));
        if (f.DamagedNotes > 0) w.Add(new("bad", $"{f.DamagedNotes} note file(s) could not be read and were set aside.", "Open notes", "notes"));
        if (f.DamagedIncidents > 0) w.Add(new("bad", $"{f.DamagedIncidents} incident file(s) could not be read and were set aside.", "Open incidents", "incidents"));
        if (f.SnapshotFailure != null) w.Add(new("bad", "The last automatic snapshot failed: " + f.SnapshotFailure, "Snapshots", "settings:Snapshots"));
        else if (!f.AutoSnapshots) w.Add(new("warn", "Automatic snapshots are off, so there is no rolling copy to recover from.", "Snapshots", "settings:Snapshots"));
        else if (f.LastAutoSnapshot is not { } last) w.Add(new("info", "No automatic snapshot yet (the first is taken shortly after start).", "Snapshots", "settings:Snapshots"));
        else if (f.Now - last > TimeSpan.FromHours(Math.Clamp(f.SnapshotIntervalHours, 1, 24 * 30) * 2 + 1))
            w.Add(new("warn", $"The newest automatic snapshot is from {last:d MMM HH:mm} — overdue.", "Snapshots", "settings:Snapshots"));
        if (f.SyncedFolder != null) w.Add(new("warn", $"Your data folder is in {f.SyncedFolder}: encrypted copies leave this PC.", "Data & storage", "settings:Data & storage"));
        if (f.ErrorsThisSession > 0) w.Add(new("warn", $"{f.ErrorsThisSession} error(s) were logged since the app started.", "View error log", "settings:Data & storage"));
        if (f.FailedTransfers24h > 0) w.Add(new("warn", $"{f.FailedTransfers24h} transfer(s) failed in the last 24 hours.", "File commander", "commander"));
        if (f.InternetAi != null) w.Add(new("info", $"An internet AI provider is in use ({f.InternetAi}); what you send leaves your network.", "AI engine", "settings:AI engine"));
        if (f.LocalApi) w.Add(new("info", "The local API is listening on this PC (loopback only).", "Local API", "settings:Local API & broadcast"));
        if (f.AddonsBlockedOffline > 0) w.Add(new("info", $"{f.AddonsBlockedOffline} add-on(s) can't run while offline mode is on.", "Offline mode", "settings:Offline mode"));
        if (f.UpdatesOff) w.Add(new("info", "Updates are switched off in this build (no release key); check the website for new versions.", null, null));
        return w;
    }

    // ---- remote checks -------------------------------------------------------------------------------------------------

    public sealed record Source(string Id, string Title, string Host);

    // A module's dashboard source and its tile (CheckOutcome, TileText, ModuleSource) live with the module contracts in
    // the UI kit since 0.2.0 phase 6f (Dcx.Ui, ModuleContributions.cs); the dashboard reads them here as before.
    /// <summary>The contributed sources, in registration order (the composition root adds them through the app modules).</summary>
    public static IReadOnlyList<ModuleSource> ModuleSources => DashboardSources.All;

    /// <summary>Every remote source the dashboard could check: each cloud provider's active account, the modules' sources
    /// (the AI engine), each infrastructure provider. Cloud sources other than Vultr are opt-in (Checks ▾), like
    /// infrastructure.</summary>
    public static List<Source> Sources()
    {
        var list = new List<Source>();
        foreach (var p in CloudProviders.All)
            if (p.ActiveAccount != null) list.Add(new(p.Id, $"{p.Title} account", p.ApiHosts[0]));
        foreach (var m in DashboardSources.All)
        {
            string? host = null;
            try { host = m.Host(); } catch (Exception ex) { Log.Write($"Dashboard source ({m.Module}, {m.Id})", ex); }
            if (host != null) list.Add(new(m.Id, m.Title, host));
        }
        foreach (var c in AppData.Current.InfraProviders) list.Add(new("infra:" + c.Id, c.Name, ProviderHost(c)));
        return list;
    }

    public static string ProviderHost(ProviderConfig c) => c.Kind switch
    {
        "docker-local" => "localhost",
        "docker-ssh" => AppData.Current.FindHost(c.HostId)?.Address ?? "",
        _ => Offline.HostOf(c.Endpoint),
    };

    public static string ModeText(string mode, int minutes) => mode switch
    {
        "manual" => "only when you press Check now",
        "interval" => $"every {minutes} min while the dashboard is on screen",
        _ => "when the dashboard opens",
    };

    /// <summary>Why an automatic check of <paramref name="s"/> doesn't run now, or null when it should.
    /// <paramref name="trigger"/> is "open" or "interval".</summary>
    public static string? AutoCheck(Source s, string trigger)
    {
        var st = AppSettings.Current;
        if (!st.DashboardAutoSources.Contains(s.Id)) return "not included in automatic checks";
        if (st.DashboardChecks == "manual") return "checks are manual";
        if (st.DashboardChecks != trigger) return trigger == "open" ? "checks run on the interval" : "checks run when the dashboard opens";
        if (Offline.On && Offline.Quick(s.Host) is { Allowed: false }) return "skipped: offline mode";
        return null;
    }

    public static void SetAutoSource(string id, bool on)
    {
        var list = AppSettings.Current.DashboardAutoSources;
        list.Remove(id);
        if (on) list.Add(id);
    }
}
