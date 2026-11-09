using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DcxManager.Services;

namespace DcxManager.Addons;

/// <summary>
/// The signature of a module add-on (ADR-120). A module runs inside the app with the app's rights, so the app loads one
/// only when the package's <c>module.json</c> verifies with DarkCipher Systems' pinned release keys: ECDSA P-256
/// (<c>module.json.sig</c>) and ML-DSA-65 (<c>module.json.pq.sig</c>). These are the same keys that sign update.json,
/// used through tools\sign-update.ps1 by samples\addons\pack.ps1. <c>module.json</c> says what it signs:
/// <c>{"purpose": "dcx-addon-module", "id", "version", "api", "assembly", "type", "files": {path: sha256}}</c>.
/// The purpose keeps a module signature from being taken for an update manifest and the other way round. The id,
/// version, assembly, type and API must match addon.json, and the files must be exactly the package's files (except
/// module.json and its signatures) with those hashes. A third party can't make a module add-on the app will load; their
/// add-ons stay programs (over the encrypted bridge) or data.
/// </summary>
public static class ModuleSignature
{
    public const string ManifestFile = "module.json", SigFile = "module.json.sig", PqSigFile = "module.json.pq.sig";
    public const string Purpose = "dcx-addon-module";

    public static bool IsSignatureFile(string rel) => rel is ManifestFile or SigFile or PqSigFile;

    /// <summary>Null when the package's module is signed and matches; else why it isn't loaded. <paramref name="read"/>
    /// returns a file's bytes (null when missing); <paramref name="files"/> are the package's files.</summary>
    public static string? Problem(AddonManifest m, IReadOnlyCollection<string> files, Func<string, byte[]?> read)
    {
        if (m.Module is not { } mod) return "not a module add-on";
        var json = read(ManifestFile);
        var sig = read(SigFile);
        var pq = read(PqSigFile);
        if (json is null || sig is null) return "The module isn't signed (module.json and module.json.sig are missing).";
        if (json.Length > 64 * 1024) return "module.json is too large.";
        if (!Updater.VerifyManifest(json, sig)) return "The module's signature doesn't verify with DarkCipher Systems' release key: it isn't loaded.";
        if (Updater.ReleasePqKeyPinned && (pq is null || !Updater.VerifyManifestPq(json, pq))) return "The module's post-quantum signature (module.json.pq.sig) is missing or doesn't verify: it isn't loaded.";
        JsonObject o;
        try { o = JsonNode.Parse(Encoding.UTF8.GetString(json).TrimStart('﻿')) as JsonObject ?? throw new FormatException(); }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException) { return "module.json isn't valid JSON."; }
        string Str(string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var x) ? x : "";
        if (Str("purpose") != Purpose) return "module.json isn't a module signature.";
        if (Str("id") != m.Id || Str("version") != m.Version) return "module.json was signed for another add-on or version.";
        if (Str("assembly") != mod.Assembly || Str("type") != mod.Type || (o["api"] is JsonValue av && av.TryGetValue<int>(out var api) ? api : -1) != mod.Api)
            return "module.json doesn't match addon.json's module.";
        if (o["files"] is not JsonObject list) return "module.json lists no files.";
        var expected = files.Where(f => !IsSignatureFile(f)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Paths as the package uses them (backslashes); one that isn't a safe relative path is refused.
        if (list.Any(kv => PackagePath.SafeRelative(kv.Key) is null)) return "module.json lists an unsafe path.";
        var signed = list.Select(kv => PackagePath.SafeRelative(kv.Key)!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!expected.SetEquals(signed)) return "The package's files aren't the ones that were signed: " + string.Join(", ", expected.Except(signed).Concat(signed.Except(expected)).Take(5));
        foreach (var (rel, h) in list)
        {
            var want = h is JsonValue hv && hv.TryGetValue<string>(out var hs) ? hs : "";
            if (!Regex.IsMatch(want, "^[0-9a-fA-F]{64}$")) return "module.json has a bad hash for " + rel;
            var bytes = read(PackagePath.SafeRelative(rel)!);
            if (bytes is null || !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), want, StringComparison.OrdinalIgnoreCase)) return "A file isn't the one that was signed: " + rel;
        }
        return null;
    }

    /// <summary>What module.json holds for a package (the packer signs exactly these bytes).</summary>
    public static byte[] Build(AddonManifest m, IEnumerable<(string Rel, byte[] Bytes)> files)
    {
        var mod = m.Module ?? throw new ArgumentException("not a module add-on");
        var list = new JsonObject();
        foreach (var (rel, bytes) in files.Where(f => !IsSignatureFile(f.Rel)).OrderBy(f => f.Rel, StringComparer.Ordinal))
            list[rel] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var o = new JsonObject { ["purpose"] = Purpose, ["id"] = m.Id, ["version"] = m.Version, ["api"] = mod.Api, ["assembly"] = mod.Assembly, ["type"] = mod.Type, ["files"] = list };
        return Encoding.UTF8.GetBytes(o.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }
}
