using System.Windows;
using System.Windows.Controls;
using DcxManager.Services;

namespace DcxManager.Views;

/// <summary>
/// The tick tree of an item-by-item export or import (0.3.0 §11): groups → folders → items, each a tri-state check box,
/// with a live line saying how many items and how many secrets are ticked. Built from this store (export) or from a
/// read file (import); <see cref="Choice"/> turns the ticks into a <see cref="Backup.Choice"/>. Never shows a window
/// itself (the self-test builds and drives it off screen).
/// </summary>
public sealed class BackupPickTree : DockPanel
{
    private sealed class Node
    {
        public required string Label { get; init; }
        /// <summary>"host", "cred", "acct:vultr", "note"… for an item; null for a group or folder.</summary>
        public string? Kind { get; init; }
        public string? Id { get; init; }
        public Node? Parent { get; set; }
        public List<Node> Children { get; } = new();
        public CheckBox Box { get; set; } = null!;
        public IEnumerable<Node> Leaves => Children.Count == 0 ? (Kind != null ? [this] : []) : Children.SelectMany(c => c.Leaves);
    }

    private readonly List<Node> _roots = new();
    private readonly TreeView _tree = new() { BorderThickness = new Thickness(0) };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private bool _updating;

    /// <summary>Raised whenever a tick changes (the dialogs enable/disable their button on it).</summary>
    public event Action? Changed;

    public BackupPickTree(AppData data, AppSettings? settings, IEnumerable<Notes.Note> notes, IEnumerable<Incidents.Incident> incidents,
                          bool reportsAvailable, Backup.Choice? ticked = null)
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        var all = new Button { Content = "Select all", Margin = new Thickness(0, 0, 6, 0) };
        var none = new Button { Content = "Select none" };
        all.Click += (_, _) => SetAll(true); none.Click += (_, _) => SetAll(false);
        bar.Children.Add(all); bar.Children.Add(none);
        SetDock(bar, Dock.Top); Children.Add(bar);
        SetDock(_summary, Dock.Bottom); Children.Add(_summary);
        Children.Add(_tree);

        // Hosts, in their folders.
        var hosts = Group("Hosts", "▣");
        var folders = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);
        Node FolderNode(string path)
        {
            path = AppData.NormalizeFolder(path);
            if (path.Length == 0) path = "General";
            if (folders.TryGetValue(path, out var f)) return f;
            var cut = path.LastIndexOf('/');
            var parent = cut < 0 ? hosts : FolderNode(path[..cut]);
            f = Add(parent, new Node { Label = "📁 " + (cut < 0 ? path : path[(cut + 1)..]) });
            return folders[path] = f;
        }
        foreach (var h in data.Hosts.OrderBy(h => h.Group, StringComparer.OrdinalIgnoreCase).ThenBy(h => h.Display, StringComparer.OrdinalIgnoreCase))
            Add(FolderNode(h.Group), new Node { Label = h.Display + (h.Address.Length > 0 && h.Address != h.Display ? "  ·  " + h.Address : ""), Kind = "host", Id = h.Id });

        var creds = Group("Credentials (passwords and keys)", "⚿");
        foreach (var c in data.Credentials.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            Add(creds, new Node { Label = $"{c.Name}  ({c.KindText}{(c.Username.Length > 0 ? ", " + c.Username : "")})", Kind = "cred", Id = c.Id });

        if (settings != null)
        {
            var cloud = Group("Cloud accounts (API keys)", "☁");
            void Accounts<T>(string id, string name, IEnumerable<T> list) where T : Cloud.ICloudAccount
            {
                var items = list.ToList();
                if (items.Count == 0) return;
                var p = Add(cloud, new Node { Label = name });
                foreach (var a in items) Add(p, new Node { Label = a.Name + (a.Disabled ? "  (disabled)" : ""), Kind = "acct:" + id, Id = a.Id });
            }
            Accounts("vultr", "Vultr", settings.VultrAccounts);
            Accounts("contabo", "Contabo", settings.ContaboAccounts);
            Accounts("hetzner", "Hetzner", settings.HetznerAccounts);
            Accounts("azure", "Azure", settings.AzureAccounts);
            Accounts("gcp", "Google Cloud", settings.GcpAccounts);
        }

        void Simple<T>(string label, string icon, string kind, IEnumerable<T> items, Func<T, string> id, Func<T, string> text)
        {
            var g = Group(label, icon);
            foreach (var i in items.OrderBy(text, StringComparer.OrdinalIgnoreCase)) Add(g, new Node { Label = text(i), Kind = kind, Id = id(i) });
        }
        Simple("Infrastructure providers", "◫", "infra", data.InfraProviders, p => p.Id, p => $"{p.Name}  ({p.Kind})");
        Simple("Portals and web UIs", "🌐", "portal", data.Portals, p => p.Id, p => p.Title.Length > 0 ? p.Title : p.Url);
        Simple("Tunnels", "⇄", "tunnel", data.Tunnels, t => t.Id, t => (t.Name.Length > 0 ? t.Name + "  ·  " : "") + t.Summary);
        Simple("Serial consoles", "≋", "serial", data.SerialProfiles, s => s.Id, s => $"{s.Name}  ({s.Port})");
        Simple("Snippets", "⚡", "snippet", data.Snippets, s => s.Id, s => s.Name);
        Simple("Notes", "✎", "note", notes, n => n.Id, n => n.Title);
        Simple("Incidents", "⚑", "incident", incidents, i => i.Id, i => i.Title);

        var other = Group("Other", "◇");
        if (data.KnownHosts.Count > 0) Add(other, new Node { Label = $"Trusted SSH host keys ({data.KnownHosts.Count})", Kind = "known" });
        if (data.WebPins.Count > 0) Add(other, new Node { Label = $"Trusted web certificates ({data.WebPins.Count})", Kind = "webpins" });
        if (data.LanMonitors.Count > 0) Add(other, new Node { Label = $"LAN monitoring schedules ({data.LanMonitors.Count})", Kind = "lanmon" });
        if (settings != null) Add(other, new Node { Label = "App settings (terminal profiles, appearance, AI engines…)", Kind = "settings" });
        if (reportsAvailable) Add(other, new Node { Label = "Saved reports", Kind = "reports" });

        // Empty groups say nothing useful: leave them out.
        foreach (var r in _roots.Where(r => !r.Leaves.Any()).ToList()) _roots.Remove(r);
        foreach (var r in _roots) _tree.Items.Add(Build(r, expand: _roots.Count <= 3));

        if (ticked != null) foreach (var leaf in _roots.SelectMany(r => r.Leaves)) leaf.Box.IsChecked = IsIn(leaf, ticked);
        else SetAll(true);
        Refresh();
    }

    /// <summary>The tree for this store's current contents.</summary>
    public static BackupPickTree ForStore(Backup.Choice ticked) =>
        new(AppData.Current, AppSettings.Current, Notes.NoteStore.Snapshot().Where(n => n.Deleted == null),
            Incidents.IncidentStore.Snapshot().Incidents.Where(i => i.Deleted == null), reportsAvailable: true, ticked);

    /// <summary>The tree for what a read file holds (everything ticked).</summary>
    public static BackupPickTree ForFile(Backup.BackupFile b)
    {
        // Only list "App settings" when the file really carries them (not just picked cloud accounts).
        var settings = b.Settings;
        var t = new BackupPickTree(b.Data ?? new AppData(), settings, b.Notes ?? [], b.Incidents ?? [], b.Reports is { Count: > 0 }, Backup.Choice.From(b));
        if (b.AppSettingsIncluded == false) t.Remove("settings");
        return t;
    }

    private void Remove(string kind)
    {
        foreach (var leaf in _roots.SelectMany(r => r.Leaves).Where(l => l.Kind == kind).ToList())
        {
            leaf.Parent?.Children.Remove(leaf);
            if (leaf.Box.Parent is TreeViewItem tvi && ItemsControl.ItemsControlFromItemContainer(tvi) is { } owner) owner.Items.Remove(tvi);
        }
        Refresh();
    }

    private Node Group(string label, string icon) { var n = new Node { Label = icon + "  " + label }; _roots.Add(n); return n; }

    private static Node Add(Node parent, Node child) { child.Parent = parent; parent.Children.Add(child); return child; }

    private TreeViewItem Build(Node n, bool expand)
    {
        // Groups show "mixed" (set in code) but a click only ever ticks or clears all of it (not three-state for the user).
        n.Box = new CheckBox { Content = n.Label, Focusable = false };
        n.Box.Click += (_, _) => { if (n.Children.Count > 0) Set(n, n.Box.IsChecked == true); };
        n.Box.Checked += (_, _) => OnLeaf(n);
        n.Box.Unchecked += (_, _) => OnLeaf(n);
        var item = new TreeViewItem { Header = n.Box, IsExpanded = expand };
        foreach (var c in n.Children) item.Items.Add(Build(c, expand: false));
        return item;
    }

    private void OnLeaf(Node n)
    {
        if (_updating || n.Children.Count > 0) return;
        Refresh();
    }

    private void Set(Node n, bool on)
    {
        _updating = true;
        try { foreach (var l in n.Leaves) l.Box.IsChecked = on; }
        finally { _updating = false; }
        Refresh();
    }

    public void SetAll(bool on) { foreach (var r in _roots) Set(r, on); }

    /// <summary>0.5.0 (ADR-060): when exports may not carry secrets, the credential and cloud-key groups are cleared and
    /// greyed out with the reason (Backup.Write strips them anyway; this only shows it before you press Export).</summary>
    public void LockSecrets(string why)
    {
        foreach (var r in _roots.Where(r => r.Label.Contains("Credentials") || r.Label.Contains("Cloud accounts")))
        {
            Set(r, false);
            foreach (var n in r.Leaves.Append(r)) { n.Box.IsEnabled = false; n.Box.ToolTip = why; }
        }
    }

    /// <summary>Ticks or unticks one item (the self-test drives the tree with this).</summary>
    public void Tick(string kind, string? id, bool on)
    {
        foreach (var l in _roots.SelectMany(r => r.Leaves).Where(l => l.Kind == kind && (id == null || l.Id == id))) l.Box.IsChecked = on;
        Refresh();
    }

    private void Refresh()
    {
        if (_updating) return;
        _updating = true;
        try { foreach (var r in _roots) Rollup(r); }
        finally { _updating = false; }
        var leaves = _roots.SelectMany(r => r.Leaves).Where(l => l.Box.IsChecked == true).ToList();
        var creds = leaves.Count(l => l.Kind == "cred");
        var keys = leaves.Count(l => l.Kind!.StartsWith("acct:"));
        _summary.Text = leaves.Count == 0 ? "Nothing ticked." :
            $"{leaves.Count} item(s) ticked · {creds} password(s)/key(s) · {keys} cloud API key(s)" +
            (leaves.Any(l => l.Kind == "settings") ? " · app settings (may include AI engine keys)" : "");
        Changed?.Invoke();
    }

    private static bool? Rollup(Node n)
    {
        if (n.Children.Count == 0) return n.Box.IsChecked == true;
        var states = n.Children.Select(Rollup).ToList();
        n.Box.IsChecked = states.All(s => s == true) ? true : states.All(s => s == false) ? false : null;
        return n.Box.IsChecked;
    }

    private static bool IsIn(Node leaf, Backup.Choice c) => leaf.Kind switch
    {
        "host" => c.Hosts.Contains(leaf.Id!), "cred" => c.Credentials.Contains(leaf.Id!), "snippet" => c.Snippets.Contains(leaf.Id!),
        "tunnel" => c.Tunnels.Contains(leaf.Id!), "serial" => c.Serial.Contains(leaf.Id!), "infra" => c.Infra.Contains(leaf.Id!),
        "portal" => c.Portals.Contains(leaf.Id!), "note" => c.Notes.Contains(leaf.Id!), "incident" => c.Incidents.Contains(leaf.Id!),
        "known" => c.KnownHosts, "webpins" => c.WebPins, "lanmon" => c.LanMonitors, "settings" => c.AppSettings, "reports" => c.Reports,
        { } k when k.StartsWith("acct:") => c.Accounts[k[5..]].Contains(leaf.Id!),
        _ => false,
    };

    /// <summary>The ticks as a <see cref="Backup.Choice"/>.</summary>
    public Backup.Choice Choice
    {
        get
        {
            var c = new Backup.Choice();
            foreach (var l in _roots.SelectMany(r => r.Leaves).Where(l => l.Box.IsChecked == true))
            {
                switch (l.Kind)
                {
                    case "host": c.Hosts.Add(l.Id!); break;
                    case "cred": c.Credentials.Add(l.Id!); break;
                    case "snippet": c.Snippets.Add(l.Id!); break;
                    case "tunnel": c.Tunnels.Add(l.Id!); break;
                    case "serial": c.Serial.Add(l.Id!); break;
                    case "infra": c.Infra.Add(l.Id!); break;
                    case "portal": c.Portals.Add(l.Id!); break;
                    case "note": c.Notes.Add(l.Id!); break;
                    case "incident": c.Incidents.Add(l.Id!); break;
                    case "known": c.KnownHosts = true; break;
                    case "webpins": c.WebPins = true; break;
                    case "lanmon": c.LanMonitors = true; break;
                    case "settings": c.AppSettings = true; break;
                    case "reports": c.Reports = true; break;
                    case { } k when k.StartsWith("acct:"): c.Accounts[k[5..]].Add(l.Id!); break;
                }
            }
            return c;
        }
    }

    /// <summary>The summary line as shown (the self-test checks it names counts, never a secret).</summary>
    public string SummaryText => _summary.Text;
}
