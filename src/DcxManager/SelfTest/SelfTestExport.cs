using System.IO;
using System.Windows;
using DcxManager.Services;
using DcxManager.Views;

namespace DcxManager;

/// <summary>
/// 0.3.0 preview.4 (§11): item-by-item encrypted export and import. A tick tree built off screen picks hosts, credentials
/// and cloud accounts; the file holds only those (plus the jump hosts the picked hosts need), an unticked credential is
/// removed from the hosts using it, the import tree filters a read file the same way, and the audit line carries counts
/// only. Demo data with TEST-NET addresses; nothing is shown.
/// </summary>
public static class ExportTests
{
    public static Task RunAsync(MainWindow main, string reportDir, Action<string> ok, Action<string, Exception?> fail, Action<Window, string> snap)
    {
        var d = AppData.Current;
        var s = AppSettings.Current;
        var keep = new Credential { Name = "export-keep", Kind = CredentialKind.Password, Username = "ops", Secret = "keep-SECRET-4411" };
        var drop = new Credential { Name = "export-drop", Kind = CredentialKind.Password, Username = "root", Secret = "drop-SECRET-9921" };
        var bastion = new HostEntry { Name = "export-bastion", Address = "192.0.2.10", Group = "Export/Edge", CredentialId = keep.Id };
        var web = new HostEntry { Name = "export-web", Address = "192.0.2.11", Group = "Export/Web", CredentialId = keep.Id, SudoCredentialId = drop.Id, JumpHostId = bastion.Id };
        var other = new HostEntry { Name = "export-other", Address = "198.51.100.12", Group = "Export/Web", CredentialId = drop.Id };
        var tunnel = new TunnelDef { Name = "export-tunnel", HostId = web.Id, BindPort = 15432, DestPort = 5432 };
        var vKeep = new VultrAccount { Name = "export-vultr-keep", ApiKey = "VULTRKEEP0000000000000000000000000001" };
        var vDrop = new VultrAccount { Name = "export-vultr-drop", ApiKey = "VULTRDROP0000000000000000000000000002" };
        var path = Path.Combine(reportDir, "selftest-custom.dcxbackup");
        try
        {
            d.Credentials.AddRange([keep, drop]); d.Hosts.AddRange([bastion, web, other]); d.Tunnels.Add(tunnel);
            s.VultrAccounts.AddRange([vKeep, vDrop]);

            // The tree over this store, everything ticked; then pick web (not bastion), one credential, one account.
            var tree = BackupPickTree.ForStore(Backup.Choice.Everything());
            if (tree.Choice.Hosts.Count != d.Hosts.Count || !tree.Choice.AppSettings || tree.Choice.Reports) throw new Exception("Everything() doesn't tick every host + settings (and not reports)");
            tree.SetAll(false);
            if (tree.Choice.Count != 0 || !tree.SummaryText.StartsWith("Nothing")) throw new Exception("Select none left ticks: " + tree.SummaryText);
            tree.Tick("host", web.Id, true); tree.Tick("cred", keep.Id, true); tree.Tick("acct:vultr", vKeep.Id, true); tree.Tick("tunnel", tunnel.Id, true);
            var c = tree.Choice;
            if (c.Hosts.Count != 1 || c.Credentials.Count != 1 || c.Accounts["vultr"].Count != 1 || c.AppSettings) throw new Exception("ticks didn't map to the choice");
            if (tree.SummaryText.Contains("SECRET") || tree.SummaryText.Contains("VULTRKEEP") || !tree.SummaryText.Contains("1 cloud API key")) throw new Exception("summary line: " + tree.SummaryText);

            var r = Backup.Custom(c, "selftest");
            var f = r.File;
            if (f.Scope != "custom" || f.AppSettingsIncluded != false) throw new Exception("scope / settings flag");
            if (f.Data!.Hosts.Count != 2 || f.Data.FindHost(bastion.Id) == null) throw new Exception("jump host not pulled in");
            if (!r.AddedJumpHosts.Contains(bastion.Display)) throw new Exception("jump host not reported");
            if (f.Data.Credentials.Count != 1 || f.Data.Credentials[0].Id != keep.Id) throw new Exception("credentials: " + f.Data.Credentials.Count);
            if (f.Data.Hosts.Single(h => h.Id == web.Id).SudoCredentialId != null) throw new Exception("unticked sudo credential still referenced");
            if (!r.StrippedCredentials.Any(x => x.Contains("export-web"))) throw new Exception("stripped credential not reported");
            if (d.FindHost(web.Id)!.SudoCredentialId != drop.Id) throw new Exception("the export changed the live host");
            if (f.Settings!.VultrAccounts.Count != 1 || f.Settings.VultrAccounts[0].Id != vKeep.Id) throw new Exception("accounts not filtered");
            if (f.Data.Tunnels.Count != 1 || f.Notes != null || f.Reports != null) throw new Exception("tunnel / notes / reports");
            if (f.Data.Folders.Any(x => !x.StartsWith("Export", StringComparison.OrdinalIgnoreCase))) throw new Exception("unrelated folders: " + string.Join(", ", f.Data.Folders));
            if (r.Secrets != (1, 1)) throw new Exception("secret counts " + r.Secrets);

            // Encrypted on disk, read back, then the import tree filters the read file the same way.
            Backup.Write(path, "custom-export-pw-1", f);
            var raw = File.ReadAllBytes(path);
            var text = System.Text.Encoding.UTF8.GetString(raw);
            if (text.Contains("keep-SECRET") || text.Contains("VULTRKEEP") || text.Contains("export-web")) throw new Exception("plaintext in the file");
            var back = Backup.Read(path, "custom-export-pw-1");
            var importTree = BackupPickTree.ForFile(back);
            var all = importTree.Choice;
            if (all.Hosts.Count != 2 || all.Credentials.Count != 1 || all.AppSettings) throw new Exception("import tree doesn't start with the file's items (and no app settings)");
            importTree.Tick("cred", keep.Id, false);
            importTree.Tick("host", bastion.Id, false);
            var pick = Backup.Filter(back, importTree.Choice);
            if (pick.File.Data!.Hosts.Count != 2) throw new Exception("import filter dropped the jump host the picked host needs");
            if (pick.File.Data.Credentials.Count != 0 || pick.File.Data.Hosts.Any(h => h.CredentialId != null)) throw new Exception("import filter kept an unticked credential");

            // Merge of the filtered file into a store that has the hosts: skipped, nothing duplicated, settings untouched.
            int before = d.Hosts.Count, accts = s.VultrAccounts.Count;
            var m = Backup.Merge(pick.File, Backup.Conflict.Skip);
            if (m.HostsSkipped != 2 || d.Hosts.Count != before || s.VultrAccounts.Count != accts) throw new Exception("merge: " + m);
            ok("export/import (§11): tick tree → only picked items, jump host pulled in, unticked credential stripped, accounts filtered, encrypted, import tree filters, counts-only summary");
        }
        catch (Exception ex) { fail("export/import (§11)", ex); }
        finally
        {
            d.Hosts.RemoveAll(h => h.Name.StartsWith("export-")); d.Credentials.RemoveAll(x => x.Name.StartsWith("export-"));
            d.Tunnels.RemoveAll(t => t.Name == "export-tunnel"); d.Folders.RemoveAll(x => x.StartsWith("Export", StringComparison.OrdinalIgnoreCase));
            s.VultrAccounts.RemoveAll(a => a.Name.StartsWith("export-vultr"));
            AppData.Save(); AppSettings.Save();
            try { File.Delete(path); } catch { }
        }
        return Task.CompletedTask;
    }
}
