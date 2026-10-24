using Renci.SshNet;

namespace DcxManager.Ssh;

/// <summary>
/// Post-quantum SSH key exchange (0.6.12, ADR-090). SSH.NET 2026.0.0 implements the hybrid exchanges OpenSSH uses:
/// <c>mlkem768x25519-sha256</c> (ML-KEM-768 + X25519; OpenSSH 10's default) and <c>sntrup761x25519-sha512</c> (Streamlined
/// NTRU Prime + X25519; OpenSSH 9.0+). Every connection offers them first; with "Require post-quantum key exchange for
/// SSH" (Settings, admin-protected, Group Policy <c>PqSshRequired</c>) they are the only ones offered, so a server without
/// them is refused rather than silently connected classically. What was negotiated is shown on the session's strip.
/// Host keys and user keys stay classical signatures (no post-quantum SSH signature is standardised yet).
/// </summary>
public static class SshPq
{
    /// <summary>The hybrid post-quantum key exchanges, in order of preference.</summary>
    public static readonly string[] Algorithms = { "mlkem768x25519-sha256", "sntrup761x25519-sha512", "sntrup761x25519-sha512@openssh.com" };

    public static bool IsPostQuantum(string? kex) => kex != null && Algorithms.Contains(kex, StringComparer.Ordinal);

    /// <summary>Puts the post-quantum exchanges first (the client's order decides), or with <paramref name="require"/>
    /// leaves only them. Throws when post-quantum is required and the library offers none (it always does from 2026.0.0).</summary>
    public static void Apply(ConnectionInfo ci, bool require)
    {
        var algs = ci.KeyExchangeAlgorithms;
        var pq = Algorithms.Where(algs.ContainsKey).Select(k => (Name: k, Make: algs[k])).ToList();
        if (pq.Count == 0)
        {
            if (require) throw new InvalidOperationException("This build's SSH library offers no post-quantum key exchange, and it is required (Settings → Security & privacy).");
            return;
        }
        var rest = require ? new List<(string Name, Func<Renci.SshNet.Security.IKeyExchange> Make)>() : algs.Where(kv => !IsPostQuantum(kv.Key)).Select(kv => (kv.Key, kv.Value)).ToList();
        algs.Clear();
        foreach (var (name, make) in pq.Concat(rest)) algs.Add(name, make);
    }

    /// <summary>The negotiated key exchange as the session strip shows it.</summary>
    public static string Describe(string? kex) =>
        string.IsNullOrEmpty(kex) ? "key exchange unknown" : IsPostQuantum(kex) ? $"post-quantum {kex}" : $"classical {kex}";

    /// <summary>Why a connection failed, when it was the post-quantum requirement (no shared key exchange), else null.</summary>
    public static string? RequirementProblem(Exception ex, bool require) =>
        require && ex.Message.Contains("key exchange", StringComparison.OrdinalIgnoreCase)
            ? "This server doesn't offer a post-quantum key exchange (OpenSSH 9.0+ offers sntrup761x25519, 10.0+ mlkem768x25519 by default), and \"Require post-quantum key exchange for SSH\" is on. Update the server's OpenSSH, or turn the requirement off in Settings → Security & privacy (administrator approval)."
            : null;
}
