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
    /// <summary>0.6.29: the first tab of a run of the group's tabs in a strip; only it prints the name (the rest show the
    /// colour line), so a group takes less room.</summary>
    public static readonly DependencyProperty LeadProperty = DependencyProperty.RegisterAttached("Lead", typeof(bool), typeof(TabGroups), new PropertyMetadata(false));
    public static bool GetLead(DependencyObject o) => (bool)o.GetValue(LeadProperty);
    public static void SetLead(DependencyObject o, bool v) => o.SetValue(LeadProperty, v);

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
        foreach (var gone in Colours.Keys.Where(n => Members(n).Count == 0).ToList()) { Colours.Remove(gone); StackedNames.Remove(gone); Faces.Remove(gone); }
        Raise();
    }

    // 0.6.31 (DarkCipher0x: "when you "group" tabs, it can cascade the tabs? so it takes less space? but make it easy to
    // choose which tab you want"): a stacked group shows one tab per run, the one you used last, with the group's chip
    // saying how many it holds ("web 4 ▾"). Click the chip for a list of its tabs, or turn the mouse wheel over it to step
    // through them; Ctrl+Tab, "switch to the open tab" and the like still reach every tab (the one shown becomes the face).
    // An unstacked group's chip stacks it with one click.
    public static readonly DependencyProperty StackedProperty = DependencyProperty.RegisterAttached("Stacked", typeof(bool), typeof(TabGroups), new PropertyMetadata(false));
    public static bool GetStacked(DependencyObject o) => (bool)o.GetValue(StackedProperty);
    /// <summary>The chip's count on a stacked group's face tab ("4 ▾"), empty otherwise.</summary>
    public static readonly DependencyProperty StackTextProperty = DependencyProperty.RegisterAttached("StackText", typeof(string), typeof(TabGroups), new PropertyMetadata(""));
    public static string GetStackText(DependencyObject o) => (string)o.GetValue(StackTextProperty);
    /// <summary>Tabs this hides (only those are shown again: nothing else's visibility is touched).</summary>
    private static readonly DependencyProperty HiddenByStackProperty = DependencyProperty.RegisterAttached("HiddenByStack", typeof(bool), typeof(TabGroups), new PropertyMetadata(false));

    private static readonly HashSet<string> StackedNames = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The tab each stacked group shows when none of its tabs is in front.</summary>
    private static readonly Dictionary<string, WeakReference<TabItem>> Faces = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsStacked(string name) => StackedNames.Contains(name);

    /// <summary>Stacks a group into one tab (or spreads it out again).</summary>
    public static void SetStacked(string name, bool stacked)
    {
        if (name.Length == 0 || IsStacked(name) == stacked) return;
        if (stacked)
        {
            StackedNames.Add(name);
            var members = Members(name);
            if (members.FirstOrDefault(t => t.IsSelected) is { } front) Faces[name] = new(front);
        }
        else StackedNames.Remove(name);
        Raise();
    }

    /// <summary>The run of its group's tabs that <paramref name="face"/> sits in, in its strip (the chip's picker).</summary>
    public static IReadOnlyList<TabItem> StackOf(TabItem face)
    {
        if (ItemsControl.ItemsControlFromItemContainer(face) is not { } strip) return new[] { face };
        var name = GetName(face);
        var items = strip.Items.OfType<TabItem>().ToList();
        int i = items.IndexOf(face);
        if (name.Length == 0 || i < 0) return new[] { face };
        int a = i, b = i;
        while (a > 0 && GetName(items[a - 1]).Equals(name, StringComparison.OrdinalIgnoreCase)) a--;
        while (b < items.Count - 1 && GetName(items[b + 1]).Equals(name, StringComparison.OrdinalIgnoreCase)) b++;
        return items.GetRange(a, b - a + 1);
    }

    /// <summary>Marks the first tab of each run of one group's tabs in <paramref name="strip"/> (0.6.29), and folds each run
    /// of a stacked group to its face tab (0.6.31).</summary>
    public static void RefreshLeads(ItemsControl strip)
    {
        var items = strip.Items.OfType<TabItem>().ToList();
        for (int i = 0; i < items.Count;)
        {
            var n = GetName(items[i]);
            int j = i + 1;
            if (n.Length > 0) while (j < items.Count && GetName(items[j]).Equals(n, StringComparison.OrdinalIgnoreCase)) j++;
            var run = items.GetRange(i, j - i);
            bool stack = n.Length > 0 && run.Count > 1 && IsStacked(n);
            TabItem? face = null;
            if (stack)
            {
                face = run.FirstOrDefault(t => t.IsSelected);
                if (face != null) Faces[n] = new(face);
                else if (Faces.TryGetValue(n, out var w) && w.TryGetTarget(out var last) && run.Contains(last)) face = last;
                face ??= run[0];
            }
            foreach (var t in run)
            {
                bool shown = !stack || t == face;
                SetLead(t, n.Length > 0 && (stack ? shown : t == run[0]));
                t.SetValue(StackedProperty, stack && shown);
                t.SetValue(StackTextProperty, stack && shown ? $"{run.Count} ▾" : "");
                if (!shown) { t.Visibility = Visibility.Collapsed; t.SetValue(HiddenByStackProperty, true); }
                else if ((bool)t.GetValue(HiddenByStackProperty)) { t.Visibility = Visibility.Visible; t.ClearValue(HiddenByStackProperty); }
            }
            i = j;
        }
    }

    private static void Raise()
    {
        foreach (var strip in AllTabs.Select(ItemsControl.ItemsControlFromItemContainer).OfType<ItemsControl>().Distinct().ToList()) RefreshLeads(strip);
        try { Changed?.Invoke(); } catch (Exception ex) { Log.Write("tab groups changed", ex); }
    }
}
