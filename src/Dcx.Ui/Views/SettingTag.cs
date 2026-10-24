using System.Windows;

namespace DcxManager.Views;

/// <summary>Which AppSettings property a Settings control edits (0.5.0, ADR-059). Settings greys out a control whose setting
/// Group Policy sets and marks admin-protected ones with 🛡; modules' cards (the AI's) tag their controls the same way.</summary>
public static class SettingTag
{
    public static readonly DependencyProperty SettingProperty =
        DependencyProperty.RegisterAttached("Setting", typeof(string), typeof(SettingTag), new PropertyMetadata(null));

    public static string? Get(DependencyObject d) => (string?)d.GetValue(SettingProperty);
    public static void Set(DependencyObject d, string? setting) => d.SetValue(SettingProperty, setting);

    /// <summary>Tags and returns the control (for use inside an expression).</summary>
    public static T For<T>(T control, string setting) where T : DependencyObject { Set(control, setting); return control; }
}
