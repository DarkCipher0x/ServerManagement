using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DcxManager.Services;
using DcxManager.Ssh;
using Microsoft.Win32;

namespace DcxManager.Views;

/// <summary>Add or edit a vault entry: a password, or a private key (paste, load from file, or generate).</summary>
public sealed class CredentialEditWindow : Window
{
    public Credential? Result { get; private set; }

    private readonly Credential _cred;
    private readonly TextBox _name = new() { Padding = new Thickness(5, 4, 5, 4), Margin = new Thickness(0, 0, 0, 10) };
    private readonly TextBox _user = new() { Padding = new Thickness(5, 4, 5, 4), Margin = new Thickness(0, 0, 0, 10) };
    private readonly RadioButton _isPassword = new() { Content = "Password", GroupName = "k", Margin = new Thickness(0, 0, 16, 0) };
    private readonly RadioButton _isKey = new() { Content = "SSH private key", GroupName = "k" };
    private readonly PasswordBox _password = new() { Margin = new Thickness(0, 0, 0, 10) };
    private readonly TextBox _key = new TextBox() { Height = 150, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(5, 4, 5, 4) }.Fs(FontStep.Caption);
    private readonly PasswordBox _passphrase = new() { Margin = new Thickness(0, 0, 0, 10) };
    /// <summary>0.6.20: the sign-in's authenticator secret, for "⧉ TOTP code" and filling a web page's code field.</summary>
    private readonly PasswordBox _totp = new() { Margin = new Thickness(0, 0, 0, 10), ToolTip = "The secret behind the site's authenticator QR code (Base32), or the otpauth:// link. Optional." };
    private readonly TextBox _notes = new() { Padding = new Thickness(5, 4, 5, 4), Margin = new Thickness(0, 0, 0, 10) };
    private readonly ComboBox _folder = new() { IsEditable = true, Margin = new Thickness(0, 0, 0, 10), ToolTip = "A folder in the vault, nested with / (e.g. Production/Databases); empty = top level" };
    private readonly ComboBox _category = new() { IsEditable = true, Margin = new Thickness(0, 0, 0, 10), ToolTip = "What this credential is for; pick one or type your own" };
    private readonly TextBlock _info = new TextBlock() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) }.Fs(FontStep.Small);
    private readonly StackPanel _pwPanel = new(), _keyPanel = new();

    public CredentialEditWindow(Credential? existing, CredentialKind kind = CredentialKind.PrivateKey, string username = "")
    {
        _cred = existing?.Clone() ?? new Credential { Kind = kind, Username = username };
        Title = existing == null ? "New credential" : "Edit credential";
        SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        Typography.Size(this, 600);
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        this.Fs(FontStep.Control);
        SetResourceReference(BackgroundProperty, "Bg"); SetResourceReference(ForegroundProperty, "Fg");
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        _key.Mono(); _info.Mono();
        _info.SetResourceReference(TextBlock.ForegroundProperty, "Muted");

        TextBlock L(string t) => new() { Text = t, Style = (Style)FindResource("Label") };
        var root = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
        root.Children.Add(new TextBlock { Text = Title, Style = (Style)FindResource("H1"), Margin = new Thickness(0, 0, 0, 4) });
        root.Children.Add(new TextBlock { Text = "Stored only in the encrypted vault (AES-256-GCM). Keys can be pasted, loaded from a file or generated here.", Style = (Style)FindResource("Hint"), Margin = new Thickness(0, 0, 0, 14) });
        root.Children.Add(L("NAME")); root.Children.Add(_name);
        // Where it sits and what it is for (0.4.0): folders like the hosts', a type to filter by.
        foreach (var f in AppData.Current.AllCredentialFolders) _folder.Items.Add(f);
        foreach (var c in Credential.Categories) _category.Items.Add(c);
        var where = new Grid { Margin = new Thickness(0, 0, 0, 0) };
        where.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        where.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        where.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var fp = new StackPanel(); fp.Children.Add(L("FOLDER")); fp.Children.Add(_folder);
        var cp = new StackPanel(); cp.Children.Add(L("TYPE")); cp.Children.Add(_category);
        Grid.SetColumn(fp, 0); Grid.SetColumn(cp, 2);
        where.Children.Add(fp); where.Children.Add(cp);
        root.Children.Add(where);
        root.Children.Add(L("DEFAULT USERNAME (optional)")); root.Children.Add(_user);
        var kinds = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 12) };
        kinds.Children.Add(_isPassword); kinds.Children.Add(_isKey);
        root.Children.Add(kinds);

        _pwPanel.Children.Add(L("PASSWORD")); _pwPanel.Children.Add(_password);
        _pwPanel.Children.Add(L("TOTP SECRET OR otpauth:// LINK (optional)")); _pwPanel.Children.Add(_totp);
        root.Children.Add(_pwPanel);

        var keyButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        Button Small(string t, RoutedEventHandler h) { var b = new Button { Content = t, Style = (Style)FindResource("SmallButton") }; b.Click += h; keyButtons.Children.Add(b); return b; }
        Small("Load file…", OnLoadFile);
        Small("Generate Ed25519", async (_, _) => await Generate("ed25519"));
        Small("Generate RSA 4096", async (_, _) => await Generate("rsa"));
        _keyPanel.Children.Add(L("PRIVATE KEY (OpenSSH, PEM or PuTTY .ppk)"));
        _keyPanel.Children.Add(keyButtons);
        _keyPanel.Children.Add(_key);
        _keyPanel.Children.Add(L("PASSPHRASE (if the key has one)"));
        _keyPanel.Children.Add(_passphrase);
        _keyPanel.Children.Add(_info);
        root.Children.Add(_keyPanel);

        root.Children.Add(L("NOTES")); root.Children.Add(_notes);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        var ok = new Button { Content = "Save", Style = (Style)FindResource("PrimaryButton"), IsDefault = true, MinWidth = 100 };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        // UI scale + text size (ADR-015); capped to the screen, so the body scrolls rather than being cut off.
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Typography.Track(this, root);

        _name.Text = _cred.Name; _user.Text = _cred.Username; _notes.Text = _cred.Notes;
        _folder.Text = _cred.Folder; _category.Text = _cred.Category;
        if (_cred.Kind == CredentialKind.Password) { _password.Password = _cred.Secret; _totp.Password = _cred.Totp; } else { _key.Text = _cred.Secret; _passphrase.Password = _cred.Passphrase; }
        _isPassword.Checked += (_, _) => Toggle(); _isKey.Checked += (_, _) => Toggle();
        (_cred.Kind == CredentialKind.Password ? _isPassword : _isKey).IsChecked = true;
        if (existing != null) { _isPassword.IsEnabled = _isKey.IsEnabled = false; ShowInfo(); }
        _key.LostFocus += (_, _) => ShowInfo();
        _passphrase.LostFocus += (_, _) => ShowInfo();
        ok.Click += OnSave;
        Loaded += (_, _) => _name.Focus();
    }

    private void Toggle()
    {
        bool key = _isKey.IsChecked == true;
        _pwPanel.Visibility = key ? Visibility.Collapsed : Visibility.Visible;
        _keyPanel.Visibility = key ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowInfo()
    {
        if (_key.Text.Trim().Length == 0) { _info.Text = ""; return; }
        var probe = new Credential { Kind = CredentialKind.PrivateKey, Name = _name.Text, Secret = _key.Text, Passphrase = _passphrase.Password };
        try { KeyTools.Describe(probe); _info.Text = $"✓ {probe.KeyType}  {probe.Fingerprint}"; }
        catch (Exception ex) { _info.Text = "✕ " + ex.Message; }
    }

    private void OnLoadFile(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Private key", Filter = "Private keys|*.pem;*.key;*.ppk;id_*;*|All files|*.*", InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh") };
        if (dlg.ShowDialog(this) != true) return;
        _key.Text = File.ReadAllText(dlg.FileName);
        if (_name.Text.Trim().Length == 0) _name.Text = Path.GetFileName(dlg.FileName);
        ShowInfo();
    }

    private async Task Generate(string type)
    {
        if (_key.Text.Trim().Length > 0 && !Dialogs.Confirm("Generate key", "Replace the key in the box with a newly generated one?", "Replace")) return;
        try
        {
            IsEnabled = false;
            var name = _name.Text.Trim().Length > 0 ? _name.Text.Trim() : $"dcx-{type}-{DateTime.Now:yyyyMMdd}";
            var c = await KeyTools.GenerateAsync(name, type, _user.Text.Trim());
            _name.Text = name; _key.Text = c.Secret; _passphrase.Password = "";
            ShowInfo();
        }
        catch (Exception ex) { Dialogs.Error("Generate key", ex.Message); }
        finally { IsEnabled = true; }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _cred.Name = _name.Text.Trim();
        _cred.Username = _user.Text.Trim();
        _cred.Notes = _notes.Text.Trim();
        _cred.Folder = AppData.NormalizeFolder(_folder.Text);
        _cred.Category = _category.Text.Trim().ToLowerInvariant() is { Length: > 0 and <= 40 } cat ? cat : "";
        _cred.Kind = _isKey.IsChecked == true ? CredentialKind.PrivateKey : CredentialKind.Password;
        if (_cred.Name.Length == 0) { Dialogs.Error("Credential", "Give it a name."); return; }
        if (_cred.Kind == CredentialKind.Password)
        {
            _cred.Secret = _password.Password;
            _cred.Passphrase = _cred.PublicKey = _cred.KeyType = _cred.Fingerprint = "";
            var totp = _totp.Password.Trim();
            if (totp.Length > 0 && Totp.Parse(totp) == null) { Dialogs.Error("Credential", "The TOTP secret isn't readable: paste the Base32 secret shown under the site's QR code, or its otpauth://totp/… link."); return; }
            _cred.Totp = totp;
        }
        else
        {
            _cred.Totp = "";
            _cred.Secret = _key.Text.Trim() + "\n";
            _cred.Passphrase = _passphrase.Password;
            try { KeyTools.Describe(_cred); }
            catch (Exception ex) { Dialogs.Error("Credential", "The key can't be read: " + ex.Message); return; }
        }
        Result = _cred;
        DialogResult = true;
    }
}
