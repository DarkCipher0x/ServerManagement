using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using DcxManager.Services;

namespace DcxManager.Views;

// SettingsView: the Locks & restrictions card (0.5.1 #9–#11, ADR-067). Every category with its lock and a drop-down of the
// same lock commands the sidebar and lists use; the item padlocks set anywhere in the app; "Lock everything now"; and
// what Group Policy has turned off (read-only here: only policy changes those).
public sealed partial class SettingsView
{
    private Border LocksCard()
    {
        var state = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) };
        var cats = new StackPanel();
        var items = new StackPanel();
        var restricted = new TextBlock { TextWrapping = TextWrapping.Wrap, Style = (Style)FindResource("Caption") };
        var lockAll = Btn("🔒 Lock everything now", () => { Locks.CloseAll("you locked everything"); MainWindow.Instance?.Status("🔒 Everything locked is closed again."); }, "SmallButton");
        lockAll.ToolTip = "Closes every open lock now (they also close when the app locks, Windows locks, the PC sleeps or the open time runs out)";

        FrameworkElement LockRow(string target, string title, string what)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var pick = new Button { Content = "Lock ▾", Style = (Style)FindResource("SmallButton"), MinWidth = 0, Padding = new Thickness(8, 2, 8, 2), VerticalAlignment = VerticalAlignment.Center };
            Menus.AttachDropDown(pick, () => LockUi.Items(target, title));
            DockPanel.SetDock(pick, Dock.Right);
            row.Children.Add(pick);
            var own = Locks.RuleOn(target);
            var text = own == null ? "not locked"
                : own.Managed ? $"locked by your organisation · {Locks.ModeTitle(own.Mode)}"
                : $"locked · {Locks.ModeTitle(own.Mode)}";
            if (own != null) text += Locks.IsOpen(target) ? " · 🔓 open now" : " · 🔒 closed";
            var t = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, ToolTip = what };
            t.Inlines.Add(new System.Windows.Documents.Run((own != null ? LockUi.Padlock(target) + " " : "   ") + title) { FontWeight = FontWeights.SemiBold });
            t.Inlines.Add(new System.Windows.Documents.Run("  —  " + text));
            row.Children.Add(t);
            return row;
        }

        void Refresh()
        {
            var s = AppSettings.Current;
            int mine = s.UserLocks.Count, managed = Locks.Categories.Count(c => Locks.RuleOn("cat:" + c.Key) is { Managed: true });
            state.Text = (mine + managed == 0 ? "○ Nothing is locked." : $"🔒 {managed} categor{(managed == 1 ? "y" : "ies")} locked by your organisation, {mine} lock(s) of your own.")
                + $" An opened lock stays open for {(s.LockOpenMinutes > 0 ? s.LockOpenMinutes + " minute(s)" : "one use")} and closes when the app or Windows locks."
                + (Locks.WhyNotAllowed("cat:hosts") is { } no ? "\n" + no : "");
            state.SetResourceReference(TextBlock.ForegroundProperty, mine + managed == 0 ? "Muted" : "Info");
            cats.Children.Clear();
            foreach (var c in Locks.Categories) cats.Children.Add(LockRow("cat:" + c.Key, c.Title, c.Guards));
            items.Children.Clear();
            var own = s.UserLocks.Where(r => !r.Target.StartsWith("cat:", StringComparison.Ordinal)).OrderBy(r => r.Target).ThenBy(r => r.Title).ToList();
            if (own.Count == 0) items.Children.Add(new TextBlock { Text = "None. Right-click a host, folder, credential, provider, cloud account, portal or note → 🔒 Lock.", Style = (Style)FindResource("Caption") });
            foreach (var r in own) items.Children.Add(LockRow(r.Target, r.Title, KindOf(r.Target)));
            var off = Restrictions.All.Where(a => !a.Allowed(s)).Select(a => "✕ " + Restrictions.Why(a.Action)).ToList();
            restricted.Text = off.Count == 0
                ? "Nothing is turned off. An organisation can turn off adding, changing or deleting hosts, folders, credentials, cloud accounts, infrastructure providers, portals, tunnels, snippets, shared libraries, integrations and add-ons (Group Policy “What users may add and change”)."
                : string.Join("\n", off);
        }
        static string KindOf(string target) => target.Split(':')[0] switch
        {
            "host" => "a saved host", "folder" => "a host folder (every host in it)", "cred" => "a credential", "infra" => "an infrastructure provider",
            "cloud" => "a cloud account", "portal" => "a portal", "note" => "a note", _ => target,
        };

        // Administrator settings (protected: each change asks for admin approval; Group Policy can set and lock them).
        var mode = Bound(new ComboBox { ItemsSource = new[] { "Windows admin approval (UAC)", "Windows sign-in (Hello or password)" }, SelectedIndex = _s.LockPolicyMode == Locks.SignIn ? 1 : 0, MinWidth = 280, HorizontalAlignment = HorizontalAlignment.Left }, nameof(AppSettings.LockPolicyMode));
        var minutes = Bound(new ComboBox { IsEditable = true, ItemsSource = new[] { "0", "5", "15", "30", "60", "120", "480" }, Text = _s.LockOpenMinutes.ToString(), Width = 90, HorizontalAlignment = HorizontalAlignment.Left }, nameof(AppSettings.LockOpenMinutes));
        var applyLocks = AdminApproval.Button("Apply", async () =>
        {
            if (!int.TryParse(minutes.Text.Trim(), out var m) || m is < 0 or > 480) { Dialogs.Error("Locks", "The open time is 0–480 minutes (0 = ask every time)."); return; }
            if (await ApplyProtected("change how locks open", new Dictionary<string, JsonNode?>
            {
                ["locks.policyMode"] = JsonValue.Create(mode.SelectedIndex == 1 ? Locks.SignIn : Locks.Uac),
                ["locks.openMinutes"] = JsonValue.Create(m),
            })) Refresh();
            else { mode.SelectedIndex = _s.LockPolicyMode == Locks.SignIn ? 1 : 0; minutes.Text = _s.LockOpenMinutes.ToString(); }
        });

        Action onChange = () => Dispatcher.BeginInvoke(Refresh);
        state.Loaded += (_, _) => { Locks.Changed += onChange; Refresh(); };
        state.Unloaded += (_, _) => Locks.Changed -= onChange;
        Refresh();
        return Card("Locks & restrictions",
            "Lock a whole category (it opens with admin approval, Windows sign-in or a password of its own) or single items — hosts, folders, credentials, providers, cloud accounts, portals and notes. " +
            "A locked thing shows 🔒 and opens nothing until it is unlocked; the AI asks for nothing locked, and add-ons and the local API never get locked items, open or not. " +
            "The audit log is locked by default. Group Policy can lock categories, choose how they open, and turn off what users may add and change.",
            state, Row(lockAll),
            L("CATEGORIES"), cats,
            L("YOUR ITEM LOCKS"), items,
            L("ADMINISTRATOR SETTINGS (EACH CHANGE NEEDS ADMIN APPROVAL)"),
            Check("The Audit log needs an unlock to open", _s.LockAuditLog, v => _s.LockAuditLog = v),
            Check("Users may set their own locks", _s.AllowUserLocks, v => _s.AllowUserLocks = v),
            Check("Users may lock single items (hosts, folders, credentials…)", _s.AllowItemLocks, v => _s.AllowItemLocks = v),
            Check("Installing, updating and removing add-ons needs admin approval", _s.AddonsNeedApproval, v => _s.AddonsNeedApproval = v),
            Check("Add-ons may be installed", _s.AllowAddonInstall, v => _s.AllowAddonInstall = v),
            Check("Only add-ons whose program is signed (Authenticode)", _s.AddonRequireSigned, v => _s.AddonRequireSigned = v),
            Row(Labeled("LOCKS YOUR ORGANISATION SETS OPEN WITH", mode), Labeled("AN OPENED LOCK STAYS OPEN (MINUTES)", minutes), applyLocks),
            L("TURNED OFF BY YOUR ORGANISATION"), restricted);
    }
}
