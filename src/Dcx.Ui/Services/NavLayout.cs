using System.Windows;

namespace DcxManager.Services;

/// <summary>
/// Sidebar customisation (Settings → Appearance, or right-click a section header): section order, hidden sections and
/// list density. Pure list logic so the self-test can check it; the order/hidden lists live in the encrypted settings.
/// </summary>
public static class NavLayout
{
    /// <summary>The sections in the user's order: saved names first (ignoring ones that no longer exist), then any new
    /// sections at their default position relative to the ones before them.</summary>
    public static List<string> Order(IReadOnlyList<string> defaults, IEnumerable<string> saved)
    {
        var result = saved.Where(defaults.Contains).Distinct().ToList();
        for (int i = 0; i < defaults.Count; i++)
        {
            if (result.Contains(defaults[i])) continue;
            // insert after the nearest earlier default that is already placed
            int at = 0;
            for (int j = i - 1; j >= 0; j--) { int k = result.IndexOf(defaults[j]); if (k >= 0) { at = k + 1; break; } }
            result.Insert(at, defaults[i]);
        }
        return result;
    }

    /// <summary>Moves <paramref name="name"/> by <paramref name="delta"/> places (clamped). Returns the new order.</summary>
    public static List<string> Move(IReadOnlyList<string> order, string name, int delta)
    {
        var list = order.ToList();
        int i = list.IndexOf(name);
        if (i < 0) return list;
        int to = Math.Clamp(i + delta, 0, list.Count - 1);
        list.RemoveAt(i);
        list.Insert(to, name);
        return list;
    }

    /// <summary>Drag and drop: puts <paramref name="name"/> where <paramref name="target"/> is (before it when moving up, after it when moving down).</summary>
    public static List<string> MoveTo(IReadOnlyList<string> order, string name, string target)
    {
        int from = order.ToList().IndexOf(name), to = order.ToList().IndexOf(target);
        return from < 0 || to < 0 || from == to ? order.ToList() : Move(order, name, to - from);
    }

    /// <summary>Hides a section unless it is the last visible one (the sidebar must never end up empty).</summary>
    public static bool CanHide(IEnumerable<string> order, IEnumerable<string> hidden, string name) =>
        order.Count(s => !hidden.Contains(s) && s != name) > 0;

    // ---- density ----------------------------------------------------------------------------------------------------

    public static bool Compact => AppSettings.Current.Density == "compact";

    /// <summary>Row padding for lists, tables, trees and sidebar items ({DynamicResource …} in Themes/Controls.xaml).
    /// "comfortable" keeps the original spacing.</summary>
    public static IReadOnlyDictionary<string, Thickness> Paddings(bool compact) => new Dictionary<string, Thickness>
    {
        ["RowPadding"] = compact ? new Thickness(2, 1, 2, 1) : new Thickness(2, 5, 2, 5),
        ["ListRowPadding"] = compact ? new Thickness(4, 0, 4, 0) : new Thickness(4, 1, 4, 1),
        ["TreeRowPadding"] = compact ? new Thickness(2, 1, 2, 1) : new Thickness(2, 3, 2, 3),
        ["NavItemPadding"] = compact ? new Thickness(14, 3, 10, 3) : new Thickness(14, 6, 10, 6),
    };

    public static void ApplyDensity()
    {
        var res = Application.Current.Resources;
        foreach (var (k, v) in Paddings(Compact)) res[k] = v;
        // The sidebar's own style tightens its rows further (Settings → Navigation → Sidebar: Compact).
        if (AppSettings.Current.SidebarStyle == "compact") res["NavItemPadding"] = new Thickness(12, 2, 8, 2);
        ApplyCorners();
        ApplyMotion();
    }

    // ---- corners and motion (0.2.1, the DarkCipher house theme) --------------------------------------------------------

    /// <summary>Corner radius of controls ("Radius") and cards ("RadiusCard"): 0, 2 (default) or 4 px.</summary>
    public static void ApplyCorners()
    {
        var r = AppSettings.Current.Corners is 0 or 2 or 4 ? AppSettings.Current.Corners : 2;
        var res = Application.Current.Resources;
        res["Radius"] = new CornerRadius(r);
        res["RadiusCard"] = new CornerRadius(r == 0 ? 0 : r + (r >= 4 ? 2 : 0));
    }

    /// <summary>Animation: "reduced" (or "windows" with Windows animations off) stops decorative motion — the blinking
    /// header cursor and indeterminate progress sweeps. Screens read <see cref="Reduced"/>.</summary>
    public static bool Reduced => AppSettings.Current.Motion switch
    {
        "reduced" => true,
        "standard" => false,
        _ => !SystemParameters.ClientAreaAnimation,
    };

    public static event Action? MotionChanged;

    public static void ApplyMotion() => MotionChanged?.Invoke();

    /// <summary>Sidebar width presets for the expanded sidebar (Settings → Navigation); the edge can still be dragged.</summary>
    public static readonly int[] SidebarWidths = { 180, 214, 250, 290 };
    public static readonly string[] SidebarStyles = { "expanded", "compact", "icons" };
    /// <summary>The saved page order with one section's pages replaced by <paramref name="sectionOrder"/> (0.6.2): the
    /// other sections' saved pages keep their order; this section's go to the end, in the new order.</summary>
    public static List<string> WithSection(IEnumerable<string> saved, IReadOnlyList<string> sectionOrder) =>
        saved.Where(k => !sectionOrder.Contains(k)).Concat(sectionOrder).ToList();

    public const int MaxPinned = 6;
    /// <summary>The page that can never be hidden from the sidebar (Settings: where hidden pages come back).</summary>
    public const string AlwaysShownPage = "settings";

    /// <summary>Corrects out-of-range navigation values; returns what was corrected.</summary>
    public static List<string> Normalize(AppSettings s, IReadOnlyCollection<string>? knownKeys = null)
    {
        var fixes = new List<string>();
        if (!SidebarStyles.Contains(s.SidebarStyle)) { fixes.Add($"sidebar style “{s.SidebarStyle}”"); s.SidebarStyle = "expanded"; }
        if (s.Density is not ("comfortable" or "compact")) { fixes.Add($"density “{s.Density}”"); s.Density = "comfortable"; }
        var pinned = s.PinnedNavItems.Where(k => !string.IsNullOrWhiteSpace(k) && (knownKeys == null || knownKeys.Contains(k))).Distinct().Take(MaxPinned).ToList();
        if (!pinned.SequenceEqual(s.PinnedNavItems)) { fixes.Add("pinned pages"); s.PinnedNavItems = pinned; }
        // Hidden pages: Settings always stays (the way back). Unknown keys are kept: a module's pages (AI) may only be
        // missing while the module is off.
        var itemOrder = s.NavItemOrder.Where(k => !string.IsNullOrWhiteSpace(k)).Distinct().ToList();   // unknown keys kept (a module's pages)
        if (!itemOrder.SequenceEqual(s.NavItemOrder)) { fixes.Add("page order"); s.NavItemOrder = itemOrder; }
        var hiddenPages = s.HiddenNavItems.Where(k => !string.IsNullOrWhiteSpace(k) && k != AlwaysShownPage).Distinct().ToList();
        if (!hiddenPages.SequenceEqual(s.HiddenNavItems)) { fixes.Add("hidden pages"); s.HiddenNavItems = hiddenPages; }
        if (knownKeys != null && s.LandingPage.Length > 0 && !knownKeys.Contains(s.LandingPage)) { fixes.Add($"landing page “{s.LandingPage}”"); s.LandingPage = ""; }
        return fixes;
    }
}
