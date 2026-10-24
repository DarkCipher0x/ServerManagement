using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using DcxManager.Net;

namespace DcxManager.Vpn;

/// <summary>
/// Reads and writes WireGuard (.conf) and OpenVPN (.ovpn) configurations (0.3.0 §24): import splits the secrets out (so
/// they go to the vault) and refuses directives that would run programs; render joins the secrets back in memory and
/// applies the app's route choice. Everything the user gave is validated before it reaches an engine.
/// </summary>
public static class VpnConfig
{
    /// <summary>What an import found: the config without secrets, the secrets, the routes it carried, and warnings.</summary>
    public sealed record Imported(string Kind, string Config, string? PrivateKey, string? Psks, bool NeedsAuth, List<string> Routes, bool RouteAll, List<string> Dns, List<string> Warnings, string SuggestedName);

    private static readonly Regex KeyRx = new(@"^[A-Za-z0-9+/]{42}[AEIMQUYcgkosw048]=$");   // a 32-byte base64 key
    private static readonly Regex OvpnForbidden = new(@"^\s*(script-security|up|down|up-pre|route-up|route-pre-down|ipchange|tls-verify|auth-user-pass-verify|client-connect|client-disconnect|learn-address|plugin|management\S*|daemon|inetd|log|log-append|writepid|status|dev-node|windows-driver)\b", RegexOptions.Multiline | RegexOptions.IgnoreCase);

    public static string DetectKind(string text) => Regex.IsMatch(text, @"^\s*\[Interface\]", RegexOptions.Multiline | RegexOptions.IgnoreCase) ? "wireguard" : "openvpn";

    public static Imported Import(string text, string fileName = "")
    {
        if (text.Length > 256 * 1024) throw new ArgumentException("the configuration is larger than 256 KB");
        text = text.Replace("\r\n", "\n");
        return DetectKind(text) == "wireguard" ? ImportWireGuard(text, fileName) : ImportOpenVpn(text, fileName);
    }

    // ---- WireGuard ----------------------------------------------------------------------------------------------------------

    private static Imported ImportWireGuard(string text, string fileName)
    {
        var warnings = new List<string>();
        string? priv = null; var psks = new StringBuilder(); var kept = new StringBuilder();
        var routes = new List<string>(); bool all = false; var dns = new List<string>();
        string section = ""; string? peerPub = null; string? pendingPsk = null;
        void FlushPeer() { if (peerPub != null && pendingPsk != null) psks.Append(peerPub).Append(' ').Append(pendingPsk).Append('\n'); peerPub = null; pendingPsk = null; }   // base64 keys end in '=': a space separates them
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';')) { kept.Append(raw).Append('\n'); continue; }
            if (line.StartsWith('['))
            {
                FlushPeer();
                section = line.ToLowerInvariant();
                if (section is not ("[interface]" or "[peer]")) throw new ArgumentException($"unknown section {line}");
                kept.Append(line).Append('\n'); continue;
            }
            var eq = line.IndexOf('=');
            if (eq <= 0) throw new ArgumentException($"not a key = value line: {Short(line)}");
            var key = line[..eq].Trim(); var value = line[(eq + 1)..].Trim();
            switch (key.ToLowerInvariant())
            {
                case "privatekey": if (!KeyRx.IsMatch(value)) throw new ArgumentException("the PrivateKey isn't a WireGuard key"); priv = value; continue;
                case "presharedkey": if (!KeyRx.IsMatch(value)) throw new ArgumentException("a PresharedKey isn't a WireGuard key"); pendingPsk = value; continue;
                case "publickey": if (!KeyRx.IsMatch(value)) throw new ArgumentException("a PublicKey isn't a WireGuard key"); peerPub = value; break;
                case "preup" or "postup" or "predown" or "postdown": warnings.Add($"{key} removed: the app never runs commands from a configuration"); continue;
                case "allowedips":
                    foreach (var r in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (!VpnRoutes.TryParseCidr(r, out _, out _)) throw new ArgumentException($"AllowedIPs: {r} isn't an address/prefix");
                        if (r is "0.0.0.0/0" or "::/0") all = true; else routes.Add(r);
                    }
                    continue;   // AllowedIPs are rendered from the app's route choice
                case "dns": dns.AddRange(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)); continue;
                case "endpoint":
                    var h = value.LastIndexOf(':') is var i && i > 0 ? value[..i].Trim('[', ']') : value;
                    if (!NetTests.ValidHost(h)) throw new ArgumentException("Endpoint isn't a host:port");
                    break;
                case "address" or "listenport" or "mtu" or "persistentkeepalive" or "table" or "fwmark": break;
                default: warnings.Add($"{key} ignored (not a WireGuard setting the app passes on)"); continue;
            }
            kept.Append(key).Append(" = ").Append(value).Append('\n');
        }
        FlushPeer();
        if (priv == null) throw new ArgumentException("no PrivateKey in the [Interface] section");
        if (!kept.ToString().Contains("[Peer]", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("no [Peer] section");
        foreach (var d in dns) if (!IPAddress.TryParse(d, out _) && !NetTests.ValidHost(d)) throw new ArgumentException($"DNS: {d}");
        return new Imported("wireguard", kept.ToString(), priv, psks.Length > 0 ? psks.ToString() : null, false, routes, all, dns, warnings, Name(fileName, "WireGuard"));
    }

    /// <summary>The runnable WireGuard config: secrets back in, AllowedIPs from the route choice, DNS applied.</summary>
    public static string RenderWireGuard(string config, string privateKey, string? psks, IReadOnlyList<string> routes, bool routeAll, IReadOnlyList<string> dns)
    {
        if (!KeyRx.IsMatch(privateKey)) throw new ArgumentException("private key");
        var pskMap = (psks ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split(' ', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);
        var allowed = routeAll ? "0.0.0.0/0, ::/0" : routes.Count > 0 ? string.Join(", ", routes) : null;
        var sb = new StringBuilder();
        string section = ""; string? peerPub = null;
        var lines = config.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i <= lines.Length; i++)
        {
            var line = i < lines.Length ? lines[i].Trim() : "[end]";
            if (line.StartsWith('['))
            {
                // closing the previous section: add what it needs
                if (section == "[interface]") { sb.Append("PrivateKey = ").Append(privateKey).Append('\n'); if (dns.Count > 0) sb.Append("DNS = ").Append(string.Join(", ", dns)).Append('\n'); }
                if (section == "[peer]")
                {
                    if (peerPub != null && pskMap.TryGetValue(peerPub, out var psk)) sb.Append("PresharedKey = ").Append(psk).Append('\n');
                    sb.Append("AllowedIPs = ").Append(allowed ?? TunnelNetworkOf(config)).Append('\n');
                }
                if (line == "[end]") break;
                section = line.ToLowerInvariant(); peerPub = null;
                sb.Append(line).Append('\n'); continue;
            }
            if (line.StartsWith("PublicKey", StringComparison.OrdinalIgnoreCase)) peerPub = line[(line.IndexOf('=') + 1)..].Trim();
            if (line.Length > 0) sb.Append(line).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Without routes, only the tunnel's own network is allowed: the interface Address with its prefix.</summary>
    private static string TunnelNetworkOf(string config)
    {
        var m = Regex.Match(config, @"^\s*Address\s*=\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        if (!m.Success) return "0.0.0.0/32";
        var nets = new List<string>();
        foreach (var a in m.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (VpnRoutes.TryParseCidr(a, out var ip, out var bits)) nets.Add(VpnRoutes.Network(ip, bits) + "/" + bits);
        return nets.Count > 0 ? string.Join(", ", nets) : "0.0.0.0/32";
    }

    // ---- OpenVPN -----------------------------------------------------------------------------------------------------------

    private static Imported ImportOpenVpn(string text, string fileName)
    {
        var warnings = new List<string>();
        if (OvpnForbidden.Match(text) is { Success: true } bad) throw new ArgumentException($"the configuration contains “{bad.Value.Trim()}”, which runs programs or changes logging — the app refuses it");
        string? key = null;
        var cfg = Regex.Replace(text, @"<key>\s*(.*?)\s*</key>\n?", m => { key = m.Groups[1].Value.Trim(); return ""; }, RegexOptions.Singleline);
        if (Regex.IsMatch(cfg, @"^\s*key\s+\S", RegexOptions.Multiline)) throw new ArgumentException("“key <file>” points at a file: inline the key (<key>…</key>) and import again");
        if (Regex.IsMatch(cfg, @"^\s*(ca|cert|tls-auth|tls-crypt|pkcs12|dh|secret)\s+\S", RegexOptions.Multiline)) throw new ArgumentException("the configuration points at separate files: inline them (<ca>, <cert>, <tls-auth>…) and import again");
        bool needsAuth = Regex.IsMatch(cfg, @"^\s*auth-user-pass\b", RegexOptions.Multiline);
        cfg = Regex.Replace(cfg, @"^\s*auth-user-pass\b.*$", "auth-user-pass", RegexOptions.Multiline);   // never a file argument
        var routes = new List<string>(); bool all = false; var dns = new List<string>();
        var kept = new StringBuilder();
        foreach (var raw in cfg.Split('\n'))
        {
            var line = raw.Trim();
            if (Regex.Match(line, @"^route\s+(\S+)\s+(\S+)") is { Success: true } r)
            {
                if (IPAddress.TryParse(r.Groups[1].Value, out var net) && IPAddress.TryParse(r.Groups[2].Value, out var mask)) { routes.Add(net + "/" + VpnRoutes.MaskBits(mask)); continue; }
                throw new ArgumentException("route: " + Short(line));
            }
            if (line.StartsWith("redirect-gateway", StringComparison.OrdinalIgnoreCase)) { all = true; continue; }
            if (Regex.Match(line, @"^dhcp-option\s+DNS\s+(\S+)", RegexOptions.IgnoreCase) is { Success: true } d) { dns.Add(d.Groups[1].Value); continue; }
            if (Regex.Match(line, @"^remote\s+(\S+)", RegexOptions.IgnoreCase) is { Success: true } rem && !NetTests.ValidHost(rem.Groups[1].Value)) throw new ArgumentException("remote isn't a host name or address");
            if (line.StartsWith("route-nopull", StringComparison.OrdinalIgnoreCase)) continue;   // rendered from the app's choice
            kept.Append(raw).Append('\n');
        }
        if (!Regex.IsMatch(kept.ToString(), @"^\s*remote\s+\S", RegexOptions.Multiline)) throw new ArgumentException("no remote in the configuration");
        if (key != null && !key.Contains("PRIVATE KEY")) throw new ArgumentException("the <key> block isn't a PEM private key");
        return new Imported("openvpn", kept.ToString(), key, null, needsAuth, routes, all, dns, warnings, Name(fileName, "OpenVPN"));
    }

    /// <summary>The runnable OpenVPN config: the key back in, the route choice (split tunnel pulls no routes from the
    /// server), DNS, and the management settings the app needs. Secrets for auth-user-pass come over the management
    /// channel, never from a file.</summary>
    public static string RenderOpenVpn(string config, string? privateKey, IReadOnlyList<string> routes, bool routeAll, IReadOnlyList<string> dns, int managementPort, string? managementPasswordFile = null)
    {
        var sb = new StringBuilder(config.Replace("\r\n", "\n").TrimEnd()).Append('\n');
        if (privateKey != null) sb.Append("<key>\n").Append(privateKey.Trim()).Append("\n</key>\n");
        if (routeAll) sb.Append("redirect-gateway def1\n");
        else
        {
            sb.Append("route-nopull\n");
            foreach (var r in routes)
            {
                if (!VpnRoutes.TryParseCidr(r, out var ip, out var bits) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) throw new ArgumentException("route " + r);
                sb.Append("route ").Append(VpnRoutes.Network(ip, bits)).Append(' ').Append(VpnRoutes.Mask(bits)).Append('\n');
            }
        }
        foreach (var d in dns) { if (!IPAddress.TryParse(d, out _)) throw new ArgumentException("DNS " + d); sb.Append("dhcp-option DNS ").Append(d).Append('\n'); }
        // The management channel is password-protected (the file lives in the app's ACL'd folder beside this configuration),
        // so another local process can't drive or stop the tunnel.
        sb.Append($"management 127.0.0.1 {managementPort}{(managementPasswordFile != null ? " " + Quote(managementPasswordFile) : "")}\nmanagement-query-passwords\nmanagement-hold\nauth-nocache\nverb 3\nscript-security 0\n");
        return sb.ToString();
    }

    private static string Name(string file, string kind) => file.Length > 0 ? System.IO.Path.GetFileNameWithoutExtension(file) : kind;
    /// <summary>An OpenVPN option argument in double quotes (backslashes doubled, as OpenVPN's parser expects on Windows paths).</summary>
    private static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    private static string Short(string s) => s.Length > 60 ? s[..60] + "…" : s;
}
