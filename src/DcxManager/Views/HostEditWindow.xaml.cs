using System.IO;
using System.Windows;
using System.Windows.Controls;
using DcxManager.Services;
using DcxManager.Ssh;
using Microsoft.Win32;

namespace DcxManager.Views;

public partial class HostEditWindow : Window
{
    private sealed record Choice(string Display, Credential? Cred, bool Inline = false);
    private sealed record HostChoice(string Display, HostEntry? Host);
    public sealed record TermProfileChoice(string Display, string? Id);
    public sealed record VpnChoice(string Display, string? Id);

    private readonly HostEntry _host;
    private readonly bool _adHoc;

    public HostEntry? Result { get; private set; }
    /// <summary>For ad-hoc: terminal | files | manage | external.</summary>
    public string ConnectAs { get; private set; } = "terminal";

    public HostEditWindow(HostEntry? host, bool adHoc = false, bool seedIsNew = false)
    {
        InitializeComponent();
        Typography.Size(this, 640);
        ThemeManager.TrackScale(this, Root);
        _adHoc = adHoc;
        _host = host?.Clone() ?? new HostEntry { Username = AppSettings.Current.DefaultUsername };

        Heading.Text = adHoc ? "Ad-hoc connection" : host == null || seedIsNew ? "Add host" : "Edit host";
        KeepAliveBox.ItemsSource = new[] { "0", "15", "30", "60", "120" };
        KeepAliveBox.Text = _host.KeepAliveSeconds.ToString();
        RdpAudio.ItemsSource = new[] { "on this PC", "on the server", "off" };
        var r = _host.Rdp;
        RdpDomain.Text = r.Domain; RdpGateway.Text = r.Gateway;
        RdpFull.IsChecked = r.FullScreen; RdpW.Text = r.Width.ToString(); RdpH.Text = r.Height.ToString();
        RdpMulti.IsChecked = r.MultiMonitor; RdpAdmin.IsChecked = r.Admin; RdpClip.IsChecked = r.Clipboard; RdpDrives.IsChecked = r.Drives;
        RdpPrinters.IsChecked = r.Printers; RdpMic.IsChecked = r.Microphone; RdpAudio.SelectedIndex = Math.Clamp(r.Audio, 0, 2);
        RdpReconnect.IsChecked = r.AutoReconnect; RdpRetries.Text = r.ReconnectRetries.ToString();
        // 0.5.0 (ADR-062): single sign-on with the Windows account.
        RdpSignOn.ItemsSource = RdpSettings.SignOnChoices.Select(c => c.Title).ToList();
        RdpSignOn.SelectedIndex = Math.Max(0, Array.FindIndex(RdpSettings.SignOnChoices, c => c.Id == r.SignOn));
        SshKerberos.IsChecked = _host.KerberosSignOn;
        SshKerberos.IsEnabled = AppSettings.Current.SsoKerberosSsh || _host.KerberosSignOn;
        // Telnet appears only while allowed (or for a host that already is one, so it can be changed away).
        ProtoTelnet.Visibility = AppSettings.Current.TelnetEnabled || _host.IsTelnet ? Visibility.Visible : Visibility.Collapsed;
        // 0.5.1 #19: VNC.
        var v = _host.Vnc;
        (v.Kind == "mac" ? VncMac : VncPlain).IsChecked = true;
        (v.Through == "direct" ? VncDirect : VncThroughSsh).IsChecked = true;
        var sshHosts = new List<HostChoice> { new("(automatic: a saved SSH host with the same address)", null) };
        sshHosts.AddRange(AppData.Current.Hosts.Where(x => x.IsSsh && !x.IsAdHoc && x.Id != _host.Id).OrderBy(x => x.Display).Select(x => new HostChoice(x.Display, x)));
        VncSshBox.ItemsSource = sshHosts;
        VncSshBox.SelectedItem = sshHosts.FirstOrDefault(c => c.Host?.Id == v.SshHostId) ?? sshHosts[0];
        VncTarget.Text = v.TunnelTarget;
        VncScale.IsChecked = v.Scale; VncViewOnly.IsChecked = v.ViewOnly; VncShared.IsChecked = v.Shared; VncClipboard.IsChecked = v.ClipboardFromServer;
        _vncPin = v.TlsSha256;
        ShowVncPin();
        OnVncRouteChanged(null!, null!);
        (_host.Protocol == "rdp" ? ProtoRdp : _host.IsVnc ? ProtoVnc : _host.IsTelnet ? ProtoTelnet : ProtoSsh).IsChecked = true;
        var profiles = new List<TermProfileChoice> { new($"(default: {Sessions.TerminalProfiles.Default.Name})", null) };
        profiles.AddRange(Sessions.TerminalProfiles.All.Select(p => new TermProfileChoice(p.Name, p.Id)));
        TermProfileBox.ItemsSource = profiles;
        TermProfileBox.DisplayMemberPath = nameof(TermProfileChoice.Display);
        TermProfileBox.SelectedItem = profiles.FirstOrDefault(p => p.Id != null && p.Id == _host.TerminalProfileId) ?? profiles[0];
        SubHeading.Text = adHoc
            ? "Connect once without saving anything — or tick “Save as a host”. Secrets typed here stay in memory only unless saved."
            : "Secrets are stored in the encrypted vault, never in the host entry.";
        AdHocPanel.Visibility = adHoc ? Visibility.Visible : Visibility.Collapsed;
        SavedOnly.Visibility = NotesPanel.Visibility = adHoc ? Visibility.Collapsed : Visibility.Visible;
        OkButton.Content = adHoc ? "Connect" : "Save";

        NameBox.Text = _host.IsAdHoc ? "" : _host.Name;
        AddressBox.Text = _host.Address;
        UserBox.Text = _host.Username;
        PortBox.Text = _host.Port.ToString();
        GroupBox.ItemsSource = AppData.Current.Groups.DefaultIfEmpty("General").ToList();
        GroupBox.Text = _host.Group;
        TagsBox.Text = _host.Tags;
        PlatformBox.ItemsSource = new[] { "" }.Concat(HostEntry.Platforms).Select(HostEntry.PlatformLabel).ToList();
        PlatformBox.SelectedIndex = Math.Max(0, Array.IndexOf(HostEntry.Platforms, _host.Platform) + 1);
        var env = Envs.Normalize(_host.Environment);
        var envs = new[] { "(not set)" }.Concat(Envs.All).ToList();
        if (env.Length > 0 && !envs.Contains(env)) envs.Add(env);   // a custom label stays selectable
        EnvBox.ItemsSource = envs;
        EnvBox.SelectedItem = env.Length > 0 ? env : "(not set)";
        NotesBox.Text = _host.Notes;

        FillSudo(_host.SudoCredentialId);
        // 0.6.19: say when the host signs in through a direct link, and that a credential picked here replaces it.
        var link = _host.BitwardenItem.Length > 0 ? BitwardenSessions.Describe(_host.BitwardenItem)
                 : PasswordSafeSessions.Parse(_host.PasswordSafeAccount) is { } ps ? "Password Safe: " + ps.Label : "";
        if (link.Length > 0) TestResult.Text = $"Linked: signs in with {link}. Picking a credential above (or 🔍 Find…) replaces the link.";

        var jumps = new List<HostChoice> { new("(direct)", null) };
        jumps.AddRange(AppData.Current.Hosts.Where(h => h.Id != _host.Id).Select(h => new HostChoice(h.Display, h)));
        JumpBox.ItemsSource = jumps;
        JumpBox.SelectedItem = jumps.FirstOrDefault(j => j.Host?.Id == _host.JumpHostId) ?? jumps[0];
        var vpns = new List<VpnChoice> { new("(none)", null) };
        vpns.AddRange(AppData.Current.VpnProfiles.Select(v => new VpnChoice($"{v.Name} ({v.KindTitle}, {v.RoutesText})", v.Id)));
        VpnBox.ItemsSource = vpns;
        VpnBox.SelectedItem = vpns.FirstOrDefault(v => v.Id != null && v.Id == _host.VpnProfileId) ?? vpns[0];

        (_host.Auth switch { AuthMethod.Password => AuthPassword, AuthMethod.KeyboardInteractive => AuthInteractive, _ => AuthKey }).IsChecked = true;
        Loaded += (_, _) => (AddressBox.Text.Length == 0 ? AddressBox : NameBox.Text.Length == 0 && !adHoc ? NameBox : (Control)OkButton).Focus();
    }

    private void OnSourceInitialized(object? sender, EventArgs e) => ThemeManager.ApplyTitleBar(this);

    private bool IsRdp => ProtoRdp?.IsChecked == true;
    private bool IsTelnet => ProtoTelnet?.IsChecked == true;
    private bool IsVnc => ProtoVnc?.IsChecked == true;
    private string _vncPin = "";

    private void OnVncRouteChanged(object sender, RoutedEventArgs e)
    {
        if (VncSshPanel == null || VncDirectWarning == null) return;
        bool ssh = VncThroughSsh.IsChecked == true;
        VncSshPanel.Visibility = ssh ? Visibility.Visible : Visibility.Collapsed;
        VncDirectWarning.Visibility = ssh ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowVncPin()
    {
        VncPinRow.Visibility = _vncPin.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        VncPin.Text = _vncPin.Length > 0 ? "Pinned TLS certificate SHA-256 " + Vnc.RfbClient.TlsPinPretty(_vncPin) : "";
    }

    private void OnVncForgetPin(object sender, RoutedEventArgs e)
    {
        if (!Dialogs.Confirm("Forget the certificate", "Forget the pinned certificate? The next direct connection asks you to compare and trust it again.", "Forget")) return;
        _vncPin = "";
        ShowVncPin();
    }

    private void OnProtoChanged(object sender, RoutedEventArgs e)
    {
        if (SshOnly == null) return;
        bool rdp = IsRdp, telnet = IsTelnet, vnc = IsVnc;
        SshOnly.Visibility = AuthChoice.Visibility = rdp || telnet || vnc ? Visibility.Collapsed : Visibility.Visible;
        RdpOnly.Visibility = RdpOptions.Visibility = rdp ? Visibility.Visible : Visibility.Collapsed;
        if (VncOptions != null) VncOptions.Visibility = vnc ? Visibility.Visible : Visibility.Collapsed;
        // Telnet: the device asks for its login in the terminal; nothing is ever typed for you over an unencrypted link.
        CredLabel.Visibility = CredRow.Visibility = telnet ? Visibility.Collapsed : Visibility.Visible;
        if (telnet) InlinePanel.Visibility = Visibility.Collapsed; else OnCredChanged(null!, null!);
        SignInLabel.Text = rdp ? "SIGN IN (Windows account; leave empty to type it in Remote Desktop)"
            : telnet ? "SIGN IN: at the device's own prompt in the terminal (Telnet is unencrypted; nothing is typed for you)"
            : vnc ? "SIGN IN (a Mac: its macOS user name and password · a VNC server: its VNC password)" : "SIGN IN WITH";
        if (rdp || vnc) AuthPassword.IsChecked = true;
        if (PortBox.Text is "22" or "3389" or "23" or "5900" or "") PortBox.Text = rdp ? "3389" : telnet ? "23" : vnc ? "5900" : "22";
        if (rdp && UserBox.Text == "root") UserBox.Text = "Administrator";
        if (vnc && UserBox.Text == "root") UserBox.Text = "";
        TestButton.Content = rdp || telnet ? "Test port" : vnc ? "Test sign-in" : "Test connection";
        OkButton.Content = _adHoc ? (rdp ? "Connect (RDP)" : telnet ? "Connect (Telnet)" : vnc ? "Connect (VNC)" : "Connect") : "Save";
        if (AdHocPanel != null) foreach (var rb in new[] { OpenFiles, OpenManage, OpenExternal }) rb.IsEnabled = !rdp && !telnet && !vnc;
    }

    private AuthMethod Auth => AuthKey.IsChecked == true ? AuthMethod.PrivateKey : AuthInteractive.IsChecked == true ? AuthMethod.KeyboardInteractive : AuthMethod.Password;

    private void OnAuthChanged(object sender, RoutedEventArgs e) => FillCredentials(null);

    private void FillCredentials(string? selectId)
    {
        if (CredBox == null) return;
        var kind = Auth == AuthMethod.PrivateKey ? CredentialKind.PrivateKey : CredentialKind.Password;
        var list = new List<Choice>();
        list.Add(new Choice(kind == CredentialKind.PrivateKey ? "⌨ enter below (key file)" : "⌨ enter below / ask when connecting", null, Inline: true));
        list.AddRange(AppData.Current.Credentials.Concat(Library.SharedLibraries.Credentials).Where(c => c.Kind == kind).Select(c => new Choice(c.Display, c)));   // shared ones are labelled (ADR-033)
        CredBox.ItemsSource = list;
        var want = selectId ?? _host.CredentialId;
        CredBox.SelectedItem = list.FirstOrDefault(c => c.Cred?.Id == want) ?? (list.Count > 1 && kind == CredentialKind.PrivateKey ? list[1] : list[0]);
        CredLabel.Text = kind == CredentialKind.PrivateKey ? "KEY FROM VAULT" : "PASSWORD FROM VAULT";
        OnCredChanged(null!, null!);
    }

    private void FillSudo(string? selectId)
    {
        var sudo = new List<Choice> { new("(none — use the login password, or sudo without password)", null) };
        sudo.AddRange(AppData.Current.Credentials.Concat(Library.SharedLibraries.Credentials).Where(c => c.Kind == CredentialKind.Password).Select(c => new Choice(c.Display, c)));   // shared ones are labelled (ADR-033)
        SudoBox.ItemsSource = sudo;
        SudoBox.SelectedItem = sudo.FirstOrDefault(c => c.Cred?.Id == selectId) ?? sudo[0];
    }

    // 0.6.19 (DarkCipher0x: "make it easy to search for bitwarden/password safe creds"): 🔍 Find… searches this vault,
    // Bitwarden, Secrets Manager and Password Safe, starting from the host's name or address.
    private string FindHint() => NameBox.Text.Trim().Length > 0 ? NameBox.Text.Trim() : AddressBox.Text.Trim();

    private void OnFindCredential(object sender, RoutedEventArgs e)
    {
        var kind = Auth == AuthMethod.PrivateKey ? CredentialKind.PrivateKey : CredentialKind.Password;
        if (CredentialPicker.Pick(kind, $"signing in to {(FindHint().Length > 0 ? FindHint() : "this host")}", FindHint()) is not { } c) return;
        FillCredentials(c.Id);
        if (UserBox.Text.Trim().Length == 0 && c.Username.Length > 0) UserBox.Text = c.Username;   // the item's own user name, not "root"
        TestResult.Text = $"Signs in with “{c.Name}”{(c.IsExternal ? $" from {c.ExternalText} (read when it connects)" : "")}.";
    }

    private void OnFindSudo(object sender, RoutedEventArgs e)
    {
        if (CredentialPicker.Pick(CredentialKind.Password, $"sudo on {(FindHint().Length > 0 ? FindHint() : "this host")}", FindHint()) is not { } c) return;
        FillSudo(c.Id);
        FillCredentials((CredBox.SelectedItem as Choice)?.Cred?.Id);   // the new vault entry is listed there too
    }

    // ---- self-test (the window is built but never shown) -----------------------------------------------------------
    internal void FindCredentialForTest() => OnFindCredential(this, new RoutedEventArgs());
    internal void FindSudoForTest() => OnFindSudo(this, new RoutedEventArgs());
    internal HostEntry? BuildForTest() => Build(true, out _);
    internal string NoteForTest => TestResult.Text;
    internal string UserForTest { get => UserBox.Text; set => UserBox.Text = value; }

    private void OnCredChanged(object sender, SelectionChangedEventArgs e)
    {
        bool inline = (CredBox.SelectedItem as Choice)?.Inline == true;
        InlinePanel.Visibility = inline ? Visibility.Visible : Visibility.Collapsed;
        InlinePassword.Visibility = Auth != AuthMethod.PrivateKey ? Visibility.Visible : Visibility.Collapsed;
        InlineKey.Visibility = Auth == AuthMethod.PrivateKey ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnNewCredential(object sender, RoutedEventArgs e)
    {
        var w = new CredentialEditWindow(null, Auth == AuthMethod.PrivateKey ? CredentialKind.PrivateKey : CredentialKind.Password, UserBox.Text.Trim()) { Owner = this };
        if (w.ShowDialog() == true && w.Result is { } c)
        {
            AppData.Current.Credentials.Add(c);
            AppData.Save();
            FillCredentials(c.Id);
        }
    }

    private void OnBrowseKey(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Private key", Filter = "Private keys|*.pem;*.key;*.ppk;id_*;*|All files|*.*",
            InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh"),
        };
        if (dlg.ShowDialog(this) == true) KeyPathBox.Text = dlg.FileName;
    }

    /// <summary>Builds the host from the form. Inline secrets become a transient credential (or a new vault entry when saving).</summary>
    private HostEntry? Build(bool forSave, out Credential? newCredential)
    {
        newCredential = null;
        var address = AddressBox.Text.Trim();
        if (address.Length == 0) { Dialogs.Error("Host", "Enter an address."); return null; }
        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1 or > 65535) { Dialogs.Error("Host", "Port must be 1–65535."); return null; }

        var h = _host.Clone();
        h.Address = address; h.Port = port;
        h.Username = UserBox.Text.Trim().Length > 0 ? UserBox.Text.Trim() : IsVnc ? "" : "root";
        h.Name = NameBox.Text.Trim();
        h.Group = string.IsNullOrWhiteSpace(GroupBox.Text) ? "General" : GroupBox.Text.Trim();
        h.Tags = TagsBox.Text.Trim();
        h.Platform = PlatformBox.SelectedIndex > 0 ? HostEntry.Platforms[PlatformBox.SelectedIndex - 1] : "";
        h.Environment = Envs.Normalize(EnvBox.SelectedItem as string);
        h.Notes = NotesBox.Text;
        h.Auth = Auth;
        h.SudoCredentialId = (SudoBox.SelectedItem as Choice)?.Cred?.Id;
        h.JumpHostId = (JumpBox.SelectedItem as HostChoice)?.Host?.Id;
        h.VpnProfileId = (VpnBox.SelectedItem as VpnChoice)?.Id;
        h.Transient = null;
        h.Protocol = IsRdp ? "rdp" : IsVnc ? "vnc" : IsTelnet ? "telnet" : "ssh";
        var vnc = _host.Vnc.Clone();
        vnc.Kind = VncMac.IsChecked == true ? "mac" : "vnc";
        vnc.Through = VncDirect.IsChecked == true ? "direct" : "ssh";
        vnc.SshHostId = (VncSshBox.SelectedItem as HostChoice)?.Host?.Id;
        vnc.TunnelTarget = VncTarget.Text.Trim().Length > 0 ? VncTarget.Text.Trim() : "127.0.0.1";
        vnc.Scale = VncScale.IsChecked == true; vnc.ViewOnly = VncViewOnly.IsChecked == true; vnc.Shared = VncShared.IsChecked == true;
        vnc.ClipboardFromServer = VncClipboard.IsChecked == true;
        vnc.TlsSha256 = _vncPin;
        h.Vnc = vnc;
        if (h.IsVnc && vnc.Kind == "mac" && h.Username.Length == 0 && (CredBox.SelectedItem as Choice)?.Cred is not { Username.Length: > 0 })
        { Dialogs.Error("Host", "A Mac signs in with its macOS user name: enter it (and the password, or a vault credential)."); return null; }
        h.TerminalProfileId = (TermProfileBox.SelectedItem as TermProfileChoice)?.Id;
        if (int.TryParse(KeepAliveBox.Text.Trim(), out var ka)) h.KeepAliveSeconds = Math.Clamp(ka, 0, 3600);
        else { Dialogs.Error("Host", "Keepalive must be a number of seconds (0 = off)."); return null; }
        // From the host's own settings (0.5.0 fix: a new object here reset Fit, smart cards and the keyboard hook on every save).
        var rdp = (_host.Rdp ?? new RdpSettings()).Clone();
        rdp.Domain = RdpDomain.Text.Trim(); rdp.Gateway = RdpGateway.Text.Trim(); rdp.FullScreen = RdpFull.IsChecked == true;
        rdp.Width = int.TryParse(RdpW.Text, out var w) ? w : 1600; rdp.Height = int.TryParse(RdpH.Text, out var hh) ? hh : 900;
        rdp.MultiMonitor = RdpMulti.IsChecked == true; rdp.Admin = RdpAdmin.IsChecked == true; rdp.Clipboard = RdpClip.IsChecked == true;
        rdp.Drives = RdpDrives.IsChecked == true; rdp.Printers = RdpPrinters.IsChecked == true; rdp.Microphone = RdpMic.IsChecked == true;
        rdp.Audio = Math.Max(0, RdpAudio.SelectedIndex); rdp.AutoReconnect = RdpReconnect.IsChecked == true;
        rdp.ReconnectRetries = int.TryParse(RdpRetries.Text, out var rr) ? rr : 20;
        rdp.SignOn = RdpSettings.SignOnChoices[Math.Max(0, RdpSignOn.SelectedIndex)].Id;
        h.Rdp = rdp;
        h.KerberosSignOn = SshKerberos.IsChecked == true && h.Protocol == "ssh";
        if (h.Protocol is "rdp" or "vnc") { h.Auth = AuthMethod.Password; h.JumpHostId = null; }
        if (h.IsTelnet) { h.Auth = AuthMethod.Password; h.JumpHostId = null; h.SudoCredentialId = null; h.CredentialId = null; return h; }

        var choice = CredBox.SelectedItem as Choice;
        if (choice?.Inline != true)
        {
            h.CredentialId = choice?.Cred?.Id;
            // 0.6.19: a credential picked here replaces a direct Bitwarden / Password Safe link (which would otherwise win).
            if (h.CredentialId != null && h.CredentialId != _host.CredentialId) { h.BitwardenItem = ""; h.PasswordSafeAccount = ""; }
            return h;
        }

        h.CredentialId = null;
        Credential? inline = null;
        if (Auth == AuthMethod.PrivateKey)
        {
            var path = KeyPathBox.Text.Trim();
            if (path.Length == 0) { Dialogs.Error("Host", "Choose a private key file, or a key from the vault."); return null; }
            if (!File.Exists(path)) { Dialogs.Error("Host", "Key file not found."); return null; }
            inline = new Credential { Name = $"{Path.GetFileName(path)} ({h.Address})", Kind = CredentialKind.PrivateKey, Username = h.Username, Secret = File.ReadAllText(path), Passphrase = PassphraseBox.Password };
            try { KeyTools.Describe(inline); }
            catch (Exception ex) { Dialogs.Error("Key", "That key can't be read: " + ex.Message + "\n\nWrong passphrase, or an unsupported format."); return null; }
        }
        else if (PasswordBox.Password.Length > 0)
        {
            inline = new Credential { Name = $"{h.Username}@{h.Address}", Kind = CredentialKind.Password, Username = h.Username, Secret = PasswordBox.Password };
        }

        if (inline != null)
        {
            h.BitwardenItem = ""; h.PasswordSafeAccount = "";   // 0.6.19: a password / key typed here replaces a direct link
            if (forSave) { newCredential = inline; h.CredentialId = inline.Id; }
            else h.Transient = inline;
        }
        return h;
    }

    private async void OnTest(object sender, RoutedEventArgs e)
    {
        var h = Build(false, out var nc);
        if (h == null) return;
        if (nc != null) h.Transient = nc;
        h.IsAdHoc = true;
        TestButton.IsEnabled = false;
        TestResult.Text = "connecting…";
        if (h.IsVnc)
        {
            // The real handshake (tunnel, security type, sign-in), then straight back out: nothing is drawn.
            try
            {
                using var s = await Vnc.VncConnector.ConnectAsync(h, (fp, subject) => Dispatcher.Invoke(() =>
                    Dialogs.Confirm("Trust this certificate?", $"{subject}\nSHA-256 {Vnc.RfbClient.TlsPinPretty(fp)}\n\nTrust it for this test?", "Trust")), CancellationToken.None);
                var c = s.Client;
                if (c.PinnedSha256.Length > 0) { _vncPin = c.PinnedSha256; ShowVncPin(); }
                TestResult.Text = $"✓ {(c.DesktopName.Length > 0 ? c.DesktopName + " · " : "")}{c.Width}×{c.Height} · RFB {c.Version} · {c.Security} · {s.Route}" + (c.Encrypted ? " · encrypted" : " · ⚠ not encrypted on the network");
            }
            catch (Exception ex) { TestResult.Text = "✕ " + ex.Message; }
            finally { TestButton.IsEnabled = true; }
            return;
        }
        if (h.Protocol is "rdp" or "telnet")
        {
            try
            {
                await Offline.EnforceAsync(h.Address, h.IsTelnet ? "Telnet (test)" : "Remote Desktop (test)");
                using var tcp = new System.Net.Sockets.TcpClient();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await tcp.ConnectAsync(h.Address, h.Port, new CancellationTokenSource(4000).Token);
                TestResult.Text = $"✓ {h.Address}:{h.Port} is open ({sw.ElapsedMilliseconds} ms)" + (h.IsTelnet ? " — Telnet is unencrypted and can't verify the device." : " — Remote Desktop will ask Windows to authenticate the server.");
            }
            catch (Exception ex) { TestResult.Text = "✕ " + ex.Message; }
            finally { TestButton.IsEnabled = true; }
            return;
        }
        try
        {
            using var s = await SshConnector.ConnectSshAsync(h);
            var r = await RemoteExec.RunAsync(s.Client, h, "uname -srm; (. /etc/os-release 2>/dev/null && echo \"$PRETTY_NAME\"); id -un", false);
            TestResult.Text = "✓ " + string.Join("  ·  ", r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        }
        catch (Exception ex) { TestResult.Text = "✕ " + ex.Message; }
        finally { TestButton.IsEnabled = true; }
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        bool save = !_adHoc || SaveHost.IsChecked == true;
        var h = Build(save, out var newCred);
        if (h == null) return;
        if (newCred != null)
        {
            AppData.Current.Credentials.Add(newCred);
            if (!_adHoc) AppData.Save();
        }
        if (_adHoc)
        {
            ConnectAs = h.Protocol == "rdp" ? "rdp" : h.IsVnc ? "vnc" : OpenFiles.IsChecked == true ? "files" : OpenManage.IsChecked == true ? "manage" : OpenExternal.IsChecked == true ? "external" : "terminal";
            if (save)
            {
                h.IsAdHoc = false;
                if (h.Name.Length == 0) h.Name = $"{h.Username}@{h.Address}";
                AppData.Current.Hosts.Add(h);
                AppData.Save();
                AuditLog.Write("you", h.Display, "added host");
            }
            else
            {
                h.IsAdHoc = true;
                if (h.Name.Length == 0) h.Name = $"{h.Username}@{h.Address}";
            }
        }
        Result = h;
        DialogResult = true;
    }
}
