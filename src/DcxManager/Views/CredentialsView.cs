using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using DcxManager.Services;
using DcxManager.Ssh;
using DcxManager.Vultr;
using Microsoft.Win32;

namespace DcxManager.Views;

/// <summary>The vault: passwords and SSH keys, all inside the encrypted store. Since 0.4.0 organised in folders (like the
/// hosts) and by type (ssh, sudo, database, api token…): a folder tree on the left, a type filter above the list.</summary>
public sealed class CredentialsView : UserControl, IDisposable
{
    private const string AllFolders = "All", NoFolder = "(no folder)", SharedFolder = "⇄ shared";
    private readonly ListView _list = new();
    private readonly ListBox _folders = new() { MinWidth = 150 };
    private readonly ComboBox _type = new() { Width = 150, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Only credentials of this type" };
    private readonly TextBox _filter = new() { Width = 180, Padding = new Thickness(5, 3, 5, 3), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Name, user, notes or fingerprint" };
    private readonly TextBlock _detail = new TextBlock() { TextWrapping = TextWrapping.Wrap }.Fs(FontStep.Meta);
    private readonly CheckBox _sharedOnly = new() { Content = "⇄ Shared only", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 4), ToolTip = "Only credentials shared through your team's libraries (Sidebar → TEAM → Team credentials)" };
    private bool _reloading;

    public CredentialsView()
    {
        var root = new DockPanel { Margin = new Thickness(14, 12, 14, 12) };
        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        head.Children.Add(new TextBlock { Text = "Credentials", Style = (Style)FindResource("H1") });
        head.Children.Add(new TextBlock { Text = "Passwords and SSH keys, in folders and by type. Everything here is encrypted at rest with AES-256-GCM; the key is protected by your Windows account and, if set, your master password (Settings → Security).", Style = (Style)FindResource("Hint"), Margin = new Thickness(0, 4, 0, 0) });
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        Button B(string t, RoutedEventHandler h, string? style = null) { var b = new Button { Content = t, Margin = new Thickness(0, 0, 6, 4) }; if (style != null) b.Style = (Style)FindResource(style); b.Click += h; bar.Children.Add(b); return b; }
        B("＋ Add", OnAdd, "PrimaryButton");
        B("🔑 New SSH key…", (_, _) => SshKeyWindow.Show(Window.GetWindow(this)));
        // 0.6.19: add a Bitwarden item, Secrets Manager secret or Password Safe account by searching for it.
        B("⛓ From Bitwarden / Password Safe…", (_, _) =>
            {
                var before = AppData.Current.Credentials.Count;
                var sources = CredentialSearch.Sources();
                if (CredentialPicker.Pick(null, "your vault", "", sources.Count > 1 ? sources[1] : CredentialSearch.Vault) is not { } c) return;
                Reload();
                _list.SelectedItem = _list.Items.OfType<Credential>().FirstOrDefault(x => x.Id == c.Id);
                MainWindow.Instance.Status(AppData.Current.Credentials.Count > before ? $"Added “{c.Name}” ({c.ExternalText})." : $"“{c.Name}” is already in the vault.");
            });
        B("✎ Edit", OnEdit);
        B("✕ Delete", OnDelete, "DangerButton");
        B("📁 New folder…", OnNewFolder);
        bar.Children.Add(Menus.MoreButton(() =>
        {
            bool anyKey = Sel is { Kind: CredentialKind.PrivateKey };
            bool key = Sel is { Kind: CredentialKind.PrivateKey, IsExternal: false };   // 0.6.19: a key kept in Bitwarden has no public / private key text here
            var ev = new RoutedEventArgs();
            return new object?[]
            {
                Menus.Item("⧉ Copy public key", () => OnCopyPublic(this, ev), enabled: key),
                Menus.Item("⇪ Upload public key to Vultr", () => OnUploadVultr(this, ev), enabled: key && VultrClient.Active != null),
                Menus.AsyncItem("⇪ Upload public key to Contabo (Secrets)", () => UploadToContaboAsync(Sel!), enabled: key && Contabo.ContaboProvider.Instance.Active != null),
                Menus.AsyncItem("⇪ Upload public key to Hetzner Cloud", () => UploadToHetznerAsync(Sel!), enabled: key && Hetzner.HetznerProvider.Instance.Active != null),
                Menus.Item("⇩ Export private key…", () => OnExport(this, ev), enabled: key, tip: "Writes the private key to a file you choose, optionally protected by a passphrase"),
                Menus.AsyncItem("⇪ Install public key on hosts…", () => SshKeyWindow.InstallOnHostsAsync(Window.GetWindow(this), Sel!, s => MainWindow.Instance.Status(s.Split('\n')[^1])), enabled: key),
                Menus.Sep(),
                Menus.Item(Sel != null && AppSettings.Current.DefaultKeyCredentialId == Sel.Id ? "★ Default for quick connect (current)" : "★ Use for quick connect", () => OnDefault(this, ev), enabled: anyKey),
            };
        }));
        bar.Children.Add(new TextBlock { Text = "type", Style = (Style)FindResource("Label"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 6, 0) });
        bar.Children.Add(_type);
        bar.Children.Add(new TextBlock { Text = "find", Style = (Style)FindResource("Label"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 6, 0) });
        bar.Children.Add(_filter);
        _sharedOnly.Visibility = Library.SharedLibraries.Enabled ? Visibility.Visible : Visibility.Collapsed;
        _sharedOnly.Click += (_, _) => Reload();
        bar.Children.Add(_sharedOnly);
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);

        var detailBorder = new Border { Style = (Style)FindResource("Card"), Margin = new Thickness(0, 10, 0, 0), Child = _detail };
        _detail.Mono();
        DockPanel.SetDock(detailBorder, Dock.Bottom);
        root.Children.Add(detailBorder);

        var gv = new GridView();
        void Col(string h, string path, double w) => gv.Columns.Add(new GridViewColumn { Header = h, Width = w, DisplayMemberBinding = new Binding(path) });
        Col("Name", "ListName", 200); Col("Folder", "FolderText", 150); Col("Type", "CategoryText", 110); Col("Kind", "KindText", 130); Col("User", "Username", 100); Col("Source", "Source", 160); Col("Fingerprint", "Fingerprint", 300); Col("Notes", "Notes", 240);
        _list.View = gv;
        _list.MouseDoubleClick += OnEdit;
        _list.SelectionChanged += (_, _) => ShowDetail();
        Menus.ForList(_list, this, () => Sel is not { } c ? Array.Empty<object?>() : SharedLibraryUi.CredentialMenu(c, Window.GetWindow(this)).Concat(new object?[]
        {
            c.IsShared ? null : LockUi.Menu("cred:" + c.Id, c.Name),
            Menus.Item("Copy name", () => Menus.Clip(c.Name)),
            Menus.Item("Copy username", () => Menus.Clip(c.Username), enabled: c.Username.Length > 0),
            Menus.Item("Hosts using this credential…", () =>
            {
                var hosts = AppData.Current.Hosts.Where(h => h.CredentialId == c.Id || h.SudoCredentialId == c.Id).Select(h => h.Display).ToList();
                Dialogs.ShowText($"Hosts using “{c.Name}”", hosts.Count == 0 ? "No host uses this credential." : string.Join("\n", hosts));
            }),
            Menus.Sep(),
            Menus.Item("📁 Move to folder…", () => MoveToFolder(c), enabled: !c.IsShared),
            Menus.Sub("Set type", Credential.Categories.Select(t => (object?)Menus.Item((c.Category == t ? "✓ " : "   ") + t, () => SetCategory(c, t), enabled: !c.IsShared))
                .Append(Menus.Sep()).Append(Menus.Item((c.Category.Length == 0 ? "✓ " : "   ") + "(none)", () => SetCategory(c, ""), enabled: !c.IsShared)).ToArray()),
            Menus.Sep(),
            Menus.AssistItem("✦ Review key hygiene with AI (metadata only)", a => a.AskAsync("credential vault (metadata)",
                SafeMetadata.Credentials(AppData.Current.Credentials),
                "Review this SSH credential inventory. Only metadata is given — no secrets. Flag weak or old key types (RSA < 3072, DSA), keys without passphrases, unused credentials, one key shared by many hosts, password logins that should be keys, and suggest a rotation plan.")),
        }).ToArray());

        // Folders on the left (every folder a credential is in, with the parents they imply), the list on the right.
        _folders.SelectionChanged += (_, _) => { if (!_reloading) Reload(); };
        Menus.ForList(_folders, this, () => _folders.SelectedItem is not ListBoxItem { Tag: string f } || f is AllFolders or NoFolder or SharedFolder ? Array.Empty<object?>() : new object?[]
        {
            Menus.Item("Rename folder…", () => RenameFolder(f)),
            Menus.Item("Remove folder (credentials go to its parent)", () => RemoveFolder(f)),
        });
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210), MinWidth = 120 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 300 });
        var split = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeBehavior = GridResizeBehavior.PreviousAndNext, ToolTip = "Drag to resize" };
        Grid.SetColumn(_folders, 0); Grid.SetColumn(split, 1); Grid.SetColumn(_list, 2);
        _list.Margin = new Thickness(8, 0, 0, 0);
        grid.Children.Add(_folders); grid.Children.Add(split); grid.Children.Add(_list);
        root.Children.Add(grid);
        Content = root;

        _type.SelectionChanged += (_, _) => { if (!_reloading) Reload(); };
        _filter.TextChanged += (_, _) => { if (!_reloading) Reload(); };
        AppData.Changed += Reload;
        Library.SharedLibraries.Changed += Reload;
        Locks.Changed += OnLocksChanged;
        Reload();
    }

    public void Dispose() { AppData.Changed -= Reload; Library.SharedLibraries.Changed -= Reload; Locks.Changed -= OnLocksChanged; }

    private void OnLocksChanged() => Dispatcher.BeginInvoke(Reload);

    /// <summary>Runs <paramref name="action"/> once the credential is unlocked (0.5.1, ADR-067); at once when it isn't locked.</summary>
    private void Unlocked(Credential c, string what, Action action)
    {
        if (c.IsShared || Locks.IsOpen("cred:" + c.Id)) { action(); return; }
        _ = Dispatcher.InvokeAsync(async () => { if (await LockGate.EnsureAsync("cred:" + c.Id, what)) action(); });
    }

    /// <summary>Sidebar → TEAM → Team credentials (true) or VAULT → Credentials (false).</summary>
    public void ShowSharedOnly(bool on)
    {
        if (_sharedOnly.IsChecked == on) return;
        _sharedOnly.IsChecked = on;
        Reload();
    }

    internal int RowsForTest => _list.Items.Count;
    internal IReadOnlyList<string> FoldersForTest => _folders.Items.OfType<ListBoxItem>().Select(i => (string)i.Tag).ToList();
    internal void SelectFolderForTest(string folder) { _folders.SelectedItem = _folders.Items.OfType<ListBoxItem>().FirstOrDefault(i => (string)i.Tag == folder); }
    internal void SetTypeForTest(string type) { _type.SelectedItem = _type.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == type); }

    private string SelectedFolder => (_folders.SelectedItem as ListBoxItem)?.Tag as string ?? AllFolders;
    private string SelectedType => (_type.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    private void Reload()
    {
        _reloading = true;
        try
        {
            var sel = (_list.SelectedItem as Credential)?.Id;
            var folderWas = SelectedFolder; var typeWas = SelectedType;
            _sharedOnly.Visibility = Library.SharedLibraries.Enabled ? Visibility.Visible : Visibility.Collapsed;
            bool sharedOnly = _sharedOnly.IsChecked == true && Library.SharedLibraries.Enabled;
            var local = sharedOnly ? new List<Credential>() : AppData.Current.Credentials.ToList();
            // Shared library credentials (ADR-033) follow the local ones under their own folder; they are never stored in this vault.
            var shared = Library.SharedLibraries.Credentials.OrderBy(c => c.SharedFrom).ThenBy(c => c.Name).ToList();

            // Folders: the paths credentials are in and the parents they imply, with counts.
            var paths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in local)
            {
                var parts = AppData.NormalizeFolder(c.Folder).Split('/', StringSplitOptions.RemoveEmptyEntries);
                for (int i = 1; i <= parts.Length; i++) paths.Add(string.Join("/", parts.Take(i)));
            }
            _folders.Items.Clear();
            ListBoxItem Row(string label, string tag, int count, int depth = 0) => new() { Content = $"{label}  ({count})", Tag = tag, Padding = new Thickness(6 + depth * 14, 3, 6, 3) };
            _folders.Items.Add(Row("📂 All", AllFolders, local.Count + shared.Count));
            if (local.Any(c => c.Folder.Length == 0)) _folders.Items.Add(Row("📁 (no folder)", NoFolder, local.Count(c => c.Folder.Length == 0)));
            foreach (var p in paths)
                _folders.Items.Add(Row("📁 " + p.Split('/')[^1], p, local.Count(c => AppData.InFolder(AppData.NormalizeFolder(c.Folder), p)), p.Count(ch => ch == '/')));
            if (shared.Count > 0) _folders.Items.Add(Row(SharedFolder, SharedFolder, shared.Count));
            _folders.SelectedItem = _folders.Items.OfType<ListBoxItem>().FirstOrDefault(i => (string)i.Tag == folderWas) ?? _folders.Items[0];

            // Types: the usual ones plus any word in use.
            var types = Credential.Categories.Concat(local.Concat(shared).Select(c => c.Category).Where(c => c.Length > 0)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            _type.Items.Clear();
            _type.Items.Add(new ComboBoxItem { Content = "all types", Tag = "" });
            foreach (var t in types) _type.Items.Add(new ComboBoxItem { Content = t, Tag = t });
            _type.SelectedItem = _type.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == typeWas) ?? _type.Items[0];

            var folder = SelectedFolder; var type = SelectedType; var q = _filter.Text.Trim();
            IEnumerable<Credential> rows = folder switch
            {
                AllFolders => local.OrderBy(c => c.Folder, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Kind).ThenBy(c => c.Name).Concat(shared),
                NoFolder => local.Where(c => c.Folder.Length == 0).OrderBy(c => c.Kind).ThenBy(c => c.Name),
                SharedFolder => shared,
                _ => local.Where(c => AppData.InFolder(AppData.NormalizeFolder(c.Folder), folder)).OrderBy(c => c.Folder, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Name),
            };
            if (type.Length > 0) rows = rows.Where(c => c.CategoryText.Equals(type, StringComparison.OrdinalIgnoreCase));
            if (q.Length > 0) rows = rows.Where(c => $"{c.Name} {c.Username} {c.Notes} {c.Fingerprint} {c.Folder} {c.Category}".Contains(q, StringComparison.OrdinalIgnoreCase));
            var all = rows.ToList();
            _list.ItemsSource = all;
            _list.SelectedItem = all.FirstOrDefault(c => c.Id == sel);
            ShowDetail();
        }
        finally { _reloading = false; }
    }

    private Credential? Sel => _list.SelectedItem as Credential;

    private void ShowDetail()
    {
        if (Sel is not { } c) { _detail.Text = $"{AppData.Current.Credentials.Count} credential(s) in the vault, {AppData.Current.AllCredentialFolders.Count(f => AppData.Current.Credentials.Any(x => AppData.InFolder(AppData.NormalizeFolder(x.Folder), f)))} folder(s) in use. Right-click a credential to move it or set its type."; return; }
        var used = AppData.Current.Hosts.Concat(Library.SharedLibraries.Hosts).Where(h => h.CredentialId == c.Id || h.SudoCredentialId == c.Id).Select(h => h.Display).ToList();
        var where = (c.Folder.Length > 0 ? $"folder {c.Folder}" : "no folder") + (c.CategoryText.Length > 0 ? $", type {c.CategoryText}" : "");
        if (!c.IsShared && !Locks.IsOpen("cred:" + c.Id)) { _detail.Text = $"🔒 {c.Name} is locked. Right-click → 🔒 Lock → Unlock for now to see its details."; return; }
        if (c.IsShared) { _detail.Text = $"{c.Name} — {c.KindText}, shared: {c.SharedFrom}\nUsed by: {(used.Count == 0 ? "no hosts" : string.Join(", ", used))}\n\nIt lives in the shared library, not in this vault. Copy to my vault makes a local copy; edits go to the library (editors only)."; return; }
        var isDefault = AppSettings.Current.DefaultKeyCredentialId == c.Id ? "  ★ quick-connect default" : "";
        if (c.IsExternal)   // 0.6.17 (ADR-096)
        {
            _detail.Text = $"⛓ {c.Name} — {c.KindText} kept in {CredentialSources.Describe(c)}, {where}{isDefault}\nUsed by: {(used.Count == 0 ? "no hosts" : string.Join(", ", used))}\n\n" +
                           $"Nothing secret is stored here: it is read into memory each time it is used ({(c.External.StartsWith("ps:") ? "checked out of Password Safe, one checkout shared until it runs out" : "Bitwarden asks for the master password when the vault is locked")}). Dashboard and scheduled checks never ask: they fail until it has been used once.";
            return;
        }
        _detail.Text = c.Kind == CredentialKind.Password
            ? $"{c.Name} — password ({c.Secret.Length} characters), {where}{isDefault}\nUsed by: {(used.Count == 0 ? "no hosts" : string.Join(", ", used))}"
            : $"{c.Name} — {c.KeyType} {c.Fingerprint}{(c.Passphrase.Length > 0 ? " (passphrase stored)" : "")}, {where}{isDefault}\nUsed by: {(used.Count == 0 ? "no hosts" : string.Join(", ", used))}\n\n{c.PublicKey}";
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        if (!Restrictions.Check("credential.add")) return;
        var w = new CredentialEditWindow(null) { Owner = Window.GetWindow(this) };
        if (w.ShowDialog() != true || w.Result is not { } c) return;
        // A new credential lands in the folder being looked at.
        if (c.Folder.Length == 0 && SelectedFolder is not (AllFolders or NoFolder or SharedFolder)) c.Folder = SelectedFolder;
        AppData.Current.Credentials.Add(c);
        AppData.Save();
        AuditLog.Write("you", "vault", $"added credential {c.Name} ({c.KindText}{(c.Folder.Length > 0 ? ", " + c.Folder : "")})");
    }

    private void OnEdit(object sender, RoutedEventArgs e)
    {
        if (Sel is not { } c) return;
        if (!c.IsShared && !Restrictions.Check("credential.edit")) return;
        if (!c.IsShared && !Locks.IsOpen("cred:" + c.Id)) { Unlocked(c, "open " + c.Name, () => OnEdit(sender, e)); return; }
        if (c.IsExternal)   // 0.6.17 (ADR-096): nothing secret to show or edit here; the name, user name and folder are
        {
            var name = Dialogs.Prompt("Rename", $"“{c.Name}” is kept in {CredentialSources.Describe(c)}: change it there. Its name in this vault:", c.Name);
            if (string.IsNullOrWhiteSpace(name) || name.Trim() == c.Name) return;
            c.Name = name.Trim().Length > 120 ? name.Trim()[..120] : name.Trim();
            AppData.Save();
            AuditLog.Write("you", "vault", $"renamed credential {c.Name}");
            return;
        }
        // The editor shows a private key in clear text (0.3.0 §2: confirm with Windows Hello when that is on).
        if (!SensitiveAction.Confirm($"open the credential “{c.Name}”", Window.GetWindow(this))) return;
        if (c.IsShared) { SharedLibraryUi.EditCredential(c, Window.GetWindow(this)); return; }
        var w = new CredentialEditWindow(c) { Owner = Window.GetWindow(this) };
        if (w.ShowDialog() != true || w.Result is not { } edited) return;
        var i = AppData.Current.Credentials.FindIndex(x => x.Id == c.Id);
        AppData.Current.Credentials[i] = edited;
        foreach (var h in AppData.Current.Hosts.Where(h => h.CredentialId == c.Id)) SshPool.Drop(h.Id);
        AppData.Save();
        AuditLog.Write("you", "vault", $"edited credential {edited.Name}");
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (Sel is not { } c) return;
        if (c.IsShared) { SharedLibraryUi.DeleteShared(new() { c.Id }, "credential"); return; }
        if (!Restrictions.Check("credential.delete")) return;
        if (!Locks.IsOpen("cred:" + c.Id)) { Unlocked(c, "delete " + c.Name, () => OnDelete(sender, e)); return; }
        var used = AppData.Current.Hosts.Count(h => h.CredentialId == c.Id || h.SudoCredentialId == c.Id);
        if (!Dialogs.Confirm("Delete credential", $"Delete “{c.Name}” from the vault?{(used > 0 ? $"\n\n{used} host(s) use it and will ask for a password instead." : "")}\n\nThis can't be undone — export the key first if you have no other copy.", "Delete", danger: true)) return;
        AppData.Current.Credentials.Remove(c); Locks.Forget("cred:" + c.Id);
        foreach (var h in AppData.Current.Hosts)
        {
            if (h.CredentialId == c.Id) h.CredentialId = null;
            if (h.SudoCredentialId == c.Id) h.SudoCredentialId = null;
        }
        if (AppSettings.Current.DefaultKeyCredentialId == c.Id) { AppSettings.Current.DefaultKeyCredentialId = null; AppSettings.Save(); }
        AppData.Save();
        AuditLog.Write("you", "vault", $"deleted credential {c.Name}");
    }

    // ---- folders and types (0.4.0) ------------------------------------------------------------------------------------

    private void OnNewFolder(object sender, RoutedEventArgs e)
    {
        if (!Restrictions.Check("credential.edit")) return;
        var parent = SelectedFolder is AllFolders or NoFolder or SharedFolder ? "" : SelectedFolder + "/";
        if (Dialogs.Prompt("New folder", "Folder path (use / for nested, e.g. Production/Databases). A folder exists while a credential is in it — move or add one to it:", parent) is not { Length: > 0 } p || AppData.NormalizeFolder(p) is not { Length: > 0 } np) return;
        if (Sel is { IsShared: false } c) { SetFolder(c, np); return; }
        var w = new CredentialEditWindow(null) { Owner = Window.GetWindow(this) };
        if (w.ShowDialog() != true || w.Result is not { } made) return;
        made.Folder = np;
        AppData.Current.Credentials.Add(made);
        AppData.Save();
        AuditLog.Write("you", "vault", $"added credential {made.Name} ({made.KindText}, {np})");
    }

    private void MoveToFolder(Credential c)
    {
        var p = Dialogs.Prompt("Move to folder", $"Folder for “{c.Name}” (empty = top level; use / for nested):", c.Folder);
        if (p == null) return;
        SetFolder(c, AppData.NormalizeFolder(p));
    }

    private void SetFolder(Credential c, string folder)
    {
        if (c.Folder == folder) return;
        if (!Restrictions.Check("credential.edit")) return;
        if (!Locks.IsOpen("cred:" + c.Id)) { Unlocked(c, "move " + c.Name, () => SetFolder(c, folder)); return; }
        c.Folder = folder;
        AppData.Save();
        AuditLog.Write("you", "vault", $"moved credential {c.Name} to {(folder.Length > 0 ? folder : "the top level")}");
        _folders.SelectedItem = _folders.Items.OfType<ListBoxItem>().FirstOrDefault(i => (string)i.Tag == (folder.Length > 0 ? folder : NoFolder));
    }

    private void SetCategory(Credential c, string category)
    {
        if (c.Category == category) return;
        if (!Restrictions.Check("credential.edit")) return;
        if (!Locks.IsOpen("cred:" + c.Id)) { Unlocked(c, "change " + c.Name, () => SetCategory(c, category)); return; }
        c.Category = category;
        AppData.Save();
        AuditLog.Write("you", "vault", $"credential {c.Name} is now type {(category.Length > 0 ? category : "(none)")}");
    }

    private void RenameFolder(string folder)
    {
        if (!Restrictions.Check("credential.edit")) return;
        if (Dialogs.Prompt("Rename folder", "New name or path (use / for nested):", folder) is not { Length: > 0 } p || AppData.NormalizeFolder(p) is not { Length: > 0 } np || np.Equals(folder, StringComparison.OrdinalIgnoreCase)) return;
        int n = 0;
        foreach (var c in AppData.Current.Credentials.Where(c => AppData.InFolder(AppData.NormalizeFolder(c.Folder), folder)))
        {
            c.Folder = np + AppData.NormalizeFolder(c.Folder)[folder.Length..];
            n++;
        }
        AppData.Save();
        AuditLog.Write("you", "vault", $"renamed credential folder {folder} → {np} ({n} credential(s))");
    }

    private void RemoveFolder(string folder)
    {
        if (!Restrictions.Check("credential.edit")) return;
        var inside = AppData.Current.Credentials.Where(c => AppData.InFolder(AppData.NormalizeFolder(c.Folder), folder)).ToList();
        var parent = folder.Contains('/') ? folder[..folder.LastIndexOf('/')] : "";
        if (!Dialogs.Confirm("Remove folder", $"Remove the folder “{folder}”? Its {inside.Count} credential(s) move to {(parent.Length > 0 ? parent : "the top level")}; nothing is deleted.", "Remove")) return;
        foreach (var c in inside) c.Folder = parent;
        AppData.Save();
        AuditLog.Write("you", "vault", $"removed credential folder {folder} ({inside.Count} moved to {(parent.Length > 0 ? parent : "the top level")})");
    }

    // ---- keys ---------------------------------------------------------------------------------------------------------

    private void OnCopyPublic(object sender, RoutedEventArgs e)
    {
        if (Sel is { Kind: CredentialKind.PrivateKey } c) Unlocked(c, "copy the public key of " + c.Name, () => { Clipboard.SetText(c.PublicKey); MainWindow.Instance.Status("Public key copied."); });
    }

    private async void OnUploadVultr(object sender, RoutedEventArgs e)
    {
        if (Sel is not { Kind: CredentialKind.PrivateKey } c) { Dialogs.Info("Upload", "Select an SSH key."); return; }
        if (!Locks.IsOpen("cred:" + c.Id) && !await LockGate.EnsureAsync("cred:" + c.Id, "upload " + c.Name)) return;
        await UploadToVultrAsync(c);
    }

    public static Task UploadToVultrAsync(Credential c) => VultrProvider.UploadPublicKeyAsync(c);

    public static async Task UploadToContaboAsync(Credential c)
    {
        try
        {
            var api = Contabo.ContaboProvider.Instance.Require();
            await Contabo.ContaboProvider.UploadPublicKeyAsync(api, c);
            Dialogs.Info("Contabo", $"“{c.Name}” was added to Contabo Secrets (type ssh) on {api.AccountName}. Orders, reinstalls and rescue can now use it.");
        }
        catch (Exception ex) { Dialogs.Error("Contabo", ex.Message); }
    }

    public static async Task UploadToHetznerAsync(Credential c)
    {
        try
        {
            var api = Hetzner.HetznerProvider.Instance.Require();
            await Hetzner.HetznerProvider.UploadPublicKeyAsync(api, c);
            Dialogs.Info("Hetzner Cloud", $"“{c.Name}” was added to the SSH keys of project {api.AccountName}. New servers, rebuilds and rescue can now use it.");
        }
        catch (Exception ex) { Dialogs.Error("Hetzner Cloud", ex.Message); }
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        if (Sel is { Kind: CredentialKind.PrivateKey } c) Unlocked(c, "export " + c.Name, () => SshKeyWindow.ExportDialog(Window.GetWindow(this), c));
    }

    private void OnDefault(object sender, RoutedEventArgs e)
    {
        if (Sel is not { Kind: CredentialKind.PrivateKey } c) { Dialogs.Info("Quick connect", "Select an SSH key."); return; }
        AppSettings.Current.DefaultKeyCredentialId = c.Id;
        AppSettings.Save();
        ShowDetail();
        MainWindow.Instance.Status($"Quick connect will try “{c.Name}” first.");
    }
}
