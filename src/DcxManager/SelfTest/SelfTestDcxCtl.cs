using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DcxManager.Infra;
using DcxManager.DcxCtl;
using static DcxManager.DcxCtl.DcxCtlProtocol;

namespace DcxManager;

/// <summary>
/// 0.3.0 preview.8 (§9): the DarkCipher portal client (dcx-ctl/1). RFC vectors pass; the enrolment string's check digits
/// catch a typo; against a loopback fake of the gateway + portal (which checks the hello MAC, the open proof, every
/// request MAC and the strictly increasing sequence, exactly as ctl_api.py does) the provider opens a session, lists the
/// portal, its sections and users, and a reply whose end-to-end MAC was changed in transit is refused. A wrong key is
/// refused at the handshake. No real portal is contacted (the live test is on the test VPS, with DarkCipher0x).
/// </summary>
public static class DcxCtlTests
{
    public static async Task RunAsync(Action<string> ok, Action<string, Exception?> fail)
    {
        try
        {
            SelfTest();
            var kid = RandomNumberGenerator.GetBytes(16); var secret = RandomNumberGenerator.GetBytes(32);
            var enrol = MakeEnrolment("dcx-test", kid, secret);
            var (portal, k2, s2) = ParseEnrolment(enrol);
            if (portal != "dcx-test" || !k2.SequenceEqual(kid) || !s2.SequenceEqual(secret)) throw new Exception("enrolment round trip");
            try { ParseEnrolment(enrol[..^1] + (enrol[^1] == 'a' ? 'b' : 'a')); throw new Exception("a typo in the check digits was accepted"); } catch (ProtocolException) { }
            if (Fields("a"u8.ToArray(), "bc").SequenceEqual(Fields("ab"u8.ToArray(), "c"))) throw new Exception("field encoding splices");

            using var fake = new FakePortal(kid, secret);
            var cfg = new ProviderConfig { Kind = "dcx-portal", Name = "selftest-portal" };
            DcxPortalProvider.Connector = async (_, ct) =>
            {
                var tcp = new TcpClient(); await tcp.ConnectAsync(IPAddress.Loopback, fake.Port, ct);
                return await DcxCtlSession.OpenAsync(tcp.GetStream(), enrol, ct, new Closer(tcp));
            };
            var p = Infra.Infra.Create(cfg);
            var caps = await p.DetectAsync(CancellationToken.None);
            if (!caps.Reachable || caps.CanControl || caps.Version != "9.9.9" || !caps.Detail.Contains("scope read")) throw new Exception("detect: " + caps);
            lock (fake.Seqs) fake.Seqs.Clear();   // sequence numbers are per session: check the inventory session's
            var inv = await p.InventoryAsync(CancellationToken.None);
            if (!inv.Any(o => o.Kind == "Portal" && o.Name == "DarkCipher Test") || !inv.Any(o => o.Kind == "System" && o.Props.GetValueOrDefault("version") == "9.9.9") || inv.Count(o => o.Kind == "User") != 2)
                throw new Exception("inventory: " + string.Join(" | ", inv.Select(o => $"{o.Kind}:{o.Name}:{o.Status}")));
            if (inv.Any(o => o.Props.Values.Any(v => v.Contains("hunter2-secret")))) throw new Exception("a secret-looking value wasn't masked");
            if (fake.Seqs.Count < 6 || !fake.Seqs.Zip(fake.Seqs.Skip(1)).All(x => x.Second > x.First)) throw new Exception("sequence numbers: " + string.Join(",", fake.Seqs));

            fake.TamperNext = true;
            try { await p.InventoryAsync(CancellationToken.None); throw new Exception("a tampered reply was accepted"); }
            catch (ProtocolException ex) when (ex.Message.Contains("end-to-end MAC")) { }

            // A wrong key: the fake (like the gateway) drops the connection; the client gets no session.
            var other = MakeEnrolment("dcx-test", kid, RandomNumberGenerator.GetBytes(32));
            try
            {
                var tcp = new TcpClient(); await tcp.ConnectAsync(IPAddress.Loopback, fake.Port);
                await using var bad = await DcxCtlSession.OpenAsync(tcp.GetStream(), other, CancellationToken.None, new Closer(tcp));
                throw new Exception("a wrong key got a session");
            }
            catch (ProtocolException) { }
            catch (System.IO.IOException) { }
            ok("DarkCipher portals (dcx-ctl/1): RFC 5869/7748/8439 vectors pass; enrolment string with check digits; against a loopback gateway+portal fake the handshake, open proof and per-request MACs verify, sequence numbers strictly increase, the portal, its sections (version 9.9.9) and users are listed with secrets masked; a reply altered in transit and a wrong key are refused");
        }
        catch (Exception ex) { fail("DarkCipher portals (dcx-ctl/1)", ex); }
        finally { DcxPortalProvider.Connector = null; }
    }

    private sealed class Closer(TcpClient t) : IAsyncDisposable { public ValueTask DisposeAsync() { t.Dispose(); return ValueTask.CompletedTask; } }

    /// <summary>The gateway and the portal in one: checks what dcx-ctl.py and ctl_api.py check, answers with MACs.</summary>
    private sealed class FakePortal : IDisposable
    {
        private readonly TcpListener _l = new(IPAddress.Loopback, 0);
        private readonly byte[] _kid, _kHs, _kAuth;
        public int Port { get; }
        public List<long> Seqs { get; } = new();
        public bool TamperNext { get; set; }

        public FakePortal(byte[] kid, byte[] secret)
        {
            _kid = kid; (_kHs, _kAuth) = ClientKeys(secret);
            _l.Start(); Port = ((IPEndPoint)_l.LocalEndpoint).Port;
            _ = Task.Run(async () => { while (true) { TcpClient c; try { c = await _l.AcceptTcpClientAsync(); } catch { return; } _ = Task.Run(() => Serve(c)); } });
        }

        private async Task Serve(TcpClient c)
        {
            using var _ = c;
            try
            {
                var s = c.GetStream();
                var hello = await ReadFrameAsync(s, CancellationToken.None);
                if (hello.Length != HelloLen || !hello.AsSpan(8, 16).SequenceEqual(_kid)) return;
                var (reply, keys) = MakeReply(hello, _kHs);   // throws on a wrong hello MAC → connection dropped
                await WriteFrameAsync(s, reply, CancellationToken.None);
                var ch = new Channel(s, keys, server: true);
                var open = await ch.RecvAsync(CancellationToken.None);
                if (open["proof"]?.GetValue<string>() != OpenProof(_kAuth, _kid, keys.Sid, keys.Th)) { await ch.SendAsync(new JsonObject { ["op"] = "closed", ["error"] = "refused" }, default); return; }
                await ch.SendAsync(new JsonObject { ["op"] = "opened", ["portal"] = "dcx-test", ["brand"] = "DarkCipher Test", ["account"] = "demo", ["scope"] = "read", ["expires"] = 0 }, default);
                long last = -1;
                while (true)
                {
                    var m = await ch.RecvAsync(CancellationToken.None);
                    if (m["op"]?.GetValue<string>() == "close") return;
                    var seq = m["seq"]!.GetValue<long>(); var verb = m["verb"]!.GetValue<string>(); var path = m["path"]!.GetValue<string>();
                    var digest = m["body_sha256"]!.GetValue<string>();
                    var body = Convert.FromBase64String(m["body"]!.GetValue<string>());
                    int status = 200; JsonNode reply2;
                    if (seq <= last || m["mac"]?.GetValue<string>() != RequestMac(_kAuth, keys.Sid, seq, verb, path, digest) || Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant() != digest)
                    { status = 403; reply2 = new JsonObject { ["error"] = "bad mac" }; }
                    else
                    {
                        last = seq; lock (Seqs) Seqs.Add(seq);
                        reply2 = path switch
                        {
                            "/admin/api/system" => new JsonObject { ["version"] = "9.9.9", ["ok"] = true, ["uptime"] = 1234, ["note"] = "password=hunter2-secret" },
                            "/admin/api/access/users" => new JsonObject { ["users"] = new JsonArray(new JsonObject { ["username"] = "demo", ["role"] = "owner" }, new JsonObject { ["username"] = "jat", ["role"] = "admin" }) },
                            _ => new JsonObject { ["ok"] = true },
                        };
                    }
                    var bytes = Encoding.UTF8.GetBytes(reply2.ToJsonString());
                    var mac = ResponseMac(_kAuth, keys.Sid, seq, status, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
                    if (TamperNext) { TamperNext = false; bytes = Encoding.UTF8.GetBytes(reply2.ToJsonString().Replace("9.9.9", "6.6.6").Replace("\"ok\":true", "\"ok\":false")); }
                    await ch.SendAsync(new JsonObject { ["id"] = m["id"]!.GetValue<long>(), ["status"] = status, ["type"] = "application/json", ["body"] = Convert.ToBase64String(bytes), ["more"] = false, ["mac"] = mac }, default);
                }
            }
            catch { }
        }

        public void Dispose() => _l.Stop();
    }
}
