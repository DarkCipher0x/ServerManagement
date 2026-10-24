using System.Text.Json;
using DcxManager.Incidents;
using DcxManager.Services;

namespace DcxManager.Api;

/// <summary>
/// The local API's endpoints (ADR-011), run on the UI thread. Read inventory without secrets; add events to incidents;
/// create notes. Every request needs a Bearer capability token with the right scope; browser requests (Origin header)
/// are refused; failures are rate-limited and audited. Never returns credentials, keys, settings, terminals or files.
/// </summary>
public static class LocalApi
{
    private static readonly RateLimiter PerToken = new(10, 30);
    private static readonly Queue<DateTime> Failures = new();
    private static DateTime _lockedUntil;

    public const int MaxTitle = 200, MaxText = 100_000;

    /// <summary>The endpoint list shown in Settings (and used by the self-test).</summary>
    public static readonly (string Method, string Path, string Scope, string Text)[] Endpoints =
    {
        ("GET", "/api/v1/status", "status.read", "App version and the active incident"),
        ("GET", "/api/v1/hosts", "hosts.read", "Saved hosts (no credentials)"),
        ("GET", "/api/v1/incidents", "incidents.read", "Incidents (?status=open for open/monitoring only)"),
        ("GET", "/api/v1/incidents/{id}", "incidents.read", "One incident with its timeline (attachments listed, not served)"),
        ("POST", "/api/v1/incidents/{id|active}/events", "incidents.write", "Add an event: {\"title\":\"…\",\"text\":\"…\",\"kind\":\"event|log\"}"),
        ("POST", "/api/v1/notes", "notes.write", "Create a note {\"title\",\"body\",\"host\"}; with \"append\":true appends to the host's log note"),
    };

    internal static void ResetLimits() { lock (Failures) { Failures.Clear(); _lockedUntil = default; } }

    public static HttpResp Handle(HttpReq r)
    {
        if (r.Header("origin") != null || r.Header("sec-fetch-site") is { } sfs && sfs != "none")
            return HttpResp.Error(403, "browser requests are not accepted by the local API");
        lock (Failures)
        {
            if (DateTime.UtcNow < _lockedUntil) return HttpResp.Error(429, "too many failed authentications — try again later", new() { ["Retry-After"] = "60" });
        }
        var auth = r.Header("authorization") ?? "";
        var token = auth.StartsWith("Bearer ", StringComparison.Ordinal) ? ApiTokens.Find(auth[7..].Trim()) : null;
        if (token == null) { Failed(); return HttpResp.Error(401, "missing, unknown, expired or revoked token", new() { ["WWW-Authenticate"] = "Bearer" }); }
        if (!PerToken.Allow(token.Id)) return HttpResp.Error(429, "rate limit: 10 requests per second", new() { ["Retry-After"] = "1" });
        ApiTokens.Touch(token);

        var seg = r.Path.TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries);   // api, v1, …
        if (seg.Length < 3 || seg[1] != "v1") return HttpResp.Error(404, "unknown endpoint");
        try
        {
            return (r.Method, seg[2], seg.Length) switch
            {
                ("GET", "status", 3) => Need(token, "status.read") ?? Status(),
                ("GET", "hosts", 3) => Need(token, "hosts.read") ?? Category("hosts") ?? Hosts(),
                ("GET", "incidents", 3) => Need(token, "incidents.read") ?? Category("incidents") ?? IncidentList(r.Query),
                ("GET", "incidents", 4) => Need(token, "incidents.read") ?? Category("incidents") ?? IncidentOne(seg[3]),
                ("POST", "incidents", 5) when seg[4] == "events" => Need(token, "incidents.write") ?? Category("incidents") ?? AddEvent(token, seg[3], r),
                ("POST", "notes", 3) => Need(token, "notes.write") ?? Category("notes") ?? AddNote(token, r),
                _ => HttpResp.Error(404, "unknown endpoint"),
            };
        }
        catch (Exception ex) { Log.Write("Local API handler", ex); return HttpResp.Error(500, "internal error"); }
    }

    private static void Failed()
    {
        lock (Failures)
        {
            var now = DateTime.UtcNow;
            Failures.Enqueue(now);
            while (Failures.Count > 0 && now - Failures.Peek() > TimeSpan.FromMinutes(1)) Failures.Dequeue();
            if (Failures.Count > 20)
            {
                _lockedUntil = now.AddSeconds(60);
                Failures.Clear();
                AuditLog.Write("api", "api", "local API locked for 60 s after repeated failed authentications");
            }
        }
    }

    private static HttpResp? Need(ApiToken t, string scope) => t.Scopes.Contains(scope) ? null : HttpResp.Error(403, $"this token lacks the scope {scope}");

    /// <summary>0.5.1 #9: a locked category is closed to the local API (open or not: there is no one to approve it).</summary>
    private static HttpResp? Category(string key) => Locks.IsCategoryLocked(key) ? HttpResp.Error(403, $"{Locks.FindCategory(key)?.Title ?? key} is locked in the app") : null;

    private static HttpResp Status() => HttpResp.Json(200, new
    {
        app = AppInfo.ProductName, version = AppInfo.Version,
        activeIncident = IncidentStore.Active is { } a ? new { a.Id, a.Title, status = a.Status, severity = a.Severity } : null,
    });

    // 0.5.1 #10: a padlocked host is listed by name only.
    private static HttpResp Hosts() => HttpResp.Json(200, AppData.Current.Hosts.Where(h => !h.IsAdHoc).Select(h => Locks.IsLocked("host:" + h.Id) ? (object)new { h.Id, name = h.Display, locked = true } : new
    {
        h.Id, name = h.Display, h.Address, h.Port, user = h.Username, folder = h.Group, protocol = h.Protocol, environment = Envs.For(h),
    }));

    private static object Summary(Incident i) => new { i.Id, i.Title, status = i.Status, severity = i.Severity, environment = i.Environment, i.Created, i.Updated, i.Resolved, entries = i.Items.Count, hosts = i.HostIds };

    private static HttpResp IncidentList(string query)
    {
        bool open = query.Split('&').Contains("status=open");
        return HttpResp.Json(200, IncidentStore.All.Where(i => !open || i.IsActive).Select(Summary));
    }

    private static HttpResp IncidentOne(string id)
    {
        if (!IncidentStore.ValidId(id) || IncidentStore.Find(id) is not { Deleted: null } i) return HttpResp.Error(404, "no such incident");
        return HttpResp.Json(200, new
        {
            incident = Summary(i), i.Summary,
            timeline = i.Items.OrderBy(x => x.At).Select(x => new { x.Id, x.At, kind = x.Kind, x.Title, text = x.AttachmentId != null ? null : x.Text, attachment = x.AttachmentId != null ? new { x.Mime, x.Bytes } : null, x.HostId }),
        });
    }

    private static JsonElement? Body(HttpReq r, out HttpResp? error)
    {
        error = null;
        if (r.Header("content-type") is not { } ct || !ct.StartsWith("application/json", StringComparison.OrdinalIgnoreCase)) { error = HttpResp.Error(415, "Content-Type must be application/json"); return null; }
        try
        {
            using var doc = JsonDocument.Parse(r.Body, new JsonDocumentOptions { MaxDepth = 4 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) { error = HttpResp.Error(400, "expected a JSON object"); return null; }
            return doc.RootElement.Clone();
        }
        catch (JsonException) { error = HttpResp.Error(400, "invalid JSON"); return null; }
    }

    private static string? Str(JsonElement e, string name, int max, out HttpResp? error)
    {
        error = null;
        if (!e.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind != JsonValueKind.String) { error = HttpResp.Error(400, $"\"{name}\" must be a string"); return null; }
        var s = v.GetString()!;
        if (s.Length > max) { error = HttpResp.Error(400, $"\"{name}\" is longer than {max:N0} characters"); return null; }
        // Control characters (other than newlines/tabs) have no business in a title or note.
        return new string(s.Where(c => !char.IsControl(c) || c is '\n' or '\t').ToArray());
    }

    private static HttpResp AddEvent(ApiToken t, string which, HttpReq r)
    {
        var inc = which == "active" ? IncidentStore.Active : IncidentStore.ValidId(which) ? IncidentStore.Find(which) : null;
        if (inc is not { Deleted: null }) return HttpResp.Error(404, which == "active" ? "no active incident" : "no such incident");
        if (Body(r, out var err) is not { } b) return err!;
        var title = Str(b, "title", MaxTitle, out err); if (err != null) return err;
        var text = Str(b, "text", MaxText, out err) ?? ""; if (err != null) return err;
        var kind = Str(b, "kind", 10, out err) ?? "event"; if (err != null) return err;
        if (string.IsNullOrWhiteSpace(title)) return HttpResp.Error(400, "\"title\" is required");
        if (kind is not ("event" or "log")) return HttpResp.Error(400, "\"kind\" must be event or log");
        var item = IncidentStore.Add(inc, new IncidentItem { Kind = kind == "log" ? ItemKind.Log : ItemKind.Event, Title = $"{title.Trim()} (API: {t.Name})", Text = text });
        AuditLog.Write("api:" + t.Name, "incidents", "event added via local API", inc.Title);
        return HttpResp.Json(201, new { item.Id, incident = inc.Id });
    }

    private static HttpResp AddNote(ApiToken t, HttpReq r)
    {
        if (Body(r, out var err) is not { } b) return err!;
        var title = Str(b, "title", MaxTitle, out err); if (err != null) return err;
        var body = Str(b, "body", MaxText, out err) ?? ""; if (err != null) return err;
        var hostRef = Str(b, "host", 200, out err); if (err != null) return err;
        bool append = b.TryGetProperty("append", out var ap) && ap.ValueKind == JsonValueKind.True;
        HostEntry? host = null;
        if (hostRef != null && (host = AppData.Current.FindHost(hostRef) ?? AppData.Current.Hosts.FirstOrDefault(h => h.Display.Equals(hostRef, StringComparison.OrdinalIgnoreCase))) == null)
            return HttpResp.Error(404, "no such host");
        if (host != null && Locks.IsLocked("host:" + host.Id)) return HttpResp.Error(403, "that host is locked in the app");
        if (append && host != null && Notes.NoteStore.All.FirstOrDefault(x => x.Tags.Contains("log") && x.HostIds.Contains(host.Id)) is { } log && Locks.IsLocked("note:" + log.Id))
            return HttpResp.Error(403, "that host's log note is locked in the app");
        Notes.Note n;
        if (append)
        {
            if (host == null) return HttpResp.Error(400, "\"append\" needs \"host\"");
            n = Notes.NoteStore.AppendToLog(host.Display, host.Id, null, $"(API: {t.Name}) " + (title != null ? title + "\n" : "") + body);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(title)) return HttpResp.Error(400, "\"title\" is required");
            n = Notes.NoteStore.Create(title, host != null ? new[] { host.Id } : null, body);
            n.Tags.Add("api"); Notes.NoteStore.Save(n);
        }
        AuditLog.Write("api:" + t.Name, "notes", append ? "note appended via local API" : "note created via local API", n.Title);
        return HttpResp.Json(201, new { n.Id, n.Title });
    }
}
