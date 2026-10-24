using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DcxManager.Services;
using DcxManager.Ssh;
using static DcxManager.DcxCtl.DcxCtlProtocol;

namespace DcxManager.DcxCtl;

/// <summary>
/// One dcx-ctl/1 session with a DarkCipher portal (CONTROL-API.md §5): over SSH to the portal's dcx-ctl user (the server
/// forces the relay command; the host key is pinned), the PSK + X25519 handshake, the sealed channel, "open" with the
/// end-to-end proof, then admin requests each carrying a K_auth MAC and a strictly increasing sequence number; every JSON
/// reply's MAC is checked, so a compromised gateway can't forge or change an answer. The enrolment string's secret is
/// only ever in memory here (it is stored sealed in the vault).
/// </summary>
public sealed class DcxCtlSession : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly IAsyncDisposable? _transport;
    private readonly Channel _ch;
    private readonly Keys _keys;
    private readonly byte[] _kAuth;
    private long _seq, _id;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>What the portal said on open: portal, brand, account, scope, expires.</summary>
    public JsonObject Opened { get; }
    public string Scope => Opened["scope"]?.GetValue<string>() ?? "";

    private static bool _selfTested;

    private DcxCtlSession(Stream s, IAsyncDisposable? transport, Channel ch, Keys keys, byte[] kAuth, JsonObject opened)
    { _stream = s; _transport = transport; _ch = ch; _keys = keys; _kAuth = kAuth; Opened = opened; }

    /// <summary>Over any byte stream (the SSH channel; the self-test's loopback).</summary>
    public static async Task<DcxCtlSession> OpenAsync(Stream s, string enrolment, CancellationToken ct, IAsyncDisposable? transport = null)
    {
        if (!_selfTested) { SelfTest(); _selfTested = true; }   // refuses to run with broken primitives
        var (_, keyId, secret) = ParseEnrolment(enrolment);
        var (kHs, kAuth) = ClientKeys(secret);
        CryptographicOperations.ZeroMemory(secret);
        var (hello, priv) = MakeHello(keyId, kHs);
        await WriteFrameAsync(s, hello, ct);
        var keys = CheckReply(hello, await ReadFrameAsync(s, ct), kHs, priv);
        CryptographicOperations.ZeroMemory(priv); CryptographicOperations.ZeroMemory(kHs);
        var ch = new Channel(s, keys, server: false);
        await ch.SendAsync(new JsonObject { ["op"] = "open", ["proof"] = OpenProof(kAuth, keyId, keys.Sid, keys.Th) }, ct);
        var opened = await ch.RecvAsync(ct);
        if (opened["op"]?.GetValue<string>() != "opened")
            throw new ProtocolException("the portal refused the session: " + (opened["error"]?.GetValue<string>() ?? "refused") + " (key revoked, expired, wrong address, or locked)");
        return new DcxCtlSession(s, transport, ch, keys, kAuth, opened);
    }

    /// <summary>Over SSH: the saved host is the portal server with user dcx-ctl and its Ed25519 key; its host key must be
    /// the pinned one (Known hosts), or the connection is refused.</summary>
    public static async Task<DcxCtlSession> ConnectAsync(HostEntry host, string enrolment, CancellationToken ct)
    {
        var ssh = await SshConnector.ConnectSshAsync(host, ct);
        try
        {
            var cmd = ssh.Client.CreateCommand("dcx-ctl");
            var run = cmd.BeginExecute();
            var duplex = new Duplex(cmd.OutputStream, cmd.CreateInputStream(), () => { try { cmd.Dispose(); } catch { } ssh.Dispose(); return Task.CompletedTask; });
            _ = run;
            return await OpenAsync(duplex, enrolment, ct, duplex);
        }
        catch { ssh.Dispose(); throw; }
    }

    public sealed record Response(int Status, byte[] Body, string Type)
    {
        public JsonNode? Json => Body.Length == 0 ? null : JsonNode.Parse(Body);
    }

    /// <summary>One admin request. GET bodies are empty. Throws when the reply's MAC is missing or wrong.</summary>
    public async Task<Response> RequestAsync(string verb, string path, JsonNode? body = null, CancellationToken ct = default)
    {
        if (verb is not ("GET" or "POST") || !path.StartsWith("/admin/api/") || path.Contains("..")) throw new ArgumentException("not an admin API request");
        await _gate.WaitAsync(ct);
        try
        {
            var data = body == null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(body.ToJsonString());
            var seq = ++_seq; var id = ++_id;
            var digest = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
            await _ch.SendAsync(new JsonObject
            {
                ["op"] = "http", ["id"] = id, ["seq"] = seq, ["verb"] = verb, ["path"] = path, ["body"] = Convert.ToBase64String(data),
                ["body_sha256"] = digest, ["mac"] = RequestMac(_kAuth, _keys.Sid, seq, verb, path, digest),
            }, ct);
            var first = await _ch.RecvAsync(ct);
            using var all = new MemoryStream();
            var part = first;
            while (true)
            {
                if (part["id"]?.GetValue<long>() != id) throw new ProtocolException("reply for another request");
                all.Write(Convert.FromBase64String(part["body"]?.GetValue<string>() ?? ""));
                if (all.Length > 64L << 20) throw new ProtocolException("reply too large");
                if (part["more"]?.GetValue<bool>() != true) break;
                part = await _ch.RecvAsync(ct);
            }
            var bytes = all.ToArray();
            var status = (int)(first["status"]?.GetValue<long>() ?? 0);
            var mac = first["mac"]?.GetValue<string>() ?? "";
            var expect = ResponseMac(_kAuth, _keys.Sid, seq, status, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
            if (mac.Length != 64 || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(mac), Encoding.ASCII.GetBytes(expect)))
                throw new ProtocolException("the reply isn't signed by the portal (end-to-end MAC) — not trusted");
            return new Response(status, bytes, first["type"]?.GetValue<string>() ?? "");
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        try { using var t = new CancellationTokenSource(2000); await _ch.SendAsync(new JsonObject { ["op"] = "close" }, t.Token); } catch { }
        CryptographicOperations.ZeroMemory(_kAuth);
        if (_transport != null) await _transport.DisposeAsync();
    }

    /// <summary>The SSH command's output (read) and input (write) as one stream.</summary>
    private sealed class Duplex(Stream read, Stream write, Func<Task> close) : Stream, IAsyncDisposable
    {
        public override bool CanRead => true; public override bool CanWrite => true; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => read.Read(buffer, offset, count);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            // SSH.NET's output stream blocks: read on the pool, then copy.
            var tmp = new byte[buffer.Length];
            var n = await Task.Run(() => read.Read(tmp, 0, tmp.Length), ct);
            tmp.AsSpan(0, n).CopyTo(buffer.Span);
            return n;
        }
        public override void Write(byte[] buffer, int offset, int count) => write.Write(buffer, offset, count);
        public override void Flush() => write.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override async ValueTask DisposeAsync() { try { write.Dispose(); } catch { } await close(); }
    }
}
