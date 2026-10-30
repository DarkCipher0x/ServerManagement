using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using DcxManager.Services;
using ThemeMode = DcxManager.Services.ThemeMode;   // .NET 9+ WPF has System.Windows.ThemeMode too
using DcxManager.Ssh;
using DcxManager.Views;
using DcxManager.Vultr;

namespace DcxManager;

public partial class MainWindow : Window
{
    public static MainWindow Instance { get; private set; } = null!;

    private readonly Dictionary<string, TabItem> _singletons = new();
    private readonly Dictionary<string, RadioButton> _navByKey = new();
    /// <summary>Every sidebar screen key (the self-test's timer audit opens and closes each).</summary>
    internal IReadOnlyList<string> NavKeys => _navByKey.Keys.ToList();

    /// <summary>The document area: tabs in split/tiled panes and pop-out windows.</summary>
    public PaneHost Panes { get; } = new();

    /// <summary>Whatever module fills the right dock column (0.2.0 phase 3); null when none does.</summary>
    public IDockController? RightDock { get; }

    public MainWindow()
    {
        Instance = this;
        InitializeComponent();
        _cursorBlink.KeyFrames.Add(new System.Windows.Media.Animation.DiscreteDoubleKeyFrame(1, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.Zero)));
        _cursorBlink.KeyFrames.Add(new System.Windows.Media.Animation.DiscreteDoubleKeyFrame(0, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.55))));
        DocArea.Child = Panes;
        InitHostPane();   // 0.6.31: the hosts panel beside every tab (Shell/MainWindow.HostPane.cs)
        RightDock = AppModules.CreateRightDock(this, DockCol, DockSplitter, DockHost);
        Panes.ActiveTabChanged += OnActiveTab;
        Activated += (_, _) => Panes.ActivateMain();   // 0.6.4: new tabs open in the window you're in
        Panes.TabMenu = TabMenu;
        Panes.CloseRequested = t => { if ((t.Tag as string) != "fixed") CloseTab(t); };
        VersionText.Text = "v" + App.DisplayVersion;
        _defaultLogo = HeaderLogo.Source;
        ApplyBranding();
        ThemeManager.Changed += ApplyBranding;   // the main window lives as long as the app: nothing to unhook
        Browser.PortalBrowser.Changed += () => Dispatcher.BeginInvoke(() => BuildNav());   // the portal browser switch (Settings) or add-on shows / hides Portals (ADR-032)
        Library.SharedLibraries.FeatureChanged += () => BuildNav();   // a library add-on shows / hides Shared library (ADR-033)
        Locks.Changed += () => Dispatcher.BeginInvoke(OnLocksChanged);   // 0.5.1 (ADR-067): padlocks redraw; screens that locked again close
        foreach (ComboBoxItem i in ThemeBox.Items) if ((string)i.Tag == AppSettings.Current.Theme) ThemeBox.SelectedItem = i;
        SideCol.Width = new GridLength(Math.Clamp(AppSettings.Current.SidebarWidth, 150, 480));
        if (AppSettings.Current.SidebarCollapsed) SideCol.Width = new GridLength(0);
        CloudProvider.ActiveAccountChanged += OnCloudAccountChanged;
        RefreshLockText();
        ThemeManager.TrackScale(this, Root);
        if (MigrateNavSettings(AppSettings.Current)) SaveSettings();
        if (AppSettings.Current.RememberNavExpansion) _navExpanded.UnionWith(AppSettings.Current.ExpandedNavSections);
        BuildNav();
        ApplyMotion();
        NavLayout.MotionChanged += ApplyMotion;
        // Group Policy (0.3.0 §10): a change to a managed setting is put back on save; say why it didn't stay.
        AppSettings.Reverted += names => Dispatcher.BeginInvoke(() => Status(
            $"{string.Join(", ", names.Select(n => PolicySettings.Get(n)?.Entry.Title ?? n))}: set by your organisation's policy, so your change wasn't kept."));   // the main window lives as long as the app: nothing to unhook
        // Settings → Navigation → Open on start (a sidebar screen; Dashboard by default or when the choice no longer exists).
        var landing = AppSettings.Current.LandingPage;
        Navigate(landing.Length > 0 && AllNavKeys.Contains(landing) ? landing : "dashboard");
        ContentRendered += (_, _) =>
        {
            TunnelsView.AutoStartAll();
            RightDock?.RestoreDetached();
            TabGroups.Changed += SaveWorkspace;
            // Modules' window-ready hooks (0.2.0 phase 3): the AI module reports Foundry Local's set-up steps in the status line.
            AppModules.OnWindowReady(this);
            RestoreExtraWindows();   // 0.6.4: the extra windows come back on their monitors (before crash recovery fills them)
            OfferRestore();
            // Local API (ADR-011): only if switched on in Settings; broadcasts are never restored.
            Api.Broadcasts.Changed += UpdateBroadcastUi;
            BroadcastBadge.Click += (_, _) => Api.Broadcasts.StopAll("stopped by you (status bar)");
            // Offline mode (ADR-016): always visible while on, with the number of blocked attempts this session.
            Offline.Changed += UpdateOfflineUi; Offline.BlockedChanged += UpdateOfflineUi;
            // Environment labels follow host edits on every open tab, docked or popped out (ADR-017).
            Envs.Changed += () => Dispatcher.BeginInvoke(() => { foreach (var t in Panes.AllTabs) EnvTab.Apply(t); });
            OfflineBadge.Click += (_, _) => { Navigate("settings"); SettingsView.Current?.RevealCard("Offline mode"); };
            UpdateOfflineUi();
            Api.LocalServer.Sync();
            if (StoreRecovery.Notices.Count > 0) Status(string.Join("  ·  ", StoreRecovery.Notices));
            // Local-first: warn (once per location, dismissible for good) when the store sits in OneDrive & co. Never in self-test.
            if (Environment.GetEnvironmentVariable("DCX_DATA_DIR") is not { Length: > 0 })
            {
                Dispatcher.BeginInvoke(StorageGuard.CheckAtStartup, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                // An update started last time is reported only once the running version proves it (or that it didn't happen).
                if (Updater.ConfirmPendingUpdate() is { } upd) Dispatcher.BeginInvoke(() => Dialogs.Info("Update", upd), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                _ = AutoUpdateCheckAsync();
                _ = BitwardenSessions.AutoUnlockAsync("the app started");   // 0.6.20: with a remembered master password only
                StartSnapshotTimer();
                StartSyncScheduler();
            }
        };
    }

    /// <summary>Scheduled and real-time folder sync jobs (0.6.26, ADR-102): while the store is open (the lock screen doesn't
    /// stop them, as it doesn't stop scheduled backups: an idle auto-lock would otherwise stop every overnight job), never in
    /// the self-test.</summary>
    private void StartSyncScheduler()
    {
        Files.SyncScheduler.RunFinished += (job, r) => Dispatcher.BeginInvoke(() =>
            Status($"{(r.Ok ? "✓" : "⚠")} Sync job “{job.Name}”: {r.Text.Split('\n')[0]}{(r.Ok ? "" : " (File Commander → Sync jobs)")}"));
        Files.SyncScheduler.Start(() => !TestMode.Running);
    }

    /// <summary>The red BROADCASTING badge and the per-terminal strips follow the active broadcasts.</summary>
    private void UpdateBroadcastUi()
    {
        var all = Api.Broadcasts.All;
        BroadcastBadge.Visibility = all.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BroadcastBadge.Content = all.Count == 1 ? $"📡 BROADCASTING “{all[0].Title}” — ■ Stop" : $"📡 BROADCASTING {all.Count} — ■ Stop all";
        foreach (var tv in Panes.AllTabs.Select(t => t.Content).OfType<TerminalView>()) tv.ShowBroadcasting(Api.Broadcasts.For(tv));
    }

    /// <summary>The OFFLINE MODE badge follows the switch and counts what it blocked (events may come from any thread).</summary>
    private void UpdateOfflineUi()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(UpdateOfflineUi); return; }
        OfflineBadge.Visibility = Offline.On ? Visibility.Visible : Visibility.Collapsed;
        int n = Offline.BlockedTotal;
        OfflineBadge.Content = "⛔ OFFLINE MODE" + (n > 0 ? $" · {n} blocked" : "");
    }

    private System.Windows.Threading.DispatcherTimer? _snapTimer;

    /// <summary>Automatic snapshots (ADR-009): shortly after start, then checked hourly; local disk only, off-thread.</summary>
    private void StartSnapshotTimer()
    {
        async void Tick()
        {
            try
            {
                var r = await Task.Run(Snapshots.AutoIfDue);
                if (r is { Ok: false }) Status("⚠ Automatic snapshot failed: " + r.Message + " (Settings → Snapshots)");
                // 0.5.0 (ADR-060): scheduled encrypted backups ride the same hourly check.
                var b = await Task.Run(() => ScheduledBackup.RunIfDue());
                if (b is { Ok: false }) Status("⚠ Scheduled backup failed: " + b.Message + " (Settings → Data & backups)");
            }
            catch (Exception ex) { App.Log("Automatic snapshot", ex); }
        }
        _snapTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromHours(1) };
        _snapTimer.Tick += (_, _) => Tick();
        _snapTimer.Start();
        var first = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        first.Tick += (_, _) => { first.Stop(); Tick(); };
        first.Start();
    }

    /// <summary>The window title (DarkCipher0x, 0.4.0): the product, by the company.</summary>
    public const string DefaultTitle = "Server Management by DarkCipher Systems";   // 0.6.9 (DarkCipher0x): the same in the title bar and Task Manager

    private void OnSourceInitialized(object? sender, EventArgs e) => ThemeManager.ApplyTitleBar(this);

    private System.Windows.Media.ImageSource? _defaultLogo;

    /// <summary>Header logo, product name, tagline and window title: a theme add-on's when one is in use (ADR-030), else the
    /// app's own. A wide logo keeps its proportions at the header height; a logo plate is a rounded pill behind it.</summary>
    internal void ApplyBranding()
    {
        var t = ThemeManager.ActivePack;
        if (t?.LogoImage is { } img)
        {
            HeaderLogo.Source = img;
            HeaderLogo.Height = 36;
            HeaderLogo.Width = Math.Min(200, 36.0 * img.PixelWidth / Math.Max(1, img.PixelHeight));
        }
        else { HeaderLogo.Source = _defaultLogo; HeaderLogo.Width = 36; HeaderLogo.Height = 36; }
        if (t?.LogoBackground is { } plate)
        {
            var b = new System.Windows.Media.SolidColorBrush(plate); b.Freeze();
            LogoPlate.Background = b;
            LogoPlate.Padding = new Thickness(14, 5, 14, 5);
        }
        else { LogoPlate.Background = null; LogoPlate.Padding = new Thickness(0); }
        // The header shows just "ServerManagement" (DarkCipher0x, 0.3.0): the darkcipher0x prompt line under it carries the brand.
        HeaderTitle.Text = t?.ProductName ?? "ServerManagement";
        // The DarkCipher name is set in the mono "terminal" face; a theme's name uses the UI font.
        HeaderTitle.SetResourceReference(TextBlock.FontFamilyProperty, t?.ProductName != null ? "UiFont" : "MonoFont");
        HeaderPrompt.Visibility = t?.Tagline == null ? Visibility.Visible : Visibility.Collapsed;
        HeaderTagline.Text = t?.Tagline ?? "";
        HeaderTagline.Visibility = string.IsNullOrEmpty(t?.Tagline) ? Visibility.Collapsed : Visibility.Visible;
        Title = t?.ProductName ?? DefaultTitle;
    }

    // ---- keyboard -----------------------------------------------------------------------------------------------

    private void OnPreviewKey(object sender, KeyEventArgs e) => HandleShortcut(e);

    /// <summary>
    /// App shortcuts. Inside a terminal, plain Ctrl+letter and F-keys belong to the remote program (Ctrl+W deletes a word,
    /// Ctrl+B is the tmux prefix, F1 is htop's help…), so there only Ctrl+Shift / Ctrl+Alt / Ctrl+Tab combos are taken.
    /// </summary>
    public void HandleShortcut(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        bool inTerminal = Keyboard.FocusedElement is DependencyObject d && Menus.Ancestor<TerminalView>(d) != null
                          || e.OriginalSource is DependencyObject o && Menus.Ancestor<TerminalView>(o) != null;
        bool ctrl = mods == ModifierKeys.Control, ctrlShift = mods == (ModifierKeys.Control | ModifierKeys.Shift), ctrlAlt = mods == (ModifierKeys.Control | ModifierKeys.Alt);
        // Modules' shortcuts first (0.2.0 phase 3): Ctrl+Shift+A toggles the AI Engineer sidebar. They are Ctrl+Shift /
        // Ctrl+Alt combos, so they work in terminals too.
        if (AppModules.RunShortcut(key, mods)) { e.Handled = true; return; }
        bool handled = true;
        switch (key)
        {
            case Key.W when ctrlShift || ctrl && !inTerminal: if (Panes.ActiveTab is { } t && (t.Tag as string) != "fixed") CloseTab(t); break;
            case Key.K when ctrlShift || ctrl && !inTerminal: FocusQuickConnect(); break;
            case Key.B when ctrlShift || ctrl && !inTerminal: ToggleSidebar(); break;
            case Key.F1 when mods == ModifierKeys.None && !inTerminal || ctrl: ShowHelp(); break;
            case Key.F6 when mods == ModifierKeys.None && !inTerminal || ctrl: Panes.FocusNextPane(); break;
            case Key.Tab when ctrl: Panes.NextTab(+1); break;
            case Key.Tab when ctrlShift: Panes.NextTab(-1); break;
            case Key.Right when ctrlAlt: if (Panes.ActiveTab is { } r) Panes.Split(r, Orientation.Horizontal); break;
            case Key.Down when ctrlAlt: if (Panes.ActiveTab is { } dn) Panes.Split(dn, Orientation.Vertical); break;
            case Key.L when ctrlShift: LockNow(); break;
            case Key.P when ctrlShift: CommandPalette.Show(this); break;   // 0.6.29: the command palette (ADR-104)
            case Key.S when ctrlShift: _ = ScreenshotActiveAsync(); break;
            case Key.T when ctrlAlt: Panes.Tile(); break;
            case Key.U when ctrlAlt: Panes.MergeAll(); break;
            case Key.O when ctrlAlt: if (Panes.ActiveTab is { } p) { if (Panes.IsFloating(p)) Panes.DockBack(p); else Panes.PopOut(p); } break;
            case Key.N when ctrlShift && !inTerminal: Panes.NewWindow(); break;   // 0.6.4: a window with a host pane for another monitor
            default: handled = false; break;
        }
        if (handled) e.Handled = true;
    }

    // ---- header -------------------------------------------------------------------------------------------------

    // 0.3.0 (DarkCipher0x, s45): the header's cloud account picker is gone. Accounts are switched where they are used — right-click
    // a provider in the Cloud providers tree → Switch account — or in Settings → Cloud providers.

    public void RefreshLockText()
    {
        LockText.Text = (SecureStore.PasswordRequiredOnStart ? "🔒 AES-256-GCM · master password" : "🔒 AES-256-GCM · Windows account (DPAPI)")
                        + (SecureStore.HasTotp ? " · 2FA" : "") + (AppSettings.Current.SandboxMode ? " · sandbox" : "")
                        + (SecureStore.IsRelocated ? " · " + SecureStore.DataDir : "")
                        + (Profiles.Current is { } pr && Profiles.LoadWithDefault().Profiles.Count > 1 ? $" · profile {pr.Name}" : "")
                        + (Profiles.ReadOnly ? " · read-only (viewer)" : "");
        LockText.Cursor = Cursors.Hand;
        LockText.ToolTip = "Click for security options · Ctrl+Shift+L locks the app";
        if (LockText.ContextMenu == null)
        {
            IEnumerable<object?> Items() => new object?[]
            {
                Menus.Item("🔒 Lock now", LockNow, "Ctrl+Shift+L", AppLock.CanLock),
                Menus.Item("Security settings…", () => Navigate("settings")),
                Menus.Item(Offline.On ? "⛔ Turn offline mode off" : "⛔ Turn offline mode on (block everything outside your network)", async () => await ProtectedSettings.ChangeAsync(Offline.On ? "turn offline mode off" : "turn offline mode on", "net.offline", !Offline.On)),   // admin-protected (0.3.0 §1)
                Menus.Item("Encrypted backup / export…", () => BackupDialogs.ExportFull(this)),
                Menus.Sub("Switch profile", Profiles.LoadWithDefault().Profiles.Where(p => p.Id != (Profiles.Current?.Id ?? Profiles.DefaultId))
                    .Select(p => (object?)Menus.Item($"{(p.Kind == "directory" ? "🏢" : "👤")} {p.Name}", () => { if (Dialogs.Confirm("Switch profile", $"Close this profile and open “{p.Name}”? Open sessions end.", "Switch")) StartupFlow.SwitchTo(p); })).ToArray()),
                Menus.Item("Open data folder", () => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(SecureStore.DataDir) { UseShellExecute = true })),
            };
            Menus.Attach(LockText, Items);
            LockText.MouseLeftButtonUp += (_, _) => Menus.ShowAt(LockText, Items());
        }
    }

    /// <summary>A provider's active account changed (Cloud providers tree, Settings): its screens and the all-provider ones reload.</summary>
    private void OnCloudAccountChanged(CloudProvider p)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => OnCloudAccountChanged(p)); return; }
        foreach (var t in Panes.AllTabs)
        {
            if (t.Content is CloudWorkspaceView w) w.AccountChanged(p);   // only that provider's screens inside it reload
            else if (t.Content is ICloudView v && (v.Provider == null || v.Provider == p)) v.AccountChanged();
        }
        if (p.ActiveAccount is { } a) Status($"{p.Title} account: {a.Name}");
    }

    private bool _syncingTheme;

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingTheme || ThemeBox.SelectedItem is not ComboBoxItem { Tag: string tag } || !IsLoaded && AppSettings.Current.Theme == tag) return;
        AppSettings.Current.Theme = tag;
        try { AppSettings.Save(); } catch { }
        ThemeManager.Apply(Enum.Parse<ThemeMode>(tag));
    }

    /// <summary>The header's theme selector shows the setting (after Settings → Appearance changed it, a reset or an import).</summary>
    public void SyncThemeBox()
    {
        _syncingTheme = true;
        try { foreach (ComboBoxItem i in ThemeBox.Items) if ((string)i.Tag == AppSettings.Current.Theme) ThemeBox.SelectedItem = i; }
        finally { _syncingTheme = false; }
    }

    /// <summary>Opt-in automatic check (Settings → Updates, off by default), daily or weekly: tells you in the status bar;
    /// with the background download allowed it also downloads and verifies the update. It never installs.</summary>
    private async Task AutoUpdateCheckAsync()
    {
        var s = AppSettings.Current;
        if (!Updater.Enabled || !s.AutoCheckUpdates || DateTime.Now - s.LastUpdateCheck < Updater.CheckInterval) return;
        await Task.Delay(TimeSpan.FromSeconds(20));
        try
        {
            var (info, _) = await Updater.CheckAsync(new CancellationTokenSource(TimeSpan.FromSeconds(40)).Token);
            s.LastUpdateCheck = DateTime.Now; AppSettings.Save();
            if (info == null) return;
            if (!s.AutoDownloadUpdates) { Status($"Update available: {info.Version} (you have {App.Version}) — Settings → Updates to review and install."); return; }
            var (path, v) = await Updater.DownloadAndVerifyAsync(info, null, new CancellationTokenSource(TimeSpan.FromMinutes(30)).Token);
            AuditLog.Write("app", "app", "update downloaded in the background", $"{info.Version} · {v.Verdict}");
            if (v.Ok && path != null) { Updater.Ready = (info, path); Status($"Update {info.Version} downloaded and verified — Settings → Updates to install it (you have {App.Version})."); }
            else Status($"Update {info.Version} is available but its download didn't verify ({v.Message}) — nothing was kept.");
        }
        catch (Exception ex) { App.Log("Auto update check", ex); }
    }

    /// <summary>Set by the lock screen's "Exit app" — the user already chose to quit.</summary>
    public bool ExitWithoutPrompt { get; set; }

    public void LockNow()
    {
        if (!AppLock.Lock())
            Dialogs.Info("Lock", "Set a master password or turn on two-factor sign-in first (Settings → Security) — otherwise there would be nothing to unlock with.");
    }

    /// <summary>Start-up (0.6.4, ADR-082): reopens the extra windows open at the last exit, empty and ready (their host
    /// pane connects into them), where they were — a window whose monitor is gone comes back on the main one. Not in the
    /// self-test, and not when Group Policy or Settings turn it off (RestoreWorkspaceWindows).</summary>
    private void RestoreExtraWindows()
    {
        var s = AppSettings.Current;
        if (TestMode.Running || Environment.GetEnvironmentVariable("DCX_DATA_DIR") is { Length: > 0 } || !s.RestoreWorkspaceWindows) return;
        foreach (var p in s.WorkspaceWindows.Take(8)) Panes.NewWindow(p);
        if (s.WorkspaceWindows.Count > 0) Activate();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        var all = Panes.AllTabs.ToList();
        int sessions = all.Count(t => t.Content is TerminalView { IsConnected: true });
        // 0.6.29: the open sessions can be saved and reopen at the next start (Settings → Terminal → Saved sessions).
        var saveable = TestMode.Running ? new List<Workspace.Entry>() : WorkspaceEntries();
        if (!ExitDecision(saveable, sessions)) { e.Cancel = true; return; }
        // 0.6.4: where the extra windows are, so they come back on their monitors next time.
        if (!TestMode.Running)
        {
            AppSettings.Current.WorkspaceWindows = Panes.Floats.Select(f => f.Placement()).ToList();
            try { AppSettings.Save(); } catch (Exception ex) { App.Log("Save window placements", ex); }
        }
        foreach (var t in all)
            if (t.Content is IDisposable d) { try { d.Dispose(); } catch { } }
        Panes.ShutDown();
        RightDock?.ShutDown();
        AppModules.OnShutdown();
        TunnelManager.StopAll();
        VpnManager.StopAll();
        SshPool.DisposeAll();
        // Unsaved editor drafts and unfinished transfers survive a normal exit too: shutdown can't wait for them (ADR-022).
        Recovery.FlushAtExit();
        Workspace.EndSessionCleanly();
    }
}

/// <summary>Views that can refuse to close (unsaved edits, running jobs).</summary>
public interface ICloseable { bool CanClose(); }
/// <summary>Views that refresh when their tab is selected.</summary>
public interface IActivatable { void Activated(); }
/// <summary>A tab tied to a saved/ad-hoc host (used to persist/restore the workspace). Null for tabs with no remote host
/// (local shells, WSL). Cloud screens that follow an account implement <see cref="ICloudView"/>.</summary>
public interface IHostView { HostEntry? Host { get; } }
