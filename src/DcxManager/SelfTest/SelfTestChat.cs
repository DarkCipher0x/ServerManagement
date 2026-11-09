using System.Text.Json.Nodes;
using System.Windows.Controls;
using DcxManager.Services;
using DcxManager.Views;

namespace DcxManager;

/// <summary>
/// ADR-117 (CipherChat built in). In every run: the switch's rule, its protection and policy, and — with CipherChat off
/// (the default, and the normal run) — that none of the chat code or CipherChat's libraries is loaded and the sidebar has
/// no chat section. With <c>-ChatOn</c> (DCX_CHAT_FEATURES=1): the module's contributions and the rules the screens rely
/// on (servers, accounts, admin checks, text cleaning, the PC's chat rules), with no server, window or dialog.
/// </summary>
internal static class ChatTests
{
    public static async Task RunAsync(MainWindow main, Action<string> ok, Action<string, Exception?> fail)
    {
        try
        {
            void Expect(bool? pol, string? env, bool set, bool on, ChatFeatures.From src)
            {
                var d = ChatFeatures.Decide(pol, env, set);
                if (d.On != on || d.Source != src) throw new Exception($"Decide({pol},{env},{set}) = {d.On}/{d.Source}, expected {on}/{src}");
            }
            Expect(false, "1", true, false, ChatFeatures.From.Policy);
            Expect(true, "0", false, true, ChatFeatures.From.Policy);
            Expect(null, "0", true, false, ChatFeatures.From.Environment);
            Expect(null, "1", false, true, ChatFeatures.From.Environment);
            Expect(null, "yes", true, true, ChatFeatures.From.Setting);
            Expect(null, null, false, false, ChatFeatures.From.Setting);
            if (new AppSettings().ChatFeatures) throw new Exception("a new install has CipherChat on");
            bool wanted = Environment.GetEnvironmentVariable(ChatFeatures.EnvVar) == "1";
            if (ChatFeatures.Current.On != wanted || ChatFeatures.Current.Source != ChatFeatures.From.Environment) throw new Exception("this run: " + ChatFeatures.Current);
            foreach (var key in new[] { "chat.features", "chat.admin" })
                if (ProtectedSettings.Items.FirstOrDefault(i => i.Key == key) is not { } item || item.Setting is null) throw new Exception("not admin-protected: " + key);
            foreach (var p in new[] { nameof(AppSettings.ChatFeatures), nameof(AppSettings.ChatAdmin), nameof(AppSettings.ChatAllowedServers), nameof(AppSettings.ChatAllowFiles),
                                      nameof(AppSettings.ChatMaxFileMB), nameof(AppSettings.ChatAllowCalls), nameof(AppSettings.ChatShareFromScreens), nameof(AppSettings.ChatToastPreviews),
                                      nameof(AppSettings.ChatNotify), nameof(AppSettings.ChatRingSound), nameof(AppSettings.ChatAutoLockMinutes), nameof(AppSettings.ChatAllowSessionShare) })
                if (!PolicySettings.Catalog.Any(e => e.Name == p)) throw new Exception("not a policy: " + p);
            // 0.6.39: CipherChat has a Settings page of its own (shown with chat on or off); the module adds "Chat accounts".
            if (!SettingsView.Categories.Any(c => c.Name == "CipherChat")) throw new Exception("no CipherChat settings page");
            foreach (var card in new[] { "CipherChat", "Chat rules on this PC", "Chat notifications & locking" })
                if (SettingsView.CategoryOfForTest(card) != "CipherChat") throw new Exception("card not on the CipherChat page: " + card);
            if (AppModules.SettingsCardsIn("CipherChat").Any(c => c.Title == "Chat accounts") != wanted) throw new Exception("the Chat accounts card follows the module");
            var entry = AuditLog.Read(DateTime.Now.ToString("yyyy-MM")).FirstOrDefault(e => e.Target == "chat" && e.Action.StartsWith("CipherChat module"));
            if (entry?.Action != (wanted ? "CipherChat module loaded" : "CipherChat module not loaded")) throw new Exception("start-up audit: " + (entry?.Action ?? "none"));
            ok($"CipherChat switch (ADR-117): machine policy beats DCX_CHAT_FEATURES beats the setting (new installs off); on/off and administration are admin-protected; 12 chat settings are Group Policies; a CipherChat Settings page with the switch, rules and notifications (plus Chat accounts from the module when on); this run {(wanted ? "loaded" : "didn't load")} the module and audited it");
        }
        catch (Exception ex) { fail("CipherChat switch", ex); }

        if (!ChatFeatures.Current.On)
        {
            try
            {
                var loaded = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name ?? "").Where(n => n == "Dcx.Chat" || n.StartsWith("CipherChat.", StringComparison.Ordinal)).ToList();
                if (loaded.Count > 0) throw new Exception("loaded with CipherChat off: " + string.Join(", ", loaded));
                if (AppModules.IsLoaded("chat") || AppModules.NavItemsIn("CHAT").Any()) throw new Exception("chat contributions with CipherChat off");
                if (KeyCommands().Any(c => c.StartsWith("chat.", StringComparison.Ordinal))) throw new Exception("chat shortcuts with CipherChat off");
                if (Services.DashboardSources.All.Any(s => s.Module == "chat") || Services.PrivacyRows.All.Any(p => p.Module == "chat")) throw new Exception("chat dashboard/privacy rows with CipherChat off");
                if (SendTo.Current != null || Menus.SendItem("probe", () => SendItem.OfText("probe", "x")) != null) throw new Exception("a send target with CipherChat off");
                if (AppModules.FindSidePanel("chat") != null) throw new Exception("a chat side panel with CipherChat off");
                ok("CipherChat off (ADR-117): Dcx.Chat and CipherChat's libraries are not loaded; no chat section, shortcut, dashboard source, privacy row, side panel or \"Send to a chat\" item exists");
            }
            catch (Exception ex) { fail("CipherChat off", ex); }
            return;
        }
        await ChatModuleTests.RunAsync(main, ok, fail);
    }

    private static IEnumerable<string> KeyCommands() => Services.Keys.KeyCommands.All.Select(c => c.Id);
}

/// <summary>The chat module's checks; only compiled (and Dcx.Chat only loaded) in the CipherChat-on run.</summary>
internal static class ChatModuleTests
{
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public static async Task RunAsync(MainWindow main, Action<string> ok, Action<string, Exception?> fail)
    {
        // 1. What the module contributes.
        try
        {
            if (!AppModules.IsLoaded("chat")) throw new Exception("module not registered");
            var nav = AppModules.NavItemsIn("CHAT").Select(n => n.Key).ToList();
            if (!nav.SequenceEqual(new[] { "chat", "chat-admin" })) throw new Exception("sidebar: " + string.Join(", ", nav));
            foreach (var id in new[] { "chat.open", "chat.admin", "chat.lock", "chat.panel" })
                if (Services.Keys.KeyCommands.Find(id) is null) throw new Exception("no command " + id);
            if (Services.Keys.KeyCommands.Text("chat.open") != "Ctrl+Shift+M") throw new Exception("chat.open key: " + Services.Keys.KeyCommands.Text("chat.open"));
            if (Services.Keys.KeyCommands.Problems.FirstOrDefault(p => p.Contains("chat", StringComparison.OrdinalIgnoreCase)) is { } clash) throw new Exception("shortcut clash: " + clash);
            if (!Services.DashboardSources.All.Any(s => s.Module == "chat" && s.Id == "chat")) throw new Exception("no dashboard source");
            if (Services.DashboardSources.All.First(s => s.Id == "chat").Host() is not null) throw new Exception("dashboard source listed with no chat account");
            // 0.6.38: contacts (listed only with a chat unlocked) and the server you administer (only while signed in to it).
            foreach (var id in new[] { "chat-people", "chat-admin" })
                if (Services.DashboardSources.All.FirstOrDefault(x => x.Id == id) is not { Module: "chat" } src) throw new Exception("no dashboard source " + id);
                else if (src.Host() is not null) throw new Exception(id + " listed with nothing unlocked / no admin sign-in");
            if (Chat.ChatModule.TrustTile(new(3, 0, 1)) is not { Alert: true, Value: "⚠ 1" } || Chat.ChatModule.TrustTile(new(3, 2, 0)) is not { Alert: false, Value: "2" }
                || Chat.ChatModule.TrustTile(new(4, 0, 0)) is not { Value: "4", Detail: "people verified" } || !Chat.ChatModule.TrustText(new(0, 0, 2)).StartsWith("2 key change"))
                throw new Exception("contacts tile");
            if (Chat.ChatModule.AdminTile(new CheckOutcome("3", null)) is not { Value: "3", Alert: true } || Chat.ChatModule.AdminTile(new CheckOutcome("0", null)) is not { Alert: false }
                || Chat.ChatModule.AdminTile(new CheckOutcome("-", null)).Detail != "you can't review sign-up requests here" || Chat.ChatModule.AdminTile(new CheckOutcome(null, "offline")) is not { Alert: true, Detail: "offline" }
                || Chat.ChatModule.AdminTile(null).Value != "…") throw new Exception("server tile");
            if (!Services.PrivacyRows.All.Any(p => p.Module == "chat")) throw new Exception("no privacy rows");
            var (_, startup, shutdown) = AppModules.HookCounts;
            if (startup < 1 || shutdown < 1) throw new Exception("start-up / shut-down hooks");
            ok("chat module (ADR-117): CHAT sidebar section with Chats and Chat server admin, commands chat.open (Ctrl+Shift+M, no clash) / chat.admin / chat.lock, dashboard sources (unread, listed only with an account; 0.6.38: contacts with a changed key as an alert, and sign-up requests while signed in to a server's administration), privacy rows, start-up resume (never run in the self-test) and shut-down lock");
        }
        catch (Exception ex) { fail("chat module", ex); }

        // 2. Servers and stored accounts: https only (loopback http only in tests), the organisation's list, no user info.
        var s = AppSettings.Current;
        var keepServers = s.ChatAllowedServers.ToList();
        try
        {
            void Allowed(string a, bool yes) { if ((Chat.ChatServers.Check(a) == null) != yes) throw new Exception($"{a}: {(yes ? "refused" : "allowed")} ({Chat.ChatServers.Check(a)})"); }
            Allowed("https://chat.example.com", true);
            Allowed("https://chat.example.com:8443/chat/", true);
            Allowed("http://chat.example.com", false);
            Allowed("http://192.0.2.10", false);
            Allowed("http://127.0.0.1:8765", true);          // CipherChat's dev mode, self-test only
            Allowed("ftp://chat.example.com", false);
            Allowed("https://user:pw@chat.example.com", false);
            Allowed("not a server", false);
            s.ChatAllowedServers = ["https://chat.example.com"];
            Allowed("https://chat.example.com", true);
            Allowed("https://other.example.net", false);
            s.ChatAllowedServers = keepServers;
            var good = new Chat.ChatAccount("https://chat.example.com", "alice", "dev1", "tok", "chat.example.com", DateTime.UtcNow);
            if (!Chat.ChatAccounts.Valid(good)) throw new Exception("a good account is refused");
            if (Chat.ChatAccounts.Valid(good with { Origin = "http://chat.example.com" })) throw new Exception("http account accepted");
            if (Chat.ChatAccounts.Valid(good with { DeviceToken = "" }) || Chat.ChatAccounts.Valid(good with { DeviceToken = new string('x', 513) })) throw new Exception("token length");
            if (Chat.ChatAccounts.Valid(good with { User = "" })) throw new Exception("empty user");
            ok("chat servers (ADR-117): https:// only (http only for a loopback dev server in tests), never user info, the organisation's server list enforced; a stored account is used only when its server, user and token pass the same rules");
        }
        catch (Exception ex) { fail("chat servers", ex); }
        finally { s.ChatAllowedServers = keepServers; }

        // 3. Administration: changes are checked against the server's own schema and formats before anything is sent;
        // one-time links are shown only when they are the server's own sign-in links.
        try
        {
            var schema = new[]
            {
                new Chat.Admin.AdminSettingSpec("invite_hours", "int", 1, 720, 72),
                new Chat.Admin.AdminSettingSpec("access_requests", "bool", 0, 0, true),
                new Chat.Admin.AdminSettingSpec("name", "str", 0, 60, ""),
            };
            var body = Chat.Admin.ChatAdminClient.CheckSettings(schema, new Dictionary<string, JsonNode> { ["invite_hours"] = 48, ["access_requests"] = false, ["name"] = "Ops" });
            if (body.Count != 3 || (long)body["invite_hours"]! != 48) throw new Exception("valid settings: " + body.ToJsonString());
            void Refused(string what, Action a) { try { a(); } catch (ArgumentException) { return; } throw new Exception("accepted: " + what); }
            Refused("int out of range", () => Chat.Admin.ChatAdminClient.CheckSettings(schema, new Dictionary<string, JsonNode> { ["invite_hours"] = 721 }));
            Refused("int as text", () => Chat.Admin.ChatAdminClient.CheckSettings(schema, new Dictionary<string, JsonNode> { ["invite_hours"] = "48" }));
            Refused("bool as number", () => Chat.Admin.ChatAdminClient.CheckSettings(schema, new Dictionary<string, JsonNode> { ["access_requests"] = 1 }));
            Refused("text too long", () => Chat.Admin.ChatAdminClient.CheckSettings(schema, new Dictionary<string, JsonNode> { ["name"] = new string('x', 61) }));
            Refused("unknown setting", () => Chat.Admin.ChatAdminClient.CheckSettings(schema, new Dictionary<string, JsonNode> { ["evil"] = 1 }));
            Chat.Admin.ChatAdminClient.CheckUser("alice.ops");
            Refused("short username", () => Chat.Admin.ChatAdminClient.CheckUser("al"));
            Refused("upper-case username", () => Chat.Admin.ChatAdminClient.CheckUser("Alice"));
            Chat.Admin.ChatAdminClient.CheckRole("on-call");
            Refused("role with _", () => Chat.Admin.ChatAdminClient.CheckRole("on_call"));
            Chat.Admin.ChatAdminClient.CheckPerm("admin.users.view");
            Refused("bad permission id", () => Chat.Admin.ChatAdminClient.CheckPerm("admin users"));
            var client = new Chat.Admin.ChatAdminClient();
            client.SetOriginForTest("https://chat.example.com");
            if (!client.IsOwnLink("https://chat.example.com/chat/login/#invite=abc")) throw new Exception("own link refused");
            foreach (var bad in new[] { "https://evil.example.net/chat/login/#x", "https://chat.example.com/other", "https://u@chat.example.com/chat/login/", "javascript:alert(1)" })
                if (client.IsOwnLink(bad)) throw new Exception("link shown: " + bad);
            if (client.SignedIn) throw new Exception("a new admin client is signed in");
            s.ChatAdmin = false;
            bool blocked = Chat.Admin.ChatAdminClient.Blocked() != null;
            s.ChatAdmin = true;
            if (!blocked) throw new Exception("policy ChatAdmin=0 doesn't block administration");
            ok("chat administration (ADR-117 §7a): settings checked against the server's schema (kind, range, length, unknown keys), usernames / roles / permission ids by CipherChat's own formats, one-time links shown only for the server's /chat/login/ links, ChatAdmin=0 blocks it");
        }
        catch (Exception ex) { fail("chat administration", ex); }
        finally { s.ChatAdmin = true; }

        // 4. The PC's chat rules and the text the screens draw.
        var keep = (s.ChatAllowFiles, s.ChatMaxFileMB, s.ChatAllowCalls, s.ChatShareFromScreens, s.ChatToastPreviews);
        try
        {
            s.ChatAllowFiles = false; s.ChatAllowCalls = false; s.ChatShareFromScreens = false;
            if (Chat.ChatPolicy.FilesBlocked() is null || Chat.ChatPolicy.CallsBlocked() is null || Chat.ChatPolicy.ShareBlocked() is null) throw new Exception("rules off but not blocked");
            s.ChatAllowFiles = true; s.ChatAllowCalls = true; s.ChatShareFromScreens = true;
            if (Chat.ChatPolicy.FilesBlocked() is not null || Chat.ChatPolicy.CallsBlocked() is not null || Chat.ChatPolicy.ShareBlocked() is not null) throw new Exception("rules on but blocked");
            s.ChatMaxFileMB = 500; if (Chat.ChatPolicy.MaxFileBytes != 100L << 20) throw new Exception("size not capped at 100 MB");
            s.ChatMaxFileMB = 0; if (Chat.ChatPolicy.MaxFileBytes != 1L << 20) throw new Exception("size not at least 1 MB");
            s.ChatToastPreviews = false;
            if (Chat.ChatPolicy.ToastText("bob", "#ops", "the secret text").Contains("secret")) throw new Exception("notification shows text with previews off");
            s.ChatToastPreviews = true;
            var t = Chat.ChatPolicy.ToastText("bob", "#ops", new string('a', 300));
            if (!t.StartsWith("bob in #ops: ") || t.Length > 140) throw new Exception("preview: " + t);
            var cleaned = Chat.Views.ChatRoomView.Clean("line1\nline2\ttab\u0007\u001b[31m‮red");
            if (cleaned.Any(c => c is '\u0007' or '\u001b') || !cleaned.Contains('\n') || !cleaned.Contains('\t')) throw new Exception("clean: " + cleaned);
            if (Chat.Views.ChatRoomView.Clean(new string('x', 30_000)).Length != 20_000) throw new Exception("clean doesn't cap the length");
            ok("chat rules (ADR-117): files, calls and sharing follow their settings/policies; file size clamped to 1–100 MB; notifications show only who and where unless previews are on (then at most 120 characters); message text loses control characters (escape sequences, bells) and is capped at 20,000 characters");
        }
        catch (Exception ex) { fail("chat rules", ex); }
        finally { (s.ChatAllowFiles, s.ChatMaxFileMB, s.ChatAllowCalls, s.ChatShareFromScreens, s.ChatToastPreviews) = keep; }

        // 5. The screens open as tabs from the sidebar with nothing signed in (no dialog, no connection), and lock clears.
        try
        {
            int before = main.Panes.AllTabs.Count();
            main.Navigate("chat");
            await Task.Delay(400);
            if (main.Panes.ActiveTab is not TabItem { Content: Chat.Views.ChatHomeView }) throw new Exception("Chats didn't open as the active tab");
            main.Navigate("chat-admin");
            await Task.Delay(400);
            if (main.Panes.ActiveTab is not TabItem { Content: Chat.Views.ChatAdminView }) throw new Exception("Chat server admin didn't open");
            main.Navigate("chat");
            if (main.Panes.AllTabs.Count() != before + 2) throw new Exception("Chats isn't a singleton tab");
            AppLockEvents.RaiseLocking("self-test");
            if (Chat.ChatHub.Admin.SignedIn || Chat.ChatHub.Sessions.Any(x => x.Engine != null)) throw new Exception("lock left a chat unlocked");
            foreach (var tab in main.Panes.AllTabs.Where(x => x.Content is Chat.Views.ChatHomeView or Chat.Views.ChatAdminView).ToList()) main.CloseTab(tab);
            ok("chat screens (ADR-117): Chats and Chat server admin open from the sidebar as singleton tabs with nothing signed in (add-account and admin sign-in forms; no dialog, no connection); the app lock reaches the chat hub");
        }
        catch (Exception ex) { fail("chat screens", ex); }

        // 6. The side panel: the app's panel column shows the chat panel beside the documents, toggles, and keeps the
        // documents' room (at most 40 % of the window).
        try
        {
            if (AppModules.FindSidePanel("chat") is not { Module: "chat" }) throw new Exception("no chat side panel contributed");
            if (MainWindow.FitPanelWidth(1000, 1400) != 560 || MainWindow.FitPanelWidth(100, 2000) != MainWindow.PanelMinWidth || MainWindow.FitPanelWidth(2000, 4000) != MainWindow.PanelMaxWidth)
                throw new Exception("panel width fitting");
            var col = (ColumnDefinition)main.FindName("PanelCol");
            var host = (System.Windows.Controls.Border)main.FindName("PanelHost");
            ModulePanels.Show("chat");
            await Task.Delay(300);
            if (ModulePanels.Shown() != "chat" || host.Visibility != System.Windows.Visibility.Visible || host.Tag is not Chat.Views.ChatPanelView || col.Width.Value < MainWindow.PanelMinWidth)
                throw new Exception($"panel not shown: {ModulePanels.Shown()} / {host.Visibility} / {col.Width}");
            var text = string.Join(" ", Menus.Descendants<TextBlock>(host).Select(t => t.Text));
            if (!text.Contains("No chat is unlocked")) throw new Exception("panel without a chat: " + text);
            // 0.6.38: kept for the next start (encrypted settings): open or not, its width, the pinned room.
            if (s.SidePanelShown != "chat" || s.SidePanelWidth < MainWindow.PanelMinWidth) throw new Exception($"open panel not remembered: “{s.SidePanelShown}” {s.SidePanelWidth}");
            var pinWas = s.ChatPanelRoom;
            s.ChatPanelRoom = "chat:https://chat.example.test|alice|r1";
            if (Chat.Views.ChatPanelView.Pinned != s.ChatPanelRoom) throw new Exception("the pinned room isn't read from the settings");
            s.ChatPanelRoom = pinWas;
            ModulePanels.Toggle("chat");
            if (ModulePanels.Shown() != null || col.Width.Value != 0 || host.Visibility != System.Windows.Visibility.Collapsed) throw new Exception("panel not closed");
            if (s.SidePanelShown != "") throw new Exception("closed panel still remembered as open");
            Services.Keys.KeyCommands.Find("chat.panel")!.Run!();
            if (ModulePanels.Shown() != "chat") throw new Exception("chat.panel didn't show it");
            Services.Keys.KeyCommands.Find("chat.panel")!.Run!();
            if (ModulePanels.Shown() != null) throw new Exception("chat.panel didn't hide it");
            ok("chat side panel (ADR-117): the app's panel column (between the documents and the AI dock) shows the chat panel (no chat unlocked: it offers Chats), chat.panel (Ctrl+Shift+J) toggles it, closing gives the column back; width 260–720 and at most 40 % of the window; 0.6.38: open or not, its width and the pinned room are kept in the encrypted settings");
        }
        catch (Exception ex) { fail("chat side panel", ex); ModulePanels.Hide(); }

        // 7. Send to a chat: the screens' items exist only with the target, follow ChatShareFromScreens, and what goes into
        // the room follows the PC's rules (long text as a file, files off / too big dropped with a note, safe names).
        var shareWas = s.ChatShareFromScreens;
        try
        {
            if (SendTo.Current is not Chat.Share.ChatSendTarget) throw new Exception("send target: " + SendTo.Current?.GetType().Name);
            s.ChatShareFromScreens = true;
            if (Menus.SendItem("💬 Send to a chat…", () => SendItem.OfText("probe", "x")) is not { IsEnabled: true }) throw new Exception("item missing or disabled");
            s.ChatShareFromScreens = false;
            if (Menus.SendItem("💬 Send to a chat…", () => SendItem.OfText("probe", "x")) is not { IsEnabled: false, ToolTip: string tip } || !tip.Contains("turned off")) throw new Exception("policy off but the item is usable");
            s.ChatShareFromScreens = true;
            var shot = AppModules.MenuItems("screenshot", new ScreenshotMenuContext(() => Task.FromResult<System.Windows.Media.Imaging.BitmapSource>(null!), "web-01 terminal", null))
                .OfType<MenuItem>().Select(m => m.Header as string).ToList();
            if (!shot.Contains("💬 Send screenshot to a chat…")) throw new Exception("screenshot menu: " + string.Join(", ", shot));
            var big = new string('x', Chat.Share.ChatSendTarget.MaxText + 10);
            var (t1, f1, n1) = Chat.Share.ChatSendTarget.Prepare(SendItem.OfText("web-01: output", big), true, 1L << 20);
            if (t1.Length != 0 || f1.Count != 1 || f1[0].Name != "web-01- output.txt" || n1.Count != 1) throw new Exception($"long text with files on: {t1.Length} chars, {f1.Count} files {f1.FirstOrDefault()?.Name}");
            var (t2, f2, _) = Chat.Share.ChatSendTarget.Prepare(SendItem.OfText("o", big), false, 1L << 20);
            if (f2.Count != 0 || t2.Length != Chat.Share.ChatSendTarget.MaxText + 2) throw new Exception("long text with files off: " + t2.Length);
            var file = new SendFile("..\\evil/name.log", new byte[10]);
            var (_, f3, n3) = Chat.Share.ChatSendTarget.Prepare(new SendItem("s", "hi", new[] { file, new SendFile("big.bin", new byte[2048]) }), true, 1024);
            if (f3.Count != 1 || f3[0].Name.IndexOfAny(new[] { '\\', '/' }) >= 0 || n3.Count != 1 || !n3[0].Contains("big.bin")) throw new Exception("files: " + string.Join(", ", f3.Select(f => f.Name)) + " / " + string.Join(" ", n3));
            var (_, f4, n4) = Chat.Share.ChatSendTarget.Prepare(new SendItem("s", "hi", new[] { file }), false, 1024);
            if (f4.Count != 0 || n4.Count != 1) throw new Exception("files off");
            if (Chat.Share.ChatSendTarget.SafeName(new string('a', 120) + ".png", keepExtension: true) is not { Length: 80 } n || !n.EndsWith(".png")) throw new Exception("long name");
            ok("send to a chat (ADR-117): the screens' \"💬 Send … to a chat\" items and the 📷 menus' \"Send screenshot to a chat…\" come from the chat's target and follow ChatShareFromScreens; long text goes as a .txt file (or is cut when files are off), files that are off or too big are dropped with a note, names lose path characters; the room then opens for review and nothing is sent without Send");
        }
        catch (Exception ex) { fail("send to a chat", ex); }
        finally { s.ChatShareFromScreens = shareWas; }

        // 8. Pictures (0.6.38): a picture sent from here carries a JPEG thumbnail and its size, as CipherChat's own app sends
        // them (so it shows inline on every client); only PNG / JPEG / GIF bytes count, whatever the name says.
        try
        {
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(800, 400, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            var dv = new System.Windows.Media.DrawingVisual();
            using (var dc = dv.RenderOpen()) dc.DrawRectangle(System.Windows.Media.Brushes.Teal, null, new System.Windows.Rect(0, 0, 800, 400));
            rtb.Render(dv);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            byte[] png;
            using (var ms = new System.IO.MemoryStream()) { enc.Save(ms); png = ms.ToArray(); }
            var pic = Chat.Views.ChatPictures.Outgoing("web-01 terminal.png", png, "image/png");
            if (pic.Thumb is not { } thumb || pic.W != 800 || pic.H != 400 || CipherChat.Crypto.Files.Sniff(thumb) != "image/jpeg") throw new Exception($"picture: thumb {pic.Thumb?.Length} {pic.W}x{pic.H}");
            var shown = Chat.Views.ChatPictures.TryDecode(thumb, 0) ?? throw new Exception("thumbnail doesn't decode");
            if (Math.Max(shown.PixelWidth, shown.PixelHeight) > Chat.Views.ChatPictures.ThumbMax) throw new Exception($"thumbnail {shown.PixelWidth}x{shown.PixelHeight}");
            var fake = Chat.Views.ChatPictures.Outgoing("notes.png", System.Text.Encoding.UTF8.GetBytes("not a picture"));
            if (fake.Thumb != null || fake.W != null || Chat.Views.ChatPictures.TryDecode(fake.Bytes, 100) != null) throw new Exception("a text file named .png was treated as a picture");
            var log = Chat.Views.ChatPictures.Outgoing("output.log", new byte[] { 0x89, 0x50, 0x4e, 0x47, 1, 2, 3 });
            if (log.Thumb != null) throw new Exception("broken picture bytes got a thumbnail");
            ok("chat pictures (0.6.38): a picture sent from here (a screenshot) carries a JPEG thumbnail of at most 360 px and its size, as CipherChat's own app sends them; only PNG / JPEG / GIF bytes are decoded, whatever the name (a .png that isn't one, or broken picture bytes, go as plain files)");
        }
        catch (Exception ex) { fail("chat pictures", ex); }

        // 9. Calls (0.6.38): CipherChat's own call page, embedded and served from memory to a locked-down browser; the page
        // may ask only for call operations; calls follow ChatAllowCalls; nothing rings or opens in the self-test.
        var callsWas = s.ChatAllowCalls;
        System.Windows.Window? probe = null;
        try
        {
            var files = Chat.Calls.CallPage.Files;
            foreach (var need in new[] { "call.html", "host.js", "host.css", "app/call.js", "app/callui.js", "app/callworker.js", "app/crypto.js", "app/chat.css", "app/vendor/post-quantum/ml-kem.js", "app/vendor/hashes/sha3.js" })
                if (!files.ContainsKey(need)) throw new Exception("call page file not embedded: " + need + " (have " + files.Count + ")");
            if (Chat.Calls.CallPage.Get("call.html") is not { Mime: "text/html; charset=utf-8" } html || !System.Text.Encoding.UTF8.GetString(html.Bytes).Contains("connect-src 'none'"))
                throw new Exception("call.html or its policy");
            foreach (var bad in new[] { "../call.html", "app\\call.js", "app/../../x", "nope.js", "", "/" })
                if (Chat.Calls.CallPage.Get(bad) != null) throw new Exception("served: " + bad);
            if (Chat.Calls.CallWindow.ApiPath("calls/signal") != "calls/signal" || Chat.Calls.CallWindow.ApiPath("calls/turn") != "calls/turn") throw new Exception("call paths");
            foreach (var path in new[] { "admin/users", "rooms/send", "calls/signal/../admin", null })
            {
                try { Chat.Calls.CallWindow.ApiPath(path); throw new Exception("the page may use " + path); }
                catch (InvalidOperationException) { }
            }
            s.ChatAllowCalls = false;
            var idle = new Chat.ChatSession();
            if (Chat.Calls.CallWindow.WhyNot(idle) is not { } off || !off.Contains("turned off")) throw new Exception("calls off: " + Chat.Calls.CallWindow.WhyNot(idle));
            s.ChatAllowCalls = true;
            if (Chat.Calls.CallWindow.WhyNot(idle) is null) throw new Exception("a call could start without an unlocked chat");
            if (Chat.Calls.RingPopup.Clean("ali\u202Ece\u0007") != "alice" || Chat.Calls.RingPopup.Clean(new string('x', 120)).Length != 81) throw new Exception("names in the ring");
            using (var tone = Chat.Calls.RingPopup.Ringtone()) if (tone.Length != 44 + 22050 * 2 * 2) throw new Exception("ringtone " + tone.Length);
            if (!Chat.Calls.CallPage.ProfileFolder.EndsWith("-webview2-calls", StringComparison.Ordinal)) throw new Exception("the self-test's call browser profile: " + Chat.Calls.CallPage.ProfileFolder);

            // The page itself, in an off-screen browser (no call is made): every module of it loads under its policy and it
            // says it's ready; anything outside the page is refused.
            string heard = "";
            if (Chat.Calls.CallPage.RuntimeAvailable())
            {
                var web = new Microsoft.Web.WebView2.Wpf.WebView2();
                probe = new System.Windows.Window { Width = 480, Height = 320, Left = -10000, Top = -10000, WindowStartupLocation = System.Windows.WindowStartupLocation.Manual, ShowInTaskbar = false, ShowActivated = false, WindowStyle = System.Windows.WindowStyle.None, Content = web };
                probe.Show();
                await web.EnsureCoreWebView2Async(await Chat.Calls.CallPage.EnvironmentAsync());
                var core = web.CoreWebView2;
                Chat.Calls.CallPage.Lock(core.Settings);
                Chat.Calls.CallPage.Map(core);
                var ready = new TaskCompletionSource<string>();
                core.WebMessageReceived += (_, e) => { try { if (JsonNode.Parse(e.WebMessageAsJson)?["event"]?.GetValue<string>() == "ready") ready.TrySetResult(e.Source); } catch (System.Text.Json.JsonException) { } };
                core.Navigate(Chat.Calls.CallPage.Origin + "/" + Chat.Calls.CallPage.Start);
                if (await Task.WhenAny(ready.Task, Task.Delay(20000)) != ready.Task) throw new Exception("the call page didn't say it's ready (a module didn't load?)");
                if (!ready.Task.Result.StartsWith(Chat.Calls.CallPage.Origin + "/", StringComparison.Ordinal)) throw new Exception("ready from " + ready.Task.Result);
                // ExecuteScriptAsync doesn't wait for a promise: DevTools' evaluate does (awaitPromise), and gives the value back.
                async Task<string> Eval(string js) => JsonNode.Parse(await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", new JsonObject { ["expression"] = js, ["awaitPromise"] = true, ["returnByValue"] = true }.ToJsonString()))?["result"]?["value"]?.ToString() ?? "(nothing)";
                var outside = await Eval("fetch('https://198.51.100.7/x').then(() => 'reached', () => 'refused')");
                if (outside != "refused") throw new Exception("the call page reached outside: " + outside);
                var other = await Eval("fetch('/../../Windows/win.ini').then(r => String(r.status), () => 'refused')");
                if (other is not ("refused" or "404")) throw new Exception("the call page read another path: " + other);
                heard = "; the page loads in an off-screen browser (every module under its CSP) and says it's ready, and can't reach anything outside";
                web.Dispose();
            }
            else heard = "; the WebView2 runtime isn't installed here, so the page wasn't loaded";
            ok("chat calls (0.6.38): CipherChat's own call page (its call engine, screen and libraries) is embedded and served from memory — call.html with its no-fetch policy, nothing outside the page, no path tricks; the page may ask only for calls/signal and calls/turn; calls follow ChatAllowCalls and need an unlocked chat; caller names are cleaned (no control or direction characters); the ringtone is made in memory; nothing rings or opens in the self-test" + heard);
        }
        catch (Exception ex) { fail("chat calls", ex); }
        finally { s.ChatAllowCalls = callsWas; try { probe?.Close(); } catch { } }
    }
}
