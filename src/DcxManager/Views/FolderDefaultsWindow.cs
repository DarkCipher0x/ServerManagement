using System.Windows;
using System.Windows.Controls;
using DcxManager.Services;

namespace DcxManager.Views;

/// <summary>
/// A host folder's defaults (0.6.29, ADR-105): Hosts → right-click a folder → ⚙ Folder defaults…. Each field is "not set
/// here" (the parent folder's applies) or a value for the hosts in this folder and its sub-folders. Saving asks whether
/// the hosts already in it should follow now (their own values for those fields are replaced) or only new hosts and the
/// ones that already follow.
/// </summary>
public static class FolderDefaultsWindow
{
    private sealed record Choice(string Display, string? Value);

    /// <summary>How a field's value reads ("bastion-01", "direct", "PRODUCTION"…).</summary>
    public static string Show(string field, string value) => field switch
    {
        "credential" => value.Length == 0 ? "none" : AppData.Current.FindCredential(value)?.Name ?? "(missing credential)",
        "jump" => value.Length == 0 ? "direct" : AppData.Current.FindHost(value)?.Display ?? "(missing host)",
        "environment" => value.Length == 0 ? "no label" : value,
        "profile" => value.Length == 0 ? "the default profile" : Sessions.TerminalProfiles.All.FirstOrDefault(p => p.Id == value)?.Name ?? "(missing profile)",
        "vpn" => value.Length == 0 ? "none" : AppData.Current.VpnProfiles.FirstOrDefault(v => v.Id == value)?.Name ?? "(missing VPN)",
        "tmux" => value switch { "on" => "always", "off" => "never", _ => "as Settings say" },
        _ => value,
    };

    public static void Edit(string folder)
    {
        folder = AppData.NormalizeFolder(folder);
        if (folder.Length == 0 || !Restrictions.Check("folder.edit")) return;
        if (!Locks.IsOpen("folder:" + folder)) { _ = Application.Current.Dispatcher.InvokeAsync(async () => { if (await LockGate.EnsureAsync("folder:" + folder, "change the folder's defaults")) Edit(folder); }); return; }
        if (TestMode.Running) { Dialogs.RefusedInSelfTest("folder defaults: " + folder); return; }
        var cur = FolderDefaults.For(folder);
        var parent = folder.Contains('/') ? folder[..folder.LastIndexOf('/')] : "";

        var w = Dialogs.MakeWindow("Folder defaults", 600);
        var root = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
        root.Children.Add(new TextBlock { Text = "Folder defaults: " + folder, Style = (Style)Application.Current.Resources["H1"], Margin = new Thickness(0, 0, 0, 4) });
        root.Children.Add(new TextBlock
        {
            Text = "For the hosts in this folder and its sub-folders (a sub-folder can set its own). A host follows a field while it shows the folder's value; giving it another value in the host editor makes it the host's own. Secrets stay in the vault: a folder only points at a credential.",
            Style = (Style)Application.Current.Resources["Hint"], TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
        });

        string NotSet(string field) => FolderDefaults.Effective(parent, field) is { } p ? $"(not set here: {Show(field, p.Value)} from {p.Folder})" : "(not set here)";
        var boxes = new Dictionary<string, ComboBox>();
        void Field(string field, string label, IEnumerable<Choice> choices, bool editable = false)
        {
            var list = new List<Choice> { new(NotSet(field), null) };
            list.AddRange(choices);
            var current = cur == null ? null : FolderDefaults.ValueOf(cur, field);
            if (current != null && list.All(c => c.Value != current)) list.Add(new Choice(Show(field, current), current));
            var box = new ComboBox { ItemsSource = list, DisplayMemberPath = nameof(Choice.Display), Margin = new Thickness(0, 0, 0, 10), IsEditable = editable };
            box.SelectedItem = list.FirstOrDefault(c => c.Value == current) ?? list[0];
            boxes[field] = box;
            root.Children.Add(new TextBlock { Text = label, Style = (Style)Application.Current.Resources["Label"] });
            root.Children.Add(box);
        }

        var user = new TextBox { Text = cur?.Username ?? "", Margin = new Thickness(0, 0, 0, 10), Padding = new Thickness(5, 4, 5, 4), ToolTip = "Empty = not set here" }.Mono();
        Hint.Set(user, FolderDefaults.Effective(parent, "user") is { } pu ? $"not set here: {pu.Value} from {pu.Folder}" : "not set here");
        root.Children.Add(new TextBlock { Text = "USER NAME", Style = (Style)Application.Current.Resources["Label"] });
        root.Children.Add(user);
        Field("credential", "CREDENTIAL FROM THE VAULT", AppData.Current.Credentials.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Select(c => new Choice(c.Display, c.Id)));
        Field("jump", "CONNECT THROUGH (jump host, SSH hosts)", new[] { new Choice("(direct: no jump host)", "") }
            .Concat(AppData.Current.Hosts.Where(h => h.IsSsh && !h.IsAdHoc).OrderBy(h => h.Display, StringComparer.OrdinalIgnoreCase).Select(h => new Choice(h.Display, h.Id))));
        Field("environment", "ENVIRONMENT", new[] { new Choice("(no label)", "") }.Concat(Envs.All.Select(e => new Choice(e, e))));
        Field("profile", "TERMINAL PROFILE", new[] { new Choice($"(the default: {Sessions.TerminalProfiles.Default.Name})", "") }
            .Concat(Sessions.TerminalProfiles.All.Select(p => new Choice(p.Name, p.Id))));
        Field("vpn", "VPN (brought up before connecting)", new[] { new Choice("(none)", "") }
            .Concat(AppData.Current.VpnProfiles.Select(v => new Choice($"{v.Name} ({v.KindTitle})", v.Id))));
        Field("tmux", "TMUX (SSH terminals start inside your tmux session)", new[] { new Choice("(as Settings → Terminal → tmux say)", ""), new Choice("Always", "on"), new Choice("Never", "off") });

        var save = new Button { Content = "Save", MinWidth = 90, IsDefault = true, Style = (Style)Application.Current.Resources["PrimaryButton"] };
        var cancel = new Button { Content = "Cancel", MinWidth = 90, IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(save); buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        w.Content = root;

        save.Click += (_, _) =>
        {
            string? V(string f) => (boxes[f].SelectedItem as Choice)?.Value;
            var s = new FolderSettings
            {
                Path = folder, Username = user.Text.Trim().Length > 0 ? user.Text.Trim() : null, CredentialId = V("credential"), JumpHostId = V("jump"),
                Environment = V("environment"), TerminalProfileId = V("profile"), VpnProfileId = V("vpn"), Tmux = V("tmux"),
            };
            var setNow = FolderDefaults.Fields.Where(f => FolderDefaults.ValueOf(s, f) != null).ToList();
            var changedFields = setNow.Where(f => cur == null || FolderDefaults.ValueOf(cur, f) != FolderDefaults.ValueOf(s, f)).ToList();
            var hosts = FolderDefaults.HostsUnder(folder);
            bool adopt = false;
            if (changedFields.Count > 0 && hosts.Any(h => changedFields.Any(f => !h.Inherits.Contains(f))))
            {
                var what = string.Join(", ", changedFields.Select(FolderDefaults.Title));
                var pick = Dialogs.Choose("Folder defaults", $"{hosts.Count} host(s) are in “{folder}” and its sub-folders. Should they follow the new defaults ({what}) now?\n\n" +
                    "Apply to all: their own values for these fields are replaced, and they follow the folder from now on.\n" +
                    "Only new hosts: hosts already following keep following; the others keep their own values.",
                    new[] { "Cancel", $"Apply to all {hosts.Count} host(s)", "Only new hosts and those already following" }, cancelIndex: 0, width: 620);
                if (pick <= 0) return;
                adopt = pick == 1;
            }
            FolderDefaults.Set(s);
            int changed = adopt ? FolderDefaults.Adopt(hosts, changedFields) : 0;
            try { AppData.Save(); }
            catch (Exception ex) { Dialogs.Error("Folder defaults", ex.Message, explain: false); return; }
            AuditLog.Write("you", folder, "folder defaults saved", (setNow.Count == 0 ? "none set" : string.Join(", ", setNow.Select(FolderDefaults.Title))) + (adopt ? $" · applied to {changed} host(s)" : ""));
            MainWindow.Instance?.Status(adopt ? $"Folder defaults saved for “{folder}” and applied to {changed} host(s)." : $"Folder defaults saved for “{folder}”.");
            w.DialogResult = true;
        };
        w.ShowDialog();
    }
}
