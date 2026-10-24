using System.Windows;
using System.Windows.Controls;

namespace DcxManager.Views;

/// <summary>
/// Hint text shown in an empty text box ("Search hosts…"), drawn by the TextBox template in Themes/Controls.xaml.
/// <c>Hint.Set(box, "…")</c> in code or <c>v:Hint.Text="…"</c> in XAML. Never used for anything secret or required: it
/// only says what to type.
/// </summary>
public static class Hint
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.RegisterAttached("Text", typeof(string), typeof(Hint), new FrameworkPropertyMetadata(""));

    public static string GetText(DependencyObject o) => (string)o.GetValue(TextProperty);
    public static void SetText(DependencyObject o, string value) => o.SetValue(TextProperty, value);

    /// <summary>Sets the hint and returns the box (for object initialisers in code).</summary>
    public static T Set<T>(T box, string text) where T : TextBox { SetText(box, text); return box; }
}
