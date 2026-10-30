using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DcxManager.Services;
using ThemeMode = DcxManager.Services.ThemeMode;   // .NET 9+ WPF has System.Windows.ThemeMode too

namespace DcxManager.Views;

// SettingsView: the Appearance tab (Theme, Layout and text, Typography) and the Navigation tab (Sidebar, Navigation &
// explorers) — 0.2.1, the DarkCipher house theme's options, shared across the DarkCipher Systems apps.
public sealed partial class SettingsView
{
    /// <summary>A dropdown of (value, text) choices that applies on change.</summary>
    private ComboBox Choice<T>(IEnumerable<(T Value, string Text)> items, T current, Action<T> set, string tip, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(current))] string? currentExpr = null)
    {
        var cb = new ComboBox { ToolTip = tip };
        if (SettingFromExpression(currentExpr) is { } setting) SetSetting(cb, setting);   // 0.5.0: policy lock / 🛡
        foreach (var (v, t) in items) cb.Items.Add(new ComboBoxItem { Content = t, Tag = v });
        cb.SelectedItem = cb.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, current)) ?? cb.Items.OfType<ComboBoxItem>().FirstOrDefault();
        cb.SelectionChanged += (_, _) => { if (cb.SelectedItem is ComboBoxItem { Tag: T v } && !Equals(v, current)) { current = v; set(v); } };
        return cb;
    }

    private static ThemeMode ModeOf(string s) => s switch { "Dark" => ThemeMode.Dark, "Light" => ThemeMode.Light, _ => ThemeMode.System };

    // ---- Appearance → Theme -------------------------------------------------------------------------------------------

    private Border ThemeCard()
    {
        void Repaint() { ThemeManager.Apply(ModeOf(_s.Theme)); MainWindow.Instance?.SyncThemeBox(); }

        var mode = Choice(new[] { ("System", "Follow Windows (dark or light)"), ("Dark", "Dark"), ("Light", "Light") }, _s.Theme,
            v => { _s.Theme = v; Save(); Repaint(); }, "Dark or light. Follow Windows switches with the Windows app mode. The same choice as the selector in the header.");
        // Accent: swatches, or any colour that reaches 3:1 against the background (WCAG 2.2 non-text contrast).
        var custom = Bound(new TextBox { Text = _s.AccentColor, Width = 100, Padding = new Thickness(6, 4, 6, 4), ToolTip = "Six hex digits, e.g. 36D2CB" }.Mono(), nameof(AppSettings.AccentColor));
        // Theme packs (0.6.9): choosing one also brings its own accent, unless you picked a different accent yourself
        // (or your organisation sets it).
        var preset = Choice(ThemeManager.Presets.Select(p => (p.Id, $"{p.Name} · {p.Modes}")), _s.ThemePreset,
            v =>
            {
                var was = ThemeManager.Preset(_s.ThemePreset);
                _s.ThemePreset = v;
                if (string.Equals(_s.AccentColor, was.Accent, StringComparison.OrdinalIgnoreCase) && !PolicySettings.IsManaged(nameof(AppSettings.AccentColor)))
                    custom.Text = _s.AccentColor = ThemeManager.Preset(v).Accent;
                Save(); Repaint();
            }, "The theme pack: its dark and light palettes and its accent. The mode above picks dark or light; packs without a light palette use DarkCipher Light, High Contrast is always dark.");
        var presetNote = new TextBlock { Style = (Style)FindResource("Caption"), Text = ThemeManager.Preset(_s.ThemePreset).Description };
        var gallery = PresetGallery(preset);
        preset.SelectionChanged += (_, _) => presetNote.Text = ThemeManager.Preset(_s.ThemePreset).Description;
        System.Windows.Automation.AutomationProperties.SetName(custom, "Custom accent colour");
        var inUse = new TextBlock { Style = (Style)FindResource("Caption"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        void ShowInUse()
        {
            var painted = Application.Current.Resources["Accent"] is SolidColorBrush b ? $"{b.Color.R:X2}{b.Color.G:X2}{b.Color.B:X2}" : _s.AccentColor;
            inUse.Text = $"In use: #{_s.AccentColor}{(painted != _s.AccentColor ? $" (painted as #{painted})" : "")} · {ThemeManager.CheckAccent(_s.AccentColor).Ratio:0.0}:1 against the background";
        }
        void SetAccent(string hex)
        {
            var (ok, ratio) = ThemeManager.CheckAccent(hex);
            if (ThemeManager.ParseHex(hex) == null) { Dialogs.Error("Accent", "Use six hex digits, e.g. 36D2CB."); return; }
            if (!ok) { Dialogs.Error("Accent", $"#{hex.Trim().TrimStart('#').ToUpperInvariant()} reaches only {ratio:0.0}:1 against the background; accents need at least 3:1 so focus rings, selections and primary buttons stay visible. Pick a brighter colour (or a darker one in light mode)."); return; }
            _s.AccentColor = hex.Trim().TrimStart('#').ToUpperInvariant();
            custom.Text = _s.AccentColor;
            Save(); Repaint(); ShowInUse();
        }
        ShowInUse();
        // The ratio is against the background as painted, so it follows dark / light / preset changes from anywhere.
        Action onTheme = () => inUse.Dispatcher.BeginInvoke(ShowInUse);
        inUse.Loaded += (_, _) => { ThemeManager.Changed -= onTheme; ThemeManager.Changed += onTheme; };
        inUse.Unloaded += (_, _) => ThemeManager.Changed -= onTheme;
        var swatches = new WrapPanel();
        foreach (var (name, hex) in ThemeManager.AccentSwatches)
        {
            var dot = new Border { Width = 14, Height = 14, Background = new SolidColorBrush(ThemeManager.ParseHex(hex)!.Value), Margin = new Thickness(0, 0, 6, 0) };
            dot.SetResourceReference(Border.CornerRadiusProperty, "Radius");
            var label = new StackPanel { Orientation = Orientation.Horizontal };
            label.Children.Add(dot);
            label.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
            var b = new Button { Content = label, Style = (Style)FindResource("SmallButton"), Margin = new Thickness(0, 0, 6, 6), ToolTip = $"#{hex}" };
            System.Windows.Automation.AutomationProperties.SetName(b, "Accent " + name);
            var h = hex;
            b.Click += (_, _) => SetAccent(h);
            swatches.Children.Add(b);
        }
        var customRow = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        customRow.Children.Add(new TextBlock { Text = "Custom #", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
        customRow.Children.Add(custom);
        var apply = new Button { Content = "Apply", Margin = new Thickness(8, 0, 0, 0) };
        apply.Click += (_, _) => SetAccent(custom.Text);
        customRow.Children.Add(apply);
        customRow.Children.Add(inUse);

        var intensity = Choice(new[] { ("subtle", "Subtle"), ("standard", "Standard (default)"), ("strong", "Strong") }, _s.StatusIntensity,
            v => { _s.StatusIntensity = v; Save(); Repaint(); }, "How strong the good / warning / error colours are. Status is always written as text too; colour is never the only signal.");
        var hc = Check("Use the High Contrast preset while Windows high contrast is on", _s.FollowWindowsHighContrast, v => { _s.FollowWindowsHighContrast = v; Repaint(); });

        var addon = ThemeManager.ActiveTheme is { } t
            ? new TextBlock { Style = (Style)FindResource("Caption"), Text = $"◐ The theme add-on “{t.Manifest.Name}” is in use and sets the colours it names (the accent too); Add-ons → Back to the default theme restores the app's own.", Margin = new Thickness(0, 0, 0, 8) }
            : ThemeManager.ActivePack != null
            ? new TextBlock { Style = (Style)FindResource("Caption"), Text = "◐ Your branding (the next card) is in use and sets the colours it names; clearing it there restores the app's own.", Margin = new Thickness(0, 0, 0, 8) }
            : null;
        var children = new List<UIElement>();
        if (addon != null) children.Add(addon);
        children.AddRange(new UIElement[]
        {
            Labeled("MODE", mode), Labeled("THEME PACK", preset), gallery, presetNote,
            L("ACCENT COLOUR"), swatches, customRow,
            new TextBlock { Style = (Style)FindResource("Caption"), Text = "Custom accents must reach 3:1 contrast against the background, so focus rings and selections stay visible. Neon accents are darkened automatically in light mode." },
            Labeled("STATUS COLOUR INTENSITY", intensity), hc,
        });
        return Card("Theme", "Dark or light, the theme pack, the accent and how strong status colours are. Changes apply at once.", children.ToArray());
    }

    /// <summary>The theme packs as preview tiles (dark half, light half, accent): clicking one chooses it in the dropdown,
    /// so the policy lock and the accent hand-over stay in one place. The chosen pack is outlined in the accent.</summary>
    private WrapPanel PresetGallery(ComboBox preset)
    {
        var panel = new WrapPanel { Margin = new Thickness(0, 6, 0, 4) };
        var tiles = new List<(Border Frame, string Id)>();
        void Mark() { foreach (var (f, id) in tiles) { f.SetResourceReference(Border.BorderBrushProperty, id == _s.ThemePreset ? "Accent" : "Line"); f.BorderThickness = new Thickness(id == _s.ThemePreset ? 2 : 1); } }
        foreach (var p in ThemeManager.Presets)
        {
            Border Half(bool dark)
            {
                Brush B(string k) => new SolidColorBrush(ThemeManager.PresetColour(p.Id, k, dark));
                var accent = new SolidColorBrush(ThemeManager.AccentFor(ThemeManager.ParseHex(p.Accent)!.Value, dark, p.Id));
                var lines = new StackPanel { Margin = new Thickness(6) };
                lines.Children.Add(new Border { Height = 5, Width = 34, Background = B("Fg"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 4) });
                lines.Children.Add(new Border { Height = 4, Width = 24, Background = B("Muted"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 6) });
                lines.Children.Add(new Border { Height = 8, Width = 22, Background = accent, HorizontalAlignment = HorizontalAlignment.Left });
                return new Border { Background = B("Bg"), Width = 56, Height = 44, Child = new Border { Background = B("Surface"), Margin = new Thickness(4), Child = lines } };
            }
            var halves = new StackPanel { Orientation = Orientation.Horizontal };
            halves.Children.Add(Half(dark: true));
            if (!p.AlwaysDark) halves.Children.Add(Half(dark: false));   // packs without a light palette show DarkCipher Light: what light mode paints
            var text = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            // Short names fit the tile ("Graphite", not "DarkCipher Graphite"); the default says so on its second line.
            var tileName = p.Id == "darkcipher" ? "DarkCipher" : p.Name.StartsWith("DarkCipher ", StringComparison.Ordinal) ? p.Name["DarkCipher ".Length..] : p.Name;
            text.Children.Add(new TextBlock { Text = tileName, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 112 });
            var modes = new TextBlock { Text = p.Id == "darkcipher" ? "Default · " + p.Modes : p.Modes }.Fs(FontStep.Small);
            modes.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            text.Children.Add(modes);
            var body = new StackPanel { Margin = new Thickness(6) };
            body.Children.Add(halves);
            body.Children.Add(text);
            var frame = new Border { Child = body, Margin = new Thickness(0, 0, 8, 8), Cursor = System.Windows.Input.Cursors.Hand, ToolTip = p.Description, Focusable = true };
            frame.SetResourceReference(Border.CornerRadiusProperty, "Radius");
            frame.SetResourceReference(Border.BackgroundProperty, "Surface2");
            frame.SetBinding(IsEnabledProperty, new System.Windows.Data.Binding(nameof(IsEnabled)) { Source = preset });   // locked by policy: tiles locked too
            System.Windows.Automation.AutomationProperties.SetName(frame, "Theme pack " + p.Name);
            var id = p.Id;
            void Pick() { if (preset.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == id) is { } item) preset.SelectedItem = item; }
            frame.MouseLeftButtonUp += (_, _) => Pick();
            frame.KeyDown += (_, e) => { if (e.Key is System.Windows.Input.Key.Enter or System.Windows.Input.Key.Space) { e.Handled = true; Pick(); } };
            tiles.Add((frame, id));
            panel.Children.Add(frame);
        }
        Mark();
        preset.SelectionChanged += (_, _) => Mark();
        return panel;
    }

    /// <summary>Self-test: the theme pack tiles in Settings → Appearance (id and whether it is the outlined one).</summary>
    internal static IReadOnlyList<(string Name, bool Chosen)> PresetTilesForTest(DependencyObject root)
    {
        var found = new List<(string, bool)>();
        void Walk(DependencyObject d)
        {
            if (d is Border b && System.Windows.Automation.AutomationProperties.GetName(b) is { } n && n.StartsWith("Theme pack ", StringComparison.Ordinal))
                found.Add((n["Theme pack ".Length..], b.BorderThickness.Left >= 2));
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) Walk(VisualTreeHelper.GetChild(d, i));
        }
        Walk(root);
        return found;
    }

    // ---- Appearance → Branding & colours (0.4.0) ------------------------------------------------------------------------

    /// <summary>Make the app the organisation's: product name, tagline, logo, UI font and every colour of the palette
    /// (dark and light), kept in the encrypted settings and applied like a theme add-on. Exportable as a theme add-on
    /// (.dcxaddon) for colleagues; a company can push the theme text and a logo path by Group Policy.</summary>
    private Border BrandingCard()
    {
        var d = Branding.Current();
        byte[]? logo = null;
        try { logo = Branding.LogoBytes(); } catch (Exception ex) { App.Log("Branding logo", ex); }
        TextBox T(string text, double width, string tip) => new() { Text = text, Width = width, Padding = new Thickness(5, 4, 5, 4), ToolTip = tip };
        var name = T(d.ProductName, 260, "Shown in the header and the window title instead of “ServerManagement”; empty = the app's own name");
        var tagline = T(d.TaglineSet ? d.Tagline : "", 260, "The line under the name (empty hides the prompt line; leave the box untouched to keep the app's own)");
        var taglineSet = new CheckBox { Content = "Set the tagline (unticked = the app's own prompt line)", IsChecked = d.TaglineSet, Margin = new Thickness(0, 4, 0, 0) };
        var uiFont = T(d.UiFont, 260, "A font family list such as “Roboto, Segoe UI” (used when installed and no UI font is chosen in Typography)");
        var plate = T(d.LogoBackground, 100, "Six hex digits: a rounded plate behind the logo, for logos drawn for a light background; empty = none");
        var logoView = new Image { Height = 40, MaxWidth = 220, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 4) };
        var logoNote = new TextBlock { Style = (Style)FindResource("Caption") };
        void ShowLogo()
        {
            try { logoView.Source = logo != null ? Addons.ThemePack.DecodeLogo(logo) : null; logoNote.Text = logo != null ? $"PNG, {logo.Length / 1024} KB" : _s.BrandingLogoPath.Length > 0 ? "From policy: " + _s.BrandingLogoPath : "The app's own logo"; }
            catch (Exception ex) { logoView.Source = null; logoNote.Text = "✕ " + ex.Message; }
        }
        ShowLogo();

        // Every colour of the palette and the accent keys: dark and light, empty = the app's own.
        var keys = ThemeManager.PaletteKeys.Concat(Addons.ThemePack.AccentKeys).Distinct().ToList();
        var rows = new Dictionary<string, (TextBox Dark, TextBox Light, Border DarkDot, Border LightDot)>();
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 6) };
        for (int c = 0; c < 5; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = c == 0 ? new GridLength(150) : GridLength.Auto });
        void Head(string t, int col) { var h = new TextBlock { Text = t, Style = (Style)FindResource("Label"), Margin = new Thickness(0, 0, 10, 4) }; Grid.SetColumn(h, col); Grid.SetRow(h, 0); grid.Children.Add(h); }
        grid.RowDefinitions.Add(new RowDefinition());
        Head("COLOUR", 0); Head("DARK (#RRGGBB)", 1); Head("", 2); Head("LIGHT (#RRGGBB)", 3); Head("", 4);
        Border Dot() { var b = new Border { Width = 18, Height = 18, Margin = new Thickness(4, 0, 12, 0), BorderThickness = new Thickness(1) }; b.SetResourceReference(Border.BorderBrushProperty, "Line"); b.SetResourceReference(Border.CornerRadiusProperty, "Radius"); return b; }
        void Paint(TextBox box, Border dot, string key, bool dark)
        {
            var hex = box.Text.Trim().TrimStart('#');
            var colour = hex.Length == 0 ? ThemeManager.DefaultColour(key, dark) : ThemeManager.ParseHex(hex);
            dot.Background = colour is { } cc ? new SolidColorBrush(cc) : Brushes.Transparent;
            dot.ToolTip = hex.Length == 0 ? "the app's own colour" : colour == null ? "not a colour: six hex digits" : "#" + hex.ToUpperInvariant();
        }
        int r = 1;
        foreach (var key in keys)
        {
            grid.RowDefinitions.Add(new RowDefinition());
            d.Colours.TryGetValue(key, out var cur);
            var label = new TextBlock { Text = key, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 2) }.Mono();
            var dark = new TextBox { Text = cur.Dark ?? "", Width = 90, Padding = new Thickness(4, 2, 4, 2), Margin = new Thickness(0, 0, 0, 2) }.Mono();
            var light = new TextBox { Text = cur.Light ?? "", Width = 90, Padding = new Thickness(4, 2, 4, 2), Margin = new Thickness(0, 0, 0, 2) }.Mono();
            var dd = Dot(); var ld = Dot();
            var k = key;
            dark.TextChanged += (_, _) => Paint(dark, dd, k, true);
            light.TextChanged += (_, _) => Paint(light, ld, k, false);
            Paint(dark, dd, key, true); Paint(light, ld, key, false);
            System.Windows.Automation.AutomationProperties.SetName(dark, key + " dark"); System.Windows.Automation.AutomationProperties.SetName(light, key + " light");
            foreach (var (el, col) in new (UIElement, int)[] { (label, 0), (dark, 1), (dd, 2), (light, 3), (ld, 4) }) { Grid.SetRow(el, r); Grid.SetColumn(el, col); grid.Children.Add(el); }
            rows[key] = (dark, light, dd, ld);
            r++;
        }

        Branding.Draft Read()
        {
            var x = new Branding.Draft { ProductName = name.Text, TaglineSet = taglineSet.IsChecked == true, Tagline = tagline.Text, UiFont = uiFont.Text, LogoBackground = plate.Text };
            foreach (var (key, row) in rows) x.Colours[key] = (row.Dark.Text.Trim().TrimStart('#'), row.Light.Text.Trim().TrimStart('#'));
            return x;
        }
        void Fill(Branding.Draft x, bool colours = true)
        {
            name.Text = x.ProductName; taglineSet.IsChecked = x.TaglineSet; tagline.Text = x.Tagline; uiFont.Text = x.UiFont; plate.Text = x.LogoBackground;
            if (!colours) return;
            foreach (var (key, row) in rows) { x.Colours.TryGetValue(key, out var c); row.Dark.Text = c.Dark ?? ""; row.Light.Text = c.Light ?? ""; }
        }
        void Repaint() { ThemeManager.Apply(ModeOf(_s.Theme)); MainWindow.Instance?.SyncThemeBox(); MainWindow.Instance?.ApplyBranding(); }
        var status = new TextBlock { Style = (Style)FindResource("Caption"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };

        var buttons = Row(
            Btn("Apply", () =>
            {
                try { Branding.Apply(Read(), logo); Repaint(); status.Text = ThemeManager.ActivePack != null && ThemeManager.ActiveTheme == null ? "Branding applied." : ThemeManager.ActiveTheme != null ? "Saved — a theme add-on is in use and paints the app until Add-ons → Back to the default theme." : "Branding cleared (nothing set)."; }
                catch (Exception ex) when (ex is System.IO.InvalidDataException or FormatException) { Dialogs.Error("Branding", ex.Message); }
            }, "PrimaryButton"),
            Btn("Fill with the colours in use", () =>
            {
                // Every row gets the colour painted now, dark and light, so a custom palette starts from the real one.
                foreach (var (key, row) in rows) { var dc = ThemeManager.DefaultColour(key, true); var lc = ThemeManager.DefaultColour(key, false); row.Dark.Text = dc is { } a ? $"{a.R:X2}{a.G:X2}{a.B:X2}" : ""; row.Light.Text = lc is { } b ? $"{b.R:X2}{b.G:X2}{b.B:X2}" : ""; }
            }),
            Btn("Clear colours", () => { foreach (var (_, row) in rows) { row.Dark.Text = ""; row.Light.Text = ""; } }),
            Btn("Load logo (PNG)…", () =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Logo", Filter = "PNG image|*.png" };
                if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
                try { var bytes = System.IO.File.ReadAllBytes(dlg.FileName); Addons.ThemePack.DecodeLogo(bytes); logo = bytes; ShowLogo(); }
                catch (Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException or UnauthorizedAccessException) { Dialogs.Error("Logo", ex.Message); }
            }),
            Btn("Remove logo", () => { logo = null; ShowLogo(); }),
            Btn("Import theme.json…", () =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Theme", Filter = "Theme|theme.json;*.json" };
                if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
                try { Fill(Branding.Import(System.IO.File.ReadAllText(dlg.FileName))); status.Text = "Imported — press Apply to use it."; }
                catch (Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException or UnauthorizedAccessException) { Dialogs.Error("Import theme", ex.Message); }
            }),
            Btn("Export theme.json…", () =>
            {
                try { Menus.SaveText(Branding.Compose(Read(), logo != null ? "logo.png" : null), "theme.json", "Theme|*.json|All files|*.*"); }
                catch (Exception ex) when (ex is System.IO.InvalidDataException) { Dialogs.Error("Export theme", ex.Message); }
            }),
            Btn("Export as theme add-on (.dcxaddon)…", () =>
            {
                var id = Dialogs.Prompt("Theme add-on", "Add-on id (lower-case letters, digits, dots and hyphens, e.g. com.example.brand):", "com.example.brand")?.Trim();
                if (string.IsNullOrEmpty(id)) return;
                if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-z0-9][a-z0-9.-]{2,63}$")) { Dialogs.Error("Theme add-on", "The id is 3–64 lower-case letters, digits, dots and hyphens."); return; }
                var publisher = Dialogs.Prompt("Theme add-on", "Publisher (your organisation):", AppInfo.Company)?.Trim();
                if (string.IsNullOrEmpty(publisher)) return;
                var dlg = new Microsoft.Win32.SaveFileDialog { FileName = id + ".dcxaddon", Filter = "Add-on package|*.dcxaddon" };
                if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
                try
                {
                    System.IO.File.WriteAllBytes(dlg.FileName, Branding.ExportAddon(Read(), logo, id, name.Text.Trim().Length > 0 ? name.Text.Trim() + " theme" : "Branding", publisher));
                    AuditLog.Write("you", "appearance", "branding exported as a theme add-on", System.IO.Path.GetFileName(dlg.FileName));
                    status.Text = $"Exported → {dlg.FileName}. Colleagues install it from Add-ons → Install; it needs no permissions (nothing in it runs).";
                }
                catch (Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException or UnauthorizedAccessException) { Dialogs.Error("Export add-on", ex.Message); }
            }),
            Btn("Clear branding", () => { Branding.Clear(); logo = null; Fill(new Branding.Draft()); ShowLogo(); Repaint(); status.Text = "Branding cleared — the app's own look."; }, "DangerButton"));

        var policy = PolicySettings.IsManaged(nameof(AppSettings.BrandingTheme))
            ? new TextBlock { Style = (Style)FindResource("Caption"), Text = "⚠ The branding is set by your organisation (Group Policy “Branding theme”); changes here don't apply.", Margin = new Thickness(0, 0, 0, 6) }
            : new TextBlock { Style = (Style)FindResource("Caption"), Text = "For a whole organisation: push the exported theme.json text with the Group Policy “Branding theme” and a logo file path with “Branding logo path”, or distribute the .dcxaddon. A chosen theme add-on wins over this card.", Margin = new Thickness(0, 0, 0, 6) };

        return Card("Branding & colours", "Make the app yours: name, tagline, logo, UI font and every colour of the palette for dark and light mode. Empty boxes keep the app's own values; everything is kept in the encrypted settings.",
            policy,
            Row(Labeled("PRODUCT NAME", name), Labeled("TAGLINE", tagline)), taglineSet,
            Row(Labeled("UI FONT (optional)", uiFont), Labeled("LOGO PLATE (hex, optional)", plate)),
            L("LOGO (PNG, ≤ 1 MB, ≤ 2048 px)"), logoView, logoNote,
            L("COLOURS"), grid,
            buttons, status);
    }

    // ---- Appearance → Layout and text ---------------------------------------------------------------------------------

    private Border LayoutCard()
    {
        var density = Choice(new[] { ("comfortable", "Comfortable (default)"), ("compact", "Compact") }, _s.Density,
            v => { _s.Density = v; Save(); NavLayout.ApplyDensity(); }, "Row spacing in lists, tables, trees and the sidebar. Applies immediately.");
        var textSizes = new[] { 0.85, 0.9, 1.0, 1.1, 1.25, 1.5 };
        var text = Choice(textSizes.Select(s => (s, $"{s * 100:0}%" + (Math.Abs(s - 1) < 0.001 ? " (default)" : ""))), textSizes.OrderBy(s => Math.Abs(s - Typography.TextSize)).First(),
            v => Typography.Set(textSize: v), "Text only: makes interface text larger or smaller without enlarging spacing, borders or icons. Terminals keep their own size.");
        var scales = new[] { 0.9, 1.0, 1.1, 1.25, 1.5, 1.75 };
        var zoom = Choice(scales.Select(s => (s, $"{s * 100:0}%" + (Math.Abs(s - Typography.DefaultScale) < 0.001 ? " (default)" : ""))), scales.OrderBy(s => Math.Abs(s - Typography.UiScale)).First(),
            v => Typography.Set(scale: v), "Zooms the whole interface (text, controls, spacing and icons), including menus, tooltips and pop-out windows. Terminals keep their own size.");
        var corners = Choice(new[] { (0, "Square (0 px)"), (2, "DarkCipher (2 px, default)"), (4, "Rounded (4 px, cards 6 px)") }, _s.Corners,
            v => { _s.Corners = v; Save(); NavLayout.ApplyCorners(); }, "Corner radius of buttons, fields, cards and pop-ups.");
        var motion = Choice(new[] { ("windows", "Follow Windows (default)"), ("standard", "Standard"), ("reduced", "Reduced") }, _s.Motion,
            v => { _s.Motion = v; Save(); NavLayout.ApplyMotion(); }, "Reduced stops decorative movement such as the blinking header cursor. Follow Windows uses “Animation effects” in Windows settings.");
        return Card("Layout and text", "Applies immediately to every window, dialog, menu, tooltip and pop-out, and is kept after restart.",
            Labeled("DENSITY", density), Labeled("TEXT SIZE", text), Labeled("INTERFACE ZOOM", zoom), Labeled("CORNERS", corners), Labeled("ANIMATION", motion));
    }

    // ---- Navigation → Sidebar -----------------------------------------------------------------------------------------

    private Border SidebarCard()
    {
        void Rebuild() { Save(); NavLayout.ApplyDensity(); MainWindow.Instance?.BuildNav(); }
        var style = Choice(new[] { ("expanded", "Expanded (default)"), ("compact", "Compact (tighter rows)"), ("icons", "Icons only (names in tooltips)") }, _s.SidebarStyle,
            v => { _s.SidebarStyle = v; Rebuild(); }, "Icons only shows every visible page as an icon in a narrow strip; hover for its name. Screen readers read the names.");
        var width = Choice(NavLayout.SidebarWidths.Select(w => (w, $"{w} px" + (w == 214 ? " (default)" : ""))), NavLayout.SidebarWidths.OrderBy(w => Math.Abs(w - _s.SidebarWidth)).First(),
            v => { _s.SidebarWidth = v; Save(); MainWindow.Instance?.ApplySidebarWidth(); }, "Width of the expanded sidebar. You can still drag its edge; Ctrl+B hides it.");
        var entries = MainWindow.Instance?.NavEntries ?? Array.Empty<MainWindow.NavEntry>();
        var landing = Choice(new[] { ("", "Dashboard (default)") }.Concat(entries.Where(e => e.Key != "dashboard").Select(e => (e.Key, e.Title))), _s.LandingPage,
            v => { _s.LandingPage = v; Save(); }, "The screen opened when the app starts (workspace restore after a crash still reopens your tabs).");

        // 0.6.31 (ADR-109): the hosts panel beside every tab of the main window, pinned open or auto-collapsing.
        var hostsDock = Choice(new[] { ("pinned", "Pinned open (default)"), ("auto", "Auto-collapse") }, HostsView.FolderMode,
            v => HostsView.SetFolderMode(v), "The hosts panel on the left of the main window, beside every tab. Pinned: always open. Auto-collapse: folds to a slim Hosts strip; point at it to open the panel. Also the 📌 / 📍 on the panel itself. Sessions always open in the main tab bar.");
        // 0.6.4 (ADR-082): extra windows for other monitors, each with a host pane.
        var windowPane = Choice(new[] { ("pinned", "Pinned open (default)"), ("auto", "Auto-collapse") }, _s.WindowHostPane,
            v => { if (MainWindow.Instance?.Panes.Floats.FirstOrDefault() is { } fw) fw.SetMode(v); else { _s.WindowHostPane = v; Save(); } },
            "The host pane of extra windows (Ctrl+Shift+N, or a tab's menu → New window): pinned open, or folded to a Hosts tab at the window's edge that slides out when you point at it.");
        var restoreWindows = Bound(new CheckBox { Content = "Reopen extra windows where they were (monitor, position, size) when the app starts", IsChecked = _s.RestoreWorkspaceWindows, Margin = new Thickness(0, 6, 0, 0) }, nameof(AppSettings.RestoreWorkspaceWindows));
        restoreWindows.Click += (_, _) => { _s.RestoreWorkspaceWindows = restoreWindows.IsChecked == true; Save(); };

        var pinsNote = new TextBlock { Style = (Style)FindResource("Caption") };
        var pins = new WrapPanel();
        void FillPins()
        {
            pins.Children.Clear();
            pinsNote.Text = $"{_s.PinnedNavItems.Count} of {NavLayout.MaxPinned} pinned. Pinned pages sit in a PINNED section at the top of the sidebar; you can also right-click a page in the sidebar → Pin to the top.";
            foreach (var e in entries)
            {
                var key = e.Key;
                var c = Bound(new CheckBox { Content = e.Title, IsChecked = _s.PinnedNavItems.Contains(key), Margin = new Thickness(0, 4, 18, 0), MinWidth = 190, Style = (Style)FindResource("Switch") }, nameof(AppSettings.PinnedNavItems));
                c.IsEnabled = c.IsChecked == true || _s.PinnedNavItems.Count < NavLayout.MaxPinned;
                c.Click += (_, _) => { MainWindow.Instance?.SetNavPinned(key, c.IsChecked == true); FillPins(); };
                pins.Children.Add(c);
            }
        }
        FillPins();
        return Card("Sidebar", "",
            Labeled("SIDEBAR", style), Labeled("SIDEBAR WIDTH (expanded)", width), Labeled("OPEN ON START", landing), Labeled("HOSTS PANEL", hostsDock),Labeled("HOST PANE IN EXTRA WINDOWS", windowPane), restoreWindows,
            L($"PINNED PAGES (at most {NavLayout.MaxPinned})"), pinsNote, pins);
    }

    // ---- Navigation → Navigation & explorers (ADR-028) ----------------------------------------------------------------

    private Border NavigationCard()
    {
        // Sidebar sections: show/hide here, reorder by dragging headers or with their right-click menu.
        var sections = new WrapPanel();
        void FillSections()
        {
            sections.Children.Clear();
            var order = MainWindow.Instance?.NavOrder ?? MainWindow.NavSections.ToList();
            foreach (var name in order)
            {
                var n = name;
                var c = Bound(new CheckBox { Content = MainWindow.SectionTitle(n), IsChecked = !_s.HiddenNavSections.Contains(n), Margin = new Thickness(0, 4, 18, 0), MinWidth = 150, ToolTip = "Show this section in the sidebar", Style = (Style)FindResource("Switch") }, nameof(AppSettings.HiddenNavSections));
                c.Click += (_, _) => { MainWindow.Instance?.SetNavSectionHidden(n, c.IsChecked != true); FillSections(); };
                sections.Children.Add(c);
            }
        }
        FillSections();
        // Pages: show/hide each one (0.2.2); Settings always stays so hidden pages can come back.
        var pages = new WrapPanel();
        var pagesNote = new TextBlock { Style = (Style)FindResource("Caption") };
        void FillPages()
        {
            pages.Children.Clear();
            var entries = MainWindow.Instance?.NavEntries ?? Array.Empty<MainWindow.NavEntry>();
            int hidden = entries.Count(e => _s.HiddenNavItems.Contains(e.Key));
            pagesNote.Text = (hidden == 0 ? "Every page is shown." : $"{hidden} page(s) hidden.") + " A hidden page still opens from links, menus and quick connect; you can also right-click a page in the sidebar → Hide. Settings is always shown.";
            foreach (var e in entries)
            {
                var key = e.Key;
                var c = Bound(new CheckBox { Content = e.Title, IsChecked = !_s.HiddenNavItems.Contains(key), Margin = new Thickness(0, 4, 18, 0), MinWidth = 190, Style = (Style)FindResource("Switch") }, nameof(AppSettings.HiddenNavItems));
                if (key == NavLayout.AlwaysShownPage) { c.IsChecked = true; c.IsEnabled = false; }
                c.Click += (_, _) => { MainWindow.Instance?.SetNavHidden(key, c.IsChecked != true); FillPages(); };
                pages.Children.Add(c);
            }
        }
        FillPages();
        var up = new ComboBox { ItemsSource = MainWindow.Instance?.NavOrder ?? MainWindow.NavSections.ToList(), SelectedIndex = 0, ToolTip = "The section to move" };
        void Refresh() { var sel = up.SelectedItem as string; up.ItemsSource = MainWindow.Instance?.NavOrder; up.SelectedItem = sel; FillSections(); }
        return Card("Navigation & explorers",
            "The sidebar and the trees in Hosts, Cloud providers and the other explorers open collapsed, so the app starts clean. What you expand stays expanded while the app runs.",
            L("SECTIONS SHOWN IN THE SIDEBAR"),
            new TextBlock { Style = (Style)FindResource("Caption"), Text = "At least one stays visible. Pages of hidden sections are still reachable from links, Ctrl+K and tab menus. Drag a header in the sidebar to reorder." },
            sections,
            L("PAGES SHOWN IN THE SIDEBAR"), pagesNote, pages,
            Labeled("ORDER", up),
            Row(Btn("▲ Move up", () => { if (up.SelectedItem is string n) { MainWindow.Instance?.MoveNavSection(n, -1); Refresh(); } }),
                Btn("▼ Move down", () => { if (up.SelectedItem is string n) { MainWindow.Instance?.MoveNavSection(n, 1); Refresh(); } })),
            Check("Remember which sidebar sections I expanded when the app restarts (off: every start is collapsed)", _s.RememberNavExpansion, v =>
            {
                _s.RememberNavExpansion = v;
                if (!v) _s.ExpandedNavSections = new();
                else MainWindow.Instance?.SaveNavExpansion();   // what is expanded now is what the next start shows
            }),
            Check("The command palette (Ctrl+Shift+P) puts what I use most at the top (off: forgets what it learned)", _s.PaletteLearn, v =>
            {
                _s.PaletteLearn = v;
                if (!v) _s.PaletteRecent = new();
            }),
            Row(Btn("Unpin every Cloud providers screen", () => { _s.CloudPinned.Clear(); Save(); CloudWorkspaceView.Current?.Refresh(); MainWindow.Instance?.Status("Cloud provider pins cleared."); })));
    }

    // ---- Appearance → Typography --------------------------------------------------------------------------------------

    /// <summary>Fonts. Every control applies live through <see cref="Typography"/>; the preview uses the same resources as
    /// the rest of the app, so what it shows is what the app now uses. Text size and zoom are in Layout and text.</summary>
    private Border TypographyCard()
    {
        const string uiDefault = "Segoe UI Variable / Segoe UI (default)", monoDefault = "Cascadia Mono / Consolas (default)";
        var uiFont = new ComboBox { IsTextSearchEnabled = true, ToolTip = "Interface font: navigation, menus, settings, dialogs, tables, AI, notifications." };
        var monoFont = new ComboBox { IsTextSearchEnabled = true, ToolTip = "Code and data: addresses, ids, commands, log excerpts." };
        var editorFont = new ComboBox { IsTextSearchEnabled = true, ToolTip = "Notes and multi-line editors (SFTP / Commander edit, snippets)." };
        var termFont = new ComboBox { IsTextSearchEnabled = true, ToolTip = "Terminal only. Changing it never affects the interface, and UI fonts never change terminal character cells." };
        var termSize = new ComboBox { ItemsSource = Enumerable.Range(9, 20).ToList(), ToolTip = "Size for new terminal tabs (A− / A+ adjusts an open tab)." };
        var accents = new CheckBox { Content = new TextBlock { Text = "Terminal-style accents (buttons, tabs, sidebar and labels in the monospace font)", TextWrapping = TextWrapping.Wrap }, Margin = new Thickness(0, 8, 0, 0) };
        foreach (var (c, n) in new (FrameworkElement, string)[] { (uiFont, nameof(AppSettings.UiFont)), (monoFont, nameof(AppSettings.MonoFont)), (editorFont, nameof(AppSettings.EditorFont)), (termFont, nameof(AppSettings.TerminalFont)), (termSize, nameof(AppSettings.TerminalFontSize)), (accents, nameof(AppSettings.MonoAccents)) }) SetSetting(c, n);   // 0.5.0: policy lock / 🛡
        var missing = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        missing.SetResourceReference(TextBlock.ForegroundProperty, "Warn");

        var all = ThemeManager.InstalledFonts(false);
        var monos = ThemeManager.InstalledFonts(true);
        bool loading = false;
        void Fill(ComboBox cb, List<string> fonts, string def, string current)
        {
            var list = new List<string> { def };
            // A saved face that is no longer installed stays visible (marked) so the user sees why the default is in use.
            if (current.Length > 0 && !fonts.Contains(current, StringComparer.OrdinalIgnoreCase)) list.Add(current + "  (not installed)");
            list.AddRange(fonts);
            cb.ItemsSource = list;
            cb.SelectedIndex = current.Length == 0 ? 0 : Math.Max(0, list.FindIndex(x => x.Equals(current, StringComparison.OrdinalIgnoreCase) || x == current + "  (not installed)"));
        }
        string Pick(ComboBox cb) => cb.SelectedIndex <= 0 ? "" : ((string)cb.SelectedItem).Replace("  (not installed)", "");
        void Load()
        {
            loading = true;
            Fill(uiFont, all, uiDefault, _s.UiFont);
            Fill(monoFont, monos, monoDefault, _s.MonoFont);
            Fill(editorFont, all, monoDefault, _s.EditorFont);
            Fill(termFont, monos, monoDefault, _s.TerminalFont);
            termSize.SelectedItem = _s.TerminalFontSize;
            accents.IsChecked = _s.MonoAccents;
            var m = Typography.MissingChoices();
            missing.Text = m.Count == 0 ? "" : "⚠ " + string.Join("\n⚠ ", m);
            missing.Visibility = m.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            loading = false;
        }
        Load();
        uiFont.SelectionChanged += (_, _) => { if (!loading) Typography.Set(uiFont: Pick(uiFont)); };
        monoFont.SelectionChanged += (_, _) => { if (!loading) Typography.Set(monoFont: Pick(monoFont)); };
        editorFont.SelectionChanged += (_, _) => { if (!loading) Typography.Set(editorFont: Pick(editorFont)); };
        accents.Click += (_, _) => Typography.Set(monoAccents: accents.IsChecked == true);

        // Live preview: bound to the same resources as the app, so it changes the moment a font is picked.
        var preview = new StackPanel();
        var heading = new TextBlock { Text = "PROD-WEB-04 · Overview", Style = (Style)FindResource("H2") };
        var body = new TextBlock { Text = "The quick brown fox jumps over the lazy dog — 0O 1lI 5S 8B. Restart nginx after the certificate renewal.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
        var chrome = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        chrome.Children.Add(new Button { Content = "Connect", Style = (Style)FindResource("PrimaryButton"), IsHitTestVisible = false });
        chrome.Children.Add(new Button { Content = "Files", IsHitTestVisible = false });
        chrome.Children.Add(new TextBlock { Text = "LABEL TEXT", Style = (Style)FindResource("Label"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) });
        var code = new TextBlock { Text = "ssh root@192.0.2.10 -p 22   # code / monospace", Margin = new Thickness(0, 0, 0, 4) }.Mono();
        var editor = new TextBlock { Text = "14:06:17 Connected to PROD-WEB-04   # notes / editor", Margin = new Thickness(0, 0, 0, 4) }.Editor();
        var term = new Border { Padding = new Thickness(8, 5, 8, 5) };
        term.SetResourceReference(Border.CornerRadiusProperty, "Radius");
        term.SetResourceReference(Border.BackgroundProperty, "Input");
        var termText = new TextBlock { Text = "root@prod-web-04:~# systemctl status nginx   # terminal (approximate)" };
        termText.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        term.Child = termText;
        void UpdateTermPreview()
        {
            termText.FontFamily = new FontFamily(Typography.Resolve(_s.TerminalFont, Typography.DefaultMono));
            termText.FontSize = _s.TerminalFontSize;   // terminal sizes are not affected by UI text size: the preview undoes it
            termText.LayoutTransform = new ScaleTransform(1 / Typography.UiScale, 1 / Typography.UiScale);
        }
        UpdateTermPreview();
        termFont.SelectionChanged += (_, _) => { if (!loading) { Typography.SetTerminal(family: Pick(termFont)); UpdateTermPreview(); } };
        termSize.SelectionChanged += (_, _) => { if (!loading && termSize.SelectedItem is int f) { Typography.SetTerminal(size: f); UpdateTermPreview(); } };
        Action onScale = UpdateTermPreview;
        Loaded += (_, _) => Typography.Changed += onScale;
        Unloaded += (_, _) => Typography.Changed -= onScale;
        foreach (var e in new UIElement[] { heading, body, chrome, code, editor, term }) preview.Children.Add(e);
        var previewBox = new Border { BorderThickness = new Thickness(1), Padding = new Thickness(12, 10, 12, 10), Child = preview, Margin = new Thickness(0, 6, 0, 6) };
        previewBox.SetResourceReference(Border.CornerRadiusProperty, "RadiusCard");
        previewBox.SetResourceReference(Border.BorderBrushProperty, "Line");
        previewBox.SetResourceReference(Border.BackgroundProperty, "Bg");

        return Card("Typography", "Interface: Segoe UI Variable Text · code and consoles: Cascadia Mono · icons: Segoe Fluent Icons — all ship with Windows. A font that gets uninstalled falls back to the default. Terminal typography is separate from the interface; Remote Desktop content is never changed.",
            Labeled("UI FONT", uiFont), Labeled("CODE / MONOSPACE FONT", monoFont), Labeled("NOTES / EDITOR FONT", editorFont),
            Labeled("TERMINAL FONT", termFont), Labeled("TERMINAL FONT SIZE (new tabs)", termSize),
            accents, missing,
            L("LIVE PREVIEW"), previewBox,
            Row(Btn("Re-scan installed fonts", () => { Typography.RefreshInstalled(); Typography.Apply(); all = ThemeManager.InstalledFonts(false); monos = ThemeManager.InstalledFonts(true); Load(); })));
    }
}
