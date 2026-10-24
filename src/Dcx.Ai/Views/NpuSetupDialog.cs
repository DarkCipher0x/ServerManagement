using System.Windows;
using System.Windows.Controls;

namespace DcxManager.Views;

/// <summary>The NPU offer's question (Ai/NpuSetupOffer.cs): set up the NPU, with a "Remember my choice" tick box.</summary>
public static class NpuSetupDialog
{
    public static (bool Yes, bool Remember) Ask(string npu)
    {
        var w = Dialogs.MakeWindow("AI Engineer — NPU found", 500);
        var p = new StackPanel { Margin = new Thickness(18) };
        p.Children.Add(new TextBlock
        {
            Text = $"This PC has an NPU ({npu}), but the AI Engineer isn't using it.\n\nSet up the best options for it? Foundry Local runs " +
                   "phi-4-mini on the NPU: low power, no internet needed for answers, nothing leaves this PC. Foundry Local and the model " +
                   "are downloaded from Microsoft the first time (a few GB).",
            TextWrapping = TextWrapping.Wrap,
        });
        var remember = new CheckBox { Content = "Remember my choice", Margin = new Thickness(0, 12, 0, 0) };
        p.Children.Add(remember);
        p.Children.Add(new TextBlock { Text = "Change it later in Settings → AI engine.", Style = (Style)Application.Current.Resources["Hint"], Margin = new Thickness(0, 4, 0, 0) });
        var yes = new Button { Content = "Set up the NPU", Style = (Style)Application.Current.Resources["PrimaryButton"], IsDefault = true };
        var bs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        bs.Children.Add(yes); bs.Children.Add(new Button { Content = "Not now", IsCancel = true });
        p.Children.Add(bs);
        w.Content = p;
        yes.Click += (_, _) => w.DialogResult = true;
        var ok = w.ShowDialog() == true;
        return (ok, remember.IsChecked == true);
    }
}