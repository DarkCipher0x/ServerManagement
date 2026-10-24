using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using DcxManager.Services;

namespace DcxManager.Views;

/// <summary>
/// The profile picker at start (0.3.0 §23): which profile to open, a new local profile, or a profile that signs in with
/// your organisation. Shown only when there is more than one profile (or none may open without a directory sign-in,
/// by policy). The choice is made before any store opens; "Open" restarts nothing — the chosen store is the one opened.
/// </summary>
public sealed class ProfilePickerWindow : Window
{
    private readonly ListBox _list = new() { Height = 220 };
    private readonly CheckBox _auto = new() { Content = "Open this profile next time without asking (Settings → Profiles changes it)", Margin = new Thickness(0, 10, 0, 0) };
    public ProfileInfo? Chosen { get; private set; }

    public ProfilePickerWindow(ProfileIndex ix)
    {
        Title = "Choose a profile — " + App.ProductName;
        SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        Typography.Size(this, 520);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        this.Fs(FontStep.Control);
        SetResourceReference(BackgroundProperty, "Bg"); SetResourceReference(ForegroundProperty, "Fg");
        try { Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/app.ico")); } catch { }
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        var root = new StackPanel { Margin = new Thickness(26, 22, 26, 22) };
        root.Children.Add(new TextBlock { Text = "Profiles", Style = (Style)FindResource("H1") });
        root.Children.Add(new TextBlock { Text = "Each profile is its own encrypted store: hosts, vault, settings, notes and team membership are separate. A profile bound to your organisation opens only after that sign-in.", Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 12) });
        bool localOk = Profiles.LocalProfilesAllowed();
        foreach (var p in ix.Profiles.OrderByDescending(p => p.LastUsed ?? p.Created))
            _list.Items.Add(new ListBoxItem { Content = $"{(p.Kind == "directory" ? "🏢" : "👤")}  {p.Name}   ·   {p.KindText}{(p.LastUsed is { } lu ? $"   ·   last used {lu:yyyy-MM-dd}" : "")}" + (!localOk && p.Kind == "local" ? "   ·   ⛔ local profiles are not allowed by policy" : ""), Tag = p, Padding = new Thickness(6, 4, 6, 4) });
        if (_list.Items.Count > 0) _list.SelectedIndex = 0;
        root.Children.Add(_list);
        root.Children.Add(_auto);
        if (!localOk) root.Children.Add(new TextBlock { Text = "Your organisation requires a directory sign-in: only profiles that sign in with it can open.", Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        var bar = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        Button B(string t, Action a, string? style = null) { var b = new Button { Content = t, Margin = new Thickness(0, 0, 6, 4) }; if (style != null) b.Style = (Style)FindResource(style); b.Click += (_, _) => a(); bar.Children.Add(b); return b; }
        var open = B("Open", Open, "PrimaryButton"); open.IsDefault = true;
        if (localOk) B("＋ New local profile…", () =>
        {
            if (Dialogs.Prompt("New profile", "Name for the new profile:") is not { Length: > 0 } name) return;
            try { Chosen = Profiles.Create(name.Trim()); DialogResult = true; } catch (Exception ex) when (ex is ArgumentException or System.IO.IOException) { Dialogs.Error("New profile", ex.Message, explain: false); }
        });
        B("🏢 Sign in with your organisation…", () =>
        {
            if (DirectoryProfileDialog.Ask(this) is not { } b) return;
            if (Dialogs.Prompt("New profile", "Name for the profile:", b.User.Length > 0 ? b.User : "Work") is not { Length: > 0 } name) return;
            try { Chosen = Profiles.Create(name.Trim(), b); DialogResult = true; } catch (Exception ex) when (ex is ArgumentException or System.IO.IOException) { Dialogs.Error("New profile", ex.Message, explain: false); }
        });
        B("Delete…", () =>
        {
            if (Sel is not { } p) return;
            if (p.Id == Profiles.DefaultId) { Dialogs.Info("Delete profile", "The first profile can't be deleted here; remove the app's data from Windows Settings instead."); return; }
            if (!Dialogs.Confirm("Delete profile", $"Delete the profile “{p.Name}” and its encrypted store? This can't be undone.", "Delete", danger: true, typeToConfirm: p.Name)) return;
            try { Profiles.Delete(p.Id); _list.Items.Remove(_list.SelectedItem); } catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException) { Dialogs.Error("Delete profile", ex.Message, explain: false); }
        }, "DangerButton");
        B("Quit", () => { DialogResult = false; });
        root.Children.Add(bar);
        Content = root;
        ThemeManager.TrackScale(this, root);
        _list.MouseDoubleClick += (_, _) => Open();
    }

    private ProfileInfo? Sel => (_list.SelectedItem as ListBoxItem)?.Tag as ProfileInfo;

    private void Open()
    {
        if (Sel is not { } p) return;
        if (p.Kind == "local" && !Profiles.LocalProfilesAllowed()) { Dialogs.Info("Profiles", "Local profiles are not allowed on this PC (Group Policy). Use a profile that signs in with your organisation."); return; }
        Chosen = p;
        if (_auto.IsChecked == true) { var ix = Profiles.LoadWithDefault(); ix.OpenLastWithoutAsking = true; try { Profiles.Save(ix); } catch (System.IO.IOException) { } }
        DialogResult = true;
    }
}
