using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DcxManager.Services;

namespace DcxManager.Api;

public sealed record HttpReq(string Method, string Path, string Query, Dictionary<string, string> Headers, byte[] Body)
{
    public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
}

public sealed record HttpResp(int Status, string ContentType, byte[] Body, Dictionary<string, string>? Extra = null)
{
    public static HttpResp Json(int status, object value) => new(status, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(value, JsonOpts));
    public static HttpResp Error(int status, string message, Dictionary<string, string>? extra = null) => new(status, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(new { error = message }, JsonOpts), extra);
    public static HttpResp Text(int status, string text, string type = "text/plain; charset=utf-8", Dictionary<string, string>? extra = null) => new(status, type, Encoding.UTF8.GetBytes(text), extra);

    public static JsonSerializerOptions JsonOpts => ApiJson.Options;
}

/// <summary>
/// The loopback HTTP server behind the local API and OBS broadcasts (ADR-011). A raw TcpListener on 127.0.0.1 only —
/// never another interface — with a deliberately small HTTP/1.1 subset: GET/POST, Content-Length bodies ≤ 64 KB,
/// headers ≤ 8 KB, 5 s to send a request, one request per connection, at most 32 at once. The Host header must name this
/// listener (DNS-rebinding defence). It runs only while the API is enabled or a broadcast is active, never while locked.
/// </summary>
public static class LocalServer
{
    public const int MaxHeader = 8 * 1024, MaxBody = 64 * 1024, MaxConcurrent = 32;

    private static TcpListener? _listener;
    private static CancellationTokenSource? _cts;
    private static readonly SemaphoreSlim Slots = new(MaxConcurrent);

    public static int Port { get; private set; }
    public static bool Running => _listener != null;
    /// <summary>Why the last start failed (e.g. the port is taken), shown in Settings.</summary>
    public static string? LastError { get; private set; }
    public static string BaseUrl => $"http://127.0.0.1:{Port}";

    public static event Action? StateChanged;

    /// <summary>Starts or stops the listener to match the settings and active broadcasts. UI thread.</summary>
    public static void Sync()
    {
        var s = AppSettings.Current;
        bool want = (s.LocalApiEnabled || Broadcasts.Any) && !AppLock.IsLocked && SecureStore.IsUnlocked && !SecureStore.Frozen;
        int port = Math.Clamp(s.LocalApiPort, 1024, 65535);
        if (want && (_listener == null || Port != port)) { Stop(); Start(port); }
        else if (!want && _listener != null) Stop();
    }

    private static void Start(int port)
    {
        try
        {
            var l = new TcpListener(IPAddress.Loopback, port);
            l.Server.ExclusiveAddressUse = true;
            l.Start(16);
            _listener = l; Port = port; LastError = null;
            _cts = new CancellationTokenSource();
            _ = AcceptLoop(l, _cts.Token);
            AuditLog.Write("app", "api", "local listener started", $"127.0.0.1:{port}");
        }
        catch (Exception ex)
        {
            _listener = null;
            LastError = ex is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse } ? $"Port {port} is already in use on this PC — pick another port in Settings." : ex.Message;
            Log.Write("Local API start", ex);
        }
        StateChanged?.Invoke();
    }

    public static void Stop()
    {
        if (_listener == null) return;
        try { _cts?.Cancel(); _listener.Stop(); } catch { }
        _listener = null; _cts = null;
        AuditLog.Write("app", "api", "local listener stopped", $"127.0.0.1:{Port}");
        StateChanged?.Invoke();
    }

    private static async Task AcceptLoop(TcpListener l, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await l.AcceptTcpClientAsync(ct); }
            catch { break; }
            if (!Slots.Wait(0)) { try { c.Close(); } catch { } continue; }
            _ = Task.Run(async () =>
            {
                try { await Handle(c); }
                catch (Exception ex) { Log.Write("Local API request", ex); }
                finally { Slots.Release(); try { c.Close(); } catch { } }
            });
        }
    }

    private static async Task Handle(TcpClient c)
    {
        // Belt and braces: the socket is bound to loopback, so anything else is impossible — refuse it anyway.
        if (c.Client.RemoteEndPoint is not IPEndPoint { } ep || !IPAddress.IsLoopback(ep.Address)) return;
        using var stream = c.GetStream();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        HttpResp resp;
        try
        {
            var req = await ReadRequest(stream, cts.Token);
            resp = req.Item2 ?? await Route(req.Item1!);
        }
        catch (OperationCanceledException) { resp = HttpResp.Error(408, "request timeout"); }
        catch (IOException) { return; }
        await Write(stream, resp);
        // Graceful close: half-close, then drain what the client may still be sending (an oversized request) so the
        // socket isn't reset before the client has read the response.
        try
        {
            c.Client.Shutdown(SocketShutdown.Send);
            using var drain = new CancellationTokenSource(500);
            var sink = new byte[8192];
            for (int total = 0; total < 256 * 1024;)
            {
                int n = await stream.ReadAsync(sink, drain.Token);
                if (n == 0) break;
                total += n;
            }
        }
        catch { }
    }

    /// <summary>Parses one request, or returns the error response for a malformed/oversized one.</summary>
    internal static async Task<(HttpReq?, HttpResp?)> ReadRequest(Stream s, CancellationToken ct)
    {
        var buf = new byte[MaxHeader + 4];
        int len = 0, end = -1;
        while (end < 0)
        {
            if (len >= MaxHeader) return (null, HttpResp.Error(431, "headers too large"));
            int n = await s.ReadAsync(buf.AsMemory(len, MaxHeader - len), ct);
            if (n == 0) throw new IOException("closed");
            len += n;
            end = buf.AsSpan(0, len).IndexOf("\r\n\r\n"u8);
        }
        var head = Encoding.Latin1.GetString(buf, 0, end).Split("\r\n");
        var parts = head[0].Split(' ');
        if (parts.Length != 3 || !parts[2].StartsWith("HTTP/1.")) return (null, HttpResp.Error(400, "bad request line"));
        if (parts[0] is not ("GET" or "POST")) return (null, HttpResp.Error(405, "method not allowed"));
        var target = parts[1];
        if (!target.StartsWith('/') || target.Length > 512) return (null, HttpResp.Error(400, "bad path"));
        var q = target.IndexOf('?');
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in head.Skip(1))
        {
            var i = line.IndexOf(':');
            if (i <= 0) return (null, HttpResp.Error(400, "bad header"));
            var name = line[..i].Trim();
            if (headers.ContainsKey(name) && name.Equals("host", StringComparison.OrdinalIgnoreCase)) return (null, HttpResp.Error(400, "duplicate Host"));
            headers[name] = line[(i + 1)..].Trim();
            if (headers.Count > 60) return (null, HttpResp.Error(431, "too many headers"));
        }
        if (headers.ContainsKey("transfer-encoding")) return (null, HttpResp.Error(501, "chunked bodies are not supported"));
        var body = Array.Empty<byte>();
        if (headers.TryGetValue("content-length", out var cl))
        {
            if (!int.TryParse(cl, out var blen) || blen < 0) return (null, HttpResp.Error(400, "bad Content-Length"));
            if (blen > MaxBody) return (null, HttpResp.Error(413, "body too large"));
            body = new byte[blen];
            int have = Math.Min(blen, len - end - 4);
            Array.Copy(buf, end + 4, body, 0, have);
            while (have < blen)
            {
                int n = await s.ReadAsync(body.AsMemory(have, blen - have), ct);
                if (n == 0) throw new IOException("closed");
                have += n;
            }
        }
        else if (parts[0] == "POST") return (null, HttpResp.Error(411, "Content-Length required"));
        var path = Uri.UnescapeDataString(q < 0 ? target : target[..q]);
        if (path.Contains("..") || path.Contains('\\') || path.Contains('\0')) return (null, HttpResp.Error(400, "bad path"));
        return (new HttpReq(parts[0], path, q < 0 ? "" : target[(q + 1)..], headers, body), null);
    }

    /// <summary>Routing after the shared checks. Handlers that touch app state run on the UI thread.</summary>
    internal static async Task<HttpResp> Route(HttpReq r)
    {
        var host = r.Header("host") ?? "";
        if (!host.Equals($"127.0.0.1:{Port}", StringComparison.OrdinalIgnoreCase) && !host.Equals($"localhost:{Port}", StringComparison.OrdinalIgnoreCase))
            return HttpResp.Error(403, "Host header not allowed");
        if (AppLock.IsLocked) return HttpResp.Error(423, "the app is locked");
        if (r.Path.StartsWith("/api/", StringComparison.Ordinal))
        {
            if (!AppSettings.Current.LocalApiEnabled) return HttpResp.Error(404, "the API is switched off");
            return await Ui.Thread.InvokeAsync(() => LocalApi.Handle(r));
        }
        if (r.Path.StartsWith("/b/", StringComparison.Ordinal)) return Broadcasts.Handle(r, host);
        return HttpResp.Error(404, "not found");
    }

    private static async Task Write(Stream s, HttpResp r)
    {
        var sb = new StringBuilder();
        sb.Append($"HTTP/1.1 {r.Status} {Reason(r.Status)}\r\n");
        sb.Append($"Content-Type: {r.ContentType}\r\nContent-Length: {r.Body.Length}\r\n");
        sb.Append("Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\nConnection: close\r\n");
        if (r.Extra != null) foreach (var (k, v) in r.Extra) sb.Append($"{k}: {v}\r\n");
        sb.Append("\r\n");
        try
        {
            await s.WriteAsync(Encoding.Latin1.GetBytes(sb.ToString()));
            await s.WriteAsync(r.Body);
            await s.FlushAsync();
        }
        catch (IOException) { }
    }

    private static string Reason(int s) => s switch
    {
        200 => "OK", 201 => "Created", 308 => "Permanent Redirect", 400 => "Bad Request", 401 => "Unauthorized", 403 => "Forbidden", 404 => "Not Found",
        405 => "Method Not Allowed", 408 => "Request Timeout", 411 => "Length Required", 413 => "Payload Too Large", 415 => "Unsupported Media Type",
        423 => "Locked", 429 => "Too Many Requests", 431 => "Request Header Fields Too Large", 501 => "Not Implemented", _ => "Error",
    };
}
