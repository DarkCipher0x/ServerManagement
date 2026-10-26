using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using DcxManager.Services;
using ThemeMode = DcxManager.Services.ThemeMode;   // .NET 9+ WPF has System.Windows.ThemeMode too
using DcxManager.Views;
using DcxManager.Sessions;

namespace DcxManager;

public partial class App : Application
{
    public const string AppUserModelId = "darkcipher0x.DarkCipherServerManagement";
    /// <summary>The on-screen name (window titles, dialog captions): "ServerManagement", like the logo.</summary>
    public const string ProductName = AppInfo.DisplayName;

    /// <summary>Error logs (sealed per entry): see <see cref="Services.Log"/>, which owns them since 0.2.0 phase 1.</summary>
    public static string LogDir => Services.Log.Dir;
    // Name, version and cache folder live in Services.AppInfo since 0.2.0 phase 6 (code below the screens reads them there).
    public static string CacheDir => AppInfo.CacheDir;
    public static string Version => AppInfo.Version;
    public static string DisplayVersion => AppInfo.DisplayVersion;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SetCurrentProcessExplicitAppUserModelID(string appId);

    /// <summary>
    /// What the projects below the app ask the layers above them for (0.2.0 phase 6, docs/history/PLAN-0.2.0.md §2). Each lower
    /// project offers a hook instead of referencing upward; the composition root — this method — connects them, before
    /// anything below the screens runs. A hook left unset has a safe default (nothing shared, no theme add-on, no tabs).
    /// </summary>
    private static DispatcherTimer? _saveLater;
    private static Action? _saveLaterWork;

    /// <summary>Deferred store saves (<see cref="AppData.SaveSoon"/>): 2 s after the last request, once, on the UI thread.</summary>
    private static void SaveLater(Action save)
    {
        var d = Current?.Dispatcher;
        if (d == null) { save(); return; }
        d.BeginInvoke(() =>
        {
            _saveLaterWork = save;
            _saveLater ??= new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => { _saveLater!.Stop(); var w = _saveLaterWork; _saveLaterWork = null; w?.Invoke(); }, d);
            _saveLater.Stop(); _saveLater.Start();
        });
    }

    /// <summary>A deferred save still waiting (written now: at exit, before a backup or a profile switch).</summary>
    internal static void FlushSaveLater() { if (_saveLaterWork is { } w) { _saveLater?.Stop(); _saveLaterWork = null; w(); } }

    private static void WireLayers()
    {
        // Core: shared library ids resolve through the library; the cloud link of a host; the recovery journal's file.
        AppData.SharedHost = Library.SharedLibraries.FindHost;
        AppData.SharedCredential = Library.SharedLibraries.FindCredential;
        AppData.SaveLater = SaveLater;   // 0.6.29: last-connected bookkeeping saves once, a moment later, on the UI thread
        CredentialSources.Install();   // 0.6.17 (ADR-096): vault credentials kept in Bitwarden or Password Safe
        SafeMetadata.CloudLink = h => Cloud.CloudProviders.ForHost(h) is { } c ? c.Provider.Title : null;
        StorageGuard.OpenDataLocation = () => SettingsView.Current?.PickNewDataLocation();
        StorageGuard.OpenDataCard = () => SettingsView.Current?.RevealDataCard();
        StoreSchema.Register(Recovery.FileName, "unsaved edits and unfinished transfers kept for recovery (recovery.dcx)",
            plain => System.Text.Json.JsonSerializer.Deserialize<Recovery.Journal>(plain, SecureStore.Json) ?? throw new InvalidDataException("empty"));
        // Connectivity: shared credentials are audited by the library; the bridge reaches terminal tabs; reports use the
        // live accent; unfinished transfers are journaled for crash recovery (ADR-022).
        Ssh.SshConnector.SharedCredentialUsed = Library.SharedLibraries.Used;
        // 0.4.0: logged session lines also go to the syslog collector when that is switched on (masked like every message).
        Sessions.SessionLog.Forward = (session, line) =>
        {
            var s = AppSettings.Current;
            if (s.SyslogEnabled && s.SyslogSendSessionLogs) Notify.SyslogForwarder.Enqueue(Notify.SyslogMessage.Session(DateTimeOffset.Now, session, line));
            if (s.ObserveEnabled && s.ObserveSendSessionLogs) Notify.LogShipper.Enqueue(Notify.LogEvent.SessionLine(DateTimeOffset.Now, session, line));
        };
        // 0.4.0 Observability: the protected sink list is checked by the sinks' own rules; Azure Monitor sinks sign in through
        // the Azure subscriptions' app registrations (the cloud layer's certificate sign-in, the Monitor scope).
        ProtectedSettings.SinksCheck = Notify.LogSink.Check;
        Notify.LogShipper.AzureToken = Azure.AzureClient.MonitorTokenAsync;
        Sessions.Terminals.Open = () => DcxManager.MainWindow.Instance?.Panes.AllTabs.Select(t => t.Content).OfType<TerminalView>() ?? Enumerable.Empty<Sessions.ITerminal>();
        Sessions.Terminals.Front = () => DcxManager.MainWindow.Instance?.Panes.ActiveTab?.Content as TerminalView;
        Reports.ReportEngine.AccentHex = () => ThemeManager.HexString("Accent");
        Files.TransferQueue.JobsChanged += Recovery.Changed;
        // UI kit: the error box asks the modules for an extra action; a tab's environment comes from the app's screens;
        // the theme add-on in use comes from the add-ons.
        Dialogs.ErrorAction = AppModules.ErrorActionFor;
        DetachedWindow.AppShortcuts = e => DcxManager.MainWindow.Instance?.HandleShortcut(e);
        PrivacyRows.DashboardNote = PrivacyReport.DashboardNote;
        EnvUi.ViewEnvironment = content => content switch
        {
            TerminalView tv when tv.Target.Kind is "local" or "wsl" => Envs.Local,
            BrowserView bv => bv.EnvironmentName,
            IHostView { Host: { } h } => Envs.For(h),
            _ => "",
        };
        // Add-ons: the data add-on kinds owned by the portal browser (ADR-032) and the shared library (ADR-033), checked at
        // inspection and told about changes; the library's hosts and credentials for add-ons, and its audit.
        Addons.AddonPackages.RegisterKind(new("browser", Browser.PortalBrowser.IsBrowser, m => m.Browser, Browser.BrowserPack.MaxBytes,
            json => Browser.BrowserPack.Parse(json), Browser.PortalBrowser.Notify));
        Addons.AddonPackages.RegisterKind(new("library", Library.SharedLibraries.IsLibrary, m => m.Library, Library.LibraryProfile.MaxBytes,
            json => Library.LibraryProfile.Parse(json), Library.SharedLibraries.Notify));
        Addons.AddonHost.SharedHosts = () => Library.SharedLibraries.Hosts;
        Addons.AddonHost.SharedCredentials = () => Library.SharedLibraries.Credentials;
        Addons.AddonHost.SharedCredentialGiven = (c, addonId) =>
        {
            if (Library.SharedLibraries.Resolve(c.Id) is { } shared) _ = Task.Run(() => shared.Client.Audit("shared credential given to an add-on", $"{c.Name} → {addonId}"));
        };
        // Add-ons: "ui.open" / "ui.openHost" reach the main window through the app.
        Addons.AddonApi.OpenScreen = screen =>
        {
            if (DcxManager.MainWindow.Instance is not { } main) return false;
            main.Navigate(screen == "cloud" ? CloudWorkspaceView.NavKeyName : screen);
            return true;
        };
        Addons.AddonApi.OpenHostView = (h, view) =>
        {
            if (DcxManager.MainWindow.Instance is not { } main) return false;
            switch (view)
            {
                case "server": main.OpenServer(h); break;
                case "terminal": main.OpenTerminal(h); break;
                case "files": main.OpenFiles(h); break;
                case "commander": main.OpenCommander(h); break;
                case "notes": main.OpenNotes(h.Id); break;
            }
            return true;
        };
        // A chosen theme add-on paints the app; otherwise the branding set in Settings (or pushed by policy), 0.4.0.
        ThemeManager.ThemeSource = () => Addons.ThemeAddons.Active() is { } pack ? (Addons.ThemeAddons.Chosen, (Addons.ThemePack?)pack) : Branding.Active() is { } own ? (null, own) : (null, null);
    }

    /// <summary>The only place the app names the AI module. A method of its own that is never inlined: compiling the
    /// caller doesn't touch the AI's types, so with AI features off the AI assembly (Dcx.Ai) is never loaded.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void LoadAiModule() => AppModules.Add(new AiModule());

    /// <summary>What <see cref="AppInfo.MigrateLegacyFolders"/> moved at this start (written to the audit log once the store is open).</summary>
    private List<string>? _migrated;

    protected override void OnStartup(StartupEventArgs e)
    {
        // The seams the lower layers use for dialogs, the window and the UI thread (0.2.0 phase 1): registered before
        // anything below the screens can run.
        WpfUi.Register();
        WireLayers();
        // The capability registry (0.2.0 phase 2) learns the app's own capabilities here, in this order: the operator's
        // built-ins first, then each feature's (the AI's tool list and add-ons' app.capabilities keep their 0.1.18 order).
        Capabilities.BuiltIns = () => { OperatorCapabilities.Register(); FeatureCapabilities.Register(); };
        // A module's own capabilities (the AI's terminal-target ones) exist only while that module is loaded.
        Capabilities.ModuleLoaded = AppModules.IsLoaded;

        // Started elevated through UAC to approve a protected-settings change (0.3.0 §1, ADR-039): one window, no store,
        // no single-instance check (the user's own copy is running and waiting for this one), nothing else.
        if (e.Args.Length >= 1 && e.Args[0] == AdminGate.Arg)
        {
            // Never against a test store, and never without a request.
            if (e.Args.Length < 2 || Environment.GetEnvironmentVariable("DCX_DATA_DIR") is { Length: > 0 }) { Shutdown(2); return; }
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            ThemeManager.Apply(Services.ThemeMode.System);
            Typography.Install();
            base.OnStartup(e);
            int code;
            try { code = AdminGate.RunElevatedSide(e.Args[1]); }
            catch (Exception ex) { Log("Protected settings change", ex); code = 2; }
            Shutdown(code);
            return;
        }

        // Started elevated through UAC to approve one action (0.5.1, ADR-067: unlocking a lock, installing an add-on): one
        // window, no store, nothing else; never against a test store.
        if (e.Args.Length >= 1 && e.Args[0] == LockGate.VerifyArg)
        {
            if (e.Args.Length < 2 || Environment.GetEnvironmentVariable("DCX_DATA_DIR") is { Length: > 0 }) { Shutdown(2); return; }
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            ThemeManager.Apply(Services.ThemeMode.System);
            Typography.Install();
            base.OnStartup(e);
            int code;
            try { code = LockGate.RunElevatedSide(e.Args[1]); }
            catch (Exception ex) { Log("Admin approval", ex); code = 2; }
            Shutdown(code);
            return;
        }

        // ssh-keygen's askpass helper (Ssh/KeyExport.cs, the SSH key tool's passphrase export): it only runs with the one-shot
        // pipe in its environment; one line in, one line out, no window, no store.
        if (e.Args.Length == 1 && Environment.GetEnvironmentVariable(Ssh.KeyExport.PipeVariable) is { Length: > 0 })
        {
            Shutdown(Ssh.KeyExport.AskpassMain(e.Args[0]));
            return;
        }

        // Writes the Group Policy templates and docs/POLICIES.md (0.3.0 §10; build.ps1 runs it): no window, no store.
        if (e.Args.Length >= 2 && e.Args[0] == "--export-policy-templates")
        {
            int code = 0;
            try { PolicyTemplates.Write(e.Args[1]); } catch (Exception ex) { Log("Policy templates", ex); code = 1; }
            Shutdown(code);
            return;
        }

        // Run by the uninstaller (0.3.0 §11, Services\Uninstall.cs): "--cleanup" as the user, "--cleanup-machine" as SYSTEM.
        // No window; never against a test store.
        if (e.Args.Length > 0 && e.Args[0] is "--cleanup" or "--cleanup-machine")
        {
            try { Cleanup(e.Args[0] == "--cleanup-machine", e.Args.Skip(1)); } catch (Exception ex) { Log("Cleanup", ex); }
            Shutdown(0);
            return;
        }

        // One instance per store (ADR-021): a second launch brings the running window forward and exits before it opens a
        // window, the store or a browser. "--selftest-second" is the self-test's probe: it must exit without any UI either way
        // (9 = the guard let it through, which the test reports as a failure).
        bool probe = e.Args.Length > 0 && e.Args[0] == "--selftest-second";
        bool selfTestRun = e.Args.Length >= 2 && e.Args[0] == "--selftest";
        // 0.3.0 §23: which profile (store) opens — chosen before the single-instance guard keys itself on the store path,
        // so each profile has its own instance. Never in the self-test (its scratch store is the only profile).
        if (!selfTestRun && !probe && Environment.GetEnvironmentVariable("DCX_DATA_DIR") is not { Length: > 0 })
        {
            ThemeManager.Apply(Services.ThemeMode.System);
            Typography.Install();
            // 0.4.0: the per-user folders moved from "DarkCipher" to "DarkCipher Systems" — a rename before anything opens them.
            try { _migrated = AppInfo.MigrateLegacyFolders(); }
            catch (IOException ex)
            {
                Log("Folder migration", ex);
                Dialogs.Error(ProductName, ex.Message + "\n\nClose any other copy of the app (or a program holding its files) and start it again; nothing was changed.", explain: false);
                Shutdown(1);
                return;
            }
            if (!StartupFlow.ChooseProfile(e.Args)) { Shutdown(0); return; }
        }
        if (!SingleInstance.TryAcquire())
        {
            if (selfTestRun) { EarlyExitReport(e, "another instance is already running on this test store (DCX_DATA_DIR) — a previous run still exiting?"); Shutdown(3); return; }
            SingleInstance.SignalExisting();
            Shutdown(0);
            return;
        }
        if (probe) { Shutdown(9); return; }
        // From now on a later launch brings this instance forward — the unlock window too, if it is still asking. Never during
        // the self-test: there it is only counted, so an off-screen test window can't take focus from the user's desktop.
        SingleInstance.Listen(() => Dispatcher.BeginInvoke(() =>
        {
            if (SelfTest.Running) return;
            var w = Windows.OfType<Window>().FirstOrDefault(x => x is UnlockWindow && x.IsVisible) ?? MainWindow;
            if (w == null) return;
            if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
            w.Show(); w.Activate();
            w.Topmost = true; w.Topmost = false;   // Windows only lets a foreground process take focus: flash to the front
        }));

        // WinForms (only the embedded RDP host) must never show its own "unhandled exception" box: errors go to ours.
        System.Windows.Forms.Application.SetUnhandledExceptionMode(System.Windows.Forms.UnhandledExceptionMode.ThrowException);
        DispatcherUnhandledException += OnUiException;
        TaskScheduler.UnobservedTaskException += (_, ev) => { Log("Background task", ev.Exception); ev.SetObserved(); };
        AppDomain.CurrentDomain.UnhandledException += (_, ev) =>
        {
            Log("Fatal", ev.ExceptionObject as Exception);
            if (ev.IsTerminating) Recovery.Emergency("fatal error");   // ADR-022: save what can be saved before the process ends
        };
        SessionEnding += (_, _) => Recovery.Emergency("Windows session ending");
        // A Store/MSIX install already has its identity from the manifest; overriding it would split the taskbar entry.
        if (!Packaging.IsPackaged) try { SetCurrentProcessExplicitAppUserModelID(AppUserModelId); } catch { }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        ThemeManager.Apply(Services.ThemeMode.System);
        Typography.Install();
        base.OnStartup(e);

        bool open;
        try { open = SecureStore.Initialize(); }
        catch (Exception ex)
        {
            // Never a box during the self-test (it would be on the user's screen): the report says it instead.
            if (selfTestRun) EarlyExitReport(e, "the test store could not be opened: " + ex.Message);
            else Dialogs.Error(ProductName, $"The encrypted data store could not be opened.\n\n{ex.Message}", explain: false);
            Shutdown(1);
            return;
        }
        if (!open && selfTestRun) { EarlyExitReport(e, "the test store asks for a password (it must be a fresh scratch folder)"); Shutdown(1); return; }
        if (!open && new UnlockWindow(needPassword: true, needCode: SecureStore.HasTotp).ShowDialog() != true) { Shutdown(0); return; }
        if (open && SecureStore.HasTotp && new UnlockWindow(needPassword: false, needCode: true).ShowDialog() != true) { SecureStore.Forget(); Shutdown(0); return; }

        // ADR-009: finish an interrupted restore/upgrade, offer snapshot copies of damaged files, migrate the format.
        // The self-test never shows a window: any question there means "close" (and is logged).
        bool selfTest = e.Args.Length >= 1 && e.Args[0] is "--selftest" or "--selftest-crash";
        Func<string, string, string[], int> ask = selfTest
            ? (t, m, _) => { Log("Start-up question in self-test: " + t, new InvalidOperationException(m)); return -1; }
            : (t, m, o) => Dialogs.Choose(t, m, o, cancelIndex: Array.FindIndex(o, x => x.StartsWith("Close")));
        if (!StoreRecovery.RunAtStart(ask)) { if (selfTestRun) EarlyExitReport(e, "start-up store recovery stopped (see the error log)"); SecureStore.Forget(); Shutdown(1); return; }

        ShareService.Cleanup();
        // Group Policy (0.3.0 §10) is read before the settings, so every load and save enforces it. The self-test never
        // reads the real policy keys.
        if (selfTest) PolicySettings.SourceForTest = _ => null;
        PolicySettings.Install();
        AppSettings.Load();
        // Admin-protected settings (0.3.0 §1) override the user's copies before anything reads them (AI features below).
        // The self-test reads a scratch store, never the real HKLM record.
        if (selfTest) ProtectedSettings.UseStoreForTest(new ProtectedSettings.FileStore(System.IO.Path.Combine(SecureStore.DataDir, "protected-test")));
        AdminGate.Install();
        if (ProtectedSettings.ApplyTo(AppSettings.Current) is { Count: > 0 } enforced)
        {
            try { AppSettings.Save(); } catch (Exception ex) { Log("Protected settings", ex); }
            AuditLog.Write("app", "protected settings", "protected values put back over the settings file", string.Join(", ", enforced));
        }
        // Modules contribute to the screens (0.2.0 phase 3). The AI module is loaded only when AI features are on (phase 5:
        // machine policy, then DCX_AI_FEATURES unless locked, then the setting); off means no AI anywhere — nothing to hide.
        // Registered before the main window exists, so the first sidebar build already sees the contributions.
        // 0.3.0 §23: a profile bound to a directory opens only after that sign-in (or a recent cached proof).
        if (!selfTest && !StartupFlow.DirectoryGate()) { SecureStore.Forget(); Shutdown(0); return; }
        if (AiFeatures.DecideAtStartup().On) LoadAiModule();
        try { Addons.AddonPackages.SealExisting(); } catch (Exception ex) { Log("Seal add-ons", ex); }   // ADR-035: data-only add-ons encrypted at rest
        AppData.Load();
        if (_migrated is { Count: > 0 } mv) AuditLog.Write("app", "data folders", "moved to the DarkCipher Systems folder (0.4.0)", string.Join("; ", mv));
        Workspace.BeginSession();
        Recovery.LoadPrevious();   // ADR-022: unsaved drafts / unfinished transfers left by the previous run
        DcxManager.Library.SharedLibraries.Load();   // ADR-033: cached shared libraries (the share is synced after start-up)
        // 0.3.0 §3/§4: the audit and error logs to a syslog collector over TLS (when set up) and e-mail notifications.
        Notify.SyslogForwarder.Start();
        Notify.LogShipper.Start();   // 0.4.0: the same events to Elastic / Loki / Azure Monitor / Splunk / Graylog / webhooks (when set up)
        Notify.Notifier.Start();
        LanMonitors.Start();      // 0.3.0 §7: the scheduled LAN re-scans you set up (none until you do)
        VpnManager.Start();       // 0.3.0 §24: "connect through" hook + auto-start tunnels (none until you add one)
        // The self-test's crash probe (ADR-022): journals work and waits to be killed, or reports what it finds. No UI ever,
        // and only against a scratch store.
        if (e.Args.Length >= 3 && e.Args[0] == "--selftest-crash" && Environment.GetEnvironmentVariable("DCX_DATA_DIR") is { Length: > 0 })
        {
            if (RecoveryTests.Probe(e.Args[1], e.Args[2])) Shutdown(0);
            return;
        }
        Typography.Apply();
        AppLock.ReconcileSandbox();
        NavLayout.ApplyDensity();
        ThemeManager.Apply(Enum.TryParse<ThemeMode>(AppSettings.Current.Theme, out var mode) ? mode : Services.ThemeMode.System);
        ThemeManager.FollowSystemChanges();
        ScrollAssist.Install();   // 0.3.0 §6: middle-click autoscroll, wheel hand-off, scrolling menus
        Menus.InstallTextMenus();   // 0.5.2: every text box right-clicks into the app's own menu, never WPF's built-in one
        DispatcherWatchdog.Start(Dispatcher);   // 0.5.3: WPF's work queue can't stall for good if its wake-up message is lost
        CaptureGuard.Install();   // 0.5.4: the mouse is never left captured once every button is up (outside menus and popups)
        DcxManager.Ssh.ExternalSsh.CleanUp();

        var main = new MainWindow();
        MainWindow = main;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        if (e.Args.Length >= 2 && e.Args[0] == "--selftest" && Environment.GetEnvironmentVariable("DCX_DATA_DIR") is { Length: > 0 })
        {
            // Off-screen, against a scratch data folder only.
            main.WindowStartupLocation = WindowStartupLocation.Manual;
            main.Left = -10000; main.Top = -10000; main.ShowActivated = false; main.ShowInTaskbar = false;
            main.Show();
            _ = SelfTest.RunAsync(main, e.Args[1]);
            return;
        }
        main.Show();
        AppLock.Start();
        LockGate.Install();   // 0.5.1 (ADR-067): locks close on time, and when Windows locks or the PC sleeps
        StartupFlow.MaybeRunSetup(main);   // 0.3.0 §19: a new store gets the guided, all-optional setup
        // Modules' background start-up tasks (0.2.0 phase 3): the AI module applies its default engine (ADR-023), sets up
        // Foundry Local hands-off (ADR-026) and starts the memory guard (ADR-034). Never in the self-test (above).
        AppModules.StartBackgroundTasks();
    }

    /// <summary>A self-test that ends before it starts still leaves a report saying why (ADR-025): a missing report is
    /// never the only clue (the s30 "exit -1, no report" one-off).</summary>
    private static void EarlyExitReport(StartupEventArgs e, string why)
    {
        try { File.WriteAllText(e.Args[1], $"DarkCipher ServerManagement {Version} self-test {DateTime.Now:yyyy-MM-dd HH:mm:ss} — 1 failure(s)\nFAIL  start-up :: {why}\n", new System.Text.UTF8Encoding(false)); }
        catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { FlushSaveLater(); } catch { }   // a last-connected time still waiting for its deferred save
        try { AppLock.Stop(); } catch { }
        try { LockGate.Uninstall(); } catch { }
        try { PasswordSafeSessions.CheckInAll(); } catch { }   // 0.5.0: no Password Safe checkout outlives the app
        try { Bitwarden.BitwardenVault.LockAtExit(); } catch { }   // 0.6.14: nor an unlocked Bitwarden vault
        try { Notify.SyslogForwarder.Stop(); } catch { }   // what is still queued goes to the encrypted spool
        try { Notify.LogShipper.Stop(); } catch { }
        try { Api.Broadcasts.StopAll("app closed"); Api.LocalServer.Stop(); } catch { }
        ShareService.Cleanup();
        try { var rdp = Path.Combine(CacheDir, "rdp"); if (Directory.Exists(rdp)) Directory.Delete(rdp, true); } catch { }
        SecureStore.Forget();
        base.OnExit(e);
        if (RestartRequested && Packaging.RelaunchInfo(Environment.ProcessPath) is { } start)
        {
            SingleInstance.Release();   // the new process starts while this one is still exiting: let it take the store

            try { System.Diagnostics.Process.Start(start); } catch (Exception ex) { Log("Restart", ex); }
        }
    }

    /// <summary>Set after a snapshot restore: the app starts again once this instance has fully exited.</summary>
    public static bool RestartRequested { get; set; }

    /// <summary>
    /// Uninstall clean-up (0.3.0 §11, <see cref="Uninstall"/>). Deliberately ignores DCX_DATA_DIR: only the standard per-user
    /// folders (and the store files of a moved store, never the folder it was moved into) and the app's own registry keys.
    /// Nothing is logged: a log would be the one thing left behind.
    /// </summary>
    private static void Cleanup(bool machine, IEnumerable<string> args)
    {
        var o = Uninstall.Options.Parse(args);
        if (machine) Uninstall.CleanMachine(o, new Uninstall.RealMachine());
        else Uninstall.CleanUser(Uninstall.CurrentProfile(), o, name => WindowsHello.DeleteAsync(name).GetAwaiter().GetResult());
    }

    private static int _shown;

    private void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        // s37: an exception raised while WPF lays out or draws an element comes back on every pass. Log it once, count the
        // repeats, close the screen that loops, and never open a box from inside this handler (UiErrorGuard).
        var verdict = UiErrorGuard.Classify(e.Exception);
        if (UiErrorGuard.IsDispatcherReentrancy(e.Exception))
        {
            // Never answered with a modal box (that froze the app behind a blank window, s37). And WPF consumed the message:
            // if it was WPF's own wake-up, its work queue would never run again (0.5.2: the app took no clicks after a
            // right-click, until killed) — so post the wake-up again once this layout pass is over (DispatcherWatchdog).
            DispatcherWatchdog.KickSoon();
            if (verdict == UiErrorGuard.Verdict.LogAndShow) Log("UI (window message during drawing; wake-up re-posted)", e.Exception);
            return;
        }
        if (verdict is UiErrorGuard.Verdict.LogAndShow or UiErrorGuard.Verdict.LogOnly) Log("UI", e.Exception);
        if (verdict == UiErrorGuard.Verdict.Count) return;
        Recovery.FlushNow();   // ADR-022: if the app goes down after all, the journal is current
        string? closed = null;
        if (verdict == UiErrorGuard.Verdict.Storm)
        {
            Log($"UI error loop ({UiErrorGuard.CountOf(e.Exception)}× in a few seconds)", e.Exception);
            closed = CloseLoopingScreen();
        }
        // The self-test never shows this box (it once covered the user's screen, s26); its own handler records a failure.
        if (SelfTest.Running) { Dialogs.RefusedInSelfTest("unhandled UI exception: " + e.Exception.Message); return; }
        if (verdict == UiErrorGuard.Verdict.LogOnly) return;
        var ex = e.Exception;
        UiErrorGuard.WhenIdle(Dispatcher, () =>
        {
            if (Interlocked.Increment(ref _shown) > 1) { Interlocked.Decrement(ref _shown); return; }
            try
            {
                // Dialogs.Error offers "✦ Explain this error with AI" (click only; stack paths, user name and secrets stripped).
                Dialogs.Error(ProductName, (closed != null
                        ? $"The “{closed}” screen kept failing while it was being drawn, so it was closed to keep the app responsive. Your sessions are untouched; you can open it again.\n\n"
                        : "Something went wrong, but the app kept running.\n\n")
                    + $"{ex.Message}\n\nDetails were saved (encrypted) to the error log — Settings → Data & storage → View error log.", ex: ex);
            }
            catch { }
            finally { Interlocked.Decrement(ref _shown); }
        });
    }

    /// <summary>Closes the tab in front when a drawing error loops — never a live session (terminal, RDP, files), whose
    /// connection matters more than a broken frame. Returns its title, or null when nothing was closed.</summary>
    internal static string? CloseLoopingScreen()
    {
        try
        {
            if (Current?.MainWindow is not MainWindow mw || mw.Panes.ActiveTab is not { } tab || tab.Content is IHostView or TerminalView) return null;
            var title = tab.Header as string ?? tab.Content?.GetType().Name ?? "screen";
            if (tab.Tag as string == "fixed") return null;   // the dashboard can't be closed
            mw.CloseTab(tab);
            return title;
        }
        catch (Exception ex) { Log("Close looping screen", ex); return null; }
    }

    /// <summary>Appends an error to today's log file (no secrets are ever passed here). Never throws. The screens still call
    /// this; everything below them calls <see cref="Services.Log.Write"/> directly (0.2.0 phase 1).</summary>
    public static string Log(string where, Exception? ex) => Services.Log.Write(where, ex);

    /// <summary>Errors logged since start (the dashboard's local warnings, ADR-018).</summary>
    public static int ErrorsThisSession => Services.Log.ErrorsThisSession;

    /// <summary>The error logs of the last <paramref name="days"/> days, newest last, with sealed entries decrypted.</summary>
    public static string ReadLogs(int days) => Services.Log.ReadLogs(days);
}
