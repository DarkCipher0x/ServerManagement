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
        /// <summary>0.6.31: the tab's group was stacked into one tab.</summary>
        public bool GroupStacked { get; init; }
        /// <summary>0.6.29: a terminal's working folder (from OSC 7 or its prompt), typed back after the reconnect.</summary>
        public string? Cwd { get; init; }
        /// <summary>0.6.29: the tmux session the terminal was in; re-attached on every reconnect.</summary>
        public string? Tmux { get; init; }
        /// <summary>0.6.29: Files: the folder. Commander: the right pane's place ("host:id|/path"), <see cref="LeftPath"/> the left's.</summary>
        public string? Path { get; init; }
        public string? LeftPath { get; init; }
        /// <summary>0.6.29: a quick-connect (unsaved) host. Its settings only: the secret was never part of it.</summary>
        public HostEntry? AdHoc { get; init; }
    }

    /// <summary>A named set of tabs (0.6.29): "Homelab", "VPS"… opened from the title bar's Workspaces menu.</summary>
    public sealed class Named
    {
        public string Name { get; set; } = "";
        public DateTime Saved { get; set; }
        public List<Entry> Entries { get; set; } = new();
    }

    private sealed class NamedFile
    {
        public List<Named> Workspaces { get; set; } = new();
        /// <summary>Removed workspaces are kept here, never deleted outright.</summary>
        public List<Named> Archived { get; set; } = new();
    }

    private const string ExitFile = "workspace-exit.dcx";
    private const string NamedStore = "workspaces.dcx";

    /// <summary>What was open when you chose "Save and exit" (or exit saves by itself): reopened at the next start.</summary>
    public static void SaveOnExit(IEnumerable<Entry> entries)
    {
        try { SecureStore.Write(ExitFile, entries.ToList()); } catch { }
    }

    /// <summary>The tabs saved at the last exit, once: taking them clears the file, so they come back one time only.</summary>
    public static List<Entry> TakeSavedOnExit()
    {
        try
        {
            if (!File.Exists(Path.Combine(Dir, ExitFile))) return new();
            var list = SecureStore.Read<List<Entry>>(ExitFile) ?? new();
            SecureStore.Write(ExitFile, new List<Entry>());
            return list;
        }
        catch { return new(); }
    }

    private static NamedFile ReadNamed()
    {
        try { return File.Exists(Path.Combine(Dir, NamedStore)) ? SecureStore.Read<NamedFile>(NamedStore) ?? new() : new(); }
        catch { return new(); }
    }

    public static IReadOnlyList<Named> AllNamed() => ReadNamed().Workspaces.OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Saves (or replaces) the workspace called <paramref name="name"/>.</summary>
    public static void SaveNamed(string name, IEnumerable<Entry> entries)
    {
        var f = ReadNamed();
        f.Workspaces.RemoveAll(w => w.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        f.Workspaces.Add(new Named { Name = name.Trim(), Saved = DateTime.Now, Entries = entries.ToList() });
        SecureStore.Write(NamedStore, f);
    }

    public static void RenameNamed(string from, string to)
    {
        var f = ReadNamed();
        if (f.Workspaces.FirstOrDefault(w => w.Name.Equals(from, StringComparison.OrdinalIgnoreCase)) is not { } w) return;
        f.Workspaces.RemoveAll(x => x != w && x.Name.Equals(to, StringComparison.OrdinalIgnoreCase));
        w.Name = to.Trim();
        SecureStore.Write(NamedStore, f);
    }

    /// <summary>Removes a workspace from the menu; it is archived in the same encrypted file, not deleted.</summary>
    public static void RemoveNamed(string name)
    {
        var f = ReadNamed();
        var gone = f.Workspaces.Where(w => w.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (gone.Count == 0) return;
        f.Workspaces.RemoveAll(gone.Contains);
        f.Archived.AddRange(gone);
        SecureStore.Write(NamedStore, f);
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
