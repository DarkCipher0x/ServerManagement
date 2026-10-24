using System.IO;
using System.Text.Json;

namespace DcxManager.Services;

/// <summary>
/// Crash/workspace recovery. A marker file is written at start-up and deleted on a clean exit; if it is still there
/// next time, the last run ended unexpectedly. The set of open tabs (saved hosts only) is persisted so they can be
/// reopened — terminals come back disconnected, never auto-reconnected blindly.
/// </summary>
public static class Workspace
{
    /// <summary>One open tab. Since ADR-022 also where it was: the main pane's position (or the pop-out window's number
    /// when <see cref="Floating"/>), whether it was the active tab, and its tab group. Older files simply lack them.</summary>
    public sealed record Entry(string Kind, string HostId)
    {
        public int Pane { get; init; }
        public bool Floating { get; init; }
        /// <summary>0.6.4: the pane inside its extra window (windows split too); 0 for older files.</summary>
        public int WindowPane { get; init; }
        public bool Active { get; init; }
        public string? Group { get; init; }
    }

    private static string Dir => SecureStore.DataDir;   // isolated under DCX_DATA_DIR in tests
    private static string MarkerPath => Path.Combine(Dir, "running.marker");
    private const string StateFile = "workspace.dcx";   // encrypted like everything else

    /// <summary>True when the previous run did not exit cleanly.</summary>
    public static bool UncleanLastExit { get; private set; }

    public static void BeginSession()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            UncleanLastExit = File.Exists(MarkerPath);
            File.WriteAllText(MarkerPath, DateTime.Now.ToString("o"));
        }
        catch { }
    }

    public static void EndSessionCleanly()
    {
        try { if (File.Exists(MarkerPath)) File.Delete(MarkerPath); } catch { }
    }

    public static void Save(IEnumerable<Entry> entries)
    {
        try
        {
            SecureStore.Write(StateFile, entries.ToList());
            var legacy = Path.Combine(Dir, "workspace.json");
            if (File.Exists(legacy)) File.Delete(legacy);
        }
        catch { }
    }

    public static List<Entry> Load()
    {
        try { return SecureStore.Read<List<Entry>>(StateFile); }
        catch { return new(); }
    }
}
