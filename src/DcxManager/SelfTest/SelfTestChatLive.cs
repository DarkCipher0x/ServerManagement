using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CipherChat.Api;
using CipherChat.Crypto;
using CipherChat.Engine;
using CipherChat.Transport;
using DcxManager.Chat;
using DcxManager.Chat.Calls;
using DcxManager.Chat.Views;
using DcxManager.Services;
using DcxManager.Views;
using Microsoft.Web.WebView2.Core;
using Room = CipherChat.Engine.Room;

namespace DcxManager;

/// <summary>
/// The live run (ADR-117): ServerManagement's CipherChat integration against a REAL CipherChat server — CipherChat's own
/// Python server in development mode on 127.0.0.1, started by <c>tools\chat-live-test.ps1</c> from the commit pinned in
/// VENDOR.json. Only that script sets <c>DCX_CHAT_LIVE_ORIGIN</c>; the release build never does, so this suite never
/// runs there. The server's owner is "the other person" (CipherChat's own engine, as CipherChat's apps run it); this PC
/// goes through ServerManagement's own code — the same sign-in, identity, owner check, pairing, room screen, pictures,
/// dashboard tiles, administration, ring and call window the user drives. No window is ever on the screen: the room is a
/// tab of the off-screen main window and both call pages run in off-screen windows with CipherChat's drawn test camera.
/// </summary>
internal static class ChatLiveSuite
{
    public const string OriginVar = "DCX_CHAT_LIVE_ORIGIN", OwnerVar = "DCX_CHAT_LIVE_OWNER", EnrolVar = "DCX_CHAT_LIVE_ENROL";

    public static bool Requested => Environment.GetEnvironmentVariable(OriginVar) is { Length: > 0 };

    /// <summary>Where the run's renders go (the report's shots folder).</summary>
    private static string _shots = "";

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public static async Task RunAsync(MainWindow main, string reportPath)
    {
        var log = new StringBuilder();
        int failures = 0;
        _shots = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(reportPath)!, "shots")).FullName;
        void Ok(string s) { log.AppendLine("ok    " + s); SelfTest.Progress(reportPath, "ok    " + s); }
        void Fail(string s, Exception? ex) { failures++; log.AppendLine("FAIL  " + s + " :: " + ex); SelfTest.Progress(reportPath, "FAIL  " + s); }
        Application.Current.DispatcherUnhandledException += (_, e) => { Fail("unhandled UI exception", e.Exception); e.Handled = true; };
        Ok(SelfTest.StartupLine);
        try { await new Run(main, Ok, Fail).AllAsync(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Fail("CipherChat live run", ex); }
        finally { CallWindow.LiveTest = false; }
        lock (Dialogs.SelfTestPrompts)
            if (Dialogs.SelfTestPrompts.Count > 0) Fail("the live run reached " + Dialogs.SelfTestPrompts.Count + " dialog(s): " + string.Join(" | ", Dialogs.SelfTestPrompts.Distinct().Take(12)), null);
            else Ok("no dialog was reached");
        log.Insert(0, $"DarkCipher ServerManagement {App.DisplayVersion} self-test (CipherChat live) {DateTime.Now:yyyy-MM-dd HH:mm:ss} — {failures} failure(s)\n\n");
        File.WriteAllText(reportPath, log.ToString());
        Application.Current.Shutdown(failures == 0 ? 0 : 1);
    }

    /// <summary>One person's authenticator: RFC 6238 codes, each 30-second window used once (the server takes a code once).</summary>
    private sealed class Totp(string secret)
    {
        private long _used = -1;
        public async Task<string> NextAsync()
        {
            long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (Now() / 30 <= _used) await Task.Delay(TimeSpan.FromSeconds((_used + 1) * 30 - Now() + 1));
            _used = Now() / 30;
            return Code(secret, 0);
        }
        /// <summary>A code of an earlier window (enrolment's first code, so the sign-in after it gets the current one).</summary>
        public string Previous() => Code(secret, -1);

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350", Justification = "RFC 6238 TOTP is defined over HMAC-SHA1")]
        private static string Code(string secret, int offset)
        {
            const string A = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
            int bits = 0, val = 0; var key = new List<byte>();
            foreach (var c in secret.TrimEnd('=').ToUpperInvariant()) { val = val << 5 | A.IndexOf(c); bits += 5; if (bits >= 8) { key.Add((byte)(val >> (bits - 8))); bits -= 8; } }
            var step = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30 + offset;
            var msg = BitConverter.GetBytes(step); if (BitConverter.IsLittleEndian) Array.Reverse(msg);
            var h = HMACSHA1.HashData(key.ToArray(), msg);
            var o = h[^1] & 15;
            var n = ((h[o] & 0x7f) << 24 | h[o + 1] << 16 | h[o + 2] << 8 | h[o + 3]) % 1_000_000;
            return n.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private sealed class Run(MainWindow main, Action<string> ok, Action<string, Exception?> fail)
    {
        private readonly Uri _origin = new(Environment.GetEnvironmentVariable(OriginVar)!);
        private readonly string _ownerUser = Environment.GetEnvironmentVariable(OwnerVar) ?? "owner";
        private const string OwnerPassword = "live run first password 7", OwnerPass = "live run first chat passphrase";
        private const string MemberPassword = "live run second password 7", MemberPass = "live run second chat passphrase";

        private ChatApi _ownerApi = null!;
        private Totp _ownerCode = null!;
        private ChatEngine _owner = null!;
        private string _member = "";
        private Totp _memberCode = null!;
        private ChatSession _pc = null!;
        private Room _room = null!;

        private ITransport T() => new LoopbackDevTransport(_origin);
        private ChatApi Api() => new(T(), "CipherChat/0.2 (live run; owner)");

        /// <summary>Waits out the server's sign-in limit (6 a minute per connection; every request here comes from 127.0.0.1).</summary>
        private static async Task<T> Paced<T>(Func<Task<T>> call)
        {
            for (var i = 0; ; i++)
            {
                try { return await call(); }
                catch (ApiException e) when (e.Status == 429 && i < 3) { await Task.Delay(TimeSpan.FromSeconds(62)); }
            }
        }
        private static Task<bool> Paced(Func<Task> call) => Paced(async () => { await call(); return true; });

        private static async Task Until(Func<bool> cond, string what, int seconds = 40)
        {
            for (var i = 0; i < seconds * 10; i++)
            {
                if (cond()) return;
                await Task.Delay(100);
            }
            throw new Exception("timed out waiting for " + what);
        }

        private static async Task Settle(int ms = 300)
        {
            await Task.Delay(ms);
            await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }

        public async Task AllAsync()
        {
            CallWindow.LiveTest = true;
            await Step("live server: the owner enrols and creates the chat's trust anchor", OwnerAsync);
            await Step("this PC signs in, creates its identity and checks the owner's safety number (ServerManagement's own sign-in steps)", MemberAsync);
            await Step("pairing: a code from the unlocked chat pairs a second session that is remembered encrypted and resumes", PairingAsync);
            await Step("the room screen: messages both ways through the server (typed and sent in ServerManagement's room tab)", RoomAsync);
            await Step("pictures: a picture from this PC arrives with its thumbnail and size; one from the other person shows inline here", PicturesAsync);
            await Step("reactions, edits and burn messages between ServerManagement and CipherChat's engine", ExtrasAsync);
            await Step("the chat screen, laid out as CipherChat's Windows client: sidebar, room header, terminal log, prompt and status line", ScreenAsync);
            await Step("notifications: a new message from the other person raises a Windows notification (text only when previews allow); none with ChatNotify off", NotifyAsync);
            await Step("dashboard tiles from the live chat (unread, contacts) and the administration tile", DashboardAsync);
            await Step("server administration: ServerManagement's admin pane signs in to the real server (memory only)", AdminAsync);
            await Step("a call rings here: the other person's signed ring reaches ServerManagement's ring and stops when declined", RingAsync);
            await Step("a video call: ServerManagement's call window and CipherChat's call page connect through the server, same call words, video both ways", CallAsync);
            await Step("live session sharing: a tab shared into the room moves into its own window, the call captures only that window and the other person sees it; the title comes back after", ShareAsync);
            await Step("saved chat passphrase: ticked at the unlock it is kept encrypted (in no file readable), a locked chat unlocks with Windows Hello, the app's unlock opens it without asking again; a refused one is forgotten, the policy deletes them", SavedPassAsync);
            await Step("lock and tidy up: locking wipes this PC's chat keys; the test accounts are removed", TidyAsync);
        }

        private async Task Step(string name, Func<Task<string>> body)
        {
            try { ok("CipherChat live — " + name + ": " + await body()); }
            catch (Exception ex) { fail("CipherChat live — " + name, ex); throw new OperationCanceledException("stopped after a failed step"); }
        }

        // ------------------------------------------------------------------------------------------- the other person
        private async Task<string> OwnerAsync()
        {
            var token = Environment.GetEnvironmentVariable(EnrolVar) ?? throw new Exception(EnrolVar + " isn't set");
            _ownerApi = Api();
            var start = await Paced(() => _ownerApi.CallAsync("auth/enrol/start", new JsonObject { ["token"] = token, ["password"] = OwnerPassword }));
            _ownerCode = new Totp(start.Str("secret"));
            await Paced(() => _ownerApi.CallAsync("auth/enrol/finish", new JsonObject { ["token"] = token, ["code"] = _ownerCode.Previous() }));
            await Paced(async () => await _ownerApi.LoginAsync(_ownerUser, OwnerPassword, await _ownerCode.NextAsync()));
            var st = await _ownerApi.CallAsync("state");
            var me = await Gate.SetupAsync(_ownerApi, st, OwnerPass, reset: false, ask: async _ => await _ownerCode.NextAsync());
            _owner = new ChatEngine(_ownerApi, me) { Ask = async _ => await _ownerCode.NextAsync() };
            _ownerApi.Ask = _owner.Ask;
            await _owner.StartAsync();
            return $"{_ownerUser} enrolled with password + authenticator, identity made on {_origin.Authority}";
        }

        // ------------------------------------------------------------------------------------------- this PC
        private async Task<string> MemberAsync()
        {
            _member = "sm" + Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant();
            var inv = await _ownerApi.CallAsync("admin/users/invite", new JsonObject { ["username"] = _member, ["role"] = "member" });
            var token = inv.Obj("link").Str("url").Split("#enrol=")[1];
            var enrol = Api();
            var start = await Paced(() => enrol.CallAsync("auth/enrol/start", new JsonObject { ["token"] = token, ["password"] = MemberPassword }));
            _memberCode = new Totp(start.Str("secret"));
            await Paced(() => enrol.CallAsync("auth/enrol/finish", new JsonObject { ["token"] = token, ["code"] = _memberCode.Previous() }));

            // From here on, ServerManagement's own steps (as the Chats screen runs them).
            _pc = new ChatSession { Ask = async _ => await _memberCode.NextAsync() };
            await Paced(async () => await _pc.SignInAsync(_origin.ToString(), _member, MemberPassword, await _memberCode.NextAsync()));
            if (_pc.Stage != ChatSession.Stages.NeedsIdentity) throw new Exception("after sign-in: " + _pc.Stage);
            await _pc.CreateIdentityAsync(MemberPass);
            if (_pc.Stage != ChatSession.Stages.NeedsOwnerCheck) throw new Exception("after the identity: " + _pc.Stage);
            await _owner.RefreshAsync();
            var them = _owner.People.GetValueOrDefault(_member) ?? throw new Exception("the owner doesn't see " + _member);
            if (them.Trust != TrustLevel.Pending) throw new Exception("the owner's view of the new person: " + them.Trust);
            if (_pc.OwnerSafetyNumber is not { Length: > 0 } sn || sn != _owner.SafetyNumber(them)) throw new Exception("the safety numbers differ");
            await _pc.ConfirmOwnerAsync(MemberPass);
            if (_pc.Stage != ChatSession.Stages.Ready || _pc.Engine is null) throw new Exception("not ready: " + _pc.Stage);
            await _owner.CertifyAsync(them);
            await _pc.Engine.RefreshAsync();
            if (_pc.Engine.People.GetValueOrDefault(_ownerUser)?.Trust != TrustLevel.Ok) throw new Exception("this PC doesn't trust the owner");
            ChatHub.Adopt(_pc);
            return $"{_member}: signed in → identity → the same 60-digit safety number on both sides → owner pinned and certified; both trust each other";
        }

        private async Task<string> PairingAsync()
        {
            var code = await _pc.Engine!.PairDeviceAsync("ServerManagement live run");
            var second = new ChatSession();
            await Paced(() => second.PairAsync(code));
            if (second.Account is not { } acct || second.Stage != ChatSession.Stages.NeedsUnlock) throw new Exception("after pairing: " + second.Stage);
            ChatAccounts.Reset();
            if (!ChatAccounts.All.Any(a => a.Origin == acct.Origin && a.User == _member)) throw new Exception("the pairing wasn't remembered");
            var store = Directory.GetFiles(Environment.GetEnvironmentVariable("DCX_DATA_DIR")!, "*", SearchOption.AllDirectories);
            foreach (var f in store)
            {
                var bytes = await File.ReadAllBytesAsync(f);
                if (bytes.Length < 50_000_000 && Encoding.UTF8.GetString(bytes).Contains(acct.DeviceToken, StringComparison.Ordinal)) throw new Exception("the device token is readable in " + Path.GetFileName(f));
            }
            var resumed = new ChatSession();
            await Paced(() => resumed.ResumeAsync(acct));
            if (resumed.Stage != ChatSession.Stages.NeedsUnlock) throw new Exception("resumed: " + resumed.Stage);
            await resumed.UnlockAsync(MemberPass);
            if (resumed.Stage != ChatSession.Stages.Ready) throw new Exception("unlocked: " + resumed.Stage);
            var devices = await resumed.Engine!.DevicesAsync();
            resumed.Lock();
            await resumed.SignOutAsync(forgetPc: true);
            second.Lock();
            ChatAccounts.Reset();
            if (ChatAccounts.All.Any(a => a.User == _member)) throw new Exception("forgetting the PC left its pairing");
            return $"paired with a one-time code, the device token found in no file of the store (encrypted), resumed with it and unlocked with the passphrase ({(devices["devices"] as JsonArray)?.Count ?? 0} devices listed), then forgotten";
        }

        // ------------------------------------------------------------------------------------------- the room
        private ChatRoomView _view = null!;

        private async Task<string> RoomAsync()
        {
            _room = await _owner.CreateRoomAsync("live-" + _member, "ServerManagement live run", 1, [_member]);
            await _owner.OpenRoomAsync(_room);
            await _owner.SendTextAsync(_room, "hello from CipherChat's engine ✓");
            var e = _pc.Engine!;
            await Until(() => e.RoomById(_room.Id) is not null || Refresh(e), "the room at this PC");
            var mine = e.RoomById(_room.Id)!;
            ChatHomeView.OpenRoom(_pc, mine);
            await Settle(600);
            _view = main.Panes.AllTabs.Select(t => t.Content).OfType<ChatRoomView>().FirstOrDefault()
                ?? throw new Exception("the room didn't open as a tab");
            await Until(() => _view.RowTextsForTest.Any(t => t.Contains("hello from CipherChat's engine ✓")), "the owner's message in the room tab");
            await _view.SendForTest("hello from ServerManagement ✓");
            await Until(() => Texts(_owner, _room.Id).Contains("hello from ServerManagement ✓"), "this PC's message at the owner");
            return "the owner's message shows in ServerManagement's room tab; one typed and sent there arrives at CipherChat's engine, decrypted and verified";
        }

        private static bool Refresh(ChatEngine e) { _ = e.RefreshAsync(); return false; }

        private static List<string> Texts(ChatEngine e, string room)
        {
            var r = e.RoomById(room);
            if (r is null) return [];
            lock (r.Timeline) return r.Timeline.Where(x => x.Message is not null).Select(x => x.Message!.Text).ToList();
        }

        private static ChatMessage? Find(ChatEngine e, string room, Func<ChatMessage, bool> pick)
        {
            var r = e.RoomById(room);
            if (r is null) return null;
            lock (r.Timeline) return r.Timeline.Select(x => x.Message).OfType<ChatMessage>().LastOrDefault(pick);
        }

        private async Task<string> PicturesAsync()
        {
            // From this PC: a picture added to the composer (as a screenshot is) goes with a thumbnail and its size.
            var png = MakePng(900, 300);
            _view.AddFile("live-shot.png", png, "image/png");
            if (_view.PendingForTest is not [{ Thumb: not null, W: 900, H: 300 }]) throw new Exception("the composer's picture has no preview");
            await _view.SendForTest("a screenshot");
            await Until(() => Find(_owner, _room.Id, m => m.Files.Count > 0) is not null, "the picture at the owner");
            var got = Find(_owner, _room.Id, m => m.Files.Count > 0)!.Files[0];
            if (got.Kind != "image" || got.Thumb is null || got.W != 900 || got.H != 300) throw new Exception($"at the owner: {got.Kind} thumb={got.Thumb is not null} {got.W}x{got.H}");
            var back = await _owner.FetchFileAsync(_owner.RoomById(_room.Id)!, got);
            if (!back.AsSpan().SequenceEqual(png)) throw new Exception("the picture changed on the way");
            var thumb = await _owner.FetchFileAsync(_owner.RoomById(_room.Id)!, got.Thumb);
            System.Windows.Media.Imaging.BitmapFrame t;
            using (var ms = new MemoryStream(thumb)) t = System.Windows.Media.Imaging.BitmapDecoder.Create(ms, System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames[0];
            if (thumb is not [0xFF, 0xD8, ..] || Math.Max(t.PixelWidth, t.PixelHeight) > ChatPictures.ThumbMax) throw new Exception($"the thumbnail isn't a small JPEG ({t.PixelWidth}x{t.PixelHeight})");

            // From the other person: shown inline in the room tab from its encrypted thumbnail.
            var before = _view.PicturesShownForTest;
            var theirs = MakePng(400, 400);
            var o = ChatPictures.Outgoing("theirs.png", theirs);
            await _owner.SendFilesAsync(_owner.RoomById(_room.Id)!, "their picture", [o]);
            await Until(() => _view.PicturesShownForTest > before, "the owner's picture shown in the room tab");
            return $"900×300 PNG sent with a {t.PixelWidth}×{t.PixelHeight} JPEG thumbnail, byte-identical at the owner; the owner's picture shows inline here ({_view.PicturesShownForTest} shown)";
        }

        private static byte[] MakePng(int w, int h)
        {
            var px = new byte[w * h * 4];
            for (var i = 0; i < px.Length; i += 4) { px[i] = (byte)(i / 4 % w); px[i + 1] = (byte)(i / 4 / w); px[i + 2] = 0x80; px[i + 3] = 0xff; }
            var bmp = System.Windows.Media.Imaging.BitmapSource.Create(w, h, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, px, w * 4);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }

        /// <summary>0.6.41: Chats as CipherChat draws it — the sidebar's ROOMS / DIRECT / PEOPLE (this room, the owner verified),
        /// the room on the right with its key line, prompt and status line, a slash command answered in the log, and a
        /// render of the screen.</summary>
        private async Task<string> ScreenAsync()
        {
            ChatModule.OpenHome();
            await Settle(600);
            var home = main.Panes.AllTabs.Select(t => t.Content).OfType<ChatHomeView>().FirstOrDefault() ?? throw new Exception("Chats didn't open");
            home.SelectForTest(_pc);
            var room = _pc.Engine!.RoomById(_room.Id)!;
            home.ShowRoomForTest(_pc, room);
            await Settle(800);
            var side = home.SideTextsForTest;
            foreach (var want in new[] { "ACCOUNTS", "ROOMS", "DIRECT", "PEOPLE", room.Label(_pc.Engine.MyUser), "verified" })
                if (!side.Any(t => t.Contains(want, StringComparison.Ordinal))) throw new Exception("the sidebar has no “" + want + "”: " + string.Join(" | ", side.Take(30)));
            var view = home.RoomViewForTest ?? throw new Exception("the room isn't shown on the chat screen");
            if (view.RoomId != room.Id) throw new Exception("another room is shown");
            var head = view.ChromeTextsForTest;
            foreach (var want in new[] { room.Label(_pc.Engine.MyUser), "key #", "hold it", "@cipherchat:", "ML-KEM-1024", "online" })
                if (!head.Any(t => t.Contains(want, StringComparison.Ordinal))) throw new Exception("the room has no “" + want + "”: " + string.Join(" | ", head));
            if (!view.RowTextsForTest.Any(t => t.Contains("hello from CipherChat's engine ✓"))) throw new Exception("the room's messages aren't shown on the chat screen");
            await view.SendForTest("/help");
            await Until(() => view.LogTextsForTest.Any(t => t.Contains("/join #room")), "the /help lines in the log", 10);
            if (!view.LogTextsForTest.Any(t => t == "/kick user ...")) throw new Exception("/help: /kick isn't on its own line");
            if (Texts(_owner, _room.Id).Any(t => t.Contains("/help"))) throw new Exception("a slash command was sent as a message");
            await Settle(400);
            var png = Path.Combine(_shots, "chat-screen.png");
            SelfTest.Snap(main, png);
            // and at a full-size window (the self-test's own window is small): the same screen, laid out wide
            var (ow, oh) = (main.Width, main.Height);
            main.Width = 1920; main.Height = 1080;
            await Settle(900);
            SelfTest.Snap(main, Path.Combine(_shots, "chat-screen-wide.png"));
            main.Width = ow; main.Height = oh;
            await Settle(300);
            return "Chats shows the sidebar (accounts, ROOMS, DIRECT, PEOPLE with trust) and the room in place: its key line, the prompt, the status line; /help answered in the log and nothing sent; rendered " + Path.GetFileName(png) + " and chat-screen-wide.png";
        }

        private async Task<string> ExtrasAsync()
        {
            var e = _pc.Engine!;
            var mine = e.RoomById(_room.Id)!;
            var hello = Find(e, _room.Id, m => m.Text == "hello from CipherChat's engine ✓") ?? throw new Exception("no message to react to");
            await e.ReactAsync(mine, hello, "👍", true);
            await Until(() => Find(_owner, _room.Id, m => m.Text.StartsWith("hello from CipherChat", StringComparison.Ordinal))?.Reactions.Any(r => r.Users.Contains(_member)) == true, "the reaction at the owner");
            var ownersCopy = Find(_owner, _room.Id, m => m.Text == "hello from ServerManagement ✓") ?? throw new Exception("the owner lost the message");
            await _owner.ReactAsync(_owner.RoomById(_room.Id)!, ownersCopy, "✅", true);
            var myMsg = Find(e, _room.Id, m => m.Mine && m.Text == "hello from ServerManagement ✓") ?? throw new Exception("this PC lost its message");
            await e.EditAsync(mine, myMsg, "hello from ServerManagement (edited) ✓");
            await Until(() => Texts(_owner, _room.Id).Contains("hello from ServerManagement (edited) ✓"), "the edit at the owner");
            await Until(() => _view.RowTextsForTest.Any(t => t.Contains("✅")), "the owner's reaction in the room tab");
            await _owner.SendTextAsync(_owner.RoomById(_room.Id)!, "this burns", burn: 10);
            await Until(() => Find(e, _room.Id, m => m.Burn == 10) is not null, "the burn message here");
            var burn = Find(e, _room.Id, m => m.Burn == 10)!;
            if (_view.RowTextsForTest.Any(t => t.Contains("this burns"))) throw new Exception("a burn message shows before it's revealed");
            await e.RevealBurnAsync(mine, burn);
            e.BurnOut(mine, burn);
            await Until(() => Find(_owner, _room.Id, m => m.Burn == 10) is null, "the burn message destroyed for the owner");
            return "👍 from here and ✅ from the owner reach the other side (shown in the tab), an edit arrives as edited, a burn message stays hidden until revealed and is then destroyed on the server";
        }

        // ------------------------------------------------------------------------------------------- notifications
        private async Task<string> NotifyAsync()
        {
            var e = _pc.Engine!;
            var st = DcxManager.Services.AppSettings.Current;
            var (was, notify) = (e.Viewing, st.ChatNotify);
            static List<(string Title, string Text)> Raised() { lock (ChatNotifier.RaisedForTest) return ChatNotifier.RaisedForTest.ToList(); }
            try
            {
                e.Viewing = null;              // the room isn't the one in front of you
                st.ChatNotify = true;
                ChatNotifier.ResetForTest();
                await _owner.SendTextAsync(_owner.RoomById(_room.Id)!, "notify me about this");
                await Until(() => Raised().Any(t => t.Text.Contains(_ownerUser)), "the notification for the owner's message");
                var text = Raised().Last(t => t.Text.Contains(_ownerUser)).Text;
                if (text.Contains("notify me about this") != st.ChatToastPreviews) throw new Exception("the notification's text doesn't follow ChatToastPreviews: " + text);
                st.ChatNotify = false;
                ChatNotifier.ResetForTest();
                await _owner.SendTextAsync(_owner.RoomById(_room.Id)!, "no notification for this one");
                await Until(() => Texts(e, _room.Id).Contains("no notification for this one"), "the second message at this PC");
                await Task.Delay(500);
                if (Raised().Count > 0) throw new Exception("a notification with ChatNotify off");
                return $"“{text}” (previews {(st.ChatToastPreviews ? "on" : "off")}); with ChatNotify off the next message raised none";
            }
            finally { st.ChatNotify = notify; e.Viewing = was; }
        }

        // ------------------------------------------------------------------------------------------- dashboard + admin
        private async Task<string> DashboardAsync()
        {
            await _owner.SendTextAsync(_owner.RoomById(_room.Id)!, "unread for the dashboard");
            await Until(() => (_pc.Engine!.RoomById(_room.Id)?.Unread ?? 0) > 0 || Texts(_pc.Engine!, _room.Id).Contains("unread for the dashboard"), "the message at this PC", 30);
            var t = ChatModule.Trust();
            var tile = ChatModule.TrustTile(t);
            if (t.Verified < 1 || t.Changed != 0) throw new Exception($"trust counts {t}");
            var sources = DashboardSources.All.Where(s => s.Module == "chat").Select(s => s.Id).ToList();
            if (!sources.Contains("chat") || !sources.Contains("chat-people") || !sources.Contains("chat-admin")) throw new Exception("sources: " + string.Join(",", sources));
            return $"contacts: {t.Verified} verified, {t.Pending} not yet, {t.Changed} changed → tile “{tile.Value} {tile.Detail}”; unread over the live chats: {ChatHub.Unread}";
        }

        private async Task<string> AdminAsync()
        {
            var a = ChatHub.Admin;
            await Paced(async () => await a.SignInAsync(_origin.ToString(), _ownerUser, OwnerPassword, await _ownerCode.NextAsync()));
            a.Ask = async _ => await _ownerCode.NextAsync();
            if (!a.SignedIn) throw new Exception("not signed in");
            var people = await a.PeopleAsync();
            if (people.Users.FirstOrDefault(u => u.Username == _member) is not { } u || !u.ChatIdentity || !u.Certified) throw new Exception("the admin pane doesn't see " + _member + " certified");
            var req = await a.RequestsAsync("new");
            var roles = await a.RolesAsync();
            var settings = await a.SettingsAsync();
            var audit = await a.AuditAsync();
            var outcome = await DashboardSources.All.First(s => s.Id == "chat-admin").Check();
            await a.SignOutAsync();
            if (a.SignedIn) throw new Exception("still signed in");
            return $"signed in as {_ownerUser}; people ({people.Users.Count}, {_member} has an identity and is certified), {req.Requests.Count} request(s) waiting, {roles.Roles.Count} roles, {settings.GetType().Name} read, {audit.Count} audit events; the admin tile checked “{outcome.Text ?? outcome.Error}”; signed out (nothing kept)";
        }

        // ------------------------------------------------------------------------------------------- calls
        private async Task<string> RingAsync()
        {
            var shownBefore = RingPopup.Shown;
            var stopped = new TaskCompletionSource<string>();
            _owner.Calls.RingStopped += (_, why) => stopped.TrySetResult(why);
            IncomingCall? inc = null;
            _pc.Engine!.Calls.Ringing += x => inc = x;
            var callId = Call.NewCallId();
            var to = new JsonArray(_member);
            var sig = _owner.Calls.Seal(new JsonObject { ["kind"] = "ring", ["room"] = _room.Id, ["call"] = callId, ["dev"] = _owner.Calls.Device, ["to"] = to, ["media"] = "audio", ["people"] = new JsonArray(_ownerUser, _member) });
            await _owner.Calls.SignalAsync(new JsonObject { ["room"] = _room.Id, ["to"] = to.DeepClone(), ["sig"] = sig });
            await Until(() => RingPopup.Shown > shownBefore && inc is not null, "the ring at this PC");
            if (inc!.From != _ownerUser || inc.Room != _room.Id || inc.Call != callId) throw new Exception("the ring says " + inc);
            _pc.Engine.Calls.Decline(inc);
            // The decline goes to the caller as a signed "decline" signal; read it back from the owner's poll.
            var seen = false;
            _owner.Calls.Signals += _ => seen = true;
            _owner.Calls.Started(callId);
            await Until(() => seen, "the decline at the caller", 30);
            _owner.Calls.Closed();
            return "a signed ring from the other person reached ServerManagement's ring (counted, never drawn in a self-test) with caller, room and call id; Decline sent a signed decline the caller received";
        }

        private Task<string> CallAsync() => CallCoreAsync(share: false);
        private Task<string> ShareAsync() => CallCoreAsync(share: true);

        private async Task<string> CallCoreAsync(bool share)
        {
            // The other person's side: CipherChat's own call page in an off-screen browser, its requests answered by the
            // same dispatcher ServerManagement's call window uses, with the owner's engine (keys stay in the engine).
            Window? peerWin = null;
            Action<JsonArray>? onSignals = null;
            Action<IncomingCall>? onRing = null;
            try
            {
                var web = new Microsoft.Web.WebView2.Wpf.WebView2();
                peerWin = new Window { Width = 960, Height = 600, Left = -12000, Top = -12000, WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None, Content = web };
                peerWin.Show();
                await web.EnsureCoreWebView2Async(await CallPage.EnvironmentAsync());
                var core = web.CoreWebView2;
                CallPage.Lock(core.Settings);
                CallPage.Map(core);
                core.PermissionRequested += (_, e) => { e.State = CoreWebView2PermissionState.Allow; e.Handled = true; };
                var peerNotices = new List<string>();
                string[] peerWords = [];
                var ready = new TaskCompletionSource();
                void Post(string ev, JsonObject d) => core.PostWebMessageAsJson(new JsonObject { ["event"] = ev, ["detail"] = d }.ToJsonString());
                core.WebMessageReceived += async (_, e) =>
                {
                    var m = JsonNode.Parse(e.WebMessageAsJson) as JsonObject;
                    if (m is null) return;
                    if (m["event"] is not null)
                    {
                        var ev = m.StrOrNull("event"); var d = m["detail"] as JsonObject ?? [];
                        if (ev == "ready") ready.TrySetResult();
                        else if (ev == "notice") lock (peerNotices) peerNotices.Add(d.StrOrNull("text") ?? "");
                        else if (ev == "words") peerWords = (d["words"] as JsonArray ?? []).Select(x => x?.GetValue<string>() ?? "").ToArray();
                        return;
                    }
                    JsonObject reply;
                    try { reply = new JsonObject { ["reply"] = m.LongOrNull("id"), ["ok"] = true, ["value"] = await CallWindow.Dispatch(_owner.Calls, m.StrOrNull("op") ?? "", m["args"]) }; }
                    catch (Exception ex) { reply = new JsonObject { ["reply"] = m.LongOrNull("id"), ["ok"] = false, ["error"] = ex.Message }; }
                    core.PostWebMessageAsJson(reply.ToJsonString());
                };
                onSignals = items => Application.Current.Dispatcher.BeginInvoke(() => { try { Post("signals", new JsonObject { ["items"] = items.DeepClone() }); } catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or System.Runtime.InteropServices.COMException) { } });
                _owner.Calls.Signals += onSignals;
                IncomingCall? ring = null;
                onRing = x => ring = x;
                _owner.Calls.Ringing += onRing;
                core.Navigate(CallPage.Origin + "/" + CallPage.Start);
                if (await Task.WhenAny(ready.Task, Task.Delay(20000)) != ready.Task) throw new Exception("the other person's call page didn't start");
                var labels = new JsonObject { [_room.Id] = _owner.RoomById(_room.Id)!.Label(_owner.MyUser) };
                Post("init", new JsonObject { ["user"] = _owner.MyUser, ["dev"] = _owner.Calls.Device, ["labels"] = labels, ["trusted"] = new JsonArray(_owner.Calls.TrustedUsers.Select(u => (JsonNode)u).ToArray()), ["diag"] = false, ["fakeMedia"] = true });

                if (CallWindow.WhyNot(_pc) is { } why) throw new Exception("can't call: " + why);
                if (share) return await ShareCoreAsync(core, () => ring, Post);
                // This PC calls, through ServerManagement's call window (the room's 🎥 Video button runs this).
                CallWindow.Start(_pc, _pc.Engine!.RoomById(_room.Id)!, "video");
                await Until(() => ring is not null, "the ring at the other person", 40);
                Post("answer", new JsonObject { ["call"] = ring!.Call, ["items"] = _owner.Calls.Open(ring.Call) });
                var w = CallWindow.Current ?? throw new Exception("no call window");
                await Until(() => { lock (w.NoticesForTest) return w.NoticesForTest.Any(n => n.Contains("joined the call")); }, "the other person joining (as ServerManagement sees it)", 60);
                await Until(() => w.WordsForTest.Length > 0 && peerWords.Length > 0, "the call words on both sides", 30);
                if (!w.WordsForTest.SequenceEqual(peerWords)) throw new Exception($"call words differ: {string.Join(" ", w.WordsForTest)} / {string.Join(" ", peerWords)}");
                async Task<int> RemoteVideo(CoreWebView2 c) =>
                    int.TryParse(JsonNode.Parse(await c.CallDevToolsProtocolMethodAsync("Runtime.evaluate", new JsonObject { ["expression"] = "[...document.querySelectorAll('video')].filter(v => !v.muted && v.videoWidth > 0).length", ["returnByValue"] = true }.ToJsonString()))?["result"]?["value"]?.ToString(), out var n) ? n : 0;
                int here = 0, there = 0;
                for (var i = 0; i < 300 && (here == 0 || there == 0); i++)
                {
                    here = await RemoteVideo(w.CoreForTest!);
                    there = await RemoteVideo(core);
                    if (here == 0 || there == 0) await Task.Delay(200);
                }
                if (here == 0 || there == 0) throw new Exception($"no remote video (here {here}, there {there})");
                w.Close();   // hangs up properly first ("ended" closes it)
                await Until(() => CallWindow.Current is null, "the call window to close after hanging up", 15);
                return $"rang the other person, answered on CipherChat's call page; ServerManagement saw them join; call words “{string.Join(" ", w.WordsForTest)}” on both sides; remote video playing on both sides (frame-encrypted, CipherChat's drawn test camera); hung up and the window closed";
            }
            finally
            {
                if (onSignals is not null) _owner.Calls.Signals -= onSignals;
                if (onRing is not null) _owner.Calls.Ringing -= onRing;
                try { _owner.Calls.Closed(); } catch (InvalidOperationException) { }   // the other person's page is gone: its call is over
                if (CallWindow.Current is { } left) { try { left.Close(); } catch { } }
                try { peerWin?.Close(); } catch { }
            }
        }

        /// <summary>Live session sharing against the other person's call page: a Settings tab stands in for the session
        /// (LiveShare.Start is what the tab menu's item runs after its questions).</summary>
        private async Task<string> ShareCoreAsync(CoreWebView2 peer, Func<IncomingCall?> ring, Action<string, JsonObject> post)
        {
            var sv = main.Panes.AllTabs.FirstOrDefault(t => t.Content is SettingsView);
            if (sv is null) { main.Navigate("settings"); sv = main.Panes.AllTabs.First(t => t.Content is SettingsView); }
            if (LiveShare.Blocked() is { } off) throw new Exception("blocked: " + off);
            if (!LiveShare.Start(_pc, _pc.Engine!.RoomById(_room.Id)!, sv, "Settings (live run)")) throw new Exception("the share didn't start");
            var shared = Window.GetWindow(sv) as FloatWindow ?? throw new Exception("the tab didn't move into a window of its own");
            if (shared.Title != LiveShare.WindowTitle) throw new Exception("the shared window's title: " + shared.Title);
            // 0.6.41: a square window, so the picture's shape tells the window (square) from a screen (16:9) beyond doubt
            shared.WindowState = WindowState.Normal;
            shared.Width = 760; shared.Height = 760;
            await Settle(300);
            var probe = new System.Windows.Controls.TabItem { Header = "probe", Content = new System.Windows.Controls.TextBlock() };
            main.Panes.Add(probe);
            bool leaked = Window.GetWindow(probe) == shared;
            main.CloseTab(probe);
            if (leaked) throw new Exception("a new tab opened into the shared window");
            await Until(() => ring() is not null, "the ring at the other person", 40);
            if (ring()!.Media != "audio") throw new Exception("a share call rang as " + ring()!.Media);
            post("answer", new JsonObject { ["call"] = ring()!.Call, ["items"] = _owner.Calls.Open(ring()!.Call) });
            var w = CallWindow.Current ?? throw new Exception("no call window");
            if (!w.Sharing) throw new Exception("the call window isn't a share");
            await Until(() => { lock (w.NoticesForTest) return w.NoticesForTest.Any(n => n.Contains("joined the call")); }, "the other person joining", 60);
            await Until(() => w.ShareStartedForTest, "the screen share starting", 20);
            // 0.6.41: the received share (never the other person's own preview, the ".self" tile), once a frame with
            // something on it has arrived: before that a call's video is a small blank placeholder.
            const string Probe = "(() => { const v = [...document.querySelectorAll('video')].find(v => v.videoWidth > 0 && !v.closest('.self')); if (!v) return ''; " +
                "const c = document.createElement('canvas'); c.width = 64; c.height = 64; const g = c.getContext('2d'); g.drawImage(v, 0, 0, 64, 64); const d = g.getImageData(0, 0, 64, 64).data; " +
                "let lit = 0; for (let i = 0; i < d.length; i += 4) if (d[i] + d[i + 1] + d[i + 2] > 30) lit++; return v.videoWidth + 'x' + v.videoHeight + ':' + lit; })()";
            string dims = "";
            var lit = 0;
            for (var i = 0; i < 75 && lit == 0; i++)
            {
                var r = JsonNode.Parse(await peer.CallDevToolsProtocolMethodAsync("Runtime.evaluate", new JsonObject { ["expression"] = Probe, ["returnByValue"] = true }.ToJsonString()))?["result"]?["value"]?.ToString() ?? "";
                if (r.Split(':') is [var size, var n] && int.TryParse(n, out var count)) { dims = size; lit = count; }
                if (lit == 0) await Task.Delay(200);
            }
            List<string> notices; lock (w.NoticesForTest) notices = w.NoticesForTest.ToList();
            if (dims.Length == 0) throw new Exception("the other person never saw the shared window; notices here: " + string.Join(" | ", notices.TakeLast(4)));
            // Only the shared window is captured, never a screen. With something on the received frames their shape must be
            // the window's (it was made square above; both screens are 16:9). The self-test keeps every window off the
            // screen (-10000,-10000), where Windows composes nothing for it, so the frames can arrive blank: that is
            // still the window, since a captured screen always has the desktop on it.
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(shared);
            double winW = shared.ActualWidth * dpi.DpiScaleX, winH = shared.ActualHeight * dpi.DpiScaleY;
            var first = dims.Split('x');
            double vw = double.Parse(first[0], System.Globalization.CultureInfo.InvariantCulture), vh = double.Parse(first[1], System.Globalization.CultureInfo.InvariantCulture);
            if (lit > 0 && Math.Abs(vw / vh - winW / winH) > 0.06) throw new Exception($"the picture ({dims}) isn't the shape of the window ({winW:0}x{winH:0}): something other than the shared window was captured");
            if (lit == 0) dims += ", blank: the window is off the screen in the self-test, and a captured screen is never blank";
            dims += $", window {winW:0}x{winH:0}";
            w.Close();
            await Until(() => CallWindow.Current is null, "the call window to close after hanging up", 15);
            await Until(() => shared.Title != LiveShare.WindowTitle, "the shared window's title back", 10);
            if (!AuditLog.Read(DateTime.Now.ToString("yyyy-MM")).Any(e => e.Target == "chat" && e.Action == "live session shared")) throw new Exception("not audited");
            main.Panes.DockBack(sv);
            main.CloseTab(sv);
            return $"the tab moved into its own window titled “{LiveShare.WindowTitle}” (a new tab opened beside, not into it); the other person answered the voice call and saw the window ({dims}); hung up, the title came back; audited";
        }

        // ------------------------------------------------------------------------------------------- the end
        private async Task<string> SavedPassAsync()
        {
            // 0.6.42 (DarkCipher0x: "a way to save the chat passphrase ... the same option" as the app's Windows Hello), through
            // the unlock panel the user sees. Hello is the self-test's stand-in (never a real prompt).
            var st = DcxManager.Services.AppSettings.Current;
            var key = ChatPassphrases.KeyOf(_pc);
            var fake = new DcxManager.Services.WindowsHello.FakeHello();
            DcxManager.Services.WindowsHello.Fake = fake;
            ChatPassphrases.ProtectedForTest = true;
            try
            {
                ChatModule.OpenHome();
                await Settle(400);
                var home = main.Panes.AllTabs.Select(t => t.Content).OfType<ChatHomeView>().FirstOrDefault() ?? throw new Exception("Chats didn't open");
                async Task Locked()
                {
                    _pc.Lock();
                    await Settle(300);
                    home.SelectForTest(_pc);
                    await Settle(300);
                    if (_pc.Stage != ChatSession.Stages.NeedsUnlock) throw new Exception("locking left " + _pc.Stage);
                }
                await Locked();
                if (home.UnlockWithHelloForTest is not null) throw new Exception("“Unlock with Windows Hello” offered with nothing saved");
                await home.UnlockForTest(MemberPass, save: true);
                if (_pc.Stage != ChatSession.Stages.Ready) throw new Exception("typed and saved: " + _pc.Stage);
                if (!ChatPassphrases.IsSaved(key)) throw new Exception("“Save on this PC” didn't save it");
                foreach (var f in Directory.GetFiles(Environment.GetEnvironmentVariable("DCX_DATA_DIR")!, "*", SearchOption.AllDirectories))
                {
                    var bytes = await File.ReadAllBytesAsync(f);
                    if (bytes.Length < 50_000_000 && (Encoding.UTF8.GetString(bytes).Contains(MemberPass, StringComparison.Ordinal) || Encoding.Unicode.GetString(bytes).Contains(MemberPass, StringComparison.Ordinal)))
                        throw new Exception("the saved passphrase is readable in " + Path.GetFileName(f));
                }
                await Locked();
                var hello = home.UnlockWithHelloForTest ?? throw new Exception("no “Unlock with Windows Hello” with a saved passphrase");
                fake.Answer = DcxManager.Services.WindowsHello.Outcome.Cancelled;
                var p0 = fake.Prompts;
                await hello();
                if (fake.Prompts != p0 + 1) throw new Exception("Windows Hello wasn't asked");
                if (_pc.Stage == ChatSession.Stages.Ready) throw new Exception("unlocked although Windows Hello was cancelled");
                fake.Answer = DcxManager.Services.WindowsHello.Outcome.Verified;
                home.SelectForTest(_pc);
                await (home.UnlockWithHelloForTest ?? throw new Exception("the Hello button went away"))();
                if (_pc.Stage != ChatSession.Stages.Ready) throw new Exception("Windows Hello verified, still " + _pc.Stage);
                // the app's own unlock (you just verified): unlocks by itself, no second prompt
                await Locked();
                var p1 = fake.Prompts;
                DcxManager.Services.AppLockEvents.RaiseUnlocked("the live run unlocked the app");
                await Until(() => _pc.Stage == ChatSession.Stages.Ready, "the chat unlocking with the app", 30);
                if (fake.Prompts != p1) throw new Exception("the app's unlock asked Windows Hello again");
                // a passphrase changed elsewhere: refused, forgotten, still locked (said in the status line, no dialog)
                await Locked();
                st.ObserveSecrets["chat:pass:" + key] = "not the passphrase any more";
                await (home.UnlockWithHelloForTest ?? throw new Exception("no Hello button"))();
                if (_pc.Stage == ChatSession.Stages.Ready) throw new Exception("unlocked with a wrong saved passphrase");
                if (st.ObserveSecrets.ContainsKey("chat:pass:" + key)) throw new Exception("the refused passphrase was kept");
                // the policy off: not offered, saved ones deleted
                ChatPassphrases.Save(key, MemberPass, _pc.Display);
                st.ChatAllowRemember = false;
                home.SelectForTest(_pc);
                await Settle(200);
                if (st.ObserveSecrets.ContainsKey("chat:pass:" + key) && ChatPassphrases.IsSaved(key)) throw new Exception("saved with the policy off");
                if (await ChatPassphrases.UnlockAsync(_pc, askHello: false, "self-test") != ChatPassphrases.Result.NotSaved) throw new Exception("the policy off still unlocks");
                if (st.ObserveSecrets.ContainsKey("chat:pass:" + key)) throw new Exception("the policy off didn't delete the saved passphrase");
                st.ChatAllowRemember = true;
                home.SelectForTest(_pc);
                await Settle(200);
                await home.UnlockForTest(MemberPass, save: false);
                if (_pc.Stage != ChatSession.Stages.Ready) throw new Exception("typed again: " + _pc.Stage);
                if (ChatPassphrases.IsSaved(key)) throw new Exception("saved without the tick");
                return $"ticked at the unlock: saved encrypted (no file shows it); Hello cancelled → locked, verified → unlocked; the app's unlock opened it with no second prompt ({fake.Prompts} Hello prompts in all); a refused one forgotten; the policy off deleted it";
            }
            finally
            {
                st.ChatAllowRemember = true;
                ChatPassphrases.Forget(key, _pc.Display, "the live run ended");
                ChatPassphrases.ProtectedForTest = false;
                DcxManager.Services.WindowsHello.Fake = null;
            }
        }

        private async Task<string> TidyAsync()
        {
            foreach (var t in main.Panes.AllTabs.Where(t => t.Content is ChatRoomView).ToList()) main.CloseTab(t);
            // Idle auto-lock (ChatAutoLockMinutes): 4 minutes idle against a 5-minute setting keeps it open, 6 locks it.
            var st = DcxManager.Services.AppSettings.Current;
            var mins = st.ChatAutoLockMinutes;
            try
            {
                st.ChatAutoLockMinutes = 0;
                if (ChatIdleLock.Check(TimeSpan.FromHours(5))) throw new Exception("auto-lock with the setting at 0 (off)");
                st.ChatAutoLockMinutes = 5;
                if (ChatIdleLock.Check(TimeSpan.FromMinutes(4)) || _pc.Engine is null) throw new Exception("auto-locked before the time");
                if (!ChatIdleLock.Check(TimeSpan.FromMinutes(6))) throw new Exception("no auto-lock after the time");
            }
            finally { st.ChatAutoLockMinutes = mins; }
            if (_pc.Engine is not null) throw new Exception("this PC's chat is still unlocked after the idle lock");
            ChatHub.LockAll("the live run ended");
            await _owner.DeleteRoomAsync(_owner.RoomById(_room.Id)!);
            await _ownerApi.CallAsync("admin/users/" + _member + "/delete", new JsonObject());
            await _pc.SignOutAsync(forgetPc: true);
            _owner.Lock();
            try { await _ownerApi.LogoutAsync(); } catch (ApiException) { }
            return "the room tab closed, idle auto-lock (5 min: open at 4, locked at 6) wiped this PC's chat keys, the room and the test account deleted on the server, both signed out";
        }
    }
}
