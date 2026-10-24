using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using DcxManager.Services;
using DcxManager.Ssh;
using DcxManager.Views;
using DcxManager.Vultr;

namespace DcxManager;

// MainWindow: opening terminals, remote desktop, files, notes and incidents; quick connect (moved unchanged from MainWindow.xaml.cs in 0.2.0 phase 6½).
public partial class MainWindow
{
    // ---- open what's open (0.5.9) -----------------------------------------------------------------------------

    /// <summary>What a tab is "the same thing" as (0.5.9, <see cref="AppSettings.ReuseOpenTabs"/>): a host's terminal,
    /// Remote Desktop, VNC desktop, files or commander, or a local shell profile. Null for anything that is never reused.</summary>
    internal static string? ReuseKey(object? content) => content switch
    {
        TerminalView t => t.Target switch
        {
            Sessions.LocalTarget l => "term:local:" + l.Profile.Id,
            _ => HostKey(t.Host) is { } k ? $"term:{t.Target.GetType().Name}:{k}" : null,
        },
        RdpView r => HostKey(r.Host) is { } k ? "rdp:" + k : null,
        VncView v => HostKey(v.Host) is { } k ? "vnc:" + k : null,
        SftpView f => HostKey(f.Host) is { } k ? "files:" + k : null,
        CommanderView c => c.OpenedForHostId is { } id ? "commander:" + id : null,
        _ => null,
    };

    /// <summary>Whether a terminal, Remote Desktop or VNC tab of <paramref name="host"/> is open.</summary>
    internal bool HasOpenSession(HostEntry host)
    {
        var k = HostKey(host);
        return k != null && Panes.AllTabs.Any(t => ReuseKey(t.Content) is { } key && (key.StartsWith("term:", StringComparison.Ordinal) || key.StartsWith("rdp:", StringComparison.Ordinal) || key.StartsWith("vnc:", StringComparison.Ordinal)) && key.EndsWith(":" + k, StringComparison.Ordinal));
    }

    /// <summary>A saved host by its id; an ad-hoc one (quick connect) by its endpoint.</summary>
    private static string? HostKey(HostEntry? h) => h == null ? null : h.IsAdHoc ? "adhoc:" + h.Endpoint.ToLowerInvariant() : h.Id;

    /// <summary>When reusing is on (and Shift isn't held, and <paramref name="forceNew"/> isn't set), switches to an open tab
    /// with <paramref name="key"/> and returns true; otherwise false, and the caller opens a new one.</summary>
    internal bool SwitchToOpen(string? key, bool forceNew)
    {
        if (key == null || forceNew || !AppSettings.Current.ReuseOpenTabs || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return false;
        if (Panes.AllTabs.FirstOrDefault(t => ReuseKey(t.Content) == key) is not { } tab) return false;
        Panes.Select(tab);
        bool live = tab.Content switch { TerminalView t => t.IsConnected, RdpView r => r.IsConnected, VncView v => v.IsConnected, SftpView f => f.IsConnected, _ => true };
        Status($"Switched to the open tab “{tab.Header}”{(live ? "" : " (disconnected: press Enter or ↻ Reconnect)")}. Shift-click, or right-click → New connection, opens another.");
        return true;
    }

    // ---- sessions -----------------------------------------------------------------------------------------------

    public void OpenTerminal(HostEntry host, string? initialCommand = null, bool connectOnOpen = true, bool forceNew = false)
    {
        // 0.5.9: an open session of this host is switched to, unless a command is to run in a fresh one.
        string kind = host.Protocol == "rdp" ? "" : host.IsVnc ? "" : host.IsTelnet ? "TelnetTarget" : host.KerberosSignOn ? "" : "SshTarget";
        if (kind.Length > 0 && initialCommand == null && SwitchToOpen($"term:{kind}:{HostKey(host)}", forceNew)) return;
        if (!HostGate(host, "connect to", () => OpenTerminal(host, initialCommand, connectOnOpen, forceNew))) return;
        // 0.5.0 (#16): a host linked to a Password Safe account checks its password out into memory first.
        // 0.6.17 (ADR-096): never for a shared library host (its links would read this user's vault for a host another chose).
        if (host.PasswordSafeAccount.Length > 0 && host.Transient == null && !host.IsShared) { _ = OpenWithPasswordSafeAsync(host, initialCommand, connectOnOpen); return; }
        // 0.6.14 (ADR-093): a host linked to a Bitwarden item reads its password or key into memory first.
        if (host.BitwardenItem.Length > 0 && host.Transient == null && !host.IsShared) { _ = OpenWithBitwardenAsync(host, initialCommand, connectOnOpen); return; }
        if (host.Protocol == "rdp") { OpenRdp(host, forceNew: forceNew); return; }
        if (host.IsVnc) { OpenVnc(host, forceNew); return; }
        if (host.IsTelnet) { AddTab("⌁ " + host.Display, new TerminalView(new Sessions.TelnetTarget(host), null, connectOnOpen)); return; }
        // 0.5.0 (ADR-062): Kerberos single sign-on runs Windows' OpenSSH in the tab (the built-in library has no GSSAPI).
        if (host.KerberosSignOn)
        {
            if (Ssh.KerberosSsh.Profile(host, out var why) is not { } kp) { Dialogs.Error("Kerberos sign-on", why ?? "not available"); return; }
            if (initialCommand == null && SwitchToOpen("term:local:" + kp.Id, forceNew)) return;
            void Open() { AddTab("🔑 " + host.Display, new TerminalView(new Sessions.LocalTarget(kp), null, connectOnOpen)); AuditLog.Write("you", host.Display, "opened SSH with Kerberos sign-on", "Windows OpenSSH, GSSAPI"); }
            if (Offline.On) Offline.Run(host.Address, "SSH (Kerberos)", Open); else Open();
            return;
        }
        AddTab("⌨ " + host.Display, new TerminalView(host, initialCommand, connectOnOpen));
    }

    private async Task OpenWithPasswordSafeAsync(HostEntry host, string? initialCommand, bool connectOnOpen)
    {
        Status($"Checking out {host.Display}'s credential from Password Safe…");
        try
        {
            var (h, c) = await PasswordSafeSessions.PrepareAsync(host);
            var before = Panes.AllTabs.ToHashSet();
            OpenTerminal(h, initialCommand, connectOnOpen);
            if (c != null)
            {
                if (Panes.AllTabs.FirstOrDefault(t => !before.Contains(t)) is { Content: { } view }) { PasswordSafeSessions.Attach(view, c); Status($"Password Safe: {c.Label} checked out until {c.Until:HH:mm}; checked in when the tab closes."); }
                else
                {
                    // Opened in mstsc (RD Gateway, or the embedded client switched off): the password went to Windows for
                    // that sign-in only (RdpLauncher deletes it after 25 s). Check it in two minutes later, after the sign-in,
                    // so a rotate-on-check-in policy can't change it mid-logon.
                    _ = Task.Delay(TimeSpan.FromMinutes(2)).ContinueWith(_ => PasswordSafeSessions.ReleaseAsync(c, "opened in mstsc"), TaskScheduler.Default);
                    Status($"Password Safe: {c.Label} handed to Remote Desktop Connection for its sign-in; checked in again in two minutes.");
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or TimeoutException or System.Net.Http.HttpRequestException or OfflineBlockedException)
        { Dialogs.Error("Password Safe", $"{host.Display}: {ex.Message}"); }
    }

    private async Task OpenWithBitwardenAsync(HostEntry host, string? initialCommand, bool connectOnOpen)
    {
        Status($"Reading {host.Display}'s sign-in from Bitwarden…");
        try
        {
            var h = await BitwardenSessions.PrepareAsync(host);
            if (h.Transient == null) { Status("The host's Bitwarden link isn't valid: link it again (VAULT → Bitwarden)."); return; }
            OpenTerminal(h, initialCommand, connectOnOpen);
            Status($"Signed in with {BitwardenSessions.Describe(host.BitwardenItem)} (held in memory for this session only).");
        }
        catch (OperationCanceledException) { Status("Bitwarden stayed locked: not connected."); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or TimeoutException or OfflineBlockedException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception or System.IO.IOException)
        { Dialogs.Error("Bitwarden", $"{host.Display}: {ex.Message}"); }
    }

    /// <summary>0.5.1 (ADR-067): a locked host (or one in a locked folder) asks first; once unlocked the open goes ahead.
    /// Returns true when it may open now.</summary>
    public bool HostGate(HostEntry host, string what, Action retry)
    {
        // The host (and its folders), its jump host and the credential it signs in with: everything the connection will use.
        var targets = new List<string>();
        if (!host.IsAdHoc) targets.Add("host:" + host.Id);
        if (host.JumpHostId is { Length: > 0 } j) targets.Add("host:" + j);
        if (host.Transient == null && host.CredentialId is { Length: > 0 } c) targets.Add("cred:" + c);
        if (targets.All(Locks.IsOpen)) return true;
        _ = Dispatcher.InvokeAsync(async () =>
        {
            foreach (var t in targets) if (!await LockGate.EnsureAsync(t, what + " " + host.Display)) return;
            retry();
        });
        return false;
    }

    /// <summary>Files, Manage and Commander speak SSH: a Telnet (or RDP) host is refused with the reason.</summary>
    private bool NeedsSsh(HostEntry host, string what)
    {
        if (host.IsSsh) return true;
        Status($"{what} needs SSH — {host.Display} is a {(host.IsTelnet ? "Telnet" : "Remote Desktop")} host.");
        return false;
    }

    /// <summary>Any terminal session (SSH host, local shell, WSL) in a new tab.</summary>
    public void OpenSession(Sessions.SessionTarget target, bool connectOnOpen = true, bool forceNew = false)
    {
        if (target.Host is { } h) { OpenTerminal(h, connectOnOpen: connectOnOpen, forceNew: forceNew); return; }
        if (target is Sessions.LocalTarget l && SwitchToOpen("term:local:" + l.Profile.Id, forceNew)) return;   // 0.5.9
        AddTab(target.Glyph + " " + target.Title, new TerminalView(target, null, connectOnOpen));
    }

    /// <summary>Remote Desktop: embedded in a tab (ADR-013) unless switched off, the host uses an RD Gateway, the
    /// control isn't available, or <paramref name="external"/> asks for mstsc.</summary>
    public void OpenRdp(HostEntry host, bool external = false, bool forceNew = false)
    {
        if (!external && SwitchToOpen("rdp:" + HostKey(host), forceNew)) return;   // 0.5.9
        if (!HostGate(host, "connect to", () => OpenRdp(host, external, forceNew))) return;
        // 0.3.0 §24: a host that connects through a VPN gets its tunnel first (SSH does this inside the connector).
        if (host.VpnProfileId is { Length: > 0 } && AppData.Current.VpnProfiles.Any(p => p.Id == host.VpnProfileId && !VpnManager.IsUp(p)))
        {
            Status($"Bringing up the VPN for {host.Display}…");
            _ = VpnManager.EnsureForHostAsync(host, CancellationToken.None).ContinueWith(t => Dispatcher.BeginInvoke(() =>
            {
                if (t.IsFaulted) { Status("VPN: " + (t.Exception?.InnerException?.Message ?? "failed")); return; }
                var h2 = host.Clone(); h2.VpnProfileId = null;   // the tunnel is up: open without going round again
                OpenRdp(h2, external, forceNew);
            }), TaskScheduler.Default);
            return;
        }
        if (external || !AppSettings.Current.RdpEmbedded || host.Rdp.Gateway.Trim().Length > 0 || Rdp.RdpAxHost.AvailableClsid() == null)
        {
            RdpLauncher.Launch(host);
            return;
        }
        try { AddTab("🖥 " + host.Display, new RdpView(host)); }
        catch (Exception ex) { App.Log("RDP embedded", ex); Status("Embedded Remote Desktop unavailable (" + ex.Message + ") — opening mstsc."); RdpLauncher.Launch(host); }
    }

    /// <summary>A VNC desktop in a tab (0.5.1 #19, ADR-069): a Mac or a Linux desktop, through SSH by default.</summary>
    public void OpenVnc(HostEntry host, bool forceNew = false)
    {
        if (SwitchToOpen("vnc:" + HostKey(host), forceNew)) return;   // 0.5.9
        if (!HostGate(host, "connect to", () => OpenVnc(host, forceNew))) return;
        // A direct connection through a VPN gets its tunnel first (through SSH, the SSH connector does this).
        if (host.Vnc.Through != "ssh" && host.VpnProfileId is { Length: > 0 } && AppData.Current.VpnProfiles.Any(p => p.Id == host.VpnProfileId && !VpnManager.IsUp(p)))
        {
            Status($"Bringing up the VPN for {host.Display}…");
            _ = VpnManager.EnsureForHostAsync(host, CancellationToken.None).ContinueWith(t => Dispatcher.BeginInvoke(() =>
            {
                if (t.IsFaulted) { Status("VPN: " + (t.Exception?.InnerException?.Message ?? "failed")); return; }
                var h2 = host.Clone(); h2.VpnProfileId = null;
                OpenVnc(h2, forceNew);
            }), TaskScheduler.Default);
            return;
        }
        AddTab("🖵 " + host.Display, new VncView(host));
    }

    public void OpenLocal(Sessions.LocalShellProfile profile, bool connectOnOpen = true, bool forceNew = false) => OpenSession(new Sessions.LocalTarget(profile), connectOnOpen, forceNew);

    /// <summary>Docs (0.6.12): the documents pinned to a host / host folder / infrastructure provider, or the Docs screen.</summary>
    public void OpenDocs(string? pin = null, string? label = null)
    {
        Navigate("docs");
        if (pin != null && DocsView.Current is { } v) v.Dispatcher.BeginInvoke(() => v.ShowPinned(pin, label ?? Docs.DocStore.PinLabel(pin)), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Notes: a host's notebook, a specific note, or just the notes screen.</summary>
    public void OpenNotes(string? hostId = null, Notes.Note? note = null)
    {
        Navigate("notes");
        if (NotesView.Current is not { } v) return;
        if (note != null) v.Dispatcher.BeginInvoke(() => { v.Refresh(); v.Open(note); }, System.Windows.Threading.DispatcherPriority.Loaded);
        else if (hostId != null) v.Dispatcher.BeginInvoke(() => v.ShowHost(hostId), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Incidents screen, optionally showing one incident.</summary>
    public void OpenIncidents(Incidents.Incident? incident = null)
    {
        Navigate("incidents");
        if (incident != null && IncidentsView.Current is { } v) v.Dispatcher.BeginInvoke(() => { v.Refresh(); v.Open(incident); }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    public void OpenServer(HostEntry host)
    {
        if (!HostGate(host, "manage", () => OpenServer(host))) return;
        if (host.IsTelnet && !NeedsSsh(host, "Manage")) return;
        OpenSingleton("server:" + host.Id, "▣ " + host.Display, () => new ServerView(host));
    }

    public void OpenFiles(HostEntry host, string? path = null, bool forceNew = false)
    {
        if (path == null && SwitchToOpen("files:" + HostKey(host), forceNew)) return;   // 0.5.9 (a given folder opens its own tab)
        if (!HostGate(host, "open the files of", () => OpenFiles(host, path, forceNew))) return;
        if (host.IsTelnet && !NeedsSsh(host, "Files")) return;
        AddTab("⇅ " + host.Display, new SftpView(host, path));
    }

    /// <summary>Two-pane file manager with this PC on the left and <paramref name="host"/> on the right.</summary>
    public void OpenCommander(HostEntry host, string? path = null, bool forceNew = false)
    {
        if (path == null && SwitchToOpen("commander:" + host.Id, forceNew)) return;   // 0.5.9
        if (!HostGate(host, "open the files of", () => OpenCommander(host, path, forceNew))) return;
        if (host.IsTelnet && !NeedsSsh(host, "The file commander")) return;
        AddTab("⇆ " + host.Display, new CommanderView(host, path));
    }

    public void Status(string text) => StatusText.Text = text;

    private void OnQuickConnectAdvanced(object sender, RoutedEventArgs e)
    {
        bool rdp = _quickProto == "rdp";
        var seed = ParseQuick(QuickConnect.Text.Trim(), rdp) is { IsAdHoc: true } h ? h : rdp ? new HostEntry { Protocol = "rdp", Port = 3389, Username = "Administrator", Auth = AuthMethod.Password, IsAdHoc = true } : null;
        var win = new HostEditWindow(seed, adHoc: true) { Owner = this };
        if (win.ShowDialog() == true && win.Result is { } host)
        {
            if (win.ConnectAs == "rdp") OpenRdp(host);
            else if (win.ConnectAs == "vnc") OpenVnc(host);
            else if (win.ConnectAs == "files") OpenFiles(host);
            else if (win.ConnectAs == "manage") OpenServer(host);
            else if (win.ConnectAs == "external") ExternalSsh.Launch(host);
            else OpenTerminal(host);
        }
    }

    /// <summary>"telnet host", "telnet host 2323", "telnet host:2323" or "telnet://host:2323" → an ad-hoc Telnet host.</summary>
    public static HostEntry? ParseTelnet(string text)
    {
        var m = Regex.Match(text.Trim(), @"^(?:telnet://(?<h>\[[0-9a-fA-F:]+\]|[^:\s/]+)(?::(?<p>\d{1,5}))?/?|telnet\s+(?<h>\[[0-9a-fA-F:]+\]|[^:\s]+)(?:[:\s]\s*(?<p>\d{1,5}))?)$", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        int port = m.Groups["p"].Success ? int.Parse(m.Groups["p"].Value) : 23;
        if (port is < 1 or > 65535) return null;
        var address = m.Groups["h"].Value.Trim('[', ']');
        return new HostEntry { Name = port == 23 ? address : $"{address}:{port}", Address = address, Port = port, Username = "", Protocol = "telnet", IsAdHoc = true, Auth = AuthMethod.Password };
    }

    /// <summary>A saved host by name/address, or an ad-hoc entry from user@host:port.</summary>
    public static HostEntry? ParseQuick(string text, bool rdp = false)
    {
        if (text.Length == 0) return null;
        // rdp://user@host and mstsc user@host also mean Remote Desktop
        if (text.StartsWith("rdp://", StringComparison.OrdinalIgnoreCase)) { rdp = true; text = text[6..]; }
        else if (text.StartsWith("mstsc ", StringComparison.OrdinalIgnoreCase)) { rdp = true; text = text[6..].Trim(); }
        else if (text.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)) text = text[6..];
        var saved = AppData.Current.Hosts.FirstOrDefault(h => h.Name.Equals(text, StringComparison.OrdinalIgnoreCase));
        if (saved != null) return saved;
        var m = Regex.Match(text, @"^(?:(?<u>[^@\s]+)@)?(?<h>\[[0-9a-fA-F:]+\]|[^:\s@]+)(?::(?<p>\d{1,5}))?$");
        if (!m.Success) return null;
        var address = m.Groups["h"].Value.Trim('[', ']');
        var user = m.Groups["u"].Success ? m.Groups["u"].Value : rdp ? "Administrator" : AppSettings.Current.DefaultUsername;
        var port = m.Groups["p"].Success ? int.Parse(m.Groups["p"].Value) : rdp ? 3389 : 22;
        var match = AppData.Current.Hosts.FirstOrDefault(h => h.Address == address && h.Port == port && (!m.Groups["u"].Success || h.Username == user));
        if (match != null) return match;
        if (rdp) return new HostEntry { Name = $"{user}@{address}", Address = address, Port = port, Username = user, IsAdHoc = true, Protocol = "rdp", Auth = AuthMethod.Password };
        return new HostEntry
        {
            Name = $"{user}@{address}", Address = address, Port = port, Username = user, IsAdHoc = true,
            Auth = AppSettings.Current.DefaultKeyCredentialId != null ? AuthMethod.PrivateKey : AuthMethod.Password,
            CredentialId = AppSettings.Current.DefaultKeyCredentialId,
        };
    }
}
