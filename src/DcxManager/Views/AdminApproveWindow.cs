using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using DcxManager.Services;

namespace DcxManager.Views;

/// <summary>
/// The elevated copy's one window for admin approval of an action (0.5.1, ADR-067: unlocking a locked category or item,
/// installing an add-on…): who asks, what for, Approve or Cancel. Nothing is changed by this window; the app that asked
/// goes ahead only when it is approved.
/// </summary>
public sealed class AdminApproveWindow : Window
{
    public bool Approved { get; private set; }

    public AdminApproveWindow(string purpose, string user, string? refusal)
    {
        Title = "Admin approval — " + App.ProductName;
        SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        Typography.Size(this, 520);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        SetResourceReference(BackgroundProperty, "Bg");
        SetResourceReference(ForegroundProperty, "Fg");
        try { Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/app.ico")); } catch { }
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);

        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        root.Children.Add(new TextBlock { Text = refusal == null ? "Approve this?" : "Not approved", Style = (Style)FindResource("PageTitle") });
        root.Children.Add(new TextBlock { Text = refusal ?? purpose, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) });
        if (refusal == null)
            root.Children.Add(new TextBlock { Text = $"Asked by {user}. Approving lets them continue once; nothing else changes.", Style = (Style)FindResource("Caption"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        if (refusal == null)
        {
            var approve = new Button { Content = "Approve", Style = (Style)FindResource("PrimaryButton"), MinWidth = 100 };
            approve.Click += (_, _) => { Approved = true; Close(); };
            buttons.Children.Add(approve);
        }
        var cancel = new Button { Content = refusal == null ? "Cancel" : "Close", IsCancel = true, IsDefault = refusal != null, MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
        cancel.Click += (_, _) => Close();
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        Content = root;
        Loaded += (_, _) => { Activate(); cancel.Focus(); };
    }
}
