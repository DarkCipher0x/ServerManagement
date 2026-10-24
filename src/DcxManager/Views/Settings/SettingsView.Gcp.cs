using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using DcxManager.Gcp;
using DcxManager.Services;
using Microsoft.Win32;

namespace DcxManager.Views;

// SettingsView: the Google Cloud projects card (0.2.0). One file per cloud service.
public sealed partial class SettingsView
{
    private readonly ListView _gcp = new() { Height = 110 };
    private GcpAccount? SelGcp => _gcp.SelectedItem as GcpAccount;
    private const int MaxKeyFileBytes = 32 * 1024;

    private Border GcpCard()
    {
        var gv = new GridView();
        gv.Columns.Add(new GridViewColumn { Header = "Project", Width = 160, DisplayMemberBinding = new Binding("Name") });
        gv.Columns.Add(new GridViewColumn { Header = "Project id", Width = 150, DisplayMemberBinding = new Binding("ProjectId") });
        gv.Columns.Add(new GridViewColumn { Header = "Service account", Width = 230, DisplayMemberBinding = new Binding("ClientEmail") });
        gv.Columns.Add(new GridViewColumn { Header = "State", Width = 170, DisplayMemberBinding = new Binding("State") });
        _gcp.View = gv;
        RefreshGcp();
        var p = GcpProvider.Instance;
        var card = Card("Google Cloud projects",
            "The app signs in to Google Cloud as a service account with a JSON key file (IAM & Admin → Service accounts → Keys → Add key → JSON). Give the service account roles on the project " +
            "(Viewer to only look). A new project is read-only here until you allow changes: until then the app also asks Google for read-only access only. " +
            "The key is stored only in the encrypted settings and never shown again — delete the downloaded file afterwards.",
            NetUi.External("Internet access",
                "Google Cloud projects talk to oauth2.googleapis.com (sign-in) and the Google Cloud APIs (compute, container, run, sqladmin, dns, storage, cloudresourcemanager .googleapis.com) when you open Google Cloud screens, press Test, or include Google Cloud in the dashboard's checks. Nothing runs in the background; offline mode blocks it."),
            _gcp,
            Row(Btn("＋ Add project…", AddGcp, "PrimaryButton"), Btn("Test", async () => await TestGcp()), Btn("Make active", () => { if (SelGcp is { } a) { p.SetActive(a.Id); RefreshGcp(); } }),
                Btn("Rename…", () => { if (SelGcp is { } a && Dialogs.Prompt("Rename", "Name:", a.Name) is { Length: > 0 } n) { a.Name = n.Trim(); Save(); RefreshGcp(); } }),
                Btn("Replace key…", ReplaceGcp),
                Btn("Allow changes / read-only", ToggleGcpChanges),
                Btn("Disable / enable", () => { if (SelGcp is { } a) { a.Disabled = !a.Disabled; Save(); RefreshGcp(); p.AccountsChanged(); AuditLog.Write("you", "gcp:" + a.Name, a.Disabled ? "project disabled" : "project enabled"); } }),
                Btn("✕ Remove", RemoveGcp, "DangerButton")),
            new TextBlock { Text = "Available through the Google Cloud APIs in this app: Compute Engine VMs (start, stop, reset, suspend / resume, machine type, serial output), managed instance groups (resize), instance templates, custom and machine images, disks and snapshots, " +
                               "GKE clusters and node pools (size), Cloud Run services and revisions, Cloud Functions, Artifact Registry, Cloud SQL instances (start / stop / restart / back up now), databases and backups, Cloud Storage buckets (settings), " +
                               "VPC networks, subnets, firewall rules, routes, Cloud Routers / NAT, IP addresses, load balancer front ends, backend services, health checks, SSL certificates, VPN tunnels, Cloud DNS zones and records, Pub/Sub topics and subscriptions, regions, and a read-only API explorer. " +
                               "Not available: service-account keys, Secret Manager, Cloud SQL users, object and message contents, the serial console and IAM.",
                Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        Menus.ForList(_gcp, card, () => SelGcp is not { } a ? Array.Empty<object?>() : new object?[]
        {
            Menus.Item("Copy project id", () => Menus.Clip(a.ProjectId)),
            Menus.Item("Copy service account", () => Menus.Clip(a.ClientEmail)),
        });
        return card;
    }

    private void RefreshGcp() { _gcp.ItemsSource = null; _gcp.ItemsSource = _s.GcpAccounts; }

    /// <summary>The key file's text, checked to be a service-account key. Null when cancelled.</summary>
    private static (string Json, GcpAuth.KeyFile Key)? AskKeyFile(string title)
    {
        var dlg = new OpenFileDialog { Title = title + " — service-account key (JSON)", Filter = "Key file (*.json)|*.json|All files|*.*" };
        if (dlg.ShowDialog() != true) return null;
        if (new FileInfo(dlg.FileName).Length > MaxKeyFileBytes) { Dialogs.Error(title, "That file is too big for a service-account key."); return null; }
        var json = File.ReadAllText(dlg.FileName);
        try { return (json, GcpAuth.ParseKeyFile(json)); }
        catch (Cloud.CloudApiException ex) { Dialogs.Error(title, ex.Message); return null; }
    }

    private async void AddGcp()
    {
        if (!Restrictions.Check("cloud.add")) return;
        const string title = "Google Cloud project";
        if (AskKeyFile(title) is not { } k) return;
        if (Dialogs.Prompt(title, "Project id to manage (the key file's own project is suggested):", k.Key.ProjectId) is not { Length: > 0 } project) return;
        if (!System.Text.RegularExpressions.Regex.IsMatch(project.Trim(), "^[a-z][a-z0-9-]{4,28}[a-z0-9]$")) { Dialogs.Error(title, "A project id is 6-30 lower-case letters, digits and hyphens, starting with a letter."); return; }
        if (Dialogs.Prompt(title, "A name for this project (as you call it):", project.Trim()) is not { Length: > 0 } name) return;
        var acct = new GcpAccount { Name = name.Trim(), ProjectId = project.Trim(), ServiceAccountJson = k.Json, ClientEmail = k.Key.ClientEmail, ReadOnly = true };
        try
        {
            var what = await new GcpClient(acct).TestAsync();
            _s.GcpAccounts.Add(acct);
            _s.ActiveGcpAccountId ??= acct.Id;
            Save(); RefreshGcp(); GcpProvider.Instance.AccountsChanged();
            AuditLog.Write("you", "gcp:" + acct.Name, "project added", "read-only · " + acct.ClientEmail);
            Dialogs.Info("Google Cloud", $"Connected: {what}.\n\nThe project is read-only in this app until you press “Allow changes / read-only”. You can delete the downloaded key file now.");
        }
        catch (Exception ex) { GcpAuth.Forget(acct.Id); Dialogs.Error("Google Cloud", "Signing in didn't work, so nothing was saved:\n\n" + ex.Message); }
    }

    private async Task TestGcp()
    {
        if (SelGcp is not { } acct) return;
        try
        {
            GcpAuth.Forget(acct.Id);
            var what = await new GcpClient(acct).TestAsync();
            Dialogs.Info("Google Cloud", $"✓ Connected · {what}" +
                (acct.ReadOnly ? "\n\nRead-only in this app (and the sign-in asks Google for read-only access): changes are refused before they are sent." : "\n\nChanges are allowed in this app; Google still checks the service account's roles on each one."));
        }
        catch (Exception ex) { Dialogs.Error("Google Cloud", ex.Message); }
    }

    private async void ReplaceGcp()
    {
        if (!Restrictions.Check("cloud.edit")) return;
        if (SelGcp is not { } acct || AskKeyFile("Replace key") is not { } k) return;
        var test = new GcpAccount { Id = "replace-test", Name = acct.Name, ProjectId = acct.ProjectId, ServiceAccountJson = k.Json, ClientEmail = k.Key.ClientEmail, ReadOnly = true };
        try
        {
            await new GcpClient(test).TestAsync();
            GcpAuth.Forget(test.Id);
            acct.ServiceAccountJson = k.Json; acct.ClientEmail = k.Key.ClientEmail;
            GcpAuth.Forget(acct.Id);
            Save(); RefreshGcp(); GcpProvider.Instance.AccountsChanged();
            AuditLog.Write("you", "gcp:" + acct.Name, "key replaced", acct.ClientEmail);
        }
        catch (Exception ex) { GcpAuth.Forget(test.Id); Dialogs.Error("Google Cloud", "The new key didn't work (the old one is kept):\n\n" + ex.Message); }
    }

    private void ToggleGcpChanges()
    {
        if (!Restrictions.Check("cloud.edit")) return;
        if (SelGcp is not { } acct) return;
        if (acct.ReadOnly && !Dialogs.Confirm("Allow changes", $"Allow changes to “{acct.Name}” from this app? Each change still asks first, and Google still checks the service account's roles.", "Allow changes", danger: true)) return;
        acct.ReadOnly = !acct.ReadOnly;
        GcpAuth.Forget(acct.Id);
        Save(); RefreshGcp(); GcpProvider.Instance.AccountsChanged();
        AuditLog.Write("you", "gcp:" + acct.Name, acct.ReadOnly ? "project set read-only" : "changes allowed");
    }

    private void RemoveGcp()
    {
        if (!Restrictions.Check("cloud.edit")) return;
        if (SelGcp is not { } acct || !Dialogs.Confirm("Remove project", $"Remove “{acct.Name}” and its key from this app? (Nothing changes in Google Cloud. Delete the key in the service account too if you no longer need it.)", "Remove", danger: true)) return;
        _s.GcpAccounts.Remove(acct);
        GcpAuth.Forget(acct.Id);
        if (_s.ActiveGcpAccountId == acct.Id) _s.ActiveGcpAccountId = _s.GcpAccounts.FirstOrDefault()?.Id;
        Save(); RefreshGcp(); GcpProvider.Instance.AccountsChanged();
        AuditLog.Write("you", "gcp:" + acct.Name, "project removed");
    }
}
