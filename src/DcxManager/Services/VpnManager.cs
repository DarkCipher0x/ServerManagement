using DcxManager.Ssh;
using DcxManager.Vpn;

namespace DcxManager.Services;

/// <summary>
/// The VPN tunnels (0.3.0 §24): profiles in the encrypted store, secrets in the vault, up / down through the engine of
/// each kind, status, and "connect through": a saved host that names a profile has its tunnel brought up before the
/// SSH / RDP connection opens (and down again after the idle time, when set). Every up / down is audited; the endpoint
/// goes through offline mode like any destination.
/// </summary>
public static class VpnManager
{
    public static Dictionary<string, IVpnEngine> Engines { get; } = new(StringComparer.OrdinalIgnoreCase) { ["wireguard"] = new WireGuardEngine(), ["openvpn"] = new OpenVpnEngine() };
    private static readonly Dictionary<string, DateTime> _used = new();
    private static readonly HashSet<string> _up = new();
    private static IDisposable? _idle;

    /// <summary>Raised (UI thread) when a tunnel's state changes.</summary>
    public static event Action<VpnProfile>? Changed;

    public static bool IsUp(VpnProfile p) { lock (_up) return _up.Contains(p.Id); }

    public static void Start()
    {
        SshConnector.BeforeConnect = EnsureForHostAsync;
        foreach (var p in AppData.Current.VpnProfiles.Where(p => p.AutoStart)) _ = SafeUpAsync(p, "auto-start");
        _idle ??= Ui.Thread.Every(TimeSpan.FromMinutes(1), IdleTick);
    }

    public static void Stop() { _idle?.Dispose(); _idle = null; }

    private static async Task SafeUpAsync(VpnProfile p, string why)
    {
        try { await UpAsync(p, why); }
        catch (Exception ex) { Log.Write("VPN " + p.Name, ex); }
    }

    /// <summary>Imports a .conf / .ovpn: secrets go to the vault, the rest to the profile. Returns the profile (not yet saved).</summary>
    public static VpnProfile Import(string text, string fileName)
    {
        var im = VpnConfig.Import(text, fileName);
        var p = new VpnProfile { Name = im.SuggestedName, Kind = im.Kind, Config = im.Config, Routes = im.Routes, RouteAll = im.RouteAll, Dns = im.Dns };
        if (im.PrivateKey != null)
        {
            var c = new Credential { Name = $"VPN key: {p.Name}", Kind = CredentialKind.PrivateKey, Secret = im.PrivateKey, Notes = $"{p.KindTitle} private key of the tunnel “{p.Name}” (imported {DateTime.Now:yyyy-MM-dd})", KeyType = im.Kind == "wireguard" ? "wireguard" : "openvpn" };
            AppData.Current.Credentials.Add(c); p.KeyCredentialId = c.Id;
        }
        if (im.Psks != null)
        {
            var c = new Credential { Name = $"VPN pre-shared keys: {p.Name}", Kind = CredentialKind.Password, Secret = im.Psks, Notes = "WireGuard peer pre-shared keys (publickey psk per line)" };
            AppData.Current.Credentials.Add(c); p.PskCredentialId = c.Id;
        }
        return p;
    }

    /// <summary>The runnable configuration (secrets joined back in, in memory only).</summary>
    public static string Render(VpnProfile p, int managementPort = 0)
    {
        if (p.Kind == "wireguard")
        {
            var key = ExternalCredentials.Use(p.KeyCredentialId, "VPN " + p.Name)?.Secret ?? throw new InvalidOperationException("the tunnel's private key is missing from the vault");
            return VpnConfig.RenderWireGuard(p.Config, key, ExternalCredentials.Use(p.PskCredentialId, "VPN " + p.Name)?.Secret, p.Routes, p.RouteAll, p.Dns);
        }
        return VpnConfig.RenderOpenVpn(p.Config, ExternalCredentials.Use(p.KeyCredentialId, "VPN " + p.Name)?.Secret, p.Routes, p.RouteAll, p.Dns, managementPort);
    }

    private static string EndpointOf(VpnProfile p)
    {
        var m = System.Text.RegularExpressions.Regex.Match(p.Config, p.Kind == "wireguard" ? @"^\s*Endpoint\s*=\s*([^:\s]+|\[[^\]]+\])" : @"^\s*remote\s+(\S+)", System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Trim('[', ']') : "";
    }

    public static async Task UpAsync(VpnProfile p, string why = "you", CancellationToken ct = default)
    {
        if (IsUp(p)) return;
        if (!Engines.TryGetValue(p.Kind, out var e)) throw new InvalidOperationException("unknown VPN kind");
        if (e.Find(out var hint) == null) throw new InvalidOperationException(hint);
        if (Profiles.ReadOnly) throw new InvalidOperationException("This profile is read-only (viewer).");
        var endpoint = EndpointOf(p);
        if (endpoint.Length > 0) { Offline.EnforceStatic(endpoint, "a VPN tunnel"); await Offline.EnforceAsync(endpoint, "a VPN tunnel", ct); }
        int port = p.Kind == "openvpn" ? FreePort() : 0;
        // 0.6.17 (ADR-096): read first (kept in Bitwarden or Password Safe they come from there), so Render finds them in memory.
        var auth = await ExternalCredentials.UseAsync(p.AuthCredentialId, "VPN " + p.Name, ct);
        var key = await ExternalCredentials.UseAsync(p.KeyCredentialId, "VPN " + p.Name, ct);
        if (p.Kind == "wireguard") await ExternalCredentials.UseAsync(p.PskCredentialId, "VPN " + p.Name, ct);
        var rendered = Render(p, port);
        string? Secret(string what) => what switch { "auth-user" => auth?.Username, "auth-pass" => auth?.Secret, "key-passphrase" => key?.Passphrase, _ => null };
        p.LastState = "starting…"; Raise(p);
        try { await e.UpAsync(p, rendered, Secret, ct); }
        catch (Exception ex) { p.LastState = "✕ " + ex.Message.Split('\n')[0]; Raise(p); AuditLog.Write("you", "vpn", "tunnel failed to start", $"{p.Name}: {ex.Message.Split('\n')[0]}"); throw; }
        lock (_up) _up.Add(p.Id);
        p.LastUp = DateTime.Now; p.LastState = "up";
        try { AppData.Save(); } catch (Exception ex) { Log.Write("vpn save", ex); }
        AuditLog.Write("you", "vpn", "tunnel up", $"{p.Name} ({p.KindTitle}) · routes: {p.RoutesText} · {why}");
        Raise(p);
    }

    public static async Task DownAsync(VpnProfile p, string why = "you", CancellationToken ct = default)
    {
        if (!Engines.TryGetValue(p.Kind, out var e)) return;
        try { await e.DownAsync(p, ct); }
        finally
        {
            lock (_up) _up.Remove(p.Id);
            p.LastState = "down";
            AuditLog.Write("you", "vpn", "tunnel down", $"{p.Name} · {why}");
            Raise(p);
        }
    }

    public static Task<VpnStatus> StatusAsync(VpnProfile p, CancellationToken ct = default) =>
        Engines.TryGetValue(p.Kind, out var e) ? e.StatusAsync(p, ct) : Task.FromResult(new VpnStatus(false, "unknown kind", "", null, 0, 0, ""));

    /// <summary>The routes installed through the tunnel's interface right now (read back from Windows).</summary>
    public static List<VpnRoutes.Route> InstalledRoutes(VpnProfile p) =>
        Engines.TryGetValue(p.Kind, out var e) && VpnRoutes.InterfaceIndex(e.InterfaceName(p)) is { } idx ? VpnRoutes.Via(idx) : new();

    /// <summary>"Connect through": called before every SSH connection (SshConnector.BeforeConnect) and RDP launch.</summary>
    public static async Task EnsureForHostAsync(HostEntry host, CancellationToken ct)
    {
        if (host.VpnProfileId is not { Length: > 0 } id) return;
        // Fail closed: a host that names a tunnel which no longer exists must not quietly connect outside it.
        if (AppData.Current.VpnProfiles.FirstOrDefault(p => p.Id == id) is not { } p) throw new InvalidOperationException($"{host.Display} connects through a VPN profile that no longer exists — pick another in the host's settings, or clear it");
        lock (_used) _used[p.Id] = DateTime.Now;
        if (IsUp(p)) return;
        await UpAsync(p, "needed by " + host.Display, ct);
    }

    private static void IdleTick()
    {
        foreach (var p in AppData.Current.VpnProfiles.Where(p => p.IdleMinutes > 0 && IsUp(p) && !p.AutoStart).ToList())
        {
            DateTime last; lock (_used) last = _used.GetValueOrDefault(p.Id, p.LastUp ?? DateTime.Now);
            if (DateTime.Now - last >= TimeSpan.FromMinutes(p.IdleMinutes)) _ = DownAsync(p, $"idle for {p.IdleMinutes} min");
        }
    }

    public static void StopAll() { foreach (var p in AppData.Current.VpnProfiles.Where(IsUp).ToList()) { try { DownAsync(p, "app closing").GetAwaiter().GetResult(); } catch (Exception ex) { Log.Write("VPN down", ex); } } }

    private static int FreePort() { var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); l.Start(); var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return port; }
    private static void Raise(VpnProfile p) { if (Ui.Thread.HasUi) Ui.Thread.Post(() => Changed?.Invoke(p)); }
}
