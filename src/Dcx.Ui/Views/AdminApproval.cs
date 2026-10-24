using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using DcxManager.Services;

namespace DcxManager.Views;

/// <summary>
/// The one look for an action that asks Windows for admin approval (UAC) (0.5.1 #1): the verb, then "(admin approval)"
/// one step smaller and in italics (same colour as the verb), then "…" — "Apply (admin approval)…". Every such button uses it, so the wording
/// is the same everywhere and a self-test can find them (<see cref="Marker"/>).
/// </summary>
public static class AdminApproval
{
    /// <summary>The words shown after the verb.</summary>
    public const string Words = "(admin approval)";
    /// <summary>The automation name suffix every approval button carries (screen readers, the self-test).</summary>
    public const string Marker = " (admin approval)";

    /// <summary>The button content for <paramref name="verb"/> ("Apply", "Save", "Turn on").</summary>
    public static TextBlock Content(string verb)
    {
        var t = new TextBlock();
        t.Inlines.Add(new Run(verb + " "));
        var small = new Run(Words) { FontStyle = FontStyles.Italic };
        small.SetResourceReference(TextElement.FontSizeProperty, DcxManager.Services.Typography.Ramp[(int)FontStep.Small].Key);
        // Same colour as the verb (DarkCipher0x, 0.5.2): smaller and italic only, never muted.
        t.Inlines.Add(small);
        t.Inlines.Add(new Run("…"));
        return t;
    }

    /// <summary>A button with <see cref="Content"/>; <paramref name="style"/> is a resource key ("PrimaryButton").</summary>
    public static Button Button(string verb, Action click, string? style = "PrimaryButton", string? tip = null)
    {
        var b = new Button { Content = Content(verb), ToolTip = tip ?? "Windows asks an administrator to approve this change (UAC)." };
        if (style != null && Application.Current?.TryFindResource(style) is Style st) b.Style = st;
        System.Windows.Automation.AutomationProperties.SetName(b, verb + Marker);
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>The plain text of a button's content ("Apply (admin approval)…"), for search and tests.</summary>
    public static string TextOf(object? content) => content switch
    {
        string s => s,
        TextBlock tb => string.Concat(tb.Inlines.OfType<Run>().Select(r => r.Text)),
        _ => content?.ToString() ?? "",
    };
}
