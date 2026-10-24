using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DcxManager.Services;
using DcxManager.SignIn;

namespace DcxManager.Views;

/// <summary>
/// "Sign in with your organisation" (0.3.0 §23): how a profile is bound to a directory — this Windows account (Active
/// Directory), LDAP over TLS, or Microsoft Entra ID — with the group → role map and the required group. The sign-in runs
/// right here before the profile is made, so a binding that doesn't work is never saved. Nothing typed is stored.
/// </summary>
public static class DirectoryProfileDialog
{
    /// <summary>Asks for the binding, signs in, and returns the binding bound to the account that signed in. Null = cancelled or failed.</summary>
    public static DirectoryBinding? Ask(Window? owner, DirectoryBinding? existing = null)
    {
        var w = Dialogs.MakeWindow("Sign in with your organisation", 560);
        if (owner != null) w.Owner = owner;
        var p = new StackPanel { Margin = new Thickness(18) };
        TextBlock L(string t) => new() { Text = t, Style = (Style)Application.Current.Resources["Label"], Margin = new Thickness(0, 8, 0, 4) };
        TextBox T(string v, string? tip = null) => new() { Text = v, Padding = new Thickness(5, 4, 5, 4), ToolTip = tip };
        p.Children.Add(new TextBlock { Text = "The profile opens only after this sign-in succeeds. The directory's password or token is never stored: the Windows session, Kerberos, or a browser sign-in with MFA does the work each time.", TextWrapping = TextWrapping.Wrap });
        var kind = new ComboBox { Margin = new Thickness(0, 4, 0, 0) };
        kind.Items.Add("Windows / Active Directory — this Windows account (no password typed)");
        kind.Items.Add("LDAP over TLS (LDAPS / StartTLS) — Kerberos or a password inside TLS");
        kind.Items.Add("Microsoft Entra ID — sign in through the browser (MFA, Conditional Access)");
        kind.SelectedIndex = existing?.Kind switch { "ldaps" => 1, "entra" => 2, _ => 0 };
        p.Children.Add(L("HOW")); p.Children.Add(kind);
        var ldap = new StackPanel(); var entra = new StackPanel();
        var server = T(existing?.Server ?? "", "ldap.example.com"); var port = T(existing?.Port.ToString() ?? "636");
        var transport = new ComboBox(); transport.Items.Add("ldaps (TLS from the first byte, 636)"); transport.Items.Add("starttls (389 → TLS before anything else)"); transport.SelectedIndex = existing?.Transport == "starttls" ? 1 : 0;
        var bind = new ComboBox(); bind.Items.Add("negotiate — the Windows session (Kerberos), nothing typed"); bind.Items.Add("simple — a password, asked each time, sent only inside TLS"); bind.SelectedIndex = existing?.Bind == "simple" ? 1 : 0;
        var baseDn = T(existing?.BaseDn ?? "", "Empty = the server's default naming context"); var pin = T(existing?.CertPin ?? "", "Optional: SHA-256 of the server certificate (self-signed servers)");
        var ldapUser = T(existing?.Kind == "ldaps" ? existing.User : Environment.UserName + "@" + (Environment.UserDomainName.Contains('.') ? Environment.UserDomainName : Environment.UserDomainName.ToLowerInvariant() + ".local"), "user@domain or DOMAIN\\user");
        foreach (var (l, c) in new (string, UIElement)[] { ("SERVER", server), ("PORT", port), ("TRANSPORT", transport), ("BIND", bind), ("BASE DN (optional)", baseDn), ("CERTIFICATE PIN (optional)", pin), ("ACCOUNT", ldapUser) }) { ldap.Children.Add(L(l)); ldap.Children.Add(c); }
        var tenant = T(existing?.Kind == "entra" ? existing.Server : "", "The tenant id (GUID) or verified domain"); var client = T(existing?.ClientId ?? "", "The app registration's client id (public client, redirect http://127.0.0.1)");
        var upn = T(existing?.Kind == "entra" ? existing.User : "", "name@tenant-domain"); var authority = T(existing?.Authority ?? "", "Empty = https://login.microsoftonline.com/<tenant>/v2.0");
        foreach (var (l, c) in new (string, UIElement)[] { ("TENANT", tenant), ("CLIENT ID", client), ("ACCOUNT (UPN)", upn), ("AUTHORITY (optional)", authority) }) { entra.Children.Add(L(l)); entra.Children.Add(c); }
        p.Children.Add(ldap); p.Children.Add(entra);
        var required = T(existing?.RequiredGroup ?? "", "Only members of this group can open the profile (empty = any)");
        var roles = T(existing != null ? string.Join("\n", existing.GroupRoles.Select(kv => kv.Key + " = " + kv.Value)) : "", "One per line: Group name = admin | operator | viewer. First match wins; no match = operator. A viewer can't change anything.");
        roles.AcceptsReturn = true; roles.MinHeight = 60; roles.TextWrapping = TextWrapping.NoWrap;
        var grace = T(existing?.OfflineGraceDays.ToString() ?? "7", "Days a cached sign-in proof may open the profile while the directory can't be reached (0 = never)");
        p.Children.Add(L("REQUIRED GROUP (optional)")); p.Children.Add(required);
        p.Children.Add(L("GROUP → ROLE (optional)")); p.Children.Add(roles);
        p.Children.Add(L("OFFLINE GRACE (days)")); p.Children.Add(grace);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
        status.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        p.Children.Add(status);
        void Show() { ldap.Visibility = kind.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed; entra.Visibility = kind.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed; }
        kind.SelectionChanged += (_, _) => Show(); Show();
        var ok = new Button { Content = "Sign in now", Style = (Style)Application.Current.Resources["PrimaryButton"], IsDefault = true };
        var bs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        bs.Children.Add(ok); bs.Children.Add(new Button { Content = "Cancel", IsCancel = true });
        p.Children.Add(bs);
        w.Content = new ScrollViewer { Content = p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 720 };
        DirectoryBinding? result = null;
        ok.Click += async (_, _) =>
        {
            var b = kind.SelectedIndex switch
            {
                1 => new DirectoryBinding { Kind = "ldaps", Server = server.Text.Trim(), Port = int.TryParse(port.Text.Trim(), out var pt) ? pt : 0, Transport = transport.SelectedIndex == 1 ? "starttls" : "ldaps", Bind = bind.SelectedIndex == 1 ? "simple" : "negotiate", BaseDn = baseDn.Text.Trim(), CertPin = pin.Text.Replace(":", "").Replace(" ", "").Trim(), User = ldapUser.Text.Trim() },
                2 => new DirectoryBinding { Kind = "entra", Server = tenant.Text.Trim(), ClientId = client.Text.Trim(), User = upn.Text.Trim(), Authority = authority.Text.Trim() },
                _ => WindowsAccountSignIn.BindCurrent(),
            };
            b.RequiredGroup = required.Text.Trim();
            b.OfflineGraceDays = int.TryParse(grace.Text.Trim(), out var g) ? g : 7;
            foreach (var line in roles.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var eq = line.LastIndexOf('=');
                if (eq > 0) b.GroupRoles[line[..eq].Trim()] = line[(eq + 1)..].Trim().ToLowerInvariant();
            }
            if (b.Problem() is { } bad) { status.Text = "✕ " + bad; return; }
            ok.IsEnabled = false; status.Text = "Signing in…";
            SignInResult r;
            try { r = await DirectorySignIn.Providers[b.Kind].SignInAsync(b, q => Dialogs.Prompt("Sign in", q, password: true), new CancellationTokenSource(TimeSpan.FromMinutes(6)).Token); }
            catch (Exception ex) { r = SignInResult.Fail(ex.Message); }
            ok.IsEnabled = true;
            if (!r.Ok) { status.Text = "✕ " + r.Error; return; }
            if (b.Kind != "windows" || b.Subject.Length == 0) b.Subject = r.Subject;   // bound to the account that signed in
            if (DirectorySignIn.Check(b, r) is { } why) { status.Text = "✕ " + why; return; }
            status.Text = $"✓ Signed in as {r.DisplayName} ({r.Subject}); {r.Groups.Count} group(s); role {b.RoleFor(r.Groups)}.";
            result = b;
            w.DialogResult = true;
        };
        return w.ShowDialog() == true ? result : null;
    }
}
