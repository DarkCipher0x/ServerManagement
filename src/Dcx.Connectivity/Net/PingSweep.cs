using System.Net;
using System.Net.NetworkInformation;

namespace DcxManager.Net;

/// <summary>One answer to a ping: round trip, the TTL it came back with (a hint at the OS: ~64 Linux/macOS, ~128 Windows,
/// ~255 network gear) and the reverse-DNS name.</summary>
public sealed record PingResult(string Address, long RoundTripMs, int Ttl, string Name)
{
    public string OsHint => Ttl switch { > 0 and <= 64 => "Linux / Unix / macOS", > 64 and <= 128 => "Windows", > 128 => "network device", _ => "" };
}

/// <summary>
/// ICMP echo discovery (0.3.0 §7): which addresses answer a ping, at most 64 at a time, 800 ms each, no admin rights
/// needed (Windows' ICMP API). Offline mode is respected like every other scan. Hosts that drop ICMP (Windows' firewall
/// by default) don't show here — the port tests find those.
/// </summary>
public static class PingSweep
{
    public static async Task RunAsync(IReadOnlyList<IPAddress> addresses, IProgress<PingResult> found, IProgress<int> progress, CancellationToken ct, int timeoutMs = 800)
    {
        Services.Restrictions.Demand("network.scan", "you");   // 0.5.1: Group Policy can turn scanning off
        if (addresses.Count > 0) Services.Offline.EnforceStatic(addresses[0].ToString(), "a ping sweep");
        var list = Services.Offline.On ? addresses.Where(a => Services.Offline.Quick(a.ToString()).Allowed == true).ToList() : addresses.ToList();
        int done = 0;
        using var gate = new SemaphoreSlim(64);
        await Task.WhenAll(list.Select(async a =>
        {
            await gate.WaitAsync(ct);
            try
            {
                using var ping = new Ping();
                var r = await ping.SendPingAsync(a, TimeSpan.FromMilliseconds(timeoutMs), new byte[32], new PingOptions(128, true), ct);
                if (r.Status == IPStatus.Success)
                {
                    string name = "";
                    try { using var dns = CancellationTokenSource.CreateLinkedTokenSource(ct); dns.CancelAfter(1500); name = (await Dns.GetHostEntryAsync(a.ToString(), dns.Token)).HostName; } catch { }
                    found.Report(new PingResult(a.ToString(), r.RoundtripTime, r.Options?.Ttl ?? 0, name));
                }
            }
            catch (OperationCanceledException) { }
            catch { }
            finally
            {
                gate.Release();
                progress.Report(Interlocked.Increment(ref done) * 100 / Math.Max(1, list.Count));
            }
        }));
    }
}
