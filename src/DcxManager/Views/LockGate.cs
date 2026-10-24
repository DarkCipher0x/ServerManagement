using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text.Json;
using DcxManager.Services;

namespace DcxManager.Views;

/// <summary>
/// The interactive side of locks (0.5.1, ADR-067; the rules are <see cref="Locks"/>):
/// <list type="bullet">
/// <item><see cref="EnsureAsync"/> — a screen or a user action: opens every closed lock that guards the target, asking for
/// each its way (admin approval, Windows sign-in or its password). Refusing leaves it closed and is audited.</item>
/// <item><see cref="Demand"/> — the AI and anything without a person in front of it: refuses a closed lock, never asks.</item>
/// <item><see cref="Refuse"/> — add-ons and the local API: refuse a locked item even while it is open.</item>
/// <item><see cref="VerifyAdminAsync"/> — Windows admin approval for an action (installing an add-on…), no lock involved.</item>
/// </list>
/// Admin approval starts the installed app elevated with <see cref="VerifyArg"/> and a one-time request; that copy shows
/// what is being approved and exits 0 only when an administrator approves. A copy that can't elevate (portable,
/// development) asks for Windows sign-in instead and says so. Every lock closes when the app or Windows locks, and when
/// its time is up (a one-minute check).
/// </summary>
public static class LockGate
{
    public const string VerifyArg = "--verify-admin";
    public const string RequestFormat = "dcx-admin-verify";

    /// <summary>The self-test's stand-ins (never a prompt): admin approval and the password prompt.</summary>
    internal static Func<string, bool>? FakeAdmin { get; set; }
    internal static Func<string, string?>? FakePassword { get; set; }

    private static System.Windows.Threading.DispatcherTimer? _timer;

    /// <summary>Start-up: the expiry check, and closing every lock when Windows locks or the session ends.</summary>
    public static void Install()
    {
        _timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _timer.Tick += (_, _) => Locks.Expire();
        _timer.Start();
        Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPower;
    }

    private static void OnSessionSwitch(object? s, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        if (e.Reason is Microsoft.Win32.SessionSwitchReason.SessionLock or Microsoft.Win32.SessionSwitchReason.ConsoleDisconnect or Microsoft.Win32.SessionSwitchReason.RemoteDisconnect)
            Ui.Thread.Post(() => Locks.CloseAll("Windows locked"));
    }

    private static void OnPower(object? s, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode == Microsoft.Win32.PowerModes.Suspend) Ui.Thread.Post(() => Locks.CloseAll("the PC went to sleep"));
    }

    public static void Uninstall()
    {
        _timer?.Stop();
        Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPower;
    }

    /// <summary>Opens what guards <paramref name="target"/>, asking for each closed lock. True when it is open now.</summary>
    public static async Task<bool> EnsureAsync(string target, string what)
    {
        foreach (var r in Locks.Governing(target))
        {
            if (Locks.IsOpenRule(r.Target)) continue;
            if (!await OpenRuleAsync(r, what)) return false;
        }
        return true;
    }

    /// <summary>Opens one lock (its own way). True when it opened.</summary>
    public static async Task<bool> OpenRuleAsync(Locks.Effective r, string what)
    {
        var purpose = $"Unlock “{r.Title}”" + (what.Length > 0 && what != r.Title ? $" to {what}" : "");
        bool ok = r.Mode switch
        {
            Locks.Password => AskPassword(r, purpose),
            Locks.SignIn => await SignInAsync(purpose),
            _ => await VerifyAdminAsync(purpose),
        };
        AuditLog.Write("you", "locks", ok ? "unlocked" : "unlock refused", $"{r.Title} ({r.Target}) · {Locks.ModeTitle(r.Mode)}" + (what.Length > 0 ? " · " + what : ""));
        if (ok) { Locks.MarkOpen(r.Target); Locks.RaiseChanged(); }
        return ok;
    }

    /// <summary>For the AI and background work: throws when a lock guarding the target is closed (never asks).</summary>
    public static void Demand(string target, string actor) => Locks.Demand(target, TitleOf(target));

    /// <summary>For add-ons and the local API: a locked item is never theirs, open or not.</summary>
    public static void Refuse(string target, string actor) => Locks.Refuse(target, actor);

    private static string TitleOf(string target) => Locks.Governing(target).FirstOrDefault()?.Title ?? target;

    private static bool AskPassword(Locks.Effective r, string purpose)
    {
        if (r.User is not { } rule) return false;
        if (Locks.WaitLeft(rule) is { TotalSeconds: > 0 } w) { Ui.Dialogs.Info("Locked", $"Too many wrong passwords — try again in {Math.Ceiling(w.TotalSeconds):0} s."); return false; }
        var pw = FakePassword != null ? FakePassword(purpose) : Dialogs.Prompt("Locked", purpose + "\n\nEnter this lock's password.", password: true);
        if (pw == null) return false;
        var res = Locks.Verify(rule, pw);
        if (res == Locks.Result.Ok) return true;
        if (FakePassword == null) Ui.Dialogs.Info("Locked", res == Locks.Result.Wait ? "Too many wrong passwords — wait a little and try again." : "Wrong password.");
        return false;
    }

    private static async Task<bool> SignInAsync(string purpose) =>
        FakeAdmin != null ? FakeAdmin("signin: " + purpose) : await WindowsSignIn.ConfirmAsync(purpose) == WindowsSignIn.Outcome.Verified;

    /// <summary>Windows admin approval (UAC) for <paramref name="purpose"/>. A copy that can't start itself elevated asks for
    /// Windows sign-in instead (and the audit says so).</summary>
    public static async Task<bool> VerifyAdminAsync(string purpose)
    {
        if (FakeAdmin != null) return FakeAdmin(purpose);
        if (TestMode.Running) { Dialogs.RefusedInSelfTest("admin approval (UAC): " + purpose); return false; }   // never a UAC prompt in the self-test
        if (!AdminGate.CanElevate)
        {
            AuditLog.Write("app", "locks", "admin approval not possible here: Windows sign-in asked instead", purpose);
            return await WindowsSignIn.ConfirmAsync(purpose + "\n\n(This copy isn't the installed app, so it can't ask for admin approval.)") == WindowsSignIn.Outcome.Verified;
        }
        var req = new Request { Id = Guid.NewGuid().ToString("N"), Sid = ProtectedSettings.CurrentSid, User = WindowsIdentity.GetCurrent().Name, Name = AppIdentity.Name, RequestedUtc = DateTime.UtcNow, Purpose = purpose.Length > 300 ? purpose[..300] : purpose };
        var dir = Path.Combine(Path.GetTempPath(), AppInfo.RequestsTempFolder);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, req.Id + ".verify.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(req, SecureStore.Json));
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
            psi.ArgumentList.Add(VerifyArg);
            psi.ArgumentList.Add(path);
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("the elevated copy did not start");
            await p.WaitForExitAsync();
            return p.ExitCode == 0;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return false; }   // the user said no at UAC
        catch (Exception ex) { Log.Write("Admin approval", ex); return false; }
        finally { try { File.Delete(path); } catch { } }
    }

    /// <summary>What the elevated copy is asked to approve.</summary>
    public sealed class Request
    {
        public string Format { get; set; } = RequestFormat;
        public string Id { get; set; } = "";
        public string Sid { get; set; } = "";
        public string User { get; set; } = "";
        /// <summary>The asking profile's name (0.5.7), shown first; <see cref="User"/> (the account) stays beside it.</summary>
        public string Name { get; set; } = "";
        public DateTime RequestedUtc { get; set; }
        public string Purpose { get; set; } = "";
    }

    /// <summary>The elevated side (App.OnStartup, before anything else): only the installed copy, only elevated, only a
    /// fresh request; shows it and exits 0 when an administrator approves, 1 when not, 2 when refused.</summary>
    public static int RunElevatedSide(string requestPath)
    {
        string? refuse = null;
        Request? req = null;
        if (!Environment.IsPrivilegedProcess) refuse = "This step needs administrator rights (it is started through a Windows UAC prompt).";
        else if (!AdminGate.InstalledCopy) refuse = "Only the installed app (under Program Files) may approve this.";
        else
        {
            try
            {
                var fi = new FileInfo(requestPath);
                if (!fi.Exists || fi.Length > 8192) throw new InvalidDataException("the request is missing or too large");
                req = JsonSerializer.Deserialize<Request>(File.ReadAllBytes(requestPath), SecureStore.Json) ?? throw new InvalidDataException("empty request");
                var now = DateTime.UtcNow;
                if (req.Format != RequestFormat || req.RequestedUtc > now + TimeSpan.FromMinutes(1) || now - req.RequestedUtc > TimeSpan.FromMinutes(5)) throw new InvalidDataException("the request has expired");
                _ = new SecurityIdentifier(req.Sid);
            }
            catch (Exception ex) { refuse = "The request was refused: " + ex.Message + "."; req = null; }
        }
        var w = new AdminApproveWindow(req?.Purpose ?? "", req == null ? "" : AppIdentity.Clean(req.Name) is { Length: > 0 } who ? $"{who} (Windows account {req.User})" : req.User, refuse);
        w.ShowDialog();
        if (refuse != null) return 2;
        return w.Approved ? 0 : 1;
    }
}
