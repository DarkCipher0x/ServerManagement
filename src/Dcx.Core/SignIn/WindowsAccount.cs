using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace DcxManager.SignIn;

/// <summary>
/// Who is signed in to Windows, read-only (0.5.0, ADR-062): the account (DOMAIN\name, UPN, SID), whether it is a domain
/// account, the PC's join state (Active Directory, Microsoft Entra ID, hybrid) from <c>dsregcmd /status</c>, and the
/// Kerberos tickets from <c>klist</c> — counts, realm and expiry only, never a ticket. Used by Settings → Identity &
/// directory and to open the profile bound to this account without asking.
/// </summary>
public static class WindowsAccount
{
    public sealed record Account(string Name, string? Upn, string Sid, bool DomainAccount, string Domain);

    public sealed record JoinState(bool AdJoined, bool EntraJoined, bool WorkplaceJoined, string Tenant, string DomainName, bool PrimaryRefreshToken);

    public sealed record Tickets(int Count, bool HasTgt, string Realm, DateTime? TgtExpires);

    public static Account Current()
    {
        using var id = WindowsIdentity.GetCurrent();
        var sid = id.User?.Value ?? "";
        var name = id.Name;
        var domain = name.Contains('\\') ? name[..name.IndexOf('\\')] : "";
        bool domainAccount = sid.StartsWith("S-1-5-21-", StringComparison.Ordinal) && !domain.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase) && domain.Length > 0;
        return new Account(name, Upn(), sid, domainAccount, domain);
    }

    [DllImport("secur32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetUserNameEx(int nameFormat, StringBuilder? name, ref uint size);

    /// <summary>The account's user principal name (name@domain), or null for a local or Microsoft account without one.</summary>
    public static string? Upn()
    {
        try
        {
            uint size = 0;
            GetUserNameEx(8, null, ref size);   // NameUserPrincipal
            if (size == 0 || size > 1024) return null;
            var sb = new StringBuilder((int)size);
            return GetUserNameEx(8, sb, ref size) ? sb.ToString() : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    /// <summary>Does <paramref name="binding"/> belong to this Windows account? Windows: the SID; LDAPS / Entra: the UPN or
    /// DOMAIN\name (case-insensitive).</summary>
    public static bool Matches(DirectoryBinding binding, Account a) => binding.Kind switch
    {
        "windows" => binding.Subject.Length > 0 && binding.Subject.Equals(a.Sid, StringComparison.OrdinalIgnoreCase),
        _ => binding.User.Length > 0 && (binding.User.Equals(a.Upn ?? "", StringComparison.OrdinalIgnoreCase) || binding.User.Equals(a.Name, StringComparison.OrdinalIgnoreCase)),
    };

    /// <summary>Runs a Windows tool from System32 (no shell, no PATH lookup), up to 10 s, and returns its output.</summary>
    private static async Task<string> RunAsync(string exe, string args)
    {
        var path = Path.Combine(Environment.SystemDirectory, exe);
        if (!File.Exists(path)) return "";
        var psi = new ProcessStartInfo(path, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 };
        using var p = Process.Start(psi);
        if (p == null) return "";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var text = await p.StandardOutput.ReadToEndAsync(cts.Token);
            await p.WaitForExitAsync(cts.Token);
            return text.Length > 256 * 1024 ? text[..(256 * 1024)] : text;
        }
        catch (OperationCanceledException) { try { p.Kill(); } catch (InvalidOperationException) { } return ""; }
    }

    public static async Task<JoinState> JoinAsync() => ParseJoin(await RunAsync("dsregcmd.exe", "/status"));

    public static JoinState ParseJoin(string text)
    {
        string V(string key) => Regex.Match(text, @"^\s*" + Regex.Escape(key) + @"\s*:\s*(.+?)\s*$", RegexOptions.Multiline) is { Success: true } m ? m.Groups[1].Value : "";
        bool Yes(string key) => V(key).Equals("YES", StringComparison.OrdinalIgnoreCase);
        return new JoinState(Yes("DomainJoined"), Yes("AzureAdJoined"), Yes("WorkplaceJoined"), V("TenantName"), V("DomainName"), Yes("AzureAdPrt"));
    }

    public static async Task<Tickets> TicketsAsync() => ParseKlist(await RunAsync("klist.exe", ""));

    /// <summary>klist output: "#n> Client: … Server: krbtgt/REALM @ REALM … End Time: 10/21/2026 18:02:11 (local)".</summary>
    public static Tickets ParseKlist(string text)
    {
        var blocks = Regex.Split(text, @"(?m)^\s*#\d+>").Skip(1).ToList();
        string realm = ""; DateTime? tgtEnd = null; bool tgt = false;
        foreach (var b in blocks)
        {
            var server = Regex.Match(b, @"Server:\s*(\S+)\s*@\s*(\S+)");
            if (!server.Success || !server.Groups[1].Value.StartsWith("krbtgt/", StringComparison.OrdinalIgnoreCase)) continue;
            tgt = true; realm = server.Groups[2].Value;
            var end = Regex.Match(b, @"End Time:\s*(.+?)\s*\(local\)");
            // klist prints in the PC's own date format; the invariant (US) one is tried too.
            if (end.Success && (DateTime.TryParse(end.Groups[1].Value, System.Globalization.CultureInfo.CurrentCulture, System.Globalization.DateTimeStyles.None, out var t)
                || DateTime.TryParse(end.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out t))) tgtEnd = t;
        }
        return new Tickets(blocks.Count, tgt, realm, tgtEnd);
    }
}
