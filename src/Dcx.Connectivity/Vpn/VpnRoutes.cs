using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace DcxManager.Vpn;

/// <summary>
/// Routes for the VPN tunnels (0.3.0 §24): CIDR parsing and masks, and the Windows routing table read back
/// (GetIpForwardTable2, read-only, no rights needed) so the screen shows the routes that are really installed for a
/// tunnel's interface — not just the ones asked for.
/// </summary>
public static class VpnRoutes
{
    public sealed record Route(string Prefix, int Bits, string NextHop, int InterfaceIndex, int Metric);

    public static bool TryParseCidr(string s, out IPAddress ip, out int bits)
    {
        ip = IPAddress.None; bits = 0;
        var parts = s.Trim().Split('/');
        if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out var a)) return false;
        int max = a.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
        if (parts.Length == 2) { if (!int.TryParse(parts[1], out bits) || bits < 0 || bits > max) return false; }
        else bits = max;
        ip = a;
        return true;
    }

    /// <summary>Validates a list of routes typed by the user ("10.0.0.0/8, 192.168.50.0/24"); returns them normalised.</summary>
    public static List<string> Parse(string text)
    {
        var o = new List<string>();
        foreach (var r in text.Split([',', ';', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryParseCidr(r, out var ip, out var bits)) throw new ArgumentException($"{r} isn't an address/prefix like 10.0.0.0/8");
            o.Add(Network(ip, bits) + "/" + bits);
        }
        return o.Distinct().ToList();
    }

    public static string Network(IPAddress ip, int bits)
    {
        var b = ip.GetAddressBytes();
        for (int i = 0; i < b.Length; i++)
        {
            int keep = Math.Clamp(bits - i * 8, 0, 8);
            b[i] = (byte)(b[i] & (keep == 0 ? 0 : 0xFF << (8 - keep)));
        }
        return new IPAddress(b).ToString();
    }

    public static string Mask(int bits)
    {
        var b = new byte[4];
        for (int i = 0; i < 4; i++) { int keep = Math.Clamp(bits - i * 8, 0, 8); b[i] = (byte)(keep == 0 ? 0 : 0xFF << (8 - keep)); }
        return new IPAddress(b).ToString();
    }

    public static int MaskBits(IPAddress mask) => mask.GetAddressBytes().Sum(x => System.Numerics.BitOperations.PopCount(x));

    /// <summary>Is <paramref name="ip"/> covered by any of the routes?</summary>
    public static bool Covers(IEnumerable<string> routes, IPAddress ip) =>
        routes.Any(r => TryParseCidr(r, out var net, out var bits) && Mail.MailNet.InCidr(ip, net, bits));

    /// <summary>The interface index of a network interface by its name ("wg-office", "OpenVPN TAP-Windows6"), or null.</summary>
    public static int? InterfaceIndex(string name)
    {
        foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            if (n.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || n.Description.Equals(name, StringComparison.OrdinalIgnoreCase))
                try { return n.GetIPProperties().GetIPv4Properties()?.Index; } catch (NetworkInformationException) { return null; }
        return null;
    }

    /// <summary>The IPv4 routing table (read-only).</summary>
    public static List<Route> Table()
    {
        var o = new List<Route>();
        if (GetIpForwardTable2(2 /* AF_INET */, out var table) != 0) return o;
        try
        {
            int count = Marshal.ReadInt32(table);
            // MIB_IPFORWARD_TABLE2: NumEntries (4) + padding to 8, then MIB_IPFORWARD_ROW2 entries (each 104 bytes on x64).
            var rowSize = Marshal.SizeOf<MIB_IPFORWARD_ROW2>();
            var first = table + 8;
            for (int i = 0; i < count; i++)
            {
                var r = Marshal.PtrToStructure<MIB_IPFORWARD_ROW2>(first + i * rowSize);
                var prefix = new IPAddress(BitConverter.GetBytes(r.DestinationPrefixAddr)).ToString();
                var hop = new IPAddress(BitConverter.GetBytes(r.NextHopAddr)).ToString();
                o.Add(new Route(prefix, r.PrefixLength, hop, (int)r.InterfaceIndex, (int)r.Metric));
            }
        }
        finally { FreeMibTable(table); }
        return o;
    }

    /// <summary>The routes installed through one interface.</summary>
    public static List<Route> Via(int interfaceIndex) => Table().Where(r => r.InterfaceIndex == interfaceIndex).OrderBy(r => r.Prefix).ToList();

    // Only the fields the table needs are read: the layout below mirrors MIB_IPFORWARD_ROW2 up to Metric (x64).
    [StructLayout(LayoutKind.Explicit, Size = 104)]
    private struct MIB_IPFORWARD_ROW2
    {
        [FieldOffset(0)] public ulong InterfaceLuid;
        [FieldOffset(8)] public uint InterfaceIndex;
        [FieldOffset(12)] public ushort DestFamily;        // SOCKADDR_INET starts at 12: sa_family
        [FieldOffset(16)] public uint DestinationPrefixAddr;   // sin_addr (IPv4) at +4 of the sockaddr
        [FieldOffset(40)] public byte PrefixLength;        // after the 28-byte SOCKADDR_INET
        [FieldOffset(44)] public ushort NextHopFamily;
        [FieldOffset(48)] public uint NextHopAddr;
        [FieldOffset(76)] public uint SitePrefixLengthAndPad;
        [FieldOffset(80)] public uint ValidLifetime;
        [FieldOffset(84)] public uint PreferredLifetime;
        [FieldOffset(88)] public uint Metric;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("iphlpapi.dll")] private static extern int GetIpForwardTable2(ushort family, out IntPtr table);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("iphlpapi.dll")] private static extern void FreeMibTable(IntPtr table);
}
