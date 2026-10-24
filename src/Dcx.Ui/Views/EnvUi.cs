using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DcxManager.Services;

namespace DcxManager.Views;

/// <summary>
/// The environment labels on screen (spec §55, ADR-017): coloured pills, the live strip on a host's view, and the
/// environment of whatever a tab shows. The vocabulary and the PRODUCTION safeguards are <see cref="Envs"/> (Core); this
/// is only their WPF face (split out in 0.2.0 phase 6, so the code below the screens can use the labels without WPF).
/// </summary>
public static class EnvUi
{
    public static Brush BrushFor(string? env) => Application.Current.TryFindResource(Envs.BrushKey(env)) as Brush ?? Brushes.Gray;

    /// <summary>A coloured label ("PRODUCTION · web-01" or just "LAB"). Collapsed when there is no environment.</summary>
    public static Border Pill(string? env, string? suffix = null, bool small = false)
    {
        var text = new TextBlock { FontWeight = FontWeights.Bold }.Fs(small ? FontStep.Micro : FontStep.Caption);
        text.SetResourceReference(TextBlock.ForegroundProperty, "Bg");
        var b = new Border { CornerRadius = new CornerRadius(2), Padding = new Thickness(6, 1, 6, 1), VerticalAlignment = VerticalAlignment.Center, Child = text };
        SetPill(b, env, suffix);
        return b;
    }

    public static void SetPill(Border b, string? env, string? suffix = null)
    {
        var e = Envs.Normalize(env);
        if (b.Child is TextBlock t) t.Text = e.Length == 0 ? "" : e + (string.IsNullOrEmpty(suffix) ? "" : " · " + suffix);
        b.SetResourceReference(Border.BackgroundProperty, Envs.BrushKey(e));
        b.Visibility = e.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        b.ToolTip = Envs.IsProduction(e) ? "PRODUCTION: disruptive actions ask you to type the host's name." : e.Length > 0 ? $"Environment: {e}" : null;
    }

    /// <summary>A strip for the top of a host's view that follows the host's environment live (subscribed only while shown).</summary>
    public static Border Strip(Func<HostEntry?> host, bool localWhenNoHost = true, bool withName = true)
    {
        var pill = Pill("", null);
        pill.Margin = new Thickness(0, 0, 8, 0);
        void Refresh()
        {
            var h = host();
            var e = h == null ? (localWhenNoHost ? Envs.Local : "") : Envs.For(h);
            SetPill(pill, e, h == null || !withName ? null : Envs.Live(h).Display);
        }
        Action onChange = () => pill.Dispatcher.BeginInvoke(Refresh);
        pill.Loaded += (_, _) => { Envs.Changed += onChange; Refresh(); };
        pill.Unloaded += (_, _) => Envs.Changed -= onChange;
        Refresh();
        return pill;
    }

    /// <summary>The environment of whatever a tab shows: a host's view → the host's; a local shell / WSL → LOCAL; else none
    /// (a serial console's device has no saved host, so it isn't guessed). The app registers how it reads its screens
    /// (<see cref="ViewEnvironment"/>): this kit doesn't know them.</summary>
    public static string ForView(object? content) => ViewEnvironment(content);

    public static Func<object?, string> ViewEnvironment { get; set; } = _ => "";
}

/// <summary>The environment label on a document tab (the DocTab template shows it and colours the tab's top edge).</summary>
public static class EnvTab
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.RegisterAttached("Label", typeof(string), typeof(EnvTab), new PropertyMetadata(""));
    public static readonly DependencyProperty BrushProperty = DependencyProperty.RegisterAttached("Brush", typeof(Brush), typeof(EnvTab), new PropertyMetadata(null));
    public static readonly DependencyProperty ShownProperty = DependencyProperty.RegisterAttached("Shown", typeof(bool), typeof(EnvTab), new PropertyMetadata(false));

    public static string GetLabel(DependencyObject o) => (string)o.GetValue(LabelProperty);
    public static void SetLabel(DependencyObject o, string v) => o.SetValue(LabelProperty, v);
    public static Brush? GetBrush(DependencyObject o) => (Brush?)o.GetValue(BrushProperty);
    public static void SetBrush(DependencyObject o, Brush? v) => o.SetValue(BrushProperty, v);
    public static bool GetShown(DependencyObject o) => (bool)o.GetValue(ShownProperty);
    public static void SetShown(DependencyObject o, bool v) => o.SetValue(ShownProperty, v);

    /// <summary>Reads the tab content's environment and updates the header (call again after a host's environment changes).</summary>
    public static void Apply(TabItem tab)
    {
        var env = EnvUi.ForView(tab.Content);
        SetLabel(tab, Envs.Short(env));
        SetShown(tab, env.Length > 0);
        if (env.Length > 0) tab.SetResourceReference(BrushProperty, Envs.BrushKey(env)); else tab.ClearValue(BrushProperty);
        tab.ToolTip = env.Length > 0 ? $"{env}" + (Envs.IsProduction(env) ? " — disruptive actions ask you to type the host's name" : "") : null;
    }
}
