using System.Text.RegularExpressions;

namespace DcxManager.WinTools.Wmi;

/// <summary>
/// wmic, without wmic.exe (ADR-119). Windows is removing wmic.exe, so the WMI console turns its command line into the
/// WQL query it stands for and runs that natively:
/// <c>[/namespace:\\root\x] alias|path Class [where "cond" | where (cond)] [get a,b | list [brief|full]]</c>.
/// The console only reads: <c>call</c>, <c>set</c>, <c>create</c> and <c>delete</c> are refused (the snap-ins' actions
/// change things, each confirmed and audited), and so is <c>/node:</c> (pick the computer in the toolbar instead).
/// A query typed as WQL is run when it reads (SELECT, ASSOCIATORS OF, REFERENCES OF).
/// </summary>
public static class WmicCommand
{
    /// <summary>wmic's aliases and the classes they stand for (with the columns "list brief" shows).</summary>
    public static readonly IReadOnlyDictionary<string, (string Class, string[] Brief)> Aliases = new Dictionary<string, (string, string[])>(StringComparer.OrdinalIgnoreCase)
    {
        ["baseboard"] = ("Win32_BaseBoard", ["Manufacturer", "Product", "SerialNumber", "Version"]),
        ["bios"] = ("Win32_BIOS", ["Manufacturer", "Name", "SerialNumber", "SMBIOSBIOSVersion", "Version"]),
        ["bootconfig"] = ("Win32_BootConfiguration", ["BootDirectory", "Caption", "TempDirectory"]),
        ["cdrom"] = ("Win32_CDROMDrive", ["Drive", "Manufacturer", "MediaLoaded", "Name"]),
        ["computersystem"] = ("Win32_ComputerSystem", ["Domain", "Manufacturer", "Model", "Name", "TotalPhysicalMemory", "UserName"]),
        ["cpu"] = ("Win32_Processor", ["Name", "NumberOfCores", "NumberOfLogicalProcessors", "MaxClockSpeed", "LoadPercentage"]),
        ["csproduct"] = ("Win32_ComputerSystemProduct", ["IdentifyingNumber", "Name", "Vendor", "Version", "UUID"]),
        ["desktopmonitor"] = ("Win32_DesktopMonitor", ["Name", "ScreenHeight", "ScreenWidth", "Status"]),
        ["diskdrive"] = ("Win32_DiskDrive", ["Model", "InterfaceType", "MediaType", "Size", "Status"]),
        ["diskquota"] = ("Win32_DiskQuota", ["DiskSpaceUsed", "Limit", "QuotaVolume", "User"]),
        ["environment"] = ("Win32_Environment", ["Name", "UserName", "VariableValue"]),
        ["group"] = ("Win32_Group", ["Domain", "Name", "SID", "Description"]),
        ["logicaldisk"] = ("Win32_LogicalDisk", ["DeviceID", "DriveType", "FileSystem", "FreeSpace", "Size", "VolumeName"]),
        ["memorychip"] = ("Win32_PhysicalMemory", ["BankLabel", "Capacity", "DeviceLocator", "Manufacturer", "Speed"]),
        ["netlogin"] = ("Win32_NetworkLoginProfile", ["Name", "FullName", "LastLogon", "NumberOfLogons"]),
        ["netprotocol"] = ("Win32_NetworkProtocol", ["Name", "Description", "Status"]),
        ["netuse"] = ("Win32_NetworkConnection", ["LocalName", "RemoteName", "ConnectionState", "Status"]),
        ["nic"] = ("Win32_NetworkAdapter", ["Name", "MACAddress", "NetConnectionID", "NetEnabled", "Speed"]),
        ["nicconfig"] = ("Win32_NetworkAdapterConfiguration", ["Description", "IPAddress", "DefaultIPGateway", "DNSServerSearchOrder", "DHCPEnabled", "MACAddress"]),
        ["ntdomain"] = ("Win32_NTDomain", ["DomainName", "DnsForestName", "DomainControllerName", "Status"]),
        ["onboarddevice"] = ("Win32_OnBoardDevice", ["Description", "DeviceType", "Enabled"]),
        ["os"] = ("Win32_OperatingSystem", ["Caption", "Version", "BuildNumber", "OSArchitecture", "LastBootUpTime", "FreePhysicalMemory"]),
        ["pagefile"] = ("Win32_PageFileUsage", ["Name", "AllocatedBaseSize", "CurrentUsage", "PeakUsage"]),
        ["partition"] = ("Win32_DiskPartition", ["Name", "Bootable", "BootPartition", "Size", "Type"]),
        ["port"] = ("Win32_PortResource", ["Name", "StartingAddress", "EndingAddress", "Status"]),
        ["printer"] = ("Win32_Printer", ["Name", "DriverName", "PortName", "Default", "Shared", "WorkOffline"]),
        ["printjob"] = ("Win32_PrintJob", ["Document", "Owner", "JobStatus", "TotalPages"]),
        ["process"] = ("Win32_Process", ["ProcessId", "Name", "ParentProcessId", "ThreadCount", "WorkingSetSize"]),
        ["product"] = ("Win32_Product", ["Name", "Vendor", "Version", "InstallDate"]),
        ["qfe"] = ("Win32_QuickFixEngineering", ["HotFixID", "Description", "InstalledBy", "InstalledOn"]),
        ["recoveros"] = ("Win32_OSRecoveryConfiguration", ["AutoReboot", "DebugInfoType", "WriteToSystemLog"]),
        ["scsicontroller"] = ("Win32_SCSIController", ["Name", "Manufacturer", "Status"]),
        ["server"] = ("Win32_PerfRawData_PerfNet_Server", ["Name", "ServerSessions", "FilesOpen"]),
        ["service"] = ("Win32_Service", ["Name", "DisplayName", "State", "StartMode", "StartName", "ProcessId"]),
        ["share"] = ("Win32_Share", ["Name", "Path", "Description", "Type"]),
        ["sounddev"] = ("Win32_SoundDevice", ["Name", "Manufacturer", "Status"]),
        ["startup"] = ("Win32_StartupCommand", ["Caption", "Command", "Location", "User"]),
        ["sysaccount"] = ("Win32_SystemAccount", ["Name", "Domain", "SID"]),
        ["sysdriver"] = ("Win32_SystemDriver", ["Name", "DisplayName", "State", "StartMode", "PathName"]),
        ["systemenclosure"] = ("Win32_SystemEnclosure", ["Manufacturer", "SerialNumber", "SMBIOSAssetTag", "ChassisTypes"]),
        ["systemslot"] = ("Win32_SystemSlot", ["SlotDesignation", "CurrentUsage", "Status"]),
        ["tapedrive"] = ("Win32_TapeDrive", ["Name", "Manufacturer", "Status"]),
        ["temperature"] = ("Win32_TemperatureProbe", ["Name", "CurrentReading", "Status"]),
        ["timezone"] = ("Win32_TimeZone", ["Caption", "Bias", "StandardName", "DaylightName"]),
        ["useraccount"] = ("Win32_UserAccount", ["Name", "FullName", "Domain", "Disabled", "Lockout", "SID"]),
        ["volume"] = ("Win32_Volume", ["DriveLetter", "Label", "FileSystem", "Capacity", "FreeSpace"]),
    };

    private static readonly string[] Changes = ["call", "set", "create", "delete"];

    /// <summary>What a console line runs: the namespace and the WQL; or why it isn't run.</summary>
    public sealed record Parsed(string Namespace, string Wql, string? Error, bool FromWmic);

    public static bool ValidNamespace(string ns) => Regex.IsMatch(ns, @"^[A-Za-z0-9_]+(\\[A-Za-z0-9_]+)*$");

    /// <summary>A WQL statement that only reads: one SELECT / ASSOCIATORS OF / REFERENCES OF, nothing after it.</summary>
    public static bool IsReadOnlyWql(string wql)
    {
        var t = wql.Trim();
        if (t.Length == 0 || t.Length > 4000 || t.Contains(';') || t.Any(c => char.IsControl(c) && c != '\t')) return false;
        return Regex.IsMatch(t, @"^(select\s|associators\s+of\s|references\s+of\s)", RegexOptions.IgnoreCase);
    }

    public static Parsed Parse(string line, string defaultNamespace = @"root\cimv2")
    {
        var text = line.Trim();
        if (text.Length == 0) return new(defaultNamespace, "", "Type a WQL query or a wmic command (for example: os get caption,version).", false);
        if (Regex.IsMatch(text, @"^(select|associators|references)\s", RegexOptions.IgnoreCase))
            return IsReadOnlyWql(text) ? new(defaultNamespace, text, null, false) : new(defaultNamespace, text, "Only one query that reads can be run (no ';').", false);

        var ns = defaultNamespace;
        if (text.StartsWith("wmic ", StringComparison.OrdinalIgnoreCase)) text = text[5..].TrimStart();
        // Global switches
        while (text.StartsWith('/'))
        {
            var m = Regex.Match(text, @"^/(\w+)(?::(""[^""]*""|\S*))?\s*");
            if (!m.Success) return new(ns, "", "That switch isn't understood.", true);
            var name = m.Groups[1].Value.ToLowerInvariant();
            var value = m.Groups[2].Value.Trim('"');
            switch (name)
            {
                case "namespace":
                    value = value.TrimStart('\\');
                    if (!ValidNamespace(value)) return new(ns, "", "That isn't a WMI namespace (such as \\\\root\\cimv2).", true);
                    ns = value; break;
                case "node": return new(ns, "", "Pick the computer in the toolbar instead of /node: (it uses that computer's saved credential).", true);
                case "user" or "password" or "authority": return new(ns, "", "Credentials aren't typed here: the toolbar's computer uses its saved credential or your Windows sign-in.", true);
                default: return new(ns, "", "/" + name + " isn't supported here.", true);
            }
            text = text[m.Length..];
        }

        // alias | path Class
        string cls; string[] brief;
        var head = Regex.Match(text, @"^(path\s+)?([A-Za-z_][A-Za-z0-9_]*)\s*", RegexOptions.IgnoreCase);
        if (!head.Success) return new(ns, "", "Start with an alias (os, service, process, logicaldisk, …) or path Win32_Class.", true);
        var word = head.Groups[2].Value;
        if (head.Groups[1].Success) { cls = word; brief = []; }
        else if (Aliases.TryGetValue(word, out var a)) (cls, brief) = a;
        else return new(ns, "", $"“{word}” isn't a wmic alias. Use path Win32_… for any class, or one of: " + string.Join(", ", Aliases.Keys.Take(12)) + ", …", true);
        text = text[head.Length..];

        string? where = null;
        var w = Regex.Match(text, @"^where\s+(""(?<c>[^""]*)""|\((?<c>[^()]*(\([^()]*\)[^()]*)*)\)|(?<c>\S+))\s*", RegexOptions.IgnoreCase);
        if (w.Success) { where = w.Groups["c"].Value.Trim(); text = text[w.Length..]; }

        string cols = "*";
        var verb = Regex.Match(text, @"^(\w+)\s*(.*)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (verb.Success)
        {
            var v = verb.Groups[1].Value.ToLowerInvariant();
            var rest = verb.Groups[2].Value.Trim();
            if (Changes.Contains(v)) return new(ns, "", $"The console only reads; “{v}” changes things. Use the snap-ins' actions (each is confirmed and audited).", true);
            if (v == "get")
            {
                rest = Regex.Replace(rest, @"\s*/\w+(:\S*)?", "").Trim();
                if (rest.Length > 0)
                {
                    var names = rest.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (names.Any(n => !Regex.IsMatch(n, @"^[A-Za-z_][A-Za-z0-9_]*$"))) return new(ns, "", "get takes property names separated by commas.", true);
                    cols = string.Join(", ", names);
                }
            }
            else if (v == "list")
            {
                var kind = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant() ?? "full";
                if (kind is "brief" or "status" or "instance" && brief.Length > 0) cols = kind == "instance" ? brief[0] : string.Join(", ", brief);
                else if (kind is not ("full" or "brief" or "status" or "instance" or "system" or "writeable" or "/format:list")) return new(ns, "", "list takes brief, full, instance, status or system.", true);
            }
            else return new(ns, "", $"“{v}” isn't a wmic verb the console runs (get, list).", true);
        }
        else if (text.Trim().Length > 0) return new(ns, "", "That part isn't understood: " + text.Trim(), true);
        else if (brief.Length > 0) cols = string.Join(", ", brief);   // bare "wmic os" shows the brief list here

        if (where is not null && (where.Contains(';') || where.Any(char.IsControl))) return new(ns, "", "That where clause isn't allowed.", true);
        var wql = $"SELECT {cols} FROM {cls}" + (where is { Length: > 0 } ? " WHERE " + where : "");
        return new(ns, wql, null, true);
    }
}
