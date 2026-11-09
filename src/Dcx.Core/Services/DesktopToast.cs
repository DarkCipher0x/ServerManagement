namespace DcxManager.Services;

/// <summary>
/// A Windows notification from code below the app (ADR-118: the stream manager's "Clip created" while a game has the
/// screen). The app sets <see cref="Show"/> to its notification-area balloon; nothing leaves the PC. In the self-test the
/// app records the notification instead of showing it.
/// </summary>
public static class DesktopToast
{
    /// <summary>Shows a notification with a title and text (set by the app; does nothing until then).</summary>
    public static Action<string, string> Show { get; set; } = (_, _) => { };
}
