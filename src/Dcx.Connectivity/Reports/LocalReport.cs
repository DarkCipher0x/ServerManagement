using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using DcxManager.Services;
using DcxManager.Ssh;
using Microsoft.Win32;

namespace DcxManager.Reports;

/// <summary>What a local evidence source is: id, title, default on, and whether it needs administrator rights.</summary>
public sealed record LocalSource(string Id, string Title, bool Default, bool Admin = false);

/// <summary>The facts about this PC the rules look at (public for the self-test: rules run on canned facts).</summary>
public sealed class PcFacts
{
    public string Machine { get; set; } = "";
    public string Os { get; set; } = "";
    public string Build { get; set; } = "";
    public string Domain { get; set; } = "";
    public string User { get; set; } = "";
    public string Model { get; set; } = "";
    public int Cpus { get; set; }
    public long MemTotal { get; set; }
    public long MemFree { get; set; }
    public double MemPercent => MemTotal > 0 ? (MemTotal - MemFree) * 100.0 / MemTotal : 0;
    public TimeSpan Uptime { get; set; }
    public bool RebootPending { get; set; }
    public List<string> RebootReasons { get; } = new();
    public DateTime? LastUpdateInstall { get; set; }
    public DateTime? LastUpdateCheck { get; set; }
    public List<(string Drive, long Size, long Free)> Disks { get; } = new();
    public List<(string Name, string Display, string StartMode, string State)> AutoServicesStopped { get; } = new();
    public bool? AntivirusOn { get; set; }
    public bool? RealTimeOn { get; set; }
    public int? SignatureAgeDays { get; set; }
    public int? QuickScanAgeDays { get; set; }
    public bool? TamperProtected { get; set; }
    public string AntivirusProduct { get; set; } = "";
    public List<(string Profile, bool Enabled, string Inbound)> Firewall { get; } = new();
    public List<(string Drive, string Status)> BitLocker { get; } = new();
    public List<(string Proto, string Address, int Port)> Listeners { get; } = new();
    public List<(string Name, long WorkingSet, double Cpu)> TopProcesses { get; } = new();
    public List<string> Problems { get; } = new();

    public string Summary()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"PC: {Machine}  ({Model})  user {User}  {(Domain.Length > 0 ? "domain " + Domain : "workgroup")}");
        sb.AppendLine($"OS: {Os} build {Build}  CPUs: {Cpus}  Memory: {Fmt.Bytes(MemTotal - MemFree)} / {Fmt.Bytes(MemTotal)} ({MemPercent:0}%)");
        sb.AppendLine($"Uptime: {Fmt.Duration(Uptime)}  Reboot pending: {(RebootPending ? "YES (" + string.Join(", ", RebootReasons) + ")" : "no")}");
        sb.AppendLine($"Windows Update: last install {(LastUpdateInstall is { } i ? i.ToString("yyyy-MM-dd HH:mm") : "unknown")}, last check {(LastUpdateCheck is { } c ? c.ToString("yyyy-MM-dd HH:mm") : "unknown")}");
        foreach (var (d, size, free) in Disks) sb.AppendLine($"Disk {d}  {Fmt.Bytes(size - free)} / {Fmt.Bytes(size)}  ({(size > 0 ? (size - free) * 100.0 / size : 0):0}%)");
        if (AntivirusOn != null) sb.AppendLine($"Antivirus: {AntivirusProduct} on={AntivirusOn} real-time={RealTimeOn} signatures {SignatureAgeDays?.ToString() ?? "?"} day(s) old, quick scan {QuickScanAgeDays?.ToString() ?? "?"} day(s) ago, tamper protection {TamperProtected?.ToString() ?? "?"}");
        foreach (var (p, on, inbound) in Firewall) sb.AppendLine($"Firewall {p}: {(on ? "on" : "OFF")} (inbound {inbound})");
        foreach (var (d, st) in BitLocker) sb.AppendLine($"BitLocker {d}: {st}");
        if (AutoServicesStopped.Count > 0) sb.AppendLine("Automatic services not running: " + string.Join(", ", AutoServicesStopped.Select(s => s.Display)));
        foreach (var p in Problems) sb.AppendLine("Couldn't read: " + p);
        return sb.ToString();
    }
}

/// <summary>
/// A health report of this PC (0.4.0, DarkCipher0x: "own local system reports"): the same pipeline as a server report — evidence,
/// an offline digest, rule findings, the AI pass — with Windows' own sources: WMI (System.Management), the Event Viewer
/// (<see cref="WindowsEvents"/>), Defender, the firewall, BitLocker, services, listeners and processes. Read-only; the
/// Security log and BitLocker need administrator rights and say so when they can't be read.
/// </summary>
public static class LocalReport
{
    public const string HostPrefix = "This PC";
    public static string Display => $"{HostPrefix} ({Environment.MachineName})";
    public static bool IsLocal(string host) => host.StartsWith(HostPrefix, StringComparison.Ordinal);

    public static readonly LocalSource[] Sources =
    {
        new("overview", "System overview (OS, uptime, memory, reboot pending, Windows Update)", true),
        new("disks", "Disk space", true),
        new("events", "Event Viewer: System and Application (critical, error, warning)", true),
        new("security", "Event Viewer: Security (failed logons, account changes, log cleared)", true, Admin: true),
        new("defender", "Microsoft Defender / antivirus state", true),
        new("firewall", "Windows Firewall profiles", true),
        new("services", "Automatic services that aren't running", true),
        new("bitlocker", "BitLocker status", false, Admin: true),
        new("ports", "Listening TCP / UDP ports", false),
        new("processes", "Top processes (memory, CPU)", false),
    };

    /// <summary>Default event logs for the "events" source; the report options can change them.</summary>
    public static readonly string[] DefaultEventLogs = { "System", "Application" };

    // ---- collect --------------------------------------------------------------------------------------------------------

    public static async Task<(List<EvidenceSection> Evidence, PcFacts Facts, List<WinEvent> Events)> CollectAsync(ReportOptions o, IProgress<string>? progress, CancellationToken ct)
    {
        var list = new List<EvidenceSection>();
        var facts = new PcFacts();
        var events = new List<WinEvent>();
        var window = WindowsEvents.WindowOf(o.Window);
        EvidenceSection Sec(string id, string title) => new() { Host = Display, Id = id, Title = title };
        foreach (var src in Sources.Where(s => o.LocalSources.Contains(s.Id)))
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"{Display}: {src.Title.Split(':')[0].ToLowerInvariant()}…");
            var sec = Sec(src.Id, src.Title.Split(" (")[0]);
            try
            {
                switch (src.Id)
                {
                    case "overview": await Task.Run(() => Overview(facts), ct); sec.Digest = facts.Summary(); break;
                    case "disks": Disks(facts); sec.Digest = string.Join("\n", facts.Disks.Select(d => $"{d.Drive}  {Fmt.Bytes(d.Size - d.Free)} / {Fmt.Bytes(d.Size)}  ({(d.Size > 0 ? (d.Size - d.Free) * 100.0 / d.Size : 0):0}%)")); break;
                    case "events":
                    {
                        var all = new List<WinEvent>();
                        foreach (var log in (o.LocalEventLogs.Count > 0 ? o.LocalEventLogs : DefaultEventLogs.ToList()))
                        {
                            try { all.AddRange(await WindowsEvents.ReadAsync(log, WindowsEvents.Query(new[] { 1, 2, 3 }, window), 2000, ct)); }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { sec.Error = (sec.Error.Length > 0 ? sec.Error + "; " : "") + ex.Message.Split('\n')[0]; }
                        }
                        events.AddRange(all);
                        sec.RawLines = all.Count;
                        sec.Digest = all.Count == 0 ? "(no critical, error or warning events in the window)" : WindowsEvents.Digest(all, 40);
                        break;
                    }
                    case "security":
                    {
                        var ids = new[] { 4625, 4740, 4720, 4722, 4724, 4728, 4732, 4756, 1102, 4648, 4719, 4672 };
                        var sec4 = await WindowsEvents.ReadAsync("Security", WindowsEvents.Query(new[] { 0, 4 }, window, ids), 5000, ct);
                        events.AddRange(sec4);
                        sec.RawLines = sec4.Count;
                        sec.Digest = sec4.Count == 0 ? "(no failed logons, account or policy changes, or audit-log clears in the window)" : WindowsEvents.Digest(sec4, 30);
                        break;
                    }
                    case "defender": await Task.Run(() => Defender(facts), ct); sec.Digest = facts.AntivirusOn == null ? "(no antivirus state readable)" : $"{facts.AntivirusProduct}: antivirus {(facts.AntivirusOn == true ? "on" : "OFF")}, real-time protection {(facts.RealTimeOn == true ? "on" : "OFF")}, signatures {facts.SignatureAgeDays?.ToString() ?? "?"} day(s) old, last quick scan {facts.QuickScanAgeDays?.ToString() ?? "?"} day(s) ago, tamper protection {facts.TamperProtected?.ToString() ?? "unknown"}"; break;
                    case "firewall": await Task.Run(() => Firewall(facts), ct); sec.Digest = string.Join("\n", facts.Firewall.Select(f => $"{f.Profile}: {(f.Enabled ? "on" : "OFF")}, inbound default {f.Inbound}")); break;
                    case "services": await Task.Run(() => Services(facts), ct); sec.Digest = facts.AutoServicesStopped.Count == 0 ? "(every automatic service is running)" : string.Join("\n", facts.AutoServicesStopped.Select(s => $"{s.Display} ({s.Name}) {s.StartMode} → {s.State}")); break;
                    case "bitlocker": await Task.Run(() => BitLocker(facts), ct); sec.Digest = facts.BitLocker.Count == 0 ? "(no volumes reported)" : string.Join("\n", facts.BitLocker.Select(b => $"{b.Drive}: {b.Status}")); break;
                    case "ports": Listeners(facts); sec.Digest = string.Join("\n", facts.Listeners.OrderBy(l => l.Port).Select(l => $"{l.Proto,-4} {l.Address}:{l.Port}{(IsAllInterfaces(l.Address) ? "   ⚠ all interfaces" : "")}")); break;
                    case "processes": await ProcessesAsync(facts, ct); sec.Digest = string.Join("\n", facts.TopProcesses.Select(p => $"{p.Name,-28} {Fmt.Bytes(p.WorkingSet),10}  {p.Cpu,5:0.0}% CPU")); break;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException or IOException or InvalidOperationException or ArgumentException or Win32Exception)
            {
                sec.Error = ex.Message.Split('\n')[0];
                facts.Problems.Add(src.Title.Split(':')[0] + ": " + sec.Error);
            }
            list.Add(sec);
        }
        return (list, facts, events);
    }

    private static bool IsAllInterfaces(string a) => a is "0.0.0.0" or "::" or "*";

    // ---- the Windows sources ----------------------------------------------------------------------------------------------

    private static IEnumerable<ManagementObject> Wmi(string query, string scope = @"root\cimv2")
    {
        using var s = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(query));
        using var r = s.Get();
        return r.Cast<ManagementObject>().ToList();
    }

    private static string S(ManagementObject o, string p) => o[p]?.ToString() ?? "";
    private static long L(ManagementObject o, string p) => long.TryParse(S(o, p), out var v) ? v : 0;

    private static void Overview(PcFacts f)
    {
        f.Machine = Environment.MachineName;
        f.User = Environment.UserName;
        f.Os = RuntimeInformation.OSDescription;
        f.Cpus = Environment.ProcessorCount;
        f.Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        try { f.Domain = IPGlobalProperties.GetIPGlobalProperties().DomainName; } catch (NetworkInformationException) { }
        try
        {
            foreach (var os in Wmi("SELECT Caption, Version, BuildNumber, TotalVisibleMemorySize, FreePhysicalMemory, LastBootUpTime FROM Win32_OperatingSystem"))
            {
                f.Os = S(os, "Caption").Trim(); f.Build = S(os, "Version") + " (" + S(os, "BuildNumber") + ")";
                f.MemTotal = L(os, "TotalVisibleMemorySize") * 1024; f.MemFree = L(os, "FreePhysicalMemory") * 1024;
            }
            foreach (var cs in Wmi("SELECT Manufacturer, Model, Domain, PartOfDomain FROM Win32_ComputerSystem"))
            {
                f.Model = (S(cs, "Manufacturer") + " " + S(cs, "Model")).Trim();
                if (S(cs, "PartOfDomain").Equals("True", StringComparison.OrdinalIgnoreCase)) f.Domain = S(cs, "Domain");
            }
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException) { f.Problems.Add("WMI: " + ex.Message); }
        // Reboot pending: the three places Windows records it.
        try
        {
            using var cbs = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
            if (cbs != null) f.RebootReasons.Add("servicing");
            using var wu = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
            if (wu != null) f.RebootReasons.Add("Windows Update");
            using var sm = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager");
            if (sm?.GetValue("PendingFileRenameOperations") is string[] { Length: > 0 }) f.RebootReasons.Add("pending file renames");
            f.RebootPending = f.RebootReasons.Count > 0;
            using var results = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\Results\Install");
            if (results?.GetValue("LastSuccessTime") is string li && DateTime.TryParse(li, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var lit)) f.LastUpdateInstall = lit.ToLocalTime();
            using var detect = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\Results\Detect");
            if (detect?.GetValue("LastSuccessTime") is string ld && DateTime.TryParse(ld, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var ldt)) f.LastUpdateCheck = ldt.ToLocalTime();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException) { f.Problems.Add("registry: " + ex.Message); }
    }

    private static void Disks(PcFacts f)
    {
        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
        {
            try { if (d.IsReady) f.Disks.Add((d.Name.TrimEnd('\\'), d.TotalSize, d.AvailableFreeSpace)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { f.Problems.Add(d.Name + ": " + ex.Message); }
        }
    }

    private static void Defender(PcFacts f)
    {
        try
        {
            foreach (var m in Wmi("SELECT AMServiceEnabled, AntivirusEnabled, RealTimeProtectionEnabled, AntivirusSignatureAge, QuickScanAge, IsTamperProtected FROM MSFT_MpComputerStatus", @"root\Microsoft\Windows\Defender"))
            {
                f.AntivirusProduct = "Microsoft Defender";
                f.AntivirusOn = S(m, "AntivirusEnabled").Equals("True", StringComparison.OrdinalIgnoreCase);
                f.RealTimeOn = S(m, "RealTimeProtectionEnabled").Equals("True", StringComparison.OrdinalIgnoreCase);
                f.SignatureAgeDays = (int)L(m, "AntivirusSignatureAge");
                f.QuickScanAgeDays = (int)L(m, "QuickScanAge");
                if (S(m, "IsTamperProtected") is { Length: > 0 } tp) f.TamperProtected = tp.Equals("True", StringComparison.OrdinalIgnoreCase);
                return;
            }
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException) { f.Problems.Add("Defender: " + ex.Message); }
        // Another antivirus: Security Center's view (productState bit 12 = on, bit 4 of the next byte = signatures current).
        try
        {
            foreach (var av in Wmi("SELECT displayName, productState FROM AntiVirusProduct", @"root\SecurityCenter2"))
            {
                var state = (int)L(av, "productState");
                f.AntivirusProduct = S(av, "displayName");
                f.AntivirusOn = (state & 0x1000) != 0;
                f.RealTimeOn = f.AntivirusOn;
                f.SignatureAgeDays = (state & 0x10) != 0 ? 99 : 0;
                return;
            }
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException) { f.Problems.Add("Security Center: " + ex.Message); }
    }

    private static void Firewall(PcFacts f)
    {
        foreach (var p in Wmi("SELECT Name, Enabled, DefaultInboundAction FROM MSFT_NetFirewallProfile", @"root\StandardCimv2"))
            f.Firewall.Add((S(p, "Name"), S(p, "Enabled") is "1" or "True", S(p, "DefaultInboundAction") switch { "2" => "Allow", "4" => "Block", var x => x.Length == 0 ? "NotConfigured (Block)" : x }));
    }

    private static void Services(PcFacts f)
    {
        foreach (var s in Wmi("SELECT Name, DisplayName, StartMode, State, DelayedAutoStart FROM Win32_Service WHERE StartMode = 'Auto' AND State <> 'Running'"))
        {
            // Trigger-started and delayed services sit idle by design; the ones Windows itself stops after a while are noise.
            var name = S(s, "Name");
            if (S(s, "DelayedAutoStart").Equals("True", StringComparison.OrdinalIgnoreCase)) continue;
            if (name is "sppsvc" or "RemoteRegistry" or "edgeupdate" or "MapsBroker" or "WbioSrvc" or "BITS" or "TrustedInstaller" or "gupdate" || name.StartsWith("GoogleUpdater", StringComparison.OrdinalIgnoreCase) || name.StartsWith("MicrosoftEdgeElevation", StringComparison.OrdinalIgnoreCase)) continue;
            f.AutoServicesStopped.Add((name, S(s, "DisplayName"), S(s, "StartMode"), S(s, "State")));
        }
    }

    private static void BitLocker(PcFacts f)
    {
        foreach (var v in Wmi("SELECT DriveLetter, ProtectionStatus, ConversionStatus FROM Win32_EncryptableVolume", @"root\CIMV2\Security\MicrosoftVolumeEncryption"))
        {
            var status = S(v, "ProtectionStatus") switch { "1" => "protected", "0" => S(v, "ConversionStatus") is "0" ? "NOT encrypted" : "encrypted, protection OFF", _ => "unknown" };
            f.BitLocker.Add((S(v, "DriveLetter"), status));
        }
    }

    private static void Listeners(PcFacts f)
    {
        var p = IPGlobalProperties.GetIPGlobalProperties();
        foreach (var l in p.GetActiveTcpListeners()) f.Listeners.Add(("tcp", l.Address.ToString(), l.Port));
        foreach (var l in p.GetActiveUdpListeners()) f.Listeners.Add(("udp", l.Address.ToString(), l.Port));
    }

    private static async Task ProcessesAsync(PcFacts f, CancellationToken ct)
    {
        var first = new Dictionary<int, (Process P, TimeSpan Cpu)>();
        foreach (var p in Process.GetProcesses()) { try { first[p.Id] = (p, p.TotalProcessorTime); } catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { p.Dispose(); } }
        await Task.Delay(1000, ct);
        var rows = new List<(string Name, long WorkingSet, double Cpu)>();
        foreach (var (id, (p, cpu0)) in first)
        {
            try
            {
                p.Refresh();
                var cpu = (p.TotalProcessorTime - cpu0).TotalMilliseconds / 1000.0 / Environment.ProcessorCount * 100;
                rows.Add((p.ProcessName, p.WorkingSet64, Math.Max(0, cpu)));
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
            finally { p.Dispose(); }
        }
        foreach (var r in rows.OrderByDescending(r => r.WorkingSet).Take(12).Concat(rows.OrderByDescending(r => r.Cpu).Take(6)).DistinctBy(r => r.Name + r.WorkingSet)) f.TopProcesses.Add(r);
    }

    // ---- rules ------------------------------------------------------------------------------------------------------------

    /// <summary>The offline checks on this PC's facts and events (public for the self-test, which feeds canned ones).
    /// Suggested commands are PowerShell (shown with Copy; the report never runs anything on this PC by itself).</summary>
    public static List<Finding> Rules(PcFacts f, IReadOnlyList<WinEvent> events, List<EvidenceSection> ev)
    {
        var x = new List<Finding>();
        void Add(string sev, string title, string detail, string rec, params string[] cmds) =>
            x.Add(new Finding { Host = Display, Severity = sev, Title = title, Detail = detail, Recommendation = rec, Commands = cmds.ToList() });

        foreach (var (drive, size, free) in f.Disks)
        {
            double pct = size > 0 ? (size - free) * 100.0 / size : 0;
            if (pct >= 95) Add("critical", $"{drive} is {pct:0}% full", $"{Fmt.Bytes(free)} free of {Fmt.Bytes(size)}", "Free space now — a full system drive breaks updates, logging and the page file.", "Get-ChildItem -Path " + drive + "\\ -Directory | ForEach-Object { [pscustomobject]@{ Folder = $_.FullName; GB = [math]::Round((Get-ChildItem $_.FullName -Recurse -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum / 1GB, 1) } } | Sort-Object GB -Descending | Select-Object -First 15", "cleanmgr /d " + drive.TrimEnd(':'));
            else if (pct >= 85) Add("high", $"{drive} is {pct:0}% full", $"{Fmt.Bytes(free)} free of {Fmt.Bytes(size)}", "Plan a clean-up.", "Get-PSDrive -PSProvider FileSystem");
        }
        if (f.MemTotal > 0 && f.MemPercent >= 92) Add("high", $"Memory {f.MemPercent:0}% used", $"{Fmt.Bytes(f.MemTotal - f.MemFree)} of {Fmt.Bytes(f.MemTotal)}", "Check the top consumers; close or restart what grew.", "Get-Process | Sort-Object WorkingSet64 -Descending | Select-Object -First 15 Name, @{n='MB';e={[math]::Round($_.WorkingSet64/1MB)}}");
        if (f.RebootPending) Add("medium", "Reboot pending", "Windows reports " + string.Join(", ", f.RebootReasons) + " waiting for a restart.", "Restart when convenient — updates and servicing finish only then.", "Restart-Computer");
        if (f.Uptime.TotalDays > 30) Add("low", $"Up {f.Uptime.TotalDays:0} days", "Windows updates and many fixes need a restart to take effect.", "Restart in a maintenance window.");
        if (f.LastUpdateInstall is { } li && (DateTime.Now - li).TotalDays > 45) Add("medium", $"No Windows update installed for {(DateTime.Now - li).TotalDays:0} days", $"Last successful install {li:yyyy-MM-dd}.", "Check Windows Update; a stuck client or a policy may be holding updates back.", "Get-HotFix | Sort-Object InstalledOn -Descending | Select-Object -First 10", "Get-WinEvent -LogName Microsoft-Windows-WindowsUpdateClient/Operational -MaxEvents 50 | Where-Object Id -in 19,20");
        if (f.AntivirusOn == false) Add("high", "Antivirus is off", f.AntivirusProduct + " reports antivirus disabled.", "Turn it on (Windows Security → Virus & threat protection) or confirm another product covers this PC.", "Get-MpComputerStatus | Select-Object AntivirusEnabled, RealTimeProtectionEnabled, AntivirusSignatureAge");
        else if (f.RealTimeOn == false) Add("high", "Real-time protection is off", f.AntivirusProduct + " is on but not scanning in real time.", "Turn real-time protection back on.", "Set-MpPreference -DisableRealtimeMonitoring $false");
        if (f.SignatureAgeDays is > 3) Add("medium", $"Antivirus signatures {f.SignatureAgeDays} days old", "Definitions update several times a day normally.", "Update them; if that fails, the update path (proxy, policy) is the problem.", "Update-MpSignature");
        if (f.QuickScanAgeDays is > 14) Add("low", $"No quick scan for {f.QuickScanAgeDays} days", "Scheduled scans aren't running.", "Run a quick scan and check the scan schedule.", "Start-MpScan -ScanType QuickScan");
        if (f.TamperProtected == false) Add("medium", "Defender tamper protection is off", "Settings can be changed by malware or scripts.", "Turn tamper protection on in Windows Security.");
        foreach (var (profile, on, _) in f.Firewall) if (!on) Add("high", $"Windows Firewall is off for the {profile} profile", "Inbound connections aren't filtered on this profile.", "Turn the firewall on for every profile.", $"Set-NetFirewallProfile -Profile {profile} -Enabled True");
        foreach (var (drive, status) in f.BitLocker) if (status.StartsWith("NOT", StringComparison.Ordinal) || status.Contains("OFF", StringComparison.Ordinal)) Add("medium", $"BitLocker: {drive} {status}", "A lost or stolen drive is readable.", "Turn BitLocker on (Control Panel → BitLocker Drive Encryption) and save the recovery key in AD / Entra or a safe place.", $"Enable-BitLocker -MountPoint '{drive}' -TpmProtector -UsedSpaceOnly");
        if (f.AutoServicesStopped.Count > 0) Add(f.AutoServicesStopped.Count > 3 ? "medium" : "low", $"{f.AutoServicesStopped.Count} automatic service(s) not running", string.Join(", ", f.AutoServicesStopped.Take(8).Select(s => s.Display)), "Start them or set them to manual if they aren't needed.", "Get-Service | Where-Object { $_.StartType -eq 'Automatic' -and $_.Status -ne 'Running' } | Format-Table Name, DisplayName, Status");
        var open = f.Listeners.Where(l => l.Proto == "tcp" && IsAllInterfaces(l.Address)).Select(l => l.Port).Distinct().OrderBy(p => p).ToList();
        if (open.Count > 0) Add("info", $"{open.Count} TCP port(s) listening on all interfaces", string.Join(", ", open.Take(20)), "Make sure each one is meant to be reachable from the network (firewall rules decide what actually gets in).", "Get-NetTCPConnection -State Listen | Where-Object LocalAddress -in '0.0.0.0','::' | Sort-Object LocalPort | Format-Table LocalAddress, LocalPort, OwningProcess");

        // Events: the ones that matter, counted.
        int failed = events.Count(e => e.Log == "Security" && e.Id == 4625);
        if (failed >= 200) Add("medium", $"{failed} failed logons", "Brute force on RDP / SMB / WinRM, or a stale credential retrying.", "Find the source (IpAddress in the events); block it or fix the stale credential; keep RDP off the internet.", "Get-WinEvent -FilterHashtable @{LogName='Security'; Id=4625} -MaxEvents 200 | ForEach-Object { $_.Properties[19].Value } | Group-Object | Sort-Object Count -Descending | Select-Object -First 10");
        else if (failed >= 20) Add("low", $"{failed} failed logons", "Some failed authentication attempts.", "Review the sources.");
        if (events.Any(e => e.Log == "Security" && e.Id == 1102)) Add("high", "The Security audit log was cleared", "Event 1102: someone cleared the log — attackers do this to cover tracks.", "Find who (the event names the account) and why.", "Get-WinEvent -FilterHashtable @{LogName='Security'; Id=1102} -MaxEvents 5 | Format-List");
        int lockouts = events.Count(e => e.Log == "Security" && e.Id == 4740);
        if (lockouts > 0) Add("low", $"{lockouts} account lockout(s)", "Event 4740.", "Check which accounts and from where.");
        int accounts = events.Count(e => e.Log == "Security" && e.Id is 4720 or 4722 or 4724 or 4728 or 4732 or 4756);
        if (accounts > 0) Add("medium", $"{accounts} account or group change(s)", "Accounts created / enabled / password reset, or members added to privileged groups (4720, 4722, 4724, 4728, 4732, 4756).", "Confirm each one was intended.", "Get-WinEvent -FilterHashtable @{LogName='Security'; Id=4720,4722,4724,4728,4732,4756} -MaxEvents 50 | Format-Table TimeCreated, Id, Message -Wrap");
        if (events.Any(e => e.Id == 4719 && e.Log == "Security")) Add("medium", "Audit policy changed", "Event 4719.", "Confirm the change was yours or Group Policy's.");
        if (events.Any(e => e.Log == "System" && e.Provider.Contains("Kernel-Power", StringComparison.OrdinalIgnoreCase) && e.Id == 41)) Add("high", "Unexpected shutdown or crash (Kernel-Power 41)", "The PC restarted without shutting down cleanly: a crash, power loss or hard reset.", "Look for a bug check (1001) or a power event near the same time.", "Get-WinEvent -FilterHashtable @{LogName='System'; Id=41,1001,6008} -MaxEvents 20 | Format-Table TimeCreated, Id, ProviderName -Wrap");
        else if (events.Any(e => e.Log == "System" && e.Id == 6008)) Add("medium", "Unexpected shutdown (6008)", "The previous shutdown was unexpected.", "Check power and crash events around that time.");
        if (events.Any(e => e.Log == "System" && e.Id == 1001 && e.Provider.Contains("BugCheck", StringComparison.OrdinalIgnoreCase))) Add("high", "Blue screen (bug check)", "A kernel crash was recorded.", "Note the stop code and the driver; check the minidump.", "Get-WinEvent -FilterHashtable @{LogName='System'; ProviderName='Microsoft-Windows-WER-SystemErrorReporting'} -MaxEvents 5 | Format-List");
        if (events.Any(e => e.Log == "System" && (e.Provider.Equals("disk", StringComparison.OrdinalIgnoreCase) && e.Id is 7 or 11 or 51 or 153 || e.Provider.Equals("Ntfs", StringComparison.OrdinalIgnoreCase) && e.Id is 55 or 98))) Add("critical", "Disk or file-system errors", "disk 7/11/51/153 or Ntfs 55/98 events: a failing drive or a corrupt volume.", "Back up now; check SMART; run chkdsk in a window.", "Get-PhysicalDisk | Format-Table FriendlyName, HealthStatus, OperationalStatus", "chkdsk C: /scan");
        if (events.Any(e => e.Provider.Contains("WHEA", StringComparison.OrdinalIgnoreCase))) Add("high", "Hardware error (WHEA)", "The platform reported a machine-check / hardware error.", "Check memory, CPU temperatures and firmware.", "Get-WinEvent -FilterHashtable @{LogName='System'; ProviderName='Microsoft-Windows-WHEA-Logger'} -MaxEvents 10 | Format-List");
        int crashes = events.Count(e => e.Log == "Application" && e.Provider.Equals("Application Error", StringComparison.OrdinalIgnoreCase) && e.Id == 1000);
        if (crashes >= 3) Add("low", $"{crashes} application crash(es)", "Application Error 1000.", "See which programs; update or repair them.", "Get-WinEvent -FilterHashtable @{LogName='Application'; Id=1000} -MaxEvents 20 | ForEach-Object { $_.Properties[0].Value } | Group-Object | Sort-Object Count -Descending");
        int svcCrash = events.Count(e => e.Log == "System" && e.Provider.Contains("Service Control Manager", StringComparison.OrdinalIgnoreCase) && e.Id is 7031 or 7034);
        if (svcCrash > 0) Add("low", $"{svcCrash} service crash(es)", "Service Control Manager 7031/7034.", "Which services, and whether they recovered.");
        int wuFail = events.Count(e => e.Provider.Contains("WindowsUpdateClient", StringComparison.OrdinalIgnoreCase) && e.Id == 20);
        if (wuFail > 0) Add("low", $"{wuFail} Windows Update failure(s)", "WindowsUpdateClient 20.", "Retry; if it keeps failing, the error code names the cause.");
        foreach (var e in ev.Where(e => e.Error.Length > 0)) x.Add(new Finding { Host = Display, Severity = "info", Title = $"Couldn't collect: {e.Title}", Detail = e.Error, Source = "rule" });
        return x;
    }
}
