using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using DcxManager.Services;
using DcxManager.Views;
using Microsoft.Win32;

namespace DcxManager.Vultr;

/// <summary>Vultr as a <see cref="CloudProvider"/> (ADR-020): its catalog, accounts and the Vultr-only actions (web
/// console, bare-metal VNC, kubeconfig, startup scripts, user data, key upload, invoices).</summary>
public sealed class VultrProvider : CloudProvider
{
    public static VultrProvider Instance { get; } = new();
    private VultrProvider() { }

    public override string Id => "vultr";
    public override string Title => "Vultr";
    public override ResourceDef[] Catalog => VultrCatalog.All;
    public override string[] GroupOrder => VultrCatalog.GroupOrder;
    public override ResourceDef InstanceDef => VultrCatalog.Instance;
    public override CloudCaps Caps => CloudCaps.Create | CloudCaps.Delete | CloudCaps.Start | CloudCaps.Stop | CloudCaps.Reboot | CloudCaps.Reinstall
        | CloudCaps.Resize | CloudCaps.Rename | CloudCaps.Snapshots | CloudCaps.Images | CloudCaps.CustomImages | CloudCaps.Console | CloudCaps.SshKeys
        | CloudCaps.PrivateNetworks | CloudCaps.Firewalls | CloudCaps.Dns | CloudCaps.Backups;
    public override string[] ApiHosts => new[] { "api.vultr.com" };
    public override string ConnectHelp =>
        "Connect a Vultr account:\n\n1. my.vultr.com → Account → API → Enable API\n2. Copy the Personal Access Token\n3. Add this PC's public IP under Access Control\n4. Settings → Vultr accounts → Add\n\nThe key is stored in the encrypted store.";

    public override string ReferenceLabel => "◍  Regions, plans & images";
    public override IEnumerable<(string Key, string Text, string? Tip)> ExtraNav => new (string, string, string?)[]
    {
        ("apiexplorer", "{ }  API explorer", "Call any Vultr API endpoint"),
        ("portal", "⧉  Vultr portal", "my.vultr.com in an app window (sign in there)"),
    };

    public override IReadOnlyList<ICloudAccount> Accounts => AppSettings.Current.VultrAccounts;
    public override string? ActiveAccountId { get => AppSettings.Current.ActiveVultrAccountId; set => AppSettings.Current.ActiveVultrAccountId = value; }
    protected override ICloudApi CreateApi(ICloudAccount account) => new VultrClient((VultrAccount)account);

    public override CloudInstance? Normalize(JsonElement r, string accountId)
    {
        var id = J.Str(r, "id");
        if (id.Length == 0) return null;
        var power = J.Str(r, "power_status"); var status = J.Str(r, "status");
        var state = status == "pending" ? CloudState.Busy : power switch { "running" => CloudState.Running, "stopped" => CloudState.Stopped, _ => CloudState.Other };
        var ip = J.Str(r, "main_ip");
        double.TryParse(J.Str(r, "ram"), out var ram);
        int.TryParse(J.Str(r, "vcpu_count") is { Length: > 0 } v ? v : J.Str(r, "cpu_count"), out var cpu);
        return new CloudInstance("vultr", accountId, id, J.Str(r, "label") is { Length: > 0 } l ? l : ip, ip == "0.0.0.0" ? "" : ip, J.Str(r, "v6_main_ip"),
            J.Str(r, "region"), J.Str(r, "plan"), J.Str(r, "os"), state, power.Length > 0 ? $"{status}/{power}" : status, cpu, ram, "root", r);
    }

    public override Task PowerAsync(ICloudApi api, string instanceId, CloudPower action, CancellationToken ct = default, string actor = "you") =>
        api.SendAsync(System.Net.Http.HttpMethod.Post, $"/instances/{Uri.EscapeDataString(instanceId)}/{action switch { CloudPower.Start => "start", CloudPower.Stop => "halt", _ => "reboot" }}", null, ct, actor);

    public override async Task<string?> AccountSummaryAsync(ICloudApi api, CancellationToken ct = default)
    {
        if (api is not VultrClient v) return null;
        var acct = await v.AccountAsync(ct);
        var bal = J.Get(acct, "balance"); var pend = J.Get(acct, "pending_charges");
        // Vultr reports credit as a negative balance.
        double b = bal.ValueKind == JsonValueKind.Number ? bal.GetDouble() : 0, p = pend.ValueKind == JsonValueKind.Number ? pend.GetDouble() : 0;
        return (b <= 0 ? $"${-b:0.00} credit" : $"${b:0.00} due") + $" · ${p:0.00} pending this month";
    }

    public override async Task<string?> ConsoleUrlAsync(ICloudApi api, string instanceId, CancellationToken ct = default)
    {
        var r = await api.GetItemAsync($"/instances/{Uri.EscapeDataString(instanceId)}", ct);
        return J.Str(r, "kvm") is { Length: > 0 } kvm ? kvm : null;
    }

    public override async Task<bool> RunSpecialAsync(ActionDef a, CloudViewContext c)
    {
        var row = c.Row;
        string Name() => row == null ? "" : row[c.Def.NameField] is { Length: > 0 } n ? n : row[c.Def.IdField];
        string ItemPath() => Tpl.Fill(a.Path, row?.Raw, c.Parent, url: true);
        switch (a.Special)
        {
            case "console":
            {
                if (await ConsoleUrlAsync(c.Api, row!["id"]) is not { } kvm) { Ui.Dialogs.Info("Console", "Vultr didn't return a console address."); return true; }
                AuditLog.Write("you", "vultr:" + c.Api.AccountName, $"opened web console for {Name()}");
                Ui.Window.OpenWeb($"Console — {Name()}", kvm);
                return true;
            }
            case "vnc":
            {
                var r = await c.Api.GetAsync(ItemPath());
                var url = J.Str(r, "vnc.url");
                if (url.Length == 0) { Ui.Dialogs.Info("Console", "No VNC address returned."); return true; }
                Ui.Window.OpenWeb($"Console — {Name()}", url);
                return true;
            }
            case "kubeconfig":
            {
                var r = await c.Api.GetAsync(ItemPath());
                var cfg = Encoding.UTF8.GetString(Convert.FromBase64String(J.Str(r, "kube_config")));
                var dlg = new SaveFileDialog { FileName = $"{Name()}-kubeconfig.yaml", Filter = "YAML|*.yaml;*.yml|All files|*.*" };
                if (dlg.ShowDialog(c.Owner) == true)
                {
                    await System.IO.File.WriteAllTextAsync(dlg.FileName, cfg);
                    AuditLog.Write("you", "vultr:" + c.Api.AccountName, $"downloaded kubeconfig for {Name()}");
                }
                return true;
            }
            case "script":
            {
                var r = await c.Api.GetAsync(ItemPath());
                Ui.Dialogs.ShowText($"Startup script — {Name()}", Decode(J.Str(r, "startup_script.script")));
                return true;
            }
            case "userdata":
            {
                var r = await c.Api.GetAsync(ItemPath());
                Ui.Dialogs.ShowText($"User data — {Name()}", Decode(J.Str(r, "user_data.data")) is { Length: > 0 } t ? t : "(none)");
                return true;
            }
            case "uploadkey":
            {
                var keys = AppData.Current.Credentials.Where(k => k.Kind == CredentialKind.PrivateKey).ToList();
                if (keys.Count == 0) { Ui.Dialogs.Info("Upload", "No keys in the vault yet (Credentials → Add)."); return true; }
                if (Ui.Dialogs.Pick("Upload SSH key", "Which key's public half should be added to Vultr?", keys, k => $"{k.Name}   {k.KeyType}   {k.Fingerprint}") is { } key)
                {
                    await UploadPublicKeyAsync(key);
                    await c.RefreshAsync();
                }
                return true;
            }
        }
        return false;
    }

    /// <summary>Adds a vault key's public half to the active account's SSH keys and says how it went (the Credentials
    /// screen's "Upload public key to Vultr" uses it too).</summary>
    public static async Task UploadPublicKeyAsync(Credential c)
    {
        try
        {
            var v = VultrClient.Require();
            await v.PostAsync("/ssh-keys", new { name = c.Name, ssh_key = c.PublicKey });
            Ui.Dialogs.Info("Vultr", $"“{c.Name}” was added to Vultr SSH keys on {v.AccountName}. New instances can now be created with it.");
        }
        catch (Exception ex) { Ui.Dialogs.Error("Vultr", ex.Message); }
    }

    private static string Decode(string b64)
    {
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(b64)); } catch { return b64; }
    }

    // ---- invoices and billing (Vultr's API has no PDFs: readable documents from the line items) ---------------------------

    public override IEnumerable<CloudExtra> ToolbarExtras(ResourceDef def)
    {
        if (def == VultrCatalog.Invoices)
            yield return new CloudExtra("📄 Open invoice", OpenInvoiceAsync, "Printable invoice from the API line items (save as PDF, review with AI)", Primary: true);
        if (def.Group == "Account")
            yield return new CloudExtra("↗ Billing portal", _ => { Ui.Window.OpenWeb("Vultr billing", VultrDocuments.PortalBilling, "portal-billing"); return Task.CompletedTask; }, "The official PDF invoices are in the Vultr customer portal");
    }

    public override IEnumerable<object?> RowMenuExtras(ResourceDef def, CloudViewContext c)
    {
        if (def != VultrCatalog.Invoices || Assist.Current is not { } assist) yield break;   // AI items only with an assistant module
        var ai = assist.WhyNot();
        yield return Menus.AsyncItem("✦ Review invoice with AI", () => ReviewInvoiceAsync(c), enabled: ai == null);
        yield return Menus.AsyncItem(c.Selected.Count == 2 ? "✦ Compare these two invoices with AI" : "✦ Compare with the previous invoice (AI)", () => CompareInvoicesAsync(c), enabled: ai == null);
    }

    public override async Task<bool> DoubleClickAsync(CloudViewContext c)
    {
        if (c.Def != VultrCatalog.Invoices) return false;
        await OpenInvoiceAsync(c);
        return true;
    }

    private static async Task OpenInvoiceAsync(CloudViewContext c)
    {
        if (c.Row is not { } row || c.Api is not VultrClient v) { c.Status("Select an invoice first."); return; }
        var id = row[c.Def.IdField];
        c.Status($"loading invoice {id}…");
        try { Ui.Window.ShowDocument(await VultrDocuments.InvoiceDocumentAsync(v, id)); c.Status(""); }
        catch (Exception ex) { c.Status("✕ " + ex.Message.Split('\n')[0]); Ui.Dialogs.Error("Invoice", ex.Message); }
    }

    private static async Task ReviewInvoiceAsync(CloudViewContext c)
    {
        if (c.Row is not { } row || c.Api is not VultrClient v || Assist.Current is not { } assist) return;
        try
        {
            var doc = await VultrDocuments.InvoiceDocumentAsync(v, row[c.Def.IdField]);
            await assist.AskAsync(doc.Title, doc.AiText!, doc.AiTask);
        }
        catch (Exception ex) { Ui.Dialogs.Error("Invoice", ex.Message); }
    }

    private static async Task CompareInvoicesAsync(CloudViewContext c)
    {
        if (c.Row == null || c.Api is not VultrClient v) return;
        string newer, older;
        try
        {
            if (c.Selected.Count == 2)
            {
                var o = c.Selected.OrderByDescending(r => r["date"]).ToList();
                newer = o[0][c.Def.IdField]; older = o[1][c.Def.IdField];
            }
            else
            {
                var all = (await v.ListAsync(c.Def.Path, c.Def.Key)).Select(e => new JRow(e)).OrderByDescending(r => r["date"]).ToList();
                var i = all.FindIndex(r => r[c.Def.IdField] == c.Row[c.Def.IdField]);
                if (i < 0 || i + 1 >= all.Count) { Ui.Dialogs.Info("Compare", "There is no earlier invoice to compare with."); return; }
                newer = all[i][c.Def.IdField]; older = all[i + 1][c.Def.IdField];
            }
            await VultrDocuments.CompareAsync(v, newer, older);
        }
        catch (Exception ex) { Ui.Dialogs.Error("Compare invoices", ex.Message); }
    }
}
