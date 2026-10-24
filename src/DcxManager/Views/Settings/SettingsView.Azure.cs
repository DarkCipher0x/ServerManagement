using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using DcxManager.Azure;
using DcxManager.Services;
using Microsoft.Win32;

namespace DcxManager.Views;

// SettingsView: the Azure subscriptions card (0.2.0). One file per cloud service.
public sealed partial class SettingsView
{
    private readonly ListView _azure = new() { Height = 110 };
    private AzureAccount? SelAzure => _azure.SelectedItem as AzureAccount;
    private const int MaxCertificateBytes = 64 * 1024;

    private Border AzureCard()
    {
        var gv = new GridView();
        gv.Columns.Add(new GridViewColumn { Header = "Subscription", Width = 170, DisplayMemberBinding = new Binding("Name") });
        gv.Columns.Add(new GridViewColumn { Header = "Subscription id", Width = 250, DisplayMemberBinding = new Binding("SubscriptionId") });
        gv.Columns.Add(new GridViewColumn { Header = "State", Width = 180, DisplayMemberBinding = new Binding("State") });
        _azure.View = gv;
        RefreshAzure();
        var p = AzureProvider.Instance;
        var card = Card("Azure subscriptions",
            "The app signs in to Azure as an Entra app registration with a certificate (client secrets aren't accepted): give the registration a role on the subscription, " +
            "then add its tenant id, application (client) id, the subscription id and the certificate as a .pfx with its private key. " +
            "A new subscription is read-only here until you allow changes; the app then still asks before each one. The certificate is stored only in the encrypted settings and never shown again.",
            NetUi.External("Internet access",
                "Azure subscriptions talk to login.microsoftonline.com (sign-in) and management.azure.com when you open Azure screens, press Test, or include Azure in the dashboard's checks. Nothing runs in the background; offline mode blocks it."),
            _azure,
            Row(Btn("＋ Add subscription…", AddAzure, "PrimaryButton"), Btn("Test", async () => await TestAzure()), Btn("Make active", () => { if (SelAzure is { } a) { p.SetActive(a.Id); RefreshAzure(); } }),
                Btn("Rename…", () => { if (SelAzure is { } a && Dialogs.Prompt("Rename", "Name:", a.Name) is { Length: > 0 } n) { a.Name = n.Trim(); Save(); RefreshAzure(); } }),
                Btn("Replace certificate…", ReplaceAzure),
                Btn("Allow changes / read-only", ToggleAzureChanges),
                Btn("Disable / enable", () => { if (SelAzure is { } a) { a.Disabled = !a.Disabled; Save(); RefreshAzure(); p.AccountsChanged(); AuditLog.Write("you", "azure:" + a.Name, a.Disabled ? "subscription disabled" : "subscription enabled"); } }),
                Btn("✕ Remove", RemoveAzure, "DangerButton")),
            new TextBlock { Text = "Available through Azure Resource Manager in this app: virtual machines (start, deallocate, power off, restart, redeploy, resize, extensions), VM scale sets (start / deallocate / restart / scale), availability sets, AKS (start / stop, node pools), " +
                               "App Service (start / stop / restart), container instances (start / stop / restart), Azure SQL servers and databases, PostgreSQL and MySQL flexible servers (start / stop / restart), disks and snapshots, storage accounts (settings), " +
                               "virtual networks, network security groups and rules, network interfaces, public IPs, load balancers, DNS and private DNS zones with records and links, key vaults (settings only), resource groups, every resource, " +
                               "Resource Health, Advisor recommendations, and a read-only API explorer. Not available: secrets, keys and connection strings of any kind, the serial console, and identity / role assignments.",
                Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        Menus.ForList(_azure, card, () => SelAzure is not { } a ? Array.Empty<object?>() : new object?[]
        {
            Menus.Item("Copy subscription id", () => Menus.Clip(a.SubscriptionId)),
            Menus.Item("Copy certificate thumbprint (SHA-256)", () => Menus.Clip(a.CertificateSha256)),
        });
        return card;
    }

    private void RefreshAzure() { _azure.ItemsSource = null; _azure.ItemsSource = _s.AzureAccounts; }

    private static bool IsGuid(string s) => Guid.TryParse(s, out _);

    /// <summary>The certificate (.pfx) and its password; checks that it opens and has an RSA private key. Null when cancelled.</summary>
    private static (string Pfx, string Password, string Sha256)? AskCertificate(string title)
    {
        var dlg = new OpenFileDialog { Title = title + " — certificate with private key", Filter = "Certificate with key (*.pfx;*.p12)|*.pfx;*.p12|All files|*.*" };
        if (dlg.ShowDialog() != true) return null;
        var info = new FileInfo(dlg.FileName);
        if (info.Length > MaxCertificateBytes) { Dialogs.Error(title, "That file is too big for a certificate."); return null; }
        var pfx = Convert.ToBase64String(File.ReadAllBytes(dlg.FileName));
        var password = Dialogs.Prompt(title, "The .pfx file's password (leave empty if it has none):", password: true);
        if (password == null) return null;
        try
        {
            using var cert = AzureAuth.LoadCertificate(new AzureAccount { CertificatePfx = pfx, CertificatePassword = password });
            if (cert.GetRSAPrivateKey() is not { } key) { Dialogs.Error(title, "The certificate's key isn't an RSA key (Entra ID needs RSA)."); return null; }
            key.Dispose();
            if (cert.NotAfter < DateTime.Now) { Dialogs.Error(title, $"The certificate expired on {cert.NotAfter:d}."); return null; }
            return (pfx, password, AzureAuth.Sha256Hex(cert));
        }
        catch (Cloud.CloudApiException ex) { Dialogs.Error(title, ex.Message); return null; }
    }

    private static AzureAccount? AskAzure(string title)
    {
        if (Dialogs.Prompt(title, "A name for this subscription (as you call it, e.g. Production):", "Azure") is not { Length: > 0 } name) return null;
        if (Dialogs.Prompt(title, "Directory (tenant) id — Entra admin center → Overview:") is not { Length: > 0 } tenant) return null;
        if (!IsGuid(tenant.Trim())) { Dialogs.Error(title, "The tenant id is a GUID (xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx)."); return null; }
        if (Dialogs.Prompt(title, "Application (client) id of the app registration:") is not { Length: > 0 } client) return null;
        if (!IsGuid(client.Trim())) { Dialogs.Error(title, "The application (client) id is a GUID."); return null; }
        if (Dialogs.Prompt(title, "Subscription id (Azure portal → Subscriptions):") is not { Length: > 0 } sub) return null;
        if (!IsGuid(sub.Trim())) { Dialogs.Error(title, "The subscription id is a GUID."); return null; }
        if (AskCertificate(title) is not { } cert) return null;
        return new AzureAccount
        {
            Name = name.Trim(), TenantId = tenant.Trim(), ClientId = client.Trim(), SubscriptionId = sub.Trim(),
            CertificatePfx = cert.Pfx, CertificatePassword = cert.Password, CertificateSha256 = cert.Sha256, ReadOnly = true,
        };
    }

    private async void AddAzure()
    {
        if (!Restrictions.Check("cloud.add")) return;
        if (AskAzure("Azure subscription") is not { } acct) return;
        try
        {
            var what = await new AzureClient(acct).TestAsync();
            _s.AzureAccounts.Add(acct);
            _s.ActiveAzureAccountId ??= acct.Id;
            Save(); RefreshAzure(); AzureProvider.Instance.AccountsChanged();
            AuditLog.Write("you", "azure:" + acct.Name, "subscription added", "read-only · certificate " + acct.CertificateSha256[..12]);
            Dialogs.Info("Microsoft Azure", $"Connected: {what}.\n\nThe subscription is read-only in this app until you press “Allow changes / read-only”.");
        }
        catch (Exception ex) { Dialogs.Error("Microsoft Azure", "Signing in didn't work, so nothing was saved:\n\n" + ex.Message); }
    }

    private async Task TestAzure()
    {
        if (SelAzure is not { } acct) return;
        try
        {
            AzureAuth.Forget(acct.Id);
            var what = await new AzureClient(acct).TestAsync();
            Dialogs.Info("Microsoft Azure", $"✓ Connected · {what}" +
                (acct.ReadOnly ? "\n\nRead-only in this app: changes are refused before they are sent." : "\n\nChanges are allowed in this app; Azure still checks the app registration's role on each one."));
        }
        catch (Exception ex) { Dialogs.Error("Microsoft Azure", ex.Message); }
    }

    private async void ReplaceAzure()
    {
        if (!Restrictions.Check("cloud.edit")) return;
        if (SelAzure is not { } acct || AskCertificate("Replace certificate") is not { } cert) return;
        var test = new AzureAccount { Id = "replace-test", Name = acct.Name, TenantId = acct.TenantId, ClientId = acct.ClientId, SubscriptionId = acct.SubscriptionId, CertificatePfx = cert.Pfx, CertificatePassword = cert.Password, ReadOnly = true };
        try
        {
            await new AzureClient(test).TestAsync();
            AzureAuth.Forget(test.Id);
            acct.CertificatePfx = cert.Pfx; acct.CertificatePassword = cert.Password; acct.CertificateSha256 = cert.Sha256;
            AzureAuth.Forget(acct.Id);
            Save(); RefreshAzure(); AzureProvider.Instance.AccountsChanged();
            AuditLog.Write("you", "azure:" + acct.Name, "certificate replaced", "certificate " + cert.Sha256[..12]);
        }
        catch (Exception ex) { AzureAuth.Forget(test.Id); Dialogs.Error("Microsoft Azure", "The new certificate didn't work (the old one is kept):\n\n" + ex.Message); }
    }

    private void ToggleAzureChanges()
    {
        if (!Restrictions.Check("cloud.edit")) return;
        if (SelAzure is not { } acct) return;
        if (acct.ReadOnly && !Dialogs.Confirm("Allow changes", $"Allow changes to “{acct.Name}” from this app? Each change still asks first, and Azure still checks the app registration's role.", "Allow changes", danger: true)) return;
        acct.ReadOnly = !acct.ReadOnly;
        Save(); RefreshAzure(); AzureProvider.Instance.AccountsChanged();
        AuditLog.Write("you", "azure:" + acct.Name, acct.ReadOnly ? "subscription set read-only" : "changes allowed");
    }

    private void RemoveAzure()
    {
        if (!Restrictions.Check("cloud.edit")) return;
        if (SelAzure is not { } acct || !Dialogs.Confirm("Remove subscription", $"Remove “{acct.Name}” and its certificate from this app? (Nothing changes in Azure. Remove the certificate from the app registration too if you no longer need it.)", "Remove", danger: true)) return;
        _s.AzureAccounts.Remove(acct);
        AzureAuth.Forget(acct.Id);
        if (_s.ActiveAzureAccountId == acct.Id) _s.ActiveAzureAccountId = _s.AzureAccounts.FirstOrDefault()?.Id;
        Save(); RefreshAzure(); AzureProvider.Instance.AccountsChanged();
        AuditLog.Write("you", "azure:" + acct.Name, "subscription removed");
    }
}
