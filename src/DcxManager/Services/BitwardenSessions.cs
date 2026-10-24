using System.Text.RegularExpressions;
using DcxManager.Bitwarden;

namespace DcxManager.Services;

/// <summary>
/// Bitwarden sign-ins piped into connections (0.6.14, ADR-093), the way Password Safe checkouts are: a saved host can be
/// linked to a vault item ("pm:") or a Secrets Manager secret ("sm:"); only the id and a label are stored on the host.
/// Connecting reads the item's password or SSH key and hands it to the SSH / Remote Desktop / VNC session as an in-memory
/// credential (HostEntry.Transient: never saved, never in a file, a log or the audit trail). A locked vault asks for the
/// master password first.
/// </summary>
public static class BitwardenSessions
{
    private static readonly Regex LinkRx = new(@"^(pm|sm):([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\|([^\p{Cc}]{1,200})$", RegexOptions.CultureInvariant);   // 0.6.17: no control characters in a label (found by the fuzzer)

    public static string Link(VaultItem i) => $"pm:{i.Id}|{Label(i.Name)}";
    public static string Link(SmSecret s) => $"sm:{s.Id}|{Label(s.Key)}";
    private static string Label(string name) { var n = string.Concat(name.Where(c => !char.IsControl(c) && c != '|')).Trim(); return n.Length == 0 ? "item" : n.Length > 200 ? n[..200] : n; }

    public static (bool Secrets, string Id, string Label)? Parse(string link) =>
        LinkRx.Match(link ?? "") is { Success: true } m ? (m.Groups[1].Value == "sm", m.Groups[2].Value, m.Groups[3].Value) : null;

    /// <summary>"Bitwarden: name" or "Secrets Manager: key" for a host's link.</summary>
    public static string Describe(string link) => Parse(link) is { } l ? (l.Secrets ? "Secrets Manager: " : "Bitwarden: ") + l.Label : "";

    /// <summary>Self-test: the master password the unlock prompt would have returned (no dialog in the self-test).</summary>
    internal static Func<string?>? MasterPasswordForTest { get; set; }
    /// <summary>Self-test: whether "Remember on this PC" was ticked at that prompt.</summary>
    internal static bool RememberForTest { get; set; }

    // ---- the remembered master password (0.6.20; DarkCipher0x: "it should also allow you to save your master password
    // encrypted, so that it remembers the next time you use it and launch ServerManager") ------------------------------
    // Kept with the app's other integration secrets (AppSettings.ObserveSecrets: AES-256-GCM in the app's store, never in
    // the registry, a backup or a policy), bound to the server it unlocks, only when the user ticks "Remember" and the
    // policy allows it. A password Bitwarden refuses (changed since) is forgotten and asked for again.
    private const string MasterKey = "bw:master", MasterServerKey = "bw:master-server";

    private static string? Remembered()
    {
        var s = AppSettings.Current;
        if (!s.BitwardenAllowRemember || s.ObserveSecrets.GetValueOrDefault(MasterKey) is not { Length: > 0 } pw) return null;
        return s.ObserveSecrets.GetValueOrDefault(MasterServerKey) == BitwardenVault.ServerUrl(s.BitwardenServer) ? pw : null;
    }

    /// <summary>A master password is remembered for the configured server (and the policy allows using it).</summary>
    public static bool IsRemembered => Remembered() != null;

    /// <summary>Forgets the remembered master password (settings, sign-out, a server change, the policy, a refusal).</summary>
    public static void Forget(string why)
    {
        var s = AppSettings.Current;
        bool had = s.ObserveSecrets.Remove(MasterKey);
        had |= s.ObserveSecrets.Remove(MasterServerKey);
        if (!had) return;
        try { AppSettings.Save(); } catch (Exception ex) { Log.Write("Bitwarden: forget the master password", ex); }
        AuditLog.Write("you", "bitwarden", "remembered Bitwarden master password forgotten", why);
    }

    private static void Remember(string pw)
    {
        var s = AppSettings.Current;
        if (!s.BitwardenAllowRemember) return;
        s.ObserveSecrets[MasterKey] = pw;
        s.ObserveSecrets[MasterServerKey] = BitwardenVault.ServerUrl(s.BitwardenServer);
        AppSettings.Save();
        AuditLog.Write("you", "bitwarden", "Bitwarden master password remembered on this PC (encrypted)", BitwardenVault.ServerLabel(s.BitwardenServer));
    }

    /// <summary>bw's answer to a wrong master password.</summary>
    private static bool WrongPassword(Exception ex) => ex.Message.Contains("Invalid master password", StringComparison.OrdinalIgnoreCase);

    /// <summary>At start-up and after the app unlocks: with a remembered master password, the vault unlocks in the
    /// background (its list is then read and synced), so the first search or sign-in doesn't wait. Never prompts.</summary>
    public static async Task AutoUnlockAsync(string when)
    {
        if (!AppSettings.Current.BitwardenAllowRemember) Forget("remembering it was switched off");
        var s = AppSettings.Current;
        if (!s.BitwardenEnabled || BitwardenVault.Unlocked || Remembered() is not { } pw) return;
        if (BitwardenCli.Fake == null && (BitwardenCli.Find(BitwardenCli.Tool.Bw) is not { } bw || !BitwardenCli.Check(bw).Ok)) return;
        try
        {
            await BitwardenVault.UnlockAsync(pw);
            AuditLog.Write("app", "bitwarden", "Bitwarden vault unlocked with the remembered master password", when);
        }
        catch (InvalidOperationException ex) when (WrongPassword(ex)) { Forget("Bitwarden refused it (the master password changed?)"); }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.IO.IOException or System.ComponentModel.Win32Exception or OperationCanceledException)
        { Log.Write("Bitwarden unlock with the remembered master password", ex); }
    }

    /// <summary>Unlocks the vault when it is locked: with the remembered master password, else by asking for it (it is
    /// kept only when "Remember on this PC" is ticked). False when cancelled.</summary>
    public static async Task<bool> EnsureUnlockedAsync(string why, CancellationToken ct = default)
    {
        if (BitwardenVault.Unlocked) return true;
        if (!AppSettings.Current.BitwardenAllowRemember) Forget("remembering it was switched off");
        // 0.6.16: everything that can be checked is checked before the master password is asked for, so a missing CLI or
        // API key is said plainly instead of failing after the prompt.
        if (BitwardenCli.Fake == null)   // (the self-test's scripted bw needs no file)
        {
            if (BitwardenCli.Find(BitwardenCli.Tool.Bw) is not { } bw) throw new InvalidOperationException(BitwardenCli.Missing);
            if (BitwardenCli.Check(bw) is { Ok: false } t) throw new InvalidOperationException(t.Why);
        }
        var st = await BitwardenVault.StatusAsync(ct);
        if (st.State == "unauthenticated" && (AppSettings.Current.ObserveSecrets.GetValueOrDefault("bw:clientsecret") ?? "").Length == 0)
            throw new InvalidOperationException("Add your personal API key first: Settings → Integrations → Bitwarden → Server & sign-in (web vault → Settings → Security → Keys → View API key).");
        if (Remembered() is { } saved)
        {
            try
            {
                await BitwardenVault.UnlockAsync(saved, ct);
                AuditLog.Write("you", "bitwarden", "Bitwarden vault unlocked with the remembered master password", why);
                return true;
            }
            catch (InvalidOperationException ex) when (WrongPassword(ex)) { Forget("Bitwarden refused it (the master password changed?)"); }   // asked for below
        }
        var server = BitwardenVault.ServerLabel(AppSettings.Current.BitwardenServer);
        bool allow = AppSettings.Current.BitwardenAllowRemember;
        // 0.6.17: on the UI thread whoever asks (a vault credential can be read from a connection's worker thread).
        var (pw, remember) = MasterPasswordForTest != null ? (MasterPasswordForTest(), RememberForTest)
            : allow ? Ui.Thread.Invoke(() => Ui.Dialogs.PromptPassword("Unlock Bitwarden", $"Master password for {server} ({why}).", "Remember on this PC (encrypted with the app's other secrets; unlocks Bitwarden without asking, also when the app starts)"))
            : (Ui.Thread.Invoke(() => Ui.Dialogs.Prompt("Unlock Bitwarden", $"Master password for {server} ({why}). It unlocks the vault on this PC and isn't stored.", "", password: true)), false);
        if (string.IsNullOrEmpty(pw)) return false;
        await BitwardenVault.UnlockAsync(pw, ct);
        if (remember && allow) Remember(pw);
        return true;
    }

    /// <summary>Reads the host's linked item and returns an in-memory copy of the host carrying it (or the host itself when
    /// it isn't linked).</summary>
    public static async Task<HostEntry> PrepareAsync(HostEntry host, CancellationToken ct = default)
    {
        if (Parse(host.BitwardenItem) is not { } link || host.Transient != null) return host;
        if (!AppSettings.Current.BitwardenAllowSessions) throw new InvalidOperationException("Signing in sessions with Bitwarden items is switched off on this PC (policy).");
        VaultSecret secret;
        if (link.Secrets) secret = await BitwardenSecrets.ValueAsync(link.Id, ct);
        else
        {
            if (!await EnsureUnlockedAsync("to sign in to " + host.Display, ct)) throw new OperationCanceledException("the vault stayed locked");
            secret = await BitwardenVault.SecretAsync(link.Id, ct);
        }
        var h = host.Clone();
        if (secret.Username.Length > 0 && (h.Username.Length == 0 || h.Protocol == "ssh" && !secret.IsKey)) h.Username = secret.Username;
        h.Auth = secret.IsKey ? AuthMethod.PrivateKey : h.Auth == AuthMethod.KeyboardInteractive ? AuthMethod.KeyboardInteractive : AuthMethod.Password;
        h.Transient = new Credential
        {
            Name = link.Label + (link.Secrets ? " (Bitwarden Secrets Manager)" : " (Bitwarden)"),
            Kind = secret.IsKey ? CredentialKind.PrivateKey : CredentialKind.Password, Username = h.Username, Secret = secret.Secret,
        };
        if (h.Protocol == "rdp" && !secret.IsKey) h.Rdp.SignOn = "password";
        AuditLog.Write("you", host.Display, "Bitwarden credential used for a session", $"{(link.Secrets ? "Secrets Manager" : "vault")}: {link.Label} ({(secret.IsKey ? "SSH key" : "password")})");
        return h;
    }
}
