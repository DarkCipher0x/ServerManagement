using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using DcxManager.Services;

namespace DcxManager.Ssh;

/// <summary>
/// Writes a vault key to a file for use elsewhere (0.3.0 SSH key tool), optionally protected by a passphrase in OpenSSH's
/// own format (bcrypt KDF + AES-256-CTR, what every OpenSSH, PuTTYgen and the app itself read). The passphrase is put on
/// the key by Windows' ssh-keygen, which gets it through SSH_ASKPASS from this app's own executable
/// (<see cref="AskpassMain"/>), which in turn asks a one-shot named pipe that only the current user can open: the
/// passphrase is never on a command line, in an environment variable, or on disk. The file is restricted to the current
/// user (OpenSSH refuses keys others can read).
/// </summary>
public static class KeyExport
{
    /// <summary>Set in ssh-keygen's environment only: the pipe the askpass helper asks.</summary>
    public const string PipeVariable = "DCX_ASKPASS_PIPE";

    /// <summary>The program ssh-keygen runs for each prompt (this app's executable; the self-test's own build in tests).</summary>
    public static Func<string?> AskpassProgram { get; set; } = () => Environment.ProcessPath;

    /// <summary>Writes the key; with a passphrase, in the OpenSSH format protected by it. Deletes the file again if
    /// protecting it fails, so no unprotected copy stays behind.</summary>
    public static async Task ExportAsync(Credential key, string path, string? passphrase, CancellationToken ct = default)
    {
        KeyTools.ExportPrivateKey(key, path);
        if (string.IsNullOrEmpty(passphrase) && string.IsNullOrEmpty(key.Passphrase)) return;
        try
        {
            if (string.IsNullOrEmpty(passphrase))
                throw new InvalidOperationException("This key is protected by a passphrase in the vault: give the export a passphrase too (an unprotected copy isn't written).");
            await ProtectAsync(path, key.Passphrase, passphrase, ct);
        }
        catch { try { File.Delete(path); } catch { } throw; }
    }

    /// <summary>ssh-keygen -p on the file, answering its prompts through the pipe: the old passphrase (or none) and the new one.</summary>
    private static async Task ProtectAsync(string path, string oldPass, string newPass, CancellationToken ct)
    {
        var keygen = KeyTools.SshKeygenPath() ?? throw new InvalidOperationException("ssh-keygen.exe was not found (Windows Optional Feature: OpenSSH Client), so the key can't be given a passphrase.");
        var helper = AskpassProgram() ?? throw new InvalidOperationException("The app's own executable isn't known.");
        var pipe = "dcx-askpass-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(2));
        var server = ServeAsync(pipe, prompt => prompt.Contains("old", StringComparison.OrdinalIgnoreCase) ? oldPass : newPass, cts.Token);

        var psi = new ProcessStartInfo(keygen) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        // -P "" only when there's no old passphrase (then nothing is asked for it); the new one always comes from askpass.
        foreach (var a in string.IsNullOrEmpty(oldPass) ? new[] { "-p", "-q", "-P", "", "-f", path } : new[] { "-p", "-q", "-f", path }) psi.ArgumentList.Add(a);
        psi.Environment["SSH_ASKPASS"] = helper;
        psi.Environment["SSH_ASKPASS_REQUIRE"] = "force";
        psi.Environment["DISPLAY"] = "dcx:0";
        psi.Environment[PipeVariable] = pipe;
        psi.Environment.Remove("DCX_DATA_DIR");   // the helper must never open a store
        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        var outTask = KeyTools.ReadOnThread(p.StandardOutput);
        var errTask = KeyTools.ReadOnThread(p.StandardError);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw new TimeoutException("ssh-keygen didn't finish."); }
        await Task.WhenAll(outTask, errTask);
        cts.Cancel();
        try { await server; } catch (OperationCanceledException) { }
        if (p.ExitCode != 0) throw new InvalidOperationException("ssh-keygen couldn't protect the key: " + (await errTask).Trim());
    }

    /// <summary>Answers askpass prompts (at most three, then it closes). Only the current user can connect.</summary>
    internal static async Task ServeAsync(string name, Func<string, string> answer, CancellationToken ct)
    {
        for (int i = 0; i < 3; i++)
        {
            // CurrentUserOnly: the pipe's ACL is this user only, and a client running as anyone else is refused.
            using var srv = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await srv.WaitForConnectionAsync(ct);
            using var r = new StreamReader(srv, new UTF8Encoding(false), false, 1024, leaveOpen: true);
            var prompt = await r.ReadLineAsync(ct) ?? "";
            var bytes = Encoding.UTF8.GetBytes(answer(prompt) + "\n");
            await srv.WriteAsync(bytes, ct);
            await srv.FlushAsync(ct);
            CryptographicOperations.ZeroMemory(bytes);
            try { srv.WaitForPipeDrain(); } catch { }
        }
    }

    /// <summary>The askpass side (App.OnStartup, before any window or store): sends ssh-keygen's prompt to the pipe named
    /// in the environment and prints the answer for ssh-keygen. Exit 1 when there's no pipe to ask.</summary>
    public static int AskpassMain(string prompt)
    {
        var name = Environment.GetEnvironmentVariable(PipeVariable);
        if (string.IsNullOrEmpty(name) || !name.StartsWith("dcx-askpass-", StringComparison.Ordinal)) return 1;
        try
        {
            using var c = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            c.Connect(10_000);
            var q = Encoding.UTF8.GetBytes(prompt.Replace('\n', ' ') + "\n");
            c.Write(q); c.Flush();
            using var r = new StreamReader(c, new UTF8Encoding(false));
            var answer = r.ReadLine() ?? "";
            using var stdout = Console.OpenStandardOutput();
            var bytes = Encoding.UTF8.GetBytes(answer + "\n");
            stdout.Write(bytes); stdout.Flush();
            CryptographicOperations.ZeroMemory(bytes);
            return 0;
        }
        catch { return 1; }
    }
}
