using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using DcxManager.Services;
using Microsoft.Win32;

namespace DcxManager.Views;

/// <summary>Export / import of password-encrypted .dcxbackup files: a selection (folder, hosts) or everything.</summary>
public static class BackupDialogs
{
    private static TextBlock L(string t) => new() { Text = t, Style = (Style)Application.Current.Resources["Label"], Margin = new Thickness(0, 10, 0, 4) };

    /// <summary>Asks what to include with a selection of hosts + a password (twice). Null = cancelled. (Exports of
    /// everything pick item by item in <see cref="ExportFull"/>.)</summary>
    private static (Backup.Include Inc, string Password)? Ask(Window? owner, string title, string intro)
    {
        var w = Dialogs.MakeWindow(title, 520);
        w.Owner = owner;
        var p = new StackPanel { Margin = new Thickness(18) };
        p.Children.Add(new TextBlock { Text = intro, TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["Hint"] });
        CheckBox C(string t, bool v) { var c = new CheckBox { Content = t, IsChecked = v, Margin = new Thickness(0, 6, 0, 0) }; p.Children.Add(c); return c; }
        p.Children.Add(L("INCLUDE"));
        var creds = C("Credentials the hosts use (passwords, SSH keys)", BackupPolicy.SecretsAllowed);
        if (!BackupPolicy.SecretsAllowed) { creds.IsEnabled = false; creds.ToolTip = NoSecrets; }
        var tunnels = C("Tunnels", true);
        var known = C("Trusted host keys (known hosts)", true);
        var snippets = C("Snippets", false);
        p.Children.Add(L($"FILE PASSWORD ({BackupPolicy.MinPassword}+ characters — needed to import it)"));
        var a = new PasswordBox(); var b = new PasswordBox();
        p.Children.Add(a);
        p.Children.Add(L("AGAIN"));
        p.Children.Add(b);
        p.Children.Add(new TextBlock { Text = "PBKDF2-SHA256 (600,000 rounds) + AES-256-GCM. There is no way to open the file without this password.", Style = (Style)Application.Current.Resources["Hint"], TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
        var ok = new Button { Content = "Export…", Style = (Style)Application.Current.Resources["PrimaryButton"], IsDefault = true };
        var bs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        bs.Children.Add(ok); bs.Children.Add(new Button { Content = "Cancel", IsCancel = true });
        p.Children.Add(bs);
        w.Content = p;
        ok.Click += (_, _) =>
        {
            if (BackupPolicy.PasswordProblem(a.Password) is { } weak) { Dialogs.Error(title, weak); return; }
            if (a.Password != b.Password) { Dialogs.Error(title, "The passwords don't match."); return; }
            w.DialogResult = true;
        };
        w.Loaded += (_, _) => a.Focus();
        if (w.ShowDialog() != true) return null;
        return (new Backup.Include(creds.IsChecked == true, snippets.IsChecked == true, tunnels.IsChecked == true, known.IsChecked == true, Settings: false, Reports: false), a.Password);
    }

    private static string? SavePath(Window? owner, string name)
    {
        var dlg = new SaveFileDialog { FileName = name, Filter = "DarkCipher encrypted backup|*.dcxbackup", DefaultExt = ".dcxbackup" };
        if (dlg.ShowDialog(owner) != true) return null;
        // An explicit backup into OneDrive/a NAS is the user's call — say so, then allow it.
        return Services.StorageGuard.ConfirmDestination(System.IO.Path.GetDirectoryName(dlg.FileName)!, "Backup", encrypted: true) ? dlg.FileName : null;
    }

    private static string Slug(string s) => string.Concat(s.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-')).Trim('-');

    private const string NoSecrets = "Exports may not carry credentials, keys or cloud API keys on this PC (Settings → Data & backups, or your organisation's policy).";

    public static void ExportSelection(Window? owner, List<HostEntry> hosts, string what)
    {
        if (BackupPolicy.ExportBlocked() is { } no) { Dialogs.Error("Export", no); return; }
        if (hosts.Count == 0) { Dialogs.Info("Export", "Nothing selected."); return; }
        if (!SensitiveAction.Confirm($"export {what}", owner)) return;
        var ans = Ask(owner, "Export " + what, $"Export {hosts.Count} host(s) from {what}, keeping the folder structure. Jump hosts they connect through are included automatically.");
        if (ans is not { } x) return;
        if (SavePath(owner, $"darkcipher-{Slug(what)}-{DateTime.Now:yyyyMMdd}.dcxbackup") is not { } path) return;
        try
        {
            var b = Backup.Selection(hosts, x.Inc, what);
            Backup.Write(path, x.Password, b);
            Verify(path, x.Password);
            AuditLog.Write("you", "store", "encrypted export", $"{what}: {b.Data?.Hosts.Count} hosts, {b.Data?.Credentials.Count} credentials");
            Dialogs.Info("Export", $"Encrypted and verified.\n\n{b.Summary}\n\n{path}");
        }
        catch (Exception ex) { Dialogs.Error("Export", ex.Message); }
    }

    /// <summary>Encrypted export of exactly the ticked items (0.3.0 §11) — everything is ticked to start with, except
    /// reports. Returns true only when the file was written AND read back with the password.</summary>
    public static bool ExportFull(Window? owner, string? reason = null)
    {
        if (BackupPolicy.ExportBlocked() is { } no) { Dialogs.Error("Encrypted export", no); return false; }
        if (!SensitiveAction.Confirm("export secrets, credentials and hosts to an encrypted file", owner)) return false;
        const string title = "Encrypted export";
        var tree = BackupPickTree.ForStore(Backup.Choice.Everything());
        if (!BackupPolicy.SecretsAllowed) tree.LockSecrets(NoSecrets);
        var w = Dialogs.MakeWindow(title, 640, 720);
        w.Owner = owner;
        var root = new DockPanel { Margin = new Thickness(18) };
        var intro = new TextBlock
        {
            Text = (reason != null ? reason + "\n\n" : "") + "Tick what goes into the file: hosts by folder, credentials, cloud API keys, notes, incidents and the rest. " +
                   "A host's jump hosts come along automatically; a credential you leave out is removed from the hosts that use it.",
            TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["Hint"], Margin = new Thickness(0, 0, 0, 8),
        };
        DockPanel.SetDock(intro, Dock.Top); root.Children.Add(intro);
        var bottom = new StackPanel();
        bottom.Children.Add(L($"FILE PASSWORD ({BackupPolicy.MinPassword}+ characters — needed to import it)"));
        var a = new PasswordBox(); var b2 = new PasswordBox();
        bottom.Children.Add(a); bottom.Children.Add(L("AGAIN")); bottom.Children.Add(b2);
        bottom.Children.Add(new TextBlock { Text = "PBKDF2-SHA256 (600,000 rounds) + AES-256-GCM. There is no way to open the file without this password.", Style = (Style)Application.Current.Resources["Hint"], TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
        var ok = new Button { Content = "Export…", Style = (Style)Application.Current.Resources["PrimaryButton"], IsDefault = true };
        var bs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        bs.Children.Add(ok); bs.Children.Add(new Button { Content = "Cancel", IsCancel = true });
        bottom.Children.Add(bs);
        DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        root.Children.Add(tree);
        w.Content = root;
        tree.Changed += () => ok.IsEnabled = tree.Choice.Count > 0;
        ok.Click += (_, _) =>
        {
            if (BackupPolicy.PasswordProblem(a.Password) is { } weak) { Dialogs.Error(title, weak); return; }
            if (a.Password != b2.Password) { Dialogs.Error(title, "The passwords don't match."); return; }
            w.DialogResult = true;
        };
        if (w.ShowDialog() != true) return false;
        var choice = tree.Choice;
        var pw = a.Password;
        if (SavePath(owner, $"darkcipher-export-{DateTime.Now:yyyyMMdd}.dcxbackup") is not { } path) return false;
        try
        {
            var r = Backup.Custom(choice);
            Backup.Write(path, pw, r.File);
            Verify(path, pw);
            var (credCount, cloudKeys) = r.Secrets;
            AppSettings.Current.BackupLastUtc = DateTime.UtcNow; try { AppSettings.Save(); } catch (Exception ex) { Log.Write("Backup state", ex); }
            // Counts only: never a name, address or secret in the audit trail.
            AuditLog.Write("you", "store", "encrypted export written", $"{choice.Count} ticked, {r.File.Data?.Hosts.Count ?? 0} hosts, {credCount} credentials, {cloudKeys} cloud keys");
            Dialogs.Info(title, $"Encrypted and verified.\n\n{r.File.Summary}{Extras(r)}\n\n{path}");
            return true;
        }
        catch (Exception ex) { Dialogs.Error(title, ex.Message); return false; }
    }

    /// <summary>What the export or import did beyond the ticks (jump hosts pulled in, credentials left out).</summary>
    private static string Extras(Backup.CustomResult r) =>
        (r.AddedJumpHosts.Count > 0 ? "\n\nAdded because the ticked hosts connect through them: " + string.Join(", ", r.AddedJumpHosts) : "") +
        (r.StrippedCredentials.Count > 0 ? "\n\nNo credential (not ticked) for: " + string.Join(", ", r.StrippedCredentials.Take(12)) + (r.StrippedCredentials.Count > 12 ? $" and {r.StrippedCredentials.Count - 12} more" : "") : "");

    /// <summary>Re-reads the written file with the password so a typo or a bad disk is caught now, not at restore time.</summary>
    private static void Verify(string path, string pw)
    {
        try { Backup.Read(path, pw); }
        catch (Exception ex) { throw new IOException("The file was written but could not be read back — don't rely on it. " + ex.Message); }
    }

    public static void Import(Window? owner, string? intoFolder = null)
    {
        if (BackupPolicy.ImportBlocked() is { } no) { Dialogs.Error("Import", no); return; }
        var dlg = new OpenFileDialog { Filter = "DarkCipher encrypted backup|*.dcxbackup|All files|*.*" };
        if (dlg.ShowDialog(owner) != true) return;
        if (Dialogs.Prompt("Import", "File password:", password: true) is not { } pw) return;
        Backup.BackupFile b;
        try { b = Backup.Read(dlg.FileName, pw); }
        catch (CryptographicException) { Dialogs.Error("Import", "Wrong password, or the file is damaged."); return; }
        catch (Exception ex) { Dialogs.Error("Import", ex.Message); return; }

        // Pick what comes in (0.3.0 §11): the file's contents as a tick tree, everything ticked.
        var tree = BackupPickTree.ForFile(b);
        var w = Dialogs.MakeWindow("Import", 640, 760);
        w.Owner = owner;
        var root = new DockPanel { Margin = new Thickness(18) };
        var head = new TextBlock { Text = b.Summary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(head, Dock.Top); root.Children.Add(head);
        var p = new StackPanel();
        DockPanel.SetDock(p, Dock.Bottom); root.Children.Add(p);
        root.Children.Add(tree);
        p.Children.Add(L("HOW"));
        RadioButton R(string t, bool on) { var r = new RadioButton { Content = new TextBlock { Text = t, TextWrapping = TextWrapping.Wrap }, IsChecked = on, GroupName = "how", Margin = new Thickness(0, 6, 0, 0) }; p.Children.Add(r); return r; }
        var skip = R("Merge — keep my version when a host already exists", true);
        var replace = R("Merge — overwrite my version with the file's", false);
        var both = R("Merge — keep both (imported copies are renamed)", false);
        var all = b.Scope != "selection" ? R("Replace EVERYTHING here with the ticked items (hosts, credentials" + (b.Settings != null ? ", settings" : "") + ")", false) : null;
        CheckBox? into = null;
        if (intoFolder != null) { into = new CheckBox { Content = $"Put the imported folders inside “{intoFolder}”", IsChecked = true, Margin = new Thickness(0, 12, 0, 0) }; p.Children.Add(into); }
        var ok = new Button { Content = "Import", Style = (Style)Application.Current.Resources["PrimaryButton"], IsDefault = true };
        var bs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        bs.Children.Add(ok); bs.Children.Add(new Button { Content = "Cancel", IsCancel = true });
        p.Children.Add(bs);
        w.Content = root;
        tree.Changed += () => ok.IsEnabled = tree.Choice.Count > 0;
        ok.Click += (_, _) => w.DialogResult = true;
        if (w.ShowDialog() != true) return;
        var picked = Backup.Filter(b, tree.Choice);
        b = picked.File;
        var applySettings = tree.Choice.AppSettings && b.Settings != null && b.AppSettingsIncluded != false;
        if (picked.StrippedCredentials.Count > 0 || picked.AddedJumpHosts.Count > 0)
            Dialogs.Info("Import", "Before importing:" + Extras(picked));

        // A verified local snapshot of the current state first (ADR-009), so any import can be undone from Settings → Snapshots.
        bool SafetySnapshot()
        {
            var s = Snapshots.Create("pre-import");
            return s.Ok || Dialogs.Confirm("Import", "The safety snapshot of your current data failed:\n" + s.Message + "\n\nImport anyway, without a way back?", "Import anyway", danger: true);
        }
        try
        {
            if (all?.IsChecked == true)
            {
                if (!Dialogs.Confirm("Replace everything", "Replace ALL hosts, credentials" + (b.Settings != null ? " and settings" : "") + " with the backup?\n\nA snapshot of the current state is taken first (Settings → Snapshots can put it back).", "Replace", danger: true, typeToConfirm: "REPLACE")) return;
                if (!SafetySnapshot()) return;
                Backup.ReplaceAll(b);
                AuditLog.Write("you", "store", "restored from encrypted backup (replace)");
                Dialogs.Info("Import", "Replaced. Restart the app to reload every screen.");
                return;
            }
            var mode = replace.IsChecked == true ? Backup.Conflict.Replace : both.IsChecked == true ? Backup.Conflict.KeepBoth : Backup.Conflict.Skip;
            if (!SafetySnapshot()) return;
            var r = Backup.Merge(b, mode, into?.IsChecked == true ? intoFolder : null);
            if (applySettings) Backup.ApplySettings(b.Settings!);
            AuditLog.Write("you", "store", "imported encrypted file (merge)", $"+{r.HostsAdded} hosts, {r.HostsReplaced} replaced, {r.HostsSkipped} skipped, +{r.CredsAdded} credentials" + (applySettings ? ", app settings applied" : ""));
            Dialogs.Info("Import", $"{r.HostsAdded} host(s) added, {r.HostsReplaced} replaced, {r.HostsSkipped} skipped (already here).\n{r.CredsAdded} credential(s) added, {r.Other} other item(s)." + (applySettings ? "\nApp settings applied (admin-protected and Group Policy settings stay as they are). Restart to reload every screen." : ""));
        }
        catch (Exception ex) { Dialogs.Error("Import", ex.Message); }
    }
}
