using System.Windows;
using System.Windows.Controls;
using DcxManager.Addons;
using DcxManager.Services;
using Microsoft.Win32;

namespace DcxManager.Views;

/// <summary>Install (with the approval screen), run and menu entries for add-ons (ADR-012).</summary>
public static class AddonUi
{
    /// <summary>Install or update from a .dcxaddon file: inspect → approval screen → install. Returns the add-on or null.</summary>
    public static async Task<InstalledAddon?> InstallFromFile(string? path = null)
    {
        if (!Restrictions.Check("addon.install")) return null;
        if (path == null)
        {
            var dlg = new OpenFileDialog { Title = "Install an add-on", Filter = "DarkCipher add-on|*.dcxaddon|All files|*.*" };
            if (dlg.ShowDialog() != true) return null;
            path = dlg.FileName;
        }
        var info = AddonPackages.Inspect(path);
        if (info.Refusal != null) { Dialogs.Error("Add-on", info.Refusal, explain: false); return null; }
        var grant = Approve(info);
        if (grant == null) return null;
        if (!await AdminApprovedAsync($"{(info.Existing != null ? "Update" : "Install")} the add-on {info.Manifest.Name} {info.Manifest.Version} ({info.Manifest.Id}) · {info.Signature} · permissions: {(grant.Count == 0 ? "none" : string.Join(", ", grant))}")) return null;
        try
        {
            var a = AddonPackages.Install(info, grant);
            // A theme-only add-on (ADR-030) is installed to be worn: it becomes the app's theme straight away (Back to the default theme undoes it).
            bool themeOnly = ThemeAddons.IsTheme(a.Manifest) && a.Manifest.Entry.Length == 0;
            if (themeOnly) ThemeAddons.Use(a);
            MainWindow.Instance?.Status($"◇ {a.Title} {(info.Existing != null ? "updated" : "installed")}" + (themeOnly ? " · theme in use" : $" · {a.Granted.Count} of {a.Manifest.Permissions.Count} permission(s) granted"));
            return a;
        }
        catch (Exception ex) { Dialogs.Error("Add-on", "Install failed: " + ex.Message, explain: false); return null; }
    }

    /// <summary>0.5.1 #7: installing, updating, enabling, granting and removing need admin approval (UAC) while
    /// AddonsNeedApproval is on (the default; Group Policy can require it). False = not approved (audited).</summary>
    public static async Task<bool> AdminApprovedAsync(string purpose)
    {
        if (!AppSettings.Current.AddonsNeedApproval) return true;
        var ok = await LockGate.VerifyAdminAsync(purpose);
        AuditLog.Write("you", "addons", ok ? "admin approved an add-on change" : "add-on change not approved", purpose);
        return ok;
    }

    /// <summary>The approval screen. Returns the permissions the user ticked (empty = none), or null for Cancel.</summary>
    public static List<string>? Approve(PackageInfo info)
    {
        var m = info.Manifest;
        bool update = info.Existing != null;
        var w = Dialogs.MakeWindow(update ? "Update add-on" : "Install add-on", 680);
        var p = new StackPanel { Margin = new Thickness(18) };
        var head = new TextBlock { Text = $"◇ {m.Name} {m.Version}" + (update ? $"  (installed: {info.Existing!.Manifest.Version})" : ""), FontWeight = FontWeights.Bold }.Fs(FontStep.Heading);
        head.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        p.Children.Add(head);
        var meta = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Text = $"Publisher (as stated by the package): {m.Publisher}\n{m.Description}" };
        p.Children.Add(meta);
        var facts = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
            Text = (m.Library.Length > 0 ? $"Shared library: {m.Library} · no program" : m.Browser.Length > 0 ? $"Portal browser: {m.Browser} · no program" : m.Entry.Length == 0 ? $"Theme: {m.Theme} · no program" : $"Program: {m.Entry} · {info.Signature}") + $"\nPackage SHA-256: {info.Sha256}\n{info.FileCount} file(s), {info.Bytes / 1024.0:N0} KB · timeout {m.TimeoutSeconds} s · commands: {string.Join(", ", m.Commands.Select(c => $"{c.Title} [{c.Context}]"))}",
        }.Fs(FontStep.Meta).Mono();
        facts.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        p.Children.Add(facts);
        var warn = new Border { Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 10, 0, 0), BorderThickness = new Thickness(1) };
        warn.SetResourceReference(Border.BorderBrushProperty, "Warn");
        warn.Child = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = m.Library.Length > 0
                ? "⇄ This connects the app to a shared library of hosts and team credentials (ADR-033). The package holds no secret and no program: only the library's id, its folder and the owner's key fingerprint. " +
                  "After installing, open Shared library → Join a library…: your keys are made on this PC, and an admin approves you after you read them your fingerprint. Disable or Remove hides the library; your member keys stay sealed in your store until you Forget them."
                : m.Browser.Length > 0
                ? "🌐 This turns on the portal browser: web UIs (routers, NAS, Proxmox/Unraid, provider portals) in tabs inside the app. The browser is part of the app, not a program in the package: nothing in the package runs, and it gets no access to your data, cookies or vault. It may offer starter bookmarks, which you add yourself on the Portals screen. Disable or Remove hides Portals again; your bookmarks stay in your encrypted store."
                : m.Entry.Length == 0
                ? "◐ This is a theme: colours, a font name and optionally a header logo and product name. Nothing runs and it gets no access to your data. It overrides the app's light and dark themes as soon as it's installed and whenever it's enabled; Add-ons → Back to the default theme, Disable or Remove undoes it."
                : "⚠ An add-on is a program. It runs in a sandbox (Low integrity: it can't change your files, the app or other add-ons; memory and process limits; killed after its timeout), " +
                   "but it can still READ many files and use the network on its own. The permissions below control only what this app hands to it. Install only add-ons from people you trust.",
        };
        p.Children.Add(warn);
        if (m.Entry.Length == 0) warn.SetResourceReference(Border.BorderBrushProperty, "Line");
        p.Children.Add(new TextBlock { Text = update ? "PERMISSIONS — already granted ones stay; NEW ones are off unless you tick them" : "PERMISSIONS — tick what you allow", Style = (Style)Application.Current.Resources["Label"], Margin = new Thickness(0, 12, 0, 4) });
        var boxes = new List<CheckBox>();
        if (m.Permissions.Count == 0) p.Children.Add(new TextBlock { Text = "none — it only gets app.info and status-bar messages" });
        foreach (var perm in m.Permissions)
        {
            var pi = AddonPackages.Permission(perm)!;
            bool isNew = info.NewPermissions.Contains(perm);
            bool had = update && info.Existing!.Granted.Contains(perm);
            var cb = new CheckBox
            {
                Tag = perm, Margin = new Thickness(0, 3, 0, 0), IsChecked = had || (!update && pi.Risk != "HIGH"), IsEnabled = !update || isNew,
                Content = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 600, Text = $"{(isNew && update ? "NEW · " : "")}{perm} — {pi.Text}   [risk: {pi.Risk}]" + (update && !isNew ? (had ? " (granted earlier)" : " (not granted earlier; change it on the Add-ons screen)") : "") },
            };
            if (pi.Risk == "HIGH") ((TextBlock)cb.Content).SetResourceReference(TextBlock.ForegroundProperty, "Bad");
            boxes.Add(cb);
            p.Children.Add(cb);
        }
        var ok = new Button { Content = update ? "Update" : "Install", Style = (Style)Application.Current.Resources["PrimaryButton"], IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        var bs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        bs.Children.Add(ok); bs.Children.Add(cancel);
        p.Children.Add(bs);
        ok.Click += (_, _) => w.DialogResult = true;
        w.Content = new ScrollViewer { Content = p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 760 };
        return w.ShowDialog() == true ? boxes.Where(b => b.IsChecked == true && b.IsEnabled).Select(b => (string)b.Tag).ToList() : null;
    }

    public static async Task RunAsync(InstalledAddon a, AddonCommand c, AddonContext ctx)
    {
        MainWindow.Instance?.Status($"◇ {a.Manifest.Name}: running “{c.Title}” (sandboxed)…");
        var r = await AddonHost.RunAsync(a, c.Id, ctx);
        MainWindow.Instance?.Status($"◇ {a.Manifest.Name}: “{c.Title}” — {r.Status}");
        AddonsView.Current?.Refresh();
        if (r.Show) Dialogs.ShowText($"{a.Manifest.Name} — {c.Title}", r.Text + (r.Log.Count > 0 ? "\n\n── log ──\n" + string.Join("\n", r.Log) : ""));
    }

    private static IEnumerable<(InstalledAddon A, AddonCommand C)> Commands(string context) =>
        AddonPackages.All.Where(a => a.Enabled).SelectMany(a => a.Manifest.Commands.Where(c => c.Context == context).Select(c => (a, c)));

    /// <summary>"◇ Add-ons" submenu for a host, or null when no add-on offers host commands.</summary>
    public static MenuItem? HostMenu(HostEntry h)
    {
        var cmds = Commands("host").ToList();
        if (cmds.Count == 0 || h.IsAdHoc) return null;
        return Menus.Sub("◇ Add-ons", cmds.Select(x => (object?)Menus.AsyncItem($"{x.C.Title} — {x.A.Manifest.Name}", () => RunAsync(x.A, x.C, new AddonContext(h)))).ToArray());
    }

    /// <summary>"◇ Add-ons" submenu for a terminal.</summary>
    public static MenuItem? TerminalMenu(TerminalView tv)
    {
        var cmds = Commands("terminal").ToList();
        if (cmds.Count == 0) return null;
        return Menus.Sub("◇ Add-ons", cmds.Select(x => (object?)Menus.AsyncItem($"{x.C.Title} — {x.A.Manifest.Name}", () => RunAsync(x.A, x.C, new AddonContext(tv.Host, tv.Target.Title, tv.CaptureScreenAsync)))).ToArray());
    }
}

/// <summary>Sidebar → SYSTEM → Add-ons: installed add-ons, their permissions (grant/revoke), commands, run log.</summary>
public sealed class AddonsView : UserControl, IDisposable
{
    /// <summary>Closing the tab lets go of the static reference (closed-view leak check, s23).</summary>
    public void Dispose() { if (Current == this) Current = null; }

    public static AddonsView? Current { get; private set; }
    /// <summary>Self-test: the detail pane of one add-on, and its text.</summary>
    internal string ShowForTest(InstalledAddon a) { Show(a); return string.Join("\n", Menus.Descendants<TextBlock>(_detail).Select(t => t.Text)); }
    private readonly ListBox _list = new();
    private readonly StackPanel _detail = new() { Margin = new Thickness(16, 0, 0, 0) };

    public AddonsView()
    {
        Current = this;
        var root = new Grid { Margin = new Thickness(12, 10, 12, 10) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        root.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new DockPanel();
        var head = new StackPanel();
        head.Children.Add(new TextBlock { Text = "Add-ons", Style = (Style)FindResource("H1") });
        head.Children.Add(new TextBlock
        {
            Text = "Programs that extend the app, each run in a sandbox only when you click one of its commands. They get only the permissions you grant. SDK: docs/ADDONS.md.",
            Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8),
        });
        var install = new Button { Content = "＋ Install add-on…", Style = (Style)FindResource("PrimaryButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8) };
        install.Click += async (_, _) => { if (await AddonUi.InstallFromFile() is { } a) { Refresh(); Show(a); } };
        head.Children.Add(install);
        DockPanel.SetDock(head, Dock.Top);
        left.Children.Add(head);
        left.Children.Add(_list);
        root.Children.Add(left);
        var sv = new ScrollViewer { Content = _detail, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(sv, 1);
        root.Children.Add(sv);
        Content = root;
        _list.SelectionChanged += (_, _) => { if (_list.SelectedItem is FrameworkElement { Tag: InstalledAddon a }) Show(a); };
        Loaded += (_, _) => Refresh();
        Refresh();
    }

    public void Refresh()
    {
        var sel = (_list.SelectedItem as FrameworkElement)?.Tag as InstalledAddon;
        _list.ItemsSource = AddonPackages.All.OrderBy(a => a.Manifest.Name).Select(a =>
        {
            var p = new StackPanel { Tag = a, Margin = new Thickness(2, 3, 2, 5) };
            p.Children.Add(new TextBlock { Text = $"◇ {a.Title}", FontWeight = FontWeights.SemiBold });
            var s = new TextBlock { Text = (a.Enabled ? "enabled" : "DISABLED" + (a.DisabledReason != null ? " — " + a.DisabledReason : "")) + $" · {a.Manifest.Publisher}", TextWrapping = TextWrapping.Wrap }.Fs(FontStep.Small);
            s.SetResourceReference(TextBlock.ForegroundProperty, a.Enabled ? "Muted" : "Warn");
            p.Children.Add(s);
            return (FrameworkElement)p;
        }).ToList();
        var keep = sel != null ? _list.Items.OfType<FrameworkElement>().FirstOrDefault(e => e.Tag == sel) : _list.Items.OfType<FrameworkElement>().FirstOrDefault();
        _list.SelectedItem = keep;
        if (keep?.Tag is InstalledAddon k) Show(k);
        else
        {
            _detail.Children.Clear();
            var empty = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "No add-ons installed. ＋ Install add-on… takes a .dcxaddon file and shows exactly what it asks for before anything is installed." };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            _detail.Children.Add(empty);
        }
    }

    /// <summary>THEME (ADR-030): in use or not, the switch, and swatches of what it sets in the current mode.</summary>
    private void ThemeSection(InstalledAddon a)
    {
        bool inUse = ThemeManager.ActiveTheme?.Id == a.Id;
        bool chosen = AppSettings.Current.ThemeAddon == a.Id;
        _detail.Children.Add(new TextBlock { Text = "THEME", Style = (Style)FindResource("Label"), Margin = new Thickness(0, 10, 0, 4) });
        var status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = inUse ? $"◐ In use: the app is painted with {a.Manifest.Name}. The header's theme selector still switches between light and dark."
                : chosen ? "Chosen, but not applied: " + (a.Enabled ? "its theme file could not be read (see the error log)." : "the add-on is disabled" + (a.DisabledReason != null ? $" ({a.DisabledReason})." : "."))
                : "Not in use. The app is using " + (ThemeManager.ActiveTheme is { } other ? other.Manifest.Name : "its own theme") + ".",
        };
        status.SetResourceReference(TextBlock.ForegroundProperty, inUse ? "Good" : "Muted");
        _detail.Children.Add(status);
        var use = new Button { Content = inUse ? "Back to the default theme" : "Use this theme", Style = (Style)FindResource(inUse ? "SmallButton" : "PrimaryButton"), IsEnabled = inUse || a.Enabled, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 4) };
        use.Click += (_, _) => { ThemeAddons.Use(inUse ? null : a); MainWindow.Instance?.Status(inUse ? "◐ Default theme restored" : $"◐ {a.Manifest.Name} theme in use"); Refresh(); };
        _detail.Children.Add(use);
        var pack = ThemeAddons.Peek(a);
        if (pack?.LogoImage != null || pack?.ProductName != null)
        {
            var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 6) };
            if (pack.LogoImage is { } img)
            {
                var plate = new Border { CornerRadius = new CornerRadius(16), Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
                if (pack.LogoBackground is { } pc) plate.Background = new System.Windows.Media.SolidColorBrush(pc);
                plate.Child = new Image { Source = img, Height = 28, Width = Math.Min(160, 28.0 * img.PixelWidth / Math.Max(1, img.PixelHeight)) };
                brand.Children.Add(plate);
            }
            if (pack.ProductName != null) brand.Children.Add(new TextBlock { Text = $"shown as “{pack.ProductName}”", VerticalAlignment = VerticalAlignment.Center });
            _detail.Children.Add(brand);
        }
        var swatches = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        foreach (var key in new[] { "Bg", "Surface", "Fg", "Muted", "Line", "HeaderBg", "Accent", "AccentHover", "Brand", "Good", "Warn", "Bad" })
        {
            var cell = new StackPanel { Margin = new Thickness(0, 0, 8, 6), Width = 74 };
            var chip = new Border { Height = 22, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3) };
            chip.SetResourceReference(Border.BorderBrushProperty, "Line");
            if (!inUse && pack?.Get(key, ThemeManager.IsDark) is { } c) chip.Background = new System.Windows.Media.SolidColorBrush(c);
            else chip.SetResourceReference(Border.BackgroundProperty, key);
            cell.Children.Add(chip);
            var label = new TextBlock { Text = key }.Fs(FontStep.Micro);
            label.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            cell.Children.Add(label);
            swatches.Children.Add(cell);
        }
        _detail.Children.Add(swatches);
        if (pack?.UiFont is { Length: > 0 } font)
        {
            var f = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0),
                Text = $"UI font: {font}. " + (Typography.IsMissing(font) ? "Not installed on this PC, so the app's own font is used (install it through Windows Settings → Fonts for the full look)."
                    : !string.IsNullOrWhiteSpace(AppSettings.Current.UiFont) ? "Your own UI font (Settings → Typography) takes precedence." : "Installed; used while the theme is in use."),
            }.Fs(FontStep.Small);
            f.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            _detail.Children.Add(f);
        }
    }

    /// <summary>PORTAL BROWSER (ADR-032): whether Portals is on, the starter bookmarks, and the way to the Portals screen.</summary>
    private void BrowserSection(InstalledAddon a)
    {
        _detail.Children.Add(new TextBlock { Text = "PORTAL BROWSER", Style = (Style)FindResource("Label"), Margin = new Thickness(0, 10, 0, 4) });
        var pack = a.Enabled ? Browser.PortalBrowser.Pack(a) : null;
        var status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = !a.Enabled ? "Off: Portals is hidden and no portal opens. Your saved portals stay in the encrypted store." + (a.DisabledReason != null ? $" ({a.DisabledReason})" : "")
                : pack == null ? "The add-on's file could not be read (see the error log)."
                : $"🌐 On: Portals is in the sidebar (SERVERS) and hosts have “Open web UI”. {Browser.Portals.All.Count} portal(s) saved." + (pack.Starters.Count > 0 ? $" {pack.Name} offers {pack.Starters.Count} starter bookmark(s) on the Portals screen." : ""),
        };
        status.SetResourceReference(TextBlock.ForegroundProperty, a.Enabled && pack != null ? "Good" : "Muted");
        _detail.Children.Add(status);
        if (pack?.Notes is { Length: > 0 } notes) _detail.Children.Add(new TextBlock { Text = notes, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) }.Fs(FontStep.Small));
        var open = new Button { Content = "Open Portals", Style = (Style)FindResource("SmallButton"), IsEnabled = a.Enabled && pack != null, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 4) };
        open.Click += (_, _) => MainWindow.Instance?.Navigate("portals");
        _detail.Children.Add(open);
    }

    /// <summary>SHARED LIBRARY (ADR-033): joined or not, this PC's role and sync state, and the way to the Shared library screen.</summary>
    private void LibrarySection(InstalledAddon a)
    {
        _detail.Children.Add(new TextBlock { Text = "SHARED LIBRARY", Style = (Style)FindResource("Label"), Margin = new Thickness(0, 10, 0, 4) });
        var prof = Library.SharedLibraries.Profile(a, out var problem);
        var c = prof != null ? Library.SharedLibraries.Client(prof.LibraryId) : null;
        var status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = !a.Enabled ? "Off: the library's hosts and credentials are hidden. This PC's member keys (if any) stay sealed in the store." + (a.DisabledReason != null ? $" ({a.DisabledReason})" : "")
                : prof == null ? "The add-on's file could not be read: " + problem
                : prof.IsGeneral ? "⇄ Shared library is switched on: Sidebar → TEAM → Shared library. Create a library there, or install the add-on a library's owner gave you and Join it."
                : c == null ? $"⇄ “{prof.Name}” — this PC hasn't joined yet. Open Shared library → Join a library…\nOwner fingerprint: {prof.OwnerFingerprint}"
                : c.State.RemovedFromLibrary ? $"⇄ “{c.State.Name}” — this PC is no longer a member."
                : c.State.Pending ? $"⇄ “{c.State.Name}” — waiting for an admin to approve this PC (fingerprint {c.Keys.Fingerprint})."
                : $"⇄ “{c.State.Name}” — your role: {Library.Roles.Label(c.MyRole)} · {c.ItemsSnapshot().Count} shared item(s) · " + (c.State.LastError != null ? $"using the copy cached {c.State.LastSync:yyyy-MM-dd HH:mm}" : $"synced {c.State.LastSync:yyyy-MM-dd HH:mm}"),
        };
        status.SetResourceReference(TextBlock.ForegroundProperty, a.Enabled && (prof?.IsGeneral == true || c is { State.Pending: false, State.RemovedFromLibrary: false }) ? "Good" : "Muted");
        _detail.Children.Add(status);
        var open = new Button { Content = "Open Shared library", Style = (Style)FindResource("SmallButton"), IsEnabled = a.Enabled && prof != null, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 4) };
        open.Click += (_, _) => MainWindow.Instance?.Navigate("library");
        _detail.Children.Add(open);
    }

    private void Show(InstalledAddon a)
    {
        _detail.Children.Clear();
        _detail.Children.Add(new TextBlock { Text = a.Title, Style = (Style)FindResource("H2") });
        _detail.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Text = $"{a.Manifest.Publisher}\n{a.Manifest.Description}", Margin = new Thickness(0, 4, 0, 6) });
        var facts = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = a.Manifest.Library.Length > 0 ? $"id {a.Id} · library {a.Manifest.Library} · {a.Signature}\n" +
                   $"installed {a.Installed:yyyy-MM-dd} · updated {a.Updated:yyyy-MM-dd HH:mm} · package SHA-256 {a.PackageSha256[..16]}… · {a.Files.Count} file(s) verified whenever the library is loaded\nfolder {a.Folder}" :
                   a.Manifest.Browser.Length > 0 ? $"id {a.Id} · browser {a.Manifest.Browser} · {a.Signature}\n" +
                   $"installed {a.Installed:yyyy-MM-dd} · updated {a.Updated:yyyy-MM-dd HH:mm} · package SHA-256 {a.PackageSha256[..16]}… · {a.Files.Count} file(s) verified whenever the add-on's file is read\nfolder {a.Folder}" :
                   a.Manifest.Entry.Length == 0 ? $"id {a.Id} · theme {a.Manifest.Theme} · {a.Signature}\n" +
                   $"installed {a.Installed:yyyy-MM-dd} · updated {a.Updated:yyyy-MM-dd HH:mm} · package SHA-256 {a.PackageSha256[..16]}… · {a.Files.Count} file(s) verified every time the theme is applied\nfolder {a.Folder}" :
                   $"id {a.Id} · {a.Manifest.Entry} · {a.Signature}\nsandbox: Low integrity · job (≤ {SandboxProcess.MemoryLimit >> 20} MB, ≤ {SandboxProcess.ProcessLimit} processes) · timeout {a.Manifest.TimeoutSeconds} s\n" +
                   $"installed {a.Installed:yyyy-MM-dd} · updated {a.Updated:yyyy-MM-dd HH:mm} · package SHA-256 {a.PackageSha256[..16]}… · {a.Files.Count} file(s) verified before every run\nfolder {a.Folder}\nwritable data {AddonPackages.DataFolder(a.Id)}",
        }.Fs(FontStep.Meta).Mono();
        facts.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        _detail.Children.Add(facts);

        var state = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        Button B(string t, Action act, string style = "SmallButton") { var b = new Button { Content = t, Style = (Style)FindResource(style), Margin = new Thickness(0, 0, 6, 4) }; b.Click += (_, _) => act(); state.Children.Add(b); return b; }
        // Disabling is always allowed (the safe direction); enabling, updating and removing follow the add-on policy.
        B(a.Enabled ? "Disable" : "Enable", async () =>
        {
            if (!a.Enabled)
            {
                if (!Restrictions.Check("addon.install")) return;
                if (AddonPackages.UseRefusal(a) is { } why) { Dialogs.Info("Add-on", why); return; }
                if (!await AddonUi.AdminApprovedAsync($"Enable the add-on {a.Title} ({a.Id})")) return;
            }
            AddonPackages.SetEnabled(a, !a.Enabled); Refresh();
        });
        B("Update from file…", async () => { if (await AddonUi.InstallFromFile() is { } u) { Refresh(); Show(u); } });
        B("Remove…", async () =>
        {
            if (!Restrictions.Check("addon.install")) return;
            if (!Dialogs.Confirm("Remove add-on", $"Remove {a.Title} and its data folder? Its permissions go with it.", "Remove", danger: true)) return;
            if (!await AddonUi.AdminApprovedAsync($"Remove the add-on {a.Title} ({a.Id}) and its data")) return;
            AddonPackages.Remove(a); Refresh();
        }, "DangerButton");
        _detail.Children.Add(state);
        if (ThemeAddons.IsTheme(a.Manifest)) ThemeSection(a);
        if (Browser.PortalBrowser.IsBrowser(a.Manifest)) BrowserSection(a);
        if (Library.SharedLibraries.IsLibrary(a.Manifest)) LibrarySection(a);
        if (a.Manifest.Entry.Length == 0) return;   // theme only: no permissions, commands or runs

        _detail.Children.Add(new TextBlock { Text = "PERMISSIONS (untick to revoke — takes effect on the next call)", Style = (Style)FindResource("Label"), Margin = new Thickness(0, 10, 0, 4) });
        if (a.Manifest.Permissions.Count == 0) _detail.Children.Add(new TextBlock { Text = "none requested" });
        foreach (var perm in a.Manifest.Permissions)
        {
            var pi = AddonPackages.Permission(perm);
            var cb = new CheckBox { IsChecked = a.Granted.Contains(perm), Margin = new Thickness(0, 3, 0, 0), Content = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 540, Text = $"{perm} — {pi?.Text}   [risk: {pi?.Risk}]" } };
            cb.Click += async (_, _) =>
            {
                if (cb.IsChecked == true && pi?.Risk == "HIGH" && !Dialogs.Confirm("Grant permission", $"Allow {a.Manifest.Name} to {pi.Text.ToLowerInvariant()}?", "Allow", danger: true)) { cb.IsChecked = false; return; }
                // Granting is an install-time decision made later: same approval. Revoking is always allowed.
                if (cb.IsChecked == true && !await AddonUi.AdminApprovedAsync($"Grant {perm} to the add-on {a.Title} ({a.Id})")) { cb.IsChecked = false; return; }
                AddonPackages.SetPermission(a, perm, cb.IsChecked == true);
            };
            _detail.Children.Add(cb);
        }

        _detail.Children.Add(new TextBlock { Text = "COMMANDS", Style = (Style)FindResource("Label"), Margin = new Thickness(0, 10, 0, 4) });
        foreach (var c in a.Manifest.Commands)
        {
            var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
            var run = new Button { Content = "▶ Run", Style = (Style)FindResource("SmallButton"), IsEnabled = a.Enabled, Margin = new Thickness(0, 0, 8, 0) };
            run.Click += async (_, _) =>
            {
                HostEntry? host = null;
                if (c.Context == "host")
                {
                    host = Dialogs.Pick("Host", $"Run “{c.Title}” for which host?", AppData.Current.Hosts.OrderBy(h => h.Display, StringComparer.OrdinalIgnoreCase), h => $"{h.Display}   {h.Endpoint}");
                    if (host == null) return;
                }
                if (c.Context == "terminal") { Dialogs.Info("Add-on", "This command runs from a terminal: right-click inside a terminal → ◇ Add-ons."); return; }
                await AddonUi.RunAsync(a, c, new AddonContext(host));
            };
            row.Children.Add(run);
            row.Children.Add(new TextBlock { Text = $"{c.Title}   [{c.Context}]", VerticalAlignment = VerticalAlignment.Center });
            _detail.Children.Add(row);
        }

        _detail.Children.Add(new TextBlock { Text = "RUN LOG", Style = (Style)FindResource("Label"), Margin = new Thickness(0, 10, 0, 4) });
        _detail.Children.Add(new TextBox { IsReadOnly = true, Height = 140, TextWrapping = TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Text = a.RunLog.Count == 0 ? "(not run yet)" : string.Join("\n", a.RunLog.AsEnumerable().Reverse()) }.Mono());
    }
}
