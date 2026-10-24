using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.Windows;
using DcxManager.Services;

namespace DcxManager.Ai;

/// <summary>What this PC can run locally: RAM, GPUs (with dedicated VRAM), NPU, power source. Used to pick a sensible engine.</summary>
public sealed record GpuInfo(string Name, long VramBytes)
{
    public bool Discrete => VramBytes >= 3L << 30 && !Name.Contains("UHD", StringComparison.OrdinalIgnoreCase) && !Name.Contains("Iris", StringComparison.OrdinalIgnoreCase);
}

public sealed record HardwareProfile(long RamBytes, int Cores, IReadOnlyList<GpuInfo> Gpus, string? Npu, bool OnBattery, bool HasBattery)
{
    public GpuInfo? BestGpu => Gpus.OrderByDescending(g => g.VramBytes).FirstOrDefault();
    public long BestVram => BestGpu?.VramBytes ?? 0;

    /// <summary>Recommended engine id + model and a one-line reason: Foundry Local with the best model for this PC
    /// (the same choice as the default, <see cref="AiDefaults.Best"/>); a strong GPU is mentioned as an Ollama option.</summary>
    public (string Engine, string Model, string Why) Recommend()
    {
        double vram = BestVram / (double)(1L << 30), ram = RamBytes / (double)(1L << 30);
        if (Npu == null && ram < 3) return ("off", "", $"Only {ram:0} GB RAM and no NPU — leave AI off; offline digests, rule-based reports and help search still work.");
        var (model, why) = AiDefaults.Best(Npu, RamBytes, BestVram, Cores);
        if (BestGpu is { Discrete: true } && vram >= 7) why += $" Optional: {BestGpu.Name} ({vram:0} GB VRAM) can also run larger models with the Ollama · GPU engine (qwen3:{(vram >= 14 ? "14b" : "8b")}).";
        return ("npu", model, why);
    }

    public string Describe()
    {
        var gpus = Gpus.Count == 0 ? "none" : string.Join("; ", Gpus.Select(g => g.VramBytes > 0 ? $"{g.Name} ({g.VramBytes / (double)(1L << 30):0.#} GB)" : g.Name));
        return $"RAM {RamBytes / (double)(1L << 30):0} GB · {Cores} CPU threads · GPU: {gpus} · NPU: {Npu ?? "none"} · power: {(HasBattery ? OnBattery ? "battery" : "mains (laptop)" : "mains")}";
    }
}

public static class AiHardware
{
    private static Task<HardwareProfile>? _cached;

    public static Task<HardwareProfile> DetectAsync(bool refresh = false)
    {
        if (refresh || _cached == null) _cached = Task.Run(Detect);
        return _cached;
    }

    private static HardwareProfile Detect()
    {
        long ram = 0;
        var ms = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref ms)) ram = (long)ms.ullTotalPhys;
        var (onBattery, hasBattery) = Power();
        return new HardwareProfile(ram, Environment.ProcessorCount, Gpus(), Npu(), onBattery, hasBattery);
    }

    /// <summary>True when running on battery (cheap; checked before automatic AI work).</summary>
    public static bool OnBattery => Power().OnBattery;

    private static (bool OnBattery, bool HasBattery) Power()
    {
        if (!GetSystemPowerStatus(out var s)) return (false, false);
        bool has = s.BatteryFlag != 128 && s.BatteryFlag != 255;
        return (has && s.ACLineStatus == 0, has);
    }

    /// <summary>Display adapters from the registry — WMI's AdapterRAM is a 32-bit field and caps at 4 GB.</summary>
    private static List<GpuInfo> Gpus()
    {
        var list = new List<GpuInfo>();
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            foreach (var sub in cls?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                if (!sub.All(char.IsDigit)) continue;
                using var k = cls!.OpenSubKey(sub);
                var name = k?.GetValue("DriverDesc") as string;
                if (string.IsNullOrWhiteSpace(name) || name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) || name.Contains("Remote", StringComparison.OrdinalIgnoreCase)) continue;
                long vram = k!.GetValue("HardwareInformation.qwMemorySize") switch
                {
                    long l => l,
                    byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0),
                    byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b, 0),
                    int i => (uint)i,
                    _ => 0,
                };
                if (!list.Any(g => g.Name == name)) list.Add(new GpuInfo(name, vram));
            }
        }
        catch { }
        return list;
    }

    /// <summary>NPU (Intel AI Boost, AMD Ryzen AI / XDNA, Qualcomm Hexagon) via the "Neural processors"/ComputeAccelerator device class.
    /// Name matching is whole-word only: a plain substring match on "NPU" also hit "…virtual iNPUt device" (a Corsair
    /// keyboard driver) and reported an NPU that doesn't exist.</summary>
    private static string? Npu()
    {
        try
        {
            using var q = new ManagementObjectSearcher("SELECT Name, PNPClass FROM Win32_PnPEntity WHERE PNPClass = 'ComputeAccelerator' OR Name LIKE '%NPU%' OR Name LIKE '%AI Boost%' OR Name LIKE '%Hexagon%' OR Name LIKE '%Neural%'");
            q.Options.Timeout = TimeSpan.FromSeconds(5);
            foreach (ManagementObject o in q.Get())
            {
                var n = o["Name"] as string;
                if (IsNpu(n, o["PNPClass"] as string)) return n;
            }
        }
        catch { }
        return null;
    }

    /// <summary>True for a real neural-processor device: the ComputeAccelerator class, or a whole-word NPU / known NPU name.</summary>
    public static bool IsNpu(string? name, string? pnpClass)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (string.Equals(pnpClass, "ComputeAccelerator", StringComparison.OrdinalIgnoreCase)) return true;
        return System.Text.RegularExpressions.Regex.IsMatch(name, @"\bNPU\b|\bAI Boost\b|\bHexagon\b.*\b(NPU|Processor)\b|\bNeural Processing\b|\bRyzen AI\b|\bXDNA\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);
}

/// <summary>
/// The AI default (ADR-023/024): Foundry Local, never Ollama, with the best model this PC runs well — the NPU build of
/// phi-4-mini when there is an NPU, otherwise a CPU build sized to the RAM. Applied once per <see cref="Revision"/>, and
/// only to settings nobody has changed (an earlier default — Ollama · GPU untouched, or Foundry with a model an earlier
/// default set — counts as unchanged).
/// </summary>
public static class AiDefaults
{
    /// <summary>1 = 0.1.5 (phi-4-mini / lightweight CPU), 2 = 0.1.6 (best model for the hardware, as x64 CPU ids),
    /// 3 = 0.1.8 (aliases: Foundry Local's own catalogue picks the build for THIS PC — NPU, GPU or CPU, x64 or ARM64; ADR-026),
    /// 4 = 0.3.0 (phi-4-mini wherever it runs: NPU, then a 4 GB+ GPU, then 8 GB+ RAM),
    /// 5 = 0.5.1 (#14, DarkCipher0x: phi-4-mini on every PC; a lighter model for small PCs is only suggested, never chosen),
    /// 6 = 0.6.17 (DarkCipher0x: "change the LLM model if there is something better … supports all CPU/NPU/GPU … whatever is
    /// best suited for the PC"): the strongest tool-calling model the PC runs well — Qwen2.5 14B / 7B on a GPU with 12 / 6
    /// GB+, phi-4-mini on an NPU, a smaller GPU or most CPUs, Qwen2.5 7B on a big CPU, lighter ones on small PCs.</summary>
    public const int Revision = 6;
    public const string NpuModel = "phi-4-mini";
    public const string CpuLargeModel = "Phi-4-mini-instruct-generic-cpu";      // revision 2's id (recognised as a default)
    public const string CpuMediumModel = "qwen2.5-1.5b-instruct-generic-cpu";   // revision 2's id (recognised as a default)
    public const string LargeAlias = "phi-4-mini", MediumAlias = "qwen2.5-1.5b", LightAlias = "qwen2.5-0.5b";
    public const string GpuAlias = "qwen2.5-7b", BigGpuAlias = "qwen2.5-14b";

    /// <summary>Models an earlier default may have set (switching between them is not a user's choice we'd overwrite).</summary>
    private static readonly string[] DefaultModels = { NpuModel, CpuLargeModel, CpuMediumModel, AiClient.FoundryLightCpuModel, MediumAlias, LightAlias, GpuAlias, BigGpuAlias };

    /// <summary>Defaults meant for a GPU: loaded as a CPU build they would crawl, so set-up takes the catalogue's own pick.</summary>
    public static bool ForGpu(string alias) => alias.Equals(GpuAlias, StringComparison.OrdinalIgnoreCase) || alias.Equals(BigGpuAlias, StringComparison.OrdinalIgnoreCase);

    /// <summary>The best Foundry Local model for this hardware, as an alias (Foundry Local picks the GPU, NPU or CPU build on
    /// the PC itself), and why. Tool calling decides it (the AI Engineer and operator act through tools; phi-4-mini often
    /// writes calls as text or refuses), so a GPU gets Qwen2.5 first; an NPU runs phi-4-mini at low power; a CPU gets
    /// phi-4-mini unless it is big enough for Qwen2.5 7B. Not every PC has an NPU or a GPU: smaller PCs get a lighter model.</summary>
    public static (string Model, string Why) Best(string? npu, long ramBytes, long vramBytes = 0, int cores = 0)
    {
        double ram = ramBytes / (double)(1L << 30), vram = vramBytes / (double)(1L << 30);
        if (vram >= 12) return (BigGpuAlias, $"A GPU with {vram:0} GB of video memory runs Qwen2.5 14B, the most reliable tool-calling model Foundry Local offers for it (about 9 GB to download, once).");
        if (vram >= 6) return (GpuAlias, $"A GPU with {vram:0} GB of video memory runs Qwen2.5 7B, which calls tools reliably (about 5 GB to download, once).");
        if (npu != null) return (LargeAlias, $"{npu} found: phi-4-mini runs on the NPU at low power.");
        if (vram >= 4) return (LargeAlias, $"No NPU; a GPU with {vram:0} GB of video memory runs phi-4-mini (3.8B).");
        if (ram >= 30 && cores >= 12) return (GpuAlias, $"No NPU or large GPU, but {ram:0} GB RAM and {cores} CPU threads run Qwen2.5 7B on the CPU: slower answers, reliable tool calls.");
        if (ram >= 7.5) return (LargeAlias, $"No NPU or GPU for it; {ram:0} GB RAM runs phi-4-mini (3.8B) on the CPU (about 5 GB of RAM while it answers).");
        if (ram >= 5.5) return (MediumAlias, $"No NPU and {ram:0} GB RAM: too little for phi-4-mini, so Qwen2.5 1.5B (about 2 GB of RAM).");
        return (LightAlias, $"No NPU and {ram:0} GB RAM: the lightweight Qwen2.5 0.5B (about 1 GB of RAM).");
    }

    /// <summary>Applies the default to <paramref name="s"/> for this hardware. True when it changed anything (incl. the bookkeeping).</summary>
    public static bool Choose(AppSettings s, string? npu, long ramBytes, long vramBytes = 0, int cores = 0)
    {
        if (s.AiDefaultRevision >= Revision) return false;
        bool firstTime = !s.AiDefaultChosen;
        s.AiDefaultChosen = true;
        s.AiDefaultRevision = Revision;
        bool untouched = s.AiEngine == "npu" && s.FoundryUrl.Length == 0 && DefaultModels.Contains(s.FoundryModel, StringComparer.OrdinalIgnoreCase)
            || firstTime && s.AiEngine == "ollama-gpu" && s.OllamaModel == "qwen2.5:7b" && s.OllamaUrl == "http://localhost:11434" && s.OllamaTunnelHostId == null;
        if (!untouched) return true;
        s.AiEngine = "npu";
        // 0.6.17 (revision 6): the best model for this PC; a lighter one than phi-4-mini is still only suggested, never
        // chosen (0.5.1 #14), so a small PC keeps phi-4-mini.
        var best = Best(npu, ramBytes, vramBytes, cores).Model;
        s.FoundryModel = best is MediumAlias or LightAlias ? LargeAlias : best;
        return true;
    }

    /// <summary>Detects the hardware off the UI thread, then applies <see cref="Choose"/> and saves.</summary>
    public static async Task ApplyAsync()
    {
        try
        {
            if (AppSettings.Current.AiDefaultRevision >= Revision) return;
            var hw = await AiHardware.DetectAsync();
            await Ui.Thread.InvokeAsync(() =>
            {
                if (Choose(AppSettings.Current, hw.Npu, hw.RamBytes, hw.BestVram, hw.Cores)) AppSettings.Save();
            });
        }
        catch (Exception ex) { Log.Write("AI default", ex); }
    }
}
