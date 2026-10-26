namespace DcxManager.Services;

/// <summary>
/// Find-by-id in a list in O(1) (0.6.29 performance pass: FindHost / FindCredential were linear, from 115 call sites, on
/// 10,000 hosts). The lists stay plain lists that any code may change, so the index never trusts itself: it maps an id to
/// a position and a hit counts only when <c>list[i]</c> still has that id. After any change that moved or removed it, the
/// check fails and one scan finds it (or not) and rebuilds the index. Each index is an immutable snapshot swapped in whole,
/// so a reader on another thread never sees one half-built.
/// </summary>
public sealed class IdIndex<T>(Func<T, string> idOf) where T : class
{
    private sealed record Snapshot(List<T> List, Dictionary<string, int> Index);
    private Snapshot? _snap;

    public T? Find(List<T> list, string id)
    {
        var snap = _snap;
        if (snap != null && ReferenceEquals(snap.List, list) && snap.Index.TryGetValue(id, out var i)
            && (uint)i < (uint)list.Count && list[i] is { } hit && idOf(hit) == id)
            return hit;
        // Missed or moved: one scan, and an index that matches the list as it is now (only when the id is there, so
        // looking up ids that don't exist doesn't rebuild every time).
        T? found = null;
        foreach (var x in list) if (idOf(x) == id) { found = x; break; }
        if (found != null) Rebuild(list);
        return found;
    }

    private void Rebuild(List<T> list)
    {
        var index = new Dictionary<string, int>(list.Count, StringComparer.Ordinal);
        for (int i = 0; i < list.Count; i++) if (idOf(list[i]) is { } id) index.TryAdd(id, i);   // the first, as FirstOrDefault
        _snap = new Snapshot(list, index);
    }
}
