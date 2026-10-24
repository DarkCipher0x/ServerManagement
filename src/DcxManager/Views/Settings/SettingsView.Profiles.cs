using System.Windows;
using System.Windows.Controls;
using DcxManager.Services;
using DcxManager.SignIn;

namespace DcxManager.Views;

// SettingsView: the Profiles card (0.3.0 §23) and the Team features card (0.3.0 §22).
public sealed partial class SettingsView
{
    private readonly TextBlock _profileStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _profileList = new();

    private Border ProfilesCard()
    {
        RefreshProfiles();
        return Card("Profiles",
            "Several profiles in one installation, each its own encrypted store (own data key, hosts, vault, settings, notes, team membership). A profile can sign in with your organisation — Windows / Active Directory, LDAP over TLS or Microsoft Entra ID — and then opens only after that sign-in; local profiles stay available unless Group Policy forbids them.",
            _profileStatus,
            Row(Btn("＋ New local profile…", NewLocalProfile, "PrimaryButton"), Btn("🏢 New profile with organisation sign-in…", NewDirectoryProfile), Btn("Forget cached sign-in proof", () => { DirectorySignIn.ForgetProof(); AuditLog.Write("you", "sign-in", "cached directory proof forgotten"); RefreshProfiles(); })),
            L("PROFILES ON THIS PC"), _profileList);
    }

    private void RefreshProfiles()
    {
        var cur = Profiles.Current;
        var r = DirectorySignIn.Current;
        _profileStatus.Text = cur == null ? "This run opened the default store." :
            $"Open now: {cur.Name} · {cur.KindText}" + (r != null ? $" · signed in as {r.DisplayName} ({r.Subject}) · role {Profiles.Role} · {r.Groups.Count} group(s)" : "")
            + (DirectorySignIn.ReadProof() is { } pr ? $" · cached proof from {pr.When:yyyy-MM-dd HH:mm}" : "")
            + (Profiles.DirectoryRequiredByPolicy() ? " · policy: directory sign-in required" : "");
        _profileList.Children.Clear();
        foreach (var p in Profiles.LoadWithDefault().Profiles)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var bar = new StackPanel { Orientation = Orientation.Horizontal };
            bool isCurrent = p.Id == (cur?.Id ?? Profiles.DefaultId);
            if (!isCurrent) bar.Children.Add(Small("Switch to…", () => { if (Dialogs.Confirm("Switch profile", $"Close this profile and open “{p.Name}”? Open sessions end.", "Switch")) StartupFlow.SwitchTo(p); }));
            bar.Children.Add(Small("Rename…", () => { if (Dialogs.Prompt("Rename profile", "Name:", p.Name) is { Length: > 0 } n) { Profiles.Rename(p.Id, n); if (isCurrent && cur != null) cur.Name = n.Trim(); RefreshProfiles(); } }));
            if (!isCurrent && p.Id != Profiles.DefaultId) bar.Children.Add(Small("Delete…", () =>
            {
                if (!Dialogs.Confirm("Delete profile", $"Delete “{p.Name}” and its encrypted store? This can't be undone.", "Delete", danger: true, typeToConfirm: p.Name)) return;
                Profiles.Delete(p.Id); AuditLog.Write("you", "profiles", "profile deleted", p.Name); RefreshProfiles();
            }));
            DockPanel.SetDock(bar, Dock.Right);
            row.Children.Add(bar);
            row.Children.Add(new TextBlock { Text = $"{(p.Kind == "directory" ? "🏢" : "👤")} {p.Name}{(isCurrent ? "  (open now)" : "")}  ·  {p.KindText}", VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            _profileList.Children.Add(row);
        }
        var auto = new CheckBox { Content = "Open the last used profile without asking", IsChecked = Profiles.LoadWithDefault().OpenLastWithoutAsking, Margin = new Thickness(0, 6, 0, 0) };
        auto.Checked += (_, _) => SetAuto(true); auto.Unchecked += (_, _) => SetAuto(false);
        _profileList.Children.Add(auto);
    }

    private static void SetAuto(bool on) { var ix = Profiles.LoadWithDefault(); ix.OpenLastWithoutAsking = on; try { Profiles.Save(ix); } catch (System.IO.IOException ex) { App.Log("profiles", ex); } }

    private Button Small(string t, Action a) { var b = new Button { Content = t, Style = (Style)FindResource("SmallButton"), Margin = new Thickness(4, 0, 0, 0) }; b.Click += (_, _) => { try { a(); } catch (Exception ex) { Dialogs.Error("Profiles", ex.Message, explain: false); } }; return b; }

    private void NewLocalProfile()
    {
        if (!Profiles.LocalProfilesAllowed()) { Dialogs.Info("Profiles", "Local profiles are not allowed on this PC (Group Policy)."); return; }
        if (Dialogs.Prompt("New profile", "Name for the new profile:") is not { Length: > 0 } name) return;
        var p = Profiles.Create(name.Trim());
        AuditLog.Write("you", "profiles", "local profile created", p.Name);
        RefreshProfiles();
        if (Dialogs.Confirm("New profile", $"“{p.Name}” was created (its store is empty until it is opened). Switch to it now?", "Switch")) StartupFlow.SwitchTo(p);
    }

    private void NewDirectoryProfile()
    {
        if (DirectoryProfileDialog.Ask(Window.GetWindow(this)) is not { } b) return;
        if (Dialogs.Prompt("New profile", "Name for the profile:", b.User.Length > 0 ? b.User : "Work") is not { Length: > 0 } name) return;
        var p = Profiles.Create(name.Trim(), b);
        AuditLog.Write("you", "profiles", "directory profile created", $"{p.Name} · {b.Describe()}");
        RefreshProfiles();
        if (Dialogs.Confirm("New profile", $"“{p.Name}” was created and signs in with {b.Describe()}. Switch to it now?", "Switch")) StartupFlow.SwitchTo(p);
    }

    // ---- team features (0.3.0 §22) ---------------------------------------------------------------------------------------

    private Border TeamCard() => Card("Team features",
        "Shared libraries are built in: the TEAM section and the Shared library screen are always there, with permissions per person and per group enforced by the encryption, and Windows Hello confirming reveals, exports and approvals when it is on. A team's library is joined from its folder with the owner's fingerprint (no add-on needed; add-ons from older versions still work).",
        ProtectedCheck("team.features", "Team features (Shared library screen and the TEAM section)", TeamFeaturesAsync, () => _s.TeamFeatures),
        Row(Btn("Open Shared library", () => MainWindow.Instance.Navigate("library"))));

    private async Task<bool> TeamFeaturesAsync(bool on)
    {
        if (!await ProtectedSettings.ChangeAsync(on ? "turn team features on" : "turn team features off", "team.features", on)) return false;
        Library.SharedLibraries.Load();
        MainWindow.Instance?.BuildNav();
        return true;
    }
}
