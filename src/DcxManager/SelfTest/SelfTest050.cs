using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DcxManager.Browser;
using DcxManager.Services;
using DcxManager.Views;
using Microsoft.Win32;

namespace DcxManager;

/// <summary>
/// 0.5.0 fixes from DarkCipher0x's 0.4.0 feedback (docs/PLAN-0.5.0.md): screens that follow their data while cached, dropdowns that
/// show names instead of type names, settings that say when Group Policy set them or an administrator must approve them,
/// and the About title. No window, prompt or dialog; policies come from a made-up source.
/// </summary>
public static class Release050Tests
{
    public static async Task RunAsync(MainWindow main, Action<string> ok, Action<string, Exception?> fail)
    {
        async Task Settle(int ms = 200)
        {
            await Task.Delay(ms);
            await Application.Current.Dispatcher.InvokeAsync(() => main.UpdateLayout(), DispatcherPriority.ApplicationIdle);
        }

        // 1. A cached screen follows its data after it was hidden and shown again (#12: "it doesn't show until you leave and
        //    come back"). The old Portals screen unsubscribed on its first Unloaded and never subscribed again.
        PortalBookmark? added = null;
        try
        {
            main.Navigate("portals"); await Settle(300);
            main.Navigate("hosts"); await Settle(300);
            main.Navigate("portals"); await Settle(300);
            var pv = PortalsView.Current ?? throw new Exception("no Portals screen");
            int before = pv.RowCountForTest;
            added = new PortalBookmark { Title = "selftest live portal", Url = "https://192.0.2.10/" };
            Portals.Upsert(added);
            await Settle(400);
            if (pv.RowCountForTest != before + 1) throw new Exception($"the shown list has {pv.RowCountForTest} rows, expected {before + 1}");
            ok("live screens (#12): a portal added while the cached Portals screen is shown again after a tab switch appears at once (Live.While subscribes on every Loaded and refreshes); File Commander's host list, Ansible's nodes and inventory and the Toolbox's host pickers use the same helper, and a local Commander folder follows changes on disk");
        }
        catch (Exception ex) { fail("live screens (#12)", ex); }
        finally { if (added != null) Portals.Delete(added); }

        // 2. Dropdowns show names (#3): the theme's ComboBox template passes DisplayMemberPath on to the selection box.
        try
        {
            main.Navigate("settings"); await Settle(400);
            var sv = SettingsView.Current ?? throw new Exception("no settings view");
            var typeName = new Regex(@"^(DcxManager|Dcx)\.[A-Za-z]+(\.[A-Za-z]+)+$");
            int checkedBoxes = 0;
            var bad = new List<string>();
            foreach (var tab in sv.TabsForTest)
            {
                sv.RevealCategory(tab); await Settle(250);
                foreach (var cb in VisualDescendants<ComboBox>(sv).Where(c => c.IsVisible && c.SelectedItem != null))
                {
                    checkedBoxes++;
                    foreach (var t in VisualDescendants<TextBlock>(cb))
                        if (typeName.IsMatch(t.Text.Trim())) bad.Add($"{tab}: {t.Text.Trim()}");
                }
            }
            if (checkedBoxes == 0) throw new Exception("no dropdown checked");
            if (bad.Count > 0) throw new Exception("type names shown: " + string.Join(", ", bad.Distinct()));
            if (new Sessions.TerminalProfile { Name = "Ops" }.ToString() != "Ops") throw new Exception("TerminalProfile.ToString");
            ok($"dropdowns (#3): {checkedBoxes} dropdown(s) on every Settings tab show their item names (the terminal profile picker no longer shows DcxManager.Sessions.TerminalProfile); the ComboBox template forwards ItemTemplateSelector");
        }
        catch (Exception ex) { fail("dropdowns (#3)", ex); }

        // 3. Settings say what Group Policy set and what needs an administrator (#5).
        try
        {
            PolicySettings.SourceForTest = hive => hive == "HKLM"
                ? new Dictionary<string, (object, RegistryValueKind)> { ["SyslogHost"] = ("syslog.example.test", RegistryValueKind.String) }
                : null;
            PolicySettings.Reload();
            if (main.Panes.AllTabs.FirstOrDefault(t => t.Content is SettingsView) is { } st0) main.CloseTab(st0);
            main.Navigate("settings"); await Settle(400);
            var sv = SettingsView.Current ?? throw new Exception("no settings view");
            sv.RevealCard("Syslog (TLS)"); await Settle(400);
            var card = sv.CardForTest("Syslog (TLS)") ?? throw new Exception("no syslog card");
            var host = Menus.Descendants<TextBox>(card).FirstOrDefault(t => SettingsView.GetSetting(t) == "SyslogHost") ?? throw new Exception("the syslog host field isn't bound to its setting");
            if (host.IsEnabled) throw new Exception("a policy-set field is still editable");
            var text = string.Join(" ", Menus.Descendants<TextBlock>(card).Select(t => t.Text));
            if (!text.Contains("🔒 Set by your organisation")) throw new Exception("no policy line on the card");
            if (!text.Contains("🛡 Needs admin approval")) throw new Exception("no admin-approval line on the card");
            var port = Menus.Descendants<TextBox>(card).First(t => SettingsView.GetSetting(t) == "SyslogPort");
            if (!port.IsEnabled || port.ToolTip is not string tip || !tip.StartsWith("🛡")) throw new Exception("the port isn't marked as admin-protected");

            var bound = sv.BoundSettingsForTest();
            var protectedUnbound = ProtectedSettings.Items.Select(i => i.Setting).Where(n => n.Length > 0 && !bound.Contains(n)).Distinct().ToList();
            if (protectedUnbound.Count > 0) throw new Exception("protected settings no Settings control is bound to: " + string.Join(", ", protectedUnbound));
            int catalog = PolicySettings.Catalog.Count, shown = PolicySettings.Catalog.Count(e => bound.Contains(e.Name));
            ok($"policy locks (#5): a field set by Group Policy is disabled with who set it, the card says “🔒 Set by your organisation”, admin-protected fields carry 🛡 and the card lists them; every one of the {ProtectedSettings.Items.Count} protected settings has a bound control ({shown} of {catalog} policies are bound to a Settings control)");
        }
        catch (Exception ex) { fail("policy locks (#5)", ex); }
        finally
        {
            PolicySettings.SourceForTest = _ => null;
            PolicySettings.Reload();
            if (main.Panes.AllTabs.FirstOrDefault(t => t.Content is SettingsView) is { } st1) main.CloseTab(st1);
        }

        // 4. Drag and drop (#1, #11): folders move with their sub-folders and hosts, never into themselves; dashboard cards
        //    slide to their new places (positions captured before, played after).
        var dragHosts = new List<HostEntry>();
        try
        {
            main.Navigate("hosts"); await Settle(300);
            var hv = main.Panes.AllTabs.Select(t => t.Content).OfType<HostsView>().FirstOrDefault() ?? throw new Exception("no Hosts screen");
            dragHosts.Add(new HostEntry { Name = "dd-1", Address = "192.0.2.150", Group = "ddtest/a" });
            dragHosts.Add(new HostEntry { Name = "dd-2", Address = "192.0.2.151", Group = "ddtest/a/b" });
            AppData.Current.Hosts.AddRange(dragHosts); AppData.Save();
            if (hv.MoveFolder("ddtest/a", "ddtest/a/b/c")) throw new Exception("a folder moved inside itself");
            if (!hv.MoveFolder("ddtest/a", "ddtest/z/a")) throw new Exception("folder move refused");
            if (dragHosts[0].Group != "ddtest/z/a" || dragHosts[1].Group != "ddtest/z/a/b") throw new Exception($"groups now {dragHosts[0].Group}, {dragHosts[1].Group}");

            main.Navigate("dashboard"); await Settle(500);
            var dv = main.Panes.AllTabs.Select(t => t.Content).OfType<DashboardView>().FirstOrDefault() ?? throw new Exception("no dashboard");
            var placed = dv.VisibleOrderForTest.ToList();
            if (placed.Count >= 2)
            {
                var order = AppSettings.Current.DashboardOrder.ToList();
                dv.SetUnlocked(true);
                dv.DropCard(placed[1], placed[0]);
                await Settle(100);
                if (dv.VisibleOrderForTest[0] != placed[1]) throw new Exception("drop didn't reorder: " + string.Join(",", dv.VisibleOrderForTest.Take(3)));
                dv.SetUnlocked(false);
                AppSettings.Current.DashboardOrder = order; AppSettings.Save();
            }
            ok("drag and drop (#1, #11): a folder dragged onto another moves with its sub-folders and hosts (never into itself); a dashboard card dropped on another takes its place and the cards slide there; while dragging, a picture of the card / the hosts / the folder follows the cursor and the target is outlined (DragGhost)");
        }
        catch (Exception ex) { fail("drag and drop (#1, #11)", ex); }
        finally { foreach (var h in dragHosts) AppData.Current.Hosts.Remove(h); AppData.Current.Folders.RemoveAll(f => f.StartsWith("ddtest")); AppData.Save(); }

        // Pingdom (#17): a fake API (no network); the token is a made-up one.
        {
            var fake = new FakePingdom();
            var s0 = AppSettings.Current;
            var hadToken = s0.ObserveSecrets.GetValueOrDefault(Notify.PingdomClient.SecretKey);
            DashboardTile? tile = null;
            try
            {
                Notify.PingdomClient.HandlerForTest = fake; Notify.PingdomClient.ResetForTest();
                s0.ObserveSecrets[Notify.PingdomClient.SecretKey] = "selftest-token";
                var list = await Notify.PingdomClient.ChecksAsync();
                if (list.Count != 2 || list[0].Name != "API" || list[1].Status != "down" || list[0].Tags.Length != 1) throw new Exception("checks list");
                if (fake.LastAuth != "Bearer selftest-token" || fake.LastHost != "api.pingdom.com" || fake.LastMethod != "GET") throw new Exception("request: " + fake.LastAuth + " " + fake.LastHost);
                tile = Checks.Pin(new DashboardTile { Kind = "pingdom", Name = "API", Target = "85975" });
                await Checks.RunAsync(tile);
                if (tile.LastOk != true || tile.LastValue != "up · 355 ms") throw new Exception("pinned check: " + tile.LastValue + " " + tile.LastDetail);
                fake.Status = 401;
                try { await Notify.PingdomClient.ChecksAsync(); throw new Exception("401 accepted"); } catch (InvalidOperationException ex) when (ex.Message.Contains("refused the token")) { }
                if (Notify.PingdomClient.Healthy("paused") != null || Notify.PingdomClient.Healthy("unconfirmed_down") != false) throw new Exception("status mapping");
                ok("Pingdom (#17): checks read from api.pingdom.com/api/3.1 with the read-only token as a Bearer header (GET only), a Pingdom check pinned to the dashboard shows Pingdom's state and response time on the dashboard's schedule, a refused token is explained, paused checks aren't judged");
            }
            catch (Exception ex) { fail("Pingdom (#17)", ex); }
            finally
            {
                Notify.PingdomClient.HandlerForTest = null; Notify.PingdomClient.ResetForTest();
                if (hadToken != null) s0.ObserveSecrets[Notify.PingdomClient.SecretKey] = hadToken; else s0.ObserveSecrets.Remove(Notify.PingdomClient.SecretKey);
                if (tile != null) Checks.Unpin(tile);
                AppSettings.Save();
            }
        }

        // Microsoft Teams (#13): a fake Graph / webhook endpoint and a made-up token; nothing reaches Microsoft.
        {
            var s1 = AppSettings.Current;
            var saved1 = (s1.TeamsEnabled, s1.TeamsAllowImages, s1.TeamsDestinations);
            var fake = new FakeTeams();
            try
            {
                Notify.TeamsShare.HandlerForTest = fake; Notify.TeamsShare.ResetForTest();
                Notify.TeamsShare.TokenForTest = () => Task.FromResult("graph-token-selftest");
                var chan = new Notify.TeamsDestination { Name = "Ops › General", Kind = "channel", TeamId = "team-1", ChannelId = "19:abc@thread.tacv2" };
                var hook = new Notify.TeamsDestination { Name = "Hook", Kind = "webhook" };
                s1.TeamsEnabled = false;
                try { await Notify.TeamsShare.SendAsync(chan, "t", "x", null); throw new Exception("sent while switched off"); } catch (InvalidOperationException) { }
                s1.TeamsEnabled = true; s1.TeamsAllowImages = true;
                if (Notify.TeamsShare.WebhookProblem("https://prod-12.westeurope.logic.azure.com/workflows/x/triggers/manual/paths/invoke?sig=abc") != null) throw new Exception("a Workflows URL was refused");
                foreach (var bad in new[] { "http://prod.logic.azure.com/x", "https://evil.example/logic.azure.com", "https://user:pw@x.logic.azure.com/" })
                    if (Notify.TeamsShare.WebhookProblem(bad) == null) throw new Exception("accepted webhook " + bad);
                Notify.TeamsShare.SetWebhookUrl(hook, "https://prod-12.westeurope.logic.azure.com/workflows/x/triggers/manual/paths/invoke?sig=abc");
                var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
                await Notify.TeamsShare.SendAsync(chan, "Disk <full>", "password: Hunter2-selftest\n<script>x</script>", png);
                if (fake.LastPath != "/v1.0/teams/team-1/channels/19%3Aabc%40thread.tacv2/messages" || fake.LastAuth != "Bearer graph-token-selftest") throw new Exception("graph request " + fake.LastPath);
                var html = System.Text.Json.Nodes.JsonNode.Parse(fake.LastBody)!["body"]!["content"]!.GetValue<string>();
                if (!fake.LastBody.Contains("hostedContents") || !html.Contains("../hostedContents/1/$value") || html.Contains("Hunter2-selftest") || html.Contains("<script>") || !html.Contains("Disk &lt;full&gt;")) throw new Exception("graph body: " + html[..Math.Min(300, html.Length)]);
                try { await Notify.TeamsShare.SendAsync(hook, "t", "x", png); throw new Exception("an image went to a webhook"); } catch (InvalidOperationException) { }
                await Notify.TeamsShare.SendAsync(hook, "Hello", "token=abcd1234efgh5678secret", null);
                if (fake.LastHost != "prod-12.westeurope.logic.azure.com" || !fake.LastBody.Contains("AdaptiveCard") || fake.LastBody.Contains("abcd1234efgh5678secret")) throw new Exception("webhook body");
                s1.TeamsAllowImages = false;
                try { await Notify.TeamsShare.SendAsync(chan, "t", "x", png); throw new Exception("an image went while images are off"); } catch (InvalidOperationException) { }
                ok("Microsoft Teams (#13): nothing goes while sharing is off (and no image while images are off); a channel message goes to Graph with the screenshot as hosted content, HTML-escaped and with secrets masked; a webhook gets a text-only Adaptive Card and only Microsoft's Workflows hosts are accepted as webhook addresses (https, no user info)");
            }
            catch (Exception ex) { fail("Microsoft Teams (#13)", ex); }
            finally
            {
                Notify.TeamsShare.HandlerForTest = null; Notify.TeamsShare.TokenForTest = null; Notify.TeamsShare.ResetForTest();
                (s1.TeamsEnabled, s1.TeamsAllowImages, s1.TeamsDestinations) = saved1;
                foreach (var k in s1.ObserveSecrets.Keys.Where(k => k.StartsWith("teams:")).ToList()) s1.ObserveSecrets.Remove(k);
                AppSettings.Save();
            }
        }

        // BeyondTrust (#16): fake PRA and Password Safe servers (made-up credentials); nothing reaches an appliance.
        try
        {
            var fake = new FakeBeyondTrust();
            var pra = new BeyondTrust.PraClient("https://pra.example.test", "client-1", "secret-1", fake);
            var items = await pra.JumpItemsAsync();
            if (fake.TokenAuth != "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("client-1:secret-1")) || fake.ApiAuth != "Bearer tok-1") throw new Exception("auth headers");
            if (items.Count != 101 || items.Count(i => i.Kind == "remote-rdp") != 1 || items.First(i => i.Kind == "remote-rdp").Group != "Datacentre") throw new Exception($"jump items {items.Count}");
            var sess = BeyondTrust.PraClient.ParseSessions("<?xml version=\"1.0\"?><session_list xmlns=\"http://www.beyondtrust.com/sra/namespaces/API/reporting\"><session lsid=\"abc\"><start_time>2026-10-20T10:00:00</start_time><end_time>2026-10-20T10:30:00</end_time><jumpoint>JP1</jumpoint><primary_customer><hostname>web01</hostname></primary_customer><rep_list><representative><username>jo</username></representative></rep_list></session></session_list>");
            if (sess.Single() is not { Id: "abc", Target: "web01", Users: "jo", Jumpoint: "JP1" }) throw new Exception("session report");
            try { BeyondTrust.PraClient.ParseSessions("<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY e SYSTEM \"file:///c:/windows/win.ini\">]><session_list>&e;</session_list>"); throw new Exception("a DTD was processed"); } catch (System.Xml.XmlException) { }
            var url = BeyondTrust.PraClient.JumpUrl("https://pra.example.test", items.First(i => i.Kind == "remote-rdp"));
            if (url != "https://pra.example.test/api/client_script?type=rep&operation=generate&action=start_jump_item_session&client.hostname=dc01.corp.test&jump.method=rdp") throw new Exception(url);
            if (BeyondTrust.PraClient.SiteProblem("https://pra.example.test/evil") == null || BeyondTrust.PraClient.SiteProblem("http://pra.example.test") == null) throw new Exception("site checks");

            var ps = new BeyondTrust.PasswordSafeClient("https://bi.example.test", "key-1", "svc-api", null, fake);
            var accts = await ps.AccountsAsync();
            if (accts.Single() is not { SystemName: "web01", AccountName: "root", SystemId: 5, AccountId: 9 }) throw new Exception("managed accounts");
            if (fake.PsAuth != "PS-Auth key=key-1; runas=svc-api;") throw new Exception("PS-Auth " + fake.PsAuth);
            var (rid, pw) = await ps.CheckOutAsync(accts[0], 30, "selftest");
            if (rid != 77 || pw != "Vault-Pass-1" || !fake.RequestBody.Contains("\"DurationMinutes\":30") || !fake.RequestBody.Contains("\"AccountID\":9")) throw new Exception("checkout");
            await ps.CheckInAsync(rid, "done");
            if (fake.CheckedIn != 77) throw new Exception("check-in");
            // Linked hosts: the checkout goes into an in-memory credential for the session and back in when its tab closes.
            {
                var st = AppSettings.Current; var was = (st.BeyondTrustEnabled, st.BeyondTrustAllowCheckout, st.BeyondTrustAskReason);
                try
                {
                    st.BeyondTrustEnabled = true; st.BeyondTrustAllowCheckout = true; st.BeyondTrustAskReason = false;   // (no prompt in the self-test)
                    fake.CheckedIn = 0;
                    PasswordSafeSessions.UseForTest(new BeyondTrust.PasswordSafeClient("https://bi.example.test", "key-1", "svc-api", null, fake));
                    var link = PasswordSafeSessions.Link(accts[0]);
                    if (PasswordSafeSessions.Parse(link) is not { System: 5, Account: 9, Label: @"web01\root" }) throw new Exception("link format " + link);
                    var host = new HostEntry { Name = "ps-linked", Address = "192.0.2.77", Username = "ignored", PasswordSafeAccount = link };
                    var (h2, co) = await PasswordSafeSessions.PrepareAsync(host);
                    if (co == null || h2 == host || h2.Transient?.Secret != "Vault-Pass-1" || h2.Username != "root" || host.Transient != null) throw new Exception("prepared host");
                    if (System.Text.Json.JsonSerializer.Serialize(h2).Contains("Vault-Pass-1")) throw new Exception("the checked-out password would be saved with the host");
                    var view = new object();
                    PasswordSafeSessions.Attach(view, co);
                    PasswordSafeSessions.Closed(view);
                    for (int i = 0; i < 20 && fake.CheckedIn != 77; i++) await Task.Delay(50);
                    if (fake.CheckedIn != 77 || PasswordSafeSessions.Active.Count != 0) throw new Exception("not checked in when the tab closed");
                    // 0.6.17 (ADR-096): a vault credential kept in Password Safe. A background use never starts a checkout; the
                    // first use checks out once, every later use (after the in-memory hold too, and in the background) shares
                    // it; the app lock checks it in and the password goes with it.
                    var psCred = CredentialSources.New(CredentialSources.Link(accts[0]), @"web01\root (demo)", CredentialKind.Password, "root", "service account");
                    CredentialSources.Forget();
                    try { using (ExternalCredentials.Background()) await ExternalCredentials.ResolveAsync(psCred, "a dashboard check"); throw new Exception("a background use started a checkout"); }
                    catch (InvalidOperationException ex) when (ex.Message.Contains("never start one")) { }
                    fake.CheckOuts = 0; fake.CheckedIn = 0;
                    var r1 = await ExternalCredentials.ResolveAsync(psCred, "self-test");
                    CredentialSources.Forget();
                    var r2 = await ExternalCredentials.ResolveAsync(psCred, "self-test again");
                    CredentialSources.Forget();
                    Credential? r3;
                    using (ExternalCredentials.Background()) r3 = await ExternalCredentials.ResolveAsync(psCred, "a dashboard check");
                    if (r1 is not { Secret: "Vault-Pass-1", Username: "root", IsExternal: false } || r2?.Secret != "Vault-Pass-1" || r3?.Secret != "Vault-Pass-1" || fake.CheckOuts != 1 || psCred.Secret.Length != 0)
                        throw new Exception($"leases: {fake.CheckOuts} checkout(s)");
                    if (PasswordSafeSessions.Active.Count != 1 || System.Text.Json.JsonSerializer.Serialize(psCred).Contains("Vault-Pass-1")) throw new Exception("lease not listed, or the credential would save the password");
                    PasswordSafeSessions.CheckInLeases("self-test");
                    for (int i = 0; i < 20 && fake.CheckedIn != 77; i++) await Task.Delay(50);
                    CredentialSources.Forget();
                    if (fake.CheckedIn != 77 || PasswordSafeSessions.Active.Count != 0) throw new Exception("the lease was not checked in");
                    try { using (ExternalCredentials.Background()) await ExternalCredentials.ResolveAsync(psCred, "after the lock"); throw new Exception("the password outlived its check-in"); }
                    catch (InvalidOperationException ex) when (ex.Message.Contains("never start one")) { }
                    // 0.6.19: the credential picker lists the managed accounts (names only) and adds a picked one once.
                    CredentialSearch.Forget();
                    var psHits = await CredentialSearch.LoadAsync(CredentialSearch.PasswordSafe);
                    if (psHits.Count != 1 || psHits[0].Name != @"web01\root" || psHits[0].External != CredentialSources.Link(accts[0])) throw new Exception("Password Safe in the picker: " + string.Join(", ", psHits.Select(h => h.Name)));
                    var psPicked = CredentialSearch.Use(psHits[0]);
                    var psAgain = CredentialSearch.Use((await CredentialSearch.LoadAsync(CredentialSearch.PasswordSafe))[0]);
                    AppData.Current.Credentials.Remove(psPicked);
                    if (!psPicked.IsExternal || psPicked.Username != "root" || psAgain.Id != psPicked.Id || fake.CheckOuts != 1) throw new Exception("a picked Password Safe account");
                    st.BeyondTrustAllowCheckout = false;
                    try { await PasswordSafeSessions.PrepareAsync(new HostEntry { Name = "x", Address = "192.0.2.78", PasswordSafeAccount = link }); throw new Exception("checked out while forbidden"); }
                    catch (InvalidOperationException) { }
                }
                finally { (st.BeyondTrustEnabled, st.BeyondTrustAllowCheckout, st.BeyondTrustAskReason) = was; PasswordSafeSessions.UseForTest(null); CredentialSources.Forget(); }
            }
            try { _ = new BeyondTrust.PasswordSafeClient("https://bi.example.test", "key; runas=admin", "u", null, fake); throw new Exception("header injection"); } catch (ArgumentException) { }
            ok("BeyondTrust (#16): PRA's OAuth token (client credentials, Basic) then Bearer calls, Jump Items read page by page across kinds (a kind the appliance lacks is skipped) with their Jump Groups, the access-session report read as XML without DTDs, a Jump Item opens through the access console's client-script link; Password Safe signs in with PS-Auth (no header injection), lists managed accounts, checks a password out for the configured minutes and back in; a host linked to a managed account gets the password only as an in-memory credential for its session (never serialised with the host), checked back in when the tab closes, and nothing is checked out while checkouts are forbidden; 0.6.17: a vault credential kept in Password Safe checks out once and every use shares it (background uses only reuse it, never start one), the app lock checks it in and the password goes with it");
        }
        catch (Exception ex) { fail("BeyondTrust (#16)", ex); }

        // 6. The help answers questions about every 0.5.0 feature (#10): each question's best article is the right one.
        try
        {
            var asks = new (string Q, string Expect)[]
            {
                ("how do I back up and restore", "Backup & restore"), ("import from mRemoteNG", "Import connections"), ("puppet certificate sign", "Puppet"),
                ("wsl.conf mirrored networking", "WSL"), ("open host in vscode", "VS Code"), ("share screenshot to microsoft teams", "Microsoft Teams"),
                ("pingdom pinned check", "Pingdom"), ("beyondtrust password safe checkout", "BeyondTrust"), ("kerberos single sign-on ssh", "Single sign-on"),
                ("why is this setting greyed out", "locked"), ("drag hosts into a folder", "Drag and drop"), ("windows hello automatic sign in domain", "Automatic sign-in"),
            };
            var misses = asks.Where(a => !(HelpKb.Search(a.Q, 2).Any(r => r.Article.Title.Contains(a.Expect, StringComparison.OrdinalIgnoreCase)))).Select(a => a.Q).ToList();
            if (misses.Count > 0) throw new Exception("no matching article for: " + string.Join("; ", misses));
            ok($"help (#10): {HelpKb.Articles.Count} articles; questions about backup & restore, importers, Puppet, WSL, IDEs, Microsoft Teams, Pingdom, BeyondTrust, single sign-on, locked settings, drag and drop and automatic sign-in each find their article first or second");
        }
        catch (Exception ex) { fail("help (#10)", ex); }

        // 5. About reads "ServerManagement v<version>" (#2).
        try
        {
            if (SettingsView.AboutTitle(AppInfo.ProductName, "0.5.0") != "ServerManagement v0.5.0") throw new Exception(SettingsView.AboutTitle(AppInfo.ProductName, "0.5.0"));
            if (SettingsView.AboutTitle("Ocean ServerManagement", "0.5.0") != "Ocean ServerManagement v0.5.0") throw new Exception("branded name");
            ok("About (#2): “ServerManagement v0.5.0”, the company line underneath; a branded product name is kept");
        }
        catch (Exception ex) { fail("About title (#2)", ex); }
    }

    private sealed class FakePingdom : System.Net.Http.HttpMessageHandler
    {
        public string? LastAuth, LastHost, LastMethod;
        public int Status = 200;
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage req, CancellationToken ct)
        {
            LastAuth = req.Headers.Authorization?.ToString(); LastHost = req.RequestUri!.Host; LastMethod = req.Method.Method;
            var path = req.RequestUri.AbsolutePath;
            var body = Status != 200 ? "{\"error\":{\"statuscode\":401,\"errormessage\":\"Invalid token\"}}"
                : path.EndsWith("/checks/85975") ? "{\"check\":{\"id\":85975,\"name\":\"API\",\"hostname\":\"api.example.com\",\"status\":\"up\",\"lastresponsetime\":355,\"lasttesttime\":1761040000,\"type\":{\"http\":{\"url\":\"/health\"}}}}"
                : "{\"checks\":[{\"id\":85975,\"name\":\"API\",\"hostname\":\"api.example.com\",\"status\":\"up\",\"lastresponsetime\":355,\"type\":\"http\",\"tags\":[{\"name\":\"prod\",\"type\":\"u\"}]},{\"id\":161748,\"name\":\"Web\",\"hostname\":\"www.example.com\",\"status\":\"down\",\"type\":\"http\"}],\"counts\":{\"total\":2}}";
            return Task.FromResult(new System.Net.Http.HttpResponseMessage((System.Net.HttpStatusCode)Status) { Content = new System.Net.Http.StringContent(body, System.Text.Encoding.UTF8, "application/json") });
        }
    }

    private sealed class FakeTeams : System.Net.Http.HttpMessageHandler
    {
        public string? LastAuth, LastHost, LastPath; public string LastBody = "";
        protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage req, CancellationToken ct)
        {
            LastAuth = req.Headers.Authorization?.ToString(); LastHost = req.RequestUri!.Host; LastPath = req.RequestUri.AbsolutePath;
            LastBody = req.Content != null ? await req.Content.ReadAsStringAsync(ct) : "";
            return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.Created) { Content = new System.Net.Http.StringContent("{}") };
        }
    }

    private sealed class FakeBeyondTrust : System.Net.Http.HttpMessageHandler
    {
        public string? TokenAuth, ApiAuth, PsAuth; public string RequestBody = ""; public long CheckedIn; public int CheckOuts;
        protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath; var q = req.RequestUri.Query;
            var auth = req.Headers.TryGetValues("Authorization", out var a) ? a.First() : "";
            string body; var code = System.Net.HttpStatusCode.OK;
            if (path == "/oauth2/token") { TokenAuth = auth; body = "{\"access_token\":\"tok-1\",\"token_type\":\"Bearer\",\"expires_in\":3600}"; }
            else if (path.StartsWith("/api/config/v1/")) { ApiAuth = auth; body = path switch
                {
                    "/api/config/v1/jump-group" => "[{\"id\":3,\"name\":\"Datacentre\"}]",
                    "/api/config/v1/jump-item/shell-jump" => q.Contains("current_page=1") ? "[" + string.Join(",", Enumerable.Range(1, 100).Select(i => $"{{\"id\":{i},\"name\":\"srv{i}\",\"hostname\":\"srv{i}.test\",\"port\":22,\"jump_group_id\":3}}")) + "]" : "[]",
                    "/api/config/v1/jump-item/remote-rdp" => q.Contains("current_page=1") ? "[{\"id\":500,\"name\":\"DC01\",\"hostname\":\"dc01.corp.test\",\"jump_group_id\":3}]" : "[]",
                    "/api/config/v1/jump-item/web-jump" => null!,
                    _ => "[]",
                }; if (body == null) { code = System.Net.HttpStatusCode.NotFound; body = "{}"; } }
            else if (path.StartsWith("/BeyondTrust/api/public/v3/"))
            {
                PsAuth = auth;
                var p = path["/BeyondTrust/api/public/v3".Length..];
                if (req.Content != null) RequestBody = await req.Content.ReadAsStringAsync(ct);
                body = p switch
                {
                    "/Auth/SignAppIn" => "{\"UserId\":1}",
                    "/ManagedAccounts" => "[{\"SystemId\":5,\"AccountId\":9,\"SystemName\":\"web01\",\"AccountName\":\"root\",\"DomainName\":\"\"}]",
                    "/Requests" => "77",
                    "/Credentials/77" => "\"Vault-Pass-1\"",
                    "/Requests/77/Checkin" => "",
                    _ => "{}",
                };
                if (p == "/Requests/77/Checkin") CheckedIn = 77;
                if (p == "/Requests") CheckOuts++;
            }
            else { code = System.Net.HttpStatusCode.NotFound; body = ""; }
            return new System.Net.Http.HttpResponseMessage(code) { Content = new System.Net.Http.StringContent(body) };
        }
    }

    internal static IEnumerable<T> VisualDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        int n;
        try { n = VisualTreeHelper.GetChildrenCount(root); } catch (InvalidOperationException) { yield break; }
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) yield return t;
            foreach (var d in VisualDescendants<T>(c)) yield return d;
        }
    }
}
