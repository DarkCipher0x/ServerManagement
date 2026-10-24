using DcxManager.Services;
using Renci.SshNet;

namespace DcxManager.Ssh;

/// <summary>Runs SSH port forwards: local (-L), remote (-R) and dynamic SOCKS5 (-D). Each tunnel has its own connection.</summary>
public static class TunnelManager
{
    private sealed class Running(SshSession<SshClient> session, ForwardedPort port) : IDisposable
    {
        public SshSession<SshClient> Session { get; } = session;
        public ForwardedPort Port { get; } = port;
        public void Dispose() { try { Port.Stop(); } catch { } Session.Dispose(); }
    }

    private static readonly Dictionary<string, Running> Active = new();

    public static bool IsRunning(string id) { lock (Active) return Active.ContainsKey(id); }

    public static async Task StartAsync(TunnelDef t)
    {
        if (IsRunning(t.Id)) return;
        var host = AppData.Current.FindHost(t.HostId) ?? throw new InvalidOperationException("The tunnel's host no longer exists.");
        t.Status = "connecting…";
        try
        {
            var s = await SshConnector.ConnectSshAsync(host);
            ForwardedPort port = t.Kind switch
            {
                TunnelKind.Local => new ForwardedPortLocal(t.BindAddress, (uint)t.BindPort, t.DestHost, (uint)t.DestPort),
                TunnelKind.Remote => new ForwardedPortRemote(t.BindAddress, (uint)t.BindPort, t.DestHost, (uint)t.DestPort),
                _ => new ForwardedPortDynamic(t.BindAddress, (uint)t.BindPort),
            };
            port.Exception += (_, e) => Log.Write("Tunnel " + t.Name, e.Exception);
            s.Client.AddForwardedPort(port);
            await Task.Run(port.Start);
            s.Client.ErrorOccurred += (_, _) => Stop(t, "dropped");
            lock (Active) Active[t.Id] = new Running(s, port);
            t.Status = "running";
            AuditLog.Write("you", host.Display, $"tunnel started: {t.Summary}");
        }
        catch (Exception ex)
        {
            t.Status = "error: " + ex.Message;
            throw;
        }
    }

    /// <summary>Opens a short-lived local forward (used for Ollama over SSH). Returns the local port.</summary>
    public static async Task<int> EnsureEphemeralAsync(string key, HostEntry host, string destHost, int destPort)
    {
        lock (Active)
            if (Active.TryGetValue(key, out var r) && r.Session.IsConnected && r.Port is ForwardedPortLocal l && l.IsStarted) return (int)l.BoundPort;
        Stop(key);
        var s = await SshConnector.ConnectSshAsync(host);
        var port = new ForwardedPortLocal("127.0.0.1", 0, destHost, (uint)destPort);
        s.Client.AddForwardedPort(port);
        port.Start();
        lock (Active) Active[key] = new Running(s, port);
        return (int)port.BoundPort;
    }

    public static void Stop(TunnelDef t, string status = "stopped")
    {
        Stop(t.Id);
        t.Status = status;
    }

    /// <summary>Stops an internal forward started by <see cref="EnsureEphemeralAsync"/> (a VNC session ending).</summary>
    public static void StopEphemeral(string key) => Stop(key);

    private static void Stop(string id)
    {
        Running? r;
        lock (Active) { if (!Active.Remove(id, out r)) return; }
        r.Dispose();
    }

    /// <summary>A port this app is listening on right now (or, for -R, asked the server to listen on).</summary>
    public sealed record Listener(string Key, string Kind, string BindHost, int Port, bool OnServer);

    /// <summary>Snapshot of every running forward: user tunnels and internal ones (jump hosts, Ollama over SSH).</summary>
    public static List<Listener> Listeners()
    {
        lock (Active)
            return Active.Select(kv => kv.Value.Port switch
            {
                ForwardedPortLocal l => new Listener(kv.Key, "local forward", l.BoundHost, (int)l.BoundPort, false),
                ForwardedPortDynamic d => new Listener(kv.Key, "SOCKS5 proxy", d.BoundHost, (int)d.BoundPort, false),
                ForwardedPortRemote r => new Listener(kv.Key, "remote forward", r.BoundHost, (int)r.BoundPort, true),
                var p => new Listener(kv.Key, p.GetType().Name, "?", 0, false),
            }).ToList();
    }

    public static void StopAll()
    {
        List<Running> all;
        lock (Active) { all = Active.Values.ToList(); Active.Clear(); }
        foreach (var r in all) r.Dispose();
    }
}
