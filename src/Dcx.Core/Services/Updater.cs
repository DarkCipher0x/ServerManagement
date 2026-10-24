using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DcxManager.Services;

/// <summary>What the update source says (untrusted until every check below passes). <paramref name="ManifestSigned"/>:
/// the update information carried a valid signature from the pinned DarkCipher release key (ADR-079), which then vouches
/// for the package's size and SHA-256. <paramref name="IsSetup"/>: the package is the setup program (Setup.exe), used
/// when this PC was installed with it; otherwise the MSI.</summary>
public sealed record UpdateInfo(Version Version, string Released, string Notes, bool SchemaChange, string FileName, Uri Url, long Bytes, string Sha256, bool ManifestSigned = false, bool IsSetup = false);

public enum UpdateVerdict { Verified, NotSigned, BadSignature, UntrustedPublisher, WrongProduct, WrongVersion, HashMismatch, SizeMismatch, Failed }

public sealed record VerifyResult(UpdateVerdict Verdict, string Message, string? Signer = null)
{
    public bool Ok => Verdict == UpdateVerdict.Verified;
}

/// <summary>
/// User-initiated updates (ADR-008; 0.6.1 ADR-079: GitHub Releases as the source, trust from DarkCipher0x's own release key).
/// <para>Source: by default the repository's latest GitHub Release (<see cref="GitHubRepo"/>): its update.json, the
/// detached signature update.json.sig, and the installers, fetched from github.com and GitHub's download host only
/// (redirects followed only between those, https only). Alternatively an https address of your own (or the website).</para>
/// <para>Trust: the update information must carry a valid ECDSA P-256 signature from the pinned release key
/// (<see cref="ReleaseKeyX"/> / <see cref="ReleaseKeyY"/>; the private key never leaves DarkCipher0x's Windows key store,
/// tools\sign-update.ps1), or else the installer must be Authenticode-signed by a pinned publisher certificate. The
/// server is only transport: a hijacked account or server can't make the app install anything.</para>
/// <para>The information is untrusted metadata until then: strict schema, only a strictly NEWER version (no downgrade;
/// an old signed release can't be replayed as new), package name tied to the version, size cap. The download goes to
/// the app's private temp folder and must match the signed size and SHA-256; an MSI must also have our UpgradeCode and
/// the advertised version. Installing always goes through you (backup prompt, then the normal installer UI).</para>
/// The requests carry no identifiers: plain GETs with a generic User-Agent, no cookies, no query string.
/// </summary>
public static class Updater
{
    public const string DefaultEndpoint = "https://darkciphersystems.com/downloads/servermanagement/update.json";
    /// <summary>The GitHub repository whose Releases carry update.json, update.json.sig and the installers.</summary>
    public const string GitHubRepo = "DarkCipher0x/ServerManagement";
    public static string GitHubLatestManifest => $"https://github.com/{GitHubRepo}/releases/latest/download/update.json";
    public const string UpgradeCode = "{6B7AC824-8E15-4FE4-BA73-D3FC492B50E3}";
    /// <summary>The setup program's permanent identity (installer\setup-app.wxi): finds a Setup.exe install.</summary>
    public const string BundleUpgradeCode = "{DC5144B0-240E-42CF-A17C-507E2864D614}";
    public const long MaxBytes = 400L << 20;

    /// <summary>The DarkCipher release key (ECDSA P-256 public point, hex), created once on 2026-10-21 by
    /// tools\sign-update.ps1 -NewKey. Never change it: installed copies trust only this key.</summary>
    public const string ReleaseKeyX = "1DCB9C7FF6B2A495A8B62BF17F93FB8512E79108375E76B949F78950DB3203DF";
    public const string ReleaseKeyY = "033E23C670828C9A9D1AFB6216D8AF95F6B81E24CA0B703D1495AFA8578E825E";
    public static bool ReleaseKeyPinned => ReleaseKeyX.Length == 64 && ReleaseKeyY.Length == 64;

    /// <summary>The post-quantum release key (ML-DSA-65 public key, base64; 0.6.12, ADR-090), created once on 2026-10-24 by
    /// tools\sign-update.ps1 -NewPqKey. From 0.6.12 on an update is offered only when update.json carries BOTH signatures:
    /// the ECDSA one (update.json.sig) and this one (update.json.pq.sig). Never change it either.</summary>
    public const string ReleasePqKey = "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";
    public static bool ReleasePqKeyPinned => ReleasePqKey.Length > 2000;

    /// <summary>Self-test seam: test release keys (ECDSA X / Y hex, ML-DSA-65 base64) in place of the pinned ones, so a
    /// fully signed fake release can be checked end to end. Never set outside the self-test.</summary>
    internal static (string X, string Y, string Pq)? TestKeys { get; set; }

    /// <summary>True when <paramref name="pqSigFile"/> (base64 of an ML-DSA-65 signature) signs exactly <paramref name="data"/>
    /// with the post-quantum release key (or the given test key, base64).</summary>
    public static bool VerifyManifestPq(byte[] data, byte[] pqSigFile, string? pubB64 = null)
    {
        try { return Pqc.Verify(Convert.FromBase64String(pubB64 ?? TestKeys?.Pq ?? ReleasePqKey), data, Convert.FromBase64String(Encoding.ASCII.GetString(pqSigFile).Trim())); }
        catch (FormatException) { return false; }
    }

    /// <summary>The post-quantum check of a manifest that passed the ECDSA check: null when it passes (or no post-quantum
    /// key is pinned), else why it was refused.</summary>
    private static string? PqProblem(byte[] manifest, byte[]? pqSig, string? fetchProblem, string where) =>
        !ReleasePqKeyPinned ? null
        : fetchProblem != null || pqSig == null ? $"{where} has no post-quantum signature (update.json.pq.sig); nothing is offered."
        : VerifyManifestPq(manifest, pqSig) ? null
        : "The update information's post-quantum signature doesn't match the DarkCipher release key (tampered, or not ours); refused.";

    /// <summary>SHA-1 thumbprints of the DarkCipher Systems code-signing certificate(s). Empty: no certificate yet; the
    /// release key (above) is the trust anchor until one exists.</summary>
    public static readonly string[] TrustedPublisherThumbprints = Array.Empty<string>();

    /// <summary>On when a trust anchor is pinned (the release key since 0.6.1). Checks still happen only when you click,
    /// or on the schedule you opt into. A Microsoft Store install (ADR-029) never uses it: the Store delivers updates.</summary>
    public static bool Enabled => (TrustedPublisherThumbprints.Length > 0 || ReleaseKeyPinned) && !Packaging.IsPackaged;

    public static string DisabledMessage => Packaging.IsPackaged
        ? "This copy was installed from the Microsoft Store, which signs and delivers updates (Microsoft Store → Library → Get updates). Your encrypted data stays where it is."
        : "Updates are turned off in this build: no release key or publisher certificate is pinned.";

    /// <summary>"github" (default) or "url" (<see cref="AppSettings.UpdateEndpoint"/>, empty = the website).</summary>
    public static bool FromGitHub => AppSettings.Current.UpdateSource != "url";
    public static string CustomEndpoint => string.IsNullOrWhiteSpace(AppSettings.Current.UpdateEndpoint) ? DefaultEndpoint : AppSettings.Current.UpdateEndpoint.Trim();
    /// <summary>Where the update information is read from.</summary>
    public static string Endpoint => FromGitHub ? GitHubLatestManifest : CustomEndpoint;
    /// <summary>How often the opt-in automatic check runs ("daily" or "weekly").</summary>
    public static TimeSpan CheckInterval => AppSettings.Current.UpdateCheckInterval == "daily" ? TimeSpan.FromDays(1) : TimeSpan.FromDays(7);

    public static Version Installed => Version.TryParse(AppInfo.Version, out var v) ? v : new Version(0, 0, 0);

    /// <summary>Endpoint check for Settings: https only.</summary>
    public static string? ValidateEndpoint(string url) =>
        !Uri.TryCreate(url, UriKind.Absolute, out var u) ? "Not a valid address." : u.Scheme != Uri.UriSchemeHttps ? "The update address must use https." : null;

    /// <summary>No automatic redirects: <see cref="FetchAsync"/> decides which hops are allowed.</summary>
    private static HttpClient NewHttp(HttpMessageHandler? h) =>
        new(h ?? Offline.Handler(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false })) { Timeout = TimeSpan.FromSeconds(30), DefaultRequestHeaders = { { "User-Agent", "DarkCipher-Updater" } } };

    /// <summary>github.com, its API and GitHub's release download host(s), nothing else.</summary>
    public static bool GitHubHost(Uri u) => u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || IsGitHubApi(u) || u.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    private static bool IsGitHubApi(Uri u) => u.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>A GET with a size cap, following at most 5 redirects and only while <paramref name="allowed"/> accepts
    /// every hop (https only). <paramref name="apiToken"/> and <paramref name="accept"/> go only on requests to
    /// api.github.com (never with a redirect to the download host). Returns the status, the body (2xx only) or why it
    /// was refused.</summary>
    private static async Task<(int Status, byte[]? Body, string? Refused)> FetchAsync(HttpClient http, Uri url, long cap, Func<Uri, bool>? allowed, CancellationToken ct, IProgress<double>? progress = null, Stream? to = null, string? apiToken = null, string? accept = null)
    {
        for (int hop = 0; hop <= 5; hop++)
        {
            if (url.Scheme != Uri.UriSchemeHttps) return (0, null, "an address that isn't https");
            if (hop > 0 && (allowed == null || !allowed(url))) return (0, null, $"a redirect to {url.Host}, which isn't allowed");
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (IsGitHubApi(url))
            {
                req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
                if (accept != null) req.Headers.Accept.ParseAdd(accept);
                if (apiToken is { Length: > 0 }) req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiToken);
            }
            using var r = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            int code = (int)r.StatusCode;
            if (code is >= 300 and < 400)
            {
                if (allowed == null || r.Headers.Location is not { } loc) return (code, null, null);
                url = loc.IsAbsoluteUri ? loc : new Uri(url, loc);
                continue;
            }
            if (!r.IsSuccessStatusCode) return (code, null, null);
            if (r.Content.Headers.ContentLength > cap) return (code, null, "a larger file than allowed");
            await using var src = await r.Content.ReadAsStreamAsync(ct);
            var mem = to == null ? new MemoryStream() : null;
            var dst = to ?? mem!;
            var buf = new byte[81920]; long total = 0; int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                total += n;
                if (total > cap) return (code, null, "a larger file than announced");
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                progress?.Report((double)total / cap);
            }
            return (code, mem?.ToArray() ?? Array.Empty<byte>(), null);
        }
        return (0, null, "too many redirects");
    }

    /// <summary>Fetches and validates the update information. Returns (info, message) when an update exists,
    /// (null, message) otherwise.</summary>
    public static async Task<(UpdateInfo? Info, string Message)> CheckAsync(CancellationToken ct, HttpMessageHandler? handler = null)
    {
        try { return FromGitHub ? await CheckGitHubAsync(ct, handler) : await CheckUrlAsync(ct, handler); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return (null, "Couldn't reach the update server: " + (ex is TaskCanceledException ? "timed out" : ex.Message)); }
    }

    private static async Task<(UpdateInfo? Info, string Message)> CheckUrlAsync(CancellationToken ct, HttpMessageHandler? handler)
    {
        var url = CustomEndpoint;
        if (ValidateEndpoint(url) is { } bad) return (null, bad);
        var u = new Uri(url);
        using var http = NewHttp(handler);
        var (code, body, refused) = await FetchAsync(http, u, 256 * 1024, null, ct);
        if (refused != null) return (null, "The update information was refused: " + refused + ".");
        if (code == 404) return (null, $"No update information is published at {url} yet.");
        if (code is >= 300 and < 400) return (null, "The update address redirected elsewhere; redirects aren't followed. Check the address in Settings → Updates.");
        if (body == null) return (null, $"The update server answered HTTP {code}.");
        var (info, msg) = Parse(Utf8(body), u);
        if (info == null) return (null, msg);
        // The signature beside it (update.json.sig), when published: a bad one is refused, a missing one leaves the
        // Authenticode certificate as the only way to install.
        var (sc, sig, sr) = await FetchAsync(http, new Uri(u.AbsoluteUri + ".sig"), 4096, null, ct);
        if (sr == null && sig != null)
        {
            if (!VerifyManifest(body, sig)) return (null, "The update information's signature doesn't match the DarkCipher release key (tampered, or not ours); refused.");
            var (_, pqSig, pqr) = await FetchAsync(http, new Uri(u.AbsoluteUri + ".pq.sig"), 8192, null, ct);
            if (PqProblem(body, pqSig, pqr, "The update information at " + url) is { } pqBad) return (null, pqBad);
            var (signedInfo, signedMsg) = Parse(Utf8(body), u, preferSetup: SetupInstalled());
            return (signedInfo! with { ManifestSigned = true }, signedMsg);
        }
        return (info, msg);
    }

    /// <summary>The access token for a private repository (Settings → Updates), or null.</summary>
    private static string? GitHubToken => AppSettings.Current.UpdateGitHubToken is { Length: > 0 } t ? t.Trim() : null;

    private static async Task<(UpdateInfo? Info, string Message)> CheckGitHubAsync(CancellationToken ct, HttpMessageHandler? handler)
    {
        if (GitHubToken is { } token) return await CheckGitHubApiAsync(token, ct, handler);
        using var http = NewHttp(handler);
        var (code, body, refused) = await FetchAsync(http, new Uri(GitHubLatestManifest), 256 * 1024, GitHubHost, ct);
        if (refused != null) return (null, "The update information was refused: " + refused + ".");
        if (code == 404) return (null, $"No release with update information is published on GitHub yet (github.com/{GitHubRepo}/releases), or the repository isn't public.");
        if (body == null) return (null, $"GitHub answered HTTP {code}.");
        // Only the version (digits and dots) from the still-untrusted file goes into the next address.
        string? ver = null;
        try { ver = (JsonNode.Parse(Utf8(body)) as JsonObject)?["version"]?.ToString(); } catch { }
        if (ver == null || !Regex.IsMatch(ver, @"^\d{1,4}\.\d{1,4}\.\d{1,6}$")) return (null, "The update information on GitHub is malformed (no valid version).");
        var release = new Uri($"https://github.com/{GitHubRepo}/releases/download/v{ver}/update.json");
        var (sc, sig, sr) = await FetchAsync(http, new Uri(release.AbsoluteUri + ".sig"), 4096, GitHubHost, ct);
        if (sr != null || sig == null) return (null, $"The GitHub release v{ver} has no signature for its update information (update.json.sig); nothing is offered.");
        if (!VerifyManifest(body, sig)) return (null, "The update information's signature doesn't match the DarkCipher release key (tampered, not ours, or the release changed while checking: try again); refused.");
        var (_, pqSig, pqr) = await FetchAsync(http, new Uri(release.AbsoluteUri + ".pq.sig"), 8192, GitHubHost, ct);
        if (PqProblem(body, pqSig, pqr, $"The GitHub release v{ver}") is { } pqBad) return (null, pqBad);
        var (info, msg) = Parse(Utf8(body), release, preferSetup: SetupInstalled());
        return info == null ? (null, msg) : (info with { ManifestSigned = true }, msg);
    }

    /// <summary>A private repository (0.6.2, DarkCipher0x: "leave it private ... i can still access it for testing"): the latest
    /// release through api.github.com with your token, each file as a release asset (the asset ids come from GitHub; the
    /// addresses are built here from the repository and the number). The same signature and parsing rules as above.</summary>
    private static async Task<(UpdateInfo? Info, string Message)> CheckGitHubApiAsync(string token, CancellationToken ct, HttpMessageHandler? handler)
    {
        using var http = NewHttp(handler);
        var api = $"https://api.github.com/repos/{GitHubRepo}";
        var (code, body, refused) = await FetchAsync(http, new Uri(api + "/releases/latest"), 1024 * 1024, GitHubHost, ct, apiToken: token, accept: "application/vnd.github+json");
        if (refused != null) return (null, "The release information was refused: " + refused + ".");
        if (code is 401 or 403) return (null, "GitHub refused the access token (expired, revoked, or without read access to this repository). Check it in Settings → Updates.");
        if (code == 404) return (null, $"No release is published on GitHub yet (github.com/{GitHubRepo}/releases), or the access token can't see the repository.");
        if (body == null) return (null, $"GitHub answered HTTP {code}.");
        var assets = new Dictionary<string, long>(StringComparer.Ordinal);
        string tag;
        try
        {
            var rel = JsonNode.Parse(Utf8(body)) as JsonObject ?? throw new FormatException();
            tag = rel["tag_name"]?.ToString() ?? "";
            foreach (var a in (rel["assets"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
                if (a["name"]?.ToString() is { Length: > 0 } n && long.TryParse(a["id"]?.ToString(), out var id) && id > 0) assets[n] = id;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException) { return (null, "GitHub's release information is malformed."); }
        Uri Asset(long id) => new($"{api}/releases/assets/{id}");
        if (!assets.TryGetValue("update.json", out var mid)) return (null, $"The latest GitHub release ({tag}) has no update information (update.json); nothing is offered.");
        if (!assets.TryGetValue("update.json.sig", out var sid)) return (null, $"The latest GitHub release ({tag}) has no signature for its update information (update.json.sig); nothing is offered.");
        const string Octet = "application/octet-stream";
        var (_, manifest, mr) = await FetchAsync(http, Asset(mid), 256 * 1024, GitHubHost, ct, apiToken: token, accept: Octet);
        var (_, sig, sr) = await FetchAsync(http, Asset(sid), 4096, GitHubHost, ct, apiToken: token, accept: Octet);
        if (mr != null || sr != null || manifest == null || sig == null)
        {
            var why = mr ?? sr;
            return (null, "Couldn't download the update information from GitHub" + (why != null ? $": {why}." : "."));
        }
        if (!VerifyManifest(manifest, sig)) return (null, "The update information's signature doesn't match the DarkCipher release key (tampered, or not ours); refused.");
        byte[]? pqSig = null;
        string? pqr = "missing";
        if (assets.TryGetValue("update.json.pq.sig", out var qid)) { var (_, q, qr) = await FetchAsync(http, Asset(qid), 8192, GitHubHost, ct, apiToken: token, accept: Octet); pqSig = q; pqr = qr; }
        if (PqProblem(manifest, pqSig, pqr, $"The latest GitHub release ({tag})") is { } pqBad) return (null, pqBad);
        string? ver = null;
        try { ver = (JsonNode.Parse(Utf8(manifest)) as JsonObject)?["version"]?.ToString(); } catch { }
        if (ver == null || !Regex.IsMatch(ver, @"^\d{1,4}\.\d{1,4}\.\d{1,6}$") || tag != "v" + ver) return (null, $"The signed update information (version {ver ?? "?"}) doesn't belong to the release {tag}; refused.");
        var (info, msg) = Parse(Utf8(manifest), new Uri($"https://github.com/{GitHubRepo}/releases/download/v{ver}/update.json"), preferSetup: SetupInstalled());
        if (info == null) return (null, msg);
        if (!assets.TryGetValue(info.FileName, out var pid)) return (null, $"The release {tag} doesn't carry {info.FileName}; nothing is offered.");
        return (info with { ManifestSigned = true, Url = Asset(pid) }, msg);
    }

    private static string Utf8(byte[] b) => Encoding.UTF8.GetString(b).TrimStart('﻿');

    /// <summary>True when <paramref name="sigFile"/> (base64 of an ECDSA P-256 / SHA-256 signature, r||s) signs exactly
    /// <paramref name="data"/> with the release key (or the given test key).</summary>
    public static bool VerifyManifest(byte[] data, byte[] sigFile, string? keyX = null, string? keyY = null)
    {
        try
        {
            var sig = Convert.FromBase64String(Encoding.ASCII.GetString(sigFile).Trim());
            if (sig.Length != 64) return false;
            using var ec = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = Convert.FromHexString(keyX ?? TestKeys?.X ?? ReleaseKeyX), Y = Convert.FromHexString(keyY ?? TestKeys?.Y ?? ReleaseKeyY) } });
            return ec.VerifyData(data, sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch { return false; }
    }

    /// <summary>This PC was installed with the setup program (a Burn bundle with ServerManagement's identity is
    /// registered): updates then use the new Setup.exe, so its Add/Remove Programs entry stays right.</summary>
    public static bool SetupInstalled()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var un = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (un == null) continue;
                foreach (var name in un.GetSubKeyNames())
                {
                    using var k = un.OpenSubKey(name);
                    var v = k?.GetValue("BundleUpgradeCode");
                    if (v is string[] codes && codes.Any(c => c.Equals(BundleUpgradeCode, StringComparison.OrdinalIgnoreCase))) return true;
                    if (v is string one && one.Equals(BundleUpgradeCode, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            catch { }
        }
        return false;
    }

    /// <summary>Strict manifest parsing (public for the self-test). Never trusts a URL, name or number it doesn't check.
    /// <paramref name="preferSetup"/>: pick the setup program when listed (only for signed information).</summary>
    public static (UpdateInfo? Info, string Message) Parse(string json, Uri manifestUrl, Version? installed = null, bool preferSetup = false)
    {
        installed ??= Installed;
        JsonObject? o;
        try { o = JsonNode.Parse(json) as JsonObject; } catch { return (null, "The update information is malformed (not JSON)."); }
        // Duplicate keys or wrongly typed values surface only when read: same answer as malformed JSON (fuzzing, s23).
        try { return ParseChecked(o, manifestUrl, installed, preferSetup); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException) { return (null, "The update information is malformed."); }
    }

    private static (UpdateInfo? Info, string Message) ParseChecked(JsonObject? o, Uri manifestUrl, Version installed, bool preferSetup)
    {
        // The product was named "DarkCipher Server Management" until 0.1.8; manifests with either name are this product.
        if (o == null || o["product"]?.ToString() is not ("DarkCipher ServerManagement" or "DarkCipher Server Management")) return (null, "The update information is for a different product.");
        if (!Version.TryParse(o["version"]?.ToString(), out var v) || v.Build < 0 || v.Revision >= 0) return (null, "The update information has no valid version (x.y.z).");
        if (v <= installed) return (null, v == installed ? $"You are running the latest available version ({installed})." : $"You are running {installed}; the published version is {v} (older). Downgrades are never offered.");
        var all = (o["packages"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new();
        JsonObject? Kind(string kind) => all.FirstOrDefault(p => p["kind"]?.ToString() == kind && p["arch"]?.ToString() == "x64");
        var pkg = preferSetup ? Kind("setup") : null;
        bool isSetup = pkg != null;
        pkg ??= Kind("msi");
        if (pkg == null) return (null, "The update information lists no Windows x64 installer.");
        var file = pkg["file"]?.ToString() ?? "";
        if (file != (isSetup ? $"DarkCipherServerManagement-{v}-Setup.exe" : $"DarkCipherServerManagement-{v}-x64.msi")) return (null, "The installer's file name doesn't match the version; refused.");
        if (!Uri.TryCreate(manifestUrl, pkg["url"]?.ToString() ?? file, out var url) || url.Scheme != Uri.UriSchemeHttps || !url.Host.Equals(manifestUrl.Host, StringComparison.OrdinalIgnoreCase)
            || !url.AbsolutePath.EndsWith("/" + file, StringComparison.Ordinal) || url.Query.Length > 0)
            return (null, "The installer must be on the same https server as the update information; refused.");
        if (!long.TryParse(pkg["bytes"]?.ToString(), out var bytes) || bytes <= 0 || bytes > MaxBytes) return (null, "The installer size is missing or implausible; refused.");
        var sha = pkg["sha256"]?.ToString() ?? "";
        if (!Regex.IsMatch(sha, "^[0-9a-fA-F]{64}$")) return (null, "The installer checksum is missing; refused.");
        var notes = o["notes"]?.ToString() ?? "";
        if (notes.Length > 8000) notes = notes[..8000] + "…";
        return (new UpdateInfo(v, o["released"]?.ToString() ?? "", notes, o["schemaChange"]?.GetValue<bool>() == true, file, url, bytes, sha.ToLowerInvariant(), IsSetup: isSetup), $"Version {v} is available (you have {installed}).");
    }

    /// <summary>Downloads into the app's private temp folder with the size cap (redirects only within GitHub for a GitHub
    /// release, none otherwise), then verifies.</summary>
    public static async Task<(string? Path, VerifyResult Result)> DownloadAndVerifyAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct, HttpMessageHandler? handler = null)
    {
        var dir = Path.Combine(AppInfo.ShareDir, "update-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, info.FileName);
        try
        {
            using var http = NewHttp(handler);
            http.Timeout = TimeSpan.FromMinutes(20);
            int code; string? refused;
            await using (var dst = File.Create(path))
                (code, _, refused) = await FetchAsync(http, info.Url, Math.Min(info.Bytes, MaxBytes), GitHubHost(info.Url) ? GitHubHost : null, ct, progress, dst,
                    apiToken: IsGitHubApi(info.Url) ? GitHubToken : null, accept: IsGitHubApi(info.Url) ? "application/octet-stream" : null);
            if (refused != null) { TryDelete(dir); return (null, new VerifyResult(refused.Contains("larger") ? UpdateVerdict.SizeMismatch : UpdateVerdict.Failed, refused.Contains("larger") ? "The download is larger than announced; stopped and discarded." : "Download refused: " + refused + ".")); }
            if (code is < 200 or >= 300) { TryDelete(dir); return (null, new VerifyResult(UpdateVerdict.Failed, $"Download failed: HTTP {code}.")); }
            var v = Verify(path, info);
            if (!v.Ok) { TryDelete(dir); return (null, v); }
            return (path, v);
        }
        catch (OperationCanceledException) { TryDelete(dir); throw; }
        catch (Exception ex)
        {
            TryDelete(dir);
            return (null, new VerifyResult(UpdateVerdict.Failed, "Download failed: " + ex.Message));
        }
    }

    private static void TryDelete(string dir) { try { Directory.Delete(dir, true); } catch { } }

    /// <summary>All checks on a downloaded package, cheapest first. Public for the self-test.</summary>
    public static VerifyResult Verify(string path, UpdateInfo info, IReadOnlyCollection<string>? trusted = null, bool onlineRevocation = true)
    {
        trusted ??= TrustedPublisherThumbprints;
        var len = new FileInfo(path).Length;
        if (len != info.Bytes) return new(UpdateVerdict.SizeMismatch, $"Size {len:N0} bytes, announced {info.Bytes:N0}; refused.");
        using (var fs = File.OpenRead(path))
            if (!Convert.ToHexString(SHA256.HashData(fs)).Equals(info.Sha256, StringComparison.OrdinalIgnoreCase)) return new(UpdateVerdict.HashMismatch, "The SHA-256 doesn't match the published checksum (damaged or substituted); refused.");
        string how, signer;
        if (info.ManifestSigned)
        {
            // The release key signed the size and SHA-256 these bytes just matched.
            how = "the update information is signed by the DarkCipher release key"; signer = "DarkCipher release key";
        }
        else
        {
            var sig = CheckSignature(path, trusted, onlineRevocation);
            if (!sig.Ok) return sig;
            how = $"Authenticode signature valid ({sig.Signer})"; signer = sig.Signer ?? "";
        }
        if (info.IsSetup)
        {
            if (!info.ManifestSigned) return new(UpdateVerdict.WrongProduct, "A setup program is only accepted with signed update information; refused.");
            using var fs = File.OpenRead(path);
            if (fs.ReadByte() != 'M' || fs.ReadByte() != 'Z') return new(UpdateVerdict.WrongProduct, "The setup program isn't a Windows program; refused.");
            return new(UpdateVerdict.Verified, $"Verified: size and SHA-256 match, {how}; the setup program for {info.Version}.", signer);
        }
        var (upgrade, version) = MsiProps(path);
        if (!string.Equals(upgrade, UpgradeCode, StringComparison.OrdinalIgnoreCase)) return new(UpdateVerdict.WrongProduct, $"The installer is for a different product (UpgradeCode {upgrade ?? "missing"}); refused.", signer);
        if (!Version.TryParse(version, out var mv) || new Version(mv.Major, mv.Minor, Math.Max(0, mv.Build)) != info.Version) return new(UpdateVerdict.WrongVersion, $"The installer says version {version}, not {info.Version} (possible replay of an old release); refused.", signer);
        return new(UpdateVerdict.Verified, $"Verified: size and SHA-256 match, {how}, product and version {info.Version} confirmed.", signer);
    }

    /// <summary>WinVerifyTrust (chain + revocation, no UI) + the signer must be a pinned publisher. Revocation is checked
    /// online for a real install (the CA's CRL/OCSP — part of the user-initiated update); <paramref name="onlineRevocation"/>
    /// false uses cached revocation data only (self-test, no network).</summary>
    public static VerifyResult CheckSignature(string path, IReadOnlyCollection<string> trusted, bool onlineRevocation = true)
    {
        int rc = WinVerify(path, onlineRevocation);
        if (rc == unchecked((int)0x800B0100)) return new(UpdateVerdict.NotSigned, "The installer is not code-signed. The app only installs updates signed by DarkCipher Systems.");
        if (rc == unchecked((int)0x80092013)) return new(UpdateVerdict.BadSignature, "Couldn't check whether the signing certificate was revoked (offline?); refused. Try again when online.");
        if (rc != 0) return new(UpdateVerdict.BadSignature, $"The installer's signature is invalid or untrusted (WinVerifyTrust 0x{rc:X8}); refused.");
        X509Certificate2 signer;
        // CreateFromSignedFile is obsolete in .NET 9+ with no loader replacement; it only reads the Authenticode signer of a
        // file WinVerifyTrust has just verified, which is the use it is still meant for.
#pragma warning disable SYSLIB0057
        try { signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(path)); } catch (Exception ex) { return new(UpdateVerdict.BadSignature, "Couldn't read the signer: " + ex.Message); }
#pragma warning restore SYSLIB0057
        var name = signer.GetNameInfo(X509NameType.SimpleName, false);
        if (!trusted.Contains(signer.Thumbprint, StringComparer.OrdinalIgnoreCase))
            return new(UpdateVerdict.UntrustedPublisher, $"Signed by “{name}” (thumbprint {signer.Thumbprint}), which is not a DarkCipher Systems publisher certificate; refused.", name);
        return new(UpdateVerdict.Verified, "signature OK", name);
    }

    // ---- install / post-install verification ----------------------------------------------------------------------

    /// <summary>An update the opt-in background download already fetched and verified this session (Settings → Updates
    /// offers it at once). Never persisted: the file sits in the private temp folder, wiped at exit.</summary>
    public static (UpdateInfo Info, string Path)? Ready { get; set; }

    /// <summary>Where a verified installer waits while the app closes (the share folder is wiped at exit, which could race
    /// the installer reading it); cleared on the next start.</summary>
    private static string StagingDir => Path.Combine(AppInfo.CacheDir, "update-ready");

    /// <summary>Starts the installer's normal UI for an already verified package (the user sees the setup program or
    /// Windows' installer, and UAC). The package is copied to its own folder and verified again there first. The app
    /// records what it expects so the next start can say whether the update really happened.</summary>
    public static void LaunchInstaller(string path, UpdateInfo info)
    {
        TryDelete(StagingDir);
        Directory.CreateDirectory(StagingDir);
        var staged = Path.Combine(StagingDir, info.FileName);
        File.Copy(path, staged, true);
        var again = Verify(staged, info);
        if (!again.Ok) { TryDelete(StagingDir); throw new InvalidOperationException(again.Message); }
        AppSettings.Current.PendingUpdateVersion = info.Version.ToString();
        AppSettings.Current.PendingUpdateFrom = AppInfo.Version;
        AppSettings.Save();
        AuditLog.Write("you", "app", "update installer started", $"{AppInfo.Version} → {info.Version} ({(info.IsSetup ? "setup program" : "MSI")})");
        var psi = info.IsSetup
            ? new System.Diagnostics.ProcessStartInfo(staged) { UseShellExecute = false, WorkingDirectory = StagingDir }
            : new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe"), $"/i \"{staged}\"") { UseShellExecute = false };
        System.Diagnostics.Process.Start(psi);
    }

    /// <summary>On start: if an update was started, compare the running version with what was expected.
    /// Returns a message for the status bar / a dialog, or null when nothing was pending.</summary>
    public static string? ConfirmPendingUpdate()
    {
        var s = AppSettings.Current;
        if (string.IsNullOrEmpty(s.PendingUpdateVersion)) return null;
        TryDelete(StagingDir);
        var expected = s.PendingUpdateVersion; var from = s.PendingUpdateFrom;
        s.PendingUpdateVersion = ""; s.PendingUpdateFrom = "";
        try { AppSettings.Save(); } catch { }
        if (AppInfo.Version == expected) { AuditLog.Write("app", "app", "update verified", $"{from} → {expected}"); return $"Update verified: now running {expected} (was {from})."; }
        AuditLog.Write("app", "app", "update not completed", $"expected {expected}, running {AppInfo.Version}");
        return $"The update to {expected} did not complete: still running {AppInfo.Version}. Your data is unchanged.";
    }

    // ---- Win32: WinVerifyTrust (Authenticode.cs since 0.6.14) and MSI properties --------------------------------------

    private static int WinVerify(string path, bool online) => Authenticode.Verify(path, online);

    public static (string? UpgradeCode, string? Version) MsiProps(string path)
    {
        if (MsiOpenDatabaseW(path, IntPtr.Zero, out var db) != 0) return (null, null);
        try { return (Prop(db, "UpgradeCode"), Prop(db, "ProductVersion")); }
        finally { MsiCloseHandle(db); }
    }

    private static string? Prop(IntPtr db, string name)
    {
        if (!Regex.IsMatch(name, "^[A-Za-z]+$") || MsiDatabaseOpenViewW(db, $"SELECT `Value` FROM `Property` WHERE `Property`='{name}'", out var view) != 0) return null;
        try
        {
            if (MsiViewExecute(view, IntPtr.Zero) != 0 || MsiViewFetch(view, out var rec) != 0) return null;
            try
            {
                var sb = new StringBuilder(256); int len = sb.Capacity;
                return MsiRecordGetStringW(rec, 1, sb, ref len) == 0 ? sb.ToString() : null;
            }
            finally { MsiCloseHandle(rec); }
        }
        finally { MsiCloseHandle(view); }
    }

    [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern uint MsiOpenDatabaseW(string path, IntPtr persist, out IntPtr handle);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern uint MsiDatabaseOpenViewW(IntPtr db, string query, out IntPtr view);
    [DllImport("msi.dll")] private static extern uint MsiViewExecute(IntPtr view, IntPtr record);
    [DllImport("msi.dll")] private static extern uint MsiViewFetch(IntPtr view, out IntPtr record);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern uint MsiRecordGetStringW(IntPtr record, int field, StringBuilder value, ref int len);
    [DllImport("msi.dll")] private static extern uint MsiCloseHandle(IntPtr handle);
}
