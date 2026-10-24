using System.Windows;
using System.Windows.Controls;
using DcxManager.Net;
using DcxManager.Services;

namespace DcxManager.Views.Lan;

/// <summary>Small pieces the LAN discovery tabs share: the toolbar look, the scope check, adding hosts, connecting.</summary>
internal static class LanUi
{
    public static WrapPanel Bar(params UIElement[] items)
    {
        var w = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        foreach (var i in items) { if (i is FrameworkElement f) f.Margin = new Thickness(0, 0, 8, 4); if (i is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center; w.Children.Add(i); }
        return w;
    }

    public static TextBlock Label(string t) => new() { Text = t, Style = (Style)Application.Current.Resources["Label"], VerticalAlignment = VerticalAlignment.Center };

    public static Button Button(string t, bool primary = false)
    {
        var b = new Button { Content = t, Margin = new Thickness(0, 0, 6, 4) };
        if (primary) b.Style = (Style)Application.Current.Resources["PrimaryButton"];
        return b;
    }

    /// <summary>Parses targets with the scope guard; a typed public address is confirmed first. Null = refused / cancelled.</summary>
    public static NetTargets.Parsed? Targets(string text, string what)
    {
        NetTargets.Parsed t;
        try { t = NetTargets.Parse(text); }
        catch (ArgumentException ex) { Dialogs.Error(what, ex.Message, explain: false); return null; }
        if (t.Addresses.Count + t.Names.Count == 0) { Dialogs.Error(what, "Enter an address, a range (192.168.1.0/24 or 192.168.1.10-60) or a host name."); return null; }
        if (t.PublicSingles.Count > 0 && !Dialogs.Confirm(what, $"{string.Join(", ", t.PublicSingles)} {(t.PublicSingles.Count == 1 ? "is a public address" : "are public addresses")} on the internet.\n\nOnly test servers you own or are allowed to test.", "Test it", danger: true))
            return null;
        return t;
    }

    public static void AddToHosts(IEnumerable<(string Address, int Port, string Name, string Note)> items)
    {
        int n = 0;
        foreach (var (address, port, name, note) in items)
        {
            if (AppData.Current.Hosts.Any(x => x.Address == address && x.Port == port)) continue;
            AppData.Current.Hosts.Add(new HostEntry
            {
                Name = name.Length > 0 ? name.Split('.')[0] : address, Address = address, Port = port, Group = "LAN",
                Username = AppSettings.Current.DefaultUsername, Auth = AppSettings.Current.DefaultKeyCredentialId != null ? AuthMethod.PrivateKey : AuthMethod.Password,
                CredentialId = AppSettings.Current.DefaultKeyCredentialId, Notes = note,
            });
            n++;
        }
        if (n == 0) { MainWindow.Instance.Status("Nothing new to add (select rows first; existing hosts are skipped)."); return; }
        AppData.Save();
        MainWindow.Instance.Status($"{n} host(s) added to the LAN group — edit them to set the username and key.");
    }

    public static void ConnectSsh(string address, int port) =>
        MainWindow.Instance.OpenTerminal(new HostEntry { Name = address, Address = address, Port = port, Username = AppSettings.Current.DefaultUsername, IsAdHoc = true, Auth = AppSettings.Current.DefaultKeyCredentialId != null ? AuthMethod.PrivateKey : AuthMethod.Password, CredentialId = AppSettings.Current.DefaultKeyCredentialId });
}
