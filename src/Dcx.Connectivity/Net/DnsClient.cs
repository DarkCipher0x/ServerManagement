using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DcxManager.Net;

/// <summary>
/// A small DNS client (RFC 1035 wire format, EDNS0 4 KB, TCP when the answer is truncated) that asks one chosen server
/// directly — Windows' resolver can't be pointed at a server per query, and the propagation check needs exactly that:
/// the same question to Cloudflare, Google, Quad9, OpenDNS and your own resolver, compared side by side. Read-only; one
/// query per call; the server goes through offline mode like any destination; nothing but the question is sent.
/// </summary>
public static class DnsClient
{
    public sealed record Answer(string Name, string Type, uint Ttl, string Data);
    public sealed record Response(string Server, string Rcode, bool Authoritative, bool Truncated, double Ms, List<Answer> Answers, string? Error)
    {
        public string Summary => Error != null ? "✕ " + Error : Answers.Count == 0 ? (Rcode == "NOERROR" ? "no records" : Rcode) : string.Join(" · ", Answers.Where(a => a.Type != "RRSIG").Select(a => a.Data));
    }

    /// <summary>The public resolvers the propagation check offers (name, address). "System" is added by the caller.</summary>
    public static readonly (string Name, string Address)[] PublicResolvers =
    [
        ("Cloudflare", "1.1.1.1"), ("Google", "8.8.8.8"), ("Quad9", "9.9.9.9"), ("OpenDNS", "208.67.222.222"),
        ("Cloudflare (2)", "1.0.0.1"), ("Google (2)", "8.8.4.4"), ("Control D", "76.76.2.0"), ("AdGuard", "94.140.14.14"),
    ];

    private static readonly Dictionary<string, ushort> Types = new(StringComparer.OrdinalIgnoreCase)
    { ["A"] = 1, ["NS"] = 2, ["CNAME"] = 5, ["SOA"] = 6, ["PTR"] = 12, ["MX"] = 15, ["TXT"] = 16, ["AAAA"] = 28, ["SRV"] = 33, ["CAA"] = 257, ["ANY"] = 255 };
    private static readonly Dictionary<ushort, string> TypeNames = Types.ToDictionary(k => k.Value, k => k.Key);
    private static readonly string[] Rcodes = ["NOERROR", "FORMERR", "SERVFAIL", "NXDOMAIN", "NOTIMP", "REFUSED"];

    public static IEnumerable<string> TypeList => Types.Keys.Where(k => k != "ANY");

    /// <summary>Asks <paramref name="server"/> (IP, optional :port) for <paramref name="name"/> / <paramref name="type"/>.</summary>
    public static async Task<Response> QueryAsync(string name, string type, string server, int timeoutMs = 3000, CancellationToken ct = default)
    {
        if (!Types.TryGetValue(type, out var qtype)) throw new ArgumentException("record type");
        var ep = ParseServer(server);
        if (qtype == 12 && IPAddress.TryParse(name.Trim(), out var ip)) name = DnsRecords.ReverseName(ip);
        name = NetTests.CheckHost(name).TrimEnd('.');
        Services.Offline.EnforceStatic(ep.Address.ToString(), "a DNS propagation check");
        var id = (ushort)System.Security.Cryptography.RandomNumberGenerator.GetInt32(1, 65535);   // unguessable id: a forged answer must match it
        var query = Build(id, name, qtype);
        var sw = Stopwatch.StartNew();
        try
        {
            byte[] reply;
            using (var t = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                t.CancelAfter(timeoutMs);
                reply = await UdpAsync(ep, query, id, t.Token);
                var r = Parse(reply, id, server, sw.Elapsed.TotalMilliseconds);
                if (!r.Truncated) return r;
                reply = await TcpAsync(ep, query, t.Token);
            }
            return Parse(reply, id, server, sw.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new Response(server, "", false, false, sw.Elapsed.TotalMilliseconds, [], "no answer (timed out)"); }
        catch (Exception ex) when (ex is SocketException or IOException or InvalidDataException) { return new Response(server, "", false, false, sw.Elapsed.TotalMilliseconds, [], ex.Message); }
    }

    public static IPEndPoint ParseServer(string server)
    {
        server = server.Trim();
        if (IPEndPoint.TryParse(server, out var ep) && ep.Port != 0 && !IPAddress.TryParse(server, out _)) return ep;
        if (IPAddress.TryParse(server.Trim('[', ']'), out var ip)) return new IPEndPoint(ip, 53);
        throw new ArgumentException("A resolver is an IP address (with an optional :port).");
    }

    private static async Task<byte[]> UdpAsync(IPEndPoint ep, byte[] query, ushort id, CancellationToken ct)
    {
        using var udp = new UdpClient(ep.AddressFamily);
        udp.Connect(ep);
        await udp.SendAsync(query, ct);
        // Only an answer with our id from the server we asked counts (anything else is noise, or an answer to someone else).
        for (int i = 0; i < 8; i++)
        {
            var r = await udp.ReceiveAsync(ct);
            if (r.Buffer.Length >= 12 && BinaryPrimitives.ReadUInt16BigEndian(r.Buffer) == id && r.RemoteEndPoint.Address.Equals(ep.Address)) return r.Buffer;
        }
        throw new InvalidDataException("the answers didn't match the question");
    }

    private static async Task<byte[]> TcpAsync(IPEndPoint ep, byte[] query, CancellationToken ct)
    {
        using var tcp = new TcpClient(ep.AddressFamily);
        await tcp.ConnectAsync(ep, ct);
        var s = tcp.GetStream();
        var len = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(len, (ushort)query.Length);
        await s.WriteAsync(len, ct); await s.WriteAsync(query, ct);
        await s.ReadExactlyAsync(len, ct);
        var n = BinaryPrimitives.ReadUInt16BigEndian(len);
        if (n > 65535) throw new InvalidDataException("oversized answer");
        var buf = new byte[n];
        await s.ReadExactlyAsync(buf, ct);
        return buf;
    }

    // ---- wire format --------------------------------------------------------------------------------------------------

    public static byte[] Build(ushort id, string name, ushort qtype)
    {
        var b = new List<byte>(64);
        void U16(ushort v) { b.Add((byte)(v >> 8)); b.Add((byte)v); }
        U16(id); U16(0x0100); U16(1); U16(0); U16(0); U16(1);   // RD set; 1 question, 1 additional (OPT)
        foreach (var label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            if (bytes.Length > 63) throw new ArgumentException("a label is over 63 characters");
            b.Add((byte)bytes.Length); b.AddRange(bytes);
        }
        b.Add(0); U16(qtype); U16(1);
        b.Add(0); U16(41); U16(4096); b.AddRange(new byte[4]); U16(0);   // OPT: 4 KB UDP payload
        return b.ToArray();
    }

    public static Response Parse(byte[] m, ushort expectId, string server, double ms)
    {
        if (m.Length < 12) throw new InvalidDataException("short answer");
        int p = 0;
        ushort R16() { var v = BinaryPrimitives.ReadUInt16BigEndian(m.AsSpan(p)); p += 2; return v; }
        uint R32() { var v = BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(p)); p += 4; return v; }
        var id = R16(); var flags = R16(); var qd = R16(); var an = R16(); R16(); R16();
        if (id != expectId) throw new InvalidDataException("the answer's id doesn't match");
        var rcode = flags & 0xF;
        var answers = new List<Answer>();
        for (int i = 0; i < qd; i++) { ReadName(m, ref p); p += 4; }
        for (int i = 0; i < an && p < m.Length; i++)
        {
            var name = ReadName(m, ref p);
            var type = R16(); R16(); var ttl = R32(); var len = R16();
            if (p + len > m.Length) throw new InvalidDataException("truncated record");
            var data = Rdata(m, p, len, type);
            p += len;
            answers.Add(new Answer(name, TypeNames.GetValueOrDefault(type, "TYPE" + type), ttl, data));
        }
        return new Response(server, rcode < Rcodes.Length ? Rcodes[rcode] : "RCODE" + rcode, (flags & 0x0400) != 0, (flags & 0x0200) != 0, ms, answers, null);
    }

    private static string Rdata(byte[] m, int p, int len, ushort type)
    {
        int q = p;
        switch (type)
        {
            case 1 when len == 4: return new IPAddress(m.AsSpan(p, 4)).ToString();
            case 28 when len == 16: return new IPAddress(m.AsSpan(p, 16)).ToString();
            case 2 or 5 or 12: return ReadName(m, ref q);
            case 15: { var pref = BinaryPrimitives.ReadUInt16BigEndian(m.AsSpan(p)); q = p + 2; return $"{pref} {ReadName(m, ref q)}"; }
            case 6: { var mname = ReadName(m, ref q); var rname = ReadName(m, ref q); var serial = BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(q)); return $"{mname} {rname} serial {serial}"; }
            case 33: { var pr = BinaryPrimitives.ReadUInt16BigEndian(m.AsSpan(p)); var w = BinaryPrimitives.ReadUInt16BigEndian(m.AsSpan(p + 2)); var port = BinaryPrimitives.ReadUInt16BigEndian(m.AsSpan(p + 4)); q = p + 6; return $"{pr} {w} {port} {ReadName(m, ref q)}"; }
            case 16:
            {
                var sb = new StringBuilder();
                while (q < p + len) { int l = m[q++]; if (q + l > p + len) break; sb.Append(Encoding.UTF8.GetString(m, q, l)); q += l; }
                return Printable(sb.ToString());
            }
            case 257 when len >= 2: { int tagLen = m[p + 1]; if (2 + tagLen > len) break; return $"{m[p]} {Encoding.ASCII.GetString(m, p + 2, tagLen)} \"{Printable(Encoding.UTF8.GetString(m, p + 2 + tagLen, len - 2 - tagLen))}\""; }
        }
        return Convert.ToHexString(m, p, len);
    }

    private static string ReadName(byte[] m, ref int p)
    {
        var sb = new StringBuilder();
        int jumps = 0, end = -1;
        while (p < m.Length)
        {
            int l = m[p];
            if (l == 0) { p++; break; }
            if ((l & 0xC0) == 0xC0)
            {
                if (p + 1 >= m.Length || ++jumps > 32) throw new InvalidDataException("bad name pointer");
                int target = ((l & 0x3F) << 8) | m[p + 1];
                if (end < 0) end = p + 2;
                if (target >= p) throw new InvalidDataException("forward name pointer");   // pointers only go backwards
                p = target; continue;
            }
            p++;
            if (p + l > m.Length) throw new InvalidDataException("bad label");
            if (sb.Length > 0) sb.Append('.');
            sb.Append(Printable(Encoding.ASCII.GetString(m, p, l)));
            p += l;
            if (sb.Length > 255) throw new InvalidDataException("name too long");
        }
        if (end >= 0) p = end;
        return sb.ToString();
    }

    private static string Printable(string s) => new(s.Select(c => char.IsControl(c) ? '·' : c).ToArray());

    // ---- propagation --------------------------------------------------------------------------------------------------

    public sealed record PropagationRow(string Resolver, string Address, Response Response, bool Agrees);

    /// <summary>The same question to every resolver, compared with the majority answer.</summary>
    public static async Task<List<PropagationRow>> PropagationAsync(string name, string type, IReadOnlyList<(string Name, string Address)> resolvers, Action<PropagationRow>? onRow = null, CancellationToken ct = default)
    {
        var rows = new List<PropagationRow>();
        var results = await Task.WhenAll(resolvers.Select(async r => (r, await QueryAsync(name, type, r.Address, ct: ct))));
        string Key(Response x) => x.Error != null ? "" : string.Join("|", x.Answers.Where(a => a.Type != "RRSIG").Select(a => a.Data).OrderBy(d => d, StringComparer.Ordinal));
        var majority = results.Select(x => Key(x.Item2)).Where(k => k.Length > 0).GroupBy(k => k).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key ?? "";
        foreach (var (r, resp) in results)
        {
            var row = new PropagationRow(r.Name, r.Address, resp, resp.Error == null && Key(resp) == majority);
            rows.Add(row);
            onRow?.Invoke(row);
        }
        return rows;
    }
}
