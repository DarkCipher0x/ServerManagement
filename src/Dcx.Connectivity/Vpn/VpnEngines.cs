using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using DcxManager.Services;

namespace DcxManager.Vpn;

/// <summary>What a tunnel reports while up.</summary>
public sealed record VpnStatus(bool Up, string State, string Endpoint, DateTime? LastHandshake, long RxBytes, long TxBytes, string Detail);

/// <summary>One engine per VPN kind (WireGuard, OpenVPN; the self-test has a fake). An engine brings a rendered,
/// secrets-included configuration up and down and reports status. Where the engine needs administrator rights
/// (installing a tunnel service, creating a TAP adapter), Windows asks (UAC) each time — the app never stores an
/// elevation.</summary>
public interface IVpnEngine
{
    string Kind { get; }
    /// <summary>Where the engine's program is, or null with the install hint when it isn't on this PC.</summary>
    string? Find(out string hint);
    Task UpAsync(VpnProfile p, string renderedConfig, Func<string, string?> secret, CancellationToken ct);
    Task DownAsync(VpnProfile p, CancellationToken ct);
    Task<VpnStatus> StatusAsync(VpnProfile p, CancellationToken ct);
    /// <summary>The network interface the tunnel uses while up (for the routes read-back).</summary>
    string InterfaceName(VpnProfile p);
}

/// <summary>Where rendered configurations live for the moment an engine needs them: a folder only this user can read
/// (an explicit ACL), under %LOCALAPPDATA%. The file is removed when the tunnel goes down.</summary>
public static class VpnFiles
{
    public static string Dir => Path.Combine(AppInfo.CacheDir, "vpn");

    public static string Write(string name, string ext, string text)
    {
        Directory.CreateDirectory(Dir);
        try
        {
            var di = new DirectoryInfo(Dir);
            var acl = new DirectorySecurity();
            acl.SetAccessRuleProtection(true, false);
            acl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            di.SetAccessControl(acl);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException) { Log.Write("vpn folder ACL", ex); }
        var path = Path.Combine(Dir, SafeName(name) + ext);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    public static void Delete(string name, string ext) { try { File.Delete(Path.Combine(Dir, SafeName(name) + ext)); } catch (IOException) { } }

    /// <summary>WireGuard needs the tunnel name to be the file name: letters, digits, _ = + . -, at most 15 characters.</summary>
    public static string SafeName(string name)
    {
        var s = Regex.Replace(name, @"[^A-Za-z0-9_=+.\-]", "-");
        return s.Length == 0 ? "tunnel" : s.Length > 15 ? s[..15] : s;
    }
}

/// <summary>WireGuard through the official Windows client: wireguard.exe /installtunnelservice (and /uninstalltunnelservice),
/// which asks for administrator approval; status from wg.exe show. The configuration file exists only while the tunnel is up.</summary>
public sealed class WireGuardEngine : IVpnEngine
{
    public string Kind => "wireguard";
    public static Func<string, string[], bool, CancellationToken, Task<(int Code, string Output)>> Run { get; set; } = RunProcessAsync;

    public string? Find(out string hint)
    {
        hint = "Install WireGuard for Windows from wireguard.com/install — the app drives its tunnel service.";
        foreach (var d in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            var p = Path.Combine(d, "WireGuard", "wireguard.exe");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    public string InterfaceName(VpnProfile p) => VpnFiles.SafeName(p.Name);

    public async Task UpAsync(VpnProfile p, string renderedConfig, Func<string, string?> secret, CancellationToken ct)
    {
        var exe = Find(out var hint) ?? throw new InvalidOperationException(hint);
        var path = VpnFiles.Write(p.Name, ".conf", renderedConfig);
        var (code, output) = await Run(exe, ["/installtunnelservice", path], true, ct);
        if (code != 0) { VpnFiles.Delete(p.Name, ".conf"); throw new InvalidOperationException($"WireGuard didn't install the tunnel (exit {code}){(output.Length > 0 ? ": " + output : "")} — administrator approval is needed (UAC)"); }
    }

    public async Task DownAsync(VpnProfile p, CancellationToken ct)
    {
        var exe = Find(out var hint) ?? throw new InvalidOperationException(hint);
        try { await Run(exe, ["/uninstalltunnelservice", InterfaceName(p)], true, ct); }
        finally { VpnFiles.Delete(p.Name, ".conf"); }
    }

    public async Task<VpnStatus> StatusAsync(VpnProfile p, CancellationToken ct)
    {
        var exe = Find(out _);
        var wg = exe == null ? null : Path.Combine(Path.GetDirectoryName(exe)!, "wg.exe");
        if (wg == null || !File.Exists(wg)) return new VpnStatus(false, "WireGuard not installed", "", null, 0, 0, "");
        var (code, output) = await Run(wg, ["show", InterfaceName(p)], false, ct);
        if (code != 0) return new VpnStatus(false, "down", "", null, 0, 0, output.Trim());
        return ParseWgShow(output);
    }

    /// <summary>"wg show" output → status (endpoint, latest handshake, transfer).</summary>
    public static VpnStatus ParseWgShow(string output)
    {
        var endpoint = Regex.Match(output, @"endpoint:\s*(\S+)").Groups[1].Value;
        DateTime? hs = null;
        var m = Regex.Match(output, @"latest handshake:\s*(.+)");
        if (m.Success)
        {
            var t = m.Groups[1].Value; double secs = 0;
            foreach (Match x in Regex.Matches(t, @"(\d+)\s*(day|hour|minute|second)")) secs += int.Parse(x.Groups[1].Value) * x.Groups[2].Value switch { "day" => 86400, "hour" => 3600, "minute" => 60, _ => 1 };
            hs = DateTime.Now.AddSeconds(-secs);
        }
        var tr = Regex.Match(output, @"transfer:\s*([\d.]+)\s*(\w+) received,\s*([\d.]+)\s*(\w+) sent");
        long Bytes(string n, string unit) => (long)(double.Parse(n, System.Globalization.CultureInfo.InvariantCulture) * unit.ToLowerInvariant() switch { "kib" => 1024, "mib" => 1048576, "gib" => 1073741824L, "tib" => 1099511627776L, _ => 1 });
        long rx = tr.Success ? Bytes(tr.Groups[1].Value, tr.Groups[2].Value) : 0, tx = tr.Success ? Bytes(tr.Groups[3].Value, tr.Groups[4].Value) : 0;
        bool fresh = hs != null && DateTime.Now - hs.Value < TimeSpan.FromMinutes(3);
        return new VpnStatus(true, fresh ? "up" : hs == null ? "up (no handshake yet)" : "up (handshake stale)", endpoint, hs, rx, tx, "");
    }

    private static async Task<(int, string)> RunProcessAsync(string exe, string[] args, bool elevated, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = elevated, CreateNoWindow = true, RedirectStandardOutput = !elevated, RedirectStandardError = !elevated };
        if (elevated) psi.Verb = "runas";   // the tunnel service needs administrator rights: Windows asks (UAC)
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process p;
        try { p = Process.Start(psi) ?? throw new InvalidOperationException("didn't start"); }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { return (1223, "administrator approval was declined"); }
        using (p)
        {
            var output = elevated ? "" : await p.StandardOutput.ReadToEndAsync(ct) + await p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            return (p.ExitCode, output);
        }
    }
}

/// <summary>OpenVPN through openvpn.exe (OpenVPN Community 2.5+): started elevated with the rendered configuration and a
/// management channel on 127.0.0.1; the user name and password (auth-user-pass) and the key passphrase go over that
/// channel from the vault, never into a file. Status and bytes come from the same channel.</summary>
public sealed class OpenVpnEngine : IVpnEngine
{
    public string Kind => "openvpn";
    private static readonly Dictionary<string, (Process? Proc, int Port, string Password)> Running = new();

    /// <summary>Opens the management channel and authenticates with the per-run password.</summary>
    private static async Task<(TcpClient Tcp, StreamReader R, StreamWriter W)> ManagementAsync(int port, string password, int attempts, CancellationToken ct)
    {
        var tcp = new TcpClient();
        for (int i = 0; i < attempts && !tcp.Connected; i++) { try { await tcp.ConnectAsync(IPAddress.Loopback, port, ct); } catch (SocketException) { await Task.Delay(200, ct); } }
        if (!tcp.Connected) { tcp.Dispose(); throw new InvalidOperationException("OpenVPN's management channel didn't open"); }
        var s = tcp.GetStream();
        var r = new StreamReader(s, Encoding.ASCII, false, 1024, true); var w = new StreamWriter(s, new UTF8Encoding(false), 1024, true) { AutoFlush = true, NewLine = "\n" };
        // "ENTER PASSWORD:" (no newline) comes first; the answer must be right or OpenVPN closes the channel.
        var buf = new char[64]; int n = await r.ReadAsync(buf, 0, buf.Length);
        if (n <= 0 || !new string(buf, 0, n).Contains("PASSWORD", StringComparison.OrdinalIgnoreCase)) { tcp.Dispose(); throw new InvalidOperationException("OpenVPN's management channel didn't ask for its password"); }
        await w.WriteLineAsync(password);
        var ok = await r.ReadLineAsync(ct);
        if (ok == null || !ok.StartsWith("SUCCESS")) { tcp.Dispose(); throw new InvalidOperationException("OpenVPN refused the management password"); }
        return (tcp, r, w);
    }

    public string? Find(out string hint)
    {
        hint = "Install OpenVPN Community (openvpn.net/community-downloads) — the app runs openvpn.exe with a management channel.";
        foreach (var d in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            var p = Path.Combine(d, "OpenVPN", "bin", "openvpn.exe");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    public string InterfaceName(VpnProfile p) => "OpenVPN TAP-Windows6";

    public async Task UpAsync(VpnProfile p, string renderedConfig, Func<string, string?> secret, CancellationToken ct)
    {
        var exe = Find(out var hint) ?? throw new InvalidOperationException(hint);
        var port = int.Parse(Regex.Match(renderedConfig, @"^management 127\.0\.0\.1 (\d+)", RegexOptions.Multiline).Groups[1].Value);
        var mgmtPassword = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        var pwFile = VpnFiles.Write(p.Name, ".mgmt", mgmtPassword + "\n");
        var path = VpnFiles.Write(p.Name, ".ovpn", renderedConfig.Replace($"management 127.0.0.1 {port}\n", $"management 127.0.0.1 {port} \"{pwFile.Replace("\\", "\\\\")}\"\n"));
        var psi = new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas", CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        psi.ArgumentList.Add("--config"); psi.ArgumentList.Add(path);
        Process proc;
        try { proc = Process.Start(psi) ?? throw new InvalidOperationException("openvpn.exe didn't start"); }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { VpnFiles.Delete(p.Name, ".ovpn"); VpnFiles.Delete(p.Name, ".mgmt"); throw new InvalidOperationException("administrator approval was declined (UAC)"); }
        lock (Running) Running[p.Id] = (proc, port, mgmtPassword);
        // The management channel: answer the password questions from the vault, then release the hold.
        try
        {
            var (tcp, r, w) = await ManagementAsync(port, mgmtPassword, 50, ct);
            using var _t = tcp; using var _r = r; using var _w = w;
            await w.WriteLineAsync("hold release");
            var deadline = DateTime.Now.AddSeconds(60);
            while (DateTime.Now < deadline)
            {
                var line = await r.ReadLineAsync(ct) ?? throw new InvalidOperationException("OpenVPN closed the management channel");
                if (line.StartsWith(">PASSWORD:Need 'Auth'"))
                {
                    var user = secret("auth-user") ?? throw new InvalidOperationException("no user name for auth-user-pass");
                    var pw = secret("auth-pass") ?? throw new InvalidOperationException("no password for auth-user-pass");
                    await w.WriteLineAsync($"username \"Auth\" \"{Esc(user)}\""); await w.WriteLineAsync($"password \"Auth\" \"{Esc(pw)}\"");
                }
                else if (line.StartsWith(">PASSWORD:Need 'Private Key'"))
                    await w.WriteLineAsync($"password \"Private Key\" \"{Esc(secret("key-passphrase") ?? "")}\"");
                else if (line.StartsWith(">PASSWORD:Verification Failed")) throw new InvalidOperationException("OpenVPN: the user name or password was refused");
                else if (line.StartsWith(">STATE:") && line.Contains(",CONNECTED,")) return;
                else if (line.StartsWith(">FATAL:")) throw new InvalidOperationException("OpenVPN: " + line[7..]);
            }
            throw new InvalidOperationException("OpenVPN didn't connect within 60 s");
        }
        catch { await DownAsync(p, CancellationToken.None); throw; }
    }

    private static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    public async Task DownAsync(VpnProfile p, CancellationToken ct)
    {
        (Process? Proc, int Port, string Password) r;
        lock (Running) { if (!Running.Remove(p.Id, out r)) { VpnFiles.Delete(p.Name, ".ovpn"); VpnFiles.Delete(p.Name, ".mgmt"); return; } }
        try
        {
            var (tcp, rd, w) = await ManagementAsync(r.Port, r.Password, 3, ct);
            using (tcp) using (rd) using (w) await w.WriteLineAsync("signal SIGTERM");
            if (r.Proc != null) { try { await r.Proc.WaitForExitAsync(new CancellationTokenSource(5000).Token); } catch (OperationCanceledException) { } }
        }
        catch (Exception ex) when (ex is SocketException or IOException or InvalidOperationException) { }
        finally { try { if (r.Proc is { HasExited: false }) r.Proc.Kill(); } catch (InvalidOperationException) { } VpnFiles.Delete(p.Name, ".ovpn"); VpnFiles.Delete(p.Name, ".mgmt"); }
    }

    public async Task<VpnStatus> StatusAsync(VpnProfile p, CancellationToken ct)
    {
        (Process? Proc, int Port, string Password) r;
        lock (Running) if (!Running.TryGetValue(p.Id, out r)) return new VpnStatus(false, "down", "", null, 0, 0, "");
        try
        {
            var (tcp, rd, w) = await ManagementAsync(r.Port, r.Password, 3, ct);
            using var _t = tcp; using var _r = rd; using var _w = w;
            await w.WriteLineAsync("state"); var state = await ReadUntilEnd(rd, ct);
            await w.WriteLineAsync("status 3"); var status = await ReadUntilEnd(rd, ct);
            return ParseManagement(state, status);
        }
        catch (Exception ex) when (ex is SocketException or IOException or InvalidOperationException) { return new VpnStatus(false, "down", "", null, 0, 0, ex.Message); }
    }

    /// <summary>"state" and "status 3" answers → status.</summary>
    public static VpnStatus ParseManagement(string state, string status)
    {
        var st = state.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0 && char.IsDigit(l[0]))?.Split(',') ?? [];
        var name = st.Length > 1 ? st[1] : "unknown";
        var endpoint = st.Length > 4 ? st[4] : "";
        long rx = 0, tx = 0;
        var m = Regex.Match(status, @"TUN/TAP read bytes[,\t](\d+)"); if (m.Success) rx = long.Parse(m.Groups[1].Value);
        m = Regex.Match(status, @"TUN/TAP write bytes[,\t](\d+)"); if (m.Success) tx = long.Parse(m.Groups[1].Value);
        bool up = name == "CONNECTED";
        return new VpnStatus(up, up ? "up" : name.ToLowerInvariant(), endpoint, up && st.Length > 0 && long.TryParse(st[0], out var ts) ? DateTimeOffset.FromUnixTimeSeconds(ts).LocalDateTime : null, rx, tx, "");
    }

    private static async Task<string> ReadUntilEnd(StreamReader r, CancellationToken ct)
    {
        var sb = new StringBuilder();
        string? line;
        while ((line = await r.ReadLineAsync(ct)) != null) { if (line == "END") break; if (line.StartsWith('>')) continue; sb.Append(line).Append('\n'); if (sb.Length > 64 * 1024) break; }
        return sb.ToString();
    }
}
