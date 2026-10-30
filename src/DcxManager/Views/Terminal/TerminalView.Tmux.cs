using System.Windows;
using DcxManager.Services;
using DcxManager.Sessions;
using DcxManager.Ssh;

namespace DcxManager.Views;

// TerminalView: the ⧉ tmux button (0.6.6, ADR-085).
public sealed partial class TerminalView
{
    /// <summary>The button is offered on SSH sessions to Linux and macOS hosts.</summary>
    /// <summary>Self-test: the ⧉ tmux button is offered here.</summary>
    internal bool TmuxButtonShown => TmuxApplies;

    private bool TmuxApplies => _target is SshTarget && _target.Host is { } h && h.EffectivePlatform is not ("windows" or "network");

    /// <summary>Self-test: the last line typed by the button.</summary>
    internal string? LastTmuxLine { get; private set; }

    /// <summary>Into tmux on this host: your config written to its own file, tmux installed when it is missing (you are
    /// asked first; sudo prompts in the terminal), then your session attached or created.</summary>
    internal async Task StartTmuxAsync()
    {
        if (!TmuxApplies || _target.Host is not { } h) return;
        if (!IsConnected) { SetStatus("Connect first, then ⧉ tmux."); return; }
        var s = AppSettings.Current;
        bool? present = null;
        bool withConfig = false;
        try
        {
            var probe = await SshPool.RunAsync(h, Tmux.ProbeCommand, timeout: TimeSpan.FromSeconds(20));
            present = probe.Output.Trim() == "yes";
            if (Tmux.HasConfig)
            {
                var w = await SshPool.RunAsync(h, Tmux.WriteConfigCommand(s.TmuxConfig), timeout: TimeSpan.FromSeconds(20));
                withConfig = w.Output.Contains("written");
                if (!withConfig) SetStatus("Couldn't write your tmux config on this host: " + w.Combined.Trim());
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The side channel isn't available (e.g. a sign-in that can't be repeated): the typed line checks for itself.
            App.Log("tmux: probe / config", ex);
        }
        bool install = false;
        if (present != true)
        {
            if (!s.TmuxInstall) install = false;
            else if (present == false)
            {
                install = Dialogs.Confirm("tmux", $"tmux isn't installed on {h.Display}. Install it with the host's package manager (apt, dnf, yum, zypper, pacman, apk or brew)? You may be asked for your sudo password in the terminal.",
                    "Install and start tmux", banner: Envs.IsProduction(Envs.For(h)) ? (Envs.Production, h.Display) : null);
                if (!install) return;
            }
            else install = true;   // unknown: the line installs only if it really is missing
        }
        LastTmuxLine = Tmux.AttachLine(install, withConfig);
        TmuxSession = Tmux.SessionName;
        SendInput(LastTmuxLine + "\r");
        AuditLog.Write("you", "terminal", install ? "tmux installed (if missing) and started" : "tmux started", $"{h.Display} · session {Tmux.SessionName}" + (withConfig ? " · your config" : ""));
        _web.Focus();
    }

    // ---- saved sessions (0.6.29) ---------------------------------------------------------------------------------

    /// <summary>The tmux session this terminal is in (entered with ⧉ tmux, or restored): re-attached after every
    /// reconnect, until tmux says it was left ("[detached]" / "[exited]").</summary>
    internal string? TmuxSession { get; private set; }
    private string? _restoreCwd;

    /// <summary>A restored tab: the folder to go back to once connected, and the tmux session to re-attach.</summary>
    internal void RestorePlace(string? cwd, string? tmux)
    {
        _restoreCwd = cwd;
        TmuxSession = TmuxApplies && Tmux.ValidName(tmux) ? tmux : null;
    }

    /// <summary>What a saved session keeps of this terminal: its working folder (not inside tmux, which keeps its own) and
    /// its tmux session. Read from the output only; nothing is typed into the shell.</summary>
    internal (string? Cwd, string? Tmux) Place()
    {
        if (TmuxSession != null) return (null, TmuxSession);
        var cwd = Shell.Length > 0 ? ShellPlace.FromOutput(RecentOutput(200_000), Shell) : null;
        return (cwd ?? (IsConnected ? null : _restoreCwd), null);
    }

    /// <summary>Output watch: leaving tmux ends the re-attach (and always-on tmux leaves this tab alone from then on).</summary>
    private void WatchTmux(string s)
    {
        if (TmuxSession != null && s.Contains('[') && ShellPlace.LeftTmux(s)) { TmuxSession = null; _leftTmux = true; }
    }

    // ---- always-on tmux (0.6.29) ---------------------------------------------------------------------------------

    /// <summary>The host's own choice ("on" / "off", possibly from its folder's defaults), else Settings → Terminal → tmux.</summary>
    internal bool AutoTmux => TmuxApplies && _target.Host is { } h && h.Tmux switch { "on" => true, "off" => false, _ => AppSettings.Current.TmuxAlways };
    /// <summary>You left tmux in this tab ("[detached]" / "[exited]"): reconnects stay in the plain shell.</summary>
    private bool _leftTmux;

    /// <summary>After a connect: into tmux when always-on applies and nothing else is planned for the shell.</summary>
    private void MaybeAutoTmux()
    {
        if (TmuxSession == null && !_leftTmux && string.IsNullOrEmpty(_initialCommand) && AutoTmux) TmuxSession = Tmux.SessionName;
    }

    /// <summary>After a connect: re-attach tmux (no questions: never installs; the config is written again when there is
    /// one), otherwise go back to the restored folder once.</summary>
    private async Task AfterConnectAsync()
    {
        if (TmuxSession is { } name && _target.Host is { } h)
        {
            bool withConfig = false, present = true;
            try
            {
                present = (await SshPool.RunAsync(h, Tmux.ProbeCommand, timeout: TimeSpan.FromSeconds(20))).Output.Trim() == "yes";
                if (present && Tmux.HasConfig)
                    withConfig = (await SshPool.RunAsync(h, Tmux.WriteConfigCommand(AppSettings.Current.TmuxConfig), timeout: TimeSpan.FromSeconds(20))).Output.Contains("written");
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { App.Log("tmux: re-attach probe", ex); }
            if (!present && IsConnected)
            {
                TmuxSession = null;
                SetStatus($"● {_target.Endpoint} · tmux isn't installed on this host: ⧉ tmux installs it (asking first)");
            }
            if (!present || !IsConnected) { _restoreCwd = null; return; }
            LastTmuxLine = Tmux.AttachLine(false, withConfig, name);
            // Nothing awaits this task: a transport that died between the probe and the write is logged, not left unobserved.
            try { _transport?.Write(LastTmuxLine + "\r"); } catch (Exception ex) { App.Log("tmux: re-attach", ex); }
            _restoreCwd = null;
            return;
        }
        if (ShellPlace.ChangeDirLine(_restoreCwd, Shell) is { } cd)
        {
            await Task.Delay(400);
            try { if (IsConnected) _transport?.Write(cd + "\r"); } catch (Exception ex) { App.Log("saved session: folder", ex); }
        }
        _restoreCwd = null;
    }
}
