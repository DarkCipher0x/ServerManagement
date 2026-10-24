using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DcxManager.Addons;
using DcxManager.Files;
using DcxManager.Net;
using DcxManager.Services;
using DcxManager.Views;

namespace DcxManager;

/// <summary>
/// 0.5.1 (docs/PLAN-0.5.1.md): locks (categories and items; open for a while, closed by the timer; folders cover their
/// hosts; password locks throttle; add-ons and the local API never get a locked item), the Group Policy restrictions, the
/// add-on policy, cloud accounts behind their padlock, the AI tool listing a locked host by name only, the host import
/// (loopback fakes of an SSH server and Apple Screen Sharing → reviewed rows → imported hosts, the desktop tunnelled through
/// the SSH twin), the File Commander's "not connected" pane, the LOGS section, every dropdown showing its selection's text
/// (Settings and the host editor), and a VNC tab drawing what the server sends. No window, prompt or dialog.
/// </summary>
public static class Release051Tests
{
    public static async Task RunAsync(MainWindow main, Action<string> ok, Action<string, Exception?> fail)
    {
        async Task Settle(int ms = 200)
        {
            await Task.Delay(ms);
            await Application.Current.Dispatcher.InvokeAsync(() => main.UpdateLayout(), DispatcherPriority.ApplicationIdle);
        }
        async Task Run(string name, Func<Task> test)
        {
            try { await test(); ok(name); }
            catch (Exception ex) { fail(name, ex); }
        }
        var s = AppSettings.Current;

        await Run("0.5.1 locks: an item lock refuses until opened, closes on its timer; a folder lock covers its hosts", () =>
        {
            var h = new HostEntry { Name = "selftest-locked", Address = "192.0.2.51", Group = "SelfTestLocks/Inner" };
            AppData.Current.Hosts.Add(h);
            var clock = Locks.UtcNow;
            var minutes = s.LockOpenMinutes;
            try
            {
                Locks.ResetForTest();
                s.LockOpenMinutes = 15;
                if (Locks.Set("host:" + h.Id, h.Display, Locks.Uac) != Locks.Result.Ok) throw new Exception("could not lock a host");
                if (!Locks.IsLocked("host:" + h.Id) || Locks.IsOpen("host:" + h.Id) || LockUi.Padlock("host:" + h.Id) != "🔒") throw new Exception("not shown as locked");
                try { Locks.Demand("host:" + h.Id, h.Display); throw new Exception("a closed lock let the host through"); } catch (Locks.LockedException) { }
                Locks.MarkOpen("host:" + h.Id);
                Locks.Demand("host:" + h.Id, h.Display);
                if (LockUi.Padlock("host:" + h.Id) != "🔓") throw new Exception("open padlock");
                var now = DateTime.UtcNow;
                Locks.UtcNow = () => now.AddMinutes(16);
                if (!Locks.Expire() || Locks.IsOpen("host:" + h.Id)) throw new Exception("the lock didn't close after its time");
                Locks.UtcNow = clock;
                Locks.Remove("host:" + h.Id);
                // A folder lock covers every host inside it (and its sub-folders).
                if (Locks.Set("folder:SelfTestLocks", "SelfTestLocks", Locks.Uac) != Locks.Result.Ok) throw new Exception("could not lock a folder");
                if (!Locks.IsLocked("host:" + h.Id) || Locks.Padlock("host:" + h.Id) != "inherited") throw new Exception("folder lock didn't cover its host");
                Locks.MarkOpen("folder:SelfTestLocks");
                if (!Locks.IsOpen("host:" + h.Id)) throw new Exception("opening the folder didn't open its host");
                Locks.CloseAll("self-test");
                if (Locks.IsOpen("host:" + h.Id)) throw new Exception("CloseAll left it open");
                Locks.Remove("folder:SelfTestLocks");
                // Policy: no item locks at all.
                s.AllowItemLocks = false;
                if (Locks.Set("host:" + h.Id, h.Display, Locks.Uac) != Locks.Result.NotAllowed) throw new Exception("AllowItemLocks = false still let a host be locked");
            }
            finally
            {
                s.AllowItemLocks = true; s.LockOpenMinutes = minutes; Locks.UtcNow = clock;
                Locks.Forget("host:" + h.Id); Locks.Forget("folder:SelfTestLocks"); Locks.ResetForTest();
                AppData.Current.Hosts.Remove(h);
            }
            return Task.CompletedTask;
        });

        await Run("0.5.1 locks: a password lock checks its password, throttles wrong ones, and never stores it", () =>
        {
            var clock = Locks.UtcNow;
            const string target = "note:selftest-password-lock", pw = "Lock-Selftest-Pass-9";
            try
            {
                if (Locks.Set(target, "selftest note", Locks.Password, "short") != Locks.Result.TooShort) throw new Exception("a short password was accepted");
                if (Locks.Set(target, "selftest note", Locks.Password, pw) != Locks.Result.Ok) throw new Exception("could not set a password lock");
                var rule = s.UserLocks.Single(r => r.Target == target);
                if (rule.Hash.Contains(pw) || JsonSerializer.Serialize(rule).Contains(pw)) throw new Exception("the password is stored");
                for (int i = 0; i < 3; i++) if (Locks.Verify(rule, "wrong-" + i) != Locks.Result.Wrong) throw new Exception("a wrong password passed");
                if (Locks.Verify(rule, pw) != Locks.Result.Wait) throw new Exception("no wait after three wrong passwords");
                var now = DateTime.UtcNow;
                Locks.UtcNow = () => now.AddMinutes(11);
                if (Locks.Verify(rule, pw) != Locks.Result.Ok) throw new Exception("the right password failed after the wait");
            }
            finally { Locks.UtcNow = clock; Locks.Forget(target); }
            return Task.CompletedTask;
        });

        await Run("0.5.1 locks: the audit log is locked by default; Group Policy locks a category its own way", async () =>
        {
            var cats = s.LockedCategories.ToList();
            var auditWas = s.LockAuditLog;
            try
            {
                if (!new AppSettings().LockAuditLog) throw new Exception("the audit log isn't locked by default");
                s.LockAuditLog = true;
                if (Locks.RuleOn("cat:audit") is not { Managed: true } || Locks.IsOpen("cat:audit")) throw new Exception("LockAuditLog doesn't lock the audit log");
                // Opening the locked screen asks (refused in the self-test, never shown) and doesn't navigate.
                int asked = Dialogs.SelfTestPrompts.Count;
                main.Navigate("audit"); await Settle(300);
                if (Dialogs.SelfTestPrompts.Count != asked + 1 || main.Panes.AllTabs.Any(t => t.IsSelected && t.Content is AuditView)) throw new Exception("the locked audit log opened, or nothing asked");
                lock (Dialogs.SelfTestPrompts) Dialogs.SelfTestPrompts.RemoveAt(Dialogs.SelfTestPrompts.Count - 1);   // expected here
                if (Locks.CategoryOfScreen("audit") != "audit" || Locks.CategoryOfScreen("errorlog") != "errorlog") throw new Exception("LOGS screens aren't lockable categories");
                s.LockedCategories = new() { "credentials" };
                if (Locks.RuleOn("cat:credentials") is not { Managed: true, Mode: Locks.Uac }) throw new Exception("LockedCategories didn't lock the category");
                if (Locks.Remove("cat:credentials") != Locks.Result.Managed) throw new Exception("a policy lock could be removed");
                if (Locks.Categories.Count < 20) throw new Exception("categories: " + Locks.Categories.Count);
            }
            finally { s.LockedCategories = cats; s.LockAuditLog = auditWas; }
        });

        await Run("0.5.1 locks: add-ons and the local API never get a locked item, even while it is open", async () =>
        {
            var h = new HostEntry { Name = "selftest-strict", Address = "192.0.2.52", Username = "secretuser" };
            AppData.Current.Hosts.Add(h);
            try
            {
                Locks.Set("host:" + h.Id, h.Display, Locks.Uac);
                Locks.MarkOpen("host:" + h.Id);
                var ctx = new CapabilityContext((_, _, _) => Task.FromResult(true), "addon:com.example.selftest");
                var json = await Capabilities.RunAsync("list_hosts", new JsonObject(), ctx, CancellationToken.None);
                var row = JsonNode.Parse(json)!.AsArray().OfType<JsonObject>().FirstOrDefault(o => o["name"]?.GetValue<string>() == h.Display) ?? throw new Exception("host not listed");
                if (row["locked"]?.GetValue<bool>() != true || json.Contains("192.0.2.52") || json.Contains("secretuser")) throw new Exception("an add-on saw a locked host's details: " + row.ToJsonString());
                var you = await Capabilities.RunAsync("list_hosts", new JsonObject(), new CapabilityContext((_, _, _) => Task.FromResult(true)), CancellationToken.None);
                if (!you.Contains("192.0.2.52")) throw new Exception("the AI (for you) didn't see the open host");
                Locks.Strict.Value = "addon:com.example.selftest";
                try { Locks.Demand("host:" + h.Id, h.Display); throw new Exception("Strict let an open locked host through"); }
                catch (Locks.LockedException) { }
                finally { Locks.Strict.Value = null; }
            }
            finally { Locks.Forget("host:" + h.Id); Locks.ResetForTest(); AppData.Current.Hosts.Remove(h); }
        });

        await Run("0.5.1 locks: a padlocked cloud account gives no API client until it is opened", () =>
        {
            var acct = new VultrAccount { Name = "selftest-vultr", ApiKey = "selftest-not-a-key" };
            var p = Vultr.VultrProvider.Instance;
            try
            {
                Locks.Set(p.LockTarget(acct), "selftest-vultr", Locks.Uac);
                try { p.ApiFor(acct); throw new Exception("a locked account gave an API client"); } catch (Locks.LockedException) { }
                Locks.MarkOpen(p.LockTarget(acct));
                _ = p.ApiFor(acct);
            }
            finally { Locks.Forget(p.LockTarget(acct)); Locks.ResetForTest(); }
            return Task.CompletedTask;
        });

        await Run("0.5.1 Hyper-V: the right module fix on Server, Pro and Home; who may run the cmdlets; one-click install", async () =>
        {
            var runWas = Infra.HyperVProvider.Run; var elevWas = Infra.HyperVSetup.RunElevated;
            int exit = 3; string err = "", output = "";
            Infra.HyperVProvider.Run = (_, _, _, _) => Task.FromResult((exit, output, err));
            string? ran = null;
            Infra.HyperVSetup.RunElevated = (cmd, _) => { ran = cmd; return Task.FromResult(0); };
            try
            {
                var hv = new Infra.HyperVProvider(new Infra.ProviderConfig { Kind = "hyperv", Name = "hv-selftest" });
                err = "NO_MODULE|3|Microsoft Windows Server 2022 Datacenter";
                var server = await hv.DetectAsync(CancellationToken.None);
                if (server.Reachable || hv.MissingModule is not { IsServer: true } se || Infra.HyperVSetup.ModuleFix(se).Command?.Contains("Install-WindowsFeature") != true || !server.Detail.Contains("Server 2022")) throw new Exception("server: " + server.Detail);
                if (await Infra.HyperVSetup.InstallModuleAsync(se, CancellationToken.None) != null || ran?.Contains("Hyper-V-PowerShell") != true) throw new Exception("server install: " + ran);
                err = "NO_MODULE|1|Microsoft Windows 11 Pro";
                await hv.DetectAsync(CancellationToken.None);
                if (hv.MissingModule is not { IsServer: false, IsHome: false } pro || Infra.HyperVSetup.ModuleFix(pro).Command?.Contains("Microsoft-Hyper-V-Management-PowerShell") != true) throw new Exception("pro fix");
                err = "NO_MODULE|1|Microsoft Windows 11 Home";
                var home = await hv.DetectAsync(CancellationToken.None);
                ran = null;
                if (hv.MissingModule is not { IsHome: true } he || Infra.HyperVSetup.ModuleFix(he).Command != null || !home.Detail.Contains("has no Hyper-V")
                    || await Infra.HyperVSetup.InstallModuleAsync(he, CancellationToken.None) == null || ran != null) throw new Exception("home: " + home.Detail);
                exit = 0; err = "";
                output = "{\"name\":\"HV01\",\"cpus\":8,\"memory\":1,\"os\":\"Microsoft Windows 11 Pro\",\"productType\":1,\"admin\":true,\"elevated\":false,\"hvAdmin\":false}";
                var caps = await hv.DetectAsync(CancellationToken.None);
                if (!caps.Reachable || hv.MissingModule != null || !caps.Detail.Contains("Windows 11 Pro") || !caps.Notes.Any(n => n.Contains("isn't elevated"))) throw new Exception("rights: " + string.Join(" | ", caps.Notes));
                output = output.Replace("\"hvAdmin\":false", "\"hvAdmin\":true");
                if (!(await hv.DetectAsync(CancellationToken.None)).Notes.Any(n => n.Contains("reading and changes work without elevation"))) throw new Exception("Hyper-V Administrators note");
            }
            finally { Infra.HyperVProvider.Run = runWas; Infra.HyperVSetup.RunElevated = elevWas; }
        });

        await Run("0.5.1 BeyondTrust: a self-signed appliance is refused until pinned, then used; a changed certificate is refused", async () =>
        {
            var pinWas = s.BeyondTrustPraPin;
            using var cert = TestWeb.MakeCert("dcx-selftest-pra");
            using var other = TestWeb.MakeCert("dcx-selftest-pra-other");
            using var fake = new FakePra(cert);
            try
            {
                s.BeyondTrustPraPin = "";
                var site = $"https://127.0.0.1:{fake.Port}";
                try { await new BeyondTrust.PraClient(site, "selftest-client", "selftest-secret").JumpointsAsync(); throw new Exception("an untrusted certificate was accepted"); }
                catch (InvalidOperationException ex) when (ex.Message.Contains("not trusted yet") && ex.Message.Contains(Infra.TlsPin.Pretty(Infra.TlsPin.Sha256(cert)))) { }
                if (!BeyondTrust.BtTls.Seen.TryGetValue("pra", out var seen) || seen.Decision != Infra.TlsDecision.NeedsTrust) throw new Exception("not recorded for the TLS card");
                s.BeyondTrustPraPin = seen.Sha256;
                var jp = await new BeyondTrust.PraClient(site, "selftest-client", "selftest-secret").JumpointsAsync();
                if (jp.Count != 1 || jp[0].Name != "dc1-jumpoint" || !jp[0].Connected) throw new Exception("jumpoints: " + jp.Count);
                fake.Cert = other;
                try { await new BeyondTrust.PraClient(site, "selftest-client", "selftest-secret").JumpointsAsync(); throw new Exception("a changed certificate was accepted"); }
                catch (InvalidOperationException ex) when (ex.Message.Contains("CERTIFICATE CHANGED")) { }
                if (ProtectedSettings.Find("bt.praPin")!.Check(System.Text.Json.Nodes.JsonValue.Create("not-a-pin")) == null) throw new Exception("a bad pin passed the protected check");
            }
            finally { s.BeyondTrustPraPin = pinWas; BeyondTrust.BtTls.Seen.Clear(); }
        });

        await Run("0.5.1 Settings: Integrations has a page per integration; BeyondTrust's page has its eight cards", async () =>
        {
            main.Navigate("settings"); await Settle(400);
            var sv = SettingsView.Current ?? throw new Exception("no Settings screen");
            sv.RevealIntegration("BeyondTrust"); await Settle(300);
            var shown = sv.VisibleCardsForTest;
            var want = new[] { "BeyondTrust", "BeyondTrust · Connection", "BeyondTrust · Password Safe", "BeyondTrust · Checkout policy", "BeyondTrust · Sessions", "BeyondTrust · TLS", "BeyondTrust · Scopes", "BeyondTrust · Audit" };
            if (!shown.SequenceEqual(want)) throw new Exception("BeyondTrust page: " + string.Join(", ", shown));
            sv.RevealIntegration("Teams"); await Settle(200);
            if (!sv.VisibleCardsForTest.SequenceEqual(new[] { "Microsoft Teams" })) throw new Exception("Teams page: " + string.Join(", ", sv.VisibleCardsForTest));
            sv.RevealIntegration("All"); await Settle(200);
            if (sv.VisibleCardsForTest.Count < want.Length + 8) throw new Exception("All: " + sv.VisibleCardsForTest.Count);
        });

        await Run("0.5.1 dashboard: one card of every kind is added and drawn, a locked host shows its padlock only, the lock policy stops changes, reset brings the default back", async () =>
        {
            var order = s.DashboardOrder.ToList(); var hidden = s.DashboardHidden.ToList(); var cards = s.DashboardCards.ToList(); var lockedWas = s.DashboardLocked;
            var h = new HostEntry { Name = "selftest-dash", Address = "192.0.2.61", Group = "SelfTestDash" };
            var p = new Infra.ProviderConfig { Kind = "proxmox", Name = "selftest-dash-pve", Endpoint = "https://192.0.2.62:8006" };
            AppData.Current.Hosts.Add(h); AppData.Current.InfraProviders.Add(p);
            try
            {
                main.Navigate("dashboard"); await Settle(400);
                var dv = DashboardView.Current ?? throw new Exception("no dashboard");
                DashboardCatalog.Reset(s);
                var host = DashboardCatalog.Add(s, "host", h.Id);
                DashboardCatalog.Add(s, "folder", "SelfTestDash");
                DashboardCatalog.Add(s, "infra", p.Id);
                foreach (var t in new[] { "grafana", "sentinel", "pingdom", "beyondtrust", "puppet", "entra" }) DashboardCatalog.Add(s, t, "");
                if (Cloud.CloudProviders.All.FirstOrDefault() is { } cp) DashboardCatalog.Add(s, "cloud", cp.Id);
                dv.AfterEdit(); await Settle(600);
                var added = DashboardCatalog.Cards(s);
                if (added.Count < 9 || added.Any(c => !dv.PlacedCards.Contains(c.Id))) throw new Exception("not placed: " + string.Join(", ", added.Where(c => !dv.PlacedCards.Contains(c.Id)).Select(c => c.Type)));
                if (DashCard.Parse(host.Encode()) != host || DashCard.Parse("x:bad id|host|x|t|normal") != null || DashCard.Parse("x:abc|nonsense|x|t") != null) throw new Exception("card lines don't round-trip / bad ones accepted");
                if (!dv.TextOfCardForTest(host.Id).Contains("192.0.2.61")) throw new Exception("host card: " + dv.TextOfCardForTest(host.Id));
                Locks.Set("host:" + h.Id, h.Display, Locks.Uac);
                dv.AfterEdit(); await Settle(400);
                if (dv.TextOfCardForTest(host.Id).Contains("192.0.2.61") || !dv.TextOfCardForTest(host.Id).Contains("🔒")) throw new Exception("a locked host's card shows its address");
                Locks.Forget("host:" + h.Id);
                s.DashboardLocked = true;
                var before = s.DashboardOrder.ToList();
                dv.MoveCard(host.Id, -1); dv.SetCardHidden(host.Id, true);
                if (!s.DashboardOrder.SequenceEqual(before) || s.DashboardHidden.Contains(host.Id) || DashboardCatalog.Locked(s) == null) throw new Exception("DashboardLocked didn't stop changes");
                s.DashboardLocked = false;
                dv.ResetToDefault(ask: false); await Settle(300);
                if (s.DashboardCards.Count != 0 || !dv.VisibleOrderForTest.SequenceEqual(Overview.Cards.Select(c => c.Id))) throw new Exception("reset: " + string.Join(",", dv.VisibleOrderForTest));
                if (DashboardEditor.Choices("host").All(c => c.Id != h.Id) || DashboardEditor.Choices("infra").All(c => c.Id != p.Id)) throw new Exception("editor choices");
            }
            finally
            {
                s.DashboardOrder = order; s.DashboardHidden = hidden; s.DashboardCards = cards; s.DashboardLocked = lockedWas;
                AppData.Current.Hosts.Remove(h); AppData.Current.InfraProviders.Remove(p); Locks.Forget("host:" + h.Id);
                DashboardView.Current?.AfterEdit();
            }
        });

        await Run("0.5.1 help: every new feature has an article that a plain question finds first or second", () =>
        {
            var asks = new (string Q, string Expect)[]
            {
                ("connect to a mac with vnc screen sharing", "VNC"), ("find hosts on the network and import them", "Find hosts"), ("lock a host with a password", "Locks"),
                ("why can't I add a host turned off", "Turned off"), ("add-on install needs uac", "Add-ons"), ("add a card to the dashboard", "Dashboard"),
                ("install hyper-v powershell module on windows server", "Hyper-V"), ("quick start first run", "Quick Start"), ("set up foundry phi-4-mini one click", "Foundry"),
            };
            var misses = asks.Where(a => !HelpKb.Search(a.Q, 2).Any(r => r.Article.Title.Contains(a.Expect, StringComparison.OrdinalIgnoreCase))).Select(a => a.Q).ToList();
            if (misses.Count > 0) throw new Exception("no matching article for: " + string.Join("; ", misses));
            return Task.CompletedTask;
        });

        await Run("0.5.9 (DarkCipher0x): opening a host that is already open switches to its tab; Shift / New connection opens another", () =>
        {
            var mw = MainWindow.Instance ?? throw new Exception("no main window");
            bool before = s.ReuseOpenTabs;
            var host = new HostEntry { Id = "selftest-reuse", Name = "reuse-test", Address = "192.0.2.10", Port = 22, Username = "test" };
            const string key = "term:SshTarget:selftest-reuse";
            int Count() => mw.Panes.AllTabs.Count(t => MainWindow.ReuseKey(t.Content) == key);
            try
            {
                if (!new AppSettings().ReuseOpenTabs) throw new Exception("ReuseOpenTabs is not on by default");
                s.ReuseOpenTabs = true;   // the older suites run with it off (SelfTest.RunAsync); this test checks it on
                mw.OpenTerminal(host, connectOnOpen: false);
                if (Count() != 1) throw new Exception("first open made " + Count() + " tab(s)");
                mw.OpenTerminal(host, connectOnOpen: false);
                if (Count() != 1) throw new Exception("opening it again made another tab");
                if (MainWindow.ReuseKey(mw.Panes.ActiveTab?.Content) != key) throw new Exception("the open tab was not brought to the front");
                mw.OpenTerminal(host, connectOnOpen: false, forceNew: true);
                if (Count() != 2) throw new Exception("New connection did not open a second tab");
                if (!mw.HasOpenSession(host)) throw new Exception("HasOpenSession is false with tabs open");
                s.ReuseOpenTabs = false;
                mw.OpenTerminal(host, connectOnOpen: false);
                if (Count() != 3) throw new Exception("with the setting off, opening did not make a new tab");
                if (PolicySettings.Catalog.All(e => e.Name != nameof(AppSettings.ReuseOpenTabs))) throw new Exception("ReuseOpenTabs is not a policy");
            }
            finally
            {
                s.ReuseOpenTabs = before;
                foreach (var t in mw.Panes.AllTabs.Where(t => MainWindow.ReuseKey(t.Content) == key).ToList()) mw.CloseTab(t);
            }
            return Task.CompletedTask;
        });

        await Run("0.6.7 (DarkCipher0x): sessions opened from Hosts open inside the Hosts tab, beside its folder pane (or as separate tabs); other screens never land inside it", () =>
        {
            var mw = MainWindow.Instance ?? throw new Exception("no main window");
            var P = mw.Panes;
            var before = s.HostsOpenIn;
            var mine = new List<TabItem>();
            var host = new HostEntry { Id = "selftest-inside", Name = "inside-host", Address = "192.0.2.40", Port = 22, Username = "test" };
            try
            {
                HostsView.SetOpenIn("hosts");
                mw.Navigate("hosts");
                var page = P.AllTabs.Select(t => t.Content).OfType<HostsView>().FirstOrDefault() ?? throw new Exception("no Hosts tab");
                page.ApplyOpenIn();
                if (page.SessionPane is not { } area || page.ListTab is not { } listTab || !area.Items.Contains(listTab)) throw new Exception("the Hosts tab has no session area with the host list in it");
                // Connect from the page: the session opens inside the Hosts tab.
                page.ArmSessions();
                var t1 = mw.AddTab("inside", new TerminalView(host, connectOnOpen: false));
                mine.Add(t1);
                if (P.PaneOf(t1) != area) throw new Exception("a session from Hosts didn't open inside the Hosts tab");
                if (page.HeaderRow.Visibility == Visibility.Visible) throw new Exception("the page's title and toolbar didn't step aside for the session");
                // A screen that isn't a session never lands inside the Hosts tab, even while you're in there.
                var t2 = mw.AddTab("not-a-session", new Border());
                mine.Add(t2);
                if (P.IsEmbedded(P.PaneOf(t2))) throw new Exception("a non-session screen opened inside the Hosts tab");
                // Back to the list: the toolbar returns; the list tab can't be closed or dragged away.
                area.SelectedItem = listTab;
                if (page.HeaderRow.Visibility != Visibility.Visible) throw new Exception("the toolbar didn't come back with the list");
                if ((listTab.Tag as string) != "fixed") throw new Exception("the list tab is closable");
                // Splits work inside the Hosts tab.
                if (!P.Split(t1, Orientation.Horizontal) || P.EmbeddedPanes.Count() != 2) throw new Exception("split inside the Hosts tab");
                // Not armed, from the main window: a session opens as an ordinary tab.
                P.ActivateMain();
                var t3 = mw.AddTab("outside", new TerminalView(host, connectOnOpen: false));
                mine.Add(t3);
                if (P.IsEmbedded(P.PaneOf(t3))) throw new Exception("a session not opened from Hosts landed inside it");
                // Separate tabs: the sessions move out to ordinary tabs; the list is the page's own again.
                HostsView.SetOpenIn("tab");
                if (page.SessionPane != null || P.IsEmbedded(P.PaneOf(t1)) || !P.Contains(t1) || P.Panes.Any(p => p.Items.Contains(listTab))) throw new Exception("switching to separate tabs didn't move the sessions out");
                if (PolicySettings.Catalog.FirstOrDefault(x => x.Name == nameof(AppSettings.HostsOpenIn)) is not { Choices: { } ch } || !ch.SequenceEqual(new[] { "hosts", "tab" }))
                    throw new Exception("HostsOpenIn is not a policy with the choices hosts, tab");
            }
            finally
            {
                foreach (var t in mine) if (P.Contains(t)) mw.CloseTab(t);
                HostsView.SetOpenIn(before == "tab" ? "tab" : "hosts");
                s.HostsOpenIn = before;
                P.ActivateMain();
            }
            return Task.CompletedTask;
        });

        await Run("0.6.7 (DarkCipher0x): the Hosts page's tree is a full host pane: hosts inside their folders, filter, selecting a host selects it in the list, drop onto a host = into its folder", () =>
        {
            var mw = MainWindow.Instance ?? throw new Exception("no main window");
            var host = new HostEntry { Id = "selftest-tree", Name = "tree-test-host", Address = "192.0.2.41", Port = 22, Username = "test", Group = "SelfTest/Tree" };
            AppData.Current.Hosts.Add(host);
            try
            {
                mw.Navigate("hosts");
                var page = mw.Panes.AllTabs.Select(t => t.Content).OfType<HostsView>().FirstOrDefault() ?? throw new Exception("no Hosts tab");
                page.TreeFilterText = "";
                page.RebuildForTest();
                if (page.TreeHostsForTest.All(h => h.Id != host.Id)) throw new Exception("the host isn't inside its folder in the tree");
                var leaf = page.TreeLeafForTest(host.Id) ?? throw new Exception("no tree node for the host");
                if (page.ParentKeyForTest(leaf) != "SelfTest/Tree") throw new Exception("the host's node isn't under its folder: " + page.ParentKeyForTest(leaf));
                leaf.IsSelected = true;
                if (page.FolderForTest != "SelfTest/Tree") throw new Exception("selecting a host didn't show its folder in the list: " + page.FolderForTest);
                page.TreeFilterText = "tree-test";
                if (page.TreeHostsForTest.Count != 1 || page.TreeHostsForTest[0].Id != host.Id) throw new Exception("the filter shows " + page.TreeHostsForTest.Count + " host(s)");
                page.TreeFilterText = "zz-no-such-host";
                if (page.TreeHostsForTest.Count != 0) throw new Exception("the filter kept non-matching hosts");
                page.TreeFilterText = "";
            }
            finally
            {
                AppData.Current.Hosts.RemoveAll(h => h.Id == host.Id);
                (mw.Panes.AllTabs.Select(t => t.Content).OfType<HostsView>().FirstOrDefault())?.RebuildForTest();
            }
            return Task.CompletedTask;
        });

        await Run("0.6.6 (DarkCipher0x): Microsoft 365 admin, read-only: Intune devices, Defender incidents, Entra users through a fake Graph (no network), portal links only to the admin centres", async () =>
        {
            var tenantBefore = s.M365Tenant; var clientBefore = s.M365ClientId;
            try
            {
                s.M365Tenant = "contoso.example"; s.M365ClientId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
                Microsoft365.GraphReader.TokenForTest = () => Task.FromResult("test-token");
                string K(string u) => new Uri(u).AbsoluteUri;
                var dev = "11111111-2222-3333-4444-555555555555";
                byte[] B(string j) => System.Text.Encoding.UTF8.GetBytes(j);
                var seen = new List<(string Host, bool Authorized)>();
                var graph = new FakeRoutes(new()
                {
                    [K("https://graph.microsoft.com/v1.0/deviceManagement/managedDevices?$select=id,deviceName,operatingSystem,osVersion,complianceState,lastSyncDateTime,userPrincipalName,model,manufacturer,isEncrypted,managementAgent&$top=200")] =
                        (200, B("{\"value\":[{\"id\":\"" + dev + "\",\"deviceName\":\"LAPTOP-07\",\"complianceState\":\"noncompliant\",\"operatingSystem\":\"Windows\",\"osVersion\":\"10.0.26100\",\"lastSyncDateTime\":\"2026-10-21T10:00:00Z\",\"userPrincipalName\":\"a.user@example.com\",\"isEncrypted\":true}],\"@odata.nextLink\":\"https://evil.example/next\"}"), null),
                    [K("https://graph.microsoft.com/v1.0/security/incidents?$orderby=createdDateTime desc&$top=50")] =
                        (200, B("{\"value\":[{\"displayName\":\"Phishing on one endpoint\",\"severity\":\"high\",\"status\":\"active\",\"incidentWebUrl\":\"https://security.microsoft.com/incidents/42\"},{\"displayName\":\"Elsewhere\",\"severity\":\"low\",\"incidentWebUrl\":\"https://evil.example/x\"}]}"), null),
                    [K("https://graph.microsoft.com/v1.0/users?$select=id,displayName,userPrincipalName,accountEnabled,jobTitle,department,userType,createdDateTime&$top=200")] =
                        (200, B("{\"value\":[{\"id\":\"" + dev + "\",\"displayName\":\"Zed\",\"userPrincipalName\":\"z@example.com\",\"accountEnabled\":false},{\"id\":\"x\",\"displayName\":\"Amy\",\"userPrincipalName\":\"amy@example.com\",\"accountEnabled\":true}]}"), null),
                }, seen);
                var devices = await Microsoft365.IntuneAdmin.DevicesAsync(CancellationToken.None, graph);
                if (devices.Rows.Count != 1 || devices.Rows[0].Cells[0] != "LAPTOP-07" || devices.Rows[0].Cells[1] != "noncompliant" || devices.Rows[0].PortalUrl?.StartsWith("https://intune.microsoft.com/") != true) throw new Exception("Intune devices: " + string.Join(" | ", devices.Rows.Select(r => string.Join(",", r.Cells))));
                var inc = await Microsoft365.DefenderAdmin.IncidentsAsync(CancellationToken.None, graph);
                if (inc.Rows.Count != 2 || inc.Rows[0].PortalUrl != "https://security.microsoft.com/incidents/42" || inc.Rows[1].PortalUrl != null) throw new Exception("Defender incidents / portal links");
                var users = await Microsoft365.EntraAdmin.UsersAsync(CancellationToken.None, graph);
                if (users.Rows.Count != 2 || users.Rows[0].Cells[0] != "Amy" || users.Rows[1].Cells[2] != "disabled" || users.Rows[0].PortalUrl != null || users.Rows[1].PortalUrl == null) throw new Exception("Entra users (sorting, disabled, links from checked ids only)");
                if (seen.Any(x => x.Host != "graph.microsoft.com" || !x.Authorized)) throw new Exception("requests went off Graph: " + string.Join(", ", seen));
                try { await Microsoft365.EntraAdmin.SignInsAsync(CancellationToken.None, new FakeHttpStatus(_ => (403, "{\"error\":{\"code\":\"Authentication_RequestFromNonPremiumTenantOrB2CTenant\"}}"))); throw new Exception("a 403 was accepted"); }
                catch (InvalidOperationException ex) when (ex.Message.Contains("P1")) { }
                // The page: load through the fake Graph, filter.
                var view = new M365AdminView();
                view.Pick(0);
                await view.LoadAsync(force: true, graph);
                if (view.Table?.Rows.Count != 1 || view.ShownRows != 1) throw new Exception("page load: " + view.ShownRows);
                view.Filter = "nothing-matches";
                if (view.ShownRows != 0) throw new Exception("filter");
                if (ProtectedSettings.Find("m365.clientId") == null || PolicySettings.Catalog.All(x => x.Name != nameof(AppSettings.M365AdminEnabled))) throw new Exception("the app registration isn't admin-approved / the page isn't a policy");
                if (Microsoft365.M365Admin.Permissions.Any(p => !p.EndsWith(".Read.All"))) throw new Exception("a permission that isn't read-only");
            }
            finally
            {
                Microsoft365.GraphReader.TokenForTest = null;
                s.M365Tenant = tenantBefore; s.M365ClientId = clientBefore;
            }
        });

        await Run("0.6.6 (DarkCipher0x): tmux: the tmux line (install only when agreed, attach or create, your config from its own file, never nested), the config written as base64, names checked", () =>
        {
            var sessionBefore = s.TmuxSession; var configBefore = s.TmuxConfig;
            try
            {
                s.TmuxSession = "main";
                var plain = Ssh.Tmux.AttachLine(install: false, withConfig: false);
                if (!plain.Contains("tmux new-session -A -s main") || !plain.Contains("$TMUX") || plain.Contains("apt-get") || plain.Contains("-f ")) throw new Exception("plain line: " + plain);
                var full = Ssh.Tmux.AttachLine(install: true, withConfig: true);
                foreach (var need in new[] { "apt-get install -y tmux", "dnf install -y tmux", "pacman -S --noconfirm tmux", "apk add tmux", "brew install tmux", "S=sudo", "tmux source-file", "-f \"$HOME/.config/dcx-servermanagement/tmux.conf\" new-session -A -s main" })
                    if (!full.Contains(need)) throw new Exception("install line lacks " + need);
                s.TmuxSession = "x; rm -rf ~";
                if (Ssh.Tmux.SessionName != "main" || Ssh.Tmux.AttachLine(false, false).Contains("rm -rf")) throw new Exception("an unchecked session name reached the shell");
                var cfg = "set -g mouse on\r\nset -g prefix C-a\n# it's 'quoted' $HOME `x`\n";
                var cmd = Ssh.Tmux.WriteConfigCommand(cfg);
                var b64 = System.Text.RegularExpressions.Regex.Match(cmd, "printf '%s' '([A-Za-z0-9+/=]+)'").Groups[1].Value;
                if (b64.Length == 0 || System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64)) != cfg.Replace("\r\n", "\n") || cmd.Contains("$HOME `x`") || !cmd.Contains("umask 077")) throw new Exception("config command: " + cmd);
                try { Ssh.Tmux.WriteConfigCommand(new string('x', Ssh.Tmux.MaxConfigBytes + 1)); throw new Exception("an oversized config was accepted"); } catch (ArgumentException) { }
                // The button: SSH to Linux / macOS only; nothing typed while not connected.
                var linux = new HostEntry { Id = "selftest-tmux", Name = "tmux-host", Address = "192.0.2.30", Port = 22, Username = "test" };
                var win = new HostEntry { Id = "selftest-tmux-w", Name = "win-host", Address = "192.0.2.31", Port = 22, Username = "test", Platform = "windows" };
                var tv = new TerminalView(linux, connectOnOpen: false);
                var tw = new TerminalView(win, connectOnOpen: false);
                try
                {
                    if (!tv.TmuxButtonShown || tw.TmuxButtonShown) throw new Exception("tmux button offered on the wrong hosts");
                    tv.StartTmuxAsync().GetAwaiter().GetResult();
                    if (tv.LastTmuxLine != null) throw new Exception("tmux typed into a session that isn't connected");
                }
                finally { tv.Dispose(); tw.Dispose(); }
                if (PolicySettings.Catalog.All(x => x.Name != nameof(AppSettings.TmuxInstall))) throw new Exception("TmuxInstall is not a policy");
            }
            finally { s.TmuxSession = sessionBefore; s.TmuxConfig = configBefore; }
            return Task.CompletedTask;
        });

        await Run("0.6.4 (DarkCipher0x): Cloud PCs: listed through Microsoft Graph (fake, no network), direct launch links from checked ids only, the page and its settings", async () =>
        {
            var mw = MainWindow.Instance ?? throw new Exception("no main window");
            var tenantBefore = s.CloudPcTenant; var clientBefore = s.CloudPcClientId;
            try
            {
                CloudPc.CloudPcClient.TokenForTest = () => Task.FromResult("test-token");
                const string G = "https://graph.microsoft.com/v1.0/me/cloudPCs";
                var id1 = "11111111-2222-3333-4444-555555555555"; var id2 = "66666666-7777-8888-9999-000000000000";
                var p1 = System.Text.Encoding.UTF8.GetBytes("{\"value\":[{\"id\":\"" + id2 + "\",\"displayName\":\"Zed PC\",\"servicePlanName\":\"Windows 365 Enterprise 2vCPU\",\"imageDisplayName\":\"Windows 11\",\"provisioningType\":\"dedicated\"},{\"id\":\"not-a-guid\",\"displayName\":\"Bad\"}],\"@odata.nextLink\":\"https://graph.microsoft.com/v1.0/me/cloudPCs?$skiptoken=2\"}");
                var p2 = System.Text.Encoding.UTF8.GetBytes("{\"value\":[{\"id\":\"" + id1 + "\",\"displayName\":\"Alpha PC\"}],\"@odata.nextLink\":\"https://evil.example/next\"}");
                var seen = new List<(string Host, bool Authorized)>();
                var graph = new FakeRoutes(new()
                {
                    [G + "?$select=id,displayName,imageDisplayName,servicePlanName,provisioningType"] = (200, p1, null),
                    [G + "?$skiptoken=2"] = (200, p2, null),
                }, seen);
                var pcs = await CloudPc.CloudPcClient.ListAsync(CancellationToken.None, graph);
                if (pcs.Count != 2 || pcs[0].Name != "Alpha PC" || pcs[1].Plan != "Windows 365 Enterprise 2vCPU") throw new Exception("list: " + string.Join(", ", pcs.Select(p => p.Name)));
                if (seen.Any(x => x.Host != "graph.microsoft.com") || !seen.All(x => x.Authorized)) throw new Exception("requests went off Graph: " + string.Join(", ", seen));
                var refused = new FakeRoutes(new() { [G + "?$select=id,displayName,imageDisplayName,servicePlanName,provisioningType"] = (403, Array.Empty<byte>(), null) });
                try { await CloudPc.CloudPcClient.ListAsync(CancellationToken.None, refused); throw new Exception("a 403 was accepted"); }
                catch (InvalidOperationException ex) when (ex.Message.Contains("CloudPC.Read.All")) { }
                // Links: GUIDs only; the login hint escaped and only an e-mail-like name.
                if (CloudPc.CloudPcClient.WebClientLink(id1) != "https://windows.cloud.microsoft/webclient/ent/" + id1) throw new Exception("link: " + CloudPc.CloudPcClient.WebClientLink(id1));
                if (CloudPc.CloudPcClient.WebClientLink(id1, "a.user@example.com") is not { } hinted || !hinted.EndsWith("#loginHint=a.user%40example.com")) throw new Exception("login hint");
                if (CloudPc.CloudPcClient.WebClientLink("../../evil") != null || CloudPc.CloudPcClient.WebClientLink(id1, "x\" onload=") is { } bad && bad.Contains("onload")) throw new Exception("an unchecked id or hint went into a link");
                // 0.6.5: actions. Refused while off; Microsoft's documented paths from checked GUIDs; the rename body; refusals.
                var actionsBefore = s.CloudPcAllowActions;
                try
                {
                    s.CloudPcAllowActions = false;
                    try { await CloudPc.CloudPcClient.RunAsync(CloudPc.CloudPcAction.Restart, id1, CancellationToken.None); throw new Exception("an action ran while actions are off"); }
                    catch (InvalidOperationException ex) when (ex.Message.Contains("off")) { }
                    s.CloudPcAllowActions = true;
                    var calls = new List<string>();
                    var ok = new FakeHttpStatus(r => { calls.Add(r.Method + " " + r.RequestUri!.AbsoluteUri + " " + (r.Content?.ReadAsStringAsync().Result ?? "")); return (204, ""); });
                    foreach (var a in Enum.GetValues<CloudPc.CloudPcAction>())
                        await CloudPc.CloudPcClient.RunAsync(a, id1, CancellationToken.None, a == CloudPc.CloudPcAction.Rename ? "Build PC" : null, ok);
                    string[] want =
                    {
                        $"POST https://graph.microsoft.com/beta/me/cloudPCs/{id1}/reboot ",
                        $"POST https://graph.microsoft.com/beta/me/cloudPCs/{id1}/rename {{\"displayName\":\"Build PC\"}}",
                        $"POST https://graph.microsoft.com/v1.0/deviceManagement/virtualEndpoint/cloudPCs/{id1}/troubleshoot ",
                        $"POST https://graph.microsoft.com/beta/deviceManagement/virtualEndpoint/cloudPCs/{id1}/poweron ",
                        $"POST https://graph.microsoft.com/beta/deviceManagement/virtualEndpoint/cloudPCs/{id1}/poweroff ",
                    };
                    if (!calls.SequenceEqual(want)) throw new Exception("action calls: " + string.Join(" | ", calls));
                    try { await CloudPc.CloudPcClient.RunAsync(CloudPc.CloudPcAction.Restart, "../me", CancellationToken.None, null, ok); throw new Exception("an unchecked id reached Graph"); }
                    catch (ArgumentException) { }
                    try { await CloudPc.CloudPcClient.RunAsync(CloudPc.CloudPcAction.Rename, id1, CancellationToken.None, "bad\u0007name", ok); throw new Exception("a control character went into a name"); }
                    catch (ArgumentException) { }
                    var denied = new FakeHttpStatus(_ => (403, ""));
                    try { await CloudPc.CloudPcClient.RunAsync(CloudPc.CloudPcAction.PowerOff, id1, CancellationToken.None, null, denied); throw new Exception("a 403 was accepted"); }
                    catch (InvalidOperationException ex) when (ex.Message.Contains("administrator")) { }
                    if (ProtectedSettings.Find("cloudpc.actions") == null || PolicySettings.Catalog.All(x => x.Name != nameof(AppSettings.CloudPcAllowActions))) throw new Exception("Cloud PC actions aren't an admin-approved policy setting");
                }
                finally { s.CloudPcAllowActions = actionsBefore; }
                // Settings: the app registration is admin-approved; without one the page still works (no list).
                s.CloudPcTenant = ""; s.CloudPcClientId = "";
                if (CloudPc.CloudPcClient.Configured) throw new Exception("configured without an app registration");
                if (ProtectedSettings.Find("cloudpc.clientId") == null) throw new Exception("the Cloud PCs app registration isn't an admin-approved setting");
                var view = new CloudPcView();
                if (view.Shown.Count != 0) throw new Exception("the page listed something without signing in");
                mw.BuildNav();
                if (s.CloudPcEnabled && !mw.AllNavKeys.Contains("cloudpc")) throw new Exception("no Cloud PCs page in the sidebar");
            }
            finally
            {
                CloudPc.CloudPcClient.TokenForTest = null;
                s.CloudPcTenant = tenantBefore; s.CloudPcClientId = clientBefore;
            }
        });

        await Run("0.6.4 (DarkCipher0x): extra windows for other monitors: their own tabs and split panes, their own host pane (pinned / auto-collapse), new tabs open in the window you're in, remembered", () =>
        {
            var mw = MainWindow.Instance ?? throw new Exception("no main window");
            var P = mw.Panes;
            var modeBefore = s.WindowHostPane;
            FloatWindow? w = null;
            var mine = new List<TabItem>();
            try
            {
                w = P.NewWindow();
                if (!w.KeepOpen || !P.Floats.Contains(w) || w.HostPane == null || !w.Label.StartsWith("Window ")) throw new Exception("New window: " + w.Label);
                // While the window is the active one, new tabs open in it.
                P.ActivateWindow(w);
                mine.Add(mw.AddTab("w-one", new Border()));
                mine.Add(mw.AddTab("w-two", new Border()));
                if (mine.Any(t => P.WindowOf(t) != w)) throw new Exception("a new tab didn't open in the active window");
                // Split inside the window; the main window is untouched.
                int mainPanes = P.PaneCount;
                if (!P.Split(mine[1], Orientation.Horizontal) || PaneHost.PanesOf(w).Count() != 2 || P.PaneCount != mainPanes) throw new Exception("split inside the window");
                P.MoveToNextPane(mine[1]);
                if (P.WindowOf(mine[1]) != w) throw new Exception("Move to next pane left the window");
                var e = mw.WorkspaceEntries();   // the window's pane is part of the workspace
                // Back to the main window: new tabs open there again.
                P.ActivateMain();
                mine.Add(mw.AddTab("main-one", new Border()));
                if (P.IsFloating(mine[2])) throw new Exception("a tab opened in the window while the main window was active");
                // Move between windows; the window's last tab leaving keeps a New window open (empty).
                P.MoveToWindow(mine[2], w);
                if (P.WindowOf(mine[2]) != w) throw new Exception("Move to window");
                foreach (var t in mine.ToList()) { P.DockBack(t); }
                if (!P.Floats.Contains(w) || PaneHost.PanesOf(w).Any(p => p.Items.Count > 0)) throw new Exception("a New window closed (or kept tabs) when its tabs left");
                // A popped-out tab's window still closes with its last tab (as before).
                var pop = P.PopOut(mine[0]) ?? throw new Exception("pop out");
                if (pop.KeepOpen) throw new Exception("a pop-out window keeps itself open");
                P.DockBack(mine[0]);
                if (P.Floats.Contains(pop)) throw new Exception("the empty pop-out window stayed");
                // The host pane: pinned, or auto-collapsing to a Hosts tab and sliding out.
                w.SetMode("auto");
                if (w.HostPaneOpen) throw new Exception("auto: the host pane didn't fold");
                w.SetPaneOpen(true);
                if (!w.HostPaneOpen) throw new Exception("auto: the host pane didn't slide out");
                w.SetMode("pinned");
                if (!w.HostPaneOpen) throw new Exception("pinned: the host pane isn't open");
                var place = w.Placement();
                if (place.Width <= 0 || place.Height <= 0) throw new Exception("placement");
                if (PolicySettings.Catalog.FirstOrDefault(x => x.Name == nameof(AppSettings.WindowHostPane)) is not { Choices: { } ch } || !ch.SequenceEqual(new[] { "pinned", "auto" }))
                    throw new Exception("WindowHostPane is not a policy with the choices pinned, auto");
                if (PolicySettings.Catalog.All(x => x.Name != nameof(AppSettings.RestoreWorkspaceWindows))) throw new Exception("RestoreWorkspaceWindows is not a policy");
            }
            finally
            {
                foreach (var t in mine) if (P.Contains(t)) mw.CloseTab(t);
                if (w != null) P.CloseWindow(w);
                s.WindowHostPane = modeBefore;
                P.ActivateMain();
            }
            return Task.CompletedTask;
        });

        await Run("0.6.3 (DarkCipher0x): sidebar pages can be rearranged (drag or Move up / down, within their section) and pinned (drag onto PINNED, the 📌, reorder pins, drag out to unpin)", () =>
        {
            var mw = MainWindow.Instance ?? throw new Exception("no main window");
            var orderBefore = s.NavItemOrder.ToList(); var pinsBefore = s.PinnedNavItems.ToList(); var hiddenBefore = s.HiddenNavItems.ToList();
            try
            {
                s.NavItemOrder.Clear(); s.PinnedNavItems.Clear(); s.HiddenNavItems.Clear();
                mw.BuildNav();
                var servers = mw.NavItemsOf("SERVERS").ToList();
                if (servers.Count < 3 || servers[0] != "hosts") throw new Exception("default SERVERS order: " + string.Join(",", servers));
                // Move up / down and drag within the section.
                var third = servers[2];
                mw.MoveNavItem(third, -1);
                if (mw.NavItemsOf("SERVERS")[1] != third) throw new Exception("Move up: " + string.Join(",", mw.NavItemsOf("SERVERS")));
                var lastPage = servers[^1];   // 0.6.16: Notes moved to DOCUMENTS; drag SERVERS' own last page instead
                mw.DropNavItem(lastPage, false, "hosts", false);
                if (mw.NavItemsOf("SERVERS")[0] != lastPage || !s.NavItemOrder.Contains(lastPage)) throw new Exception("drag to the top: " + string.Join(",", mw.NavItemsOf("SERVERS")));
                // A page never leaves its section.
                mw.DropNavItem("credentials", false, "hosts", false);
                if (mw.NavItemsOf("SERVERS").Contains("credentials") || !mw.NavItemsOf("VAULT").Contains("credentials")) throw new Exception("a page moved into another section");
                // Pin by dropping on PINNED, at a pinned page, reorder the pins, unpin by dragging out.
                if (!mw.PinNavAt("notes", null) || !mw.PinNavAt("audit", "notes")) throw new Exception("pinning refused");
                if (!s.PinnedNavItems.SequenceEqual(new[] { "audit", "notes" })) throw new Exception("pin order: " + string.Join(",", s.PinnedNavItems));
                mw.DropNavItem("notes", true, "audit", true);
                if (!s.PinnedNavItems.SequenceEqual(new[] { "notes", "audit" })) throw new Exception("pin reorder: " + string.Join(",", s.PinnedNavItems));
                mw.DropNavItem("audit", true, "hosts", false);
                if (s.PinnedNavItems.Contains("audit")) throw new Exception("dragging a pin out didn't unpin it");
                // The order survives a rebuild; Reset sidebar layout clears it.
                mw.BuildNav();
                if (mw.NavItemsOf("SERVERS")[0] != lastPage) throw new Exception("the order didn't survive a rebuild");
                mw.ResetNavLayout();
                if (s.NavItemOrder.Count != 0 || mw.NavItemsOf("SERVERS")[0] != "hosts") throw new Exception("reset kept the page order");
                if (NavLayout.WithSection(new[] { "a", "x", "b" }, new[] { "b", "a" }) is var w && !w.SequenceEqual(new[] { "x", "b", "a" })) throw new Exception("WithSection: " + string.Join(",", w));
                if (PolicySettings.Catalog.All(e => e.Name != nameof(AppSettings.NavItemOrder))) throw new Exception("NavItemOrder is not a policy");
            }
            finally
            {
                s.NavItemOrder = orderBefore; s.PinnedNavItems = pinsBefore; s.HiddenNavItems = hiddenBefore;
                mw.BuildNav();
            }
            return Task.CompletedTask;
        });

        await Run("0.6.2 (DarkCipher0x): the Hosts page's folder pane pins open or auto-collapses INSIDE the page; nothing docks beside the sessions", () =>
        {
            var mw = MainWindow.Instance ?? throw new Exception("no main window");
            var before = s.HostsDock;
            TabItem? probe = null;
            try
            {
                mw.Navigate("hosts");
                var page = mw.Panes.AllTabs.Select(t => t.Content).OfType<HostsView>().FirstOrDefault() ?? throw new Exception("Hosts did not open as a tab");
                // Pinned (the default): the pane has its column beside the list, as before.
                HostsView.SetFolderMode("pinned");
                if (page.FolderHost.Visibility != Visibility.Visible || Grid.GetColumnSpan(page.FolderHost) != 1 || page.FolderStrip.Visibility == Visibility.Visible)
                    throw new Exception("pinned: the folder pane is not in its column");
                if (page.FolderCol.Width.Value is < 140 or > 520) throw new Exception("pinned: folder pane width " + page.FolderCol.Width.Value);
                if ((string)page.PinButton.Content != "📌") throw new Exception("pinned: the pane's pin shows " + page.PinButton.Content);
                // Opening a session tab changes nothing on the Hosts page (no pane beside the sessions any more).
                probe = mw.AddTab("selftest", new Border());
                if (!page.FolderPaneOpen) throw new Exception("pinned: the pane folded when a tab opened");
                // Auto-collapse: a narrow strip inside the page; the pane slides out over the list and back.
                HostsView.SetFolderMode("auto");
                if (Math.Abs(page.FolderCol.Width.Value - 22) > 0.5 || page.FolderStrip.Visibility != Visibility.Visible || page.FolderHost.Visibility == Visibility.Visible)
                    throw new Exception("auto: the pane did not fold to the strip (column " + page.FolderCol.Width.Value + ")");
                if ((string)page.PinButton.Content != "📍") throw new Exception("auto: the pane's pin shows " + page.PinButton.Content);
                page.SetFolderOpen(true);
                if (page.FolderHost.Visibility != Visibility.Visible || Grid.GetColumnSpan(page.FolderHost) != 3 || Math.Abs(page.FolderCol.Width.Value - 22) > 0.5)
                    throw new Exception("auto: the open pane is not sliding out over the list (the list must not resize)");
                page.SetFolderOpen(false);
                if (page.FolderHost.Visibility == Visibility.Visible) throw new Exception("auto: the pane did not slide back");
                // 0.5.8–0.6.1's "tab" reads as pinned; the policy offers pinned / auto.
                s.HostsDock = "tab";
                if (HostsView.FolderMode != "pinned") throw new Exception("the old 'tab' value isn't read as pinned");
                if (PolicySettings.Catalog.FirstOrDefault(e => e.Name == nameof(AppSettings.HostsDock)) is not { Choices: { } ch } || !ch.SequenceEqual(new[] { "pinned", "auto" }))
                    throw new Exception("HostsDock is not a policy with the choices pinned, auto");
            }
            finally
            {
                if (probe != null && mw.Panes.Contains(probe)) mw.CloseTab(probe);
                HostsView.SetFolderMode(before == "auto" ? "auto" : "pinned");
                s.HostsDock = before;
            }
            return Task.CompletedTask;
        });

        await Run("0.5.7 fix (DarkCipher0x): approvals and records show the profile's name, not PC\\user (another administrator keeps their account)", () =>
        {
            var account = System.Security.Principal.WindowsIdentity.GetCurrent();
            if (AppIdentity.Show(account.Name) != AppIdentity.Name) throw new Exception($"this account showed as {AppIdentity.Show(account.Name)}, not the profile name");
            if (AppIdentity.Show("OTHERPC\\admin") != "OTHERPC\\admin") throw new Exception("another account was renamed");
            if (AppIdentity.Clean("Dark\u0007Cipher\n0x") != "DarkCipher0x") throw new Exception("control characters kept: " + AppIdentity.Clean("Dark\u0007Cipher\n0x"));
            if (AppIdentity.Clean(new string('x', 100)).Length != 64) throw new Exception("name not capped");
            if (AppIdentity.Slug().Any(c => !(char.IsLetterOrDigit(c) || c == '-'))) throw new Exception("slug: " + AppIdentity.Slug());
            if (AppIdentity.Name.Contains('\\') && AppIdentity.Name == account.Name) throw new Exception("the profile name is the Windows account");
            // The approval record: your own approval → the profile's name; a different administrator → their account.
            var req = ProtectedSettings.BuildRequest(s, new Dictionary<string, JsonNode?>(), "selftest");
            if (req.Name != AppIdentity.Name) throw new Exception("the request does not carry the profile's name");
            req.Name = "DarkCipher0x";
            var mine = new ProtectedSettings.Plan(req, account.Name, new());
            if (ProtectedSettings.Approver(mine) != "DarkCipher0x") throw new Exception("own approval recorded as " + ProtectedSettings.Approver(mine));
            var other = new ProtectedSettings.Plan(new ProtectedSettings.Request { Sid = "S-1-5-21-1-2-3-4242", Name = "Spoofed" }, "OTHERPC\\x", new());
            if (ProtectedSettings.Approver(other) != account.Name) throw new Exception("another account's request recorded the requester's chosen name: " + ProtectedSettings.Approver(other));
            return Task.CompletedTask;
        });

        await Run("0.5.4 fix (DarkCipher0x): the mouse is never left captured once every button is up (capture guard)", () =>
        {
            // DarkCipher0x: after a right-click in LOGS → Error log the mouse stayed "locked into that pane"; only the pane's own
            // buttons worked. The rule: with no button pressed, only menus, drop-downs and popups may hold the capture.
            CaptureGuard.Install();
            if (CaptureGuard.Legitimate(new Border())) throw new Exception("a plain element counted as a legitimate holder");
            if (CaptureGuard.Legitimate(new TextBox())) throw new Exception("a text box counted as a legitimate holder");
            if (!CaptureGuard.Legitimate(new ContextMenu()) || !CaptureGuard.Legitimate(new MenuItem()) || !CaptureGuard.Legitimate(new Menu()))
                throw new Exception("a menu was not treated as a legitimate holder");
            var inMenu = new TextBlock(); var item = new MenuItem { Header = inMenu };
            if (!CaptureGuard.Legitimate(inMenu)) throw new Exception("an element inside a menu item was not treated as legitimate");
            if (CaptureGuard.Legitimate(new ComboBox())) throw new Exception("a closed drop-down counted as a legitimate holder");
            var desc = CaptureGuard.Describe(inMenu);
            if (!desc.StartsWith("TextBlock in MenuItem", StringComparison.Ordinal)) throw new Exception("description: " + desc);
            // The real thing, in the app's own (off-screen) window: something grabs the mouse and never lets go.
            if (MainWindow.Instance?.StatusText is { } held && held.IsVisible)
            {
                int before = CaptureGuard.Releases;
                var physical = CaptureGuard.AnyButtonDown;
                try
                {
                    if (held.CaptureMouse() && System.Windows.Input.Mouse.Captured == held)
                    {
                        // 0.5.5: Windows' physical button state decides, not WPF's (which a lost button-up leaves "pressed").
                        CaptureGuard.AnyButtonDown = () => true;    // a real drag in progress: hands off
                        if (CaptureGuard.Sweep() || System.Windows.Input.Mouse.Captured != held) throw new Exception("a capture was released while a button was physically down");
                        CaptureGuard.AnyButtonDown = () => false;   // every button physically up: let go
                        if (!CaptureGuard.Sweep() || System.Windows.Input.Mouse.Captured != null || CaptureGuard.Releases != before + 1)
                            throw new Exception("a stuck capture with every button physically up was not released");
                    }
                }
                finally { CaptureGuard.AnyButtonDown = physical; if (System.Windows.Input.Mouse.Captured == held) held.ReleaseMouseCapture(); }
            }
            return Task.CompletedTask;
        });

        await Run("0.5.3 fix (DarkCipher0x): a lost WPF wake-up message can't stall the UI for good (dispatcher watchdog)", async () =>
        {
            // 0.5.2 still froze after a right-click in LOGS: a dump showed the UI thread idle, 138 operations queued and WPF's
            // "wake-up posted" flag set with no message on its way. This puts the dispatcher into that exact state and
            // checks queued work runs again within seconds. No window, no input.
            var d = Application.Current.Dispatcher;
            if (!DispatcherWatchdog.Running && !DispatcherWatchdog.Start(d)) throw new Exception("the watchdog did not start");
            if (DispatcherWatchdog.WindowOf(d) == IntPtr.Zero) throw new Exception("WPF's dispatcher window was not found (WPF internals changed?)");
            long wpf = DispatcherWatchdog.WpfWakeMessage();
            if (wpf <= 0 || wpf != DispatcherWatchdog.WakeMessage) throw new Exception($"wake-up message differs: WPF {wpf}, the watchdog {DispatcherWatchdog.WakeMessage}");
            int kicks = DispatcherWatchdog.Kicks;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long stalledAt = -1;
            var ran = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            // Safety net: if the watchdog failed, this test must fail, not hang the run.
            using var net = new System.Threading.Timer(_ => DispatcherWatchdog.Kick(), null, 15000, Timeout.Infinite);
            // At idle priority nothing above it is queued, so no real wake-up is pending when the flag is forced.
            await d.InvokeAsync(() =>
            {
                if (!DispatcherWatchdog.SimulateLostWakeUp(d)) throw new Exception("WPF's _postedProcessingType was not found");
                stalledAt = sw.ElapsedMilliseconds;
                d.BeginInvoke(DispatcherPriority.Normal, new Action(() => ran.TrySetResult(sw.ElapsedMilliseconds)));
            }, DispatcherPriority.ApplicationIdle).Task.ConfigureAwait(false);
            var first = await Task.WhenAny(ran.Task, Task.Delay(20000)).ConfigureAwait(false);
            if (first != ran.Task) throw new Exception("queued work never ran after the simulated lost wake-up");
            long after = ran.Task.Result - stalledAt;
            if (after > 8000) throw new Exception($"queued work ran only after {after} ms (the safety net, not the watchdog)");
            if (DispatcherWatchdog.Kicks == kicks) throw new Exception($"work ran after {after} ms without the watchdog — the stall was not reproduced");
        });

        await Run("0.5.2 fix (DarkCipher0x): a text box without a menu of its own right-clicks into the app's menu, never WPF's built-in one", () =>
        {
            // 0.5.1: a right-click in LOGS → Error log opened WPF's built-in text box menu, and afterwards the app took no
            // clicks until it was killed. The menus are built here as a right-click would, never opened (no popup).
            static string[] Heads(TextBox b) => b.ContextMenu!.Items.OfType<MenuItem>().Select(i => i.Header?.ToString() ?? "").ToArray();
            var ro = new TextBox { IsReadOnly = true, Text = "selftest" };
            Menus.EnsureTextMenu(ro);
            if (ro.ContextMenu == null || Menus.BuilderOf(ro) == null) throw new Exception("a read-only box got no app menu");
            var h = Heads(ro);
            foreach (var need in new[] { "Copy", "Copy all", "Select all", "Find…" }) if (!h.Contains(need)) throw new Exception("read-only menu lacks " + need + ": " + string.Join(", ", h));
            if (h.Contains("Paste") || h.Contains("Cut")) throw new Exception("a read-only box offers Paste/Cut");
            // Security: a box given the menu automatically may show a secret, so nothing writes or sends its text.
            if (h.Contains("Save as…") || h.Contains("Share…")) throw new Exception("the automatic menu offers Save as / Share");
            if (!ro.IsInactiveSelectionHighlightEnabled) throw new Exception("the highlight would vanish while the menu is open");
            // Highlight, right-click, Copy: the menu built with a selection offers Copy for exactly that text.
            ro.Text = "alpha beta gamma"; ro.Select(6, 4);
            ro.ContextMenu!.Items.Clear(); Menus.Fill(ro.ContextMenu.Items, Menus.BuilderOf(ro)!());
            if (ro.ContextMenu.Items.OfType<MenuItem>().FirstOrDefault(i => i.Header?.ToString() == "Copy") is not { IsEnabled: true }) throw new Exception("Copy is not enabled with text highlighted");
            var ed = new TextBox();
            Menus.EnsureTextMenu(ed);
            if (!Heads(ed).Contains("Paste") || !Heads(ed).Contains("Cut")) throw new Exception("an editable box lacks Paste/Cut");
            var own = new TextBox();
            Menus.Attach(own, () => new object?[] { Menus.Item("Mine", () => { }) });
            var mine = own.ContextMenu;
            Menus.EnsureTextMenu(own);
            if (own.ContextMenu != mine) throw new Exception("a box with its own menu was given another");
            // The LOGS screen that froze: every text box in it gets the app's menu.
            var boxes = Menus.Descendants<TextBox>(new ErrorLogView()).ToList();
            if (boxes.Count < 2) throw new Exception($"found {boxes.Count} text box(es) in the Error log, expected the log and its filter");
            foreach (var tb in boxes) { Menus.EnsureTextMenu(tb); if (Menus.BuilderOf(tb) == null) throw new Exception("an Error log box has no app menu"); }
            return Task.CompletedTask;
        });

        await Run("0.5.1 fix (DarkCipher0x): a protected whole number made in code (the backup schedule) passes the check and applies", () =>
        {
            // 0.5.0 threw "A value of type 'System.Int32' cannot be converted to a 'System.Double'" saving the backup schedule.
            var it = ProtectedSettings.Find("backup.scheduleDays") ?? throw new Exception("no backup.scheduleDays");
            if (it.Check(JsonValue.Create(7)) != null || it.Check(JsonValue.Create(7L)) != null || it.Check(JsonNode.Parse("7")) != null) throw new Exception("a whole number was refused");
            if (it.Check(JsonValue.Create(7.5)) == null || it.Check(JsonValue.Create("7")) == null || it.Check(JsonValue.Create(100000)) == null) throw new Exception("a bad value passed");
            var copy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(s))!;
            it.Set(copy, JsonValue.Create(14));
            if (copy.BackupScheduleDays != 14) throw new Exception("not applied: " + copy.BackupScheduleDays);
            foreach (var p in ProtectedSettings.Items.Where(p => p.Get(s)?.GetValueKind() == JsonValueKind.Number))
                if (p.Check(p.Get(s)) is { } bad) throw new Exception($"{p.Key}: its own current value is refused ({bad})");
            return Task.CompletedTask;
        });

        await Run("0.5.1 restrictions: every Allow* policy refuses its action with the reason; scanning can be turned off", async () =>
        {
            foreach (var a in Restrictions.All)
            {
                var prop = typeof(AppSettings).GetProperty(a.Setting) ?? throw new Exception("no setting " + a.Setting);
                if (PolicySettings.Catalog.All(e => e.Name != a.Setting)) throw new Exception(a.Setting + " has no Group Policy");
                prop.SetValue(s, false);
                try
                {
                    if (Restrictions.Allowed(a.Action) || Restrictions.Why(a.Action) is not { } why || !why.Contains(a.Setting)) throw new Exception(a.Action + " still allowed");
                    try { Restrictions.Demand(a.Action, "you"); throw new Exception(a.Action + " Demand passed"); } catch (UnauthorizedAccessException) { }
                }
                finally { prop.SetValue(s, true); }
            }
            s.AllowNetworkScan = false;
            try
            {
                try { await HostDiscovery.ScanAsync("127.0.0.1", new[] { 22 }, new Progress<int>(), CancellationToken.None); throw new Exception("a scan ran with AllowNetworkScan off"); }
                catch (UnauthorizedAccessException) { }
                try { await PortScanner.RunAsync(new[] { "127.0.0.1" }, new[] { 22 }, new Progress<OpenPort>(), new Progress<int>(), CancellationToken.None); throw new Exception("the port scanner ran with AllowNetworkScan off"); }
                catch (UnauthorizedAccessException) { }
            }
            finally { s.AllowNetworkScan = true; }
        });

        await Run("0.5.1 add-ons: blocked ids, the allowed list and signed-only programs are refused (data-only add-ons have no program)", () =>
        {
            var blocked = s.AddonBlockedIds.ToList(); var allowed = s.AddonAllowedIds.ToList(); var signed = s.AddonRequireSigned;
            var program = new AddonManifest { Id = "com.example.selftest.policy", Entry = "tool.exe" };
            var theme = new AddonManifest { Id = "com.example.selftest.theme", Theme = "theme.json" };
            try
            {
                if (AddonPackages.PolicyRefusal(program, "Signed by Example (signature valid)") != null) throw new Exception("refused with no policy");
                s.AddonBlockedIds = new() { program.Id };
                if (AddonPackages.PolicyRefusal(program, "Signed by Example (signature valid)") is not { } b || !b.Contains("blocked")) throw new Exception("a blocked id was allowed");
                s.AddonBlockedIds = new(); s.AddonAllowedIds = new() { "com.example.other" };
                if (AddonPackages.PolicyRefusal(program, "Signed by Example (signature valid)") is not { } a || !a.Contains("AddonAllowedIds")) throw new Exception("an id off the allowed list was allowed");
                s.AddonAllowedIds = new(); s.AddonRequireSigned = true;
                if (AddonPackages.PolicyRefusal(program, "NOT signed") == null) throw new Exception("an unsigned program was allowed");
                if (AddonPackages.PolicyRefusal(program, "script (no signature check)") == null) throw new Exception("an unsigned script was allowed");
                if (AddonPackages.PolicyRefusal(theme, "theme only (no program)") != null) throw new Exception("signing was required of a theme");
                s.AllowAddonInstall = false;
                if (AddonPackages.PolicyRefusal(theme, "theme only (no program)") is not { } off || !off.Contains("AllowAddonInstall")) throw new Exception("AllowAddonInstall = false let an install through");
                if (!s.AddonsNeedApproval) throw new Exception("add-on changes don't need admin approval by default");
            }
            finally { s.AddonBlockedIds = blocked; s.AddonAllowedIds = allowed; s.AddonRequireSigned = signed; s.AllowAddonInstall = true; }
            return Task.CompletedTask;
        });

        await Run("0.5.1 host import: a scan finds SSH and Apple Screen Sharing; review rows; import links the desktop to its SSH host", async () =>
        {
            using var ssh = new Banner("SSH-2.0-OpenSSH_9.6 selftest\r\n");
            using var vnc = new Banner("RFB 003.889\n");
            var found = await HostDiscovery.ScanAsync("127.0.0.1", new[] { ssh.Port, vnc.Port }, new Progress<int>(), CancellationToken.None, timeoutMs: 1500);
            var d = found.SingleOrDefault() ?? throw new Exception("found " + found.Count);
            if (d.Platform != "macos" || d.Ports.Count != 2) throw new Exception($"guess: {d.Platform} · {string.Join(", ", d.Ports.Select(p => p.Port + " " + p.Service))}");
            var rows = HostImportWindow.BuildRows(found, "SelfTestImport");
            var sshRow = rows.Single(r => r.Candidate.Protocol == "ssh");
            var vncRow = rows.Single(r => r.Candidate.Protocol == "vnc");
            if (vncRow.Type != "VNC (Mac)" || vncRow.Candidate.Vnc.Through != "ssh" || !rows.All(r => r.Import && !r.Saved)) throw new Exception("rows: " + string.Join(" | ", rows.Select(r => $"{r.Type} {r.Name} {r.Status}")));
            int applied = HostImportWindow.Apply(rows, new[] { new HostSuggestion("127.0.0.1", "studio-mac", "macos", "Desktops/Mac", "Mac (Screen Sharing on)", "Apple RFB") });
            if (applied != 2 || sshRow.Name != "studio-mac" || !vncRow.Name.StartsWith("studio-mac desktop", StringComparison.Ordinal) || sshRow.Folder != "Desktops/Mac") throw new Exception($"suggestions: {sshRow.Name} / {vncRow.Name} / {sshRow.Folder}");
            sshRow.User = "selftest-mac";
            var added = HostImportWindow.Import(rows);
            try
            {
                var hs = added.Single(h => h.IsSsh); var hv = added.Single(h => h.IsVnc);
                if (hv.Vnc.SshHostId != hs.Id || hv.Vnc.Kind != "mac" || hs.Username != "selftest-mac" || hs.Platform != "macos" || hs.Group != "Desktops/Mac") throw new Exception("imported: " + JsonSerializer.Serialize(new { hs.Name, hs.Group, hs.Platform, hv.Vnc }));
                if (!rows.All(r => HostDiscovery.AlreadySaved(r.Candidate))) throw new Exception("imported hosts aren't seen as saved");
                if (HostImportWindow.Import(HostImportWindow.BuildRows(found, "SelfTestImport")).Count != 0) throw new Exception("a second import added duplicates");
            }
            finally
            {
                foreach (var h in added) AppData.Current.Hosts.Remove(h);
                AppData.Current.Folders.RemoveAll(f => f.StartsWith("Desktops", StringComparison.Ordinal) || f.StartsWith("SelfTestImport", StringComparison.Ordinal));
                AppData.Save();
            }
        });

        await Run("0.5.1 File Commander: the not-connected pane lists nothing and refuses every change", async () =>
        {
            var r = FsRef.Parse("none");
            if (!r.IsNone || r != FsRef.None) throw new Exception("none doesn't parse");
            using var fs = r.Open();
            if ((await fs.ListAsync("/")).Count != 0) throw new Exception("the none pane listed something");
            try { await fs.MkdirAsync("/x"); throw new Exception("the none pane made a folder"); } catch (InvalidOperationException) { }
            if (s.CommanderRestoreLast) throw new Exception("the Commander reconnects to its last places by default");
        });

        await Run("0.5.1 navigation: a LOGS section holds the audit, session and error logs and observability", () =>
        {
            var nav = main.NavSectionsForTest;
            if (!nav.TryGetValue("LOGS", out var logs) || !logs.SequenceEqual(new[] { "audit", "sessionlogs", "errorlog", "observe" })) throw new Exception("LOGS: " + string.Join(",", logs ?? new()));
            if (nav.Where(kv => kv.Key != "LOGS").Any(kv => kv.Value.Intersect(logs).Any())) throw new Exception("a log screen is still in another section");
            return Task.CompletedTask;
        });

        await Run("0.6.16 navigation (DarkCipher0x: TOOLS, DOCUMENTS, \"a bit better organised\"): every page in its section, none in two", () =>
        {
            var nav = main.NavSectionsForTest;
            var want = new Dictionary<string, string[]>
            {
                ["TOOLS"] = new[] { "commander", "snippets", "tunnels", "lan", "toolbox" },
                ["DOCUMENTS"] = new[] { "notes", "docs", "incidents", "reports" },
                ["VAULT"] = new[] { "credentials", "knownhosts" },
                ["SYSTEM"] = new[] { "addons", "privacy", "help", "settings" },
            };
            foreach (var (section, keys) in want)
                if (!nav.TryGetValue(section, out var got) || keys.Any(k => !got.Contains(k))) throw new Exception($"{section}: {string.Join(",", got ?? new())}");
            var all = nav.SelectMany(kv => kv.Value).ToList();
            if (all.Count != all.Distinct().Count()) throw new Exception("a page is in two sections");
            if (nav.TryGetValue("SERVERS", out var servers) && servers.Intersect(want["TOOLS"].Concat(want["DOCUMENTS"])).Any()) throw new Exception("SERVERS still holds tools or documents: " + string.Join(",", servers));
            var order = MainWindow.NavSections.ToList();
            if (order.IndexOf("TOOLS") != order.IndexOf("SERVERS") + 1 || order.IndexOf("DOCUMENTS") != order.IndexOf("TOOLS") + 1) throw new Exception("section order: " + string.Join(",", order));
            return Task.CompletedTask;
        });

        await Run("0.5.1 dropdowns: every dropdown in Settings and the host editor shows its selection's text, never a type name", async () =>
        {
            main.Navigate("settings"); await Settle(600);
            var settings = SettingsView.Current ?? throw new Exception("no Settings screen");
            var bad = new List<string>();
            int checkedCount = Sweep(settings, bad);
            var editor = new HostEditWindow(new HostEntry { Name = "selftest-dropdowns", Address = "192.0.2.53" });
            if (editor.Content is FrameworkElement root) { root.Measure(new Size(900, 4000)); root.Arrange(new Rect(0, 0, 900, 4000)); checkedCount += Sweep(root, bad); }
            editor.Close();
            if (bad.Count > 0) throw new Exception(string.Join(" | ", bad.Take(8)));
            if (checkedCount < 25) throw new Exception("only " + checkedCount + " dropdowns could be checked");
        });

        await Run("0.5.1 VNC tab: connects (direct, loopback fake), draws what the server sends, disconnects", async () =>
        {
            using var fake = new TinyRfb();
            var h = new HostEntry { Name = "selftest-vnc", Address = "127.0.0.1", Port = fake.Port, Protocol = "vnc", Vnc = new VncSettings { Through = "direct" }, IsAdHoc = true };
            var view = new VncView(h, connectOnOpen: false);
            var tab = main.AddTab("🖵 selftest", view);
            try
            {
                await view.ConnectAsync();
                for (int i = 0; i < 40 && view.PixelForTest(1, 1) != 0x0000FF; i++) await Settle(100);
                if (!view.IsConnected) throw new Exception("not connected: " + view.StatusText);
                if (view.PixelForTest(1, 1) is not 0x0000FF) throw new Exception($"pixel {view.PixelForTest(1, 1):X6} · {view.StatusText}");
                if (!view.StatusText.Contains("RFB 3.8") || !view.StatusText.Contains("NOT encrypted")) throw new Exception("status: " + view.StatusText);
            }
            finally { main.CloseTab(tab); await Settle(200); }
            if (view.IsConnected) throw new Exception("still connected after the tab closed");
        });
    }

    /// <summary>Checks every ComboBox under <paramref name="root"/> that has a selection: the box must show text, and that
    /// text must not be a type name or a record's ToString ("Choice { Display = … }").</summary>
    private static int Sweep(DependencyObject root, List<string> bad)
    {
        int n = 0;
        foreach (var cb in Descendants(root).OfType<ComboBox>().Where(c => c.SelectedItem != null && !c.IsEditable).ToList())
        {
            cb.ApplyTemplate();
            cb.Measure(new Size(400, 60)); cb.Arrange(new Rect(0, 0, 400, 30));
            if (cb.Template?.FindName("SelectionBox", cb) is not ContentPresenter box) continue;
            box.ApplyTemplate(); box.Measure(new Size(400, 60)); box.Arrange(new Rect(0, 0, 400, 30));
            var text = string.Join(" ", Descendants(box).OfType<TextBlock>().Select(t => t.Text)).Trim();
            if (text.Length == 0 && cb.SelectionBoxItem is string sbi) text = sbi;
            n++;
            if (text.Contains(" { ", StringComparison.Ordinal) || text.StartsWith("DcxManager.", StringComparison.Ordinal) || text.StartsWith("System.", StringComparison.Ordinal))
                bad.Add($"{cb.Name}{(cb.ToolTip is string tip ? " (" + tip[..Math.Min(30, tip.Length)] + ")" : "")}: “{text}”");
        }
        return n;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject d)
    {
        var children = new List<DependencyObject>();
        if (d is Visual or System.Windows.Media.Media3D.Visual3D)
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) children.Add(VisualTreeHelper.GetChild(d, i));
        foreach (var l in LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>()) if (!children.Contains(l)) children.Add(l);
        foreach (var c in children)
        {
            yield return c;
            foreach (var g in Descendants(c)) yield return g;
        }
    }

    /// <summary>A loopback listener that greets every connection with a banner (an SSH or VNC server for the scan).</summary>
    private sealed class Banner : IDisposable
    {
        private readonly TcpListener _l = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        public int Port { get; }
        public Banner(string text)
        {
            _l.Start();
            Port = ((IPEndPoint)_l.LocalEndpoint).Port;
            var bytes = Encoding.ASCII.GetBytes(text);
            _ = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    try
                    {
                        var c = await _l.AcceptTcpClientAsync(_cts.Token);
                        _ = Task.Run(async () => { using (c) { try { await c.GetStream().WriteAsync(bytes); await Task.Delay(2000); } catch (IOException) { } } });
                    }
                    catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { break; }
                }
            });
        }
        public void Dispose() { _cts.Cancel(); _l.Stop(); }
    }

    /// <summary>A PRA appliance on 127.0.0.1 over TLS (its certificate can be swapped): a token, then one Jumpoint.</summary>
    private sealed class FakePra : IDisposable
    {
        private readonly TcpListener _l = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        public int Port { get; }
        public System.Security.Cryptography.X509Certificates.X509Certificate2 Cert { get; set; }
        public FakePra(System.Security.Cryptography.X509Certificates.X509Certificate2 cert)
        {
            Cert = cert;
            _l.Start();
            Port = ((IPEndPoint)_l.LocalEndpoint).Port;
            _ = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    TcpClient c;
                    try { c = await _l.AcceptTcpClientAsync(_cts.Token); } catch { return; }
                    _ = Task.Run(() => Serve(c));
                }
            });
        }
        private async Task Serve(TcpClient c)
        {
            using (c)
            {
                try
                {
                    using var ssl = new System.Net.Security.SslStream(c.GetStream(), false);
                    await ssl.AuthenticateAsServerAsync(new System.Net.Security.SslServerAuthenticationOptions { ServerCertificate = Cert, EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13 }, _cts.Token);
                    var buf = new byte[16384]; int n = 0;
                    while (n < buf.Length) { int r = await ssl.ReadAsync(buf.AsMemory(n), _cts.Token); if (r == 0) return; n += r; if (Encoding.ASCII.GetString(buf, 0, n).Contains("\r\n\r\n")) break; }
                    var path = Encoding.ASCII.GetString(buf, 0, n).Split("\r\n")[0].Split(' ') is { Length: > 1 } parts ? parts[1] : "/";
                    var body = path.StartsWith("/oauth2/token", StringComparison.Ordinal) ? "{\"access_token\":\"selftest-token\",\"expires_in\":3600}"
                             : path.StartsWith("/api/config/v1/jumpoint", StringComparison.Ordinal) ? "[{\"name\":\"dc1-jumpoint\",\"appliance_hostname\":\"jp1.lab\",\"connected\":true}]" : "[]";
                    var bytes = Encoding.UTF8.GetBytes(body);
                    await ssl.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"), _cts.Token);
                    await ssl.WriteAsync(bytes, _cts.Token);
                    await ssl.FlushAsync(_cts.Token);
                }
                catch { /* refused handshakes are expected */ }
            }
        }
        public void Dispose() { _cts.Cancel(); _l.Stop(); }
    }

    /// <summary>The smallest RFB 3.8 server: no password, an 8×8 desktop, one blue Raw rectangle, then quiet.</summary>
    private sealed class TinyRfb : IDisposable
    {
        private readonly TcpListener _l = new(IPAddress.Loopback, 0);
        public int Port { get; }
        public TinyRfb()
        {
            _l.Start();
            Port = ((IPEndPoint)_l.LocalEndpoint).Port;
            _ = Task.Run(async () =>
            {
                try
                {
                    using var c = await _l.AcceptTcpClientAsync();
                    var s = c.GetStream();
                    void W(byte[] b) => s.Write(b);
                    byte[] R(int n) { var b = new byte[n]; s.ReadExactly(b); return b; }
                    W("RFB 003.008\n"u8.ToArray()); R(12);
                    W(new byte[] { 1, 1 }); R(1);
                    W(new byte[] { 0, 0, 0, 0 });                         // SecurityResult OK
                    R(1);                                               // ClientInit
                    W(new byte[] { 0, 8, 0, 8, 32, 24, 0, 1, 0, 255, 0, 255, 0, 255, 16, 8, 0, 0, 0, 0, 0, 0, 0, 4 }.Concat("tiny"u8.ToArray()).ToArray());
                    R(20); var e = R(4); R((e[2] << 8 | e[3]) * 4); R(10);
                    var m = new MemoryStream();
                    m.Write(new byte[] { 0, 0, 0, 1, 0, 0, 0, 0, 0, 8, 0, 8, 0, 0, 0, 0 });
                    for (int i = 0; i < 64; i++) m.Write(new byte[] { 255, 0, 0, 0 });   // blue (B first)
                    W(m.ToArray());
                    var buf = new byte[256];
                    while (await s.ReadAsync(buf) > 0) { }
                }
                catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or EndOfStreamException) { }
            });
        }
        public void Dispose() => _l.Stop();
    }
}
