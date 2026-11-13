using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DcxManager.Services;
using DcxManager.Ssh;
using Renci.SshNet;
using Renci.SshNet.Common;
using static DcxManager.DcxCtl.DcxCtlProtocol;

namespace DcxManager.DcxCtl;

/// <summary>
/// One dcx-ctl/2 session with a DarkCipher portal (CONTROL-API.md §5): over SSH to the portal's dcx-ctl user (the server
/// forces the relay command; the host key is pinned), the PSK + hybrid X25519 / ML-KEM-768 handshake, the sealed channel
/// (a portal that doesn't confirm version 2 on open is refused), "open" with the
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
        var (hello, priv, kemSeed) = MakeHello(keyId, kHs);
        Keys keys;
        try
        {
            await WriteFrameAsync(s, hello, ct);
            keys = CheckReply(hello, await ReadFrameAsync(s, ct), kHs, priv, kemSeed);   // wipes priv and kemSeed
        }
        finally { CryptographicOperations.ZeroMemory(priv); CryptographicOperations.ZeroMemory(kemSeed); CryptographicOperations.ZeroMemory(kHs); }
        var ch = new Channel(s, keys, server: false);
        await ch.SendAsync(new JsonObject { ["op"] = "open", ["proof"] = OpenProof(kAuth, keyId, keys.Sid, keys.Th) }, ct);
        var opened = await ch.RecvAsync(ct);
        if (opened["op"]?.GetValue<string>() != "opened")
            throw new ProtocolException("the portal refused the session: " + (opened["error"]?.GetValue<string>() ?? "refused") + " (key revoked, expired, wrong address, or locked)");
        if (opened["proto"] is not JsonValue pv || !pv.TryGetValue<int>(out var proto) || proto != DcxCtlProtocol.Version)
            throw new ProtocolException("the portal did not confirm protocol version 2 — not connected");
        return new DcxCtlSession(s, transport, ch, keys, kAuth, opened);
    }

    /// <summary>Over SSH: the saved host is the portal server with user dcx-ctl and its Ed25519 key; its host key must be
    /// the pinned one (Known hosts), or the connection is refused. The channel is opened the way <c>ssh -T dcx-ctl@host</c>
    /// opens it: a shell request with no terminal and NO command (0.6.54). The server's forced command (the relay) then
    /// runs; it refuses a connection that asked for any command of its own (it exits at once when SSH_ORIGINAL_COMMAND is
    /// set, so the key can't be used as a general tunnel), which is why an exec request never got a handshake reply.</summary>
    public static async Task<DcxCtlSession> ConnectAsync(HostEntry host, string enrolment, CancellationToken ct)
    {
        var ssh = await SshConnector.ConnectSshAsync(host, ct);
        try
        {
            var wire = new ShellWire(ssh.Client.CreateShellStreamNoTerminal(bufferSize: 256 * 1024), ssh.Dispose);
            return await OpenAsync(wire, enrolment, ct, wire);
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

    /// <summary>
    /// The no-terminal shell channel as a plain byte stream (0.6.54). A read waits for data and returns 0 only once the
    /// channel is closed: it is woken by the channel's data, close and error events, so it doesn't depend on whether the
    /// library's own read waits. Closing it ends the SSH connection, which also ends a read that is waiting.
    /// </summary>
    private sealed class ShellWire : Stream, IAsyncDisposable
    {
        private readonly ShellStream _shell;
        private readonly Action _closeSsh;
        private readonly SemaphoreSlim _signal = new(0);
        private int _ended, _closed;

        public ShellWire(ShellStream shell, Action closeSsh)
        {
            _shell = shell; _closeSsh = closeSsh;
            shell.DataReceived += (_, _) => Wake();
            shell.Closed += (_, _) => { Volatile.Write(ref _ended, 1); Wake(); };
            shell.ErrorOccurred += (_, e) => { Log.Write("DCX-CTL SSH channel", e.Exception); Volatile.Write(ref _ended, 1); Wake(); };
        }

        private void Wake()
        {
            try { if (_signal.CurrentCount == 0) _signal.Release(); }
            catch (Exception ex) when (ex is ObjectDisposedException or SemaphoreFullException) { }
        }

        public override bool CanRead => true; public override bool CanWrite => true; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (buffer.Length == 0) return 0;
            var tmp = new byte[Math.Min(buffer.Length, 64 * 1024)];
            // A cancelled wait closes the connection (a session is finished after a cancelled request anyway).
            await using var stop = ct.Register(Shut);
            while (true)
            {
                if (Volatile.Read(ref _closed) != 0) { ct.ThrowIfCancellationRequested(); throw new IOException("the SSH connection to the portal ended"); }
                bool ended = Volatile.Read(ref _ended) != 0;   // read the flag first, then drain: nothing that arrived before the close is lost
                int n;
                try { n = _shell.DataAvailable ? await Task.Run(() => _shell.Read(tmp, 0, tmp.Length), CancellationToken.None) : 0; }
                catch (Exception ex) when (ex is ObjectDisposedException or SshException or InvalidOperationException) { ct.ThrowIfCancellationRequested(); throw new IOException("the SSH connection to the portal ended", ex); }
                if (n > 0) { tmp.AsSpan(0, n).CopyTo(buffer.Span); return n; }
                if (ended) return 0;
                // Woken by data, close or error; the short limit is only a guard against a missed event.
                try { await _signal.WaitAsync(250, CancellationToken.None); }
                catch (ObjectDisposedException) { throw new IOException("the SSH connection to the portal ended"); }
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            try { _shell.Write(buffer, offset, count); _shell.Flush(); }
            catch (Exception ex) when (ex is ObjectDisposedException or SshException or InvalidOperationException) { throw new IOException("the SSH connection to the portal ended", ex); }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => Task.Run(() => Write(buffer, offset, count), ct);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => new(WriteAsync(buffer.ToArray(), 0, buffer.Length, ct));
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;

        private void Shut()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            try { _shell.Dispose(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or SshException) { }
            try { _closeSsh(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or SshException) { Log.Write("DCX-CTL SSH close", ex); }
            Wake();
        }

        protected override void Dispose(bool disposing) { if (disposing) Shut(); base.Dispose(disposing); }

        public override ValueTask DisposeAsync() { Shut(); GC.SuppressFinalize(this); return ValueTask.CompletedTask; }
    }
}
