using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DcxManager.Services;
using DcxManager.Ssh;
using DcxManager.Views;
using DcxManager.Vultr;
using DcxManager.Documents;

namespace DcxManager;

/// <summary>
/// DcxServerManager.exe --selftest &lt;report.txt&gt;: with DCX_DATA_DIR pointing at a scratch folder, opens every screen
/// off-screen, checks the encrypted store round-trips, records any exception, then exits. No network calls succeed
/// (no Vultr key; the test host is 127.0.0.1:1) — it checks that screens build and fail gracefully.
/// </summary>
public static partial class SelfTest
{
    /// <summary>True while the self-test drives the app: code that would activate a window (steal focus) skips that.</summary>
    public static bool Running { get => TestMode.Running; private set => TestMode.Running = value; }

    /// <summary>The start-up timing line both suites report (measured when the self-test begins).</summary>
    internal static string StartupLine { get; private set; } = "";

    public static async Task RunAsync(MainWindow main, string reportPath)
    {
        Running = true;
        // Start-up cost (0.2.0 audit, for phase 7's budgets): process start → main window shown and this test running.
        using (var me = System.Diagnostics.Process.GetCurrentProcess())
            StartupLine = $"start-up: {(DateTime.Now - me.StartTime).TotalMilliseconds:N0} ms from process start to the main window, {me.PrivateMemorySize64 >> 20:N0} MB private, {me.HandleCount:N0} handles, {me.Threads.Count} threads (AI {(AiFeatures.Current.On ? "on" : "off")})";
        // 0.5.1 #8: the audit log is locked by default and both suites open every screen. Each run starts with that lock off
        // (as an administrator could set it); Release051Tests checks the default, the lock and its prompt on their own.
        AppSettings.Current.LockAuditLog = false;
        // 0.5.9 (ADR-076): opening an open host switches to its tab by default. The older suites open the same host or shell
        // several times and expect a tab each, so they run with switching off; Release051Tests checks the default and the
        // switching itself on its own.
        AppSettings.Current.ReuseOpenTabs = false;
        // 0.2.0 phase 5: the second run starts with AI features off (DCX_AI_FEATURES=0) and has its own suite — it must not
        // touch any AI code, so it branches before the lines below.
        if (!AiFeatures.Current.On) { await AiOffTests.RunAsync(main, reportPath); return; }
        await RunFullAsync(main, reportPath);
    }

    /// <summary>The full suite (AI on). A method of its own that is never inlined: it names the AI's types, and compiling it
    /// would load the AI assembly, which the AI-off run must never do (0.2.0 phase 6, checked at the end of that run).</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task RunFullAsync(MainWindow main, string reportPath)
    {
        // A popup menu is a window on the screen: record any that opens during the run (what it held, and who opened it).
        var openedMenus = new List<string>();
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.ContextMenu), System.Windows.Controls.ContextMenu.OpenedEvent, new RoutedEventHandler((s, _) =>
        {
            var m = (System.Windows.Controls.ContextMenu)s;
            if (!m.IsOpen) return; // a test raising Opened on a detached menu (typography re-binding) shows nothing
            var heads = string.Join(", ", m.Items.OfType<System.Windows.Controls.MenuItem>().Select(i => i.Header?.ToString()).Take(4));
            var who = string.Join(" < ", new System.Diagnostics.StackTrace(1).GetFrames().Select(f => f.GetMethod()).Where(x => x?.DeclaringType?.Namespace?.StartsWith("DcxManager", StringComparison.Ordinal) == true).Select(x => $"{x!.DeclaringType!.Name}.{x.Name}").Take(5));
            lock (openedMenus) openedMenus.Add($"[{heads}] from {(who.Length > 0 ? who : "WPF (no app frame)")} · target {m.PlacementTarget?.GetType().Name}");
        }));
        // WPF (since .NET Core 3) also opens a control's tooltip when it gets keyboard focus after keyboard input: in the
        // self-test that is a tooltip of the off-screen main window (nothing on the user's screen), so it is cancelled before
        // it opens and counted apart. A tooltip of any window that IS on screen still fails the run below.
        var keyboardTips = new List<string>();
        EventManager.RegisterClassHandler(typeof(FrameworkElement), System.Windows.Controls.ToolTipService.ToolTipOpeningEvent, new System.Windows.Controls.ToolTipEventHandler((s, e) =>
        {
            if (s is not FrameworkElement fe || Window.GetWindow(fe) is not { Left: <= -5000 } || fe.IsMouseOver) return;
            e.Handled = true;
            var who = string.Join(" < ", new System.Diagnostics.StackTrace(1).GetFrames().Select(f => f.GetMethod()).Where(x => x?.DeclaringType?.Namespace?.StartsWith("DcxManager", StringComparison.Ordinal) == true).Select(x => $"{x!.DeclaringType!.Name}.{x.Name}").Take(4));
            lock (keyboardTips) keyboardTips.Add($"“{(fe.ToolTip?.ToString() ?? "").Split('\n')[0]}” ({fe.GetType().Name}{(who.Length > 0 ? " from " + who : "")})");
        }));
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.ToolTip), System.Windows.Controls.ToolTip.OpenedEvent, new RoutedEventHandler((s, _) =>
        {
            var t = (System.Windows.Controls.ToolTip)s;
            if (!t.IsOpen) return;
            lock (openedMenus) openedMenus.Add($"tooltip “{(t.Content?.ToString() ?? "").Split('\n')[0]}” · target {t.PlacementTarget?.GetType().Name} {(t.PlacementTarget as FrameworkElement)?.Name}");
        }));
        // Never drive a real Foundry Local on the test PC (Settings ticks its set-up steps on open): "not installed" unless a test fakes it.
        Ai.AiClient.FoundryCli = (_, _, _) => Task.FromResult((-1, ""));
        // …and never install anything or end a process (ADR-026's automatic set-up): tests fake these when they need them.
        Ai.FoundryManager.Winget = (_, _, _) => Task.FromResult((-1, ""));
        Ai.FoundryManager.EndDaemon = _ => false;
        var log = new StringBuilder();
        int failures = 0;
        void Ok(string s) => log.AppendLine("ok    " + s);
        void Fail(string s, Exception? ex) { failures++; log.AppendLine("FAIL  " + s + " :: " + ex); }
        // The freeze test's deliberately failing screen (FreezeTests) is the one expected exception.
        Application.Current.DispatcherUnhandledException += (_, e) => { if (!e.Exception.Message.Contains("s37 freeze probe")) Fail("unhandled UI exception", e.Exception); e.Handled = true; };

        async Task Settle(int ms = 600)
        {
            await Task.Delay(ms);
            await Application.Current.Dispatcher.InvokeAsync(() => main.UpdateLayout(), DispatcherPriority.ApplicationIdle);
        }

        var run = new SelfTestRun(main, reportPath, Ok, Fail, Settle);
        Ok(StartupLine);

        await StoreAndParsersAsync(run);

        await ScreensAndSessionsAsync(run);

        await NotesIncidentsDocumentsAsync(run);

        await InfrastructureAsync(run);

        await UpdatesAndServicesAsync(run);

        await ApiAddonsRdpAsync(run);

        await DataAndFilesAsync(run);

        await UiAsync(run);

        // 6. Vultr forms build without an account (lookups fail gracefully)
        foreach (var def in VultrCatalog.All.Where(d => d.Create != null))
        {
            try
            {
                var w = new CloudFormWindow("t", "", def.Create!, new VultrClient(new VultrAccount { ApiKey = "invalid" }), null, null, false)
                { Left = -5000, Top = -5000, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
                w.Show(); await Settle(150); w.Close();
                Ok("form " + def.Title);
            }
            catch (Exception ex) { Fail("form " + def.Title, ex); }
        }

        // Spec §53 / ADR-016: offline / air-gapped mode (SelfTestOffline.cs).
        try { await OfflineTests.RunAsync(main, Path.GetDirectoryName(reportPath)!, Ok, Fail, Snap); }
        catch (Exception ex) { Fail("offline mode", ex); }

        // Spec §55 / ADR-017: environment labels and production safeguards (SelfTestEnv.cs).
        try { await EnvTests.RunAsync(main, Path.GetDirectoryName(reportPath)!, Ok, Fail, Snap); }
        catch (Exception ex) { Fail("environments", ex); }

        // Spec §48 / ADR-018: engineering dashboard (SelfTestDashboard.cs).
        try { await DashboardTests.RunAsync(main, Path.GetDirectoryName(reportPath)!, Ok, Fail, Snap); }
        catch (Exception ex) { Fail("dashboard", ex); }

        // Spec §65 / ADR-019: terminal features (SelfTestTerminal.cs).
        try { await TerminalTests.RunAsync(main, Path.GetDirectoryName(reportPath)!, Ok, Fail, Snap); }
        catch (Exception ex) { Fail("terminal features", ex); }

        // ADR-020: the cloud provider layer and Contabo against an in-process fake API (SelfTestContabo.cs).
        try { await ContaboTests.RunAsync(main, Path.GetDirectoryName(reportPath)!, Ok, Fail, Snap); }
        catch (Exception ex) { Fail("Contabo", ex); }

        // ADR-021: single-instance guard and terminal renderer recovery (SelfTestStability.cs).
        try { await StabilityTests.RunAsync(main, Path.GetDirectoryName(reportPath)!, Ok, Fail); }
        catch (Exception ex) { Fail("stability", ex); }

        // ADR-022: crash recovery — journal, editor drafts, transfers, workspace layout, the recovery screen, a real crash.
        try { await RecoveryTests.RunAsync(main, Path.GetDirectoryName(reportPath)!, Ok, Fail); }
        catch (Exception ex) { Fail("crash recovery", ex); }

        // ADR-023: the NPU engine with the current (`foundry server`) and older (`foundry service`) Foundry Local CLI.
        try { await FoundryTests.RunAsync(main, Path.GetDirectoryName(reportPath)!, Ok, Fail); }
        catch (Exception ex) { Fail("foundry local", ex); }

        // ADR-026: Foundry Local hands-off set-up, self-repair, models from this PC's catalogue (all against fakes).
        try { await FoundrySetupTests.RunAsync(main, Path.GetDirectoryName(reportPath)!, Ok, Fail); }
        catch (Exception ex) { Fail("foundry set-up", ex); }

        // ADR-025: a closed screen with a hanging background check is released; no timer left running for closed screens.
        try { await RobustnessTests.RunAsync(main, Ok, Fail); }
        catch (Exception ex) { Fail("robustness", ex); }

        // ADR-028 (0.1.11): Hetzner Cloud, the Cloud providers workspace, vCenter, Hyper-V, AI tools and terminal bridge,
        // collapsed explorers, the Settings categories (all against fakes, plus real local PowerShell sessions).
        try { await ReleaseTests.RunAsync(main, Path.GetDirectoryName(reportPath)!, Ok, Fail, Snap); }
        catch (Exception ex) { Fail("0.1.11 release tests", ex); }

        // 0.2.0: Microsoft Azure and Google Cloud against fakes of their sign-in endpoints and APIs.
        try { await AzureGcpTests.RunAsync(main, Path.GetDirectoryName(reportPath)!, Ok, Fail, Snap); }
        catch (Exception ex) { Fail("Azure / Google Cloud tests", ex); }

        // 0.2.1: the DarkCipher house theme (presets, tokens, Fluent icons, sidebar styles, tabbed Settings, import / export).
        try { await HouseThemeTests.RunAsync(main, Path.GetDirectoryName(reportPath)!, Ok, Fail, Snap); }
        catch (Exception ex) { Fail("house theme tests", ex); }

        // 0.3.0 preview.1: admin-protected settings (UAC faked) and Windows Hello (faked): never a prompt.
        try { await ProtectedSettingsTests.RunAsync(main, Path.GetDirectoryName(reportPath)!, Ok, Fail, Snap); }
        catch (Exception ex) { Fail("protected settings / Windows Hello tests", ex); }

        // 0.3.0 preview.2: Group Policy for every setting, the templates, and the uninstaller's clean-up.
        try { await PolicyTests.RunAsync(main, Path.GetDirectoryName(reportPath)!, Ok, Fail, Snap); }
        catch (Exception ex) { Fail("policy / uninstall tests", ex); }

        // 0.3.0 preview.4: item-by-item encrypted export and import (tick tree).
        try { await ExportTests.RunAsync(main, Path.GetDirectoryName(reportPath)!, Ok, Fail, Snap); }
        catch (Exception ex) { Fail("export / import tests", ex); }

        // 0.3.0 preview.4: the SSH key tool (generation, passphrase export through askpass, the window off screen).
        try { await SshKeyTests.RunAsync(main, Path.GetDirectoryName(reportPath)!, Ok, Fail); }
        catch (Exception ex) { Fail("SSH key tool tests", ex); }

        // 0.3.0 preview.4: quick connect protocols, pinned / recent dropdown (never opened), Connect.
        try { await QuickConnectTests.RunAsync(main, Ok, Fail); }
        catch (Exception ex) { Fail("quick connect tests", ex); }

        // 0.3.0 preview.5: syslog over TLS and e-mail notifications (fakes on 127.0.0.1).
        try { await NotifyTests.RunAsync(main, Ok, Fail); }
        catch (Exception ex) { Fail("syslog / notification tests", ex); }

        // 0.4.0: log sinks (Elastic / Loki / Azure Monitor / Splunk / Graylog / webhook payloads, a TLS fake on 127.0.0.1 with a
        // pinned certificate), the Grafana dashboard list (fake), the Observability screen and its policies.
        try { await ObserveTests.RunAsync(main, Ok, Fail); }
        catch (Exception ex) { Fail("observability tests", ex); }

        // 0.4.0: the penetration-test suite (docs/SECURITY-REPORT.md): adversarial inputs at every boundary a hostile server,
        // file, policy, add-on or web answer can reach.
        try { await PentestTests.RunAsync(Ok, Fail); }
        catch (Exception ex) { Fail("pentest suite", ex); }

        // 0.5.0: DarkCipher0x's 0.4.0 feedback (docs/PLAN-0.5.0.md): live screens, dropdown names, policy locks, About.
        try { await Release050Tests.RunAsync(main, Ok, Fail); }
        catch (Exception ex) { Fail("0.5.0 tests", ex); }
        try { await Release050DataTests.RunAsync(Ok, Fail); }
        catch (Exception ex) { Fail("0.5.0 data tests", ex); }
        try { await Release050AutomationTests.RunAsync(main, Ok, Fail); }
        catch (Exception ex) { Fail("0.5.0 automation tests", ex); }
        // 0.5.1: locks, restrictions, add-on policy, host import, Commander, LOGS, dropdowns, the VNC tab (docs/PLAN-0.5.1.md).
        try { await Release051Tests.RunAsync(main, Ok, Fail); }
        catch (Exception ex) { Fail("0.5.1 tests", ex); }

        // 0.3.0 preview.6: LAN discovery tools (ping, port tests, connection, monitoring) and scrolling (loopback only).
        try { await LanTests.RunAsync(main, Ok, Fail); }
        catch (Exception ex) { Fail("LAN / scrolling tests", ex); }

        // 0.3.0 preview.7: Postfix checks, the open-relay probe (loopback fakes only).
        try { await MailTests.RunAsync(main, Ok, Fail); }
        catch (Exception ex) { Fail("mail check tests", ex); }

        // 0.3.0 preview.7: shared library short approval codes and per-member / per-item permissions.
        try { await LibraryAccessTests.RunAsync(Path.GetDirectoryName(reportPath)!, Ok, Fail); }
        catch (Exception ex) { Fail("library access tests", ex); }

        // 0.3.0 preview.8: DarkCipher portals over dcx-ctl/1 (loopback fake gateway + portal).
        try { await DcxCtlTests.RunAsync(Ok, Fail); }
        catch (Exception ex) { Fail("dcx-ctl tests", ex); }

        // 0.5.1 #19: the VNC client (loopback fake RFB servers: VNC password, Apple Remote Desktop, VeNCrypt, every encoding).
        try { await VncTests.RunAsync(Ok, Fail); }
        catch (Exception ex) { Fail("VNC tests", ex); }

        // 0.6.26 (ADR-102): FreeFileSync-style folder sync (scratch folders on this PC).
        try { await FolderSyncTests.RunAsync(Path.GetDirectoryName(reportPath)!, Ok, Fail); }
        catch (Exception ex) { Fail("folder sync tests", ex); }

        // 0.3.0: Synology DSM provider (fake DSM Web API).
        try { await SynologyTests.RunAsync(Ok, Fail); }
        catch (Exception ex) { Fail("Synology tests", ex); }

        // 0.3.0 §17: Red Hat Satellite / Foreman + Katello provider (fake REST API).
        // ADR-094 (0.6.16): UniFi Network (Integration API) and Site Manager against fakes.
        try { await UniFiTests.RunAsync(Ok, Fail); }
        catch (Exception ex) { Fail("unifi", ex); }
        // 0.6.17: the AI deploys a Vultr server (fake Vultr).
        try { await VultrDeployTests.RunAsync(Ok, Fail); }
        catch (Exception ex) { Fail("Vultr deploy", ex); }
        try { await SatelliteTests.RunAsync(Ok, Fail); }
        catch (Exception ex) { Fail("Satellite tests", ex); }

        // 0.3.0 §13: editor tools (Vim engine and profiles, text editor, hex editor).
        try { await EditorTests.RunAsync(Ok, Fail); }
        catch (Exception ex) { Fail("editor tests", ex); }

        // 0.3.0 §15: File commander — Midnight Commander tools (pure logic + a real local pane off screen).
        try { await CommanderMcTests.RunAsync(Ok, Fail); }
        catch (Exception ex) { Fail("File commander MC tests", ex); }

        // 0.3.0 §18: network & mail toolbox logic (fake DNS; 127.0.0.1 servers only).
        try { await ToolboxTests.RunAsync(Ok, Fail); }
        catch (Exception ex) { Fail("toolbox tests", ex); }

        // 0.3.0 §16: Ansible (inventory, commands, the job's stdin layout, the screen with a fake runner).
        try { await AnsibleTests.RunAsync(Ok, Fail); }
        catch (Exception ex) { Fail("Ansible tests", ex); }

        // 0.3.0 §19 / §23: whole-app encryption audit, profiles, directory sign-in (fakes), the setup window.
        try { await ProfileTests.RunAsync(Ok, Fail); }
        catch (Exception ex) { Fail("profile tests", ex); }

        // 0.3.0 §20: the dashboard's pinned checks (127.0.0.1 only).
        try { await ChecksTests.RunAsync(Ok, Fail); }
        catch (Exception ex) { Fail("checks tests", ex); }

        // 0.3.0 §24: VPN tunnels (configs, routes, parsing, the manager with a fake engine; no real tunnel).
        try { await VpnTests.RunAsync(Ok, Fail); }
        catch (Exception ex) { Fail("VPN tests", ex); }

        // ADR-031 (0.1.14): provider controls that cover each management API (docs/PROVIDER-API-COVERAGE.md), against fakes.
        try { await InfraCoverageTests.RunAsync(Ok, Fail); }
        catch (Exception ex) { Fail("provider API coverage", ex); }

        // s37: DarkCipher0x's freeze (terminals, then Cloud providers): a looping drawing error is contained; every cloud screen with
        // a live terminal open; no white flash in new terminals.
        try { await FreezeTests.RunAsync(main, Ok, Fail); }
        catch (Exception ex) { Fail("freeze tests", ex); }

        // 0.2.0 phase 1: the Ui / Log seams are registered and forward; nothing below the screens calls the UI directly.
        try { await ArchitectureTests.RunAsync(main, Ok, Fail); }
        catch (Exception ex) { Fail("architecture seams", ex); }

        // 0.2.0 phase 5: the AI features switch, its lock and the capability filter (the AI-off run has its own suite).
        try { await AiSwitchTests.RunAsync(main, Ok, Fail); }
        catch (Exception ex) { Fail("AI features switch", ex); }

        // ADR-030 (0.1.12): theme add-ons (the sample theme): applied, refused when malformed, removable back to the default.
        try { await ThemeAddonTests.RunAsync(main, Path.GetDirectoryName(reportPath)!, Ok, Fail, Snap); }
        catch (Exception ex) { Fail("theme add-ons", ex); }

        // ADR-032 (0.1.15): the portal browser add-on, against a local HTTP / self-signed HTTPS test server only.
        try { await PortalTests.RunAsync(main, Path.GetDirectoryName(reportPath)!, Ok, Fail, Snap); }
        catch (Exception ex) { Fail("portal browser", ex); }

        // ADR-033: the shared library add-on (three simulated members against a scratch folder as the share).
        try { await LibraryTests.RunAsync(main, Path.GetDirectoryName(reportPath)!, Ok, Fail); }
        catch (Exception ex) { Fail("shared library", ex); }

        // ADR-090 (0.6.12): post-quantum primitives, the library's hybrid keys, the SSH key exchange order.
        try { PqcTests.Run(Path.GetDirectoryName(reportPath)!, Ok, Fail); }
        catch (Exception ex) { Fail("post-quantum", ex); }

        // ADR-091 (0.6.12): Docs, the Markdown editor (store, renderer, screen).
        try { await DocsTests.RunAsync(main, Path.GetDirectoryName(reportPath)!, Ok, Fail, Snap); }
        catch (Exception ex) { Fail("docs", ex); }

        // ADR-093 (0.6.14): Bitwarden (the CLI runner on a real process, vault and Secrets Manager against scripted CLIs,
        // sessions, screens).
        try { await BitwardenTests.RunAsync(main, Path.GetDirectoryName(reportPath)!, Ok, Fail, Snap); }
        catch (Exception ex) { Fail("bitwarden", ex); }

        // ADR-034: AI speed (streaming on every engine, timings before/after), agentic tool loop, per-chat model, the memory
        // guard, the AI Engineer commanding its target session, more tools, and terminal typing latency — against a fake engine.
        try { await AgentTests.RunAsync(main, Path.GetDirectoryName(reportPath)!, Ok, Fail, Snap); }
        catch (Exception ex) { Fail("AI agent tests", ex); }

        // ROADMAP #15: fuzzing, endurance loops and leak checks (SelfTestStress.cs, docs/TESTING.md).
        await StressTests.RunAsync(main, Path.GetDirectoryName(reportPath)!, Ok, Fail);

        // No test may reach a dialog (it would be a window on the user's screen): prompts, confirmations, choices, info and
        // error boxes, and the unhandled-exception box are all refused during the self-test and fail the report here.
        lock (openedMenus)
            if (openedMenus.Count > 0) Fail($"self-test opened {openedMenus.Count} popup menu(s) or tooltip(s) on the screen: " + string.Join(" | ", openedMenus.Distinct().Take(8)), null);
            else Ok("no popup menu or tooltip was opened on the screen" + (keyboardTips.Count > 0 ? $" ({keyboardTips.Count} keyboard-focus tooltip(s) of the off-screen window cancelled before opening: {string.Join(" | ", keyboardTips.Distinct().Take(6))})" : ""));
        lock (Dialogs.SelfTestPrompts)
            if (Dialogs.SelfTestPrompts.Count > 0) Fail("self-test reached " + Dialogs.SelfTestPrompts.Count + " dialog(s): " + string.Join(" | ", Dialogs.SelfTestPrompts.Distinct().Take(12)), null);
            else Ok("no dialog was reached (prompts, confirmations, choices, info/error boxes and the unhandled-error box are refused during the self-test and reported)");

        log.Insert(0, $"DarkCipher ServerManagement {App.DisplayVersion} self-test {DateTime.Now:yyyy-MM-dd HH:mm:ss} — {failures} failure(s)\n\n");
        File.WriteAllText(reportPath, log.ToString());
        Application.Current.Shutdown(failures == 0 ? 0 : 1);
    }
}

/// <summary>A serial device for the self-test: Feed() delivers bytes to the reader, Written captures what was sent,
/// Unplug() makes the pending read fail like a pulled USB adapter.</summary>
internal sealed class FakeSerialStream : Stream
{
    private readonly System.Collections.Concurrent.BlockingCollection<byte[]> _in = new();
    private volatile bool _unplugged;
    public MemoryStream Written { get; } = new();
    public void Feed(byte[] b) => _in.Add(b);
    public void Unplug() { _unplugged = true; _in.CompleteAdding(); }
    public override int Read(byte[] buffer, int offset, int count)
    {
        try { var b = _in.Take(); Array.Copy(b, 0, buffer, offset, Math.Min(count, b.Length)); return Math.Min(count, b.Length); }
        catch (InvalidOperationException) { if (_unplugged) throw new IOException("The device is not connected."); return 0; }
    }
    public override void Write(byte[] buffer, int offset, int count) { lock (Written) Written.Write(buffer, offset, count); }
    public override void Flush() { }
    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
    public override void SetLength(long v) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (!_in.IsAddingCompleted) _in.CompleteAdding(); base.Dispose(disposing); }
}

/// <summary>An HTTP handler for the self-test: answers every request with JSON from a function (no network).</summary>
internal sealed class FakeHttp(Func<System.Net.Http.HttpRequestMessage, string> respond) : System.Net.Http.HttpMessageHandler
{
    protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent(respond(request), Encoding.UTF8, "application/json") });
}

/// <summary>A fake HTTP server for the updater self-test: status + JSON text, or raw bytes for downloads.</summary>
/// <summary>A fake web of exact addresses → (status, body, Location) for redirect tests; anything else is 404.</summary>
internal sealed class FakeRoutes(Dictionary<string, (int Code, byte[] Body, string? Location)> routes, List<(string Host, bool Authorized)>? seen = null) : System.Net.Http.HttpMessageHandler
{
    protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
    {
        seen?.Add((request.RequestUri!.Host, request.Headers.Authorization != null));
        var (code, body, location) = routes.TryGetValue(request.RequestUri!.AbsoluteUri, out var r) ? r : (404, Array.Empty<byte>(), null);
        var m = new System.Net.Http.HttpResponseMessage((System.Net.HttpStatusCode)code) { Content = new System.Net.Http.ByteArrayContent(body) };
        if (location != null) m.Headers.Location = new Uri(location);
        return Task.FromResult(m);
    }
}

internal sealed class FakeHttpStatus(Func<System.Net.Http.HttpRequestMessage, (int Code, string Body)> respond, Func<System.Net.Http.HttpRequestMessage, byte[]>? bytes = null) : System.Net.Http.HttpMessageHandler
{
    protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (bytes != null) return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.ByteArrayContent(bytes(request)) });
        var (code, body) = respond(request);
        return Task.FromResult(new System.Net.Http.HttpResponseMessage((System.Net.HttpStatusCode)code) { Content = new System.Net.Http.StringContent(body, Encoding.UTF8, "application/json") });
    }
}
