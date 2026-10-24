using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using DcxManager.Services;
using DcxManager.Sessions;
using DcxManager.Ssh;
using DcxManager.Vultr;
using Microsoft.Win32;

namespace DcxManager.Views;

// SettingsView: the Vultr, Contabo and Hetzner Cloud account cards (moved unchanged from SettingsView.cs in 0.2.0 phase 6
public sealed partial class SettingsView
{
    // ---- Vultr -------------------------------------------------------------------------------------------------------

    private Border Vultr()
    {
        var gv = new GridView();
        gv.Columns.Add(new GridViewColumn { Header = "Name", Width = 200, DisplayMemberBinding = new Binding("Name") });
        gv.Columns.Add(new GridViewColumn { Header = "API key", Width = 300, DisplayMemberBinding = new Binding("Masked") });
        gv.Columns.Add(new GridViewColumn { Header = "State", Width = 80, DisplayMemberBinding = new Binding("State") });
        _accounts.View = gv;
        RefreshAccounts();
        var card = Card("Vultr accounts",
            "Vultr doesn't offer sign-in (OAuth) for other apps: access is by Personal Access Token. In my.vultr.com → Account → API, enable the API, copy the key " +
            "and add this PC's public IP under Access Control. Use a sub-account user with limited permissions if you want to restrict what the app can do. " +
            "For the web portal itself, use Vultr portal in the sidebar — you sign in there with your normal Vultr login and 2FA.",
            NetUi.External("Internet access",
                "Vultr accounts talk to Vultr's API on the internet (api.vultr.com) when you open Vultr views, the dashboard or the API explorer. " +
                "Nothing is sent while no account exists, and nothing runs in the background. The portal opens my.vultr.com in an embedded browser."),
            _accounts,
            Row(Btn("＋ Add account…", AddAccount, "PrimaryButton"), Btn("Test", async () => await TestAccount()), Btn("Make active", MakeActive), Btn("Rename…", RenameAccount), Btn("Replace key…", ReplaceKey),
                Btn("Disable / enable", () => { if (SelAccount is { } a) { a.Disabled = !a.Disabled; Save(); RefreshAccounts(); VultrProvider.Instance.AccountsChanged(); AuditLog.Write("you", "vultr:" + a.Name, a.Disabled ? "account disabled" : "account enabled"); } }),
                Btn("✕ Remove", RemoveAccount, "DangerButton")));
        // Scope the menu to this card so it mirrors only the account buttons (the key itself is never offered for copy).
        Menus.ForList(_accounts, card, () => SelAccount is not { } a ? Array.Empty<object?>() : new object?[] { Menus.Item("Copy account name", () => Menus.Clip(a.Name)) });
        return card;
    }

    private void RefreshAccounts() { _accounts.ItemsSource = null; _accounts.ItemsSource = _s.VultrAccounts; }
    private VultrAccount? SelAccount => _accounts.SelectedItem as VultrAccount;

    private async void AddAccount()
    {
        if (!Restrictions.Check("cloud.add")) return;
        if (Dialogs.Prompt("Vultr account", "A name for this account (e.g. Personal, Work):", "Vultr") is not { Length: > 0 } name) return;
        if (Dialogs.Prompt("Vultr account", "Personal Access Token (API key):", password: true) is not { Length: > 0 } key) return;
        var acct = new VultrAccount { Name = name, ApiKey = key.Trim() };
        try
        {
            var a = await new VultrClient(acct).AccountAsync();
            _s.VultrAccounts.Add(acct);
            _s.ActiveVultrAccountId ??= acct.Id;
            Save();
            RefreshAccounts(); VultrProvider.Instance.AccountsChanged();
            AuditLog.Write("you", "vultr:" + name, "API key added");
            Dialogs.Info("Vultr", $"Connected: {J.Str(a, "name")} <{J.Str(a, "email")}>\nPermissions: {J.Str(a, "acls")}");
        }
        catch (Exception ex) { Dialogs.Error("Vultr", "The key didn't work:\n\n" + ex.Message); }
    }

    private async Task TestAccount()
    {
        if (SelAccount is not { } acct) return;
        try { var a = await new VultrClient(acct).AccountAsync(); Dialogs.Info("Vultr", $"✓ {J.Str(a, "name")} <{J.Str(a, "email")}> · balance {J.Str(a, "balance")}"); }
        catch (Exception ex) { Dialogs.Error("Vultr", ex.Message); }
    }

    private void MakeActive()
    {
        if (SelAccount is not { } acct) return;
        VultrProvider.Instance.SetActive(acct.Id);   // open Vultr screens reload (ADR-020)
        RefreshAccounts();
    }

    private void RenameAccount()
    {
        if (!Restrictions.Check("cloud.edit")) return;
        if (SelAccount is not { } acct || Dialogs.Prompt("Rename", "Name:", acct.Name) is not { Length: > 0 } n) return;
        acct.Name = n; Save(); RefreshAccounts();
    }

    private async void ReplaceKey()
    {
        if (!Restrictions.Check("cloud.edit")) return;
        if (SelAccount is not { } acct || Dialogs.Prompt("Replace key", "New API key:", password: true) is not { Length: > 0 } key) return;
        var test = new VultrAccount { Name = acct.Name, ApiKey = key.Trim() };
        try { await new VultrClient(test).AccountAsync(); acct.ApiKey = test.ApiKey; Save(); RefreshAccounts(); VultrProvider.Instance.AccountsChanged(); AuditLog.Write("you", "vultr:" + acct.Name, "API key replaced"); }
        catch (Exception ex) { Dialogs.Error("Vultr", "The new key didn't work:\n\n" + ex.Message); }
    }

    private void RemoveAccount()
    {
        if (!Restrictions.Check("cloud.edit")) return;
        if (SelAccount is not { } acct || !Dialogs.Confirm("Remove account", $"Remove “{acct.Name}” and its API key from this app? (Nothing changes at Vultr. Revoke the key there too if you no longer need it.)", "Remove", danger: true)) return;
        _s.VultrAccounts.Remove(acct);
        if (_s.ActiveVultrAccountId == acct.Id) _s.ActiveVultrAccountId = _s.VultrAccounts.FirstOrDefault()?.Id;
        Save(); RefreshAccounts(); VultrProvider.Instance.AccountsChanged();
        AuditLog.Write("you", "vultr:" + acct.Name, "API key removed");
    }

    // ---- Contabo (ADR-020) -------------------------------------------------------------------------------------------

    private readonly ListView _contabo = new() { Height = 110 };
    private ContaboAccount? SelContabo => _contabo.SelectedItem as ContaboAccount;

    private Border ContaboCard()
    {
        var gv = new GridView();
        gv.Columns.Add(new GridViewColumn { Header = "Name", Width = 160, DisplayMemberBinding = new Binding("Name") });
        gv.Columns.Add(new GridViewColumn { Header = "Client ID", Width = 170, DisplayMemberBinding = new Binding("MaskedClient") });
        gv.Columns.Add(new GridViewColumn { Header = "API user", Width = 200, DisplayMemberBinding = new Binding("ApiUser") });
        gv.Columns.Add(new GridViewColumn { Header = "State", Width = 80, DisplayMemberBinding = new Binding("State") });
        _contabo.View = gv;
        RefreshContabo();
        var caps = Contabo.ContaboProvider.Instance.Caps;
        var card = Card("Contabo accounts",
            "Contabo's API signs in with four values from the Customer Control Panel → API: the Client ID, the Client Secret, your API user (your Contabo login e-mail) " +
            "and an API password you set there. They are stored only in the encrypted settings and never shown again. Several accounts are supported; the active one is used by the Contabo screens " +
            "(switch it in the header while a Contabo screen is open).",
            NetUi.External("Internet access",
                "Contabo accounts talk to api.contabo.com and auth.contabo.com on the internet when you open Contabo screens, press Check now / Test, or include Contabo in the dashboard's checks. " +
                "Nothing runs in the background; offline mode blocks both."),
            _contabo,
            Row(Btn("＋ Add account…", AddContabo, "PrimaryButton"), Btn("Test", async () => await TestContabo()), Btn("Make active", () => { if (SelContabo is { } a) { Contabo.ContaboProvider.Instance.SetActive(a.Id); RefreshContabo(); } }),
                Btn("Rename…", () => { if (SelContabo is { } a && Dialogs.Prompt("Rename", "Name:", a.Name) is { Length: > 0 } n) { a.Name = n; Save(); RefreshContabo(); } }),
                Btn("Replace credentials…", ReplaceContabo),
                Btn("Disable / enable", () => { if (SelContabo is { } a) { a.Disabled = !a.Disabled; Save(); RefreshContabo(); Contabo.ContaboProvider.Instance.AccountsChanged(); AuditLog.Write("you", "contabo:" + a.Name, a.Disabled ? "account disabled" : "account enabled"); } }),
                Btn("✕ Remove", RemoveContabo, "DangerButton")),
            new TextBlock { Text = "Available through Contabo's API: " + string.Join(", ", Enum.GetValues<CloudCaps>().Where(c => c != CloudCaps.None && caps.HasFlag(c)).Select(c => c.ToString())) +
                               ".  Not available: delete (contracts are cancelled instead), resize (no product change), web console.", Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        Menus.ForList(_contabo, card, () => SelContabo is not { } a ? Array.Empty<object?>() : new object?[] { Menus.Item("Copy account name", () => Menus.Clip(a.Name)) });
        return card;
    }

    private void RefreshContabo() { _contabo.ItemsSource = null; _contabo.ItemsSource = _s.ContaboAccounts; }

    /// <summary>Asks for the four credentials (secrets in password boxes). Null when cancelled.</summary>
    private static ContaboAccount? AskContabo(string title, ContaboAccount? current)
    {
        var name = current?.Name ?? Dialogs.Prompt(title, "A name for this account (e.g. Production, Lab):", "Contabo");
        if (name is not { Length: > 0 }) return null;
        if (Dialogs.Prompt(title, "Client ID (Customer Control Panel → API):", current?.ClientId ?? "") is not { Length: > 0 } cid) return null;
        if (Dialogs.Prompt(title, "Client Secret:", password: true) is not { Length: > 0 } secret) return null;
        if (Dialogs.Prompt(title, "API user (your Contabo login e-mail):", current?.ApiUser ?? "") is not { Length: > 0 } user) return null;
        if (Dialogs.Prompt(title, "API password (set in the Customer Control Panel → API):", password: true) is not { Length: > 0 } pw) return null;
        return new ContaboAccount { Name = name.Trim(), ClientId = cid.Trim(), ClientSecret = secret.Trim(), ApiUser = user.Trim(), ApiPassword = pw };
    }

    private async void AddContabo()
    {
        if (!Restrictions.Check("cloud.add")) return;
        if (AskContabo("Contabo account", null) is not { } acct) return;
        try
        {
            int n = await new Contabo.ContaboClient(acct).TestAsync();
            _s.ContaboAccounts.Add(acct);
            _s.ActiveContaboAccountId ??= acct.Id;
            Save(); RefreshContabo(); Contabo.ContaboProvider.Instance.AccountsChanged();
            AuditLog.Write("you", "contabo:" + acct.Name, "API account added");
            Dialogs.Info("Contabo", $"Connected: {n} instance(s) on this account.");
        }
        catch (Exception ex) { Contabo.ContaboClient.Forget(acct.Id); Dialogs.Error("Contabo", "The credentials didn't work:\n\n" + ex.Message); }
    }

    private async Task TestContabo()
    {
        if (SelContabo is not { } acct) return;
        try { int n = await new Contabo.ContaboClient(acct).TestAsync(); Dialogs.Info("Contabo", $"✓ Connected · {n} instance(s) on {acct.Name}."); }
        catch (Exception ex) { Dialogs.Error("Contabo", ex.Message + (ex is CloudApiException { RequestId: { } rid } ? $"\n\nRequest id (for Contabo support): {rid}" : "")); }
    }

    private async void ReplaceContabo()
    {
        if (!Restrictions.Check("cloud.edit")) return;
        if (SelContabo is not { } acct || AskContabo("Replace credentials", acct) is not { } test) return;
        test.Id = Guid.NewGuid().ToString("N");   // a throw-away id: the old token stays valid until the new values work
        try
        {
            await new Contabo.ContaboClient(test).TestAsync();
            acct.ClientId = test.ClientId; acct.ClientSecret = test.ClientSecret; acct.ApiUser = test.ApiUser; acct.ApiPassword = test.ApiPassword;
            Contabo.ContaboClient.Forget(acct.Id);
            Save(); RefreshContabo(); Contabo.ContaboProvider.Instance.AccountsChanged();
            AuditLog.Write("you", "contabo:" + acct.Name, "API credentials replaced");
        }
        catch (Exception ex) { Dialogs.Error("Contabo", "The new credentials didn't work:\n\n" + ex.Message); }
        finally { Contabo.ContaboClient.Forget(test.Id); }
    }

    private void RemoveContabo()
    {
        if (!Restrictions.Check("cloud.edit")) return;
        if (SelContabo is not { } acct || !Dialogs.Confirm("Remove account", $"Remove “{acct.Name}” and its credentials from this app? (Nothing changes at Contabo. Revoke the API access there too if you no longer need it.)", "Remove", danger: true)) return;
        _s.ContaboAccounts.Remove(acct);
        if (_s.ActiveContaboAccountId == acct.Id) _s.ActiveContaboAccountId = _s.ContaboAccounts.FirstOrDefault()?.Id;
        Contabo.ContaboClient.Forget(acct.Id);
        Save(); RefreshContabo(); Contabo.ContaboProvider.Instance.AccountsChanged();
        AuditLog.Write("you", "contabo:" + acct.Name, "API account removed");
    }

    // ---- Hetzner Cloud (ADR-028) ----------------------------------------------------------------------------------------

    private readonly ListView _hetzner = new() { Height = 110 };
    private HetznerAccount? SelHetzner => _hetzner.SelectedItem as HetznerAccount;

    private Border HetznerCard()
    {
        var gv = new GridView();
        gv.Columns.Add(new GridViewColumn { Header = "Project", Width = 180, DisplayMemberBinding = new Binding("Name") });
        gv.Columns.Add(new GridViewColumn { Header = "Token", Width = 200, DisplayMemberBinding = new Binding("MaskedToken") });
        gv.Columns.Add(new GridViewColumn { Header = "State", Width = 170, DisplayMemberBinding = new Binding("State") });
        _hetzner.View = gv;
        RefreshHetzner();
        var p = Hetzner.HetznerProvider.Instance;
        var card = Card("Hetzner Cloud projects",
            "Hetzner gives every project its own API token (Hetzner Console → project → Security → API tokens). Add one per project you want to manage. " +
            "A “Read” token only lets the app look; mark it read-only here and the app refuses every change before sending it. " +
            "Tokens are stored only in the encrypted settings and never shown again.",
            NetUi.External("Internet access",
                "Hetzner projects talk to api.hetzner.cloud on the internet when you open Hetzner screens, press Test, or include Hetzner in the dashboard's checks. Nothing runs in the background; offline mode blocks it."),
            _hetzner,
            Row(Btn("＋ Add project…", AddHetzner, "PrimaryButton"), Btn("Test", async () => await TestHetzner()), Btn("Make active", () => { if (SelHetzner is { } a) { p.SetActive(a.Id); RefreshHetzner(); } }),
                Btn("Rename…", () => { if (SelHetzner is { } a && Dialogs.Prompt("Rename", "Name:", a.Name) is { Length: > 0 } n) { a.Name = n.Trim(); Save(); RefreshHetzner(); } }),
                Btn("Replace token…", ReplaceHetzner),
                Btn("Disable / enable", () => { if (SelHetzner is { } a) { a.Disabled = !a.Disabled; Save(); RefreshHetzner(); p.AccountsChanged(); AuditLog.Write("you", "hetzner:" + a.Name, a.Disabled ? "project disabled" : "project enabled"); } }),
                Btn("✕ Remove", RemoveHetzner, "DangerButton")),
            new TextBlock { Text = "Available through the Hetzner Cloud API in this app: " + string.Join(", ", Enum.GetValues<CloudCaps>().Where(c => c != CloudCaps.None && p.Caps.HasFlag(c)).Select(c => c.ToString())) +
                               ", plus volumes, floating and primary IPs, load balancers, placement groups and certificates. Not available: the web console (VNC), metrics, and Hetzner's separate Robot (dedicated servers) and Storage Box APIs.",
                Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        Menus.ForList(_hetzner, card, () => SelHetzner is not { } a ? Array.Empty<object?>() : new object?[] { Menus.Item("Copy project name", () => Menus.Clip(a.Name)) });
        return card;
    }

    private void RefreshHetzner() { _hetzner.ItemsSource = null; _hetzner.ItemsSource = _s.HetznerAccounts; }

    /// <summary>Asks for the project name, the token (password box) and whether it is read-only. Null when cancelled.</summary>
    private static HetznerAccount? AskHetzner(string title, HetznerAccount? current)
    {
        var name = current?.Name ?? Dialogs.Prompt(title, "The project's name (as you call it, e.g. Production):", "Hetzner");
        if (name is not { Length: > 0 }) return null;
        if (Dialogs.Prompt(title, "API token (Hetzner Console → project → Security → API tokens):", password: true) is not { Length: > 0 } token) return null;
        token = token.Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(token, "^[A-Za-z0-9]{32,128}$")) { Dialogs.Error(title, "That doesn't look like a Hetzner API token (64 letters and digits)."); return null; }
        var kind = Dialogs.Choose(title, "What permission did you give this token in the Hetzner Console?", new[] { "Cancel", "Read & Write", "Read only" }, cancelIndex: 0);
        if (kind <= 0) return null;
        return new HetznerAccount { Name = name.Trim(), Token = token, ReadOnly = kind == 2 };
    }

    private async void AddHetzner()
    {
        if (!Restrictions.Check("cloud.add")) return;
        if (AskHetzner("Hetzner Cloud project", null) is not { } acct) return;
        try
        {
            int n = await new Hetzner.HetznerClient(acct).TestAsync();
            _s.HetznerAccounts.Add(acct);
            _s.ActiveHetznerAccountId ??= acct.Id;
            Save(); RefreshHetzner(); Hetzner.HetznerProvider.Instance.AccountsChanged();
            AuditLog.Write("you", "hetzner:" + acct.Name, "API project added", acct.ReadOnly ? "read-only token" : "read & write token");
            Dialogs.Info("Hetzner Cloud", $"Connected: {n} server(s) in this project.");
        }
        catch (Exception ex) { Dialogs.Error("Hetzner Cloud", "The token didn't work, so nothing was saved:\n\n" + ex.Message); }
    }

    private async Task TestHetzner()
    {
        if (SelHetzner is not { } acct) return;
        try
        {
            int n = await new Hetzner.HetznerClient(acct).TestAsync();
            var rl = Hetzner.HetznerClient.RateLimitOf(acct.Id);
            Dialogs.Info("Hetzner Cloud", $"✓ Connected · {n} server(s) in {acct.Name}" + (rl is { } l ? $" · {l.Remaining} of {l.Limit} requests left this hour" : "") +
                (acct.ReadOnly ? "\n\nMarked read-only: the app won't send changes with this token." : "\n\nWrite permission can't be checked without making a change; a Read-only token is refused by Hetzner on the first change."));
        }
        catch (Exception ex) { Dialogs.Error("Hetzner Cloud", ex.Message); }
    }

    private async void ReplaceHetzner()
    {
        if (!Restrictions.Check("cloud.edit")) return;
        if (SelHetzner is not { } acct || AskHetzner("Replace token", acct) is not { } test) return;
        try
        {
            await new Hetzner.HetznerClient(test).TestAsync();
            acct.Token = test.Token; acct.ReadOnly = test.ReadOnly;
            Save(); RefreshHetzner(); Hetzner.HetznerProvider.Instance.AccountsChanged();
            AuditLog.Write("you", "hetzner:" + acct.Name, "API token replaced", acct.ReadOnly ? "read-only token" : "read & write token");
        }
        catch (Exception ex) { Dialogs.Error("Hetzner Cloud", "The new token didn't work (the old one is kept):\n\n" + ex.Message); }
    }

    private void RemoveHetzner()
    {
        if (!Restrictions.Check("cloud.edit")) return;
        if (SelHetzner is not { } acct || !Dialogs.Confirm("Remove project", $"Remove “{acct.Name}” and its token from this app? (Nothing changes at Hetzner. Delete the token in the Hetzner Console too if you no longer need it.)", "Remove", danger: true)) return;
        _s.HetznerAccounts.Remove(acct);
        if (_s.ActiveHetznerAccountId == acct.Id) _s.ActiveHetznerAccountId = _s.HetznerAccounts.FirstOrDefault()?.Id;
        Save(); RefreshHetzner(); Hetzner.HetznerProvider.Instance.AccountsChanged();
        AuditLog.Write("you", "hetzner:" + acct.Name, "API project removed");
    }
}
