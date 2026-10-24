using System.Globalization;
using System.Text.RegularExpressions;

namespace DcxManager.Net;

/// <summary>
/// A remote performance snapshot of a Linux host (0.3.0 §20: dashboard performance tiles): one command over SSH reads
/// the load average, core count, memory and the root filesystem, and this parses the answer. No tools beyond coreutils
/// and procps are needed; each part is optional so a missing one doesn't fail the rest.
/// </summary>
public static class HostPerf
{
    public const string Command = "echo LOAD $(cut -d' ' -f1-3 /proc/loadavg); echo CORES $(nproc 2>/dev/null || grep -c ^processor /proc/cpuinfo); " +
                                  "echo MEM $(awk '/MemTotal/{t=$2}/MemAvailable/{a=$2}END{print t, a}' /proc/meminfo); echo DISK $(df -P / | awk 'NR==2{print $2, $4}'); echo UP $(cut -d' ' -f1 /proc/uptime)";

    public sealed record Snapshot(double? Load1, double? Load5, double? Load15, int? Cores, long? MemTotalKb, long? MemAvailKb, long? DiskTotalKb, long? DiskAvailKb, double? UptimeSeconds)
    {
        public double? MemPercent => MemTotalKb is > 0 && MemAvailKb is { } a ? 100.0 * (MemTotalKb.Value - a) / MemTotalKb.Value : null;
        public double? DiskPercent => DiskTotalKb is > 0 && DiskAvailKb is { } a ? 100.0 * (DiskTotalKb.Value - a) / DiskTotalKb.Value : null;
        /// <summary>Load per core as a percent (100 = every core busy on average).</summary>
        public double? LoadPercent => Load1 is { } l && Cores is > 0 ? 100.0 * l / Cores.Value : null;
        public double? Worst => new[] { MemPercent, DiskPercent, LoadPercent }.Where(x => x != null).DefaultIfEmpty(null).Max();
        public string Headline => string.Join(" · ", new[]
        {
            Load1 is { } l ? $"load {l:0.00}" + (Cores is { } c ? $"/{c}" : "") : null,
            MemPercent is { } m ? $"mem {m:0}%" : null,
            DiskPercent is { } d ? $"disk {d:0}%" : null,
            UptimeSeconds is { } u ? "up " + (u >= 86400 ? $"{u / 86400:0}d" : $"{u / 3600:0}h") : null,
        }.Where(x => x != null));
    }

    public static Snapshot Parse(string output)
    {
        double? L(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
        long? K(string s) => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
        string[] Line(string key) => Regex.Match(output, $@"^{key}\s+(.*)$", RegexOptions.Multiline) is { Success: true } m ? m.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries) : [];
        var load = Line("LOAD"); var cores = Line("CORES"); var mem = Line("MEM"); var disk = Line("DISK"); var up = Line("UP");
        return new Snapshot(load.Length > 0 ? L(load[0]) : null, load.Length > 1 ? L(load[1]) : null, load.Length > 2 ? L(load[2]) : null,
            cores.Length > 0 && int.TryParse(cores[0], out var c) ? c : null,
            mem.Length > 0 ? K(mem[0]) : null, mem.Length > 1 ? K(mem[1]) : null,
            disk.Length > 0 ? K(disk[0]) : null, disk.Length > 1 ? K(disk[1]) : null,
            up.Length > 0 ? L(up[0]) : null);
    }
}
