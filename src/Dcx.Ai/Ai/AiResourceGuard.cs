using System.Runtime.InteropServices;
using DcxManager.Services;

namespace DcxManager.Ai;

public enum AiPressure { Ok, High, Critical }

/// <summary>What the guard saw: the level, the commit charge (used / limit), physical memory load, the part of the app's
/// Ollama model that spilled out of GPU memory, and one line for the user.</summary>
public sealed record AiResourceState(AiPressure Level, ulong CommitUsed, ulong CommitLimit, uint MemoryLoad, long OllamaSpill, string Text)
{
    public double CommitRatio => CommitLimit == 0 ? 0 : CommitUsed / (double)CommitLimit;
}

/// <summary>
/// Keeps local AI from starving the app and the PC (ADR-034). On 2026-10-19 DarkCipher0x's app froze in the AI operator: Ollama's
/// gpt-oss:20b at a 64K context held 12 GB of the 16 GB GPU, Foundry Local held 6.7 GB more, GPU memory overflowed by
/// 7.6 GB into system RAM and the commit charge reached 60 of 63 GB. The guard:
/// <list type="bullet">
/// <item>reads the commit charge (and, for Ollama, how much of the loaded model is outside GPU memory, /api/ps);</item>
/// <item>under pressure caps the context it asks for (High 8K, Critical 4K) and asks Ollama to unload the model after the
/// answer (keep_alive 0) instead of keeping it warm;</item>
/// <item>keeps the model warm only while the user is using the AI (<see cref="AppSettings.AiKeepWarmMinutes"/>);</item>
/// <item>unloads Foundry Local's loaded models when memory is tight and Foundry isn't the engine in use, or when it is but
/// the app's own use has been idle past the keep-warm time (<c>foundry model unload</c>; the service keeps running).</item>
/// </list>
/// It never stops or kills an Ollama or Foundry process, and never unloads an Ollama model other than the app's own.
/// </summary>
public static class AiResourceGuard
{
    public const double HighCommit = 0.85, CriticalCommit = 0.93;
    public const long SpillLimit = 1L << 30;

    private static AppSettings S => AppSettings.Current;

    /// <summary>Test seam: (commit used, commit limit, memory load %).</summary>
    internal static Func<(ulong Used, ulong Limit, uint Load)> Memory = ReadMemory;
    /// <summary>Test seam: the bytes of the app's Ollama model outside GPU memory (null = unknown / not Ollama).</summary>
    internal static Func<CancellationToken, Task<long?>> OllamaSpill = ReadOllamaSpillAsync;

    private static long _spill;
    private static DateTime _spillAt;

    private static DateTime _lastUse = DateTime.MinValue;
    /// <summary>The user used the AI (typed, sent, opened a chat).</summary>
    public static void Touch() => _lastUse = DateTime.UtcNow;
    internal static void SetLastUse(DateTime utc) => _lastUse = utc;
    public static bool UserActive => DateTime.UtcNow - _lastUse < TimeSpan.FromMinutes(Math.Max(1, S.AiKeepWarmMinutes));

    /// <summary>Raised when the guard did something the user should know about (any thread).</summary>
    public static event Action<string>? Notice;
    public static string LastNotice { get; private set; } = "";
    private static void Say(string s) { LastNotice = s; Log.Write("AI resource guard: " + s, null); try { Notice?.Invoke(s); } catch { } }

    public static AiResourceState Check()
    {
        var (used, limit, load) = Memory();
        double r = limit == 0 ? 0 : used / (double)limit;
        long spill = DateTime.UtcNow - _spillAt < TimeSpan.FromMinutes(2) ? _spill : 0;
        var level = !S.AiResourceGuard ? AiPressure.Ok
            : r >= CriticalCommit ? AiPressure.Critical
            : r >= HighCommit || spill > SpillLimit ? AiPressure.High : AiPressure.Ok;
        string gb(ulong b) => (b / (double)(1UL << 30)).ToString("0.#");
        var text = $"commit {gb(used)} of {gb(limit)} GB ({r:P0}), memory load {load}%" + (spill > 0 ? $", {spill / (double)(1L << 30):0.#} GB of the model outside GPU memory" : "");
        return new AiResourceState(level, used, limit, load, spill, text);
    }

    /// <summary>The context to ask for: unchanged normally; capped under pressure.</summary>
    public static int CapContext(int wanted)
    {
        var st = Check();
        int cap = st.Level switch { AiPressure.Critical => 4096, AiPressure.High => 8192, _ => int.MaxValue };
        if (wanted > cap) { Say($"memory is tight ({st.Text}): this answer uses a {cap / 1024}K context instead of {wanted / 1024}K."); return cap; }
        return wanted;
    }

    /// <summary>Ollama keep_alive: warm for the keep-warm minutes while the user is active; unload right after the answer
    /// under pressure or when keep-warm is 0.</summary>
    public static string KeepAlive()
    {
        if (Check().Level != AiPressure.Ok || S.AiKeepWarmMinutes <= 0) return "0";
        return $"{Math.Clamp(S.AiKeepWarmMinutes, 1, 120)}m";
    }

    // ---- background check -------------------------------------------------------------------------------------------

    private static Timer? _timer;
    private static int _ticking;

    /// <summary>Starts the minute-by-minute check (not in the self-test, which calls <see cref="TickAsync"/> itself).</summary>
    public static void Start()
    {
        if (TestMode.Running || _timer != null) return;
        _timer = new Timer(_ => _ = TickAsync(CancellationToken.None), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60));
    }

    /// <summary>Test seam: runs the foundry CLI (defaults to <see cref="AiClient.FoundryCli"/>, which the self-test fakes).</summary>
    internal static Func<string, TimeSpan, CancellationToken, Task<(int Code, string Output)>> Foundry = (a, t, ct) => AiClient.FoundryCli(a, t, ct);

    /// <summary>One check: refresh the Ollama spill reading, and unload idle Foundry Local models when the rules say so.
    /// Returns what it did (for the self-test and the log).</summary>
    public static async Task<string> TickAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return "busy";
        try
        {
            if (!S.AiResourceGuard) return "off";
            try { if (await OllamaSpill(ct) is { } sp) { _spill = sp; _spillAt = DateTime.UtcNow; } }
            catch (Exception ex) when (!ct.IsCancellationRequested) { Log.Write("AI guard: ollama ps", ex); }
            var st = Check();
            if (!S.AiUnloadIdleFoundry || S.FoundryUrl.Trim().Length > 0) return "checked: " + st.Text;
            bool foundryInUse = AiClient.Engine == "npu";
            bool unload = st.Level != AiPressure.Ok && !(foundryInUse && UserActive)
                          || foundryInUse && !UserActive && _lastUse != DateTime.MinValue;
            if (!unload) return "checked: " + st.Text;
            var (code, output) = await Foundry("model list --loaded -o json", TimeSpan.FromSeconds(30), ct);
            if (code != 0) return "foundry not available";
            if (FoundryManager.ParseJson(output) is not { } node) return "foundry list unreadable";
            var loaded = FoundryManager.ParseCatalog(node).Select(m => m.Id).ToList();
            if (loaded.Count == 0) return "checked: nothing loaded in Foundry Local";
            var done = new List<string>();
            foreach (var id in loaded)
            {
                var (c, _) = await Foundry("model unload " + id, TimeSpan.FromSeconds(60), ct);
                if (c == 0) done.Add(id);
            }
            if (foundryInUse) AiClient.ForgetFoundry();
            if (done.Count > 0)
            {
                Say($"unloaded Foundry Local model(s) {string.Join(", ", done)} — {(st.Level != AiPressure.Ok ? "memory is tight (" + st.Text + ")" : "the AI was idle")}. Foundry Local itself keeps running; the next question loads its model again.");
                AuditLog.Write("app", "ai", "resource guard unloaded Foundry Local model(s)", string.Join(", ", done));
            }
            return "unloaded: " + string.Join(", ", done);
        }
        finally { Interlocked.Exchange(ref _ticking, 0); }
    }

    private static async Task<long?> ReadOllamaSpillAsync(CancellationToken ct)
    {
        if (AiClient.Engine is not ("ollama-gpu" or "ollama-cpu") || TestMode.Running) return null;
        using var t = CancellationTokenSource.CreateLinkedTokenSource(ct);
        t.CancelAfter(5000);
        var loaded = await AiClient.OllamaLoadedAsync(t.Token);
        var mine = loaded.FirstOrDefault(m => m.Name == AiClient.DefaultModel || m.Name == AiClient.DefaultModel + ":latest");
        return mine.Name == null ? 0 : Math.Max(0, mine.Size - mine.Vram);
    }

    // ---- Win32 -------------------------------------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    private static (ulong, ulong, uint) ReadMemory()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref m)) return (0, 0, 0);
        // "Page file" here is the commit limit (RAM + page files); used = limit − available.
        return (m.ullTotalPageFile - m.ullAvailPageFile, m.ullTotalPageFile, m.dwMemoryLoad);
    }
}
