using System.IO;
using System.Security.Cryptography;

namespace DcxManager.Services;

/// <summary>
/// What may leave the store and what may come in (0.5.0, ADR-060). Enforced here, below every screen: the export and
/// import code calls it, so no dialog, add-on or local API path can skip it. The rules are settings (Group Policy and admin
/// protection decide them): exports allowed, imports allowed, secrets in exports allowed, the shortest file password.
/// </summary>
public static class BackupPolicy
{
    /// <summary>Why an export can't happen now, or null.</summary>
    public static string? ExportBlocked() =>
        AppSettings.Current.BackupAllowExport ? null : "Exports and backups are switched off on this PC (Settings → Data & backups, or your organisation's policy).";

    /// <summary>Why an import can't happen now, or null.</summary>
    public static string? ImportBlocked() =>
        AppSettings.Current.BackupAllowImport ? null : "Imports are switched off on this PC (Settings → Data & backups, or your organisation's policy).";

    public static bool SecretsAllowed => AppSettings.Current.BackupAllowSecrets;

    public static int MinPassword => Math.Clamp(AppSettings.Current.BackupMinPasswordLength, 12, 128);

    /// <summary>Why a file password is refused, or null.</summary>
    public static string? PasswordProblem(string pw) => pw.Length < MinPassword ? $"Use at least {MinPassword} characters." : null;

    /// <summary>Removes what an export may not carry from a pick list: credentials (passwords, SSH and VPN keys) and cloud
    /// API keys when secrets are not allowed.</summary>
    public static void Restrict(Backup.Choice c)
    {
        if (SecretsAllowed) return;
        c.Credentials.Clear();
        foreach (var a in c.Accounts.Values) a.Clear();
    }

    /// <summary>The secrets the app's own settings carry (AI provider keys, log-sink and Grafana tokens, the SMTP password,
    /// local API tokens), cleared from a copy that is about to be exported when secrets are not allowed.</summary>
    public static void StripSettingSecrets(AppSettings s)
    {
        s.OpenAiKey = ""; s.AnthropicKey = ""; s.SmtpPassword = ""; s.UpdateGitHubToken = "";
        s.ObserveSecrets = new(); s.ApiTokens = new();
        s.VultrAccounts = new(); s.ContaboAccounts = new(); s.HetznerAccounts = new(); s.AzureAccounts = new(); s.GcpAccounts = new();
    }
}

/// <summary>
/// Scheduled encrypted backups (0.5.0, ADR-060): every N days into a chosen folder, as the same password-protected
/// .dcxbackup a manual export writes, read back before it counts, the newest N kept. The file password is the user's,
/// kept only in the encrypted store (never the registry or a policy). Snapshots stay as they are: local, store-keyed and
/// automatic; a backup is portable and restorable on another PC.
/// </summary>
public static class ScheduledBackup
{
    private const string KeyFile = "backup-password.dcx";
    public const string Prefix = "darkcipher-backup-";

    private sealed class Secret { public string Password { get; set; } = ""; }

    public static bool HasPassword => SecureStore.IsUnlocked && File.Exists(Path.Combine(SecureStore.DataDir, KeyFile));

    public static void SetPassword(string pw)
    {
        if (BackupPolicy.PasswordProblem(pw) is { } why) throw new ArgumentException(why);
        SecureStore.Write(KeyFile, new Secret { Password = pw });
    }

    public static void ForgetPassword() { try { File.Delete(Path.Combine(SecureStore.DataDir, KeyFile)); } catch (IOException) { } }

    public sealed record Result(bool Ok, string Message, string? Path = null);

    /// <summary>Why a scheduled backup can't run, or null.</summary>
    public static string? Problem()
    {
        var s = AppSettings.Current;
        if (BackupPolicy.ExportBlocked() is { } b) return b;
        if (s.BackupFolder.Trim().Length == 0) return "no backup folder chosen";
        if (ProtectedSettings.PathCheck(s.BackupFolder.Trim()) is { } p) return "the backup folder is " + p;
        if (!HasPassword) return "no backup password set";
        return null;
    }

    /// <summary>When the next scheduled backup is due (null = scheduling is off or not set up).</summary>
    public static DateTime? NextDueUtc()
    {
        var s = AppSettings.Current;
        if (s.BackupScheduleDays <= 0 || Problem() != null) return null;
        return (s.BackupLastUtc ?? DateTime.MinValue).AddDays(Math.Clamp(s.BackupScheduleDays, 1, 30));
    }

    /// <summary>Runs the backup when it is due (called hourly, off the UI thread).</summary>
    public static Result? RunIfDue(Func<DateTime>? utcNow = null)
    {
        if (!SecureStore.IsUnlocked || SecureStore.Frozen) return null;
        if (NextDueUtc() is not { } due || (utcNow?.Invoke() ?? DateTime.UtcNow) < due) return null;
        return Run("scheduled");
    }

    /// <summary>Writes, verifies and prunes one backup now. Never throws.</summary>
    public static Result Run(string why)
    {
        var s = AppSettings.Current;
        try
        {
            if (Problem() is { } p) return Record(new Result(false, p));
            var dir = s.BackupFolder.Trim();
            Directory.CreateDirectory(dir);
            var choice = Backup.Choice.Everything();
            var r = Backup.Custom(choice, $"{why} backup");
            if (!s.BackupScheduledSecrets) Backup.WithoutSecrets(r.File);
            var pw = SecureStore.Read<Secret>(KeyFile).Password;
            var path = Path.Combine(dir, $"{Prefix}{DateTime.Now:yyyyMMdd-HHmmss}.dcxbackup");
            Backup.Write(path, pw, r.File);
            try { Backup.Read(path, pw); }
            catch (Exception ex) { try { File.Delete(path); } catch (IOException) { } return Record(new Result(false, "the backup was written but could not be read back: " + ex.Message)); }
            s.BackupLastUtc = DateTime.UtcNow;
            Prune(dir, Math.Clamp(s.BackupKeep, 1, 100), path);
            var (creds, keys) = r.Secrets;
            AuditLog.Write("app", "store", $"{why} encrypted backup written and verified", $"{r.File.Data?.Hosts.Count ?? 0} hosts, {creds} credentials, {keys} cloud keys");
            return Record(new Result(true, $"Written and verified: {Path.GetFileName(path)}", path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or InvalidOperationException or ArgumentException)
        {
            Log.Write("Scheduled backup", ex);
            return Record(new Result(false, ex.Message));
        }
    }

    private static Result Record(Result r)
    {
        var s = AppSettings.Current;
        s.BackupLastError = r.Ok ? "" : r.Message;
        try { AppSettings.Save(); } catch (Exception ex) { Log.Write("Backup state", ex); }
        return r;
    }

    /// <summary>The backups in a folder, newest first (only this app's own file names).</summary>
    public static List<FileInfo> List(string dir)
    {
        try { return new DirectoryInfo(dir).GetFiles(Prefix + "*.dcxbackup").OrderByDescending(f => f.Name, StringComparer.Ordinal).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return new(); }
    }

    /// <summary>Deletes all but the newest <paramref name="keep"/> — never the one just written and verified.</summary>
    public static int Prune(string dir, int keep, string? justWritten = null)
    {
        int n = 0;
        foreach (var f in List(dir).Skip(keep))
        {
            if (justWritten != null && f.FullName.Equals(justWritten, StringComparison.OrdinalIgnoreCase)) continue;
            try { f.Delete(); n++; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Write("Backup prune", ex); }
        }
        return n;
    }
}
