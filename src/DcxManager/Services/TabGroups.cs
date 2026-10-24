using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DcxManager.Services;

/// <summary>
/// Tab groups (spec §65, ADR-019): a name on a set of document tabs, shown in each tab's header in the group's colour
/// (the name is always printed). Groups live for this session only. The DocTab template reads the attached properties.
/// </summary>
public static class TabGroups
{
    public static readonly DependencyProperty NameProperty = DependencyProperty.RegisterAttached("Name", typeof(string), typeof(TabGroups), new PropertyMetadata(""));
    public static readonly DependencyProperty BrushProperty = DependencyProperty.RegisterAttached("Brush", typeof(Brush), typeof(TabGroups), new PropertyMetadata(null));
    public static readonly DependencyProperty ShownProperty = DependencyProperty.RegisterAttached("Shown", typeof(bool), typeof(TabGroups), new PropertyMetadata(false));

    public static string GetName(DependencyObject o) => (string)o.GetValue(NameProperty);
    public static void SetName(DependencyObject o, string v) => o.SetValue(NameProperty, v);
    public static Brush? GetBrush(DependencyObject o) => (Brush?)o.GetValue(BrushProperty);
    public static void SetBrush(DependencyObject o, Brush? v) => o.SetValue(BrushProperty, v);
    public static bool GetShown(DependencyObject o) => (bool)o.GetValue(ShownProperty);
    public static void SetShown(DependencyObject o, bool v) => o.SetValue(ShownProperty, v);

    /// <summary>Readable on dark and light backgrounds; a group keeps its colour while it exists.</summary>
    private static readonly string[] Palette = { "#2F81F7", "#A855F7", "#D97706", "#16A34A", "#DB2777", "#0891B2" };
    private static readonly Dictionary<string, int> Colours = new(StringComparer.OrdinalIgnoreCase);

    public static event Action? Changed;

    private static IEnumerable<TabItem> AllTabs => MainWindow.Instance?.Panes.AllTabs ?? Enumerable.Empty<TabItem>();

    public static IReadOnlyList<string> Names => AllTabs.Select(GetName).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    public static IReadOnlyList<TabItem> Members(string name) => AllTabs.Where(t => GetName(t).Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();

    public static string? Validate(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "Give the group a name." : name.Trim().Length > 24 ? "Keep it to 24 characters." : null;

    public static void Assign(TabItem tab, string name)
    {
        name = name.Trim();
        if (Validate(name) != null) return;
        // Same group, same spelling: reuse the existing name's case.
        name = Names.FirstOrDefault(n => n.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? name;
        if (!Colours.ContainsKey(name)) Colours[name] = Enumerable.Range(0, Palette.Length).OrderBy(i => Colours.Values.Count(v => v == i)).ThenBy(i => i).First();
        SetName(tab, name);
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(Palette[Colours[name]]));
        b.Freeze();
        SetBrush(tab, b);
        SetShown(tab, true);
        Raise();
    }

    public static void Remove(TabItem tab)
    {
        tab.ClearValue(NameProperty); tab.ClearValue(BrushProperty); tab.ClearValue(ShownProperty);
        foreach (var gone in Colours.Keys.Where(n => Members(n).Count == 0).ToList()) Colours.Remove(gone);
        Raise();
    }

    private static void Raise() { try { Changed?.Invoke(); } catch (Exception ex) { Log.Write("tab groups changed", ex); } }
}
