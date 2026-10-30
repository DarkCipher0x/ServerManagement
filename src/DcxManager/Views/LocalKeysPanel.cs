using System.Windows;
using System.Windows.Controls;
using DcxManager.Services;
using DcxManager.Ssh;

namespace DcxManager.Views;

/// <summary>
/// The SSH keys found in ~/.ssh, each with a tick box, and Import (0.6.30, ADR-107): Quick Start's "Your SSH keys" page and
/// Credentials → ⇩ From ~/.ssh. Default identities are ticked; one already in the store, or one that can't be used, says
/// so and can't be ticked. A key with a passphrase is asked for it when it's imported.
/// </summary>
internal sealed class LocalKeysPanel : StackPanel
{
    private readonly bool _autoOption;
    private readonly List<(LocalKeys.Found Key, CheckBox Box)> _rows = new();
    private CheckBox? _default;
    private readonly TextBlock _result = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };

    /// <summary>Self-test: answers the passphrase questions instead of a dialog.</summary>
    internal Func<LocalKeys.Found, bool, string?>? PassphraseForTest { get; set; }
    internal IReadOnlyList<LocalKeys.Found> FoundForTest => _rows.Select(r => r.Key).ToList();
    internal IReadOnlyList<bool> TickedForTest => _rows.Select(r => r.Box.IsChecked == true).ToList();
    internal string ResultForTest => _result.Text;

    public LocalKeysPanel(bool autoOption)
    {
        _autoOption = autoOption;
        Build();
    }

    private void Build()
    {
        Children.Clear();
        _rows.Clear();
        var found = LocalKeys.Scan(AppData.Current.Credentials);
        if (found.Count == 0)
        {
            Children.Add(Hint("No SSH keys were found in your .ssh folder (%USERPROFILE%\\.ssh). Credentials → 🔑 New SSH key… makes one that lives only in the encrypted store."));
        }
        else
        {
            foreach (var f in found)
            {
                bool usable = f.State is LocalKeys.State.Ready or LocalKeys.State.NeedsPassphrase;
                var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
                text.Inlines.Add(new System.Windows.Documents.Run(f.Label) { FontWeight = f.IsDefault ? FontWeights.SemiBold : FontWeights.Normal });
                var detail = new System.Windows.Documents.Run("\n" + Detail(f));
                detail.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Muted");
                text.Inlines.Add(detail);
                var box = new CheckBox { Content = text, IsChecked = usable && f.IsDefault, IsEnabled = usable, Margin = new Thickness(0, 6, 0, 0) };
                box.Checked += (_, _) => Refresh();
                box.Unchecked += (_, _) => Refresh();
                _rows.Add((f, box));
                Children.Add(box);
            }
            var current = AppData.Current.FindCredential(AppSettings.Current.DefaultKeyCredentialId) is { Kind: CredentialKind.PrivateKey } k ? k : null;
            _default = new CheckBox
            {
                Content = new TextBlock { Text = current == null ? "Use the first default key for quick connect (user@host when no saved host matches)" : $"Quick connect already uses “{current.Name}” (★ in Credentials changes it)", TextWrapping = TextWrapping.Wrap },
                IsChecked = current == null && found.Any(f => f.IsDefault && f.State != LocalKeys.State.Unsupported),
                IsEnabled = current == null,
                Margin = new Thickness(0, 12, 0, 0),
            };
            Children.Add(_default);
            var import = new Button { Content = "⇩ Import the ticked keys", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0), Style = (Style)FindResource("PrimaryButton") };
            import.Click += async (_, _) => await ImportAsync();
            Children.Add(import);
            Refresh();
        }
        if (_autoOption) Children.Add(AutoOption());
        Children.Add(_result);
        Children.Add(Hint("Copies go into the encrypted store; the key files are only read, never changed or removed. A key with a passphrase keeps it, encrypted with the key."));
    }

    private static string Detail(LocalKeys.Found f) => f.State switch
    {
        LocalKeys.State.Ready => f.Fingerprint,
        LocalKeys.State.NeedsPassphrase => "🔑 protected by a passphrase: asked when you import it" + (f.Fingerprint.Length > 0 ? " · " + f.Fingerprint : ""),
        LocalKeys.State.AlreadyStored => "✓ already in the store" + (f.Fingerprint.Length > 0 ? " · " + f.Fingerprint : ""),
        _ => "✕ " + f.Why,
    };

    private CheckBox AutoOption()
    {
        var s = AppSettings.Current;
        bool managed = PolicySettings.Get(nameof(AppSettings.LocalSshKeysAutoImport)) != null;
        var c = new CheckBox
        {
            Content = new TextBlock { Text = (managed ? "🔒 Set by your organisation: " : "") + "Also pick up new keys from this folder each time the app starts (keys without a passphrase are copied in; Settings → Terminal & connections)", TextWrapping = TextWrapping.Wrap },
            IsChecked = s.LocalSshKeysAutoImport, IsEnabled = !managed, Margin = new Thickness(0, 12, 0, 0),
        };
        c.Checked += (_, _) => Set(true);
        c.Unchecked += (_, _) => Set(false);
        return c;

        static void Set(bool on)
        {
            AppSettings.Current.LocalSshKeysAutoImport = on;
            try { AppSettings.Save(); } catch (Exception ex) { App.Log("SSH keys: pick up at start", ex); }
        }
    }

    private void Refresh()
    {
        foreach (var child in Children.OfType<Button>()) child.IsEnabled = _rows.Any(r => r.Box.IsChecked == true);
    }

    private TextBlock Hint(string t) => new() { Text = t, Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };

    /// <summary>Import pressed: the same checks as ＋ Add (asking to unlock Credentials when it's locked), then the copy.</summary>
    internal async Task ImportAsync()
    {
        var chosen = _rows.Where(r => r.Box.IsChecked == true).Select(r => r.Key).ToList();
        if (chosen.Count == 0) return;
        if (Profiles.ReadOnly) { _result.Text = "✕ This profile is read-only (viewer role): nothing can be added."; return; }
        if (!Restrictions.Check("credential.add")) return;
        if (!Locks.IsOpen("cat:credentials") && !await LockGate.EnsureAsync("cat:credentials", "import SSH keys")) return;
        var r = LocalKeyImport.Import(chosen, _default?.IsChecked == true, PassphraseForTest ?? Ask, "imported by you");
        Build();
        var lines = new List<string>();
        if (r.Added.Count > 0) lines.Add($"✓ Imported {r.Added.Count} key(s): {string.Join(", ", r.Added.Select(c => c.Name))}.");
        if (r.Default != null) lines.Add($"Quick connect now uses “{r.Default.Name}”.");
        lines.AddRange(r.Skipped.Select(x => "• " + x));
        _result.Text = string.Join("\n", lines);
        if (r.Added.Count > 0) MainWindow.Instance?.Status($"Imported {r.Added.Count} SSH key(s) from ~/.ssh into the encrypted store.");
    }

    private string? Ask(LocalKeys.Found f, bool again) =>
        Dialogs.Prompt("Key passphrase", (again ? "That passphrase didn't open it. " : "") + $"“{f.FileName}” is protected by a passphrase. Enter it to import the key (kept with the key, encrypted):", password: true);

    /// <summary>Credentials → ⇩ From ~/.ssh.</summary>
    public static void ShowDialog(Window? owner)
    {
        if (TestMode.Running) { Dialogs.RefusedInSelfTest("SSH keys from ~/.ssh"); return; }
        var w = Dialogs.MakeWindow("SSH keys on this PC", 620);
        if (owner != null) w.Owner = owner;
        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new TextBlock { Text = "Keys in your Windows account's .ssh folder. Tick the ones to copy into the encrypted store.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) });
        root.Children.Add(new LocalKeysPanel(autoOption: true));
        var close = new Button { Content = "Close", MinWidth = 90, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0), IsCancel = true };
        close.Click += (_, _) => w.Close();
        root.Children.Add(close);
        w.Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        w.ShowDialog();
    }
}
