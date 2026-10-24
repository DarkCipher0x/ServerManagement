using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using DcxManager.Views;

namespace DcxManager.Services;

/// <summary>
/// The app's side of admin-protected settings (0.3.0 §1, ADR-039). A change to a protected setting:
/// <list type="number">
/// <item>asks for Windows Hello first when "Confirm sensitive actions with Windows Hello" is on;</item>
/// <item>applies a stricter value (offline mode on, Telnet off…) for this session at once, so tightening never waits;</item>
/// <item>writes a one-time request and starts the installed app elevated (UAC) with <see cref="Arg"/>: that copy shows
/// the administrator exactly what changes and writes it to the admin-only store (<see cref="AdminChangeWindow"/>);</item>
/// <item>reads the store back and applies it.</item>
/// </list>
/// Only the installed copy (under Program Files, which only administrators can change) is started elevated: elevating a
/// copy in a user-writable folder would let anything running as the user swap the program or its files first. A portable
/// or development copy can't protect settings; it says so, and changes them as before while nothing is protected yet.
/// </summary>
public static class AdminGate
{
    public const string Arg = "--protected-change";

    /// <summary>The self-test's stand-in for UAC + the admin window: request path → exit code. Never a prompt.</summary>
    internal static Func<string, int>? FakeElevate { get; set; }

    /// <summary>This copy is the installed one (under Program Files), the only kind that may be started elevated.</summary>
    public static bool InstalledCopy
    {
        get
        {
            var exe = Environment.ProcessPath;
            if (exe == null) return false;
            foreach (var pf in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
                if (pf.Length > 0 && exe.StartsWith(pf.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    public static bool CanElevate => FakeElevate != null || (!TestMode.Running && InstalledCopy);

    /// <summary>Raised on the UI thread after protected settings changed (screens refresh).</summary>
    public static event Action<IReadOnlyCollection<string>>? Changed;

    /// <summary>Start-up: wire the gate and read this user's protected values.</summary>
    public static void Install()
    {
        ProtectedSettings.Gate = ChangeAsync;
        // The AI features lock can use Windows Hello instead of a password (0.3.0 §2).
        FeatureLock.HelloCheck = purpose => WindowsHello.Confirm("AI features lock: " + purpose) == WindowsHello.Outcome.Verified;
        ProtectedSettings.Reload();
        if (ProtectedSettings.Problem is { } p) AuditLog.Write("app", "protected settings", "protected settings ignored", p);
    }

    public static async Task<bool> ChangeAsync(string reason, IReadOnlyDictionary<string, JsonNode?> changes)
    {
        var s = AppSettings.Current;
        var real = changes.Where(kv => ProtectedSettings.Find(kv.Key) is { } it && !JsonNode.DeepEquals(it.Get(s), kv.Value))
                          .ToDictionary(kv => kv.Key, kv => kv.Value);
        if (real.Count == 0 && (ProtectedSettings.Active || !CanElevate)) return true;

        // Group Policy (0.3.0 §10) outranks admin protection: a managed setting can't be changed here at all.
        var managed = real.Keys.Select(k => ProtectedSettings.Find(k)!.Setting).Where(PolicySettings.IsManaged).ToList();
        if (managed.Count > 0)
        {
            Ui.Dialogs.Info("Managed by your organisation", string.Join(", ", managed.Select(m => PolicySettings.Get(m)!.Entry.Title)) +
                " is set by your organisation's policy (Group Policy). Ask your administrator to change it.");
            return false;
        }

        if (!CanElevate)
        {
            if (ProtectedSettings.Active)
            {
                Ui.Dialogs.Error("Admin protection", "An administrator protects these settings on this PC. Change them from the installed app (Start menu), which can ask Windows for admin approval.\n\nThis copy (" + Environment.ProcessPath + ") is not the installed one.");
                return false;
            }
            ProtectedSettings.ApplyDirect(real);
            AuditLog.Write("you", "protected settings", "changed without admin protection (this copy is not the installed app)", string.Join(", ", real.Keys) + " · " + reason);
            After(real.Keys);
            return true;
        }

        if (s.HelloConfirm)
        {
            var o = await WindowsHello.ConfirmAsync("Confirm a security setting change: " + reason);
            if (o is WindowsHello.Outcome.Cancelled or WindowsHello.Outcome.Failed)
            {
                AuditLog.Write("you", "protected settings", "change stopped: Windows Hello not confirmed", reason);
                return false;
            }
        }

        // Stricter values take effect for this session straight away; the admin step makes them stick.
        var tighten = real.Where(kv => ProtectedSettings.Find(kv.Key)?.Tighten is { } t && t(ProtectedSettings.Find(kv.Key)!.Get(s), kv.Value))
                          .ToDictionary(kv => kv.Key, kv => kv.Value);
        if (tighten.Count > 0) { ProtectedSettings.ApplyDirect(tighten); After(tighten.Keys); }

        ProtectedSettings.Request req;
        try { req = ProtectedSettings.BuildRequest(s, changes, reason); }
        catch (ArgumentException ex) { Ui.Dialogs.Error("Admin protection", ex.Message); return false; }
        var path = ProtectedSettings.WriteRequestFile(req);
        int code;
        try { code = FakeElevate != null ? FakeElevate(path) : await RunElevatedAsync(path); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { code = 1; }   // UAC: the user said no
        catch (Exception ex) { Log.Write("Admin protection", ex); code = 3; }
        finally { try { File.Delete(path); } catch { } }

        if (code == 0)
        {
            ProtectedSettings.Reload();
            ProtectedSettings.ApplyTo(s);
            AppSettings.Save();
            AuditLog.Write("you", "protected settings", "changed with administrator approval", string.Join(", ", req.Values.Keys.Where(changes.ContainsKey)) + " · " + reason);
            After(req.Values.Keys);
            Changed?.Invoke(req.Values.Keys.ToList());
            return true;
        }
        AuditLog.Write("you", "protected settings", code switch { 1 => "change cancelled at the administrator prompt", 2 => "change refused: the request was not valid", _ => "change failed" },
            string.Join(", ", changes.Keys) + " · " + reason);
        if (tighten.Count > 0)
            Ui.Window.Status("Applied for this session only: without administrator approval it goes back after a restart.");
        return false;
    }

    private static async Task<int> RunElevatedAsync(string requestPath)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
        psi.ArgumentList.Add(Arg);
        psi.ArgumentList.Add(requestPath);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("the elevated copy did not start");
        await p.WaitForExitAsync();
        return p.ExitCode;
    }

    /// <summary>What has to follow a change of these keys straight away.</summary>
    private static void After(IEnumerable<string> keys)
    {
        var k = keys.ToList();
        if (k.Any(x => x.StartsWith("net.offline", StringComparison.Ordinal))) Offline.Reapply();
        if (k.Any(x => x.StartsWith("net.localApi", StringComparison.Ordinal))) Api.LocalServer.Sync();
    }

    // ---- the elevated side ------------------------------------------------------------------------------------------

    /// <summary>Started elevated with <see cref="Arg"/> (App.OnStartup, before anything else): checks where it runs and that
    /// it really is elevated, reads the request, shows it, and writes it if the administrator approves. Exit codes: 0
    /// written, 1 cancelled, 2 refused (invalid request or not allowed to run here).</summary>
    public static int RunElevatedSide(string requestPath)
    {
        string? refuse = null;
        if (!Environment.IsPrivilegedProcess) refuse = "This step needs administrator rights (it is started through a Windows UAC prompt).";
        else if (!InstalledCopy) refuse = "Only the installed app (under Program Files) may change protected settings.";
        ProtectedSettings.Plan? plan = null;
        if (refuse == null)
        {
            try { plan = ProtectedSettings.ReadRequest(requestPath); }
            catch (InvalidDataException ex) { refuse = "The request was refused: " + ex.Message + "."; }
            catch (Exception ex) { refuse = "The request could not be read: " + ex.Message; }
        }
        var w = new AdminChangeWindow(plan, refuse);
        w.ShowDialog();
        if (plan == null || refuse != null) return 2;
        if (!w.Approved) return 1;
        try
        {
            ProtectedSettings.Commit(plan, ProtectedSettings.Approver(plan));   // 0.5.7: the profile's name when you approve your own change
            return 0;
        }
        catch (Exception ex)
        {
            Ui.Dialogs.Error("Admin protection", "The protected settings could not be written: " + ex.Message);
            return 2;
        }
    }
}
