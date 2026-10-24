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
        SendInput(LastTmuxLine + "\r");
        AuditLog.Write("you", "terminal", install ? "tmux installed (if missing) and started" : "tmux started", $"{h.Display} · session {Tmux.SessionName}" + (withConfig ? " · your config" : ""));
        _web.Focus();
    }
}
