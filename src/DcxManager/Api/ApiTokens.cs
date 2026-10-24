using System.Security.Cryptography;
using System.Text;
using DcxManager.Services;

namespace DcxManager.Api;

/// <summary>Creates, checks and revokes API tokens. Scopes are fixed; nothing grants access to secrets.</summary>
public static class ApiTokens
{
    public static readonly (string Scope, string Text)[] Scopes =
    {
        ("status.read", "App version and the active incident"),
        ("hosts.read", "Saved hosts: name, address, port, user, folder, platform (never credentials)"),
        ("incidents.read", "Incidents and their timelines (text entries; attachments are listed, not served)"),
        ("incidents.write", "Add events to an incident's timeline"),
        ("notes.write", "Create notes / append to a host's log note"),
    };

    private static List<ApiToken> All => AppSettings.Current.ApiTokens;

    /// <summary>Creates a token and returns its secret — the only time it exists in readable form.</summary>
    public static (ApiToken Token, string Secret) Create(string name, IEnumerable<string> scopes, TimeSpan? lifetime)
    {
        var valid = scopes.Where(s => Scopes.Any(x => x.Scope == s)).Distinct().ToList();
        if (valid.Count == 0) throw new ArgumentException("Pick at least one scope.");
        var secret = "dcx_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var t = new ApiToken
        {
            Name = name.Trim().Length > 0 ? name.Trim()[..Math.Min(60, name.Trim().Length)] : "token",
            Hash = HashOf(secret), Prefix = secret[..8], Scopes = valid, Expires = lifetime is { } l ? DateTime.Now + l : null,
        };
        All.Add(t);
        AppSettings.Save();
        AuditLog.Write("you", "api", "API token created", $"{t.Name} · {string.Join(",", t.Scopes)} · {t.StateText}");
        return (t, secret);
    }

    public static void Revoke(ApiToken t)
    {
        if (t.Revoked != null) return;
        t.Revoked = DateTime.Now;
        AppSettings.Save();
        AuditLog.Write("you", "api", "API token revoked", t.Name);
    }

    public static int RevokeAll()
    {
        int n = 0;
        foreach (var t in All.Where(t => t.Revoked == null)) { t.Revoked = DateTime.Now; n++; }
        if (n > 0) { AppSettings.Save(); AuditLog.Write("you", "api", "all API tokens revoked", $"{n} token(s)"); }
        return n;
    }

    /// <summary>Removes revoked/expired tokens from the list.</summary>
    public static int Purge()
    {
        int n = All.RemoveAll(t => !t.Valid);
        if (n > 0) AppSettings.Save();
        return n;
    }

    /// <summary>The valid token matching <paramref name="secret"/>, compared in constant time against every stored hash.</summary>
    public static ApiToken? Find(string? secret)
    {
        if (secret is not { Length: > 20 and < 100 } || !secret.StartsWith("dcx_")) return null;
        var h = Convert.FromHexString(HashOf(secret));
        ApiToken? hit = null;
        foreach (var t in All.ToList())
        {
            byte[] th;
            try { th = Convert.FromHexString(t.Hash); } catch { continue; }
            if (th.Length == h.Length && CryptographicOperations.FixedTimeEquals(th, h)) hit = t;
        }
        return hit is { Valid: true } ? hit : null;
    }

    private static DateTime _lastSave;

    /// <summary>Usage bookkeeping; persisted at most once a minute (no disk write per request).</summary>
    public static void Touch(ApiToken t)
    {
        t.LastUsed = DateTime.Now; t.Uses++;
        if (DateTime.Now - _lastSave > TimeSpan.FromMinutes(1)) { _lastSave = DateTime.Now; try { AppSettings.Save(); } catch (Exception ex) { Log.Write("API token usage", ex); } }
    }

    public static string HashOf(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
}

/// <summary>Token buckets keyed by string (per token, per broadcast).</summary>
public sealed class RateLimiter
{
    private readonly double _rate, _burst;
    private readonly Dictionary<string, (double Tokens, DateTime At)> _b = new();
    private readonly object _gate = new();
    public RateLimiter(double perSecond, double burst) { _rate = perSecond; _burst = burst; }

    public bool Allow(string key)
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            var (tokens, at) = _b.TryGetValue(key, out var v) ? v : (_burst, now);
            tokens = Math.Min(_burst, tokens + (now - at).TotalSeconds * _rate);
            bool ok = tokens >= 1;
            _b[key] = (ok ? tokens - 1 : tokens, now);
            if (_b.Count > 10_000) _b.Clear();
            return ok;
        }
    }
}
