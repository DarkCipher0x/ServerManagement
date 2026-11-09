namespace DcxManager.Services;

/// <summary>One kind of history the app keeps (0.6.37, ADR-116): quick connect's recents, the palette's, Toolbox runs…
/// <paramref name="Count"/> says how many entries there are (-1 = can't be counted, e.g. the portal browser's pages);
/// <paramref name="Clear"/> forgets them (the caller saves the stores afterwards).</summary>
public sealed record HistorySource(string Id, string Title, string Where, Func<int> Count, Action Clear);

/// <summary>
/// History: what the app remembers about what you did, so it can offer it again (0.6.37, ADR-116). Every history registers
/// here once (<c>HistorySources.Register</c> in the app) and asks <see cref="MayRecord"/> before it remembers anything, so
/// one switch covers all of them:
/// <list type="bullet">
/// <item><b>Off (the default):</b> nothing is written. Turning it off, and every start while it is off, clears what was kept.</item>
/// <item><b>On:</b> kept (encrypted, like everything stored), and cleared automatically when the app closes or every 1, 7
/// or 30 days, or never (<see cref="AppSettings.HistoryAutoClear"/>).</item>
/// </list>
/// Clearing (all, or one kind) is written to the audit log with what was cleared, never the entries themselves. The audit
/// log, saved hosts, pins, notes and reports are not history: they are kept whatever this says. Group Policy can set both
/// settings (<c>HistorySave</c>, <c>HistoryAutoClear</c>).
/// </summary>
public static class ActivityHistory
{
    /// <summary>The values of <see cref="AppSettings.HistoryAutoClear"/>: never, when the app closes, or every N days.</summary>
    public static class AutoClear
    {
        public const string Never = "never", OnExit = "exit", Daily = "1", Weekly = "7", Monthly = "30";
        public static readonly string[] All = { Never, OnExit, Daily, Weekly, Monthly };

        /// <summary>Days between automatic clears (0 = not by age).</summary>
        public static int Days(string? v) => v is Daily or Weekly or Monthly ? int.Parse(v) : 0;

        public static string Text(string? v) => v switch
        {
            OnExit => "when the app closes",
            Daily => "every day",
            Weekly => "every 7 days",
            Monthly => "every 30 days",
            _ => "never (only when I clear it)",
        };
    }

    private static readonly object Gate = new();
    private static readonly List<HistorySource> _sources = new();

    /// <summary>Raised after history was cleared: the id of the one kind, or null for all of it. Screens that hold a
    /// history in memory (the file commander's folders and commands) forget theirs too.</summary>
    public static event Action<string?>? Cleared;

    /// <summary>Every history, in the order registered.</summary>
    public static IReadOnlyList<HistorySource> Sources { get { lock (Gate) return _sources.ToList(); } }

    /// <summary>Adds a history (one with the same id is replaced).</summary>
    public static void Register(HistorySource source)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(source.Id, "^[a-z][a-z0-9-]{1,40}$")) throw new ArgumentException("history ids are short lower-case words: " + source.Id);
        lock (Gate) { _sources.RemoveAll(s => s.Id == source.Id); _sources.Add(source); }
    }

    public static HistorySource? Find(string id) { lock (Gate) return _sources.FirstOrDefault(s => s.Id == id); }

    /// <summary>Whether history is kept at all (off by default).</summary>
    public static bool Saving => AppSettings.Current.HistorySave;

    /// <summary>Asked by every history before it remembers something: false while history is off. Clears first when an
    /// automatic clear is due, so nothing outlives its time even if the app stays open for weeks.</summary>
    public static bool MayRecord()
    {
        ClearIfDue();
        return Saving;
    }

    /// <summary>Stamps "last connected" on a saved host, when history is kept.</summary>
    public static void NoteConnected(HostEntry host) { if (MayRecord()) host.LastConnected = DateTime.Now; }

    /// <summary>How many entries are kept in all (the ones that can be counted).</summary>
    public static int Total => Sources.Sum(s => Math.Max(0, SafeCount(s)));

    public static int SafeCount(HistorySource s)
    {
        try { return s.Count(); } catch (Exception ex) { Log.Write("History", ex); return 0; }
    }

    /// <summary>Clears one kind of history. Returns how many entries went.</summary>
    public static int Clear(string id, string who = "you") =>
        Find(id) is { } s ? Run(new[] { s }, who, "cleared: " + s.Title, id) : 0;

    /// <summary>Clears every history. Returns how many entries went.</summary>
    public static int ClearAll(string who = "you", string why = "cleared") => Run(Sources, who, why, null);

    private static int Run(IReadOnlyList<HistorySource> list, string who, string why, string? id)
    {
        int total = 0;
        var what = new List<string>();
        foreach (var s in list)
        {
            int n = SafeCount(s);
            if (n == 0) continue;
            try { s.Clear(); total += Math.Max(0, n); what.Add(n > 0 ? $"{s.Title} ({n})" : s.Title); }
            catch (Exception ex) { Log.Write("Clear history: " + s.Title, ex); }
        }
        AppSettings.Current.HistoryLastCleared = DateTime.UtcNow;
        try { AppData.Save(); } catch (Exception ex) { Log.Write("Clear history", ex); }
        try { AppSettings.Save(); } catch (Exception ex) { Log.Write("Clear history", ex); }
        // The audit says what kinds were cleared and how many entries, never the entries themselves.
        if (what.Count > 0 || who == "you") AuditLog.Write(who, "history", why, what.Count == 0 ? "nothing kept" : string.Join(", ", what));
        try { Cleared?.Invoke(id); } catch (Exception ex) { Log.Write("Clear history", ex); }
        return total;
    }

    /// <summary>Turns history on or off. Off clears what was kept; on starts the automatic-clear clock now.</summary>
    public static void SetSaving(bool on)
    {
        var s = AppSettings.Current;
        if (s.HistorySave == on) return;
        // Off: cleared while it still counts as kept (the portal pages only count then), then switched off.
        if (!on) ClearAll("you", "history turned off");
        else AuditLog.Write("you", "history", "history turned on", "cleared automatically " + AutoClear.Text(s.HistoryAutoClear));
        s.HistorySave = on;
        s.HistoryLastCleared = DateTime.UtcNow;
        try { AppSettings.Save(); } catch (Exception ex) { Log.Write("History", ex); }
    }

    /// <summary>At start-up, after the stores are open: while history is off nothing may be left (from an older version,
    /// a restored backup or a policy that just turned it off); while it is on, an automatic clear that came due is done.</summary>
    public static void AtStart()
    {
        if (!Saving) { if (Total > 0) ClearAll("app", "history is off: cleared at start"); return; }
        ClearIfDue();
    }

    /// <summary>When the app closes: "clear when the app closes".</summary>
    public static void AtExit()
    {
        if (Saving && AppSettings.Current.HistoryAutoClear == AutoClear.OnExit) ClearAll("app", "cleared when the app closed");
    }

    /// <summary>When the next automatic clear is (null: none by age).</summary>
    public static DateTime? NextClearUtc
    {
        get
        {
            var s = AppSettings.Current;
            int days = AutoClear.Days(s.HistoryAutoClear);
            return !s.HistorySave || days == 0 ? null : (s.HistoryLastCleared ?? DateTime.UtcNow).AddDays(days);
        }
    }

    private static void ClearIfDue()
    {
        var s = AppSettings.Current;
        if (!s.HistorySave || AutoClear.Days(s.HistoryAutoClear) == 0) return;
        if (s.HistoryLastCleared == null) { s.HistoryLastCleared = DateTime.UtcNow; return; }   // the clock starts now
        if (NextClearUtc is { } due && DateTime.UtcNow >= due) ClearAll("app", "cleared automatically (" + AutoClear.Text(s.HistoryAutoClear) + ")");
    }

    /// <summary>For the self-test: forget the registrations it made.</summary>
    public static void RemoveWhere(Func<HistorySource, bool> match) { lock (Gate) _sources.RemoveAll(s => match(s)); }
}
