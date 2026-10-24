using System.Runtime.InteropServices;

namespace DcxManager.Net;

/// <summary>
/// MX and TXT lookups through Windows' own resolver (DnsQuery, dnsapi.dll) — .NET has no API for them and no third-party
/// DNS library is needed. Used by the mail checks (MX, SPF, DMARC, a DKIM selector). Read-only; the queries go to the
/// resolvers Windows is configured with, like any name lookup.
/// </summary>
public static class DnsRecords
{
    private const ushort TypeMx = 15, TypeTxt = 16, TypeNs = 2, TypeCname = 5, TypeSoa = 6, TypePtr = 12, TypeSrv = 33;

    public sealed record Mx(string Host, int Preference);
    public sealed record Srv(string Target, int Priority, int Weight, int Port);
    public sealed record Soa(string PrimaryServer, string Admin, uint Serial, uint Refresh, uint Retry, uint Expire, uint MinimumTtl);

    /// <summary>NS, CNAME or PTR names ("NS", "CNAME", "PTR"; a PTR query takes the in-addr.arpa / ip6.arpa name or an IP).</summary>
    public static Task<List<string>> NamesAsync(string name, string type) => Task.Run(() =>
    {
        ushort t = type switch { "NS" => TypeNs, "CNAME" => TypeCname, "PTR" => TypePtr, _ => throw new ArgumentException("NS, CNAME or PTR") };
        if (t == TypePtr && System.Net.IPAddress.TryParse(name, out var ip)) name = ReverseName(ip);
        var list = new List<string>();
        Query(name, t, data => list.Add(Marshal.PtrToStringUni(Marshal.ReadIntPtr(data)) ?? ""));
        return list;
    });

    public static Task<List<Srv>> SrvAsync(string name) => Task.Run(() =>
    {
        var list = new List<Srv>();
        Query(name, TypeSrv, data =>
        {
            var o = IntPtr.Size;
            list.Add(new Srv(Marshal.PtrToStringUni(Marshal.ReadIntPtr(data)) ?? "", (ushort)Marshal.ReadInt16(data, o), (ushort)Marshal.ReadInt16(data, o + 2), (ushort)Marshal.ReadInt16(data, o + 4)));
        });
        return list.OrderBy(s => s.Priority).ThenByDescending(s => s.Weight).ToList();
    });

    public static Task<Soa?> SoaAsync(string name) => Task.Run(() =>
    {
        Soa? soa = null;
        Query(name, TypeSoa, data =>
        {
            var o = IntPtr.Size * 2;
            soa = new Soa(Marshal.PtrToStringUni(Marshal.ReadIntPtr(data)) ?? "", Marshal.PtrToStringUni(Marshal.ReadIntPtr(data, IntPtr.Size)) ?? "",
                (uint)Marshal.ReadInt32(data, o), (uint)Marshal.ReadInt32(data, o + 4), (uint)Marshal.ReadInt32(data, o + 8), (uint)Marshal.ReadInt32(data, o + 12), (uint)Marshal.ReadInt32(data, o + 16));
        });
        return soa;
    });

    /// <summary>192.0.2.10 → 10.2.0.192.in-addr.arpa; IPv6 → the nibble ip6.arpa name.</summary>
    public static string ReverseName(System.Net.IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        if (b.Length == 4) return $"{b[3]}.{b[2]}.{b[1]}.{b[0]}.in-addr.arpa";
        return string.Join(".", b.Reverse().SelectMany(x => new[] { (x & 0xF).ToString("x"), (x >> 4).ToString("x") })) + ".ip6.arpa";
    }

    public static Task<List<Mx>> MxAsync(string domain) => Task.Run(() =>
    {
        var list = new List<Mx>();
        Query(domain, TypeMx, data => list.Add(new Mx(Marshal.PtrToStringUni(Marshal.ReadIntPtr(data)) ?? "", Marshal.ReadInt16(data, IntPtr.Size))));
        return list.OrderBy(m => m.Preference).ToList();
    });

    /// <summary>Each TXT record as one string (its parts joined, as SPF/DMARC/DKIM expect).</summary>
    public static Task<List<string>> TxtAsync(string name) => Task.Run(() =>
    {
        var list = new List<string>();
        Query(name, TypeTxt, data =>
        {
            int count = Marshal.ReadInt32(data);
            var arr = data + (IntPtr.Size == 8 ? 8 : 4);
            list.Add(string.Concat(Enumerable.Range(0, Math.Min(count, 64)).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(arr, i * IntPtr.Size)) ?? "")));
        });
        return list;
    });

    private static void Query(string name, ushort type, Action<IntPtr> read)
    {
        if (name.Length is 0 or > 253 || name.Any(c => char.IsControl(c) || c == ' ')) throw new ArgumentException("not a DNS name");
        Services.Offline.EnforceStatic(name, "a DNS lookup");
        int rc = DnsQuery_W(name, type, 0, IntPtr.Zero, out var results, IntPtr.Zero);
        if (rc == 9003 || rc == 9501) return;   // name / record doesn't exist
        if (rc != 0) throw new InvalidOperationException($"DNS lookup of {name} failed (error {rc})");
        try
        {
            int dataOffset = IntPtr.Size == 8 ? 32 : 24;   // DNS_RECORD header before the Data union
            for (var p = results; p != IntPtr.Zero; p = Marshal.ReadIntPtr(p))
                if ((ushort)Marshal.ReadInt16(p, IntPtr.Size * 2) == type) read(p + dataOffset);
        }
        finally { DnsRecordListFree(results, 1); }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("dnsapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int DnsQuery_W(string name, ushort type, uint options, IntPtr extra, out IntPtr results, IntPtr reserved);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("dnsapi.dll")]
    private static extern void DnsRecordListFree(IntPtr list, int freeType);
}
