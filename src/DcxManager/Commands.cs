using System.Windows.Input;

namespace DcxManager;

public static class Commands
{
    public static readonly RoutedUICommand CloseTab = new("Close tab", nameof(CloseTab), typeof(Commands));
}
