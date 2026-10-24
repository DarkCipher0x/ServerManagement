using System.Diagnostics;
using System.IO;
using System.Net.Http;
using DcxManager.Services;

namespace DcxManager.Cloud;

/// <summary>Progress of a long-running provider operation (e.g. a Hetzner action at 40 %), for whichever screen started
/// it. Text only: never request bodies or secrets.</summary>
public static class CloudProgress
{
    public static event Action<string, string>? Changed;
    public static void Report(string provider, string text) => Changed?.Invoke(provider, text);
}

/// <summary>An answered request: status, body text, how many retries it took and the provider's request id.</summary>
public sealed record CloudResponse(int Status, string Text, int Retries, long ElapsedMs, string? RequestId, System.Net.Http.Headers.HttpResponseHeaders? Headers = null)
{
    public bool Ok => Status is >= 200 and < 300;
}

/// <summary>
/// The request runner every cloud client uses (ADR-020): a finite timeout per attempt, retries only where repeating is
/// safe, and answers or sanitised <see cref="CloudApiException"/>s — never a hang, never a duplicate order.
/// <list type="bullet">
/// <item>429 (the server did not process it): retried for reads and idempotent changes, honouring Retry-After.</item>
/// <item>502 / 503 / 504, network errors, timeouts: retried for reads only.</item>
/// <item>A change that timed out is reported as uncertain ("refresh before trying again"), never repeated.</item>
/// </list>
/// </summary>
public static class CloudHttp
{
    public const int MaxAttempts = 4;

    /// <summary>Test seam: how retries wait (the self-test makes it instant and counts the waits).</summary>
    internal static Func<TimeSpan, CancellationToken, Task> Sleep = Task.Delay;

    /// <summary>The delay before the next attempt, or null to stop. status 0 = no answer (network or timeout).</summary>
    public static TimeSpan? RetryDelay(HttpMethod method, int status, int attempt, bool idempotent, TimeSpan? retryAfter = null)
    {
        if (attempt >= MaxAttempts - 1) return null;
        bool read = method == HttpMethod.Get || method == HttpMethod.Head;
        if (status == 429 && (read || idempotent))
            return retryAfter is { } ra ? TimeSpan.FromSeconds(Math.Clamp(ra.TotalSeconds, 1, 30)) : Backoff(attempt);
        if (status is 0 or 502 or 503 or 504 && read) return Backoff(attempt);
        return null;
    }

    private static TimeSpan Backoff(int attempt) => TimeSpan.FromMilliseconds(Math.Min(8000, 500 * (1 << attempt)) + Random.Shared.Next(0, 250));

    /// <summary>Sends with retries. build() makes a fresh request per attempt (new request id, current token).</summary>
    public static async Task<CloudResponse> SendAsync(HttpClient http, Func<HttpRequestMessage> build, HttpMethod method, bool idempotent,
        TimeSpan timeout, string provider, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        bool mutating = method != HttpMethod.Get && method != HttpMethod.Head;
        for (int attempt = 0; ; attempt++)
        {
            using var req = build();
            var requestId = req.Headers.TryGetValues("x-request-id", out var ids) ? ids.FirstOrDefault() : null;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            int status; string text; System.Net.Http.Headers.HttpResponseHeaders? headers = null; TimeSpan? retryAfter = null;
            try
            {
                using var resp = await http.SendAsync(req, cts.Token);
                text = await resp.Content.ReadAsStringAsync(cts.Token);
                status = (int)resp.StatusCode;
                headers = resp.Headers;
                retryAfter = resp.Headers.RetryAfter?.Delta ?? (resp.Headers.RetryAfter?.Date is { } d ? (TimeSpan?)(d - DateTimeOffset.UtcNow) : null) ?? RateLimitReset(resp.Headers);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                if (RetryDelay(method, 0, attempt, idempotent) is { } wait) { await Sleep(wait, ct); continue; }
                throw new CloudApiException(provider, 0, mutating
                    ? $"{provider} didn't answer within {timeout.TotalSeconds:0} s. The change may or may not have been applied — refresh to check before trying again."
                    : $"{provider} didn't answer within {timeout.TotalSeconds:0} s.", requestId, uncertain: mutating);
            }
            catch (HttpRequestException ex) when (ex.InnerException is OfflineBlockedException ob)
            {
                throw ob;   // offline mode refused it before any connection: no retry, the reason as it is (ADR-016)
            }
            catch (HttpRequestException ex)
            {
                if (RetryDelay(method, 0, attempt, idempotent) is { } wait) { await Sleep(wait, ct); continue; }
                // The exception names the host and the socket error only (never headers or bodies).
                throw new CloudApiException(provider, 0, $"Couldn't reach {provider}: {FirstLine(ex.InnerException?.Message ?? ex.Message)}", requestId, uncertain: mutating && ex.InnerException is IOException);
            }
            if (status is 429 or 502 or 503 or 504 && RetryDelay(method, status, attempt, idempotent, retryAfter) is { } delay)
            {
                await Sleep(delay, ct);
                continue;
            }
            return new CloudResponse(status, text, attempt, sw.ElapsedMilliseconds, requestId, headers);
        }
    }

    /// <summary>"RateLimit-Reset: &lt;UNIX time&gt;" (Hetzner) as a wait, when a provider sends no Retry-After. Clamped by
    /// RetryDelay like any other hint.</summary>
    internal static TimeSpan? RateLimitReset(System.Net.Http.Headers.HttpResponseHeaders h) =>
        h.TryGetValues("RateLimit-Reset", out var v) && long.TryParse(v.FirstOrDefault(), out var unix) && unix > 1_000_000_000
            ? DateTimeOffset.FromUnixTimeSeconds(unix) - DateTimeOffset.UtcNow : null;

    public static string FirstLine(string s) { var l = s.Split('\n')[0].Trim(); return l.Length > 300 ? l[..300] + "…" : l; }

    /// <summary>Structured record of a call: changes go to the audit log (who, account, call, result, time, retries,
    /// request id); failures of any call go to the error log. Never headers, bodies or credentials.</summary>
    public static void Record(string provider, string account, HttpMethod method, string path, CloudResponse? r, string actor, string? error = null, string? requestId = null)
    {
        bool mutating = method != HttpMethod.Get && method != HttpMethod.Head;
        var where = $"{provider.ToLowerInvariant()}:{account}";
        var call = $"{method} {StripQuery(path)}";
        var rid = r?.RequestId ?? requestId;
        var detail = (r != null ? $"{r.Status} · {r.ElapsedMs} ms" + (r.Retries > 0 ? $" · {r.Retries} retr{(r.Retries == 1 ? "y" : "ies")}" : "") : "no answer")
                     + (rid != null ? " · request " + rid : "");
        if (mutating) AuditLog.Write(actor, where, call, error == null ? detail : $"failed: {error} ({detail})");
        if (error != null) Log.Write($"{provider} API {call}", new Exception($"{error} ({detail})"));
    }

    /// <summary>Query strings can carry search text; the logs keep the path only.</summary>
    public static string StripQuery(string path) { int q = path.IndexOf('?'); return q < 0 ? path : path[..q]; }
}
