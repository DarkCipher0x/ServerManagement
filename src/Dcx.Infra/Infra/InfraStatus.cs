using DcxManager.Services;

namespace DcxManager.Infra;

/// <summary>A provider's last-known state: what was detected, how many objects and when, and which screen asked.</summary>
public sealed record InfraSnapshot(string ProviderId, ProviderCaps Caps, int Objects, int Running, int Stopped, DateTime At, string By)
{
    public string Text => !Caps.Reachable ? "✕ " + Caps.Detail
        : !Caps.CanRead ? "◇ reachable, no read access"
        : $"● {Objects} object(s) · {Running} running" + (Stopped > 0 ? $" · {Stopped} stopped" : "") + (Caps.Version.Length > 0 ? " · " + Caps.Version : "");
}

/// <summary>
/// Last-known infrastructure state shared by the Infrastructure screen and the dashboard (ADR-018). Memory only: after a
/// restart nothing is known until something is checked again, so stale state never looks current. Checking is always an
/// explicit call — this class never contacts anything by itself.
/// </summary>
public static class InfraStatus
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, InfraSnapshot> Last = new();

    /// <summary>Raised (on any thread) after a provider's state was recorded or forgotten.</summary>
    public static event Action? Changed;

    /// <summary>Test hook: how providers are created for <see cref="CheckAsync"/>.</summary>
    internal static Func<ProviderConfig, IInfraProvider> Factory = Infra.Create;

    public static InfraSnapshot? For(string providerId) { lock (Gate) return Last.TryGetValue(providerId, out var s) ? s : null; }

    public static void Record(ProviderConfig c, ProviderCaps caps, IReadOnlyList<InfraObject> items, string by)
    {
        int running = items.Count(o => o.Status is "running" or "online" or "active" or "available");
        int stopped = items.Count(o => o.Status is "stopped" or "exited" or "offline");
        lock (Gate) Last[c.Id] = new InfraSnapshot(c.Id, caps, items.Count, running, stopped, DateTime.Now, by);
        Raise();
    }

    public static void Forget(string providerId) { lock (Gate) if (!Last.Remove(providerId)) return; Raise(); }

    internal static void Reset() { lock (Gate) Last.Clear(); }

    /// <summary>Detects and reads one provider (offline mode applies inside the provider's own transport) and records it.
    /// Failures are recorded as an unreachable state, never thrown.</summary>
    public static async Task<InfraSnapshot> CheckAsync(ProviderConfig c, string by, CancellationToken ct = default)
    {
        ProviderCaps caps;
        IReadOnlyList<InfraObject> items = Array.Empty<InfraObject>();
        try
        {
            var p = Factory(c);
            caps = await p.DetectAsync(ct);
            if (caps.Reachable && caps.CanRead)
            {
                try { items = await p.InventoryAsync(ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { caps = caps with { Notes = caps.Notes.Append("Inventory failed: " + ex.Message.Split('\n')[0]).ToList() }; }
            }
        }
        catch (OperationCanceledException) { caps = ProviderCaps.Unreachable("timed out"); }
        catch (OfflineBlockedException ex) { caps = ProviderCaps.Unreachable(ex.Message); }
        catch (Exception ex) { caps = ProviderCaps.Unreachable(ex.Message.Split('\n')[0]); Log.Write("Infra check", ex); }
        Record(c, caps, items, by);
        return For(c.Id)!;
    }

    private static void Raise() { try { Changed?.Invoke(); } catch (Exception ex) { Log.Write("infra status changed", ex); } }
}
