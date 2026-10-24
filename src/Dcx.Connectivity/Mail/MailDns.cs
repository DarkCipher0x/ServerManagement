using System.Net;
using System.Net.Sockets;
using DcxManager.Net;

namespace DcxManager.Mail;

/// <summary>The DNS questions the mail toolbox asks (SPF, DMARC, DKIM, blacklists, MTA-STS…). The real one uses Windows'
/// resolver; the self-test passes a fake so every check is exercised without the network.</summary>
public interface IMailDns
{
    Task<List<string>> TxtAsync(string name);
    /// <summary>A and AAAA; empty when the name has none.</summary>
    Task<List<IPAddress>> AddressesAsync(string name);
    Task<List<DnsRecords.Mx>> MxAsync(string name);
    Task<List<string>> PtrAsync(IPAddress ip);
}

public sealed class SystemMailDns : IMailDns
{
    public static readonly SystemMailDns Instance = new();

    public Task<List<string>> TxtAsync(string name) => DnsRecords.TxtAsync(name);
    public Task<List<DnsRecords.Mx>> MxAsync(string name) => DnsRecords.MxAsync(name);
    public Task<List<string>> PtrAsync(IPAddress ip) => DnsRecords.NamesAsync(ip.ToString(), "PTR");

    public async Task<List<IPAddress>> AddressesAsync(string name)
    {
        Services.Offline.EnforceStatic(name, "a DNS lookup");
        try { return (await Dns.GetHostAddressesAsync(name)).ToList(); }
        catch (SocketException) { return []; }
    }
}

/// <summary>Small helpers shared by the mail checks.</summary>
public static class MailNet
{
    /// <summary>Is <paramref name="ip"/> inside <paramref name="network"/>/<paramref name="bits"/>? (IPv4 and IPv6; an IPv4
    /// address mapped into IPv6 counts as IPv4.)</summary>
    public static bool InCidr(IPAddress ip, IPAddress network, int bits)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (network.IsIPv4MappedToIPv6) network = network.MapToIPv4();
        if (ip.AddressFamily != network.AddressFamily) return false;
        var a = ip.GetAddressBytes(); var b = network.GetAddressBytes();
        bits = Math.Clamp(bits, 0, a.Length * 8);
        for (int i = 0; i < a.Length && bits > 0; i++, bits -= 8)
        {
            int mask = bits >= 8 ? 0xFF : 0xFF << (8 - bits) & 0xFF;
            if ((a[i] & mask) != (b[i] & mask)) return false;
        }
        return true;
    }

    /// <summary>A domain name as the checks accept it (no IPs, no spaces, no shell or URL characters).</summary>
    public static string CheckDomain(string d)
    {
        d = d.Trim().TrimEnd('.').ToLowerInvariant();
        if (d.StartsWith("http://") || d.StartsWith("https://"))
            d = Uri.TryCreate(d, UriKind.Absolute, out var u) ? u.IdnHost : throw new ArgumentException("Enter a domain, e.g. example.com");
        if (d.Contains('@')) d = d[(d.LastIndexOf('@') + 1)..];
        if (!NetTests.ValidHost(d) || IPAddress.TryParse(d, out _) || !d.Contains('.')) throw new ArgumentException("Enter a domain, e.g. example.com");
        return d;
    }
}
