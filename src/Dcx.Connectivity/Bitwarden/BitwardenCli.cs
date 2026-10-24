using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using DcxManager.Services;

namespace DcxManager.Bitwarden;

/// <summary>
/// Runs Bitwarden's own command-line tools (0.6.14, ADR-093): <c>bw</c> for the Password Manager vault and <c>bws</c> for
/// Secrets Manager. They are Bitwarden's supported way in for other programs (the vault's own API is internal), and they
/// do the end-to-end decryption, so the app never handles Bitwarden's key material.
/// <list type="bullet">
/// <item>Only a CLI <b>signed by Bitwarden</b> (Authenticode, chain and revocation checked) or a build an administrator
/// approved by its SHA-256 (<see cref="AppSettings.BitwardenCliPins"/>, admin-protected) is ever started. The file is held
/// open with writers locked out from the check until the process has started, so it can't be swapped in between.</item>
/// <item>Secrets (the master password, the personal API key, the session key, the access token) go to the CLI only as
/// environment variables of that one process, never on its command line (which Task Manager, process auditing and EDR
/// tools record).</item>
/// <item>The environment is cleaned first: inherited <c>BW_*</c> / <c>BWS_*</c> values and every <c>NODE_*</c> variable
/// except <c>NODE_EXTRA_CA_CERTS</c> (so nothing can inject code into the Node runtime inside bw, or turn off its TLS
/// checks); bw keeps its encrypted vault cache in a folder of the app's own, apart from any bw you use in a terminal.</item>
/// <item>No window, no prompt (<c>--nointeraction</c>, stdin closed), a time limit, and stderr masked before it is shown.</item>
/// </list>
/// </summary>
public static class BitwardenCli
{
    public enum Tool { Bw, Bws }
    public sealed record Result(int ExitCode, string Output, string Error);
    /// <summary>One call as a test fake sees it.</summary>
    public sealed record Call(Tool Tool, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string> Env);
    /// <summary>What the trust check found for a CLI file.</summary>
    public sealed record Trust(bool Ok, string Path, string Sha256, string Signer, string Why);

    /// <summary>Self-test seam: answers calls instead of starting a process (no CLI is ever run by the self-test).</summary>
    internal static Func<Call, CancellationToken, Task<Result>>? Fake { get; set; }
    /// <summary>Self-test seam: the file to run instead of the real CLI (it still goes through the trust check).</summary>
    internal static string? ExeForTest { get; set; }

    /// <summary>The names a signed Bitwarden build carries as its signer (the certificate's common name).</summary>
    internal static readonly string[] Publishers = { "Bitwarden Inc.", "Bitwarden, Inc." };

    /// <summary>bw's own folder (its encrypted vault cache and settings): the app's, not the one a terminal bw uses.</summary>
    public static string DataDir { get; set; } = Path.Combine(AppInfo.CacheDir, "bitwarden-cli");

    private static readonly Dictionary<string, (long Length, DateTime Written, (string Sha, int Rc, string Signer) Seen)> Checked = new(StringComparer.OrdinalIgnoreCase);

    public static string ExeName(Tool t) => t == Tool.Bw ? "bw.exe" : "bws.exe";
    public static string Name(Tool t) => t == Tool.Bw ? "Bitwarden CLI (bw)" : "Bitwarden Secrets Manager CLI (bws)";

    /// <summary>What to do when bw isn't there (0.6.16: the desktop app doesn't include it, which wasn't obvious).</summary>
    public const string Missing = "The Bitwarden CLI (bw.exe) isn't installed yet: press “Install Bitwarden CLI” on VAULT → Bitwarden, or run winget install Bitwarden.CLI. The Bitwarden desktop app doesn't include it.";

    public static string NotInstalled(Tool t) => t == Tool.Bw
        ? "The Bitwarden CLI (bw.exe) isn't installed: winget install Bitwarden.CLI, or set its path in Settings → Integrations → Bitwarden. (The npm build, bw.cmd, runs through Node and isn't used.)"
        : "The Bitwarden Secrets Manager CLI (bws.exe) isn't installed: download it from Bitwarden's sdk-sm releases on GitHub, or set its path in Settings → Integrations → Bitwarden.";

    /// <summary>The CLI to run: the path set in Settings, else the usual install places (winget, Chocolatey, Scoop, Cargo,
    /// Program Files), else PATH. Only .exe files: a .cmd / .ps1 wrapper is never run.</summary>
    public static string? Find(Tool t)
    {
        if (ExeForTest is { } test) return test;
        var set = t == Tool.Bw ? AppSettings.Current.BitwardenCliPath : AppSettings.Current.BitwardenSmCliPath;
        if (set.Trim().Length > 0) return set.Trim().EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(set.Trim()) ? Path.GetFullPath(set.Trim()) : null;
        var exe = ExeName(t);
        string Env(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        var local = Env(Environment.SpecialFolder.LocalApplicationData);
        var home = Env(Environment.SpecialFolder.UserProfile);
        var places = new List<string> { Path.Combine(local, "Microsoft", "WinGet", "Links", exe) };
        try
        {
            var pkgs = Path.Combine(local, "Microsoft", "WinGet", "Packages");
            if (Directory.Exists(pkgs))
                places.AddRange(Directory.EnumerateDirectories(pkgs, "Bitwarden.*").SelectMany(d => new[] { Path.Combine(d, exe) }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        places.AddRange(t == Tool.Bw
            ? new[] { Path.Combine(@"C:\ProgramData\chocolatey\lib\bitwarden-cli\tools", exe), Path.Combine(home, "scoop", "apps", "bitwarden-cli", "current", exe), Path.Combine(Env(Environment.SpecialFolder.ProgramFiles), "Bitwarden CLI", exe), Path.Combine(local, "Programs", "Bitwarden CLI", exe) }
            : new[] { Path.Combine(home, ".cargo", "bin", exe), Path.Combine(Env(Environment.SpecialFolder.ProgramFiles), "bws", exe), Path.Combine(local, "Programs", "bws", exe) });
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            if (dir.Trim().Length > 0) try { places.Add(Path.Combine(dir.Trim(), exe)); } catch (ArgumentException) { }
        foreach (var p in places)
            try { if (File.Exists(p)) return Resolve(p); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>A winget link points at the real file: that file is the one checked and run.</summary>
    private static string Resolve(string p) => new FileInfo(p).ResolveLinkTarget(true)?.FullName ?? Path.GetFullPath(p);

    /// <summary>Whether this file may be run: signed by Bitwarden, or approved by its SHA-256. Cached per file version.</summary>
    public static Trust Check(string path)
    {
        using var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Check(path, hold);
    }

    private static Trust Check(string path, FileStream hold)
    {
        // The hash and the signature are worked out once per file version (hashing ~100 MB and checking revocation take a
        // moment); the approval list is read every time, so approving or withdrawing a build counts at once.
        var fi = new FileInfo(path);
        (string Sha, int Rc, string Signer) seen;
        lock (Checked) seen = Checked.TryGetValue(path, out var c) && c.Length == fi.Length && c.Written == fi.LastWriteTimeUtc ? c.Seen : default;
        if (seen.Sha == null)
        {
            hold.Position = 0;
            var sha = Convert.ToHexString(SHA256.HashData(hold));
            int rc = Authenticode.Verify(path, online: !Offline.On);
            string signer = "";
            if (rc == 0 || rc == Authenticode.RevocationOffline)
            {
                using var cert = Authenticode.Signer(path);
                signer = cert?.GetNameInfo(X509NameType.SimpleName, false) ?? "";
            }
            seen = (sha, rc, signer);
            lock (Checked) Checked[path] = (fi.Length, fi.LastWriteTimeUtc, seen);
        }
        var (s, r, who) = seen;
        bool bitwarden = Publishers.Contains(who, StringComparer.Ordinal);
        if (r == 0 && bitwarden) return new(true, path, s, who, $"signed by {who}");
        if (r == Authenticode.RevocationOffline && Offline.On && bitwarden) return new(true, path, s, who, $"signed by {who} (revocation not checked: offline mode)");
        if (Pinned(s)) return new(true, path, s, who, "approved by an administrator (SHA-256 " + Pretty(s) + ")");
        var what = r == Authenticode.NotSigned ? "isn't signed" : r != 0 ? $"has a signature Windows doesn't accept (0x{r:X8})" : $"is signed by “{who}”, not by Bitwarden";
        return new(false, path, s, who, $"{Path.GetFileName(path)} {what}, so it isn't run. If it's a build you trust, an administrator can approve it in Settings → Integrations → Bitwarden (SHA-256 {Pretty(s)}).");
    }

    /// <summary>The SHA-256 is in the administrator-approved list.</summary>
    public static bool Pinned(string sha) =>
        AppSettings.Current.BitwardenCliPins.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains(sha, StringComparer.OrdinalIgnoreCase);

    public static string Pretty(string sha) => sha.Length == 64 ? string.Join(" ", Enumerable.Range(0, 8).Select(i => sha.Substring(i * 8, 8))) : sha;

    /// <summary>Forgets the cached hashes and signatures (the next run checks the file again).</summary>
    public static void Recheck() { lock (Checked) Checked.Clear(); }

    /// <summary>Runs the tool with these arguments (never a secret) and these extra environment variables (the secrets).</summary>
    public static async Task<Result> RunAsync(Tool tool, IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? env = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        env ??= new Dictionary<string, string>();
        var all = tool == Tool.Bw ? args.Append("--nointeraction").ToList() : args.ToList();
        if (Fake is { } fake) return await fake(new Call(tool, all, env), ct);
        var exe = Find(tool) ?? throw new InvalidOperationException(NotInstalled(tool));
        Directory.CreateDirectory(DataDir);
        Process p;
        // Writers (and renames) are locked out from the check until the process has started from the checked file.
        using (var hold = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var trust = Check(exe, hold);
            if (!trust.Ok) throw new InvalidOperationException(trust.Why);
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = DataDir,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false),
            };
            Clean(psi.Environment);
            if (tool == Tool.Bw) { psi.Environment["BITWARDENCLI_APPDATA_DIR"] = DataDir; psi.Environment["BW_NOINTERACTION"] = "true"; }
            foreach (var (k, v) in env) psi.Environment[k] = v;
            foreach (var a in all) psi.ArgumentList.Add(a);
            p = Process.Start(psi) ?? throw new InvalidOperationException(Name(tool) + " didn't start.");
        }
        using (p)
        {
            try { p.StandardInput.Close(); } catch (IOException) { }
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeout ?? TimeSpan.FromSeconds(45));
            var outTask = p.StandardOutput.ReadToEndAsync(limit.Token);
            var errTask = p.StandardError.ReadToEndAsync(limit.Token);
            try { await p.WaitForExitAsync(limit.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                if (ct.IsCancellationRequested) throw;
                throw new TimeoutException($"{Name(tool)} didn't answer in time.");
            }
            return new Result(p.ExitCode, await outTask, await errTask);
        }
    }

    /// <summary>Removes what must never reach the CLI from the inherited environment.</summary>
    internal static void Clean(IDictionary<string, string?> e)
    {
        foreach (var k in e.Keys.ToList())
            if (k.StartsWith("BW_", StringComparison.OrdinalIgnoreCase) || k.StartsWith("BWS_", StringComparison.OrdinalIgnoreCase) || k.StartsWith("BITWARDENCLI_", StringComparison.OrdinalIgnoreCase)
                || k.StartsWith("NODE_", StringComparison.OrdinalIgnoreCase) && !k.Equals("NODE_EXTRA_CA_CERTS", StringComparison.OrdinalIgnoreCase))
                e.Remove(k);
    }

    // ---- installing bw (0.6.16; DarkCipher0x tried the integration without the CLI and nothing said why it didn't work) ----

    /// <summary>Runs winget with these arguments (hidden; the code is -1 when winget isn't there). Self-test seam.</summary>
    internal static Func<IReadOnlyList<string>, CancellationToken, Task<(int Code, string Output)>> Winget { get; set; } = RunWingetAsync;

    /// <summary>Installs Bitwarden's vault CLI for this Windows user from winget (package Bitwarden.CLI, publisher
    /// Bitwarden Inc.), then finds it. Returns its path; the trust check still decides whether it runs.</summary>
    public static async Task<string> InstallBwAsync(CancellationToken ct = default)
    {
        if (Offline.On) throw Offline.Block("winget", "installing the Bitwarden CLI", "offline mode is on");
        var (code, output) = await Winget(new[] { "install", "--id", "Bitwarden.CLI", "-e", "--source", "winget", "--scope", "user",
            "--accept-source-agreements", "--accept-package-agreements", "--disable-interactivity" }, ct);
        AuditLog.Write("you", "bitwarden", "Bitwarden CLI installed (winget Bitwarden.CLI)", code == 0 ? "ok" : $"exit {code}");
        Recheck();
        if (Find(Tool.Bw) is { } path) return path;
        if (code == -1) throw new InvalidOperationException("winget isn't available on this PC: install “App Installer” from the Microsoft Store, or download bw.exe from bitwarden.com/download and set its path in Settings → Integrations → Bitwarden → CLI.");
        var last = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0 && !l.All(c => c is '-' or '\\' or '|' or '/' or '█' or '▒' or ' ')) ?? "";
        throw new InvalidOperationException($"winget finished (exit {code}) but bw.exe wasn't found{(last.Length > 0 ? ": " + Redaction.Mask(last) : ".")}");
    }

    private static async Task<(int, string)> RunWingetAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe");
        var psi = new ProcessStartInfo(File.Exists(exe) ? exe : "winget.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process? p;
        try { p = Process.Start(psi); }
        catch (System.ComponentModel.Win32Exception) { return (-1, ""); }
        if (p == null) return (-1, "");
        using (p)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromMinutes(10));
            var o = p.StandardOutput.ReadToEndAsync(limit.Token);
            var e = p.StandardError.ReadToEndAsync(limit.Token);
            try { await p.WaitForExitAsync(limit.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { } throw new TimeoutException("winget didn't finish in 10 minutes."); }
            return (p.ExitCode, await o + await e);
        }
    }

    /// <summary>The CLI's error, made fit to show: first lines, masked, short.</summary>
    public static string Problem(Tool tool, Result r)
    {
        var text = (r.Error.Trim().Length > 0 ? r.Error : r.Output).Trim();
        var line = string.Join(" ", text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(2));
        line = Redaction.Mask(line);
        if (line.Length > 300) line = line[..300] + "…";
        return $"{Name(tool)}: {(line.Length > 0 ? line : "exit code " + r.ExitCode)}";
    }
}
