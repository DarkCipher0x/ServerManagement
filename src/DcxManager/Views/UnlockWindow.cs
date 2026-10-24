using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DcxManager.Services;

namespace DcxManager.Views;

/// <summary>
/// Sign-in gate. At start-up it opens the store (master password when there's no Windows copy of the key) and asks for
/// the authenticator code when 2FA is on. As the lock screen it re-checks the same factors while sessions keep running.
/// </summary>
public sealed class UnlockWindow : Window
{
    private int _failures;
    private readonly bool _lockScreen;

    /// <param name="needPassword">Open the store with the master password (start-up) / verify it (lock screen).</param>
    /// <param name="needCode">Ask for the 6-digit authenticator code or a recovery code.</param>
    public UnlockWindow(bool needPassword = true, bool needCode = false, bool lockScreen = false)
    {
        _lockScreen = lockScreen;
        Title = lockScreen ? "Locked — " + App.ProductName : App.ProductName;
        SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        Typography.Size(this, 440);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        this.Fs(FontStep.Control);
        Topmost = lockScreen;
        ShowInTaskbar = true;
        SetResourceReference(BackgroundProperty, "Bg");
        SetResourceReference(ForegroundProperty, "Fg");
        try { Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/app.ico")); } catch { }
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);

        var root = new StackPanel { Margin = new Thickness(26, 24, 26, 22) };
        root.Children.Add(new Image { Width = 72, Height = 72, Margin = new Thickness(0, 0, 0, 12), Source = new BitmapImage(new Uri("pack://application:,,,/Assets/logo.png")) });
        root.Children.Add(new TextBlock { Text = lockScreen ? "Locked" : "Sign in", Style = (Style)FindResource("H1"), HorizontalAlignment = HorizontalAlignment.Center });
        root.Children.Add(new TextBlock
        {
            Text = lockScreen ? "The app is locked. Your sessions keep running in the background." :
                "Your hosts, credentials and settings are encrypted (AES-256-GCM)." + (needPassword ? " Enter the master password to open them." : ""),
            Style = (Style)FindResource("Hint"), TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 6, 0, 14), TextWrapping = TextWrapping.Wrap,
        });
        TextBlock Lbl(string t) => new() { Text = t, Style = (Style)FindResource("Label"), Margin = new Thickness(0, 6, 0, 4) };
        var pb = new PasswordBox();
        var code = new TextBox { Padding = new Thickness(8, 4, 8, 4), MaxLength = 16, HorizontalContentAlignment = HorizontalAlignment.Center }.Fs(FontStep.Code).Mono();
        if (needPassword) { root.Children.Add(Lbl("MASTER PASSWORD")); root.Children.Add(pb); }
        if (needCode)
        {
            root.Children.Add(Lbl("AUTHENTICATOR CODE (or a recovery code)"));
            root.Children.Add(code);
        }
        var err = new TextBlock { Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
        err.SetResourceReference(TextBlock.ForegroundProperty, "Bad");
        root.Children.Add(err);
        // Windows Hello (0.3.0 §2): the Hello key's signature opens the store; the master password stays as the fallback.
        bool hello = needPassword && SecureStore.HasHello;
        var helloBtn = new Button { Content = "Sign in with Windows Hello", Style = (Style)FindResource("PrimaryButton"), HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 12, 0, 0) };
        if (hello) root.Children.Insert(root.Children.IndexOf(err), helloBtn);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var ok = new Button { Content = lockScreen ? "Unlock" : "Sign in", Style = (Style)FindResource(hello ? typeof(Button) : "PrimaryButton"), IsDefault = true, MinWidth = 100 };
        var quit = new Button { Content = lockScreen ? "Exit app" : "Quit", IsCancel = !lockScreen };
        buttons.Children.Add(ok); buttons.Children.Add(quit);
        root.Children.Add(buttons);
        Content = root;
        ThemeManager.TrackScale(this, root);

        quit.Click += (_, _) => { if (lockScreen) { QuitRequested = true; DialogResult = false; } };
        ok.Click += async (_, _) =>
        {
            ok.IsEnabled = false;
            err.Text = "Checking…";
            var pw = pb.Password; var c = code.Text;
            bool good = true;
            if (needPassword)
                good = await Task.Run(() => lockScreen ? SecureStore.VerifyPassword(pw) : SecureStore.Unlock(pw));   // PBKDF2 600k: off the UI thread
            if (good && needCode) good = SecureStore.VerifySecondFactor(c);
            if (good)
            {
                AuditLog.Write("you", "app", lockScreen ? "unlocked" : "signed in", needCode ? "password + 2FA" : "password");
                DialogResult = true;
                return;
            }
            _failures++;
            err.Text = needPassword && needCode ? "Wrong password or code." : needCode ? "Wrong or already-used code." : "Wrong password.";
            if (SecureStore.IsUnlocked) AuditLog.Write("you", "app", "failed sign-in attempt", $"#{_failures}");
            await Task.Delay(Math.Min(15000, 500 * _failures * _failures));   // slow down guessing
            ok.IsEnabled = true;
            code.Clear();
            if (needPassword) { pb.SelectAll(); pb.Focus(); } else code.Focus();
        };
        async Task HelloAsync()
        {
            if (needCode && code.Text.Trim().Length == 0) { err.Text = "Enter the authenticator code first, then use Windows Hello."; code.Focus(); return; }
            helloBtn.IsEnabled = ok.IsEnabled = false;
            err.Text = "Waiting for Windows Hello…";
            var sig = await WindowsHello.SignAsync(SecureStore.HelloKeyName!, SecureStore.HelloChallenge!);
            bool good = sig != null && (lockScreen ? SecureStore.VerifyHello(sig) : SecureStore.UnlockWithHello(sig));
            if (good && needCode) good = SecureStore.VerifySecondFactor(code.Text);
            helloBtn.IsEnabled = ok.IsEnabled = true;
            if (good)
            {
                AuditLog.Write("you", "app", lockScreen ? "unlocked" : "signed in", needCode ? "Windows Hello + 2FA" : "Windows Hello");
                DialogResult = true;
                return;
            }
            err.Text = sig == null ? "Windows Hello didn't verify (cancelled or not available). Use the master password instead."
                : needCode ? "Wrong or already-used code." : "Windows Hello signed, but its key doesn't open this store. Use the master password.";
            if (sig != null && SecureStore.IsUnlocked) AuditLog.Write("you", "app", "failed sign-in attempt", "Windows Hello");
            pb.Focus();
        }
        helloBtn.Click += async (_, _) => await HelloAsync();
        Loaded += async (_, _) =>
        {
            if (needPassword) pb.Focus(); else code.Focus();
            if (hello && !needCode) await HelloAsync();   // straight to Hello; the password is there if it is cancelled
        };
        Closing += (_, e) => { if (_lockScreen && DialogResult == null && !QuitRequested) e.Cancel = true; };   // the lock screen can't just be closed
    }

    public bool QuitRequested { get; private set; }
}
