using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DcxManager.Services;

namespace DcxManager.Infra;

/// <summary>
/// Microsoft Hyper-V through Windows PowerShell's Hyper-V module (ADR-028): on this PC, or on a Hyper-V host over WinRM
/// (CIM). Nothing is installed or reconfigured by the app.
/// <list type="bullet">
/// <item>Authentication: by default your Windows sign-in (Kerberos in a domain) — no password stored or sent by the app.
/// Optionally a vault credential (DOMAIN\user + password) for a CIM session; it is passed to PowerShell on standard
/// input, never on a command line.</item>
/// <item>Transport: WinRM as Windows configures it. The app never turns on Basic authentication, unencrypted WinRM or
/// TrustedHosts; if the host needs those, connecting fails with Windows' reason and the help explains the choices.</item>
/// <item>Every script is a fixed text; every value (VM id, names, paths, sizes) reaches it as JSON on standard input, so
/// nothing a user, server or AI types can become PowerShell code.</item>
/// <item>Inventory: host, storage paths, VMs, virtual switches, checkpoints. Changes: VM power, checkpoints (create,
/// apply, rename, delete), export, new VM, import, vCPU count, startup memory, rename, switch connection and delete VM
/// (ADR-031) — each verified by re-reading. Read: per-VM details (adapters and IPs, disks, integration services).</item>
/// </list>
/// </summary>
public sealed class HyperVProvider : IInfraProvider
{
    private readonly ProviderConfig _c;
    public ProviderConfig Config => _c;
    public string Where => IsLocal ? "this PC (Hyper-V)" : "winrm:" + _c.Endpoint.Trim();
    public NetClass? NetworkClass => IsLocal ? NetClass.Local : NetworkPolicy.ClassifyHost(_c.Endpoint.Trim());
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(2);
    public TimeSpan LongTimeout { get; set; } = TimeSpan.FromHours(2);

    /// <summary>Test seam: runs a script with its JSON input → (exit code, stdout, stderr). Default: a real powershell.exe.</summary>
    internal static Func<string, string, TimeSpan, CancellationToken, Task<(int Exit, string Out, string Err)>> Run = RunPowerShellAsync;

    public HyperVProvider(ProviderConfig c) => _c = c;

    /// <summary>Set when the last call found no Hyper-V module on this PC: the edition, for the one-click fix.</summary>
    public HyperVSetup.Edition? MissingModule { get; private set; }

    private bool IsLocal => _c.Endpoint.Trim() is "" or "." or "localhost" || _c.Endpoint.Trim().Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase);

    /// <summary>A computer name or address: letters, digits, dots, hyphens (no URL, no spaces, nothing PowerShell could read as code).</summary>
    public static string? ValidateHost(string host) =>
        host.Trim() is "" or "." or "localhost" ? null
        : Regex.IsMatch(host.Trim(), @"^[A-Za-z0-9]([A-Za-z0-9.\-]{0,252}[A-Za-z0-9])?$|^[0-9a-fA-F:]{2,39}$") ? null
        : "Enter the Hyper-V host's computer name or address (e.g. hv01.corp.example), or leave it empty for this PC.";

    public static bool ValidVmId(string id) => Guid.TryParse(id, out _);

    // ---- PowerShell plumbing -------------------------------------------------------------------------------------------

    /// <summary>Opens the target: $p holds -ComputerName or -CimSession for the Hyper-V cmdlets. A CIM session is removed at
    /// the end. Values come only from $in (the JSON on standard input).</summary>
    private const string Prelude = """
        $ErrorActionPreference = 'Stop'; $ProgressPreference = 'SilentlyContinue'
        $in = [Console]::In.ReadToEnd() | ConvertFrom-Json
        if (-not (Get-Module -ListAvailable -Name Hyper-V)) { $os = Get-CimInstance Win32_OperatingSystem; [Console]::Error.WriteLine('NO_MODULE|' + $os.ProductType + '|' + $os.Caption); exit 3 }
        Import-Module Hyper-V
        $p = @{}; $cim = $null
        if ($in.computer) {
          if ($in.user) { $cred = New-Object System.Management.Automation.PSCredential($in.user, (ConvertTo-SecureString $in.password -AsPlainText -Force)); $cim = New-CimSession -ComputerName $in.computer -Credential $cred; $p.CimSession = $cim }
          else { $p.ComputerName = $in.computer }
        }
        try {
        """;
    private const string Postlude = """

        } finally { if ($cim) { Remove-CimSession $cim } }
        """;

    private async Task<JsonNode?> ExecAsync(string body, JsonObject values, CancellationToken ct, bool longRunning = false)
    {
        if (ValidateHost(_c.Endpoint) is { } bad) throw new InvalidOperationException(bad);
        if (!IsLocal) await Offline.EnforceAsync(_c.Endpoint.Trim(), "Hyper-V (WinRM)", ct);
        var input = (JsonObject)values.DeepClone();
        if (!IsLocal) input["computer"] = _c.Endpoint.Trim();
        if (await ExternalCredentials.UseAsync(_c.CredentialId, "Hyper-V " + _c.Name, ct) is { Username.Length: > 0, Secret.Length: > 0 } cred) { input["user"] = cred.Username; input["password"] = cred.Secret; }
        var (exit, outText, err) = await Run(Prelude + body + Postlude, input.ToJsonString(), longRunning ? LongTimeout : Timeout, ct);
        if (exit == 3 && HyperVSetup.ParseNoModule(err) is { } edition)
        {
            // 0.5.1 #6: the fix for this edition (Server, Pro / Enterprise, or none on Home), offered as one click.
            MissingModule = edition;
            var (why, command) = HyperVSetup.ModuleFix(edition);
            throw new InvalidOperationException(why + (command != null ? " One click installs it: Install the Hyper-V module (UAC)." : ""));
        }
        MissingModule = null;
        if (exit != 0) throw new InvalidOperationException(Explain(err));
        var json = outText.Trim();
        return json.Length == 0 ? null : JsonNode.Parse(json);
    }

    /// <summary>PowerShell's error in one line, with the usual causes named (never the input, which can hold a password).</summary>
    internal static string Explain(string err)
    {
        var line = err.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith("At line", StringComparison.Ordinal) && !l.StartsWith("+", StringComparison.Ordinal)) ?? "PowerShell failed";
        if (line.Length > 300) line = line[..300] + "…";
        if (line.Contains("WinRM", StringComparison.OrdinalIgnoreCase) || line.Contains("WS-Management", StringComparison.OrdinalIgnoreCase))
            return line + " — WinRM must be enabled on the host (Enable-PSRemoting) and reachable (TCP 5985/5986); outside a domain, see Help → Hyper-V for the secure options.";
        if (line.Contains("Access", StringComparison.OrdinalIgnoreCase) && line.Contains("denied", StringComparison.OrdinalIgnoreCase) || line.Contains("permission", StringComparison.OrdinalIgnoreCase))
            return line + " — the account needs to be in the host's “Hyper-V Administrators” group (or an administrator).";
        return line;
    }

    /// <summary>Runs Windows PowerShell 5.1 with the script as -EncodedCommand (it holds no secrets) and the values on
    /// standard input. Killed on timeout or cancellation.</summary>
    private static async Task<(int, string, string)> RunPowerShellAsync(string script, string input, TimeSpan timeout, CancellationToken ct)
    {
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-OutputFormat", "Text", "-EncodedCommand",
                     Convert.ToBase64String(Encoding.Unicode.GetBytes("[Console]::OutputEncoding = [Text.Encoding]::UTF8\n" + script)) })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("couldn't start Windows PowerShell");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await p.StandardInput.WriteAsync(input);
            p.StandardInput.Close();
            var outTask = ReadOnThread(p.StandardOutput, "hyper-v stdout");
            var errTask = ReadOnThread(p.StandardError, "hyper-v stderr");
            await p.WaitForExitAsync(cts.Token);
            await Task.WhenAll(outTask, errTask).WaitAsync(cts.Token);
            return (p.ExitCode, await outTask, await errTask);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException($"Hyper-V didn't answer within {timeout.TotalMinutes:0} minute(s)");
        }
    }

    /// <summary>Reads a redirected pipe to the end on a short-lived thread of its own. Redirected process pipes are synchronous
    /// handles, so ReadToEndAsync would block a thread-pool thread for as long as PowerShell runs (s36c, as in
    /// AiClient.RunLocalAsync). The thread ends when the pipe closes, which killing the process also does.</summary>
    private static Task<string> ReadOnThread(StreamReader r, string name)
    {
        var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        new Thread(() =>
        {
            try { done.TrySetResult(r.ReadToEnd()); }
            catch (Exception ex) { done.TrySetException(ex); }
        }) { IsBackground = true, Name = name }.Start();
        return done.Task;
    }

    // ---- detect / inventory --------------------------------------------------------------------------------------------

    public async Task<ProviderCaps> DetectAsync(CancellationToken ct)
    {
        if (ValidateHost(_c.Endpoint) is { } bad) return ProviderCaps.Unreachable(bad);
        try
        {
            var h = await ExecAsync("""
                $h = Get-VMHost @p
                $os = Get-CimInstance -ClassName Win32_OperatingSystem @p
                $id = [Security.Principal.WindowsIdentity]::GetCurrent(); $pr = New-Object Security.Principal.WindowsPrincipal($id)
                [pscustomobject]@{ name = $h.ComputerName; cpus = $h.LogicalProcessorCount; memory = $h.MemoryCapacity; vmPath = $h.VirtualMachinePath; vhdPath = $h.VirtualHardDiskPath; version = [string]$h.FullyQualifiedDomainName;
                  os = [string]$os.Caption; productType = [int]$os.ProductType;
                  admin = [bool]($id.Groups -contains [Security.Principal.SecurityIdentifier]'S-1-5-32-544'); elevated = $pr.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator);
                  hvAdmin = $pr.IsInRole([Security.Principal.SecurityIdentifier]'S-1-5-32-578') } | ConvertTo-Json -Compress
                """, new JsonObject(), ct);
            bool B(string k) => h?[k] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
            var notes = new List<string>
            {
                IsLocal ? "This PC's Hyper-V, with your Windows sign-in." : AppData.Current.FindCredential(_c.CredentialId) != null
                    ? "Connected over WinRM with a CIM session and the chosen vault credential (passed to PowerShell on standard input)."
                    : "Connected over WinRM with your Windows sign-in (Kerberos). No password is stored.",
                IsLocal ? HyperVSetup.RightsNote(B("admin"), B("elevated"), B("hvAdmin"))
                    : "Changes need membership of the host's “Hyper-V Administrators” group; Windows refuses them otherwise and the reason is shown.",
                "Export and import paths are on the Hyper-V host's disks, not this PC's (unless this PC is the host).",
            };
            var os = h?["os"]?.ToString() is { Length: > 0 } caption ? " · " + caption : "";
            return new ProviderCaps(true, true, true, "", $"Hyper-V on {h?["name"]} · {h?["cpus"]} logical CPUs{os}", notes);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.Text.Json.JsonException or OfflineBlockedException or System.ComponentModel.Win32Exception)
        {
            return ProviderCaps.Unreachable(ex.Message);
        }
    }

    private const string InventoryScript = """
        $h = Get-VMHost @p
        $vms = @(Get-VM @p | ForEach-Object { [pscustomobject]@{ id = [string]$_.Id; name = $_.Name; state = [string]$_.State; cpu = $_.ProcessorCount; mem = $_.MemoryAssigned; memStartup = $_.MemoryStartup; dynamic = [bool]$_.DynamicMemoryEnabled; switch = [string](@($_.NetworkAdapters)[0].SwitchName); up = [int]$_.Uptime.TotalSeconds; gen = $_.Generation; ver = [string]$_.Version; status = [string]$_.Status; ckType = [string]$_.CheckpointType; replica = [string]$_.ReplicationState } })
        $sw = @(Get-VMSwitch @p | ForEach-Object { [pscustomobject]@{ id = [string]$_.Id; name = $_.Name; type = [string]$_.SwitchType } })
        $cp = @(Get-VMSnapshot @p -VMName * -ErrorAction SilentlyContinue | ForEach-Object { [pscustomobject]@{ id = [string]$_.Id; name = $_.Name; vm = $_.VMName; vmId = [string]$_.VMId; created = $_.CreationTime.ToString('yyyy-MM-dd HH:mm') } })
        [pscustomobject]@{ host = [pscustomobject]@{ name = $h.ComputerName; cpus = $h.LogicalProcessorCount; memory = $h.MemoryCapacity; vmPath = $h.VirtualMachinePath; vhdPath = $h.VirtualHardDiskPath }; vms = $vms; switches = $sw; checkpoints = $cp } | ConvertTo-Json -Depth 4 -Compress
        """;

    public async Task<IReadOnlyList<InfraObject>> InventoryAsync(CancellationToken ct) =>
        Parse(await ExecAsync(InventoryScript, new JsonObject(), ct) as JsonObject ?? new JsonObject());

    public static string NormalizeState(string? s) => (s ?? "").ToLowerInvariant() switch
    {
        "running" => "running",
        "off" => "stopped",
        "saved" => "saved",
        "paused" => "paused",
        var x when x.Length > 0 => x,   // starting, stopping, saving, … (transitional)
        _ => "unknown",
    };

    /// <summary>The inventory JSON (untrusted: a malformed or hostile answer yields fewer objects, never a crash).</summary>
    public static List<InfraObject> Parse(JsonObject d)
    {
        var list = new List<InfraObject>();
        string S(JsonNode? n, string k) => n?[k]?.ToString() ?? "";
        static string Gb(string bytes) => double.TryParse(bytes, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var b) && b > 0 ? $"{b / (1L << 30):0.#} GB" : "?";
        static IEnumerable<JsonObject> Arr(JsonNode? n) => n switch { JsonArray a => a.OfType<JsonObject>(), JsonObject o => new[] { o }, _ => Enumerable.Empty<JsonObject>() };   // one item comes back as an object
        var host = d["host"];
        var hostName = S(host, "name");
        if (host != null)
        {
            list.Add(new InfraObject("host:" + hostName, "host", hostName, "online", "", $"{S(host, "cpus")} logical CPUs · RAM {Gb(S(host, "memory"))}", new Dictionary<string, string> { ["vhdPath"] = S(host, "vhdPath") }));
            list.Add(new InfraObject("path:vm", "path", "Virtual machines", "available", hostName, S(host, "vmPath"), new Dictionary<string, string>()));
            list.Add(new InfraObject("path:vhd", "path", "Virtual hard disks", "available", hostName, S(host, "vhdPath"), new Dictionary<string, string>()));
        }
        foreach (var v in Arr(d["vms"]))
        {
            var id = S(v, "id");
            if (!ValidVmId(id)) continue;
            var state = NormalizeState(S(v, "state"));
            list.Add(new InfraObject(id, "vm", S(v, "name"), state, hostName,
                $"Gen {S(v, "gen")} · {S(v, "cpu")} vCPU · RAM {Gb(S(v, "mem"))}" + (state == "running" && int.TryParse(S(v, "up"), out var up) ? $" · up {TimeSpan.FromSeconds(up):d\\.hh\\:mm}" : "") + (S(v, "status") is { Length: > 0 } st && st != "Operating normally" ? " · " + st : "")
                    + (S(v, "replica") is { Length: > 0 } rep && rep != "Disabled" ? " · replica " + rep.ToLowerInvariant() : ""),
                new Dictionary<string, string> { ["vmId"] = id, ["cpu"] = S(v, "cpu"), ["memStartup"] = S(v, "memStartup"), ["dynamic"] = S(v, "dynamic"), ["switch"] = S(v, "switch"), ["generation"] = S(v, "gen"), ["checkpointType"] = S(v, "ckType"), ["replica"] = S(v, "replica") }));
        }
        foreach (var s in Arr(d["switches"]))
            list.Add(new InfraObject(S(s, "id"), "switch", S(s, "name"), "available", hostName, $"{S(s, "type").ToLowerInvariant()} switch", new Dictionary<string, string>()));
        foreach (var c in Arr(d["checkpoints"]))
        {
            if (!ValidVmId(S(c, "id")) || !ValidVmId(S(c, "vmId"))) continue;
            list.Add(new InfraObject(S(c, "id"), "checkpoint", S(c, "name"), "available", S(c, "vm"), $"checkpoint of {S(c, "vm")} · {S(c, "created")}", new Dictionary<string, string> { ["vmId"] = S(c, "vmId") }));
        }
        return list;
    }

    // ---- actions --------------------------------------------------------------------------------------------------------

    public static readonly InfraAction Start = new("start", "▶ Start", true, Consequence.None, "running", "Starts the VM (or resumes it from saved).");
    public static readonly InfraAction Shutdown = new("shutdown", "⏻ Shut down", true, Consequence.ServiceInterruption, "stopped", "Shuts the guest OS down through the shutdown integration service. Its services go down.");
    public static readonly InfraAction Save = new("save", "⤓ Save state", true, Consequence.ServiceInterruption, "saved", "Saves the VM's memory to disk and stops it; Start resumes exactly there.");
    public static readonly InfraAction Pause = new("pause", "‖ Pause", true, Consequence.ServiceInterruption, "paused", "Freezes the VM in memory.");
    public static readonly InfraAction Resume = new("resume", "▷ Resume", true, Consequence.None, "running", "Resumes a paused VM.");
    public static readonly InfraAction TurnOff = new("turnoff", "■ Turn off (hard)", true, Consequence.DataLoss, "stopped", "Cuts the VM's power immediately: unsaved data inside it can be lost. Prefer Shut down.");
    public static readonly InfraAction Reset = new("reset", "⚡ Reset (hard)", true, Consequence.DataLoss, "running", "Hard reset, like the reset button: unsaved data inside the VM can be lost.");
    public static readonly InfraAction Checkpoint = new("checkpoint", "◷ Checkpoint…", true, Consequence.None, null, "Takes a checkpoint (production checkpoint where the guest supports it).")
    {
        Inputs = new[] { new InfraInput("name", "Checkpoint name", Default: "{name} {now}") },
    };
    public static readonly InfraAction Export = new("export", "⇩ Export…", true, Consequence.None, null, "Exports the VM (configuration, disks, checkpoints) to a folder ON THE HYPER-V HOST. Large VMs take a long time; the export continues on the host if this app is closed.")
    {
        Inputs = new[] { new InfraInput("path", "Folder on the Hyper-V host", Hint: @"e.g. D:\Exports (the VM gets its own sub-folder)") },
    };
    public static readonly InfraAction Apply = new("apply", "↺ Apply checkpoint…", true, Consequence.DataLoss, null, "Returns the VM to this checkpoint. Everything since then that isn't in another checkpoint is LOST. The VM is turned off if it runs.");
    public static readonly InfraAction RemoveCheckpoint = new("remove-checkpoint", "✕ Delete checkpoint", true, Consequence.DataLoss, null, "Deletes the checkpoint (its differencing disk is merged). It can't be applied afterwards.");
    public static readonly InfraAction NewVm = new("new-vm", "＋ New VM…", true, Consequence.None, null, "Creates a VM on this host with a new, empty virtual disk in the host's default disk folder (install an OS afterwards, e.g. from an ISO).")
    {
        Inputs = new[]
        {
            new InfraInput("name", "VM name"), new InfraInput("memoryMb", "Startup memory (MB)", "2048", Number: true),
            new InfraInput("generation", "Generation", "2", Options: new[] { "1", "2" }), new InfraInput("switch", "Virtual switch", Required: false, Hint: "empty = not connected"),
            new InfraInput("diskGb", "New disk size (GB)", "40", Number: true),
        },
    };
    public static readonly InfraAction Import = new("import", "⇧ Import VM…", true, Consequence.None, null, "Imports an exported VM from a .vmcx file ON THE HYPER-V HOST as a copy with a new id (the export stays untouched).")
    {
        Inputs = new[] { new InfraInput("path", @"Path of the .vmcx file on the host", Hint: @"…\Virtual Machines\<GUID>.vmcx inside the export") },
    };

    // ADR-031: configuration and housekeeping the Hyper-V module offers, each verified by re-reading the inventory.
    public static readonly InfraAction Details = new("details", "ⓘ Details", false, Consequence.None, null, "Network adapters (switch, MAC, IP addresses the integration services report), virtual disks, integration services and the automatic start / stop actions.");
    public static readonly InfraAction Cpu = new("cpu", "⚙ vCPU count…", true, Consequence.None, null, "Sets the number of virtual processors (Set-VMProcessor). The VM must be off.")
    {
        Inputs = new[] { new InfraInput("count", "Virtual processors", "{cpu}", Number: true) },
    };
    public static readonly InfraAction Memory = new("memory", "⚙ Startup memory…", true, Consequence.None, null, "Sets the startup memory (Set-VMMemory). The VM must be off; with dynamic memory this is the amount it starts with.")
    {
        Inputs = new[] { new InfraInput("memoryMb", "Startup memory (MB)", Number: true) },
    };
    public static readonly InfraAction Rename = new("rename", "✎ Rename…", true, Consequence.None, null, "Renames the VM in Hyper-V (Rename-VM). Its files and id stay the same.")
    {
        Inputs = new[] { new InfraInput("name", "New name", "{name}") },
    };
    public static readonly InfraAction ConnectSwitch = new("connect-switch", "⇄ Connect to switch…", true, Consequence.ServiceInterruption, null, "Connects the VM's first network adapter to another virtual switch (Connect-VMNetworkAdapter). Its network connection moves, which interrupts open sessions.")
    {
        Inputs = new[] { new InfraInput("switch", "Virtual switch", Hint: "the switch's exact name, as listed under the host") },
    };
    public static readonly InfraAction RemoveVm = new("remove-vm", "✕ Delete VM", true, Consequence.DataLoss, null, "Deletes the VM from Hyper-V (Remove-VM): its configuration, saved state and checkpoints are removed. Its virtual disk files stay on the host's disk.");
    public static readonly InfraAction RenameCheckpoint = new("rename-checkpoint", "✎ Rename checkpoint…", true, Consequence.None, null, "Renames the checkpoint (Rename-VMSnapshot).")
    {
        Inputs = new[] { new InfraInput("name", "New name", "{name}") },
    };

    public IReadOnlyList<InfraAction> ActionsFor(InfraObject o) => o.Kind switch
    {
        "vm" => new[] { Start, Shutdown, Save, Pause, Resume, TurnOff, Reset, Checkpoint, Export, Details, Cpu, Memory, Rename, ConnectSwitch, RemoveVm },
        "checkpoint" => new[] { Apply, RenameCheckpoint, RemoveCheckpoint },
        "host" => new[] { NewVm, Import },
        _ => Array.Empty<InfraAction>(),
    };

    public string? Forbidden(InfraAction a, InfraObject o, ProviderCaps caps) => a.Id switch
    {
        "start" when o.Status == "running" => "already running",
        "start" when o.Status == "paused" => "paused — use Resume",
        "shutdown" or "save" or "pause" or "reset" when o.Status != "running" => "not running",
        "resume" when o.Status != "paused" => "not paused",
        "turnoff" when o.Status is "stopped" or "saved" => "already off",
        "cpu" or "memory" or "remove-vm" when o.Status != "stopped" => "turn the VM off first",
        _ => null,
    };

    public Task<ActionResult> RunAsync(InfraAction a, InfraObject o, CancellationToken ct) => RunAsync(a, o, new Dictionary<string, string>(), ct);

    public async Task<ActionResult> RunAsync(InfraAction a, InfraObject o, IReadOnlyDictionary<string, string> input, CancellationToken ct)
    {
        foreach (var i in a.Inputs) if (i.Validate(input.GetValueOrDefault(i.Name) ?? "") is { } bad) return new ActionResult(false, false, "refused: " + bad);
        var vmId = o.Props.GetValueOrDefault("vmId") ?? "";
        if (o.Kind is "vm" or "checkpoint" && !ValidVmId(vmId)) return new ActionResult(false, false, "refused: unexpected VM id");
        if (o.Kind == "checkpoint" && !ValidVmId(o.Id)) return new ActionResult(false, false, "refused: unexpected checkpoint id");
        var v = new JsonObject { ["vm"] = vmId, ["cp"] = o.Kind == "checkpoint" ? o.Id : null };
        foreach (var (k, val) in input) v["arg_" + k] = val;
        string script = a.Id switch
        {
            "start" => "Start-VM -VM (Get-VM @p -Id $in.vm)",
            "shutdown" => "Stop-VM -VM (Get-VM @p -Id $in.vm) -Force",
            "save" => "Save-VM -VM (Get-VM @p -Id $in.vm)",
            "pause" => "Suspend-VM -VM (Get-VM @p -Id $in.vm)",
            "resume" => "Resume-VM -VM (Get-VM @p -Id $in.vm)",
            "turnoff" => "Stop-VM -VM (Get-VM @p -Id $in.vm) -TurnOff -Force",
            "reset" => "Restart-VM -VM (Get-VM @p -Id $in.vm) -Force",
            "checkpoint" => "Checkpoint-VM -VM (Get-VM @p -Id $in.vm) -SnapshotName $in.arg_name",
            "export" => "Export-VM -VM (Get-VM @p -Id $in.vm) -Path $in.arg_path",
            "apply" => "Get-VMSnapshot -VM (Get-VM @p -Id $in.vm) | Where-Object { [string]$_.Id -eq $in.cp } | Restore-VMSnapshot -Confirm:$false",
            "remove-checkpoint" => "Get-VMSnapshot -VM (Get-VM @p -Id $in.vm) | Where-Object { [string]$_.Id -eq $in.cp } | Remove-VMSnapshot -Confirm:$false",
            "new-vm" => """
                $h = Get-VMHost @p
                $vhd = Join-Path $h.VirtualHardDiskPath ($in.arg_name + '.vhdx')
                $a = @{ Name = $in.arg_name; MemoryStartupBytes = [int64]$in.arg_memoryMb * 1MB; Generation = [int16]$in.arg_generation; NewVHDPath = $vhd; NewVHDSizeBytes = [int64]$in.arg_diskGb * 1GB }
                if ($in.arg_switch) { $a.SwitchName = $in.arg_switch }
                $vm = New-VM @p @a
                [pscustomobject]@{ id = [string]$vm.Id } | ConvertTo-Json -Compress
                """,
            "import" => "$vm = Import-VM @p -Path $in.arg_path -Copy -GenerateNewId; [pscustomobject]@{ id = [string]$vm.Id } | ConvertTo-Json -Compress",
            "details" => """
                $vm = Get-VM @p -Id $in.vm
                [pscustomobject]@{
                  adapters = @($vm.NetworkAdapters | ForEach-Object { [pscustomobject]@{ name = $_.Name; switch = [string]$_.SwitchName; mac = $_.MacAddress; ips = @($_.IPAddresses); status = [string]($_.Status -join ', ') } })
                  disks = @($vm | Get-VMHardDiskDrive | ForEach-Object { [pscustomobject]@{ controller = "$($_.ControllerType) $($_.ControllerNumber):$($_.ControllerLocation)"; path = $_.Path } })
                  integration = @($vm | Get-VMIntegrationService | ForEach-Object { [pscustomobject]@{ name = $_.Name; enabled = [bool]$_.Enabled; status = [string]$_.PrimaryStatusDescription } })
                  automaticStart = [string]$vm.AutomaticStartAction; automaticStop = [string]$vm.AutomaticStopAction; notes = $vm.Notes
                } | ConvertTo-Json -Depth 4 -Compress
                """,
            "cpu" => "Set-VMProcessor -VM (Get-VM @p -Id $in.vm) -Count ([int64]$in.arg_count)",
            "memory" => "Set-VMMemory -VM (Get-VM @p -Id $in.vm) -StartupBytes ([int64]$in.arg_memoryMb * 1MB)",
            "rename" => "Rename-VM -VM (Get-VM @p -Id $in.vm) -NewName $in.arg_name",
            "connect-switch" => "@((Get-VM @p -Id $in.vm).NetworkAdapters)[0] | Connect-VMNetworkAdapter -SwitchName $in.arg_switch",
            "remove-vm" => "Remove-VM -VM (Get-VM @p -Id $in.vm) -Force",
            "rename-checkpoint" => "Get-VMSnapshot -VM (Get-VM @p -Id $in.vm) | Where-Object { [string]$_.Id -eq $in.cp } | Rename-VMSnapshot -NewName $in.arg_name",
            _ => "",
        };
        if (script.Length == 0) return new ActionResult(false, false, "refused: unknown action");
        if (a.Id == "new-vm" && (!long.TryParse(input["memoryMb"], out var mb) || mb is < 32 or > 1_048_576 || !long.TryParse(input["diskGb"], out var gb) || gb is < 1 or > 65_536))
            return new ActionResult(false, false, "refused: memory must be 32 MB – 1 TB and the disk 1 GB – 64 TB");
        if (a.Id == "cpu" && (!long.TryParse(input["count"], out var cpus) || cpus is < 1 or > 2048)) return new ActionResult(false, false, "refused: 1 – 2048 virtual processors");
        if (a.Id == "memory" && (!long.TryParse(input["memoryMb"], out var smb) || smb is < 32 or > 1_048_576)) return new ActionResult(false, false, "refused: memory must be 32 MB – 1 TB");
        JsonNode? result;
        try { result = await ExecAsync(script, v, ct, longRunning: a.Id is "export" or "import" or "checkpoint" or "apply" or "remove-vm"); }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.Text.Json.JsonException or OfflineBlockedException) { return new ActionResult(false, false, "refused: " + ex.Message); }
        if (a.Id == "details") return new ActionResult(true, true, "details read", FormatDetails(result as JsonObject));

        // Verify by re-reading the inventory.
        var now = await InventoryAsync(ct);
        var after = now.FirstOrDefault(x => x.Id == vmId);
        switch (a.Id)
        {
            case "cpu":
                return after?.Props.GetValueOrDefault("cpu") == input["count"] ? new ActionResult(true, true, $"verified: the VM has {input["count"]} virtual processors")
                    : new ActionResult(true, false, $"Hyper-V accepted it, but the VM reports {after?.Props.GetValueOrDefault("cpu") ?? "?"} virtual processors");
            case "memory":
                return long.TryParse(after?.Props.GetValueOrDefault("memStartup"), out var bytes) && bytes == long.Parse(input["memoryMb"]) * 1024 * 1024
                    ? new ActionResult(true, true, $"verified: startup memory is {input["memoryMb"]} MB") : new ActionResult(true, false, "Hyper-V accepted it, but the startup memory reads differently — refresh");
            case "rename":
                return after?.Name == input["name"] ? new ActionResult(true, true, $"verified: the VM is now called “{input["name"]}”") : new ActionResult(true, false, $"the VM is still listed as “{after?.Name ?? "?"}”");
            case "connect-switch":
                return after?.Props.GetValueOrDefault("switch") == input["switch"] ? new ActionResult(true, true, $"verified: the first adapter is on “{input["switch"]}”")
                    : new ActionResult(true, false, $"the first adapter reports “{after?.Props.GetValueOrDefault("switch") ?? "?"}”");
            case "remove-vm":
                return after == null ? new ActionResult(true, true, "verified: the VM is gone (its disk files stay on the host)") : new ActionResult(true, false, "the VM is still listed — refresh later");
            case "rename-checkpoint":
                return now.FirstOrDefault(x => x.Id == o.Id)?.Name == input["name"] ? new ActionResult(true, true, $"verified: the checkpoint is now “{input["name"]}”") : new ActionResult(true, false, "the checkpoint's name reads differently — refresh");
            case "checkpoint":
                return now.Any(x => x.Kind == "checkpoint" && x.Props.GetValueOrDefault("vmId") == vmId && x.Name == input["name"])
                    ? new ActionResult(true, true, $"verified: checkpoint “{input["name"]}” exists") : new ActionResult(true, false, "Hyper-V accepted it, but the checkpoint isn't listed yet — refresh later");
            case "remove-checkpoint":
                return now.All(x => x.Id != o.Id) ? new ActionResult(true, true, "verified: the checkpoint is gone (its disk merges in the background)") : new ActionResult(true, false, "the checkpoint is still listed — refresh later");
            case "apply":
                return new ActionResult(true, now.Any(x => x.Id == o.Id) && now.Any(x => x.Id == vmId),
                    $"Hyper-V completed the restore; the VM now reports “{now.FirstOrDefault(x => x.Id == vmId)?.Status ?? "?"}” (start it if needed)");
            case "export":
                return new ActionResult(true, false, $"export finished on the host into {input["path"]} — check the folder there (the app can't read the host's disks)");
            case "new-vm" or "import":
            {
                var id = result?["id"]?.ToString() ?? "";
                return now.Any(x => x.Kind == "vm" && x.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                    ? new ActionResult(true, true, $"verified: the VM exists ({id})") : new ActionResult(true, false, "Hyper-V reported success, but the VM isn't listed — refresh later");
            }
            default:
                var state = after?.Status ?? "gone";
                return state == a.ExpectedStatus ? new ActionResult(true, true, $"verified: the VM is now “{state}”")
                    : new ActionResult(true, false, $"requested, but NOT verified: the VM reports “{state}” (expected “{a.ExpectedStatus}”) — refresh in a moment");
        }
    }

    /// <summary>The Details answer as text (untrusted: missing parts are skipped).</summary>
    public static string FormatDetails(JsonObject? d)
    {
        if (d == null) return "(nothing returned)";
        static IEnumerable<JsonObject> Arr(JsonNode? n) => n switch { JsonArray a => a.OfType<JsonObject>(), JsonObject o => new[] { o }, _ => Enumerable.Empty<JsonObject>() };
        static string Ips(JsonNode? n) => n switch { JsonArray a => string.Join(", ", a.Select(x => x?.ToString())), null => "", _ => n.ToString() };
        var sb = new StringBuilder("Network adapters:\n");
        foreach (var a in Arr(d["adapters"])) sb.Append($"  {a["name"]} · switch {(a["switch"]?.ToString() is { Length: > 0 } s ? s : "(not connected)")} · MAC {a["mac"]} · {Ips(a["ips"])} {a["status"]}\n");
        sb.Append("Virtual disks:\n");
        foreach (var x in Arr(d["disks"])) sb.Append($"  {x["controller"]} · {x["path"]}\n");
        sb.Append("Integration services:\n");
        foreach (var x in Arr(d["integration"])) sb.Append($"  {(x["enabled"]?.ToString() == "true" ? "on " : "off")} {x["name"]} · {x["status"]}\n");
        sb.Append($"Automatic start: {d["automaticStart"]} · automatic stop: {d["automaticStop"]}\n");
        if (d["notes"]?.ToString() is { Length: > 0 } notes) sb.Append("Notes: ").Append(notes).Append('\n');
        return sb.ToString();
    }
}
