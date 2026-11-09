using System.Windows;
using System.Windows.Controls;
using DcxManager.Services;

namespace DcxManager.Views;

// SettingsView: Security & privacy → History (0.6.37, ADR-116). One switch for every history the app keeps (off by
// default: nothing is remembered), when kept history is cleared automatically, and each kind with its count and its own
// Clear button. Group Policy can set the switch and the automatic clear (then they only show).
public sealed partial class SettingsView
{
    internal const string HistoryCardTitle = "History";

    private Border HistoryCard()
    {
        var rows = new StackPanel();
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 4) };
        Button clearAll = null!;   // assigned below; Render reads it after
        clearAll = Btn("Clear all history…", () =>
        {
            int n = ActivityHistory.Total;
            if (!Dialogs.Confirm("Clear history", $"Clear every history the app keeps{(n > 0 ? $" ({n} entries)" : "")}?\n\nPinned lines, saved hosts and portals, notes, reports and the audit log stay.", "Clear", danger: true)) return;
            ActivityHistory.ClearAll("you", "all history cleared");
            MainWindow.Instance.Status("History cleared.");
            Render();
        }, "SmallButton");

        void Render()
        {
            rows.Children.Clear();
            foreach (var src in ActivityHistory.Sources)
            {
                int n = ActivityHistory.SafeCount(src);
                var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
                var clear = Btn("Clear", () => { ActivityHistory.Clear(src.Id); MainWindow.Instance.Status(src.Title + " cleared."); Render(); }, "SmallButton");
                clear.IsEnabled = n != 0;
                clear.ToolTip = "Clear " + src.Title.ToLowerInvariant();
                System.Windows.Automation.AutomationProperties.SetName(clear, "Clear " + src.Title);
                DockPanel.SetDock(clear, Dock.Right);
                row.Children.Add(clear);
                var text = new StackPanel();
                text.Children.Add(new TextBlock { Text = $"{src.Title} · {(n < 0 ? "kept by the portal browser" : n == 0 ? "none" : n == 1 ? "1 entry" : n + " entries")}", TextWrapping = TextWrapping.Wrap });
                text.Children.Add(new TextBlock { Text = src.Where, Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap });
                row.Children.Add(text);
                rows.Children.Add(row);
            }
            status.Text = !ActivityHistory.Saving
                ? "History is off: nothing is remembered, and what an earlier version kept was cleared."
                : $"Kept now: {ActivityHistory.Total} entries (encrypted). " + (ActivityHistory.NextClearUtc is { } due
                    ? $"Next automatic clear: {due.ToLocalTime():dd MMM yyyy HH:mm}."
                    : _s.HistoryAutoClear == ActivityHistory.AutoClear.OnExit ? "Cleared when the app closes." : "Kept until you clear it.");
            clearAll.IsEnabled = ActivityHistory.Total > 0 || ActivityHistory.Saving;
        }

        var keep = Check("Keep history: quick connect and palette recents, Toolbox runs, finished transfers, when hosts and portals were last used, and pages visited in portal tabs (off: nothing is remembered, and turning it off clears what was kept)",
            _s.HistorySave, v => { ActivityHistory.SetSaving(v); Render(); });
        var auto = Choice(ActivityHistory.AutoClear.All.Select(v => (v, "Clear it automatically " + ActivityHistory.AutoClear.Text(v))), _s.HistoryAutoClear,
            v => { _s.HistoryAutoClear = v; _s.HistoryLastCleared = DateTime.UtcNow; Save(); AuditLog.Write("you", "history", "automatic clear changed", ActivityHistory.AutoClear.Text(v)); Render(); },
            "When kept history is cleared by itself. Every 1, 7 or 30 days counts from the last clear.");
        auto.Width = FieldWidth;
        var note = new TextBlock
        {
            Text = "Not history, so kept whatever this says: the audit log, saved hosts and portals, pinned lines, notes, reports, snippets and the dashboard's checks. " +
                   "The file commander's folder and command history live in memory only, while its tab is open; clearing history forgets them too. Clearing is written to the audit log (what kinds and how many, never the entries).",
            Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0),
        };
        var root = new StackPanel();
        root.Loaded += (_, _) => Render();
        root.Children.Add(keep);
        root.Children.Add(Labeled("AUTOMATIC CLEAR", auto));
        root.Children.Add(status);
        root.Children.Add(rows);
        root.Children.Add(Row(clearAll));
        root.Children.Add(note);
        Render();
        return Card(HistoryCardTitle,
            "What the app remembers about what you did, so it can offer it again. Off by default. Kept history is encrypted like everything stored and never leaves this PC.",
            root);
    }
}
