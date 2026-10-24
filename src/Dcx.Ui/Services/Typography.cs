using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace DcxManager.Services;

/// <summary>
/// The one place that decides the app's fonts and UI scale. Views never set a font family themselves: they use the
/// resources below through <c>{DynamicResource …}</c> (XAML) or <see cref="Use"/> / <see cref="Mono{T}"/> (code), so a
/// change in Settings reaches every open window, dialog, pop-out, menu and tooltip at once.
/// <list type="bullet">
/// <item><c>UiFont</c> — all interface text (windows pick it up through a class handler, see <see cref="Install"/>).</item>
/// <item><c>ChromeFont</c> — buttons, tabs, sidebar, labels, headings: UiFont, or MonoFont with "terminal-style accents".</item>
/// <item><c>MonoFont</c> — code, addresses, ids, tables of data.</item>
/// <item><c>EditorFont</c> — notes and multi-line text editors.</item>
/// <item><c>UiScaleTransform</c> — the UI text size (whole-UI zoom), also used by popups, which are separate visual trees.</item>
/// </list>
/// The terminal (xterm in WebView2) is separate on purpose: it reads <see cref="AppSettings.TerminalFont"/> and its own
/// size, and listens to <see cref="TerminalChanged"/> only, so UI changes never alter terminal cell metrics.
/// Remote Desktop content is drawn by mstsc and is never touched.
/// </summary>
public static class Typography
{
    /// <summary>Default UI face: Segoe UI Variable Text (Windows 11's reading-optimised optical size) with Segoe UI as the
    /// fallback on Windows 10. Both ship with Windows, so nothing is redistributed and no font licence is involved.</summary>
    public const string DefaultUi = "Segoe UI Variable Text, Segoe UI";
    public const string DefaultMono = "Cascadia Mono, Cascadia Code, Consolas, Courier New";

    /// <summary>UI fonts, accents or scale changed (resources are already updated when this fires).</summary>
    public static event Action? Changed;
    /// <summary>Only the terminal font changed. Open terminals re-apply it; nothing else listens.</summary>
    public static event Action? TerminalChanged;

    public static double UiScale => Math.Clamp(AppSettings.Current.UiScale <= 0 ? 1 : AppSettings.Current.UiScale, 0.8, 2.0);

    /// <summary>UI font size (text only, ADR-015): multiplies the type ramp. Layout, icons and borders keep their size.</summary>
    public static double TextSize => Math.Clamp(AppSettings.Current.UiTextSize <= 0 ? 1 : AppSettings.Current.UiTextSize, 0.85, 1.5);

    /// <summary>How much larger than designed a fixed window must be for its content to fit: UI scale × text size.</summary>
    public static double WindowFactor => UiScale * TextSize;

    /// <summary>The type ramp: one resource per role, with the designed size at 100 %. Every interface font size refers to one
    /// (<c>{DynamicResource Fs…}</c> or <see cref="Fs{T}"/>); the terminal is the only thing that doesn't.</summary>
    public static readonly (string Key, double Px)[] Ramp =
    {
        ("FsMicro", 10.5), ("FsCaption", 11), ("FsSmall", 11.5), ("FsMeta", 12), ("FsBody", 12.5), ("FsControl", 13), ("FsBase", 13.5),
        ("FsInput", 14), ("FsTitle", 15), ("FsHeading", 16), ("FsH1", 17), ("FsCode", 18), ("FsValue", 20), ("FsIcon", 26),
    };

    /// <summary>A token's size now: the designed size × text size, rounded to 0.5 px (crisp text, and 100 % = the design exactly).</summary>
    public static double Px(FontStep step) => Math.Round(Ramp[(int)step].Px * TextSize * 2, MidpointRounding.AwayFromZero) / 2;

    private static HashSet<string>? _installed;

    /// <summary>Installed family names (cached; <see cref="RefreshInstalled"/> after fonts are added).</summary>
    public static HashSet<string> Installed =>
        _installed ??= new HashSet<string>(Fonts.SystemFontFamilies.Select(f => f.Source), StringComparer.OrdinalIgnoreCase);

    public static void RefreshInstalled() => _installed = null;

    /// <summary>True when every face named in a "A, B" list is unknown, i.e. the choice would fall back.</summary>
    public static bool IsMissing(string? family) =>
        !string.IsNullOrWhiteSpace(family) && !family.Split(',').Select(f => f.Trim()).Any(f => Installed.Contains(f));

    /// <summary>The family string to use: the user's choice (if installed) in front of the default list, else the default.</summary>
    public static string Resolve(string? chosen, string fallback) =>
        string.IsNullOrWhiteSpace(chosen) || IsMissing(chosen) ? fallback : chosen.Trim() + ", " + fallback;

    /// <summary>Choices that point at a font that is no longer installed (shown in Settings; the app is already using the default).</summary>
    public static List<string> MissingChoices()
    {
        var s = AppSettings.Current;
        var list = new List<string>();
        void Check(string label, string v) { if (IsMissing(v)) list.Add($"{label}: “{v}” is not installed — using the default"); }
        Check("UI font", s.UiFont); Check("Monospace font", s.MonoFont); Check("Editor font", s.EditorFont); Check("Terminal font", s.TerminalFont);
        return list;
    }

    /// <summary>Pushes the current settings into the application resources. Safe to call any time on the UI thread.</summary>
    public static void Apply()
    {
        var s = AppSettings.Current;
        var res = Application.Current.Resources;
        // The user's own UI font wins; otherwise a theme add-on's font (ADR-030) when installed on this PC.
        var ui = new FontFamily(Resolve(string.IsNullOrWhiteSpace(s.UiFont) ? ThemeManager.ThemeSource().Pack?.UiFont : s.UiFont, DefaultUi));
        var mono = new FontFamily(Resolve(s.MonoFont, DefaultMono));
        res["UiFont"] = ui;
        res["MonoFont"] = mono;
        res["ChromeFont"] = s.MonoAccents ? mono : ui;
        res["EditorFont"] = new FontFamily(Resolve(s.EditorFont, DefaultMono));
        var scale = Math.Abs(UiScale - 1) < 0.001 ? Transform.Identity : new ScaleTransform(UiScale, UiScale);
        scale.Freeze();
        res["UiScaleTransform"] = scale;
        for (int i = 0; i < Ramp.Length; i++) res[Ramp[i].Key] = Px((FontStep)i);
        Changed?.Invoke();
    }

    /// <summary>Changes UI typography, saves, and applies live. Null arguments keep the current value.</summary>
    public static void Set(string? uiFont = null, string? monoFont = null, string? editorFont = null, bool? monoAccents = null, double? scale = null, double? textSize = null)
    {
        var s = AppSettings.Current;
        if (uiFont != null) s.UiFont = uiFont;
        if (monoFont != null) s.MonoFont = monoFont;
        if (editorFont != null) s.EditorFont = editorFont;
        if (monoAccents != null) s.MonoAccents = monoAccents.Value;
        if (scale != null) s.UiScale = scale.Value;
        if (textSize != null) s.UiTextSize = Math.Clamp(textSize.Value, 0.85, 1.5);
        try { AppSettings.Save(); } catch (Exception ex) { Log.Write("typography save", ex); }
        Apply();
    }

    public static void SetTerminal(string? family = null, int? size = null)
    {
        var s = AppSettings.Current;
        if (family != null) s.TerminalFont = family;
        if (size != null) s.TerminalFontSize = Math.Clamp(size.Value, 6, 40);
        try { AppSettings.Save(); } catch (Exception ex) { Log.Write("typography save", ex); }
        TerminalChanged?.Invoke();
    }

    public const double DefaultScale = 1.1;
    public const int DefaultTerminalSize = 14;

    /// <summary>Settings → Appearance → Typography → Reset to defaults (UI and terminal).</summary>
    public static void ResetDefaults()
    {
        var s = AppSettings.Current;
        s.UiFont = ""; s.MonoFont = ""; s.EditorFont = ""; s.MonoAccents = false; s.UiScale = DefaultScale; s.UiTextSize = 1;
        s.TerminalFont = ""; s.TerminalFontSize = DefaultTerminalSize;
        try { AppSettings.Save(); } catch (Exception ex) { Log.Write("typography save", ex); }
        Apply();
        TerminalChanged?.Invoke();
    }

    /// <summary>The terminal's CSS font-family list (the page itself also falls back if the face is missing).</summary>
    public static string TerminalFamily => IsMissing(AppSettings.Current.TerminalFont) ? "" : AppSettings.Current.TerminalFont.Trim();

    // ---- wiring ------------------------------------------------------------------------------------------------------

    private static readonly ConditionalWeakTable<Window, object> Seen = new();

    /// <summary>Called once at start-up: every window (main, dialogs, pop-outs, detached panes, AI windows, ones created
    /// in code) gets the UI font through a resource reference when it loads, so no window can keep a stale font.</summary>
    public static void Install()
    {
        Apply();
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((o, e) =>
        {
            if (o is not Window w || Seen.TryGetValue(w, out _)) return;
            Seen.Add(w, true);
            w.SetResourceReference(TextElement.FontFamilyProperty, "UiFont");
        }));
        // Context menus and tooltips live outside any window's tree and are often created once and reused, so a change
        // made after they were built wouldn't reach them; re-bind as each one opens (cheap, and covers every menu in the app).
        // Popups are outside every window's tree, so app-resource changes don't reach them either: the font size is re-bound
        // here too (the same tokens as their theme styles: menus FsBase, tooltips FsBody).
        static void Rebind(object o, string size)
        {
            if (o is not FrameworkElement fe) return;
            fe.SetResourceReference(TextElement.FontFamilyProperty, "UiFont");
            fe.SetResourceReference(TextElement.FontSizeProperty, size);
            fe.SetResourceReference(FrameworkElement.LayoutTransformProperty, "UiScaleTransform");
        }
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.ContextMenu), System.Windows.Controls.ContextMenu.OpenedEvent, new RoutedEventHandler((o, _) => Rebind(o, "FsBase")));
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.ToolTip), System.Windows.Controls.ToolTip.OpenedEvent, new RoutedEventHandler((o, _) => Rebind(o, "FsBody")));
    }

    /// <summary>Opts a window's content into the UI text size (whole-UI zoom). Windows that host only web content leave it off.</summary>
    public static void Track(Window w, FrameworkElement root)
    {
        w.SetResourceReference(TextElement.FontFamilyProperty, "UiFont");
        root.SetResourceReference(FrameworkElement.LayoutTransformProperty, "UiScaleTransform");
    }

    /// <summary>Binds an element's font to a typography resource ("UiFont", "MonoFont", "EditorFont", "ChromeFont").</summary>
    public static T Use<T>(this T e, string resource) where T : FrameworkElement
    {
        e.SetResourceReference(TextElement.FontFamilyProperty, resource);
        return e;
    }

    public static T Mono<T>(this T e) where T : FrameworkElement => e.Use("MonoFont");
    public static T Editor<T>(this T e) where T : FrameworkElement => e.Use("EditorFont");

    /// <summary>Binds an element's font size to a type-ramp token, so it follows the UI font size live (ADR-015).</summary>
    public static T Fs<T>(this T e, FontStep step) where T : FrameworkElement
    {
        e.SetResourceReference(TextElement.FontSizeProperty, Ramp[(int)step].Key);
        return e;
    }

    /// <summary>The sizing rule for dialogs and fixed windows (ADR-015): the designed size × <see cref="WindowFactor"/>, capped to
    /// the work area, and kept in proportion when typography changes while the window is open.</summary>
    public static void Size(Window w, double width, double? height = null)
    {
        // Every sized window is also cleared to the theme background before its first paint (no white flash): belt and
        // braces for a window that forgot its own SourceInitialized hook (ThemeManager.ApplyTitleBar is idempotent).
        w.SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(w);
        var wa = SystemParameters.WorkArea;
        double f = WindowFactor;
        w.Width = Math.Min(width * f, wa.Width * 0.96);
        if (height != null) w.Height = Math.Min(height.Value * f, wa.Height * 0.96);
        w.MaxHeight = wa.Height * 0.98;
        Action onChange = () =>
        {
            double nf = WindowFactor, ratio = nf / f;
            f = nf;
            if (Math.Abs(ratio - 1) < 0.001) return;
            var area = SystemParameters.WorkArea;
            w.Width = Math.Min(w.ActualWidth * ratio, area.Width * 0.96);
            if (w.SizeToContent is SizeToContent.Manual or SizeToContent.Width) w.Height = Math.Min(w.ActualHeight * ratio, area.Height * 0.96);
        };
        bool on = false;
        w.Loaded += (_, _) => { if (!on) { on = true; Changed += onChange; } };
        w.Closed += (_, _) => { if (on) { on = false; Changed -= onChange; } };
    }

    /// <summary>Handlers on <see cref="Changed"/> (self-test: closed windows must leave none behind).</summary>
    public static int ChangedSubscribers => Changed?.GetInvocationList().Length ?? 0;
}

/// <summary>The roles of the type ramp (<see cref="Typography.Ramp"/>, same order).</summary>
public enum FontStep { Micro, Caption, Small, Meta, Body, Control, Base, Input, Title, Heading, H1, Code, Value, Icon }
