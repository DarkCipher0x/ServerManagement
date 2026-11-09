using System.Globalization;
using System.Text.RegularExpressions;

namespace DcxManager.WinTools.Wmi;

/// <summary>An action on the selected row: its label, whether it changes something (confirmed, audited, follows
/// WinToolsAllowChanges), a warning for the confirmation, and what it runs. It returns the line shown afterwards.</summary>
public sealed record SnapAction(string Id, string Label, bool Changes, string Warning, Func<WinTarget, WmiTable, string[], Task<string>> Run);

/// <summary>One node of the console tree (an MMC snap-in): what it loads and what can be done to its rows.</summary>
public sealed record Snapin(string Id, string Group, string Glyph, string Title, string About, Func<WinTarget, CancellationToken, Task<WmiTable>> Load, IReadOnlyList<SnapAction> Actions, string? EventLog = null);

/// <summary>
/// The console's snap-ins (ADR-119), in the order of the tree. They all read through <see cref="WinTarget"/>: WMI
/// classes and the event log API, for this PC or the chosen computer. The actions call WMI methods (services,
/// processes, user accounts) or run schtasks.exe on that computer through Win32_Process.Create (scheduled tasks). Names
/// that go into a WMI path or a command are checked first, and quotes and backslashes are refused.
/// </summary>
public static class Snapins
{
    public const string ConsoleId = "console", ClassicId = "classic";

    public static IReadOnlyList<Snapin> All { get; } = Build();

    public static Snapin? Find(string id) => All.FirstOrDefault(s => s.Id == id);

    /// <summary>A value safe inside a WMI object path's quotes ('…'): no quote, backslash or control character.</summary>
    public static bool SafeKey(string s) => s.Length is > 0 and <= 260 && !s.Any(c => c is '\'' or '"' or '\\' || char.IsControl(c));

    private static List<Snapin> Build()
    {
        var list = new List<Snapin>
        {
            new("overview", "System", "🖥", "System overview", "The computer, its Windows, firmware, processors and memory.", OverviewAsync, []),
            new("services", "System", "⚙", "Services", "Every service: its state, start-up type, account and process. Start, stop, restart, or change how it starts.",
                (t, ct) => t.QueryAsync("SELECT Name, DisplayName, State, StartMode, StartName, ProcessId, PathName FROM Win32_Service", ct: ct),
                [
                    Svc("start", "▶ Start", "StartService", "Starts the service."),
                    Svc("stop", "■ Stop", "StopService", "Stops the service. What depends on it stops working until it is started again."),
                    new("restart", "↻ Restart", true, "Stops the service and starts it again. What depends on it is briefly interrupted.", RestartAsync),
                    StartMode("auto", "Start-up: Automatic", "Automatic"),
                    StartMode("manual", "Start-up: Manual", "Manual"),
                    StartMode("disabled", "Start-up: Disabled", "Disabled"),
                ]),
            new("processes", "System", "▤", "Processes", "Running processes with their parent, threads, memory and command line. End a process.",
                async (t, ct) => Megabytes(await t.QueryAsync("SELECT ProcessId, Name, ParentProcessId, ThreadCount, WorkingSetSize, CreationDate, CommandLine FROM Win32_Process", ct: ct), "WorkingSetSize", "Memory (MB)"),
                [new("end", "✕ End process", true, "Ends the process at once; anything it hasn't saved is lost.", EndProcessAsync)]),
            new("startup", "System", "⏵", "Start-up programs", "What starts when someone signs in (Run keys and Startup folders).",
                (t, ct) => t.QueryAsync("SELECT Caption, Command, Location, User FROM Win32_StartupCommand", ct: ct), []),
            new("drivers", "System", "⛭", "Drivers", "Kernel and file-system drivers: state and start-up type.",
                (t, ct) => t.QueryAsync("SELECT Name, DisplayName, State, StartMode, ServiceType, PathName FROM Win32_SystemDriver", ct: ct), []),
            new("updates", "System", "⇪", "Installed updates", "Windows updates (hotfixes) installed on the computer.",
                (t, ct) => t.QueryAsync("SELECT HotFixID, Description, InstalledBy, InstalledOn FROM Win32_QuickFixEngineering", ct: ct), []),

            Log("ev-system", "System", "Windows' own events: drivers, services, start-up and shutdown."),
            Log("ev-application", "Application", "Events from applications and services."),
            Log("ev-setup", "Setup", "Windows installation and update events."),
            Log("ev-security", "Security", "Sign-ins, privilege use and audit events (needs administrator rights on that computer)."),

            new("disks", "Storage", "🖴", "Disks", "Physical disks: model, interface, size, partitions and status.",
                async (t, ct) => Gigabytes(await t.QueryAsync("SELECT Index, Model, InterfaceType, MediaType, Size, Partitions, SerialNumber, Status FROM Win32_DiskDrive", ct: ct), "Size", "Size (GB)"), []),
            new("volumes", "Storage", "◫", "Volumes", "Drives: file system, size and free space.", VolumesAsync, []),
            new("shares", "Storage", "⇆", "Shares", "Folders and printers shared from the computer.",
                (t, ct) => t.QueryAsync("SELECT Name, Path, Description, Type, MaximumAllowed FROM Win32_Share", ct: ct), []),

            new("users", "Local users and groups", "👤", "Users", "The computer's own (local) user accounts. Enable or disable one.",
                (t, ct) => t.QueryAsync("SELECT Name, FullName, Description, Disabled, Lockout, PasswordRequired, PasswordExpires, SID FROM Win32_UserAccount WHERE LocalAccount = TRUE", ct: ct),
                [
                    Account("enable", "✓ Enable account", false, "Lets the account sign in again."),
                    Account("disable", "⊘ Disable account", true, "Stops the account signing in (its files and settings stay)."),
                ]),
            new("groups", "Local users and groups", "👥", "Groups", "The computer's own (local) groups.",
                (t, ct) => t.QueryAsync("SELECT Name, Description, SID FROM Win32_Group WHERE LocalAccount = TRUE", ct: ct), []),

            new("network", "Network", "⇄", "Network adapters", "Adapters in use: addresses, gateway, DNS and DHCP.",
                (t, ct) => t.QueryAsync("SELECT Description, IPAddress, IPSubnet, DefaultIPGateway, DNSServerSearchOrder, DHCPEnabled, DHCPServer, MACAddress FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = TRUE", ct: ct), []),
            new("connections", "Network", "⇢", "Mapped drives", "Network drives and connections of the signed-in users.",
                (t, ct) => t.QueryAsync("SELECT LocalName, RemoteName, ConnectionState, Status, UserName FROM Win32_NetworkConnection", ct: ct), []),

            new("tasks", "Scheduled tasks", "◷", "Scheduled tasks", "The Task Scheduler's tasks: state, author and what they're for. Run, enable or disable one.", TasksAsync,
                [
                    Task("run", "▶ Run now", "/Run", "Runs the task now, as it is set up to run."),
                    Task("enable", "✓ Enable", "/Change /ENABLE", "Lets the task run on its schedule again."),
                    Task("disable", "⊘ Disable", "/Change /DISABLE", "Stops the task running on its schedule."),
                ]),

            new(ConsoleId, "Tools", "⌘", "WMI console", "WQL queries and wmic-style commands (os get caption,version · service where state=\"running\" get name · path Win32_BIOS). Reading only.", (_, _) => System.Threading.Tasks.Task.FromResult(new WmiTable()), []),
            new(ClassicId, "Tools", "▣", "Classic consoles", "Windows' own MMC consoles (Computer Management, Event Viewer, Services…), opened for this computer.", ClassicAsync,
                [new("open", "Open", false, "", (t, tab, row) => System.Threading.Tasks.Task.FromResult(ClassicConsoles.Open(t, row[tab.Col("Console")])))]),
        };
        return list;
    }

    private static Snapin Log(string id, string log, string about) =>
        new(id, "Event Viewer", "≣", log, about, (t, ct) => t.EventsAsync(log, WinToolsPolicy.EventLimit, 0, ct), [], log);

    // ------------------------------------------------------------------------------------------- loads
    private static async Task<WmiTable> OverviewAsync(WinTarget t, CancellationToken ct)
    {
        var table = new WmiTable();
        table.Columns.AddRange(["Property", "Value"]);
        async Task Add(string wql, params (string Prop, string Label)[] props)
        {
            try
            {
                var r = await t.QueryAsync(wql, ct: ct).ConfigureAwait(false);
                foreach (var row in r.Rows)
                    foreach (var (prop, label) in props)
                        if (r.Col(prop) is >= 0 and var i && row[i].Length > 0) table.Rows.Add([label, prop.EndsWith("Memory", StringComparison.Ordinal) || prop == "TotalPhysicalMemory" ? Bytes(row[i], prop == "TotalPhysicalMemory" ? 1 : 1024) : row[i]]);
            }
            catch (System.Management.ManagementException ex) { table.Rows.Add([wql.Split(' ').Last(), "unavailable: " + ex.Message]); }
        }
        await Add("SELECT Name, Domain, Manufacturer, Model, TotalPhysicalMemory, UserName FROM Win32_ComputerSystem",
            ("Name", "Computer name"), ("Domain", "Domain / workgroup"), ("Manufacturer", "Manufacturer"), ("Model", "Model"), ("TotalPhysicalMemory", "Memory"), ("UserName", "Signed-in user")).ConfigureAwait(false);
        await Add("SELECT Caption, Version, BuildNumber, OSArchitecture, InstallDate, LastBootUpTime, FreePhysicalMemory, RegisteredUser FROM Win32_OperatingSystem",
            ("Caption", "Windows"), ("Version", "Version"), ("BuildNumber", "Build"), ("OSArchitecture", "Architecture"), ("InstallDate", "Installed"), ("LastBootUpTime", "Last start"), ("FreePhysicalMemory", "Free memory")).ConfigureAwait(false);
        await Add("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor",
            ("Name", "Processor"), ("NumberOfCores", "Cores"), ("NumberOfLogicalProcessors", "Logical processors"), ("MaxClockSpeed", "Max clock (MHz)")).ConfigureAwait(false);
        await Add("SELECT Manufacturer, SMBIOSBIOSVersion, SerialNumber FROM Win32_BIOS",
            ("Manufacturer", "Firmware maker"), ("SMBIOSBIOSVersion", "Firmware version"), ("SerialNumber", "Serial number")).ConfigureAwait(false);
        return table;
    }

    private static async Task<WmiTable> VolumesAsync(WinTarget t, CancellationToken ct)
    {
        var r = await t.QueryAsync("SELECT DeviceID, VolumeName, DriveType, FileSystem, Size, FreeSpace FROM Win32_LogicalDisk", ct: ct).ConfigureAwait(false);
        var table = new WmiTable();
        table.Columns.AddRange(["Drive", "Label", "Type", "File system", "Size (GB)", "Free (GB)", "Free %"]);
        int iSize = r.Col("Size"), iFree = r.Col("FreeSpace"), iType = r.Col("DriveType");
        foreach (var row in r.Rows)
        {
            double.TryParse(row[iSize], NumberStyles.Float, CultureInfo.InvariantCulture, out var size);
            double.TryParse(row[iFree], NumberStyles.Float, CultureInfo.InvariantCulture, out var free);
            var type = row[iType] switch { "2" => "Removable", "3" => "Local disk", "4" => "Network", "5" => "CD/DVD", "6" => "RAM disk", _ => row[iType] };
            table.Rows.Add([row[r.Col("DeviceID")], row[r.Col("VolumeName")], type, row[r.Col("FileSystem")],
                size > 0 ? (size / 1e9).ToString("0.0", CultureInfo.InvariantCulture) : "", size > 0 ? (free / 1e9).ToString("0.0", CultureInfo.InvariantCulture) : "",
                size > 0 ? (free / size * 100).ToString("0", CultureInfo.InvariantCulture) + " %" : ""]);
        }
        return table;
    }

    private static async Task<WmiTable> TasksAsync(WinTarget t, CancellationToken ct)
    {
        var r = await t.QueryAsync("SELECT TaskPath, TaskName, State, Author, Description FROM MSFT_ScheduledTask", @"root\Microsoft\Windows\TaskScheduler", ct: ct).ConfigureAwait(false);
        int iState = r.Col("State");
        if (iState >= 0) foreach (var row in r.Rows) row[iState] = row[iState] switch { "1" => "Disabled", "2" => "Queued", "3" => "Ready", "4" => "Running", _ => "Unknown" };
        return r;
    }

    private static Task<WmiTable> ClassicAsync(WinTarget t, CancellationToken ct)
    {
        var table = new WmiTable();
        table.Columns.AddRange(["Console", "What it manages", "For"]);
        foreach (var c in ClassicConsoles.All)
            table.Rows.Add([c.Title, c.About, t.IsLocal || c.Remote ? (t.IsLocal ? "this PC" : t.Computer) : "this PC only"]);
        return System.Threading.Tasks.Task.FromResult(table);
    }

    private static WmiTable Megabytes(WmiTable t, string col, string title) => Scale(t, col, title, 1e6);
    private static WmiTable Gigabytes(WmiTable t, string col, string title) => Scale(t, col, title, 1e9);

    private static WmiTable Scale(WmiTable t, string col, string title, double by)
    {
        int i = t.Col(col);
        if (i < 0) return t;
        t.Columns[i] = title;
        foreach (var row in t.Rows)
            if (double.TryParse(row[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) row[i] = (v / by).ToString(by >= 1e9 ? "0.0" : "0", CultureInfo.InvariantCulture);
        return t;
    }

    private static string Bytes(string s, double unit) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? (v * unit / (1024d * 1024 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " GB" : s;

    // ------------------------------------------------------------------------------------------- actions
    private static string Key(WmiTable t, string[] row, string col)
    {
        var i = t.Col(col);
        var v = i >= 0 ? row[i] : "";
        if (!SafeKey(v)) throw new InvalidOperationException("That name can't be used here (it contains quotes or backslashes).");
        return v;
    }

    private static SnapAction Svc(string id, string label, string method, string warn) =>
        new(id, label, true, warn, async (t, tab, row) =>
        {
            var name = Key(tab, row, "Name");
            var rc = await t.InvokeAsync($"Win32_Service.Name='{name}'", method).ConfigureAwait(false);
            return Result(rc, $"{label.TrimStart('▶', '■', ' ')} {name}");
        });

    private static async Task<string> RestartAsync(WinTarget t, WmiTable tab, string[] row)
    {
        var name = Key(tab, row, "Name");
        var path = $"Win32_Service.Name='{name}'";
        var rc = await t.InvokeAsync(path, "StopService").ConfigureAwait(false);
        if (rc is not (0 or 5)) return Result(rc, "Stop " + name);
        for (int i = 0; i < 60; i++)
        {
            var st = await t.QueryAsync($"SELECT State FROM Win32_Service WHERE Name = '{name}'").ConfigureAwait(false);
            if (st.Rows.FirstOrDefault()?[0] == "Stopped") break;
            await System.Threading.Tasks.Task.Delay(500).ConfigureAwait(false);
        }
        return Result(await t.InvokeAsync(path, "StartService").ConfigureAwait(false), "Restart " + name);
    }

    private static SnapAction StartMode(string id, string label, string mode) =>
        new(id, label, true, $"Sets the service to start {mode.ToLowerInvariant()}" + (mode == "Disabled" ? ": it can't be started until this is changed back." : "."), async (t, tab, row) =>
        {
            var name = Key(tab, row, "Name");
            var rc = await t.InvokeAsync($"Win32_Service.Name='{name}'", "ChangeStartMode", new Dictionary<string, object> { ["StartMode"] = mode }).ConfigureAwait(false);
            return Result(rc, $"{name} start-up → {mode}");
        });

    private static async Task<string> EndProcessAsync(WinTarget t, WmiTable tab, string[] row)
    {
        var pidText = Key(tab, row, "ProcessId");
        if (!uint.TryParse(pidText, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 4) throw new InvalidOperationException("That process is part of Windows itself and can't be ended here.");
        if (t.IsLocal && pid == (uint)Environment.ProcessId) throw new InvalidOperationException("That is ServerManagement itself.");
        var rc = await t.InvokeAsync($"Win32_Process.Handle='{pid}'", "Terminate", new Dictionary<string, object> { ["Reason"] = 1u }).ConfigureAwait(false);
        return Result(rc, $"End process {pid} ({row[tab.Col("Name")]})");
    }

    private static SnapAction Account(string id, string label, bool disable, string warn) =>
        new(id, label, true, warn, async (t, tab, row) =>
        {
            var name = Key(tab, row, "Name");
            var sid = Key(tab, row, "SID");
            if (!Regex.IsMatch(sid, @"^S-1-[0-9-]+$")) throw new InvalidOperationException("That account has no usable SID.");
            var users = await t.QueryAsync($"SELECT Domain, Name FROM Win32_UserAccount WHERE SID = '{sid}'").ConfigureAwait(false);
            var domain = users.Rows.FirstOrDefault()?[users.Col("Domain")] ?? "";
            if (!SafeKey(domain)) throw new InvalidOperationException("That account's domain can't be used here.");
            await t.PutAsync($"Win32_UserAccount.Domain='{domain}',Name='{name}'", "Disabled", disable).ConfigureAwait(false);
            return (disable ? "Disabled " : "Enabled ") + name;
        });

    private static SnapAction Task(string id, string label, string verb, string warn) =>
        new(id, label, true, warn, async (t, tab, row) =>
        {
            var pi = tab.Col("TaskPath");
            var path = (pi >= 0 ? row[pi] : "").Replace('/', '\\');
            var name = Key(tab, row, "TaskName");
            // TaskPath uses backslashes (\Microsoft\Windows\…); they're allowed only as folder separators here.
            var full = (path.EndsWith('\\') ? path : path + "\\") + name;
            if (!Regex.IsMatch(full, @"^\\[^""'\r\n\t<>|&^%]*$")) throw new InvalidOperationException("That task name can't be used here.");
            var cmd = $"schtasks.exe {verb} /TN \"{full}\"";
            var rc = await t.InvokeAsync("Win32_Process", "Create", new Dictionary<string, object> { ["CommandLine"] = cmd }).ConfigureAwait(false);
            return Result(rc, label.TrimStart('▶', '✓', '⊘', ' ') + " " + full);
        });

    /// <summary>WMI's return codes, said plainly.</summary>
    public static string Result(uint rc, string what) => rc switch
    {
        0 => what + ": done.",
        2 => what + ": access denied (that needs administrator rights on the computer).",
        3 => what + ": not enough privileges.",
        5 => what + ": the service can't accept that now.",
        6 => what + ": the service isn't running.",
        7 => what + ": the service didn't respond in time.",
        8 => what + ": failed (unknown reason).",
        10 => what + ": it is already running.",
        14 => what + ": the service is disabled.",
        15 => what + ": the service couldn't log on.",
        21 => what + ": not a valid value.",
        _ => what + $": failed (code {rc}).",
    };
}

/// <summary>Windows' own MMC consoles, opened for the chosen computer where they can manage one remotely.</summary>
public static class ClassicConsoles
{
    public sealed record Console(string Title, string About, string File, bool Remote, string RemoteArg);

    public static readonly IReadOnlyList<Console> All =
    [
        new("Computer Management", "Services, events, shares, users and groups, disks — together", "compmgmt.msc", true, "/computer="),
        new("Event Viewer", "Every event log, with filters and custom views", "eventvwr.msc", true, "/computer="),
        new("Services", "Windows services", "services.msc", true, "/computer="),
        new("Task Scheduler", "Scheduled tasks", "taskschd.msc", true, "/computer="),
        new("Shared Folders", "Shares, sessions and open files", "fsmgmt.msc", true, "/computer="),
        new("Local Users and Groups", "Local accounts (not on Home editions)", "lusrmgr.msc", true, "/computer="),
        new("Disk Management", "Disks, partitions and volumes", "diskmgmt.msc", false, ""),
        new("Device Manager", "Hardware and drivers", "devmgmt.msc", false, ""),
        new("Performance Monitor", "Counters, data collectors and reports", "perfmon.msc", false, ""),
        new("Windows Defender Firewall", "Firewall rules (with advanced security)", "wf.msc", false, ""),
        new("Certificates (computer)", "The computer's certificate stores", "certlm.msc", false, ""),
        new("Local Group Policy", "This PC's local policy (Pro and above)", "gpedit.msc", false, ""),
    ];

    /// <summary>Opens the console (never in the self-test), for the chosen computer when it can manage one remotely.</summary>
    public static string Open(WinTarget t, string title)
    {
        var c = All.FirstOrDefault(x => x.Title == title) ?? throw new InvalidOperationException("Unknown console.");
        if (!t.IsLocal && !c.Remote) throw new InvalidOperationException(c.Title + " can only manage this PC.");
        if (Services.TestMode.Running) return "Not opened in the self-test: " + c.Title;
        var psi = new System.Diagnostics.ProcessStartInfo(System.IO.Path.Combine(Environment.SystemDirectory, "mmc.exe")) { UseShellExecute = false };
        psi.ArgumentList.Add(c.File);
        if (!t.IsLocal) psi.ArgumentList.Add(c.RemoteArg + t.Computer);
        System.Diagnostics.Process.Start(psi)?.Dispose();
        Services.AuditLog.Write("you", "windows tools", "classic console opened", c.Title + (t.IsLocal ? "" : " · " + t.Computer));
        return "Opened " + c.Title + (t.IsLocal ? "." : " for " + t.Computer + " (it uses your Windows sign-in).");
    }
}
