using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DcxManager.Services;

/// <summary>
/// Settings → Import &amp; export (0.2.1, as in the other DarkCipher Systems apps): appearance and navigation settings as a small JSON file.
/// Only those settings exist in it — never accounts, keys, tokens, hosts, paths, notes or anything else from the store —
/// so it is safe to share between PCs. An import is parsed and validated in full first (every value bounded; anything
/// unknown or out of range is listed and skipped), shown as a preview of what would change, and applied only after that.
/// </summary>
public static class AppearanceTransfer
{
    public const string Format = "dcx-appearance";
    public const int Version = 1;
    public const int MaxBytes = 64 * 1024;

    private static readonly double[] Scales = { 0.9, 1.0, 1.1, 1.25, 1.5, 1.75 };
    private static readonly double[] TextSizes = { 0.85, 0.9, 1.0, 1.1, 1.25, 1.5 };

    /// <summary>One setting that an import would change: its name, the current and the new value as text.</summary>
    public sealed record Change(string Name, string From, string To, Action<AppSettings> Apply);
    public sealed record Plan(List<Change> Changes, List<string> Skipped);

    /// <summary>The export: the format marker plus the appearance and navigation settings.</summary>
    public static string Export(AppSettings s)
    {
        var o = new JsonObject
        {
            ["format"] = Format, ["version"] = Version, ["app"] = AppInfo.ProductName, ["exported"] = DateTime.UtcNow.ToString("yyyy-MM-dd"),
            ["themeMode"] = s.Theme, ["themePreset"] = s.ThemePreset, ["accent"] = s.AccentColor, ["statusIntensity"] = s.StatusIntensity,
            ["followWindowsHighContrast"] = s.FollowWindowsHighContrast,
            ["density"] = s.Density, ["textSize"] = s.UiTextSize, ["zoom"] = s.UiScale, ["corners"] = s.Corners, ["animation"] = s.Motion,
            ["uiFont"] = s.UiFont, ["monoFont"] = s.MonoFont, ["editorFont"] = s.EditorFont, ["terminalFont"] = s.TerminalFont,
            ["terminalFontSize"] = s.TerminalFontSize, ["terminalStyleAccents"] = s.MonoAccents,
            ["sidebarStyle"] = s.SidebarStyle, ["sidebarWidth"] = Math.Round(s.SidebarWidth), ["landingPage"] = s.LandingPage,
            ["pinnedPages"] = new JsonArray(s.PinnedNavItems.Select(x => (JsonNode)x).ToArray()),
            ["sectionOrder"] = new JsonArray(s.NavSectionOrder.Select(x => (JsonNode)x).ToArray()),
            ["hiddenSections"] = new JsonArray(s.HiddenNavSections.Select(x => (JsonNode)x).ToArray()),
            ["hiddenPages"] = new JsonArray(s.HiddenNavItems.Select(x => (JsonNode)x).ToArray()),
            ["rememberExpandedSections"] = s.RememberNavExpansion,
        };
        return o.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>What importing <paramref name="json"/> would change in <paramref name="current"/> (nothing is changed
    /// here). Throws <see cref="InvalidDataException"/> when the file is not an appearance export at all.</summary>
    public static Plan Read(string json, AppSettings current, IReadOnlyCollection<string>? navKeys = null, IReadOnlyCollection<string>? sections = null)
    {
        if (json.Length > MaxBytes) throw new InvalidDataException("the file is larger than an appearance export can be");
        JsonObject o;
        try { o = JsonNode.Parse(json) as JsonObject ?? throw new InvalidDataException("not a JSON object"); }
        catch (JsonException) { throw new InvalidDataException("not valid JSON"); }
        if (o["format"]?.GetValueKind() != JsonValueKind.String || o["format"]!.GetValue<string>() != Format) throw new InvalidDataException("not a DarkCipher appearance export (no \"format\": \"dcx-appearance\")");
        if (o["version"]?.GetValueKind() != JsonValueKind.Number || o["version"]!.GetValue<int>() > Version) throw new InvalidDataException("made by a newer version of the app");

        var changes = new List<Change>();
        var skipped = new List<string>();
        string? Str(string key, int max = 100)
        {
            if (o[key] is not { } n) return null;
            if (n.GetValueKind() != JsonValueKind.String) { skipped.Add($"{key}: not text"); return null; }
            var v = n.GetValue<string>();
            if (v.Length > max || v.Any(char.IsControl)) { skipped.Add($"{key}: too long or contains control characters"); return null; }
            return v;
        }
        double? Num(string key)
        {
            if (o[key] is not { } n) return null;
            if (n.GetValueKind() != JsonValueKind.Number) { skipped.Add($"{key}: not a number"); return null; }
            return n.GetValue<double>();
        }
        bool? Bool(string key)
        {
            if (o[key] is not { } n) return null;
            if (n.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False)) { skipped.Add($"{key}: not true or false"); return null; }
            return n.GetValue<bool>();
        }
        List<string>? List(string key, Func<string, bool> allowed, int max = 20)
        {
            if (o[key] is not { } n) return null;
            if (n is not JsonArray a || a.Count > max || a.Any(x => x?.GetValueKind() != JsonValueKind.String)) { skipped.Add($"{key}: not a short list of names"); return null; }
            var list = a.Select(x => x!.GetValue<string>()).ToList();
            var bad = list.Where(x => !allowed(x)).ToList();
            if (bad.Count > 0) skipped.Add($"{key}: unknown {string.Join(", ", bad.Take(5))} (left out)");
            return list.Where(allowed).Distinct().ToList();
        }
        void AddV<T>(string name, T from, T? to, Action<AppSettings, T> apply, Func<T, string>? show = null) where T : struct
        {
            if (to is not { } value || EqualityComparer<T>.Default.Equals(from, value)) return;
            var f = show ?? (x => x.ToString() ?? "");
            changes.Add(new Change(name, f(from), f(value), s => apply(s, value)));
        }
        void AddS(string name, string from, string? to, Action<AppSettings, string> apply, Func<string, string>? show = null)
        {
            if (to is null || from == to) return;
            var f = show ?? (x => x);
            changes.Add(new Change(name, f(from), f(to), s => apply(s, to)));
        }
        void OneOf(string key, string name, string from, IEnumerable<string> allowed, Action<AppSettings, string> apply)
        {
            var v = Str(key);
            if (v == null) return;
            if (!allowed.Contains(v)) { skipped.Add($"{key}: “{v}” is not one of {string.Join(", ", allowed)}"); return; }
            AddS(name, from, v, apply);
        }
        static string Pct(double d) => $"{d * 100:0}%";
        static string Join(List<string> l) => l.Count == 0 ? "(none)" : string.Join(", ", l);

        OneOf("themeMode", "Theme mode", current.Theme, new[] { "System", "Dark", "Light" }, (s, v) => s.Theme = v);
        OneOf("themePreset", "Theme preset", current.ThemePreset, ThemeManager.Presets.Select(p => p.Id), (s, v) => s.ThemePreset = v);
        if (Str("accent") is { } accent)
        {
            if (ThemeManager.ParseHex(accent) == null) skipped.Add($"accent: “{accent}” is not six hex digits");
            else AddS("Accent", current.AccentColor, accent.Trim().TrimStart('#').ToUpperInvariant(), (s, v) => s.AccentColor = v, x => "#" + x);
        }
        OneOf("statusIntensity", "Status colour intensity", current.StatusIntensity, ThemeManager.StatusIntensities, (s, v) => s.StatusIntensity = v);
        AddV("Follow Windows high contrast", current.FollowWindowsHighContrast, Bool("followWindowsHighContrast"), (s, v) => s.FollowWindowsHighContrast = v);
        OneOf("density", "Density", current.Density, new[] { "comfortable", "compact" }, (s, v) => s.Density = v);
        if (Num("textSize") is { } ts) { if (TextSizes.Any(x => Math.Abs(x - ts) < 0.001)) AddV("Text size", current.UiTextSize, ts, (s, v) => s.UiTextSize = v, Pct); else skipped.Add($"textSize: {ts} is not offered"); }
        if (Num("zoom") is { } z) { if (Scales.Any(x => Math.Abs(x - z) < 0.001)) AddV("Interface zoom", current.UiScale, z, (s, v) => s.UiScale = v, Pct); else skipped.Add($"zoom: {z} is not offered"); }
        if (Num("corners") is { } c) { if (ThemeManager.CornerChoices.Contains((int)c) && c == Math.Floor(c)) AddV("Corners", current.Corners, (int)c, (s, v) => s.Corners = v, x => x + " px"); else skipped.Add($"corners: {c} is not 0, 2 or 4"); }
        OneOf("animation", "Animation", current.Motion, ThemeManager.MotionChoices, (s, v) => s.Motion = v);
        foreach (var (key, name, from, apply) in new (string, string, string, Action<AppSettings, string>)[]
        {
            ("uiFont", "UI font", current.UiFont, (s, v) => s.UiFont = v), ("monoFont", "Code font", current.MonoFont, (s, v) => s.MonoFont = v),
            ("editorFont", "Editor font", current.EditorFont, (s, v) => s.EditorFont = v), ("terminalFont", "Terminal font", current.TerminalFont, (s, v) => s.TerminalFont = v),
        })
            AddS(name, from, Str(key, 64), apply, x => x.Length == 0 ? "(default)" : x);
        if (Num("terminalFontSize") is { } tf) { if (tf >= 9 && tf <= 28 && tf == Math.Floor(tf)) AddV("Terminal font size", current.TerminalFontSize, (int)tf, (s, v) => s.TerminalFontSize = v); else skipped.Add($"terminalFontSize: {tf} is outside 9–28"); }
        AddV("Terminal-style accents", current.MonoAccents, Bool("terminalStyleAccents"), (s, v) => s.MonoAccents = v);
        OneOf("sidebarStyle", "Sidebar", current.SidebarStyle, NavLayout.SidebarStyles, (s, v) => s.SidebarStyle = v);
        if (Num("sidebarWidth") is { } sw) { if (sw >= 150 && sw <= 480) AddV("Sidebar width", Math.Round(current.SidebarWidth), Math.Round(sw), (s, v) => s.SidebarWidth = v, x => x + " px"); else skipped.Add($"sidebarWidth: {sw} is outside 150–480"); }
        if (Str("landingPage", 64) is { } lp)
        {
            if (lp.Length > 0 && navKeys != null && !navKeys.Contains(lp)) skipped.Add($"landingPage: “{lp}” is not a page of this installation");
            else AddS("Open on start", current.LandingPage, lp, (s, v) => s.LandingPage = v, x => x.Length == 0 ? "Dashboard" : x);
        }
        bool IsPage(string k) => navKeys == null ? k.Length is > 0 and <= 64 : navKeys.Contains(k);
        bool IsSection(string k) => sections == null ? k.Length is > 0 and <= 32 : sections.Contains(k);
        if (List("pinnedPages", IsPage) is { } pins) AddList("Pinned pages", current.PinnedNavItems, pins.Take(NavLayout.MaxPinned).ToList(), (s, v) => s.PinnedNavItems = v.ToList());
        if (List("sectionOrder", IsSection) is { } order) AddList("Section order", current.NavSectionOrder, order, (s, v) => s.NavSectionOrder = v.ToList());
        if (List("hiddenSections", IsSection) is { } hidden)
        {
            if (sections != null && sections.All(hidden.Contains)) skipped.Add("hiddenSections: would hide every section (left out)");
            else AddList("Hidden sections", current.HiddenNavSections, hidden, (s, v) => s.HiddenNavSections = v.ToList());
        }
        if (List("hiddenPages", k => IsPage(k) && k != NavLayout.AlwaysShownPage, max: 64) is { } hiddenPages) AddList("Hidden pages", current.HiddenNavItems, hiddenPages, (s, v) => s.HiddenNavItems = v.ToList());
        AddV("Remember expanded sections", current.RememberNavExpansion, Bool("rememberExpandedSections"), (s, v) => s.RememberNavExpansion = v);
        return new Plan(changes, skipped);

        // Lists compare by content.
        void AddList(string name, List<string> from, List<string> to, Action<AppSettings, List<string>> apply)
        {
            if (from.SequenceEqual(to)) return;
            changes.Add(new Change(name, Join(from), Join(to), s => apply(s, to)));
        }
    }

    /// <summary>A short text preview of a plan, for the confirmation.</summary>
    public static string Describe(Plan p)
    {
        var sb = new System.Text.StringBuilder();
        if (p.Changes.Count == 0) sb.Append("Nothing would change: these settings are already in use.");
        else
        {
            sb.Append($"{p.Changes.Count} setting(s) would change:\n");
            foreach (var c in p.Changes) sb.Append($"\n  • {c.Name}: {c.From} → {c.To}");
        }
        if (p.Skipped.Count > 0) sb.Append($"\n\nLeft out ({p.Skipped.Count}):\n").Append(string.Join("\n", p.Skipped.Select(x => "  • " + x)));
        return sb.ToString();
    }

    /// <summary>Applies a plan to <paramref name="s"/> (the caller saves and repaints).</summary>
    public static void Apply(Plan p, AppSettings s) { foreach (var c in p.Changes) c.Apply(s); }
}
