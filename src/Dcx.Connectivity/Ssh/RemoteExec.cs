using System.Text;
using DcxManager.Services;
using Renci.SshNet;

namespace DcxManager.Ssh;

public sealed record ExecResult(int ExitCode, string Output, string Error)
{
    public bool Ok => ExitCode == 0;
    public string Combined => string.IsNullOrWhiteSpace(Error) ? Output : (Output.TrimEnd() + "\n" + Error).Trim();
}

/// <summary>Runs commands over an existing SSH connection. sudo uses the vault password through stdin (never on the command line).</summary>
public static class RemoteExec
{
    public static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    public static async Task<ExecResult> RunAsync(SshClient client, HostEntry host, string command, bool sudo = false,
        CancellationToken ct = default, TimeSpan? timeout = null)
    {
        string? sudoPassword = null;
        var text = command;
        if (sudo && !host.Username.Equals("root", StringComparison.Ordinal))
        {
            sudoPassword = SudoPassword(host);
            text = sudoPassword != null
                ? $"sudo -S -p '' -- sh -c {Quote(command)}"
                : $"sudo -n -- sh -c {Quote(command)}";
        }

        using var cmd = client.CreateCommand(text);
        cmd.CommandTimeout = timeout ?? TimeSpan.FromMinutes(2);
        var run = cmd.ExecuteAsync(ct);
        if (sudoPassword != null)
        {
            using var input = cmd.CreateInputStream();
            var bytes = Encoding.UTF8.GetBytes(sudoPassword + "\n");
            await input.WriteAsync(bytes, ct);
        }
        await run;
        var err = cmd.Error ?? "";
        if (sudo && sudoPassword == null && err.Contains("a password is required"))
            err += "\n(sudo needs a password: give this host a password credential or a sudo password in its settings.)";
        return new ExecResult(cmd.ExitStatus ?? -1, cmd.Result ?? "", err);
    }

    public static string? SudoPassword(HostEntry host)
    {
        if (AppData.Current.FindCredential(host.SudoCredentialId) is { Kind: CredentialKind.Password } s)
        {
            // 0.6.17: may be kept in Bitwarden or Password Safe. A locked vault in a background check (or a cancelled
            // unlock) means no sudo password: the command then says sudo needs one, instead of failing on the lookup.
            try { return ExternalCredentials.Resolve(s, "sudo on " + host.Display)!.Secret; }
            catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException) { Log.Write("sudo password: " + s.Name, ex); return null; }
        }
        if (SshConnector.CredentialFor(host) is { Kind: CredentialKind.Password } c && !string.IsNullOrEmpty(c.Secret)) return c.Secret;
        return null;
    }
}

/// <summary>Keeps one management connection per host open for the dashboards, so each refresh doesn't renegotiate SSH.</summary>
public static class SshPool
{
    private static readonly Dictionary<string, SshSession<SshClient>> Sessions = new();
    private static readonly Dictionary<string, SemaphoreSlim> Locks = new();

    public static async Task<SshClient> GetAsync(HostEntry host, CancellationToken ct = default)
    {
        SemaphoreSlim gate;
        lock (Locks) { if (!Locks.TryGetValue(host.Id, out gate!)) Locks[host.Id] = gate = new SemaphoreSlim(1, 1); }
        await gate.WaitAsync(ct);
        try
        {
            lock (Sessions)
                if (Sessions.TryGetValue(host.Id, out var s))
                {
                    if (s.IsConnected) return s.Client;
                    s.Dispose(); Sessions.Remove(host.Id);
                }
            var session = await SshConnector.ConnectSshAsync(host, ct);
            lock (Sessions) Sessions[host.Id] = session;
            return session.Client;
        }
        finally { gate.Release(); }
    }

    public static async Task<ExecResult> RunAsync(HostEntry host, string command, bool sudo = false, CancellationToken ct = default, TimeSpan? timeout = null)
    {
        var client = await GetAsync(host, ct);
        return await RemoteExec.RunAsync(client, host, command, sudo, ct, timeout);
    }

    public static void Drop(string hostId)
    {
        lock (Sessions) if (Sessions.Remove(hostId, out var s)) s.Dispose();
    }

    public static void DisposeAll()
    {
        lock (Sessions) { foreach (var s in Sessions.Values) s.Dispose(); Sessions.Clear(); }
    }
}
