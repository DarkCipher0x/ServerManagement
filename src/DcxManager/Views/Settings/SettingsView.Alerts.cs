using System.Windows;
using System.Windows.Controls;
using DcxManager.Services;
using DcxManager.Sessions;

namespace DcxManager.Views;

// SettingsView: terminal alerts, keyword highlighting and the snippet button bar (0.6.29, ADR-104).
public sealed partial class SettingsView
{
    private Border AlertsCard()
    {
        var secs = Bound(new ComboBox { Width = 110, ItemsSource = new[] { 0, 10, 30, 60, 120, 300, 600 }, SelectedItem = new[] { 0, 10, 30, 60, 120, 300, 600 }.Contains(_s.TerminalAlertSeconds) ? _s.TerminalAlertSeconds : 30 }, nameof(AppSettings.TerminalAlertSeconds));
        secs.SelectionChanged += (_, _) => { if (secs.SelectedItem is int v) { _s.TerminalAlertSeconds = v; Save(); } };
        return Card("Alerts",
            "When a terminal tab you aren't looking at needs you, its tab gets a dot, the taskbar button flashes and (when the app isn't in front) Windows shows a notification. Clicking the notification brings the tab back. A tab can switch its own alerts off from its menu. Nothing about the alert leaves this PC.",
            Check("Alerts from terminals you aren't looking at", _s.TerminalAlerts, v => _s.TerminalAlerts = v),
            Labeled("A COMMAND THAT RAN AT LEAST THIS LONG (SECONDS; 0 = NEVER) ALERTS WHEN IT FINISHES", secs),
            Check("The terminal bell (BEL) alerts", _s.TerminalAlertBell, v => _s.TerminalAlertBell = v),
            Check("A dropped session alerts", _s.TerminalAlertDrop, v => _s.TerminalAlertDrop = v),
            Check("Windows notifications when the app isn't in front (otherwise the dot and the taskbar flash only)", _s.TerminalAlertDesktop, v => _s.TerminalAlertDesktop = v));
    }

    private Border HighlightCard()
    {
        var packs = new WrapPanel { Margin = new Thickness(0, 4, 0, 4) };
        var lockedPacks = PolicySettings.Get(nameof(AppSettings.TerminalHighlightPacks));
        foreach (var p in Highlighting.Packs)
        {
            var c = new CheckBox { Content = p.Title, ToolTip = p.Example, IsChecked = _s.TerminalHighlightPacks.Contains(p.Id, StringComparer.OrdinalIgnoreCase), Margin = new Thickness(0, 4, 16, 0), IsEnabled = lockedPacks == null };
            if (lockedPacks != null) c.ToolTip = $"Set by your organisation's policy ({lockedPacks.Scope} policy TerminalHighlightPacks = {lockedPacks.Shown}).";
            var id = p.Id;
            c.Click += (_, _) =>
            {
                _s.TerminalHighlightPacks.RemoveAll(x => x.Equals(id, StringComparison.OrdinalIgnoreCase));
                if (c.IsChecked == true) _s.TerminalHighlightPacks.Add(id);
                Save();
            };
            packs.Children.Add(c);
        }

        var rules = new ListBox { Height = 130, DisplayMemberPath = nameof(HighlightRule.Name) };
        var name = new TextBox { Width = 180 };
        var pattern = new TextBox { Width = FieldWidth }.Mono();
        var colour = new ComboBox { Width = 110, ItemsSource = Highlighting.Colours, SelectedIndex = 1 };
        var cs = new CheckBox { Content = "Match case", Margin = new Thickness(0, 6, 14, 0) };
        var alert = new CheckBox { Content = "Alert when it shows in a tab I'm not looking at", Margin = new Thickness(0, 6, 14, 0) };
        var on = new CheckBox { Content = "On", IsChecked = true, Margin = new Thickness(0, 6, 14, 0) };
        var result = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        result.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        void Reload(HighlightRule? select = null) { rules.ItemsSource = null; rules.ItemsSource = _s.TerminalHighlightRules; rules.SelectedItem = select; }
        rules.SelectionChanged += (_, _) =>
        {
            if (rules.SelectedItem is not HighlightRule r) return;
            name.Text = r.Name; pattern.Text = r.Pattern; colour.SelectedItem = r.Colour; cs.IsChecked = r.CaseSensitive; alert.IsChecked = r.Alert; on.IsChecked = r.Enabled;
        };
        HighlightRule? Read(HighlightRule into)
        {
            if (Highlighting.Check(pattern.Text) is { } why) { result.Text = "✕ " + why; return null; }
            into.Name = name.Text.Trim().Length > 0 ? name.Text.Trim() : pattern.Text.Trim();
            into.Pattern = pattern.Text; into.Colour = colour.SelectedItem as string ?? "yellow";
            into.CaseSensitive = cs.IsChecked == true; into.Alert = alert.IsChecked == true; into.Enabled = on.IsChecked == true;
            return into;
        }
        void Saved(string what, HighlightRule r)
        {
            Save(); Reload(r);
            AuditLog.Write("you", "terminal", "highlight rule " + what, r.Name);
            result.Text = $"✓ {char.ToUpper(what[0]) + what[1..]}. Open terminals colour new output with it.";
        }
        var add = Btn("Add rule", () =>
        {
            if (!Restrictions.Check("settings.edit")) return;
            if (_s.TerminalHighlightRules.Count >= 100) { result.Text = "✕ Up to 100 rules."; return; }
            if (Read(new HighlightRule()) is { } r) { _s.TerminalHighlightRules.Add(r); Saved("added", r); }
        }, "PrimaryButton");
        var update = Btn("Update", () => { if (rules.SelectedItem is HighlightRule r && Read(r) is { }) Saved("updated", r); });
        var remove = Btn("Remove", () =>
        {
            if (rules.SelectedItem is not HighlightRule r || !Dialogs.Confirm("Remove rule", $"Remove the highlight rule “{r.Name}”?", "Remove", danger: true)) return;
            _s.TerminalHighlightRules.Remove(r); Save(); Reload();
            AuditLog.Write("you", "terminal", "highlight rule removed", r.Name);
        });
        var test = Btn("Check pattern", () => result.Text = Highlighting.Check(pattern.Text) is { } why ? "✕ " + why : "✓ The pattern is fine (and fast on long lines).");
        Reload();

        return Card("Highlighting & button bar",
            "Colours words in terminal output as it arrives: errors red, warnings yellow, success green, addresses cyan, plus your own patterns (regular expressions). Only the drawing changes, never the output, a copy or a log; full-screen programs (vim, top) are left alone. Each pattern is checked for speed before it's used. Ctrl+click an IP address, a path or a web link to act on it. Snippets marked for the button bar show as buttons above each terminal they fit (Snippets page).",
            Check("Highlight keywords in terminal output", _s.TerminalHighlight, v => _s.TerminalHighlight = v),
            L("BUILT-IN PACKS"), packs,
            L("YOUR RULES (DRAWN ON TOP OF THE PACKS)"), rules,
            Row(Labeled("NAME", name), Labeled("PATTERN (REGULAR EXPRESSION)", pattern), Labeled("COLOUR", colour)),
            Row(cs, alert, on),
            Row(add, update, remove, test), result,
            Check("Ctrl+click IP addresses, paths and web links in output (shows a menu; nothing opens without a click there)", _s.TerminalLinks, v => _s.TerminalLinks = v),
            Check("Snippet button bar above terminals (snippets marked “Show on the terminal button bar”)", _s.TerminalButtonBar, v => _s.TerminalButtonBar = v));
    }
}
