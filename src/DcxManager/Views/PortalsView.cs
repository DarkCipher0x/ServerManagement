using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DcxManager.Browser;
using DcxManager.Infra;
using DcxManager.Services;

namespace DcxManager.Views;

/// <summary>Opening portals, the bookmark editor and the host menu entry (ADR-032).</summary>
public static class PortalUi
{
    /// <summary>Opens a bookmark in a portal tab (or a popped-out window). Null when the add-on is off or offline mode refuses.</summary>
    public static BrowserView? Open(PortalBookmark b, bool? window = null)
    {
        if (!PortalBrowser.Enabled) { Dialogs.Info("Portals", PortalBrowser.OffText); return null; }
        if (Portals.ValidateUrl(b.Url) is { } why) { Dialogs.Info("Portals", $"“{b.Title}” can't be opened: {why}."); return null; }
        if (ClosedTarget(b) is { } locked)
        {
            // 0.5.1 #10: a padlocked portal (or its host's padlock) opens after the unlock, never before.
            _ = Application.Current.Dispatcher.InvokeAsync(async () => { if (await LockGate.EnsureAsync(locked, "open " + b.Title)) Open(b, window); });
            return null;
        }
        if (Offline.On && !Offline.QuickAllowed(Offline.HostOf(b.Url))) { Offline.Run(b.Url, $"the portal “{b.Title}”", () => OpenNow(b, window)); return null; }
        return OpenNow(b, window);
    }

    private static BrowserView OpenNow(PortalBookmark b, bool? window)
    {
        var view = new BrowserView(b, b.Url);
        var tab = MainWindow.Instance.AddTab("🌐 " + (b.Title.Length > 36 ? b.Title[..36] + "…" : b.Title), view);
        view.Tab = tab;
        if (window ?? AppSettings.Current.PortalOpenIn == "window") MainWindow.Instance.Panes.PopOut(tab);
        if (Portals.Find(b.Id) != null) { b.LastOpened = DateTime.Now; Portals.Save(); }
        return view;
    }

    /// <summary>Opens a typed address (not saved) in the general profile, or the environment given.</summary>
    public static BrowserView? OpenAddress(string url, string environment = "") =>
        Open(new PortalBookmark { Title = Offline.HostOf(url), Url = Portals.Normalize(url), Environment = environment });

    /// <summary>A pop-up as a new tab in the opener's profile; the opener hands it the new window (no navigation of its own).</summary>
    public static BrowserView OpenPopup(BrowserView opener)
    {
        var view = new BrowserView(opener.Bookmark, null, opener.ProfileName, popup: true);
        var tab = MainWindow.Instance.AddTab("🌐 Pop-up", view);
        view.Tab = tab;
        // 0.6.4: in the opener's window (an extra window keeps its pop-ups), not a new one.
        if (opener.Tab != null && MainWindow.Instance.Panes.WindowOf(opener.Tab) is { } w) MainWindow.Instance.Panes.MoveToWindow(tab, w);
        return view;
    }

    /// <summary>Asks for the download folder (remembered). Null = cancelled (always in the self-test: no dialog may show).</summary>
    public static string? PickDownloadFolder()
    {
        if (SelfTest.Running) { Dialogs.RefusedInSelfTest("folder picker: portal downloads"); return null; }
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Folder for portal downloads", InitialDirectory = Portals.DefaultDownloadFolder };
        if (dlg.ShowDialog() != true) return null;
        AppSettings.Current.PortalDownloadFolder = dlg.FolderName;
        AppSettings.Save();
        return dlg.FolderName;
    }

    /// <summary>"🌐 Open web UI" in a host's menu (null when the add-on is off).</summary>
    public static MenuItem? HostMenu(HostEntry h)
    {
        if (!PortalBrowser.Enabled || h.IsAdHoc) return null;
        var items = Portals.ForHost(h.Id).OrderBy(b => b.Title).Select(b => (object?)Menus.Item($"{b.Title}  ·  {Portals.Display(b.Url)}", () => Open(b))).ToList();
        if (items.Count > 0) items.Add(Menus.Sep());
        items.Add(Menus.Item("＋ Add web UI…", () => Edit(new PortalBookmark { HostId = h.Id, Title = h.Display + " web UI", Url = $"https://{(h.Address.Contains(':') ? "[" + h.Address + "]" : h.Address)}/" })));
        return Menus.Sub(items.Count > 2 ? $"🌐 Open web UI ({items.Count - 2})" : "🌐 Open web UI", items.ToArray());
    }

    /// <summary>The lock target that keeps this saved portal shut right now (its own padlock, else its host's), or null.</summary>
    internal static string? ClosedTarget(PortalBookmark b)
    {
        if (Portals.Find(b.Id) == null) return null;
        foreach (var t in new[] { "portal:" + b.Id, b.HostId is { Length: > 0 } hid ? "host:" + hid : null })
            if (t != null && Locks.Hidden(t)) return t;
        return null;
    }

    /// <summary>The bookmark editor (new or existing). Returns true when saved.</summary>
    public static bool Edit(PortalBookmark original)
    {
        bool adding = Portals.Find(original.Id) == null;
        if (!Restrictions.Check(adding ? "portal.add" : "portal.edit")) return false;
        if (!adding && ClosedTarget(original) is { } locked)
        {
            _ = Application.Current.Dispatcher.InvokeAsync(async () => { if (await LockGate.EnsureAsync(locked, "edit " + original.Title)) Edit(original); });
            return false;
        }
        var b = new PortalBookmark
        {
            Id = original.Id, Title = original.Title, Url = original.Url, Folder = original.Folder, HostId = original.HostId,
            Environment = original.Environment, Profile = original.Profile, CredentialId = original.CredentialId, Created = original.Created, LastOpened = original.LastOpened,
        };
        bool isNew = Portals.Find(original.Id) == null;
        var w = Dialogs.MakeWindow(isNew ? "Add portal" : "Edit portal", 600);
        var p = new StackPanel { Margin = new Thickness(18) };
        TextBox Field(string label, string value, string? hint = null)
        {
            p.Children.Add(new TextBlock { Text = label, Style = (Style)Application.Current.Resources["Label"], Margin = new Thickness(0, 8, 0, 3) });
            var t = new TextBox { Text = value, Style = (Style)Application.Current.Resources["Field"] };
            p.Children.Add(t);
            if (hint != null) { var h = new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) }.Fs(FontStep.Small); h.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); p.Children.Add(h); }
            return t;
        }
        ComboBox Combo(string label, IEnumerable<(string Text, string? Value)> options, string? current)
        {
            p.Children.Add(new TextBlock { Text = label, Style = (Style)Application.Current.Resources["Label"], Margin = new Thickness(0, 8, 0, 3) });
            var c = new ComboBox();
            foreach (var (t, v) in options) c.Items.Add(new ComboBoxItem { Content = t, Tag = v });
            c.SelectedItem = c.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (i.Tag as string) == current) ?? c.Items[0];
            p.Children.Add(c);
            return c;
        }
        var title = Field("TITLE", b.Title);
        var url = Field("ADDRESS", b.Url, "https://… or http://… only. No user name or password in the address: use the sign-in credential below.");
        var folder = Field("FOLDER", b.Folder, "Optional, for the Portals list (use / for nested). Host web UIs also list under their host.");
        var host = Combo("HOST (web UI of)", new (string, string?)[] { ("(none — a global portal)", null) }.Concat(AppData.Current.Hosts.OrderBy(h => h.Display).Select(h => (h.Display + "  ·  " + h.Address, (string?)h.Id))), b.HostId);
        var env = Combo("ENVIRONMENT (picks the browser profile)", new (string, string?)[] { ("(from the host, or none)", "") }.Concat(Envs.All.Select(e => (e, (string?)e))), Envs.Normalize(b.Environment));
        var profile = Field("NAMED PROFILE (optional)", b.Profile, "Leave empty for one profile per environment. A name (for example “home lab”) gives these portals their own cookie jar.");
        var cred = Combo("SIGN-IN CREDENTIAL (for ⚿ Fill sign-in)", new (string, string?)[] { ("(none — or the host's password)", null) }
            .Concat(AppData.Current.Credentials.Where(c => c.Kind == CredentialKind.Password).OrderBy(c => c.Name).Select(c => (c.Display, (string?)c.Id))), b.CredentialId);
        // 0.6.19: 🔍 Find… searches this vault, Bitwarden, Secrets Manager and Password Safe (from the portal's title).
        var findCred = Dialogs.Btn("🔍 Find a credential…");
        findCred.HorizontalAlignment = HorizontalAlignment.Left; findCred.Margin = new Thickness(0, 6, 0, 0);
        findCred.ToolTip = "Search this vault, Bitwarden, Secrets Manager and Password Safe";
        findCred.Click += (_, _) =>
        {
            var hint = title.Text.Trim().Length > 0 ? title.Text.Trim() : url.Text.Trim();
            if (CredentialPicker.Pick(CredentialKind.Password, $"signing in to {(hint.Length > 0 ? hint : "this portal")}", hint) is not { } picked) return;
            if (cred.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (i.Tag as string) == picked.Id) is not { } item)
                cred.Items.Add(item = new ComboBoxItem { Content = picked.Display, Tag = picked.Id });
            cred.SelectedItem = item;
        };
        p.Children.Add(findCred);
        var note = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0),
            Text = "The browser never saves passwords or form data. The credential stays in the encrypted vault and is filled only when you click ⚿ Fill sign-in on this portal's own address." }.Fs(FontStep.Small);
        note.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        p.Children.Add(note);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        error.SetResourceReference(TextBlock.ForegroundProperty, "Bad");
        p.Children.Add(error);
        var save = Dialogs.Btn("Save", primary: true); var cancel = Dialogs.Btn("Cancel");
        cancel.Margin = new Thickness(8, 0, 0, 0); cancel.IsCancel = true;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(save); buttons.Children.Add(cancel);
        p.Children.Add(buttons);
        w.Content = p;
        bool saved = false;
        save.Click += (_, _) =>
        {
            b.Title = title.Text; b.Url = Portals.Normalize(url.Text); b.Folder = folder.Text; b.Profile = profile.Text;
            b.HostId = (host.SelectedItem as ComboBoxItem)?.Tag as string;
            b.Environment = (env.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
            b.CredentialId = (cred.SelectedItem as ComboBoxItem)?.Tag as string;
            if (Portals.Validate(b) is { } why) { error.Text = why; return; }
            if (!isNew)
            {
                original.Title = b.Title; original.Url = b.Url; original.Folder = b.Folder; original.HostId = b.HostId;
                original.Environment = b.Environment; original.Profile = b.Profile; original.CredentialId = b.CredentialId;
                b = original;
            }
            Portals.Upsert(b);
            saved = true;
            w.Close();
        };
        if (SelfTest.Running) { Dialogs.RefusedInSelfTest("portal editor"); return false; }
        w.ShowDialog();
        if (saved) PortalsView.Current?.Refresh();
        return saved;
    }
}

/// <summary>The Portals screen (ADR-032): saved portals and host web UIs, starter bookmarks from the add-on, where downloads
/// go and how portals open, trusted certificates and browser profiles.</summary>
public sealed class PortalsView : UserControl
{
    public static PortalsView? Current { get; private set; }
    private readonly ListView _list = new();
    private readonly StackPanel _side = new() { Margin = new Thickness(16, 0, 0, 0) };
    private readonly TextBlock _empty = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBox _filter = Hint.Set(new TextBox { MinWidth = 200 }, "Filter portals…");

    public PortalsView()
    {
        Current = this;
        var root = new DockPanel { Margin = new Thickness(18, 14, 18, 14) };
        var head = new StackPanel();
        head.Children.Add(new TextBlock { Text = "🌐 Portals", Style = (Style)FindResource("H2") });
        var sub = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8),
            Text = "Router, NAS, hypervisor and provider web UIs in tabs inside the app. One browser profile per environment keeps sign-ins apart; self-signed certificates are pinned after you compare them; downloads ask first; the browser never saves passwords." };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        head.Children.Add(sub);
        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        Button B(string t, Action a, string style = "SmallButton", string? tip = null) { var b = new Button { Content = t, Style = (Style)FindResource(style), Margin = new Thickness(0, 0, 6, 4), ToolTip = tip }; b.Click += (_, _) => a(); bar.Children.Add(b); return b; }
        B("＋ Add portal…", () => PortalUi.Edit(new PortalBookmark()), "PrimaryButton");
        B("Open address…", () => { if (Dialogs.Prompt("Open address", "Address (https://… or a host name):", "https://") is { Length: > 0 } u) PortalUi.OpenAddress(u); });
        _filter.SetResourceReference(StyleProperty, "Field");
        _filter.ToolTip = "Filter by title, address, folder or host";
        _filter.TextChanged += (_, _) => Refresh();
        bar.Children.Add(_filter);
        head.Children.Add(bar);
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        var sideScroll = new ScrollViewer { Content = _side, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Width = 320 };
        DockPanel.SetDock(sideScroll, Dock.Right);
        root.Children.Add(sideScroll);

        var grid = new GridView();
        GridViewColumn Col(string h, string path, double width) { var c = new GridViewColumn { Header = h, Width = width, DisplayMemberBinding = new System.Windows.Data.Binding(path) }; grid.Columns.Add(c); return c; }
        Col("Title", "Title", 200); Col("Address", "Address", 260); Col("Folder / host", "Where", 160); Col("Profile", "Profile", 110); Col("Last opened", "Last", 110);
        _list.View = grid;
        _list.MouseDoubleClick += (_, _) => { if (Selected is { } b) PortalUi.Open(b); };
        _list.KeyDown += (_, e) => { if (e.Key == Key.Enter && Selected is { } b) { PortalUi.Open(b); e.Handled = true; } else if (e.Key == Key.Delete) DeleteSelected(); };
        Menus.SelectOnRightClick(_list);
        Menus.Attach(_list, ItemMenu);
        var center = new DockPanel();
        _empty.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        DockPanel.SetDock(_empty, Dock.Top);
        center.Children.Add(_empty); center.Children.Add(_list);
        root.Children.Add(center);
        Content = root;
        // A cached screen: subscribed while shown (Live), so a portal added from a host menu or another window appears at once.
        Loaded += (_, _) => Current = this;
        Unloaded += (_, _) => { if (Current == this) Current = null; };
        Live.While(this, h => { AppData.Changed += h; PortalBrowser.Changed += h; Locks.Changed += h; }, h => { AppData.Changed -= h; PortalBrowser.Changed -= h; Locks.Changed -= h; }, Refresh);
        Refresh();
    }

    private sealed record Row(PortalBookmark B, string Title, string Address, string Where, string Profile, string Last);

    internal int RowCountForTest => (_list.ItemsSource as System.Collections.ICollection)?.Count ?? 0;

    private PortalBookmark? Selected => (_list.SelectedItem as Row)?.B;

    private IEnumerable<object?> ItemMenu()
    {
        var b = Selected;
        yield return Menus.Item("Open", () => { if (b != null) PortalUi.Open(b); }, "Enter", b != null);
        yield return Menus.Item("Open in a new window", () => { if (b != null) PortalUi.Open(b, window: true); }, enabled: b != null);
        yield return Menus.Item("Edit…", () => { if (b != null) PortalUi.Edit(b); }, enabled: b != null);
        yield return Menus.Item("Copy address", () => Menus.Clip(b?.Url), enabled: b != null);
        if (b != null) yield return LockUi.Menu("portal:" + b.Id, b.Title);
        yield return Menus.Sep();
        yield return Menus.Item("Delete…", DeleteSelected, "Del", b != null);
    }

    private void DeleteSelected()
    {
        if (Selected is { } l && (!Restrictions.Check("portal.edit") || PortalUi.ClosedTarget(l) is { } t && !Unlocked(t, l))) return;
        if (Selected is not { } b || !Dialogs.Confirm("Delete portal", $"Delete “{b.Title}” ({Portals.Display(b.Url)})? Cookies in its profile are kept.", "Delete", danger: true)) return;
        Portals.Delete(b);
        Locks.Forget("portal:" + b.Id);
        Refresh();
    }

    /// <summary>Asks for the unlock and repeats the delete afterwards; false now (the caller stops).</summary>
    private bool Unlocked(string target, PortalBookmark b)
    {
        _ = Dispatcher.InvokeAsync(async () => { if (await LockGate.EnsureAsync(target, "delete " + b.Title)) DeleteSelected(); });
        return false;
    }

    public void Refresh()
    {
        var f = _filter.Text.Trim();
        var rows = Portals.All
            .Select(b => new Row(b, LockUi.Decorate("portal:" + b.Id, b.Title), Portals.Display(b.Url),
                AppData.Current.FindHost(b.HostId) is { } h ? "▣ " + h.Display : b.Folder,
                Portals.ProfileLabel(Portals.ProfileFor(b)), b.LastOpened?.ToString("yyyy-MM-dd HH:mm") ?? "—"))
            .Where(r => f.Length == 0 || $"{r.Title} {r.B.Url} {r.Where}".Contains(f, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.Where).ThenBy(r => r.Title).ToList();
        _list.ItemsSource = rows;
        _empty.Text = !PortalBrowser.Enabled ? "⚠ " + PortalBrowser.OffText + " Portals can be edited here but not opened."
            : Portals.All.Count == 0 ? "No portals yet. ＋ Add portal…, or a host's menu → 🌐 Open web UI → ＋ Add web UI…" : "";
        _empty.Visibility = _empty.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        BuildSide();
    }

    private void BuildSide()
    {
        _side.Children.Clear();
        TextBlock Label(string t) { var l = new TextBlock { Text = t, Style = (Style)FindResource("Label"), Margin = new Thickness(0, 12, 0, 4) }; _side.Children.Add(l); return l; }
        TextBlock Text(string t, string brush = "Fg") { var x = new TextBlock { Text = t, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) }.Fs(FontStep.Small); x.SetResourceReference(TextBlock.ForegroundProperty, brush); _side.Children.Add(x); return x; }
        Button Btn(string t, Action a, string style = "SmallButton") { var b = new Button { Content = t, Style = (Style)FindResource(style), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 4) }; b.Click += (_, _) => a(); _side.Children.Add(b); return b; }

        Label("OPEN PORTALS IN");
        var openIn = new ComboBox { HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 220 };
        openIn.Items.Add(new ComboBoxItem { Content = "a tab in the main window", Tag = "tab" });
        openIn.Items.Add(new ComboBoxItem { Content = "a separate window", Tag = "window" });
        openIn.SelectedIndex = AppSettings.Current.PortalOpenIn == "window" ? 1 : 0;
        openIn.SelectionChanged += (_, _) => { AppSettings.Current.PortalOpenIn = (openIn.SelectedItem as ComboBoxItem)?.Tag as string ?? "tab"; AppSettings.Save(); };
        _side.Children.Add(openIn);
        Text("Either way a tab can pop out or dock back (tab menu, Ctrl+Alt+O).", "Muted");

        Label("DOWNLOADS");
        Text(Portals.DownloadFolder is { } df ? $"Saved to {df} — you're asked before every download." : "No folder yet: the first download asks for one. You're asked before every download.");
        Btn("Choose folder…", () => { if (PortalUi.PickDownloadFolder() != null) BuildSide(); });

        if (PortalBrowser.Active is { } addon && PortalBrowser.Pack(addon) is { Starters.Count: > 0 } pack)
        {
            Label("STARTER BOOKMARKS");
            var missing = pack.Starters.Where(s => !Portals.All.Any(b => b.Url.Equals(s.Url, StringComparison.OrdinalIgnoreCase))).ToList();
            Text($"{pack.Name} offers {pack.Starters.Count} bookmark(s); {missing.Count} not added yet.", "Muted");
            if (missing.Count > 0) Btn($"Add {missing.Count} starter bookmark(s)", () => { AddStarters(missing); Refresh(); });
        }

        Label("TRUSTED CERTIFICATES (PINNED)");
        if (AppData.Current.WebPins.Count == 0) Text("None. A self-signed certificate is refused until you compare its SHA-256 and trust it for that host:port.", "Muted");
        foreach (var pin in AppData.Current.WebPins.OrderBy(x => x.HostPort).ToList())
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var forget = new Button { Content = "Forget", Style = (Style)FindResource("SmallButton"), VerticalAlignment = VerticalAlignment.Top };
            forget.Click += async (_, _) =>
            {
                if (!Dialogs.Confirm("Forget certificate", $"Forget the trusted certificate of {pin.HostPort}? The next visit asks again.", "Forget")) return;
                Portals.Forget(pin.HostPort);
                await BrowserView.ClearAcceptedAsync();
                BuildSide();
            };
            DockPanel.SetDock(forget, Dock.Right);
            row.Children.Add(forget);
            var t = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = $"{pin.HostPort}\n{TlsPin.Pretty(pin.Sha256)}\ntrusted {pin.Trusted:yyyy-MM-dd HH:mm}" }.Fs(FontStep.Meta).Mono();
            row.Children.Add(t);
            _side.Children.Add(row);
        }

        Label("BROWSER PROFILES");
        foreach (var prof in Portals.ProfilesInUse()) Text("● " + Portals.ProfileLabel(prof) + $"  ({prof})");
        Text("Each profile has its own cookies and site storage, kept by the WebView2 runtime under this app's local data folder and protected by Windows (DPAPI) — not by the app's own store. Clear a profile from an open tab's ⋯ menu.", "Muted");
    }

    /// <summary>Adds an add-on's starter bookmarks (skipping addresses already saved).</summary>
    internal static int AddStarters(IEnumerable<(string Title, string Url, string Folder)> starters)
    {
        int n = 0;
        foreach (var (t, u, f) in starters)
        {
            if (Portals.All.Any(b => b.Url.Equals(u, StringComparison.OrdinalIgnoreCase))) continue;
            Portals.Upsert(new PortalBookmark { Title = t, Url = u, Folder = f });
            n++;
        }
        return n;
    }
}
