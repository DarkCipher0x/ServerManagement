using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DcxManager.Files;
using DcxManager.Services;

namespace DcxManager.Views;

/// <summary>
/// "Recover previous work" (ADR-022, spec §56): one screen after an unclean exit, or when the recovery journal holds
/// unsaved drafts / unfinished transfers. Every item has a box; nothing reconnects, nothing is written to a server and
/// no transfer starts unless it is ticked. "Later" keeps everything for the next start.
/// </summary>
public static class RecoveryWindow
{
    public sealed record Choice(bool Tabs, IReadOnlyList<string> DraftIds, IReadOnlyList<string> TransferIds);

    /// <summary>The screen's content and its boxes (the self-test renders and ticks it off-screen).</summary>
    public sealed class Built
    {
        public required FrameworkElement Content { get; init; }
        public CheckBox? TabsBox { get; init; }
        public List<(CheckBox Box, Recovery.Draft Draft)> Drafts { get; } = new();
        public List<(CheckBox Box, Recovery.Transfer Transfer)> Transfers { get; } = new();
        public required Button RecoverButton { get; init; }
        public required Button LaterButton { get; init; }
        public required Button DiscardButton { get; init; }
        public Choice Read() => new(TabsBox?.IsChecked == true,
            Drafts.Where(d => d.Box.IsChecked == true).Select(d => d.Draft.Id).ToList(),
            Transfers.Where(t => t.Box.IsChecked == true).Select(t => t.Transfer.Id).ToList());
    }

    public static Built Build(bool unclean, IReadOnlyList<Workspace.Entry> tabs, Recovery.Journal? journal)
    {
        var root = new StackPanel { Margin = new Thickness(18) };
        TextBlock Line(string text, bool muted = false, double top = 0)
        {
            var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 0) };
            if (muted) t.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            return t;
        }
        TextBlock Heading(string text) => new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 4) }.Fs(FontStep.Small);
        CheckBox Box(string text, string? detail, bool enabled = true)
        {
            // A CheckBox measures its content at unlimited width: cap it so long paths wrap instead of running off the edge.
            var sp = new StackPanel { MaxWidth = 548 };
            sp.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
            if (detail != null)
            {
                var dt = new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap }.Fs(FontStep.Small);
                dt.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                sp.Children.Add(dt);
            }
            return new CheckBox { Content = sp, IsChecked = enabled, IsEnabled = enabled, Margin = new Thickness(0, 3, 0, 3) };
        }

        root.Children.Add(new TextBlock
        {
            Text = unclean ? "The app didn't close cleanly last time." : "Some work from the last session wasn't finished.",
            FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
        }.Fs(FontStep.Heading));
        root.Children.Add(Line("Tick what to bring back. Nothing connects, nothing is written to a server and no transfer starts unless you tick it.", muted: true, top: 4));

        CheckBox? tabsBox = null;
        if (tabs.Count > 0)
        {
            root.Children.Add(Heading("OPEN TABS"));
            var names = tabs.Select(Describe).ToList();
            tabsBox = Box($"Reopen {tabs.Count} tab(s) in their panes, pop-out windows and groups",
                string.Join(" · ", names.Take(8)) + (names.Count > 8 ? $" · +{names.Count - 8} more" : "") + "\nThey reopen disconnected: press Enter in a terminal (or ↻) to connect.");
            root.Children.Add(tabsBox);
        }

        var drafts = new List<(CheckBox, Recovery.Draft)>();
        var transfers = new List<(CheckBox, Recovery.Transfer)>();
        if (journal is { Drafts.Count: > 0 })
        {
            root.Children.Add(Heading("UNSAVED EDITS"));
            foreach (var d in journal.Drafts)
            {
                var src = FsRef.Parse(d.Source);
                bool gone = src.Kind != "local" && src.Host == null;
                int lines = d.Text.Count(c => c == '\n') + 1;
                var b = Box($"{src.Title}:{d.Path}",
                    $"{lines} line(s) · last change {d.Updated:yyyy-MM-dd HH:mm:ss} · opens in the editor; nothing is written until you press Save" +
                    (gone ? "\n⚠ That host no longer exists: the text can still be opened and copied, but not saved back." : ""));
                drafts.Add((b, d));
                root.Children.Add(b);
            }
        }
        if (journal is { Transfers.Count: > 0 })
        {
            root.Children.Add(Heading("UNFINISHED TRANSFERS"));
            foreach (var t in journal.Transfers)
            {
                var from = FsRef.Parse(t.From); var to = FsRef.Parse(t.To);
                bool gone = from.Kind != "local" && from.Host == null || to.Kind != "local" && to.Host == null;
                var b = Box($"{t.Title} · {from.Title} → {to.Title}:{t.DestDir}",
                    gone ? "⚠ A host of this transfer no longer exists, so it can't be resumed."
                         : $"was {t.Status} (started {t.Started:yyyy-MM-dd HH:mm}) · queued again, unfinished files resume from their “.dcxpart” part"
                           + (t.DeleteCount > 0 ? $"\nThe sync's {t.DeleteCount} deletion(s) on the target are NOT repeated — run the sync again to mirror." : ""),
                    enabled: !gone);
                transfers.Add((b, t));
                root.Children.Add(b);
            }
        }

        var recover = Dialogs.Btn("Recover selected", primary: true);
        var later = Dialogs.Btn("Later");
        later.ToolTip = "Keep everything and ask again at the next start";
        var discard = Dialogs.Btn("Discard all", danger: true);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        discard.Margin = new Thickness(0, 0, 6, 0); later.Margin = new Thickness(0, 0, 6, 0);
        buttons.Children.Add(discard); buttons.Children.Add(later); buttons.Children.Add(recover);
        root.Children.Add(buttons);

        var built = new Built { Content = root, TabsBox = tabsBox, RecoverButton = recover, LaterButton = later, DiscardButton = discard };
        built.Drafts.AddRange(drafts); built.Transfers.AddRange(transfers);
        return built;
    }

    private static string Describe(Workspace.Entry e) => e.Kind switch
    {
        "local" => "❯ " + (Sessions.LocalShells.Find(e.HostId)?.Name ?? "local shell"),
        "serial" => "⏚ " + (AppData.Current.SerialProfiles.FirstOrDefault(s => s.Id == e.HostId)?.Name ?? "serial"),
        _ => (e.Kind switch { "files" => "⇅ ", "server" => "▣ ", "rdp" => "🖥 ", "vnc" => "🖵 ", _ => "⌨ " }) + (AppData.Current.FindHost(e.HostId)?.Display ?? "?"),
    };

    /// <summary>Shows the screen (modal). Closing it is "Later".</summary>
    public static void Show(MainWindow main, bool unclean, IReadOnlyList<Workspace.Entry> tabs, Recovery.Journal? journal)
    {
        if (SelfTest.Running) return;
        var built = Build(unclean, tabs, journal);
        var w = Dialogs.MakeWindow("Recover previous work", 620);
        w.Content = built.Content;
        built.RecoverButton.IsDefault = true;
        built.RecoverButton.Click += (_, _) => { w.Close(); main.Status(Apply(main, tabs, journal, built.Read())); };
        built.LaterButton.Click += (_, _) => { w.Close(); main.Status("Recovery kept for later: you'll be asked again at the next start."); };
        built.DiscardButton.Click += (_, _) =>
        {
            if (journal is { Drafts.Count: > 0 } && !Dialogs.Confirm("Discard", $"Discard {journal.Drafts.Count} unsaved edit(s)? They can't be brought back.", "Discard", danger: true)) return;
            w.Close();
            main.Status(Apply(main, tabs, journal, new Choice(false, Array.Empty<string>(), Array.Empty<string>())));
        };
        w.ShowDialog();
    }

    /// <summary>Brings back the ticked items; everything else in the journal is discarded. Returns a status line.</summary>
    public static string Apply(MainWindow main, IReadOnlyList<Workspace.Entry> tabs, Recovery.Journal? journal, Choice c)
    {
        var parts = new List<string>();
        var ids = journal == null ? new List<string>() : journal.Drafts.Select(d => d.Id).Concat(journal.Transfers.Select(t => t.Id)).ToList();
        Recovery.Settle(ids);   // first: recovered items become live (open editors, queued jobs) and are journaled from there
        if (c.Tabs && tabs.Count > 0) { main.RestoreTabs(tabs); parts.Add($"{tabs.Count} tab(s) reopened (disconnected)"); }
        int nd = 0, nt = 0;
        foreach (var d in journal?.Drafts.Where(d => c.DraftIds.Contains(d.Id)) ?? Enumerable.Empty<Recovery.Draft>())
        {
            try { TextEditorWindow.OpenRecovered(d); nd++; } catch (Exception ex) { App.Log("Recover draft", ex); }
        }
        foreach (var t in journal?.Transfers.Where(t => c.TransferIds.Contains(t.Id)) ?? Enumerable.Empty<Recovery.Transfer>())
        {
            try { TransferQueue.Enqueue(Recovery.ToJob(t)); nt++; } catch (Exception ex) { App.Log("Recover transfer", ex); }
        }
        if (nd > 0) parts.Add($"{nd} unsaved edit(s) reopened — press Save to keep them");
        if (nt > 0) parts.Add($"{nt} transfer(s) queued again");
        int dropped = ids.Count - c.DraftIds.Count - c.TransferIds.Count;
        if (dropped > 0) parts.Add($"{dropped} item(s) discarded");
        AuditLog.Write("you", "this PC", "crash recovery", parts.Count == 0 ? "nothing recovered" : string.Join("; ", parts));
        return parts.Count == 0 ? "Nothing recovered." : "Recovered: " + string.Join(" · ", parts) + ".";
    }
}
