using System.Windows;

namespace DcxManager.Services;

/// <summary>
/// "Confirm sensitive actions with Windows Hello" (0.3.0 §2, ADR-039): before a password or private key is shown, a
/// private key or backup is exported, or a vault item is opened for editing, the user verifies with Windows Hello. When
/// Hello isn't available (no PIN set up, a remote desktop session) the master password is asked instead, if there is one.
/// Off by default; the switch is admin-protected. Every answer is audited, never what was typed.
/// </summary>
public static class SensitiveAction
{
    /// <summary>True when the action may go ahead.</summary>
    public static bool Confirm(string what, Window? owner = null)
    {
        if (!AppSettings.Current.HelloConfirm) return true;
        var o = WindowsHello.Confirm("Confirm: " + what, owner);
        switch (o)
        {
            case WindowsHello.Outcome.Verified:
                AuditLog.Write("you", "confirm", "Windows Hello confirmed", what);
                return true;
            case WindowsHello.Outcome.NotAvailable when SecureStore.HasPassword:
                var ok = Ui.Dialogs.Prompt("Confirm", $"Windows Hello isn't available. Master password to {what}:", password: true) is { } pw && SecureStore.VerifyPassword(pw);
                AuditLog.Write("you", "confirm", ok ? "master password confirmed (Windows Hello not available)" : "not confirmed: wrong or no master password", what);
                if (!ok) Ui.Dialogs.Error("Confirm", "Not confirmed. Nothing was done.");
                return ok;
            case WindowsHello.Outcome.NotAvailable:
                AuditLog.Write("you", "confirm", "allowed without confirmation: Windows Hello not available and no master password", what);
                return true;
            default:
                AuditLog.Write("you", "confirm", "not confirmed (Windows Hello cancelled or failed)", what);
                return false;
        }
    }
}
