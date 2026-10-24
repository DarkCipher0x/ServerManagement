using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using DcxManager.Services;

namespace DcxManager.Views;

// SettingsView: which setting each control edits, and what that means on screen (0.5.0, ADR-059). DarkCipher0x: "the settings like
// the syslog TLS config all need GPO controls and locking behind UAC". The values were already enforced (a policy wins on
// every load and save, protected settings change only through the admin gate), but the cards didn't say so: a field set by
// Group Policy stayed editable until Apply quietly put it back, and nothing marked the UAC-protected ones.
public sealed partial class SettingsView
{
    /// <summary>The AppSettings property a control edits ("SyslogHost"). Set with <see cref="Bound{T}"/> or, for switches,
    /// worked out by <see cref="Check"/> from its value expression.</summary>
    public static string? GetSetting(DependencyObject d) => SettingTag.Get(d);
    public static void SetSetting(DependencyObject d, string? v) => SettingTag.Set(d, v);

    /// <summary>Marks <paramref name="control"/> as the editor of <paramref name="setting"/> (nameof(AppSettings.X)).</summary>
    internal static T Bound<T>(T control, string setting) where T : DependencyObject { SetSetting(control, setting); return control; }

    private static readonly Regex SettingExpr = new(@"^\s*!?\s*_?s\.(?<p>[A-Z]\w*)\b", RegexOptions.CultureInvariant);

    /// <summary>The property named by a value expression such as "_s.SyslogEnabled" or "s.AiEnabled", or null.</summary>
    internal static string? SettingFromExpression(string? expr) =>
        expr != null && SettingExpr.Match(expr) is { Success: true } m && typeof(AppSettings).GetProperty(m.Groups["p"].Value) != null ? m.Groups["p"].Value : null;

    /// <summary>Every control of a card that names a setting: policy-set ones are disabled and say who set them;
    /// admin-protected ones carry 🛡. The card gets a short line for each, under its title. Re-run whenever the card is
    /// shown, so a policy refreshed while the app runs shows at once.</summary>
    internal void ApplyLocks(Border card)
    {
        if (card.Child is not StackPanel p) return;
        var managed = new List<string>(); var guarded = new List<string>();
        void Walk(DependencyObject d)
        {
            if (d is FrameworkElement fe && GetSetting(fe) is { } name)
            {
                var title = PolicySettings.Humanize(name);
                if (PolicySettings.Get(name) is { } v)
                {
                    fe.IsEnabled = false;
                    fe.ToolTip = $"🔒 Set by your organisation ({v.Scope} policy {name} = {v.Shown}). Ask your administrator to change it.";
                    if (!managed.Contains(title)) managed.Add(title);
                }
                else if (ProtectedSettings.Items.FirstOrDefault(i => i.Setting == name) is { } it)
                {
                    var tip = "🛡 Protected: changing it needs admin approval (UAC)" + (ProtectedSettings.IsProtected(it.Key) ? "." : " once admin protection is on (Security & privacy → Admin protection).");
                    if (fe.ToolTip is not string t || !t.StartsWith("🛡")) fe.ToolTip = fe.ToolTip is string old && old.Length > 0 ? tip + "\n" + old : tip;
                    if (!guarded.Contains(title)) guarded.Add(title);
                }
            }
            foreach (var c in LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>()) Walk(c);
        }
        foreach (var old in p.Children.OfType<TextBlock>().Where(t => t.Tag is "locks").ToList()) p.Children.Remove(old);
        Walk(p);
        int at = Math.Min(p.Children.Count, PlacementOf(TitleOf(card)) is { Scope: { } } ? 2 : 1);
        // 0.5.1 #11: AllowIntegrationEdit = false (Group Policy) leaves the integration cards readable but not changeable.
        bool frozen = CategoryOf(TitleOf(card)) == "Integrations" && !Restrictions.Allowed("integration.edit");
        foreach (var c in p.Children.OfType<UIElement>().Where(c => c is not TextBlock).ToList())
        {
            if (frozen && c.IsEnabled) { c.IsEnabled = false; _frozen.Add(c); }
            else if (!frozen && _frozen.Remove(c)) c.IsEnabled = true;
        }
        if (frozen) p.Children.Insert(at, LockLine("🔒 " + Restrictions.Why("integration.edit"), "Warn"));
        if (guarded.Count > 0)
            p.Children.Insert(at, LockLine($"🛡 Needs admin approval to change: {string.Join(", ", guarded)}.", "Accent"));
        if (managed.Count > 0)
            p.Children.Insert(at, LockLine($"🔒 Set by your organisation (Group Policy): {string.Join(", ", managed)}.", "Warn"));
    }

    /// <summary>Card rows disabled by the integration restriction (and only those are enabled again when it lifts).</summary>
    private readonly HashSet<UIElement> _frozen = new();

    private string TitleOf(Border card) => _cards.FirstOrDefault(kv => kv.Value == card).Key ?? "";

    private TextBlock LockLine(string text, string brush)
    {
        var t = new TextBlock { Text = text, Tag = "locks", TextWrapping = TextWrapping.Wrap, Style = (Style)FindResource("Caption"), Margin = new Thickness(0, 0, 0, 8) };
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }

    /// <summary>For the self-test: every setting some control of the built cards edits.</summary>
    internal IReadOnlyCollection<string> BoundSettingsForTest()
    {
        var set = new HashSet<string>();
        void Walk(DependencyObject d) { if (GetSetting(d) is { } n) set.Add(n); foreach (var c in LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>()) Walk(c); }
        foreach (var c in _cards.Values) Walk(c);
        return set;
    }
}
