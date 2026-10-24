using System.Windows;
using System.Windows.Controls;
using DcxManager.Services;

namespace DcxManager.Views;

/// <summary>
/// The master-password and Windows Hello steps, shared by Settings → Security and the first-run setup (0.3.0 §19) so
/// both do exactly the same thing: a password of 12+ characters, "require at start" (the Windows-account copy of the
/// data key is removed — then every file the app writes is readable only with the password or Hello), and Hello on top
/// (needs the password as its fallback). Nothing here stores a password.
/// </summary>
public static class SecuritySetupUi
{
    /// <summary>Sets or changes the master password (asks for the current one first). True when it changed.</summary>
    public static bool SetMasterPassword(Window? owner = null, bool requireDefault = true)
    {
        if (SecureStore.HasPassword)
        {
            if (Dialogs.Prompt("Master password", "Current master password:", password: true) is not { } cur) return false;
            if (!SecureStore.VerifyPassword(cur)) { Dialogs.Error("Master password", "That's not the current password."); return false; }
        }
        var w = Dialogs.MakeWindow("Master password", 460);
        if (owner != null) w.Owner = owner;
        var p = new StackPanel { Margin = new Thickness(18) };
        TextBlock L(string t) => new() { Text = t, Style = (Style)Application.Current.Resources["Label"], Margin = new Thickness(0, 8, 0, 4) };
        var a = new PasswordBox(); var b = new PasswordBox();
        var require = new CheckBox { Content = "Require it every time the app starts — the whole application is then encrypted under it (the Windows-account copy of the key is removed)", IsChecked = requireDefault, Margin = new Thickness(0, 10, 0, 0) };
        p.Children.Add(L("NEW PASSWORD (12+ characters)")); p.Children.Add(a); p.Children.Add(L("AGAIN")); p.Children.Add(b); p.Children.Add(require);
        p.Children.Add(new TextBlock { Text = "There is no recovery if you forget it while “require” is on. Keep an encrypted backup (Settings → Security).", Style = (Style)Application.Current.Resources["Hint"], TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
        var ok = new Button { Content = "Set password", Style = (Style)Application.Current.Resources["PrimaryButton"], IsDefault = true };
        var bs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        bs.Children.Add(ok); bs.Children.Add(new Button { Content = "Cancel", IsCancel = true });
        p.Children.Add(bs);
        w.Content = p;
        ok.Click += (_, _) =>
        {
            if (a.Password.Length < 12) { Dialogs.Error("Master password", "Use at least 12 characters."); return; }
            if (a.Password != b.Password) { Dialogs.Error("Master password", "The passwords don't match."); return; }
            w.DialogResult = true;
        };
        w.Loaded += (_, _) => a.Focus();
        if (w.ShowDialog() != true) return false;
        SecureStore.SetPassword(a.Password, require.IsChecked == true);
        AuditLog.Write("you", "store", require.IsChecked == true ? "master password set (required on start — whole application encrypted under it)" : "master password set");
        AppLock.ReconcileSandbox();
        MainWindow.Instance?.RefreshLockText();
        return true;
    }

    /// <summary>Turns Windows Hello sign-in on (the key is created and the data key wrapped with it). True when it is on.</summary>
    public static async Task<bool> TurnOnHelloAsync()
    {
        if (!SecureStore.HasPassword) { Dialogs.Info("Windows Hello", "Set a master password first: it opens the app when Windows Hello can't (another PC, a new face or fingerprint reader, a reset PIN)."); return false; }
        if (!await WindowsHello.IsAvailableAsync()) { Dialogs.Info("Windows Hello", "Windows Hello isn't set up for your account. Set up a PIN, face or fingerprint in Windows Settings → Accounts → Sign-in options, then try again."); return false; }
        if (!SecureStore.HasHello)
        {
            var (name, challenge) = SecureStore.NewHelloSetup();
            var sig = await WindowsHello.CreateAndSignAsync(name, challenge);
            if (sig == null) { Dialogs.Error("Windows Hello", "Windows Hello didn't create the key (cancelled or not available). Nothing changed."); return false; }
            SecureStore.SetHello(name, challenge, sig);
            AuditLog.Write("you", "store", "Windows Hello sign-in turned on", "the Windows-account copy of the data key was removed");
        }
        var protectedOk = await ProtectedSettings.ChangeAsync("turn on Windows Hello sign-in", "signin.hello", true);
        if (!protectedOk) MainWindow.Instance?.Status("Windows Hello sign-in is on, but not protected by an administrator.");
        MainWindow.Instance?.RefreshLockText();
        return true;
    }

    /// <summary>One line on how the store is protected right now.</summary>
    public static string ProtectionText() => SecureStore.HasHello
        ? "🔒 Whole application encrypted — opened with Windows Hello or your master password; no Windows-account copy of the key."
        : SecureStore.PasswordRequiredOnStart ? "🔒 Whole application encrypted under your master password — asked every time the app starts."
        : SecureStore.HasPassword ? "🔒 Encrypted; the key is also held by your Windows account (DPAPI) — anyone signed in as you can open the app. A master password exists for recovery."
        : "🔒 Encrypted (AES-256-GCM); the key is held by your Windows account (DPAPI) — anyone signed in as you can open the app.";
}
