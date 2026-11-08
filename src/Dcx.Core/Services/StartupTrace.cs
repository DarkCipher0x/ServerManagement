using System.Diagnostics;
using System.IO;

namespace DcxManager.Services;

/// <summary>An opt-in start-up timeline (0.6.32): with DCX_STARTUP_TRACE=&lt;file&gt; in the environment, every phase of the
/// start writes one line (ms since the process started, the step's own ms, the working set, the assemblies that step
/// loaded) and the file is written once the first frame has rendered and the window has gone idle. DCX_STARTUP_TRACE_STACKS=1
/// adds the call stack behind every assembly load (slow, for finding what pulls a module in). Without the variable a mark
/// is one static null check, so the timeline costs nothing in a normal start. Read it when a start feels slow, before
/// reaching for a profiler.</summary>
public static class StartupTrace
{
    private static readonly string? Target = Environment.GetEnvironmentVariable("DCX_STARTUP_TRACE") is { Length: > 0 } f ? f : null;
    private static readonly List<string> Lines = new();
    private static readonly long Origin = Target == null ? 0
        : Stopwatch.GetTimestamp() - (long)((DateTime.Now - Process.GetCurrentProcess().StartTime).TotalSeconds * Stopwatch.Frequency);
    private static long _last;
    private static readonly HashSet<string> Seen = new();
    private static readonly List<string> Loads = new();
    private static bool _stacks;

    public static bool On => Target != null;

    static StartupTrace()
    {
        if (Target == null || Environment.GetEnvironmentVariable("DCX_STARTUP_TRACE_STACKS") != "1") return;
        _stacks = true;
        AppDomain.CurrentDomain.AssemblyLoad += (_, e) =>
        {
            var frames = new StackTrace(1, false).GetFrames()
                .Select(fr => fr.GetMethod() is { } m ? $"{m.DeclaringType?.FullName}.{m.Name}" : "?")
                .Where(s => !s.StartsWith("System.Runtime.Loader.") && !s.StartsWith("System.Reflection.RuntimeAssembly") && !s.StartsWith("System.AppDomain"))
                .Take(14);
            lock (Loads) Loads.Add($"load {e.LoadedAssembly.GetName().Name}:\n      " + string.Join("\n      ", frames));
        };
    }

    /// <summary>Records that a phase has just finished (any thread), with the assemblies that phase loaded.</summary>
    public static void Mark(string what)
    {
        if (Target == null) return;
        long now = Stopwatch.GetTimestamp();
        double ms = (now - Origin) * 1000.0 / Stopwatch.Frequency;
        double step = _last == 0 ? ms : (now - _last) * 1000.0 / Stopwatch.Frequency;
        _last = now;
        var fresh = new List<string>();
        lock (Seen)
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                if (Seen.Add(a.GetName().Name ?? "?")) fresh.Add(a.GetName().Name ?? "?");
        fresh.Sort(StringComparer.OrdinalIgnoreCase);
        lock (Lines) Lines.Add($"{ms,7:N0} ms  +{step,5:N0}  {Environment.WorkingSet >> 20,4} MB  {what}" + (fresh.Count > 0 ? $"\n{"",26}loaded: {string.Join(", ", fresh)}" : ""));
    }

    /// <summary>Writes what was recorded so far (called at the first frame and again when the window is idle, with a
    /// summary of the window and the process).</summary>
    public static void Flush(string? summary = null)
    {
        if (Target == null) return;
        try
        {
            var lines = new List<string>();
            lock (Lines) lines.AddRange(Lines);
            if (summary != null)
            {
                var p = Process.GetCurrentProcess();
                lines.Add($"{summary}; {p.PrivateMemorySize64 >> 20} MB private, {p.WorkingSet64 >> 20} MB working set, {p.Threads.Count} threads, {p.HandleCount} handles");
                var names = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name ?? "?").OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                lines.Add($"assemblies loaded ({names.Count}): " + string.Join(", ", names));
            }
            if (_stacks) lock (Loads) lines.AddRange(Loads);
            File.WriteAllLines(Target, lines);
        }
        catch { }
    }
}
