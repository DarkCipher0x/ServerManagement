using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DcxManager.Addons;
using DcxManager.Infra;
using DcxManager.Services;

namespace DcxManager.Browser;

/// <summary>What the address bar's security chip says.</summary>
public enum WebSecurity { None, Secure, Pinned, NotSecure, Untrusted, Changed }

/// <summary>A portal browser add-on's browser.json (ADR-032). Pure data like a theme (ADR-030): a name, notes and optional
/// starter bookmarks the user can add from the Portals screen. Parsed strictly at inspection; unknown keys are refused.</summary>
public sealed class BrowserPack
{
    public const int MaxBytes = 64 * 1024;
    public const int MaxStarters = 50;
    public string Name { get; private set; } = "";
    public string Notes { get; private set; } = "";
    public List<(string Title, string Url, string Folder)> Starters { get; } = new();

    private static string Text(JsonNode? v, string what, int max)
    {
        var s = v?.GetValue<string>() ?? "";
        if (s.Length > max || s.Any(char.IsControl)) throw new InvalidDataException($"\"{what}\" must be plain text of at most {max} characters");
        return s.Trim();
    }

    public static BrowserPack Parse(string json)
    {
        if (json.Length > MaxBytes) throw new InvalidDataException("browser file is larger than 64 KB");
        JsonNode? root;
        try { root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { MaxDepth = 6, CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (JsonException ex) { throw new InvalidDataException("browser file is not valid JSON: " + ex.Message); }
        if (root is not JsonObject o) throw new InvalidDataException("browser file must be a JSON object");
        var p = new BrowserPack();
        try
        {
            foreach (var (prop, value) in o)
            {
                switch (prop)
                {
                    case "name": p.Name = Text(value, prop, 60); break;
                    case "notes": p.Notes = Text(value, prop, 1000); break;
                    case "bookmarks":
                        if (value is not JsonArray arr) throw new InvalidDataException("\"bookmarks\" must be a list");
                        if (arr.Count > MaxStarters) throw new InvalidDataException($"at most {MaxStarters} starter bookmarks");
                        foreach (var item in arr)
                        {
                            if (item is not JsonObject b) throw new InvalidDataException("each bookmark must be an object");
                            if (b.Any(kv => kv.Key is not ("title" or "url" or "folder"))) throw new InvalidDataException("a bookmark may only have title, url and folder");
                            var title = Text(b["title"], "title", 80);
                            var url = Text(b["url"], "url", Portals.MaxUrl);
                            if (title.Length == 0) throw new InvalidDataException("a bookmark needs a title");
                            if (Portals.ValidateUrl(url) is { } why) throw new InvalidDataException($"bookmark “{title}”: {why}");
                            p.Starters.Add((title, url, AppData.NormalizeFolder(Text(b["folder"], "folder", 120))));
                        }
                        break;
                    default: throw new InvalidDataException($"unknown field “{prop}” (allowed: name, notes, bookmarks)");
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException) { throw new InvalidDataException("browser file has a value of the wrong type: " + ex.Message); }
        if (p.Name.Length == 0) throw new InvalidDataException("browser file needs a \"name\"");
        return p;
    }
}

/// <summary>Whether the portal browser is on, and the add-on's pack. The browser itself is app code (Views/BrowserView.cs).
/// Since 0.1.17 it is built in (DarkCipher0x: "i want it natively in the app"): on unless switched off in Settings → Integrations.
/// The add-on (ADR-032) still turns it on too and brings its starter bookmarks, so existing installs keep working.</summary>
public static class PortalBrowser
{
    /// <summary>Raised after the add-on is installed, enabled, disabled or removed (the sidebar shows or hides Portals).</summary>
    public static event Action? Changed;

    public static bool IsBrowser(AddonManifest m) => !string.IsNullOrEmpty(m.Browser);

    /// <summary>The enabled portal browser add-on whose files still match, or null.</summary>
    public static InstalledAddon? Active => AddonPackages.All.FirstOrDefault(a => a.Enabled && IsBrowser(a.Manifest));

    public static bool Enabled => AppSettings.Current.PortalBrowserBuiltIn || Active != null;

    /// <summary>Settings → Integrations → Portal browser.</summary>
    public static void SetBuiltIn(bool on)
    {
        if (AppSettings.Current.PortalBrowserBuiltIn == on) return;
        AppSettings.Current.PortalBrowserBuiltIn = on;
        AppSettings.Save();
        AuditLog.Write("you", "portals", on ? "portal browser switched on" : "portal browser switched off");
        try { Changed?.Invoke(); } catch (Exception ex) { Log.Write("Portal browser change", ex); }
    }

    /// <summary>Why portals can't be opened now (shown on the Portals screen and to the AI), or null.</summary>
    public const string OffText = "The portal browser is switched off (Settings → Integrations → Portal browser).";

    /// <summary>Reads an installed add-on's browser.json after verifying its files; a mismatch disables it (like a program add-on).</summary>
    public static BrowserPack? Pack(InstalledAddon a)
    {
        if (!IsBrowser(a.Manifest)) return null;
        if (AddonPackages.UseRefusal(a) != null) return null;   // 0.5.1 #7: blocked by policy
        if (AddonPackages.VerifyFiles(a) is { } tamper) { AddonPackages.SetEnabled(a, false, "files changed after install: " + tamper); return null; }
        try { return BrowserPack.Parse(AddonPackages.ReadInstalledText(a, a.Manifest.Browser)); }   // sealed at rest (ADR-035)
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { Log.Write("Portal browser add-on " + a.Id, ex); return null; }
    }

    /// <summary>Called by <see cref="AddonPackages"/> after install / enable / disable / remove.</summary>
    public static void Notify(InstalledAddon a)
    {
        if (!IsBrowser(a.Manifest)) return;
        try { Changed?.Invoke(); } catch (Exception ex) { Log.Write("Portal browser change", ex); }
    }
}

/// <summary>Bookmarks, profiles, certificate pins and the browser's policies (ADR-032). No UI here.</summary>
public static class Portals
{
    public const int MaxUrl = 2048;
    public const string ProfilePrefix = "dcx-";

    public static List<PortalBookmark> All => AppData.Current.Portals;
    public static PortalBookmark? Find(string? id) => id == null ? null : All.FirstOrDefault(b => b.Id == id);
    public static IEnumerable<PortalBookmark> ForHost(string hostId) => All.Where(b => b.HostId == hostId);

    /// <summary>Only http(s), absolute, no user:password@ in the address (the vault is the place for that), ≤ 2048 characters.</summary>
    public static string? ValidateUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "an address is required";
        if (url.Length > MaxUrl) return $"the address is longer than {MaxUrl} characters";
        if (url.Any(char.IsControl) || url.Contains(' ')) return "the address contains spaces or control characters";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return "not a full address (https://host:port/…)";
        if (u.Scheme is not ("https" or "http")) return $"only https:// and http:// addresses can be opened (not {u.Scheme}:)";
        if (u.UserInfo.Length > 0) return "user names and passwords don't belong in the address — keep them in the vault and use Fill sign-in";
        if (u.Host.Length == 0) return "the address has no host";
        return null;
    }

    /// <summary>"192.0.2.10" → "https://192.0.2.10/"; anything with a scheme is left alone.</summary>
    public static string Normalize(string typed)
    {
        var t = typed.Trim();
        if (t.Length == 0 || t.Contains("://") || t.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return t;
        return "https://" + t;
    }

    /// <summary>The environment a bookmark opens in: its own, else its host's, else none.</summary>
    public static string EnvironmentOf(PortalBookmark b) =>
        Envs.Normalize(b.Environment) is { Length: > 0 } e ? e : AppData.Current.FindHost(b.HostId) is { } h ? Envs.Normalize(h.Environment) : "";

    private static readonly Regex ProfileRx = new("^[A-Za-z0-9][A-Za-z0-9 _-]{0,39}$");
    public static string? ValidateProfile(string name) => name.Length == 0 || ProfileRx.IsMatch(name) ? null : "1–40 letters, digits, spaces, - or _ (starting with a letter or digit)";

    /// <summary>The WebView2 profile (cookie jar) for a bookmark: a named profile if it has one, else one per environment.
    /// Profile names are WebView2-safe (ASCII letters, digits, '-', '_') and always start with "dcx-".</summary>
    public static string ProfileFor(PortalBookmark b)
    {
        if (b.Profile.Length > 0 && ValidateProfile(b.Profile) == null) return ProfilePrefix + "p-" + b.Profile.Trim().Replace(' ', '_').ToLowerInvariant();
        var env = EnvironmentOf(b);
        return ProfilePrefix + (env.Length > 0 ? env.ToLowerInvariant() : "general");
    }

    /// <summary>A readable name for a profile id ("dcx-production" → "PRODUCTION", "dcx-p-home_lab" → "home lab").</summary>
    public static string ProfileLabel(string profile) =>
        profile.StartsWith(ProfilePrefix + "p-") ? profile[(ProfilePrefix.Length + 2)..].Replace('_', ' ')
        : profile.StartsWith(ProfilePrefix) ? (profile[ProfilePrefix.Length..] is "general" ? "general (no environment)" : profile[ProfilePrefix.Length..].ToUpperInvariant())
        : profile;

    /// <summary>Every profile a saved bookmark uses, plus the general one.</summary>
    public static IEnumerable<string> ProfilesInUse() => All.Select(ProfileFor).Append(ProfilePrefix + "general").Distinct().OrderBy(p => p);

    public static void Save() => AppData.Save();

    /// <summary>Why a bookmark can't be saved, or null.</summary>
    public static string? Validate(PortalBookmark b)
    {
        if (string.IsNullOrWhiteSpace(b.Title) || b.Title.Length > 80 || b.Title.Any(char.IsControl)) return "A title of 1–80 characters is required.";
        if (ValidateUrl(b.Url) is { } why) return char.ToUpper(why[0]) + why[1..] + ".";
        if (b.HostId != null && AppData.Current.FindHost(b.HostId) == null) return "The linked host no longer exists.";
        if (b.Environment.Length > 0 && !Envs.All.Contains(Envs.Normalize(b.Environment))) return "Unknown environment.";
        if (ValidateProfile(b.Profile.Trim()) is { } p) return "Profile: " + p + ".";
        if (b.CredentialId != null && AppData.Current.FindCredential(b.CredentialId) is not { Kind: CredentialKind.Password }) return "The sign-in credential must be a vault password.";
        if (b.Folder.Length > 120) return "The folder path is longer than 120 characters.";
        return null;
    }

    /// <summary>Adds or updates a bookmark (validated), saves the encrypted store and audits it.</summary>
    public static void Upsert(PortalBookmark b)
    {
        if (Validate(b) is { } why) throw new InvalidOperationException(why);
        b.Title = b.Title.Trim(); b.Url = b.Url.Trim(); b.Profile = b.Profile.Trim(); b.Folder = AppData.NormalizeFolder(b.Folder);
        b.Environment = Envs.Normalize(b.Environment);
        var isNew = Find(b.Id) == null;
        if (isNew) All.Add(b);
        Save();
        AuditLog.Write("you", "browser", isNew ? "portal added" : "portal changed", $"{b.Title} · {Display(b.Url)}" + (b.HostId != null ? " · host web UI" : ""));
    }

    public static void Delete(PortalBookmark b)
    {
        if (!All.Remove(b)) return;
        Save();
        AuditLog.Write("you", "browser", "portal removed", $"{b.Title} · {Display(b.Url)}");
    }

    // ---- address display ------------------------------------------------------------------------------------------

    private static readonly Regex SecretParam = new("^(token|ticket|access_token|id_token|refresh_token|auth|authorization|password|passwd|pwd|secret|key|apikey|api_key|sig|signature|session|sessionid|sid|code|csrf|csrftoken|vncticket|password_reset)$", RegexOptions.IgnoreCase);

    /// <summary>The address as shown: query values whose names look like secrets (token, ticket, password…) become "…", and
    /// a user:password@ part is dropped. Console and SSO links carry tokens; screen shares and screenshots shouldn't.</summary>
    public static string Display(string? url)
    {
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var u)) return url ?? "";
        var b = new UriBuilder(u) { UserName = "", Password = "" };
        if (u.Query.Length > 1)
            b.Query = string.Join("&", u.Query[1..].Split('&').Select(kv =>
            {
                var i = kv.IndexOf('=');
                var name = WebUtility.UrlDecode(i < 0 ? kv : kv[..i]);
                return i > 0 && SecretParam.IsMatch(name) ? kv[..i] + "=…" : kv;
            }));
        // Fragments (#v1:0:=qemu/100…, #/login) stay: Proxmox and SPA routes need them, and they're never sent anywhere.
        return b.Uri.ToString();
    }

    public static string HostPort(Uri u) => $"{u.Host.ToLowerInvariant()}:{u.Port}";

    // ---- certificates -----------------------------------------------------------------------------------------------

    public static WebCertPin? Pin(string hostPort) => AppData.Current.WebPins.FirstOrDefault(p => p.HostPort.Equals(hostPort, StringComparison.OrdinalIgnoreCase));

    /// <summary>The decision for an untrusted certificate (a chain-valid one never gets here): <see cref="TlsDecision.Accept"/>
    /// only when it is the one pinned for this host:port; <see cref="TlsDecision.Mismatch"/> when another one is pinned;
    /// otherwise <see cref="TlsDecision.NeedsTrust"/>. Exactly <see cref="TlsPin.Check"/> with "the chain failed".</summary>
    public static TlsDecision Decide(string hostPort, string sha256) =>
        TlsPin.Check(Pin(hostPort)?.Sha256 ?? "", sha256, System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors);

    public static void Trust(string hostPort, string sha256, string subject)
    {
        var p = Pin(hostPort);
        var old = p?.Sha256;
        if (p == null) AppData.Current.WebPins.Add(p = new WebCertPin { HostPort = hostPort });
        p.Sha256 = sha256.ToUpperInvariant(); p.Subject = subject.Length > 200 ? subject[..200] : subject; p.Trusted = DateTime.Now;
        Save();
        AuditLog.Write("you", "browser", old == null ? "web certificate trusted" : "web certificate replaced", $"{hostPort} · SHA-256 {TlsPin.Pretty(p.Sha256)}" + (old != null ? $" (was {TlsPin.Pretty(old)})" : ""));
    }

    public static void Forget(string hostPort)
    {
        if (Pin(hostPort) is not { } p) return;
        AppData.Current.WebPins.Remove(p);
        Save();
        AuditLog.Write("you", "browser", "web certificate forgotten", $"{hostPort} · SHA-256 {TlsPin.Pretty(p.Sha256)}");
    }

    /// <summary>SHA-256 of the certificate's DER bytes from WebView2's PEM text (the same value TlsPin shows).</summary>
    public static string ShaOfPem(string pem)
    {
        var b64 = Regex.Replace(pem, "-----(BEGIN|END) CERTIFICATE-----|\\s", "");
        return Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(b64)));
    }

    // ---- navigation policy -----------------------------------------------------------------------------------------

    /// <summary>What the page may navigate to: http(s), about:blank, and data:/blob: inside the page. file: (this PC's
    /// disks) and every other scheme (an app launch: ms-settings:, mailto:, a custom handler) are refused.</summary>
    public static bool NavigationAllowed(string? url, out string reason)
    {
        reason = "";
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var u)) { reason = "not a valid address"; return false; }
        switch (u.Scheme)
        {
            case "http" or "https" or "blob" or "data": return true;
            case "about" when url.Equals("about:blank", StringComparison.OrdinalIgnoreCase): return true;
            case "file": reason = "files on this PC can't be opened in the portal browser"; return false;
            default: reason = $"“{u.Scheme}:” links would start another program; the portal browser doesn't follow them"; return false;
        }
    }

    // ---- downloads --------------------------------------------------------------------------------------------------

    private static readonly string[] RiskyExt = { ".exe", ".msi", ".msix", ".appx", ".bat", ".cmd", ".com", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".hta", ".scr", ".pif", ".lnk", ".reg", ".dll", ".cpl", ".jar", ".iso", ".img", ".vhd", ".vhdx" };
    public static bool IsRisky(string fileName) => RiskyExt.Contains(Path.GetExtension(fileName).ToLowerInvariant());

    public static string DefaultDownloadFolder => Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), "Downloads");

    /// <summary>The folder downloads go to: the saved one when it still exists, else null (the user is asked to pick one).</summary>
    public static string? DownloadFolder => AppSettings.Current.PortalDownloadFolder is { Length: > 0 } f && Directory.Exists(f) ? f : null;

    /// <summary>A file name that is safe on Windows (no path, no reserved names, ≤ 120 characters) and unused in the folder.</summary>
    public static string SafeTarget(string folder, string suggested)
    {
        var name = Path.GetFileName((suggested ?? "").Replace('/', '\\'));
        name = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim(' ', '.');
        if (name.Length == 0) name = "download";
        var stem = Path.GetFileNameWithoutExtension(name); var ext = Path.GetExtension(name);
        if (Regex.IsMatch(stem, "^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])$", RegexOptions.IgnoreCase)) stem = "_" + stem;
        if (stem.Length + ext.Length > 120) stem = stem[..Math.Max(1, 120 - ext.Length)];
        var target = Path.Combine(folder, stem + ext);
        for (int i = 2; File.Exists(target) || Directory.Exists(target); i++) target = Path.Combine(folder, $"{stem} ({i}){ext}");
        return target;
    }

    // ---- sign-in -----------------------------------------------------------------------------------------------------

    /// <summary>The vault credential "Fill sign-in" would use: the bookmark's, else its host's when that is a password.</summary>
    public static Credential? SignInCredential(PortalBookmark b)
    {
        // 0.6.17 (ADR-096): may be kept in Bitwarden or Password Safe (read only when filling), as may the host's own link.
        var host = AppData.Current.FindHost(b.HostId);
        var c = AppData.Current.FindCredential(b.CredentialId) ?? AppData.Current.FindCredential(host?.CredentialId) ?? ExternalCredentials.LinkedTo(host);
        return c is { Kind: CredentialKind.Password } ? c : null;
    }

    /// <summary>Fill sign-in only on the bookmark's own origin (scheme, host and port): a page that redirected elsewhere
    /// (or a look-alike) never receives the password.</summary>
    public static string? SignInRefusal(PortalBookmark b, string? currentUrl, WebSecurity security)
    {
        if (!Uri.TryCreate(b.Url, UriKind.Absolute, out var want) || !Uri.TryCreate(currentUrl, UriKind.Absolute, out var now)) return "the page address is unknown";
        if (!string.Equals(want.Scheme, now.Scheme, StringComparison.OrdinalIgnoreCase) || !string.Equals(want.Host, now.Host, StringComparison.OrdinalIgnoreCase) || want.Port != now.Port)
            return $"this page is {now.Scheme}://{now.Host}:{now.Port}, not the bookmark's {want.Scheme}://{want.Host}:{want.Port}";
        if (security is WebSecurity.Untrusted or WebSecurity.Changed) return "the certificate is not trusted";
        return null;
    }

    // 0.6.20 (DarkCipher0x: "the "Fill sign-in" isnt working on the webpage"): sign-in forms are found inside open shadow
    // roots and same-origin frames too, a sign-in that asks for the user name first (Synology DSM, Microsoft, Google…)
    // gets it and then its password once that box appears, and a code box gets the TOTP code. The page is probed first,
    // so the password only enters a page that has a password box.

    /// <summary>The page's inputs (the document, its open shadow roots and same-origin frames, in order), and helpers.</summary>
    private const string FormKit = """
          const roots = [];
          const walk = r => { if (!r || roots.includes(r)) return; roots.push(r);
            for (const el of r.querySelectorAll('*')) { if (el.shadowRoot) walk(el.shadowRoot);
              if (el.tagName === 'IFRAME' || el.tagName === 'FRAME') { try { walk(el.contentDocument); } catch (e) { } } } };
          walk(document);
          const all = roots.flatMap(r => [...r.querySelectorAll('input')]);
          const vis = e => e.type !== 'hidden' && !!(e.offsetWidth || e.offsetHeight || e.getClientRects().length);
          const usable = e => vis(e) && !e.disabled && !e.readOnly;
          const words = e => [e.name, e.id, e.placeholder, e.getAttribute('aria-label'), e.getAttribute('autocomplete'), e.className].join(' ');
          const isPw = e => e.type === 'password' && usable(e);
          const texty = e => ['text', 'email', 'tel', ''].includes(e.type);
          const isUser = e => usable(e) && texty(e) && (/username|email/i.test(e.getAttribute('autocomplete') || '') || e.type === 'email'
            || /user|login|e-?mail|account|identifier|benutzer|utilisateur/i.test(words(e))) && !/search|otp|code|captcha/i.test(words(e));
          const isOtp = e => usable(e) && (texty(e) || e.type === 'number') && !e.value
            && (/one-time-code/i.test(e.getAttribute('autocomplete') || '') || /otp|totp|2fa|mfa|one.?time|verif|auth.?code|security.?code|passcode|token|\bcode\b/i.test(words(e)));
          const set = (e, v) => { const d = Object.getOwnPropertyDescriptor(Object.getPrototypeOf(e), 'value') || Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value');
            e.focus(); d.set.call(e, v);
            for (const t of ['input', 'change']) e.dispatchEvent(new Event(t, { bubbles: true }));
            e.dispatchEvent(new KeyboardEvent('keyup', { bubbles: true })); };
        """;

    /// <summary>What the page offers now, without any secret in the script: "password", "user", "code" or "none".</summary>
    public static string SignInProbeScript() => $$"""
        (() => {
        {{FormKit}}
          if (all.some(isPw)) return 'password';
          if (all.some(isOtp)) return 'code';
          if (all.some(isUser)) return 'user';
          return 'none';
        })()
        """;

    /// <summary>The script that fills the first visible password field and the user-name field before it. Values travel as
    /// JSON string literals, so nothing in a password can break out of the script.</summary>
    public static string SignInScript(string user, string password) => $$"""
        (() => {
          const u = {{JsonSerializer.Serialize(user)}}, p = {{JsonSerializer.Serialize(password)}};
        {{FormKit}}
          const pw = all.find(isPw);
          if (!pw) return 'no-password-field';
          let filledUser = false;
          if (u) {
            const before = all.slice(0, all.indexOf(pw)).reverse().find(e => usable(e) && texty(e) && !isOtp(e));
            if (before) { if (before.value !== u) set(before, u); filledUser = true; }
          }
          set(pw, p);
          return filledUser ? 'filled' : 'filled-password';
        })()
        """;

    /// <summary>The first step of a two-step sign-in: the user-name box only.</summary>
    public static string SignInUserScript(string user) => $$"""
        (() => {
          const u = {{JsonSerializer.Serialize(user)}};
        {{FormKit}}
          const box = all.find(isUser);
          if (!box || !u) return 'no-user-field';
          if (box.value !== u) set(box, u);
          return 'filled-user';
        })()
        """;

    /// <summary>The authenticator code into the page's code box.</summary>
    public static string SignInCodeScript(string code) => $$"""
        (() => {
          const c = {{JsonSerializer.Serialize(code)}};
        {{FormKit}}
          const box = all.find(isOtp);
          if (!box) return 'no-code-field';
          set(box, c);
          return 'filled-code';
        })()
        """;
}
