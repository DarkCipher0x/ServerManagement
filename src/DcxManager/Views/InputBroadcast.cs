using DcxManager.Services;

namespace DcxManager.Views;

/// <summary>
/// Simultaneous input (spec §65, ADR-019): while on, what is typed or pasted in any member terminal goes to every member.
/// It starts only after a confirmation listing the terminals (a set with a PRODUCTION host means typing PRODUCTION), every
/// member shows a strip with Stop, and it ends by itself when fewer than two members are left. Data-level, so the paste
/// guard runs once where the paste happened and nothing bypasses it.
/// </summary>
public static class InputBroadcast
{
    private static readonly List<TerminalView> _members = new();
    public static event Action? Changed;

    public static IReadOnlyList<TerminalView> Members => _members.ToList();
    public static bool Contains(TerminalView t) => _members.Contains(t);

    /// <summary>Asks, then starts. Returns false when refused or fewer than two terminals.</summary>
    public static bool Start(IEnumerable<TerminalView> views, string scope)
    {
        var list = views.Where(v => !v.IsDisposed).Distinct().ToList();
        if (list.Count < 2) { MainWindow.Instance?.Status("Broadcast input needs at least two terminals " + scope + "."); return false; }
        var hosts = list.Select(v => v.Host).OfType<HostEntry>().ToList();
        var others = list.Where(v => v.Host == null).Select(v => v.Target.Title).ToList();
        var message = $"Everything you type or paste in any of these {list.Count} terminals ({scope}) will be sent to all of them until you press Stop."
                      + (others.Count > 0 ? "\n\nNot a saved host: " + string.Join(", ", others) : "");
        bool ok = hosts.Count > 0
            ? Envs.ConfirmMany(hosts, "Broadcast input", message, "Start broadcasting input", danger: true)
            : Dialogs.Confirm("Broadcast input", message, "Start broadcasting input", danger: true);
        if (!ok) return false;
        Stop(null);
        _members.AddRange(list);
        AuditLog.Write("you", $"{list.Count} terminals", "broadcast input started", string.Join(", ", list.Select(v => v.Target.Title)));
        Raise();
        return true;
    }

    public static void Stop(string? why)
    {
        if (_members.Count == 0) return;
        var n = _members.Count;
        _members.Clear();
        if (why != null) AuditLog.Write("you", $"{n} terminals", "broadcast input stopped", why);
        Raise();
    }

    /// <summary>A member closed: one left means nothing to broadcast to.</summary>
    public static void Remove(TerminalView t)
    {
        _banners.Remove(t);
        if (!_members.Remove(t)) return;
        if (_members.Count < 2) Stop("only one terminal left");
        else Raise();
    }

    /// <summary>Input from <paramref name="from"/> goes to the other members (their servers echo it as usual).</summary>
    public static void Relay(TerminalView from, string data)
    {
        if (!_members.Contains(from)) return;
        foreach (var m in _members.ToList()) if (m != from) m.SendInput(data);
    }

    private static readonly HashSet<TerminalView> _banners = new();

    private static void Raise()
    {
        foreach (var m in _banners.Concat(_members).Distinct().ToList())
            m.ShowInputBroadcast(_members.Contains(m) ? _members.Count : 0);
        _banners.Clear();
        _banners.UnionWith(_members);
        try { Changed?.Invoke(); } catch (Exception ex) { App.Log("input broadcast changed", ex); }
    }
}
