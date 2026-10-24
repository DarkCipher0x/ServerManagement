using System.Text.Json.Serialization;
using DcxManager.Services;

namespace DcxManager.Infra;

/// <summary>What a connected provider can do, discovered from the target (never assumed).</summary>
public sealed record ProviderCaps(bool Reachable, bool CanRead, bool CanControl, string Version, string Detail, IReadOnlyList<string> Notes)
{
    public static ProviderCaps Unreachable(string why) => new(false, false, false, "", why, Array.Empty<string>());
}

/// <summary>One thing a provider reports (container, image, network, volume, node, VM, LXC, storage…).</summary>
public sealed record InfraObject(string Id, string Kind, string Name, string Status, string Parent, string Detail, IReadOnlyDictionary<string, string> Props)
{
    public string StatusGlyph => Status switch { "running" or "online" or "available" or "active" => "●", "stopped" or "exited" or "offline" or "created" => "○", "paused" => "‖", _ => "◇" };
}

/// <summary>Consequence categories shown before anything disruptive.</summary>
public enum Consequence { None, ServiceInterruption, DataLoss }

/// <summary>A value an action needs before it runs (a clone's name, a checkpoint name, an export folder…). Values reach
/// the provider as data (JSON / request bodies), never spliced into a command line.</summary>
public sealed record InfraInput(string Name, string Label, string? Default = null, string? Hint = null, bool Required = true, string[]? Options = null, bool Number = false)
{
    /// <summary>Why <paramref name="value"/> isn't acceptable, or null. Control characters and very long values are refused.</summary>
    public string? Validate(string value)
    {
        if (value.Length == 0) return Required ? $"{Label} is required." : null;
        if (value.Length > 400 || value.Any(char.IsControl)) return $"{Label}: at most 400 characters, no control characters.";
        if (Number && !long.TryParse(value, out _)) return $"{Label} must be a whole number.";
        if (Options is { Length: > 0 } && !Options.Contains(value)) return $"{Label} must be one of: {string.Join(", ", Options)}.";
        return null;
    }
}

/// <summary>An operation on an object. Mutating ones need AllowChanges + the provider's own permission.</summary>
public sealed record InfraAction(string Id, string Label, bool Mutating, Consequence Consequence, string? ExpectedStatus, string Explain)
{
    /// <summary>Values asked for before the confirmation (empty = none).</summary>
    public IReadOnlyList<InfraInput> Inputs { get; init; } = Array.Empty<InfraInput>();
}

/// <summary>A result is only "Verified" when the provider re-read the object and saw the expected state.</summary>
public sealed record ActionResult(bool Accepted, bool Verified, string Message, string? Output = null);

/// <summary>An action as the UI may offer it: available, or disabled with the reason (no fake controls).</summary>
public sealed record OfferedAction(InfraAction Action, bool Enabled, string? Why);

/// <summary>
/// An infrastructure provider (ADR-007). Each implementation stays provider-specific; this is only the shared shape the
/// Infrastructure screen, AI and audit use. Every call may fail; failures are results, never crashes of the app.
/// </summary>
public interface IInfraProvider
{
    ProviderConfig Config { get; }
    /// <summary>Where it connects (for the network class badge): a URL or "ssh:&lt;host&gt;" / "local pipe".</summary>
    string Where { get; }
    NetClass? NetworkClass { get; }
    Task<ProviderCaps> DetectAsync(CancellationToken ct);
    Task<IReadOnlyList<InfraObject>> InventoryAsync(CancellationToken ct);
    IReadOnlyList<InfraAction> ActionsFor(InfraObject o);
    /// <summary>Provider-level permission for this action on this object (independent of AllowChanges).</summary>
    string? Forbidden(InfraAction a, InfraObject o, ProviderCaps caps);
    Task<ActionResult> RunAsync(InfraAction a, InfraObject o, CancellationToken ct);
    /// <summary>An action with the values its <see cref="InfraAction.Inputs"/> asked for (already validated).</summary>
    Task<ActionResult> RunAsync(InfraAction a, InfraObject o, IReadOnlyDictionary<string, string> input, CancellationToken ct) => RunAsync(a, o, ct);
}

public static class Infra
{
    /// <summary>The app-wide environment vocabulary (Services/Environments.cs, ADR-017).</summary>
    public static string[] Environments => Envs.All;

    public static IInfraProvider Create(ProviderConfig c) => c.Kind switch
    {
        "proxmox" => new ProxmoxProvider(c),
        "unraid" => new UnraidProvider(c),
        "vsphere" => new VSphereProvider(c),
        "hyperv" => new HyperVProvider(c),
        "dcx-portal" => new DcxCtl.DcxPortalProvider(c),
        "synology" => new SynologyProvider(c),
        "satellite" => new SatelliteProvider(c),
        "unifi" => new UniFiProvider(c),
        "unifi-cloud" => new UniFiCloudProvider(c),
        "docker-local" => new DockerProvider(c, DockerBackend.LocalPipe(c.Endpoint)),
        _ => new DockerProvider(c, DockerBackend.Ssh(c)),
    };

    public static string KindTitle(string kind) => kind switch
    {
        "proxmox" => "Proxmox VE (API token)",
        "unraid" => "Unraid (API key)",
        "vsphere" => "VMware vCenter (vSphere Automation API)",
        "hyperv" => "Microsoft Hyper-V (PowerShell / WinRM)",
        "dcx-portal" => "DCX-CTL (dcx-ctl/1 over SSH)",
        "synology" => "Synology DSM (Web API over https)",
        "satellite" => "Red Hat Satellite / Foreman + Katello (REST API)",
        "unifi" => "UniFi Network (Integration API, API key)",
        "unifi-cloud" => "UniFi Site Manager (api.ui.com, API key)",
        "docker-local" => "Docker on this PC (Docker Desktop)",
        _ => "Docker on a saved host (SSH)",
    };

    /// <summary>What the UI may offer for an object: read-only actions always (if readable); changes only with
    /// AllowChanges AND the provider's own permission — otherwise disabled with the reason.</summary>
    public static List<OfferedAction> Offer(IInfraProvider p, InfraObject o, ProviderCaps caps) =>
        p.ActionsFor(o).Select(a =>
        {
            if (!caps.Reachable) return new OfferedAction(a, false, "not connected");
            if (!a.Mutating) return new OfferedAction(a, caps.CanRead, caps.CanRead ? null : "no read access");
            if (!p.Config.AllowChanges) return new OfferedAction(a, false, "read-only: turn on “Allow changes” for this provider first");
            if (!caps.CanControl) return new OfferedAction(a, false, "the credential has no permission to change things");
            return p.Forbidden(a, o, caps) is { } why ? new OfferedAction(a, false, why) : new OfferedAction(a, true, null);
        }).ToList();

    /// <summary>An input's default for this object: {name}, {now} and {prop} (any of the object's Props, e.g. {cpu}) are
    /// filled; an unknown {prop} becomes empty rather than staying as a placeholder.</summary>
    public static string FillDefault(InfraInput i, InfraObject o)
    {
        var def = (i.Default ?? "").Replace("{name}", o.Name).Replace("{now}", DateTime.Now.ToString("yyyy-MM-dd HH-mm"));
        return System.Text.RegularExpressions.Regex.Replace(def, @"\{([A-Za-z]+)\}", m => o.Props.GetValueOrDefault(m.Groups[1].Value) ?? "");
    }

    /// <summary>The confirmation text for a disruptive action: target, action, environment, consequence.</summary>
    public static string ConfirmText(IInfraProvider p, InfraAction a, InfraObject o, IReadOnlyDictionary<string, string>? input = null) =>
        $"Target:       {o.Kind} “{o.Name}”{(o.Parent.Length > 0 ? " on " + o.Parent : "")}\n" +
        $"Provider:     {p.Config.Name} ({KindTitle(p.Config.Kind)}) · {p.Where}\n" +
        $"Action:       {a.Label}\n" +
        (input is { Count: > 0 } ? string.Concat(a.Inputs.Where(i => input.ContainsKey(i.Name)).Select(i => $"  {i.Label}: {input[i.Name]}\n")) : "") +
        $"Environment:  {p.Config.Environment}\n" +
        $"Consequence:  {a.Consequence switch { Consequence.DataLoss => "DATA LOSS — cannot be undone", Consequence.ServiceInterruption => "SERVICE INTERRUPTION", _ => "none expected" }}\n\n" +
        a.Explain + "\n\nThe result is reported only after the app re-reads the object and sees the expected state.";
}
