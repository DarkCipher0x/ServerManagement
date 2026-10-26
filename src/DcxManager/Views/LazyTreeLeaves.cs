using System.Windows;
using System.Windows.Controls;
using DcxManager.Services;

namespace DcxManager.Views;

/// <summary>
/// Host nodes made when their folder opens (0.6.29 performance pass). 10,000 hosts used to be 10,000 tree nodes on every
/// rebuild, most of them inside closed folders. A closed node keeps its hosts as a list plus one placeholder child (so it
/// can still be expanded); opening it makes the nodes. Used by the Hosts page's tree and the workspace host pane.
/// </summary>
internal sealed class LazyTreeLeaves(Func<HostEntry, TreeViewItem> make)
{
    private readonly Dictionary<TreeViewItem, List<HostEntry>> _pending = new();
    private static readonly object PendingTag = new();

    /// <summary>The placeholder under a closed folder (never selectable).</summary>
    public static bool IsPlaceholder(object? item) => item is TreeViewItem { Tag: var t } && ReferenceEquals(t, PendingTag);

    public void Clear() => _pending.Clear();

    /// <summary>Adds <paramref name="hosts"/> under <paramref name="node"/>: now when it is open (or <paramref name="now"/>),
    /// else when it is first expanded.</summary>
    public void Add(TreeViewItem node, IReadOnlyCollection<HostEntry> hosts, bool now)
    {
        if (hosts.Count == 0) return;
        if (now || node.IsExpanded) { foreach (var h in hosts) node.Items.Add(make(h)); return; }
        if (_pending.TryGetValue(node, out var list)) { list.AddRange(hosts); return; }
        _pending[node] = hosts.ToList();
        if (node.Items.Count == 0) node.Items.Add(new TreeViewItem { Tag = PendingTag, Header = "…", IsEnabled = false });
        // Handled events too: the folder node's own Expanded handler marks the event handled.
        node.AddHandler(TreeViewItem.ExpandedEvent, new RoutedEventHandler((_, e) => { if (ReferenceEquals(e.OriginalSource, node)) Make(node); }), true);
    }

    public void Make(TreeViewItem node)
    {
        if (!_pending.Remove(node, out var list)) return;
        foreach (var p in node.Items.OfType<TreeViewItem>().Where(IsPlaceholder).ToList()) node.Items.Remove(p);
        foreach (var h in list) node.Items.Add(make(h));
    }

    public void MakeAll() { foreach (var n in _pending.Keys.ToList()) Make(n); }

    /// <summary>The first node of host <paramref name="id"/>, made first if its folder hasn't been opened yet.</summary>
    public TreeViewItem? Find(ItemCollection roots, string id)
    {
        foreach (var node in _pending.Where(kv => kv.Value.Any(h => h.Id == id)).Select(kv => kv.Key).ToList()) Make(node);
        return FindMade(roots, id);
    }

    private static TreeViewItem? FindMade(ItemCollection items, string id)
    {
        foreach (var o in items)
            if (o is TreeViewItem i)
            {
                if (i.Tag is HostEntry h && h.Id == id) return i;
                if (FindMade(i.Items, id) is { } found) return found;
            }
        return null;
    }
}
