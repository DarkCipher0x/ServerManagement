using System.Windows;
using System.Windows.Controls;
using DcxManager.Cloud;
using DcxManager.Services;

namespace DcxManager.Views;

/// <summary>
/// Edit dashboard (0.5.1 #12): every card you can have, grouped — the built-in ones (shown or hidden), cards for a host, a
/// folder, an infrastructure or cloud provider, the integrations (Grafana, Sentinel, Pingdom, BeyondTrust, Puppet, Entra
/// ID) and the pinned checks (ping, TCP port, HTTP(S), certificate, DNS, SPF / DMARC) — plus the cards you added, and
/// Reset to default. Group Policy can lock the dashboard (DashboardLocked) or set its cards (DashboardCards).
/// </summary>
public static class DashboardEditor
{
    public static void Show(Window? owner, DashboardView view)
    {
        var s = AppSettings.Current;
        var w = Dialogs.MakeWindow("Edit dashboard", 760, 640);
        w.Owner = owner;
        Style St(string key) => (Style)Application.Current.Resources[key];
        TextBlock L(string t) => new() { Text = t, Style = St("Label"), Margin = new Thickness(0, 14, 0, 6) };
        var page = new StackPanel { Margin = new Thickness(18) };
        page.Children.Add(new TextBlock { Text = "Choose what the dashboard shows. Cards read what the app already knows; nothing is contacted until a check runs (the dashboard's Checks ▾ rule, or a card's own button). Locked hosts and providers show their name and padlock only.", TextWrapping = TextWrapping.Wrap, Style = St("Hint") });

        page.Children.Add(L("BUILT-IN CARDS"));
        var builtIn = new WrapPanel();
        foreach (var c in Overview.Cards)
        {
            var box = new CheckBox { Content = c.Title, ToolTip = c.Explain, IsChecked = !s.DashboardHidden.Contains(c.Id), Margin = new Thickness(0, 0, 18, 6), Width = 210 };
            box.Click += (_, _) => { s.DashboardHidden.Remove(c.Id); if (box.IsChecked != true) s.DashboardHidden.Add(c.Id); view.AfterEdit(); };
            builtIn.Children.Add(box);
        }
        page.Children.Add(builtIn);

        var added = new StackPanel();
        void FillAdded()
        {
            added.Children.Clear();
            var cards = DashboardCatalog.Cards(s);
            if (cards.Count == 0) added.Children.Add(new TextBlock { Text = "None yet — add one below.", Style = St("Hint") });
            foreach (var c in cards)
            {
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                var remove = new Button { Content = "✕ Remove", Style = St("SmallButton") };
                remove.Click += (_, _) => { DashboardCatalog.Remove(s, c.Id); view.AfterEdit(); FillAdded(); };
                DockPanel.SetDock(remove, Dock.Right);
                row.Children.Add(remove);
                row.Children.Add(new TextBlock { Text = $"{c.Title}  ·  {DashboardCatalog.Find(c.Type)?.Title}{(c.Wide ? " · full width" : "")}", VerticalAlignment = VerticalAlignment.Center });
                added.Children.Add(row);
            }
        }

        foreach (var group in DashboardCatalog.Types.GroupBy(t => t.Group))
        {
            page.Children.Add(L(group.Key.ToUpperInvariant()));
            foreach (var t in group)
            {
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 6) };
                var add = new Button { Content = "＋ Add", Style = St("SmallButton"), VerticalAlignment = VerticalAlignment.Top };
                DockPanel.SetDock(add, Dock.Right);
                row.Children.Add(add);
                ComboBox? pick = null;
                if (t.Pick != null)
                {
                    var choices = Choices(t.Pick);
                    pick = new ComboBox { Width = 230, Margin = new Thickness(0, 0, 8, 0), DisplayMemberPath = nameof(Choice.Title), ItemsSource = choices, SelectedIndex = choices.Count > 0 ? 0 : -1, VerticalAlignment = VerticalAlignment.Top };
                    DockPanel.SetDock(pick, Dock.Right);
                    row.Children.Add(pick);
                    add.IsEnabled = choices.Count > 0;
                }
                var text = new StackPanel();
                text.Children.Add(new TextBlock { Text = t.Title, FontWeight = FontWeights.SemiBold });
                text.Children.Add(new TextBlock { Text = t.Explain, TextWrapping = TextWrapping.Wrap, Style = St("Caption") });
                row.Children.Add(text);
                add.Click += (_, _) =>
                {
                    var target = (pick?.SelectedItem as Choice)?.Id ?? "";
                    if (t.Pick != null && target.Length == 0) return;
                    DashboardCatalog.Add(s, t.Type, target);
                    view.AfterEdit(); FillAdded();
                };
                page.Children.Add(row);
            }
        }

        page.Children.Add(L("CHECKS"));
        var pin = new Button { Content = "📌 Pin a check — ping, TCP port, HTTP(S), certificate expiry, DNS record, SPF / DMARC…", Style = St("SmallButton"), HorizontalAlignment = HorizontalAlignment.Left };
        pin.Click += (_, _) => { if (PinCheckDialog.Show(w) != null) { s.DashboardHidden.Remove("checks"); view.AfterEdit(); } };
        page.Children.Add(pin);
        page.Children.Add(new TextBlock { Text = "Pinned checks run on their own interval and keep their history; they show in the Pinned checks card.", Style = St("Caption"), Margin = new Thickness(0, 4, 0, 0) });

        page.Children.Add(L("CARDS YOU ADDED"));
        FillAdded();
        page.Children.Add(added);

        var bottom = new DockPanel { Margin = new Thickness(18, 6, 18, 14) };
        var close = new Button { Content = "Close", Style = St("PrimaryButton"), MinWidth = 90 };
        close.Click += (_, _) => w.Close();
        DockPanel.SetDock(close, Dock.Right);
        bottom.Children.Add(close);
        var reset = new Button { Content = "Reset to default…", Style = St("DangerButton"), HorizontalAlignment = HorizontalAlignment.Left };
        reset.Click += (_, _) => { view.ResetToDefault(); w.Close(); };
        bottom.Children.Add(reset);

        var root = new DockPanel();
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        root.Children.Add(new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        w.Content = root;
        w.ShowDialog();
    }

    public sealed record Choice(string Id, string Title);

    /// <summary>What a card can show: saved hosts, host folders, infrastructure providers, cloud providers with an account.</summary>
    internal static List<Choice> Choices(string pick) => pick switch
    {
        "host" => AppData.Current.Hosts.Where(h => !h.IsAdHoc).OrderBy(h => h.Display).Select(h => new Choice(h.Id, Locks.Hidden("host:" + h.Id) ? "🔒 " + h.Display : h.Display)).ToList(),
        "folder" => AppData.Current.AllFolders.Where(f => f.Length > 0).OrderBy(f => f).Select(f => new Choice(f, f)).ToList(),
        "infra" => AppData.Current.InfraProviders.Select(p => new Choice(p.Id, $"{p.Name} ({Infra.Infra.KindTitle(p.Kind)})")).ToList(),
        "cloud" => CloudProviders.All.Where(p => p.ActiveAccount != null).Select(p => new Choice(p.Id, p.Title)).ToList(),
        _ => new(),
    };
}
