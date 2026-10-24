using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DcxManager.Services;
using DcxManager.Ssh;
using Microsoft.Win32;

namespace DcxManager.Ide;

/// <summary>
/// Opening hosts, WSL distributions and folders in an IDE on this PC (0.5.0, ADR-065; DarkCipher0x: "ways to connect to VS Code
/// etc., not in the app"). Editors are found by their registered URL scheme and opened through it (no command line is
/// built). Remote-SSH needs a host alias: the app writes ONLY its own file, ~/.ssh/dcx_config (Host blocks with no secret),
/// and adds one "Include dcx_config" line to ~/.ssh/config with your consent; both are removed by "Remove". A host's vault
/// key is never written to disk: it is loaded into the Windows OpenSSH agent from stdin with a lifetime (ssh-add -t), its
/// passphrase answered through the app's own askpass pipe.
/// </summary>
public static class IdeIntegration
{
    public sealed record Editor(string Id, string Title, string Scheme);

    private static readonly Editor[] Known =
    {
        new("vscode", "Visual Studio Code", "vscode"), new("vscode-insiders", "VS Code Insiders", "vscode-insiders"),
        new("cursor", "Cursor", "cursor"), new("vscodium", "VSCodium", "vscodium"), new("windsurf", "Windsurf", "windsurf"),
        new("gateway", "JetBrains Gateway", "jetbrains-gateway"),
    };

    /// <summary>Editors whose URL scheme is registered for this user or machine.</summary>
    public static IReadOnlyList<Editor> Installed()
    {
        bool Has(string scheme)
        {
            try
            {
                using var k = Registry.ClassesRoot.OpenSubKey(scheme);
                return k?.GetValue("URL Protocol") != null;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return false; }
        }
        return Known.Where(e => Has(e.Scheme)).ToList();
    }

    public static Editor? Preferred(IReadOnlyList<Editor> installed) =>
        installed.FirstOrDefault(e => e.Id == AppSettings.Current.IdePreferred) ?? installed.FirstOrDefault(e => e.Id != "gateway") ?? installed.FirstOrDefault();

    // ---- addresses ---------------------------------------------------------------------------------------------------

    private static readonly Regex HostRx = new(@"^[A-Za-z0-9](?:[A-Za-z0-9.\-]{0,252})$|^[0-9A-Fa-f:.]{2,45}$", RegexOptions.CultureInvariant);
    private static readonly Regex UserRx = new(@"^[A-Za-z0-9._][A-Za-z0-9._\-@]{0,63}$", RegexOptions.CultureInvariant);

    /// <summary>The alias a host has in dcx_config: "dcx-" + its name, lower case, letters / digits / '-', with a short id
    /// suffix so two hosts of the same name never collide.</summary>
    public static string Alias(HostEntry h)
    {
        var slug = Regex.Replace(h.Display.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (slug.Length > 40) slug = slug[..40].Trim('-');
        return $"dcx-{(slug.Length > 0 ? slug : "host")}-{h.Id[..Math.Min(6, h.Id.Length)].ToLowerInvariant()}";
    }

    public static string? Problem(HostEntry h) =>
        h.Protocol != "ssh" ? "only SSH hosts open in an IDE" : !HostRx.IsMatch(h.Address) ? "the address can't go in an SSH config" :
        h.Username.Length > 0 && !UserRx.IsMatch(h.Username) ? "the user name can't go in an SSH config" : h.Port is < 1 or > 65535 ? "port" : null;

    /// <summary>The URI that opens <paramref name="path"/> (absolute, '/') on the host through Remote-SSH, or the gateway URL.</summary>
    public static string SshUri(Editor e, HostEntry h, string path = "")
    {
        if (Problem(h) is { } why) throw new ArgumentException(why);
        path = CleanRemotePath(path);
        if (e.Id == "gateway")
            return $"jetbrains-gateway://connect#type=ssh&deploy=false&host={Uri.EscapeDataString(h.Address)}&port={h.Port}&user={Uri.EscapeDataString(h.Username)}" + (path.Length > 1 ? "&projectPath=" + Uri.EscapeDataString(path) : "");
        return $"{e.Scheme}://vscode-remote/ssh-remote+{Alias(h)}{Escape(path.Length > 0 ? path : "/")}";
    }

    public static string WslUri(Editor e, string distro, string path = "")
    {
        if (!Sessions.LocalShells.ValidDistroName(distro)) throw new ArgumentException("distribution");
        if (e.Id == "gateway") throw new ArgumentException("JetBrains Gateway opens WSL from its own window.");
        return $"{e.Scheme}://vscode-remote/wsl+{distro}{Escape(CleanRemotePath(path) is { Length: > 0 } p ? p : "/")}";
    }

    public static string LocalUri(Editor e, string folder)
    {
        if (!Path.IsPathFullyQualified(folder) || folder.Any(char.IsControl)) throw new ArgumentException("folder");
        if (e.Id == "gateway") throw new ArgumentException("JetBrains Gateway is for remote projects.");
        return $"{e.Scheme}://file/{Escape(folder.Replace('\\', '/'))}";
    }

    private static string CleanRemotePath(string p)
    {
        p = (p ?? "").Trim();
        if (p.Length == 0) return "";
        if (p.Any(char.IsControl) || p.Length > 1024) throw new ArgumentException("path");
        return p.StartsWith('/') ? p : "/" + p.TrimStart('~', '/');
    }

    /// <summary>Each path segment escaped, '/' (and a drive's ':') kept.</summary>
    private static string Escape(string path) => string.Join("/", path.Split('/').Select(s => Uri.EscapeDataString(s).Replace("%3A", ":")));

    /// <summary>Opens a URI through its registered handler (only the schemes above get here).</summary>
    public static void Open(string uri)
    {
        if (!Known.Any(k => uri.StartsWith(k.Scheme + "://", StringComparison.Ordinal))) throw new ArgumentException("not an IDE link");
        Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
    }

    // ---- ~/.ssh/dcx_config ------------------------------------------------------------------------------------------

    public static string SshDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
    public static string ManagedConfig => Path.Combine(SshDir, "dcx_config");
    private const string IncludeLine = "Include dcx_config";
    private const string Marker = "# Added by DarkCipher ServerManagement (Settings → Integrations → IDE integration removes it)";

    /// <summary>The Host blocks for these hosts: address, user, port, Kerberos when the host uses it, a jump host when it is
    /// in the file too. No password, key or key path.</summary>
    public static string Render(IEnumerable<HostEntry> hosts)
    {
        var list = hosts.Where(h => Problem(h) == null).ToList();
        var ids = list.ToDictionary(h => h.Id);
        var sb = new StringBuilder("# Managed by DarkCipher ServerManagement: rewritten whenever you open a host in an IDE; edits here are lost.\n# No secrets: keys are loaded into ssh-agent for a limited time instead.\n\n");
        foreach (var h in list)
        {
            sb.Append("Host ").Append(Alias(h)).Append('\n');
            sb.Append("    HostName ").Append(h.Address).Append('\n');
            if (h.Username.Length > 0) sb.Append("    User ").Append(h.Username).Append('\n');
            sb.Append("    Port ").Append(h.Port).Append('\n');
            if (h.KerberosSignOn) sb.Append("    GSSAPIAuthentication yes\n    GSSAPIDelegateCredentials ").Append(AppSettings.Current.SsoKerberosDelegate ? "yes" : "no").Append('\n');
            if (h.JumpHostId != null && ids.TryGetValue(h.JumpHostId, out var j)) sb.Append("    ProxyJump ").Append(Alias(j)).Append('\n');
            sb.Append("    ServerAliveInterval ").Append(Math.Clamp(h.KeepAliveSeconds, 0, 3600)).Append('\n');
            sb.Append('\n');
        }
        return sb.ToString();
    }

    public static bool IncludePresent() => File.Exists(Path.Combine(SshDir, "config")) && File.ReadAllLines(Path.Combine(SshDir, "config")).Any(l => l.Trim().Equals(IncludeLine, StringComparison.OrdinalIgnoreCase));

    /// <summary>Writes dcx_config for every SSH host and (once, with <paramref name="mayAddInclude"/>) puts the Include line at
    /// the top of ~/.ssh/config, keeping a copy of the old file. Throws when the setting forbids it.</summary>
    public static void WriteConfig(IEnumerable<HostEntry> hosts, bool mayAddInclude)
    {
        if (!AppSettings.Current.IdeSshConfig) throw new InvalidOperationException("Writing an SSH config for IDEs is switched off on this PC (Settings → Integrations → IDE integration, or your organisation's policy).");
        Directory.CreateDirectory(SshDir);
        File.WriteAllText(ManagedConfig, Render(hosts), new UTF8Encoding(false));
        if (IncludePresent() || !mayAddInclude) return;
        var cfg = Path.Combine(SshDir, "config");
        var old = File.Exists(cfg) ? File.ReadAllText(cfg) : "";
        if (old.Length > 0) File.Copy(cfg, cfg + ".dcx-bak", overwrite: true);
        File.WriteAllText(cfg, Marker + "\n" + IncludeLine + "\n\n" + old, new UTF8Encoding(false));
    }

    /// <summary>Removes dcx_config and the two lines this app added to ~/.ssh/config.</summary>
    public static void Remove()
    {
        try { File.Delete(ManagedConfig); } catch (IOException) { }
        var cfg = Path.Combine(SshDir, "config");
        if (!File.Exists(cfg)) return;
        var lines = File.ReadAllLines(cfg).ToList();
        int n = lines.RemoveAll(l => l.Trim() == Marker || l.Trim().Equals(IncludeLine, StringComparison.OrdinalIgnoreCase));
        if (n > 0) File.WriteAllText(cfg, string.Join("\n", lines).TrimStart('\n') + "\n", new UTF8Encoding(false));
    }

    // ---- ssh-agent ---------------------------------------------------------------------------------------------------

    /// <summary>Is the Windows "OpenSSH Authentication Agent" service running?</summary>
    public static async Task<bool> AgentRunningAsync()
    {
        var sc = Path.Combine(Environment.SystemDirectory, "sc.exe");
        if (!File.Exists(sc)) return false;
        var psi = new ProcessStartInfo(sc) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        psi.ArgumentList.Add("query"); psi.ArgumentList.Add("ssh-agent");
        using var p = Process.Start(psi);
        if (p == null) return false;
        var text = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return text.Contains("RUNNING", StringComparison.Ordinal);
    }

    /// <summary>Loads a vault key into ssh-agent for <paramref name="minutes"/> (ssh-add -t … - reads it from stdin; a
    /// passphrase is answered through the askpass pipe). Nothing is written to disk.</summary>
    public static async Task<string> AgentAddAsync(Credential key, int minutes, CancellationToken ct = default)
    {
        if (key.Kind != CredentialKind.PrivateKey) throw new ArgumentException("not a key");
        if (key.Secret.Contains("PuTTY-User-Key-File")) throw new InvalidOperationException("This is a PuTTY .ppk key: OpenSSH's agent can't read it. Convert it in the credential editor first.");
        var add = Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh-add.exe");
        if (!File.Exists(add)) throw new InvalidOperationException("Windows' OpenSSH client isn't installed (Settings → System → Optional features → OpenSSH Client).");
        if (!await AgentRunningAsync()) throw new InvalidOperationException("The “OpenSSH Authentication Agent” service isn't running. Start it once as administrator: Set-Service ssh-agent -StartupType Manual; Start-Service ssh-agent");
        var helper = KeyExport.AskpassProgram() ?? throw new InvalidOperationException("The app's own executable isn't known.");
        var pipe = "dcx-askpass-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(1));
        var server = string.IsNullOrEmpty(key.Passphrase) ? Task.CompletedTask : KeyExport.ServeAsync(pipe, _ => key.Passphrase, cts.Token);
        var psi = new ProcessStartInfo(add) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("-t"); psi.ArgumentList.Add((Math.Clamp(minutes, 1, 1440) * 60).ToString()); psi.ArgumentList.Add("-");
        psi.Environment["SSH_ASKPASS"] = helper;
        psi.Environment["SSH_ASKPASS_REQUIRE"] = "force";
        psi.Environment["DISPLAY"] = "dcx:0";
        psi.Environment[KeyExport.PipeVariable] = pipe;
        psi.Environment.Remove("DCX_DATA_DIR");
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("ssh-add didn't start");
        await p.StandardInput.WriteAsync(key.Secret.Replace("\r\n", "\n").TrimEnd() + "\n");
        p.StandardInput.Close();
        var err = p.StandardError.ReadToEndAsync(cts.Token);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch (InvalidOperationException) { } throw new TimeoutException("ssh-add didn't finish."); }
        cts.Cancel();
        try { await server; } catch (OperationCanceledException) { }
        if (p.ExitCode != 0) throw new InvalidOperationException("ssh-add couldn't load the key: " + (await err).Trim());
        return $"{key.Name} is in ssh-agent for {minutes} minutes.";
    }
}
