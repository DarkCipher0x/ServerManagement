using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using DcxManager.Sessions;
using Microsoft.Win32;

namespace DcxManager.Wsl;

/// <summary>
/// The Windows Subsystem for Linux on this PC (0.5.0, ADR-064; DarkCipher0x: "full WSL management abilities and integration …
/// manage network / conf files"). Everything runs wsl.exe / wslc.exe from System32 or the WSL install folder with an
/// argument list (never a shell string, never PATH); distribution names, container ids and image names are checked first.
/// Output is read as UTF-8 (WSL_UTF8=1). Files inside a distribution (/etc/wsl.conf, /etc/hosts) are read and written as
/// root through stdin, with a backup beside them; %UserProfile%\.wslconfig is edited here with a dated backup.
/// </summary>
public static class WslManager
{
    public sealed record Distro(string Name, string State, int Version, bool IsDefault, string BasePath, long DiskBytes);
    public sealed record Result(int ExitCode, string Output, string Error)
    {
        public bool Ok => ExitCode == 0;
        public string Combined => (Output.TrimEnd() + (Error.Trim().Length > 0 ? "\n" + Error.Trim() : "")).Trim();
    }

    public static string WslExe => Path.Combine(Environment.SystemDirectory, "wsl.exe");
    public static bool Installed => File.Exists(WslExe) && (Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss") != null || File.Exists(WslcExe ?? ""));

    /// <summary>wslc.exe (WSL Containers): the WSL install folder or System32; null when WSL Containers aren't installed.</summary>
    public static string? WslcExe => new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WSL", "wslc.exe"),
        Path.Combine(Environment.SystemDirectory, "wslc.exe"),
    }.FirstOrDefault(File.Exists);

    private static readonly Regex IdRx = new(@"^[A-Za-z0-9][A-Za-z0-9_.\-]{0,127}$", RegexOptions.CultureInvariant);
    private static readonly Regex ImageRx = new(@"^[a-z0-9][a-z0-9._\-/:@]{0,254}$", RegexOptions.CultureInvariant);
    public static string Name(string n) => LocalShells.ValidDistroName(n) ? n : throw new ArgumentException("A distribution name is letters, digits, . _ -");
    public static string Id(string n) => IdRx.IsMatch(n.Trim()) ? n.Trim() : throw new ArgumentException("A container name or id is letters, digits, . _ -");
    public static string Image(string n) => ImageRx.IsMatch(n.Trim()) ? n.Trim() : throw new ArgumentException("An image is like docker.io/library/nginx:latest (lower case)");

    /// <summary>Runs a WSL tool with an argument list; stdin optional; 10 minutes at most unless cancelled.</summary>
    public static async Task<Result> RunAsync(string exe, IEnumerable<string> args, string? stdin = null, CancellationToken ct = default, TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = stdin != null, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["WSL_UTF8"] = "1";
        using var p = Process.Start(psi) ?? throw new InvalidOperationException(Path.GetFileName(exe) + " didn't start");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromMinutes(10));
        if (stdin != null) { await p.StandardInput.WriteAsync(stdin.AsMemory(), cts.Token); p.StandardInput.Close(); }
        var o = p.StandardOutput.ReadToEndAsync(cts.Token); var e = p.StandardError.ReadToEndAsync(cts.Token);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch (InvalidOperationException) { } throw; }
        return new Result(p.ExitCode, Clean(await o), Clean(await e));
    }

    /// <summary>wsl.exe writes UTF-16 without WSL_UTF8 on old builds: NULs are dropped either way.</summary>
    private static string Clean(string s) => s.Replace("\0", "").Replace("\r\n", "\n");

    public static Task<Result> Wsl(CancellationToken ct, params string[] args) => RunAsync(WslExe, args, null, ct);

    // ---- distributions -------------------------------------------------------------------------------------------------

    /// <summary>Every distribution: state and version from `wsl --list --verbose`, base path and disk size from the registry.</summary>
    public static async Task<List<Distro>> ListAsync(CancellationToken ct = default)
    {
        var reg = new Dictionary<string, (string Path, bool Default)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var lxss = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss");
            var def = lxss?.GetValue("DefaultDistribution") as string;
            foreach (var id in lxss?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                using var k = lxss!.OpenSubKey(id);
                if (k?.GetValue("DistributionName") is string n && LocalShells.ValidDistroName(n)) reg[n] = (k.GetValue("BasePath") as string ?? "", string.Equals(id, def, StringComparison.OrdinalIgnoreCase));
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        var r = await Wsl(ct, "--list", "--verbose");
        return ParseList(r.Output).Select(d =>
        {
            var basePath = reg.TryGetValue(d.Name, out var x) ? x.Path.Replace(@"\\?\", "") : "";
            long size = 0;
            try { var vhd = Path.Combine(basePath, "ext4.vhdx"); if (basePath.Length > 0 && File.Exists(vhd)) size = new FileInfo(vhd).Length; } catch (IOException) { }
            return d with { BasePath = basePath, DiskBytes = size };
        }).ToList();
    }

    /// <summary>"  NAME      STATE     VERSION" then "* Ubuntu    Running   2" (the star marks the default).</summary>
    public static List<Distro> ParseList(string text)
    {
        var list = new List<Distro>();
        foreach (var raw in text.Split('\n').Skip(1))
        {
            var line = raw.TrimEnd();
            if (line.Trim().Length == 0) continue;
            bool def = line.TrimStart().StartsWith('*');
            var parts = line.Trim().TrimStart('*').Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || !LocalShells.ValidDistroName(parts[0])) continue;
            list.Add(new Distro(parts[0], parts[1], int.TryParse(parts[^1], out var v) ? v : 2, def, "", 0));
        }
        return list;
    }

    public static Task<Result> StartAsync(string d, CancellationToken ct) => Wsl(ct, "-d", Name(d), "--exec", "/bin/true");
    public static Task<Result> TerminateAsync(string d, CancellationToken ct) => Wsl(ct, "--terminate", Name(d));
    public static Task<Result> ShutdownAsync(CancellationToken ct) => Wsl(ct, "--shutdown");
    public static Task<Result> SetDefaultAsync(string d, CancellationToken ct) => Wsl(ct, "--set-default", Name(d));
    public static Task<Result> SetVersionAsync(string d, int v, CancellationToken ct) => v is 1 or 2 ? Wsl(ct, "--set-version", Name(d), v.ToString()) : throw new ArgumentException("version 1 or 2");
    public static Task<Result> UnregisterAsync(string d, CancellationToken ct) => Wsl(ct, "--unregister", Name(d));
    public static Task<Result> UpdateAsync(CancellationToken ct) => Wsl(ct, "--update");
    public static Task<Result> StatusAsync(CancellationToken ct) => Wsl(ct, "--status");
    public static Task<Result> VersionAsync(CancellationToken ct) => Wsl(ct, "--version");
    public static Task<Result> OnlineAsync(CancellationToken ct) => Wsl(ct, "--list", "--online");
    public static Task<Result> InstallAsync(string d, CancellationToken ct) => Wsl(ct, "--install", "-d", Name(d), "--no-launch");

    /// <summary>Exports to a .tar (or a .vhdx with vhd = true) at an absolute path chosen in a save dialog.</summary>
    public static Task<Result> ExportAsync(string d, string file, bool vhd, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(file)) throw new ArgumentException("full path");
        return vhd ? RunAsync(WslExe, new[] { "--export", Name(d), file, "--vhd" }, null, ct, TimeSpan.FromHours(2)) : RunAsync(WslExe, new[] { "--export", Name(d), file }, null, ct, TimeSpan.FromHours(2));
    }

    public static Task<Result> ImportAsync(string newName, string installDir, string file, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(installDir) || !Path.IsPathFullyQualified(file)) throw new ArgumentException("full paths");
        var args = new List<string> { "--import", Name(newName), installDir, file, "--version", "2" };
        if (file.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase)) args.Add("--vhd");
        return RunAsync(WslExe, args, null, ct, TimeSpan.FromHours(2));
    }

    /// <summary>The distribution's addresses and listening TCP ports (ss), read as root.</summary>
    public static Task<Result> NetworkAsync(string d, CancellationToken ct) => RunAsync(WslExe, new[] { "-d", Name(d), "-u", "root", "--exec", "sh", "-c",
        "echo \"addresses: $(hostname -I 2>/dev/null)\"; echo; echo 'listening (TCP):'; (ss -tlnpH 2>/dev/null || netstat -tln 2>/dev/null) | head -n 100; echo; echo '/etc/resolv.conf:'; cat /etc/resolv.conf 2>/dev/null" }, null, ct);

    // ---- files inside a distribution ------------------------------------------------------------------------------------

    public static readonly string[] EditableFiles = { "/etc/wsl.conf", "/etc/hosts", "/etc/resolv.conf" };

    public static Task<Result> ReadFileAsync(string d, string path, CancellationToken ct)
    {
        if (!EditableFiles.Contains(path)) throw new ArgumentException("not an editable file");
        return RunAsync(WslExe, new[] { "-d", Name(d), "-u", "root", "--exec", "cat", path }, null, ct);
    }

    /// <summary>Writes the file as root from stdin: the old one is kept as &lt;file&gt;.dcx-bak, the new one replaces it atomically.</summary>
    public static Task<Result> WriteFileAsync(string d, string path, string content, CancellationToken ct)
    {
        if (!EditableFiles.Contains(path)) throw new ArgumentException("not an editable file");
        if (content.Length > 256 * 1024 || content.Contains('\0')) throw new ArgumentException("content");
        var script = "set -e; f=\"$1\"; t=\"$f.dcx-new\"; cat > \"$t\"; [ -f \"$f\" ] && cp -p \"$f\" \"$f.dcx-bak\"; chmod 644 \"$t\"; mv \"$t\" \"$f\"; echo \"saved $f (previous copy: $f.dcx-bak)\"";
        return RunAsync(WslExe, new[] { "-d", Name(d), "-u", "root", "--exec", "sh", "-c", script, "sh", path }, content.Replace("\r\n", "\n"), ct);
    }

    // ---- %UserProfile%\.wslconfig --------------------------------------------------------------------------------------

    public static string WslConfigPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".wslconfig");

    /// <summary>The keys the form edits ([wsl2] section unless noted) and what each accepts.</summary>
    public static readonly (string Key, string Title, string Pattern)[] WslConfigKeys =
    {
        ("memory", "Memory (e.g. 8GB)", @"^\d{1,4}(MB|GB)$"),
        ("processors", "Processors", @"^\d{1,3}$"),
        ("swap", "Swap (e.g. 4GB, 0 = none)", @"^(0|\d{1,4}(MB|GB))$"),
        ("networkingMode", "Networking mode (nat / mirrored)", @"^(nat|mirrored|none|virtioproxy)$"),
        ("dnsTunneling", "DNS tunneling (true / false)", @"^(true|false)$"),
        ("firewall", "Hyper-V firewall (true / false)", @"^(true|false)$"),
        ("autoProxy", "Use Windows' proxy (true / false)", @"^(true|false)$"),
        ("localhostForwarding", "localhost forwarding (true / false)", @"^(true|false)$"),
        ("guiApplications", "GUI apps / WSLg (true / false)", @"^(true|false)$"),
        ("nestedVirtualization", "Nested virtualisation (true / false)", @"^(true|false)$"),
    };

    /// <summary>The value of a key in [wsl2] (or null).</summary>
    public static Dictionary<string, string> ReadWslConfig(string text)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string section = "";
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('#') || line.StartsWith(';') || line.Length == 0) continue;
            if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1].Trim().ToLowerInvariant(); continue; }
            int eq = line.IndexOf('=');
            if (section == "wsl2" && eq > 0) d[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return d;
    }

    /// <summary>Sets keys in [wsl2], keeping every other line, comment and section; an empty value removes the key.
    /// Throws when a value doesn't match its pattern.</summary>
    public static string SetWslConfig(string text, IReadOnlyDictionary<string, string> values)
    {
        foreach (var (k, v) in values)
        {
            var def = WslConfigKeys.FirstOrDefault(x => x.Key.Equals(k, StringComparison.OrdinalIgnoreCase));
            if (def.Key == null) throw new ArgumentException("unknown key " + k);
            if (v.Length > 0 && !Regex.IsMatch(v, def.Pattern, RegexOptions.IgnoreCase)) throw new ArgumentException($"{def.Title}: “{v}” isn't accepted.");
        }
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count == 1 && lines[0].Length == 0) lines.Clear();
        int start = lines.FindIndex(l => l.Trim().Equals("[wsl2]", StringComparison.OrdinalIgnoreCase));
        if (start < 0) { if (lines.Count > 0 && lines[^1].Length > 0) lines.Add(""); lines.Add("[wsl2]"); start = lines.Count - 1; }
        int end = lines.FindIndex(start + 1, l => l.TrimStart().StartsWith('['));
        if (end < 0) end = lines.Count;
        foreach (var (k, v) in values)
        {
            int at = -1;
            for (int i = start + 1; i < end; i++) { var t = lines[i].Trim(); int eq = t.IndexOf('='); if (eq > 0 && t[..eq].Trim().Equals(k, StringComparison.OrdinalIgnoreCase)) { at = i; break; } }
            if (v.Length == 0) { if (at >= 0) { lines.RemoveAt(at); end--; } continue; }
            if (at >= 0) lines[at] = $"{k}={v}"; else { lines.Insert(end, $"{k}={v}"); end++; }
        }
        return string.Join("\r\n", lines).TrimEnd() + "\r\n";
    }

    /// <summary>Writes .wslconfig, keeping a dated copy of the old one next to it.</summary>
    public static string SaveWslConfig(string text)
    {
        var p = WslConfigPath;
        string? backup = null;
        if (File.Exists(p)) { backup = p + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bak"; File.Copy(p, backup, overwrite: false); }
        File.WriteAllText(p, text, new UTF8Encoding(false));
        return backup ?? "";
    }

    // ---- WSL Containers (wslc) ----------------------------------------------------------------------------------------

    public static Task<Result> Wslc(CancellationToken ct, params string[] args) =>
        WslcExe is { } exe ? RunAsync(exe, args, null, ct) : throw new InvalidOperationException("WSL Containers (wslc.exe) aren't installed: wsl --update brings them (WSL 2.9.3 or later).");

    /// <summary>Which container / image subcommands this wslc offers, read from its own help (Microsoft hasn't published a
    /// full reference yet, so nothing is assumed).</summary>
    public static async Task<HashSet<string>> CapabilitiesAsync(CancellationToken ct)
    {
        var caps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in new[] { "container", "image", "volume", "network" })
        {
            var r = await Wslc(ct, group, "--help");
            foreach (Match m in Regex.Matches(r.Combined, @"^\s{2,}([a-z][a-z\-]+)\s{2,}", RegexOptions.Multiline)) caps.Add(group + " " + m.Groups[1].Value);
        }
        return caps;
    }

    /// <summary>A table as printed by `wslc container list` / `image list`: the header's column starts cut every row.</summary>
    public static (List<string> Columns, List<List<string>> Rows) ParseTable(string text)
    {
        var lines = text.Replace("\r", "").Split('\n').Where(l => l.Trim().Length > 0).ToList();
        if (lines.Count == 0) return (new(), new());
        var head = lines[0];
        var starts = Regex.Matches(head, @"(?<=^|\s{2,})\S").Select(m => m.Index).ToList();
        var cols = starts.Select((s, i) => head[s..(i + 1 < starts.Count ? starts[i + 1] : head.Length)].Trim()).ToList();
        var rows = new List<List<string>>();
        foreach (var l in lines.Skip(1))
            rows.Add(starts.Select((s, i) => s >= l.Length ? "" : l[s..Math.Min(l.Length, i + 1 < starts.Count ? starts[i + 1] : l.Length)].Trim()).ToList());
        return (cols, rows);
    }
}
