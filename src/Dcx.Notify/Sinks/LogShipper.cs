using System.Net.Http;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using DcxManager.Services;

namespace DcxManager.Notify;

/// <summary>What the Observability screen and Settings show about one sink.</summary>
public sealed record SinkStatus(string Id, string Name, string Kind, string State, DateTime? LastSent, long Sent, int Queued, string? LastError, DateTime? LastErrorAt);

/// <summary>
/// Ships the app's events to the configured <see cref="LogSink"/>s (0.4.0, Observability) the way <see cref="SyslogForwarder"/>
/// ships syslog: masked at enqueue time, kept in an encrypted spool while a sink can't be reached (at most <see cref="MaxQueue"/>
/// events; the oldest go first and the number lost is reported), per-sink back-off from 5 s to 5 min, TLS 1.2+ only with the
/// server's certificate checked by Windows or against the sink's SHA-256 pin, offline mode enforced by the shared HTTP guard.
/// Secrets are added as headers at send time only; nothing here writes to the app's own logs (that would loop) — failures
/// are kept in <see cref="Statuses"/>, masked.
/// </summary>
public static class LogShipper
{
    public const int MaxQueue = 5000;
    public const int Batch = 200;
    private const string SpoolFile = "observe-spool.dcx";

    /// <summary>An Azure access token for the Logs Ingestion API (scope https://monitor.azure.com/.default) for one of the
    /// Azure subscriptions; set by the app (the cloud layer knows how to sign in). Null = Azure Monitor sinks can't send.</summary>
    public static Func<string, CancellationToken, Task<string>>? AzureToken { get; set; }
    /// <summary>Test seam: the HTTP handler for every sink (the self-test's fakes); null = real sockets per sink.</summary>
    internal static Func<LogSink, HttpMessageHandler>? HandlerForTest { get; set; }
    internal static TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    private sealed class Item(string doc, HashSet<string> pending) { public string Doc = doc; public HashSet<string> Pending = pending; }
    private sealed record Spooled(string Doc, string[] Pending);
    private sealed class Conn(HttpClient http, string key) { public HttpClient Http = http; public string Key = key; }

    private static readonly object Gate = new();
    private static readonly LinkedList<Item> Queue = new();
    private static readonly SemaphoreSlim Signal = new(0, int.MaxValue);
    private static readonly Dictionary<string, SinkStatus> Status = new();
    private static readonly Dictionary<string, (DateTime RetryAt, TimeSpan Backoff)> Retry = new();
    private static readonly Dictionary<string, Conn> Conns = new();
    private static readonly int[] Minute = new int[60];   // events shipped per minute, the last hour (the screen's bars)
    private static long _minuteStamp;
    private static CancellationTokenSource? _cts;
    private static Task? _loop;
    private static int _dropped;
    private static bool _spoolDirty;
    private static DateTime _lastSpool;

    public static event Action? StatusChanged;
    /// <summary>A sink fails after it had worked (the notifier can mail about it).</summary>
    public static event Action<string, string>? Failing;

    private static AppSettings S => AppSettings.Current;

    /// <summary>The configured sinks (from the settings), valid ones only.</summary>
    public static List<LogSink> Sinks => LogSink.ParseList(S.ObserveSinks);

    public static IReadOnlyList<SinkStatus> Statuses
    {
        get
        {
            lock (Gate)
            {
                var sinks = Sinks;
                return sinks.Select(s => (Status.TryGetValue(s.Id, out var st) ? st : new SinkStatus(s.Id, s.Name, s.Kind, s.Enabled ? "idle" : "disabled", null, 0, 0, null, null))
                    with { Name = s.Name, Kind = s.Kind, Queued = Queue.Count(i => i.Pending.Contains(s.Id)) }).ToList();
            }
        }
    }

    public static int Dropped { get { lock (Gate) return _dropped; } }
    public static int QueuedTotal { get { lock (Gate) return Queue.Count; } }

    /// <summary>Events shipped per minute over the last hour, oldest first (for the activity bars).</summary>
    public static int[] LastHour()
    {
        lock (Gate)
        {
            Roll();
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
            var o = new int[60];
            for (int i = 0; i < 60; i++) o[i] = Minute[(now - 59 + i) % 60 is var k && k >= 0 ? k : k + 60];
            return o;
        }
    }

    private static void Roll()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
        if (_minuteStamp == 0) _minuteStamp = now;
        for (var m = _minuteStamp + 1; m <= now && m - _minuteStamp <= 60; m++) Minute[m % 60] = 0;
        if (now - _minuteStamp > 60) Array.Clear(Minute);
        _minuteStamp = now;
    }

    /// <summary>Starts shipping (after the store is unlocked). Safe to call again.</summary>
    public static void Start()
    {
        lock (Gate)
        {
            if (_loop != null) return;
            AuditLog.Added += OnAudit;
            Log.Written += OnAppLog;
            try
            {
                foreach (var sp in SecureStore.Read<List<Spooled>>(SpoolFile))
                    if (sp.Doc.Length > 0 && sp.Pending.Length > 0) Queue.AddLast(new Item(sp.Doc, new HashSet<string>(sp.Pending)));
            }
            catch { }
            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => LoopAsync(_cts.Token));
        }
        Signal.Release();
    }

    /// <summary>Stops, writing what is still queued to the encrypted spool.</summary>
    public static void Stop()
    {
        lock (Gate)
        {
            AuditLog.Added -= OnAudit;
            Log.Written -= OnAppLog;
            _cts?.Cancel();
            _loop = null;
        }
        SaveSpool(force: true);
        lock (Gate) { foreach (var c in Conns.Values) c.Http.Dispose(); Conns.Clear(); }
    }

    private static void OnAudit(AuditEntry e) { if (S.ObserveEnabled && S.ObserveSendAudit) Enqueue(LogEvent.Audit(e)); }
    private static void OnAppLog(string where, string entry) { if (S.ObserveEnabled && S.ObserveSendAppLog) Enqueue(LogEvent.AppLog(DateTimeOffset.Now, where, entry)); }

    /// <summary>Queues an event for every enabled sink (nothing when none is configured).</summary>
    public static void Enqueue(LogEvent e)
    {
        var targets = Sinks.Where(s => s.Enabled).Select(s => s.Id).ToHashSet();
        if (targets.Count == 0) return;
        lock (Gate)
        {
            Queue.AddLast(new Item(e.ToJsonString(), targets));
            while (Queue.Count > MaxQueue) { Queue.RemoveFirst(); _dropped++; }
            _spoolDirty = true;
        }
        Signal.Release();
    }

    /// <summary>Sends one test event to one sink (or every enabled sink) now and says what happened, per sink.</summary>
    public static async Task<string> SendTestAsync(string? sinkId = null, CancellationToken ct = default)
    {
        var sinks = Sinks.Where(s => sinkId == null ? s.Enabled : s.Id == sinkId).ToList();
        if (sinks.Count == 0) return sinkId == null ? "No log sink is set up." : "That sink doesn't exist.";
        var lines = new List<string>();
        foreach (var s in sinks)
        {
            try
            {
                await SendAsync(s, new[] { LogEvent.Test(s.Name) }, ct);
                lines.Add($"✓ {s.Name}: sent to {s.Host} ({s.KindTitle}).");
            }
            catch (Exception ex) { Fail(s, ex); lines.Add($"✕ {s.Name}: {Status[s.Id].LastError}"); }
        }
        return string.Join("\n", lines);
    }

    private static async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Signal.WaitAsync(TimeSpan.FromSeconds(5), ct); } catch (OperationCanceledException) { break; }
            SaveSpool(force: false);
            var sinks = Sinks;
            if (!S.ObserveEnabled || sinks.Count == 0)
            {
                lock (Gate) { if (Queue.Count > 0) { Queue.Clear(); _spoolDirty = true; } }
                continue;
            }
            var known = sinks.Select(s => s.Id).ToHashSet();
            lock (Gate)
            {
                // Sinks that were removed: nothing waits for them any more.
                var node = Queue.First;
                while (node != null) { var next = node.Next; node.Value.Pending.IntersectWith(known); if (node.Value.Pending.Count == 0) Queue.Remove(node); node = next; }
            }
            bool more = false;
            foreach (var s in sinks.Where(s => s.Enabled))
            {
                List<Item> batch;
                lock (Gate)
                {
                    if (Retry.TryGetValue(s.Id, out var r) && DateTime.UtcNow < r.RetryAt) continue;
                    batch = Queue.Where(i => i.Pending.Contains(s.Id)).Take(Batch).ToList();
                }
                if (batch.Count == 0) continue;
                var events = batch.Select(i => LogEvent.FromJson(i.Doc)).Where(e => e != null).Cast<LogEvent>().ToList();
                try
                {
                    int dropped; lock (Gate) { dropped = _dropped; }
                    if (dropped > 0) events.Insert(0, LogEvent.Make(DateTimeOffset.Now, "dropped", "warning", $"{dropped} event(s) were dropped while the sinks were unreachable (spool limit {MaxQueue})."));
                    await SendAsync(s, events, ct);
                    lock (Gate)
                    {
                        if (dropped > 0) _dropped = Math.Max(0, _dropped - dropped);
                        foreach (var i in batch) { i.Pending.Remove(s.Id); if (i.Pending.Count == 0) Queue.Remove(i); }
                        _spoolDirty = true;
                        Retry.Remove(s.Id);
                        more |= Queue.Any(i => i.Pending.Contains(s.Id));
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    Fail(s, ex);
                    lock (Gate)
                    {
                        var back = Retry.TryGetValue(s.Id, out var r) ? TimeSpan.FromSeconds(Math.Min(300, r.Backoff.TotalSeconds * 2)) : TimeSpan.FromSeconds(5);
                        Retry[s.Id] = (DateTime.UtcNow + back, back);
                    }
                }
            }
            if (more) Signal.Release();
        }
    }

    /// <summary>Builds the sink's requests and sends them (one connection per sink, re-made when its settings change).</summary>
    private static async Task SendAsync(LogSink s, IReadOnlyList<LogEvent> events, CancellationToken ct)
    {
        string? secret = S.ObserveSecrets.TryGetValue(s.Id, out var sec) ? sec : null;
        string? token = null;
        if (s.Auth == "entra")
        {
            if (AzureToken == null) throw new InvalidOperationException("Azure sign-in isn't available in this build");
            using var tcts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            tcts.CancelAfter(RequestTimeout);
            token = await AzureToken(s.AzureAccountId, tcts.Token);
        }
        else if (s.Auth != "none" && string.IsNullOrEmpty(secret)) throw new InvalidOperationException("no API key / password / token is stored for this sink (Settings → Integrations → Log sinks)");
        var http = Client(s);
        foreach (var p in s.Payloads(events, secret, token))
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, s.RequestUrl(p)) { Content = new StringContent(p.Body, Encoding.UTF8, p.ContentType) };
            foreach (var (n, v) in p.Headers) req.Headers.TryAddWithoutValidation(n, v);
            req.Headers.TryAddWithoutValidation("User-Agent", LogEvent.App + "/0.4");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(RequestTimeout);
            HttpResponseMessage r;
            try { r = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException($"no answer from {s.Host} within {RequestTimeout.TotalSeconds:0} s"); }
            using (r)
            {
                var body = r.Content.Headers.ContentLength is > 65536 ? "" : await r.Content.ReadAsStringAsync(cts.Token);
                if (LogSink.ResponseProblem(s.Kind, (int)r.StatusCode, body) is { } why) throw new HttpRequestException(why);
            }
        }
        lock (Gate)
        {
            Roll();
            Minute[(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60) % 60] += events.Count;
            var st = Status.TryGetValue(s.Id, out var old) ? old : new SinkStatus(s.Id, s.Name, s.Kind, "idle", null, 0, 0, null, null);
            Status[s.Id] = st with { State = "connected", LastSent = DateTime.Now, Sent = st.Sent + events.Count, LastError = null };
        }
        Changed();
    }

    private static HttpClient Client(LogSink s)
    {
        var key = $"{s.Url}|{s.Pin}";
        lock (Gate)
        {
            if (Conns.TryGetValue(s.Id, out var c) && c.Key == key) return c.Http;
            if (c != null) { c.Http.Dispose(); Conns.Remove(s.Id); }
            HttpMessageHandler handler;
            if (HandlerForTest != null) handler = HandlerForTest(s);
            else
            {
                var pin = s.Pin.Replace(":", "").Trim();
                var inner = new SocketsHttpHandler
                {
                    PooledConnectionLifetime = TimeSpan.FromMinutes(10), AutomaticDecompression = System.Net.DecompressionMethods.None, UseCookies = false, UseProxy = false,
                    SslOptions = new SslClientAuthenticationOptions
                    {
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        RemoteCertificateValidationCallback = (_, cert, chain, errors) => Validate(pin, cert, chain, errors),
                        CertificateRevocationCheckMode = pin.Length > 0 ? X509RevocationMode.NoCheck : X509RevocationMode.Online,
                    },
                };
                handler = Offline.Handler(inner);
            }
            var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            Conns[s.Id] = new Conn(http, key);
            return http;
        }
    }

    /// <summary>Windows trust, or exactly the pinned certificate (the server's own or its CA's SHA-256).</summary>
    internal static bool Validate(string pin, X509Certificate? cert, X509Chain? chain, SslPolicyErrors errors)
    {
        if (cert == null) return false;
        if (pin.Length == 0) return errors == SslPolicyErrors.None;
        static string Sha(X509Certificate c) => Convert.ToHexString(SHA256.HashData(c.GetRawCertData()));
        if (Sha(cert).Equals(pin, StringComparison.OrdinalIgnoreCase)) return true;
        return (errors & SslPolicyErrors.RemoteCertificateNameMismatch) == 0
               && chain != null && chain.ChainElements.Cast<X509ChainElement>().Skip(1).Any(e => Sha(e.Certificate).Equals(pin, StringComparison.OrdinalIgnoreCase));
    }

    private static void Fail(LogSink s, Exception ex)
    {
        var why = (ex is AuthenticationException ? "TLS: " : "") + (ex.InnerException?.Message ?? ex.Message);
        bool wasOk;
        lock (Gate)
        {
            var st = Status.TryGetValue(s.Id, out var old) ? old : new SinkStatus(s.Id, s.Name, s.Kind, "idle", null, 0, 0, null, null);
            wasOk = st.State == "connected";
            Status[s.Id] = st with { State = "failing", LastError = Redaction.Mask(why), LastErrorAt = DateTime.Now };
        }
        Changed();
        if (wasOk) try { Failing?.Invoke(s.Name, why); } catch { }
    }

    private static void SaveSpool(bool force)
    {
        if (!_spoolDirty || !SecureStore.IsUnlocked || (!force && DateTime.UtcNow - _lastSpool < TimeSpan.FromSeconds(2))) return;
        List<Spooled> list;
        lock (Gate) { list = Queue.Select(i => new Spooled(i.Doc, i.Pending.ToArray())).ToList(); _spoolDirty = false; }
        try { SecureStore.Write(SpoolFile, list); _lastSpool = DateTime.UtcNow; } catch { _spoolDirty = true; }
    }

    private static void Changed() { try { StatusChanged?.Invoke(); } catch { } }

    /// <summary>Self-test: forget statuses, retries and connections (the queue is left alone).</summary>
    internal static void ResetForTest()
    {
        lock (Gate) { Status.Clear(); Retry.Clear(); foreach (var c in Conns.Values) c.Http.Dispose(); Conns.Clear(); Queue.Clear(); _dropped = 0; Array.Clear(Minute); }
    }
}
