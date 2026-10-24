using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Threading;
using DcxManager.Files;

namespace DcxManager.Services;

/// <summary>
/// Crash recovery (ADR-022, spec §56): an encrypted journal (<c>recovery.dcx</c>) of the work that would otherwise only
/// live in memory — unsaved remote-file edits and unfinished transfers. Written 2 s after a change (nothing while idle)
/// and at once on a fatal error, Windows session end and app exit; deleted when empty. What a previous run left is kept
/// (and re-journaled) until the user recovers or discards it on the "Recover previous work" screen.
/// </summary>
public static class Recovery
{
    public const string FileName = "recovery.dcx";
    public const int MaxDraftChars = 2_000_000, MaxTotalChars = 8_000_000;
    internal static TimeSpan Debounce = TimeSpan.FromSeconds(2);

    /// <summary>An editor's unsaved text: where it belongs, and a hash of the text as it was loaded (conflict check).</summary>
    public sealed class Draft
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Title { get; set; } = "";
        /// <summary><see cref="FsRef"/> as text: "local" or "host:&lt;id&gt;".</summary>
        public string Source { get; set; } = "local";
        public string Path { get; set; } = "";
        public string Text { get; set; } = "";
        public string OriginalHash { get; set; } = "";
        public DateTime Updated { get; set; }
    }

    public sealed record ItemRef(string Name, string FullPath, bool IsDir, long Length, DateTime Modified, int Mode);
    public sealed record PlanStep(ItemRef Item, string Target);

    /// <summary>An unfinished transfer job: enough to queue it again (part files are resumed).</summary>
    public sealed class Transfer
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Title { get; set; } = "";
        public string Route { get; set; } = "";
        public string From { get; set; } = "local";
        public string To { get; set; } = "local";
        public List<ItemRef> Items { get; set; } = new();
        public string DestDir { get; set; } = "";
        public bool Move { get; set; }
        public Conflict OnConflict { get; set; }
        public bool Verify { get; set; } = true;
        public bool KeepTimes { get; set; } = true;
        public string? Label { get; set; }
        public string Actor { get; set; } = "you";
        public List<PlanStep>? Plan { get; set; }
        /// <summary>Deletions a mirror sync would have made: never repeated after a crash (ADR-022), only reported.</summary>
        public int DeleteCount { get; set; }
        public string Status { get; set; } = "";
        public DateTime Started { get; set; }
    }

    public sealed class Journal
    {
        public int Version { get; set; } = 1;
        public DateTime Written { get; set; }
        public string WrittenBy { get; set; } = "";
        public List<Draft> Drafts { get; set; } = new();
        public List<Transfer> Transfers { get; set; } = new();
        [JsonIgnore] public bool IsEmpty => Drafts.Count == 0 && Transfers.Count == 0;
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Func<Draft?>> Live = new();
    private static Journal _kept = new();
    private static Timer? _timer;
    private static int _writes;

    /// <summary>What the previous run left (null when nothing). Stays set until settled.</summary>
    public static Journal? Previous { get; private set; }
    /// <summary>Journal writes this session (the self-test checks nothing is written while idle).</summary>
    public static int Writes => Volatile.Read(ref _writes);
    /// <summary>Drafts skipped because of the size caps (names only; the self-test reads it).</summary>
    public static List<string> Skipped { get; } = new();

    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>Reads what the previous run left. Called once at start, after the store is open.</summary>
    public static void LoadPrevious()
    {
        lock (Gate)
        {
            Previous = null; _kept = new();
            try
            {
                if (!File.Exists(Path.Combine(SecureStore.DataDir, FileName))) return;
                var j = SecureStore.Read<Journal>(FileName);
                if (j.IsEmpty) return;
                Previous = j; _kept = new Journal { Drafts = j.Drafts.ToList(), Transfers = j.Transfers.ToList() };
            }
            catch (Exception ex) { Log.Write("Recovery journal (unreadable; snapshots keep earlier copies)", ex); }
        }
    }

    /// <summary>Registers an open editor; <paramref name="current"/> returns its draft while it has unsaved text
    /// (null otherwise) and must not touch UI objects (it can run on any thread). Dispose when the editor closes.</summary>
    public static IDisposable TrackDraft(Func<Draft?> current)
    {
        var id = Guid.NewGuid().ToString("N");
        lock (Gate) Live[id] = current;
        return new Untrack(id);
    }

    private sealed class Untrack(string id) : IDisposable
    {
        public void Dispose() { lock (Gate) Live.Remove(id); Changed(); }
    }

    /// <summary>Something journaled changed: write within <see cref="Debounce"/>. No timer runs while nothing changes.</summary>
    public static void Changed()
    {
        if (ShuttingDown) return;   // the exit flush already wrote it; windows closing afterwards must not drop drafts
        if (!Ui.Thread.HasUi) { FlushNow(); return; }
        // One debounce timer (no UI needed): each change pushes the write back to Debounce from now.
        lock (Gate)
        {
            _timer ??= new Timer(_ => { _ = Task.Run(FlushNow); }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _timer.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>The journal as it would be written now: live drafts and unfinished jobs, plus what is kept from before.</summary>
    public static Journal Build()
    {
        var j = new Journal { Written = DateTime.Now, WrittenBy = AppInfo.Version };
        List<Func<Draft?>> live;
        lock (Gate) live = Live.Values.ToList();
        long total = 0;
        Skipped.Clear();
        foreach (var d in live.Select(f => { try { return f(); } catch { return null; } }).Where(d => d != null).Concat(_kept.Drafts))
        {
            if (d!.Text.Length > MaxDraftChars || total + d.Text.Length > MaxTotalChars) { Skipped.Add(d.Title); continue; }
            total += d.Text.Length;
            j.Drafts.Add(d);
        }
        List<TransferJob> jobs;
        try { jobs = TransferQueue.Jobs.ToList(); } catch { jobs = new(); }
        foreach (var job in jobs.Where(x => !x.Finished)) j.Transfers.Add(FromJob(job));
        j.Transfers.AddRange(_kept.Transfers);
        return j;
    }

    /// <summary>Set by the exit flush: later changes (editors closing during shutdown) are not written.</summary>
    public static bool ShuttingDown { get; private set; }

    /// <summary>The last write, at a normal exit.</summary>
    public static void FlushAtExit() { FlushNow(); ShuttingDown = true; }

    /// <summary>Writes (or, when empty, deletes) the journal now. Never throws; skipped while the store is locked.</summary>
    public static bool FlushNow()
    {
        try
        {
            lock (Gate)
            {
                if (!SecureStore.IsUnlocked) return false;
                var j = Build();
                var path = Path.Combine(SecureStore.DataDir, FileName);
                if (j.IsEmpty) { if (File.Exists(path)) File.Delete(path); }
                else SecureStore.Write(FileName, j);
                Interlocked.Increment(ref _writes);
                if (Skipped.Count > 0) Log.Write("Recovery journal", new InvalidOperationException("Too large to keep for recovery: " + string.Join(", ", Skipped)));
                return true;
            }
        }
        catch (Exception ex) { Log.Write("Recovery journal", ex); return false; }
    }

    /// <summary>Fatal error or session end: save open note/incident edits, then the journal. Best effort, bounded.</summary>
    public static void Emergency(string why)
    {
        try
        {
            // Editors and the workspace are flushed on the UI thread, but never waited for longer than 2 s: this runs on a fatal error.
            if (Ui.Thread.HasUi) Ui.Thread.TryInvoke(Ui.Window.SaveAllNow, TimeSpan.FromSeconds(2));
        }
        catch { }
        FlushNow();
        LastEmergency = why;
    }

    /// <summary>Why the last emergency flush ran (self-test, diagnostics).</summary>
    public static string? LastEmergency { get; private set; }

    /// <summary>Items recovered or discarded on the recovery screen: no longer kept.</summary>
    public static void Settle(IEnumerable<string> ids)
    {
        lock (Gate)
        {
            var set = ids.ToHashSet();
            _kept.Drafts.RemoveAll(d => set.Contains(d.Id));
            _kept.Transfers.RemoveAll(t => set.Contains(t.Id));
            if (_kept.IsEmpty) Previous = null;
        }
        Changed();
    }

    public static Transfer FromJob(TransferJob j) => new()
    {
        Title = j.Title, Route = j.Route, From = j.From.ToString(), To = j.To.ToString(), DestDir = j.DestDir, Move = j.Move, OnConflict = j.OnConflict,
        Verify = j.Verify, KeepTimes = j.KeepTimes, Label = j.Label, Actor = j.Actor, Status = j.Status, Started = j.Started,
        Items = j.Items.Select(Ref).ToList(),
        Plan = j.Plan?.Select(p => new PlanStep(Ref(p.Item), p.Target)).ToList(),
        DeleteCount = j.DeleteOnTarget?.Count ?? 0,
    };

    /// <summary>A job to queue again. Mirror deletions are dropped with a warning; finished files are resumed or re-checked.</summary>
    public static TransferJob ToJob(Transfer t)
    {
        var job = new TransferJob
        {
            From = FsRef.Parse(t.From), To = FsRef.Parse(t.To), Items = t.Items.Select(Item).ToList(), DestDir = t.DestDir, Move = t.Move,
            OnConflict = t.OnConflict, Verify = t.Verify, KeepTimes = t.KeepTimes, Label = t.Label, Actor = t.Actor,
            Plan = t.Plan?.Select(p => (Item(p.Item), p.Target)).ToList(),
        };
        if (t.DeleteCount > 0) job.Warnings.Add($"Recovered after a crash: the {t.DeleteCount} deletion(s) on the target were not repeated — run the sync again to mirror.");
        return job;
    }

    private static ItemRef Ref(FsItem i) => new(i.Name, i.FullPath, i.IsDir, i.Length, i.Modified, i.Mode);
    private static FsItem Item(ItemRef r) => new() { Name = r.Name, FullPath = r.FullPath, IsDir = r.IsDir, Length = r.Length, Modified = r.Modified, Mode = r.Mode };

    /// <summary>Test hook: forget everything in memory (the file is left alone).</summary>
    internal static void ResetForTests() { lock (Gate) { Live.Clear(); _kept = new(); Previous = null; } }
}
