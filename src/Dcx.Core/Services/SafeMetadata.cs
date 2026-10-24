using System.Text;

namespace DcxManager.Services;

/// <summary>
/// What each kind of object exposes when the user deliberately sends it to the assistant (the AI). A formatter, not AI
/// code (0.2.0 phase 4 moved it out of Ai\ as AiContext). One place, so the rule is
/// auditable: descriptions carry configuration and metadata only — never passwords, private keys, passphrases, API keys or
/// free-text notes (notes are where people paste secrets). Everything still passes the assistant's masking on the way out.
/// </summary>
public static class SafeMetadata
{
    /// <summary>The cloud provider a host is linked to ("Vultr"), or null: set by the app at start-up (0.2.0 phase 6: the
    /// formatter doesn't reference the cloud providers above it).</summary>
    public static Func<HostEntry, string?>? CloudLink { get; set; }

    public static string Host(HostEntry h)
    {
        var jump = AppData.Current.FindHost(h.JumpHostId)?.Display;
        var cred = AppData.Current.FindCredential(h.CredentialId);
        var sb = new StringBuilder();
        sb.AppendLine($"Saved host “{h.Display}”: protocol {h.Protocol}, platform {h.EffectivePlatform}, address {h.Address}:{h.Port}, user {h.Username}");
        sb.AppendLine($"Folder: {(h.Group.Length > 0 ? h.Group : "(none)")} · tags: {(h.Tags.Length > 0 ? h.Tags : "(none)")} · favourite: {h.Favorite}");
        sb.AppendLine($"Authentication: {h.Auth}{(cred != null ? $" using vault credential “{cred.Name}” ({cred.Kind}{(cred.KeyType.Length > 0 ? ", " + cred.KeyType : "")})" : "")}{(h.SudoCredentialId != null ? " · sudo password stored in the vault" : "")}");
        sb.AppendLine($"Jump host: {jump ?? "none"} · keep-alive {h.KeepAliveSeconds}s · last connected {(h.LastConnected?.ToString("yyyy-MM-dd HH:mm") ?? "never")}");
        if (CloudLink?.Invoke(h) is { } cloud) sb.AppendLine($"Linked to a {cloud} instance.");
        if (h.Protocol == "rdp" && h.Rdp is { } r)
            sb.AppendLine($"RDP: {(r.FullScreen ? "full screen" : $"{r.Width}×{r.Height}")}, multi-monitor {r.MultiMonitor}, admin {r.Admin}, clipboard {r.Clipboard}, drives {r.Drives}, printers {r.Printers}, gateway {(r.Gateway.Length > 0 ? r.Gateway : "none")}, auto-reconnect {r.AutoReconnect} ({r.ReconnectRetries} retries)");
        var tunnels = AppData.Current.Tunnels.Where(t => t.HostId == h.Id).ToList();
        if (tunnels.Count > 0) sb.AppendLine("Tunnels: " + string.Join("; ", tunnels.Select(Tunnel)));
        return sb.ToString();
    }

    /// <summary>Key hygiene view of the vault: names, types, fingerprints, ages and usage — never the secret material.</summary>
    public static string Credentials(IEnumerable<Credential> creds)
    {
        var sb = new StringBuilder("name\tkind\tkey type\tfingerprint\tusername\tcreated\tused by hosts\thas passphrase\n");
        foreach (var c in creds)
        {
            var used = AppData.Current.Hosts.Count(h => h.CredentialId == c.Id || h.SudoCredentialId == c.Id);
            sb.AppendLine($"{c.Name}\t{c.Kind}\t{c.KeyType}\t{c.Fingerprint}\t{c.Username}\t{c.Created:yyyy-MM-dd}\t{used}\t{(c.Kind == CredentialKind.PrivateKey ? (c.Passphrase.Length > 0 ? "yes" : "no") : "-")}");
        }
        return sb.ToString();
    }

    public static string Tunnel(TunnelDef t)
    {
        var h = AppData.Current.FindHost(t.HostId);
        return $"{t.Name}: {t.Kind} {t.BindAddress}:{t.BindPort} → {t.DestHost}:{t.DestPort} via {h?.Display ?? "(missing host)"}{(t.AutoStart ? " (auto-start)" : "")}";
    }

    public static string KnownHosts(IEnumerable<KnownHost> keys) =>
        "host\talgorithm\tSHA256 fingerprint\ttrusted since\n" + string.Join("\n", keys.Select(k => $"{k.Key}\t{k.Algorithm}\t{k.Fingerprint}\t{k.FirstSeen:yyyy-MM-dd}"));
}
