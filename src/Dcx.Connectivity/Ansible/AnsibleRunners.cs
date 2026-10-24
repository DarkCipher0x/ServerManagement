using System.Diagnostics;
using System.Text;
using DcxManager.Services;
using DcxManager.Sessions;
using DcxManager.Ssh;

namespace DcxManager.Ansible;

/// <summary>Where a job runs: a WSL distribution on this PC, or a saved Linux host over SSH. Output arrives line by line.</summary>
public interface IAnsibleRunner
{
    string Name { get; }
    Task<int> RunAsync(AnsibleCommands.Job job, Action<string> onLine, CancellationToken ct);
}

/// <summary>wsl.exe -d &lt;distro&gt; -- sh -c '&lt;bootstrap&gt;' with the job on stdin (no console window).</summary>
public sealed class WslAnsibleRunner(string distro) : IAnsibleRunner
{
    public string Name => "WSL · " + distro;

    public async Task<int> RunAsync(AnsibleCommands.Job job, Action<string> onLine, CancellationToken ct)
    {
        if (!LocalShells.ValidDistroName(distro)) throw new ArgumentException("distribution name");
        var wsl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");
        var psi = new ProcessStartInfo(wsl) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        psi.ArgumentList.Add("-d"); psi.ArgumentList.Add(distro); psi.ArgumentList.Add("--");
        psi.ArgumentList.Add("sh"); psi.ArgumentList.Add("-c"); psi.ArgumentList.Add(AnsibleCommands.BootstrapScript);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("wsl.exe didn't start");
        p.OutputDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        var input = new UTF8Encoding(false);
        await p.StandardInput.BaseStream.WriteAsync(input.GetBytes(job.Stdin), ct);
        p.StandardInput.Close();
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch (InvalidOperationException) { } throw; }
        return p.ExitCode;
    }
}

/// <summary>The bootstrap over the host's pooled SSH connection; stdin written through the channel, output streamed.</summary>
public sealed class SshAnsibleRunner(HostEntry host) : IAnsibleRunner
{
    public string Name => host.Display;
    public HostEntry Host => host;

    public async Task<int> RunAsync(AnsibleCommands.Job job, Action<string> onLine, CancellationToken ct)
    {
        var client = await SshPool.GetAsync(host, ct);
        using var cmd = client.CreateCommand(job.Command);
        cmd.CommandTimeout = TimeSpan.FromHours(2);
        var run = cmd.ExecuteAsync(ct);
        using (var input = cmd.CreateInputStream()) await input.WriteAsync(Encoding.UTF8.GetBytes(job.Stdin), ct);
        var pump = Task.Run(async () =>
        {
            using var r = new StreamReader(cmd.OutputStream, Encoding.UTF8, false, 4096, leaveOpen: true);
            string? line;
            while ((line = await r.ReadLineAsync(ct)) != null) onLine(line);
        }, ct);
        await run;
        try { await pump; } catch (OperationCanceledException) { }
        if (!string.IsNullOrWhiteSpace(cmd.Error)) foreach (var l in cmd.Error.Split('\n')) onLine(l.TrimEnd('\r'));
        return cmd.ExitStatus ?? -1;
    }
}
