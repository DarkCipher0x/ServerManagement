using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using DcxManager.Services;

namespace DcxManager.Views;

// SettingsView: the Backup & restore card (0.5.0, ADR-060). DarkCipher0x: "a proper backup and import option, including tickboxes
// of what to back up or import, under Data & backups … managed via GPO like every setting". The encrypted pick-what
// export and import (BackupDialogs) live here now, with scheduled backups and the rules an administrator sets.
public sealed partial class SettingsView
{
    private Border BackupCard()
    {
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) };
        var result = new TextBlock { TextWrapping = TextWrapping.Wrap, Style = (Style)FindResource("Caption") };
        void Show()
        {
            var s = _s;
            var last = s.BackupLastUtc is { } t ? $"Last backup: {t.ToLocalTime():yyyy-MM-dd HH:mm} (written and read back)." : "No backup written from this PC yet.";
            var sched = s.BackupScheduleDays <= 0 ? "Scheduled backups are off."
                : ScheduledBackup.Problem() is { } p ? $"⚠ Scheduled every {s.BackupScheduleDays} day(s), but not running: {p}."
                : $"Scheduled every {s.BackupScheduleDays} day(s) to {s.BackupFolder}, newest {s.BackupKeep} kept; next {ScheduledBackup.NextDueUtc()?.ToLocalTime():yyyy-MM-dd HH:mm}.";
            var err = s.BackupLastError.Length > 0 ? $"\n⚠ Last scheduled backup failed: {s.BackupLastError}" : "";
            var rules = (BackupPolicy.ExportBlocked() != null ? "\n⛔ Exports and backups are switched off." : "") + (BackupPolicy.ImportBlocked() != null ? "\n⛔ Imports are switched off." : "")
                      + (!BackupPolicy.SecretsAllowed ? "\n🔒 Exports leave out credentials, keys and cloud API keys." : "");
            status.Text = last + " " + sched + err + rules;
            status.SetResourceReference(TextBlock.ForegroundProperty, err.Length > 0 || sched.StartsWith("⚠") ? "Warn" : "Fg");
        }

        var backup = Btn("⤓ Back up now…", () => { if (BackupDialogs.ExportFull(Window.GetWindow(this))) { result.Text = "✓ Encrypted backup written and read back."; Show(); } }, "PrimaryButton");
        var restore = Btn("⤒ Restore or import…", () => { BackupDialogs.Import(Window.GetWindow(this)); Show(); });
        var other = Btn("⇩ Import from another app…", () => ConnectionImportDialog.Show(Window.GetWindow(this)));
        other.ToolTip = "mRemoteNG, SecureCRT, Royal TS, MobaXterm, PuTTY / KiTTY, WinSCP, Remote Desktop Connection Manager, .rdp files, Devolutions RDM — previewed with ticks, passwords straight into the vault.";
        backup.ToolTip = "A password-protected .dcxbackup (PBKDF2-SHA256, AES-256-GCM) of exactly what you tick: hosts by folder, credentials, cloud API keys, notes, incidents, reports, settings.";
        restore.ToolTip = "Opens a .dcxbackup, shows its contents as ticks, then merges (keep mine / take theirs / keep both) or replaces; a snapshot is taken first.";

        // Scheduled backups: how often, where, how many, and the file password (kept in the encrypted store only).
        int[] dayChoices = { 0, 1, 2, 7, 14, 30 };
        var every = Bound(new ComboBox { ItemsSource = dayChoices.Select(d => d == 0 ? "off" : d == 1 ? "every day" : $"every {d} days").ToList(),
            SelectedIndex = Math.Max(0, Array.IndexOf(dayChoices, _s.BackupScheduleDays)), ToolTip = "How often a scheduled encrypted backup is written to the folder below." }, nameof(AppSettings.BackupScheduleDays));
        if (!dayChoices.Contains(_s.BackupScheduleDays)) every.SelectedIndex = 0;
        var folder = Bound(new TextBox { Text = _s.BackupFolder, Width = FieldWidth, ToolTip = "A folder on another disk or a NAS share is best. A sync client's folder (OneDrive…) is allowed after a warning: the files are encrypted." }, nameof(AppSettings.BackupFolder));
        int[] keepChoices = { 3, 5, 7, 14, 30, 60 };
        var keep = Bound(new ComboBox { ItemsSource = keepChoices, SelectedItem = keepChoices.Contains(_s.BackupKeep) ? _s.BackupKeep : 7, ToolTip = "Scheduled backups kept; older ones are deleted only after a newer one was read back." }, nameof(AppSettings.BackupKeep));
        int[] lenChoices = { 12, 16, 20, 24, 32 };
        var minLen = Bound(new ComboBox { ItemsSource = lenChoices, SelectedItem = lenChoices.Contains(_s.BackupMinPasswordLength) ? _s.BackupMinPasswordLength : 12, ToolTip = "The shortest file password an export or backup accepts." }, nameof(AppSettings.BackupMinPasswordLength));
        bool loading = true;
        async Task Change(string reason, string key, JsonNode value, Action revert)
        {
            if (loading) return;
            bool ok = false;
            try { ok = await ProtectedSettings.ChangeAsync(reason, key, value); }
            catch (Exception ex) { Dialogs.Error("Backup & restore", ex.Message); }
            loading = true; if (!ok) revert(); loading = false;
            Show();
        }
        void RevertDays() => every.SelectedIndex = Math.Max(0, Array.IndexOf(dayChoices, _s.BackupScheduleDays));
        every.SelectionChanged += async (_, _) =>
        {
            if (loading) return;
            var days = dayChoices[Math.Max(0, every.SelectedIndex)];
            // 0.5.1 (DarkCipher0x): a schedule needs somewhere to go — choose the folder first, then both are approved together.
            if (days > 0 && _s.BackupFolder.Trim().Length == 0)
            {
                if (PickFolder() is not { } d) { loading = true; RevertDays(); loading = false; result.Text = "Choose a folder for the scheduled backups first — the schedule stays off."; return; }
                bool ok = false;
                try { ok = await ProtectedSettings.ChangeAsync("turn on scheduled backups", new Dictionary<string, JsonNode?> { ["backup.scheduleDays"] = JsonValue.Create(days), ["backup.folder"] = JsonValue.Create(d) }); }
                catch (Exception ex) { Dialogs.Error("Backup & restore", ex.Message); }
                loading = true; if (ok) folder.Text = _s.BackupFolder; else RevertDays(); loading = false;
                if (ok && !ScheduledBackup.HasPassword) SetPassword();
                Show();
                return;
            }
            await Change("change the backup schedule", "backup.scheduleDays", JsonValue.Create(days), RevertDays);
            if (days > 0 && _s.BackupScheduleDays == days && !ScheduledBackup.HasPassword) { SetPassword(); Show(); }
        };
        minLen.SelectionChanged += async (_, _) => { if (minLen.SelectedItem is int n) await Change("change the shortest export password", "backup.minPassword", JsonValue.Create(n), () => minLen.SelectedItem = _s.BackupMinPasswordLength); };
        keep.SelectionChanged += (_, _) => { if (!loading && keep.SelectedItem is int n) { _s.BackupKeep = n; Save(); Show(); } };
        // The folder picker with its checks (outside the store; a sync client's folder only after the warning), or null.
        string? PickFolder()
        {
            if (TestMode.Running) { Dialogs.RefusedInSelfTest("folder picker: scheduled backups"); return null; }
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Folder for scheduled backups" };
            if (dlg.ShowDialog(Window.GetWindow(this)) != true) return null;
            var d = dlg.FolderName;
            if (d.StartsWith(SecureStore.DataDir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) { Dialogs.Error("Backup & restore", "Pick a folder outside the store: a backup inside it is lost with it."); return null; }
            return StorageGuard.ConfirmDestination(d, "Scheduled backups", encrypted: true) ? d : null;
        }
        var browse = Btn("Browse…", async () =>
        {
            if (PickFolder() is not { } d) return;
            if (await ProtectedSettings.ChangeAsync("change the scheduled backup folder", "backup.folder", JsonValue.Create(d))) folder.Text = _s.BackupFolder;
            Show();
        });
        SetSetting(browse, nameof(AppSettings.BackupFolder));
        folder.LostFocus += async (_, _) =>
        {
            var d = folder.Text.Trim();
            if (d == _s.BackupFolder || !folder.IsEnabled) return;
            if (ProtectedSettings.PathCheck(d) is { } bad) { Dialogs.Error("Backup & restore", "Backup folder: " + bad + "."); folder.Text = _s.BackupFolder; return; }
            if (!await ProtectedSettings.ChangeAsync("change the scheduled backup folder", "backup.folder", JsonValue.Create(d))) folder.Text = _s.BackupFolder;
            Show();
        };
        var password = Btn(ScheduledBackup.HasPassword ? "Change backup password…" : "Set backup password…", () => SetPassword());
        void SetPassword()
        {
            var a = Dialogs.Prompt("Backup password", $"The password scheduled backups are encrypted with ({BackupPolicy.MinPassword}+ characters). You need it to restore one, on this or another PC. It is kept only in this encrypted store.", password: true);
            if (a == null) return;
            var b = Dialogs.Prompt("Backup password", "Again:", password: true);
            if (b == null) return;
            if (a != b) { Dialogs.Error("Backup password", "The passwords don't match."); return; }
            try { ScheduledBackup.SetPassword(a); AuditLog.Write("you", "store", "scheduled backup password set"); result.Text = "✓ Backup password saved (encrypted)."; }
            catch (ArgumentException ex) { Dialogs.Error("Backup password", ex.Message); }
            Show();
        }
        var now = Btn("Run a scheduled backup now", async () =>
        {
            result.Text = "writing…";
            var r = await Task.Run(() => ScheduledBackup.Run("manual"));
            result.Text = (r.Ok ? "✓ " : "✕ ") + r.Message;
            Show();
        });
        var open = Btn("Open folder", () =>
        {
            if (_s.BackupFolder.Length > 0 && Directory.Exists(_s.BackupFolder)) Process.Start(new ProcessStartInfo("explorer.exe", "\"" + _s.BackupFolder + "\"") { UseShellExecute = false });
        });
        loading = false;
        Show();
        Live.While(status, h => AppSettings.Saved += h, h => AppSettings.Saved -= h, Show);

        return Card("Backup & restore",
            "Encrypted backups you can restore on this or another PC: tick exactly what goes in, and on import tick exactly what comes back. " +
            "Files are password-protected (PBKDF2-SHA256 600,000 rounds + AES-256-GCM) and read back before they count. Snapshots (below) stay as the automatic local safety net.",
            status,
            Row(backup, restore, other),
            result,
            L("SCHEDULED BACKUPS"),
            Row(Labeled("HOW OFTEN", every), Labeled("KEEP", keep)),
            Row(Labeled("FOLDER", folder), browse, open),
            Row(password, now),
            L("RULES (an administrator's call: changes need approval, Group Policy can set them)"),
            ProtectedCheck("backup.allowExport", "Allow encrypted exports and backups"),
            ProtectedCheck("backup.allowImport", "Allow imports (backups and other apps' connection files)"),
            ProtectedCheck("backup.allowSecrets", "Exports and backups may carry credentials, SSH keys and cloud API keys"),
            ProtectedCheck("backup.scheduledSecrets", "Scheduled backups include credentials and keys"),
            Row(Labeled("SHORTEST FILE PASSWORD", minLen)));
    }
}
