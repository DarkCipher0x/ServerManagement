using DcxManager.Services;

namespace DcxManager.Files;

/// <summary>The saved sync jobs (0.6.26, ADR-102), encrypted in syncjobs.dcx like everything else the app keeps.</summary>
public static class SyncJobStore
{
    private const string FileName = "syncjobs.dcx";
    private static List<SyncJobDef>? _all;
    private static readonly object Gate = new();

    /// <summary>A job was added, changed, removed or ran (the jobs window and the scheduler listen).</summary>
    public static event Action? Changed;

    public static IReadOnlyList<SyncJobDef> All { get { lock (Gate) return (_all ??= Load()).Select(j => j.Clone()).ToList(); } }

    public static SyncJobDef? Get(string id) { lock (Gate) return (_all ??= Load()).FirstOrDefault(j => j.Id == id)?.Clone(); }

    private static List<SyncJobDef> Load()
    {
        try { return SecureStore.IsUnlocked ? SecureStore.Read<List<SyncJobDef>>(FileName) : new(); }
        catch (Exception ex) { Log.Write("Sync jobs", ex); return new(); }
    }

    internal static void Reset() { lock (Gate) _all = null; }

    public static void Save(SyncJobDef job)
    {
        lock (Gate)
        {
            _all ??= Load();
            int i = _all.FindIndex(j => j.Id == job.Id);
            if (i >= 0) _all[i] = job.Clone(); else _all.Add(job.Clone());
            Write();
        }
        Raise();
    }

    public static void Remove(string id)
    {
        lock (Gate)
        {
            _all ??= Load();
            if (_all.RemoveAll(j => j.Id == id) == 0) return;
            Write();
        }
        Raise();
    }

    /// <summary>Records a run's time and result on the stored job (not the editor's copy).</summary>
    public static void Ran(string id, DateTime utc, string result)
    {
        lock (Gate)
        {
            _all ??= Load();
            if (_all.FirstOrDefault(j => j.Id == id) is not { } j) return;
            j.LastRunUtc = utc; j.LastResult = result;
            Write();
        }
        Raise();
    }

    private static void Write() { if (SecureStore.IsUnlocked) SecureStore.Write(FileName, _all); }

    private static void Raise() { try { Changed?.Invoke(); } catch (Exception ex) { Log.Write("Sync jobs changed", ex); } }
}
