using System.Text;
using System.Text.RegularExpressions;
using DcxManager.Services;

namespace DcxManager.Ssh;

/// <summary>
/// tmux in a terminal (0.6.6, ADR-085; DarkCipher0x: "press the tmux button in the terminal window and it will launch me into
/// tmux... if it doesnt have it, it will install it. but have in the settings a field i can paste my custom tmux
/// config"). Builds what the terminal's ⧉ tmux button does:
/// <list type="bullet">
/// <item>your config (Settings → Terminal → tmux) goes to its own file, <see cref="ConfigPath"/> (directory 700, file 600),
/// written over the SSH connection's exec channel as base64 — never your ~/.tmux.conf;</item>
/// <item>one shell line typed into the session: install tmux with the host's package manager when it is missing (only when
/// you agreed; sudo asks for its password right there in the terminal), then attach to your session or create it, with
/// your config.</item>
/// </list>
/// The session name is checked (letters, digits, _ . -), so nothing from the settings reaches the shell unquoted.
/// </summary>
public static class Tmux
{
    public const string ConfigDir = "$HOME/.config/dcx-servermanagement";
    public const string ConfigPath = ConfigDir + "/tmux.conf";
    public const int MaxConfigBytes = 64 * 1024;
    private static readonly Regex NameRx = new("^[A-Za-z0-9_.-]{1,32}$");

    public static bool ValidName(string? s) => s != null && NameRx.IsMatch(s);
    public static string SessionName => ValidName(AppSettings.Current.TmuxSession) ? AppSettings.Current.TmuxSession : "main";
    public static bool HasConfig => AppSettings.Current.TmuxConfig.Trim().Length > 0;

    /// <summary>The exec-channel command that says whether tmux is there ("yes" / "no").</summary>
    public const string ProbeCommand = "command -v tmux >/dev/null 2>&1 && echo yes || echo no";

    /// <summary>The exec-channel command that writes your config (base64, so no character of it is ever interpreted).</summary>
    public static string WriteConfigCommand(string config)
    {
        var bytes = Encoding.UTF8.GetBytes(config.Replace("\r\n", "\n"));
        if (bytes.Length > MaxConfigBytes) throw new ArgumentException($"The tmux config is larger than {MaxConfigBytes / 1024} KB.");
        var b64 = Convert.ToBase64String(bytes);   // A–Z a–z 0–9 + / = only: safe inside single quotes
        return $"umask 077 && mkdir -p \"{ConfigDir}\" && printf '%s' '{b64}' | base64 -d > \"{ConfigPath}\" && echo written";
    }

    /// <summary>Install tmux when it is missing: the host's package manager, with sudo unless you are root.</summary>
    public const string InstallSnippet =
        "command -v tmux >/dev/null 2>&1 || { S=; [ \"$(id -u)\" = 0 ] || S=sudo; " +
        "if command -v apt-get >/dev/null 2>&1; then $S apt-get update && $S apt-get install -y tmux; " +
        "elif command -v dnf >/dev/null 2>&1; then $S dnf install -y tmux; " +
        "elif command -v yum >/dev/null 2>&1; then $S yum install -y tmux; " +
        "elif command -v zypper >/dev/null 2>&1; then $S zypper --non-interactive install tmux; " +
        "elif command -v pacman >/dev/null 2>&1; then $S pacman -S --noconfirm tmux; " +
        "elif command -v apk >/dev/null 2>&1; then $S apk add tmux; " +
        "elif command -v brew >/dev/null 2>&1; then brew install tmux; " +
        "else echo 'No supported package manager found: install tmux yourself.'; fi; }; ";

    /// <summary>The line typed into the terminal: (install,) then attach to / create the session — with your config when
    /// there is one (reloaded into a tmux server that is already running).</summary>
    public static string AttachLine(bool install, bool withConfig)
    {
        var name = SessionName;
        var attach = withConfig
            ? $"tmux source-file \"{ConfigPath}\" 2>/dev/null; tmux -f \"{ConfigPath}\" new-session -A -s {name}"
            : $"tmux new-session -A -s {name}";
        var run = $"if [ -n \"$TMUX\" ]; then echo 'Already inside tmux.'; elif command -v tmux >/dev/null 2>&1; then {attach}; else echo 'tmux is not installed on this host.'; fi";
        return (install ? InstallSnippet : "") + run;
    }
}
