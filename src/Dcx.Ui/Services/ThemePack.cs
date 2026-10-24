using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DcxManager.Services;

namespace DcxManager.Addons;

// A theme definition (ADR-030), next to ThemeManager that paints with it (0.2.0 phase 6: the UI kit; the add-on
// package code that finds and verifies installed themes stays with the add-ons in ThemeAddons.cs).

/// <summary>A theme add-on's colours, UI font and header branding (ADR-030). Pure data: no program runs. Unknown keys, anything
/// that is not a #RRGGBB colour and any logo that isn't a small PNG are refused when the package is inspected, so an installed
/// theme can only recolour and rebrand the header, never inject.</summary>
public sealed class ThemePack
{
    /// <summary>Palette key → (dark, light). A missing side keeps the app's own colour for that mode.</summary>
    public Dictionary<string, (Color? Dark, Color? Light)> Palette { get; } = new(StringComparer.Ordinal);
    /// <summary>Accent keys this theme may set beside the palette: Accent, AccentHover, OnAccent, Brand.</summary>
    public static readonly string[] AccentKeys = { "Accent", "AccentHover", "OnAccent", "Brand" };
    /// <summary>A UI font family list ("Roboto, Segoe UI"); used only when installed and the user hasn't chosen a UI font.</summary>
    public string UiFont { get; private set; } = "";
    /// <summary>Shown instead of "DarkCipher ServerManagement" in the header and the window title; null = unchanged.</summary>
    public string? ProductName { get; private set; }
    /// <summary>Replaces the "darkcipher0x@servers:~$" line under the name; "" hides it; null = unchanged.</summary>
    public string? Tagline { get; private set; }
    /// <summary>A PNG in the package that replaces the header logo; null = the app's own logo.</summary>
    public string? Logo { get; private set; }
    /// <summary>A rounded plate behind the logo (for logos drawn for a light background); null = none.</summary>
    public Color? LogoBackground { get; private set; }
    /// <summary>The decoded logo (frozen), filled in by <see cref="ThemeAddons"/> from the verified installed file.</summary>
    public BitmapSource? LogoImage { get; internal set; }

    public const int MaxBytes = 64 * 1024;
    public const int MaxLogoBytes = 1 << 20;
    public const int MaxLogoPixels = 2048;

    public Color? Get(string key, bool dark) => Palette.TryGetValue(key, out var p) ? (dark ? p.Dark : p.Light) : null;

    /// <summary>Decodes a logo: PNG only (the PNG decoder is asked for explicitly, whatever the bytes claim), ≤ 1 MB and
    /// ≤ 2048 px a side, one frame. Throws <see cref="InvalidDataException"/> with a plain reason.</summary>
    public static BitmapSource DecodeLogo(byte[] png)
    {
        if (png.Length > MaxLogoBytes) throw new InvalidDataException("the logo is larger than 1 MB");
        if (png.Length < 8 || png[0] != 0x89 || png[1] != (byte)'P' || png[2] != (byte)'N' || png[3] != (byte)'G') throw new InvalidDataException("the logo must be a PNG file");
        try
        {
            var dec = new PngBitmapDecoder(new MemoryStream(png), BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            if (dec.Frames.Count != 1) throw new InvalidDataException("the logo must be a single image");
            var f = dec.Frames[0];
            if (f.PixelWidth is < 1 or > MaxLogoPixels || f.PixelHeight is < 1 or > MaxLogoPixels) throw new InvalidDataException($"the logo must be at most {MaxLogoPixels} × {MaxLogoPixels} pixels");
            f.Freeze();
            return f;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or OverflowException or InvalidOperationException)
        { throw new InvalidDataException("the logo is not a readable PNG: " + ex.Message); }
    }

    private static string Text(JsonNode? value, string prop, int max)
    {
        var s = value?.GetValue<string>() ?? "";
        if (s.Length > max || s.Any(char.IsControl)) throw new InvalidDataException($"\"{prop}\" must be plain text of at most {max} characters");
        return s.Trim();
    }

    /// <summary>Parses theme.json strictly. Returns the theme, or throws <see cref="InvalidDataException"/> with a plain reason.</summary>
    public static ThemePack Parse(string json)
    {
        if (json.Length > MaxBytes) throw new InvalidDataException("theme file is larger than 64 KB");
        JsonNode? root;
        try { root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { MaxDepth = 6, CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (JsonException ex) { throw new InvalidDataException("theme file is not valid JSON: " + ex.Message); }
        if (root is not JsonObject o) throw new InvalidDataException("theme file must be a JSON object");
        var t = new ThemePack();
        var allowed = ThemeManager.PaletteKeys.Concat(AccentKeys).ToHashSet(StringComparer.Ordinal);
        Color Hex(JsonNode? v, string what)
        {
            var s = v?.GetValue<string>() ?? "";
            if (s.Length != 7 || s[0] != '#') throw new InvalidDataException($"“{what}” must be #RRGGBB");
            return ThemeManager.ParseHex(s) ?? throw new InvalidDataException($"“{what}” must be #RRGGBB");
        }
        try
        {
            foreach (var (prop, value) in o)
            {
                switch (prop)
                {
                    case "uiFont":
                        var f = value?.GetValue<string>() ?? "";
                        if (f.Length > 120 || f.IndexOfAny(new[] { '"', '\'', '<', '>', ';', '\n', '\r' }) >= 0) throw new InvalidDataException("uiFont must be a short font list like \"Roboto, Segoe UI\"");
                        t.UiFont = f.Trim();
                        break;
                    case "colors":
                        if (value is not JsonObject colors) throw new InvalidDataException("\"colors\" must be an object");
                        foreach (var (key, pair) in colors)
                        {
                            if (!allowed.Contains(key)) throw new InvalidDataException($"unknown colour key “{key}”");
                            if (pair is not JsonObject dl) throw new InvalidDataException($"“{key}” must be {{\"dark\": \"#RRGGBB\", \"light\": \"#RRGGBB\"}}");
                            if (dl.Any(kv => kv.Key is not ("dark" or "light"))) throw new InvalidDataException($"“{key}” may only have \"dark\" and \"light\"");
                            t.Palette[key] = (dl["dark"] is { } d ? Hex(d, key + ".dark") : null, dl["light"] is { } l ? Hex(l, key + ".light") : null);
                        }
                        break;
                    case "productName":
                        t.ProductName = Text(value, prop, 60);
                        if (t.ProductName.Length == 0) throw new InvalidDataException("\"productName\" can't be empty");
                        break;
                    case "tagline":
                        t.Tagline = Text(value, prop, 60);
                        break;
                    case "logo":
                        var logo = Text(value, prop, 200);
                        if (PackagePath.SafeRelative(logo) is not { } rel || !rel.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("\"logo\" must be a .png file in the package");
                        t.Logo = rel;
                        break;
                    case "logoBackground":
                        t.LogoBackground = Hex(value, prop);
                        break;
                    case "name" or "notes":
                        Text(value, prop, 1000);
                        break;
                    default:
                        throw new InvalidDataException($"unknown field “{prop}” (allowed: colors, uiFont, productName, tagline, logo, logoBackground, name, notes)");
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException) { throw new InvalidDataException("theme file has a value of the wrong type: " + ex.Message); }
        // A theme may rebrand without recolouring (0.4.0: a company logo and name over the house colours) — but it must do something.
        if (t.Palette.Count == 0 && t.ProductName == null && t.Tagline == null && t.Logo == null && t.LogoBackground == null) throw new InvalidDataException("theme file sets no colours and no branding (productName, tagline, logo)");
        return t;
    }
}
