using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DcxManager.Services;

namespace DcxManager.Notify;

/// <summary>What the Privacy &amp; network box shows about syslog.</summary>
public sealed record SyslogStatus(string State, string Server, string? Tls, string? Certificate, DateTime? CertExpires,
    DateTime? LastSent, int Queued, int Dropped, string? LastError, DateTime? LastErrorAt);

/// <summary>
/// Sends the audit log and the application log to a syslog collector over TLS only (0.3.0 §3): RFC 5424 messages,
/// RFC 5425 framing, TLS 1.2 or 1.3, the collector's certificate checked by Windows or against a SHA-256 pin, an optional
/// client certificate. There is no UDP or plain-TCP path at all. While the collector can't be reached, messages wait in
/// an encrypted spool in the store (at most <see cref="MaxQueue"/>; the oldest go first, and the number lost is sent once
/// the collector is back). Offline mode applies: a collector outside the allowed destinations is not contacted. Nothing
/// here writes to the app's own logs (that would loop): failures are kept in <see cref="Status"/>.
/// </summary>
public static class SyslogForwarder
{
    public const int MaxQueue = 5000;
    private const string SpoolFile = "syslog-spool.dcx";

    private static readonly object Gate = new();
    private static readonly LinkedList<byte[]> Queue = new();
    private static readonly SemaphoreSlim Signal = new(0, int.MaxValue);
    private static CancellationTokenSource? _cts;
    private static Task? _loop;
    private static SslStream? _ssl;
    private static TcpClient? _tcp;
    private static string? _connectedTo;
    private static int _dropped;
    private static DateTime _retryAt = DateTime.MinValue;
    private static TimeSpan _backoff = TimeSpan.FromSeconds(5);
    private static bool _spoolDirty;

    private static SyslogStatus _status = new("off", "", null, null, null, null, 0, 0, null, null);
    public static SyslogStatus Status { get { lock (Gate) return _status with { Queued = Queue.Count, Dropped = _dropped }; } }
    public static event Action? StatusChanged;
    /// <summary>The collector failed after it had worked, or keeps failing (the notifier mails about it, rate-limited).</summary>
    public static event Action<string>? Failing;

    private static AppSettings S => AppSettings.Current;
    private static string Server => $"{S.SyslogHost}:{S.SyslogPort}";

    /// <summary>Starts forwarding (after the store is unlocked). Safe to call again.</summary>
    public static void Start()
    {
        lock (Gate)
        {
            if (_loop != null) return;
            AuditLog.Added += OnAudit;
            Log.Written += OnAppLog;
            try { foreach (var b64 in SecureStore.Read<List<string>>(SpoolFile)) Queue.AddLast(Convert.FromBase64String(b64)); } catch { }
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
        Disconnect();
    }

    private static void OnAudit(AuditEntry e)
    {
        if (S.SyslogEnabled && S.SyslogSendAudit) Enqueue(SyslogMessage.Audit(e));
    }

    private static void OnAppLog(string where, string entry)
    {
        if (S.SyslogEnabled && S.SyslogSendAppLog) Enqueue(SyslogMessage.AppLog(DateTimeOffset.Now, where, entry));
    }

    public static void Enqueue(byte[] message)
    {
        lock (Gate)
        {
            Queue.AddLast(message);
            while (Queue.Count > MaxQueue) { Queue.RemoveFirst(); _dropped++; }
            _spoolDirty = true;
        }
        Signal.Release();
    }

    /// <summary>Sends one test message now (Privacy &amp; network → Send test) and says what happened.</summary>
    public static async Task<string> SendTestAsync(CancellationToken ct = default)
    {
        if (S.SyslogHost.Trim().Length == 0) return "No syslog server is set.";
        try
        {
            var msg = SyslogMessage.Build(DateTimeOffset.Now, SyslogMessage.Severity.Notice, "test", [("actor", Environment.UserName)], "Test message from DarkCipher ServerManagement");
            await SendAsync([msg], ct);
            return $"✓ Sent to {Server} over {Status.Tls}.";
        }
        catch (Exception ex) { Fail(ex); return "✕ " + Status.LastError; }
    }

    private static async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Signal.WaitAsync(TimeSpan.FromSeconds(5), ct); } catch (OperationCanceledException) { break; }
            SaveSpool(force: false);
            if (!S.SyslogEnabled || S.SyslogHost.Trim().Length == 0)
            {
                Disconnect();
                lock (Gate) { if (!S.SyslogEnabled) Queue.Clear(); _spoolDirty = true; }
                Set(_status with { State = S.SyslogEnabled ? "no server set" : "off", Server = S.SyslogEnabled ? "" : _status.Server });
                continue;
            }
            List<byte[]> batch;
            lock (Gate) { if (Queue.Count == 0 || DateTime.UtcNow < _retryAt) continue; batch = Queue.Take(200).ToList(); }
            var fromQueue = batch.ToList();
            try
            {
                if (_dropped > 0)
                {
                    int n; lock (Gate) { n = _dropped; _dropped = 0; }
                    batch.Insert(0, SyslogMessage.Build(DateTimeOffset.Now, SyslogMessage.Severity.Warning, "dropped", [], $"{n} message(s) were dropped while the collector was unreachable (spool limit {MaxQueue})."));
                }
                await SendAsync(batch, ct);
                lock (Gate) { foreach (var b in fromQueue) if (ReferenceEquals(Queue.First?.Value, b)) Queue.RemoveFirst(); _spoolDirty = true; }
                _backoff = TimeSpan.FromSeconds(5);
                if (Queue.Count > 0) Signal.Release();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Fail(ex);
                _retryAt = DateTime.UtcNow + _backoff;
                _backoff = TimeSpan.FromSeconds(Math.Min(300, _backoff.TotalSeconds * 2));
            }
        }
    }

    /// <summary>Connects if needed (same settings → same connection) and writes the framed messages.</summary>
    private static async Task SendAsync(IReadOnlyList<byte[]> messages, CancellationToken ct)
    {
        var host = S.SyslogHost.Trim();
        var target = $"{host}:{S.SyslogPort}|{S.SyslogPin}|{S.SyslogClientCert}";
        if (_ssl == null || _connectedTo != target)
        {
            Disconnect();
            if (Offline.On && !(await Offline.DecideAsync(host, ct)).Allowed)
            {
                Set(_status with { State = "blocked by offline mode", Server = Server });
                throw new InvalidOperationException($"offline mode: {host} isn't an allowed destination");
            }
            Set(_status with { State = "connecting", Server = Server });
            var tcp = new TcpClient { NoDelay = true };
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(TimeSpan.FromSeconds(10));
                try { await tcp.ConnectAsync(host, S.SyslogPort, cts.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { tcp.Dispose(); throw new TimeoutException($"no answer from {Server} within 10 s"); }
                catch { tcp.Dispose(); throw; }
            }
            var ssl = new SslStream(tcp.GetStream(), false);
            var opts = new SslClientAuthenticationOptions
            {
                TargetHost = host,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                RemoteCertificateValidationCallback = Validate,
                CertificateRevocationCheckMode = S.SyslogPin.Length > 0 ? X509RevocationMode.NoCheck : X509RevocationMode.Online,
            };
            if (ClientCertificate() is { } client) opts.ClientCertificates = new X509CertificateCollection { client };
            try { await ssl.AuthenticateAsClientAsync(opts, ct); }
            catch { ssl.Dispose(); tcp.Dispose(); throw; }
            var cert = ssl.RemoteCertificate is { } rc ? new X509Certificate2(rc) : null;
            _tcp = tcp; _ssl = ssl; _connectedTo = target;
            Set(_status with
            {
                State = "connected", Server = Server, Tls = ssl.SslProtocol switch { SslProtocols.Tls13 => "TLS 1.3", SslProtocols.Tls12 => "TLS 1.2", var p => p.ToString() },
                Certificate = cert?.GetNameInfo(X509NameType.SimpleName, false), CertExpires = cert?.NotAfter,
            });
        }
        foreach (var m in messages) await _ssl!.WriteAsync(SyslogMessage.Frame(m), ct);
        await _ssl!.FlushAsync(ct);
        Set(_status with { State = "connected", LastSent = DateTime.Now });
    }

    /// <summary>Windows trust, or exactly the pinned certificate (the collector's own or its CA's SHA-256).</summary>
    private static bool Validate(object sender, X509Certificate? cert, X509Chain? chain, SslPolicyErrors errors)
    {
        if (cert == null) return false;
        var pin = S.SyslogPin.Replace(":", "").Trim();
        if (pin.Length == 0) return errors == SslPolicyErrors.None;
        static string Sha(X509Certificate c) => Convert.ToHexString(SHA256.HashData(c.GetRawCertData()));
        if (Sha(cert).Equals(pin, StringComparison.OrdinalIgnoreCase)) return true;   // the collector's own certificate
        // A pinned CA: the chain must lead to it and the name must match.
        return (errors & SslPolicyErrors.RemoteCertificateNameMismatch) == 0
               && chain != null && chain.ChainElements.Cast<X509ChainElement>().Skip(1).Any(e => Sha(e.Certificate).Equals(pin, StringComparison.OrdinalIgnoreCase));
    }

    private static X509Certificate2? ClientCertificate()
    {
        var thumb = S.SyslogClientCert.Replace(" ", "").Trim();
        if (thumb.Length == 0) return null;
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumb, validOnly: false).OfType<X509Certificate2>().FirstOrDefault(c => c.HasPrivateKey);
        return found ?? throw new InvalidOperationException("the client certificate isn't in your certificate store (Personal), or has no private key");
    }

    private static void Fail(Exception ex)
    {
        Disconnect();
        var why = (ex is AuthenticationException ? "TLS: " : "") + (ex.InnerException?.Message ?? ex.Message);
        bool wasOk = _status.State == "connected";
        Set(_status with { State = Queue.Count > 0 ? "failing — queued" : "failing", LastError = Redaction.Mask(why), LastErrorAt = DateTime.Now });
        if (wasOk || _status.LastSent == null) try { Failing?.Invoke(why); } catch { }
    }

    private static void Disconnect()
    {
        try { _ssl?.Dispose(); } catch { }
        try { _tcp?.Dispose(); } catch { }
        _ssl = null; _tcp = null; _connectedTo = null;
    }

    private static DateTime _lastSpool;
    private static void SaveSpool(bool force)
    {
        if (!_spoolDirty || !SecureStore.IsUnlocked || (!force && DateTime.UtcNow - _lastSpool < TimeSpan.FromSeconds(2))) return;
        List<string> list;
        lock (Gate) { list = Queue.Select(Convert.ToBase64String).ToList(); _spoolDirty = false; }
        try { SecureStore.Write(SpoolFile, list); _lastSpool = DateTime.UtcNow; } catch { _spoolDirty = true; }
    }

    private static void Set(SyslogStatus s)
    {
        lock (Gate) _status = s;
        try { StatusChanged?.Invoke(); } catch { }
    }
}
