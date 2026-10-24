using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using DcxManager.Notify;
using DcxManager.Services;

namespace DcxManager.Views;

/// <summary>Share to Microsoft Teams (0.5.0, #13): pick the destination, see exactly what goes (title, masked text, the
/// image), send. Used by screenshots, terminal selections, incidents and reports.</summary>
public static class TeamsUi
{
    public static bool Available => AppSettings.Current.TeamsEnabled && TeamsShare.Destinations().Count > 0;

    public static async Task ShareAsync(Window? owner, string title, string text, BitmapSource? image)
    {
        if (!AppSettings.Current.TeamsEnabled) { Dialogs.Info("Share to Teams", "Sharing to Microsoft Teams is off. Turn it on and add a destination in Settings → Integrations → Microsoft Teams (it needs admin approval)."); return; }
        var dests = TeamsShare.Destinations();
        if (dests.Count == 0) { Dialogs.Info("Share to Teams", "No Teams destination yet: Settings → Integrations → Microsoft Teams → add a channel, chat or webhook."); return; }
        if (SelfTest.Running) { Dialogs.RefusedInSelfTest("share to Teams"); return; }
        var png = image != null && AppSettings.Current.TeamsAllowImages ? Screenshot.Png(image) : null;
        var w = Dialogs.MakeWindow("Share to Microsoft Teams", 680, 720);
        w.Owner = owner;
        var p = new StackPanel { Margin = new Thickness(18) };
        var pick = new ComboBox { ItemsSource = dests, SelectedIndex = 0 };
        var t = new TextBox { Text = title, Padding = new Thickness(5, 4, 5, 4) };
        var body = new TextBox { Text = Redaction.Mask(text), AcceptsReturn = true, Height = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.Wrap };
        body.Mono();
        TextBlock L(string s) => new() { Text = s, Style = (Style)Application.Current.Resources["Label"], Margin = new Thickness(0, 8, 0, 4) };
        p.Children.Add(new TextBlock { Text = "This is exactly what will be posted. Secrets the app recognises are already masked; edit the text if anything else shouldn't go.", TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["Hint"] });
        p.Children.Add(L("TO")); p.Children.Add(pick);
        p.Children.Add(L("TITLE")); p.Children.Add(t);
        p.Children.Add(L("TEXT")); p.Children.Add(body);
        var imgNote = new TextBlock { TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["Caption"], Margin = new Thickness(0, 6, 0, 0) };
        if (png != null) { p.Children.Add(L("IMAGE")); p.Children.Add(new Image { Source = image, MaxHeight = 220, HorizontalAlignment = HorizontalAlignment.Left }); }
        else if (image != null) imgNote.Text = "Images are switched off by policy: only the text is sent.";
        p.Children.Add(imgNote);
        void Note() { if (png != null) imgNote.Text = pick.SelectedItem is TeamsDestination { Kind: "webhook" } ? "⚠ A webhook can't carry the image (Teams' 28 KB limit): pick a channel or chat to include it." : $"Image: {png.Length / 1024:N0} KB, posted inside the message."; }
        pick.SelectionChanged += (_, _) => Note(); Note();
        var send = new Button { Content = "Send", Style = (Style)Application.Current.Resources["PrimaryButton"], IsDefault = true };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        bar.Children.Add(send); bar.Children.Add(new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(8, 0, 0, 0) });
        p.Children.Add(bar);
        w.Content = new ScrollViewer { Content = p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        send.Click += async (_, _) =>
        {
            if (pick.SelectedItem is not TeamsDestination d) return;
            send.IsEnabled = false; imgNote.Text = "Sending…";
            try
            {
                await TeamsShare.SendAsync(d, t.Text.Trim().Length > 0 ? t.Text.Trim() : "Shared from ServerManagement", body.Text, d.Kind == "webhook" ? null : png);
                AuditLog.Write("you", "teams", "shared to Microsoft Teams", $"{d.Name} ({d.KindTitle}){(png != null && d.Kind != "webhook" ? ", with an image" : "")}");
                MainWindow.Instance?.Status($"Shared to Teams: {d.Name}.");
                w.DialogResult = true;
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.Net.Http.HttpRequestException or OfflineBlockedException)
            { imgNote.Text = "✕ " + ex.Message; send.IsEnabled = true; }
        };
        w.ShowDialog();
        await Task.CompletedTask;
    }
}
