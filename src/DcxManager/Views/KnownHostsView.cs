using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using DcxManager.Services;

namespace DcxManager.Views;

/// <summary>Trusted server host keys (trust on first use). Remove one to be asked again.</summary>
public sealed class KnownHostsView : UserControl, IDisposable
{
    private readonly ListView _list = new() { SelectionMode = SelectionMode.Extended };

    public KnownHostsView()
    {
        var root = new DockPanel { Margin = new Thickness(14, 12, 14, 12) };
        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        head.Children.Add(new TextBlock { Text = "Known hosts", Style = (Style)FindResource("H1") });
        head.Children.Add(new TextBlock { Text = "Server keys you have trusted. A changed key raises a warning before connecting. Remove an entry after you reinstall a server.", Style = (Style)FindResource("Hint"), Margin = new Thickness(0, 4, 0, 8) });
        var remove = new Button { Content = "✕ Remove selected", Style = (Style)FindResource("DangerButton"), HorizontalAlignment = HorizontalAlignment.Left };
        remove.Click += (_, _) =>
        {
            var sel = _list.SelectedItems.OfType<KnownHost>().ToList();
            if (sel.Count == 0 || !Dialogs.Confirm("Remove", $"Forget {sel.Count} host key(s)?", "Remove", danger: true)) return;
            foreach (var k in sel) AppData.Current.KnownHosts.Remove(k);
            AppData.Save();
        };
        head.Children.Add(remove);
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);
        var gv = new GridView();
        void Col(string h, string p, double w) => gv.Columns.Add(new GridViewColumn { Header = h, Width = w, DisplayMemberBinding = new Binding(p) });
        Col("Host", "Key", 240); Col("Algorithm", "Algorithm", 180); Col("SHA256 fingerprint", "Fingerprint", 420); Col("Trusted", "FirstSeen", 160);
        _list.View = gv;
        Menus.ForList(_list, this, () => _list.SelectedItem is not KnownHost k ? Array.Empty<object?>() : new object?[]
        {
            Menus.Item("Copy fingerprint", () => Menus.Clip($"SHA256:{k.Fingerprint}")),
            Menus.Item("Copy host", () => Menus.Clip(k.Key)),
            Menus.Item("Compare with the server's key now…", () => Dialogs.ShowText("Verify a host key",
                $"On the server (or its provider console) run:\n\n  ssh-keygen -lf /etc/ssh/ssh_host_{Alg(k.Algorithm)}_key.pub\n\nIt should print  SHA256:{k.Fingerprint}\nIf it differs, the server was reinstalled or something is in the middle — remove this entry only once you know which.")),
            Menus.AssistItem("✦ Review trusted host keys with AI", a => a.AskAsync("known host keys",
                SafeMetadata.KnownHosts(AppData.Current.KnownHosts),
                "Review these trusted SSH host keys: weak or legacy algorithms (ssh-rsa with SHA-1, DSA, small ECDSA), very old entries, duplicates for the same host, and explain how to verify or rotate them safely.")),
        });
        root.Children.Add(_list);
        Content = root;
        AppData.Changed += Reload;
        Reload();
    }

    private void Reload() { _list.ItemsSource = null; _list.ItemsSource = AppData.Current.KnownHosts.OrderBy(k => k.Key).ToList(); }
    public void Dispose() => AppData.Changed -= Reload;

    /// <summary>ssh-ed25519 → ed25519, ecdsa-sha2-nistp256 → ecdsa, rsa-sha2-512/ssh-rsa → rsa (the host key file name).</summary>
    private static string Alg(string a) => a.Contains("ed25519") ? "ed25519" : a.Contains("ecdsa") ? "ecdsa" : a.Contains("rsa") ? "rsa" : a;
}
