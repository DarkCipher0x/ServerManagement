using System.IO;
using System.Text.RegularExpressions;
using DcxManager.Sessions;
using DcxManager.Services;

namespace DcxManager.Ssh;

/// <summary>
/// SSH single sign-on with the Windows account's Kerberos ticket (0.5.0, ADR-062). The built-in SSH library has no GSSAPI,
/// so the host opens in a terminal tab running Windows' own OpenSSH client with GSSAPI on: no password or key is typed,
/// stored or passed. The server must accept gssapi-with-mic (a domain-joined Linux server: sssd / realmd, a host keytab).
/// Every value that reaches the command line is checked first; the ticket is forwarded only when the administrator allows
/// it (SsoKerberosDelegate). Host keys are checked by OpenSSH against your known_hosts as usual.
/// </summary>
public static class KerberosSsh
{
    private static readonly Regex HostRx = new(@"^[A-Za-z0-9](?:[A-Za-z0-9.\-]{0,252})$|^\[?[0-9A-Fa-f:.]{2,45}\]?$", RegexOptions.CultureInvariant);
    private static readonly Regex UserRx = new(@"^[A-Za-z0-9][A-Za-z0-9._\-]{0,63}(?:@[A-Za-z0-9.\-]{1,253})?$", RegexOptions.CultureInvariant);

    public static string SshExe
    {
        get
        {
            var p = Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh.exe");
            return File.Exists(p) ? p : "";
        }
    }

    /// <summary>The terminal profile that opens <paramref name="host"/> with Kerberos, or null with the reason.</summary>
    public static LocalShellProfile? Profile(HostEntry host, out string? why)
    {
        why = null;
        var s = AppSettings.Current;
        if (!s.SsoKerberosSsh) { why = "Kerberos sign-on is switched off on this PC (Settings → Identity & directory, or your organisation's policy)."; return null; }
        if (SshExe.Length == 0) { why = "Windows' OpenSSH client isn't installed (Settings → System → Optional features → OpenSSH Client)."; return null; }
        if (!HostRx.IsMatch(host.Address)) { why = "The host's address can't be used on the OpenSSH command line."; return null; }
        if (host.Username.Length > 0 && !UserRx.IsMatch(host.Username)) { why = "The user name can't be used on the OpenSSH command line (letters, digits, . _ - and an optional @realm)."; return null; }
        if (host.Port is < 1 or > 65535) { why = "port"; return null; }
        if (host.JumpHostId != null) { why = "Kerberos sign-on through a jump host isn't supported yet: connect without the jump host, or sign in with a key."; return null; }
        var args = new List<string>
        {
            "-o", "GSSAPIAuthentication=yes",
            "-o", "GSSAPIDelegateCredentials=" + (s.SsoKerberosDelegate ? "yes" : "no"),
            "-o", "PreferredAuthentications=gssapi-with-mic,publickey,keyboard-interactive,password",
            "-o", $"ServerAliveInterval={Math.Clamp(host.KeepAliveSeconds, 0, 3600)}",
            "-p", host.Port.ToString(),
            (host.Username.Length > 0 ? host.Username + "@" : "") + host.Address,
        };
        var cmd = Quote(SshExe) + " " + string.Join(" ", args);
        return new LocalShellProfile("kerberos:" + host.Id, host.Display, "🔑", cmd, null, false, "Kerberos (Windows sign-on) · Windows OpenSSH");
    }

    private static string Quote(string p) => p.Contains(' ') ? "\"" + p + "\"" : p;
}
