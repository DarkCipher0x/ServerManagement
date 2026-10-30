using DcxManager.Services;
using DcxManager.Ssh;

namespace DcxManager.Rdp;

/// <summary>
/// Remote Desktop through SSH (0.6.29): the RDP connection goes through a local forward on a random 127.0.0.1 port to the
/// Windows server as a saved SSH host sees it (a bastion, or the server's own OpenSSH), as VNC already does (ADR-069). The
/// forward lives as long as the tab (or mstsc) and is never reachable from the network. No RD Gateway needed.
/// </summary>
public static class RdpTunnel
{
    public const int DefaultPort = 3389;

    public static bool Wanted(HostEntry h) => h.Protocol == "rdp" && h.Rdp.Through == "ssh";

    /// <summary>The SSH host chosen, else a saved SSH host with the same address; a shared library host only one of its own
    /// library (<see cref="HostEntry.TunnelHost"/>).</summary>
    public static HostEntry? SshHostFor(HostEntry h) => h.TunnelHost(h.Rdp.SshHostId, AppData.Current.FindHost, AppData.Current.Hosts);

    /// <summary>The Remote Desktop server as the SSH host reaches it.</summary>
    public static string TargetFor(HostEntry h) => h.Rdp.TunnelTarget.Trim() is { Length: > 0 } t ? t : h.Address;

    public static int PortFor(HostEntry h) => h.Port is > 0 and <= 65535 ? h.Port : DefaultPort;

    /// <summary>Opens (or reuses, for the same key) the forward: its local port and a description of the route.</summary>
    public static async Task<(int Port, string Route)> OpenAsync(HostEntry h, string key)
    {
        var ssh = SshHostFor(h) ?? throw new InvalidOperationException($"No saved SSH host to tunnel through for {h.Display}. Pick one in the host editor (Remote Desktop → Connect through), or choose direct.");
        Locks.Demand("host:" + ssh.Id, ssh.Display);
        var port = await TunnelManager.EnsureEphemeralAsync(key, ssh, TargetFor(h), PortFor(h));
        return (port, $"through SSH ({ssh.Display}) to {TargetFor(h)}:{PortFor(h)}");
    }

    public static void Close(string? key)
    {
        if (key == null) return;
        try { TunnelManager.StopEphemeral(key); } catch (Exception ex) { Log.Write("RDP tunnel close", ex); }
    }

    public static string NewKey(HostEntry h) => $"rdp:{h.Id}:{Guid.NewGuid():N}";
}
