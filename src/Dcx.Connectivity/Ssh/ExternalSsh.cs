using System.Diagnostics;
using System.IO;
using DcxManager.Services;

namespace DcxManager.Ssh;

/// <summary>
/// Opens the host in Windows Terminal (or a console) with Windows' OpenSSH client — useful for ssh-agent, FIDO keys,
/// X11/agent forwarding or anything the built-in terminal doesn't do. A vault key is written to a private temp file
/// (current user only) that is deleted again a few seconds after ssh has read it.
/// </summary>
public static class ExternalSsh
{
    public static void Launch(HostEntry host)
    {
        // Offline mode (ADR-016): the first hop (the jump host, if any) is checked fully, the target without DNS.
        if (Offline.On)
        {
            var jump = AppData.Current.FindHost(host.JumpHostId);
            try { if (jump != null) Offline.EnforceStatic(host.Address, "external SSH terminal (through a jump host)"); }
            catch (OfflineBlockedException ex) { Offline.Tell(ex); return; }
            Offline.Run(jump?.Address ?? host.Address, "external SSH terminal", () => LaunchNow(host));
            return;
        }
        LaunchNow(host);
    }

    private static void LaunchNow(HostEntry host)
    {
        var ssh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", "ssh.exe");
        if (!File.Exists(ssh)) ssh = "ssh.exe";
        var args = new List<string> { "-p", host.Port.ToString() };
        string? tempKey = null;

        if (SshConnector.CredentialFor(host) is { Kind: CredentialKind.PrivateKey } key)
        {
            if (!string.IsNullOrEmpty(key.Passphrase) || key.Secret.Contains("PuTTY-User-Key-File"))
            {
                Ui.Dialogs.Info("External terminal", "This key has a passphrase or is a PuTTY .ppk; OpenSSH will prompt for it or can't read it. Load it into ssh-agent instead, or use the built-in terminal.");
            }
            var dir = Path.Combine(AppInfo.CacheDir, "tmp");
            Directory.CreateDirectory(dir);
            tempKey = Path.Combine(dir, "k" + Guid.NewGuid().ToString("N"));
            KeyTools.ExportPrivateKey(key, tempKey);
            args.AddRange(new[] { "-i", tempKey, "-o", "IdentitiesOnly=yes" });
        }
        if (AppData.Current.FindHost(host.JumpHostId) is { } jump)
            args.AddRange(new[] { "-J", $"{jump.Username}@{jump.Address}:{jump.Port}" });
        args.Add($"{host.Username}@{host.Address}");

        var wt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe");
        ProcessStartInfo psi;
        if (File.Exists(wt))
        {
            psi = new ProcessStartInfo(wt) { UseShellExecute = false };
            foreach (var a in new[] { "new-tab", "--title", host.Display, ssh }.Concat(args)) psi.ArgumentList.Add(a);
        }
        else
        {
            psi = new ProcessStartInfo(ssh) { UseShellExecute = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
        }
        Process.Start(psi);
        AuditLog.Write("you", host.Display, "opened external OpenSSH terminal");

        if (tempKey != null)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(20));
                try { File.Delete(tempKey); } catch { }
            });
        }
    }

    /// <summary>Removes any key files left behind by a crash.</summary>
    public static void CleanUp()
    {
        try { var dir = Path.Combine(AppInfo.CacheDir, "tmp"); if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }
}
