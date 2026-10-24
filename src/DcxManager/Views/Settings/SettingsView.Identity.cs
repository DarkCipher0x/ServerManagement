using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using DcxManager.Services;
using DcxManager.SignIn;

namespace DcxManager.Views;

// SettingsView: the Identity & directory tab (0.5.0, ADR-062). DarkCipher0x: "its own tab for authentication service integrations
// — Azure, Entra, LDAPS — tools to integrate, view or edit the configuration; user aware: logged in as my domain account it
// signs me in with Windows Hello; rely on Kerberos and pass single sign-on through to terminals / RDP where possible."
public sealed partial class SettingsView
{
    /// <summary>Who is signed in to Windows, the PC's join state and Kerberos tickets (read-only; refreshed on demand).</summary>
    private Border IdentityAccountCard()
    {
        var who = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4) };
        var join = new TextBlock { TextWrapping = TextWrapping.Wrap, Style = (Style)FindResource("Caption") };
        var tickets = new TextBlock { TextWrapping = TextWrapping.Wrap, Style = (Style)FindResource("Caption"), Margin = new Thickness(0, 2, 0, 4) };
        var match = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) };
        async Task Refresh()
        {
            var a = WindowsAccount.Current();
            who.Text = $"{a.Name}" + (a.Upn != null ? $"  ·  {a.Upn}" : "") + (a.DomainAccount ? "  ·  domain account" : "  ·  local or Microsoft account") + $"\nSID {a.Sid}";
            var ix = Profiles.LoadWithDefault();
            var bound = ix.Profiles.Where(p => p.Directory is { } b && WindowsAccount.Matches(b, a)).ToList();
            match.Text = bound.Count switch
            {
                0 => a.DomainAccount ? "No profile is bound to this account yet: “Bind a profile to this account” makes one that opens with your Windows sign-in." : "Local and Microsoft accounts sign in with the master password or Windows Hello.",
                1 => $"✓ The profile “{bound[0].Name}” is bound to this account" + (Profiles.ForWindowsAccount(ix, a) != null ? " and opens automatically." : "."),
                _ => $"⚠ {bound.Count} profiles are bound to this account; the picker asks which one.",
            };
            join.Text = "Checking how this PC is joined…"; tickets.Text = "";
            if (SelfTest.Running) { join.Text = "(not read in the self-test)"; return; }
            var j = await WindowsAccount.JoinAsync();
            join.Text = (j.AdJoined && j.EntraJoined ? "Hybrid joined" : j.AdJoined ? "Active Directory joined" : j.EntraJoined ? "Microsoft Entra joined" : j.WorkplaceJoined ? "Workplace registered" : "Not joined to an organisation")
                + (j.DomainName.Length > 0 ? $" · domain {j.DomainName}" : "") + (j.Tenant.Length > 0 ? $" · tenant {j.Tenant}" : "") + (j.PrimaryRefreshToken ? " · Entra single sign-on token present" : "");
            var t = await WindowsAccount.TicketsAsync();
            tickets.Text = t.HasTgt ? $"Kerberos: signed in to {t.Realm}, {t.Count} ticket(s), renews by {t.TgtExpires:HH:mm}." : t.Count > 0 ? $"Kerberos: {t.Count} ticket(s), no ticket-granting ticket." : "Kerberos: no tickets (not a domain sign-in, or the domain can't be reached).";
        }
        _ = Refresh();
        return Card("This Windows account",
            "Who is signed in to Windows on this PC and what single sign-on can use. Read-only: nothing here is stored or sent.",
            who, join, tickets, match,
            Row(Btn("↻ Refresh", () => _ = Refresh()), Btn("Bind a profile to this account…", BindProfileToAccount, "PrimaryButton")));
    }

    private void BindProfileToAccount()
    {
        var a = WindowsAccount.Current();
        if (!a.DomainAccount) { Dialogs.Info("Bind a profile", "This is a local or Microsoft account: there is no domain account to bind to. Use “New profile with organisation sign-in” for LDAPS or Microsoft Entra ID."); return; }
        var seed = new DirectoryBinding { Kind = "windows", Subject = a.Sid, User = a.Upn ?? a.Name };
        if (DirectoryProfileDialog.Ask(Window.GetWindow(this), seed) is not { } b) return;
        if (Dialogs.Prompt("Bind a profile", "Name for the new profile:", a.Name.Split('\\').Last()) is not { Length: > 0 } name) return;
        try
        {
            var p = Profiles.Create(name, b);
            AuditLog.Write("you", "profiles", "directory profile created for this Windows account", b.Describe());
            Dialogs.Info("Bind a profile", $"“{p.Name}” is bound to {a.Name}. Switch to it from Profiles; with automatic sign-in on, it opens by itself next time.");
            RefreshProfiles();
        }
        catch (Exception ex) { Dialogs.Error("Bind a profile", ex.Message); }
    }

    /// <summary>Every profile that signs in with the organisation: what it binds to, whether the binding is sound, and the
    /// tools to view, edit and test it.</summary>
    private Border DirectoryConnectionsCard()
    {
        var list = new StackPanel();
        var result = new TextBlock { TextWrapping = TextWrapping.Wrap, Style = (Style)FindResource("Caption"), Margin = new Thickness(0, 6, 0, 0) };
        void Fill()
        {
            list.Children.Clear();
            var dirs = Profiles.LoadWithDefault().Profiles.Where(p => p.Kind == "directory" && p.Directory != null).ToList();
            if (dirs.Count == 0) list.Children.Add(new TextBlock { Text = "No profile signs in with the organisation yet. ＋ New profile with organisation sign-in (Profiles below), or bind one to your Windows account above.", TextWrapping = TextWrapping.Wrap, Style = (Style)FindResource("Hint") });
            foreach (var p in dirs)
            {
                var b = p.Directory!;
                var problem = b.Problem();
                var head = new TextBlock { Text = $"{(problem == null ? "●" : "⚠")}  {p.Name}  —  {b.Describe()}", TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
                var detail = new TextBlock { TextWrapping = TextWrapping.Wrap, Style = (Style)FindResource("Caption"),
                    Text = (b.User.Length > 0 ? $"user {b.User} · " : "") + (b.RequiredGroup.Length > 0 ? $"requires group {b.RequiredGroup} · " : "") +
                           (b.GroupRoles.Count > 0 ? "roles: " + string.Join(", ", b.GroupRoles.Select(kv => $"{kv.Key} → {kv.Value}")) + " · " : "no group roles (operator) · ") +
                           $"offline grace {b.OfflineGraceDays} day(s)" + (problem != null ? $"\n⚠ {problem}" : "") };
                var bar = new WrapPanel { Margin = new Thickness(0, 2, 0, 8) };
                bar.Children.Add(Btn("View configuration", () => Dialogs.Info($"{p.Name}: sign-in configuration", JsonSerializer.Serialize(b, new JsonSerializerOptions { WriteIndented = true }) + "\n\nNo secret is stored in a binding: passwords are asked at sign-in, tokens are never kept."), "SmallButton"));
                bar.Children.Add(Btn("Edit…", () =>
                {
                    if (!SensitiveAction.Confirm($"change how “{p.Name}” signs in", Window.GetWindow(this))) return;
                    if (DirectoryProfileDialog.Ask(Window.GetWindow(this), b) is not { } nb) return;
                    try { Profiles.UpdateBinding(p.Id, nb); AuditLog.Write("you", "profiles", "directory sign-in changed", $"{p.Name}: {nb.Describe()}"); Fill(); }
                    catch (Exception ex) { Dialogs.Error("Directory sign-in", ex.Message); }
                }, "SmallButton"));
                bar.Children.Add(Btn("Test sign-in", async () =>
                {
                    result.Text = $"Signing in to {b.Describe()}…";
                    var why = await DirectorySignIn.OpenAsync(b, q => Dialogs.Prompt("Test sign-in", q, password: true));
                    result.Text = why == null ? $"✓ {p.Name}: signed in as {DirectorySignIn.Current?.DisplayName} · role {Profiles.Role} · {DirectorySignIn.Current?.Groups.Count ?? 0} group(s)." : $"✕ {p.Name}: {why}";
                    AuditLog.Write("you", "sign-in", why == null ? "directory sign-in test passed" : "directory sign-in test failed", p.Name);
                }, "SmallButton"));
                var row = new StackPanel();
                row.Children.Add(head); row.Children.Add(detail); row.Children.Add(bar);
                list.Children.Add(row);
            }
        }
        Fill();
        return Card("Directory connections",
            "Active Directory (the Windows session, Kerberos), LDAP over TLS and Microsoft Entra ID: what each profile signs in with, the groups that give it a role, and a test that signs in for real. Azure and Google Cloud API access is under Cloud providers.",
            list, result,
            Row(Btn("🏢 New profile with organisation sign-in…", NewDirectoryProfile), Btn("↻ Refresh", Fill)));
    }

    /// <summary>Opening the right profile by itself, and Windows Hello for the store.</summary>
    private Border AutoSignInCard()
    {
        var ix = Profiles.LoadWithDefault();
        var policy = PolicySettings.MachineFlag("AutoProfileForWindowsAccount");
        var auto = Check("Open the profile bound to my Windows account automatically (no profile picker)", policy ?? ix.AutoOpenForWindowsAccount, v =>
        {
            try { Profiles.SetAutoOpen(v); AuditLog.Write("you", "profiles", v ? "automatic profile for the Windows account on" : "automatic profile for the Windows account off"); }
            catch (Exception ex) { Dialogs.Error("Automatic sign-in", ex.Message); }
        });
        if (policy != null) { auto.IsEnabled = false; auto.ToolTip = $"🔒 Set by your organisation (computer policy AutoProfileForWindowsAccount = {(policy == true ? "on" : "off")})."; }
        return Card("Automatic sign-in",
            "Signed in to Windows as the account a profile is bound to: that profile opens without the picker, signs in with your Windows session (Kerberos, nothing typed) and Windows Hello opens its encrypted store. The master password stays as the fallback.",
            auto,
            ProtectedCheck("signin.hello", "Sign in with Windows Hello (face, fingerprint or PIN) instead of typing the master password"));
    }

    /// <summary>Kerberos into SSH, the Windows account into Remote Desktop.</summary>
    private Border SsoCard()
    {
        var hosts = AppData.Current.Hosts;
        var count = new TextBlock { Style = (Style)FindResource("Caption"), TextWrapping = TextWrapping.Wrap,
            Text = $"{hosts.Count(h => h.KerberosSignOn)} SSH host(s) use Kerberos sign-on, {hosts.Count(h => h.Protocol == "rdp" && h.Rdp.SignOn != "password")} Remote Desktop host(s) use the Windows account. Set it per host in the host editor." };
        var help = new TextBlock { TextWrapping = TextWrapping.Wrap, Style = (Style)FindResource("Hint"), Margin = new Thickness(0, 6, 0, 0),
            Text = "SSH: the server must accept GSSAPI (a domain-joined Linux server — sssd / realmd with a host keytab, GSSAPIAuthentication yes in sshd_config); the tab runs Windows' OpenSSH client. " +
                   "Remote Desktop: single sign-on needs the “Allow delegating default credentials” policy for TERMSRV/<server> (otherwise Windows asks once); Remote Credential Guard needs Windows 10 1607 / Server 2016 or later on both ends and the server's DisableRestrictedAdmin set to 0; Restricted Admin needs an account that is an administrator on the server." };
        return Card("Single sign-on to sessions",
            "Use the Windows account you are signed in with — no password typed, stored or sent — for SSH (Kerberos) and Remote Desktop (CredSSP, Remote Credential Guard, Restricted Admin).",
            ProtectedCheck("sso.kerberosSsh", "SSH hosts may sign in with my Kerberos ticket (Windows OpenSSH, GSSAPI)"),
            ProtectedCheck("sso.kerberosDelegate", "Forward my Kerberos ticket to SSH servers (lets them reach other services as me; off unless you need it)"),
            ProtectedCheck("sso.rdp", "Remote Desktop hosts may sign in with my Windows account"),
            count, help);
    }
}
