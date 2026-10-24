using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using DcxManager.Services;

namespace DcxManager.Views;

/// <summary>
/// The elevated copy's only window (0.3.0 §1, ADR-039): what a protected-settings request would change, for which Windows
/// user, and why, with Apply / Cancel. UAC only says "DarkCipher ServerManagement wants to make changes"; this is where the
/// administrator sees the changes themselves. Nothing is written unless Apply is pressed.
/// </summary>
public sealed class AdminChangeWindow : Window
{
    public bool Approved { get; private set; }

    public AdminChangeWindow(ProtectedSettings.Plan? plan, string? refusal)
    {
        Title = "Approve security settings — " + App.ProductName;
        SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        Typography.Size(this, 560);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        SetResourceReference(BackgroundProperty, "Bg");
        SetResourceReference(ForegroundProperty, "Fg");
        try { Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/app.ico")); } catch { }
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);

        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        root.Children.Add(new TextBlock { Text = refusal == null ? "Approve security settings" : "Not changed", Style = (Style)FindResource("PageTitle") });
        if (refusal != null || plan == null)
        {
            root.Children.Add(new TextBlock { Text = refusal ?? "Nothing to approve.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) });
        }
        else
        {
            var r = plan.Request;
            root.Children.Add(new TextBlock
            {
                // 0.5.7: the profile's name first; the Windows account stays (a name is chosen by the user, the account is the fact).
                Text = (AppIdentity.Clean(r.Name) is { Length: > 0 } who ? $"For {who} (Windows account {plan.ForUser})" : $"For Windows user {plan.ForUser}") + $", asked at {r.RequestedUtc.ToLocalTime():HH:mm:ss}" + (r.Reason.Length > 0 ? $" — {r.Reason}" : "") + ".",
                Style = (Style)FindResource("Caption"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 12),
            });
            if (plan.Lines.Count == 0)
                root.Children.Add(new TextBlock { Text = "These values are already in place; applying only records the approval.", TextWrapping = TextWrapping.Wrap });
            foreach (var group in plan.Lines.GroupBy(l => l.Group))
            {
                root.Children.Add(new TextBlock { Text = group.Key.ToUpperInvariant(), Style = (Style)FindResource("Label"), Margin = new Thickness(0, 8, 0, 4) });
                foreach (var l in group)
                {
                    var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                    var change = new TextBlock { Text = $"{l.From}  →  {l.To}", Margin = new Thickness(12, 0, 0, 0) }.Mono();
                    DockPanel.SetDock(change, Dock.Right);
                    row.Children.Add(change);
                    row.Children.Add(new TextBlock { Text = l.Title, TextWrapping = TextWrapping.Wrap });
                    root.Children.Add(row);
                }
            }
            root.Children.Add(new TextBlock
            {
                Text = "Once approved, these settings can only be changed again with admin approval. They are stored in HKLM (administrators can write, everyone can read) and never hold a password or key.",
                Style = (Style)FindResource("Caption"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 0),
            });
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        if (refusal == null && plan != null)
        {
            var apply = new Button { Content = "Apply", Style = (Style)FindResource("PrimaryButton"), MinWidth = 100 };
            apply.Click += (_, _) => { Approved = true; Close(); };
            buttons.Children.Add(apply);
        }
        var cancel = new Button { Content = refusal == null ? "Cancel" : "Close", IsCancel = true, IsDefault = refusal != null, MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
        cancel.Click += (_, _) => Close();
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 700 };
        Loaded += (_, _) => { Activate(); cancel.Focus(); };
    }
}
