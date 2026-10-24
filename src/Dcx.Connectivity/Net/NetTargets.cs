using System.Net;
using System.Net.Sockets;

namespace DcxManager.Net;

/// <summary>
/// What a scan may touch (0.3.0 §7, the scope guard) and how ports are written. Targets: an address, a CIDR range
/// (/16 or smaller), a first-last range ("192.168.1.10-60") or a host name, several separated by commas or spaces. Ranges
/// must be private (RFC 1918, loopback, link-local, CGNAT, IPv6 ULA); a public address is only scanned when it is typed
/// on its own (never as part of a range), which <see cref="Parse"/> reports so the screen can confirm it. Ports: "22",
/// "22,80,443", "8000-8100", "common" (the built-in list), together up to <see cref="MaxPorts"/>.
/// </summary>
public static class NetTargets
{
    public const int MaxAddresses = 65_536, MaxPorts = 65_535;

    /// <summary>The common-ports list: what servers, appliances and Windows machines usually answer on.</summary>
    public static readonly int[] Common =
    {
        21, 22, 23, 25, 53, 80, 81, 88, 110, 111, 123, 135, 139, 143, 161, 389, 443, 445, 465, 514, 587, 631, 636, 873, 902, 993, 995,
        1080, 1194, 1433, 1521, 1883, 2049, 2375, 2376, 3000, 3128, 3268, 3306, 3389, 4443, 5000, 5001, 5060, 5432, 5672, 5900, 5985,
        5986, 6379, 6443, 6514, 8000, 8006, 8008, 8080, 8081, 8443, 8888, 9000, 9090, 9100, 9200, 9443, 10000, 11434, 27017,
    };

    public sealed record Parsed(List<IPAddress> Addresses, List<string> Names, List<string> PublicSingles);

    /// <summary>Expands the targets. Throws <see cref="ArgumentException"/> with a readable reason for anything refused.</summary>
    public static Parsed Parse(string text)
    {
        var addrs = new List<IPAddress>(); var names = new List<string>(); var pub = new List<string>();
        foreach (var raw in text.Split(new[] { ',', ' ', ';', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var t = raw.Trim();
            if (t.Contains('/'))
            {
                var range = Ssh.LanScanner.Expand(t).ToList();
                if (range.Any(a => !IsPrivate(a))) throw new ArgumentException($"{t} isn't a private range: only your own networks can be swept (type a single public address to test just that one).");
                addrs.AddRange(range);
            }
            else if (t.Contains('-') && t.Split('-') is [var a, var b] && IPAddress.TryParse(a, out var first) && first.AddressFamily == AddressFamily.InterNetwork)
            {
                // "192.168.1.10-60" or "192.168.1.10-192.168.1.60"
                var last = b.Contains('.') && IPAddress.TryParse(b, out var full) ? full : IPAddress.TryParse(string.Join('.', a.Split('.')[..3]) + "." + b, out var tail) ? tail : throw new ArgumentException($"{t}: not a range");
                uint u1 = ToUInt(first), u2 = ToUInt(last);
                if (u2 < u1 || u2 - u1 > MaxAddresses) throw new ArgumentException($"{t}: a range from low to high, at most {MaxAddresses:N0} addresses");
                for (uint u = u1; u <= u2; u++) { var ip = FromUInt(u); if (!IsPrivate(ip)) throw new ArgumentException($"{t} isn't a private range."); addrs.Add(ip); }
            }
            else if (IPAddress.TryParse(t, out var ip))
            {
                addrs.Add(ip);
                if (!IsPrivate(ip)) pub.Add(t);
            }
            else if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^[A-Za-z0-9][A-Za-z0-9.\-]{0,252}$")) names.Add(t);
            else throw new ArgumentException($"“{t}” isn't an address, range or host name.");
            if (addrs.Count > MaxAddresses) throw new ArgumentException($"At most {MaxAddresses:N0} addresses in one scan.");
        }
        return new Parsed(addrs.Distinct().ToList(), names.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), pub);
    }

    /// <summary>"22,80,443,8000-8100,common" → sorted distinct ports.</summary>
    public static List<int> Ports(string text)
    {
        var set = new SortedSet<int>();
        foreach (var raw in text.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var t = raw.Trim();
            if (t.Equals("common", StringComparison.OrdinalIgnoreCase)) { set.UnionWith(Common); continue; }
            if (t.Equals("all", StringComparison.OrdinalIgnoreCase)) t = "1-65535";
            var parts = t.Split('-');
            if (parts.Length is 1 or 2 && parts.All(p => int.TryParse(p, out var n) && n is >= 1 and <= 65535))
            {
                int a = int.Parse(parts[0]), b = int.Parse(parts[^1]);
                if (b < a) (a, b) = (b, a);
                for (int p = a; p <= b; p++) set.Add(p);
            }
            else throw new ArgumentException($"“{t}” isn't a port, a range like 8000-8100, or \"common\".");
        }
        if (set.Count == 0) throw new ArgumentException("No ports given.");
        return set.ToList();
    }

    /// <summary>Private, loopback, link-local, CGNAT (100.64/10) and IPv6 unique-local / link-local addresses.</summary>
    public static bool IsPrivate(IPAddress a)
    {
        if (IPAddress.IsLoopback(a)) return true;
        if (a.AddressFamily == AddressFamily.InterNetworkV6) return a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || (a.GetAddressBytes()[0] & 0xFE) == 0xFC;
        var b = a.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] is >= 64 and <= 127);
    }

    private static uint ToUInt(IPAddress a) { var b = a.GetAddressBytes(); return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]); }
    private static IPAddress FromUInt(uint u) => new(new[] { (byte)(u >> 24), (byte)(u >> 16), (byte)(u >> 8), (byte)u });
}
