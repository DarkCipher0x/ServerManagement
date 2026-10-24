using System.Windows;
using System.Windows.Controls;
using DcxManager.Services;
using DcxManager.Ssh;
using Microsoft.Win32;

namespace DcxManager.Views;

/// <summary>
/// The SSH key tool (0.3.0): create a key (Ed25519, RSA 3072/4096, ECDSA P-256/384/521) straight into this PC's vault or
/// a shared library collection you can edit, then copy the public key, install it on saved hosts, or export it to a file
/// (optionally with a passphrase, <see cref="KeyExport"/>). Opened from Credentials (＋ New SSH key) and the command palette.
/// </summary>
public sealed class SshKeyWindow
{
    /// <summary>The window (built in the house dialog style; shown by <see cref="Show"/>).</summary>
    public Window Window { get; }
    private readonly TextBox _name = new();
    private readonly ComboBox _type = new();
    private readonly TextBox _user = new();
    private readonly ComboBox _where = new();
    private readonly CheckBox _default = new() { Content = "Use it for quick connect", Margin = new Thickness(0, 10, 0, 0) };
    private readonly StackPanel _form = new();
    private readonly StackPanel _done = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button _create;

    /// <summary>The key made (null until created). Self-test: <see cref="CreateAsync"/> without showing the window.</summary>
    public Credential? Created { get; private set; }

    private sealed record Where(string Label, SharedLibraryUi.Target? Target);

    public SshKeyWindow(SharedLibraryUi.Target? into = null)
    {
        Window = Dialogs.MakeWindow("New SSH key", 560);

        TextBlock L(string t) => new() { Text = t, Style = (Style)Application.Current.Resources["Label"], Margin = new Thickness(0, 10, 0, 4) };
        _form.Children.Add(new TextBlock { Text = "A new key pair is made on this PC and stored encrypted. The private key never leaves the vault unless you export it.", TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["Hint"] });
        _form.Children.Add(L("NAME")); _form.Children.Add(_name);
        _form.Children.Add(L("TYPE")); _form.Children.Add(_type);
        foreach (var (id, label) in KeyTools.Types) _type.Items.Add(new ComboBoxItem { Content = label, Tag = id });
        _type.SelectedIndex = 0;
        _form.Children.Add(L("USERNAME ON THE SERVERS (optional)")); _form.Children.Add(_user);
        _form.Children.Add(L("SAVE IN"));
        _form.Children.Add(_where);
        _where.Items.Add(new ComboBoxItem { Content = "This PC's vault", Tag = new Where("This PC's vault", null) });
        foreach (var t in SharedLibraryUi.Writable()) _where.Items.Add(new ComboBoxItem { Content = "⇄ Shared library: " + t.Label, Tag = new Where(t.Label, t) });
        _where.SelectedIndex = into == null ? 0 : Math.Max(0, _where.Items.Cast<ComboBoxItem>().ToList().FindIndex(i => ((Where)i.Tag).Target?.Cid == into.Cid && ((Where)i.Tag).Target?.Client == into.Client));
        _where.SelectionChanged += (_, _) => _default.IsEnabled = ((Where)((ComboBoxItem)_where.SelectedItem).Tag).Target == null;
        _form.Children.Add(_default);
        _name.Text = $"{AppIdentity.Slug()}-{DateTime.Now:yyyyMMdd}";   // 0.5.7: the profile's name, not the Windows account

        _create = new Button { Content = "Create key", Style = (Style)Application.Current.Resources["PrimaryButton"], IsDefault = true };
        _create.Click += async (_, _) => await CreateAsync();
        var bs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        bs.Children.Add(_create); bs.Children.Add(new Button { Content = "Close", IsCancel = true });

        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(_form); root.Children.Add(_done); root.Children.Add(_status); root.Children.Add(bs);
        Window.Content = root;
        Window.Loaded += (_, _) => _name.Focus();
    }

    /// <summary>Opens the tool (modal) over <paramref name="owner"/>; the key made, if any.</summary>
    public static Credential? Show(Window? owner, SharedLibraryUi.Target? into = null)
    {
        var t = new SshKeyWindow(into);
        if (owner != null) t.Window.Owner = owner;
        t.Window.ShowDialog();
        return t.Created;
    }

    /// <summary>Self-test: fills the form without showing anything.</summary>
    internal void Fill(string name, string type, string user, bool asDefault)
    {
        _name.Text = name; _user.Text = user; _default.IsChecked = asDefault;
        _type.SelectedItem = _type.Items.Cast<ComboBoxItem>().First(i => (string)i.Tag == type);
    }

    internal string StatusText => _status.Text;

    /// <summary>Makes the key and saves it where chosen; then shows what can be done with it.</summary>
    public async Task<bool> CreateAsync()
    {
        var name = _name.Text.Trim();
        if (name.Length == 0) { _status.Text = "Give the key a name."; return false; }
        var type = (string)((ComboBoxItem)_type.SelectedItem).Tag;
        var where = (Where)((ComboBoxItem)_where.SelectedItem).Tag;
        _create.IsEnabled = false;
        _status.Text = "Generating…";
        try
        {
            var c = await KeyTools.GenerateAsync(name, type, _user.Text.Trim());
            if (where.Target is { } t)
            {
                // Into a shared collection: the library engine signs and encrypts it for that collection's members.
                var x = c.Clone(); x.Id = ""; x.SharedFrom = null;
                var item = t.Client.SaveCredential(t.Cid, null, x);
                Library.SharedLibraries.Rebuild();
                AuditLog.Write("you", "vault", $"created SSH key {c.Name} ({c.KeyType}) in shared library {t.Label}", c.Fingerprint);
                c = Library.SharedLibraries.Credentials.FirstOrDefault(k => k.Fingerprint == c.Fingerprint) ?? c;
                _ = item;
            }
            else
            {
                AppData.Current.Credentials.Add(c);
                if (_default.IsChecked == true) { AppSettings.Current.DefaultKeyCredentialId = c.Id; AppSettings.Save(); }
                AppData.Save();
                AuditLog.Write("you", "vault", $"created SSH key {c.Name} ({c.KeyType})", c.Fingerprint);
            }
            Created = c;
            ShowDone(c, where.Label);
            return true;
        }
        catch (Exception ex) { _status.Text = "✕ " + ex.Message; _create.IsEnabled = true; return false; }
    }

    private void ShowDone(Credential c, string where)
    {
        _form.Visibility = Visibility.Collapsed;
        _create.Visibility = Visibility.Collapsed;
        _status.Text = $"✓ Created in {where}: {c.KeyType} {c.Fingerprint}";
        _done.Visibility = Visibility.Visible;
        _done.Children.Add(new TextBlock { Text = "PUBLIC KEY", Style = (Style)Application.Current.Resources["Label"], Margin = new Thickness(0, 0, 0, 4) });
        var pub = new TextBox { Text = c.PublicKey, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 90 };
        pub.Mono();
        _done.Children.Add(pub);
        var acts = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        Button A(string t, string tip, Action a) { var b = new Button { Content = t, ToolTip = tip, Margin = new Thickness(0, 0, 6, 4) }; b.Click += (_, _) => a(); acts.Children.Add(b); return b; }
        A("⧉ Copy public key", "For authorized_keys, a cloud console, Git…", () => { Clipboard.SetText(c.PublicKey); _status.Text = "Public key copied."; });
        A("⇪ Install on hosts…", "Appends it to ~/.ssh/authorized_keys on the hosts you tick (like ssh-copy-id)", () => _ = InstallOnHostsAsync(Window, c, s => _status.Text = s));
        A("⇩ Export private key…", "Writes it to a file, optionally protected by a passphrase", () => ExportDialog(Window, c));
        _done.Children.Add(acts);
    }

    /// <summary>Picks saved hosts (tick boxes) and installs the public key on each, one after another, with a result line each.</summary>
    public static async Task InstallOnHostsAsync(Window? owner, Credential key, Action<string> report)
    {
        var hosts = AppData.Current.Hosts.Where(h => h.IsSsh).OrderBy(h => h.Group).ThenBy(h => h.Display).ToList();
        if (hosts.Count == 0) { report("No saved SSH hosts."); return; }
        var w = Dialogs.MakeWindow("Install the public key", 520, 520);
        w.Owner = owner;
        var list = new ListBox();
        foreach (var h in hosts) list.Items.Add(new CheckBox { Content = $"{h.Group} / {h.Display}  ·  {h.Username}@{h.Address}", Tag = h });
        var ok = new Button { Content = "Install", Style = (Style)Application.Current.Resources["PrimaryButton"], IsDefault = true };
        ok.Click += (_, _) => w.DialogResult = true;
        var bs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        bs.Children.Add(ok); bs.Children.Add(new Button { Content = "Cancel", IsCancel = true });
        var root = new DockPanel { Margin = new Thickness(14) };
        var hint = new TextBlock { Text = $"“{key.Name}” is appended to ~/.ssh/authorized_keys of each ticked host's login user (not duplicated). The hosts are reached with their saved logins.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(hint, Dock.Top); root.Children.Add(hint);
        DockPanel.SetDock(bs, Dock.Bottom); root.Children.Add(bs);
        root.Children.Add(list);
        w.Content = root;
        if (w.ShowDialog() != true) return;
        var picked = list.Items.OfType<CheckBox>().Where(x => x.IsChecked == true).Select(x => (HostEntry)x.Tag).ToList();
        if (picked.Count == 0) return;
        if (picked.Any(h => Envs.IsProduction(Envs.For(h))) && !Dialogs.Confirm("Install the public key", $"{picked.Count(h => Envs.IsProduction(Envs.For(h)))} of these are PRODUCTION hosts. Install the key there too?", "Install", danger: true)) return;
        var lines = new List<string>();
        foreach (var h in picked)
        {
            report($"Installing on {h.Display}…");
            try
            {
                var r = await KeyTools.InstallPublicKeyAsync(h, key);
                lines.Add((r.Output.Contains("installed") ? "✓ " : "✕ ") + h.Display + (r.Output.Contains("installed") ? "" : ": " + (r.Error.Length > 0 ? r.Error : r.Output).Trim()));
            }
            catch (Exception ex) { lines.Add($"✕ {h.Display}: {ex.Message}"); }
        }
        AuditLog.Write("you", "vault", $"installed public key {key.Name} on {lines.Count(l => l.StartsWith('✓'))} host(s)", string.Join(", ", picked.Select(h => h.Display)));
        report(string.Join("\n", lines));
    }

    /// <summary>Export with an optional passphrase (twice), then the .pub next to it. Used here and by Credentials.</summary>
    public static void ExportDialog(Window? owner, Credential c)
    {
        if (!SensitiveAction.Confirm($"export the private key “{c.Name}”", owner)) return;
        var w = Dialogs.MakeWindow("Export private key", 500);
        w.Owner = owner;
        var p = new StackPanel { Margin = new Thickness(18) };
        p.Children.Add(new TextBlock { Text = "The private key is written to a file only your Windows account can read. A passphrase protects the file itself (OpenSSH format; ssh, PuTTYgen and this app read it). Without one, anyone who gets the file can use the key.", TextWrapping = TextWrapping.Wrap });
        TextBlock L(string t) => new() { Text = t, Style = (Style)Application.Current.Resources["Label"], Margin = new Thickness(0, 10, 0, 4) };
        var a = new PasswordBox(); var b = new PasswordBox();
        p.Children.Add(L("PASSPHRASE (recommended; empty = none)")); p.Children.Add(a);
        p.Children.Add(L("AGAIN")); p.Children.Add(b);
        var ok = new Button { Content = "Export…", Style = (Style)Application.Current.Resources["PrimaryButton"], IsDefault = true };
        var bs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        bs.Children.Add(ok); bs.Children.Add(new Button { Content = "Cancel", IsCancel = true });
        p.Children.Add(bs);
        w.Content = p;
        ok.Click += (_, _) =>
        {
            if (a.Password != b.Password) { Dialogs.Error("Export private key", "The passphrases don't match."); return; }
            if (a.Password.Length is > 0 and < 10) { Dialogs.Error("Export private key", "Use at least 10 characters (or none)."); return; }
            if (a.Password.Length == 0 && !Dialogs.Confirm("Export private key", "Export WITHOUT a passphrase? Anyone with the file can use the key.", "Export unprotected", danger: true)) return;
            w.DialogResult = true;
        };
        if (w.ShowDialog() != true) return;
        var pass = a.Password;
        var dlg = new SaveFileDialog { FileName = string.Concat(c.Name.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_')), Filter = "All files|*.*" };
        if (dlg.ShowDialog(owner) != true) return;
        if (!StorageGuard.ConfirmDestination(System.IO.Path.GetDirectoryName(dlg.FileName)!, "Export private key", encrypted: pass.Length > 0)) return;
        _ = Run();
        async Task Run()
        {
            try
            {
                await KeyExport.ExportAsync(c, dlg.FileName, pass.Length > 0 ? pass : null);
                System.IO.File.WriteAllText(dlg.FileName + ".pub", c.PublicKey + "\n");
                AuditLog.Write("you", "vault", $"exported private key {c.Name}", pass.Length > 0 ? "passphrase-protected" : "unprotected");
                Ui.Window.Status($"⇩ Exported {c.Name}{(pass.Length > 0 ? " (passphrase-protected)" : "")} and its .pub");
            }
            catch (Exception ex) { Dialogs.Error("Export private key", ex.Message); }
        }
    }
}
