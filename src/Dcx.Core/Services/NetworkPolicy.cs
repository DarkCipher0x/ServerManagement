using System.Net;

namespace DcxManager.Services;

/// <summary>Where a configured endpoint sits relative to this workstation's privacy boundary.</summary>
public enum NetClass
{
    /// <summary>This PC only (loopback). Nothing leaves the machine.</summary>
    Local,
    /// <summary>Private address space / LAN names (RFC 1918, CGNAT/Tailscale, link-local, ULA, single-label, .lan/.local/.internal/.home.arpa).</summary>
    OnPremises,
    /// <summary>A DNS name we can't place reliably (could be a LAN DNS entry or a public service). Never claimed to be safe.</summary>
    UserConfigured,
    /// <summary>A public IP address or a known internet service. Data sent here leaves your network.</summary>
    External,
}

/// <summary>
/// The single classifier for "does this destination cross the external trust boundary?" Used by the AI gate, the
/// Settings badges/warnings and the Network activity summary. Ordinary operational traffic the user asked for (SSH, RDP,
/// SFTP, tunnels to their own hosts) is not telemetry and is not routed through here.
/// </summary>
public static class NetworkPolicy
{
    /// <summary>Hosts (and their sub-domains) known to be internet services this app can be pointed at.</summary>
    private static readonly string[] KnownInternet =
    {
        "darkciphersystems.com", "openai.com", "openai.azure.com", "anthropic.com", "claude.ai", "vultr.com", "googleapis.com", "github.com",
        "githubusercontent.com", "huggingface.co", "together.xyz", "groq.com", "mistral.ai", "openrouter.ai", "x.ai", "deepseek.com",
    };

    public static NetClass Classify(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return NetClass.UserConfigured;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) && !Uri.TryCreate("http://" + url.Trim(), UriKind.Absolute, out u))
            return NetClass.UserConfigured;
        return ClassifyHost(u.Host);
    }

    public static NetClass ClassifyHost(string host)
    {
        var h = host.Trim().Trim('[', ']').TrimEnd('.');
        if (h.Length == 0) return NetClass.UserConfigured;
        if (h.Equals("localhost", StringComparison.OrdinalIgnoreCase) || h.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) return NetClass.Local;
        if (IPAddress.TryParse(h, out var ip))
        {
            if (IPAddress.IsLoopback(ip)) return NetClass.Local;
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            var b = ip.GetAddressBytes();
            if (b.Length == 4)
            {
                bool priv = b[0] == 10 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168
                            || b[0] == 100 && b[1] is >= 64 and <= 127 || b[0] == 169 && b[1] == 254;
                return priv ? NetClass.OnPremises : NetClass.External;
            }
            return ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal || ip.IsIPv6SiteLocal ? NetClass.OnPremises : NetClass.External;
        }
        if (!h.Contains('.')) return NetClass.OnPremises;   // single-label name: resolved on the local network
        foreach (var suffix in new[] { ".local", ".lan", ".internal", ".home.arpa", ".intranet", ".corp", ".localdomain" })
            if (h.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return NetClass.OnPremises;
        foreach (var k in KnownInternet)
            if (h.Equals(k, StringComparison.OrdinalIgnoreCase) || h.EndsWith("." + k, StringComparison.OrdinalIgnoreCase)) return NetClass.External;
        return NetClass.UserConfigured;
    }

    /// <summary>Local or on-premises (the AI gate's "local/LAN" rule). User-configured names are NOT treated as local.</summary>
    public static bool IsPrivate(string? url) => Classify(url) is NetClass.Local or NetClass.OnPremises;

    public static string Label(NetClass c) => c switch
    {
        NetClass.Local => "LOCAL / OFFLINE",
        NetClass.OnPremises => "LOCAL NETWORK / ON-PREMISES",
        NetClass.UserConfigured => "USER-CONFIGURED NETWORK ENDPOINT",
        _ => "EXTERNAL NETWORK / INTERNET",
    };

    /// <summary>Shape-coded glyphs so the class is readable without colour.</summary>
    public static string Glyph(NetClass c) => c switch
    {
        NetClass.Local => "●",
        NetClass.OnPremises => "◆",
        NetClass.UserConfigured => "◇",
        _ => "⚠",
    };

    public static string Explain(NetClass c) => c switch
    {
        NetClass.Local => "Stays on this PC.",
        NetClass.OnPremises => "A private / LAN address: stays on your network.",
        NetClass.UserConfigured => "A DNS name the app can't place: it may be on your network or on the internet. Treated as not local.",
        _ => "On the internet: information you send to it leaves your network.",
    };
}
