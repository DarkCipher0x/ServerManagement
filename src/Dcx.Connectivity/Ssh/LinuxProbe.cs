using System.Globalization;
using DcxManager.Services;

namespace DcxManager.Ssh;

public sealed class DiskInfo
{
    public string Mount { get; set; } = "";
    public long Size { get; set; }
    public long Used { get; set; }
    public double Percent => Size > 0 ? Used * 100.0 / Size : 0;
    public string Text => $"{Mount}  {Fmt.Bytes(Used)} / {Fmt.Bytes(Size)}  ({Percent:0}%)";
}

/// <summary>Snapshot of a Linux host gathered in a single SSH round trip.</summary>
public sealed class ServerFacts
{
    public string Hostname { get; set; } = "";
    public string Os { get; set; } = "";
    public string Kernel { get; set; } = "";
    public string Arch { get; set; } = "";
    public TimeSpan Uptime { get; set; }
    public string Load { get; set; } = "";
    public int Cpus { get; set; }
    public string CpuModel { get; set; } = "";
    public double CpuPercent { get; set; }
    public long MemTotal { get; set; }
    public long MemAvailable { get; set; }
    public long SwapTotal { get; set; }
    public long SwapUsed { get; set; }
    public List<DiskInfo> Disks { get; set; } = new();
    public List<string> Addresses { get; set; } = new();
    public string PackageManager { get; set; } = "";
    public bool HasDocker { get; set; }
    public bool HasSystemd { get; set; }
    public bool RebootRequired { get; set; }
    public List<string> FailedUnits { get; set; } = new();
    public string Virtualization { get; set; } = "";
    public string LoggedIn { get; set; } = "";

    public double MemPercent => MemTotal > 0 ? (MemTotal - MemAvailable) * 100.0 / MemTotal : 0;

    public string Summary() =>
        $"Host: {Hostname}\nOS: {Os} (kernel {Kernel}, {Arch}{(Virtualization.Length > 0 ? ", " + Virtualization : "")})\n" +
        $"Uptime: {Fmt.Duration(Uptime)}  Load: {Load}  CPUs: {Cpus} ({CpuModel})  CPU used: {CpuPercent:0}%\n" +
        $"Memory: {Fmt.Bytes(MemTotal - MemAvailable)} / {Fmt.Bytes(MemTotal)} ({MemPercent:0}%)  Swap: {Fmt.Bytes(SwapUsed)} / {Fmt.Bytes(SwapTotal)}\n" +
        $"Disks: {string.Join("; ", Disks.Select(d => d.Text))}\n" +
        $"Addresses: {string.Join(", ", Addresses)}\n" +
        $"Packages: {PackageManager}  Docker: {(HasDocker ? "yes" : "no")}  Reboot required: {(RebootRequired ? "YES" : "no")}\n" +
        $"Failed units: {(FailedUnits.Count == 0 ? "none" : string.Join(", ", FailedUnits))}";
}

public static class LinuxProbe
{
    // POSIX sh; every section is marked so missing tools just leave a section empty.
    private const string Script = """
        export LC_ALL=C
        echo '##HOST'; hostname 2>/dev/null
        echo '##OS'; (. /etc/os-release 2>/dev/null && echo "$PRETTY_NAME") || uname -s
        echo '##KERNEL'; uname -r; echo '##ARCH'; uname -m
        echo '##UPTIME'; cut -d' ' -f1 /proc/uptime
        echo '##LOAD'; cut -d' ' -f1-3 /proc/loadavg
        echo '##CPUS'; nproc 2>/dev/null
        echo '##CPUMODEL'; grep -m1 'model name' /proc/cpuinfo 2>/dev/null | cut -d: -f2-
        echo '##STAT1'; head -1 /proc/stat; sleep 0.4; echo '##STAT2'; head -1 /proc/stat
        echo '##MEM'; grep -E '^(MemTotal|MemAvailable|SwapTotal|SwapFree):' /proc/meminfo
        echo '##DISK'; df -P -B1 -x tmpfs -x devtmpfs -x overlay -x squashfs -x efivarfs 2>/dev/null | tail -n +2
        echo '##IP'; ip -o -4 addr show scope global 2>/dev/null | awk '{print $2" "$4}'; ip -o -6 addr show scope global 2>/dev/null | awk '{print $2" "$4}'
        echo '##PKG'; for p in apt dnf yum zypper pacman apk; do command -v $p >/dev/null 2>&1 && { echo $p; break; }; done
        echo '##DOCKER'; command -v docker >/dev/null 2>&1 && echo yes
        echo '##SYSTEMD'; command -v systemctl >/dev/null 2>&1 && echo yes
        echo '##REBOOT'; { [ -f /var/run/reboot-required ] || { command -v needs-restarting >/dev/null 2>&1 && ! needs-restarting -r >/dev/null 2>&1; }; } && echo yes
        echo '##FAILED'; systemctl --failed --no-legend --plain 2>/dev/null | awk '{print $1}'
        echo '##VIRT'; systemd-detect-virt 2>/dev/null
        echo '##WHO'; who 2>/dev/null | wc -l
        echo '##END'
        """;

    public static async Task<ServerFacts> ProbeAsync(HostEntry host, CancellationToken ct = default)
    {
        var r = await SshPool.RunAsync(host, Script, false, ct, TimeSpan.FromSeconds(30));
        return Parse(r.Output);
    }

    public static ServerFacts Parse(string output)
    {
        var sections = new Dictionary<string, List<string>>();
        List<string>? cur = null;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("##")) { cur = new List<string>(); sections[line[2..]] = cur; continue; }
            if (cur != null && line.Trim().Length > 0) cur.Add(line);
        }
        string One(string k) => sections.TryGetValue(k, out var l) && l.Count > 0 ? l[0].Trim() : "";
        List<string> All(string k) => sections.TryGetValue(k, out var l) ? l : new List<string>();

        var f = new ServerFacts
        {
            Hostname = One("HOST"), Os = One("OS"), Kernel = One("KERNEL"), Arch = One("ARCH"),
            Load = One("LOAD"), CpuModel = One("CPUMODEL"), PackageManager = One("PKG"),
            HasDocker = One("DOCKER") == "yes", HasSystemd = One("SYSTEMD") == "yes", RebootRequired = One("REBOOT") == "yes",
            Virtualization = One("VIRT") is var v && v != "none" ? v : "",
            FailedUnits = All("FAILED").Select(s => s.Trim()).Where(s => s.Length > 0).ToList(),
            Addresses = All("IP").Select(s => s.Trim()).ToList(),
            LoggedIn = One("WHO"),
        };
        if (double.TryParse(One("UPTIME"), NumberStyles.Float, CultureInfo.InvariantCulture, out var up)) f.Uptime = TimeSpan.FromSeconds(up);
        int.TryParse(One("CPUS"), out var cpus); f.Cpus = cpus;

        // CPU % from two /proc/stat samples
        var a = Ticks(One("STAT1")); var b = Ticks(One("STAT2"));
        if (a != null && b != null)
        {
            long total = b.Value.Total - a.Value.Total, idle = b.Value.Idle - a.Value.Idle;
            if (total > 0) f.CpuPercent = Math.Clamp((total - idle) * 100.0 / total, 0, 100);
        }

        foreach (var l in All("MEM"))
        {
            var parts = l.Split(':', 2);
            if (parts.Length < 2) continue;
            var kb = long.TryParse(parts[1].Trim().Split(' ')[0], out var n) ? n * 1024 : 0;
            switch (parts[0])
            {
                case "MemTotal": f.MemTotal = kb; break;
                case "MemAvailable": f.MemAvailable = kb; break;
                case "SwapTotal": f.SwapTotal = kb; break;
                case "SwapFree": f.SwapUsed = f.SwapTotal - kb; break;
            }
        }

        foreach (var l in All("DISK"))
        {
            var p = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 6) continue;
            long.TryParse(p[1], out var size); long.TryParse(p[2], out var used);
            if (size <= 0) continue;
            f.Disks.Add(new DiskInfo { Mount = p[5], Size = size, Used = used });
        }
        return f;
    }

    private static (long Total, long Idle)? Ticks(string line)
    {
        var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 5 || p[0] != "cpu") return null;
        var n = p.Skip(1).Select(x => long.TryParse(x, out var v) ? v : 0).ToArray();
        long idle = n[3] + (n.Length > 4 ? n[4] : 0);
        return (n.Sum(), idle);
    }
}

public static class Fmt
{
    public static string Bytes(long b)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB", "PB" };
        double v = b; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{b} B" : $"{v:0.#} {u[i]}";
    }

    public static string Duration(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h" : t.TotalHours >= 1 ? $"{t.Hours}h {t.Minutes}m" : $"{t.Minutes}m {t.Seconds}s";
}
