using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DcxManager.Addons;

namespace DcxManager.Services;

/// <summary>
/// The branding set in Settings → Appearance → Branding &amp; colours (0.4.0, DarkCipher0x: "people should be able to make it fit
/// their org"): the same theme.json a theme add-on carries (<see cref="ThemePack"/>, so every rule and limit is the
/// add-on's), kept in the encrypted settings and applied like an add-on when none is chosen. A company can push the text
/// by Group Policy (BrandingTheme) with a logo file on a share (BrandingLogoPath), or export it as a theme add-on
/// (.dcxaddon) for its users.
/// </summary>
public static class Branding
{
    private static (string Key, ThemePack Pack)? _cache;

    /// <summary>The branding to paint with, or null when none is set or it doesn't parse (then the app's own look; the
    /// reason is logged once per change).</summary>
    public static ThemePack? Active()
    {
        var s = AppSettings.Current;
        if (s.BrandingTheme.Trim().Length == 0) return null;
        var key = s.BrandingTheme + "|" + s.BrandingLogoPath + "|" + s.BrandingLogoPng.Length;
        if (_cache is { } c && c.Key == key) return c.Pack;
        try
        {
            var pack = ThemePack.Parse(s.BrandingTheme);
            if (LogoBytes() is { } png) pack.LogoImage = ThemePack.DecodeLogo(png);
            _cache = (key, pack);
            return pack;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or FormatException)
        {
            if (_cache?.Key != key) Log.Write("Branding", ex);
            _cache = null;
            return null;
        }
    }

    /// <summary>The logo as bytes: the one picked in Settings first, else the policy's file (≤ 1 MB); null = none.</summary>
    public static byte[]? LogoBytes()
    {
        var s = AppSettings.Current;
        if (s.BrandingLogoPng.Length > 0) return Convert.FromBase64String(s.BrandingLogoPng);
        var path = s.BrandingLogoPath.Trim();
        if (path.Length == 0) return null;
        if (!File.Exists(path)) throw new FileNotFoundException("the branding logo file is missing: " + path);
        if (new FileInfo(path).Length > ThemePack.MaxLogoBytes) throw new InvalidDataException("the branding logo is larger than 1 MB: " + path);
        return File.ReadAllBytes(path);
    }

    /// <summary>What the editor shows: one row per palette / accent key, dark and light hex without '#' ("" = the app's own).</summary>
    public sealed class Draft
    {
        public string ProductName { get; set; } = "";
        public string Tagline { get; set; } = "";
        public bool TaglineSet { get; set; }
        public string UiFont { get; set; } = "";
        public string LogoBackground { get; set; } = "";
        public Dictionary<string, (string Dark, string Light)> Colours { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>The saved branding as a draft (empty when none).</summary>
    public static Draft Current()
    {
        var d = new Draft();
        var s = AppSettings.Current;
        if (s.BrandingTheme.Trim().Length == 0) return d;
        try
        {
            if (JsonNode.Parse(s.BrandingTheme) is not JsonObject o) return d;
            d.ProductName = o["productName"]?.GetValue<string>() ?? "";
            if (o["tagline"] is { } tl) { d.TaglineSet = true; d.Tagline = tl.GetValue<string>() ?? ""; }
            d.UiFont = o["uiFont"]?.GetValue<string>() ?? "";
            d.LogoBackground = (o["logoBackground"]?.GetValue<string>() ?? "").TrimStart('#');
            if (o["colors"] is JsonObject colours)
                foreach (var (k, v) in colours)
                    if (v is JsonObject dl) d.Colours[k] = ((dl["dark"]?.GetValue<string>() ?? "").TrimStart('#'), (dl["light"]?.GetValue<string>() ?? "").TrimStart('#'));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { Log.Write("Branding read", ex); }
        return d;
    }

    /// <summary>The theme.json text for a draft (validated by <see cref="ThemePack.Parse"/> before it is returned; throws
    /// <see cref="InvalidDataException"/> with the plain reason). <paramref name="logo"/> names the logo file inside an
    /// exported package; the settings never need it (the logo bytes live beside the text).</summary>
    public static string Compose(Draft d, string? logo = null)
    {
        var o = new JsonObject();
        if (d.ProductName.Trim().Length > 0) o["productName"] = d.ProductName.Trim();
        if (d.TaglineSet) o["tagline"] = d.Tagline.Trim();
        if (d.UiFont.Trim().Length > 0) o["uiFont"] = d.UiFont.Trim();
        if (d.LogoBackground.Trim().Length > 0) o["logoBackground"] = "#" + d.LogoBackground.Trim().TrimStart('#').ToUpperInvariant();
        if (logo != null) o["logo"] = logo;
        var colours = new JsonObject();
        foreach (var (key, (dark, light)) in d.Colours)
        {
            if (dark.Trim().Length == 0 && light.Trim().Length == 0) continue;
            var pair = new JsonObject();
            if (dark.Trim().Length > 0) pair["dark"] = "#" + dark.Trim().TrimStart('#').ToUpperInvariant();
            if (light.Trim().Length > 0) pair["light"] = "#" + light.Trim().TrimStart('#').ToUpperInvariant();
            colours[key] = pair;
        }
        if (colours.Count > 0) o["colors"] = colours;
        var json = o.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        ThemePack.Parse(json.Replace("\"logo\":", "\"logo\":"));   // the same checks an add-on's theme gets (keys, #RRGGBB, lengths)
        return json;
    }

    /// <summary>Saves a draft (and an optional logo) as the branding and repaints. An empty draft clears it.</summary>
    public static void Apply(Draft d, byte[]? logoPng)
    {
        var s = AppSettings.Current;
        bool empty = d.ProductName.Trim().Length == 0 && !d.TaglineSet && d.UiFont.Trim().Length == 0 && d.LogoBackground.Trim().Length == 0 && logoPng == null
                     && d.Colours.All(c => c.Value.Dark.Trim().Length == 0 && c.Value.Light.Trim().Length == 0);
        if (empty) { Clear(); return; }
        if (logoPng != null) ThemePack.DecodeLogo(logoPng);   // PNG, ≤ 1 MB, ≤ 2048 px — or a plain reason
        s.BrandingTheme = Compose(d, logoPng != null ? "logo.png" : null);
        s.BrandingLogoPng = logoPng != null ? Convert.ToBase64String(logoPng) : "";
        AppSettings.Save();
        _cache = null;
        AuditLog.Write("you", "appearance", "branding applied", $"{d.Colours.Count(c => c.Value.Dark.Length + c.Value.Light.Length > 0)} colour(s){(d.ProductName.Length > 0 ? ", name " + d.ProductName : "")}{(logoPng != null ? ", logo" : "")}");
    }

    public static void Clear()
    {
        var s = AppSettings.Current;
        if (s.BrandingTheme.Length == 0 && s.BrandingLogoPng.Length == 0) return;
        s.BrandingTheme = ""; s.BrandingLogoPng = "";
        AppSettings.Save();
        _cache = null;
        AuditLog.Write("you", "appearance", "branding cleared");
    }

    /// <summary>Everything an add-on package needs: addon.json (API 2, theme only — nothing runs), theme.json and the logo.
    /// Installable on any copy of the app through Add-ons → Install; the id is lower-case letters, digits, dots and hyphens.</summary>
    public static byte[] ExportAddon(Draft d, byte[]? logoPng, string id, string name, string publisher, string version = "1.0.0")
    {
        var theme = Compose(d, logoPng != null ? "logo.png" : null);
        var manifest = new JsonObject
        {
            ["api"] = 2, ["id"] = id.Trim().ToLowerInvariant(), ["name"] = name.Trim(), ["version"] = version, ["publisher"] = publisher.Trim(),
            ["description"] = $"Branding and colours for {AppInfo.ProductName}, exported from Settings → Appearance.", ["theme"] = "theme.json",
        };
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            Write(zip, "addon.json", Encoding.UTF8.GetBytes(manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));
            Write(zip, "theme.json", Encoding.UTF8.GetBytes(theme));
            if (logoPng != null) Write(zip, "logo.png", logoPng);
        }
        return ms.ToArray();
    }

    private static void Write(ZipArchive zip, string name, byte[] bytes)
    {
        using var s = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        s.Write(bytes);
    }

    /// <summary>Reads a theme.json (an add-on's, or one exported here) into a draft; throws <see cref="InvalidDataException"/>.</summary>
    public static Draft Import(string json)
    {
        var pack = ThemePack.Parse(json);
        var d = new Draft { ProductName = pack.ProductName ?? "", TaglineSet = pack.Tagline != null, Tagline = pack.Tagline ?? "", UiFont = pack.UiFont, LogoBackground = pack.LogoBackground is { } lb ? $"{lb.R:X2}{lb.G:X2}{lb.B:X2}" : "" };
        foreach (var (k, (dark, light)) in pack.Palette) d.Colours[k] = (dark is { } dc ? $"{dc.R:X2}{dc.G:X2}{dc.B:X2}" : "", light is { } lc ? $"{lc.R:X2}{lc.G:X2}{lc.B:X2}" : "");
        return d;
    }

    /// <summary>Resets the parse cache (the self-test changes the setting directly).</summary>
    internal static void ForgetForTest() => _cache = null;
}
