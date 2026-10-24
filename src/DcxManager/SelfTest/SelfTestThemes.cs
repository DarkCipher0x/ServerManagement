using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using DcxManager.Addons;
using DcxManager.Services;
using ThemeMode = DcxManager.Services.ThemeMode;   // .NET 9+ WPF has System.Windows.ThemeMode too
using DcxManager.Views;

namespace DcxManager;

/// <summary>
/// ADR-030 (0.1.12). Theme add-ons: the repository's sample theme (samples/addons/sample-theme) is packed, inspected,
/// installed and worn; every colour it names reaches the app's resources in light and dark, the environment colours do
/// not move, and Back to the default theme / disable / remove / changed files all put the app's own palette back exactly.
/// Malformed themes are refused at inspection. Everything runs against the scratch store; no window or dialog is shown.
/// </summary>
public static class ThemeAddonTests
{
    public static async Task RunAsync(MainWindow main, string reportDir, Action<string> ok, Action<string, Exception?> fail, Action<Window, string> snap)
    {
        async Task Settle(int ms = 200)
        {
            await Task.Delay(ms);
            await Application.Current.Dispatcher.InvokeAsync(() => main.UpdateLayout(), DispatcherPriority.ApplicationIdle);
        }
        var res = Application.Current.Resources;
        var shots = Directory.CreateDirectory(Path.Combine(reportDir, "shots")).FullName;
        var work = Directory.CreateDirectory(Path.Combine(reportDir, "themes")).FullName;
        var s = AppSettings.Current;
        var modeWas = ThemeManager.Mode;
        var themeWas = s.ThemeAddon;
        string[] keys = { "Bg", "Surface", "Input", "Fg", "Muted", "Line", "Hover", "Track", "ScrollThumb", "HeaderBg", "HeaderFg", "Good", "Warn", "Bad",
                          "EnvProd", "EnvTest", "EnvLab", "EnvDev", "EnvLocal", "EnvOther", "Accent", "AccentHover", "Brand", "OnAccent", "Selection" };
        Dictionary<string, Color> Now() => keys.ToDictionary(k => k, k => ((SolidColorBrush)res[k]).Color);
        string Diff(Dictionary<string, Color> a, Dictionary<string, Color> b) => string.Join(", ", keys.Where(k => a[k] != b[k]).Select(k => $"{k} {a[k]}≠{b[k]}"));

        // Package helpers: the repo sample when this build runs from the repository, else the same theme written inline.
        string? sampleDir = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null && sampleDir == null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "samples", "addons", "sample-theme"))) sampleDir = Path.Combine(d.FullName, "samples", "addons", "sample-theme");
        string Pack(string name, object manifest, string? themeJson, params (string Name, string Text)[] extra)
        {
            var path = Path.Combine(work, name + ".dcxaddon");
            if (File.Exists(path)) File.Delete(path);
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            using (var w = new StreamWriter(zip.CreateEntry("addon.json").Open())) w.Write(JsonSerializer.Serialize(manifest));
            if (themeJson != null) using (var w = new StreamWriter(zip.CreateEntry("theme.json").Open(), new UTF8Encoding(false))) w.Write(themeJson);
            foreach (var (n, t) in extra) using (var w = new StreamWriter(zip.CreateEntry(n).Open())) w.Write(t);
            return path;
        }
        string PackBytes(string name, object manifest, string themeJson, string file, byte[] bytes)
        {
            var path = Pack(name, manifest, themeJson);
            using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
            using (var st = zip.CreateEntry(file).Open()) st.Write(bytes);
            return path;
        }
        static byte[] Png(int w, int h)
        {
            var bmp = System.Windows.Media.Imaging.BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, new byte[w * h * 4], w * 4);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using var ms = new MemoryStream(); enc.Save(ms); return ms.ToArray();
        }
        object Manifest(string id, string version = "1.0.0", object? permissions = null, object? commands = null, string? entry = null) =>
            new { api = 2, id, name = "Self-test theme", version, publisher = "Self-test", description = "colours", theme = "theme.json", entry = entry ?? "", permissions = permissions ?? Array.Empty<string>(), commands = commands ?? Array.Empty<object>() };

        string samplePkg;
        string themeText;
        if (sampleDir != null)
        {
            samplePkg = Path.Combine(work, "sample-theme.dcxaddon");
            if (File.Exists(samplePkg)) File.Delete(samplePkg);
            ZipFile.CreateFromDirectory(sampleDir, samplePkg);
            themeText = File.ReadAllText(Path.Combine(sampleDir, "theme.json"), Encoding.UTF8);
        }
        else
        {
            themeText = """{ "uiFont": "Roboto", "colors": { "Bg": { "dark": "#0B1030", "light": "#FFFFFF" }, "HeaderBg": { "dark": "#182358", "light": "#182358" }, "Accent": { "dark": "#E86F2D", "light": "#DB4602" }, "OnAccent": { "dark": "#FFFFFF", "light": "#FFFFFF" } } }""";
            samplePkg = Pack("sample-inline", Manifest("com.darkcipher.theme.sample"), themeText);
        }
        var expected = ThemePack.Parse(themeText);

        try
        {
            s.ThemeAddon = ""; AppSettings.Save();
            ThemeManager.Apply(ThemeMode.Dark);
            var defDark = Now();
            ThemeManager.Apply(ThemeMode.Light);
            var defLight = Now();
            var defFont = res["UiFont"]?.ToString();

            // 1. inspect + install + wear
            var info = AddonPackages.Inspect(samplePkg);
            if (info.Refusal != null) throw new Exception("sample theme refused: " + info.Refusal);
            if (info.Manifest.Entry.Length != 0 || info.Manifest.Permissions.Count != 0 || info.Manifest.Commands.Count != 0 || !info.Signature.StartsWith("theme only")) throw new Exception("theme package not recognised as theme-only: " + info.Signature);
            // s40: every add-on to the same standard — a data-only package made for API 1 is refused at install too.
            var api1 = Pack("api1-theme", new { api = 1, id = "com.selftest.theme.api1", name = "Old theme", version = "1.0.0", publisher = "Self-test", description = "colours", theme = "theme.json" }, themeText);
            if (AddonPackages.Inspect(api1).Refusal?.Contains("API 2") != true) throw new Exception("an API 1 data-only add-on was accepted");
            var a = AddonPackages.Install(info, Array.Empty<string>());
            if (ThemeManager.ActiveTheme != null) throw new Exception("installing alone switched the theme (only the install screen or Use does)");
            ThemeAddons.Use(a);
            if (ThemeManager.ActiveTheme?.Id != a.Id || s.ThemeAddon != a.Id) throw new Exception("Use did not make it the active theme");
            string Check(bool dark, Dictionary<string, Color> def)
            {
                var now = Now();
                var wrong = new List<string>();
                foreach (var k in keys)
                {
                    var want = k.StartsWith("Env") ? def[k] : expected.Get(k, dark);
                    if (want != null && now[k] != want.Value) wrong.Add($"{k} {now[k]} (want {want})");
                }
                return string.Join(", ", wrong);
            }
            var badLight = Check(false, defLight);
            ThemeManager.Apply(ThemeMode.Dark);
            var badDark = Check(true, defDark);
            if (badLight.Length > 0 || badDark.Length > 0) throw new Exception($"light: {badLight} · dark: {badDark}");
            // 0.2.1 house theme: keys the add-on doesn't name (Surface2, FgStrong, LineStrong…) come from its own colours,
            // not DarkCipher's, and headings stay readable on its background.
            Color Res(string k) => ((SolidColorBrush)res[k]).Color;
            foreach (var k in new[] { "Surface2", "FgStrong", "LineStrong", "AccentDim", "HeaderMuted" })
                if (res[k] is not SolidColorBrush) throw new Exception("theme add-on: no " + k);
            if (expected.Get("FgStrong", true) == null && Res("FgStrong") == ThemeManager.PresetColour("darkcipher", "FgStrong", true)) throw new Exception("theme add-on: FgStrong is DarkCipher's, not derived from the add-on");
            if (ThemeManager.Contrast(Res("FgStrong"), Res("Bg")) < 4.5) throw new Exception($"theme add-on: headings {ThemeManager.Contrast(Res("FgStrong"), Res("Bg")):0.0}:1 on its background");
            var accentDark = ((SolidColorBrush)res["Accent"]).Color;

            // header branding: the theme's name, logo (with its plate) and title; the app's own back when it goes
            string Branding() => $"{main.HeaderTitle.Text} | {main.Title} | logo {(main.HeaderLogo.Source as System.Windows.Media.Imaging.BitmapSource)?.PixelWidth}×{(main.HeaderLogo.Source as System.Windows.Media.Imaging.BitmapSource)?.PixelHeight} w{main.HeaderLogo.Width} | plate {(main.LogoPlate.Background as SolidColorBrush)?.Color} | prompt {main.HeaderPrompt.Visibility}";
            var defBranding = "ServerManagement | " + MainWindow.DefaultTitle;
            if (expected.ProductName != null)
            {
                var logo = ThemeAddons.Peek(a)?.LogoImage;
                if (main.HeaderTitle.Text != expected.ProductName || main.Title != expected.ProductName
                    || (expected.Logo != null && (main.HeaderLogo.Source != ThemeManager.ActivePack?.LogoImage || logo == null || main.HeaderLogo.Width <= 36))
                    || (expected.LogoBackground is { } pb && (main.LogoPlate.Background as SolidColorBrush)?.Color != pb)
                    || (expected.Tagline == "" && main.HeaderPrompt.Visibility == Visibility.Visible))
                    throw new Exception("branding not applied: " + Branding());
                ok($"theme add-on: header shows “{expected.ProductName}” with the theme's logo ({logo?.PixelWidth}×{logo?.PixelHeight} PNG, {main.HeaderLogo.Width:0} px wide on a {expected.LogoBackground} plate); window title follows; the darkcipher0x prompt line is hidden");
            }
            ok($"theme add-on: sample package{(sampleDir != null ? " (repo sample)" : " (inline copy; repo sample not found from this build)")} installs theme-only (no program, no permissions), and Use paints every colour it names in dark and light (accent {accentDark}); environment labels unchanged");

            // 2. UI font: only when installed and the user hasn't chosen one
            var font = res["UiFont"]?.ToString() ?? "";
            bool installed = !Typography.IsMissing(expected.UiFont);
            if (installed != font.StartsWith(expected.UiFont, StringComparison.OrdinalIgnoreCase)) throw new Exception($"UI font {font} (theme font installed: {installed})");
            ok($"theme add-on: UI font “{expected.UiFont}” {(installed ? "is installed and used" : "is not installed here, so the app's own font stays")} ({font})");

            // 3. Add-ons screen with the theme on (render), light mode
            ThemeManager.Apply(ThemeMode.Light);
            main.Navigate("addons"); await Settle(600);
            AddonsView.Current?.Refresh(); await Settle(300);
            snap(main, Path.Combine(shots, "theme-sample-light.png"));
            ThemeManager.Apply(ThemeMode.Dark); await Settle(300);
            snap(main, Path.Combine(shots, "theme-sample-dark.png"));
            // s37: DarkCipher0x's freeze happened with this theme installed: terminals, then every Cloud providers screen.
            await FreezeTests.CloudSweepAsync(main, ok, fail, "sample theme, dark");
            ThemeManager.Apply(ThemeMode.Light);
            await FreezeTests.CloudSweepAsync(main, ok, fail, "sample theme, light");
            ThemeManager.Apply(ThemeMode.Dark); await Settle(200);

            // 4. back to the default, disable, re-enable
            ThemeAddons.Use(null);
            if (ThemeManager.ActiveTheme != null || Diff(Now(), defDark) is { Length: > 0 }) throw new Exception("Back to the default theme left: " + Diff(Now(), defDark));
            if (res["UiFont"]?.ToString() != defFont) throw new Exception("UI font not restored: " + res["UiFont"]);
            if (!Branding().StartsWith(defBranding) || main.HeaderLogo.Width != 36 || main.LogoPlate.Background != null || main.HeaderPrompt.Visibility != Visibility.Visible) throw new Exception("branding not restored: " + Branding());
            ThemeAddons.Use(a);
            AddonPackages.SetEnabled(a, false);
            if (ThemeManager.ActiveTheme != null || Diff(Now(), defDark) is { Length: > 0 }) throw new Exception("disabling left: " + Diff(Now(), defDark));
            if (!Branding().StartsWith(defBranding)) throw new Exception("disabling left the branding: " + Branding());
            AddonPackages.SetEnabled(a, true);
            if (ThemeManager.ActiveTheme?.Id != a.Id) throw new Exception("re-enabling did not bring the chosen theme back");
            // Enabled = in use: after Back to the default theme, disabling and enabling the add-on puts it in use again.
            ThemeAddons.Use(null); AddonPackages.SetEnabled(a, false); AddonPackages.SetEnabled(a, true);
            if (ThemeManager.ActiveTheme?.Id != a.Id || s.ThemeAddon != a.Id) throw new Exception("enabling a theme add-on did not put it in use");
            ok("theme add-on: Back to the default theme and Disable restore the app's own palette, font, logo, name and title exactly; enabling a theme add-on puts it in use");

            // 5. changed files: refused and the add-on disabled, like a program add-on
            File.AppendAllText(Path.Combine(a.Folder, "theme.json"), " ");
            ThemeManager.Apply(ThemeMode.Dark);
            if (ThemeManager.ActiveTheme != null || a.Enabled || a.DisabledReason?.Contains("theme.json was modified") != true || Diff(Now(), defDark) is { Length: > 0 }) throw new Exception($"tampered theme: active {ThemeManager.ActiveTheme?.Id}, enabled {a.Enabled}, {a.DisabledReason}");
            ok("theme add-on: a theme file changed after install is not applied; the add-on is disabled with the reason and the default palette stays");

            // 6. malformed themes are refused at inspection
            string Refusal(string name, object manifest, string? theme, params (string, string)[] extra) => AddonPackages.Inspect(Pack(name, manifest, theme, extra)).Refusal ?? "";
            var cases = new (string What, string Refusal, string Expect)[]
            {
                ("environment colour", Refusal("env", Manifest("com.example.theme-env"), """{ "colors": { "EnvProd": { "dark": "#00FF00" } } }"""), "unknown colour key"),
                ("unknown key", Refusal("key", Manifest("com.example.theme-key"), """{ "colors": { "Wallpaper": { "dark": "#000000" } } }"""), "unknown colour key"),
                ("named colour", Refusal("hex", Manifest("com.example.theme-hex"), """{ "colors": { "Bg": { "dark": "red" } } }"""), "#RRGGBB"),
                ("alpha colour", Refusal("argb", Manifest("com.example.theme-argb"), """{ "colors": { "Bg": { "dark": "#FF000000" } } }"""), "#RRGGBB"),
                ("unknown field", Refusal("field", Manifest("com.example.theme-field"), """{ "script": "x", "colors": { "Bg": { "dark": "#000000" } } }"""), "unknown field"),
                ("wrong type", Refusal("type", Manifest("com.example.theme-type"), """{ "colors": { "Bg": { "dark": 12 } } }"""), "wrong type"),
                ("not JSON", Refusal("json", Manifest("com.example.theme-json"), "{ colors: "), "not valid JSON"),
                ("no colours", Refusal("empty", Manifest("com.example.theme-empty"), """{ "uiFont": "Roboto" }"""), "sets no colours and no branding"),
                ("font injection", Refusal("font", Manifest("com.example.theme-font"), """{ "uiFont": "x\"; y", "colors": { "Bg": { "dark": "#000000" } } }"""), "uiFont"),
                ("permissions", Refusal("perm", Manifest("com.example.theme-perm", permissions: new[] { "hosts.read" }), """{ "colors": { "Bg": { "dark": "#000000" } } }"""), "can't have permissions"),
                ("commands", Refusal("cmd", Manifest("com.example.theme-cmd", commands: new[] { new { id = "x", title = "x", context = "global" } }), """{ "colors": { "Bg": { "dark": "#000000" } } }"""), "can't have permissions"),
                ("missing file", Refusal("missing", Manifest("com.example.theme-missing"), null, ("other.json", "{}")), "must be a .json file"),
                ("logo not .png", Refusal("logo-ext", Manifest("com.example.theme-logo-ext"), """{ "logo": "logo.svg", "colors": { "Bg": { "dark": "#000000" } } }""", ("logo.svg", "<svg/>")), ".png file in the package"),
                ("logo path escape", Refusal("logo-path", Manifest("com.example.theme-logo-path"), """{ "logo": "../logo.png", "colors": { "Bg": { "dark": "#000000" } } }"""), ".png file in the package"),
                ("logo missing", Refusal("logo-missing", Manifest("com.example.theme-logo-missing"), """{ "logo": "logo.png", "colors": { "Bg": { "dark": "#000000" } } }"""), "not in the package"),
                ("logo not a PNG", Refusal("logo-fake", Manifest("com.example.theme-logo-fake"), """{ "logo": "logo.png", "colors": { "Bg": { "dark": "#000000" } } }""", ("logo.png", "GIF89a not really")), "must be a PNG"),
                ("logo too large", AddonPackages.Inspect(PackBytes("logo-big", Manifest("com.example.theme-logo-big"), """{ "logo": "logo.png", "colors": { "Bg": { "dark": "#000000" } } }""", "logo.png", Png(3000, 2))).Refusal ?? "", "at most 2048"),
                ("product name control chars", Refusal("name-ctl", Manifest("com.example.theme-name-ctl"), """{ "productName": "a\u0007b", "colors": { "Bg": { "dark": "#000000" } } }"""), "plain text"),
                ("empty product name", Refusal("name-empty", Manifest("com.example.theme-name-empty"), """{ "productName": "", "colors": { "Bg": { "dark": "#000000" } } }"""), "can't be empty"),
            };
            var missed = cases.Where(c => !c.Refusal.Contains(c.Expect, StringComparison.OrdinalIgnoreCase)).Select(c => $"{c.What}: “{c.Refusal}”").ToList();
            if (missed.Count > 0) throw new Exception(string.Join(" | ", missed));
            ok($"theme add-on: {cases.Length} malformed themes refused at inspection (environment colours, unknown keys, non-#RRGGBB, unknown fields, wrong types, bad JSON, empty, font injection, permissions/commands, missing file, logos that aren't a small PNG in the package, bad product names)");

            // 7. remove: the setting is cleared and the default stays
            AddonPackages.Remove(a);
            a = AddonPackages.Install(AddonPackages.Inspect(samplePkg), Array.Empty<string>());
            ThemeAddons.Use(a);
            if (ThemeManager.ActiveTheme?.Id != a.Id) throw new Exception("reinstalled theme not in use");
            var folder = a.Folder;
            AddonPackages.Remove(a);
            if (s.ThemeAddon != "" || ThemeManager.ActiveTheme != null || Directory.Exists(folder) || Diff(Now(), defDark) is { Length: > 0 }) throw new Exception($"after remove: setting “{s.ThemeAddon}”, folder {Directory.Exists(folder)}, {Diff(Now(), defDark)}");
            ThemeManager.Apply(ThemeMode.Light);
            if (Diff(Now(), defLight) is { Length: > 0 } d2) throw new Exception("light after remove: " + d2);
            ok("theme add-on: Remove deletes its files, clears the choice and leaves the app's own palette in dark and light");
        }
        catch (Exception ex) { fail("theme add-on", ex); }
        finally
        {
            // leave the window as found: the Add-ons tab this test opened is closed (later leak checks open and close screens)
            if (main.Panes.AllTabs.FirstOrDefault(t => t.Content is AddonsView) is { } addonsTab) main.CloseTab(addonsTab);
            foreach (var x in AddonPackages.All.Where(x => ThemeAddons.IsTheme(x.Manifest)).ToList()) { try { AddonPackages.Remove(x); } catch { } }
            s.ThemeAddon = themeWas; AppSettings.Save();
            ThemeManager.Apply(modeWas);
            Typography.Apply();
        }
    }
}
