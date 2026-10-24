using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DcxManager.Cloud;
using DcxManager.Services;
using DcxManager.Ssh;
using DcxManager.Vultr;

namespace DcxManager;

/// <summary>
/// The operator's built-in capabilities (0.2.0 phase 2; before that the switch inside <c>Ai.Assistant</c>): saved hosts
/// over SSH (status, logs, services, commands), Vultr and any cloud provider's instances, and files on hosts or this PC.
/// Registered first, so the schema lists them before the features' capabilities, as it always did. Every one asks for
/// approval with its own exact wording (<see cref="Capability.GatesItself"/>): every change is shown to the user and only
/// happens on a yes; reads ask unless auto-approve of read-only tools is on. Everything is audited under the caller's actor.
/// </summary>
public static class OperatorCapabilities
{
    public static void Register()
    {
        foreach (var c in Build()) Capabilities.Register(c);
    }

    private static string S(JsonObject a, string k, string def = "") => Capabilities.Arg(a, k, def);
    private static bool B(JsonObject a, string k) => Capabilities.Flag(a, k);

    /// <summary>The saved host named in the call. 0.5.1 #10: a padlocked host (or folder) that is closed is refused here,
    /// before anything connects; the connector refuses it again (defence in depth).</summary>
    private static HostEntry HostArg(JsonObject a)
    {
        var h = AppData.Current.FindHostByName(S(a, "host")) ?? throw new InvalidOperationException($"No saved host called '{S(a, "host")}'. Use list_hosts.");
        Locks.Demand("host:" + h.Id, h.Display);
        return h;
    }

    /// <summary>Reads ask unless auto-approve is on; everything else asks.</summary>
    private static async Task<bool> GateAsync(CapabilityContext ctx, bool readOnly, string title, string detail, HostEntry? host = null)
    {
        if (readOnly && AppSettings.Current.AiAutoApproveReadOnly) return true;
        return await ctx.Approve(title, detail, host);
    }

    // vultr_deploy_server (0.6.17): how long to wait for a new server, how often to look, and the self-test's seams.
    internal static TimeSpan DeployWait = TimeSpan.FromMinutes(10), DeployPoll = TimeSpan.FromSeconds(10);
    internal static Func<VultrClient, VultrDeploy.Api>? DeployApi;
    internal static Func<string, int, CancellationToken, Task<Net.NetTests.TcpResult>>? DeployProbe;

    /// <summary>Vultr's generated root password as a vault credential (null when Vultr made none, e.g. SSH keys only).</summary>
    private static Credential? SaveDeployedCredential(DeployPlan plan, string password)
    {
        if (password.Length == 0) return null;
        var cred = new Credential { Name = $"{plan.Label} {(plan.Windows ? "Administrator" : "root")} (Vultr)", Kind = CredentialKind.Password, Username = plan.Windows ? "Administrator" : "root", Secret = password, Category = plan.Windows ? "other" : "ssh", Notes = "Vultr's generated password, saved when the AI Engineer deployed the server." };
        AppData.Current.Credentials.Add(cred);
        AppData.Save();
        return cred;
    }

    /// <summary>A deployed server as a saved host (once per address), signing in with <paramref name="cred"/>. Returns the
    /// sentence for the AI (names only).</summary>
    private static string SaveDeployed(VultrClient v, JsonElement inst, DeployPlan plan, Credential? cred)
    {
        var host = "";
        if (VultrProvider.Instance.Normalize(inst, v.AccountId) is { } ci && ci.Ipv4 is { Length: > 0 } ip && ip != "0.0.0.0")
        {
            if (AppData.Current.Hosts.FirstOrDefault(h => h.Address == ip) is { } existing) host = $"A saved host already uses {ip} ({existing.Display}).";
            else
            {
                var h = Views.HostsView.FromCloud(ci, null);
                if (cred != null) { h.CredentialId = cred.Id; h.Auth = AuthMethod.Password; }
                else if (AppData.Current.FindCredential(AppSettings.Current.DefaultKeyCredentialId) is { Kind: CredentialKind.PrivateKey } key) { h.CredentialId = key.Id; h.Auth = AuthMethod.PrivateKey; }
                if (plan.Windows) { h.Protocol = "rdp"; h.Port = 3389; h.Username = "Administrator"; }
                AppData.Current.Hosts.Add(h);
                host = $"Saved as host “{h.Display}”.";
            }
        }
        else host = "No IPv4 address yet, so it isn't a saved host yet (Cloud → Vultr → Instances → Save as host later).";
        AppData.Save();
        return host + (cred != null ? $" The {cred.Username} password is in the vault as “{cred.Name}” (not shown here)." : "");
    }

    /// <summary>The saved host linked to a cloud instance (so the approval shows its environment, ADR-017/020).</summary>
    private static HostEntry? LinkedHost(CloudProvider p, string instanceId) =>
        AppData.Current.Hosts.FirstOrDefault(h => CloudProviders.ForHost(h) is { } l && l.Provider == p && l.InstanceId == instanceId);

    /// <summary>"local" / "this pc" / "localhost" = this computer; otherwise a saved host.</summary>
    private static (Files.FsRef Ref, string Label) FsArg(JsonObject a, string key)
    {
        var v = S(a, key).Trim();
        if (v.Length == 0 || v.Equals("local", StringComparison.OrdinalIgnoreCase) || v.Equals("this pc", StringComparison.OrdinalIgnoreCase) || v.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return (Files.FsRef.Local, "This PC");
        var h = AppData.Current.FindHostByName(v) ?? throw new InvalidOperationException($"No saved host called '{v}'. Use list_hosts, or 'local' for this PC.");
        if (!h.IsSsh) throw new InvalidOperationException($"{h.Display} is a {(h.Protocol == "rdp" ? "Remote Desktop" : h.IsVnc ? "VNC desktop" : h.Protocol)} host; file tools need SSH/SFTP.");
        Locks.Demand("host:" + h.Id, h.Display);
        return (Files.FsRef.ForHost(h), h.Display);
    }

    /// <summary>Paths that look like keys, credentials or password files: never read, flagged in transfers
    /// (<see cref="Redaction.LooksSecretPath"/>, in Core since 0.2.0 phase 6f so the AI's chat side shares the rule).</summary>
    public static bool LooksSecret(string path) => Redaction.LooksSecretPath(path);

    private static readonly (string, string, string, bool)[] None = Array.Empty<(string, string, string, bool)>();
    private static (string, string, string, bool)[] P(params (string Name, string Type, string Desc, bool Required)[] ps) => ps;

    private static IEnumerable<Capability> Build()
    {
        yield return new Capability("list_hosts", "List the saved servers (name, address, user, group, tags).", CapabilityRisk.Read, None,
            // A closed padlock: the name only, marked locked (no address, user or tags).
            (_, _, _) => Task.FromResult(JsonSerializer.Serialize(AppData.Current.Hosts.Select(h => Locks.Hidden("host:" + h.Id) ? (object)new { name = h.Display, locked = true } : new { name = h.Display, address = h.Address, port = h.Port, user = h.Username, group = h.Group, tags = h.Tags, environment = Envs.For(h), cloud = CloudProviders.ForHost(h) is { } l ? $"{l.Provider.Id}:{l.InstanceId}" : null }))),
            GatesItself: true);

        yield return new Capability("host_overview", "Live status of a saved Linux host: OS, uptime, load, CPU, memory, disks, failed units, reboot-required.", CapabilityRisk.Read,
            P(("host", "string", "Saved host name or address", true)),
            async (a, ctx, ct) =>
            {
                var h = HostArg(a);
                if (!await GateAsync(ctx, true, "AI: read server status", $"Gather an overview of {h.Display}", h)) return "User declined.";
                return (await LinuxProbe.ProbeAsync(h, ct)).Summary();
            }, GatesItself: true);

        yield return new Capability("read_logs", "Read the systemd journal on a host (read-only).", CapabilityRisk.Read,
            P(("host", "string", "Saved host name or address", true), ("unit", "string", "systemd unit, e.g. nginx.service (optional)", false),
              ("lines", "integer", "How many lines (10-500, default 100)", false), ("priority", "string", "err, warning, info... (optional)", false),
              ("since", "string", "e.g. '1 hour ago' (optional)", false)),
            async (a, ctx, ct) =>
            {
                var h = HostArg(a);
                var unit = S(a, "unit"); var lines = Math.Clamp(int.TryParse(S(a, "lines", "100"), out var n) ? n : 100, 10, 500);
                var prio = S(a, "priority");
                var cmd = "journalctl --no-pager -o short-iso -n " + lines + (unit.Length > 0 ? " -u " + RemoteExec.Quote(unit) : "") +
                          (prio.Length > 0 ? " -p " + RemoteExec.Quote(prio) : "") + (S(a, "since").Length > 0 ? " --since " + RemoteExec.Quote(S(a, "since")) : "");
                if (!await GateAsync(ctx, true, "AI: read logs", $"{h.Display}\n\n{cmd}", h)) return "User declined.";
                var r = await SshPool.RunAsync(h, cmd, true, ct);
                return r.Combined;
            }, GatesItself: true);

        yield return new Capability("list_services", "List systemd services on a host.", CapabilityRisk.Read,
            P(("host", "string", "Saved host name or address", true), ("state", "string", "Filter: running, failed, inactive (optional)", false)),
            async (a, ctx, ct) =>
            {
                var h = HostArg(a);
                var state = S(a, "state");
                var cmd = "systemctl list-units --type=service --no-pager --no-legend --plain" + (state.Length > 0 ? " --state=" + RemoteExec.Quote(state) : "");
                if (!await GateAsync(ctx, true, "AI: list services", $"{h.Display}\n\n{cmd}", h)) return "User declined.";
                return (await SshPool.RunAsync(h, cmd, false, ct)).Combined;
            }, GatesItself: true);

        yield return new Capability("service_action", "Start/stop/restart/reload/enable/disable or get status of a systemd service. Changes need user approval.", CapabilityRisk.Change,
            P(("host", "string", "Saved host name or address", true), ("service", "string", "Unit name", true), ("action", "string", "start|stop|restart|reload|enable|disable|status", true)),
            async (a, ctx, ct) =>
            {
                var h = HostArg(a);
                var svc = S(a, "service"); var act = S(a, "action");
                if (act is not ("start" or "stop" or "restart" or "reload" or "enable" or "disable" or "status")) return "ERROR: action must be start|stop|restart|reload|enable|disable|status";
                var cmd = $"systemctl {act} {RemoteExec.Quote(svc)}" + (act == "status" ? " --no-pager -l" : "");
                if (!await GateAsync(ctx, act == "status", $"AI: {act} service", $"{h.Display}\n\n{cmd}", h)) return "User declined.";
                if (act != "status") AuditLog.Write(ctx.Actor, h.Display, cmd, "approved");
                var r = await SshPool.RunAsync(h, cmd, act != "status", ct);
                return $"exit code {r.ExitCode}\n{r.Combined}";
            }, GatesItself: true);

        yield return new Capability("run_command", "Run a shell command on a saved host (non-interactive). Always needs user approval. Use for anything the other tools don't cover.", CapabilityRisk.Change,
            P(("host", "string", "Saved host name or address", true), ("command", "string", "POSIX shell command", true), ("sudo", "boolean", "Run as root via sudo", false)),
            async (a, ctx, ct) =>
            {
                var h = HostArg(a);
                var cmd = S(a, "command"); var sudo = B(a, "sudo");
                if (cmd.Length == 0) return "ERROR: empty command";
                if (!await ctx.Approve("AI wants to run a command", $"Host: {h.Display} ({h.Endpoint})\nAs: {(sudo ? "root via sudo" : h.Username)}\n\n{cmd}", h))
                    return "User declined to run this command.";
                AuditLog.Write(ctx.Actor, h.Display, (sudo ? "sudo " : "") + cmd, "approved");
                var r = await SshPool.RunAsync(h, cmd, sudo, ct, TimeSpan.FromMinutes(5));
                return $"exit code {r.ExitCode}\n{r.Combined}";
            }, GatesItself: true);

        yield return new Capability("vultr_list_instances", "List Vultr cloud instances with id, label, IP, region, plan, status and power state.", CapabilityRisk.Read, None,
            async (_, _, ct) =>
            {
                var v = VultrClient.Require();
                var list = await v.ListAsync("/instances", "instances", ct);
                return JsonSerializer.Serialize(list.Select(i => new
                {
                    id = J.Str(i, "id"), label = J.Str(i, "label"), ip = J.Str(i, "main_ip"), region = J.Str(i, "region"), plan = J.Str(i, "plan"),
                    os = J.Str(i, "os"), status = J.Str(i, "status"), power = J.Str(i, "power_status"), server = J.Str(i, "server_status"), tags = J.Str(i, "tags"),
                }));
            }, GatesItself: true);

        yield return new Capability("vultr_instance_action", "Start, halt (power off) or reboot a Vultr instance. Needs user approval.", CapabilityRisk.Change,
            P(("instance_id", "string", "Vultr instance id", true), ("action", "string", "start|halt|reboot", true)),
            async (a, ctx, ct) =>
            {
                var v = VultrClient.Require();
                var id = S(a, "instance_id"); var act = S(a, "action");
                if (act is not ("start" or "halt" or "reboot")) return "ERROR: action must be start|halt|reboot";
                var inst = await v.GetAsync($"/instances/{Uri.EscapeDataString(id)}", ct);
                var label = J.Str(inst, "instance.label"); var ip = J.Str(inst, "instance.main_ip");
                if (!await ctx.Approve($"AI wants to {act} a Vultr instance", $"{label} ({ip})\nid {id}\n\nPOST /instances/{id}/{act}", LinkedHost(VultrProvider.Instance, id)))
                    return "User declined.";
                await v.PostAsync($"/instances/{Uri.EscapeDataString(id)}/{act}", null, ct, actor: ctx.Actor);
                return $"{act} requested for {label}.";
            }, GatesItself: true);

        // 0.6.17 (DarkCipher0x: "ask it to deploy a new server with the name and spec that i choose. If it doesnt match the
        // spec, choose the cheapest one available for the OS described. Then deploy it … and confirm"). The order is worked
        // out from Vultr's own lists and shown in full (plan, size, region, monthly price) before anything is created.
        yield return new Capability("vultr_deploy_server",
            "Deploy (order) a NEW Vultr server: give its name and operating system, optionally the region and size (vcpus, ram_gb, disk_gb) or an exact plan id. " +
            "The cheapest plan matching the size is used; if none matches, the cheapest plan that runs the OS (the approval says so). Waits until it is running, " +
            "saves it as a host (Vultr's root password goes into the vault, never to you) and checks SSH / Remote Desktop answers. Billed: needs the user's approval.",
            CapabilityRisk.Change,
            P(("name", "string", "Server name (label); the hostname is made from it", true), ("os", "string", "Operating system, e.g. Ubuntu 24.04, Debian 12, Rocky 9, Windows 2022", true),
              ("region", "string", "Region id (lhr, ams, fra, ewr…) or city; empty = where the account's servers are", false),
              ("vcpus", "integer", "vCPU count", false), ("ram_gb", "number", "RAM in GB (e.g. 1, 2, 4)", false), ("disk_gb", "integer", "Minimum disk in GB", false),
              ("plan", "string", "Exact Vultr plan id (e.g. vc2-1c-1gb) instead of a size", false), ("ssh_keys", "string", "Vultr SSH key names, comma-separated", false)),
            async (a, ctx, ct) =>
            {
                if (!AppSettings.Current.AiAllowServerDeploy) return "REFUSED: deploying servers from the AI is switched off (Settings → AI, or Group Policy AiAllowServerDeploy).";
                var v = DeployApi != null ? new VultrClient(new VultrAccount { Name = "demo account (self-test)" }) : VultrClient.Require();
                int? I(string k) => int.TryParse(S(a, k), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
                double? D(string k) => double.TryParse(S(a, k), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;
                var req = new DeployRequest(S(a, "name"), S(a, "os"), S(a, "region"), I("vcpus"), D("ram_gb"), I("disk_gb"), S(a, "plan"),
                    S(a, "ssh_keys").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                var api = DeployApi?.Invoke(v) ?? VultrDeploy.Api.For(v, ctx.Actor);
                DeployPlan plan;
                try { plan = await VultrDeploy.PlanAsync(api, req, ct); }
                catch (InvalidOperationException ex) { return "ERROR: " + ex.Message + ". Ask the user, then try again."; }
                if (!await ctx.Approve("AI wants to deploy a new Vultr server", plan.Describe(v.AccountName), null)) return "User declined: nothing was ordered.";
                var created = await api.Create(VultrDeploy.Body(plan).ToJsonString(), ct);
                var inst = created.ValueKind == JsonValueKind.Object && created.TryGetProperty("instance", out var ci) ? ci : created;
                var id = J.Str(inst, "id");
                if (id.Length == 0) return "ERROR: Vultr accepted the order but sent no instance id; check Vultr → Instances.";
                var password = J.Str(inst, "default_password");   // only in the create answer: straight into the vault, never returned
                AuditLog.Write(ctx.Actor, plan.Label, "Vultr server deployed by the AI", $"{plan.PlanId} · {plan.OsName} · {plan.RegionId} · ${plan.Cost}/month · id {id}");
                // 0.6.19: the root password is in the create answer only, so it goes into the vault before the wait (■ Stop
                // during the wait must not lose it).
                var cred = await Ui.Thread.InvokeAsync(() => SaveDeployedCredential(plan, password));
                JsonElement ready; bool isReady;
                try { (ready, isReady) = await VultrDeploy.WaitReadyAsync(api, id, DeployWait, DeployPoll, null, ct); }
                catch (OperationCanceledException)
                {
                    return $"Ordered “{plan.Label}” on Vultr (id {id}, {plan.PlanId}, ${plan.Cost}/month); the wait was stopped while it was being set up. " +
                           (cred != null ? $"The {cred.Username} password is in the vault as “{cred.Name}”. " : "") + "Save it as a host from Cloud → Vultr → Instances once it is running.";
                }
                // Saved as a host (once), signing in with that credential.
                var saved = await Ui.Thread.InvokeAsync(() => SaveDeployed(v, ready.ValueKind == JsonValueKind.Object ? ready : inst, plan, cred));
                string reach = "";
                if (isReady && J.Str(ready, "main_ip") is { Length: > 0 } ip)
                {
                    int port = plan.Windows ? 3389 : 22;
                    for (int i = 0; i < 6 && reach.Length == 0; i++)
                    {
                        try { if ((await (DeployProbe ?? ((h, p, c) => Net.NetTests.TcpAsync(h, p, TimeSpan.FromSeconds(5), c)))(ip, port, ct)).State == "open") reach = $"port {port} answers"; }
                        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { reach = "not checked (" + ex.Message.Split('\n')[0] + ")"; }
                        if (reach.Length == 0) await Task.Delay(DeployPoll, ct);
                    }
                    if (reach.Length == 0) reach = $"port {port} doesn't answer yet (the OS may still be starting)";
                }
                return $"Deployed “{plan.Label}” on Vultr: id {id}, {plan.OsName}, {plan.RegionText} ({plan.RegionId}), {plan.PlanId} ({plan.Size}, ${plan.Cost}/month). " +
                       (plan.SpecMatched ? "" : plan.SpecNote + " ") +
                       (isReady ? $"It is running at {J.Str(ready, "main_ip")}; {reach}. " : $"Still provisioning ({J.Str(ready, "status")} · {J.Str(ready, "server_status")}); check with cloud_list_instances shortly. ") +
                       saved;
            }, GatesItself: true);

        yield return new Capability("vultr_get","Read any Vultr API v2 GET endpoint, e.g. /account, /billing/pending-charges, /instances/{id}/bandwidth, /firewalls/{id}/rules.", CapabilityRisk.Read,
            P(("path", "string", "API path starting with /", true)),
            async (a, ctx, ct) =>
            {
                var v = VultrClient.Require();
                var path = S(a, "path");
                if (!path.StartsWith('/') || path.Contains("..")) return "ERROR: path must start with / (e.g. /account, /instances/{id}/bandwidth)";
                if (!await GateAsync(ctx, true, "AI: read Vultr API", "GET " + path)) return "User declined.";
                return J.Pretty(await v.GetAsync(path, ct));
            }, GatesItself: true);

        // ---- any cloud provider (ADR-020): neutral instance list and power actions, approval-gated -----------------
        yield return new Capability("cloud_list_instances", "List the instances of a connected cloud provider (" + string.Join(", ", CloudProviders.All.Select(x => x.Id)) + "): id, name, IPv4, region, plan, OS and state.", CapabilityRisk.Read,
            P(("provider", "string", string.Join("|", CloudProviders.All.Select(x => x.Id)), true)),
            async (a, _, ct) =>
            {
                var p = CloudProviders.Find(S(a, "provider").ToLowerInvariant());
                if (p == null) return "ERROR: provider must be one of " + string.Join(", ", CloudProviders.All.Select(x => x.Id));
                var list = await p.ListInstancesAsync(p.Require(), ct);
                return JsonSerializer.Serialize(list.Select(i => new { provider = p.Id, id = i.Id, name = i.Name, ipv4 = i.Ipv4, region = i.Region, plan = i.Plan, os = i.Os, state = i.State.ToString(), status = i.StatusText }));
            }, GatesItself: true);

        yield return new Capability("cloud_instance_action", "Start, stop (hard power-off) or reboot an instance of a cloud provider. Needs user approval.", CapabilityRisk.Change,
            P(("provider", "string", string.Join("|", CloudProviders.All.Select(x => x.Id)), true), ("instance_id", "string", "Instance id from cloud_list_instances", true), ("action", "string", "start|stop|reboot", true)),
            async (a, ctx, ct) =>
            {
                var p = CloudProviders.Find(S(a, "provider").ToLowerInvariant());
                if (p == null) return "ERROR: provider must be one of " + string.Join(", ", CloudProviders.All.Select(x => x.Id));
                var id = S(a, "instance_id"); var act = S(a, "action");
                CloudPower? power = act switch { "start" => CloudPower.Start, "stop" => CloudPower.Stop, "reboot" or "restart" => CloudPower.Reboot, _ => null };
                if (power is not { } pw) return "ERROR: action must be start|stop|reboot";
                if (pw == CloudPower.Start && !p.Has(CloudCaps.Start) || pw == CloudPower.Stop && !p.Has(CloudCaps.Stop) || pw == CloudPower.Reboot && !p.Has(CloudCaps.Reboot))
                    return $"ERROR: {p.Title} can't {act} instances through its API.";
                var api = p.Require();
                var inst = (await p.ListInstancesAsync(api, ct)).FirstOrDefault(i => i.Id == id);
                if (inst == null) return $"ERROR: no {p.Title} instance with id {id} on account {api.AccountName}.";
                if (!await ctx.Approve($"AI wants to {act} a {p.Title} instance", $"{inst.Name} ({inst.Ipv4}) · {inst.StatusText}\nid {id} · account {api.AccountName}", LinkedHost(p, id)))
                    return "User declined.";
                await p.PowerAsync(api, id, pw, ct, actor: ctx.Actor);
                return $"{act} requested for {inst.Name} on {p.Title}.";
            }, GatesItself: true);

        // ---- files (SFTP + this PC) — same layer as the File commander; every change needs approval -------------
        yield return new Capability("list_files", "List a folder over SFTP on a saved host, or on this PC with host 'local' (asks the user first).", CapabilityRisk.Read,
            P(("host", "string", "Saved host name/address, or 'local' for this PC", true), ("path", "string", "Folder path (empty = home folder)", false)),
            async (a, ctx, ct) =>
            {
                var (r, label) = FsArg(a, "host");
                var path = S(a, "path");
                if (!await GateAsync(ctx, r.Kind != "local", "AI: list files", $"{label}:{(path.Length > 0 ? path : "(home)")}" + (r.Kind == "local" ? "\n\nThis is a folder on THIS PC." : ""))) return "User declined.";
                using var fs = r.Open();
                if (path.Length == 0) path = await fs.HomeAsync(ct);
                var items = await fs.ListAsync(path, ct);
                return $"{label}:{path}\n" + string.Join("\n", items.OrderByDescending(i => i.IsDir).ThenBy(i => i.Name).Take(400)
                    .Select(i => $"{(i.IsDir ? "d" : "-")} {i.Perms,-10} {(i.IsDir ? "" : Fmt.Bytes(i.Length)),10}  {i.ModifiedText,-16}  {i.Name}"))
                    + (items.Count > 400 ? $"\n…{items.Count - 400} more" : "");
            }, GatesItself: true);

        yield return new Capability("read_file", "Read the start of a text file (logs, configs). Key/credential files are refused.", CapabilityRisk.Read,
            P(("host", "string", "Saved host name/address, or 'local'", true), ("path", "string", "File path", true), ("max_kb", "integer", "1-256, default 64", false)),
            async (a, ctx, ct) =>
            {
                var (r, label) = FsArg(a, "host");
                var path = S(a, "path");
                if (LooksSecret(path)) return "REFUSED: that path looks like a key, credential or password file. Ask the user to open it themselves if needed.";
                int maxKb = Math.Clamp(int.TryParse(S(a, "max_kb", "64"), out var k) ? k : 64, 1, 256);
                if (!await GateAsync(ctx, r.Kind != "local", "AI: read a file", $"{label}:{path}  (first {maxKb} KB)" + (r.Kind == "local" ? "\n\nThis file is on THIS PC." : ""))) return "User declined.";
                using var fs = r.Open();
                await using var s = await fs.OpenReadAsync(path, ct);
                var buf = new byte[maxKb * 1024];
                int n = 0, got;
                while (n < buf.Length && (got = await s.ReadAsync(buf.AsMemory(n), ct)) > 0) n += got;
                if (buf.Take(Math.Min(n, 8000)).Any(b => b == 0)) return "That is a binary file.";
                return Encoding.UTF8.GetString(buf, 0, n) + (n == buf.Length ? $"\n…(stopped at {maxKb} KB)" : "");
            }, GatesItself: true);

        yield return new Capability("transfer_files", "Copy (or move) a file or folder between this PC and hosts, or between two hosts. Always needs user approval; runs in the transfer queue.", CapabilityRisk.Change,
            P(("from_host", "string", "Source: saved host or 'local'", true), ("from_path", "string", "Source file or folder", true),
              ("to_host", "string", "Destination: saved host or 'local'", true), ("to_dir", "string", "Existing destination folder", true),
              ("move", "boolean", "Delete the source after copying", false)),
            async (a, ctx, ct) =>
            {
                var (from, fromLabel) = FsArg(a, "from_host");
                var (to, toLabel) = FsArg(a, "to_host");
                var src = S(a, "from_path"); var dest = S(a, "to_dir"); var move = B(a, "move");
                if (src.Length == 0 || dest.Length == 0) return "ERROR: from_path and to_dir are required";
                Files.FsItem item;
                using (var sfs = from.Open()) item = await sfs.StatAsync(src, ct) ?? throw new InvalidOperationException($"{fromLabel}:{src} doesn't exist");
                using (var dfs = to.Open()) if (await dfs.StatAsync(dest, ct) is not { IsDir: true }) return $"ERROR: {toLabel}:{dest} is not an existing folder";
                if (!await ctx.Approve($"AI wants to {(move ? "MOVE" : "copy")} files",
                        $"{(item.IsDir ? "Folder (with everything inside)" : "File")}: {fromLabel}:{src}{(item.IsDir ? "" : $"  ({Fmt.Bytes(item.Length)})")}\nTo folder: {toLabel}:{dest}\n\n" +
                        (move ? "The original is deleted after the copy completes.\n" : "") + "Existing files with the same name are overwritten." + (LooksSecret(src) ? "\n\n⚠ This looks like a key or credential file." : ""), to.Host ?? from.Host))
                    return "User declined.";
                var job = Files.TransferQueue.Enqueue(new Files.TransferJob { From = from, To = to, Items = new[] { item }, DestDir = dest, Move = move, Actor = ctx.Actor });
                using (ct.Register(() => job.Cts.Cancel())) await job.Done.Task;
                return job.Summary;
            }, GatesItself: true);

        yield return new Capability("make_directory", "Create a folder. Needs approval.", CapabilityRisk.Change,
            P(("host", "string", "Saved host or 'local'", true), ("path", "string", "Folder to create", true)),
            async (a, ctx, ct) =>
            {
                var (r, label) = FsArg(a, "host");
                var path = S(a, "path");
                if (!await ctx.Approve("AI wants to create a folder", $"{label}:{path}", r.Host)) return "User declined.";
                using var fs = r.Open();
                await fs.MkdirAsync(path, ct);
                AuditLog.Write(ctx.Actor, label, "created folder " + path, "approved");
                return "created " + path;
            }, GatesItself: true);

        yield return new Capability("rename_path", "Rename or move a path within one host/this PC. Needs approval.", CapabilityRisk.Change,
            P(("host", "string", "Saved host or 'local'", true), ("from", "string", "Existing path", true), ("to", "string", "New path", true)),
            async (a, ctx, ct) =>
            {
                var (r, label) = FsArg(a, "host");
                var from = S(a, "from"); var to = S(a, "to");
                if (!await ctx.Approve("AI wants to rename / move a path", $"{label}\n{from}\n→ {to}", r.Host)) return "User declined.";
                using var fs = r.Open();
                await fs.RenameAsync(from, to, ct);
                AuditLog.Write(ctx.Actor, label, $"renamed {from} → {to}", "approved");
                return $"renamed {from} → {to}";
            }, GatesItself: true);

        yield return new Capability("delete_path", "Delete a file or folder (local: Recycle Bin; remote: permanent). Needs approval.", CapabilityRisk.Change,
            P(("host", "string", "Saved host or 'local'", true), ("path", "string", "Path to delete", true)),
            async (a, ctx, ct) =>
            {
                var (r, label) = FsArg(a, "host");
                var path = S(a, "path");
                using var fs = r.Open();
                var it = await fs.StatAsync(path, ct) ?? throw new InvalidOperationException($"{label}:{path} doesn't exist");
                if (path.Trim() is "/" or "" || path.Length <= 3 && r.Kind == "local") return "REFUSED: won't delete a root folder.";
                if (!await ctx.Approve($"AI wants to DELETE {(it.IsDir ? "a folder and everything inside" : "a file")}",
                        $"{label}:{path}\n\n{(r.Kind == "local" ? "Goes to the Recycle Bin." : "Permanent — SFTP has no recycle bin.")}", r.Host)) return "User declined.";
                await fs.DeleteAsync(path, it.IsDir && !it.IsLink, ct);
                AuditLog.Write(ctx.Actor, label, "deleted " + path, "approved");
                return "deleted " + path;
            }, GatesItself: true);
    }
}
