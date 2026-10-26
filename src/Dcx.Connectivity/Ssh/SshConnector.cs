using System.IO;
using System.Net.Sockets;
using System.Text;
using DcxManager.Services;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace DcxManager.Ssh;

/// <summary>A connected SSH or SFTP client plus anything that keeps it alive (jump-host hops and their port forwards).</summary>
public sealed class SshSession<T> : IDisposable where T : BaseClient
{
    public required T Client { get; init; }
    public required HostEntry Host { get; init; }
    internal List<IDisposable> Chain { get; } = new();

    public bool IsConnected => Client.IsConnected;

    public void Dispose()
    {
        try { if (Client.IsConnected) Client.Disconnect(); } catch { }
        Client.Dispose();
        for (int i = Chain.Count - 1; i >= 0; i--) { try { Chain[i].Dispose(); } catch { } }
    }
}

public sealed class HostKeyRejectedException(string message) : Exception(message);

/// <summary>
/// Opens SSH/SFTP connections for a <see cref="HostEntry"/>: password, private key (OpenSSH, PEM, PuTTY .ppk),
/// keyboard-interactive (2FA/OTP prompts), optional jump host, and trust-on-first-use host key checking.
/// </summary>
public static class SshConnector
{
    private static int _relays;

    /// <summary>Jump-host relays open right now. Each is a 127.0.0.1:random listener used only by its own session
    /// (shown in Privacy &amp; network → Listening services).</summary>
    public static int ActiveRelays => Volatile.Read(ref _relays);

    private sealed class Relay : IDisposable
    {
        private ForwardedPortLocal? _fwd;
        public Relay(ForwardedPortLocal fwd) { _fwd = fwd; Interlocked.Increment(ref _relays); }
        public void Dispose()
        {
            var f = Interlocked.Exchange(ref _fwd, null);
            if (f == null) return;
            Interlocked.Decrement(ref _relays);
            try { f.Dispose(); } catch { }
        }
    }

    public static Task<SshSession<SshClient>> ConnectSshAsync(HostEntry host, CancellationToken ct = default) =>
        ConnectAsync(host, ci => new SshClient(ci), 0, ct);

    /// <summary>SCP (fallback for servers without the SFTP subsystem, ADR-014). Paths are shell-quoted on the server.</summary>
    public static Task<SshSession<ScpClient>> ConnectScpAsync(HostEntry host, CancellationToken ct = default) =>
        ConnectAsync(host, ci => new ScpClient(ci, RemotePathTransformation.ShellQuote) { OperationTimeout = TimeSpan.FromMinutes(5) }, 0, ct);

    public static Task<SshSession<SftpClient>> ConnectSftpAsync(HostEntry host, CancellationToken ct = default) =>
        ConnectAsync(host, ci => new SftpClient(ci) { OperationTimeout = TimeSpan.FromMinutes(5) }, 0, ct);

    /// <summary>Runs before a connection to a host opens (0.3.0 §24: the VPN manager brings up the tunnel the host needs).</summary>
    public static Func<HostEntry, CancellationToken, Task>? BeforeConnect { get; set; }

    private static async Task<SshSession<T>> ConnectAsync<T>(HostEntry host, Func<ConnectionInfo, T> factory, int depth, CancellationToken ct) where T : BaseClient
    {
        if (depth > 4) throw new InvalidOperationException("Jump host chain is too long (loop?).");
        // 0.5.1 (ADR-067): a locked host (or jump host) connects only while it is unlocked — the screens ask first; the AI,
        // monitors, transfers and anything else without a person in front of it are refused here.
        if (!host.IsAdHoc) Locks.Demand("host:" + host.Id, host.Display);
        if (BeforeConnect != null) await BeforeConnect(host, ct);
        var chain = new List<IDisposable>();
        string address = host.Address; int port = host.Port;
        var viaJump = host.JumpHostId is { } jid && AppData.Current.FindHost(jid) is { } j && j.Id != host.Id;
        // Offline mode (ADR-016): checked before any socket opens. A target behind a jump host may only resolve over there,
        // so it gets the rule without DNS; the jump host itself is checked fully by the recursive call.
        if (viaJump) Offline.EnforceStatic(host.Address, "SSH (through a jump host)");
        else if (Offline.On)
        {
            var d = await Offline.DecideAsync(host.Address, ct);
            if (!d.Allowed) throw Offline.Block(host.Address, "SSH", d.Reason);
            // Connect to the address that was checked, not a second lookup (host keys stay keyed by the saved address).
            if (d.Addresses.Length > 0) address = (d.Addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? d.Addresses[0]).ToString();
        }

        // Jump host: connect to it, forward a random local port to the target's SSH port, then connect through that.
        if (host.JumpHostId is { } jumpId && AppData.Current.FindHost(jumpId) is { } jump && jump.Id != host.Id)
        {
            var hop = await ConnectAsync(jump, ci => new SshClient(ci), depth + 1, ct);
            chain.Add(hop);
            var fwd = new ForwardedPortLocal("127.0.0.1", 0, host.Address, (uint)host.Port);
            hop.Client.AddForwardedPort(fwd);
            fwd.Start();
            chain.Add(new Relay(fwd));
            address = "127.0.0.1"; port = (int)fwd.BoundPort;
        }

        var info = BuildConnectionInfo(host, address, port);
        var client = factory(info);
        client.KeepAliveInterval = TimeSpan.FromSeconds(Math.Max(0, host.KeepAliveSeconds));
        var knownKey = $"{host.Address}:{host.Port}";
        client.HostKeyReceived += (_, e) => e.CanTrust = CheckHostKey(knownKey, host.Display, e);

        try
        {
            await Task.Run(() => client.Connect(), ct);
        }
        catch (Exception ex)
        {
            client.Dispose();
            foreach (var d in Enumerable.Reverse(chain)) { try { d.Dispose(); } catch { } }
            throw Friendly(ex, host);
        }

        if (!host.IsAdHoc)
        {
            host.LastConnected = DateTime.Now;
            try { AppData.SaveSoon(); } catch { }   // 0.6.29: not an encrypted write + every screen's reload on the connect thread
        }
        var s = new SshSession<T> { Client = client, Host = host };
        s.Chain.AddRange(chain);
        return s;
    }

    public static Credential? CredentialFor(HostEntry host)
    {
        if (host.Transient != null) return host.Transient;
        var c = ExternalCredentials.LinkedTo(host) ?? AppData.Current.FindCredential(host.CredentialId);   // a direct link wins, as for its session tabs
        if (c != null && AppData.Current.FindCredential(c.Id) != null) Locks.Demand("cred:" + c.Id, c.Name);   // a locked credential is used only while unlocked (ADR-067)
        if (c is { IsShared: true }) SharedCredentialUsed?.Invoke(c, host.Endpoint);   // audited by the shared library (ADR-033)
        // 0.6.17 (ADR-096): kept in Bitwarden or Password Safe → read into memory for this use (Files, Manage, tunnels,
        // checks and sessions alike), never saved.
        return ExternalCredentials.Resolve(c, "signing in to " + host.Display);
    }

    /// <summary>A shared library credential was used to connect (credential, endpoint): the library audits it. Wired by
    /// the app at start-up (0.2.0 phase 6: the connector doesn't reference the library above it).</summary>
    public static Action<Credential, string>? SharedCredentialUsed { get; set; }

    public static ConnectionInfo BuildConnectionInfo(HostEntry host, string address, int port)
    {
        var cred = CredentialFor(host);
        var user = string.IsNullOrWhiteSpace(host.Username) ? cred?.Username ?? "root" : host.Username;
        var methods = new List<AuthenticationMethod>();

        if (cred?.Kind == CredentialKind.PrivateKey)
        {
            methods.Add(new PrivateKeyAuthenticationMethod(user, LoadKey(cred)));
            // If the key is refused (or the server wants a second factor), fall back to prompting — only when asked.
            var ki = new KeyboardInteractiveAuthenticationMethod(user);
            ki.AuthenticationPrompt += (_, e) =>
            {
                foreach (var p in e.Prompts)
                    p.Response = OnUi(() => Ui.Dialogs.Prompt($"{host.Display} — sign in", p.Request.Trim(), password: !p.IsEchoed)) ?? "";
            };
            methods.Add(ki);
        }
        else if (host.Auth == AuthMethod.KeyboardInteractive)
        {
            var ki = new KeyboardInteractiveAuthenticationMethod(user);
            var pw = cred?.Secret;
            ki.AuthenticationPrompt += (_, e) =>
            {
                foreach (var p in e.Prompts)
                {
                    // First password-looking prompt is answered from the vault; anything else (OTP, 2FA) is asked.
                    if (pw != null && p.Request.Contains("assword", StringComparison.OrdinalIgnoreCase)) { p.Response = pw; pw = null; continue; }
                    p.Response = OnUi(() => Ui.Dialogs.Prompt($"{host.Display} — sign in", p.Request.Trim(), password: !p.IsEchoed)) ?? "";
                }
            };
            methods.Add(ki);
        }
        else
        {
            var pw = cred?.Secret;
            if (string.IsNullOrEmpty(pw))
                pw = OnUi(() => Ui.Dialogs.Prompt($"{host.Display} — password", $"Password for {user}@{host.Address}:", password: true))
                     ?? throw new OperationCanceledException("Sign-in cancelled.");
            methods.Add(new PasswordAuthenticationMethod(user, pw));
            // Many servers only offer keyboard-interactive for passwords: answer it with the same password.
            var ki = new KeyboardInteractiveAuthenticationMethod(user);
            ki.AuthenticationPrompt += (_, e) => { foreach (var p in e.Prompts) p.Response = pw; };
            methods.Add(ki);
        }

        var ci = new ConnectionInfo(address, port, user, methods.ToArray())
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(5, AppSettings.Current.ConnectTimeoutSeconds)),
            Encoding = Encoding.UTF8,
        };
        SshPq.Apply(ci, AppSettings.Current.PqSshRequired);   // 0.6.12: post-quantum key exchange first (or only)
        return ci;
    }

    public static PrivateKeyFile LoadKey(Credential cred)
    {
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(cred.Secret.Replace("\r\n", "\n")));
        return string.IsNullOrEmpty(cred.Passphrase) ? new PrivateKeyFile(ms) : new PrivateKeyFile(ms, cred.Passphrase);
    }

    /// <summary>Trust on first use: new keys are shown to the user; a changed key is a loud warning.</summary>
    private static bool CheckHostKey(string key, string display, HostKeyEventArgs e)
    {
        var fp = e.FingerPrintSHA256;
        var data = AppData.Current;
        var known = data.KnownHosts.FirstOrDefault(k => k.Key == key && k.Algorithm == e.HostKeyName)
                    ?? data.KnownHosts.FirstOrDefault(k => k.Key == key);
        if (known != null && known.Fingerprint == fp && known.Algorithm == e.HostKeyName) return true;

        bool changed = known != null && known.Algorithm == e.HostKeyName;
        var msg = changed
            ? $"WARNING: the host key for {display} ({key}) has CHANGED.\n\nSaved:  {known!.Algorithm} SHA256:{known.Fingerprint}\nNow:    {e.HostKeyName} SHA256:{fp}\n\n" +
              "This happens after a reinstall — or when someone is intercepting the connection. Only continue if you know why it changed."
            : $"First connection to {display} ({key}).\n\n{e.HostKeyName}\nSHA256:{fp}\n\nCompare this with the server's fingerprint (ssh-keygen -lf /etc/ssh/ssh_host_*_key.pub) before trusting it.";
        bool ok = OnUi(() => Ui.Dialogs.Confirm(changed ? "Host key changed" : "Trust this host?", msg, changed ? "Trust the new key" : "Trust and connect", danger: changed));
        if (!ok) throw new HostKeyRejectedException("Host key not trusted.");
        if (known != null) data.KnownHosts.RemoveAll(k => k.Key == key && k.Algorithm == e.HostKeyName);
        data.KnownHosts.Add(new KnownHost { Key = key, Algorithm = e.HostKeyName, Fingerprint = fp });
        try { AppData.Save(); } catch { }
        AuditLog.Write("you", display, changed ? $"accepted CHANGED host key {e.HostKeyName} {fp}" : $"trusted host key {e.HostKeyName} {fp}");
        return true;
    }

    private static T OnUi<T>(Func<T> f) => Ui.Thread.Invoke(f);

    private static Exception Friendly(Exception ex, HostEntry h) => ex switch
    {
        HostKeyRejectedException or OperationCanceledException => ex,
        SshAuthenticationException => new InvalidOperationException($"Authentication failed for {h.Endpoint}: {ex.Message}\nCheck the username and credential (and that the server allows this method).", ex),
        SocketException se => new InvalidOperationException($"Can't reach {h.Address}:{h.Port} — {se.Message}", ex),
        SshOperationTimeoutException => new InvalidOperationException($"Timed out connecting to {h.Address}:{h.Port}.", ex),
        SshConnectionException when SshPq.RequirementProblem(ex, AppSettings.Current.PqSshRequired) is { } pq => new InvalidOperationException($"Connection to {h.Address}:{h.Port} refused: {pq}", ex),
        SshConnectionException => new InvalidOperationException($"Connection to {h.Address}:{h.Port} failed: {ex.Message}", ex),
        _ => ex,
    };
}
