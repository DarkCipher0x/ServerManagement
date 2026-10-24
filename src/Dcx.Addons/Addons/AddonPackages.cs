using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using DcxManager.Services;

namespace DcxManager.Addons;

/// <summary>What the approval screen shows for a package before anything is installed.</summary>
public sealed record PackageInfo(string Path, AddonManifest Manifest, string Sha256, long Bytes, int FileCount, string Signature,
    InstalledAddon? Existing, List<string> NewPermissions, string? Refusal);

/// <summary>Package inspection, installation, update, verification and removal (ADR-012).</summary>
public static class AddonPackages
{
    /// <summary>A kind of data add-on owned by a feature above the add-ons (the portal browser, ADR-032; the shared library,
    /// ADR-033): how its manifest is recognised, its data file, the size limit and strict parse checked at inspection, and
    /// what to tell the feature after install / enable / disable / remove. Registered at start-up (0.2.0 phase 6).</summary>
    public sealed record DataKind(string Name, Func<AddonManifest, bool> Is, Func<AddonManifest, string> FileOf, int MaxBytes, Action<string> Parse, Action<InstalledAddon> Notify);

    private static readonly List<DataKind> Kinds = new();
    public static void RegisterKind(DataKind kind) { if (Kinds.All(k => k.Name != kind.Name)) Kinds.Add(kind); }

    private static void NotifyKinds(InstalledAddon a)
    {
        foreach (var k in Kinds) k.Notify(a);
    }

    public const int MaxFiles = 500;
    public const long MaxUnpacked = 50L << 20;

    public sealed record PermissionInfo(string Id, string Text, string Risk);

    public static readonly PermissionInfo[] Catalog =
    {
        new("hosts.read", "Read your saved hosts: names, addresses, ports, users, folders — never passwords or keys", "low"),
        new("library.read", "Also list hosts from your shared libraries (marked shared) — never passwords or keys", "low"),
        new("incidents.read", "Read your incidents and their timelines", "medium"),
        new("incidents.write", "Add entries to incidents (marked with the add-on's name)", "medium"),
        new("notes.read", "Search and read your notes", "medium"),
        new("notes.write", "Create notes (tagged with the add-on's name)", "medium"),
        new("terminal.read", "Read the visible screen of the terminal you run it from (secrets masked)", "medium"),
        new("filesystem.read", "Read a file YOU pick in a dialog, each time it asks", "medium"),
        new("credentials.request", "Ask you for a password or key: each request shows a dialog where you pick a credential or deny", "HIGH"),
        // ADR-036: the full-control API (everything the app can do, through app.call and the registered methods).
        new("app.read", "Use any read-only capability of the app: server status, logs, services, files on saved hosts, infrastructure and cloud listings, terminal sessions, transfers, portals — the reads the AI operator has. Never passwords or keys", "medium"),
        new("app.private", "Read notes, incidents and documents through the app's capabilities (each read asks you)", "medium"),
        new("app.control", "Ask to make ANY change the app can make: run commands, restart services, power and change servers, cloud and infrastructure actions, file operations, terminal commands. Each change shows you the exact action first; you allow or deny it", "HIGH"),
        new("hosts.write", "Add, change or remove saved hosts (each change shows you the exact fields; never reads passwords or keys)", "medium"),
        new("ui.control", "Open the app's screens, and a saved host's server, terminal, files or notes view", "low"),
    };

    public static PermissionInfo? Permission(string id) => Catalog.FirstOrDefault(p => p.Id == id);

    public static string AddonsRoot => Environment.GetEnvironmentVariable("DCX_DATA_DIR") is { Length: > 0 } scratch
        ? Path.GetFullPath(scratch).TrimEnd('\\') + "-addons"
        : Path.Combine(AppInfo.CacheDir, "addons");

    /// <summary>The add-on's writable sandbox folder (Low integrity: under LocalLow).</summary>
    public static string DataFolder(string id)
    {
        var scope = Environment.GetEnvironmentVariable("DCX_DATA_DIR") is { Length: > 0 } ? "selftest" : "ServerManagement";
        return Path.Combine(AppInfo.LocalLowVendorDir, scope, "addons", id);
    }

    public static List<InstalledAddon> All => AppSettings.Current.Addons;
    public static InstalledAddon? Find(string id) => All.FirstOrDefault(a => a.Id == id);

    private static readonly Regex IdRx = new("^[a-z0-9][a-z0-9.-]{2,63}$");
    private static readonly Regex VersionRx = new(@"^\d{1,5}\.\d{1,5}\.\d{1,5}$");

    /// <summary>Reads and validates a package without installing anything.</summary>
    public static PackageInfo Inspect(string path)
    {
        var empty = new AddonManifest();
        string sha;
        long size = new FileInfo(path).Length;
        if (size > MaxUnpacked) return new(path, empty, "", size, 0, "", null, new(), "The package is larger than 50 MB.");
        using (var fs = File.OpenRead(path)) sha = Convert.ToHexString(SHA256.HashData(fs));
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var entries = zip.Entries.Where(e => !e.FullName.EndsWith('/')).ToList();
            if (entries.Count == 0 || entries.Count > MaxFiles) return new(path, empty, sha, size, entries.Count, "", null, new(), $"The package must hold 1–{MaxFiles} files.");
            if (entries.Sum(e => e.Length) > MaxUnpacked) return new(path, empty, sha, size, entries.Count, "", null, new(), "The package unpacks to more than 50 MB.");
            foreach (var e in entries) if (SafeRelative(e.FullName) == null) return new(path, empty, sha, size, entries.Count, "", null, new(), $"Unsafe path in the package: {e.FullName}");
            var mf = zip.GetEntry("addon.json") ?? throw new InvalidDataException("addon.json is missing at the root of the package.");
            if (mf.Length > 64 * 1024) throw new InvalidDataException("addon.json is too large.");
            AddonManifest m;
            using (var s = mf.Open()) m = JsonSerializer.Deserialize<AddonManifest>(s, new JsonSerializerOptions { PropertyNameCaseInsensitive = true, MaxDepth = 8 }) ?? throw new InvalidDataException("addon.json is empty.");
            var problem = Validate(m, entries.Select(e => SafeRelative(e.FullName)!).ToHashSet(StringComparer.OrdinalIgnoreCase));
            if (problem != null) return new(path, m, sha, size, entries.Count, "", null, new(), problem);
            if (ThemeAddons.IsTheme(m))
            {
                var te = entries.First(e => string.Equals(SafeRelative(e.FullName), SafeRelative(m.Theme), StringComparison.OrdinalIgnoreCase));
                if (te.Length > ThemePack.MaxBytes) return new(path, m, sha, size, entries.Count, "", null, new(), "The theme file is larger than 64 KB.");
                using var tr = new StreamReader(te.Open(), System.Text.Encoding.UTF8);
                try
                {
                    var tp = ThemePack.Parse(tr.ReadToEnd());
                    if (tp.Logo != null)
                    {
                        var le = entries.FirstOrDefault(e => string.Equals(SafeRelative(e.FullName), tp.Logo, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidDataException($"the logo {tp.Logo} is not in the package");
                        if (le.Length > ThemePack.MaxLogoBytes) throw new InvalidDataException("the logo is larger than 1 MB");
                        using var ls = le.Open(); using var ms = new MemoryStream(); ls.CopyTo(ms);
                        ThemePack.DecodeLogo(ms.ToArray());
                    }
                }
                catch (InvalidDataException ex) { return new(path, m, sha, size, entries.Count, "", null, new(), "The theme is not valid: " + ex.Message); }
            }
            // Data add-ons of the kinds registered by their features (the portal browser, the shared library): their data
            // file is size-checked and parsed strictly before anything is installed.
            foreach (var kind in Kinds.Where(k => k.Is(m)))
            {
                var de = entries.First(e => string.Equals(SafeRelative(e.FullName), SafeRelative(kind.FileOf(m)), StringComparison.OrdinalIgnoreCase));
                if (de.Length > kind.MaxBytes) return new(path, m, sha, size, entries.Count, "", null, new(), $"The {kind.Name} file is larger than {kind.MaxBytes / 1024} KB.");
                using var dr = new StreamReader(de.Open(), System.Text.Encoding.UTF8);
                try { kind.Parse(dr.ReadToEnd()); }
                catch (InvalidDataException ex) { return new(path, m, sha, size, entries.Count, "", null, new(), $"The {kind.Name} file is not valid: " + ex.Message); }
            }
            var existing = Find(m.Id);
            var newPerms = existing == null ? m.Permissions.ToList() : m.Permissions.Except(existing.Manifest.Permissions).ToList();
            string? refusal = null;
            if (existing != null)
            {
                var vNew = Version.Parse(m.Version); var vOld = Version.Parse(existing.Manifest.Version);
                if (vNew < vOld) refusal = $"This is version {m.Version}, older than the installed {existing.Manifest.Version}. Downgrades are refused — remove the add-on first if you really want the older one.";
                else if (vNew == vOld) refusal = $"Version {m.Version} is already installed.";
            }
            // The entry program's Authenticode state, checked on a temporary copy (shown on the approval screen).
            string signature = m.Entry.Length == 0 ? (m.Library.Length > 0 ? "shared library (no program)" : m.Browser.Length > 0 ? "portal browser (no program)" : "theme only (no program)") : "script (no signature check)";
            if (m.Entry.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                var tmp = Path.Combine(Path.GetTempPath(), "dcx-addon-" + Guid.NewGuid().ToString("N") + ".exe");
                try
                {
                    zip.GetEntry(m.Entry.Replace('\\', '/'))!.ExtractToFile(tmp);
                    var v = Updater.CheckSignature(tmp, Array.Empty<string>(), onlineRevocation: false);
                    signature = v.Signer is { Length: > 0 } who && v.Verdict == UpdateVerdict.UntrustedPublisher ? $"Signed by {who} (signature valid)" : v.Verdict == UpdateVerdict.NotSigned ? "NOT signed" : "Signature INVALID — " + v.Message;
                }
                finally { try { File.Delete(tmp); } catch { } }
            }
            refusal ??= PolicyRefusal(m, signature);   // 0.5.1 #7: Group Policy says no before the approval screen opens
            return new(path, m, sha, size, entries.Count, signature, existing, newPerms, refusal);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException)
        {
            return new(path, empty, sha, size, 0, "", null, new(), "Not a valid add-on package: " + ex.Message);
        }
    }

    private static string? Validate(AddonManifest m, HashSet<string> files)
    {
        // s40 (DarkCipher0x: every add-on to the same standard, encrypted only): API 2 for every package, data-only ones too. API 1
        // data-only add-ons installed earlier keep working (they have no channel to the app and are sealed at rest, ADR-035).
        if (m.Api == 1) return "This add-on is made for API 1, the old unencrypted standard. Every add-on must be built for API 2 (program add-ons talk to the app only over the encrypted bridge; data-only add-ons are sealed at rest); see docs/ADDONS.md.";
        if (m.Api != 2) return $"The add-on needs API {m.Api}; this app provides API 2.";
        if (!IdRx.IsMatch(m.Id ?? "")) return "Invalid id (3–64 lowercase letters, digits, dots, hyphens).";
        if (!VersionRx.IsMatch(m.Version ?? "")) return "Invalid version (major.minor.patch).";
        if (string.IsNullOrWhiteSpace(m.Name) || m.Name.Length > 60) return "A name of 1–60 characters is required.";
        if (string.IsNullOrWhiteSpace(m.Publisher) || m.Publisher.Length > 80) return "A publisher of 1–80 characters is required.";
        if ((m.Description ?? "").Length > 1000) return "The description is longer than 1000 characters.";
        m.Entry ??= ""; m.Theme ??= ""; m.Browser ??= ""; m.Library ??= ""; m.Permissions ??= new(); m.Commands ??= new();
        if (m.Library.Length > 0)
        {
            // Shared library (ADR-033): data only; the library engine is app code, so nothing in the package runs or asks for anything.
            if (SafeRelative(m.Library) is not { } lf || !files.Contains(lf) || !lf.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return "The library file must be a .json file in the package.";
            if (m.Entry.Length > 0 || m.Theme.Length > 0 || m.Browser.Length > 0 || m.Permissions.Count > 0 || m.Commands.Count > 0) return "A shared library add-on can't also have an entry program, a theme, a browser, permissions or commands.";
            return null;
        }
        if (m.Browser.Length > 0)
        {
            // Portal browser (ADR-032): data only; the browser is app code, so nothing in the package runs or asks for anything.
            if (SafeRelative(m.Browser) is not { } bf || !files.Contains(bf) || !bf.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return "The browser file must be a .json file in the package.";
            if (m.Entry.Length > 0 || m.Theme.Length > 0 || m.Permissions.Count > 0 || m.Commands.Count > 0) return "A portal browser add-on can't also have an entry program, a theme, permissions or commands.";
            return null;
        }
        if (m.Theme.Length > 0 && (SafeRelative(m.Theme) is not { } theme || !files.Contains(theme) || !theme.EndsWith(".json", StringComparison.OrdinalIgnoreCase))) return "The theme must be a .json file in the package.";
        if (m.Entry.Length == 0 && m.Theme.Length > 0)
        {
            // Theme-only (ADR-030): data, never a program, so it can't ask for anything.
            if (m.Permissions.Count > 0 || m.Commands.Count > 0) return "A theme-only add-on (no entry) can't have permissions or commands.";
            return null;
        }
        // ADR-035: a program add-on talks to the app only over the encrypted bridge; the plain-text API 1 is refused.
        if (m.Api != 2) return "This add-on uses the old, unencrypted bridge (API 1). Program add-ons must use API 2, where every message between the app and the add-on is encrypted (AES-256-GCM, keys made fresh for each run); see docs/ADDONS.md and the SDK in samples/addons/sdk.";
        if (SafeRelative(m.Entry) is not { } entry || !files.Contains(entry)) return "The entry program is not in the package.";
        if (!entry.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !entry.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)) return "The entry must be a .exe or .ps1 file.";
        if (m.TimeoutSeconds is < 5 or > 600) return "timeoutSeconds must be 5–600.";
        if (m.Permissions.Any(p => Permission(p) == null)) return "Unknown permission: " + m.Permissions.First(p => Permission(p) == null);
        m.Permissions = m.Permissions.Distinct().ToList();
        if (m.Commands.Count is 0 or > 30) return "An add-on needs 1–30 commands.";
        foreach (var c in m.Commands)
        {
            if (!Regex.IsMatch(c.Id ?? "", "^[a-z0-9][a-z0-9.-]{0,40}$") || string.IsNullOrWhiteSpace(c.Title) || c.Title.Length > 60) return $"Invalid command “{c.Id}”.";
            if (c.Context is not ("global" or "host" or "terminal")) return $"Command “{c.Id}” has an unknown context “{c.Context}”.";
        }
        if (m.Commands.Select(c => c.Id).Distinct().Count() != m.Commands.Count) return "Command ids must be unique.";
        return null;
    }

    /// <summary>A relative path inside the package, or null when it could escape the install folder.</summary>
    public static string? SafeRelative(string p) => PackagePath.SafeRelative(p);

    /// <summary>Installs or updates. <paramref name="grant"/> is what the user approved on this screen; on an update the
    /// previous grants are kept and only <paramref name="grant"/> ∩ NEW permissions are added.</summary>
    public static InstalledAddon Install(PackageInfo info, IEnumerable<string> grant)
    {
        if (info.Refusal != null) throw new InvalidOperationException(info.Refusal);
        if (PolicyRefusal(info.Manifest, info.Signature) is { } policy) throw new InvalidOperationException(policy);   // checked again: policy may have changed
        var m = info.Manifest;
        // The package must not have changed since it was inspected and approved.
        string sha; using (var fs = File.OpenRead(info.Path)) sha = Convert.ToHexString(SHA256.HashData(fs));
        if (sha != info.Sha256) throw new InvalidOperationException("The package file changed after it was checked. Install it again.");
        var folder = Path.Combine(AddonsRoot, m.Id, m.Version);
        var staging = folder + ".installing";
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        Directory.CreateDirectory(staging);
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var zip = ZipFile.OpenRead(info.Path);
            foreach (var e in zip.Entries.Where(e => !e.FullName.EndsWith('/')))
            {
                var rel = SafeRelative(e.FullName) ?? throw new InvalidDataException("unsafe path " + e.FullName);
                var target = Path.GetFullPath(Path.Combine(staging, rel));
                if (!target.StartsWith(Path.GetFullPath(staging) + "\\", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("unsafe path " + e.FullName);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                byte[] plain;
                using (var es = e.Open()) using (var ms = new MemoryStream()) { es.CopyTo(ms); plain = ms.ToArray(); }
                hashes[rel] = Convert.ToHexString(SHA256.HashData(plain));
                // Data-only add-ons (theme, portal browser, shared library) are read only by the app: their files are
                // encrypted at rest like the rest of the store. A program add-on's files must stay runnable by Windows.
                File.WriteAllBytes(target, m.Entry.Length == 0 ? SecureStore.Encrypt(plain, SealContext(m.Id, m.Version, rel)) : plain);
            }
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            Directory.Move(staging, folder);
        }
        catch { try { Directory.Delete(staging, true); } catch { } throw; }

        var existing = Find(m.Id);
        var approved = grant.Intersect(m.Permissions).ToHashSet();
        var a = existing ?? new InstalledAddon();
        var oldFolder = existing?.Folder;
        var granted = existing == null ? approved : existing.Granted.Intersect(m.Permissions).Concat(approved.Intersect(info.NewPermissions)).ToHashSet();
        a.Manifest = m; a.Granted = granted.ToList(); a.Folder = folder; a.Files = hashes; a.PackageSha256 = sha;
        a.Signature = info.Signature; a.Updated = DateTime.Now; a.DisabledReason = null; a.Enabled = true; a.Sealed = m.Entry.Length == 0;
        if (existing == null) All.Add(a);
        AppSettings.Save();
        Directory.CreateDirectory(DataFolder(m.Id));
        if (oldFolder != null && !oldFolder.Equals(folder, StringComparison.OrdinalIgnoreCase)) { try { if (Directory.Exists(oldFolder)) Directory.Delete(oldFolder, true); } catch (Exception ex) { Log.Write("Add-on old version", ex); } }
        AuditLog.Write("you", "addons", existing == null ? "add-on installed" : "add-on updated", $"{m.Id} {m.Version} · granted: {string.Join(",", a.Granted)} · {info.Signature} · sha256 {sha[..16]}…");
        ThemeAddons.Reapply(a);
        NotifyKinds(a);
        return a;
    }

    private static string SealContext(string id, string version, string rel) => $"addon|{id}|{version}|{rel.Replace('/', '\\').ToLowerInvariant()}";

    /// <summary>An installed file's content, decrypted when the add-on is sealed and checked against the hash recorded at
    /// install. Throws <see cref="InvalidDataException"/> when it was changed.</summary>
    public static byte[] ReadInstalled(InstalledAddon a, string rel)
    {
        rel = SafeRelative(rel) ?? throw new InvalidDataException("unsafe path " + rel);
        var key = a.Files.Keys.FirstOrDefault(k => k.Equals(rel, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidDataException(rel + " is not part of the add-on");
        var bytes = File.ReadAllBytes(Path.Combine(a.Folder, key));
        if (a.Sealed)
            try { bytes = SecureStore.Decrypt(bytes, SealContext(a.Id, a.Manifest.Version, key)); }
            catch (System.Security.Cryptography.CryptographicException) { throw new InvalidDataException(key + " was modified"); }
        if (Convert.ToHexString(SHA256.HashData(bytes)) != a.Files[key]) throw new InvalidDataException(key + " was modified");
        return bytes;
    }

    /// <summary>Start-up migration (0.1.17): data-only add-ons installed before sealing existed get their files encrypted in
    /// place — only when every file still matches the hash approved at install; a changed add-on is left for VerifyFiles to disable.</summary>
    public static int SealExisting()
    {
        int n = 0;
        foreach (var a in All.Where(a => !a.Sealed && a.Manifest.Entry.Length == 0 && Directory.Exists(a.Folder)).ToList())
        {
            if (VerifyFiles(a) != null) continue;
            try
            {
                // Two phases: every sealed copy is written first, then all are swapped in, so a failure leaves the plain set.
                var paths = a.Files.Keys.Select(rel => (Path: Path.Combine(a.Folder, rel), Rel: rel)).ToList();
                foreach (var (path, rel) in paths) File.WriteAllBytes(path + ".sealing", SecureStore.Encrypt(File.ReadAllBytes(path), SealContext(a.Id, a.Manifest.Version, rel)));
                foreach (var (path, _) in paths) File.Move(path + ".sealing", path, overwrite: true);
                a.Sealed = true;
                n++;
                AuditLog.Write("app", "addons", "add-on files encrypted at rest", a.Id);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Write("Seal add-on " + a.Id, ex); }
        }
        if (n > 0) AppSettings.Save();
        return n;
    }

    public static string ReadInstalledText(InstalledAddon a, string rel) => System.Text.Encoding.UTF8.GetString(ReadInstalled(a, rel)).TrimStart('\uFEFF');

    /// <summary>Every installed file must still match its recorded hash, and nothing may have been added.</summary>
    public static string? VerifyFiles(InstalledAddon a)
    {
        if (!Directory.Exists(a.Folder)) return "its files are missing";
        var root = Path.GetFullPath(a.Folder);
        var present = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(f => f[(root.Length + 1)..]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (rel, hash) in a.Files)
        {
            var p = Path.Combine(root, rel);
            if (!File.Exists(p)) return $"{rel} is missing";
            if (a.Sealed) { try { ReadInstalled(a, rel); } catch (InvalidDataException ex) { return ex.Message; } continue; }
            if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))) != hash) return $"{rel} was modified";
        }
        var extra = present.Except(a.Files.Keys, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        return extra != null ? $"an unexpected file appeared: {extra}" : null;
    }

    // ---- policy (0.5.1 #7) --------------------------------------------------------------------------------------------

    /// <summary>Why Group Policy (or this PC's settings) refuses installing this package, or null. AllowAddonInstall,
    /// AddonBlockedIds, AddonAllowedIds and AddonRequireSigned (program add-ons only: data-only ones have no program).</summary>
    public static string? PolicyRefusal(AddonManifest m, string signature) =>
        Restrictions.Why("addon.install") ?? UseRefusal(m.Id, m.Entry.Length > 0, signature);

    /// <summary>Why an installed add-on may not run or be applied now (blocked, not on the allowed list, or unsigned while
    /// signing is required), or null. Not a tamper verdict: it is not disabled, so lifting the policy brings it back.</summary>
    public static string? UseRefusal(InstalledAddon a) => UseRefusal(a.Id, a.Manifest.Entry.Length > 0, a.Signature);

    private static string? UseRefusal(string id, bool program, string signature)
    {
        var s = AppSettings.Current;
        if (s.AddonBlockedIds.Contains(id, StringComparer.OrdinalIgnoreCase)) return $"The add-on {id} is blocked by your organisation (AddonBlockedIds).";
        if (s.AddonAllowedIds.Count > 0 && !s.AddonAllowedIds.Contains(id, StringComparer.OrdinalIgnoreCase)) return $"Only approved add-ons are allowed here, and {id} isn't one of them (AddonAllowedIds).";
        if (s.AddonRequireSigned && program && !signature.StartsWith("Signed by", StringComparison.Ordinal)) return $"Add-on programs must be Authenticode-signed here (AddonRequireSigned); {id} is: {signature}.";
        return null;
    }

    public static void SetEnabled(InstalledAddon a, bool on, string? reason = null)
    {
        a.Enabled = on; a.DisabledReason = on ? null : reason;
        AppSettings.Save();
        AuditLog.Write(reason != null && !on ? "app" : "you", "addons", on ? "add-on enabled" : "add-on disabled", a.Id + (reason != null ? " · " + reason : ""));
        ThemeAddons.Reapply(a, enabled: on);
        NotifyKinds(a);
    }

    public static void SetPermission(InstalledAddon a, string perm, bool on)
    {
        if (!a.Manifest.Permissions.Contains(perm)) return;
        if (on && !a.Granted.Contains(perm)) a.Granted.Add(perm);
        if (!on) a.Granted.Remove(perm);
        AppSettings.Save();
        AuditLog.Write("you", "addons", on ? "add-on permission granted" : "add-on permission revoked", $"{a.Id}: {perm}");
    }

    public static void Remove(InstalledAddon a, bool deleteData = true)
    {
        try { var dir = Path.Combine(AddonsRoot, a.Id); if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (Exception ex) { Log.Write("Add-on remove", ex); }
        if (deleteData) { try { if (Directory.Exists(DataFolder(a.Id))) Directory.Delete(DataFolder(a.Id), true); } catch (Exception ex) { Log.Write("Add-on data remove", ex); } }
        All.Remove(a);
        AppSettings.Save();
        AuditLog.Write("you", "addons", "add-on removed", $"{a.Id} {a.Manifest.Version}");
        if (AppSettings.Current.ThemeAddon == a.Id) ThemeAddons.Use(null);   // back to the app's own theme
        NotifyKinds(a);
    }
}
