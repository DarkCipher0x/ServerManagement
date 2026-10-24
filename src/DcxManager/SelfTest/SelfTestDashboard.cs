using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DcxManager.Incidents;
using DcxManager.Infra;
using DcxManager.Notes;
using DcxManager.Services;
using DcxManager.Views;

namespace DcxManager;

/// <summary>
/// Spec §48 / ADR-018: the engineering dashboard. The model is checked directly; the real dashboard tab is driven
/// through its events. Infrastructure uses a fake provider (no network), terminals are created without connecting, and
/// no dialog is reached.
/// </summary>
public static class DashboardTests
{
    public static async Task RunAsync(MainWindow main, string reportDir, Action<string> ok, Action<string, Exception?> fail, Action<Window, string> snap)
    {
        async Task Settle(int ms = 200)
        {
            await Task.Delay(ms);
            await Application.Current.Dispatcher.InvokeAsync(() => main.UpdateLayout(), DispatcherPriority.ApplicationIdle);
        }
        var s = AppSettings.Current;
        var was = (Order: s.DashboardOrder.ToList(), Hidden: s.DashboardHidden.ToList(), Checks: s.DashboardChecks, Minutes: s.DashboardCheckMinutes, Sources: s.DashboardAutoSources.ToList());
        bool offlineWas = Offline.On;
        var factoryWas = InfraStatus.Factory;
        var fake = new ProviderConfig { Kind = "docker-local", Name = "dash-docker", Endpoint = "dcx-selftest-nopipe", Environment = "LAB" };
        var remote = new ProviderConfig { Kind = "proxmox", Name = "dash-pve", Endpoint = "https://203.0.113.10:8006", Environment = "PRODUCTION" };
        var prodHost = new HostEntry { Name = "dash-prod-db", Address = "192.0.2.31", Username = "ops", Environment = "PRODUCTION" };
        var tabs = new List<TabItem>();
        Note? note = null; Incident? incident = null;
        try
        {
            // 1. The model: layout, sessions, warnings, check rule.
            try
            {
                var order = Overview.Order(new[] { "notes", "bogus", "summary" });
                // Saved ids first; cards missing from the saved list keep their place relative to the default order.
                if (order[0] != "notes" || order.IndexOf("summary") < order.IndexOf("activity") || order.IndexOf("summary") > order.IndexOf("sessions") || order.Contains("bogus") || order.Count != Overview.Cards.Length)
                    throw new Exception("order: " + string.Join(",", order));
                var vis = Overview.Visible(Array.Empty<string>(), new[] { "cloud", "activity" });
                if (vis.Contains("cloud") || vis.Count != Overview.Cards.Length - 2) throw new Exception("visible");
                var two = Overview.Columns(new[] { "a", "b", "c" }, 1200);
                if (two.Count != 2 || !two[0].SequenceEqual(new[] { "a", "c" }) || !two[1].SequenceEqual(new[] { "b" })) throw new Exception("two columns");
                if (Overview.Columns(new[] { "a", "b" }, 800).Count != 1) throw new Exception("narrow should be one column");

                var rows = new[]
                {
                    new Overview.SessionRow("SSH", "a", "PRODUCTION", true, 1), new Overview.SessionRow("SSH", "b", "", false, 2),
                    new Overview.SessionRow("Remote Desktop", "c", "LAB", true, 3), new Overview.SessionRow("Server view", "d", "", null, 4),
                };
                var counts = Overview.Count(rows);
                if (counts.Count != 3 || counts[0] is not { Kind: "SSH", Open: 2, Connected: 1 } || counts[1].Kind != "Remote Desktop" || counts[2] is not { Kind: "Server view", Connected: 1 })
                    throw new Exception("counts: " + string.Join(", ", counts));

                var now = new DateTime(2026, 10, 18, 12, 0, 0);
                var clean = new Overview.Facts(Array.Empty<string>(), 0, 0, true, 24, now.AddHours(-3), null, null, 0, null, false, 0, 0, false, now);
                if (Overview.Warnings(clean).Count != 0) throw new Exception("clean facts gave warnings: " + string.Join(" | ", Overview.Warnings(clean)));
                var bad = clean with { RecoveryNotices = new[] { "data.dcx restored from journal" }, DamagedNotes = 2, LastAutoSnapshot = now.AddDays(-3), SyncedFolder = "OneDrive",
                    ErrorsThisSession = 4, InternetAi = "anthropic at api.anthropic.com", LocalApi = true, AddonsBlockedOffline = 1, FailedTransfers24h = 2, UpdatesOff = true };
                var w = Overview.Warnings(bad);
                string[] expect = { "Store recovery", "note file", "overdue", "OneDrive", "4 error", "2 transfer", "internet AI", "local API", "add-on", "Updates are switched off" };
                foreach (var e in expect) if (!w.Any(x => x.Text.Contains(e))) throw new Exception("missing warning: " + e);
                if (w.First(x => x.Text.Contains("Store recovery")).Level != "bad" || w.First(x => x.Text.Contains("overdue")).Level != "warn" || w.First(x => x.Text.Contains("Updates")).Level != "info") throw new Exception("levels");
                if (w.Where(x => x.Go != null).Any(x => !x.Go!.StartsWith("settings:") && x.Go is not ("notes" or "incidents" or "commander"))) throw new Exception("fix target");
                if (!Overview.Warnings(clean with { SnapshotFailure = "disk full" }).Single().Text.Contains("disk full")) throw new Exception("snapshot failure");
                if (Overview.Warnings(clean with { AutoSnapshots = false }).Single().Level != "warn") throw new Exception("snapshots off");

                var src = new Overview.Source("infra:x", "x", "localhost");   // allowed offline (TEST-NET addresses are outside your network)
                var ext = new Overview.Source("infra:y", "y", "203.0.113.10");
                s.DashboardAutoSources = new() { "infra:x", "infra:y" };
                s.DashboardChecks = "manual";
                if (Overview.AutoCheck(src, "open") == null) throw new Exception("manual ran a check");
                s.DashboardChecks = "open";
                if (Overview.AutoCheck(src, "open") != null || Overview.AutoCheck(src, "interval") == null) throw new Exception("open rule");
                if (Overview.AutoCheck(new Overview.Source("infra:z", "z", "192.0.2.41"), "open") == null) throw new Exception("excluded source checked");
                Offline.Set(true);
                if (Overview.AutoCheck(ext, "open") != "skipped: offline mode" || Overview.AutoCheck(src, "open") != null) throw new Exception("offline: " + Overview.AutoCheck(ext, "open"));
                if (!offlineWas) Offline.Set(false);
                s.DashboardChecks = "interval";
                if (Overview.AutoCheck(src, "interval") != null || Overview.AutoCheck(src, "open") == null) throw new Exception("interval rule");
                if (!new AppSettings().DashboardAutoSources.SequenceEqual(new[] { "vultr", "ai" }) || new AppSettings().DashboardChecks != "open") throw new Exception("defaults");
                ok("dashboard model: card order (unknown dropped, new in place), hide, two columns in reading order / one when narrow, sessions counted by kind (connected vs open), local warnings by level with a fix target each, check rule (manual / on open / interval, per-source opt-in, offline-refused sources skipped), defaults keep providers out of automatic checks");
            }
            catch (Exception ex) { fail("dashboard model", ex); }
            finally { if (!offlineWas && Offline.On) Offline.Set(false); }

            // 2. The real dashboard: remote checks only by the rule; offline skips without attempting.
            DashboardView dv;
            main.Navigate("dashboard");
            await Settle(400);
            dv = DashboardView.Current ?? throw new Exception("no dashboard");
            try
            {
                InfraStatus.Factory = c => new FakeProvider(c);
                AppData.Current.InfraProviders.AddRange(new[] { fake, remote });
                s.DashboardAutoSources = new() { };
                s.DashboardChecks = "open";
                dv.Contacted.Clear();
                await dv.RunChecksAsync("open");
                if (dv.Contacted.Count != 0 || InfraStatus.For(fake.Id) != null) throw new Exception("contacted with nothing included: " + string.Join(",", dv.Contacted));
                await Settle(400);
                if (!dv.BodyText("infra").Contains("not checked — not included in automatic checks")) throw new Exception("infra card: " + dv.BodyText("infra"));

                s.DashboardAutoSources = new() { "infra:" + fake.Id, "infra:" + remote.Id };
                s.DashboardChecks = "manual";
                await dv.RunChecksAsync("open"); await dv.RunChecksAsync("interval");
                if (dv.Contacted.Count != 0) throw new Exception("manual mode contacted " + string.Join(",", dv.Contacted));

                s.DashboardChecks = "open";
                Offline.Set(true);
                int blocked = Offline.BlockedTotal;
                await dv.RunChecksAsync("open");
                if (!dv.Contacted.SequenceEqual(new[] { "infra:" + fake.Id })) throw new Exception("open with offline: " + string.Join(",", dv.Contacted));
                if (Offline.BlockedTotal != blocked) throw new Exception("a skipped check still counted as a blocked attempt");
                var snapF = InfraStatus.For(fake.Id) ?? throw new Exception("no state recorded");
                if (snapF is not { Objects: 3, Running: 2, Stopped: 1, By: "dashboard · on open" } || !snapF.Caps.Reachable) throw new Exception("state: " + snapF);
                await Settle(400);
                var infra = dv.BodyText("infra");
                if (!infra.Contains("● 3 object(s) · 2 running · 1 stopped") || !infra.Contains("skipped: offline mode")) throw new Exception("infra card: " + infra);
                if (!offlineWas) Offline.Set(false);

                // "Check now" contacts every source, included or not.
                s.DashboardAutoSources = new() { };
                dv.Contacted.Clear();
                await dv.RunChecksAsync("manual", only: "infra:" + remote.Id);
                if (!dv.Contacted.SequenceEqual(new[] { "infra:" + remote.Id }) || InfraStatus.For(remote.Id)?.By != "dashboard · Check now") throw new Exception("check now");

                // Interval timer only in interval mode and only while on screen.
                s.DashboardChecks = "interval"; s.DashboardCheckMinutes = 15; dv.SetPoll();
                if (dv.PollingEvery != TimeSpan.FromMinutes(15)) throw new Exception("interval timer: " + dv.PollingEvery);
                s.DashboardChecks = "open"; dv.SetPoll();
                if (dv.PollingEvery != null) throw new Exception("timer left running outside interval mode");
                s.DashboardChecks = "interval"; dv.SetPoll();
                main.Navigate("hosts"); await Settle(300);
                if (dv.PollingEvery != null) throw new Exception("timer running while the dashboard is hidden");
                main.Navigate("dashboard"); await Settle(300);
                if (dv.PollingEvery == null) throw new Exception("timer not resumed");
                s.DashboardChecks = "open"; dv.SetPoll();

                var privacy = PrivacyReport.Endpoints().FirstOrDefault(e => e.Service.EndsWith("dash-docker"));
                s.DashboardAutoSources = new() { "infra:" + fake.Id };
                var privacy2 = PrivacyReport.Endpoints().First(e => e.Service.EndsWith("dash-docker"));
                if (privacy == null || privacy.State.Contains("dashboard") || !privacy2.State.Contains("dashboard checks when the dashboard opens")) throw new Exception("privacy row: " + privacy2.State);
                ok("dashboard remote checks: nothing contacted unless included, manual mode contacts nothing by itself, offline-refused sources skipped without a blocked attempt, last-known infrastructure state with source and time, Check now always works, the interval timer runs only in interval mode and only on screen, the Privacy view states the rule");
            }
            catch (Exception ex) { fail("dashboard remote checks", ex); }
            finally
            {
                InfraStatus.Factory = factoryWas;
                if (!offlineWas && Offline.On) Offline.Set(false);
                s.DashboardAutoSources = new() { };   // the rest of the test must not contact anything when the tab is shown again
            }

            // 3. Local facts arrive by events; redraws coalesce and wait while hidden.
            try
            {
                AppData.Current.Hosts.Add(prodHost);
                var term = new TerminalView(prodHost, connectOnOpen: false);
                tabs.Add(main.AddTab("⌨ " + prodHost.Display, term));
                main.Panes.Select(tabs[0]);
                await Settle(300);
                int r0 = dv.Redraws;
                for (int i = 0; i < 100; i++) LiveSessions.Touch();
                await Settle(500);
                if (dv.Redraws != r0) throw new Exception($"redrew {dv.Redraws - r0}× while hidden");
                main.Navigate("dashboard");
                await Settle(600);
                if (dv.Redraws - r0 is < 1 or > 2) throw new Exception($"catch-up redraws: {dv.Redraws - r0}");
                var sessions = dv.BodyText("sessions");
                if (!sessions.Contains("SSH: " + prodHost.Display) || !sessions.Contains("SSH")) throw new Exception("sessions card: " + sessions);
                // Earlier tests may leave other tabs open: the tile must match the real tabs.
                var real = main.Panes.AllTabs.Select(t => Overview.Classify(t.Content)).OfType<Overview.SessionRow>().ToList();
                var tile = $"{real.Count(r => r.Connected != false)}/{real.Count}";
                if (!dv.SummaryText.Contains(tile) || !dv.SummaryText.Contains($"{real.Count(r => Envs.IsProduction(r.Environment))} PRODUCTION") || !real.Any(r => r.Content == term))
                    throw new Exception($"summary (expected {tile}): " + dv.SummaryText.Replace("\n", " | "));
                if (!dv.BodyText("safety").Contains("1 PRODUCTION session(s): " + prodHost.Display)) throw new Exception("safety: " + dv.BodyText("safety"));

                int r1 = dv.Redraws;
                for (int i = 0; i < 500; i++) LiveSessions.Touch();
                await Settle(700);
                if (dv.Redraws - r1 is < 1 or > 3) throw new Exception($"a flood of 500 events gave {dv.Redraws - r1} redraws");

                note = NoteStore.Create("dash-note-selftest", new[] { prodHost.Id }, "checked the database");
                incident = IncidentStore.Create("dash-incident-selftest", new[] { prodHost.Id }, IncidentSeverity.Sev2, "PRODUCTION");
                await Settle(600);
                if (!dv.BodyText("notes").Contains("dash-note-selftest")) throw new Exception("notes card");
                if (!dv.BodyText("incidents").Contains("dash-incident-selftest") || !dv.SummaryText.Contains("worst")) throw new Exception("incidents card: " + dv.BodyText("incidents"));
                if (dv.BodyText("warnings").Contains("Updates are switched off") == Updater.Enabled) throw new Exception("warnings card (updates " + (Updater.Enabled ? "on" : "off") + "): " + dv.BodyText("warnings"));

                var shots = Directory.CreateDirectory(Path.Combine(reportDir, "shots")).FullName;
                snap(main, Path.Combine(shots, "dashboard.png"));

                main.CloseTab(tabs[0]); tabs.Clear();
                await Settle(600);
                if (dv.BodyText("sessions").Contains(prodHost.Display)) throw new Exception("closed tab still listed");
                ok("dashboard local facts: session tabs, PRODUCTION sessions, notes, incidents and warnings follow events with no polling; 100 events while hidden → no redraw, one catch-up when shown; 500 events → at most 3 redraws; closing a tab updates it; rendered dashboard.png");
            }
            catch (Exception ex) { fail("dashboard local facts", ex); }

            // 4. Customise: move, hide, reset; persisted.
            try
            {
                dv.ResetLayout();
                var before = Overview.Visible(s.DashboardOrder, s.DashboardHidden);
                dv.MoveCard("notes", -1);
                var after = Overview.Visible(s.DashboardOrder, s.DashboardHidden);
                if (after.IndexOf("notes") != before.IndexOf("notes") - 1) throw new Exception("move up");
                dv.SetCardHidden("cloud", true);
                if (dv.PlacedCards.Contains("cloud") || !s.DashboardHidden.Contains("cloud")) throw new Exception("hide");
                var saved = SecureStore.Read<AppSettings>("settings.dcx");
                if (!saved.DashboardHidden.Contains("cloud") || saved.DashboardOrder.Count == 0) throw new Exception("layout not saved");
                if (dv.LayoutColumns is < 1 or > 2) throw new Exception("columns " + dv.LayoutColumns);
                dv.ResetLayout();
                if (dv.PlacedCards.Count != Overview.Cards.Length || s.DashboardOrder.Count != 0) throw new Exception("reset");
                ok("dashboard customise: move up, hide (not placed, saved encrypted in settings), reset restores every card");
            }
            catch (Exception ex) { fail("dashboard customise", ex); }

            // 5. A disposed dashboard leaves no handler and is collected.
            try
            {
                int subs = LiveSessions.Subscribers;
                var weak = MakeAndDispose();
                if (LiveSessions.Subscribers != subs) throw new Exception($"handlers {subs} → {LiveSessions.Subscribers}");
                for (int i = 0; i < 30 && weak.IsAlive; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); await Task.Delay(100); }
                if (weak.IsAlive) throw new Exception("disposed dashboard still alive");
                main.Navigate("hosts"); await Settle(200); main.Navigate("dashboard"); await Settle(300);
                if (DashboardView.Current != dv) throw new Exception("the live dashboard was not restored as current");
                ok("dashboard lifetime: dispose unhooks every event (no handler left) and the view is collected");
            }
            catch (Exception ex) { fail("dashboard lifetime", ex); }
        }
        finally
        {
            InfraStatus.Factory = factoryWas;
            if (!offlineWas && Offline.On) Offline.Set(false);
            foreach (var t in tabs) { try { main.CloseTab(t); } catch { } }
            if (note != null) NoteStore.Delete(note);
            if (incident != null) IncidentStore.Delete(incident);
            AppData.Current.InfraProviders.RemoveAll(c => c.Id == fake.Id || c.Id == remote.Id);
            InfraStatus.Forget(fake.Id); InfraStatus.Forget(remote.Id);
            AppData.Current.Hosts.RemoveAll(h => h.Id == prodHost.Id);
            AppData.Save();
            s.DashboardOrder = was.Order; s.DashboardHidden = was.Hidden; s.DashboardChecks = was.Checks; s.DashboardCheckMinutes = was.Minutes; s.DashboardAutoSources = was.Sources;
            try { AppSettings.Save(); } catch { }
            DashboardView.Current?.SetPoll();
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference MakeAndDispose()
    {
        var d = new DashboardView();
        d.Dispose();
        return new WeakReference(d);
    }

    /// <summary>Answers like a reachable Docker host with three containers; contacts nothing.</summary>
    private sealed class FakeProvider(ProviderConfig c) : IInfraProvider
    {
        public ProviderConfig Config => c;
        public string Where => "fake";
        public NetClass? NetworkClass => NetClass.Local;
        public Task<ProviderCaps> DetectAsync(CancellationToken ct) => Task.FromResult(new ProviderCaps(true, true, false, "fake 1.0", "fake", Array.Empty<string>()));
        public Task<IReadOnlyList<InfraObject>> InventoryAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<InfraObject>>(new[]
        {
            new InfraObject("1", "container", "web", "running", "", "", new Dictionary<string, string>()),
            new InfraObject("2", "container", "db", "running", "", "", new Dictionary<string, string>()),
            new InfraObject("3", "container", "old", "exited", "", "", new Dictionary<string, string>()),
        });
        public IReadOnlyList<InfraAction> ActionsFor(InfraObject o) => Array.Empty<InfraAction>();
        public string? Forbidden(InfraAction a, InfraObject o, ProviderCaps caps) => "fake";
        public Task<ActionResult> RunAsync(InfraAction a, InfraObject o, CancellationToken ct) => Task.FromResult(new ActionResult(false, false, "fake"));
    }
}
