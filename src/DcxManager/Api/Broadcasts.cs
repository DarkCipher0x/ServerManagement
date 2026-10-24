using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using DcxManager.Incidents;
using DcxManager.Services;
using DcxManager.Views;

namespace DcxManager.Api;

public interface IBroadcastSource
{
    string Title { get; }
    bool Alive { get; }
    /// <summary>The object this broadcast belongs to (a TerminalView, an incident id) — for "stop for this".</summary>
    object Owner { get; }
    Task<ScreenText?> CaptureAsync();
}

public sealed class Broadcast
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    internal string Token { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Title => Source.Title;
    public DateTime Started { get; } = DateTime.Now;
    public DateTime Expires { get; init; }
    public BroadcastOptions Options { get; init; } = new();
    internal IBroadcastSource Source { get; init; } = null!;
    internal Regex[] Custom { get; init; } = Array.Empty<Regex>();
    public bool Hidden { get; internal set; }
    public long Seq { get; internal set; }
    internal string[] Lines = Array.Empty<string>();
    public DateTime LastViewed { get; internal set; }
    public string Url => $"{LocalServer.BaseUrl}/b/{Token}/";
}

/// <summary>
/// OBS broadcasts (ADR-011): an explicit source, its own unguessable URL, an expiry, a visible BROADCASTING state,
/// stop / stop all, and text-only frames that are redacted before they leave the source's view. Frames are captured
/// on the UI thread every 400 ms and served from memory to the OBS Browser Source.
/// </summary>
public static class Broadcasts
{
    private static readonly List<Broadcast> Active = new();
    private static readonly RateLimiter PerBroadcast = new(10, 20);
    private static IDisposable? _timer;
    private static bool _ticking;

    public static event Action? Changed;
    public static bool Any { get { lock (Active) return Active.Count > 0; } }
    public static IReadOnlyList<Broadcast> All { get { lock (Active) return Active.ToList(); } }

    /// <summary>Starts a broadcast. Throws with a user-facing message when it can't (bad pattern, listener failed).</summary>
    public static Broadcast Start(IBroadcastSource source, string kind, BroadcastOptions o)
    {
        if (AppLock.IsLocked) throw new InvalidOperationException("The app is locked.");
        var custom = new List<Regex>();
        foreach (var p in o.Patterns.Where(p => p.Trim().Length > 0))
        {
            try { custom.Add(new Regex(p, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50))); }
            catch (ArgumentException ex) { throw new InvalidOperationException($"The redaction pattern “{p}” is not a valid regular expression: {ex.Message}"); }
        }
        o.FontPx = Math.Clamp(o.FontPx, 8, 72);
        o.Rows = Math.Clamp(o.Rows, 0, 200);
        o.Minutes = Math.Clamp(o.Minutes, 5, 24 * 60);
        if (o.Theme is not ("dark" or "light" or "transparent")) o.Theme = "dark";
        var b = new Broadcast
        {
            Token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_'),
            Kind = kind, Source = source, Options = o, Custom = custom.ToArray(), Expires = DateTime.Now.AddMinutes(o.Minutes),
        };
        lock (Active) Active.Add(b);
        LocalServer.Sync();
        if (!LocalServer.Running)
        {
            lock (Active) Active.Remove(b);
            LocalServer.Sync();
            throw new InvalidOperationException("The local listener could not start: " + (LocalServer.LastError ?? "unknown error"));
        }
        _timer ??= Ui.Thread.Every(TimeSpan.FromMilliseconds(400), async () => await TickAsync());
        AuditLog.Write("you", "broadcast", "broadcast started", $"{kind} “{source.Title}” · until {b.Expires:HH:mm} · masks: secrets{(o.MaskIps ? ", IPs" : "")}{(o.MaskEmails ? ", e-mail" : "")}{(custom.Count > 0 ? $", {custom.Count} custom" : "")}");
        _ = TickAsync();
        Changed?.Invoke();
        return b;
    }

    public static void Stop(Broadcast b, string reason)
    {
        bool removed;
        lock (Active) removed = Active.Remove(b);
        if (!removed) return;
        AuditLog.Write("you", "broadcast", "broadcast stopped", $"“{b.Title}” · {reason}");
        AfterChange();
    }

    public static int StopAll(string reason)
    {
        List<Broadcast> all;
        lock (Active) { all = Active.ToList(); Active.Clear(); }
        foreach (var b in all) AuditLog.Write("you", "broadcast", "broadcast stopped", $"“{b.Title}” · {reason}");
        if (all.Count > 0) AfterChange();
        return all.Count;
    }

    public static void StopFor(object owner, string reason) { foreach (var b in All.Where(b => Equals(b.Source.Owner, owner))) Stop(b, reason); }

    public static Broadcast? For(object owner) => All.FirstOrDefault(b => Equals(b.Source.Owner, owner));

    private static void AfterChange()
    {
        if (!Any) { _timer?.Dispose(); _timer = null; }
        LocalServer.Sync();
        Changed?.Invoke();
    }

    /// <summary>Captures and redacts every active broadcast's frame (UI thread).</summary>
    internal static async Task TickAsync()
    {
        if (_ticking) return;
        _ticking = true;
        try
        {
            if (AppLock.IsLocked) { StopAll("app locked"); return; }
            foreach (var b in All)
            {
                if (DateTime.Now >= b.Expires) { Stop(b, "expired"); continue; }
                if (!b.Source.Alive) { Stop(b, "source closed"); continue; }
                ScreenText? s;
                try { s = await b.Source.CaptureAsync(); } catch (Exception ex) { Log.Write("Broadcast capture", ex); continue; }
                if (s == null) continue;
                var (lines, hidden) = BroadcastRedactor.Apply(s, b.Options, b.Custom);
                bool wasHidden = b.Hidden;
                lock (b)
                {
                    b.Hidden = hidden;
                    // While a password prompt is on screen the last safe frame stays up.
                    if (!hidden && !lines.SequenceEqual(b.Lines)) { b.Lines = lines; b.Seq++; }
                }
                if (wasHidden != hidden) Changed?.Invoke();
            }
        }
        finally { _ticking = false; }
    }

    // ---- HTTP (any thread) ---------------------------------------------------------------------------------------------

    internal static HttpResp Handle(HttpReq r, string host)
    {
        var seg = r.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);   // b, token, [file]
        if (seg.Length is < 2 or > 3 || r.Method != "GET") return HttpResp.Error(404, "not found");
        var b = FindToken(seg[1]);
        if (b == null) return HttpResp.Error(404, "no such broadcast (it may have ended)");
        var origin = r.Header("origin");
        if (origin != null && origin != $"http://{host}") return HttpResp.Error(403, "cross-origin requests are not accepted");
        if (!PerBroadcast.Allow(b.Id)) return HttpResp.Error(429, "too many requests");
        if (seg.Length == 2)
            return r.Path.EndsWith('/') ? Page() : HttpResp.Text(308, "", extra: new() { ["Location"] = $"/b/{seg[1]}/" });
        switch (seg[2])
        {
            case "app.js": return HttpResp.Text(200, Script, "text/javascript; charset=utf-8");
            case "frame":
                lock (b)
                {
                    b.LastViewed = DateTime.Now;
                    return HttpResp.Json(200, new { seq = b.Seq, title = b.Title, lines = b.Lines, hidden = b.Hidden, theme = b.Options.Theme, fontPx = b.Options.FontPx, showTitle = b.Options.ShowTitle });
                }
            default: return HttpResp.Error(404, "not found");
        }
    }

    private static Broadcast? FindToken(string token)
    {
        var t = Encoding.UTF8.GetBytes(token);
        Broadcast? hit = null;
        foreach (var b in All)
        {
            var bt = Encoding.UTF8.GetBytes(b.Token);
            if (bt.Length == t.Length && CryptographicOperations.FixedTimeEquals(bt, t)) hit = b;
        }
        return hit;
    }

    private const string Csp = "default-src 'none'; script-src 'self'; connect-src 'self'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    private static HttpResp Page() => HttpResp.Text(200, """
        <!doctype html><html><head><meta charset="utf-8"><title>DarkCipher broadcast</title>
        <style>
          html,body{margin:0;height:100%;overflow:hidden;font-family:'Cascadia Mono',Consolas,monospace}
          body.theme-dark{background:#050909;color:#bcd8d5} body.theme-light{background:#f1f6f6;color:#08191a}
          body.theme-transparent{background:transparent;color:#fff;text-shadow:0 0 3px #000,0 0 6px #000}
          #bar{padding:6px 10px;font-size:14px;opacity:.75;border-bottom:1px solid rgba(127,127,127,.35)}
          #t{margin:0;padding:8px 10px;white-space:pre;line-height:1.25}
          #hid{position:fixed;right:12px;bottom:12px;padding:6px 10px;background:#b3202e;color:#fff;font-size:14px;border-radius:3px}
          #end{position:fixed;inset:0;display:flex;align-items:center;justify-content:center;font-size:20px;opacity:.8}
        </style></head>
        <body class="theme-dark"><div id="bar" hidden></div><pre id="t"></pre><div id="hid" hidden>⏸ input hidden</div><div id="end" hidden>Broadcast ended</div>
        <script src="app.js"></script></body></html>
        """, "text/html; charset=utf-8", new() { ["Content-Security-Policy"] = Csp });

    private const string Script = """
        const t = document.getElementById('t'), bar = document.getElementById('bar'), hid = document.getElementById('hid'), end = document.getElementById('end');
        let seq = -1;
        async function tick() {
          try {
            const r = await fetch('frame', { cache: 'no-store' });
            if (r.status === 404) { t.textContent = ''; bar.hidden = true; hid.hidden = true; end.hidden = false; return; }
            if (r.ok) {
              const f = await r.json();
              document.body.className = 'theme-' + f.theme;
              t.style.fontSize = f.fontPx + 'px';
              bar.hidden = !f.showTitle; bar.textContent = f.showTitle ? f.title : '';
              hid.hidden = !f.hidden;
              if (f.seq !== seq) { seq = f.seq; t.textContent = f.lines.join('\n'); }
            }
          } catch (e) { }
          setTimeout(tick, 400);
        }
        tick();
        """;
}

/// <summary>A terminal tab as a broadcast source: its on-screen rows.</summary>
public sealed class TerminalSource : IBroadcastSource
{
    private readonly TerminalView _view;
    public TerminalSource(TerminalView v) { _view = v; }
    public string Title => _view.Target.Title;
    public bool Alive => !_view.IsDisposed;
    public object Owner => _view;
    public Task<ScreenText?> CaptureAsync() => _view.CaptureScreenAsync();
}

/// <summary>An incident status card: title, status, severity, duration and the latest entry TITLES (never their text).</summary>
public sealed class IncidentSource : IBroadcastSource
{
    private readonly string _id;
    public IncidentSource(Incident i) { _id = i.Id; }
    public string Title => IncidentStore.Find(_id)?.Title ?? "incident";
    public bool Alive => IncidentStore.Find(_id) is { Deleted: null };
    public object Owner => _id;

    public Task<ScreenText?> CaptureAsync()
    {
        if (IncidentStore.Find(_id) is not { } i) return Task.FromResult<ScreenText?>(null);
        var lines = new List<string>
        {
            $"⚑ {i.Title}",
            $"{i.Status.ToString().ToUpperInvariant()} · {IncidentStore.SeverityText(i.Severity)}" + (i.Environment.Length > 0 ? " · " + i.Environment : ""),
            $"{(i.Resolved != null ? "took" : "open for")} {IncidentStore.Duration((i.Resolved ?? DateTime.Now) - i.Created)} · {i.Items.Count} entries",
            "",
            "Latest:",
        };
        lines.AddRange(i.Items.OrderByDescending(x => x.At).Take(8).Select(x => $"  {x.At:HH:mm}  {IncidentStore.KindText(x.Kind)} — {x.Title}"));
        return Task.FromResult<ScreenText?>(new ScreenText(lines.ToArray(), -1));
    }
}
