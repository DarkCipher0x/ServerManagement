using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using CipherChat.Crypto;
using CipherChat.Engine;
using DcxManager.Services;
using DcxManager.Views;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Room = CipherChat.Engine.Room;

namespace DcxManager.Chat.Calls;

/// <summary>
/// A voice or video call (0.6.38, ADR-117), as CipherChat's own Windows app makes it (its docs/CALLS.md): a window whose
/// browser runs CipherChat's call page (<see cref="CallPage"/>, served from memory). The page can't fetch anything and
/// can ask the app only for the operations in <see cref="Dispatch"/>: signing, verifying and key wrapping happen in
/// CipherChat's engine (Calls.cs) with the identity — the private keys never enter the page — and signals travel over
/// the chat's PQ-only TLS connection. The camera and microphone are allowed for that page only; media is end-to-end
/// encrypted frame by frame by the page. One call at a time; calls follow ChatAllowCalls (Group Policy).
/// </summary>
public sealed class CallWindow : Window
{
    private static CallWindow? _open;

    private readonly ChatSession _session;
    private readonly ChatEngine _e;
    private readonly WebView2 _web = new();
    private readonly Func<CallWindow, Task> _first;
    private bool _ended;
    // 0.6.39 live session sharing: the shared tab's window (its title put back when the call ends) and whether the
    // screen share has been started.
    private IDisposable? _share;
    private bool _shareStarted;

    /// <summary>The call shares a tab live (view-only): the page captures only the window titled <see cref="LiveShare.WindowTitle"/>.</summary>
    internal bool Sharing => _share is not null;
    internal bool ShareStartedForTest => _shareStarted;

    /// <summary>The window of the current call, if there is one.</summary>
    public static CallWindow? Current => _open;

    /// <summary>The self-test's live run against a local CipherChat server only: calls are made, the window opens off the
    /// screen and the page uses CipherChat's drawn test camera and tone instead of real devices.</summary>
    internal static bool LiveTest { get; set; }

    /// <summary>The live run: the page's notices ("… joined the call.") and the browser, to look at the remote video.</summary>
    internal readonly List<string> NoticesForTest = new();
    internal CoreWebView2? CoreForTest => _web.CoreWebView2;
    internal string[] WordsForTest { get; private set; } = [];

    private CallWindow(ChatSession session, string title, Func<CallWindow, Task> first)
    {
        _session = session;
        _e = session.Engine ?? throw new InvalidOperationException("Unlock the chat first.");
        _first = first;
        Title = title + " — ServerManagement";
        Width = 1180; Height = 760; MinWidth = 520; MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (TestMode.Running)   // the live run only (WhyNot refuses every other self-test call): never on the screen
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -12000; Top = -12000; ShowInTaskbar = false; ShowActivated = false;
        }
        SetResourceReference(BackgroundProperty, "Bg");
        Icon = Application.Current?.MainWindow?.Icon;
        Content = _web;
        _e.Calls.Signals += OnSignals;
        Loaded += async (_, _) => await InitAsync();
        Closed += (_, _) =>
        {
            _e.Calls.Signals -= OnSignals;
            _e.Calls.Closed();
            if (_open == this) _open = null;
            if (_share is { } sh) { _share = null; try { sh.Dispose(); } catch (Exception ex) { Log.Write("Live share end", ex); } AuditLog.Write("you", "chat", "live session sharing ended"); }
            try { _web.Dispose(); } catch (Exception ex) { Log.Write("Call window close", ex); }
        };
    }

    /// <summary>Why a call can't start or be answered now, or null.</summary>
    public static string? WhyNot(ChatSession s)
    {
        if (ChatPolicy.CallsBlocked() is { } blocked) return blocked;
        if (s.Engine is null) return "Unlock the chat first.";
        if (_open is not null) return "Hang up the current call first.";
        if (TestMode.Running && !LiveTest) return "Calls aren't made in the self-test.";
        if (!CallPage.RuntimeAvailable()) return "Calls need Windows' WebView2 runtime, which isn't installed on this PC (get \"WebView2 Runtime\" from Microsoft).";
        return null;
    }

    /// <summary>Starts a call with the room's other members (they must be verified: the page asks the engine, which refuses
    /// anyone it doesn't trust).</summary>
    public static void Start(ChatSession s, Room room, string media)
    {
        if (WhyNot(s) is { } why) { if (_open is not null && !TestMode.Running) _open.Activate(); Dialogs.Info("CipherChat call", why); return; }
        var e = s.Engine!;
        var people = room.Members.Select(m => m.User).Where(u => u != e.MyUser).ToList();
        AuditLog.Write("you", "chat", "call started", (media == "video" ? "video" : "voice") + " · " + room.Label(e.MyUser));
        Show(s, (media == "video" ? "Video call" : "Voice call") + " · " + room.Label(e.MyUser), w =>
            w.PostAsync("start", new JsonObject { ["room"] = room.Id, ["media"] = media == "video" ? "video" : "audio", ["people"] = new JsonArray(people.Select(p => (JsonNode)p).ToArray()) }));
    }

    /// <summary>Live session sharing (0.6.39): a voice call with the room in which this PC shares one window — the tab's,
    /// isolated by <paramref name="share"/> — and nothing else, once someone has joined.</summary>
    internal static void StartShare(ChatSession s, Room room, string what, IDisposable share)
    {
        if (WhyNot(s) is { } why) { share.Dispose(); Dialogs.Info("Live session sharing", why); return; }
        var e = s.Engine!;
        var people = room.Members.Select(m => m.User).Where(u => u != e.MyUser).ToList();
        _open = new CallWindow(s, "📡 Sharing " + what + " · " + room.Label(e.MyUser), w =>
            w.PostAsync("start", new JsonObject { ["room"] = room.Id, ["media"] = "audio", ["people"] = new JsonArray(people.Select(p => (JsonNode)p).ToArray()) }))
        { _share = share };
        _open.Show();
    }

    /// <summary>Starts the page's screen share (its "S" key, sent as real input so the browser accepts it as your
    /// gesture); the share browser picks the isolated window by its title, never the screen or another window.</summary>
    private async Task StartScreenShareAsync()
    {
        if (_shareStarted || _web.CoreWebView2 is not { } core) return;
        _shareStarted = true;
        foreach (var type in new[] { "keyDown", "keyUp" })
            await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", new JsonObject
            {
                ["type"] = type, ["key"] = "s", ["code"] = "KeyS", ["windowsVirtualKeyCode"] = 83, ["nativeVirtualKeyCode"] = 83,
                ["text"] = type == "keyDown" ? "s" : null,
            }.ToJsonString());
    }

    /// <summary>Answers a ringing call: the window gets every signal kept for it, then accepts.</summary>
    public static void Answer(ChatSession s, IncomingCall inc)
    {
        if (WhyNot(s) is { } why) { s.Engine?.Calls.Decline(inc); Dialogs.Info("CipherChat call", why); return; }
        AuditLog.Write("you", "chat", "call answered", (inc.Media == "video" ? "video" : "voice") + " · " + inc.From);
        Show(s, (inc.Media == "video" ? "Video call" : "Voice call") + " with " + RingPopup.Clean(inc.From), w =>
            w.PostAsync("answer", new JsonObject { ["call"] = inc.Call, ["items"] = w._e.Calls.Open(inc.Call) }));
    }

    private static void Show(ChatSession s, string title, Func<CallWindow, Task> first)
    {
        _open = new CallWindow(s, title, first);
        _open.Show();
    }

    /// <summary>The chat was locked (or the app is locking): the call ends with it.</summary>
    public static void EndFor(ChatSession s)
    {
        if (_open is { } w && ReferenceEquals(w._session, s)) w.HangUp();
    }

    // ------------------------------------------------------------------------------------------- WebView2
    private async Task InitAsync()
    {
        try
        {
            await _web.EnsureCoreWebView2Async(await CallPage.EnvironmentAsync(share: _share is not null));
            var core = _web.CoreWebView2;
            CallPage.Lock(core.Settings);
            CallPage.Map(core);
            core.NavigationStarting += (_, e) => { if (!e.Uri.StartsWith(CallPage.Origin + "/", StringComparison.Ordinal)) e.Cancel = true; };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.PermissionRequested += (_, e) =>
            {
                var ours = e.Uri.StartsWith(CallPage.Origin + "/", StringComparison.Ordinal);
                e.State = ours && e.PermissionKind is CoreWebView2PermissionKind.Camera or CoreWebView2PermissionKind.Microphone
                    ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
                e.Handled = true;
            };
            core.WebMessageReceived += OnMessage;
            core.Navigate(CallPage.Origin + "/" + CallPage.Start);
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or InvalidOperationException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException or System.IO.IOException)
        {
            Log.Write("Call window", ex);
            _ended = true;
            Ui.Window.Status("📞 The call couldn't start: " + ex.Message);
            Close();
        }
    }

    private Task PostAsync(string ev, JsonObject detail)
    {
        _web.CoreWebView2?.PostWebMessageAsJson(new JsonObject { ["event"] = ev, ["detail"] = detail }.ToJsonString());
        return Task.CompletedTask;
    }

    private void OnSignals(JsonArray items) => Dispatcher.BeginInvoke(() => PostAsync("signals", new JsonObject { ["items"] = items }));

    private async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!e.Source.StartsWith(CallPage.Origin + "/", StringComparison.Ordinal)) return;   // only our own page
        JsonObject m;
        try { m = JsonNode.Parse(e.WebMessageAsJson) as JsonObject ?? []; } catch (JsonException) { return; }
        if (m["event"] is not null) { await OnEventAsync(m.StrOrNull("event") ?? "", m["detail"] as JsonObject ?? []); return; }
        var id = m.LongOrNull("id");
        if (id is null) return;
        JsonObject reply;
        try { reply = new JsonObject { ["reply"] = id, ["ok"] = true, ["value"] = await Dispatch(_e.Calls, m.StrOrNull("op") ?? "", m["args"]) }; }
        catch (Exception ex) when (ex is CryptoFail or CipherChat.Api.ApiException or InvalidOperationException or ArgumentException or FormatException or KeyNotFoundException)
        {
            reply = new JsonObject { ["reply"] = id, ["ok"] = false, ["error"] = ex.Message };
        }
        _web.CoreWebView2?.PostWebMessageAsJson(reply.ToJsonString());
    }

    /// <summary>Everything the call page may ask of the app — the same list as CipherChat's own call window. Anything else
    /// (another API path, an unknown operation, a malformed request) is refused.</summary>
    internal static async Task<JsonNode?> Dispatch(CipherChat.Engine.Calls c, string op, JsonNode? args)
    {
        switch (op)
        {
            case "seal":
            {
                var fields = Obj(args);
                c.Started(fields.Str("call"));                     // a call this window started: its signals come here from now on
                return c.Seal(fields);
            }
            case "open": return c.OpenSignal(Obj(args));
            case "wrapKey":
            {
                var a = Obj(args);
                var key = Bytes.Unb64(a.Str("key"), 32);
                try { return c.WrapKey(key, a.Str("call"), a.Long("epoch"), Users(a)); }
                finally { Bytes.Wipe(key); }
            }
            case "unwrapKey":
            {
                var key = c.UnwrapKey(Obj(args));
                try { return Bytes.B64(key); } finally { Bytes.Wipe(key); }
            }
            case "words": { var a = Obj(args); return new JsonArray(c.Words(a.Str("call"), Users(a)).Select(w => (JsonNode)w).ToArray()); }
            case "api":
            {
                var a = Obj(args);
                return ApiPath(a.StrOrNull("path")) switch
                {
                    "calls/signal" => await c.SignalAsync(a.Obj("body")).ConfigureAwait(true),
                    _ => await c.TurnAsync().ConfigureAwait(true),
                };
            }
            default: throw new InvalidOperationException("unknown request");
        }
    }

    /// <summary>The only server paths the page may use.</summary>
    internal static string ApiPath(string? path) => path is "calls/signal" or "calls/turn" ? path : throw new InvalidOperationException("the call page may only send call signals");

    private static JsonObject Obj(JsonNode? n) => n as JsonObject ?? throw new ArgumentException("malformed request");

    private static List<string> Users(JsonObject a) =>
        (a["users"] as JsonArray ?? throw new ArgumentException("malformed request"))
            .Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : throw new ArgumentException("malformed request")).ToList();

    private async Task OnEventAsync(string ev, JsonObject d)
    {
        switch (ev)
        {
            case "ready":
                var labels = new JsonObject();
                foreach (var r in _e.Rooms) labels[r.Id] = r.Label(_e.MyUser);
                await PostAsync("init", new JsonObject
                {
                    ["user"] = _e.MyUser, ["dev"] = _e.Calls.Device, ["labels"] = labels,
                    ["trusted"] = new JsonArray(_e.Calls.TrustedUsers.Select(u => (JsonNode)u).ToArray()),
                    ["diag"] = false, ["fakeMedia"] = TestMode.Running && LiveTest,
                }).ConfigureAwait(true);
                await _first(this).ConfigureAwait(true);
                break;
            case "words":
                // The call's safety words: the same on every device in the call when nobody is in the middle.
                WordsForTest = (d["words"] as JsonArray ?? []).Select(x => x is JsonValue v && v.TryGetValue<string>(out var w) ? RingPopup.Clean(w) : "").ToArray();
                Title = Title.Split("  ·  ")[0] + "  ·  " + string.Join(" ", WordsForTest);
                break;
            case "notice":
                lock (NoticesForTest) NoticesForTest.Add(RingPopup.Clean(d.StrOrNull("text") ?? ""));
                if (_share is not null && !_shareStarted && (d.StrOrNull("text") ?? "").Contains("joined", StringComparison.OrdinalIgnoreCase))
                    try { await StartScreenShareAsync().ConfigureAwait(true); }
                    catch (Exception ex)
                    {
                        Log.Write("Live share start", ex);
                        AuditLog.Write("you", "chat", "live session sharing failed to start", ex.GetBaseException().Message);
                        Ui.Window.Status("📡 The live share couldn't start: the call is voice only. Hang up and share again.");
                        Title = "⚠ Not sharing — " + Title;
                    }
                Ui.Window.Status("📞 " + RingPopup.Clean(d.StrOrNull("text") ?? ""));
                break;
            case "ended":
                if (_ended) return;
                _ended = true;
                Ui.Window.Status("📞 Call ended (" + RingPopup.Clean(d.StrOrNull("why") ?? "") + ").");
                AuditLog.Write("you", "chat", "call ended", RingPopup.Clean(d.StrOrNull("why") ?? ""));
                await Task.Delay(600).ConfigureAwait(true);
                Close();
                break;
        }
    }

    private void HangUp()
    {
        if (_ended || _web.CoreWebView2 is null) { _ended = true; Close(); return; }
        _ = PostAsync("hangup", []);
        _ = Task.Delay(3000).ContinueWith(_ => Dispatcher.BeginInvoke(() => { _ended = true; Close(); }), TaskScheduler.Default);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_ended && _web.CoreWebView2 is not null)
        {
            e.Cancel = true;                                        // hang up properly first; "ended" closes the window
            HangUp();
            return;
        }
        base.OnClosing(e);
    }
}
