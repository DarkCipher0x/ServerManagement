using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace DcxManager.Services;

public enum ThemeMode { System, Dark, Light }

/// <summary>Swaps the app's colour brushes between dark and light and darkens the window title bar (the DarkCipher house theme, ADR-038).</summary>
public static class ThemeManager
{
    /// <summary>darkcipher0x "terminal" palette (teal on black). Dark = neon terminal; Light = ink on paper. Accent-derived colours are computed.</summary>
    private static readonly Dictionary<string, (string Dark, string Light)> Palette = new()
    {
        ["Bg"]          = ("#050909", "#F1F6F6"),
        ["Surface"]     = ("#0B1314", "#E3EDED"),
        ["Input"]       = ("#020505", "#FAFDFD"),
        ["Fg"]          = ("#BCD8D5", "#08191A"),  // 0.2.2: dialled back from #D2F2EF (glare on near-black), 13:1
        ["Muted"]       = ("#5A8986", "#4E6E6C"),
        ["Line"]        = ("#16302F", "#BCD2D1"),
        ["Hover"]       = ("#0F2122", "#D5E5E5"),
        ["Track"]       = ("#0F2122", "#CFDFDF"),
        ["ScrollThumb"] = ("#234746", "#A3BEBD"),
        ["HeaderBg"]    = ("#000000", "#061010"),
        ["HeaderFg"]    = ("#BCD8D5", "#E2F4F2"),
        ["Good"]        = ("#36D27A", "#137A3F"),
        ["Warn"]        = ("#E8C547", "#8A6A00"),
        ["Bad"]         = ("#FF5F6D", "#B3202E"),
        // The DarkCipher house theme's extra keys (0.2.1, shared across the DarkCipher Systems apps): elevated surfaces, strong text and
        // borders, and the information / experimental / critical / unknown status colours.
        ["Surface2"]    = ("#0E1A1B", "#EBF3F3"),
        ["FgStrong"]    = ("#D6EEEB", "#000F10"),  // 0.2.2: from #EAFBF9
        ["LineStrong"]  = ("#234746", "#93B2B0"),
        ["Info"]        = ("#5B9BFF", "#1F4FB0"),
        ["Experimental"] = ("#B58CFF", "#6A3BC2"),
        ["Critical"]    = ("#FF4D5E", "#B3202E"),
        ["Unknown"]     = ("#6F8584", "#4E6463"),
        // Environment labels (ADR-017): always printed with their text; text on them uses "Bg" (dark on bright / light on deep).
        // Environment labels (DarkCipher0x, 0.3.0: red and yellow read as errors and warnings): their own hues, none of them a
        // status colour — PRODUCTION fuchsia, TEST sky, DEVELOPMENT violet, LAB slate. Still the same under every theme.
        ["EnvProd"]     = ("#F472D0", "#A21C83"),
        ["EnvTest"]     = ("#5CC8F5", "#0B6A9C"),
        ["EnvLab"]      = ("#9AA7C7", "#4A5878"),
        ["EnvDev"]      = ("#B49BFF", "#6233C8"),
        ["EnvLocal"]    = ("#7FA6A3", "#3F5F5D"),
        ["EnvOther"]    = ("#6F8584", "#4E6463"),
    };

    public const string DefaultAccent = AppSettings.DefaultAccent;   // darkcipher0x brand teal

    /// <summary>The palette keys a theme add-on may replace (ADR-030). Environment labels are not among them: PRODUCTION
    /// red and the other ADR-017 colours are a safety signal and look the same under every theme.</summary>
    public static IEnumerable<string> PaletteKeys => Palette.Keys.Where(k => !k.StartsWith("Env", StringComparison.Ordinal));

    /// <summary>The theme add-on painting the app now, or null for the app's own palette.</summary>
    public static Addons.InstalledAddon? ActiveTheme { get; private set; }
    /// <summary>Its colours and header branding (product name, tagline, logo), or null.</summary>
    public static Addons.ThemePack? ActivePack { get; private set; }

    /// <summary>The installed theme add-on to paint with and its theme, or (null, null): the app registers how to read them
    /// (0.2.0 phase 6: the add-ons sit above this kit).</summary>
    public static Func<(Addons.InstalledAddon? Addon, Addons.ThemePack? Pack)> ThemeSource { get; set; } = () => (null, null);

    /// <summary>The product name to show: a theme add-on's, else the app's own.</summary>
    public static string ProductName => ActivePack?.ProductName ?? AppInfo.DisplayName;

    public static ThemeMode Mode { get; private set; } = ThemeMode.System;
    public static bool IsDark { get; private set; }
    public static event Action? Changed;

    // ---- presets (0.2.1: the DarkCipher house theme, the same presets as the other DarkCipher Systems apps) ---------------------------------

    /// <summary>A built-in theme pack: its id, name and look; whether it has its own light palette (else light mode uses
    /// DarkCipher Light) or is always dark; its own accent (dark mode) and, optionally, the accent painted in light mode
    /// while that accent is in use (else the shared rule darkens it).</summary>
    public sealed record ThemePresetInfo(string Id, string Name, string Description, bool HasLight, bool AlwaysDark, string Accent, string? LightAccent = null)
    {
        /// <summary>"Dark + light", "Dark only" or "Always dark", for the pickers.</summary>
        public string Modes => AlwaysDark ? "Always dark" : HasLight ? "Dark + light" : "Dark only";
    }

    /// <summary>The theme packs offered in Settings → Appearance (0.6.9, DarkCipher0x: "normal looking colours like dark and light
    /// mode theme packs ... default to my darkcipher theme"). The DarkCipher packs first, then the everyday ones, each with
    /// its own dark and light palette and accent. The mode (System / Dark / Light) picks the palette.</summary>
    public static readonly IReadOnlyList<ThemePresetInfo> Presets = new ThemePresetInfo[]
    {
        new("darkcipher", "DarkCipher (default)", "The DarkCipher house theme: teal on near-black; DarkCipher Light in light mode.", true, false, DefaultAccent),
        new("graphite", "DarkCipher Graphite", "Neutral graphite surfaces with the DarkCipher accent (light mode uses DarkCipher Light).", false, false, DefaultAccent),
        new("teal", "DarkCipher Teal", "Deeper teal-tinted surfaces; the most saturated DarkCipher look (light mode uses DarkCipher Light).", false, false, DefaultAccent),
        new("highcontrast", "DarkCipher High Contrast", "Pure black, white text, strong borders and brighter status colours (always dark).", false, true, DefaultAccent),
        new("neutral", "Neutral", "Plain greys and a blue accent, like Windows 11: charcoal in dark mode, white and light grey in light mode.", true, false, "4CC2FF", "005FB8"),
        new("slate", "Slate", "Cool blue-grey surfaces with an indigo accent: deep navy in dark mode, crisp white in light mode.", true, false, "818CF8", "4F46E5"),
        new("arctic", "Arctic", "Soft polar greys and frost blue (Nord-style): muted and easy on the eyes in both modes.", true, false, "88C0D0", "5E81AC"),
    };

    /// <summary>A pack by id (the default for an unknown one).</summary>
    public static ThemePresetInfo Preset(string? id) => Presets.FirstOrDefault(p => p.Id == id) ?? Presets[0];

    /// <summary>Base colours of the other dark presets (the DarkCipher preset is <see cref="Palette"/>'s dark column).</summary>
    private static readonly Dictionary<string, Dictionary<string, string>> DarkPresets = new()
    {
        ["highcontrast"] = new()
        {
            ["Bg"] = "000000", ["Surface"] = "000000", ["Surface2"] = "0A0F0F", ["Input"] = "000000", ["Fg"] = "FFFFFF", ["FgStrong"] = "FFFFFF",
            ["Muted"] = "C7DAD8", ["Line"] = "6F9C99", ["LineStrong"] = "B9E6E2", ["Hover"] = "102A2A", ["Track"] = "102A2A", ["ScrollThumb"] = "8FBFBB",
            ["HeaderBg"] = "000000", ["HeaderFg"] = "FFFFFF", ["Good"] = "4CFF9A", ["Warn"] = "FFD84D", ["Bad"] = "FF7A86",
            ["Info"] = "8CB8FF", ["Experimental"] = "D2B8FF", ["Critical"] = "FF6B78", ["Unknown"] = "B5C4C3",
        },
        ["graphite"] = new()
        {
            ["Bg"] = "0A0B0C", ["Surface"] = "121416", ["Surface2"] = "171A1D", ["Input"] = "070809", ["Fg"] = "C4CBCE", ["FgStrong"] = "DDE2E4",
            ["Muted"] = "7D8B91", ["Line"] = "23292D", ["LineStrong"] = "343C41", ["Hover"] = "1A1F22", ["Track"] = "1A1F22", ["ScrollThumb"] = "343C41",
            ["HeaderBg"] = "000000", ["HeaderFg"] = "C4CBCE", ["Unknown"] = "7D8B91",
        },
        ["teal"] = new()
        {
            ["Bg"] = "031111", ["Surface"] = "071D1E", ["Surface2"] = "0A2627", ["Input"] = "010909", ["Fg"] = "B9DDD9", ["FgStrong"] = "D4F0EC",
            ["Muted"] = "5FA39E", ["Line"] = "134240", ["LineStrong"] = "1F5654", ["Hover"] = "0B2C2D", ["Track"] = "0B2C2D", ["ScrollThumb"] = "1F5654",
            ["HeaderBg"] = "000808", ["HeaderFg"] = "B9DDD9", ["Unknown"] = "6F9894",
        },
        // The everyday packs (0.6.9): every base key set, so nothing of the DarkCipher look shows through.
        ["neutral"] = new()
        {
            ["Bg"] = "202020", ["Surface"] = "2B2B2B", ["Surface2"] = "323232", ["Input"] = "1C1C1C", ["Fg"] = "E4E4E4", ["FgStrong"] = "FFFFFF",
            ["Muted"] = "A0A0A0", ["Line"] = "3A3A3A", ["LineStrong"] = "4F4F4F", ["Hover"] = "383838", ["Track"] = "333333", ["ScrollThumb"] = "5C5C5C",
            ["HeaderBg"] = "1A1A1A", ["HeaderFg"] = "E4E4E4", ["Good"] = "6CCB5F", ["Warn"] = "FCE100", ["Bad"] = "FF99A4",
            ["Info"] = "60CDFF", ["Experimental"] = "C3A6FF", ["Critical"] = "FF7B7B", ["Unknown"] = "A0A0A0",
        },
        ["slate"] = new()
        {
            ["Bg"] = "0F172A", ["Surface"] = "1E293B", ["Surface2"] = "243247", ["Input"] = "0B1222", ["Fg"] = "E2E8F0", ["FgStrong"] = "F8FAFC",
            ["Muted"] = "94A3B8", ["Line"] = "334155", ["LineStrong"] = "475569", ["Hover"] = "273449", ["Track"] = "273449", ["ScrollThumb"] = "475569",
            ["HeaderBg"] = "0B1120", ["HeaderFg"] = "E2E8F0", ["Good"] = "4ADE80", ["Warn"] = "FACC15", ["Bad"] = "F87171",
            ["Info"] = "60A5FA", ["Experimental"] = "C084FC", ["Critical"] = "FB7185", ["Unknown"] = "94A3B8",
        },
        ["arctic"] = new()
        {
            ["Bg"] = "2E3440", ["Surface"] = "3B4252", ["Surface2"] = "434C5E", ["Input"] = "272C36", ["Fg"] = "E5E9F0", ["FgStrong"] = "ECEFF4",
            ["Muted"] = "A9B3C5", ["Line"] = "434C5E", ["LineStrong"] = "4C566A", ["Hover"] = "3F4758", ["Track"] = "3B4252", ["ScrollThumb"] = "4C566A",
            ["HeaderBg"] = "242933", ["HeaderFg"] = "E5E9F0", ["Good"] = "A3BE8C", ["Warn"] = "EBCB8B", ["Bad"] = "D08770",
            ["Info"] = "81A1C1", ["Experimental"] = "B48EAD", ["Critical"] = "E0707A", ["Unknown"] = "A9B3C5",
        },
    };

    /// <summary>Light palettes of the packs that have one (DarkCipher's is <see cref="Palette"/>'s light column; packs
    /// without one use it too). Light packs get a light header, like ordinary Windows apps.</summary>
    private static readonly Dictionary<string, Dictionary<string, string>> LightPresets = new()
    {
        ["neutral"] = new()
        {
            ["Bg"] = "F3F3F3", ["Surface"] = "FFFFFF", ["Surface2"] = "F9F9F9", ["Input"] = "FFFFFF", ["Fg"] = "1B1B1B", ["FgStrong"] = "000000",
            ["Muted"] = "5C5C5C", ["Line"] = "E0E0E0", ["LineStrong"] = "C2C2C2", ["Hover"] = "EAEAEA", ["Track"] = "E5E5E5", ["ScrollThumb"] = "B3B3B3",
            ["HeaderBg"] = "EBEBEB", ["HeaderFg"] = "1B1B1B", ["Good"] = "0F7B0F", ["Warn"] = "8F5A00", ["Bad"] = "C42B1C",
            ["Info"] = "005FB8", ["Experimental"] = "7A3FC2", ["Critical"] = "B3201E", ["Unknown"] = "6B6B6B",
        },
        ["slate"] = new()
        {
            ["Bg"] = "F8FAFC", ["Surface"] = "FFFFFF", ["Surface2"] = "F1F5F9", ["Input"] = "FFFFFF", ["Fg"] = "0F172A", ["FgStrong"] = "020617",
            ["Muted"] = "475569", ["Line"] = "E2E8F0", ["LineStrong"] = "CBD5E1", ["Hover"] = "EEF2F7", ["Track"] = "E2E8F0", ["ScrollThumb"] = "94A3B8",
            ["HeaderBg"] = "F1F5F9", ["HeaderFg"] = "0F172A", ["Good"] = "15803D", ["Warn"] = "A16207", ["Bad"] = "B91C1C",
            ["Info"] = "1D4ED8", ["Experimental"] = "7E22CE", ["Critical"] = "BE123C", ["Unknown"] = "64748B",
        },
        ["arctic"] = new()
        {
            ["Bg"] = "ECEFF4", ["Surface"] = "F8F9FB", ["Surface2"] = "E5E9F0", ["Input"] = "FFFFFF", ["Fg"] = "2E3440", ["FgStrong"] = "242933",
            ["Muted"] = "4C566A", ["Line"] = "D8DEE9", ["LineStrong"] = "C2CAD6", ["Hover"] = "E2E7EF", ["Track"] = "D8DEE9", ["ScrollThumb"] = "A9B3C4",
            ["HeaderBg"] = "E5E9F0", ["HeaderFg"] = "2E3440", ["Good"] = "4F7A3A", ["Warn"] = "8A6100", ["Bad"] = "A3404A",
            ["Info"] = "3B6A9E", ["Experimental"] = "8A5A84", ["Critical"] = "A3303C", ["Unknown"] = "5E6779",
        },
    };

    /// <summary>Accent choices offered in Settings (each reaches 3:1 against every preset's background, dark and light as
    /// painted).</summary>
    public static readonly IReadOnlyList<(string Name, string Hex)> AccentSwatches = new[]
    {
        ("DarkCipher teal", DefaultAccent), ("Cyan", "3FB6FF"), ("Green", "36D27A"), ("Violet", "B58CFF"), ("Amber", "E8C547"), ("Rose", "FF6B9A"),
    };

    public static readonly string[] StatusIntensities = { "subtle", "standard", "strong" };
    public static readonly int[] CornerChoices = { 0, 2, 4 };
    public static readonly string[] MotionChoices = { "windows", "standard", "reduced" };

    /// <summary>The preset painting the app now: the setting, or High Contrast while Windows high contrast is on and the
    /// user follows it.</summary>
    public static string EffectivePreset { get; private set; } = "darkcipher";

    /// <summary>Corrects out-of-range appearance values (an older or hand-edited settings file can't break the look).
    /// Returns what was corrected.</summary>
    public static List<string> Normalize(AppSettings s)
    {
        var fixes = new List<string>();
        if (Presets.All(p => p.Id != s.ThemePreset)) { fixes.Add($"theme preset “{s.ThemePreset}”"); s.ThemePreset = "darkcipher"; }
        if (!StatusIntensities.Contains(s.StatusIntensity)) { fixes.Add($"status intensity “{s.StatusIntensity}”"); s.StatusIntensity = "standard"; }
        if (!CornerChoices.Contains(s.Corners)) { fixes.Add($"corners {s.Corners}"); s.Corners = 2; }
        if (!MotionChoices.Contains(s.Motion)) { fixes.Add($"animation “{s.Motion}”"); s.Motion = "windows"; }
        if (s.Theme is not ("System" or "Dark" or "Light")) { fixes.Add($"theme mode “{s.Theme}”"); s.Theme = "System"; }
        if (ParseHex(s.AccentColor) == null) { fixes.Add($"accent “{s.AccentColor}”"); s.AccentColor = DefaultAccent; }
        return fixes;
    }

    public static void Apply(ThemeMode mode)
    {
        var s = AppSettings.Current;
        Normalize(s);
        Mode = mode;
        EffectivePreset = s.FollowWindowsHighContrast && SystemParameters.HighContrast ? "highcontrast" : s.ThemePreset;
        IsDark = Preset(EffectivePreset).AlwaysDark || mode == ThemeMode.Dark || (mode == ThemeMode.System && SystemPrefersDark());

        // A theme add-on (ADR-030) replaces any colour it names; everything it leaves out keeps the app's own value.
        var (chosen, theme) = ThemeSource();
        ActiveTheme = theme != null ? chosen : null;
        ActivePack = theme;
        var res = Application.Current.Resources;
        var colours = new Dictionary<string, Color>();
        foreach (var key in Palette.Keys) colours[key] = theme?.Get(key, IsDark) ?? PresetColour(EffectivePreset, key, IsDark);
        // A theme add-on that names the base colours but not the house theme's extra keys gets them derived from its own
        // colours, so its surfaces and text stay consistent (the first sample theme predates these keys).
        if (theme != null)
        {
            Color Derive(string key, Func<Color> make) => theme.Get(key, IsDark) ?? make();
            colours["Surface2"] = Derive("Surface2", () => Mix(colours["Surface"], colours["Fg"], 0.03));
            colours["FgStrong"] = Derive("FgStrong", () => Mix(colours["Fg"], IsDark ? Colors.White : Colors.Black, 0.5));
            colours["LineStrong"] = Derive("LineStrong", () => Mix(colours["Line"], colours["Fg"], IsDark ? 0.08 : 0.2));
        }
        // Status intensity (Subtle / Standard / Strong), as in the other DarkCipher Systems apps.
        foreach (var key in new[] { "Good", "Warn", "Bad", "Info", "Critical" })
            colours[key] = s.StatusIntensity switch
            {
                "subtle" => Mix(colours[key], colours["Muted"], 0.30),
                "strong" => IsDark ? Lighten(colours[key], 0.18) : Darken(colours[key], 0.18),
                _ => colours[key],
            };
        foreach (var (key, c) in colours) res[key] = Frozen(c);

        var brand = ParseHex(s.AccentColor) ?? ParseHex(DefaultAccent)!.Value;
        var bg = colours["Bg"];
        var accent = theme?.Get("Accent", IsDark) ?? AccentFor(brand, IsDark, EffectivePreset);
        res["Accent"] = Frozen(accent);
        res["AccentHover"] = Frozen(theme?.Get("AccentHover", IsDark) ?? (IsDark ? Lighten(accent, 0.25) : Darken(accent, 0.2)));
        res["AccentDim"] = Frozen(Mix(bg, accent, IsDark ? 0.35 : 0.30));
        res["Brand"] = Frozen(theme?.Get("Brand", IsDark) ?? brand);
        res["OnAccent"] = Frozen(theme?.Get("OnAccent", IsDark) ?? (Luminance(accent) > 0.5 ? Colors.Black : Colors.White));
        res["Selection"] = Frozen(Mix(bg, accent, IsDark ? 0.18 : 0.20));
        res["HeaderMuted"] = Frozen(Mix(colours["HeaderBg"], colours["HeaderFg"], 0.62));
        res["FgDisabled"] = Frozen(Mix(colours["Surface"], colours["Muted"], 0.62));
        res["AltRow"] = Frozen(Mix(colours["Surface"], bg, 0.45));
        res["DangerLine"] = Frozen(Mix(colours["Surface"], colours["Bad"], 0.40));

        foreach (Window w in Application.Current.Windows) ApplyTitleBar(w);
        Changed?.Invoke();
    }

    /// <summary>The accent as painted: a pack's own accent in light mode is its light accent; other neon accents are
    /// darkened on light backgrounds (the shared DarkCipher rule).</summary>
    internal static Color AccentFor(Color brand, bool dark, string preset)
    {
        if (dark) return brand;
        var pack = Preset(preset);
        if (pack.LightAccent != null && ParseHex(pack.Accent) == brand && ParseHex(pack.LightAccent) is { } own) return own;
        return Darken(brand, Luminance(brand) > 0.45 ? 0.42 : 0.1);
    }

    /// <summary>WCAG 2.2 contrast ratio of two colours (1–21).</summary>
    public static double Contrast(Color a, Color b)
    {
        static double L(byte v) { var x = v / 255.0; return x <= 0.04045 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); }
        static double Rel(Color c) => 0.2126 * L(c.R) + 0.7152 * L(c.G) + 0.0722 * L(c.B);
        var (x, y) = (Rel(a), Rel(b));
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    /// <summary>The accent rule: as painted, it must reach 3:1 against the current background (WCAG 2.2 non-text
    /// contrast) so focus rings, selection bars and primary buttons stay visible.</summary>
    public static (bool Ok, double Ratio) CheckAccent(string? hex)
    {
        if (ParseHex(hex) is not { } brand) return (false, 0);
        var ratio = Contrast(AccentFor(brand, IsDark, EffectivePreset), Hex("Bg"));
        return (ratio >= 3.0, ratio);
    }

    /// <summary>The app's own colour for a key in dark or light mode — the preset in use for the base keys, the derived
    /// accent keys as painted (the branding editor's "fill with the colours in use"). Null for an unknown key.</summary>
    public static Color? DefaultColour(string key, bool dark)
    {
        if (Palette.ContainsKey(key)) return PresetColour(EffectivePreset, key, dark);
        if (Application.Current?.Resources[key] is SolidColorBrush b) return b.Color;
        return null;
    }

    /// <summary>The base colour of a preset for a key in dark or light mode (a pack without a light palette uses DarkCipher
    /// Light; the preview tiles and the self-test's contrast rules read it too). Environment labels never change.</summary>
    public static Color PresetColour(string preset, string key, bool dark) =>
        (Color)ColorConverter.ConvertFromString(!key.StartsWith("Env", StringComparison.Ordinal) && (dark ? DarkPresets : LightPresets).TryGetValue(preset, out var p) && p.TryGetValue(key, out var hx) ? "#" + hx
            : Palette.TryGetValue(key, out var v) ? (dark ? v.Dark : v.Light) : "#808080");

    /// <summary>Fonts and UI text size live in <see cref="Typography"/>.</summary>
    public static double UiScale => Typography.UiScale;

    /// <summary>Installed font families, sorted; <paramref name="monoOnly"/> keeps the usual fixed-pitch ones.</summary>
    public static List<string> InstalledFonts(bool monoOnly = false)
    {
        var names = Fonts.SystemFontFamilies.Select(f => f.Source).Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        if (!monoOnly) return names;
        string[] hints = { "Mono", "Consolas", "Courier", "Code", "Fira", "JetBrains", "Hack", "Menlo", "Source Code", "Ubuntu Mono", "Lucida Console", "Terminal", "IBM Plex Mono", "Iosevka", "Inconsolata", "Roboto Mono", "DejaVu Sans Mono", "Noto Sans Mono", "Victor" };
        return names.Where(n => hints.Any(h => n.Contains(h, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    /// <summary>Scales a window's content with the UI text size (a resource reference: no event, nothing to unhook).</summary>
    public static void TrackScale(Window w, FrameworkElement root) => Typography.Track(w, root);

    /// <summary>Current colour of a palette key ("Bg", "Fg", "Accent"...).</summary>
    public static Color Hex(string key)
    {
        if (Application.Current.Resources[key] is SolidColorBrush b) return b.Color;
        return Palette.TryGetValue(key, out var p) ? (Color)ColorConverter.ConvertFromString(IsDark ? p.Dark : p.Light) : Colors.Gray;
    }

    public static string HexString(string key) { var c = Hex(key); return $"{c.R:X2}{c.G:X2}{c.B:X2}"; }

    public static Color? ParseHex(string? hex)
    {
        hex = (hex ?? "").Trim().TrimStart('#');
        if (hex.Length != 6) return null;
        try { return (Color)ColorConverter.ConvertFromString("#" + hex); } catch { return null; }
    }

    private static SolidColorBrush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    private static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
    private static Color Mix(Color a, Color b, double t) =>
        Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
    private static Color Darken(Color c, double t) => Mix(c, Colors.Black, t);
    private static Color Lighten(Color c, double t) => Mix(c, Colors.White, t);

    public static void FollowSystemChanges()
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General && Mode == ThemeMode.System)
                Application.Current.Dispatcher.Invoke(() => Apply(ThemeMode.System));
        };
        // Windows high contrast switched on or off: the High Contrast preset follows it (when the user lets it).
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemParameters.HighContrast) && AppSettings.Current.FollowWindowsHighContrast)
                Application.Current.Dispatcher.BeginInvoke(() => { Apply(Mode); NavLayout.ApplyMotion(); });
        };
    }

    public static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Dark title bar, and the colour the window is cleared to before WPF paints its first frame. WPF clears a new
    /// window to white: in a dark theme that was a white flash on every window the app opened, worst at start-up. Painting
    /// the theme background instead (the composition target's colour) means the window is dark from its first pixel.
    /// Called from SourceInitialized (the handle exists, nothing is on screen yet) and again on every theme change.</summary>
    public static void ApplyTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        int dark = IsDark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
        if (HwndSource.FromHwnd(hwnd) is { CompositionTarget: { } target }) target.BackgroundColor = Hex("Bg");
    }
}
