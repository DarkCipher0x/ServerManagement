using System.Net.Sockets;
using DcxManager.Services;
using DcxManager.Ssh;

namespace DcxManager.Vnc;

/// <summary>An open VNC session: the protocol client, the TCP connection and (when tunnelled) the SSH forward under it.</summary>
public sealed class VncSession(RfbClient client, TcpClient tcp, string? tunnelKey, string route) : IDisposable
{
    public RfbClient Client { get; } = client;
    /// <summary>"through SSH (web-01) to 127.0.0.1:5900" or "direct to 192.0.2.10:5900".</summary>
    public string Route { get; } = route;
    private int _disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Client.Dispose();
        try { tcp.Dispose(); } catch { }
        if (tunnelKey != null) TunnelManager.StopEphemeral(tunnelKey);
    }
}

/// <summary>
/// Connects a saved VNC host (0.5.1 #19, ADR-069). By default the session goes through an SSH tunnel to the machine (its
/// saved SSH host), so the whole stream is encrypted whatever the VNC server offers; "direct" connects to the VNC port
/// itself and then prefers VeNCrypt TLS (pinned), Apple Remote Desktop and the VNC password in that order. The password
/// or macOS account is the host's vault credential (a padlock on the host, its folder or the credential is honoured).
/// </summary>
public static class VncConnector
{
    public const int DefaultPort = 5900;

    /// <summary>The SSH host a tunnelled session goes through: the one chosen, else a saved SSH host with the same address.</summary>
    public static HostEntry? SshHostFor(HostEntry h) =>
        AppData.Current.FindHost(h.Vnc.SshHostId) is { IsSsh: true } chosen ? chosen
            : AppData.Current.Hosts.FirstOrDefault(x => x.IsSsh && !x.IsAdHoc && x.Address.Equals(h.Address, StringComparison.OrdinalIgnoreCase));

    /// <summary>Opens the session. <paramref name="trustCertificate"/> is asked (fingerprint, subject) when a VeNCrypt server
    /// shows a certificate Windows doesn't trust and none is pinned; a yes is pinned on the host.</summary>
    public static async Task<VncSession> ConnectAsync(HostEntry h, Func<string, string, bool>? trustCertificate, CancellationToken ct)
    {
        Locks.Demand("host:" + h.Id, h.Display);
        var cred = SshConnector.CredentialFor(h);
        var user = h.Username.Length > 0 ? h.Username : cred?.Username ?? "";
        var password = cred is { Kind: CredentialKind.Password } ? cred.Secret : "";
        if (h.Vnc.Kind == "mac" && user.Length == 0) throw new RfbException("A Mac signs in with a macOS user name and password: give the host a credential with both.");

        string address = h.Address, route;
        int port = h.Port is > 0 and <= 65535 ? h.Port : DefaultPort;
        string? tunnelKey = null;
        bool secure = false;
        if (h.Vnc.Through == "ssh")
        {
            var ssh = SshHostFor(h) ?? throw new RfbException($"No saved SSH host to tunnel through for {h.Display}. Pick one in the host editor (VNC → Through SSH), or choose Direct.");
            Locks.Demand("host:" + ssh.Id, ssh.Display);
            var target = h.Vnc.TunnelTarget.Trim().Length > 0 ? h.Vnc.TunnelTarget.Trim() : "127.0.0.1";
            tunnelKey = $"vnc:{h.Id}:{Guid.NewGuid():N}";
            port = await TunnelManager.EnsureEphemeralAsync(tunnelKey, ssh, target, port);
            route = $"through SSH ({ssh.Display}) to {target}:{(h.Port > 0 ? h.Port : DefaultPort)}";
            address = "127.0.0.1";
            secure = true;
        }
        else
        {
            if (Offline.On)
            {
                var d = await Offline.DecideAsync(h.Address, ct);
                if (!d.Allowed) throw Offline.Block(h.Address, "VNC", d.Reason);
            }
            route = $"direct to {h.Address}:{port}";
        }

        var tcp = new TcpClient { NoDelay = true, ReceiveTimeout = 20_000, SendTimeout = 20_000 };
        try
        {
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(TimeSpan.FromSeconds(15));
                await tcp.ConnectAsync(address, port, cts.Token);
            }
            var options = new RfbOptions
            {
                Username = user, Password = password, TransportSecure = secure, Shared = h.Vnc.Shared,
                TlsHost = h.Address, TlsPin = h.Vnc.TlsSha256, TrustCertificate = trustCertificate,
            };
            var client = await Task.Run(() => RfbClient.Handshake(tcp.GetStream(), options), ct);
            tcp.ReceiveTimeout = 0;   // the reader waits as long as the desktop is quiet
            if (client.PinnedSha256.Length > 0 && h.Vnc.TlsSha256.Length == 0)
            {
                h.Vnc.TlsSha256 = client.PinnedSha256;
                AppData.Save();
                AuditLog.Write("you", h.Display, "VNC TLS certificate pinned", RfbClient.TlsPinPretty(client.PinnedSha256));
            }
            h.LastConnected = DateTime.Now;
            AuditLog.Write("you", h.Display, "VNC session opened", $"{route} · RFB {client.Version} · {client.Security}{(client.Encrypted ? " · encrypted" : " · NOT encrypted on the network")}");
            return new VncSession(client, tcp, tunnelKey, route);
        }
        catch
        {
            tcp.Dispose();
            if (tunnelKey != null) TunnelManager.StopEphemeral(tunnelKey);
            throw;
        }
    }
}
