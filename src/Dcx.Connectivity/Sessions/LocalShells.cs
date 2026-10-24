using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DcxManager.Sessions;

/// <summary>A local shell the app can open in a terminal tab.</summary>
/// <param name="Id">"powershell", "pwsh", "cmd" or "wsl:&lt;distribution&gt;" — stable, used for restore and nav keys.</param>
public sealed record LocalShellProfile(string Id, string Name, string Glyph, string CommandLine, string? WorkingDir, bool Wsl, string Detail);

/// <summary>
/// Finds local shells without running anything: Windows PowerShell and cmd (always there), PowerShell 7 (Program Files
/// or PATH) and WSL distributions from the per-user Lxss registry (reading it doesn't start WSL). Docker Desktop's
/// internal distributions are skipped — they aren't meant for interactive shells.
/// </summary>
public static class LocalShells
{
    private static List<LocalShellProfile>? _cache;

    public static IReadOnlyList<LocalShellProfile> All => _cache ??= Discover();

    public static void Refresh() => _cache = null;

    public static LocalShellProfile? Find(string id) => All.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static List<LocalShellProfile> Discover()
    {
        var list = new List<LocalShellProfile>();
        var sys = Environment.SystemDirectory;

        var pwsh = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7-preview", "pwsh.exe"),
        }.FirstOrDefault(File.Exists) ?? OnPath("pwsh.exe");
        if (pwsh != null) list.Add(new("pwsh", "PowerShell 7", "❯", Quote(pwsh) + " -NoLogo", Home, false, pwsh));

        var ps = Path.Combine(sys, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (File.Exists(ps)) list.Add(new("powershell", "Windows PowerShell", "❯", Quote(ps) + " -NoLogo", Home, false, ps));

        var cmd = Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } c && File.Exists(c) ? c : Path.Combine(sys, "cmd.exe");
        if (File.Exists(cmd)) list.Add(new("cmd", "Command Prompt", "›", Quote(cmd), Home, false, cmd));

        var wsl = Path.Combine(sys, "wsl.exe");
        if (File.Exists(wsl))
            foreach (var (name, version, isDefault) in WslDistributions())
                list.Add(new("wsl:" + name, name, "∆", $"{Quote(wsl)} -d {name} --cd ~", null, true,
                    $"WSL {version}{(isDefault ? " · default" : "")}"));
        return list;
    }

    /// <summary>Distribution names are only accepted if they are plain identifiers (they go on a command line).</summary>
    public static bool ValidDistroName(string? n) => n is { Length: > 0 and <= 64 } && Regex.IsMatch(n, @"^[A-Za-z0-9][A-Za-z0-9._-]*$");

    public static List<(string Name, int Version, bool IsDefault)> WslDistributions()
    {
        var result = new List<(string, int, bool)>();
        try
        {
            using var lxss = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss");
            if (lxss == null) return result;
            var def = lxss.GetValue("DefaultDistribution") as string;
            foreach (var id in lxss.GetSubKeyNames())
            {
                using var k = lxss.OpenSubKey(id);
                var name = k?.GetValue("DistributionName") as string;
                if (!ValidDistroName(name) || name!.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase)) continue;
                if (k!.GetValue("State") is int state && state != 1) continue;   // 1 = installed; others are mid-install/uninstall
                int version = k.GetValue("Version") is int v ? v : 2;
                result.Add((name, version, string.Equals(id, def, StringComparison.OrdinalIgnoreCase)));
            }
        }
        catch { }
        return result.OrderByDescending(r => r.Item3).ThenBy(r => r.Item1, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Quick connect words: local, powershell/ps, pwsh, cmd, wsl, "wsl Ubuntu" / "wsl:Ubuntu".</summary>
    public static LocalShellProfile? FromQuick(string text)
    {
        var t = text.Trim();
        var m = Regex.Match(t, @"^wsl(?:[:\s]+(?<d>\S+))?$", RegexOptions.IgnoreCase);
        if (m.Success)
            return m.Groups["d"].Success ? Find("wsl:" + m.Groups["d"].Value) : All.FirstOrDefault(p => p.Wsl);
        return t.ToLowerInvariant() switch
        {
            "local" or "shell" => Find("pwsh") ?? Find("powershell") ?? Find("cmd"),
            "powershell" or "ps" => Find("powershell"),
            "pwsh" => Find("pwsh") ?? Find("powershell"),
            "cmd" => Find("cmd"),
            _ => null,
        };
    }

    private static string Quote(string path) => path.Contains(' ') ? "\"" + path + "\"" : path;

    private static string? OnPath(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try { var p = Path.Combine(dir.Trim(), exe); if (File.Exists(p)) return p; } catch { }
        }
        return null;
    }
}

/// <summary>A local shell or WSL distribution in a terminal tab. No host tools; a shell that exits is not restarted
/// automatically (press Enter for a new one).</summary>
public sealed class LocalTarget(LocalShellProfile profile) : SessionTarget
{
    public LocalShellProfile Profile => profile;
    public override string Kind => profile.Wsl ? "wsl" : "local";
    public override string Title => profile.Wsl ? "WSL · " + profile.Name : profile.Name;
    public override string Endpoint => profile.Wsl ? $"WSL · {profile.Name}" : $"this PC · {profile.Name}";
    public override string Glyph => profile.Glyph;
    public override SessionCaps Caps => SessionCaps.None;
    public override ITerminalTransport CreateTransport() => new ConPtyTransport(profile.CommandLine, profile.WorkingDir);
    public override SessionTarget Clone() => new LocalTarget(profile);
}
