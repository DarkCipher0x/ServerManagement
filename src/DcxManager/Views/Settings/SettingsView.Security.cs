using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using DcxManager.Services;
using DcxManager.Sessions;
using DcxManager.Ssh;
using DcxManager.Vultr;
using Microsoft.Win32;

namespace DcxManager.Views;

// SettingsView: the Security & encryption card: master password, 2FA, auto-lock, sandbox, backup (moved unchanged from SettingsView.cs in 0.2.0 phase 6½).
public sealed partial class SettingsView
{
    // ---- security --------------------------------------------------------------------------------------------------

    private Border Security()
    {
        UpdateStore();
        return Card("Security & encryption",
            "All hosts, credentials, settings, API keys, known hosts, snippets, tunnels and the audit log are encrypted with AES-256-GCM under one random data key. " +
            "Add a master password to require it at start-up — then the files can't be opened even by an administrator or from a copy of your disk.",
            _storeStatus,
            Row(Btn("Set / change master password…", SetPassword, "PrimaryButton"), Btn("Remove master password", RemovePassword), Btn("Encrypted backup / export…", Backup), Btn("Import / restore…", Restore)),
            Row(Btn("✓ Verify everything is encrypted", VerifyEncryption), _encryptionStatus),
            L("TWO-FACTOR SIGN-IN (authenticator app)"), _totpStatus,
            Row(Btn("Set up 2FA…", SetupTotp), Btn("New recovery codes…", NewRecoveryCodes), Btn("Turn off 2FA…", RemoveTotp)),
            L("WINDOWS HELLO"), _helloStatus,
            _helloSwitch = ProtectedCheck("signin.hello", "Sign in with Windows Hello (face, fingerprint or PIN) — the master password stays as the fallback", SetHelloAsync, () => SecureStore.HasHello),
            ProtectedCheck("signin.helloConfirm", "Confirm sensitive actions with Windows Hello: showing a password or key, exports and backups, and security setting changes"),
            L("APP LOCK"),
            Row(Labeled("LOCK AFTER INACTIVITY", AutoLockBox()), Btn("🔒 Lock now (Ctrl+Shift+L)", () => MainWindow.Instance.LockNow())),
            ProtectedCheck("lock.onWindowsLock", "Lock when Windows locks or the PC sleeps"),
            _sandbox = ProtectedCheck("lock.sandbox", "Sandbox mode — master password required at start, auto-lock, temporary share/RDP files wiped on exit (error logs are always encrypted)", SetSandboxAsync),
            Row(Check("Ask before every change (confirmations)", _s.ConfirmDestructive, v => _s.ConfirmDestructive = v)),
            L("POST-QUANTUM"), PostQuantumStatus(),
            ProtectedCheck("net.pqSshRequired", "Require post-quantum key exchange for SSH: a server without one is refused instead of connected classically"));
    }

    /// <summary>What is post-quantum and what isn't (0.6.12, ADR-090), with a live check of the primitives.</summary>
    private static TextBlock PostQuantumStatus()
    {
        var ok = Pqc.Works();
        return new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = (ok ? $"✓ {Pqc.KemName} and {Pqc.SigName} work on this PC (checked now)." : "⚠ The post-quantum self-check failed on this PC; see the error log.") +
                   "\n• Everything saved (hosts, credentials, settings, notes, docs, logs, snapshots, backups): AES-256-GCM, which quantum computers don't break." +
                   "\n• The data key's protectors: your Windows account (DPAPI, AES) and the master password (PBKDF2 + AES) are quantum-safe; Windows Hello sign-in is a hybrid " +
                   (SecureStore.HasHello ? (SecureStore.HelloPostQuantum ? "(on and post-quantum ✓)" : "(upgrades itself at your next Windows Hello unlock)") : "when you turn it on") +
                   ": the Hello signature plus a secret only this Windows account can open, so a future quantum computer alone can't rebuild it. Still one Hello prompt." +
                   "\n• SSH: hybrid post-quantum key exchange (mlkem768x25519 / sntrup761x25519) is offered first; each session's strip says what was negotiated." +
                   $"\n• Shared library: new libraries and members use hybrid keys (P-256 + {Pqc.KemName} to wrap keys, P-256 + {Pqc.SigName} to sign); members who joined before 0.6.12 keep classical keys until they join again." +
                   $"\n• Updates: offered only with both release signatures (ECDSA P-256 and {Pqc.SigName})." +
                   "\n• Not post-quantum (outside this app's control): HTTPS to cloud and Microsoft services (Windows' TLS), Remote Desktop, SSH host and user key signatures, Entra sign-in tokens.",
        };
    }

    private readonly TextBlock _helloStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _encryptionStatus = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>0.3.0 §19: walks the store and the logs and says whether every file is sealed (the self-test does the same).</summary>
    private void VerifyEncryption()
    {
        var r = EncryptionAudit.Run(SecureStore.DataDir, Log.Dir);
        _encryptionStatus.Text = r.Summary;
        AuditLog.Write("you", "store", "encryption verified", r.Summary);
        if (r.Plain.Any()) Dialogs.Info("Encryption check", string.Join("\n", r.Plain.Select(p => $"{p.Path}\n   {p.Note}")));
    }
    private CheckBox? _helloSwitch;

    private void UpdateHello() =>
        _helloStatus.Text = SecureStore.HasHello
            ? "✓ On — the app opens with Windows Hello (a TPM-backed key signs to unwrap the data key). No Windows-account copy of the key is kept."
              + (_s.HelloUnlock ? "" : " Not yet protected by an administrator.")
            : _s.HelloUnlock ? "⚠ An administrator asked for Windows Hello sign-in, but it isn't set up for this store yet: switch it on to set it up."
            : "Off. Needs a master password first (the fallback) and Windows Hello set up in Windows Settings → Accounts → Sign-in options.";

    /// <summary>Hello sign-in on: create the Hello key and wrap the data key with it, then have an administrator protect
    /// the choice. Off: an administrator approves first, then the key and its wrap are removed.</summary>
    private async Task<bool> SetHelloAsync(bool on)
    {
        if (on)
        {
            var ok = await SecuritySetupUi.TurnOnHelloAsync();   // shared with the first-run setup (0.3.0 §19)
            UpdateStore();
            return ok;
        }
        if (!await ProtectedSettings.ChangeAsync("turn off Windows Hello sign-in", "signin.hello", false)) return false;
        if (SecureStore.HelloKeyName is { } key) await WindowsHello.DeleteAsync(key);
        if (SecureStore.HasHello) { SecureStore.RemoveHello(); AuditLog.Write("you", "store", "Windows Hello sign-in turned off"); }
        UpdateStore();
        return true;
    }

    private readonly TextBlock _totpStatus = new() { TextWrapping = TextWrapping.Wrap };
    private CheckBox? _sandbox;

    private ComboBox AutoLockBox()
    {
        var opts = new[] { (0, "never"), (5, "5 minutes"), (10, "10 minutes"), (15, "15 minutes"), (30, "30 minutes"), (60, "1 hour"), (240, "4 hours") };
        var box = SettingTag.For(new ComboBox { Width = 140, ItemsSource = opts.Select(o => o.Item2).ToList() }, nameof(AppSettings.AutoLockMinutes));
        box.SelectedIndex = Math.Max(0, Array.FindIndex(opts, o => o.Item1 == _s.AutoLockMinutes));
        bool busy = false;
        int Index() => Math.Max(0, Array.FindIndex(opts, o => o.Item1 == _s.AutoLockMinutes));
        box.SelectionChanged += async (_, _) =>
        {
            if (busy) return;
            var v = opts[Math.Max(0, box.SelectedIndex)].Item1;
            if (v == _s.AutoLockMinutes) return;
            busy = true;
            try
            {
                if (v > 0 && !AppLock.CanLock) { Dialogs.Info("Auto-lock", "Set a master password or turn on 2FA first — the lock screen needs something to unlock with."); return; }
                if (v == 0 && _s.SandboxMode) { Dialogs.Info("Auto-lock", "Sandbox mode needs auto-lock. Turn sandbox mode off first to choose “never”."); return; }
                await ProtectedSettings.ChangeAsync("change auto-lock", "lock.autoLockMinutes", v);   // admin-protected (0.3.0 §1)
            }
            finally { box.SelectedIndex = Index(); busy = false; }
        };
        return box;
    }

    private async Task<bool> SetSandboxAsync(bool on)
    {
        if (on && !SecureStore.PasswordRequiredOnStart)
        {
            if (!Dialogs.Confirm("Sandbox mode", "Sandbox mode makes the master password mandatory at every start (the Windows-account copy of the key is removed), so nothing opens without signing in.\n\nSet the master password now?", "Set password")) return false;
            SetPassword();
            if (!SecureStore.PasswordRequiredOnStart) { Dialogs.Info("Sandbox mode", "Not enabled — a master password with “require at start” is needed."); return false; }
        }
        var changes = new Dictionary<string, System.Text.Json.Nodes.JsonNode?> { ["lock.sandbox"] = on };
        if (on && _s.AutoLockMinutes == 0) changes["lock.autoLockMinutes"] = 15;
        if (!await ProtectedSettings.ChangeAsync(on ? "turn on sandbox mode" : "turn off sandbox mode", changes)) return false;
        AuditLog.Write("you", "app", on ? "sandbox mode on" : "sandbox mode off");
        AppLock.ReconcileSandbox();
        MainWindow.Instance?.RefreshLockText();
        return true;
    }

    private void UpdateTotp() =>
        _totpStatus.Text = SecureStore.HasTotp
            ? $"✓ On — a 6-digit code from your authenticator is needed to open or unlock the app. {SecureStore.RecoveryCodesLeft} recovery code(s) left."
            : "Off. Adds a second factor (Microsoft/Google Authenticator, Authy, 1Password, Bitwarden…) on top of the password / Windows account.";

    private bool ReauthPassword(string title)
    {
        if (!SecureStore.HasPassword) return true;
        if (Dialogs.Prompt(title, "Current master password:", password: true) is { } pw && SecureStore.VerifyPassword(pw)) return true;
        Dialogs.Error(title, "Wrong password.");
        return false;
    }

    private void SetupTotp()
    {
        if (!ReauthPassword("Set up 2FA")) return;
        var secret = Totp.NewSecret();
        var account = AppIdentity.Name;   // 0.5.7: the profile's name in the authenticator app, not the account and PC
        var uri = Totp.Uri(secret, account);
        var w = Dialogs.MakeWindow("Set up two-factor sign-in", 520);
        var p = new StackPanel { Margin = new Thickness(18) };
        p.Children.Add(new TextBlock { Text = "1. Scan this with your authenticator app (or type the key).", TextWrapping = TextWrapping.Wrap });
        try
        {
            using var gen = new QRCoder.QRCodeGenerator();
            using var data = gen.CreateQrCode(uri, QRCoder.QRCodeGenerator.ECCLevel.M);
            var png = new QRCoder.PngByteQRCode(data).GetGraphic(6, new byte[] { 0, 0, 0 }, new byte[] { 255, 255, 255 });
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit(); bmp.StreamSource = new MemoryStream(png); bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; bmp.EndInit(); bmp.Freeze();
            p.Children.Add(new Border { Background = System.Windows.Media.Brushes.White, Padding = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 10), Child = new System.Windows.Controls.Image { Source = bmp, Width = 220, Height = 220 } });
        }
        catch (Exception ex) { App.Log("QR", ex); }
        var key = string.Join(" ", Enumerable.Range(0, (Totp.Base32(secret).Length + 3) / 4).Select(i => Totp.Base32(secret).Substring(i * 4, Math.Min(4, Totp.Base32(secret).Length - i * 4))));
        p.Children.Add(new TextBox { Text = key, IsReadOnly = true, HorizontalContentAlignment = HorizontalAlignment.Center, Padding = new Thickness(4) }.Mono());
        p.Children.Add(new TextBlock { Text = $"Account: DarkCipher ServerManagement:{account} · issuer DarkCipher Systems · SHA1, 6 digits, 30 s", Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 10) });
        p.Children.Add(new TextBlock { Text = "2. Enter the 6-digit code it shows to confirm:", TextWrapping = TextWrapping.Wrap });
        var code = new TextBox { MaxLength = 7, Width = 140, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left }.Fs(FontStep.Code).Mono();
        p.Children.Add(code);
        var ok = new Button { Content = "Confirm", Style = (Style)FindResource("PrimaryButton"), IsDefault = true };
        var bs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        bs.Children.Add(ok); bs.Children.Add(new Button { Content = "Cancel", IsCancel = true });
        p.Children.Add(bs);
        w.Content = p;
        ok.Click += (_, _) =>
        {
            var c = new string(code.Text.Where(char.IsDigit).ToArray());
            long step = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
            if (Enumerable.Range(-1, 3).Any(d => Totp.Code(secret, step + d) == c)) w.DialogResult = true;
            else Dialogs.Error("2FA", "That code doesn't match. Check the phone's clock is set automatically and try the next code.");
        };
        w.Loaded += (_, _) => code.Focus();
        if (w.ShowDialog() != true) { System.Security.Cryptography.CryptographicOperations.ZeroMemory(secret); return; }
        var codes = Totp.RecoveryCodes();
        SecureStore.SetTotp(secret, codes);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(secret);
        AuditLog.Write("you", "app", "two-factor sign-in enabled");
        ShowRecovery(codes);
        UpdateTotp();
        if (AppLock.ReconcileSandbox() && _sandbox != null) _sandbox.IsChecked = _s.SandboxMode;
        MainWindow.Instance?.RefreshLockText();
    }

    private void ShowRecovery(List<string> codes)
    {
        var text = "DarkCipher ServerManagement — 2FA recovery codes\n" + $"Created {DateTime.Now:yyyy-MM-dd HH:mm} for {AppIdentity.Name}. Each code works once.\n\n" + string.Join("\n", codes);
        var w = Dialogs.MakeWindow("Recovery codes", 440);
        var p = new StackPanel { Margin = new Thickness(18) };
        p.Children.Add(new TextBlock { Text = "Store these somewhere safe (password manager, printed). Each one signs you in once if you lose your phone. They won't be shown again.", TextWrapping = TextWrapping.Wrap });
        p.Children.Add(new TextBox { Text = string.Join("\n", codes), IsReadOnly = true, Margin = new Thickness(0, 10, 0, 10), Padding = new Thickness(8), TextAlignment = TextAlignment.Center }.Fs(FontStep.Title).Mono());
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var copy = new Button { Content = "Copy" }; copy.Click += (_, _) => Menus.Clip(text);
        var save = new Button { Content = "Save as…" }; save.Click += (_, _) => Menus.SaveText(text, "darkcipher-recovery-codes.txt");
        var done = new Button { Content = "I've saved them", Style = (Style)FindResource("PrimaryButton"), IsDefault = true }; done.Click += (_, _) => w.DialogResult = true;
        row.Children.Add(copy); row.Children.Add(save); row.Children.Add(done);
        p.Children.Add(row);
        w.Content = p;
        w.Closing += (_, e) => { if (w.DialogResult != true && !Dialogs.Confirm("Recovery codes", "Close without saving the recovery codes?", "Close")) e.Cancel = true; };
        w.ShowDialog();
    }

    private void NewRecoveryCodes()
    {
        if (!SecureStore.HasTotp) { Dialogs.Info("2FA", "Two-factor sign-in is off."); return; }
        if (Dialogs.Prompt("New recovery codes", "Enter a current authenticator code:") is not { } c || !SecureStore.VerifySecondFactor(c)) { Dialogs.Error("2FA", "Wrong code."); return; }
        // Re-seal the same secret with fresh codes: read nothing back — the user re-enrols only if they want a new secret.
        Dialogs.Info("New recovery codes", "To issue new recovery codes, 2FA is set up again with a new key (your authenticator entry changes). Continue on the next screen.");
        SecureStore.RemoveTotp();
        SetupTotp();
    }

    private void RemoveTotp()
    {
        if (!SecureStore.HasTotp) return;
        if (!ReauthPassword("Turn off 2FA")) return;
        if (Dialogs.Prompt("Turn off 2FA", "Authenticator code (or a recovery code):") is not { } c || !SecureStore.VerifySecondFactor(c)) { Dialogs.Error("2FA", "Wrong code."); return; }
        SecureStore.RemoveTotp();
        AuditLog.Write("you", "app", "two-factor sign-in disabled");
        UpdateTotp();
        if (AppLock.ReconcileSandbox() && _sandbox != null) _sandbox.IsChecked = _s.SandboxMode;
        MainWindow.Instance?.RefreshLockText();
    }

    private void UpdateStore()
    {
        UpdateTotp();
        UpdateHello();
        if (_helloSwitch != null) _helloSwitch.IsChecked = SecureStore.HasHello;
        _storeStatus.Text = SecureStore.HasHello
            ? "🔒 Key protected by Windows Hello and your master password — one of them is needed every time the app starts."
            : SecureStore.PasswordRequiredOnStart
            ? "🔒 Key protected by your master password only — asked every time the app starts."
            : SecureStore.HasPassword
                ? "🔒 Key protected by your Windows account (DPAPI) and a master password (used for recovery on another PC/account)."
                : "🔒 Key protected by your Windows account (DPAPI). Anyone signed in as you can open the app.";
        if (AppLock.ReconcileSandbox() && _sandbox != null) _sandbox.IsChecked = _s.SandboxMode;
        MainWindow.Instance?.RefreshLockText();
    }

    /// <summary>Sets or changes the master password (shared with the first-run setup, 0.3.0 §19).</summary>
    private void SetPassword() { if (SecuritySetupUi.SetMasterPassword(Window.GetWindow(this))) UpdateStore(); }

    private void RemovePassword()
    {
        if (!SecureStore.HasPassword) return;
        if (Dialogs.Prompt("Remove master password", "Current master password:", password: true) is not { } cur || !SecureStore.VerifyPassword(cur)) { Dialogs.Error("Master password", "Wrong password."); return; }
        SecureStore.RemovePassword();
        AuditLog.Write("you", "store", "master password removed");
        UpdateStore();
    }

    /// <summary>One file with everything (or chosen parts), encrypted with its own password — restorable on another PC.</summary>
    private void Backup() => BackupDialogs.ExportFull(Window.GetWindow(this));

    /// <summary>Merge or replace from a .dcxbackup (full backup or an exported folder).</summary>
    private void Restore() => BackupDialogs.Import(Window.GetWindow(this));
}
