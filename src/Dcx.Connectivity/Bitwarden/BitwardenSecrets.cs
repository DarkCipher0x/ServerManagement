using System.Text.Json;
using DcxManager.Services;
using static DcxManager.Bitwarden.BitwardenCli;

namespace DcxManager.Bitwarden;

/// <summary>
/// Bitwarden Secrets Manager through <c>bws</c> (0.6.14, ADR-093): the projects and secrets a <b>machine account</b> may
/// read, with its <b>access token</b> (kept encrypted in the app's settings; it goes to bws as an environment variable of
/// that one process). bws decrypts the secrets itself. Lists keep keys, projects and notes only; a value is read one secret
/// at a time, for the session or the copy that asked. Every call reaches the server, so offline mode governs each.
/// Self-hosted Bitwarden servers work; Vaultwarden has no Secrets Manager.
/// </summary>
public static class BitwardenSecrets
{
    private static AppSettings S => AppSettings.Current;

    public static bool HasToken => (S.ObserveSecrets.GetValueOrDefault("bws:token") ?? "").Length > 0;

    private static async Task<Result> Bws(List<string> args, CancellationToken ct, string? token = null)
    {
        BitwardenVault.Enabled();
        token ??= S.ObserveSecrets.GetValueOrDefault("bws:token") ?? "";
        if (token.Length == 0) throw new InvalidOperationException("Add a machine account's access token first: Settings → Integrations → Bitwarden → Secrets Manager.");
        await Offline.EnforceAsync(BitwardenVault.ServerUrl(S.BitwardenServer), "Bitwarden Secrets Manager", ct);
        args.AddRange(new[] { "--output", "json" });
        if (S.BitwardenServer.Trim().Length > 0) { args.Add("--server-url"); args.Add(BitwardenVault.ServerUrl(S.BitwardenServer)); }
        var r = await RunAsync(Tool.Bws, args, new Dictionary<string, string> { ["BWS_ACCESS_TOKEN"] = token }, TimeSpan.FromSeconds(60), ct);
        if (r.ExitCode != 0) throw new InvalidOperationException(Problem(Tool.Bws, r));
        return r;
    }

    private static string Str(JsonElement e, string n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>The projects the machine account can see. A token typed but not saved yet can be tried with <paramref name="token"/>.</summary>
    public static async Task<List<SmProject>> ProjectsAsync(CancellationToken ct = default, string? token = null)
    {
        var r = await Bws(new List<string> { "project", "list" }, ct, token);
        using var d = JsonDocument.Parse(r.Output);
        if (d.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("not a list of projects");
        return d.RootElement.EnumerateArray().Select(e => new SmProject(Str(e, "id"), Str(e, "name")))
            .Where(p => BitwardenVault.IsId(p.Id)).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>The secrets (all, or one project's): keys and notes only. bws's answer includes the values; they are
    /// dropped here, while reading it.</summary>
    public static async Task<List<SmSecret>> SecretsAsync(string? projectId = null, CancellationToken ct = default)
    {
        var args = new List<string> { "secret", "list" };
        if (projectId is { Length: > 0 })
        {
            if (!BitwardenVault.IsId(projectId)) throw new ArgumentException("Not a Secrets Manager project id.");
            args.Add(projectId);
        }
        var r = await Bws(args, ct);
        return ParseSecrets(r.Output);
    }

    internal static List<SmSecret> ParseSecrets(string json)
    {
        using var d = JsonDocument.Parse(json);
        if (d.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("not a list of secrets");
        return d.RootElement.EnumerateArray().Select(e => new SmSecret(Str(e, "id"), Str(e, "key"), Str(e, "projectId"), Str(e, "note")))
            .Where(s => BitwardenVault.IsId(s.Id)).OrderBy(s => s.Key, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>One secret's value, for the use that asked (a session, a copy).</summary>
    public static async Task<VaultSecret> ValueAsync(string id, CancellationToken ct = default)
    {
        if (!BitwardenVault.IsId(id)) throw new ArgumentException("Not a Secrets Manager secret id.");
        var r = await Bws(new List<string> { "secret", "get", id }, ct);
        return ParseValue(r.Output);
    }

    internal static VaultSecret ParseValue(string json)
    {
        using var d = JsonDocument.Parse(json);
        var key = Str(d.RootElement, "key");
        var value = Str(d.RootElement, "value");
        if (value.Length == 0) throw new InvalidOperationException($"“{key}” has no value.");
        return new VaultSecret(key, "", value, value.TrimStart().StartsWith("-----BEGIN", StringComparison.Ordinal));
    }
}
