using System.Windows;
using System.Windows.Threading;

namespace DcxManager.Views;

/// <summary>
/// Keeps a screen in step with the data it shows (0.5.0, DarkCipher0x: "after you add something it doesn't show until you leave
/// and come back"). Screens are cached: WPF unloads them on every tab switch and loads them again when shown, so an event
/// handler added once in the constructor and removed on Unloaded goes deaf after the first switch. <see cref="While"/>
/// subscribes on every Loaded (refreshing at once, since changes may have happened while hidden), unsubscribes on every
/// Unloaded (no leak, no work for hidden screens), and coalesces bursts of events into one refresh on the UI thread.
/// </summary>
public static class Live
{
    /// <param name="subscribe">Adds the given handler to the events the screen depends on.</param>
    /// <param name="unsubscribe">Removes it again.</param>
    /// <param name="refresh">Redraws the screen; always called on the UI thread.</param>
    public static void While(FrameworkElement element, Action<Action> subscribe, Action<Action> unsubscribe, Action refresh)
    {
        bool queued = false, attached = false;
        void Fire()
        {
            if (queued) return;
            queued = true;
            element.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => { queued = false; if (element.IsLoaded) refresh(); });
        }
        void Attach() { if (attached) return; attached = true; subscribe(Fire); }
        void Detach() { if (!attached) return; attached = false; unsubscribe(Fire); }
        element.Loaded += (_, _) => { Attach(); refresh(); };
        element.Unloaded += (_, _) => Detach();
        if (element.IsLoaded) Attach();
    }
}
