using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace DcxManager.Services;

/// <summary>A destination refused by offline mode. The message says what, where, why and how to approve it.</summary>
public sealed class OfflineBlockedException(string host, string purpose, string reason)
    : InvalidOperationException($"Offline mode: {purpose} to {host} is blocked — {reason}. Approve it in Settings → Offline mode, or turn offline mode off.")
{
    public string Host { get; } = host;
    public string Purpose { get; } = purpose;
}

/// <summary>
/// Offline / air-gapped mode (spec §53, ADR-016). One rule, enforced at each transport before a connection is opened:
/// while on, only this PC, private / LAN addresses, approved destinations, and DNS names whose every address is private
/// are allowed. Everything else fails closed with <see cref="OfflineBlockedException"/>, is audited and listed.
/// <list type="bullet">
/// <item>HTTP: <see cref="Handler"/> (request URI check + a connect callback that connects to the address it checked).</item>
/// <item>SSH / RDP / external terminal / web windows / browser links / add-ons: <see cref="EnforceAsync"/> or <see cref="Run"/>.</item>
/// </list>
/// </summary>
public static class Offline
{
    public sealed record BlockedAttempt(DateTime Time, string Purpose, string Host, string Reason);

    /// <summary>The switch, the approved list or the options changed.</summary>
    public static event Action? Changed;
    /// <summary>Something was blocked (the Settings list and the badge count listen).</summary>
    public static event Action? BlockedChanged;

    public static bool On => AppSettings.Current.OfflineMode;

    /// <summary>Name resolution (replaceable by the self-test, which must not depend on real DNS).</summary>
    public static Func<string, CancellationToken, Task<IPAddress[]>> Resolver = (h, ct) => Dns.GetHostAddressesAsync(h, ct);

    /// <summary>Connections opened through <see cref="Handler"/> (self-test: a blocked request must open none).</summary>
    public static int Connects => Volatile.Read(ref _connects);
    private static int _connects;

    private static readonly object Lock = new();
    private static readonly LinkedList<BlockedAttempt> _blocked = new();
    private static readonly Dictionary<string, DateTime> _resolvedPrivate = new(StringComparer.OrdinalIgnoreCase);
    private static int _blockedTotal;
    public static int BlockedTotal => Volatile.Read(ref _blockedTotal);

    /// <summary>Newest first, at most 50 (this session only).</summary>
    public static List<BlockedAttempt> Blocked { get { lock (Lock) return _blocked.ToList(); } }

    // ---- settings ----------------------------------------------------------------------------------------------------

    public static void Set(bool on)
    {
        var s = AppSettings.Current;
        if (s.OfflineMode == on) return;
        s.OfflineMode = on;
        lock (Lock) _resolvedPrivate.Clear();
        try { AppSettings.Save(); } catch (Exception ex) { Log.Write("offline save", ex); }
        try { AuditLog.Write("you", "offline mode", on ? "turned on" : "turned off"); } catch { }
        Changed?.Invoke();
    }

    /// <summary>The settings changed underneath (an admin-approved protected change, 0.3.0 §1): forget cached private
    /// names and tell the screens.</summary>
    public static void Reapply()
    {
        lock (Lock) _resolvedPrivate.Clear();
        Changed?.Invoke();
    }

    /// <summary>Validates and saves the approved list. Returns an error (nothing saved) or null.</summary>
    public static string? SetAllowed(IEnumerable<string> entries, bool? resolvePrivateNames = null, bool? allowAddons = null)
    {
        var list = entries.Select(e => e.Trim()).Where(e => e.Length > 0 && !e.StartsWith('#')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count > 200) return "At most 200 approved destinations.";
        foreach (var e in list) if (Validate(e) is { } bad) return $"“{e}”: {bad}";
        var s = AppSettings.Current;
        s.OfflineAllowed = list;
        if (resolvePrivateNames != null) s.OfflineResolvePrivateNames = resolvePrivateNames.Value;
        if (allowAddons != null) s.OfflineAllowAddons = allowAddons.Value;
        lock (Lock) _resolvedPrivate.Clear();
        try { AppSettings.Save(); } catch (Exception ex) { Log.Write("offline save", ex); }
        Changed?.Invoke();
        return null;
    }

    // ---- approved-destination rules ----------------------------------------------------------------------------------

    private static readonly Regex HostName = new(@"^(?=.{1,253}$)[a-z0-9_]([a-z0-9_-]{0,61}[a-z0-9_])?(\.[a-z0-9_]([a-z0-9_-]{0,61}[a-z0-9_])?)*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>null = valid: a host name, *.suffix, an IP address, or an IPv4/IPv6 CIDR.</summary>
    public static string? Validate(string entry)
    {
        var e = entry.Trim();
        if (e.Contains('/'))
        {
            var parts = e.Split('/');
            if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var net) || !int.TryParse(parts[1], out var bits)) return "not a valid CIDR range (e.g. 203.0.113.0/24)";
            int max = net.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
            return bits < 0 || bits > max ? $"the prefix must be 0–{max}" : bits == 0 ? "a /0 range would approve everything" : null;
        }
        if (IPAddress.TryParse(e.Trim('[', ']'), out _)) return null;
        var name = e.StartsWith("*.") ? e[2..] : e.StartsWith('.') ? e[1..] : e;
        if (name.Contains('*')) return "only a leading *. wildcard is allowed";
        return HostName.IsMatch(name.TrimEnd('.')) ? null : "not a valid host name, *.suffix, IP or CIDR";
    }

    private static string Norm(string host) => host.Trim().Trim('[', ']').TrimEnd('.').ToLowerInvariant();

    private static bool NameApproved(string host)
    {
        foreach (var raw in AppSettings.Current.OfflineAllowed)
        {
            var e = raw.Trim().ToLowerInvariant();
            if (e.Contains('/') || IPAddress.TryParse(e.Trim('[', ']'), out _)) continue;
            if (e.StartsWith("*.") || e.StartsWith('.'))
            {
                var suffix = "." + e.TrimStart('*', '.');
                if (host.EndsWith(suffix, StringComparison.Ordinal)) return true;
            }
            else if (host == e.TrimEnd('.')) return true;
        }
        return false;
    }

    private static bool IpApproved(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        foreach (var raw in AppSettings.Current.OfflineAllowed)
        {
            var e = raw.Trim();
            if (e.Contains('/'))
            {
                var p = e.Split('/');
                if (p.Length != 2 || !IPAddress.TryParse(p[0], out var net) || !int.TryParse(p[1], out var bits)) continue;
                if (net.IsIPv4MappedToIPv6) net = net.MapToIPv4();
                if (InRange(ip, net, bits)) return true;
            }
            else if (IPAddress.TryParse(e.Trim('[', ']'), out var one))
            {
                if (one.IsIPv4MappedToIPv6) one = one.MapToIPv4();
                if (one.Equals(ip)) return true;
            }
        }
        return false;
    }

    private static bool InRange(IPAddress ip, IPAddress net, int bits)
    {
        if (ip.AddressFamily != net.AddressFamily) return false;
        var a = ip.GetAddressBytes(); var b = net.GetAddressBytes();
        for (int i = 0; i < a.Length && bits > 0; i++, bits -= 8)
        {
            int mask = bits >= 8 ? 0xFF : (0xFF << (8 - bits)) & 0xFF;
            if ((a[i] & mask) != (b[i] & mask)) return false;
        }
        return true;
    }

    private static bool PrivateAddress(IPAddress ip) => NetworkPolicy.ClassifyHost(ip.ToString()) is NetClass.Local or NetClass.OnPremises;

    // ---- decisions ---------------------------------------------------------------------------------------------------

    /// <summary>The rule without DNS: true / false, or null when only resolving the name can decide.</summary>
    public static (bool? Allowed, string Reason) Quick(string host)
    {
        if (!On) return (true, "offline mode is off");
        var h = Norm(host);
        if (h.Length == 0) return (false, "no destination");
        if (IPAddress.TryParse(h, out var ip))
            return IpApproved(ip) ? (true, "approved") : PrivateAddress(ip) ? (true, "this PC / your network") : (false, "a public address");
        if (NameApproved(h)) return (true, "approved");
        return NetworkPolicy.ClassifyHost(h) switch
        {
            NetClass.Local or NetClass.OnPremises => (true, "this PC / your network"),
            NetClass.External => (false, "an internet service"),
            _ => AppSettings.Current.OfflineResolvePrivateNames ? (null, "") : (false, "a DNS name that isn't approved (resolving names is off)"),
        };
    }

    /// <summary>The full rule: resolves a name the static rule can't place. Fails closed.</summary>
    public static async Task<(bool Allowed, string Reason, IPAddress[] Addresses)> DecideAsync(string host, CancellationToken ct = default)
    {
        var (q, why) = Quick(host);
        if (q != null) return (q.Value, why, Array.Empty<IPAddress>());
        var h = Norm(host);
        IPAddress[] addrs;
        try { addrs = await Resolver(h, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return (false, "its name could not be resolved (" + ex.Message + ")", Array.Empty<IPAddress>()); }
        if (addrs.Length == 0) return (false, "its name has no addresses", addrs);
        foreach (var a in addrs)
            if (!PrivateAddress(a) && !IpApproved(a)) return (false, $"it resolves to {a}, outside your network", addrs);
        lock (Lock) _resolvedPrivate[h] = DateTime.UtcNow.AddMinutes(10);
        return (true, "resolves to private addresses only", addrs);
    }

    /// <summary>For checks that can't wait for DNS (web-window sub-requests): the static rule plus names recently resolved to
    /// private addresses. Anything still unknown is blocked.</summary>
    public static bool QuickAllowed(string host)
    {
        var (q, _) = Quick(host);
        if (q != null) return q.Value;
        lock (Lock) return _resolvedPrivate.TryGetValue(Norm(host), out var until) && until > DateTime.UtcNow;
    }

    public static string HostOf(string hostOrUrl) =>
        hostOrUrl.Contains("://") && Uri.TryCreate(hostOrUrl.Trim(), UriKind.Absolute, out var u) && u.Host.Length > 0 ? u.Host : hostOrUrl.Trim();

    /// <summary>Throws <see cref="OfflineBlockedException"/> when offline mode refuses the destination.</summary>
    public static async Task EnforceAsync(string hostOrUrl, string purpose, CancellationToken ct = default)
    {
        if (!On) return;
        var host = HostOf(hostOrUrl);
        var d = await DecideAsync(host, ct);
        if (!d.Allowed) throw Block(host, purpose, d.Reason);
    }

    /// <summary>The rule without DNS, for a target reached through a jump host (its name may only resolve over there):
    /// public addresses and internet services are refused, unknown names allowed.</summary>
    public static void EnforceStatic(string hostOrUrl, string purpose)
    {
        if (!On) return;
        var host = HostOf(hostOrUrl);
        var (q, why) = Quick(host);
        if (q == false) throw Block(host, purpose, why);
    }

    /// <summary>Records and audits a refusal and returns the exception to throw.</summary>
    public static OfflineBlockedException Block(string host, string purpose, string reason)
    {
        lock (Lock)
        {
            _blocked.AddFirst(new BlockedAttempt(DateTime.Now, purpose, host, reason));
            while (_blocked.Count > 50) _blocked.RemoveLast();
        }
        Interlocked.Increment(ref _blockedTotal);
        try { AuditLog.Write("app", "offline mode", "blocked " + purpose, host + " — " + reason); } catch { }
        try { BlockedChanged?.Invoke(); } catch (Exception ex) { Log.Write("offline blocked event", ex); }
        return new OfflineBlockedException(host, purpose, reason);
    }

    /// <summary>UI helper: runs <paramref name="allowed"/> when the destination is allowed, otherwise tells the user why.</summary>
    public static async void Run(string hostOrUrl, string purpose, Action allowed)
    {
        try { await EnforceAsync(hostOrUrl, purpose); }
        catch (OfflineBlockedException ex) { Tell(ex); return; }
        catch (Exception ex) { Log.Write("offline check", ex); return; }
        allowed();
    }

    /// <summary>Shows a refusal (status line only during the self-test, which never opens dialogs).</summary>
    public static void Tell(OfflineBlockedException ex)
    {
        if (TestMode.Running) { Ui.Window.Status("⛔ " + ex.Message); return; }
        Ui.Dialogs.Error("Offline mode", ex.Message, explain: false);
    }

    /// <summary>Why add-ons can't run now (null = they can).</summary>
    public static string? AddonsBlocked() => On && !AppSettings.Current.OfflineAllowAddons
        ? "Offline mode is on. Add-ons are separate programs that the sandbox can't keep off the network, so they don't run while offline unless you allow them (Settings → Offline mode)."
        : null;

    // ---- HTTP --------------------------------------------------------------------------------------------------------

    /// <summary>The handler every app HttpClient is built on: checks the request URI (covers proxies), and connects only to
    /// addresses it checked (covers redirects and DNS rebinding). Auto-redirects are off.</summary>
    public static HttpMessageHandler Handler(SocketsHttpHandler? inner = null)
    {
        inner ??= new SocketsHttpHandler();
        inner.AllowAutoRedirect = false;
        inner.ConnectCallback = ConnectAsync;
        return new Guard(inner);
    }

    private sealed class Guard(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (On && request.RequestUri is { } u) await EnforceAsync(u.Host, "a web request (" + u.GetLeftPart(UriPartial.Authority) + ")", ct);
            return await base.SendAsync(request, ct);
        }
    }

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext ctx, CancellationToken ct)
    {
        Interlocked.Increment(ref _connects);
        var ep = ctx.DnsEndPoint;
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            if (!On) await socket.ConnectAsync(ep, ct);
            else
            {
                var d = await DecideAsync(ep.Host, ct);
                if (!d.Allowed) throw Block(ep.Host, "a connection", d.Reason);
                // A resolved name connects to exactly the addresses that were checked (no second lookup to rebind).
                if (d.Addresses.Length > 0) await socket.ConnectAsync(d.Addresses, ep.Port, ct);
                else await socket.ConnectAsync(ep, ct);
            }
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch { socket.Dispose(); throw; }
    }
}
