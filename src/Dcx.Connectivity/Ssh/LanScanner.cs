using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace DcxManager.Ssh;

public sealed record LanHost(string Address, int Port, string Banner, string Name);

/// <summary>Finds SSH servers on the local network by trying the SSH port on every address of a subnet and reading the banner.</summary>
public static class LanScanner
{
    /// <summary>Guesses the user's LAN as CIDR from the first up, non-virtual IPv4 adapter with a gateway.</summary>
    public static string GuessSubnet()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            var props = ni.GetIPProperties();
            if (!props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork)) continue;
            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                int prefix = Math.Max(ua.PrefixLength, 24);   // never sweep more than a /24 by default
                var b = ua.Address.GetAddressBytes();
                uint ip = (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
                uint mask = prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);
                uint net = ip & mask;
                return $"{net >> 24}.{(net >> 16) & 255}.{(net >> 8) & 255}.{net & 255}/{prefix}";
            }
        }
        return "192.168.1.0/24";
    }

    public static IEnumerable<IPAddress> Expand(string cidr)
    {
        var parts = cidr.Trim().Split('/');
        var baseIp = IPAddress.Parse(parts[0]).GetAddressBytes();
        int prefix = parts.Length > 1 ? int.Parse(parts[1]) : 32;
        if (prefix < 16) throw new ArgumentException("Use a /16 or smaller range.");
        uint ip = (uint)(baseIp[0] << 24 | baseIp[1] << 16 | baseIp[2] << 8 | baseIp[3]);
        uint mask = prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);
        uint net = ip & mask, count = prefix >= 31 ? (1u << (32 - prefix)) : (1u << (32 - prefix)) - 2;
        uint start = prefix >= 31 ? net : net + 1;
        for (uint i = 0; i < count; i++)
        {
            uint a = start + i;
            yield return new IPAddress(new[] { (byte)(a >> 24), (byte)(a >> 16), (byte)(a >> 8), (byte)a });
        }
    }

    public static async Task ScanAsync(string cidr, int port, IProgress<LanHost> found, IProgress<int> progress, CancellationToken ct)
    {
        // Offline mode (ADR-016): a sweep of a public range would leave the network; refused before the first probe.
        Services.Restrictions.Demand("network.scan", "you");   // 0.5.1: Group Policy can turn scanning off
        var addresses = Expand(cidr).ToList();
        if (addresses.Count > 0) Services.Offline.EnforceStatic(addresses[0].ToString(), "a network scan");
        if (Services.Offline.On) addresses = addresses.Where(a => Services.Offline.Quick(a.ToString()).Allowed == true).ToList();
        int done = 0;
        using var gate = new SemaphoreSlim(64);
        var tasks = addresses.Select(async a =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var h = await ProbeAsync(a, port, ct);
                if (h != null) found.Report(h);
            }
            catch (OperationCanceledException) { }
            catch { }
            finally
            {
                gate.Release();
                progress.Report(Interlocked.Increment(ref done) * 100 / Math.Max(1, addresses.Count));
            }
        });
        await Task.WhenAll(tasks);
    }

    private static async Task<LanHost?> ProbeAsync(IPAddress a, int port, CancellationToken ct)
    {
        using var tcp = new TcpClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(600);
        try { await tcp.ConnectAsync(a, port, cts.Token); } catch { return null; }
        string banner = "";
        try
        {
            using var rcts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            rcts.CancelAfter(1500);
            var buf = new byte[256];
            int n = await tcp.GetStream().ReadAsync(buf, rcts.Token);
            banner = Encoding.ASCII.GetString(buf, 0, n).Split('\n')[0].Trim();
        }
        catch { }
        string name = "";
        try { name = (await Dns.GetHostEntryAsync(a)).HostName; } catch { }
        return new LanHost(a.ToString(), port, banner, name);
    }
}
