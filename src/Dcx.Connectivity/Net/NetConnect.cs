using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace DcxManager.Net;

/// <summary>
/// The connection tool (0.3.0 §7, ncat-style): connect to host:port over TCP or TLS and talk to it line by line — check a
/// banner, speak SMTP or HTTP by hand, see a certificate. Client only: there is no listening mode (no firewall prompts,
/// nothing exposed). TLS checks the certificate like everything else unless "inspect only" is chosen, in which case the
/// certificate is shown and the session is marked untrusted.
/// </summary>
public sealed class NetConnect : IAsyncDisposable
{
    private readonly TcpClient _tcp = new() { NoDelay = true };
    private Stream? _stream;
    private readonly CancellationTokenSource _cts = new();

    public event Action<string>? Received;
    public event Action<string>? Closed;
    public string Describe { get; private set; } = "";

    public static async Task<NetConnect> OpenAsync(string host, int port, bool tls, bool inspectOnly, CancellationToken ct)
    {
        Services.Offline.EnforceStatic(host, "a connection test");
        await Services.Offline.EnforceAsync(host, "a connection test", ct);
        var c = new NetConnect();
        using (var t = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            t.CancelAfter(TimeSpan.FromSeconds(10));
            await c._tcp.ConnectAsync(host, port, t.Token);
        }
        c.Describe = $"connected to {host}:{port} (TCP)";
        Stream s = c._tcp.GetStream();
        if (tls)
        {
            X509Certificate2? seen = null; SslPolicyErrors errors = SslPolicyErrors.None;
#pragma warning disable CA5359 // "inspect only" is the user's explicit choice; the session is then labelled untrusted
            var ssl = new SslStream(s, false, (_, cert, _, e) => { if (cert != null) seen = new X509Certificate2(cert); errors = e; return inspectOnly || e == SslPolicyErrors.None; });
#pragma warning restore CA5359
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, ct);
            s = ssl;
            c.Describe = $"connected to {host}:{port} over {ssl.SslProtocol}" + (seen != null ? $" · certificate {seen.GetNameInfo(X509NameType.SimpleName, false)}, expires {seen.NotAfter:yyyy-MM-dd}" : "") +
                         (errors != SslPolicyErrors.None ? $" · ⚠ UNTRUSTED ({errors})" : " · trusted");
        }
        c._stream = s;
        _ = c.ReadLoopAsync();
        return c;
    }

    private async Task ReadLoopAsync()
    {
        var buf = new byte[8192];
        try
        {
            while (true)
            {
                var n = await _stream!.ReadAsync(buf, _cts.Token);
                if (n == 0) { Closed?.Invoke("the other side closed the connection"); return; }
                Received?.Invoke(Encoding.UTF8.GetString(buf, 0, n));
            }
        }
        catch (OperationCanceledException) { Closed?.Invoke("closed"); }
        catch (Exception ex) { Closed?.Invoke(ex.Message); }
    }

    /// <summary>Sends a line with the chosen ending ("\r\n" for most protocols).</summary>
    public async Task SendLineAsync(string line, string ending = "\r\n")
    {
        var b = Encoding.UTF8.GetBytes(line + ending);
        await _stream!.WriteAsync(b, _cts.Token);
        await _stream.FlushAsync(_cts.Token);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { if (_stream != null) await _stream.DisposeAsync(); } catch { }
        _tcp.Dispose();
    }
}
