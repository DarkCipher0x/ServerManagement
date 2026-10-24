using System.Windows;
using System.Windows.Controls;
using DcxManager.Services;

namespace DcxManager.Views;

/// <summary>
/// Shared presentation of the network trust boundary (see <see cref="NetworkPolicy"/>). Anything that can make data leave
/// this workstation is drawn with <see cref="External"/>: a ⚠ icon, an uppercase heading, a thick warning bar and an
/// explanation, so it never relies on colour alone and reads the same in dark, light and custom-accent themes.
/// </summary>
public static class NetUi
{
    /// <summary>A small pill: glyph + class name. Warning colour only for External; the glyph and words carry the meaning.</summary>
    public static Border Badge(NetClass c, string? detail = null)
    {
        var t = new TextBlock { Text = $"{NetworkPolicy.Glyph(c)} {NetworkPolicy.Label(c)}" + (detail is { Length: > 0 } ? " · " + detail : ""), FontWeight = FontWeights.SemiBold }.Fs(FontStep.Caption);
        t.SetResourceReference(TextBlock.ForegroundProperty, c == NetClass.External ? "Warn" : c == NetClass.UserConfigured ? "Fg" : "Muted");
        var b = new Border { Child = t, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2), Padding = new Thickness(6, 1, 6, 2), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 3, 0, 0), ToolTip = NetworkPolicy.Explain(c) };
        b.SetResourceReference(Border.BorderBrushProperty, c == NetClass.External ? "Warn" : "Line");
        return b;
    }

    /// <summary>A badge under a URL box that re-classifies as the user types.</summary>
    public static ContentControl LiveBadge(TextBox url)
    {
        var host = new ContentControl();
        void Update() => host.Content = Badge(NetworkPolicy.Classify(url.Text));
        url.TextChanged += (_, _) => Update();
        Update();
        return host;
    }

    /// <summary>The warning treatment for a control that opens the external trust boundary.</summary>
    public static Border External(string heading, string explanation, params UIElement[] controls)
    {
        var p = new StackPanel();
        var h = new TextBlock { Text = "⚠ " + heading.ToUpperInvariant(), FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 2) }.Fs(FontStep.Meta);
        h.SetResourceReference(TextBlock.ForegroundProperty, "Warn");
        h.Use("ChromeFont");
        p.Children.Add(h);
        foreach (var c in controls) p.Children.Add(c);
        var ex = new TextBlock { Text = explanation, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) }.Fs(FontStep.Meta);
        ex.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        p.Children.Add(ex);
        var b = new Border { Child = p, BorderThickness = new Thickness(4, 1, 1, 1), CornerRadius = new CornerRadius(2), Padding = new Thickness(10, 6, 10, 8), Margin = new Thickness(0, 8, 0, 4), Tag = "external" };
        b.SetResourceReference(Border.BorderBrushProperty, "Warn");
        return b;
    }

    /// <summary>One-time confirmation when a setting moves the trust boundary outward. Not repeated on normal requests, and
    /// shown even when "Ask before every change" is off (it is a privacy decision, not a routine confirmation).</summary>
    public static bool ConfirmEnable(string feature, string endpoint) =>
        Dialogs.Choose("Enable external connection?",
            $"{feature} communicates with:\n\n    {endpoint}\n\nInformation may leave this workstation depending on how you use this integration. " +
            "You can switch it off again at any time; nothing is sent until you use the feature.",
            new[] { "Cancel", "Enable" }, cancelIndex: 0) == 1;
}
