using System.Windows;
using System.Windows.Controls;
using DcxManager.Library;
using DcxManager.Services;

namespace DcxManager.Views;

/// <summary>
/// Shared library permissions made easy (0.3.0 §5, DarkCipher0x: "easily assign permissions to different people who joined, and
/// different libraries or folders or credentials specifically"):
/// <list type="bullet">
/// <item><b>One member:</b> role and, per folder (collection), no access / read / edit — in one window, not a question per
/// folder.</item>
/// <item><b>Permissions grid:</b> every member × every folder of a library at once; only the changed members are applied.</item>
/// <item><b>One item:</b> "Limit who can see this…" moves a host or credential into its own folder that only the people you
/// tick can open. Access is enforced by the encryption (a folder is a collection with its own key), not just hidden.</item>
/// </list>
/// Removing someone's access re-keys that folder and lists the credentials they could read for rotation (as before).
/// </summary>
public static class LibraryAccessUi
{
    private static readonly string[] Levels = { "no access", "read", "edit" };
    private static string Level(string? role) => role == Roles.Editor ? "edit" : role == Roles.Reader ? "read" : "no access";
    private static string? RoleOf(string level) => level switch { "edit" => Roles.Editor, "read" => Roles.Reader, _ => null };

    /// <summary>Role and per-folder access for one member (new or existing). Null = cancelled.</summary>
    public static (string Role, Dictionary<string, string> Access)? Ask(LibraryClient c, string who, RosterEntry? current, Window? owner)
    {
        var w = Dialogs.MakeWindow("Access — " + who, 520);
        if (owner != null) w.Owner = owner;
        var p = new StackPanel { Margin = new Thickness(18) };
        p.Children.Add(new TextBlock { Text = $"What may {who} do? Admins can read and edit every folder and manage members; only the owner appoints admins.", TextWrapping = TextWrapping.Wrap });
        var role = new ComboBox { Width = 200, Margin = new Thickness(0, 10, 0, 6), HorizontalAlignment = HorizontalAlignment.Left };
        role.Items.Add("Member"); if (c.IsOwner) role.Items.Add("Admin");
        role.SelectedIndex = current?.Role == Roles.Admin && c.IsOwner ? 1 : 0;
        p.Children.Add(new TextBlock { Text = "ROLE", Style = (Style)Application.Current.Resources["Label"], Margin = new Thickness(0, 10, 0, 0) });
        p.Children.Add(role);
        p.Children.Add(new TextBlock { Text = "FOLDERS", Style = (Style)Application.Current.Resources["Label"], Margin = new Thickness(0, 8, 0, 4) });
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var combos = new Dictionary<string, ComboBox>();
        int row = 0;
        foreach (var (cid, name) in c.State.Collections.OrderBy(x => x.Value, StringComparer.OrdinalIgnoreCase))
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = "📁 " + name, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 12, 3) };
            var cb = new ComboBox { Width = 140, ItemsSource = Levels, SelectedItem = Level(current?.Access.GetValueOrDefault(cid)), Margin = new Thickness(0, 3, 0, 3) };
            Grid.SetRow(label, row); Grid.SetRow(cb, row); Grid.SetColumn(cb, 1);
            grid.Children.Add(label); grid.Children.Add(cb);
            combos[cid] = cb;
            row++;
        }
        p.Children.Add(grid);
        var allRead = new Button { Content = "All: read", Margin = new Thickness(0, 6, 6, 0) }; var allNone = new Button { Content = "All: no access", Margin = new Thickness(0, 6, 0, 0) };
        allRead.Click += (_, _) => { foreach (var cb in combos.Values) cb.SelectedItem = "read"; };
        allNone.Click += (_, _) => { foreach (var cb in combos.Values) cb.SelectedItem = "no access"; };
        var quick = new StackPanel { Orientation = Orientation.Horizontal }; quick.Children.Add(allRead); quick.Children.Add(allNone);
        p.Children.Add(quick);
        role.SelectionChanged += (_, _) => grid.IsEnabled = role.SelectedIndex == 0;
        grid.IsEnabled = role.SelectedIndex == 0;
        var ok = new Button { Content = "OK", Style = (Style)Application.Current.Resources["PrimaryButton"], IsDefault = true };
        var bs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        bs.Children.Add(ok); bs.Children.Add(new Button { Content = "Cancel", IsCancel = true });
        p.Children.Add(bs);
        w.Content = p;
        ok.Click += (_, _) => w.DialogResult = true;
        if (w.ShowDialog() != true) return null;
        var r = role.SelectedIndex == 1 ? Roles.Admin : Roles.Member;
        var access = r == Roles.Admin ? new Dictionary<string, string>() : combos.Where(kv => RoleOf((string)kv.Value.SelectedItem) != null).ToDictionary(kv => kv.Key, kv => RoleOf((string)kv.Value.SelectedItem)!);
        return (r, access);
    }

    /// <summary>The permissions grid: members down, folders across. Applies only the members that changed.</summary>
    public static void Matrix(LibraryClient c, Window? owner)
    {
        if (c.State.Roster is not { } roster) return;
        var w = Dialogs.MakeWindow("Permissions — " + c.State.Name, 860, 560);
        if (owner != null) w.Owner = owner;
        var root = new DockPanel { Margin = new Thickness(16) };
        var intro = new TextBlock { Text = "Who can open which folder. Admins and the owner can open everything. Taking access away re-keys that folder and lists the credentials the person could read, to change them.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(intro, Dock.Top); root.Children.Add(intro);
        var cols = c.State.Collections.OrderBy(x => x.Value, StringComparer.OrdinalIgnoreCase).ToList();
        var members = roster.Members.Where(m => m.Role != Roles.Owner && m.Id != c.Me).OrderBy(m => c.NameOf(m.Id)).ToList();
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        foreach (var _ in cols) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        void Cell(UIElement e, int r, int col) { Grid.SetRow(e, r); Grid.SetColumn(e, col); g.Children.Add(e); }
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Cell(new TextBlock { Text = "MEMBER", Style = (Style)Application.Current.Resources["Label"] }, 0, 0);
        Cell(new TextBlock { Text = "ROLE", Style = (Style)Application.Current.Resources["Label"] }, 0, 1);
        for (int i = 0; i < cols.Count; i++) Cell(new TextBlock { Text = "📁 " + cols[i].Value, Style = (Style)Application.Current.Resources["Label"], TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = cols[i].Value }, 0, i + 2);
        var rows = new List<(RosterEntry M, ComboBox Role, Dictionary<string, ComboBox> Cells)>();
        int r = 1;
        foreach (var m in members)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Cell(new TextBlock { Text = c.NameOf(m.Id), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 6, 3) }, r, 0);
            var role = new ComboBox { Margin = new Thickness(0, 3, 6, 3), IsEnabled = c.IsOwner };
            role.Items.Add("Member"); role.Items.Add("Admin");
            role.SelectedIndex = m.Role == Roles.Admin ? 1 : 0;
            Cell(role, r, 1);
            var cells = new Dictionary<string, ComboBox>();
            for (int i = 0; i < cols.Count; i++)
            {
                var cb = new ComboBox { ItemsSource = Levels, SelectedItem = Level(m.Access.GetValueOrDefault(cols[i].Key)), Margin = new Thickness(0, 3, 6, 3), IsEnabled = m.Role != Roles.Admin };
                cells[cols[i].Key] = cb;
                Cell(cb, r, i + 2);
            }
            role.SelectionChanged += (_, _) => { foreach (var cb in cells.Values) cb.IsEnabled = role.SelectedIndex == 0; };
            rows.Add((m, role, cells));
            r++;
        }
        var scroll = new ScrollViewer { Content = g, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var bs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var apply = new Button { Content = "Apply changes", Style = (Style)Application.Current.Resources["PrimaryButton"], IsDefault = true };
        bs.Children.Add(apply); bs.Children.Add(new Button { Content = "Close", IsCancel = true });
        DockPanel.SetDock(bs, Dock.Bottom); root.Children.Add(bs);
        if (members.Count == 0) root.Children.Add(new TextBlock { Text = "No other members yet: approve join requests first.", Margin = new Thickness(0, 10, 0, 0) });
        else root.Children.Add(scroll);
        w.Content = root;
        apply.Click += (_, _) =>
        {
            var tasks = new List<RotationTask>();
            int changed = 0;
            try
            {
                foreach (var (m, roleBox, cells) in rows)
                {
                    var role = roleBox.SelectedIndex == 1 ? Roles.Admin : Roles.Member;
                    var access = role == Roles.Admin ? new Dictionary<string, string>() : cells.Where(kv => RoleOf((string)kv.Value.SelectedItem) != null).ToDictionary(kv => kv.Key, kv => RoleOf((string)kv.Value.SelectedItem)!);
                    bool same = role == m.Role && access.Count == m.Access.Count && access.All(a => m.Access.GetValueOrDefault(a.Key) == a.Value);
                    if (same) continue;
                    tasks.AddRange(c.SetAccess(m.Id, role, access));
                    changed++;
                }
            }
            catch (LibraryException ex) { Dialogs.Error("Permissions", ex.Message, explain: false); return; }
            SharedLibraries.Rebuild();
            w.DialogResult = true;
            Dialogs.Info("Permissions", $"{changed} member(s) changed." + (tasks.Count > 0 ? "\n\nThese credentials were readable by someone who lost access; change them on their targets (also on the ROTATION CHECKLIST):\n" + string.Join("\n", tasks.Select(t => $"• {t.Collection} / {t.Name}")) : ""));
        };
        w.ShowDialog();
    }

    /// <summary>Groups (0.3.0 §22): named sets of members with their own per-folder access, on top of each member's own.
    /// New / edit / delete in one window; a change re-keys folders people lose and lists credentials to rotate.</summary>
    public static void Groups(LibraryClient c, Window? owner)
    {
        if (c.State.Roster is not { } roster) return;
        var w = Dialogs.MakeWindow("Groups — " + c.State.Name, 720, 520);
        if (owner != null) w.Owner = owner;
        var root = new DockPanel { Margin = new Thickness(16) };
        var intro = new TextBlock { Text = "A member's access is their own plus every group's. Groups make team-wide rights one change: put people in “Ops” once and give Ops the folders. Enforced by the encryption like a direct grant.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(intro, Dock.Top); root.Children.Add(intro);
        var list = new ListBox { Height = 160 };
        void Fill() { list.Items.Clear(); foreach (var g in c.State.Roster!.Groups.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)) list.Items.Add(new ListBoxItem { Content = $"{g.Name}  ·  {g.Members.Count} member(s)  ·  {string.Join(", ", g.Access.Select(a => c.CollectionName(a.Key) + ": " + a.Value))}", Tag = g }); }
        Fill();
        var bar = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        Button B(string t, Action a, string? style = null) { var b = new Button { Content = t, Margin = new Thickness(0, 0, 6, 4) }; if (style != null) b.Style = (Style)Application.Current.Resources["PrimaryButton"]; b.Click += (_, _) => { try { a(); } catch (LibraryException ex) { Dialogs.Error("Groups", ex.Message, explain: false); } }; bar.Children.Add(b); return b; }
        RosterGroup? Sel() => (list.SelectedItem as ListBoxItem)?.Tag as RosterGroup;
        void Edit(RosterGroup? g)
        {
            var d = Dialogs.MakeWindow(g == null ? "New group" : "Group — " + g.Name, 520);
            d.Owner = w;
            var p = new StackPanel { Margin = new Thickness(18) };
            p.Children.Add(new TextBlock { Text = "NAME", Style = (Style)Application.Current.Resources["Label"] });
            var name = new TextBox { Text = g?.Name ?? "", Padding = new Thickness(5, 4, 5, 4), IsEnabled = g == null };
            p.Children.Add(name);
            p.Children.Add(new TextBlock { Text = "MEMBERS", Style = (Style)Application.Current.Resources["Label"], Margin = new Thickness(0, 8, 0, 4) });
            var boxes = new List<(RosterEntry M, CheckBox Box)>();
            foreach (var m in roster.Members.Where(m => !Roles.IsAdmin(m.Role)).OrderBy(m => c.NameOf(m.Id)))
            {
                var cb = new CheckBox { Content = c.NameOf(m.Id), IsChecked = g?.Members.Contains(m.Id) == true, Margin = new Thickness(0, 2, 0, 2) };
                boxes.Add((m, cb)); p.Children.Add(cb);
            }
            if (boxes.Count == 0) p.Children.Add(new TextBlock { Text = "(no members besides admins yet)", Margin = new Thickness(0, 2, 0, 2) });
            p.Children.Add(new TextBlock { Text = "FOLDERS", Style = (Style)Application.Current.Resources["Label"], Margin = new Thickness(0, 8, 0, 4) });
            var combos = new Dictionary<string, ComboBox>();
            foreach (var (cid, cname) in c.State.Collections.OrderBy(x => x.Value, StringComparer.OrdinalIgnoreCase))
            {
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                var cb = new ComboBox { Width = 140, ItemsSource = Levels, SelectedItem = Level(g?.Access.GetValueOrDefault(cid)) };
                DockPanel.SetDock(cb, Dock.Right); row.Children.Add(cb);
                row.Children.Add(new TextBlock { Text = "📁 " + cname, VerticalAlignment = VerticalAlignment.Center });
                combos[cid] = cb; p.Children.Add(row);
            }
            var ok = new Button { Content = "Save group", Style = (Style)Application.Current.Resources["PrimaryButton"], IsDefault = true };
            var bs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            bs.Children.Add(ok); bs.Children.Add(new Button { Content = "Cancel", IsCancel = true });
            p.Children.Add(bs);
            d.Content = new ScrollViewer { Content = p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 640 };
            ok.Click += (_, _) => { if (name.Text.Trim().Length == 0) { Dialogs.Error("Groups", "A name is needed.", explain: false); return; } d.DialogResult = true; };
            if (d.ShowDialog() != true) return;
            var access = combos.Where(kv => RoleOf((string)kv.Value.SelectedItem) != null).ToDictionary(kv => kv.Key, kv => RoleOf((string)kv.Value.SelectedItem)!);
            var tasks = c.SetGroup(name.Text.Trim(), boxes.Where(b => b.Box.IsChecked == true).Select(b => b.M.Id), access);
            SharedLibraries.Rebuild(); Fill();
            if (tasks.Count > 0) Dialogs.Info("Groups", "These credentials were readable by someone who lost access; change them on their targets (also on the ROTATION CHECKLIST):\n" + string.Join("\n", tasks.Select(t => $"• {t.Collection} / {t.Name}")));
        }
        B("＋ New group…", () => Edit(null), "PrimaryButton");
        B("✎ Edit…", () => { if (Sel() is { } g) Edit(g); });
        B("✕ Delete…", () =>
        {
            if (Sel() is not { } g || !Dialogs.Confirm("Groups", $"Delete the group “{g.Name}”? Its members keep only their own access; folders they lose are re-keyed.", "Delete", danger: true)) return;
            var tasks = c.RemoveGroup(g.Name);
            SharedLibraries.Rebuild(); Fill();
            if (tasks.Count > 0) Dialogs.Info("Groups", "Credentials to rotate (someone lost access):\n" + string.Join("\n", tasks.Select(t => $"• {t.Collection} / {t.Name}")));
        });
        bar.Children.Add(new Button { Content = "Close", IsCancel = true, Margin = new Thickness(0, 0, 6, 4) });
        DockPanel.SetDock(bar, Dock.Bottom); root.Children.Add(bar);
        root.Children.Add(list);
        w.Content = root;
        list.MouseDoubleClick += (_, _) => { if (Sel() is { } g) Edit(g); };
        w.ShowDialog();
    }

    /// <summary>"Limit who can see this…": the item moves into its own folder that only the ticked members can open.</summary>
    public static void Restrict(LibraryClient c, CachedItem item, Window? owner)
    {
        if (!c.IsAdmin) { Dialogs.Info("Limit access", "Only admins can change who can open what."); return; }
        var title = LibraryClient.Title(item);
        var members = c.State.Roster?.Members.Where(m => !Roles.IsAdmin(m.Role)).OrderBy(m => c.NameOf(m.Id)).ToList() ?? new();
        var w = Dialogs.MakeWindow("Limit who can see “" + title + "”", 480);
        if (owner != null) w.Owner = owner;
        var p = new StackPanel { Margin = new Thickness(18) };
        p.Children.Add(new TextBlock { Text = $"“{title}” moves into a folder of its own, “{title} (restricted)”, with its own key. Admins and the owner can always open it; tick the members who may too. Anyone not ticked loses it on their next sync.", TextWrapping = TextWrapping.Wrap });
        var picks = new List<(RosterEntry M, CheckBox Box, CheckBox Edit)>();
        foreach (var m in members)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            var box = new CheckBox { Content = c.NameOf(m.Id), Width = 220 };
            var edit = new CheckBox { Content = "may edit" };
            row.Children.Add(box); row.Children.Add(edit);
            p.Children.Add(row);
            picks.Add((m, box, edit));
        }
        if (members.Count == 0) p.Children.Add(new TextBlock { Text = "(No members besides admins: only admins will see it.)", Margin = new Thickness(0, 8, 0, 0) });
        var ok = new Button { Content = "Limit access", Style = (Style)Application.Current.Resources["PrimaryButton"], IsDefault = true };
        var bs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        bs.Children.Add(ok); bs.Children.Add(new Button { Content = "Cancel", IsCancel = true });
        p.Children.Add(bs);
        w.Content = p;
        ok.Click += (_, _) => w.DialogResult = true;
        if (w.ShowDialog() != true) return;
        try
        {
            var cid = MoveTo(c, item, title + " (restricted)");
            foreach (var (m, box, edit) in picks.Where(x => x.Box.IsChecked == true))
            {
                var access = new Dictionary<string, string>(m.Access) { [cid] = edit.IsChecked == true ? Roles.Editor : Roles.Reader };
                c.SetAccess(m.Id, m.Role, access);
            }
            SharedLibraries.Rebuild();
            AuditLog.Write("you", "shared library " + c.State.Name, "item access limited", $"{title}: {picks.Count(x => x.Box.IsChecked == true)} member(s)");
            Dialogs.Info("Limit access", $"“{title}” is now in its own folder. {picks.Count(x => x.Box.IsChecked == true)} member(s) besides the admins can open it.\n\nPeople who could read it before have already seen it: if it is a credential, change it on its targets if that matters.");
        }
        catch (LibraryException ex) { Dialogs.Error("Limit access", ex.Message, explain: false); }
    }

    /// <summary>Creates the folder and moves the item into it (a new signed revision there, a tombstone where it was).</summary>
    internal static string MoveTo(LibraryClient c, CachedItem item, string folder)
    {
        var cid = c.CreateCollection(folder.Length > 60 ? folder[..60] : folder);
        c.SaveItem(cid, null, item.Payload);
        c.DeleteItem(item.Item);
        return cid;
    }
}
