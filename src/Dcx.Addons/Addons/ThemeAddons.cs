using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DcxManager.Services;

namespace DcxManager.Addons;

/// <summary>Which theme add-on is in use, and switching it (ADR-030). One theme at a time: installing or enabling a theme
/// add-on puts it in use; Back to the default theme, disabling or removing it, or its files changing, puts the app's own
/// palette and branding back.</summary>
public static class ThemeAddons
{
    private static (string Key, ThemePack Pack)? _cache;
    private static bool _reapplying;

    public static bool IsTheme(AddonManifest m) => !string.IsNullOrEmpty(m.Theme);

    /// <summary>The add-on chosen as the theme (may be disabled or broken; see <see cref="Active"/>).</summary>
    public static InstalledAddon? Chosen => AppSettings.Current.ThemeAddon is { Length: > 0 } id ? AddonPackages.Find(id) : null;

    /// <summary>The theme to paint with now, or null for the app's own palette. Files are verified every time (they are tiny);
    /// a theme whose files changed is disabled exactly like a program add-on would be.</summary>
    public static ThemePack? Active()
    {
        var a = Chosen;
        if (a == null || !a.Enabled || !IsTheme(a.Manifest)) return null;
        if (AddonPackages.UseRefusal(a) != null) return null;   // 0.5.1 #7: blocked by policy — the app's own theme
        if (AddonPackages.VerifyFiles(a) is { } tamper)
        {
            // The caller is already painting; don't let SetEnabled start a second repaint inside this one.
            var was = _reapplying; _reapplying = true;
            try { AddonPackages.SetEnabled(a, false, "files changed after install: " + tamper); }
            finally { _reapplying = was; }
            return null;
        }
        var key = a.Id + "|" + a.PackageSha256;
        if (_cache is { } c && c.Key == key) return c.Pack;
        try
        {
            var pack = Load(a);
            _cache = (key, pack);
            return pack;
        }
        catch (Exception ex) { Log.Write("Theme add-on " + a.Id, ex); return null; }
    }

    /// <summary>Reads an installed theme (its files must already be verified).</summary>
    private static ThemePack Load(InstalledAddon a)
    {
        var pack = ThemePack.Parse(AddonPackages.ReadInstalledText(a, a.Manifest.Theme));   // sealed at rest (ADR-035)
        if (pack.Logo != null) pack.LogoImage = ThemePack.DecodeLogo(AddonPackages.ReadInstalled(a, pack.Logo));
        return pack;
    }

    /// <summary>An installed theme's colours for a preview, without applying it (null if its files changed or don't parse).</summary>
    public static ThemePack? Peek(InstalledAddon a)
    {
        if (!IsTheme(a.Manifest) || AddonPackages.VerifyFiles(a) != null) return null;
        try { return Load(a); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Makes <paramref name="a"/> the theme (null = the app's own theme), saves and repaints.</summary>
    public static void Use(InstalledAddon? a)
    {
        if (a != null && !IsTheme(a.Manifest)) throw new InvalidOperationException($"{a.Title} is not a theme add-on.");
        var was = AppSettings.Current.ThemeAddon;
        AppSettings.Current.ThemeAddon = a?.Id ?? "";
        AppSettings.Save();
        if (was != AppSettings.Current.ThemeAddon)
            AuditLog.Write("you", "addons", a != null ? "theme add-on in use" : "default theme in use", a != null ? $"{a.Id} {a.Manifest.Version}" : was ?? "");
        Repaint();
    }

    /// <summary>Called after an install, update, enable/disable or removal. Enabling a theme add-on puts it in use (it overrides
    /// the app's theme while enabled); anything else repaints only when it concerns the theme in use.</summary>
    public static void Reapply(InstalledAddon a, bool enabled = false)
    {
        if (!IsTheme(a.Manifest)) return;
        if (enabled && a.Enabled && !_reapplying && AppSettings.Current.ThemeAddon != a.Id) { Use(a); return; }
        if (AppSettings.Current.ThemeAddon != a.Id) return;
        Repaint();
    }

    private static void Repaint()
    {
        if (_reapplying || !Ui.Thread.HasUi) return;
        _reapplying = true;
        try { _cache = null; ThemeManager.Apply(ThemeManager.Mode); Typography.Apply(); }
        finally { _reapplying = false; }
    }
}
